# -*- coding: utf-8 -*-
"""
只读探针：Shell.Application.NameSpace() 认不认「此电脑\设备\…」这串的各种写法。
不弹窗、不抢前台、不起 explorer —— 只在当前进程里解析。
判据：解析成功就拿到 IShellFolder（PIDL 有效），失败返回 None。
"""
import ctypes
from ctypes import wintypes, POINTER, byref, c_void_p

ole32 = ctypes.windll.ole32
ole32.CoInitialize(None)


class GUID(ctypes.Structure):
    _fields_ = [('d1', ctypes.c_ulong), ('d2', ctypes.c_ushort),
                ('d3', ctypes.c_ushort), ('d4', ctypes.c_ubyte * 8)]


def guid(s):
    from uuid import UUID
    x = UUID(s)
    g = GUID()
    g.d1, g.d2, g.d3 = x.time_low, x.time_mid, x.time_hi_version
    b = x.bytes
    for i in range(8):
        g.d4[i] = b[8 + i]
    return g


class VARIANT(ctypes.Structure):
    """x64 VARIANT = 24 字节（vt/保留 8 字节 + 联合 8 + pad 8）。"""
    _fields_ = [('vt', ctypes.c_ushort), ('r1', ctypes.c_ushort),
                ('r2', ctypes.c_ushort), ('r3', ctypes.c_ushort),
                ('v', ctypes.c_void_p), ('pad', ctypes.c_void_p)]


VT_BSTR = 8
VT_DISPATCH = 9
VT_EMPTY = 0
DISPATCH_PROPERTYGET = 2
DISPATCH_METHOD = 1

ole32.CoCreateInstance.argtypes = [POINTER(GUID), c_void_p, wintypes.DWORD,
                                   POINTER(GUID), POINTER(c_void_p)]
ole32.CoCreateInstance.restype = ctypes.c_long

# Shell.Application
p = c_void_p()
hr = ole32.CoCreateInstance(byref(guid('{13709620-C279-11CE-A49E-444553540000}')),
                            None, 0x17, byref(guid('{00020400-0000-0000-C000-000000000046}')),
                            byref(p))
if hr != 0 or not p.value:
    print('拿不到 Shell.Application hr=0x%08X' % (hr & 0xffffffff))
    raise SystemExit(1)

vtbl = ctypes.cast(p, POINTER(POINTER(c_void_p))).contents
GetIDsOfNames = ctypes.WINFUNCTYPE(ctypes.c_long, c_void_p, POINTER(GUID),
                                   POINTER(ctypes.c_wchar_p), ctypes.c_uint,
                                   ctypes.c_uint, POINTER(ctypes.c_long))(vtbl[5])
Invoke = ctypes.WINFUNCTYPE(ctypes.c_long, c_void_p, ctypes.c_long, POINTER(GUID),
                            ctypes.c_uint, wintypes.USHORT, c_void_p,
                            c_void_p, c_void_p, ctypes.c_uint, c_void_p)(vtbl[6])
NIL = GUID()


def dispid(name):
    i = ctypes.c_long(-1)
    nm = (ctypes.c_wchar_p * 1)()
    nm[0] = name
    GetIDsOfNames(p, byref(NIL), nm, 1, 0x400, byref(i))
    return i.value


ID_NS = dispid('NameSpace')

# 一个 BSTR 参数的 DISPPARAMS（按引用传 VARIANT）
def make_dp(bstr_ptr):
    inner = ctypes.create_string_buffer(24)
    outer = ctypes.create_string_buffer(24)
    ctypes.memset(inner, 0, 24)
    ctypes.memset(outer, 0, 24)
    ctypes.memmove(inner, ctypes.byref(ctypes.c_ushort(VT_BSTR)), 2)
    ctypes.memmove(ctypes.addressof(inner) + 8, ctypes.byref(c_void_p(bstr_ptr)), 8)
    ctypes.memmove(outer, ctypes.byref(ctypes.c_ushort(11)), 2)   # VT_VARIANT|VT_BYREF
    ctypes.memmove(ctypes.addressof(outer) + 8, ctypes.byref(c_void_p(ctypes.addressof(inner))), 8)
    return inner, outer


def namespace_of(path):
    b = ctypes.windll.oleaut32.SysAllocString(path)
    inner, outer = make_dp(b)
    dp = ctypes.c_void_p * 0
    # DISPPARAMS: rgvarg, rgdispidNamedArgs, cArgs, cNamedArgs
    class DISPPARAMS(ctypes.Structure):
        _fields_ = [('rgvarg', ctypes.c_void_p), ('rgdispidNamedArgs', ctypes.c_void_p),
                    ('cArgs', ctypes.c_uint), ('cNamedArgs', ctypes.c_uint)]
    d = DISPPARAMS()
    d.rgvarg = ctypes.addressof(outer)
    d.rgdispidNamedArgs = None
    d.cArgs = 1
    d.cNamedArgs = 0
    res = VARIANT()
    hr = Invoke(p, ID_NS, byref(NIL), 0x400, DISPATCH_METHOD,
                ctypes.byref(d), byref(res), None, 0, None)
    ctypes.windll.oleaut32.SysFreeString(b)
    ok = (hr == 0 and res.vt == VT_DISPATCH and res.v)
    return ok, hr, res.vt, res.v


CANDIDATES = [
    r'此电脑\motorola edge 20 pro\内部共享存储空间',
    r'::{20D04FE0-3AEA-1069-A2D8-08002B30309D}\motorola edge 20 pro\内部共享存储空间',
    r'::{20D04FE0-3AEA-1069-A2D8-08002B30309D}',
    r'此电脑',
    r'::{645FF040-5081-101B-9F08-00AA002F954E}',   # 回收站（对照组，应能解析）
]

for c in CANDIDATES:
    try:
        ok, hr, vt, vp = namespace_of(c)
        print('%-8s vt=%-3d hr=0x%08X  %r' % ('能' if ok else '不能', vt, hr & 0xffffffff, c))
    except Exception as e:
        print('异常 %r -> %s' % (c, e))
