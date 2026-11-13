using System.Text;

namespace BrAIn.Cli.View;

/// <summary>
/// Draws frames of the browser's screen into the terminal and keeps them up to date cheaply.
///
/// The window is sized so that every terminal cell covers exactly K×M of its pixels. A cell therefore always maps to the same
/// pixel block, so after the first frame only the cells that changed are sent again — a blinking caret or a spinner costs a
/// few hundred bytes, not a whole screenshot — and partial pictures line up with the rest without seams.
/// Kitty and iTerm2 get PNG (scaled by the terminal), sixel gets the terminal's real pixels, and every other terminal gets
/// coloured half blocks (▀: foreground = upper half, background = lower half), diffed cell by cell.
/// </summary>
internal sealed class Painter(Term t, TermCaps caps)
{
    internal int Cols, Rows, K, M;
    internal int W => Cols * K;
    internal int H => Rows * M;

    private Frame? _shown;           // what the terminal shows now (picture protocols)
    private int[]? _cells;           // blocks: top colour, bottom colour per cell
    private bool _full = true;
    private int _kittyNext = 1;
    private readonly List<int> _kittyLive = [];
    internal long BytesSent;

    /// <summary>Fits the picture to <paramref name="cols"/>×<paramref name="rows"/> cells (the last row is the status bar),
    /// aiming at a page about <paramref name="pageWidth"/> CSS pixels wide.</summary>
    internal void Layout(int cols, int rows, int pageWidth, int maxW, int maxH)
    {
        Cols = Math.Max(10, cols);
        Rows = Math.Max(3, rows - 1);
        var aspect = caps.CellW > 0 && caps.CellH > 0 ? (double)caps.CellH / caps.CellW : 2.0;
        aspect = Math.Clamp(aspect, 1.2, 3.5);
        K = Math.Clamp((int)Math.Round(pageWidth / (double)Cols), 2, 64);
        if (Cols * K > maxW) K = Math.Max(1, maxW / Cols);
        M = Math.Clamp((int)Math.Round(K * aspect), 2, 128);
        if (Rows * M > maxH) M = Math.Max(2, maxH / Rows);
        Invalidate();
    }

    /// <summary>The next frame is drawn in full (after a resize, the text view, a terminal that lost our pictures).</summary>
    internal void Invalidate()
    {
        _full = true;
        _shown = null;
        _cells = null;
        _tCh = null;
    }

    /// <summary>Removes our pictures and blanks the screen.</summary>
    internal void Clear()
    {
        if (caps.Gfx == Gfx.Kitty) { t.Put("\e_Ga=d,d=A,q=2\e\\"); _kittyLive.Clear(); }
        t.Put("\e[0m\e[2J");
        Invalidate();
    }

    /// <summary>Draws what changed since the last frame. Returns true when anything was sent.</summary>
    internal bool Paint(Frame f)
    {
        if (f.W != W || f.H != H) return false;
        if (caps.Gfx == Gfx.Blocks) return PaintBlocks(f);

        var regions = new List<(int C, int R, int Cw, int Ch)>();
        if (_full || _shown is null || (caps.Gfx == Gfx.Kitty && _kittyLive.Count > 400))
            regions.Add((0, 0, Cols, Rows));
        else
        {
            var changed = ChangedCells(f, _shown);
            if (changed is null) return false;
            regions = Bands(changed);
            var area = regions.Sum(r => r.Cw * r.Ch);
            if (regions.Count > 40 || area > Cols * Rows * 6 / 10) regions = [(0, 0, Cols, Rows)];
        }
        var full = regions.Count == 1 && regions[0] == (0, 0, Cols, Rows);
        var oldKitty = full && caps.Gfx == Gfx.Kitty ? _kittyLive.ToArray() : [];
        foreach (var (c, r, cw, ch) in regions) Draw(f, c, r, cw, ch);
        // a new full picture covers the old ones: free them only now, so the screen never shows a gap
        foreach (var id in oldKitty) { t.Put($"\e_Ga=d,d=I,i={id},q=2\e\\"); _kittyLive.Remove(id); }
        _shown ??= new Frame(f.W, f.H);
        foreach (var (c, r, cw, ch) in regions)
            for (var y = r * M; y < (r + ch) * M; y++)
                Buffer.BlockCopy(f.Px, y * f.Stride + c * K * 4, _shown.Px, y * f.Stride + c * K * 4, cw * K * 4);
        _full = false;
        return true;
    }

