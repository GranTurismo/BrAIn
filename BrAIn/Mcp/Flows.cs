using System.ComponentModel;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BrAIn.Browser;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace BrAIn.Mcp;

// ======================================================================
// Model — one JSON file per flow, written so a person can edit it by hand.
// ======================================================================

public sealed class FlowParam
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string? Description { get; set; }
    /// <summary>string | number | boolean</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = "string";
    [JsonPropertyName("required")] public bool Required { get; set; } = true;
    [JsonPropertyName("default")] public JsonNode? Default { get; set; }
}

public sealed class FlowStep
{
    /// <summary>Any allowed browser_* tool, e.g. browser_click.</summary>
    [JsonPropertyName("tool")] public string Tool { get; set; } = "";
    /// <summary>Tool arguments. Strings may contain {{param}} placeholders.</summary>
    [JsonPropertyName("args")] public JsonObject Args { get; set; } = new();
    /// <summary>Fallback selectors for args.element, tried in order if the primary doesn't resolve.</summary>
    [JsonPropertyName("alts")] public List<string>? Alts { get; set; }
    [JsonPropertyName("note")] public string? Note { get; set; }
    /// <summary>If true, a failure of this step doesn't stop the flow.</summary>
    [JsonPropertyName("optional"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Optional { get; set; }
}

public sealed class FlowDef
{
    /// <summary>Set on pending proposals only.</summary>
    [JsonPropertyName("id")] public string? Id { get; set; }
    /// <summary>Tool name is "flow_" + name.</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("parameters")] public List<FlowParam> Parameters { get; set; } = new();
    [JsonPropertyName("steps")] public List<FlowStep> Steps { get; set; } = new();
    /// <summary>Optional selector whose text is returned when the flow finishes.</summary>
    [JsonPropertyName("returns")] public string? Returns { get; set; }
    [JsonPropertyName("created")] public long Created { get; set; }
    /// <summary>agent | manual | activity</summary>
    [JsonPropertyName("source")] public string? Source { get; set; }
}

public sealed class FlowRunResult
{
    public bool Ok { get; init; }
    public string Text { get; init; } = "";
}

/// <summary>How the agent names a value that should become a flow argument.</summary>
public sealed class FlowParamSpec
{
    [Description("Argument name: lowercase letters, digits and underscores (e.g. 'amount').")]
    public string Name { get; set; } = "";
    [Description("What the argument means; shown to future agents.")]
    public string? Description { get; set; }
    [Description("The literal value used during the recorded run. Every occurrence in the recorded steps is replaced with this argument.")]
    public string? Example { get; set; }
    [Description("string (default), number or boolean.")]
    public string? Type { get; set; }
}

// ======================================================================
// Service — storage, approval queue, replay, and live MCP tool registration.
// ======================================================================

public sealed class FlowService
{
    public const string ToolPrefix = "flow_";
    private static readonly Regex NameRx = new("^[a-z][a-z0-9_]{1,39}$", RegexOptions.Compiled);
    private static readonly Regex ParamRx = new("^[a-z_][a-z0-9_]{0,31}$", RegexOptions.Compiled);
    private static readonly Regex PlaceholderRx = new(@"\{\{\s*([A-Za-z_][A-Za-z0-9_]*)\s*\}\}", RegexOptions.Compiled);
    private static readonly Regex WholePlaceholderRx = new(@"^\s*\{\{\s*([A-Za-z_][A-Za-z0-9_]*)\s*\}\}\s*$", RegexOptions.Compiled);

    /// <summary>Tools a flow step may call. Excludes anything that reads secrets or only observes.</summary>
    public static readonly string[] AllowedTools =
    {
        "browser_navigate", "browser_back", "browser_forward", "browser_reload",
        "browser_click", "browser_click_at", "browser_double_click", "browser_hover",
        "browser_type", "browser_type_text", "browser_press_key", "browser_select_option",
        "browser_set_checked", "browser_scroll", "browser_scroll_to", "browser_wait_for",
        "browser_text", "browser_evaluate", "browser_new_tab", "browser_switch_tab", "browser_upload",
    };

    /// <summary>Tools after which the page may be changing; the runner waits for it to settle.</summary>
    private static readonly HashSet<string> SettleAfter = new(StringComparer.OrdinalIgnoreCase)
    {
        "browser_navigate", "browser_back", "browser_forward", "browser_reload", "browser_click",
        "browser_double_click", "browser_press_key", "browser_new_tab", "browser_select_option", "browser_click_at",
    };

    private static readonly JsonSerializerOptions FileJson = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly BrowserController _browser;
    private readonly ActivityLog _log;
    private readonly string _dir;
    private readonly string _pendingDir;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly Dictionary<string, MethodInfo> _methods = new(StringComparer.OrdinalIgnoreCase);
    private readonly BrowserTools _tools;
    private readonly Dictionary<string, McpServerTool> _registered = new(StringComparer.Ordinal);
    private McpServerPrimitiveCollection<McpServerTool>? _collection;

    /// <summary>Raised when a flow or proposal is added, changed or removed.</summary>
    public event Action? Changed;

    public FlowService(BrowserController browser, ActivityLog log)
    {
        _browser = browser;
        _log = log;
        _dir = Path.Combine(browser.StoreDir, "flows");
        _pendingDir = Path.Combine(_dir, "pending");
        try { Directory.CreateDirectory(_pendingDir); } catch { }
        _tools = new BrowserTools(browser);
        foreach (var m in typeof(BrowserTools).GetMethods(BindingFlags.Public | BindingFlags.Instance))
            if (m.GetCustomAttribute<McpServerToolAttribute>() is { Name: { Length: > 0 } n })
                _methods[n] = m;
    }

    public string FlowsDir => _dir;

    // ---------------- storage ----------------

    public List<FlowDef> List()
        => ReadAll(_dir).OrderBy(f => f.Name, StringComparer.Ordinal).ToList();

    public List<FlowDef> Pending()
        => ReadAll(_pendingDir).OrderByDescending(f => f.Created).ToList();

    public FlowDef? Get(string name)
        => List().FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<FlowDef> ReadAll(string dir)
    {
        if (!Directory.Exists(dir)) yield break;
        foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
        {
            FlowDef? d = null;
            try { d = JsonSerializer.Deserialize<FlowDef>(File.ReadAllText(f), FileJson); }
            catch { /* hand-edited file with a syntax error: skip, don't crash */ }
            if (d is not null && d.Name.Length > 0) yield return d;
        }
    }

    public static string Serialize(FlowDef d) => JsonSerializer.Serialize(d, FileJson);

    public static FlowDef? Parse(string json) => JsonSerializer.Deserialize<FlowDef>(json, FileJson);

    public static string Normalize(string? name)
    {
        var s = Regex.Replace((name ?? "").Trim().ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');
        if (s.Length == 0) return "";
        if (!char.IsLetter(s[0])) s = "f_" + s;
        return s.Length > 40 ? s[..40].TrimEnd('_') : s;
    }

    /// <summary>Returns an error message, or null when the definition is valid.</summary>
    public static string? Validate(FlowDef d)
    {
        if (!NameRx.IsMatch(d.Name)) return "name must be 2-40 chars: lowercase letters, digits, underscores, starting with a letter.";
        if (d.Steps.Count == 0) return "a flow needs at least one step.";
        if (d.Steps.Count > 80) return "a flow can have at most 80 steps.";
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in d.Parameters)
        {
            if (!ParamRx.IsMatch(p.Name)) return $"parameter '{p.Name}': use lowercase letters, digits, underscores.";
            if (!names.Add(p.Name)) return $"duplicate parameter '{p.Name}'.";
            if (p.Type is not ("string" or "number" or "boolean")) return $"parameter '{p.Name}': type must be string, number or boolean.";
        }
        for (var i = 0; i < d.Steps.Count; i++)
        {
            var s = d.Steps[i];
            if (!AllowedTools.Contains(s.Tool, StringComparer.OrdinalIgnoreCase))
                return $"step {i + 1}: tool '{s.Tool}' is not allowed in a flow. Allowed: {string.Join(", ", AllowedTools)}.";
            foreach (var used in Placeholders(s.Args))
                if (!names.Contains(used)) return $"step {i + 1}: uses {{{{{used}}}}} but no such parameter is declared.";
        }
        return null;
    }

    private static IEnumerable<string> Placeholders(JsonNode? n)
    {
        switch (n)
        {
            case JsonValue v when v.TryGetValue<string>(out var s):
                foreach (Match m in PlaceholderRx.Matches(s)) yield return m.Groups[1].Value;
                break;
            case JsonObject o:
                foreach (var kv in o) foreach (var x in Placeholders(kv.Value)) yield return x;
                break;
            case JsonArray a:
                foreach (var e in a) foreach (var x in Placeholders(e)) yield return x;
                break;
        }
    }

    /// <summary>Creates or replaces an active flow. <paramref name="oldName"/> renames.</summary>
    public string? Save(FlowDef d, string? oldName = null)
    {
        d.Name = Normalize(d.Name);
        d.Id = null;
        var err = Validate(d);
        if (err is not null) return err;
        if (d.Created == 0) d.Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        lock (_lock)
        {
            File.WriteAllText(PathFor(_dir, d.Name), Serialize(d));
            if (!string.IsNullOrEmpty(oldName) && !oldName.Equals(d.Name, StringComparison.OrdinalIgnoreCase))
                TryDelete(PathFor(_dir, Normalize(oldName)));
        }
        Reload();
        return null;
    }

    public bool Delete(string name)
    {
        var path = PathFor(_dir, Normalize(name));
        var existed = File.Exists(path);
        TryDelete(path);
        if (existed) Reload();
        return existed;
    }

    /// <summary>Stores a proposal for the user to review. Nothing becomes callable yet.</summary>
    public (string? Error, string? Id) Propose(FlowDef d)
    {
        d.Name = Normalize(d.Name);
        var err = Validate(d);
        if (err is not null) return (err, null);
        d.Id = Guid.NewGuid().ToString("N")[..8];
        d.Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        d.Source ??= "agent";
        lock (_lock) File.WriteAllText(PathFor(_pendingDir, d.Id), Serialize(d));
        Raise();
        return (null, d.Id);
    }

    /// <summary>Activates a pending proposal (optionally with the user's edits).</summary>
    public string? Approve(string id, FlowDef? edited)
    {
        var path = PathFor(_pendingDir, id);
        if (!File.Exists(path)) return "that proposal no longer exists.";
        var d = edited ?? Parse(File.ReadAllText(path));
        if (d is null) return "proposal is unreadable.";
        var err = Save(d);
        if (err is not null) return err;
        TryDelete(path);
        Raise();
        return null;
    }

    public void Reject(string id)
    {
        TryDelete(PathFor(_pendingDir, id));
        Raise();
    }

    private static string PathFor(string dir, string name)
        => Path.Combine(dir, Regex.Replace(name, "[^A-Za-z0-9_]", "") + ".json");

    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }

    private void Raise() { try { Changed?.Invoke(); } catch { } }

    // ---------------- MCP registration ----------------

    /// <summary>Hands over the live tool collection; every active flow becomes a tool.</summary>
    public void Attach(McpServerPrimitiveCollection<McpServerTool> collection)
    {
        _collection = collection;
        Reload();
    }

    /// <summary>Re-syncs the MCP tool list with the flows on disk. The collection
    /// raises its Changed event, which the SDK turns into tools/list_changed.</summary>
    public void Reload()
    {
        var c = _collection;
        if (c is not null)
        {
            lock (_lock)
            {
                using (c.DeferChangedEvents())
                {
                    foreach (var t in _registered.Values) c.Remove(t);
                    _registered.Clear();
                    foreach (var f in List())
                    {
                        if (Validate(f) is not null) continue; // hand-edited into an invalid state
                        try
                        {
                            var tool = McpServerTool.Create(new FlowFunction(this, f), new McpServerToolCreateOptions
                            {
                                Name = ToolPrefix + f.Name,
                                Description = FlowFunction.DescribeFor(f),
                                Destructive = false,
                                OpenWorld = true,
                            });
                            if (c.TryAdd(tool)) _registered[tool.ProtocolTool.Name] = tool;
                        }
                        catch { /* one bad flow must not block the others */ }
                    }
                }
            }
        }
        Raise();
    }

    // ---------------- building flows from recorded calls ----------------

    private static readonly string MaskText = ActivityLog.MaskText;

    /// <summary>Converts logged calls into flow steps: drops reads and failures,
    /// swaps refs for stable locators, and turns masked secrets and named example
    /// values into parameters. Never copies a secret.</summary>
    public FlowDef BuildFromActivity(IEnumerable<ActivityEntry> entries, string name, string description,
        IEnumerable<FlowParamSpec>? specs)
    {
        var def = new FlowDef
        {
            Name = Normalize(name),
            Description = description ?? "",
            Source = "activity",
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        var secretN = 0;
        foreach (var e in entries.OrderBy(x => x.T))
        {
            if (!e.Ok || e.Flow || ActivityLog.ReadOnlyTools.Contains(e.Tool)) continue;
            if (!AllowedTools.Contains(e.Tool, StringComparer.OrdinalIgnoreCase)) continue;
            if (e.Tool.Equals("browser_text", StringComparison.OrdinalIgnoreCase)) continue;
            var step = new FlowStep { Tool = e.Tool, Args = (JsonObject)(e.Args?.DeepClone() ?? new JsonObject()) };
            step.Args.Remove("tab"); // tab ids don't survive a restart
            if (e.Loc is { Sel: { Length: > 0 } } loc && step.Args.ContainsKey("element"))
            {
                step.Args["element"] = loc.Sel;
                if (loc.Alts is { Count: > 0 }) step.Alts = new List<string>(loc.Alts);
                if (!string.IsNullOrEmpty(loc.Name)) step.Note = $"{loc.Role ?? loc.Tag}: {loc.Name}";
            }
            // Masked values stand for secrets: make the caller supply them.
            foreach (var key in step.Args.Select(kv => kv.Key).ToList())
            {
                if (step.Args[key] is JsonValue v && v.TryGetValue<string>(out var sv) && sv == MaskText)
                {
                    var pname = $"secret_{++secretN}";
                    step.Args[key] = "{{" + pname + "}}";
                    def.Parameters.Add(new FlowParam
                    {
                        Name = pname,
                        Description = $"Secret entered at this step during recording (not stored). {step.Note}".Trim(),
                    });
                }
            }
            def.Steps.Add(step);
        }
        foreach (var spec in specs ?? Enumerable.Empty<FlowParamSpec>())
        {
            var pname = Normalize(spec.Name);
            if (pname.Length == 0) continue;
            def.Parameters.Add(new FlowParam
            {
                Name = pname,
                Description = spec.Description,
                Type = spec.Type is "number" or "boolean" ? spec.Type : "string",
            });
            if (!string.IsNullOrEmpty(spec.Example))
                foreach (var s in def.Steps)
                    ReplaceExample(s.Args, spec.Example!, pname);
        }
        // Parameters whose example never appeared are still valid (agent may add them by hand).
        return def;
    }

    private static void ReplaceExample(JsonObject o, string example, string pname)
    {
        foreach (var key in o.Select(kv => kv.Key).ToList())
        {
            if (key is "element" or "tab") continue; // never rewrite locators
            switch (o[key])
            {
                case JsonValue v when v.TryGetValue<string>(out var s):
                    if (s.Contains(example, StringComparison.Ordinal))
                        o[key] = s == example ? "{{" + pname + "}}" : s.Replace(example, "{{" + pname + "}}", StringComparison.Ordinal);
                    break;
                case JsonValue v2 when example.Length > 0 && v2.ToJsonString() == example:
                    o[key] = "{{" + pname + "}}"; // number/bool example
                    break;
            }
        }
    }

    // ---------------- replay ----------------

    /// <summary>Set by the host: true while the user has paused the agent.</summary>
    public Func<bool>? PausedCheck { get; set; }

    public async Task<FlowRunResult> RunAsync(FlowDef def, JsonObject? input, CancellationToken ct, bool honorPause = true)
    {
        if (Validate(def) is { } verr) return new FlowRunResult { Ok = false, Text = $"Flow '{def.Name}' is invalid: {verr}" };

        // Resolve arguments (defaults, required, type coercion).
        var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var p in def.Parameters)
        {
            JsonNode? v = input is not null && input.TryGetPropertyValue(p.Name, out var given) ? given : null;
            if (v is null) v = p.Default?.DeepClone();
            if (v is null)
            {
                if (p.Required) return new FlowRunResult { Ok = false, Text = $"Missing required argument '{p.Name}'." };
                v = JsonValue.Create("");
            }
            values[p.Name] = Coerce(v, p.Type);
        }

        await _runGate.WaitAsync(ct);
        try
        {
            var report = new StringBuilder();
            var outputs = new List<string>();
            for (var i = 0; i < def.Steps.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (honorPause && PausedCheck?.Invoke() == true)
                    return new FlowRunResult { Ok = false, Text = $"Flow '{def.Name}' paused by the user before step {i + 1}/{def.Steps.Count}. Steps 1-{i} completed. Ask the user to resume the agent in BrAIn before continuing." };
                var step = def.Steps[i];
                var label = $"step {i + 1}/{def.Steps.Count} ({step.Tool}{Describe(step)})";
                string text;
                try
                {
                    text = await RunStepAsync(step, values, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    text = "ERROR: " + (ex.InnerException?.Message ?? ex.Message);
                }
                var failed = IsFailure(text);
                if (failed && !step.Optional)
                {
                    var ctx = await PageContextAsync(ct);
                    return new FlowRunResult
                    {
                        Ok = false,
                        Text = $"Flow '{def.Name}' stopped at {label}: {ActivityLog.Clip(text, 400)}\n" +
                               $"Steps 1-{i} completed.{ctx}\n" +
                               "Finish the task manually with the browser_* tools; tell the user the flow needs updating (about:flows).",
                    };
                }
                if (!failed && step.Tool is "browser_text" or "browser_evaluate")
                    outputs.Add(ActivityLog.Clip(text, 3000));
                if (SettleAfter.Contains(step.Tool)) await SettleAsync(ct);
            }

            report.Append($"Flow '{def.Name}' completed ({def.Steps.Count} steps).");
            if (!string.IsNullOrWhiteSpace(def.Returns))
            {
                try
                {
                    await _browser.EnsurePageHelpersAsync(_browser.ActiveTabOrThrow());
                    var txt = await _browser.EvaluateTextAsync(
                        $"window.__brain.text({JsonSerializer.Serialize(Substitute(JsonValue.Create(def.Returns), values)?.GetValue<string>() ?? def.Returns)})",
                        null, ct);
                    outputs.Add(ActivityLog.Clip(txt ?? "(nothing found)", 3000));
                }
                catch (Exception ex) { outputs.Add("(could not read result: " + ex.Message + ")"); }
            }
            foreach (var o in outputs) report.Append("\n\n").Append(o);
            return new FlowRunResult { Ok = true, Text = report.ToString() };
        }
        finally { _runGate.Release(); }
    }

    private async Task<string> RunStepAsync(FlowStep step, Dictionary<string, JsonNode?> values, CancellationToken ct)
    {
        var args = (JsonObject)Substitute(step.Args, values)!;

        // Element steps: find the first locator that resolves, waiting for the page.
        if (args["element"] is JsonValue ev && ev.TryGetValue<string>(out var primary) && primary.Length > 0
            && ActivityLog.ElementTools.Contains(step.Tool))
        {
            var cands = new List<string> { primary };
            if (step.Alts is not null)
                foreach (var a in step.Alts) cands.Add((Substitute(JsonValue.Create(a), values) as JsonValue)?.GetValue<string>() ?? a);
            var wait = step.Tool.Equals("browser_wait_for", StringComparison.OrdinalIgnoreCase)
                ? Math.Clamp(args["timeoutMs"]?.GetValue<int>() ?? 15000, 500, 60000)
                : 8000;
            var found = await PickLocatorAsync(cands, wait, ct);
            if (found is null)
                return $"element not found (tried {string.Join(" | ", cands.Select(c => ActivityLog.Clip(c, 60)))}) within {wait}ms";
            args["element"] = found;
        }

        if (!_methods.TryGetValue(step.Tool, out var m)) return "ERROR: unknown tool " + step.Tool;
        var ps = m.GetParameters();
        var argv = new object?[ps.Length];
        for (var i = 0; i < ps.Length; i++)
        {
            var p = ps[i];
            if (p.ParameterType == typeof(CancellationToken)) { argv[i] = ct; continue; }
            if (p.Name is not null && args.TryGetPropertyValue(p.Name, out var node) && node is not null)
                argv[i] = JsonSerializer.Deserialize(node.ToJsonString(), p.ParameterType);
            else if (p.HasDefaultValue) argv[i] = p.DefaultValue;
            else return $"ERROR: step is missing required argument '{p.Name}'";
        }
        var task = (Task)m.Invoke(_tools, argv)!;
        await task;
        var result = task.GetType().GetProperty("Result")?.GetValue(task);
        return result switch
        {
            string s => s,
            CallToolResult r => ActivityLog.Summarize(r),
            null => "",
            _ => result.ToString() ?? "",
        };
    }

    private async Task<string?> PickLocatorAsync(List<string> cands, int timeoutMs, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        var json = JsonSerializer.Serialize(cands);
        do
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var tab = _browser.ActiveTabOrThrow();
                await _browser.EnsurePageHelpersAsync(tab);
                var r = await _browser.EvaluateAsync($"window.__brain.firstMatch({json})", false, tab, ct);
                if (r is { ValueKind: JsonValueKind.String }) return r.Value.GetString();
            }
            catch (OperationCanceledException) { throw; }
            catch { /* execution context lost mid-navigation: keep polling */ }
            await Task.Delay(250, ct);
        } while (DateTime.UtcNow < deadline);
        return null;
    }

