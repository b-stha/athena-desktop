using System.Net;
using Athena.Desktop.Models;

namespace Athena.Desktop.Server.Endpoints;

public sealed class HealthEndpoint : IHttpEndpoint
{
    public string Path => "/health";

    public async Task<Func<CancellationToken, Task>?> HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        if (context.Request.HttpMethod != "GET")
        {
            context.Response.Headers["Allow"] = "GET";
            await HttpJson.ReplyAsync(context.Response, 405,
                new CommandResponse { Message = "Use GET /health." }, cancellationToken);
            return null;
        }

        await HttpJson.ReplyAsync(context.Response, 200, new { Status = "ok" }, cancellationToken);
        return null;
    }
}
