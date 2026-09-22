"""只读探针：数一下当前有几个顶层的资源管理器文件夹窗口，各自属于哪个 pid、标题是什么。

用途：验证「关标签 / 退出时有没有把嵌进来的窗口留成孤儿」——
退出前数一次、退出后再数一次，数字不该涨。不移动鼠标、不抢焦点、不碰任何窗口。
用法: python cab_count.py
"""
import ctypes
from ctypes import wintypes

u = ctypes.windll.user32
try:
    u.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
except Exception:
    pass

CB = ctypes.WINFUNCTYPE(ctypes.c_bool, wintypes.HWND, wintypes.LPARAM)
rows = []


def cb(h, l):
    b = ctypes.create_unicode_buffer(256)
    u.GetClassNameW(h, b, 256)
    if b.value in ("CabinetWClass", "ExploreWClass"):
        t = ctypes.create_unicode_buffer(512)
        u.GetWindowTextW(h, t, 512)
        pid = wintypes.DWORD()
        u.GetWindowThreadProcessId(h, ctypes.byref(pid))
        vis = u.IsWindowVisible(h)
        rows.append("%s pid=%d vis=%d %r" % (hex(h), pid.value, vis, t.value))
    return True


u.EnumWindows(CB(cb), 0)
print("顶层文件夹窗口 %d 个：" % len(rows))
for r in rows:
    print("   ", r)
