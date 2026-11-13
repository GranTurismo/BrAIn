using BrAIn.Browser;
using BrAIn.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;

namespace BrAIn;

public static class Program
{
    // STA is mandatory: WebView2's CreateCoreWebView2EnvironmentWithOptions
    // fails with RPC_E_CHANGED_MODE on the default MTA main thread.
    // Must be a synchronous entry point — the attribute is ignored on
    // async Main because the compiler synthesizes the real entry point.
    [STAThread]
    public static int Main(string[] args)
    {
        // `brain open …`, `brain view`, …: the command line talks to the running browser (starting one when needed) and
        // never opens a window of its own.
        // Plain `brain` typed in a terminal on a machine with no display: there is no window to open here, so say how it is
        // used instead of starting an invisible browser in the foreground (`BrAIn --headless` still does that on purpose).
        if (OperatingSystem.IsLinux() && args.Length == 0 && !Cli.Headless.HasDisplay() && Cli.Posix.IsTty(0) && Cli.Posix.IsTty(1))
            args = ["help"];
        if (Cli.CliMain.IsCommand(args))
        {
            if (OperatingSystem.IsWindows()) AttachConsole(-1);
            var rc = Cli.CliMain.Run(args);
            Environment.Exit(rc);
            return rc;
        }
        var code = MainAsync(args).GetAwaiter().GetResult();
        // Whatever is still parked (the MCP reader on stdin, WebKit helper threads) must not keep a closed browser alive.
        Environment.Exit(code);
        return code;
    }

    private static async Task<int> MainAsync(string[] args)
    {
        // A GUI-subsystem exe gets no console on Windows; when the user asks
        // for CLI output, reattach to the console they launched us from so
        // the text actually reaches their terminal. No-op elsewhere — MCP
        // clients give us pipes, not consoles.
        if (OperatingSystem.IsWindows() &&
            (args.Contains("--help") || args.Contains("-h") ||
             args.Contains("--version") || args.Contains("--verbose")))
            AttachConsole(-1); // ATTACH_PARENT_PROCESS

        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.Error.WriteLine("""
                BrAIn - AI-controllable browser (Photino.NET + MCP server)

                Usage: BrAIn [options] [url]

                  url                 Initial URL to open (default: built-in new tab page)
                  --cdp-port <port>   Force a fixed CDP debugging port (Windows)
                  --no-toolbar        Do not inject the BrAIn toolbar into pages
                  --grant-permissions Auto-allow camera/mic/location/etc. without asking
                                      (default: sites show a permission prompt)
                  --no-fullscreen     Do not open in full screen
                  --no-split          (macOS) keep the toolbar inside the page instead of in
                                      its own native view above it (also BRAIN_SPLIT=0)
                  --selftest-tabs     Run the tab-switching self-test, write selftest-tabs.txt, exit
                                      (--selftest-quiet skips the result dialog on Windows;
                                      --selftest-evict also exercises the opt-in live-view cap)
                  --headless          (Linux) run on an invisible screen of its own: automatic when
                                      there is no display (a server, a container, an SSH login)
                  --version           Print version

                Command line (talks to the running BrAIn, starts one when needed):
                  BrAIn help          every command: open, read, snap, click, type, shot, tabs,
                                      view (the browser live in your terminal), mcp, …

                MCP: this process speaks MCP over stdio. Point your MCP client
                config at this executable (or at `BrAIn mcp` to share a running
                browser). All diagnostics go to stderr.
                """);
            return 0;
        }

