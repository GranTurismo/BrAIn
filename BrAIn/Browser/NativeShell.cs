using BrAIn.Browser.Import;

namespace BrAIn.Browser;

/// <summary>
/// The native "split shell" the macOS and Linux builds share: a window with a toolbar view over one web view per
/// tab. This facade is the only thing <see cref="BrowserController"/> and the WebKit driver talk to — on macOS each
/// call is exactly the Cocoa/WKWebView call the code made inline before the Linux port (Browser/Mac/*), on Linux it
/// is the GTK/WebKitGTK equivalent (Browser/Linux/*). Windows never gets here (it has its own WebView2 views).
///
/// Window "ids" are opaque numbers: the NSWindow windowNumber on macOS, the GtkWindow pointer on Linux.
/// Everything must run on the UI thread, like the code underneath.
/// </summary>
internal static class NativeShell
{
    private static bool OnMac => OperatingSystem.IsMacOS();

    // ------------------------------------------------------------------ windows

    internal static IntPtr WindowById(long id) => OnMac ? Mac.MacWindow.WindowByNumber(id) : Linux.LinuxWindow.WindowByNumber(id);
    internal static long WindowId(IntPtr win) => OnMac ? Mac.MacWindow.WindowNumber(win) : Linux.LinuxWindow.WindowNumber(win);
    internal static IntPtr[] AllWindows() => OnMac ? Mac.MacWindow.AllWindows() : Linux.LinuxWindow.AllWindows();
    internal static IntPtr FindWebView(IntPtr win) => OnMac ? Mac.MacWindow.FindWebView(win) : Linux.LinuxWindow.FindWebView(win);
    internal static IntPtr WindowForTab(BrowserTab tab) => OnMac ? Mac.MacWindow.NsWindowForTab(tab) : Linux.LinuxWindow.WindowForTab(tab);

    internal static void Activate(BrowserTab tab)
    {
        if (OnMac) Mac.MacWindow.Activate(tab); else Linux.LinuxWindow.Activate(tab);
    }

    internal static bool IsFullScreen(IntPtr win) => OnMac ? Mac.MacWindow.IsFullScreen(win) : Linux.LinuxWindow.IsFullScreen(win);

    internal static void ToggleFullScreen(IntPtr win)
    {
        if (OnMac) Mac.MacWindow.ToggleFullScreen(win); else Linux.LinuxWindow.ToggleFullScreen(win);
    }

    /// <summary>macOS only: leaves a legacy NSView full-screen presentation. False where there is no such thing.</summary>
    internal static bool ExitLegacyFullScreen(IntPtr win) => OnMac && Mac.MacWindow.ExitLegacyFullScreen(win);

    /// <summary>macOS only: snaps the web view's frame back after a full-screen round trip. GTK lays out by itself.</summary>
    internal static void RepairWebViewFrame(IntPtr win)
    {
        if (OnMac) Mac.MacWindow.RepairWebViewFrame(win);
    }

    internal static void BeginWindowDrag(IntPtr win)
    {
        if (OnMac) Mac.MacWindow.BeginWindowDrag(win); else Linux.LinuxWindow.BeginWindowDrag(win);
    }

    /// <summary>Linux only (the window has no native frame): starts an edge/corner resize. <paramref name="hitTest"/>
    /// is a Windows HT* code, as chrome.js sends.</summary>
    internal static void BeginWindowResize(IntPtr win, int hitTest)
    {
        if (!OnMac) Linux.LinuxWindow.BeginWindowResize(win, hitTest);
    }

    internal static void SetLoadingBar(IntPtr win, bool on)
    {
        if (OnMac) Mac.MacWindow.SetLoadingBar(win, on); else Linux.LinuxWindow.SetLoadingBar(win, on);
    }

    internal static void SetWindowBackground(IntPtr win)
    {
        if (OnMac) Mac.WkTabs.SetWindowBackground(win); else Linux.LinuxWindow.SetBackground(win);
    }

    /// <summary>Gives the download machinery the controller (its download list and data folder).</summary>
    internal static void InstallDownloads(IntPtr view, BrowserController ctl)
    {
        if (OnMac) Mac.WkDownloads.Install(view, ctl); else Linux.LinuxDownloads.Configure(ctl);
    }

