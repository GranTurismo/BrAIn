using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using BrAIn.Browser.Linux;

namespace BrAIn.Browser;

/// <summary>
/// Linux key protection for the password vault: the AES key lives in the freedesktop Secret Service (GNOME
/// Keyring, KWallet, KeePassXC, ...) through <see cref="LinuxSecrets"/>. When no Secret Service is reachable
/// (headless box, no session bus, no keyring daemon) the key lives in a 0600 file next to the vault, exactly like
/// the macOS/Windows last-resort fallback, and moves into the keyring on a later launch once one is available.
///
/// The one rule everything below serves: <b>the vault is unrecoverable without its key, so a key is never lost,
/// replaced or discarded unless a copy has been read back from its new home.</b>
///   · a fresh key is handed out only after it was stored AND read back (keyring) or written AND re-read (file);
///   · the file key is deleted only after the keyring returned identical bytes;
///   · an existing keyring item is never overwritten (a locked keyring is told apart from an empty one);
///   · when a vault exists and no key opens it, no key is returned and nothing is written for the session.
/// </summary>
internal sealed partial class PasswordStore
{
    /// <summary>Set when a vault exists but no key could be obtained. Sticky for the process: <c>EnsureLoaded</c>
    /// has already given up on the file, so a key that appears later (keyring unlocked in the meantime) must not
    /// suddenly let <c>Save</c> overwrite the vault with only the entries added since.</summary>
    private bool _linuxDenied;

    [SupportedOSPlatform("linux")]
    private byte[]? LinuxKey()
    {
        if (_linuxDenied) return null;
        try
        {
            var fileKey = ReadKeyFile();
            var ring = LinuxSecrets.LookupVaultKey(out var ringKey, out var ringWhy);
            var vault = VaultKind();

            byte[]? key = null;
            if (vault == Vault.Sealed)
            {
                // Whichever candidate actually opens the vault wins (the keyring first). They differ only if
                // another BrAIn instance wrote a different key into the keyring, and then the file is the right one.
                if (ringKey is not null && Opens(ringKey)) key = ringKey;
                else if (fileKey is not null && Opens(fileKey)) key = fileKey;
                if (key is null)
                {
                    Console.Error.WriteLine("[passwords] the vault key is not available (" + (ringWhy ?? (ringKey is not null ? "the keyring key does not open it" : "no key found")) + "); the vault stays untouched this session");
                    _linuxDenied = true;
                    return null;
                }
            }
            else key = ringKey ?? fileKey;

            if (key is not null)
            {
                if (ringKey is not null && key == ringKey)
                {
                    // The keyring has it; a leftover file copy (an interrupted migration) is redundant plaintext.
                    if (fileKey is not null && fileKey.AsSpan().SequenceEqual(ringKey)) DeleteKeyFile();
                }
                else if (fileKey is not null && key == fileKey && ring == LinuxSecrets.Status.NotFound)
                {
                    MigrateToKeyring(fileKey);
                }
                return key;
            }

            // No key anywhere and no vault to protect (first launch, or a vault that is not even well formed): mint one.
            // An existing-but-locked keyring item is left alone — the key goes to the file instead.
            var fresh = RandomNumberGenerator.GetBytes(32);
            if (ring == LinuxSecrets.Status.NotFound && StoreAndVerify(fresh)) return fresh;
            if (WriteKeyFile(fresh)) return fresh;
            Console.Error.WriteLine("[passwords] could not store a vault key anywhere; passwords are kept in memory only this session");
            _linuxDenied = true;
            return null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[passwords] vault key error: " + ex.Message);
            _linuxDenied = true;
            return null;
        }
    }

    // ---- keyring ----

