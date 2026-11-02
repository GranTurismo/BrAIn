using System.Text.Json;

namespace BrAIn.Browser;

/// <summary>
/// Quick controls — the sliders button in the toolbar: a few simple switches (block pop-ups,
/// block autoplay, mute sound, dark pages, allow copy &amp; right-click), each settable for this
/// tab, this site or everywhere. The narrowest scope that has a value wins: tab › site ›
/// everywhere. Site and everywhere values persist in prefs.json; tab values go with the tab.
///
/// Pop-ups and sound are enforced here (the engine's new-window hook, native mute). The page
/// switches run inside the document (chrome.js), which gets its values from {b:97} pushes and,
/// from a document-start script (macOS: the tab view's user script; Windows: the DevTools
/// driver's new-document script) so nothing flashes before the first push lands.
/// ({b:90}-{b:92} belong to the Personality page.)
/// </summary>
public sealed partial class BrowserController
{
    internal static readonly string[] QcIds = ["popups", "autoplay", "mute", "dark", "unlock"];
    // what chrome.js applies in the page (pop-ups too: it opens the user's own target=_blank clicks as tabs)
    private static readonly string[] QcPageIds = ["popups", "autoplay", "dark", "unlock"];

    private readonly Dictionary<string, bool> _qcAll = new();
    private readonly Dictionary<string, Dictionary<string, bool>> _qcSites = new(); // site key → values
    private readonly object _qcLock = new();

    /// <summary>The value that applies to <paramref name="id"/> on <paramref name="tab"/> while it shows <paramref name="url"/>.</summary>
    private bool QcOn(BrowserTab? tab, string id, string? url)
    {
        lock (_qcLock)
        {
            if (tab is not null && tab.Qc.TryGetValue(id, out var v)) return v;
            if (SiteKey(url) is { } site && _qcSites.TryGetValue(site, out var s) && s.TryGetValue(id, out v)) return v;
            return _qcAll.TryGetValue(id, out v) && v;
        }
    }

    /// <summary>Address of the document the tab shows right now (the native one in per-tab mode:
    /// at document start it is already the new page while LastUrl may still be the old one).</summary>
    private string QcUrl(BrowserTab tab)
        => (!PerTab ? null
            : tab.WinView is Win.WinTabs.View wv ? _winTabs?.Url(wv)
            : (tab.Driver as Drivers.WebKitDriver)?.NativeUrl()) ?? tab.LastUrl;

    /// <summary>Site key used for the "This site" scope, or null where it can't apply
    /// (internal pages, and private tabs / Ghost mode, which never write sites to disk).</summary>
    private string? QcSite(BrowserTab tab) => tab.IsPrivate || _ghost ? null : SiteKey(tab.LastUrl);

    /// <summary>{qc} in the b:9 state: the active tab's values per scope for the toolbar panel.</summary>
    private object? QcState(BrowserTab? tab)
    {
        if (tab is null) return null;
        var key = SiteKey(tab.LastUrl);
        lock (_qcLock)
            return new
            {
                t = new Dictionary<string, bool>(tab.Qc),
                s = key is not null && _qcSites.TryGetValue(key, out var sv) ? new Dictionary<string, bool>(sv) : new Dictionary<string, bool>(),
                a = new Dictionary<string, bool>(_qcAll),
                site = QcSite(tab),
                n = tab.PopupsBlocked,
                lp = tab.LastBlockedPopup,
            };
    }

    /// <summary>qcset:&lt;t|s|a&gt;:&lt;id&gt;:&lt;1|0|x&gt; — set (or clear, x) one switch for the
    /// active tab, its site or everywhere.</summary>
    private void QcSet(BrowserTab tab, string arg)
    {
        var p = arg.Split(':');
        if (p.Length != 3 || !QcIds.Contains(p[1]) || p[2] is not ("1" or "0" or "x")) return;
        bool? v = p[2] == "x" ? null : p[2] == "1";
        if (!QcEdit(tab, p[0], m => { if (v is { } on) m[p[1]] = on; else m.Remove(p[1]); })) return;
        QcChanged(p[0] != "t");
    }

