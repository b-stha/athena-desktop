"""Tracked power requests using the former Desktop handlers' Windows operations."""

import ctypes
from pathlib import Path
import subprocess
import sys
import threading

from .catalog import REQUEST_ID

POWER_ACTIONS = ('shutdown', 'restart', 'sleep')
SE_PRIVILEGE_REMOVED = 0x00000004


class PowerNotSent(RuntimeError):
    pass


class PowerRejected(RuntimeError):
    pass


class WindowsPowerActions:
    def __init__(self):
        self.privilege_lock = threading.Lock()

    @staticmethod
    def _shutdown_path():
        import win32api
        return Path(win32api.GetSystemDirectory()) / 'shutdown.exe'

    @staticmethod
    def _token():
        import win32api
        import win32con
        import win32security
        return win32security.OpenProcessToken(win32api.GetCurrentProcess(),
            win32con.TOKEN_QUERY | win32con.TOKEN_ADJUST_PRIVILEGES)

    def check(self, action):
        if sys.platform != 'win32' or action not in POWER_ACTIONS:
            raise PowerNotSent('Unsupported power request.')
        import win32con
        import win32security
        token = self._token()
        try:
            luid = win32security.LookupPrivilegeValue(None, 'SeShutdownPrivilege')
            privileges = win32security.GetTokenInformation(token, win32security.TokenPrivileges)
            if not any(value == luid and not attributes & SE_PRIVILEGE_REMOVED
                       for value, attributes in privileges):
                raise PowerNotSent('The signed-in account lacks shutdown permission.')
        finally:
            token.Close()
        if action != 'sleep' and not self._shutdown_path().is_file():
            raise PowerNotSent('The Windows power executable is unavailable.')

    def perform(self, action):
        self.check(action)
        if action == 'sleep':
            return self._sleep()
        # Same commands as the retired PowerActions.cs. Positive /t values
        # imply /f, so defer in our worker and keep the Windows timeout zero.
        result = subprocess.run([str(self._shutdown_path()),
            '/s' if action == 'shutdown' else '/r', '/t', '0'],
            capture_output=True, creationflags=subprocess.CREATE_NO_WINDOW, timeout=15)
        if result.returncode:
            raise PowerRejected('Windows rejected the power request.')

    def _sleep(self):
        import win32con
        import win32security
        # SeShutdownPrivilege belongs to this process. Preserve its old state,
        # as the previous WindowsSleepAction.cs did.
        with self.privilege_lock:
            token = self._token()
            previous = None
            try:
                luid = win32security.LookupPrivilegeValue(None, 'SeShutdownPrivilege')
                previous = win32security.AdjustTokenPrivileges(
                    token, False, [(luid, win32con.SE_PRIVILEGE_ENABLED)])
                function = ctypes.WinDLL('powrprof', use_last_error=True).SetSuspendState
                function.argtypes = [ctypes.c_ubyte, ctypes.c_ubyte, ctypes.c_ubyte]
                function.restype = ctypes.c_ubyte
                # Request sleep and keep wake events enabled. No hibernation.
                if not function(False, False, False):
                    raise ctypes.WinError(ctypes.get_last_error())
            finally:
                try:
                    if previous:
                        win32security.AdjustTokenPrivileges(token, False, previous)
                finally:
                    token.Close()


def defer(seconds, callback):
    timer = threading.Timer(seconds, callback)
    timer.daemon = True
    timer.start()


class PowerService:
    def __init__(self, catalog, backend=None, *, schedule=defer, delay=2):
        self.catalog = catalog
        self.backend = WindowsPowerActions() if backend is None else backend
        self.schedule, self.delay = schedule, delay
        self.active = None

    def status(self, request_id=None):
        with self.catalog.lock:
            if request_id is not None:
                self._request_id(request_id)
                previous = self.catalog.ledger.get(request_id)
                if previous is not None and previous[0][0] == 'power':
                    return dict(previous[1])
                return {'schema_version': 1, 'request_id': request_id, 'status': 'not_found'}
            actions = []
            for action in POWER_ACTIONS:
                try:
                    self.backend.check(action)
                except Exception:
                    continue
                actions.append(action)
            return {'schema_version': 1, 'status': 'ready', 'actions': actions,
                    'delay_seconds': self.delay, 'state_confirmed': False}

    @staticmethod
    def _request_id(request_id):
        if not isinstance(request_id, str) or not REQUEST_ID.fullmatch(request_id):
            raise ValueError('Use a short request ID.')

    def execute(self, action, request_id):
        if action not in POWER_ACTIONS:
            raise ValueError('Use shutdown, restart or sleep.')
        self._request_id(request_id)
        with self.catalog.lock:
            fingerprint = ('power', action)
            previous = self.catalog.ledger.get(request_id)
            if previous is not None:
                if previous[0] == fingerprint:
                    return dict(previous[1])
                return self._receipt(action, request_id, 'not_started', 'request_id_conflict')
            receipt = self._receipt(action, request_id, 'unknown', 'interrupted')
            self.catalog.ledger[request_id] = (fingerprint, receipt)
            while len(self.catalog.ledger) > self.catalog.ledger_limit:
                self.catalog.ledger.popitem(last=False)
            if self.active is not None:
                receipt.update(status='not_started', reason='power_request_pending')
                return dict(receipt)
            try:
                self.backend.check(action)
            except Exception:
                receipt.update(status='not_started', reason='power_unavailable')
                return dict(receipt)
            self.active = request_id
            receipt.update(status='scheduled', reason='deferred_power_request')
            try:
                self.schedule(self.delay, lambda: self._run(action, request_id, receipt))
            except Exception:
                # A scheduler exception cannot prove its callback never ran.
                receipt.update(status='unknown', reason='scheduling_outcome_unknown')
            return dict(receipt)

    def _run(self, action, request_id, receipt):
        with self.catalog.lock:
            previous = self.catalog.ledger.get(request_id)
            if (self.active != request_id or previous is None or previous[1] is not receipt
                    or receipt['status'] != 'scheduled'):
                if self.active == request_id:
                    self.active = None
                return
            receipt.update(status='unknown', reason='windows_request_in_progress')
        try:
            self.backend.perform(action)
        except (PowerNotSent, PowerRejected):
            outcome = ('rejected', 'windows_rejected_power')
        except Exception:
            outcome = ('unknown', 'windows_power_outcome_unknown')
        else:
            outcome = ('submitted', 'windows_power_requested')
        with self.catalog.lock:
            receipt.update(status=outcome[0], reason=outcome[1])
            self.active = None

    def _receipt(self, action, request_id, status, reason):
        return {'schema_version': 1, 'operation': 'power', 'action': action,
                'request_id': request_id, 'status': status, 'reason': reason,
                'delay_seconds': self.delay, 'state_confirmed': False}
