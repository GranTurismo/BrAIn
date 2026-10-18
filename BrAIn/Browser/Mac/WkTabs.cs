using System.Runtime.InteropServices;

namespace BrAIn.Browser.Mac;

/// <summary>
/// Per-tab web views (macOS split mode). Every tab owns a real WKWebView, the
/// way Safari/Chrome do: switching tabs only changes which view is visible, so
/// pages keep their scroll position, video, form input and history, and two
/// tabs can never end up showing each other's content.
///
/// Each view gets its own WKUserContentController with
///   - a "brain" script-message handler bound to the tab id, so every message
///     arrives already attributed to the tab that sent it, and
///   - a document-start shim that provides window.external.sendMessage /
///     receiveMessage (the contract chrome.js and the eval envelopes use) and
///     the page helper scripts.
/// The configuration is a copy of the bootstrap (Photino) view's, so process
/// pool, website data store, preferences, user agent, navigation delegate
/// (downloads, crash recovery) and UI delegate (alerts, file pickers,
/// permission prompts) are all shared.
///
/// Everything here must run on the main thread.
/// </summary>
internal static partial class WkTabs
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

    [LibraryImport(LibObjC, EntryPoint = "objc_getProtocol")]
    private static partial IntPtr GetProtocol([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [LibraryImport(LibObjC, EntryPoint = "class_addProtocol")]
    private static partial byte ClassAddProtocol(IntPtr cls, IntPtr proto);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MsgImp(IntPtr self, IntPtr sel, IntPtr controller, IntPtr message);

    // Rooted forever — the IMP must never be collected.
    private static readonly MsgImp _msgImp = OnScriptMessage;

    private const nint NSWindowBelow = -1;
    private const nint NSWindowAbove = 1;

    /// <summary>The same window.external contract Photino provides, plus
    /// __brainPush for host → page pushes (queued until chrome.js subscribes).</summary>
    internal const string Shim =
        "window.external={sendMessage:function(m){try{window.webkit.messageHandlers.brain.postMessage(String(m));}catch(e){}}," +
        "receiveMessage:function(cb){window.__brainCb=cb;var q=window.__brainQ||[];window.__brainQ=null;" +
        "q.forEach(function(o){try{cb(o)}catch(e){}});}};" +
        "window.__brainPush=function(o){var cb=window.__brainCb;if(cb){try{cb(o)}catch(e){}}" +
        "else{(window.__brainQ=window.__brainQ||[]).push(o);}};\n";

    private sealed class ViewInfo
    {
        public IntPtr View, Handler, Ucc;
        public Guid Tab;
        public bool Private;
    }

    private static IntPtr _handlerClass;
    private static readonly Dictionary<IntPtr, ViewInfo> _views = new();
    private static readonly Dictionary<IntPtr, Guid> _byHandler = new();
    private static IntPtr _privStore;
    private static IntPtr _boot;

    /// <summary>Message from a page view: (tab id, "brain:..." command or JSON envelope).</summary>
    internal static Action<Guid, string>? OnMessage;

    /// <summary>The view currently shown.</summary>
    internal static IntPtr Active { get; private set; }

    internal static bool Has(IntPtr view) => view != IntPtr.Zero && _views.ContainsKey(view);

    // ------------------------------------------------------------------ messages

    private static void OnScriptMessage(IntPtr self, IntPtr sel, IntPtr controller, IntPtr message)
    {
        try
        {
            var body = ObjC.Send(message, ObjC.Sel("body"));
            if (body == IntPtr.Zero || !ObjC.IsKindOf(body, "NSString")) return;
            Guid id;
            lock (_byHandler)
                if (!_byHandler.TryGetValue(self, out id)) return;
            var s = ObjC.NsStringToString(body);
            if (!string.IsNullOrEmpty(s)) OnMessage?.Invoke(id, s);
        }
        catch { }
    }

    private static IntPtr NewHandler(Guid tab)
    {
        if (_handlerClass == IntPtr.Zero)
        {
            var cls = objc_allocateClassPair(ObjC.GetClass("NSObject"), "BrAInTabHandler", 0);
            if (cls == IntPtr.Zero) return IntPtr.Zero;
            ClassAddMethod(cls, ObjC.Sel("userContentController:didReceiveScriptMessage:"),
                Marshal.GetFunctionPointerForDelegate(_msgImp), "v@:@@");
            var proto = GetProtocol("WKScriptMessageHandler");
            if (proto != IntPtr.Zero) ClassAddProtocol(cls, proto);
            objc_registerClassPair(cls);
            _handlerClass = cls;
        }
        var h = ObjC.Send(ObjC.Send(_handlerClass, ObjC.Sel("alloc")), ObjC.Sel("init"));
        if (h != IntPtr.Zero) lock (_byHandler) _byHandler[h] = tab;
        return h;
    }

    private static void AddScript(IntPtr ucc, string source)
    {
        var us = ObjC.SendStrIntBool(
            ObjC.Send(ObjC.GetClass("WKUserScript"), ObjC.Sel("alloc")),
            ObjC.Sel("initWithSource:injectionTime:forMainFrameOnly:"),
            ObjC.NsString(source), 0 /* document start */, 1 /* main frame only */);
        if (us == IntPtr.Zero) return;
        ObjC.Send(ucc, ObjC.Sel("addUserScript:"), us);
        ObjC.Send(us, ObjC.Sel("release")); // the controller retains it
    }

    // ------------------------------------------------------------------ lifecycle

    /// <summary>The bootstrap view Photino created. It is hidden and only used
    /// as the template new tab views copy their configuration from.</summary>
    internal static void SetBootstrap(IntPtr bootView)
    {
        _boot = bootView;
        // Wrap its UIDelegate before tab views are made: Create copies the
        // bootstrap delegate, so every view inherits the popup handler too.
        try { WkPopup.Install(bootView); } catch { }
    }

    /// <summary>The bootstrap view's navigation delegate — popup windows reuse
    /// it so loads inside them behave like tab loads.</summary>
    internal static IntPtr BootstrapNavDelegate =>
        _boot == IntPtr.Zero ? IntPtr.Zero : ObjC.Send(_boot, ObjC.Sel("navigationDelegate"));

    /// <summary>Frame of the page area: everything below the toolbar strip.</summary>
    private static MacWindow.CGRect PageFrame(IntPtr content)
    {
        var b = ObjC.SendRect(content, ObjC.Sel("bounds"));
        var bar = WkBar.BarHeight;
        return new MacWindow.CGRect { X = 0, Y = 0, W = Math.Max(1, b.W - WkBar.SideWidth(b.W)), H = Math.Max(1, b.H - bar) };
    }

    /// <summary>Creates a hidden web view for a tab and adds it under the toolbar.</summary>
    internal static IntPtr Create(IntPtr nsWindow, Guid tab, string libSource, bool priv, int zoomPct)
    {
        var content = ObjC.Send(nsWindow, ObjC.Sel("contentView"));
        if (content == IntPtr.Zero || _boot == IntPtr.Zero) return IntPtr.Zero;

        var tcfg = ObjC.Send(_boot, ObjC.Sel("configuration"));
        var cfg = tcfg == IntPtr.Zero ? IntPtr.Zero : ObjC.Send(tcfg, ObjC.Sel("copy")); // +1
        if (cfg == IntPtr.Zero) return IntPtr.Zero;

        var ucc = ObjC.Send(ObjC.Send(ObjC.GetClass("WKUserContentController"), ObjC.Sel("alloc")), ObjC.Sel("init")); // +1
        var handler = NewHandler(tab); // +1
        if (ucc == IntPtr.Zero || handler == IntPtr.Zero)
        {
            ObjC.Send(cfg, ObjC.Sel("release"));
            return IntPtr.Zero;
        }
        ObjC.Send(ucc, ObjC.Sel("addScriptMessageHandler:name:"), handler, ObjC.NsString("brain"));
        AddScript(ucc, Shim);
        AddScript(ucc, libSource);
        ObjC.Send(cfg, ObjC.Sel("setUserContentController:"), ucc);

        if (priv)
        {
            // One ephemeral store shared by all private tabs (a private "window"):
            // nothing it holds ever reaches disk, and it is dropped with the last tab.
            if (_privStore == IntPtr.Zero)
            {
                _privStore = ObjC.Send(ObjC.GetClass("WKWebsiteDataStore"), ObjC.Sel("nonPersistentDataStore"));
                if (_privStore != IntPtr.Zero) ObjC.Send(_privStore, ObjC.Sel("retain"));
            }
            if (_privStore != IntPtr.Zero) ObjC.Send(cfg, ObjC.Sel("setWebsiteDataStore:"), _privStore);
        }

        var wv = ObjC.SendRectObj(ObjC.Send(ObjC.GetClass("WKWebView"), ObjC.Sel("alloc")),
            ObjC.Sel("initWithFrame:configuration:"), PageFrame(content), cfg);
        ObjC.Send(cfg, ObjC.Sel("release")); // the view keeps its own copy
        if (wv == IntPtr.Zero)
        {
            ObjC.Send(ucc, ObjC.Sel("release"));
            ObjC.Send(handler, ObjC.Sel("release"));
            lock (_byHandler) _byHandler.Remove(handler);
            return IntPtr.Zero;
        }

        // Same browser identity and delegates as the bootstrap view.
        var ua = ObjC.Send(_boot, ObjC.Sel("customUserAgent"));
        if (ua != IntPtr.Zero) ObjC.Send(wv, ObjC.Sel("setCustomUserAgent:"), ua);
        var nav = ObjC.Send(_boot, ObjC.Sel("navigationDelegate"));
        if (nav != IntPtr.Zero) ObjC.Send(wv, ObjC.Sel("setNavigationDelegate:"), nav);
        var ui = ObjC.Send(_boot, ObjC.Sel("UIDelegate"));
        if (ui != IntPtr.Zero) ObjC.Send(wv, ObjC.Sel("setUIDelegate:"), ui);

        // Pages draw their own background (default white) — a transparent view let the
        // dark window colour bleed through sites that set no background. Internal pages
        // set their own dark background in theme.css.
        var yes = ObjC.Send(ObjC.GetClass("NSNumber"), ObjC.Sel("numberWithBool:"), (IntPtr)1);
        ObjC.Send(wv, ObjC.Sel("setValue:forKey:"), yes, ObjC.NsString("drawsBackground"));
        // Web content always sees a light system appearance, so sites don't flip to their
        // dark style (prefers-color-scheme) just because macOS is in dark mode.
        var aqua = ObjC.Send(ObjC.GetClass("NSAppearance"), ObjC.Sel("appearanceNamed:"), ObjC.NsString("NSAppearanceNameAqua"));
        if (aqua != IntPtr.Zero) ObjC.Send(wv, ObjC.Sel("setAppearance:"), aqua);
        ObjC.SendNInt(wv, ObjC.Sel("setAutoresizingMask:"), 18); // width + height follow the window
        ObjC.SendNInt(wv, ObjC.Sel("setHidden:"), 1);
        if (zoomPct != 100) SetZoom(wv, zoomPct);

        var bar = WkBar.BarView;
        if (bar != IntPtr.Zero)
            ObjC.SendViewPosView(content, ObjC.Sel("addSubview:positioned:relativeTo:"), wv, NSWindowBelow, bar);
        else
            ObjC.SendViewPosView(content, ObjC.Sel("addSubview:positioned:relativeTo:"), wv, NSWindowAbove, _boot);

        // The controller (ucc) holds the handler; our +1 refs are no longer needed.
        ObjC.Send(handler, ObjC.Sel("release"));
        ObjC.Send(ucc, ObjC.Sel("release"));

        _views[wv] = new ViewInfo { View = wv, Handler = handler, Ucc = ucc, Tab = tab, Private = priv };
        return wv;
    }

    /// <summary>Tears a tab's view down completely (stops loads, drops scripts,
    /// releases the web content process).</summary>
    internal static void Destroy(IntPtr view)
    {
        if (view == IntPtr.Zero || !_views.Remove(view, out var info)) return;
        try
        {
            ObjC.Send(view, ObjC.Sel("stopLoading"));
            ObjC.Send(view, ObjC.Sel("setNavigationDelegate:"), IntPtr.Zero);
            ObjC.Send(view, ObjC.Sel("setUIDelegate:"), IntPtr.Zero);
            ObjC.Send(info.Ucc, ObjC.Sel("removeScriptMessageHandlerForName:"), ObjC.NsString("brain"));
            ObjC.Send(info.Ucc, ObjC.Sel("removeAllUserScripts"));
            ObjC.Send(view, ObjC.Sel("removeFromSuperview"));
            ObjC.Send(view, ObjC.Sel("release"));
        }
        catch { }
        lock (_byHandler) _byHandler.Remove(info.Handler);
        if (Active == view) Active = IntPtr.Zero;
        if (info.Private && _privStore != IntPtr.Zero && !_views.Values.Any(v => v.Private))
        {
            ObjC.Send(_privStore, ObjC.Sel("release")); // ephemeral data goes with it
            _privStore = IntPtr.Zero;
        }
    }

    // ------------------------------------------------------------------ visibility / layout

    /// <summary>Makes <paramref name="view"/> the visible page view. The new view
    /// is shown before the old ones are hidden so there is never an empty frame,
    /// and keyboard focus only moves if it was inside a view that is being hidden
    /// (a focused address bar keeps its focus).</summary>
    internal static void Show(IntPtr nsWindow, IntPtr view)
    {
        if (view == IntPtr.Zero || !_views.ContainsKey(view)) return;
        var fr = nsWindow == IntPtr.Zero ? IntPtr.Zero : ObjC.Send(nsWindow, ObjC.Sel("firstResponder"));
        var focusWasInHidden = false;
        bool InsideView(IntPtr v) =>
            fr != IntPtr.Zero && fr != nsWindow &&
            ObjC.SendBool(fr, ObjC.Sel("respondsToSelector:"), ObjC.Sel("isDescendantOf:")) != 0 &&
            ObjC.SendBool(fr, ObjC.Sel("isDescendantOf:"), v) != 0;
        var wasHidden = (ObjC.SendNInt(view, ObjC.Sel("isHidden")) & 0xFF) != 0;
        ObjC.SendNInt(view, ObjC.Sel("setHidden:"), 0);
        if (wasHidden) FadeIn(view);
        foreach (var v in _views.Keys)
        {
            if (v == view) continue;
            if (InsideView(v)) focusWasInHidden = true;
            ObjC.SendNInt(v, ObjC.Sel("setHidden:"), 1);
        }
        // the window starts with focus in Photino's (now hidden) bootstrap view
        if (_boot != IntPtr.Zero && (fr == _boot || InsideView(_boot))) focusWasInHidden = true;
        Active = view;
        if (focusWasInHidden) ObjC.Send(nsWindow, ObjC.Sel("makeFirstResponder:"), view);
    }

    /// <summary>Soft entrance for a page view that just became visible (new tab,
    /// switch, or the neighbour after a close): a short fade plus a few points of
    /// upward settle. Core Animation only — the model layer is untouched, so a
    /// dropped animation can never leave the page translucent.</summary>
    private static void FadeIn(IntPtr view)
    {
        try
        {
            var layer = ObjC.Send(view, ObjC.Sel("layer"));
            if (layer == IntPtr.Zero) return;
            var num = ObjC.GetClass("NSNumber");
            var timing = ObjC.Send(ObjC.GetClass("CAMediaTimingFunction"), ObjC.Sel("functionWithName:"), ObjC.NsString("easeOut"));
            IntPtr Anim(string keyPath, double from, double to, double dur)
            {
                var a = ObjC.Send(ObjC.GetClass("CABasicAnimation"), ObjC.Sel("animationWithKeyPath:"), ObjC.NsString(keyPath));
                ObjC.Send(a, ObjC.Sel("setFromValue:"), ObjC.SendDoubleArg(num, ObjC.Sel("numberWithDouble:"), from));
                ObjC.Send(a, ObjC.Sel("setToValue:"), ObjC.SendDoubleArg(num, ObjC.Sel("numberWithDouble:"), to));
                ObjC.SendDoubleArg(a, ObjC.Sel("setDuration:"), dur);
                if (timing != IntPtr.Zero) ObjC.Send(a, ObjC.Sel("setTimingFunction:"), timing);
                return a;
            }
            ObjC.Send(layer, ObjC.Sel("addAnimation:forKey:"), Anim("opacity", 0, 1, 0.18), ObjC.NsString("brainFade"));
            ObjC.Send(layer, ObjC.Sel("addAnimation:forKey:"), Anim("transform.translation.y", 8, 0, 0.22), ObjC.NsString("brainSettle"));
        }
        catch { /* purely cosmetic */ }
    }

    internal static void FocusActive(IntPtr nsWindow)
    {
        if (Active != IntPtr.Zero && nsWindow != IntPtr.Zero)
            ObjC.Send(nsWindow, ObjC.Sel("makeFirstResponder:"), Active);
    }

    /// <summary>Resizes every page view (and the hidden bootstrap view) to the page area.</summary>
    internal static void ApplyFrame(MacWindow.CGRect want)
    {
        void Set(IntPtr v)
        {
            if (v == IntPtr.Zero) return;
            var cur = ObjC.SendRect(v, ObjC.Sel("frame"));
            if (Math.Abs(cur.X - want.X) > 0.5 || Math.Abs(cur.Y - want.Y) > 0.5 ||
                Math.Abs(cur.W - want.W) > 0.5 || Math.Abs(cur.H - want.H) > 0.5)
                ObjC.SendRectArg(v, ObjC.Sel("setFrame:"), want);
        }
        foreach (var v in _views.Keys) Set(v);
        Set(_boot);
    }

    /// <summary>Hides the bootstrap view (it only exists to host the window).</summary>
    internal static void HideBootstrap()
    {
        if (_boot != IntPtr.Zero) ObjC.SendNInt(_boot, ObjC.Sel("setHidden:"), 1);
    }

    // ------------------------------------------------------------------ view operations

    private static IntPtr NsUrl(string url)
    {
        string abs;
        try { abs = new Uri(url).AbsoluteUri; } catch { abs = url; }
        return ObjC.Send(ObjC.GetClass("NSURL"), ObjC.Sel("URLWithString:"), ObjC.NsString(abs));
    }

    internal static void Load(IntPtr view, string url)
    {
        var nsUrl = NsUrl(url);
        if (nsUrl == IntPtr.Zero) return;
        if (url.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            var dir = ObjC.Send(nsUrl, ObjC.Sel("URLByDeletingLastPathComponent"));
            ObjC.Send(view, ObjC.Sel("loadFileURL:allowingReadAccessToURL:"), nsUrl, dir == IntPtr.Zero ? nsUrl : dir);
            return;
        }
        var req = ObjC.Send(ObjC.GetClass("NSURLRequest"), ObjC.Sel("requestWithURL:"), nsUrl);
        if (req != IntPtr.Zero) ObjC.Send(view, ObjC.Sel("loadRequest:"), req);
    }

    /// <summary>Internal pages (start page, history…): raw HTML, no base URL.</summary>
    internal static void LoadHtml(IntPtr view, string html)
        => ObjC.Send(view, ObjC.Sel("loadHTMLString:baseURL:"), ObjC.NsString(html), IntPtr.Zero);

    internal static bool CanGoBack(IntPtr view) => (ObjC.SendNInt(view, ObjC.Sel("canGoBack")) & 0xFF) != 0;
    internal static bool CanGoForward(IntPtr view) => (ObjC.SendNInt(view, ObjC.Sel("canGoForward")) & 0xFF) != 0;

    internal static void Eval(IntPtr view, string js)
    {
        if (view == IntPtr.Zero) return;
        ObjC.Send(view, ObjC.Sel("evaluateJavaScript:completionHandler:"), ObjC.NsString(js), IntPtr.Zero);
    }

    /// <summary>Host → page push (chrome state, DevTools data, password prompts…).</summary>
    internal static void Push(IntPtr view, string json)
        => Eval(view, "window.__brainPush&&window.__brainPush(" + json + ")");

    internal static void SetZoom(IntPtr view, int pct)
    {
        var sel = ObjC.Sel("setPageZoom:");
        if (ObjC.SendBool(view, ObjC.Sel("respondsToSelector:"), sel) == 0) return;
        ObjC.SendDoubleArg(view, sel, pct / 100.0);
    }

    internal static void SetZoomAll(int pct)
    {
        foreach (var v in _views.Keys) SetZoom(v, pct);
    }

    /// <summary>Window background behind transparent/blank pages — the app's dark theme colour.</summary>
    internal static void SetWindowBackground(IntPtr nsWindow)
    {
        var color = ObjC.SendDouble4(ObjC.GetClass("NSColor"), ObjC.Sel("colorWithSRGBRed:green:blue:alpha:"),
            0x0b / 255.0, 0x0b / 255.0, 0x13 / 255.0, 1.0);
        if (color != IntPtr.Zero) ObjC.Send(nsWindow, ObjC.Sel("setBackgroundColor:"), color);
    }
}
