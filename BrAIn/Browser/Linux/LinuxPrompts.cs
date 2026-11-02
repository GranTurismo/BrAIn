using System.Runtime.InteropServices;

namespace BrAIn.Browser.Linux;

/// <summary>
/// The questions the engine asks the user. WebKitGTK has no built-in UI for them (WKWebView shows an alert on
/// macOS), so a site that wants the camera, microphone, location or clipboard — or a server that wants a login —
/// gets a small native dialog here. Nothing is granted silently unless the app was started with
/// <c>--grant-permissions</c> (unattended automation). Dialogs are non-blocking: the request is answered from
/// the dialog's response signal, so the GTK main loop never nests.
/// </summary>
internal static unsafe class LinuxPrompts
{
    /// <summary>--grant-permissions / BRAIN_GRANT_PERMISSIONS: allow everything without asking.</summary>
    internal static bool GrantAll;

    private static readonly Dictionary<string, bool> _decided = new(StringComparer.OrdinalIgnoreCase); // "host|kind" → allowed

    private sealed class Ask
    {
        public IntPtr Request, Dialog, UserEntry, PassEntry;
        public string Key = "";
    }

    internal static void Connect(IntPtr view)
    {
        GLib.Connect(view, "permission-request", (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int>)&OnPermission);
        GLib.Connect(view, "authenticate", (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int>)&OnAuthenticate);
        LinuxNotifications.Connect(view); // what a site that was allowed to show notifications then does
    }

    // ------------------------------------------------------------------ permissions

    private enum Kind { Unknown, Camera, Microphone, CameraAndMic, Location, Notifications, Clipboard, Quiet }

    private static Kind Classify(IntPtr req)
    {
        if (GLib.IsA(req, WebKit.webkit_user_media_permission_request_get_type()))
        {
            var a = WebKit.webkit_user_media_permission_is_for_audio_device(req) != 0;
            var v = WebKit.webkit_user_media_permission_is_for_video_device(req) != 0;
            return a && v ? Kind.CameraAndMic : v ? Kind.Camera : Kind.Microphone;
        }
        if (GLib.IsA(req, WebKit.webkit_geolocation_permission_request_get_type())) return Kind.Location;
        if (GLib.IsA(req, WebKit.webkit_notification_permission_request_get_type())) return Kind.Notifications;
        if (GLib.IsA(req, WebKit.webkit_clipboard_permission_request_get_type())) return Kind.Clipboard;
        if (GLib.IsA(req, WebKit.webkit_device_info_permission_request_get_type())) return Kind.Quiet;
        return Kind.Unknown;
    }

    private static string What(Kind k) => k switch
    {
        Kind.Camera => "use your camera",
        Kind.Microphone => "use your microphone",
        Kind.CameraAndMic => "use your camera and microphone",
        Kind.Location => "know your location",
        Kind.Notifications => "show notifications",
        Kind.Clipboard => "see what you have copied to the clipboard",
        _ => "use a browser feature",
    };

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static int OnPermission(IntPtr view, IntPtr request, IntPtr data)
    {
        try
        {
            var kind = Classify(request);
            if (GrantAll || kind == Kind.Quiet) { WebKit.webkit_permission_request_allow(request); return 1; }
            if (kind == Kind.Unknown) { WebKit.webkit_permission_request_deny(request); return 1; }

            var host = "This page";
            try { var u = new Uri(GLib.Str(WebKit.webkit_web_view_get_uri(view)) ?? ""); if (u.Host.Length > 0) host = u.Host; } catch { }
            var key = host + "|" + kind;
            if (_decided.TryGetValue(key, out var allowed))
            {
                if (allowed) WebKit.webkit_permission_request_allow(request); else WebKit.webkit_permission_request_deny(request);
                return 1;
            }

            GLib.g_object_ref(request);
            ShowQuestion(view, $"{host} wants to {What(kind)}.", "Allow", "Block", new Ask { Request = request, Key = key });
            return 1; // answered later, from the dialog
        }
        catch { return 0; }
    }

    private static IntPtr NewDialog(IntPtr view, string title)
    {
        var dlg = Gtk.gtk_dialog_new();
        Gtk.gtk_window_set_title(dlg, title);
        var main = LinuxWindow.Window;
        if (main != IntPtr.Zero) Gtk.gtk_window_set_transient_for(dlg, main);
        Gtk.gtk_window_set_modal(dlg, 1);
        Gtk.gtk_window_set_position(dlg, 4 /* GTK_WIN_POS_CENTER_ON_PARENT */);
        return dlg;
    }

