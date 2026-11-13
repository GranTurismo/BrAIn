namespace BrAIn.Cli.View;

/// <summary><c>brain shot</c> in a terminal: the screenshot drawn right there, as wide as the terminal, with whatever picture
/// protocol it has (half blocks as the last resort).</summary>
internal static class Inline
{
    internal static bool Show(byte[] png)
    {
        if (OperatingSystem.IsWindows() || !Posix.IsTty(0) || !Posix.IsTty(1)) return false;
        if (!Posix.TermSize(1, out var ws)) return false;
        var t = new Term();
        if (!t.EnterRaw()) return false;
        TermCaps caps;
        try { caps = t.Probe([]); }
        finally { t.LeaveRaw(); }
        if (!Png.TryDecode(png, out var f)) return false;
        var cols = Math.Max(10, ws.Cols - 1);
        var aspect = caps.CellW > 0 && caps.CellH > 0 ? (double)caps.CellH / caps.CellW : 2.0;
        var rows = Math.Max(1, (int)Math.Round(f.H / (double)f.W * cols / aspect));
        switch (caps.Gfx)
        {
            case Gfx.Kitty:
                {
                    var b64 = Convert.ToBase64String(png);
                    for (var i = 0; i < b64.Length; i += 4096)
                    {
                        var more = i + 4096 < b64.Length ? 1 : 0;
                        t.Put(i == 0 ? $"\e_Ga=T,f=100,q=2,c={cols},r={rows},m={more};" : $"\e_Gm={more};");
                        t.Put(b64.AsSpan(i, Math.Min(4096, b64.Length - i)).ToString());
                        t.Put("\e\\");
                    }
                    t.Put("\r\n");
                    break;
                }
            case Gfx.Iterm:
                t.Put($"\e]1337;File=inline=1;size={png.Length};width={cols};height={rows};preserveAspectRatio=0:{Convert.ToBase64String(png)}\a\r\n");
                break;
            default:
                {
                    // resample into a frame of whole cells, then draw it like the viewer does
                    var k = Math.Max(1, f.W / cols);
                    var m = Math.Max(1, (int)Math.Round(k * aspect));
                    rows = Math.Max(1, f.H / m);
                    var g = new Frame(cols * k, rows * m);
                    for (var y = 0; y < g.H; y++)
                        for (var x = 0; x < g.W; x++)
                        {
                            var sx = Math.Min(f.W - 1, (int)((long)x * f.W / g.W));
                            var sy = Math.Min(f.H - 1, (int)((long)y * f.H / g.H));
                            Buffer.BlockCopy(f.Px, sy * f.Stride + sx * 4, g.Px, y * g.Stride + x * 4, 4);
                        }
                    if (caps.Gfx == Gfx.Sixel)
                        t.Put(Sixel.Encode(g, 0, 0, k, m, cols, rows, caps.CellW, caps.CellH) + "\r\n");
                    else
                    {
                        var sb = new System.Text.StringBuilder();
                        var half = m / 2;
                        for (var r = 0; r < rows; r++)
                        {
                            for (var c = 0; c < cols; c++)
                            {
                                var top = Avg(g, c * k, r * m, k, half);
                                var bot = Avg(g, c * k, r * m + half, k, m - half);
                                sb.Append(caps.TrueColor
                                    ? $"\e[38;2;{top >> 16 & 255};{top >> 8 & 255};{top & 255};48;2;{bot >> 16 & 255};{bot >> 8 & 255};{bot & 255}m▀"
                                    : $"\e[38;5;{Painter.To256(top >> 16 & 255, top >> 8 & 255, top & 255)};48;5;{Painter.To256(bot >> 16 & 255, bot >> 8 & 255, bot & 255)}m▀");
                            }
                            sb.Append("\e[0m\r\n");
                        }
                        t.Put(sb.ToString());
                    }
                    break;
                }
        }
        t.Flush();
        return true;
    }

    private static int Avg(Frame f, int x0, int y0, int w, int h)
    {
        h = Math.Max(1, h);
        long r = 0, g = 0, b = 0, n = 0;
        for (var y = y0; y < y0 + h && y < f.H; y++)
            for (var x = x0; x < x0 + w && x < f.W; x++)
            {
                var o = y * f.Stride + x * 4;
                b += f.Px[o]; g += f.Px[o + 1]; r += f.Px[o + 2]; n++;
            }
        return n == 0 ? 0 : (int)((r / n) << 16 | (g / n) << 8 | (b / n));
    }
}
