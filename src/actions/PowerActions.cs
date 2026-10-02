using System.Diagnostics;

namespace Athena.Desktop.Actions;

public sealed class PowerActions
{
    public void Shutdown()
    {
        // /s requests shutdown; /t 0 starts it without a countdown.
        // Do not add /f: applications with unsaved work may block shutdown.
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe"),
            ArgumentList = { "/s", "/t", "0" },
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Could not start the Windows shutdown command.");

        // Check whether Windows accepted the request, not whether shutdown finished.
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Windows rejected the shutdown request (exit code {process.ExitCode}).");
        }
    }

    public void Restart()
    {
        throw new NotImplementedException("Restart is not implemented yet.");
    }

    public void Sleep()
    {
        throw new NotImplementedException("Sleep is not implemented yet.");
    }
}
