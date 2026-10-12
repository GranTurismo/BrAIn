using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using BrAIn.Browser.Drivers;
using BrAIn.Browser.Import;
using Photino.NET;

namespace BrAIn.Browser;

/// <summary>
/// Owns the Photino window(s), the per-tab automation driver, the
/// window.external.sendMessage return channel, and log buffers.
/// The MCP tool layer talks only to this class.
/// </summary>
public sealed class BrowserController : IAsyncDisposable
{
    public const string AppName = "BrAIn";

    private readonly string? _initialUrl;
    private readonly int? _forcedCdpPort;
    private readonly bool _noToolbar;
    private readonly bool _noFullscreen;
    // macOS: the toolbar lives in its own native web view above the page view
    // (Browser/Mac/WkBar.cs). On by default; --no-split / BRAIN_SPLIT=0 falls
    // back to the in-page toolbar that reserves space by shifting the page.
    private readonly bool _splitMode = Environment.GetEnvironmentVariable("BRAIN_SPLIT") != "0";
    private bool SplitOn => _splitMode && IsMac && !_noToolbar;
    // macOS split mode gives every tab its own WKWebView (Browser/Mac/WkTabs.cs):
    // switching tabs shows another view instead of reloading one shared page.
    // Windows does the same with WebView2 (Browser/Win/WinTabs.cs): one view per tab, parented to
    // the main window. BRAIN_PERTAB=0 or a failure creating the views falls back to the old
    // behaviour where all tabs share one view (also what --no-split / --no-toolbar get).
    private bool _winTabsWanted;
    private bool _winTabsFailed;
    private bool WinPerTab => _winTabsWanted && !_winTabsFailed;
    private bool PerTab => SplitOn || WinPerTab;
    private Win.WinTabs? _winTabs;
    private long _nsWindowNumber;
    private BrowserTab? _firstTab;
    private readonly ConcurrentDictionary<Guid, Task<IBrowserDriver>> _viewTasks = new();
    private readonly bool _grantPermissions;
    private readonly string _userDataDir;

    private PhotinoWindow? _mainWindow;
    private readonly TaskCompletionSource _windowCreated =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _driverReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _closed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>JS→host envelopes. Keyed by eval id; the page posts
    /// {"b":1,"id":..,"ok":..,"v":..} through window.external.sendMessage.</summary>
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pendingEval = new();

    private readonly ConcurrentQueue<ConsoleEntry> _console = new();
    // Per-request merge: CDP emits one event per phase (request/response/
    // finished), so keying by requestId yields a single row per request that
    // accumulates status, bytes, headers. Ordered view = sort by Time.
    private readonly ConcurrentDictionary<string, NetworkEntry> _network = new();
    private const int LogCap = 500;

    // Chrome state: loading spinner, nav availability, visit history (most
    // recent first) feeding the omnibox dropdown and the new-tab tiles.
    // _loadDepth counts overlapping loads — a tab switch aborts one load and
    // starts another, so a plain boolean gets clobbered by the stale stop.
    private int _loadDepth;
    private bool _canBack;
    private bool _canFwd;
    private readonly List<HistoryEntry> _history = new();
    // Sized for imported browser history (real Firefox/Chrome profiles carry
    // thousands of entries). The omnibox only ever pushes the top 40.
    private const int HistoryCap = 5000;
    private readonly ConcurrentStack<(string Url, string? Icon)> _closedTabs = new();
    // DevTools panel survives document swaps: chrome.js reports its state and
    // every boot/push carries it so the panel reopens on the new document.
    private bool _dtOpen;
    private string _dtTab = "con";

    // Browser-level state shared across tabs (one window = one zoom level).
    private int _zoom = 100;
    private bool _topmost;
    // 8-char id prefix of the tab that was just created — baked into the next
    // document's boot state so only it plays the slide-in animation.
    private string? _pendingFresh;
    private static readonly int[] ZoomSteps = [25, 33, 50, 67, 75, 80, 90, 100, 110, 125, 150, 175, 200, 250, 300, 400, 500];

    private static readonly SearchEngine[] SearchEngines =
    [
        new("google",     "Google",       "https://www.google.com/search?q="),
        new("bing",       "Bing",         "https://www.bing.com/search?q="),
        new("duckduckgo", "DuckDuckGo",   "https://duckduckgo.com/?q="),
        new("brave",      "Brave Search", "https://search.brave.com/search?q="),
        new("yahoo",      "Yahoo",        "https://search.yahoo.com/search?p="),
        new("ecosia",     "Ecosia",       "https://www.ecosia.org/search?q="),
        new("startpage",  "Startpage",    "https://www.startpage.com/sp/search?query="),
    ];
    private SearchEngine _engine = SearchEngines[0];

    /// <summary>Encrypted credential vault — passwords.dat, key in the OS
    /// keystore. <c>_pwSkip</c> holds origins where the user picked "Never".</summary>
    private PasswordStore? _pw;
    private readonly HashSet<string> _pwSkip = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Save-prompts survive navigation: a submit usually loads a new
    /// document, so the b:24 push may land on a dying page. The pending entry
    /// is re-flushed when the next document mounts (its 'tabs' hello) and
    /// cleared by save/skip/dismiss or after a couple of attempts.</summary>
    private readonly Dictionary<Guid, (string O, string U, string P, int Attempts)> _pwPending = new();

    private readonly List<(string Url, string? Title, string? Icon)> _bookmarks = new();
    private const int BookmarkCap = 2000;

    // URLs open when the previous run exited — offered via "Restore session".
    // The file is continuously overwritten while running, so keep the startup
    // copy here and never reload it.
    private List<string> _savedSession = new();
    private int _sessionDirty;

    /// <summary>%LOCALAPPDATA%\BrAIn — parent of the per-process profile dirs,
    /// shared across runs so bookmarks/history persist.</summary>
    private readonly string _storeDir;
    /// <summary>Folder holding bookmarks, history, the MCP call log and saved flows.</summary>
    public string StoreDir => _storeDir;
    /// <summary>History of MCP tool calls (calls.jsonl).</summary>
    public BrAIn.Mcp.ActivityLog Activity { get; private set; } = null!;
    /// <summary>User-approved reusable automations, exposed to agents as flow_* tools.</summary>
    public BrAIn.Mcp.FlowService Flows { get; private set; } = null!;
    public BrAIn.Mcp.Seatbelt Belt { get; private set; } = null!;
    public BrAIn.Mcp.Handoff Handoff { get; private set; } = null!;
    public BrAIn.Mcp.FlowRecorder Recorder { get; private set; } = null!;
    public Updater Updater { get; } = new();

    private Cdp.CdpClient? _cdpBrowser; // Windows: browser-level CDP connection
    /// <summary>The tab object that owns the shared window's driver. Kept
    /// separately from the registry so closing the main tab doesn't orphan
    /// the driver for the remaining tabs.</summary>
    private BrowserTab? _driverTab;

