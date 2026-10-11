using System.Collections.Concurrent;
using System.Drawing;
using Microsoft.Web.WebView2.Core;

namespace BrAIn.Browser.Win;

/// <summary>
/// Windows per-tab views: every tab owns a WebView2 controller parented to the
/// main window, so switching tabs shows another view instead of reloading one
/// shared page (the Windows counterpart of Browser/Mac/WkTabs.cs).
///
/// Threading: WebView2 objects are bound to the UI thread. Every COM call goes
/// through <c>_ui</c> (Photino's Invoke — runs inline when already on the UI
/// thread); async completions are awaited from pool threads, never touched there.
///
/// All views share one WebView2 environment. It is created with the same user-data
/// folder and browser arguments as Photino's own webview, so both live in one
/// browser process; if the runtime refuses that combination a private environment
/// (own folder, own debugging port) is used instead.
/// </summary>
internal sealed class WinTabs
{
    internal sealed class View
    {
        public Guid TabId;
        public string Marker = "";
        public bool FromSpare;
        public Size LastSize;
        public CoreWebView2Controller Controller = null!;
        public CoreWebView2 Core = null!;
        public volatile bool Visible;
        public volatile bool Closed;
        public volatile bool Audible;
        public volatile bool Suspended;
        public bool InPrivate;
        public IntPtr Hwnd;   // the controller's top-level child window of the main window
        public CancellationTokenSource? SuspendCts;
        public TaskCompletionSource? NavDone;
    }

    internal sealed record EnvConfig(string Folder, string Args, int Port);

    // Same shim Photino injects: chrome.js / brain.js talk through window.external.
    private const string Shim =
        "window.external = { sendMessage: function(message) { window.chrome.webview.postMessage(message); }, " +
        "receiveMessage: function(callback) { window.chrome.webview.addEventListener('message', function(e) { callback(e.data); }); } };";

    private readonly Action<Action> _ui;
    private readonly Func<IntPtr> _hwnd;
    private readonly bool _grantPermissions;
    private readonly ConcurrentDictionary<Guid, View> _views = new();
    private readonly SemaphoreSlim _createLock = new(1, 1);   // one controller at a time: its HWND is found by diffing the children
    private CoreWebView2Environment? _env;

    public int Port { get; private set; }
    public bool SharedEnvironment { get; private set; }

    public Action<Guid, string>? OnMessage;
    public Action<Guid, bool>? OnAudible;
    public Action<string>? OnNewTab;
    public Action<string>? Log;

    /// <summary>How long a hidden tab stays warm before its renderer is suspended.</summary>
    public TimeSpan SuspendAfter { get; set; } = TimeSpan.FromSeconds(20);
    /// <summary>Opt-in (BRAIN_SUSPEND=1): hidden tabs get WebView2's low-memory hint at once and are
    /// suspended after <see cref="SuspendAfter"/>. Off by default — it makes switching back repaint from
    /// scratch and freezes background JS.</summary>
    public bool SuspendEnabled { get; set; }

    /// <summary>Keep one blank, hidden view ready so a new tab doesn't pay for controller creation.</summary>
    public bool PrewarmEnabled { get; set; } = true;

    public WinTabs(Action<Action> ui, Func<IntPtr> hwnd, bool grantPermissions)
    {
        _ui = ui;
        _hwnd = hwnd;
        _grantPermissions = grantPermissions;
    }

