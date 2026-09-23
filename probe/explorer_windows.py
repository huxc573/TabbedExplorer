# 只读探针：列出所有 explorer.exe 进程的顶层窗口（不论可见与否）
# 用途：程序没在跑时，判断桌面上是否残留「孤儿文件夹窗口」——
#       正常情况应只有 shell 自己那几个（Progman / WorkerW / Shell_TrayWnd / CabinetWClass 桌面）。
import ctypes
import subprocess
from ctypes import wintypes

u32 = ctypes.windll.user32
u32.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
u32.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
u32.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
u32.GetWindowLongW.argtypes = [wintypes.HWND, ctypes.c_int]
u32.GetWindowLongW.restype = ctypes.c_long
u32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(ctypes.c_ulong)]

EnumProc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq explorer.exe", "/FO", "CSV", "/NH"],
                     capture_output=True, text=True, errors="replace").stdout.strip()
pids = {}
for line in out.splitlines():
    parts = [p.strip('"') for p in line.split('","')]
    if len(parts) > 1 and parts[1].isdigit():
        pids[int(parts[1])] = True
print("explorer.exe 进程数 =", len(pids))

WS_CHILD, WS_VISIBLE, WS_EX_TOPMOST = 0x40000000, 0x10000000, 0x8
rows = []


def enum(hwnd, _):
    pid = ctypes.c_ulong()
    u32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    if pid.value not in pids:
        return True
    cls = ctypes.create_unicode_buffer(256)
    txt = ctypes.create_unicode_buffer(512)
    u32.GetClassNameW(hwnd, cls, 256)
    u32.GetWindowTextW(hwnd, txt, 512)
    st = u32.GetWindowLongW(hwnd, -16) & 0xFFFFFFFF
    ex = u32.GetWindowLongW(hwnd, -20) & 0xFFFFFFFF
    r = wintypes.RECT()
    u32.GetWindowRect(hwnd, ctypes.byref(r))
    rows.append((pid.value, hwnd, cls.value, txt.value, st, ex, r))
    return True


u32.EnumWindows(EnumProc(enum), 0)
shell_kept = {"Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
              "Shell_InputSwitchTopLevelWindow", "ForegroundStaging"}
rows.sort(key=lambda x: (x[0], x[2]))
orphan = 0
for pid, hwnd, cls, txt, st, ex, r in rows:
    flags = []
    if st & WS_CHILD:
        flags.append("CHILD")
    if st & WS_VISIBLE:
        flags.append("VIS")
    if ex & WS_EX_TOPMOST:
        flags.append("TOPMOST")
    tag = ""
    if cls not in shell_kept:
        tag = "   <== 非 shell 窗口"
        orphan += 1
    print("pid=%-6d 0x%06X %-9s cls=%-22s rect=%d,%d-%d,%d title=%r%s"
          % (pid, hwnd, ",".join(flags) or "-", cls, r.left, r.top, r.right, r.bottom, txt, tag))
print("—— 顶层窗口共 %d 个，其中非 shell 类 %d 个" % (len(rows), orphan))
