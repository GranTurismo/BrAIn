using System.Runtime.InteropServices;

namespace BrAIn.Browser.Linux;

/// <summary>
/// Download support and navigation policy (Linux counterpart of <c>Mac/WkDownloads.cs</c>).
///
/// Flow: the view's "decide-policy" answers .download for attachment or unrenderable responses → WebKit converts
/// the load to a WebKitDownload and announces it on the session (<c>download-started</c>) → we pick a uniquified
/// path in the downloads folder and report progress / finish / failure to <see cref="Downloads"/>.
/// Also reloads a view whose web-content process died, and logs navigation failures.
/// Everything runs on the GTK main thread.
/// </summary>
internal static unsafe class LinuxDownloads
{
    private static Downloads? _mgr;
    private static BrowserController? _ctl;
    private static readonly Dictionary<IntPtr, Downloads.Item> _live = new();
    private static readonly HashSet<IntPtr> _hooked = new(); // sessions whose download-started we listen to
    private static string? _retryUrl;
    private static long _retryAt;

    /// <summary>Hands the controller over (its download list and data folder). Idempotent.</summary>
    internal static void Configure(BrowserController ctl)
    {
        _ctl = ctl;
        _mgr = ctl.Downloads;
    }

    /// <summary>Wires one view: response policy, crash recovery, failure log, and — once per session — the
    /// session's download announcements.</summary>
    internal static void Connect(IntPtr view)
    {
        GLib.Connect(view, "decide-policy", (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, IntPtr, int>)&OnDecidePolicy);
        GLib.Connect(view, "web-process-terminated", (delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr, void>)&OnProcessTerminated);
        GLib.Connect(view, "load-failed", (delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr, IntPtr, IntPtr, int>)&OnLoadFailed);

        var ctx = WebKit.webkit_web_view_get_context(view);
        if (ctx != IntPtr.Zero && _hooked.Add(ctx))
            GLib.Connect(ctx, "download-started", (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&OnDownloadStarted);
    }

    // ------------------------------------------------ policy

    /// Response policy: download anything marked for attachment or that WebKit can't render; everything else
    /// is used as a normal page load. A web page must never turn into a download: a transient "can't show"
    /// answer for an HTML main document would leave a blank view plus a stray .html in Downloads.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static int OnDecidePolicy(IntPtr view, IntPtr decision, int type, IntPtr data)
    {
        try
        {
            if (type != WebKit.PolicyResponse) return 0; // navigation / new-window actions: engine default (allow)
            var resp = WebKit.webkit_response_policy_decision_get_response(decision);
            var mime = GLib.Str(WebKit.webkit_uri_response_get_mime_type(resp));
            var canShow = WebKit.webkit_response_policy_decision_is_mime_type_supported(decision) != 0;
            var forAttach = IsAttachmentHeader(resp);
            var isHtml = mime is not null &&
                (mime.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) ||
                 mime.StartsWith("application/xhtml", StringComparison.OrdinalIgnoreCase));
            // Only download what we positively know is a non-page file. A response with no MIME type at all
            // is NOT a file — let WebKit handle it as a normal page load.
            var download = forAttach || (!canShow && mime is not null && !isHtml);
            if (download || !canShow)
                DiagLog($"response {(download ? "DOWNLOAD" : "kept-as-page")} mime={mime ?? "?"} canShow={canShow} attach={forAttach} url={GLib.Str(WebKit.webkit_uri_response_get_uri(resp))}");
            if (!download) return 0;
            WebKit.webkit_policy_decision_download(decision);
            return 1;
        }
        catch { return 0; }
    }

