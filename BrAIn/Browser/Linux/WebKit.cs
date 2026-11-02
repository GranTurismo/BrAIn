using System.Runtime.InteropServices;

namespace BrAIn.Browser.Linux;

/// <summary>WebKitGTK 4.1 (libsoup 3) + JavaScriptCore bindings — the engine behind the Linux build.
/// Photino.Native links the very same libwebkit2gtk-4.1 / libjavascriptcoregtk-4.1, so the library is
/// already in the process and these calls reach the instance Photino uses.</summary>
internal static unsafe partial class WebKit
{
    private const string LibWk = "libwebkit2gtk-4.1.so.0";
    private const string LibJsc = "libjavascriptcoregtk-4.1.so.0";

    // WebKitUserContentInjectedFrames / WebKitUserScriptInjectionTime
    internal const int InjectAllFrames = 0, InjectTopFrame = 1, AtDocumentStart = 0, AtDocumentEnd = 1;
    // WebKitPolicyDecisionType
    internal const int PolicyNavigation = 0, PolicyNewWindow = 1, PolicyResponse = 2;
    // WebKitSnapshotRegion / WebKitSnapshotOptions
    internal const int SnapshotVisible = 0, SnapshotFullDocument = 1, SnapshotNone = 0;
    // WebKitLoadEvent
    internal const int LoadStarted = 0, LoadRedirected = 1, LoadCommitted = 2, LoadFinished = 3;
    // WebKitHardwareAccelerationPolicy
    internal const int HwOnDemand = 0, HwAlways = 1, HwNever = 2;
    // WebKitNavigationType
    internal const int NavLinkClicked = 0, NavFormSubmitted = 1, NavBackForward = 2, NavReload = 3, NavFormResubmitted = 4, NavOther = 5;

