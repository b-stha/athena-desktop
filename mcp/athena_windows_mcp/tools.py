"""Tools share Windows-MCP's existing auth, allowlist, lifecycle and control gate."""

import asyncio
from fastmcp.tools import ToolResult

from .catalog import ApplicationCatalog
from .closing import GracefulClose


def register(mcp, *, catalog=None, closer=None):
    catalog = ApplicationCatalog() if catalog is None else catalog
    closer = GracefulClose(catalog) if closer is None else closer

    @mcp.tool(name="AthenaAppCatalog",
              description="List registered Windows applications and stable IDs. Read-only; "
                          "use query to search display names instead of returning the entire catalog. "
                          "Refresh after installing/removing an app. Use these IDs with AthenaLaunchApp or AthenaCloseApp.",
              annotations={"readOnlyHint": True, "destructiveHint": False,
                           "idempotentHint": True, "openWorldHint": False})
    async def app_catalog(refresh: bool = False, query: str | None = None) -> dict:
        if query is not None and (not query.strip() or len(query) > 160):
            raise ValueError("Use a nonempty application name of at most 160 characters.")
        document = await asyncio.to_thread(catalog.catalog, refresh)
        if query is not None:
            needle = query.strip().casefold()
            document["applications"] = [row for row in document["applications"]
                if any(needle in name.casefold() for name in [row["display_name"], *row["aliases"]])]
        return document

    @mcp.tool(name="AthenaLaunchApp",
              description="Launch one application from AthenaAppCatalog by its exact ID and revision. "
                          "Use a unique request_id for each new command; replaying a retained ID "
                          "returns its receipt without launching again. Acceptance confirms a "
                          "Windows handoff, not window readiness. No paths or command arguments.",
              annotations={"readOnlyHint": False, "destructiveHint": True,
                           "idempotentHint": False, "openWorldHint": False})
    async def launch_app(application_id: str, revision: str, request_id: str) -> ToolResult:
        receipt = await asyncio.to_thread(catalog.launch, application_id, revision, request_id)
        # All MCP clients, including Athena's contextual agent, must stop when
        # Windows cannot determine whether an invocation took effect.
        return ToolResult(structured_content=receipt, is_error=receipt["status"] == "unknown")

    @mcp.tool(name="AthenaAppWindows",
              description="Read visible top-level windows attributed to one registered application. "
                          "Returns short-lived window IDs and titles; does not focus or close anything.",
              annotations={"readOnlyHint": True, "destructiveHint": False,
                           "idempotentHint": True, "openWorldHint": False})
    async def app_windows(application_id: str, revision: str) -> dict:
        return await asyncio.to_thread(closer.windows, application_id, revision)

    @mcp.tool(name="AthenaCloseApp",
              description="Request normal WM_CLOSE for a registered application's window. "
                          "Never kills a process or answers a save dialog. Automatically selects only "
                          "one unambiguous window; otherwise asks for a user selection. A window_id "
                          "must be a user's explicit choice from issued IDs, never a guessed handle. "
                          "Closed means the selected window disappeared, not that background processes exited. "
                          "Pending/uncertain outcomes must not be retried. Reuse a request ID only to replay its receipt.",
              annotations={"readOnlyHint": False, "destructiveHint": True,
                           "idempotentHint": False, "openWorldHint": False})
    async def close_app(application_id: str, revision: str, request_id: str,
                        window_id: str | None = None) -> ToolResult:
        receipt = await asyncio.to_thread(closer.close, application_id, revision, request_id, window_id)
        return ToolResult(structured_content=receipt,
                          is_error=receipt["status"] not in {"closed", "hidden"})
