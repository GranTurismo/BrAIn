using System.Runtime.InteropServices;

namespace BrAIn.Browser.Mac;

/// <summary>
/// Minimal Objective-C runtime interop for driving WKWebView directly.
/// Only usable on macOS — every call site must be guarded by an OS check.
/// All UI-object calls must run on the main thread (Photino's Invoke does that).
/// </summary>
internal static partial class ObjC
{
    private const string LibObjC = "/usr/lib/libobjc.A.dylib";

    [LibraryImport(LibObjC)]
    internal static partial IntPtr objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [LibraryImport(LibObjC)]
    internal static partial IntPtr sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial IntPtr Send(IntPtr recv, IntPtr sel);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial IntPtr Send(IntPtr recv, IntPtr sel, IntPtr a);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial IntPtr Send(IntPtr recv, IntPtr sel, IntPtr a, IntPtr b);

    /// NSString* + NSInteger + BOOL args (WKUserScript init).
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial IntPtr SendStrIntBool(IntPtr recv, IntPtr sel, IntPtr a, nint b, byte c);

    /// NSInteger + two object args (LAContext evaluatePolicy:localizedReason:reply:).
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial IntPtr SendNIntObj2(IntPtr recv, IntPtr sel, nint a, IntPtr b, IntPtr c);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial nint SendNInt(IntPtr recv, IntPtr sel);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial nint SendNInt(IntPtr recv, IntPtr sel, nint a);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial byte SendBool(IntPtr recv, IntPtr sel, IntPtr a);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial double SendDouble(IntPtr recv, IntPtr sel);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial IntPtr SendDoubleArg(IntPtr recv, IntPtr sel, double a);

    /// +[NSColor colorWithSRGBRed:green:blue:alpha:]
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial IntPtr SendDouble4(IntPtr recv, IntPtr sel, double a, double b, double c, double d);

    /// -[NSView addSubview:positioned:relativeTo:]
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial void SendViewPosView(IntPtr recv, IntPtr sel, IntPtr view, nint pos, IntPtr rel);

    /// -[WKWebView initWithFrame:configuration:]
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial IntPtr SendRectObj(IntPtr recv, IntPtr sel, MacWindow.CGRect rect, IntPtr obj);

    /// CGRect-returning send (frame/bounds). arm64 returns large structs via
    /// the normal objc_msgSend path; x86_64 requires the stret variant.
    internal static MacWindow.CGRect SendRect(IntPtr recv, IntPtr sel)
        => RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? SendRectStret(recv, sel)
            : SendRectArm64(recv, sel);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial MacWindow.CGRect SendRectArm64(IntPtr recv, IntPtr sel);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend_stret")]
    private static partial MacWindow.CGRect SendRectStret(IntPtr recv, IntPtr sel);

    /// CGRect-by-value send (setFrame: and friends).
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial void SendRectArg(IntPtr recv, IntPtr sel, MacWindow.CGRect rect);

    /// CGPoint-returning send (NSEvent.mouseLocation etc). 16-byte struct —
    /// register-returned on both arm64 and x86_64, no stret needed.
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial MacWindow.CGPoint SendPoint(IntPtr recv, IntPtr sel);

    /// CGPoint arg → CGPoint return (convertPointFromScreen:).
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial MacWindow.CGPoint SendPointRet(IntPtr recv, IntPtr sel, MacWindow.CGPoint p);

    /// CGPoint-by-value send, void return (setFrameOrigin:).
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial void SendPointArg(IntPtr recv, IntPtr sel, MacWindow.CGPoint p);

    /// CGPoint + NSView args → CGPoint return (convertPoint:fromView:).
    /// Pass IntPtr.Zero for the view to use window-base coordinates.
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial MacWindow.CGPoint SendPointFromView(IntPtr recv, IntPtr sel,
        MacWindow.CGPoint p, IntPtr view);

    /// +[NSEvent mouseEventWithType:location:modifierFlags:timestamp:
    /// windowNumber:context:eventNumber:clickCount:pressure:]
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial IntPtr SendMouseEvent(IntPtr recv, IntPtr sel,
        nint type, MacWindow.CGPoint location, nuint modifierFlags, double timestamp,
        nint windowNumber, IntPtr context, nint eventNumber, nint clickCount, float pressure);

    private static readonly Dictionary<string, IntPtr> _classes = new();
    private static readonly Dictionary<string, IntPtr> _sels = new();

    internal static IntPtr GetClass(string name)
    {
        lock (_classes)
        {
            if (!_classes.TryGetValue(name, out var c))
                _classes[name] = c = objc_getClass(name);
            return c;
        }
    }

    internal static IntPtr Sel(string name)
    {
        lock (_sels)
        {
            if (!_sels.TryGetValue(name, out var s))
                _sels[name] = s = sel_registerName(name);
            return s;
        }
    }

    /// <summary>Autoreleased NSString from a UTF-8 literal. Call on a thread
    /// with an autorelease pool (the main thread always has one).</summary>
    internal static IntPtr NsString(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value + '\0');
        unsafe
        {
            fixed (byte* p = bytes)
            {
                return Send(GetClass("NSString"), Sel("stringWithUTF8String:"), (IntPtr)p);
            }
        }
    }

    /// <summary>NSArray count.</summary>
    internal static nint ArrayCount(IntPtr nsArray) => SendNInt(nsArray, Sel("count"));

    /// <summary>NSArray objectAtIndex:.</summary>
    internal static IntPtr ArrayAt(IntPtr nsArray, nint i) => Send(nsArray, Sel("objectAtIndex:"), i);

    internal static bool IsKindOf(IntPtr obj, string className)
    {
        var cls = GetClass(className);
        if (cls == IntPtr.Zero) return false;
        return SendBool(obj, Sel("isKindOfClass:"), cls) != 0;
    }

    /// <summary>Converts an NSString pointer to a C# string via UTF8String.</summary>
    internal static string? NsStringToString(IntPtr nsString)
    {
        if (nsString == IntPtr.Zero) return null;
        var p = Send(nsString, Sel("UTF8String"));
        return p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);
    }
}

