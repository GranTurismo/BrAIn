using System.Runtime.InteropServices;

namespace BrAIn.Browser.Mac;

/// <summary>
/// Pieces for scrolling (full-page) screenshots: grab the page view's pixels out of
/// the window image, and paint successive grabs into one tall bitmap.
/// Everything except <see cref="Canvas"/>'s PNG encode must run on the main thread.
/// </summary>
internal static partial class MacWindow
{
    [LibraryImport(CoreGraphics)]
    private static partial IntPtr CGColorSpaceCreateDeviceRGB();

    [LibraryImport(CoreGraphics)]
    private static partial void CGColorSpaceRelease(IntPtr cs);

    [LibraryImport(CoreGraphics)]
    private static partial IntPtr CGBitmapContextCreate(IntPtr data, nuint width, nuint height, nuint bitsPerComponent,
        nuint bytesPerRow, IntPtr colorSpace, uint bitmapInfo);

    [LibraryImport(CoreGraphics)]
    private static partial void CGContextDrawImage(IntPtr ctx, CGRect rect, IntPtr image);

    [LibraryImport(CoreGraphics)]
    private static partial void CGContextSetRGBFillColor(IntPtr ctx, double r, double g, double b, double a);

    [LibraryImport(CoreGraphics)]
    private static partial void CGContextFillRect(IntPtr ctx, CGRect rect);

    [LibraryImport(CoreGraphics)]
    private static partial IntPtr CGBitmapContextCreateImage(IntPtr ctx);

    [LibraryImport(CoreGraphics)]
    private static partial void CGContextRelease(IntPtr ctx);

    [LibraryImport(CoreGraphics)]
    private static partial nuint CGImageGetWidth(IntPtr image);

    [LibraryImport(CoreGraphics)]
    private static partial nuint CGImageGetHeight(IntPtr image);

    private const uint KCGImageAlphaPremultipliedLast = 1;

    /// <summary>Largest bitmap we are willing to build (bytes), keeps a very long page from eating all memory.</summary>
    internal const long MaxCanvasBytes = 480L * 1024 * 1024;

    internal static readonly string ShotDir = Environment.GetEnvironmentVariable("BRAIN_SHOT_DEBUG") ?? "";
    internal static bool ShotDebug => ShotDir.Length > 0;

    internal static void ShotLog(string msg)
    {
        if (ShotDebug) Console.Error.WriteLine("[shot] " + msg);
    }

    internal static void ShotDump(string tag, IntPtr img)
    {
        if (!ShotDebug || img == IntPtr.Zero) return;
        try
        {
            var png = ImageToPng(img);
            if (png is not null) File.WriteAllBytes(Path.Combine(ShotDir, "brain-shot-" + tag + ".png"), png);
            ShotLog($"dump {tag}: {ImageWidth(img)}x{ImageHeight(img)} -> {(png?.Length ?? 0)} bytes");
        }
        catch (Exception ex) { ShotLog("dump failed: " + ex.Message); }
    }

    internal static byte[]? ImageToPng(IntPtr image)
    {
        var rep = ObjC.Send(ObjC.GetClass("NSBitmapImageRep"), ObjC.Sel("alloc"));
        rep = ObjC.Send(rep, ObjC.Sel("initWithCGImage:"), image);
        if (rep == IntPtr.Zero) return null;
        var data = ObjC.Send(rep, ObjC.Sel("representationUsingType:properties:"), (nint)4, IntPtr.Zero); // NSPNGFileType
        ObjC.Send(rep, ObjC.Sel("release"));
        if (data == IntPtr.Zero) return null;
        var len = (int)(long)ObjC.SendNInt(data, ObjC.Sel("length"));
        var ptr = ObjC.Send(data, ObjC.Sel("bytes"));
        if (len <= 0 || ptr == IntPtr.Zero) return null;
        var bytes = new byte[len];
        Marshal.Copy(ptr, bytes, 0, len);
        return bytes;
    }

    [LibraryImport(CoreGraphics)]
    private static partial IntPtr CFRetain(IntPtr obj);

    // ---- WKWebView.takeSnapshot: renders from the web process, so it works where a
    // window capture only sees an empty (black) layer. Needs an Objective-C block as
    // completion handler — built by hand as a "global" block with a managed invoke thunk.

    private static TaskCompletionSource<IntPtr>? _snapTcs;
    private static IntPtr _snapBlock;

    [UnmanagedCallersOnly]
    private static void SnapInvoke(IntPtr block, IntPtr nsImage, IntPtr nsError)
    {
        var cg = IntPtr.Zero;
        try
        {
            if (nsImage != IntPtr.Zero)
            {
                var tiff = ObjC.Send(nsImage, ObjC.Sel("TIFFRepresentation"));
                if (tiff != IntPtr.Zero)
                {
                    var rep = ObjC.Send(ObjC.GetClass("NSBitmapImageRep"), ObjC.Sel("imageRepWithData:"), tiff);
                    if (rep != IntPtr.Zero)
                    {
                        cg = ObjC.Send(rep, ObjC.Sel("CGImage"));
                        if (cg != IntPtr.Zero) CFRetain(cg); // the rep is autoreleased; keep the pixels
                    }
                }
            }
        }
        catch { cg = IntPtr.Zero; }
        _snapTcs?.TrySetResult(cg);
    }

