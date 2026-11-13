using System.Buffers.Binary;
using System.IO.Compression;

namespace BrAIn.Cli.View;

/// <summary>Pixels as X gives them: 4 bytes per pixel, B G R x, rows <see cref="Stride"/> bytes apart.</summary>
internal sealed class Frame(int w, int h)
{
    public readonly int W = w, H = h, Stride = w * 4;
    public readonly byte[] Px = new byte[w * h * 4];
}

/// <summary>A small PNG encoder (RGB, 8 bit, per-row filter choice, fast deflate) and decoder (8-bit grey/RGB/palette/alpha,
/// not interlaced — what browsers' screenshots are). Kitty and iTerm2 take PNG, and <c>brain shot</c> gets PNG from the tools.</summary>
internal static class Png
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    /// <summary>Encodes the w×h region at (x0,y0) of a BGRx frame.</summary>
    internal static byte[] Encode(Frame f, int x0, int y0, int w, int h)
    {
        var rowLen = w * 3;
        var prev = new byte[rowLen];
        var cur = new byte[rowLen];
        var cand = new byte[3][];
        for (var i = 0; i < 3; i++) cand[i] = new byte[rowLen];
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        {
            for (var y = 0; y < h; y++)
            {
                var src = (y0 + y) * f.Stride + x0 * 4;
                for (int x = 0, d = 0; x < w; x++, src += 4, d += 3)
                {
                    cur[d] = f.Px[src + 2]; cur[d + 1] = f.Px[src + 1]; cur[d + 2] = f.Px[src];
                }
                // filters 0 (none), 1 (sub), 2 (up): keep the one with the smallest sum of magnitudes (the usual heuristic)
                long s0 = 0, s1 = 0, s2 = 0;
                for (var i = 0; i < rowLen; i++)
                {
                    var c = cur[i];
                    var a = i >= 3 ? cur[i - 3] : (byte)0;
                    var v1 = (byte)(c - a);
                    var v2 = (byte)(c - prev[i]);
                    cand[1][i] = v1; cand[2][i] = v2;
                    s0 += c < 128 ? c : 256 - c;
                    s1 += v1 < 128 ? v1 : 256 - v1;
                    s2 += v2 < 128 ? v2 : 256 - v2;
                }
                if (s0 <= s1 && s0 <= s2) { z.WriteByte(0); z.Write(cur); }
                else if (s1 <= s2) { z.WriteByte(1); z.Write(cand[1]); }
                else { z.WriteByte(2); z.Write(cand[2]); }
                (prev, cur) = (cur, prev);
            }
        }
        using var o = new MemoryStream((int)raw.Length + 64);
        o.Write(Signature);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 8; ihdr[9] = 2; // 8-bit RGB
        Chunk(o, "IHDR", ihdr);
        Chunk(o, "IDAT", raw.GetBuffer().AsSpan(0, (int)raw.Length));
        Chunk(o, "IEND", []);
        return o.ToArray();
    }

    private static void Chunk(Stream o, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        o.Write(len);
        Span<byte> t = stackalloc byte[4];
        for (var i = 0; i < 4; i++) t[i] = (byte)type[i];
        o.Write(t);
        o.Write(data);
        var crc = Crc(Crc(0xFFFFFFFFu, t), data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(len, crc);
        o.Write(len);
    }

    private static readonly uint[] CrcTable = MakeCrc();

    private static uint[] MakeCrc()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    private static uint Crc(uint c, ReadOnlySpan<byte> b)
    {
        foreach (var x in b) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        return c;
    }

    /// <summary>Decodes a PNG into a BGRx frame (alpha is composited over white). False for what it does not handle.</summary>
    internal static bool TryDecode(byte[] png, out Frame frame)
    {
        frame = null!;
        try
        {
            if (png.Length < 33 || !png.AsSpan(0, 8).SequenceEqual(Signature)) return false;
            int w = 0, h = 0, depth = 0, ctype = 0, interlace = 0;
            byte[]? pal = null, trns = null;
            using var idat = new MemoryStream();
            var p = 8;
            while (p + 12 <= png.Length)
            {
                var len = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(p));
                var type = System.Text.Encoding.ASCII.GetString(png, p + 4, 4);
                var data = png.AsSpan(p + 8, len);
                switch (type)
                {
                    case "IHDR":
                        w = BinaryPrimitives.ReadInt32BigEndian(data);
                        h = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                        depth = data[8]; ctype = data[9]; interlace = data[12];
                        break;
                    case "PLTE": pal = data.ToArray(); break;
                    case "tRNS": trns = data.ToArray(); break;
                    case "IDAT": idat.Write(data); break;
                }
                if (type == "IEND") break;
                p += 12 + len;
            }
            if (w <= 0 || h <= 0 || interlace != 0 || depth != 8 || (long)w * h > 64_000_000) return false;
            var channels = ctype switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
            if (channels == 0 || (ctype == 3 && pal is null)) return false;
            var bpp = channels;
            var rowLen = w * bpp;
            var raw = new byte[(rowLen + 1) * h];
            idat.Position = 0;
            using (var z = new ZLibStream(idat, CompressionMode.Decompress))
            {
                var got = 0;
                while (got < raw.Length)
                {
                    var n = z.Read(raw, got, raw.Length - got);
                    if (n == 0) break;
                    got += n;
                }
                if (got < raw.Length) return false;
            }
            var f = new Frame(w, h);
            var prev = new byte[rowLen];
            var cur = new byte[rowLen];
            for (var y = 0; y < h; y++)
            {
                var ft = raw[y * (rowLen + 1)];
                var src = raw.AsSpan(y * (rowLen + 1) + 1, rowLen);
                for (var i = 0; i < rowLen; i++)
                {
                    int a = i >= bpp ? cur[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                    cur[i] = ft switch
                    {
                        0 => src[i],
                        1 => (byte)(src[i] + a),
                        2 => (byte)(src[i] + b),
                        3 => (byte)(src[i] + ((a + b) >> 1)),
                        4 => (byte)(src[i] + Paeth(a, b, c)),
                        _ => src[i],
                    };
                }
                var d = y * f.Stride;
                for (var x = 0; x < w; x++, d += 4)
                {
                    int r, g, bl, al = 255;
                    switch (ctype)
                    {
                        case 0: r = g = bl = cur[x]; break;
                        case 2: r = cur[x * 3]; g = cur[x * 3 + 1]; bl = cur[x * 3 + 2]; break;
                        case 3:
                            {
                                var ix = cur[x];
                                r = pal![ix * 3]; g = pal[ix * 3 + 1]; bl = pal[ix * 3 + 2];
                                if (trns is not null && ix < trns.Length) al = trns[ix];
                                break;
                            }
                        case 4: r = g = bl = cur[x * 2]; al = cur[x * 2 + 1]; break;
                        default: r = cur[x * 4]; g = cur[x * 4 + 1]; bl = cur[x * 4 + 2]; al = cur[x * 4 + 3]; break;
                    }
                    if (al < 255)
                    {
                        r = (r * al + 255 * (255 - al)) / 255; g = (g * al + 255 * (255 - al)) / 255; bl = (bl * al + 255 * (255 - al)) / 255;
                    }
                    f.Px[d] = (byte)bl; f.Px[d + 1] = (byte)g; f.Px[d + 2] = (byte)r; f.Px[d + 3] = 255;
                }
                (prev, cur) = (cur, prev);
            }
            frame = f;
            return true;
        }
        catch { return false; }
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
