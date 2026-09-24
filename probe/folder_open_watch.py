"""只读探针：三方应用点「打开文件夹」那一下，系统里到底发生了什么。

背景（2026-09-24 排查）：
  目标文件夹**已经开在某个标签里**时，点「打开文件夹」毫无反应，而数据/log.txt 里
  **一行都没有** —— 没有新窗口、没有转生记录。候选解释有三条，本探针一次全测：

  ① 新起了一个 explorer.exe，但它是「转发进程」：把路径交给已有窗口后自己就退出
     ⇒ 一个窗口都不多，但**命令行里有那个路径**。测法：高频快照进程 + 立刻读它的命令行。
  ② shell 去「激活」那扇已有的窗口，只是激活落到了我们 SetParent 来的子窗口上
     ⇒ 测法：盯前台窗口，和每扇窗口所属线程的 活动窗/焦点窗（GetGUIThreadInfo）。
  ③ shell 把窗口提到了 z 序前面 / 显示了一遍（BringWindowToTop / ShowWindow）
     ⇒ 测法：盯每个标签在**它的父窗**里的 z 位次，以及它自己的可见位。

三条都不动 ⇒ 外部拿不到任何信号，只能当已知限制；
任一条有动静 ⇒ 就有了「切到那个标签」的抓手。

⚠ 找我们自己的窗口**不能用「面积 > 300」**：窗口最小化时（或收进托盘时）顶层窗的
   `GetWindowRect` 是一小块，会被漏掉。真做法是**按 pid 找顶层窗、再用原生
   `EnumChildWindows` 在它子树里找 CabinetWClass** —— 子树是缩进层级，最小化不影响。

用法：python folder_open_watch.py [秒数=180] [输出文件]
纯 ctypes、只读；不点鼠标、不抢前台、不改任何东西。
"""
import ctypes
import sys
import time
from ctypes import wintypes

u32 = ctypes.windll.user32
k32 = ctypes.windll.kernel32
ntdll = ctypes.windll.ntdll
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)     # 本机 150%：不声明会把坐标虚拟化
except Exception:
    pass

# ---- 声明类型：64 位下不声明会把句柄按 c_int 截断（踩过） ----
u32.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
u32.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
u32.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
u32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(ctypes.c_ulong)]
u32.GetWindowThreadProcessId.restype = ctypes.c_ulong
u32.GetParent.argtypes = [wintypes.HWND]
u32.GetParent.restype = wintypes.HWND
u32.GetWindow.argtypes = [wintypes.HWND, ctypes.c_uint]
u32.GetWindow.restype = wintypes.HWND
u32.GetForegroundWindow.restype = wintypes.HWND
u32.IsWindowVisible.argtypes = [wintypes.HWND]
u32.EnumWindows.argtypes = [ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM),
                            wintypes.LPARAM]
u32.EnumChildWindows.argtypes = [wintypes.HWND,
                                 ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM),
                                 wintypes.LPARAM]
k32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
k32.OpenProcess.restype = wintypes.HANDLE
k32.ReadProcessMemory.argtypes = [wintypes.HANDLE, ctypes.c_void_p, ctypes.c_void_p,
                                 ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]
k32.CloseHandle.argtypes = [wintypes.HANDLE]
k32.CreateToolhelp32Snapshot.argtypes = [wintypes.DWORD, wintypes.DWORD]
k32.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
ntdll.NtQueryInformationProcess.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p,
                                           ctypes.c_ulong, ctypes.POINTER(ctypes.c_ulong)]

GW_HWNDFIRST = 0
GW_HWNDNEXT = 2
TH32CS_SNAPPROCESS = 0x00000002
PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
PROCESS_VM_READ = 0x0010
CAB_CLASSES = ("CabinetWClass", "ExploreWClass")

EnumProc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)


class GUITHREADINFO(ctypes.Structure):
    _fields_ = [("cbSize", wintypes.DWORD), ("flags", wintypes.DWORD),
                ("hwndActive", wintypes.HWND), ("hwndFocus", wintypes.HWND),
                ("hwndCapture", wintypes.HWND), ("hwndMenuOwner", wintypes.HWND),
                ("hwndMoveSize", wintypes.HWND), ("hwndCaret", wintypes.HWND),
                ("rcCaret", wintypes.RECT)]


class PROCESSENTRY32W(ctypes.Structure):
    _fields_ = [("dwSize", wintypes.DWORD), ("cntUsage", wintypes.DWORD),
                ("th32ProcessID", wintypes.DWORD), ("th32DefaultHeapID", ctypes.c_size_t),
                ("th32ModuleID", wintypes.DWORD), ("cntThreads", wintypes.DWORD),
                ("th32ParentProcessID", wintypes.DWORD), ("pcPriClassBase", ctypes.c_long),
                ("dwFlags", wintypes.DWORD), ("szExeFile", ctypes.c_wchar * 260)]