    private static Task<T> Run<T>(Func<Task<T>> start, Action<Action> ui)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        ui(() =>
        {
            try
            {
                start().ContinueWith(t =>
                {
                    if (t.IsFaulted) tcs.TrySetException(t.Exception!.GetBaseException());
                    else if (t.IsCanceled) tcs.TrySetCanceled();
                    else tcs.TrySetResult(t.Result);
                }, TaskScheduler.Default);
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return tcs.Task;
    }

    // ------------------------------------------------------------------
    // Environment
    // ------------------------------------------------------------------

    /// <summary>Creates the environment: <paramref name="shared"/> first (one browser
    /// process with Photino's webview), <paramref name="fallback"/> if that is refused.</summary>
    public async Task InitializeAsync(EnvConfig shared, EnvConfig fallback)
    {
        try
        {
            _env = await CreateEnvAsync(shared).WaitAsync(TimeSpan.FromSeconds(8));
            Port = shared.Port;
            SharedEnvironment = true;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"shared WebView2 environment refused ({ex.GetType().Name}: {ex.Message}); using a private one");
            _env = await CreateEnvAsync(fallback).WaitAsync(TimeSpan.FromSeconds(8));
            Port = fallback.Port;
            SharedEnvironment = false;
        }
    }

    private Task<CoreWebView2Environment> CreateEnvAsync(EnvConfig c) =>
        Run(() =>
        {
            var opts = new CoreWebView2EnvironmentOptions();
            if (!string.IsNullOrEmpty(c.Args)) opts.AdditionalBrowserArguments = c.Args;
            return CoreWebView2Environment.CreateAsync(null, c.Folder, opts);
        }, _ui);

    // ------------------------------------------------------------------
    // Views
    // ------------------------------------------------------------------

    /// <summary>Creates a hidden view navigated to a blank data URL containing <c>marker</c> — the marker
    /// lets the CDP driver find this view's page target among all of them.</summary>
    public async Task<View> CreateViewAsync(Guid tabId, bool priv, int zoomPercent, string marker)
    {
        if (!priv && PrewarmEnabled)
        {
            var spare = Interlocked.Exchange(ref _spare, null);
            if (spare is not null)
            {
                try
                {
                    var v = await spare;
                    if (!v.Closed)
                    {
                        _views.TryRemove(new KeyValuePair<Guid, View>(v.TabId, v));
                        v.TabId = tabId;
                        v.FromSpare = true;
                        _views[tabId] = v;
                        SetZoom(v, zoomPercent);
                        return v;
                    }
                }
                catch { /* the spare failed to build — make a fresh one below */ }
            }
        }
        return await BuildViewAsync(tabId, priv, zoomPercent, marker);
    }

    private Task<View>? _spare;

    /// <summary>Starts building the spare view in the background (no-op if one exists).</summary>
    public void PrewarmSpare(int zoomPercent)
    {
        if (!PrewarmEnabled || _env is null || Volatile.Read(ref _spare) is not null) return;
        var t = BuildViewAsync(Guid.NewGuid(), false, zoomPercent, "braintab-spare" + Guid.NewGuid().ToString("N")[..8]);
        if (Interlocked.CompareExchange(ref _spare, t, null) is not null) return;
        t.ContinueWith(r =>
        {
            if (r.IsFaulted) { Log?.Invoke("spare view failed: " + r.Exception!.GetBaseException().Message); Interlocked.CompareExchange(ref _spare, null, t); }
        }, TaskScheduler.Default);
    }

    private async Task<View> BuildViewAsync(Guid tabId, bool priv, int zoomPercent, string marker)
    {
        var env = _env ?? throw new InvalidOperationException("WebView2 environment not created");
        var view = new View { TabId = tabId, Marker = marker };

        await _createLock.WaitAsync();
        try
        {
        var before = new List<IntPtr>();
        _ui(() => before = Native.Win32.DirectChildren(_hwnd()));
        CoreWebView2Controller controller;
        if (priv)
        {
            try
            {
                controller = await Run(() =>
                {
                    var o = env.CreateCoreWebView2ControllerOptions();
                    o.IsInPrivateModeEnabled = true;
                    return env.CreateCoreWebView2ControllerAsync(_hwnd(), o);
                }, _ui).WaitAsync(TimeSpan.FromSeconds(20));
                view.InPrivate = true;
            }
            catch (Exception ex)
            {
                // Old runtime: a normal view; the controller still wipes the site data when the tab closes.
                Log?.Invoke("InPrivate view unavailable, using a regular view: " + ex.Message);
                controller = await Run(() => env.CreateCoreWebView2ControllerAsync(_hwnd()), _ui).WaitAsync(TimeSpan.FromSeconds(20));
            }
        }
        else
            controller = await Run(() => env.CreateCoreWebView2ControllerAsync(_hwnd()), _ui).WaitAsync(TimeSpan.FromSeconds(20));

        view.Controller = controller;
        _ui(() =>
        {
            Configure(view, zoomPercent);
            // Photino's own (hidden-by-us) bootstrap view is a sibling; the new child is this controller's window
            view.Hwnd = Native.Win32.DirectChildren(_hwnd()).FirstOrDefault(h => !before.Contains(h));
        });
        _views[tabId] = view;
        }
        finally { _createLock.Release(); }

        // The bridge shim must exist before the first document is created.
        await Run(() => view.Core.AddScriptToExecuteOnDocumentCreatedAsync(Shim), _ui).WaitAsync(TimeSpan.FromSeconds(10));

        // A real (cross-document) navigation, so NavigationCompleted is certain to fire; the data URL
        // carries the marker and paints the window colour instead of a white flash.
        var done = Arm(view);
        var blank = "data:text/html;charset=utf-8,%3Ctitle%3E" + marker +
            "%3C%2Ftitle%3E%3Cbody%20style%3D%22background%3A%230b0d12%22%3E%3C%2Fbody%3E";
        _ui(() => view.Core.Navigate(blank));
        await Task.WhenAny(done, Task.Delay(TimeSpan.FromSeconds(6)));
        return view;
    }

    private void Configure(View v, int zoomPercent)
    {
        var c = v.Controller;
        var core = c.CoreWebView2;
        v.Core = core;

        var s = core.Settings;
        s.AreDevToolsEnabled = false;            // our own DevTools live in the page
        s.IsZoomControlEnabled = true;

        try { c.DefaultBackgroundColor = Color.FromArgb(255, 11, 13, 18); } catch { }   // window colour, never a white flash
        c.IsVisible = false;
        c.ZoomFactor = Math.Max(0.25, zoomPercent / 100.0);
        Refit(v);

        core.WebMessageReceived += (_, e) =>
        {
            string m;
            try { m = e.TryGetWebMessageAsString(); } catch { return; }
            OnMessage?.Invoke(v.TabId, m);
        };
        core.PermissionRequested += (_, e) =>
        {
            if (_grantPermissions) e.State = CoreWebView2PermissionState.Allow;
        };
        core.NewWindowRequested += (_, e) =>
        {
            // Plain target=_blank / window.open links become tabs. Sized popups (OAuth, payment
            // windows) keep the engine's own window so they retain their window.opener link.
            var f = e.WindowFeatures;
            var uri = e.Uri ?? "";
            if (f.HasSize || f.HasPosition) return;
            if (!uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
            e.Handled = true;
            OnNewTab?.Invoke(uri);
        };
        core.IsDocumentPlayingAudioChanged += (_, _) =>
        {
            try { v.Audible = core.IsDocumentPlayingAudio; } catch { return; }
            OnAudible?.Invoke(v.TabId, v.Audible);
            if (!v.Audible && !v.Visible) ScheduleSuspend(v);
        };
        core.NavigationCompleted += (_, _) => v.NavDone?.TrySetResult();
        core.ProcessFailed += (_, e) => Log?.Invoke($"WebView2 process failed ({e.ProcessFailedKind}) in tab {v.TabId.ToString("N")[..8]}");
    }

    private static Task Arm(View v)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        v.NavDone = tcs;
        return tcs.Task;
    }

    private void Refit(View v)
    {
        var (w, h) = Native.Win32.ClientSize(_hwnd());
        if (w <= 0 || h <= 0) return;
        var size = new Size(w, h);
        if (v.LastSize == size) return;   // a no-op resize still makes Chromium re-layout the page
        v.LastSize = size;
        v.Controller.Bounds = new Rectangle(0, 0, w, h);
    }

    /// <summary>Makes <paramref name="view"/> the visible one; every other view is hidden (and
    /// later suspended). The new view is shown first so the window never flashes empty.</summary>
    public void Show(View view)
    {
        _ui(() =>
        {
            if (view.Closed) return;
            Refit(view);
            view.Controller.IsVisible = true;
            view.Visible = true;
            Native.Win32.BringToTop(view.Hwnd);   // above Photino's bootstrap view (and any view shown earlier)
            CancelSuspend(view);
            Resume(view);
            if (SuspendEnabled) SetMemory(view, low: false);
            foreach (var o in _views.Values)
            {
                if (ReferenceEquals(o, view) || !o.Visible) continue;
                o.Controller.IsVisible = false;
                o.Visible = false;
                if (SuspendEnabled) { SetMemory(o, low: true); ScheduleSuspend(o); }
            }
            try { view.Controller.MoveFocus(CoreWebView2MoveFocusReason.Programmatic); } catch { }
        });
    }

    public void FocusView(View view) => _ui(() =>
    {
        try { if (!view.Closed && view.Visible) view.Controller.MoveFocus(CoreWebView2MoveFocusReason.Programmatic); } catch { }
    });

    public void RefitAll() => _ui(() =>
    {
        foreach (var v in _views.Values)
            try { if (!v.Closed) Refit(v); } catch { }
    });

    public void NotifyMoved() => _ui(() =>
    {
        foreach (var v in _views.Values)
            try { if (!v.Closed) v.Controller.NotifyParentWindowPositionChanged(); } catch { }
    });

    public void Push(View view, string json) => _ui(() =>
    {
        try { if (!view.Closed) view.Core.PostWebMessageAsString(json); } catch { }
    });

    public bool Has(View view) => !view.Closed;

    public void SetZoom(View view, int percent) => _ui(() =>
    {
        try { if (!view.Closed) view.Controller.ZoomFactor = Math.Max(0.25, percent / 100.0); } catch { }
    });

    public void SetZoomAll(int percent) => _ui(() =>
    {
        foreach (var v in _views.Values)
            try { if (!v.Closed) v.Controller.ZoomFactor = Math.Max(0.25, percent / 100.0); } catch { }
    });

    public void SetMuted(View view, bool muted) => _ui(() =>
    {
        try { if (!view.Closed) view.Core.IsMuted = muted; } catch { }
    });

    /// <summary>Loads an HTML document (about: pages); completes when the navigation finishes.</summary>
    public async Task NavigateToStringAsync(View view, string html)
    {
        var done = Arm(view);
        _ui(() => { if (!view.Closed) view.Core.NavigateToString(html); });
        await Task.WhenAny(done, Task.Delay(TimeSpan.FromSeconds(15)));
    }

    public void Destroy(View view)
    {
        // value-checked: a rebuilt view for the same tab may already have taken the slot
        _views.TryRemove(new KeyValuePair<Guid, View>(view.TabId, view));
        _ui(() =>
        {
            if (view.Closed) return;
            view.Closed = true;
            CancelSuspend(view);
            try { view.Controller.IsVisible = false; } catch { }
        });
        // Controller.Close() runs on the UI thread and can stall it while the renderer is torn down:
        // let the switch to the neighbouring tab finish painting first.
        Task.Delay(TimeSpan.FromMilliseconds(600)).ContinueWith(_ =>
        {
            try { _ui(() => { try { view.Controller.Close(); } catch { } }); } catch { }
        }, TaskScheduler.Default);
    }

    public void CloseAll()
    {
        foreach (var v in _views.Values.ToList()) Destroy(v);
    }

    /// <summary>Makes sure a view can run JS again (agent calls / CDP need a live renderer).</summary>
    public void EnsureResumed(View view)
    {
        if (view.Suspended) _ui(() => Resume(view));
    }

    // ------------------------------------------------------------------
    // Background savings: low-memory hint right away, full suspend after a delay
    // ------------------------------------------------------------------

    private static void SetMemory(View v, bool low)
    {
        try
        {
            v.Core.MemoryUsageTargetLevel = low
                ? CoreWebView2MemoryUsageTargetLevel.Low
                : CoreWebView2MemoryUsageTargetLevel.Normal;
        }
        catch { /* runtime without the API */ }
    }

    private void Resume(View v)
    {
        if (!v.Suspended) return;
        v.Suspended = false;
        try { v.Core.Resume(); } catch { }
    }

    private void CancelSuspend(View v)
    {
        try { v.SuspendCts?.Cancel(); } catch { }
        v.SuspendCts = null;
    }

    private void ScheduleSuspend(View v, int attempt = 0)
    {
        if (!SuspendEnabled || v.Closed) return;
        CancelSuspend(v);
        var cts = v.SuspendCts = new CancellationTokenSource();
        var token = cts.Token;
        Task.Delay(attempt == 0 ? SuspendAfter : TimeSpan.FromSeconds(20), token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            try
            {
                _ui(() =>
                {
                    if (v.Closed || v.Visible || v.Suspended || v.Audible || token.IsCancellationRequested) return;
                    v.Core.TrySuspendAsync().ContinueWith(r =>
                    {
                        if (r.IsCompletedSuccessfully && r.Result) v.Suspended = true;
                        else if (attempt < 3 && !token.IsCancellationRequested) _ui(() => ScheduleSuspend(v, attempt + 1));
                    }, TaskScheduler.Default);
                });
            }
            catch { /* window closing */ }
        }, TaskScheduler.Default);
    }
}