    private async Task SettleAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(250, ct);
            var deadline = DateTime.UtcNow.AddSeconds(6);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    var r = await _browser.EvaluateAsync("document.readyState", false, _browser.ActiveTabOrThrow(), ct);
                    if (r is { ValueKind: JsonValueKind.String } && r.Value.GetString() == "complete" && !_browser.IsLoading) break;
                }
                catch (OperationCanceledException) { throw; }
                catch { }
                await Task.Delay(150, ct);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
    }

    private async Task<string> PageContextAsync(CancellationToken ct)
    {
        try
        {
            var tab = _browser.ActiveTabOrThrow();
            await _browser.EnsurePageHelpersAsync(tab);
            var txt = await _browser.EvaluateTextAsync("window.__brain.text()", tab, ct);
            return $"\nCurrent page: {tab.LastUrl}\nVisible text: {ActivityLog.Clip((txt ?? "").Replace('\n', ' '), 500)}";
        }
        catch { return ""; }
    }

    internal static bool IsFailure(string text)
        => text.StartsWith("ERROR", StringComparison.Ordinal)
           || text.StartsWith("Navigation failed", StringComparison.Ordinal)
           || text.StartsWith("Cannot go", StringComparison.Ordinal)
           || text.Contains("\"ok\":false", StringComparison.Ordinal)
           || text.Contains("\"ok\": false", StringComparison.Ordinal)
           || text.Contains("element not found", StringComparison.OrdinalIgnoreCase)
           || text.StartsWith("timeout after", StringComparison.Ordinal)
           || text.Contains("no focused element", StringComparison.OrdinalIgnoreCase);

    private static string Describe(FlowStep s)
        => s.Args["element"]?.ToString() is { Length: > 0 } e ? " " + ActivityLog.Clip(e, 50)
         : s.Args["url"]?.ToString() is { Length: > 0 } u ? " " + ActivityLog.Clip(u, 60) : "";

    private static JsonNode Coerce(JsonNode v, string type)
    {
        try
        {
            switch (type)
            {
                case "number":
                    if (v is JsonValue nv && nv.TryGetValue<double>(out var d)) return JsonValue.Create(d);
                    if (double.TryParse(v.ToString(), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var parsed)) return JsonValue.Create(parsed);
                    break;
                case "boolean":
                    if (v is JsonValue bv && bv.TryGetValue<bool>(out var b)) return JsonValue.Create(b);
                    if (bool.TryParse(v.ToString(), out var pb)) return JsonValue.Create(pb);
                    break;
                default:
                    return v is JsonValue sv && sv.TryGetValue<string>(out var s) ? JsonValue.Create(s)! : JsonValue.Create(v.ToString())!;
            }
        }
        catch { }
        return v;
    }

    /// <summary>Replaces {{param}} in every string. A string that is exactly one
    /// placeholder takes the parameter's own type (so numbers stay numbers).</summary>
    private static JsonNode? Substitute(JsonNode? n, Dictionary<string, JsonNode?> values)
    {
        switch (n)
        {
            case null: return null;
            case JsonObject o:
                var no = new JsonObject();
                foreach (var kv in o) no[kv.Key] = Substitute(kv.Value, values);
                return no;
            case JsonArray a:
                var na = new JsonArray();
                foreach (var e in a) na.Add(Substitute(e, values));
                return na;
            case JsonValue v when v.TryGetValue<string>(out var s):
                var whole = WholePlaceholderRx.Match(s);
                if (whole.Success && values.TryGetValue(whole.Groups[1].Value, out var wv) && wv is not null)
                    return wv.DeepClone();
                return JsonValue.Create(PlaceholderRx.Replace(s, m =>
                    values.TryGetValue(m.Groups[1].Value, out var rv) && rv is not null
                        ? (rv is JsonValue jv && jv.TryGetValue<string>(out var str) ? str : rv.ToJsonString())
                        : m.Value));
            default:
                return n.DeepClone();
        }
    }
}

