using System.Net;

namespace Athena.Desktop.Legacy.Server;

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
                Func<CancellationToken, Task>? afterResponse;
                try
                {
                    afterResponse = await router.RouteAsync(context, cancellationToken);
                }
                finally
                {
                    context.Response.Close();
                }

                // Only run deferred actions after writing and closing both succeed.
                if (afterResponse is not null)
                {
                    try
                    {
                        await afterResponse(cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        // The acknowledgement is already sent, so report failures locally.
                        Console.Error.WriteLine($"Post-response action failed: {exception.Message}");
                    }
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
