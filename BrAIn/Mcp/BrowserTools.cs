using System.ComponentModel;
using System.Text.Json;
using BrAIn.Browser;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace BrAIn.Mcp;

/// <summary>
/// MCP tools exposing full control of the BrAIn browser.
/// Element-addressing selectors accept:
///   - "e5" / "ref:e5"  — element ref from browser_snapshot / browser_find
///   - "css:..." or any CSS selector
///   - "text:..."       — first visible element containing the text
///   - "role:button:Submit" — ARIA role + optional accessible name
///   - "xpath://..."    — XPath
/// </summary>
[McpServerToolType]
public sealed class BrowserTools(BrowserController browser)
{
    private const string SelHelp =
        "Element selector: element ref like 'e5' (from snapshot/find), CSS selector ('css:#id' or bare '#id'), 'text:Sign in', 'role:button:Submit', or 'xpath://button'.";

    // ================================================================ NAV

    [McpServerTool(Name = "browser_navigate"), Description("Navigate the active browser tab to a URL or search query.")]
    public async Task<string> Navigate(
        [Description("URL or search query")] string url,
        [Description("Tab id/prefix (default: active tab)")] string? tab = null,
        CancellationToken ct = default)
    {
        var t = ResolveTab(tab);
        var r = await browser.NavigateAsync(url, t, ct);
        return r.Ok ? $"Navigated to {r.Url}\nTitle: {r.Title}" : $"Navigation failed: {r.Error}";
    }

    [McpServerTool(Name = "browser_back"), Description("Go back in history.")]
    public async Task<string> Back([Description("Tab id/prefix")] string? tab = null, CancellationToken ct = default)
    {
        var r = await browser.GoBackAsync(ResolveTab(tab), ct);
        return r.Ok ? $"Back to {r.Url}\nTitle: {r.Title}" : $"Cannot go back: {r.Error}";
    }

    [McpServerTool(Name = "browser_forward"), Description("Go forward in history.")]
    public async Task<string> Forward([Description("Tab id/prefix")] string? tab = null, CancellationToken ct = default)
    {
        var r = await browser.GoForwardAsync(ResolveTab(tab), ct);
        return r.Ok ? $"Forward to {r.Url}\nTitle: {r.Title}" : $"Cannot go forward: {r.Error}";
    }

    [McpServerTool(Name = "browser_reload"), Description("Reload the page.")]
    public async Task<string> Reload(
        [Description("Bypass cache (hard reload)")] bool ignoreCache = false,
        [Description("Tab id/prefix")] string? tab = null,
        CancellationToken ct = default)
    {
        var r = await browser.ReloadAsync(ignoreCache, ResolveTab(tab), ct);
        return r.Ok ? $"Reloaded {r.Url}\nTitle: {r.Title}" : $"Reload failed: {r.Error}";
    }

    [McpServerTool(Name = "browser_state"), Description("Current URL, title, readyState, viewport and scroll position.")]
    public async Task<string> State([Description("Tab id/prefix")] string? tab = null, CancellationToken ct = default)
    {
        var t = ResolveTab(tab);
        var el = await browser.EvaluateAsync("window.__brain ? window.__brain.info() : ({u:location.href,t:document.title,r:document.readyState})", false, t, ct);
        var (w, h) = browser.GetWindowSize(t);
        var (cb, cf) = browser.NavState;
        return $"URL: {t.LastUrl}\nTitle: {t.LastTitle}\nWindow: {w}x{h}\n" +
               $"Loading: {(browser.IsLoading ? "yes" : "no")}  Back: {(cb ? "yes" : "no")}  Forward: {(cf ? "yes" : "no")}\n" +
               $"Page: {BrowserController.JsonToText(el)}";
    }

    // ============================================================== CONTENT

    [McpServerTool(Name = "browser_snapshot"), Description("Accessibility-tree snapshot of the page: interactive elements get refs (e1, e2, ...) usable as selectors in other tools. Call this after navigation before interacting.")]
    public async Task<string> Snapshot(
        [Description("Max nodes to include")] int maxNodes = 500,
        [Description("Tab id/prefix")] string? tab = null,
        CancellationToken ct = default)
    {
        var t = ResolveTab(tab);
        await EnsureLibAsync(t, ct);
        var el = await browser.EvaluateAsync($"window.__brain.snapshot({{}})", false, t, ct);
        if (el is null) return "No snapshot (page empty?)";
        var url = el.Value.TryGetProperty("url", out var u) ? u.GetString() : t.LastUrl;
        var title = el.Value.TryGetProperty("title", out var ti) ? ti.GetString() : "";
        var count = el.Value.TryGetProperty("nodeCount", out var nc) ? nc.GetInt32() : 0;
        var trunc = el.Value.TryGetProperty("truncated", out var tr) && tr.GetBoolean();
        var tree = el.Value.TryGetProperty("tree", out var tr2) ? tr2.GetString() : "";
        return $"Page: {title} <{url}> — {count} nodes{(trunc ? " (truncated)" : "")}\n{tree}";
    }

