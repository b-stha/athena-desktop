using System.Net;
using System.Text.Json;
using Athena.Desktop.Models;

namespace Athena.Desktop.Server.Endpoints;

public sealed class CommandsEndpoint : IHttpEndpoint
{
    private readonly Func<CommandRequest, CancellationToken, Task<CommandResponse>>? handler;

    public CommandsEndpoint(Func<CommandRequest, CancellationToken, Task<CommandResponse>>? handler = null)
    {
        this.handler = handler;
    }

    public string Path => "/commands";

    public async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        if (context.Request.HttpMethod != "POST")
        {
            context.Response.Headers["Allow"] = "POST";
            await ReplyAsync(405, new() { Message = "Use POST /commands." });
            return;
        }

        CommandRequest? command;
        try
        {
            command = await JsonSerializer.DeserializeAsync<CommandRequest>(
                context.Request.InputStream, HttpJson.Options, cancellationToken);
        }
        catch (JsonException)
        {
            await ReplyAsync(400, new() { Message = "Invalid command JSON." });
            return;
        }

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

        await ReplyAsync(200, await handler(command, cancellationToken));

        Task ReplyAsync(int statusCode, CommandResponse response) =>
            HttpJson.ReplyAsync(context.Response, statusCode, response, cancellationToken);
    }
}
