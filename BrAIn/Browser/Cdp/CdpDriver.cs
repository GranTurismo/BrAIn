using System.Text.Json;
using BrAIn.Browser.Drivers;

namespace BrAIn.Browser.Cdp;

/// <summary>
/// Windows driver: full control via Chrome DevTools Protocol over the
/// WebView2 --remote-debugging-port endpoint.
/// </summary>
public sealed class CdpDriver : DriverBase
{
    private readonly CdpClient _cdp;
    private CdpSession? _session;
    private TaskCompletionSource? _loadFired;
    private string? _mainFrameId;
    private static readonly string _injectSource = Assets.BrainJs + "\n" + Assets.ChromeJs;
    private readonly Dictionary<string, (string Method, string Url, string? Type)> _pendingRequests = new();

    public CdpDriver(CdpClient cdp, BrowserTab tab, BrowserController controller)
        : base(tab, controller)
    {
        _cdp = cdp;
    }

    public override string Name => "cdp";
    public override bool SupportsEval => true;
    public override bool SupportsScreenshot => true;
    public override bool SupportsRealInput => true;
    public override bool SupportsConsoleLog => true;
    public override bool SupportsNetworkLog => true;
    public override bool SupportsCookies => true;
    public override bool SupportsViewportEmulation => true;

    public override async Task InitializeAsync(BrowserController controller, CancellationToken ct)
    {
        // Bind to this tab's page target (all logical tabs share the one
        // page target behind the main window).
        var target = await FindTargetAsync(ct)
            ?? throw new InvalidOperationException("Could not find the CDP page target for this window");
        Tab.TargetId = target.TargetId;
        _session = await _cdp.AttachToTargetAsync(target.TargetId, ct);
        _cdp.EventReceived += OnCdpEvent;

        await _session.SendAsync("Runtime.enable", null, ct);
        await _session.SendAsync("Page.enable", null, ct);
        await _session.SendAsync("Network.enable", null, ct);
        await _session.SendAsync("Log.enable", null, ct);

        // Default page background is white, like other browsers; pages that set their
        // own background (including dark ones) are unaffected. Also pin the reported
        // colour scheme to light so sites don't switch to dark just because the OS is.
        try
        {
            await _session.SendAsync("Emulation.setEmulatedMedia",
                new { features = new[] { new { name = "prefers-color-scheme", value = "light" } } }, ct);
        }
        catch { }

        // Inject helpers into every future document in this target.
        try
        {
            var res = await _session.SendAsync("Page.addScriptToEvaluateOnNewDocument",
                new { source = _injectSource }, ct);
            if (res.ValueKind == JsonValueKind.Object &&
                res.TryGetProperty("identifier", out var idEl) &&
                idEl.GetString() is { } id)
                _injectIds.Add(id);
        }
        catch { /* older runtimes lack this method; controller reinjects anyway */ }

        // Downloads: page-level behavior is deprecated but still honored by
        // WebView2 and its events arrive on this session; Browser-level is
        // the modern path but needs the browser target (sessionId null).
        try
        {
            await _session.SendAsync("Page.setDownloadBehavior",
                new { behavior = "allow", downloadPath = Controller.Downloads.Dir }, ct);
        }
        catch
        {
            try
            {
                await _cdp.SendAsync("Browser.setDownloadBehavior",
                    new { behavior = "allow", downloadPath = Controller.Downloads.Dir, eventsEnabled = true },
                    null, ct);
            }
            catch { /* engine keeps its own download UI */ }
        }

        // Let the shared registry stop engine-side transfers on user cancel.
        Controller.Downloads.EngineCancel = it =>
        {
            if (it.CdpGuid is null) return Task.CompletedTask;
            return _cdp.SendAsync("Browser.cancelDownload",
                new { guid = it.CdpGuid }, null, CancellationToken.None);
        };

        // And into the current document right away.
        await Controller.EnsurePageHelpersAsync(Tab);
    }

    /// <summary>Re-register the new-document script with a fresh
    /// window.__brainBoot payload so the toolbar paints final state on the
    /// next navigation instead of empty → pop-in.</summary>
    private readonly SemaphoreSlim _bootLock = new(1, 1);
    private readonly List<string> _injectIds = new();

