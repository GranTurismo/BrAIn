using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace BrAIn.Browser;

/// <summary>
/// Who BrAIn trusts to say "this file is a BrAIn release". Not GitHub: an update is downloaded from a GitHub release, but what
/// makes BrAIn install it is a signature made with a private key that is NOT on GitHub (it lives on the maintainer's machine,
/// see <c>scripts/sign-release.sh</c>); only the public half is in this program. Someone who takes over the GitHub account, a
/// token or the release page can publish files, but cannot make a signature these keys accept.
///
/// What is signed is not just the file: <c>BrAIn-update-v1 \n version \n asset name \n sha256 of the file</c>. So a validly signed
/// OLD release cannot be passed off as a new one (the version is in the message) and a signed Mac disk image cannot be offered as
/// the Windows installer (the name is). The signature travels as a second release asset, <c>&lt;asset&gt;.sig</c>: base64 of an
/// ECDSA P-256 / SHA-256 signature in DER form (what <c>openssl dgst -sha256 -sign</c> writes).
///
/// Anything missing or odd — no digest, no signature, an unknown key, a download address that is not this repository's release
/// folder — means "do not install", never "install anyway".
/// </summary>
internal static partial class UpdateTrust
{
    /// <summary>The release-signing public keys (P-256, SubjectPublicKeyInfo, base64). More than one is allowed so a key can be
    /// retired: ship a release signed with the old key that adds the new one, then drop the old one a release later.</summary>
    private static readonly string[] Keys =
    {
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEnsxQymup5kf0AHV8kCW0U6vfpuWHabSeLsKT3xoZznLM7r8H4u8tCDjc9LQ7nWMb5fB+62K0eg/iyNpBbfvDPQ==",
    };

    /// <summary>For the updater's end-to-end tests, which sign a fake release with a throw-away key: this variable ADDS one more
    /// trusted public key (same format as <see cref="Keys"/>). It cannot remove or replace the real one.</summary>
    internal const string TestKeyVariable = "BRAIN_UPDATE_TEST_KEY";

    /// <summary>File names that may reach an installer script: no quotes, spaces, separators or shell characters.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._+-]{0,119}$")]
    private static partial Regex SafeName();

    internal static bool IsSafeAssetName(string? name) => name is not null && SafeName().IsMatch(name) && !name.Contains("..", StringComparison.Ordinal);

    /// <summary>True only for https://github.com/&lt;repo&gt;/releases/download/&lt;tag&gt;/&lt;file&gt; — the one place this program's
    /// releases are published. (GitHub then redirects to its own storage; the content is checked by signature either way.)</summary>
    internal static bool IsTrustedUrl(string? url, string repo)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        if (u.Scheme != Uri.UriSchemeHttps || !u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || !u.IsDefaultPort) return false;
        if (u.UserInfo.Length > 0 || u.Query.Length > 0 || u.Fragment.Length > 0) return false;
        var prefix = $"/{repo}/releases/download/";
        if (!u.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var rest = u.AbsolutePath[prefix.Length..].Split('/');
        return rest.Length == 2 && rest[0].Length > 0 && IsSafeAssetName(rest[1]) && !rest[0].Contains("..", StringComparison.Ordinal);
    }

    /// <summary>The exact text a release signature covers. <c>scripts/sign-release.sh</c> builds the same bytes.</summary>
    internal static string Message(string version, string assetName, string sha256Hex)
        => "BrAIn-update-v1\n" + version + "\n" + assetName + "\n" + sha256Hex.ToLowerInvariant() + "\n";

    /// <summary>Does <paramref name="signatureBase64"/> prove that a trusted key signed exactly this version, name and content hash?</summary>
    internal static bool Verify(string version, string assetName, string sha256Hex, string signatureBase64)
    {
        byte[] sig;
        try { sig = Convert.FromBase64String((signatureBase64 ?? "").Trim()); }
        catch (FormatException) { return false; }
        if (sig.Length is < 8 or > 160) return false;
        var data = Encoding.UTF8.GetBytes(Message(version, assetName, sha256Hex));
        foreach (var key in AllKeys())
        {
            try
            {
                using var ec = ECDsa.Create();
                ec.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key), out _);
                if (ec.VerifyData(data, sig, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)) return true;
            }
            catch { /* a key that does not load or a signature of the wrong shape: this key says no */ }
        }
        return false;
    }

    private static IEnumerable<string> AllKeys()
    {
        foreach (var k in Keys) yield return k;
        var extra = Environment.GetEnvironmentVariable(TestKeyVariable);
        if (!string.IsNullOrWhiteSpace(extra)) yield return extra.Trim();
    }
}
