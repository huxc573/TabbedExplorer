# -*- coding: utf-8 -*-
"""决定性实测：三种候选串，哪个真能开手机目录。

从 TB 日志取现成的工作串（09:16:21 那次成功的）：
  09:16:21.199 Embed: 现在在 此电脑\motorola edge 20 pro\...official-rom
并对照 09:39 那次失败的重复拼串。

做法（只读、不抢前台）：SHParseDisplayName 判「shell 认不认」，
认了再用 SHGetPathFromIDList / SHGetNameFromIDList 看它指向哪。
比真起窗口更干净，也不会被 TB 收编盖住。
"""
import ctypes
from ctypes import wintypes

shell32 = ctypes.windll.shell32
ole32 = ctypes.windll.ole32

PC = "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}"
DEV = "\\\\?\\usb#vid_18d1&pid_4ee2&mi_00#7&2ebcb680&0&0000#{6ac27878-a6fa-4155-ba85-f98f491d4f33}"
REST = "\\内部共享存储空间\\!573\\APPS\\Eta\\projects\\los-enhance\\refs\\official-rom"
NAME = "motorola edge 20 pro"

CANDIDATES = {
    "① 设备段=设备路径，前置一个此电脑（本地探针 mtp_open_real.py 实测成功的那串）":
        PC + "\\" + DEV + REST,
    "② 设备段=设备路径，无此前缀":
        DEV + REST,
    "③ 设备段=显示名，前置一个此电脑（书上老写法）":
        PC + "\\" + NAME + REST,
    "④ 重复拼（TB 09:39 那次实际发出去的）":
        PC + "\\" + PC + "\\" + DEV + REST,
    "⑤ 光杆此电脑（对照，必须成）":
        PC,
}

ole32.CoInitialize(None)
SHParseDisplayName = shell32.SHParseDisplayName
SHParseDisplayName.argtypes = [ctypes.c_wchar_p, ctypes.c_void_p,
                               ctypes.POINTER(ctypes.c_void_p), wintypes.ULONG,
                               ctypes.POINTER(wintypes.ULONG)]
SHParseDisplayName.restype = ctypes.c_long

SHGetNameFromIDList = shell32.SHGetNameFromIDList
SHGetNameFromIDList.argtypes = [ctypes.c_void_p, ctypes.c_int,
                                ctypes.POINTER(ctypes.c_wchar_p)]
SHGetNameFromIDList.restype = ctypes.c_long
SIGDN_DESKTOPABSOLUTEPARSING = ctypes.c_int(0x80028000 - (1 << 32))
SIGDN_NORMALDISPLAY = ctypes.c_int(0)

for label, s in CANDIDATES.items():
    pidl = ctypes.c_void_p()
    attr = wintypes.ULONG(0)
    hr = SHParseDisplayName(s, None, ctypes.byref(pidl), 0, ctypes.byref(attr))
    print("-" * 78)
    print(label)
    print("  串 = %s" % s)
    print("  hr = 0x%08X %s" % (hr & 0xFFFFFFFF, "OK" if hr == 0 else "失败"))
    if hr == 0 and pidl.value:
        nm = ctypes.c_wchar_p()
        h2 = SHGetNameFromIDList(pidl, SIGDN_DESKTOPABSOLUTEPARSING, ctypes.byref(nm))
        if h2 == 0 and nm.value:
            got = nm.value
            print("  绝对串 = %s" % got)
        nd = ctypes.c_wchar_p()
        h3 = SHGetNameFromIDList(pidl, SIGDN_NORMALDISPLAY, ctypes.byref(nd))
        if h3 == 0 and nd.value:
            print("  显示名 = %s" % nd.value)
        ole32.CoTaskMemFree(pidl)
