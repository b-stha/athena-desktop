using System.Diagnostics;

namespace Athena.Desktop.Legacy.Actions;

public sealed class PowerActions
{
    public void Shutdown()
    {
        RunShutdownCommand("/s", "shutdown");
    }

    public void Restart()
    {
        RunShutdownCommand("/r", "restart");
    }

    public void Sleep()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Sleep is only supported on Windows.");
        }

        WindowsSleepAction.Sleep();
    }

    private static void RunShutdownCommand(string operation, string action)
    {
        // /t 0 avoids the implicit /f used by Windows for a positive timeout.
        // Applications with unsaved work may block shutdown or restart.
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe"),
            ArgumentList = { operation, "/t", "0" },
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException($"Could not start the Windows {action} command.");

        // Check whether Windows accepted the request, not whether it finished.
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Windows rejected the {action} request (exit code {process.ExitCode}).");
        }
    }
}
