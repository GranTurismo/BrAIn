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
    private int _evalCounter;

    public WebKitDriver(BrowserTab tab, BrowserController controller) : base(tab, controller) { }

    public override string Name => "webkit";
    public override bool SupportsEval => true;
    public override bool SupportsScreenshot => true;
    public override bool SupportsRealInput => false; // JS-dispatched events only
    public override bool SupportsConsoleLog => true; // via brain.js console hook
    public override bool SupportsNetworkLog => true; // via PerformanceResourceTiming polling
    public override bool SupportsCookies => true;    // document.cookie only (no HttpOnly)

    public override async Task InitializeAsync(BrowserController controller, CancellationToken ct)
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
            await Task.Delay(300, ct);
        }

        if (_webview == IntPtr.Zero)
            throw new InvalidOperationException("Could not locate the WKWebView for this window");

        await Controller.EnsurePageHelpersAsync(Tab);
        StartWatcher();
    }

    private void Bind(long windowNumber, IntPtr webview)
    {
        _windowNumber = windowNumber;
        _webview = webview;
        Tab.MacWindowNumber = windowNumber;
        try { _watchedUrl = ""; } catch { }
    }

    /// <summary>Re-resolve the webview by remembered window number
    /// (webview pointer stays valid while the window lives, but be safe).</summary>
    private IntPtr ResolveWebView()
    {
        if (_webview != IntPtr.Zero) return _webview;
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
        return await EvalOnWebViewAsync(wv, expression, ct, awaitPromise);
    }

    private async Task<JsonElement?> EvalOnWebViewAsync(IntPtr webview, string expression, CancellationToken ct, bool awaitPromise = true)
    {
        var id = "e" + Interlocked.Increment(ref _evalCounter);
        var script = BuildEnvelope(id, expression, awaitPromise);
        var pending = Controller.RegisterPendingEval(id, ct);

        Controller.InvokeOnUi(() =>
        {
            var ns = ObjC.NsString(script);
            ObjC.Send(webview, ObjC.Sel("evaluateJavaScript:completionHandler:"), ns, IntPtr.Zero);
        });

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
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
    /// </summary>
    internal static string BuildEnvelope(string id, string expression, bool awaitPromise)
    {
        var idJson = JsonSerializer.Serialize(id);
        var exprJson = JsonSerializer.Serialize(expression);
        var awaitFlag = awaitPromise ? "true" : "false";
        return
            "(function(){" +
            "var __id=" + idJson + ";" +
            "function __fin(ok,v){" +
            "try{window.external.sendMessage(JSON.stringify({b:1,id:__id,ok:ok,v:v===undefined?null:v}))}" +
            "catch(e){try{window.external.sendMessage(JSON.stringify({b:1,id:__id,ok:false,e:String(e)}))}catch(_){}}" +
            "}" +
            "try{" +
            "var __r=(0,eval)(" + exprJson + ");" +
            "if(" + awaitFlag + "&&__r&&typeof __r.then==='function'){" +
            "__r.then(function(v){__fin(true,v)},function(e){__fin(false,String(e&&e.stack||e))});" +
            "} else { __fin(true,__r); }" +
            "}catch(e){ __fin(false,String(e&&e.stack||e)); }" +
            "})();";
    }

    // ---------------- navigation ----------------

    public override async Task<NavigateResult> NavigateAsync(string url, CancellationToken ct)
    {
        Controller.InvokeOnUi(() => Tab.Window.Load(new Uri(url)));
        _watchedUrl = url;
        await WaitForLoadAsync(ct);
        return NavigateResult.Success(await GetUrlSafeAsync(ct), await GetTitleSafeAsync(ct));
    }

    // ---------------- screenshots ----------------

    public override async Task<byte[]> ScreenshotAsync(RectD? clip, bool fullPage, CancellationToken ct)
    {
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

    // ---------------- helpers ----------------

    private void StartWatcher()
    {
        _watcher = new Timer(async _ =>
        {
            try
            {
                var info = await EvaluateAsync(
                    "JSON.stringify({u:location.href,r:document.readyState})", false, CancellationToken.None);
                if (info?.ValueKind != JsonValueKind.String) return;
                using var doc = JsonDocument.Parse(info.Value.GetString()!);
                var url = doc.RootElement.GetProperty("u").GetString() ?? "";
                if (url != _watchedUrl && !string.IsNullOrEmpty(url))
                {
                    _watchedUrl = url;
                    Tab.LastUrl = url;
                    RaiseLoadCompleted();
                }
            }
            catch { /* page transitioning or webview gone */ }
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(800));
    }

    public override async ValueTask DisposeAsync()
    {
        try { if (_watcher is not null) await _watcher.DisposeAsync(); } catch { }
    }
}
