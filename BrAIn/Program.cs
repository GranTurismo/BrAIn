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
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.Error.WriteLine("""
                BrAIn - AI-controllable browser (Photino.NET + MCP server)

                Usage: BrAIn [options] [url]

                  url                 Initial URL to open (default: built-in new tab page)
                  --cdp-port <port>   Force a fixed CDP debugging port (Windows)
                  --no-toolbar        Do not inject the BrAIn toolbar into pages
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

        var controller = new BrowserController(args);

        // MCP host on a background thread; stdio transport must keep stdout
        // clean, so all logging is pushed to stderr.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = Array.Empty<string>(),
            DisableDefaults = true,
        });
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Services.AddSingleton(controller);
        builder.Services.AddSingleton(controller.Tabs);
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly(typeof(Program).Assembly);

        var host = builder.Build();

        // stdio MCP servers exit when the client closes stdin — the transport
        // stops the host, and we close the windows so the process doesn't
        // linger as a zombie with no server attached.
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
        return exitCode;
    }
}
