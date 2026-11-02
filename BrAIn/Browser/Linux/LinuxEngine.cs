using System.Runtime.InteropServices;

namespace BrAIn.Browser.Linux;

/// <summary>
/// WebKitGTK sessions, settings and view construction (main thread only).
///
/// A "session" in the 4.1 API is a <c>WebKitWebContext</c> bound to a <c>WebKitWebsiteDataManager</c>:
///   - <see cref="Persistent"/>  normal tabs: cookies, logins, storage live in the app's data folder,
///   - <see cref="NewEphemeral"/> private tabs (one shared by all of them), the Ghost store and the toolbar
///     itself: nothing it holds ever reaches disk and it is gone when the last view using it is.
/// Views of one context share a web-content process pool; different contexts never share anything.
/// </summary>
internal static unsafe class LinuxEngine
{
    private static IntPtr _persistent;
    private static IntPtr _ui;

    /// <summary>Where the persistent session keeps its data / cache (set before the first tab is created).</summary>
    internal static string DataDir = "";
    internal static string CacheDir = "";

    /// <summary>The session normal tabs use. Created on first use.</summary>
    internal static IntPtr Persistent
    {
        get
        {
            if (_persistent != IntPtr.Zero) return _persistent;
            try
            {
                if (DataDir.Length > 0)
                {
                    Directory.CreateDirectory(DataDir);
                    if (CacheDir.Length > 0) Directory.CreateDirectory(CacheDir);
                    var mgr = GLib.NewObjectStrings(WebKit.webkit_website_data_manager_get_type(),
                        ("base-data-directory", DataDir),
                        ("base-cache-directory", CacheDir.Length > 0 ? CacheDir : DataDir));
                    if (mgr != IntPtr.Zero)
                    {
                        _persistent = WebKit.webkit_web_context_new_with_website_data_manager(mgr);
                        // WebKitGTK keeps cookies in memory unless it is told where to store them: without this every login
                        // (and every "stay signed in" cookie) is forgotten when the app quits.
                        var cookies = WebKit.webkit_website_data_manager_get_cookie_manager(mgr);
                        if (cookies != IntPtr.Zero)
                            WebKit.webkit_cookie_manager_set_persistent_storage(cookies, Path.Combine(DataDir, "cookies.sqlite"), 1);
                        GLib.g_object_unref(mgr); // the context holds it
                    }
                }
            }
            catch (Exception ex) { BrowserController.Log("persistent web session failed: " + ex.Message); }
            if (_persistent == IntPtr.Zero) _persistent = WebKit.webkit_web_context_new();
            try { WebKit.webkit_web_context_set_spell_checking_enabled(_persistent, 1); } catch { }
            return _persistent;
        }
    }

    /// <summary>A fresh in-memory session (private tabs, Ghost, toolbar). The caller owns the reference.</summary>
    internal static IntPtr NewEphemeral() => WebKit.webkit_web_context_new_ephemeral();

    /// <summary>The toolbar and side panel's own throwaway session. They never browse, so they share nothing.</summary>
    internal static IntPtr UiSession => _ui != IntPtr.Zero ? _ui : _ui = NewEphemeral();

