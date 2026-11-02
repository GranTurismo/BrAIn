namespace BrAIn.Browser.Linux;

/// <summary>
/// Makes the page "show through" the toolbar view while a popover is open (the dropdown, the menus, the command
/// palette, tooltips). On macOS the toolbar view is simply transparent. WebKitGTK cannot do that reliably: whatever
/// the rendering mode, a transparent web view whose document uses shadows, animations or backdrop filters (chrome.js
/// uses all three) is composited as opaque, and in some modes as black. So the toolbar view is opaque, and just before
/// it grows over the page the host captures the page view with WebKit's snapshot API and hands the picture to the toolbar
/// document as the background behind the popover — pixel-identical to seeing through, with click-away, shadows and the
/// palette's dimming all working as they do on a transparent view. While the popover is up the page behind it is a still
/// picture; it is refreshed the next time the toolbar grows.
/// </summary>
internal static class LinuxBackdrop
{
    private static string? _lastUrl;                 // data URL of the last capture
    private static IntPtr _lastView;
    private static int _lastW, _lastH;
    private static long _lastAt;
    private static readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>The injected helper the toolbar document gets: sets / clears the picture behind its popovers.</summary>
    internal const string Js =
        "window.__brainBackdrop=function(u,t,w,h){var id='__brain_bd',e=document.getElementById(id);" +
        "if(!u){if(e)e.remove();return}" +
        "if(!e){e=document.createElement('div');e.id=id;(document.documentElement||document.body).insertBefore(e,(document.documentElement||document.body).firstChild)}" +
        "e.style.cssText='position:fixed;left:0;top:'+t+'px;width:'+w+'px;height:'+h+'px;z-index:0;pointer-events:none;" +
        "background:url('+u+') 0 0/100% 100% no-repeat'};\n";

    /// <summary>A capture younger than this is reused (tooltips open and close constantly).</summary>
    private const int FreshMs = 900;

    /// <summary>Captures the page view (main thread), then calls <paramref name="done"/> on the main thread once the
    /// toolbar document holds the picture — or without one when the capture failed or took too long.</summary>
    internal static void Prepare(IntPtr pageView, double top, Action done)
    {
        if (pageView == IntPtr.Zero || !LinuxTabs.Has(pageView)) { done(); return; }
        var w = Gtk.gtk_widget_get_allocated_width(pageView);
        var h = Gtk.gtk_widget_get_allocated_height(pageView);
        if (w < 2 || h < 2) { done(); return; }

        if (_lastUrl is not null && _lastView == pageView && _lastW == w && _lastH == h && Environment.TickCount64 - _lastAt < FreshMs)
        {
            Apply(_lastUrl, top, w, h);
            done();
            return;
        }
        var snap = LinuxShot.BeginSnapshot(pageView);
        _ = Task.Run(async () =>
        {
            string? url = null;
            try
            {
                var finished = await Task.WhenAny(snap, Task.Delay(450));
                if (finished == snap)
                {
                    var surface = snap.Result;
                    if (surface != IntPtr.Zero)
                    {
                        try
                        {
                            var pb = Gtk.gdk_pixbuf_get_from_surface(surface, 0, 0, Cairo.cairo_image_surface_get_width(surface), Cairo.cairo_image_surface_get_height(surface));
                            if (pb != IntPtr.Zero)
                            {
                                try
                                {
                                    var jpg = Pixbuf.ToJpeg(pb, 78);
                                    if (jpg is { Length: > 0 }) url = "data:image/jpeg;base64," + Convert.ToBase64String(jpg);
                                }
                                finally { GLib.g_object_unref(pb); }
                            }
                        }
                        finally { LinuxShot.ReleaseImage(surface); }
                    }
                }
                else _ = snap.ContinueWith(t => { if (t.IsCompletedSuccessfully && t.Result != IntPtr.Zero) LinuxShot.ReleaseImage(t.Result); });
            }
            catch { url = null; }
            GLib.RunOnMainLoop(() =>
            {
                if (url is not null)
                {
                    _lastUrl = url; _lastView = pageView; _lastW = w; _lastH = h; _lastAt = Environment.TickCount64;
                    Apply(url, top, w, h);
                }
                done();
            });
        });
    }

    private static void Apply(string dataUrl, double top, int w, int h)
        => LinuxBar.Eval($"window.__brainBackdrop&&window.__brainBackdrop('{dataUrl}',{top.ToString(System.Globalization.CultureInfo.InvariantCulture)},{w},{h})");

    /// <summary>The toolbar is back to its strip: drop the picture (and the cache — the page will have moved on).</summary>
    internal static void Clear()
    {
        _lastUrl = null;
        LinuxBar.Eval("window.__brainBackdrop&&window.__brainBackdrop(null)");
    }
}
