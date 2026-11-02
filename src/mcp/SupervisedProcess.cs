using System.Diagnostics;

namespace Athena.Desktop.Mcp;

/// <summary>Owns one process tree; it never restarts or replays actions after a failure.</summary>
public sealed class SupervisedProcess(ProcessStartInfo startInfo, TimeSpan startupTimeout,
    Func<CancellationToken, Task> waitUntilReady)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var job = new WindowsProcessJob();
        using var child = new Process { StartInfo = McpProcessWorker.CreateStartInfo() };
        if (!child.Start()) throw new InvalidOperationException("Windows-MCP process could not start.");
        var output = ForwardAsync(child.StandardOutput, Console.Out, cancellationToken);
        var error = ForwardAsync(child.StandardError, Console.Error, cancellationToken);
        var exited = child.WaitForExitAsync(CancellationToken.None);
        var ownedByJob = false;
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startup.CancelAfter(startupTimeout);
        try
        {
            job.Assign(child);
            ownedByJob = true;
            // The helper blocks on stdin until ownership is established. Every upstream descendant
            // is therefore born in this job; Python launchers cannot escape a post-Start assignment race.
            cancellationToken.ThrowIfCancellationRequested();
            await child.StandardInput.WriteLineAsync(McpProcessWorker.SerializeLaunch(startInfo).AsMemory(), startup.Token).ConfigureAwait(false);
            child.StandardInput.Close();
            var ready = waitUntilReady(startup.Token);
            var first = await Task.WhenAny(ready, exited).ConfigureAwait(false);
            if (first == exited)
            {
                startup.Cancel();
                try { await ready.ConfigureAwait(false); }
                catch (OperationCanceledException) when (startup.IsCancellationRequested) { }
                cancellationToken.ThrowIfCancellationRequested();
                throw new InvalidOperationException($"Windows-MCP exited before readiness (code {child.ExitCode}).");
            }
            try { await ready.ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Windows-MCP did not open its configured network port before the startup timeout.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (child.HasExited) throw new InvalidOperationException($"Windows-MCP exited during startup (code {child.ExitCode}).");
            Console.WriteLine("Windows-MCP network listener is available. The Pi checks MCP discovery and authentication.");
            await exited.WaitAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"Windows-MCP stopped unexpectedly (code {child.ExitCode}).");
        }
        catch (OperationCanceledException) when (startup.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Windows-MCP launch helper or network listener did not become ready before the startup timeout.");
        }
        finally
        {
            // A Python entry-point launcher may exit before its worker. The job still owns that worker.
            job.Dispose();
            // Exit/Stop/session ending stops the owned child tree, never a process found by name.
            if (!ownedByJob && !child.HasExited)
            {
                try { child.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (child.HasExited) { }
            }
            await child.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            // Readers finish on EOF; no unbounded wait if a descendant retained a pipe handle.
            try { await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (TimeoutException) { Console.Error.WriteLine("Windows-MCP output pipes did not close promptly."); }
        }
    }

    private static async Task ForwardAsync(StreamReader reader, TextWriter destination, CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                await destination.WriteLineAsync("[Windows-MCP] " + line).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }
}
