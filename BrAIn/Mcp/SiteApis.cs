using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BrAIn.Browser;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace BrAIn.Mcp;

// ======================================================================
// "Every website becomes an API" — the service.
//
//   learn      the user lets BrAIn learn a site; pages report the JSON calls their own scripts make
//   compile    ApiCompiler turns those into endpoint drafts (stored one file per site, human-readable)
//   approve    nothing is callable until the user approves it in about:apis
//   call       an approved endpoint is a live MCP tool, api_<site>_<name>; calling it replays the request
//              inside a tab on the site (cookies and CSRF tokens work), after asking the user whenever
//              it changes data
// ======================================================================

public sealed class ExecResult
{
    public bool Ok { get; set; }
    public int Status { get; set; }
    public string ContentType { get; set; } = "";
    public string Text { get; set; } = "";
    public bool Truncated { get; set; }
    public string? Error { get; set; }
    public List<string> Missing { get; set; } = new();
}

public sealed class ApiService
{
    public const string ToolPrefix = "api_";
    private static readonly Regex SiteRx = new(@"^[a-z0-9]([a-z0-9.\-]{0,98}[a-z0-9])?$", RegexOptions.Compiled);

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
    private readonly string _dir;
    private readonly object _lock = new();
    private readonly Dictionary<string, ApiSiteFile> _sites = new(StringComparer.Ordinal);
    private readonly Dictionary<string, McpServerTool> _registered = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dirty = new(StringComparer.Ordinal);
    private McpServerPrimitiveCollection<McpServerTool>? _collection;
    private Timer? _flush;
    private long _windowStart; private int _windowCount;

    /// <summary>Endpoints or sites changed (any thread): the about:apis page refreshes.</summary>
    public event Action? Changed;
    /// <summary>The lists of learned / usable sites changed: page boot scripts must be refreshed.</summary>
    public event Action? ConfigChanged;

    public ApiService(BrowserController browser)
    {
        _browser = browser;
        _dir = Path.Combine(browser.StoreDir, "apis");
        try { Directory.CreateDirectory(_dir); } catch { }
        foreach (var f in Directory.EnumerateFiles(_dir, "*.json"))
        {
            try
            {
                var site = JsonSerializer.Deserialize<ApiSiteFile>(File.ReadAllText(f), FileJson);
                if (site is null || !SiteRx.IsMatch(site.Site ?? "")) continue;
                if (site.Endpoints.Count > ApiCompiler.MaxEndpointsPerSite) site.Endpoints = site.Endpoints.Take(ApiCompiler.MaxEndpointsPerSite).ToList();
                _sites[site.Site!] = site;
            }
            catch { /* a file edited into nonsense is ignored, not fatal */ }
        }
    }

    private void Raise() { try { Changed?.Invoke(); } catch { } }
    private void RaiseConfig() { try { ConfigChanged?.Invoke(); } catch { } }

    // ---------------- what the pages need to know ----------------

    public IReadOnlyList<string> LearnSites() { lock (_lock) return _sites.Values.Where(s => s.Learn).Select(s => s.Site).ToList(); }

    /// <summary>Sites with at least one usable endpoint: their pages keep the live credentials a call needs.</summary>
    public IReadOnlyList<string> UseSites()
    {
        lock (_lock) return _sites.Values.Where(s => s.Enabled && s.Endpoints.Any(e => e.Approved)).Select(s => s.Site).ToList();
    }

    public bool IsLearning(string site) { lock (_lock) return _sites.TryGetValue(site, out var s) && s.Learn; }

    public (int Endpoints, int Drafts, int Approved) Counts(string site)
    {
        lock (_lock)
        {
            if (!_sites.TryGetValue(site, out var s)) return (0, 0, 0);
            return (s.Endpoints.Count, s.Endpoints.Count(e => !e.Approved), s.Endpoints.Count(e => e.Approved));
        }
    }

    public int TotalDrafts() { lock (_lock) return _sites.Values.Sum(s => s.Endpoints.Count(e => !e.Approved)); }

