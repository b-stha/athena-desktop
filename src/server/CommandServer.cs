using System.Net;
using System.Text.Json;
using Athena.Desktop.Models;

namespace Athena.Desktop.Server;

public sealed class CommandServer : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpListener listener = new();
    private readonly Func<CommandRequest, CancellationToken, Task<CommandResponse>>? handler;

    public CommandServer(
        string prefix = "http://localhost:5000/",
        Func<CommandRequest, CancellationToken, Task<CommandResponse>>? handler = null)
    {
        listener.Prefixes.Add(prefix);
        this.handler = handler;
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
                await HandleAsync(context, cancellationToken);
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

    private async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        CommandRequest? command = null;

        try
        {
            if (context.Request.Url?.AbsolutePath != "/commands")
            {
                await ReplyAsync(404, new() { Message = "Endpoint not found." });
                return;
            }

            if (context.Request.HttpMethod != "POST")
            {
                context.Response.Headers["Allow"] = "POST";
                await ReplyAsync(405, new() { Message = "Use POST /commands." });
                return;
            }

            command = await JsonSerializer.DeserializeAsync<CommandRequest>(
                context.Request.InputStream, JsonOptions, cancellationToken);

            if (command is null || string.IsNullOrWhiteSpace(command.Command))
            {
                await ReplyAsync(400, new() { RequestId = command?.RequestId, Message = "Command is required." });
                return;
            }

            if (handler is null)
            {
                await ReplyAsync(501, new() { RequestId = command.RequestId, Message = "Command execution is not implemented yet." });
                return;
            }

            var response = await handler(command, cancellationToken);
            await ReplyAsync(200, response);
        }
        catch (JsonException)
        {
            await ReplyAsync(400, new() { Message = "Invalid command JSON." });
        }
        finally
        {
            context.Response.Close();
        }

        async Task ReplyAsync(int statusCode, CommandResponse response)
        {
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json; charset=utf-8";
            await JsonSerializer.SerializeAsync(context.Response.OutputStream, response, JsonOptions, cancellationToken);
        }
    }

    public void Dispose() => listener.Close();
}
