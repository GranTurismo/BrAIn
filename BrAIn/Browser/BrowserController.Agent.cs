using BrAIn.Browser.Import;

namespace BrAIn.Browser;

/// <summary>
/// Programmatic entry points for the MCP tools that mirror things the user
/// does in the toolbar: bookmarks, history, tab pin/mute/move, the clipboard
/// and browser-data import. The toolbar's own handlers stay in
/// BrowserController.cs; these take explicit arguments and report back.
/// </summary>
public sealed partial class BrowserController
{
    // ---------------- bookmarks ----------------

    private static string BmKey(string url) => url.Trim().TrimEnd('/');

    private void SaveBookmarksLocked()
        => SaveStore("bookmarks.json", _bookmarks.Select(b => new { u = b.Url, t = b.Title, i = b.Icon }));

    public IReadOnlyList<(string Url, string? Title)> ListBookmarks(string? query = null, int limit = 50)
    {
        var terms = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        lock (_bookmarks)
            return _bookmarks
                .Where(b => terms.All(t =>
                    b.Url.Contains(t, StringComparison.OrdinalIgnoreCase)
                    || (b.Title?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false)))
                .Take(Math.Clamp(limit, 1, BookmarkCap))
                .Select(b => (b.Url, b.Title))
                .ToList();
    }

    public bool IsBookmarked(string url)
    {
        var k = BmKey(url);
        lock (_bookmarks) return _bookmarks.Any(b => BmKey(b.Url) == k);
    }

    /// <summary>Adds an http(s) page to the bookmarks. Returns false when it was already there.</summary>
    public bool AddBookmark(string url, string? title = null)
    {
        url = url.Trim();
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("only http(s) pages can be bookmarked");
        string? icon = null;
        var tab = Tabs.All.FirstOrDefault(t => !t.IsPrivate && BmKey(t.LastUrl) == BmKey(url));
        if (tab is not null) { title ??= tab.LastTitle; icon = tab.LastIcon; }
        lock (_history)
        {
            var h = _history.FirstOrDefault(h => BmKey(h.Url) == BmKey(url));
            if (h is not null) { title ??= h.Title; icon ??= h.Icon; }
        }
        lock (_bookmarks)
        {
            var k = BmKey(url);
            if (_bookmarks.Any(b => BmKey(b.Url) == k)) return false;
            _bookmarks.Insert(0, (url, string.IsNullOrWhiteSpace(title) ? null : title, icon));
            if (_bookmarks.Count > BookmarkCap) _bookmarks.RemoveAt(BookmarkCap);
            SaveBookmarksLocked();
        }
        PushChromeStateAll();
        return true;
    }

    /// <summary>Removes a bookmark by URL. Returns false when there was none.</summary>
    public bool RemoveBookmark(string url)
    {
        var k = BmKey(url);
        lock (_bookmarks)
        {
            if (_bookmarks.RemoveAll(b => BmKey(b.Url) == k) == 0) return false;
            SaveBookmarksLocked();
        }
        PushChromeStateAll();
        return true;
    }

    // ---------------- history ----------------

    public IReadOnlyList<HistoryEntry> SearchHistory(string? query = null, int limit = 25)
    {
        var terms = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        lock (_history)
            return _history
                .Where(h => terms.All(t =>
                    h.Url.Contains(t, StringComparison.OrdinalIgnoreCase)
                    || (h.Title?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false)))
                .Take(Math.Clamp(limit, 1, HistoryCap))
                .ToList();
    }