    /// <summary>A copy of everything, for the about:apis page.</summary>
    public List<ApiSiteFile> Sites()
    {
        lock (_lock)
            return JsonSerializer.Deserialize<List<ApiSiteFile>>(JsonSerializer.Serialize(_sites.Values.OrderBy(s => s.Site).ToList(), FileJson), FileJson) ?? new();
    }

    public ApiSiteFile? Site(string site)
    {
        lock (_lock)
            return _sites.TryGetValue(site, out var s) ? JsonSerializer.Deserialize<ApiSiteFile>(JsonSerializer.Serialize(s, FileJson), FileJson) : null;
    }

    // ---------------- learning ----------------

    public static bool ValidSite(string? site) => site is not null && SiteRx.IsMatch(site);

    public string? SetLearn(string site, bool on)
    {
        site = (site ?? "").Trim().ToLowerInvariant();
        if (!ValidSite(site)) return "That is not a site name.";
        lock (_lock)
        {
            if (!_sites.TryGetValue(site, out var s))
            {
                if (!on) return null;
                if (_sites.Count >= 200) return "BrAIn already keeps 200 sites; forget some in about:apis first.";
                s = new ApiSiteFile { Site = site };
                _sites[site] = s;
            }
            if (s.Learn == on) return null;
            s.Learn = on;
            _dirty.Add(site);
        }
        FlushNow();
        RaiseConfig();
        Raise();
        return null;
    }

