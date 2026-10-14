# -*- coding: utf-8 -*-
"""只读：列出所有顶层 explorer 壳窗（CabinetWClass/ExploreWClass），区分
   ① 被 TB 收编的（父窗口不是桌面）② 孤立散窗（父=桌面且可见）。
   顺便列出 TB 进程与 explorer 进程。"""
import ctypes, ctypes.wintypes as w
u = ctypes.windll.user32
k = ctypes.windll.kernel32

CB = ctypes.WINFUNCTYPE(ctypes.c_bool, w.HWND, w.LPARAM)
def cls_of(h):
    b = ctypes.create_unicode_buffer(256); u.GetClassNameW(h, b, 256); return b.value
def title_of(h):
    n = u.GetWindowTextLengthW(h)
    if n <= 0: return ""
    b = ctypes.create_unicode_buffer(n+1); u.GetWindowTextW(h, b, n+1); return b.value
def pid_of(h):
    p = w.DWORD(); u.GetWindowThreadProcessId(h, ctypes.byref(p)); return p.value

DESK = u.GetDesktopWindow()
rows = []
def cb(h, l):
    c = cls_of(h)
    if c in ("CabinetWClass", "ExploreWClass"):
        par = u.GetParent(h)
        owner = u.GetWindow(h, 4)  # GW_OWNER
        r = w.RECT(); u.GetWindowRect(h, ctypes.byref(r))
        rows.append((h, pid_of(h), c, title_of(h)[:50], bool(u.IsWindowVisible(h)),
                     "child" if par else ("top" if owner == 0 else "owned"),
                     (r.left, r.top, r.right-r.left, r.bottom-r.top)))
    return True
u.EnumWindows(CB(cb), 0)

tb_pid = None
for h, pid, c, t, v, kind, rect in rows:
    print("h=%s pid=%d %s kind=%s vis=%d rect=%s | %s" % (hex(h), pid, c, kind, v, rect, t))
print("壳窗总数:", len(rows))
# TB 进程
import subprocess
print("--- TB 进程 ---")
