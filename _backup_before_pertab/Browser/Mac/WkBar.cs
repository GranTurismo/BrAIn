using System.Runtime.InteropServices;

namespace BrAIn.Browser.Mac;

/// <summary>
/// Split-toolbar mode (macOS, --split): the toolbar lives in its own native
/// WKWebView stacked above the page's WKWebView, the way a real browser keeps
/// its chrome outside the page. The page view is simply shorter, so every site
/// lays out below the bar with no shifting of fixed headers, 100vh bugs or
/// per-site fixes.
///
/// The bar view runs chrome.js with window.__BRAIN_ROLE='bar'. It talks to the
/// host through its own WKScriptMessageHandler ("brain") — the same
/// "brain:&lt;verb&gt;[:arg]" strings the page's window.external bridge carries —
/// and receives host pushes via evaluateJavaScript. Everything must run on the
/// main thread.
/// </summary>
internal static partial class WkBar
{
    private const string LibObjC = "/usr/lib/libobjc.A.dylib";
    internal const double BarHeight = 46;

    [LibraryImport(LibObjC)]
    private static partial IntPtr objc_allocateClassPair(IntPtr superclass,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, nint extraBytes);

    [LibraryImport(LibObjC)]
    private static partial void objc_registerClassPair(IntPtr cls);

    [LibraryImport(LibObjC, EntryPoint = "class_addMethod")]
    private static partial byte ClassAddMethod(IntPtr cls, IntPtr sel, IntPtr imp,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string types);

