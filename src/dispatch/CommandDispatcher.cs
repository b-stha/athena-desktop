using System.ComponentModel;
using System.Text.Json;
using Athena.Desktop.Actions;
using Athena.Desktop.Models;

namespace Athena.Desktop.Dispatch;

public sealed class CommandDispatcher
{
    private readonly ProgramActions programActions = new();
    private readonly Action shutdownAction;
    private readonly Dictionary<string, Func<CommandRequest, CancellationToken, Task<CommandDispatchResult>>> handlers;

    public CommandDispatcher(Action? shutdownAction = null)
    {
        this.shutdownAction = shutdownAction ?? new PowerActions().Shutdown;
        handlers = new(StringComparer.OrdinalIgnoreCase)
        {
            ["launch_app"] = LaunchAppAsync,
            ["shutdown"] = ShutdownAsync
        };
    }

    private Task<CommandDispatchResult> ShutdownAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(new CommandDispatchResult
        {
            Response = new CommandResponse
            {
                RequestId = request.RequestId,
                Success = true,
                Message = "Shutdown accepted."
            },
            AfterResponse = async token =>
            {
                // The server closes the acknowledgement before invoking this action.
                // Delay here instead of using shutdown /t > 0, which implies /f.
                await Task.Delay(TimeSpan.FromSeconds(2), token);
                token.ThrowIfCancellationRequested();
                shutdownAction();
            }
        });
    }

    private Task<CommandDispatchResult> LaunchAppAsync(CommandRequest request, CancellationToken cancellationToken)
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

        Task<CommandDispatchResult> ReplyAsync(bool success, string message) => Task.FromResult(new CommandDispatchResult
        {
            Response = new CommandResponse
            {
                RequestId = request.RequestId,
                Success = success,
                Message = message
            }
        });
    }

    public Task<CommandDispatchResult> DispatchAsync(
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

        Task<CommandDispatchResult> FailureAsync(string message) => Task.FromResult(new CommandDispatchResult
        {
            Response = new CommandResponse
            {
                RequestId = request.RequestId,
                Success = false,
                Message = message
            }
        });
    }
}
