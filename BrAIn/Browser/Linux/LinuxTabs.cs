using System.Runtime.InteropServices;

namespace BrAIn.Browser.Linux;

/// <summary>
/// Per-tab web views (Linux counterpart of <c>Mac/WkTabs.cs</c>). Every tab owns a real WebKitGTK view, the way
/// Safari / Chrome do: switching tabs only changes which view is visible, so pages keep their scroll position,
/// video, form input and history, and two tabs can never show each other's content.
///
/// Each view gets its own WebKitUserContentManager with
///   - a "brain" script-message handler bound to the tab id, so every message arrives already attributed to the
///     tab that sent it, and
///   - a document-start shim that provides window.external.sendMessage / receiveMessage (the contract chrome.js
///     and the eval envelopes use — the very same string the macOS build injects) and the page helper scripts.
///
/// Everything here runs on the GTK main thread.
/// </summary>
internal static unsafe partial class LinuxTabs
{
    private sealed class ViewInfo
    {
        public IntPtr View, Ucm;
        public Guid Tab;
        public bool Private;
        public bool Ghost;   // private view on the seeded ghost store ("Keep my cookies & logins")
        public string Lib = "", Qc = "", Token = ""; // Token: this view's secret, see ViewAuth
        public bool Dark;        // the "dark pages" quick control
        public bool? Emulated;   // a scheme forced by browser_emulate (true dark, false light); wins over Dark
    }

    private static bool WantsDark(ViewInfo info) => info.Emulated ?? info.Dark;

    private static readonly Dictionary<IntPtr, ViewInfo> _views = new();
    private static IntPtr _privCtx, _ghostCtx;
    private static IntPtr _boot;

    /// <summary>Message from a page view: (tab id, "brain:..." command or JSON envelope).</summary>
    internal static Action<Guid, string>? OnMessage;

    /// <summary>The view currently shown.</summary>
    internal static IntPtr Active { get; private set; }

    /// <summary>webkit_web_view_get_uri — what the engine has committed, not what the host last asked for.</summary>
    internal static string? CurrentUrl(IntPtr view) => view == IntPtr.Zero ? null : GLib.Str(WebKit.webkit_web_view_get_uri(view));

    internal static bool Has(IntPtr view) => view != IntPtr.Zero && _views.ContainsKey(view);

    /// <summary>Tab id of a view, if it is one of ours.</summary>
    internal static Guid? TabOf(IntPtr view) => _views.TryGetValue(view, out var i) ? i.Tab : null;

    /// <summary>Whether the view belongs to a private / ghost tab.</summary>
    internal static bool IsPrivate(IntPtr view) => _views.TryGetValue(view, out var i) && i.Private;

    internal static void SetBootstrap(IntPtr bootView) => _boot = bootView;

    // ------------------------------------------------------------------ lifecycle

    /// <summary>Creates a view for a tab and adds it to the page stack (not shown until <see cref="Show"/>).</summary>
    internal static IntPtr Create(IntPtr win, Guid tab, string libSource, bool priv, int zoomPct, bool ghostLogins = false)
    {
        if (win == IntPtr.Zero || LinuxWindow.Pages == IntPtr.Zero) return IntPtr.Zero;

        var token = ViewAuth.NewToken();
        var ucm = LinuxEngine.NewContentManager(s =>
        {
            var ok = ViewAuth.Accept(token, s); // a page's own script cannot produce the token: its messages stop here
            if (ok is not null) OnMessage?.Invoke(tab, ok);
        });
        LinuxEngine.AddScript(ucm, Mac.WkTabs.ShimFor(token));
        LinuxEngine.AddScript(ucm, libSource);
        if (_netRec.Length > 0) LinuxEngine.AddScript(ucm, _netRec); // DevTools is open: this tab records its requests from the first one

        // Ghost tabs that keep the user's cookies share the seeded ephemeral store (LinuxTabs.Ghost.cs).
        // If it is gone (its last tab just closed), they fall back to the clean private store below.
        var ghost = priv && ghostLogins && _ghostCtx != IntPtr.Zero;
        IntPtr ctx;
        if (ghost) ctx = _ghostCtx;
        else if (priv)
        {
            // One ephemeral store shared by all private tabs (a private "window"): nothing it holds ever
            // reaches disk, and it is dropped with the last tab.
            if (_privCtx == IntPtr.Zero) _privCtx = LinuxEngine.NewEphemeral();
            ctx = _privCtx;
        }
        else ctx = LinuxEngine.Persistent;

        var view = LinuxEngine.NewView(ctx, ucm, LinuxEngine.NewSettings(true));
        if (view == IntPtr.Zero)
        {
            LinuxEngine.ReleaseContentManager(ucm);
            GLib.g_object_unref(ucm);
            return IntPtr.Zero;
        }
        GLib.g_object_unref(ucm); // the view holds it now

        // Pages draw their own background (default white) — internal pages set a dark one in theme.css.
        WebKit.SetBackground(view, 1, 1, 1, 1);
        if (zoomPct != 100) SetZoom(view, zoomPct);

        Gtk.gtk_stack_add_named(LinuxWindow.Pages, view, tab.ToString("N"));
        Gtk.gtk_widget_show(view);

        _views[view] = new ViewInfo { View = view, Ucm = ucm, Tab = tab, Private = priv, Ghost = ghost, Lib = libSource, Token = token };
        ConnectSignals(view);
        return view;
    }

