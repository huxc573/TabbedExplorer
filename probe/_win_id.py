# -*- coding: utf-8 -*-
"""只读：列出所有类名含 QuickLook 的顶层窗，带 pid / 标题 / rect / 可见。"""
import ctypes, ctypes.wintypes as w
u = ctypes.windll.user32
CB = ctypes.WINFUNCTYPE(ctypes.c_bool, w.HWND, w.LPARAM)
rows = []
def cls_of(h):
    b = ctypes.create_unicode_buffer(256); u.GetClassNameW(h, b, 256); return b.value
def title_of(h):
    n = u.GetWindowTextLengthW(h)
    if n <= 0: return ""
    b = ctypes.create_unicode_buffer(n+1); u.GetWindowTextW(h, b, n+1); return b.value
def pid_of(h):
    p = w.DWORD(); u.GetWindowThreadProcessId(h, ctypes.byref(p)); return p.value
def cb(h, l):
    c = cls_of(h)
    if "quicklook" in c.lower():
        r = w.RECT(); u.GetWindowRect(h, ctypes.byref(r))
        rows.append((hex(h), pid_of(h), c, title_of(h)[:40], bool(u.IsWindowVisible(h)),
                     (r.left, r.top, r.right-r.left, r.bottom-r.top)))
    return True
u.EnumWindows(CB(cb), 0)
for r in rows: print(r)
print("总命中:", len(rows))