// ======================================================================
// A flow exposed as an MCP tool.
// ======================================================================

internal sealed class FlowFunction : AIFunction
{
    private readonly FlowService _svc;
    private readonly string _name;
    private readonly string _description;
    private readonly JsonElement _schema;

    public FlowFunction(FlowService svc, FlowDef def)
    {
        _svc = svc;
        _name = def.Name; // re-read from disk on each call so edits apply immediately
        _description = DescribeFor(def);
        var props = new JsonObject();
        var req = new JsonArray();
        foreach (var p in def.Parameters)
        {
            var po = new JsonObject { ["type"] = p.Type };
            if (!string.IsNullOrWhiteSpace(p.Description)) po["description"] = p.Description;
            if (p.Default is not null) po["default"] = p.Default.DeepClone();
            props[p.Name] = po;
            if (p.Required && p.Default is null) req.Add(p.Name);
        }
        var schema = new JsonObject { ["type"] = "object", ["properties"] = props, ["additionalProperties"] = false };
        if (req.Count > 0) schema["required"] = req;
        _schema = JsonSerializer.SerializeToElement(schema);
    }

    public static string DescribeFor(FlowDef f)
        => (string.IsNullOrWhiteSpace(f.Description) ? $"Saved BrAIn flow '{f.Name}'." : f.Description.Trim())
           + $" (Replays {f.Steps.Count} recorded browser steps; edit in about:flows.)";