    /// <summary>Cells whose pixels differ (rows that are equal as a whole are skipped with one comparison).</summary>
    private bool[]? ChangedCells(Frame a, Frame b)
    {
        bool[]? grid = null;
        var seg = K * 4;
        for (var y = 0; y < H; y++)
        {
            var row = y * a.Stride;
            if (a.Px.AsSpan(row, a.Stride).SequenceEqual(b.Px.AsSpan(row, a.Stride))) continue;
            grid ??= new bool[Cols * Rows];
            var r = y / M;
            for (var c = 0; c < Cols; c++)
            {
                if (grid[r * Cols + c]) continue;
                var o = row + c * seg;
                if (!a.Px.AsSpan(o, seg).SequenceEqual(b.Px.AsSpan(o, seg))) grid[r * Cols + c] = true;
            }
        }
        return grid;
    }

    /// <summary>Groups changed cells into a few rectangles by recursive XY-cut: shrink to the changed cells' bounding box,
    /// split at a run of unchanged rows, else at a gap of unchanged columns, until no gap is left. A glow along the window's
    /// edges and a button that changed in the middle thus become thin edge strips plus one small box, not the whole screen.</summary>
    private List<(int, int, int, int)> Bands(bool[] grid)
    {
        var res = new List<(int, int, int, int)>();
        Cut(grid, 0, 0, Cols, Rows, res, 0);
        return res;
    }

    private void Cut(bool[] g, int c0, int r0, int c1, int r1, List<(int, int, int, int)> res, int depth)
    {
        // bounding box of the changed cells in [c0,c1) x [r0,r1)
        int minC = int.MaxValue, maxC = -1, minR = int.MaxValue, maxR = -1;
        for (var r = r0; r < r1; r++)
            for (var c = c0; c < c1; c++)
                if (g[r * Cols + c]) { if (c < minC) minC = c; if (c > maxC) maxC = c; if (r < minR) minR = r; maxR = r; }
        if (maxC < 0) return;
        c0 = minC; c1 = maxC + 1; r0 = minR; r1 = maxR + 1;
        if (depth < 24 && res.Count < 40)
        {
            // a fully unchanged row splits it
            for (var r = r0 + 1; r < r1 - 1; r++)
            {
                var any = false;
                for (var c = c0; c < c1 && !any; c++) any = g[r * Cols + c];
                if (any) continue;
                Cut(g, c0, r0, c1, r, res, depth + 1);
                Cut(g, c0, r + 1, c1, r1, res, depth + 1);
                return;
            }
            // else a gap of at least two unchanged columns
            var run = 0;
            for (var c = c0 + 1; c < c1 - 1; c++)
            {
                var any = false;
                for (var r = r0; r < r1 && !any; r++) any = g[r * Cols + c];
                run = any ? 0 : run + 1;
                if (run < 2) continue;
                Cut(g, c0, r0, c - 1, r1, res, depth + 1);
                Cut(g, c + 1, r0, c1, r1, res, depth + 1);
                return;
            }
        }
        res.Add((c0, r0, c1 - c0, r1 - r0));
    }

