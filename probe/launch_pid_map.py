# -*- coding: utf-8 -*-
"""只读探针：搞清 `explorer.exe /n,/separate,<路径>` 起的窗口**归谁**，以及隐藏状态下地址栏多久可读。

要回答两件事（并发启动能不能靠 pid 一一对应）：
  A. `CreateProcess` 拿到的 launcher pid，跟新窗口的属主 pid 是不是同一个？（会不会被转交给已有进程）
  B. 窗口**一出现就藏起来**（程序就是这么干的）之后，地址栏还能不能读、多久能读出来？
     —— 若隐藏期间永远读不出来，那就只能靠 pid 对应，地址栏比对不能当并发时的判据。

手法对齐程序：发现新窗口立刻 `ShowWindow(SW_HIDE)`，全程不动鼠标、不抢前台。
跑完把开出来的窗口关掉（`WM_CLOSE`）。用法：
    python launch_pid_map.py [每组次数，默认 3]
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
u.EnumChildWindows.argtypes = [wintypes.HWND,
                               ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM),
                               wintypes.LPARAM]
u.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
u.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
u.GetWindowTextLengthW.argtypes = [wintypes.HWND]
u.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
u.ShowWindow.argtypes = [wintypes.HWND, ctypes.c_int]
u.PostMessageW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
u.IsWindow.argtypes = [wintypes.HWND]
u.IsWindowVisible.argtypes = [wintypes.HWND]
k.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
k.CloseHandle.argtypes = [wintypes.HANDLE]
k.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
k.QueryFullProcessImageNameW.argtypes = [wintypes.HANDLE, wintypes.DWORD,
                                         wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)]
k.CreateToolhelp32Snapshot.argtypes = [wintypes.DWORD, wintypes.DWORD]

CAB = ("CabinetWClass", "ExploreWClass")
WM_CLOSE = 0x0010
SW_HIDE = 0
STILL_ACTIVE = 259


class STARTUPINFOW(ctypes.Structure):
    _fields_ = [("cb", wintypes.DWORD), ("lpReserved", wintypes.LPWSTR),
                ("lpDesktop", wintypes.LPWSTR), ("lpTitle", wintypes.LPWSTR),
                ("dwX", wintypes.DWORD), ("dwY", wintypes.DWORD),
                ("dwXSize", wintypes.DWORD), ("dwYSize", wintypes.DWORD),
                ("dwXCountChars", wintypes.DWORD), ("dwYCountChars", wintypes.DWORD),
                ("dwFillAttribute", wintypes.DWORD), ("dwFlags", wintypes.DWORD),
                ("wShowWindow", wintypes.WORD), ("cbReserved2", wintypes.WORD),
                ("lpReserved2", ctypes.POINTER(ctypes.c_byte)),
                ("hStdInput", wintypes.HANDLE), ("hStdOutput", wintypes.HANDLE),
                ("hStdError", wintypes.HANDLE)]


class PROCESS_INFORMATION(ctypes.Structure):
    _fields_ = [("hProcess", wintypes.HANDLE), ("hThread", wintypes.HANDLE),
                ("dwProcessId", wintypes.DWORD), ("dwThreadId", wintypes.DWORD)]


class PROCESSENTRY32W(ctypes.Structure):
    _fields_ = [("dwSize", wintypes.DWORD), ("cntUsage", wintypes.DWORD),
                ("th32ProcessID", wintypes.DWORD),
                ("th32DefaultHeapID", ctypes.POINTER(ctypes.c_ulong)),
                ("th32ModuleID", wintypes.DWORD), ("cntThreads", wintypes.DWORD),
                ("th32ParentProcessID", wintypes.DWORD),
                ("pcPriClassBase", ctypes.c_long), ("dwFlags", wintypes.DWORD),
                ("szExeFile", ctypes.c_wchar * 260)]


k.CreateProcessW.argtypes = [wintypes.LPCWSTR, wintypes.LPWSTR, ctypes.c_void_p,
                             ctypes.c_void_p, wintypes.BOOL, wintypes.DWORD,
                             ctypes.c_void_p, wintypes.LPCWSTR,
                             ctypes.POINTER(STARTUPINFOW),
                             ctypes.POINTER(PROCESS_INFORMATION)]


def cls(h):
    b = ctypes.create_unicode_buffer(256)
    u.GetClassNameW(h, b, 256)
    return b.value


def text(h):
    n = u.GetWindowTextLengthW(h) + 1
    b = ctypes.create_unicode_buffer(n)
    u.GetWindowTextW(h, b, n)
    return b.value


def pid_of(h):
    p = wintypes.DWORD()
    u.GetWindowThreadProcessId(h, ctypes.byref(p))
    return p.value


def exe_of(pid):
    hp = k.OpenProcess(0x1000, False, pid)
    if not hp:
        return "?"
    try:
        buf = ctypes.create_unicode_buffer(512)
        n = wintypes.DWORD(512)
        return buf.value if k.QueryFullProcessImageNameW(hp, 0, buf, ctypes.byref(n)) else "?"
    finally:
        k.CloseHandle(hp)


def children(h):
    got = []

    def cb(c, lp):
        got.append(c)
        return True
    u.EnumChildWindows(h, ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)(cb), 0)
    return got


def all_tops():
    got = []

    def cb(h, lp):
        got.append(h)
        return True
    u.EnumWindows(ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)(cb), 0)
    return got


def cabs():
    return [h for h in all_tops() if cls(h) in CAB]


def read_path(cab):
    """照 EmbedApi.FindAddressBand 的路子读地址栏（Breadcrumb Parent → ToolbarWindow32）。"""
    band = None
    for c in children(cab):
        if cls(c) == "Breadcrumb Parent":
            for t in children(c):
                if cls(t) == "ToolbarWindow32":
                    band = t
                    break
    if band is None:
        for c in children(cab):
            if cls(c) == "ToolbarWindow32" and ": " in text(c):
                band = c
                break
    if band is None:
        return None
    raw = text(band)
    _, _, rest = raw.partition(": ")
    return rest.strip()


def has_defview(cab):
    return any(cls(c) == "SHELLDLL_DefView" for c in children(cab))


def explorer_pids():
    TH32CS_SNAPPROCESS = 0x2
    s = k.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
    out = set()
    try:
        e = PROCESSENTRY32W()
        e.dwSize = ctypes.sizeof(PROCESSENTRY32W)
        ok = k.Process32FirstW(s, ctypes.byref(e))
        while ok:
            if e.szExeFile.lower() == "explorer.exe":
                out.add(e.th32ProcessID)
            ok = k.Process32NextW(s, ctypes.byref(e))
    finally:
        k.CloseHandle(s)
    return out


def alive(hproc):
    return k.WaitForSingleObject(hproc, 0) == 0x102      # WAIT_TIMEOUT = 还活着


def shell_pid():
    h = u.FindWindowW("Shell_TrayWnd", None)
    return pid_of(h) if h else 0


def run_one(target, label):
    before_cabs = set(cabs())
    before_pids = explorer_pids()
    t0 = time.time()

    cmd = 'explorer.exe /n,/separate,"%s"' % target
    si = STARTUPINFOW()
    si.cb = ctypes.sizeof(STARTUPINFOW)
    pi = PROCESS_INFORMATION()
    buf = ctypes.create_unicode_buffer(cmd)
    ok = k.CreateProcessW(None, buf, None, None, False, 0, None, None,
                          ctypes.byref(si), ctypes.byref(pi))
    if not ok:
        print("[%s] CreateProcess 失败 err=%d" % (label, k.GetLastError()))
        return
    launcher = pi.dwProcessId
    k.CloseHandle(pi.hThread)

    cab = None
    t_appear = None
    pid_win = 0
    t_addr = None
    addr_val = None
    t_addr_visible = None
    t_view = None
    shown_again = 0

    deadline = t0 + 8.0
    while time.time() < deadline:
        now = time.time()
        if cab is None:
            for h in cabs():
                if h in before_cabs:
                    continue
                cab = h
                t_appear = (now - t0) * 1000
                pid_win = pid_of(h)
                u.ShowWindow(h, SW_HIDE)      # 程序就是这么干的：发现即藏
                break
        else:
            if u.IsWindowVisible(cab):
                shown_again += 1
                u.ShowWindow(cab, SW_HIDE)    # 它自己又显示了一次，再藏
            if t_addr is None:
                p = read_path(cab)
                if p:
                    t_addr = (now - t0) * 1000
                    addr_val = p
                    t_addr_visible = 0        # 藏起来之后仍可读
            if t_view is None and has_defview(cab):
                t_view = (now - t0) * 1000
            if t_addr is not None and t_view is not None and now - t0 > 2.5:
                break
        time.sleep(0.015)

    new_pids = sorted(explorer_pids() - before_pids)
    launcher_alive_1 = alive(pi.hProcess)
    print("[%s] target=%s" % (label, target))
    print("     launcher pid=%d  窗口 pid=%d  同一个=%s" % (
        launcher, pid_win, "是" if pid_win == launcher else "否"))
    print("     新出现的 explorer 进程: %s   窗口属主在里头=%s" % (
        new_pids, pid_win in new_pids))
    print("     窗口属主是不是 shell(=桌面)进程: %s" % ("是" if pid_win == shell_pid() else "否"))
    if t_appear is None:
        print("     ✗ 6 秒内没等到新窗口")
    else:
        print("     窗口出现 +%dms   地址栏可读 +%s   文件列表 +%s   又自己显示过 %d 次" % (
            t_appear, ("%dms" % t_addr) if t_addr else "** 2.5s 内一直读不到 **",
            ("%dms" % t_view) if t_view else ">2.5s", shown_again))
        print("     地址栏内容: %r" % (addr_val,))
    if cab is not None and u.IsWindow(cab):
        u.PostMessageW(cab, WM_CLOSE, 0, 0)
        time.sleep(0.6)
        print("     关窗: %s" % ("已关" if not u.IsWindow(cab) else "** 还开着 **"))
    if launcher_alive_1:
        print("     ⚠ launcher 进程仍在（说明它就是那个 explorer）")
    k.CloseHandle(pi.hProcess)
    time.sleep(0.8)


def main():
    n = int(sys.argv[1]) if len(sys.argv) > 1 else 3
    targets = [r"D:/Dev/AI", r"D:/Dev/!tmp", r"D:/Software/!Sync",
               r"D:/Dev/Workspaces/WorkBuddy", "shell:MyComputerFolder"]
    print("留给前一轮窗口关闭的时间…")
    time.sleep(1.5)
    for i in range(n):
        for t in targets:
            run_one(t, "第%d轮" % (i + 1))
    print("\n===== 剩余顶层浏览窗口（应该为 0）=====")
    for h in cabs():
        print("   hwnd=0x%08X pid=%d 可见=%d 标题=%r" % (
            h, pid_of(h), 1 if u.IsWindowVisible(h) else 0, text(h)))
    print("剩余 explorer 进程: %s" % sorted(explorer_pids()))
    print("shell(桌面) explorer pid: %d" % shell_pid())


if __name__ == "__main__":
    main()
