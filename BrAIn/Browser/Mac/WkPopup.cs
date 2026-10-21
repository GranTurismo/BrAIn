using System.Runtime.InteropServices;

namespace BrAIn.Browser.Mac;

/// <summary>Popup support for WKWebView: the engine only asks for a window via
/// -[WKUIDelegate webView:createWebViewWithConfiguration:forNavigationAction:windowFeatures:]
/// and Photino's stock delegate doesn't implement it, so window.open and
/// target=_blank navigations were dropped entirely.
///
/// BrAInUiDelegate wraps the web view's original delegate and adds:
///   - plain window.open / target=_blank  → OnNewTab(url) (becomes a browser tab), returns nil
///   - sized popups (OAuth/payment flows that carry width/height) → a real NSWindow
///     with a WKWebView built from the supplied configuration, so window.opener
///     stays wired and the flow can post back to its parent;
///   - webViewDidClose → closes a popup window it created;
///   - every other selector → forwardingTargetForSelector: to the original delegate
///     (JS alerts, permission prompts, file pickers keep working).
///
/// Install once on the bootstrap view; per-tab views copy it with the configuration.
/// </summary>
internal static partial class WkPopup
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

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr CreateImp(IntPtr self, IntPtr sel, IntPtr webView, IntPtr cfg, IntPtr action, IntPtr feats);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr FwdImp(IntPtr self, IntPtr sel, IntPtr aSel);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte RespImp(IntPtr self, IntPtr sel, IntPtr aSel);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void CloseImp(IntPtr self, IntPtr sel, IntPtr webView);

    private static readonly CreateImp _create = OnCreate;
    private static readonly FwdImp _fwd = OnForward;
    private static readonly RespImp _resp = OnResp;
    private static readonly CloseImp _close = OnClose;

    /// <summary>A plain new-window request wants a tab. Main-thread only.</summary>
    internal static Action<string>? OnNewTab;
    internal static Action<string>? Log;

    private static IntPtr _cls;
    private static IntPtr _self;
    private static IntPtr _orig; // delegate we forward everything else to
    private static readonly Dictionary<IntPtr, IntPtr> _popupWindow = new(); // webview → nswindow

    private static IntPtr MakeSelf()
    {
        if (_cls == IntPtr.Zero)
        {
            var cls = objc_allocateClassPair(ObjC.GetClass("NSObject"), "BrAInUiDelegate", 0);
            if (cls == IntPtr.Zero) return IntPtr.Zero;
            ClassAddMethod(cls, ObjC.Sel("webView:createWebViewWithConfiguration:forNavigationAction:windowFeatures:"),
                Marshal.GetFunctionPointerForDelegate(_create), "@@:@@@@");
            ClassAddMethod(cls, ObjC.Sel("webViewDidClose:"),
                Marshal.GetFunctionPointerForDelegate(_close), "v@:@");
            ClassAddMethod(cls, ObjC.Sel("forwardingTargetForSelector:"),
                Marshal.GetFunctionPointerForDelegate(_fwd), "@@::");
            ClassAddMethod(cls, ObjC.Sel("respondsToSelector:"),
                Marshal.GetFunctionPointerForDelegate(_resp), "B@::");
            objc_registerClassPair(cls);
            _cls = cls;
        }
        return ObjC.Send(ObjC.Send(_cls, ObjC.Sel("alloc")), ObjC.Sel("init"));
    }

    /// <summary>Wrap the bootstrap view's UIDelegate once; per-tab views inherit it
    /// when they copy the delegate. Safe to call again — installs only once.</summary>
    internal static void Install(IntPtr bootView)
    {
        if (bootView == IntPtr.Zero || _self != IntPtr.Zero) return;
        _orig = ObjC.Send(bootView, ObjC.Sel("UIDelegate"));
        _self = MakeSelf();
        if (_self == IntPtr.Zero) return;
        ObjC.Send(bootView, ObjC.Sel("setUIDelegate:"), _self);
    }

    // ---------------------------------------------------------------- callbacks

    private static bool SelectorIsOurs(IntPtr aSel)
        => aSel == ObjC.Sel("webView:createWebViewWithConfiguration:forNavigationAction:windowFeatures:")
        || aSel == ObjC.Sel("webViewDidClose:")
        || aSel == ObjC.Sel("forwardingTargetForSelector:")
        || aSel == ObjC.Sel("respondsToSelector:");

    private static byte OnResp(IntPtr self, IntPtr sel, IntPtr aSel)
    {
        if (SelectorIsOurs(aSel)) return 1;
        if (_orig != IntPtr.Zero)
            return ObjC.SendBool(_orig, ObjC.Sel("respondsToSelector:"), aSel);
        return 0;
    }

    private static IntPtr OnForward(IntPtr self, IntPtr sel, IntPtr aSel)
        => SelectorIsOurs(aSel) || _orig == IntPtr.Zero ? IntPtr.Zero : _orig;

    private static IntPtr OnCreate(IntPtr self, IntPtr sel, IntPtr webView, IntPtr cfg, IntPtr action, IntPtr feats)
    {
        try
        {
            var url = "";
            var req = ObjC.Send(action, ObjC.Sel("request"));
            if (req != IntPtr.Zero)
            {
                var u = ObjC.Send(req, ObjC.Sel("URL"));
                if (u != IntPtr.Zero)
                {
                    var abs = ObjC.Send(u, ObjC.Sel("absoluteString"));
                    url = ObjC.NsStringToString(abs) ?? "";
                }
            }
            // Only web URLs can live in a browser tab; javascript:/data:/empty
            // requests are left to the engine (nil = no window, nothing to host).
            var web = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                   || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            if (!web) return IntPtr.Zero;

            // A sized popup (OAuth/payment) keeps a real window so window.opener works.
            if (FeatureNumber(feats, "width") != IntPtr.Zero || FeatureNumber(feats, "height") != IntPtr.Zero)
                return CreatePopupWindow(cfg, feats);

            // Everything else becomes a normal browser tab.
            if (url.Length != 0) OnNewTab?.Invoke(url);
            return IntPtr.Zero;
        }
        catch (Exception ex) { Log?.Invoke("popup failed: " + ex.Message); return IntPtr.Zero; }
    }

    private static IntPtr FeatureNumber(IntPtr feats, string name)
        => feats == IntPtr.Zero ? IntPtr.Zero : ObjC.Send(feats, ObjC.Sel(name));

    private static double Num(IntPtr ns, double fallback)
    {
        if (ns == IntPtr.Zero) return fallback;
        return ObjC.SendDouble(ns, ObjC.Sel("doubleValue"));
    }

    private static IntPtr CreatePopupWindow(IntPtr cfg, IntPtr feats)
    {
        var w = Num(FeatureNumber(feats, "width"), 760);
        var h = Num(FeatureNumber(feats, "height"), 560);
        if (w < 200) w = 200; if (h < 120) h = 120;

        // WKWebView must use the supplied configuration — it carries the opener link.
        var frame = new MacWindow.CGRect { X = 0, Y = 0, W = w, H = h };
        var wv = ObjC.SendRectObj(ObjC.Send(ObjC.GetClass("WKWebView"), ObjC.Sel("alloc")),
            ObjC.Sel("initWithFrame:configuration:"), frame, cfg);
        if (wv == IntPtr.Zero) return IntPtr.Zero;

        // Same delegates as our tabs: our popup wrapper + the bootstrap nav delegate.
        ObjC.Send(wv, ObjC.Sel("setUIDelegate:"), _self);
        if (WkTabs.BootstrapNavDelegate is { } nav && nav != IntPtr.Zero)
            ObjC.Send(wv, ObjC.Sel("setNavigationDelegate:"), nav);

        const nint titled = 1, closable = 2, miniaturizable = 4, resizable = 8;
        var win = ObjC.SendRectNIntNIntBool(
            ObjC.Send(ObjC.GetClass("NSWindow"), ObjC.Sel("alloc")),
            ObjC.Sel("initWithContentRect:styleMask:backing:defer:"),
            frame, titled | closable | miniaturizable | resizable, 2, 0);
        if (win == IntPtr.Zero) { ObjC.Send(wv, ObjC.Sel("release")); return IntPtr.Zero; }

        ObjC.Send(win, ObjC.Sel("setReleasedWhenClosed:"), (IntPtr)0); // we release it on close
        ObjC.Send(win, ObjC.Sel("setContentView:"), wv);
        ObjC.Send(wv, ObjC.Sel("release")); // the window now holds the only reference
        ObjC.Send(win, ObjC.Sel("center"));
        ObjC.Send(win, ObjC.Sel("makeKeyAndOrderFront:"), IntPtr.Zero);

        lock (_popupWindow) _popupWindow[wv] = win;
        return wv;
    }

    private static void OnClose(IntPtr self, IntPtr sel, IntPtr webView)
    {
        try
        {
            IntPtr win;
            lock (_popupWindow) { if (!_popupWindow.Remove(webView, out win)) return; }
            if (win == IntPtr.Zero) return;
            // The window owns the web view (one reference), so it must not be released
            // again here — a second release left AppKit's focus-ring state pointing at
            // a freed first responder and crashed the next run-loop turn. We're also
            // inside a WebKit callback on this view, so keep it alive until the
            // autorelease pool drains, and drop first-responder status before it goes.
            ObjC.Send(ObjC.Send(webView, ObjC.Sel("retain")), ObjC.Sel("autorelease"));
            ObjC.Send(win, ObjC.Sel("makeFirstResponder:"), IntPtr.Zero);
            ObjC.Send(win, ObjC.Sel("close"));
            ObjC.Send(win, ObjC.Sel("autorelease"));
        }
        catch { }
    }
}
