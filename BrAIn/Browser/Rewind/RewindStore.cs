using System.IO.Compression;
using System.Text;

namespace BrAIn.Browser.Rewind;

/// <summary>
/// In-memory history of gzip-compressed page snapshots, per tab, for Rewind.
/// Memory only — nothing here is ever written to disk. Bounded three ways so a
/// forgotten toggle can't balloon: a global byte budget, a maximum age, and a
/// per-tab count; the oldest snapshots go first.
/// </summary>
internal sealed class RewindStore
{
    internal sealed class Snap
    {
        public long Id;
        public long T;                 // capture time, unix ms
        public string Url = "";
        public string Title = "";
        public byte[] Html = [];       // gzip(document HTML)
        public byte[] Text = [];       // gzip(visible text) — search + MCP reads
        public int Sx, Sy, Vw, Vh;     // scroll + viewport at capture
        public uint Hash;              // content hash of the HTML (page-computed)
        public string? Tag;            // e.g. "agent · browser_click"
        public int Bytes => Html.Length + Text.Length;
    }

    private readonly object _lock = new();
    private readonly Dictionary<Guid, List<Snap>> _tabs = new();
    private long _bytes, _nextId;

    public long MaxBytes { get; set; } = 128L << 20;
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromMinutes(30);
    public int MaxPerTab { get; set; } = 400;
    /// <summary>One snapshot (compressed) larger than this is refused outright.</summary>
    public int MaxSnapBytes { get; set; } = 3 << 20;
    /// <summary>Cap on any decompressed payload we read host-side (search/MCP).</summary>
    public const int MaxInflate = 8 << 20;

    public long Bytes { get { lock (_lock) return _bytes; } }
    public int Count { get { lock (_lock) return _tabs.Values.Sum(l => l.Count); } }

    /// <returns>The stored snapshot, or null when it was a duplicate / refused.</returns>
    public Snap? Add(Guid tab, string url, string title, byte[] html, byte[] text,
        int sx, int sy, int vw, int vh, uint hash, string? tag, long? now = null)
    {
        if (html.Length + text.Length > MaxSnapBytes) return null;
        var t = now ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (_lock)
        {
            if (!_tabs.TryGetValue(tab, out var list)) _tabs[tab] = list = new();
            if (list.Count > 0)
            {
                var last = list[^1];
                // Same page state: keep one copy, but let an agent tag stick to it.
                if (last.Hash == hash && last.Url == url)
                {
                    if (tag is not null && last.Tag is null) last.Tag = tag;
                    return null;
                }
            }
            var s = new Snap
            {
                Id = ++_nextId, T = t, Url = url, Title = title, Html = html, Text = text,
                Sx = sx, Sy = sy, Vw = vw, Vh = vh, Hash = hash, Tag = tag,
            };
            list.Add(s);
            _bytes += s.Bytes;
            Evict(t);
            return s;
        }
    }

    private void Evict(long nowMs)
    {
        var cutoff = nowMs - (long)MaxAge.TotalMilliseconds;
        foreach (var l in _tabs.Values)
        {
            var n = 0;
            while (n < l.Count && l[n].T < cutoff) _bytes -= l[n++].Bytes;
            if (n > 0) l.RemoveRange(0, n);
            while (l.Count > MaxPerTab) { _bytes -= l[0].Bytes; l.RemoveAt(0); }
        }
        // Over budget: drop the globally oldest snapshot until it fits.
        while (_bytes > MaxBytes)
        {
            List<Snap>? oldest = null;
            foreach (var l in _tabs.Values)
                if (l.Count > 0 && (oldest is null || l[0].T < oldest[0].T)) oldest = l;
            if (oldest is null) break;
            _bytes -= oldest[0].Bytes;
            oldest.RemoveAt(0);
        }
        foreach (var k in _tabs.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList())
            _tabs.Remove(k);
    }

    /// <summary>Re-apply limits after a setting change (shrinking frees memory now).</summary>
    public void Trim()
    {
        lock (_lock) Evict(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public List<Snap> List(Guid tab)
    {
        lock (_lock) return _tabs.TryGetValue(tab, out var l) ? [.. l] : [];
    }

    public Snap? Get(Guid tab, long id)
    {
        lock (_lock) return _tabs.TryGetValue(tab, out var l) ? l.FirstOrDefault(s => s.Id == id) : null;
    }

    public (int Count, long Bytes) TabStats(Guid tab)
    {
        lock (_lock)
            return _tabs.TryGetValue(tab, out var l) ? (l.Count, l.Sum(s => (long)s.Bytes)) : (0, 0);
    }

    public void RemoveTab(Guid tab)
    {
        lock (_lock)
        {
            if (_tabs.Remove(tab, out var l)) _bytes -= l.Sum(s => (long)s.Bytes);
        }
    }

    public void Clear()
    {
        lock (_lock) { _tabs.Clear(); _bytes = 0; }
    }

    /// <summary>Forget everything recorded from one site (the user excluded it).</summary>
    public void RemoveHost(string host)
    {
        lock (_lock)
        {
            foreach (var l in _tabs.Values)
                for (var i = l.Count - 1; i >= 0; i--)
                    if (HostOf(l[i].Url) is { } h && HostMatches(h, host)) { _bytes -= l[i].Bytes; l.RemoveAt(i); }
            foreach (var k in _tabs.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList())
                _tabs.Remove(k);
        }
    }

    /// <summary>Newest-first scan of a tab's visible text for <paramref name="query"/>.</summary>
    public List<(Snap Snap, string Snippet)> Search(Guid tab, string query, int max = 20)
    {
        var res = new List<(Snap, string)>();
        if (string.IsNullOrWhiteSpace(query)) return res;
        var list = List(tab);
        for (var i = list.Count - 1; i >= 0 && res.Count < max; i--)
        {
            var s = list[i];
            string text;
            try { text = Gunzip(s.Text); } catch { continue; }
            var at = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;
            var from = Math.Max(0, at - 50);
            var len = Math.Min(text.Length - from, query.Length + 110);
            res.Add((s, (from > 0 ? "…" : "") + text.Substring(from, len).Replace('\n', ' ').Trim() + (from + len < text.Length ? "…" : "")));
        }
        return res;
    }

    public static bool HostMatches(string host, string rule) =>
        host.Equals(rule, StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith("." + rule, StringComparison.OrdinalIgnoreCase);

    public static string? HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? u.Host : null;

    // ---- compression ----

    public static byte[] Gzip(string s)
    {
        using var ms = new MemoryStream();
        using (var z = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            z.Write(Encoding.UTF8.GetBytes(s));
        return ms.ToArray();
    }

    /// <summary>Inflate with a hard output cap — payloads come from page JS, so a
    /// gzip bomb must not be able to exhaust host memory.</summary>
    public static string Gunzip(byte[] data, int max = MaxInflate)
    {
        using var z = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var o = new MemoryStream();
        var buf = new byte[16 * 1024];
        int n;
        while ((n = z.Read(buf, 0, buf.Length)) > 0)
        {
            if (o.Length + n > max) throw new InvalidDataException("payload too large");
            o.Write(buf, 0, n);
        }
        return Encoding.UTF8.GetString(o.GetBuffer(), 0, (int)o.Length);
    }

    /// <summary>True when <paramref name="data"/> starts with the gzip magic bytes.</summary>
    public static bool LooksGzip(byte[] data) => data.Length > 2 && data[0] == 0x1f && data[1] == 0x8b;
}
