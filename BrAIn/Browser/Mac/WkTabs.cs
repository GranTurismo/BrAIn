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

    /// <summary>The same window.external contract Photino provides, plus __brainPush for host → page pushes (queued until chrome.js
    /// subscribes) — but built so a WEB PAGE cannot use or watch it (see ViewAuth). Per view, because it carries the view's secret token:
    /// <list type="bullet">
    /// <item>BrAIn's own scripts (brain.js, chrome.js) take a private handle from <c>window.__brainCap</c> at the very start, and the library bundle
    /// removes it again before any page script runs; messages sent through it carry the token.</item>
    /// <item>For a web page <c>window.external.sendMessage</c> only forwards the replies to scripts the host evaluated itself and the user-script
    /// runner's status reports; everything else is ignored. BrAIn's own pages (about:*) get the full, authenticated sender.</item>
    /// <item><c>window.__brainPush</c> (what the host calls) is sealed: not writable, not configurable, and it feeds a callback only BrAIn's scripts
    /// can reach — a page can no longer wrap it to read what the host sends.</item>
    /// <item>The native postMessage is captured now, so a page that later replaces it, or reaches a pristine copy through an iframe, still has no token.</item>
    /// </list></summary>
    internal static string ShimFor(string token) =>
        "(function(){if(window.__brainPush)return;var T='" + token + "',S=String,apply=Reflect.apply,H=null,P=null;" +
        "try{H=window.webkit.messageHandlers.brain;P=H.postMessage}catch(e){}" +
        "function raw(s){if(P){try{apply(P,H,[s])}catch(e){}}}" +
        "var internal=false;try{internal=/^(about|data|app):$/i.test(location.protocol)&&window.top===window&&!window.opener}catch(e){}" +
        "var q=[],cb=null;" +
        "function recv(f){cb=f;var a=q;q=[];for(var i=0;i<a.length;i++){try{f(a[i])}catch(e){}}}" +
        "function push(o){if(cb){try{cb(o)}catch(e){}}else{q.push(o)}}" +
        "function send(m){raw(T+'|'+S(m))}" +
        "function reply(m){m=S(m);if(m.indexOf('{\"b\":1,')===0||m.indexOf('{\"b\": 1,')===0||m.indexOf('brain:usran:')===0||m.indexOf('brain:usrerr:')===0)raw(m)}" +
        "try{Object.defineProperty(window,'__brainCap',{value:{send:send,recv:recv},writable:true,configurable:true,enumerable:false})}catch(e){}" +
        "window.external=internal?{sendMessage:send,receiveMessage:recv}:{sendMessage:reply,receiveMessage:function(){}};" +
        "Object.defineProperty(window,'__brainPush',{value:push,writable:false,configurable:false,enumerable:false});" +
        "})();\n";

    private sealed class ViewInfo
    {
        public IntPtr View, Handler, Ucc;
        public Guid Tab;
        public bool Private;
        public bool Ghost;   // private view on the seeded ghost store ("Keep my cookies & logins")
        public string Lib = "", Qc = "", Token = ""; // Token: this view's secret, see ViewAuth
        public bool Dark;
    }

    private static IntPtr _handlerClass;
    private static readonly Dictionary<IntPtr, ViewInfo> _views = new();
    private static readonly Dictionary<IntPtr, Guid> _byHandler = new();
    private static readonly Dictionary<IntPtr, string> _tokens = new(); // handler -> the view's secret (same lock as _byHandler)
    private static IntPtr _privStore;
    private static IntPtr _boot;

    /// <summary>Message from a page view: (tab id, "brain:..." command or JSON envelope).</summary>
    internal static Action<Guid, string>? OnMessage;

    /// <summary>The view currently shown.</summary>
    internal static IntPtr Active { get; private set; }

    /// <summary>WKWebView.URL — what the engine has committed, not what the host last asked for.</summary>
    internal static string? CurrentUrl(IntPtr view)
    {
        if (view == IntPtr.Zero) return null;
        var u = ObjC.Send(view, ObjC.Sel("URL"));
        if (u == IntPtr.Zero) return null;
        var s = ObjC.Send(u, ObjC.Sel("absoluteString"));
        return s == IntPtr.Zero ? null : ObjC.NsStringToString(s);
    }

    internal static bool Has(IntPtr view) => view != IntPtr.Zero && _views.ContainsKey(view);

    // ------------------------------------------------------------------ messages

    private static void OnScriptMessage(IntPtr self, IntPtr sel, IntPtr controller, IntPtr message)
    {
        try
        {
            var body = ObjC.Send(message, ObjC.Sel("body"));
            if (body == IntPtr.Zero || !ObjC.IsKindOf(body, "NSString")) return;
            Guid id; string? token;
            lock (_byHandler)
            {
                if (!_byHandler.TryGetValue(self, out id)) return;
                _tokens.TryGetValue(self, out token);
            }
            var s = ObjC.NsStringToString(body);
            if (string.IsNullOrEmpty(s) || token is null) return;
            s = ViewAuth.Accept(token, s); // a page's own script cannot produce the token: its messages stop here
            if (s is not null) OnMessage?.Invoke(id, s);
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
    internal static IntPtr Create(IntPtr nsWindow, Guid tab, string libSource, bool priv, int zoomPct, bool ghostLogins = false)
    {
        var content = ObjC.Send(nsWindow, ObjC.Sel("contentView"));
        if (content == IntPtr.Zero || _boot == IntPtr.Zero) return IntPtr.Zero;

        var tcfg = ObjC.Send(_boot, ObjC.Sel("configuration"));
        var cfg = tcfg == IntPtr.Zero ? IntPtr.Zero : ObjC.Send(tcfg, ObjC.Sel("copy")); // +1
        if (cfg == IntPtr.Zero) return IntPtr.Zero;

        var ucc = ObjC.Send(ObjC.Send(ObjC.GetClass("WKUserContentController"), ObjC.Sel("alloc")), ObjC.Sel("init")); // +1
        var handler = NewHandler(tab); // +1
        var token = ViewAuth.NewToken();
        lock (_byHandler) _tokens[handler] = token;
        if (ucc == IntPtr.Zero || handler == IntPtr.Zero)
        {
            ObjC.Send(cfg, ObjC.Sel("release"));
            return IntPtr.Zero;
        }
        ObjC.Send(ucc, ObjC.Sel("addScriptMessageHandler:name:"), handler, ObjC.NsString("brain"));
        AddScript(ucc, ShimFor(token));
        AddScript(ucc, libSource);
        if (_netRec.Length > 0) AddScript(ucc, _netRec); // DevTools is open: this tab records its requests from the first one
        ObjC.Send(cfg, ObjC.Sel("setUserContentController:"), ucc);
        EnableElementFullscreen(cfg);

        // Ghost tabs that keep the user's cookies share the seeded ephemeral store (WkTabs.Ghost.cs).
        // If it is gone (its last tab just closed), they fall back to the clean private store below.
        var ghost = priv && ghostLogins && _ghostStore != IntPtr.Zero;
        if (ghost)
            ObjC.Send(cfg, ObjC.Sel("setWebsiteDataStore:"), _ghostStore);
        else if (priv)
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
            lock (_byHandler) { _byHandler.Remove(handler); _tokens.Remove(handler); }
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
        // Two-finger trackpad swipe left/right steps back/forward through history (the macOS
        // "Swipe between pages" gesture, with the native page-peel animation).
        ObjC.SendNInt(wv, ObjC.Sel("setAllowsBackForwardNavigationGestures:"), 1);
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

        _views[wv] = new ViewInfo { View = wv, Handler = handler, Ucc = ucc, Tab = tab, Private = priv, Ghost = ghost, Lib = libSource, Token = token };
        return wv;
    }

    /// <summary>Quick controls: the view's document-start values (window.__brainQc, read and
    /// removed by chrome.js before any page script runs). Applies from the next document on;
    /// the page showing now gets a push instead.</summary>
    internal static void SetQcScript(IntPtr view, string js)
    {
        if (!_views.TryGetValue(view, out var info) || info.Qc == js) return;
        info.Qc = js;
        ApplyScripts(info);
    }

    /// <summary>The view's document-start scripts, in order: quick-control values (first, so chrome.js finds them), the shim,
    /// the page library, and — only while DevTools is open — the network recorder. Applies from the next document on.</summary>
    private static void ApplyScripts(ViewInfo info)
    {
        ObjC.Send(info.Ucc, ObjC.Sel("removeAllUserScripts"));
        if (info.Qc.Length > 0) AddScript(info.Ucc, info.Qc);
        AddScript(info.Ucc, ShimFor(info.Token));
        AddScript(info.Ucc, info.Lib);
        if (_netRec.Length > 0) AddScript(info.Ucc, _netRec);
    }

    private static string _netRec = "";

    /// <summary>DevTools opened / closed: every view (and every view made while it stays open) gets / drops the network
    /// recorder as a document-start script. The page already showing is handled by the driver's first poll.</summary>
    internal static void SetNetRecorder(string js)
    {
        if (_netRec == js) return;
        _netRec = js;
        foreach (var info in _views.Values) ApplyScripts(info);
    }

    /// <summary>Dark pages: the view reports a dark system appearance, so sites with a dark theme
    /// of their own (prefers-color-scheme) switch to it; chrome.js inverts the ones without.
    /// Off restores the light appearance every page gets by default.</summary>
    internal static void SetDarkLook(IntPtr view, bool dark)
    {
        if (!_views.TryGetValue(view, out var info) || info.Dark == dark) return;
        var look = ObjC.Send(ObjC.GetClass("NSAppearance"), ObjC.Sel("appearanceNamed:"),
            ObjC.NsString(dark ? "NSAppearanceNameDarkAqua" : "NSAppearanceNameAqua"));
        if (look == IntPtr.Zero) return;
        ObjC.Send(view, ObjC.Sel("setAppearance:"), look);
        info.Dark = dark;
    }

    /// <summary>The Fullscreen API (element.requestFullscreen). WKWebView ships with it off and
    /// Photino never turns it on, so only a bare &lt;video&gt; could go fullscreen: players whose
    /// button makes their own container fullscreen (most custom players) did nothing.
    /// Public since macOS 12.3; older systems have the private switch.</summary>
    private static void EnableElementFullscreen(IntPtr cfg)
    {
        var prefs = ObjC.Send(cfg, ObjC.Sel("preferences"));
        if (prefs == IntPtr.Zero) return;
        foreach (var name in new[] { "setElementFullscreenEnabled:", "_setFullScreenEnabled:" })
        {
            var sel = ObjC.Sel(name);
            if (ObjC.SendBool(prefs, ObjC.Sel("respondsToSelector:"), sel) == 0) continue;
            ObjC.SendNInt(prefs, sel, 1);
            return;
        }
    }

    private static readonly Dictionary<IntPtr, int> _fsWaits = new(); // main thread only
    private const int FullscreenWaitMs = 100, FullscreenWaitTicks = 1200;

    /// <summary>WKWebView.fullscreenState (macOS 12+): 0 not fullscreen, 1 entering, 2 fullscreen, 3 leaving — while it is not 0 WebKit has moved
    /// the page's view into its own screen-sized window and taken the browser window off screen, and only finishing the exit puts that
    /// back. -1 where the property does not exist (older macOS cannot tell).</summary>
    internal static int FullscreenState(IntPtr view)
    {
        if (view == IntPtr.Zero) return 0;
        var sel = ObjC.Sel("fullscreenState");
        if (ObjC.SendBool(view, ObjC.Sel("respondsToSelector:"), sel) == 0) return -1;
        return (int)ObjC.SendNInt(view, sel);
    }

    /// <summary>Tears a tab's view down completely (stops loads, drops scripts,
    /// releases the web content process).</summary>
    internal static void Destroy(IntPtr view)
    {
        if (view == IntPtr.Zero || !_views.ContainsKey(view)) return;
        // Never release a view while WebKit is entering, in or leaving fullscreen: its WKFullScreenWindowController then dies with a
        // transition still pending and WebKit asserts (a crash report: WebFullScreenManagerProxy::beganEnterFullScreen from
        // -[WKFullScreenWindowController dealloc] — e.g. when the window cannot animate because it is not on screen). A view torn
        // down at the end of one also leaves the browser window off screen. So wait for the transition to finish and destroy it then;
        // the page was asked to leave fullscreen already (BrowserController.ExitPageFullscreenAsync).
        if (FullscreenState(view) > 0 && Later is not null)
        {
            _fsWaits.TryGetValue(view, out var waited);
            if (waited < FullscreenWaitTicks)
            {
                _fsWaits[view] = waited + 1;
                Later(FullscreenWaitMs, () => Destroy(view));
                return;
            }
            // two minutes and WebKit never finished: releasing it now would crash the whole browser — leave this one view alone instead
            Console.Error.WriteLine("[BrAIn] a closed tab's fullscreen never finished; its view is kept so the browser survives");
            return;
        }
        _fsWaits.Remove(view);
        if (!_views.Remove(view, out var info)) return;
        try
        {
            // still on screen = the closed tab's page crossfading into its neighbour: it goes
            // dead now, but leaves the window only once that fade has run
            var onScreen = (ObjC.SendNInt(view, ObjC.Sel("isHidden")) & 0xFF) == 0 && Later is not null && !ReduceMotion();
            ObjC.Send(view, ObjC.Sel("stopLoading"));
            ObjC.Send(view, ObjC.Sel("setNavigationDelegate:"), IntPtr.Zero);
            ObjC.Send(view, ObjC.Sel("setUIDelegate:"), IntPtr.Zero);
            ObjC.Send(info.Ucc, ObjC.Sel("removeScriptMessageHandlerForName:"), ObjC.NsString("brain"));
            ObjC.Send(info.Ucc, ObjC.Sel("removeAllUserScripts"));
            void Remove()
            {
                try { ObjC.Send(view, ObjC.Sel("removeFromSuperview")); ObjC.Send(view, ObjC.Sel("release")); } catch { }
            }
            if (onScreen) Later!((int)(FadeSecs * 1000) + 40, Remove);
            else Remove();
        }
        catch { }
        lock (_byHandler) { _byHandler.Remove(info.Handler); _tokens.Remove(info.Handler); }
        if (Active == view) Active = IntPtr.Zero;
        if (info.Private && !info.Ghost && _privStore != IntPtr.Zero && !_views.Values.Any(v => v.Private && !v.Ghost))
        {
            ObjC.Send(_privStore, ObjC.Sel("release")); // ephemeral data goes with it
            _privStore = IntPtr.Zero;
        }
        if (info.Ghost && _ghostStore != IntPtr.Zero && !_views.Values.Any(v => v.Ghost))
        {
            ObjC.Send(_ghostStore, ObjC.Sel("release")); // the cookie copy and everything set since dies here
            _ghostStore = IntPtr.Zero;
        }
    }

    // ------------------------------------------------------------------ visibility / layout

    /// <summary>Runs an action on the main thread after a delay (set by the controller).
    /// Without it, a tab switch hides the old page at once instead of crossfading.</summary>
    internal static Action<int, Action>? Later;

    private const double FadeSecs = 0.18;
    private static int _switchGen;

    /// <summary>Makes <paramref name="view"/> the visible page view, crossfading from the
    /// page that was showing: whichever of the two sits higher in the view stack fades
    /// (the new page in, or the old page out), so the other is always fully drawn
    /// underneath and the dark window never flashes through. Keyboard focus only moves
    /// if it was inside a view that is being hidden (a focused address bar keeps its focus).</summary>
    internal static void Show(IntPtr nsWindow, IntPtr view)
    {
        if (view == IntPtr.Zero || !_views.ContainsKey(view)) return;
        var fr = nsWindow == IntPtr.Zero ? IntPtr.Zero : ObjC.Send(nsWindow, ObjC.Sel("firstResponder"));
        var focusWasInHidden = false;
        bool InsideView(IntPtr v) =>
            fr != IntPtr.Zero && fr != nsWindow &&
            ObjC.SendBool(fr, ObjC.Sel("respondsToSelector:"), ObjC.Sel("isDescendantOf:")) != 0 &&
            ObjC.SendBool(fr, ObjC.Sel("isDescendantOf:"), v) != 0;
        bool Hidden(IntPtr v) => (ObjC.SendNInt(v, ObjC.Sel("isHidden")) & 0xFF) != 0;
        var wasHidden = Hidden(view);
        var prev = Active != view && _views.ContainsKey(Active) && !Hidden(Active) ? Active : IntPtr.Zero;
        ClearFade(view);
        ObjC.SendNInt(view, ObjC.Sel("setHidden:"), 0);
        var still = ReduceMotion();
        var crossfade = wasHidden && prev != IntPtr.Zero && Later is not null && !still;
        if (crossfade && StackIndex(view) < StackIndex(prev)) FadeOut(prev); // the old page is on top: it fades away
        else if (wasHidden && !still) FadeIn(view);
        foreach (var v in _views.Keys)
        {
            if (v == view) continue;
            if (InsideView(v)) focusWasInHidden = true;
            if (crossfade && v == prev) continue; // hidden once the fade is done
            ObjC.SendNInt(v, ObjC.Sel("setHidden:"), 1);
            ClearFade(v);
        }
        // the window starts with focus in Photino's (now hidden) bootstrap view
        if (_boot != IntPtr.Zero && (fr == _boot || InsideView(_boot))) focusWasInHidden = true;
        Active = view;
        if (focusWasInHidden) ObjC.Send(nsWindow, ObjC.Sel("makeFirstResponder:"), view);
        if (crossfade)
        {
            var gen = ++_switchGen;
            Later!((int)(FadeSecs * 1000) + 40, () =>
            {
                if (gen != _switchGen || !_views.ContainsKey(prev) || prev == Active) return;
                ObjC.SendNInt(prev, ObjC.Sel("setHidden:"), 1);
                ClearFade(prev);
            });
        }
    }

    /// <summary>System Settings → Accessibility → Display → Reduce motion.</summary>
    private static bool ReduceMotion()
    {
        try
        {
            var ws = ObjC.Send(ObjC.GetClass("NSWorkspace"), ObjC.Sel("sharedWorkspace"));
            return ws != IntPtr.Zero && (ObjC.SendNInt(ws, ObjC.Sel("accessibilityDisplayShouldReduceMotion")) & 0xFF) != 0;
        }
        catch { return false; }
    }

    /// <summary>Position of a page view among its window's subviews (higher draws on top).</summary>
    private static nint StackIndex(IntPtr view)
    {
        var sup = ObjC.Send(view, ObjC.Sel("superview"));
        if (sup == IntPtr.Zero) return 0;
        var subs = ObjC.Send(sup, ObjC.Sel("subviews"));
        return subs == IntPtr.Zero ? 0 : ObjC.SendNInt(subs, ObjC.Sel("indexOfObject:"), view);
    }

    private static IntPtr FadeAnim(string keyPath, double from, double to, double dur, string timingName)
    {
        var num = ObjC.GetClass("NSNumber");
        var a = ObjC.Send(ObjC.GetClass("CABasicAnimation"), ObjC.Sel("animationWithKeyPath:"), ObjC.NsString(keyPath));
        ObjC.Send(a, ObjC.Sel("setFromValue:"), ObjC.SendDoubleArg(num, ObjC.Sel("numberWithDouble:"), from));
        ObjC.Send(a, ObjC.Sel("setToValue:"), ObjC.SendDoubleArg(num, ObjC.Sel("numberWithDouble:"), to));
        ObjC.SendDoubleArg(a, ObjC.Sel("setDuration:"), dur);
        var timing = ObjC.Send(ObjC.GetClass("CAMediaTimingFunction"), ObjC.Sel("functionWithName:"), ObjC.NsString(timingName));
        if (timing != IntPtr.Zero) ObjC.Send(a, ObjC.Sel("setTimingFunction:"), timing);
        return a;
    }

    /// <summary>The outgoing page fades to nothing and stays there (fill forwards) until
    /// it is hidden; <see cref="ClearFade"/> removes that before the view is shown again.</summary>
    private static void FadeOut(IntPtr view)
    {
        try
        {
            var layer = ObjC.Send(view, ObjC.Sel("layer"));
            if (layer == IntPtr.Zero) return;
            var a = FadeAnim("opacity", 1, 0, FadeSecs, "easeInEaseOut");
            ObjC.Send(a, ObjC.Sel("setFillMode:"), ObjC.NsString("forwards"));
            ObjC.SendNInt(a, ObjC.Sel("setRemovedOnCompletion:"), 0);
            ObjC.Send(layer, ObjC.Sel("addAnimation:forKey:"), a, ObjC.NsString("brainOut"));
        }
        catch { /* purely cosmetic */ }
    }

    private static void ClearFade(IntPtr view)
    {
        try
        {
            var layer = ObjC.Send(view, ObjC.Sel("layer"));
            if (layer != IntPtr.Zero) ObjC.Send(layer, ObjC.Sel("removeAnimationForKey:"), ObjC.NsString("brainOut"));
        }
        catch { }
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
            ObjC.Send(layer, ObjC.Sel("addAnimation:forKey:"), FadeAnim("opacity", 0, 1, FadeSecs, "easeOut"), ObjC.NsString("brainFade"));
            ObjC.Send(layer, ObjC.Sel("addAnimation:forKey:"), FadeAnim("transform.translation.y", 8, 0, 0.22, "easeOut"), ObjC.NsString("brainSettle"));
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
        var home = _boot == IntPtr.Zero ? IntPtr.Zero : ObjC.Send(_boot, ObjC.Sel("window"));
        void Set(IntPtr v)
        {
            if (v == IntPtr.Zero) return;
            // a page in element fullscreen lives in WebKit's own fullscreen window until it exits
            if (home != IntPtr.Zero && ObjC.Send(v, ObjC.Sel("window")) is var w && w != IntPtr.Zero && w != home) return;
            var cur = ObjC.SendRect(v, ObjC.Sel("frame"));
            if (Math.Abs(cur.X - want.X) > 0.5 || Math.Abs(cur.Y - want.Y) > 0.5 ||
                Math.Abs(cur.W - want.W) > 0.5 || Math.Abs(cur.H - want.H) > 0.5)
                ObjC.SendRectArg(v, ObjC.Sel("setFrame:"), want);
        }
        foreach (var v in _views.Keys) Set(v);
        Set(_boot);
    }

    /// <summary>The bootstrap view, so the crash-reload handler can leave it alone (see <see cref="HideBootstrap"/>).</summary>
    internal static IntPtr BootView => _boot;

    /// <summary>Hides the bootstrap view (it only exists to host the window and as the configuration template) and lets go of its
    /// web-content process. Photino loaded a blank page into it, which starts a WebContent process of its own — 28 to 47 MB for a
    /// view nobody ever sees. The view itself stays (tabs copy its configuration, popups reuse its delegate); only its process goes,
    /// and WebKit starts a new one only if something loads into it again. -[WKWebView _killWebContentProcessAndResetState] is WebKit
    /// SPI, so it is used only where the view answers it.</summary>
    internal static void HideBootstrap()
    {
        if (_boot == IntPtr.Zero) return;
        ObjC.SendNInt(_boot, ObjC.Sel("setHidden:"), 1);
        try
        {
            var sel = ObjC.Sel("_killWebContentProcessAndResetState");
            if (ObjC.SendBool(_boot, ObjC.Sel("respondsToSelector:"), sel) != 0) ObjC.Send(_boot, sel);
        }
        catch { }
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
            0x11 / 255.0, 0x0f / 255.0, 0x0d / 255.0, 1.0);
        if (color != IntPtr.Zero) ObjC.Send(nsWindow, ObjC.Sel("setBackgroundColor:"), color);
    }
}