    // ------------------------------------------------------------------ one web view

    internal static string? Url(IntPtr view) => OnMac ? Mac.MacWindow.GetWebViewUrl(view) : GLibStr(Linux.WebKit.webkit_web_view_get_uri(view));
    internal static string? Title(IntPtr view) => OnMac ? Mac.MacWindow.GetWebViewTitle(view) : GLibStr(Linux.WebKit.webkit_web_view_get_title(view));
    internal static bool IsLoading(IntPtr view) => OnMac ? Mac.MacWindow.IsWebViewLoading(view) : Linux.WebKit.webkit_web_view_is_loading(view) != 0;
    internal static double Progress(IntPtr view) => OnMac ? Mac.MacWindow.WebViewProgress(view) : Linux.WebKit.webkit_web_view_get_estimated_load_progress(view);
    internal static bool IsPlayingAudio(IntPtr view) => OnMac ? Mac.MacWindow.IsWebViewPlayingAudio(view) : Linux.WebKit.webkit_web_view_is_playing_audio(view) != 0;

    internal static void SetMuted(IntPtr view, bool muted)
    {
        if (OnMac) Mac.MacWindow.SetWebViewMuted(view, muted); else Linux.WebKit.webkit_web_view_set_is_muted(view, muted ? 1 : 0);
    }

    /// <summary>macOS WKWebView ships with picture-in-picture off for embedders; WebKitGTK has no such switch.</summary>
    internal static void EnablePictureInPicture(IntPtr view)
    {
        if (OnMac) Mac.MacWindow.EnablePictureInPicture(view);
    }

    private static string? GLibStr(IntPtr p) => Linux.GLib.Str(p);

    /// <summary>Fire-and-forget JS in a view; results come back through the "brain" message handler.</summary>
    internal static void Eval(IntPtr view, string js)
    {
        if (view == IntPtr.Zero) return;
        if (OnMac) Mac.ObjC.Send(view, Mac.ObjC.Sel("evaluateJavaScript:completionHandler:"), Mac.ObjC.NsString(js), IntPtr.Zero);
        else Linux.WebKit.EvalFireAndForget(view, js);
    }

    internal static string? CustomUserAgent(IntPtr view)
        => OnMac ? Mac.ObjC.NsStringToString(Mac.ObjC.Send(view, Mac.ObjC.Sel("customUserAgent")))
                 : GLibStr(Linux.WebKit.webkit_settings_get_user_agent(Linux.WebKit.webkit_web_view_get_settings(view)));

    internal static void SetUserAgent(IntPtr view, string? ua)
    {
        if (OnMac) Mac.ObjC.Send(view, Mac.ObjC.Sel("setCustomUserAgent:"), ua is null ? IntPtr.Zero : Mac.ObjC.NsString(ua));
        else Linux.WebKit.webkit_settings_set_user_agent(Linux.WebKit.webkit_web_view_get_settings(view), ua);
    }

    /// <summary>Makes the view report a light or dark appearance (prefers-color-scheme). False when unsupported.</summary>
    internal static bool SetAppearance(IntPtr view, string scheme)
    {
        var dark = scheme == "dark";
        if (OnMac)
        {
            var ap = Mac.ObjC.Send(Mac.ObjC.GetClass("NSAppearance"), Mac.ObjC.Sel("appearanceNamed:"),
                Mac.ObjC.NsString(dark ? "NSAppearanceNameDarkAqua" : "NSAppearanceNameAqua"));
            if (ap == IntPtr.Zero) return false;
            Mac.ObjC.Send(view, Mac.ObjC.Sel("setAppearance:"), ap);
            return true;
        }
        return Linux.LinuxTabs.SetEmulatedScheme(view, scheme);
    }

    // ------------------------------------------------------------------ per-tab views (WkTabs / LinuxTabs)

    internal static bool TabsHas(IntPtr view) => OnMac ? Mac.WkTabs.Has(view) : Linux.LinuxTabs.Has(view);

