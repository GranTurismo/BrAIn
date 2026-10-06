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
