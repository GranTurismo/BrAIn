using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BrAIn.Cli;

/// <summary>What one tool call answered.</summary>
internal sealed record ToolResult(string Text, List<(string Mime, byte[] Data)> Images, bool IsError);

/// <summary>Talks to the running browser over its control socket (see <see cref="ControlServer"/>).</summary>
internal static class ControlClient
{
    internal sealed record Info(int Pid, string Version, string Os, bool Headless, string? Display, string? XAuthority, int[]? Screen,
        bool Ready, string? Failed);

    internal static Socket? TryConnect(string? path = null)
    {
        path ??= ControlPaths.Socket();
        if (!File.Exists(path)) return null;
        var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try { s.Connect(new UnixDomainSocketEndPoint(path)); return s; }
        catch { s.Dispose(); return null; }
    }

    /// <summary>One request line, one JSON line back. Null when no browser answers.</summary>
    internal static JsonElement? Ask(string request, TimeSpan? timeout = null)
    {
        using var s = TryConnect();
        if (s is null) return null;
        s.ReceiveTimeout = (int)(timeout ?? TimeSpan.FromSeconds(10)).TotalMilliseconds;
        using var ns = new NetworkStream(s);
        ns.Write(Encoding.UTF8.GetBytes(request + "\n"));
        try
        {
            using var r = new StreamReader(ns, Encoding.UTF8);
            var line = r.ReadLine();
            return line is null ? null : JsonDocument.Parse(line).RootElement.Clone();
        }
        catch { return null; }
    }

    internal static Info? GetInfo()
    {
        var j = Ask("info");
        if (j is not { ValueKind: JsonValueKind.Object } o) return null;
        string? S(string k) => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        int[]? screen = o.TryGetProperty("screen", out var sc) && sc.ValueKind == JsonValueKind.Array
            ? sc.EnumerateArray().Select(e => e.GetInt32()).ToArray() : null;
        return new Info(o.GetProperty("pid").GetInt32(), S("version") ?? "", S("os") ?? "",
            o.TryGetProperty("headless", out var h) && h.ValueKind == JsonValueKind.True, S("display"), S("xauthority"), screen,
            !o.TryGetProperty("ready", out var rd) || rd.ValueKind != JsonValueKind.False, S("failed"));
    }

    // ---------------------------------------------------------------- starting the browser

    /// <summary>Makes sure a browser answers, starting one in the background when none does. Returns false (after saying
    /// why on stderr) when it could not.</summary>
    internal static bool EnsureRunning(bool quiet = false)
    {
        if (GetInfo() is not null) return true;
        return StartDaemon(headless: null, url: null, quiet) == 0;
    }

    /// <summary>Starts BrAIn in the background (headless on Linux when there is no display, or when asked) and waits until
    /// it answers. 0 = running.</summary>
    internal static int StartDaemon(bool? headless, string? url, bool quiet, string[]? extra = null)
    {
        if (GetInfo() is { } running)
        {
            if (!quiet) Console.Error.WriteLine($"BrAIn is already running (pid {running.Pid}{(running.Headless ? ", headless" : "")}).");
            return 0;
        }
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) { Console.Error.WriteLine("brain: cannot tell where BrAIn is installed"); return 2; }
        var log = ControlPaths.DaemonLog();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            if (File.Exists(log) && new FileInfo(log).Length > 4 << 20) File.Move(log, log + ".1", true);
        }
        catch { }

        var psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        psi.ArgumentList.Add("-c");
        // exec: the browser takes the shell's place; its output goes to the log; it detaches from the terminal itself (setsid)
        psi.ArgumentList.Add("exec \"$0\" \"$@\" </dev/null >>\"$BRAIN_DAEMON_LOG\" 2>&1");
        psi.ArgumentList.Add(exe);
        psi.ArgumentList.Add("--daemon");
        if (headless == true && OperatingSystem.IsLinux()) psi.ArgumentList.Add("--headless");
        foreach (var x in extra ?? []) if (x is not "--daemon") psi.ArgumentList.Add(x);
        if (!string.IsNullOrWhiteSpace(url)) psi.ArgumentList.Add(url);
        psi.Environment["BRAIN_DAEMON_LOG"] = log;
        var showProgress = !quiet && Posix.IsTty(2);
        if (showProgress) Console.Error.Write("Starting BrAIn…");
        Process? p;
        try { p = Process.Start(psi); }
        catch (Exception ex) { Console.Error.WriteLine($"\nbrain: could not start BrAIn: {ex.Message}"); return 2; }
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(45))
        {
            Thread.Sleep(250);
            if (GetInfo() is { } info)
            {
                if (info.Failed is { } why)
                {
                    if (showProgress) Console.Error.WriteLine();
                    Console.Error.WriteLine($"brain: BrAIn started but its browser view did not ({why}). Log: {log}");
                    return 2;
                }
                if (!info.Ready) continue; // answering, but the first view is still being made: commands would have to wait anyway
                Ask("warm", TimeSpan.FromSeconds(20)); // the very first page evaluation after a start can get lost: absorb that here
                if (showProgress) Console.Error.WriteLine($" ready (pid {info.Pid}{(info.Headless ? $", headless on {info.Display}" : "")}).");
                return 0;
            }
            if (p is { HasExited: true })
            {
                if (showProgress) Console.Error.WriteLine();
                Console.Error.WriteLine("brain: BrAIn stopped right after starting. The end of its log (" + log + "):");
                try { foreach (var l in File.ReadLines(log).TakeLast(15)) Console.Error.WriteLine("  " + l); } catch { }
                return 2;
            }
        }
        if (showProgress) Console.Error.WriteLine();
        Console.Error.WriteLine("brain: BrAIn did not answer within 45 s (log: " + log + ")");
        return 2;
    }

    internal static int Stop()
    {
        var info = GetInfo();
        if (info is null) { Console.Error.WriteLine("BrAIn is not running."); return 0; }
        Ask("quit");
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(15))
        {
            Thread.Sleep(200);
            if (!Posix.Alive(info.Pid) && TryConnect() is null) { Console.Error.WriteLine("BrAIn stopped."); return 0; }
        }
        Console.Error.WriteLine($"brain: BrAIn (pid {info.Pid}) is still running after 15 s.");
        return 1;
    }
}

