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

// Every dispatcher has an injected fake: running this suite cannot request Windows shutdown.
var tests = new (string Name, Func<Task> Run)[]
{
    ("Shutdown dispatch defers execution and honors cancellation", DeferredShutdownAsync),
    ("Ordinary dispatch responses retain their behavior", OrdinaryDispatchAsync),
    ("Client receives complete acknowledgment before shutdown", AcknowledgmentBeforeShutdownAsync),
    ("A failed response write does not expose the shutdown callback", FailedResponseWriteAsync),
    ("Shutdown failure is logged and the server keeps serving", ShutdownFailureAsync),
    ("Server cancellation prevents a pending shutdown", ServerCancellationAsync)
};

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

Console.WriteLine($"{tests.Length - failures}/{tests.Length} checks passed.");
return failures == 0 ? 0 : 1;

static async Task DeferredShutdownAsync()
{
    var executions = 0;
    var dispatcher = new CommandDispatcher(() => executions++);
    var result = await dispatcher.DispatchAsync(new CommandRequest
    {
        Command = " SHUTDOWN ", RequestId = "dispatch-shutdown"
    });
    Assert(result.Response.Success, "Shutdown should be accepted.");
    Assert(result.Response.Message == "Shutdown accepted.", "Expected the acceptance message.");
    Assert(result.Response.RequestId == "dispatch-shutdown", "The request ID should be preserved.");
    Assert(result.AfterResponse is not null, "Shutdown should return a deferred action.");
    Assert(executions == 0, "Dispatching must not run shutdown.");

    using (var alreadyCanceled = new CancellationTokenSource())
    {
        alreadyCanceled.Cancel();
        await AssertCanceledAsync(() => result.AfterResponse!(alreadyCanceled.Token));
    }
    using (var canceledDuringDelay = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
    {
        await AssertCanceledAsync(() => result.AfterResponse!(canceledDuringDelay.Token));
    }
    Assert(executions == 0, "Cancellation must prevent the injected shutdown action.");

    var elapsed = Stopwatch.StartNew();
    await result.AfterResponse!(CancellationToken.None);
    Assert(executions == 1, "The deferred callback should run shutdown once.");
    Assert(elapsed.Elapsed >= TimeSpan.FromMilliseconds(1900), "Shutdown should retain the two-second delivery grace period.");
}

static async Task OrdinaryDispatchAsync()
{
    var dispatcher = new CommandDispatcher(() => throw new Exception("Unexpected shutdown execution."));
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

static async Task AcknowledgmentBeforeShutdownAsync()
{
    var acknowledgmentReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var shutdownObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    await using var server = new TestServer(() => shutdownObserved.TrySetResult(acknowledgmentReceived.Task.IsCompletedSuccessfully));

    var response = await server.Client.PostAsJsonAsync("commands", new { command = "shutdown", requestId = "http-shutdown" });
    using (response)
    {
        Assert(response.StatusCode == HttpStatusCode.OK, "Shutdown should receive HTTP 200.");
        var body = await response.Content.ReadFromJsonAsync<CommandResponse>();
        Assert(body is { Success: true, Message: "Shutdown accepted.", RequestId: "http-shutdown" }, "The full acknowledgment should reach the client.");
        acknowledgmentReceived.SetResult();
    }
    Assert(await shutdownObserved.Task.WaitAsync(TimeSpan.FromSeconds(10)), "Shutdown ran before the client received the complete acknowledgment.");
    await AssertHealthyAsync(server.Client);

    using var invalid = await server.Client.PostAsync("commands", new StringContent("{", Encoding.UTF8, "application/json"));
    Assert(invalid.StatusCode == HttpStatusCode.BadRequest, "Malformed command JSON should remain HTTP 400.");
    using var wrongMethod = await server.Client.GetAsync("commands");
    Assert(wrongMethod.StatusCode == HttpStatusCode.MethodNotAllowed, "GET /commands should remain HTTP 405.");
    Assert(wrongMethod.Content.Headers.Allow.Contains("POST"), "The Allow header should advertise POST.");
    using var unknownRoute = await server.Client.GetAsync("missing");
    Assert(unknownRoute.StatusCode == HttpStatusCode.NotFound, "Unknown routes should remain HTTP 404.");
}

static async Task ShutdownFailureAsync()
{
    const string failureMarker = "Injected shutdown failure for regression test";
    var shutdownObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var previousError = Console.Error;
    using var capturedError = new StringWriter();
    Console.SetError(capturedError);
    try
    {
        await using var server = new TestServer(() =>
        {
            shutdownObserved.SetResult();
            throw new InvalidOperationException(failureMarker);
        });
        using var response = await server.Client.PostAsJsonAsync("commands", new { command = "shutdown", requestId = "failure-shutdown" });
        var body = await response.Content.ReadFromJsonAsync<CommandResponse>();
        Assert(response.StatusCode == HttpStatusCode.OK && body is { Success: true, Message: "Shutdown accepted." }, "The acknowledgment should already be successful when shutdown later fails.");
        await shutdownObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await AssertHealthyAsync(server.Client);
        Assert(capturedError.ToString().Contains(failureMarker, StringComparison.Ordinal), "Deferred action failures should be logged.");
    }
    finally
    {
        Console.SetError(previousError);
    }
}

static async Task FailedResponseWriteAsync()
{
    var executions = 0;
    var endpoint = new FailedWriteEndpoint(() => Interlocked.Increment(ref executions));
    await using var server = new TestServer([new HealthEndpoint(), endpoint]);
    using var response = await server.Client.PostAsJsonAsync("commands", new { command = "shutdown", requestId = "failed-write" });
    Assert(await response.Content.ReadAsStringAsync() == string.Empty, "The deliberately closed response should have no acknowledgment body.");
    await endpoint.WriteFailed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    await AssertHealthyAsync(server.Client);
    Assert(Volatile.Read(ref executions) == 0, "A failed response write must not schedule shutdown.");
}

static async Task ServerCancellationAsync()
{
    var executions = 0;
    await using var server = new TestServer(() => Interlocked.Increment(ref executions));
    using var response = await server.Client.PostAsJsonAsync("commands", new { command = "shutdown", requestId = "canceled-shutdown" });
    var body = await response.Content.ReadFromJsonAsync<CommandResponse>();
    Assert(body is { Success: true, Message: "Shutdown accepted." }, "Cancellation check needs a delivered acknowledgment.");
    await server.StopAsync();
    Assert(Volatile.Read(ref executions) == 0, "Stopping the server during the grace period should prevent shutdown.");
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

    public TestServer(Action shutdownAction)
        : this([new HealthEndpoint(), new CommandsEndpoint(new CommandDispatcher(shutdownAction).DispatchAsync)])
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

sealed class FailedWriteEndpoint(Action shutdownAction) : IHttpEndpoint
{
    private readonly CommandDispatcher dispatcher = new(shutdownAction);
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
