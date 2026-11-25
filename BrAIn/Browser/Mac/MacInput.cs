using System.Runtime.InteropServices;

namespace BrAIn.Browser.Mac;

/// <summary>Mouse input for the page views (macOS). Everything here is an NSEvent handed straight to the
/// page's own view: WebKit sees a genuine, trusted user event (so it carries user activation — passkeys,
/// popups from click handlers, clipboard), but nothing goes through the window server. The pointer of the
/// person at the keyboard never moves, no other app is raised or clicked, no Accessibility permission is
/// needed, and it behaves the same whatever is stacked above BrAIn's window or when BrAIn is in the background.
/// (An earlier version posted CGEvents at the HID tap — the same path as hardware — which lands on whatever
/// app is topmost at that screen point: an agent click went into the app the person was working in.)
/// All ObjC/AppKit calls must run on the main thread.</summary>
internal static partial class MacInput
{
    // NSEventType
    private const int LeftDown = 1, LeftUp = 2, RightDown = 3, RightUp = 4, Moved = 5, OtherDown = 25, OtherUp = 26;
    private const int BtnRight = 1, BtnOther = 2;

    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static partial IntPtr SendPointRetObj(IntPtr recv, IntPtr sel, MacWindow.CGPoint p);

    private static IntPtr NewEvent(IntPtr nsWindow, MacWindow.CGPoint windowPoint, int type, int clicks)
    {
        var now = ObjC.SendDouble(ObjC.Send(ObjC.GetClass("NSProcessInfo"), ObjC.Sel("processInfo")), ObjC.Sel("systemUptime"));
        return ObjC.SendMouseEvent(ObjC.GetClass("NSEvent"), ObjC.Sel(
            "mouseEventWithType:location:modifierFlags:timestamp:windowNumber:context:eventNumber:clickCount:pressure:"),
            type, windowPoint, 0, now, (nint)MacWindow.WindowNumber(nsWindow), IntPtr.Zero, 0,
            type == Moved ? 0 : Math.Max(1, clicks), type is LeftDown or RightDown or OtherDown ? 1f : 0f);
    }

    /// <summary>Point of <paramref name="view"/> (CSS points, top-left origin) → window base coordinates.</summary>
    private static MacWindow.CGPoint ToWindow(IntPtr view, double x, double y)
        => ObjC.SendPointFromView(view, ObjC.Sel("convertPoint:toView:"), new MacWindow.CGPoint { X = x, Y = y }, IntPtr.Zero);

    /// <summary>The view that takes the event: the part of the page's own view under the point (the way AppKit
    /// would find it), else the page view itself — a point outside the page, or a view that is not shown, still
    /// ends up there and nowhere else.</summary>
    private static IntPtr Target(IntPtr view, MacWindow.CGPoint windowPoint)
    {
        var sup = ObjC.Send(view, ObjC.Sel("superview"));
        var inSuper = sup == IntPtr.Zero ? windowPoint
            : ObjC.SendPointFromView(sup, ObjC.Sel("convertPoint:fromView:"), windowPoint, IntPtr.Zero);
        var hit = SendPointRetObj(view, ObjC.Sel("hitTest:"), inSuper);
        return hit != IntPtr.Zero ? hit : view;
    }

    private static void Send(IntPtr target, string selector, IntPtr ev)
    {
        if (ev != IntPtr.Zero) ObjC.Send(target, ObjC.Sel(selector), ev);
    }

    /// <summary>Moves the (virtual) pointer over a point of the page: hover states, tooltips, menus open on it.
    /// The real pointer stays where it is. Main-thread only.</summary>
    public static void MoveTo(IntPtr nsWindow, IntPtr view, double x, double y)
    {
        if (view == IntPtr.Zero) return;
        var win = ObjC.Send(view, ObjC.Sel("window")); if (win == IntPtr.Zero) win = nsWindow; if (win == IntPtr.Zero) return;
        var wp = ToWindow(view, x, y);
        Send(Target(view, wp), "mouseMoved:", NewEvent(win, wp, Moved, 0));
    }

    /// <summary>Click at a point of the page: moves over it, then press and release.
    /// <paramref name="clickCount"/> = 2 performs a double click. Main-thread only.</summary>
    public static void Click(IntPtr nsWindow, IntPtr view, double x, double y, int button = 0, int clickCount = 1)
    {
        if (view == IntPtr.Zero) return;
        var win = ObjC.Send(view, ObjC.Sel("window")); if (win == IntPtr.Zero) win = nsWindow; if (win == IntPtr.Zero) return;
        var wp = ToWindow(view, x, y);
        var target = Target(view, wp);
        var (down, up, downSel, upSel) = button switch
        {
            BtnRight => (RightDown, RightUp, "rightMouseDown:", "rightMouseUp:"),
            BtnOther => (OtherDown, OtherUp, "otherMouseDown:", "otherMouseUp:"),
            _ => (LeftDown, LeftUp, "mouseDown:", "mouseUp:"),
        };
        Send(target, "mouseMoved:", NewEvent(win, wp, Moved, 0));
        // like a press, the page takes the keyboard (the window is not raised and the app is not activated)
        ObjC.Send(win, ObjC.Sel("makeFirstResponder:"), target);
        for (var n = 1; n <= Math.Clamp(clickCount, 1, 2); n++)
        {
            Send(target, downSel, NewEvent(win, wp, down, n));
            Send(target, upSel, NewEvent(win, wp, up, n));
        }
    }

    /// <summary>One mouse event delivered straight to the window as an NSEvent: type 5 = moved,
    /// 1 = left down, 2 = left up. Main thread only.</summary>
    public static void SendMouseEvent(IntPtr nsWindow, IntPtr view, double x, double y, int type)
    {
        var ev = NewEvent(nsWindow, ToWindow(view, x, y), type, 1);
        if (ev != IntPtr.Zero) ObjC.Send(nsWindow, ObjC.Sel("sendEvent:"), ev);
    }
}
