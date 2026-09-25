"""复现「从开始菜单点文件夹」那条路 + 量闪多久（只读采样，不改任何窗口）。

为什么要专门造这一条：开始菜单是**在 shell 进程里**点开的，那扇窗属于 shell 自己，
走的是 `DesktopHub` 的「shell 转生」而不是普通收编。用 `explorer.exe <path>` 起的是
**新进程**，走的是普通收编 —— 两条路的表现完全不同，拿后者量前者等于没量。

触发器：`Shell.Application`（CLSID 13709620-...）是 explorer.exe 托管的**进程外**组件，
`FolderItem.InvokeVerb("open")` 是让 **shell 自己**去开这个文件夹，跟开始菜单同路。

采样：每 10ms 全屏数一遍 CabinetWClass，记 (可见, 扩展样式) 的变化 ——
「从第一次可见到窗口消失」就是肉眼看到的闪。
纯读：不点鼠标、不动光标、不抢前台（除了被触发的那个文件夹窗）。
用法：python startmenu_open_probe.py [要打开的目录] [采样秒数]
"""
import ctypes
import ctypes.wintypes as wt
import sys
import time
from ctypes import POINTER, byref, c_void_p, c_uint, c_long, c_ushort, c_byte

ole32 = ctypes.windll.ole32
oleaut32 = ctypes.windll.oleaut32
user32 = ctypes.windll.user32

COINIT_APARTMENTTHREADED = 0x2
CLSCTX_LOCAL_SERVER = 0x4
DISPATCH_METHOD = 0x1
DISPATCH_PROPERTYGET = 0x2
LOCALE_USER_DEFAULT = 0x400
VT_I4, VT_BSTR, VT_DISPATCH = 3, 8, 9

GWL_EXSTYLE = -20
WS_EX_TOOLWINDOW = 0x00000080
WS_EX_APPWINDOW = 0x00040000
WS_EX_LAYERED = 0x00080000

GetExStyle = user32.GetWindowLongPtrW
GetExStyle.argtypes = [wt.HWND, ctypes.c_int]
GetExStyle.restype = ctypes.c_longlong
user32.GetClassNameW.argtypes = [wt.HWND, wt.LPWSTR, ctypes.c_int]
user32.GetWindowThreadProcessId.argtypes = [wt.HWND, POINTER(wt.DWORD)]


class GUID(ctypes.Structure):
    _fields_ = [("d1", ctypes.c_ulong), ("d2", c_ushort), ("d3", c_ushort), ("d4", c_byte * 8)]


def guid(s):
    g = GUID()
    ole32.CLSIDFromString(ctypes.c_wchar_p(s), byref(g))
    return g


class VARIANT(ctypes.Structure):
    class _U(ctypes.Union):
        _fields_ = [("llVal", ctypes.c_longlong), ("lVal", c_long), ("wReserved", c_ushort),
                    ("bstrVal", c_void_p), ("punkVal", c_void_p), ("pdispVal", c_void_p)]
    _fields_ = [("vt", c_ushort), ("r1", c_ushort), ("r2", c_ushort), ("r3", c_ushort), ("u", _U)]


class DISPPARAMS(ctypes.Structure):
    _fields_ = [("rgvarg", POINTER(VARIANT)), ("rgdispidNamedArgs", c_void_p),
                ("cArgs", c_uint), ("cNamedArgs", c_uint)]


class EXCEPINFO(ctypes.Structure):
    _fields_ = [("wCode", c_ushort), ("wReserved", c_ushort), ("bstrSource", c_void_p),
                ("bstrDescription", c_void_p), ("bstrHelpFile", c_void_p),
                ("dwHelpContext", ctypes.c_ulong), ("pvReserved", c_void_p),
                ("pfnDeferredFillIn", c_void_p), ("scode", c_long)]


def vcall(p, index, restype, *argtypes):
    vtbl = ctypes.cast(p, POINTER(c_void_p))[0]
    return ctypes.cast(ctypes.cast(vtbl, POINTER(c_void_p))[index],
                       ctypes.WINFUNCTYPE(restype, c_void_p, *argtypes))


