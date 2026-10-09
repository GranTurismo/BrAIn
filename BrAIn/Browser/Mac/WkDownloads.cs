using System.Runtime.InteropServices;

namespace BrAIn.Browser.Mac;

/// <summary>
/// WKWebView download support, grafted onto the existing navigation delegate
/// class at runtime — Photino's NavigationDelegate only implements the
/// auth-challenge method, so class_addMethod adds our download methods to it
/// without touching anything else. If a future Photino grows these methods,
/// the originals chain through ours.
///
/// Flow: decidePolicyForNavigationResponse answers .download for attachment
/// or unrenderable responses → WebKit converts the load to a WKDownload and
/// calls didBecomeDownload → we set a WKDownloadDelegate (our own class) →
/// it picks a uniquified path in ~/Downloads and reports progress/finish.
///
/// Everything runs on the main thread (WebKit delegate callbacks) except the
/// progress poll, which reads NSProgress properties via InvokeOnUi.
/// </summary>
internal static partial class WkDownloads
{
    private const string LibObjC = "/usr/lib/libobjc.A.dylib";

    [LibraryImport(LibObjC)]
    private static partial IntPtr objc_allocateClassPair(IntPtr superclass,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, nint extraBytes);

    [LibraryImport(LibObjC)]
    private static partial void objc_registerClassPair(IntPtr cls);

    [LibraryImport(LibObjC, EntryPoint = "class_addMethod")]
    private static partial byte ClassAddMethod(IntPtr cls, IntPtr sel, IntPtr imp,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string types);

    [LibraryImport(LibObjC)]
    private static partial IntPtr class_getMethodImplementation(IntPtr cls, IntPtr sel);

    // IMP signatures — (id self, SEL _cmd, ...args)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DecRespImp(IntPtr self, IntPtr sel, IntPtr wv, IntPtr resp, IntPtr handler);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void BecameImp(IntPtr self, IntPtr sel, IntPtr wv, IntPtr mid, IntPtr dl);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DestImp(IntPtr self, IntPtr sel, IntPtr dl, IntPtr resp, IntPtr name, IntPtr handler);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void VoidDlImp(IntPtr self, IntPtr sel, IntPtr dl);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FailImp(IntPtr self, IntPtr sel, IntPtr dl, IntPtr err, IntPtr resume);

    // ObjC block invoke pointers: block layout has `invoke` at offset 16.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void BlockPolicy(IntPtr block, nint policy);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void BlockUrl(IntPtr block, IntPtr url);

    // Rooted forever — the IMPs must never be collected.
    private static readonly DecRespImp _decResp = OnDecideResponse;
    private static readonly BecameImp _became = OnDidBecomeDownload;
    private static readonly DestImp _dest = OnDecideDestination;
    private static readonly VoidDlImp _didFinish = OnDownloadFinish;
    private static readonly FailImp _didFail = OnDownloadFail;

    private static readonly IntPtr _selDecResp = ObjC.Sel("webView:decidePolicyForNavigationResponse:decisionHandler:");
    private static readonly IntPtr _selBecameR = ObjC.Sel("webView:navigationResponse:didBecomeDownload:");
    private static readonly IntPtr _selBecameA = ObjC.Sel("webView:navigationAction:didBecomeDownload:");

    private static Downloads? _mgr;
    private static BrowserController? _ctl;
    private static readonly Dictionary<IntPtr, Downloads.Item> _live = new();
    private static readonly HashSet<IntPtr> _patched = new();
    private static IntPtr _dlDelegate;   // shared WKDownloadDelegate instance
    private static IntPtr _origDecResp;  // pre-existing IMP to chain to, if any
    private static IntPtr _origBecameR;
    private static IntPtr _origBecameA;
    private static Timer? _poll;

