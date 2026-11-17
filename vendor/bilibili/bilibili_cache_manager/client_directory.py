"""Choose the cache folder through the official client's Windows folder dialog."""
from __future__ import annotations

import ctypes
import time
from ctypes import wintypes as w


class ClientFolderDialog:
    def __init__(self, pids):
        self.pids = set(pids)
        self.user = ctypes.WinDLL('user32', use_last_error=True)
        self.callback = ctypes.WINFUNCTYPE(w.BOOL, w.HWND, w.LPARAM)
        self.user.GetDlgItem.argtypes = [w.HWND, ctypes.c_int]
        self.user.GetDlgItem.restype = w.HWND
        self.user.PostMessageW.argtypes = [w.HWND, w.UINT, w.WPARAM, w.LPARAM]
        self.user.IsWindowVisible.argtypes = [w.HWND]
        self.user.IsWindowEnabled.argtypes = [w.HWND]
        self.user.SendMessageTimeoutW.argtypes = [w.HWND, w.UINT, w.WPARAM, w.LPARAM,
                                                 w.UINT, w.UINT, ctypes.POINTER(ctypes.c_size_t)]
        self.user.SendMessageTimeoutW.restype = w.LPARAM
        self.existing = set(self.dialogs())
        if self.existing:
            raise RuntimeError('客户端有未关闭的对话框，请关闭后再次拉起客户端')
        self.owned = None

    def dialogs(self):
        found = []
        @self.callback
        def visit(hwnd, _):
            pid = w.DWORD()
            self.user.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
            name = ctypes.create_unicode_buffer(128)
            self.user.GetClassNameW(hwnd, name, 128)
            if pid.value in self.pids and name.value == '#32770':
                found.append(hwnd)
            return True
        self.user.EnumWindows(visit, 0)
        return found

    def choose(self, directory, timeout=12):
        deadline = time.monotonic() + timeout
        ready = None
        ready_at = 0
        while time.monotonic() < deadline:
            candidates = [h for h in self.dialogs() if h not in self.existing]
            if len(candidates) > 1:
                raise RuntimeError('客户端同时打开多个对话框，无法确认缓存目录选择框')
            if candidates:
                hwnd = candidates[0]
                edit = self.user.GetDlgItem(hwnd, 1152)
                accept = self.user.GetDlgItem(hwnd, 1)
                if edit and accept:
                    self.owned = hwnd
                if (edit and accept and self.user.IsWindowVisible(edit)
                        and self.user.IsWindowVisible(accept) and self.user.IsWindowEnabled(accept)):
                    controls = (hwnd, edit, accept)
                    if controls != ready:
                        ready, ready_at = controls, time.monotonic()
                    if time.monotonic() - ready_at < .5:
                        time.sleep(.1)
                        continue
                    self.owned = hwnd
                    text = ctypes.create_unicode_buffer(directory)
                    result = ctypes.c_size_t()
                    if not self.user.SendMessageTimeoutW(edit, 0x000C, 0, ctypes.addressof(text),
                                                         2, 1500, ctypes.byref(result)):
                        raise RuntimeError('无法填写客户端缓存目录')
                    # BM_CLICK, scoped to the newly created client folder picker.
                    self.user.PostMessageW(w.HWND(accept), 0x00F5, 0, 0)
                    return
            time.sleep(.1)
        raise RuntimeError('等待客户端缓存目录选择框超时')

    def cancel(self):
        if self.owned and self.owned in self.dialogs():
            self.user.PostMessageW(w.HWND(self.owned), 0x0111, 2, 0)