    [LibraryImport(LibObjC, EntryPoint = "objc_getProtocol")]
    private static partial IntPtr GetProtocol([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [LibraryImport(LibObjC, EntryPoint = "class_addProtocol")]
    private static partial byte ClassAddProtocol(IntPtr cls, IntPtr proto);

    /// -[WKWebView initWithFrame:configuration:]
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial IntPtr SendRectObj(IntPtr recv, IntPtr sel, MacWindow.CGRect rect, IntPtr obj);

    /// -[NSView addSubview:positioned:relativeTo:]
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial void SendViewPosView(IntPtr recv, IntPtr sel, IntPtr view, nint pos, IntPtr rel);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MsgImp(IntPtr self, IntPtr sel, IntPtr controller, IntPtr message);

    // Rooted forever — the IMP must never be collected.
    private static readonly MsgImp _msgImp = OnMessage;

    private static IntPtr _bar;
    private static IntPtr _page;
    private static string _mode = "bar";

    /// <summary>Receives every "brain:..." command string the bar view posts.</summary>
    internal static Action<string>? OnBarMessage;

    internal static bool Exists => _bar != IntPtr.Zero;

    private static void OnMessage(IntPtr self, IntPtr sel, IntPtr controller, IntPtr message)
    {
        try
        {
            var body = ObjC.Send(message, ObjC.Sel("body"));
            if (body == IntPtr.Zero || !ObjC.IsKindOf(body, "NSString")) return;
            var s = ObjC.NsStringToString(body);
            if (!string.IsNullOrEmpty(s)) OnBarMessage?.Invoke(s);
        }
        catch { }
    }

    private static IntPtr NewHandler()
    {
        var cls = objc_allocateClassPair(ObjC.GetClass("NSObject"), "BrAInBarHandler", 0);
        if (cls == IntPtr.Zero) return IntPtr.Zero;
        ClassAddMethod(cls, ObjC.Sel("userContentController:didReceiveScriptMessage:"),
            Marshal.GetFunctionPointerForDelegate(_msgImp), "v@:@@");
        var proto = GetProtocol("WKScriptMessageHandler");
        if (proto != IntPtr.Zero) ClassAddProtocol(cls, proto);
        objc_registerClassPair(cls);
        return ObjC.Send(ObjC.Send(cls, ObjC.Sel("alloc")), ObjC.Sel("init"));
    }

    /// <summary>Creates the bar view, shrinks the page view below it and loads
    /// <paramref name="html"/>. <paramref name="script"/> is injected at document start.</summary>
    internal static bool Create(IntPtr nsWindow, string script, string html)
    {
        if (_bar != IntPtr.Zero || nsWindow == IntPtr.Zero) return _bar != IntPtr.Zero;
        var page = MacWindow.FindWebView(nsWindow);
        var content = ObjC.Send(nsWindow, ObjC.Sel("contentView"));
        if (page == IntPtr.Zero || content == IntPtr.Zero) return false;

        var cfg = ObjC.Send(ObjC.Send(ObjC.GetClass("WKWebViewConfiguration"), ObjC.Sel("alloc")), ObjC.Sel("init"));
        var ucc = ObjC.Send(ObjC.Send(ObjC.GetClass("WKUserContentController"), ObjC.Sel("alloc")), ObjC.Sel("init"));
        var handler = NewHandler();
        if (cfg == IntPtr.Zero || ucc == IntPtr.Zero || handler == IntPtr.Zero) return false;
        ObjC.Send(ucc, ObjC.Sel("addScriptMessageHandler:name:"), handler, ObjC.NsString("brain"));
        ObjC.Send(cfg, ObjC.Sel("setUserContentController:"), ucc);

        // chrome.js (+ the window.external shim it expects), document start
        var us = ObjC.SendStrIntBool(
            ObjC.Send(ObjC.GetClass("WKUserScript"), ObjC.Sel("alloc")),
            ObjC.Sel("initWithSource:injectionTime:forMainFrameOnly:"),
            ObjC.NsString(script), 0, 1);
        if (us != IntPtr.Zero) ObjC.Send(ucc, ObjC.Sel("addUserScript:"), us);

        var b = ObjC.SendRect(content, ObjC.Sel("bounds"));
        var frame = new MacWindow.CGRect { X = 0, Y = b.H - BarHeight, W = b.W, H = BarHeight };
        var wv = SendRectObj(ObjC.Send(ObjC.GetClass("WKWebView"), ObjC.Sel("alloc")),
            ObjC.Sel("initWithFrame:configuration:"), frame, cfg);
        if (wv == IntPtr.Zero) return false;

        // transparent: the page shows through while a popover is open
        var no = ObjC.Send(ObjC.GetClass("NSNumber"), ObjC.Sel("numberWithBool:"), IntPtr.Zero);
        ObjC.Send(wv, ObjC.Sel("setValue:forKey:"), no, ObjC.NsString("drawsBackground"));
        ObjC.SendNInt(wv, ObjC.Sel("setAutoresizingMask:"), 10); // width + stick to top
        SendViewPosView(content, ObjC.Sel("addSubview:positioned:relativeTo:"), wv, 1 /* NSWindowAbove */, page);

        _bar = wv;
        _page = page;
        _mode = "bar";
        Layout(nsWindow, "bar");

        var baseUrl = ObjC.Send(ObjC.GetClass("NSURL"), ObjC.Sel("URLWithString:"), ObjC.NsString("https://brain-bar.invalid/"));
        ObjC.Send(wv, ObjC.Sel("loadHTMLString:baseURL:"), ObjC.NsString(html), baseUrl);
        Console.Error.WriteLine("[BrAIn] split: bar view created");
        return true;
    }

    /// <summary>Lays out the page view below the bar and sizes the bar:
    /// "bar" = 46pt strip, "full" = whole window (palette/menus), or a pixel
    /// height for small popovers that hang below the strip.</summary>
    internal static void Layout(IntPtr nsWindow, string? mode = null)
    {
        if (_bar == IntPtr.Zero || nsWindow == IntPtr.Zero) return;
        if (mode is not null) _mode = mode;
        var content = ObjC.Send(nsWindow, ObjC.Sel("contentView"));
        if (content == IntPtr.Zero) return;
        var b = ObjC.SendRect(content, ObjC.Sel("bounds"));
        if (b.W <= 0 || b.H <= BarHeight) return;

        // page: everything below the strip (skip no-op resizes — each one re-lays-out the page)
        var want = new MacWindow.CGRect { X = 0, Y = 0, W = b.W, H = b.H - BarHeight };
        var cur = ObjC.SendRect(_page, ObjC.Sel("frame"));
        if (Math.Abs(cur.X - want.X) > 0.5 || Math.Abs(cur.Y - want.Y) > 0.5 ||
            Math.Abs(cur.W - want.W) > 0.5 || Math.Abs(cur.H - want.H) > 0.5)
            ObjC.SendRectArg(_page, ObjC.Sel("setFrame:"), want);

        var full = _mode == "full";
        double barH = BarHeight;
        if (full) barH = b.H;
        else if (double.TryParse(_mode, System.Globalization.NumberStyles.Float,
                     System.Globalization.CultureInfo.InvariantCulture, out var px))
            barH = Math.Clamp(px, BarHeight, b.H);
        ObjC.SendNInt(_bar, ObjC.Sel("setAutoresizingMask:"), full ? 18 : 10);
        ObjC.SendRectArg(_bar, ObjC.Sel("setFrame:"),
            new MacWindow.CGRect { X = 0, Y = b.H - barH, W = b.W, H = barH });
    }

    /// <summary>Runs JS in the bar view (host → bar pushes and commands).</summary>
    internal static void Eval(string js)
    {
        if (_bar == IntPtr.Zero) return;
        ObjC.Send(_bar, ObjC.Sel("evaluateJavaScript:completionHandler:"), ObjC.NsString(js), IntPtr.Zero);
    }

    internal static void FocusBar(IntPtr nsWindow)
    {
        if (_bar != IntPtr.Zero && nsWindow != IntPtr.Zero)
            ObjC.Send(nsWindow, ObjC.Sel("makeFirstResponder:"), _bar);
    }

    internal static void FocusPage(IntPtr nsWindow)
    {
        if (_page != IntPtr.Zero && nsWindow != IntPtr.Zero)
            ObjC.Send(nsWindow, ObjC.Sel("makeFirstResponder:"), _page);
    }
}
