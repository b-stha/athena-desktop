using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Athena.Desktop.Dispatch;
using Athena.Desktop.Models;
using Athena.Desktop.Server;
using Athena.Desktop.Server.Endpoints;

// All three power actions are injected fakes in every dispatcher.
var tests = new List<(string Name, Func<Task> Run)>
{
    ("Ordinary dispatch responses retain their behavior", OrdinaryDispatchAsync)
};
foreach (var (command, acknowledgment) in PowerCommandTests.Cases)
{
    tests.Add(($"{command}: dispatch defers execution and honors cancellation", () => DeferredPowerAsync(command, acknowledgment)));
    tests.Add(($"{command}: client receives complete acknowledgment before execution", () => AcknowledgmentBeforePowerAsync(command, acknowledgment)));
    tests.Add(($"{command}: failed response write prevents execution", () => FailedResponseWriteAsync(command)));
    tests.Add(($"{command}: failure is logged and server keeps serving", () => PowerFailureAsync(command, acknowledgment)));
    tests.Add(($"{command}: server cancellation prevents pending execution", () => ServerCancellationAsync(command, acknowledgment)));
}

var failures = 0;
foreach (var (name, run) in tests)
{
    try
    {
        await run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {name}: {exception}");
    }
}

Console.WriteLine($"{tests.Count - failures}/{tests.Count} checks passed.");
return failures == 0 ? 0 : 1;

