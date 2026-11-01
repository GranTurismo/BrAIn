using System.Text.Json;
using System.Text.Json.Nodes;
using BrAIn.Mcp;

namespace BrAIn.Browser;

/// <summary>
/// "Every website becomes an API" — the browser side (the compiler and the service live in Mcp/).
///
///   learning   chrome.js/brain.js report the JSON calls a site's own scripts make ({apirec}); only while the
///              user lets BrAIn learn that site, never in Ghost mode or a private tab, and only from the
///              document the tab really shows (a page cannot feed another site's drafts)
///   review     about:apis lists the drafts; approving is possible only from that internal page
///   calling    <see cref="ApiExecAsync"/> replays a request inside a tab on the site's own origin, so the user's
///              cookies and the site's own CSRF/bearer tokens (kept in that page's memory, never here) apply
/// </summary>
public sealed partial class BrowserController
{
    /// <summary>Learned site APIs, exposed to agents as api_* tools once the user approves them.</summary>
    public BrAIn.Mcp.ApiService Apis { get; private set; } = null!;

    private static readonly JsonSerializerOptions ApiUiJson = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ---------------- what pages are told at document start ----------------

    /// <summary>Part of the document-start script (see QcBootScript): which sites this document should watch.
    /// l = sites being learned (never in Ghost mode / private tabs), u = sites with approved endpoints (their pages keep
    /// the live credentials a call needs, in memory only). brain.js reads it and deletes the global before page scripts run.</summary>
    private string ApiBootScript(BrowserTab tab)
    {
        if (Apis is null) return "";
        var learn = NoTrace(tab) ? Array.Empty<string>() : Apis.LearnSites().ToArray();
        var use = Apis.UseSites().ToArray();
        if (learn.Length == 0 && use.Length == 0) return "";
        return "window.__brainApi=" + JsonSerializer.Serialize(new { l = learn, u = use }).Replace("<", "\\u003c") + ";";
    }

    /// <summary>For the document the tab shows now: is it being learned / does it serve approved endpoints?</summary>
    private object ApiPageState(BrowserTab tab, string url)
    {
        var site = ApiSite.KeyOfUrl(url);
        if (Apis is null || site.Length == 0) return new { l = false, u = false };
        return new { l = !NoTrace(tab) && Apis.IsLearning(site), u = Apis.UseSites().Contains(site) };
    }

    /// <summary>The toolbar's view of the active tab's site: its name, whether it is being learned, how much was learned.</summary>
    private object? ApiUiState(BrowserTab? tab)
    {
        if (tab is null || Apis is null) return null;
        var site = ApiSite.KeyOfUrl(tab.LastUrl);
        if (site.Length == 0) return null;
        var (n, drafts, approved) = Apis.Counts(site);
        return new { site, learn = Apis.IsLearning(site), n, d = drafts, a = approved, t = Apis.TotalDrafts() };
    }

    /// <summary>The learn/use lists changed, or a site was switched on or off: every tab gets a fresh boot script
    /// and a push for the page it shows now.</summary>
    internal void ApiLearnChanged(string site)
    {
        try { QcChanged(false); } catch { }
        PushChromeStateAll();
        ScheduleApisPush();
    }

    // ---------------- a report from a page ----------------

