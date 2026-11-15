"""Tools share Windows-MCP's existing auth, allowlist, lifecycle and control gate."""

import asyncio
from fastmcp.tools import ToolResult

from .catalog import ApplicationCatalog


def register(mcp, *, catalog=None):
    catalog = ApplicationCatalog() if catalog is None else catalog

    @mcp.tool(name="AthenaAppCatalog",
              description="List registered Windows applications and stable IDs. Read-only; "
                          "use query to search display names instead of returning the entire catalog. "
                          "Refresh after installing/removing an app. Use these IDs with AthenaLaunchApp.",
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
