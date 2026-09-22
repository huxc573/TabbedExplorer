"""只读探针：给一个 pid，列出顶层窗口的样式/放置状态/是否响应消息。
不移动鼠标、不抢焦点、不改任何状态。用法: python win_state.py <pid>
"""
import sys
import ctypes
from ctypes import wintypes

u = ctypes.windll.user32
k = ctypes.windll.kernel32
try:
    u.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
except Exception:
    pass

GWL_STYLE = -16
GWL_EXSTYLE = -20
WS_MINIMIZE = 0x20000000
WS_VISIBLE = 0x10000000
WM_NULL = 0x0000
SMTO_ABORTIFHUNG = 0x0002


class WINDOWPLACEMENT(ctypes.Structure):
    _fields_ = [("length", wintypes.UINT),
                ("flags", wintypes.UINT),
                ("showCmd", wintypes.UINT),
                ("ptMinPosition", wintypes.POINT),
                ("ptMaxPosition", wintypes.POINT),
                ("rcNormalPosition", wintypes.RECT)]


pid_target = int(sys.argv[1])
WNDENUMPROC = ctypes.WINFUNCTYPE(ctypes.c_bool, wintypes.HWND, wintypes.LPARAM)
rows = []


def cb(h, l):
    pid = wintypes.DWORD()
    u.GetWindowThreadProcessId(h, ctypes.byref(pid))
    if pid.value != pid_target:
        return True
    cls = ctypes.create_unicode_buffer(256)
    u.GetClassNameW(h, cls, 256)
    txt = ctypes.create_unicode_buffer(256)
    u.GetWindowTextW(h, txt, 256)
    style = u.GetWindowLongW(h, GWL_STYLE) & 0xFFFFFFFF
    pl = WINDOWPLACEMENT()
    pl.length = ctypes.sizeof(WINDOWPLACEMENT)
    u.GetWindowPlacement(h, ctypes.byref(pl))
    res = ctypes.c_size_t()
    ok = u.SendMessageTimeoutW(h, WM_NULL, 0, 0, SMTO_ABORTIFHUNG, 300, ctypes.byref(res))
    kids = []
    KID = ctypes.WINFUNCTYPE(ctypes.c_bool, wintypes.HWND, wintypes.LPARAM)

    def kcb(ch, cl):
        kc = ctypes.create_unicode_buffer(256)
        u.GetClassNameW(ch, kc, 256)
        kt = ctypes.create_unicode_buffer(256)
        u.GetWindowTextW(ch, kt, 256)
        kids.append("%s(%r)" % (kc.value, kt.value))
        return True
    u.EnumChildWindows(h, KID(kcb), 0)
    rows.append((h, cls.value, txt.value, style, pl.showCmd, bool(ok),
                 pl.rcNormalPosition, kids[:8]))
    return True


u.EnumWindows(WNDENUMPROC(cb), 0)
SHOW = {1: "normal", 2: "minimized", 3: "maximized", 0: "hidden"}
for h, cls, txt, style, show, alive, rc, kids in rows:
    print("hwnd=%d" % h)
    print("  class=%s text=%r" % (cls, txt))
    print("  style=0x%08X  WS_VISIBLE=%s WS_MINIMIZE=%s" %
          (style, bool(style & WS_VISIBLE), bool(style & WS_MINIMIZE)))
    print("  showCmd=%d(%s)  响应WM_NULL=%s" % (show, SHOW.get(show, "?"), alive))
    print("  normalRect=(%d,%d,%d,%d) 宽高=%dx%d" %
          (rc.left, rc.top, rc.right, rc.bottom,
           rc.right - rc.left, rc.bottom - rc.top))
    print("  子窗口: %s" % (", ".join(kids) if kids else "(无)"))