    /// <summary>qcreset:&lt;t|s|a&gt; — forget every switch set at that scope.</summary>
    private void QcReset(BrowserTab tab, string scope)
    {
        if (!QcEdit(tab, scope, m => m.Clear())) return;
        QcChanged(scope != "t");
    }

    private bool QcEdit(BrowserTab tab, string scope, Action<Dictionary<string, bool>> edit)
    {
        lock (_qcLock)
        {
            switch (scope)
            {
                case "t": edit(tab.Qc); return true;
                case "a": edit(_qcAll); return true;
                case "s":
                    if (QcSite(tab) is not { } site) return false;
                    if (!_qcSites.TryGetValue(site, out var m)) _qcSites[site] = m = new();
                    edit(m);
                    if (m.Count == 0) _qcSites.Remove(site);
                    return true;
                default: return false;
            }
        }
    }

    private void QcChanged(bool persist)
    {
        if (persist) SavePrefs();
        foreach (var t in Tabs.All)
        {
            QcApplyTab(t);
            // Windows: the next document's start-up script carries the values (see QcBootFor)
            if (!_noToolbar && QcCdp(t) is { } cdp) _ = cdp.SetBootStateAsync(BuildChromeStateJson(null, "inj"), CancellationToken.None);
        }
        PushChromeStateAll();
    }

    /// <summary>Brings a tab in line with its current values: native mute, the macOS dark look,
    /// the document-start copy for its next page, and a {b:97} push to the page it shows now.</summary>
    private void QcApplyTab(BrowserTab tab, string? url = null)
    {
        if (tab.IsClosed || _noToolbar) return;
        url ??= QcUrl(tab);
        QcApplyMute(tab, url);
        var page = QcPageIds.ToDictionary(id => id, id => QcOn(tab, id, url));
        var json = JsonSerializer.Serialize(new { b = 97, qc = page, api = ApiPageState(tab, url) });
        try
        {
            if (PerTab)
            {
                var boot = IsNative && tab.NativeView != IntPtr.Zero ? QcBootScript(tab) : null;
                InvokeOnUi(() =>
                {
                    if (tab.IsClosed) return;
                    if (boot is not null)
                    {
                        NativeShell.TabsSetQcScript(tab.NativeView, boot);
                        NativeShell.TabsSetDarkLook(tab.NativeView, page["dark"]);
                    }
                    PushToView(tab, json);
                });
            }
            else if (tab == DocTab)
                InvokeOnUi(() => { if (!tab.IsClosed) tab.Window.SendWebMessage(json); });
            if (QcCdp(tab) is { } cdp) _ = cdp.SetDarkSchemeAsync(page["dark"]); // Windows: the macOS dark look's counterpart
        }
        catch { /* window mid-close */ }
    }

    /// <summary>Windows: the DevTools driver behind the document <paramref name="tab"/> shows.</summary>
    private Cdp.CdpDriver? QcCdp(BrowserTab tab)
        => (PerTab ? tab.Driver : tab == DocTab ? _driverTab?.Driver : null) as Cdp.CdpDriver;

    private void QcApplyMute(BrowserTab tab, string? url)
    {
        var mute = QcOn(tab, "mute", url);
        if (tab.IsMuted == mute) return;
        tab.IsMuted = mute;
        if (PerTab) ApplyTabMute(tab);
        else SyncPageMute();
    }

    /// <summary>A new document started in the tab (chrome.js says so at document start): its
    /// pop-up count starts over. The engine's new-window request can overtake that message
    /// (WebKit dispatches its synchronous requests ahead of queued script messages), so a count
    /// made moments ago by this very document is kept. Returns whether a count was dropped.</summary>
    private bool QcDocReset(BrowserTab tab)
    {
        if (tab.PopupsBlocked == 0) return false;
        // page messages arrive on the main thread, so the view can be asked directly
        var doc = tab.WinView is Win.WinTabs.View wv ? _winTabs?.Url(wv)
            : IsNative && tab.NativeView != IntPtr.Zero ? NativeShell.Url(tab.NativeView)
            : tab.LastUrl;
        if (tab.PopupOpener == doc && Environment.TickCount64 - tab.PopupAt < 3000) return false;
        var had = tab.PopupsBlocked > 0;
        tab.PopupsBlocked = 0;
        tab.LastBlockedPopup = null;
        return had;
    }

