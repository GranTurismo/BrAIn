using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BrAIn.Mcp;

/// <summary>What brain.js saw about the element a click/Enter would hit (see risk() in brain.js).</summary>
public sealed class BeltFacts
{
    [JsonPropertyName("tag")] public string? Tag { get; set; }
    [JsonPropertyName("role")] public string? Role { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    /// <summary>Clicking submits a form.</summary>
    [JsonPropertyName("submit")] public bool Submit { get; set; }
    [JsonPropertyName("post")] public bool Post { get; set; }
    /// <summary>The form has card-number style fields.</summary>
    [JsonPropertyName("card")] public bool Card { get; set; }
    [JsonPropertyName("host")] public string? Host { get; set; }
}

/// <summary>
/// "Ask before risky actions". The MCP request filter calls <see cref="GateAsync"/>
/// before a click or Enter reaches the page. Anything that looks like paying,
/// deleting, sending or consenting waits for the user's answer in the browser
/// (pushed as {b:47}); no answer, or Deny, refuses the call. Reads never wait.
/// Saved flows are user-approved, so they are not gated step by step.
/// </summary>
public sealed class Seatbelt
{
    public const string ModeAsk = "ask";
    public const string ModeAuto = "auto";
    public static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan SiteGrant = TimeSpan.FromMinutes(10);

    private static readonly HashSet<string> Gated = new(StringComparer.OrdinalIgnoreCase)
    {
        "browser_click", "browser_double_click", "browser_click_at", "browser_press_key",
    };

    private const RegexOptions Rx = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    // Order matters: the first match names the reason shown to the user.
    private static readonly (string Why, Regex Re)[] Words =
    {
        ("looks like a payment or purchase", new(@"\b(buy|purchase|pay|payment|place\s+(your\s+)?order|order\s+now|checkout|check\s+out|complete\s+(order|purchase)|donate|transfer|withdraw|deposit|subscribe|upgrade|купить|оплатить|оформить\s+заказ|გადახდა|ყიდვა|შეძენა|შეკვეთ\w*)\b", Rx)),
        ("looks like a deletion", new(@"\b(delete|erase|remove\s+account|close\s+account|deactivate|terminate|cancel\s+(my\s+)?(subscription|account|plan|order)|unlink|revoke|удалить|წაშლა|გაუქმება)\b", Rx)),
        ("looks like sending or publishing", new(@"\b(send|post|publish|tweet|reply|submit|отправить|опубликовать|გაგზავნა|გამოქვეყნება)\b", Rx)),
        ("looks like agreeing or authorizing", new(@"\b(i\s+agree|agree\s+(and|&)\s+\w+|authorize|grant\s+access|accept\s+(and|&)\s+\w+|confirm|დათანხმება|დადასტურება|подтвердить)\b", Rx)),
    };
    private static readonly Regex CheckoutPath = new(@"/(checkout|payment|billing|pay|order|cart/confirm)\b", Rx);

    private sealed class Pending
    {
        public string Id = "";
        public string Tool = "";
        public string Host = "";
        public string Label = "";
        public string Why = "";
        public string Url = "";
        public TaskCompletionSource<string> Answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly string _file;
    private readonly object _lock = new();
    private readonly List<Pending> _pending = new();
    private readonly Dictionary<string, DateTime> _siteUntil = new(StringComparer.OrdinalIgnoreCase);
    private volatile string _mode = ModeAsk;

    /// <summary>Raised when the mode or the pending list changes (any thread).</summary>
    public event Action? Changed;

    public Seatbelt(string storeDir)
    {
        _file = Path.Combine(storeDir, "belt.json");
        try
        {
            if (File.Exists(_file) && JsonNode.Parse(File.ReadAllText(_file)) is JsonObject o
                && o["mode"]?.GetValue<string>() == ModeAuto)
                _mode = ModeAuto;
        }
        catch { /* default: ask */ }
    }

    public string Mode => _mode;

    public bool HasPending { get { lock (_lock) return _pending.Count > 0; } }

    public void SetMode(string mode)
    {
        _mode = mode == ModeAuto ? ModeAuto : ModeAsk;
        try { File.WriteAllText(_file, JsonSerializer.Serialize(new { mode = _mode })); } catch { }
        Changed?.Invoke();
    }

    /// <summary>Should this call be checked at all?</summary>
    public bool Gates(string tool, JsonObject args)
    {
        if (_mode != ModeAsk || !Gated.Contains(tool)) return false;
        if (tool.Equals("browser_press_key", StringComparison.OrdinalIgnoreCase))
        {
            var k = args["key"]?.ToString() ?? "";
            return k.Equals("Enter", StringComparison.OrdinalIgnoreCase) || k.Equals("NumpadEnter", StringComparison.OrdinalIgnoreCase);
        }
        return true;
    }

