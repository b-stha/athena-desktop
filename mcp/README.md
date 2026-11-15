# Athena Windows-MCP extension

Adds two tools to the existing Windows-MCP server:

- `AthenaAppCatalog`: discovers Start-menu applications and returns stable IDs.
  A name query keeps model responses small; the Pi caches the full catalog.
- `AthenaLaunchApp`: resolves an ID on Windows and returns a launch receipt.
  Replaying a retained request ID returns the receipt without launching again.

The extension uses the same endpoint, authentication, allowlist and process
supervision as upstream. The C# tray host stays responsible for lifecycle.
Windows owns launch identities; the Pi sends no executable paths or arguments.

## Install

Use the upstream revision in [the runtime profile](../config/windows-mcp-runtime.json).
The bootstrap registration hook and FastMCP version are tested against that profile.
Install into the existing Python 3.14 Windows-MCP environment:

```powershell
& "$env:LOCALAPPDATA\AthenaDesktop\windows-mcp-c14240d4\Scripts\python.exe" -m pip install --no-deps .\mcp
```

Set `Executable` in Desktop's private `mcp.json` to the resulting
`Scripts\athena-windows-mcp.exe`, then restart Desktop. The CLI arguments and
private TOML settings stay the same. No upstream package files are patched.
Stop Desktop before updating the extension; Windows locks its active launcher.

## Limits and checks

Only applications registered in Windows' AppsFolder appear automatically.
Portable apps without a registration need a future override. Duplicate spoken
names require clarification. Launch acceptance does not confirm a ready window;
Windows may reuse an existing instance or hand the request to a launcher.
Closing applications is separate work.

Both catalog caches expire after 60 seconds. Launch checks the current catalog
revision and declines a stale selection. Receipts are retained in memory for
256 requests; this is not a guarantee across server restarts.

Tests use fake app discovery and launching:

```powershell
& "$env:LOCALAPPDATA\AthenaDesktop\windows-mcp-c14240d4\Scripts\python.exe" -m unittest discover -s mcp/tests -v
```

Discovery follows Microsoft's [AppsFolder/AUMID example](https://learn.microsoft.com/en-us/windows/configuration/store/find-aumid).
