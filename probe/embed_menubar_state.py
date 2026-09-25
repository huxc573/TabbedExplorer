# -*- coding: utf-8 -*-
"""
只读探针：一句话说清「内嵌窗口 / 原生窗口」顶部长什么样。

想知道的事：一个 CabinetWClass 顶上到底是
  · Ribbon 那条（UIRibbonCommandBarDock，原生窗口里就是它，25px，收起时显示「文件 计算机 查看」），还是
  · 老式菜单栏（ShellTabWindowClass 下「子窗口是 ReBarWindow32 的 WorkerW」，白条，20px）。
两者是互斥的：Ribbon 建出来了就不会有老式菜单栏。

用法：python probe/embed_menubar_state.py
全程只读：只发 GetClassNameW/GetWindowRect/IsWindowVisible/GetWindowLongW，不动鼠标、不抢前台、不改任何窗口。
"""
import ctypes
from ctypes import wintypes

u32 = ctypes.windll.user32
k32 = ctypes.windll.kernel32

u32.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
u32.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
u32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
u32.GetWindow.argtypes = [wintypes.HWND, ctypes.c_uint]
u32.GetWindow.restype = wintypes.HWND
u32.GetParent.argtypes = [wintypes.HWND]
u32.GetParent.restype = wintypes.HWND
u32.IsWindowVisible.argtypes = [wintypes.HWND]
u32.IsWindowVisible.restype = wintypes.BOOL
u32.GetWindowLongW.argtypes = [wintypes.HWND, ctypes.c_int]
u32.GetWindowLongW.restype = ctypes.c_uint
u32.EnumWindows.argtypes = [ctypes.c_void_p, wintypes.LPARAM]

GWL_STYLE = -16
WS_VISIBLE = 0x10000000
GW_CHILD, GW_HWNDNEXT = 5, 2
EnumProc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)


def cls(h):
    b = ctypes.create_unicode_buffer(256)
    u32.GetClassNameW(h, b, 256)
    return b.value


def rect(h):
    r = wintypes.RECT()
    if not u32.GetWindowRect(h, ctypes.byref(r)):
        return None
    return r.left, r.top, r.right - r.left, r.bottom - r.top  # x, y, w, h


def children(h):
    out, c = [], u32.GetWindow(h, GW_CHILD)
    while c:
        out.append(c)
        c = u32.GetWindow(c, GW_HWNDNEXT)
    return out


def descendants(h, depth=8):
    if depth <= 0:
        return []
    out = []
    for c in children(h):
        out.append(c)
        out.extend(descendants(c, depth - 1))
    return out


def pid_of(h):
    p = wintypes.DWORD()
    u32.GetWindowThreadProcessId(h, ctypes.byref(p))
    return p.value


def report(cab):
    kids = descendants(cab)
    ribbon = None
    for k in kids:
        if cls(k) == "UIRibbonCommandBarDock":
            r = rect(k)
            if not r or r[2] <= 100:                 # 只看横向那条（Top/Bottom），左右 dock 宽度是 0
                continue
            vis = bool(u32.GetWindowLongW(k, GWL_STYLE) & WS_VISIBLE)
            # 别被「Bottom 那条 h=0 且不可见」盖掉 Top 那条：可见且更高的优先
            if ribbon is None or (r[3], vis) > (ribbon[0], ribbon[1]):
                ribbon = (r[3], vis)
    menubar = None
    container = None
    view = None
    for k in kids:
        if cls(k) == "ShellTabWindowClass":
            container = k
        if cls(k) == "DUIViewWndClassName" and view is None:
            view = k
    if container:
        for k in children(container):
            if cls(k) == "WorkerW" and any(cls(g) == "ReBarWindow32" for g in children(k)):
                r = rect(k)
                menubar = (r[3], bool(u32.GetWindowLongW(k, GWL_STYLE) & WS_VISIBLE))
    rv = rect(view) if view else None
    rc = rect(container) if container else None
    par = u32.GetParent(cab) or 0
    kind = "原生" if par == 0 else "内嵌"
    print("  [%s] cab=0x%X pid=%d parent=0x%X  Ribbon=%s  老式菜单栏=%s  文件视图高=%s" % (
        kind, cab, pid_of(cab), par,
        ("%dpx%s" % (ribbon[0], "" if ribbon[1] else "(不可见)")) if ribbon else "没有",
        ("%dpx%s" % (menubar[0], "" if menubar[1] else "(不可见)")) if menubar else "没有",
        ("%d(容器%d)" % (rv[3], rc[3])) if rv and rc else "?"))


def main():
    # ⚠ 内嵌的 cab 早被 SetParent 进来了 ⇒ 不再是顶层窗口，EnumWindows 看不到它。
    # 所以从每个顶层窗口往下走一遍，把 CabinetWClass 全捞出来（原生那份也在里面）。
    tops = []

    def cb(h, _):
        tops.append(h)
        return True

    u32.EnumWindows(EnumProc(cb), 0)

    cabs = []
    for t in tops:
        if cls(t) == "CabinetWClass":
            cabs.append(t)
        cabs.extend(d for d in descendants(t, 10) if cls(d) == "CabinetWClass")
    print("共 %d 个 CabinetWClass：" % len(cabs))
    for h in cabs:
        report(h)


if __name__ == "__main__":
    main()
