using System.Runtime.InteropServices;

namespace BrAIn.Browser.Mac;

/// <summary>
/// macOS window/webview discovery + window capture.
/// Everything here must be invoked on the main thread (via Photino's Invoke).
/// </summary>
internal static partial class MacWindow
{
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    [StructLayout(LayoutKind.Sequential)]
    internal struct CGRect
    {
        public double X, Y, W, H;
        internal static readonly CGRect Null = new() { X = double.PositiveInfinity, Y = double.PositiveInfinity, W = 0, H = 0 };
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CGPoint
    {
        public double X, Y;
        internal CGPoint(double x, double y) { X = x; Y = y; }
    }

    [LibraryImport(CoreGraphics)]
    private static partial IntPtr CGWindowListCreateImage(CGRect bounds, uint listOption, uint windowId, uint imageOption);

    [LibraryImport(CoreGraphics)]
    private static partial IntPtr CGImageCreateWithImageInRect(IntPtr image, CGRect rect);

    [LibraryImport(CoreGraphics)]
    private static partial void CFRelease(IntPtr obj);

    private const uint KCGWindowListOptionIncludingWindow = 1 << 0;
    private const uint KCGWindowImageBoundsIgnoreFraming = 1 << 0;

    /// <summary>NSApplication.sharedApplication.windows</summary>
    internal static IntPtr[] AllWindows()
    {
        var nsApp = ObjC.Send(ObjC.GetClass("NSApplication"), ObjC.Sel("sharedApplication"));
        var windows = ObjC.Send(nsApp, ObjC.Sel("windows"));
        var count = ObjC.ArrayCount(windows);
        var result = new IntPtr[count];
        for (nint i = 0; i < count; i++)
            result[i] = ObjC.ArrayAt(windows, i);
        return result;
    }

    internal static long WindowNumber(IntPtr nsWindow)
        => (long)ObjC.SendNInt(nsWindow, ObjC.Sel("windowNumber"));

    /// <summary>Finds the WKWebView inside a window's view hierarchy, or IntPtr.Zero.</summary>
    internal static IntPtr FindWebView(IntPtr nsWindow)
    {
        var contentView = ObjC.Send(nsWindow, ObjC.Sel("contentView"));
        if (contentView == IntPtr.Zero) return IntPtr.Zero;
        return FindWebViewInView(contentView);
    }