    internal static IntPtr TabsCreate(IntPtr win, Guid tab, string lib, bool priv, int zoom, bool ghostLogins)
        => OnMac ? Mac.WkTabs.Create(win, tab, lib, priv, zoom, ghostLogins) : Linux.LinuxTabs.Create(win, tab, lib, priv, zoom, ghostLogins);

    internal static void TabsShow(IntPtr win, IntPtr view)
    {
        if (OnMac) Mac.WkTabs.Show(win, view); else Linux.LinuxTabs.Show(win, view);
    }

    internal static void TabsDestroy(IntPtr view)
    {
        if (OnMac) Mac.WkTabs.Destroy(view); else Linux.LinuxTabs.Destroy(view);
    }

    internal static void TabsPush(IntPtr view, string json)
    {
        if (OnMac) Mac.WkTabs.Push(view, json); else Linux.LinuxTabs.Push(view, json);
    }

    internal static void TabsSetZoom(IntPtr view, int pct)
    {
        if (OnMac) Mac.WkTabs.SetZoom(view, pct); else Linux.LinuxTabs.SetZoom(view, pct);
    }

    internal static void TabsSetZoomAll(int pct)
    {
        if (OnMac) Mac.WkTabs.SetZoomAll(pct); else Linux.LinuxTabs.SetZoomAll(pct);
    }

    internal static void TabsSetQcScript(IntPtr view, string js)
    {
        if (OnMac) Mac.WkTabs.SetQcScript(view, js); else Linux.LinuxTabs.SetQcScript(view, js);
    }

    internal static void TabsSetDarkLook(IntPtr view, bool dark)
    {
        if (OnMac) Mac.WkTabs.SetDarkLook(view, dark); else Linux.LinuxTabs.SetDarkLook(view, dark);
    }

    internal static void TabsLoad(IntPtr view, string url)
    {
        if (OnMac) Mac.WkTabs.Load(view, url); else Linux.LinuxTabs.Load(view, url);
    }

    internal static void TabsLoadHtml(IntPtr view, string html)
    {
        if (OnMac) Mac.WkTabs.LoadHtml(view, html); else Linux.LinuxTabs.LoadHtml(view, html);
    }

    internal static bool TabsCanGoBack(IntPtr view) => OnMac ? Mac.WkTabs.CanGoBack(view) : Linux.LinuxTabs.CanGoBack(view);
    internal static bool TabsCanGoForward(IntPtr view) => OnMac ? Mac.WkTabs.CanGoForward(view) : Linux.LinuxTabs.CanGoForward(view);

    internal static Action<Guid, string>? TabsOnMessage
    {
        set { if (OnMac) Mac.WkTabs.OnMessage = value; else Linux.LinuxTabs.OnMessage = value; }
    }

    /// <summary>macOS only: tab switches crossfade — this runs the "hide the old page" step after the fade.</summary>
    internal static void TabsSetLater(Action<int, Action> later)
    {
        if (OnMac) Mac.WkTabs.Later = later;
    }

    internal static bool HasGhostStore => OnMac ? Mac.WkTabs.HasGhostStore : Linux.LinuxTabs.HasGhostStore;
    internal static Task SeedGhostStore() => OnMac ? Mac.WkTabs.SeedGhostStore() : Linux.LinuxTabs.SeedGhostStore();

    // ------------------------------------------------------------------ toolbar + side panel (WkBar / LinuxBar)

    internal static bool BarExists => OnMac ? Mac.WkBar.Exists : Linux.LinuxBar.Exists;

    internal static Action<string>? BarOnMessage
    {
        set { if (OnMac) Mac.WkBar.OnBarMessage = value; else Linux.LinuxBar.OnBarMessage = value; }
    }

    internal static bool BarCreate(IntPtr win, string script, string html, string? iconPng = null)
        => OnMac ? Mac.WkBar.Create(win, script, html) : Linux.LinuxBar.Create(win, script, html, iconPng);

    internal static void BarLayout(IntPtr win, string? mode = null)
    {
        if (OnMac) Mac.WkBar.Layout(win, mode); else Linux.LinuxBar.Layout(win, mode);
    }

    internal static void BarEval(string js)
    {
        if (OnMac) Mac.WkBar.Eval(js); else Linux.LinuxBar.Eval(js);
    }