    /// <summary>Quick controls: the view's document-start values (window.__brainQc, read and removed by
    /// chrome.js before any page script runs). Applies from the next document on; the page showing now gets a
    /// push instead.</summary>
    internal static void SetQcScript(IntPtr view, string js)
    {
        if (!_views.TryGetValue(view, out var info) || info.Qc == js) return;
        info.Qc = js;
        ApplyScripts(info);
    }

    /// <summary>The view's document-start scripts, in order: quick-control values, the shim, the page library and — only while
    /// DevTools is open — the network recorder.</summary>
    private static void ApplyScripts(ViewInfo info)
    {
        WebKit.webkit_user_content_manager_remove_all_scripts(info.Ucm);
        if (info.Qc.Length > 0) LinuxEngine.AddScript(info.Ucm, info.Qc);
        LinuxEngine.AddScript(info.Ucm, Mac.WkTabs.ShimFor(info.Token));
        LinuxEngine.AddScript(info.Ucm, info.Lib);
        if (_netRec.Length > 0) LinuxEngine.AddScript(info.Ucm, _netRec);
    }

    private static string _netRec = "";

    /// <summary>DevTools opened / closed: see WkTabs.SetNetRecorder.</summary>
    internal static void SetNetRecorder(string js)
    {
        if (_netRec == js) return;
        _netRec = js;
        foreach (var info in _views.Values) ApplyScripts(info);
    }

    /// <summary>Dark pages: the view reports a dark system appearance, so sites with a dark theme of their own
    /// (prefers-color-scheme) switch to it; chrome.js inverts the ones without. Off restores the light
    /// appearance every page gets by default. WebKitGTK takes the appearance from the toolkit, so it follows
    /// the view that is shown.</summary>
    internal static void SetDarkLook(IntPtr view, bool dark)
    {
        if (!_views.TryGetValue(view, out var info) || info.Dark == dark) return;
        info.Dark = dark;
        if (view == Active) LinuxAppearance.Apply(WantsDark(info));
    }

    /// <summary><c>browser_emulate colorScheme</c>: this view reports "dark" or "light" whatever the quick controls say. It is
    /// remembered per view because the toolkit setting is global and follows the view that is shown (see <see cref="Show"/>).
    /// False when the view is unknown.</summary>
    internal static bool SetEmulatedScheme(IntPtr view, string scheme)
    {
        if (!_views.TryGetValue(view, out var info)) return false;
        info.Emulated = scheme == "dark" ? true : scheme == "light" ? false : null;
        if (view == Active) LinuxAppearance.Apply(WantsDark(info));
        return true;
    }

