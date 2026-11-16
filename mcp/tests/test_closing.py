from dataclasses import replace
import unittest

from athena_windows_mcp.catalog import ApplicationCatalog, RegisteredApp, WindowsApps
from athena_windows_mcp.closing import GracefulClose
from athena_windows_mcp.windows import CloseNotSent, Window, belongs
from test_catalog import FakeWindows


class Desktop:
    def __init__(self):
        self.rows = [Window(100, 50, 'creation-1', 7, 'Notepad', r'C:\notepad.exe',
                            'notepad.app!App', 'Fixture')]
        self.sent = []
        self.behavior = 'close'
        self.error = None
        self.after_error = False

    def windows(self):
        return list(self.rows)

    def current(self, original):
        if self.after_error and self.sent:
            raise RuntimeError('identity unreadable')
        return next((row for row in self.rows if row.hwnd == original.hwnd), None)

    def post_close(self, window):
        if self.error:
            raise self.error
        self.sent.append(window)
        if self.behavior == 'close':
            self.rows.remove(window)
        elif self.behavior == 'hide':
            self.rows[self.rows.index(window)] = replace(window, visible=False)


class ClosingTests(unittest.TestCase):
    def setUp(self):
        self.registry = FakeWindows()
        self.catalog = ApplicationCatalog(self.registry)
        self.desktop = Desktop()
        self.now = 0
        self.closer = GracefulClose(self.catalog, self.desktop, clock=lambda: self.now,
                                   sleep=lambda seconds: setattr(self, 'now', self.now + seconds), wait=0.2)
        self.snapshot = self.catalog.catalog()
        self.app = next(row for row in self.snapshot['applications'] if row['display_name'] == 'Notepad')

    def close(self, request_id='one', window_id=None):
        return self.closer.close(self.app['id'], self.snapshot['revision'], request_id, window_id)

    def test_window_close_confirmed_and_replay_has_no_second_effect(self):
        result = self.close()
        self.assertEqual(result['status'], 'closed')
        self.assertTrue(result['message_sent'])
        self.assertTrue(result['window_closed'])
        self.assertEqual(self.close(), result)
        self.assertEqual(len(self.desktop.sent), 1)
        self.assertEqual(self.registry.calls, [])

    def test_multiple_windows_clarify_without_any_close_and_explicit_selection_is_exact(self):
        second = replace(self.desktop.rows[0], hwnd=101, title='Second fixture')
        self.desktop.rows.append(second)
        result = self.close()
        self.assertEqual(result['status'], 'clarification')
        self.assertEqual(self.desktop.sent, [])
        selected = result['windows'][1]['id']
        final = self.close('two', selected)
        self.assertEqual(final['status'], 'closed')
        self.assertEqual(self.desktop.sent, [second])
        self.assertEqual(len(self.desktop.rows), 1)

    def test_no_match_is_not_claimed_closed(self):
        self.desktop.rows.clear()
        result = self.close()
        self.assertEqual(result['reason'], 'window_not_identified')
        self.assertFalse(result['message_sent'])
        self.assertFalse(result['window_closed'])

    def test_save_dialog_or_refused_close_remains_pending_without_retry(self):
        self.desktop.behavior = 'keep'
        result = self.close()
        self.assertEqual(result['status'], 'pending')
        self.assertFalse(result['window_closed'])
        self.assertEqual(self.close(), result)
        self.assertEqual(len(self.desktop.sent), 1)

    def test_hidden_window_does_not_claim_process_exit_or_destruction(self):
        self.desktop.behavior = 'hide'
        result = self.close()
        self.assertEqual(result['status'], 'hidden')
        self.assertFalse(result['window_closed'])

    def test_reused_process_identity_or_handle_does_not_receive_close(self):
        choices = self.closer.windows(self.app['id'], self.snapshot['revision'])['windows']
        self.desktop.rows[0] = replace(self.desktop.rows[0], created='different-process')
        result = self.close(window_id=choices[0]['id'])
        self.assertEqual(result['reason'], 'window_changed')
        self.assertFalse(result['message_sent'])
        self.assertEqual(self.desktop.sent, [])

    def test_expired_token_is_not_replaced_by_another_window(self):
        token = self.closer.windows(self.app['id'], self.snapshot['revision'])['windows'][0]['id']
        self.now = 61
        self.assertEqual(self.close(window_id=token)['reason'], 'selection_expired')
        self.assertEqual(self.desktop.sent, [])

    def test_title_change_after_selection_requires_a_fresh_choice(self):
        token = self.closer.windows(self.app['id'], self.snapshot['revision'])['windows'][0]['id']
        self.desktop.rows[0] = replace(self.desktop.rows[0], title='A different document')
        result = self.close(window_id=token)
        self.assertEqual(result['reason'], 'window_changed')
        self.assertEqual(result['window_id'], token)
        self.assertEqual(self.desktop.sent, [])

    def test_stale_catalog_or_foreign_application_token_sends_nothing(self):
        token = self.closer.windows(self.app['id'], self.snapshot['revision'])['windows'][0]['id']
        other = next(row for row in self.snapshot['applications'] if row['id'] != self.app['id'])
        result = self.closer.close(other['id'], self.snapshot['revision'], 'other', token)
        self.assertEqual(result['reason'], 'selection_expired')
        self.registry.rows.pop()
        self.assertEqual(self.close()['reason'], 'catalog_changed')
        self.assertEqual(self.desktop.sent, [])

    def test_access_denial_is_not_sent_and_unknown_post_error_is_never_retried(self):
        for error, status in [(CloseNotSent('denied'), 'not_started'), (RuntimeError('unknown'), 'unknown')]:
            with self.subTest(status=status):
                self.desktop.error = error
                result = self.close(status)
                self.assertEqual(result['status'], status)
                self.assertEqual(self.close(status), result)
                self.assertEqual(self.desktop.sent, [])

    def test_unreadable_window_after_post_is_unknown_not_closed(self):
        self.desktop.behavior = 'keep'
        self.desktop.after_error = True
        result = self.close()
        self.assertEqual(result['status'], 'unknown')
        self.assertTrue(result['message_sent'])
        self.assertFalse(result['window_closed'])
        self.assertEqual(len(self.desktop.sent), 1)

    def test_launch_close_request_id_conflict_is_rejected(self):
        self.catalog.launch(self.app['id'], self.snapshot['revision'], 'same')
        result = self.close('same')
        self.assertEqual(result['reason'], 'request_id_conflict')
        self.assertEqual(self.desktop.sent, [])

    def test_read_only_window_inventory_never_closes_or_focuses(self):
        result = self.closer.windows(self.app['id'], self.snapshot['revision'])
        self.assertEqual(result['status'], 'ready')
        self.assertEqual(len(result['windows']), 1)
        self.assertEqual(self.desktop.sent, [])

    def test_matching_uses_app_id_or_unique_exact_executable_not_title_or_shared_host(self):
        app = RegisteredApp('right.app', 'Notepad', r'C:\notepad.exe')
        other = RegisteredApp('other.app', 'Other', r'C:\notepad.exe')
        window = replace(self.desktop.rows[0], app_id='', title='Wrong app title')
        self.assertTrue(belongs(window, app, [app]))
        self.assertFalse(belongs(window, app, [app, other]))
        self.assertFalse(belongs(replace(window, app_id='other.app'), app, [app]))
        self.assertFalse(belongs(replace(window, image=r'C:\elsewhere\notepad.exe'), app, [app]))

    def test_document_shortcut_does_not_own_its_host_app_windows(self):
        class Item:
            Path, Name = 'notepad.app', 'Notepad'
            def ExtendedProperty(self, key):
                return {'System.Link.TargetParsingPath': r'C:\notepad.exe',
                        'System.Link.Arguments': None}[key]
        class Document(Item):
            Path, Name = 'license.shortcut', 'License document'
            def ExtendedProperty(self, key):
                if key == 'System.Link.Arguments':
                    return r'C:\licenses\license.txt'
                return super().ExtendedProperty(key)
        app, document = WindowsApps._registration(Item()), WindowsApps._registration(Document())
        window = replace(self.desktop.rows[0], app_id='')
        self.assertTrue(belongs(window, app, [app, document]))
        self.assertFalse(belongs(window, document, [app, document]))
        self.assertEqual(document.public()['id'], RegisteredApp('license.shortcut', 'License document').id)

    def test_unreadable_shortcut_arguments_disable_executable_matching(self):
        class Item:
            Path, Name = 'unknown.app', 'Unknown'
            def ExtendedProperty(self, key):
                if key == 'System.Link.Arguments':
                    raise RuntimeError('Unavailable')
                return r'C:\notepad.exe'
        self.assertEqual(WindowsApps._registration(Item()).executable, '')


if __name__ == '__main__':
    unittest.main()
