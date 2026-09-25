# -*- coding: utf-8 -*-
"""只读探针：`explorer.exe /n,/separate` 的**启动开销能不能随并发变多而摊薄**。

要回答的问题：一次同时起 4 个，比一个一个起，整批是快了、还是白忙？
（TabbedExplorer 的「并发起标签页」开关值不值得留，就看这里。）

三种跑法，各起同一批 8 个文件夹：
  serial  —— 一次起 1 个，等它就绪（地址栏可读）再起下一个
  par4    —— 一次起 4 个，每个都**照程序那样每 25ms 读一次地址栏**
  par4np  —— 一次起 4 个，但**完全不读地址栏**（只看 `SHELLDLL_DefView` 建没建好）

为什么要 par4np 这一路：程序并发时要靠「地址栏内容 == 我要开的路径」认领窗口，
那是跨进程同步消息，会**卡住对方 UI 线程**。par4np 与 par4 一比，
就能把「explorer 自己扛不住并发」和「我们自己的轮询拖慢了它」分开。

窗口一出现立刻 `SW_HIDE`（照程序的做法），全程不动鼠标、不抢前台。
跑完 `WM_CLOSE` 关窗 + 杀掉本次新起的 explorer 进程（绝不碰桌面 shell 那个）。
用法：
    python launch_concurrency_bench.py            # 三种全跑
    python launch_concurrency_bench.py serial     # 只跑一种
"""
import ctypes
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
u.SendMessageW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
u.FindWindowW.argtypes = [wintypes.LPCWSTR, wintypes.LPCWSTR]
u.FindWindowW.restype = wintypes.HWND
k.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
k.CloseHandle.argtypes = [wintypes.HANDLE]
k.TerminateProcess.argtypes = [wintypes.HANDLE, wintypes.UINT]
k.CreateToolhelp32Snapshot.argtypes = [wintypes.DWORD, wintypes.DWORD]

CAB = ("CabinetWClass", "ExploreWClass")
WM_CLOSE = 0x0010
WM_GETTEXT = 0x000D
SW_HIDE = 0
POLL_MS = 25                      # 跟程序一样
BUDGET_S = 20.0                   # 单个窗口最多等这么久


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
                             ctypes.POINTER(STARTUPINFOW), ctypes.POINTER(PROCESS_INFORMATION)]


def cls(h):
    b = ctypes.create_unicode_buffer(256)
    u.GetClassNameW(h, b, 256)
    return b.value


def text(h):
    n = u.GetWindowTextLengthW(h) + 1
    b = ctypes.create_unicode_buffer(n)
    u.GetWindowTextW(h, b, n)
    return b.value


def text_x(h):
    """跨进程读文本 —— 跟程序的 EmbedApi.WindowTextOf 一样走 WM_GETTEXT。"""
    b = ctypes.create_unicode_buffer(1024)
    u.SendMessageW(h, WM_GETTEXT, 1024, ctypes.addressof(b))
    return b.value


def pid_of(h):
    p = wintypes.DWORD()
    u.GetWindowThreadProcessId(h, ctypes.byref(p))
    return p.value


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
            if cls(c) == "ToolbarWindow32" and ": " in text_x(c):
                band = c
                break
    if band is None:
        return None
    raw = text_x(band)
    _, _, rest = raw.partition(": ")
    return rest.strip() or None


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


def shell_pid():
    h = u.FindWindowW("Shell_TrayWnd", None)
    return pid_of(h) if h else 0


# ----------------------------------------------------------------------
# 一个「在飞的窗口」：起完就交给 tick() 推进，直到就绪
# ----------------------------------------------------------------------
class Flight(object):
    def __init__(self, target, t0, hproc):
        self.target = target
        self.t0 = t0
        self.hproc = hproc
        self.cab = None
        self.before = None      # 本批开跑前的窗口快照（同批共用）
        self.taken = None       # 本批已被别人认领的窗口（同批共用，防抢）
        self.t_appear = None
        self.t_addr = None
        self.t_view = None
        self.addr_val = None
        self.done = False

    def latency(self):
        """就绪时刻（看模式：读地址栏的那种以地址栏为准，不读的以文件列表为准）。"""
        return self.t_addr if self.t_addr is not None else self.t_view

    def label(self):
        return self.target.replace("\\", "/").rsplit("/", 1)[-1] or self.target


def spawn(target):
    cmd = 'explorer.exe /n,/separate,"%s"' % target
    si = STARTUPINFOW()
    si.cb = ctypes.sizeof(STARTUPINFOW)
    pi = PROCESS_INFORMATION()
    buf = ctypes.create_unicode_buffer(cmd)
    ok = k.CreateProcessW(None, buf, None, None, False, 0, None, None,
                          ctypes.byref(si), ctypes.byref(pi))
    if not ok:
        return None
    k.CloseHandle(pi.hThread)
    return Flight(target, time.time(), pi.hProcess), pi.dwProcessId


def tick(f, want_addr, now):
    """推进一个在飞的窗口；返回 True 表示它已经就绪。"""
    if f.cab is None:
        for h in cabs():
            if h in f.before or h in f.taken:
                continue
            f.taken.add(h)                    # 同批里一个窗口只能被一个人认领
            f.cab = h
            f.t_appear = (now - f.t0) * 1000
            u.ShowWindow(h, SW_HIDE)          # 程序就是这么干的：发现即藏
            break
        return False
    if u.IsWindow(f.cab):
        if want_addr and f.t_addr is None:
            p = read_path(f.cab)
            if p:
                f.t_addr = (now - f.t0) * 1000
                f.addr_val = p
        if f.t_view is None and has_defview(f.cab):
            f.t_view = (now - f.t0) * 1000
    if want_addr:
        return f.t_addr is not None and f.t_view is not None
    return f.t_view is not None