class PROCESS_BASIC_INFORMATION(ctypes.Structure):
    _fields_ = [("Reserved1", ctypes.c_void_p), ("PebBaseAddress", ctypes.c_void_p),
                ("Reserved2", ctypes.c_void_p * 2), ("UniqueProcessId", ctypes.c_void_p),
                ("InheritedFromUniqueProcessId", ctypes.c_void_p)]


_log = None


def say(s):
    print(s, flush=True)
    if _log:
        _log.write(s + "\n")
        _log.flush()


def text(h):
    b = ctypes.create_unicode_buffer(512)
    u32.GetWindowTextW(h, b, 512)
    return b.value


def cls(h):
    b = ctypes.create_unicode_buffer(256)
    u32.GetClassNameW(h, b, 256)
    return b.value


def pid_of(h):
    p = ctypes.c_ulong(0)
    u32.GetWindowThreadProcessId(h, ctypes.byref(p))
    return p.value


def tid_of(h):
    p = ctypes.c_ulong(0)
    return u32.GetWindowThreadProcessId(h, ctypes.byref(p))


def rect(h):
    r = wintypes.RECT()
    u32.GetWindowRect(h, ctypes.byref(r))
    return (r.left, r.top, r.right - r.left, r.bottom - r.top)


def top_windows():
    out = []

    def cb(h, l):
        out.append(h)
        return True

    u32.EnumWindows(EnumProc(cb), 0)
    return out


def sub_tree(root):
    """root 自己 + 它所有后代（原生枚举，快）。"""
    out = [root]

    def cb(h, l):
        out.append(h)
        return True

    u32.EnumChildWindows(root, EnumProc(cb), 0)
    return out


def sibling_z(h):
    """h 在**它自己那一层**里的 z 位次（0 = 最上）。
    用「往前数同级」而不是「从父窗第一个往后数」—— 后者要先拿对父窗句柄，
    而重定父之后的窗口在这一项上不如同级链稳。"""
    i = 0
    x = u32.GetWindow(h, 3)      # GW_HWNDPREV
    while x and i < 500:
        i += 1
        x = u32.GetWindow(x, 3)
    return i


def address_of(sub):
    """标签现在显示哪个文件夹 —— 地址栏那个 ToolbarWindow32 的窗口文本就是地址本身。"""
    for h in sub:
        if cls(h) == "ToolbarWindow32":
            t = text(h)
            if ":\\" in t:
                return t
    return "?"


def thread_state(tid):
    """某线程的 活动窗 / 焦点窗 —— 跨进程也问得到。"""
    g = GUITHREADINFO()
    g.cbSize = ctypes.sizeof(GUITHREADINFO)
    if not u32.GetGUIThreadInfo(tid, ctypes.byref(g)):
        return (0, 0)
    return (g.hwndActive or 0, g.hwndFocus or 0)