    /// <summary>Folds one page report into the drafts. Returns the reason it was skipped, or null.</summary>
    public string? Record(ApiRecordIn rec)
    {
        var site = ApiSite.KeyOfUrl(rec.Origin);
        if (site.Length == 0) return "no site";
        lock (_lock)
        {
            // a page can only feed the site it is on, and only while the user lets BrAIn learn it
            if (!_sites.TryGetValue(site, out var s) || !s.Learn) return "not learning this site";
            var now = Environment.TickCount64;
            if (now - _windowStart > 1000) { _windowStart = now; _windowCount = 0; }
            if (++_windowCount > 40) return "too many reports";
            var why = ApiCompiler.Ingest(s, rec, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (why is null) { _dirty.Add(site); ScheduleFlush(); }
            else return why;
        }
        Raise();
        return null;
    }

    /// <summary>Parses the page's report (verb apirec).</summary>
    public static ApiRecordIn? ParseReport(string json)
    {
        try
        {
            var o = JsonNode.Parse(json) as JsonObject;
            if (o is null) return null;
            string? S(string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            var rec = new ApiRecordIn
            {
                Method = S("m") ?? "", Url = S("u") ?? "", Origin = S("o") ?? "", Body = S("b"), BodyType = S("bt"),
                ContentType = S("ct"), Response = S("r"),
                Status = o["s"] is JsonValue sv && sv.TryGetValue<int>(out var st) ? st : 0,
            };
            if (o["h"] is JsonObject h)
                foreach (var kv in h) if (kv.Value is JsonValue hv && hv.TryGetValue<string>(out var hs) && kv.Key.Length < 60) rec.Headers[kv.Key.ToLowerInvariant()] = hs.Length > 200 ? hs[..200] : hs;
            if (o["l"] is JsonArray l)
                foreach (var n in l.Take(30)) if (n is JsonValue nv && nv.TryGetValue<string>(out var ns) && ns.Length < 60) rec.Live.Add(ns.ToLowerInvariant());
            if (rec.Body is { Length: > 30000 }) rec.Body = null;
            if (rec.Response is { Length: > 60000 }) rec.Response = null;
            return rec;
        }
        catch { return null; }
    }

    // ---------------- the user's edits ----------------

    public string? Save(string site, ApiEndpoint edited)
    {
        lock (_lock)
        {
            if (!_sites.TryGetValue(site, out var s)) return "Unknown site.";
            var i = s.Endpoints.FindIndex(e => e.Id == edited.Id);
            if (i < 0) return "That endpoint no longer exists.";
            var old = s.Endpoints[i];
            // only what a person may change; the request itself (host, method, body shape) stays as learned
            old.Name = (edited.Name ?? "").Trim();
            old.Description = (edited.Description ?? "").Trim();
            old.Path = (edited.Path ?? "").Trim();
            foreach (var p in edited.Params)
            {
                var mine = old.Params.FirstOrDefault(x => x.In == p.In && x.Key == p.Key);
                if (mine is null) continue;
                mine.Name = p.Name; mine.Description = p.Description; mine.Required = p.Required;
                if (p.Type is "string" or "number" or "boolean" or "object" or "array") mine.Type = p.Type;
            }
            var err = ApiCompiler.Validate(s, old);
            if (err is not null) { s.Endpoints[i] = old; return err; }
            old.Edited = true;
            _dirty.Add(site);
        }
        FlushNow(); ReloadTools(); Raise();
        return null;
    }

    public string? SetApproved(string site, string id, bool on)
    {
        lock (_lock)
        {
            if (!_sites.TryGetValue(site, out var s)) return "Unknown site.";
            var e = s.Endpoints.FirstOrDefault(x => x.Id == id);
            if (e is null) return "That endpoint no longer exists.";
            if (on)
            {
                var err = ApiCompiler.Validate(s, e);
                if (err is not null) return err;
            }
            e.Approved = on;
            if (on) s.Enabled = true;
            _dirty.Add(site);
        }
        FlushNow(); ReloadTools(); RaiseConfig(); Raise();
        return null;
    }

    public void Delete(string site, string id)
    {
        lock (_lock)
        {
            if (!_sites.TryGetValue(site, out var s)) return;
            if (s.Endpoints.RemoveAll(e => e.Id == id) == 0) return;
            _dirty.Add(site);
        }
        FlushNow(); ReloadTools(); RaiseConfig(); Raise();
    }

    public void SetSiteEnabled(string site, bool on)
    {
        lock (_lock)
        {
            if (!_sites.TryGetValue(site, out var s) || s.Enabled == on) return;
            s.Enabled = on;
            _dirty.Add(site);
        }
        FlushNow(); ReloadTools(); RaiseConfig(); Raise();
    }

    /// <summary>Forget everything learned about a site (and stop learning it).</summary>
    public void DeleteSite(string site)
    {
        lock (_lock)
        {
            if (!_sites.Remove(site)) return;
            _dirty.Remove(site);
            try { File.Delete(PathFor(site)); } catch { }
        }
        ReloadTools(); RaiseConfig(); Raise();
    }

    /// <summary>Forget the drafts (not the approved endpoints) of a site.</summary>
    public void ClearDrafts(string site)
    {
        lock (_lock)
        {
            if (!_sites.TryGetValue(site, out var s) || s.Endpoints.RemoveAll(e => !e.Approved) == 0) return;
            _dirty.Add(site);
        }
        FlushNow(); Raise();
    }

    // ---------------- persistence ----------------

    private string PathFor(string site) => Path.Combine(_dir, site + ".json");

    private void ScheduleFlush()
    {
        lock (_lock) _flush ??= new Timer(_ => FlushNow(), null, 1500, Timeout.Infinite);
    }

    private void FlushNow()
    {
        List<(string Site, string Json)> work;
        lock (_lock)
        {
            _flush?.Dispose(); _flush = null;
            work = _dirty.Where(_sites.ContainsKey).Select(s => (s, JsonSerializer.Serialize(_sites[s], FileJson))).ToList();
            _dirty.Clear();
        }
        foreach (var (site, json) in work)
        {
            try
            {
                var path = PathFor(site);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, path, true);
            }
            catch { /* a read-only store: the drafts live on in memory */ }
        }
    }

    // ---------------- MCP registration ----------------

    public void Attach(McpServerPrimitiveCollection<McpServerTool> collection)
    {
        _collection = collection;
        ReloadTools();
    }

    /// <summary>Re-syncs the MCP tool list with the approved endpoints; the SDK announces the change to the client.</summary>
    public void ReloadTools()
    {
        var c = _collection;
        if (c is null) return;
        lock (_lock)
        {
            using (c.DeferChangedEvents())
            {
                foreach (var t in _registered.Values) c.Remove(t);
                _registered.Clear();
                foreach (var s in _sites.Values.OrderBy(x => x.Site))
                {
                    if (!s.Enabled) continue;
                    foreach (var e in s.Endpoints.Where(x => x.Approved))
                    {
                        if (ApiCompiler.Validate(s, e) is not null) continue; // hand-edited into an invalid state
                        try
                        {
                            var name = ToolName(s.Site, e.Name);
                            for (var i = 2; _registered.ContainsKey(name); i++) name = ToolName(s.Site, e.Name) + "_" + i;
                            var fn = new ApiFunction(this, s.Site, e, name);
                            var tool = McpServerTool.Create(fn, new McpServerToolCreateOptions
                            {
                                Name = name,
                                Description = fn.Description,
                                ReadOnly = !(e.Write || e.Sensitive),
                                Destructive = e.Write,
                                OpenWorld = true,
                            });
                            if (c.TryAdd(tool)) _registered[name] = tool;
                        }
                        catch { /* one bad endpoint must not block the others */ }
                    }
                }
            }
        }
    }

    public static string ToolName(string site, string name) => ApiCompiler.ToolName(site, name);

    /// <summary>The tool name an endpoint currently has (None when it is not callable).</summary>
    public string? RegisteredName(string site, string id)
    {
        lock (_lock)
            return _registered.Keys.FirstOrDefault(k => _sites.TryGetValue(site, out var s) && s.Endpoints.FirstOrDefault(e => e.Id == id) is { } e
                                                      && (k == ToolName(site, e.Name) || k.StartsWith(ToolName(site, e.Name) + "_", StringComparison.Ordinal)));
    }

    // ---------------- calling ----------------

    /// <summary>Replays an approved endpoint as the user. Throws McpException with a message the agent can act on.</summary>
    public async Task<string> CallAsync(string site, string id, string toolName, JsonObject args, CancellationToken ct)
    {
        ApiEndpoint ep;
        lock (_lock)
        {
            if (!_sites.TryGetValue(site, out var s)) throw new McpException("This site is no longer known to BrAIn.");
            if (!s.Enabled) throw new McpException($"The user has switched off API access to {site} (about:apis). Ask them before trying again.");
            var found = s.Endpoints.FirstOrDefault(e => e.Id == id && e.Approved);
            if (found is null) throw new McpException("The user removed or un-approved this endpoint in about:apis.");
            ep = JsonSerializer.Deserialize<ApiEndpoint>(JsonSerializer.Serialize(found, FileJson), FileJson)!;
        }

        var map = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var kv in args) map[kv.Key] = kv.Value;
        var (plan, err) = ApiCompiler.BuildCall(ep, map);
        if (plan is null) throw new McpException(err ?? "Bad arguments.");

        // Anything that changes data, or touches something sensitive, waits for the user.
        if (ep.Write || ep.Sensitive)
        {
            var what = ep.GqlOp is not null ? $"{ep.GqlOp} ({(ep.Write ? "mutation" : "query")})" : $"{ep.Method} {ep.Path}";
            var why = ep.Write ? $"changes data on {site}" : $"reads something sensitive on {site}";
            var denied = await _browser.Belt.AskAsync(toolName, site, what, why, ct);
            if (denied is not null) throw new McpException(denied);
        }

        ExecResult r;
        try { r = await _browser.ApiExecAsync(ep, plan, ct); }
        catch (OperationCanceledException) { throw; }
        catch (McpException) { throw; }
        catch (Exception ex) { throw new McpException($"Could not run the call in the browser: {ex.Message}"); }

        lock (_lock)
            if (_sites.TryGetValue(site, out var s2) && s2.Endpoints.FirstOrDefault(e => e.Id == id) is { } live) { live.Last = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); _dirty.Add(site); ScheduleFlush(); }