    public override string Name => FlowService.ToolPrefix + _name;
    public override string Description => _description;
    public override JsonElement JsonSchema => _schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var def = _svc.Get(_name);
        if (def is null) return $"Flow '{_name}' no longer exists.";
        var input = new JsonObject();
        foreach (var kv in arguments)
            input[kv.Key] = kv.Value is null ? null : JsonSerializer.SerializeToNode(kv.Value);
        var res = await _svc.RunAsync(def, input, cancellationToken);
        if (!res.Ok) throw new McpException(res.Text); // McpException messages reach the agent; other exceptions are masked by the SDK
        return res.Text;
    }
}

// ======================================================================
// Agent-facing tools.
// ======================================================================

[McpServerToolType]
public sealed class FlowTools(FlowService flows, ActivityLog log)
{
    [McpServerTool(Name = "browser_activity"),
     Description("Recent MCP tool calls in this browser, newest last, each with an id. Use the ids with browser_save_flow to turn a task you just completed into a reusable flow.")]
    public Task<string> Activity(
        [Description("How many recent calls (default 30, max 200)")] int last = 30,
        [Description("Hide read-only calls like snapshots and screenshots (default true)")] bool onlyActions = true)
    {
        var rows = log.Recent(Math.Clamp(last, 1, 200) * (onlyActions ? 4 : 1))
            .Where(e => !onlyActions || !ActivityLog.ReadOnlyTools.Contains(e.Tool))
            .TakeLast(Math.Clamp(last, 1, 200))
            .ToList();
        if (rows.Count == 0) return Task.FromResult("(no calls recorded yet)");
        return Task.FromResult(string.Join('\n', rows.Select(e =>
            $"{e.Id}  {DateTimeOffset.FromUnixTimeMilliseconds(e.T).LocalDateTime:HH:mm:ss}  {(e.Ok ? "ok " : "ERR")}  {e.Tool}  " +
            ActivityLog.Clip(ShortArgs(e), 120))));
    }

