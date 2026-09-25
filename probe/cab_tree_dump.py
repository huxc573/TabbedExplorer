# -*- coding: utf-8 -*-
"""
只读探针：把每个 CabinetWClass（我们的内嵌窗口 + 原生窗口）的**全部后代**按层级打出来
（类名 + 矩形 + 可见性 + 文本），用来定位「ReBar 下面那 20px 的白条到底是什么窗口」。

全程只读：不发鼠标键盘消息、不 ShowWindow、不抢前台、不动光标。
唯一发消息的地方是 SendMessageTimeoutW(WM_GETTEXT)，300ms 超时 + SMTO_ABORTIFHUNG。

用法：python probe/cab_tree_dump.py [--depth 8]
"""
import ctypes
import sys
from ctypes import wintypes

u32 = ctypes.windll.user32
k32 = ctypes.windll.kernel32

u32.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
u32.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
u32.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
u32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
u32.GetWindow.argtypes = [wintypes.HWND, ctypes.c_uint]
u32.GetWindow.restype = wintypes.HWND
u32.IsWindowVisible.argtypes = [wintypes.HWND]
u32.IsWindowVisible.restype = wintypes.BOOL
u32.GetWindowLongW.argtypes = [wintypes.HWND, ctypes.c_int]
u32.GetWindowLongW.restype = ctypes.c_uint
u32.SendMessageTimeoutW.argtypes = [wintypes.HWND, ctypes.c_uint, ctypes.c_size_t,
                                    ctypes.c_ssize_t, ctypes.c_uint, ctypes.c_uint,
                                    ctypes.POINTER(ctypes.c_size_t)]
u32.GetMenu.argtypes = [wintypes.HWND]
u32.GetMenu.restype = wintypes.HMENU

GW_CHILD, GW_HWNDNEXT = 5, 2
GWL_STYLE, GWL_EXSTYLE = -16, -20
WM_GETTEXT = 0x000D
SMTO_ABORTIFHUNG = 0x0002


def cls(h):
    b = ctypes.create_unicode_buffer(256)
    u32.GetClassNameW(h, b, 256)
    return b.value


def txt_any(h):
    b = ctypes.create_unicode_buffer(512)
    got = ctypes.c_size_t(0)
    u32.SendMessageTimeoutW(h, WM_GETTEXT, 512, ctypes.cast(b, ctypes.c_void_p).value or 0,
                            SMTO_ABORTIFHUNG, 300, ctypes.byref(got))
    return b.value


def rect(h):
    r = wintypes.RECT()
    u32.GetWindowRect(h, ctypes.byref(r))
    return r


def child(h):
    return u32.GetWindow(h, GW_CHILD)


def nextsib(h):
    return u32.GetWindow(h, GW_HWNDNEXT)


def walk(h, depth, maxdepth, top):
    c = child(h)
    while c:
        r = rect(c)
        print('  ' * depth + '%-34s 0x%-8X y=%-5d h=%-5d w=%-5d vis=%d style=0x%08X %r'
              % (cls(c)[:34], c, r.top - top, r.bottom - r.top, r.right - r.left,
                 u32.IsWindowVisible(c), u32.GetWindowLongW(c, GWL_STYLE), txt_any(c)[:40]))
        if depth < maxdepth:
            walk(c, depth + 1, maxdepth, top)
        c = nextsib(c)


def main():
    maxdepth = 6
    args = sys.argv[1:]
    for i, a in enumerate(args):
        if a == '--depth' and i + 1 < len(args):
            maxdepth = int(args[i + 1])

    tops = []
    EnumProc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

    def on(h, _):
        tops.append(h)
        return True

    u32.EnumWindows(EnumProc(on), 0)

    def all_desc(h, limit=6000):
        out, stack = [], [child(h)]
        while stack:
            c = stack.pop()
            if not c or len(out) > limit:
                continue
            out.append(c)
            stack.append(nextsib(c))
            stack.append(child(c))
        return out

    n = 0
    for t in tops:
        if not u32.IsWindowVisible(t):
            continue
        cabs = [t] if cls(t) in ('CabinetWClass', 'ExploreWClass') else [
            d for d in all_desc(t) if cls(d) in ('CabinetWClass', 'ExploreWClass')]
        for cab in cabs:
            n += 1
            r = rect(cab)
            pid = wintypes.DWORD()
            u32.GetWindowThreadProcessId(cab, ctypes.byref(pid))
            print('')
            print('=== Cab 0x%X pid=%d rect=%s 菜单句柄=0x%X 父=0x%X'
                  % (cab, pid.value, (r.left, r.top, r.right - r.left, r.bottom - r.top),
                     int(u32.GetMenu(cab) or 0), int(u32.GetParent(cab) or 0)))
            walk(cab, 1, maxdepth, r.top)
    print('')
    print('共 %d 个 CabinetWClass' % n)
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