        if (r.Error is not null) throw new McpException($"The request could not be made: {r.Error}");
        if (r.Missing.Count > 0 && !r.Ok)
            throw new McpException($"HTTP {r.Status}. The site needs {string.Join(", ", r.Missing)} (a token the page sends itself) and none was available yet. " +
                                   $"Open {ep.Origin} in a tab, use the site for a moment so it makes a request of its own, then try again.");
        if (!r.Ok)
        {
            var snippet = r.Text.Length > 600 ? r.Text[..600] + "…" : r.Text;
            throw new McpException($"HTTP {r.Status} from {ep.Host}: {snippet}" +
                (r.Status is 401 or 403 ? " (The user may be signed out of the site, or the site needs a token this call did not have.)" : ""));
        }
        if (r.ContentType.Contains("html", StringComparison.OrdinalIgnoreCase))
            throw new McpException($"{ep.Host} answered with a web page instead of data. The user is probably signed out of {site}; ask them to sign in, then try again.");

        var text = r.Text;
        const int cap = 100_000;
        if (text.Length > cap || r.Truncated) text = text[..Math.Min(text.Length, cap)] + $"\n… [cut short: the answer is longer than {cap} characters; ask for a narrower query]";
        return text.Length == 0 ? $"(HTTP {r.Status}, empty answer)" : text;
    }
}

