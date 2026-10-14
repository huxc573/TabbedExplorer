# -*- coding: utf-8 -*-
"""
MTP 路径形态取证（只读，不起窗口、不抢前台）。

思路：从「此电脑」(::{20D04FE0-...}) 出发，用 shell 命名空间枚举它的子项，
逐个打印：显示名、PIDL 的可解析路径 (SHGetPathFromIDList)、以及 SIGDN 各档
（NORMALDISPLAY / DESKTOPABSOLUTEPARSING / PARENTRELATIVEPARSING）。
这段能一次性回答：
  · MTP 设备在 shell 里的「可判别名字」是什么；
  · 有没有文件系统路径（预计没有）；
  · 稳定的唯一标识长什么样（后续要靠它存进书签）。
"""
import ctypes, os
from ctypes import wintypes, byref, POINTER, c_void_p

shell32 = ctypes.windll.shell32
ole32 = ctypes.windll.ole32
ole32.CoInitialize(None)

class _GUID(ctypes.Structure):
    _fields_ = [("d1", ctypes.c_ulong), ("d2", ctypes.c_ushort),
                ("d3", ctypes.c_ushort), ("d4", ctypes.c_ubyte * 8)]

def guid(s):
    from uuid import UUID
    x = UUID(s); g = _GUID()
    g.d1, g.d2, g.d3 = x.time_low, x.time_mid, x.time_hi_version
    b = x.bytes
    for i in range(8): g.d4[i] = b[8 + i]
    return g

# SIGDN 枚举
SIGDN_NORMALDISPLAY              = 0x00000000
SIGDN_PARENTRELATIVEPARSING      = 0x80018001
SIGDN_DESKTOPABSOLUTEPARSING     = 0x80028000
SIGDN_PARENTRELATIVEEDITING      = 0x80031001
SIGDN_DESKTOPABSOLUTEEDITING     = 0x8004C000
SIGDN_FILESYSPATH                = 0x80058000   # ★ 只有这个才是真文件系统路径
SIGDN_URL                        = 0x80068000
SIGDN_PARENTRELATIVEFORADDRESSBAR= 0x8007C001
SIGDN_PARENTRELATIVE             = 0x80080001

shell32.SHGetKnownFolderPath.argtypes = [POINTER(_GUID), wintypes.DWORD,
                                         wintypes.HANDLE, POINTER(ctypes.c_wchar_p)]
shell32.SHGetPathFromIDListW.argtypes = [c_void_p, ctypes.c_wchar_p]
shell32.SHGetPathFromIDListW.restype = wintypes.BOOL

# IShellItem / IShellFolder 手写 vtable 太啰嗦 —— 改用 SHParseDisplayName + SHBindToParent
shell32.SHParseDisplayName.argtypes = [ctypes.c_wchar_p, c_void_p,
                                       POINTER(c_void_p), wintypes.ULONG,
                                       POINTER(wintypes.ULONG)]
shell32.SHParseDisplayName.restype = ctypes.c_long

shell32.SHCreateItemFromParsingName.argtypes = [ctypes.c_wchar_p, c_void_p,
                                                POINTER(_GUID), POINTER(c_void_p)]
shell32.SHCreateItemFromParsingName.restype = ctypes.c_long

IID_IShellItem = guid("{43826D1E-E718-42EE-BC55-A1E261C37BFE}")

def shell_item(name):
    p = c_void_p()
    hr = shell32.SHCreateItemFromParsingName(name, None, byref(IID_IShellItem), byref(p))
    return (hr, p)

def vt_call(p, index, restype, *argtypes):
    """按 COM vtable 下标取函数指针并调用。"""
    vt = ctypes.cast(p, POINTER(POINTER(c_void_p))).contents
    fn = ctypes.WINFUNCTYPE(restype, c_void_p, *argtypes)(vt[index])
    return fn

def get_display(p, sigdn):
    # IShellItem vtable: 0..2 IUnknown, 3 BindToHandler, 4 GetParent, 5 GetDisplayName,
    #                    6 GetAttributes, 7 Compare
    pout = ctypes.c_wchar_p()
    fn = vt_call(p, 5, ctypes.c_long, ctypes.c_int, POINTER(ctypes.c_wchar_p))
    hr = fn(p, sigdn, byref(pout))
    val = pout.value
    if val:
        ole32.CoTaskMemFree(ctypes.cast(pout, c_void_p))
    return hr, val

def release(p):
    vt = ctypes.cast(p, POINTER(POINTER(c_void_p))).contents
    fn = ctypes.WINFUNCTYPE(ctypes.c_ulong, c_void_p)(vt[2])
    fn(p)

