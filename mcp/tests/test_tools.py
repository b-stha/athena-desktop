import unittest

from fastmcp import Client, FastMCP

from athena_windows_mcp.catalog import ApplicationCatalog
from athena_windows_mcp.tools import register
from test_catalog import FakeWindows
from test_closing import Desktop
from athena_windows_mcp.closing import GracefulClose


class ToolTests(unittest.IsolatedAsyncioTestCase):
    async def test_pending_close_is_a_protocol_error_and_not_retried(self):
        windows, desktop = FakeWindows(), Desktop()
        desktop.behavior = 'keep'
        catalog = ApplicationCatalog(windows)
        server = FastMCP('close-tool-test')
        register(server, catalog=catalog, closer=GracefulClose(catalog, desktop, wait=0))
        async with Client(server) as client:
            document = (await client.call_tool('AthenaAppCatalog', {})).data
            app = next(row for row in document['applications'] if row['display_name'] == 'Notepad')
            request = {'application_id': app['id'], 'revision': document['revision'], 'request_id': 'pending'}
            first = await client.call_tool_mcp('AthenaCloseApp', request)
            replay = await client.call_tool_mcp('AthenaCloseApp', request)
            self.assertTrue(first.is_error)
            self.assertEqual(first.structured_content['status'], 'pending')
            self.assertEqual(first.structured_content, replay.structured_content)
            self.assertEqual(len(desktop.sent), 1)

    async def test_catalog_and_launch_schemas_and_receipts(self):
        windows = FakeWindows()
        server = FastMCP("catalog-test")
        register(server, catalog=ApplicationCatalog(windows))
        async with Client(server) as client:
            tools = {tool.name: tool for tool in await client.list_tools()}
            self.assertEqual(set(tools), {"AthenaAppCatalog", "AthenaLaunchApp",
                                         "AthenaAppWindows", "AthenaCloseApp",
                                         "AthenaPowerStatus", "AthenaPowerAction"})
            self.assertTrue(tools["AthenaAppCatalog"].annotations.read_only_hint)
            self.assertEqual(set(tools["AthenaLaunchApp"].input_schema["required"]),
                             {"application_id", "revision", "request_id"})
            result = await client.call_tool("AthenaAppCatalog", {})
            document = result.data
            self.assertIsInstance(document, dict)
            self.assertEqual(windows.calls, [])
            request = {"application_id": document["applications"][0]["id"],
                       "revision": document["revision"], "request_id": "tool-test:1"}
            first = await client.call_tool("AthenaLaunchApp", request)
            replay = await client.call_tool("AthenaLaunchApp", request)
            self.assertEqual(first.data, replay.data)
            self.assertEqual(first.data["status"], "accepted")
            self.assertEqual(len(windows.calls), 1)

    async def test_tool_rejects_path_before_any_windows_effect(self):
        windows = FakeWindows()
        server = FastMCP("catalog-test")
        register(server, catalog=ApplicationCatalog(windows))
        async with Client(server) as client:
            with self.assertRaises(Exception):
                await client.call_tool("AthenaLaunchApp",
                    {"application_id": r"C:\Windows\notepad.exe",
                     "revision": "0" * 64, "request_id": "path"})
            self.assertEqual(windows.calls, [])

    async def test_query_limits_model_results_but_keeps_the_full_catalog_revision(self):
        windows = FakeWindows()
        server = FastMCP("catalog-query-test")
        register(server, catalog=ApplicationCatalog(windows))
        async with Client(server) as client:
            full = (await client.call_tool("AthenaAppCatalog", {})).data
            filtered = (await client.call_tool("AthenaAppCatalog", {"query": "note"})).data
            self.assertEqual(filtered["revision"], full["revision"])
            self.assertEqual([row["display_name"] for row in filtered["applications"]], ["Notepad"])
            self.assertEqual(windows.calls, [])

    async def test_unknown_receipt_is_a_protocol_error_and_replay_never_invokes_again(self):
        windows = FakeWindows()
        windows.error = RuntimeError("uncertain Windows handoff")
        server = FastMCP("unknown-launch-test")
        register(server, catalog=ApplicationCatalog(windows))
        async with Client(server) as client:
            document = (await client.call_tool("AthenaAppCatalog", {})).data
            request = {"application_id": document["applications"][0]["id"],
                       "revision": document["revision"], "request_id": "uncertain:1"}
            first = await client.call_tool_mcp("AthenaLaunchApp", request)
            replay = await client.call_tool_mcp("AthenaLaunchApp", request)
            self.assertTrue(first.is_error)
            self.assertEqual(first.structured_content["status"], "unknown")
            self.assertEqual(replay.structured_content, first.structured_content)
            self.assertEqual(len(windows.calls), 1)

    async def test_wrapper_extends_upstream_registration_without_starting_second_server(self):
        import windows_mcp.tools
        import windows_mcp.__main__
        from unittest.mock import patch
        from athena_windows_mcp import main

        calls = []
        server = FastMCP("wrapper-test")
        def upstream_main():
            windows_mcp.tools.register_all(server, get_desktop=None, get_analytics=None)
        original = windows_mcp.tools.register_all
        try:
            with patch.object(windows_mcp.tools, "register_all",
                              side_effect=lambda *args, **kwargs: calls.append("upstream")), \
                 patch.object(windows_mcp.__main__, "main", side_effect=upstream_main):
                main()
                async with Client(server) as client:
                    tools = {tool.name for tool in await client.list_tools()}
                self.assertEqual(calls, ["upstream"])
                self.assertIn("AthenaAppCatalog", tools)
                self.assertIn("AthenaLaunchApp", tools)
        finally:
            windows_mcp.tools.register_all = original


if __name__ == "__main__":
    unittest.main()
