using System.Net;
using System.Text.Json;

namespace Athena.Desktop.Server;

internal static class HttpJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    internal static Task ReplyAsync<T>(
        HttpListenerResponse response, int statusCode, T body, CancellationToken cancellationToken)
    {
        response.StatusCode = statusCode;
        response.ContentType = "application/json; charset=utf-8";
        return JsonSerializer.SerializeAsync(response.OutputStream, body, Options, cancellationToken);
    }
}
