using System.Text.Json;

namespace Athena.Desktop.Models;

public sealed class CommandRequest
{
    public string? RequestId { get; init; }
    public string Command { get; init; } = string.Empty;
    public Dictionary<string, JsonElement> Parameters { get; init; } = [];
}
