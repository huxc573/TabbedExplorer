"""Probe 4: AccessibleObjectFromWindow(hwnd, OBJID_NATIVEOM) -> browser OM -> current folder.

Why this and not ShellWindows: after SetParent, IWebBrowser2.HWND reports the window's
TOP-LEVEL ancestor, i.e. OUR FORM, for every embedded tab -> all tabs collapse to the same
key and can't be told apart. OBJID_NATIVEOM asks *that specific HWND* for its native object,
so it works per-tab and survives reparenting.

Reads: LocationURL  (file:///... for real folders, "" for virtual like This PC)
       Document.Folder.Self.Path  (the plain path, the thing we want to persist)
"""
import ctypes
import ctypes.wintypes as wt
from ctypes import POINTER, byref, c_void_p, c_uint, c_byte, c_ushort

ole32 = ctypes.windll.ole32
oleacc = ctypes.windll.oleacc
user32 = ctypes.windll.user32

COINIT_APARTMENTTHREADED = 0x2
DISPATCH_METHOD = 0x1
DISPATCH_PROPERTYGET = 0x2
LOCALE_USER_DEFAULT = 0x400
VT_I4, VT_BSTR, VT_DISPATCH = 3, 8, 9
OBJID_NATIVEOM = 0xFFFFFFF0
OBJID_WINDOW = 0x0
OBJID_CLIENT = 0xFFFFFFFC


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
                    ("bstrVal", c_void_p), ("pdispVal", c_void_p)]
    _fields_ = [("vt", c_ushort), ("r1", c_ushort), ("r2", c_ushort), ("r3", c_ushort),
                ("u", _U)]


class DISPPARAMS(ctypes.Structure):
    _fields_ = [("rgvarg", POINTER(VARIANT)), ("rgdispidNamedArgs", c_void_p),
                ("cArgs", c_uint), ("cNamedArgs", c_uint)]


class EXCEPINFO(ctypes.Structure):
    _fields_ = [("wCode", c_ushort), ("wReserved", c_ushort), ("bstrSource", c_void_p),
                ("bstrDescription", c_void_p), ("bstrHelpFile", c_void_p),
                ("dwHelpContext", ctypes.c_ulong), ("pvReserved", c_void_p),
                ("pfnDeferredFillIn", c_void_p), ("scode", ctypes.c_long)]


def vcall(p, index, restype, *argtypes):
    vtbl = ctypes.cast(p, POINTER(c_void_p))[0]
    return ctypes.cast(ctypes.cast(vtbl, POINTER(c_void_p))[index],
                       ctypes.WINFUNCTYPE(restype, c_void_p, *argtypes))


def invoke(p, name, args=()):
    if not p:
        return "null object", None
    iid_null = GUID()
    dispid = ctypes.c_long()
    nm = ctypes.c_wchar_p(name)
    hr = vcall(p, 5, ctypes.c_long, POINTER(GUID), POINTER(ctypes.c_wchar_p), c_uint,
               ctypes.c_ulong, POINTER(ctypes.c_long))(
        p, byref(iid_null), byref(nm), 1, LOCALE_USER_DEFAULT, byref(dispid))
    if hr != 0:
        return "no such member %s (0x%08X)" % (name, hr & 0xFFFFFFFF), None
    arr = (VARIANT * max(1, len(args)))()
    for i, a in enumerate(reversed(args)):
        arr[i] = a
    dp = DISPPARAMS()
    dp.rgvarg = ctypes.cast(arr, POINTER(VARIANT))
    dp.cArgs = len(args)
    res, ei, err = VARIANT(), EXCEPINFO(), c_uint()
    hr = vcall(p, 6, ctypes.c_long, ctypes.c_long, POINTER(GUID), ctypes.c_ulong, c_ushort,
               POINTER(DISPPARAMS), POINTER(VARIANT), POINTER(EXCEPINFO), POINTER(c_uint))(
        p, dispid.value, byref(iid_null), LOCALE_USER_DEFAULT,
        DISPATCH_METHOD | DISPATCH_PROPERTYGET, byref(dp), byref(res), byref(ei), byref(err))
    if hr != 0:
        return "Invoke(%s) hr=0x%08X" % (name, hr & 0xFFFFFFFF), None
    return None, res


def s_of(v):
    return ctypes.wstring_at(v.u.bstrVal) if (v and v.vt == VT_BSTR and v.u.bstrVal) else ""


def disp_of(v):
    return v.u.pdispVal if (v and v.vt == VT_DISPATCH and v.u.pdispVal) else None


def native_om(hwnd, which=OBJID_NATIVEOM):
    ole32.CoInitializeEx(None, COINIT_APARTMENTTHREADED)
    iid = ctypes.cast(byref(guid("{00020400-0000-0000-C000-000000000046}")), POINTER(GUID))
    out = c_void_p()
    hr = oleacc.AccessibleObjectFromWindow(c_void_p(hwnd), ctypes.c_ulong(which), iid, byref(out))
    return hr, out.value


def describe(hwnd, label):
    print("=== %s  hwnd=%s ===" % (label, hex(hwnd)))
    cls = ctypes.create_unicode_buffer(256); user32.GetClassNameW(hwnd, cls, 256)
    t = ctypes.create_unicode_buffer(512); user32.GetWindowTextW(hwnd, t, 512)
    pid = wt.DWORD(); user32.GetWindowThreadProcessId(hwnd, byref(pid))
    print("    class=%s pid=%d title=%r" % (cls.value, pid.value, t.value))
    for which, wname in ((OBJID_NATIVEOM, "NATIVEOM"), (OBJID_WINDOW, "WINDOW"),
                         (OBJID_CLIENT, "CLIENT")):
        hr, p = native_om(hwnd, which)
        tag = "0x%08X" % (hr & 0xFFFFFFFF)
        if hr != 0 or not p:
            print("    %-9s hr=%s ptr=%s" % (wname, tag, hex(p or 0)))
            if which == OBJID_NATIVEOM:
                e, v = invoke(p, "LocationURL")
                print("       LocationURL -> %s / %r" % (e, s_of(v)))
            continue
        e_url, v_url = invoke(p, "LocationURL")
        e_name, v_name = invoke(p, "LocationName")
        print("    %-9s hr=%s ptr=%s  LocationURL=%r  LocationName=%r"
              % (wname, tag, hex(p), s_of(v_url), s_of(v_name)))
        # classic VBScript chain: Document.Folder.Self.Path
        e_doc, v_doc = invoke(p, "Document")
        d = disp_of(v_doc)
        if not d:
            print("       Document -> %s" % e_doc)
            continue
        e_f, v_f = invoke(d, "Folder")
        f = disp_of(v_f)
        if not f:
            print("       Document.Folder -> %s" % e_f)
            continue
        e_s, v_s = invoke(f, "Self")
        s = disp_of(v_s)
        if not s:
            print("       .Folder.Self -> %s" % e_s)
            continue
        e_p, v_p = invoke(s, "Path")
        print("       Document.Folder.Self.Path = %r   (%s)" % (s_of(v_p), e_p or "ok"))


# find on-screen cabinets
cabs = []


def walk(h):
    cls = ctypes.create_unicode_buffer(256); user32.GetClassNameW(h, cls, 256)
    if cls.value == "CabinetWClass":
        cabs.append(h)
    c = user32.GetWindow(h, 5)
    while c:
        walk(c); c = user32.GetWindow(c, 2)


@ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)
def top(h, l):
    walk(h); return True


user32.EnumWindows(top, None)
for h in cabs:
    describe(h, "embedded" if user32.GetParent(h) else "top-level")
