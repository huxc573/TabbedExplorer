"""只读探针：盯住顶层 CabinetWClass 的扩展样式，回答「收编之前那扇窗会不会占一个任务栏按钮」。

背景：`EmbedApi.MakeTransparent` 除了把窗口置成全透明（防画面闪），现在还会打上
`WS_EX_TOOLWINDOW` 并摘掉 `WS_EX_APPWINDOW` —— 后者是任务栏按钮的唯一判据。本探针就是
拿来做物证的：每 30ms 全屏扫一遍，只挑 `CabinetWClass` / `ExploreWClass`，把它们**首次出现**
以及**样式位每次变化**记下来。只要「窗口变可见（vis=1）的那条记录」里已经带 TOOLWINDOW、
不带 APPWINDOW，任务栏那一刻就不可能给它加按钮。

⚠ 纯读：不点鼠标、不发消息、不抢前台。别往这里加任何写操作。
用法：python tray_icon_probe.py [秒数] [输出文件]
"""

import ctypes
import sys
import time
from ctypes import wintypes

u32 = ctypes.windll.user32

GWL_EXSTYLE = -20
WS_EX_TOPMOST = 0x00000008
WS_EX_TOOLWINDOW = 0x00000080
WS_EX_APPWINDOW = 0x00040000
WS_EX_LAYERED = 0x00080000

# 64 位下不声明 argtypes/restype，HWND 会被当 32 位截断、样式读出来是垃圾值。
GetWindowLongPtrW = u32.GetWindowLongPtrW
GetWindowLongPtrW.argtypes = [wintypes.HWND, ctypes.c_int]
GetWindowLongPtrW.restype = ctypes.c_longlong

GetClassNameW = u32.GetClassNameW
GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
GetClassNameW.restype = ctypes.c_int

GetWindowThreadProcessId = u32.GetWindowThreadProcessId
GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]

IsWindowVisible = u32.IsWindowVisible
IsWindowVisible.argtypes = [wintypes.HWND]

EnumWindows = u32.EnumWindows
ENUMPROC = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

CABS = ("CabinetWClass", "ExploreWClass")

duration = float(sys.argv[1]) if len(sys.argv) > 1 else 25.0
path = sys.argv[2] if len(sys.argv) > 2 else "tray_probe.log"

seen = {}          # hwnd -> (exstyle, visible)
t0 = time.time()
out = open(path, "w", encoding="utf-8")


def flags_of(ex):
    names = []
    if ex & WS_EX_TOOLWINDOW:
        names.append("TOOLWINDOW")
    if ex & WS_EX_APPWINDOW:
        names.append("APPWINDOW")
    if ex & WS_EX_LAYERED:
        names.append("LAYERED")
    if ex & WS_EX_TOPMOST:
        names.append("TOPMOST")
    return ",".join(names) if names else "-"


def on_window(hwnd, _):
    buf = ctypes.create_unicode_buffer(256)
    if GetClassNameW(hwnd, buf, 256) <= 0:
        return True
    if buf.value not in CABS:
        return True

    ex = GetWindowLongPtrW(hwnd, GWL_EXSTYLE) & 0xFFFFFFFF
    vis = 1 if IsWindowVisible(hwnd) else 0
    prev = seen.get(hwnd)
    if prev == (ex, vis):
        return True
    seen[hwnd] = (ex, vis)
    tag = "首次出现" if prev is None else "样式变化"
    pid = wintypes.DWORD(0)
    GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    out.write("%7.3fs %s  hwnd=0x%X pid=%d vis=%d ex=0x%08X [%s]\n"
              % (time.time() - t0, tag, hwnd, pid.value, vis, ex, flags_of(ex)))
    out.flush()
    return True


while time.time() - t0 < duration:
    EnumWindows(ENUMPROC(on_window), 0)
    time.sleep(0.03)

out.close()
print("probe done, %d hwnd tracked" % len(seen))
