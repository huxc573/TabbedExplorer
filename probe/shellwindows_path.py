"""Probe: can we read the CURRENT FOLDER PATH of an explorer window from outside the process?

Context: TabbedExplorer embeds foreign explorer.exe windows (each started as
`explorer.exe /n,/separate`) via SetParent. To remember tabs per virtual desktop we must
learn each tab's current folder. Reading the address bar toolbar (TB_GETBUTTONTEXTW) came
back empty on the Win10 Ribbon explorer, so try the supported route instead:
the shell's ShellWindows collection -> IWebBrowser2.LocationURL, matched by HWND.

Pure ctypes + raw COM (no pywin32/comtypes on this box, and cscript/PowerShell-COM are blocked).
"""
import ctypes
import ctypes.wintypes as wt
from ctypes import POINTER, byref, c_void_p, c_uint, c_int, c_ushort, c_byte

ole32 = ctypes.windll.ole32
oleaut32 = ctypes.windll.oleaut32
user32 = ctypes.windll.user32

COINIT_APARTMENTTHREADED = 0x2
CLSCTX_LOCAL_SERVER = 0x4
CLSCTX_INPROC_SERVER = 0x1
DISPATCH_METHOD = 0x1
DISPATCH_PROPERTYGET = 0x2
DISP_E_EXCEPTION = 0x80020009
LOCALE_USER_DEFAULT = 0x400
VT_EMPTY, VT_I4, VT_BSTR, VT_DISPATCH, VT_VARIANT = 0, 3, 8, 9, 12
DISPID_PROPERTYPUT = -3


class GUID(ctypes.Structure):
    _fields_ = [("d1", ctypes.c_ulong), ("d2", ctypes.c_ushort),
                ("d3", ctypes.c_ushort), ("d4", c_byte * 8)]


def guid(s):
    g = GUID()
    ole32.CLSIDFromString(ctypes.c_wchar_p(s), byref(g))
    return g


class VARIANT(ctypes.Structure):
    class _U(ctypes.Union):
        _fields_ = [("llVal", ctypes.c_longlong), ("lVal", ctypes.c_long),
                    ("wReserved", c_ushort), ("bstrVal", c_void_p),
                    ("punkVal", c_void_p), ("pdispVal", c_void_p)]
    _fields_ = [("vt", c_ushort), ("r1", c_ushort), ("r2", c_ushort), ("r3", c_ushort),
                ("u", _U)]


class DISPPARAMS(ctypes.Structure):
    _fields_ = [("rgvarg", POINTER(VARIANT)), ("rgdispidNamedArgs", c_void_p),
                ("cArgs", c_uint), ("cNamedArgs", c_uint)]


class EXCEPINFO(ctypes.Structure):
    _fields_ = [("wCode", c_ushort), ("wReserved", c_ushort),
                ("bstrSource", c_void_p), ("bstrDescription", c_void_p),
                ("bstrHelpFile", c_void_p), ("dwHelpContext", ctypes.c_ulong),
                ("pvReserved", c_void_p), ("pfnDeferredFillIn", c_void_p),
                ("scode", ctypes.c_long)]


# IDispatch vtable:  0 QI | 1 AddRef | 2 Release | 3 GetTypeInfoCount | 4 GetTypeInfo
#                    5 GetIDsOfNames | 6 Invoke
_QI = ctypes.WINFUNCTYPE(ctypes.c_long, c_void_p, POINTER(GUID), c_void_p)
_ADDREF = ctypes.WINFUNCTYPE(ctypes.c_ulong, c_void_p)
_RELEASE = ctypes.WINFUNCTYPE(ctypes.c_ulong, c_void_p)
_GETIDSOFNAMES = ctypes.WINFUNCTYPE(ctypes.c_long, c_void_p, POINTER(GUID),
                                    POINTER(ctypes.c_wchar_p), c_uint, ctypes.c_ulong,
                                    POINTER(ctypes.c_long))
_INVOKE = ctypes.WINFUNCTYPE(ctypes.c_long, c_void_p, ctypes.c_long, POINTER(GUID),
                             ctypes.c_ulong, c_ushort, POINTER(DISPPARAMS),
                             POINTER(VARIANT), POINTER(EXCEPINFO), POINTER(c_uint))


def vcall(p, index, restype, *argtypes):
    vtbl = ctypes.cast(p, POINTER(c_void_p))[0]
    fn = ctypes.cast(ctypes.cast(vtbl, POINTER(c_void_p))[index],
                     ctypes.WINFUNCTYPE(restype, c_void_p, *argtypes))
    return fn