    [McpServerTool(Name = "browser_text"), Description("Visible innerText of the page (or of a selector).")]
    public async Task<string> Text(
        [Description(SelHelp)] string? selector = null,
        [Description("Max characters")] int maxLength = 20000,
        [Description("Tab id/prefix")] string? tab = null,
        CancellationToken ct = default)
    {
        var t = ResolveTab(tab);
        await EnsureLibAsync(t, ct);
        var r = await browser.EvaluateTextAsync(
            $"window.__brain.text({Js(selector)})", t, ct);
        return Truncate(r ?? "(no text)", maxLength);
    }

    [McpServerTool(Name = "browser_markdown"), Description(
        "The page (or one element, or what the user has selected) as clean Markdown: headings, links, lists, tables, code blocks and images. " +
        "Scripts, hidden content, navigation, footers and cookie banners are dropped, so it is far more compact than browser_html and keeps " +
        "the links that browser_text loses. Best for reading an article, docs page or search results.")]
    public async Task<string> Markdown(
        [Description(SelHelp + " Omit for the page's main content.")] string? selector = null,
        [Description("Convert only the text the user has selected on the page")] bool selection = false,
        [Description("Keep navigation, header, footer and banners too")] bool full = false,
        [Description("Include images as ![alt](url)")] bool images = true,
        [Description("Max characters")] int maxLength = 40000,
        [Description("Tab id/prefix")] string? tab = null,
        CancellationToken ct = default)
    {
        var t = ResolveTab(tab);
        await EnsureLibAsync(t, ct);
        var opts = $"{{selection:{(selection ? "true" : "false")},full:{(full ? "true" : "false")},images:{(images ? "true" : "false")},meta:true,max:{Math.Clamp(maxLength, 1000, 400000)}}}";
        var r = await browser.EvaluateTextAsync(
            $"window.__brain.markdown ? window.__brain.markdown({Js(selector)},{opts}) : '(this page loaded an older BrAIn script - reload it and try again)'", t, ct);
        return Truncate(r ?? "(no content)", maxLength);
    }

    [McpServerTool(Name = "browser_html"), Description("Outer HTML of the page or a specific element.")]
    public async Task<string> Html(
        [Description(SelHelp)] string? selector = null,
        [Description("Max characters")] int maxLength = 50000,
        [Description("Tab id/prefix")] string? tab = null,
        CancellationToken ct = default)
    {
        var t = ResolveTab(tab);
        await EnsureLibAsync(t, ct);
        var r = await browser.EvaluateTextAsync(
            $"window.__brain.html({Js(selector)})", t, ct);
        return Truncate(r ?? "(empty)", maxLength);
    }

    [McpServerTool(Name = "browser_find"), Description("Find visible elements matching a CSS selector; returns up to 50 with refs.")]
    public async Task<string> Find(
        [Description("CSS selector")] string selector,
        [Description("Tab id/prefix")] string? tab = null,
        CancellationToken ct = default)
    {
        var t = ResolveTab(tab);
        await EnsureLibAsync(t, ct);
        var r = await browser.EvaluateTextAsync($"window.__brain.queryAll({Js(selector)})", t, ct);
        return r ?? "[]";
    }

    [McpServerTool(Name = "browser_describe"), Description("Details about a single element: role, name, text, bounds.")]
    public async Task<string> Describe(
        [Description(SelHelp)] string element,
        [Description("Tab id/prefix")] string? tab = null,
        CancellationToken ct = default)
    {
        var t = ResolveTab(tab);
        await EnsureLibAsync(t, ct);
        var r = await browser.EvaluateTextAsync($"window.__brain.describe({Js(element)})", t, ct);
        return r ?? "element not found";
    }

