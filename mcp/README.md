# Athena's Windows app tools

This small extension adds app discovery, launching, and closing to the existing
Windows-MCP server. It uses the same network address, token, and background
process as the other Windows tools.

## Available tools

| Tool | What it does |
| --- | --- |
| `AthenaAppCatalog` | Lists registered apps and their IDs; can search by name. |
| `AthenaLaunchApp` | Asks Windows to open a selected app. |
| `AthenaAppWindows` | Lists the app's windows without changing them. |
| `AthenaCloseApp` | Requests normal closing and checks what happened. |

Windows supplies the app list. The Pi selects an app by ID, without sending
executable paths or command arguments.

## Install or update

Use the Windows-MCP version in the [runtime settings](../config/windows-mcp-runtime.json).
From the Desktop repository root, stop Desktop before changing its active
extension:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\desktop.ps1 -Action Stop
& "$env:LOCALAPPDATA\AthenaDesktop\windows-mcp-c14240d4\Scripts\python.exe" -m pip install --no-deps .\mcp
```

Set `Executable` in the private `mcp.json` to the runtime's
`Scripts\athena-windows-mcp.exe`, then start Desktop:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\desktop.ps1 -Action Start
```

The existing network and token settings stay the same.

## What the results mean

An accepted launch means Windows received the request. It does not confirm
that a window is ready.

Closing affects one identified window. It never kills a process or answers
a save prompt. Results distinguish a closed window, an app hidden in the tray,
and a window that remains open.

If several windows match, Athena asks you to choose. Reply `close window one`
or another listed number within 50 seconds. Changed or expired choices require
a new close command.

Recent request IDs and results are kept in memory so a replay does not repeat
the action. Restarting the server clears that history.

## Limits

Apps must appear in Windows' registered app list. Portable apps may need a
future override. Closing also needs a reliable match between an app and its
window; some launchers and shared app hosts cannot be matched yet.
Shortcuts that open a document or use extra arguments cannot claim windows
by their executable alone.

Unknown app nicknames may still use cloud reasoning. Automatic nickname
learning is planned on the Pi.

## Checks

Run the Python checks from the Desktop repository root:

```powershell
& "$env:LOCALAPPDATA\AthenaDesktop\windows-mcp-c14240d4\Scripts\python.exe" -m unittest discover -s mcp/tests -v
```

They use fake apps and windows and do not control your open applications.
