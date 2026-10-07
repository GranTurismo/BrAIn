using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using BrAIn.Browser.Drivers;
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
    private readonly ConcurrentQueue<NetworkEntry> _network = new();
    private const int LogCap = 500;

    // Chrome state: loading spinner, nav availability, visit history (most
    // recent first) feeding the omnibox dropdown and the new-tab tiles.
    // _loadDepth counts overlapping loads — a tab switch aborts one load and
    // starts another, so a plain boolean gets clobbered by the stale stop.
    private int _loadDepth;
    private bool _canBack;
    private bool _canFwd;
    private readonly List<(string Url, string? Icon)> _history = new();
    private const int HistoryCap = 40;
    private readonly ConcurrentStack<(string Url, string? Icon)> _closedTabs = new();

    // Browser-level state shared across tabs (one window = one zoom level).
    private int _zoom = 100;
    private bool _topmost;
    // 8-char id prefix of the tab that was just created — baked into the next
    // document's boot state so only it plays the slide-in animation.
    private string? _pendingFresh;
    private static readonly int[] ZoomSteps = [25, 33, 50, 67, 75, 80, 90, 100, 110, 125, 150, 175, 200, 250, 300, 400, 500];

    private readonly List<(string Url, string? Title, string? Icon)> _bookmarks = new();
    private const int BookmarkCap = 100;

    // URLs open when the previous run exited — offered via "Restore session".
    // The file is continuously overwritten while running, so keep the startup
    // copy here and never reload it.
    private List<string> _savedSession = new();
    private int _sessionDirty;

    /// <summary>%LOCALAPPDATA%\BrAIn — parent of the per-process profile dirs,
    /// shared across runs so bookmarks/history persist.</summary>
    private readonly string _storeDir;

    private Cdp.CdpClient? _cdpBrowser; // Windows: browser-level CDP connection
    /// <summary>The tab object that owns the shared window's driver. Kept
    /// separately from the registry so closing the main tab doesn't orphan
    /// the driver for the remaining tabs.</summary>
    private BrowserTab? _driverTab;

    public BrowserTabRegistry Tabs { get; } = new();
    public int? CdpPort { get; private set; }
    public static bool IsWindows => OperatingSystem.IsWindows();
    public static bool IsMac => OperatingSystem.IsMacOS();

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
                case { } a when !a.StartsWith('-'):
                    _initialUrl = NormalizeUrl(a);
                    break;
            }
        }
        _userDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BrAIn",
            $"profile-{Environment.ProcessId}");
        _storeDir = Path.GetDirectoryName(_userDataDir)!;
        Directory.CreateDirectory(_userDataDir);
        LoadStores();
        CleanupStaleProfiles();
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
                    _history.Add((
                        el.GetProperty("u").GetString() ?? "",
                        el.TryGetProperty("i", out var i) ? i.GetString() : null));
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

    private (PhotinoWindow Window, BrowserTab Tab) CreatePhotinoWindow()
    {
        var tab = new BrowserTab
        {
            Id = Guid.NewGuid(),
            IsMain = true,
            Window = null!, // set below, before handlers can fire
        };

        var window = new PhotinoWindow()
            .SetTitle(AppName)
            .SetLogVerbosity(0) // NEVER write to stdout — stdio MCP lives there
            .SetUseOsDefaultSize(false)
            .SetSize(1440, 900)
            .Center()
            .SetDevToolsEnabled(true)
            .SetContextMenuEnabled(true)
            .SetGrantBrowserPermissions(true)
            .SetJavascriptClipboardAccessEnabled(true)
            .SetTemporaryFilesPath(Path.Combine(_userDataDir, "webview"));
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
            // Logical tabs share this single window/webview; only the main
            // driver needs wiring.
            _windowCreated.TrySetResult();
            Task.Run(InitializeMainDriverAsync);
        });
        window.RegisterWindowClosingHandler((_, _) =>
        {
            tab.IsClosed = true;
            Tabs.Remove(tab.Id);
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
            _driverTab = tab;
            tab.Bound.TrySetResult();
            _driverReady.TrySetResult();
            Log("driver ready: " + tab.Driver.Name);
        }
        catch (Exception ex)
        {
            Log("driver init failed: " + ex);
            _driverReady.TrySetException(ex);
            foreach (var t in Tabs.All) t.Bound.TrySetException(ex);
        }
    }

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
        driver.NetworkEvent += (_, e) => Enqueue(_network, e);
        driver.LoadStarted += (_, _) =>
        {
            Interlocked.Increment(ref _loadDepth);
            PushChromeStateAll();
            // A document is being created — refresh the boot payload it will
            // carry (covers back/forward and page-initiated navigations where
            // NavigateAsync isn't on the path).
            _ = driver.SetBootStateAsync(BuildChromeStateJson(null, "inj"), CancellationToken.None);
        };
        driver.LoadCompleted += async (_, _) =>
        {
            if (Interlocked.Decrement(ref _loadDepth) < 0)
                Interlocked.Exchange(ref _loadDepth, 0);
            // The window hosts whichever tab is active — refresh that one.
            var target = Tabs.Active ?? tab;
            await RefreshTabInfoAsync(target);
            await EnsurePageHelpersAsync(target);
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
        // Navigating a tab implies making it the shown/active one.
        if (Tabs.Active?.Id != tab.Id) Tabs.Activate(tab.Id);
        url = NormalizeUrl(url);
        if (url is "about:newtab" or "about:help")
        {
            var nt = url == "about:newtab";
            var html = InjectBootState(nt ? Assets.NewTabHtml : Assets.HelpHtml);
            InvokeOnUi(() => tab.Window.LoadRawString(html));
            tab.LastUrl = url;
            tab.LastTitle = nt ? "New Tab" : "How to use";
            try { await EnsurePageHelpersAsync(tab); } catch { }
            // NavigateToString docs never emit frameStoppedLoading — settle
            // the loading depth here or the spinner sticks forever.
            Interlocked.Exchange(ref _loadDepth, 0);
            PushChromeStateAll();
            return NavigateResult.Success(url, tab.LastTitle);
        }
        var driver = await DriverOrThrowAsync(tab, ct);
        // Bake chrome state into the next document so the toolbar renders
        // final tab state on first paint (no empty-strip flash).
        try { await driver.SetBootStateAsync(BuildChromeStateJson(ConsumeFresh(), "inj"), ct); } catch { }
        var result = await driver.NavigateAsync(url, ct);
        if (result.Ok) tab.LastUrl = result.Url;
        return result;
    }

    /// <summary>Photino-level navigation (works even without a driver).</summary>
    public void NavigateRaw(PhotinoWindow window, string url)
        => window.Invoke(() => window.Load(new Uri(NormalizeUrl(url))));

    public async Task<NavigateResult> GoBackAsync(BrowserTab? tab = null, CancellationToken ct = default)
        => await (await DriverOrThrowAsync(tab ??= ActiveTabOrThrow(), ct)).GoBackAsync(ct);

    public async Task<NavigateResult> GoForwardAsync(BrowserTab? tab = null, CancellationToken ct = default)
        => await (await DriverOrThrowAsync(tab ??= ActiveTabOrThrow(), ct)).GoForwardAsync(ct);

    public async Task<NavigateResult> ReloadAsync(bool ignoreCache = false, BrowserTab? tab = null, CancellationToken ct = default)
        => await (await DriverOrThrowAsync(tab ??= ActiveTabOrThrow(), ct)).ReloadAsync(ignoreCache, ct);

    public async Task<byte[]> ScreenshotAsync(RectD? clip, bool fullPage, BrowserTab? tab = null, CancellationToken ct = default)
    {
        var t = tab ??= ActiveTabOrThrow();
        var driver = await DriverOrThrowAsync(t, ct);
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

    public IReadOnlyList<ConsoleEntry> ConsoleLog => _console.ToArray();
    public IReadOnlyList<NetworkEntry> NetworkLog => _network.ToArray();
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
        Tabs.Add(tab);
        Tabs.Activate(tab.Id);
        _pendingFresh = tab.Id.ToString("N")[..8]; // consumed by boot state
        await NavigateAsync(url ?? "newtab", tab, ct);
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
        if (Tabs.Active?.Id != tab.Id)
        {
            Tabs.Activate(tab.Id);
            await RaiseWindowAsync(tab, ct);
            await NavigateAsync(tab.LastUrl, tab, ct);
        }
        PushChromeStateAll();
        return true;
    }

    public async Task<bool> CloseWindowAsync(string idOrPrefix, CancellationToken ct = default)
    {
        var tab = Tabs.FindByPrefix(idOrPrefix) ?? throw new InvalidOperationException($"No such tab '{idOrPrefix}'");
        if (tab.IsPinned) return false; // pinned tabs resist close — unpin first
        var wasActive = Tabs.Active?.Id == tab.Id;
        var idx = Tabs.IndexOf(tab.Id);
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
        SaveSessionSoon();
        var remaining = Tabs.All;
        if (remaining.Count == 0)
        {
            // Closing the last tab closes the browser, like a normal browser.
            try { InvokeOnUi(() => _mainWindow?.Close()); } catch { }
            return true;
        }
        PushChromeStateAll();
        if (wasActive)
        {
            // Activate the tab that slid into the closed slot (right neighbor),
            // or the new last tab when closing the rightmost one.
            var next = remaining[Math.Clamp(idx, 0, remaining.Count - 1)];
            await ActivateWindowAsync(next.Id.ToString("N")[..8], ct);
        }
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
            await Task.Run(() => Mac.MacWindow.Activate(tab), ct);
        }
    }

    // ---------------- window chrome ops ----------------

    public void SetWindowSize(BrowserTab tab, int width, int height)
        => tab.Window.Invoke(() => { tab.Window.Width = width; tab.Window.Height = height; });

    public void SetWindowState(BrowserTab tab, string state)
        => tab.Window.Invoke(() =>
        {
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

    public void SetZoom(BrowserTab tab, int percent) => tab.Window.Invoke(() => tab.Window.Zoom = percent);

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
        try { InvokeOnUi(() => { if (_mainWindow is not null) _mainWindow.Zoom = pct; }); } catch { }
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

    private void HandleWebMessage(BrowserTab tab, string message)
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
                        tcs.TrySetException(new JavaScriptException(
                            root.TryGetProperty("e", out var e) ? e.GetString() ?? "unknown JS error" : "unknown JS error"));
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

        // Toolbar / start-page commands: "brain:<command>[:arg]"
        if (message.StartsWith("brain:", StringComparison.Ordinal))
        {
            var cmd = message[6..];
            var colon = cmd.IndexOf(':');
            var (verb, arg) = colon < 0 ? (cmd, "") : (cmd[..colon], cmd[(colon + 1)..]);
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
                        case "reload": await ReloadAsync(false, active); break;
                        case "stop":
                            var d = await DriverOrThrowAsync(active, default);
                            await d.EvaluateAsync("window.stop()", false, default);
                            Interlocked.Exchange(ref _loadDepth, 0);
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
                            await ActivateWindowAsync(arg);
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
                        case "tabs": PushChromeStateAll(); break;
                        case "zoomin": ZoomBy(+1); break;
                        case "zoomout": ZoomBy(-1); break;
                        case "zoomreset": SetZoomLevel(100); break;
                        case "fullscreen":
                            try { InvokeOnUi(() => { if (_mainWindow is not null) _mainWindow.FullScreen = !_mainWindow.FullScreen; }); } catch { }
                            break;
                        case "topmost":
                            _topmost = !_topmost;
                            try { InvokeOnUi(() => { if (_mainWindow is not null) _mainWindow.Topmost = _topmost; }); } catch { }
                            PushChromeStateAll();
                            break;
                        case "bm": ToggleBookmark(active); break;
                        case "scroll": RecordScroll(active, arg); break;
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
            title = t.LastTitle ?? (t.LastUrl is "about:newtab" or "app://newtab" or "about:blank" ? "New Tab" : t.LastUrl == "about:help" ? "How to use" : t.LastUrl),
            url = t.LastUrl,
            icon = t.LastIcon,
            active = t.Id == activeId,
            priv = t.IsPrivate,
            pin = t.IsPinned,
        }).ToList();
        object[] hist;
        lock (_history)
            hist = _history.Take(12).Select(h => (object)new { u = h.Url, i = h.Icon }).ToArray();
        object[] bmarks;
        lock (_bookmarks)
            bmarks = _bookmarks.Take(10).Select(bm => (object)new { u = bm.Url, t = bm.Title, i = bm.Icon }).ToArray();
        var activeUrl = Tabs.Active?.LastUrl;
        bool isBm;
        lock (_bookmarks)
            isBm = activeUrl is not null && _bookmarks.Any(b => b.Url == activeUrl);
        return JsonSerializer.Serialize(new
        {
            b = 9,
            tabs,
            loading = IsLoading,
            canBack = _canBack,
            canFwd = _canFwd,
            hist,
            bmarks,
            bm = isBm,
            zoom = _zoom,
            topmost = _topmost,
            privActive = Tabs.Active?.IsPrivate == true,
            sess = _savedSession.Count,
            fresh,
            src,
        });
    }

    /// <summary>Push current tab/URL state to a window's injected chrome bar.</summary>
    public void PushChromeState(BrowserTab tab)
    {
        if (_noToolbar || tab.IsClosed) return;
        var json = BuildChromeStateJson(null);
        try { InvokeOnUi(() => { if (!tab.IsClosed) tab.Window.SendWebMessage(json); }); }
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
        var lib = _noToolbar ? Assets.BrainJs : Assets.BrainJs + "\n" + Assets.ChromeJs;
        return html.Replace("</head>",
            "<script>window.__brainBoot=" + json +
            ";window.__brainPageAnim=" + (animate ? "true" : "false") +
            (animate ? ";document.documentElement.className+=' anim'" : "") + ";" +
            "try{window.__brainChrome&&__brainChrome.applyBoot()}catch(e){}</script>" +
            "<script>" + lib + "</script></head>");
    }

    private string? ConsumeFresh()
    {
        var f = _pendingFresh;
        _pendingFresh = null;
        return f;
    }

    public void PushChromeStateAll()
    {
        // All logical tabs share the single window — one push updates chrome.
        var t = Tabs.Main ?? Tabs.All.FirstOrDefault();
        if (t is not null) PushChromeState(t);
    }

    /// <summary>Inject brain.js (+ toolbar) into the tab's current document if absent.</summary>
    public async Task EnsurePageHelpersAsync(BrowserTab tab)
    {
        // Logical tabs share one webview — always drive through the bound
        // driver, not tab.Driver (null on non-main logical tabs, which made
        // internal about: pages lose the chrome bar entirely).
        var driver = tab.Driver ?? _driverTab?.Driver;
        if (driver is null || !driver.SupportsEval) return;
        try
        {
            var installed = await driver.EvaluateAsync(
                "window.__brain && window.__brain.v === " + Assets.BrainJsVersion, false, CancellationToken.None);
            if (installed?.ValueKind == JsonValueKind.True)
            {
                if (!_noToolbar)
                    await driver.EvaluateAsync("window.__brainChrome && window.__brainChrome.mount()", false, CancellationToken.None);
                return;
            }
            var script = Assets.BrainJs + (_noToolbar ? "" : "\n" + Assets.ChromeJs);
            await driver.EvaluateAsync(script, false, CancellationToken.None);
        }
        catch (Exception ex) { Log($"helper inject failed: {ex.Message}"); }
    }

    public async Task RefreshTabInfoAsync(BrowserTab tab)
    {
        var driver = tab.Driver ?? _driverTab?.Driver;
        if (driver is null || !driver.SupportsEval) return;
        try
        {
            var info = await driver.EvaluateAsync(
                """
                JSON.stringify({
                  u: location.href, t: document.title,
                  n: !!window.__brainNewTab, h: !!window.__brainHelp,
                  i: (document.querySelector("link[rel~='icon']") || document.querySelector("link[rel='shortcut icon']") || {}).href || ''
                })
                """, false, CancellationToken.None);
            if (info?.ValueKind == JsonValueKind.String)
            {
                using var doc = JsonDocument.Parse(info.Value.GetString()!);
                var root = doc.RootElement;
                // Our start page is served via LoadRawString, so location.href
                // reads about:blank — the doc flags itself so tab switching
                // reloads the start page instead of a literal blank page.
                var isNt = root.TryGetProperty("n", out var n) && n.GetBoolean();
                var isHelp = root.TryGetProperty("h", out var h) && h.GetBoolean();
                tab.LastUrl = isNt ? "about:newtab" : isHelp ? "about:help" : root.GetProperty("u").GetString() ?? tab.LastUrl;
                tab.LastTitle = isNt ? "New Tab" : isHelp ? "How to use" : root.GetProperty("t").GetString();
                var icon = root.GetProperty("i").GetString();
                if (!string.IsNullOrEmpty(icon)) tab.LastIcon = icon;
                (_canBack, _canFwd) = await driver.GetNavStateAsync(CancellationToken.None);
                if (!tab.IsPrivate) RecordHistory(tab.LastUrl, tab.LastIcon);
                SaveSessionSoon();
                var title = string.IsNullOrWhiteSpace(tab.LastTitle) ? "BrAIn" : tab.LastTitle + " — BrAIn";
                InvokeOnUi(() => { if (!tab.IsClosed) tab.Window.Title = title; });
                PushChromeStateAll();
            }
        }
        catch { /* page mid-navigation */ }
    }

    private void RecordHistory(string url, string? icon)
    {
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return;
        lock (_history)
        {
            _history.RemoveAll(h => h.Url == url);
            _history.Insert(0, (url, icon));
            if (_history.Count > HistoryCap) _history.RemoveAt(HistoryCap);
            SaveStore("history.json", _history.Select(h => new { u = h.Url, i = h.Icon }));
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    public BrowserTab ActiveTabOrThrow()
        => Tabs.Active ?? throw new InvalidOperationException("No browser tab available");

    public bool IsLoading => _loadDepth > 0;
    public (bool CanBack, bool CanFwd) NavState => (_canBack, _canFwd);

    public async Task<IBrowserDriver> DriverOrThrowAsync(BrowserTab tab, CancellationToken ct)
    {
        await _windowCreated.Task.WaitAsync(ct);
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

    internal static string NormalizeUrl(string url)
    {
        url = url.Trim();
        if (url.Equals("newtab", StringComparison.OrdinalIgnoreCase) ||
            url.Equals("home", StringComparison.OrdinalIgnoreCase))
            return "about:newtab";
        if (url.Equals("help", StringComparison.OrdinalIgnoreCase))
            return "about:help";
        if (!url.Contains("://") && !url.StartsWith("about:") && !url.StartsWith("file:") &&
            !url.StartsWith("data:") && !url.StartsWith("javascript:"))
        {
            // Looks like a search or bare domain
            if (url.Contains('.') && !url.Contains(' '))
                url = "https://" + url;
            else
                url = "https://www.google.com/search?q=" + Uri.EscapeDataString(url);
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

    internal static void Log(string msg) => Console.Error.WriteLine($"[BrAIn] {msg}");

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
    private static readonly Lazy<string> _newTab = new(() => Load("BrAIn.Assets.newtab.html"));
    private static readonly Lazy<string> _help = new(() => Load("BrAIn.Assets.help.html"));

    public static string BrainJs => _brainJs.Value;
    public static string ChromeJs => _chromeJs.Value;
    public static string NewTabHtml => _newTab.Value;
    public static string HelpHtml => _help.Value;
    public const int BrainJsVersion = 1;
}
