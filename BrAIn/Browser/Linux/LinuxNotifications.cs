using System.Runtime.InteropServices;

namespace BrAIn.Browser.Linux;

/// <summary>
/// Web notifications (<c>new Notification(...)</c>): WebKitGTK shows nothing by itself — it hands every notification to
/// the view's "show-notification" signal and waits for the app. They become desktop notifications through libnotify
/// (the same library Photino already links), so they look like every other notification of the session. A click on one
/// raises the window, switches to the tab and fires the page's own <c>onclick</c>; a close from either side is passed on.
/// The permission itself is asked by <see cref="LinuxPrompts"/> before a site ever gets here.
/// </summary>
internal static unsafe partial class LinuxNotifications
{
    private const string LibNotify = "libnotify.so.4";

    [LibraryImport(LibNotify, StringMarshalling = StringMarshalling.Utf8)] private static partial int notify_init(string appName);
    [LibraryImport(LibNotify)] private static partial int notify_is_initted();
    [LibraryImport(LibNotify, StringMarshalling = StringMarshalling.Utf8)] private static partial IntPtr notify_notification_new(string summary, string body, string? icon);
    [LibraryImport(LibNotify)] private static partial int notify_notification_show(IntPtr n, out IntPtr error);
    [LibraryImport(LibNotify)] private static partial int notify_notification_close(IntPtr n, out IntPtr error);
    [LibraryImport(LibNotify, StringMarshalling = StringMarshalling.Utf8)]
    private static partial void notify_notification_add_action(IntPtr n, string action, string label, IntPtr callback, IntPtr userData, IntPtr freeFunc);

    /// <summary>Icon shown on notifications (the app's PNG). Set once by the controller.</summary>
    internal static string? IconPath;
    /// <summary>A notification was clicked: switch to the tab that owns this view. Main thread.</summary>
    internal static Action<IntPtr>? OnClicked;
    internal static Action<string>? Log;

    private sealed class Live
    {
        public IntPtr Web;      // the WebKitNotification (we hold a reference)
        public IntPtr Desktop;  // the NotifyNotification (we hold a reference)
        public IntPtr View;
        public bool Closing;
    }

    private static bool _ready, _unavailable;

    private static bool EnsureReady()
    {
        if (_ready) return true;
        if (_unavailable) return false;
        try { _ready = notify_is_initted() != 0 || notify_init("BrAIn") != 0; }
        catch (Exception ex) { Log?.Invoke("notifications unavailable: " + ex.Message); }
        _unavailable = !_ready;
        return _ready;
    }

    internal static void Connect(IntPtr view)
    {
        GLib.Connect(view, "show-notification", (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int>)&OnShow);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static int OnShow(IntPtr view, IntPtr web, IntPtr data)
    {
        try
        {
            if (!EnsureReady()) return 0; // not handled: WebKit does nothing, as before
            var title = GLib.Str(WebKit.webkit_notification_get_title(web)) ?? "";
            var body = GLib.Str(WebKit.webkit_notification_get_body(web)) ?? "";
            var icon = !string.IsNullOrEmpty(IconPath) && File.Exists(IconPath) ? IconPath : null;
            var n = notify_notification_new(title, body, icon);
            if (n == IntPtr.Zero) return 0;

            GLib.g_object_ref(web);
            var live = new Live { Web = web, Desktop = n, View = view };
            var token = GLib.Stash(live);
            notify_notification_add_action(n, "default", "Open",
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&OnAction, token, IntPtr.Zero);
            GLib.Connect(n, "closed", (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&OnDesktopClosed, token);
            GLib.Connect(web, "closed", (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&OnWebClosed, token);

            if (notify_notification_show(n, out var err) == 0)
            {
                // no notification service on this session: undo everything and let the page know it was not shown
                Log?.Invoke("showing a notification failed (is a notification service running?)");
                if (err != IntPtr.Zero) GLib.g_error_free(err);
                GLib.Unstash<Live>(token);
                GLib.g_object_unref(web);
                GLib.g_object_unref(n);
                return 0;
            }
            return 1;
        }
        catch (Exception ex) { Log?.Invoke("notification failed: " + ex.Message); return 0; }
    }

    /// <summary>The user clicked the notification itself.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static void OnAction(IntPtr notification, IntPtr action, IntPtr token)
    {
        try
        {
            var live = GLib.Peek<Live>(token);
            if (live is null) return;
            var win = LinuxWindow.Window;
            if (win != IntPtr.Zero) Gtk.gtk_window_present(win);
            OnClicked?.Invoke(live.View);
            WebKit.webkit_notification_clicked(live.Web); // fires the page's onclick
        }
        catch { }
    }

    /// <summary>The desktop closed it (dismissed, expired, or after a click): tell the page and release everything.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static void OnDesktopClosed(IntPtr notification, IntPtr token)
    {
        try
        {
            var live = GLib.Unstash<Live>(token);
            if (live is null) return;
            if (!live.Closing)
            {
                live.Closing = true;
                WebKit.webkit_notification_close(live.Web);
            }
            GLib.g_object_unref(live.Web);
            GLib.g_object_unref(live.Desktop);
        }
        catch { }
    }

    /// <summary>The page closed it (<c>notification.close()</c>): take the desktop one down.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static void OnWebClosed(IntPtr web, IntPtr token)
    {
        try
        {
            var live = GLib.Peek<Live>(token);
            if (live is null || live.Closing) return;
            live.Closing = true;
            notify_notification_close(live.Desktop, out var err);
            if (err != IntPtr.Zero) GLib.g_error_free(err);
        }
        catch { }
    }
}