    /// <summary>Called once per webview (driver Bind). Patches the current
    /// navigation delegate's class in place; installs a standalone delegate
    /// only if the webview has none.</summary>
    internal static void Install(IntPtr webview, BrowserController ctl)
    {
        _ctl = ctl;
        _mgr = ctl.Downloads;

        // The shared WKDownloadDelegate object (weak property — keep rooted).
        if (_dlDelegate == IntPtr.Zero)
            _dlDelegate = NewDlDelegate();

        var cur = ObjC.Send(webview, ObjC.Sel("navigationDelegate"));
        if (cur != IntPtr.Zero)
        {
            var cls = ObjC.Send(cur, ObjC.Sel("class"));
            PatchNavClass(cls);
            // WKWebView snapshots respondsToSelector into its
            // _navigationDelegateMethods flags at setNavigationDelegate:
            // time — methods added to the class afterwards are invisible
            // until the delegate is re-assigned.
            ObjC.Send(webview, ObjC.Sel("setNavigationDelegate:"), cur);
        }
        else
        {
            // No delegate at all — install a standalone one.
            var cls = NewNavClass();
            var inst = ObjC.Send(ObjC.Send(cls, ObjC.Sel("alloc")), ObjC.Sel("init"));
            ObjC.Send(webview, ObjC.Sel("setNavigationDelegate:"), inst);
            ObjC.Send(inst, ObjC.Sel("retain")); // navigationDelegate is weak
        }
    }

    /// <summary>Adds our methods to a navigation-delegate class, chaining to
    /// original IMPs where they exist. Idempotent per class.</summary>
    private static void PatchNavClass(IntPtr cls)
    {
        if (cls == IntPtr.Zero) return;
        lock (_patched)
        {
            if (!_patched.Add(cls)) return;
        }

        _origDecResp = AddOrGet(cls, _selDecResp,
            Marshal.GetFunctionPointerForDelegate(_decResp), "v@:@@?");
        _origBecameR = AddOrGet(cls, _selBecameR,
            Marshal.GetFunctionPointerForDelegate(_became), "v@:@@@");
        _origBecameA = AddOrGet(cls, _selBecameA,
            Marshal.GetFunctionPointerForDelegate(_became), "v@:@@@");
    }

    /// <summary>class_addMethod; returns IntPtr.Zero on success, or the
    /// pre-existing IMP when the class already had the selector.</summary>
    private static IntPtr AddOrGet(IntPtr cls, IntPtr sel, IntPtr imp, string types)
    {
        return ClassAddMethod(cls, sel, imp, types) != 0
            ? IntPtr.Zero
            : class_getMethodImplementation(cls, sel);
    }

    private static IntPtr NewNavClass()
    {
        var cls = objc_allocateClassPair(ObjC.GetClass("NSObject"), "BrAInNavDl", 0);
        ClassAddMethod(cls, _selDecResp, Marshal.GetFunctionPointerForDelegate(_decResp), "v@:@@?");
        ClassAddMethod(cls, _selBecameR, Marshal.GetFunctionPointerForDelegate(_became), "v@:@@@");
        ClassAddMethod(cls, _selBecameA, Marshal.GetFunctionPointerForDelegate(_became), "v@:@@@");
        objc_registerClassPair(cls);
        return cls;
    }

    private static IntPtr NewDlDelegate()
    {
        var cls = objc_allocateClassPair(ObjC.GetClass("NSObject"), "BrAInDlDl", 0);
        ClassAddMethod(cls, ObjC.Sel("download:decideDestinationUsingResponse:suggestedFilename:completionHandler:"),
            Marshal.GetFunctionPointerForDelegate(_dest), "v@:@@@?");
        ClassAddMethod(cls, ObjC.Sel("downloadDidFinish:"),
            Marshal.GetFunctionPointerForDelegate(_didFinish), "v@:@");
        ClassAddMethod(cls, ObjC.Sel("download:didFailWithError:resumeData:"),
            Marshal.GetFunctionPointerForDelegate(_didFail), "v@:@@@");
        objc_registerClassPair(cls);
        var inst = ObjC.Send(ObjC.Send(cls, ObjC.Sel("alloc")), ObjC.Sel("init"));
        return ObjC.Send(inst, ObjC.Sel("retain"));
    }

    // ------------------------------------------------ delegate IMPs

