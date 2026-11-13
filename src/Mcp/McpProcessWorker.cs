using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Athena.Desktop.Mcp;

/// <summary>A launch gate: it cannot start upstream until its parent assigns ownership and sends stdin.</summary>
public static class McpProcessWorker
{
    internal static ProcessStartInfo CreateStartInfo() => new(
        Path.Combine(AppContext.BaseDirectory, "Athena.Desktop.exe"))
    {
        ArgumentList = { "--mcp-worker" },
        WorkingDirectory = AppContext.BaseDirectory,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardInputEncoding = new UTF8Encoding(false),
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8
    };

    internal static string SerializeLaunch(ProcessStartInfo start) => JsonSerializer.Serialize(new LaunchRequest(
        start.FileName, start.ArgumentList.ToArray(), start.WorkingDirectory,
        start.Environment.ToDictionary(pair => pair.Key, pair => pair.Value)));

    // This internal program mode receives no credentials on its command line and opens no network itself.
    public static async Task<int> RunAsync()
    {
        Console.SetIn(new StreamReader(Console.OpenStandardInput(), Encoding.UTF8));
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });
        try
        {
            // EOF before the parent sends a request means no upstream process is created.
            var line = await Console.In.ReadLineAsync().ConfigureAwait(false);
            if (line is null) return 2;
            var request = JsonSerializer.Deserialize<LaunchRequest>(line) ?? throw new JsonException();
            var start = new ProcessStartInfo(request.FileName)
            {
                WorkingDirectory = request.WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var argument in request.Arguments) start.ArgumentList.Add(argument);
            start.Environment.Clear();
            foreach (var pair in request.Environment) start.Environment[pair.Key] = pair.Value;
            using var upstream = Process.Start(start) ?? throw new InvalidOperationException();
            var output = CopyAsync(upstream.StandardOutput, Console.Out);
            var error = CopyAsync(upstream.StandardError, Console.Error);
            await upstream.WaitForExitAsync().ConfigureAwait(false);
            // An orphaned worker can retain inherited pipes. Report launcher exit so the owning job closes.
            try { await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch (TimeoutException) { }
            return upstream.ExitCode;
        }
        catch (Exception exception)
        {
            // Never echo the launch payload, environment, or potentially sensitive command line.
            Console.Error.WriteLine($"Windows-MCP launch helper failed ({exception.GetType().Name}). Check the configured executable and its runtime.");
            return 1;
        }
    }

    private static async Task CopyAsync(StreamReader source, TextWriter destination)
    {
        while (await source.ReadLineAsync().ConfigureAwait(false) is { } line)
            await destination.WriteLineAsync(line).ConfigureAwait(false);
    }

    private sealed record LaunchRequest(string FileName, string[] Arguments, string WorkingDirectory,
        Dictionary<string, string?> Environment);
}
