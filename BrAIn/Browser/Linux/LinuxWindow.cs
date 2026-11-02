using System.Runtime.InteropServices;

namespace BrAIn.Browser.Linux;

/// <summary>
/// The GTK window behind the browser (Linux counterpart of <c>Mac/MacWindow.cs</c> + the layout half of
/// <c>Mac/WkBar.cs</c>). Photino creates a plain GtkWindow whose only child is its own web view; this
/// class rebuilds that into the same split layout the macOS build uses:
///
///   GtkWindow
///    └ GtkOverlay
///        ├ (main)    GtkBox  — top margin = toolbar height
///        │             ├ GtkStack  one web view per tab (only the shown one is visible)
///        │             └ side panel view (Ollama), hidden until opened
///        └ (overlay) toolbar view — transparent, sits on top; grows over the page for popovers
///
/// Photino's own view is kept (hidden): Photino still holds a pointer to it.
/// Everything here runs on the GTK main thread.
/// </summary>
internal static unsafe partial class LinuxWindow
{
    internal const double BarHeight = 46;

    private static IntPtr _win, _boot, _overlay, _row, _pages;
    private static bool _built;
    private static int _strip = (int)BarHeight;   // height the page area leaves free for the toolbar (0 without one)

    internal static IntPtr Window => _win;
    internal static IntPtr Boot => _boot;
    internal static IntPtr Pages => _pages;
    internal static IntPtr Row => _row;
    internal static IntPtr Overlay => _overlay;
    internal static bool Built => _built;

    // ---------------------------------------------------------------- discovery

    /// <summary>All top-level GTK windows of the app (Photino's main window and any popup windows).</summary>
    internal static IntPtr[] AllWindows() => Gtk.Toplevels();

    /// <summary>Linux has no "window number": the GtkWindow pointer itself identifies the window.</summary>
    internal static long WindowNumber(IntPtr win) => (long)win;

    internal static IntPtr WindowByNumber(long number)
    {
        if (number == 0) return IntPtr.Zero;
        var p = (IntPtr)number;
        foreach (var w in Gtk.Toplevels()) if (w == p) return p; // a closed window's pointer must never be dereferenced
        return IntPtr.Zero;
    }

    /// <summary>The first web view found under a window (Photino's own one before <see cref="Setup"/>).</summary>
    internal static IntPtr FindWebView(IntPtr win)
    {
        if (win == IntPtr.Zero) return IntPtr.Zero;
        return Find(win, WebKit.webkit_web_view_get_type());
    }

    private static IntPtr Find(IntPtr w, nuint wvType)
    {
        if (GLib.IsA(w, wvType)) return w;
        if (!GLib.IsA(w, ContainerType)) return IntPtr.Zero;
        foreach (var c in Gtk.Children(w))
        {
            var f = Find(c, wvType);
            if (f != IntPtr.Zero) return f;
        }
        return IntPtr.Zero;
    }

    [LibraryImport("libgtk-3.so.0")] private static partial nuint gtk_container_get_type();
    private static nuint _contType;
    private static nuint ContainerType => _contType != 0 ? _contType : _contType = gtk_container_get_type();

    /// <summary>The tab's window — by remembered id, falling back to the first window that hosts a web view.</summary>
    internal static IntPtr WindowForTab(BrowserTab tab)
    {
        var win = tab.MacWindowNumber != 0 ? WindowByNumber(tab.MacWindowNumber) : IntPtr.Zero;
        if (win != IntPtr.Zero) return win;
        foreach (var w in AllWindows()) if (FindWebView(w) != IntPtr.Zero) return w;
        return IntPtr.Zero;
    }

    // ---------------------------------------------------------------- layout

