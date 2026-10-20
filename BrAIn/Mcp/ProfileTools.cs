using System.ComponentModel;
using System.Text.Json;
using BrAIn.Browser;
using ModelContextProtocol.Server;

namespace BrAIn.Mcp;

/// <summary>
/// MCP tools for the user's browser data and chrome: bookmarks, history,
/// downloads, tab pin/mute/move, clipboard, find-in-page, page saving,
/// emulation and import. Saved passwords and cookie/password import are
/// deliberately absent.
/// </summary>
[McpServerToolType]
public sealed class ProfileTools(BrowserController browser)
{
    private BrowserTab ResolveTab(string? idOrPrefix)
        => idOrPrefix is null
            ? browser.ActiveTabOrThrow()
            : browser.Tabs.FindByPrefix(idOrPrefix)
              ?? throw new InvalidOperationException($"No tab matching '{idOrPrefix}'");

    private static string Line(string url, string? title) => $"{(string.IsNullOrWhiteSpace(title) ? "(untitled)" : title)}  <{url}>";

    // ============================================================ BOOKMARKS

    [McpServerTool(Name = "browser_bookmark"), Description("Manage bookmarks (favourites). add / remove / toggle act on 'url', or on the page open in the tab when no url is given; check says whether it is bookmarked; list shows bookmarks, newest first, optionally filtered by 'query'. Only http(s) pages in non-private tabs can be bookmarked.")]
    public Task<string> Bookmark(
        [Description("add | remove | toggle | check | list")] string action = "list",
        [Description("Page URL (default: URL of the tab)")] string? url = null,
        [Description("Title to store when adding (default: the page's title)")] string? title = null,
        [Description("list: only bookmarks whose title or URL contain all these words")] string? query = null,
        [Description("list: maximum entries (default 50)")] int limit = 50,
        [Description("Tab id/prefix, used when no url is given")] string? tab = null)
    {
        var act = action.ToLowerInvariant();
        if (act == "list")
        {
            var rows = browser.ListBookmarks(query, limit);
            return Task.FromResult(rows.Count == 0
                ? "(no bookmarks)"
                : $"{rows.Count} bookmark(s):\n" + string.Join('\n', rows.Select(r => Line(r.Url, r.Title))));
        }
        if (act is not ("add" or "remove" or "toggle" or "check"))
            return Task.FromResult($"unknown action '{action}' (add | remove | toggle | check | list)");

        BrowserTab? t = null;
        if (url is null)
        {
            t = ResolveTab(tab);
            if (t.IsPrivate && act is "add" or "toggle") return Task.FromResult("refusing to bookmark a private tab");
            url = t.LastUrl;
            title ??= t.LastTitle;
        }
        switch (act)
        {
            case "check":
                return Task.FromResult(browser.IsBookmarked(url) ? $"bookmarked: {url}" : $"not bookmarked: {url}");
            case "remove":
                return Task.FromResult(browser.RemoveBookmark(url) ? $"removed bookmark {url}" : $"no bookmark for {url}");
            case "toggle":
                if (browser.RemoveBookmark(url)) return Task.FromResult($"removed bookmark {url}");
                goto default;
            default:
                try
                {
                    return Task.FromResult(browser.AddBookmark(url, title) ? $"bookmarked {url}" : $"already bookmarked: {url}");
                }
                catch (ArgumentException ex) { return Task.FromResult(ex.Message); }
        }
    }

    // ============================================================= HISTORY