    /// <summary>Pure classification: null = fine, otherwise the reason to ask.</summary>
    public static string? Classify(string tool, ActivityLocator? loc, BeltFacts? facts, string? url)
    {
        var label = (facts?.Name ?? loc?.Name ?? "").Trim();
        var path = "";
        try { if (!string.IsNullOrEmpty(url)) path = new Uri(url).AbsolutePath; } catch { }

        if (facts is { Submit: true, Card: true }) return "submits a payment-card form";
        var actionable = facts is not null
            ? facts.Submit || facts.Tag is "button" or "a" or "input" || facts.Role is "button" or "link" or "menuitem"
            : loc is not null && (loc.Tag is "button" or "a" or "input" || loc.Role is "button" or "link" or "menuitem");
        if (!actionable || label.Length == 0 || label.Length > 60)
        {
            if (facts is { Submit: true } && CheckoutPath.IsMatch(path)) return "submits a form on a checkout page";
            return null;
        }
        foreach (var (why, re) in Words)
            if (re.IsMatch(label)) return why;
        if (facts is { Submit: true } && CheckoutPath.IsMatch(path)) return "submits a form on a checkout page";
        return null;
    }

    /// <summary>
    /// Returns null when the call may proceed, or the refusal text to hand back to the agent.
    /// </summary>
    public async Task<string?> GateAsync(string tool, JsonObject args, ActivityLocator? loc, BeltFacts? facts, string? url, CancellationToken ct)
    {
        var host = HostOf(facts?.Host, url);
        var why = Classify(tool, loc, facts, url);
        if (why is null) return null;

        lock (_lock)
        {
            if (host.Length > 0 && _siteUntil.TryGetValue(host, out var until))
            {
                if (until > DateTime.UtcNow) return null;
                _siteUntil.Remove(host);
            }
        }

        var p = new Pending
        {
            Id = Guid.NewGuid().ToString("N"),
            Tool = tool,
            Host = host,
            Label = (facts?.Name ?? loc?.Name ?? "").Trim(),
            Why = why,
            Url = url ?? "",
        };
        lock (_lock) _pending.Add(p);
        Changed?.Invoke();

        string answer;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(AnswerTimeout);
            using (cts.Token.Register(() => p.Answer.TrySetResult("timeout")))
                answer = await p.Answer.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_lock) _pending.Remove(p);
            Changed?.Invoke();
        }

        if (answer == "once") return null;
        if (answer == "site")
        {
            if (host.Length > 0) lock (_lock) _siteUntil[host] = DateTime.UtcNow + SiteGrant;
            return null;
        }
        var what = p.Label.Length > 0 ? $"'{p.Label}'" : "that element";
        return answer == "timeout"
            ? $"NOT APPROVED: the user did not answer in BrAIn in time, so the action on {what} ({why}) was not performed. Do not retry or find another route; tell the user what you wanted to do and ask them."
            : $"DENIED: the user declined the action on {what} ({why}) in BrAIn. Do not retry or find another route; tell the user and ask what they want instead.";
    }

    /// <summary>While a question is open, other actions wait their turn instead of racing it.</summary>
    public const string BusyText = "WAIT: BrAIn is asking the user to approve another action. Do not retry yet; wait for the user's answer.";

    /// <summary>Called from the UI. Unknown or stale ids are ignored.</summary>
    public void Answer(string idAndChoice)
    {
        var i = idAndChoice.IndexOf(':');
        if (i <= 0) return;
        var id = idAndChoice[..i];
        var choice = idAndChoice[(i + 1)..];
        if (choice is not ("once" or "site" or "deny")) return;
        Pending? p;
        lock (_lock) p = _pending.FirstOrDefault(x => x.Id == id);
        p?.Answer.TrySetResult(choice);
    }

    /// <summary>The push payload for the UI. The id is only ever sent to the UI, never logged.</summary>
    public object View()
    {
        lock (_lock)
            return new
            {
                mode = _mode,
                ask = _pending.Select(p => new { id = p.Id, tool = p.Tool, host = p.Host, label = p.Label, why = p.Why }).ToArray(),
            };
    }

    private static string HostOf(string? factsHost, string? url)
    {
        if (!string.IsNullOrEmpty(factsHost)) return factsHost!.ToLowerInvariant();
        try { if (!string.IsNullOrEmpty(url)) return new Uri(url).Host.ToLowerInvariant(); } catch { }
        return "";
    }
}