    /// Response policy: download anything marked for attachment or that
    /// WebKit can't render; everything else defers to the original delegate
    /// (or plain allow when there is none).
    private static void OnDecideResponse(IntPtr self, IntPtr sel, IntPtr wv, IntPtr resp, IntPtr handler)
    {
        var download = false;
        try
        {
            var forAttach = BoolProp(resp, "isForAttachment") || BoolProp(resp, "forAttachment");
            var canShow = BoolProp(resp, "canShowMIMEType");
            download = forAttach || !canShow;
        }
        catch { }

        if (!download && _origDecResp != IntPtr.Zero)
        {
            Marshal.GetDelegateForFunctionPointer<DecRespImp>(_origDecResp)(self, sel, wv, resp, handler);
            return;
        }
        CallBlock(handler, download ? 2 : 1); // 2 = Download, 1 = Allow
    }

    private static void OnDidBecomeDownload(IntPtr self, IntPtr sel, IntPtr wv, IntPtr mid, IntPtr dl)
    {
        // If a previous delegate implementation exists, let it run first —
        // ours then replaces the download delegate anyway.
        var orig = sel == _selBecameR ? _origBecameR : _origBecameA;
        if (orig != IntPtr.Zero)
            try { Marshal.GetDelegateForFunctionPointer<BecameImp>(orig)(self, sel, wv, mid, dl); } catch { }

        try
        {
            ObjC.Send(dl, ObjC.Sel("retain"));
            ObjC.Send(dl, ObjC.Sel("setDelegate:"), _dlDelegate);
            var item = _mgr!.Begin(ExtractUrl(mid));
            item.Wk = dl;
            // Expected size, when the response carries one.
            var len = IntProp(mid, "response", "expectedContentLength");
            if (len > 0) item.Total = len;
            lock (_live) _live[dl] = item;
            EnsurePoll();
        }
        catch { }
    }

    private static void OnDecideDestination(IntPtr self, IntPtr sel, IntPtr dl, IntPtr resp, IntPtr name, IntPtr handler)
    {
        try
        {
            var suggested = ObjC.NsStringToString(name);
            Downloads.Item? it;
            lock (_live) _live.TryGetValue(dl, out it);
            var path = it is not null
                ? _mgr!.DestinationFor(it, suggested)
                : System.IO.Path.Combine(_mgr!.Dir, Downloads.Sanitize(suggested) is { Length: > 0 } s ? s : "download");
            var url = ObjC.Send(ObjC.GetClass("NSURL"), ObjC.Sel("fileURLWithPath:"), ObjC.NsString(path));
            CallBlockUrl(handler, url);
        }
        catch
        {
            // A download with no destination handler is killed — give it *something*.
            try
            {
                var url = ObjC.Send(ObjC.GetClass("NSURL"), ObjC.Sel("fileURLWithPath:"),
                    ObjC.NsString(System.IO.Path.Combine(_mgr!.Dir, "download")));
                CallBlockUrl(handler, url);
            }
            catch { CallBlockUrl(handler, IntPtr.Zero); }
        }
    }

    private static void OnDownloadFinish(IntPtr self, IntPtr sel, IntPtr dl)
    {
        Downloads.Item? it;
        lock (_live) { _live.TryGetValue(dl, out it); _live.Remove(dl); }
        if (it is not null) _mgr!.Finish(it);
        ObjC.Send(dl, ObjC.Sel("release"));
    }

    private static void OnDownloadFail(IntPtr self, IntPtr sel, IntPtr dl, IntPtr err, IntPtr resume)
    {
        Downloads.Item? it;
        lock (_live) { _live.TryGetValue(dl, out it); _live.Remove(dl); }
        if (it is not null)
        {
            var msg = err == IntPtr.Zero ? null
                : ObjC.NsStringToString(ObjC.Send(err, ObjC.Sel("localizedDescription")));
            // NSURLErrorCancelled == -999 → user cancel, not a failure.
            var code = err == IntPtr.Zero ? 0L : (long)ObjC.SendNInt(err, ObjC.Sel("code"));
            if (code == -999) _mgr!.Cancel(it.Id); else _mgr!.Fail(it, msg ?? "download failed");
        }
        ObjC.Send(dl, ObjC.Sel("release"));
    }