    private static unsafe IntPtr SnapBlock()
    {
        if (_snapBlock != IntPtr.Zero) return _snapBlock;
        var lib = NativeLibrary.Load("/usr/lib/libSystem.B.dylib");
        var isa = NativeLibrary.GetExport(lib, "_NSConcreteGlobalBlock");
        var desc = Marshal.AllocHGlobal(16);
        Marshal.WriteInt64(desc, 0, 0);   // reserved
        Marshal.WriteInt64(desc, 8, 32);  // block size
        var blk = Marshal.AllocHGlobal(32);
        Marshal.WriteIntPtr(blk, 0, isa);
        Marshal.WriteInt32(blk, 8, 1 << 28); // BLOCK_IS_GLOBAL
        Marshal.WriteInt32(blk, 12, 0);
        Marshal.WriteIntPtr(blk, 16, (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&SnapInvoke);
        Marshal.WriteIntPtr(blk, 24, desc);
        _snapBlock = blk;
        return blk;
    }

    /// <summary>Main thread. Starts a snapshot of the view's visible area; the task
    /// yields a retained CGImage (device pixels), or zero on failure. One at a time.</summary>
    internal static Task<IntPtr> BeginSnapshot(IntPtr view)
    {
        var tcs = new TaskCompletionSource<IntPtr>(TaskCreationOptions.RunContinuationsAsynchronously);
        _snapTcs = tcs;
        var cfg = ObjC.Send(ObjC.Send(ObjC.GetClass("WKSnapshotConfiguration"), ObjC.Sel("alloc")), ObjC.Sel("init"));
        ObjC.Send(view, ObjC.Sel("takeSnapshotWithConfiguration:completionHandler:"), cfg, SnapBlock());
        if (cfg != IntPtr.Zero) ObjC.Send(cfg, ObjC.Sel("release"));
        return tcs.Task;
    }

    /// <summary>Sub-rectangle of an image (pixels, origin top-left). Consumes <paramref name="img"/>.</summary>
    internal static IntPtr CropImage(IntPtr img, double x, double y, double w, double h)
    {
        var iw = (double)CGImageGetWidth(img);
        var ih = (double)CGImageGetHeight(img);
        x = Math.Max(0, Math.Floor(x)); y = Math.Max(0, Math.Floor(y));
        w = Math.Min(Math.Floor(w), iw - x); h = Math.Min(Math.Floor(h), ih - y);
        if (w < 1 || h < 1) { CFRelease(img); return IntPtr.Zero; }
        if (x == 0 && y == 0 && w == iw && h == ih) return img;
        var c = CGImageCreateWithImageInRect(img, new CGRect { X = x, Y = y, W = w, H = h });
        CFRelease(img);
        return c;
    }

    internal static int ImageWidth(IntPtr img) => (int)CGImageGetWidth(img);
    internal static int ImageHeight(IntPtr img) => (int)CGImageGetHeight(img);
    internal static void ReleaseImage(IntPtr img) { if (img != IntPtr.Zero) CFRelease(img); }

    /// <summary>One tall RGBA bitmap that frames are painted into, top to bottom.</summary>
    internal sealed class Canvas : IDisposable
    {
        private IntPtr _ctx;
        public int Width { get; }
        public int Height { get; }

        private Canvas(IntPtr ctx, int w, int h) { _ctx = ctx; Width = w; Height = h; }

        /// <summary>Null when the size is unusable or too large.</summary>
        public static Canvas? Create(int w, int h)
        {
            if (w < 1 || h < 1 || (long)w * h * 4 > MaxCanvasBytes) return null;
            var cs = CGColorSpaceCreateDeviceRGB();
            var ctx = CGBitmapContextCreate(IntPtr.Zero, (nuint)w, (nuint)h, 8, 0, cs, KCGImageAlphaPremultipliedLast);
            CGColorSpaceRelease(cs);
            if (ctx == IntPtr.Zero) return null;
            CGContextSetRGBFillColor(ctx, 1, 1, 1, 1);
            CGContextFillRect(ctx, new CGRect { X = 0, Y = 0, W = w, H = h });
            return new Canvas(ctx, w, h);
        }

        /// <summary>Paints <paramref name="img"/> with its top edge at <paramref name="topPx"/>.</summary>
        public void Draw(IntPtr img, int topPx)
        {
            if (_ctx == IntPtr.Zero || img == IntPtr.Zero) return;
            var w = ImageWidth(img); var h = ImageHeight(img);
            CGContextDrawImage(_ctx, new CGRect { X = 0, Y = Height - topPx - h, W = w, H = h }, img);
        }

        public byte[]? ToPng()
        {
            if (_ctx == IntPtr.Zero) return null;
            var image = CGBitmapContextCreateImage(_ctx);
            if (image == IntPtr.Zero) return null;
            try { return ImageToPng(image); }
            finally { CFRelease(image); }
        }

        public void Dispose()
        {
            if (_ctx != IntPtr.Zero) { CGContextRelease(_ctx); _ctx = IntPtr.Zero; }
        }
    }
}
