# -*- coding: utf-8 -*-
"""更宽的检测：不起子进程扫描、直接枚举所有顶层窗，按 pid 归属数窗口。"""
import ctypes, subprocess, time

u = ctypes.windll.user32
u.GetClassNameW.argtypes = [ctypes.c_void_p, ctypes.c_wchar_p, ctypes.c_int]
u.GetWindowTextW.argtypes = [ctypes.c_void_p, ctypes.c_wchar_p, ctypes.c_int]
u.GetWindow.argtypes = [ctypes.c_void_p, ctypes.c_uint]; u.GetWindow.restype = ctypes.c_void_p
u.GetWindowThreadProcessId.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_ulong)]
u.IsWindowVisible.argtypes = [ctypes.c_void_p]
u.GetWindowRect.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_long * 4)]
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
    n = u.SendMessageTimeoutW(h, 14, None, None, 0x0002, 800, None)
    n = int(n) if n else 0
    if n <= 0 or n > 4096: return ""
    buf = ctypes.create_unicode_buffer(n + 2)
    u.SendMessageTimeoutW(h, 0x000D, ctypes.c_void_p(n + 1),
                          ctypes.cast(buf, ctypes.c_void_p), 0x0002, 800, None)
    return buf.value

def all_tops():
    tops = []
    E = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)
    u.EnumWindows.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
    u.EnumWindows(E(lambda h, _: (tops.append(h), True)[1]), None)
    return tops

def pick(cab):
    band = ""
    for x in desc(cab):
        if cls(x) == "ToolbarWindow32":
            v = wtext(x)
            if ": " in v:
                band = v; break
    return band

def expl_pids():
    out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq explorer.exe", "/FO", "CSV", "/NH"],
                         capture_output=True, text=True, errors="replace").stdout
    s = set()
    for line in out.splitlines():
        p = [x.strip('"') for x in line.split('","')]
        if len(p) > 1 and p[1].isdigit(): s.add(int(p[1]))
    return s

CANDS = [
    ("::此电脑GUID（对照组，必成功）", r"::{20D04FE0-3AEA-1069-A2D8-08002B30309D}"),
    ("原样（此电脑\\…）",             r"此电脑\motorola edge 20 pro\内部共享存储空间"),
    ("此电脑GUID 前缀 + 设备名",       r"::{20D04FE0-3AEA-1069-A2D8-08002B30309D}\motorola edge 20 pro"),
    ("shell:::{此电脑GUID}\\设备名",   r"shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}\motorola edge 20 pro"),
]

for label, target in CANDS:
    old = expl_pids()
    subprocess.Popen(["explorer.exe", "/n," + target], creationflags=0x08000000)
    time.sleep(3.0)
    new = expl_pids() - old
    rows = []
    for t in all_tops():
        if pid(t) in new:
            rows.append((t, cls(t), ttl(t), bool(u.IsWindowVisible(t)),
                         pick(t) if cls(t) == "CabinetWClass" else ""))
    print("=" * 74)
    print("[%s]  %r" % (label, target))
    print("   新 pid=%s  新顶层窗 %d 个" % (sorted(new), len(rows)))
    for h, c, t, vis, band in rows:
        print("     0x%-8X %-22s vis=%-5s %r  地址栏=%r" % (h, c, vis, t[:40], band))
    for h, c, _, _, _ in rows:
        ctypes.windll.user32.PostMessageW.argtypes = [ctypes.c_void_p, ctypes.c_uint,
                                                      ctypes.c_void_p, ctypes.c_void_p]
        ctypes.windll.user32.PostMessageW(h, 0x0010, None, None)
    time.sleep(0.6)
