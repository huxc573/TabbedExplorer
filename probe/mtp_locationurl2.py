# -*- coding: utf-8 -*-
"""读 ShellWindows 每项 LocationURL（只读）。VARIANT 按 x64 的 24 字节排。"""
import ctypes
from ctypes import wintypes, POINTER, byref, c_void_p

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

# ★ x64 的 VARIANT 是 24 字节
class VARIANT(ctypes.Structure):
    _fields_ = [("vt", ctypes.c_ushort), ("r1", ctypes.c_ushort),
                ("r2", ctypes.c_ushort), ("r3", ctypes.c_ushort),
                ("v1", ctypes.c_void_p), ("v2", ctypes.c_void_p)]
assert ctypes.sizeof(VARIANT) == 24, ctypes.sizeof(VARIANT)

VT_I4, VT_BSTR, VT_DISPATCH, VT_BOOL = 3, 8, 9, 11
VT_BYREF, VT_VARIANT = 0x4000, 12

ole32.CoCreateInstance.argtypes = [POINTER(_GUID), c_void_p, wintypes.DWORD,
                                   POINTER(_GUID), POINTER(c_void_p)]
ole32.CoCreateInstance.restype = ctypes.c_long

p = c_void_p()
hr = ole32.CoCreateInstance(byref(guid("{9BA05972-F6A8-11CF-A442-00A0C90A8F39}")), None,
                            0x17, byref(guid("{00020400-0000-0000-C000-000000000046}")),
                            byref(p))
if hr != 0 or not p.value:
    raise SystemExit("CoCreateInstance 失败")

vtp = ctypes.cast(p, POINTER(POINTER(c_void_p))).contents
GetIDsOfNames = ctypes.WINFUNCTYPE(ctypes.c_long, c_void_p, POINTER(_GUID),
                                   POINTER(ctypes.c_wchar_p), ctypes.c_uint,
                                   ctypes.c_uint, POINTER(ctypes.c_long))(vtp[5])
Invoke = ctypes.WINFUNCTYPE(ctypes.c_long, c_void_p, ctypes.c_long, POINTER(_GUID),
                            ctypes.c_uint, wintypes.USHORT, POINTER(VARIANT),
                            ctypes.c_void_p, ctypes.c_void_p,
                            POINTER(VARIANT))(vtp[6])

IID_NULL = _GUID()
LOCALE_USER = 0x0400
PROPGET = 2

def dispid(d, name):
    nm = (ctypes.c_wchar_p * 1)()
    nm[0] = name
    i = ctypes.c_long(-1)
    hr = GetIDsOfNames(d, byref(IID_NULL), nm, 1, LOCALE_USER, byref(i))
    return hr, i.value

def pget(d, name, argv=None):
    hr, i = dispid(d, name)
    if hr != 0: return None
    n = len(argv) if argv else 0
    arr = (VARIANT * max(n, 1))()
    if argv:
        for k, a in enumerate(argv):
            ctypes.memmove(byref(arr[k]), byref(a), ctypes.sizeof(VARIANT))
    res = VARIANT()
    hr = Invoke(d, i, byref(IID_NULL), LOCALE_USER, PROPGET, byref(res),
                None, None, ctypes.cast(arr, POINTER(VARIANT)))
    if hr != 0: return None
    return res

def as_int(v):
    return (v.v1 or 0) & 0xFFFFFFFF if v and v.vt == VT_I4 else None
def as_str(v):
    if not v or v.vt != VT_BSTR or not v.v1: return None
    s = ctypes.cast(v.v1, ctypes.c_wchar_p).value
    ole32.SysFreeString(v.v1)
    return s
def as_disp(v):
    return v.v1 if (v and v.vt == VT_DISPATCH and v.v1) else None

cnt = as_int(pget(p, "Count"))
print("ShellWindows.Count =", cnt)
if cnt is None:
    raise SystemExit("Count 读不到")

def mk_i4(n):
    a = VARIANT(); a.vt = VT_I4; a.v1 = ctypes.c_void_p(n); return a

u = ctypes.windll.user32
u.GetWindowTextW.argtypes = [c_void_p, ctypes.c_wchar_p, ctypes.c_int]

for i in range(cnt):
    it = as_disp(pget(p, "Item", [mk_i4(i)]))
    if not it: continue
    hw = as_int(pget(it, "HWND"))
    loc = as_str(pget(it, "LocationURL"))
    nm = as_str(pget(it, "LocationName"))
    if isinstance(loc, str) and loc.startswith("file:"):
        continue                        # 真路径的不占篇幅
    b = ctypes.create_unicode_buffer(512)
    if hw: u.GetWindowTextW(hw, b, 512)
    print("-" * 74)
    print("  #%d hwnd=0x%X 窗口=%r" % (i, hw or 0, b.value))
    print("     LocationURL  = %r" % (loc,))
    print("     LocationName = %r" % (nm,))
    doc = as_disp(pget(it, "Document"))
    if doc:
        fol = as_disp(pget(doc, "Folder"))
        if fol:
            print("     Folder.Title = %r" % (as_str(pget(fol, "Title")),))
            sf = as_disp(pget(fol, "Self"))
            if sf:
                print("     Self.Path    = %r" % (as_str(pget(sf, "Path")),))
