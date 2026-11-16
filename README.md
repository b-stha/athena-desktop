# Athena Desktop

Athena's Windows companion runs in the system tray and manages Windows-MCP.
The Raspberry Pi interprets commands and connects to MCP; Desktop handles
startup, logs, shutdown, and automatic recovery of the Windows-MCP process.
Windows also supplies the Pi with a catalog of registered applications, so
opening another app does not require adding it to Athena's source code.

## Setup

Requires the .NET 10 SDK and a separately installed Windows-MCP runtime. The
[runtime profile](config/windows-mcp-runtime.json) records the version used by Athena.
Install the [Athena MCP extension](mcp/README.md) into that runtime, then use
its `athena-windows-mcp.exe` executable in the Desktop settings.

Use the [JSON](config/mcp.example.json) and [TOML](config/windows-mcp.example.toml)
examples to create private `mcp.json` and `windows-mcp.toml` files in
`%LOCALAPPDATA%\AthenaDesktop\`. Set the executable path, network address, and
token for your PC. The Pi must use the same MCP address and token.

Validate the settings, then install the tray app and sign-in shortcut:

```powershell
dotnet run --project src/Athena.Desktop.csproj -- --check-config
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\desktop.ps1 -Action Install
```

## Use

Keep Windows signed in for desktop interaction. Right-click the tray icon to
open logs or exit. The management script also supports `Start`, `Stop`, `Status`,
and `Uninstall` through its `-Action` option.

## Development

`src/` contains the host, `config/` holds public examples, `scripts/` manages
installation, `mcp/` adds application catalog/launch tools, and `tests/` checks
background/MCP behavior. Local `docs/` and temporary files under `tmp/` are ignored.

```powershell
dotnet run --project tests/Athena.Desktop.RegressionTests.csproj
```

See [test coverage](tests/README.md) and the [MCP extension checks](mcp/README.md).

Core application: [Athena](https://github.com/b-stha/athena).
