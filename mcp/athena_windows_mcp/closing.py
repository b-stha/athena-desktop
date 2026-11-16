"""Window selection, bounded graceful close verification, and shared replay ledger."""

import re
import time
from uuid import uuid4

from .catalog import APPLICATION_ID, REQUEST_ID, REVISION
from .windows import CloseNotSent, WindowsDesktop, belongs

WINDOW_ID = re.compile(r'win_[0-9a-f]{32}')


class GracefulClose:
    def __init__(self, catalog, desktop=None, *, clock=time.monotonic, sleep=time.sleep, wait=2):
        self.catalog = catalog
        self.desktop = WindowsDesktop() if desktop is None else desktop
        self.clock, self.sleep, self.wait = clock, sleep, wait
        self.choices = {}

    @staticmethod
    def _validate(application_id, revision):
        if (not isinstance(application_id, str) or not APPLICATION_ID.fullmatch(application_id)
                or not isinstance(revision, str) or not REVISION.fullmatch(revision)):
            raise ValueError('Use a catalog application ID and revision.')

    def _lookup(self, application_id, revision):
        self.catalog._refresh(force=True)
        if revision != self.catalog.revision:
            return None, 'catalog_changed'
        app = self.catalog.apps.get(application_id)
        if app is None:
            return None, 'unknown_application'
        return app, None

    def _windows(self, app, revision):
        now = self.clock()
        self.choices = {token: row for token, row in self.choices.items() if row[3] > now}
        matches = [window for window in self.desktop.windows()
                   if window.visible and belongs(window, app, self.catalog.apps.values())]
        if len(matches) > 32:
            raise ValueError('Too many matching windows.')
        public = []
        for window in matches:
            token = 'win_' + uuid4().hex
            self.choices[token] = (app.id, revision, window, now + 60)
            public.append({'id': token, 'title': window.title[:240]})
        while len(self.choices) > 256:
            self.choices.pop(next(iter(self.choices)))
        return public

    def windows(self, application_id, revision):
        self._validate(application_id, revision)
        with self.catalog.lock:
            try:
                app, reason = self._lookup(application_id, revision)
                rows = self._windows(app, revision) if app else []
            except Exception:
                reason, rows = 'windows_unavailable', []
            return {'schema_version': 1, 'application_id': application_id, 'revision': revision,
                    'status': 'ready' if reason is None else 'not_started',
                    'reason': reason, 'windows': rows}

    def close(self, application_id, revision, request_id, window_id=None):
        self._validate(application_id, revision)
        if (not isinstance(request_id, str) or not REQUEST_ID.fullmatch(request_id)
                or window_id is not None and (not isinstance(window_id, str) or not WINDOW_ID.fullmatch(window_id))):
            raise ValueError('Use a request ID and an optional issued window ID.')
        with self.catalog.lock:
            fingerprint = ('close', application_id, revision, window_id)
            previous = self.catalog.ledger.get(request_id)
            if previous is not None:
                if previous[0] == fingerprint:
                    return dict(previous[1])
                return {**self._receipt(application_id, revision, request_id, 'request_id_conflict'),
                        'window_id': window_id}
            receipt = self._receipt(application_id, revision, request_id, 'interrupted')
            receipt['window_id'] = window_id
            receipt['status'] = 'unknown'
            self.catalog.ledger[request_id] = (fingerprint, receipt)
            while len(self.catalog.ledger) > self.catalog.ledger_limit:
                self.catalog.ledger.popitem(last=False)
            try:
                app, reason = self._lookup(application_id, revision)
                if reason:
                    receipt.update(status='not_started', reason=reason)
                    return dict(receipt)
                if window_id is None:
                    choices = self._windows(app, revision)
                    if len(choices) != 1:
                        receipt.update(status='clarification' if choices else 'not_started',
                                       reason='multiple_windows' if choices else 'window_not_identified',
                                       windows=choices)
                        return dict(receipt)
                    window_id = choices[0]['id']
                choice = self.choices.get(window_id)
                if (choice is None or choice[0:2] != (application_id, revision)
                        or choice[3] <= self.clock()):
                    receipt.update(status='not_started', reason='selection_expired')
                    return dict(receipt)
                window = choice[2]
                current = self.desktop.current(window)
                if (current is None or current.identity != window.identity
                        or current.title != window.title or not current.visible
                        or not belongs(current, app, self.catalog.apps.values())):
                    receipt.update(status='not_started', reason='window_changed')
                    return dict(receipt)
            except Exception:
                receipt.update(status='not_started', reason='windows_unavailable')
                return dict(receipt)
            receipt.update(window_id=window_id, display_name=app.display_name)
            try:
                self.desktop.post_close(window)
            except CloseNotSent:
                receipt.update(status='not_started', reason='close_not_sent')
                return dict(receipt)
            except Exception:
                receipt.update(status='unknown', reason='close_outcome_unknown')
                return dict(receipt)
            receipt['message_sent'] = True
            deadline = self.clock() + self.wait
            while True:
                try:
                    current = self.desktop.current(window)
                    if current is None or current.identity != window.identity:
                        receipt.update(status='closed', reason='window_destroyed', window_closed=True)
                        break
                    if self.clock() >= deadline:
                        receipt.update(status='pending' if current.visible else 'hidden',
                                       reason='window_still_open' if current.visible else 'window_hidden')
                        break
                except Exception:
                    if self.clock() >= deadline:
                        receipt.update(status='unknown', reason='close_outcome_unknown')
                        break
                # Read-only verification can be repeated while Windows tears
                # down a window. The close message itself is never repeated.
                self.sleep(min(0.1, max(0, deadline - self.clock())))
            return dict(receipt)

    @staticmethod
    def _receipt(application_id, revision, request_id, reason):
        return {'schema_version': 1, 'application_id': application_id, 'revision': revision,
                'request_id': request_id, 'operation': 'close', 'window_id': None,
                'status': 'not_started', 'reason': reason,
                'message_sent': False, 'window_closed': False, 'windows': []}
