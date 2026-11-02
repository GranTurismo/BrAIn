using System.Globalization;

namespace BrAIn.Browser.Linux;

/// <summary>
/// Split-toolbar mode on Linux (counterpart of <c>Mac/WkBar.cs</c>): the toolbar lives in its own transparent
/// web view layered over the top of the window, above the page views — so every site lays out below the bar with
/// no shifting of fixed headers or 100vh bugs. The bar view runs chrome.js with <c>window.__BRAIN_ROLE='bar'</c>,
/// talks to the host through a "brain" script-message handler and receives host pushes through evaluate_javascript.
/// It grows over the page (never moves it) while a popover or the command palette is open: see <see cref="Layout"/>.
///
/// Also owns the Ollama side panel, a second view docked to the right of the page area.
/// Everything must run on the GTK main thread.
/// </summary>
internal static unsafe partial class LinuxBar
{
    internal const double BarHeight = LinuxWindow.BarHeight;
    internal const double SidePanelWidth = 400;

    private static IntPtr _bar, _side;
    private static string _mode = "bar";
    private static bool _sideOpen;

    /// <summary>Receives every "brain:..." command string the toolbar view posts.</summary>
    internal static Action<string>? OnBarMessage;
    /// <summary>Receives every "brain:..." command string the side panel posts.</summary>
    internal static Action<string>? OnSideMessage;

    internal static bool Exists => _bar != IntPtr.Zero;
    internal static IntPtr BarView => _bar;
    internal static bool SideExists => _side != IntPtr.Zero;
    internal static bool SideOpen => _sideOpen;

    /// <summary>Creates the toolbar view over the window and loads <paramref name="html"/>.
    /// <paramref name="script"/> (the chrome.js bundle plus its window.external shim) runs at document start.</summary>
    internal static bool Create(IntPtr win, string script, string html, string? iconPng = null)
    {
        if (_bar != IntPtr.Zero) return true;
        if (win == IntPtr.Zero || !LinuxWindow.Setup(win, iconPng)) return false;

        var ucm = LinuxEngine.NewContentManager(s =>
        {
            if (s.StartsWith("brain:dbg:", StringComparison.Ordinal)) { Console.Error.WriteLine("[bar-debug] " + s[10..]); return; }
            OnBarMessage?.Invoke(s);
        });
        LinuxEngine.AddScript(ucm, LinuxBackdrop.Js + script);
        if (DebugBar) LinuxEngine.AddScript(ucm, DebugProbe);
        var view = LinuxEngine.NewChromeView(ucm, LinuxEngine.NewSettings(false));
        if (view == IntPtr.Zero) return false;

        // Opaque, in the app's dark colour. (macOS makes this view transparent so the page shows through while a
        // popover is open; WebKitGTK cannot composite that reliably, so LinuxBackdrop paints the page behind it.)
        WebKit.SetBackground(view, 0x11 / 255.0, 0x0f / 255.0, 0x0d / 255.0, 1);
        LinuxWindow.AddOverlay(view);
        _bar = view;
        _mode = "bar";
        _applied = "bar";
        ApplyLayout(win);

        WebKit.webkit_web_view_load_html(view, html, "https://brain-bar.invalid/");
        Console.Error.WriteLine("[BrAIn] split: bar view created");
        AttachDebug(win, view);
        return true;
    }

    private static readonly bool DebugBar = Environment.GetEnvironmentVariable("BRAIN_DEBUG_BAR") == "1";
    private static string _applied = "bar";   // the size the view really has right now
    private static int _gen;                    // newest layout request wins

    /// <summary>Sizes the toolbar view: "bar" = the 46px strip, "full" = the whole window (palette / menus), or a
    /// pixel height for small popovers hanging below the strip. The page area never moves, only the side panel
    /// takes width from it. Growing from the strip first captures the page behind the new area
    /// (<see cref="LinuxBackdrop"/>), so the popover appears a few milliseconds later with the page "showing through".</summary>
    internal static void Layout(IntPtr win, string? mode = null)
    {
        if (_bar == IntPtr.Zero || win == IntPtr.Zero) return;
        var gen = ++_gen;
        if (DebugBar) Console.Error.WriteLine($"[bar-layout] request mode={mode ?? "(refit)"} applied={_applied} t={Environment.TickCount64 % 100000}");
        if (mode is null) { ApplyLayout(win); return; }          // window resized / side panel toggled
        _mode = mode;
        if (mode == "bar")
        {
            ApplyLayout(win);
            LinuxBackdrop.Clear();
            return;
        }
        var fromStrip = _applied == "bar";
        if (fromStrip && LinuxTabs.Active != IntPtr.Zero && !LinuxFullscreen.Active)
        {
            LinuxBackdrop.Prepare(LinuxTabs.Active, BarHeight, () => { if (gen == _gen) ApplyLayout(win); });
            return;
        }
        ApplyLayout(win);
    }

