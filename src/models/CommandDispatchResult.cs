namespace Athena.Desktop.Models;

public sealed class CommandDispatchResult
{
    public required CommandResponse Response { get; init; }
    public Func<CancellationToken, Task>? AfterResponse { get; init; }
}