    [McpServerTool(Name = "browser_list_flows"),
     Description("Saved flows (reusable automations the user approved). Each is also available as its own flow_* tool.")]
    public Task<string> ListFlows()
    {
        var list = flows.List();
        var pending = flows.Pending();
        var sb = new StringBuilder();
        if (list.Count == 0) sb.AppendLine("(no saved flows)");
        foreach (var f in list)
            sb.AppendLine($"flow_{f.Name}({string.Join(", ", f.Parameters.Select(p => p.Name))}) - {f.Description} [{f.Steps.Count} steps]");
        if (pending.Count > 0) sb.AppendLine($"\n{pending.Count} proposal(s) waiting for the user's approval.");
        return Task.FromResult(sb.ToString().TrimEnd());
    }

    [McpServerTool(Name = "browser_run_flow"),
     Description("Run a saved flow by name with the given arguments. Same as calling its flow_* tool; use this when the flow_* tool is not in your tool list yet (some clients only refresh the list on reconnect).")]
    public async Task<string> RunFlow(
        [Description("Flow name from browser_list_flows (with or without the flow_ prefix)")] string name,
        [Description("Arguments as a JSON object, e.g. {\"query\":\"red shoes\"}")] string? argsJson = null,
        CancellationToken ct = default)
    {
        var n = FlowService.Normalize(name.StartsWith(FlowService.ToolPrefix, StringComparison.Ordinal) ? name[FlowService.ToolPrefix.Length..] : name);
        var def = flows.Get(n);
        if (def is null) return $"No saved flow named '{n}'. Use browser_list_flows.";
        JsonObject? input = null;
        if (!string.IsNullOrWhiteSpace(argsJson))
        {
            try { input = JsonNode.Parse(argsJson) as JsonObject; }
            catch (JsonException ex) { return "argsJson is not valid JSON: " + ex.Message; }
        }
        var res = await flows.RunAsync(def, input, ct);
        return res.Ok ? res.Text : "FAILED: " + res.Text;
    }

