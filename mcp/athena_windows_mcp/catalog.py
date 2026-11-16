"""Discover registered apps and retain launch/close receipts without command strings."""

from collections import OrderedDict
from dataclasses import dataclass
import hashlib
import json
import re
import threading
import time


MAX_APPLICATIONS = 2000
APPLICATION_ID = re.compile(r"app_[0-9a-f]{32}")
REVISION = re.compile(r"[0-9a-f]{64}")
REQUEST_ID = re.compile(r"[A-Za-z0-9_:-]{1,128}")


class LaunchNotSent(RuntimeError):
    """Discovery/resolution failed before a Windows launch invocation."""


@dataclass(frozen=True)
class RegisteredApp:
    identity: str
    display_name: str
    executable: str = ''

    @property
    def id(self):
        return "app_" + hashlib.sha256(self.identity.casefold().encode("utf-8")).hexdigest()[:32]

    def public(self):
        return {"id": self.id, "display_name": self.display_name,
                "aliases": [], "operations": ["launch", "close"]}


class WindowsApps:
    """COM objects stay on the initialized thread; only strings leave it."""

    @staticmethod
    def _folder():
        import win32com.client
        shell = win32com.client.Dispatch("Shell.Application")
        folder = shell.NameSpace("shell:AppsFolder")
        if folder is None:
            raise LaunchNotSent("Windows application registration is unavailable.")
        return folder

    def discover(self):
        import pythoncom
        pythoncom.CoInitialize()
        try:
            folder = self._folder()
            items = folder.Items()
            if items.Count > MAX_APPLICATIONS:
                raise LaunchNotSent("Windows application catalog exceeds its supported limit.")
            rows = []
            for item in items:
                try:
                    executable = item.ExtendedProperty("System.Link.TargetParsingPath")
                except Exception:
                    executable = ''
                rows.append(RegisteredApp(str(item.Path), str(item.Name),
                                          executable if isinstance(executable, str) else ''))
            return rows
        finally:
            # Release COM references before uninitializing this worker thread.
            folder = items = None
            pythoncom.CoUninitialize()

    def launch(self, identity):
        import pythoncom
        pythoncom.CoInitialize()
        try:
            try:
                folder = self._folder()
                item = folder.ParseName(identity)
                if item is None or str(item.Path).casefold() != identity.casefold():
                    raise LaunchNotSent("The registered application is no longer available.")
            except Exception:
                raise LaunchNotSent("Windows could not resolve the registered application.") from None
            # Invoke only the exact OS-owned item's default verb. No user-supplied
            # path, executable arguments, shell command, or elevation verb exists.
            item.InvokeVerb()
        finally:
            item = folder = None
            pythoncom.CoUninitialize()


class ApplicationCatalog:
    def __init__(self, backend=None, *, clock=time.monotonic, ttl=60, ledger_limit=256):
        self.backend = WindowsApps() if backend is None else backend
        self.clock, self.ttl, self.ledger_limit = clock, ttl, ledger_limit
        self.lock = threading.RLock()
        self.apps, self.revision, self.refreshed = {}, "", float("-inf")
        self.ledger = OrderedDict()

    def _refresh(self, force=False):
        if not force and self.revision and self.clock() - self.refreshed < self.ttl:
            return
        rows = list(self.backend.discover())
        if len(rows) > MAX_APPLICATIONS:
            raise LaunchNotSent("Windows application catalog exceeds its supported limit.")
        apps = {}
        for row in rows:
            if (not isinstance(row, RegisteredApp) or not row.identity
                    or len(row.identity) > 2048 or not row.display_name.strip()
                    or len(row.display_name) > 160
                    or not isinstance(row.executable, str) or len(row.executable) > 32768
                    or any(ord(char) < 32 for char in row.identity + row.display_name)):
                raise LaunchNotSent("Windows returned an invalid application registration.")
            previous = apps.get(row.id)
            if previous is not None and previous != row:
                raise LaunchNotSent("Windows returned conflicting application registrations.")
            apps[row.id] = row
        public = [{**apps[key].public(), "binding": apps[key].executable.casefold()} for key in sorted(apps)]
        revision = hashlib.sha256(json.dumps(public, sort_keys=True, separators=(",", ":"),
                                               ensure_ascii=False).encode("utf-8")).hexdigest()
        self.apps, self.revision, self.refreshed = apps, revision, self.clock()

    def catalog(self, refresh=False):
        with self.lock:
            self._refresh(force=refresh)
            return {"schema_version": 1, "revision": self.revision,
                    "applications": [self.apps[key].public() for key in sorted(self.apps)]}

    def launch(self, application_id, revision, request_id):
        if (not isinstance(application_id, str) or not APPLICATION_ID.fullmatch(application_id)
                or not isinstance(revision, str) or not REVISION.fullmatch(revision)
                or not isinstance(request_id, str) or not REQUEST_ID.fullmatch(request_id)):
            raise ValueError("Use a catalog application ID, revision, and request ID.")
        with self.lock:
            fingerprint = ("launch", application_id, revision)
            previous = self.ledger.get(request_id)
            if previous is not None:
                if previous[0] == fingerprint:
                    return dict(previous[1])
                return self._receipt(application_id, revision, request_id, "not_started",
                                     "request_id_conflict")
            # Reserve before touching Windows. A lost response never authorizes
            # another invocation of the same retained request.
            receipt = self._receipt(application_id, revision, request_id, "unknown", "interrupted")
            self.ledger[request_id] = (fingerprint, receipt)
            while len(self.ledger) > self.ledger_limit:
                self.ledger.popitem(last=False)
            try:
                self._refresh(force=True)
            except Exception:
                receipt.update(status="not_started", reason="catalog_unavailable")
                return dict(receipt)
            if revision != self.revision:
                receipt.update(status="not_started", reason="catalog_changed")
            elif application_id not in self.apps:
                receipt.update(status="not_started", reason="unknown_application")
            else:
                try:
                    self.backend.launch(self.apps[application_id].identity)
                except LaunchNotSent:
                    receipt.update(status="not_started", reason="application_unavailable")
                except Exception:
                    receipt.update(status="unknown", reason="launch_outcome_unknown")
                else:
                    receipt.update(status="accepted", reason="windows_handoff",
                                   display_name=self.apps[application_id].display_name)
            return dict(receipt)

    @staticmethod
    def _receipt(application_id, revision, request_id, status, reason):
        return {"schema_version": 1, "application_id": application_id, "revision": revision,
                "request_id": request_id, "status": status, "reason": reason,
                "window_ready": False}
