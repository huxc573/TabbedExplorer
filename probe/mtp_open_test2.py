# -*- coding: utf-8 -*-
"""试多种 MTP 目标写法，看 explorer.exe 认哪一种（每种起一窗、3 秒后读、再关）。"""
import ctypes, subprocess, sys, time

u = ctypes.windll.user32
u.GetClassNameW.argtypes = [ctypes.c_void_p, ctypes.c_wchar_p, ctypes.c_int]
u.GetWindowTextW.argtypes = [ctypes.c_void_p, ctypes.c_wchar_p, ctypes.c_int]
u.GetWindow.argtypes = [ctypes.c_void_p, ctypes.c_uint]; u.GetWindow.restype = ctypes.c_void_p
u.GetWindowThreadProcessId.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_ulong)]
u.SendMessageTimeoutW.argtypes = [ctypes.c_void_p, ctypes.c_uint, ctypes.c_void_p,
                                  ctypes.c_void_p, ctypes.c_uint, ctypes.c_uint,
                                  ctypes.POINTER(ctypes.c_size_t)]
u.SendMessageTimeoutW.restype = ctypes.c_void_p

def cls(h):
    b = ctypes.create_unicode_buffer(256); u.GetClassNameW(h, b, 256); return b.value
def ttl(h):
    b = ctypes.create_unicode_buffer(512); u.GetWindowTextW(h, b, 512); return b.value
def pid(h):
    p = ctypes.c_ulong(); u.GetWindowThreadProcessId(h, ctypes.byref(p)); return p.value
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
def wtext(h):
    n = u.SendMessageTimeoutW(h, 14, None, None, 0x0002, 500, None)
    n = int(n) if n else 0
    if n <= 0 or n > 4096: return ""
    buf = ctypes.create_unicode_buffer(n + 2)
    u.SendMessageTimeoutW(h, 0x000D, ctypes.c_void_p(n + 1),
                          ctypes.cast(buf, ctypes.c_void_p), 0x0002, 500, None)
    return buf.value

def cabs_of(pids):
    tops = []
    E = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)
    u.EnumWindows.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
    u.EnumWindows(E(lambda h, _: (tops.append(h), True)[1]), None)
    out = []
    for t in tops:
        if pid(t) in pids and cls(t) == "CabinetWClass":
            band = ""
            for x in desc(t):
                if cls(x) == "ToolbarWindow32":
                    v = wtext(x)
                    if ": " in v:
                        band = v; break
            out.append((t, ttl(t), band))
    return out

def expl_pids():
    out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq explorer.exe", "/FO", "CSV", "/NH"],
                         capture_output=True, text=True, errors="replace").stdout
    s = set()
    for line in out.splitlines():
        p = [x.strip('"') for x in line.split('","')]
        if len(p) > 1 and p[1].isdigit(): s.add(int(p[1]))
    return s

CANDS = [
    ("原样（此电脑\\…）",            r"此电脑\motorola edge 20 pro\内部共享存储空间"),
    ("shell: 前缀",                  r"shell:MyComputerFolder\motorola edge 20 pro\内部共享存储空间"),
    ("此电脑 CLSID 前缀",            r"::{20D04FE0-3AEA-1069-A2D8-08002B30309D}\motorola edge 20 pro\内部共享存储空间"),
    ("shell:::{此电脑GUID}\\…",      r"shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}\motorola edge 20 pro\内部共享存储空间"),
    ("设备名单独",                   r"motorola edge 20 pro\内部共享存储空间"),
    ("ComputerFolder 计数写法",      r"::{20D04FE0-3AEA-1069-A2D8-08002B30309D}"),
]

for label, target in CANDS:
    old = expl_pids()
    subprocess.Popen(["explorer.exe", "/n," + target],
                     creationflags=0x08000000)   # CREATE_NO_WINDOW
    time.sleep(2.6)
    new = expl_pids() - old
    found = cabs_of(new) if new else []
    print("=" * 72)
    print("[%s]  %r" % (label, target))
    print("   新进程 %s  新窗口 %d" % (sorted(new), len(found)))
    for h, t, band in found:
        print("     hwnd=0x%X 标题=%r 地址栏=%r" % (h, t, band))
    for h, _, _ in found:
        ctypes.windll.user32.PostMessageW.argtypes = [ctypes.c_void_p, ctypes.c_uint,
                                                      ctypes.c_void_p, ctypes.c_void_p]
        ctypes.windll.user32.PostMessageW(h, 0x0010, None, None)
    time.sleep(0.4)
