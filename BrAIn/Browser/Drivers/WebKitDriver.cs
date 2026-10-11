using System.Text.Json;
using BrAIn.Browser.Mac;

namespace BrAIn.Browser.Drivers;

/// <summary>
/// macOS driver: controls the Photino window's WKWebView via Objective-C
/// evaluateJavaScript (fire-and-forget) and receives results through the
/// window.external.sendMessage channel that Photino injects into every page.
/// </summary>
public sealed class WebKitDriver : DriverBase
{
    private IntPtr _webview;
    private long _windowNumber;
    private Timer? _watcher;
    private string _watchedUrl = "";
    private bool _loading, _sawLoad;
    private DateTime _loadStartAt;
    private int _evalCounter;
    // Per-tab mode: this driver owns exactly one tab's own WKWebView (Tab.NativeView).
    private bool _prebound;
    private bool _everLoaded;
    private Timer? _tabTimer;

    public WebKitDriver(BrowserTab tab, BrowserController controller) : base(tab, controller) { }

    public override string Name => "webkit";
    public override bool SupportsEval => true;
    public override bool SupportsScreenshot => true;
    public override bool SupportsRealInput => false; // JS-dispatched events only
    public override bool SupportsConsoleLog => true; // via brain.js console hook
    public override bool SupportsNetworkLog => true; // via PerformanceResourceTiming polling
    public override bool SupportsCookies => true;    // document.cookie only (no HttpOnly)

    public override Task InitializeAsync(BrowserController controller, CancellationToken ct)
        => Tab.NativeView != IntPtr.Zero ? InitializePerTab() : InitializeSharedAsync(controller, ct);

    /// <summary>Per-tab mode: the view already exists (WkTabs.Create) and carries
    /// its own scripts, so there is nothing to discover or inject.</summary>
    private Task InitializePerTab()
    {
        _prebound = true;
        _webview = Tab.NativeView;
        _windowNumber = Tab.MacWindowNumber;
        _watchedUrl = "";
        try { Controller.InvokeOnUi(() => MacWindow.EnablePictureInPicture(_webview)); } catch { }
        try { Controller.InvokeOnUi(() => WkDownloads.Install(_webview, Controller)); } catch { }
        StartTabWatcher();
        return Task.CompletedTask;
    }

    private async Task InitializeSharedAsync(BrowserController controller, CancellationToken ct)
    {
        // Resolve this tab's WKWebView: scan NSApplication windows.
        // Main tab: first window containing a WKWebView.
        // Child tabs: webview whose location.hash carries our braintab marker.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var boundNumbers = Controller.Tabs.All
                .Where(t => !ReferenceEquals(t, Tab))
                .Select(t => t.MacWindowNumber)
                .Where(n => n != 0)
                .ToHashSet();

            var found = Controller.InvokeOnUi(() =>
            {
                foreach (var win in MacWindow.AllWindows())
                {
                    var wv = MacWindow.FindWebView(win);
                    if (wv == IntPtr.Zero) continue;
                    var num = MacWindow.WindowNumber(win);
                    if (boundNumbers.Contains(num)) continue;
                    return (Window: win, WebView: wv, Number: num);
                }
                return default;
            });

            if (found.WebView != IntPtr.Zero)
            {
                if (Tab.IsMain)
                {
                    Bind(found.Number, found.WebView);
                    break;
                }
                // Child tab: confirm the marker by evaluating location.hash.
                var marker = Tab.LastUrl.Contains("braintab-")
                    ? Tab.LastUrl.Split('#').Last()
                    : null;
                var hash = await EvalOnWebViewAsync(found.WebView, "location.hash", ct);
                var hashStr = hash.HasValue && hash.Value.ValueKind == JsonValueKind.String
                    ? hash.Value.GetString() : null;
                if (marker is null || hashStr?.Contains(marker) == true)
                {
                    Bind(found.Number, found.WebView);
                    break;
                }
            }
            await Task.Delay(25, ct);
        }

        if (_webview == IntPtr.Zero)
            throw new InvalidOperationException("Could not locate the WKWebView for this window");

