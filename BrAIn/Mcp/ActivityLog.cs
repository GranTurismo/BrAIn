using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using BrAIn.Browser;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace BrAIn.Mcp;

/// <summary>Stable selectors for the element a call targeted (see brain.js locator()).</summary>
public sealed class ActivityLocator
{
    [JsonPropertyName("sel")] public string? Sel { get; set; }
    [JsonPropertyName("alts")] public List<string>? Alts { get; set; }
    [JsonPropertyName("tag")] public string? Tag { get; set; }
    [JsonPropertyName("role")] public string? Role { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("sensitive")] public bool Sensitive { get; set; }
}

/// <summary>One MCP tool call, as stored in calls.jsonl.</summary>
public sealed class ActivityEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    /// <summary>Unix milliseconds.</summary>
    [JsonPropertyName("t")] public long T { get; set; }
    [JsonPropertyName("tool")] public string Tool { get; set; } = "";
    [JsonPropertyName("args")] public JsonObject? Args { get; set; }
    [JsonPropertyName("loc")] public ActivityLocator? Loc { get; set; }
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("ms")] public int Ms { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("res")] public string? Res { get; set; }
    /// <summary>A secret (password field, cookie value) was replaced with ••••.</summary>
    [JsonPropertyName("masked")] public bool Masked { get; set; }
    /// <summary>The call was a saved flow, not a primitive tool.</summary>
    [JsonPropertyName("flow")] public bool Flow { get; set; }
}

/// <summary>
/// Append-only history of MCP tool calls: a JSON-lines file in the app data
/// folder, trimmed to the newest <see cref="Cap"/> entries. This is the raw
/// material saved flows are built from.
/// </summary>
public sealed class ActivityLog
{
    public const int Cap = 1000;
    private const int RewriteEvery = 200;
    internal const string MaskText = "••••••";

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;
    private readonly List<ActivityEntry> _items = new();
    private readonly object _lock = new();
    private int _sinceRewrite;

    /// <summary>Raised after an entry is added or the log is cleared (any thread).</summary>
    public event Action? Changed;

    public ActivityLog(string storeDir)
    {
        _path = Path.Combine(storeDir, "calls.jsonl");
        try
        {
            if (File.Exists(_path))
                foreach (var line in File.ReadLines(_path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var e = JsonSerializer.Deserialize<ActivityEntry>(line, Json);
                        if (e is not null && e.Id.Length > 0) _items.Add(e);
                    }
                    catch { /* skip a corrupt line */ }
                }
        }
        catch { /* unreadable log = start empty */ }
        if (_items.Count > Cap) _items.RemoveRange(0, _items.Count - Cap);
    }

    public void Add(ActivityEntry e)
    {
        lock (_lock)
        {
            _items.Add(e);
            var trimmed = false;
            while (_items.Count > Cap) { _items.RemoveAt(0); trimmed = true; }
            try
            {
                if (trimmed && ++_sinceRewrite >= RewriteEvery)
                {
                    _sinceRewrite = 0;
                    File.WriteAllLines(_path, _items.Select(x => JsonSerializer.Serialize(x, Json)));
                }
                else
                {
                    File.AppendAllText(_path, JsonSerializer.Serialize(e, Json) + "\n");
                }
            }
            catch { /* disk problems must never break a tool call */ }
        }
        try { Changed?.Invoke(); } catch { }
    }

    /// <summary>Newest-last slice of the last <paramref name="n"/> entries.</summary>
    public List<ActivityEntry> Recent(int n)
    {
        lock (_lock) return _items.Skip(Math.Max(0, _items.Count - Math.Max(1, n))).ToList();
    }

    public List<ActivityEntry> Find(IEnumerable<string> ids)
    {
        var set = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
        lock (_lock) return _items.Where(e => set.Contains(e.Id)).ToList();
    }

