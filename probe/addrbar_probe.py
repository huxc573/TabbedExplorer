# -*- coding: utf-8 -*-
"""读每个内嵌 cab 的地址栏文本（只读，不发按键）。用来确认 MTP 位置被表示成什么字符串。"""
import ctypes, time
from ctypes import wintypes

u = ctypes.windll.user32
u.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
u.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
u.GetWindow.argtypes = [wintypes.HWND, ctypes.c_uint]; u.GetWindow.restype = wintypes.HWND
u.GetParent.argtypes = [wintypes.HWND]; u.GetParent.restype = wintypes.HWND
u.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]

def cls(h):
    b = ctypes.create_unicode_buffer(256); u.GetClassNameW(h, b, 256); return b.value
def txt(h):
    b = ctypes.create_unicode_buffer(512); u.GetWindowTextW(h, b, 512); return b.value
def pid(h):
    p = wintypes.DWORD(); u.GetWindowThreadProcessId(h, ctypes.byref(p)); return p.value
def kids(h):
    o, c = [], u.GetWindow(h, 5)
    while c:
        o.append(c); c = u.GetWindow(c, 2)
    return o
def desc(h, d=10):
    if d <= 0: return []
    o = []
    for c in kids(h):
        o.append(c); o.extend(desc(c, d - 1))
    return o

# WM_GETTEXT 对跨进程是安全的（OS 自己封送）
u.SendMessageTimeoutW.argtypes = [wintypes.HWND, ctypes.c_uint, ctypes.c_void_p,
                                  wintypes.LPVOID, ctypes.c_uint, ctypes.c_uint,
                                  ctypes.POINTER(ctypes.c_size_t)]
u.SendMessageTimeoutW.restype = wintypes.LPARAM

def wtext(h, maxlen=2048):
    n = u.SendMessageTimeoutW(h, 14, None, None, 0x0002, 400, None)  # WM_GETTEXTLENGTH
    n = int(n) if n else 0
    if n <= 0 or n > 4096:
        return ""
    buf = ctypes.create_unicode_buffer(n + 2)
    r = ctypes.c_size_t()
    u.SendMessageTimeoutW(h, 0x000D, n + 1, ctypes.cast(buf, wintypes.LPVOID), 0x0002, 400,
                          ctypes.byref(r))
    return buf.value

tops = []
E = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
u.EnumWindows.argtypes = [ctypes.c_void_p, wintypes.LPARAM]
u.EnumWindows(E(lambda h, _: (tops.append(h), True)[1]), 0)

cabs = []
for t in tops:
    if cls(t) == "CabinetWClass": cabs.append(t)
    cabs.extend(x for x in desc(t, 10) if cls(x) == "CabinetWClass")

print("cab 共 %d（内嵌 %d）" % (len(cabs), sum(1 for c in cabs if u.GetParent(c))))
KEEP = ("ToolbarWindow32", "Edit", "ComboBoxEx32", "ComboBox", "NetUIHWND",
        "DirectUIHWND", "SHELLDLL_DefView", "SysTreeView32", "msvb_lib_toolbar")
for c in cabs:
    if u.GetParent(c) == 0: continue
    print("=== cab=0x%X pid=%-6d 标题=%r" % (c, pid(c), txt(c)))
    seen = 0
    for x in desc(c):
        kc = cls(x)
        if kc in KEEP:
            t = wtext(x)
            if t.strip():
                print("     %-18s 0x%-9X vis=%-5s %r" % (
                    kc, x, bool(u.IsWindowVisible(x)), t[:200]))
                seen += 1
        if seen > 12: break
