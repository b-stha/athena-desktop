"""Add Athena tools to the pinned Windows-MCP server, retaining its CLI/security."""


def main():
    # This registration seam is tied to config/windows-mcp-runtime.json's pin.
    # No installed upstream source is edited and no second server is started.
    import windows_mcp.tools as upstream_tools
    from windows_mcp.__main__ import main as upstream_main
    from .tools import register

    original = upstream_tools.register_all

    def register_all(mcp, **dependencies):
        original(mcp, **dependencies)
        register(mcp)

    upstream_tools.register_all = register_all
    try:
        upstream_main()
    finally:
        upstream_tools.register_all = original
