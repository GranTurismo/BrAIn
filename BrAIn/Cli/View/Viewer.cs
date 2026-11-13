using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace BrAIn.Cli.View;

/// <summary>
/// <c>brain view</c>: the headless browser, live in the terminal. The browser's whole window — page, tab strip, address bar,
/// menus, dialogs — is mirrored as pictures (or coloured blocks), and the terminal's mouse and keyboard are played into it as
/// real input. BrAIn's own shortcuts therefore just work (Ctrl+L address bar, Ctrl+T new tab, Ctrl+W close, Ctrl+K palette, …).
/// The viewer keeps two keys: Ctrl+Q leaves (the browser keeps running) and Ctrl+O switches to a text view of the page.
/// </summary>
internal sealed class Viewer
{
    private readonly Term _t = new();
    private TermCaps _caps = null!;
    private Painter _p = null!;
    private readonly X11 _cap, _inp;
    private readonly ControlClient.Info _info;
    private int _pageWidth;
    private readonly InputParser _parser = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly ConcurrentQueue<Ev> _textModeEvents = new();
    private volatile bool _quit, _resized, _textMode, _toggleMode;
    private long _lastInput, _lastChange, _copyIntent;
    private string _title = "", _url = "";
    private string? _flash;
    private long _flashUntil;
    private Frame? _frame;
    private Reader? _reader;
    private bool _clip;

    private Viewer(X11 cap, X11 inp, ControlClient.Info info, int pageWidth)
    {
        _cap = cap; _inp = inp; _info = info; _pageWidth = pageWidth;
    }

