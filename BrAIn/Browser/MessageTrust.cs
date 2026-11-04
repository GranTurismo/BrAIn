namespace BrAIn.Browser;

/// <summary>
/// Which host commands a message may trigger, depending on who sent it.
///
/// The model, in layers (macOS and Linux; each layer assumes the one above it can fail):
///  1. AUTHENTICATION (ViewAuth, WkTabs.ShimFor): chrome.js and brain.js run inside every web page, in the page's own JavaScript world.
///     Every message they send carries a per-view secret held in a closure the page cannot reach, and the host drops any page-view message
///     without it before this class is asked. So a page's own script cannot send a command at all, however it tries (window.external,
///     the native message handler, an iframe's copy of it). chrome.js also ignores input events a page fabricated (isTrusted).
///  2. FEED (PageFeed): a page can still WATCH what is pushed into its own world (poisoned built-ins), so a web page's view is only ever pushed
///     what a page has business with: no browsing history, bookmarks, other tabs, downloads. The toolbar lives in its own view in split mode.
///  3. COMMANDS (here): for what gets past (1) — the user's real clicks and keys, and BrAIn's own pages — the tiers below. Commands that only
///     BrAIn's own pages (history, passwords, flows, import) ever send are accepted only while one of those pages is showing; the approval and
///     seatbelt commands must come from the separate toolbar view when there is one (no web page runs there).
///  4. EFFECTS: whatever a command does host-side is limited to what a page could do itself (DevTools' edit-and-resend: the inspected site only,
///     never a local-network address; cookies: the inspected site's, HttpOnly values hidden).
/// Windows and the non-split modes keep the toolbar INSIDE the page, so layers 1 and 2 cannot separate the page from the toolbar there: only
/// layer 4 applies. That is the architectural gap to close (a toolbar view of its own, as on macOS and Linux).
/// </summary>
internal static class MessageTrust
{
    private static readonly HashSet<string> InternalOnly = new(StringComparer.Ordinal)
    {
        "actclear", "actget",
        "flowapprove", "flowdel", "flowdraft", "flowget", "flowreject", "flowrun", "flowsave",
        "histclear", "histdel", "histget",
        "importgo", "importscan",
        "keyset", "keyreset",
        "olclear", "olget", "olsend", "olset", "olstop",
        "pwdel", "pwget", "pwreveal",
        "apiget", "apisave", "apiapprove", "apidel", "apisite", "apisetlearn", "apisitedel", "apicleardrafts", "apiexport",
        "usget", "ussave", "usdel", "ustoggle", "usrun", "ustest", "usexclude", "usclrerr",
    };

    private static readonly HashSet<string> BarOnly = new(StringComparer.Ordinal) { "askok", "beltmode", "uploadreset", "agcancel", "handoff", "olside", "updateapply", "updatecancel", "ghostlogins", "apilearn", "qcset", "qcreset", "qcpop", "fullctl" };

    // History search: the toolbar's address bar and BrAIn's own pages (the new-tab search box).
    private static readonly HashSet<string> BarOrInternal = new(StringComparer.Ordinal) { "histq" };

    private static readonly HashSet<string> InternalUrls = new(StringComparer.Ordinal)
    {
        "about:newtab", "about:help", "about:shortcuts", "about:history", "about:import", "about:passwords", "about:flows", "about:personality", "about:roadmap", "about:ollama", "about:apis",
    };

    public static bool IsInternalUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        var i = url.IndexOfAny(new[] { '#', '?' });
        return InternalUrls.Contains(i < 0 ? url : url[..i]);
    }

    /// <param name="fromBar">The message came from the toolbar view (a BrAIn-only document).</param>
    /// <param name="splitOn">A separate toolbar view exists, so toolbar-only commands can be told apart.</param>
    /// <param name="senderUrl">The address of the document that sent it (the tab's current URL).</param>
    public static bool Allowed(string verb, bool fromBar, bool splitOn, string? senderUrl)
    {
        if (fromBar) return true;
        if (InternalOnly.Contains(verb)) return IsInternalUrl(senderUrl);
        if (splitOn && BarOnly.Contains(verb)) return false;
        if (splitOn && BarOrInternal.Contains(verb)) return IsInternalUrl(senderUrl);
        return true;
    }
}
