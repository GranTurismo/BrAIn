using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace BrAIn.Browser.Import;

/// <summary>
/// Chromium-cookie decryption on macOS. Chrome-family browsers encrypt
/// cookie values with AES-128-CBC; the key is PBKDF2-HMAC-SHA1 of the
/// "&lt;Browser&gt; Safe Storage" Keychain password (salt "saltysalt",
/// 1003 iterations). Encrypted blobs are "v10" + ciphertext, and the
/// plaintext is prefixed with a 32-byte SHA256 of the host on modern builds.
/// Reading the keychain may show a one-time macOS consent prompt.
/// </summary>
public static class ChromeCrypto
{
    private static readonly Dictionary<string, byte[]?> _keyCache = new();

    /// <summary>Derived 16-byte AES key for the given Safe Storage service,
    /// or null when the keychain denies access (user dismissed the prompt).</summary>
    public static byte[]? SafeStorageKey(string service)
    {
        lock (_keyCache)
        {
            if (_keyCache.TryGetValue(service, out var cached)) return cached;
            byte[]? key = null;
            try
            {
                var psi = new ProcessStartInfo("security")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                psi.ArgumentList.Add("find-generic-password");
                psi.ArgumentList.Add("-s");
                psi.ArgumentList.Add(service);
                psi.ArgumentList.Add("-w");
                using var p = Process.Start(psi)!;
                var pw = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(15000);
                if (p.ExitCode == 0 && pw.Length > 0)
                    key = Rfc2898DeriveBytes.Pbkdf2(
                        Encoding.UTF8.GetBytes(pw), Encoding.UTF8.GetBytes("saltysalt"),
                        1003, HashAlgorithmName.SHA1, 16);
            }
            catch { }
            _keyCache[service] = key;
            return key;
        }
    }

    /// <summary>Decrypt a Chromium encrypted_value blob. Returns null when
    /// the blob isn't a supported scheme or the key is wrong.</summary>
    public static string? Decrypt(byte[] blob, byte[] key, string host)
    {
        var pt = DecryptBytes(blob, key);
        if (pt is null) return null;
        // Modern Chrome prefixes plaintext with SHA256(host) — 32 bytes.
        var sha = SHA256.HashData(Encoding.UTF8.GetBytes(host));
        if (pt.Length > 32 && pt[..32].SequenceEqual(sha)) pt = pt[32..];
        return Encoding.UTF8.GetString(pt);
    }

    /// <summary>Login Data password_value — the metadata-hash prefix can be
    /// keyed on the origin's host or the full origin_url depending on build,
    /// so both strip candidates are tried before accepting the raw bytes.</summary>
    public static string? DecryptPassword(byte[] blob, byte[] key, string originUrl)
    {
        var pt = DecryptBytes(blob, key);
        if (pt is null) return null;
        foreach (var cand in new[] { SafeHost(originUrl), originUrl })
        {
            if (cand.Length == 0) continue;
            var sha = SHA256.HashData(Encoding.UTF8.GetBytes(cand));
            if (pt.Length > 32 && pt[..32].SequenceEqual(sha))
                return Encoding.UTF8.GetString(pt[32..]);
        }
        return Encoding.UTF8.GetString(pt);
    }

    /// <summary>Windows v10 scheme — AES-256-GCM with the Local State
    /// "encrypted_key" (DPAPI-unwrapped). Blob: "v10" + 12-byte nonce + ct +
    /// 16-byte tag. v20 app-bound blobs can't be decrypted outside Chrome —
    /// they return null like every other unsupported scheme.</summary>
    public static string? DecryptPasswordWindows(byte[] blob, byte[] key)
    {
        if (blob.Length < 3 + 12 + 16) return null;
        if (blob[0] != 'v' || blob[1] != '1' || blob[2] != '0') return null;
        try
        {
            var nonce = blob[3..15];
            var ct = blob[15..^16];
            var tag = blob[^16..];
            var pt = new byte[ct.Length];
            using var gcm = new AesGcm(key, 16);
            gcm.Decrypt(nonce, ct, tag, pt);
            return Encoding.UTF8.GetString(pt);
        }
        catch { return null; }
    }

    private static string SafeHost(string url)
    {
        try { return new Uri(url).Host; } catch { return ""; }
    }

    /// <summary>v10 blob → raw plaintext bytes (AES-128-CBC, 16-space IV),
    /// still carrying any metadata-hash prefix.</summary>
    private static byte[]? DecryptBytes(byte[] blob, byte[] key)
    {
        if (blob.Length < 3) return null;
        // v10 = AES-128-CBC, IV = 16 spaces. (v20/ABE is Windows-only.)
        if (blob[0] != 'v' || blob[1] != '1' || blob[2] != '0') return null;
        var ct = blob[3..];
        if (ct.Length == 0 || ct.Length % 16 != 0) return null;
        try
        {
            using var aes = Aes.Create();
            aes.Key = key;
            aes.IV = new byte[16].Select(_ => (byte)0x20).ToArray();
            aes.Mode = CipherMode.CBC;
            return aes.CreateDecryptor().TransformFinalBlock(ct, 0, ct.Length);
        }
        catch { return null; }
    }
}