def cmdline(pid):
    """读**别人**进程的命令行（走 PEB）。只能同权限，读不到返回 None。"""
    h = k32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, False, pid)
    if not h:
        return None
    try:
        pbi = PROCESS_BASIC_INFORMATION()
        got = ctypes.c_ulong(0)
        if ntdll.NtQueryInformationProcess(h, 0, ctypes.byref(pbi),
                                           ctypes.sizeof(pbi), ctypes.byref(got)) != 0:
            return None
        pp = ctypes.c_void_p(0)
        n = ctypes.c_size_t(0)
        if not k32.ReadProcessMemory(h, ctypes.c_void_p(pbi.PebBaseAddress + 0x20),
                                     ctypes.byref(pp), 8, ctypes.byref(n)):
            return None
        ln = ctypes.c_ushort(0)
        buf = ctypes.c_void_p(0)
        if not k32.ReadProcessMemory(h, ctypes.c_void_p(pp.value + 0x70),
                                     ctypes.byref(ln), 2, ctypes.byref(n)):
            return None
        if not k32.ReadProcessMemory(h, ctypes.c_void_p(pp.value + 0x78),
                                     ctypes.byref(buf), 8, ctypes.byref(n)):
            return None
        if not ln.value or not buf.value:
            return ""
        raw = ctypes.create_unicode_buffer(ln.value // 2 + 1)
        if not k32.ReadProcessMemory(h, ctypes.c_void_p(buf.value), raw,
                                     ln.value, ctypes.byref(n)):
            return ""
        return raw.value
    except Exception:
        return None
    finally:
        k32.CloseHandle(h)


def explorer_pids():
    """当前所有 explorer.exe 的 pid -> (父 pid, 线程数)。"""
    out = {}
    snap = k32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
    if not snap or snap == ctypes.c_void_p(-1).value:
        return out
    try:
        pe = PROCESSENTRY32W()
        pe.dwSize = ctypes.sizeof(PROCESSENTRY32W)
        ok = k32.Process32FirstW(snap, ctypes.byref(pe))
        while ok:
            if pe.szExeFile.lower() == "explorer.exe":
                out[pe.th32ProcessID] = (pe.th32ParentProcessID, pe.cntThreads)
            ok = k32.Process32NextW(snap, ctypes.byref(pe))
    finally:
        k32.CloseHandle(snap)
    return out


def scan():
    """返回 (我们的顶层窗清单, 标签清单)。标签不看面积、不看可见 —— 最小化也照样找得到。"""
    forms = []
    tabs = []
    for top in top_windows():
        if pid_of(top) not in target_pids:
            continue
        sub = sub_tree(top)
        cabs = [h for h in sub if cls(h) in CAB_CLASSES]
        if not cabs:
            continue
        forms.append((top, cls(top), text(top), rect(top), bool(u32.IsWindowVisible(top))))
        for h in cabs:
            # ⚠ 地址要**按这个标签自己的子树**读：拿整棵树的子树读到的永远是第一个标签那个地址
            tabs.append({"h": h, "z": sibling_z(h), "title": text(h), "addr": address_of(sub_tree(h)),
                         "par": u32.GetParent(h), "tid": tid_of(h),
                         "vis": bool(u32.IsWindowVisible(h))})
    return forms, tabs


# ---------------- 找 TabbedExplorer ----------------
target_pids = set()
try:
    import subprocess
    rows = subprocess.run(["tasklist", "/FI", "IMAGENAME eq TabbedExplorer.exe", "/FO", "CSV", "/NH"],
                          capture_output=True, text=True, errors="replace").stdout
    for row in rows.splitlines():
        parts = [x.strip('"') for x in row.split('","')]
        if len(parts) > 1 and parts[1].isdigit():
            target_pids.add(int(parts[1]))
except Exception as e:
    print("tasklist 失败:", e)

if not target_pids:
    print("TabbedExplorer.exe 没在跑")
    sys.exit(1)

secs = int(sys.argv[1]) if len(sys.argv) > 1 else 180
if len(sys.argv) > 2:
    _log = open(sys.argv[2], "a", encoding="utf-8")

say("指针 %d 位 | TabbedExplorer pid=%s" % (ctypes.sizeof(ctypes.c_void_p) * 8, sorted(target_pids)))
forms0, tabs0 = scan()
for h, k, t, r, v in forms0:
    say("主窗 0x%X 可见=%s %s \"%s\"" % (h, v, r, t[:40]))
say("现有 explorer.exe: " + ", ".join("%d(父%d/线程%d)" % (p, v[0], v[1])
                                      for p, v in sorted(explorer_pids().items())))
say("标签清单:")
for t in tabs0:
    say("  0x%-10X z=%-3d 父=0x%-9X tid=%-6d 可见=%-5s \"%s\" 文件夹=%s"
        % (t["h"], t["z"], t["par"], t["tid"], t["vis"], t["title"][:22], t["addr"]))
say("开始观察 %d 秒 —— 现在去点「打开文件夹」" % secs)

known = dict(explorer_pids())
born = {}
last_fg = u32.GetForegroundWindow()
last_sig = None
last_thr = {}
t0 = time.time()

while time.time() - t0 < secs:
    now = time.time() - t0

    # ① 新 explorer 进程 + 命令行（转发进程只有几十~几百毫秒）
    cur = explorer_pids()
    for p in sorted(set(cur) - set(known)):
        cl_ = cmdline(p)
        born[p] = (cl_, now)
        say("[%.2f] 新进程 explorer.exe pid=%d 父=%d 线程=%d 命令行=%s"
            % (now, p, cur[p][0], cur[p][1], repr(cl_)))
    for p in sorted(set(known) - set(cur)):
        b = born.pop(p, (None, now))
        say("[%.2f] 进程退出 pid=%d 存活 %.2fs 命令行=%s" % (now, p, now - b[1], repr(b[0])))
    known = cur

    # ② 前台
    fg = u32.GetForegroundWindow()
    if fg != last_fg:
        say("[%.2f] 前台 -> 0x%X [%s] pid=%d 可见=%s \"%s\""
            % (now, fg, cls(fg), pid_of(fg), bool(u32.IsWindowVisible(fg)), text(fg)[:40]))
        last_fg = fg

    # ③ 标签集合 / z 序 / 可见位
    _, tabs = scan()
    sig = tuple((t["h"], t["z"], t["vis"]) for t in tabs)
    if sig != last_sig:
        if last_sig is not None:
            say("[%.2f] 标签(z序/可见)变化 ->" % now)
            for t in tabs:
                say("      0x%-10X z=%-3d 父=0x%-9X 可见=%-5s \"%s\" 文件夹=%s"
                    % (t["h"], t["z"], t["par"], t["vis"], t["title"][:22], t["addr"]))
        last_sig = sig

    # ④ 各标签线程的活动窗/焦点窗
    for t in tabs:
        act, foc = thread_state(t["tid"])
        if last_thr.get(t["h"]) != (act, foc):
            say("[%.2f] 线程态变 标签=0x%X \"%s\" tid=%d 活动=0x%X 焦点=0x%X"
                % (now, t["h"], t["title"][:22], t["tid"], act, foc))
            last_thr[t["h"]] = (act, foc)

    time.sleep(0.015)

say("观察结束")
if _log:
    _log.close()
