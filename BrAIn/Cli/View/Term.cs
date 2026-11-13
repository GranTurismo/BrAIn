using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace BrAIn.Cli.View;

/// <summary>The terminal, used directly through file descriptors 0 and 1. .NET's Console is avoided on purpose: on Unix its
/// standard-input stream does line editing when stdin is a terminal, and initialising it changes keypad modes.</summary>
internal sealed partial class Term : IDisposable
{
    [LibraryImport("libc", SetLastError = true)] private static partial nint read(int fd, byte[] buf, nint count);
    [LibraryImport("libc", SetLastError = true)] private static unsafe partial nint write(int fd, byte* buf, nint count);

    private byte[]? _saved;
    private readonly MemoryStream _out = new(1 << 20);
    private readonly BlockingCollection<byte[]> _in = new();
    private Thread? _reader;

    internal bool Raw => _saved is not null;

    internal bool EnterRaw()
    {
        _saved = Posix.RawTerm(0);
        if (_saved is null) return false;
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "term-in" };
        _reader.Start();
        return true;
    }

    private void ReadLoop()
    {
        var buf = new byte[65536];
        while (true)
        {
            nint n;
            try { n = read(0, buf, buf.Length); } catch { break; }
            if (n < 0 && Marshal.GetLastPInvokeError() == 4 /* EINTR */) continue;
            if (n <= 0) { _in.CompleteAdding(); break; }
            _in.Add(buf.AsSpan(0, (int)n).ToArray());
        }
    }

    /// <summary>The next chunk of input, or null on timeout (or when the terminal went away: see <see cref="Closed"/>).</summary>
    internal byte[]? Read(int timeoutMs)
    {
        try { return _in.TryTake(out var b, timeoutMs) ? b : null; }
        catch (InvalidOperationException) { return null; }
    }

    internal bool Closed => _in.IsCompleted;

    internal void Put(string s) => Put(Encoding.UTF8.GetBytes(s));
    internal void Put(ReadOnlySpan<byte> b) => _out.Write(b);

    /// <summary>Writes everything queued, in full (a large image can take several writes).</summary>
    internal unsafe void Flush()
    {
        if (_out.Length == 0) return;
        var buf = _out.GetBuffer();
        var len = (int)_out.Length;
        fixed (byte* p = buf)
        {
            var off = 0;
            while (off < len)
            {
                var n = write(1, p + off, len - off);
                if (n < 0)
                {
                    var e = Marshal.GetLastPInvokeError();
                    if (e is 4 or 11 /* EINTR, EAGAIN */) { Thread.Sleep(1); continue; }
                    break;
                }
                off += (int)n;
            }
        }
        _out.SetLength(0);
    }

    internal static void WriteNow(string s)
    {
        var t = new Term();
        t.Put(s);
        t.Flush();
    }

    internal void LeaveRaw()
    {
        Posix.RestoreTerm(0, _saved);
        _saved = null;
    }

    public void Dispose() => LeaveRaw();

    // ---------------------------------------------------------------- what the terminal can do

    /// <summary>Asks the terminal what it supports (the answers come back on stdin) and waits for the reply to the last
    /// question, which every terminal answers (DA1). Must be in raw mode. Input that is not an answer is kept for the caller.</summary>
    internal TermCaps Probe(List<byte> leftover)
    {
        Put("\e_Gi=31,s=1,v=1,a=q,t=d,f=24;AAAA\e\\"  // kitty graphics: answers OK when it can show images
            + "\e[>0q"                                // XTVERSION: the terminal's name
            + "\e[16t"                                // the size of one cell in pixels
            + "\e[?1016$p"                            // can it report the mouse in pixels?
            + "\e[?u"                                 // the kitty keyboard protocol?
            + "\e[c");                                // DA1, answered by everyone: the end of the answers (4 = sixel)
        Flush();
        var got = new StringBuilder();
        var raw = new List<byte>();
        var deadline = Environment.TickCount64 + 2000;
        while (Environment.TickCount64 < deadline)
        {
            var b = Read(Math.Max(1, (int)(deadline - Environment.TickCount64)));
            if (b is null) { if (Closed) break; continue; }
            raw.AddRange(b);
            got.Append(Encoding.Latin1.GetString(b));
            if (Da1().IsMatch(got.ToString())) break;
        }
        var s = got.ToString();
        var caps = new TermCaps();
        caps.KittyGraphics = Regex.IsMatch(s, "\e_Gi=31;OK");
        var xv = Regex.Match(s, "\eP>\\|([^\e]*)\e\\\\");
        if (xv.Success) caps.Name = xv.Groups[1].Value;
        var cell = Regex.Match(s, "\e\\[6;(\\d+);(\\d+)t");
        if (cell.Success) { caps.CellH = int.Parse(cell.Groups[1].Value); caps.CellW = int.Parse(cell.Groups[2].Value); }
        var pm = Regex.Match(s, "\e\\[\\?1016;(\\d)\\$y");
        caps.PixelMouse = pm.Success && pm.Groups[1].Value is "1" or "2";
        caps.KittyKeys = Regex.IsMatch(s, "\e\\[\\?\\d+u");
        var da = Da1().Match(s);
        caps.Sixel = da.Success && da.Groups[1].Value.Split(';').Contains("4");
        caps.Answered = da.Success;
        // keep what the user typed meanwhile: everything that is not one of the answers
        var rest = Regex.Replace(s, "\e_G[^\e]*\e\\\\|\eP[^\e]*\e\\\\|\e\\[[0-9;?$]*[tyuc]", "");
        leftover.AddRange(Encoding.Latin1.GetBytes(rest));
        if (caps.CellW <= 0 && Posix.TermSize(1, out var ws) && ws.XPixel > 0 && ws.YPixel > 0)
        {
            caps.CellW = ws.XPixel / ws.Cols;
            caps.CellH = ws.YPixel / ws.Rows;
        }
        caps.Decide();
        return caps;
    }

    [GeneratedRegex("\e\\[\\?([0-9;]*)c")] private static partial Regex Da1();
}

