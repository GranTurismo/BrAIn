using System.Text.Json;

namespace BrAIn.Browser.Drivers;

/// <summary>
/// Shared driver behavior: navigation via Photino load + readyState polling,
/// input via JS event dispatch (works on every platform), cookies via
/// document.cookie. Concrete drivers override what their platform can do
/// natively (CDP on Windows, evaluateJavaScript on macOS).
/// </summary>
public abstract class DriverBase : IBrowserDriver
{
    protected readonly BrowserTab Tab;
    protected readonly BrowserController Controller;

    protected DriverBase(BrowserTab tab, BrowserController controller)
    {
        Tab = tab;
        Controller = controller;
    }

    public abstract string Name { get; }
    public abstract bool SupportsEval { get; }
    public virtual bool SupportsScreenshot => false;
    public virtual bool SupportsRealInput => false;
    public virtual bool SupportsConsoleLog => false;
    public virtual bool SupportsNetworkLog => false;
    public virtual bool SupportsCookies => SupportsEval;
    public virtual bool SupportsViewportEmulation => false;

    public event EventHandler<ConsoleEntry>? ConsoleMessage;
    public event EventHandler<NetworkEntry>? NetworkEvent;
    public event EventHandler? LoadStarted;
    public event EventHandler? LoadCompleted;

    protected void RaiseConsole(ConsoleEntry e) => ConsoleMessage?.Invoke(this, e);
    protected void RaiseNetwork(NetworkEntry e) => NetworkEvent?.Invoke(this, e);
    protected void RaiseLoadStarted() => LoadStarted?.Invoke(this, EventArgs.Empty);
    protected void RaiseLoadCompleted() => LoadCompleted?.Invoke(this, EventArgs.Empty);

    public virtual Task InitializeAsync(BrowserController controller, CancellationToken ct)
        => Task.CompletedTask;

    public abstract Task<JsonElement?> EvaluateAsync(string expression, bool awaitPromise, CancellationToken ct);

    // ---------------- navigation ----------------

    public virtual async Task<NavigateResult> NavigateAsync(string url, CancellationToken ct)
    {
        RaiseLoadStarted();
        Controller.InvokeOnUi(() => Tab.Window.Load(new Uri(url)));
        await WaitForLoadAsync(ct);
        return NavigateResult.Success(url, await GetTitleSafeAsync(ct));
    }

    public virtual async Task<NavigateResult> GoBackAsync(CancellationToken ct)
    {
        RaiseLoadStarted();
        await EvalVoidAsync("history.back()", ct);
        await WaitForLoadAsync(ct);
        return NavigateResult.Success(await GetUrlSafeAsync(ct), await GetTitleSafeAsync(ct));
    }

    public virtual async Task<NavigateResult> GoForwardAsync(CancellationToken ct)
    {
        RaiseLoadStarted();
        await EvalVoidAsync("history.forward()", ct);
        await WaitForLoadAsync(ct);
        return NavigateResult.Success(await GetUrlSafeAsync(ct), await GetTitleSafeAsync(ct));
    }

    public virtual async Task<NavigateResult> ReloadAsync(bool ignoreCache, CancellationToken ct)
    {
        RaiseLoadStarted();
        await EvalVoidAsync($"location.reload({(ignoreCache ? "true" : "false")})", ct);
        await WaitForLoadAsync(ct);
        return NavigateResult.Success(await GetUrlSafeAsync(ct), await GetTitleSafeAsync(ct));
    }

    /// <summary>Approximation via the Navigation API (same-document entries).
    /// CDP overrides this with the real cross-document history state.</summary>
    public virtual async Task<(bool CanBack, bool CanFwd)> GetNavStateAsync(CancellationToken ct)
    {
        try
        {
            var el = await EvaluateAsync(
                "JSON.stringify(typeof navigation==='undefined'?{b:history.length>1,f:false}:{b:navigation.canGoBack,f:navigation.canGoForward})",
                false, ct);
            if (el?.ValueKind == JsonValueKind.String)
            {
                using var d = JsonDocument.Parse(el.Value.GetString()!);
                return (d.RootElement.GetProperty("b").GetBoolean(),
                        d.RootElement.GetProperty("f").GetBoolean());
            }
        }
        catch { }
        return (false, false);
    }

