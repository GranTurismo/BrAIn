using System.Text.Json;

namespace BrAIn.Browser;

/// <summary>Linux without a display: the browser runs on its own invisible screen (Cli/Headless.cs) and is used through the
/// <c>brain</c> command and its terminal viewer. There is no window manager on that screen, so the window has no
/// min/max/close buttons (closing is <c>brain stop</c>), and the welcome film is skipped (nobody is watching it start).</summary>
public sealed partial class BrowserController
{
    /// <summary>Set by Program before <see cref="RunWindow"/> when this process drew its own screen.</summary>
    public bool Headless
    {
        get => _headless;
        set { _headless = value; if (IsLinux) Linux.LinuxPopup.Headless = value; if (IsMac) Mac.MacView.Headless = value; }
    }
    private bool _headless;

    /// <summary>For the <c>brain</c> command: is the browser ready to be driven yet (its first view exists), and if its
    /// start failed, why.</summary>
    internal (bool Ready, string? Error) CliState
        => (_driverReady.Task.IsCompletedSuccessfully,
            _driverReady.Task.IsFaulted ? _driverReady.Task.Exception?.GetBaseException().Message : null);

    /// <summary>Right after a start the page's answer to the first evaluation can be lost (the first tool call then waits
    /// for its full timeout). `brain start` calls this once before handing over: it evaluates a constant in the active tab,
    /// outside the activity log, until an answer comes back (at most ~15 s).</summary>
    internal async Task<string> WarmUpAsync()
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < until)
        {
            try
            {
                var tab = ActiveTabOrThrow();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                await DriverOrThrowAsync(tab, cts.Token);
                var r = await EvaluateAsync("1+1", false, tab, cts.Token);
                if (r is { ValueKind: JsonValueKind.Number } n && n.GetInt32() == 2) return "{\"ok\":true}";
            }
            catch { /* not yet */ }
            await Task.Delay(250);
        }
        return "{\"ok\":false}";
    }

    /// <summary>The terminal viewer's text layer (Cli/TextGridJs.cs): where the visible text of the toolbar and of the active
    /// page sits, in terminal cells of k×m pixels. Evaluated through WebKit directly (the page in an isolated world), so it is
    /// not an agent action: no activity log, no agent indicator. <paramref name="css"/>: 1 lays the page out on a character
    /// grid, 0 takes that away, -1 leaves it. JSON: { bar, page, barH (the toolbar view's height), strip (its own strip) }.</summary>
    internal async Task<string> TextGridAsync(int k, int m, int css)
    {
        if (Headless && IsMac) return await MacTextGridAsync(Math.Clamp(k, 2, 64), Math.Clamp(m, 2, 128), css);
        if (!Headless || !IsLinux || !Linux.LinuxEval.Available) return "{\"error\":\"the text layer needs a headless BrAIn on Linux\"}";
        k = Math.Clamp(k, 2, 64); m = Math.Clamp(m, 2, 128);
        Task<string?> barTask = Task.FromResult<string?>(null), pageTask = Task.FromResult<string?>(null);
        var popups = new List<(Task<string?> Text, int X, int Y, int W, int H, int ViewY, string Title)>();
        int barH = 0, winH = 0, strip = 0;
        try
        {
            InvokeOnUi(() =>
            {
                var win = Linux.LinuxWindow.Window;
                if (win == IntPtr.Zero) return;
                Linux.Gtk.gtk_window_get_size(win, out _, out winH);
                var bar = Linux.LinuxBar.BarView;
                var box = Linux.LinuxWindow.BarBox;
                if (bar != IntPtr.Zero && box != IntPtr.Zero && Linux.Gtk.gtk_widget_get_visible(box) != 0)
                {
                    barH = Linux.LinuxBar.WantedHeight(winH);
                    strip = (int)Linux.LinuxWindow.BarHeight;
                    barTask = Linux.LinuxEval.EvalAsync(bar, Cli.TextGridJs.Source + $"({k},{m},0,0,-1)", null);
                }
                var view = Tabs.Active?.NativeView ?? IntPtr.Zero;
                if (view != IntPtr.Zero)
                {
                    Linux.Gtk.gtk_widget_translate_coordinates(view, win, 0, 0, out var ox, out var oy);
                    pageTask = Linux.LinuxEval.EvalAsync(view, Cli.TextGridJs.Source + $"({k},{m},{ox},{oy},{css})", "brain-view");
                }
                // popup windows (sign-in, payment): their page, drawn over everything at their place on the screen
                foreach (var (pv, pw, title) in Linux.LinuxPopup.OpenWindows())
                {
                    var gw = Linux.Gtk.gtk_widget_get_window(pw);
                    var gv = Linux.Gtk.gtk_widget_get_window(pv);
                    if (gw == IntPtr.Zero || gv == IntPtr.Zero || Linux.Gtk.gtk_widget_get_visible(pw) == 0) continue;
                    Linux.Gtk.gdk_window_get_origin(gw, out var wx, out var wy);
                    Linux.Gtk.gdk_window_get_origin(gv, out var vx, out var vy);
                    Linux.Gtk.gtk_window_get_size(pw, out var ww, out var wh);
                    var t = Linux.LinuxEval.EvalAsync(pv, Cli.TextGridJs.Source + $"({k},{m},{vx},{vy},{css})", "brain-view");
                    popups.Add((t, wx, wy, Math.Max(ww, vx - wx + Linux.Gtk.gtk_widget_get_allocated_width(pv)),
                        Math.Max(wh, vy - wy + Linux.Gtk.gtk_widget_get_allocated_height(pv)), vy, title));
                }
            });
            var all = Task.WhenAll(new[] { barTask, pageTask }.Concat(popups.Select(p => p.Text)));
            await Task.WhenAny(all, Task.Delay(4000));
        }
        catch (Exception ex) { return JsonSerializer.Serialize(new { error = ex.Message }); }
        static string Part(Task<string?> t) => t.IsCompletedSuccessfully && t.Result is { Length: > 0 } s && s[0] == '{' ? s : "null";
        var pops = string.Join(",", popups.Select(p =>
            $"{{\"page\":{Part(p.Text)},\"x\":{p.X},\"y\":{p.Y},\"w\":{p.W},\"h\":{p.H},\"viewY\":{p.ViewY},\"title\":{JsonSerializer.Serialize(p.Title)}}}"));
        return $"{{\"bar\":{Part(barTask)},\"page\":{Part(pageTask)},\"barH\":{barH},\"strip\":{strip},\"popups\":[{pops}]}}";
    }

    /// <summary>The viewer is about to click at (x,y) on the invisible screen. There is no window manager to move the keyboard
    /// focus with a click, so give it to the window under that point (a popup, or the browser window) if it lacks it.</summary>
    internal string FocusAt(int x, int y)
    {
        if (!Headless || !IsLinux) return "{\"ok\":false}";
        try
        {
            InvokeOnUi(() =>
            {
                IntPtr target = IntPtr.Zero;
                // popups lie over the browser window: the newest one that contains the point wins
                foreach (var (_, pw, _) in Linux.LinuxPopup.OpenWindows())
                {
                    var gw = Linux.Gtk.gtk_widget_get_window(pw);
                    if (gw == IntPtr.Zero || Linux.Gtk.gtk_widget_get_visible(pw) == 0) continue;
                    Linux.Gtk.gdk_window_get_origin(gw, out var wx, out var wy);
                    Linux.Gtk.gtk_window_get_size(pw, out var ww, out var wh);
                    if (x >= wx && y >= wy && x < wx + ww + 40 && y < wy + wh + 60) target = pw; // (+ the header GTK draws)
                }
                if (target == IntPtr.Zero) target = Linux.LinuxWindow.Window;
                if (target != IntPtr.Zero && Linux.Gtk.gtk_window_is_active(target) == 0) Linux.LinuxWindow.Activate(target);
            });
        }
        catch { }
        return "{\"ok\":true}";
    }

    /// <summary>The terminal viewer asks for a window exactly the size of what it can show, at the screen's top-left corner
    /// (where it captures). Returns the size the window was asked to take, as JSON.</summary>
    internal string SetHeadlessViewSize(int w, int h)
    {
        if (Headless && IsMac) return MacSetViewSize(w, h);
        if (!Headless || !IsLinux) return "{\"error\":\"this BrAIn is not headless\"}";
        w = Math.Clamp(w, 320, Cli.Headless.ScreenW);
        h = Math.Clamp(h, 240, Cli.Headless.ScreenH);
        try
        {
            InvokeOnUi(() =>
            {
                foreach (var win in Linux.LinuxWindow.AllWindows())
                {
                    if (win != Linux.LinuxWindow.Window) continue;
                    Linux.Gtk.gtk_window_resize(win, w, h);
                    Linux.Gtk.gtk_window_move(win, 0, 0);
                    Linux.LinuxWindow.Activate(win);
                }
            });
        }
        catch (Exception ex) { return JsonSerializer.Serialize(new { error = ex.Message }); }
        return JsonSerializer.Serialize(new { ok = true, w, h });
    }
}
