# 只读探针：把 TabbedExplorer.exe 这个进程拥有的顶层窗口 + 一层子窗口全列出来
# 目的：主窗口（EmbedForm）到底在不在顶层？样式带没带 WS_EX_TOPMOST？
import ctypes
import subprocess
from ctypes import wintypes

u32 = ctypes.windll.user32
k32 = ctypes.windll.kernel32
u32.GetWindowLongW.argtypes = [wintypes.HWND, ctypes.c_int]
u32.GetWindowLongW.restype = ctypes.c_long
u32.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
u32.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
u32.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
u32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(ctypes.c_ulong)]

GWL_STYLE, GWL_EXSTYLE = -16, -20
WS_CHILD, WS_VISIBLE = 0x40000000, 0x10000000
WS_EX_TOPMOST = 0x8

out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq TabbedExplorer.exe", "/FO", "CSV", "/NH"],
                     capture_output=True, text=True).stdout.strip()
pids = []
for line in out.splitlines():
    parts = [p.strip('"') for p in line.split('","')]
    if len(parts) > 1 and parts[1].isdigit():
        pids.append(int(parts[1]))
print("TabbedExplorer pid =", pids)
if not pids:
    raise SystemExit("程序没在跑")

EnumProc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)


def info(h, indent=""):
    cls = ctypes.create_unicode_buffer(256)
    txt = ctypes.create_unicode_buffer(512)
    u32.GetClassNameW(h, cls, 256)
    u32.GetWindowTextW(h, txt, 512)
    st = u32.GetWindowLongW(h, GWL_STYLE) & 0xFFFFFFFF
    ex = u32.GetWindowLongW(h, GWL_EXSTYLE) & 0xFFFFFFFF
    r = wintypes.RECT()
    u32.GetWindowRect(h, ctypes.byref(r))
    pid = ctypes.c_ulong()
    u32.GetWindowThreadProcessId(h, ctypes.byref(pid))
    flags = []
    if st & WS_CHILD:
        flags.append("CHILD")
    if st & WS_VISIBLE:
        flags.append("VIS")
    if ex & WS_EX_TOPMOST:
        flags.append("TOPMOST")
    print("%s0x%06X pid=%d [%s] cls=%s rect=%d,%d-%d,%d title=%r st=0x%08X ex=0x%08X"
          % (indent, h, pid.value, ",".join(flags) or "-", cls.value,
             r.left, r.top, r.right, r.bottom, txt.value, st, ex))


def top(hwnd, _):
    pid = ctypes.c_ulong()
    u32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    if pid.value in pids:
        info(hwnd)
        kids = []

        def child(h, __):
            kp = ctypes.c_ulong()
            u32.GetWindowThreadProcessId(h, ctypes.byref(kp))
            kids.append((h, kp.value))
            return True

        u32.EnumChildWindows(hwnd, EnumProc(child), 0)
        for kh, kpid in kids:
            if kpid in pids:
                info(kh, "     ")
    return True


u32.EnumWindows(EnumProc(top), 0)