    [McpServerTool(Name = "browser_listeners"), Description(
        "Event listeners bound to an element — including delegated handlers on its ancestors, document and window — " +
        "with each listener's event type, capture/once/passive flags, function source and the exact call site that registered it. " +
        "Use it to answer \"why does clicking this do nothing?\" or \"what runs on keydown?\". " +
        "Omit 'element' for a page-wide summary (counts by event type and the busiest targets). " +
        "Registrations are recorded from document start, so everything the page bound is covered (main frame).")]
    public async Task<string> Listeners(
        [Description(SelHelp + " 'window' and 'document' are also accepted. Omit for a page-wide summary.")] string? element = null,
        [Description("Include listeners on ancestors, document and window (delegated handlers). Default true.")] bool ancestors = true,
        [Description("Only these event types, comma-separated, e.g. 'click,keydown'")] string? type = null,
        [Description("Also include recently removed listeners (with who removed them)")] bool includeRemoved = false,
        [Description("Max listeners (or targets in the summary)")] int max = 100,
        [Description("Tab id/prefix")] string? tab = null,
        CancellationToken ct = default)
    {
        var t = ResolveTab(tab);
        await EnsureLibAsync(t, ct);
        var opts = $"{{ancestors:{(ancestors ? "true" : "false")},removed:{(includeRemoved ? "true" : "false")}," +
                   $"type:{Js(type)},max:{Math.Clamp(max, 1, 1000)}}}";
        var r = await browser.EvaluateTextAsync($"window.__brain.listenersText({Js(element)},{opts})", t, ct);
        return r ?? "(no data)";
    }

    [McpServerTool(Name = "browser_rewind"), Description(
        "Look back at what this tab's page used to show. BrAIn's Rewind keeps compressed snapshots of pages " +
        "(opt-in by the user; off by default; never private tabs). action=list shows the timeline (agent steps are tagged); " +
        "action=read returns the page's text at a moment (id from list, or ago like '5m' / '90s'); " +
        "action=search finds which snapshots contained some text (e.g. an old price). " +
        "Read-only: you cannot turn Rewind on, and the user must separately allow agents to read the history — " +
        "if not, the tool says so; do not try to work around it.")]
    public async Task<string> Rewind(
        [Description("list | read | search")] string action = "list",
        [Description("Snapshot id from action=list (for read)")] string? id = null,
        [Description("For read: the snapshot at/before this long ago, e.g. '5m', '90s', '1h'. Default: latest")] string? ago = null,
        [Description("For search: text to look for")] string? query = null,
        [Description("Max entries (list/search)")] int max = 30,
        [Description("Max characters of page text (read)")] int maxChars = 20000,
        [Description("Tab id/prefix")] string? tab = null,
        CancellationToken ct = default)
    {
        var t = ResolveTab(tab);
        await Task.CompletedTask;
        return (action ?? "list").Trim().ToLowerInvariant() switch
        {
            "list" => browser.RewindToolList(t, max),
            "read" => browser.RewindToolRead(t, id, ago, maxChars),
            "search" => browser.RewindToolSearch(t, query ?? "", max),
            _ => "Unknown action. Use list, read or search.",
        };
    }

    // ========================================================== INTERACTION

    [McpServerTool(Name = "browser_click"), Description("Click an element (scrolls it into view first). Uses real OS-level input where the driver supports it, so clicks count as user activation (passkeys, popups).")]
    public async Task<string> Click(
        [Description(SelHelp)] string element,
        [Description("Tab id/prefix")] string? tab = null,
        CancellationToken ct = default)
    {
        var t = ResolveTab(tab);
        await EnsureLibAsync(t, ct);
        // Real OS-level input when the driver has it (macOS CGEvent, WebView2 input):
        // trusted clicks carry user activation, so gesture-gated flows — passkey
        // prompts, payment sheets, popup-openers — work like a human click.
        var driver = await browser.DriverOrThrowAsync(t, ct);
        if (driver.SupportsRealInput)
        {
            var r2 = await browser.EvaluateAsync($"window.__brain.rect({Js(element)})", false, t, ct);
            if (r2 is { ValueKind: JsonValueKind.Object } o
                && o.TryGetProperty("w", out var w) && w.GetDouble() > 0
                && o.TryGetProperty("h", out var h) && h.GetDouble() > 0)
            {
                var cx = o.GetProperty("x").GetDouble() + w.GetDouble() / 2;
                var cy = o.GetProperty("y").GetDouble() + h.GetDouble() / 2;
                await driver.ClickAtAsync(cx, cy, MouseButton.Left, 1, ct);
                return $"click {element}: ok (real input)";
            }
        }
        var r = await browser.EvaluateTextAsync($"window.__brain.click({Js(element)})", t, ct);
        return $"click {element}: {r}";
    }

    [McpServerTool(Name = "browser_click_at"), Description("Click at viewport coordinates (x,y). On Windows uses real OS-level input events.")]
    public async Task<string> ClickAt(
        [Description("X coordinate (viewport px)")] double x,
        [Description("Y coordinate (viewport px)")] double y,
        [Description("left|right|middle")] string button = "left",
        [Description("Click count (2 = double-click)")] int clicks = 1,
        [Description("Tab id/prefix")] string? tab = null,
        CancellationToken ct = default)
    {
        var t = ResolveTab(tab);
        var btn = button.ToLowerInvariant() switch
        {
            "right" => MouseButton.Right,
            "middle" => MouseButton.Middle,
            _ => MouseButton.Left,
        };
        var driver = await browser.DriverOrThrowAsync(t, ct);
        await driver.ClickAtAsync(x, y, btn, Math.Clamp(clicks, 1, 3), ct);
        return $"clicked at ({x},{y}) {button} x{clicks}";
    }

