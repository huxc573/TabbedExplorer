"""只读：把 ShellWindows.Item(i) 的几种传参姿势都试一遍，看哪种能稳定拿到非空 DISPATCH。

背景：`src/ShellBrowserReg.PathOfWindow` 靠「枚举 ShellWindows -> 按 HWND 对上号」抢在
shell 那扇窗露脸之前读出它的目标目录。但探针实测 121 项里有 120 项 `Item(i)` 回的是
`VT_DISPATCH` + **空指针**，只有一项正常 —— 那条路于是白走，用户看到的还是「还是会闪」。

这里不改任何东西，只回答两个问题：
  A. `Item` 的形参到底该怎么传（`VT_VARIANT|VT_BYREF` 还是直接 `VT_I4`）；
  B. 索引从 0 还是从 1 起。
"""
import ctypes
import ctypes.wintypes as wt
from ctypes import POINTER, byref, c_void_p, c_uint, c_ushort, c_byte, c_int

ole32 = ctypes.windll.ole32
user32 = ctypes.windll.user32

COINIT_APARTMENTTHREADED = 0x2
CLSCTX_INPROC_SERVER = 0x1
CLSCTX_LOCAL_SERVER = 0x4
CLSCTX_ALL = 0x17
DISPATCH_METHOD = 0x1
DISPATCH_PROPERTYGET = 0x2
LOCALE_USER_DEFAULT = 0x400
VT_EMPTY, VT_I4, VT_BSTR, VT_DISPATCH, VT_VARIANT = 0, 3, 8, 9, 12
VT_BYREF = 0x4000


class GUID(ctypes.Structure):
    _fields_ = [("d1", ctypes.c_ulong), ("d2", c_ushort), ("d3", c_ushort), ("d4", c_byte * 8)]


def guid(s):
    g = GUID()
    ole32.CLSIDFromString(ctypes.c_wchar_p(s), byref(g))
    return g


def progid_of(clsid):
    buf = ctypes.c_wchar_p()
    hr = ole32.ProgIDFromCLSID(byref(clsid), byref(buf))
    return (hr, buf.value)


class VARIANT(ctypes.Structure):
    class _U(ctypes.Union):
        _fields_ = [("llVal", ctypes.c_longlong), ("lVal", c_int), ("wReserved", c_ushort),
                    ("bstrVal", c_void_p), ("punkVal", c_void_p), ("pdispVal", c_void_p),
                    ("pad", c_byte * 16)]
    _fields_ = [("vt", c_ushort), ("r1", c_ushort), ("r2", c_ushort), ("r3", c_ushort), ("u", _U)]


class DISPPARAMS(ctypes.Structure):
    _fields_ = [("rgvarg", c_void_p), ("rgdispidNamedArgs", c_void_p),
                ("cArgs", c_uint), ("cNamedArgs", c_uint)]


class EXCEPINFO(ctypes.Structure):
    _fields_ = [("wCode", c_ushort), ("wReserved", c_ushort), ("bstrSource", c_void_p),
                ("bstrDescription", c_void_p), ("bstrHelpFile", c_void_p),
                ("dwHelpContext", ctypes.c_ulong), ("pvReserved", c_void_p),
                ("pfnDeferredFillIn", c_void_p), ("scode", c_int)]


def vcall(p, index, restype, *argtypes):
    vtbl = ctypes.cast(p, POINTER(c_void_p))[0]
    return ctypes.cast(ctypes.cast(vtbl, POINTER(c_void_p))[index],
                       ctypes.WINFUNCTYPE(restype, c_void_p, *argtypes))


def dispid(p, name):
    iid = GUID()
    d = c_int()
    nm = ctypes.c_wchar_p(name)
    hr = vcall(p, 5, ctypes.c_long, POINTER(GUID), POINTER(ctypes.c_wchar_p), c_uint,
               ctypes.c_ulong, POINTER(c_int))(p, byref(iid), byref(nm), 1,
                                               LOCALE_USER_DEFAULT, byref(d))
    return (hr, d.value)


def invoke_raw(p, disp_id, wflags, rgvarg_ptr, nargs):
    dp = DISPPARAMS()
    dp.rgvarg = rgvarg_ptr
    dp.cArgs = nargs
    res = VARIANT()
    ei = EXCEPINFO()
    err = c_uint()
    iid = GUID()
    hr = vcall(p, 6, ctypes.c_long, c_int, POINTER(GUID), ctypes.c_ulong, c_ushort,
               POINTER(DISPPARAMS), POINTER(VARIANT), POINTER(EXCEPINFO),
               POINTER(c_uint))(p, disp_id, byref(iid), LOCALE_USER_DEFAULT, wflags,
                                byref(dp), byref(res), byref(ei), byref(err))
    return hr, res, ei


def mk_variant(vt, val):
    v = VARIANT()
    v.vt = vt
    v.u.lVal = val
    return v


def read_bstr(p):
    if not p:
        return None
    return ctypes.wstring_at(p)