/// <summary>A minimal MCP client over the control socket: initialize once, then tools/call.</summary>
internal sealed class McpSession : IDisposable
{
    private readonly Socket _sock;
    private readonly NetworkStream _ns;
    private readonly StreamReader _r;
    private int _id;

    private McpSession(Socket s)
    {
        _sock = s;
        _ns = new NetworkStream(s, ownsSocket: true);
        _r = new StreamReader(_ns, new UTF8Encoding(false));
    }

    internal static McpSession? Open()
    {
        var s = ControlClient.TryConnect();
        if (s is null) return null;
        var m = new McpSession(s);
        m.Raw("mcp\n");
        m.Request("initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-06-18",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "brain-cli", ["version"] = typeof(McpSession).Assembly.GetName().Version?.ToString(3) },
        });
        m.Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" });
        return m;
    }

    private void Raw(string s) { _ns.Write(Encoding.UTF8.GetBytes(s)); _ns.Flush(); }

    private void Send(JsonObject msg) => Raw(msg.ToJsonString() + "\n");

    internal JsonElement Request(string method, JsonObject? prms)
    {
        var id = ++_id;
        Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = prms ?? new JsonObject() });
        while (true)
        {
            var line = _r.ReadLine() ?? throw new IOException("BrAIn closed the connection");
            if (line.Length == 0) continue;
            JsonElement m;
            try { m = JsonDocument.Parse(line).RootElement.Clone(); } catch { continue; }
            var hasId = m.TryGetProperty("id", out var mid);
            if (m.TryGetProperty("method", out _))
            {
                // a request from the server (none is expected): say we cannot; notifications are ignored
                if (hasId) Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = JsonNode.Parse(mid.GetRawText()), ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "not supported by the brain command" } });
                continue;
            }
            if (!hasId || mid.ValueKind != JsonValueKind.Number || mid.GetInt32() != id) continue;
            if (m.TryGetProperty("error", out var err))
                throw new InvalidOperationException(err.TryGetProperty("message", out var em) ? em.GetString() : err.GetRawText());
            return m.GetProperty("result");
        }
    }

    internal ToolResult Call(string tool, JsonObject? args = null)
    {
        var res = Request("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = args ?? new JsonObject() });
        var text = new StringBuilder();
        var images = new List<(string, byte[])>();
        if (res.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            foreach (var c in content.EnumerateArray())
            {
                var type = c.TryGetProperty("type", out var t) ? t.GetString() : "";
                if (type == "text" && c.TryGetProperty("text", out var tx))
                {
                    if (text.Length > 0) text.Append('\n');
                    text.Append(tx.GetString());
                }
                else if (type == "image" && c.TryGetProperty("data", out var d))
                    images.Add((c.TryGetProperty("mimeType", out var mt) ? mt.GetString() ?? "image/png" : "image/png", Convert.FromBase64String(d.GetString() ?? "")));
            }
        var isError = res.TryGetProperty("isError", out var ie) && ie.ValueKind == JsonValueKind.True;
        return new ToolResult(text.ToString(), images, isError);
    }

    internal List<(string Name, string Description)> Tools()
    {
        var res = Request("tools/list", null);
        var list = new List<(string, string)>();
        if (res.TryGetProperty("tools", out var tools))
            foreach (var t in tools.EnumerateArray())
                list.Add((t.GetProperty("name").GetString() ?? "", t.TryGetProperty("description", out var d) ? d.GetString() ?? "" : ""));
        return list;
    }

    /// <summary>The input schema of one tool, for <c>brain call tool key=value</c> (numbers and booleans by type).</summary>
    internal JsonElement? Schema(string tool)
    {
        var res = Request("tools/list", null);
        if (res.TryGetProperty("tools", out var tools))
            foreach (var t in tools.EnumerateArray())
                if (t.GetProperty("name").GetString() == tool && t.TryGetProperty("inputSchema", out var s)) return s.Clone();
        return null;
    }

    public void Dispose()
    {
        try { _r.Dispose(); } catch { }
        try { _ns.Dispose(); } catch { }
        try { _sock.Dispose(); } catch { }
    }
}
