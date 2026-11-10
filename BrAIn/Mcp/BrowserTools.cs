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
        if (FileUrlRefusal(url) is { } refused) return refused;
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
        var el = await browser.EvaluateAsync($"window.__brain.snapshot({{maxNodes:{Math.Clamp(maxNodes, 10, 3000)}}})", false, t, ct);
        if (el is null) return "No snapshot (page empty?)";
        var url = el.Value.TryGetProperty("url", out var u) ? u.GetString() : t.LastUrl;
        var title = el.Value.TryGetProperty("title", out var ti) ? ti.GetString() : "";
        var count = el.Value.TryGetProperty("nodeCount", out var nc) ? nc.GetInt32() : 0;
        var trunc = el.Value.TryGetProperty("truncated", out var tr) && tr.GetBoolean();
        var tree = el.Value.TryGetProperty("tree", out var tr2) ? tr2.GetString() : "";
        return $"Page: {title} <{url}> — {count} nodes{(trunc ? " (truncated: more on the page; scroll, use browser_find, or raise maxNodes)" : "")}\n{tree}";
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
                // The OS click can be dropped or land elsewhere without any error (missing input permission, window
                // not frontmost, an overlay on top), so watch for it arriving and report what the page did.
                var token = (await browser.EvaluateAsync("window.__brain.clickWatch(" + Js(element) + ")", false, t, ct))?.GetString() ?? "";
                await driver.ClickAtAsync(cx, cy, MouseButton.Left, 1, ct);
                await Task.Delay(350, ct);
                JsonElement? after = null;
                try { after = await browser.EvaluateAsync("window.__brain.clickResult()", false, t, ct); }
                catch (OperationCanceledException) { throw; }
                catch { /* the document went away: handled below */ }
                var delivered = after is { ValueKind: JsonValueKind.Object } a && a.TryGetProperty("hit", out _);
                if (!delivered)
                {
                    // A click that navigates destroys the document that was watching it. That is a success; clicking
                    // again by ref would hit some other element of the new page.
                    var sameDoc = false;
                    try { sameDoc = (await browser.EvaluateAsync($"window.__brainClickDoc==={Js(token)}", false, t, ct))?.ValueKind == JsonValueKind.True; }
                    catch (OperationCanceledException) { throw; }
                    catch { }
                    if (!sameDoc) return $"click {element}: ok (real input), the page navigated or reloaded. Take a new snapshot.";
                    var syn = await browser.EvaluateTextAsync($"window.__brain.click({Js(element)})", t, ct);
                    return $"click {element}: the OS click never reached the page, so a synthetic click was used instead: {syn}";
                }
                var res = after!.Value;
                var landedOnTarget = res.GetProperty("hit").GetBoolean();
                var changed = res.TryGetProperty("changed", out var ch) && ch.GetBoolean();
                var note = changed ? "the page changed" : "no visible change on the page";
                if (!landedOnTarget)
                {
                    var tag = res.TryGetProperty("tag", out var tg) ? tg.GetString() : "?";
                    var name = res.TryGetProperty("name", out var nm) ? nm.GetString() : "";
                    return $"click {element}: WARNING the click landed on <{tag}> \"{name}\" instead of the target (something may be covering it); {note}. Take a new snapshot before trying again.";
                }
                return changed ? $"click {element}: ok (real input), {note}"
                               : $"click {element}: delivered (real input) but {note}. If you expected a change, snapshot again; the element may need a different action.";
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
        // Say plainly when the field did not take the text, so the agent does not assume it worked.
        if (r.Contains("\"set\":false", StringComparison.Ordinal))
            return $"type into {element}: WARNING the text did not appear in the field. {r}";
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
        await EnsureLibAsync(t, ct);
        // Typing into nothing is silently lost; say so instead of claiming success.
        var focus = await browser.EvaluateAsync("window.__brain.focusInfo()", false, t, ct);
        if (focus is { ValueKind: JsonValueKind.Object } f && f.TryGetProperty("editable", out var ed) && ed.ValueKind == JsonValueKind.False)
            return "FAILED: no text field is focused, so nothing was typed. Click the field first, or use browser_type with the field's element ref.";
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
        var timeout = Math.Max(timeoutMs, 500);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeout);
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
                    return $"condition met (after {sw.ElapsedMilliseconds}ms)";
            }
            catch { /* context lost during navigation — keep polling */ }
            await Task.Delay(200, ct);
        }
        return $"timeout after {timeout}ms waiting for: element={element} text={text} url={url} readyState={readyState}";
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

    [McpServerTool(Name = "browser_network_requests"), Description("List the requests the page made: id, time, method, status, type, duration, size, URL. macOS/Linux: recording starts with the first call (or when the DevTools panel is open) and then captures fetch/XHR/WebSocket with headers, bodies and timing — requests made BEFORE recording started only show timing, so reload the page after the first call to capture everything. Windows: live CDP capture. Use browser_network_request for one request's headers and bodies.")]
    public async Task<string> NetworkRequests(
        [Description("Substring filter on the URL")] string? filter = null,
        [Description("Max entries")] int last = 50,
        [Description("Tab id/prefix")] string? tab = null,
        [Description("Only this type: document, fetch, xhr, script, stylesheet, image, font, media, websocket, other")] string? type = null,
        [Description("Only failed requests (status >= 400 or a network error)")] bool failedOnly = false,
        [Description("Show secrets (tokens in addresses) instead of [redacted]. The user is asked first.")] bool revealSecrets = false,
        CancellationToken ct = default)
    {
        var t = ResolveTab(tab);
        var driver = await browser.DriverOrThrowAsync(t, ct);
        var wasRecording = browser.NetworkRecording;
        if (driver is Browser.Drivers.WebKitDriver)
        {
            browser.SetAgentNetworkRecording(true);
            await browser.PullNetworkNowAsync(t, ct);
        }
        if (driver.SupportsNetworkLog && browser.NetworkFor(t).Count > 0)
        {
            var entries = browser.NetworkFor(t).AsEnumerable();
            if (filter is not null)
                entries = entries.Where(e => e.Url.Contains(filter, StringComparison.OrdinalIgnoreCase));
            if (type is not null)
                entries = entries.Where(e => string.Equals(e.ResourceType, type, StringComparison.OrdinalIgnoreCase));
            if (failedOnly)
                entries = entries.Where(e => e.Error is not null || e.Status >= 400);
            var take = entries.TakeLast(Math.Clamp(last, 1, 500)).ToList();
            if (take.Count == 0) return "(no matching requests)";
            string Show(string u) { var v = revealSecrets ? u : NetRedact.Url(u); return v[..Math.Min(v.Length, 150)]; }
            var lines = take.Select(e =>
                $"{e.RequestId} [{e.Time:HH:mm:ss}] {e.Method} {e.Status?.ToString() ?? "-"} {e.ResourceType ?? "?"}" +
                $"{(e.X is { Done: true } x ? $" {x.DurationMs:0}ms" : "")}{(e.BytesReceived > 0 ? $" {FormatBytes(e.BytesReceived)}" : "")}" +
                $" {Show(e.Url)}{(e.Error is null ? "" : $" ERR:{e.Error}")}");
            var note = !wasRecording && driver is Browser.Drivers.WebKitDriver
                ? "\n(Recording just started: requests made before this call only have timing. Reload the page, then call again for headers and bodies.)"
                : "";
            return string.Join('\n', lines) + note;
        }
        // Fallback: performance entries
        await EnsureLibAsync(t, ct);
        var r = await browser.EvaluateTextAsync("window.__brain.networkLog(0)", t, ct);
        return r ?? "(no network data)";
    }

    private static string FormatBytes(long n) => n >= 1048576 ? $"{n / 1048576.0:0.#}MB" : n >= 1024 ? $"{n / 1024.0:0.#}KB" : $"{n}B";

    [McpServerTool(Name = "browser_network_request"), Description("Everything recorded about ONE request: status, timing phases, request headers and body, response headers and (text) body, WebSocket messages. The id comes from browser_network_requests. Bodies are only captured for fetch/XHR responses and only while recording was on (see browser_network_record).")]
    public async Task<string> NetworkRequest(
        [Description("Request id (or a unique prefix of it) from browser_network_requests")] string id,
        [Description("Max characters of the response body to return")] int maxBody = 6000,
        [Description("Tab id/prefix")] string? tab = null,
        [Description("Show secrets (Authorization, cookies, tokens, passwords in bodies) instead of [redacted]. The user is asked first.")] bool revealSecrets = false,
        CancellationToken ct = default)
    {
        var t = ResolveTab(tab);
        await browser.PullNetworkNowAsync(t, ct);
        string U(string? v) => revealSecrets ? v ?? "" : NetRedact.Url(v);
        string B(string? v) => revealSecrets ? v ?? "" : NetRedact.Body(v);
        var n = browser.NetworkEntryById(id);
        if (n is null) return $"ERROR: no request '{id}'. List them with browser_network_requests.";
        var x = n.X;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{n.Method} {U(n.Url)}");
        sb.AppendLine($"Status: {(n.Error is not null ? "ERROR " + n.Error : n.Status?.ToString() ?? (x?.Done == true ? "unknown" : "pending"))}{(x?.StatusText is { Length: > 0 } st ? " " + st : "")}");
        sb.AppendLine($"Type: {n.ResourceType}{(x?.Mime is { Length: > 0 } mt ? " · " + mt : "")}{(x?.Protocol is { Length: > 0 } pr ? " · " + pr : "")}");
        sb.AppendLine($"Size: {FormatBytes(n.BytesReceived)}{(x is { DecodedBytes: > 0 } ? $" ({FormatBytes(x.DecodedBytes)} uncompressed)" : "")}");
        if (x is not null)
        {
            sb.AppendLine($"Started: {n.Time:HH:mm:ss.fff} (+{x.StartMs:0} ms after the page began) · total {(x.Done ? x.DurationMs.ToString("0.#") + " ms" : "still running")}");
            if (x.Timing is { } tm)
                sb.AppendLine($"Phases (ms): queued {tm.Queue:0.#}, dns {tm.Dns:0.#}, connect {tm.Connect:0.#} (tls {tm.Tls:0.#}), wait {tm.Wait:0.#}, download {tm.Receive:0.#}");
            if (x.Redirected) sb.AppendLine($"Redirected to: {U(x.FinalUrl)}");
        }
        void Headers(string title, Dictionary<string, string>? h)
        {
            sb.AppendLine().AppendLine(title + ":");
            if (h is null || h.Count == 0) { sb.AppendLine("  (none recorded)"); return; }
            foreach (var kv in h.OrderBy(k => k.Key)) sb.AppendLine($"  {kv.Key}: {(revealSecrets ? kv.Value : NetRedact.Header(kv.Key, kv.Value))}");
        }
        Headers("Request headers", n.RequestHeaders);
        if (n.PostData is not null) sb.AppendLine().AppendLine("Request body:").AppendLine(B(n.PostData));
        Headers("Response headers", n.ResponseHeaders);
        if (x?.ResponseBody is { } body)
        {
            var cut = body.Length > maxBody;
            sb.AppendLine().AppendLine($"Response body ({body.Length} chars{(x.BodyTruncated ? ", recorded part of " + x.BodyLength : "")}{(cut ? $", showing first {maxBody}" : "")}):");
            sb.AppendLine(B(cut ? body[..Math.Max(0, maxBody)] : body));
        }
        else if (x is { BodyDropped: true }) sb.AppendLine().AppendLine("Response body: dropped by the recorder's memory budget.");
        else if (x is not null) sb.AppendLine().AppendLine("Response body: not recorded (only text responses of fetch/XHR calls made while recording was on are).");
        if (x?.Frames is { Count: > 0 } fr)
        {
            sb.AppendLine().AppendLine($"WebSocket messages ({x.FrameCount}{(x.FrameCount > fr.Count ? ", showing " + fr.Count : "")}){(x.CloseInfo is null ? "" : ", closed " + x.CloseInfo)}:");
            foreach (var f in fr.TakeLast(60)) sb.AppendLine($"  {(f.Dir == "s" ? "↑ sent    " : "↓ received")} +{f.AtMs:0}ms {f.Size}B {B(f.Text)}");
        }
        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "browser_network_har"), Description("Export the tab's recorded requests as a HAR 1.2 file (what Chrome's 'Save all as HAR' writes: headers, bodies, timings). Saved in the Downloads folder; returns the path, or the JSON itself with inline=true.")]
    public async Task<string> NetworkHar(
        [Description("Tab id/prefix")] string? tab = null,
        [Description("Return the HAR JSON in the answer instead of (as well as) a file")] bool inline = false,
        [Description("With inline: cut the JSON at this many characters")] int maxChars = 20000,
        [Description("With inline: show secrets (tokens, cookies, passwords) instead of [redacted]. The user is asked first. (The saved file always holds everything, like Chrome's.)")] bool revealSecrets = false,
        CancellationToken ct = default)
    {
        var t = ResolveTab(tab);
        await browser.PullNetworkNowAsync(t, ct);
        var entries = browser.NetworkFor(t);
        if (entries.Count == 0) return "No recorded requests. Call browser_network_requests first (it starts recording), reload the page, then export.";
        var saved = await browser.SaveHarAsync(t);
        var msg = saved is { } s ? $"Saved {s.Count} requests to {s.Path}" : "(nothing saved)";
        if (!inline) return msg;
        var json = browser.BuildHar(entries, t.LastUrl ?? "", t.LastTitle);
        if (!revealSecrets) json = NetRedact.Har(json);
        return msg + "\n" + (json.Length > maxChars ? json[..Math.Max(0, maxChars)] + $"\n… (cut at {maxChars} of {json.Length} characters)" : json);
    }

    [McpServerTool(Name = "browser_network_record"), Description("Switch the network recorder (macOS/Linux) on or off, clear what it has, or ask its state. It is on while the DevTools panel is open and after the first browser_network_requests call. It costs memory and a little speed in every tab while on, so stop it when you are done.")]
    public async Task<string> NetworkRecord(
        [Description("start | stop | clear | status")] string action = "status",
        CancellationToken ct = default)
    {
        switch ((action ?? "status").ToLowerInvariant())
        {
            case "start": browser.SetAgentNetworkRecording(true); return "Network recording is on. Requests of pages loaded from now on are captured in full; reload the current page to include it.";
            case "stop": browser.SetAgentNetworkRecording(false); return browser.NetworkRecording ? "Agent recording stopped (DevTools is still open, so the recorder keeps running)." : "Network recording is off.";
            case "clear": browser.ClearNetworkLog(); return "Cleared.";
            default: return browser.NetworkRecording ? "Network recording is on." : "Network recording is off.";
        }
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
                // Windows (CDP) answers with every cookie of the whole profile: only this site's are the agent's business, and a login
                // session that page scripts cannot even read (HttpOnly) is not handed to an agent either.
                var host = Uri.TryCreate(t.LastUrl, UriKind.Absolute, out var tu) ? tu.Host : "";
                var cookies = (await driver.GetCookiesAsync(ct)).Where(c => BrowserController.CookieBelongsTo(c.Domain, host)).ToList();
                if (cookies.Count == 0) return "(no cookies)";
                return string.Join('\n', cookies.Select(c =>
                    $"{c.Name}={(c.HttpOnly ? "[HttpOnly: hidden]" : c.Value[..Math.Min(c.Value.Length, 40)])} dom={c.Domain ?? "-"} path={c.Path ?? "-"}{(c.HttpOnly ? " httpOnly" : "")}{(c.Secure ? " secure" : "")}"));
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
        if (FileUrlRefusal(url) is { } refused) return refused;
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
        var confined = PathGuard.InsideFolder(outputPath, browser.Downloads.Dir, out var whyNot);
        if (confined is null) return $"ERROR: {whyNot}. Give a plain file name and it is saved there.";
        if (!confined.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) confined += ".pdf";
        var res = await cdp.PrintToPdfAsync(confined, ct);
        return res;
    }

    // ============================================================== FILES

    private const long MaxUploadBytes = 5L << 30;   // 5 GiB
    private const int RawPiece = 450_000;           // divisible by 3: a piece is whole base64 (600 000 characters), padding only ever ends the last one
    private const int FoldPieces = 32;              // decoded pieces kept as plain arrays before they are folded into the Blob (about 14 MB)

    private static async Task<int> ReadFullAsync(Stream s, byte[] buf, CancellationToken ct)
    {
        var got = 0;
        while (got < buf.Length)
        {
            var n = await s.ReadAsync(buf.AsMemory(got), ct);
            if (n == 0) break;
            got += n;
        }
        return got;
    }

    private static string PieceJs(string holder, string b64) =>
        "(function(h){var s=atob(" + Js(b64) + "),n=s.length,a=new Uint8Array(n);" +
        "for(var i=0;i<n;i++)a[i]=s.charCodeAt(i);h.p.push(a);" +
        "if(h.p.length>=" + FoldPieces + "){h.b=new Blob([h.b].concat(h.p));h.p=[]}" +
        "return 'piece-ok'})(window." + holder + ")";

    [McpServerTool(Name = "browser_upload"), Description("Load a local file into an <input type=file> by injecting a File via a DataTransfer — no native dialog. The bytes are read by the browser process itself, so nothing traverses the network. Files up to 5 GB are accepted; they reach the page in pieces.")]
    public async Task<string> Upload(
        [Description("Absolute path to the local file")] string path,
        [Description(SelHelp + " Defaults to the first input[type=file] (preferring one that accepts .aab).")] string? element = null,
        [Description("Tab id/prefix")] string? tab = null,
        CancellationToken ct = default)
    {
        var t = ResolveTab(tab);
        // Credential and key files are never handed to a page by an agent (whatever the mode): see PathGuard.
        if (PathGuard.Sensitive(path, browser.StoreDir) is { } why) return PathGuard.Refusal(why);
        await EnsureLibAsync(t, ct);
        var info = new FileInfo(path);
        if (!info.Exists) return $"ERROR: no such file: {path}";
        if (info.Length > MaxUploadBytes) return "ERROR: files over 5 GB cannot be uploaded this way";
        var name = Path.GetFileName(path);

        // The file never exists whole on this side: a byte[] stops near 2 GB and a base64 string near 1 G characters, and the page's
        // atob cannot take that either. It is read piece by piece, each piece is decoded in the page and folded into one Blob (a Blob
        // made of Blobs refers to them, it does not copy them), and the Blob becomes the File at the end.
        // The pieces travel through the page's own JavaScript world, in a variable with a name nobody could guess beforehand, which
        // is deleted again as soon as the File has been built (a page watching for a fixed name never sees one).
        var holder = "__u" + Guid.NewGuid().ToString("N")[..16];
        string? r;
        try
        {
            await browser.EvaluateTextAsync($"window.{holder}={{b:new Blob([]),p:[]}}", t, ct);
            var buf = new byte[RawPiece];
            long sent = 0;
            int got;
            await using (var fs = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.ReadWrite,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            }))
            {
                while ((got = await ReadFullAsync(fs, buf, ct)) > 0)
                {
                    var ack = await browser.EvaluateTextAsync(PieceJs(holder, Convert.ToBase64String(buf, 0, got)), t, ct);
                    if (ack is null || !ack.Contains("piece-ok"))
                        return $"upload {name}: ERROR: the page stopped taking the file after {sent} bytes ({ack})";
                    sent += got;
                }
            }

            var target = element is null
                ? "(document.querySelector('input[type=file][accept*=\".aab\"]')||document.querySelector('input[type=file]'))"
                : $"window.__brain.resolve({Js(element)})";
            var js =
                "(function(){try{" +
                "var h=window." + holder + ";delete window." + holder + ";" +
                "var f=new File([h.b].concat(h.p)," + Js(name) + ",{type:'application/octet-stream'});" +
                "if(f.size!==" + sent + ")return 'ERR the page holds '+f.size+' of " + sent + " bytes';" +
                "var dt=new DataTransfer(); dt.items.add(f);" +
                "var inp=" + target + "; if(!inp) return 'no file input found';" +
                "try{inp.files=dt.files}catch(e){}" +
                "inp.dispatchEvent(new Event('input',{bubbles:true}));" +
                "inp.dispatchEvent(new Event('change',{bubbles:true}));" +
                "return 'injected '+f.name+' ('+f.size+' bytes); input.files='+inp.files.length;" +
                "}catch(e){return 'ERR '+String(e&&e.stack||e)}})()";
            r = await browser.EvaluateTextAsync(js, t, ct);
        }
        finally
        {
            try { await browser.EvaluateTextAsync($"delete window.{holder}", t, CancellationToken.None); } catch { /* already gone */ }
        }
        return $"upload {name}: {r}";
    }

    // ============================================================== HELPERS

    /// <summary>An AI agent opening a local file as an address would be reading it (browser_text, snapshot…): never for credential and
    /// key files, in any mode. Other local files open freely unless the user turned on strict asking (Seatbelt.Strict).</summary>
    private string? FileUrlRefusal(string? url)
    {
        if (url is null || !url.TrimStart().StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return null;
        var why = PathGuard.Sensitive(url.Trim(), browser.StoreDir);
        return why is null ? null : PathGuard.Refusal(why);
    }

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
