using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace BrAIn.Browser.Mac;

/// <summary>
/// macOS terminal mode (<c>brain view</c> on a Mac): BrAIn runs with an invisible window and the terminal viewer is fed from
/// here. A separate process may not capture another app's windows (that needs Screen Recording permission), so the picture is
/// made inside BrAIn: snapshots of the toolbar view, the active page and any popup, composed into one frame. Input comes back
/// as native events sent straight to the window (the OS pointer never moves, nothing reaches other apps).
///
/// The window stays on screen at alpha 0 and ignores the mouse, the app leaves the Dock (accessory), App Nap is held off and
/// WebKit's occlusion detection is switched off on every web view: an invisible window would otherwise be throttled to a stop.
/// Main thread unless noted.
/// </summary>
internal static unsafe partial class MacView
{
    private const string LibObjC = "/usr/lib/libobjc.A.dylib";
    private const string CG = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    /// <summary>Set once at start for a headless BrAIn (also read by WkPopup for its windows).</summary>
    internal static bool Headless;

    /// <summary>The window that gets typed keys: the popup last opened or clicked, zero for the browser window.</summary>
    internal static IntPtr KeyTarget;

    // ---------------------------------------------------------------- the invisible window

    private static IntPtr _activity;

    internal static void MakeHeadless(IntPtr win)
    {
        if (win == IntPtr.Zero) return;
        HideWindow(win);
        var app = ObjC.Send(ObjC.GetClass("NSApplication"), ObjC.Sel("sharedApplication"));
        ObjC.SendNInt(app, ObjC.Sel("setActivationPolicy:"), 1); // accessory: no Dock icon, no menu bar
        if (_activity == IntPtr.Zero)
        {
            // NSActivityUserInitiated | NSActivityLatencyCritical: no App Nap, no timer coalescing while it serves a terminal
            var pi = ObjC.Send(ObjC.GetClass("NSProcessInfo"), ObjC.Sel("processInfo"));
            _activity = SendULongObj(pi, ObjC.Sel("beginActivityWithOptions:reason:"), 0x00FFFFFFUL | 0xFF00000000UL,
                ObjC.NsString("BrAIn is shown in a terminal (brain view)"));
            if (_activity != IntPtr.Zero) ObjC.Send(_activity, ObjC.Sel("retain"));
        }
        KeepRendering(win);
    }

    /// <summary>Fully transparent, click-through, out of Mission Control and the window cycle — but still on screen, so the
    /// window server keeps it alive.</summary>
    internal static void HideWindow(IntPtr win)
    {
        ObjC.SendDoubleArg(win, ObjC.Sel("setAlphaValue:"), 0);
        ObjC.SendNInt(win, ObjC.Sel("setIgnoresMouseEvents:"), 1);
        ObjC.SendNInt(win, ObjC.Sel("setCollectionBehavior:"), (1 << 3) | (1 << 6)); // transient | ignoresCycle
        ObjC.SendNInt(win, ObjC.Sel("setAcceptsMouseMovedEvents:"), 1);
        ObjC.SendNInt(win, ObjC.Sel("setHasShadow:"), 0);
    }

    /// <summary>Every web view in the window keeps rendering although nobody can see the window.</summary>
    internal static void KeepRendering(IntPtr win)
    {
        if (win == IntPtr.Zero) return;
        var content = ObjC.Send(win, ObjC.Sel("contentView"));
        if (content != IntPtr.Zero) Walk(content, 0);
    }

    private static readonly IntPtr _occl = ObjC.Sel("_setWindowOcclusionDetectionEnabled:");

    private static void Walk(IntPtr view, int depth)
    {
        if (depth > 12) return;
        if (ObjC.SendBool(view, ObjC.Sel("isKindOfClass:"), ObjC.GetClass("WKWebView")) != 0 &&
            ObjC.SendBool(view, ObjC.Sel("respondsToSelector:"), _occl) != 0)
            ObjC.SendNInt(view, _occl, 0);
        var subs = ObjC.Send(view, ObjC.Sel("subviews"));
        if (subs == IntPtr.Zero) return;
        var n = ObjC.ArrayCount(subs);
        for (nint i = 0; i < n; i++) Walk(ObjC.ArrayAt(subs, i), depth + 1);
    }

    // ---------------------------------------------------------------- geometry

