using System.Security.Cryptography;
using System.Text.Json;

namespace BrAIn.Browser;

/// <summary>
/// The credential vault. Entries live in <c>passwords.dat</c> under the profile
/// store directory, AES-256-GCM encrypted with a random 256-bit key. The key is
/// protected by the OS credential store — macOS Keychain (generic-password item)
/// or Windows DPAPI — and held in memory for the process lifetime so the OS
/// prompt (if any) happens once per launch, not per lookup. If key protection
/// is unavailable the key falls back to a permission-locked file; the store
/// still encrypts, just without OS binding.
/// </summary>
internal sealed class PasswordStore
{
    /// <summary>One saved login. Origin is scheme://host[:port] — the browser
    /// never stores credentials for a host the tab wasn't actually on.</summary>
    internal sealed record Entry(string Id, string Origin, string User, string Pass, long Created, long Used);

    public enum UpsertResult { Same, Updated, Added }

    private readonly string _path;
    private readonly string _keyPath;
    private readonly object _gate = new();
    private List<Entry> _items = new();
    private bool _loaded;
    private byte[]? _key;

    public PasswordStore(string storeDir)
    {
        _path = Path.Combine(storeDir, "passwords.dat");
        _keyPath = Path.Combine(storeDir, "pwkey.bin");
    }

    // ---------------- key management ----------------

    /// <summary>Reads the store key from the OS keystore, creating and
    /// protecting a fresh one on first launch. Returns null only when every
    /// protection path failed AND no fallback could be written.</summary>
    private byte[]? Key()
    {
        if (_key is not null) return _key;
        byte[]? raw = null;
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                raw = Mac.WkKeychain.Read();
                if (raw is null && File.Exists(_keyPath))
                    raw = File.ReadAllBytes(_keyPath); // earlier fallback-mode key
            }
            else if (OperatingSystem.IsWindows() && File.Exists(_keyPath))
            {
                raw = Native.Win32.DpapiUnprotect(File.ReadAllBytes(_keyPath));
            }
        }
        catch { /* keystore unavailable */ }
        if (raw is { Length: 32 }) return _key = raw;

        // Denied OS keystore while a vault file already exists → a fresh key
        // would orphan the on-disk ciphertext (next launch can't decrypt).
        // Fail safe: no key, no writes — the in-memory list still works for
        // the session and the file stays decryptable under the real key.
        if (File.Exists(_path)) return null;

        var fresh = RandomNumberGenerator.GetBytes(32);
        try
        {
            if (OperatingSystem.IsMacOS() && Mac.WkKeychain.Write(fresh)) return _key = fresh;
            if (OperatingSystem.IsWindows())
            {
                var blob = Native.Win32.DpapiProtect(fresh);
                if (blob is not null) { File.WriteAllBytes(_keyPath, blob); return _key = fresh; }
            }
            // Last resort: the key file is still encryption-at-rest, just not
            // bound to the OS account. Lock it to the owner where possible.
            File.WriteAllBytes(_keyPath, fresh);
            if (!OperatingSystem.IsWindows())
            {
                try { File.SetUnixFileMode(_keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
            }
        }
        catch { }
        return _key = fresh;
    }

    // ---------------- persistence ----------------

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(_path)) return;
            var doc = JsonDocument.Parse(File.ReadAllText(_path)).RootElement;
            var nonce = Convert.FromBase64String(doc.GetProperty("n").GetString()!);
            var data = Convert.FromBase64String(doc.GetProperty("d").GetString()!);
            var key = Key();
            if (key is null || data.Length < 16) return;
            var plain = new byte[data.Length - 16];
            using (var gcm = new AesGcm(key, 16))
                gcm.Decrypt(nonce, data[..^16], data[^16..], plain);
            foreach (var el in JsonDocument.Parse(plain).RootElement.EnumerateArray())
                _items.Add(new Entry(
                    el.GetProperty("i").GetString() ?? "",
                    el.GetProperty("o").GetString() ?? "",
                    el.GetProperty("u").GetString() ?? "",
                    el.GetProperty("p").GetString() ?? "",
                    el.TryGetProperty("c", out var c) ? c.GetInt64() : 0,
                    el.TryGetProperty("m", out var m) ? m.GetInt64() : 0));
        }
        catch { _items = new(); /* corrupt or key rotated — start fresh */ }
    }

    private void Save()
    {
        try
        {
            var key = Key();
            if (key is null) return;
            var plain = JsonSerializer.SerializeToUtf8Bytes(
                _items.Select(e => new { i = e.Id, o = e.Origin, u = e.User, p = e.Pass, c = e.Created, m = e.Used }));
            var nonce = RandomNumberGenerator.GetBytes(12);
            var ct = new byte[plain.Length];
            var tag = new byte[16];
            using (var gcm = new AesGcm(key, 16))
                gcm.Encrypt(nonce, plain, ct, tag);
            var json = JsonSerializer.Serialize(new
            {
                v = 1,
                n = Convert.ToBase64String(nonce),
                d = Convert.ToBase64String(ct.Concat(tag).ToArray()),
            });
            // Write-then-rename so a crash mid-save can't leave a half file.
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch { /* disk full / locked — non-fatal */ }
    }

    // ---------------- queries ----------------

    /// <summary>Origin match is exact — scheme+host+port — so a cred saved on
    /// https://site never fills on http://site or a subdomain lookalike.</summary>
    public IReadOnlyList<Entry> ListFor(string origin)
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _items.Where(e => e.Origin == origin).ToArray();
        }
    }

    /// <summary>Every entry, for the about:passwords manager page.</summary>
    public IReadOnlyList<Entry> All()
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _items.OrderByDescending(e => e.Used).ToArray();
        }
    }

    /// <summary>Add or update. Same user+pass at the origin = no-op (Used
    /// bumps so recently-used sorts first); same user new pass = update in
    /// place so the manager never shows duplicate rows.</summary>
    public UpsertResult Upsert(string origin, string user, string pass)
    {
        if (string.IsNullOrEmpty(pass)) return UpsertResult.Same;
        lock (_gate)
        {
            EnsureLoaded();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var i = _items.FindIndex(e => e.Origin == origin && e.User == user);
            if (i >= 0 && _items[i].Pass == pass)
            {
                _items[i] = _items[i] with { Used = now };
                Save();
                return UpsertResult.Same;
            }
            if (i >= 0) _items[i] = _items[i] with { Pass = pass, Used = now };
            else _items.Add(new Entry(Guid.NewGuid().ToString("N")[..12], origin, user, pass, now, now));
            Save();
            return i >= 0 ? UpsertResult.Updated : UpsertResult.Added;
        }
    }

    /// <summary>Does the store already hold this exact user+pass? Used to
    /// suppress the save prompt when the vault already has the login.</summary>
    public bool HasExact(string origin, string user, string pass)
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _items.Any(e => e.Origin == origin && e.User == user && e.Pass == pass);
        }
    }

    /// <summary>Any credential at all for the origin — even a different
    /// username still means "this site is handled", so an update prompt is
    /// appropriate rather than silence.</summary>
    public bool HasAny(string origin)
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _items.Any(e => e.Origin == origin);
        }
    }

    public bool Delete(string id)
    {
        lock (_gate)
        {
            EnsureLoaded();
            var i = _items.FindIndex(e => e.Id == id);
            if (i < 0) return false;
            _items.RemoveAt(i);
            Save();
            return true;
        }
    }
}