    internal static void FocusBar(IntPtr win)
    {
        if (OnMac) Mac.WkBar.FocusBar(win); else Linux.LinuxBar.FocusBar(win);
    }

    internal static void FocusPage(IntPtr win)
    {
        if (OnMac) Mac.WkBar.FocusPage(win); else Linux.LinuxBar.FocusPage(win);
    }

    internal static bool SideExists => OnMac ? Mac.WkBar.SideExists : Linux.LinuxBar.SideExists;
    internal static bool SideOpen => OnMac ? Mac.WkBar.SideOpen : Linux.LinuxBar.SideOpen;

    internal static Action<string>? SideOnMessage
    {
        set { if (OnMac) Mac.WkBar.OnSideMessage = value; else Linux.LinuxBar.OnSideMessage = value; }
    }

    internal static bool CreateSide(IntPtr win, string script, string html)
        => OnMac ? Mac.WkBar.CreateSide(win, script, html) : Linux.LinuxBar.CreateSide(win, script, html);

    internal static bool SetSideOpen(IntPtr win, bool open)
        => OnMac ? Mac.WkBar.SetSideOpen(win, open) : Linux.LinuxBar.SetSideOpen(win, open);

    internal static void EvalSide(string js)
    {
        if (OnMac) Mac.WkBar.EvalSide(js); else Linux.LinuxBar.EvalSide(js);
    }

    // ------------------------------------------------------------------ pop-ups

    internal static Action<string>? PopupOnNewTab
    {
        set { if (OnMac) Mac.WkPopup.OnNewTab = value; else Linux.LinuxPopup.OnNewTab = value; }
    }

    internal static Action<string>? PopupLog
    {
        set { if (OnMac) Mac.WkPopup.Log = value; else Linux.LinuxPopup.Log = value; }
    }

    internal static Func<IntPtr, string, string, bool>? PopupBlocks
    {
        set { if (OnMac) Mac.WkPopup.Blocks = value; else Linux.LinuxPopup.Blocks = value; }
    }

    // ------------------------------------------------------------------ clipboard

    internal static string? ClipboardText() => OnMac ? Mac.WkClipboard.Text() : Linux.LinuxClipboard.Text();
    internal static bool SetClipboardText(string text) => OnMac ? Mac.WkClipboard.SetText(text) : Linux.LinuxClipboard.SetText(text);

    internal static bool SetClipboardImage(byte[] data, string mime, long pixels)
        => OnMac ? Mac.WkClipboard.SetImage(data, mime, pixels) : Linux.LinuxClipboard.SetImage(data, mime, pixels);

    // ------------------------------------------------------------------ real input

    internal static void RealClick(BrowserTab tab, IntPtr view, double x, double y, int button, int clickCount)
    {
        if (OnMac)
        {
            var win = Mac.MacWindow.WindowByNumber(tab.MacWindowNumber);
            Mac.MacWindow.Activate(win);
            Mac.MacInput.Click(win, view, x, y, button, clickCount);
        }
        else Linux.LinuxInput.Click(Linux.LinuxWindow.WindowByNumber(tab.MacWindowNumber), view, x, y, button, clickCount);
    }

    internal static void RealMove(BrowserTab tab, IntPtr view, double x, double y)
    {
        var win = WindowById(tab.MacWindowNumber);
        if (OnMac) Mac.MacInput.MoveTo(win, view, x, y); else Linux.LinuxInput.MoveTo(win, view, x, y);
    }

    /// <summary>One mouse event straight to the view (5 = moved, 1 = left down, 2 = left up).</summary>
    internal static void SendMouseEvent(BrowserTab tab, IntPtr view, double x, double y, int type)
    {
        var win = WindowById(tab.MacWindowNumber);
        if (win == IntPtr.Zero) return;
        if (OnMac) Mac.MacInput.SendMouseEvent(win, view, x, y, type); else Linux.LinuxInput.SendMouseEvent(win, view, x, y, type);
    }

    // ------------------------------------------------------------------ cookies

    internal static int SetCookies(IntPtr view, IReadOnlyList<ImportedCookie> cookies)
        => OnMac ? Mac.WkCookieStore.SetCookies(view, cookies) : Linux.LinuxCookies.SetCookies(view, cookies);

