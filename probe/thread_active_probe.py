# -*- coding: utf-8 -*-
"""
只读探针：看每扇（内嵌 / 原生）CabinetWClass 所在线程的「激活/焦点」状态。

想回答：内嵌窗口顶上那条 Ribbon 点不动，是不是因为
  「收编成 WS_CHILD 之后，explorer 那个线程从来没有『活动窗口』」——
  即 ribbon（UIRibbon 框架）认为宿主窗口不是当前活动窗、于是不接命令。

判据用 `GetGUIThreadInfo`（**纯读，不发任何消息**）：
  hwndActive = 该线程当前的活动窗口
  hwndFocus  = 该线程键盘焦点落在哪个窗口
  hwndCaret  = 光标（有编辑框时才有）

对照：原生 explorer 窗口（ribbon 一定能用）那行的 hwndActive 应当就是它自己。

用法：python probe/thread_active_probe.py
"""
import ctypes
from ctypes import wintypes

u32 = ctypes.windll.user32


class GUITHREADINFO(ctypes.Structure):
    _fields_ = [("cbSize", wintypes.DWORD),
                ("flags", wintypes.DWORD),
                ("hwndActive", wintypes.HWND),
                ("hwndFocus", wintypes.HWND),
                ("hwndCapture", wintypes.HWND),
                ("hwndMenuOwner", wintypes.HWND),
                ("hwndMoveSize", wintypes.HWND),
                ("hwndCaret", wintypes.HWND),
                ("rcCaret", wintypes.RECT)]


u32.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
u32.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
u32.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
u32.GetParent.argtypes = [wintypes.HWND]
u32.GetParent.restype = wintypes.HWND
u32.GetWindow.argtypes = [wintypes.HWND, ctypes.c_uint]
u32.GetWindow.restype = wintypes.HWND
u32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
u32.GetGUITThreadInfo = None
u32.GetGUIThreadInfo.argtypes = [wintypes.DWORD, ctypes.POINTER(GUITHREADINFO)]
u32.GetGUIThreadInfo.restype = wintypes.BOOL
u32.EnumWindows.argtypes = [ctypes.c_void_p, wintypes.LPARAM]
u32.GetWindowLongW.argtypes = [wintypes.HWND, ctypes.c_int]
u32.GetWindowLongW.restype = ctypes.c_uint
u32.GetForegroundWindow.restype = wintypes.HWND
u32.GetWindowThreadProcessId.restype = wintypes.DWORD

GW_CHILD, GW_HWNDNEXT = 5, 2
GWL_STYLE = -16
WS_VISIBLE = 0x10000000
EnumProc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)


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


def children(h):
    out, c = [], u32.GetWindow(h, GW_CHILD)
    while c:
        out.append(c)
        c = u32.GetWindow(c, GW_HWNDNEXT)
    return out


def descendants(h, depth=10):
    if depth <= 0:
        return []
    out = []
    for c in children(h):
        out.append(c)
        out.extend(descendants(c, depth - 1))
    return out


def label(h):
    if not h:
        return "(空)"
    return "%s 0x%X %r" % (cls(h), h, txt(h)[:24])


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

    fg = u32.GetForegroundWindow()
    print("前台窗口 = %s" % label(fg))
    print("共 %d 个 CabinetWClass：" % len(cabs))
    for c in cabs:
        par = u32.GetParent(c) or 0
        tid = u32.GetWindowThreadProcessId(c, None)
        g = GUITHREADINFO()
        g.cbSize = ctypes.sizeof(GUITHREADINFO)
        ok = u32.GetGUIThreadInfo(tid, ctypes.byref(g))
        print("\n[%s] cab=0x%X pid=%d tid=%d rect=%s %r" % (
            "原生" if par == 0 else "内嵌", c, pid(c), tid, rect(c), txt(c)[:24]))
        if not ok:
            print("     GetGUIThreadInfo 失败")
            continue
        mark = lambda h: "  <= 本 cab" if h == c else ""
        print("     活动 hwndActive = %s%s" % (label(g.hwndActive), mark(g.hwndActive)))
        print("     焦点 hwndFocus  = %s%s" % (label(g.hwndFocus), mark(g.hwndFocus)))
        # 焦点/活动窗口是不是落在本 cab 的子树里
        sub = set(descendants(c, 10)) | {c}
        print("     活动在子树内 = %s ；焦点在子树内 = %s" % (
            g.hwndActive in sub, g.hwndFocus in sub))


if __name__ == "__main__":
    main()