        // Wait for WKWebView's initial document to commit before releasing the driver as ready.
        // This completely eliminates the 30-second eval stall on early tool calls.
        var readyDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < readyDeadline)
        {
            ct.ThrowIfCancellationRequested();
            var loading = Controller.InvokeOnUi(() => MacWindow.IsWebViewLoading(_webview));
            if (!loading)
            {
                try
                {
                    using var probeCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, probeCts.Token);
                    var probeId = "init" + Interlocked.Increment(ref _evalCounter);
                    var res = await EvalOnWebViewAsync(_webview, probeId, linked.Token, TimeSpan.FromMilliseconds(150),
                        BuildEnvelope(probeId, "document.readyState === 'complete' || document.readyState === 'interactive'", false));
                    if (res?.ValueKind == JsonValueKind.True)
                        break;
                }
                catch
                {
                    // Frame still committing
                }
            }
            await Task.Delay(10, ct);
        }

        StartWatcher();
    }

    private void Bind(long windowNumber, IntPtr webview)
    {
        _windowNumber = windowNumber;
        _webview = webview;
        Tab.MacWindowNumber = windowNumber;
        try { _watchedUrl = ""; } catch { }
        try { Controller.InvokeOnUi(() => MacWindow.EnablePictureInPicture(webview)); } catch { }
        try { Controller.InvokeOnUi(() => WkDownloads.Install(webview, Controller)); } catch { }
        // Document-start injection: mounts the chrome bar while the page is
        // still parsing instead of waiting for the post-load eval path.
        try { Controller.InvokeOnUi(() => WkUserScript.Install(webview, Controller.PageLibSource)); } catch { }
    }

    /// <summary>Re-resolve the webview by remembered window number
    /// (webview pointer stays valid while the window lives, but be safe).</summary>
    private IntPtr ResolveWebView()
    {
        if (_webview != IntPtr.Zero || _prebound) return _webview;
        if (_windowNumber != 0)
        {
            _webview = Controller.InvokeOnUi(() =>
            {
                var win = MacWindow.WindowByNumber(_windowNumber);
                return win == IntPtr.Zero ? IntPtr.Zero : MacWindow.FindWebView(win);
            });
        }
        return _webview;
    }

    public override async Task<JsonElement?> EvaluateAsync(string expression, bool awaitPromise, CancellationToken ct)
    {
        var wv = ResolveWebView();
        if (wv == IntPtr.Zero)
            throw new InvalidOperationException("WKWebView not available (window closed?)");
        var id = "e" + Interlocked.Increment(ref _evalCounter);
        return await EvalOnWebViewAsync(wv, id, ct, null, BuildEnvelope(id, expression, awaitPromise));
    }

    /// <summary>Whole-program eval for helper injection and user scripts.
    /// Pass 1 uses real eval() so statement completion values survive. When a
    /// strict CSP refuses eval (EvalError → __ce sentinel), two raw-source
    /// envelopes are sent under the same pending id: expression-wrap first so
    /// expression-shaped scripts still return a value, then a statement body
    /// — they cannot share one script because a parse failure in either would
    /// kill the whole envelope before its try/catch ran. A __brk_<id>
    /// window marker set by whichever runs first prevents double execution;
    /// the loser's response is dropped by the pending table.</summary>
    public override async Task<JsonElement?> EvalScriptAsync(string script, bool awaitPromise, CancellationToken ct)
    {
        var wv = ResolveWebView();
        if (wv == IntPtr.Zero)
            throw new InvalidOperationException("WKWebView not available (window closed?)");
        var id = "e" + Interlocked.Increment(ref _evalCounter);
        var res = await EvalOnWebViewAsync(wv, id, ct, null, BuildScriptEnvelope(id, script, awaitPromise));
        if (res is { ValueKind: JsonValueKind.Object } el
            && el.TryGetProperty("__ce", out var ce) && ce.ValueKind == JsonValueKind.True)
        {
            res = await EvalOnWebViewAsync(wv, id, ct, null,
                BuildRawExprEnvelope(id, script, awaitPromise),
                BuildRawStmtEnvelope(id, script, awaitPromise));
        }
        return res;
    }

    private async Task<JsonElement?> EvalOnWebViewAsync(
        IntPtr webview, string id, CancellationToken ct, TimeSpan? timeout = null, params string[] scripts)
    {
        var pending = Controller.RegisterPendingEval(id, ct);

        foreach (var script in scripts)
        {
            var s = script;
            Controller.InvokeOnUi(() =>
            {
                if (_prebound && !WkTabs.Has(webview)) return; // the tab was closed meanwhile
                var ns = ObjC.NsString(s);
                ObjC.Send(webview, ObjC.Sel("evaluateJavaScript:completionHandler:"), ns, IntPtr.Zero);
            });
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout ?? TimeSpan.FromSeconds(6));
        await using var reg = timeoutCts.Token.Register(() =>
            BrowserController.Log("webkit eval timed out")).ConfigureAwait(false);

        try
        {
            return await pending.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("WKWebView eval timed out (page may be navigating)");
        }
    }

    /// <summary>
    /// Wraps a user expression in a promise-aware envelope that posts the
    /// JSON-serialized result back via window.external.sendMessage.
    /// The expression is inlined as source: evaluateJavaScript's own script
    /// is CSP-exempt, but an inner eval()/new Function() call is not — strict
    /// script-src pages (github.com, most Google properties) rejected every
    /// eval wrapped as (0,eval)("...").
    /// </summary>
    internal static string BuildEnvelope(string id, string expression, bool awaitPromise)
    {
        var idJson = JsonSerializer.Serialize(id);
        var awaitFlag = awaitPromise ? "true" : "false";
        return
            "(function(){" +
            "var __id=" + idJson + ";" +
            "function __fin(ok,v){" +
            "try{window.external.sendMessage(JSON.stringify({b:1,id:__id,ok:ok,v:v===undefined?null:v}))}" +
            "catch(e){try{window.external.sendMessage(JSON.stringify({b:1,id:__id,ok:false,v:String(e)}))}catch(_){}}" +
            "}" +
            "try{" +
            "var __r=(" + expression + ");" +
            "if(" + awaitFlag + "&&__r&&typeof __r.then==='function'){" +
            "__r.then(function(v){__fin(true,v)},function(e){__fin(false,String(e&&e.stack||e))});" +
            "} else { __fin(true,__r); }" +
            "}catch(e){ __fin(false,String(e&&e.stack||e)); }" +
            "})();";
    }

    /// <summary>
    /// Envelope for whole programs (statement lists, helper injection, user
    /// scripts). Real eval() preserves the completion value of the last
    /// statement. When CSP refuses eval it throws EvalError — reported back
    /// as {__ce:true} so EvalScriptAsync can re-send the source embedded
    /// raw in a pair of envelopes (evaluateJavaScript's own source is
    /// CSP-exempt).
    /// </summary>
    internal static string BuildScriptEnvelope(string id, string script, bool awaitPromise)
    {
        var idJson = JsonSerializer.Serialize(id);
        var scriptJson = JsonSerializer.Serialize(script);
        var awaitFlag = awaitPromise ? "true" : "false";
        return
            "(function(){" +
            "var __id=" + idJson + ";" +
            FinFunc +
            // CSP without unsafe-eval throws EvalError, but Trusted Types
            // (YouTube, Google) throws a TypeError instead — so probe whether
            // string eval is available at all rather than sniffing the error
            // class, which would also misread a script's own TypeError.
            "var __ok=true;try{(0,eval)('0')}catch(_){__ok=false}" +
            "if(!__ok){__fin(true,{__ce:true});return}" +
            "try{" +
            "var __r=(0,eval)(" + scriptJson + ");" +
            "if(" + awaitFlag + "&&__r&&typeof __r.then==='function'){" +
            "__r.then(function(v){__fin(true,v)},function(e){__fin(false,String(e&&e.stack||e))});" +
            "} else { __fin(true,__r); }" +
            // A SyntaxError means nothing ran (e.g. a top-level 'return') —
            // hand the source to the function-body envelopes instead.
            "}catch(__ce){if(__ce instanceof SyntaxError){__fin(true,{__ce:true})}" +
            "else{__fin(false,String(__ce&&__ce.stack||__ce))}}" +
            "})();";
    }

    private const string FinFunc =
        "function __fin(ok,v){" +
        "try{window.external.sendMessage(JSON.stringify({b:1,id:__id,ok:ok,v:v===undefined?null:v}))}" +
        "catch(e){try{window.external.sendMessage(JSON.stringify({b:1,id:__id,ok:false,v:String(e)}))}catch(_){}}" +
        "}";

    /// <summary>CSP fallback 1/2: the script embedded in expression position.
    /// Sets a per-eval marker before running so the statement envelope (sent
    /// alongside) skips itself; if the source isn't expression-shaped this
    /// whole envelope fails to parse and simply never runs.</summary>
    internal static string BuildRawExprEnvelope(string id, string script, bool awaitPromise)
    {
        var idJson = JsonSerializer.Serialize(id);
        var awaitFlag = awaitPromise ? "true" : "false";
        return
            "(function(){" +
            "var __id=" + idJson + ",__m=\"__brk_\"+__id;" +
            FinFunc +
            "window[__m]=1;" +
            "try{" +
            "var __r=(function(){return (" + script + "\n);})();" +
            "if(" + awaitFlag + "&&__r&&typeof __r.then==='function'){" +
            "__r.then(function(v){__fin(true,v)},function(e){__fin(false,String(e&&e.stack||e))});" +
            "} else { __fin(true,__r); }" +
            "}catch(e){ __fin(false,String(e&&e.stack||e)); }" +
            "})();";
    }

    /// <summary>CSP fallback 2/2: the script embedded as a function body.
    /// Runs only when the expression envelope never executed (marker unset)
    /// — i.e. when the source is statement-shaped and the sibling envelope
    /// failed to parse. Explicit 'return' still produces a value.</summary>
    internal static string BuildRawStmtEnvelope(string id, string script, bool awaitPromise)
    {
        var idJson = JsonSerializer.Serialize(id);
        var awaitFlag = awaitPromise ? "true" : "false";
        return
            "(function(){" +
            "var __id=" + idJson + ",__m=\"__brk_\"+__id;" +
            FinFunc +
            "if(window[__m])return;window[__m]=1;" +
            "try{" +
            "var __r=(function(){" + script + "\n})();" +
            "if(" + awaitFlag + "&&__r&&typeof __r.then==='function'){" +
            "__r.then(function(v){__fin(true,v)},function(e){__fin(false,String(e&&e.stack||e))});" +
            "} else { __fin(true,__r); }" +
            "}catch(e){ __fin(false,String(e&&e.stack||e)); }" +
            "})();";
    }

    // ---------------- navigation ----------------

    /// <summary>Guarded load events — WKWebView has no navigation callbacks,
    /// so Started/Completed come from three sources that can overlap (explicit
    /// navigation, the load-flag watcher, the pagehide beacon). Started
    /// only fires once per load; Completed only fires while loading, so any
    /// path may end it but no path can double-count it.</summary>
    protected override void RaiseLoadStarted()
    {
        if (_loading) return;
        _loading = true;
        _sawLoad = false;
        _loadStartAt = DateTime.UtcNow;
        base.RaiseLoadStarted();
    }

    protected override bool PageLoading
    {
        get { var wv = _webview; return wv != IntPtr.Zero && Controller.InvokeOnUi(() => Alive(wv) && MacWindow.IsWebViewLoading(wv)); }
    }

    protected override void RaiseLoadCompleted()
    {
        if (!_loading) return;
        _loading = false;
        base.RaiseLoadCompleted();
    }

    /// <summary>brain.js 'pagehide' beacon: a navigation away from the live
    /// document is starting (link click, form submit, JS redirect). The
    /// watcher ends it when the next document is interactive — or sweeps it
    /// on the next tick if the navigation aborted.</summary>
    internal void MarkLoadStarted() => RaiseLoadStarted();

    /// <summary>WKWebView has no network event stream — the DevTools pane
    /// pulls PerformanceResourceTiming deltas out of the live document
    /// (resource + navigation entries) and feeds them into the shared log.
    /// Runs only while the panel polls dtget, so it costs nothing when
    /// DevTools is closed.</summary>
    private int _netCounter;

    public override async Task PullNetworkAsync(CancellationToken ct)
    {
        if (_webview == IntPtr.Zero) return;
        try
        {
            var res = await EvaluateAsync(
                "window.__brain && window.__brain.networkLog ? JSON.stringify(window.__brain.networkLog()) : '[]'",
                false, ct);
            if (res?.ValueKind != JsonValueKind.String) return;
            using var doc = JsonDocument.Parse(res.Value.GetString()!);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var url = e.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (string.IsNullOrEmpty(url)) continue;
                var when = e.TryGetProperty("t", out var t) && t.ValueKind == JsonValueKind.Number
                    ? DateTimeOffset.FromUnixTimeMilliseconds(t.GetInt64()).LocalDateTime
                    : DateTime.Now;
                RaiseNetwork(new NetworkEntry(
                    when,
                    "wk" + Interlocked.Increment(ref _netCounter),
                    "GET", url,
                    null, // status not exposed by the timing API
                    e.TryGetProperty("type", out var ty) ? ty.GetString() : null,
                    e.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0,
                    null));
            }
        }
        catch { /* context mid-navigation */ }
    }

    /// <summary>Bulk cookie write into the webview's WKWebsiteDataStore for
    /// the browser-data importer. ObjC calls must land on the UI thread.</summary>
    public override Task<int> ImportCookiesAsync(
        IReadOnlyList<Import.ImportedCookie> cookies, CancellationToken ct)
    {
        var wv = ResolveWebView();
        if (wv == IntPtr.Zero) return Task.FromResult(0);
        return Task.FromResult(Controller.InvokeOnUi(() => WkCookieStore.SetCookies(wv, cookies)));
    }

    private bool Alive(IntPtr wv) => !_prebound || WkTabs.Has(wv);

    public override async Task<NavigateResult> NavigateAsync(string url, CancellationToken ct)
    {
        if (_prebound) return await NavigateNativeAsync(url, ct);
        // Mark the outgoing document so WaitForNewDocAsync can tell when the
        // new one commits — readyState still describes the OLD page until then.
        var navTok = "__navW" + Interlocked.Increment(ref _evalCounter);
        try { await EvaluateAsync("window." + navTok + "=1", false, ct); } catch { }
        RaiseLoadStarted();
        Controller.InvokeOnUi(() => Tab.Window.Load(new Uri(url)));
        _watchedUrl = url;
        await WaitForNewDocAsync(navTok, ct);
        return NavigateResult.Success(await GetUrlSafeAsync(ct), await GetTitleSafeAsync(ct));
    }

    /// <summary>Per-tab navigation: loads into this tab's own view and waits on
    /// WebKit's own loading flag (no JS needed, so it also works on a view that has
    /// never held a document, or one showing an error / PDF page).</summary>
    private async Task<NavigateResult> NavigateNativeAsync(string url, CancellationToken ct)
    {
        RaiseLoadStarted();
        Controller.InvokeOnUi(() => { if (Alive(_webview)) WkTabs.Load(_webview, url); });
        _watchedUrl = url;
        await WaitNativeLoadAsync(ct);
        _everLoaded = true;
        return NavigateResult.Success(await GetUrlSafeAsync(ct), await GetTitleSafeAsync(ct));
    }

    /// <summary>Internal pages (start page, history…): raw HTML into this tab's view.</summary>
    public async Task LoadHtmlAsync(string html, CancellationToken ct)
    {
        RaiseLoadStarted();
        Controller.InvokeOnUi(() => { if (Alive(_webview)) WkTabs.LoadHtml(_webview, html); });
        _watchedUrl = "about:blank";
        await WaitNativeLoadAsync(ct, 8000);
        _everLoaded = true;
    }

    /// <summary>Waits until WebKit reports the view finished loading. Returns
    /// early when no load ever started (blocked/cancelled navigation).</summary>
    private async Task WaitNativeLoadAsync(CancellationToken ct, int timeoutMs = 20000)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        var saw = false; var idle = 0;
        await Task.Delay(40, ct);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (PageLoading) { saw = true; idle = 0; }
            else
            {
                idle++;
                if (saw && idle >= 2) break;   // finished
                if (!saw && idle >= 8) break;  // never started
            }
            await Task.Delay(60, ct);
        }
        if (saw) await Task.Delay(150, ct); // let SPA hydration run before helpers inject
        RaiseLoadCompleted();
    }

    public override Task<(bool CanBack, bool CanFwd)> GetNavStateAsync(CancellationToken ct)
    {
        if (!_prebound) return base.GetNavStateAsync(ct);
        var wv = _webview;
        if (wv == IntPtr.Zero) return Task.FromResult((false, false));
        return Task.FromResult(Controller.InvokeOnUi(() =>
            Alive(wv) ? (WkTabs.CanGoBack(wv), WkTabs.CanGoForward(wv)) : (false, false)));
    }

    /// <summary>Polls until the pre-navigation marker is gone (new document
    /// committed) and its readyState is interactive+. WKWebView exposes no
    /// commit callback, so the token is the only reliable boundary.</summary>
    private async Task WaitForNewDocAsync(string navTok, CancellationToken ct, int timeoutMs = 15000)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        await Task.Delay(120, ct);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var rs = await EvaluateAsync(
                    "window." + navTok + " ? 'pending' : document.readyState", false, ct);
                if (rs?.ValueKind == JsonValueKind.String &&
                    (rs.Value.GetString() is "interactive" or "complete") && !PageLoading)
                {
                    await Task.Delay(150, ct); // settle — SPA hydration
                    RaiseLoadCompleted();
                    return;
                }
            }
            catch { /* context mid-swap */ }
            await Task.Delay(200, ct);
        }
        RaiseLoadCompleted(); // timeout — still let helpers inject
    }

    // ---------------- screenshots ----------------

    public override async Task<byte[]> ScreenshotAsync(RectD? clip, bool fullPage, CancellationToken ct)
    {
        if (fullPage && clip is null)
        {
            var tall = await FullPageShotAsync(ct);
            if (tall is not null) return tall;
            // fall through: at least give the visible part
        }
        var snapView = ResolveWebView();
        if (snapView != IntPtr.Zero)
        {
            var snap = await SnapViewAsync(snapView, ct);
            if (snap != IntPtr.Zero)
            {
                if (clip is { } c0)
                {
                    double vwCss = 0;
                    try { vwCss = (await EvaluateAsync("innerWidth", false, ct))?.GetDouble() ?? 0; } catch { }
                    var k = vwCss > 0 ? MacWindow.ImageWidth(snap) / vwCss : 1;
                    snap = MacWindow.CropImage(snap, c0.X * k, c0.Y * k, c0.Width * k, c0.Height * k);
                }
                if (snap != IntPtr.Zero)
                {
                    var shot = Controller.InvokeOnUi(() => MacWindow.ImageToPng(snap));
                    MacWindow.ReleaseImage(snap);
                    if (shot is { Length: > 0 }) return shot;
                }
            }
        }

        var png = Controller.InvokeOnUi(() =>
        {
            var win = _windowNumber != 0 ? MacWindow.WindowByNumber(_windowNumber) : IntPtr.Zero;
            if (win == IntPtr.Zero) return null;

            // Convert CSS clip to image pixels via the backing scale factor.
            // CGWindowList coordinates are in window-content points.
            MacWindow.CGRect? clipPx = null;
            if (clip is { } c)
            {
                var scale = MacWindow.BackingScale(win);
                clipPx = new MacWindow.CGRect
                {
                    X = c.X * scale,
                    Y = c.Y * scale,
                    W = c.Width * scale,
                    H = c.Height * scale,
                };
            }
            return MacWindow.CapturePng(win, clipPx);
        });

        if (png is null || png.Length == 0)
            throw new InvalidOperationException("Window capture failed");
        return png;
    }

    // ---- scrolling screenshot: scroll the page one viewport at a time, grab the
    // view's pixels after each step, and paint the grabs into one tall bitmap.

    private const string ShotPrepJs = @"(function(){
 var S=window.__brainShot={}, de=document.documentElement, b=document.body;
 var st=document.createElement('style'); st.textContent='html,body,*{scroll-behavior:auto !important}'; (document.head||de).appendChild(st); S.style=st;
 function docH(){return Math.max(de.scrollHeight,b?b.scrollHeight:0);}
 var sc=document.scrollingElement||de; S.sc=sc; S.inner=false;
 if(docH()<=innerHeight+4&&b){
  var best=null,bestA=0,all=b.querySelectorAll('*');
  for(var i=0;i<all.length&&i<8000;i++){var e=all[i];
   if(e.scrollHeight<=e.clientHeight+4||e.clientHeight<innerHeight*0.5||e.clientWidth<innerWidth*0.5)continue;
   var o=getComputedStyle(e).overflowY; if(o!=='auto'&&o!=='scroll'&&o!=='overlay')continue;
   var a=e.clientHeight*e.clientWidth; if(a>bestA){bestA=a;best=e;}}
  if(best){S.sc=best;S.inner=true;}
 }
 S.hid=[]; S.hideOn=false;
 S.hide=function(){ if(!S.hideOn)return;
  var all=document.querySelectorAll('body *'),vw=innerWidth,vh=innerHeight;
  for(var i=0;i<all.length&&i<30000;i++){var e=all[i]; if(e.__bsh)continue;
   var p=getComputedStyle(e).position; if(p!=='fixed'&&p!=='sticky')continue;
   if(e.contains(S.sc))continue;
   var r=e.getBoundingClientRect(); if(r.width*r.height>vw*vh*0.7)continue;
   var o=e.style; e.__bsh=1;
   S.hid.push([e,o.getPropertyValue('opacity'),o.getPropertyPriority('opacity'),o.getPropertyValue('transition'),o.getPropertyPriority('transition')]);
   o.setProperty('transition','none','important'); o.setProperty('opacity','0','important');}
 };
 S.x=scrollX; S.y=S.sc.scrollTop;
 S.total=function(){return S.inner?S.sc.scrollHeight:docH();};
 var r=S.inner?S.sc.getBoundingClientRect():{left:0,top:0,width:innerWidth,height:innerHeight};
 S.sc.scrollTo(0,0);
 return {total:S.total(),vw:innerWidth,rx:r.left,ry:r.top,rw:r.width,rh:r.height,y0:S.y};
})()";

    private const string ShotStepJs = @"(function(y){var S=window.__brainShot,sc=S.sc;
 return new Promise(function(res){var done=false;
  function fin(){if(done)return;done=true;res({y:sc.scrollTop,total:S.total()});}
  sc.scrollTo(0,y);
  // pages add their pinned bars from scroll handlers, so look for them again after the scroll settles
  requestAnimationFrame(function(){requestAnimationFrame(function(){setTimeout(function(){
   try{S.hide();}catch(e){}
   requestAnimationFrame(function(){requestAnimationFrame(fin);});},150);});});
  setTimeout(fin,900);
 });})(__Y__)";

    // fixed / sticky bars would repeat in every frame — hide them after the first one
    private const string ShotHideFixedJs = @"(function(){var S=window.__brainShot; if(!S)return 0; S.hideOn=true; S.hide(); return S.hid.length;})()";

    private const string ShotRestoreJs = @"(function(){var S=window.__brainShot; if(!S)return 0;
 (S.hid||[]).forEach(function(a){var o=a[0].style;
  if(a[1])o.setProperty('opacity',a[1],a[2]);else o.removeProperty('opacity');
  if(a[3])o.setProperty('transition',a[3],a[4]);else o.removeProperty('transition');
  try{delete a[0].__bsh;}catch(e){}});
 if(S.style)S.style.remove();
 try{S.sc.scrollTo(S.inner?0:S.x,S.y);}catch(e){}
 delete window.__brainShot; return 1;})()";

    private const int ShotMaxFrames = 120;

    /// <summary>Whole-page PNG, or null when the page can't be captured this way
    /// (no eval, view hidden, bitmap would be too big...).</summary>
    private async Task<byte[]?> FullPageShotAsync(CancellationToken ct)
    {
        var wv = ResolveWebView();
        if (wv == IntPtr.Zero) return null;
        JsonElement? meta;
        try { meta = await EvaluateAsync(ShotPrepJs, false, ct); }
        catch { return null; }
        if (meta is not { ValueKind: JsonValueKind.Object } m) return null;

        double total = m.GetProperty("total").GetDouble();
        double vw = m.GetProperty("vw").GetDouble();
        double rx = m.GetProperty("rx").GetDouble(), ry = m.GetProperty("ry").GetDouble();
        double rw = m.GetProperty("rw").GetDouble(), rh = m.GetProperty("rh").GetDouble();
        MacWindow.ShotLog($"prep total={total} vw={vw} rect={rx},{ry} {rw}x{rh}");
        if (total < 1 || vw < 1 || rw < 1 || rh < 1) { await ShotRestoreAsync(); return null; }

        MacWindow.Canvas? canvas = null;
        try
        {
            double scale = 0;
            double y = 0;
            for (var frame = 0; frame < ShotMaxFrames; frame++)
            {
                ct.ThrowIfCancellationRequested();
                var r = await EvaluateAsync(ShotStepJs.Replace("__Y__", ((long)Math.Round(y)).ToString()), true, ct);
                if (r is not { ValueKind: JsonValueKind.Object } st) break;
                var actual = st.GetProperty("y").GetDouble();
                total = Math.Max(total, st.GetProperty("total").GetDouble());

                var img = await SnapViewAsync(wv, ct);
                if (img == IntPtr.Zero) { MacWindow.ShotLog("grab failed"); break; }

                if (scale == 0)
                {
                    scale = MacWindow.ImageWidth(img) / vw; // device pixels per CSS pixel (retina and page zoom included)
                    var cw = (int)Math.Round(rw * scale);
                    var ch = (int)Math.Round(total * scale);
                    var maxH = (int)(MacWindow.MaxCanvasBytes / 4 / Math.Max(1, cw));
                    canvas = MacWindow.Canvas.Create(cw, Math.Min(ch, Math.Min(maxH, 60000)));
                    MacWindow.ShotLog($"scale={scale} canvas={cw}x{ch}");
                    if (canvas is null) { MacWindow.ReleaseImage(img); break; }
                }

                img = MacWindow.CropImage(img, rx * scale, ry * scale, rw * scale, rh * scale);
                if (img == IntPtr.Zero) break;
                MacWindow.ShotLog($"frame {frame}: requested y={y} actual={actual} total={total}");
                MacWindow.ShotDump("frame" + frame, img);
                canvas!.Draw(img, (int)Math.Round(actual * scale));
                MacWindow.ReleaseImage(img);

                var shown = actual + rh;
                var clamped = actual < y - 1;                   // the page refused to scroll further
                if (clamped || shown >= total - 1 || (actual + rh) * scale >= canvas.Height) break;
                if (frame == 0) { try { await EvaluateAsync(ShotHideFixedJs, false, ct); } catch { } }
                y = actual + rh;
            }
            var outPng = canvas is null ? null : Controller.InvokeOnUi(() => canvas.ToPng());
            if (MacWindow.ShotDebug && outPng is not null) File.WriteAllBytes(Path.Combine(MacWindow.ShotDir, "brain-shot-final.png"), outPng);
            return outPng;
        }
        finally
        {
            canvas?.Dispose();
            await ShotRestoreAsync();
        }
    }

    private static readonly SemaphoreSlim SnapGate = new(1, 1);

    /// <summary>Visible area of a view as a retained CGImage (zero on failure).</summary>
    private async Task<IntPtr> SnapViewAsync(IntPtr wv, CancellationToken ct)
    {
        await SnapGate.WaitAsync(ct);
        try
        {
            var t = Controller.InvokeOnUi(() => Alive(wv) ? MacWindow.BeginSnapshot(wv) : null);
            if (t is null) return IntPtr.Zero;
            var done = await Task.WhenAny(t, Task.Delay(8000, ct));
            return done == t ? t.Result : IntPtr.Zero;
        }
        finally { SnapGate.Release(); }
    }

    private async Task ShotRestoreAsync()
    {
        try { await EvaluateAsync(ShotRestoreJs, false, CancellationToken.None); } catch { }
    }

    // ---------------- helpers ----------------

    public override void SetPageMuted(bool muted)
    {
        var wv = _webview;
        if (wv == IntPtr.Zero) return;
        Controller.InvokeOnUi(() => { if (Alive(wv)) MacWindow.SetWebViewMuted(wv, muted); });
    }

    private Timer? _audioTimer;
    private bool _audible;

    /// <summary>Polls WKWebView's native "is playing audio" flag so the tab
    /// shows a speaker while sound plays; only pushes on change.</summary>
    private void StartAudioWatcher()
    {
        _audioTimer = new Timer(_ =>
        {
            try
            {
                var wv = _webview;
                if (wv == IntPtr.Zero) return;
                var on = Controller.InvokeOnUi(() => MacWindow.IsWebViewPlayingAudio(wv));
                if (on == _audible) return;
                _audible = on;
                Controller.SetPageAudible(on);
            }
            catch { /* webview gone or transitioning */ }
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(700));
    }

    /// <summary>URL the web view is showing right now (native, no JS).</summary>
    internal string? NativeUrl()
    {
        var wv = _webview;
        if (wv == IntPtr.Zero) return null;
        try { return Controller.InvokeOnUi(() => Alive(wv) ? MacWindow.GetWebViewUrl(wv) : null); }
        catch { return null; }
    }

    private int _watchTick;
    private void StartWatcher()
    {
        StartAudioWatcher();
        _watcher = new Timer(_ =>
        {
            try
            {
                // When idle, only check occasionally (~2.5s). When loading, check every tick.
                if (!_loading && (Interlocked.Increment(ref _watchTick) % 5 != 0))
                    return;

                var loading = PageLoading;
                if (loading) { RaiseLoadStarted(); _sawLoad = true; }

                var age = DateTime.UtcNow - _loadStartAt;
                if (_loading && (!loading && _sawLoad || !_sawLoad && age > TimeSpan.FromSeconds(4) || age > TimeSpan.FromSeconds(60)))
                    RaiseLoadCompleted();

                // Native URL inspection — zero JS evaluation, zero allocations, zero CPU waste
                var wv = _webview;
                if (wv != IntPtr.Zero)
                {
                    var url = Controller.InvokeOnUi(() => MacWindow.GetWebViewUrl(wv));
                    if (!string.IsNullOrEmpty(url) && url != _watchedUrl)
                    {
                        _watchedUrl = url;
                        // Right after a tab switch the view still shows the PREVIOUS
                        // tab's page; stamping it onto the new owner is what made two
                        // tabs end up on the same site.
                        if (Controller.IsStaleDocUrl(url)) return;
                        Controller.ClearStaleDoc();
                        // the shared webview shows the doc owner's page, not necessarily the driver's own tab
                        BrowserController.SetTabUrl(Controller.DocOwner ?? Tab, url);
                        if (!loading) { RaiseLoadStarted(); RaiseLoadCompleted(); }
                    }
                }
            }
            catch { /* page transitioning or webview gone */ }
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(500));
    }

    /// <summary>Per-tab watcher: one native read per tick (loading flag, URL,
    /// audio) for THIS tab's view only. Idle tabs are checked every ~1.5s, loading
    /// tabs every tick — and because the view is the tab's own, nothing read here
    /// can belong to another tab.</summary>
    private void StartTabWatcher()
    {
        _tabTimer = new Timer(_ =>
        {
            try
            {
                if (!_loading && (Interlocked.Increment(ref _watchTick) % 3 != 0)) return;
                var wv = _webview;
                if (wv == IntPtr.Zero || Tab.IsClosed) return;
                var (loading, url, audible) = Controller.InvokeOnUi(() => WkTabs.Has(wv)
                    ? (MacWindow.IsWebViewLoading(wv), MacWindow.GetWebViewUrl(wv), MacWindow.IsWebViewPlayingAudio(wv))
                    : (false, (string?)null, false));

                if (audible != _audible) { _audible = audible; Controller.SetTabAudible(Tab, audible); }

                if (loading) { RaiseLoadStarted(); _sawLoad = true; }
                var age = DateTime.UtcNow - _loadStartAt;
                if (_loading && (!loading && _sawLoad || !_sawLoad && age > TimeSpan.FromSeconds(4) || age > TimeSpan.FromSeconds(60)))
                    RaiseLoadCompleted();

                if (!string.IsNullOrEmpty(url) && url != _watchedUrl)
                {
                    _watchedUrl = url;
                    // Raw-HTML pages (start page, history…) and empty views report
                    // about:blank; their logical URL is tracked by the controller.
                    if (url == "about:blank") return;
                    BrowserController.SetTabUrl(Tab, url);
                    if (!loading) { RaiseLoadStarted(); RaiseLoadCompleted(); }
                }
            }
            catch { /* view transitioning or closed */ }
        }, null, TimeSpan.FromMilliseconds(700), TimeSpan.FromMilliseconds(500));
    }

    public override async ValueTask DisposeAsync()
    {
        _webview = IntPtr.Zero;
        try { if (_tabTimer is not null) await _tabTimer.DisposeAsync(); } catch { }
        try { if (_watcher is not null) await _watcher.DisposeAsync(); } catch { }
        try { if (_audioTimer is not null) await _audioTimer.DisposeAsync(); } catch { }
    }
}
