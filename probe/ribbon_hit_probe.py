# -*- coding: utf-8 -*-
"""
只读探针：那排「文件 计算机 查看」到底是谁的窗口、点它一下落点归谁。

想回答的问题：内嵌窗口顶上那条 Ribbon（收起态，25px，显示「文件 计算机 查看」）
点下去没反应 —— 是因为
  A. 鼠标落点被**我们自己的窗口**吃掉了（ribbon 被盖住 / 坐标错位），还是
  B. 落点确实落在 explorer 的 Ribbon 上，但它自己不响应（子窗口里 UIRibbon 不干活）。

做法：对每个 CabinetWClass 找到顶部那条 `UIRibbonCommandBarDock`，
在它左端（「文件」那块）取一个点，调 `WindowFromPoint` 看这个像素归谁。
再和**原生窗口**对表：原生窗口同一个位置落点应该是 explorer 自己的窗口。

⚠ 全程只读：不点鼠标、不动光标、不抢前台、不改任何窗口。
用法：python probe/ribbon_hit_probe.py
"""
import ctypes
import sys
from ctypes import wintypes

u32 = ctypes.windll.user32

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
u32.IsWindowEnabled.argtypes = [wintypes.HWND]
u32.IsWindowEnabled.restype = wintypes.BOOL
u32.WindowFromPoint.argtypes = [wintypes.POINT]
u32.WindowFromPoint.restype = wintypes.HWND
u32.GetAncestor.argtypes = [wintypes.HWND, ctypes.c_uint]
u32.GetAncestor.restype = wintypes.HWND
u32.EnumWindows.argtypes = [ctypes.c_void_p, wintypes.LPARAM]
u32.GetForegroundWindow.restype = wintypes.HWND

GWL_STYLE, GWL_EXSTYLE = -16, -20
WS_VISIBLE, WS_DISABLED = 0x10000000, 0x08000000
GW_CHILD, GW_HWNDNEXT = 5, 2
GA_ROOT = 2
EnumProc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)


def cls(h):
    b = ctypes.create_unicode_buffer(256)
    u32.GetClassNameW(h, b, 256)
    return b.value


def rect(h):
    r = wintypes.RECT()
    if not u32.GetWindowRect(h, ctypes.byref(r)):
        return None
    return r.left, r.top, r.right - r.left, r.bottom - r.top


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


def ribbon_dock(cab):
    """顶部那条 Ribbon（横向、可见优先）。"""
    best = None
    for k in descendants(cab):
        if cls(k) != "UIRibbonCommandBarDock":
            continue
        r = rect(k)
        if not r or r[2] <= 100:
            continue
        vis = bool(u32.GetWindowLongW(k, GWL_STYLE) & WS_VISIBLE)
        if best is None or (r[3], vis) > (best[1][3], best[2]):
            best = (k, r, vis)
    return best


def hit(x, y):
    pt = wintypes.POINT(x, y)
    h = u32.WindowFromPoint(pt)
    if not h:
        return h, None, None
    return h, cls(h), u32.GetAncestor(h, GA_ROOT)


def report(cab):
    r = rect(cab)
    par = u32.GetParent(cab) or 0
    kind = "原生" if par == 0 else "内嵌"
    fg = u32.GetForegroundWindow()
    line = "  [%s] cab=0x%X pid=%d rect=%s  前台=%s" % (
        kind, cab, pid_of(cab), r, "本窗口" if fg == cab else "否")
    d = ribbon_dock(cab)
    if not d:
        print(line + "  Ribbon=没有")
        return
    k, kr, vis = d
    st = u32.GetWindowLongW(k, GWL_STYLE)
    ex = u32.GetWindowLongW(k, GWL_EXSTYLE)
    print(line)
    print("      Ribbon=0x%X rect=%s vis=%s enabled=%s style=0x%08X ex=0x%08X" % (
        k, kr, vis, bool(u32.IsWindowEnabled(k)), st, ex))

    # 在「文件」那块和「查看」那块各取一点，看落点归谁
    for name, x in (("文件", kr[0] + 12), ("计算机", kr[0] + 70), ("查看", kr[0] + 140)):
        if x >= kr[0] + kr[2]:
            continue
        h, c, root = hit(x, kr[1] + max(4, kr[3] // 2))
        owner = "explorer(它自己)" if h and pid_of(h) == pid_of(cab) else (
            "别的进程 pid=%d" % pid_of(h) if h else "无")
        same_root = "根=cab" if root == cab else ("根=0x%X" % root if root else "根=无")
        print("      点「%s」(%d,%d) -> 0x%X %s  %s  %s" % (
            name, x, kr[1] + max(4, kr[3] // 2), h or 0, c, owner, same_root))


def main():
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