    [McpServerTool(Name = "browser_double_click"), Description("Double-click an element.")]
    public async Task<string> DoubleClick(
        [Description(SelHelp)] string element,
        [Description("Tab id/prefix")] string? tab = null,
        CancellationToken ct = default)
    {
        var t = ResolveTab(tab);
        await EnsureLibAsync(t, ct);
        var r = await browser.EvaluateTextAsync($"window.__brain.dblclick({Js(element)})", t, ct);
        return $"dblclick {element}: {r}";
    }

    [McpServerTool(Name = "browser_hover"), Description("Hover over an element.")]
    public async Task<string> Hover(
        [Description(SelHelp)] string element,
        [Description("Tab id/prefix")] string? tab = null,
        CancellationToken ct = default)
    {
        var t = ResolveTab(tab);
        await EnsureLibAsync(t, ct);
        var r = await browser.EvaluateTextAsync($"window.__brain.hover({Js(element)})", t, ct);
        return $"hover {element}: {r}";
    }

    [McpServerTool(Name = "browser_type"), Description("Focus an input/textarea/editable and set its value (fires input+change events; framework-safe).")]
    public async Task<string> Type(
        [Description(SelHelp)] string element,
        [Description("Text to enter")] string text,
        [Description("Tab id/prefix")] string? tab = null,
        CancellationToken ct = default)
    {
        var t = ResolveTab(tab);
        await EnsureLibAsync(t, ct);
        var r = await browser.EvaluateTextAsync($"window.__brain.fill({Js(element)},{Js(text)})", t, ct);
        return $"type into {element}: {r}";
    }

    [McpServerTool(Name = "browser_type_text"), Description("Insert text into the currently focused element (keystroke-like).")]
    public async Task<string> TypeText(
        [Description("Text to type")] string text,
        [Description("Tab id/prefix")] string? tab = null,
        CancellationToken ct = default)
    {
        var t = ResolveTab(tab);
        var driver = await browser.DriverOrThrowAsync(t, ct);
        await driver.InsertTextAsync(text, ct);
        return "text inserted";
    }

    [McpServerTool(Name = "browser_press_key"), Description("Press a key: Enter, Tab, Escape, Backspace, Delete, ArrowUp/Down/Left/Right, Home, End, PageUp/Down, Space, F1-F12, or a single character.")]
    public async Task<string> PressKey(
        [Description("Key name")] string key,
        [Description("Modifiers: 'ctrl', 'ctrl+shift', 'alt', 'meta/cmd'")] string? modifiers = null,
        [Description("Tab id/prefix")] string? tab = null,
        CancellationToken ct = default)
    {
        var t = ResolveTab(tab);
        var driver = await browser.DriverOrThrowAsync(t, ct);
        var mods = KeyModifiers.Parse(modifiers);
        // Single printable chars behave better as text insertion.
        if (key.Length == 1 && mods == KeyModifiers.None)
            await driver.InsertTextAsync(key, ct);
        else
            await driver.KeyPressAsync(key, mods, ct);
        return $"pressed {modifiers}{(modifiers is null ? "" : "+")}{key}";
    }

    [McpServerTool(Name = "browser_select_option"), Description("Select <option>(s) in a <select> by value, label or text.")]
    public async Task<string> SelectOption(
        [Description(SelHelp)] string element,
        [Description("Values/labels to select")] string[] values,
        [Description("Tab id/prefix")] string? tab = null,
        CancellationToken ct = default)
    {
        var t = ResolveTab(tab);
        await EnsureLibAsync(t, ct);
        var vals = JsonSerializer.Serialize(values);
        var r = await browser.EvaluateTextAsync($"window.__brain.selectOption({Js(element)},{vals})", t, ct);
        return $"select {element}: {r}";
    }

    [McpServerTool(Name = "browser_set_checked"), Description("Check/uncheck a checkbox or radio input.")]
    public async Task<string> SetChecked(
        [Description(SelHelp)] string element,
        [Description("true = checked")] bool check,
        [Description("Tab id/prefix")] string? tab = null,
        CancellationToken ct = default)
    {
        var t = ResolveTab(tab);
        await EnsureLibAsync(t, ct);
        var r = await browser.EvaluateTextAsync(
            $"window.__brain.setChecked({Js(element)},{(check ? "true" : "false")})", t, ct);
        return $"setChecked {element}: {r}";
    }

