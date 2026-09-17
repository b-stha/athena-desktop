# Athena Desktop

Athena Desktop is the Windows desktop client for Athena, written in C#.

## Architecture

Athena runs on the Raspberry Pi and handles voice transcription and command routing. The desktop client will receive structured commands from Athena and execute supported Windows actions.

- **Athena:** Decides what action to perform and which backend should handle it.
- **Athena Desktop:** Executes supported PC actions and returns their results.
- **Home Assistant:** Handles smart-home actions separately from the desktop client.

The initial focus is deterministic commands. LLM fallback is a later Athena feature and is not required for desktop control.

The HTTP implementation separates listener lifecycle, routing, and endpoint behavior:

- `src/server/CommandServer.cs` accepts HTTP requests, delegates to the router, and closes responses.
- `src/server/HttpRouter.cs` matches URL paths and returns 404 for unknown routes.
- `src/server/endpoints/` contains endpoint classes that define their paths, validate methods, and handle requests.
- `src/Program.cs` registers endpoints with the router and configures the server.

To add an endpoint, implement `IHttpEndpoint` in the endpoints directory and register it in `Program.cs`.

## Current Progress

The desktop process includes an HTTP command-server scaffold and JSON request/response models. Desktop actions are not implemented yet.

Planned capabilities include launching applications and arranging windows. The initial HTTP contract below still needs to be aligned with the Pi command router.

## Development

The client targets .NET 10. Build and run with:

```powershell
dotnet build src/Athena.Desktop.csproj
dotnet run --project src/Athena.Desktop.csproj
```

The server listens on `http://localhost:5000/` by default and accepts `POST /commands`:

```json
{
  "requestId": "example-1",
  "command": "launch_app",
  "parameters": { "name": "notepad" }
}
```

Until a handler is supplied to `CommandsEndpoint`, valid commands return HTTP 501:

```json
{
  "requestId": "example-1",
  "success": false,
  "message": "Command execution is not implemented yet."
}
```

Malformed JSON or a missing command returns HTTP 400. Supply a handler through the `CommandsEndpoint` constructor in `Program.cs` to implement actions; handlers should echo the request ID in their response. Requests are processed sequentially.

Use `GET /health` to check that the HTTP server is responding:

```powershell
Invoke-RestMethod http://localhost:5000/health
```

It returns HTTP 200 with `{"status":"ok"}` without executing a command. Other methods return HTTP 405. To test from the Pi, run `curl http://192.168.68.138:5000/health`, replacing the address with your desktop's configured LAN IP.

For Pi access, set `ATHENA_BIND_HOST` to your desktop's LAN IP before starting the process:

```powershell
$env:ATHENA_BIND_HOST = "192.168.68.138"
dotnet run --project src/Athena.Desktop.csproj
```

This listens on `http://192.168.68.138:5000/`. If `ATHENA_BIND_HOST` is unset or blank, the host defaults to `localhost`. To persist the IP for future terminal sessions, use `[Environment]::SetEnvironmentVariable("ATHENA_BIND_HOST", "192.168.68.138", "User")`, then restart your terminal or IDE.

`ATHENA_HTTP_PREFIX` remains available for a complete URL override, including a custom port, such as `http://192.168.68.138:5001/`. A nonblank `ATHENA_HTTP_PREFIX` takes precedence over `ATHENA_BIND_HOST` and must include a trailing slash.

Windows may require an HTTP URL reservation and an inbound firewall rule for that address and port. The scaffold has no authentication; use it only on a trusted development network. Ctrl-C stops the listener.

## Related Repository

- [Athena](https://github.com/b-stha/athena): Raspberry Pi assistant and command router.