    internal static int Run(List<string> a)
    {
        string? gfx = null;
        var pageWidth = int.TryParse(Environment.GetEnvironmentVariable("BRAIN_VIEW_WIDTH"), out var envW) ? envW : 0;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i] == "--gfx" && i + 1 < a.Count) gfx = a[++i];
            else if (a[i] == "--width" && i + 1 < a.Count && int.TryParse(a[i + 1], out var w)) { pageWidth = w; i++; }
            else if (a[i] is "-h" or "--help")
            {
                Console.WriteLine("brain view [--gfx kitty|iterm|sixel|text|blocks] [--width CSS-pixels]");
                Console.WriteLine("  The browser live in this terminal. Mouse and keyboard go to the browser (its shortcuts work: Ctrl+L, Ctrl+T, Ctrl+W, Ctrl+K…).");
                Console.WriteLine("  Ctrl+O  reader: the page as text, links numbered   Ctrl+Q  leave (the browser keeps running)");
                Console.WriteLine("  --gfx text: the page's text as real characters over colour blocks (the default where pictures cannot be shown)");
                Console.WriteLine("  Copying in the browser (Ctrl+C) also copies to this terminal's clipboard; pasting here types into the browser.");
                return 0;
            }
        }
        if (!OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine("brain view shows a BrAIn that runs without a display on Linux (a server, a container, an SSH login).");
            Console.Error.WriteLine("Here BrAIn has its own window, which is the view. The other brain commands (open, read, snap, click …) work.");
            return 1;
        }
        if (!Posix.IsTty(0) || !Posix.IsTty(1)) { Console.Error.WriteLine("brain view needs a terminal (it was given a pipe or a file)."); return 1; }
        if (!ControlClient.EnsureRunning()) return 2;
        var info = ControlClient.GetInfo();
        if (info is null) { Console.Error.WriteLine("brain view: BrAIn is not answering"); return 2; }
        if (!info.Headless || string.IsNullOrEmpty(info.Display))
        {
            Console.Error.WriteLine("brain view: this BrAIn has a window on a desktop of its own, so there is nothing to mirror.");
            Console.Error.WriteLine("To use it from the terminal instead:  brain stop && brain start --headless && brain view");
            return 1;
        }
        var cap = X11.Open(info.Display, info.XAuthority, out var why);
        var inp = cap is null ? null : X11.Open(info.Display, info.XAuthority, out why);
        if (cap is null || inp is null) { Console.Error.WriteLine("brain view: " + why); return 2; }
        if (!inp.HasXTest()) { Console.Error.WriteLine("brain view: the browser's screen has no XTEST extension, so input cannot be sent (libXtst.so.6 missing?)."); return 2; }
        return new Viewer(cap, inp, info, pageWidth).Loop(gfx);
    }

    private int Loop(string? gfx)
    {
        if (!_t.EnterRaw()) { Console.Error.WriteLine("brain view: cannot switch the terminal to raw mode"); return 1; }
        var exit = 0;
        PosixSignalRegistration? winch = null, term = null, hup = null;
        try
        {
            _t.Put("\e[?1049h\e[?25l\e[?7l\e[2J"); // alternate screen, no cursor, no auto-wrap
            _t.Flush();
            var leftover = new List<byte>();
            _caps = _t.Probe(leftover);
            if (gfx is not null) _caps.Decide(gfx);
            _parser.PixelMouse = _caps.PixelMouse && _caps.CellW > 0;
            _t.Put("\e[?1003h\e[?1006h\e[?2004h\e[?1004h" + (_parser.PixelMouse ? "\e[?1016h" : "") + (_caps.KittyKeys ? "\e[>1u" : ""));
            _p = new Painter(_t, _caps);
            // coloured blocks are coarse: a narrower page (tablet width) makes everything on it bigger
            if (_pageWidth <= 0) _pageWidth = _caps.Gfx switch { Gfx.Blocks => 1024, Gfx.Text => 1280, _ => 1440 };
            winch = PosixSignalRegistration.Create(PosixSignal.SIGWINCH, c => { c.Cancel = true; _resized = true; _wake.Set(); });
            term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; _quit = true; _wake.Set(); });
            hup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, c => { c.Cancel = true; _quit = true; _wake.Set(); });
            Relayout();
            _clip = _cap.WatchClipboard();
            if (leftover.Count > 0) Handle(_parser.Feed(leftover.ToArray()));
            new Thread(InputLoop) { IsBackground = true, Name = "view-input" }.Start();
            Flash(_caps.Gfx switch
            {
                Gfx.Text => "Text mode: this terminal shows no pictures · Ctrl+O reader",
                Gfx.Blocks => "No pictures in this terminal: coloured blocks · Ctrl+O reader",
                _ => $"{_caps.Describe()} · mouse and keys go to the browser",
            }, 6000);
            Main();
        }
        catch (Exception ex)
        {
            exit = 1;
            Restore();
            Console.Error.WriteLine("brain view: " + ex.Message);
            return exit;
        }
        finally
        {
            winch?.Dispose(); term?.Dispose(); hup?.Dispose();
            Restore();
        }
        if (_gone) Console.Error.WriteLine("BrAIn was closed. Start it again with: brain start");
        return exit;
    }

    private bool _restored, _gone;

    private void Restore()
    {
        if (_restored) return;
        _restored = true;
        try
        {
            if (_caps?.Gfx == Gfx.Kitty) _t.Put("\e_Ga=d,d=A,q=2\e\\");
            // the page goes back to its own fonts (the character grid was only for this view)
            if (_gridUsed) ControlClient.Ask($"grid {_p.K} {_p.M} 0", TimeSpan.FromSeconds(3));
            _t.Put("\e[?1003l\e[?1006l\e[?1016l\e[?2004l\e[?1004l" + (_caps?.KittyKeys == true ? "\e[<u" : "") + "\e[0m\e[2J\e[?7h\e[?25h\e[?1049l");
            _t.Flush();
        }
        catch { }
        _t.LeaveRaw();
        try { _inp.Dispose(); } catch { }
    }

    private void Relayout()
    {
        _resized = false;
        if (!Posix.TermSize(1, out var ws)) ws = new Posix.WinSize { Cols = 80, Rows = 24 };
        if (ws.XPixel > 0 && ws.YPixel > 0 && ws.Cols > 0 && ws.Rows > 0)
        {
            _caps.CellW = ws.XPixel / ws.Cols;
            _caps.CellH = ws.YPixel / ws.Rows;
        }
        var maxW = _info.Screen is { Length: 2 } s ? s[0] : 2560;
        var maxH = _info.Screen is { Length: 2 } s2 ? s2[1] : 1600;
        _p.Layout(ws.Cols, ws.Rows, _pageWidth, maxW, maxH);
        _p.Clear();
        _frame = new Frame(_p.W, _p.H);
        ControlClient.Ask($"view {_p.W} {_p.H}");
        _reader?.Relayout(_p.Cols, _p.Rows);
        _lastChange = Environment.TickCount64;
    }

    // ---------------------------------------------------------------- main loop (captures and draws; the only writer)

    private void Main()
    {
        long lastInfo = 0;
        var misses = 0;
        while (!_quit)
        {
            if (_resized) Relayout();
            var now = Environment.TickCount64;
            if (_toggleMode) ToggleMode();
            if (now - lastInfo > 1500)
            {
                lastInfo = now;
                var j = ControlClient.Ask("info", TimeSpan.FromSeconds(3));
                if (j is not { } o)
                {
                    if (++misses >= 2) { _gone = true; break; }
                }
                else
                {
                    misses = 0;
                    _title = o.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                    _url = o.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                }
            }
            if (_textMode)
            {
                while (_textModeEvents.TryDequeue(out var ev)) _reader!.Handle(ev, this);
                _reader!.Render(_t);
            }
            else if (_caps.Gfx == Gfx.Text && _frame is not null) PaintTextFrame();
            else if (_frame is not null && _cap.Capture(_frame, 0, 0))
            {
                if (_p.Paint(_frame)) _lastChange = Environment.TickCount64;
            }
            if (_clip && _cap.ClipboardChanged() && Environment.TickCount64 - _copyIntent < 4000)
            {
                var text = _cap.ReadClipboard();
                if (!string.IsNullOrEmpty(text) && text.Length <= 100_000)
                {
                    _t.Put("\e]52;c;" + Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) + "\a");
                    Flash($"Copied {text.Length} characters to your clipboard", 2500);
                }
            }
            DrawStatus();
            PlaceCursor();
            _t.Flush();
            if (_t.Closed) break;
            // quick while something happens, slow when idle
            var busy = Environment.TickCount64 - Math.Max(_lastInput, _lastChange) < 1500;
            _wake.WaitOne(_textMode ? 200 : busy ? 33 : 250);
        }
    }

    // ---------------------------------------------------------------- text mode

    private TextLayer? _layer;
    private long _layerAt;
    private Frame? _prev;
    private bool _gridUsed, _gridFailed;

    /// <summary>Text mode: when the screen changed (or the text is a second and a half old) ask the browser where its text
    /// is, then capture the picture that goes with it, and draw both.</summary>
    private void PaintTextFrame()
    {
        if (!_cap.Capture(_frame!, 0, 0)) return;
        var now = Environment.TickCount64;
        var changed = _prev is null || _prev.W != _frame!.W || !_frame!.Px.AsSpan().SequenceEqual(_prev.Px);
        if (changed || _layer is null || now - _layerAt > 1500)
        {
            var reply = ControlClient.Ask($"grid {_p.K} {_p.M} 1", TimeSpan.FromSeconds(5));
            var layer = TextLayer.Parse(reply, _p.M, _p.K);
            if (layer is null)
            {
                if (!_gridFailed) { _gridFailed = true; Flash("The text layer is not available here: coloured blocks only", 5000); }
                _caps.Gfx = Gfx.Blocks;
                _p.Invalidate();
                return;
            }
            _gridUsed = true;
            _layer = layer;
            _layerAt = now;
            _cap.Capture(_frame!, 0, 0); // the picture as it is now, with the text where the layer says
        }
        if (_prev is null || _prev.W != _frame!.W || _prev.H != _frame.H) _prev = new Frame(_frame!.W, _frame.H);
        Buffer.BlockCopy(_frame!.Px, 0, _prev.Px, 0, _frame.Px.Length);
        if (_p.PaintText(_frame, _layer)) { _lastChange = now; _cursorDrawn = ""; }
    }

    private string _cursorDrawn = "";

    /// <summary>Text mode shows the terminal's own cursor at the focused field's caret; everywhere else it stays hidden.</summary>
    private void PlaceCursor()
    {
        var want = !_textMode && _caps.Gfx == Gfx.Text && _p.Caret is { } c ? $"\e[{c.Row + 1};{c.Col + 1}H\e[?25h" : "\e[?25l";
        if (want == _cursorDrawn && _lastStatus.Length > 0) return;
        _t.Put(want);
        _cursorDrawn = want;
    }

    private string _lastStatus = "";

    private void DrawStatus()
    {
        var right = _textMode ? " ^O picture  ^Q quit " : " ^O reader  ^Q quit ";
        string left;
        if (_flash is not null && Environment.TickCount64 < _flashUntil) left = " " + _flash;
        else if (_textMode && _reader?.Prompt is { } pr) left = " " + pr;
        else
        {
            _flash = null;
            left = " BrAIn  " + (_title.Length > 0 ? _title : _url);
        }
        var line = left + "|" + right + "|" + _p.Cols;
        if (line == _lastStatus) return;
        _lastStatus = line;
        _p.Status(left, right);
        _cursorDrawn = "";
    }

    internal void Flash(string msg, int ms = 2500)
    {
        _flash = msg;
        _flashUntil = Environment.TickCount64 + ms;
        _lastStatus = "";
    }

    internal void Wake() => _wake.Set();

    private void ToggleMode()
    {
        _toggleMode = false;
        _textMode = !_textMode;
        _p.Clear();
        _lastStatus = "";
        if (_textMode)
        {
            _reader ??= new Reader();
            _reader.Relayout(_p.Cols, _p.Rows);
            _reader.Load(this);
        }
    }

    internal void SetTextMode(bool on)
    {
        if (on != _textMode) { _toggleMode = true; _wake.Set(); }
    }

    // ---------------------------------------------------------------- input (its own thread: input reaches the browser at once)

    private void InputLoop()
    {
        while (!_quit)
        {
            var b = _t.Read(_parser.Pending ? 30 : 500);
            if (b is null)
            {
                if (_t.Closed) { _quit = true; _wake.Set(); return; }
                if (_parser.Pending) Handle(_parser.Feed([], flushEscape: true));
                continue;
            }
            try { Handle(_parser.Feed(b)); }
            catch { /* a broken sequence: drop it */ }
        }
    }

    private int _mx = -1, _my = -1;

    private void Handle(List<Ev> evs)
    {
        // only the newest of a run of mouse moves matters
        for (var i = 0; i < evs.Count; i++)
        {
            var ev = evs[i];
            if (ev is MouseEv { Motion: true } && i + 1 < evs.Count && evs[i + 1] is MouseEv { Motion: true }) continue;
            _lastInput = Environment.TickCount64;
            if (ev is KeyEv { Mods: Mods.Ctrl } k && k.Keysym == 'q') { _quit = true; _wake.Set(); return; }
            if (ev is KeyEv { Mods: Mods.Ctrl } k2 && k2.Keysym == 'o') { _toggleMode = true; _wake.Set(); continue; }
            if (ev is MouseEv sm && StatusClick(sm)) continue;
            if (_textMode) { _textModeEvents.Enqueue(ev); _wake.Set(); continue; }
            Inject(ev);
        }
        _wake.Set();
    }

    /// <summary>A click on the status bar's own buttons.</summary>
    private bool StatusClick(MouseEv m)
    {
        var (cx, cy) = Cell(m);
        if (cy != _p.Rows + 1) return false;
        if (!m.Press || m.Button != 1) return true;
        var right = _textMode ? " ^O picture  ^Q quit " : " ^O reader  ^Q quit ";
        var start = _p.Cols - right.Length + 1;
        if (cx >= start + right.IndexOf("^Q", StringComparison.Ordinal)) { _quit = true; _wake.Set(); }
        else if (cx >= start) { _toggleMode = true; _wake.Set(); }
        return true;
    }

    /// <summary>1-based cell of a mouse report.</summary>
    internal (int X, int Y) Cell(MouseEv m)
        => m.Pixel ? (m.X / Math.Max(1, _caps.CellW) + 1, m.Y / Math.Max(1, _caps.CellH) + 1) : (m.X, m.Y);

    /// <summary>Screen pixel under a mouse report (the window sits at the screen's top-left corner).</summary>
    private (int X, int Y) ScreenPoint(MouseEv m)
    {
        int x, y;
        if (m.Pixel && _caps.CellW > 0 && _caps.CellH > 0)
        {
            x = (int)((long)Math.Max(0, m.X - 1) * _p.K / _caps.CellW);
            y = (int)((long)Math.Max(0, m.Y - 1) * _p.M / _caps.CellH);
        }
        else
        {
            x = (m.X - 1) * _p.K + _p.K / 2;
            y = (m.Y - 1) * _p.M + _p.M / 2;
        }
        return (Math.Clamp(x, 0, _p.W - 1), Math.Clamp(y, 0, _p.H - 1));
    }

    private void Inject(Ev ev)
    {
        switch (ev)
        {
            case MouseEv m:
                {
                    var (x, y) = ScreenPoint(m);
                    if (m.Button is >= 4 and <= 7)
                    {
                        if (m.Press) _inp.Wheel(x, y, (uint)m.Button, 1);
                    }
                    else if (m.Motion)
                    {
                        if (x != _mx || y != _my) { _inp.Move(x, y); _mx = x; _my = y; }
                    }
                    else if (m.Button is >= 1 and <= 3 or 8 or 9)
                    {
                        // terminals report the release of any button as "no button"; X needs the one that went down
                        var btn = (uint)(m.Button >= 8 ? m.Button : m.Button);
                        // no window manager on that screen: the window under the click (a popup, the browser) gets the keyboard
                        if (m.Press) ControlClient.Ask($"focusat {x} {y}", TimeSpan.FromSeconds(2));
                        if (m.Press) _down = btn;
                        else btn = _down != 0 ? _down : btn;
                        _inp.Button(x, y, btn, m.Press);
                        if (!m.Press) _down = 0;
                        if (m.Press && btn == 3) _copyIntent = Environment.TickCount64 + 6000; // a context menu may copy
                    }
                    else if (m.Button == 0 && !m.Press && _down != 0)
                    {
                        _inp.Button(x, y, _down, false);
                        _down = 0;
                    }
                    break;
                }
            case KeyEv k:
                {
                    var key = k.Keysym;
                    if (k.Mods.HasFlag(Mods.Ctrl) && key is 'c' or 'x' or 'C' or 'X' or InputParser.Insert) _copyIntent = Environment.TickCount64;
                    _inp.Key(key, k.Mods.HasFlag(Mods.Ctrl), k.Mods.HasFlag(Mods.Alt), k.Mods.HasFlag(Mods.Shift), k.Mods.HasFlag(Mods.Super));
                    break;
                }
            case TextEv t:
                _inp.Type(t.Text);
                break;
        }
    }

    private uint _down;

    // ---------------------------------------------------------------- for the text view

    internal int Cols => _p.Cols;
    internal int Rows => _p.Rows;
    internal string Url => _url;
}
