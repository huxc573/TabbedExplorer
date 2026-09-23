"""只读探针：量准屏幕分辨率 / 缩放 / 显示器几何。

为什么需要它：**没有声明 DPI 感知的进程，Windows 会把坐标按缩放比虚拟化**。
本机面板是 2880×1800、系统缩放 150%，于是默认（DPI 不感知）读出来是 1920×1200 ——
少了的正是那 1.5 倍。凡是要报尺寸 / 定位窗口 / 截图的探针，开头都得先声明感知级别。

跑两遍对照：一遍不声明，一遍声明 PerMonitorV2。
"""
import ctypes
from ctypes import wintypes

u = ctypes.windll.user32
shcore = ctypes.windll.shcore
gdi = ctypes.windll.gdi32

SM_CXSCREEN, SM_CYSCREEN = 0, 1
SM_CXVIRTUALSCREEN, SM_CYVIRTUALSCREEN = 78, 79
LOGPIXELSX = 88


class RECT(ctypes.Structure):
    _fields_ = [("left", wintypes.LONG), ("top", wintypes.LONG),
                ("right", wintypes.LONG), ("bottom", wintypes.LONG)]


class MONITORINFO(ctypes.Structure):
    _fields_ = [("cbSize", wintypes.DWORD), ("rcMonitor", RECT),
                ("rcWork", RECT), ("dwFlags", wintypes.DWORD)]


u.EnumDisplayMonitors.argtypes = [wintypes.HDC, ctypes.c_void_p, ctypes.c_void_p, wintypes.LPARAM]
u.GetMonitorInfoW.argtypes = [wintypes.HANDLE, ctypes.POINTER(MONITORINFO)]
u.GetDC.argtypes = [wintypes.HWND]
u.GetDC.restype = wintypes.HDC
u.ReleaseDC.argtypes = [wintypes.HWND, wintypes.HDC]
# HDC 是 64 位句柄：不声明 argtypes 会被按 c_int 传，直接 OverflowError
gdi.GetDeviceCaps.argtypes = [wintypes.HDC, ctypes.c_int]
gdi.GetDeviceCaps.restype = ctypes.c_int


def report(tag):
    dpi = gdi.GetDeviceCaps(u.GetDC(0), LOGPIXELSX)
    print("[%s] 缩放 = %d%%   主屏(SM_CXSCREEN) = %dx%d   虚拟桌面 = %dx%d"
          % (tag, dpi * 100 // 96,
             u.GetSystemMetrics(SM_CXSCREEN), u.GetSystemMetrics(SM_CYSCREEN),
             u.GetSystemMetrics(SM_CXVIRTUALSCREEN), u.GetSystemMetrics(SM_CYVIRTUALSCREEN)))
    rows = []
    PROC = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HANDLE, wintypes.HDC,
                              ctypes.POINTER(RECT), wintypes.LPARAM)

    def cb(hmon, hdc, lprc, lp):
        mi = MONITORINFO()
        mi.cbSize = ctypes.sizeof(MONITORINFO)
        if u.GetMonitorInfoW(hmon, ctypes.byref(mi)):
            m, w = mi.rcMonitor, mi.rcWork
            rows.append((m.left, m.top, m.right - m.left, m.bottom - m.top,
                         w.right - w.left, w.bottom - w.top,
                         bool(mi.dwFlags & 1)))
        return True

    u.EnumDisplayMonitors(0, None, PROC(cb), 0)
    for left, top, mw, mh, ww, wh, primary in rows:
        print("    显示器 at (%d,%d) 分辨率 %dx%d 工作区 %dx%d%s"
              % (left, top, mw, mh, ww, wh, "  [主]" if primary else ""))
    return rows


print("=== 默认（不声明 DPI 感知）===")
report("虚拟化")
try:
    shcore.SetProcessDpiAwareness(2)   # PROCESS_PER_MONITOR_DPI_AWARE
    print("=== 声明 Per-Monitor DPI 感知后 ===")
    report("真实")
except Exception as e:
    print("SetProcessDpiAwareness 失败（可能已设置过）:", e)

# 主屏真实 DPI（GetDpiForMonitor）
try:
    dpiX, dpiY = wintypes.UINT(), wintypes.UINT()
    mon = u.MonitorFromPoint(wintypes.POINT(0, 0), 1)
    shcore.GetDpiForMonitor.argtypes = [wintypes.HANDLE, ctypes.c_int,
                                       ctypes.POINTER(wintypes.UINT), ctypes.POINTER(wintypes.UINT)]
    if shcore.GetDpiForMonitor(mon, 0, ctypes.byref(dpiX), ctypes.byref(dpiY)) == 0:
        print("主显示器 DPI = %d (%d%%)" % (dpiX.value, dpiX.value * 100 // 96))
except Exception as e:
    print("GetDpiForMonitor 不可用:", e)
