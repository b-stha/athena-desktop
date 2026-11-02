using Athena.Desktop.Hosting;

namespace Athena.Desktop.RegressionTests;

internal static class BackgroundTests
{
    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("Background instance rejects duplicates and waits for graceful stop", InstanceAsync),
        ("Background runtime observes cancellation and startup failures", RuntimeAsync),
        ("Background logs retain errors, rotate and restore console writers", LogsAsync)
    ];

    private static Task InstanceAsync()
    {
        var name = "Athena.Desktop.Regression." + Guid.NewGuid().ToString("N");
        using var ready = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var stopReceived = new ManualResetEventSlim();
        Exception? threadFailure = null;
        var owner = new Thread(() =>
        {
            try
            {
                using var instance = DesktopInstance.TryAcquire(name)
                    ?? throw new Exception("First instance should acquire ownership.");
                using var registration = instance.OnStop(stopReceived.Set);
                ready.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new Exception("Owner was not released.");
            }
            catch (Exception exception) { threadFailure = exception; ready.Set(); }
        });
        owner.Start();
        try
        {
            Check(ready.Wait(TimeSpan.FromSeconds(5)), "Owner should initialize.");
            Check(threadFailure is null, $"Owner failed: {threadFailure}");
            Check(DesktopInstance.IsRunning(name), "Another thread must report the instance running.");
            using var contender = DesktopInstance.TryAcquire(name);
            Check(contender is null, "Duplicate instance must not acquire the mutex.");
            var stopping = Task.Run(() => DesktopInstance.Stop(TimeSpan.FromSeconds(5), name));
            Check(stopReceived.Wait(TimeSpan.FromSeconds(5)), "Stop should reach the owning process.");
            Check(!stopping.IsCompleted, "Stop must wait for ownership release.");
            release.Set();
            Check(stopping.GetAwaiter().GetResult(), "Graceful stop should complete after ownership release.");
            Check(!DesktopInstance.IsRunning(name), "Stopped instance should release ownership.");
            Check(DesktopInstance.Stop(TimeSpan.Zero, name), "Stopping an absent instance should succeed.");
            using var restarted = DesktopInstance.TryAcquire(name);
            Check(restarted is not null, "An instance should start again after stopping.");
        }
        finally { release.Set(); owner.Join(); }
        Check(threadFailure is null, $"Owner failed: {threadFailure}");
        return Task.CompletedTask;
    }

    private static async Task RuntimeAsync()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new DesktopRuntime(async token =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        }, "test://cancel");
        using (runtime)
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await runtime.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Check(await runtime.Completion == 0, "Requested cancellation should be a clean exit.");
        }
        runtime.RequestStop();
        var previousError = Console.Error;
        using var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            using var failed = new DesktopRuntime(_ => throw new InvalidOperationException("injected startup failure"), "test://failure");
            Check(await failed.Completion.WaitAsync(TimeSpan.FromSeconds(5)) == 1, "Startup failure should produce a failing exit code.");
            Check(captured.ToString().Contains("injected startup failure"), "Startup failure should remain visible in logs.");
        }
        finally { Console.SetError(previousError); }
    }

    private static Task LogsAsync()
    {
        var directory = Path.Join(Path.GetTempPath(), "athena-background-tests-" + Guid.NewGuid().ToString("N"));
        var path = Path.Join(directory, "athena.log");
        var previousOut = Console.Out;
        var previousError = Console.Error;
        try
        {
            using (var log = new DesktopLog(path, maxBytes: 128))
            {
                for (var index = 0; index < 20; index++) Console.WriteLine("Rotating test line " + index);
                using (var lockedArchive = new FileStream(path + ".3", FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    Console.WriteLine(new string('x', 200));
                    Console.WriteLine("Logging must survive a locked archive.");
                }
                Console.Error.WriteLine("last error marker");
            }
            Check(ReferenceEquals(previousOut, Console.Out) && ReferenceEquals(previousError, Console.Error),
                "Disposing logs should restore the existing console writers.");
            Check(Directory.GetFiles(directory).Length == 4, "Logs should retain one current file and three archives.");
            var retained = string.Join("\n", Directory.GetFiles(directory).Select(File.ReadAllText));
            Check(retained.Contains("[ERROR] last error marker"), "Error output should reach timestamped logs.");
            Check(!retained.Contains("Rotating test line 0\n"), "Old output should expire after rotation.");
        }
        finally
        {
            for (var index = 0; index <= 3; index++) File.Delete(index == 0 ? path : path + "." + index);
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
        return Task.CompletedTask;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