    /// <summary>The toolbar view's height for a window <paramref name="windowH"/> tall: the strip, a popover's height,
    /// or the whole window.</summary>
    internal static int WantedHeight(int windowH)
    {
        if (_mode == "full") return windowH;
        if (double.TryParse(_mode, NumberStyles.Float, CultureInfo.InvariantCulture, out var px))
            return (int)Math.Clamp(px, BarHeight, windowH > BarHeight ? windowH : 4000);
        return (int)BarHeight;
    }

    private static void ApplyLayout(IntPtr win)
    {
        if (_bar == IntPtr.Zero || win == IntPtr.Zero) return;
        var shrinking = _applied != "bar" && _mode == "bar";
        _applied = _mode;
        LinuxWindow.RelayoutOverlay();

        if (_side != IntPtr.Zero)
        {
            var w = Gtk.gtk_widget_get_allocated_width(win);
            var sw = SideWidth(w);
            if (sw > 0)
            {
                Gtk.gtk_widget_set_size_request(_side, (int)sw, -1);
                Gtk.gtk_widget_show(_side);
            }
            else Gtk.gtk_widget_hide(_side);
        }

        if (DebugBar)
            GLib.Every(600, () =>
            {
                var barWin = Gtk.gtk_widget_get_window(_bar);
                var boxWin = Gtk.gtk_widget_get_window(LinuxWindow.BarBox);
                var pageWin = LinuxTabs.Active != IntPtr.Zero ? Gtk.gtk_widget_get_window(LinuxTabs.Active) : IntPtr.Zero;
                string Name(IntPtr w) => w == barWin ? "BAR-VIEW" : w == boxWin ? "BAR-BOX" : w == pageWin ? "PAGE" : w.ToString("x");
                var sb = new System.Text.StringBuilder();
                foreach (var win0 in new[] { Gtk.gdk_window_get_parent(barWin), Gtk.gdk_window_get_parent(boxWin), Gtk.gdk_window_get_parent(pageWin) }.Distinct())
                {
                    sb.Append("[children of ").Append(win0.ToString("x")).Append(" top→bottom: ");
                    foreach (var ch in GLib.ListItems(Gtk.gdk_window_get_children(win0))) sb.Append(Name(ch)).Append(' ').Append(Gtk.gdk_window_get_height(ch)).Append("  ");
                    sb.Append(']');
                }
                Console.Error.WriteLine($"[bar-layout] applied {_applied}: bar view {Gtk.gtk_widget_get_allocated_width(_bar)}x{Gtk.gtk_widget_get_allocated_height(_bar)}, box {Gtk.gtk_widget_get_allocated_width(LinuxWindow.BarBox)}x{Gtk.gtk_widget_get_allocated_height(LinuxWindow.BarBox)} {sb}");
                return false;
            });
        // The page view is not repainted by itself in the area the toolbar just uncovered.
        if (shrinking) RepaintPages();
    }

    private static void RepaintPages()
    {
        // Shrinking the shape exposes the page window's area without any repaint request of its own: invalidate it.
        var pv = LinuxTabs.Active;
        var pw = pv == IntPtr.Zero ? IntPtr.Zero : Gtk.gtk_widget_get_window(pv);
        if (pw != IntPtr.Zero) Gtk.gdk_window_invalidate_rect(pw, IntPtr.Zero, 1);
        if (LinuxTabs.Active != IntPtr.Zero) Gtk.gtk_widget_queue_draw(LinuxTabs.Active);
        if (LinuxWindow.Overlay != IntPtr.Zero) Gtk.gtk_widget_queue_draw(LinuxWindow.Overlay);
        // a second request after the resize has been allocated: the first one can land on the old geometry
        GLib.RunOnMainLoop(() => { if (LinuxTabs.Active != IntPtr.Zero) Gtk.gtk_widget_queue_draw(LinuxTabs.Active); });
    }