        if (args.Contains("--version"))
        {
            Console.Error.WriteLine(typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0");
            return 0;
        }

        // Linux: without a usable OpenSSL the .NET runtime abort()s the whole process the first time anything needs crypto or TLS
        // (the update check, a favicon hash, the vault) — a few seconds in, uncatchable. Say what is missing instead.
        if (OperatingSystem.IsLinux() && !Browser.Linux.LinuxPreflight.Ok()) return 1;

        // --daemon: started in the background by `brain start` (or any brain command that found no browser running). It has no
        // MCP client on stdio, leaves the terminal's session and outlives the login that started it.
        var daemon = args.Contains("--daemon") && !OperatingSystem.IsWindows();
        // first leave the terminal's session, so nothing started below (the X server!) gets its hang-up at logout
        if (daemon) Cli.Headless.Detach();
        // Linux with no display: bring an invisible screen (Xvfb) before anything touches GTK.
        var headless = false;
        if (OperatingSystem.IsLinux() && (args.Contains("--headless") || !Cli.Headless.HasDisplay()))
        {
            if (!Cli.Headless.Start(Cli.ControlPaths.StoreDir(), daemon, out var why))
            {
                Console.Error.WriteLine("[BrAIn] " + why);
                return 1;
            }
            headless = true;
            BrowserController.Log($"headless: invisible screen {Cli.Headless.Display}");
        }

        var selfTest = args.Contains("--selftest-tabs");
        if (selfTest && args.Contains("--selftest-evict")) Environment.SetEnvironmentVariable("BRAIN_MAXLIVE", "3"); // opt-in cap to exercise eviction
        BrowserController.Mark("main");
        // fullscreen needs a window manager, which the invisible screen does not have
        var controller = new BrowserController(headless && !args.Contains("--no-fullscreen") ? [.. args, "--no-fullscreen"] : args)
        {
            Headless = headless,
        };
        BrowserController.Mark("controller ready");
        var selfTestFails = 0;
        if (selfTest)
            _ = Task.Run(async () =>
            {
                var run = SelfTest.RunTabsAsync(controller);
                if (await Task.WhenAny(run, Task.Delay(TimeSpan.FromMinutes(4))) == run) selfTestFails = await run;
                else
                {
                    selfTestFails = 99;
                    BrowserController.Log("selftest: timed out after 4 minutes");
                    controller.CloseAllWindows();
                }
            });

        // MCP host on a background thread; stdio transport must keep stdout
        // clean, so all logging is pushed to stderr.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = Array.Empty<string>(),
            DisableDefaults = true,
        });
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(args.Contains("--verbose") ? LogLevel.Information : LogLevel.Warning);
        builder.Services.AddSingleton(controller);
        builder.Services.AddSingleton(controller.Tabs);
        builder.Services.AddSingleton(controller.Activity);
        builder.Services.AddSingleton(controller.Flows);
        builder.Services.AddSingleton(controller.Apis);
        builder.Services.AddSingleton(controller.Scripts);
        // stdio MCP only when an MCP client is really on the other end (a pipe or socket). A desktop launch hands us /dev/null or
        // a terminal: a stdio session would end at once, and stopping the host would dispose the services the `brain` command's
        // sessions run on. (Windows keeps its old behaviour; it has no `brain` command.)
        var clientPipe = StdinIsClientPipe();
        var useStdio = !daemon && (OperatingSystem.IsWindows() || clientPipe);
        var mcp = builder.Services.AddMcpServer();
        if (useStdio) mcp.WithStdioServerTransport();
        mcp
            .WithTools<BrowserTools>()
            .WithTools<ProfileTools>()
            .WithTools<FlowTools>()
            .WithTools<UserScriptTools>()
            .WithTools<ApiTools>()
            // Record every tool call (primitives and flow_* alike) in calls.jsonl.
            .WithRequestFilters(f => f.AddCallToolFilter(ActivityRecorder.Create(controller, controller.Activity)));

        var host = builder.Build();

        // Saved flows become real MCP tools: hand the live tool list to the
        // flow service, which adds/removes flow_* entries (the SDK announces
        // each change to the client via tools/list_changed).
        ModelContextProtocol.Server.McpServerOptions? mcpOptions = null;
        try
        {
            mcpOptions = host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ModelContextProtocol.Server.McpServerOptions>>().Value;
            mcpOptions.ToolCollection ??= new ModelContextProtocol.Server.McpServerPrimitiveCollection<ModelContextProtocol.Server.McpServerTool>();
            controller.Flows.Attach(mcpOptions.ToolCollection);
            controller.Apis.Attach(mcpOptions.ToolCollection);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[BrAIn] flows unavailable: {ex.Message}"); }

        // The `brain` command's way in (macOS/Linux): the same tools and checks over a private Unix socket.
        Cli.ControlServer? control = null;
        if (mcpOptions is not null && !selfTest)
            control = Cli.ControlServer.Start(controller, host.Services, mcpOptions, host.Services.GetRequiredService<ILoggerFactory>());
        if (daemon && control is null)
        {
            // a background browser nobody can reach would only be in the way (the reason is logged above)
            Console.Error.WriteLine("[BrAIn] the control socket could not be opened, so this background browser could not be used: exiting");
            Cli.Headless.Stop();
            return 1;
        }

        // SIGTERM (kill, systemctl stop, shutdown) and SIGINT (Ctrl+C in the terminal that started it) close the browser
        // properly. Without this the hosting layer takes them: it stops the services the brain command's sessions run on and
        // leaves the window running.
        PosixSignalRegistration? sigTerm = null, sigInt = null;
        if (!OperatingSystem.IsWindows())
        {
            void Close(PosixSignalContext c) { c.Cancel = true; controller.CloseAllWindows(); }
            try
            {
                sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Close);
                sigInt = PosixSignalRegistration.Create(PosixSignal.SIGINT, Close);
            }
            catch { /* not supported: keep the defaults */ }
        }

        // stdio MCP servers exit when the client closes stdin — the transport
        // stops the host, and we close the windows so the process doesn't
        // linger as a zombie with no server attached. But only when stdin is
        // actually a client pipe: Finder/Dock launches hand us /dev/null (a
        // character device), which EOFs instantly and would otherwise kill
        // the window the moment the app opens.
        if (useStdio && clientPipe)
            host.Services.GetRequiredService<IHostApplicationLifetime>()
                .ApplicationStopped.Register(controller.CloseAllWindows);

        var hostTask = Task.Run(() => host.RunAsync());

        int exitCode = 0;
        try
        {
            // Blocks until the browser window closes (runs the OS message loop).
            controller.RunWindow();
            BrowserController.Mark("window loop ended");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[BrAIn] fatal: {ex}");
            exitCode = 1;
        }
        finally
        {
            // The window is gone, so the app is done. The MCP host gets a short moment to say goodbye, but it is never
            // waited for: its stdin reader cannot be cancelled, so while the client keeps the pipe open StopAsync can
            // take its full timeout or never return at all — which used to leave a windowless process lingering for
            // seconds, or for good. It stops on its own thread; the process ends right after (see Main).
            var stopped = Task.Run(async () =>
            {
                try { await host.StopAsync(TimeSpan.FromSeconds(1)); }
                catch { /* shutting down anyway */ }
            });
            await Task.WhenAny(stopped, Task.Delay(TimeSpan.FromSeconds(1.5)));
            control?.Dispose();
            Cli.Headless.Stop();
            sigTerm?.Dispose(); // (also keeps both registrations alive until here)
            sigInt?.Dispose();
            BrowserController.Mark("exiting");
        }
        return selfTest ? Math.Max(exitCode, selfTestFails) : exitCode;
    }

    /// <summary>
    /// True when stdin is a pipe/socket — i.e. an MCP client spawned us and
    /// its disconnect should end the process. Character devices (/dev/null on
    /// Finder launch, a TTY on terminal launch) and unreadable stdin mean
    /// "standalone": the window outlives stdin.
    /// </summary>
    private static bool StdinIsClientPipe()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // FILE_TYPE_PIPE; consoles and NUL report FILE_TYPE_CHAR.
                return GetFileType(GetStdHandle(-10)) == 3;
            }
            if (OperatingSystem.IsLinux())
            {
                // struct stat is laid out differently here, so ask /proc what fd 0 is: "pipe:[n]" / "socket:[n]" is a
                // client; /dev/null (desktop launch), a tty or a file is "standalone".
                var target = new FileInfo("/proc/self/fd/0").LinkTarget ?? "";
                return target.StartsWith("pipe:", StringComparison.Ordinal) || target.StartsWith("socket:", StringComparison.Ordinal);
            }
            var buf = new byte[256];
            if (fstat(0, buf) != 0) return true; // can't tell → assume client
            // struct stat: st_mode is a uint16 at offset 4 (st_dev then mode).
            var fmt = BitConverter.ToUInt16(buf, 4) & 0xF000;
            return fmt is 0x1000 or 0xC000; // S_IFIFO | S_IFSOCK
        }
        catch { return true; }
    }

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int fstat(int fd, [System.Runtime.InteropServices.Out] byte[] buf);

    [System.Runtime.InteropServices.DllImport("kernel32")]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [System.Runtime.InteropServices.DllImport("kernel32")]
    private static extern bool AttachConsole(int dwProcessId);

    [System.Runtime.InteropServices.DllImport("kernel32")]
    private static extern uint GetFileType(IntPtr hFile);
}
