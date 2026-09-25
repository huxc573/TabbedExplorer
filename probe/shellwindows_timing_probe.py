"""只读：触发 shell 开一扇文件夹窗，然后在几个时间点做**全量**枚举，打印清单里
每一项的 (index, hwnd, url) —— 看那扇新窗什么时候进清单、URL 什么时候有值。

`ShellWindows` 是 explorer.exe 托管的进程外组件；实测它的清单里绝大多数项是
**读不出来的幽灵项**（`Item(i)` 回 `VT_DISPATCH` + 空指针），只有少数是真的。
所以这里逐项打印，别只看 Count。

用法：python shellwindows_timing_probe.py [目录] [采样秒数]
"""
import ctypes
import ctypes.wintypes as wt
import sys
import time
from ctypes import POINTER, byref, c_void_p, c_uint, c_long, c_ushort, c_byte, c_int

ole32 = ctypes.windll.ole32
oleaut32 = ctypes.windll.oleaut32
user32 = ctypes.windll.user32

oleaut32.SysAllocString.restype = c_void_p
oleaut32.SysAllocString.argtypes = [ctypes.c_wchar_p]
user32.FindWindowW.restype = wt.HWND
user32.FindWindowW.argtypes = [wt.LPCWSTR, wt.LPCWSTR]
user32.GetClassNameW.argtypes = [wt.HWND, wt.LPWSTR, c_int]
user32.GetWindowThreadProcessId.argtypes = [wt.HWND, POINTER(wt.DWORD)]
user32.PostMessageW.argtypes = [wt.HWND, c_uint, c_void_p, c_void_p]
GetExStyle = user32.GetWindowLongPtrW
GetExStyle.argtypes = [wt.HWND, c_int]
GetExStyle.restype = ctypes.c_longlong

COINIT_APARTMENTTHREADED = 0x2
CLSCTX_ALL = 0x17
CLSCTX_LOCAL_SERVER = 0x4
CLSCTX_INPROC_SERVER = 0x1
DISPATCH_METHOD = 0x1
DISPATCH_PROPERTYGET = 0x2
LOCALE_USER_DEFAULT = 0x400
VT_I4, VT_BSTR, VT_DISPATCH, VT_VARIANT = 3, 8, 9, 12
VT_BYREF = 0x4000


class GUID(ctypes.Structure):
    _fields_ = [("d1", ctypes.c_ulong), ("d2", c_ushort), ("d3", c_ushort), ("d4", c_byte * 8)]


def guid(s):
    g = GUID()
    ole32.CLSIDFromString(ctypes.c_wchar_p(s), byref(g))
    return g


class VARIANT(ctypes.Structure):
    class _U(ctypes.Union):
        _fields_ = [("llVal", ctypes.c_longlong), ("lVal", c_int), ("bstrVal", c_void_p),
                    ("pdispVal", c_void_p), ("pad", c_byte * 16)]
    _fields_ = [("vt", c_ushort), ("r1", c_ushort), ("r2", c_ushort), ("r3", c_ushort), ("u", _U)]


class DISPPARAMS(ctypes.Structure):
    _fields_ = [("rgvarg", c_void_p), ("rgdispidNamedArgs", c_void_p),
                ("cArgs", c_uint), ("cNamedArgs", c_uint)]


class EXCEPINFO(ctypes.Structure):
    _fields_ = [("wCode", c_ushort), ("wReserved", c_ushort), ("bstrSource", c_void_p),
                ("bstrDescription", c_void_p), ("bstrHelpFile", c_void_p),
                ("dwHelpContext", ctypes.c_ulong), ("pvReserved", c_void_p),
                ("pfnDeferredFillIn", c_void_p), ("scode", c_int)]


def vcall(p, index, restype, *at):
    vt = ctypes.cast(p, POINTER(c_void_p))[0]
    return ctypes.cast(ctypes.cast(vt, POINTER(c_void_p))[index],
                       ctypes.WINFUNCTYPE(restype, c_void_p, *at))


def dispid(p, name):
    iid = GUID()
    d = c_int()
    nm = ctypes.c_wchar_p(name)
    hr = vcall(p, 5, ctypes.c_long, POINTER(GUID), POINTER(ctypes.c_wchar_p), c_uint,
               ctypes.c_ulong, POINTER(c_int))(p, byref(iid), byref(nm), 1,
                                               LOCALE_USER_DEFAULT, byref(d))
    return hr, d.value


def invoke_raw(p, did, flags, rgv, n):
    dp = DISPPARAMS()
    dp.rgvarg = rgv
    dp.cArgs = n
    res = VARIANT()
    ei = EXCEPINFO()
    err = c_uint()
    iid = GUID()
    hr = vcall(p, 6, ctypes.c_long, c_int, POINTER(GUID), ctypes.c_ulong, c_ushort,
               POINTER(DISPPARAMS), POINTER(VARIANT), POINTER(EXCEPINFO), POINTER(c_uint))(
        p, did, byref(iid), LOCALE_USER_DEFAULT, flags, byref(dp), byref(res), byref(ei), byref(err))
    return hr, res


def vstr(s):
    v = VARIANT()
    v.vt = VT_BSTR
    v.u.bstrVal = oleaut32.SysAllocString(s)
    return v


