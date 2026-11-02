using System.Security.Cryptography;
using System.Text;
using BrAIn.Browser.Linux;

namespace BrAIn.Browser.Import;

/// <summary>
/// Chromium-on-Linux value encryption. Same cipher as on macOS — AES-128-CBC, 16-space IV, PKCS#7 (the shared
/// <c>DecryptBytes</c>) — and the same plaintext layout (a SHA-256 of the host in front of newer cookie values,
/// of the host or origin in front of some password values); what differs is the key:
/// <list type="bullet">
/// <item><c>v10</c> values: password "peanuts" (a constant; used when no keyring is configured).</item>
/// <item><c>v11</c> values: the "&lt;Browser&gt; Safe Storage" secret from the freedesktop Secret Service.</item>
/// </list>
/// Both go through PBKDF2-HMAC-SHA1 with salt "saltysalt", ONE iteration (macOS uses 1003) and 16 output bytes.
/// </summary>
public static partial class ChromeCrypto
{
    /// <summary>Why a Linux value could not be turned into text.</summary>
    internal enum LinuxFail { None, NeedsKey, Unsupported, Bad }

    /// <summary>The two AES keys of one browser. The v10 key is derived at once; the v11 key is fetched from the
    /// keyring the first time a v11 value is met, so a profile without any never wakes (or prompts) the keyring.</summary>
    internal sealed class LinuxKeySet
    {
        private readonly string _service;
        private byte[]? _v11;
        private bool _tried;
        private long _triedAt;

        internal LinuxKeySet(string service)
        {
            _service = service;
            V10 = Derive(Encoding.ASCII.GetBytes("peanuts"));
        }

        internal byte[] V10 { get; }

        /// <summary>Set when the keyring could not be read: "locked", "unavailable (...)"; null when it answered
        /// and simply holds no such item (v11 values then fall back to "peanuts", like Chromium's own fallback).</summary>
        internal string? V11Problem { get; private set; }

        internal byte[]? V11
        {
            get
            {
                lock (this)
                {
                    // A failure is remembered for two minutes so the import's cookie and password steps do not
                    // each pop an unlock prompt; success is final.
                    if (_v11 is null && (!_tried || Environment.TickCount64 - _triedAt > 120_000))
                    {
                        _tried = true;
                        _triedAt = Environment.TickCount64;
                        var st = LinuxSecrets.LookupChromiumSafeStorage(_service, ApplicationsFor(_service), out var secret, out var why);
                        if (st == LinuxSecrets.Status.Found && secret is { Length: > 0 })
                        {
                            _v11 = Derive(secret);
                            CryptographicOperations.ZeroMemory(secret);
                            V11Problem = null;
                        }
                        else
                        {
                            V11Problem = st switch
                            {
                                LinuxSecrets.Status.NotFound => null,
                                LinuxSecrets.Status.Locked => "locked",
                                _ => "unavailable" + (string.IsNullOrEmpty(why) ? "" : " (" + why + ")"),
                            };
                            if (V11Problem is not null) Console.Error.WriteLine($"[imp] {_service}: keyring {V11Problem}");
                        }
                    }
                    return _v11;
                }
            }
        }

        private static byte[] Derive(byte[] password)
            => Rfc2898DeriveBytes.Pbkdf2(password, Encoding.ASCII.GetBytes("saltysalt"), 1, HashAlgorithmName.SHA1, 16);
    }

    private static readonly Dictionary<string, LinuxKeySet> _linuxKeys = new();

    /// <summary>The key set for a "&lt;Browser&gt; Safe Storage" service name (cached for the process).</summary>
    internal static LinuxKeySet LinuxKeys(string service)
    {
        lock (_linuxKeys)
        {
            if (!_linuxKeys.TryGetValue(service, out var k)) _linuxKeys[service] = k = new LinuxKeySet(service);
            return k;
        }
    }

    /// <summary>The <c>application</c> attribute values a browser's Safe Storage item is known under (the label is
    /// matched too, so this only has to disambiguate and cover renamed labels).</summary>
    private static string[] ApplicationsFor(string service) => service switch
    {
        "Chrome Safe Storage" => new[] { "chrome", "google-chrome" },
        "Chromium Safe Storage" => new[] { "chromium" },
        "Brave Safe Storage" => new[] { "brave", "brave-browser" },
        "Microsoft Edge Safe Storage" => new[] { "microsoft-edge", "edge" },
        "Vivaldi Safe Storage" => new[] { "vivaldi" },
        "Opera Safe Storage" => new[] { "opera" },
        _ => Array.Empty<string>(),
    };

    /// <summary>A Linux cookie <c>encrypted_value</c> → text, or null with the reason in <paramref name="why"/>.
    /// Newer Chromium puts SHA-256(host) in front of the value (an empty value is the bare hash).</summary>
    internal static string? DecryptLinux(byte[] blob, LinuxKeySet keys, string host, out LinuxFail why)
        => LinuxValue(blob, keys, out why, pt => StripHash(pt, host));

    /// <summary>A Linux Login Data <c>password_value</c> → text, or null with the reason in <paramref name="why"/>
    /// (the optional hash prefix may be keyed on the host or on the whole origin, as on macOS).</summary>
    internal static string? DecryptPasswordLinux(byte[] blob, LinuxKeySet keys, string originUrl, out LinuxFail why)
        => LinuxValue(blob, keys, out why, pt => StripHash(StripHash(pt, SafeHost(originUrl)), originUrl));

    private static byte[] StripHash(byte[] plain, string keyedOn)
    {
        if (keyedOn.Length == 0 || plain.Length < 32) return plain;
        return plain.AsSpan(0, 32).SequenceEqual(SHA256.HashData(Encoding.UTF8.GetBytes(keyedOn))) ? plain[32..] : plain;
    }

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static string? LinuxValue(byte[] blob, LinuxKeySet keys, out LinuxFail why, Func<byte[], byte[]> unwrap)
    {
        why = LinuxFail.Unsupported;
        if (blob.Length < 3 + 16 || blob[0] != 'v' || blob[1] != '1' || (blob[2] != '0' && blob[2] != '1')) return null;
        var v11 = blob[2] == '1';
        var key = v11 ? keys.V11 ?? keys.V10 : keys.V10; // no keyring secret → "peanuts", as Chromium itself would
        // v10 and v11 differ only in the key: hand both to the shared AES-128-CBC routine as "v10".
        var norm = (byte[])blob.Clone();
        norm[2] = (byte)'0';
        string? text = null;
        var plain = DecryptBytes(norm, key);
        if (plain is not null)
        {
            try { text = StrictUtf8.GetString(unwrap(plain)); }
            catch (DecoderFallbackException) { /* a wrong key still unpads about 1 time in 256, but the result is not UTF-8 */ }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        if (text is not null) { why = LinuxFail.None; return text; }
        why = v11 && keys.V11 is null ? LinuxFail.NeedsKey : LinuxFail.Bad;
        return null;
    }
}
