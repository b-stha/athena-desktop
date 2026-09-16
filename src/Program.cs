using Athena.Desktop.Server;

using var shutdown = new CancellationTokenSource();

Console.CancelKeyPress += (_, args) =>
{
    args.Cancel = true;
    shutdown.Cancel();
};

Console.WriteLine("Athena Desktop — press Ctrl-C to exit.");
var prefix = Environment.GetEnvironmentVariable("ATHENA_HTTP_PREFIX") ?? "http://localhost:5000/";
using var server = new CommandServer(prefix);
Console.WriteLine($"Listening for commands at {prefix}commands");
await server.RunAsync(shutdown.Token);
