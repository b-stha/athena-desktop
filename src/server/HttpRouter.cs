using System.Net;
using Athena.Desktop.Models;
using Athena.Desktop.Server.Endpoints;

namespace Athena.Desktop.Server;

public sealed class HttpRouter
{
    private readonly Dictionary<string, IHttpEndpoint> endpoints;

    public HttpRouter(IEnumerable<IHttpEndpoint> endpoints)
    {
        this.endpoints = endpoints.ToDictionary(endpoint => endpoint.Path, StringComparer.Ordinal);
    }

    public async Task<Func<CancellationToken, Task>?> RouteAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var path = context.Request.Url?.AbsolutePath ?? string.Empty;
        if (endpoints.TryGetValue(path, out var endpoint))
        {
            return await endpoint.HandleAsync(context, cancellationToken);
        }

        await HttpJson.ReplyAsync(context.Response, 404,
            new CommandResponse { Message = "Endpoint not found." }, cancellationToken);
        return null;
    }
}
