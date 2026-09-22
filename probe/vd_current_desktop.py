# -*- coding: utf-8 -*-
"""
只读探针：验证「未公开的 GetCurrentDesktop」这条路在本机能不能跑，并和公开的
「由前台窗口反推」做交叉比对。

为什么要它：公开 API 根本没有「当前桌面是哪张」这个查询，只能从前台窗口反推；
而 `GetWindowDesktopId(fg)` 实测会**间歇性失败**（TabbedExplorer 16:10:26 / 16:10:36 / 16:11:21
三次「取不到当前桌面，不搬」→ 随后抢前台把用户从别的桌面拽走）。
这条未公开路径是那个问题的**权威解**，但它是未公开接口，必须先证明它在活体上能跑。

**只读**：只调 GetCurrentDesktop / GetId / GetWindowDesktopId / IsWindowOnCurrentVirtualDesktop，
绝不调 MoveViewToDesktop / SwitchDesktop / MoveWindowToDesktop。
"""
import ctypes
from ctypes import wintypes
import sys

ole32 = ctypes.WinDLL("ole32", use_last_error=True)
user32 = ctypes.WinDLL("user32", use_last_error=True)
CLSCTX_ALL = 23


class GUID(ctypes.Structure):
    _fields_ = [("Data1", ctypes.c_uint32), ("Data2", ctypes.c_uint16),
                ("Data3", ctypes.c_uint16), ("Data4", ctypes.c_ubyte * 8)]

    def __str__(self):
        d4 = "".join("%02x" % b for b in self.Data4)
        return "{%08x-%04x-%04x-%s-%s}" % (self.Data1, self.Data2, self.Data3, d4[:4], d4[4:])

    def short(self):
        return "%08x" % self.Data1

    def is_null(self):
        return (self.Data1 == 0 and self.Data2 == 0 and self.Data3 == 0
                and not any(self.Data4))


def guid(s):
    g = GUID()
    hr = ole32.CLSIDFromString(ctypes.c_wchar_p(s), ctypes.byref(g))
    if hr != 0:
        raise OSError("CLSIDFromString(%s) hr=0x%08X" % (s, hr & 0xFFFFFFFF))
    return g


CLSID_ImmersiveShell = guid("{C2F03A33-21F5-47FA-B4BB-156362A2F239}")
CLSID_VDMInternal = guid("{C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B}")
CLSID_VDManager = guid("{AA509086-5CA9-4C25-8F95-589D3C07B48A}")
IID_IServiceProvider = guid("{6D5140C1-7436-11CE-8034-00AA006009FA}")
IID_VDMInternal = guid("{F31574D6-B682-4CDC-BD56-1827860ABEC6}")
IID_IVirtualDesktop = guid("{FF72FFDD-BE7E-43FC-9C03-AD81681E88E4}")
IID_VDManager = guid("{A5CD92FF-29BE-454C-8D04-D82879FB3F1B}")

SHORT = "%-16s"


def hx(hr):
    return "0x%08X" % (hr & 0xFFFFFFFF)


def vtbl(ptr):
    return ctypes.cast(ctypes.cast(ptr, ctypes.POINTER(ctypes.c_void_p))[0],
                       ctypes.POINTER(ctypes.c_void_p))


def fn(ptr, idx, restype, *argtypes):
    return ctypes.WINFUNCTYPE(restype, ctypes.c_void_p, *argtypes)(vtbl(ptr)[idx])


def main():
    hr = ole32.CoInitializeEx(None, 2)
    print("CoInitializeEx ->", hx(hr))

    shell = ctypes.c_void_p()
    hr = ole32.CoCreateInstance(ctypes.byref(CLSID_ImmersiveShell), None, CLSCTX_ALL,
                                ctypes.byref(IID_IServiceProvider), ctypes.byref(shell))
    print("CoCreateInstance(ImmersiveShell) ->", hx(hr), "ptr=", bool(shell))
    if hr != 0 or not shell:
        print("!! 未公开路径不可用，这条路不能走")
        return 1

    out = ctypes.c_void_p()
    svc = GUID.from_buffer_copy(CLSID_VDMInternal)
    riid = GUID.from_buffer_copy(IID_VDMInternal)
    f = fn(shell, 3, ctypes.c_long, ctypes.POINTER(GUID), ctypes.POINTER(GUID),
           ctypes.POINTER(ctypes.c_void_p))
    hr = f(shell, ctypes.byref(svc), ctypes.byref(riid), ctypes.byref(out))
    print("QueryService(IVirtualDesktopManagerInternal) ->", hx(hr), "ptr=", bool(out))
    if hr != 0 or not out:
        print("!! 拿不到 VDMInternal")
        return 1
    vdmi = out

    # --- 未公开：GetCurrentDesktop (vtable 6) ---
    d = ctypes.c_void_p()
    hr = fn(vdmi, 6, ctypes.c_long, ctypes.POINTER(ctypes.c_void_p))(vdmi, ctypes.byref(d))
    print("GetCurrentDesktop ->", hx(hr), "ptr=", bool(d))
    undocumented = None
    if hr == 0 and d:
        g = GUID()
        hr2 = fn(d, 4, ctypes.c_long, ctypes.POINTER(GUID))(d, ctypes.byref(g))
        print("IVirtualDesktop::GetId ->", hx(hr2), str(g))
        if hr2 == 0 and not g.is_null():
            undocumented = str(g)
        fn(d, 2, ctypes.c_long)(d)   # Release

    # --- 公开：由前台窗口反推 ---
    vdm = ctypes.c_void_p()
    hr = ole32.CoCreateInstance(ctypes.byref(CLSID_VDManager), None, CLSCTX_ALL,
                                ctypes.byref(IID_VDManager), ctypes.byref(vdm))
    public = None
    if hr == 0 and vdm:
        fg = user32.GetForegroundWindow()
        g = GUID()
        hr3 = fn(vdm, 4, ctypes.c_long, ctypes.c_void_p, ctypes.POINTER(GUID))(
            vdm, ctypes.c_void_p(fg), ctypes.byref(g))
        print("GetWindowDesktopId(foreground=0x%X) ->" % fg, hx(hr3), str(g))
        if hr3 == 0 and not g.is_null():
            public = str(g)
        on = ctypes.c_int(-1)
        fn(vdm, 3, ctypes.c_long, ctypes.c_void_p, ctypes.POINTER(ctypes.c_int))(
            vdm, ctypes.c_void_p(fg), ctypes.byref(on))

    print()
    print("未公开 GetCurrentDesktop =", undocumented)
    print("公开 前台反推            =", public)
    if undocumented and public:
        print("一致" if undocumented == public else "!! 不一致")
    return 0


if __name__ == "__main__":
    sys.exit(main())
