using System.Diagnostics;

namespace BrAIn.Browser;

/// <summary>
/// Browser-wide download registry. Engine glue (WKDownload on macOS, CDP
/// Browser/Page download events on Windows) funnels lifecycle into here;
/// the controller pushes snapshots to the toolbar as {b:20} messages.
/// Files always land in the user's Downloads folder with a uniquified name —
/// no save dialog, like Chrome's default.
/// </summary>
public sealed class Downloads
{
    public sealed class Item
    {
        public required string Id { get; init; }
        public required string Url { get; init; }
        public string Name { get; set; } = "download";
        public string Path { get; set; } = "";
        public long Received, Total = -1; // Total -1 = size unknown
        public int State;                 // 0 running, 1 done, 2 failed, 3 canceled
        public string? Error;
        public DateTime Start = DateTime.Now;

        // Engine handles (only one is ever set)
        internal IntPtr Wk;            // WKDownload pointer, macOS
        internal string? CdpGuid;      // download guid, CDP
    }

    private readonly List<Item> _items = new();
    private readonly object _lock = new();
    private int _next;
    public readonly string Dir;

    /// <summary>Fires on any state change — controller pushes the snapshot.</summary>
    public event Action? Changed;

    /// <summary>Set by the CDP driver so user cancels can also stop the
    /// engine-side download (Browser.cancelDownload needs the browser
    /// target, which Downloads itself can't reach).</summary>
    internal Func<Item, Task>? EngineCancel;

    public Downloads(string dir)
    {
        Dir = dir;
        try { Directory.CreateDirectory(dir); } catch { }
    }

    public IReadOnlyList<Item> Items { get { lock (_lock) return _items.ToArray(); } }

    public Item Begin(string url)
    {
        var it = new Item { Id = (++_next).ToString(), Url = url ?? "" };
        lock (_lock) _items.Insert(0, it);
        Changed?.Invoke();
        return it;
    }

    /// <summary>Picks the destination path for a running item: sanitizes the
    /// suggested filename into Downloads/, uniquifying "name (n).ext" style.</summary>
    public string DestinationFor(Item it, string? suggested)
    {
        var name = Sanitize(suggested);
        if (name.Length == 0) name = NameFromUrl(it.Url);
        var path = Unique(Path.Combine(Dir, name));
        it.Name = Path.GetFileName(path);
        it.Path = path;
        return path;
    }

    public void SetTotal(Item it, long total) { if (total >= 0 && it.Total < 0) { it.Total = total; Changed?.Invoke(); } }

    public void Progress(Item it, long received, long total)
    {
        if (it.State != 0) return;
        it.Received = Math.Max(it.Received, received);
        if (total > 0) it.Total = total;
        Changed?.Invoke();
    }

    public void Finish(Item it, string? path = null)
    {
        if (it.State != 0) return;
        it.State = 1;
        if (path is { Length: > 0 }) { it.Path = path; it.Name = System.IO.Path.GetFileName(path); }
        // When the engine never told us a size (no expectedContentLength and
        // progress was too fast to poll), take the on-disk length so the UI
        // doesn't show "0 B" on completed rows.
        if (it.Received == 0)
            try
            {
                var len = new FileInfo(it.Path).Length;
                if (len > 0) it.Received = len;
                if (it.Total < 0) it.Total = len;
            }
            catch { }
        if (it.Received == 0 && it.Total > 0) it.Received = it.Total;
        Changed?.Invoke();
    }

    public void Fail(Item it, string? error)
    {
        if (it.State != 0) return;
        it.State = 2;
        it.Error = error;
        Changed?.Invoke();
    }

    /// <summary>User-initiated cancel. The mac path cancels the native
    /// WKDownload; elsewhere we just mark it — the engine has no stop call.</summary>
    public void Cancel(string id)
    {
        var it = Find(id); if (it is null || it.State != 0) return;
        if (it.Wk != IntPtr.Zero && OperatingSystem.IsMacOS())
            Mac.WkDownloads.CancelDownload(it.Wk);
        else if (it.CdpGuid is not null && EngineCancel is not null)
            _ = EngineCancel(it);
        it.State = 3;
        Changed?.Invoke();
    }

    public void ClearDone()
    {
        lock (_lock) _items.RemoveAll(i => i.State != 0);
        Changed?.Invoke();
    }

    public Item? Find(string id) { lock (_lock) return _items.FirstOrDefault(i => i.Id == id); }

    /// <summary>Open the finished file with the OS default app.</summary>
    public void Open(string id)
    {
        var it = Find(id);
        if (it is null || it.Path.Length == 0 || !File.Exists(it.Path)) return;
        try { Process.Start(new ProcessStartInfo(it.Path) { UseShellExecute = true }); } catch { }
    }

    /// <summary>Reveal in Finder / Explorer.</summary>
    public void Reveal(string id)
    {
        var it = Find(id);
        if (it is null || it.Path.Length == 0) return;
        try
        {
            if (OperatingSystem.IsMacOS())
                Process.Start("open", "-R " + Quote(it.Path));
            else if (OperatingSystem.IsWindows())
                Process.Start("explorer.exe", "/select," + Quote(it.Path));
            else
                Process.Start("xdg-open", Quote(Path.GetDirectoryName(it.Path) ?? Dir));
        }
        catch { }
    }

    private static string Quote(string s) => "\"" + s + "\"";

    internal static string Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var n = name.Trim();
        var bad = System.IO.Path.GetInvalidFileNameChars();
        var cs = new char[n.Length];
        for (var i = 0; i < n.Length; i++)
        {
            var c = n[i];
            cs[i] = c is '/' or '\\' or ':' || Array.IndexOf(bad, c) >= 0 || char.IsControl(c) ? '-' : c;
        }
        return new string(cs).Trim();
    }

    private static string NameFromUrl(string url)
    {
        try
        {
            var p = new Uri(url).AbsolutePath;
            var seg = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (seg.Length > 0)
            {
                var n = Sanitize(Uri.UnescapeDataString(seg[^1]));
                if (n.Length > 0 && n != "-") return n;
            }
        }
        catch { }
        return "download";
    }

    private static string Unique(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; i < 1000; i++)
        {
            var p = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(p)) return p;
        }
        return path;
    }

    /// <summary>Snapshot for the toolbar: [{i,n,r,t,s,e}] newest first.</summary>
    public string SnapshotJson()
    {
        var sb = new System.Text.StringBuilder("{\"b\":20,\"dls\":[");
        lock (_lock)
        {
            var first = true;
            foreach (var it in _items)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"i\":").Append(it.Id)
                  .Append(",\"n\":").Append(System.Text.Json.JsonSerializer.Serialize(it.Name))
                  .Append(",\"u\":").Append(System.Text.Json.JsonSerializer.Serialize(it.Url))
                  .Append(",\"r\":").Append(it.Received)
                  .Append(",\"t\":").Append(it.Total)
                  .Append(",\"s\":").Append(it.State);
                if (it.Error is not null)
                    sb.Append(",\"e\":").Append(System.Text.Json.JsonSerializer.Serialize(it.Error));
                sb.Append('}');
            }
        }
        return sb.Append("]}").ToString();
    }
}
