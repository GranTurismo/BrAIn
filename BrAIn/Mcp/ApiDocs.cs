using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BrAIn.Mcp;

// ======================================================================
// Documentation for a learned site: an OpenAPI 3 file and a full Markdown reference.
//
// Both are generated from what BrAIn stored (never from live traffic), so they contain exactly what the
// about:apis page shows: URL templates, argument types and descriptions, header NAMES, and trimmed, masked
// example answers. No token, cookie or other credential value exists in the data they are built from.
// Pure functions: no browser, no disk. (Tested on their own, see the notes in the changelog.)
// ======================================================================

public static class ApiDocs
{
    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private static readonly string[] Boring = { "api", "rest", "v1", "v2", "v3", "v4", "v5", "graphql", "gql", "_next", "internal", "json", "ajax", "xhr", "data", "web", "public", "private", "app" };

    /// <summary>The endpoints a document covers: the approved ones, or everything learned.</summary>
    public static List<ApiEndpoint> Select(ApiSiteFile site, bool includeDrafts)
        => site.Endpoints.Where(e => includeDrafts || e.Approved)
            .OrderBy(e => e.Approved ? 0 : 1).ThenBy(e => e.Host, StringComparer.Ordinal).ThenBy(e => e.Path, StringComparer.Ordinal).ThenBy(e => e.Method, StringComparer.Ordinal)
            .ToList();

    /// <summary>"list_hello_issues" → "List hello issues".</summary>
    public static string Humanize(string name)
    {
        var s = (name ?? "").Replace('_', ' ').Trim();
        return s.Length == 0 ? "Endpoint" : char.ToUpperInvariant(s[0]) + s[1..];
    }

    private static string Scheme(ApiEndpoint e) => e.Origin.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "http" : "https";
    private static string BaseUrl(ApiEndpoint e) => Scheme(e) + "://" + e.Host;
    private static string Access(ApiEndpoint e) => e.Write ? "changes data: BrAIn asks the user before every call" : e.Sensitive ? "reads something sensitive: BrAIn asks the user before every call" : "read-only";
    private static bool Asks(ApiEndpoint e) => e.Write || e.Sensitive;
    private static string Stamp(long ms) => ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("yyyy-MM-dd") : "";

    /// <summary>What an argument is and how it is sent.</summary>
    public static string ParamText(ApiEndpoint ep, ApiParam p)
    {
        var how = p.In switch
        {
            "path" => "Part of the URL path.",
            "query" => $"Query parameter `{p.Key}`.",
            "var" => $"GraphQL variable `{p.Key}`.",
            _ => ep.BodyKind == "form" ? $"Form field `{p.Key}`." : $"Field `{p.Key}` of the JSON request body.",
        };
        var own = string.IsNullOrWhiteSpace(p.Description) ? "" : p.Description!.Trim() + " ";
        return (own + how + (p.Default is not null ? $" Default: `{p.Default.ToJsonString()}`." : "")).Trim();
    }

    private static JsonNode ExampleValue(ApiParam p)
    {
        if (p.Default is not null) return p.Default.DeepClone();
        return p.Type switch
        {
            "number" => JsonValue.Create(1),
            "boolean" => JsonValue.Create(false),
            "object" => new JsonObject(),
            "array" => new JsonArray(),
            _ => JsonValue.Create(p.In == "path" ? p.Name.ToUpperInvariant() : "string"),
        };
    }

    /// <summary>A JSON Schema for a (masked, trimmed) sample answer.</summary>
    public static JsonObject SchemaOf(JsonNode? n, int depth = 0)
    {
        switch (n)
        {
            case null: return new JsonObject { ["nullable"] = true };
            case JsonArray a:
                return new JsonObject { ["type"] = "array", ["items"] = a.Count == 0 || depth > 5 ? new JsonObject() : SchemaOf(a[0], depth + 1) };
            case JsonObject o:
                if (depth > 5) return new JsonObject { ["type"] = "object" };
                var props = new JsonObject();
                foreach (var kv in o.Take(60)) props[kv.Key] = SchemaOf(kv.Value, depth + 1);
                return new JsonObject { ["type"] = "object", ["properties"] = props };
            case JsonValue v:
                if (v.TryGetValue<bool>(out _)) return new JsonObject { ["type"] = "boolean" };
                if (v.TryGetValue<long>(out _)) return new JsonObject { ["type"] = "integer" };
                if (v.TryGetValue<double>(out _)) return new JsonObject { ["type"] = "number" };
                return new JsonObject { ["type"] = "string" };
        }
        return new JsonObject();
    }