// ======================================================================
// An approved endpoint as an MCP tool.
// ======================================================================

internal sealed class ApiFunction : AIFunction
{
    private readonly ApiService _svc;
    private readonly string _site, _id, _name, _description;
    private readonly JsonElement _schema;

    public ApiFunction(ApiService svc, string site, ApiEndpoint ep, string toolName)
    {
        _svc = svc; _site = site; _id = ep.Id; _name = toolName;
        _description = ep.Description + $" [Learned from {site}; manage in about:apis.]";
        var (props, req) = ApiCompiler.Schema(ep);
        var schema = new JsonObject { ["type"] = "object", ["properties"] = props, ["additionalProperties"] = false };
        if (req.Count > 0) schema["required"] = req;
        _schema = JsonSerializer.SerializeToElement(schema);
    }

    public override string Name => _name;
    public override string Description => _description;
    public override JsonElement JsonSchema => _schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var input = new JsonObject();
        foreach (var kv in arguments)
            input[kv.Key] = kv.Value is null ? null : JsonSerializer.SerializeToNode(kv.Value);
        return await _svc.CallAsync(_site, _id, _name, input, cancellationToken);
    }
}

// ======================================================================
// Agent-facing tools.
// ======================================================================

[McpServerToolType]
public sealed class ApiTools(ApiService apis, BrowserController browser)
{
    [McpServerTool(Name = "browser_api_list"),
     Description("Websites BrAIn has learned an API for. Lists the api_* tools the user approved (call them directly: they talk to the site's own backend as the user, " +
                 "far faster and more reliably than clicking), whether BrAIn is currently learning a site, and how many learned endpoints still wait for the user's approval.")]
    public Task<string> List()
    {
        var sites = apis.Sites();
        if (sites.Count == 0)
            return Task.FromResult("No site has been learned yet. The user can turn on 'Learn this site's API' in the BrAIn menu (⋮) while browsing it; or call browser_api_learn.");
        var sb = new StringBuilder();
        foreach (var s in sites)
        {
            var approved = s.Endpoints.Where(e => e.Approved).ToList();
            var drafts = s.Endpoints.Count - approved.Count;
            sb.AppendLine($"{s.Site}: learning {(s.Learn ? "ON" : "off")}, agent access {(s.Enabled ? "on" : "SWITCHED OFF by the user")}, {approved.Count} approved tool(s), {drafts} draft(s) waiting for the user's review (about:apis)");
            if (s.Enabled)
                foreach (var e in approved)
                {
                    var name = apis.RegisteredName(s.Site, e.Id) ?? ApiService.ToolName(s.Site, e.Name);
                    var sig = string.Join(", ", e.Params.Select(p => p.Name + (p.Required && p.Default is null ? "" : "?")));
                    sb.AppendLine($"  {name}({sig}) — {(e.Write || e.Sensitive ? "ASKS THE USER each time. " : "")}{Trim(e.Description, 160)}");
                }
        }
        return Task.FromResult(sb.ToString().TrimEnd());
    }

