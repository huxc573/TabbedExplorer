# -*- coding: utf-8 -*-
"""只读探针：拿到 MTP 目录的 PIDL（SIGDN_DESKTOPABSOLUTEPARSING 文本）。

思路：SHParseDisplayName 不认文本串 ⇒ 只能**逐级绑定**：
  桌面 IShellFolder → BindToObject("此电脑"项) → … 一级一级走下去。
但 MTP 项在「此电脑」下要等设备枚举，这条路在 Python 里够用吗？先试。

只读：不写任何东西、不起窗口、不抢前台。
"""
import ctypes
from ctypes import wintypes, POINTER, byref, c_void_p, c_uint, c_ulonglong

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
IID_IEnumIDList = guid('000214F2-0000-0000-C000-000000000046')
IID_IPersistFolder = guid('000214EA-0000-0000-C000-000000000046')
IID_IShellItem = guid('43826D1E-E718-42EE-BC55-A1E261C37BFE')


class STRRET(ctypes.Structure):
    _fields_ = [('uType', ctypes.c_uint), ('pad', ctypes.c_uint),
                ('pOleStr', c_void_p), ('uOffset', ctypes.c_uint), ('cStr', ctypes.c_char * 260)]


class SHITEMID(ctypes.Structure):
    _fields_ = [('cb', ctypes.c_ushort)]   # 后面跟可变长度 abID


def vtbl(p, i):
    """从 COM 接口指针取第 i 个方法地址。"""
    v = ctypes.cast(p, POINTER(POINTER(c_void_p))).contents
    return v[i]


VtFn = ctypes.WINFUNCTYPE


def enum_of(folder):
    """IShellFolder::EnumObjects (vtable 3) -> IEnumIDList"""
    fn = VtFn(ctypes.c_long, c_void_p, c_void_p, ctypes.c_uint, ctypes.POINTER(c_void_p))(vtbl(folder, 3))
    out = c_void_p()
    hr = fn(folder, None, 0x20 | 0x40, byref(out))   # NORMALDISPLAY | STORAGECAPS
    return (hr & 0xFFFFFFFF), out


def next_of(enum, count=1):
    """IEnumIDList::Next (vtable 3)"""
    fn = VtFn(ctypes.c_long, c_void_p, ctypes.c_ulong, POINTER(c_void_p), POINTER(ctypes.c_ulong))(vtbl(enum, 3))
    arr = (c_void_p * count)()
    got = ctypes.c_ulong(0)
    hr = fn(enum, count, arr, byref(got))
    return (hr & 0xFFFFFFFF), arr, got.value


def name_of(folder, pidl):
    """IShellFolder::GetDisplayNameOf (vtable 10) + StrRetToBuf 取显示名。"""
    fn = VtFn(ctypes.c_long, c_void_p, c_void_p, ctypes.c_ulong, POINTER(STRRET))(vtbl(folder, 10))
    sr = STRRET()
    hr = fn(folder, pidl, 0x00000000, byref(sr))     # SHGDN_NORMAL
    if (hr & 0xFFFFFFFF) != 0:
        return None
    buf = ctypes.create_unicode_buffer(512)
    hr2 = shell32.StrRetToBufW(byref(sr), pidl, buf, 512)
    if (hr2 & 0xFFFFFFFF) != 0:
        return None
    return buf.value


def bind(folder, pidl):
    """IShellFolder::BindToObject (vtable 7)"""
    fn = VtFn(ctypes.c_long, c_void_p, c_void_p, c_void_p, POINTER(GUID), POINTER(c_void_p))(vtbl(folder, 7))
    out = c_void_p()
    hr = fn(folder, pidl, None, byref(IID_IShellFolder), byref(out))
    return (hr & 0xFFFFFFFF), out


def main():
    ole32.CoInitialize(None)
    psf = c_void_p()
    hr = shell32.SHGetDesktopFolder(byref(psf))
    print('SHGetDesktopFolder hr=0x%08X p=%s' % (hr & 0xFFFFFFFF, psf.value))

    cur = psf
    path = []
    for depth in range(6):
        hr, enum = enum_of(cur)
        if hr != 0:
            print('  enum hr=0x%08X' % hr)
            return
        hr, arr, got = next_of(enum)
        if got == 0:
            # 换个思路：设备可能不在第一项，全扫一遍找名字带 motorola 的
            print('  Next 返回 0 项')
            break
        # 全扫这一层，找带 motorola / 内部共享 字样的
        names = []
        while got:
            pidl = arr[0]
            nm = name_of(cur, pidl)
            names.append(nm)
            if nm and (u'motorola' in nm.lower() or u'内部共享' in nm or u'edge' in nm.lower()):
                hr2, sub = bind(cur, pidl)
                print('  命中「%s」bind hr=0x%08X' % (nm, hr2))
                if hr2 == 0 and sub.value:
                    cur = sub
                    path.append(nm)
                    break
            hr, arr, got = next_of(enum)
        else:
            print('  这一层没找到目标，见到：', names[:20])
            break
        continue
    print('走过的路径：', path)


if __name__ == '__main__':
    main()