    private static IntPtr AddLabel(IntPtr dlg, string text)
    {
        var area = Gtk.gtk_dialog_get_content_area(dlg);
        Gtk.gtk_container_set_border_width(area, 18);
        Gtk.gtk_box_set_spacing(area, 10);
        var label = Gtk.gtk_label_new(text);
        Gtk.gtk_label_set_line_wrap(label, 1);
        Gtk.gtk_label_set_max_width_chars(label, 52);
        Gtk.gtk_label_set_xalign(label, 0f);
        Gtk.gtk_box_pack_start(area, label, 0, 0, 0);
        return area;
    }

    private static void ShowQuestion(IntPtr view, string text, string yes, string no, Ask ask)
    {
        var dlg = NewDialog(view, "Permission request");
        AddLabel(dlg, text);
        Gtk.gtk_dialog_add_button(dlg, no, Gtk.ResponseReject);
        Gtk.gtk_dialog_add_button(dlg, yes, Gtk.ResponseAccept);
        Gtk.gtk_dialog_set_default_response(dlg, Gtk.ResponseReject);
        ask.Dialog = dlg;
        GLib.Connect(dlg, "response", (delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr, void>)&OnPermissionResponse, GLib.Stash(ask));
        Gtk.gtk_widget_show_all(dlg);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static void OnPermissionResponse(IntPtr dlg, int response, IntPtr token)
    {
        var ask = GLib.Unstash<Ask>(token);
        try
        {
            if (ask is not null)
            {
                var allow = response == Gtk.ResponseAccept;
                _decided[ask.Key] = allow;
                if (allow) WebKit.webkit_permission_request_allow(ask.Request); else WebKit.webkit_permission_request_deny(ask.Request);
                GLib.g_object_unref(ask.Request);
            }
        }
        catch { }
        Gtk.gtk_widget_destroy(dlg);
    }

    // ------------------------------------------------------------------ HTTP authentication

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static int OnAuthenticate(IntPtr view, IntPtr request, IntPtr data)
    {
        try
        {
            var host = GLib.Str(WebKit.webkit_authentication_request_get_host(request)) ?? "this site";
            var realm = GLib.Str(WebKit.webkit_authentication_request_get_realm(request));
            var retry = WebKit.webkit_authentication_request_is_retry(request) != 0;
            GLib.g_object_ref(request);

            var dlg = NewDialog(view, "Sign in");
            var area = AddLabel(dlg, (retry ? "That did not work — try again.\n\n" : "") +
                $"{host} requires a username and password" + (string.IsNullOrEmpty(realm) ? "." : $" ({realm})."));
            var user = Gtk.gtk_entry_new();
            var pass = Gtk.gtk_entry_new();
            Gtk.gtk_entry_set_visibility(pass, 0);
            Gtk.gtk_entry_set_activates_default(user, 1);
            Gtk.gtk_entry_set_activates_default(pass, 1);
            Gtk.gtk_box_pack_start(area, user, 0, 0, 0);
            Gtk.gtk_box_pack_start(area, pass, 0, 0, 0);
            Gtk.gtk_dialog_add_button(dlg, "Cancel", Gtk.ResponseCancel);
            Gtk.gtk_dialog_add_button(dlg, "Sign in", Gtk.ResponseOk);
            Gtk.gtk_dialog_set_default_response(dlg, Gtk.ResponseOk);
            GLib.Connect(dlg, "response", (delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr, void>)&OnAuthResponse,
                GLib.Stash(new Ask { Request = request, Dialog = dlg, UserEntry = user, PassEntry = pass }));
            Gtk.gtk_widget_show_all(dlg);
            return 1;
        }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static void OnAuthResponse(IntPtr dlg, int response, IntPtr token)
    {
        var ask = GLib.Unstash<Ask>(token);
        try
        {
            if (ask is not null)
            {
                if (response == Gtk.ResponseOk)
                {
                    var u = GLib.Str(Gtk.gtk_entry_get_text(ask.UserEntry)) ?? "";
                    var p = GLib.Str(Gtk.gtk_entry_get_text(ask.PassEntry)) ?? "";
                    var cred = WebKit.webkit_credential_new(u, p, 0 /* WEBKIT_CREDENTIAL_PERSISTENCE_NONE */);
                    WebKit.webkit_authentication_request_authenticate(ask.Request, cred);
                    WebKit.webkit_credential_free(cred);
                }
                else WebKit.webkit_authentication_request_cancel(ask.Request);
                GLib.g_object_unref(ask.Request);
            }
        }
        catch { }
        Gtk.gtk_widget_destroy(dlg);
    }
}
