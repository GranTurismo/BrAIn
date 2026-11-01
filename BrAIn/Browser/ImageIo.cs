using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace BrAIn.Browser;

/// <summary>
/// Image bytes arriving from a web page ("Copy image" / "Save image" in the right-click menu) are
/// untrusted: a page can send these messages without any click. So the content is identified from its
/// first bytes (never from a name or type the page claims), only a few raster formats are accepted,
/// and the size and pixel count are capped. SVG and anything else that can carry script is refused.
/// </summary>
internal static class ImageIo
{
    public const int MaxBytes = 40 * 1024 * 1024;
    public const long MaxPixels = 120_000_000;

    /// <param name="Ext">The file extension for this content, without the dot.</param>
    /// <param name="Width">Pixels, or 0 when the header does not say (AVIF).</param>
    public sealed record Kind(string Mime, string Ext, int Width, int Height);

    /// <summary>What the bytes are, or null. A picture whose header gives no size (a corrupt or invented one) is not one.</summary>
    public static Kind? Sniff(byte[] d)
    {
        var k = SniffHeader(d);
        return k is not null && (k.Ext == "avif" || (k.Width > 0 && k.Height > 0)) ? k : null;
    }

    private static Kind? SniffHeader(byte[] d)
    {
        if (d.Length < 16) return null;
        // PNG
        if (d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47 && d[4] == 0x0D && d[5] == 0x0A && d[6] == 0x1A && d[7] == 0x0A)
            return d.Length >= 24 ? new("image/png", "png", BE32(d, 16), BE32(d, 20)) : null;
        // JPEG: walk the segments up to the frame header that holds the size
        if (d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF)
        {
            var i = 2;
            while (i + 9 < d.Length)
            {
                if (d[i] != 0xFF) { i++; continue; }
                var m = d[i + 1];
                if (m == 0xFF) { i++; continue; }
                if (m is 0xD8 or 0x01 || m is >= 0xD0 and <= 0xD7) { i += 2; continue; }
                var len = (d[i + 2] << 8) | d[i + 3];
                if (m is >= 0xC0 and <= 0xCF && m is not (0xC4 or 0xC8 or 0xCC))
                    return new("image/jpeg", "jpg", (d[i + 7] << 8) | d[i + 8], (d[i + 5] << 8) | d[i + 6]);
                if (len < 2) return null;
                i += 2 + len;
            }
            return new("image/jpeg", "jpg", 0, 0);
        }
        // GIF
        if (d[0] == 'G' && d[1] == 'I' && d[2] == 'F' && d[3] == '8' && (d[4] == '7' || d[4] == '9') && d[5] == 'a')
            return new("image/gif", "gif", d[6] | (d[7] << 8), d[8] | (d[9] << 8));
        // WebP
        if (d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' && d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P' && d.Length >= 30)
        {
            if (d[12] == 'V' && d[13] == 'P' && d[14] == '8' && d[15] == ' ')
                return new("image/webp", "webp", (d[26] | (d[27] << 8)) & 0x3FFF, (d[28] | (d[29] << 8)) & 0x3FFF);
            if (d[12] == 'V' && d[13] == 'P' && d[14] == '8' && d[15] == 'L')
            {
                var bits = (uint)(d[21] | (d[22] << 8) | (d[23] << 16) | (d[24] << 24));
                return new("image/webp", "webp", (int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
            }
            if (d[12] == 'V' && d[13] == 'P' && d[14] == '8' && d[15] == 'X')
                return new("image/webp", "webp", 1 + (d[24] | (d[25] << 8) | (d[26] << 16)), 1 + (d[27] | (d[28] << 8) | (d[29] << 16)));
            return null;
        }
        // BMP
        if (d[0] == 'B' && d[1] == 'M' && d.Length >= 26)
            return new("image/bmp", "bmp", LE32(d, 18), Math.Abs(LE32(d, 22)));
        // AVIF (ISO base media file with an avif brand)
        if (d[4] == 'f' && d[5] == 't' && d[6] == 'y' && d[7] == 'p' && d[8] == 'a' && d[9] == 'v' && d[10] == 'i' && (d[11] == 'f' || d[11] == 's'))
            return new("image/avif", "avif", 0, 0);
        return null;
    }

    private static int BE32(byte[] d, int o) => (d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3];
    private static int LE32(byte[] d, int o) => d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24);

    // ---------------------------------------------------------------- downloading on the page's behalf

    /// <summary>Is this a machine on the user's own network (or the user's own machine)?</summary>
    internal static bool IsPrivate(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (IPAddress.IsLoopback(a) || a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any) || a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6Multicast) return true;
        var b = a.GetAddressBytes();
        if (b.Length == 16) return (b[0] & 0xFE) == 0xFC;                 // fc00::/7 unique local
        return b[0] == 0 || b[0] == 10 || b[0] >= 224 ||
               (b[0] == 100 && b[1] is >= 64 and <= 127) ||                // carrier-grade NAT
               (b[0] == 169 && b[1] == 254) ||
               (b[0] == 172 && b[1] is >= 16 and <= 31) ||
               (b[0] == 192 && b[1] == 168) ||
               (b[0] == 198 && b[1] is 18 or 19);
    }

