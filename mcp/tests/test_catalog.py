import unittest
from concurrent.futures import ThreadPoolExecutor

from athena_windows_mcp.catalog import ApplicationCatalog, LaunchNotSent, RegisteredApp


class FakeWindows:
    def __init__(self):
        self.rows = [RegisteredApp("notepad.app!App", "Notepad"),
                     RegisteredApp("code.exe", "Visual Studio Code")]
        self.calls, self.discoveries, self.error = [], 0, None

    def discover(self):
        self.discoveries += 1
        return list(self.rows)

    def launch(self, identity):
        self.calls.append(identity)
        if self.error:
            raise self.error


class CatalogTests(unittest.TestCase):
    def setUp(self):
        self.windows = FakeWindows()
        self.now = 0
        self.catalog = ApplicationCatalog(self.windows, clock=lambda: self.now)

    def request(self, request_id="one"):
        snapshot = self.catalog.catalog()
        app = next(row for row in snapshot["applications"] if row["display_name"] == "Notepad")
        return app["id"], snapshot["revision"], request_id

    def test_stable_ids_survive_name_and_inventory_order_changes(self):
        original = self.catalog.catalog()
        self.windows.rows.reverse()
        self.assertEqual(original, self.catalog.catalog(refresh=True))
        self.windows.rows[1] = RegisteredApp("notepad.app!App", "Renamed")
        updated = self.catalog.catalog(refresh=True)
        self.assertEqual([r["id"] for r in original["applications"]],
                         [r["id"] for r in updated["applications"]])
        self.assertNotEqual(original["revision"], updated["revision"])

    def test_public_catalog_has_no_launch_identity_or_executable(self):
        row = self.catalog.catalog()["applications"][0]
        self.assertEqual(set(row), {"id", "display_name", "aliases", "operations"})
        self.assertEqual(row["operations"], ["launch", "close"])

    def test_cache_refresh_expiry_and_explicit_refresh(self):
        self.catalog.catalog()
        self.catalog.catalog()
        self.assertEqual(self.windows.discoveries, 1)
        self.now = 61
        self.catalog.catalog()
        self.catalog.catalog(refresh=True)
        self.assertEqual(self.windows.discoveries, 3)

    def test_exact_registered_app_handoff_and_replay(self):
        request = self.request()
        first = self.catalog.launch(*request)
        self.assertEqual(first["status"], "accepted")
        self.assertFalse(first["window_ready"])
        self.assertEqual(self.catalog.launch(*request), first)
        self.assertEqual(self.windows.calls, ["notepad.app!App"])

    def test_concurrent_same_request_invokes_windows_once(self):
        request = self.request()
        with ThreadPoolExecutor(max_workers=4) as pool:
            receipts = list(pool.map(lambda _: self.catalog.launch(*request), range(4)))
        self.assertTrue(all(row == receipts[0] for row in receipts))
        self.assertEqual(len(self.windows.calls), 1)

    def test_new_request_can_launch_same_app_again(self):
        first = self.request()
        self.catalog.launch(*first)
        self.catalog.launch(*self.request("two"))
        self.assertEqual(len(self.windows.calls), 2)

    def test_conflicting_request_id_cannot_launch_another_app(self):
        first = self.request()
        self.catalog.launch(*first)
        other = next(row for row in self.catalog.catalog()["applications"] if row["id"] != first[0])
        result = self.catalog.launch(other["id"], first[1], first[2])
        self.assertEqual(result["reason"], "request_id_conflict")
        self.assertEqual(len(self.windows.calls), 1)

    def test_stale_revision_rejected_before_launch(self):
        request = self.request()
        self.windows.rows.pop()
        result = self.catalog.launch(*request)
        self.assertEqual(result["reason"], "catalog_changed")
        self.assertEqual(result["status"], "not_started")
        self.assertEqual(self.windows.calls, [])

    def test_unknown_id_rejected(self):
        _, revision, request_id = self.request()
        result = self.catalog.launch("app_" + "0" * 32, revision, request_id)
        self.assertEqual(result["reason"], "unknown_application")
        self.assertEqual(self.windows.calls, [])

    def test_raw_path_shell_string_and_malformed_ids_cannot_launch(self):
        _, revision, request_id = self.request()
        for value in [r"C:\Windows\notepad.exe", "notepad; shutdown /s", "", "app_" + "x" * 32]:
            with self.subTest(value=value), self.assertRaises(ValueError):
                self.catalog.launch(value, revision, request_id)
        self.assertEqual(self.windows.calls, [])

    def test_resolution_failure_is_not_started_and_is_retained(self):
        self.windows.error = LaunchNotSent("private path")
        request = self.request()
        receipt = self.catalog.launch(*request)
        self.assertEqual(receipt["status"], "not_started")
        self.assertNotIn("private path", str(receipt))
        self.assertEqual(self.catalog.launch(*request), receipt)
        self.assertEqual(len(self.windows.calls), 1)

    def test_uncertain_launch_is_not_retried(self):
        self.windows.error = RuntimeError("private path")
        request = self.request()
        receipt = self.catalog.launch(*request)
        self.assertEqual(receipt["status"], "unknown")
        self.assertEqual(self.catalog.launch(*request), receipt)
        self.assertEqual(len(self.windows.calls), 1)

    def test_discovery_failure_never_launches(self):
        request = self.request()
        self.windows.discover = lambda: (_ for _ in ()).throw(RuntimeError("private path"))
        self.assertEqual(self.catalog.launch(*request)["status"], "not_started")
        self.assertEqual(self.windows.calls, [])

    def test_conflicting_identity_or_oversized_catalog_fails_closed(self):
        self.windows.rows.append(RegisteredApp("notepad.app!App", "Conflicting"))
        with self.assertRaises(LaunchNotSent):
            self.catalog.catalog()
        self.windows.rows = [RegisteredApp(str(i), str(i)) for i in range(2001)]
        with self.assertRaises(LaunchNotSent):
            self.catalog.catalog()

    def test_identical_names_keep_distinct_application_ids(self):
        self.windows.rows.append(RegisteredApp("second.notepad", "Notepad"))
        matches = [r for r in self.catalog.catalog()["applications"] if r["display_name"] == "Notepad"]
        self.assertEqual(len(matches), 2)
        self.assertNotEqual(matches[0]["id"], matches[1]["id"])


if __name__ == "__main__":
    unittest.main()