    /// <summary>Runs JS in the toolbar view (host → bar pushes and commands).</summary>
    internal static void Eval(string js) => WebKit.EvalFireAndForget(_bar, js);

    // Keyboard focus moves between the toolbar view and the page view as the user works (Ctrl+K opens the palette in the toolbar,
    // Esc hands the keyboard back to the page). Those moves are requested by the very key press that is still being handled, and
    // WebKitGTK answers a key its document did not consume a few milliseconds later: it re-dispatches the key through GTK and flags
    // the view that sent it to let its NEXT key through untouched. If the focus has moved on in between, the re-dispatched key lands
    // on the other view and the flag stays on the first one, which then swallows the next real key it receives (the first letter
    // typed into the palette vanished) — and the key the toolbar had already handled (Esc) also reached the page. So a focus move
    // waits until that round trip is over; the newest request wins.
    private const uint FocusDelayMs = 40;
    private static int _focusGen;

    private static void MoveFocus(IntPtr win, bool toBar)
    {
        if (win == IntPtr.Zero) return;
        var gen = ++_focusGen;
        GLib.Every(FocusDelayMs, () =>
        {
            if (gen != _focusGen) return false;
            if (toBar)
            {
                if (_bar != IntPtr.Zero) Gtk.gtk_widget_grab_focus(_bar);
            }
            else if (LinuxTabs.Active != IntPtr.Zero) LinuxTabs.FocusActive(win);
            else if (LinuxWindow.Boot != IntPtr.Zero) Gtk.gtk_widget_grab_focus(LinuxWindow.Boot);
            return false;
        });
    }

    internal static void FocusBar(IntPtr win) => MoveFocus(win, true);

    internal static void FocusPage(IntPtr win) => MoveFocus(win, false);

    // ------------------------------------------------------------------ side panel

    /// <summary>Width the page area gives up to the panel (0 while it is closed or the window is tiny).</summary>
    internal static double SideWidth(double windowWidth)
        => _sideOpen && _side != IntPtr.Zero && windowWidth > SidePanelWidth + 320 ? SidePanelWidth : 0;

    /// <summary>Creates the side panel (hidden) to the right of the page area and loads <paramref name="html"/>.</summary>
    internal static bool CreateSide(IntPtr win, string script, string html)
    {
        if (_side != IntPtr.Zero) return true;
        if (_bar == IntPtr.Zero || win == IntPtr.Zero) return false;

        var ucm = LinuxEngine.NewContentManager(s => OnSideMessage?.Invoke(s));
        LinuxEngine.AddScript(ucm, script);
        var view = LinuxEngine.NewChromeView(ucm, LinuxEngine.NewSettings(false));
        if (view == IntPtr.Zero) return false;
        WebKit.SetBackground(view, 0x11 / 255.0, 0x0f / 255.0, 0x0d / 255.0, 1);
        Gtk.gtk_widget_set_size_request(view, (int)SidePanelWidth, -1);
        Gtk.gtk_box_pack_start(LinuxWindow.Row, view, 0, 0, 0); // after the page stack; the bar overlay paints over it
        _side = view;
        WebKit.webkit_web_view_load_html(view, html, "https://brain-side.invalid/");
        return true;
    }

    /// <summary>Shows or hides the panel and re-lays-out the page area. Returns the new state.</summary>
    internal static bool SetSideOpen(IntPtr win, bool open)
    {
        if (_side == IntPtr.Zero) return false;
        _sideOpen = open;
        Layout(win);
        if (open && SideWidth(Gtk.gtk_widget_get_allocated_width(win)) > 0) Gtk.gtk_widget_grab_focus(_side);
        else FocusPage(win);
        return _sideOpen;
    }

    internal static void EvalSide(string js) => WebKit.EvalFireAndForget(_side, js);

    /// <summary>A fullscreen page takes the whole window: the panel steps aside (Layout brings it back).</summary>
    internal static void HideSideForFullscreen()
    {
        if (_side != IntPtr.Zero) Gtk.gtk_widget_hide(_side);
    }
}
