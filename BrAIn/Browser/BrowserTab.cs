using System.Collections.Concurrent;
using Photino.NET;

namespace BrAIn.Browser;

/// <summary>
/// One browser tab — a logical entry; all tabs share the single main window's
/// webview (Photino embeds exactly one webview per window). Switching tabs
/// navigates the window to that tab's URL.
/// </summary>
public sealed class BrowserTab
{
    public required Guid Id { get; init; }
    public required PhotinoWindow Window { get; set; }
    public bool IsMain { get; init; }

    /// <summary>Driver once the platform channel is attached; null until ready.</summary>
    public IBrowserDriver? Driver { get; set; }

    /// <summary>Windows/CDP: the DevTools target id this tab is bound to.</summary>
    public string? TargetId { get; set; }

    /// <summary>macOS: NSWindow windowNumber used to relocate the WKWebView.</summary>
    public long MacWindowNumber { get; set; }

    /// <summary>macOS per-tab mode: this tab's own WKWebView (never shared).
    /// Zero until the view is created; zero again once the tab is closed.</summary>
    public IntPtr NativeView { get; set; }

    /// <summary>Per-tab mode: a page load is in flight in this tab's view.</summary>
    public volatile bool Loading;

    /// <summary>Per-tab mode: this tab's view can go back / forward.</summary>
    public bool CanBack { get; set; }
    public bool CanFwd { get; set; }

    /// <summary>Set while a child window is being created; used to bind it to
    /// the automation channel (CDP target / WKWebView) once it appears.</summary>
    public TaskCompletionSource Bound { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string LastUrl { get; set; } = "about:blank";
    public string? LastTitle { get; set; }
    public string? LastIcon { get; set; }

    /// <summary>Last reported window scroll position — logical tabs reload on
    /// switch, so we restore it after load like a real browser's reload does.</summary>
    public int? ScrollX { get; set; }
    public int? ScrollY { get; set; }

    /// <summary>Private tab: no history, never joins the reopen stack or the
    /// persisted session, and its site data is wiped on close.</summary>
    public bool IsPrivate { get; set; }

    /// <summary>Pinned tab: compact icon-only pill pinned to the strip's left
    /// edge; immune to close/middle-click until unpinned.</summary>
    public bool IsPinned { get; set; }

    /// <summary>User muted this tab. Applies whenever the tab owns the live
    /// document, so it survives navigation and tab switches.</summary>
    public bool IsMuted { get; set; }

    /// <summary>Tab's document is currently producing sound (only ever true
    /// for the document owner — logical tabs unload when switched away).</summary>
    public bool IsAudible { get; set; }

    public bool IsClosed { get; set; }

    public override string ToString() => $"{Id.ToString()[..8]} {(IsMain ? "(main) " : "")}{LastTitle ?? "?"} <{LastUrl}>";
}

/// <summary>Tracks all tabs and which one tool calls target.</summary>
public sealed class BrowserTabRegistry
{
    private readonly ConcurrentDictionary<Guid, BrowserTab> _tabs = new();
    // ConcurrentDictionary enumeration order isn't guaranteed — keep an
    // explicit strip order so tabs render where the user put them.
    private readonly List<Guid> _order = new();
    private readonly object _orderLock = new();
    private Guid _activeId;

    public IReadOnlyList<BrowserTab> All
    {
        get
        {
            var list = new List<BrowserTab>(_tabs.Count);
            lock (_orderLock)
                foreach (var id in _order)
                    if (_tabs.TryGetValue(id, out var t)) list.Add(t);
            if (list.Count != _tabs.Count) // anything untracked goes last
                foreach (var t in _tabs.Values)
                    if (!list.Contains(t)) list.Add(t);
            return list;
        }
    }

    /// <summary>Move a tab to a new position in the strip order.</summary>
    public bool Move(Guid id, int index)
    {
        lock (_orderLock)
        {
            var cur = _order.IndexOf(id);
            if (cur < 0) return false;
            index = Math.Clamp(index, 0, _order.Count - 1);
            if (index == cur) return false;
            _order.RemoveAt(cur);
            _order.Insert(index, id);
            return true;
        }
    }

    public int IndexOf(Guid id)
    {
        lock (_orderLock) return _order.IndexOf(id);
    }

    /// <summary>Pinned tabs cluster at the strip's left edge.</summary>
    public int PinnedCount
    {
        get
        {
            lock (_orderLock)
                return _order.Count(id => _tabs.TryGetValue(id, out var t) && t.IsPinned);
        }
    }

    public BrowserTab? Active =>
        _tabs.TryGetValue(_activeId, out var t) ? t : All.FirstOrDefault();

    /// <summary>The explicitly activated tab — no first-tab fallback (that
    /// fallback made a just-closed active tab look like tab 0 was active).</summary>
    public BrowserTab? ActiveExact => _tabs.TryGetValue(_activeId, out var t) ? t : null;

    public BrowserTab? Main => _tabs.Values.FirstOrDefault(t => t.IsMain);

    /// <summary>New tabs slot in right after the active one, like a real browser.</summary>
    public void Add(BrowserTab tab)
    {
        _tabs[tab.Id] = tab;
        lock (_orderLock)
        {
            var ai = _order.IndexOf(_activeId);
            _order.Insert(ai < 0 ? _order.Count : ai + 1, tab.Id);
        }
        if (_tabs.Count == 1) _activeId = tab.Id;
    }

    public bool Activate(Guid id)
    {
        if (!_tabs.TryGetValue(id, out var tab) || tab.IsClosed) return false;
        _activeId = id;
        return true;
    }

    public bool Remove(Guid id)
    {
        lock (_orderLock) _order.Remove(id);
        if (_activeId == id) _activeId = Guid.Empty;
        return _tabs.TryRemove(id, out _);
    }

    public BrowserTab? Find(Guid id) => _tabs.TryGetValue(id, out var t) ? t : null;

    public BrowserTab? FindByPrefix(string idOrPrefix)
    {
        if (string.IsNullOrEmpty(idOrPrefix)) return null;
        return Guid.TryParse(idOrPrefix, out var g) ? Find(g)
            : All.FirstOrDefault(t => t.Id.ToString().StartsWith(idOrPrefix, StringComparison.OrdinalIgnoreCase));
    }
}
