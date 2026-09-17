using System.Net;
using Athena.Desktop.Models;

namespace Athena.Desktop.Server.Endpoints;

public sealed class HealthEndpoint : IHttpEndpoint
{
    public string Path => "/health";

    public Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        if (context.Request.HttpMethod != "GET")
        {
            context.Response.Headers["Allow"] = "GET";
            return HttpJson.ReplyAsync(context.Response, 405,
                new CommandResponse { Message = "Use GET /health." }, cancellationToken);
        }

        return HttpJson.ReplyAsync(context.Response, 200, new { Status = "ok" }, cancellationToken);
    }
}
