"""只读探针：列出 winsta0 里的所有桌面名。

截图实验用 CreateDesktopW 造过隐藏桌面（名字形如 TBEShot<时间>）；脚本退出后
那些桌面是否被销毁、进程是否残留，看这个列表就知道。
"""
import ctypes
from ctypes import wintypes

u32 = ctypes.windll.user32
# ⚠ 不声明 DPI 感知时 Windows 会按缩放比把坐标虚拟化（本机 150%：2880×1800 读成 1920×1200）
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    pass
u32.OpenWindowStationW.argtypes = [wintypes.LPCWSTR, wintypes.BOOL, wintypes.DWORD]
u32.OpenWindowStationW.restype = wintypes.HANDLE
u32.EnumDesktopsW.argtypes = [wintypes.HANDLE, ctypes.c_void_p, wintypes.LPARAM]
u32.GetUserObjectInformationW.argtypes = [wintypes.HANDLE, ctypes.c_int, wintypes.LPVOID,
                                          wintypes.DWORD, ctypes.POINTER(wintypes.DWORD)]
WINSTA_ENUMDESKTOPS = 0x0001
UOI_NAME = 2

h = u32.OpenWindowStationW("winsta0", False, WINSTA_ENUMDESKTOPS)
if not h:
    raise SystemExit("打不开 winsta0")
names = []
PROC = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.LPWSTR, wintypes.LPARAM)


def cb(name, _):
    names.append(name)
    return True


if not u32.EnumDesktopsW(h, ctypes.cast(PROC(cb), ctypes.c_void_p), 0):
    print("EnumDesktops 失败, err=%d" % ctypes.get_last_error())
for n in names:
    print("desktop: %r%s" % (n, "   <== 疑似实验残留" if n.lower().startswith("tbeshot") else ""))
print("桌面共 %d 个" % len(names))
