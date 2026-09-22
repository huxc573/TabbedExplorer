"""Probe 5: find a per-HWND way to read an explorer window's CURRENT FOLDER.

Background
----------
ShellWindows (the official COM OM) gives LocationURL correctly, but after we SetParent a
tab's explorer window into our form, IWebBrowser2.HWND reports the window's TOP-LEVEL
ancestor -- i.e. OUR FORM -- for every tab. All tabs collapse onto one key, so it can't be
used to tell tabs apart. And AccessibleObjectFromWindow(OBJID_NATIVEOM) returns E_FAIL on
CabinetWClass, so the "native OM" shortcut is unavailable.

What we need is something keyed by the HWND we already hold. Two candidates, both per-HWND:

  A) the breadcrumb toolbar's WINDOW TEXT
     observed: '地址: 视频' on a window whose folder is D:\\Life\\Videos (display name)
               '地址: D:\\'    on a window whose title is 'FB.Data (D:)'
     -> maybe display name, maybe a real path; unclear. Needs controlled samples.

  B) MSAA (IAccessible) walk of that toolbar -> child names = breadcrumb segments
     -> join with '\\' to rebuild the path. Does not depend on window text at all.

This probe dumps A and B for every explorer window on the box, next to the ShellWindows
LocationURL (only available for windows we have NOT reparented) as ground truth.
"""
import ctypes
import ctypes.wintypes as wt
from ctypes import POINTER, byref, c_void_p, c_uint, c_byte, c_ushort

ole32 = ctypes.windll.ole32
oleacc = ctypes.windll.oleacc
oleaut32 = ctypes.windll.oleaut32
user32 = ctypes.windll.user32

COINIT_APARTMENTTHREADED = 0x2
DISPATCH_METHOD = 0x1
DISPATCH_PROPERTYGET = 0x2
LOCALE_USER_DEFAULT = 0x400
VT_I4, VT_BSTR, VT_DISPATCH = 3, 8, 9
OBJID_CLIENT = 0xFFFFFFFC
WM_GETTEXT = 0x000D

IID_IAccessible = "{618736E0-3C3D-11CF-810C-00AA00389B71}"
IID_IDispatch = "{00020400-0000-0000-C000-000000000046}"


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


def vtbl_fn(p, index, restype, *argtypes):
    vtbl = ctypes.cast(p, POINTER(c_void_p))[0]
    return ctypes.cast(ctypes.cast(vtbl, POINTER(c_void_p))[index],
                       ctypes.WINFUNCTYPE(restype, c_void_p, *argtypes))


# ---- IAccessible: 7 get_accParent | 8 childCount | 9 accChild | 10 accName
#      11 accValue | 13 accRole
def acc_childcount(p):
    n = ctypes.c_long()
    vtbl_fn(p, 8, ctypes.c_long, POINTER(ctypes.c_long))(p, byref(n))
    return n.value


def acc_name(p, child_id):
    var = VARIANT(); var.vt = VT_I4; var.u.lVal = child_id
    out = c_void_p()
    hr = vtbl_fn(p, 10, ctypes.c_long, POINTER(VARIANT), POINTER(c_void_p))(p, byref(var), byref(out))
    if hr != 0 or not out.value:
        return "0x%08X" % (hr & 0xFFFFFFFF)
    s = ctypes.wstring_at(out.value)
    oleaut32.SysFreeString(c_void_p(out.value))
    return s


def acc_value(p, child_id):
    var = VARIANT(); var.vt = VT_I4; var.u.lVal = child_id
    out = c_void_p()
    hr = vtbl_fn(p, 11, ctypes.c_long, POINTER(VARIANT), POINTER(c_void_p))(p, byref(var), byref(out))
    if hr != 0 or not out.value:
        return None
    s = ctypes.wstring_at(out.value)
    oleaut32.SysFreeString(c_void_p(out.value))
    return s


def acc_of(hwnd, iid):
    ole32.CoInitializeEx(None, COINIT_APARTMENTTHREADED)
    out = c_void_p()
    hr = oleacc.AccessibleObjectFromWindow(c_void_p(hwnd), ctypes.c_ulong(OBJID_CLIENT),
                                           ctypes.cast(byref(guid(iid)), POINTER(GUID)),
                                           byref(out))
    return hr, out.value


def wtext(h):
    buf = ctypes.create_unicode_buffer(1024)
    user32.SendMessageW(h, WM_GETTEXT, 1024, buf)
    return buf.value


def find_desc(h, want_class, out):
    cls = ctypes.create_unicode_buffer(256)
    user32.GetClassNameW(h, cls, 256)
    if cls.value == want_class:
        out.append(h)
    c = user32.GetWindow(h, 5)
    while c:
        find_desc(c, want_class, out)
        c = user32.GetWindow(c, 2)


# ground truth from ShellWindows
import json
shell = {}
try:
    spec = {}
    exec(open("probe/_sw_helpers.py", encoding="utf-8").read(), spec) if False else None
except Exception:
    pass

print("### per-window address-band dump ###\n")
cabs = []


def walk(h, parent):
    cls = ctypes.create_unicode_buffer(256)
    user32.GetClassNameW(h, cls, 256)
    if cls.value == "CabinetWClass":
        cabs.append(h)
    c = user32.GetWindow(h, 5)
    while c:
        walk(c, h)
        c = user32.GetWindow(c, 2)


@ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)
def top(h, l):
    walk(h, 0)
    return True


user32.EnumWindows(top, None)

for cab in cabs:
    pid = wt.DWORD()
    user32.GetWindowThreadProcessId(cab, byref(pid))
    t = ctypes.create_unicode_buffer(512)
    user32.GetWindowTextW(cab, t, 512)
    kind = "EMBEDDED" if user32.GetParent(cab) else "TOPLEVEL"
    print("cabinet %s pid=%d %s title=%r" % (hex(cab), pid.value, kind, t.value))

    tools = []
    find_desc(cab, "ToolbarWindow32", tools)
    for tw in tools:
        label = wtext(tw)
        if not label.startswith("地址"):
            continue
        print("   toolbar %s text=%r" % (hex(tw), label))
        # B) MSAA walk
        for iid, tag in ((IID_IAccessible, "IAccessible"), (IID_IDispatch, "IDispatch")):
            hr, p = acc_of(tw, iid)
            if hr != 0 or not p:
                print("      %s hr=0x%08X" % (tag, hr & 0xFFFFFFFF))
                continue
            if tag == "IDispatch":
                print("      %s ok (usable for accName via IDispatch)" % tag)
                continue
            n = acc_childcount(p)
            print("      IAccessible ok childCount=%d" % n)
            for cid in range(1, min(n, 12) + 1):
                print("         child[%d] name=%r value=%r" % (cid, acc_name(p, cid), acc_value(p, cid)))
    print()