    public void Clear()
    {
        lock (_lock)
        {
            _items.Clear();
            try { File.WriteAllText(_path, ""); } catch { }
        }
        try { Changed?.Invoke(); } catch { }
    }

    // ------------------------------------------------------------------
    // Classification helpers (shared with the flow builder)
    // ------------------------------------------------------------------

    /// <summary>Tools that only read state — never part of a replayable flow.</summary>
    public static readonly HashSet<string> ReadOnlyTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "browser_state", "browser_snapshot", "browser_text", "browser_markdown", "browser_html", "browser_find",
        "browser_describe", "browser_listeners", "browser_rewind", "browser_screenshot", "browser_console_log",
        "browser_network_requests", "browser_tabs", "browser_cookies", "browser_activity",
        "browser_list_flows", "browser_save_flow", "browser_run_flow", "browser_ask_human",
    };

    /// <summary>Tools whose "element" argument should be resolved to a stable locator when logged.</summary>
    internal static readonly HashSet<string> ElementTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "browser_click", "browser_double_click", "browser_hover", "browser_type",
        "browser_select_option", "browser_set_checked", "browser_scroll_to", "browser_upload", "browser_wait_for",
    };

    public static string Summarize(CallToolResult? r)
    {
        if (r is null) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var c in r.Content)
        {
            if (c is TextContentBlock t) sb.Append(t.Text);
            else if (c is ImageContentBlock) sb.Append("[image]");
            else sb.Append('[').Append(c.GetType().Name).Append(']');
            if (sb.Length > 600) break;
        }
        return Clip(sb.ToString(), 500);
    }

    /// <summary>One-line "key=value" view of a call's arguments for lists.</summary>
    public static string ArgsSummary(ActivityEntry e)
    {
        if (e.Args is null) return "";
        var parts = new List<string>();
        foreach (var kv in e.Args)
        {
            if (kv.Key == "tab") continue;
            var v = kv.Value?.ToString() ?? "";
            if (kv.Key == "element" && e.Loc?.Sel is { Length: > 0 } sel) v = sel;
            parts.Add($"{kv.Key}={Clip(v.Replace('\n', ' '), 80)}");
        }
        return Clip(string.Join("  ", parts), 220);
    }

    public static string Clip(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";
}

/// <summary>
/// The MCP request filter that records every tool call. One filter covers all
/// tools (including flows registered at runtime), so BrowserTools stays untouched.
/// </summary>
internal static class ActivityRecorder
{
    public static McpRequestFilter<CallToolRequestParams, CallToolResult> Create(
        BrowserController browser, ActivityLog log)
        => next => (ctx, ct) => new ValueTask<CallToolResult>(
            RunAsync(browser, log, ctx.Params?.Name ?? "", ctx.Params?.Arguments, t => next(ctx, t).AsTask(), ct));