    /// <summary>Settings for a page view — the same browser behaviour WKWebView gives the macOS build.</summary>
    internal static IntPtr NewSettings(bool pageView)
    {
        var s = WebKit.webkit_settings_new();
        GLib.SetBool(s, "enable-developer-extras", false);            // our own DevTools live in the page (F12)
        GLib.SetBool(s, "javascript-can-open-windows-automatically", true); // window.open → the "create" signal → a tab
        GLib.SetBool(s, "enable-back-forward-navigation-gestures", true);   // two-finger swipe
        GLib.SetBool(s, "enable-media-stream", true);                 // camera / microphone (behind a prompt)
        GLib.SetBool(s, "enable-webrtc", true);
        GLib.SetBool(s, "enable-mediasource", true);
        GLib.SetBool(s, "enable-media-capabilities", true);
        GLib.SetBool(s, "enable-mock-capture-devices", false);
        GLib.SetBool(s, "javascript-can-access-clipboard", true);     // copy buttons (execCommand) keep working
        GLib.SetBool(s, "enable-smooth-scrolling", true);
        GLib.SetBool(s, "enable-page-cache", true);
        GLib.SetBool(s, "enable-fullscreen", true);                   // element.requestFullscreen
        GLib.SetBool(s, "enable-write-console-messages-to-stdout", false);
        if (!pageView)
        {
            // Our own chrome never needs the GPU, and WebKit's accelerated (GL / DMABuf) presentation is the one thing that goes
            // wrong for it. The toolbar view is window-sized but only ever partly visible (clipped to its strip or to the popover
            // it shows): under accelerated presentation a view like that is not drawn at all — an empty dark band where the toolbar
            // belongs — and the rows it grows into can stay unpainted (the palette's dimming stopped 46px short of the bottom).
            // Software painting has neither problem (verified against WebKitGTK 2.52 with the default renderer, the legacy one
            // and GDK GL off: only this combination is wrong). The policy is per view; page views keep full acceleration.
            WebKit.webkit_settings_set_hardware_acceleration_policy(s, WebKit.AccelNever);
            // the toolbar / side panel are our own documents: no page niceties that could get in their way
            GLib.SetBool(s, "enable-back-forward-navigation-gestures", false);
            GLib.SetBool(s, "enable-media-stream", false);
            GLib.SetBool(s, "enable-webrtc", false);
        }
        return s;
    }

    /// <summary>A view for BrAIn's own chrome (the toolbar overlay, the side panel): a normal view on the throwaway
    /// session. It is opaque — see <see cref="LinuxBackdrop"/> for how the page shows through the toolbar.</summary>
    internal static IntPtr NewChromeView(IntPtr ucm, IntPtr settings) => NewView(UiSession, ucm, settings);

    /// <summary>A web view of <paramref name="ctx"/> with its own content manager. Returns a floating widget
    /// (the container it is added to takes the reference); the caller keeps ucm/settings alive via the view.</summary>
    internal static IntPtr NewView(IntPtr ctx, IntPtr ucm, IntPtr settings)
        => GLib.NewObject(WebKit.webkit_web_view_get_type(),
            ("web-context", ctx), ("user-content-manager", ucm), ("settings", settings));

    /// <summary>Hooks a script-message handler named "brain" to <paramref name="onMessage"/>. The handler
    /// receives whatever the page posts through <c>window.webkit.messageHandlers.brain.postMessage(string)</c>.</summary>
    internal static IntPtr NewContentManager(Action<string> onMessage)
    {
        var ucm = WebKit.webkit_user_content_manager_new();
        var token = GLib.Stash(onMessage);
        WebKit.webkit_user_content_manager_register_script_message_handler(ucm, "brain");
        GLib.Connect(ucm, "script-message-received::brain",
            (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&OnScriptMessage, token);
        GLib.g_object_set_data(ucm, "brain-token", token);
        return ucm;
    }

    /// <summary>Drops the callback state of a content manager made by <see cref="NewContentManager"/>.</summary>
    internal static void ReleaseContentManager(IntPtr ucm)
    {
        if (ucm == IntPtr.Zero) return;
        var token = GLib.g_object_get_data(ucm, "brain-token");
        if (token != IntPtr.Zero) GLib.Unstash<Action<string>>(token);
        GLib.g_object_set_data(ucm, "brain-token", IntPtr.Zero);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static void OnScriptMessage(IntPtr ucm, IntPtr result, IntPtr token)
    {
        try
        {
            var cb = GLib.Peek<Action<string>>(token);
            if (cb is null) return;
            var js = WebKit.webkit_javascript_result_get_js_value(result);
            if (js == IntPtr.Zero || WebKit.jsc_value_is_string(js) == 0) return;
            var s = GLib.TakeStr(WebKit.jsc_value_to_string(js));
            if (!string.IsNullOrEmpty(s)) cb(s);
        }
        catch { }
    }

    /// <summary>Adds a document-start user script (main frame only).</summary>
    internal static void AddScript(IntPtr ucm, string source)
    {
        if (ucm == IntPtr.Zero || source.Length == 0) return;
        var us = WebKit.webkit_user_script_new(source, WebKit.InjectTopFrame, WebKit.AtDocumentStart, IntPtr.Zero, IntPtr.Zero);
        if (us == IntPtr.Zero) return;
        WebKit.webkit_user_content_manager_add_script(ucm, us);
        WebKit.webkit_user_script_unref(us); // the manager keeps its own reference
    }
}
