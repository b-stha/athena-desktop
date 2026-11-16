# Desktop checks

Run these from the Desktop repository root:

```powershell
dotnet run --project tests/Athena.Desktop.RegressionTests.csproj
```

The 13 C# checks cover the background app and its Windows-MCP process:

- Preventing duplicate instances and stopping cleanly.
- Handling startup failures, cancellation, and bounded log files.
- Checking settings and waiting for the server to become ready.
- Detecting failures and cleaning up only the processes Desktop started.
- Recovering the tool server after a failure.

They use temporary files and test processes. They do not start your installed
Windows-MCP server, change sign-in settings, control user apps, or run power
commands.

To run just the ten server-management checks:

```powershell
dotnet run --project tests/Athena.Desktop.RegressionTests.csproj -- --mcp-only
```

The Python checks for the app list, launch results, graceful closing, window
choices, and repeated requests are separate. See the
[app tools guide](../mcp/README.md) for their command. Those checks also use
fake app and window data.
