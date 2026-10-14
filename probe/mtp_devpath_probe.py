# -*- coding: utf-8 -*-
"""设备段的**真实 PIDL 文本形式**已经拿到（vID/pid + WPD GUID）。
现在测：直接用这个真形式拼出整条 → SHParseDisplayName 认不认？
若认 → 根本不用枚举，直接存这个串、直接给 explorer 命令行。
只读。"""
import ctypes
from ctypes import wintypes, byref, POINTER, c_void_p

shell32 = ctypes.windll.shell32
ole32 = ctypes.windll.ole32
ole32.CoInitialize(None)

shell32.SHParseDisplayName.argtypes = [wintypes.LPCWSTR, c_void_p,
                                       POINTER(c_void_p), wintypes.ULONG,
                                       POINTER(wintypes.ULONG)]
shell32.SHParseDisplayName.restype = ctypes.c_long
ole32.CoTaskMemFree.argtypes = [c_void_p]

DEV = "\\\\?\\usb#vid_18d1&pid_4ee2&mi_00#7&2ebcb680&0&0000#{6ac27878-a6fa-4155-ba85-f98f491d4f33}"
PC = "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}"
REST = "内部共享存储空间\\!573\\APPS\\Eta\\projects\\los-enhance\\refs\\official-rom"

CANDS = [
    ("设备段真形式（光杆）", PC + "\\" + DEV),
    ("设备段真形式 + 存储", PC + "\\" + DEV + "\\内部共享存储空间"),
    ("设备段真形式 + 全路径", PC + "\\" + DEV + "\\" + REST),
    ("只有设备段（不带此电脑）", DEV),
    ("只有设备段 + 存储", DEV + "\\内部共享存储空间"),
    ("此电脑 + 显示名（旧写法）", PC + "\\motorola edge 20 pro\\内部共享存储空间"),
]

for name, s in CANDS:
    pidl = c_void_p()
    hr = shell32.SHParseDisplayName(s, None, byref(pidl), 0, None)
    ok = "OK ★" if (hr == 0 and pidl.value) else "0x%08X" % (hr & 0xFFFFFFFF)
    extra = ""
    if hr == 0 and pidl.value:
        ole32.CoTaskMemFree(pidl)
    print("%-32s %-10s  %s" % (name, ok, s[:110]))
