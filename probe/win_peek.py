"""只读探针：列出指定 pid 的顶层窗口（可见性 / 位置 / 是否最小化），并报告当前前台窗口。
不移动鼠标、不抢焦点。用法: python win_peek.py <pid>
"""
import sys
import ctypes
from ctypes import wintypes

u = ctypes.windll.user32
try:
    u.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
except Exception:
    pass

pid_target = int(sys.argv[1]) if len(sys.argv) > 1 else 0
WNDENUMPROC = ctypes.WINFUNCTYPE(ctypes.c_bool, wintypes.HWND, wintypes.LPARAM)
res = []


def cb(h, l):
    pid = wintypes.DWORD()
    u.GetWindowThreadProcessId(h, ctypes.byref(pid))
    if pid.value != pid_target:
        return True
    cls = ctypes.create_unicode_buffer(256)
    u.GetClassNameW(h, cls, 256)
    txt = ctypes.create_unicode_buffer(256)
    u.GetWindowTextW(h, txt, 256)
    r = wintypes.RECT()
    u.GetWindowRect(h, ctypes.byref(r))
    res.append((h, cls.value, txt.value,
                (r.left, r.top, r.right, r.bottom),
                bool(u.IsWindowVisible(h)), bool(u.IsIconic(h))))
    return True


u.EnumWindows(WNDENUMPROC(cb), 0)
for h, c, t, r, v, i in res:
    print("hwnd=%d class=%s text=%r rect=%s visible=%s minimized=%s" % (h, c, t, r, v, i))
fg = u.GetForegroundWindow()
print("foreground hwnd =", fg)
pid = wintypes.DWORD()
u.GetWindowThreadProcessId(fg, ctypes.byref(pid))
print("foreground pid  =", pid.value)
