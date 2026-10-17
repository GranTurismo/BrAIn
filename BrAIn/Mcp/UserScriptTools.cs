using System.ComponentModel;
using System.Text;
using BrAIn.Browser;
using ModelContextProtocol.Server;

namespace BrAIn.Mcp;

/// <summary>
/// Agent-facing tools for auto-run site scripts. Anything an agent writes is a
/// DRAFT: it is stored disabled and only starts running after the user turns it
/// on in the Personality page, because it would otherwise be persistent
/// agent-written code that executes on every visit to a site.
/// </summary>
[McpServerToolType]
public sealed class UserScriptTools(UserScriptStore store, BrowserController browser)
{
    private const string DraftNote =
        "Saved as a DRAFT. It does not run yet: the user must review it and switch it on in the Personality page (about:personality → Site scripts). Tell them that.";

    [McpServerTool(Name = "userscript_list"),
     Description("List the auto-run site scripts (id, name, URL patterns, status). Scripts run automatically when a page whose address matches loads.")]
    public Task<string> List()
    {
        var all = store.All();
        if (all.Count == 0) return Task.FromResult("(no site scripts yet)");
        var sb = new StringBuilder();
        foreach (var s in all)
            sb.AppendLine($"{s.Id}  [{Status(s)}]  {s.Name}  — {string.Join(", ", s.Matches)}  (by {s.Source})" +
                (s.Exclude.Length > 0 ? $"  paused on: {string.Join(", ", s.Exclude)}" : ""));
        return Task.FromResult(sb.ToString().TrimEnd());
    }

    [McpServerTool(Name = "userscript_run"),
     Description("Run a script RIGHT NOW on an open tab (even a draft) to test it, and get back any errors it threw. " +
                 "The tab must be on a site the script's URL patterns match. Use this after userscript_create/userscript_update to verify the code works, then fix errors and update.")]
    public async Task<string> Run(
        [Description("Script id")] string id,
        [Description("Tab id/prefix (default: the active tab)")] string? tab = null,
        CancellationToken ct = default)
    {
        var s = store.Get(id);
        if (s is null) return $"No script with id '{id}'.";
        var t = tab is null ? browser.ActiveTabOrThrow()
            : browser.Tabs.FindByPrefix(tab) ?? throw new InvalidOperationException($"No tab matching '{tab}'");
        if (t.LastUrl is not { } url || !store.UrlMatches(s, url))
            return $"That tab ({t.LastUrl}) does not match this script's sites ({string.Join(", ", s.Matches)}). Navigate there first, or change the patterns with userscript_update.";
        var (ok, text) = await browser.RunScriptOnTabAsync(s, t, ct);
        return (ok ? "OK: " : "ERROR: ") + text;
    }

    [McpServerTool(Name = "userscript_errors"),
     Description("How often a script has run since BrAIn started and the last errors it threw (including ones from timers and observers). Use it to debug a script, then fix it with userscript_update.")]
    public Task<string> Errors([Description("Script id")] string id)
        => Task.FromResult(store.Get(id) is null ? $"No script with id '{id}'." : browser.UserScriptReport(id));

    [McpServerTool(Name = "userscript_templates"),
     Description("Ready-made site script templates (cookie-banner rejection, pop-up removal, dark mode, ...). Install one with userscript_install_template.")]
    public Task<string> Templates()
        => Task.FromResult(string.Join('\n', UserScriptStore.Templates.Select(t =>
            $"{t.Id}  {t.Name} — {t.Description}  (default sites: {string.Join(", ", t.Matches)})")));

    [McpServerTool(Name = "userscript_get"),
     Description("Read a site script's full source code.")]
    public Task<string> Get([Description("Script id from userscript_list")] string id)
    {
        var s = store.Get(id);
        return Task.FromResult(s is null ? $"No script with id '{id}'."
            : $"{s.Name}  [{Status(s)}]\nMatches: {string.Join(", ", s.Matches)}\n\n{s.Js}");
    }

