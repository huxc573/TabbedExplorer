# 只读探针：看 TabbedExplorer 主窗口的扩展样式（重点 WS_EX_TOPMOST 0x8）
# 为什么要看这个：弹出的菜单是**非置顶**窗口，主窗口一旦还带着 WS_EX_TOPMOST，
# 菜单就会被画在主窗口**底下** —— 日志里「弹出/关闭」都有、点哪个条目都没反应，
# 因为用户点到的其实是主窗口（对菜单来说那是「点在别处」）。
import ctypes
from ctypes import wintypes

u32 = ctypes.windll.user32
u32.GetWindowLongW.argtypes = [wintypes.HWND, ctypes.c_int]
u32.GetWindowLongW.restype = ctypes.c_long
u32.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
u32.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
u32.IsWindowVisible.argtypes = [wintypes.HWND]
u32.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]

GWL_EXSTYLE = -20
WS_EX_TOPMOST = 0x00000008

EnumProc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
found = []


def on_win(hwnd, _):
    cls = ctypes.create_unicode_buffer(256)
    u32.GetClassNameW(hwnd, cls, 256)
    if "WindowsForms10" in cls.value:
        txt = ctypes.create_unicode_buffer(512)
        u32.GetWindowTextW(hwnd, txt, 512)
        ex = u32.GetWindowLongW(hwnd, GWL_EXSTYLE) & 0xFFFFFFFF
        r = wintypes.RECT()
        u32.GetWindowRect(hwnd, ctypes.byref(r))
        found.append((hwnd, cls.value, txt.value, ex, r))
    return True


u32.EnumWindows(EnumProc(on_win), 0)

print("屏幕: %dx%d" % (u32.GetSystemMetrics(0), u32.GetSystemMetrics(1)))
for hwnd, cls, txt, ex, r in found:
    top = "★TOPMOST" if (ex & WS_EX_TOPMOST) else "  普通"
    print("%s hwnd=0x%X vis=%d ex=0x%08X rect=%d,%d-%d,%d title=%r"
          % (top, hwnd, u32.IsWindowVisible(hwnd), ex, r.left, r.top, r.right, r.bottom, txt))
if not found:
    print("没找到 WindowsForms10 窗口（程序没在跑？）")
