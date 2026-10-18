using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BrAIn.Browser;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace BrAIn.Mcp;

/// <summary>
/// Built-in agent for a local Ollama server: runs the chat/tool loop against /api/chat and executes
/// the model's tool calls on the browser, through the same guarded path as external MCP clients
/// (pause, handoff, seatbelt, activity log).
/// </summary>
public sealed class OllamaAgent
{
    public const string DefaultUrl = "http://localhost:11434";

    /// <summary>A small, well-described set: local models pick tools badly when offered all ~40.</summary>
    private static readonly string[] ToolNames =
    {
        "browser_navigate", "browser_back", "browser_snapshot", "browser_text", "browser_find",
        "browser_click", "browser_type", "browser_press_key", "browser_select_option", "browser_scroll",
        "browser_wait_for", "browser_tabs", "browser_new_tab", "browser_switch_tab", "browser_ask_human",
    };

    private const int MaxSteps = 25;
    private const int ToolResultChars = 6000;

    private const string SystemPrompt =
        "You control a real web browser through tools. Work step by step.\n" +
        "- Call browser_snapshot after navigating and before interacting: it lists page elements with refs like e5. Use those refs as the 'element' argument.\n" +
        "- Pages change after clicks, so snapshot again before using refs from an earlier snapshot.\n" +
        "- Use browser_text to read page content. Do not guess content you have not read.\n" +
        "- Never enter passwords, payment details or other secrets. If a step needs the user, use browser_ask_human.\n" +
        "- When the task is done, reply with a short plain-text answer and no tool call.";

    public sealed class Config
    {
        public string Url { get; set; } = DefaultUrl;
        public string Model { get; set; } = "";
        public int NumCtx { get; set; } = 16384;
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    private readonly BrowserController _browser;
    private readonly string _cfgPath;
    private readonly Action<object> _push;
    private readonly object _lock = new();
    private readonly List<JsonObject> _history = new();
    private Config _cfg = new();
    private AIFunction[]? _tools;
    private CancellationTokenSource? _cts;
    private bool _running;

    public OllamaAgent(BrowserController browser, string storeDir, Action<object> push)
    {
        _browser = browser;
        _push = push;
        _cfgPath = Path.Combine(storeDir, "ollama.json");
        try { if (File.Exists(_cfgPath)) _cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(_cfgPath), Json) ?? new(); }
        catch { /* corrupt file: start from defaults */ }
    }

    public Config Current => _cfg;
    public bool Running => _running;

    public void SetConfig(string? url, string? model, int? numCtx)
    {
        if (!string.IsNullOrWhiteSpace(url)) _cfg.Url = url.Trim().TrimEnd('/');
        if (model is not null) _cfg.Model = model.Trim();
        if (numCtx is >= 2048 and <= 262144) _cfg.NumCtx = numCtx.Value;
        try { File.WriteAllText(_cfgPath, JsonSerializer.Serialize(_cfg, Json)); }
        catch (Exception ex) { BrowserController.Log($"ollama: cannot save config: {ex.Message}"); }
    }