    /// <summary>Is the page itself on the user's own network? Then its images may be too.</summary>
    internal static bool HostIsLocal(string? pageUrl)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) return false;
        var h = u.Host;
        if (h == "localhost" || h.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) || h.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(h.Trim('[', ']'), out var ip) && IsPrivate(ip);
    }

    /// <summary>Downloads an image the page could not read itself (another site's picture without CORS).
    /// Only http(s); the connection is made by this code, which refuses addresses on the user's own network
    /// — also after a redirect or a DNS answer that changes — unless the page is itself local.</summary>
    public static async Task<(byte[]? Data, string? Error)> FetchAsync(string url, string? referer, bool allowLocal, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) return (null, "That image has no address BrAIn can download.");
        try
        {
            using var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 5,
                ConnectTimeout = TimeSpan.FromSeconds(10),
                UseCookies = false,
                ConnectCallback = async (ctx, token) =>
                {
                    var addrs = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, token);
                    Exception? last = null;
                    foreach (var a in addrs)
                    {
                        if (!allowLocal && IsPrivate(a)) { last = new HttpRequestException("blocked: that address is on your own network"); continue; }
                        var s = new Socket(a.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                        try { await s.ConnectAsync(new IPEndPoint(a, ctx.DnsEndPoint.Port), token); return new NetworkStream(s, ownsSocket: true); }
                        catch (Exception ex) { s.Dispose(); last = ex; }
                    }
                    throw last ?? new HttpRequestException("no address for " + ctx.DnsEndPoint.Host);
                },
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
            using var req = new HttpRequestMessage(HttpMethod.Get, u);
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) BrAIn");
            req.Headers.Accept.ParseAdd("image/avif,image/webp,image/png,image/jpeg,image/*;q=0.8,*/*;q=0.5");
            if (Uri.TryCreate(referer, UriKind.Absolute, out var r) && r.Scheme is "http" or "https") req.Headers.Referrer = new Uri(r.GetLeftPart(UriPartial.Query));
            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!res.IsSuccessStatusCode) return (null, $"The site refused to send that image ({(int)res.StatusCode}).");
            if (res.Content.Headers.ContentLength is > MaxBytes) return (null, "That image is too large.");
            await using var body = await res.Content.ReadAsStreamAsync(ct);
            using var ms = new MemoryStream();
            var buf = new byte[81920];
            int n;
            while ((n = await body.ReadAsync(buf, ct)) > 0)
            {
                ms.Write(buf, 0, n);
                if (ms.Length > MaxBytes) return (null, "That image is too large.");
            }
            return (ms.ToArray(), null);
        }
        catch (OperationCanceledException) { return (null, "Downloading the image took too long."); }
        catch (Exception ex) when (ex.ToString().Contains("blocked: that address", StringComparison.Ordinal)) { return (null, "BrAIn won't download images from addresses on your own network."); }
        catch (Exception) { return (null, "Couldn't download that image."); }
    }
}
