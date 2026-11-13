using System.Net.Sockets;

namespace Athena.Desktop.Mcp;

public sealed class WindowsMcpSupervisor(WindowsMcpOptions options)
{
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
