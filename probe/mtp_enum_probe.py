# -*- coding: utf-8 -*-
"""决定性验证：从「此电脑」的 IShellFolder 枚举子项，看 MTP 设备在不在里面。
只读：不打开窗口、不碰前台、不写任何东西。

若枚举得到设备 → 正解 = 「枚举此电脑 → 按名找到设备 → 取它的 PIDL → 逐级往下」，
这正是 explorer 地址栏自己做的事，MTP 位置本来就不存在外部文本入口（已实测）。
"""
import ctypes
from ctypes import wintypes, byref, POINTER, c_void_p

shell32 = ctypes.windll.shell32
ole32 = ctypes.windll.ole32
ole32.CoInitialize(None)

class _GUID(ctypes.Structure):
    _fields_ = [("d1", ctypes.c_ulong), ("d2", ctypes.c_ushort),
                ("d3", ctypes.c_ushort), ("d4", ctypes.c_ubyte * 8)]

def guid(s):
    from uuid import UUID
    x = UUID(s); g = _GUID()
    g.d1, g.d2, g.d3 = x.time_low, x.time_mid, x.time_hi_version
    b = x.bytes
    for i in range(8): g.d4[i] = b[8 + i]
    return g

shell32.SHParseDisplayName.argtypes = [wintypes.LPCWSTR, c_void_p,
                                       POINTER(c_void_p), wintypes.ULONG,
                                       POINTER(wintypes.ULONG)]
shell32.SHParseDisplayName.restype = ctypes.c_long
shell32.SHBindToObject.argtypes = [c_void_p, c_void_p, c_void_p,
                                   POINTER(_GUID), POINTER(c_void_p)]
shell32.SHBindToObject.restype = ctypes.c_long
shell32.ILCombine.argtypes = [c_void_p, c_void_p]
shell32.ILCombine.restype = ctypes.c_void_p
shell32.ILFree.argtypes = [c_void_p]
shell32.ILFree.restype = None
shell32.SHGetNameFromIDList.argtypes = [c_void_p, ctypes.c_int, POINTER(c_void_p)]
shell32.SHGetNameFromIDList.restype = ctypes.c_long
ole32.CoTaskMemFree.argtypes = [c_void_p]
ole32.CoTaskMemFree.restype = None
SIGDN_NORMALDISPLAY = 0x00000000

IID_IShellFolder = guid("{000214E6-0000-0000-C000-000000000046}")

def slot(p, i):
    vt = ctypes.cast(p, POINTER(POINTER(c_void_p))).contents
    return vt[i]

def release(p):
    if not p:
        return
    ctypes.WINFUNCTYPE(ctypes.c_ulong, c_void_p)(slot(p, 2))(p)

# ① 「此电脑」的绝对 PIDL
pidl = c_void_p()
hr = shell32.SHParseDisplayName("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", None,
                                byref(pidl), 0, None)
print("① SHParseDisplayName(此电脑) hr=0x%08X pidl=0x%X" % (hr & 0xFFFFFFFF, pidl.value or 0))
if hr != 0 or not pidl.value:
    raise SystemExit("此电脑都解析不了，停")

# ② 绑定到它的 IShellFolder
sf = c_void_p()
hr = shell32.SHBindToObject(None, pidl, None, byref(IID_IShellFolder), byref(sf))
print("② SHBindToObject(此电脑) hr=0x%08X sf=0x%X" % (hr & 0xFFFFFFFF, sf.value or 0))
if hr != 0 or not sf.value:
    raise SystemExit("绑定不了 IShellFolder，停")

# ③ EnumObjects（IShellFolder vtable 4）
ppenum = c_void_p()
EnumObjects = ctypes.WINFUNCTYPE(ctypes.c_long, c_void_p, c_void_p,
                                 wintypes.DWORD, POINTER(c_void_p))(slot(sf, 4))
SHCONTF = 0x20 | 0x40 | 0x80          # FOLDERS | NONFOLDERS | INCLUDEHIDDEN
hr = EnumObjects(sf, None, SHCONTF, byref(ppenum))
print("③ EnumObjects hr=0x%08X enum=0x%X" % (hr & 0xFFFFFFFF, ppenum.value or 0))

class STRRET(ctypes.Structure):
    # x64 布局：uType(4) + pad(4) + union(8)。STRRET_WSTR 用 pOleStr 指针，
    # STRRET_OFFSET 用 uOffset，STRRET_CSTR 用 cStr[260]。
    _fields_ = [("uType", ctypes.c_uint), ("pad", ctypes.c_uint),
                ("pOleStr", ctypes.c_void_p),
                ("offset", ctypes.c_uint), ("cstr", ctypes.c_char * 260)]

if hr == 0 and ppenum.value:
    # IEnumIDList::Next（vtable 3）
    Next = ctypes.WINFUNCTYPE(ctypes.c_long, c_void_p, ctypes.c_ulong,
                              POINTER(c_void_p), POINTER(ctypes.c_ulong))(slot(ppenum, 3))
    # IShellFolder::GetDisplayNameOf（vtable 11）
    GetName = ctypes.WINFUNCTYPE(ctypes.c_long, c_void_p, c_void_p,
                                 POINTER(STRRET), ctypes.c_uint)(slot(sf, 11))
    n = 0
    while n < 60:
        one = c_void_p(); got = ctypes.c_ulong(0)
        hr = Next(ppenum, 1, byref(one), byref(got))
        if hr != 0 or got.value == 0:
            break
        # 拼成绝对 PIDL，再问它要显示名 —— 比自己调 GetDisplayNameOf 稳
        nm = None
        abs_ = shell32.ILCombine(pidl, one)
        if abs_:
            namep = c_void_p()
            if shell32.SHGetNameFromIDList(abs_, SIGDN_NORMALDISPLAY, byref(namep)) == 0 and namep.value:
                nm = ctypes.wstring_at(namep)
                ole32.CoTaskMemFree(namep)
            shell32.ILFree(abs_)
        if nm is None:
            nm = "<无名字>"
        cb = ctypes.cast(one, POINTER(ctypes.c_ushort)).contents.value if one.value else -1
        mark = "  ★★★ 可能是便携设备" if ("motorola" in nm.lower() or "edge" in nm.lower()) else ""
        print("   [%02d] cb=%-4d 名字=%r%s" % (n, cb, nm, mark))
        n += 1
    print("共列出 %d 项" % n)
    release(ppenum)

release(sf)
if pidl.value:
    ole32.CoTaskMemFree(pidl)