    /// <summary>Poll document.readyState until interactive/complete or ~15s.</summary>
    protected async Task WaitForLoadAsync(CancellationToken ct, int timeoutMs = 15000)
    {
        if (!SupportsEval) { await Task.Delay(400, ct); return; }
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        // Let the navigation commit first.
        await Task.Delay(120, ct);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var rs = await EvaluateAsync("document.readyState", false, ct);
                if (rs?.ValueKind == JsonValueKind.String &&
                    rs.Value.GetString() is "complete" or "interactive")
                {
                    // Small settle delay so SPA hydration/late scripts run.
                    await Task.Delay(150, ct);
                    RaiseLoadCompleted();
                    return;
                }
            }
            catch { /* navigating — context may be gone */ }
            await Task.Delay(200, ct);
        }
        RaiseLoadCompleted(); // timeout — still signal so helpers get injected
    }

    protected async Task<string> GetUrlSafeAsync(CancellationToken ct)
    {
        try { return (await EvaluateAsync("location.href", false, ct))?.GetString() ?? Tab.LastUrl; }
        catch { return Tab.LastUrl; }
    }

    protected async Task<string?> GetTitleSafeAsync(CancellationToken ct)
    {
        try { return (await EvaluateAsync("document.title", false, ct))?.GetString(); }
        catch { return null; }
    }

    protected async Task EvalVoidAsync(string js, CancellationToken ct)
        => _ = await EvaluateAsync(js, false, ct);

    // ---------------- screenshots ----------------

    public virtual Task<byte[]> ScreenshotAsync(RectD? clip, bool fullPage, CancellationToken ct)
        => throw new NotSupportedException($"Screenshots are not supported by the {Name} driver");

    // ---------------- input (JS-dispatch fallback) ----------------

    public virtual async Task ClickAtAsync(double x, double y, MouseButton button, int clickCount, CancellationToken ct)
    {
        await EnsureLibAsync(ct);
        await EvalVoidAsync(
            $"window.__brain.clickAt({Json(x)},{Json(y)},'{button.ToString().ToLower()}',{clickCount})", ct);
    }

    public virtual async Task MouseMoveAsync(double x, double y, CancellationToken ct)
    {
        await EnsureLibAsync(ct);
        await EvalVoidAsync($"window.__brain.hover({Json(x)},{Json(y)})", ct);
    }

    public virtual async Task MouseWheelAsync(double deltaX, double deltaY, CancellationToken ct)
    {
        await EnsureLibAsync(ct);
        await EvalVoidAsync($"window.__brain.wheel({Json(deltaX)},{Json(deltaY)})", ct);
    }

    public virtual async Task KeyPressAsync(string key, KeyModifiers modifiers, CancellationToken ct)
    {
        await EnsureLibAsync(ct);
        await EvalVoidAsync($"window.__brain.pressKey({Json(key)},{Json(modifiers.ToCdpMask())})", ct);
    }

    public virtual async Task InsertTextAsync(string text, CancellationToken ct)
    {
        await EnsureLibAsync(ct);
        await EvalVoidAsync($"window.__brain.insertText({Json(text)})", ct);
    }

    // ---------------- cookies (document.cookie fallback) ----------------

    public virtual async Task<IReadOnlyList<CookieInfo>> GetCookiesAsync(CancellationToken ct)
    {
        await EnsureLibAsync(ct);
        var el = await EvaluateAsync("window.__brain.getCookies()", false, ct);
        if (el is null || el.Value.ValueKind != JsonValueKind.Array) return Array.Empty<CookieInfo>();
        var list = new List<CookieInfo>();
        foreach (var c in el.Value.EnumerateArray())
        {
            list.Add(new CookieInfo(
                c.GetProperty("name").GetString() ?? "",
                c.GetProperty("value").GetString() ?? "",
                null, null, null, false, false));
        }
        return list;
    }

    public virtual async Task<bool> SetCookieAsync(CookieInfo cookie, CancellationToken ct)
    {
        await EnsureLibAsync(ct);
        var el = await EvaluateAsync(
            $"window.__brain.setCookie({Json(cookie.Name)},{Json(cookie.Value)},{Json(cookie.Path ?? "/")},{Json(cookie.Domain)})",
            false, ct);
        return el?.ValueKind == JsonValueKind.True;
    }

    public virtual async Task<bool> DeleteCookiesAsync(string? name, string? url, CancellationToken ct)
    {
        await EnsureLibAsync(ct);
        var el = await EvaluateAsync($"window.__brain.deleteCookie({Json(name)})", false, ct);
        return el?.ValueKind == JsonValueKind.True;
    }

    /// <summary>No origin-scoped wipe on this transport — CDP overrides it.</summary>
    public virtual Task ClearSiteDataAsync(string origin, CancellationToken ct) => Task.CompletedTask;

    public virtual Task SetBootStateAsync(string stateJson, CancellationToken ct) => Task.CompletedTask;

    // ---------------- viewport ----------------

    public virtual Task SetViewportAsync(int width, int height, double deviceScaleFactor, bool mobile, CancellationToken ct)
    {
        Controller.SetWindowSize(Tab, width, height);
        return Task.CompletedTask;
    }

    // ---------------- helpers ----------------

    protected Task EnsureLibAsync(CancellationToken ct) => Controller.EnsurePageHelpersAsync(Tab);

    internal static string Json(string? s) => s is null ? "null" : JsonSerializer.Serialize(s);
    internal static string Json(double d) => d.ToString(System.Globalization.CultureInfo.InvariantCulture);
    internal static string Json(int d) => d.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
