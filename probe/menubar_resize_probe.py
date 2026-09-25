# -*- coding: utf-8 -*-
"""
只读+一次微调探针：验证「窗口缩放（explorer 重新排版）之后，那条菜单栏白条会不会回来」。

做法：把我们内嵌的 cab 临时放大 8px（它是我们面板的子窗口，超出的部分被面板裁掉，看不出来），
这会触发 explorer 的 WM_SIZE 重排；量一遍；再改回原尺寸，再量一遍。

⚠ 不碰原生窗口、不动鼠标、不抢前台。
用法：python probe/menubar_resize_probe.py
"""
import ctypes
import sys
import time
from ctypes import wintypes

u32 = ctypes.windll.user32
k32 = ctypes.windll.kernel32

for f, a in [
    (u32.GetClassNameW, [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]),
    (u32.GetWindowTextW, [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]),
    (u32.GetWindowRect, [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]),
    (u32.GetWindowThreadProcessId, [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]),
    (u32.GetWindow, [wintypes.HWND, ctypes.c_uint]),
    (u32.IsWindowVisible, [wintypes.HWND]),
]:
    f.argtypes = a
u32.GetWindow.restype = wintypes.HWND
u32.IsWindowVisible.restype = wintypes.BOOL
u32.SetWindowPos.argtypes = [wintypes.HWND, wintypes.HWND, ctypes.c_int, ctypes.c_int,
                             ctypes.c_int, ctypes.c_int, ctypes.c_uint]

GW_CHILD, GW_HWNDNEXT = 5, 2
SWP_NOZORDER, SWP_NOACTIVATE = 0x0004, 0x0010


def cls(h):
    b = ctypes.create_unicode_buffer(256)
    u32.GetClassNameW(h, b, 256)
    return b.value


def rect(h):
    r = wintypes.RECT()
    u32.GetWindowRect(h, ctypes.byref(r))
    return r


def child(h):
    return u32.GetWindow(h, GW_CHILD)


def nextsib(h):
    return u32.GetWindow(h, GW_HWNDNEXT)


def descendants(h, limit=6000):
    out, stack = [], [child(h)]
    while stack:
        c = stack.pop()
        if not c or len(out) > limit:
            continue
        out.append(c)
        stack.append(nextsib(c))
        stack.append(child(c))
    return out


def dump(tag, cab, top):
    st = u32.FindWindowExW(cab, 0, 'ShellTabWindowClass', None)
    print('  --- %s ---' % tag)
    if not st:
        print('    没有 ShellTabWindowClass')
        return
    r = rect(st)
    print('    ShellTabWindowClass y=%d h=%d' % (r.top - top, r.bottom - r.top))
    c = child(st)
    while c:
        r = rect(c)
        print('      %-22s y=%-5d h=%-5d vis=%d' %
              (cls(c)[:22], r.top - top, r.bottom - r.top, u32.IsWindowVisible(c)))
        c = nextsib(c)


def main():
    tops = []
    EnumProc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    u32.EnumWindows(EnumProc(lambda h, l: (tops.append(h), True)[1]), 0)

    cab = None
    for t in tops:
        for d in [t] + descendants(t):
            if cls(d) in ('CabinetWClass', 'ExploreWClass'):
                cab = d
                break
        if cab:
            break
    if not cab:
        print('没找到我们内嵌的 CabinetWClass')
        return 1

    r = rect(cab)
    x, y, w, h = r.left, r.top, r.right - r.left, r.bottom - r.top
    print('内嵌 cab 0x%X (%d,%d) %dx%d' % (cab, x, y, w, h))
    dump('缩放前', cab, y)

    u32.SetWindowPos(cab, None, x, y, w + 8, h + 8, SWP_NOZORDER | SWP_NOACTIVATE)
    time.sleep(0.5)
    dump('放大 8px 后（.5s）', cab, y)

    time.sleep(1.0)
    dump('放大 8px 后（1.5s）', cab, y)

    u32.SetWindowPos(cab, None, x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE)
    time.sleep(0.5)
    dump('改回原尺寸后（.5s）', cab, y)
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
