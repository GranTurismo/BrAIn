using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using BrAIn.Browser;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace BrAIn.Cli;

/// <summary>Where the running browser listens for the <c>brain</c> command. One per data folder, so a test copy of BrAIn
/// with its own home folder has its own. BRAIN_SOCKET overrides it.</summary>
internal static class ControlPaths
{
    internal static string StoreDir() => Path.Combine(OsPaths.LocalAppData(), "BrAIn");

    internal static string Socket()
    {
        var env = Environment.GetEnvironmentVariable("BRAIN_SOCKET");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        var p = Path.Combine(StoreDir(), "brain.sock");
        if (Encoding.UTF8.GetByteCount(p) < 100) return p; // sun_path holds 104 (macOS) / 108 (Linux) bytes
        // a data folder with a very long path: a short private place, named after that folder so two never share a socket
        var tag = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(StoreDir())))[..12].ToLowerInvariant();
        var run = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        var dir = !string.IsNullOrEmpty(run) && Directory.Exists(run) ? Path.Combine(run, "brain") : Path.Combine(Path.GetTempPath(), $"brain-{Posix.geteuid()}");
        return Path.Combine(dir, tag + ".sock");
    }

    internal static string DaemonLog() => Path.Combine(StoreDir(), "brain-daemon.log");
}

/// <summary>
/// The running browser's end of the <c>brain</c> command: a Unix socket in BrAIn's private data folder. Every connection
/// starts with one line saying what it wants:
///
///   <c>mcp</c>            the MCP protocol, newline-delimited JSON-RPC: the same tools, the same safety checks and the same
///                         activity log as an MCP client on stdio (the CLI and <c>brain mcp</c> use this)
///   <c>info</c>           one JSON line: pid, version, whether it is headless, its display
///   <c>view W H</c>       headless only: size the window to W×H at the screen's top-left corner (the terminal viewer)
///   <c>warm</c>           wait until the page answers evaluations (right after a start)
///   <c>grid K M CSS</c>   headless only: the text layer for a terminal without pictures (TextGridJs)
///   <c>quit</c>           close the browser
///
/// Only the account that runs the browser can connect: the folder is private (0700), the socket is 0600 and the peer's uid is
/// checked. That gives nothing a same-user program could not already do by starting BrAIn as an MCP server itself.
/// </summary>
internal sealed class ControlServer : IDisposable
{
    private readonly BrowserController _browser;
    private readonly IServiceProvider _services;
    private readonly McpServerOptions _options;
    private readonly ILoggerFactory _logs;
    private readonly Socket _listen;
    private readonly string _path;
    private readonly CancellationTokenSource _stop = new();

    private ControlServer(BrowserController browser, IServiceProvider services, McpServerOptions options, ILoggerFactory logs,
        Socket listen, string path)
    {
        _browser = browser; _services = services; _options = options; _logs = logs; _listen = listen; _path = path;
    }

    /// <summary>Starts listening, or returns null (logged) when another BrAIn already answers on the socket or it cannot be made.</summary>
    internal static ControlServer? Start(BrowserController browser, IServiceProvider services, McpServerOptions options, ILoggerFactory logs)
    {
        if (OperatingSystem.IsWindows()) return null;
        var path = ControlPaths.Socket();
        try
        {
            var dir = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            try { File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } catch { }
            if (File.Exists(path))
            {
                // A live BrAIn already owns it (a second window started by hand, or an MCP client's copy): leave it alone.
                if (Answers(path))
                {
                    BrowserController.Log("brain command: another BrAIn already listens on " + path + " - this one does not");
                    return null;
                }
                File.Delete(path); // left behind by a crash
            }
            var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            s.Bind(new UnixDomainSocketEndPoint(path));
            try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
            s.Listen(16);
            var server = new ControlServer(browser, services, options, logs, s, path);
            _ = Task.Run(server.AcceptLoop);
            BrowserController.Log("brain command: listening on " + path);
            return server;
        }
        catch (Exception ex)
        {
            BrowserController.Log("brain command: cannot listen on " + path + ": " + ex.Message);
            return null;
        }
    }

