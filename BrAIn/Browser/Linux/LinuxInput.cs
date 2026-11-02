using System.Runtime.InteropServices;

namespace BrAIn.Browser.Linux;

/// <summary>
/// Real mouse input for the page views (Linux counterpart of <c>Mac/MacInput.cs</c>). Events are built as genuine
/// GDK events and delivered straight to the view's GDK window through GTK's own dispatcher, so WebKit sees exactly
/// what a mouse would send: the page gets trusted events with user activation (WebAuthn/passkeys, pop-ups from click
/// handlers, clipboard and fullscreen requests that need a gesture). The OS pointer does not move, no
/// accessibility permission is needed and it works whatever is stacked above the window.
/// All calls must run on the GTK main thread.
/// </summary>
internal static unsafe class LinuxInput
{
    private const int BtnLeft = 0, BtnRight = 1, BtnOther = 2;
    private const uint Button1Mask = 1 << 8, Button2Mask = 1 << 9, Button3Mask = 1 << 10;

    private static uint _t;
    /// <summary>Strictly increasing event time (ms). GDK only uses differences (double-click detection).</summary>
    private static uint Now()
    {
        var t = (uint)(Environment.TickCount64 & 0xFFFFFFFF);
        if (t <= _t) t = _t + 1;
        return _t = t;
    }

    private static IntPtr Pointer()
        => Gtk.gdk_seat_get_pointer(Gtk.gdk_display_get_default_seat(Gtk.gdk_display_get_default()));

    /// <summary>Builds and dispatches one pointer event at (x,y) in the view's coordinates (CSS points, top-left).</summary>
    private static void Dispatch(IntPtr view, int type, double x, double y, uint state, uint button, uint time)
    {
        var gw = Gtk.gtk_widget_get_window(view);
        if (gw == IntPtr.Zero) return;
        var ev = Gtk.gdk_event_new(type);
        if (ev == IntPtr.Zero) return;
        try
        {
            Gtk.gdk_window_get_origin(gw, out var ox, out var oy);
            // GdkEventAny: type @0, window @8, send_event @16 — GdkEventButton/Motion continue: time @20, x @24, y @32,
            // axes @40, state @48, button @52 (motion: is_hint @52), device @56, x_root @64, y_root @72
            Marshal.WriteIntPtr(ev, 8, GLib.g_object_ref(gw));
            Marshal.WriteByte(ev, 16, 1);
            Marshal.WriteInt32(ev, 20, (int)time);
            Marshal.WriteInt64(ev, 24, BitConverter.DoubleToInt64Bits(x));
            Marshal.WriteInt64(ev, 32, BitConverter.DoubleToInt64Bits(y));
            Marshal.WriteInt32(ev, 48, (int)state);
            if (type != Gtk.EvMotion) Marshal.WriteInt32(ev, 52, (int)button);
            Marshal.WriteInt64(ev, 64, BitConverter.DoubleToInt64Bits(ox + x));
            Marshal.WriteInt64(ev, 72, BitConverter.DoubleToInt64Bits(oy + y));
            var dev = Pointer();
            Gtk.gdk_event_set_device(ev, dev);
            Gtk.gdk_event_set_source_device(ev, dev);
            Gtk.gtk_main_do_event(ev);
        }
        finally { Gtk.gdk_event_free(ev); }
    }

    private static (uint Num, uint Mask) Btn(int b) => b switch { BtnRight => (3u, Button3Mask), BtnOther => (2u, Button2Mask), _ => (1u, Button1Mask) };

    /// <summary>Moves the pointer over a point in view coordinates (hover effects, tooltips). Main thread only.</summary>
    public static void MoveTo(IntPtr win, IntPtr view, double x, double y)
        => Dispatch(view, Gtk.EvMotion, x, y, 0, 0, Now());

    /// <summary>Real click at a point in view coordinates: hover, press, release (twice for a double click —
    /// WebKit counts consecutive presses itself). Main thread only.</summary>
    public static void Click(IntPtr win, IntPtr view, double x, double y, int button = BtnLeft, int clickCount = 1)
    {
        var (num, mask) = Btn(button);
        Dispatch(view, Gtk.EvMotion, x, y, 0, 0, Now());
        for (var i = 0; i < Math.Max(1, clickCount); i++)
        {
            Dispatch(view, Gtk.EvButtonPress, x, y, 0, num, Now());
            Dispatch(view, Gtk.EvButtonRelease, x, y, mask, num, Now());
        }
    }

    /// <summary>One mouse event: type 5 = moved, 1 = left down, 2 = left up (the macOS numbering). Main thread only.</summary>
    public static void SendMouseEvent(IntPtr win, IntPtr view, double x, double y, int type)
    {
        switch (type)
        {
            case 5: Dispatch(view, Gtk.EvMotion, x, y, 0, 0, Now()); break;
            case 1: Dispatch(view, Gtk.EvButtonPress, x, y, 0, 1, Now()); break;
            case 2: Dispatch(view, Gtk.EvButtonRelease, x, y, Button1Mask, 1, Now()); break;
        }
    }
}
