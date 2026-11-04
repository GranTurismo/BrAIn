using System.Security.Cryptography;
using System.Text;

namespace BrAIn.Browser;

/// <summary>
/// Which messages from a web page's view really come from BrAIn's own scripts.
/// BrAIn's scripts (chrome.js, brain.js) run inside every page, in the page's own JavaScript world — so the page's scripts can call
/// the same <c>window.webkit.messageHandlers</c> and could, before this, make the host do anything those scripts can: replay requests with
/// the user's cookies, read the clipboard, list every open tab. Now each view has a secret token the host puts into BrAIn's scripts (held in
/// a closure the page cannot reach, see WkTabs.ShimFor) and every message BrAIn sends carries it. A message without it is dropped here, before
/// the command layer sees it, whatever its text says. The two exceptions are harmless by construction: the replies to scripts the host itself
/// evaluated ({"b":1,...}, matched to unguessable request ids) and the user-script runner's two status reports.
/// </summary>
internal static class ViewAuth
{
    /// <summary>A new unguessable per-view secret (128 bits from the OS random source).</summary>
    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    /// <summary>The message to process (the token stripped), or null when it must be dropped.</summary>
    public static string? Accept(string token, string raw)
    {
        if (raw.Length > token.Length + 1 && raw[token.Length] == '|' &&
            CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(raw.AsSpan(0, token.Length).ToString()), Encoding.UTF8.GetBytes(token)))
            return raw[(token.Length + 1)..];
        if (raw.StartsWith("{\"b\":1,", StringComparison.Ordinal) || raw.StartsWith("{\"b\": 1,", StringComparison.Ordinal)) return raw;
        if (raw.StartsWith("brain:usran:", StringComparison.Ordinal) || raw.StartsWith("brain:usrerr:", StringComparison.Ordinal)) return raw;
        Console.Error.WriteLine("[BrAIn] dropped a message that was not sent by BrAIn's own scripts (" + (raw.Length > 40 ? raw[..40] + "…" : raw) + ")");
        return null;
    }
}
