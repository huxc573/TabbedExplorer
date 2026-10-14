# -*- coding: utf-8 -*-
"""
只读探针：把「我们进程的窗体 → 内容面板 → 每个标签那扇 explorer」整条链拍下来。

想回答：内嵌窗口顶上那条 Ribbon（「文件 计算机 查看」）在某些标签下点不动 ——
是不是有窗口被摆错了（尺寸 0 / 位置重叠 / 可见位不对 / 多出一扇）。

全程只读：GetClassNameW / GetWindowRect / GetWindowLongW / GetParent / EnumWindows。
不动鼠标、不发消息、不改任何窗口、不抢前台。
用法：python probe/host_tree_probe.py
"""
import ctypes
from ctypes import wintypes

u32 = ctypes.windll.user32

u32.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
u32.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
u32.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
u32.GetParent.argtypes = [wintypes.HWND]
u32.GetParent.restype = wintypes.HWND
u32.GetWindow.argtypes = [wintypes.HWND, ctypes.c_uint]
u32.GetWindow.restype = wintypes.HWND
u32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
u32.GetWindowLongW.argtypes = [wintypes.HWND, ctypes.c_int]
u32.GetWindowLongW.restype = ctypes.c_uint
u32.EnumWindows.argtypes = [ctypes.c_void_p, wintypes.LPARAM]

GWL_STYLE = -16
WS_VISIBLE = 0x10000000
WS_CHILD = 0x40000000
GW_CHILD, GW_HWNDNEXT = 5, 2
EnumProc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

WF = "WindowsForms10.Window"


def cls(h):
    b = ctypes.create_unicode_buffer(256)
    u32.GetClassNameW(h, b, 256)
    return b.value


def txt(h):
    b = ctypes.create_unicode_buffer(512)
    u32.GetWindowTextW(h, b, 512)
    return b.value


def rect(h):
    r = wintypes.RECT()
    u32.GetWindowRect(h, ctypes.byref(r))
    return (r.left, r.top, r.right - r.left, r.bottom - r.top)


def pid(h):
    p = wintypes.DWORD()
    u32.GetWindowThreadProcessId(h, ctypes.byref(p))
    return p.value


def vis(h):
    return bool(u32.GetWindowLongW(h, GWL_STYLE) & WS_VISIBLE)


def kids(h):
    o, c = [], u32.GetWindow(h, GW_CHILD)
    while c:
        o.append(c)
        c = u32.GetWindow(c, GW_HWNDNEXT)
    return o


def desc(h, d=10):
    if d <= 0:
        return []
    o = []
    for c in kids(h):
        o.append(c)
        o.extend(desc(c, d - 1))
    return o


def line(h, indent):
    clsname = cls(h)
    if len(clsname) > 46:
        clsname = clsname[:46] + "…"
    print("%s0x%-7X %-48s VIS=%-5s rect=%-22s pid=%-6d %r" % (
        "  " * indent, h, clsname, vis(h), rect(h), pid(h), txt(h)[:30]))


def main():
    tops = []

    def cb(h, _):
        tops.append(h)
        return True

    u32.EnumWindows(EnumProc(cb), 0)

    forms = []
    for t in tops:
        if cls(t).startswith(WF):
            forms.append(t)
    print("我们进程的顶层窗体 %d 个：" % len(forms))
    for f in forms:
        line(f, 0)
        for c in desc(f, 6):
            k = cls(c)
            if k.startswith(WF) or k in ("CabinetWClass", "UIRibbonCommandBarDock",
                                         "UIRibbonWorkPane", "NetUIHWND", "DUIViewWndClassName",
                                         "ShellTabWindowClass"):
                # 只打「结构节点」，Ribbon 那堆内部窗只留一条 DockTop
                if k == "UIRibbonCommandBarDock":
                    r = rect(c)
                    if r[2] < 100:
                        continue
                depth = 0
                x = c
                while x and depth < 12:
                    x = u32.GetParent(x)
                    depth += 1
                    if x == f:
                        break
                line(c, min(depth, 5))
        print()


if __name__ == "__main__":
    main()