def pdisp_invoke(p, name, args=(), want=VARIANT, prop=True):
    """IDispatch::Invoke by name. args: list of VARIANT (already built)."""
    iid_null = GUID()
    dispid = ctypes.c_long()
    nm = ctypes.c_wchar_p(name)
    hr = vcall(p, 5, ctypes.c_long, POINTER(GUID), POINTER(ctypes.c_wchar_p),
               c_uint, ctypes.c_ulong, POINTER(ctypes.c_long))(
        p, byref(iid_null), byref(nm), 1, LOCALE_USER_DEFAULT, byref(dispid))
    if hr != 0:
        return ("GetIDsOfNames hr=0x%08X" % (hr & 0xFFFFFFFF), None)

    arr = (VARIANT * max(1, len(args)))()
    for i, a in enumerate(reversed(args)):
        arr[i] = a
    dp = DISPPARAMS()
    dp.rgvarg = ctypes.cast(arr, POINTER(VARIANT))
    dp.cArgs = len(args)
    dp.cNamedArgs = 0
    if not prop:
        dp.cArgs = len(args)
    res = VARIANT()
    ei = EXCEPINFO()
    err = c_uint()
    wflags = DISPATCH_PROPERTYGET | DISPATCH_METHOD if prop else DISPATCH_METHOD
    hr = vcall(p, 6, ctypes.c_long, ctypes.c_long, POINTER(GUID), ctypes.c_ulong,
               c_ushort, POINTER(DISPPARAMS), POINTER(VARIANT), POINTER(EXCEPINFO),
               POINTER(c_uint))(p, dispid.value, byref(iid_null), LOCALE_USER_DEFAULT,
                                wflags, byref(dp), byref(res), byref(ei), byref(err))
    if hr != 0:
        return ("Invoke(%s) hr=0x%08X scode=0x%08X" %
                (name, hr & 0xFFFFFFFF, ei.scode & 0xFFFFFFFF), None)
    return (None, res)


def bstr(v):
    p = v.u.bstrVal
    if not p:
        return ""
    s = ctypes.wstring_at(p)
    return s


def vint(i):
    v = VARIANT()
    v.vt = VT_I4
    v.u.lVal = i
    return v


def main():
    ole32.CoInitializeEx(None, COINIT_APARTMENTTHREADED)
    clsid = guid("{9BA05972-F6A8-11CF-A442-00A0C90A8F39}")   # ShellWindows
    iid_disp = guid("{00020400-0000-0000-C000-000000000046}")  # IDispatch
    p = c_void_p()
    hr = ole32.CoCreateInstance(byref(clsid), None,
                                CLSCTX_LOCAL_SERVER | CLSCTX_INPROC_SERVER,
                                byref(iid_disp), byref(p))
    print("CoCreateInstance(ShellWindows) hr=0x%08X p=%s" % (hr & 0xFFFFFFFF, hex(p.value or 0)))
    if hr != 0:
        return

    err, cnt = pdisp_invoke(p, "Count")
    print("Count ->", err, None if cnt is None else cnt.u.lVal)
    total = cnt.u.lVal if cnt and cnt.vt == VT_I4 else 0

    hwnds = []
    for i in range(total):
        err, item = pdisp_invoke(p, "Item", [vint(i)])
        if err:
            print("  Item(%d) -> %s" % (i, err))
            continue
        if item.vt != VT_DISPATCH or not item.u.pdispVal:
            print("  Item(%d) -> vt=%d (unexpected)" % (i, item.vt))
            continue
        q = item.u.pdispVal
        e2, hv = pdisp_invoke(q, "HWND")
        e3, url = pdisp_invoke(q, "LocationURL")
        e4, nm = pdisp_invoke(q, "LocationName")
        hval = hv.u.lVal if (hv is not None and hv.vt == VT_I4) else None
        uval = bstr(url) if (url is not None and url.vt == VT_BSTR) else "<%s>" % e3
        nval = bstr(nm) if (nm is not None and nm.vt == VT_BSTR) else "<%s>" % e4
        hwnds.append(hval)
        print("  [%d] hwnd=%s url=%s name=%s  (hwerr=%s)" % (i, hval, uval, nval, e2))

    # our own candidates: every CabinetWClass window, plus whether it is top-level or reparented
    print("\n-- CabinetWClass windows on screen (top-level vs reparented/embedded) --")
    found = []

    @ctypes.WINFUNCTYPE(ctypes.c_bool, c_void_p, ctypes.c_void_p)
    def cb(h, l):
        cls = ctypes.create_unicode_buffer(256)
        user32.GetClassNameW(h, cls, 256)
        if cls.value == "CabinetWClass":
            parent = user32.GetParent(h)
            pid = wt.DWORD()
            user32.GetWindowThreadProcessId(h, byref(pid))
            title = ctypes.create_unicode_buffer(512)
            user32.GetWindowTextW(h, title, 512)
            found.append((int(h), int(parent or 0), int(pid.value), title.value))
        user32.EnumChildWindows(h, cb, None)
        return True

    user32.EnumWindows(cb, None)
    for h, parent, pid, title in found:
        kind = "TOP" if parent == 0 else "EMBEDDED(parent=%s)" % hex(parent)
        inshell = "YES" if h in hwnds else "no"
        print("  hwnd=%s %-22s pid=%-6d inShellWindows=%-4s title=%s" % (hex(h), kind, pid, inshell, title))
    print("ShellWindows hwnd list:", [hex(x) if x else None for x in hwnds])


main()
