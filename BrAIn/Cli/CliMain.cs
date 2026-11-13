using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BrAIn.Cli;

/// <summary>
/// The <c>brain</c> command: drive the running BrAIn from a shell. Each command is one MCP tool call over the browser's
/// private control socket, so it has the same abilities, safety checks and activity log as an AI agent — and when no browser
/// is running one is started in the background (on Linux without a display: on an invisible screen of its own).
/// <c>brain view</c> shows that browser live in the terminal.
/// </summary>
internal static class CliMain
{
    private static readonly HashSet<string> Commands =
    [
        "help", "start", "stop", "status", "open", "new", "back", "forward", "reload", "read", "text", "html", "snap", "find",
        "click", "dblclick", "hover", "type", "key", "select", "check", "uncheck", "scroll", "wait", "shot", "tabs", "tab", "close",
        "eval", "cookies", "url", "upload", "downloads", "view", "mcp", "tools", "call",
    ];

    internal static bool IsCommand(string[] args) => args.Length > 0 && Commands.Contains(args[0]);

    internal static int Run(string[] args)
    {
        // Plain UTF-8 straight to the file descriptors: .NET's own console, once used on a terminal, switches it to
        // "application keypad" mode (and leaves it there), which a command line has no business doing.
        if (!OperatingSystem.IsWindows())
        {
            Console.SetOut(new StreamWriter(new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(1, false), FileAccess.Write, 1), new UTF8Encoding(false)) { AutoFlush = true });
            Console.SetError(new StreamWriter(new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(2, false), FileAccess.Write, 1), new UTF8Encoding(false)) { AutoFlush = true });
        }
        else Console.OutputEncoding = new UTF8Encoding(false);
        var cmd = args[0];
        var a = args.Skip(1).ToList();
        if (OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("The brain command is available on macOS and Linux; on Windows, drive BrAIn through its MCP server.");
            return 2;
        }
        try
        {
            switch (cmd)
            {
                case "help": return Help(a.FirstOrDefault());
                case "start":
                    {
                        bool? headless = Flag(a, "--headless") ? true : Flag(a, "--visible") ? false : null;
                        // anything else that looks like a flag is the browser's own (--no-fullscreen, --grant-permissions, --no-toolbar …)
                        var extra = a.Where(x => x.StartsWith("--")).ToArray();
                        return ControlClient.StartDaemon(headless, a.FirstOrDefault(x => !x.StartsWith("--")), quiet: false, extra);
                    }
                case "stop": return ControlClient.Stop();
                case "status": return Status();
                case "view": return View.Viewer.Run(a);
                case "mcp": return Bridge();
            }
            if (!ControlClient.EnsureRunning()) return 2;
            using var s = McpSession.Open();
            if (s is null) { Console.Error.WriteLine("brain: BrAIn is not answering"); return 2; }
            return Dispatch(s, cmd, a);
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine("brain " + cmd + ": " + ex.Message);
            Console.Error.WriteLine("  usage: " + Usage(cmd));
            return 64;
        }
        catch (IOException ex) when (ex.HResult == 32 || ex.Message.Contains("Broken pipe", StringComparison.OrdinalIgnoreCase))
        {
            return 0; // the reader went away (brain read | head): nothing to complain about
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException)
        {
            Console.Error.WriteLine("brain " + cmd + ": " + ex.Message);
            return 1;
        }
    }

    private sealed class UsageException(string m) : Exception(m);

    private static int Dispatch(McpSession s, string cmd, List<string> a)
    {
        switch (cmd)
        {
            case "open":
                {
                    var newTab = Flag(a, "-n") | Flag(a, "--new-tab");
                    var priv = Flag(a, "--private");
                    var url = Need(string.Join(' ', a), "a URL or search words");
                    return newTab || priv
                        ? Print(s.Call("browser_new_tab", new() { ["url"] = url, ["priv"] = priv }))
                        : Print(s.Call("browser_navigate", new() { ["url"] = url }));
                }
            case "new":
                {
                    var priv = Flag(a, "--private");
                    return Print(s.Call("browser_new_tab", new() { ["url"] = a.Count > 0 ? string.Join(' ', a) : null, ["priv"] = priv }));
                }
            case "back": return Print(s.Call("browser_back"));
            case "forward": return Print(s.Call("browser_forward"));
            case "reload": return Print(s.Call("browser_reload", new() { ["ignoreCache"] = Flag(a, "--hard") }));
            case "url": return Print(s.Call("browser_state"));
            case "read":
                {
                    var args = new JsonObject { ["maxLength"] = 400000, ["full"] = Flag(a, "--all"), ["selection"] = Flag(a, "--selection") };
                    if (Flag(a, "--no-images")) args["images"] = false;
                    if (a.Count > 0) args["selector"] = Target(a[0]);
                    return Print(s.Call("browser_markdown", args));
                }
            case "text":
                return Print(s.Call("browser_text", new() { ["maxLength"] = 400000, ["selector"] = a.Count > 0 ? Target(a[0]) : null }));
            case "html":
                return Print(s.Call("browser_html", new() { ["maxLength"] = 2000000, ["selector"] = a.Count > 0 ? Target(a[0]) : null }));
            case "snap":
                {
                    var max = Option(a, "--max");
                    return Print(s.Call("browser_snapshot", new() { ["maxNodes"] = max is null ? 500 : int.Parse(max) }));
                }
            case "find": return Print(s.Call("browser_find", new() { ["selector"] = Need(a.FirstOrDefault(), "a CSS selector") }));
            case "click":
                {
                    if (Option(a, "--at") is { } at)
                    {
                        var xy = at.Split(',', 'x');
                        if (xy.Length != 2) throw new UsageException("--at takes X,Y");
                        return Print(s.Call("browser_click_at", new()
                        {
                            ["x"] = double.Parse(xy[0], System.Globalization.CultureInfo.InvariantCulture),
                            ["y"] = double.Parse(xy[1], System.Globalization.CultureInfo.InvariantCulture),
                            ["button"] = Flag(a, "--right") ? "right" : "left",
                        }));
                    }
                    return Print(s.Call("browser_click", new() { ["element"] = Target(Need(a.FirstOrDefault(), "what to click")) }));
                }
            case "dblclick": return Print(s.Call("browser_double_click", new() { ["element"] = Target(Need(a.FirstOrDefault(), "what to double-click")) }));
            case "hover": return Print(s.Call("browser_hover", new() { ["element"] = Target(Need(a.FirstOrDefault(), "what to hover")) }));
            case "type":
                {
                    var enter = Flag(a, "--enter");
                    int rc;
                    if (a.Count == 1) rc = Print(s.Call("browser_type_text", new() { ["text"] = a[0] }));
                    else if (a.Count >= 2) rc = Print(s.Call("browser_type", new() { ["element"] = Target(a[0]), ["text"] = string.Join(' ', a.Skip(1)) }));
                    else throw new UsageException("say what to type");
                    if (rc == 0 && enter) rc = Print(s.Call("browser_press_key", new() { ["key"] = "Enter" }));
                    return rc;
                }
            case "key":
                {
                    var spec = Need(a.FirstOrDefault(), "a key, like Enter or ctrl+a");
                    var parts = spec.Split('+');
                    var key = parts[^1];
                    var mods = parts.Length > 1 ? string.Join('+', parts[..^1]).ToLowerInvariant() : null;
                    return Print(s.Call("browser_press_key", new() { ["key"] = key, ["modifiers"] = mods }));
                }
            case "select":
                {
                    var el = Target(Need(a.FirstOrDefault(), "the list to choose in"));
                    if (a.Count < 2) throw new UsageException("say which option(s)");
                    return Print(s.Call("browser_select_option", new() { ["element"] = el, ["values"] = new JsonArray(a.Skip(1).Select(v => (JsonNode)v).ToArray()) }));
                }
            case "check":
            case "uncheck":
                return Print(s.Call("browser_set_checked", new() { ["element"] = Target(Need(a.FirstOrDefault(), "the checkbox")), ["check"] = cmd == "check" }));
            case "scroll":
                {
                    var dir = a.FirstOrDefault() ?? "down";
                    if (dir is "up" or "down" or "left" or "right" or "top" or "bottom")
                        return Print(s.Call("browser_scroll", new() { ["direction"] = dir, ["amount"] = a.Count > 1 && int.TryParse(a[1], out var px) ? px : 600 }));
                    return Print(s.Call("browser_scroll_to", new() { ["element"] = Target(dir) }));
                }
            case "wait":
                {
                    var t = Option(a, "--timeout");
                    var args = new JsonObject { ["timeoutMs"] = t is null ? 15000 : (int)(double.Parse(t, System.Globalization.CultureInfo.InvariantCulture) * 1000) };
                    if (Option(a, "--for") is { } el) args["element"] = Target(el);
                    if (Option(a, "--url") is { } u) args["url"] = u;
                    if (Flag(a, "--load")) args["readyState"] = "complete";
                    if (a.Count > 0) args["text"] = string.Join(' ', a);
                    var r = s.Call("browser_wait_for", args);
                    return Print(r with { IsError = r.IsError || r.Text.StartsWith("timeout", StringComparison.Ordinal) });
                }
            case "shot":
                {
                    var full = Flag(a, "--full");
                    var el = Option(a, "--el");
                    var r = s.Call("browser_screenshot", new() { ["fullPage"] = full, ["element"] = el is null ? null : Target(el) });
                    if (r.IsError || r.Images.Count == 0) return Print(r with { IsError = true });
                    var png = r.Images[0].Data;
                    var file = a.FirstOrDefault();
                    if (file is not null)
                    {
                        File.WriteAllBytes(file, png);
                        Console.WriteLine($"saved {Path.GetFullPath(file)} ({png.Length / 1024} KB)");
                        return 0;
                    }
                    if (!Posix.IsTty(1))
                    {
                        using var o = Console.OpenStandardOutput();
                        o.Write(png);
                        return 0;
                    }
                    return View.Inline.Show(png) ? 0 : SaveTemp(png);
                }
            case "tabs": return PrintTabs(s);
            case "tab":
                {
                    var id = TabId(s, Need(a.FirstOrDefault(), "a tab number (see brain tabs)"));
                    var rc = Print(s.Call("browser_switch_tab", new() { ["id"] = id }), quiet: true);
                    return rc == 0 ? PrintTabs(s) : rc;
                }
            case "close":
                {
                    var id = a.Count > 0 ? TabId(s, a[0]) : ParseTabs(s).FirstOrDefault(t => t.Active).Id;
                    if (id is null) throw new UsageException("no tab to close");
                    return Print(s.Call("browser_close_tab", new() { ["id"] = id }));
                }
            case "eval": return Print(s.Call("browser_evaluate", new() { ["script"] = Need(string.Join(' ', a), "a JavaScript expression") }));
            case "cookies":
                return Print(s.Call("browser_cookies", new()
                {
                    ["action"] = a.FirstOrDefault() ?? "list",
                    ["name"] = a.Count > 1 ? a[1] : null,
                    ["value"] = a.Count > 2 ? a[2] : null,
                }));
            case "upload":
                {
                    var file = Path.GetFullPath(Need(a.FirstOrDefault(), "the file to upload"));
                    return Print(s.Call("browser_upload", new() { ["path"] = file, ["element"] = a.Count > 1 ? Target(a[1]) : null }));
                }
            case "downloads": return Print(s.Call("browser_downloads"));
            case "tools":
                foreach (var (name, desc) in s.Tools().OrderBy(t => t.Name))
                {
                    var first = desc.Split('\n')[0];
                    var cut = first.IndexOf(". ", StringComparison.Ordinal);
                    Console.WriteLine($"{name,-28} {(cut > 0 ? first[..(cut + 1)] : first)}");
                }
                return 0;
            case "call":
                {
                    var tool = Need(a.FirstOrDefault(), "a tool name (see brain tools)");
                    var rest = a.Skip(1).ToList();
                    JsonObject args;
                    if (rest.Count == 1 && rest[0].TrimStart().StartsWith('{')) args = JsonNode.Parse(rest[0])!.AsObject();
                    else
                    {
                        args = new JsonObject();
                        var schema = rest.Count > 0 ? s.Schema(tool) : null;
                        foreach (var kv in rest)
                        {
                            var eq = kv.IndexOf('=');
                            if (eq <= 0) throw new UsageException($"'{kv}' is not key=value");
                            args[kv[..eq]] = Typed(schema, kv[..eq], kv[(eq + 1)..]);
                        }
                    }
                    var r = s.Call(tool, args);
                    foreach (var (mime, data) in r.Images)
                    {
                        var ext = mime.Contains("jpeg") ? ".jpg" : ".png";
                        var f = Path.Combine(Path.GetTempPath(), $"brain-{DateTime.Now:HHmmss}-{Guid.NewGuid().ToString("N")[..4]}{ext}");
                        File.WriteAllBytes(f, data);
                        Console.WriteLine($"(image saved to {f})");
                    }
                    return Print(r);
                }
        }
        throw new UsageException("unknown command");
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>What a user types for an element → a selector the tools take: 12 / e12 → the ref from <c>brain snap</c>,
    /// css:/text:/role:/xpath: as given, something that looks like CSS as CSS, anything else is visible text.</summary>
    internal static string Target(string a)
    {
        a = a.Trim();
        if (Regex.IsMatch(a, @"^\d+$")) return "e" + a;
        if (Regex.IsMatch(a, @"^e\d+$")) return a;
        if (Regex.IsMatch(a, @"^(css|text|role|xpath|ref):", RegexOptions.IgnoreCase)) return a;
        if (a.StartsWith('#') || a.StartsWith('.') || a.StartsWith('[') || a.Contains('>') ||
            Regex.IsMatch(a, @"^[a-zA-Z][a-zA-Z0-9-]*([.#\[:][^ ]*)$")) return a;
        return "text:" + a;
    }

    private static readonly string[] FailurePrefixes =
        ["ERROR", "FAILED", "Navigation failed", "Cannot go", "Reload failed", "element not found", "No tab matching", "timeout after"];

    private static int Print(ToolResult r, bool quiet = false)
    {
        // the element tools answer "click X: {"ok":false,"error":…}" when they could not do it
        var failed = r.IsError || FailurePrefixes.Any(p => r.Text.StartsWith(p, StringComparison.Ordinal)) ||
                     r.Text.Contains(": {\"ok\":false", StringComparison.Ordinal) || r.Text.StartsWith("{\"ok\":false", StringComparison.Ordinal);
        if (!quiet || failed)
        {
            var w = failed ? Console.Error : Console.Out;
            w.Write(r.Text);
            if (!r.Text.EndsWith('\n')) w.WriteLine();
        }
        return failed ? 1 : 0;
    }

    private static int SaveTemp(byte[] png)
    {
        var f = Path.Combine(Path.GetTempPath(), $"brain-shot-{DateTime.Now:yyyyMMdd-HHmmss}.png");
        File.WriteAllBytes(f, png);
        Console.WriteLine($"saved {f} ({png.Length / 1024} KB) — this terminal cannot show images; `brain view` can show the page");
        return 0;
    }

    private static bool Flag(List<string> a, string name)
    {
        var i = a.IndexOf(name);
        if (i < 0) return false;
        a.RemoveAt(i);
        return true;
    }

    private static string? Option(List<string> a, string name)
    {
        var i = a.IndexOf(name);
        if (i < 0) return null;
        if (i + 1 >= a.Count) throw new UsageException($"{name} needs a value");
        var v = a[i + 1];
        a.RemoveRange(i, 2);
        return v;
    }

    private static string Need(string? v, string what)
        => string.IsNullOrWhiteSpace(v) ? throw new UsageException("missing " + what) : v;

    private static JsonNode? Typed(JsonElement? schema, string key, string value)
    {
        string? type = null;
        if (schema is { } sc && sc.TryGetProperty("properties", out var props) && props.TryGetProperty(key, out var p) &&
            p.TryGetProperty("type", out var t))
            type = t.ValueKind == JsonValueKind.String ? t.GetString()
                : t.ValueKind == JsonValueKind.Array ? t.EnumerateArray().Select(x => x.GetString()).FirstOrDefault(x => x != "null") : null;
        return type switch
        {
            "integer" => long.Parse(value),
            "number" => double.Parse(value, System.Globalization.CultureInfo.InvariantCulture),
            "boolean" => value is "true" or "1" or "yes",
            "array" => value.TrimStart().StartsWith('[') ? JsonNode.Parse(value) : new JsonArray(value.Split(',').Select(v => (JsonNode)v).ToArray()),
            "object" => JsonNode.Parse(value),
            _ => value,
        };
    }

    internal readonly record struct TabRow(int N, bool Active, string Id, string Title, string Url, string Flags);

    internal static List<TabRow> ParseTabs(McpSession s)
    {
        var rows = new List<TabRow>();
        var n = 0;
        foreach (var line in s.Call("browser_tabs").Text.Split('\n'))
        {
            if (line.Length < 11 || line.StartsWith("Tabs:")) continue;
            var active = line.StartsWith("* ");
            var id = line.Substring(2, 8);
            var rest = line[10..];
            var flags = "";
            foreach (var f in new[] { " [private]", " [pinned]" })
                if (rest.StartsWith(f)) { flags += f.Trim() + " "; rest = rest[f.Length..]; }
            rest = rest.TrimStart();
            var lt = rest.LastIndexOf("  <", StringComparison.Ordinal);
            var title = lt >= 0 ? rest[..lt] : rest;
            var url = lt >= 0 ? rest[(lt + 3)..].TrimEnd('>') : "";
            rows.Add(new TabRow(++n, active, id, title, url, flags.Trim()));
        }
        return rows;
    }

    private static int PrintTabs(McpSession s)
    {
        foreach (var t in ParseTabs(s))
            Console.WriteLine($"{(t.Active ? "*" : " ")}{t.N,3}  {(t.Flags.Length > 0 ? t.Flags + " " : "")}{t.Title}  {t.Url}");
        return 0;
    }

    private static string TabId(McpSession s, string which)
    {
        if (int.TryParse(which, out var n))
        {
            var row = ParseTabs(s).FirstOrDefault(t => t.N == n);
            if (row.Id is null) throw new UsageException($"there is no tab {n} (see brain tabs)");
            return row.Id;
        }
        return which;
    }

    private static int Status()
    {
        var info = ControlClient.GetInfo();
        if (info is null) { Console.WriteLine("BrAIn is not running."); return 3; }
        var how = !info.Headless ? "with a window"
            : string.IsNullOrEmpty(info.Display) ? "without a window, for the terminal: brain view" : $"headless, invisible screen {info.Display}";
        Console.WriteLine($"BrAIn {info.Version} is running (pid {info.Pid}, {how}).");
        using var s = McpSession.Open();
        if (s is not null) PrintTabs(s);
        return 0;
    }

    /// <summary><c>brain mcp</c>: an MCP server on stdio that is really the running browser — point an MCP client (an AI agent
    /// on a server) here and it shares the browser that <c>brain view</c> shows.</summary>
    private static int Bridge()
    {
        if (!ControlClient.EnsureRunning(quiet: true)) return 2;
        using var sock = ControlClient.TryConnect();
        if (sock is null) { Console.Error.WriteLine("brain mcp: BrAIn is not answering"); return 2; }
        using var ns = new System.Net.Sockets.NetworkStream(sock);
        ns.Write("mcp\n"u8);
        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();
        var up = Task.Run(() =>
        {
            try { stdin.CopyTo(ns); } catch { }
            try { sock.Shutdown(System.Net.Sockets.SocketShutdown.Send); } catch { }
        });
        var down = Task.Run(() =>
        {
            var buf = new byte[65536];
            try
            {
                int n;
                while ((n = ns.Read(buf)) > 0) { stdout.Write(buf, 0, n); stdout.Flush(); }
            }
            catch { }
        });
        Task.WaitAny(up, down);
        // the client went away (stdin closed): give the last answers a moment to arrive
        if (up.IsCompleted) down.Wait(TimeSpan.FromSeconds(2));
        return 0;
    }

    // ---------------------------------------------------------------- help

    private static readonly (string Cmd, string Args, string Text)[] HelpRows =
    [
        ("", "", "The browser"),
        ("start", "[--headless|--visible] [flags] [url]", "start BrAIn in the background (--headless: without a window, for view)"),
        ("stop", "", "close it"),
        ("status", "", "is it running, and its tabs"),
        ("view", "", "the browser live in this terminal: mouse, keyboard, tabs, address bar (a BrAIn without a window)"),
        ("", "", "Pages"),
        ("open", "<url|words> [-n] [--private]", "go there (-n: in a new tab)"),
        ("back / forward / reload", "[--hard]", ""),
        ("url", "", "address, title, loading state"),
        ("read", "[element] [--all]", "the page as Markdown (articles, docs, search results)"),
        ("text / html", "[element]", "visible text / HTML"),
        ("snap", "[--max N]", "what is on the page, interactive parts numbered: click 12, type 7 …"),
        ("find", "<css>", "elements matching a CSS selector, with numbers"),
        ("shot", "[file.png] [--full] [--el X]", "screenshot: shown here if the terminal can, else saved"),
        ("", "", "Doing things    (X = a number from snap, visible text like \"Sign in\", or a CSS selector)"),
        ("click / dblclick / hover", "<X> | --at x,y", ""),
        ("type", "<X> <text> [--enter]", "fill a field (just <text>: type into the focused one)"),
        ("key", "<key>", "Enter, Tab, Escape, ArrowDown, ctrl+a, …"),
        ("select", "<X> <option…>", "choose in a list"),
        ("check / uncheck", "<X>", ""),
        ("scroll", "[down|up|top|bottom [px]] | <X>", ""),
        ("wait", "[text] [--for X] [--url pat] [--load] [--timeout s]", "until it is there"),
        ("upload", "<file> [X]", "put a file into a file field"),
        ("eval", "<js>", "run JavaScript in the page"),
        ("", "", "Tabs"),
        ("tabs", "", "list them (numbered)"),
        ("tab", "<n>", "switch to tab n"),
        ("new", "[url]", "open a tab"),
        ("close", "[n]", "close tab n (default: this one)"),
        ("", "", "More"),
        ("cookies", "[list|set|delete|clear] [name] [value]", ""),
        ("downloads", "", "what was downloaded"),
        ("tools", "", "every tool BrAIn has (the commands above are a few of them)"),
        ("call", "<tool> [key=value… | json]", "call any tool"),
        ("mcp", "", "MCP on stdio, attached to this browser (for an AI agent: it then shares what you see in view)"),
    ];

    private static string Usage(string cmd)
    {
        foreach (var (c, args, _) in HelpRows)
            if (c.Split(" / ").Contains(cmd)) return $"brain {cmd} {args}".TrimEnd();
        return "brain help";
    }

    private static int Help(string? topic)
    {
        if (topic is not null && Commands.Contains(topic)) { Console.WriteLine(Usage(topic)); return 0; }
        var o = Console.Out;
        o.WriteLine("brain — use the BrAIn browser from a terminal. It starts one in the background when none is running.");
        if (OperatingSystem.IsLinux() && !Headless.HasDisplay())
            o.WriteLine("(No display here: BrAIn runs on an invisible screen of its own. `brain view` shows it in this terminal.)");
        foreach (var (c, args, text) in HelpRows)
        {
            if (c.Length == 0) { o.WriteLine(); o.WriteLine(text); continue; }
            var left = $"  brain {c} {args}".TrimEnd();
            o.WriteLine(text.Length == 0 ? left : left.Length < 46 ? left.PadRight(46) + text : left + "\n" + new string(' ', 46) + text);
        }
        o.WriteLine();
        o.WriteLine("Examples:");
        o.WriteLine("  brain open news.ycombinator.com && brain read");
        o.WriteLine("  brain snap            # then: brain click 12 / brain type 7 \"hello\" --enter");
        o.WriteLine("  brain view            # the whole browser in this terminal (pictures, or real text where pictures cannot be shown); Ctrl+O reader, Ctrl+Q leave");
        return 0;
    }
}