    [McpServerTool(Name = "browser_api_learn"),
     Description("Start or stop learning a site's API. While learning, BrAIn watches the JSON requests the site's own scripts make as the page is used (sign-in endpoints, " +
                 "analytics and anything carrying a secret are never kept) and compiles them into endpoint DRAFTS. Drafts do nothing until the user approves them in about:apis. " +
                 "Typical use: turn learning on, reload the page and use the site (browse, open the views you will need), then tell the user to review the drafts. " +
                 "action: on | off | status. site defaults to the site of the active tab (e.g. github.com). Not available in Ghost mode.")]
    public Task<string> Learn(
        [Description("on | off | status")] string action = "status",
        [Description("Site, e.g. github.com (default: the site of the active tab)")] string? site = null)
    {
        var key = string.IsNullOrWhiteSpace(site) ? ApiSite.KeyOfUrl(browser.Tabs.Active?.LastUrl) : ApiSite.KeyOf(site!.Trim().Replace("https://", "").Replace("http://", "").Split('/')[0]);
        if (key.Length == 0 || !ApiService.ValidSite(key)) return Task.FromResult("No site: open the site in a tab first, or pass site.");
        var act = (action ?? "status").Trim().ToLowerInvariant();
        switch (act)
        {
            case "on":
                if (browser.GhostMode) return Task.FromResult("Not changed: Ghost mode is on, and nothing is learned or kept in Ghost mode.");
                var err = apis.SetLearn(key, true);
                if (err is not null) return Task.FromResult("Error: " + err);
                browser.ApiLearnChanged(key);
                return Task.FromResult($"Learning {key}. Reload the page (browser_reload) so its first requests are seen, then use the site. " +
                                       "Learned endpoints are drafts: the user must approve them in about:apis before any api_* tool exists.");
            case "off":
                apis.SetLearn(key, false);
                return Task.FromResult($"Stopped learning {key}. {Summary(key)}");
            default:
                return Task.FromResult($"{key}: learning {(apis.IsLearning(key) ? "ON" : "off")}. {Summary(key)}");
        }
    }

    private string Summary(string key)
    {
        var (n, drafts, approved) = apis.Counts(key);
        return n == 0 ? "Nothing learned yet." : $"{n} endpoint(s) learned: {approved} approved, {drafts} waiting for the user's review in about:apis.";
    }

    [McpServerTool(Name = "browser_api_run"),
     Description("Call an approved api_* tool by name with a JSON object of arguments. Same as calling the tool itself; use it when the api_* tool is not in your tool list yet " +
                 "(some clients only refresh the list on reconnect). Names come from browser_api_list.")]
    public async Task<string> Run(
        [Description("Tool name from browser_api_list, e.g. api_github_list_notifications")] string name,
        [Description("Arguments as a JSON object, e.g. {\"per_page\":5}")] string? argsJson = null,
        CancellationToken ct = default)
    {
        JsonObject? input = new();
        if (!string.IsNullOrWhiteSpace(argsJson))
        {
            try { input = JsonNode.Parse(argsJson) as JsonObject ?? new(); }
            catch (JsonException ex) { return "argsJson is not valid JSON: " + ex.Message; }
        }
        foreach (var s in apis.Sites())
            foreach (var e in s.Endpoints.Where(e => e.Approved))
            {
                var reg = apis.RegisteredName(s.Site, e.Id) ?? ApiService.ToolName(s.Site, e.Name);
                if (!reg.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                try { return await apis.CallAsync(s.Site, e.Id, reg, input, ct); }
                catch (McpException ex) { return "FAILED: " + ex.Message; }
            }
        return $"No approved tool named '{name}'. Use browser_api_list.";
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