    [McpServerTool(Name = "browser_history"), Description("Read or edit the browsing history (private tabs never record any). list shows the most recent pages; search filters by words in the title or URL; delete removes one 'url' or every entry matching 'query'; clear wipes everything and needs confirm=true.")]
    public Task<string> History(
        [Description("list | search | delete | clear")] string action = "list",
        [Description("search/delete: words that must all appear in the title or URL")] string? query = null,
        [Description("delete: exact URL to forget")] string? url = null,
        [Description("list/search: maximum entries (default 25)")] int limit = 25,
        [Description("clear: must be true")] bool confirm = false)
    {
        switch (action.ToLowerInvariant())
        {
            case "list":
            case "search":
                {
                    var rows = browser.SearchHistory(action.Equals("search", StringComparison.OrdinalIgnoreCase) ? query : null, limit);
                    if (rows.Count == 0) return Task.FromResult("(no history)");
                    return Task.FromResult($"{rows.Count} entr{(rows.Count == 1 ? "y" : "ies")}:\n" + string.Join('\n', rows.Select(h =>
                        $"{DateTimeOffset.FromUnixTimeSeconds(h.Time).LocalDateTime:yyyy-MM-dd HH:mm}  x{h.Visits}  {Line(h.Url, h.Title)}")));
                }
            case "delete":
                {
                    if (url is not null)
                    {
                        var n = browser.ForgetHistoryUrl(url);
                        return Task.FromResult(n > 0 ? $"deleted {url} from history" : $"{url} is not in history");
                    }
                    if (string.IsNullOrWhiteSpace(query)) return Task.FromResult("delete needs 'url' or 'query'");
                    return Task.FromResult($"deleted {browser.ForgetHistoryMatching(query)} history entr(ies) matching '{query}'");
                }
            case "clear":
                if (!confirm) return Task.FromResult("clear erases the whole history; call again with confirm=true to do it");
                return Task.FromResult($"cleared {browser.ClearAllHistory()} history entr(ies)");
            default:
                return Task.FromResult($"unknown action '{action}' (list | search | delete | clear)");
        }
    }

    // ========================================================== DOWNLOADS

    private static string DlState(Downloads.Item i) => i.State switch
    {
        0 => i.Total > 0 ? $"downloading {i.Received * 100 / i.Total}%" : $"downloading {i.Received / 1024} KB",
        1 => "done",
        2 => "failed" + (i.Error is { Length: > 0 } ? $": {i.Error}" : ""),
        _ => "canceled",
    };

    private static string DlLine(Downloads.Item i)
        => $"#{i.Id}  {DlState(i)}  {i.Name}  {(i.Path.Length > 0 ? i.Path : i.Url)}";

    [McpServerTool(Name = "browser_downloads"), Description("Inspect and control downloads. Files land in the Downloads folder. list shows them with state and path; wait blocks until download 'id' (default: the newest) finishes; cancel stops a running one; reveal shows the file in Finder/Explorer; clear forgets finished ones (files stay on disk). Downloaded files are never opened from here.")]
    public async Task<string> DownloadsTool(
        [Description("list | wait | cancel | reveal | clear")] string action = "list",
        [Description("Download id from list (default: newest)")] string? id = null,
        [Description("wait: seconds to wait (default 60, max 600)")] int timeoutSeconds = 60,
        CancellationToken ct = default)
    {
        var dl = browser.Downloads;
        var act = action.ToLowerInvariant();
        if (act == "list")
        {
            var items = dl.Items;
            return items.Count == 0 ? "(no downloads)" : string.Join('\n', items.Select(DlLine));
        }
        if (act == "clear")
        {
            dl.ClearDone();
            return "cleared finished downloads from the list";
        }
        if (act is not ("wait" or "cancel" or "reveal"))
            return $"unknown action '{action}' (list | wait | cancel | reveal | clear)";

        var item = id is null ? dl.Items.FirstOrDefault() : dl.Find(id.TrimStart('#'));
        if (item is null) return id is null ? "(no downloads)" : $"no download '{id}'";
        switch (act)
        {
            case "cancel":
                if (item.State != 0) return $"#{item.Id} is not running ({DlState(item)})";
                dl.Cancel(item.Id);
                return $"canceled #{item.Id}";
            case "reveal":
                if (item.Path.Length == 0) return $"#{item.Id} has no file yet";
                dl.Reveal(item.Id);
                return $"revealed {item.Path}";
            default:
                var end = DateTime.UtcNow.AddSeconds(Math.Clamp(timeoutSeconds, 1, 600));
                while (item.State == 0 && DateTime.UtcNow < end)
                    await Task.Delay(300, ct);
                return item.State == 0 ? "still running: " + DlLine(item) : DlLine(item);
        }
    }

