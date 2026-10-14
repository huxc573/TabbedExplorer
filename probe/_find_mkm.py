# -*- coding: utf-8 -*-
import ctypes, ctypes.wintypes as w
u = ctypes.windll.user32
CB = ctypes.WINFUNCTYPE(ctypes.c_bool, w.HWND, w.LPARAM)
res = []
def cb(h, l):
    pid = w.DWORD()
    u.GetWindowThreadProcessId(h, ctypes.byref(pid))
    n = u.GetWindowTextLengthW(h)
    cls = ctypes.create_unicode_buffer(256)
    u.GetClassNameW(h, cls, 256)
    if n:
        buf = ctypes.create_unicode_buffer(n+1)
        u.GetWindowTextW(h, buf, n+1)
        res.append((pid.value, cls.value, buf.value, h))
    else:
        res.append((pid.value, cls.value, "", h))
    return True
u.EnumWindows(CB(cb), 0)
for pid, cls, title, h in res:
    if "mykeymap" in cls.lower() or "mykeymap" in title.lower():
        print(pid, "|", cls, "|", title, "|", hex(h))