    /// <summary>
    /// Server reachability plus the installed models (/api/tags). Tries the configured address first, then
    /// $OLLAMA_HOST and the loopback defaults, and adopts whichever answers. Also repairs a missing model choice.
    /// </summary>
    public async Task<(bool Ok, string[] Models, string? Error)> ProbeAsync(CancellationToken ct = default)
    {
        var candidates = new List<string> { _cfg.Url };
        var env = Environment.GetEnvironmentVariable("OLLAMA_HOST")?.Trim();
        if (!string.IsNullOrEmpty(env))
        {
            if (!env.Contains("://")) env = "http://" + env;
            env = env.Replace("://0.0.0.0", "://127.0.0.1").TrimEnd('/');
            if (!env.Substring(env.IndexOf("://") + 3).Contains(':')) env += ":11434";
            candidates.Add(env);
        }
        candidates.Add(DefaultUrl);
        candidates.Add("http://127.0.0.1:11434");

        string? firstErr = null;
        foreach (var url in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(2));
                using var res = await Http.GetAsync(url + "/api/tags", cts.Token);
                res.EnsureSuccessStatusCode();
                var doc = await res.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: cts.Token);
                var names = (doc?["models"] as JsonArray)?
                    .Select(m => m?["name"]?.GetValue<string>()).Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).ToArray()
                    ?? Array.Empty<string>();
                var model = _cfg.Model;
                if (names.Length > 0 && !names.Contains(model)) model = names[0];
                if (!url.Equals(_cfg.Url, StringComparison.OrdinalIgnoreCase) || model != _cfg.Model)
                    SetConfig(url, model, null);
                return (true, names, null);
            }
            catch (Exception ex) { firstErr ??= ex.Message; }
        }
        return (false, Array.Empty<string>(), $"Cannot reach Ollama at {_cfg.Url} ({firstErr}). Start it with `ollama serve` or open the Ollama app; BrAIn keeps checking.");
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
    }

    public void Clear()
    {
        Stop();
        lock (_lock) _history.Clear();
    }

    /// <summary>Runs one user task to completion. Progress is pushed to the page as events.</summary>
    public async Task SendAsync(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        lock (_lock)
        {
            if (_running) { Emit("error", "The agent is still working. Stop it first."); return; }
            _running = true;
        }
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        int turnStart;
        lock (_lock) turnStart = _history.Count;
        Emit("state", "running");
        try
        {
            if (string.IsNullOrEmpty(_cfg.Model)) throw new InvalidOperationException("Pick a model first.");
            var tools = GetTools();
            lock (_lock) _history.Add(new JsonObject { ["role"] = "user", ["content"] = text });
            Emit("user", text);
            await LoopAsync(tools, ct);
        }
        catch (OperationCanceledException)
        {
            // Cancelled means rejected: forget the whole task so a half-done turn can't leak into the next one.
            lock (_lock) if (_history.Count > turnStart) _history.RemoveRange(turnStart, _history.Count - turnStart);
            Emit("note", "Cancelled. The task was discarded.");
        }
        catch (Exception ex) { Emit("error", ex.Message); }
        finally
        {
            _running = false;
            Emit("state", "idle");
        }
    }

    private async Task LoopAsync(AIFunction[] tools, CancellationToken ct)
    {
        var toolDefs = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["parameters"] = JsonNode.Parse(t.JsonSchema.GetRawText()),
            },
        }).ToArray());

        var tries = new Dictionary<string, int>();
        for (var step = 0; step < MaxSteps; step++)
        {
            var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = $"You are the BrAIn browser assistant, running locally on the Ollama model '{_cfg.Model}'. If asked who you are, say so.\n" + SystemPrompt } };
            lock (_lock) foreach (var m in Compact(_history)) messages.Add(m.DeepClone());

            var body = new JsonObject
            {
                ["model"] = _cfg.Model,
                ["messages"] = messages,
                ["tools"] = toolDefs.DeepClone(),
                ["options"] = new JsonObject { ["num_ctx"] = _cfg.NumCtx },
            };
            var (content, calls) = await StreamChatAsync(body, ct);
            lock (_lock) _history.Add(new JsonObject { ["role"] = "assistant", ["content"] = content, ["tool_calls"] = calls?.DeepClone() });

            if (calls is null || calls.Count == 0) return;

            foreach (var call in calls)
            {
                ct.ThrowIfCancellationRequested();
                var fn = call?["function"];
                var name = fn?["name"]?.GetValue<string>() ?? "";
                var args = fn?["arguments"] as JsonObject ?? new JsonObject();
                Emit("tool", name, args.ToJsonString());
                var (ok, result) = await RunToolAsync(tools, name, args, ct);
                // Small models loop: the same action again and again, with a snapshot in between. Say so, plainly.
                if (name is not ("browser_snapshot" or "browser_text" or "browser_find" or "browser_tabs"))
                {
                    var key = name + args.ToJsonString();
                    var n = tries[key] = tries.GetValueOrDefault(key) + 1;
                    if (n >= 3)
                        result += $"\n[NOTE: you have called {name} with these exact arguments {n} times. Repeating it will not help. Take a fresh browser_snapshot and try a different element or approach, or use browser_ask_human.]";
                }
                Emit("result", name, Clip(result, 400), ok);
                lock (_lock) _history.Add(new JsonObject { ["role"] = "tool", ["tool_name"] = name, ["content"] = Clip(result, ToolResultChars, " …[output clipped: use browser_find, browser_text with a selector, or scroll to see more]") });
            }
        }
        Emit("note", $"Stopped after {MaxSteps} steps. Send another message to continue.");
    }

    /// <summary>Models Ollama reported as unable to think; asked once, then skipped.</summary>
    private readonly HashSet<string> _noThink = new();

    /// <summary>
    /// One streamed /api/chat turn. Thinking and answer text are pushed to the page as they arrive
    /// (batched every ~60ms so the UI thread isn't flooded); the full answer and tool calls are returned.
    /// </summary>
    private async Task<(string Content, JsonArray? Calls)> StreamChatAsync(JsonObject body, CancellationToken ct)
    {
        var think = !_noThink.Contains(_cfg.Model);
        while (true)
        {
            body["stream"] = true;
            if (think) body["think"] = true; else body.Remove("think");
            using var req = new HttpRequestMessage(HttpMethod.Post, _cfg.Url + "/api/chat")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!res.IsSuccessStatusCode)
            {
                var err = ErrorText(await res.Content.ReadAsStringAsync(ct)) ?? $"Ollama returned {(int)res.StatusCode}.";
                // "<model> does not support thinking": remember it and go again without.
                if (think && err.Contains("think", StringComparison.OrdinalIgnoreCase)) { _noThink.Add(_cfg.Model); think = false; continue; }
                throw new InvalidOperationException(err);
            }

            await using var stream = await res.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            var content = new StringBuilder();
            JsonArray? calls = null;
            StringBuilder thinkBuf = new(), textBuf = new();
            var since = System.Diagnostics.Stopwatch.StartNew();
            void Flush()
            {
                if (thinkBuf.Length > 0) { Emit("think", thinkBuf.ToString()); thinkBuf.Clear(); }
                if (textBuf.Length > 0) { Emit("delta", textBuf.ToString()); textBuf.Clear(); }
                since.Restart();
            }

            string? line;
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                if (line.Length == 0) continue;
                var j = JsonNode.Parse(line);
                if (j?["error"] is JsonNode e) throw new InvalidOperationException(e.ToString());
                var m = j?["message"];
                if (m?["thinking"]?.GetValue<string>() is { Length: > 0 } th) thinkBuf.Append(th);
                if (m?["content"]?.GetValue<string>() is { Length: > 0 } c) { content.Append(c); textBuf.Append(c); }
                if (m?["tool_calls"] is JsonArray tc)
                {
                    calls ??= new JsonArray();
                    foreach (var call in tc) calls.Add(call?.DeepClone());
                }
                if (since.ElapsedMilliseconds > 60) Flush();
            }
            Flush();
            Emit("turn_end", "");
            return (content.ToString(), calls);
        }
    }

    private async Task<(bool Ok, string Text)> RunToolAsync(AIFunction[] tools, string name, JsonObject args, CancellationToken ct)
    {
        var fn = tools.FirstOrDefault(t => t.Name == name);
        if (fn is null)
            return (false, $"Unknown tool '{name}'. Available: {string.Join(", ", tools.Select(t => t.Name))}.");

        var dict = new Dictionary<string, JsonElement>();
        foreach (var kv in args)
            if (kv.Value is not null) dict[kv.Key] = JsonSerializer.SerializeToElement(kv.Value);

        var res = await ActivityRecorder.RunAsync(_browser, _browser.Activity, name, dict, async toolCt =>
        {
            try
            {
                var o = await fn.InvokeAsync(new AIFunctionArguments(dict.ToDictionary(k => k.Key, k => (object?)k.Value)), toolCt);
                var text = o is JsonElement { ValueKind: JsonValueKind.String } je ? je.GetString() : o?.ToString();
                return new CallToolResult { Content = [new TextContentBlock { Text = text ?? "" }] };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Bad arguments from the model land here; tell it so it can correct itself.
                return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = $"FAILED: {ex.Message}" }] };
            }
        }, ct);

        var sb = new StringBuilder();
        foreach (var c in res.Content)
            sb.Append(c is TextContentBlock t ? t.Text : "[non-text result]");
        var s = sb.ToString();
        var failed = res.IsError == true || s.StartsWith("FAILED:", StringComparison.Ordinal) || s.StartsWith("Navigation failed", StringComparison.Ordinal);
        return (!failed, s);
    }

    /// <summary>Old tool output is what blows a local model's context: keep the newest two in full.</summary>
    private static List<JsonObject> Compact(List<JsonObject> history)
    {
        var toolIdx = history.Select((m, i) => (m, i)).Where(x => x.m["role"]?.GetValue<string>() == "tool").Select(x => x.i).ToList();
        var keep = toolIdx.Skip(Math.Max(0, toolIdx.Count - 2)).ToHashSet();
        var list = new List<JsonObject>(history.Count);
        for (var i = 0; i < history.Count; i++)
        {
            var m = history[i];
            if (m["role"]?.GetValue<string>() == "tool" && !keep.Contains(i))
            {
                m = (JsonObject)m.DeepClone();
                m["content"] = Clip(m["content"]?.GetValue<string>() ?? "", 300) + " …[older output trimmed]";
            }
            list.Add(m);
        }
        return list;
    }

    private AIFunction[] GetTools()
    {
        if (_tools is not null) return _tools;
        var target = new BrowserTools(_browser);
        var list = new List<AIFunction>();
        foreach (var m in typeof(BrowserTools).GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            var attr = m.GetCustomAttribute<McpServerToolAttribute>();
            if (attr?.Name is null || !ToolNames.Contains(attr.Name)) continue;
            list.Add(AIFunctionFactory.Create(m, target, new AIFunctionFactoryOptions { Name = attr.Name }));
        }
        return _tools = list.OrderBy(t => Array.IndexOf(ToolNames, t.Name)).ToArray();
    }

    private static string? ErrorText(string raw)
    {
        try { return JsonNode.Parse(raw)?["error"]?.GetValue<string>(); } catch { return null; }
    }

    private static string Clip(string s, int n, string more = "…") => s.Length <= n ? s : s[..n] + more;

    private void Emit(string kind, string text, string? extra = null, bool ok = true)
        => _push(new { b = 95, ev = kind, text, extra, ok });
}