    // =============================================================== TABS

    [McpServerTool(Name = "browser_tab"), Description("Change a tab beyond navigating it: pin / unpin (pinned tabs sit at the left and resist closing), mute / unmute, duplicate, move to a position, or reopen the last closed tab. 'id' is a tab id or prefix from browser_tabs (default: active tab).")]
    public async Task<string> TabAction(
        [Description("pin | unpin | mute | unmute | duplicate | move | reopen")] string action,
        [Description("Tab id or prefix (default: active tab)")] string? id = null,
        [Description("move: new position in the tab strip, 0 = leftmost")] int? index = null,
        CancellationToken ct = default)
    {
        var act = action.ToLowerInvariant();
        if (act == "reopen")
        {
            var re = await browser.ReopenClosedTabAsync(ct);
            return re is null ? "no closed tab to reopen" : $"reopened tab {re.Id.ToString()[..8]} → {re.LastUrl}";
        }
        var t = ResolveTab(id);
        var tid = t.Id.ToString()[..8];
        switch (act)
        {
            case "pin": browser.SetTabPinned(t, true); return $"pinned {tid}";
            case "unpin": browser.SetTabPinned(t, false); return $"unpinned {tid}";
            case "mute": browser.SetTabMuted(t, true); return $"muted {tid}";
            case "unmute": browser.SetTabMuted(t, false); return $"unmuted {tid}";
            case "duplicate":
                var d = await browser.DuplicateTabAsync(t, ct);
                return $"duplicated {tid} → new tab {d.Id.ToString()[..8]} ({d.LastUrl})";
            case "move":
                if (index is null) return "move needs 'index'";
                return browser.MoveTab(t, index.Value) ? $"moved {tid} to position {browser.Tabs.IndexOf(t.Id)}" : $"{tid} is already there";
            default:
                return $"unknown action '{action}' (pin | unpin | mute | unmute | duplicate | move | reopen)";
        }
    }

    // ========================================================== CLIPBOARD

    private const int ClipboardReadCap = 20_000;

    [McpServerTool(Name = "browser_clipboard"), Description("Read or replace the system clipboard text, to hand text between the browser and other apps. The clipboard may hold something the user copied for another purpose (a password, say), so read it only when the task is about it. Contents are not written to the activity log.")]
    public Task<string> Clipboard(
        [Description("read | write")] string action = "read",
        [Description("write: the text to place on the clipboard")] string? text = null)
    {
        switch (action.ToLowerInvariant())
        {
            case "read":
                var t = browser.ReadClipboard();
                if (string.IsNullOrEmpty(t)) return Task.FromResult("(clipboard has no text)");
                return Task.FromResult(t.Length > ClipboardReadCap ? t[..ClipboardReadCap] + $"\n… (truncated, {t.Length} chars)" : t);
            case "write":
                if (text is null) return Task.FromResult("write needs 'text'");
                return Task.FromResult(browser.WriteClipboard(text) ? $"clipboard set ({text.Length} chars)" : "could not write the clipboard");
            default:
                return Task.FromResult($"unknown action '{action}' (read | write)");
        }
    }

    // ======================================================= FIND IN PAGE