    private static string SecurityKey(string header) => Regex.Replace(header.ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');

    private static string Tag(ApiEndpoint e)
    {
        if (e.GqlOp is not null) return "GraphQL";
        var lit = e.Path.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(s => s[0] != '{' && !Boring.Contains(s.ToLowerInvariant()));
        return lit ?? "root";
    }

    // =========================================================== OpenAPI

    /// <summary>OpenAPI 3.0.3 as JSON text, with a description on every operation, argument, field and security scheme.</summary>
    public static string OpenApi(ApiSiteFile site, bool includeDrafts, DateTimeOffset now)
    {
        var eps = Select(site, includeDrafts);
        var hosts = eps.Select(BaseUrl).Distinct().ToList();
        var servers = new JsonArray();
        foreach (var h in hosts)
            servers.Add(new JsonObject { ["url"] = h, ["description"] = "Where the site sends these requests." });
        if (hosts.Count == 0) servers.Add(new JsonObject { ["url"] = "https://" + site.Site });

        var info = new JsonObject
        {
            ["title"] = $"{site.Site} (learned by BrAIn)",
            ["description"] =
                $"The private API behind **{site.Site}**, as BrAIn learned it from the data requests the site's own pages make. " +
                $"It is not an official, documented or stable API: it can change without notice. Calls need the user's signed-in session (cookies) and, on many endpoints, " +
                $"tokens the site's own scripts send (listed under *security*; their values are never stored).\n\n" +
                $"GraphQL operations all go to one URL; here each is listed as `POST <path>#<OperationName>`. The `#...` part only tells them apart: " +
                $"send the request to `<path>` with the body shown.\n\n" +
                $"Endpoints marked `x-brain-asks-before-call` change data or touch something sensitive: when an AI agent calls them through BrAIn, BrAIn asks the user every time.",
            ["version"] = $"learned-{now:yyyy-MM-dd}",
            ["x-generated-by"] = "BrAIn (Site APIs)",
            ["x-generated-at"] = now.ToString("yyyy-MM-dd'T'HH:mm:ssK"),
            ["x-includes-drafts"] = includeDrafts,
        };

        var securitySchemes = new JsonObject
        {
            ["sessionCookie"] = new JsonObject
            {
                ["type"] = "apiKey", ["in"] = "cookie", ["name"] = "session",
                ["description"] = "The user's signed-in session. The browser sends the site's cookies by itself; the cookie's real name is not recorded (and no cookie value is ever stored).",
            },
        };
        foreach (var h in eps.SelectMany(e => e.Live).Distinct().OrderBy(x => x))
            securitySchemes[SecurityKey(h)] = new JsonObject
            {
                ["type"] = "apiKey", ["in"] = "header", ["name"] = h,
                ["description"] = $"`{h}` is sent by the site's own scripts (a token or similar). BrAIn reads it live from the page when it makes a call and never stores its value.",
            };

        var paths = new JsonObject();
        var tags = new List<string>();
        var opIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in eps)
        {
            var key = e.GqlOp is not null ? e.Path + "#" + e.GqlOp : e.Path;
            if (paths[key] is JsonObject taken && taken[e.Method.ToLowerInvariant()] is not null) key += "#" + e.Host;
            var item = paths[key] as JsonObject ?? new JsonObject();
            paths[key] = item;
            var tag = Tag(e);
            if (!tags.Contains(tag)) tags.Add(tag);
            item[e.Method.ToLowerInvariant()] = Operation(site, e, tag, opIds, hosts.FirstOrDefault() ?? "");
        }

        var doc = new JsonObject
        {
            ["openapi"] = "3.0.3",
            ["info"] = info,
            ["servers"] = servers,
            ["tags"] = new JsonArray(tags.Select(t => (JsonNode)new JsonObject { ["name"] = t, ["description"] = t == "GraphQL" ? "GraphQL operations (all sent to one URL)." : $"Endpoints under `/{t}`." }).ToArray()),
            ["paths"] = paths,
            ["components"] = new JsonObject { ["securitySchemes"] = securitySchemes },
        };
        return doc.ToJsonString(Pretty) + "\n";
    }

