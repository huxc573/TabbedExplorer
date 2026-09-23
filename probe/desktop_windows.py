"""只读探针：列出指定隐藏桌面上的所有窗口与所属进程。

用法: python desktop_windows.py [桌面名]   不给名字则列出所有非 Default 桌面。
EnumWindows 只覆盖调用线程的桌面；这里用 OpenDesktop + EnumDesktopWindows 进到隐藏桌面里看。
"""
import ctypes
import sys
from ctypes import wintypes

u32 = ctypes.windll.user32
u32.OpenDesktopW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
u32.OpenDesktopW.restype = wintypes.HANDLE
u32.EnumDesktopWindows.argtypes = [wintypes.HANDLE, ctypes.c_void_p, wintypes.LPARAM]
u32.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
u32.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
u32.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
u32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(ctypes.c_ulong)]
u32.GetWindowLongW.argtypes = [wintypes.HWND, ctypes.c_int]
u32.GetWindowLongW.restype = ctypes.c_long

DESKTOP_READOBJECTS = 0x0001
DESKTOP_ENUMERATE = 0x0040

name = sys.argv[1] if len(sys.argv) > 1 else None
if not name:
    names = []
    u32.OpenWindowStationW("winsta0", False, 0x0001)
    print("未给桌面名；请用 desktops_list.py 先取名字")
    raise SystemExit(1)

h = u32.OpenDesktopW(name, 0, False, DESKTOP_READOBJECTS | DESKTOP_ENUMERATE)
if not h:
    raise SystemExit("打开桌面 %r 失败 (err=%d)" % (name, ctypes.get_last_error()))

rows = []
PROC = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)


def cb(hwnd, _):
    pid = ctypes.c_ulong()
    u32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    cls = ctypes.create_unicode_buffer(256)
    txt = ctypes.create_unicode_buffer(512)
    u32.GetClassNameW(hwnd, cls, 256)
    u32.GetWindowTextW(hwnd, txt, 512)
    r = wintypes.RECT()
    u32.GetWindowRect(hwnd, ctypes.byref(r))
    st = u32.GetWindowLongW(hwnd, -16) & 0xFFFFFFFF
    rows.append((pid.value, hwnd, cls.value, txt.value, st, r))
    return True


u32.EnumDesktopWindows(h, PROC(cb), 0)
pids = {}
for pid, hwnd, cls, txt, st, r in sorted(rows):
    pids[pid] = pids.get(pid, 0) + 1
    print("pid=%-6d 0x%06X %-8s cls=%-22s rect=%d,%d-%d,%d title=%r"
          % (pid, hwnd, "VIS" if st & 0x10000000 else "-", cls,
             r.left, r.top, r.right, r.bottom, txt))
print("—— 桌面 %r：窗口 %d 个，进程 %d 个: %s" % (name, len(rows), len(pids), sorted(pids)))
