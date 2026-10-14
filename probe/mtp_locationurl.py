# -*- coding: utf-8 -*-
"""读 ShellWindows 每项的 LocationURL / LocationName / Folder.Self.Path（只读）。纯 ctypes。"""
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

CLSID_ShellWindows = guid("{9BA05972-F6A8-11CF-A442-00A0C90A8F39}")
IID_IDispatch      = guid("{00020400-0000-0000-C000-000000000046}")
IID_NULL           = _GUID()

ole32.CoCreateInstance.argtypes = [POINTER(_GUID), c_void_p, wintypes.DWORD,
                                   POINTER(_GUID), POINTER(c_void_p)]
ole32.CoCreateInstance.restype = ctypes.c_long

p = c_void_p()
hr = ole32.CoCreateInstance(byref(CLSID_ShellWindows), None, 0x17,
                            byref(IID_IDispatch), byref(p))
if hr != 0 or not p.value:
    raise SystemExit("拿不到 ShellWindows hr=0x%08X" % (hr & 0xffffffff))

# vtable: 0 QI, 1 AddRef, 2 Release, 3 GetTypeInfoCount, 4 GetTypeInfo,
#         5 GetIDsOfNames, 6 Invoke
vt = ctypes.cast(p, POINTER(POINTER(c_void_p))).contents
GetIDsOfNames = ctypes.WINFUNCTYPE(ctypes.c_long, c_void_p, POINTER(_GUID),
                                   POINTER(ctypes.c_wchar_p), ctypes.c_uint,
                                   ctypes.c_uint, POINTER(ctypes.c_long))(vt[5])
Invoke = ctypes.WINFUNCTYPE(ctypes.c_long, c_void_p, ctypes.c_long, POINTER(_GUID),
                            ctypes.c_uint, wintypes.USHORT, ctypes.c_void_p,
                            ctypes.c_void_p, ctypes.c_void_p,
                            ctypes.c_void_p)(vt[6])

LOCALE_USER = 0x0400
METHOD = 1
PROPGET = 2

class VARIANT(ctypes.Structure):
    # 16 bytes on x64: vt, wReserved1..3, union(8 bytes), pad
    _fields_ = [("vt", ctypes.c_ushort), ("w1", ctypes.c_ushort),
                ("w2", ctypes.c_ushort), ("w3", ctypes.c_ushort),
                ("val", ctypes.c_void_p), ("pad", ctypes.c_void_p)]

VT_I4, VT_BSTR, VT_DISPATCH, VT_BOOL = 3, 8, 9, 11
VT_BYREF, VT_VARIANT = 0x4000, 12
SYSMEM = (1 << 10) | (1 << 11)   # MEMBERID_NIL? 实际用 DISPID_PROPERTYPUT 时用

def dispid(d, name):
    namebuf = ctypes.create_unicode_buffer(name)
    arr = (ctypes.c_wchar_p * 1)()
    arr[0] = ctypes.cast(namebuf, ctypes.c_wchar_p)
    ids = ctypes.c_long(-1)
    hr = GetIDsOfNames(d, byref(IID_NULL), arr, 1, LOCALE_USER, byref(ids))
    return (hr, ids.value)

def call(d, name, kind=PROPGET, args=()):
    """args = [(VT, value), ...] 按位置传。返回 VARIANT 或 None。"""
    hr, i = dispid(d, name)
    if hr != 0:
        return ("ERR_DISPID_%s" % name, hr)
    n = len(args)
    argv = (VARIANT * max(n, 1))()
    for k, (t, v) in enumerate(args):
        ctypes.memset(byref(argv[k]), 0, ctypes.sizeof(VARIANT))
        argv[k].vt = t
        if t == VT_I4:
            argv[k].val = ctypes.c_void_p(int(v))
        elif t == VT_BSTR:
            argv[k].val = ctypes.cast(ctypes.c_wchar_p(str(v)), ctypes.c_void_p)
        elif t == VT_BOOL:
            argv[k].val = ctypes.c_void_p(-1 if v else 0)
        elif t == VT_DISPATCH:
            argv[k].val = ctypes.c_void_p(int(v))
    res = VARIANT(); ctypes.memset(byref(res), 0, ctypes.sizeof(VARIANT))
    hr = Invoke(d, i, byref(IID_NULL), LOCALE_USER, kind,
                ctypes.byref(res), None, None, ctypes.cast(argv, ctypes.c_void_p))
    if hr != 0:
        return ("ERR_INVOKE_%s" % name, hr)
    return res

def val(res):
    if res is None or isinstance(res, tuple): return res
    if res.vt == VT_I4:  return ctypes.c_long(res.val or 0).value
    if res.vt == VT_BSTR:
        s = ctypes.cast(res.val, ctypes.c_wchar_p).value if res.val else None
        if res.val: ole32.SysFreeString(res.val)
        return s
    if res.vt == VT_DISPATCH: return res.val
    if res.vt == (VT_DISPATCH | VT_BYREF): return None
    return "vt=%d val=0x%X" % (res.vt, res.val or 0)

def s_prop(d, name):
    return val(call(d, name))

u = ctypes.windll.user32
u.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
u.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]

cnt = s_prop(p, "Count")
print("ShellWindows.Count =", cnt)
if not isinstance(cnt, int):
    raise SystemExit("Count 读不到 -> %r" % (cnt,))

for i in range(cnt):
    it = val(call(p, "Item", PROPGET, [(VT_I4, i)]))
    if not isinstance(it, int) or it == 0:
        continue
    hw = s_prop(it, "HWND")
    loc = s_prop(it, "LocationURL")
    nm  = s_prop(it, "LocationName")
    if not (isinstance(loc, str) and ("usb" in (loc or "").lower()
            or "portable" in (loc or "").lower() or "motorola" in (loc or "").lower()
            or "edge" in (loc or "").lower())):
        # 只打非 file:/// 的、以及含 moto 关键词的，避免刷屏
        if isinstance(loc, str) and loc.startswith("file:"):
            continue
    b = ctypes.create_unicode_buffer(512)
    if isinstance(hw, int) and hw:
        u.GetWindowTextW(hw, b, 512)
    print("-" * 76)
    print("  #%d hwnd=%s 窗口=%r" % (i, hw, b.value))
    print("     LocationURL  = %r" % (loc,))
    print("     LocationName = %r" % (nm,))
    doc = val(call(it, "Document"))
    if isinstance(doc, int) and doc:
        fol = val(call(doc, "Folder"))
        if isinstance(fol, int) and fol:
            print("     Folder.Title = %r" % (s_prop(fol, "Title"),))
            self_ = val(call(fol, "Self"))
            if isinstance(self_, int) and self_:
                print("     Self.Path    = %r" % (s_prop(self_, "Path"),))
