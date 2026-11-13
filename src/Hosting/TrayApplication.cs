using System.Diagnostics;
using Microsoft.Win32;

namespace Athena.Desktop.Hosting;

internal sealed class TrayApplication : ApplicationContext
{
    private readonly DesktopRuntime runtime;
    private readonly Control dispatcher = new();
    private readonly NotifyIcon icon;
    private readonly ContextMenuStrip menu = new();
    private readonly IDisposable stopRegistration;
    private bool stopping;

    public TrayApplication(DesktopRuntime runtime, DesktopInstance instance, string logPath, string mode = "mcp")
    {
        this.runtime = runtime;
        _ = dispatcher.Handle;
        menu.Items.Add(mode == "mcp" ? "Managing Windows-MCP" : "Legacy command listener", null, (_, _) => { }).Enabled = false;
        menu.Items.Add("Open logs", null, (_, _) => OpenLogs(logPath));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Stop());
        icon = new NotifyIcon { Icon = SystemIcons.Application, Text = "Athena Desktop", ContextMenuStrip = menu, Visible = true };
        stopRegistration = instance.OnStop(() => Post(Stop));
        SystemEvents.SessionEnding += SessionEnding;
        _ = ObserveCompletionAsync();
    }

    private async Task ObserveCompletionAsync()
    {
        var exitCode = await runtime.Completion.ConfigureAwait(false);
        Post(() =>
        {
            if (exitCode != 0 && !stopping)
                MessageBox.Show("Athena Desktop's backend stopped. Open the log file for details.",
                    "Athena Desktop", MessageBoxButtons.OK, MessageBoxIcon.Error);
            ExitThread();
        });
    }

    private void Post(Action action)
    {
        try { dispatcher.BeginInvoke(action); }
        catch (InvalidOperationException) when (dispatcher.IsDisposed) { }
    }

    private async void Stop()
    {
        if (stopping) return;
        stopping = true;
        icon.Text = "Athena Desktop stopping";
        await runtime.StopAsync();
        ExitThread();
    }

    private void SessionEnding(object? sender, SessionEndingEventArgs args) => runtime.RequestStop();

    private static void OpenLogs(string path)
    {
        try { using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception exception) { Console.Error.WriteLine($"Could not open logs: {exception.Message}"); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.SessionEnding -= SessionEnding;
            stopRegistration.Dispose();
            icon.Visible = false;
            icon.Dispose();
            menu.Dispose();
            dispatcher.Dispose();
        }
        base.Dispose(disposing);
    }
}