    private static bool Answers(string path)
    {
        try
        {
            using var c = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            c.Connect(new UnixDomainSocketEndPoint(path));
            return true;
        }
        catch { return false; }
    }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket c;
            try { c = await _listen.AcceptAsync(_stop.Token); }
            catch { if (_stop.IsCancellationRequested) return; await Task.Delay(50); continue; }
            _ = Task.Run(() => Serve(c));
        }
    }

    private async Task Serve(Socket c)
    {
        try
        {
            if (!PeerIsUs(c)) { c.Dispose(); return; }
            await using var ns = new NetworkStream(c, ownsSocket: true);
            var line = await ReadLineAsync(ns, TimeSpan.FromSeconds(10));
            if (line is null) return;
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            switch (parts.FirstOrDefault())
            {
                case "mcp":
                    {
                        var transport = new StreamServerTransport(ns, ns, "brain-cli", _logs);
                        await using var server = McpServer.Create(transport, _options, _logs, _services);
                        await server.RunAsync(_stop.Token);
                        break;
                    }
                case "info":
                    await Reply(ns, Info());
                    break;
                case "view" when parts.Length >= 3 && int.TryParse(parts[1], out var w) && int.TryParse(parts[2], out var h):
                    await Reply(ns, _browser.SetHeadlessViewSize(w, h));
                    break;
                case "grid" when parts.Length >= 4 && int.TryParse(parts[1], out var gk) && int.TryParse(parts[2], out var gm) && int.TryParse(parts[3], out var gc):
                    await Reply(ns, await _browser.TextGridAsync(gk, gm, gc));
                    break;
                case "focusat" when parts.Length >= 3 && int.TryParse(parts[1], out var fx) && int.TryParse(parts[2], out var fy):
                    await Reply(ns, _browser.FocusAt(fx, fy));
                    break;
                case "warm":
                    await Reply(ns, await _browser.WarmUpAsync());
                    break;
                case "quit":
                    await Reply(ns, "{\"ok\":true}");
                    _ = Task.Run(async () => { await Task.Delay(100); _browser.CloseAllWindows(); });
                    break;
                default:
                    await Reply(ns, "{\"error\":\"unknown request\"}");
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (Exception ex) { BrowserController.Log("brain command: " + ex.Message); }
    }

    private string Info() => JsonSerializer.Serialize(new
    {
        pid = Environment.ProcessId,
        version = typeof(ControlServer).Assembly.GetName().Version?.ToString(3) ?? "",
        os = OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsLinux() ? "linux" : "other",
        headless = Headless.Active,
        display = Headless.Active ? Headless.Display : null,
        xauthority = Headless.Active ? Headless.XAuthority : null,
        screen = Headless.Active ? new[] { Headless.ScreenW, Headless.ScreenH } : null,
        store = ControlPaths.StoreDir(),
        title = _browser.Tabs.Active?.LastTitle,
        url = _browser.Tabs.Active?.LastUrl,
        tabs = _browser.Tabs.All.Count(),
        ready = _browser.CliState.Ready,
        failed = _browser.CliState.Error,
    });

    private static async Task Reply(Stream s, string json)
    {
        var b = Encoding.UTF8.GetBytes(json + "\n");
        await s.WriteAsync(b);
        await s.FlushAsync();
    }

    /// <summary>One line, read byte by byte so nothing after it (the MCP stream) is consumed.</summary>
    private static async Task<string?> ReadLineAsync(Stream s, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var buf = new List<byte>(64);
        var one = new byte[1];
        while (buf.Count < 256)
        {
            int n;
            try { n = await s.ReadAsync(one, cts.Token); }
            catch (OperationCanceledException) { return null; }
            if (n == 0) return null;
            if (one[0] == (byte)'\n') return Encoding.UTF8.GetString(buf.ToArray());
            buf.Add(one[0]);
        }
        return null;
    }

    /// <summary>The connecting process runs as the same user (Linux SO_PEERCRED, macOS LOCAL_PEERCRED). The folder
    /// permissions already keep others out; this is the second lock.</summary>
    private static bool PeerIsUs(Socket c)
    {
        try
        {
            var me = Posix.geteuid();
            if (OperatingSystem.IsLinux())
            {
                var cred = new byte[12]; // struct ucred { pid_t pid; uid_t uid; gid_t gid; }
                var n = c.GetRawSocketOption(1 /* SOL_SOCKET */, 17 /* SO_PEERCRED */, cred);
                return n >= 8 && BitConverter.ToUInt32(cred, 4) == me;
            }
            if (OperatingSystem.IsMacOS())
            {
                var cred = new byte[76]; // struct xucred { u_int cr_version; uid_t cr_uid; short cr_ngroups; gid_t cr_groups[16]; }
                var n = c.GetRawSocketOption(0 /* SOL_LOCAL */, 1 /* LOCAL_PEERCRED */, cred);
                return n >= 8 && BitConverter.ToUInt32(cred, 4) == me;
            }
        }
        catch (Exception ex) when (ex is SocketException or PlatformNotSupportedException or NotSupportedException)
        {
            return true; // the private folder still protects it
        }
        return true;
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _listen.Dispose(); } catch { }
        try { File.Delete(_path); } catch { }
    }
}
