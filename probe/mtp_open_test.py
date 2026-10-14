# -*- coding: utf-8 -*-
"""
验证 explorer.exe 吃不吃 MTP 那种「此电脑\设备名\...」串（只读探测，最小化）。
做法：起一个 explorer 进程、约 1.2 秒后读它的地址栏/标题，再关掉它。
⚠ 这会短暂开出一个资源管理器窗口 —— 只在得到用户同意后跑。
"""
import ctypes, subprocess, sys, time, os

TARGET = sys.argv[1] if len(sys.argv) > 1 else r"此电脑\motorola edge 20 pro\内部共享存储空间"
u = ctypes.windll.user32
u.GetClassNameW.argtypes = [ctypes.c_int if False else ctypes.c_void_p, ctypes.c_wchar_p, ctypes.c_int]
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

def snap(pids):
    tops = []
    E = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)
    u.EnumWindows.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
    u.EnumWindows(E(lambda h, _: (tops.append(h), True)[1]), None)
    res = []
    for t in tops:
        if pid(t) not in pids: continue
        if cls(t) != "CabinetWClass": continue
        band = ""
        for x in desc(t):
            if cls(x) == "ToolbarWindow32":
                v = wtext(x)
                if ": " in v:
                    band = v; break
        res.append((t, ttl(t), band))
    return res

before = set(p.pid for p in
             [__import__("subprocess").Popen("echo", shell=True)])  # 占位
# 记录已存在的 explorer 进程
def expl_pids():
    out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq explorer.exe", "/FO", "CSV", "/NH"],
                         capture_output=True, text=True, errors="replace").stdout
    s = set()
    for line in out.splitlines():
        parts = [x.strip('"') for x in line.split('","')]
        if len(parts) > 1 and parts[1].isdigit():
            s.add(int(parts[1]))
    return s

old = expl_pids()
print("现有 explorer 进程:", sorted(old))
print("测试目标: %r" % TARGET)
print("启动 explorer.exe /n,%s ..." % TARGET)
proc = subprocess.Popen(["explorer.exe", "/n," + TARGET])
time.sleep(1.6)

new = expl_pids() - old
print("新起的 explorer pid:", sorted(new))
found = snap(new) if new else []
for h, t, band in found:
    print("  新窗口 hwnd=0x%X 标题=%r 地址栏=%r" % (h, t, band))
if not found:
    print("  ⚠ 没有新窗口 —— 说明 explorer 没认这个串（或它复用/忽略了）")

# 收尾：关掉我们起的那些窗口（不急，先打印）
print()
print("（如出现新窗口，下面逐一向它发 WM_CLOSE）")
for h, t, band in found:
    print("  关闭 0x%X %r" % (h, t))
    u.PostMessageW = u.PostMessageW
    ctypes.windll.user32.PostMessageW.argtypes = [ctypes.c_void_p, ctypes.c_uint,
                                                  ctypes.c_void_p, ctypes.c_void_p]
    ctypes.windll.user32.PostMessageW(h, 0x0010, None, None)   # WM_CLOSE
