using System.Collections.Concurrent;
using System.Text.Json;

namespace BrAIn.Browser;

/// <summary>
/// Site icons as <c>data:</c> URIs, kept on disk. The start page's "Recent" tiles used to point
/// at the sites' own favicon URLs: WebKit counts those fetches as the page still loading, so every
/// launch showed a loading bar for ~a second and the page needed the network just to open.
/// <see cref="Get"/> never blocks and never touches the network on the caller's thread — a miss
/// returns null (the tile shows its letter) and queues a background fetch that raises
/// <see cref="Changed"/> when the icon arrives.
/// </summary>
internal sealed class FaviconCache
{
    private const int MaxEntries = 200;
    private const int MaxBytes = 32 * 1024;

    private readonly string _path;
    private readonly object _lock = new();
    private readonly Dictionary<string, string> _icons = new();        // icon URL -> data URI (insertion order = age)
    private readonly Queue<string> _order = new();
    private readonly ConcurrentDictionary<string, byte> _pending = new();
    private readonly HashSet<string> _failed = new();                   // don't hammer a dead icon this session
    // Built on first use: standing up HttpClient costs startup time and most launches never need it.
    private readonly Lazy<HttpClient> _httpLazy = new(() =>
    {
        var c = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 3 }) { Timeout = TimeSpan.FromSeconds(5) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Macintosh) BrAIn");
        return c;
    });
    private HttpClient _http => _httpLazy.Value;
    private int _saveQueued;

    /// <summary>Set false (Ghost mode) to stop fetching new icons; cached ones are still served.</summary>
    public volatile bool FetchEnabled = true;

    /// <summary>An icon was added (any thread).</summary>
    public event Action? Changed;

    public FaviconCache(string storeDir)
    {
        _path = Path.Combine(storeDir, "favicons.json");
        try
        {
            if (!File.Exists(_path)) return;
            var rows = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path));
            if (rows is null) return;
            foreach (var kv in rows)
                if (kv.Value.StartsWith("data:image/", StringComparison.Ordinal)) { _icons[kv.Key] = kv.Value; _order.Enqueue(kv.Key); }
        }
        catch { /* unreadable cache = start empty */ }
    }

    /// <summary>The cached data URI for an icon URL, or null (and a background fetch is queued).</summary>
    public string? Get(string? iconUrl)
    {
        if (string.IsNullOrEmpty(iconUrl)) return null;
        if (iconUrl.StartsWith("data:", StringComparison.Ordinal)) return iconUrl;
        lock (_lock)
        {
            if (_icons.TryGetValue(iconUrl, out var hit)) return hit;
            if (_failed.Contains(iconUrl)) return null;
        }
        if (FetchEnabled && iconUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) && _pending.TryAdd(iconUrl, 0))
            _ = Task.Run(() => FetchAsync(iconUrl));
        return null;
    }

    private async Task FetchAsync(string url)
    {
        string? dataUri = null;
        try
        {
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            var type = resp.Content.Headers.ContentType?.MediaType;
            if (resp.IsSuccessStatusCode && type is not null && type.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                && (resp.Content.Headers.ContentLength ?? 0) <= MaxBytes)
            {
                var bytes = await resp.Content.ReadAsByteArrayAsync();
                if (bytes.Length is > 0 and <= MaxBytes) dataUri = $"data:{type};base64,{Convert.ToBase64String(bytes)}";
            }
        }
        catch { /* offline / blocked / timed out — try again next launch */ }
        finally { _pending.TryRemove(url, out _); }

        lock (_lock)
        {
            if (dataUri is null) { _failed.Add(url); return; }
            _icons[url] = dataUri;
            _order.Enqueue(url);
            while (_order.Count > MaxEntries) _icons.Remove(_order.Dequeue());
        }
        QueueSave();
        try { Changed?.Invoke(); } catch { }
    }

    private void QueueSave()
    {
        if (Interlocked.Exchange(ref _saveQueued, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500); // batch the burst of icons a fresh profile fetches
            Interlocked.Exchange(ref _saveQueued, 0);
            try
            {
                Dictionary<string, string> snap;
                lock (_lock) snap = new Dictionary<string, string>(_icons);
                File.WriteAllText(_path, JsonSerializer.Serialize(snap));
            }
            catch { /* non-fatal */ }
        });
    }
}