    private void Draw(Frame f, int c, int r, int cw, int ch)
    {
        var before = 0L;
        t.Put($"\e[{r + 1};{c + 1}H");
        switch (caps.Gfx)
        {
            case Gfx.Kitty:
                {
                    var png = Png.Encode(f, c * K, r * M, cw * K, ch * M);
                    var id = _kittyNext++;
                    if (_kittyNext > 1_000_000) _kittyNext = 1;
                    var b64 = Convert.ToBase64String(png);
                    for (var i = 0; i < b64.Length; i += 4096)
                    {
                        var part = b64.AsSpan(i, Math.Min(4096, b64.Length - i));
                        var more = i + 4096 < b64.Length ? 1 : 0;
                        t.Put(i == 0 ? $"\e_Ga=T,f=100,i={id},q=2,C=1,c={cw},r={ch},m={more};" : $"\e_Gm={more};");
                        t.Put(Encoding.ASCII.GetBytes(part.ToString()));
                        t.Put("\e\\");
                    }
                    _kittyLive.Add(id);
                    before = b64.Length;
                    break;
                }
            case Gfx.Iterm:
                {
                    var png = Png.Encode(f, c * K, r * M, cw * K, ch * M);
                    t.Put($"\e]1337;File=inline=1;size={png.Length};width={cw};height={ch};preserveAspectRatio=0;doNotMoveCursor=1:");
                    t.Put(Encoding.ASCII.GetBytes(Convert.ToBase64String(png)));
                    t.Put("\a");
                    before = png.Length * 4 / 3;
                    break;
                }
            case Gfx.Sixel:
                {
                    var s = Sixel.Encode(f, c * K, r * M, K, M, cw, ch, caps.CellW, caps.CellH);
                    t.Put(s);
                    before = s.Length;
                    break;
                }
        }
        BytesSent += before;
    }

    // ---------------------------------------------------------------- half blocks

    private bool PaintBlocks(Frame f)
    {
        var cells = new int[Cols * Rows * 2];
        var half = M / 2;
        for (var r = 0; r < Rows; r++)
            for (var c = 0; c < Cols; c++)
            {
                cells[(r * Cols + c) * 2] = Average(f, c * K, r * M, K, half);
                cells[(r * Cols + c) * 2 + 1] = Average(f, c * K, r * M + half, K, M - half);
            }
        var prev = _full ? null : _cells;
        var sb = new StringBuilder();
        int lastFg = -1, lastBg = -1, cursorAt = -1;
        for (var i = 0; i < Cols * Rows; i++)
        {
            int top = cells[i * 2], bot = cells[i * 2 + 1];
            if (prev is not null && prev[i * 2] == top && prev[i * 2 + 1] == bot) continue;
            if (cursorAt != i) sb.Append($"\e[{i / Cols + 1};{i % Cols + 1}H");
            if (top != lastFg) { sb.Append(Sgr(top, true)); lastFg = top; }
            if (bot != lastBg) { sb.Append(Sgr(bot, false)); lastBg = bot; }
            sb.Append('▀');
            cursorAt = i + 1;
            if ((i + 1) % Cols == 0) cursorAt = -1;
        }
        _cells = cells;
        _full = false;
        if (sb.Length == 0) return false;
        sb.Append("\e[0m");
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        t.Put(bytes);
        BytesSent += bytes.Length;
        return true;
    }

    /// <summary>Mean colour of a block as 0xRRGGBB (a grid of up to 4×4 samples is plenty for one half cell).</summary>
    private static int Average(Frame f, int x0, int y0, int w, int h)
    {
        if (h <= 0) h = 1;
        int sx = Math.Max(1, w / 4), sy = Math.Max(1, h / 4);
        long r = 0, g = 0, b = 0, n = 0;
        for (var y = y0; y < y0 + h && y < f.H; y += sy)
            for (var x = x0; x < x0 + w && x < f.W; x += sx)
            {
                var o = y * f.Stride + x * 4;
                b += f.Px[o]; g += f.Px[o + 1]; r += f.Px[o + 2]; n++;
            }
        if (n == 0) return 0;
        return (int)((r / n) << 16 | (g / n) << 8 | (b / n));
    }

    private string Sgr(int rgb, bool fg)
    {
        int r = rgb >> 16 & 255, g = rgb >> 8 & 255, b = rgb & 255;
        if (caps.TrueColor) return $"\e[{(fg ? 38 : 48)};2;{r};{g};{b}m";
        return $"\e[{(fg ? 38 : 48)};5;{To256(r, g, b)}m";
    }

    private static readonly int[] Levels = [0, 95, 135, 175, 215, 255];

