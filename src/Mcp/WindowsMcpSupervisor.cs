using System.Net.Sockets;

namespace Athena.Desktop.Mcp;

public sealed class WindowsMcpSupervisor(WindowsMcpOptions options)
{
    public Task RunContinuouslyAsync(CancellationToken cancellationToken) =>
        RunContinuouslyAsync(cancellationToken, TimeSpan.FromSeconds(10));

    // Each attempt owns and cleans up its process tree before the next starts.
    // Recovery never resends a tool call or adopts an unrelated listener.
    public async Task RunContinuouslyAsync(CancellationToken cancellationToken, TimeSpan retryDelay)
    {
        options.Validate();
        if (retryDelay <= TimeSpan.Zero || retryDelay > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(retryDelay));
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { await RunAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Console.Error.WriteLine($"Windows-MCP is unavailable ({exception.GetType().Name}); retrying in {retryDelay.TotalSeconds:g} seconds.");
            }
            await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        options.Validate();
        var endpoint = options.EndpointUri;
        if (await CanConnectAsync(endpoint, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("The configured MCP port is already in use. Stop the separate Windows-MCP instance before Athena Desktop owns it.");
        var process = new SupervisedProcess(options.CreateStartInfo(), TimeSpan.FromSeconds(options.StartupTimeoutSeconds),
            async token =>
            {
                while (!await CanConnectAsync(endpoint, token).ConfigureAwait(false))
                    await Task.Delay(200, token).ConfigureAwait(false);
            });
        await process.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> CanConnectAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            await client.ConnectAsync(endpoint.Host, endpoint.Port, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (SocketException) { return false; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
    }
}
