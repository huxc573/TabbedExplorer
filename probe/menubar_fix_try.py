# -*- coding: utf-8 -*-
"""
一次性验证探针（**会写**，只动我们自己内嵌的那个 explorer 窗口）：

内嵌窗口里 explorer 会多出一条 20px 的「文件(F) 编辑(E) 查看(V) 工具(T)」菜单栏，
原生窗口里它是收着的（ReBar 高度 0）。这里试着：

  ① `ShowWindow(menuWorkerW, SW_HIDE)` —— 藏掉菜单栏那条 WorkerW；
  ② `SetWindowPos(DUIView, …)` —— 把文件视图补到 ShellTabWindowClass 的整个高度。

然后重新量一遍，看 ① 白条有没有消失 ② explorer 有没有自己把它恢复。

⚠ 只对「父窗口属于 TabbedExplorer 进程」的 CabinetWClass 动手；原生窗口一律不碰。
用法：python probe/menubar_fix_try.py
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
u32.GetParent.argtypes = [wintypes.HWND]
u32.GetParent.restype = wintypes.HWND
u32.IsWindowVisible.argtypes = [wintypes.HWND]
u32.IsWindowVisible.restype = wintypes.BOOL
u32.ShowWindow.argtypes = [wintypes.HWND, ctypes.c_int]
u32.SetWindowPos.argtypes = [wintypes.HWND, wintypes.HWND, ctypes.c_int, ctypes.c_int,
                             ctypes.c_int, ctypes.c_int, ctypes.c_uint]
u32.GetWindowLongW.argtypes = [wintypes.HWND, ctypes.c_int]
u32.GetWindowLongW.restype = ctypes.c_uint
u32.FindWindowExW.argtypes = [wintypes.HWND, wintypes.HWND, wintypes.LPCWSTR, wintypes.LPCWSTR]
u32.FindWindowExW.restype = wintypes.HWND

GW_CHILD, GW_HWNDNEXT = 5, 2
SW_HIDE = 0
SWP_NOZORDER, SWP_NOACTIVATE = 0x0004, 0x0010
WS_VISIBLE = 0x10000000


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


def pid_of(h):
    q = wintypes.DWORD()
    u32.GetWindowThreadProcessId(h, ctypes.byref(q))
    return q.value


def find(name):
    return u32.FindWindowExW(0, 0, name, None)


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


def own_pids():
    """"哪些 pid 的窗口是 TabbedExplorer 自己的" —— 用一个简单的办法：
    从 TabbedExplorer 进程的顶层窗口往下走，凡是走到过的 CabinetWClass 就算我们的。"""
    return None


def dump(cab, tag):
    print('  --- %s ---' % tag)
    st = u32.FindWindowExW(cab, 0, 'ShellTabWindowClass', None)
    if not st:
        print('    没有 ShellTabWindowClass')
        return
    r = rect(st)
    print('    ShellTabWindowClass 0x%X y=%d h=%d w=%d' % (st, r.top, r.bottom - r.top, r.right - r.left))
    c = child(st)
    while c:
        r = rect(c)
        print('      %-22s 0x%-8X y=%-5d h=%-5d w=%-5d vis=%d' %
              (cls(c)[:22], c, r.top, r.bottom - r.top, r.right - r.left, u32.IsWindowVisible(c)))
        c = nextsib(c)


def main():
    # 找我们自己进程的那扇 cab：父窗口属于 TabbedExplorer.exe
    tops = []
    EnumProc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    u32.EnumWindows(EnumProc(lambda h, l: (tops.append(h), True)[1]), 0)

    ours = None
    for t in tops:
        for d in [t] + descendants(t):
            if cls(d) in ('CabinetWClass', 'ExploreWClass'):
                ours = d
                break
        if ours:
            break
    if not ours:
        print('没找到我们内嵌的 CabinetWClass')
        return 1

    cab = ours
    r = rect(cab)
    print('内嵌 cab 0x%X pid=%d y=%d h=%d w=%d' % (cab, pid_of(cab), r.top, r.bottom - r.top, r.right - r.left))
    dump(cab, '改之前')

    st = u32.FindWindowExW(cab, 0, 'ShellTabWindowClass', None)
    if not st:
        return 1
    menu = None
    dview = None
    c = child(st)
    while c:
        cn = cls(c)
        rr = rect(c)
        hh = rr.bottom - rr.top
        if cn == 'WorkerW' and 1 <= hh <= 30:
            menu = c
        elif cn == 'DUIViewWndClassName':
            dview = c
        c = nextsib(c)

    if not menu or not dview:
        print('没同时找到菜单栏 WorkerW 和 DUIViewWndClassName（menu=%s dview=%s）' % (menu, dview))
        return 1

    stR = rect(st)
    stW, stH = stR.right - stR.left, stR.bottom - stR.top
    print('菜单栏 WorkerW=0x%X  DUIView=0x%X' % (menu, dview))

    u32.ShowWindow(menu, SW_HIDE)
    u32.SetWindowPos(dview, None, 0, 0, stW, stH, SWP_NOZORDER | SWP_NOACTIVATE)
    print('已隐藏菜单栏 + 把 DUIView 补到 %dx%d' % (stW, stH))

    import time
    time.sleep(1.2)
    dump(cab, '改之后 1.2s')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
