# -*- coding: utf-8 -*-
"""实测：explorer.exe /n,/separate,"<设备真形式串>" 能不能开到手机目录。
不抢前台（窗口会先隐藏再被 TB 收编）。判定看 TB 日志里的「现在在」。
"""
import subprocess, time, ctypes
from ctypes import wintypes

DEV = "\\\\?\\usb#vid_18d1&pid_4ee2&mi_00#7&2ebcb680&0&0000#{6ac27878-a6fa-4155-ba85-f98f491d4f33}"
PC = "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}"
T = PC + "\\" + DEV + "\\内部共享存储空间\\!573\\APPS\\Eta\\projects\\los-enhance\\refs\\official-rom"

print("准备开：")
print(T)
print()

subprocess.Popen(["explorer.exe", "/n,/separate,%s" % T])
time.sleep(8)

# 读 ShellWindows 清单，看有没有窗口停在 MTP 路径上
ole32 = ctypes.windll.ole32
ole32.CoInitialize(None)

class G(ctypes.Structure):
    _fields_ = [("d1", ctypes.c_ulong), ("d2", ctypes.c_ushort),
                ("d3", ctypes.c_ushort), ("d4", ctypes.c_ubyte * 8)]

def guid(s):
    from uuid import UUID
    x = UUID(s); g = G()
    g.d1, g.d2, g.d3 = x.time_low, x.time_mid, x.time_hi_version
    b = x.bytes
    for i in range(8): g.d4[i] = b[8 + i]
    return g

class V(ctypes.Structure):
    _fields_ = [("vt", ctypes.c_ushort), ("a", ctypes.c_ushort), ("b", ctypes.c_ushort),
                ("c", ctypes.c_ushort), ("p1", ctypes.c_void_p), ("p2", ctypes.c_void_p)]

ole32.CoCreateInstance.argtypes = [ctypes.POINTER(G), ctypes.c_void_p, wintypes.DWORD,
                                   ctypes.POINTER(G), ctypes.POINTER(ctypes.c_void_p)]
ole32.CoCreateInstance.restype = ctypes.c_long
p = ctypes.c_void_p()
hr = ole32.CoCreateInstance(ctypes.byref(guid('{9BA05972-F6A8-11CF-A442-00A0C90A8F39}')),
                            None, 0x17,
                            ctypes.byref(guid('{00020400-0000-0000-C000-000000000046}')),
                            ctypes.byref(p))
if hr != 0:
    print("ShellWindows 拿不到 hr=0x%08X" % (hr & 0xFFFFFFFF))
    raise SystemExit

vt = ctypes.cast(p, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p))).contents
GIDN = ctypes.WINFUNCTYPE(ctypes.c_long, ctypes.c_void_p, ctypes.POINTER(G),
                          ctypes.POINTER(ctypes.c_wchar_p), ctypes.c_uint,
                          ctypes.c_uint, ctypes.POINTER(ctypes.c_long))(vt[5])
INV = ctypes.WINFUNCTYPE(ctypes.c_long, ctypes.c_void_p, ctypes.c_long, ctypes.POINTER(G),
                         ctypes.c_uint, ctypes.c_ushort, ctypes.POINTER(V), ctypes.c_void_p,
                         ctypes.c_void_p, ctypes.c_uint, ctypes.POINTER(V))(vt[6])
NIL = G()

def get(name):
    nm = (ctypes.c_wchar_p * 1)(); nm[0] = name
    i = ctypes.c_long(-1)
    GIDN(p, ctypes.byref(NIL), nm, 1, 0x400, ctypes.byref(i))
    r = V()
    INV(p, i.value, ctypes.byref(NIL), 0x400, 2, ctypes.byref(r), None, None, 0, None)
    return r

cnt = get("Count")
print("ShellWindows 里窗口数 =", cnt.p1)
n = cnt.p1 or 0
for k in range(max(0, n - 8), n):
    nm = (ctypes.c_wchar_p * 1)(); nm[0] = "Item"
    i = ctypes.c_long(-1)
    GIDN(p, ctypes.byref(NIL), nm, 1, 0x400, ctypes.byref(i))
    r = V()
    INV(p, i.value, ctypes.byref(NIL), 0x400, 2, ctypes.byref(r), None, None, 0, None)
    if r.vt != 9 or not r.p1:
        continue
    d = ctypes.c_void_p(r.p1)
    dvt = ctypes.cast(d, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p))).contents
    DGIDN = ctypes.WINFUNCTYPE(ctypes.c_long, ctypes.c_void_p, ctypes.POINTER(G),
                              ctypes.POINTER(ctypes.c_wchar_p), ctypes.c_uint,
                              ctypes.c_uint, ctypes.POINTER(ctypes.c_long))(dvt[5])
    DINV = ctypes.WINFUNCTYPE(ctypes.c_long, ctypes.c_void_p, ctypes.c_long, ctypes.POINTER(G),
                              ctypes.c_uint, ctypes.c_ushort, ctypes.POINTER(V), ctypes.c_void_p,
                              ctypes.c_void_p, ctypes.c_uint, ctypes.POINTER(V))(dvt[6])
    q = (ctypes.c_wchar_p * 1)(); q[0] = "LocationURL"
    j = ctypes.c_long(-1)
    DGIDN(d, ctypes.byref(NIL), q, 1, 0x400, ctypes.byref(j))
    rv = V()
    DINV(d, j.value, ctypes.byref(NIL), 0x400, 2, ctypes.byref(rv), None, None, 0, None)
    u = ctypes.wstring_at(rv.p1) if (rv.vt == 8 and rv.p1) else None
    print("  [%d] LocationURL=%r" % (k, u))