    /// <summary>A view's rectangle in its window's content coordinates, origin top-left (points).</summary>
    internal static MacWindow.CGRect RectInContent(IntPtr view)
    {
        var win = ObjC.Send(view, ObjC.Sel("window"));
        var content = win == IntPtr.Zero ? IntPtr.Zero : ObjC.Send(win, ObjC.Sel("contentView"));
        if (content == IntPtr.Zero) return default;
        var b = ObjC.SendRect(view, ObjC.Sel("bounds"));
        var r = ConvertRect(view, ObjC.Sel("convertRect:toView:"), b, content);
        var ch = ObjC.SendRect(content, ObjC.Sel("bounds")).H;
        var flipped = (ObjC.SendNInt(content, ObjC.Sel("isFlipped")) & 0xFF) != 0;
        return new MacWindow.CGRect { X = r.X, Y = flipped ? r.Y : ch - r.Y - r.H, W = r.W, H = r.H };
    }

    internal static (double W, double H) ContentSize(IntPtr win)
    {
        var content = ObjC.Send(win, ObjC.Sel("contentView"));
        if (content == IntPtr.Zero) return (0, 0);
        var b = ObjC.SendRect(content, ObjC.Sel("bounds"));
        return (b.W, b.H);
    }

    internal static void SetContentSize(IntPtr win, double w, double h)
        => ObjC.SendPointArg(win, ObjC.Sel("setContentSize:"), new MacWindow.CGPoint(w, h)); // NSSize = two doubles, like a point

    // ---------------------------------------------------------------- callbacks (Objective-C blocks with a tag)

    // A hand-made global block (the runtime never copies those) with one extra field: a tag the shared invoke function
    // reads back from the block pointer to find its request. Blocks are pooled; a block is reused only after it was called.
    private static IntPtr _isa;
    private static readonly ConcurrentDictionary<long, Action<object?>> _pending = new();
    private static readonly ConcurrentBag<IntPtr> _snapPool = [], _evalPool = [];
    private static long _tag;

    private static IntPtr Block(ConcurrentBag<IntPtr> pool, IntPtr invoke, Action<object?> done, out long tag)
    {
        if (_isa == IntPtr.Zero) _isa = NativeLibrary.GetExport(NativeLibrary.Load("/usr/lib/libSystem.B.dylib"), "_NSConcreteGlobalBlock");
        if (!pool.TryTake(out var blk))
        {
            var desc = Marshal.AllocHGlobal(16);
            Marshal.WriteInt64(desc, 0, 0);
            Marshal.WriteInt64(desc, 8, 40);
            blk = Marshal.AllocHGlobal(40);
            Marshal.WriteIntPtr(blk, 0, _isa);
            Marshal.WriteInt32(blk, 8, 1 << 28); // BLOCK_IS_GLOBAL
            Marshal.WriteInt32(blk, 12, 0);
            Marshal.WriteIntPtr(blk, 16, invoke);
            Marshal.WriteIntPtr(blk, 24, desc);
        }
        tag = Interlocked.Increment(ref _tag);
        Marshal.WriteInt64(blk, 32, tag);
        _pending[tag] = done;
        return blk;
    }

    private static void Finish(IntPtr blk, ConcurrentBag<IntPtr> pool, object? result)
    {
        var tag = Marshal.ReadInt64(blk, 32);
        pool.Add(blk);
        if (_pending.TryRemove(tag, out var done)) done(result);
    }

