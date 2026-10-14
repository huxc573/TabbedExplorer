# -*- coding: utf-8 -*-
"""带各种 STR_PARSE_* flag 试 SHParseDisplayName —— 看 shell 到底认哪个写法/哪种解析模式。
只读：只解析，不打开任何窗口、不碰前台。"""
import ctypes
from ctypes import wintypes

shell32 = ctypes.windll.shell32
ole32 = ctypes.windll.ole32

shell32.SHParseDisplayName.argtypes = [
    wintypes.LPCWSTR, ctypes.c_void_p, ctypes.POINTER(ctypes.c_void_p),
    ctypes.c_uint, ctypes.POINTER(ctypes.c_uint)]
shell32.SHParseDisplayName.restype = ctypes.c_long

# STR_PARSE_* （shlwapi.h / shobjidl.h）
FLAGS = {
    "0": 0,
    "PREFER_FOLDER_BROWSING": 0x00010000,
    "PREFER_FILESYSTEM": 0x00040000,
    "ALLOW_RELATIVE": 0x00000001,
    "ENABLE_ASYNC": 0x00100000,
    "DONT_VERIFY": 0x00000004,
    "PREFER_FOLDER_BROWSING|ALLOW_RELATIVE": 0x00010001,
    "PREFER_FOLDER_BROWSING|DONT_VERIFY": 0x00010004,
}

P = "此电脑\\motorola edge 20 pro\\内部共享存储空间\\!573\\APPS"
TESTS = [
    ("显示名串", P),
    ("带尾反斜杠", P + "\\"),
    ("双反斜杠前缀", "\\\\" + P),
    ("::{GUID}串", "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}\\motorola edge 20 pro\\内部共享存储空间"),
    ("光杆此电脑", "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}"),
    ("shell:MyComputerFolder", "shell:MyComputerFolder"),
]

for name, s in TESTS:
    row = []
    for fname, fl in FLAGS.items():
        pidl = ctypes.c_void_p()
        attr = ctypes.c_uint()
        hr = shell32.SHParseDisplayName(s, None, ctypes.byref(pidl), fl, ctypes.byref(attr))
        ok = "OK " if (hr == 0 and pidl.value) else "%-8s" % ("0x%08X" % (hr & 0xFFFFFFFF))
        if pidl.value:
            ole32.CoTaskMemFree(pidl)
        row.append("%s=%s" % (fname, ok))
    print("%-22s %s" % (name, " | ".join(row)))
