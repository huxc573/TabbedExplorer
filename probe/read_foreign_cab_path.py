# -*- coding: utf-8 -*-
"""只读探针：从一个**不属于我们**的资源管理器窗口上读出它当前打开的文件夹。

用途：判断「接管 shell 打开的文件夹」这条路走不走得通 —— 要是能在不碰窗口对象的前提下把
真实路径读出来，就能走「关掉 shell 那个窗口 + 用自己的 explorer /n,/separate 开成标签」。
手法跟程序里 `EmbedApi.FindAddressBand` 一样：`Breadcrumb Parent` → `ToolbarWindow32`
（地址栏那一条的窗口文本是「地址: <当前地址>」，同一条 rebar 上另外两条没有冒号）。

不动鼠标、不抢前台。用法：python read_foreign_cab_path.py [可选：hwnd 十六进制]
"""
import ctypes
import sys
from ctypes import wintypes

try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    pass

u = ctypes.windll.user32
k = ctypes.windll.kernel32
u.EnumWindows.argtypes = [ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM),
                          wintypes.LPARAM]
u.EnumChildWindows.argtypes = [wintypes.HWND,
                               ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM),
                               wintypes.LPARAM]
u.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
u.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
u.GetWindowTextLengthW.argtypes = [wintypes.HWND]
u.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
k.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
k.QueryFullProcessImageNameW.argtypes = [wintypes.HANDLE, wintypes.DWORD,
                                         wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)]

CAB = ("CabinetWClass", "ExploreWClass")


def cls(h):
    b = ctypes.create_unicode_buffer(256)
    u.GetClassNameW(h, b, 256)
    return b.value


def text(h):
    n = u.GetWindowTextLengthW(h) + 1
    b = ctypes.create_unicode_buffer(n)
    u.GetWindowTextW(h, b, n)
    return b.value


def pid_of(h):
    p = wintypes.DWORD()
    u.GetWindowThreadProcessId(h, ctypes.byref(p))
    return p.value


def exe_of(pid):
    hp = k.OpenProcess(0x1000, False, pid)
    if not hp:
        return "?"
    try:
        buf = ctypes.create_unicode_buffer(512)
        n = wintypes.DWORD(512)
        return buf.value if k.QueryFullProcessImageNameW(hp, 0, buf, ctypes.byref(n)) else "?"
    finally:
        k.CloseHandle(hp)


def children(h):
    got = []

    def cb(c, lp):
        got.append(c)
        return True

    u.EnumChildWindows(h, ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)(cb), 0)
    return got


def all_tops():
    got = []

    def cb(h, lp):
        got.append(h)
        return True

    u.EnumWindows(ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)(cb), 0)
    return got


def read_path(cab):
    """照 EmbedApi.FindAddressBand 的路子读地址栏文本；返回 (原始toolbar文本, 剥掉前缀的路径)。"""
    band = None
    for c in children(cab):
        if cls(c) == "Breadcrumb Parent":
            for t in children(c):
                if cls(t) == "ToolbarWindow32":
                    band = t
                    break
    if band is None:
        for c in children(cab):
            if cls(c) == "ToolbarWindow32" and ": " in text(c):
                band = c
                break
    if band is None:
        return None, None
    raw = text(band)
    _, _, rest = raw.partition(": ")          # 「地址: <当前地址>」——前缀跟系统语言有关，不写死
    return raw, rest.strip()


def main():
    want = int(sys.argv[1], 16) if len(sys.argv) > 1 else None
    cabs = [h for h in all_tops() if cls(h) in CAB]
    if want:
        cabs = [h for h in cabs if h == want] or [want]
    print("顶层浏览窗口 %d 个" % len(cabs))
    for h in cabs:
        pid = pid_of(h)
        raw, path = read_path(h)
        print("\nhwnd=0x%08X  类=%s  可见=%d  pid=%d  %s" % (
            h, cls(h), 1 if u.IsWindowVisible(h) else 0, pid, exe_of(pid)))
        print("   标题   : %r" % text(h))
        print("   地址栏 : %r" % raw)
        print("   路径   : %r  %s" % (path, "← 是个真目录" if path and __import__("os").path.isdir(path) else ""))


if __name__ == "__main__":
    main()
