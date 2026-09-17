using Athena.Desktop.Server;

using var shutdown = new CancellationTokenSource();

Console.CancelKeyPress += (_, args) =>
{
    args.Cancel = true;
    shutdown.Cancel();
};

Console.WriteLine("Athena Desktop — press Ctrl-C to exit.");
var bindHost = Environment.GetEnvironmentVariable("ATHENA_BIND_HOST");
var prefix = Environment.GetEnvironmentVariable("ATHENA_HTTP_PREFIX");
if (string.IsNullOrWhiteSpace(prefix))
{
    var host = string.IsNullOrWhiteSpace(bindHost) ? "localhost" : bindHost.Trim();
    prefix = new UriBuilder("http", host, 5000).Uri.AbsoluteUri;
}
using var server = new CommandServer(prefix);
Console.WriteLine($"Listening for commands at {prefix}commands");
await server.RunAsync(shutdown.Token);
