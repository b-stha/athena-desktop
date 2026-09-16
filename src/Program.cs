var shutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

Console.CancelKeyPress += (_, args) =>
{
    args.Cancel = true;
    shutdown.TrySetResult();
};

Console.WriteLine("Athena Desktop — press Ctrl-C to exit.");
await shutdown.Task;
