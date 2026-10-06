using System.Text.Json;

namespace BrAIn.Browser;

/// <summary>
/// Platform-specific automation transport for a single browser view (tab).
/// Windows: backed by the Chrome DevTools Protocol over WebView2's
/// --remote-debugging-port. macOS: backed by WKWebView evaluateJavaScript
/// through Objective-C interop, plus the window.external.sendMessage channel.
/// </summary>
public interface IBrowserDriver : IAsyncDisposable
{
    string Name { get; }
    bool SupportsEval { get; }
    bool SupportsScreenshot { get; }
    bool SupportsRealInput { get; }
    bool SupportsConsoleLog { get; }
    bool SupportsNetworkLog { get; }
    bool SupportsCookies { get; }
    bool SupportsViewportEmulation { get; }

    /// <summary>Fires when the page produces a console message or JS exception.</summary>
    event EventHandler<ConsoleEntry>? ConsoleMessage;

    /// <summary>Fires on network request lifecycle events (when supported).</summary>
    event EventHandler<NetworkEntry>? NetworkEvent;

    /// <summary>Fires when a top-level navigation starts loading.</summary>
    event EventHandler? LoadStarted;

    /// <summary>Fires when a top-level navigation/load completes.</summary>
    event EventHandler? LoadCompleted;

    /// <summary>Attach to the tab's webview and wire up events/injection.</summary>
    Task InitializeAsync(BrowserController controller, CancellationToken ct);

    /// <summary>
    /// Evaluate a JS expression in the page's main world. The expression's value
    /// (awaited when awaitPromise is true) is JSON-serialized back.
    /// </summary>
    Task<JsonElement?> EvaluateAsync(string expression, bool awaitPromise, CancellationToken ct);

    /// <summary>Whether the page can go back/forward in its navigation history.</summary>
    Task<(bool CanBack, bool CanFwd)> GetNavStateAsync(CancellationToken ct);

    Task<NavigateResult> NavigateAsync(string url, CancellationToken ct);
    Task<NavigateResult> GoBackAsync(CancellationToken ct);
    Task<NavigateResult> GoForwardAsync(CancellationToken ct);
    Task<NavigateResult> ReloadAsync(bool ignoreCache, CancellationToken ct);

    /// <summary>Returns PNG bytes.</summary>
    Task<byte[]> ScreenshotAsync(RectD? clip, bool fullPage, CancellationToken ct);

    Task ClickAtAsync(double x, double y, MouseButton button, int clickCount, CancellationToken ct);
    Task MouseMoveAsync(double x, double y, CancellationToken ct);
    Task MouseWheelAsync(double deltaX, double deltaY, CancellationToken ct);
    Task KeyPressAsync(string key, KeyModifiers modifiers, CancellationToken ct);
    Task InsertTextAsync(string text, CancellationToken ct);

    Task<IReadOnlyList<CookieInfo>> GetCookiesAsync(CancellationToken ct);
    Task<bool> SetCookieAsync(CookieInfo cookie, CancellationToken ct);
    Task<bool> DeleteCookiesAsync(string? name, string? url, CancellationToken ct);

    Task SetViewportAsync(int width, int height, double deviceScaleFactor, bool mobile, CancellationToken ct);
}