    /// <summary>Browser-wide downloads; engine glue funnels lifecycle here,
    /// changes are pushed to the toolbar as {b:20}.</summary>
    internal Downloads Downloads { get; } = new Downloads(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));

    /// <summary>The tab whose document currently lives in the shared webview.
    /// Blank new tabs are "virtual": chrome.js paints the start page as an
    /// overlay over this document instead of loading a new one — every
    /// document load spins a fresh renderer process (~200ms cold start), which
    /// was the visible new-tab delay. Switching back to this tab is instant.</summary>
    private Guid? _docTabIdValue;
    private Guid? _docTabId
    {
        get => _docTabIdValue;
        set
        {
            _docTabIdValue = value;
            // Mute follows the document: apply the new owner's flag before its
            // page starts making sound, so a muted tab never leaks audio.
            SyncPageMute();
        }
    }
    private BrowserTab? DocTab => !PerTab && _docTabId is { } id ? Tabs.Find(id) : null;

    // Ownership moves to a tab the moment it starts navigating, but the shared
    // web view keeps showing the previous owner's page — or is still loading it —
    // until the new document commits. While one of those sites is what's on
    // screen (_staleSites) nothing may be attributed to the new owner: not the
    // URL watcher, not the post-load refresh, not the navigation result.
    // Compared by site (host minus "www.") so redirects still match.
    private readonly HashSet<string> _staleSites = new(StringComparer.OrdinalIgnoreCase);
    private string? _inflightNavSite;
    private Guid _inflightNavTab;

    private static string? SiteKey(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https")) return null;
        var h = u.Host.ToLowerInvariant();
        return h.StartsWith("www.", StringComparison.Ordinal) ? h[4..] : h;
    }

    internal bool IsStaleDocUrl(string? url)
    {
        var k = SiteKey(url);
        if (k is null) return false;
        lock (_staleSites) return _staleSites.Contains(k);
    }

    internal void ClearStaleDoc() { lock (_staleSites) _staleSites.Clear(); }

    private void MarkStaleDoc(Guid newOwner, string? targetUrl)
    {
        lock (_staleSites)
        {
            _staleSites.Clear();
            if (_docTabId != newOwner)
            {
                var cur = SiteKey((_driverTab?.Driver as Drivers.WebKitDriver)?.NativeUrl());
                if (cur is not null) _staleSites.Add(cur);
            }
            // another tab's navigation may still be loading — it will commit later
            if (_inflightNavSite is { } inflight && _inflightNavTab != newOwner) _staleSites.Add(inflight);
            // ...but never treat the page we're about to load as stale
            var tgt = SiteKey(targetUrl);
            if (tgt is not null) _staleSites.Remove(tgt);
        }
        _inflightNavTab = newOwner;
        _inflightNavSite = SiteKey(targetUrl);
    }

    /// <summary>The tab that owns the shared webview's live document (drivers stamp URL changes onto it).</summary>
    internal BrowserTab? DocOwner => DocTab;

    /// <summary>Pushes the document owner's mute flag to the webview and
    /// clears the speaker indicator on every other tab (their pages are gone).</summary>
    internal void SyncPageMute()
    {
        try
        {
            if (PerTab)
            {
                // every tab owns its view, so each one just applies its own flag
                foreach (var t in Tabs.All) ApplyTabMute(t);
                return;
            }
            var owner = DocTab;
            foreach (var t in Tabs.All)
                if (t != owner && t.IsAudible) t.IsAudible = false;
            _driverTab?.Driver?.SetPageMuted(owner?.IsMuted == true);
        }
        catch { /* no window/driver yet — applied again once the driver is ready */ }
    }

    /// <summary>The page's audio state changed (native poll on macOS, in-page
    /// detection elsewhere). Attributed to the document owner.</summary>
    internal void SetPageAudible(bool on)
    {
        var t = PerTab ? Tabs.Active : DocTab;
        if (t is null || t.IsAudible == on) return;
        t.IsAudible = on;
        PushChromeStateAll();
    }

    /// <summary>Per-tab mode: a tab's own view started/stopped playing sound.</summary>
    internal void SetTabAudible(BrowserTab tab, bool on)
    {
        if (tab.IsClosed || tab.IsAudible == on) return;
        tab.IsAudible = on;
        PushChromeStateAll();
    }

    /// <summary>Per-tab mute: macOS drivers mute their WKWebView; on Windows the tab's WebView2 view does.</summary>
    private void ApplyTabMute(BrowserTab t)
    {
        if (t.WinView is Win.WinTabs.View wv) _winTabs?.SetMuted(wv, t.IsMuted);
        else t.Driver?.SetPageMuted(t.IsMuted);
    }

    private void ToggleTabMute(BrowserTab tab)
    {
        tab.IsMuted = !tab.IsMuted;
        if (PerTab) ApplyTabMute(tab);
        else SyncPageMute();
        PushChromeStateAll();
    }

    /// <summary>Active tab is a blank new tab painted as an overlay.</summary>
    private bool OverlayActive =>
        !PerTab && Tabs.Active is { } a && a.Id != _docTabId && a.LastUrl == "about:newtab";

    public BrowserTabRegistry Tabs { get; } = new();
    public int? CdpPort { get; private set; }
    public static bool IsWindows => OperatingSystem.IsWindows();
    public static bool IsMac => OperatingSystem.IsMacOS();

    /// <summary>Frameless window: tabs live in the title-bar row next to our
    /// own min/max/close buttons (Windows only — macOS keeps native traffic
    /// lights; --no-toolbar automation keeps native chrome so the window
    /// stays movable/closable without our bar).</summary>
    private bool Chromeless => IsWindows && !_noToolbar;

    /// <summary>JS the document-start user script and post-load fallback
    /// inject — brain.js alone for chromeless windows, both libs otherwise.</summary>
    internal string PageLibSource => _noToolbar ? Assets.BrainJs
        : SplitOn ? "window.__BRAIN_SPLIT=true;\n" + Assets.CombinedLib : Assets.CombinedLib;
    private bool _winMax;

    public BrowserController(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--cdp-port" when i + 1 < args.Length:
                    _forcedCdpPort = int.Parse(args[++i]);
                    break;
                case "--no-toolbar":
                    _noToolbar = true;
                    break;
                case "--grant-permissions":
                    _grantPermissions = true;
                    break;
                case "--no-fullscreen":
                    _noFullscreen = true;
                    break;
                case "--no-split":
                    _splitMode = false;
                    break;
                case { } a when !a.StartsWith('-'):
                    _initialUrl = NormalizeUrl(a);
                    break;
            }
        }
        if (Environment.GetEnvironmentVariable("BRAIN_GRANT_PERMISSIONS") is "1" or "true") _grantPermissions = true;
        _winTabsWanted = IsWindows && !_noToolbar
            && Environment.GetEnvironmentVariable("BRAIN_PERTAB") != "0"
            && Environment.GetEnvironmentVariable("BRAIN_NO_CDP") is null;
        _userDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BrAIn",
            $"profile-{Environment.ProcessId}");
        _storeDir = Path.GetDirectoryName(_userDataDir)!;
        Directory.CreateDirectory(_userDataDir);
        _pw = new PasswordStore(_storeDir);
        Activity = new BrAIn.Mcp.ActivityLog(_storeDir);
        Flows = new BrAIn.Mcp.FlowService(this, Activity);
        Activity.Changed += ScheduleFlowsPush;
        Flows.Changed += ScheduleFlowsPush;
        Flows.PausedCheck = () => _agPaused;
        Belt = new BrAIn.Mcp.Seatbelt(_storeDir);
        Belt.Changed += PushBelt;
        Handoff = new BrAIn.Mcp.Handoff();
        Handoff.Changed += PushHandoff;
        Updater.Changed += () => { Log("update: " + Updater); PushUpdate(); };
        Recorder = new BrAIn.Mcp.FlowRecorder();
        Recorder.Changed += () => PushRec();
        LoadStores();
        Downloads.Changed += PushDownloads;
        Task.Run(CleanupStaleProfiles);
        // Release check in the background; the menu badge lights up if one lands.
        _ = Task.Run(async () =>
        {
            await Task.Delay(4000);
            try { await Updater.CheckAsync(false, CancellationToken.None); } catch { }
        });
    }

    // ---------------- persisted stores (bookmarks + history) ----------------

    private void LoadStores()
    {
        try
        {
            var bmPath = Path.Combine(_storeDir, "bookmarks.json");
            if (File.Exists(bmPath))
            {
                foreach (var el in JsonDocument.Parse(File.ReadAllText(bmPath)).RootElement.EnumerateArray())
                    _bookmarks.Add((
                        el.GetProperty("u").GetString() ?? "",
                        el.TryGetProperty("t", out var t) ? t.GetString() : null,
                        el.TryGetProperty("i", out var i) ? i.GetString() : null));
            }
            var hPath = Path.Combine(_storeDir, "history.json");
            if (File.Exists(hPath))
            {
                foreach (var el in JsonDocument.Parse(File.ReadAllText(hPath)).RootElement.EnumerateArray())
                    _history.Add(new HistoryEntry(
                        el.GetProperty("u").GetString() ?? "",
                        el.TryGetProperty("t", out var t) ? t.GetString() : null,
                        el.TryGetProperty("i", out var i) ? i.GetString() : null,
                        el.TryGetProperty("d", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt64() : 0,
                        el.TryGetProperty("v", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 1));
            }
            var pPath = Path.Combine(_storeDir, "prefs.json");
            if (File.Exists(pPath))
            {
                var root = JsonDocument.Parse(File.ReadAllText(pPath)).RootElement;
                if (root.TryGetProperty("se", out var se))
                    _engine = SearchEngines.FirstOrDefault(x => x.Id == se.GetString()) ?? _engine;
                // Rewind is opt-in: absent / malformed = off.
                _rwOn = root.TryGetProperty("rw", out var rwp) && rwp.ValueKind == JsonValueKind.True;
                _rwAgent = root.TryGetProperty("rwa", out var rwa) && rwa.ValueKind == JsonValueKind.True;
                if (root.TryGetProperty("rwk", out var rwk) && rwk.ValueKind == JsonValueKind.Number) _rwKeepMin = rwk.GetInt32();
                if (root.TryGetProperty("rwm", out var rwm) && rwm.ValueKind == JsonValueKind.Number) _rwMaxMb = rwm.GetInt32();
                if (root.TryGetProperty("rws", out var rws) && rws.ValueKind == JsonValueKind.Array)
                    foreach (var h in rws.EnumerateArray())
                        if (h.GetString() is { Length: > 0 } hs) _rwSkip.Add(hs);
                if (root.TryGetProperty("keys", out var keys) && keys.ValueKind == JsonValueKind.Object)
                    foreach (var kv in keys.EnumerateObject())
                        if (kv.Value.ValueKind == JsonValueKind.String)
                            _keyBinds[kv.Name] = kv.Value.GetString() ?? "";
                ApplyRwConfig();
            }
            var kPath = Path.Combine(_storeDir, "pwskip.json");
            if (File.Exists(kPath))
            {
                foreach (var el in JsonDocument.Parse(File.ReadAllText(kPath)).RootElement.EnumerateArray())
                    if (el.GetString() is { } o && o.Length > 0) _pwSkip.Add(o);
            }
            var sPath = Path.Combine(_storeDir, "session.json");
            if (File.Exists(sPath))
            {
                _savedSession = JsonDocument.Parse(File.ReadAllText(sPath)).RootElement
                    .EnumerateArray().Select(e => e.GetString() ?? "")
                    .Where(u => u.Length > 0).ToList();
            }
        }
        catch { /* corrupt/absent store — start fresh */ }
    }

    /// <summary>Persist the open non-private tab URLs ~800ms after the last
    /// mutation — nav/reorder churn shouldn't hammer the disk.</summary>
    private void SaveSessionSoon()
    {
        if (Interlocked.Exchange(ref _sessionDirty, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(800);
            Interlocked.Exchange(ref _sessionDirty, 0);
            var urls = Tabs.All
                .Where(t => !t.IsClosed && !t.IsPrivate &&
                    t.LastUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                .Select(t => t.LastUrl).ToList();
            SaveStore("session.json", urls);
        });
    }

    private void SaveStore(string name, object rows)
    {
        try { File.WriteAllText(Path.Combine(_storeDir, name), JsonSerializer.Serialize(rows)); }
        catch { /* disk full / locked — non-fatal */ }
    }

    /// <summary>Each process needs its own WebView2 user-data folder — WebView2
    /// environments sharing one share a single browser process, so a second
    /// instance's AdditionalBrowserArguments (our --remote-debugging-port)
    /// would be silently ignored. Prunes dirs left by dead processes.</summary>
    private void CleanupStaleProfiles()
    {
        var root = Path.GetDirectoryName(_userDataDir)!;
        foreach (var dir in Directory.EnumerateDirectories(root, "profile-*"))
        {
            var name = Path.GetFileName(dir);
            if (!int.TryParse(name["profile-".Length..], out var pid)
                || pid == Environment.ProcessId)
                continue;
            try { System.Diagnostics.Process.GetProcessById(pid); }
            catch
            {
                try { Directory.Delete(dir, recursive: true); }
                catch { /* locked files — leave it */ }
            }
        }
    }

    public Task Ready => _driverReady.Task;
    public Task Closed => _closed.Task;

    // ------------------------------------------------------------------
    // Window lifecycle (main thread)
    // ------------------------------------------------------------------

    /// <summary>Creates the main window and runs the OS message loop.
    /// Blocks until the window is closed. Call on the process main thread.</summary>
    public void RunWindow()
    {
        var (window, tab) = CreatePhotinoWindow();
        tab.LastUrl = _initialUrl ?? "about:newtab";
        if (!PerTab) _docTabId = tab.Id;

        _mainWindow = window;
        Tabs.Add(tab);
        PrepareLoad(window);

        try
        {
            window.WaitForClose(); // blocks; runs OS message loop
        }
        finally
        {
            tab.IsClosed = true;
            _closed.TrySetResult();
        }
    }

    /// <summary>Photino's default agent is the literal string "Photino WebView".
    /// Sites sniff the UA, and an unknown one gets a degraded legacy page —
    /// Facebook served a light-theme layout with no sidebars to it, rendering
    /// dark-on-dark and unreadable. Present as the engine's own browser.</summary>
    private static string DesktopUserAgent => OperatingSystem.IsMacOS()
        ? "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.0 Safari/605.1.15"
        : "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36 Edg/141.0.0.0";

    private (PhotinoWindow Window, BrowserTab Tab) CreatePhotinoWindow()
    {
        var tab = new BrowserTab
        {
            Id = Guid.NewGuid(),
            IsMain = true,
            Window = null!, // set below, before handlers can fire
        };

        var window = new PhotinoWindow()
            .SetLogVerbosity(0) // NEVER write to stdout — stdio MCP lives there (set first to suppress title logging)
            .SetTitle(AppName)
            .SetUseOsDefaultSize(false)
            .SetUseOsDefaultLocation(false)
            .SetSize(1440, 900)
            .Center()
            .SetDevToolsEnabled(false) // no WebKit inspector — ours lives in the page (F12)
            .SetContextMenuEnabled(true)
            // Sites must ask: camera, microphone, location, notifications and clipboard
            // reads show the engine's own permission prompt (WebView2 bubble / WebKit alert).
            // Unattended automation can opt back in with --grant-permissions.
            .SetGrantBrowserPermissions(_grantPermissions)
            .SetJavascriptClipboardAccessEnabled(true)
            .SetUserAgent(DesktopUserAgent)
            .SetTemporaryFilesPath(Path.Combine(_userDataDir, "webview"));
        if (Chromeless) window.SetChromeless(true);
        window.IconFile = ResolveIconPath();

        if (IsWindows && Environment.GetEnvironmentVariable("BRAIN_NO_CDP") is null)
        {
            CdpPort ??= _forcedCdpPort ?? FindFreePort();
            // WebView2 honors Chromium flags here; this opens the DevTools
            // endpoint our CdpClient connects to.
            window.SetBrowserControlInitParameters($"--remote-debugging-port={CdpPort}");
        }

        window.RegisterWebMessageReceivedHandler((_, msg) => HandleWebMessage(tab, msg));
        window.RegisterWindowCreatedHandler((_, _) =>
        {
            if (Chromeless)
            {
                Native.Win32.ApplyBorderlessChrome(window.WindowHandle);
                Native.Win32.RoundCorners(window.WindowHandle);
            }
            // Open fullscreen by default (macOS does this after the inline
            // titlebar is applied, in InitializeMainDriverAsync).
            if (IsWindows && !_noFullscreen)
            {
                try { window.FullScreen = true; }
                catch (Exception ex) { Log("auto fullscreen failed: " + ex.Message); }
            }
            // Logical tabs share this single window/webview; only the main
            // driver needs wiring.
            _windowCreated.TrySetResult();
            // Pushes are skipped while _driverReady is pending — flush the
            // current state the moment init settles (success or failure) so
            // a page mounted during init isn't left without tabs/history.
            _ = _driverReady.Task.ContinueWith(_ => PushChromeStateAll(), TaskScheduler.Default);
            Task.Run(() => SplitOn ? InitializePerTabAsync() : WinPerTab ? InitializeWinPerTabAsync() : InitializeMainDriverAsync());
        });
        window.RegisterMaximizedHandler((_, _) =>
        {
            _winMax = true;
            _winTabs?.RefitAll();
            PushChromeStateAll();
        });
        window.RegisterRestoredHandler((_, _) =>
        {
            _winMax = window.Maximized;
            _winTabs?.RefitAll();
            PushChromeStateAll();
        });
        if (IsWindows)
        {
            // Per-tab WebView2 views are plain children of this window: keep them sized to it,
            // tell them when it moves, and hand keyboard focus back to the shown one on activation
            // (Photino hands it to its own, hidden, bootstrap view first).
            window.RegisterSizeChangedHandler((_, _) => _winTabs?.RefitAll());
            window.RegisterLocationChangedHandler((_, _) => _winTabs?.NotifyMoved());
            window.RegisterFocusInHandler((_, _) =>
            {
                if (Tabs.ActiveExact?.WinView is Win.WinTabs.View v) _winTabs?.FocusView(v);
            });
        }
        if (IsMac)
        {
            // The webview's autoresizing mask keeps it pinned top-left; if a
            // fullscreen transition or display move leaves its frame offset
            // (page rendered to one side, blank on the other), snap it back.
            window.RegisterSizeChangedHandler((_, _) =>
            {
                var nsWin = Mac.MacWindow.NsWindowForTab(tab);
                if (nsWin != IntPtr.Zero)
                {
                    Mac.MacWindow.RepairWebViewFrame(nsWin);
                    // Window resizes re-lay out the titlebar; re-pin the
                    // traffic lights to the toolbar's vertical center.
                    Mac.MacWindow.CenterTrafficLights(nsWin, 23);
                }
            });
        }
        window.RegisterWindowClosingHandler((_, _) =>
        {
            try { _winTabs?.CloseAll(); } catch { }
            tab.IsClosed = true;
            Tabs.Remove(tab.Id);
            _rw.RemoveTab(tab.Id); // a closed tab takes its Rewind history with it
            PushChromeStateAll();
            return false; // allow close
        });

        tab.Window = window;
        return (window, tab);
    }

    private static string ResolveIconPath()
    {
        var p = Path.Combine(AppContext.BaseDirectory, "icon.ico");
        return File.Exists(p) ? p : Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico");
    }

    private PhotinoWindow PrepareLoad(PhotinoWindow window)
    {
        if (PerTab)
        {
            // Photino needs some content to open the window; the real pages live in
            // the per-tab views created once the window exists.
            window.LoadRawString("<!doctype html><html><head><meta charset=\"utf-8\"></head><body style=\"background:#0b0b13\"></body></html>");
            return window;
        }
        if (_initialUrl is not null && NormalizeUrl(_initialUrl) != "about:newtab")
            window.Load(new Uri(NormalizeUrl(_initialUrl)));
        else
            window.LoadRawString(InjectBootState(Assets.NewTabHtml, animate: true));
        return window;
    }

    // ------------------------------------------------------------------
    // Driver plumbing
    // ------------------------------------------------------------------

    private async Task InitializeMainDriverAsync()
    {
        try
        {
            var tab = Tabs.All.First(t => t.IsMain);
            tab.Driver = await CreateDriverAsync(tab);
            WireDriverEvents(tab, tab.Driver);
            await tab.Driver.InitializeAsync(this, CancellationToken.None);
            if (IsMac && !_noToolbar && tab.MacWindowNumber != 0)
            {
                // Fold the titlebar into the toolbar — traffic lights overlay
                // the bar's left inset. The webview exists now, so the window
                // is unambiguous.
                try { InvokeOnUi(() => Mac.MacWindow.ApplyInlineTitlebar(Mac.MacWindow.WindowByNumber(tab.MacWindowNumber))); }
                catch (Exception ex) { Log("inline titlebar failed: " + ex.Message); }
            }
            if (SplitOn && tab.MacWindowNumber != 0)
            {
                try { InvokeOnUi(() => StartSplitBar(tab)); }
                catch (Exception ex) { Log("split bar failed: " + ex.Message); }
            }
            if (IsMac && !_noFullscreen && tab.MacWindowNumber != 0)
            {
                // Open in real Space-based fullscreen (same path as ⌃⌘F) — but only
                // once the first page has finished loading. Entering the Space while
                // WKWebView is still mid-load could leave the view blank until a
                // manual reload (seen when launched unbundled via `dotnet run`).
                _ = Task.Run(async () =>
                {
                    try
                    {
                        for (var i = 0; i < 40 && (IsLoading || tab.LastUrl is null); i++) await Task.Delay(100);
                        await Task.Delay(450);
                        InvokeOnUi(() => SetWindowState(tab, "fullscreen"));
                    }
                    catch (Exception ex) { Log("auto fullscreen failed: " + ex.Message); }
                });
            }
            _driverTab = tab;
            tab.Bound.TrySetResult();
            _driverReady.TrySetResult();
            Log("driver ready: " + tab.Driver.Name);
            // Post-startup memory trim: reclaim temporary startup allocations
            _ = Task.Delay(400).ContinueWith(_ =>
            {
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: false, compacting: true);
            }, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            Log("driver init failed: " + ex);
            _driverReady.TrySetException(ex);
            foreach (var t in Tabs.All) t.Bound.TrySetException(ex);
        }
    }

    // ------------------------------------------------------------------
    // Per-tab views (macOS split mode)
    // ------------------------------------------------------------------

    /// <summary>Per-tab startup: find the window Photino made, dress it (inline
    /// titlebar, toolbar view, dark background), hide Photino's own web view —
    /// it only hosts the window — and give the first tab a view of its own.</summary>
    private async Task InitializePerTabAsync()
    {
        try
        {
            var first = Tabs.All.First(t => t.IsMain);
            _firstTab = first;

            IntPtr nsWin = IntPtr.Zero, boot = IntPtr.Zero; long number = 0;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (boot == IntPtr.Zero && DateTime.UtcNow < deadline)
            {
                (nsWin, boot, number) = InvokeOnUi(() =>
                {
                    foreach (var w in Mac.MacWindow.AllWindows())
                    {
                        var v = Mac.MacWindow.FindWebView(w);
                        if (v != IntPtr.Zero) return (w, v, Mac.MacWindow.WindowNumber(w));
                    }
                    return (IntPtr.Zero, IntPtr.Zero, 0L);
                });
                if (boot == IntPtr.Zero) await Task.Delay(25);
            }
            if (boot == IntPtr.Zero) throw new InvalidOperationException("Could not locate the browser window");

            _nsWindowNumber = number;
            first.MacWindowNumber = number;
            Mac.WkPopup.OnNewTab = url => _ = Task.Run(() => NewTabAsync(url, default));
            Mac.WkPopup.Log = Log;
            InvokeOnUi(() =>
            {
                Mac.WkTabs.SetBootstrap(boot);
                try { Mac.MacWindow.ApplyInlineTitlebar(nsWin); }
                catch (Exception ex) { Log("inline titlebar failed: " + ex.Message); }
                try { Mac.WkTabs.SetWindowBackground(nsWin); } catch { }
                try { StartSplitBar(first); }
                catch (Exception ex) { Log("split bar failed: " + ex.Message); }
                Mac.WkTabs.HideBootstrap();
            });

            await EnsureTabViewAsync(first);
            ShowTabView(first); // views are created hidden; the first tab's must be shown explicitly
            _driverTab = first;
            first.Bound.TrySetResult();
            _driverReady.TrySetResult();
            Log("driver ready: per-tab views");

            // Load the first tab's page (start page or the URL from the command line).
            _ = Task.Run(async () =>
            {
                try { await NavigateAsync(first.LastUrl, first); }
                catch (Exception ex) { Log("first load failed: " + ex.Message); }
            });

            if (!_noFullscreen)
            {
                // Real Space-based fullscreen, but only once the first page has settled:
                // entering the Space mid-load could leave a view blank.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        for (var i = 0; i < 40 && (IsLoading || first.LastUrl is null); i++) await Task.Delay(100);
                        await Task.Delay(450);
                        InvokeOnUi(() => SetWindowState(first, "fullscreen"));
                    }
                    catch (Exception ex) { Log("auto fullscreen failed: " + ex.Message); }
                });
            }
            _ = Task.Delay(400).ContinueWith(_ =>
            {
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: false, compacting: true);
            }, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            Log("driver init failed: " + ex);
            _driverReady.TrySetException(ex);
            foreach (var t in Tabs.All) t.Bound.TrySetException(ex);
        }
    }

    // ------------------------------------------------------------------
    // Per-tab views (Windows / WebView2)
    // ------------------------------------------------------------------

    /// <summary>Every tab stays live by default. BRAIN_MAXLIVE=n opts into a cap: older views are
    /// then evicted (destroyed, rebuilt + reloaded on demand) — for machines short on RAM.</summary>
    private int MaxLiveViews =>
        int.TryParse(Environment.GetEnvironmentVariable("BRAIN_MAXLIVE"), out var n) && n >= 2 ? n : int.MaxValue;

    /// <summary>The browser arguments Photino's native layer builds for its own WebView2
    /// environment. A second environment on the same user-data folder only joins the same
    /// browser process if these match exactly, so the composition is replicated verbatim
    /// (Photino.Windows.cpp, AttachWebView).</summary>
    private string PhotinoBrowserArgs(string debugParam)
    {
        bool B(Func<bool> f, bool dflt) { try { return f(); } catch { return dflt; } }
        var w = _mainWindow!;
        return Win.ViewPolicy.ComposePhotinoArgs(DesktopUserAgent,
            B(() => w.MediaAutoplayEnabled, true), B(() => w.FileSystemAccessEnabled, true),
            B(() => w.WebSecurityEnabled, true), B(() => w.JavascriptClipboardAccessEnabled, true),
            B(() => w.MediaStreamEnabled, true), B(() => w.SmoothScrollingEnabled, true),
            B(() => w.IgnoreCertificateErrorsEnabled, false), debugParam);
    }

    /// <summary>Windows per-tab startup: create the shared WebView2 environment, connect CDP to
    /// it, give the first tab a view. Any failure falls back to one shared view for all tabs.</summary>
    private async Task InitializeWinPerTabAsync()
    {
        // Never leave the user staring at an empty window: if per-tab startup stalls, fall back.
        var work = InitializeWinPerTabCoreAsync();
        if (await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(25))) == work) return;
        Log("per-tab WebView2 startup did not finish within 25 s — using one shared view");
        await FallbackToSharedViewAsync();
    }

    private async Task InitializeWinPerTabCoreAsync()
    {
        try
        {
            Log("per-tab startup: creating WebView2 environment");
            var first = Tabs.All.First(t => t.IsMain);
            _firstTab = first;
            var win = _mainWindow ?? throw new InvalidOperationException("No window");
            var ws = new Win.WinTabs(InvokeOnUi, () => win.WindowHandle, _grantPermissions)
            {
                // opt-in: a suspended tab freezes its JS, and every tab is meant to stay live
                SuspendEnabled = Environment.GetEnvironmentVariable("BRAIN_SUSPEND") == "1",
                PrewarmEnabled = Environment.GetEnvironmentVariable("BRAIN_PREWARM") != "0",
                Log = Log,
                OnMessage = OnPageViewMessage,
                OnAudible = (id, on) => { if (Tabs.Find(id) is { } t) SetTabAudible(t, on); },
                OnNewTab = url => _ = NewTabAsync(url, default),
            };
            _winTabs = ws;

            var shared = new Win.WinTabs.EnvConfig(
                Path.Combine(_userDataDir, "webview"),
                PhotinoBrowserArgs($"--remote-debugging-port={CdpPort!.Value}"),
                CdpPort!.Value);
            var privPort = FindFreePort();
            var fallback = new Win.WinTabs.EnvConfig(
                Path.Combine(_userDataDir, "webview-tabs"),
                PhotinoBrowserArgs($"--remote-debugging-port={privPort}"),
                privPort);
            await ws.InitializeAsync(shared, fallback);
            Log("per-tab startup: environment ready (" + (ws.SharedEnvironment ? "shared" : "private") + "), connecting CDP on port " + ws.Port);

            _cdpBrowser = await Cdp.CdpClient.ConnectAsync(ws.Port, CancellationToken.None);
            Log("per-tab startup: CDP connected, creating first view");
            await EnsureTabViewAsync(first);
            ShowTabView(first);
            _driverTab = first;
            first.Bound.TrySetResult();
            _driverReady.TrySetResult();
            Log("driver ready: per-tab WebView2 views (" + (ws.SharedEnvironment ? "shared browser process" : "private environment") + ")");

            _ = Task.Run(async () =>
            {
                try { await NavigateAsync(first.LastUrl, first); Log("per-tab startup: first page loaded"); }
                catch (Exception ex) { Log("first load failed: " + ex.Message); }
                // diagnostics for "window stays blank" reports: where the views sit and what the page sees
                try
                {
                    await Task.Delay(2500);
                    Log("window layout: " + InvokeOnUi(() => Native.Win32.DescribeWindow(win.WindowHandle)));
                    var d = first.Driver;
                    if (d is not null)
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                        var r = await d.EvaluateAsync(
                            "JSON.stringify({w:innerWidth,h:innerHeight,rs:document.readyState,vis:document.visibilityState,chrome:!!window.__brainChrome,len:document.documentElement.outerHTML.length,title:document.title})",
                            false, cts.Token);
                        Log("first page: " + JsonToText(r));
                    }
                }
                catch (Exception ex) { Log("diagnostics failed: " + ex.Message); }
            });
            _ = Task.Delay(400).ContinueWith(_ =>
            {
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: false, compacting: true);
            }, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            Log("per-tab WebView2 views unavailable — all tabs share one view: " + ex);
            if (!_winTabsFailed) await FallbackToSharedViewAsync();
        }
    }

    /// <summary>Per-tab views could not be created: drop what exists and run the original
    /// single-webview model (the Photino window's own webview) instead.</summary>
    private async Task FallbackToSharedViewAsync()
    {
        if (_winTabsFailed) return; // already falling back (watchdog and the failure path can both arrive)
        try
        {
            _winTabsFailed = true;
            var ws = _winTabs;
            _winTabs = null;
            try { ws?.CloseAll(); } catch { }
            try { if (_cdpBrowser is not null) await _cdpBrowser.DisposeAsync(); } catch { }
            _cdpBrowser = null;
            _viewTasks.Clear();
            foreach (var t in Tabs.All) { t.Driver = null; t.WinView = null; t.Evicted = false; }
            var first = Tabs.All.FirstOrDefault(t => t.IsMain) ?? Tabs.All.First();
            _docTabId = first.Id;
            InvokeOnUi(() => PrepareLoad(_mainWindow!));
            await InitializeMainDriverAsync();
        }
        catch (Exception ex)
        {
            Log("shared-view fallback failed: " + ex);
            _driverReady.TrySetException(ex);
        }
    }

    private async Task<IBrowserDriver> CreateWinTabViewAsync(BrowserTab tab)
    {
        try
        {
            await _windowCreated.Task;
            if (!ReferenceEquals(tab, _firstTab)) await _driverReady.Task.WaitAsync(TimeSpan.FromSeconds(25));
            if (tab.IsClosed) throw new InvalidOperationException("Tab is closed");
            var ws = _winTabs ?? throw new InvalidOperationException("WebView2 views are not available");
            var cdp = _cdpBrowser ?? throw new InvalidOperationException("CDP is not connected");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            // unique per creation: a view rebuilt after eviction must never bind to its dying predecessor's target
            var marker = "braintab-" + tab.Id.ToString("N")[..8] + Guid.NewGuid().ToString("N")[..8];
            var view = await ws.CreateViewAsync(tab.Id, tab.IsPrivate, _zoom, marker);
            tab.BindMarker = view.Marker; // a prewarmed view already carries its own marker
            var viewMs = sw.ElapsedMilliseconds;
            if (tab.IsClosed) { ws.Destroy(view); throw new InvalidOperationException("Tab is closed"); }
            tab.WinView = view;
            var driver = new Cdp.CdpDriver(cdp, tab, this);
            tab.Driver = driver;
            WireDriverEvents(tab, driver);
            try { await driver.InitializeAsync(this, CancellationToken.None); }
            catch
            {
                tab.Driver = null;
                tab.WinView = null;
                try { await driver.DisposeAsync(); } catch { }
                ws.Destroy(view);
                throw;
            }
            tab.Bound.TrySetResult();
            ApplyTabMute(tab);
            Log($"tab {tab.Id.ToString("N")[..8]} view ready in {sw.ElapsedMilliseconds} ms (webview {viewMs} ms{(view.FromSpare ? ", prewarmed" : "")}, cdp {sw.ElapsedMilliseconds - viewMs} ms)");
            // keep one blank view ready for the next new tab, once things have settled
            _ = Task.Delay(1500).ContinueWith(_ => { try { ws.PrewarmSpare(_zoom); } catch { } }, TaskScheduler.Default);
            return driver;
        }
        catch
        {
            _viewTasks.TryRemove(tab.Id, out _);
            throw;
        }
    }

    private readonly ConcurrentDictionary<Guid, Task> _restoreTasks = new();

    /// <summary>An evicted tab got its view back (empty); load its page again and restore scroll.</summary>
    private Task RestoreIfNeededAsync(BrowserTab tab)
    {
        if (!tab.Evicted) return Task.CompletedTask;
        return _restoreTasks.GetOrAdd(tab.Id, _ => RestoreEvictedAsync(tab));
    }

    private async Task RestoreEvictedAsync(BrowserTab tab)
    {
        try
        {
            await EnsureTabViewAsync(tab);
            if (tab.IsClosed) return;
            tab.Evicted = false;
            var url = tab.LastUrl;
            if (MessageTrust.IsInternalUrl(url)) await LoadInternalPageAsync(tab, url);
            else if (!url.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
            {
                await NavigatePerTabAsync(tab, url, default);
                if (tab.ScrollX is { } sx && tab.Driver is { } d)
                {
                    try
                    {
                        await Task.Delay(220);
                        await d.EvaluateAsync($"window.scrollTo({sx},{tab.ScrollY ?? 0})", false, default);
                    }
                    catch { }
                }
            }
            PushChromeStateAll();
        }
        catch (Exception ex) { Log("restoring evicted tab failed: " + ex.Message); }
        finally { _restoreTasks.TryRemove(tab.Id, out _); }
    }

    /// <summary>A view that gets hidden never receives "mouse left": whatever the cursor was over in its
    /// in-page toolbar (usually the tab pill that was just clicked) would keep its hover/pressed look until
    /// the next mouse move over it. Move the pointer outside the page so the page clears that state.</summary>
    private async Task ClearHoverAsync(BrowserTab prev)
    {
        try
        {
            if (prev.IsClosed || prev.Driver is not { } d) return;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await d.MouseMoveAsync(-40, -40, cts.Token);
        }
        catch { /* page busy or gone — purely cosmetic */ }
    }

    private int _evictBusy;

    /// <summary>Keeps at most <see cref="MaxLiveViews"/> live views: the least recently shown
    /// ones go first. The shown tab, audible tabs, private tabs (their session can't be
    /// restored) and tabs mid-load are never evicted.</summary>
    private async Task EvictExcessViewsAsync()
    {
        if (!WinPerTab || Interlocked.Exchange(ref _evictBusy, 1) == 1) return;
        try
        {
            foreach (var t in Win.ViewPolicy.PickEvictionVictims(Tabs.All, Tabs.ActiveExact?.Id, MaxLiveViews))
                await EvictViewAsync(t);
        }
        catch (Exception ex) { Log("view eviction failed: " + ex.Message); }
        finally { Volatile.Write(ref _evictBusy, 0); }
    }

    private async Task EvictViewAsync(BrowserTab tab)
    {
        if (tab.WinView is not Win.WinTabs.View wv || tab.IsClosed) return;
        // URL, title and (live-reported) scroll position stay on the tab; the page itself goes.
        tab.Evicted = true;
        var drv = tab.Driver;
        tab.Driver = null;
        tab.WinView = null;
        tab.Loading = false;
        tab.IsAudible = false;
        _viewTasks.TryRemove(tab.Id, out _);
        try { if (drv is not null) await drv.DisposeAsync(); } catch { }
        try { _winTabs?.Destroy(wv); } catch { }
        Log($"evicted tab {tab.Id.ToString("N")[..8]} ({tab.LastUrl}) to cap live views at {MaxLiveViews} (BRAIN_MAXLIVE)");
        PushChromeStateAll();
    }

    /// <summary>Pushes one message to a tab's page view (macOS WKWebView or Windows WebView2).</summary>
    private void PushToView(BrowserTab tab, string json)
    {
        if (tab.WinView is Win.WinTabs.View wv) { _winTabs?.Push(wv, json); return; }
        var view = tab.NativeView;
        if (view != IntPtr.Zero && Mac.WkTabs.Has(view)) Mac.WkTabs.Push(view, json);
    }

    private async Task LoadHtmlWinAsync(BrowserTab tab, string html)
    {
        if (tab.WinView is not Win.WinTabs.View wv || _winTabs is null)
            throw new InvalidOperationException("Tab view is not available");
        await _winTabs.NavigateToStringAsync(wv, html);
        // NavigateToString documents never produce a stop-loading event: settle the spinner here.
        tab.Loading = false;
        try { await EnsurePageHelpersAsync(tab); } catch { }
        if (Tabs.Active?.Id == tab.Id) SetLoadBar(tab, false);
        PushChromeStateAll();
    }

    /// <summary>Gives a tab its own web view and driver (idempotent; concurrent
    /// callers share one creation).</summary>
    private Task<IBrowserDriver> EnsureTabViewAsync(BrowserTab tab)
    {
        if (tab.Driver is { } existing && tab.HasView) return Task.FromResult(existing);
        return _viewTasks.GetOrAdd(tab.Id, _ => WinPerTab ? CreateWinTabViewAsync(tab) : CreateTabViewAsync(tab));
    }

    private async Task<IBrowserDriver> CreateTabViewAsync(BrowserTab tab)
    {
        await _windowCreated.Task;
        if (_nsWindowNumber == 0 && !ReferenceEquals(tab, _firstTab))
            await _driverReady.Task.WaitAsync(TimeSpan.FromSeconds(25));
        if (tab.IsClosed) throw new InvalidOperationException("Tab is closed");
        var view = InvokeOnUi(() =>
        {
            var nsWin = Mac.MacWindow.WindowByNumber(_nsWindowNumber);
            return nsWin == IntPtr.Zero
                ? IntPtr.Zero
                : Mac.WkTabs.Create(nsWin, tab.Id, PageLibSource, tab.IsPrivate, _zoom);
        });
        if (view == IntPtr.Zero) throw new InvalidOperationException("Could not create a web view for the tab");
        tab.MacWindowNumber = _nsWindowNumber;
        tab.NativeView = view;
        var driver = new Drivers.WebKitDriver(tab, this);
        tab.Driver = driver;
        WireDriverEvents(tab, driver);
        await driver.InitializeAsync(this, CancellationToken.None);
        tab.Bound.TrySetResult();
        if (tab.IsMuted) driver.SetPageMuted(true);
        return driver;
    }

    /// <summary>Makes the tab's view the visible one. Focus only moves if it was
    /// sitting in the view being hidden (so a focused address bar stays focused).</summary>
    private void ShowTabView(BrowserTab tab)
    {
        if (!PerTab || !tab.HasView) return;
        tab.LastActiveUtc = DateTime.UtcNow;
        if (tab.WinView is Win.WinTabs.View wv)
            _winTabs?.Show(wv);
        else
        {
            var view = tab.NativeView;
            InvokeOnUi(() =>
            {
                var nsWin = Mac.MacWindow.WindowByNumber(_nsWindowNumber);
                Mac.WkTabs.Show(nsWin, view);
            });
        }
        // the toolbar must repaint for the newly shown page (dedupe would skip it)
        _lastPushedStateJson = null;
    }

    private async Task DestroyTabViewAsync(BrowserTab tab)
    {
        tab.IsClosed = true;
        var drv = tab.Driver;
        var view = tab.NativeView;
        var winView = tab.WinView;
        tab.Driver = null;
        tab.NativeView = IntPtr.Zero;
        tab.WinView = null;
        _viewTasks.TryRemove(tab.Id, out _);
        try { if (drv is not null) await drv.DisposeAsync(); } catch { }
        if (winView is Win.WinTabs.View wv)
            try { _winTabs?.Destroy(wv); } catch { }
        if (view != IntPtr.Zero)
            try { InvokeOnUi(() => Mac.WkTabs.Destroy(view)); } catch { }
    }

    /// <summary>A message from one tab's page view. Eval replies must always complete,
    /// even for a tab that has just been closed; anything else from a dead tab is dropped.</summary>
    private void OnPageViewMessage(Guid tabId, string message)
    {
        var t = Tabs.Find(tabId);
        if (t is null || t.IsClosed)
        {
            if (message.StartsWith("{\"b\":1,", StringComparison.Ordinal) && _firstTab is { } f)
                HandleWebMessage(f, message);
            return;
        }
        HandleWebMessage(t, message);
    }

    /// <summary>The tab whose page view the toolbar-level pushes (state, downloads,
    /// agent status) should reach.</summary>
    private BrowserTab? UiTab() => PerTab
        ? (Tabs.Active ?? Tabs.All.FirstOrDefault())
        : (Tabs.Main ?? Tabs.All.FirstOrDefault());

    private async Task<IBrowserDriver> CreateDriverAsync(BrowserTab tab)
    {
        if (IsWindows)
        {
            _cdpBrowser ??= await Cdp.CdpClient.ConnectAsync(CdpPort!.Value, CancellationToken.None);
            return new Cdp.CdpDriver(_cdpBrowser, tab, this);
        }
        if (IsMac)
            return new Drivers.WebKitDriver(tab, this);
        return new StubDriver(tab, this);
    }

    private void WireDriverEvents(BrowserTab tab, IBrowserDriver driver)
    {
        driver.ConsoleMessage += (_, e) => Enqueue(_console, e);
        driver.NetworkEvent += (_, e) =>
        {
            _network.AddOrUpdate(e.RequestId, e, (rid, old) => MergeNetwork(old, e));
            // trim oldest when the map grows past the cap
            if (_network.Count > LogCap * 2)
                foreach (var k in _network.Values.OrderBy(n => n.Time).Take(LogCap).Select(n => n.RequestId).ToList())
                    _network.TryRemove(k, out var _);
        };
        driver.LoadStarted += (_, _) =>
        {
            if (PerTab)
            {
                // each tab tracks its own load; only the shown tab drives the spinner / bar
                tab.Loading = true;
                // a document is being created (link click, back/forward, redirect): refresh its boot payload
                if (WinPerTab) _ = driver.SetBootStateAsync(BuildChromeStateJson(null, "inj"), CancellationToken.None);
                if (Tabs.Active?.Id == tab.Id)
                {
                    PushChromeStateAll();
                    _ = Task.Delay(160).ContinueWith(_ =>
                    {
                        if (tab.Loading && Tabs.Active?.Id == tab.Id) SetLoadBar(tab, true);
                    }, TaskScheduler.Default);
                }
                return;
            }
            Interlocked.Increment(ref _loadDepth);
            PushChromeStateAll();
            _ = Task.Delay(160).ContinueWith(_ => { if (IsLoading) SetLoadBar(tab, true); }, TaskScheduler.Default);
            // A document is being created — refresh the boot payload it will
            // carry (covers back/forward and page-initiated navigations where
            // NavigateAsync isn't on the path).
            _ = driver.SetBootStateAsync(BuildChromeStateJson(null, "inj"), CancellationToken.None);
        };
        driver.LoadCompleted += async (_, _) =>
        {
            if (PerTab)
            {
                tab.Loading = false;
                if (Tabs.Active?.Id == tab.Id) SetLoadBar(tab, false);
                if (tab.IsClosed) return;
                try
                {
                    await RefreshTabInfoAsync(tab);
                    await EnsurePageHelpersAsync(tab);
                    _ = BlankWatchdogAsync(tab);
                }
                finally
                {
                    // the spinner must clear even when the info refresh was dropped mid-navigation
                    if (Tabs.Active?.Id == tab.Id) PushChromeStateAll();
                }
                return;
            }
            if (Interlocked.Decrement(ref _loadDepth) < 0)
                Interlocked.Exchange(ref _loadDepth, 0);
            if (!IsLoading) SetLoadBar(tab, false);
            // Refresh the tab that OWNS the document — with a new-tab overlay
            // up, the active tab is virtual and must not inherit the hidden
            // page's URL/title.
            var target = DocTab ?? Tabs.Active ?? tab;
            await RefreshTabInfoAsync(target);
            await EnsurePageHelpersAsync(target);
            _ = BlankWatchdogAsync(target);
            // Logical tabs reload on switch — restore the scroll position the
            // page reported while it was visible (also restores after reload).
            if (target.ScrollX is { } sx)
            {
                try
                {
                    await Task.Delay(220);
                    await target.Driver!.EvaluateAsync(
                        $"window.scrollTo({sx},{target.ScrollY ?? 0})", false, default);
                }
                catch { }
            }
        };
    }

    // ------------------------------------------------------------------
    // Public façade used by MCP tools
    // ------------------------------------------------------------------

    /// <summary>Evaluates a JS expression in the active (or given) tab.</summary>
    public async Task<JsonElement?> EvaluateAsync(
        string expression, bool awaitPromise = true, BrowserTab? tab = null, CancellationToken ct = default)
    {
        tab ??= ActiveTabOrThrow();
        var driver = await DriverOrThrowAsync(tab, ct);
        return await driver.EvaluateAsync(expression, awaitPromise, ct);
    }

    /// <summary>Evaluates a full JS program (statements + 'return'). Use for
    /// helper injection and user-typed scripts; expression callers should
    /// use EvaluateAsync.</summary>
    public async Task<JsonElement?> EvalScriptAsync(
        string script, bool awaitPromise = true, BrowserTab? tab = null, CancellationToken ct = default)
    {
        tab ??= ActiveTabOrThrow();
        var driver = await DriverOrThrowAsync(tab, ct);
        return await driver.EvalScriptAsync(script, awaitPromise, ct);
    }

    /// <summary>Evaluates a JS expression and converts the result to T.</summary>
    public async Task<T?> EvaluateAsync<T>(
        string expression, bool awaitPromise = true, BrowserTab? tab = null, CancellationToken ct = default)
    {
        var el = await EvaluateAsync(expression, awaitPromise, tab, ct);
        if (el is null || el.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return default;
        return el.Value.Deserialize<T>(JsonOpts);
    }

    /// <summary>Evaluates and returns the result as display text.</summary>
    public async Task<string?> EvaluateTextAsync(
        string expression, BrowserTab? tab = null, CancellationToken ct = default)
    {
        var el = await EvaluateAsync(expression, true, tab, ct);
        return JsonToText(el);
    }

    public async Task<NavigateResult> NavigateAsync(string url, BrowserTab? tab = null, CancellationToken ct = default)
    {
        tab ??= ActiveTabOrThrow();
        // Shared-view mode: navigating a tab implies making it the shown/active one
        // (there is only one view). Per-tab mode navigates the tab's own view in place,
        // so an agent can drive a background tab without stealing the screen.
        if (!PerTab && Tabs.Active?.Id != tab.Id) Tabs.Activate(tab.Id);
        url = NormalizeUrl(url);
        if (url is "about:newtab" or "about:help" or "about:shortcuts" or "about:history" or "about:import" or "about:passwords" or "about:flows")
            return await LoadInternalPageAsync(tab, url);
        if (PerTab) return await NavigatePerTabAsync(tab, url, ct);
        // materialize:false — a virtual new tab navigating away must not
        // first load the start-page document it's about to replace.
        var driver = await DriverOrThrowAsync(tab, ct, materialize: false);
        MarkStaleDoc(tab.Id, url);
        _docTabId = tab.Id;
        // Record where this tab is going right away. If another tab takes over the
        // web view before this page commits, nothing else would ever tell this tab
        // its address — it would come back blank.
        if (SiteKey(url) != SiteKey(tab.LastUrl)) tab.LastTitle = null;
        SetTabUrl(tab, url);
        // Bake chrome state into the next document so the toolbar renders
        // final tab state on first paint (no empty-strip flash).
        try { await driver.SetBootStateAsync(BuildChromeStateJson(ConsumeFresh(), "inj"), ct); } catch { }
        var result = await driver.NavigateAsync(url, ct);
        // Only the tab that still owns the document may take the result: another
        // tab may have started its own navigation meanwhile.
        if (_docTabId == tab.Id)
        {
            ClearStaleDoc();
            _inflightNavSite = null;
            if (result.Ok) SetTabUrl(tab, result.Url);
        }
        return result;
    }

    /// <summary>Per-tab navigation: the tab's own view loads the URL; nothing else is touched.</summary>
    private async Task<NavigateResult> NavigatePerTabAsync(BrowserTab tab, string url, CancellationToken ct)
    {
        var driver = await EnsureTabViewAsync(tab);
        // Record where this tab is going right away so the strip shows it immediately.
        if (SiteKey(url) != SiteKey(tab.LastUrl)) tab.LastTitle = null;
        SetTabUrl(tab, url);
        PushChromeStateAll();
        // Bake chrome state into the next document so the toolbar paints final state on first paint.
        if (WinPerTab) try { await driver.SetBootStateAsync(BuildChromeStateJson(ConsumeFresh(), "inj"), ct); } catch { }
        var result = await driver.NavigateAsync(url, ct);
        if (!tab.IsClosed && result.Ok && result.Url.Length > 0 && result.Url != "about:blank")
            SetTabUrl(tab, result.Url);
        return result;
    }

    /// <summary>Loads an internal about: page as a real document.</summary>
    private async Task<NavigateResult> LoadInternalPageAsync(BrowserTab tab, string url)
    {
        if (PerTab) return await LoadInternalPagePerTabAsync(tab, url);
        MarkStaleDoc(tab.Id, null);
        _docTabId = tab.Id;
        SetTabUrl(tab, url);
        var (page, title) = url switch
        {
            "about:help" => (Assets.HelpHtml, "How to use"),
            "about:shortcuts" => (Assets.ShortcutsHtml, "Keyboard shortcuts"),
            "about:history" => (BuildHistoryHtml(), "History"),
            "about:import" => (Assets.ImportHtml, "Import browser data"),
            "about:passwords" => (BuildPasswordsHtml(tab.IsPrivate), "Passwords"),
            "about:flows" => (BuildFlowsHtml(), "Flows & activity"),
            _ => (Assets.NewTabHtml, "New Tab"),
        };
        tab.LastTitle = title;
        var html = InjectBootState(page);
        InvokeOnUi(() => tab.Window.LoadRawString(html));
        try { await EnsurePageHelpersAsync(tab); } catch { }
        ClearStaleDoc(); // the raw-string document replaced the previous page
        _inflightNavSite = null;
        // NavigateToString docs never emit frameStoppedLoading — settle
        // the loading depth here or the spinner sticks forever.
        Interlocked.Exchange(ref _loadDepth, 0);
        PushChromeStateAll();
        return NavigateResult.Success(url, tab.LastTitle);
    }

    private async Task<NavigateResult> LoadInternalPagePerTabAsync(BrowserTab tab, string url)
    {
        var driver = await EnsureTabViewAsync(tab);
        var (page, title) = url switch
        {
            "about:help" => (Assets.HelpHtml, "How to use"),
            "about:shortcuts" => (Assets.ShortcutsHtml, "Keyboard shortcuts"),
            "about:history" => (BuildHistoryHtml(), "History"),
            "about:import" => (Assets.ImportHtml, "Import browser data"),
            "about:passwords" => (BuildPasswordsHtml(tab.IsPrivate), "Passwords"),
            "about:flows" => (BuildFlowsHtml(), "Flows & activity"),
            _ => (Assets.NewTabHtml, "New Tab"),
        };
        SetTabUrl(tab, url);
        tab.LastTitle = title;
        PushChromeStateAll();
        if (driver is Drivers.WebKitDriver wk) await wk.LoadHtmlAsync(InjectBootState(page), default);
        else await LoadHtmlWinAsync(tab, InjectBootState(page));
        return NavigateResult.Success(url, tab.LastTitle);
    }

    /// <summary>Toolbar screenshot: capture the tab (whole scrolling page or just what is
    /// visible), save it as a PNG in Downloads and list it on the downloads shelf.</summary>
    private async Task SaveScreenshotAsync(BrowserTab? tab, bool fullPage)
    {
        tab ??= Tabs.Active;
        if (tab is null) return;
        try
        {
            var png = await ScreenshotAsync(null, fullPage, tab, CancellationToken.None);
            var item = Downloads.Begin(tab.LastUrl);
            var path = Downloads.DestinationFor(item, $"Screenshot {DateTime.Now:yyyy-MM-dd 'at' HH.mm.ss}.png");
            await File.WriteAllBytesAsync(path, png);
            Downloads.Finish(item, path);
            Log($"screenshot saved: {path} ({png.Length / 1024} KB, {(fullPage ? "full page" : "visible")})");
        }
        catch (Exception ex) { Log($"screenshot failed: {ex.Message}"); }
    }

    /// <summary>Can a blank new tab be shown as an in-document overlay right
    /// now? Needs a live document owner with the chrome bar mounted (error
    /// pages / PDF viewers don't run our injection).</summary>
    private async Task<bool> CanOverlayAsync()
    {
        if (_noToolbar || _docTabId is null || DocTab is null) return false;
        var d = _driverTab?.Driver;
        if (d is null || !d.SupportsEval) return false;
        try
        {
            var r = await d.EvaluateAsync(
                "!!(window.__brainChrome && document.getElementById('__brain-chrome-host'))",
                false, CancellationToken.None);
            return r?.ValueKind == JsonValueKind.True;
        }
        catch { return false; }
    }

    private void SetWindowTitleFor(BrowserTab tab)
    {
        var title = string.IsNullOrWhiteSpace(tab.LastTitle) ? "BrAIn" : tab.LastTitle + " — BrAIn";
        try { InvokeOnUi(() => { if (!tab.IsClosed) tab.Window.Title = title; }); } catch { }
    }

    /// <summary>Photino-level navigation (works even without a driver).</summary>
    public void NavigateRaw(PhotinoWindow window, string url)
        => window.Invoke(() => window.Load(new Uri(NormalizeUrl(url))));

    public async Task<NavigateResult> GoBackAsync(BrowserTab? tab = null, CancellationToken ct = default)
        => await (await DriverOrThrowAsync(tab ??= ActiveTabOrThrow(), ct)).GoBackAsync(ct);

    public async Task<NavigateResult> GoForwardAsync(BrowserTab? tab = null, CancellationToken ct = default)
        => await (await DriverOrThrowAsync(tab ??= ActiveTabOrThrow(), ct)).GoForwardAsync(ct);

    public async Task<NavigateResult> ReloadAsync(bool ignoreCache = false, BrowserTab? tab = null, CancellationToken ct = default)
    {
        tab ??= ActiveTabOrThrow();
        // Internal about: pages render via LoadRawString, so their location is
        // about:blank — a webview reload would just blank them. Re-render instead.
        if (MessageTrust.IsInternalUrl(tab.LastUrl))
            return await LoadInternalPageAsync(tab, tab.LastUrl!);
        return await (await DriverOrThrowAsync(tab, ct)).ReloadAsync(ignoreCache, ct);
    }

    public async Task<byte[]> ScreenshotAsync(RectD? clip, bool fullPage, BrowserTab? tab = null, CancellationToken ct = default)
    {
        var t = tab ??= ActiveTabOrThrow();
        var driver = await DriverOrThrowAsync(t, ct);
        // A screenshot captures the window, so a background tab must be brought forward first.
        if (PerTab && Tabs.Active?.Id != t.Id) await ActivateWindowAsync(t.Id.ToString("N")[..8], ct);
        // Keep the injected chrome bar out of captured pixels.
        try { await driver.EvaluateAsync("window.__brainChrome && window.__brainChrome.hide()", false, ct); }
        catch { }
        try
        {
            return await driver.ScreenshotAsync(clip, fullPage, ct);
        }
        finally
        {
            try { await driver.EvaluateAsync("window.__brainChrome && window.__brainChrome.show()", false, ct); }
            catch { }
        }
    }

    /// <summary>Combine per-phase CDP events into one row: later fields win
    /// only when they carry data (status set, bytes counted, headers seen).</summary>
    private static NetworkEntry MergeNetwork(NetworkEntry old, NetworkEntry next) =>
        old with
        {
            Url = next.Url.Length > 0 ? next.Url : old.Url,
            Status = next.Status ?? old.Status,
            ResourceType = next.ResourceType ?? old.ResourceType,
            BytesReceived = next.BytesReceived > 0 ? next.BytesReceived : old.BytesReceived,
            Error = next.Error ?? old.Error,
            RequestHeaders = next.RequestHeaders ?? old.RequestHeaders,
            ResponseHeaders = next.ResponseHeaders ?? old.ResponseHeaders,
            PostData = next.PostData ?? old.PostData,
        };

    public IReadOnlyList<ConsoleEntry> ConsoleLog => _console.ToArray();
    public IReadOnlyList<NetworkEntry> NetworkLog =>
        _network.Values.OrderBy(n => n.Time).ToArray();
    public void ClearConsoleLog() => _console.Clear();
    public void ClearNetworkLog() => _network.Clear();

    // ---------------- tabs / windows ----------------

    /// <summary>Opens a new logical tab in the (single) browser window and
    /// navigates it to <paramref name="url"/>. Private tabs leave no history,
    /// never join the session/reopen state and wipe site data on close.</summary>
    public async Task<BrowserTab> NewTabAsync(string? url, CancellationToken ct = default, bool priv = false)
    {
        await _windowCreated.Task.WaitAsync(ct);
        var main = _mainWindow ?? throw new InvalidOperationException("Browser window not initialized");
        var tab = new BrowserTab { Id = Guid.NewGuid(), IsMain = false, Window = main, IsPrivate = priv };
        if (PerTab) return await NewTabPerTabAsync(tab, url, ct);
        var overlay = url is null && await CanOverlayAsync();
        Tabs.Add(tab);
        Tabs.Activate(tab.Id);
        if (overlay)
        {
            // Blank tab: no document load — chrome.js paints the start page
            // over the current document (instant; no renderer cold start).
            SetTabUrl(tab, "about:newtab");
            tab.LastTitle = "New Tab";
            SetWindowTitleFor(tab);
        }
        else
        {
            _pendingFresh = tab.Id.ToString("N")[..8]; // consumed by boot state
            await NavigateAsync(url ?? "newtab", tab, ct);
        }
        PushChromeStateAll();
        SaveSessionSoon();
        return tab;
    }

    private async Task<BrowserTab> NewTabPerTabAsync(BrowserTab tab, string? url, CancellationToken ct)
    {
        Tabs.Add(tab);
        Tabs.Activate(tab.Id);
        SetTabUrl(tab, url is null ? "about:newtab" : "about:blank");
        tab.LastTitle = url is null ? "New Tab" : null;
        _pendingFresh = tab.Id.ToString("N")[..8]; // only this pill plays the slide-in
        PushChromeStateAll();                       // the new pill appears at once
        try
        {
            await EnsureTabViewAsync(tab);
            if (Tabs.Active?.Id == tab.Id) ShowTabView(tab); // an empty view in the window colour while it loads
            SetWindowTitleFor(tab);
            await NavigateAsync(url ?? "newtab", tab, ct);
            if (url is not null) ConsumeFresh(); // only internal pages carry it in their boot state
            if (WinPerTab) _ = EvictExcessViewsAsync();
        }
        catch
        {
            // couldn't build the view: don't leave a dead pill behind
            Tabs.Remove(tab.Id);
            _rw.RemoveTab(tab.Id); // a closed tab takes its Rewind history with it
            await DestroyTabViewAsync(tab);
            if (Tabs.Active is { } a) await ActivateWindowAsync(a.Id.ToString("N")[..8], default);
            PushChromeStateAll();
            throw;
        }
        PushChromeStateAll();
        SaveSessionSoon();
        return tab;
    }

    /// <summary>Activates a tab and navigates the window to its URL.
    /// Already-active tabs are a no-op — clicking the current tab must not
    /// reload the page.</summary>
    public async Task<bool> ActivateWindowAsync(string idOrPrefix, CancellationToken ct = default)
    {
        var tab = Tabs.FindByPrefix(idOrPrefix) ?? throw new InvalidOperationException($"No such tab '{idOrPrefix}'");
        if (tab.IsClosed) throw new InvalidOperationException("Tab is closed");
        if (PerTab)
        {
            if (Tabs.ActiveExact?.Id != tab.Id)
            {
                var prev = Tabs.ActiveExact;
                var swSw = System.Diagnostics.Stopwatch.StartNew();
                Tabs.Activate(tab.Id);
                await EnsureTabViewAsync(tab);
                var ensureMs = swSw.ElapsedMilliseconds;
                // Windows: let the target page's own toolbar take the new state while it is still hidden,
                // so the reveal shows a finished toolbar instead of a stale one that then re-renders
                if (WinPerTab) { _lastPushedStateJson = null; PushChromeStateAll(); }
                ShowTabView(tab);
                if (WinPerTab) Log($"switch to {tab.Id.ToString("N")[..8]}: ensure {ensureMs} ms, show {swSw.ElapsedMilliseconds - ensureMs} ms");
                SetWindowTitleFor(tab);
                _ = RefreshNavStateAsync(tab);
                _ = RaiseWindowAsync(tab, ct);
                if (WinPerTab)
                {
                    if (prev is not null) _ = ClearHoverAsync(prev);
                    _ = RestoreIfNeededAsync(tab); // evicted tab: the view is up (empty), load its page now
                    _ = EvictExcessViewsAsync();
                }
            }
            PushChromeStateAll();
            return true;
        }
        if (Tabs.ActiveExact?.Id != tab.Id)
        {
            Tabs.Activate(tab.Id);
            await RaiseWindowAsync(tab, ct);
            if (tab.Id == _docTabId)
                SetWindowTitleFor(tab); // its document is still loaded under the overlay — instant
            else if (tab.LastUrl == "about:newtab" && await CanOverlayAsync())
                SetWindowTitleFor(tab); // blank tab → overlay, no document load
            else
                await NavigateAsync(tab.LastUrl, tab, ct);
        }
        PushChromeStateAll();
        return true;
    }

    /// <summary>Re-reads a tab's back/forward availability natively (no JS) and repaints.</summary>
    private async Task RefreshNavStateAsync(BrowserTab tab)
    {
        try
        {
            var d = tab.Driver;
            if (d is null) return;
            (tab.CanBack, tab.CanFwd) = await d.GetNavStateAsync(CancellationToken.None);
            if (Tabs.Active?.Id == tab.Id) PushChromeStateAll();
        }
        catch { }
    }

    public async Task<bool> CloseWindowAsync(string idOrPrefix, CancellationToken ct = default)
    {
        var tab = Tabs.FindByPrefix(idOrPrefix) ?? throw new InvalidOperationException($"No such tab '{idOrPrefix}'");
        if (tab.IsPinned) return false; // pinned tabs resist close — unpin first
        var wasActive = Tabs.Active?.Id == tab.Id;
        var idx = Tabs.IndexOf(tab.Id);
        if (PerTab) return await ClosePerTabAsync(tab, wasActive, idx, ct);
        if (tab.IsPrivate)
        {
            // Never resurface private tabs via reopen — and wipe their site
            // data so cookies/logins from the private session don't leak.
            if (tab.LastUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                _ = ClearPrivateSiteDataAsync(tab.LastUrl);
        }
        else if (tab.LastUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            _closedTabs.Push((tab.LastUrl, tab.LastIcon));
        Tabs.Remove(tab.Id);
        _rw.RemoveTab(tab.Id); // a closed tab takes its Rewind history with it
        // The closed tab owned the live document — it must not linger hidden
        // under an overlay (audio, private data), so the next view reloads.
        var closedDoc = tab.Id == _docTabId;
        if (closedDoc) _docTabId = null;
        SaveSessionSoon();
        var remaining = Tabs.All;
        if (remaining.Count == 0)
        {
            // Closing the last tab closes the browser, like a normal browser.
            try { InvokeOnUi(() => _mainWindow?.Close()); } catch { }
            return true;
        }
        if (wasActive)
        {
            // Activate the tab that slid into the closed slot (right neighbor),
            // or the new last tab when closing the rightmost one. No push
            // before this: an intermediate state would show tab 0 as active
            // (registry fallback) and drop the new-tab overlay for a frame.
            var next = remaining[Math.Clamp(idx, 0, remaining.Count - 1)];
            await ActivateWindowAsync(next.Id.ToString("N")[..8], ct); // pushes once
        }
        else
        {
            PushChromeStateAll();
            if (closedDoc && Tabs.Active is { } act && act.LastUrl == "about:newtab")
                await LoadInternalPageAsync(act, "about:newtab"); // unload the hidden page
        }
        return true;
    }

    private async Task<bool> ClosePerTabAsync(BrowserTab tab, bool wasActive, int idx, CancellationToken ct)
    {
        // Private tabs keep their data in an ephemeral store that dies with the tab.
        if (!tab.IsPrivate && tab.LastUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            _closedTabs.Push((tab.LastUrl, tab.LastIcon));
        Tabs.Remove(tab.Id);
        _rw.RemoveTab(tab.Id); // a closed tab takes its Rewind history with it
        SaveSessionSoon();
        var remaining = Tabs.All;
        if (remaining.Count == 0)
        {
            // Closing the last tab closes the browser, like a normal browser.
            await DestroyTabViewAsync(tab);
            try { InvokeOnUi(() => _mainWindow?.Close()); } catch { }
            return true;
        }
        if (wasActive)
        {
            // The neighbour that slid into the closed slot (or the new last tab) is shown
            // BEFORE the old view is torn down, so the window never goes blank.
            var next = remaining[Math.Clamp(idx, 0, remaining.Count - 1)];
            await ActivateWindowAsync(next.Id.ToString("N")[..8], ct);
        }
        else PushChromeStateAll();
        await DestroyTabViewAsync(tab);
        return true;
    }

    /// <summary>Best-effort origin wipe after a private tab closes.</summary>
    private async Task ClearPrivateSiteDataAsync(string url)
    {
        try
        {
            var origin = new Uri(url).GetLeftPart(UriPartial.Authority);
            var d = _driverTab?.Driver;
            if (d is not null) await d.ClearSiteDataAsync(origin, CancellationToken.None);
        }
        catch { /* profile teardown in flight — ignore */ }
    }

    /// <summary>Closes every window — called when the MCP host stops (stdio
    /// client disconnected) so the process exits instead of leaving a zombie
    /// window. Safe to call from any thread.</summary>
    public void CloseAllWindows()
    {
        try
        {
            InvokeOnUi(() =>
            {
                foreach (var tab in Tabs.All)
                {
                    try { tab.Window.Close(); } catch { }
                }
            });
        }
        catch
        {
            // UI thread may already be gone during shutdown.
        }
    }

    /// <summary>Bring a tab's OS window to the foreground.</summary>
    public async Task RaiseWindowAsync(BrowserTab tab, CancellationToken ct = default)
    {
        if (IsWindows)
        {
            var hwnd = tab.Window.WindowHandle;
            if (hwnd != IntPtr.Zero)
                await Task.Run(() => Native.Win32.Raise(hwnd), ct);
        }
        else if (IsMac)
        {
            // AppKit window ordering is main-thread only — off-thread it can
            // spin forever inside WindowManagement when the app isn't frontmost.
            await Task.Run(() => InvokeOnUi(() => Mac.MacWindow.Activate(tab)), ct);
        }
    }

    // ---------------- window chrome ops ----------------

    public void SetWindowSize(BrowserTab tab, int width, int height)
        => tab.Window.Invoke(() => { tab.Window.Width = width; tab.Window.Height = height; });

    public void SetWindowState(BrowserTab tab, string state)
        => tab.Window.Invoke(() =>
        {
            if (IsMac)
            {
                var nsWin = Mac.MacWindow.NsWindowForTab(tab);
                switch (state.ToLowerInvariant())
                {
                    case "maximized": tab.Window.Maximized = true; return;
                    case "minimized": tab.Window.Minimized = true; return;
                    case "fullscreen":
                        if (nsWin == IntPtr.Zero) return;
                        Mac.MacWindow.ExitLegacyFullScreen(nsWin);
                        if (!Mac.MacWindow.IsFullScreen(nsWin)) Mac.MacWindow.ToggleFullScreen(nsWin);
                        return;
                    case "normal":
                        tab.Window.Minimized = false;
                        tab.Window.Maximized = false;
                        if (nsWin != IntPtr.Zero)
                        {
                            Mac.MacWindow.ExitLegacyFullScreen(nsWin);
                            if (Mac.MacWindow.IsFullScreen(nsWin)) Mac.MacWindow.ToggleFullScreen(nsWin);
                            Mac.MacWindow.RepairWebViewFrame(nsWin);
                        }
                        return;
                    default: throw new ArgumentException($"Unknown state '{state}'");
                }
            }
            switch (state.ToLowerInvariant())
            {
                case "maximized": tab.Window.Maximized = true; break;
                case "minimized": tab.Window.Minimized = true; break;
                case "fullscreen": tab.Window.FullScreen = true; break;
                case "normal":
                    tab.Window.Minimized = false;
                    tab.Window.Maximized = false;
                    tab.Window.FullScreen = false;
                    break;
                default: throw new ArgumentException($"Unknown state '{state}'");
            }
        });

    public void SetZoom(BrowserTab tab, int percent)
    {
        if (PerTab)
        {
            if (tab.WinView is Win.WinTabs.View wv) { _winTabs?.SetZoom(wv, percent); return; }
            var v = tab.NativeView;
            if (v != IntPtr.Zero) tab.Window.Invoke(() => { if (Mac.WkTabs.Has(v)) Mac.WkTabs.SetZoom(v, percent); });
            return;
        }
        tab.Window.Invoke(() => tab.Window.Zoom = percent);
    }

    /// <summary>Step through the standard browser zoom ladder.</summary>
    private void ZoomBy(int dir)
    {
        var i = 0; var best = int.MaxValue;
        for (var k = 0; k < ZoomSteps.Length; k++)
        {
            var d = Math.Abs(ZoomSteps[k] - _zoom);
            if (d < best) { best = d; i = k; }
        }
        SetZoomLevel(ZoomSteps[Math.Clamp(i + dir, 0, ZoomSteps.Length - 1)]);
    }

    private void SetZoomLevel(int pct)
    {
        _zoom = pct;
        try
        {
            InvokeOnUi(() =>
            {
                if (SplitOn) Mac.WkTabs.SetZoomAll(pct);
                else if (WinPerTab) _winTabs?.SetZoomAll(pct);
                else if (_mainWindow is not null) _mainWindow.Zoom = pct;
            });
        }
        catch { }
        PushChromeStateAll();
    }

    private void ToggleBookmark(BrowserTab tab)
    {
        if (tab.IsPrivate) return; // bookmarking a private tab defeats the point
        if (!tab.LastUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return;
        lock (_bookmarks)
        {
            var i = _bookmarks.FindIndex(b => b.Url == tab.LastUrl);
            if (i >= 0) _bookmarks.RemoveAt(i);
            else _bookmarks.Insert(0, (tab.LastUrl, tab.LastTitle, tab.LastIcon));
            if (_bookmarks.Count > BookmarkCap) _bookmarks.RemoveAt(BookmarkCap);
            SaveStore("bookmarks.json",
                _bookmarks.Select(b => new { u = b.Url, t = b.Title, i = b.Icon }));
        }
        PushChromeStateAll();
    }

    /// <summary>Per-tab scroll position, reported live by chrome.js.
    /// arg: "&lt;page-url&gt;|&lt;x&gt;,&lt;y&gt;" — the url tag guards against a
    /// scroll event from the previous document landing after a tab switch.</summary>
    private void RecordScroll(BrowserTab tab, string arg)
    {
        var bar = arg.IndexOf('|');
        if (bar <= 0) return;
        var url = arg[..bar];
        var same = url.Equals(tab.LastUrl, StringComparison.OrdinalIgnoreCase)
            // internal pages are served via LoadRawString and report about:blank
            // while being stored under their logical about:* URL
            || (url == "about:blank" && tab.LastUrl.StartsWith("about:", StringComparison.Ordinal));
        if (!same) return;
        var xy = arg[(bar + 1)..].Split(',');
        if (xy.Length == 2 &&
            int.TryParse(xy[0], out var x) && int.TryParse(xy[1], out var y))
        {
            tab.ScrollX = x;
            tab.ScrollY = y;
        }
    }

    public void SetTitle(BrowserTab tab, string title) => tab.Window.Invoke(() => tab.Window.Title = title);

    public (int W, int H) GetWindowSize(BrowserTab tab)
    {
        var s = tab.Window.Size;
        return (s.Width, s.Height);
    }

    // ------------------------------------------------------------------
    // JS → host message channel (window.external.sendMessage)
    // ------------------------------------------------------------------

    internal Task<JsonElement> RegisterPendingEval(string id, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingEval[id] = tcs;
        ct.Register(() => _pendingEval.TryRemove(id, out _));
        return tcs.Task;
    }

    /// <summary>Host → UI push: the page view, and — in split mode — the bar view too.</summary>
    private void SendUi(BrowserTab tab, string json)
    {
        if (SplitOn && (json.StartsWith("{\"b\":47,", StringComparison.Ordinal) || json.StartsWith("{\"b\":49,", StringComparison.Ordinal)))
        {
            // Approval ids go to the toolbar view only: no web page runs there. If it is not up yet, nothing is sent (the ask times out as a no).
            if (Mac.WkBar.Exists)
                try { Mac.WkBar.Eval("window.__brainBarPush&&window.__brainBarPush(" + json + ")"); } catch { }
            return;
        }
        if (PerTab)
        {
            // the tab's own page view, plus the toolbar view when it cares about this push
            PushToView(tab, json);
            if (SplitOn && Mac.WkBar.Exists && !PageOnlyPush(json))
                try { Mac.WkBar.Eval("window.__brainBarPush&&window.__brainBarPush(" + json + ")"); } catch { }
            return;
        }
        tab.Window.SendWebMessage(json);
        if (SplitOn && Mac.WkBar.Exists && !PageOnlyPush(json))
            try { Mac.WkBar.Eval("window.__brainBarPush&&window.__brainBarPush(" + json + ")"); } catch { }
    }

    /// <summary>Pushes only the page view consumes (DevTools data, field/password
    /// helpers) — skipped for the bar view so big payloads aren't parsed twice.</summary>
    private static bool PageOnlyPush(string json)
    {
        const string p = "{\"b\":";
        if (!json.StartsWith(p, StringComparison.Ordinal)) return false;
        var i = p.Length; var n = 0;
        while (i < json.Length && char.IsDigit(json[i]) && n < 1000) n = n * 10 + (json[i++] - '0');
        return n is >= 10 and <= 15 or >= 22 and <= 26 or >= 70 and <= 79;
    }

    /// <summary>Starts the split-toolbar bar view (main thread).</summary>
    private void StartSplitBar(BrowserTab tab)
    {
        var nsWin = Mac.MacWindow.WindowByNumber(tab.MacWindowNumber);
        if (nsWin == IntPtr.Zero) return;
        const string shim =
            "window.__BRAIN_ROLE='bar';" +
            "window.external={sendMessage:function(m){try{window.webkit.messageHandlers.brain.postMessage(String(m));}catch(e){}}," +
            "receiveMessage:function(cb){window.__brainBarCb=cb;var q=window.__brainBarQ||[];window.__brainBarQ=null;" +
            "q.forEach(function(o){try{cb(o)}catch(e){}});}};" +
            "window.__brainBarPush=function(o){var cb=window.__brainBarCb;if(cb){try{cb(o)}catch(e){}}" +
            "else{(window.__brainBarQ=window.__brainBarQ||[]).push(o);}};\n";
        const string html = "<!doctype html><html style=\"background:transparent\"><head><meta charset=\"utf-8\">" +
            "<style>html,body{margin:0;background:transparent;overflow:hidden}</style></head><body></body></html>";
        // Keep a direct reference: the first tab leaves the tab registry when the
        // user closes it, but the window (and this view) live on.
        var firstTab = tab;
        // Toolbar commands act on whichever tab is shown; page views report their own tab.
        Mac.WkBar.OnBarMessage = msg => HandleWebMessage(Tabs.Active ?? Tabs.All.FirstOrDefault() ?? firstTab, msg, fromBar: true);
        Mac.WkTabs.OnMessage = OnPageViewMessage;
        Mac.WkBar.Create(nsWin, shim + Assets.ChromeJs, html);
    }

    private IntPtr SplitWindow()
    {
        if (_nsWindowNumber != 0) return Mac.MacWindow.WindowByNumber(_nsWindowNumber);
        return _driverTab is { } t && t.MacWindowNumber != 0 ? Mac.MacWindow.WindowByNumber(t.MacWindowNumber) : IntPtr.Zero;
    }

    private void HandleWebMessage(BrowserTab tab, string message, bool fromBar = false)
    {
        if (string.IsNullOrEmpty(message)) return;

        // Eval result envelope
        if (message.StartsWith("{\"b\":1,", StringComparison.Ordinal) ||
            message.StartsWith("{\"b\": 1,", StringComparison.Ordinal))
        {
            try
            {
                using var doc = JsonDocument.Parse(message);
                var root = doc.RootElement;
                var id = root.GetProperty("id").GetString()!;
                if (_pendingEval.TryRemove(id, out var tcs))
                {
                    if (root.GetProperty("ok").GetBoolean())
                        tcs.TrySetResult(root.TryGetProperty("v", out var v) ? v.Clone() : default);
                    else
                    {
                        // The envelope reports failures as ok:false with the
                        // error text in v (e only exists when sendMessage
                        // itself threw inside the envelope).
                        var err = root.TryGetProperty("e", out var e) ? e.GetString()
                            : root.TryGetProperty("v", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()
                            : null;
                        tcs.TrySetException(new JavaScriptException(err ?? "unknown JS error"));
                    }
                }
            }
            catch { /* malformed envelope — ignore */ }
            return;
        }

        // Console relay from brain.js hook (macOS path)
        if (message.StartsWith("{\"b\":2,", StringComparison.Ordinal))
        {
            try
            {
                using var doc = JsonDocument.Parse(message);
                Enqueue(_console, new ConsoleEntry(
                    DateTime.Now,
                    doc.RootElement.GetProperty("level").GetString() ?? "log",
                    doc.RootElement.GetProperty("text").GetString() ?? "",
                    tab.LastUrl, null));
            }
            catch { /* ignore */ }
            return;
        }

        // brain.js pagehide beacon — a navigation away is starting; mark the
        // load in progress so the toolbar progress bar appears. No-op on
        // other drivers (CDP gets real navigation events already).
        if (message.StartsWith("{\"b\":30", StringComparison.Ordinal))
        {
            ((PerTab ? tab.Driver : _driverTab?.Driver) as Drivers.WebKitDriver)?.MarkLoadStarted();
            return;
        }

        // brain.js recorder events ("show it once"): only consumed while the user is recording
        if (message.StartsWith("{\"b\":31,", StringComparison.Ordinal))
        {
            Recorder.Accept(message);
            return;
        }

        // Toolbar / start-page commands: "brain:<command>[:arg]"
        if (message.StartsWith("brain:", StringComparison.Ordinal))
        {
            var cmd = message[6..];
            var colon = cmd.IndexOf(':');
            var (verb, arg) = colon < 0 ? (cmd, "") : (cmd[..colon], cmd[(colon + 1)..]);
            // A web page can call window.external.sendMessage too: sensitive commands need a trusted sender.
            if (!MessageTrust.Allowed(verb, fromBar, SplitOn, (PerTab ? tab : (Tabs.Active ?? tab)).LastUrl)) return;
            Task.Run(async () =>
            {
                try
                {
                    // Navigation verbs act on the ACTIVE tab (the window
                    // shows whichever logical tab is active).
                    var active = Tabs.Active ?? tab;
                    switch (verb)
                    {
                        case "navigate": await NavigateAsync(arg, active); break;
                        case "back": await GoBackAsync(active); break;
                        case "forward": await GoForwardAsync(active); break;
                        case "reload": if (!OverlayActive) await ReloadAsync(false, active); break;
                        case "hardreload": if (!OverlayActive) await ReloadAsync(true, active); break;
                        case "pastego":
                            {
                                string? clip = null;
                                try { clip = IsMac ? InvokeOnUi(() => Mac.WkClipboard.Text()) : Native.Win32.ClipboardText(); } catch { }
                                if (!string.IsNullOrWhiteSpace(clip)) await NavigateAsync(clip.Trim(), active);
                                break;
                            }
                        case "stop":
                            var d = await DriverOrThrowAsync(active, default);
                            await d.EvaluateAsync("window.stop()", false, default);
                            Interlocked.Exchange(ref _loadDepth, 0);
                            if (PerTab) active.Loading = false;
                            PushChromeStateAll();
                            break;
                        case "home": await NavigateAsync("newtab", active); break;
                        case "newtab": await NewTabAsync(string.IsNullOrEmpty(arg) ? null : arg, default); break;
                        case "newprivtab": await NewTabAsync(string.IsNullOrEmpty(arg) ? null : arg, default, priv: true); break;
                        case "pin":
                            if (Tabs.FindByPrefix(arg) is { } pt)
                            {
                                pt.IsPinned = !pt.IsPinned;
                                // pinned tabs cluster at the strip's left edge
                                Tabs.Move(pt.Id, pt.IsPinned ? Tabs.PinnedCount - 1 : Tabs.PinnedCount);
                                PushChromeStateAll();
                                SaveSessionSoon();
                            }
                            break;
                        case "mute":
                            if (Tabs.FindByPrefix(arg) is { } mu) ToggleTabMute(mu);
                            break;
                        case "audible": SetPageAudible(arg == "1"); break;
                        case "restoresession":
                            {
                                var urls = _savedSession;
                                _savedSession = new List<string>();
                                foreach (var u in urls) await NewTabAsync(u, default);
                                PushChromeStateAll();
                                break;
                            }
                        case "switchtab": if (arg.Length > 0) await ActivateWindowAsync(arg); break;
                        case "closetab": if (arg.Length > 0) await CloseWindowAsync(arg); break;
                        case "movetab":
                            {
                                var p2 = arg.Split(':', 2);
                                if (p2.Length == 2 &&
                                    Tabs.FindByPrefix(p2[0]) is { } mt &&
                                    int.TryParse(p2[1], out var idx) && Tabs.Move(mt.Id, idx))
                                    PushChromeStateAll();
                                break;
                            }
                        case "reopen":
                            if (_closedTabs.TryPop(out var ct2))
                                await NewTabAsync(ct2.Url, default);
                            break;
                        case "dupetab":
                            if (Tabs.FindByPrefix(arg) is { } src)
                                await NewTabAsync(src.LastUrl, default);
                            break;
                        case "reloadtab":
                            if (!PerTab) await ActivateWindowAsync(arg);
                            await ReloadAsync(false, Tabs.FindByPrefix(arg));
                            break;
                        case "closeothers":
                            if (Tabs.FindByPrefix(arg) is { } keep)
                            {
                                await ActivateWindowAsync(arg); // target becomes active first
                                foreach (var t in Tabs.All.ToList())
                                    if (t.Id != keep.Id)
                                        await CloseWindowAsync(t.Id.ToString("N")[..8], default);
                            }
                            break;
                        case "closeright":
                            {
                                var anchor = Tabs.FindByPrefix(arg);
                                if (anchor is not null)
                                {
                                    var order = Tabs.All.ToList();
                                    var ai = order.FindIndex(t => t.Id == anchor.Id);
                                    foreach (var t in order.Skip(ai + 1).ToList())
                                        await CloseWindowAsync(t.Id.ToString("N")[..8], default);
                                }
                                break;
                            }
                        case "tabs":
                            // A freshly mounted toolbar is asking because it has no
                            // state — never dedupe against the previous document's push.
                            _lastPushedStateJson = null;
                            if (PerTab && Tabs.Active is { } shown && shown.Id != tab.Id && !tab.IsClosed)
                            {
                                // a hidden tab's page just mounted: bring only that page up to date
                                var sj = BuildChromeStateJson(null);
                                try { InvokeOnUi(() => PushToView(tab, sj)); } catch { }
                            }
                            else PushChromeStateAll();
                            // A login submit navigated away before its prompt could
                            // land — offer it on the fresh document now.
                            FlushPwPending(tab);
                            // a new document in the recorded tab: put the recorder back
                            if (Recorder.Active && Tabs.Active is { } recTab) _ = RecInjectAsync(recTab);
                            break;
                        case "shot": _ = SaveScreenshotAsync(active, true); break;
                        case "shotview": _ = SaveScreenshotAsync(active, false); break;
                        case "updatecheck": _ = Updater.CheckAsync(force: true, CancellationToken.None); break;
                        case "updateapply":
                            // One click does the whole dance: fetch if needed, then swap + relaunch.
                            _ = Task.Run(async () =>
                            {
                                await Updater.DownloadAsync(CancellationToken.None);
                                Updater.ApplyAndRelaunch();
                            });
                            break;
                        case "zoomin": ZoomBy(+1); break;
                        case "zoomout": ZoomBy(-1); break;
                        case "zoomreset": SetZoomLevel(100); break;
                        case "fullscreen":
                            try { InvokeOnUi(() =>
                            {
                                if (_mainWindow is null) return;
                                if (IsMac)
                                {
                                    var nsWin = active is not null ? Mac.MacWindow.NsWindowForTab(active) : IntPtr.Zero;
                                    if (nsWin == IntPtr.Zero) return;
                                    // Legacy enterFullScreenMode presentation trumps: exit it,
                                    // don't stack a real fullscreen on top.
                                    if (!Mac.MacWindow.ExitLegacyFullScreen(nsWin))
                                        Mac.MacWindow.ToggleFullScreen(nsWin);
                                    else
                                        Mac.MacWindow.RepairWebViewFrame(nsWin);
                                }
                                else _mainWindow.FullScreen = !_mainWindow.FullScreen;
                            }); } catch { }
                            break;
                        case "topmost":
                            _topmost = !_topmost;
                            try { InvokeOnUi(() => { if (_mainWindow is not null) _mainWindow.Topmost = _topmost; }); } catch { }
                            PushChromeStateAll();
                            break;
                        // Chromeless-window controls (drawn in the bar by
                        // chrome.js; the native title bar is gone).
                        case "wmin":
                            try { InvokeOnUi(() => { if (_mainWindow is not null) _mainWindow.Minimized = true; }); } catch { }
                            break;
                        case "wmax":
                            try { InvokeOnUi(() =>
                            {
                                if (_mainWindow is null) return;
                                _mainWindow.Maximized = !_mainWindow.Maximized;
                                _winMax = _mainWindow.Maximized;
                            }); } catch { }
                            break;
                        case "wclose":
                            try { InvokeOnUi(() => _mainWindow?.Close()); } catch { }
                            break;
                        case "wdrag":
                            try { InvokeOnUi(() =>
                            {
                                if (IsMac)
                                {
                                    var nsWin = active is not null ? Mac.MacWindow.NsWindowForTab(active) : IntPtr.Zero;
                                    if (nsWin != IntPtr.Zero) Mac.MacWindow.BeginWindowDrag(nsWin);
                                }
                                else Native.Win32.BeginNcDrag(_mainWindow?.WindowHandle ?? IntPtr.Zero, Native.Win32.HtCaption);
                            }); } catch { }
                            break;
                        case "wresize" when int.TryParse(arg, out var ht):
                            try { InvokeOnUi(() => Native.Win32.BeginNcDrag(_mainWindow?.WindowHandle ?? IntPtr.Zero, ht)); } catch { }
                            break;
                        case "bm": ToggleBookmark(active); break;
                        // macOS per-tab views keep their own scroll; shared views and Windows views (which can be
                        // evicted and reloaded) need it recorded
                        case "scroll": if (!SplitOn) RecordScroll(PerTab ? tab : (DocTab ?? active), arg); break;
                        // DevTools panel feed + REPL
                        // Downloads
                        case "dllist": PushDownloads(); break;
                        case "dlopen": Downloads.Open(arg); break;
                        case "dlreveal": Downloads.Reveal(arg); break;
                        case "dlcancel": Downloads.Cancel(arg); break;
                        case "dlclear": Downloads.ClearDone(); break;
                        case "se": SetEngine(arg); break;
                        case "clipread": PushClipboardText(tab); break;
                        // Custom key bindings (about:shortcuts). keyset:<id>:<gesture>
                        // stores an override ("" = unbind); keyreset[:<id>] restores.
                        case "keyset": KeySet(arg); break;
                        case "keyreset": KeyReset(arg); break;
                        // Rewind (opt-in page time travel) — see the Rewind region below.
                        case "rwtoggle": RwSetOn(arg == "1"); break;
                        case "rwcfg": RwConfig(arg); break;
                        case "rwskip": RwToggleSkip(tab); break;
                        case "rwsnap": RwSnap(tab, arg); break;
                        case "rwlist": PushRwList(tab); break;
                        case "rwget": PushRwSnap(tab, arg); break;
                        case "rwsearch": PushRwSearch(tab, arg); break;
                        case "rwclear": RwClear(tab, arg); break;
                        // Password vault — page-side asks for this origin's
                        // creds (b:23), submits a detected login for the save
                        // prompt (b:24), and the manager page lists all (b:25).
                        case "pwlist": await PushPwCreds(tab); break;
                        case "pwcand": PwCandidate(tab, arg); break;
                        case "pwsave": _pwPending.TryGetValue(tab.Id, out var pps); PwSave(tab, arg, pps.O); _pwPending.Remove(tab.Id); break;
                        case "pwskip": _pwPending.TryGetValue(tab.Id, out var ppk); PwSkip(tab, ppk.O); _pwPending.Remove(tab.Id); break;
                        case "pwdismiss": _pwPending.Remove(tab.Id); break;
                        case "pwget": PushPwAll(tab); break;
                        case "pwfill": await PwFillAsync(tab, arg); break;
                        case "pwreveal": await PwRevealAsync(tab, arg); break;
                        case "pwdel": await PwDeleteAsync(tab, arg); break;
                        case "dtget": await PushDevTools(tab); break;
                        case "dtclearc": _console.Clear(); break;
                        case "dtclearn": _network.Clear(); break;
                        case "dteval": await DevToolsEvalAsync(active, arg); break;
                        case "dtnet": PushNetDetail(tab, arg); break;
                        case "dtsend": await DevToolsSendAsync(active, arg); break;
                        case "dtcookies": await PushDtCookiesAsync(tab); break;
                        case "dtcdel": await DtCookieDeleteAsync(tab, arg); break;
                        case "dtcset": await DtCookieSetAsync(tab, arg); break;
                        case "dtsessions": PushDtSessions(tab); break;
                        case "dtstate":
                            _dtOpen = arg.Length > 0;
                            if (arg.Length > 0) _dtTab = arg;
                            break;
                        case "agtoggle": SetAgentPaused(!_agPaused); break;
                        case "askok": Belt.Answer(arg); break;
                        case "beltmode": Belt.SetMode(arg); break;
                        case "beltget": PushBelt(); PushHandoff(); break;
                        case "handoff": Handoff.Answer(arg); break;
                        case "recstart": await RecStartAsync(active); break;
                        case "recstop": await RecStopAsync(active, arg); break;
                        case "recdiscard":
                            try { await EvaluateAsync("window.__brain && window.__brain.recStop && window.__brain.recStop()", false, active, default); } catch { }
                            Recorder.Discard();
                            break;
                        case "recget": PushRec(); break;
                        case "barready": PushChromeStateAll(); break;
                        case "barh":
                            if (SplitOn)
                                try { InvokeOnUi(() => Mac.WkBar.Layout(SplitWindow(), arg)); } catch { }
                            break;
                        case "barui":
                            if (SplitOn && System.Text.RegularExpressions.Regex.IsMatch(arg, "^[a-z]{1,12}$"))
                                try { InvokeOnUi(() =>
                                {
                                    Mac.WkBar.FocusBar(SplitWindow());
                                    Mac.WkBar.Eval("window.__brainBarCmd&&window.__brainBarCmd('" + arg + "')");
                                }); } catch { }
                            break;
                        case "pageui":
                            if (SplitOn && System.Text.RegularExpressions.Regex.IsMatch(arg, "^[a-z]{1,12}$"))
                                try { InvokeOnUi(() =>
                                {
                                    Mac.WkBar.FocusPage(SplitWindow());
                                    var pv = Tabs.Active?.NativeView ?? IntPtr.Zero;
                                    if (pv != IntPtr.Zero && Mac.WkTabs.Has(pv))
                                        Mac.WkTabs.Push(pv, "{\"b\":60,\"c\":\"" + arg + "\"}");
                                }); } catch { }
                            break;
                        case "focuspage":
                            if (SplitOn) try { InvokeOnUi(() => Mac.WkBar.FocusPage(SplitWindow())); } catch { }
                            break;
                        case "flowget": PushFlowsState(tab); break;
                        case "flowsave": FlowUiSave(tab, arg); break;
                        case "flowdel": Flows.Delete(arg); PushFlowsState(tab); break;
                        case "flowapprove": FlowUiApprove(tab, arg); break;
                        case "flowreject": Flows.Reject(arg); PushFlowsState(tab); break;
                        case "flowrun": await FlowUiRunAsync(tab, arg); break;
                        case "flowdraft": FlowUiDraft(tab, arg); break;
                        case "actget": FlowUiActivityDetail(tab, arg); break;
                        case "actclear": Activity.Clear(); PushFlowsState(tab); break;
                        case "histget": PushHistory(tab); break;
                        case "histdel": if (arg.Length > 0) { DeleteHistory(arg); PushHistory(tab); } break;
                        case "histclear": ClearHistory(); PushHistory(tab); break;
                        case "importscan": await ImportScanAsync(tab); break;
                        case "importgo": await ImportRunAsync(tab, arg); break;
                        case "dtunclose":
                            {
                                // Restore a specific closed entry (not just
                                // the newest) — rebuild the stack without it.
                                var rest = _closedTabs.Where(c => c.Url != arg).ToList();
                                _closedTabs.Clear();
                                for (var i = rest.Count - 1; i >= 0; i--) _closedTabs.Push(rest[i]);
                                await NewTabAsync(arg, default);
                                break;
                            }
                    }
                }
                catch (Exception ex) { Log($"toolbar cmd '{verb}' failed: {ex.Message}"); }
            });
            return;
        }

        // lib-request beacon from reinjection watcher (macOS)
        if (message == "brain:needlib")
        {
            _ = EnsurePageHelpersAsync(tab);
            return;
        }
    }

    /// <summary>Serialize the chrome state payload (b:9) shared by pushes and
    /// document boot state. <paramref name="fresh"/> marks a just-created tab
    /// id prefix — only that pill animates on first paint.
    /// <paramref name="src"/> tags boot origin ("inj" = injected new-document
    /// script, "emb" = embedded in an internal page) so chrome.js can let the
    /// embedded payload win when both exist.</summary>
    private string BuildChromeStateJson(string? fresh, string? src = null)
    {
        var activeId = Tabs.Active?.Id;
        var tabs = Tabs.All.Where(t => !t.IsClosed).Select(t => new
        {
            id = t.Id.ToString("N")[..8],
            title = t.LastTitle ?? (t.LastUrl is "about:newtab" or "app://newtab" or "about:blank" ? "New Tab" : t.LastUrl == "about:help" ? "How to use" : t.LastUrl == "about:shortcuts" ? "Shortcuts" : t.LastUrl == "about:history" ? "History" : t.LastUrl == "about:import" ? "Import" : t.LastUrl == "about:passwords" ? "Passwords" : t.LastUrl == "about:flows" ? "Flows & activity" : t.LastUrl),
            url = t.LastUrl,
            icon = t.LastIcon,
            active = t.Id == activeId,
            priv = t.IsPrivate,
            pin = t.IsPinned,
            mute = t.IsMuted,
            aud = t.IsAudible && (PerTab || t.Id == _docTabId),
            // owns the live document — lets chrome.js predict whether a
            // switch is instant (doc/overlay) or a navigation
            doc = PerTab ? !t.Evicted : t.Id == _docTabId, // live per-tab views: switching never reloads
        }).ToList();
        object[] hist;
        lock (_history)
            hist = _history.Take(40).Select(h => (object)new { u = h.Url, t = h.Title, i = h.Icon, d = h.Time, v = h.Visits }).ToArray();
        object[] bmarks;
        lock (_bookmarks)
            bmarks = _bookmarks.Take(10).Select(bm => (object)new { u = bm.Url, t = bm.Title, i = bm.Icon }).ToArray();
        var activeUrl = Tabs.Active?.LastUrl;
        bool isBm;
        lock (_bookmarks)
            isBm = activeUrl is not null && _bookmarks.Any(b => b.Url == activeUrl);
        // Overlay tab: the hidden document's nav/loading state isn't this tab's.
        var ov = OverlayActive;
        return JsonSerializer.Serialize(new
        {
            b = 9,
            tabs,
            loading = IsLoading && !ov,
            canBack = (PerTab ? Tabs.Active?.CanBack == true : _canBack) && !ov,
            canFwd = (PerTab ? Tabs.Active?.CanFwd == true : _canFwd) && !ov,
            ov,
            hist,
            bmarks,
            bm = isBm,
            zoom = _zoom,
            topmost = _topmost,
            wc = Chromeless,
            // macOS: real traffic lights float over the toolbar's left inset
            // (FullSizeContentView + transparent titlebar) — the bar pads for
            // them and empty bar space starts a native window drag.
            mtl = IsMac && !_noToolbar,
            // mute/audio handled natively by the engine; otherwise chrome.js
            // enforces and detects it inside the page
            nat = IsMac || WinPerTab,
            w = _winMax ? 1 : 0,
            dt = _dtOpen ? _dtTab : "",
            privActive = Tabs.Active?.IsPrivate == true,
            se = _engine.Id,
            rw = _rwOn, rwa = _rwAgent, rwk = _rwKeepMin, rwm = _rwMaxMb, rws = RwSkipList(),
            keys = _keyBinds,
            sel = SearchEngines.Select(e => new { i = e.Id, n = e.Name }).ToArray(),
            sess = _savedSession.Count,
            ag = AgentObj(),
            split = SplitOn,
            fresh,
            src,
        });
    }

    private string? _lastPushedStateJson;

    /// <summary>Push current tab/URL state to a window's injected chrome bar.</summary>
    public void PushChromeState(BrowserTab tab)
    {
        // Skip until the webview exists — SendWebMessage on a bare window
        // crashes (chromeless window events fire before the webview is up),
        // and without brain.js there's nothing listening anyway.
        if (_noToolbar || tab.IsClosed || !_driverReady.Task.IsCompleted) return;
        var json = BuildChromeStateJson(null);
        if (json == _lastPushedStateJson) return; // duplicate push elision
        _lastPushedStateJson = json;
        try { InvokeOnUi(() => { if (!tab.IsClosed) SendUi(tab, json); }); }
        catch { /* window mid-close */ }
    }

    /// <summary>Embed the boot state AND the helper libs directly into an
    /// internal page's HTML. NavigateToString docs don't get
    /// addScriptToEvaluateOnNewDocument, so this is the only way the chrome
    /// bar mounts before first paint on about: pages (zero-gap = no flicker).
    /// <paramref name="animate"/> plays the page's entrance rise — first
    /// launch only; tab opens/switches paint final state instantly.</summary>
    private string InjectBootState(string html, bool animate = false)
    {
        // '<' inside a <script> would break parsing — escape it in the JSON.
        var json = BuildChromeStateJson(ConsumeFresh(), "emb").Replace("<", "\\u003c");
        var lib = PageLibSource;
        // The new-document injection normally already ran brain.js+chrome.js
        // in this doc — executing the inlined copy again cost ~40ms before
        // first paint. Ship it as inert text/plain (no compile) and eval only
        // when the injection is missing (driver not bound yet, first launch).
        return html.Replace("</head>",
            "<script>window.__brainBoot=" + json +
            ";window.__brainPageAnim=" + (animate ? "true" : "false") +
            (animate ? ";document.documentElement.className+=' anim'" : "") + ";" +
            "try{window.__brainChrome&&__brainChrome.applyBoot()}catch(e){}</script>" +
            "<script type=\"text/plain\" id=\"__brainlib\">" + lib + "</script>" +
            "<script>if(!window.__brainChrome){try{(0,eval)(document.getElementById('__brainlib').textContent)}catch(e){}}</script></head>");
    }

    private string? ConsumeFresh()
    {
        var f = _pendingFresh;
        _pendingFresh = null;
        return f;
    }

    /// <summary>macOS: drives the native load indicator. The in-page bar can't paint
    /// during a pending navigation (WKWebView freezes the page), so it is the only
    /// indicator that shows while a slow page is loading.</summary>
    private void SetLoadBar(BrowserTab tab, bool on)
    {
        if (!IsMac || _noToolbar) return;
        var nsWin = Mac.MacWindow.NsWindowForTab(tab);
        if (nsWin == IntPtr.Zero) return;
        try { InvokeOnUi(() => Mac.MacWindow.SetLoadingBar(nsWin, on)); } catch { }
    }

    // ------------------------------------------------------------------
    // Agent activity + pause. The MCP request filter calls AgentBegin/End
    // around every tool call; while paused, action tools are refused (reads
    // still work) and running flows stop between steps.
    // ------------------------------------------------------------------
    private int _agBusy, _agBlocked;
    private string _agTool = "";
    private volatile bool _agPaused;
    private volatile bool _agSeen;

    public bool AgentPaused => _agPaused;

    private object AgentObj() => new { p = _agPaused, n = Volatile.Read(ref _agBusy), t = _agTool, s = _agSeen, k = Volatile.Read(ref _agBlocked) };

    public void AgentBegin(string tool) { _agSeen = true; _agTool = tool; Interlocked.Increment(ref _agBusy); PushAgent(); }
    public void AgentEnd() { if (Interlocked.Decrement(ref _agBusy) < 0) Interlocked.Exchange(ref _agBusy, 0); PushAgent(); }
    public void AgentBlocked(string tool) { _agSeen = true; _agTool = tool; Interlocked.Increment(ref _agBlocked); PushAgent(); }

    public void SetAgentPaused(bool paused)
    {
        if (_agPaused == paused) return;
        _agPaused = paused;
        Interlocked.Exchange(ref _agBlocked, 0);
        PushAgent();
    }

    /// <summary>Small {b:40} push — the full chrome state also carries "ag" for page swaps.</summary>
    private void PushAgent()
    {
        if (_noToolbar || !_driverReady.Task.IsCompleted) return;
        var t = UiTab();
        if (t is null || t.IsClosed) return;
        var json = JsonSerializer.Serialize(new { b = 40, ag = AgentObj() });
        try { InvokeOnUi(() => { if (!t.IsClosed) SendUi(t, json); }); }
        catch { /* window mid-close */ }
    }

    /// <summary>{b:47}: the seatbelt mode and any actions waiting for the user's answer.
    /// Not relayed to page scripts (the ids in it are what authorise an action).</summary>
    private void PushBelt()
    {
        if (_noToolbar || !_driverReady.Task.IsCompleted) return;
        var t = Tabs.Main ?? Tabs.All.FirstOrDefault();
        if (t is null || t.IsClosed) return;
        var json = JsonSerializer.Serialize(new { b = 47, belt = Belt.View() });
        try { InvokeOnUi(() => { if (!t.IsClosed) SendUi(t, json); }); }
        catch { /* window mid-close */ }
    }

    /// <summary>{b:49}: questions the agent asked the user to answer by hand (browser_ask_human).
    /// Not relayed to page scripts (the ids in it are what authorise an answer).</summary>
    private void PushHandoff()
    {
        if (_noToolbar || !_driverReady.Task.IsCompleted) return;
        // Every tab gets the card: the agent may be working on a background tab
        // while the user is looking at another one.
        var json = JsonSerializer.Serialize(new { b = 49, ho = Handoff.View() });
        foreach (var t in Tabs.All)
            try { InvokeOnUi(() => { if (!t.IsClosed) SendUi(t, json); }); }
            catch { /* window mid-close */ }
    }

    private void PushUpdate()
    {
        var json = JsonSerializer.Serialize(new { b = 61, upd = Updater.View() });
        foreach (var t in Tabs.All)
            try { InvokeOnUi(() => { if (!t.IsClosed) SendUi(t, json); }); }
            catch { /* window mid-close */ }
    }

    /// <summary>{b:48}: recording state for the pill and the save card, plus the result of a save.
    /// Not relayed to page scripts.</summary>
    private void PushRec(object? done = null)
    {
        if (_noToolbar || !_driverReady.Task.IsCompleted) return;
        var t = Tabs.Main ?? Tabs.All.FirstOrDefault();
        if (t is null || t.IsClosed) return;
        var json = JsonSerializer.Serialize(new { b = 48, rec = Recorder.View(), done });
        try { InvokeOnUi(() => { if (!t.IsClosed) SendUi(t, json); }); }
        catch { /* window mid-close */ }
    }

    private async Task RecStartAsync(BrowserTab tab)
    {
        var url = tab.LastUrl ?? "";
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            PushRec(new { ok = false, name = "", err = "Open a web page first, then start recording." });
            return;
        }
        Recorder.Start(tab.Id, url, tab.LastTitle ?? "");
        await RecInjectAsync(tab);
    }

    private async Task RecInjectAsync(BrowserTab tab)
    {
        try
        {
            await EnsurePageHelpersAsync(tab);
            await EvaluateAsync("window.__brain && window.__brain.recStart && window.__brain.recStart()", false, tab, default);
        }
        catch { /* the page may be mid-navigation; the next mount tries again */ }
    }

    /// <summary>Stops recording and files the steps as a pending flow for the user to approve in Flows.</summary>
    private async Task RecStopAsync(BrowserTab tab, string arg)
    {
        string name = "", desc = "";
        var specs = new List<BrAIn.Mcp.FlowParamSpec>();
        try
        {
            using var d = JsonDocument.Parse(arg);
            var r = d.RootElement;
            name = r.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            desc = r.TryGetProperty("description", out var ds) ? ds.GetString() ?? "" : "";
            if (r.TryGetProperty("params", out var ps) && ps.ValueKind == JsonValueKind.Array)
                foreach (var p in ps.EnumerateArray())
                    specs.Add(new BrAIn.Mcp.FlowParamSpec
                    {
                        Name = p.TryGetProperty("name", out var pn) ? pn.GetString() ?? "" : "",
                        Description = p.TryGetProperty("description", out var pd) ? pd.GetString() : null,
                        Example = p.TryGetProperty("example", out var pe) ? pe.GetString() : null,
                    });
        }
        catch { /* fall through with defaults; validation below reports a missing name */ }
        try { await EvaluateAsync("window.__brain && window.__brain.recStop && window.__brain.recStop()", false, tab, default); } catch { }
        var entries = Recorder.TakeEntries();
        if (!entries.Any(e => e.Tool != "browser_navigate"))
        {
            PushRec(new { ok = false, name, err = "Nothing was recorded." });
            return;
        }
        var def = Flows.BuildFromActivity(entries, name, desc, specs);
        def.Source = "activity";
        var (err, _) = Flows.Propose(def);
        PushRec(err is null ? new { ok = true, name = def.Name, err = "" } : new { ok = false, name = def.Name, err });
    }

    public void PushChromeStateAll()
    {
        // All logical tabs share the single window — one push updates chrome.
        var t = UiTab();
        if (t is not null) PushChromeState(t);
    }

    /// <summary>Push the full downloads snapshot to the toolbar ({b:20}).
    /// Fired by Downloads.Changed and on the chrome's dllist request.</summary>
    /// <summary>Switch the default search provider. Id arrives from the
    /// ⋮ menu flyout or a palette action; persists to prefs.json and pushes
    /// fresh state so omnibox/NTP labels re-label themselves.</summary>
    private void SetEngine(string id)
    {
        var e = SearchEngines.FirstOrDefault(x => x.Id == id);
        if (e is null || e == _engine) return;
        _engine = e;
        SavePrefs();
        PushChromeStateAll();
    }

    /// <summary>keyset:<id>:<gesture> — store a custom binding for one action.
    /// An empty gesture marks the action explicitly unbound (survives restarts).
    /// Validation is deliberately light — the page produces normalized gestures;
    /// this just keeps control chars / absurd payloads out of prefs.json.</summary>
    private void KeySet(string arg)
    {
        var p = arg.Split(':', 2);
        if (p.Length != 2 || p[0].Length is < 2 or > 24 || !p[0].All(char.IsLetterOrDigit)) return;
        if (p[1].Length > 48 || p[1].Any(c => !char.IsLetterOrDigit(c) && "+-=[]/.,';\\` ".IndexOf(c) < 0)) return;
        if (p[1].Length == 0) _keyBinds[p[0]] = ""; else _keyBinds[p[0]] = p[1];
        SavePrefs();
        PushChromeStateAll();
    }

    /// <summary>keyreset[:<id>] — drop overrides (one or all) so defaults return.</summary>
    private void KeyReset(string arg)
    {
        if (arg.Length == 0) _keyBinds.Clear(); else _keyBinds.Remove(arg);
        SavePrefs();
        PushChromeStateAll();
    }

    private void SavePrefs() => SaveStore("prefs.json", new
    {
        se = _engine.Id,
        rw = _rwOn, rwa = _rwAgent, rwk = _rwKeepMin, rwm = _rwMaxMb, rws = RwSkipList(),
        keys = _keyBinds,
    });

    /// <summary>Page-menu Paste — the webviews expose no in-page clipboard
    /// read (navigator.clipboard is absent in this context), so the text is
    /// fetched host-side and pushed back for caret insertion (b:22).</summary>
    private void PushClipboardText(BrowserTab tab)
    {
        if (_noToolbar || tab.IsClosed || !_driverReady.Task.IsCompleted) return;
        string? text = null;
        try
        {
            text = IsMac
                ? InvokeOnUi(() => Mac.WkClipboard.Text())
                : Native.Win32.ClipboardText();
        }
        catch { }
        if (string.IsNullOrEmpty(text)) return;
        var json = JsonSerializer.Serialize(new { b = 22, t = text });
        try { InvokeOnUi(() => { if (!tab.IsClosed) SendUi(tab, json); }); }
        catch { /* window mid-close */ }
    }

    // ------------------------------------------------------------------
    // Rewind — opt-in time travel for pages. OFF by default because it costs
    // CPU and memory. When on, the visible tab's page view (chrome.js)
    // periodically snapshots its DOM, gzips it and posts it here. Snapshots
    // live in RAM only (never on disk), never for private tabs, never for
    // excluded sites, inside a byte + age budget. The page view's Rewind bar
    // scrubs through them. MCP can read them only if the user separately
    // allows it ("Let the AI agent read this history").
    // ------------------------------------------------------------------
    private readonly Rewind.RewindStore _rw = new();
    private bool _rwOn;
    private bool _rwAgent;
    private int _rwKeepMin = 30, _rwMaxMb = 128;
    private readonly HashSet<string> _rwSkip = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>User key-binding overrides (about:shortcuts). action-id →
    /// normalized gesture ("mod+shift+t"); "" = explicitly unbound. Kept in
    /// prefs.json and pushed in the chrome state so every document's
    /// keymap rebuilds live.</summary>
    private readonly Dictionary<string, string> _keyBinds = new(StringComparer.Ordinal);

    private string[] RwSkipList() { lock (_rwSkip) return [.. _rwSkip]; }

    private bool RwSkipped(string url)
    {
        var h = Rewind.RewindStore.HostOf(url);
        if (h is null) return true; // only http(s) pages are ever recorded
        lock (_rwSkip) return _rwSkip.Any(r => Rewind.RewindStore.HostMatches(h, r));
    }

    private void ApplyRwConfig()
    {
        _rwKeepMin = Math.Clamp(_rwKeepMin, 5, 24 * 60);
        _rwMaxMb = Math.Clamp(_rwMaxMb, 16, 1024);
        _rw.MaxAge = TimeSpan.FromMinutes(_rwKeepMin);
        _rw.MaxBytes = (long)_rwMaxMb << 20;
        _rw.MaxSnapBytes = (int)Math.Min(3 << 20, _rw.MaxBytes / 4); // one snapshot never eats more than a quarter of the budget
        _rw.Trim();
    }

    /// <summary>The logical tab a page message belongs to. Per-tab views report
    /// themselves; the single shared Windows view reports whoever owns the live document.</summary>
    private BrowserTab RwOwner(BrowserTab sender) => PerTab ? sender : (DocTab ?? sender);

    private void RwSetOn(bool on)
    {
        if (_rwOn == on) return;
        _rwOn = on;
        if (!on) _rw.Clear(); // switching off frees every snapshot immediately
        SavePrefs();
        PushChromeStateAll();
    }

    private void RwConfig(string arg)
    {
        var p = arg.Split(':', 2);
        if (p.Length != 2) return;
        switch (p[0])
        {
            case "keep" when int.TryParse(p[1], out var m): _rwKeepMin = m; break;
            case "mb" when int.TryParse(p[1], out var mb): _rwMaxMb = mb; break;
            case "agent": _rwAgent = p[1] == "1"; break;
            default: return;
        }
        ApplyRwConfig();
        SavePrefs();
        PushChromeStateAll();
    }

    /// <summary>Exclude / re-include the current tab's site; excluding also forgets what was recorded from it.</summary>
    private void RwToggleSkip(BrowserTab sender)
    {
        var host = Rewind.RewindStore.HostOf(RwOwner(sender).LastUrl);
        if (host is null) return;
        bool added;
        lock (_rwSkip) { added = !_rwSkip.Remove(host); if (added) _rwSkip.Add(host); }
        if (added) _rw.RemoveHost(host);
        SavePrefs();
        PushChromeStateAll();
    }

    /// <summary>A snapshot from a page view. Everything here is page-controlled
    /// input: size-checked, host-checked, never trusted to be real gzip.</summary>
    private void RwSnap(BrowserTab sender, string json)
    {
        if (!_rwOn || json.Length > 14_000_000) return;
        var tab = RwOwner(sender);
        if (tab.IsPrivate || tab.IsClosed) return;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var url = r.GetProperty("u").GetString() ?? "";
            if (RwSkipped(url)) return;
            var d = r.GetProperty("d").GetString() ?? "";
            var x = r.TryGetProperty("x", out var xp) && xp.ValueKind == JsonValueKind.String ? xp.GetString() ?? "" : "";
            byte[] html, text;
            if (r.TryGetProperty("z", out var zp) && zp.ValueKind == JsonValueKind.Number && zp.GetInt32() == 1)
            {
                html = Convert.FromBase64String(d);
                text = x.Length > 0 ? Convert.FromBase64String(x) : [];
                if (!Rewind.RewindStore.LooksGzip(html)) return;
                if (text.Length > 0 && !Rewind.RewindStore.LooksGzip(text)) text = [];
            }
            else
            {   // page-side compression unavailable (or a last-gasp flush at unload): compress here
                html = Rewind.RewindStore.Gzip(d);
                text = Rewind.RewindStore.Gzip(x);
            }
            int I(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
            var title = r.TryGetProperty("ti", out var tp) && tp.ValueKind == JsonValueKind.String ? RwClip(tp.GetString()!, 200) : "";
            var tag = r.TryGetProperty("tag", out var tg) && tg.ValueKind == JsonValueKind.String ? RwClip(tg.GetString()!, 80) : null;
            var hash = r.TryGetProperty("hs", out var hp) && hp.ValueKind == JsonValueKind.Number ? hp.GetUInt32() : 0u;
            _rw.Add(tab.Id, url, title, html, text, I("sx"), I("sy"), I("vw"), I("vh"), hash, tag);
        }
        catch { /* malformed snapshot — drop it */ }
    }

    private static string RwClip(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    private void PushRwList(BrowserTab sender)
    {
        var tab = RwOwner(sender);
        var live = _rwOn && !tab.IsPrivate;
        var items = live ? _rw.List(tab.Id) : [];
        var (n, tabBytes) = _rw.TabStats(tab.Id);
        PushJson(sender, JsonSerializer.Serialize(new
        {
            b = 71, on = _rwOn, priv = tab.IsPrivate,
            items = items.Select(s => new { i = s.Id, t = s.T, u = s.Url, ti = s.Title, sz = s.Bytes, sy = s.Sy, tag = s.Tag }).ToArray(),
            n = live ? n : 0, tabBytes = live ? tabBytes : 0, bytes = _rw.Bytes, max = _rw.MaxBytes,
        }));
    }

    private void PushRwSnap(BrowserTab sender, string arg)
    {
        if (!long.TryParse(arg, out var id)) return;
        var tab = RwOwner(sender);
        var s = _rwOn && !tab.IsPrivate ? _rw.Get(tab.Id, id) : null;
        PushJson(sender, JsonSerializer.Serialize(new
        {
            b = 72, i = id, ok = s is not null, t = s?.T, u = s?.Url, ti = s?.Title,
            sx = s?.Sx, sy = s?.Sy, vw = s?.Vw, vh = s?.Vh, tag = s?.Tag,
            d = s is null ? null : Convert.ToBase64String(s.Html),
        }));
    }

    private void PushRwSearch(BrowserTab sender, string query)
    {
        var tab = RwOwner(sender);
        var hits = _rwOn && !tab.IsPrivate ? _rw.Search(tab.Id, query.Trim()) : [];
        PushJson(sender, JsonSerializer.Serialize(new
        {
            b = 73, q = query,
            hits = hits.Select(h => new { i = h.Snap.Id, t = h.Snap.T, ti = h.Snap.Title, u = h.Snap.Url, sn = h.Snippet }).ToArray(),
        }));
    }

    private void RwClear(BrowserTab sender, string scope)
    {
        if (scope == "all") _rw.Clear(); else _rw.RemoveTab(RwOwner(sender).Id);
        PushRwList(sender);
    }

    /// <summary>An MCP tool just finished. If it could have changed the page, ask the
    /// page for a tagged snapshot once it settles — so the timeline shows what the
    /// agent did, step by step.</summary>
    internal void RewindAfterTool(string tool, BrowserTab? tab)
    {
        if (!_rwOn || tab is null || tab.IsPrivate || Mcp.ActivityLog.ReadOnlyTools.Contains(tool)) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(700);
            var t = PerTab ? tab : UiTab();
            if (t is null || t.IsClosed) return;
            PushJson(t, JsonSerializer.Serialize(new { b = 74, c = "snap", tag = "agent · " + tool.Replace("browser_", "") }));
        });
    }

    // ---- MCP read access (the user opts in separately from enabling Rewind) ----

    private string? RwGate(BrowserTab tab)
    {
        if (!_rwOn) return "Rewind is off. It is opt-in (it uses extra CPU and memory) — the user can turn it on from the ⋮ menu → Rewind.";
        if (!_rwAgent) return "Rewind is on, but the user has not shared its history with the AI agent. They can allow it in the Rewind bar: ⚙ → “Let the AI agent read this history”.";
        if (tab.IsPrivate) return "Rewind never records private tabs.";
        return null;
    }

    private static string RwAgo(long ms)
    {
        var s = Math.Max(0, ms / 1000);
        return s < 60 ? $"{s}s" : s < 3600 ? $"{s / 60}m {s % 60:00}s" : $"{s / 3600}h {s % 3600 / 60:00}m";
    }

    private static long? RwParseAgo(string? ago)
    {
        if (string.IsNullOrWhiteSpace(ago)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(ago.Trim().ToLowerInvariant(), @"^(\d+)\s*(s|sec|m|min|h|hr)?");
        if (!m.Success || !long.TryParse(m.Groups[1].Value, out var n)) return null;
        return m.Groups[2].Value switch { "h" or "hr" => n * 3_600_000, "m" or "min" or "" => n * 60_000, _ => n * 1000 };
    }

    private static string RwLine(Rewind.RewindStore.Snap s, long now) =>
        $"#{s.Id}  {RwAgo(now - s.T)} ago  {DateTimeOffset.FromUnixTimeMilliseconds(s.T).ToLocalTime():HH:mm:ss}  " +
        $"{RwClip(s.Title, 60)} <{RwClip(s.Url, 100)}>{(s.Tag is null ? "" : $"  [{s.Tag}]")}";

    internal string RewindToolList(BrowserTab tab, int last)
    {
        if (RwGate(tab) is { } g) return g;
        var list = _rw.List(tab.Id);
        if (list.Count == 0) return "Rewind has no snapshots for this tab yet (it records a few seconds after a page settles).";
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var (n, bytes) = _rw.TabStats(tab.Id);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Rewind: {n} snapshot{(n == 1 ? "" : "s")} for this tab ({bytes / 1048576.0:0.0} MB), oldest {RwAgo(now - list[0].T)} ago");
        foreach (var s in list.TakeLast(Math.Clamp(last, 1, 200))) sb.AppendLine(RwLine(s, now));
        return sb.ToString().TrimEnd();
    }

    internal string RewindToolRead(BrowserTab tab, string? id, string? ago, int maxChars)
    {
        if (RwGate(tab) is { } g) return g;
        var list = _rw.List(tab.Id);
        if (list.Count == 0) return "Rewind has no snapshots for this tab yet.";
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Rewind.RewindStore.Snap? s;
        if (!string.IsNullOrWhiteSpace(id))
        {
            s = long.TryParse(id.Trim().TrimStart('#'), out var sid) ? list.FirstOrDefault(x => x.Id == sid) : null;
            if (s is null) return $"No snapshot {id} for this tab (use action=list).";
        }
        else if (RwParseAgo(ago) is { } ms) s = list.LastOrDefault(x => x.T <= now - ms) ?? list[0];
        else s = list[^1];
        string text;
        try { text = Rewind.RewindStore.Gunzip(s.Text); } catch { text = "(text unavailable)"; }
        var cut = Math.Clamp(maxChars, 500, 100_000);
        return RwLine(s, now) + $"\nscroll {s.Sy}px · viewport {s.Vw}×{s.Vh}\n\n" +
               (text.Length > cut ? text[..cut] + "\n… (truncated)" : text);
    }

    internal string RewindToolSearch(BrowserTab tab, string query, int max)
    {
        if (RwGate(tab) is { } g) return g;
        if (string.IsNullOrWhiteSpace(query)) return "Give a 'query' to search for.";
        var hits = _rw.Search(tab.Id, query.Trim(), Math.Clamp(max, 1, 50));
        if (hits.Count == 0) return $"No snapshot of this tab contains “{query}”.";
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return $"{hits.Count} snapshot(s) contain “{query}” (newest first):\n" +
               string.Join('\n', hits.Select(h => RwLine(h.Snap, now) + "\n    " + h.Snippet));
    }

    private void PushDownloads()
    {
        if (_noToolbar || !_driverReady.Task.IsCompleted) return;
        var t = UiTab();
        if (t is null || t.IsClosed) return;
        var json = Downloads.SnapshotJson();
        try { InvokeOnUi(() => { if (!t.IsClosed) SendUi(t, json); }); }
        catch { /* window mid-close */ }
    }

    // ------------------------------------------------------------------
    // Password vault — capture → prompt → fill. The page never names an
    // origin: every credential read/write is bound to the sender tab's real
    // URL so a hostile page can't pull or plant creds for another site.
    // Private tabs get nothing in either direction.
    // ------------------------------------------------------------------

    private static string? OriginOf(string url)
    {
        try
        {
            var u = new Uri(url);
            return u.Scheme is "http" or "https" ? u.GetLeftPart(UriPartial.Authority) : null;
        }
        catch { return null; }
    }

    private void PushJson(BrowserTab tab, string json)
    {
        try { InvokeOnUi(() => { if (!tab.IsClosed) SendUi(tab, json); }); }
        catch { /* window mid-close */ }
    }

    /// <summary>The live document's own origin — an eval of location.href on
    /// the tab's driver, with tab.LastUrl as fallback. pwlist asks at
    /// document-start, when LastUrl can still be the *previous* page.</summary>
    private async Task<string?> DocOriginAsync(BrowserTab tab)
    {
        try
        {
            var d = (DocTab ?? tab).Driver ?? _driverTab?.Driver;
            if (d is { SupportsEval: true })
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var r = await d.EvaluateAsync("location.href", false, cts.Token);
                if (r?.ValueKind == JsonValueKind.String)
                {
                    var o = OriginOf(r.Value.GetString()!);
                    if (o is not null) return o;
                }
            }
        }
        catch { }
        return OriginOf(tab.LastUrl);
    }

    // ---------------- OS unlock gate ----------------

    private long _pwAuthUntilMs;
    private readonly SemaphoreSlim _pwAuthSem = new(1, 1);

    /// <summary>Touch ID / Mac password (LocalAuthentication) or the Windows
    /// Hello PIN prompt, before any plaintext leaves the vault. A two-minute
    /// grace window avoids a prompt per click during one session. Auths are
    /// serialized — a second reveal during an active prompt just waits.</summary>
    private async Task<bool> PwAuthAsync(string reason)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (now < _pwAuthUntilMs) return true;
        await _pwAuthSem.WaitAsync();
        try
        {
            now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now < _pwAuthUntilMs) return true;
            bool ok;
            if (OperatingSystem.IsMacOS())
                ok = await Mac.WkLaAuth.EvaluateAsync(reason, InvokeOnUi);
            else if (OperatingSystem.IsWindows())
            {
                var hwnd = _mainWindow?.WindowHandle ?? IntPtr.Zero;
                ok = await Task.Run(() => Native.Win32.ConsentPrompt(hwnd, reason));
            }
            else ok = false;
            if (ok) _pwAuthUntilMs = now + 120_000;
            return ok;
        }
        catch { return false; }
        finally { _pwAuthSem.Release(); }
    }

    /// <summary>Looks up an entry the page is allowed to touch: for fills the
    /// id must belong to the document's real origin; for manager ops any id.
    /// Keeps hostile pages from fishing other sites' passwords by id.</summary>
    private async Task<PasswordStore.Entry?> PwEntryForFillAsync(BrowserTab tab, string id)
    {
        var o = await DocOriginAsync(tab);
        return o is null ? null : _pw?.ListFor(o).FirstOrDefault(e => e.Id == id);
    }

    private static string PwHost(string origin)
    {
        try { return new Uri(origin).Host; } catch { return origin; }
    }

    /// <summary>pwfill:&lt;id&gt; — dropdown pick from a login page. Authenticates,
    /// then b:26 hands the credential back for the page to fill.</summary>
    private async Task PwFillAsync(BrowserTab tab, string id)
    {
        if (_noToolbar || tab.IsClosed || tab.IsPrivate || _pw is null) return;
        var e = await PwEntryForFillAsync(tab, id);
        if (e is null) return;
        if (!await PwAuthAsync($"autofill your {PwHost(e.Origin)} login"))
        {
            PushJson(tab, JsonSerializer.Serialize(new { b = 26, deny = true }));
            return;
        }
        _pw.Upsert(e.Origin, e.User, e.Pass); // bump last-used without altering
        PushJson(tab, JsonSerializer.Serialize(new { b = 26, id = e.Id, u = e.User, p = e.Pass }));
    }

    /// <summary>pwreveal:&lt;id&gt; — the manager's eye/copy. Authenticates, then
    /// b:27 returns just that password to the requesting tab.</summary>
    private async Task PwRevealAsync(BrowserTab tab, string id)
    {
        if (_noToolbar || tab.IsClosed || tab.IsPrivate || _pw is null) return;
        var e = _pw.All().FirstOrDefault(x => x.Id == id);
        if (e is null) return;
        if (!await PwAuthAsync($"reveal the password for {PwHost(e.Origin)}"))
        {
            PushJson(tab, JsonSerializer.Serialize(new { b = 27, id, deny = true }));
            return;
        }
        PushJson(tab, JsonSerializer.Serialize(new { b = 27, id = e.Id, p = e.Pass }));
    }

    /// <summary>pwdel:&lt;id&gt; — delete is gated like reveal.</summary>
    private async Task PwDeleteAsync(BrowserTab tab, string id)
    {
        if (tab.IsClosed || tab.IsPrivate || _pw is null) return;
        var e = _pw.All().FirstOrDefault(x => x.Id == id);
        if (e is null) return;
        // Denied → repush the authoritative list so the dimmed row restores.
        if (!await PwAuthAsync($"delete the saved password for {PwHost(e.Origin)}"))
        {
            PushPwAll(tab);
            return;
        }
        if (_pw.Delete(id)) PushPwAll(tab);
    }

    /// <summary>b:23 — usernames saved for the sender tab's origin. Passwords
    /// never ride this push: filling goes through the authenticated pwfill →
    /// b:26 path, so a page can't grab plaintext just by loading.</summary>
    private async Task PushPwCreds(BrowserTab tab)
    {
        if (_noToolbar || tab.IsClosed || _pw is null) return;
        var o = tab.IsPrivate ? null : await DocOriginAsync(tab);
        var items = o is null
            ? Array.Empty<object>()
            : _pw.ListFor(o).Select(e => (object)new { id = e.Id, u = e.User }).ToArray();
        PushJson(tab, JsonSerializer.Serialize(new { b = 23, items }));
    }

    /// <summary>A page detected a login (form submit / pagehide with a filled
    /// password field). If the vault already holds it, stay silent; if the
    /// origin is on the Never list, stay silent; otherwise b:24 asks.</summary>
    private void PwCandidate(BrowserTab tab, string arg)
    {
        if (_noToolbar || tab.IsClosed || tab.IsPrivate || _pw is null) return;
        try
        {
            var j = JsonDocument.Parse(arg).RootElement;
            // The page reports its own origin at submit time — that IS the
            // credential's site even when a cross-origin redirect follows
            // (evaluating now could already see the landing page). The arg's
            // o is still normalized through OriginOf so junk can't pass.
            var o = OriginOf(j.TryGetProperty("o", out var oo) ? oo.GetString() ?? "" : "");
            var u = j.TryGetProperty("u", out var uu) ? uu.GetString() ?? "" : "";
            var p = j.TryGetProperty("p", out var pp) ? pp.GetString() ?? "" : "";
            if (o is null || p.Length == 0 || _pwSkip.Contains(o) || _pw.HasExact(o, u, p)) return;
            _pwPending[tab.Id] = (o, u, p, 0);
            // Same-document delay: XHR logins never navigate, so the prompt
            // shows right on the login page; a real navigation instead gets
            // its prompt from FlushPwPending when the new document mounts.
            var id = tab.Id;
            _ = Task.Run(async () =>
            {
                await Task.Delay(700);
                FlushPwPending(tab, id);
            });
        }
        catch { }
    }

    /// <summary>Pushes a pending save prompt if one is queued for the tab —
    /// drops it after two deliveries so an ignored prompt can't shadow every
    /// future page forever.</summary>
    private void FlushPwPending(BrowserTab tab, Guid? id = null)
    {
        var key = id ?? tab.Id;
        if (!_pwPending.TryGetValue(key, out var pp) || tab.IsClosed) return;
        if (pp.Attempts >= 2) { _pwPending.Remove(key); return; }
        _pwPending[key] = pp with { Attempts = pp.Attempts + 1 };
        PushJson(tab, JsonSerializer.Serialize(new { b = 24, o = pp.O, u = pp.U, p = pp.P }));
    }

    /// <summary>Save-now answer to the b:24 prompt — the origin comes from
    /// the pending entry captured at submit time, not from the page.</summary>
    private void PwSave(BrowserTab tab, string arg, string? origin)
    {
        if (tab.IsPrivate || _pw is null || origin is null) return;
        try
        {
            var j = JsonDocument.Parse(arg).RootElement;
            var u = j.TryGetProperty("u", out var uu) ? uu.GetString() ?? "" : "";
            var p = j.TryGetProperty("p", out var pp) ? pp.GetString() ?? "" : "";
            _pw.Upsert(origin, u, p);
        }
        catch { }
    }

    /// <summary>"Never for this site" — suppresses future prompts on the
    /// origin; persisted with the other store files.</summary>
    private void PwSkip(BrowserTab tab, string? origin)
    {
        if (origin is null) return;
        _pwSkip.Add(origin);
        SaveStore("pwskip.json", _pwSkip.ToArray());
    }

    /// <summary>b:25 — the vault's metadata for about:passwords. Plaintext
    /// stays host-side; rows fetch a single password via authenticated
    /// pwreveal → b:27 when the eye/copy is clicked.</summary>
    private void PushPwAll(BrowserTab tab)
    {
        if (tab.IsClosed || tab.IsPrivate || _pw is null) return;
        var items = _pw.All()
            .Select(e => (object)new { id = e.Id, o = e.Origin, u = e.User, c = e.Created, m = e.Used })
            .ToArray();
        PushJson(tab, JsonSerializer.Serialize(new { b = 25, items }));
    }

    /// <summary>about:passwords with the vault baked in — same pattern as
    /// BuildHistoryHtml; b:25 keeps it live after that.</summary>
    private string BuildPasswordsHtml(bool isPrivate = false)
    {
        var items = (_pw is null || isPrivate ? Array.Empty<PasswordStore.Entry>() : _pw.All())
            .Select(e => new { id = e.Id, o = e.Origin, u = e.User, c = e.Created, m = e.Used })
            .ToArray();
        var json = JsonSerializer.Serialize(items);
        return Assets.PasswordsHtml.Replace("__BRAIN_PW_JSON__", json.Replace("<", "\\u003c"));
    }

    // ------------------------------------------------------------------
    // DevTools feed — the in-window panel polls (dtget) and the REPL
    // evals through the driver (CSP-safe unlike page-side eval()).
    // ------------------------------------------------------------------

    private async Task PushDevTools(BrowserTab tab)
    {
        if (_noToolbar || tab.IsClosed || !_driverReady.Task.IsCompleted) return;
        // macOS has no network event stream — harvest the live document's
        // performance entries on each panel poll (no-op for CDP drivers).
        try
        {
            var netDriver = (DocTab ?? tab).Driver ?? _driverTab?.Driver;
            if (netDriver is not null)
                await netDriver.PullNetworkAsync(CancellationToken.None);
        }
        catch { }
        // Windows gets console events from both CDP and the brain.js hook —
        // collapse identical entries the double feed produces (levels differ:
        // hook says "warn", Log.entryAdded says "warning" — normalize first).
        static string NormLevel(string l) => l is "warning" ? "warn" : l is "verbose" ? "debug" : l;
        var all = _console.TakeLast(200).ToArray();
        var con = all.Where((c, i) =>
        {
            for (var k = Math.Max(0, i - 10); k < i; k++)
                if (NormLevel(c.Level) == NormLevel(all[k].Level) && c.Text == all[k].Text &&
                    Math.Abs((c.Time - all[k].Time).TotalSeconds) < 2)
                    return false;
            return true;
        }).Select(c => (object)new
        {
            l = NormLevel(c.Level),
            t = c.Time.ToString("HH:mm:ss"),
            x = c.Text.Length > 2000 ? c.Text[..2000] : c.Text,
            u = c.Url,
        }).ToArray();
        var net = _network.Values.OrderBy(n => n.Time).TakeLast(300).Select(n => (object)new
        {
            id = n.RequestId,
            m = n.Method, u = n.Url.Length > 400 ? n.Url[..400] : n.Url,
            st = n.Status, ty = n.ResourceType, sz = n.BytesReceived, er = n.Error,
        }).ToArray();
        var json = JsonSerializer.Serialize(new { b = 10, con, net });
        try { InvokeOnUi(() => { if (!tab.IsClosed) SendUi(tab, json); }); }
        catch { /* window mid-close */ }
    }

    private async Task DevToolsEvalAsync(BrowserTab tab, string expr)
    {
        if (string.IsNullOrWhiteSpace(expr)) return;
        string json;
        try
        {
            var driver = await DriverOrThrowAsync(tab, CancellationToken.None);
            var v = await driver.EvalScriptAsync(expr, awaitPromise: true, CancellationToken.None);
            json = JsonSerializer.Serialize(new { b = 11, ok = true, v });
        }
        catch (Exception ex)
        {
            json = JsonSerializer.Serialize(new { b = 11, ok = false, e = ex.Message });
        }
        try { InvokeOnUi(() => { if (!tab.IsClosed) SendUi(tab, json); }); }
        catch { }
    }

    /// <summary>Full request detail for the DevTools Network pane (b:12).</summary>
    private void PushNetDetail(BrowserTab tab, string requestId)
    {
        if (_noToolbar || tab.IsClosed || !_driverReady.Task.IsCompleted) return;
        if (!_network.TryGetValue(requestId, out var n)) return;
        var json = JsonSerializer.Serialize(new
        {
            b = 12,
            id = n.RequestId,
            m = n.Method, u = n.Url, st = n.Status, ty = n.ResourceType,
            sz = n.BytesReceived, er = n.Error, t = n.Time.ToString("HH:mm:ss"),
            rq = n.RequestHeaders, rs = n.ResponseHeaders, pd = n.PostData,
        });
        try { InvokeOnUi(() => { if (!tab.IsClosed) SendUi(tab, json); }); }
        catch { }
    }

    /// <summary>Edit-and-resend: replay an arbitrary request host-side. The
    /// browser profile's cookies for the target domain are merged in, but the
    /// request bypasses page CSP/CORS like a real API client (b:13).</summary>
    private static readonly HttpClient _dtHttp = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    });

    private async Task DevToolsSendAsync(BrowserTab tab, string arg)
    {
        string json;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var doc = JsonDocument.Parse(arg);
            var r = doc.RootElement;
            var method = r.TryGetProperty("m", out var m) ? m.GetString() ?? "GET" : "GET";
            var url = r.GetProperty("u").GetString()!;
            using var req = new HttpRequestMessage(new HttpMethod(method), url);

            Dictionary<string, string>? headers = null;
            if (r.TryGetProperty("h", out var h) && h.ValueKind == JsonValueKind.Object)
            {
                headers = new(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in h.EnumerateObject())
                    headers[kv.Name] = kv.Value.GetString() ?? "";
            }
            var body = r.TryGetProperty("b", out var b) ? b.GetString() : null;
            if (body is not null && method is not "GET" and not "HEAD")
                req.Content = new StringContent(body, System.Text.Encoding.UTF8);

            // Profile cookies for the target host, unless the caller set one.
            try
            {
                var driver = await DriverOrThrowAsync(tab, CancellationToken.None);
                var uri = new Uri(url);
                var cookies = await driver.GetCookiesAsync(CancellationToken.None);
                var cstr = string.Join("; ", cookies
                    .Where(c => c.Domain is { } d &&
                        uri.Host.EndsWith(d.TrimStart('.'), StringComparison.OrdinalIgnoreCase))
                    .Select(c => c.Name + "=" + c.Value));
                if (cstr.Length > 0 && headers?.ContainsKey("Cookie") != true)
                    req.Headers.TryAddWithoutValidation("Cookie", cstr);
            }
            catch { /* cookies are best-effort */ }

            if (headers is not null)
                foreach (var (k, v) in headers)
                {
                    if (k.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                    {
                        req.Content ??= new StringContent("", System.Text.Encoding.UTF8);
                        req.Content.Headers.TryAddWithoutValidation(k, v);
                    }
                    else if (!req.Headers.TryAddWithoutValidation(k, v))
                        req.Content?.Headers.TryAddWithoutValidation(k, v);
                }
            req.Headers.TryAddWithoutValidation("User-Agent", "BrAIn-DevTools/1.0");

            using var resp = await _dtHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            var buf = await resp.Content.ReadAsByteArrayAsync();
            string? bodyText;
            if (buf.Take(8000).Any(b2 => b2 == 0))
                bodyText = null; // binary
            else
                bodyText = buf.Length > 131072
                    ? System.Text.Encoding.UTF8.GetString(buf, 0, 131072) + "\n…truncated"
                    : System.Text.Encoding.UTF8.GetString(buf);
            var rheads = resp.Headers.Concat(resp.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
                .GroupBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Take(60)
                .ToDictionary(g => g.Key, g => string.Join(", ", g.SelectMany(x => x.Value)), StringComparer.OrdinalIgnoreCase);
            json = JsonSerializer.Serialize(new
            {
                b = 13, ok = true,
                st = (int)resp.StatusCode,
                ms = sw.ElapsedMilliseconds,
                sz = buf.LongLength,
                rh = rheads,
                body = bodyText,
                bin = bodyText is null,
            });
        }
        catch (Exception ex)
        {
            json = JsonSerializer.Serialize(new { b = 13, ok = false, e = ex.Message, ms = sw.ElapsedMilliseconds });
        }
        try { InvokeOnUi(() => { if (!tab.IsClosed) SendUi(tab, json); }); }
        catch { }
    }

    /// <summary>All profile cookies for the DevTools Cookies pane (b:14).
    /// The pane filters by the active document's host client-side.</summary>
    private async Task PushDtCookiesAsync(BrowserTab tab)
    {
        if (_noToolbar || tab.IsClosed || !_driverReady.Task.IsCompleted) return;
        string json;
        try
        {
            var driver = await DriverOrThrowAsync(tab, CancellationToken.None);
            var cookies = await driver.GetCookiesAsync(CancellationToken.None);
            string? host = null;
            try { host = new Uri(DocTab?.LastUrl ?? tab.LastUrl ?? "").Host; } catch { }
            json = JsonSerializer.Serialize(new
            {
                b = 14,
                host,
                ck = cookies.OrderBy(c => c.Domain).ThenBy(c => c.Name).Select(c => new
                {
                    // document.cookie (macOS) carries no domain — these are
                    // by definition the current site's cookies; attribute the
                    // host so the pane's site filter and editor see them.
                    n = c.Name, v = c.Value, d = c.Domain ?? host, p = c.Path,
                    e = c.Expires, h = c.HttpOnly, s = c.Secure,
                }),
            });
        }
        catch (Exception ex)
        {
            json = JsonSerializer.Serialize(new { b = 14, err = ex.Message });
        }
        try { InvokeOnUi(() => { if (!tab.IsClosed) SendUi(tab, json); }); }
        catch { }
    }

    /// <summary>Delete one cookie (name + scope) then re-push the list.</summary>
    private async Task DtCookieDeleteAsync(BrowserTab tab, string arg)
    {
        try
        {
            using var doc = JsonDocument.Parse(arg);
            var r = doc.RootElement;
            var name = r.GetProperty("n").GetString()!;
            var dom = r.TryGetProperty("d", out var d) ? d.GetString() : null;
            var path = r.TryGetProperty("p", out var p) ? p.GetString() : null;
            var secure = r.TryGetProperty("s", out var s) && s.GetBoolean();
            var driver = await DriverOrThrowAsync(tab, CancellationToken.None);
            var url = dom is { Length: > 0 }
                ? (secure ? "https://" : "http://") + dom.TrimStart('.') + (path ?? "/")
                : null;
            await driver.DeleteCookiesAsync(name, url, CancellationToken.None);
        }
        catch { }
        await PushDtCookiesAsync(tab);
    }

    /// <summary>Set/overwrite a cookie from the pane's editor, then re-push.</summary>
    private async Task DtCookieSetAsync(BrowserTab tab, string arg)
    {
        try
        {
            using var doc = JsonDocument.Parse(arg);
            var r = doc.RootElement;
            var driver = await DriverOrThrowAsync(tab, CancellationToken.None);
            await driver.SetCookieAsync(new CookieInfo(
                r.GetProperty("n").GetString()!,
                r.TryGetProperty("v", out var v) ? v.GetString() ?? "" : "",
                r.TryGetProperty("d", out var d) ? d.GetString() : null,
                r.TryGetProperty("p", out var p) ? p.GetString() : null,
                r.TryGetProperty("e", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : null,
                r.TryGetProperty("h", out var h) && h.GetBoolean(),
                r.TryGetProperty("s", out var s) && s.GetBoolean()), CancellationToken.None);
        }
        catch { }
        await PushDtCookiesAsync(tab);
    }

    /// <summary>Session browser for the DevTools Sessions pane (b:15): open
    /// tabs, recently closed entries, and the last saved session.</summary>
    private void PushDtSessions(BrowserTab tab)
    {
        if (_noToolbar || tab.IsClosed || !_driverReady.Task.IsCompleted) return;
        var activeId = Tabs.Active?.Id;
        var open = Tabs.All.Where(t => !t.IsClosed).Select(t => new
        {
            id = t.Id.ToString("N")[..8],
            t = t.LastTitle ?? t.LastUrl,
            u = t.LastUrl,
            i = t.LastIcon,
            act = t.Id == activeId,
            priv = t.IsPrivate,
            pin = t.IsPinned,
        }).ToArray();
        var closed = _closedTabs.Select(c => new { u = c.Url, i = c.Icon }).ToArray();
        var json = JsonSerializer.Serialize(new { b = 15, open, closed, saved = _savedSession });
        try { InvokeOnUi(() => { if (!tab.IsClosed) SendUi(tab, json); }); }
        catch { }
    }

    /// <summary>Inject brain.js (+ toolbar) into the tab's current document if absent.</summary>
    public async Task EnsurePageHelpersAsync(BrowserTab tab)
    {
        // Logical tabs share one webview — always drive through the bound
        // driver, not tab.Driver (null on non-main logical tabs, which made
        // internal about: pages lose the chrome bar entirely).
        var driver = PerTab ? tab.Driver : tab.Driver ?? _driverTab?.Driver;
        if (driver is null || !driver.SupportsEval) return;
        // Evals submitted while WKWebView is mid-navigation get dropped with
        // no reply — cap the wait so driver init and post-load injection
        // aren't stalled for the full 30s eval timeout.
        using var hcts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        try
        {
            var installed = await driver.EvaluateAsync(
                "window.__brain && window.__brain.v === " + Assets.BrainJsVersion, false, hcts.Token);
            if (installed?.ValueKind == JsonValueKind.True)
            {
                if (!_noToolbar)
                    await driver.EvaluateAsync("window.__brainChrome && window.__brainChrome.mount()", false, hcts.Token);
                return;
            }
            var script = PageLibSource;
            await driver.EvalScriptAsync(script, false, hcts.Token);
        }
        catch (Exception ex) { Log($"helper inject failed: {ex.Message}"); }
    }

    /// <summary>Favicons belong to a document, not the tab — when the URL
    /// moves to a different site, drop the old icon so it can't linger on the
    /// new page if the next refresh eval is dropped mid-navigation.
    /// Same-host navs keep it (pages on a site share the icon).</summary>
    internal static void SetTabUrl(BrowserTab tab, string url)
    {
        try
        {
            var o = new Uri(tab.LastUrl); var n = new Uri(url);
            if (!o.Authority.Equals(n.Authority, StringComparison.OrdinalIgnoreCase)
                || o.Scheme != n.Scheme)
                tab.LastIcon = null;
        }
        catch { if (tab.LastUrl != url) tab.LastIcon = null; }
        tab.LastUrl = url;
    }

    public async Task RefreshTabInfoAsync(BrowserTab tab)
    {
        // tab.Driver == null means the tab has never owned the shared
        // webview — evaluating it would stamp another tab's url/title/icon
        // onto this one.
        var driver = tab.Driver;
        if (driver is null || !driver.SupportsEval) return;
        using var rcts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        try
        {
            var info = await driver.EvaluateAsync(
                """
                JSON.stringify({
                  u: location.href, t: document.title,
                  n: !!window.__brainNewTab, h: !!window.__brainHelp, y: !!window.__brainHist,
                  p: !!window.__brainImport, w: !!window.__brainPw, f: !!window.__brainFlows,
                  s: !!window.__brainShortcuts,
                  i: (document.querySelector("link[rel~='icon']") || document.querySelector("link[rel='shortcut icon']") || {}).href ||
                     (/^https?:$/.test(location.protocol) ? location.origin + '/favicon.ico' : '')
                })
                """, false, rcts.Token);
            if (info?.ValueKind == JsonValueKind.String)
            {
                using var doc = JsonDocument.Parse(info.Value.GetString()!);
                var root = doc.RootElement;
                // Still the previous owner's page (tab just switched) — not this tab's info.
                if (ReferenceEquals(tab, DocTab) && IsStaleDocUrl(root.GetProperty("u").GetString())) return;
                // Our start page is served via LoadRawString, so location.href
                // reads about:blank — the doc flags itself so tab switching
                // reloads the start page instead of a literal blank page.
                var isNt = root.TryGetProperty("n", out var n) && n.GetBoolean();
                var isHelp = root.TryGetProperty("h", out var h) && h.GetBoolean();
                var isHist = root.TryGetProperty("y", out var y) && y.GetBoolean();
                var isImp = root.TryGetProperty("p", out var pp) && pp.GetBoolean();
                var isPw = root.TryGetProperty("w", out var ww) && ww.GetBoolean();
                var isFlows = root.TryGetProperty("f", out var ff) && ff.GetBoolean();
                var isSc = root.TryGetProperty("s", out var ss) && ss.GetBoolean();
                SetTabUrl(tab, isNt ? "about:newtab" : isHelp ? "about:help" : isSc ? "about:shortcuts" : isHist ? "about:history" : isImp ? "about:import" : isPw ? "about:passwords" : isFlows ? "about:flows" : root.GetProperty("u").GetString() ?? tab.LastUrl);
                tab.LastTitle = isNt ? "New Tab" : isHelp ? "How to use" : isSc ? "Keyboard shortcuts" : isHist ? "History" : isImp ? "Import browser data" : isPw ? "Passwords" : isFlows ? "Flows & activity" : root.GetProperty("t").GetString();
                // u and i come from the same document — always reassign so a
                // cross-site navigation can never keep the old site's icon.
                var iRaw = root.GetProperty("i").GetString();
                // "data:," is an intentionally empty favicon (example.com uses
                // it to suppress icon fetches) — treat as no icon so the tab
                // face falls back to the letter instead of a blank img.
                tab.LastIcon = !string.IsNullOrEmpty(iRaw)
                    && !(iRaw.StartsWith("data:") && iRaw.IndexOf(',') == iRaw.Length - 1)
                    ? iRaw : null;
                if (PerTab) (tab.CanBack, tab.CanFwd) = await driver.GetNavStateAsync(rcts.Token);
                else (_canBack, _canFwd) = await driver.GetNavStateAsync(rcts.Token);
                if (!tab.IsPrivate) RecordHistory(tab.LastUrl, tab.LastTitle, tab.LastIcon);
                SaveSessionSoon();
                if (Tabs.Active?.Id == tab.Id) SetWindowTitleFor(tab);
                PushChromeStateAll();
            }
        }
        catch { /* page mid-navigation */ }
    }

    private void RecordHistory(string url, string? title, string? icon)
    {
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return;
        lock (_history)
        {
            var i = _history.FindIndex(h => h.Url == url);
            var old = i >= 0 ? _history[i] : null;
            if (i >= 0) _history.RemoveAt(i);
            _history.Insert(0, new HistoryEntry(
                url,
                string.IsNullOrWhiteSpace(title) ? old?.Title : title,
                icon ?? old?.Icon,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                (old?.Visits ?? 0) + 1));
            if (_history.Count > HistoryCap) _history.RemoveAt(HistoryCap);
            SaveHistoryStore();
        }
    }

    private void SaveHistoryStore()
    {
        SaveStore("history.json",
            _history.Select(h => new { u = h.Url, t = h.Title, i = h.Icon, d = h.Time, v = h.Visits }));
    }

    /// <summary>about:history row delete (one URL) / clear — the page also
    /// re-fetches via histget, and b:9 refresh updates the omnibox tiles.</summary>
    private void DeleteHistory(string url)
    {
        lock (_history)
        {
            if (_history.RemoveAll(h => h.Url == url) == 0) return;
            SaveHistoryStore();
        }
        PushChromeStateAll();
    }

    private void ClearHistory()
    {
        lock (_history)
        {
            if (_history.Count == 0) return;
            _history.Clear();
            SaveHistoryStore();
        }
        PushChromeStateAll();
    }


    // ---------------- flows & activity (about:flows) ----------------

    private static readonly JsonSerializerOptions FlowUiJson = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private int _flowsPushPending;
    private (bool Ok, string Text, long T)? _flowNotice;

    /// <summary>Everything the page needs in one payload (b:30). The activity
    /// rows are trimmed; full detail is fetched per row on expand (actget).</summary>
    private string FlowsStateJson()
    {
        var rows = Activity.Recent(250).Select(e => new
        {
            id = e.Id, t = e.T, tool = e.Tool, ok = e.Ok, ms = e.Ms, url = e.Url, masked = e.Masked, flow = e.Flow,
            a = BrAIn.Mcp.ActivityLog.ArgsSummary(e),
            r = BrAIn.Mcp.ActivityLog.Clip(e.Res ?? "", 160),
        }).ToArray();
        return JsonSerializer.Serialize(new
        {
            b = 30,
            flows = Flows.List(),
            pending = Flows.Pending(),
            tools = BrAIn.Mcp.FlowService.AllowedTools,
            activity = rows,
            notice = _flowNotice is { } n ? new { ok = n.Ok, text = n.Text, t = n.T } : null,
        }, FlowUiJson);
    }

    private void PushFlowsState(BrowserTab tab)
    {
        if (tab.IsClosed) return;
        PushJson(tab, FlowsStateJson());
    }

    /// <summary>Debounced: a burst of tool calls becomes one push to the open page.</summary>
    private void ScheduleFlowsPush()
    {
        if (Interlocked.Exchange(ref _flowsPushPending, 1) == 1) return;
        _ = Task.Delay(400).ContinueWith(_ =>
        {
            Interlocked.Exchange(ref _flowsPushPending, 0);
            try
            {
                var t = Tabs.Active;
                if (t is not null && !t.IsClosed && t.LastUrl == "about:flows") PushFlowsState(t);
            }
            catch { }
        }, TaskScheduler.Default);
    }

    private string BuildFlowsHtml()
    {
        var json = FlowsStateJson();
        return Assets.FlowsHtml.Replace("__BRAIN_FLOWS_JSON__", json.Replace("<", "\\u003c"));
    }

    private void FlowUiNotice(BrowserTab tab, bool ok, string text)
    {
        // Kept in state so it survives the page being rebuilt (tabs share one webview).
        _flowNotice = (ok, text, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        PushJson(tab, JsonSerializer.Serialize(new { b = 31, ok, text, t = _flowNotice.Value.T }, FlowUiJson));
    }

    private sealed record FlowSaveReq(BrAIn.Mcp.FlowDef? Def, string? Old);
    private sealed record FlowApproveReq(string? Id, BrAIn.Mcp.FlowDef? Def);
    private sealed record FlowRunReq(string? Name, System.Text.Json.Nodes.JsonObject? Args);
    private sealed record FlowDraftReq(string[]? Ids, string? Name, string? Description);

    private void FlowUiSave(BrowserTab tab, string arg)
    {
        try
        {
            var req = JsonSerializer.Deserialize<FlowSaveReq>(arg, FlowUiJson);
            if (req?.Def is null) { FlowUiNotice(tab, false, "Nothing to save."); return; }
            req.Def.Source ??= "manual";
            var err = Flows.Save(req.Def, req.Old);
            FlowUiNotice(tab, err is null, err ?? $"Saved flow_{req.Def.Name}.");
        }
        catch (Exception ex) { FlowUiNotice(tab, false, "Could not save: " + ex.Message); }
        PushFlowsState(tab);
    }

    private void FlowUiApprove(BrowserTab tab, string arg)
    {
        try
        {
            var req = JsonSerializer.Deserialize<FlowApproveReq>(arg, FlowUiJson);
            if (req?.Id is null) { FlowUiNotice(tab, false, "Missing proposal id."); return; }
            var err = Flows.Approve(req.Id, req.Def);
            FlowUiNotice(tab, err is null, err ?? "Approved. The flow is now available to agents.");
        }
        catch (Exception ex) { FlowUiNotice(tab, false, "Could not approve: " + ex.Message); }
        PushFlowsState(tab);
    }

    private async Task FlowUiRunAsync(BrowserTab tab, string arg)
    {
        try
        {
            var req = JsonSerializer.Deserialize<FlowRunReq>(arg, FlowUiJson);
            var def = req?.Name is null ? null : Flows.Get(req.Name);
            if (def is null) { FlowUiNotice(tab, false, "Flow not found."); return; }
            FlowUiNotice(tab, true, $"Running flow_{def.Name}…");
            // Open a fresh tab for the run so this page stays where it is.
            var runTab = await NewTabAsync(null, default);
            var res = await Flows.RunAsync(def, req!.Args, default, honorPause: false);
            // Back to the Flows page, which shows the result from state.
            try { await ActivateWindowAsync(tab.Id.ToString("N")[..8], default); } catch { }
            FlowUiNotice(tab, res.Ok, res.Text);
            if (res.Ok) Activity.Add(new BrAIn.Mcp.ActivityEntry
            {
                Id = Guid.NewGuid().ToString("N")[..8], T = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Tool = "flow_" + def.Name, Args = req.Args, Ok = true, Url = runTab.LastUrl, Flow = true,
                Res = BrAIn.Mcp.ActivityLog.Clip("(run from Flows page) " + res.Text, 500),
            });
        }
        catch (Exception ex) { FlowUiNotice(tab, false, "Run failed: " + ex.Message); }
    }

    private void FlowUiDraft(BrowserTab tab, string arg)
    {
        try
        {
            var req = JsonSerializer.Deserialize<FlowDraftReq>(arg, FlowUiJson);
            var entries = Activity.Find(req?.Ids ?? Array.Empty<string>());
            if (entries.Count == 0) { FlowUiNotice(tab, false, "Select at least one call."); return; }
            var def = Flows.BuildFromActivity(entries, req?.Name ?? "new_flow", req?.Description ?? "", null);
            def.Source = "manual";
            if (def.Steps.Count == 0) { FlowUiNotice(tab, false, "Those calls are read-only or failed, so there is nothing to replay. Pick clicks, typing or navigation."); return; }
            PushJson(tab, JsonSerializer.Serialize(new { b = 33, draft = def }, FlowUiJson));
        }
        catch (Exception ex) { FlowUiNotice(tab, false, "Could not build a flow: " + ex.Message); }
    }

    private void FlowUiActivityDetail(BrowserTab tab, string id)
    {
        var e = Activity.Find(new[] { id }).FirstOrDefault();
        if (e is null) return;
        PushJson(tab, JsonSerializer.Serialize(new { b = 34, entry = e }, FlowUiJson));
    }

    /// <summary>Full history list for the about:history page (b:16).</summary>
    private void PushHistory(BrowserTab tab)
    {
        if (tab.IsClosed) return;
        object[] items;
        lock (_history)
            items = _history.Select(h => (object)new { u = h.Url, t = h.Title, i = h.Icon, d = h.Time, v = h.Visits }).ToArray();
        var json = JsonSerializer.Serialize(new { b = 16, items });
        try { InvokeOnUi(() => { if (!tab.IsClosed) SendUi(tab, json); }); }
        catch { /* window mid-close */ }
    }

    /// <summary>about:history with the current list baked in — the page also
    /// sends histget on load and re-renders on b:16 pushes.</summary>
    private string BuildHistoryHtml()
    {
        string json;
        lock (_history)
            json = JsonSerializer.Serialize(
                _history.Select(h => new { u = h.Url, t = h.Title, i = h.Icon, d = h.Time, v = h.Visits }));
        return Assets.HistoryHtml.Replace("__BRAIN_HISTORY_JSON__", json.Replace("<", "\\u003c"));
    }

    // ---------------- browser data import (about:import) ----------------

    private List<BrowserSource>? _importScan;
    private sealed record ImportPick(string id, bool hist, bool bm, bool ck, bool pw);

    /// <summary>about:import asks which browsers are installed — b:17 pushes
    /// the detected sources with per-store counts so the page can render
    /// pickers. Runs off-thread: SQLite copies + a possible Keychain prompt
    /// mustn't stall the message loop.</summary>
    private async Task ImportScanAsync(BrowserTab tab)
    {
        List<BrowserSource> sources;
        try { sources = await Task.Run(BrowserImporter.Scan); }
        catch (Exception ex) { Log($"import scan failed: {ex.Message}"); sources = new(); }
        _importScan = sources;
        var json = JsonSerializer.Serialize(new
        {
            b = 17,
            browsers = sources.Select(s => new
            {
                id = s.Id, name = s.Name, kind = s.Kind,
                hist = s.Hist, bm = s.Bm, ck = s.Ck, pw = s.Pw, note = s.Note,
            }).ToArray(),
        });
        try { InvokeOnUi(() => { if (!tab.IsClosed) SendUi(tab, json); }); }
        catch { /* window mid-close */ }
    }

    /// <summary>Runs the selected imports and reports per-source counts via
    /// b:18. Cookie values are never serialized — they go straight from the
    /// source database into the native cookie store.</summary>
    private async Task ImportRunAsync(BrowserTab tab, string arg)
    {
        List<ImportPick> picks;
        try { picks = JsonSerializer.Deserialize<List<ImportPick>>(arg) ?? new(); }
        catch { return; }
        var results = new List<object>();
        foreach (var pick in picks)
        {
            var src = _importScan?.FirstOrDefault(s => s.Id == pick.id);
            if (src is null || (!pick.hist && !pick.bm && !pick.ck && !pick.pw)) continue;
            int hist = -1, bm = -1, ck = -1, pw = -1;
            string? err = null;
            try
            {
                if (pick.hist)
                    hist = MergeImportedHistory(await Task.Run(() => BrowserImporter.ReadHistory(src)));
                if (pick.bm)
                    bm = MergeImportedBookmarks(await Task.Run(() => BrowserImporter.ReadBookmarks(src)));
                if (pick.ck)
                {
                    // Expired cookies are useless — filter before touching the store.
                    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    var cookies = (await Task.Run(() => BrowserImporter.ReadCookies(src)))
                        .Where(c => c.Expires is null || c.Expires.Value > now).ToList();
                    var driver = tab.Driver ?? _driverTab?.Driver;
                    ck = driver is null ? -1 : await driver.ImportCookiesAsync(cookies, default);
                    if (ck < 0)
                        Log($"cookie import skipped for {src.Name}: no cookie-store write on this platform");
                    else
                        Log($"cookie import {src.Name}: read {cookies.Count}, wrote {ck}");
                }
                if (pick.pw)
                {
                    // Plaintext goes straight into the encrypted vault — never
                    // serialized to the page. Upsert dedupes by origin+user.
                    var logins = await Task.Run(() => BrowserImporter.ReadPasswords(src));
                    pw = 0;
                    if (_pw is null) { pw = -1; }
                    else foreach (var l in logins)
                    {
                        var o = OriginOf(l.Origin);
                        if (o is null) continue;
                        _pw.Upsert(o, l.User, l.Pass);
                        pw++;
                    }
                    Log($"password import {src.Name}: read {logins.Count}, merged {pw}");
                }
            }
            catch (Exception ex) { err = ex.Message; }
            results.Add(new { id = src.Id, name = src.Name, hist, bm, ck, pw, err });
        }
        var json = JsonSerializer.Serialize(new { b = 18, results });
        try { InvokeOnUi(() => { if (!tab.IsClosed) SendUi(tab, json); }); }
        catch { }
        PushChromeStateAll();
        PushHistory(tab);
    }

    /// <summary>Imported URLs merge into the existing list: known URLs keep
    /// their entry but take the newer timestamp / higher visit count / a
    /// title if ours is empty. New URLs append, then everything re-sorts by
    /// recency. Returns the count of URLs not previously known.</summary>
    private int MergeImportedHistory(List<HistoryEntry> imported)
    {
        var added = 0;
        lock (_history)
        {
            foreach (var e in imported)
            {
                var i = _history.FindIndex(h => h.Url == e.Url);
                if (i >= 0)
                {
                    var old = _history[i];
                    _history[i] = old with
                    {
                        Title = string.IsNullOrWhiteSpace(old.Title) ? e.Title : old.Title,
                        Time = Math.Max(old.Time, e.Time),
                        Visits = Math.Max(old.Visits, e.Visits),
                    };
                }
                else { _history.Add(e); added++; }
            }
            _history.Sort((a, b) => b.Time.CompareTo(a.Time));
            if (_history.Count > HistoryCap)
                _history.RemoveRange(HistoryCap, _history.Count - HistoryCap);
            if (imported.Count > 0) SaveHistoryStore();
        }
        return added;
    }

    /// <summary>Bookmarks merge by URL; imported entries append after the
    /// user's own (they're the imported browser's pins, not ours).</summary>
    private int MergeImportedBookmarks(List<ImportedBookmark> imported)
    {
        var added = 0;
        lock (_bookmarks)
        {
            var have = _bookmarks.Select(b => b.Url).ToHashSet();
            foreach (var b in imported)
                if (have.Add(b.Url)) { _bookmarks.Add((b.Url, b.Title, null)); added++; }
            if (added == 0) return 0;
            if (_bookmarks.Count > BookmarkCap)
                _bookmarks.RemoveRange(BookmarkCap, _bookmarks.Count - BookmarkCap);
            SaveStore("bookmarks.json",
                _bookmarks.Select(b => new { u = b.Url, t = b.Title, i = b.Icon }));
        }
        return added;
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    public BrowserTab ActiveTabOrThrow()
        => Tabs.Active ?? throw new InvalidOperationException("No browser tab available");

    // ------------------------------------------------------------------
    // Blank-page watchdog. A web page that finishes "loading" but renders
    // nothing (no visible text, near-empty DOM) is reloaded once; every check
    // is logged to stderr so a recurring blank page can be diagnosed.
    // ------------------------------------------------------------------
    private string? _blankRetryUrl;
    private long _blankRetryAt;

    private async Task BlankWatchdogAsync(BrowserTab target)
    {
        try
        {
            await Task.Delay(2500);
            var d = target.Driver;
            if (d is not { SupportsEval: true } || target.IsClosed) return;
            var url = target.LastUrl ?? "";
            if (!(url.StartsWith("http://") || url.StartsWith("https://"))) return;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            var r = await d.EvaluateAsync(
                "(function(){var b=document.body,h=document.documentElement?document.documentElement.outerHTML:'';" +
                "var n=(performance.getEntriesByType&&performance.getEntriesByType('navigation')[0])||{};" +
                "return JSON.stringify({u:location.href,rs:document.readyState,html:h.length," +
                "text:b?(b.innerText||'').trim().length:-1,kids:b?b.children.length:-1,title:document.title," +
                "nav:{type:n.type,status:n.responseStatus,xfer:n.transferSize,body:n.encodedBodySize,dur:Math.round(n.duration||0)}," +
                "head:h.slice(0,260),tail:h.slice(-200)})})()",
                false, cts.Token);
            var info = r?.ValueKind == JsonValueKind.String ? r.Value.GetString() ?? "" : r?.ToString() ?? "";
            using var doc = JsonDocument.Parse(info);
            var root = doc.RootElement;
            var text = root.GetProperty("text").GetInt32();
            var kids = root.GetProperty("kids").GetInt32();
            var ready = root.GetProperty("rs").GetString();
            // Finished loading but nothing in <body>: the document was cut short.
            var blank = ready == "complete" && text <= 0 && kids <= 0;
            Log($"page check {(blank ? "BLANK" : "ok")} {(blank ? info : info[..Math.Min(info.Length, 200)])}");
            if (!blank) return;
            var now = Environment.TickCount64;
            if (_blankRetryUrl == url && now - _blankRetryAt < 20000) return; // one retry per url
            _blankRetryUrl = url; _blankRetryAt = now;
            Log("blank page detected — reloading once");
            await d.EvaluateAsync("location.reload()", false, cts.Token);
        }
        catch (Exception ex) { Log("page check failed: " + ex.Message); }
    }

    /// <summary>Every tab owns a live view (macOS split mode, Windows WebView2) — switching never reloads.</summary>
    public bool PerTabViews => PerTab;
    /// <summary>Windows per-tab views are capped (see <see cref="MaxLiveViews"/>) and evicted when over.</summary>
    public bool ViewEvictionActive => WinPerTab && MaxLiveViews != int.MaxValue;
    public bool IsLoading => PerTab ? Tabs.Active?.Loading == true : _loadDepth > 0;
    public (bool CanBack, bool CanFwd) NavState =>
        PerTab ? (Tabs.Active?.CanBack == true, Tabs.Active?.CanFwd == true) : (_canBack, _canFwd);

    /// <param name="materialize">A virtual (overlay) new tab gets its real
    /// start-page document loaded first, so automation sees the page the
    /// user sees rather than the document hidden underneath.</param>
    public async Task<IBrowserDriver> DriverOrThrowAsync(BrowserTab tab, CancellationToken ct, bool materialize = true)
    {
        await _windowCreated.Task.WaitAsync(ct);
        if (PerTab)
        {
            // Every tab has its own view; operating on a background tab does not switch to it.
            if (tab.IsClosed) throw new InvalidOperationException("Tab is closed");
            await _driverReady.Task.WaitAsync(TimeSpan.FromSeconds(25), ct);
            var pd = tab.Driver ?? await EnsureTabViewAsync(tab);
            if (WinPerTab)
            {
                tab.LastActiveUtc = DateTime.UtcNow; // in use by an agent: not an eviction candidate
                // an evicted tab comes back (reloaded) and a suspended one wakes up before it is driven
                await RestoreIfNeededAsync(tab);
                if (tab.WinView is Win.WinTabs.View wv) _winTabs?.EnsureResumed(wv);
            }
            return tab.Driver ?? pd;
        }
        if (tab.Driver is null)
        {
            if (!tab.IsMain)
            {
                // Logical tabs share the main window's webview and driver.
                // _driverTab survives removal from the registry, so closing
                // the original main tab doesn't orphan the channel.
                var src = _driverTab ?? throw new InvalidOperationException("Browser automation channel not ready");
                await src.Bound.Task.WaitAsync(TimeSpan.FromSeconds(25), ct);
                tab.Driver = src.Driver ?? throw new InvalidOperationException("Browser automation channel not ready");
                tab.Bound.TrySetResult();
            }
            else
            {
                await tab.Bound.Task.WaitAsync(TimeSpan.FromSeconds(25), ct);
                if (tab.Driver is null)
                    throw new InvalidOperationException("Browser automation channel not ready for this tab");
            }
        }
        // Single-window model: the window always shows the ACTIVE tab, so an
        // operation on a background tab activates it first (loads its URL).
        if (Tabs.Active?.Id != tab.Id && !tab.IsClosed)
            await ActivateWindowAsync(tab.Id.ToString("N")[..8], ct);
        if (materialize && !tab.IsClosed && tab.Id != _docTabId && tab.LastUrl == "about:newtab")
            await LoadInternalPageAsync(tab, "about:newtab");
        return tab.Driver;
    }

    internal T InvokeOnUi<T>(Func<T> f)
    {
        var win = _mainWindow ?? throw new InvalidOperationException("No window");
        T result = default!;
        win.Invoke(() => result = f());
        return result;
    }

    internal void InvokeOnUi(Action a)
    {
        var win = _mainWindow ?? throw new InvalidOperationException("No window");
        win.Invoke(a);
    }

    internal static string JsonToText(JsonElement? el)
    {
        if (el is null) return "null";
        var v = el.Value;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Null or JsonValueKind.Undefined => "null",
            _ => v.GetRawText(),
        };
    }

    internal string NormalizeUrl(string url)
    {
        url = url.Trim();
        if (url.Equals("newtab", StringComparison.OrdinalIgnoreCase) ||
            url.Equals("home", StringComparison.OrdinalIgnoreCase))
            return "about:newtab";
        if (url.Equals("help", StringComparison.OrdinalIgnoreCase))
            return "about:help";
        if (url.Equals("shortcuts", StringComparison.OrdinalIgnoreCase) ||
            url.Equals("keys", StringComparison.OrdinalIgnoreCase))
            return "about:shortcuts";
        if (url.Equals("history", StringComparison.OrdinalIgnoreCase))
            return "about:history";
        if (url.Equals("import", StringComparison.OrdinalIgnoreCase))
            return "about:import";
        if (url.Equals("flows", StringComparison.OrdinalIgnoreCase) ||
            url.Equals("activity", StringComparison.OrdinalIgnoreCase))
            return "about:flows";
        if (url.Equals("passwords", StringComparison.OrdinalIgnoreCase) ||
            url.Equals("password", StringComparison.OrdinalIgnoreCase))
            return "about:passwords";
        if (!url.Contains("://") && !url.StartsWith("about:") && !url.StartsWith("file:") &&
            !url.StartsWith("data:") && !url.StartsWith("javascript:"))
        {
            // Looks like a search or bare domain
            if (url.Contains('.') && !url.Contains(' '))
                url = "https://" + url;
            else
                url = _engine.Query + Uri.EscapeDataString(url);
        }
        if (url == "about:newtab") return url; // caller renders start page
        return url;
    }

    private static int FindFreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static void Enqueue<T>(ConcurrentQueue<T> q, T item)
    {
        q.Enqueue(item);
        while (q.Count > LogCap && q.TryDequeue(out _)) { }
    }

    private static readonly object _logLock = new();
    private static StreamWriter? _logFile;

    /// <summary>stderr (stdout carries MCP) and, on Windows — where the GUI-subsystem exe has no console —
    /// a log file next to the other app data (%LOCALAPPDATA%\BrAIn\brain.log, reset when it passes 2 MB).</summary>
    internal static void Log(string msg)
    {
        var line = $"[BrAIn] {msg}";
        try { Console.Error.WriteLine(line); } catch { }
        if (!IsWindows && Environment.GetEnvironmentVariable("BRAIN_LOG") is null) return;
        try
        {
            lock (_logLock)
            {
                if (_logFile is null)
                {
                    var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrAIn");
                    Directory.CreateDirectory(dir);
                    var path = Path.Combine(dir, "brain.log");
                    if (File.Exists(path) && new FileInfo(path).Length > 2_000_000) File.Delete(path);
                    _logFile = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
                    _logFile.WriteLine($"---- {DateTime.Now:yyyy-MM-dd HH:mm:ss} pid {Environment.ProcessId} ----");
                }
                _logFile.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {msg}");
            }
        }
        catch { /* logging must never throw */ }
    }

    public async ValueTask DisposeAsync()
    {
        try { if (_cdpBrowser is not null) await _cdpBrowser.DisposeAsync(); } catch { }
    }

    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}

public sealed class JavaScriptException(string message) : Exception(message);

internal static class Assets
{
    private static string Load(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource {name} missing");
        using var r = new StreamReader(stream);
        return r.ReadToEnd();
    }

    private static readonly Lazy<string> _brainJs = new(() => Load("BrAIn.Assets.brain.js"));
    private static readonly Lazy<string> _chromeJs = new(() => Load("BrAIn.Assets.chrome.js"));
    private static readonly Lazy<string> _theme = new(() => Load("BrAIn.Assets.theme.css"));
    // Internal pages share one stylesheet, inlined where they say /*@THEME*/.
    private static string Themed(string name) => Load(name).Replace("/*@THEME*/", _theme.Value);
    private static readonly Lazy<string> _newTab = new(() => Themed("BrAIn.Assets.newtab.html"));
    private static readonly Lazy<string> _help = new(() => Themed("BrAIn.Assets.help.html"));
    private static readonly Lazy<string> _sc = new(() => Themed("BrAIn.Assets.shortcuts.html"));
    private static readonly Lazy<string> _hist = new(() => Themed("BrAIn.Assets.history.html"));
    private static readonly Lazy<string> _imp = new(() => Themed("BrAIn.Assets.import.html"));
    private static readonly Lazy<string> _pw = new(() => Themed("BrAIn.Assets.passwords.html"));
    private static readonly Lazy<string> _flows = new(() => Themed("BrAIn.Assets.flows.html"));
    private static readonly Lazy<string> _combinedLib = new(() => BrainJs + "\n" + ChromeJs);

    public static string BrainJs => _brainJs.Value;
    public static string ChromeJs => _chromeJs.Value;
    public static string CombinedLib => _combinedLib.Value;

    public static string NewTabHtml => _newTab.Value;
    public static string HelpHtml => _help.Value;
    public static string ShortcutsHtml => _sc.Value;
    public static string HistoryHtml => _hist.Value;
    public static string FlowsHtml => _flows.Value;
    public static string ImportHtml => _imp.Value;
    public static string PasswordsHtml => _pw.Value;
    public const int BrainJsVersion = 3;
}
