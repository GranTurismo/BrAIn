using System.Text.Json.Nodes;

namespace BrAIn.Browser;

/// <summary>
/// What the host may push into a WEB page's view (split mode: macOS, Linux). BrAIn's own scripts run inside every page, in the page's own
/// JavaScript world, so whatever the host pushes there can be read by that page — a script can poison Array.prototype or Array.isArray and watch
/// the toolbar script handle the data (ViewAuth stops a page from CALLING the bridge, nothing stops it from WATCHING what passes through its own
/// world). Therefore nothing a page has no business with is sent at all: not the browsing history, bookmarks or the other tabs (the toolbar lives
/// in its own view in split mode and gets all of that there), not the downloads, the omnibox suggestions, the update state. BrAIn's own pages
/// (about:newtab, history, passwords...) are trusted and get everything.
/// </summary>
internal static class PageFeed
{
    /// <summary>The push kinds ({"b":N,...}) the page-side copy of chrome.js consumes for a web page: the DevTools panel (10-15), field and
    /// password helpers (22-26), the agent and recorder overlays (40, 48), commands for the page UI (60), Rewind (71-74), quick controls (97),
    /// toasts (98, 102), and the reduced state (9).</summary>
    private static readonly HashSet<int> Allowed = new() { 9, 10, 11, 12, 13, 14, 15, 22, 23, 24, 26, 40, 48, 60, 71, 72, 73, 74, 97, 98, 102 };

    /// <summary>State fields that describe the user's browsing rather than this page.</summary>
    private static readonly string[] StateDrop = { "hist", "bmarks", "sess", "keys", "sel", "rws", "tbh", "tbx", "us", "api" };

    /// <summary>The push for a web page, or null when the page must not get it.</summary>
    public static string? ForWebPage(string json, BrowserTab tab)
    {
        var kind = KindOf(json);
        if (kind < 0 || !Allowed.Contains(kind)) return null;
        if (kind != 9 && kind != 15) return json;
        try
        {
            if (kind == 15)
            {
                // the Sessions pane lists every open tab (private ones too) and what was closed: a web page's copy only sees itself
                if (JsonNode.Parse(json) is not JsonObject sess) return null;
                var me = tab.Id.ToString("N")[..8];
                var self = (sess["open"] as JsonArray)?.FirstOrDefault(t => t?["id"]?.GetValue<string>() == me);
                sess["open"] = self is null ? new JsonArray() : new JsonArray(self.DeepClone());
                sess["closed"] = new JsonArray();
                sess["saved"] = 0;
                return sess.ToJsonString();
            }
            if (JsonNode.Parse(json) is not JsonObject state) return null;
            foreach (var k in StateDrop) state.Remove(k);
            // the page knows its own tab; it does not need to know about the others
            var mine = tab.Id.ToString("N")[..8];
            var own = (state["tabs"] as JsonArray)?.FirstOrDefault(t => t?["id"]?.GetValue<string>() == mine);
            state["tabs"] = own is null ? new JsonArray() : new JsonArray(own.DeepClone());
            return state.ToJsonString();
        }
        catch { return null; }
    }

    private static int KindOf(string json)
    {
        const string p = "{\"b\":";
        if (!json.StartsWith(p, StringComparison.Ordinal)) return -1;
        var i = p.Length; var n = 0;
        if (i >= json.Length || !char.IsDigit(json[i])) return -1;
        while (i < json.Length && char.IsDigit(json[i]) && n < 100000) n = n * 10 + (json[i++] - '0');
        return n;
    }
}