    /// <summary>Takes over the window Photino made: re-parents its view and builds the split layout.
    /// Idempotent. <paramref name="iconPng"/> (optional) becomes the window / taskbar icon.</summary>
    internal static bool Setup(IntPtr win, string? iconPng = null, bool withToolbar = true)
    {
        if (_built) return true;
        _strip = withToolbar ? (int)BarHeight : 0;   // --no-toolbar: the pages get the whole window
        var boot = FindWebView(win);
        if (win == IntPtr.Zero || boot == IntPtr.Zero) return false;
        _win = win; _boot = boot;
        ResetSizeHints(win);
        GLib.Every(400, () => { EnsureUsableSize(win); return false; });

        // the toolbar and the page area both want the dark app colour behind them (see theme.css)
        Gtk.gtk_widget_set_name(win, "brain-window");
        Gtk.AddAppCss(win, "#brain-window{background-color:#110f0d;}");

        var parent = Gtk.gtk_widget_get_parent(boot);
        GLib.g_object_ref(boot);
        Gtk.gtk_container_remove(parent, boot);

        _overlay = Gtk.gtk_overlay_new();
        _row = Gtk.gtk_box_new(Gtk.Horizontal, 0);
        _pages = Gtk.gtk_stack_new();
        Gtk.gtk_stack_set_homogeneous(_pages, 1);
        Gtk.gtk_stack_set_transition_type(_pages, Gtk.StackNone);
        Gtk.gtk_widget_set_hexpand(_pages, 1);
        Gtk.gtk_widget_set_vexpand(_pages, 1);
        Gtk.gtk_widget_set_margin_top(_row, _strip);

        Gtk.gtk_box_pack_start(_row, _pages, 1, 1, 0);
        Gtk.gtk_box_pack_start(_row, boot, 0, 0, 0); // hidden; Photino keeps a pointer to it
        Gtk.gtk_container_add(_overlay, _row);
        Gtk.gtk_container_add(parent, _overlay);
        GLib.g_object_unref(boot);

        // The toolbar view's rectangle is dictated by us (see OnChildPosition): a web view never shrinks on its own.
        GLib.Connect(_overlay, "get-child-position", (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, int>)&OnChildPosition);

        Gtk.gtk_widget_show(_row);
        Gtk.gtk_widget_show(_pages);
        Gtk.gtk_widget_show(_overlay);
        Gtk.gtk_widget_hide(boot);

        if (!string.IsNullOrEmpty(iconPng) && File.Exists(iconPng))
            Gtk.gtk_window_set_icon_from_file(win, iconPng, out _);
        _built = true;
        return true;
    }

    /// <summary>The smallest window we allow (logical pixels): far below any real use, but phone-sized windows for
    /// responsive-layout checks (<c>browser_window_resize</c>) must stay possible.</summary>
    private const int MinWidth = 240, MinHeight = 160;

    /// <summary>Photino gives every window a maximum size of INT_MAX × INT_MAX through gtk_window_set_geometry_hints. GDK
    /// multiplies size hints by the window's scale factor, so on a scaled (HiDPI) display INT_MAX × 2 overflows to a negative
    /// maximum and the window manager squeezes the window down to its minimum — a 1-pixel-wide window. Replace the hints with
    /// a plain minimum and no maximum.</summary>
    private static void ResetSizeHints(IntPtr win)
    {
        var hints = stackalloc int[16]; // GdkGeometry: eight ints, two doubles and the gravity — all zero but the minimum
        for (var i = 0; i < 16; i++) hints[i] = 0;
        hints[0] = MinWidth; hints[1] = MinHeight;
        Gtk.gtk_window_set_geometry_hints(win, IntPtr.Zero, (IntPtr)hints, 1 << 1 /* GDK_HINT_MIN_SIZE */);
    }

    /// <summary>The size the window opens at (the same as the controller's <c>SetSize</c>).</summary>
    private const int DefaultWidth = 1440, DefaultHeight = 900;

    /// <summary>While Photino's overflowing size hints were in force the window manager may already have squeezed the window
    /// to its minimum, and a window never grows back by itself once the hints are fixed. If it is smaller than the size it was
    /// meant to open at, give it that size (fitted to the monitor's work area) and centre it.</summary>
    private static void EnsureUsableSize(IntPtr win)
    {
        try
        {
            if (IsFullScreen(win) || IsMaximized(win)) return;
            Gtk.gtk_window_get_size(win, out var w, out var h);
            var display = Gtk.gdk_display_get_default();
            var monitor = Gtk.gdk_display_get_monitor_at_window(display, Gtk.gtk_widget_get_window(win));
            if (monitor == IntPtr.Zero) monitor = Gtk.gdk_display_get_primary_monitor(display);
            var area = stackalloc int[4];
            area[2] = 1920; area[3] = 1080; // no monitor information: assume a common screen
            if (monitor != IntPtr.Zero) Gtk.gdk_monitor_get_workarea(monitor, area);
            var wantW = Math.Min(DefaultWidth, Math.Max(MinWidth, area[2]));
            var wantH = Math.Min(DefaultHeight, Math.Max(MinHeight, area[3]));
            if (w >= wantW - 8 && h >= wantH - 8) return; // already as large as it should be (or larger: the user's)
            Gtk.gtk_window_resize(win, wantW, wantH);
            Gtk.gtk_window_move(win, area[0] + Math.Max(0, (area[2] - wantW) / 2), area[1] + Math.Max(0, (area[3] - wantH) / 2));
        }
        catch { }
    }

    private static IntPtr _barBox, _barFixed;
    internal static IntPtr BarBox => _barBox;

