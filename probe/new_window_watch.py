# -*- coding: utf-8 -*-
"""只读探针：盯着「新冒出来的顶层窗口」，把类名 / 进程 / 是否 shell 自己的窗口全记下来。

用来回答「某某程序打开一个文件夹，为什么没被收成标签」——
先看它到底有没有产生 `CabinetWClass` 顶层窗口，再看那个窗口是不是 shell（桌面）进程建的
（是的话我们的 `EmbedApi.IsShellOwned` 守卫会主动放过，见 CHANGELOG v1.13.1）。

不动鼠标、不抢前台、不发任何输入。用法：
    python new_window_watch.py [秒数，默认 600]     # 结果同时打到 stdout 和 new_window_watch.log
"""
import ctypes
import os
import sys
import time
from ctypes import wintypes

try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    pass

u = ctypes.windll.user32
k = ctypes.windll.kernel32

u.EnumWindows.argtypes = [ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM),
                          wintypes.LPARAM]
u.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
u.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
u.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
u.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
u.IsWindowVisible.argtypes = [wintypes.HWND]
u.GetWindow.argtypes = [wintypes.HWND, wintypes.UINT]
k.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
k.QueryFullProcessImageNameW.argtypes = [wintypes.HANDLE, wintypes.DWORD,
                                         wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)]

SHELL_CLASSES = ("Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman")
CAB_CLASSES = ("CabinetWClass", "ExploreWClass")
BROWSER = ("explorer.exe",)

# 系统那些纯辅助窗口（输入法 / 提示 / 自动完成）每次都一拥而上，记进日志只会把真东西埋掉。
NOISE = ("tooltips_class32", "IME", "MSCTFIME UI", "MSCTFIME Composition", "CiceroUIWndFrame",
         "Auto-Suggest Dropdown", "ComboLBox", "Static", "ForegroundStaging", "SystemTray_Main",
         "TaskListThumbnailWnd", "NotifyIconOverflowWindow", "WindowsForms10.tooltips_class32")
NOISE_PREFIX = ("ATL:", "WindowsForms10.tooltips_class32")

LOG = os.path.join(os.path.dirname(os.path.abspath(__file__)), "new_window_watch.log")
_exe_cache = {}


def cls(h):
    b = ctypes.create_unicode_buffer(256)
    u.GetClassNameW(h, b, 256)
    return b.value


def title(h):
    n = u.GetWindowTextLengthW(h) + 1
    b = ctypes.create_unicode_buffer(n)
    u.GetWindowTextW(h, b, n)
    return b.value


def pid_of(h):
    p = wintypes.DWORD()
    u.GetWindowThreadProcessId(h, ctypes.byref(p))
    return p.value


def exe_of(pid):
    if pid in _exe_cache:
        return _exe_cache[pid]
    name = "?"
    hp = k.OpenProcess(0x1000, False, pid)               # PROCESS_QUERY_LIMITED_INFORMATION
    if hp:
        try:
            buf = ctypes.create_unicode_buffer(512)
            n = wintypes.DWORD(512)
            if k.QueryFullProcessImageNameW(hp, 0, buf, ctypes.byref(n)):
                name = buf.value
        finally:
            k.CloseHandle(hp)
    _exe_cache[pid] = name
    return name


def rect(h):
    r = wintypes.RECT()
    u.GetWindowRect(h, ctypes.byref(r))
    return r.right - r.left, r.bottom - r.top


def top_windows():
    """当前所有顶层窗口 -> {hwnd: 类名}。"""
    got = {}

    def cb(h, lp):
        got[h] = cls(h)
        return True

    u.EnumWindows(ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)(cb), 0)
    return got


def shell_pids(wins):
    """哪些 pid 拥有桌面 shell 的窗口（= 我们的 IsShellOwned 判据）。"""
    return set(pid_of(h) for h, c in wins.items() if c in SHELL_CLASSES)


def describe(h, spids):
    pid = pid_of(h)
    w, ht = rect(h)
    line = "0x%08X  %-22s %5dx%-5d vis=%d pid=%-6d %s" % (
        h, cls(h), w, ht, 1 if u.IsWindowVisible(h) else 0, pid, exe_of(pid))
    if pid in spids:
        line += "   [shell 进程自己开的 —— 我们的守卫会放过]"
    t = title(h)
    if t:
        line += "\n        标题: " + t
    return line


def interesting(h, c):
    """值不值得记一行：浏览窗口/有名字有尺寸的真窗口要，输入法那类辅助窗不要。"""
    if c in CAB_CLASSES:
        return True
    if c in NOISE or c.startswith(NOISE_PREFIX):
        return False
    w, ht = rect(h)
    if w <= 1 or ht <= 1:
        return False
    return u.IsWindowVisible(h) != 0 or title(h) != ""


def emit(f, s):
    print(s, flush=True)
    f.write(s + "\n")
    f.flush()


def main():
    secs = int(sys.argv[1]) if len(sys.argv) > 1 else 600
    f = open(LOG, "a", encoding="utf-8")
    emit(f, "\n=== 开始盯窗口  %s  持续 %d 秒  (pid=%d) ===" % (
        time.strftime("%Y-%m-%d %H:%M:%S"), secs, os.getpid()))

    wins = top_windows()
    spids = shell_pids(wins)
    emit(f, "-- 桌面 shell 进程: %s" % (sorted(spids) or "没找到（Shell_TrayWnd/Progman 都不在？）"))
    cabs = [(h, c) for h, c in wins.items() if c in CAB_CLASSES]
    emit(f, "-- 现在就已经开着的浏览窗口（%d 个，这些算基线、不会当成新开）:" % len(cabs))
    for h, c in cabs:
        emit(f, "   " + describe(h, spids))
    expl = sorted(set(pid_of(h) for h, c in wins.items() if exe_of(pid_of(h)).lower().endswith(BROWSER)))
    emit(f, "-- 现在活着的 explorer 进程: %s" % expl)
    emit(f, "-- 盯上了，等你操作…")

    seen = set(wins.keys())
    n = 0
    last_beat = time.time()
    t0 = time.time()
    while time.time() - t0 < secs:
        time.sleep(0.15)
        cur = top_windows()
        for h in cur:
            if h in seen:
                continue
            seen.add(h)
            c = cur[h]
            if interesting(h, c):
                n += 1
                emit(f, "[%s] 新窗口 +%.1fs" % (time.strftime("%H:%M:%S"), time.time() - t0))
                emit(f, "   " + describe(h, shell_pids(cur)))
        for h in list(seen):                              # 关掉的从集合里去掉，重开也算新
            if h not in cur:
                seen.discard(h)
        if time.time() - last_beat >= 60:                 # 心跳：一眼能看出探针还活着
            last_beat = time.time()
            emit(f, "-- 还在盯 +%ds（期间记下 %d 个值得看的窗口）" % (int(time.time() - t0), n))
    emit(f, "-- 结束：期间新出现 %d 个值得看的顶层窗口" % n)
    f.close()


if __name__ == "__main__":
    main()