def shell_pid():
    h = user32.FindWindowW("Shell_TrayWnd", None)
    pid = wt.DWORD()
    user32.GetWindowThreadProcessId(h, byref(pid))
    return pid.value


def open_in_shell(path):
    clsid = guid("{13709620-C279-11CE-A49E-444553540000}")
    iid = guid("{00020400-0000-0000-C000-000000000046}")
    p = c_void_p()
    hr = ole32.CoCreateInstance(byref(clsid), None,
                                CLSCTX_LOCAL_SERVER | CLSCTX_INPROC_SERVER, byref(iid), byref(p))
    if hr != 0:
        print("  Shell.Application hr=0x%08X" % (hr & 0xFFFFFFFF))
        return
    _, did = dispid(p.value, "Explore")
    hr2, _ = invoke_raw(p.value, did, DISPATCH_METHOD,
                        ctypes.cast((VARIANT * 1)(vstr(path.replace('/', '\\'))), c_void_p).value, 1)
    print("  Explore hr=0x%08X" % (hr2 & 0xFFFFFFFF))


def scan(label):
    """全量枚举一遍（每次重建根对象，排除代理缓存的影响），打印可读项的 hwnd/url。"""
    cl = guid("{9BA05972-F6A8-11CF-A442-00A0C90A8F39}")
    iid = guid("{00020400-0000-0000-C000-000000000046}")
    root = c_void_p()
    t0 = time.time()
    hr = ole32.CoCreateInstance(byref(cl), None, CLSCTX_ALL, byref(iid), byref(root))
    if hr != 0:
        print("  [%s] ShellWindows hr=0x%08X" % (label, hr & 0xFFFFFFFF))
        return None
    pv = root.value
    _, didc = dispid(pv, "Count")
    _, didi = dispid(pv, "Item")
    _, cnt = invoke_raw(pv, didc, DISPATCH_PROPERTYGET, None, 0)
    total = cnt.u.lVal
    inner = VARIANT()
    inner.vt = VT_I4
    outer = VARIANT()
    outer.vt = VT_VARIANT | VT_BYREF
    outer.u.pdispVal = ctypes.cast(byref(inner), c_void_p).value
    ab = (VARIANT * 1)()
    ab[0] = outer
    argp = ctypes.cast(ab, c_void_p).value
    rows = []
    for i in range(total):
        inner.u.lVal = i
        hr2, res = invoke_raw(pv, didi, DISPATCH_METHOD, argp, 1)
        if hr2 != 0 or res.vt != VT_DISPATCH or not res.u.pdispVal:
            continue
        q = res.u.pdispVal
        _, dh = dispid(q, "HWND")
        _, du = dispid(q, "LocationURL")
        _, rh = invoke_raw(q, dh, DISPATCH_PROPERTYGET, None, 0)
        _, ru = invoke_raw(q, du, DISPATCH_PROPERTYGET, None, 0)
        hv = rh.u.llVal if rh.vt == 20 else (rh.u.lVal if rh.vt == 3 else None)
        uv = ctypes.wstring_at(ru.u.bstrVal) if (ru.vt == VT_BSTR and ru.u.bstrVal) else ""
        rows.append((i, hv, uv))
    print("  [%s] Count=%d 可读=%d 耗时%.0fms" % (label, total, len(rows), (time.time() - t0) * 1000))
    for i, hv, uv in rows:
        print("       [%3d] hwnd=%-9s url=%s" % (i, hex(hv) if hv else "-", uv or "(空)"))
    return rows


CPID = shell_pid()
path = sys.argv[1] if len(sys.argv) > 1 else r"D:\Dev\!tmp"
print("shell pid=%d 目标=%s" % (CPID, path))

base = set()
ENUMPROC = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
user32.EnumWindows.argtypes = [ENUMPROC, wt.LPARAM]

def cabs():
    out = []
    def cb(h, _):
        b = ctypes.create_unicode_buffer(64)
        user32.GetClassNameW(h, b, 64)
        if b.value in ("CabinetWClass", "ExploreWClass"):
            p = wt.DWORD()
            user32.GetWindowThreadProcessId(h, byref(p))
            out.append((h, p.value))
        return True
    user32.EnumWindows(ENUMPROC(cb), 0)
    return out

for h, _ in cabs():
    base.add(h)

ole32.CoInitializeEx(None, COINIT_APARTMENTTHREADED)
print("触发前：")
scan("before")
print("--- 触发 ---")
t0 = time.time()
open_in_shell(path)
target = None
times = [0.30, 0.60, 1.00, 1.60, 2.50, 3.50]
for k, at in enumerate(times):
    while time.time() - t0 < at:
        time.sleep(0.01)
    tgt = None
    for h, pid in cabs():
        if h not in base and pid == CPID:
            tgt = h
    vis = user32.IsWindowVisible(tgt) if tgt else None
    print("--- t=%.2fs  目标窗口=%s 可见=%s ---"
          % (time.time() - t0, hex(tgt) if tgt else "-", vis))
    if tgt:
        target = tgt
    scan("t=%.2f" % (time.time() - t0))

if target and user32.IsWindow(target):
    user32.PostMessageW(target, 0x0112, 0xF060, 0)
    print("已请求关闭 hwnd=0x%X" % target)
