import unittest
from unittest.mock import patch

from fastmcp import Client, FastMCP

from athena_windows_mcp.catalog import ApplicationCatalog, RegisteredApp
from athena_windows_mcp.power import PowerNotSent, PowerRejected, PowerService, WindowsPowerActions
from athena_windows_mcp.tools import register


class Backend:
    def __init__(self):
        self.calls = []
        self.allowed = True
        self.failure = None

    def check(self, action):
        if not self.allowed:
            raise PowerNotSent()

    def perform(self, action):
        self.calls.append(action)
        if self.failure:
            raise self.failure


class PowerTests(unittest.TestCase):
    def setUp(self):
        self.catalog = ApplicationCatalog(backend=None)
        self.backend = Backend()
        self.pending = []
        self.power = PowerService(self.catalog, self.backend,
            schedule=lambda delay, callback: self.pending.append((delay, callback)))

    def test_read_only_status_does_not_schedule_or_execute(self):
        self.assertEqual(self.power.status()['actions'], ['shutdown', 'restart', 'sleep'])
        self.assertEqual(self.pending, [])
        self.assertEqual(self.backend.calls, [])

    def test_receipt_precedes_effect_and_replays_do_not_schedule_again(self):
        first = self.power.execute('shutdown', 'same')
        self.assertEqual(first['status'], 'scheduled')
        self.assertFalse(first['state_confirmed'])
        self.assertEqual(self.backend.calls, [])
        self.assertEqual(self.power.execute('shutdown', 'same'), first)
        self.assertEqual(len(self.pending), 1)
        self.assertEqual(self.pending[0][0], 2)
        self.pending[0][1]()
        self.pending[0][1]()
        self.assertEqual(self.backend.calls, ['shutdown'])
        self.assertEqual(self.power.status('same')['status'], 'submitted')
        self.assertEqual(self.power.execute('shutdown', 'same')['status'], 'submitted')
        self.assertEqual(len(self.pending), 1)

    def test_request_id_conflict_and_concurrent_power_request_send_nothing_more(self):
        self.power.execute('shutdown', 'one')
        self.assertEqual(self.power.execute('restart', 'one')['reason'], 'request_id_conflict')
        self.assertEqual(self.power.execute('sleep', 'two')['reason'], 'power_request_pending')
        self.assertEqual(len(self.pending), 1)
        self.assertEqual(self.backend.calls, [])

    def test_shared_app_request_ids_cannot_be_repurposed_as_power(self):
        self.catalog.ledger['launch'] = (('launch', 'app', 'revision'), {})
        self.assertEqual(self.power.execute('sleep', 'launch')['reason'], 'request_id_conflict')
        self.assertEqual(self.pending, [])

    def test_rejected_and_unknown_windows_outcomes_never_repeat(self):
        for exception, status in [(PowerRejected(), 'rejected'), (RuntimeError(), 'unknown')]:
            with self.subTest(status=status):
                self.backend.failure = exception
                request = status
                self.power.execute('restart', request)
                self.pending[-1][1]()
                self.assertEqual(self.power.status(request)['status'], status)
                count = len(self.backend.calls)
                self.power.execute('restart', request)
                self.assertEqual(len(self.backend.calls), count)

    def test_failed_preflight_does_not_schedule(self):
        self.backend.allowed = False
        self.assertEqual(self.power.execute('sleep', 'blocked')['status'], 'not_started')
        self.assertEqual(self.power.status()['actions'], [])
        self.assertEqual(self.pending, [])

    def test_bad_operation_and_request_id_are_rejected(self):
        for action, request in [('hibernate', 'one'), ('shutdown /f', 'one'), ('sleep', '../path')]:
            with self.assertRaises(ValueError):
                self.power.execute(action, request)
        self.assertEqual(self.pending, [])

    def test_uncertain_scheduler_is_not_retried(self):
        def broken(*args):
            raise RuntimeError()
        self.power.schedule = broken
        result = self.power.execute('sleep', 'uncertain')
        self.assertEqual(result['status'], 'unknown')
        self.assertEqual(self.power.execute('sleep', 'uncertain'), result)
        self.assertEqual(self.backend.calls, [])

    def test_shutdown_commands_preserve_previous_zero_timeout_and_no_force(self):
        native = WindowsPowerActions()
        with patch.object(native, 'check'), patch.object(native, '_shutdown_path',
                return_value='C:\\Windows\\System32\\shutdown.exe'), \
                patch('athena_windows_mcp.power.subprocess.run') as run:
            run.return_value.returncode = 0
            for action, switch in [('shutdown', '/s'), ('restart', '/r')]:
                native.perform(action)
                arguments = run.call_args.args[0]
                self.assertEqual(arguments, ['C:\\Windows\\System32\\shutdown.exe', switch, '/t', '0'])
                self.assertNotIn('/f', arguments)
                self.assertTrue(run.call_args.kwargs['creationflags'])

    def test_sleep_dispatch_uses_native_sleep_handler(self):
        native = WindowsPowerActions()
        with patch.object(native, 'check'), patch.object(native, '_sleep') as sleep:
            native.perform('sleep')
            sleep.assert_called_once_with()


    def test_sleep_preserves_wake_events_and_restores_previous_privileges(self):
        import win32security
        from unittest.mock import Mock
        native = WindowsPowerActions()
        token = Mock()
        previous = [(123, 0)]
        suspend = Mock(return_value=1)
        library = Mock(SetSuspendState=suspend)
        with patch.object(native, '_token', return_value=token), \
                patch.object(win32security, 'LookupPrivilegeValue', return_value=123), \
                patch.object(win32security, 'AdjustTokenPrivileges', side_effect=[previous, []]) as adjust, \
                patch('athena_windows_mcp.power.ctypes.WinDLL', return_value=library):
            native._sleep()
        suspend.assert_called_once_with(False, False, False)
        self.assertEqual(adjust.call_args_list[1].args, (token, False, previous))
        token.Close.assert_called_once_with()


class PowerToolTests(unittest.IsolatedAsyncioTestCase):
    async def test_sdk_receipts_and_status_use_the_same_power_service(self):
        catalog, backend, pending = ApplicationCatalog(), Backend(), []
        power = PowerService(catalog, backend, schedule=lambda delay, callback: pending.append(callback))
        server = FastMCP('power-test')
        register(server, catalog=catalog, power=power)
        async with Client(server) as client:
            status = (await client.call_tool('AthenaPowerStatus', {})).data
            self.assertIn('shutdown', status['actions'])
            args = {'action': 'shutdown', 'request_id': 'sdk-test'}
            first = await client.call_tool_mcp('AthenaPowerAction', args)
            self.assertFalse(first.is_error)
            self.assertEqual(first.structured_content['status'], 'scheduled')
            self.assertEqual(backend.calls, [])
            replay = await client.call_tool_mcp('AthenaPowerAction', args)
            self.assertEqual(replay.structured_content, first.structured_content)
            self.assertEqual(len(pending), 1)
            pending[0]()
            receipt = (await client.call_tool('AthenaPowerStatus', {'request_id': 'sdk-test'})).data
            self.assertEqual(receipt['status'], 'submitted')
            self.assertEqual(backend.calls, ['shutdown'])
            with self.assertRaises(Exception):
                await client.call_tool('AthenaPowerAction', {'action': 'hibernate', 'request_id': 'invalid'})
            self.assertEqual(backend.calls, ['shutdown'])


if __name__ == '__main__':
    unittest.main()