    private static JsonObject Operation(ApiSiteFile site, ApiEndpoint e, string tag, HashSet<string> opIds, string firstServer)
    {
        var tool = ApiCompiler.ToolName(site.Site, e.Name);
        var opId = e.Name; for (var i = 2; !opIds.Add(opId); i++) opId = e.Name + "_" + i;
        var desc = new StringBuilder(string.IsNullOrWhiteSpace(e.Description) ? $"{e.Method} {e.Path}" : e.Description.Trim());
        desc.Append($"\n\n**Access:** {Access(e)}.  \n**BrAIn tool:** `{tool}`{(e.Approved ? "" : " (draft: not yet approved by the user)")}.");
        if (e.Count > 0) desc.Append($"  \n**Seen:** {e.Count} time(s){(e.First > 0 ? $", {Stamp(e.First)} – {Stamp(e.Last)}" : "")}.");
        if (e.Headers.Count > 0) desc.Append("  \n**Headers the site sends:** " + string.Join(", ", e.Headers.Select(h => $"`{h.Key}: {h.Value}`")) + ".");

        var op = new JsonObject
        {
            ["operationId"] = opId,
            ["summary"] = Humanize(e.Name) + (e.Approved ? "" : " (draft)"),
            ["description"] = desc.ToString(),
            ["tags"] = new JsonArray(tag),
        };
        if (BaseUrl(e) != firstServer) op["servers"] = new JsonArray(new JsonObject { ["url"] = BaseUrl(e) });

        // parameters: the real key is what goes on the wire
        var pars = new JsonArray();
        foreach (var p in e.Params.Where(p => p.In is "path" or "query"))
        {
            var ps = new JsonObject { ["type"] = p.Type is "object" or "array" ? "string" : p.Type };
            if (p.Default is not null) ps["default"] = p.Default.DeepClone();
            pars.Add(new JsonObject
            {
                ["name"] = p.In == "path" ? p.Name : p.Key,
                ["in"] = p.In,
                ["required"] = p.In == "path" || p.Required,
                ["description"] = ParamText(e, p) + (p.In == "query" && p.Key != p.Name ? $" (BrAIn tool argument `{p.Name}`.)" : ""),
                ["schema"] = ps,
            });
        }
        if (pars.Count > 0) op["parameters"] = pars;

        // request body
        var bodyParams = e.Params.Where(p => p.In is "body" or "var").ToList();
        if (e.BodyKind == "graphql")
        {
            var vars = new JsonObject(); var varReq = new JsonArray(); var varEx = new JsonObject();
            foreach (var p in bodyParams)
            {
                vars[p.Key] = new JsonObject { ["type"] = p.Type, ["description"] = ParamText(e, p) };
                if (p.Required) varReq.Add(p.Key);
                varEx[p.Key] = ExampleValue(p);
            }
            var varSchema = new JsonObject { ["type"] = "object", ["properties"] = vars };
            if (varReq.Count > 0) varSchema["required"] = varReq;
            var query = Str(e.BodyConst?["query"]);
            var example = new JsonObject { ["operationName"] = e.GqlOp };
            if (query is not null) example["query"] = query;
            if (e.BodyConst?["extensions"] is JsonNode ext) example["extensions"] = ext.DeepClone();
            example["variables"] = varEx;
            op["requestBody"] = new JsonObject
            {
                ["required"] = true,
                ["description"] = $"GraphQL {(e.Write ? "mutation" : "query")} `{e.GqlOp}`. The `query` is the saved document the site itself sends.",
                ["content"] = new JsonObject
                {
                    ["application/json"] = new JsonObject
                    {
                        ["schema"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["operationName"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(e.GqlOp) },
                                ["query"] = new JsonObject { ["type"] = "string", ["description"] = "The GraphQL document." },
                                ["variables"] = varSchema,
                            },
                            ["required"] = new JsonArray("operationName", "variables"),
                        },
                        ["example"] = example,
                    },
                },
            };
        }
        else if (e.BodyKind is "json" or "form")
        {
            var props = new JsonObject(); var req = new JsonArray(); var ex = new JsonObject();
            foreach (var p in bodyParams)
            {
                props[p.Key] = new JsonObject { ["type"] = p.Type, ["description"] = ParamText(e, p) };
                if (p.Required) req.Add(p.Key);
                ex[p.Key] = ExampleValue(p);
            }
            var schema = new JsonObject { ["type"] = "object", ["properties"] = props };
            if (req.Count > 0) schema["required"] = req;
            var media = e.BodyKind == "form" ? "application/x-www-form-urlencoded" : "application/json";
            op["requestBody"] = new JsonObject
            {
                ["required"] = req.Count > 0,
                ["content"] = new JsonObject { [media] = new JsonObject { ["schema"] = schema, ["example"] = ex } },
            };
        }