def invoke(p, name, args=(), prop=True):
    iid = GUID()
    dispid = c_long()
    nm = ctypes.c_wchar_p(name)
    hr = vcall(p, 5, c_long, POINTER(GUID), POINTER(ctypes.c_wchar_p), c_uint,
               ctypes.c_ulong, POINTER(c_long))(p, byref(iid), byref(nm), 1,
                                                LOCALE_USER_DEFAULT, byref(dispid))
    if hr != 0:
        return ("GetIDsOfNames(%s) hr=0x%08X" % (name, hr & 0xFFFFFFFF), None)
    arr = (VARIANT * max(1, len(args)))()
    for i, a in enumerate(reversed(args)):
        arr[i] = a
    dp = DISPPARAMS()
    dp.rgvarg = ctypes.cast(arr, POINTER(VARIANT))
    dp.cArgs = len(args)
    res = VARIANT()
    ei = EXCEPINFO()
    err = c_uint()
    flags = (DISPATCH_PROPERTYGET | DISPATCH_METHOD) if prop else DISPATCH_METHOD
    hr = vcall(p, 6, c_long, c_long, POINTER(GUID), ctypes.c_ulong, c_ushort,
               POINTER(DISPPARAMS), POINTER(VARIANT), POINTER(EXCEPINFO),
               POINTER(c_uint))(p, dispid.value, byref(iid), LOCALE_USER_DEFAULT, flags,
                                byref(dp), byref(res), byref(ei), byref(err))
    if hr != 0:
        return ("Invoke(%s) hr=0x%08X scode=0x%08X" % (name, hr & 0xFFFFFFFF, ei.scode & 0xFFFFFFFF), None)
    return (None, res)


def vstr(s):
    v = VARIANT()
    v.vt = VT_BSTR
    v.u.bstrVal = oleaut32.SysAllocString(ctypes.c_wchar_p(s))
    return v


def shell_pid():
    h = user32.FindWindowW("Shell_TrayWnd", None)
    pid = wt.DWORD()
    user32.GetWindowThreadProcessId(h, byref(pid))
    return pid.value


def open_in_shell(path):
    ole32.CoInitializeEx(None, COINIT_APARTMENTTHREADED)
    clsid = guid("{13709620-C279-11CE-A49E-444553540000}")     # Shell.Application
    iid = guid("{00020400-0000-0000-C000-000000000046}")
    p = c_void_p()
    hr = ole32.CoCreateInstance(byref(clsid), None, CLSCTX_LOCAL_SERVER, byref(iid), byref(p))
    if hr != 0:
        print("CoCreateInstance(Shell.Application) hr=0x%08X" % (hr & 0xFFFFFFFF))
        return False
    err, ns = invoke(p, "NameSpace", [vstr(path)])
    if err or ns.vt != VT_DISPATCH:
        print("NameSpace ->", err, None if ns is None else ns.vt)
        return False
    err, selfitem = invoke(ns.u.pdispVal, "Self")
    if err or selfitem.vt != VT_DISPATCH:
        print("Self ->", err, None if selfitem is None else selfitem.vt)
        return False
    err, _ = invoke(selfitem.u.pdispVal, "InvokeVerb", [vstr("open")], prop=False)
    print("InvokeVerb(open) ->", err)
    return err is None


CPID = shell_pid()
print("shell 进程 pid = %d（被它开的窗口就是「shell 自己的」）" % CPID)

path = sys.argv[1] if len(sys.argv) > 1 else r"D:\Dev\!tmp"
duration = float(sys.argv[2]) if len(sys.argv) > 2 else 8.0

seen = {}
t0 = time.time()
fired = [False]

ENUMPROC = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
user32.EnumWindows.argtypes = [ENUMPROC, wt.LPARAM]

while time.time() - t0 < duration:
    el = time.time() - t0
    if not fired[0] and el >= 1.5:
        fired[0] = True
        print("%7.3fs 触发 shell 打开 %s" % (el, path))
        open_in_shell(path)
    if fired[0]:
        def cb(h, _):
            b = ctypes.create_unicode_buffer(64)
            user32.GetClassNameW(h, b, 64)
            if b.value in ("CabinetWClass", "ExploreWClass"):
                ex = GetExStyle(h, GWL_EXSTYLE) & 0xFFFFFFFF
                vis = 1 if user32.IsWindowVisible(h) else 0
                pid = wt.DWORD()
                user32.GetWindowThreadProcessId(h, byref(pid))
                prev = seen.get(h)
                cur = (vis, ex)
                if prev != cur:
                    seen[h] = cur
                    fl = []
                    if ex & WS_EX_TOOLWINDOW: fl.append("TOOLWIN")
                    if ex & WS_EX_APPWINDOW: fl.append("APPWIN")
                    if ex & WS_EX_LAYERED: fl.append("LAYERED")
                    first = "首次" if prev is None else "变化"
                    print("%7.3fs %s hwnd=0x%-6X pid=%-6d %s vis=%d ex=0x%08X [%s]"
                          % (el, first, h, pid.value, "SHELL" if pid.value == CPID else "other",
                             vis, ex, ",".join(fl) or "-"))
            return True
        user32.EnumWindows(ENUMPROC(cb), 0)
    time.sleep(0.01)

print("采样结束")
