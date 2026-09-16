# Athena Desktop

Athena Desktop is the Windows desktop client for Athena, written in C#.

## Architecture

Athena runs on the Raspberry Pi and handles voice transcription and command routing. The desktop client will receive structured commands from Athena and execute supported Windows actions.

- **Athena:** Decides what action to perform and which backend should handle it.
- **Athena Desktop:** Executes supported PC actions and returns their results.
- **Home Assistant:** Handles smart-home actions separately from the desktop client.

The initial focus is deterministic commands. LLM fallback is a later Athena feature and is not required for desktop control.

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

Until a handler is supplied to `CommandServer`, valid commands return HTTP 501:

```json
{
  "requestId": "example-1",
  "success": false,
  "message": "Command execution is not implemented yet."
}
```

Malformed JSON or a missing command returns HTTP 400. Supply a handler through the `CommandServer` constructor to implement actions; handlers should echo the request ID in their response. Requests are processed sequentially.

For Pi access, set `ATHENA_HTTP_PREFIX` to a desktop LAN address with a trailing slash, such as `http://192.168.1.100:5000/`. Windows may require an HTTP URL reservation and an inbound firewall rule for that address and port. The scaffold has no authentication; use it only on a trusted development network. Ctrl-C stops the listener.

## Related Repository

- [Athena](https://github.com/b-stha/athena): Raspberry Pi assistant and command router.
