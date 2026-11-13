using System.Runtime.InteropServices;

namespace BrAIn.Browser.Linux;

/// <summary>
/// Popup support (Linux counterpart of <c>Mac/WkPopup.cs</c>): the engine asks for a new window through the
/// view's "create" signal, and nothing opens unless the app answers.
///   - a link click / form submit with a target             → <see cref="OnNewTab"/>(url), returns NULL
///   - pop-ups blocked by the quick controls                 → NULL, nothing opens
///   - window.open() from script                             → a view related to the opener (so window.opener
///     stays wired); once the engine says it is ready to show, a sized popup (OAuth/payment flows carry
///     width/height) becomes a real window and a plain one becomes a browser tab.
/// </summary>
internal static unsafe partial class LinuxPopup
{
    /// <summary>BrAIn runs on its own invisible screen (no window manager there): popups are placed over the browser window,
    /// where the terminal viewer shows them, get a title bar of their own with a close button, and take the keyboard.</summary>
    internal static bool Headless;

    [LibraryImport("libgtk-3.so.0")] private static partial IntPtr gtk_header_bar_new();
    [LibraryImport("libgtk-3.so.0")] private static partial void gtk_header_bar_set_show_close_button(IntPtr bar, int show);
    [LibraryImport("libgtk-3.so.0", StringMarshalling = StringMarshalling.Utf8)] private static partial void gtk_header_bar_set_title(IntPtr bar, string title);
    [LibraryImport("libgtk-3.so.0")] private static partial void gtk_window_set_titlebar(IntPtr win, IntPtr bar);

    private static readonly Dictionary<IntPtr, IntPtr> _headerOf = new(); // popup window → its header bar (headless)

    /// <summary>The open popup windows: (web view, window, title), oldest first. Main thread only.</summary>
    internal static List<(IntPtr View, IntPtr Window, string Title)> OpenWindows()
    {
        var list = new List<(IntPtr, IntPtr, string)>();
        lock (_windowOf)
            foreach (var (view, win) in _windowOf)
                list.Add((view, win, GLib.Str(WebKit.webkit_web_view_get_title(view)) ?? ""));
        return list;
    }
    /// <summary>A plain new-window request wants a tab. Main-thread only.</summary>
    internal static Action<string>? OnNewTab;
    /// <summary>Quick controls: (opener view, opener URL, popup URL) → true drops the request because
    /// pop-ups are blocked there. Main-thread only.</summary>
    internal static Func<IntPtr, string, string, bool>? Blocks;
    internal static Action<string>? Log;

    private sealed class Pending { public IntPtr Opener; public string Url = ""; }

    private static readonly Dictionary<IntPtr, IntPtr> _windowOf = new(); // popup view → its GtkWindow

    internal static void Connect(IntPtr view)
    {
        GLib.Connect(view, "create", (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr>)&OnCreate);
    }

