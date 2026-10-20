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

    /// <summary>
    /// Evaluate a JS program (statement lists allowed; completion value of the
    /// last statement is returned). The default maps to EvaluateAsync, which is
    /// correct for drivers whose eval already takes a program (CDP
    /// Runtime.evaluate). WKWebView needs a different envelope, so WebKitDriver
    /// overrides it.
    /// </summary>
    Task<JsonElement?> EvalScriptAsync(string script, bool awaitPromise, CancellationToken ct)
        => EvaluateAsync(script, awaitPromise, ct);

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

    /// <summary>Write imported cookies into the webview's shared cookie
    /// store. Returns the number written, or -1 where the platform exposes
    /// no cookie-store write API (import reports "not supported").</summary>
    Task<int> ImportCookiesAsync(IReadOnlyList<Import.ImportedCookie> cookies, CancellationToken ct);

    /// <summary>Wipe all stored site data (cookies, localStorage, caches) for
    /// an origin — used when a private tab closes. Best-effort no-op where
    /// the platform can't reach it.</summary>
    Task ClearSiteDataAsync(string origin, CancellationToken ct);

    /// <summary>Bake the current chrome state into the NEXT document via
    /// window.__brainBoot (page-created script). Lets the toolbar render its
    /// final state on first paint instead of popping in after the state
    /// round-trip — kills the new-tab flicker. No-op where unsupported.</summary>
    Task SetBootStateAsync(string stateJson, CancellationToken ct);

    /// <summary>Pull buffered network entries into <see cref="DriverBase.NetworkEvent"/>.
    /// CDP drivers stream events live (no-op); WebKit polls resource timing.</summary>
    Task PullNetworkAsync(CancellationToken ct);

    /// <summary>Mute/unmute everything the current document plays. Native on
    /// WKWebView; elsewhere the injected toolbar enforces it in-page.</summary>
    void SetPageMuted(bool muted);

    Task SetViewportAsync(int width, int height, double deviceScaleFactor, bool mobile, CancellationToken ct);

    /// <summary>Apply user-agent / colour-scheme / offline / geolocation overrides to this tab.
    /// Returns a one-line report of what was applied and what this engine cannot do.</summary>
    Task<string> EmulateAsync(Emulation e, CancellationToken ct);
}
