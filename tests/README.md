Run the dependency-free regression checks with:

```powershell
dotnet run --project tests/Athena.Desktop.RegressionTests.csproj
```

Every dispatcher injects fake shutdown, restart, and sleep actions. The runner never performs a real power action or launches an application. For each power command, it verifies the correct action mapping, request ID and acknowledgment, two-second delay, cancellation, client receipt before execution, failed response writes, and deferred failure logging while the server continues serving. The HTTP checks bind temporary localhost ports.

Background checks cover instance ownership across threads, duplicate prevention,
graceful stop and restart, runtime cancellation and startup failures, and bounded
file logging. They use unique named handles and temporary log files; they do not
install a login shortcut or change saved user settings.