    [McpServerTool(Name = "userscript_create"),
     Description("Write a JavaScript snippet that BrAIn runs automatically every time a matching website loads (like a userscript). " +
                 "Test the code first on the live page with browser_evaluate. The code runs in the page (full DOM access) after the DOM is ready, " +
                 "once per page load, inside a function — use `return` to stop early, and a MutationObserver for elements that appear later. " +
                 "It is saved as a draft that the user must approve before it runs.")]
    public Task<string> Create(
        [Description("Short name, e.g. 'Hide YouTube Shorts'")] string name,
        [Description("URL patterns the script runs on: 'example.com' (also subdomains), '*.example.com/path/*', 'https://x.com/*', or '*' for every site")] string[] matches,
        [Description("The JavaScript source")] string js,
        [Description("One sentence on what it does")] string? description = null)
    {
        var err = store.Save(new UserScript
        {
            Name = name, Matches = matches ?? [], Js = js, Description = description ?? "",
            Enabled = false, Pending = true, Source = "agent",
        }, out var saved);
        return Task.FromResult(err is not null ? $"Not saved: {err}" : $"{DraftNote}\nid: {saved.Id}");
    }

    [McpServerTool(Name = "userscript_install_template"),
     Description("Add a built-in template (see userscript_templates) as a draft the user can approve. Optionally restrict the sites it runs on.")]
    public Task<string> InstallTemplate(
        [Description("Template id from userscript_templates")] string templateId,
        [Description("Override the URL patterns (default: the template's own)")] string[]? matches = null)
    {
        var t = UserScriptStore.Templates.FirstOrDefault(x => x.Id.Equals(templateId, StringComparison.OrdinalIgnoreCase));
        if (t is null) return Task.FromResult($"Unknown template '{templateId}'. Use userscript_templates to see the list.");
        var err = store.Save(new UserScript
        {
            Name = t.Name, Description = t.Description, Matches = matches is { Length: > 0 } ? matches : t.Matches, Js = t.Js,
            Enabled = false, Pending = true, Source = "template", Template = t.Id,
        }, out var saved);
        return Task.FromResult(err is not null ? $"Not saved: {err}" : $"{DraftNote}\nid: {saved.Id}");
    }

    [McpServerTool(Name = "userscript_update"),
     Description("Revise a script that an agent created. The revision goes back to draft and needs the user's approval again. " +
                 "Scripts the user wrote themselves cannot be changed by agents.")]
    public Task<string> Update(
        [Description("Script id")] string id,
        [Description("New JavaScript source (omit to keep)")] string? js = null,
        [Description("New URL patterns (omit to keep)")] string[]? matches = null,
        [Description("New name (omit to keep)")] string? name = null)
    {
        var s = store.Get(id);
        if (s is null) return Task.FromResult($"No script with id '{id}'.");
        if (s.Source == "user") return Task.FromResult("That script was written by the user — ask them to edit it in the Personality page.");
        if (js is not null) s.Js = js;
        if (matches is { Length: > 0 }) s.Matches = matches;
        if (!string.IsNullOrWhiteSpace(name)) s.Name = name;
        s.Enabled = false; s.Pending = true;
        var err = store.Save(s, out _);
        return Task.FromResult(err is not null ? $"Not saved: {err}" : $"Updated. {DraftNote}");
    }

    [McpServerTool(Name = "userscript_delete"),
     Description("Delete a script that an agent created. Scripts the user wrote themselves cannot be deleted by agents.")]
    public Task<string> Delete([Description("Script id")] string id)
    {
        var s = store.Get(id);
        if (s is null) return Task.FromResult($"No script with id '{id}'.");
        if (s.Source == "user") return Task.FromResult("That script was written by the user — ask them to remove it in the Personality page.");
        store.Delete(id);
        return Task.FromResult("Deleted.");
    }

    private static string Status(UserScript s) => s.Pending ? "draft — awaiting approval" : s.Enabled ? "on" : "off";
}