    /// <summary>Stops an in-flight WKDownload (user pressed ✕).</summary>
    internal static void CancelDownload(IntPtr dl)
    {
        var c = _ctl;
        try
        {
            if (c is not null)
                c.InvokeOnUi(() => ObjC.Send(dl, ObjC.Sel("cancel:"), IntPtr.Zero));
            else
                ObjC.Send(dl, ObjC.Sel("cancel:"), IntPtr.Zero);
        }
        catch { }
    }

    // ------------------------------------------------ helpers

    private static bool BoolProp(IntPtr obj, string getter)
    {
        var s = ObjC.Sel(getter);
        if (ObjC.SendBool(obj, ObjC.Sel("respondsToSelector:"), s) == 0) return false;
        return ((long)ObjC.Send(obj, s) & 0xFF) != 0;
    }

    private static long IntProp(IntPtr obj, string getter, string sub)
    {
        var s = ObjC.Sel(getter);
        if (ObjC.SendBool(obj, ObjC.Sel("respondsToSelector:"), s) == 0) return -1;
        var sub_ = ObjC.Send(obj, s);
        if (sub_ == IntPtr.Zero) return -1;
        return (long)ObjC.SendNInt(sub_, ObjC.Sel(sub));
    }

    /// Pulls the source URL out of a WKNavigationResponse (.response.URL)
    /// or WKNavigationAction (.request.URL).
    private static string ExtractUrl(IntPtr mid)
    {
        foreach (var g in new[] { "response", "request" })
        {
            var s = ObjC.Sel(g);
            if (ObjC.SendBool(mid, ObjC.Sel("respondsToSelector:"), s) == 0) continue;
            var inner = ObjC.Send(mid, s);
            if (inner == IntPtr.Zero) continue;
            var nsUrl = ObjC.Send(inner, ObjC.Sel("URL"));
            if (nsUrl == IntPtr.Zero) continue;
            var abs = ObjC.Send(nsUrl, ObjC.Sel("absoluteString"));
            var str = ObjC.NsStringToString(abs);
            if (!string.IsNullOrEmpty(str)) return str!;
        }
        return "";
    }

    /// ObjC block invoke: `invoke` fn pointer lives at offset 16 in the block.
    private static void CallBlock(IntPtr block, nint policy)
    {
        var invoke = Marshal.ReadIntPtr(block, 16);
        Marshal.GetDelegateForFunctionPointer<BlockPolicy>(invoke)(block, policy);
    }

    private static void CallBlockUrl(IntPtr block, IntPtr url)
    {
        var invoke = Marshal.ReadIntPtr(block, 16);
        Marshal.GetDelegateForFunctionPointer<BlockUrl>(invoke)(block, url);
    }

    /// <summary>NSProgress polling — WKDownload has no progress delegate
    /// callbacks, so read completedUnitCount/totalUnitCount (bytes for file
    /// downloads) a few times a second while anything is in flight.</summary>
    private static void EnsurePoll()
    {
        if (_poll != null) return;
        _poll = new Timer(_ =>
        {
            try
            {
                var any = false;
                Downloads.Item[] items;
                lock (_live) items = _live.Values.ToArray();
                foreach (var it in items)
                {
                    if (it.State != 0) continue;
                    any = true;
                    var (recv, total) = _ctl!.InvokeOnUi(() =>
                    {
                        var rts = ObjC.Sel("respondsToSelector:");
                        if (ObjC.SendBool(it.Wk, rts, ObjC.Sel("progress")) == 0) return (0L, -1L);
                        var p = ObjC.Send(it.Wk, ObjC.Sel("progress"));
                        if (p == IntPtr.Zero) return (0L, -1L);
                        var done = ObjC.SendBool(p, rts, ObjC.Sel("completedUnitCount")) != 0
                            ? (long)ObjC.SendNInt(p, ObjC.Sel("completedUnitCount")) : 0L;
                        var tot = ObjC.SendBool(p, rts, ObjC.Sel("totalUnitCount")) != 0
                            ? (long)ObjC.SendNInt(p, ObjC.Sel("totalUnitCount")) : -1L;
                        return (done, tot);
                    });
                    _mgr!.Progress(it, recv, total);
                }
                if (!any) { _poll?.Dispose(); _poll = null; }
            }
            catch { }
        }, null, 250, 300);
    }
}