    private static bool IsAttachmentHeader(IntPtr resp)
    {
        if (resp == IntPtr.Zero) return false;
        var headers = WebKit.webkit_uri_response_get_http_headers(resp);
        if (headers == IntPtr.Zero) return false;
        var v = GLib.Str(Soup.soup_message_headers_get_one(headers, "Content-Disposition"));
        return v is not null && v.TrimStart().StartsWith("attachment", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Appends a line to download-decisions.log in the app data folder (only written when a response is
    /// turned into a download, or an HTML page is rescued from becoming one) so odd downloads can be diagnosed.</summary>
    private static void DiagLog(string line)
    {
        try
        {
            var dir = _ctl?.StoreDir;
            Console.Error.WriteLine("[BrAIn] " + line);
            if (string.IsNullOrEmpty(dir)) return;
            var path = Path.Combine(dir, "download-decisions.log");
            if (File.Exists(path) && new FileInfo(path).Length > 200_000) File.Delete(path);
            File.AppendAllText(path, DateTime.Now.ToString("s") + " " + line + "\n");
        }
        catch { }
    }

    /// <summary>The web-content process died (memory pressure, crash): the view stays blank until something
    /// reloads it — which is exactly what a manual reload does. Reload it ourselves.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static void OnProcessTerminated(IntPtr view, int reason, IntPtr data)
    {
        try
        {
            DiagLog($"web content process terminated (reason {reason}) — reloading");
            WebKit.webkit_web_view_reload(view);
        }
        catch { }
    }

    /// <summary>Navigation failures were invisible before. Log them (not cancels).</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static int OnLoadFailed(IntPtr view, int loadEvent, IntPtr failingUri, IntPtr error, IntPtr data)
    {
        try
        {
            // GError { GQuark domain; gint code; gchar *message }
            var code = error == IntPtr.Zero ? 0 : Marshal.ReadInt32(error, 4);
            var msg = error == IntPtr.Zero ? "" : GLib.Str(Marshal.ReadIntPtr(error, 8)) ?? "";
            var url = GLib.Str(failingUri) ?? "";
            if (code == 302) return 0; // WEBKIT_NETWORK_ERROR_CANCELLED: a newer navigation replaced it — normal
            DiagLog($"navigation failed (event {loadEvent}) code={code} \"{msg}\" url={url}");
            // An interrupted load on the FIRST navigation after launch can leave the view blank until the user
            // reloads. Re-issue a failed http(s) load once.
            if (loadEvent == WebKit.LoadStarted && code == 299 /* WEBKIT_NETWORK_ERROR_FAILED */ &&
                (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                var now = Environment.TickCount64;
                if (_retryUrl == url && now - _retryAt < 15000) return 0; // already retried
                _retryUrl = url; _retryAt = now;
                DiagLog("retrying interrupted load once: " + url);
                LoadLater.Run(_ctl, view, url);
            }
        }
        catch { }
        return 0;
    }

    // ------------------------------------------------ downloads

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static void OnDownloadStarted(IntPtr ctx, IntPtr download, IntPtr data)
    {
        try
        {
            if (_mgr is null) return;
            GLib.g_object_ref(download);
            var req = WebKit.webkit_download_get_request(download);
            var item = _mgr.Begin(GLib.Str(req == IntPtr.Zero ? IntPtr.Zero : WebKit.webkit_uri_request_get_uri(req)) ?? "");
            item.Wk = download;
            lock (_live) _live[download] = item;
            GLib.Connect(download, "decide-destination", (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int>)&OnDecideDestination);
            GLib.Connect(download, "received-data", (delegate* unmanaged[Cdecl]<IntPtr, ulong, IntPtr, void>)&OnReceivedData);
            GLib.Connect(download, "finished", (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&OnFinished);
            GLib.Connect(download, "failed", (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&OnFailed);
        }
        catch { }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static int OnDecideDestination(IntPtr download, IntPtr suggested, IntPtr data)
    {
        try
        {
            var name = GLib.Str(suggested);
            Downloads.Item? it;
            lock (_live) _live.TryGetValue(download, out it);
            var path = it is not null
                ? _mgr!.DestinationFor(it, name)
                : Path.Combine(_mgr!.Dir, Downloads.Sanitize(name) is { Length: > 0 } s ? s : "download");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            WebKit.webkit_download_set_destination(download, new Uri(path).AbsoluteUri);
        }
        catch
        {
            // A download with no destination is cancelled — give it *something*.
            try { WebKit.webkit_download_set_destination(download, new Uri(Path.Combine(_mgr!.Dir, "download")).AbsoluteUri); } catch { }
        }
        return 1; // handled
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static void OnReceivedData(IntPtr download, ulong length, IntPtr data)
    {
        try
        {
            Downloads.Item? it;
            lock (_live) _live.TryGetValue(download, out it);
            if (it is null || it.State != 0) return;
            var resp = WebKit.webkit_download_get_response(download);
            var total = resp == IntPtr.Zero ? 0UL : WebKit.webkit_uri_response_get_content_length(resp);
            _mgr!.Progress(it, (long)WebKit.webkit_download_get_received_data_length(download), total > 0 ? (long)total : -1);
        }
        catch { }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static void OnFinished(IntPtr download, IntPtr data)
    {
        try
        {
            Downloads.Item? it;
            lock (_live) { _live.TryGetValue(download, out it); _live.Remove(download); }
            if (it is not null && it.State == 0) _mgr!.Finish(it);
        }
        catch { }
        GLib.g_object_unref(download);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static void OnFailed(IntPtr download, IntPtr error, IntPtr data)
    {
        try
        {
            Downloads.Item? it;
            lock (_live) { _live.TryGetValue(download, out it); _live.Remove(download); }
            if (it is not null)
            {
                var code = error == IntPtr.Zero ? 0 : Marshal.ReadInt32(error, 4);
                var msg = error == IntPtr.Zero ? null : GLib.Str(Marshal.ReadIntPtr(error, 8));
                // WEBKIT_DOWNLOAD_ERROR_CANCELLED_BY_USER == 400 → user cancel, not a failure.
                if (code == 400) _mgr!.Cancel(it.Id); else _mgr!.Fail(it, msg ?? "download failed");
            }
        }
        catch { }
        // "finished" follows "failed" and releases the extra reference
    }

    /// <summary>Stops an in-flight download (user pressed ✕).</summary>
    internal static void CancelDownload(IntPtr download)
    {
        var c = _ctl;
        try
        {
            if (c is not null) c.InvokeOnUi(() => WebKit.webkit_download_cancel(download));
            else WebKit.webkit_download_cancel(download);
        }
        catch { }
    }
}

/// <summary>Re-issues a failed load shortly after (kept out of the unsafe class so it can await).</summary>
internal static class LoadLater
{
    internal static void Run(BrowserController? ctl, IntPtr view, string url)
        => _ = Task.Run(async () =>
        {
            await Task.Delay(200);
            try { ctl?.InvokeOnUi(() => { if (LinuxTabs.Has(view)) WebKit.webkit_web_view_load_uri(view, url); }); } catch { }
        });
}