# 枚举「此电脑」的子项需要 IShellFolder —— 但更省事的办法：
# 直接拿几个典型标识去 SHCreateItemFromParsingName，看哪个能成功。
print("=" * 78)
print("A. 用若干候选标识去解析，看哪种能成立")
print("=" * 78)
cands = [
    "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}",              # 此电脑
    "shell:MyComputerFolder",
    "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}\\motorola edge 20 pro",
    "此电脑\\motorola edge 20 pro",
    "motorola edge 20 pro",
    "shell:MyComputerFolder\\motorola edge 20 pro",
]
for c in cands:
    hr, p = shell_item(c)
    if hr == 0 and p:
        st = []
        for tag, sig in (("ND", SIGDN_NORMALDISPLAY),
                         ("RDP", SIGDN_PARENTRELATIVEDISPLAY if False else SIGDN_PARENTRELATIVEPARSING),
                         ("DAP", SIGDN_DESKTOPABSOLUTEPARSING),
                         ("FSP", SIGDN_FILESYSPATH),
                         ("ADDR", SIGDN_PARENTRELATIVEFORADDRESSBAR)):
            h2, v = get_display(p, sig)
            st.append("%s=%s" % (tag, (v if h2 == 0 else "<hr 0x%08X>" % (h2 & 0xffffffff))))
        print("  OK   %-58r\n        %s" % (c, "  ".join(st)))
        release(p)
    else:
        print("  FAIL %-58r hr=0x%08X" % (c, hr & 0xffffffff))

print()
print("=" * 78)
print("B. 遍历此电脑的 IShellFolder，列出所有子项（找得到手机才谈得上复现）")
print("=" * 78)

# 走 SHParseDisplayName 拿「此电脑」的 PIDL，再 SHBindToParent 拿 IShellFolder
pidl = c_void_p()
hr = shell32.SHParseDisplayName("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", None,
                                byref(pidl), 0, None)
print("  SHParseDisplayName(此电脑) hr=0x%08X pidl=%s" % (hr & 0xffffffff, pidl.value))

IID_IShellFolder2 = guid("{93F2F68C-1D1B-11D3-A30E-00C04F79ABD1}")
folder = c_void_p()
shell32.SHBindToObject.argtypes = [c_void_p, c_void_p, c_void_p, POINTER(_GUID), POINTER(c_void_p)]
shell32.SHBindToObject.restype = ctypes.c_long
hr = shell32.SHBindToObject(None, pidl, None, byref(IID_IShellFolder2), byref(folder))
print("  SHBindToObject(此电脑) hr=0x%08X folder=%s" % (hr & 0xffffffff, folder.value))

if hr == 0 and folder.value:
    # IShellFolder vtable: 0-2 IUnknown, 3 ParseDisplayName, 4 EnumObjects,
    #                      5 BindToObject(没用到), ... IShellFolder2 把 EnumObjects 仍在 4
    ppenum = c_void_p()
    fn = vt_call(folder, 4, ctypes.c_long, c_void_p, wintypes.DWORD,
                 POINTER(c_void_p))
    # SHCONTF_FOLDERS=0x20 | SHCONTF_NONFOLDERS=0x40 | SHCONTF_INCLUDEHIDDEN=0x80
    hr = fn(folder, None, 0x20 | 0x40 | 0x80, byref(ppenum))
    print("  EnumObjects hr=0x%08X enum=%s" % (hr & 0xffffffff, ppenum.value))

    if hr == 0 and ppenum.value:
        # IEnumIDList vtable: 0-2 IUnknown, 3 Next, 4 Skip, 5 Reset, 6 Clone
        nxt = vt_call(ppenum, 3, ctypes.c_long, ctypes.c_ulong,
                      POINTER(c_void_p), POINTER(ctypes.c_ulong))
        n = 0
        while n < 40:
            one = c_void_p(); got = ctypes.c_ulong(0)
            hr = nxt(ppenum, 1, byref(one), byref(got))
            if hr != 0 or got.value != 0:
                break
            # 这个 child PIDL 是相对父的 —— 拼成绝对 PIDL 才能解析名字
            # 用 SHGetPathFromIDListW 先试（MTP 必然失败，正好当反证）
            buf = ctypes.create_unicode_buffer(1024)
            okpath = shell32.SHGetPathFromIDListW(one, buf)
            # 名字：IShellFolder::GetDisplayNameOf （vtable 6? 见下）
            #   实际下标: 0-2 IUnknown, 3 ParseDisplayName, 4 EnumObjects, 5 BindToObject,
            #             6 BindToStorage, 7 CompareIDs, 8 CreateViewObject,
            #             9 GetAttributesOf, 10 GetUIObjectOf, 11 GetDisplayNameOf
            namebuf = ctypes.create_unicode_buffer(1024)
            class STRRET(ctypes.Structure):
                _fields_ = [("uType", ctypes.c_uint), ("pad", ctypes.c_uint),
                            ("pOle", ctypes.c_wchar_p)]
            sr = STRRET()
            gdn = vt_call(folder, 11, ctypes.c_long, c_void_p, c_void_p,
                          ctypes.c_uint, POINTER(STRRET))
            h2 = gdn(folder, one, ctypes.byref(sr), 0x00000000)  # SHGDN_NORMAL
            nm = sr.pOle if h2 == 0 else "<hr 0x%08X>" % (h2 & 0xffffffff)
            print("    [%02d] FS路径=%s  名字=%r" % (n, repr(buf.value) if okpath else "(无)", nm))
            n += 1
        print("  共列出 %d 项" % n)