    /// <summary>Deletes every history entry matching all terms (URL or title). Returns how many went.</summary>
    public int ForgetHistoryMatching(string query)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return 0;
        int n;
        lock (_history)
        {
            n = _history.RemoveAll(h => terms.All(t =>
                h.Url.Contains(t, StringComparison.OrdinalIgnoreCase)
                || (h.Title?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false)));
            if (n > 0) SaveHistoryStore();
        }
        if (n > 0) PushChromeStateAll();
        return n;
    }

    public int ForgetHistoryUrl(string url)
    {
        int n;
        lock (_history) n = _history.Count(h => h.Url == url);
        if (n > 0) DeleteHistory(url);
        return n;
    }

    public int ClearAllHistory()
    {
        int n;
        lock (_history) n = _history.Count;
        ClearHistory();
        return n;
    }

    // ---------------- tabs ----------------

    public void SetTabPinned(BrowserTab tab, bool pinned)
    {
        if (tab.IsPinned == pinned) return;
        tab.IsPinned = pinned;
        // pinned tabs cluster at the strip's left edge
        Tabs.Move(tab.Id, pinned ? Tabs.PinnedCount - 1 : Tabs.PinnedCount);
        PushChromeStateAll();
        SaveSessionSoon();
    }

    public void SetTabMuted(BrowserTab tab, bool muted)
    {
        if (tab.IsMuted != muted) ToggleTabMute(tab);
    }

    public bool MoveTab(BrowserTab tab, int index)
    {
        // pinned tabs keep the left edge: a pinned tab stays inside it, any other stays outside
        var pinned = Tabs.PinnedCount;
        index = tab.IsPinned ? Math.Min(index, pinned - 1) : Math.Max(index, pinned);
        var moved = Tabs.Move(tab.Id, index);
        if (moved) { PushChromeStateAll(); SaveSessionSoon(); }
        return moved;
    }

    public Task<BrowserTab> DuplicateTabAsync(BrowserTab tab, CancellationToken ct = default)
        => NewTabAsync(tab.LastUrl, ct, tab.IsPrivate);

    /// <summary>Reopens the most recently closed tab; null when there is none.</summary>
    public async Task<BrowserTab?> ReopenClosedTabAsync(CancellationToken ct = default)
        => _closedTabs.TryPop(out var c) ? await NewTabAsync(c.Url, ct) : null;

    // ---------------- clipboard ----------------

    public string? ReadClipboard()
    {
        if (IsMac) return InvokeOnUi(() => Mac.WkClipboard.Text());
        if (OperatingSystem.IsWindows()) return Native.Win32.ClipboardText();
        throw new NotSupportedException("clipboard access is not available on this platform");
    }

    public bool WriteClipboard(string text)
    {
        if (IsMac) return InvokeOnUi(() => Mac.WkClipboard.SetText(text));
        if (OperatingSystem.IsWindows()) return Native.Win32.SetClipboardText(text);
        throw new NotSupportedException("clipboard access is not available on this platform");
    }

    // ---------------- import ----------------

    /// <summary>Browsers on this machine whose bookmarks/history can be imported.</summary>
    public async Task<IReadOnlyList<BrowserSource>> ScanImportSourcesAsync()
    {
        var sources = await Task.Run(BrowserImporter.Scan);
        _importScan = sources;
        return sources;
    }

    /// <summary>Imports bookmarks and/or history from one scanned browser. Cookies and
    /// saved passwords are deliberately not reachable from here; they stay a user action
    /// on about:import.</summary>
    public async Task<(int Bookmarks, int History)> ImportFromBrowserAsync(string sourceId, bool bookmarks, bool history)
    {
        var src = _importScan?.FirstOrDefault(s => s.Id == sourceId)
            ?? (await ScanImportSourcesAsync()).FirstOrDefault(s => s.Id == sourceId)
            ?? throw new ArgumentException($"no importable browser '{sourceId}' (run action=scan first)");
        int bm = 0, hist = 0;
        if (bookmarks) bm = MergeImportedBookmarks(await Task.Run(() => BrowserImporter.ReadBookmarks(src)));
        if (history) hist = MergeImportedHistory(await Task.Run(() => BrowserImporter.ReadHistory(src)));
        PushChromeStateAll();
        return (bm, hist);
    }
}