    internal static int To256(int r, int g, int b)
    {
        static int Near(int v) { var best = 0; for (var i = 1; i < 6; i++) if (Math.Abs(Levels[i] - v) < Math.Abs(Levels[best] - v)) best = i; return best; }
        int ri = Near(r), gi = Near(g), bi = Near(b);
        var cube = 16 + 36 * ri + 6 * gi + bi;
        var dc = Sq(Levels[ri] - r) + Sq(Levels[gi] - g) + Sq(Levels[bi] - b);
        var grey = Math.Clamp((int)Math.Round(((r + g + b) / 3.0 - 8) / 10), 0, 23);
        var gv = 8 + grey * 10;
        var dg = Sq(gv - r) + Sq(gv - g) + Sq(gv - b);
        return dg < dc ? 232 + grey : cube;
        static int Sq(int v) => v * v;
    }

    // ---------------------------------------------------------------- real text over blocks (terminals without pictures)

    private string?[]? _tCh;     // null = half block, "" = right half of a wide character, else the character
    private int[]? _tFg, _tBg;
    private byte[]? _tAt;
    private int[] _modeBuf = new int[256];

    /// <summary>Where the terminal cursor belongs (the caret of the focused field), 0-based, after <see cref="PaintText"/>.</summary>
    internal (int Row, int Col)? Caret { get; private set; }

