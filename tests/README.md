Run the dependency-free regression checks with:

```powershell
dotnet run --project tests/Athena.Desktop.RegressionTests.csproj
```

The runner injects fake shutdown actions throughout. It never requests Windows shutdown or launches an application. The HTTP checks bind temporary localhost ports and verify that a client receives the full acknowledgment before the fake action runs, that a failed response write prevents execution, that deferred failures are logged without stopping the server, and that cancellation prevents pending execution.
