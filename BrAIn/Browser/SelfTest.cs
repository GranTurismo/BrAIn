using System.Text;
using System.Text.Json;

namespace BrAIn.Browser;

/// <summary>
/// <c>BrAIn --selftest-tabs</c>: drives the real browser through the tab-switching scenarios that
/// matter (state survives a switch, evicted tabs come back, private tabs are never evicted) and writes
/// PASS/FAIL lines to <c>selftest-tabs.txt</c> in the app data folder, then closes the window.
/// Offline: pages are data: URLs. Exit code is the number of failures (0 = all good).
/// </summary>
public static class SelfTest
{
    private static string Page(string name) =>
        "data:text/html;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"<!doctype html><html><head><title>{name}</title></head><body style=\"margin:0;height:6000px\">" +
            "<input id=\"i\" style=\"position:fixed;top:60px;left:10px\"></body></html>"));

    public static async Task<int> RunTabsAsync(BrowserController c)
    {
        var sb = new StringBuilder();
        var fails = 0;
        void Rep(bool ok, string name, string detail = "")
        {
            if (!ok) fails++;
            var line = $"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  — " + detail : "")}";
            sb.AppendLine(line);
            BrowserController.Log("selftest: " + line);
        }
        void Note(string text) { sb.AppendLine("      " + text); BrowserController.Log("selftest: " + text); }

        try
        {
            await c.Ready.WaitAsync(TimeSpan.FromSeconds(60));
            var perTab = c.PerTabViews;
            sb.AppendLine($"BrAIn tab self-test {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"mode: {(perTab ? "per-tab views" : "shared view (switching reloads the page)")}" +
                          $"{(c.ViewEvictionActive ? ", eviction cap " + Environment.GetEnvironmentVariable("BRAIN_MAXLIVE") : "")}");
            sb.AppendLine();

            async Task<string?> Eval(BrowserTab t, string js)
            {
                try { return await c.EvaluateAsync<string>(js, true, t); } catch { return null; }
            }
            async Task<bool> WaitTitle(BrowserTab t, string title, int secs = 20)
            {
                for (var i = 0; i < secs * 4; i++)
                {
                    if (await Eval(t, "document.title") == title && await Eval(t, "document.readyState") == "complete") return true;
                    await Task.Delay(250);
                }
                return false;
            }
            static string Id(BrowserTab t) => t.Id.ToString("N")[..8];

            // ---------- 1. state survives a tab switch ----------
            var a = await c.NewTabAsync(Page("A"));
            Rep(await WaitTitle(a, "A"), "tab A loaded");
            var origin = await Eval(a, "(() => { window.__alive = 7; document.getElementById('i').value = 'draft'; window.scrollTo(0, 1500); return String(performance.timeOrigin); })()");
            await Task.Delay(700); // let chrome.js report the scroll position
            var b = await c.NewTabAsync(Page("B"));
            await WaitTitle(b, "B");
            var c3 = await c.NewTabAsync(Page("C"));
            await WaitTitle(c3, "C");
            await Task.Delay(1500);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            await c.ActivateWindowAsync(Id(a));
            string? state = null;
            for (var i = 0; i < 80 && state is null; i++)
            {
                state = await Eval(a, "JSON.stringify({a:window.__alive||0,v:(document.getElementById('i')||{}).value||'',y:Math.round(scrollY),o:String(performance.timeOrigin)})");
                if (state is null) await Task.Delay(100);
            }
            var ms = sw.ElapsedMilliseconds;
            if (state is null) Rep(false, "switch back to A", "page never answered");
            else
            {
                using var d = JsonDocument.Parse(state);
                var r = d.RootElement;
                var alive = r.GetProperty("a").GetInt32() == 7;
                var draft = r.GetProperty("v").GetString() == "draft";
                var y = r.GetProperty("y").GetInt32();
                var sameDoc = r.GetProperty("o").GetString() == origin;
                if (perTab)
                {
                    Rep(alive && sameDoc, "page not reloaded on switch", $"JS variable {(alive ? "kept" : "LOST")}, same document {sameDoc}");
                    Rep(draft, "typed text kept", draft ? "" : "field was reset");
                    Rep(Math.Abs(y - 1500) <= 8, "scroll position kept", $"scrollY={y}");
                    Rep(ms < 2000, "switch is quick", $"{ms} ms");
                }
                else Note($"shared-view mode: JS alive={alive}, draft={draft}, scrollY={y}, reloaded={!sameDoc} ({ms} ms) — expected to reload here");
            }

            // ---------- 1b. no cap: every tab stays live ----------
            if (perTab && !c.ViewEvictionActive)
            {
                for (var i = 1; i <= 8; i++)
                {
                    var t = await c.NewTabAsync(Page("L" + i));
                    await WaitTitle(t, "L" + i);
                }
                await Task.Delay(2000);
                var all = c.Tabs.All;
                var dead = all.Count(t => !t.HasView || t.Evicted);
                Rep(dead == 0, "all tabs stay live (no cap)", $"{all.Count} tabs, {dead} without a live view");
            }

            // ---------- 2. private tab never evicted ----------
            BrowserTab? priv = null;
            if (c.ViewEvictionActive)
            {
                priv = await c.NewTabAsync(Page("P"), default, priv: true);
                await WaitTitle(priv, "P");
            }

            // ---------- 3. eviction + restore ----------
            if (c.ViewEvictionActive)
            {
                var extra = new List<BrowserTab>();
                for (var i = 1; i <= 4; i++)
                {
                    var t = await c.NewTabAsync(Page("X" + i));
                    await WaitTitle(t, "X" + i);
                    extra.Add(t);
                }
                await Task.Delay(2500); // eviction runs after each new tab
                var live = c.Tabs.All.Count(t => t.HasView);
                var cap = int.TryParse(Environment.GetEnvironmentVariable("BRAIN_MAXLIVE"), out var n) ? n : 10;
                var evicted = c.Tabs.All.Where(t => t.Evicted).ToList();
                Rep(live <= cap + 1, "live views stay capped", $"{live} live (cap {cap}), {evicted.Count} evicted");
                Rep(evicted.Count > 0, "oldest tabs were evicted");
                Rep(priv is not null && priv.HasView && !priv.Evicted, "private tab was not evicted");

                var victim = c.Tabs.All.FirstOrDefault(t => t.Evicted);
                if (victim is not null)
                {
                    var title = victim.LastUrl.StartsWith("data:") ? null : victim.LastTitle;
                    await c.ActivateWindowAsync(Id(victim));
                    // the page must come back by itself
                    var back = false;
                    for (var i = 0; i < 120 && !back; i++)
                    {
                        var t = await Eval(victim, "document.readyState + '|' + document.title");
                        back = t is not null && t.StartsWith("complete|") && t.Length > "complete|".Length;
                        if (!back) await Task.Delay(250);
                    }
                    Rep(back && !victim.Evicted, "evicted tab restored when shown", $"tab {Id(victim)}");
                    if (victim.ScrollY is int sy && sy > 0)
                    {
                        await Task.Delay(800);
                        var y2 = await Eval(victim, "String(Math.round(scrollY))");
                        Rep(y2 is not null && Math.Abs(int.Parse(y2) - sy) <= 8, "restored tab scroll position", $"was {sy}, now {y2}");
                    }
                    else Note("restored tab had no recorded scroll position (skipped scroll check)");
                }
                else Note("no tab was evicted (skipped restore check)");
            }
        }
        catch (Exception ex) { Rep(false, "self-test crashed", ex.Message); }

        sb.AppendLine();
        sb.AppendLine(fails == 0 ? "RESULT: ALL PASSED" : $"RESULT: {fails} FAILED");
        try { await File.WriteAllTextAsync(Path.Combine(c.StoreDir, "selftest-tabs.txt"), sb.ToString()); } catch { }
        Console.Error.WriteLine(sb.ToString());
        // The Windows exe has no console: show the report where the person who started it can see it.
        if (OperatingSystem.IsWindows() && !Environment.GetCommandLineArgs().Contains("--selftest-quiet"))
            try { Native.Win32.MessageBox(sb.ToString() + "\nSaved to " + Path.Combine(c.StoreDir, "selftest-tabs.txt"), "BrAIn tab self-test"); } catch { }
        c.CloseAllWindows();
        return fails;
    }
}