static async Task DeferredPowerAsync(string command, string acknowledgment)
{
    var executions = new List<string>();
    var dispatcher = PowerCommandTests.CreateDispatcher(executions.Add);
    var requestId = $"dispatch-{command}";
    var result = await dispatcher.DispatchAsync(new CommandRequest
    {
        Command = $" {command.ToUpperInvariant()} ", RequestId = requestId
    });
    Assert(result.Response.Success, $"{command} should be accepted.");
    Assert(result.Response.Message == acknowledgment, "Expected the command's acceptance message.");
    Assert(result.Response.RequestId == requestId, "The request ID should be preserved.");
    Assert(result.AfterResponse is not null, $"{command} should return a deferred action.");
    Assert(executions.Count == 0, "Dispatching must not run any power action.");

    using (var alreadyCanceled = new CancellationTokenSource())
    {
        alreadyCanceled.Cancel();
        await AssertCanceledAsync(() => result.AfterResponse!(alreadyCanceled.Token));
    }
    using (var canceledDuringDelay = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
    {
        await AssertCanceledAsync(() => result.AfterResponse!(canceledDuringDelay.Token));
    }
    Assert(executions.Count == 0, "Cancellation must prevent all injected power actions.");

    var elapsed = Stopwatch.StartNew();
    await result.AfterResponse!(CancellationToken.None);
    Assert(executions.SequenceEqual([command]), "The deferred callback should run exactly the matching power action once.");
    Assert(elapsed.Elapsed >= TimeSpan.FromMilliseconds(1900), "Power actions should retain the two-second delivery grace period.");
}

static async Task OrdinaryDispatchAsync()
{
    var dispatcher = PowerCommandTests.CreateDispatcher(command => throw new Exception($"Unexpected {command} execution."));
    var cases = new (CommandRequest Request, string Message)[]
    {
        (new() { Command = " ", RequestId = "empty" }, "Command is required."),
        (new() { Command = "unsupported", RequestId = "unknown" }, "Unsupported command: 'unsupported'."),
        (new() { Command = "launch_app", RequestId = "missing-name" }, "A nonempty string parameter 'name' is required."),
        (new()
        {
            Command = "launch_app", RequestId = "unknown-app",
            Parameters = new() { ["name"] = JsonSerializer.SerializeToElement("regression-test-unknown-app") }
        }, "Unknown program: 'regression-test-unknown-app'.")
    };
    foreach (var (request, message) in cases)
    {
        var result = await dispatcher.DispatchAsync(request);
        Assert(!result.Response.Success, "Invalid commands should fail.");
        Assert(result.Response.Message == message, $"Unexpected response for {request.RequestId}.");
        Assert(result.Response.RequestId == request.RequestId, "The request ID should be preserved.");
        Assert(result.AfterResponse is null, "Ordinary failures should not schedule an action.");
    }
}

static async Task AcknowledgmentBeforePowerAsync(string command, string acknowledgment)
{
    var acknowledgmentReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var actionObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    await using var server = new TestServer(executed => actionObserved.TrySetResult(
        executed == command && acknowledgmentReceived.Task.IsCompletedSuccessfully));

    var requestId = $"http-{command}";
    var response = await server.Client.PostAsJsonAsync("commands", new { command, requestId });
    using (response)
    {
        Assert(response.StatusCode == HttpStatusCode.OK, $"{command} should receive HTTP 200.");
        var body = await response.Content.ReadFromJsonAsync<CommandResponse>();
        Assert(body is { Success: true } && body.Message == acknowledgment && body.RequestId == requestId,
            "The full acknowledgment should reach the client with its request ID.");
        acknowledgmentReceived.SetResult();
    }
    Assert(await actionObserved.Task.WaitAsync(TimeSpan.FromSeconds(10)),
        "The matching power action must run after the client receives the complete acknowledgment.");
    await AssertHealthyAsync(server.Client);

    using var invalid = await server.Client.PostAsync("commands", new StringContent("{", Encoding.UTF8, "application/json"));
    Assert(invalid.StatusCode == HttpStatusCode.BadRequest, "Malformed command JSON should remain HTTP 400.");
    using var wrongMethod = await server.Client.GetAsync("commands");
    Assert(wrongMethod.StatusCode == HttpStatusCode.MethodNotAllowed, "GET /commands should remain HTTP 405.");
    Assert(wrongMethod.Content.Headers.Allow.Contains("POST"), "The Allow header should advertise POST.");
    using var unknownRoute = await server.Client.GetAsync("missing");
    Assert(unknownRoute.StatusCode == HttpStatusCode.NotFound, "Unknown routes should remain HTTP 404.");
}

static async Task PowerFailureAsync(string command, string acknowledgment)
{
    var failureMarker = $"Injected {command} failure for regression test";
    var actionObserved = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    var previousError = Console.Error;
    using var capturedError = new StringWriter();
    Console.SetError(capturedError);
    try
    {
        await using var server = new TestServer(executed =>
        {
            actionObserved.SetResult(executed);
            throw new InvalidOperationException(failureMarker);
        });
        var requestId = $"failure-{command}";
        using var response = await server.Client.PostAsJsonAsync("commands", new { command, requestId });
        var body = await response.Content.ReadFromJsonAsync<CommandResponse>();
        Assert(response.StatusCode == HttpStatusCode.OK && body is { Success: true } &&
            body.Message == acknowledgment && body.RequestId == requestId,
            "The acknowledgment should already be successful when the power action later fails.");
        Assert(await actionObserved.Task.WaitAsync(TimeSpan.FromSeconds(10)) == command, "The matching power action should run.");
        await AssertHealthyAsync(server.Client);
        Assert(capturedError.ToString().Contains(failureMarker, StringComparison.Ordinal), "Deferred action failures should be logged.");
    }
    finally
    {
        Console.SetError(previousError);
    }
}

static async Task FailedResponseWriteAsync(string command)
{
    var executions = 0;
    var endpoint = new FailedWriteEndpoint(_ => Interlocked.Increment(ref executions));
    await using var server = new TestServer([new HealthEndpoint(), endpoint]);
    using var response = await server.Client.PostAsJsonAsync("commands", new { command, requestId = $"failed-write-{command}" });
    Assert(await response.Content.ReadAsStringAsync() == string.Empty, "The deliberately closed response should have no acknowledgment body.");
    await endpoint.WriteFailed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    await AssertHealthyAsync(server.Client);
    Assert(Volatile.Read(ref executions) == 0, "A failed response write must not schedule any power action.");
}

static async Task ServerCancellationAsync(string command, string acknowledgment)
{
    var executions = 0;
    await using var server = new TestServer(_ => Interlocked.Increment(ref executions));
    var requestId = $"canceled-{command}";
    using var response = await server.Client.PostAsJsonAsync("commands", new { command, requestId });
    var body = await response.Content.ReadFromJsonAsync<CommandResponse>();
    Assert(body is { Success: true } && body.Message == acknowledgment && body.RequestId == requestId,
        "Cancellation check needs a delivered acknowledgment with its request ID.");
    await server.StopAsync();
    Assert(Volatile.Read(ref executions) == 0, "Stopping the server during the grace period should prevent all power actions.");
}

static async Task AssertHealthyAsync(HttpClient client)
{
    using var response = await client.GetAsync("health");
    Assert(response.StatusCode == HttpStatusCode.OK, "The server should continue answering health requests.");
    var body = await response.Content.ReadFromJsonAsync<JsonElement>();
    Assert(body.GetProperty("status").GetString() == "ok", "Health response should retain its JSON body.");
}

static async Task AssertCanceledAsync(Func<Task> action)
{
    try
    {
        await action();
    }
    catch (OperationCanceledException)
    {
        return;
    }
    throw new Exception("Expected cancellation to interrupt the deferred action.");
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

sealed class TestServer : IAsyncDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly CommandServer server;
    private readonly Task running;

    public TestServer(Action<string> powerAction)
        : this([new HealthEndpoint(), new CommandsEndpoint(PowerCommandTests.CreateDispatcher(powerAction).DispatchAsync)])
    {
    }

    public TestServer(IEnumerable<IHttpEndpoint> endpoints)
    {
        using var portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();
        var prefix = $"http://localhost:{port}/";
        server = new CommandServer(new HttpRouter(endpoints), prefix);
        Client = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            BaseAddress = new Uri(prefix), Timeout = TimeSpan.FromSeconds(10)
        };
        running = server.RunAsync(cancellation.Token);
    }

    public HttpClient Client { get; }

    public async Task StopAsync()
    {
        await cancellation.CancelAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(10));
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync();
        }
        finally
        {
            Client.Dispose();
            server.Dispose();
            cancellation.Dispose();
        }
    }
}

sealed class FailedWriteEndpoint(Action<string> powerAction) : IHttpEndpoint
{
    private readonly CommandDispatcher dispatcher = PowerCommandTests.CreateDispatcher(powerAction);
    public string Path => "/commands";
    public TaskCompletionSource WriteFailed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<Func<CancellationToken, Task>?> HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var commands = new CommandsEndpoint(async (request, token) =>
        {
            var result = await dispatcher.DispatchAsync(request, token);
            // Force a deterministic write failure after dispatch has prepared the callback.
            context.Response.Close();
            return result;
        });
        try
        {
            return await commands.HandleAsync(context, cancellationToken);
        }
        catch (ObjectDisposedException)
        {
            WriteFailed.SetResult();
            return null;
        }
    }
}

static class PowerCommandTests
{
    public static readonly (string Command, string Acknowledgment)[] Cases =
    [
        ("shutdown", "Shutdown accepted."),
        ("restart", "Restart accepted."),
        ("sleep", "Sleep accepted.")
    ];

    // Supply every action even when testing one command, so incorrect routing is safe to detect.
    public static CommandDispatcher CreateDispatcher(Action<string> action) => new(
        shutdownAction: () => action("shutdown"),
        restartAction: () => action("restart"),
        sleepAction: () => action("sleep"));
}
