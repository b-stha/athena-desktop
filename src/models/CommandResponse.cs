namespace Athena.Desktop.Models;

public sealed class CommandResponse
{
    public string? RequestId { get; init; }
    public bool Success { get; init; }
    public string? Message { get; init; }
}
