# Athena Desktop

Athena Desktop runs in the signed-in Windows session as the **Windows-MCP
supervisor**. The Pi owns voice processing, deterministic routing, contextual
reasoning, and MCP client connections. This application owns upstream startup,
logs, readiness, and shutdown; Windows-MCP exposes the tools directly to the Pi.

```text
Athena on Pi -> authenticated MCP/HTTP -> Windows-MCP -> Windows controls
                                             ^
                                  Athena Desktop owns process
```

Start with [Windows-MCP setup](docs/WINDOWS_MCP.md). Provision its pinned Python
runtime separately, copy the public examples into private configuration, and
validate settings before starting the application. Configuration examples in
`config/` contain no live credentials; startup never downloads software.

```powershell
dotnet build src/Athena.Desktop.csproj
dotnet run --project src/Athena.Desktop.csproj -- --check-config
```

`--check-config` checks local paths/settings only. It starts no MCP process and
makes no network request. After provisioning and configuration, use the install
or console instructions in the setup guide.

## Repository layout

| Directory | Responsibility |
| --- | --- |
| `src/Mcp/` | Default Windows-MCP configuration and owned process lifecycle |
| `src/Hosting/` | Shared tray, single-instance control, cancellation, and logs |
| `src/Legacy/` | Explicit legacy HTTP backend, action dispatch, contracts, and Windows actions |
| `config/` | Public deployment examples and the pinned upstream runtime profile |
| `scripts/` | Publish/install/start/stop tooling |
| `tests/` | Fake-backend and loopback regression runner |
| `docs/` | Current architecture, deployment, and legacy-mode guides |
| `docs/history/` | Dated rebuild evidence, separate from current instructions |

The [module inventory and history assessment](docs/REPOSITORY_LAYOUT.md) explains
why each source module is retained. Desktop has no training or research source;
voice development tooling belongs in Athena's separate development project.

## Operation and development

- [Windows-MCP provisioning and configuration](docs/WINDOWS_MCP.md)
- [Tray, sign-in startup, logging, and installed process management](docs/BACKGROUND.md)
- [Current desktop architecture](docs/ARCHITECTURE.md)
- [Legacy HTTP API and rollback mode](docs/LEGACY_HTTP.md)
- [Regression test scope](tests/README.md)

```powershell
dotnet run --project tests/Athena.Desktop.RegressionTests.csproj
```

The regression runner uses injected actions and benign child fixtures. It does
not start installed Windows-MCP, launch user applications, change power state,
or update sign-in settings. The older HTTP backend remains reachable through
`ATHENA_DESKTOP_MODE=legacy`; it is isolated under `src/Legacy/` rather than
removed as unused code.

Related repository: [Athena](https://github.com/b-stha/athena).
