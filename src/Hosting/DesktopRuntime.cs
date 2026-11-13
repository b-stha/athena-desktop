namespace Athena.Desktop.Hosting;

public sealed class DesktopRuntime : IDisposable
{
    private readonly CancellationTokenSource shutdown = new();
    private readonly object gate = new();
    private bool disposed;
    public Task<int> Completion { get; }

    public DesktopRuntime(Func<CancellationToken, Task> run, string prefix)
    {
        // Keep listener continuations off the tray's UI thread during shutdown.
        Completion = Task.Run(async () =>
        {
            Console.WriteLine($"Athena Desktop starting at {prefix}");
            try
            {
                await run(shutdown.Token).ConfigureAwait(false);
                return 0;
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { return 0; }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Desktop backend failed: {exception}");
                return 1;
            }
            finally { Console.WriteLine("Athena Desktop stopped."); }
        });
    }

    public void RequestStop()
    {
        lock (gate) { if (!disposed) shutdown.Cancel(); }
    }
    public async Task StopAsync()
    {
        RequestStop();
        await Completion.ConfigureAwait(false);
    }

    public void Dispose()
    {
        RequestStop();
        Completion.GetAwaiter().GetResult();
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            shutdown.Dispose();
        }
    }
}
