using System.Text.Json;

namespace BrAIn.Browser;

/// <summary>
/// Developer tools beyond the panel's own feed: reading the recorded network traffic for a tab (the panel and the MCP tools
/// share it) and exporting it as HAR.
/// </summary>
public sealed partial class BrowserController
{
    private bool _agentNetRec;

    /// <summary>The recorder runs while the DevTools panel is open or an agent asked for it (browser_network_record).</summary>
    private void UpdateNetRecorder() => SetNetRecorder(_dtOpen || _agentNetRec);

    /// <summary>MCP: switch recording on or off independently of the panel. Returns whether it is on now.</summary>
    public bool SetAgentNetworkRecording(bool on)
    {
        _agentNetRec = on;
        UpdateNetRecorder();
        return _netRecOn;
    }

    public bool NetworkRecording => _netRecOn;

    /// <summary>Reads what the page's recorder has seen since the last read (starting it in a page that has none yet).</summary>
    public async Task PullNetworkNowAsync(BrowserTab tab, CancellationToken ct = default)
    {
        var d = (DocTab ?? tab).Driver ?? _driverTab?.Driver;
        if (d is not null) await d.PullNetworkAsync(ct);
    }

    /// <summary>The requests the panel would show for this tab: its newest document's, or all of them with "Preserve log".</summary>
    public IReadOnlyList<NetworkEntry> NetworkFor(BrowserTab tab)
    {
        var id = tab.Id.ToString("N")[..8];
        return _network.Values.Where(n => NetVisible(n, id, tab.NetDoc)).OrderBy(n => n.Time).ToArray();
    }

    public NetworkEntry? NetworkEntryById(string id)
        => _network.TryGetValue(id, out var n) ? n
            : _network.Values.Where(e => e.RequestId.StartsWith(id, StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.Time).FirstOrDefault();

    // ---------------------------------------------------------------- HAR

    private static string HttpVersion(string? proto) => proto switch
    {
        "h2" => "HTTP/2.0", "h3" => "HTTP/3.0", "http/1.0" => "HTTP/1.0", _ => "HTTP/1.1",
    };

    private static object[] HarHeaders(Dictionary<string, string>? h)
        => h is null ? Array.Empty<object>() : h.Select(kv => (object)new { name = kv.Key, value = kv.Value }).ToArray();

    /// <summary>HAR 1.2 (what Chrome's "Save all as HAR" writes) of the given requests. Bodies are the recorded text responses.</summary>
    public string BuildHar(IReadOnlyList<NetworkEntry> entries, string pageUrl, string? pageTitle)
    {
        var first = entries.FirstOrDefault();
        var entriesJson = entries.Select(n =>
        {
            var x = n.X;
            Uri.TryCreate(n.Url, UriKind.Absolute, out var uri);
            var query = uri is null || uri.Query.Length < 2 ? Array.Empty<object>() : uri.Query[1..].Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p =>
            {
                var i = p.IndexOf('=');
                return (object)new { name = Uri.UnescapeDataString((i < 0 ? p : p[..i]).Replace('+', ' ')), value = i < 0 ? "" : Uri.UnescapeDataString(p[(i + 1)..].Replace('+', ' ')) };
            }).ToArray();
            string? reqType = null;
            n.RequestHeaders?.TryGetValue("content-type", out reqType);
            var t = x?.Timing;
            double total = x?.DurationMs ?? 0;
            return (object)new
            {
                pageref = "page_1",
                startedDateTime = new DateTimeOffset(n.Time).ToString("o"),
                time = Math.Round(total, 1),
                request = new
                {
                    method = n.Method, url = n.Url, httpVersion = HttpVersion(x?.Protocol),
                    cookies = Array.Empty<object>(), headers = HarHeaders(n.RequestHeaders), queryString = query,
                    postData = n.PostData is null ? null : new { mimeType = reqType ?? "text/plain", text = n.PostData },
                    headersSize = -1, bodySize = n.PostData?.Length ?? 0,
                },
                response = new
                {
                    status = n.Status ?? 0, statusText = x?.StatusText ?? "", httpVersion = HttpVersion(x?.Protocol),
                    cookies = Array.Empty<object>(), headers = HarHeaders(n.ResponseHeaders),
                    content = new
                    {
                        size = x is { DecodedBytes: > 0 } ? x.DecodedBytes : (x?.BodyLength ?? n.BytesReceived),
                        mimeType = x?.Mime ?? "", text = x?.ResponseBody,
                    },
                    redirectURL = x?.Redirected == true ? x.FinalUrl ?? "" : "", headersSize = -1, bodySize = n.BytesReceived,
                },
                cache = new { },
                timings = new
                {
                    blocked = t is null ? -1 : t.Queue, dns = t is null ? -1 : t.Dns,
                    connect = t is null ? -1 : t.Connect, ssl = t is null ? -1 : t.Tls,
                    send = 0, wait = t is null ? total : t.Wait, receive = t is null ? 0 : t.Receive,
                },
                _resourceType = n.ResourceType,
                _error = n.Error,
            };
        }).ToArray();
        return JsonSerializer.Serialize(new
        {
            log = new
            {
                version = "1.2",
                creator = new { name = "BrAIn", version = typeof(BrowserController).Assembly.GetName().Version?.ToString(3) ?? "" },
                pages = new[] { new { startedDateTime = new DateTimeOffset(first?.Time ?? DateTime.Now).ToString("o"), id = "page_1", title = pageTitle ?? pageUrl, pageTimings = new { } } },
                entries = entriesJson,
            },
        }, new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
    }

    /// <summary>Writes the tab's recorded requests as a .har file in Downloads (and lists it on the downloads shelf).</summary>
    public async Task<(string Path, int Count)?> SaveHarAsync(BrowserTab tab, string? path = null)
    {
        var entries = NetworkFor(tab);
        if (entries.Count == 0) return null;
        var host = Uri.TryCreate(tab.LastUrl, UriKind.Absolute, out var u) && u.Host.Length > 0 ? u.Host : "page";
        var json = BuildHar(entries, tab.LastUrl ?? "", tab.LastTitle);
        if (path is null)
        {
            var item = Downloads.Begin(tab.LastUrl);
            path = Downloads.DestinationFor(item, $"{host} {DateTime.Now:yyyy-MM-dd 'at' HH.mm.ss}.har");
            await File.WriteAllTextAsync(path, json);
            Downloads.Finish(item, path);
        }
        else await File.WriteAllTextAsync(path, json);
        Log($"HAR saved: {path} ({entries.Count} requests)");
        return (path, entries.Count);
    }

    private async Task ExportHarAsync(BrowserTab tab)
    {
        try { await SaveHarAsync(DocTab ?? tab); }
        catch (Exception ex) { Log($"HAR export failed: {ex.Message}"); }
    }
}
