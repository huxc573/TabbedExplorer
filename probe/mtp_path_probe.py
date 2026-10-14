# -*- coding: utf-8 -*-
"""
MTP / 便携设备路径探针（只读）。

目的：安卓手机（MTP）在资源管理器里的目标是什么形态、能不能被
  Directory.Exists / Path.IsPathRooted 认下来、能不能交给 explorer.exe 打开。
对齐源码里那两道门：
  FavStore.NormalizePath  → File.Exists || Directory.Exists   （加书签）
  PathRules.Restorable    → Path.IsPathRooted && Directory.Exists（开标签）
"""
import ctypes, os, re, subprocess, sys
from ctypes import wintypes

u = ctypes.windll.user32
u.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
u.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
u.GetWindow.argtypes = [wintypes.HWND, ctypes.c_uint]; u.GetWindow.restype = wintypes.HWND
u.GetParent.argtypes = [wintypes.HWND]; u.GetParent.restype = wintypes.HWND
u.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
u.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]

# Shell 接口（走 COM，不用 Add-Type）
ole32 = ctypes.windll.ole32
ole32.CoInitialize(None)

GUID = ctypes.c_byte * 16
class _GUID(ctypes.Structure):
    _fields_ = [("d1", ctypes.c_ulong), ("d2", ctypes.c_ushort),
                ("d3", ctypes.c_ushort), ("d4", ctypes.c_ubyte * 8)]
def guid(s):
    from uuid import UUID
    x = UUID(s)
    g = _GUID()
    g.d1, g.d2, g.d3 = x.time_low, x.time_mid, x.time_hi_version
    b = x.bytes
    for i in range(8):
        g.d4[i] = b[8 + i]
    return g

CLSID_ShellWindows = guid("{9BA05972-F6A8-11CF-A442-00A0C90A8F39}")
IID_IShellWindows    = guid("{85CB6900-4D95-11CF-960C-0080C7F4EE85}")

def cls(h):
    b = ctypes.create_unicode_buffer(256); u.GetClassNameW(h, b, 256); return b.value
def txt(h):
    b = ctypes.create_unicode_buffer(512); u.GetWindowTextW(h, b, 512); return b.value
def pid(h):
    p = wintypes.DWORD(); u.GetWindowThreadProcessId(h, ctypes.byref(p)); return p.value
def rect(h):
    r = wintypes.RECT(); u.GetWindowRect(h, ctypes.byref(r))
    return (r.left, r.top, r.right - r.left, r.bottom - r.top)
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

print("=" * 74)
print("① 枚举所有资源管理器窗口，看地址栏报的是什么")
print("=" * 74)

# 用 ShellWindows COM 拿每个窗口的 LocationName / LocationURL / Folder.Self.Path
try:
    from ctypes import POINTER, byref, c_void_p, c_long
    ole32.CoCreateInstance.argtypes = [POINTER(_GUID), c_void_p, wintypes.DWORD,
                                       POINTER(_GUID), POINTER(c_void_p)]
    p = c_void_p()
    # CLSCTX_ALL = 0x17
    hr = ole32.CoCreateInstance(byref(CLSID_ShellWindows), None, 0x17,
                               byref(IID_IShellWindows), byref(p))
    if hr != 0:
        print("CoCreateInstance(ShellWindows) 失败 hr=0x%08X" % (hr & 0xffffffff))
    else:
        print("ShellWindows 已取得 (ptr=0x%X)" % (p.value or 0))
        vt = ctypes.cast(p, POINTER(POINTER(c_void_p))).contents
        # vtable 顺序: QueryInterface, AddRef, Release, GetTypeInfoCount, GetTypeInfo,
        #               GetIDsOfNames, Invoke(7)... 用 IDispatch 太绕，改用 GetCount/_NewEnum 走 IDispatch
        # 这里只取 Count 做存在性判断
        def call(idx, *args):
            # 简易 IDispatch::Invoke 太复杂 —— 改为读 vtable 占位，仅打印
            return None
        print("（COM 深挖留给后续；下面用窗口轮询 + 树形比对）")
except Exception as ex:
    print("COM 探测失败:", ex)

# 窗口轮询：找所有 Cab 及非文件系统的 shell 视图
tops = []
E = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
u.EnumWindows.argtypes = [ctypes.c_void_p, wintypes.LPARAM]
u.EnumWindows(E(lambda h, _: (tops.append(h), True)[1]), 0)

cabs = []
for t in tops:
    if cls(t) == "CabinetWClass": cabs.append(t)
    cabs.extend(x for x in desc(t, 10) if cls(x) == "CabinetWClass")
print("CabinetWClass 共 %d 个（含内嵌）" % len(cabs))
for c in cabs[:12]:
    par = u.GetParent(c)
    print("  %s 0x%-7X pid=%-6d 父=0x%-7X rect=%-22s %r" % (
        "内嵌" if par else "原生", c, pid(c), par or 0, rect(c), txt(c)[:44]))

print()
print("=" * 74)
print("② 关键判据：拿字符串试 Directory.Exists（.NET 的判据等价物）")
print("=" * 74)

# 我们从截图读到的实际形态
cand = [
    # 屏幕上「地址栏」在 此电脑 之下显示的是「motorola edge 20 > 内部共享存储空间 > ...」
    r"::{20D04FE0-3AEA-1069-A2D8-08002B30309D}\motorola edge 20",
    r"此电脑\motorola edge 20",
    r"motorola edge 20",
    r"\\?\usb#vid_22b8&pid_2e82#zy22fvplb8#{6ac27878-a6fa-4155-ba85-f98f491d4f33}\内部共享存储空间",
    r"::{20D04FE0-3AEA-1069-A2D8-08002B30309D}\motorola edge 20\内部共享存储空间",
]
for s in cand:
    try:
        de = os.path.isdir(s)
    except Exception as ex:
        de = "EXC:%s" % ex
    try:
        fe = os.path.isfile(s)
    except Exception as ex:
        fe = "EXC:%s" % ex
    rooted = os.path.isabs(s)
    print("  %-96r  isdir=%-6s isfile=%-6s rooted=%s" % (s, de, fe, rooted))

print()
print("=" * 74)
print("③ 驱动器盘符里有没有暴露 MTP？（挂载成盘符的话就没有这条问题）")
print("=" * 74)
try:
    for d in range(ord('A'), ord('Z') + 1):
        drv = chr(d) + ":\\"
        if os.path.exists(drv):
            # DRIVE_REMOVABLE=2, DRIVE_FIXED=3, DRIVE_REMOTE=4, DRIVE_CDROM=5
            t = ctypes.windll.kernel32.GetDriveTypeW(drv)
            print("   %s type=%d" % (drv, t))
except Exception as ex:
    print("  枚举失败:", ex)

print()
print("=" * 74)
print("④ 直接问 shell: 命名空间里有没有可用标识")
print("=" * 74)
try:
    out = subprocess.run(["reg", "query",
        r"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace"],
        capture_output=True, text=True, errors="replace").stdout
    print(out.strip()[:800] if out.strip() else "  （无）")
except Exception as ex:
    print("  reg 查询被拦:", ex)

print()
print("=" * 74)
print("⑤ explorer.exe 能不能直接开（只查命令，不真开）")
print("=" * 74)
for s in cand[:5]:
    print("   > explorer.exe /n,\"%s\"" % s)