    private static string? CurrentUrl(IntPtr view) => GLib.Str(WebKit.webkit_web_view_get_uri(view));

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static IntPtr OnCreate(IntPtr view, IntPtr action, IntPtr data)
    {
        try
        {
            var req = WebKit.webkit_navigation_action_get_request(action);
            var url = GLib.Str(req == IntPtr.Zero ? IntPtr.Zero : WebKit.webkit_uri_request_get_uri(req)) ?? "";

            // Only web URLs can live in a browser tab; javascript:/data:/empty requests are left to the engine
            // (NULL = no window, nothing to host).
            var web = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                   || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            if (!web) return IntPtr.Zero;

            // Pop-ups blocked for this tab/site: nothing opens and window.open returns null.
            if (Blocks?.Invoke(view, CurrentUrl(view) ?? "", url) == true) return IntPtr.Zero;

            var type = WebKit.webkit_navigation_action_get_navigation_type(action);
            if (type != WebKit.NavOther)
            {
                // a link or a form with a target: it is a tab, there is no script waiting for the window
                OnNewTab?.Invoke(url);
                return IntPtr.Zero;
            }

            // window.open(): hand out a related view and decide once its window features are known.
            var related = WebKit.webkit_web_view_new_with_related_view(view);
            if (related == IntPtr.Zero) { OnNewTab?.Invoke(url); return IntPtr.Zero; }
            GLib.g_object_ref_sink(related);
            var token = GLib.Stash(new Pending { Opener = view, Url = url });
            GLib.Connect(related, "ready-to-show", (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&OnReadyToShow, token);
            LinuxDownloads.Connect(related);
            LinuxPrompts.Connect(related);
            return related;
        }
        catch (Exception ex) { Log?.Invoke("popup failed: " + ex.Message); return IntPtr.Zero; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static void OnReadyToShow(IntPtr popup, IntPtr token)
    {
        try
        {
            var p = GLib.Unstash<Pending>(token);
            var props = WebKit.webkit_web_view_get_window_properties(popup);
            var rect = stackalloc int[4]; // GdkRectangle: x, y, width, height
            if (props != IntPtr.Zero) WebKit.webkit_window_properties_get_geometry(props, rect);
            // Without window features WebKit reports the opener window's own size as the popup's geometry; a feature
            // string (width=…, height=…) reports the requested one. So "sized" = "differs from the opener's window".
            var main = LinuxWindow.Window;
            var ww = 0; var wh = 0;
            if (main != IntPtr.Zero) Gtk.gtk_window_get_size(main, out ww, out wh);
            var openerView = p?.Opener ?? IntPtr.Zero;
            var vw = openerView == IntPtr.Zero ? 0 : Gtk.gtk_widget_get_allocated_width(openerView);
            var vh = openerView == IntPtr.Zero ? 0 : Gtk.gtk_widget_get_allocated_height(openerView);
            bool Same(int a, int w1, int w2) => Math.Abs(a - w1) <= 2 || Math.Abs(a - w2) <= 2;
            var sized = (rect[2] > 0 && !Same(rect[2], ww, vw)) || (rect[3] > 0 && !Same(rect[3], wh, vh));
            if (!sized)
            {
                // Everything else becomes a normal browser tab.
                var url = GLib.Str(WebKit.webkit_web_view_get_uri(popup)) ?? p?.Url ?? "";
                WebKit.webkit_web_view_stop_loading(popup);
                GLib.g_object_unref(popup);
                if (url.Length > 0) OnNewTab?.Invoke(url);
                return;
            }
            ShowWindow(popup, Math.Max(200, rect[2] > 0 ? rect[2] : 760), Math.Max(120, rect[3] > 0 ? rect[3] : 560), p?.Opener ?? IntPtr.Zero);
        }
        catch (Exception ex) { Log?.Invoke("popup failed: " + ex.Message); }
    }

    /// <summary>A sized popup keeps a real window so window.opener works and the flow can post back to its parent.</summary>
    private static void ShowWindow(IntPtr popup, int w, int h, IntPtr opener)
    {
        var win = Gtk.gtk_window_new(0 /* GTK_WINDOW_TOPLEVEL */);
        var main = LinuxWindow.Window;
        int x = 0, y = 0;
        if (Headless && main != IntPtr.Zero)
        {
            // over the browser window (at the screen's corner, where the viewer looks), fitted inside it, with a title bar
            // GTK draws itself (there is no window manager to): the page's title and a close button
            Gtk.gtk_window_get_size(main, out var mw, out var mh);
            w = Math.Min(w, Math.Max(200, mw - 40));
            h = Math.Min(h, Math.Max(120, mh - 80));
            x = Math.Max(0, (mw - w) / 2);
            y = Math.Max(0, (mh - h - 40) / 2);
            var bar = gtk_header_bar_new();
            gtk_header_bar_set_show_close_button(bar, 1);
            gtk_header_bar_set_title(bar, "Loading…");
            gtk_window_set_titlebar(win, bar);
            lock (_windowOf) _headerOf[win] = bar;
        }
        Gtk.gtk_window_set_default_size(win, w, h);
        if (Headless) Gtk.gtk_window_move(win, x, y);
        else Gtk.gtk_window_set_position(win, 1 /* GTK_WIN_POS_CENTER */);
        if (main != IntPtr.Zero) Gtk.gtk_window_set_transient_for(win, main);
        Gtk.gtk_container_add(win, popup);
        GLib.g_object_unref(popup); // the window owns it now
        lock (_windowOf) _windowOf[popup] = win;
        GLib.Connect(popup, "close", (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&OnClose);
        GLib.Connect(popup, "notify::title", (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&OnTitle, win);
        if (Headless) GLib.Connect(win, "destroy", (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&OnWindowGone);
        Gtk.gtk_widget_show_all(win);
        if (Headless) Gtk.gtk_window_present(win); // the keyboard goes to the popup (the sign-in field)
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static void OnClose(IntPtr popup, IntPtr data)
    {
        try
        {
            IntPtr win;
            lock (_windowOf) { if (!_windowOf.Remove(popup, out win)) return; }
            if (win != IntPtr.Zero) Gtk.gtk_widget_destroy(win);
        }
        catch { }
    }

    /// <summary>Headless: a popup window went away (the page closed it, or its close button): forget it and give the keyboard
    /// back to the browser window (without a window manager nobody else would).</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static void OnWindowGone(IntPtr win, IntPtr data)
    {
        try
        {
            lock (_windowOf)
            {
                _headerOf.Remove(win);
                foreach (var kv in _windowOf.Where(kv => kv.Value == win).ToList()) _windowOf.Remove(kv.Key);
            }
            var main = LinuxWindow.Window;
            if (main != IntPtr.Zero) GLib.RunOnMainLoop(() => LinuxWindow.Activate(main));
        }
        catch { }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static void OnTitle(IntPtr popup, IntPtr pspec, IntPtr win)
    {
        try
        {
            var t = GLib.Str(WebKit.webkit_web_view_get_title(popup));
            if (!string.IsNullOrEmpty(t))
            {
                Gtk.gtk_window_set_title(win, t);
                IntPtr bar;
                lock (_windowOf) _headerOf.TryGetValue(win, out bar);
                if (bar != IntPtr.Zero) gtk_header_bar_set_title(bar, t);
            }
        }
        catch { }
    }
}