def run_mode(name, batch, count, want_addr):
    """batch = 一批同时起几个；count = 总共起几个。"""
    targets = [r"D:\Dev", r"D:\Dev\AI", r"D:\Dev\!tmp", r"D:\Software\!Sync",
               r"D:\Backups", r"D:\Users", r"D:\Dev\Workspaces",
               r"D:\Dev\Workspaces\WorkBuddy"]
    targets = (targets * ((count // len(targets)) + 1))[:count]

    print("=" * 66)
    print("【%s】一次起 %d 个，共 %d 个；轮询地址栏=%s"
          % (name, batch, count, "是" if want_addr else "否"))
    baseline = explorer_pids()          # 收尾时「比它多出来的 explorer」一律算我们的
    flights = []
    procs = []
    batch_no = 0
    widx = 0
    wall0 = time.time()
    pending = []

    while widx < count or pending:
        if widx < count and len(pending) < batch:
            # 一批共用一个「开跑前的窗口快照」+ 一个「已被认领」集合 —— 一批里谁先看见哪扇窗
            # 就归谁，否则后起的那个会把前一个的窗户也认成自己的（地址栏就会串台）。
            before = set(cabs())
            taken = set()
            while widx < count and len(pending) < batch:
                r = spawn(targets[widx])
                widx += 1
                if r is None:
                    continue
                f, pid = r
                f.before = before
                f.taken = taken
                pending.append(f)
                procs.append(pid)
            batch_no += 1
        now = time.time()
        still = []
        for f in pending:
            if tick(f, want_addr, now) or now - f.t0 > BUDGET_S:
                f.done = True
                flights.append(f)
            else:
                still.append(f)
        pending = still
        if pending:
            time.sleep(POLL_MS / 1000.0)

    wall = time.time() - wall0

    lat = sorted(f.latency() for f in flights if f.latency() is not None)
    print("  整批墙钟 %.2fs   就绪 %d/%d" % (wall, len(lat), len(flights)))
    if lat:
        print("  每个窗口就绪耗时(ms): min %d / 中位 %d / max %d"
              % (lat[0], lat[len(lat) // 2], lat[-1]))
    for f in flights:
        print("    %-28s 出现+%s  地址栏+%s  文件列表+%s  %r"
              % (f.label(),
                 ("%d" % f.t_appear) if f.t_appear else "-",
                 ("%d" % f.t_addr) if f.t_addr else "-",
                 ("%d" % f.t_view) if f.t_view else "-",
                 f.addr_val))

    # ---- 收尾：关窗 + 杀掉本次新起的 explorer（绝不碰桌面 shell）----
    # ⚠ `/n,/separate` 交给我们的 launcher pid **不是**窗口属主（窗口属主是另一个新的
    #   explorer 进程），所以只杀 launcher 会留下一堆游离进程 ⇒ 一律按「比基线多出来的」杀。
    closed = 0
    for f in flights:
        if f.cab and u.IsWindow(f.cab):
            u.PostMessageW(f.cab, WM_CLOSE, 0, 0)
            closed += 1
    time.sleep(1.2)
    left = 0
    for f in flights:
        if f.cab and u.IsWindow(f.cab):
            u.PostMessageW(f.cab, WM_CLOSE, 0, 0)
            left += 1
    time.sleep(0.8)
    print("  关窗: 发出 %d 次，仍开着 %d 个" % (closed + left, left))

    for f in flights:
        k.CloseHandle(f.hproc)
    killed = []
    for p in sorted(explorer_pids() - baseline):
        hp = k.OpenProcess(0x0001, False, p)       # PROCESS_TERMINATE
        if hp:
            k.TerminateProcess(hp, 0)
            k.CloseHandle(hp)
            killed.append(p)
    time.sleep(1.0)
    rest = sorted(explorer_pids() - baseline)
    print("  清理: 结束 %d 个本次新起的 explorer（launcher %d 个），残留 %s"
          % (len(killed), len(set(procs)), rest))
    return wall, (lat[len(lat) // 2] if lat else None), len(lat)


def main():
    which = sys.argv[1] if len(sys.argv) > 1 else "all"
    print("留给前一轮窗口关闭的时间…")
    time.sleep(1.5)
    if cabs():
        print("⚠ 现在已经有浏览窗口开着，判据会不准，先请它们关掉：")
        for h in cabs():
            print("   hwnd=0x%08X pid=%d 标题=%r" % (h, pid_of(h), text(h)))
        return
    print("本次基线：桌面 shell explorer pid = %d，现存 explorer = %s"
          % (shell_pid(), sorted(explorer_pids())))

    res = {}
    if which in ("all", "serial"):
        res["serial"] = run_mode("serial", 1, 6, True)
        time.sleep(1.5)
    if which in ("all", "par4"):
        res["par4"] = run_mode("par4", 4, 8, True)
        time.sleep(1.5)
    if which in ("all", "par4np"):
        res["par4np"] = run_mode("par4np", 4, 8, False)

    if len(res) > 1:
        print("=" * 66)
        print("小结（墙钟 = 起完这一批总共花了多久）")
        for kk in ("serial", "par4", "par4np"):
            if kk in res:
                w, med, n = res[kk]
                print("  %-8s 墙钟 %5.2fs   单窗中位 %s ms   就绪 %d"
                      % (kk, w, med if med is not None else "-", n))
    print("shell(桌面) explorer pid: %d   （不该有别的 explorer 了）"
          % shell_pid())


if __name__ == "__main__":
    main()