    public override async Task SetBootStateAsync(string stateJson, CancellationToken ct)
    {
        if (_session is null) return;
        // Concurrent calls (NavigateAsync + LoadStarted) serialize here.
        await _bootLock.WaitAsync(ct);
        try
        {
            // ADD first, THEN prune older scripts — a document committing
            // mid-rotation still sees at least one inject script (never a
            // gap), and scripts evaluate in registration order so the newest
            // __brainBoot always wins.
            var src = "window.__brainBoot=" + stateJson + ";\n" + _injectSource;
            var res = await _session.SendAsync("Page.addScriptToEvaluateOnNewDocument",
                new { source = src }, ct);
            if (res.ValueKind == JsonValueKind.Object &&
                res.TryGetProperty("identifier", out var idEl) &&
                idEl.GetString() is { } newId)
                _injectIds.Add(newId);
            // Prune every older registration — failures retry next call.
            for (var i = _injectIds.Count - 2; i >= 0; i--)
            {
                try
                {
                    await _session.SendAsync("Page.removeScriptToEvaluateOnNewDocument",
                        new { identifier = _injectIds[i] }, ct);
                    _injectIds.RemoveAt(i);
                }
                catch { }
            }
        }
        catch (Exception ex) { BrowserController.Log("boot inject failed: " + ex.Message); }
        finally { _bootLock.Release(); }
    }

