using Athena.Desktop.Models;

namespace Athena.Desktop.Dispatch;

public sealed class CommandDispatcher
{
    private readonly Dictionary<string, Func<CommandRequest, CancellationToken, Task<CommandResponse>>> handlers
        = new(StringComparer.OrdinalIgnoreCase)
        {
            // Register command handlers here when their actions are ready.
        };

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
