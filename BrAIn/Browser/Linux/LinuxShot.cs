using System.Runtime.InteropServices;

namespace BrAIn.Browser.Linux;

/// <summary>
/// Screenshots (Linux counterpart of <c>Mac/MacShot.cs</c>): the engine renders a view's visible area itself
/// (<c>webkit_web_view_get_snapshot</c> — no window grab, so it works whatever is on top of the window), and a
/// scrolling full-page shot is painted frame by frame into one tall bitmap. Images are cairo surfaces here,
/// passed around as IntPtr exactly like the CGImages on macOS.
/// Everything except <see cref="Canvas"/>'s PNG encode must run on the GTK main thread.
/// </summary>
internal static unsafe class LinuxShot
{
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

    internal static byte[]? ImageToPng(IntPtr image) => Cairo.ToPng(image);
    internal static int ImageWidth(IntPtr img) => Cairo.cairo_image_surface_get_width(img);
    internal static int ImageHeight(IntPtr img) => Cairo.cairo_image_surface_get_height(img);
    internal static void ReleaseImage(IntPtr img) { if (img != IntPtr.Zero) Cairo.cairo_surface_destroy(img); }

    // ---- snapshot of a view's visible area (rendered by the web process) ----

    private sealed class SnapState { public readonly TaskCompletionSource<IntPtr> Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously); }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static void SnapDone(IntPtr view, IntPtr result, IntPtr token)
    {
        var st = GLib.Unstash<SnapState>(token);
        var surface = IntPtr.Zero;
        try
        {
            surface = WebKit.webkit_web_view_get_snapshot_finish(view, result, out var err);
            GLib.TakeError(err);
            if (surface != IntPtr.Zero && Cairo.cairo_surface_status(surface) != 0) { Cairo.cairo_surface_destroy(surface); surface = IntPtr.Zero; }
        }
        catch { surface = IntPtr.Zero; }
        st?.Tcs.TrySetResult(surface);
    }

    /// <summary>Main thread. Starts a snapshot of the view's visible area; the task yields a surface the caller
    /// must release (device pixels), or zero on failure.</summary>
    internal static Task<IntPtr> BeginSnapshot(IntPtr view)
    {
        var st = new SnapState();
        WebKit.webkit_web_view_get_snapshot(view, WebKit.SnapshotVisible, WebKit.SnapshotNone, IntPtr.Zero,
            (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&SnapDone, GLib.Stash(st));
        return st.Tcs.Task;
    }

    /// <summary>Sub-rectangle of an image (pixels, origin top-left). Consumes <paramref name="img"/>.</summary>
    internal static IntPtr CropImage(IntPtr img, double x, double y, double w, double h)
    {
        var iw = (double)ImageWidth(img);
        var ih = (double)ImageHeight(img);
        x = Math.Max(0, Math.Floor(x)); y = Math.Max(0, Math.Floor(y));
        w = Math.Min(Math.Floor(w), iw - x); h = Math.Min(Math.Floor(h), ih - y);
        if (w < 1 || h < 1) { ReleaseImage(img); return IntPtr.Zero; }
        if (x == 0 && y == 0 && w == iw && h == ih) return img;
        var dst = Cairo.cairo_image_surface_create(Cairo.FormatArgb32, (int)w, (int)h);
        if (Cairo.cairo_surface_status(dst) != 0) { Cairo.cairo_surface_destroy(dst); ReleaseImage(img); return IntPtr.Zero; }
        var cr = Cairo.cairo_create(dst);
        Cairo.cairo_set_source_surface(cr, img, -x, -y);
        Cairo.cairo_paint(cr);
        Cairo.cairo_destroy(cr);
        ReleaseImage(img);
        return dst;
    }

    /// <summary>Fallback when the engine cannot snapshot a view: grab the window's own pixels. Only reliable while
    /// the window is on screen and uncovered. clipPx: optional crop in image pixels (x, y, w, h).</summary>
    internal static byte[]? CapturePng(IntPtr win, (double X, double Y, double W, double H)? clipPx = null)
    {
        var gw = Gtk.gtk_widget_get_window(win);
        if (gw == IntPtr.Zero) return null;
        var scale = Math.Max(1, Gtk.gdk_window_get_scale_factor(gw));
        var w = Gtk.gdk_window_get_width(gw); var h = Gtk.gdk_window_get_height(gw);
        var x = 0; var y = 0;
        if (clipPx is { } c)
        {
            x = (int)(c.X / scale); y = (int)(c.Y / scale);
            w = Math.Min(w - x, Math.Max(1, (int)(c.W / scale))); h = Math.Min(h - y, Math.Max(1, (int)(c.H / scale)));
        }
        if (w < 1 || h < 1) return null;
        var pb = Gtk.gdk_pixbuf_get_from_window(gw, x, y, w, h);
        if (pb == IntPtr.Zero) return null;
        try { return Pixbuf.ToPng(pb); }
        finally { GLib.g_object_unref(pb); }
    }

    /// <summary>One tall bitmap that frames are painted into, top to bottom.</summary>
    internal sealed class Canvas : IDisposable
    {
        private IntPtr _surface;
        public int Width { get; }
        public int Height { get; }

        private Canvas(IntPtr surface, int w, int h) { _surface = surface; Width = w; Height = h; }

        /// <summary>Null when the size is unusable or too large.</summary>
        public static Canvas? Create(int w, int h)
        {
            if (w < 1 || h < 1 || (long)w * h * 4 > MaxCanvasBytes) return null;
            var s = Cairo.cairo_image_surface_create(Cairo.FormatArgb32, w, h);
            if (Cairo.cairo_surface_status(s) != 0) { Cairo.cairo_surface_destroy(s); return null; }
            var cr = Cairo.cairo_create(s);
            Cairo.cairo_set_source_rgb(cr, 1, 1, 1);
            Cairo.cairo_paint(cr);
            Cairo.cairo_destroy(cr);
            return new Canvas(s, w, h);
        }

        /// <summary>Paints <paramref name="img"/> with its top edge at <paramref name="topPx"/>.</summary>
        public void Draw(IntPtr img, int topPx)
        {
            if (_surface == IntPtr.Zero || img == IntPtr.Zero) return;
            var cr = Cairo.cairo_create(_surface);
            Cairo.cairo_set_source_surface(cr, img, 0, topPx);
            Cairo.cairo_paint(cr);
            Cairo.cairo_destroy(cr);
        }

        public byte[]? ToPng()
        {
            if (_surface == IntPtr.Zero) return null;
            Cairo.cairo_surface_flush(_surface);
            return Cairo.ToPng(_surface);
        }

        public void Dispose()
        {
            if (_surface != IntPtr.Zero) { Cairo.cairo_surface_destroy(_surface); _surface = IntPtr.Zero; }
        }
    }
}
