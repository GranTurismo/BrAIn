using System.Text;

namespace BrAIn.Cli.View;

/// <summary>Sixel output (xterm, foot, Windows Terminal, mlterm, …): the picture at the terminal's own pixel size, in a fixed
/// 252-colour palette (6 red × 7 green × 6 blue levels, green gets one more because the eye sees it best), run-length coded.
/// Pixels a colour does not cover are left alone (P2=1), so a partial picture paints only its own cells.</summary>
internal static class Sixel
{
    private static int Index(int r, int g, int b) => (r * 5 + 127) / 255 * 42 + (g * 6 + 127) / 255 * 6 + (b * 5 + 127) / 255;

    /// <summary>The region of <paramref name="cw"/>×<paramref name="ch"/> cells whose pixels start at (x0,y0), each cell being
    /// k×m frame pixels, drawn at cellW×cellH terminal pixels per cell.</summary>
    internal static string Encode(Frame f, int x0, int y0, int k, int m, int cw, int ch, int cellW, int cellH)
    {
        int pw = cw * cellW, ph = ch * cellH;
        var idx = new byte[pw * ph];
        var used = new bool[252];
        var xs = new int[pw];
        for (var x = 0; x < pw; x++) xs[x] = Math.Min(f.W - 1, x0 + x * k / cellW);
        for (var y = 0; y < ph; y++)
        {
            var sy = Math.Min(f.H - 1, y0 + y * m / cellH);
            var row = sy * f.Stride;
            for (var x = 0; x < pw; x++)
            {
                var o = row + xs[x] * 4;
                var i = (byte)Index(f.Px[o + 2], f.Px[o + 1], f.Px[o]);
                idx[y * pw + x] = i;
                used[i] = true;
            }
        }
        var sb = new StringBuilder(pw * ph / 3);
        sb.Append("\eP0;1;0q\"1;1;").Append(pw).Append(';').Append(ph);
        for (var i = 0; i < 252; i++)
        {
            if (!used[i]) continue;
            int r = i / 42, g = i / 6 % 7, b = i % 6;
            sb.Append('#').Append(i).Append(";2;").Append(r * 100 / 5).Append(';').Append(g * 100 / 6).Append(';').Append(b * 100 / 5);
        }
        var bits = new byte[pw];
        var inBand = new bool[252];
        for (var band = 0; band < ph; band += 6)
        {
            Array.Clear(inBand);
            var rows = Math.Min(6, ph - band);
            for (var y = 0; y < rows; y++)
                for (var x = 0; x < pw; x++) inBand[idx[(band + y) * pw + x]] = true;
            var firstColour = true;
            for (var c = 0; c < 252; c++)
            {
                if (!inBand[c]) continue;
                Array.Clear(bits);
                int lo = pw, hi = -1;
                for (var y = 0; y < rows; y++)
                {
                    var o = (band + y) * pw;
                    for (var x = 0; x < pw; x++)
                        if (idx[o + x] == c) { bits[x] |= (byte)(1 << y); if (x < lo) lo = x; if (x > hi) hi = x; }
                }
                if (hi < 0) continue;
                if (!firstColour) sb.Append('$');
                firstColour = false;
                sb.Append('#').Append(c);
                if (lo > 0) Run(sb, '?', lo);
                var x2 = lo;
                while (x2 <= hi)
                {
                    var v = bits[x2];
                    var n = 1;
                    while (x2 + n <= hi && bits[x2 + n] == v) n++;
                    Run(sb, (char)(63 + v), n);
                    x2 += n;
                }
            }
            sb.Append('-');
        }
        sb.Append("\e\\");
        return sb.ToString();
    }

    private static void Run(StringBuilder sb, char ch, int n)
    {
        if (n > 3) sb.Append('!').Append(n).Append(ch);
        else sb.Append(ch, n);
    }
}
