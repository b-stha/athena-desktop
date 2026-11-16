# Athena Desktop

Athena Desktop is the Windows companion to [Athena](https://github.com/b-stha/athena).
It runs in the system tray and keeps the Windows control tools available to the
Raspberry Pi.

The Pi handles commands. Desktop starts the tool server, keeps logs, recovers
from server failures, and can start when you sign in to Windows.

## What it provides

- Windows tools through Windows-MCP, the server the Pi connects to.
- A list of registered apps, so new apps do not need to be added to Athena's code.
- App launching with a clear report of whether Windows accepted the request.
- Normal window closing, with numbered choices when several windows match.

Closing leaves save prompts for you to answer. Some apps hide in the tray
instead of exiting. The supplied settings allow normal mouse and keyboard use
while Athena works.

## Setup

Install the .NET 10 SDK and the Windows-MCP version listed in the
[runtime settings](config/windows-mcp-runtime.json). Add the
[Athena app tools](mcp/README.md) to that Python runtime.

Use the [JSON](config/mcp.example.json) and [TOML](config/windows-mcp.example.toml)
examples to create private `mcp.json` and `windows-mcp.toml` files in
`%LOCALAPPDATA%\AthenaDesktop\`. Set the executable, network address, and token.
The Pi must use the same address and token.

Check the settings, then install Desktop and its sign-in shortcut:

```powershell
dotnet run --project src/Athena.Desktop.csproj -- --check-config
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\desktop.ps1 -Action Install
```

## Use

Keep Windows signed in so Athena can interact with your apps.
Right-click the tray icon to open logs or exit.

The management script also supports `Start`, `Stop`, `Status`, and `Uninstall`:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\desktop.ps1 -Action Status
```

For app commands and current limitations, see the
[main Athena guide](https://github.com/b-stha/athena).

## Working on Desktop

`src/` contains the tray app, `config/` contains public setup examples,
`scripts/` manages installation, and `mcp/` adds Athena's app tools.
`tests/` contains checks. Local `docs/` and temporary `tmp/` files are ignored.

Run the host checks with:

```powershell
dotnet run --project tests/Athena.Desktop.RegressionTests.csproj
```

See the [test guide](tests/README.md) and [app tools guide](mcp/README.md)
for what the checks cover.