    private static IntPtr FindWebViewInView(IntPtr view)
    {
        if (ObjC.IsKindOf(view, "WKWebView")) return view;
        var subviews = ObjC.Send(view, ObjC.Sel("subviews"));
        var count = ObjC.ArrayCount(subviews);
        for (nint i = 0; i < count; i++)
        {
            var found = FindWebViewInView(ObjC.ArrayAt(subviews, i));
            if (found != IntPtr.Zero) return found;
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// WKWebView ships with picture-in-picture off for embedders — the video
    /// element reports webkitSupportsPresentationMode('picture-in-picture')
    /// == false and requestPictureInPicture() throws NotSupportedError. The
    /// switch is a private WKPreferences property (Safari sets it itself).
    /// Guarded with respondsToSelector so a future WebKit that drops it
    /// degrades to "no PiP" instead of crashing.
    /// </summary>
    internal static void EnablePictureInPicture(IntPtr webview)
    {
        var cfg = ObjC.Send(webview, ObjC.Sel("configuration"));
        var prefs = cfg == IntPtr.Zero ? IntPtr.Zero : ObjC.Send(cfg, ObjC.Sel("preferences"));
        if (prefs == IntPtr.Zero) return;
        var setter = ObjC.Sel("_setAllowsPictureInPictureMediaPlayback:");
        if (ObjC.SendBool(prefs, ObjC.Sel("respondsToSelector:"), setter) == 0) return;
        ObjC.Send(prefs, setter, (IntPtr)1);
    }

    /// <summary>windowWithWindowNumber: on NSApp.</summary>
    internal static IntPtr WindowByNumber(long number)
    {
        var nsApp = ObjC.Send(ObjC.GetClass("NSApplication"), ObjC.Sel("sharedApplication"));
        return ObjC.Send(nsApp, ObjC.Sel("windowWithWindowNumber:"), (nint)number);
    }

    /// <summary>Raise a tab's window by its remembered window number.</summary>
    internal static void Activate(BrowserTab tab)
    {
        var win = tab.MacWindowNumber != 0 ? WindowByNumber(tab.MacWindowNumber) : IntPtr.Zero;
        if (win != IntPtr.Zero) Activate(win);
    }

    /// <summary>Raise a window and activate the app.</summary>
    internal static void Activate(IntPtr nsWindow)
    {
        if (nsWindow == IntPtr.Zero) return;
        var nsApp = ObjC.Send(ObjC.GetClass("NSApplication"), ObjC.Sel("sharedApplication"));
        // macOS 14+: activate; fall back to activateIgnoringOtherApps: on older versions.
        if (ObjC.SendBool(nsApp, ObjC.Sel("respondsToSelector:"), ObjC.Sel("activate")) != 0)
            ObjC.Send(nsApp, ObjC.Sel("activate"));
        else
            ObjC.Send(nsApp, ObjC.Sel("activateIgnoringOtherApps:"), (IntPtr)1);
        ObjC.Send(nsWindow, ObjC.Sel("makeKeyAndOrderFront:"), IntPtr.Zero);
        ObjC.Send(nsWindow, ObjC.Sel("orderFrontRegardless"));
    }

    /// <summary>Current backing scale factor (pixels per point) for a window.</summary>
    internal static double BackingScale(IntPtr nsWindow)
        => ObjC.SendDouble(nsWindow, ObjC.Sel("backingScaleFactor"));

    // ---------------- fullscreen ----------------

    private const nint NSWindowStyleMaskFullScreen = 1 << 14;

    /// <summary>Standard Space-based fullscreen (what ⌃⌘F triggers). Photino's
    /// own FullScreen path instead calls the deprecated
    /// -[NSView enterFullScreenMode:] on the contentView — no real Space is
    /// created, so Mission Control / app switching gestures get trapped and
    /// the view's frame comes back corrupted (page renders offset with blank
    /// space on one side). Route all fullscreen through this instead.</summary>
    internal static bool IsFullScreen(IntPtr nsWindow)
        => (ObjC.SendNInt(nsWindow, ObjC.Sel("styleMask")) & NSWindowStyleMaskFullScreen) != 0;

    internal static void ToggleFullScreen(IntPtr nsWindow)
        => ObjC.Send(nsWindow, ObjC.Sel("toggleFullScreen:"), IntPtr.Zero);

    /// <summary>Exits a legacy -[NSView enterFullScreenMode:] presentation if
    /// one is active (windows already stuck from the old Photino path).
    /// Returns true if a legacy presentation was exited.</summary>
    internal static bool ExitLegacyFullScreen(IntPtr nsWindow)
    {
        var contentView = ObjC.Send(nsWindow, ObjC.Sel("contentView"));
        if (contentView == IntPtr.Zero) return false;
        if (ObjC.SendNInt(contentView, ObjC.Sel("isInFullScreenMode")) == 0) return false;
        ObjC.Send(contentView, ObjC.Sel("exitFullScreenModeWithOptions:"), IntPtr.Zero);
        return true;
    }

    /// <summary>WKWebView.isLoading — the native load flag (a BOOL, so only the low byte is meaningful).</summary>
    internal static bool IsWebViewLoading(IntPtr webview) => (ObjC.SendNInt(webview, ObjC.Sel("isLoading")) & 0xFF) != 0;

    /// <summary>
    /// Page-level mute (private WKWebView API Safari uses for its tab speaker
    /// button). Covers every frame, WebAudio and media elements alike, and
    /// survives navigations. _WKMediaAudioMuted = 1&lt;&lt;0.
    /// </summary>
    internal static void SetWebViewMuted(IntPtr webview, bool muted)
    {
        var sel = ObjC.Sel("_setPageMuted:");
        if (webview == IntPtr.Zero || ObjC.SendBool(webview, ObjC.Sel("respondsToSelector:"), sel) == 0) return;
        ObjC.SendNInt(webview, sel, muted ? 1 : 0);
    }

    /// <summary>WKWebView._isPlayingAudio — true while any frame is producing sound (BOOL: low byte only).</summary>
    internal static bool IsWebViewPlayingAudio(IntPtr webview)
    {
        var sel = ObjC.Sel("_isPlayingAudio");
        if (webview == IntPtr.Zero || ObjC.SendBool(webview, ObjC.Sel("respondsToSelector:"), sel) == 0) return false;
        return (ObjC.SendNInt(webview, sel) & 0xFF) != 0;
    }

    /// <summary>WKWebView.URL.absoluteString — reads the active document URL natively with zero JS overhead.</summary>
    internal static string? GetWebViewUrl(IntPtr webview)
    {
        if (webview == IntPtr.Zero) return null;
        var nsUrl = ObjC.Send(webview, ObjC.Sel("URL"));
        if (nsUrl == IntPtr.Zero) return null;
        var abs = ObjC.Send(nsUrl, ObjC.Sel("absoluteString"));
        return ObjC.NsStringToString(abs);
    }

    /// <summary>WKWebView.title — reads the active document title natively with zero JS overhead.</summary>
    internal static string? GetWebViewTitle(IntPtr webview)
    {
        if (webview == IntPtr.Zero) return null;
        var nsTitle = ObjC.Send(webview, ObjC.Sel("title"));
        return ObjC.NsStringToString(nsTitle);
    }

    private const string LoadingBarId = "BrAInLoadingBar";
    private const nint NSViewWidthSizable = 2, NSViewMinYMargin = 8;

    /// <summary>Native load indicator: a Core Animation sweep on a thin view at the
    /// toolbar's bottom edge, above the web view. WKWebView freezes page rendering
    /// while a navigation is pending, so a bar drawn in the page never paints until
    /// the new document commits; layers animate on the render server instead.</summary>
    internal static void SetLoadingBar(IntPtr nsWindow, bool on)
    {
        var content = ObjC.Send(nsWindow, ObjC.Sel("contentView"));
        if (content == IntPtr.Zero) return;
        var bar = FindLoadingBar(content);
        if (!on)
        {
            if (bar != IntPtr.Zero) ObjC.SendNInt(bar, ObjC.Sel("setHidden:"), 1);
            return;
        }
        if (bar == IntPtr.Zero) bar = CreateLoadingBar(content);

        var b = ObjC.SendRect(content, ObjC.Sel("bounds"));
        ObjC.SendRectArg(bar, ObjC.Sel("setFrame:"), new CGRect { X = 0, Y = b.H - 46, W = b.W, H = 2 }); // 46 = toolbar height
        var root = ObjC.Send(bar, ObjC.Sel("layer"));
        var seg = ObjC.ArrayAt(ObjC.Send(root, ObjC.Sel("sublayers")), 0);
        var w = b.W * 0.38;
        ObjC.SendRectArg(seg, ObjC.Sel("setFrame:"), new CGRect { W = w, H = 2 });
        ObjC.Send(seg, ObjC.Sel("removeAnimationForKey:"), ObjC.NsString("sweep"));
        var anim = ObjC.Send(ObjC.GetClass("CABasicAnimation"), ObjC.Sel("animationWithKeyPath:"), ObjC.NsString("position.x"));
        ObjC.Send(anim, ObjC.Sel("setFromValue:"), ObjC.SendDoubleArg(ObjC.GetClass("NSNumber"), ObjC.Sel("numberWithDouble:"), -w / 2));
        ObjC.Send(anim, ObjC.Sel("setToValue:"), ObjC.SendDoubleArg(ObjC.GetClass("NSNumber"), ObjC.Sel("numberWithDouble:"), b.W + w / 2));
        ObjC.SendDoubleArg(anim, ObjC.Sel("setDuration:"), 1.2);
        ObjC.SendDoubleArg(anim, ObjC.Sel("setRepeatDuration:"), 1e9);
        ObjC.Send(seg, ObjC.Sel("addAnimation:forKey:"), anim, ObjC.NsString("sweep"));
        ObjC.SendNInt(bar, ObjC.Sel("setHidden:"), 0);
    }

    private static IntPtr FindLoadingBar(IntPtr content)
    {
        var subs = ObjC.Send(content, ObjC.Sel("subviews"));
        for (nint i = 0; i < ObjC.ArrayCount(subs); i++)
        {
            var v = ObjC.ArrayAt(subs, i);
            var id = ObjC.Send(v, ObjC.Sel("identifier"));
            if (id != IntPtr.Zero && ObjC.SendBool(id, ObjC.Sel("isEqualToString:"), ObjC.NsString(LoadingBarId)) != 0) return v;
        }
        return IntPtr.Zero;
    }

    private static IntPtr CreateLoadingBar(IntPtr content)
    {
        var view = ObjC.Send(ObjC.GetClass("NSView"), ObjC.Sel("new"));
        ObjC.Send(view, ObjC.Sel("setIdentifier:"), ObjC.NsString(LoadingBarId));
        ObjC.SendNInt(view, ObjC.Sel("setWantsLayer:"), 1);
        ObjC.SendNInt(view, ObjC.Sel("setAutoresizingMask:"), NSViewWidthSizable | NSViewMinYMargin);
        ObjC.Send(content, ObjC.Sel("addSubview:"), view);
        var root = ObjC.Send(view, ObjC.Sel("layer"));
        ObjC.SendNInt(root, ObjC.Sel("setMasksToBounds:"), 1);
        var seg = ObjC.Send(ObjC.GetClass("CALayer"), ObjC.Sel("new"));
        var color = ObjC.Send(ObjC.Send(ObjC.GetClass("NSColor"), ObjC.Sel("systemPurpleColor")), ObjC.Sel("CGColor"));
        ObjC.Send(seg, ObjC.Sel("setBackgroundColor:"), color);
        ObjC.Send(root, ObjC.Sel("addSublayer:"), seg);
        return view;
    }

    /// <summary>Snaps the webview's frame to its superview bounds — repairs the
    /// offset/blank-region left behind when a legacy fullscreen round-trip (or
    /// a display move) corrupts the view's frame. No-op when geometry is sane.</summary>
    internal static void RepairWebViewFrame(IntPtr nsWindow)
    {
        var webview = FindWebView(nsWindow);
        if (webview == IntPtr.Zero) return;
        var parent = ObjC.Send(webview, ObjC.Sel("superview"));
        if (parent == IntPtr.Zero) return;
        ObjC.SendRectArg(webview, ObjC.Sel("setFrame:"), ObjC.SendRect(parent, ObjC.Sel("bounds")));
    }

    /// <summary>The tab's NSWindow — by remembered window number, falling back
    /// to the first app window that hosts a WKWebView.</summary>
    internal static IntPtr NsWindowForTab(BrowserTab tab)
    {
        var win = tab.MacWindowNumber != 0 ? WindowByNumber(tab.MacWindowNumber) : IntPtr.Zero;
        if (win != IntPtr.Zero) return win;
        foreach (var w in AllWindows())
            if (FindWebView(w) != IntPtr.Zero) return w;
        return IntPtr.Zero;
    }

    // ---------------- inline titlebar (traffic lights inside the toolbar) ----------------

    private const nint NSWindowStyleMaskFullSizeContentView = 1 << 15;

    /// <summary>Merges the titlebar into the content area: the content view
    /// extends under a transparent, untitled titlebar so the real traffic
    /// lights float over the top-left of the toolbar — the Safari/Electron
    /// "hiddenInset" look, with genuine OS buttons and behaviors.</summary>
    internal static void ApplyInlineTitlebar(IntPtr nsWindow)
    {
        var mask = ObjC.SendNInt(nsWindow, ObjC.Sel("styleMask"));
        ObjC.SendNInt(nsWindow, ObjC.Sel("setStyleMask:"), mask | NSWindowStyleMaskFullSizeContentView);
        ObjC.SendNInt(nsWindow, ObjC.Sel("setTitlebarAppearsTransparent:"), 1);
        ObjC.SendNInt(nsWindow, ObjC.Sel("setTitleVisibility:"), 1); // NSWindowTitleHidden
        // Deliberately NOT setting movableByWindowBackground — it would make
        // drags anywhere in the webview move the window (text selection,
        // link drags). Bar drags go through BeginWindowDrag instead.
        CenterTrafficLights(nsWindow, 23); // toolbar is 46px tall → center = 23
    }

    /// <summary>Recenters the traffic-light buttons on the toolbar's vertical
    /// center (default OS inset sits higher). Same trick as Electron's
    /// trafficLightPosition — nudge each standardWindowButton's frame.</summary>
    internal static void CenterTrafficLights(IntPtr nsWindow, double centerFromTop)
    {
        var frame = ObjC.SendRect(nsWindow, ObjC.Sel("frame"));
        var winY = frame.H - centerFromTop; // window-base coords (bottom-left origin)
        for (nint i = 0; i <= 2; i++) // Close=0, Miniaturize=1, Zoom=2
        {
            var btn = ObjC.SendNInt(nsWindow, ObjC.Sel("standardWindowButton:"), i);
            if (btn == IntPtr.Zero) continue;
            var sv = ObjC.Send(btn, ObjC.Sel("superview"));
            if (sv == IntPtr.Zero) continue;
            var f = ObjC.SendRect(btn, ObjC.Sel("frame"));
            var target = ObjC.SendPointFromView(sv, ObjC.Sel("convertPoint:fromView:"),
                new CGPoint(f.X + f.W / 2, winY), IntPtr.Zero);
            ObjC.SendPointArg(btn, ObjC.Sel("setFrameOrigin:"),
                new CGPoint(f.X, target.Y - f.H / 2));
        }
    }

    /// <summary>Starts a window move tracking the real cursor — the JS toolbar
    /// calls this on mousedown in empty bar space (the transparent titlebar
    /// only owns its own strip natively). Synthesizes a left-mouse-down at the
    /// cursor and hands it to the window's standard drag loop, which then
    /// follows live mouse events until release.</summary>
    internal static void BeginWindowDrag(IntPtr nsWindow)
    {
        // Only start while the physical left button is actually held — the JS
        // path fires on real mousedowns, but a stray/dispatched call would
        // otherwise leave the window tracking a drag that never ends.
        var pressed = ObjC.SendNInt(ObjC.GetClass("NSEvent"), ObjC.Sel("pressedMouseButtons"));
        if ((pressed & 1) == 0) return;
        var screen = ObjC.SendPoint(ObjC.GetClass("NSEvent"), ObjC.Sel("mouseLocation"));
        var winPt = ObjC.SendPointRet(nsWindow, ObjC.Sel("convertPointFromScreen:"), screen);
        var ev = ObjC.SendMouseEvent(ObjC.GetClass("NSEvent"), ObjC.Sel(
            "mouseEventWithType:location:modifierFlags:timestamp:windowNumber:context:eventNumber:clickCount:pressure:"),
            1 /* NSEventTypeLeftMouseDown */, winPt, 0, 0.0,
            (nint)WindowNumber(nsWindow), IntPtr.Zero, 0, 1, 1.0f);
        if (ev != IntPtr.Zero)
            ObjC.Send(nsWindow, ObjC.Sel("performWindowDragWithEvent:"), ev);
    }

    /// <summary>Capture the window's content (without frame) as PNG bytes.
    /// clipPx: optional crop rect in *image pixels*.</summary>
    internal static byte[]? CapturePng(IntPtr nsWindow, CGRect? clipPx = null)
    {
        var number = WindowNumber(nsWindow);
        if (number <= 0) return null;

        var image = CGWindowListCreateImage(CGRect.Null, KCGWindowListOptionIncludingWindow,
            (uint)number, KCGWindowImageBoundsIgnoreFraming);
        if (image == IntPtr.Zero) return null;

        try
        {
            if (clipPx is { } c)
            {
                var cropped = CGImageCreateWithImageInRect(image, c);
                CFRelease(image);
                if (cropped == IntPtr.Zero) return null;
                image = cropped;
            }

            var rep = ObjC.Send(ObjC.GetClass("NSBitmapImageRep"), ObjC.Sel("alloc"));
            rep = ObjC.Send(rep, ObjC.Sel("initWithCGImage:"), image);
            if (rep == IntPtr.Zero) return null;

            // NSPNGFileType = 4
            var data = ObjC.Send(rep, ObjC.Sel("representationUsingType:properties:"),
                (nint)4, IntPtr.Zero);
            ObjC.Send(rep, ObjC.Sel("release"));
            if (data == IntPtr.Zero) return null;

            var len = (int)(long)ObjC.SendNInt(data, ObjC.Sel("length"));
            var ptr = ObjC.Send(data, ObjC.Sel("bytes"));
            if (len <= 0 || ptr == IntPtr.Zero) return null;

            var bytes = new byte[len];
            Marshal.Copy(ptr, bytes, 0, len);
            return bytes;
        }
        finally
        {
            CFRelease(image);
        }
    }
}
