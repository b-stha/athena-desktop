using System.Net;

namespace Athena.Desktop.Server.Endpoints;

public interface IHttpEndpoint
{
    string Path { get; }
    Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken);
}
