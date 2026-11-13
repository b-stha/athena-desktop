using System.Net;

namespace Athena.Desktop.Legacy.Server.Endpoints;

public interface IHttpEndpoint
{
    string Path { get; }
    // Return an optional action that the server runs after closing the response.
    Task<Func<CancellationToken, Task>?> HandleAsync(HttpListenerContext context, CancellationToken cancellationToken);
}
