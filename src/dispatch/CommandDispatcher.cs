using System.ComponentModel;
using System.Text.Json;
using Athena.Desktop.Actions;
using Athena.Desktop.Models;

namespace Athena.Desktop.Dispatch;

public sealed class CommandDispatcher
{
    private readonly ProgramActions programActions = new();
    private readonly Dictionary<string, Func<CommandRequest, CancellationToken, Task<CommandResponse>>> handlers;

    public CommandDispatcher()
    {
        handlers = new(StringComparer.OrdinalIgnoreCase)
        {
            ["launch_app"] = LaunchAppAsync
        };
    }

    private Task<CommandResponse> LaunchAppAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Parameters is null ||
            !request.Parameters.TryGetValue("name", out var name) ||
            name.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(name.GetString()))
        {
            return ReplyAsync(false, "A nonempty string parameter 'name' is required.");
        }

        var programName = name.GetString()!.Trim();
        try
        {
            programActions.Open(programName);
            return ReplyAsync(true, $"Launch requested for '{programName}'.");
        }
        catch (ArgumentException)
        {
            return ReplyAsync(false, $"Unknown program: '{programName}'.");
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return ReplyAsync(false, $"Could not launch '{programName}'.");
        }

        Task<CommandResponse> ReplyAsync(bool success, string message) => Task.FromResult(new CommandResponse
        {
            RequestId = request.RequestId,
            Success = success,
            Message = message
        });
    }

    public Task<CommandResponse> DispatchAsync(
        CommandRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.Command))
        {
            return FailureAsync("Command is required.");
        }

        if (handlers.TryGetValue(request.Command.Trim(), out var handler))
        {
            return handler(request, cancellationToken);
        }

        return FailureAsync($"Unsupported command: '{request.Command}'.");

        Task<CommandResponse> FailureAsync(string message) => Task.FromResult(new CommandResponse
        {
            RequestId = request.RequestId,
            Success = false,
            Message = message
        });
    }
}