    /// <summary>Tears a tab's view down completely (stops loads, drops scripts, ends its web process share).</summary>
    internal static void Destroy(IntPtr view)
    {
        if (view == IntPtr.Zero || !_views.Remove(view, out var info)) return;
        try
        {
            WebKit.webkit_web_view_stop_loading(view);
            WebKit.webkit_user_content_manager_remove_all_scripts(info.Ucm);
            LinuxEngine.ReleaseContentManager(info.Ucm);
            if (Active == view) Active = IntPtr.Zero;
            Gtk.gtk_widget_destroy(view); // removes it from the stack and drops the last reference
            LinuxFullscreen.ViewDestroyed(view); // it may have been fullscreen: the chrome must come back
        }
        catch { }
        if (info.Private && !info.Ghost && _privCtx != IntPtr.Zero && !_views.Values.Any(v => v.Private && !v.Ghost))
        {
            GLib.g_object_unref(_privCtx); // ephemeral data goes with it
            _privCtx = IntPtr.Zero;
        }
        if (info.Ghost && _ghostCtx != IntPtr.Zero && !_views.Values.Any(v => v.Ghost))
        {
            GLib.g_object_unref(_ghostCtx); // the cookie copy and everything set since dies here
            _ghostCtx = IntPtr.Zero;
        }
    }

    // ------------------------------------------------------------------ visibility

    /// <summary>Makes <paramref name="view"/> the visible page view. Keyboard focus only moves if it was inside
    /// a view that is being hidden (a focused address bar keeps its focus).</summary>
    internal static void Show(IntPtr win, IntPtr view)
    {
        if (view == IntPtr.Zero || !_views.TryGetValue(view, out var info)) return;
        var focus = win == IntPtr.Zero ? IntPtr.Zero : Gtk.gtk_window_get_focus(win);
        var prev = Active;
        bool InsideOldOrBoot(IntPtr f) =>
            f == IntPtr.Zero ||
            (prev != IntPtr.Zero && (f == prev || Gtk.gtk_widget_is_ancestor(f, prev) != 0)) ||
            (_boot != IntPtr.Zero && (f == _boot || Gtk.gtk_widget_is_ancestor(f, _boot) != 0));

        Gtk.gtk_widget_show(view);
        Gtk.gtk_stack_set_visible_child(LinuxWindow.Pages, view);
        Active = view;
        LinuxAppearance.Apply(WantsDark(info));
        // the window starts with no focus (Photino's own view is hidden now)
        if (view != prev && InsideOldOrBoot(focus)) Gtk.gtk_widget_grab_focus(view);
        else if (focus == IntPtr.Zero) Gtk.gtk_widget_grab_focus(view);
    }

    internal static void FocusActive(IntPtr win)
    {
        if (Active != IntPtr.Zero && win != IntPtr.Zero) Gtk.gtk_widget_grab_focus(Active);
    }

    // ------------------------------------------------------------------ view operations

    internal static void Load(IntPtr view, string url)
    {
        if (view == IntPtr.Zero) return;
        string abs;
        try { abs = new Uri(url).AbsoluteUri; } catch { abs = url; }
        WebKit.webkit_web_view_load_uri(view, abs);
    }

    /// <summary>Internal pages (start page, history…): raw HTML, no base URL.</summary>
    internal static void LoadHtml(IntPtr view, string html) => WebKit.webkit_web_view_load_html(view, html, null);

    internal static bool CanGoBack(IntPtr view) => WebKit.webkit_web_view_can_go_back(view) != 0;
    internal static bool CanGoForward(IntPtr view) => WebKit.webkit_web_view_can_go_forward(view) != 0;

    internal static void Eval(IntPtr view, string js) => WebKit.EvalFireAndForget(view, js);

    /// <summary>Host → page push (chrome state, DevTools data, password prompts…).</summary>
    internal static void Push(IntPtr view, string json)
        => Eval(view, "window.__brainPush&&window.__brainPush(" + json + ")");

    internal static void SetZoom(IntPtr view, int pct) => WebKit.webkit_web_view_set_zoom_level(view, pct / 100.0);

    internal static void SetZoomAll(int pct)
    {
        foreach (var v in _views.Keys) SetZoom(v, pct);
    }

    internal static IntPtr[] AllViews() => _views.Keys.ToArray();

    // ------------------------------------------------------------------ signals

    /// <summary>Everything the engine asks the app about: new windows, downloads, permission prompts,
    /// full-screen requests, crashes. Each concern lives in its own file.</summary>
    private static void ConnectSignals(IntPtr view)
    {
        LinuxPopup.Connect(view);
        LinuxDownloads.Connect(view);
        LinuxPrompts.Connect(view);
        LinuxFullscreen.Connect(view);
    }
}
