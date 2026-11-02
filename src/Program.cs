using Athena.Desktop.Dispatch;
using Athena.Desktop.Hosting;
using Athena.Desktop.Mcp;
using Athena.Desktop.Server;
using Athena.Desktop.Server.Endpoints;

namespace Athena.Desktop;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Internal launch gate has no tray, instance mutex, or network listener.
        if (args.SequenceEqual(["--mcp-worker"])) return McpProcessWorker.RunAsync().GetAwaiter().GetResult();
        if (args.Length > 1 || (args.Length == 1 && args[0] is not
            ("--console" or "--stop" or "--status" or "--check-config" or "--help")))
        {
            DesktopConsole.Attach();
            Console.Error.WriteLine("Usage: Athena.Desktop [--console | --stop | --status | --check-config | --help]");
            return 2;
        }
        if (args.Contains("--help"))
        {
            DesktopConsole.Attach();
            Console.WriteLine("Athena Desktop runs in the system tray by default.");
            Console.WriteLine("--console: run with terminal logs and Ctrl-C. --stop: exit gracefully.");
            Console.WriteLine("--status: exit 0 when running, 1 when stopped (current user/session).");
            Console.WriteLine("--check-config: validate local settings without starting a process or opening the network.");
            Console.WriteLine("Default: supervise Windows-MCP. ATHENA_DESKTOP_MODE=legacy enables the old command listener.");
            return 0;
        }
        if (args.Contains("--status")) return DesktopInstance.IsRunning() ? 0 : 1;
        if (args.Contains("--stop")) return DesktopInstance.Stop(TimeSpan.FromSeconds(10)) ? 0 : 1;
        if (args.Contains("--check-config"))
        {
            DesktopConsole.Attach();
            try
            {
                var mode = GetMode();
                if (mode == "mcp") _ = LoadMcpOptions();
                else _ = GetPrefix();
                Console.WriteLine($"Athena Desktop {mode} configuration is valid. No process or network check performed.");
                return 0;
            }
            catch (Exception exception) { Console.Error.WriteLine(exception.Message); return 1; }
        }

        var console = args.Contains("--console");
        if (console) DesktopConsole.Attach();
        using var instance = DesktopInstance.TryAcquire();
        if (instance is null)
        {
            if (console) Console.Error.WriteLine("Athena Desktop is already running in this session.");
            return 0;
        }

        var logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AthenaDesktop", "logs", "athena.log");
        try
        {
            using var log = new DesktopLog(logPath, console);
            var mode = GetMode();
            using var legacyServer = mode == "legacy" ? CreateLegacyServer() : null;
            var mcp = mode == "mcp" ? LoadMcpOptions() : null;
            Func<CancellationToken, Task> run = legacyServer is not null ? legacyServer.RunAsync : new WindowsMcpSupervisor(mcp!).RunAsync;
            var description = mcp is not null ? $"Windows-MCP ({mcp.EndpointUri})" : $"legacy HTTP ({GetPrefix()})";
            using var runtime = new DesktopRuntime(run, description);
            if (console)
            {
                ConsoleCancelEventHandler cancel = (_, eventArgs) =>
                {
                    eventArgs.Cancel = true;
                    runtime.RequestStop();
                };
                Console.CancelKeyPress += cancel;
                using var stop = instance.OnStop(runtime.RequestStop);
                try { return runtime.Completion.GetAwaiter().GetResult(); }
                finally { Console.CancelKeyPress -= cancel; }
            }

            ApplicationConfiguration.Initialize();
            using var tray = new TrayApplication(runtime, instance, logPath, mode);
            Application.Run(tray);
            return runtime.Completion.GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            if (console) Console.Error.WriteLine($"Athena Desktop could not start: {exception.Message}");
            else MessageBox.Show($"Athena Desktop could not start: {exception.Message}", "Athena Desktop",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static CommandServer CreateLegacyServer()
    {
        var dispatcher = new CommandDispatcher();
        return new CommandServer(new HttpRouter([new HealthEndpoint(), new CommandsEndpoint(dispatcher.DispatchAsync)]), GetPrefix());
    }

    private static string GetMode()
    {
        var mode = Setting("ATHENA_DESKTOP_MODE")?.Trim().ToLowerInvariant() ?? "mcp";
        if (mode is not ("mcp" or "legacy")) throw new InvalidOperationException("ATHENA_DESKTOP_MODE must be mcp or legacy.");
        return mode;
    }

    private static WindowsMcpOptions LoadMcpOptions() => WindowsMcpOptions.Load(
        Setting("ATHENA_MCP_SETTINGS") ?? WindowsMcpOptions.DefaultSettingsPath);

    private static string? Setting(string name)
    {
        var inherited = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(inherited)
            ? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User) : inherited;
    }

    private static string GetPrefix()
    {
        var prefix = Setting("ATHENA_HTTP_PREFIX");
        if (!string.IsNullOrWhiteSpace(prefix)) return prefix.Trim();
        var host = Setting("ATHENA_BIND_HOST");
        return new UriBuilder("http", string.IsNullOrWhiteSpace(host) ? "localhost" : host.Trim(), 5000)
            .Uri.AbsoluteUri;
    }
}