    private async Task<CdpClient.TargetInfo?> FindTargetAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        var boundIds = Controller.Tabs.All.Select(t => t.TargetId).Where(x => x != null).ToHashSet();
        while (DateTime.UtcNow < deadline)
        {
            var targets = await _cdp.GetTargetsAsync(ct);
            var pages = targets.Where(t => t.Type == "page").ToList();

            CdpClient.TargetInfo? pick = null;
            if (Tab.BindMarker is { } bm)
            {
                // per-tab Windows views: the view was navigated to a blank data URL carrying the marker — an exact match
                pick = pages.FirstOrDefault(p => p.Url.Contains(bm, StringComparison.Ordinal)
                                                 || p.Title.Contains(bm, StringComparison.Ordinal));
            }
            else if (Tab.IsMain)
                pick = pages.FirstOrDefault(p => !boundIds.Contains(p.TargetId)) ?? pages.FirstOrDefault();
            else if (Tab.LastUrl.Contains("braintab-"))
            {
                var marker = Tab.LastUrl.Split('#').Last();
                pick = pages.FirstOrDefault(p => p.Url.Contains(marker));
            }
            else
                pick = pages.FirstOrDefault(p => !boundIds.Contains(p.TargetId));

            if (pick is not null) return pick;
            await Task.Delay(250, ct);
        }
        return null;
    }

    private void OnCdpEvent(object? sender, (string? SessionId, string Method, JsonElement Params) e)
    {
        // Browser.download* events arrive with no sessionId; Page.download*
        // arrive on ours. Both carry a guid keying one Downloads.Item.
        if (e.Method is "Browser.downloadWillBegin" or "Page.downloadWillBegin")
        {
            HandleDownloadWillBegin(e.Params);
            return;
        }
        if (e.Method is "Browser.downloadProgress" or "Page.downloadProgress")
        {
            HandleDownloadProgress(e.Params);
            return;
        }
        if (e.SessionId != _session?.SessionId) return;
        try
        {
            switch (e.Method)
            {
                case "Runtime.consoleAPICalled":
                    HandleConsoleApi(e.Params);
                    break;
                case "Runtime.exceptionThrown":
                    HandleException(e.Params);
                    break;
                case "Log.entryAdded":
                    HandleLogEntry(e.Params);
                    break;
                case "Network.requestWillBeSent":
                    HandleRequestWillBeSent(e.Params);
                    break;
                case "Network.responseReceived":
                    HandleResponse(e.Params);
                    break;
                case "Network.loadingFinished":
                    HandleLoadingFinished(e.Params);
                    break;
                case "Network.loadingFailed":
                    HandleLoadingFailed(e.Params);
                    break;
                case "Page.frameStartedLoading":
                    if (IsMainFrame(e.Params)) RaiseLoadStarted();
                    break;
                case "Page.frameStoppedLoading":
                    if (IsMainFrame(e.Params)) RaiseLoadCompleted();
                    break;
                case "Page.navigatedWithinDocument":
                    // SPA navigation — URL/title may have changed.
                    RaiseLoadCompleted();
                    break;
                case "Page.loadEventFired":
                    // Only unblocks the navigate/reload wait — frameStoppedLoading
                    // owns the LoadCompleted raise so the depth counter stays paired.
                    _loadFired?.TrySetResult();
                    break;
                case "Page.javascriptDialogOpening":
                    // Auto-accept blocking JS dialogs so automation can't hang.
                    var accept = !string.Equals(
                        e.Params.TryGetProperty("type", out var t) ? t.GetString() : null,
                        "beforeunload", StringComparison.OrdinalIgnoreCase);
                    _ = _session?.SendAsync("Page.handleJavaScriptDialog", new { accept });
                    break;
                case "Page.frameNavigated":
                    if (e.Params.TryGetProperty("frame", out var fr))
                    {
                        if (!fr.TryGetProperty("parentId", out _))
                        {
                            _mainFrameId = fr.TryGetProperty("id", out var fid) ? fid.GetString() : null;
                            Controller.MarkReceiving(Tab); // main document committed: server has answered
                        }
                        if (fr.TryGetProperty("url", out var fu))
                            BrowserController.Log($"frameNavigated: {fu.GetString()}");
                    }
                    break;
                case "Inspector.targetCrashed":
                    BrowserController.Log("RENDERER CRASHED (Inspector.targetCrashed)");
                    _loadFired?.TrySetResult();
                    RaiseLoadCompleted();
                    break;
            }
        }
        catch { /* event handling must never throw into the reader */ }
    }

    // Browser.download* events reach every driver on the shared CDP socket (and Page.download* the
    // ones attached to the page) — the table is shared so each download is registered exactly once.
    private static readonly Dictionary<string, Downloads.Item> _cdpDownloads = new();
    private static readonly object _dlLock = new();

    private void HandleDownloadWillBegin(JsonElement p)
    {
        try
        {
            var guid = p.GetProperty("guid").GetString()!;
            var url = p.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
            var suggested = p.TryGetProperty("suggestedFilename", out var f) ? f.GetString() : null;
            lock (_dlLock)
            {
                if (_cdpDownloads.ContainsKey(guid)) return;
                var it = Controller.Downloads.Begin(url);
                it.CdpGuid = guid;
                // The engine writes into Downloads.Dir itself — pre-resolve the
                // name so the UI shows a filename and Open/Reveal have a path.
                Controller.Downloads.DestinationFor(it, suggested);
                _cdpDownloads[guid] = it;
            }
        }
        catch { }
    }

    private void HandleDownloadProgress(JsonElement p)
    {
        try
        {
            var guid = p.TryGetProperty("guid", out var g) ? g.GetString() : null;
            if (guid is null) return;
            var state = p.TryGetProperty("state", out var s) ? s.GetString() : null;
            Downloads.Item? it;
            lock (_dlLock)
            {
                if (!_cdpDownloads.TryGetValue(guid, out it)) return;
                if (state is "completed" or "canceled") _cdpDownloads.Remove(guid);
            }
            if (state == "completed") Controller.Downloads.Finish(it);
            else if (state == "canceled") Controller.Downloads.Cancel(it.Id);
            else
            {
                var recv = p.TryGetProperty("receivedBytes", out var r) ? (long)r.GetDouble() : 0;
                var total = p.TryGetProperty("totalBytes", out var t) ? (long)t.GetDouble() : -1;
                Controller.Downloads.Progress(it, recv, total);
            }
        }
        catch { }
    }

    /// <summary>True if the frame event params refer to the main frame.</summary>
    private bool IsMainFrame(JsonElement p)
    {
        var id = p.TryGetProperty("frameId", out var f) ? f.GetString() : null;
        // Before we learn the main frame id, treat all load events as main.
        return _mainFrameId is null || id is null || id == _mainFrameId;
    }

    private void HandleConsoleApi(JsonElement p)
    {
        var type = p.TryGetProperty("type", out var t) ? t.GetString() ?? "log" : "log";
        var parts = new List<string>();
        if (p.TryGetProperty("args", out var args))
            foreach (var a in args.EnumerateArray())
                parts.Add(ArgToString(a));
        var url = p.TryGetProperty("stackTrace", out var st) &&
                  st.TryGetProperty("callFrames", out var cf) && cf.GetArrayLength() > 0
            ? cf[0].TryGetProperty("url", out var u) ? u.GetString() : null
            : null;
        RaiseConsole(new ConsoleEntry(DateTime.Now, type, string.Join(' ', parts), url, null));
    }

    private static string ArgToString(JsonElement a)
    {
        if (a.TryGetProperty("value", out var v))
            return v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText();
        if (a.TryGetProperty("description", out var d)) return d.GetString() ?? "";
        if (a.TryGetProperty("unserializableValue", out var uv)) return uv.GetString() ?? "";
        return a.GetProperty("type").GetString() ?? "";
    }

    private void HandleException(JsonElement p)
    {
        var d = p.GetProperty("exceptionDetails");
        var text = d.TryGetProperty("exception", out var ex) &&
                   ex.TryGetProperty("description", out var desc)
            ? desc.GetString() ?? "JS exception"
            : d.TryGetProperty("text", out var tt) ? tt.GetString() ?? "JS exception" : "JS exception";
        var url = d.TryGetProperty("url", out var u) ? u.GetString() : null;
        int? line = d.TryGetProperty("lineNumber", out var ln) ? ln.GetInt32() : null;
        RaiseConsole(new ConsoleEntry(DateTime.Now, "error", text, url, line));
    }

    private void HandleLogEntry(JsonElement p)
    {
        var e = p.GetProperty("entry");
        var level = e.TryGetProperty("level", out var l) ? l.GetString() ?? "log" : "log";
        var text = e.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
        var url = e.TryGetProperty("url", out var u) ? u.GetString() : null;
        int? line = e.TryGetProperty("lineNumber", out var ln) ? ln.GetInt32() : null;
        RaiseConsole(new ConsoleEntry(DateTime.Now, level, text, url, line));
    }

    private static Dictionary<string, string>? ReadHeaders(JsonElement el)
    {
        if (!el.TryGetProperty("headers", out var h) || h.ValueKind != JsonValueKind.Object)
            return null;
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in h.EnumerateObject())
        {
            if (d.Count >= 40) break;
            d[kv.Name] = kv.Value.ValueKind == JsonValueKind.String
                ? kv.Value.GetString() ?? "" : kv.Value.GetRawText();
        }
        return d;
    }

    private void HandleRequestWillBeSent(JsonElement p)
    {
        var id = p.GetProperty("requestId").GetString()!;
        var req = p.GetProperty("request");
        _pendingRequests[id] = (
            req.TryGetProperty("method", out var m) ? m.GetString() ?? "GET" : "GET",
            req.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "",
            p.TryGetProperty("type", out var t) ? t.GetString() : null);
        var (method, url, type) = _pendingRequests[id];
        var post = req.TryGetProperty("postData", out var pd) && pd.ValueKind == JsonValueKind.String
            ? pd.GetString() : null;
        if (post is { Length: > 4000 }) post = post[..4000];
        RaiseNetwork(new NetworkEntry(DateTime.Now, id, method, url, null, type, 0, null,
            ReadHeaders(req), null, post));
    }

    private void HandleResponse(JsonElement p)
    {
        var id = p.GetProperty("requestId").GetString()!;
        var resp = p.GetProperty("response");
        var url = resp.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
        var status = resp.TryGetProperty("status", out var s) ? s.GetInt32() : 0;
        var method = _pendingRequests.TryGetValue(id, out var pend) ? pend.Method : "GET";
        var type = p.TryGetProperty("type", out var t) ? t.GetString()
            : _pendingRequests.TryGetValue(id, out pend) ? pend.Type : null;
        RaiseNetwork(new NetworkEntry(DateTime.Now, id, method, url, status, type, 0, null,
            null, ReadHeaders(resp), null));
    }

    private void HandleLoadingFinished(JsonElement p)
    {
        var id = p.GetProperty("requestId").GetString()!;
        long bytes = p.TryGetProperty("encodedDataLength", out var b) ? (long)b.GetDouble() : 0;
        if (_pendingRequests.TryGetValue(id, out var pend))
        {
            RaiseNetwork(new NetworkEntry(DateTime.Now, id, pend.Method, pend.Url,
                null, pend.Type, bytes, null));
            _pendingRequests.Remove(id);
        }
    }

    private void HandleLoadingFailed(JsonElement p)
    {
        var id = p.GetProperty("requestId").GetString()!;
        var err = p.TryGetProperty("errorText", out var e) ? e.GetString() : "failed";
        var isDoc = _pendingRequests.TryGetValue(id, out var pend) && pend.Type == "Document";
        if (pend.Method is not null)
        {
            RaiseNetwork(new NetworkEntry(DateTime.Now, id, pend.Method, pend.Url,
                null, pend.Type, 0, err));
            _pendingRequests.Remove(id);
        }
        else
        {
            RaiseNetwork(new NetworkEntry(DateTime.Now, id, "?", "", null, null, 0, err));
        }
        // A failed main-document load (DNS error, aborted nav) may never
        // produce loadEventFired — clear the loading state anyway.
        if (isDoc)
        {
            _loadFired?.TrySetResult();
            RaiseLoadCompleted();
        }
    }

    // ---------------- core primitives ----------------

    public override async Task<JsonElement?> EvaluateAsync(string expression, bool awaitPromise, CancellationToken ct)
    {
        var session = _session ?? throw new InvalidOperationException("CDP session not attached");
        var res = await session.SendAsync("Runtime.evaluate", new
        {
            expression,
            returnByValue = true,
            awaitPromise,
            userGesture = true,
            allowEvalByUser = true,
        }, ct);

        if (res.TryGetProperty("exceptionDetails", out var ex))
        {
            var msg = ex.TryGetProperty("exception", out var e) &&
                      e.TryGetProperty("description", out var d)
                ? d.GetString()
                : ex.TryGetProperty("text", out var t) ? t.GetString() : "JS exception";
            throw new JavaScriptException(msg ?? "JS exception");
        }

        if (!res.TryGetProperty("result", out var result)) return null;
        if (result.TryGetProperty("value", out var value)) return value;
        return null; // undefined / unserializable
    }

    public override async Task<NavigateResult> NavigateAsync(string url, CancellationToken ct)
    {
        var session = _session ?? throw new InvalidOperationException("CDP session not attached");
        _loadFired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var res = await session.SendAsync("Page.navigate", new { url }, ct);
        if (res.TryGetProperty("errorText", out var errEl))
            return NavigateResult.Fail(url, errEl.GetString() ?? "navigation failed");

        // Wait for load event, capped — SPA navigations may not produce one.
        var waitTask = _loadFired.Task;
        var done = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(15), ct)) == waitTask;
        if (!done) await WaitForLoadAsync(ct, 8000); // fallback: poll readyState

        var finalUrl = await GetUrlSafeAsync(ct);
        return NavigateResult.Success(finalUrl, await GetTitleSafeAsync(ct));
    }

    public override async Task<(bool CanBack, bool CanFwd)> GetNavStateAsync(CancellationToken ct)
    {
        try
        {
            var hist = await _session!.SendAsync("Page.getNavigationHistory", null, ct);
            var idx = hist.GetProperty("currentIndex").GetInt32();
            var count = hist.GetProperty("entries").GetArrayLength();
            return (idx > 0, idx < count - 1);
        }
        catch { return (false, false); }
    }

    public override async Task<NavigateResult> GoBackAsync(CancellationToken ct) =>
        await NavigateHistoryAsync(-1, ct);

    public override async Task<NavigateResult> GoForwardAsync(CancellationToken ct) =>
        await NavigateHistoryAsync(+1, ct);

    private async Task<NavigateResult> NavigateHistoryAsync(int delta, CancellationToken ct)
    {
        var session = _session!;
        var hist = await session.SendAsync("Page.getNavigationHistory", null, ct);
        var idx = hist.GetProperty("currentIndex").GetInt32();
        var entries = hist.GetProperty("entries");
        var targetIdx = idx + delta;
        if (targetIdx < 0 || targetIdx >= entries.GetArrayLength())
            return NavigateResult.Fail(await GetUrlSafeAsync(ct),
                delta < 0 ? "no page to go back to" : "no page to go forward to");

        var entryId = entries[targetIdx].GetProperty("id").GetInt32();
        _loadFired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await session.SendAsync("Page.navigateToHistoryEntry", new { entryId }, ct);
        await Task.WhenAny(_loadFired.Task, Task.Delay(15000, ct));
        var url = await GetUrlSafeAsync(ct);
        return NavigateResult.Success(url, await GetTitleSafeAsync(ct));
    }

    public override async Task<NavigateResult> ReloadAsync(bool ignoreCache, CancellationToken ct)
    {
        var session = _session!;
        _loadFired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await session.SendAsync("Page.reload", new { ignoreCache }, ct);
        await Task.WhenAny(_loadFired.Task, Task.Delay(15000, ct));
        return NavigateResult.Success(await GetUrlSafeAsync(ct), await GetTitleSafeAsync(ct));
    }

    public override async Task<byte[]> ScreenshotAsync(RectD? clip, bool fullPage, CancellationToken ct)
    {
        var session = _session!;
        object? clipParam = null;
        if (clip is { } c)
            clipParam = new { x = c.X, y = c.Y, width = c.Width, height = c.Height, scale = 1.0 };
        // WebView2 rejects an explicit null clip — omit the property entirely.
        var ps = new Dictionary<string, object?>
        {
            ["format"] = "png",
            ["captureBeyondViewport"] = fullPage || clip is not null,
        };
        if (clipParam is not null) ps["clip"] = clipParam;
        var res = await session.SendAsync("Page.captureScreenshot", ps, ct);
        return Convert.FromBase64String(res.GetProperty("data").GetString()!);
    }

    // ---------------- input (real CDP events) ----------------

    public override async Task ClickAtAsync(double x, double y, MouseButton button, int clickCount, CancellationToken ct)
    {
        var session = _session!;
        var btn = button switch
        {
            MouseButton.Right => "right",
            MouseButton.Middle => "middle",
            _ => "left",
        };
        for (var i = 0; i < clickCount; i++)
        {
            await session.SendAsync("Input.dispatchMouseEvent", new
            {
                type = "mousePressed",
                x, y,
                button = btn,
                clickCount = i + 1,
            }, ct);
            await session.SendAsync("Input.dispatchMouseEvent", new
            {
                type = "mouseReleased",
                x, y,
                button = btn,
                clickCount = i + 1,
            }, ct);
        }
    }

    public override async Task MouseMoveAsync(double x, double y, CancellationToken ct)
    {
        await _session!.SendAsync("Input.dispatchMouseEvent",
            new { type = "mouseMoved", x, y }, ct);
    }

    public override async Task MouseWheelAsync(double deltaX, double deltaY, CancellationToken ct)
    {
        await _session!.SendAsync("Input.dispatchMouseEvent",
            new { type = "mouseWheel", x = 0, y = 0, deltaX, deltaY }, ct);
    }

    public override async Task KeyPressAsync(string key, KeyModifiers modifiers, CancellationToken ct)
    {
        var session = _session!;
        var (code, vk, text) = CdpKeyMap.Lookup(key);
        var mods = modifiers.ToCdpMask();
        await session.SendAsync("Input.dispatchKeyEvent", new
        {
            type = text is null ? "rawKeyDown" : "keyDown",
            key = key,
            code,
            windowsVirtualKeyCode = vk,
            nativeVirtualKeyCode = vk,
            modifiers = mods,
            text,
        }, ct);
        await session.SendAsync("Input.dispatchKeyEvent", new
        {
            type = "keyUp",
            key = key,
            code,
            windowsVirtualKeyCode = vk,
            nativeVirtualKeyCode = vk,
            modifiers = mods,
        }, ct);
    }

    public override async Task InsertTextAsync(string text, CancellationToken ct)
    {
        await _session!.SendAsync("Input.insertText", new { text }, ct);
    }

    // ---------------- cookies ----------------

    public override async Task<IReadOnlyList<CookieInfo>> GetCookiesAsync(CancellationToken ct)
    {
        // Browser-level Storage domain returns all cookies in the profile.
        var res = await _cdp.SendAsync("Storage.getCookies", null, null, ct);
        var list = new List<CookieInfo>();
        foreach (var c in res.GetProperty("cookies").EnumerateArray())
        {
            list.Add(new CookieInfo(
                c.GetProperty("name").GetString() ?? "",
                c.GetProperty("value").GetString() ?? "",
                c.TryGetProperty("domain", out var d) ? d.GetString() : null,
                c.TryGetProperty("path", out var p) ? p.GetString() : null,
                c.TryGetProperty("expires", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : null,
                c.TryGetProperty("httpOnly", out var h) && h.GetBoolean(),
                c.TryGetProperty("secure", out var s) && s.GetBoolean()));
        }
        return list;
    }

    public override async Task<bool> SetCookieAsync(CookieInfo cookie, CancellationToken ct)
    {
        var url = await GetUrlSafeAsync(ct);
        // CDP rejects explicit nulls for optional numeric/string params —
        // only include domain/expires when they carry a value.
        var p = new Dictionary<string, object?>
        {
            ["name"] = cookie.Name,
            ["value"] = cookie.Value,
            ["url"] = url,
            ["path"] = cookie.Path ?? "/",
            ["httpOnly"] = cookie.HttpOnly,
            ["secure"] = cookie.Secure,
        };
        if (cookie.Domain is { Length: > 0 }) p["domain"] = cookie.Domain;
        if (cookie.Expires is { } exp) p["expires"] = exp;
        var res = await _session!.SendAsync("Network.setCookie", p, ct);
        return res.TryGetProperty("success", out var s) && s.GetBoolean();
    }

    public override async Task<bool> DeleteCookiesAsync(string? name, string? url, CancellationToken ct)
    {
        if (name is not null)
        {
            await _session!.SendAsync("Network.deleteCookies", new
            {
                name,
                url = url ?? await GetUrlSafeAsync(ct),
            }, ct);
            return true;
        }
        await _session!.SendAsync("Network.clearBrowserCookies", null, ct);
        return true;
    }

    /// <summary>Closing a private tab wipes that origin's cookies, storage and
    /// caches — the closest a shared WebView2 profile gets to incognito.</summary>
    public override async Task ClearSiteDataAsync(string origin, CancellationToken ct)
    {
        var s = _session;
        if (s is null) return;
        await s.SendAsync("Storage.clearDataForOrigin",
            new { origin, storageTypes = "all" }, ct);
    }

    // ---------------- viewport ----------------

    public override async Task SetViewportAsync(int width, int height, double deviceScaleFactor, bool mobile, CancellationToken ct)
    {
        await _session!.SendAsync("Emulation.setDeviceMetricsOverride", new
        {
            width,
            height,
            deviceScaleFactor,
            mobile,
        }, ct);
        Controller.SetWindowSize(Tab, width, height);
    }

    // ---------------- emulation ----------------

    public override async Task<string> EmulateAsync(Emulation e, CancellationToken ct)
    {
        var s = _session ?? throw new InvalidOperationException("CDP session not ready");
        var done = new List<string>();
        if (e.Reset)
        {
            await s.SendAsync("Emulation.setUserAgentOverride", new { userAgent = "" }, ct);
            // the page lib pins prefers-color-scheme to light at start-up; reset goes back to that
            await s.SendAsync("Emulation.setEmulatedMedia",
                new { features = new[] { new { name = "prefers-color-scheme", value = "light" } } }, ct);
            await s.SendAsync("Network.emulateNetworkConditions",
                new { offline = false, latency = 0, downloadThroughput = -1, uploadThroughput = -1 }, ct);
            await s.SendAsync("Emulation.clearGeolocationOverride", null, ct);
            done.Add("reset");
        }
        if (e.UserAgent is { Length: > 0 } ua)
        {
            await s.SendAsync("Emulation.setUserAgentOverride", new { userAgent = ua }, ct);
            done.Add("user agent");
        }
        if (e.ColorScheme is { Length: > 0 } cs)
        {
            await s.SendAsync("Emulation.setEmulatedMedia",
                new { features = new[] { new { name = "prefers-color-scheme", value = cs } } }, ct);
            done.Add($"{cs} colour scheme");
        }
        if (e.Offline is { } off)
        {
            await s.SendAsync("Network.emulateNetworkConditions",
                new { offline = off, latency = 0, downloadThroughput = -1, uploadThroughput = -1 }, ct);
            done.Add(off ? "offline" : "online");
        }
        if (e.Latitude is { } lat && e.Longitude is { } lon)
        {
            await s.SendAsync("Emulation.setGeolocationOverride", new { latitude = lat, longitude = lon, accuracy = 10 }, ct);
            done.Add($"geolocation {lat},{lon}");
        }
        return done.Count == 0 ? "nothing to change" : "applied: " + string.Join(", ", done);
    }

    /// <summary>Page.printToPDF → saves to disk.</summary>
    public async Task<string> PrintToPdfAsync(string outputPath, CancellationToken ct)
    {
        var res = await _session!.SendAsync("Page.printToPDF", new
        {
            printBackground = true,
            preferCSSPageSize = true,
        }, ct);
        var data = Convert.FromBase64String(res.GetProperty("data").GetString()!);
        await File.WriteAllBytesAsync(outputPath, data, ct);
        return $"PDF saved to {outputPath} ({data.Length / 1024}KB)";
    }

    public override async ValueTask DisposeAsync()
    {
        if (_session is not null)
        {
            try { _cdp.EventReceived -= OnCdpEvent; } catch { }
            try { await _cdp.SendAsync("Target.detachFromTarget", new { sessionId = _session.SessionId }, null, CancellationToken.None); } catch { }
        }
    }
}
