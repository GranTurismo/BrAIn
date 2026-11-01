using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BrAIn.Mcp;

// ======================================================================
// "Every website becomes an API" — the compiler.
//
// While the user lets BrAIn learn a site, the page reports every JSON request the site's own scripts
// make (fetch / XHR). This file turns those observations into endpoint drafts: a URL template with typed
// parameters, the shape of the answer, a name and a read/write verdict. Nothing here touches the browser
// or the disk, so it can be tested on its own (see the harness notes in the changelog).
//
// What is never kept: secrets. Header values that look like credentials stay in the page (only their names
// are recorded), URLs carrying a secret in the query string are skipped, sign-in / token endpoints are never
// learned, and sample responses are redacted and trimmed.
// ======================================================================

public sealed class ApiParam
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>path | query | body | var (a GraphQL variable)</summary>
    [JsonPropertyName("in")] public string In { get; set; } = "query";
    /// <summary>The real key in the URL / body / variables (the name is what the agent sees).</summary>
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    /// <summary>string | number | boolean | object | array</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = "string";
    [JsonPropertyName("required")] public bool Required { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    /// <summary>Used when the caller leaves the argument out (query parameters only).</summary>
    [JsonPropertyName("default")] public JsonNode? Default { get; set; }
    /// <summary>How many observations carried it.</summary>
    [JsonPropertyName("seen")] public int Seen { get; set; }
}

public sealed class ApiEndpoint
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    /// <summary>Tool suffix: the MCP tool is api_&lt;site&gt;_&lt;name&gt;.</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("method")] public string Method { get; set; } = "GET";
    /// <summary>Host (and non-default port) the request goes to; may be a sibling of the page host, e.g. api.github.com.</summary>
    [JsonPropertyName("host")] public string Host { get; set; } = "";
    /// <summary>Origin of the page that made the call: the call is replayed from a tab on this origin.</summary>
    [JsonPropertyName("origin")] public string Origin { get; set; } = "";
    /// <summary>Path with {param} placeholders, e.g. /repos/{repo}/issues/{issue_id}.</summary>
    [JsonPropertyName("path")] public string Path { get; set; } = "/";
    /// <summary>none | json | form | graphql</summary>
    [JsonPropertyName("bodyKind")] public string BodyKind { get; set; } = "none";
    /// <summary>Top-level body fields that are not parameters (GraphQL: operationName, query, extensions).</summary>
    [JsonPropertyName("bodyConst")] public JsonObject? BodyConst { get; set; }
    [JsonPropertyName("gqlOp")] public string? GqlOp { get; set; }
    [JsonPropertyName("headers")] public Dictionary<string, string> Headers { get; set; } = new();
    /// <summary>Header names whose values come from the live page (tokens, CSRF): never stored.</summary>
    [JsonPropertyName("live")] public List<string> Live { get; set; } = new();
    [JsonPropertyName("params")] public List<ApiParam> Params { get; set; } = new();
    /// <summary>Changes something (or is sensitive): every call asks the user first.</summary>
    [JsonPropertyName("write")] public bool Write { get; set; }
    [JsonPropertyName("sensitive")] public bool Sensitive { get; set; }
    [JsonPropertyName("approved")] public bool Approved { get; set; }
    /// <summary>The user changed it: learning no longer reshapes it.</summary>
    [JsonPropertyName("edited")] public bool Edited { get; set; }
    [JsonPropertyName("count")] public int Count { get; set; }
    /// <summary>The success status the site answered with (200, 201, 204 …); 0 = not recorded.</summary>
    [JsonPropertyName("status")] public int Status { get; set; }
    [JsonPropertyName("first")] public long First { get; set; }
    [JsonPropertyName("last")] public long Last { get; set; }
    /// <summary>A redacted, trimmed example answer.</summary>
    [JsonPropertyName("sample")] public JsonNode? Sample { get; set; }
    [JsonPropertyName("shape")] public string? Shape { get; set; }
}

public sealed class ApiSiteFile
{
    [JsonPropertyName("site")] public string Site { get; set; } = "";
    /// <summary>BrAIn is learning this site's requests.</summary>
    [JsonPropertyName("learn")] public bool Learn { get; set; }
    /// <summary>Agents may use this site's approved endpoints (master switch).</summary>
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("endpoints")] public List<ApiEndpoint> Endpoints { get; set; } = new();
}

/// <summary>One observation, as the page reports it.</summary>
public sealed class ApiRecordIn
{
    public string Method { get; set; } = "GET";
    public string Url { get; set; } = "";
    /// <summary>Origin of the page that made the request.</summary>
    public string Origin { get; set; } = "";
    /// <summary>Request headers whose values are harmless (lowercase names).</summary>
    public Dictionary<string, string> Headers { get; set; } = new();
    /// <summary>Names of request headers that carry live credentials (values stay in the page).</summary>
    public List<string> Live { get; set; } = new();
    public string? Body { get; set; }
    public string? BodyType { get; set; }
    public int Status { get; set; }
    public string? ContentType { get; set; }
    /// <summary>The (already trimmed) response as JSON text.</summary>
    public string? Response { get; set; }
}

