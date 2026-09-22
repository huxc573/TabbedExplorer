"""Probe 3 (decision probe): match ShellWindows items to on-screen explorer windows.

Answers the two questions the per-virtual-desktop tab memory depends on:
  Q1  Is our embedded (SetParent'd, `explorer.exe /n,/separate`) window present in the
      session-wide ShellWindows collection?  -> if yes we can read its folder path.
  Q2  Does LocationURL track navigation, and what does it look like for virtual folders
      (This PC / Recycle Bin) where restoring by path may need special handling?

Reuses the COM plumbing from shellwindows_match.py.
"""
import ctypes
import ctypes.wintypes as wt
from ctypes import POINTER, byref, c_void_p, c_uint, c_byte, c_ushort

ole32 = ctypes.windll.ole32
user32 = ctypes.windll.user32

COINIT_APARTMENTTHREADED = 0x2
DISPATCH_METHOD = 0x1
DISPATCH_PROPERTYGET = 0x2
LOCALE_USER_DEFAULT = 0x400
VT_I4, VT_BSTR, VT_DISPATCH = 3, 8, 9


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
    iid_null = GUID()
    dispid = ctypes.c_long()
    nm = ctypes.c_wchar_p(name)
    hr = vcall(p, 5, ctypes.c_long, POINTER(GUID), POINTER(ctypes.c_wchar_p), c_uint,
               ctypes.c_ulong, POINTER(ctypes.c_long))(
        p, byref(iid_null), byref(nm), 1, LOCALE_USER_DEFAULT, byref(dispid))
    if hr != 0:
        return "GetIDsOfNames(%s) hr=0x%08X" % (name, hr & 0xFFFFFFFF), None
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


def i_of(v):
    return v.u.lVal if v else None


def vint(i):
    v = VARIANT(); v.vt = VT_I4; v.u.lVal = i; return v


ole32.CoInitializeEx(None, COINIT_APARTMENTTHREADED)
iid_disp = guid("{00020400-0000-0000-C000-000000000046}")
p = c_void_p()
hr = ole32.CoCreateInstance(byref(guid("{9BA05972-F6A8-11CF-A442-00A0C90A8F39}")), None,
                            5, byref(iid_disp), byref(p))
if hr != 0:
    print("ShellWindows failed 0x%08X" % (hr & 0xFFFFFFFF)); raise SystemExit(1)

_, cnt = invoke(p, "Count")
total = i_of(cnt) or 0

shell = {}
for i in range(total):
    e, item = invoke(p, "Item", [vint(i)])
    if e or item is None or item.vt != VT_DISPATCH or not item.u.pdispVal:
        continue
    q = item.u.pdispVal
    _, hv = invoke(q, "HWND")
    _, uv = invoke(q, "LocationURL")
    _, nv = invoke(q, "LocationName")
    shell[i_of(hv)] = (s_of(uv), s_of(nv))

# on-screen explorer windows (top level + reparented), with owning pid
rows = []


def walk(h, depth):
    cls = ctypes.create_unicode_buffer(256)
    user32.GetClassNameW(h, cls, 256)
    if cls.value == "CabinetWClass":
        pid = wt.DWORD()
        user32.GetWindowThreadProcessId(h, byref(pid))
        t = ctypes.create_unicode_buffer(512)
        user32.GetWindowTextW(h, t, 512)
        pa = user32.GetParent(h)
        rows.append((int(h), int(pa or 0), int(pid.value), t.value))
    child = user32.GetWindow(h, 5)  # GW_CHILD
    while child:
        walk(child, depth + 1)
        child = user32.GetWindow(child, 2)  # GW_HWNDNEXT


for h in range(1, 0):
    pass


@ctypes.WINFUNCTYPE(ctypes.c_bool, c_void_p, ctypes.c_void_p)
def top(h, l):
    walk(h, 0)
    return True


user32.EnumWindows(top, None)

print("ShellWindows items with a live dispatch: %d (of %d slots)" % (len(shell), total))
print()
print("%-12s %-10s %-8s %-10s %s" % ("hwnd", "kind", "pid", "inShell", "url / title"))
for h, pa, pid, title in rows:
    hit = shell.get(h)
    kind = "TOP" if pa == 0 else "EMBED"
    print("%-12s %-10s %-8d %-10s %s" % (hex(h), kind, pid, "YES" if hit else "no",
                                         (hit[0] + "  |name=" + hit[1]) if hit else ("title=" + title)))
print()
print("-- shell items that matched nothing on screen (stale?) --")
onscreen = set(r[0] for r in rows)
for h, (u, n) in shell.items():
    if h not in onscreen:
        print("   hwnd=%s url=%s name=%s" % (hex(h) if h else None, u, n))
