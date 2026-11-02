using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Athena.Desktop.Mcp;

namespace Athena.Desktop.RegressionTests;

internal static class McpTests
{
    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("MCP settings resolve paths, reject invalid inputs, and keep argument boundaries", ConfigurationAsync),
        ("MCP supervisor observes listener readiness and stops owned child tree", LifecycleAsync),
        ("MCP supervisor reports early child failure and forwards output", ChildFailureAsync),
        ("MCP supervisor cleans descendants after their launcher exits", OrphanCleanupAsync),
        ("MCP supervisor reports unexpected exit after readiness", UnexpectedExitAsync),
        ("MCP supervisor times out and cleans a child that never listens", StartupTimeoutAsync),
        ("MCP supervisor refuses an already occupied port", OccupiedPortAsync),
        ("MCP supervisor propagates missing executable failures", MissingExecutableAsync)
    ];

    // Only this test executable understands the fixture arguments. No real Windows-MCP is started.
    public static async Task<int?> RunFixtureAsync(string[] args)
    {
        if (args.SequenceEqual(["--mcp-fixture-leaf"]))
        {
            await Task.Delay(Timeout.Infinite);
            return 0;
        }
        if (args.Length < 3 || args[0] != "serve" || args[1] != "--config") return null;
        var fixture = JsonSerializer.Deserialize<Fixture>(File.ReadAllText(args[2]))!;
        Console.WriteLine("fixture stdout marker");
        Console.Error.WriteLine("fixture stderr marker");
        if (fixture.ExitCode is { } code) return code;
        using var leaf = fixture.SpawnLeaf ? Process.Start(FixtureStart("--mcp-fixture-leaf")) : null;
        File.WriteAllText(fixture.StateFile, JsonSerializer.Serialize(new State(Environment.ProcessId, leaf?.Id, args)));
        if (fixture.ExitWithLeaf) return 8;
        if (fixture.Stall) await Task.Delay(Timeout.Infinite);
        var listener = new TcpListener(IPAddress.Loopback, fixture.Port);
        listener.Start();
        if (fixture.ExitAfterReady)
        {
            await Task.Delay(1000);
            return 9;
        }
        try
        {
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync();
            }
        }
        finally { listener.Stop(); }
    }

    private static Task ConfigurationAsync()
    {
        using var directory = new TemporaryDirectory();
        var config = Path.Combine(directory.Path, "mcp config & [test].toml");
        File.WriteAllText(config, "[server]\nauth_key = 'must-not-be-an-argument'\n");
        var settingsPath = Path.Combine(directory.Path, "settings.json");
        var options = new WindowsMcpOptions { Executable = FixtureExecutable, ConfigFile = config, Endpoint = "http://127.0.0.1:8000/mcp" };
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(options with { ConfigFile = Path.GetFileName(config) }));
        var loaded = WindowsMcpOptions.Load(settingsPath);
        Check(loaded.ConfigFile == config, "Relative config resolves against settings file, not working directory.");
        var start = loaded.CreateStartInfo();
        Check(!start.UseShellExecute && start.CreateNoWindow, "Child must run hidden without a shell.");
        Check(start.ArgumentList.SequenceEqual(["serve", "--config", config, "--transport", "streamable-http"]), "Config path must remain one argument with no credentials in arguments.");
        Check(start.Environment["ANONYMIZED_TELEMETRY"] == "false", "Supervisor disables upstream telemetry.");
        foreach (var invalid in new[]
        {
            options with { Endpoint = "http://user:secret@localhost:8000/mcp" },
            options with { Endpoint = "http://localhost:8000/mcp?token=secret" },
            options with { Endpoint = "http://localhost:8000/not-mcp" },
            options with { StartupTimeoutSeconds = 0 },
            options with { ConfigFile = "missing" },
            options with { Executable = "powershell.exe" }
        }) Throws<InvalidOperationException>(invalid.Validate);
        File.WriteAllText(settingsPath, "{\"Executable\": null,\"ConfigFile\":null,\"Endpoint\":null}");
        Throws<InvalidOperationException>(() => WindowsMcpOptions.Load(settingsPath));
        File.WriteAllText(settingsPath, "{\"token\":\"secret\"}");
        Throws<InvalidOperationException>(() => WindowsMcpOptions.Load(settingsPath));
        return Task.CompletedTask;
    }

    private static async Task LifecycleAsync()
    {
        using var directory = new TemporaryDirectory();
        var fixture = new Fixture(FreePort(), Path.Combine(directory.Path, "child.json"), SpawnLeaf: true);
        var options = WriteOptions(directory.Path, fixture);
        using var stop = new CancellationTokenSource();
        var run = new WindowsMcpSupervisor(options).RunAsync(stop.Token);
        var state = await ReadStateAsync(fixture.StateFile);
        try
        {
            await WaitForPortAsync(fixture.Port);
            Check(!run.IsCompleted, "Supervisor remains active while the owned child listens.");
            Check(state.Args.SequenceEqual(["serve", "--config", options.ConfigFile, "--transport", "streamable-http"]), "Fake child receives intact fixed arguments.");
            stop.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(10)));
            await AssertExitedAsync(state.Pid);
            await AssertExitedAsync(state.LeafPid!.Value);
        }
        finally
        {
            stop.Cancel();
            try { await run; } catch { }
            KillFixtureIfAlive(state.Pid);
            if (state.LeafPid is { } leaf) KillFixtureIfAlive(leaf);
        }
    }

    private static async Task ChildFailureAsync()
    {
        using var directory = new TemporaryDirectory();
        var options = WriteOptions(directory.Path, new Fixture(FreePort(), Path.Combine(directory.Path, "state.json"), ExitCode: 7));
        var previousOut = Console.Out;
        var previousError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        Console.SetOut(output);
        Console.SetError(error);
        try
        {
            var failure = await ThrowsAsync<InvalidOperationException>(() => new WindowsMcpSupervisor(options).RunAsync(CancellationToken.None));
            Check(failure.Message.Contains("code 7"), "Child exit code is reported.");
            Check(output.ToString().Contains("fixture stdout marker") && error.ToString().Contains("fixture stderr marker"), "Both output channels reach local logs.");
        }
        finally { Console.SetOut(previousOut); Console.SetError(previousError); }
    }

    private static async Task UnexpectedExitAsync()
    {
        using var directory = new TemporaryDirectory();
        var options = WriteOptions(directory.Path, new Fixture(FreePort(), Path.Combine(directory.Path, "state.json"), ExitAfterReady: true));
        var error = await ThrowsAsync<InvalidOperationException>(() => new WindowsMcpSupervisor(options).RunAsync(CancellationToken.None));
        Check(error.Message.Contains("unexpectedly (code 9)"), "A stopped child does not appear healthy or silently restart.");
    }

    private static async Task OrphanCleanupAsync()
    {
        using var directory = new TemporaryDirectory();
        var fixture = new Fixture(FreePort(), Path.Combine(directory.Path, "state.json"), SpawnLeaf: true, ExitWithLeaf: true);
        var options = WriteOptions(directory.Path, fixture);
        await ThrowsAsync<InvalidOperationException>(() => new WindowsMcpSupervisor(options).RunAsync(CancellationToken.None));
        var state = await ReadStateAsync(fixture.StateFile);
        try { await AssertExitedAsync(state.LeafPid!.Value); }
        finally { KillFixtureIfAlive(state.LeafPid!.Value); }
    }

    private static async Task StartupTimeoutAsync()
    {
        using var directory = new TemporaryDirectory();
        var fixture = new Fixture(FreePort(), Path.Combine(directory.Path, "state.json"), Stall: true);
        var options = WriteOptions(directory.Path, fixture) with { StartupTimeoutSeconds = 1 };
        await ThrowsAsync<TimeoutException>(() => new WindowsMcpSupervisor(options).RunAsync(CancellationToken.None));
        var state = await ReadStateAsync(fixture.StateFile);
        await AssertExitedAsync(state.Pid);
    }

    private static async Task OccupiedPortAsync()
    {
        using var directory = new TemporaryDirectory();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var options = WriteOptions(directory.Path, new Fixture(((IPEndPoint)listener.LocalEndpoint).Port, Path.Combine(directory.Path, "state.json")));
        var failure = await ThrowsAsync<InvalidOperationException>(() => new WindowsMcpSupervisor(options).RunAsync(CancellationToken.None));
        Check(failure.Message.Contains("already in use"), "Don't accidentally manage a separate server.");
        Check(!File.Exists(Path.Combine(directory.Path, "state.json")), "No child starts on an occupied endpoint.");
    }

    private static async Task MissingExecutableAsync()
    {
        var info = new ProcessStartInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        var failure = await ThrowsAsync<InvalidOperationException>(() => new SupervisedProcess(info, TimeSpan.FromSeconds(3), token => Task.Delay(Timeout.Infinite, token)).RunAsync(CancellationToken.None));
        Check(failure.Message.Contains("code 1"), "A missing executable is reported by the owned launch helper.");
    }

    private static WindowsMcpOptions WriteOptions(string directory, Fixture fixture)
    {
        var path = Path.Combine(directory, "fixture config & [literal].toml");
        // The fake executable uses JSON; the real executable reads actual TOML.
        File.WriteAllText(path, JsonSerializer.Serialize(fixture));
        return new WindowsMcpOptions { Executable = FixtureExecutable, ConfigFile = path, Endpoint = $"http://127.0.0.1:{fixture.Port}/mcp", StartupTimeoutSeconds = 5 };
    }
    private static string FixtureExecutable => Path.Combine(AppContext.BaseDirectory, "Athena.Desktop.RegressionTests.exe");
    private static ProcessStartInfo FixtureStart(params string[] args)
    {
        var start = new ProcessStartInfo(FixtureExecutable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return start;
    }
    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
    private static async Task<State> ReadStateAsync(string path)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (File.Exists(path))
            {
                try { return JsonSerializer.Deserialize<State>(File.ReadAllText(path))!; }
                catch (IOException) { }
                catch (JsonException) { }
            }
            await Task.Delay(50);
        }
        throw new Exception("Fake child never wrote its state.");
    }
    private static async Task WaitForPortAsync(int port)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            using var client = new TcpClient();
            try { await client.ConnectAsync(IPAddress.Loopback, port); return; }
            catch (SocketException) { await Task.Delay(50); }
        }
        throw new Exception("Fake child never opened the port.");
    }
    private static async Task AssertExitedAsync(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (ArgumentException) { }
    }
    private static void KillFixtureIfAlive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); if (!process.HasExited) process.Kill(true); }
        catch (ArgumentException) { }
    }
    private static void Throws<T>(Action run) where T : Exception
    {
        try { run(); } catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
    private static async Task<T> ThrowsAsync<T>(Func<Task> run) where T : Exception
    {
        try { await run(); } catch (T failure) { return failure; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed record Fixture(int Port, string StateFile, bool SpawnLeaf = false, int? ExitCode = null, bool Stall = false, bool ExitAfterReady = false, bool ExitWithLeaf = false);
    private sealed record State(int Pid, int? LeafPid, string[] Args);
    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "athena-mcp-tests-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            // Windows can briefly retain a terminated process's working-directory handle.
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    foreach (var file in Directory.GetFiles(Path)) File.Delete(file);
                    Directory.Delete(Path);
                    return;
                }
                catch (IOException) when (attempt < 20) { Thread.Sleep(100); }
            }
        }
    }
}
