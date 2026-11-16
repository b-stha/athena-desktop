"""Read exact window/process identities and post a normal WM_CLOSE."""

from dataclasses import dataclass
from pathlib import PureWindowsPath


class CloseNotSent(RuntimeError):
    """No close message was queued."""


@dataclass(frozen=True)
class Window:
    hwnd: int
    pid: int
    created: str
    thread: int
    class_name: str
    image: str
    app_id: str
    title: str
    visible: bool = True

    @property
    def identity(self):
        return self.hwnd, self.pid, self.created, self.thread, self.class_name, self.image, self.app_id


def image_key(value):
    return str(PureWindowsPath(value)).casefold() if value else ''


def belongs(window, app, applications):
    if window.app_id:
        return window.app_id.casefold() == app.identity.casefold()
    if not app.executable or image_key(window.image) != image_key(app.executable):
        return False
    owners = {row.id for row in applications if row.executable
              and image_key(row.executable) == image_key(app.executable)}
    return owners == {app.id}


class WindowsDesktop:
    def _read(self, hwnd):
        import ctypes
        from ctypes import wintypes
        import win32api
        import win32gui
        import win32process
        from win32com.propsys import propsys
        try:
            if not win32gui.IsWindow(hwnd):
                return None
            thread, pid = win32process.GetWindowThreadProcessId(hwnd)
            process = win32api.OpenProcess(0x1000, False, pid)
            try:
                kernel = ctypes.WinDLL('kernel32', use_last_error=True)
                query = kernel.QueryFullProcessImageNameW
                query.argtypes = [wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR,
                                  ctypes.POINTER(wintypes.DWORD)]
                query.restype = wintypes.BOOL
                buffer = ctypes.create_unicode_buffer(32768)
                size = wintypes.DWORD(len(buffer))
                if not query(int(process), 0, buffer, ctypes.byref(size)):
                    return None
                image = buffer.value
                created = str(win32process.GetProcessTimes(process)['CreationTime'])
                app_id = ''
                try:
                    store = propsys.SHGetPropertyStoreForWindow(hwnd)
                    value = store.GetValue(propsys.PSGetPropertyKeyFromName('System.AppUserModel.ID')).GetValue()
                    app_id = value if isinstance(value, str) else ''
                    store = None
                except Exception:
                    pass
                if not app_id:
                    get_id = kernel.GetApplicationUserModelId
                    get_id.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.UINT), wintypes.LPWSTR]
                    get_id.restype = wintypes.LONG
                    length = wintypes.UINT()
                    if get_id(int(process), ctypes.byref(length), None) == 122 and 0 < length.value <= 2048:
                        text = ctypes.create_unicode_buffer(length.value)
                        if get_id(int(process), ctypes.byref(length), text) == 0:
                            app_id = text.value
            finally:
                process.Close()
            if tuple(win32process.GetWindowThreadProcessId(hwnd)) != (thread, pid):
                return None
            title = ''.join(char if ord(char) >= 32 and ord(char) != 127 else ' '
                            for char in win32gui.GetWindowText(hwnd))[:240]
            return Window(hwnd, pid, created, thread, win32gui.GetClassName(hwnd),
                          image, app_id, title, bool(win32gui.IsWindowVisible(hwnd)))
        except Exception:
            # Unreadable processes/windows are never selected by name guessing.
            return None

    def windows(self):
        import pythoncom
        import win32con
        import win32gui
        pythoncom.CoInitialize()
        try:
            result = []
            def visit(hwnd, _):
                if (not win32gui.IsWindowVisible(hwnd)
                        or win32gui.GetWindow(hwnd, win32con.GW_OWNER)
                        or win32gui.GetWindowLong(hwnd, win32con.GWL_EXSTYLE) & win32con.WS_EX_TOOLWINDOW):
                    return
                row = self._read(hwnd)
                if row is not None:
                    result.append(row)
            win32gui.EnumWindows(visit, None)
            return result
        finally:
            pythoncom.CoUninitialize()

    def current(self, window):
        import pythoncom
        import win32gui
        pythoncom.CoInitialize()
        try:
            if not win32gui.IsWindow(window.hwnd):
                return None
            result = self._read(window.hwnd)
            if result is None and win32gui.IsWindow(window.hwnd):
                raise RuntimeError('Window identity is temporarily unavailable.')
            return result
        finally:
            pythoncom.CoUninitialize()

    def post_close(self, window):
        import pythoncom
        import win32con
        import win32gui
        pythoncom.CoInitialize()
        try:
            current = self._read(window.hwnd)
            if (current is None or current.identity != window.identity
                    or current.title != window.title or not current.visible):
                raise CloseNotSent('The selected window changed.')
            if not win32gui.IsWindowEnabled(window.hwnd):
                raise CloseNotSent('The window requires attention before closing.')
            try:
                # No focus change, keyboard injection, process termination or
                # save-dialog response. The application handles its usual close.
                win32gui.PostMessage(window.hwnd, win32con.WM_CLOSE, 0, 0)
            except Exception:
                raise CloseNotSent('Windows did not queue the close request.') from None
        finally:
            pythoncom.CoUninitialize()
