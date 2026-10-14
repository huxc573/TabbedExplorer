# -*- coding: utf-8 -*-
"""
探针（会改一点状态，但不弹窗 / 不动光标 / 不抢前台）：

内嵌窗口顶上那条 Ribbon（「文件 计算机 查看」）到底还认不认鼠标？
办法：往里那条 `UIRibbonCommandBarDock` 的「查看」那块 PostMessage 一组
WM_LBUTTONDOWN/UP，然后量 Ribbon 的高度有没有从 25px（收起）涨上去（展开）。

  · 高度涨了  ⇒ explorer 的 Ribbon 在子窗口里照样收得到鼠标 ⇒ 问题在别处。
  · 高度没变  ⇒ 子窗口状态下 UIRibbon 确实不响应 ⇒ 就是架构限制。

⚠ 只点「查看」（展开/收起功能区，无弹出窗）；**绝不点「文件」**（那是 backstage，
  会弹一块大面板）。全程 PostMessage（异步、无指针参数），不发前台、不 ShowWindow。
用法：python probe/ribbon_click_probe.py
"""
import ctypes
import time
from ctypes import wintypes

u32 = ctypes.windll.user32

u32.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
u32.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
u32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
u32.GetWindow.argtypes = [wintypes.HWND, ctypes.c_uint]
u32.GetWindow.restype = wintypes.HWND
u32.GetParent.argtypes = [wintypes.HWND]
u32.GetParent.restype = wintypes.HWND
u32.GetWindowLongW.argtypes = [wintypes.HWND, ctypes.c_int]
u32.GetWindowLongW.restype = ctypes.c_uint
u32.EnumWindows.argtypes = [ctypes.c_void_p, wintypes.LPARAM]
u32.PostMessageW.argtypes = [wintypes.HWND, ctypes.c_uint, wintypes.WPARAM, wintypes.LPARAM]
u32.PostMessageW.restype = wintypes.BOOL

GWL_STYLE = -16
WS_VISIBLE = 0x10000000
GW_CHILD, GW_HWNDNEXT = 5, 2
EnumProc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

WM_MOUSEMOVE, WM_LBUTTONDOWN, WM_LBUTTONUP = 0x0200, 0x0201, 0x0202


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


def ribbon_dock(cab):
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


def click(h, cx, cy):
    lp = (cy << 16) | (cx & 0xFFFF)
    u32.PostMessageW(h, WM_MOUSEMOVE, 0, lp)
    time.sleep(0.05)
    u32.PostMessageW(h, WM_LBUTTONDOWN, 1, lp)
    time.sleep(0.05)
    u32.PostMessageW(h, WM_LBUTTONUP, 0, lp)


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

    embedded = cabs          # 内嵌 + 原生一起测（原生那个当对照）
    n = 0
    for cab in embedded:
        d = ribbon_dock(cab)
        if not d:
            continue
        k, kr, _ = d
        if kr[3] < 20:              # 还没定型的备用窗，跳过
            continue
        before = rect(k)[3]
        cr = rect(cab)
        kind = "内嵌" if (u32.GetParent(cab) or 0) != 0 else "原生"
        print("[%s] cab=0x%X 窗口=%s  Ribbon 高=%dpx" % (kind, cab, cr, before))
        # 「查看」在 Ribbon 左端起 ~140px（跟原生窗口对过表）；点 tab 行中间
        click(k, 140, max(4, before // 2))
        time.sleep(0.5)
        after = rect(k)[3]
        print("    点「查看」之后 Ribbon 高=%dpx  => %s" % (
            after, "有反应（展开了）" if after != before else "没反应"))
        n += 1
        if n >= 6:
            break
    if n == 0:
        print("没找到可测的内嵌 Ribbon（窗口可能全在休眠/屏幕外）。")


if __name__ == "__main__":
    main()
