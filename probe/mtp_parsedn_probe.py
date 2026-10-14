# -*- coding: utf-8 -*-
"""只读探针：用「父文件夹的 IShellFolder::ParseDisplayName」解析 MTP 相对名。

这是 shell 解析 MTP 的**正规**路径：
    SHGetDesktopFolder → ParseDisplayName("::{20D04FE0-…}") → 拿「此电脑」的 IShellFolder
    → 它再 ParseDisplayName("motorola edge 20 pro\\内部共享存储空间\\…") → 完整绝对 PIDL

只读：不写文件、不起窗口、不抢前台。
"""
import ctypes
from ctypes import wintypes, POINTER, byref, c_void_p, c_uint, c_ulong

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


IID_IShellFolder = guid('000214E6-0000-0000-C000-000000000046')
NULL_GUID = GUID()

ole32.CoTaskMemFree.argtypes = [c_void_p]
ole32.CoInitialize.argtypes = [c_void_p]

# SHGetDesktopFolder
shell32.SHGetDesktopFolder.argtypes = [POINTER(c_void_p)]
shell32.SHGetDesktopFolder.restype = ctypes.c_long
# SHGetNameFromIDList
shell32.SHGetNameFromIDList.argtypes = [c_void_p, ctypes.c_int, POINTER(c_void_p)]
shell32.SHGetNameFromIDList.restype = ctypes.c_long
# SHBindToParent（拿某 PIDL 的父 IShellFolder + 相对 PIDL）
shell32.SHBindToParent.argtypes = [c_void_p, POINTER(GUID), POINTER(c_void_p), POINTER(c_void_p)]
shell32.SHBindToParent.restype = ctypes.c_long

SIGDN = 0x80028000   # DESKTOPABSOLUTEPARSING


def vfn(p, i):
    return ctypes.cast(p, POINTER(POINTER(c_void_p))).contents[i]


def parse_display_name(psf, name):
    """IShellFolder::ParseDisplayName —— vtable 第 3 个（QueryInterface/AddRef/Release 之后）。"""
    Fn = ctypes.WINFUNCTYPE(ctypes.c_long, c_void_p, c_void_p, ctypes.c_wchar_p,
                            c_void_p, POINTER(c_void_p), POINTER(c_ulong), POINTER(c_ulong))
    fn = Fn(vfn(psf, 3))
    out = c_void_p()
    eaten = ctypes.c_ulong(0)
    attr = ctypes.c_ulong(0)
    hr = fn(psf, None, name, None, byref(out), byref(eaten), byref(attr))
    return (hr & 0xFFFFFFFF), out


def bind_to_object(psf, pidl):
    """IShellFolder::BindToObject —— vtable 第 7 个。"""
    Fn = ctypes.WINFUNCTYPE(ctypes.c_long, c_void_p, c_void_p, c_void_p,
                            POINTER(GUID), POINTER(c_void_p))
    fn = Fn(vfn(psf, 7))
    out = c_void_p()
    hr = fn(psf, pidl, None, byref(IID_IShellFolder), byref(out))
    return (hr & 0xFFFFFFFF), out


def abs_name(pidl):
    np_ = c_void_p()
    if shell32.SHGetNameFromIDList(pidl, SIGDN, byref(np_)) == 0 and np_.value:
        s = ctypes.wstring_at(np_.value)
        ole32.CoTaskMemFree(np_)
        return s
    return None


def main():
    ole32.CoInitialize(None)
    psf = c_void_p()
    print('desktop hr=0x%08X' % (shell32.SHGetDesktopFolder(byref(psf)) & 0xFFFFFFFF))

    # ① 桌面 → 此电脑（ParseDisplayName 认 `::{20D04FE0-…}`）
    hr, thispc_pidl = parse_display_name(psf, u"::{20D04FE0-3AEA-1069-A2D8-08002B30309D}")
    print(u'  ParseDisplayName("::{{20D04FE0-…}}") hr=0x%08X pidl=%s  abs=%s'
          % (hr, thispc_pidl.value, abs_name(thispc_pidl) if thispc_pidl.value else None))
    if hr != 0:
        return

    hr, thispc = bind_to_object(psf, thispc_pidl)
    print('  BindToObject(此电脑) hr=0x%08X sf=%s' % (hr, thispc.value))
    if hr != 0:
        return

    # ② 「此电脑」的 IShellFolder → ParseDisplayName(相对名)
    for rel in (u"motorola edge 20 pro",
                u"motorola edge 20 pro\\内部共享存储空间",
                u"motorola edge 20 pro\\内部共享存储空间\\!573\\APPS\\Eta\\projects\\los-enhance\\refs\\official-rom"):
        hr, pidl = parse_display_name(thispc, rel)
        print(u'  ParseDisplayName(%r)\n       hr=0x%08X  abs=%s'
              % (rel, hr, abs_name(pidl) if pidl.value else None))


if __name__ == '__main__':
    main()