    /// <summary>Adds the toolbar view as the topmost layer. Called once by <c>LinuxBar.Create</c>.
    ///
    /// The view is NOT the overlay child itself. WebKit mis-renders (and a web view cannot even shrink as an overlay child)
    /// when its size is changed to grow and shrink the toolbar over the page, so the view keeps one constant, window-sized
    /// size inside a Fixed, inside an EventBox — a widget with its own GDK window whose height is what changes. GDK clips
    /// the view to that window and sends mouse events outside it to the page underneath, both natively, and WebKit never
    /// sees a resize.</summary>
    internal static void AddOverlay(IntPtr view)
    {
        _barBox = Gtk.gtk_event_box_new();
        Gtk.gtk_event_box_set_above_child(_barBox, 0);
        _barFixed = Gtk.gtk_fixed_new();
        Gtk.gtk_container_add(_barBox, _barFixed);
        Gtk.gtk_fixed_put(_barFixed, view, 0, 0);
        Gtk.gtk_overlay_add_overlay(_overlay, _barBox);
        Gtk.gtk_widget_show(view);
        Gtk.gtk_widget_show(_barFixed);
        Gtk.gtk_widget_show(_barBox);
    }

    /// <summary>Re-runs the overlay's child positioning (after the toolbar's wanted height changed).</summary>
    internal static void RelayoutOverlay()
    {
        if (_overlay != IntPtr.Zero) Gtk.gtk_widget_queue_resize(_overlay);
    }

    private static int _viewW, _viewH;

    /// <summary>"get-child-position": the toolbar box covers the window's width and exactly the height the toolbar asks for;
    /// the web view inside keeps the window's full size. GdkRectangle = x, y, width, height (ints).</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static int OnChildPosition(IntPtr overlay, IntPtr widget, IntPtr rect, IntPtr data)
    {
        try
        {
            if (widget == IntPtr.Zero || widget != _barBox) return 0;
            var w = Gtk.gtk_widget_get_allocated_width(overlay);
            var h = Gtk.gtk_widget_get_allocated_height(overlay);
            if (w <= 1 || h <= 1) return 0;
            Marshal.WriteInt32(rect, 0, 0);
            Marshal.WriteInt32(rect, 4, 0);
            Marshal.WriteInt32(rect, 8, w);
            Marshal.WriteInt32(rect, 12, LinuxBar.WantedHeight(h));
            if (w != _viewW || h != _viewH)
            {
                _viewW = w; _viewH = h;
                var view = LinuxBar.BarView;
                GLib.RunOnMainLoop(() => { if (view != IntPtr.Zero) Gtk.gtk_widget_set_size_request(view, w, h); });
            }
            return 1;
        }
        catch { return 0; }
    }

    /// <summary>Hides (or restores) the toolbar and side panel so a fullscreen page can use the whole window.</summary>
    internal static void SetChromeHidden(bool hidden)
    {
        if (_row == IntPtr.Zero) return;
        Gtk.gtk_widget_set_margin_top(_row, hidden ? 0 : _strip);
        if (_barBox != IntPtr.Zero)
        {
            if (hidden) Gtk.gtk_widget_hide(_barBox); else Gtk.gtk_widget_show(_barBox);
        }
        if (hidden && LinuxBar.SideExists) LinuxBar.HideSideForFullscreen();
    }

    /// <summary>Hides Photino's own view (it only exists to host the window) and lets go of its web process: the view
    /// stays (Photino holds a pointer to it) but nothing ever shows it again, and an idle web process costs tens of MB.</summary>
    internal static void HideBootstrap()
    {
        if (_boot == IntPtr.Zero) return;
        Gtk.gtk_widget_hide(_boot);
        var boot = _boot;
        // after Photino's own first load has settled; a failure to end it is harmless
        GLib.Every(1500, () =>
        {
            try { if (WebKit.Has("webkit_web_view_terminate_web_process")) WebKit.webkit_web_view_terminate_web_process(boot); } catch { }
            return false;
        });
    }

    /// <summary>Window background behind transparent / blank pages — the app's dark theme colour.</summary>
    internal static void SetBackground(IntPtr win) => Gtk.AddAppCss(win, "#brain-window{background-color:#110f0d;}");

    // ---------------------------------------------------------------- window state

    internal static bool IsFullScreen(IntPtr win)
    {
        var gw = Gtk.gtk_widget_get_window(win);
        return gw != IntPtr.Zero && (Gtk.gdk_window_get_state(gw) & Gtk.StateFullscreen) != 0;
    }

    internal static void SetFullScreen(IntPtr win, bool on)
    {
        if (on) Gtk.gtk_window_fullscreen(win); else Gtk.gtk_window_unfullscreen(win);
    }