    private void ApiOnReport(BrowserTab tab, string arg)
    {
        if (_noToolbar || tab.IsClosed || NoTrace(tab) || Apis is null) return; // Ghost mode and private tabs: nothing is learned
        var rec = ApiService.ParseReport(arg);
        if (rec is null) return;
        // The report must come from the document this tab really shows, on the page's own origin: a page can feed
        // the drafts of its own site and no other.
        var url = QcUrl(tab);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var doc) || !Uri.TryCreate(rec.Origin, UriKind.Absolute, out var claimed)) return;
        if (!string.Equals(doc.GetLeftPart(UriPartial.Authority), claimed.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase)) return;
        Apis.Record(rec);
    }

    // ---------------- the toolbar's "Learn this site's API" ----------------

    private void ApiUiToggleLearn(BrowserTab tab)
    {
        var active = Tabs.Active;
        var site = ApiSite.KeyOfUrl(active?.LastUrl);
        if (site.Length == 0 || Apis is null) return;
        if (_ghost) { Log("api learn: not in Ghost mode"); return; }
        var on = !Apis.IsLearning(site);
        Apis.SetLearn(site, on);
        ApiLearnChanged(site);
        // the page's own first requests were made before learning started: load it again so they are seen
        if (on && active is not null && !active.IsClosed && !MessageTrust.IsInternalUrl(active.LastUrl))
            _ = Task.Run(async () => { try { await ReloadAsync(false, active); } catch (Exception ex) { Log("api learn reload failed: " + ex.Message); } });
    }

    // ---------------- about:apis ----------------

    private (bool Ok, string Text, long T, object? Files)? _apiNotice;
    private int _apisPushPending;

    private string ApisStateJson()
    {
        var sites = Apis.Sites();
        var tools = new Dictionary<string, string>();
        foreach (var s in sites)
            foreach (var e in s.Endpoints.Where(e => e.Approved))
                tools[e.Id] = Apis.RegisteredName(s.Site, e.Id) ?? ApiService.ToolName(s.Site, e.Name);
        var active = ApiSite.KeyOfUrl(Tabs.All.FirstOrDefault(t => !t.IsClosed && !MessageTrust.IsInternalUrl(t.LastUrl) && ApiSite.KeyOfUrl(t.LastUrl).Length > 0 && t.Id == Tabs.Active?.Id)?.LastUrl);
        return JsonSerializer.Serialize(new
        {
            b = 100,
            sites,
            tools,
            belt = Belt.Mode,
            ghost = _ghost,
            active,
            notice = _apiNotice is { } n ? new { ok = n.Ok, text = n.Text, t = n.T, files = n.Files } : null,
        }, ApiUiJson);
    }

    private void PushApisState(BrowserTab tab)
    {
        if (tab.IsClosed || Apis is null) return;
        PushJson(tab, ApisStateJson());
    }

    private string BuildApisHtml()
        => Assets.ApisHtml.Replace("__BRAIN_APIS_JSON__", ApisStateJson().Replace("<", "\\u003c"));

    /// <summary>Debounced: a burst of learned calls becomes one push to the open page and one toolbar refresh.</summary>
    private void ScheduleApisPush()
    {
        if (Interlocked.Exchange(ref _apisPushPending, 1) == 1) return;
        _ = Task.Delay(450).ContinueWith(_ =>
        {
            Interlocked.Exchange(ref _apisPushPending, 0);
            try
            {
                if (Tabs.Active is { IsClosed: false } t && t.LastUrl == "about:apis") PushApisState(t);
                PushChromeStateAll();
            }
            catch { }
        }, TaskScheduler.Default);
    }

    private void ApiUiNotice(BrowserTab tab, bool ok, string text, object? files = null)
    {
        _apiNotice = (ok, text, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), files);
        PushJson(tab, JsonSerializer.Serialize(new { b = 101, ok, text, t = _apiNotice.Value.T, files }, ApiUiJson));
    }

    private sealed record ApiReq(string? Site, string? Id, bool? On, ApiEndpoint? Ep, bool? Openapi, bool? Markdown, bool? Drafts);

    /// <summary>Writes the chosen documents for a site into Downloads (where they also show up on the downloads shelf):
    /// an OpenAPI 3 file and/or a full Markdown reference, for the approved endpoints or all of them.</summary>
    private void ApiUiExport(BrowserTab tab, ApiReq req)
    {
        var site = req.Site ?? "";
        var file = Apis.Site(site);
        if (file is null) { ApiUiNotice(tab, false, "That site is not known any more."); return; }
        if (req.Openapi != true && req.Markdown != true) { ApiUiNotice(tab, false, "Tick OpenAPI and/or the Markdown documentation first."); return; }
        var drafts = req.Drafts == true;
        var n = ApiDocs.Select(file, drafts).Count;
        if (n == 0)
        {
            ApiUiNotice(tab, false, drafts ? "Nothing has been learned for this site yet." : "No endpoint of this site is approved yet. Approve some, or include the drafts.");
            return;
        }
        var now = DateTimeOffset.Now;
        var saved = new List<object>();
        try
        {
            void Save(string name, string text)
            {
                var item = Downloads.Begin("about:apis");
                var path = Downloads.DestinationFor(item, name);
                File.WriteAllText(path, text, new System.Text.UTF8Encoding(false));
                Downloads.Finish(item, path);
                saved.Add(new { id = item.Id, name = Path.GetFileName(path) });
            }
            if (req.Openapi == true) Save($"{site}-openapi.json", ApiDocs.OpenApi(file, drafts, now));
            if (req.Markdown == true) Save($"{site}-api.md", ApiDocs.Markdown(file, drafts, now));
        }
        catch (Exception ex) { ApiUiNotice(tab, false, "Could not write the file: " + ex.Message); return; }
        ApiUiNotice(tab, true, $"Saved {saved.Count} file{(saved.Count == 1 ? "" : "s")} to your Downloads folder ({n} endpoint{(n == 1 ? "" : "s")}{(drafts ? ", drafts included" : "")}).", saved);
    }

    /// <summary>One verb from the about:apis page (all of them internal-only: a web page cannot approve its own endpoints).</summary>
    private void ApiUiVerb(BrowserTab tab, string verb, string arg)
    {
        if (Apis is null) return;
        ApiReq? req = null;
        if (arg.Length > 0)
            try { req = JsonSerializer.Deserialize<ApiReq>(arg, ApiUiJson); } catch { ApiUiNotice(tab, false, "That did not make sense."); return; }
        var site = req?.Site ?? "";
        switch (verb)
        {
            case "apiget": break;
            case "apisave":
                {
                    var err = req?.Ep is null ? "Nothing to save." : Apis.Save(site, req.Ep);
                    ApiUiNotice(tab, err is null, err ?? "Saved.");
                    break;
                }
            case "apiapprove":
                {
                    var err = Apis.SetApproved(site, req?.Id ?? "", req?.On == true);
                    ApiUiNotice(tab, err is null, err ?? (req?.On == true ? "Approved. Agents can now call it as an api_* tool." : "Switched off."));
                    break;
                }
            case "apidel": Apis.Delete(site, req?.Id ?? ""); break;
            case "apisite": Apis.SetSiteEnabled(site, req?.On == true); break;
            case "apisetlearn":
                {
                    if (req?.On == true && _ghost) { ApiUiNotice(tab, false, "Ghost mode is on: nothing is learned in Ghost mode."); break; }
                    var err = Apis.SetLearn(site, req?.On == true);
                    ApiLearnChanged(site);
                    if (err is not null) ApiUiNotice(tab, false, err);
                    break;
                }
            case "apisitedel": Apis.DeleteSite(site); ApiLearnChanged(site); break;
            case "apiexport": if (req is not null) ApiUiExport(tab, req); break;
            case "apicleardrafts": Apis.ClearDrafts(site); break;
        }
        PushApisState(tab);
    }

    // ---------------- calling ----------------

    /// <summary>A tab already showing the endpoint's origin (the active one first), or null.</summary>
    private BrowserTab? ApiFindTab(string origin)
    {
        bool On(BrowserTab t) => !t.IsClosed && Uri.TryCreate(t.LastUrl, UriKind.Absolute, out var u)
            && string.Equals(u.GetLeftPart(UriPartial.Authority), origin, StringComparison.OrdinalIgnoreCase);
        var act = Tabs.ActiveExact;
        if (act is not null && On(act)) return act;
        return Tabs.All.Where(On).OrderByDescending(t => t.HasView).ThenByDescending(t => t.LastActiveUtc).FirstOrDefault();
    }

    /// <summary>Makes the request inside a tab on the site's own origin (cookies, CSRF, CORS all behave as for the site's
    /// own scripts). Uses a tab the user already has open on that origin; opens a helper tab when there is none, or when the
    /// open one has not yet seen the tokens the call needs (a page only keeps them from the moment it was told to watch,
    /// so a fresh page load is how it learns them). The helper tab is closed again; the user's own tabs are never reloaded.</summary>
    internal async Task<ExecResult> ApiExecAsync(ApiEndpoint ep, ApiCompiler.CallPlan plan, CancellationToken ct)
    {
        var tab = ApiFindTab(ep.Origin);
        BrowserTab? opened = null;
        Guid? back = null;
        async Task OpenHelperAsync()
        {
            back = Tabs.ActiveExact?.Id;
            opened = await NewTabAsync(ep.Origin + "/", ct);
            await Task.Delay(1800, ct); // the site's own start-up requests tell the page which tokens it uses
        }
        if (tab is null) { await OpenHelperAsync(); tab = opened; }
        try
        {
            var spec = JsonSerializer.Serialize(new { method = plan.Method, url = plan.Url, headers = plan.Headers, live = plan.Live, body = plan.Body, max = 400_000 })
                .Replace("\u2028", "\\u2028").Replace("\u2029", "\\u2029");
            async Task<ExecResult> RunInAsync(BrowserTab t)
            {
                await DriverOrThrowAsync(t, ct);
                await EnsurePageHelpersAsync(t);
                return ParseExec(await EvaluateAsync("window.__brain.apiCall(" + spec + ")", true, t, ct));
            }
            var res = await RunInAsync(tab!);
            if (!res.Ok && res.Missing.Count > 0)
            {
                if (opened is null) { await OpenHelperAsync(); tab = opened; }
                else await Task.Delay(2500, ct);
                res = await RunInAsync(tab!);
            }
            return res;
        }
        finally
        {
            if (opened is not null)
                try
                {
                    await CloseWindowAsync(opened.Id.ToString("N")[..8], CancellationToken.None);
                    if (back is { } b && Tabs.Find(b) is { IsClosed: false }) await ActivateWindowAsync(b.ToString("N")[..8], CancellationToken.None);
                }
                catch (Exception ex) { Log("api call: closing the helper tab failed: " + ex.Message); }
        }
    }

    private static ExecResult ParseExec(JsonElement? el)
    {
        var r = new ExecResult();
        if (el is not { ValueKind: JsonValueKind.Object } o) { r.Error = "the page did not answer"; return r; }
        var v = o;
        r.Ok = v.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;
        r.Status = v.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Number ? st.GetInt32() : 0;
        r.ContentType = v.TryGetProperty("ct", out var ct) && ct.ValueKind == JsonValueKind.String ? ct.GetString() ?? "" : "";
        r.Text = v.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String ? tx.GetString() ?? "" : "";
        r.Truncated = v.TryGetProperty("truncated", out var tr) && tr.ValueKind == JsonValueKind.True;
        r.Error = v.TryGetProperty("error", out var er) && er.ValueKind == JsonValueKind.String ? er.GetString() : null;
        if (v.TryGetProperty("missing", out var ms) && ms.ValueKind == JsonValueKind.Array)
            foreach (var m in ms.EnumerateArray()) if (m.ValueKind == JsonValueKind.String) r.Missing.Add(m.GetString() ?? "");
        return r;
    }
}