    // ---- web view ----
    [LibraryImport(LibWk)] internal static partial nuint webkit_web_view_get_type();
    [LibraryImport(LibWk, StringMarshalling = StringMarshalling.Utf8)] internal static partial void webkit_web_view_load_uri(IntPtr v, string uri);
    [LibraryImport(LibWk, StringMarshalling = StringMarshalling.Utf8)] internal static partial void webkit_web_view_load_html(IntPtr v, string content, string? baseUri);
    [LibraryImport(LibWk)] internal static partial void webkit_web_view_reload(IntPtr v);
    [LibraryImport(LibWk)] internal static partial void webkit_web_view_reload_bypass_cache(IntPtr v);
    [LibraryImport(LibWk)] internal static partial void webkit_web_view_go_back(IntPtr v);
    [LibraryImport(LibWk)] internal static partial void webkit_web_view_go_forward(IntPtr v);
    [LibraryImport(LibWk)] internal static partial int webkit_web_view_can_go_back(IntPtr v);
    [LibraryImport(LibWk)] internal static partial int webkit_web_view_can_go_forward(IntPtr v);
    [LibraryImport(LibWk)] internal static partial void webkit_web_view_stop_loading(IntPtr v);
    [LibraryImport(LibWk)] internal static partial IntPtr webkit_web_view_get_uri(IntPtr v);
    [LibraryImport(LibWk)] internal static partial IntPtr webkit_web_view_get_title(IntPtr v);
    [LibraryImport(LibWk)] internal static partial int webkit_web_view_is_loading(IntPtr v);
    [LibraryImport(LibWk)] internal static partial double webkit_web_view_get_estimated_load_progress(IntPtr v);
    [LibraryImport(LibWk)] internal static partial void webkit_web_view_set_zoom_level(IntPtr v, double zoom);
    [LibraryImport(LibWk)] internal static partial double webkit_web_view_get_zoom_level(IntPtr v);
    [LibraryImport(LibWk)] internal static partial int webkit_web_view_is_playing_audio(IntPtr v);
    [LibraryImport(LibWk)] internal static partial void webkit_web_view_set_is_muted(IntPtr v, int muted);
    [LibraryImport(LibWk)] internal static partial IntPtr webkit_web_view_get_settings(IntPtr v);
    [LibraryImport(LibWk)] internal static partial void webkit_web_view_set_settings(IntPtr v, IntPtr settings);
    [LibraryImport(LibWk)] internal static partial void webkit_web_view_set_background_color(IntPtr v, double* rgba);
    [LibraryImport(LibWk)] internal static partial IntPtr webkit_web_view_get_user_content_manager(IntPtr v);
    [LibraryImport(LibWk)] internal static partial IntPtr webkit_web_view_get_context(IntPtr v);
    [LibraryImport(LibWk)] internal static partial IntPtr webkit_web_view_get_website_data_manager(IntPtr v);
    [LibraryImport(LibWk)] internal static partial IntPtr webkit_web_view_get_window_properties(IntPtr v);
    [LibraryImport(LibWk)] internal static partial IntPtr webkit_web_view_new_with_related_view(IntPtr related);
    [LibraryImport(LibWk)] internal static partial IntPtr webkit_web_view_get_inspector(IntPtr v);
    [LibraryImport(LibWk)] internal static partial void webkit_web_view_terminate_web_process(IntPtr v);
    [LibraryImport(LibWk, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void webkit_web_view_evaluate_javascript(IntPtr v, string script, nint length, string? world, string? sourceUri, IntPtr cancellable, IntPtr cb, IntPtr data);
    [LibraryImport(LibWk)] internal static partial IntPtr webkit_web_view_evaluate_javascript_finish(IntPtr v, IntPtr result, out IntPtr error);
    [LibraryImport(LibWk, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void webkit_web_view_run_javascript(IntPtr v, string script, IntPtr cancellable, IntPtr cb, IntPtr data);
    [LibraryImport(LibWk)] internal static partial void webkit_web_view_get_snapshot(IntPtr v, int region, int options, IntPtr cancellable, IntPtr cb, IntPtr data);
    [LibraryImport(LibWk)] internal static partial IntPtr webkit_web_view_get_snapshot_finish(IntPtr v, IntPtr result, out IntPtr error);

    // ---- user content ----
    [LibraryImport(LibWk)] internal static partial IntPtr webkit_user_content_manager_new();
    [LibraryImport(LibWk)] internal static partial void webkit_user_content_manager_add_script(IntPtr m, IntPtr script);
    [LibraryImport(LibWk)] internal static partial void webkit_user_content_manager_remove_all_scripts(IntPtr m);
    [LibraryImport(LibWk, StringMarshalling = StringMarshalling.Utf8)] internal static partial int webkit_user_content_manager_register_script_message_handler(IntPtr m, string name);
    [LibraryImport(LibWk, StringMarshalling = StringMarshalling.Utf8)] internal static partial void webkit_user_content_manager_unregister_script_message_handler(IntPtr m, string name);
    [LibraryImport(LibWk, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr webkit_user_script_new(string source, int frames, int time, IntPtr allow, IntPtr block);
    [LibraryImport(LibWk)] internal static partial void webkit_user_script_unref(IntPtr script);
    [LibraryImport(LibWk)] internal static partial IntPtr webkit_javascript_result_get_js_value(IntPtr result);

    // ---- settings / contexts / data ----
    [LibraryImport(LibWk)] internal static partial IntPtr webkit_settings_new();
    [LibraryImport(LibWk)] internal static partial nuint webkit_settings_get_type();
    [LibraryImport(LibWk)] internal static partial void webkit_settings_set_hardware_acceleration_policy(IntPtr s, int policy);
    internal const int AccelOnDemand = 0, AccelAlways = 1, AccelNever = 2; // WebKitHardwareAccelerationPolicy
    [LibraryImport(LibWk, StringMarshalling = StringMarshalling.Utf8)] internal static partial void webkit_settings_set_user_agent(IntPtr s, string? ua);
    [LibraryImport(LibWk)] internal static partial IntPtr webkit_settings_get_user_agent(IntPtr s);
    [LibraryImport(LibWk)] internal static partial IntPtr webkit_web_context_new();
    [LibraryImport(LibWk)] internal static partial IntPtr webkit_web_context_new_ephemeral();
    [LibraryImport(LibWk)] internal static partial IntPtr webkit_web_context_new_with_website_data_manager(IntPtr mgr);
    [LibraryImport(LibWk)] internal static partial IntPtr webkit_web_context_get_website_data_manager(IntPtr ctx);
    [LibraryImport(LibWk)] internal static partial void webkit_web_context_set_spell_checking_enabled(IntPtr ctx, int enabled);
    [LibraryImport(LibWk)] internal static partial void webkit_web_context_set_spell_checking_languages(IntPtr ctx, IntPtr* langs);
    [LibraryImport(LibWk)] internal static partial IntPtr webkit_website_data_manager_get_cookie_manager(IntPtr mgr);
    [LibraryImport(LibWk, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void webkit_cookie_manager_set_persistent_storage(IntPtr m, string filename, int storage); // 0 text, 1 sqlite
    [LibraryImport(LibWk)] internal static partial nuint webkit_website_data_manager_get_type();

    // ---- cookies ----
    [LibraryImport(LibWk)] internal static partial void webkit_cookie_manager_get_all_cookies(IntPtr m, IntPtr cancellable, IntPtr cb, IntPtr data);
    [LibraryImport(LibWk)] internal static partial IntPtr webkit_cookie_manager_get_all_cookies_finish(IntPtr m, IntPtr res, out IntPtr error);
    [LibraryImport(LibWk, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void webkit_cookie_manager_get_cookies(IntPtr m, string uri, IntPtr cancellable, IntPtr cb, IntPtr data);
    [LibraryImport(LibWk)] internal static partial IntPtr webkit_cookie_manager_get_cookies_finish(IntPtr m, IntPtr res, out IntPtr error);
    [LibraryImport(LibWk)] internal static partial void webkit_cookie_manager_add_cookie(IntPtr m, IntPtr cookie, IntPtr cancellable, IntPtr cb, IntPtr data);
    [LibraryImport(LibWk)] internal static partial int webkit_cookie_manager_add_cookie_finish(IntPtr m, IntPtr res, out IntPtr error);
    [LibraryImport(LibWk)] internal static partial void webkit_cookie_manager_delete_cookie(IntPtr m, IntPtr cookie, IntPtr cancellable, IntPtr cb, IntPtr data);
    [LibraryImport(LibWk)] internal static partial int webkit_cookie_manager_delete_cookie_finish(IntPtr m, IntPtr res, out IntPtr error);

    // ---- policy / responses / downloads ----
    [LibraryImport(LibWk)] internal static partial void webkit_policy_decision_use(IntPtr d);
    [LibraryImport(LibWk)] internal static partial void webkit_policy_decision_ignore(IntPtr d);
    [LibraryImport(LibWk)] internal static partial void webkit_policy_decision_download(IntPtr d);
    [LibraryImport(LibWk)] internal static partial IntPtr webkit_navigation_policy_decision_get_navigation_action(IntPtr d);
    [LibraryImport(LibWk)] internal static partial IntPtr webkit_navigation_action_get_request(IntPtr a);
    [LibraryImport(LibWk)] internal static partial int webkit_navigation_action_get_navigation_type(IntPtr a);
    [LibraryImport(LibWk)] internal static partial int webkit_navigation_action_is_user_gesture(IntPtr a);
    [LibraryImport(LibWk)] internal static partial IntPtr webkit_response_policy_decision_get_response(IntPtr d);
    [LibraryImport(LibWk)] internal static partial IntPtr webkit_response_policy_decision_get_request(IntPtr d);
    [LibraryImport(LibWk)] internal static partial int webkit_response_policy_decision_is_mime_type_supported(IntPtr d);
    [LibraryImport(LibWk)] internal static partial IntPtr webkit_uri_request_get_uri(IntPtr r);
    [LibraryImport(LibWk)] internal static partial IntPtr webkit_uri_response_get_uri(IntPtr r);
    [LibraryImport(LibWk)] internal static partial IntPtr webkit_uri_response_get_mime_type(IntPtr r);
    [LibraryImport(LibWk)] internal static partial IntPtr webkit_uri_response_get_suggested_filename(IntPtr r);
    [LibraryImport(LibWk)] internal static partial ulong webkit_uri_response_get_content_length(IntPtr r);
    [LibraryImport(LibWk)] internal static partial IntPtr webkit_uri_response_get_http_headers(IntPtr r);
    [LibraryImport(LibWk, StringMarshalling = StringMarshalling.Utf8)] internal static partial void webkit_download_set_destination(IntPtr d, string uri);
    [LibraryImport(LibWk)] internal static partial void webkit_download_cancel(IntPtr d);
    [LibraryImport(LibWk)] internal static partial IntPtr webkit_download_get_request(IntPtr d);
    [LibraryImport(LibWk)] internal static partial IntPtr webkit_download_get_response(IntPtr d);
    [LibraryImport(LibWk)] internal static partial double webkit_download_get_estimated_progress(IntPtr d);
    [LibraryImport(LibWk)] internal static partial ulong webkit_download_get_received_data_length(IntPtr d);
    [LibraryImport(LibWk)] internal static partial IntPtr webkit_download_get_web_view(IntPtr d);

    // ---- permissions ----
    [LibraryImport(LibWk)] internal static partial void webkit_permission_request_allow(IntPtr r);
    [LibraryImport(LibWk)] internal static partial void webkit_permission_request_deny(IntPtr r);
    [LibraryImport(LibWk)] internal static partial nuint webkit_geolocation_permission_request_get_type();
    [LibraryImport(LibWk)] internal static partial nuint webkit_notification_permission_request_get_type();
    [LibraryImport(LibWk)] internal static partial IntPtr webkit_notification_get_title(IntPtr n);
    [LibraryImport(LibWk)] internal static partial IntPtr webkit_notification_get_body(IntPtr n);
    [LibraryImport(LibWk)] internal static partial void webkit_notification_clicked(IntPtr n);
    [LibraryImport(LibWk)] internal static partial void webkit_notification_close(IntPtr n);
    [LibraryImport(LibWk)] internal static partial nuint webkit_user_media_permission_request_get_type();
    [LibraryImport(LibWk)] internal static partial nuint webkit_clipboard_permission_request_get_type();
    [LibraryImport(LibWk)] internal static partial nuint webkit_device_info_permission_request_get_type();
    [LibraryImport(LibWk)] internal static partial nuint webkit_website_data_access_permission_request_get_type();
    [LibraryImport(LibWk)] internal static partial int webkit_user_media_permission_is_for_audio_device(IntPtr r);
    [LibraryImport(LibWk)] internal static partial int webkit_user_media_permission_is_for_video_device(IntPtr r);

    // ---- window properties (sized popups) ----
    [LibraryImport(LibWk)] internal static partial void webkit_window_properties_get_geometry(IntPtr p, int* rect);
    [LibraryImport(LibWk)] internal static partial int webkit_window_properties_get_toolbar_visible(IntPtr p);
    [LibraryImport(LibWk)] internal static partial int webkit_window_properties_get_menubar_visible(IntPtr p);
    [LibraryImport(LibWk)] internal static partial int webkit_window_properties_get_locationbar_visible(IntPtr p);
    [LibraryImport(LibWk)] internal static partial int webkit_window_properties_get_statusbar_visible(IntPtr p);
    [LibraryImport(LibWk)] internal static partial int webkit_window_properties_get_scrollbars_visible(IntPtr p);
    [LibraryImport(LibWk)] internal static partial int webkit_window_properties_get_resizable(IntPtr p);

    // ---- authentication ----
    [LibraryImport(LibWk)] internal static partial IntPtr webkit_authentication_request_get_host(IntPtr r);
    [LibraryImport(LibWk)] internal static partial IntPtr webkit_authentication_request_get_realm(IntPtr r);
    [LibraryImport(LibWk)] internal static partial uint webkit_authentication_request_get_port(IntPtr r);
    [LibraryImport(LibWk)] internal static partial int webkit_authentication_request_is_retry(IntPtr r);
    [LibraryImport(LibWk, StringMarshalling = StringMarshalling.Utf8)] internal static partial IntPtr webkit_credential_new(string user, string pass, int persistence);
    [LibraryImport(LibWk)] internal static partial void webkit_credential_free(IntPtr c);
    [LibraryImport(LibWk)] internal static partial void webkit_authentication_request_authenticate(IntPtr r, IntPtr credential);
    [LibraryImport(LibWk)] internal static partial void webkit_authentication_request_cancel(IntPtr r);

    // ---- version ----
    [LibraryImport(LibWk)] internal static partial uint webkit_get_major_version();
    [LibraryImport(LibWk)] internal static partial uint webkit_get_minor_version();
    [LibraryImport(LibWk)] internal static partial uint webkit_get_micro_version();

    // ---- JavaScriptCore ----
    [LibraryImport(LibJsc)] internal static partial int jsc_value_is_string(IntPtr v);
    [LibraryImport(LibJsc)] internal static partial IntPtr jsc_value_to_string(IntPtr v);
    [LibraryImport(LibJsc)] internal static partial int jsc_value_is_undefined(IntPtr v);
    [LibraryImport(LibJsc)] internal static partial int jsc_value_is_null(IntPtr v);
    [LibraryImport(LibJsc)] internal static partial IntPtr jsc_value_to_json(IntPtr v, uint indent);

    // ---- helpers ----

    /// <summary>Whether this WebKitGTK is at least <paramref name="major"/>.<paramref name="minor"/>.</summary>
    internal static bool AtLeast(uint major, uint minor)
    {
        var a = webkit_get_major_version(); var b = webkit_get_minor_version();
        return a > major || (a == major && b >= minor);
    }

    private static readonly bool _hasEvalJs = Has("webkit_web_view_evaluate_javascript");

    internal static bool Has(string symbol)
    {
        try { return NativeLibrary.TryGetExport(NativeLibrary.Load(LibWk), symbol, out _); }
        catch { return false; }
    }

    /// <summary>Runs <paramref name="js"/> in the page's main world and forgets the result (the BrAIn eval
    /// channel reports back through the script-message handler instead). evaluateJavaScript is exempt from the
    /// page's Content-Security-Policy, exactly like WKWebView's.</summary>
    internal static void EvalFireAndForget(IntPtr view, string js)
    {
        if (view == IntPtr.Zero) return;
        if (_hasEvalJs) webkit_web_view_evaluate_javascript(view, js, -1, null, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        else webkit_web_view_run_javascript(view, js, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Opaque/transparent colour for <c>webkit_web_view_set_background_color</c>.</summary>
    internal static void SetBackground(IntPtr view, double r, double g, double b, double a)
    {
        var c = stackalloc double[4];
        c[0] = r; c[1] = g; c[2] = b; c[3] = a;
        webkit_web_view_set_background_color(view, c);
    }

    private static string? _version;
    internal static string Version => _version ??= $"{webkit_get_major_version()}.{webkit_get_minor_version()}.{webkit_get_micro_version()}";
}

/// <summary>libsoup 3 cookies (what WebKitGTK 4.1's cookie manager traffics in).</summary>
internal static unsafe partial class Soup
{
    private const string LibSoup = "libsoup-3.0.so.0";

    [LibraryImport(LibSoup, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr soup_cookie_new(string name, string value, string domain, string path, int maxAge);
    [LibraryImport(LibSoup)] internal static partial void soup_cookie_free(IntPtr c);
    [LibraryImport(LibSoup)] internal static partial IntPtr soup_cookie_get_name(IntPtr c);
    [LibraryImport(LibSoup)] internal static partial IntPtr soup_cookie_get_value(IntPtr c);
    [LibraryImport(LibSoup)] internal static partial IntPtr soup_cookie_get_domain(IntPtr c);
    [LibraryImport(LibSoup)] internal static partial IntPtr soup_cookie_get_path(IntPtr c);
    [LibraryImport(LibSoup)] internal static partial IntPtr soup_cookie_get_expires(IntPtr c);
    [LibraryImport(LibSoup)] internal static partial int soup_cookie_get_secure(IntPtr c);
    [LibraryImport(LibSoup)] internal static partial int soup_cookie_get_http_only(IntPtr c);
    [LibraryImport(LibSoup)] internal static partial void soup_cookie_set_secure(IntPtr c, int secure);
    [LibraryImport(LibSoup)] internal static partial void soup_cookie_set_http_only(IntPtr c, int httpOnly);
    [LibraryImport(LibSoup)] internal static partial void soup_cookie_set_expires(IntPtr c, IntPtr dateTime);
    [LibraryImport(LibSoup)] internal static partial IntPtr soup_cookie_copy(IntPtr c);
    [LibraryImport(LibSoup, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr soup_message_headers_get_one(IntPtr headers, string name);
}

/// <summary>cairo: snapshots arrive as image surfaces; scrolling screenshots are painted into one tall surface.</summary>
internal static unsafe partial class Cairo
{
    private const string LibCairo = "libcairo.so.2";

    internal const int FormatArgb32 = 0, FormatRgb24 = 1;

    [LibraryImport(LibCairo)] internal static partial IntPtr cairo_image_surface_create(int format, int width, int height);
    [LibraryImport(LibCairo)] internal static partial IntPtr cairo_create(IntPtr surface);
    [LibraryImport(LibCairo)] internal static partial void cairo_destroy(IntPtr cr);
    [LibraryImport(LibCairo)] internal static partial void cairo_surface_destroy(IntPtr s);
    [LibraryImport(LibCairo)] internal static partial IntPtr cairo_surface_reference(IntPtr s);
    [LibraryImport(LibCairo)] internal static partial int cairo_surface_status(IntPtr s);
    [LibraryImport(LibCairo)] internal static partial void cairo_surface_flush(IntPtr s);
    [LibraryImport(LibCairo)] internal static partial int cairo_image_surface_get_width(IntPtr s);
    [LibraryImport(LibCairo)] internal static partial int cairo_image_surface_get_height(IntPtr s);
    [LibraryImport(LibCairo)] internal static partial void cairo_set_source_surface(IntPtr cr, IntPtr s, double x, double y);
    [LibraryImport(LibCairo)] internal static partial void cairo_set_source_rgb(IntPtr cr, double r, double g, double b);
    [LibraryImport(LibCairo)] internal static partial void cairo_paint(IntPtr cr);
    [LibraryImport(LibCairo)] internal static partial void cairo_rectangle(IntPtr cr, double x, double y, double w, double h);
    [LibraryImport(LibCairo)] internal static partial void cairo_fill(IntPtr cr);
    [LibraryImport(LibCairo)] internal static partial void cairo_surface_get_device_scale(IntPtr s, out double x, out double y);
    [LibraryImport(LibCairo)] internal static partial int cairo_surface_write_to_png_stream(IntPtr s, IntPtr writeFunc, IntPtr closure);

    private static MemoryStream? _png;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static int PngWrite(IntPtr closure, byte* data, uint length)
    {
        try { _png!.Write(new ReadOnlySpan<byte>(data, (int)length)); return 0; } // CAIRO_STATUS_SUCCESS
        catch { return 11; }                                                       // CAIRO_STATUS_WRITE_ERROR
    }

    private static readonly object _pngLock = new();

    /// <summary>Encodes an image surface as PNG (null on failure).</summary>
    internal static byte[]? ToPng(IntPtr surface)
    {
        if (surface == IntPtr.Zero) return null;
        lock (_pngLock)
        {
            _png = new MemoryStream();
            try
            {
                var rc = cairo_surface_write_to_png_stream(surface,
                    (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, byte*, uint, int>)&PngWrite, IntPtr.Zero);
                return rc == 0 && _png.Length > 0 ? _png.ToArray() : null;
            }
            finally { _png = null; }
        }
    }
}

/// <summary>gdk-pixbuf: decoding pictures for the clipboard.</summary>
internal static unsafe partial class Pixbuf
{
    private const string LibPix = "libgdk_pixbuf-2.0.so.0";

    [LibraryImport(LibPix, StringMarshalling = StringMarshalling.Utf8)] internal static partial IntPtr gdk_pixbuf_loader_new_with_mime_type(string mime, out IntPtr error);
    [LibraryImport(LibPix)] internal static partial IntPtr gdk_pixbuf_loader_new();
    [LibraryImport(LibPix)] internal static partial int gdk_pixbuf_loader_write(IntPtr loader, byte* buf, nuint count, out IntPtr error);
    [LibraryImport(LibPix)] internal static partial int gdk_pixbuf_loader_close(IntPtr loader, out IntPtr error);
    [LibraryImport(LibPix)] internal static partial IntPtr gdk_pixbuf_loader_get_pixbuf(IntPtr loader);
    [LibraryImport(LibPix, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int gdk_pixbuf_save_to_bufferv(IntPtr pixbuf, out IntPtr buffer, out nuint size, string type, IntPtr keys, IntPtr values, out IntPtr error);

    /// <summary>Encodes a pixbuf as JPEG at the given quality (1-100); null on failure. Alpha is dropped.</summary>
    internal static byte[]? ToJpeg(IntPtr pixbuf, int quality)
    {
        var keys = Marshal.AllocHGlobal(2 * IntPtr.Size);
        var vals = Marshal.AllocHGlobal(2 * IntPtr.Size);
        var k = Marshal.StringToCoTaskMemUTF8("quality");
        var v = Marshal.StringToCoTaskMemUTF8(quality.ToString(System.Globalization.CultureInfo.InvariantCulture));
        try
        {
            Marshal.WriteIntPtr(keys, 0, k); Marshal.WriteIntPtr(keys, IntPtr.Size, IntPtr.Zero);
            Marshal.WriteIntPtr(vals, 0, v); Marshal.WriteIntPtr(vals, IntPtr.Size, IntPtr.Zero);
            if (gdk_pixbuf_save_to_bufferv(pixbuf, out var buf, out var size, "jpeg", keys, vals, out var err) == 0)
            {
                GLib.TakeError(err);
                return null;
            }
            try
            {
                var bytes = new byte[(int)size];
                Marshal.Copy(buf, bytes, 0, bytes.Length);
                return bytes;
            }
            finally { GLib.g_free(buf); }
        }
        finally
        {
            Marshal.FreeCoTaskMem(k); Marshal.FreeCoTaskMem(v);
            Marshal.FreeHGlobal(keys); Marshal.FreeHGlobal(vals);
        }
    }

    /// <summary>Encodes a pixbuf as PNG (null on failure).</summary>
    internal static byte[]? ToPng(IntPtr pixbuf)
    {
        if (gdk_pixbuf_save_to_bufferv(pixbuf, out var buf, out var size, "png", IntPtr.Zero, IntPtr.Zero, out var err) == 0)
        {
            GLib.TakeError(err);
            return null;
        }
        try
        {
            var bytes = new byte[(int)size];
            Marshal.Copy(buf, bytes, 0, bytes.Length);
            return bytes;
        }
        finally { GLib.g_free(buf); }
    }
    [LibraryImport(LibPix)] internal static partial int gdk_pixbuf_get_width(IntPtr pixbuf);
    [LibraryImport(LibPix)] internal static partial int gdk_pixbuf_get_height(IntPtr pixbuf);

    /// <summary>Decodes picture bytes; the caller owns the returned pixbuf (g_object_unref). Zero on failure.</summary>
    internal static IntPtr Decode(byte[] data, string? mime = null)
    {
        IntPtr loader, err;
        loader = mime is null ? gdk_pixbuf_loader_new() : gdk_pixbuf_loader_new_with_mime_type(mime, out err);
        if (loader == IntPtr.Zero) loader = gdk_pixbuf_loader_new();
        if (loader == IntPtr.Zero) return IntPtr.Zero;
        try
        {
            fixed (byte* p = data)
                if (gdk_pixbuf_loader_write(loader, p, (nuint)data.Length, out err) == 0) { GLib.TakeError(err); return IntPtr.Zero; }
            if (gdk_pixbuf_loader_close(loader, out err) == 0) { GLib.TakeError(err); return IntPtr.Zero; }
            var pb = gdk_pixbuf_loader_get_pixbuf(loader); // owned by the loader
            return pb == IntPtr.Zero ? IntPtr.Zero : GLib.g_object_ref(pb);
        }
        finally { GLib.g_object_unref(loader); }
    }
}