    internal static void ToggleFullScreen(IntPtr win) => SetFullScreen(win, !IsFullScreen(win));

    internal static bool IsMaximized(IntPtr win)
    {
        var gw = Gtk.gtk_widget_get_window(win);
        return gw != IntPtr.Zero && (Gtk.gdk_window_get_state(gw) & Gtk.StateMaximized) != 0;
    }

    /// <summary>Raise a window and give it the focus (the window manager may still decline to steal focus).</summary>
    internal static void Activate(IntPtr win)
    {
        if (win != IntPtr.Zero) Gtk.gtk_window_present(win);
    }

    internal static void Activate(BrowserTab tab)
    {
        var win = tab.MacWindowNumber != 0 ? WindowByNumber(tab.MacWindowNumber) : IntPtr.Zero;
        if (win != IntPtr.Zero) Activate(win);
    }

    internal static double BackingScale(IntPtr win) => Math.Max(1, Gtk.gtk_widget_get_scale_factor(win));

    // ---------------------------------------------------------------- drag / resize (frameless window)

    private static void PointerRoot(out int x, out int y)
    {
        var seat = Gtk.gdk_display_get_default_seat(Gtk.gdk_display_get_default());
        var ptr = Gtk.gdk_seat_get_pointer(seat);
        Gtk.gdk_device_get_position(ptr, out _, out x, out y);
    }

    /// <summary>True while the physical left button is held. The request to start a move/resize reaches us a
    /// moment after the mousedown (page → host round trip); if the button is already up by then, starting a
    /// window-manager move loop would leave it waiting for a release that never comes.</summary>
    private static bool LeftButtonDown(IntPtr win)
    {
        var seat = Gtk.gdk_display_get_default_seat(Gtk.gdk_display_get_default());
        var ptr = Gtk.gdk_seat_get_pointer(seat);
        Gtk.gdk_device_get_state(ptr, Gtk.gtk_widget_get_window(win), IntPtr.Zero, out var mask);
        return (mask & (1 << 8)) != 0; // GDK_BUTTON1_MASK
    }

    /// <summary>Starts a window move that follows the real pointer — the toolbar's empty space calls this on
    /// mousedown (the window has no native title bar). The window manager runs the move loop.
    ///
    /// The move is handed over only once the pointer has really moved. Handing it over at the mousedown itself takes
    /// the pointer away from the page on the very first click, so a plain click never completes and a double-click on the
    /// empty toolbar (maximize / restore) never arrives — and a window manager that refuses to move a maximized window
    /// would leave the button state hanging.</summary>
    internal static void BeginWindowDrag(IntPtr win)
    {
        if (!LeftButtonDown(win)) return;
        PointerRoot(out var x0, out var y0);
        var polls = 0;
        GLib.Every(10, () =>
        {
            if (!LeftButtonDown(win) || ++polls > 600) return false; // released without moving: it was a click (or gave up)
            PointerRoot(out var x, out var y);
            if (Math.Abs(x - x0) + Math.Abs(y - y0) < 4) return true;
            // the press position, not the current one: the window manager then moves the window by the whole distance the
            // pointer has gone, so nothing of the first few pixels is lost
            Gtk.gtk_window_begin_move_drag(win, 1, x0, y0, Gtk.gtk_get_current_event_time());
            return false;
        });
    }

    /// <summary>Starts an edge/corner resize. <paramref name="hitTest"/> uses the Windows HT* codes chrome.js
    /// already sends (10 left, 11 right, 12 top, 13 top-left, 14 top-right, 15 bottom, 16 bottom-left, 17 bottom-right).</summary>
    internal static void BeginWindowResize(IntPtr win, int hitTest)
    {
        int edge = hitTest switch
        {
            10 => Gtk.EdgeWest, 11 => Gtk.EdgeEast, 12 => Gtk.EdgeNorth, 13 => Gtk.EdgeNorthWest,
            14 => Gtk.EdgeNorthEast, 15 => Gtk.EdgeSouth, 16 => Gtk.EdgeSouthWest, 17 => Gtk.EdgeSouthEast, _ => -1,
        };
        if (edge < 0 || !LeftButtonDown(win)) return;
        PointerRoot(out var x, out var y);
        Gtk.gtk_window_begin_resize_drag(win, edge, 1, x, y, Gtk.gtk_get_current_event_time());
    }

    /// <summary>The bar's thin native load indicator is a Core Animation sweep on macOS; here the toolbar view
    /// draws its own (it keeps animating — it is a separate view from the page being loaded).</summary>
    internal static void SetLoadingBar(IntPtr win, bool on) { }
}
