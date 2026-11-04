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
        /// <summary>Where an "allow this site" answer is remembered. Sensitive actions use their own key (host + kind), so allowing a
        /// purchase on a site never also allows reading its cookies or uploading files to it.</summary>
        public string Key = "";
        /// <summary>Only "allow once" is offered: a file upload or a clipboard read is never allowed for ten minutes at a time.</summary>
        public bool Once;
        /// <summary>Set for the "where may agents upload files from" question: the folders the user can pick (full paths, broadest first)
        /// and a line saying why it is being asked. The card shows them; an answer names one by position, never by text.</summary>
        public List<string>? UpFolders;
        public string UpNote = "";
        public TaskCompletionSource<string> Answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly string _file;
    private readonly object _lock = new();
    private readonly List<Pending> _pending = new();
    private readonly Dictionary<string, DateTime> _siteUntil = new(StringComparer.OrdinalIgnoreCase);
    private volatile string _mode = ModeAsk;
    private volatile bool _strict;
    private volatile string _uploads = "";        // "" = not chosen yet, "anywhere", or "folder" (then _uploadFolder)
    private volatile string _uploadFolder = "";

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
            if (File.Exists(_file) && JsonNode.Parse(File.ReadAllText(_file)) is JsonObject so)
            {
                if (so["strict"]?.GetValue<bool>() == true) _strict = true;
                var up = so["uploads"]?.GetValue<string>();
                var upf = so["uploadFolder"]?.GetValue<string>() ?? "";
                if (up == "anywhere") _uploads = "anywhere";
                else if (up == "folder" && upf.Length > 0) { _uploads = "folder"; _uploadFolder = upf; }
            }
        }
        catch { /* default: ask */ }
    }

    public string Mode => _mode;

    /// <summary>Opt-in (<c>"strict": true</c> in belt.json, no switch in the UI): uploads, clipboard reads, cookie listing, local
    /// file: addresses and every acting script wait for the user too. Off by default, because an agent doing ordinary work — attaching
    /// a file, opening a local page — would ask all day. The credential-file refusals do not depend on it.</summary>
    public bool Strict => _strict;

    public bool HasPending { get { lock (_lock) return _pending.Count > 0; } }

    public void SetMode(string mode)
    {
        _mode = mode == ModeAuto ? ModeAuto : ModeAsk;
        Save();
        Changed?.Invoke();
    }

    private void Save()
    {
        try { File.WriteAllText(_file, JsonSerializer.Serialize(new { mode = _mode, strict = _strict, uploads = _uploads, uploadFolder = _uploadFolder })); } catch { }
    }

    /// <summary>Where agents may upload files from: "" = not decided (the next upload asks), "anywhere", or "folder".</summary>
    public (string Scope, string Folder) UploadScope => (_uploads, _uploadFolder);

    /// <summary>Forget the choice: the next upload asks again.</summary>
    public void ResetUploadScope()
    {
        _uploads = ""; _uploadFolder = "";
        Save();
        Changed?.Invoke();
    }

    /// <summary>Should this call be checked at all?</summary>
    public bool Gates(string tool, JsonObject args)
    {
        if (_mode != ModeAsk || !Gated.Contains(tool)) return false;
        if (tool.Equals("browser_press_key", StringComparison.OrdinalIgnoreCase))
        {
            var k = args["key"]?.ToString() ?? "";
            // Space presses a focused button or link exactly as Enter does
            return k.Equals("Enter", StringComparison.OrdinalIgnoreCase) || k.Equals("NumpadEnter", StringComparison.OrdinalIgnoreCase)
                || k is " " || k.Equals("Space", StringComparison.OrdinalIgnoreCase) || k.Equals("Spacebar", StringComparison.OrdinalIgnoreCase);
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

    /// <summary>What an agent is asking for when it is not a click: <paramref name="Why"/> and <paramref name="Label"/> go on the card,
    /// <paramref name="Category"/> keeps "allow this site" for one kind of action from covering another, <paramref name="Once"/>
    /// removes the ten-minute option.</summary>
    public sealed record SensitiveAsk(string Why, string Label, string Category, bool Once);

    /// <summary>Scripts that act on the page or send something out: the same effect as the clicks above, reached another way.</summary>
    private static readonly Regex ActingScript = new(
        @"\.(submit|requestSubmit|click)\s*\(|\bfetch\s*\(|XMLHttpRequest|sendBeacon|new\s+(WebSocket|EventSource)\b|\bwindow\.open\b|\b(window\.|document\.|top\.|self\.)?location\s*(\.(href|assign|replace)\s*(=|\())|\blocation\s*=[^=]|navigator\.clipboard",
        Rx);

    private static readonly Regex HttpPostMethod = new(@"\bPOST\b|(?i:method\s*[:=]\s*['""`]post['""`])", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// The non-click calls that still ask the user (not in "auto", not in full control). Null = nothing to ask.
    /// By default that is deliberately little, so everyday agent work runs without questions:
    ///   - showing the REAL tokens/cookies/passwords of network traffic (the agent has to ask for them with revealSecrets), and
    ///   - a script that clicks, submits or sends AND names a payment/deletion/sending/consent word — the same standard the click
    ///     gate applies, so a script is no way round "ask before buying".
    /// In <paramref name="strict"/> mode (belt.json) uploads, clipboard reads, cookie listing, file: addresses and every acting script ask too.
    /// What does not depend on asking: credential files are refused in every mode (<see cref="HardRefusal"/>, <see cref="PathGuard"/>),
    /// saved files stay in Downloads, and agent-facing network output is redacted.
    /// </summary>
    public static SensitiveAsk? ClassifySensitive(string tool, JsonObject args, bool strict = false)
    {
        string Arg(string k) => args[k]?.ToString() ?? "";
        switch (tool.ToLowerInvariant())
        {
            case "browser_network_request":
            case "browser_network_requests":
            case "browser_network_har":
                return Arg("revealSecrets").Equals("true", StringComparison.OrdinalIgnoreCase)
                    ? new("shows the login tokens, cookies and passwords in this page's network traffic", "", "secrets", true)
                    : null;
            case "browser_evaluate":
            {
                var script = Arg("script");
                if (!ActingScript.IsMatch(script)) return null;
                if (strict) return new("runs a script that clicks, submits a form, navigates or sends requests", Clip(script, 70), "script", false);
                // "POST" in a script is the HTTP method, not the "Post" button: it must not make every request ask
                var text = HttpPostMethod.Replace(script, " ");
                foreach (var (why, re) in Words)
                    if (re.IsMatch(text)) return new($"runs a script that acts on the page and {why}", Clip(script, 70), "script", false);
                return null;
            }
        }
        if (!strict) return null;
        switch (tool.ToLowerInvariant())
        {
            case "browser_upload":
                return new("uploads a file from your computer to this page", Path.GetFileName(Arg("path")), "files", true);
            case "browser_clipboard":
                return Arg("action").Equals("write", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : new("reads your clipboard (it may hold something you copied for another purpose, like a password)", "", "clipboard", true);
            case "browser_cookies":
                return new("reads or changes this site's cookies, which include its login sessions", Arg("action").Length > 0 ? Arg("action") : "list", "cookies", false);
            case "browser_navigate":
            case "browser_new_tab":
            {
                var url = Arg("url").Trim();
                return url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                    ? new("opens a file from your computer in the browser, where the agent can read it", Path.GetFileName(Uri.UnescapeDataString(url)), "files", true)
                    : null;
            }
            default:
                return null;
        }
    }

    /// <summary>Calls that must be refused outright, in every mode, without asking anybody: handing credential or key files to a page, or
    /// opening them as an address. Returns the refusal text, or null.</summary>
    public static string? HardRefusal(string tool, JsonObject args, string storeDir)
    {
        string? target = tool.ToLowerInvariant() switch
        {
            "browser_upload" => args["path"]?.ToString(),
            "browser_navigate" or "browser_new_tab" when (args["url"]?.ToString() ?? "").TrimStart().StartsWith("file:", StringComparison.OrdinalIgnoreCase) => args["url"]?.ToString()?.Trim(),
            _ => null,
        };
        return PathGuard.Sensitive(target, storeDir) is { } why ? PathGuard.Refusal(why) : null;
    }

    private static string Clip(string s, int n)
    {
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s.Length <= n ? s : s[..n] + "…";
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

        return await AskCoreAsync(tool, host, (facts?.Name ?? loc?.Name ?? "").Trim(), why, url ?? "", ct);
    }

    /// <summary>
    /// Asks the user before something that is not a click: a call to a learned site API that changes data or
    /// touches something sensitive. Same card, same answers (Allow once / Allow this site 10 min / Deny).
    /// Returns null when the call may proceed, otherwise the refusal text for the agent.
    /// </summary>
    public async Task<string?> AskAsync(string tool, string host, string label, string why, CancellationToken ct, string? category = null, bool once = false)
    {
        if (_mode != ModeAsk) return null;
        host = (host ?? "").ToLowerInvariant();
        var key = category is null || host.Length == 0 ? host : host + "!" + category;
        lock (_lock)
        {
            if (!once && key.Length > 0 && _siteUntil.TryGetValue(key, out var until))
            {
                if (until > DateTime.UtcNow) return null;
                _siteUntil.Remove(key);
            }
        }
        return await AskCoreAsync(tool, host, label, why, "", ct, key, once);
    }

    private async Task<string?> AskCoreAsync(string tool, string host, string label, string why, string url, CancellationToken ct, string? key = null, bool once = false)
    {
        var p = new Pending
        {
            Key = key ?? host,
            Once = once,
            Id = Guid.NewGuid().ToString("N"),
            Tool = tool,
            Host = host,
            Label = label,
            Why = why,
            Url = url,
        };
        var answer = await WaitAsync(p, ct);
        if (answer == "once") return null;
        if (answer == "site")
        {
            if (!p.Once && p.Key.Length > 0) lock (_lock) _siteUntil[p.Key] = DateTime.UtcNow + SiteGrant;
            return null;
        }
        return NotAllowed(answer, p);
    }

    /// <summary>Shows the question and waits for the user's click (or the timeout).</summary>
    private async Task<string> WaitAsync(Pending p, CancellationToken ct)
    {
        lock (_lock) _pending.Add(p);
        Changed?.Invoke();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(AnswerTimeout);
            using (cts.Token.Register(() => p.Answer.TrySetResult("timeout")))
                return await p.Answer.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_lock) _pending.Remove(p);
            Changed?.Invoke();
        }
    }

    private static string NotAllowed(string answer, Pending p)
    {
        var what = p.Label.Length > 0 ? $"'{p.Label}'" : "that element";
        return answer == "timeout"
            ? $"NOT APPROVED: the user did not answer in BrAIn in time, so the action on {what} ({p.Why}) was not performed. Do not retry or find another route; tell the user what you wanted to do and ask them to try again."
            : $"DENIED: the user declined the action on {what} ({p.Why}) in BrAIn. Do not retry or find another route; tell the user and ask what they want instead.";
    }

    /// <summary>Folders the user is never offered as an upload root: they hold other people's or other volumes' files.</summary>
    private static readonly string[] BroadFolders = { "/Volumes", "/media", "/mnt", "/run/media", "/Users", "/home" };

    /// <summary>The folders above <paramref name="realFile"/> that can be picked as "uploads only from here", broadest first. Never the
    /// root, the home folder itself, or anything that contains it (those are "anywhere" under another name).</summary>
    internal static List<string> FolderChoices(string realFile, string home)
    {
        var res = new List<string>();
        var homeN = home.Replace('\\', '/').TrimEnd('/');
        var dir = Path.GetDirectoryName(realFile);
        while (!string.IsNullOrEmpty(dir))
        {
            var d = dir.Replace('\\', '/').TrimEnd('/');
            if (d.Length == 0 || Path.GetPathRoot(dir) == dir) break;
            if (homeN.Length > 0 && (homeN.Equals(d, StringComparison.OrdinalIgnoreCase) || homeN.StartsWith(d + "/", StringComparison.OrdinalIgnoreCase))) break;
            if (BroadFolders.Contains(d, StringComparer.OrdinalIgnoreCase)) break;
            res.Add(dir);
            dir = Path.GetDirectoryName(dir);
        }
        res.Reverse();
        // the card has room for five rows: keep the broadest two (the user's "Documents") and the three nearest the file
        if (res.Count > 5) res = res.GetRange(0, 2).Concat(res.GetRange(res.Count - 3, 3)).ToList();
        return res;
    }

    /// <summary>A folder as the user would say it: the home folder written as ~.</summary>
    internal static string Short(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('/', '\\');
        return home.Length > 1 && path.StartsWith(home + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? "~" + path[home.Length..] : path;
    }

    /// <summary>
    /// Where may an AI agent upload files from? Asked once, the first time an agent uploads (ordinary files only: credential files
    /// are refused before this is reached): "only from this folder" (pick how far up), "anywhere", or just this once. Once chosen,
    /// uploads inside the folder (or anywhere) run without a question; a file outside the chosen folder asks again, with the same
    /// answers, so the choice can be widened or moved on the spot. Returns null to proceed, otherwise the refusal text.
    /// </summary>
    public async Task<string?> UploadScopeAsync(string path, string host, CancellationToken ct)
    {
        if (_mode != ModeAsk) return null;
        string real;
        try { real = PathGuard.Real(path); } catch { return null; }
        var scope = _uploads; var folder = _uploadFolder;
        if (scope == "anywhere") return null;
        if (scope == "folder" && PathGuard.IsInside(real, folder)) return null;

        var choices = FolderChoices(real, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var p = new Pending
        {
            Id = Guid.NewGuid().ToString("N"),
            Tool = "browser_upload",
            Host = (host ?? "").ToLowerInvariant(),
            Label = Path.GetFileName(real),
            Why = "uploads a file from your computer to this page",
            Once = true,
            UpFolders = choices,
            UpNote = scope == "folder"
                ? $"AI agents may upload only from {Short(folder)}, and this file is outside it. “Anywhere” lets an agent that a web page has tricked send any of your files (but not passwords or keys) to a website."
                : "Choose where AI agents may upload files from. “Anywhere” lets an agent that a web page has tricked send any of your files (but not passwords or keys) to a website.",
        };
        var answer = await WaitAsync(p, ct);
        if (answer == "once") return null;
        if (answer == "anywhere") { _uploads = "anywhere"; _uploadFolder = ""; Save(); Changed?.Invoke(); return null; }
        if (answer.StartsWith("folder:", StringComparison.Ordinal) && int.TryParse(answer[7..], out var i) && i >= 0 && i < choices.Count)
        {
            _uploads = "folder"; _uploadFolder = choices[i]; Save(); Changed?.Invoke();
            return null;
        }
        return NotAllowed(answer, p);
    }

    /// <summary>While a question is open, other actions wait their turn instead of racing it.</summary>
    public const string BusyText = "WAIT: BrAIn is asking the user to approve another action. Do not retry yet; wait for the user's answer.";

    /// <summary>Declines every open question at once (the user cancelled the agent).</summary>
    public void DenyAll()
    {
        List<Pending> all;
        lock (_lock) all = _pending.ToList();
        foreach (var p in all) p.Answer.TrySetResult("deny");
    }

    /// <summary>Called from the UI. Unknown or stale ids, and answers the question does not offer, are ignored.</summary>
    public void Answer(string idAndChoice)
    {
        var i = idAndChoice.IndexOf(':');
        if (i <= 0) return;
        var id = idAndChoice[..i];
        var choice = idAndChoice[(i + 1)..];
        Pending? p;
        lock (_lock) p = _pending.FirstOrDefault(x => x.Id == id);
        if (p is null) return;
        var upload = p.UpFolders is not null;
        var ok = choice is "once" or "deny"
                 || (choice == "site" && !upload)
                 || (upload && (choice == "anywhere" || (choice.StartsWith("folder:", StringComparison.Ordinal) && int.TryParse(choice[7..], out var n) && n >= 0 && n < p.UpFolders!.Count)));
        if (ok) p.Answer.TrySetResult(choice);
    }

    /// <summary>The push payload for the UI. The id is only ever sent to the UI, never logged.</summary>
    public object View()
    {
        lock (_lock)
            return new
            {
                mode = _mode,
                uploads = new { scope = _uploads, folder = _uploadFolder.Length > 0 ? Short(_uploadFolder) : "" },
                ask = _pending.Select(p => new
                {
                    id = p.Id, tool = p.Tool, host = p.Host, label = p.Label, why = p.Why, once = p.Once,
                    up = p.UpFolders is null ? null : new { folders = p.UpFolders.Select(Short).ToArray(), note = p.UpNote },
                }).ToArray(),
            };
    }

    private static string HostOf(string? factsHost, string? url)
    {
        if (!string.IsNullOrEmpty(factsHost)) return factsHost!.ToLowerInvariant();
        try { if (!string.IsNullOrEmpty(url)) return new Uri(url).Host.ToLowerInvariant(); } catch { }
        return "";
    }
}