    /// <summary>Text mode: the page's text as real characters (their colour, bold/italic/underline, on the colour that is
    /// really behind them), everything else — pictures, boxes, backgrounds — as half blocks. Only changed cells are sent.</summary>
    internal bool PaintText(Frame f, TextLayer? layer)
    {
        if (f.W != W || f.H != H) return false;
        var n = Cols * Rows;
        var ch = new string?[n];
        var fg = new int[n];
        var bg = new int[n];
        var at = new byte[n];
        var half = M / 2;
        for (var r = 0; r < Rows; r++)
            for (var c = 0; c < Cols; c++)
            {
                var i = r * Cols + c;
                fg[i] = Average(f, c * K, r * M, K, half);
                bg[i] = Average(f, c * K, r * M + half, K, M - half);
            }
        Caret = null;
        if (layer is not null)
        {
            var modeOf = new int[n];
            Array.Fill(modeOf, -1);
            // 1 = under a toolbar popup (hides page text), 2 = under a popup window (hides page and toolbar text)
            var covered = new byte[n];
            void Cover(List<(int C0, int R0, int C1, int R1)> boxes, byte level)
            {
                foreach (var (c0, r0, c1, r1) in boxes)
                    for (var rr = Math.Max(0, r0); rr < Math.Min(Rows, r1); rr++)
                        for (var cc = Math.Max(0, c0); cc < Math.Min(Cols, c1); cc++) covered[rr * Cols + cc] = Math.Max(covered[rr * Cols + cc], level);
            }
            Cover(layer.Covers, 1);
            Cover(layer.Popups, 2);
            // Where each run goes. Page text on the character grid sits exactly where the page put it (a cell of slack for
            // rounding). Text in a proportional font (the toolbar; components the grid does not reach) has more letters than
            // its pixel width has cells: the runs of one clipping box (the address field, one tab) are laid out one after
            // another — keeping a space where the page had a gap — up to that box's right edge.
            var place = new (int Start, int End)[layer.Runs.Count];
            var order = Enumerable.Range(0, layer.Runs.Count)
                .OrderBy(i => layer.Runs[i].Page ? 0 : 1).ThenBy(i => layer.Runs[i].Row).ThenBy(i => layer.Runs[i].Group).ThenBy(i => layer.Runs[i].Col);
            int pRow = int.MinValue, pGroup = int.MinValue, pEnd = -1, pOrigEnd = -1; var pPage = false;
            foreach (var i in order)
            {
                var run = layer.Runs[i];
                var flow = !run.Field && !(run.Page && layer.Grid) && run.Group >= 0;
                var start = run.Col;
                if (flow && run.Row == pRow && run.Group == pGroup && run.Page == pPage && pEnd >= 0)
                {
                    var sep = run.Col > pOrigEnd ? 1 : 0;
                    if (start < pEnd + sep) start = pEnd + sep;
                }
                var limit = run.Field || (run.Page && layer.Grid) || run.Group < 0
                    ? run.Col + Math.Max(1, run.Max) + (run.Field ? 0 : 1)
                    : Math.Max(run.Col + Math.Max(1, run.Max), run.GroupEnd);
                var end = Math.Min(Cols, Math.Min(limit, start + Reader.Width(run.Text)));
                place[i] = (start, end);
                if (flow) { pRow = run.Row; pGroup = run.Group; pPage = run.Page; pEnd = end; pOrigEnd = run.Col + Math.Max(1, run.Max); }
            }
            for (var ri = 0; ri < layer.Runs.Count; ri++)
            {
                var run = layer.Runs[ri];
                if (run.Row < 0 || run.Row >= Rows) continue;
                if (run.Col >= 0 && run.Col < Cols && covered[run.Row * Cols + run.Col] > run.Level) continue;
                var end = place[ri].End;
                var c = place[ri].Start;
                // letters rarely sit exactly on cell borders, so the cells just before and after a run hold slivers of
                // its glyphs: those are background, not picture
                foreach (var pad in new[] { c - 1, end })
                {
                    if (pad < 0 || pad >= Cols) continue;
                    var pi = run.Row * Cols + pad;
                    if (ch[pi] is not null) continue;
                    if (modeOf[pi] < 0) modeOf[pi] = Mode(f, pad * K, run.Row * M, K, M);
                    ch[pi] = " "; fg[pi] = modeOf[pi]; bg[pi] = modeOf[pi]; at[pi] = 0;
                }
                foreach (var rune in run.Text.EnumerateRunes())
                {
                    var w = RuneWidth(rune);
                    if (w == 0) continue;
                    if (c + w > end) break;
                    if (c < 0) { c += w; continue; }
                    var i = run.Row * Cols + c;
                    if (modeOf[i] < 0) modeOf[i] = Mode(f, c * K, run.Row * M, K, M);
                    var back = modeOf[i];
                    var fore = Readable(run.Fg, back, (run.Flags & 8) != 0);
                    ch[i] = rune.Value < 32 ? " " : rune.ToString();
                    fg[i] = fore; bg[i] = back; at[i] = (byte)run.Flags;
                    if (w == 2 && c + 1 < Cols) { ch[i + 1] = ""; fg[i + 1] = fore; bg[i + 1] = back; at[i + 1] = at[i]; }
                    c += w;
                }
            }
            if (layer.Caret is { } k && k.Row >= 0 && k.Row < Rows && k.Col >= 0 && k.Col < Cols) Caret = k;
        }

        var prevCh = _full ? null : _tCh;
        var sb = new StringBuilder();
        var cursorAt = -1;
        string? sgr = null;
        for (var i = 0; i < n; i++)
        {
            if (prevCh is not null && prevCh[i] == ch[i] && _tFg![i] == fg[i] && _tBg![i] == bg[i] && _tAt![i] == at[i]) continue;
            if (ch[i] == "") continue; // drawn with the wide character before it
            if (cursorAt != i) sb.Append($"\e[{i / Cols + 1};{i % Cols + 1}H");
            string want;
            if (ch[i] is null) want = "\e[0;" + Col(fg[i], true) + ";" + Col(bg[i], false) + "m";
            else
            {
                var a = at[i];
                want = "\e[0" + ((a & 1) != 0 ? ";1" : "") + ((a & 2) != 0 ? ";3" : "") + ((a & 4) != 0 ? ";4" : "") +
                       ";" + Col(fg[i], true) + ";" + Col(bg[i], false) + "m";
            }
            if (want != sgr) { sb.Append(want); sgr = want; }
            sb.Append(ch[i] ?? "▀");
            var wide = ch[i] is { Length: > 0 } s && i + 1 < n && ch[i + 1] == "" ? 2 : 1;
            cursorAt = (i + wide) % Cols == 0 ? -1 : i + wide;
        }
        _tCh = ch; _tFg = fg; _tBg = bg; _tAt = at;
        _full = false;
        if (sb.Length == 0) return false;
        sb.Append("\e[0m");
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        t.Put(bytes);
        BytesSent += bytes.Length;
        return true;
    }

    private string Col(int rgb, bool fg)
    {
        int r = rgb >> 16 & 255, g = rgb >> 8 & 255, b = rgb & 255;
        return caps.TrueColor ? $"{(fg ? 38 : 48)};2;{r};{g};{b}" : $"{(fg ? 38 : 48)};5;{To256(r, g, b)}";
    }