    [McpServerTool(Name = "browser_save_flow"),
     Description("Propose a reusable flow built from steps you just performed, so the same task can be repeated later with different arguments. " +
                 "It is NOT activated: the user reviews, edits and approves it in BrAIn (menu > Flows & activity) before it becomes a callable flow_* tool. " +
                 "Typical use: after finishing a task, call browser_activity to see call ids, then browser_save_flow with those callIds " +
                 "(or lastCalls) and a 'parameters' entry for every value that should vary, giving the literal value you used as 'example'. " +
                 "Read-only calls and failed calls are dropped automatically; passwords are never stored.")]
    public Task<string> SaveFlow(
        [Description("Short snake_case name, e.g. 'submit_expense'. The tool will be called flow_<name>.")] string name,
        [Description("One or two sentences telling a future agent what this flow does and when to use it.")] string description,
        [Description("Values that should become arguments of the flow.")] List<FlowParamSpec>? parameters = null,
        [Description("Ids from browser_activity to include, in order. Preferred over lastCalls.")] string[]? callIds = null,
        [Description("Alternatively: include the most recent N logged calls.")] int lastCalls = 0,
        [Description("Advanced: hand-written steps as a JSON array of {tool,args,alts?}. Overrides callIds/lastCalls.")] string? stepsJson = null,
        [Description("Optional CSS selector whose text the flow returns when it finishes (e.g. a confirmation message).")] string? returns = null)
    {
        var norm = FlowService.Normalize(name);
        if (norm.Length < 2) return Task.FromResult("Error: give the flow a name like 'submit_expense'.");

        FlowDef def;
        if (!string.IsNullOrWhiteSpace(stepsJson))
        {
            List<FlowStep>? steps;
            try { steps = JsonSerializer.Deserialize<List<FlowStep>>(stepsJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
            catch (JsonException ex) { return Task.FromResult("Error: stepsJson is not valid JSON: " + ex.Message); }
            def = new FlowDef { Name = norm, Description = description, Steps = steps ?? new(), Source = "agent" };
            foreach (var spec in parameters ?? new())
                def.Parameters.Add(new FlowParam
                {
                    Name = FlowService.Normalize(spec.Name), Description = spec.Description,
                    Type = spec.Type is "number" or "boolean" ? spec.Type : "string",
                });
        }
        else
        {
            List<ActivityEntry> entries;
            if (callIds is { Length: > 0 }) entries = log.Find(callIds);
            else if (lastCalls > 0) entries = log.Recent(Math.Clamp(lastCalls, 1, 200));
            else return Task.FromResult("Error: pass callIds (from browser_activity), lastCalls, or stepsJson.");
            def = flows.BuildFromActivity(entries, norm, description, parameters);
            def.Source = "agent";
        }
        def.Returns = string.IsNullOrWhiteSpace(returns) ? null : returns;

        var (err, id) = flows.Propose(def);
        if (err is not null) return Task.FromResult("Error: " + err);
        var secrets = def.Parameters.Count(p => p.Name.StartsWith("secret_", StringComparison.Ordinal));
        return Task.FromResult(
            $"Proposal {id} for flow_{def.Name} saved with {def.Steps.Count} steps and {def.Parameters.Count} argument(s)" +
            (secrets > 0 ? $" ({secrets} of them are secrets the caller must supply; none were stored)" : "") + ". " +
            "It is not active yet. Tell the user to open the BrAIn menu (⋮) > Flows & activity > Pending to review and approve it.");
    }

    private static string ShortArgs(ActivityEntry e)
    {
        if (e.Args is null) return "";
        var parts = new List<string>();
        foreach (var kv in e.Args)
        {
            if (kv.Key == "tab") continue;
            var v = kv.Value?.ToString() ?? "";
            if (kv.Key == "element" && e.Loc?.Sel is { } s) v = s;
            parts.Add($"{kv.Key}={ActivityLog.Clip(v, 60)}");
        }
        return string.Join(' ', parts);
    }
}
