# 只读探针：把"看得见的大窗口"全列出来（谁在前台、谁是顶层置顶）
# 用来回答：TabbedExplorer 的主窗口此刻到底在不在屏幕上
import ctypes
import subprocess
from ctypes import wintypes

u32 = ctypes.windll.user32
u32.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
u32.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
u32.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
u32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(ctypes.c_ulong)]
u32.GetWindowLongW.argtypes = [wintypes.HWND, ctypes.c_int]
u32.GetWindowLongW.restype = ctypes.c_long

EnumProc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
WS_EX_TOPMOST = 0x8

fg = u32.GetForegroundWindow()
print("前台窗口 = 0x%X" % fg)

EnumProc2 = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
rows = []


def enum(hwnd, _):
    if not u32.IsWindowVisible(hwnd):
        return True
    r = wintypes.RECT()
    u32.GetWindowRect(hwnd, ctypes.byref(r))
    w, h = r.right - r.left, r.bottom - r.top
    if w < 400 or h < 300:
        return True
    cls = ctypes.create_unicode_buffer(256)
    txt = ctypes.create_unicode_buffer(512)
    u32.GetClassNameW(hwnd, cls, 256)
    u32.GetWindowTextW(hwnd, txt, 512)
    pid = ctypes.c_ulong()
    u32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    rows.append((hwnd, pid.value, cls.value, txt.value, r, u32.GetWindowLongW(hwnd, -20) & 0xFFFFFFFF))
    return True


u32.EnumWindows(EnumProc2(enum), 0)
for hwnd, pid, cls, txt, r, ex in rows:
    mark = " ★TOPMOST" if ex & WS_EX_TOPMOST else ""
    print("0x%06X pid=%-6d %dx%d at %d,%d cls=%s title=%r%s"
          % (hwnd, pid, r.right - r.left, r.bottom - r.top, r.left, r.top, cls, txt, mark))
print("—— 共 %d 个可见大窗口" % len(rows))
