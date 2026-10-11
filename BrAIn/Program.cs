using BrAIn.Browser;
using BrAIn.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BrAIn;

public static class Program
{
    // STA is mandatory: WebView2's CreateCoreWebView2EnvironmentWithOptions
    // fails with RPC_E_CHANGED_MODE on the default MTA main thread.
    // Must be a synchronous entry point — the attribute is ignored on
    // async Main because the compiler synthesizes the real entry point.
    [STAThread]
    public static int Main(string[] args) => MainAsync(args).GetAwaiter().GetResult();

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
                  --version           Print version

                MCP: this process speaks MCP over stdio. Point your MCP client
                config at this executable. All diagnostics go to stderr.
                """);
            return 0;
        }

        if (args.Contains("--version"))
        {
            Console.Error.WriteLine(typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0");
            return 0;
        }

        var selfTest = args.Contains("--selftest-tabs");
        if (selfTest && args.Contains("--selftest-evict")) Environment.SetEnvironmentVariable("BRAIN_MAXLIVE", "3"); // opt-in cap to exercise eviction
        var controller = new BrowserController(args);
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
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<BrowserTools>()
            .WithTools<FlowTools>()
            // Record every tool call (primitives and flow_* alike) in calls.jsonl.
            .WithRequestFilters(f => f.AddCallToolFilter(ActivityRecorder.Create(controller, controller.Activity)));

        var host = builder.Build();

        // Saved flows become real MCP tools: hand the live tool list to the
        // flow service, which adds/removes flow_* entries (the SDK announces
        // each change to the client via tools/list_changed).
        try
        {
            var mcpOptions = host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ModelContextProtocol.Server.McpServerOptions>>().Value;
            mcpOptions.ToolCollection ??= new ModelContextProtocol.Server.McpServerPrimitiveCollection<ModelContextProtocol.Server.McpServerTool>();
            controller.Flows.Attach(mcpOptions.ToolCollection);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[BrAIn] flows unavailable: {ex.Message}"); }

        // stdio MCP servers exit when the client closes stdin — the transport
        // stops the host, and we close the windows so the process doesn't
        // linger as a zombie with no server attached. But only when stdin is
        // actually a client pipe: Finder/Dock launches hand us /dev/null (a
        // character device), which EOFs instantly and would otherwise kill
        // the window the moment the app opens.
        if (StdinIsClientPipe())
            host.Services.GetRequiredService<IHostApplicationLifetime>()
                .ApplicationStopped.Register(controller.CloseAllWindows);

        var hostTask = Task.Run(() => host.RunAsync());

        int exitCode = 0;
        try
        {
            // Blocks until the browser window closes (runs the OS message loop).
            controller.RunWindow();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[BrAIn] fatal: {ex}");
            exitCode = 1;
        }
        finally
        {
            try { await host.StopAsync(TimeSpan.FromSeconds(3)); }
            catch { /* shutting down anyway */ }
            try { await hostTask.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch { /* shutting down anyway */ }
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
