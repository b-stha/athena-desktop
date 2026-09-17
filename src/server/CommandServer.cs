using System.Net;

namespace Athena.Desktop.Server;

public sealed class CommandServer : IDisposable
{
    private readonly HttpListener listener = new();
    private readonly HttpRouter router;

    public CommandServer(HttpRouter router, string prefix = "http://localhost:5000/")
    {
        listener.Prefixes.Add(prefix);
        this.router = router;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        listener.Start();
        using var registration = cancellationToken.Register(listener.Stop);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var context = await listener.GetContextAsync();
                try
                {
                    await router.RouteAsync(context, cancellationToken);
                }
                finally
                {
                    context.Response.Close();
                }
            }
        }
        catch (Exception exception) when (
            cancellationToken.IsCancellationRequested &&
            exception is HttpListenerException or ObjectDisposedException or OperationCanceledException)
        {
            // Stopping the listener interrupts the pending request during shutdown.
        }
        finally
        {
            listener.Stop();
        }
    }

    public void Dispose() => listener.Close();
}
