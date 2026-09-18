using System.Diagnostics;

namespace Athena.Desktop.Actions;

public sealed class ProgramActions
{
    private static readonly Dictionary<string, string> Programs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["notepad"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe")
    };

    public void Open(string programName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(programName);

        if (!Programs.TryGetValue(programName.Trim(), out var executablePath))
        {
            throw new ArgumentException($"Unknown program: '{programName}'.", nameof(programName));
        }

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false
        });
    }
}
