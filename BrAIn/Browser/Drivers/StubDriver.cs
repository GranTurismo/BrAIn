using System.Text.Json;

namespace BrAIn.Browser.Drivers;

/// <summary>
/// Fallback driver (Linux and any platform without a page-control channel).
/// Navigation and window chrome work; in-page automation is unavailable.
/// </summary>
public sealed class StubDriver : DriverBase
{
    public StubDriver(BrowserTab tab, BrowserController controller) : base(tab, controller) { }

    public override string Name => "stub";
    public override bool SupportsEval => false;
    public override bool SupportsCookies => false;

    public override Task<JsonElement?> EvaluateAsync(string expression, bool awaitPromise, CancellationToken ct)
        => throw new NotSupportedException(
            "In-page automation is not available on this platform. " +
            "BrAIn supports full control on Windows (CDP/WebView2) and macOS (WKWebView).");

    public override Task<NavigateResult> NavigateAsync(string url, CancellationToken ct)
    {
        Controller.InvokeOnUi(() => Tab.Window.Load(new Uri(url)));
        return Task.FromResult(NavigateResult.Success(url, null));
    }
}
