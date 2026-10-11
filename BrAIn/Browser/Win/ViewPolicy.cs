namespace BrAIn.Browser.Win;

/// <summary>Pure decisions for Windows per-tab views (kept free of WebView2 so they can be unit-tested anywhere).</summary>
public static class ViewPolicy
{
    /// <summary>Which live tabs to evict so at most <paramref name="max"/> keep a view: least recently
    /// shown first. Never the shown tab, audible tabs, private tabs (their ephemeral session can't be
    /// restored) or tabs that are mid-load.</summary>
    public static List<BrowserTab> PickEvictionVictims(IEnumerable<BrowserTab> tabs, Guid? activeId, int max)
    {
        var live = tabs.Where(t => !t.IsClosed && t.WinView is not null).ToList();
        var over = live.Count - max;
        if (over <= 0) return new List<BrowserTab>();
        return live
            .Where(t => t.Id != activeId && !t.IsAudible && !t.IsPrivate && !t.Loading)
            .OrderBy(t => t.LastActiveUtc)
            .Take(over)
            .ToList();
    }

    /// <summary>The browser arguments Photino's native layer (Photino.Windows.cpp, AttachWebView) builds for its
    /// own WebView2 environment. A second environment on the same user-data folder only joins the same browser
    /// process when these match exactly, so the composition — order, quoting, trailing spaces — is verbatim.</summary>
    public static string ComposePhotinoArgs(string? userAgent, bool autoplay, bool fileAccess, bool webSecurity,
        bool jsClipboard, bool mediaStream, bool smoothScrolling, bool ignoreCertErrors, string initParameters)
    {
        var s = "";
        if (!string.IsNullOrEmpty(userAgent)) s += "--user-agent=\"" + userAgent + "\" ";
        if (autoplay) s += "--autoplay-policy=no-user-gesture-required ";
        if (fileAccess) s += "--allow-file-access-from-files ";
        if (!webSecurity) s += "--disable-web-security ";
        if (jsClipboard) s += "--enable-javascript-clipboard-access ";
        if (mediaStream) s += "--enable-usermedia-screen-capturing ";
        if (!smoothScrolling) s += "--disable-smooth-scrolling ";
        if (ignoreCertErrors) s += "--ignore-certificate-errors ";
        return s + initParameters;
    }
}
