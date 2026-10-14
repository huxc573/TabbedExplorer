# -*- coding: utf-8 -*-
"""只读探针：SHCreateItemFromParsingName / SHParseDisplayName 各种 MTP 写法。

这是「explorer 地址栏是怎么解析 MTP 的」最接近的复刻：
地址栏内部最终也走 SHCreateItemFromParsingName（`SHCreateItemWithParent` 那条）。
只读，不开窗、不写文件。
"""
import ctypes
from ctypes import wintypes, POINTER, byref, c_void_p

ole32 = ctypes.windll.ole32
shell32 = ctypes.windll.shell32


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


IID_IShellItem = guid('43826D1E-E718-42EE-BC55-A1E261C37BFE')
SIGDN = 0x80028000

ole32.CoTaskMemFree.argtypes = [c_void_p]
ole32.CoInitialize.argtypes = [c_void_p]
shell32.SHCreateItemFromParsingName.argtypes = [
    wintypes.LPCWSTR, c_void_p, POINTER(GUID), POINTER(c_void_p)]
shell32.SHCreateItemFromParsingName.restype = ctypes.c_long
shell32.SHParseDisplayName.argtypes = [
    wintypes.LPCWSTR, c_void_p, POINTER(c_void_p), wintypes.DWORD, POINTER(wintypes.DWORD)]
shell32.SHParseDisplayName.restype = ctypes.c_long
shell32.SHGetNameFromIDList.argtypes = [c_void_p, ctypes.c_int, POINTER(c_void_p)]
shell32.SHGetNameFromIDList.restype = ctypes.c_long


def vfn(p, i):
    return ctypes.cast(p, POINTER(POINTER(c_void_p))).contents[i]


def item_name(item):
    """IShellItem::GetDisplayName (vtable 5)"""
    Fn = ctypes.WINFUNCTYPE(ctypes.c_long, c_void_p, ctypes.c_int, POINTER(c_void_p))
    fn = Fn(vfn(item, 5))
    out = c_void_p()
    hr = fn(item, SIGDN, byref(out))
    if (hr & 0xFFFFFFFF) == 0 and out.value:
        s = ctypes.wstring_at(out.value)
        ole32.CoTaskMemFree(out)
        return s
    return None


DEV = u"motorola edge 20 pro\\内部共享存储空间\\!573\\APPS\\Eta\\projects\\los-enhance\\refs\\official-rom"
TESTS = [
    u"此电脑\\" + DEV,
    u"::{20D04FE0-3AEA-1069-A2D8-08002B30309D}\\" + DEV,
    u"::{20D04FE0-3AEA-1069-A2D8-08002B30309D}\\motorola edge 20 pro",
    u"shell:MyComputerFolder\\motorola edge 20 pro",
    u"\\\\?\\usb#vid_22b8&pid_2e82#" + DEV,
    u"没用过的乱串\\" + DEV,
]


def main():
    ole32.CoInitialize(None)
    for t in TESTS:
        # ① SHCreateItemFromParsingName
        psi = c_void_p()
        hr1 = shell32.SHCreateItemFromParsingName(t, None, byref(IID_IShellItem), byref(psi)) & 0xFFFFFFFF
        n1 = item_name(psi) if hr1 == 0 else None
        # ② SHParseDisplayName
        pidl = c_void_p()
        attr = wintypes.DWORD(0)
        hr2 = shell32.SHParseDisplayName(t, None, byref(pidl), 0, byref(attr)) & 0xFFFFFFFF
        n2 = None
        if hr2 == 0 and pidl.value:
            np_ = c_void_p()
            if shell32.SHGetNameFromIDList(pidl, SIGDN, byref(np_)) == 0 and np_.value:
                n2 = ctypes.wstring_at(np_.value)
                ole32.CoTaskMemFree(np_)
            ole32.CoTaskMemFree(pidl)
        print(u"%-70s\n    ItemFromParsing hr=0x%08X %s\n    ParseDisplayName  hr=0x%08X %s"
              % (t[:70], hr1, n1, hr2, n2))


if __name__ == '__main__':
    main()