    /// <summary>Stores the key and only counts it as stored when the keyring hands back the same 32 bytes.</summary>
    private static bool StoreAndVerify(byte[] key)
    {
        if (!LinuxSecrets.StoreVaultKey(key, out var why))
        {
            Console.Error.WriteLine("[passwords] keyring did not take the vault key (" + why + ")");
            return false;
        }
        var back = LinuxSecrets.LookupVaultKey(out var read, out why);
        var same = back == LinuxSecrets.Status.Found && read is not null && read.AsSpan().SequenceEqual(key);
        if (read is not null) CryptographicOperations.ZeroMemory(read);
        if (!same) Console.Error.WriteLine("[passwords] keyring read-back did not match (" + (why ?? back.ToString()) + ")");
        return same;
    }

    /// <summary>File key → keyring. The file is removed only after a verified read-back; any failure leaves it
    /// in place and the next launch tries again.</summary>
    private void MigrateToKeyring(byte[] key)
    {
        if (StoreAndVerify(key)) DeleteKeyFile();
    }

    // ---- key file (0600, raw 32 bytes — the same file the pre-keyring Linux builds wrote) ----

    private byte[]? ReadKeyFile()
    {
        try
        {
            if (!File.Exists(_keyPath)) return null;
            var b = File.ReadAllBytes(_keyPath);
            if (b.Length == 32) return b;
            // Not ours / damaged: keep it for inspection and carry on as if there were none.
            File.Move(_keyPath, _keyPath + ".invalid-" + DateTimeOffset.UtcNow.ToUnixTimeSeconds(), overwrite: true);
        }
        catch { /* unreadable → treated as absent */ }
        return null;
    }

    [SupportedOSPlatform("linux")]
    private bool WriteKeyFile(byte[] key)
    {
        var tmp = _keyPath + ".tmp";
        try
        {
            try { File.Delete(tmp); } catch { }
            // Created with 0600 from the start (no window where it is world-readable), flushed to disk, then renamed in.
            using (var fs = new FileStream(tmp, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            }))
            {
                fs.Write(key);
                fs.Flush(true);
            }
            File.Move(tmp, _keyPath, overwrite: true);
            var back = File.ReadAllBytes(_keyPath);
            return back.AsSpan().SequenceEqual(key);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[passwords] key file not written: " + ex.Message);
            try { File.Delete(tmp); } catch { }
            return false;
        }
    }

    private void DeleteKeyFile()
    {
        try
        {
            if (!File.Exists(_keyPath)) return;
            var len = (int)new FileInfo(_keyPath).Length;
            try { File.WriteAllBytes(_keyPath, new byte[len]); } catch { } // best effort: don't leave the bytes behind
            File.Delete(_keyPath);
        }
        catch { /* leaving the file is harmless: the keyring copy is verified identical */ }
    }

    // ---- which key opens the vault? ----

    private enum Vault { None, Sealed }

    /// <summary><see cref="Vault.Sealed"/> when passwords.dat exists and has the shape of a vault (so only its key
    /// opens it); anything else — missing, empty, not our JSON — holds nothing to protect.</summary>
    private Vault VaultKind()
    {
        try
        {
            if (!File.Exists(_path)) return Vault.None;
            return TryReadVault(out _, out _) ? Vault.Sealed : Vault.None;
        }
        catch { return Vault.None; }
    }

    private bool TryReadVault(out byte[] nonce, out byte[] data)
    {
        nonce = data = Array.Empty<byte>();
        try
        {
            using var parsed = JsonDocument.Parse(File.ReadAllText(_path));
            var doc = parsed.RootElement;
            nonce = Convert.FromBase64String(doc.GetProperty("n").GetString()!);
            data = Convert.FromBase64String(doc.GetProperty("d").GetString()!);
            return nonce.Length == 12 && data.Length >= 16;
        }
        catch { return false; }
    }

    /// <summary>True when AES-GCM authenticates the vault under this key (the plaintext is discarded at once).</summary>
    private bool Opens(byte[] key)
    {
        if (!TryReadVault(out var nonce, out var data)) return false;
        var plain = new byte[data.Length - 16];
        try
        {
            using var gcm = new AesGcm(key, 16);
            gcm.Decrypt(nonce, data[..^16], data[^16..], plain);
            return true;
        }
        catch { return false; }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
