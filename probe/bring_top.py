"""把 TabbedExplorer 的主窗口置顶 + 显示出来，好让川看得见
（他的 IDE 最大化且自带置顶，新窗口会被压在底下）。

⚠ 三个必须做对的点（都在 memories / skill 里记着）：
  1) `SetWindowPos` 的 `hWndInsertAfter` 必须声明 `argtypes` —— 不声明时 64 位下 `-1`
     被当成 32 位传成 `0xFFFFFFFF`，调用静默失败（返回 0、GetLastError 恒 0），
     看上去就是「明明置顶了却没用」。
  2) 先 `SetProcessDpiAwareness(2)`，否则坐标跟截图工具差 1.5 倍。
  3) 验收用 `WindowFromPoint(窗口中心)` 命中自己，别看 `IsWindowVisible`
     （它可能确实可见，但被别的窗口盖着）。

用法: python bring_top.py
"""
import ctypes
import subprocess
from ctypes import wintypes

u = ctypes.windll.user32
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    pass

# ---- 先把签名声明齐（见上面第 1 点）----
u.SetWindowPos.argtypes = [wintypes.HWND, wintypes.HWND, ctypes.c_int, ctypes.c_int,
                           ctypes.c_int, ctypes.c_int, ctypes.c_uint]
u.SetWindowPos.restype = wintypes.BOOL
u.ShowWindow.argtypes = [wintypes.HWND, ctypes.c_int]
u.IsIconic.argtypes = [wintypes.HWND]
u.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
u.WindowFromPoint.argtypes = [wintypes.POINT]
u.WindowFromPoint.restype = wintypes.HWND
u.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
u.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]

HWND_TOPMOST = wintypes.HWND(-1)
SWP_NOSIZE = 0x0001
SWP_NOMOVE = 0x0002
SWP_SHOWWINDOW = 0x0040
SW_RESTORE = 9


def pids_of(name):
    out = subprocess.run(["tasklist", "/FO", "CSV", "/NH"], capture_output=True, text=True).stdout
    got = []
    for line in out.splitlines():
        parts = [p.strip('"') for p in line.split('","')]
        if len(parts) >= 2 and parts[0].lower() == name.lower():
            try:
                got.append(int(parts[1]))
            except ValueError:
                pass
    return got


def main():
    pids = set(pids_of("TabbedExplorer.exe"))
    if not pids:
        print("TabbedExplorer 没在跑")
        return

    found = []

    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def cb(h, l):
        pid = wintypes.DWORD()
        u.GetWindowThreadProcessId(h, ctypes.byref(pid))
        if pid.value in pids and u.IsWindowVisible(h):
            buf = ctypes.create_unicode_buffer(256)
            u.GetClassNameW(h, buf, 256)
            found.append((h, buf.value))
        return True

    u.EnumWindows(cb, 0)
    if not found:
        print("进程在、但没有可见的顶层窗口（是不是收进托盘了？）")
        return

    for h, _c in found:
        if u.IsIconic(h):
            u.ShowWindow(h, SW_RESTORE)
        u.SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0,
                       SWP_NOSIZE | SWP_NOMOVE | SWP_SHOWWINDOW)

    h, cls = found[0]
    r = wintypes.RECT()
    u.GetWindowRect(h, ctypes.byref(r))
    cx, cy = (r.left + r.right) // 2, (r.top + r.bottom) // 2

    # 中心点多半落在**内容区**上，而那是一个跨进程嵌进来的 explorer 子窗口，
    # 所以 WindowFromPoint 返回的不是我们的顶层窗口 —— 要往上找到根窗口再比。
    u.GetAncestor.argtypes = [wintypes.HWND, ctypes.c_uint]
    u.GetAncestor.restype = wintypes.HWND
    GA_ROOT = 2
    hit = u.WindowFromPoint(wintypes.POINT(cx, cy))
    root = u.GetAncestor(hit, GA_ROOT) if hit else 0
    ok = (root == h) or (hit == h)

    print("置顶 %d 个窗口；主窗口 hwnd=0x%X class=%s rect=%d,%d-%d,%d"
          % (len(found), h, cls, r.left, r.top, r.right, r.bottom))
    print("验收: 中心点(%d,%d) 命中 0x%X，根窗口 0x%X -> %s"
          % (cx, cy, hit, root, "就是它" if ok else "被别的窗口盖着"))


main()