    // ------------------------------------------------------------------ screenshots

    internal static bool ShotDebug => OnMac ? Mac.MacWindow.ShotDebug : Linux.LinuxShot.ShotDebug;
    internal static string ShotDir => OnMac ? Mac.MacWindow.ShotDir : Linux.LinuxShot.ShotDir;
    internal static long MaxCanvasBytes => OnMac ? Mac.MacWindow.MaxCanvasBytes : Linux.LinuxShot.MaxCanvasBytes;

    internal static void ShotLog(string msg)
    {
        if (OnMac) Mac.MacWindow.ShotLog(msg); else Linux.LinuxShot.ShotLog(msg);
    }

    internal static void ShotDump(string tag, IntPtr img)
    {
        if (OnMac) Mac.MacWindow.ShotDump(tag, img); else Linux.LinuxShot.ShotDump(tag, img);
    }

    /// <summary>Snapshot of a view's visible area; the task yields an image handle (release it with <see cref="ReleaseImage"/>) or zero.</summary>
    internal static Task<IntPtr> BeginSnapshot(IntPtr view) => OnMac ? Mac.MacWindow.BeginSnapshot(view) : Linux.LinuxShot.BeginSnapshot(view);

    internal static IntPtr CropImage(IntPtr img, double x, double y, double w, double h)
        => OnMac ? Mac.MacWindow.CropImage(img, x, y, w, h) : Linux.LinuxShot.CropImage(img, x, y, w, h);

    internal static int ImageWidth(IntPtr img) => OnMac ? Mac.MacWindow.ImageWidth(img) : Linux.LinuxShot.ImageWidth(img);
    internal static int ImageHeight(IntPtr img) => OnMac ? Mac.MacWindow.ImageHeight(img) : Linux.LinuxShot.ImageHeight(img);
    internal static byte[]? ImageToPng(IntPtr img) => OnMac ? Mac.MacWindow.ImageToPng(img) : Linux.LinuxShot.ImageToPng(img);

    internal static void ReleaseImage(IntPtr img)
    {
        if (OnMac) Mac.MacWindow.ReleaseImage(img); else Linux.LinuxShot.ReleaseImage(img);
    }

    /// <summary>Window grab fallback (clip in image pixels); null when unavailable.</summary>
    internal static byte[]? CaptureWindowPng(IntPtr win, (double X, double Y, double W, double H)? clipPx)
    {
        if (!OnMac) return Linux.LinuxShot.CapturePng(win, clipPx);
        Mac.MacWindow.CGRect? r = clipPx is { } c ? new Mac.MacWindow.CGRect { X = c.X, Y = c.Y, W = c.W, H = c.H } : null;
        return Mac.MacWindow.CapturePng(win, r);
    }

    internal static double BackingScale(IntPtr win) => OnMac ? Mac.MacWindow.BackingScale(win) : Linux.LinuxWindow.BackingScale(win);

    /// <summary>One tall bitmap that scrolling-screenshot frames are painted into.</summary>
    internal sealed class ShotCanvas : IDisposable
    {
        private readonly Mac.MacWindow.Canvas? _mac;
        private readonly Linux.LinuxShot.Canvas? _lin;

        private ShotCanvas(Mac.MacWindow.Canvas? m, Linux.LinuxShot.Canvas? l) { _mac = m; _lin = l; }

        public int Height => _mac?.Height ?? _lin!.Height;

        public static ShotCanvas? Create(int w, int h)
        {
            if (OnMac) { var m = Mac.MacWindow.Canvas.Create(w, h); return m is null ? null : new ShotCanvas(m, null); }
            var l = Linux.LinuxShot.Canvas.Create(w, h);
            return l is null ? null : new ShotCanvas(null, l);
        }

        public void Draw(IntPtr img, int topPx)
        {
            if (_mac is not null) _mac.Draw(img, topPx); else _lin!.Draw(img, topPx);
        }

        public byte[]? ToPng() => _mac is not null ? _mac.ToPng() : _lin!.ToPng();

        public void Dispose()
        {
            _mac?.Dispose();
            _lin?.Dispose();
        }
    }
}
