# -*- coding: utf-8 -*-
"""
只读探针：确认本机 IVirtualDesktopManager 能调通，并读出
  - 当前正在看的桌面 GUID（由前台窗口反推，和 VirtualDesktop.CurrentDesktopId() 同一路子）
  - TabbedExplorer 主窗口所在的桌面 GUID、以及它是否在当前桌面
绝不调用 MoveWindowToDesktop —— 只读，不动任何窗口、不切桌面。
"""
import ctypes
from ctypes import wintypes
import sys

user32 = ctypes.windll.user32
ole32 = ctypes.windll.ole32

CLSID_VDM = "{AA509086-5CA9-4C25-8F95-589D3C07B48A}"
IID_IVDM = "{A5CD92FF-29BE-454C-8D04-D82879FB3F1B}"


class GUID(ctypes.Structure):
    _fields_ = [("Data1", ctypes.c_ulong), ("Data2", ctypes.c_ushort),
                ("Data3", ctypes.c_ushort), ("Data4", ctypes.c_ubyte * 8)]

    def __str__(self):
        d4 = "".join("%02X" % b for b in self.Data4)
        return "{%08X-%04X-%04X-%s-%s}" % (self.Data1, self.Data2, self.Data3,
                                           d4[:4], d4[4:])


def guid_from_str(s):
    g = GUID()
    hr = ole32.CLSIDFromString(ctypes.c_wchar_p(s), ctypes.byref(g))
    if hr != 0:
        raise OSError("CLSIDFromString failed 0x%08X" % (hr & 0xFFFFFFFF))
    return g


# IUnknown 之后：0=QueryInterface 1=AddRef 2=Release 3=IsWindowOnCurrentVirtualDesktop
#                 4=GetWindowDesktopId 5=MoveWindowToDesktop
FN_IS_ON = ctypes.WINFUNCTYPE(ctypes.c_long, ctypes.c_void_p, ctypes.c_void_p, ctypes.POINTER(ctypes.c_int))
FN_GET_ID = ctypes.WINFUNCTYPE(ctypes.c_long, ctypes.c_void_p, ctypes.c_void_p, ctypes.POINTER(GUID))


def main():
    # 必须先起套间，否则 CoCreateInstance 回 CO_E_NOTINITIALIZED(0x800401F0)。
    # 我们这边是 VSCode 插件 Python 主线程，得自己去开（C# 那边 Main 带 [STAThread]，CLR 自动开）。
    hr_init = ole32.CoInitializeEx(None, 2)   # COINIT_APARTMENTTHREADED
    print("CoInitializeEx hr=0x%08X" % (hr_init & 0xFFFFFFFF))

    vdm = ctypes.c_void_p()
    hr = ole32.CoCreateInstance(ctypes.byref(guid_from_str(CLSID_VDM)), None, 1,
                                ctypes.byref(guid_from_str(IID_IVDM)),
                                ctypes.byref(vdm))
    print("CoCreateInstance hr=0x%08X  ptr=%s" % (hr & 0xFFFFFFFF, vdm))
    if hr != 0 or not vdm:
        print("接口不可用")
        return 1

    vtbl = ctypes.cast(ctypes.cast(vdm, ctypes.POINTER(ctypes.c_void_p))[0],
                       ctypes.POINTER(ctypes.c_void_p))
    is_on = FN_IS_ON(vtbl[3])
    get_id = FN_GET_ID(vtbl[4])
    self_p = ctypes.cast(vdm, ctypes.c_void_p)

    def desktop_of(h, label):
        g = GUID()
        r = get_id(self_p, ctypes.c_void_p(h), ctypes.byref(g))
        on = ctypes.c_int(-1)
        r2 = is_on(self_p, ctypes.c_void_p(h), ctypes.byref(on))
        print("  %-30s hwnd=0x%-8X GetWindowDesktopId hr=0x%08X -> %-40s onCurrent=%s"
              % (label, h, r & 0xFFFFFFFF, g, "?" if r2 != 0 else bool(on.value)))

    print("\n当前桌面（由前台窗口反推，与 C# CurrentDesktopId 同路）:")
    desktop_of(user32.GetForegroundWindow(), "GetForegroundWindow")
    desktop_of(user32.GetShellWindow(), "GetShellWindow(兜底)")

    print("\nTabbedExplorer 主窗口:")
    found = []

    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def cb(h, l):
        pid = wintypes.DWORD()
        user32.GetWindowThreadProcessId(h, ctypes.byref(pid))
        buf = ctypes.create_unicode_buffer(256)
        user32.GetClassNameW(h, buf, 256)
        if "WindowsForms10" in buf.value and user32.IsWindowVisible(h):
            r = wintypes.RECT()
            user32.GetWindowRect(h, ctypes.byref(r))
            if r.right - r.left > 600 and r.bottom - r.top > 400:
                found.append((h, pid.value))
        return True

    user32.EnumWindows(cb, 0)
    if not found:
        print("  （没找到；程序可能没在跑）")
    for h, pid in found:
        desktop_of(h, "TabbedExplorer pid=%d" % pid)
    return 0


if __name__ == "__main__":
    sys.exit(main())