public static class ApiSite
{
    private static readonly HashSet<string> SecondLevel = new(StringComparer.Ordinal)
        { "co", "com", "org", "net", "gov", "edu", "ac", "or", "ne", "go", "gob", "nom", "mil" };

    /// <summary>The "site" a host belongs to: its registrable domain (api.github.com → github.com).
    /// Same rule as in brain.js.</summary>
    public static string KeyOf(string host)
    {
        host = (host ?? "").Trim().ToLowerInvariant();
        if (host.Length == 0) return "";
        if (System.Net.IPAddress.TryParse(host.Trim('[', ']'), out _)) return host;
        var l = host.Split('.');
        if (l.Length <= 2) return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
        var take = l[^1].Length == 2 && SecondLevel.Contains(l[^2]) ? 3 : 2;
        return string.Join('.', l.Skip(l.Length - take));
    }

    public static string KeyOfUrl(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https") ? KeyOf(u.Host) : "";

    /// <summary>Short label for tool names: github.com → github.</summary>
    public static string Slug(string site)
    {
        var first = site.Split('.')[0];
        var s = Regex.Replace(first.ToLowerInvariant(), "[^a-z0-9]+", "");
        if (s.Length == 0) s = "site";
        if (char.IsDigit(s[0])) s = "s" + s;
        return s.Length > 12 ? s[..12] : s;
    }

    private static readonly Regex Uuid = new("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Hex = new("^[0-9a-f]{12,}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Digits = new(@"^\d+$", RegexOptions.Compiled);
    private static readonly Regex SlugId = new(@"-\d{3,}$", RegexOptions.Compiled);
    private static readonly Regex Blobby = new(@"^[A-Za-z0-9_\-]{20,}$", RegexOptions.Compiled);

    /// <summary>Does this path segment look like an identifier rather than a word?</summary>
    public static bool IsIdLike(string seg)
    {
        if (seg.Length == 0) return false;
        if (Digits.IsMatch(seg) || Uuid.IsMatch(seg) || Hex.IsMatch(seg) || SlugId.IsMatch(seg)) return true;
        if (Blobby.IsMatch(seg) && seg.Any(char.IsDigit)) return true;
        return false;
    }
}

public static class ApiCompiler
{
    public const int MaxEndpointsPerSite = 400;
    private const int MaxBodyConst = 8000;

    private static readonly JsonSerializerOptions J = new() { PropertyNameCaseInsensitive = true };
    private const RegexOptions Rx = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    // Endpoints that mint or manage credentials are never learned.
    private static readonly Regex AuthPath = new(
        @"/(login|logout|log-out|signin|sign-in|signup|sign-up|register|oauth2?|openid|token|tokens|authenticate|authorize|session|sessions|refresh|password|passwords|reset|verify|otp|2fa|mfa|sso|saml|csrf|captcha|recaptcha|passkey|passkeys|webauthn)(/|$)", Rx);
    // Analytics and other noise the site sends about itself.
    private static readonly Regex NoisePath = new(
        @"/(collect|track|tracking|analytics|beacon|telemetry|metrics|pixel|heartbeat|csp-report|ping|sentry|rum|gtm)(/|$)", Rx);
    private static readonly Regex SecretQuery = new(
        @"^(access_?token|auth|authorization|api_?key|key|token|secret|sig|signature|session|sid|jwt|bearer|password|passwd|csrf|xsrf|code|otp)$", Rx);
    private static readonly Regex SensitiveWords = new(
        @"(password|passwd|secret|token|credential|payment|billing|card|cvv|iban|ssn|otp|2fa|mfa|session|apikey|api_key|bank|wire|transfer|withdraw|checkout|purchase|unsubscribe|cancel|delete|remove|destroy|revoke)", Rx);
    private static readonly Regex SensitiveKey = new(
        @"(pass|secret|token|key|auth|cookie|session|ssn|card|cvv|iban|otp|credential)", Rx);
    private static readonly Regex Email = new(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", Rx);
    private static readonly Regex TokenLike = new(@"^[A-Za-z0-9_\-+/=.]{28,}$", Rx);
    private static readonly string[] Boring = { "api", "rest", "v1", "v2", "v3", "v4", "v5", "graphql", "gql", "_next", "internal", "json", "ajax", "xhr", "data", "web", "public", "private", "app" };

    /// <summary>Request headers whose values are harmless and needed to replay a call faithfully.</summary>
    private static readonly HashSet<string> HarmlessHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "accept", "accept-language", "content-type", "x-requested-with", "apollo-require-preflight",
        "x-apollo-operation-name", "x-graphql-operation-name", "x-api-version", "x-client-version",
    };

    /// <summary>Header names that must never be stored with a value, whatever it looks like.</summary>
    public static bool IsSecretHeader(string name)
        => Regex.IsMatch(name, @"(auth|token|csrf|xsrf|secret|session|cookie|api-?key|key|signature|nonce|bearer|jwt)", Rx);

    // ---------------- ingesting one observation ----------------

    /// <summary>Folds an observation into the site's endpoints. Returns null when it was recorded,
    /// otherwise the reason it was skipped.</summary>
    public static string? Ingest(ApiSiteFile site, ApiRecordIn rec, long now)
    {
        var method = (rec.Method ?? "").ToUpperInvariant();
        if (method is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE")) return "method not learned";
        if (rec.Status is < 200 or > 299) return "not a success";
        if (!Uri.TryCreate(rec.Url, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https")) return "not an http(s) url";
        if (!Uri.TryCreate(rec.Origin, UriKind.Absolute, out var o)) return "no page origin";
        if (ApiSite.KeyOf(u.Host) != site.Site || ApiSite.KeyOf(o.Host) != site.Site) return "belongs to another site";
        if (u.UserInfo.Length > 0) return "credentials in the url";

        var isJsonResp = (rec.ContentType ?? "").Contains("json", StringComparison.OrdinalIgnoreCase);
        var noContent = rec.Status == 204 || (method != "GET" && string.IsNullOrWhiteSpace(rec.Response));
        if (!isJsonResp && !noContent) return "not a JSON answer";

        var path = u.AbsolutePath;
        if (AuthPath.IsMatch(path)) return "sign-in / token endpoint (never learned)";
        if (NoisePath.IsMatch(path)) return "analytics";

        var query = ParseQuery(u.Query);
        if (query.Any(q => SecretQuery.IsMatch(q.Key))) return "carries a secret in the url";

        JsonNode? bodyNode = null;
        List<(string Key, string Value)>? form = null;
        if (!string.IsNullOrEmpty(rec.Body))
        {
            if (rec.BodyType == "json")
            {
                try { bodyNode = JsonNode.Parse(rec.Body); }
                catch { return "unreadable body"; }
            }
            else if (rec.BodyType == "form") form = ParseQuery(rec.Body);
            else return "body type not learned";
        }

        // GraphQL?
        string? gqlOp = null, gqlQuery = null;
        if (method == "POST" && bodyNode is JsonObject bo && (Str(bo["query"]) is { } qtext && qtext.Contains('{') || bo["extensions"]?["persistedQuery"] is not null))
        {
            gqlQuery = Str(bo["query"]);
            gqlOp = Str(bo["operationName"]);
            if (string.IsNullOrWhiteSpace(gqlOp) && gqlQuery is not null)
            {
                var mm = Regex.Match(gqlQuery, @"\b(?:query|mutation|subscription)\s+([A-Za-z_]\w*)");
                gqlOp = mm.Success ? mm.Groups[1].Value : null;
            }
            if (string.IsNullOrWhiteSpace(gqlOp)) gqlOp = "op_" + Hash(gqlQuery ?? rec.Body!)[..6];
            if (gqlQuery is not null && Regex.IsMatch(gqlQuery, @"^\s*subscription\b")) return "subscription";
        }
        else if (bodyNode is JsonArray) return "batched body";

        var segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        if (segs.Length > 12) return "path too deep";

        // ---- find the endpoint this observation belongs to (or make one) ----
        ApiEndpoint? ep = site.Endpoints.FirstOrDefault(e => Matches(e, method, u.Authority, segs, gqlOp));
        if (ep is null && gqlOp is null)
            ep = site.Endpoints.FirstOrDefault(e => !e.Edited && !e.Approved && MergeByVariance(e, method, u.Authority, segs));
        var created = false;
        if (ep is null)
        {
            if (site.Endpoints.Count >= MaxEndpointsPerSite) return "too many endpoints for this site";
            ep = NewEndpoint(site, method, u.Authority, o.GetLeftPart(UriPartial.Authority), segs, gqlOp);
            site.Endpoints.Add(ep);
            created = true;
        }

        ep.Count++;
        ep.Last = now;
        if (ep.Status == 0) ep.Status = rec.Status;
        if (ep.First == 0) ep.First = now;
        if (ep.Origin.Length == 0) ep.Origin = o.GetLeftPart(UriPartial.Authority);

        // an approved or hand-edited endpoint keeps its shape; only its statistics move
        if (ep.Approved || ep.Edited) return null;

        // headers
        foreach (var kv in rec.Headers)
        {
            var n = kv.Key.ToLowerInvariant();
            if (IsSecretHeader(n)) { if (!ep.Live.Contains(n)) ep.Live.Add(n); continue; }
            if (HarmlessHeaders.Contains(n) || (kv.Value.Length <= 60 && !TokenLike.IsMatch(kv.Value) && n.StartsWith("x-", StringComparison.Ordinal)))
                ep.Headers[n] = kv.Value;
            else if (!ep.Live.Contains(n)) ep.Live.Add(n);
        }
        foreach (var n0 in rec.Live) { var n = n0.ToLowerInvariant(); if (!ep.Live.Contains(n) && !ep.Headers.ContainsKey(n)) ep.Live.Add(n); }
        ep.Live.Remove("content-type");

        // path values (examples for the parameters)
        var tmpl = ep.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < tmpl.Length && i < segs.Length; i++)
            if (tmpl[i].Length > 2 && tmpl[i][0] == '{' && tmpl[i][^1] == '}')
            {
                var p = ep.Params.FirstOrDefault(x => x.In == "path" && x.Name == tmpl[i][1..^1]);
                if (p is not null) p.Seen++;
            }

        // query parameters
        var seenQ = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (k, v) in query)
        {
            if (!seenQ.Add(k)) continue;
            var p = ep.Params.FirstOrDefault(x => x.In == "query" && x.Key == k);
            if (p is null)
            {
                p = new ApiParam { In = "query", Key = k, Name = UniqueName(ep, Sanitize(k)), Type = QueryType(v) };
                ep.Params.Add(p);
            }
            else if (p.Type != "string" && QueryType(v) != p.Type) p.Type = "string";
            p.Seen++;
            if (v.Length is > 0 and <= 60 && !LooksSecret(v)) p.Default = TypedDefault(p.Type, v);
        }

        // body
        if (gqlOp is not null && bodyNode is JsonObject gb)
        {
            ep.BodyKind = "graphql";
            ep.GqlOp = gqlOp;
            var konst = new JsonObject();
            foreach (var kv in gb)
                if (kv.Key != "variables" && kv.Value is not null) konst[kv.Key] = kv.Value.DeepClone();
            if (konst.ToJsonString().Length <= MaxBodyConst) ep.BodyConst = konst;
            var vars = gb["variables"] as JsonObject;
            if (vars is not null)
                foreach (var kv in vars)
                {
                    var p = ep.Params.FirstOrDefault(x => x.In == "var" && x.Key == kv.Key);
                    if (p is null) { p = new ApiParam { In = "var", Key = kv.Key, Name = UniqueName(ep, Sanitize(kv.Key)), Type = JsonType(kv.Value) }; ep.Params.Add(p); }
                    if (kv.Value is not null) p.Seen++; // a null in the sample says nothing is needed: it stays optional
                }
        }
        else if (bodyNode is JsonObject jb)
        {
            ep.BodyKind = "json";
            ep.BodyConst ??= new JsonObject();
            foreach (var kv in jb)
            {
                var p = ep.Params.FirstOrDefault(x => x.In == "body" && x.Key == kv.Key);
                if (p is null) { p = new ApiParam { In = "body", Key = kv.Key, Name = UniqueName(ep, Sanitize(kv.Key)), Type = JsonType(kv.Value) }; ep.Params.Add(p); }
                if (kv.Value is not null) p.Seen++;
            }
        }
        else if (form is not null)
        {
            ep.BodyKind = "form";
            foreach (var (k, v) in form.DistinctBy(x => x.Key))
            {
                var p = ep.Params.FirstOrDefault(x => x.In == "body" && x.Key == k);
                if (p is null) { p = new ApiParam { In = "body", Key = k, Name = UniqueName(ep, Sanitize(k)), Type = QueryType(v) }; ep.Params.Add(p); }
                p.Seen++;
            }
        }

        // response sample (first useful one wins)
        if (ep.Sample is null && !string.IsNullOrWhiteSpace(rec.Response))
        {
            try
            {
                var node = JsonNode.Parse(rec.Response);
                ep.Sample = Redact(node, 0);
                ep.Shape = Summarize(ep.Sample);
            }
            catch { /* a truncated answer: no sample */ }
        }

        Finish(site, ep, created);
        return null;
    }

    /// <summary>Recomputes everything derived: requiredness, verdicts, name, description.</summary>
    public static void Finish(ApiSiteFile site, ApiEndpoint ep, bool created = false)
    {
        foreach (var p in ep.Params)
        {
            if (p.In == "path") p.Required = true;
            else if (p.In == "query") p.Required = p.Seen >= ep.Count && p.Default is null;
            else p.Required = ep.Count > 0 && p.Seen >= ep.Count && p.Type != "boolean";
        }
        var gqlText = ep.BodyKind == "graphql" ? Str(ep.BodyConst?["query"]) : null;
        // a GraphQL query is a read; a persisted query we cannot read the text of might be anything, so it counts as a write
        var gqlRead = ep.BodyKind == "graphql" && gqlText is not null && !Regex.IsMatch(gqlText, @"^\s*mutation\b");
        ep.Write = ep.Method != "GET" && ep.Method != "HEAD" && !gqlRead;
        var hay = ep.Path + " " + ep.GqlOp + " " + string.Join(' ', ep.Params.Select(p => p.Key));
        ep.Sensitive = SensitiveWords.IsMatch(hay);
        if (ep.Id.Length == 0) ep.Id = Hash(site.Site + "|" + ep.Method + "|" + ep.Host + "|" + ep.Path + "|" + ep.GqlOp)[..10];
        if (created || ep.Name.Length == 0) ep.Name = UniqueEndpointName(site, ep, Suggest(ep));
        if (!ep.Edited) ep.Description = Describe(ep);
    }

    // ---------------- matching ----------------

    private static bool Matches(ApiEndpoint e, string method, string host, string[] segs, string? gqlOp)
    {
        if (e.Method != method || !e.Host.Equals(host, StringComparison.OrdinalIgnoreCase)) return false;
        var t = e.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (t.Length != segs.Length) return false;
        for (var i = 0; i < t.Length; i++)
        {
            var wild = t[i].Length > 2 && t[i][0] == '{' && t[i][^1] == '}';
            if (!wild && !t[i].Equals(segs[i], StringComparison.Ordinal)) return false;
        }
        if (gqlOp is not null) return e.GqlOp == gqlOp;
        return e.GqlOp is null;
    }

    /// <summary>Two observations that differ in exactly one word-like segment, where one value looks like an
    /// identifier, are the same endpoint: that segment becomes a parameter.</summary>
    private static bool MergeByVariance(ApiEndpoint e, string method, string host, string[] segs)
    {
        if (e.GqlOp is not null || e.Method != method || !e.Host.Equals(host, StringComparison.OrdinalIgnoreCase)) return false;
        var t = e.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (t.Length != segs.Length) return false;
        var diff = -1;
        for (var i = 0; i < t.Length; i++)
        {
            var wild = t[i].Length > 2 && t[i][0] == '{' && t[i][^1] == '}';
            if (wild || t[i].Equals(segs[i], StringComparison.Ordinal)) continue;
            if (diff >= 0) return false;
            diff = i;
        }
        if (diff < 0) return false;
        bool IdishWord(string s) => s.Any(char.IsDigit) || s.Length >= 12;
        if (!IdishWord(t[diff]) && !IdishWord(segs[diff])) return false;
        var name = PathParamName(t, diff, e);
        t[diff] = "{" + name + "}";
        e.Path = "/" + string.Join('/', t);
        e.Params.Add(new ApiParam { In = "path", Key = name, Name = name, Type = "string", Required = true, Seen = e.Count });
        return true;
    }

    private static ApiEndpoint NewEndpoint(ApiSiteFile site, string method, string host, string origin, string[] segs, string? gqlOp)
    {
        var ep = new ApiEndpoint { Method = method, Host = host, Origin = origin, GqlOp = gqlOp, BodyKind = gqlOp is null ? "none" : "graphql" };
        var outSegs = new string[segs.Length];
        for (var i = 0; i < segs.Length; i++)
        {
            if (gqlOp is null && ApiSite.IsIdLike(segs[i]))
            {
                var name = PathParamName(outSegs, i, ep);
                outSegs[i] = "{" + name + "}";
                ep.Params.Add(new ApiParam { In = "path", Key = name, Name = name, Type = Digits(segs[i]) ? "number" : "string", Required = true });
            }
            else outSegs[i] = segs[i];
        }
        ep.Path = "/" + string.Join('/', outSegs);
        return ep;
    }

    private static bool Digits(string s) => s.Length > 0 && s.All(char.IsDigit);

    // ---------------- naming ----------------

    private static string Sanitize(string key)
    {
        var s = Regex.Replace(key, @"([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant();
        s = Regex.Replace(s, "[^a-z0-9_]+", "_").Trim('_');
        if (s.Length == 0) s = "value";
        if (!char.IsLetter(s[0]) && s[0] != '_') s = "p_" + s;
        return s.Length > 28 ? s[..28] : s;
    }

    private static string UniqueName(ApiEndpoint ep, string name)
    {
        var n = name; var i = 2;
        while (ep.Params.Any(p => p.Name == n)) n = name + i++;
        return n;
    }

    private static string Singular(string w)
    {
        w = w.ToLowerInvariant();
        if (w.EndsWith("ies") && w.Length > 4) return w[..^3] + "y";
        if (w.EndsWith("sses") || w.EndsWith("us") || w.EndsWith("ss")) return w;
        if (w.EndsWith("s") && w.Length > 3) return w[..^1];
        return w;
    }

    private static string PathParamName(string[] segsSoFar, int i, ApiEndpoint ep)
    {
        var baseName = "id";
        for (var j = i - 1; j >= 0; j--)
        {
            var s = segsSoFar[j];
            if (s is null || s.Length == 0 || s[0] == '{' || Boring.Contains(s.ToLowerInvariant())) continue;
            baseName = Sanitize(Singular(s)) + "_id";
            break;
        }
        var n = baseName; var k = 2;
        while (ep.Params.Any(p => p.Name == n) || segsSoFar.Contains("{" + n + "}")) n = baseName + k++;
        return n;
    }

    private static string Suggest(ApiEndpoint ep)
    {
        if (ep.GqlOp is not null)
        {
            var g = Sanitize(ep.GqlOp);
            return g.Length > 36 ? g[..36] : g;
        }
        var lits = ep.Path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => !(s[0] == '{') && !Boring.Contains(s.ToLowerInvariant())).Select(s => Sanitize(s)).ToList();
        var endsOnParam = ep.Path.TrimEnd('/').EndsWith('}');
        var noun = lits.Count > 0 ? lits[^1] : "root";
        var parent = lits.Count > 1 ? Singular(lits[^2]) : null;
        string verb;
        if (endsOnParam)
        {
            noun = Singular(noun);
            verb = ep.Method switch { "GET" => "get", "DELETE" => "delete", _ => "update" };
        }
        else verb = ep.Method switch { "GET" => "list", "POST" => "create", "DELETE" => "delete", _ => "update" };
        if (!endsOnParam && ep.Method is "POST") noun = Singular(noun);
        var name = parent is not null && parent != noun ? $"{verb}_{parent}_{noun}" : $"{verb}_{noun}";
        return name.Length > 36 ? name[..36] : name;
    }

    private static string UniqueEndpointName(ApiSiteFile site, ApiEndpoint self, string name)
    {
        var n = name; var i = 2;
        while (site.Endpoints.Any(e => !ReferenceEquals(e, self) && e.Name == n)) n = (name.Length > 33 ? name[..33] : name) + "_" + i++;
        return n;
    }

    private static string Describe(ApiEndpoint ep)
    {
        var what = ep.GqlOp is not null ? $"GraphQL {(ep.Write ? "mutation" : "query")} {ep.GqlOp}" : $"{ep.Method} {ep.Path}";
        var sb = new StringBuilder($"Calls {ep.Host}: {what}.");
        if (!string.IsNullOrEmpty(ep.Shape)) sb.Append(" Returns ").Append(ep.Shape).Append('.');
        else if (ep.Write) sb.Append(" Returns nothing useful (the site answers with an empty body).");
        sb.Append(ep.Write || ep.Sensitive
            ? " CHANGES DATA or touches something sensitive: the user is asked to approve every call."
            : " Read-only.");
        sb.Append(" Runs as the user, inside a tab on the site.");
        return sb.ToString();
    }

    // ---------------- helpers: values, types ----------------

    public static List<(string Key, string Value)> ParseQuery(string? q)
    {
        var list = new List<(string, string)>();
        if (string.IsNullOrEmpty(q)) return list;
        foreach (var part in q.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = part.IndexOf('=');
            var k = Uri.UnescapeDataString((i < 0 ? part : part[..i]).Replace('+', ' '));
            var v = i < 0 ? "" : Uri.UnescapeDataString(part[(i + 1)..].Replace('+', ' '));
            list.Add((k, v));
        }
        return list;
    }

    private static string QueryType(string v)
    {
        if (v is "true" or "false") return "boolean";
        if (Regex.IsMatch(v, @"^-?\d{1,15}(\.\d+)?$") && !(v.Length > 1 && v[0] == '0' && !v.StartsWith("0.", StringComparison.Ordinal))) return "number";
        return "string";
    }

    private static JsonNode? TypedDefault(string type, string v) => type switch
    {
        "number" => double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)
            ? (d == Math.Floor(d) && Math.Abs(d) < 1e15 ? JsonValue.Create((long)d) : JsonValue.Create(d)) : JsonValue.Create(v),
        "boolean" => JsonValue.Create(v == "true"),
        _ => JsonValue.Create(v),
    };

    private static bool LooksSecret(string v) => TokenLike.IsMatch(v) || Email.IsMatch(v);

    private static string JsonType(JsonNode? n) => n switch
    {
        null => "string",
        JsonObject => "object",
        JsonArray => "array",
        JsonValue v when v.TryGetValue<bool>(out _) => "boolean",
        JsonValue v when v.TryGetValue<double>(out _) || v.TryGetValue<long>(out _) => "number",
        _ => "string",
    };

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>The MCP tool name of an endpoint: api_&lt;site&gt;_&lt;name&gt;.</summary>
    public static string ToolName(string site, string name)
    {
        var n = "api_" + ApiSite.Slug(site) + "_" + name;
        return n.Length > 60 ? n[..60] : n;
    }

    public static string Hash(string s)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    // ---------------- samples: redaction, trimming, summary ----------------

    /// <summary>Trims and redacts a response so it can be kept: arrays shrink to three items, long strings are
    /// cut, credentials and addresses are masked.</summary>
    public static JsonNode? Redact(JsonNode? n, int depth, string? key = null)
    {
        if (n is null) return null;
        if (depth > 6) return JsonValue.Create("…");
        if (n is JsonObject o)
        {
            var r = new JsonObject();
            var count = 0;
            foreach (var kv in o)
            {
                if (++count > 40) { r["…"] = "more fields"; break; }
                r[kv.Key] = Redact(kv.Value, depth + 1, kv.Key);
            }
            return r;
        }
        if (n is JsonArray a)
        {
            var r = new JsonArray();
            foreach (var x in a.Take(3)) r.Add(Redact(x, depth + 1, key));
            return r;
        }
        if (n is JsonValue v && v.TryGetValue<string>(out var s))
        {
            if (key is not null && SensitiveKey.IsMatch(key)) return JsonValue.Create("•••");
            s = Email.Replace(s, "user@example.com");
            if (TokenLike.IsMatch(s)) return JsonValue.Create("…");
            if (s.Length > 80) s = s[..80] + "…";
            return JsonValue.Create(s);
        }
        return n.DeepClone();
    }

    /// <summary>Response keys end up in the tool description an agent reads: only plain, short names get through.</summary>
    private static string CleanKey(string k)
    {
        var s = Regex.Replace(k ?? "", @"[^A-Za-z0-9_\-.]", "_");
        return s.Length > 32 ? s[..32] : s;
    }

    /// <summary>One line describing a JSON value's shape: {id: number, title: string, user: {login: string}}.</summary>
    public static string Summarize(JsonNode? n, int depth = 0)
    {
        switch (n)
        {
            case null: return "null";
            case JsonArray a:
                return a.Count == 0 ? "[]" : "[" + Summarize(a[0], depth + 1) + ", …]";
            case JsonObject o:
                if (depth >= 3) return "{…}";
                var parts = o.Take(10).Select(kv => CleanKey(kv.Key) + ": " + Summarize(kv.Value, depth + 1)).ToList();
                if (o.Count > 10) parts.Add("…");
                return "{" + string.Join(", ", parts) + "}";
            case JsonValue v:
                if (v.TryGetValue<bool>(out _)) return "boolean";
                if (v.TryGetValue<string>(out _)) return "string";
                return "number";
        }
        return "value";
    }

    // ---------------- editing by the user ----------------

    private static readonly Regex NameRx = new("^[a-z][a-z0-9_]{1,35}$", RegexOptions.Compiled);
    private static readonly Regex ParamRx = new("^[a-z_][a-z0-9_]{0,31}$", RegexOptions.Compiled);

    /// <summary>Checks a hand-edited endpoint; null = fine.</summary>
    public static string? Validate(ApiSiteFile site, ApiEndpoint ep)
    {
        if (!NameRx.IsMatch(ep.Name ?? "")) return "Name: lowercase letters, digits and underscores, starting with a letter (2–36 characters).";
        if (site.Endpoints.Any(e => e.Id != ep.Id && e.Name == ep.Name)) return $"Another endpoint of this site is already called '{ep.Name}'.";
        if (string.IsNullOrEmpty(ep.Path) || ep.Path[0] != '/') return "Path must start with '/'.";
        var names = new HashSet<string>();
        foreach (var p in ep.Params)
        {
            if (!ParamRx.IsMatch(p.Name ?? "")) return $"Parameter '{p.Name}': lowercase letters, digits and underscores.";
            if (!names.Add(p.Name ?? "")) return $"Two parameters are called '{p.Name}'.";
            if (p.Type is not ("string" or "number" or "boolean" or "object" or "array")) p.Type = "string";
        }
        var holes = Regex.Matches(ep.Path, @"\{([a-z_][a-z0-9_]*)\}").Select(m => m.Groups[1].Value).ToList();
        var pathParams = ep.Params.Where(p => p.In == "path").Select(p => p.Name).ToList();
        if (holes.Except(pathParams).Any()) return $"The path uses {{{holes.Except(pathParams).First()}}} but no parameter has that name.";
        if (pathParams.Except(holes).Any()) return $"Parameter '{pathParams.Except(holes).First()}' is not used in the path.";
        if (ep.Description is { Length: > 1200 }) return "Description is too long.";
        return null;
    }

    // ---------------- calling: building the request ----------------

    /// <summary>What the page needs to make the call.</summary>
    public sealed class CallPlan
    {
        public string Method { get; set; } = "GET";
        public string Url { get; set; } = "";
        public Dictionary<string, string> Headers { get; set; } = new();
        public List<string> Live { get; set; } = new();
        public string? Body { get; set; }
    }

    /// <summary>Builds the request for the given arguments; returns an error text instead when they are wrong.</summary>
    public static (CallPlan? Plan, string? Error) BuildCall(ApiEndpoint ep, IDictionary<string, JsonNode?> args)
    {
        foreach (var a in args.Keys)
            if (!ep.Params.Any(p => p.Name == a)) return (null, $"Unknown argument '{a}'. Arguments: {string.Join(", ", ep.Params.Select(p => p.Name))}.");

        object? Val(ApiParam p, out string? err)
        {
            err = null;
            if (!args.TryGetValue(p.Name, out var v) || v is null)
            {
                if (p.Default is not null) return p.Default.DeepClone();
                if (p.Required) { err = $"Missing required argument '{p.Name}' ({p.Type})."; }
                return null;
            }
            return v;
        }

        var path = ep.Path;
        var query = new List<string>();
        var bodyFields = new List<(ApiParam P, JsonNode V)>();
        foreach (var p in ep.Params)
        {
            var val = Val(p, out var err);
            if (err is not null) return (null, err);
            if (val is null) continue;
            var node = (JsonNode)val;
            var text = node is JsonValue jv && jv.TryGetValue<string>(out var sv) ? sv : node.ToJsonString();
            switch (p.In)
            {
                case "path":
                    if (text.Length == 0 || text.Contains('/') && !p.Name.EndsWith("_path")) return (null, $"Argument '{p.Name}' must be a single path segment.");
                    path = path.Replace("{" + p.Name + "}", Uri.EscapeDataString(text));
                    break;
                case "query":
                    if (p.Type is "object" or "array") return (null, $"Argument '{p.Name}' cannot be an object.");
                    query.Add(Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(node is JsonValue bv && bv.TryGetValue<bool>(out var b) ? (b ? "true" : "false") : text));
                    break;
                default:
                    bodyFields.Add((p, node));
                    break;
            }
        }

        var plan = new CallPlan { Method = ep.Method, Headers = new(ep.Headers), Live = new(ep.Live) };
        plan.Url = (ep.Origin.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "http://" : "https://") + ep.Host + path;
        if (query.Count > 0) plan.Url += "?" + string.Join('&', query);

        switch (ep.BodyKind)
        {
            case "json":
                {
                    var b = ep.BodyConst?.DeepClone() as JsonObject ?? new JsonObject();
                    foreach (var (p, v) in bodyFields) b[p.Key] = Coerce(p, v);
                    plan.Body = b.ToJsonString();
                    plan.Headers["content-type"] = "application/json";
                    break;
                }
            case "graphql":
                {
                    var b = ep.BodyConst?.DeepClone() as JsonObject ?? new JsonObject();
                    var vars = new JsonObject();
                    foreach (var (p, v) in bodyFields) vars[p.Key] = Coerce(p, v);
                    b["variables"] = vars;
                    plan.Body = b.ToJsonString();
                    plan.Headers["content-type"] = "application/json";
                    break;
                }
            case "form":
                plan.Body = string.Join('&', bodyFields.Select(f =>
                    Uri.EscapeDataString(f.P.Key) + "=" + Uri.EscapeDataString(f.V is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : f.V.ToJsonString())));
                plan.Headers["content-type"] = "application/x-www-form-urlencoded";
                break;
        }
        return (plan, null);
    }

    private static JsonNode? Coerce(ApiParam p, JsonNode v)
    {
        // a string that holds JSON for an object/array parameter
        if ((p.Type is "object" or "array") && v is JsonValue jv && jv.TryGetValue<string>(out var s))
            try { return JsonNode.Parse(s); } catch { }
        return v.DeepClone();
    }

    /// <summary>The JSON schema properties for the tool arguments.</summary>
    public static (JsonObject Props, JsonArray Required) Schema(ApiEndpoint ep)
    {
        var props = new JsonObject();
        var req = new JsonArray();
        foreach (var p in ep.Params)
        {
            var po = new JsonObject { ["type"] = p.Type };
            var desc = p.Description;
            if (string.IsNullOrWhiteSpace(desc))
                desc = p.In switch
                {
                    "path" => $"Part of the URL path ({{{p.Name}}}).",
                    "query" => $"Query parameter '{p.Key}'." + (p.Default is not null ? $" Default: {p.Default.ToJsonString()}." : ""),
                    "var" => $"GraphQL variable '{p.Key}'.",
                    _ => $"Field '{p.Key}' of the request body.",
                };
            po["description"] = desc;
            if (p.Default is not null) po["default"] = p.Default.DeepClone();
            props[p.Name] = po;
            if (p.Required && p.Default is null) req.Add(p.Name);
        }
        return (props, req);
    }
}