    [McpServerTool(Name = "browser_scroll"), Description("Scroll the page: direction up/down/left/right/top/bottom.")]
    public async Task<string> Scroll(
        [Description("up|down|left|right|top|bottom")] string direction = "down",
        [Description("Pixels to scroll (default 600)")] int amount = 600,
        [Description("Tab id/prefix")] string? tab = null,
        CancellationToken ct = default)
    {
        var t = ResolveTab(tab);
        await EnsureLibAsync(t, ct);
        var r = await browser.EvaluateTextAsync(
            $"window.__brain.scrollPage({Js(direction)},{amount})", t, ct);
        return $"scroll {direction}: {r}";
    }

    [McpServerTool(Name = "browser_scroll_to"), Description("Scroll an element into view.")]
    public async Task<string> ScrollTo(
        [Description(SelHelp)] string element,
        [Description("Tab id/prefix")] string? tab = null,
        CancellationToken ct = default)
    {
        var t = ResolveTab(tab);
        await EnsureLibAsync(t, ct);
        var r = await browser.EvaluateTextAsync($"window.__brain.scrollTo({Js(element)})", t, ct);
        return $"scrollTo {element}: {r}";
    }

    // ================================================================ WAIT

    [McpServerTool(Name = "browser_wait_for"), Description("Wait until a condition holds: an element exists, text appears, URL matches, or load completes.")]
    public async Task<string> WaitFor(
        [Description("Wait for element matching selector")] string? element = null,
        [Description("Wait for page text containing this")] string? text = null,
        [Description("Wait for URL matching this regex/substring")] string? url = null,
        [Description("Wait for load state: interactive|complete")] string? readyState = null,
        [Description("Timeout ms (default 15000)")] int timeoutMs = 15000,
        [Description("Tab id/prefix")] string? tab = null,
        CancellationToken ct = default)
    {
        var t = ResolveTab(tab);
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(Math.Max(timeoutMs, 500));
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await EnsureLibAsync(t, ct);
                var checks = new List<string>();
                if (element is not null) checks.Add($"window.__brain.exists({Js(element)})");
                if (text is not null) checks.Add($"window.__brain.textIncludes({Js(text)})");
                if (url is not null) checks.Add($"window.__brain.urlMatches({Js(url)})");
                if (readyState is not null) checks.Add($"document.readyState==={Js(readyState)}");
                if (checks.Count == 0) checks.Add("document.readyState==='complete'");
                var expr = checks.Count == 1 ? checks[0] : "(" + string.Join("&&", checks) + ")";
                var el = await browser.EvaluateAsync(expr, false, t, ct);
                if (el?.ValueKind == JsonValueKind.True)
                    return $"condition met (after {timeoutMs - (int)(deadline - DateTime.UtcNow).TotalMilliseconds}ms): {expr}";
            }
            catch { /* context lost during navigation — keep polling */ }
            await Task.Delay(200, ct);
        }
        return $"timeout after {timeoutMs}ms waiting for: element={element} text={text} url={url} readyState={readyState}";
    }

    // ============================================================= CAPTURE

    [McpServerTool(Name = "browser_screenshot"), Description("Screenshot of the page (PNG). Optionally clip to an element or capture beyond the viewport.")]
    public async Task<CallToolResult> Screenshot(
        [Description(SelHelp)] string? element = null,
        [Description("Capture the whole scrolling page, not just the viewport (scrolls and stitches)")] bool fullPage = false,
        [Description("Tab id/prefix")] string? tab = null,
        CancellationToken ct = default)
    {
        var t = ResolveTab(tab);
        RectD? clip = null;
        if (element is not null)
        {
            await EnsureLibAsync(t, ct);
            var r = await browser.EvaluateAsync($"window.__brain.rect({Js(element)})", false, t, ct);
            if (r is { ValueKind: JsonValueKind.Object })
            {
                clip = new RectD(
                    r.Value.GetProperty("x").GetDouble(),
                    r.Value.GetProperty("y").GetDouble(),
                    Math.Max(1, r.Value.GetProperty("w").GetDouble()),
                    Math.Max(1, r.Value.GetProperty("h").GetDouble()));
            }
            else
            {
                return Err($"element not found for screenshot clip: {element}");
            }
        }

        var png = await browser.ScreenshotAsync(clip, fullPage, t, ct);
        return new CallToolResult
        {
            Content =
            {
                ImageContentBlock.FromBytes(png, "image/png"),
                new TextContentBlock { Text = $"screenshot {png.Length / 1024}KB png" },
            },
        };
    }

    [McpServerTool(Name = "browser_evaluate"), Description("Evaluate a JavaScript expression in the page and return the JSON-serialized result. Awaited automatically if it returns a Promise.")]
    public async Task<string> Evaluate(
        [Description("JS expression or statements (eval'd)")] string script,
        [Description("Await promise results")] bool awaitPromise = true,
        [Description("Tab id/prefix")] string? tab = null,
        CancellationToken ct = default)
    {
        var t = ResolveTab(tab);
        var r = await browser.EvalScriptAsync(script, awaitPromise, t, ct);
        return BrowserController.JsonToText(r);
    }

    // ============================================================== STATE

    [McpServerTool(Name = "browser_console_log"), Description("Recent console messages and page JS errors.")]
    public async Task<string> ConsoleLog(
        [Description("Filter by level: error|warn|info|log|debug")] string? level = null,
        [Description("Max entries")] int last = 50,
        [Description("Clear the buffer after reading")] bool clear = false,
        CancellationToken ct = default)
    {
        var entries = browser.ConsoleLog;
        if (level is not null)
            entries = entries.Where(e => e.Level.Equals(level, StringComparison.OrdinalIgnoreCase)).ToList();
        var take = entries.TakeLast(Math.Clamp(last, 1, 500)).ToList();
        if (clear) browser.ClearConsoleLog();
        if (take.Count == 0) return "(no console output captured)";
        return string.Join('\n', take.Select(e =>
            $"[{e.Time:HH:mm:ss}] {e.Level}: {e.Text}{(e.Url is null ? "" : $" ({e.Url}:{e.Line})")}"));
    }

    [McpServerTool(Name = "browser_network_requests"), Description("Recent network requests (method, url, status, bytes). Windows: live CDP capture; other platforms: PerformanceResourceTiming.")]
    public async Task<string> NetworkRequests(
        [Description("Substring filter on the URL")] string? filter = null,
        [Description("Max entries")] int last = 50,
        [Description("Tab id/prefix")] string? tab = null,
        CancellationToken ct = default)
    {
        var t = ResolveTab(tab);
        var driver = await browser.DriverOrThrowAsync(t, ct);
        if (driver.SupportsNetworkLog && browser.NetworkLog.Count > 0)
        {
            var entries = browser.NetworkLog.AsEnumerable();
            if (filter is not null)
                entries = entries.Where(e => e.Url.Contains(filter, StringComparison.OrdinalIgnoreCase));
            var take = entries.TakeLast(Math.Clamp(last, 1, 500)).ToList();
            if (take.Count == 0) return "(no matching requests)";
            return string.Join('\n', take.Select(e =>
                $"[{e.Time:HH:mm:ss}] {e.Method} {e.Status?.ToString() ?? "-"} {e.Url[..Math.Min(e.Url.Length, 150)]}" +
                $"{(e.BytesReceived > 0 ? $" {e.BytesReceived / 1024}KB" : "")}{(e.Error is null ? "" : $" ERR:{e.Error}")}"));
        }
        // Fallback: performance entries
        await EnsureLibAsync(t, ct);
        var r = await browser.EvaluateTextAsync("window.__brain.networkLog(0)", t, ct);
        return r ?? "(no network data)";
    }

    [McpServerTool(Name = "browser_cookies"), Description("List, set or delete cookies.")]
    public async Task<string> Cookies(
        [Description("list | set | delete | clear")] string action = "list",
        [Description("Cookie name (set/delete)")] string? name = null,
        [Description("Cookie value (set)")] string? value = null,
        [Description("Domain (set)")] string? domain = null,
        [Description("Path (set, default /)")] string? path = null,
        [Description("URL scope for delete")] string? url = null,
        [Description("Tab id/prefix")] string? tab = null,
        CancellationToken ct = default)
    {
        var t = ResolveTab(tab);
        var driver = await browser.DriverOrThrowAsync(t, ct);
        switch (action.ToLowerInvariant())
        {
            case "list":
                var cookies = await driver.GetCookiesAsync(ct);
                if (cookies.Count == 0) return "(no cookies)";
                return string.Join('\n', cookies.Select(c =>
                    $"{c.Name}={c.Value[..Math.Min(c.Value.Length, 40)]} dom={c.Domain ?? "-"} path={c.Path ?? "-"}{(c.HttpOnly ? " httpOnly" : "")}{(c.Secure ? " secure" : "")}"));
            case "set":
                if (name is null || value is null) return "set requires name and value";
                var ok = await driver.SetCookieAsync(new CookieInfo(name, value, domain, path, null, false, false), ct);
                return ok ? $"cookie {name} set" : $"failed to set cookie {name}";
            case "delete":
                return await driver.DeleteCookiesAsync(name, url, ct) ? "deleted" : "delete failed";
            case "clear":
                return await driver.DeleteCookiesAsync(null, url, ct) ? "cleared" : "clear failed";
            default:
                return $"unknown action '{action}'";
        }
    }

    // ========================================================== TABS/WINDOWS

    [McpServerTool(Name = "browser_tabs"), Description("List all browser windows/tabs. The 'active' one receives tool calls by default.")]
    public Task<string> Tabs(CancellationToken ct = default)
    {
        var lines = browser.Tabs.All.Select(t =>
            $"{(ReferenceEquals(t, browser.Tabs.Active) ? "* " : "  ")}{t.Id.ToString()[..8]}" +
            $"{(t.IsPrivate ? " [private]" : "")}{(t.IsPinned ? " [pinned]" : "")}  {t.LastTitle ?? "?"}  <{t.LastUrl}>");
        return Task.FromResult("Tabs:\n" + string.Join('\n', lines));
    }

    [McpServerTool(Name = "browser_new_tab"), Description("Open a new tab. Private tabs keep no history and wipe site data on close.")]
    public async Task<string> NewTab(
        [Description("URL to open (default: new tab page)")] string? url = null,
        [Description("Open as a private tab")] bool priv = false,
        CancellationToken ct = default)
    {
        var tab = await browser.NewTabAsync(url, ct, priv);
        return $"new {(priv ? "private " : "")}tab {tab.Id.ToString()[..8]} opened → {tab.LastUrl}";
    }

    [McpServerTool(Name = "browser_ask_human"), Description("Ask the user to do a step only they can do (log in, solve a CAPTCHA, enter a 2FA code, approve a payment) and wait. BrAIn brings the tab to the front and shows the user a card; they do the step in the page and press Done. Returns DONE, CANT or NO ANSWER. After DONE take a fresh browser_snapshot and carry on. Never ask for passwords or codes in the message; the user types them into the page themselves.")]
    public async Task<string> AskHuman(
        [Description("What you need the user to do, in one or two plain sentences (shown to them as is)")] string message,
        [Description("How many minutes to wait for them (1-15, default 5)")] int minutes = 5,
        [Description("Tab id/prefix (default: active tab)")] string? tab = null,
        CancellationToken ct = default)
    {
        var t = ResolveTab(tab);
        try { await browser.ActivateWindowAsync(t.Id.ToString("N")[..8], ct); } catch { /* still ask */ }
        // Bring the app forward too; never block the question on it.
        _ = Task.Run(async () => { try { await browser.RaiseWindowAsync(t, CancellationToken.None); } catch { } });
        string? host = null;
        try { host = new Uri(t.LastUrl).Host; } catch { }
        return await browser.Handoff.AskAsync(message, host, t.LastUrl, TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 15)), ct);
    }

    [McpServerTool(Name = "browser_switch_tab"), Description("Switch the browser to a tab by id or id-prefix.")]
    public async Task<string> SwitchTab(
        [Description("Tab id or prefix from browser_tabs")] string id,
        CancellationToken ct = default)
    {
        await browser.ActivateWindowAsync(id, ct);
        return "switched";
    }

    [McpServerTool(Name = "browser_close_tab"), Description("Close a tab by id. Closing the last tab exits BrAIn.")]
    public async Task<string> CloseTab(
        [Description("Tab id or prefix")] string id,
        CancellationToken ct = default)
    {
        await browser.CloseWindowAsync(id, ct);
        return "closed";
    }

    [McpServerTool(Name = "browser_window_resize"), Description("Resize the browser window.")]
    public async Task<string> WindowResize(
        [Description("Width px")] int width,
        [Description("Height px")] int height,
        [Description("Tab id/prefix")] string? tab = null,
        CancellationToken ct = default)
    {
        var t = ResolveTab(tab);
        await Task.Run(() => browser.SetWindowSize(t, width, height), ct);
        return $"resized to {width}x{height}";
    }

    [McpServerTool(Name = "browser_window_state"), Description("Change window state: normal|minimized|maximized|fullscreen.")]
    public async Task<string> WindowState(
        [Description("normal|minimized|maximized|fullscreen")] string state,
        [Description("Tab id/prefix")] string? tab = null,
        CancellationToken ct = default)
    {
        var t = ResolveTab(tab);
        await Task.Run(() => browser.SetWindowState(t, state), ct);
        return $"window state: {state}";
    }

    [McpServerTool(Name = "browser_set_viewport"), Description("Emulate a viewport size (device metrics override on Windows; window resize elsewhere).")]
    public async Task<string> SetViewport(
        [Description("Width px")] int width,
        [Description("Height px")] int height,
        [Description("Device scale factor")] double deviceScaleFactor = 1,
        [Description("Emulate mobile")] bool mobile = false,
        [Description("Tab id/prefix")] string? tab = null,
        CancellationToken ct = default)
    {
        var t = ResolveTab(tab);
        var driver = await browser.DriverOrThrowAsync(t, ct);
        await driver.SetViewportAsync(width, height, deviceScaleFactor, mobile, ct);
        return $"viewport {width}x{height}@{deviceScaleFactor}x{(mobile ? " mobile" : "")}";
    }

    [McpServerTool(Name = "browser_zoom"), Description("Set page zoom in percent (100 = normal).")]
    public async Task<string> Zoom(
        [Description("Zoom percent")] int percent,
        [Description("Tab id/prefix")] string? tab = null,
        CancellationToken ct = default)
    {
        var t = ResolveTab(tab);
        browser.SetZoom(t, Math.Clamp(percent, 25, 500));
        return $"zoom {percent}%";
    }

    [McpServerTool(Name = "browser_pdf"), Description("Print the page to PDF (Windows only). Returns the saved file path.")]
    public async Task<string> PrintToPdf(
        [Description("Output file path")] string outputPath,
        [Description("Tab id/prefix")] string? tab = null,
        CancellationToken ct = default)
    {
        var t = ResolveTab(tab);
        var driver = await browser.DriverOrThrowAsync(t, ct);
        if (driver is not Browser.Cdp.CdpDriver cdp)
            throw new NotSupportedException("PDF printing requires the CDP driver (Windows)");
        var res = await cdp.PrintToPdfAsync(outputPath, ct);
        return res;
    }

    // ============================================================== FILES

    [McpServerTool(Name = "browser_upload"), Description("Load a local file into an <input type=file> by injecting a File via a DataTransfer — no native dialog. The bytes are read by the browser process itself, so nothing traverses the network.")]
    public async Task<string> Upload(
        [Description("Absolute path to the local file")] string path,
        [Description(SelHelp + " Defaults to the first input[type=file] (preferring one that accepts .aab).")] string? element = null,
        [Description("Tab id/prefix")] string? tab = null,
        CancellationToken ct = default)
    {
        var t = ResolveTab(tab);
        await EnsureLibAsync(t, ct);
        var bytes = await File.ReadAllBytesAsync(path, ct);
        var name = Path.GetFileName(path);
        var b64 = Convert.ToBase64String(bytes);

        await browser.EvaluateTextAsync("window.__up=''", t, ct);
        const int CH = 600_000;
        for (var i = 0; i < b64.Length; i += CH)
        {
            var part = b64.Substring(i, Math.Min(CH, b64.Length - i));
            await browser.EvaluateTextAsync($"window.__up+={Js(part)}", t, ct);
        }

        var target = element is null
            ? "(document.querySelector('input[type=file][accept*=\".aab\"]')||document.querySelector('input[type=file]'))"
            : $"window.__brain.resolve({Js(element)})";
        var js =
            "(function(){try{" +
            "var bin=atob(window.__up),n=bin.length,arr=new Uint8Array(n);" +
            "for(var i=0;i<n;i++)arr[i]=bin.charCodeAt(i);" +
            "var f=new File([arr]," + Js(name) + ",{type:'application/octet-stream'});" +
            "var dt=new DataTransfer(); dt.items.add(f);" +
            "var inp=" + target + "; if(!inp) return 'no file input found';" +
            "try{inp.files=dt.files}catch(e){}" +
            "inp.dispatchEvent(new Event('input',{bubbles:true}));" +
            "inp.dispatchEvent(new Event('change',{bubbles:true}));" +
            "return 'injected '+f.name+' ('+f.size+' bytes); input.files='+inp.files.length;" +
            "}catch(e){return 'ERR '+String(e&&e.stack||e)}})()";
        var r = await browser.EvaluateTextAsync(js, t, ct);
        return $"upload {name}: {r}";
    }

    // ============================================================== HELPERS

    private BrowserTab ResolveTab(string? idOrPrefix)
        => idOrPrefix is null
            ? browser.ActiveTabOrThrow()
            : browser.Tabs.FindByPrefix(idOrPrefix)
              ?? throw new InvalidOperationException($"No tab matching '{idOrPrefix}'");

    private async Task EnsureLibAsync(BrowserTab tab, CancellationToken ct)
    {
        await browser.DriverOrThrowAsync(tab, ct); // ensure driver exists
        await browser.EnsurePageHelpersAsync(tab);
    }

    private static CallToolResult Err(string msg) => new()
    {
        IsError = true,
        Content = { new TextContentBlock { Text = msg } },
    };

    private static string Js(string? s) => s is null ? "null" : JsonSerializer.Serialize(s);

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + $"\n… [truncated {s.Length - max} chars]";
}
