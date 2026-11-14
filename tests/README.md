Run the dependency-free regression checks with:

```powershell
dotnet run --project tests/Athena.Desktop.RegressionTests.csproj
```

The runner covers the shared background host and Windows-MCP supervisor: three
background checks and ten MCP checks. It never performs real power actions,
launches user applications, or starts the installed Windows-MCP runtime.

Background checks cover instance ownership across threads, duplicate prevention,
graceful stop and restart, runtime cancellation and startup failures, and bounded
file logging. They use unique named handles and temporary log files; they do not
install a login shortcut or change saved user settings.

MCP supervisor checks run the test executable itself as a benign fake upstream
process, binding temporary loopback ports and spawning an inert descendant. They
verify local configuration validation, literal process argument boundaries,
readiness, timeout, child exit/failure output, occupied-port rejection, and owned
process-tree cleanup, backend recovery, and cancellation during recovery. The
fake's config contents are JSON fixture data, not an upstream TOML example.

For the ten supervisor checks alone:

```powershell
dotnet run --project tests/Athena.Desktop.RegressionTests.csproj -- --mcp-only
```