    /// <summary>...then the tab's values are applied for the page it now shows.</summary>
    private void QcDocStart(BrowserTab tab, bool had)
    {
        var muted = tab.IsMuted;
        QcApplyTab(tab);
        // the tab pill's speaker, or the panel's blocked count, changed
        if (muted != tab.IsMuted || (had && Tabs.Active?.Id == tab.Id)) PushChromeStateAll();
    }

    /// <summary>qcpop — open the last blocked pop-up of the active tab as a normal tab.</summary>
    private async Task QcOpenPopupAsync(BrowserTab tab)
    {
        var url = tab.LastBlockedPopup;
        if (string.IsNullOrEmpty(url)) return;
        tab.LastBlockedPopup = null;
        tab.PopupsBlocked = 0;
        await NewTabAsync(url, default);
    }

    /// <summary>The engine asks for a new window (window.open, target=_blank, sized popup).
    /// True = pop-ups are blocked where it came from: the request is dropped and counted.</summary>
    private bool QcBlocksPopup(BrowserTab? tab, string openerUrl, string url)
    {
        if (_noToolbar || !QcOn(tab, "popups", openerUrl)) return false;
        Log($"pop-up blocked: {url}");
        if (tab is null) return true;
        tab.PopupsBlocked++;
        tab.PopupOpener = openerUrl;
        tab.PopupAt = Environment.TickCount64;
        if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) tab.LastBlockedPopup = url;
        _ = Task.Run(PushChromeStateAll);
        return true;
    }

    /// <summary>Windows: the same values, put at the front of the DevTools driver's new-document
    /// script. A shared-view driver serves whichever tab owns the document.</summary>
    internal string QcBootFor(BrowserTab driverTab)
        => _noToolbar ? "" : QcBootScript(PerTab ? driverTab : DocTab ?? driverTab);

    /// <summary>Document-start script with the tab's page values for every scope. chrome.js
    /// resolves them against the new document's own site, then deletes the global before any page
    /// script can read it (it names the sites the user configured).</summary>
    private string QcBootScript(BrowserTab tab)
    {
        static Dictionary<string, bool> Page(Dictionary<string, bool> m)
            => m.Where(kv => QcPageIds.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        string json;
        lock (_qcLock)
            json = JsonSerializer.Serialize(new
            {
                t = Page(tab.Qc),
                s = _qcSites.Select(kv => (kv.Key, V: Page(kv.Value))).Where(x => x.V.Count > 0).ToDictionary(x => x.Key, x => x.V),
                a = Page(_qcAll),
            });
        return "window.__brainQc=" + json.Replace("<", "\\u003c") + ";" + ApiBootScript(tab);
    }

    // ---------------- prefs.json ----------------

    private void QcLoad(JsonElement root)
    {
        static void Read(JsonElement e, Dictionary<string, bool> into)
        {
            if (e.ValueKind != JsonValueKind.Object) return;
            foreach (var kv in e.EnumerateObject())
                if (QcIds.Contains(kv.Name) && kv.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    into[kv.Name] = kv.Value.GetBoolean();
        }
        lock (_qcLock)
        {
            if (root.TryGetProperty("qca", out var a)) Read(a, _qcAll);
            if (root.TryGetProperty("qcs", out var s) && s.ValueKind == JsonValueKind.Object)
                foreach (var site in s.EnumerateObject())
                {
                    var m = new Dictionary<string, bool>();
                    Read(site.Value, m);
                    if (m.Count > 0 && site.Name.Length is > 0 and < 256) _qcSites[site.Name] = m;
                }
        }
    }

    private (Dictionary<string, bool> All, Dictionary<string, Dictionary<string, bool>> Sites) QcSnapshot()
    {
        lock (_qcLock)
            return (new(_qcAll), _qcSites.ToDictionary(kv => kv.Key, kv => new Dictionary<string, bool>(kv.Value)));
    }
}