    /// <summary>
    /// The guarded path every tool call takes — pause, handoff, seatbelt, locator capture and the
    /// activity log. The MCP filter and the built-in Ollama agent both run through it.
    /// </summary>
    public static async Task<CallToolResult> RunAsync(
        BrowserController browser, ActivityLog log, string tool,
        IDictionary<string, JsonElement>? arguments, Func<CancellationToken, Task<CallToolResult>> next, CancellationToken callerCt)
    {
            // The user's hard cancel aborts whatever is running, for any client.
            using var killLink = CancellationTokenSource.CreateLinkedTokenSource(callerCt, browser.AgentKillToken);
            var ct = killLink.Token;

            if (tool.Length == 0 || tool is "browser_activity" or "browser_list_flows")
                return await next(ct);

            // Hard cancel: nothing runs, reads included, until the user resumes the agent.
            if (browser.AgentCancelled)
            {
                const string cmsg = "CANCELLED: the user cancelled this AI agent in BrAIn. Stop the task entirely. Do not call any more tools and do not retry; tell the user the task was cancelled.";
                browser.AgentBlocked(tool);
                log.Add(new ActivityEntry
                {
                    Id = Guid.NewGuid().ToString("N")[..8],
                    T = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Tool = tool, Ok = false, Res = cmsg,
                });
                return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = cmsg }] };
            }

            // User pause: refuse anything that acts on the browser (reads stay allowed).
            if (browser.AgentPaused && (!ActivityLog.ReadOnlyTools.Contains(tool) || tool == "browser_run_flow"))
            {
                browser.AgentBlocked(tool);
                const string msg = "BLOCKED: the user has paused the agent in BrAIn. Do not retry. Tell the user you are waiting and ask them to click the Agent button in the toolbar (or press Ctrl+Shift+.) to resume.";
                log.Add(new ActivityEntry
                {
                    Id = Guid.NewGuid().ToString("N")[..8],
                    T = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Tool = tool, Ok = false, Res = msg,
                    Flow = tool.StartsWith("flow_", StringComparison.Ordinal) || tool == "browser_run_flow",
                });
                return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = msg }] };
            }

            browser.AgentBegin(tool);
            try
            {
            var args = new JsonObject();
            if (arguments is not null)
                foreach (var kv in arguments)
                    args[kv.Key] = JsonNode.Parse(kv.Value.GetRawText());

            // Resolve the stable locator BEFORE the call: a click can navigate
            // away and take the element (and its ref) with it.
            ActivityLocator? loc = null;
            var masked = false;
            try
            {
                using var lcts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                lcts.CancelAfter(TimeSpan.FromMilliseconds(1500));
                var tab = ResolveTab(browser, args);
                if (tab is not null)
                {
                    if (ActivityLog.ElementTools.Contains(tool) && args["element"]?.GetValue<string>() is { Length: > 0 } el)
                    {
                        await browser.EnsurePageHelpersAsync(tab);
                        var r = await browser.EvaluateAsync($"window.__brain.locator({JsonSerializer.Serialize(el)})", false, tab, lcts.Token);
                        if (r is { ValueKind: JsonValueKind.Object })
                            loc = JsonSerializer.Deserialize<ActivityLocator>(r.Value.GetRawText());
                    }
                    if (tool == "browser_type_text")
                    {
                        var r = await browser.EvaluateAsync("window.__brain && window.__brain.isSensitiveFocus ? window.__brain.isSensitiveFocus() : false", false, tab, lcts.Token);
                        if (r?.ValueKind == JsonValueKind.True && args.ContainsKey("text")) { args["text"] = ActivityLog.MaskText; masked = true; }
                    }
                }
            }
            catch { /* locator is best effort */ }

            if (loc is { Sensitive: true } && tool == "browser_type" && args.ContainsKey("text"))
            { args["text"] = ActivityLog.MaskText; masked = true; }
            if (tool == "browser_cookies" && args.ContainsKey("value"))
            { args["value"] = ActivityLog.MaskText; masked = true; }
            if (tool == "browser_evaluate" && args["script"]?.GetValue<string>() is { } sc)
                args["script"] = ActivityLog.Clip(sc, 2000);

            // A "your turn" handoff is open: the user is doing a step in the page.
            // Everything that would act on the browser waits — including flows,
            // which replay actions even though the tool itself is read-classified.
            if ((!ActivityLog.ReadOnlyTools.Contains(tool) || tool == "browser_run_flow") && browser.Handoff.HasPending)
            {
                log.Add(new ActivityEntry
                {
                    Id = Guid.NewGuid().ToString("N")[..8],
                    T = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Tool = tool, Ok = false, Res = Handoff.BusyText,
                });
                return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = Handoff.BusyText }] };
            }
            if (!ActivityLog.ReadOnlyTools.Contains(tool) && browser.Belt.HasPending)
            {
                log.Add(new ActivityEntry
                {
                    Id = Guid.NewGuid().ToString("N")[..8],
                    T = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Tool = tool, Ok = false, Res = Seatbelt.BusyText,
                });
                return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = Seatbelt.BusyText }] };
            }
            if (browser.Belt.Gates(tool, args))
            {
                var gtab = ResolveTab(browser, args);
                BeltFacts? facts = null;
                try { facts = await ProbeAsync(browser, gtab, tool, args, ct); } catch { /* fall back to the locator */ }
                var denied = await browser.Belt.GateAsync(tool, args, loc, facts, gtab?.LastUrl, ct);
                if (denied is not null)
                {
                    log.Add(new ActivityEntry
                    {
                        Id = Guid.NewGuid().ToString("N")[..8],
                        T = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        Tool = tool, Args = args, Loc = loc, Ok = false, Url = gtab?.LastUrl, Res = denied,
                    });
                    return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = denied }] };
                }
            }

            var url = browser.Tabs.Active?.LastUrl;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            CallToolResult? result = null;
            string? error = null;
            try
            {
                result = await next(ct);
                return result;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                throw;
            }
            finally
            {
                sw.Stop();
                var ok = error is null && result is { IsError: not true };
                var text = error ?? ActivityLog.Summarize(result);
                // The host never lets a failed element lookup throw — it returns
                // {"ok":false,...}; surface that as a failure in the log too.
                if (ok && (text.Contains("\"ok\":false", StringComparison.Ordinal) || text.Contains("element not found", StringComparison.OrdinalIgnoreCase)
                           || text.StartsWith("Navigation failed", StringComparison.Ordinal)
                           || text.StartsWith("FAILED:", StringComparison.Ordinal)))
                    ok = false;
                log.Add(new ActivityEntry
                {
                    Id = Guid.NewGuid().ToString("N")[..8],
                    T = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Tool = tool,
                    Args = args,
                    Loc = loc,
                    Ok = ok,
                    Ms = (int)sw.ElapsedMilliseconds,
                    Url = url,
                    Res = tool == "browser_cookies" ? "(cookie data not logged)" : text,
                    Masked = masked,
                    Flow = tool.StartsWith("flow_", StringComparison.Ordinal) || tool == "browser_run_flow",
                });
                // Rewind (when the user enabled it): tag a snapshot with what the agent just did.
                try { browser.RewindAfterTool(tool, ResolveTab(browser, args)); } catch { }
            }
            }
            finally { browser.AgentEnd(); }
    }

    /// <summary>Asks the page what the click/Enter would hit (brain.js risk*). Null on old scripts or failure.</summary>
    private static async Task<BeltFacts?> ProbeAsync(BrowserController browser, BrowserTab? tab, string tool, JsonObject args, CancellationToken ct)
    {
        if (tab is null) return null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(1500));
        string call;
        if (tool.Equals("browser_click_at", StringComparison.OrdinalIgnoreCase))
        {
            if (!double.TryParse(args["x"]?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x)) return null;
            if (!double.TryParse(args["y"]?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y)) return null;
            call = FormattableString.Invariant($"window.__brain.riskAt({x},{y})");
        }
        else if (tool.Equals("browser_press_key", StringComparison.OrdinalIgnoreCase)) call = "window.__brain.riskKey()";
        else if (args["element"]?.ToString() is { Length: > 0 } el) call = $"window.__brain.risk({JsonSerializer.Serialize(el)})";
        else return null;
        await browser.EnsurePageHelpersAsync(tab);
        var r = await browser.EvaluateAsync($"(window.__brain && window.__brain.risk) ? {call} : null", false, tab, cts.Token);
        return r is { ValueKind: JsonValueKind.Object } ? JsonSerializer.Deserialize<BeltFacts>(r.Value.GetRawText()) : null;
    }

    private static BrowserTab? ResolveTab(BrowserController browser, JsonObject args)
    {
        try
        {
            if (args["tab"]?.GetValue<string>() is { Length: > 0 } t)
                return browser.Tabs.FindByPrefix(t);
            return browser.Tabs.Active;
        }
        catch { return null; }
    }
}
