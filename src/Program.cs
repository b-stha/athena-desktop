using Athena.Desktop.Hosting;
using Athena.Desktop.Mcp;

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
            Console.WriteLine("Athena Desktop supervises Windows-MCP.");
            return 0;
        }
        if (args.Contains("--status")) return DesktopInstance.IsRunning() ? 0 : 1;
        if (args.Contains("--stop")) return DesktopInstance.Stop(TimeSpan.FromSeconds(10)) ? 0 : 1;
        if (args.Contains("--check-config"))
        {
            DesktopConsole.Attach();
            try
            {
                _ = LoadMcpOptions();
                Console.WriteLine("Athena Desktop MCP configuration is valid. No process or network check performed.");
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
            var mcp = LoadMcpOptions();
            using var runtime = new DesktopRuntime(new WindowsMcpSupervisor(mcp).RunContinuouslyAsync,
                $"Windows-MCP ({mcp.EndpointUri})");
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
            using var tray = new TrayApplication(runtime, instance, logPath);
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

    private static WindowsMcpOptions LoadMcpOptions() => WindowsMcpOptions.Load(
        Setting("ATHENA_MCP_SETTINGS") ?? WindowsMcpOptions.DefaultSettingsPath);

    private static string? Setting(string name)
    {
        var inherited = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(inherited)
            ? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User) : inherited;
    }

}
