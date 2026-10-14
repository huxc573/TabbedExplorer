# -*- coding: utf-8 -*-
"""只读探针：问 shell 的 SHParseDisplayName 认不认各种 MTP 写法。

不弹窗、不起进程、不抢前台。只调 shell32 的两个解析 API，看谁返回 S_OK。
用于定死「MTP 位置该用什么文本形式交给 explorer / IWebBrowser2」。
"""
import ctypes
from ctypes import wintypes

shell32 = ctypes.windll.shell32
ole32 = ctypes.windll.ole32

shell32.SHParseDisplayName.argtypes = [
    wintypes.LPCWSTR, ctypes.c_void_p, ctypes.POINTER(ctypes.c_void_p),
    wintypes.DWORD, ctypes.POINTER(wintypes.DWORD)]
shell32.SHParseDisplayName.restype = ctypes.c_long

shell32.SHGetNameFromIDList.argtypes = [
    ctypes.c_void_p, ctypes.c_int, ctypes.POINTER(ctypes.c_void_p)]
shell32.SHGetNameFromIDList.restype = ctypes.c_long

ole32.CoTaskMemFree.argtypes = [ctypes.c_void_p]
ole32.CoInitialize.argtypes = [ctypes.c_void_p]

SIGDN_NORMALDISPLAY = 0x00000000
SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000

DEV = u"motorola edge 20 pro\\内部共享存储空间"
TESTS = [
    u"此电脑\\" + DEV,
    u"::{20D04FE0-3AEA-1069-A2D8-08002B30309D}\\motorola edge 20 pro\\内部共享存储空间",
    u"::::{20D04FE0-3AEA-1069-A2D8-08002B30309D}\\motorola edge 20 pro\\内部共享存储空间",
    u"::{20D04FE0-3AEA-1069-A2D8-08002B30309D}",
    u"shell:MyComputerFolder",
    u"::{20D04FE0-3AEA-1069-A2D8-08002B30309D}\\motorola edge 20 pro",
]


def main():
    ole32.CoInitialize(None)
    for t in TESTS:
        pidl = ctypes.c_void_p()
        attr = wintypes.DWORD(0)
        hr = shell32.SHParseDisplayName(t, None, ctypes.byref(pidl), 0, ctypes.byref(attr))
        hr &= 0xFFFFFFFF
        if hr == 0 and pidl.value:
            np_ = ctypes.c_void_p()
            nm = u""
            if shell32.SHGetNameFromIDList(pidl, SIGDN_DESKTOPABSOLUTEPARSING,
                                           ctypes.byref(np_)) == 0 and np_.value:
                nm = ctypes.wstring_at(np_.value)
                ole32.CoTaskMemFree(np_)
            ole32.CoTaskMemFree(pidl)
            print(u"OK   %s\n       -> %s" % (t, nm))
        else:
            print(u"FAIL(0x%08X)  %s" % (hr, t))


if __name__ == "__main__":
    main()