    [McpServerTool(Name = "browser_find_in_page"), Description("Find text on the page the way Ctrl+F does: counts the visible matches and shows a snippet around each, and can select and scroll to the next/previous match. Different from browser_find, which looks up elements.")]
    public async Task<string> FindInPage(
        [Description("Text to look for")] string text,
        [Description("Match case (default false)")] bool caseSensitive = false,
        [Description("Move the selection and scroll to a match: none | next | prev (default none)")] string go = "none",
        [Description("Maximum snippets to return (default 10)")] int maxSnippets = 10,
        [Description("Tab id/prefix")] string? tab = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(text)) return "text is empty";
        var t = ResolveTab(tab);
        var script = "(function(q,cs,go,max){" +
            "var body=document.body; if(!body) return JSON.stringify({count:0,snippets:[]});" +
            "var txt=body.innerText||''; var hay=cs?txt:txt.toLowerCase(); var needle=cs?q:q.toLowerCase();" +
            "var count=0, snippets=[], i=hay.indexOf(needle);" +
            "while(i>=0){ count++; if(snippets.length<max){ var s=Math.max(0,i-40), e=Math.min(txt.length,i+needle.length+40);" +
            " snippets.push(txt.slice(s,e).replace(/\\s+/g,' ').trim()); } i=hay.indexOf(needle,i+Math.max(1,needle.length)); }" +
            "var sel=null; if(go==='next'||go==='prev'){ try{ sel=window.find(q,cs,go==='prev',true,false,false,false); }catch(e){ sel=false; } }" +
            "return JSON.stringify({count:count,snippets:snippets,selected:sel});" +
            "})(" + JsonSerializer.Serialize(text) + "," + (caseSensitive ? "true" : "false") + "," +
            JsonSerializer.Serialize(go.ToLowerInvariant()) + "," + Math.Clamp(maxSnippets, 0, 100) + ")";
        var raw = await browser.EvaluateTextAsync(script, t, ct);
        try
        {
            using var doc = JsonDocument.Parse(raw ?? "{}");
            var r = doc.RootElement;
            var count = r.GetProperty("count").GetInt32();
            if (count == 0) return $"0 matches for \"{text}\"";
            var lines = new List<string> { $"{count} match(es) for \"{text}\"" };
            if (r.TryGetProperty("selected", out var sel) && sel.ValueKind is JsonValueKind.True or JsonValueKind.False)
                lines.Add(sel.GetBoolean() ? $"selected the {go} match" : "no further match to select");
            var n = 0;
            foreach (var s in r.GetProperty("snippets").EnumerateArray()) lines.Add($"  {++n}. …{s.GetString()}…");
            return string.Join('\n', lines);
        }
        catch (JsonException) { return raw ?? "(no result)"; }
    }

    // ============================================================ SAVE PAGE

    [McpServerTool(Name = "browser_save_page"), Description("Save the page's current DOM (after scripts ran) as a single .html file, with a <base> tag so relative links and images keep loading from the original site. Not an offline copy. Without outputPath it goes to the Downloads folder named after the page title; a relative path is also placed there. Existing files are not overwritten unless overwrite=true.")]
    public async Task<string> SavePage(
        [Description("File path to write (default: Downloads/<title>.html)")] string? outputPath = null,
        [Description("Replace an existing file")] bool overwrite = false,
        [Description("Tab id/prefix")] string? tab = null,
        CancellationToken ct = default)
    {
        var t = ResolveTab(tab);
        if (!t.LastUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return "only http(s) pages can be saved";
        const string script = "(function(){var d=document;" +
            "var dt=d.doctype?'<!DOCTYPE '+d.doctype.name+(d.doctype.publicId?' PUBLIC \"'+d.doctype.publicId+'\"':'')+(d.doctype.systemId?' \"'+d.doctype.systemId+'\"':'')+'>':'<!DOCTYPE html>';" +
            "var c=d.documentElement.cloneNode(true); c.querySelectorAll('[id^=\"__brain\"]').forEach(function(e){e.remove();});" +
            "var html=c.outerHTML;" +
            "if(!d.querySelector('base')) html=html.replace(/<head([^>]*)>/i,function(m){return m+'<base href=\"'+location.href.replace(/\"/g,'&quot;')+'\">';});" +
            "return JSON.stringify({title:d.title||'',html:dt+'\\n'+html});})()";
        var raw = await browser.EvaluateTextAsync(script, t, ct);
        string title, html;
        try
        {
            using var doc = JsonDocument.Parse(raw ?? "");
            title = doc.RootElement.GetProperty("title").GetString() ?? "";
            html = doc.RootElement.GetProperty("html").GetString() ?? "";
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException)
        {
            return "could not read the page DOM";
        }

        string path;
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            var name = string.Concat((title.Length > 0 ? title : new Uri(t.LastUrl).Host)
                .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
            if (name.Length > 80) name = name[..80];
            path = Path.Combine(browser.Downloads.Dir, name + ".html");
        }
        else path = Path.IsPathRooted(outputPath) ? outputPath : Path.Combine(browser.Downloads.Dir, outputPath);
        if (File.Exists(path) && !overwrite) return $"{path} already exists; pass overwrite=true or another outputPath";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, html, ct);
        return $"saved {t.LastUrl} to {path} ({html.Length / 1024} KB)";
    }

    // ============================================================ EMULATE

    [McpServerTool(Name = "browser_emulate"), Description("Override how the tab presents itself: userAgent, colorScheme (light/dark), offline, geolocation. Windows (CDP) supports all of them live; macOS (WebKit) supports userAgent and colorScheme, applied on the next navigation, and reports the others as unsupported. reset=true undoes every override first. Overrides stay until reset or the tab closes.")]
    public async Task<string> Emulate(
        [Description("Full user-agent string")] string? userAgent = null,
        [Description("light | dark (prefers-color-scheme)")] string? colorScheme = null,
        [Description("true = no network, false = back online")] bool? offline = null,
        [Description("Latitude, with longitude")] double? latitude = null,
        [Description("Longitude, with latitude")] double? longitude = null,
        [Description("Undo all overrides first")] bool reset = false,
        [Description("Tab id/prefix")] string? tab = null,
        CancellationToken ct = default)
    {
        if (colorScheme is not null && colorScheme.ToLowerInvariant() is not ("light" or "dark"))
            return "colorScheme must be 'light' or 'dark'";
        if ((latitude is null) != (longitude is null)) return "give both latitude and longitude";
        var t = ResolveTab(tab);
        var driver = await browser.DriverOrThrowAsync(t, ct);
        return await driver.EmulateAsync(new Emulation(userAgent, colorScheme?.ToLowerInvariant(), offline, latitude, longitude, reset), ct);
    }

    // ============================================================= IMPORT

    [McpServerTool(Name = "browser_import"), Description("Bring bookmarks and history over from another browser installed on this machine. scan lists the importable browsers and what each holds; run imports from one of them (bookmarks and history only; cookies and saved passwords can only be imported by the user on about:import). Existing entries are kept, duplicates are merged.")]
    public async Task<string> Import(
        [Description("scan | run")] string action = "scan",
        [Description("run: source id from scan")] string? source = null,
        [Description("run: import bookmarks (default true)")] bool bookmarks = true,
        [Description("run: import history (default true)")] bool history = true,
        CancellationToken ct = default)
    {
        switch (action.ToLowerInvariant())
        {
            case "scan":
                var found = await browser.ScanImportSourcesAsync();
                if (found.Count == 0) return "no other browsers found";
                return string.Join('\n', found.Select(s =>
                    $"{s.Id}  {s.Name}  bookmarks={s.Bm} history={s.Hist}{(s.Note is { Length: > 0 } ? $"  ({s.Note})" : "")}"));
            case "run":
                if (string.IsNullOrWhiteSpace(source)) return "run needs 'source' (an id from scan)";
                if (!bookmarks && !history) return "nothing to import";
                try
                {
                    var (bm, hist) = await browser.ImportFromBrowserAsync(source, bookmarks, history);
                    return $"imported {bm} new bookmark(s) and {hist} new history entr(ies)";
                }
                catch (ArgumentException ex) { return ex.Message; }
            default:
                return $"unknown action '{action}' (scan | run)";
        }
    }
}