        // responses
        var status = e.Status is >= 200 and <= 299 ? e.Status : (e.Sample is null && e.Write ? 204 : 200);
        var ok = new JsonObject { ["description"] = status == 204 || e.Sample is null ? "Success (the site answers without a body)." : "Success." };
        if (e.Sample is not null && status != 204)
            ok["content"] = new JsonObject
            {
                ["application/json"] = new JsonObject { ["schema"] = SchemaOf(e.Sample), ["example"] = e.Sample.DeepClone() },
            };
        op["responses"] = new JsonObject
        {
            [status.ToString()] = ok,
            ["401"] = new JsonObject { ["description"] = "Not signed in, or a token the site's page normally sends was missing." },
            ["403"] = new JsonObject { ["description"] = "The signed-in user may not do this, or a CSRF token was missing." },
        };

        // security: the session plus every token header this call carries (all of them together)
        var sec = new JsonObject { ["sessionCookie"] = new JsonArray() };
        foreach (var h in e.Live) sec[SecurityKey(h)] = new JsonArray();
        op["security"] = new JsonArray(sec);

        op["x-brain-tool"] = tool;
        op["x-brain-approved"] = e.Approved;
        op["x-brain-read-only"] = !Asks(e);
        op["x-brain-asks-before-call"] = Asks(e);
        return op;
    }

    // =========================================================== Markdown

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string Q(string s) => s.Replace("|", "\\|").Replace("\n", " ");

    /// <summary>A complete, readable reference for the site's endpoints.</summary>
    public static string Markdown(ApiSiteFile site, bool includeDrafts, DateTimeOffset now)
    {
        var eps = Select(site, includeDrafts);
        var sb = new StringBuilder();
        var approved = eps.Count(e => e.Approved);
        sb.AppendLine($"# {site.Site} — API reference");
        sb.AppendLine();
        sb.AppendLine($"> Learned by **BrAIn** from the data requests {site.Site}'s own pages make. Generated {now:yyyy-MM-dd HH:mm}.  ");
        sb.AppendLine($"> {eps.Count} endpoint(s): {approved} approved{(includeDrafts ? $", {eps.Count - approved} draft(s) not yet approved by the user" : "")}.  ");
        sb.AppendLine("> This is a **private, undocumented API**: it can change without notice, and it is only meant to be called as the signed-in user.");
        sb.AppendLine();

        sb.AppendLine("## Overview");
        sb.AppendLine();
        var bases = eps.Select(BaseUrl).Distinct().ToList();
        sb.AppendLine("| | |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| **Site** | `{site.Site}` |");
        sb.AppendLine($"| **Base URL{(bases.Count > 1 ? "s" : "")}** | {(bases.Count == 0 ? "—" : string.Join(", ", bases.Select(b => $"`{b}`")))} |");
        sb.AppendLine("| **Format** | JSON (REST and GraphQL) |");
        var live = eps.SelectMany(e => e.Live).Distinct().OrderBy(x => x).ToList();
        sb.AppendLine($"| **Authentication** | The user's signed-in session (cookies){(live.Count > 0 ? "; plus headers the site's own scripts send: " + string.Join(", ", live.Select(h => $"`{h}`")) : "")} |");
        sb.AppendLine($"| **Read / write** | {eps.Count(e => !Asks(e))} read-only, {eps.Count(Asks)} that change data or touch something sensitive |");
        sb.AppendLine();
        sb.AppendLine("### How these calls are made");
        sb.AppendLine();
        sb.AppendLine("- **Through BrAIn (recommended).** Each approved endpoint is an MCP tool named `api_<site>_<name>`. BrAIn replays the request **inside a tab on the site**, so the cookies, CSRF token and other tokens are the ones the site itself uses; tokens are never stored or shown to the agent.");
        sb.AppendLine("- **Asking first.** Calls that change data, and endpoints with a sensitive-looking path, make BrAIn ask the user before every call.");
        sb.AppendLine("- **Outside BrAIn.** The `curl` examples below show the shape of each request. They will only work with a valid session and the token headers named in each entry, which BrAIn does not give out.");
        if (eps.Any(e => e.GqlOp is not null))
            sb.AppendLine("- **GraphQL.** All operations are sent to one URL; each is documented separately by its operation name.");
        sb.AppendLine();

        sb.AppendLine("## Endpoints");
        sb.AppendLine();
        if (eps.Count == 0) { sb.AppendLine("_Nothing to document yet: approve some endpoints in BrAIn's Site APIs page, or include drafts._"); return sb.ToString(); }
        sb.AppendLine("| Name | Request | Access | Tool |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var e in eps)
            sb.AppendLine($"| [`{e.Name}`](#{Anchor(e)}) | `{(e.GqlOp is not null ? "GraphQL " + e.GqlOp : e.Method + " " + e.Path)}` | {(Asks(e) ? (e.Write ? "writes — asks" : "sensitive — asks") : "read-only")} | `{ApiCompiler.ToolName(site.Site, e.Name)}`{(e.Approved ? "" : " _(draft)_")} |");
        sb.AppendLine();

        foreach (var e in eps) Entry(sb, site, e);

        sb.AppendLine("---");
        sb.AppendLine("_Generated by BrAIn's Site APIs. Sample answers are trimmed and masked (emails, tokens). No credential, cookie or token value is stored or included._");
        return sb.ToString();
    }

    private static string Anchor(ApiEndpoint e) => e.Name.ToLowerInvariant();

    private static void Entry(StringBuilder sb, ApiSiteFile site, ApiEndpoint e)
    {
        var tool = ApiCompiler.ToolName(site.Site, e.Name);
        sb.AppendLine($"### {e.Name}");
        sb.AppendLine();
        sb.AppendLine($"**{Humanize(e.Name)}**{(e.Approved ? "" : " — _draft, not yet approved_")}");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(e.Description)) { sb.AppendLine(e.Description.Trim()); sb.AppendLine(); }
        sb.AppendLine("| | |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| **Request** | `{(e.GqlOp is not null ? "POST" : e.Method)} {BaseUrl(e)}{e.Path}` |");
        if (e.GqlOp is not null) sb.AppendLine($"| **GraphQL** | {(e.Write ? "mutation" : "query")} `{e.GqlOp}` |");
        sb.AppendLine($"| **Access** | {Access(e)} |");
        sb.AppendLine($"| **BrAIn tool** | `{tool}` |");
        if (e.Live.Count > 0) sb.AppendLine($"| **Token headers** | {string.Join(", ", e.Live.Select(h => $"`{h}`"))} — sent by the site's own scripts; values are never stored |");
        if (e.Headers.Count > 0) sb.AppendLine($"| **Other headers** | {string.Join(", ", e.Headers.Select(h => $"`{h.Key}: {Q(h.Value)}`"))} |");
        if (e.Count > 0) sb.AppendLine($"| **Seen** | {e.Count} time(s){(e.First > 0 ? $" ({Stamp(e.First)} – {Stamp(e.Last)})" : "")} |");
        sb.AppendLine();

        // arguments
        if (e.Params.Count > 0)
        {
            sb.AppendLine("#### Arguments");
            sb.AppendLine();
            sb.AppendLine("| Argument | Sent as | Type | Required | Description |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var p in e.Params)
            {
                var sent = p.In switch { "path" => $"`{{{p.Name}}}` in the path", "query" => $"`?{p.Key}=`", "var" => $"`variables.{p.Key}`", _ => e.BodyKind == "form" ? $"form field `{p.Key}`" : $"`body.{p.Key}`" };
                sb.AppendLine($"| `{p.Name}` | {sent} | {p.Type} | {(p.In == "path" || p.Required && p.Default is null ? "yes" : "no")} | {Q(ParamText(e, p))} |");
            }
            sb.AppendLine();
        }

        // request examples
        sb.AppendLine("#### Example");
        sb.AppendLine();
        var args = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var p in e.Params.Where(p => p.Required && p.Default is null || p.In == "path")) args[p.Name] = ExampleValue(p);
        sb.AppendLine("Through BrAIn (as an agent tool call):");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine($"{tool}({new JsonObject(args.Select(kv => KeyValuePair.Create(kv.Key, kv.Value?.DeepClone()))).ToJsonString()})");
        sb.AppendLine("```");
        sb.AppendLine();
        var (plan, _) = ApiCompiler.BuildCall(e, args);
        if (plan is not null)
        {
            sb.AppendLine("The request it sends (`curl` shape):");
            sb.AppendLine();
            sb.AppendLine("```bash");
            var lines = new List<string> { $"curl -X {plan.Method} '{plan.Url}'" };
            foreach (var h in plan.Headers.OrderBy(h => h.Key)) lines.Add($"  -H '{h.Key}: {h.Value.Replace("'", "'\\''")}'");
            foreach (var h in plan.Live) lines.Add($"  -H '{h}: <value the site itself sends>'");
            lines.Add("  --cookie '<signed-in session cookie>'");
            if (plan.Body is not null) lines.Add($"  --data '{plan.Body.Replace("'", "'\\''")}'");
            sb.AppendLine(string.Join(" \\\n", lines));
            sb.AppendLine("```");
            sb.AppendLine();
        }
        if (e.BodyKind == "graphql" && Str(e.BodyConst?["query"]) is { } gq)
        {
            sb.AppendLine("GraphQL document the site sends:");
            sb.AppendLine();
            sb.AppendLine("```graphql");
            sb.AppendLine(gq.Trim());
            sb.AppendLine("```");
            sb.AppendLine();
        }

        // response
        sb.AppendLine("#### Response");
        sb.AppendLine();
        var status = e.Status is >= 200 and <= 299 ? e.Status : (e.Sample is null && e.Write ? 204 : 200);
        sb.AppendLine($"`{status}` — {(e.Sample is null ? "success, no body" : "JSON")}. Other answers: `401` not signed in or a token missing; `403` not allowed or a CSRF token missing.");
        sb.AppendLine();
        if (!string.IsNullOrEmpty(e.Shape)) { sb.AppendLine($"Shape: `{e.Shape}`"); sb.AppendLine(); }
        if (e.Sample is not null)
        {
            var rows = new List<(string Path, string Type)>();
            Flatten(e.Sample, "", 0, rows);
            if (rows.Count > 0)
            {
                sb.AppendLine("| Field | Type |");
                sb.AppendLine("|---|---|");
                foreach (var (path, type) in rows.Take(60)) sb.AppendLine($"| `{path}` | {type} |");
                if (rows.Count > 60) sb.AppendLine($"| … | {rows.Count - 60} more |");
                sb.AppendLine();
            }
            sb.AppendLine("Example (trimmed and masked):");
            sb.AppendLine();
            sb.AppendLine("```json");
            sb.AppendLine(e.Sample.ToJsonString(Pretty));
            sb.AppendLine("```");
            sb.AppendLine();
        }
        if (Asks(e))
        {
            sb.AppendLine($"> **Asks first.** This call {(e.Write ? "changes data" : "reads something sensitive")}; BrAIn asks the user before every call made through an agent.");
            sb.AppendLine();
        }
    }

    private static void Flatten(JsonNode? n, string path, int depth, List<(string, string)> rows)
    {
        if (rows.Count > 80 || depth > 4) return;
        switch (n)
        {
            case JsonObject o:
                foreach (var kv in o)
                {
                    var p = path.Length == 0 ? kv.Key : path + "." + kv.Key;
                    if (kv.Value is JsonObject or JsonArray) { rows.Add((p, kv.Value is JsonArray ? "array" : "object")); Flatten(kv.Value, p, depth + 1, rows); }
                    else rows.Add((p, TypeOf(kv.Value)));
                }
                break;
            case JsonArray a when a.Count > 0:
                if (a[0] is JsonObject or JsonArray) Flatten(a[0], path + "[]", depth + 1, rows);
                else rows.Add((path + "[]", TypeOf(a[0])));
                break;
        }
    }

    private static string TypeOf(JsonNode? n) => n switch
    {
        null => "null",
        JsonValue v when v.TryGetValue<bool>(out _) => "boolean",
        JsonValue v when v.TryGetValue<long>(out _) => "integer",
        JsonValue v when v.TryGetValue<double>(out _) => "number",
        _ => "string",
    };
}