internal enum Gfx { Kitty, Iterm, Sixel, Text, Blocks }

/// <summary>What the terminal answered, and the way pictures are drawn as a result.</summary>
internal sealed class TermCaps
{
    public bool KittyGraphics, Sixel, PixelMouse, KittyKeys, Answered;
    public string Name = "";
    public int CellW, CellH;
    public Gfx Gfx = Gfx.Blocks;
    public bool TrueColor;

    /// <summary>Picks the picture protocol: BRAIN_VIEW_GFX wins; inside tmux/screen pictures do not pass through, so text;
    /// then kitty graphics (kitty, Ghostty, WezTerm), iTerm2's own (iTerm2, WezTerm), sixel (xterm, foot, Windows Terminal,
    /// …), and everywhere else real text over coloured half blocks (`blocks` = the half blocks alone).</summary>
    internal void Decide(string? forced = null)
    {
        forced ??= Environment.GetEnvironmentVariable("BRAIN_VIEW_GFX");
        var env = (string k) => Environment.GetEnvironmentVariable(k) ?? "";
        var iterm = Name.Contains("iTerm2", StringComparison.OrdinalIgnoreCase) || env("LC_TERMINAL") == "iTerm2" || env("TERM_PROGRAM") == "iTerm.app";
        var wez = Name.Contains("WezTerm", StringComparison.OrdinalIgnoreCase) || env("TERM_PROGRAM") == "WezTerm";
        var muxed = env("TMUX").Length > 0 || env("STY").Length > 0;
        TrueColor = KittyGraphics || iterm || wez || env("COLORTERM") is "truecolor" or "24bit" ||
                    Name.Contains("ghostty", StringComparison.OrdinalIgnoreCase) || Name.Contains("foot", StringComparison.OrdinalIgnoreCase) ||
                    env("TERM").Contains("direct") || env("BRAIN_VIEW_COLOR") == "24";
        if (env("BRAIN_VIEW_COLOR") == "256") TrueColor = false;
        Gfx = forced?.ToLowerInvariant() switch
        {
            "kitty" => Gfx.Kitty,
            "iterm" or "iterm2" => Gfx.Iterm,
            "sixel" => Gfx.Sixel,
            "text" => Gfx.Text,
            "blocks" or "ascii" => Gfx.Blocks,
            _ => muxed ? Gfx.Text
                : KittyGraphics ? Gfx.Kitty
                : iterm || wez ? Gfx.Iterm
                : Sixel && CellW > 0 && CellH > 0 ? Gfx.Sixel
                : Gfx.Text,
        };
        if (Gfx == Gfx.Sixel && (CellW <= 0 || CellH <= 0)) { CellW = 10; CellH = 20; }
    }

    internal string Describe() => Gfx switch
    {
        Gfx.Kitty => "kitty graphics",
        Gfx.Iterm => "iTerm2 images",
        Gfx.Sixel => "sixel",
        Gfx.Text => "real text over colour blocks",
        _ => TrueColor ? "colour blocks" : "256-colour blocks",
    };
}
