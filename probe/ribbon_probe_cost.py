"""只读：量 EnumChildWindows 在一扇真 explorer 窗口上的代价 + 后代数量。

背景：`EmbedApi.ScheduleTaskbarBits` 每 20ms 调一次 `WinFind.ByClass`（= EnumChildWindows
+ 每个子窗口一次跨进程 GetClassName），一轮最多 1.2 秒。本探针就是量「一轮要多久、要看多少窗」。
纯读：不点鼠标、不发消息、不改样式。
用法：python ribbon_probe_cost.py
"""
import ctypes
import time
from ctypes import wintypes

u32 = ctypes.windll.user32

GetClassNameW = u32.GetClassNameW
GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
GetClassNameW.restype = ctypes.c_int

EnumWindows = u32.EnumWindows
EnumChildWindows = u32.EnumChildWindows
ENUMPROC = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

u32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]

targets = []


def on_top(hwnd, _):
    buf = ctypes.create_unicode_buffer(256)
    GetClassNameW(hwnd, buf, 256)
    if buf.value in ("CabinetWClass", "ExploreWClass"):
        pid = wintypes.DWORD(0)
        u32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        targets.append((hwnd, pid.value))
    return True


EnumWindows(ENUMPROC(on_top), 0)
print("顶层 CabinetWClass/ExploreWClass: %d 扇（含被 SetParent 嵌走的会出现在下面的子窗口扫描里）" % len(targets))


def count_children(root):
    n = [0]
    names = set()

    def cb(h, _):
        n[0] += 1
        b = ctypes.create_unicode_buffer(128)
        GetClassNameW(h, b, 128)
        names.add(b.value)
        return True

    p = ENUMPROC(cb)
    EnumChildWindows(root, p, 0)
    return n[0], names


for hwnd, pid in targets[:3]:
    t = time.perf_counter()
    n, names = count_children(hwnd)
    dt = (time.perf_counter() - t) * 1000
    print("hwnd=0x%X pid=%d  后代 %d 个  一遍 EnumChildWindows = %.2fms" % (hwnd, pid, n, dt))
    t = time.perf_counter()
    RIBBON = "UIRibbonCommandBarDock"
    hit = [None]

    def cb2(h, _):
        b = ctypes.create_unicode_buffer(128)
        GetClassNameW(h, b, 128)
        if b.value.lower() == RIBBON.lower():
            hit[0] = h
            return False
        return True

    p2 = ENUMPROC(cb2)
    EnumChildWindows(hwnd, p2, 0)
    dt2 = (time.perf_counter() - t) * 1000
    print("   找 Ribbon：%s  耗时 %.2fms" % ("找到" if hit[0] else "没找到", dt2))
    print("   出现过的类名（前 12 个）:", sorted(names)[:12])

print()
print("--- 所有顶层 explorer 窗口按 pid 归组 ---")
from collections import Counter
c = Counter(p for _, p in targets)
for pid, n in c.most_common():
    print("pid=%d -> %d 扇" % (pid, n))
