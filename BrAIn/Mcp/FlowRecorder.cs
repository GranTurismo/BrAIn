using System.Text.Json;
using System.Text.Json.Nodes;

namespace BrAIn.Mcp;

/// <summary>
/// "Show it once": collects what the person does in the page (relayed by brain.js as
/// {b:31}) and turns it into the same call log the agent produces, so the existing
/// flow builder, approval queue and replay do the rest. Secret fields never leave the
/// page: brain.js sends no value for them, only a mask.
/// </summary>
public sealed class FlowRecorder
{
    public const int MaxSteps = 300;

    public sealed class RecStep
    {
        public string Kind = "";      // click | type | select | check | key
        public string Label = "";     // what the person sees in the step list
        public string? Value;         // typed / chosen text (null when masked)
        public bool Masked;
        public string Tool = "";
        public JsonObject Args = new();
        public ActivityLocator? Loc;
        public string? Url;
    }

    private readonly object _lock = new();
    private readonly List<RecStep> _steps = new();
    private bool _on;
    private Guid _tabId;
    private string _startUrl = "";
    private string _title = "";

    /// <summary>Raised when recording starts/stops or a step arrives (any thread).</summary>
    public event Action? Changed;

    public bool Active { get { lock (_lock) return _on; } }
    public Guid TabId { get { lock (_lock) return _tabId; } }

    public void Start(Guid tabId, string url, string title)
    {
        lock (_lock)
        {
            _steps.Clear();
            _on = true; _tabId = tabId; _startUrl = url; _title = title ?? "";
        }
        Changed?.Invoke();
    }

    public void Discard()
    {
        lock (_lock) { _steps.Clear(); _on = false; }
        Changed?.Invoke();
    }

    /// <summary>Handles one {"b":31,"ev":{...}} message. Ignored unless recording.</summary>
    public bool Accept(string message)
    {
        if (!Active) return false;
        try
        {
            using var doc = JsonDocument.Parse(message);
            if (!doc.RootElement.TryGetProperty("ev", out var ev) || ev.ValueKind != JsonValueKind.Object) return false;
            var kind = ev.TryGetProperty("k", out var k) ? k.GetString() ?? "" : "";
            ActivityLocator? loc = null;
            if (ev.TryGetProperty("loc", out var l) && l.ValueKind == JsonValueKind.Object)
                loc = JsonSerializer.Deserialize<ActivityLocator>(l.GetRawText());
            var url = ev.TryGetProperty("url", out var u) ? u.GetString() : null;
            var masked = ev.TryGetProperty("masked", out var m) && m.ValueKind == JsonValueKind.True;
            var value = ev.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var name = Clip(loc?.Name ?? loc?.Tag ?? "element", 50);
            var s = new RecStep { Kind = kind, Loc = loc, Url = url, Masked = masked };

            if (kind == "key")
            {
                var key = ev.TryGetProperty("key", out var kk) ? kk.GetString() : null;
                if (key != "Enter") return false;
                s.Tool = "browser_press_key"; s.Args["key"] = "Enter"; s.Label = "Press Enter";
            }
            else
            {
                if (string.IsNullOrEmpty(loc?.Sel)) return false;
                s.Args["element"] = loc!.Sel;
                switch (kind)
                {
                    case "click":
                        s.Tool = "browser_click"; s.Label = $"Click “{name}”"; break;
                    case "type":
                        s.Tool = "browser_type";
                        s.Masked = masked || loc.Sensitive;
                        s.Value = s.Masked ? null : value ?? "";
                        s.Args["text"] = s.Masked ? ActivityLog.MaskText : (value ?? "");
                        s.Label = s.Masked ? $"Type a secret into {name}" : $"Type into {name}";
                        break;
                    case "select":
                        {
                            var vals = new JsonArray();
                            if (ev.TryGetProperty("values", out var vs) && vs.ValueKind == JsonValueKind.Array)
                                foreach (var x in vs.EnumerateArray()) vals.Add(x.GetString() ?? "");
                            if (vals.Count == 0) return false;
                            s.Tool = "browser_select_option"; s.Args["values"] = vals;
                            s.Value = vals.Count == 1 ? vals[0]?.ToString() : null;
                            s.Label = $"Choose in {name}";
                            break;
                        }
                    case "check":
                        {
                            var on = ev.TryGetProperty("checked", out var c) && c.ValueKind == JsonValueKind.True;
                            s.Tool = "browser_set_checked"; s.Args["check"] = on;
                            s.Label = (on ? "Check " : "Uncheck ") + $"“{name}”";
                            break;
                        }
                    default: return false;
                }
            }

            lock (_lock)
            {
                if (_steps.Count >= MaxSteps) return false;
                // typing again into the same field replaces the earlier entry
                if (kind == "type" && _steps.Count > 0 && _steps[^1] is { Kind: "type" } prev && prev.Loc?.Sel == loc?.Sel)
                    _steps[^1] = s;
                else _steps.Add(s);
            }
            Changed?.Invoke();
            return true;
        }
        catch { return false; }
    }

    /// <summary>Snapshot for the UI. Masked values are never included.</summary>
    public object View()
    {
        lock (_lock)
            return new
            {
                on = _on,
                n = _steps.Count,
                title = _title,
                url = _startUrl,
                steps = _steps.Select((s, i) => new { i, kind = s.Kind, label = s.Label, value = s.Value, masked = s.Masked }).ToArray(),
            };
    }

    /// <summary>Stops recording and returns the steps as a call log for FlowService.BuildFromActivity.</summary>
    public List<ActivityEntry> TakeEntries()
    {
        List<RecStep> steps; string url;
        lock (_lock)
        {
            steps = new List<RecStep>(_steps); url = _startUrl;
            _steps.Clear(); _on = false;
        }
        Changed?.Invoke();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var list = new List<ActivityEntry>();
        if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            list.Add(new ActivityEntry { Id = "r0", T = now, Tool = "browser_navigate", Args = new JsonObject { ["url"] = url }, Ok = true, Url = url });
        var i = 1;
        foreach (var s in steps)
            list.Add(new ActivityEntry
            {
                Id = "r" + i, T = now + i++, Tool = s.Tool, Args = (JsonObject)s.Args.DeepClone(),
                Loc = s.Loc, Ok = true, Url = s.Url, Masked = s.Masked,
            });
        return list;
    }

    private static string Clip(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