    [UnmanagedCallersOnly]
    private static void SnapDone(IntPtr blk, IntPtr nsImage, IntPtr err)
    {
        var cg = IntPtr.Zero;
        try
        {
            if (nsImage != IntPtr.Zero)
            {
                cg = Send3(nsImage, ObjC.Sel("CGImageForProposedRect:context:hints:"), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (cg != IntPtr.Zero) CFRetain(cg);
            }
        }
        catch { cg = IntPtr.Zero; }
        Finish(blk, _snapPool, cg);
    }

    [UnmanagedCallersOnly]
    private static void EvalDone(IntPtr blk, IntPtr result, IntPtr err)
    {
        string? s = null;
        try
        {
            if (result != IntPtr.Zero)
            {
                var str = ObjC.SendBool(result, ObjC.Sel("isKindOfClass:"), ObjC.GetClass("NSString")) != 0
                    ? result : ObjC.Send(result, ObjC.Sel("description"));
                var utf8 = ObjC.Send(str, ObjC.Sel("UTF8String"));
                if (utf8 != IntPtr.Zero) s = Marshal.PtrToStringUTF8(utf8);
            }
        }
        catch { s = null; }
        if (s is null && err != IntPtr.Zero)
            try { BrowserController.Log("terminal view: script failed: " + Marshal.PtrToStringUTF8(ObjC.Send(ObjC.Send(err, ObjC.Sel("description")), ObjC.Sel("UTF8String")))); } catch { }
        Finish(blk, _evalPool, s);
    }

    /// <summary>Snapshot of a web view's visible area as a retained CGImage (zero on failure).</summary>
    internal static Task<IntPtr> Snapshot(IntPtr view, double widthPoints)
    {
        var tcs = new TaskCompletionSource<IntPtr>(TaskCreationOptions.RunContinuationsAsynchronously);
        var blk = Block(_snapPool, (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&SnapDone, r => tcs.TrySetResult(r is IntPtr p ? p : IntPtr.Zero), out _);
        var cfg = ObjC.Send(ObjC.Send(ObjC.GetClass("WKSnapshotConfiguration"), ObjC.Sel("alloc")), ObjC.Sel("init"));
        if (cfg != IntPtr.Zero && widthPoints > 0)
        {
            var num = ObjC.SendDoubleArg(ObjC.GetClass("NSNumber"), ObjC.Sel("numberWithDouble:"), widthPoints);
            ObjC.Send(cfg, ObjC.Sel("setSnapshotWidth:"), num);
        }
        ObjC.Send(view, ObjC.Sel("takeSnapshotWithConfiguration:completionHandler:"), cfg, blk);
        if (cfg != IntPtr.Zero) ObjC.Send(cfg, ObjC.Sel("release"));
        return tcs.Task;
    }

    /// <summary>Runs <paramref name="js"/> in the view (in the named isolated world, or the page's own world when null) and
    /// returns its result as a string.</summary>
    internal static Task<string?> Eval(IntPtr view, string js, string? world)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var blk = Block(_evalPool, (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&EvalDone, r => tcs.TrySetResult(r as string), out _);
        var w = World(world);
        ObjC.Send(view, ObjC.Sel("evaluateJavaScript:inFrame:inContentWorld:completionHandler:"), ObjC.NsString(js), IntPtr.Zero, w, blk);
        return tcs.Task;
    }

    private static readonly Dictionary<string, IntPtr> _worlds = [];

    /// <summary>A script world, kept alive for good: WebKit drops a named world (and everything scripts left in it) once
    /// nothing holds the object, so a second call would otherwise meet a fresh, empty world.</summary>
    private static IntPtr World(string? name)
    {
        var cw = ObjC.GetClass("WKContentWorld");
        if (name is null) return ObjC.Send(cw, ObjC.Sel("pageWorld"));
        lock (_worlds)
        {
            if (_worlds.TryGetValue(name, out var w)) return w;
            w = ObjC.Send(ObjC.Send(cw, ObjC.Sel("worldWithName:"), ObjC.NsString(name)), ObjC.Sel("retain"));
            _worlds[name] = w;
            return w;
        }
    }

    // ---------------------------------------------------------------- composing a frame (any thread)

    internal readonly record struct Layer(IntPtr Image, MacWindow.CGRect Rect, bool Header = false);

    /// <summary>Draws the layers (top-left rectangles, in order) into a w×h BGRx frame. Releases the images.</summary>
    internal static byte[] Compose(int w, int h, IEnumerable<Layer> layers)
    {
        var cs = CGColorSpaceCreateDeviceRGB();
        var ctx = CGBitmapContextCreate(IntPtr.Zero, (nuint)w, (nuint)h, 8, (nuint)(w * 4), cs, 0x2002); // premultipliedFirst | 32Little = BGRA
        try
        {
            CGContextSetInterpolationQuality(ctx, 3);
            CGContextSetRGBFillColor(ctx, 0x11 / 255.0, 0x0f / 255.0, 0x0d / 255.0, 1); // the app's window colour
            CGContextFillRect(ctx, new MacWindow.CGRect { X = 0, Y = 0, W = w, H = h });
            foreach (var l in layers)
            {
                var r = l.Rect with { Y = h - l.Rect.Y - l.Rect.H }; // CG draws from the bottom-left
                if (l.Header)
                {
                    // a popup's title bar: dark strip, close box at the right
                    CGContextSetRGBFillColor(ctx, 0.17, 0.15, 0.13, 1);
                    CGContextFillRect(ctx, r);
                    var bx = r.X + r.W - 26; var by = r.Y + (r.H - 18) / 2;
                    CGContextSetRGBFillColor(ctx, 0.36, 0.32, 0.29, 1);
                    CGContextFillRect(ctx, new MacWindow.CGRect { X = bx, Y = by, W = 18, H = 18 });
                    CGContextSetRGBStrokeColor(ctx, 0.95, 0.93, 0.9, 1);
                    CGContextSetLineWidth(ctx, 1.6);
                    CGContextMoveToPoint(ctx, bx + 5, by + 5); CGContextAddLineToPoint(ctx, bx + 13, by + 13);
                    CGContextMoveToPoint(ctx, bx + 13, by + 5); CGContextAddLineToPoint(ctx, bx + 5, by + 13);
                    CGContextStrokePath(ctx);
                    continue;
                }
                if (l.Image == IntPtr.Zero) continue;
                CGContextDrawImage(ctx, r, l.Image);
                CFRelease(l.Image);
            }
            var data = CGBitmapContextGetData(ctx);
            var px = new byte[w * h * 4];
            Marshal.Copy(data, px, 0, px.Length);
            // CG stores rows top-down in memory for a bitmap context: row 0 is the top of the picture
            return px;
        }
        finally
        {
            CGContextRelease(ctx);
            CGColorSpaceRelease(cs);
        }
    }

    // ---------------------------------------------------------------- input (native events into a window)

    private static double Now() => ObjC.SendDouble(ObjC.Send(ObjC.GetClass("NSProcessInfo"), ObjC.Sel("processInfo")), ObjC.Sel("systemUptime"));

    /// <summary>Point in the window's content (top-left, points) → window base coordinates (bottom-left).</summary>
    private static MacWindow.CGPoint Base(IntPtr win, double x, double y)
    {
        var (_, h) = ContentSize(win);
        return new MacWindow.CGPoint(x, h - y);
    }

    private static IntPtr _downView;

    /// <summary>Mouse: type 1/2 left down/up, 3/4 right down/up, 5 moved, 6 left dragged, 25/26 other down/up.
    /// The window ignores the real mouse (so it never catches the user's clicks), and AppKit then drops mouse events sent to
    /// it as well — so the event goes straight to the view under the point, found the way AppKit would (hitTest), and the
    /// rest of the gesture to the view that took the press. WebKit treats these as real, trusted input.</summary>
    internal static void Mouse(IntPtr win, double x, double y, int type, int clicks, nuint mods)
    {
        var basePt = Base(win, x, y);
        var ev = ObjC.SendMouseEvent(ObjC.GetClass("NSEvent"), ObjC.Sel(
            "mouseEventWithType:location:modifierFlags:timestamp:windowNumber:context:eventNumber:clickCount:pressure:"),
            type, basePt, mods, Now(), (nint)MacWindow.WindowNumber(win), IntPtr.Zero, 0,
            type is 5 or 6 ? 0 : Math.Max(1, clicks), type is 1 or 3 or 25 or 6 ? 1f : 0f);
        if (ev == IntPtr.Zero) return;
        var content = ObjC.Send(win, ObjC.Sel("contentView"));
        if (content == IntPtr.Zero) return;
        var sup = ObjC.Send(content, ObjC.Sel("superview"));
        var inSuper = sup == IntPtr.Zero ? basePt : ObjC.SendPointFromView(sup, ObjC.Sel("convertPoint:fromView:"), basePt, IntPtr.Zero);
        var hit = SendPointRetObj(content, ObjC.Sel("hitTest:"), inSuper);
        switch (type)
        {
            case 1: case 3: case 25:
                if (hit == IntPtr.Zero) return;
                _downView = hit;
                ObjC.Send(win, ObjC.Sel("makeFirstResponder:"), hit);
                ObjC.Send(hit, ObjC.Sel(type == 1 ? "mouseDown:" : type == 3 ? "rightMouseDown:" : "otherMouseDown:"), ev);
                break;
            case 2: case 4: case 26:
                var up = _downView != IntPtr.Zero ? _downView : hit;
                _downView = IntPtr.Zero;
                if (up != IntPtr.Zero) ObjC.Send(up, ObjC.Sel(type == 2 ? "mouseUp:" : type == 4 ? "rightMouseUp:" : "otherMouseUp:"), ev);
                break;
            case 6:
                if (_downView != IntPtr.Zero) ObjC.Send(_downView, ObjC.Sel("mouseDragged:"), ev);
                break;
            default:
                if (hit != IntPtr.Zero) ObjC.Send(hit, ObjC.Sel("mouseMoved:"), ev);
                break;
        }
    }

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial IntPtr SendPointRetObj(IntPtr recv, IntPtr sel, MacWindow.CGPoint p);

    /// <summary>Wheel: lines (positive dy = content moves down = scroll up, as AppKit counts).</summary>
    internal static void Scroll(IntPtr win, double x, double y, int dy, int dx)
    {
        var cg = CGEventCreateScrollWheelEvent2(IntPtr.Zero, 1 /* line */, 2, dy, dx, 0);
        if (cg == IntPtr.Zero) return;
        try
        {
            // the event's location is global (top-left of the main screen): the window's place plus the point
            var frame = ObjC.SendRect(win, ObjC.Sel("frame"));
            var screens = ObjC.Send(ObjC.GetClass("NSScreen"), ObjC.Sel("screens"));
            var mainH = ObjC.ArrayCount(screens) > 0 ? ObjC.SendRect(ObjC.ArrayAt(screens, 0), ObjC.Sel("frame")).H : frame.H;
            var b = Base(win, x, y);
            CGEventSetLocation(cg, new MacWindow.CGPoint(frame.X + b.X, mainH - (frame.Y + b.Y)));
            var ev = ObjC.Send(ObjC.GetClass("NSEvent"), ObjC.Sel("eventWithCGEvent:"), cg);
            if (ev == IntPtr.Zero) return;
            var content = ObjC.Send(win, ObjC.Sel("contentView"));
            var sup = content == IntPtr.Zero ? IntPtr.Zero : ObjC.Send(content, ObjC.Sel("superview"));
            var hit = content == IntPtr.Zero ? IntPtr.Zero : SendPointRetObj(content, ObjC.Sel("hitTest:"),
                sup == IntPtr.Zero ? b : ObjC.SendPointFromView(sup, ObjC.Sel("convertPoint:fromView:"), b, IntPtr.Zero));
            if (hit != IntPtr.Zero) ObjC.Send(hit, ObjC.Sel("scrollWheel:"), ev);
            else ObjC.Send(win, ObjC.Sel("sendEvent:"), ev);
        }
        finally { CFRelease(cg); }
    }

    internal const nuint Shift = 1 << 17, Control = 1 << 18, Option = 1 << 19, Command = 1 << 20;

    /// <summary>A key press (down and up) with the characters it types.</summary>
    internal static void Key(IntPtr win, ushort keyCode, string chars, string charsIgnoringMods, nuint mods)
    {
        var cls = ObjC.GetClass("NSEvent");
        var sel = ObjC.Sel("keyEventWithType:location:modifierFlags:timestamp:windowNumber:context:characters:charactersIgnoringModifiers:isARepeat:keyCode:");
        var num = (nint)MacWindow.WindowNumber(win);
        foreach (var type in new nint[] { 10, 11 }) // keyDown, keyUp
        {
            var ev = SendKeyEvent(cls, sel, type, new MacWindow.CGPoint(0, 0), mods, Now(), num, IntPtr.Zero,
                ObjC.NsString(chars), ObjC.NsString(charsIgnoringMods), 0, keyCode);
            if (ev != IntPtr.Zero) ObjC.Send(win, ObjC.Sel("sendEvent:"), ev);
        }
    }

    /// <summary>X keysym → (mac key code, the character it produces). Null when the keysym is plain text.</summary>
    internal static (ushort Code, string Chars)? NamedKey(nuint keysym) => keysym switch
    {
        0xff0d => (36, "\r"), 0xff09 => (48, "\t"), 0xff08 => (51, "\u007f"), 0xff1b => (53, "\u001b"), 0x20 => (49, " "),
        0xffff => (117, ""), 0xff63 => (114, ""), 0xff50 => (115, ""), 0xff57 => (119, ""),
        0xff55 => (116, ""), 0xff56 => (121, ""),
        0xff51 => (123, ""), 0xff53 => (124, ""), 0xff54 => (125, ""), 0xff52 => (126, ""),
        >= 0xffbe and <= 0xffc9 => (FKeys[(int)(keysym - 0xffbe)], ((char)(0xF704 + (int)(keysym - 0xffbe))).ToString()),
        _ => null,
    };

    private static readonly ushort[] FKeys = [122, 120, 99, 118, 96, 97, 98, 100, 101, 109, 103, 111];

    /// <summary>US-layout key codes, for shortcuts (Cmd+L …): AppKit and pages look at them.</summary>
    internal static ushort LetterCode(char c) => char.ToLowerInvariant(c) switch
    {
        'a' => 0, 's' => 1, 'd' => 2, 'f' => 3, 'h' => 4, 'g' => 5, 'z' => 6, 'x' => 7, 'c' => 8, 'v' => 9, 'b' => 11, 'q' => 12,
        'w' => 13, 'e' => 14, 'r' => 15, 'y' => 16, 't' => 17, '1' => 18, '2' => 19, '3' => 20, '4' => 21, '6' => 22, '5' => 23,
        '=' => 24, '9' => 25, '7' => 26, '-' => 27, '8' => 28, '0' => 29, ']' => 30, 'o' => 31, 'u' => 32, '[' => 33, 'i' => 34,
        'p' => 35, 'l' => 37, 'j' => 38, '\'' => 39, 'k' => 40, ';' => 41, '\\' => 42, ',' => 43, '/' => 44, 'n' => 45, 'm' => 46,
        '.' => 47, '`' => 50, _ => 0,
    };

    // ---------------------------------------------------------------- native bindings

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial IntPtr Send3(IntPtr recv, IntPtr sel, IntPtr a, IntPtr b, IntPtr c);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial IntPtr SendULongObj(IntPtr recv, IntPtr sel, ulong a, IntPtr b);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial IntPtr SendKeyEvent(IntPtr recv, IntPtr sel, nint type, MacWindow.CGPoint location, nuint flags,
        double timestamp, nint windowNumber, IntPtr context, IntPtr chars, IntPtr charsIgnoringMods, byte repeat, ushort keyCode);

    private static MacWindow.CGRect ConvertRect(IntPtr view, IntPtr sel, MacWindow.CGRect r, IntPtr to)
        => RuntimeInformation.ProcessArchitecture == Architecture.X64 ? ConvertRectStret(view, sel, r, to) : ConvertRectArm(view, sel, r, to);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial MacWindow.CGRect ConvertRectArm(IntPtr recv, IntPtr sel, MacWindow.CGRect r, IntPtr to);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend_stret")]
    private static partial MacWindow.CGRect ConvertRectStret(IntPtr recv, IntPtr sel, MacWindow.CGRect r, IntPtr to);

    [LibraryImport(CG)] private static partial IntPtr CGColorSpaceCreateDeviceRGB();
    [LibraryImport(CG)] private static partial void CGColorSpaceRelease(IntPtr cs);
    [LibraryImport(CG)] private static partial IntPtr CGBitmapContextCreate(IntPtr data, nuint w, nuint h, nuint bpc, nuint bpr, IntPtr cs, uint info);
    [LibraryImport(CG)] private static partial IntPtr CGBitmapContextGetData(IntPtr ctx);
    [LibraryImport(CG)] private static partial void CGContextRelease(IntPtr ctx);
    [LibraryImport(CG)] private static partial void CGContextSetInterpolationQuality(IntPtr ctx, int q);
    [LibraryImport(CG)] private static partial void CGContextSetRGBFillColor(IntPtr ctx, double r, double g, double b, double a);
    [LibraryImport(CG)] private static partial void CGContextSetRGBStrokeColor(IntPtr ctx, double r, double g, double b, double a);
    [LibraryImport(CG)] private static partial void CGContextSetLineWidth(IntPtr ctx, double w);
    [LibraryImport(CG)] private static partial void CGContextMoveToPoint(IntPtr ctx, double x, double y);
    [LibraryImport(CG)] private static partial void CGContextAddLineToPoint(IntPtr ctx, double x, double y);
    [LibraryImport(CG)] private static partial void CGContextStrokePath(IntPtr ctx);
    [LibraryImport(CG)] private static partial void CGContextFillRect(IntPtr ctx, MacWindow.CGRect r);
    [LibraryImport(CG)] private static partial void CGContextDrawImage(IntPtr ctx, MacWindow.CGRect r, IntPtr image);
    [LibraryImport(CG)] private static partial IntPtr CGEventCreateScrollWheelEvent2(IntPtr source, uint units, uint count, int w1, int w2, int w3);
    [LibraryImport(CG)] private static partial void CGEventSetLocation(IntPtr ev, MacWindow.CGPoint p);
    [LibraryImport(CG)] private static partial IntPtr CFRetain(IntPtr o);
    [LibraryImport(CG)] private static partial void CFRelease(IntPtr o);
}