def main():
    ole32.CoInitializeEx(None, COINIT_APARTMENTTHREADED)

    clsid = guid("{9BA05972-F6A8-11CF-A442-00A0C90A8F39}")
    print("ShellWindows ProgID:", progid_of(clsid))

    # ---- Shell.Application 到底能不能建（startmenu 探针卡在这） ----
    sa = guid("{13709620-C279-11CE-A49E-444553540000}")
    print("Shell.Application ProgID:", progid_of(sa))
    iid_disp = guid("{00020400-0000-0000-C000-000000000046}")
    for name, ctx in (("ALL", CLSCTX_ALL), ("LOCAL", CLSCTX_LOCAL_SERVER),
                      ("LOCAL|INPROC", CLSCTX_LOCAL_SERVER | CLSCTX_INPROC_SERVER)):
        p = c_void_p()
        hr = ole32.CoCreateInstance(byref(sa), None, ctx, byref(iid_disp), byref(p))
        print("  CoCreateInstance(Shell.Application, %-12s) hr=0x%08X p=%s"
              % (name, hr & 0xFFFFFFFF, hex(p.value or 0)))

    # ---- 打开 ShellWindows ----
    p = c_void_p()
    hr = ole32.CoCreateInstance(byref(clsid), None, CLSCTX_LOCAL_SERVER, byref(iid_disp), byref(p))
    print("\nCoCreateInstance(ShellWindows) hr=0x%08X" % (hr & 0xFFFFFFFF))
    if hr != 0:
        return
    p = p.value

    hr_count, did_count = dispid(p, "Count")
    _, cnt, _ = invoke_raw(p, did_count, DISPATCH_PROPERTYGET, None, 0)
    total = cnt.u.lVal
    print("Count =", total)

    hr_item, did_item = dispid(p, "Item")
    print("Item dispid hr=0x%08X id=%d" % (hr_item & 0xFFFFFFFF, did_item))

    def try_item(mode, i):
        """mode: 'byref' = VT_VARIANT|VT_BYREF 指向一个 VT_I4；'direct' = 直接传 VT_I4。"""
        inner = VARIANT()
        inner.vt = VT_I4
        inner.u.lVal = i
        outer = VARIANT()
        if mode == "byref":
            outer.vt = VT_VARIANT | VT_BYREF
            outer.u.pdispVal = ctypes.cast(byref(inner), c_void_p).value
        else:
            outer.vt = VT_I4
            outer.u.lVal = i
        buf = (VARIANT * 1)()
        buf[0] = outer
        hr, res, ei = invoke_raw(p, did_item, DISPATCH_METHOD,
                                 ctypes.cast(buf, c_void_p).value, 1)
        return hr, res, ei

    for mode in ("byref", "direct"):
        ok = 0
        sample = []
        for i in range(total):
            hr, res, ei = try_item(mode, i)
            if hr == 0 and res.vt == VT_DISPATCH and res.u.pdispVal:
                ok += 1
                if len(sample) < 4:
                    sample.append((i, res.u.pdispVal))
        print("\n[%s] 非空 DISPATCH %d/%d  样本=%s"
              % (mode, ok, total, [(i, hex(v)) for i, v in sample]))

    # ---- 对第一个拿到的项，看 HWND / LocationURL 是什么类型 ----
    print("\n-- 逐项明细（byref）--")
    shown = 0
    for i in range(total):
        hr, res, ei = try_item("byref", i)
        if hr != 0 or res.vt != VT_DISPATCH or not res.u.pdispVal:
            continue
        q = res.u.pdispVal
        out = {}
        for prop in ("HWND", "LocationURL", "LocationName"):
            h2, d2 = dispid(q, prop)
            if h2 != 0:
                out[prop] = "<nodisp>"
                continue
            h3, r3, _ = invoke_raw(q, d2, DISPATCH_PROPERTYGET, None, 0)
            if h3 != 0:
                out[prop] = "<hr 0x%08X>" % (h3 & 0xFFFFFFFF)
            elif r3.vt == VT_I4:
                out[prop] = "I4 %d" % r3.u.lVal
            elif r3.vt == VT_BSTR:
                out[prop] = "BSTR %r" % read_bstr(r3.u.bstrVal)
            else:
                out[prop] = "vt=%d raw=%s" % (r3.vt, hex(r3.u.pad[0] if False else 0))
        print("  [%d] %s" % (i, out))
        shown += 1
        if shown >= 12:
            print("  ...（只列前 12 项）")
            break

    print("\n(cab 窗口一览)")
    ENUMPROC = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)

    def cb(h, _):
        b = ctypes.create_unicode_buffer(64)
        user32.GetClassNameW(h, b, 64)
        if b.value in ("CabinetWClass", "ExploreWClass"):
            pid = wt.DWORD()
            user32.GetWindowThreadProcessId(h, byref(pid))
            par = user32.GetParent(h)
            vis = user32.IsWindowVisible(h)
            print("  hwnd=0x%X pid=%d visible=%d parent=%s" % (h, pid.value, vis, hex(par or 0)))
        return True

    user32.EnumWindows(ENUMPROC(cb), 0)


main()
