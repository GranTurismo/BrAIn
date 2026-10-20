using System.Runtime.InteropServices;

namespace BrAIn.Browser.Mac;

/// <summary>Real OS-level mouse input (CGEvent — the same path as hardware input).
/// Unlike JS-dispatched events these carry user activation, so gesture-gated page
/// features (WebAuthn/passkeys, popup windows from click handlers, clipboard) see
/// them as genuine user gestures. All ObjC/AppKit calls inside must run on the
/// main thread; the CGEvent posts themselves are thread-safe.</summary>
internal static partial class MacInput
{
    private const string CG = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    [StructLayout(LayoutKind.Sequential)]
    private struct CGPoint { public double X, Y; }

    [LibraryImport(CG, EntryPoint = "CGEventCreateMouseEvent")]
    private static partial IntPtr CreateMouseEvent(IntPtr source, int type, CGPoint pos, int button);
    [LibraryImport(CG, EntryPoint = "CGEventPost")]
    private static partial void Post(int tap, IntPtr evt);
    [LibraryImport(CG, EntryPoint = "CGEventSetIntegerValueField")]
    private static partial void SetIntField(IntPtr evt, int field, long value);
    [LibraryImport(CG, EntryPoint = "CFRelease")]
    private static partial void CFRelease(IntPtr obj);
    [LibraryImport(CG, EntryPoint = "CGMainDisplayID")]
    private static partial uint MainDisplayID();
    [LibraryImport(CG, EntryPoint = "CGDisplayPixelsHigh")]
    private static partial nuint DisplayPixelsHigh(uint display);

    private const int HIDTap = 0;
    private const int LeftDown = 1, LeftUp = 2, RightDown = 3, RightUp = 4;
    private const int Moved = 5, LeftDragged = 6, RightDragged = 7;
    private const int OtherDown = 25, OtherUp = 26;
    private const int ClickStateField = 22; // kCGMouseEventClickState
    private const int BtnLeft = 0, BtnRight = 1, BtnOther = 2;

    /// <summary>Converts a point in <paramref name="view"/> coordinates (CSS points,
    /// top-left origin) to global CG display coordinates (top-left origin).
    /// Main-thread only.</summary>
    private static CGPoint ViewToScreen(IntPtr nsWindow, IntPtr view, double x, double y)
    {
        // view → window → Cocoa screen (bottom-left) → CG screen (top-left).
        var p = ObjC.SendPointFromView(view, ObjC.Sel("convertPoint:toView:"),
            new MacWindow.CGPoint { X = x, Y = y }, IntPtr.Zero);
        var sp = ObjC.SendPointRet(nsWindow, ObjC.Sel("convertPointToScreen:"), p);
        var h = (double)DisplayPixelsHigh(MainDisplayID());
        return new CGPoint { X = sp.X, Y = h - sp.Y };
    }

    private static void Post(int type, CGPoint pos, int button, int clickState = 0)
    {
        var e = CreateMouseEvent(IntPtr.Zero, type, pos, button);
        if (e == IntPtr.Zero) return;
        if (clickState > 0) SetIntField(e, ClickStateField, clickState);
        Post(HIDTap, e);
        CFRelease(e);
    }

    /// <summary>Moves the OS pointer to a point in view coordinates. Main-thread only.</summary>
    public static void MoveTo(IntPtr nsWindow, IntPtr view, double x, double y)
        => Post(Moved, ViewToScreen(nsWindow, view, x, y), BtnLeft);

    /// <summary>Real click at a point in view coordinates: moves, then down/up.
    /// <paramref name="clickCount"/> = 2 performs a double click.
    /// Call on the main thread (AppKit conversion happens here).</summary>
    public static void Click(IntPtr nsWindow, IntPtr view, double x, double y, int button = BtnLeft, int clickCount = 1)
    {
        var pos = ViewToScreen(nsWindow, view, x, y);
        var (down, up, dragged) = button switch
        {
            BtnRight => (RightDown, RightUp, RightDragged),
            BtnOther => (OtherDown, OtherUp, Moved),
            _ => (LeftDown, LeftUp, LeftDragged),
        };
        Post(Moved, pos, button);
        Post(down, pos, button, clickCount);
        Post(up, pos, button, clickCount);
        if (clickCount >= 2)
        {
            Post(down, pos, button, 2);
            Post(up, pos, button, 2);
        }
    }

    /// <summary>One mouse event delivered straight to the window as an NSEvent (no CGEvent): WebKit
    /// sees a genuine, trusted user event, but the OS pointer doesn't move, no Accessibility
    /// permission is needed and it works whatever is stacked above the window. type: 5 = moved,
    /// 1 = left down, 2 = left up. Main thread only.</summary>
    public static void SendMouseEvent(IntPtr nsWindow, IntPtr view, double x, double y, int type)
    {
        var wp = ObjC.SendPointFromView(view, ObjC.Sel("convertPoint:toView:"),
            new MacWindow.CGPoint { X = x, Y = y }, IntPtr.Zero);
        var now = ObjC.SendDouble(ObjC.Send(ObjC.GetClass("NSProcessInfo"), ObjC.Sel("processInfo")), ObjC.Sel("systemUptime"));
        var ev = ObjC.SendMouseEvent(ObjC.GetClass("NSEvent"), ObjC.Sel(
            "mouseEventWithType:location:modifierFlags:timestamp:windowNumber:context:eventNumber:clickCount:pressure:"),
            type, wp, 0, now, (nint)MacWindow.WindowNumber(nsWindow), IntPtr.Zero, 0, type == 5 ? 0 : 1, type == 1 ? 1f : 0f);
        if (ev != IntPtr.Zero) ObjC.Send(nsWindow, ObjC.Sel("sendEvent:"), ev);
    }
}