    /// <summary>The colour a cell really has behind its text: the most common colour of its pixels (the glyph's own pixels
    /// are the minority), averaged within that colour's bucket.</summary>
    private int Mode(Frame f, int x0, int y0, int w, int h)
    {
        var count = 0;
        if (_modeBuf.Length < w * h) _modeBuf = new int[w * h];
        for (var y = y0; y < y0 + h && y < f.H; y++)
            for (var x = x0; x < x0 + w && x < f.W; x++)
            {
                var o = y * f.Stride + x * 4;
                _modeBuf[count++] = (f.Px[o + 2] >> 4) << 8 | (f.Px[o + 1] >> 4) << 4 | (f.Px[o] >> 4);
            }
        if (count == 0) return 0;
        Array.Sort(_modeBuf, 0, count);
        int best = _modeBuf[0], bestN = 0, run = 0;
        for (var i = 0; i < count; i++)
        {
            run = i > 0 && _modeBuf[i] == _modeBuf[i - 1] ? run + 1 : 1;
            if (run > bestN) { bestN = run; best = _modeBuf[i]; }
        }
        long sr = 0, sg = 0, sbl = 0, sn = 0;
        for (var y = y0; y < y0 + h && y < f.H; y++)
            for (var x = x0; x < x0 + w && x < f.W; x++)
            {
                var o = y * f.Stride + x * 4;
                if (((f.Px[o + 2] >> 4) << 8 | (f.Px[o + 1] >> 4) << 4 | (f.Px[o] >> 4)) != best) continue;
                sbl += f.Px[o]; sg += f.Px[o + 1]; sr += f.Px[o + 2]; sn++;
            }
        return sn == 0 ? 0 : (int)((sr / sn) << 16 | (sg / sn) << 8 | (sbl / sn));
    }

    /// <summary>The text colour, unless it would vanish on that background (white text on a light cell after the colour
    /// went through the terminal's palette): then black or white, whichever reads.</summary>
    private static int Readable(int fg, int bg, bool dim)
    {
        static double L(int c) => 0.299 * (c >> 16 & 255) + 0.587 * (c >> 8 & 255) + 0.114 * (c & 255);
        var lb = L(bg);
        if (Math.Abs(L(fg) - lb) >= (dim ? 35 : 60)) return fg;
        return lb > 140 ? (dim ? 0x707070 : 0x101010) : (dim ? 0x909090 : 0xf0f0f0);
    }

    /// <summary>Terminal cells a character takes: 2 for East Asian wide and emoji, 0 for combining marks and format characters.</summary>
    internal static int RuneWidth(System.Text.Rune r)
    {
        var v = r.Value;
        var cat = System.Text.Rune.GetUnicodeCategory(r);
        if (cat is System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.EnclosingMark
            or System.Globalization.UnicodeCategory.Format) return 0;
        return v is >= 0x1100 and <= 0x115F or >= 0x2E80 and <= 0x303E or >= 0x3041 and <= 0xA4CF or >= 0xAC00 and <= 0xD7A3
            or >= 0xF900 and <= 0xFAFF or >= 0xFE30 and <= 0xFE4F or >= 0xFF00 and <= 0xFF60 or >= 0xFFE0 and <= 0xFFE6
            or >= 0x1F300 and <= 0x1F64F or >= 0x1F900 and <= 0x1F9FF or >= 0x1FA70 and <= 0x1FAFF or >= 0x20000 and <= 0x3FFFD
            ? 2 : 1;
    }

    // ---------------------------------------------------------------- status bar

    /// <summary>The last row: reverse video, <paramref name="left"/> and <paramref name="right"/> fitted to the width.</summary>
    internal void Status(string left, string right)
    {
        var w = Cols;
        if (left.Length + right.Length + 1 > w) right = "";
        if (left.Length > w) left = left[..w];
        var line = left + new string(' ', Math.Max(0, w - left.Length - right.Length)) + right;
        t.Put($"\e[{Rows + 1};1H\e[0m\e[7m{line}\e[0m");
    }
}
