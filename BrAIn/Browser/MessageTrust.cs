namespace BrAIn.Browser;

/// <summary>
/// Which host commands a message may trigger, depending on who sent it.
/// chrome.js runs inside every web page, so a page's own scripts can call the same
/// window.external.sendMessage it does. Commands that only BrAIn's own pages
/// (history, passwords, flows, import) ever send are therefore accepted only while
/// one of those pages is showing; the approval and seatbelt commands must come from
/// the separate toolbar view when there is one (no web page runs there).
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
        "pwdel", "pwget", "pwreveal",
    };

    private static readonly HashSet<string> BarOnly = new(StringComparer.Ordinal) { "askok", "beltmode", "handoff", "histq", "updateapply" };

    private static readonly HashSet<string> InternalUrls = new(StringComparer.Ordinal)
    {
        "about:newtab", "about:help", "about:shortcuts", "about:history", "about:import", "about:passwords", "about:flows",
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
        return true;
    }
}
