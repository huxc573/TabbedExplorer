"""应用版：三方点「打开文件夹」时，shell 的动作落到我们**已收编的子窗**上，有没有可观测痕迹。

背景（2026-09-24/25 排查）：
  已用 probe/reuse_probe.py 在**普通顶层** explorer 窗上坐实：目标文件夹已有窗口时，
  shell 不新建窗口，而是对那扇窗做「显示 + 抢前台」（隐藏它 → 触发的瞬间它自己冒出来、
  且前台切到它）。对照实验证明这不是自发行为。

  于是问题变成：这套「显示 + 抢前台」落到 TabbedExplorer 里**已被 SetParent 成子窗**的
  那扇 cab 上，会在我们的进程里留下什么痕迹？本探针把可能的痕迹一次全盯：

    ① WinEvent（跨进程、不需要往别人进程注入 DLL）：
       OBJECT_SHOW / OBJECT_HIDE / OBJECT_FOCUS / OBJECT_STATECHANGE /
       OBJECT_PARENTCHANGE / SYSTEM_FOREGROUND / SYSTEM_MINIMIZE* —— 落在我们 cab
       或我们宿主窗上的任何一条。
    ② cab 自己的 WS_VISIBLE 位（子窗被 ShowWindow 会翻这一位，即使父窗藏着看不到）。
    ③ cab 的 IsWindowVisible（含祖先）、父窗句柄、它在同级里的 z 位次。
    ④ cab 所属线程的 hwndActive / hwndFocus（跨进程可读）。
    ⑤ 宿主窗（我们的 EmbedForm）的可见位 / 最小化位，以及系统前台。
    ⑥ 新起的 explorer.exe 进程。

  有痕迹 ⇒ 就有「切到那个标签」的抓手；全无 ⇒ shell 的动作对我们完全不可感知，
  只能换思路（让 shell 匹配不到我们，或自建可控的代理窗）。

用法：python reuse_app_watch.py <路径> [秒数=14] [延迟=3] [输出文件] [--hide-inactive]
  --hide-inactive: 把「非活动标签」cab 自己的 WS_VISIBLE 也清掉，验证
                   「shell 匹配后会对那扇窗调 ShowWindow(SW_SHOW)」这条猜想
                   （平时只藏父窗 ⇒ 那一下无操作、不留痕；清掉后应冒出一条
                    EVENT_OBJECT_SHOW，从而带出到底是哪个标签）。
纯只读 + 只改非活动标签 cab 的可见位（结束前恢复）；触发用 ShellExecuteW('open')
（本机沙箱会吃掉 Bash 里的直调，故须走计划任务）。
"""
import ctypes
import ctypes.wintypes as wt
import os
import subprocess
import sys
import threading
import time

u32 = ctypes.windll.user32
k32 = ctypes.windll.kernel32
shell32 = ctypes.windll.shell32
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)     # 本机 150%：不声明坐标会被虚拟化
except Exception:
    pass

# ---- 声明类型：64 位下句柄不声明会被按 c_int 截断（踩过） ----
u32.GetClassNameW.argtypes = [wt.HWND, wt.LPWSTR, ctypes.c_int]
u32.GetWindowTextW.argtypes = [wt.HWND, wt.LPWSTR, ctypes.c_int]
u32.GetWindowRect.argtypes = [wt.HWND, ctypes.POINTER(wt.RECT)]
u32.GetWindowThreadProcessId.argtypes = [wt.HWND, ctypes.POINTER(ctypes.c_ulong)]
u32.GetWindowThreadProcessId.restype = ctypes.c_ulong
u32.GetParent.argtypes = [wt.HWND]
u32.GetParent.restype = wt.HWND
u32.GetWindow.argtypes = [wt.HWND, ctypes.c_uint]
u32.GetWindow.restype = wt.HWND
u32.GetForegroundWindow.restype = wt.HWND
u32.IsWindowVisible.argtypes = [wt.HWND]
u32.IsWindowVisible.restype = wt.BOOL
u32.IsWindow.argtypes = [wt.HWND]
u32.IsWindow.restype = wt.BOOL
u32.IsIconic.argtypes = [wt.HWND]
u32.IsIconic.restype = wt.BOOL
u32.EnumWindows.argtypes = [ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM), wt.LPARAM]
u32.EnumChildWindows.argtypes = [wt.HWND,
                                 ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM),
                                 wt.LPARAM]
u32.GetWindowLongPtrW.argtypes = [wt.HWND, ctypes.c_int]
u32.GetWindowLongPtrW.restype = ctypes.c_longlong
u32.SetWinEventHook.argtypes = [ctypes.c_uint, ctypes.c_uint, wt.HMODULE,
                                ctypes.c_void_p, ctypes.c_ulong, ctypes.c_ulong,
                                ctypes.c_uint]
u32.SetWinEventHook.restype = ctypes.c_void_p
u32.UnhookWinEvent.argtypes = [ctypes.c_void_p]
u32.PeekMessageW.argtypes = [ctypes.c_void_p, wt.HWND, ctypes.c_uint, ctypes.c_uint,
                             ctypes.c_uint]
shell32.ShellExecuteW.argtypes = [wt.HWND, wt.LPCWSTR, wt.LPCWSTR, wt.LPCWSTR,
                                  wt.LPCWSTR, ctypes.c_int]
shell32.ShellExecuteW.restype = wt.HINSTANCE
k32.CreateToolhelp32Snapshot.argtypes = [wt.DWORD, wt.DWORD]
k32.CreateToolhelp32Snapshot.restype = wt.HANDLE

GWL_STYLE = -16
WS_VISIBLE = 0x10000000
TH32CS_SNAPPROCESS = 0x00000002
CAB_CLASSES = ("CabinetWClass", "ExploreWClass")
PM_REMOVE = 0x0001
WINEVENT_OUTOFCONTEXT = 0x0000
GW_HWNDPREV = 3

EVT = {
    0x0003: "SYSTEM_FOREGROUND", 0x0004: "SYSTEM_MENUSTART", 0x0008: "SYSTEM_CAPTURESTART",
    0x0009: "SYSTEM_CAPTUREEND", 0x0016: "SYSTEM_MINIMIZESTART",
    0x0017: "SYSTEM_MINIMIZEEND", 0x0010: "SYSTEM_DIALOGSTART", 0x0011: "SYSTEM_DIALOGEND",
    0x8000: "OBJECT_CREATE", 0x8001: "OBJECT_DESTROY", 0x8002: "OBJECT_SHOW",
    0x8003: "OBJECT_HIDE", 0x8004: "OBJECT_REORDER", 0x8005: "OBJECT_FOCUS",
    0x8006: "OBJECT_SELECTION", 0x800A: "OBJECT_STATECHANGE",
    0x800B: "OBJECT_LOCATIONCHANGE", 0x800C: "OBJECT_NAMECHANGE",
    0x800E: "OBJECT_VALUECHANGE", 0x800F: "OBJECT_PARENTCHANGE",
    0x8013: "OBJECT_INVOKED",
}


class GUITHREADINFO(ctypes.Structure):
    _fields_ = [("cbSize", wt.DWORD), ("flags", wt.DWORD),
                ("hwndActive", wt.HWND), ("hwndFocus", wt.HWND),
                ("hwndCapture", wt.HWND), ("hwndMenuOwner", wt.HWND),
                ("hwndMoveSize", wt.HWND), ("hwndCaret", wt.HWND),
                ("rcCaret", wt.RECT)]


class PROCESSENTRY32W(ctypes.Structure):
    _fields_ = [("dwSize", wt.DWORD), ("cntUsage", wt.DWORD),
                ("th32ProcessID", wt.DWORD), ("th32DefaultHeapID", ctypes.c_size_t),
                ("th32ModuleID", wt.DWORD), ("cntThreads", wt.DWORD),
                ("th32ParentProcessID", wt.DWORD), ("pcPriClassBase", ctypes.c_long),
                ("dwFlags", wt.DWORD), ("szExeFile", ctypes.c_wchar * 260)]


EnumProc = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
WinEventProc = ctypes.WINFUNCTYPE(None, ctypes.c_void_p, ctypes.c_uint, ctypes.c_void_p,
                                  ctypes.c_long, ctypes.c_long, ctypes.c_ulong,
                                  ctypes.c_ulong)

_log = None
_t0 = time.time()


def say(s):
    s = "[%.3f] %s" % (time.time() - _t0, s)
    print(s, flush=True)
    if _log:
        _log.write(s + "\n")
        _log.flush()


def cls(h):
    b = ctypes.create_unicode_buffer(256)
    u32.GetClassNameW(h, b, 256)
    return b.value


def text(h):
    b = ctypes.create_unicode_buffer(512)
    u32.GetWindowTextW(h, b, 512)
    return b.value


def pid_of(h):
    p = ctypes.c_ulong(0)
    u32.GetWindowThreadProcessId(h, ctypes.byref(p))
    return p.value


def tid_of(h):
    p = ctypes.c_ulong(0)
    return u32.GetWindowThreadProcessId(h, ctypes.byref(p))


def tops():
    out = []

    def cb(h, l):
        out.append(h or 0)
        return True

    u32.EnumWindows(EnumProc(cb), 0)
    return out


def sub_tree(root):
    out = [root]

    def cb(h, l):
        out.append(h or 0)
        return True

    u32.EnumChildWindows(root, EnumProc(cb), 0)
    return out


def style_vis(h):
    return bool(u32.GetWindowLongPtrW(h, GWL_STYLE) & WS_VISIBLE)


def sibling_z(h):
    i, x = 0, u32.GetWindow(h, GW_HWNDPREV)
    while x and i < 500:
        i += 1
        x = u32.GetWindow(x, GW_HWNDPREV)
    return i


def address_of(root):
    for h in sub_tree(root):
        if cls(h) == "ToolbarWindow32":
            t = text(h)
            if ":\\" in t:
                return t
    return ""


def thread_state(tid):
    g = GUITHREADINFO()
    g.cbSize = ctypes.sizeof(GUITHREADINFO)
    if not u32.GetGUIThreadInfo(tid, ctypes.byref(g)):
        return (0, 0)
    return (g.hwndActive or 0, g.hwndFocus or 0)


def explorer_pids():
    out = set()
    snap = k32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
    if not snap or snap == ctypes.c_void_p(-1).value:
        return out
    try:
        pe = PROCESSENTRY32W()
        pe.dwSize = ctypes.sizeof(PROCESSENTRY32W)
        ok = k32.Process32FirstW(snap, ctypes.byref(pe))
        while ok:
            if pe.szExeFile.lower() == "explorer.exe":
                out.add(pe.th32ProcessID)
            ok = k32.Process32NextW(snap, ctypes.byref(pe))
    finally:
        k32.CloseHandle(snap)
    return out


# ---------------- 参数 ----------------
_pos = [a for a in sys.argv[1:] if not a.startswith("--")]
_flags = {a for a in sys.argv[1:] if a.startswith("--")}
if len(_pos) < 1:
    print(__doc__)
    sys.exit(1)
path = _pos[0]
secs = int(_pos[1]) if len(_pos) > 1 else 14
delay = float(_pos[2]) if len(_pos) > 2 else 3.0
if len(_pos) > 3:
    _log = open(_pos[3], "a", encoding="utf-8")
else:
    _log = open(os.path.join(os.path.dirname(os.path.abspath(__file__)),
                             "reuse_app_watch.log"), "a", encoding="utf-8")
# --hide-inactive: 把「非活动标签」cab 自己的 WS_VISIBLE 也清掉。
#   目的：验证「shell 激活前会对匹配窗调 ShowWindow(SW_SHOW)」这条猜想 ——
#   我们平时只藏父窗，cab 自己的 WS_VISIBLE 是 1，那一下是无操作、不留痕；
#   清掉之后，若 shell 真调了，就会在该 cab 上冒出一条 EVENT_OBJECT_SHOW（带出是哪个标签）。
hide_inactive = "--hide-inactive" in _flags

# ---------------- 找 TabbedExplorer 及其标签 ----------------
target_pids = set()
try:
    rows = subprocess.run(["tasklist", "/FI", "IMAGENAME eq TabbedExplorer.exe",
                           "/FO", "CSV", "/NH"], capture_output=True, text=True,
                          errors="replace").stdout
    for row in rows.splitlines():
        parts = [x.strip('"') for x in row.split('","')]
        if len(parts) > 1 and parts[1].isdigit():
            target_pids.add(int(parts[1]))
except Exception as e:
    say("tasklist 失败: %s" % e)

say("=" * 78)
say("路径=%s 观察=%ss 触发延迟=%.1fs TabbedExplorer pid=%s"
    % (path, secs, delay, sorted(target_pids) or "没在跑"))

if not target_pids:
    say("TabbedExplorer 没在跑 —— 先起实例再跑本探针")
    sys.exit(1)


def scan():
    """返回 (宿主窗清单, 标签清单)。标签按 pid 找顶层窗再在子树里找 cab —— 最小化也找得到。"""
    forms, tabs = [], []
    for top in tops():
        if pid_of(top) not in target_pids:
            continue
        sub = sub_tree(top)
        cabs = [h for h in sub if cls(h) in CAB_CLASSES]
        if not cabs:
            continue
        forms.append({"h": top, "vis": bool(u32.IsWindowVisible(top)),
                      "icon": bool(u32.IsIconic(top)), "title": text(top)})
        for h in cabs:
            tabs.append({"h": h, "tid": tid_of(h), "par": u32.GetParent(h),
                         "vis": bool(u32.IsWindowVisible(h)), "wsv": style_vis(h),
                         "z": sibling_z(h), "title": text(h)[:24],
                         "addr": address_of(h)})
    return forms, tabs


forms0, tabs0 = scan()
say("宿主窗 %d 个，标签 %d 个：" % (len(forms0), len(tabs0)))
for f in forms0:
    say("   宿主 0x%-10X 可见=%-5s 最小化=%-5s \"%s\""
        % (f["h"], f["vis"], f["icon"], f["title"][:30]))
for t in tabs0:
    say("   标签 0x%-10X vis=%-5s WS_VISIBLE=%-5s 父=0x%-9X z=%-3d tid=%-6d \"%s\" %s"
        % (t["h"], t["vis"], t["wsv"], t["par"], t["z"], t["tid"], t["title"], t["addr"]))

# ---------------- WinEvent 钩子 ----------------
counts = {}
watch_cabs = set(t["h"] for t in tabs0)
watch_forms = set(f["h"] for f in forms0)


def on_event(hook, ev, hwnd, idobj, idchild, tid, ts):
    hwnd = hwnd or 0
    if not hwnd:
        return
    counts[ev] = counts.get(ev, 0) + 1
    if ev == 0x800B and hwnd not in watch_cabs:      # 位置变化太吵，除非就是我们某个标签
        return
    if hwnd in watch_cabs:
        say("EVENT %-22s hwnd=0x%-8X ★我们的标签 cls=%s pid=%d tid=%d obj=%d/%d"
            % (EVT.get(ev, hex(ev)), hwnd, cls(hwnd), pid_of(hwnd), tid, idobj, idchild))
        return
    if hwnd in watch_forms:
        say("EVENT %-22s hwnd=0x%-8X ☆我们的宿主窗 cls=%s pid=%d tid=%d obj=%d/%d"
            % (EVT.get(ev, hex(ev)), hwnd, cls(hwnd), pid_of(hwnd), tid, idobj, idchild))
        return
    k = cls(hwnd)
    if k in CAB_CLASSES or (ev in (0x0003, 0x0016, 0x0017, 0x8002, 0x8003, 0x800F, 0x8001)
                            and k in ("Shell_TrayWnd", "Progman", "WorkerW")):
        say("EVENT %-22s hwnd=0x%-8X cls=%-18s pid=%-6d tid=%-6d obj=%d/%d"
            % (EVT.get(ev, hex(ev)), hwnd, k[:18], pid_of(hwnd), tid, idobj, idchild))


cb_ref = WinEventProc(on_event)
hook = u32.SetWinEventHook(0x0001, 0x8018, None, ctypes.cast(cb_ref, ctypes.c_void_p),
                           0, 0, WINEVENT_OUTOFCONTEXT)
say("WinEvent 钩子 = 0x%X（0 = 失败）" % (hook or 0))

u32.ShowWindow.argtypes = [wt.HWND, ctypes.c_int]
SW_HIDE, SW_SHOW = 0, 5
hid = []
if hide_inactive:
    for t in tabs0:
        if not t["vis"]:
            u32.ShowWindow(t["h"], SW_HIDE)
            hid.append(t["h"])
            say(">>> 清掉非活动标签 0x%X \"%s\" 自己的 WS_VISIBLE（%s -> %s）"
                % (t["h"], t["title"], t["wsv"], style_vis(t["h"])))
    say(">>> 共清了 %d 个（只清非活动的，活动标签不动）" % len(hid))


def trigger():
    time.sleep(delay)
    r = shell32.ShellExecuteW(None, "open", path, None, None, 1)
    say(">>> 触发 ShellExecuteW('open', %s) 返回 %s" % (path, int(r) if r else r))


threading.Thread(target=trigger, daemon=True).start()

# ---------------- 主循环 ----------------
known_exp = explorer_pids()
last_fg = u32.GetForegroundWindow()
last_tabs = {t["h"]: (t["vis"], t["wsv"], t["z"], t["par"]) for t in tabs0}
last_thr = {}
last_form = {f["h"]: (f["vis"], f["icon"]) for f in forms0}
say("开始观察 %d 秒（先 %.1fs 静默，再触发）" % (secs, delay))

msg = wt.MSG()
t0 = time.time()
while time.time() - t0 < secs:
    while u32.PeekMessageW(ctypes.byref(msg), None, 0, 0, PM_REMOVE):
        u32.TranslateMessage(ctypes.byref(msg))
        u32.DispatchMessageW(ctypes.byref(msg))

    cur = explorer_pids()
    for p in sorted(cur - known_exp):
        say("新 explorer.exe pid=%d" % p)
    for p in sorted(known_exp - cur):
        say("explorer.exe 退出 pid=%d" % p)
    known_exp = cur

    fg = u32.GetForegroundWindow()
    if fg != last_fg:
        say("前台 -> 0x%X cls=%s pid=%d \"%s\""
            % (fg, cls(fg), pid_of(fg), text(fg)[:40]))
        last_fg = fg

    forms, tabs = scan()
    for f in forms:
        sig = (f["vis"], f["icon"])
        if last_form.get(f["h"]) != sig:
            say("宿主窗 0x%X 可见/最小化 %s -> %s" % (f["h"], last_form.get(f["h"]), sig))
            last_form[f["h"]] = sig
    for t in tabs:
        sig = (t["vis"], t["wsv"], t["z"], t["par"])
        if last_tabs.get(t["h"]) != sig:
            say("标签 0x%X \"%s\" (vis,WS_VISIBLE,z,父) %s -> %s  %s"
                % (t["h"], t["title"], last_tabs.get(t["h"]), sig, t["addr"]))
            last_tabs[t["h"]] = sig
        act, foc = thread_state(t["tid"])
        if last_thr.get(t["h"]) != (act, foc):
            say("标签 0x%X \"%s\" 线程态 活动=0x%X 焦点=0x%X" % (t["h"], t["title"], act, foc))
            last_thr[t["h"]] = (act, foc)

    time.sleep(0.006)

say("观察结束")
say("事件计数：" + ", ".join("%s=%d" % (EVT.get(k, hex(k)), v)
                             for k, v in sorted(counts.items())
                             if k != 0x800B))
if hook:
    u32.UnhookWinEvent(hook)
if hid:
    for h in hid:
        u32.ShowWindow(h, SW_SHOW)
    say(">>> 已把 %d 个标签的 WS_VISIBLE 恢复回 1" % len(hid))
say("结束态标签：")
for t in scan()[1]:
    say("   标签 0x%-10X vis=%-5s WS_VISIBLE=%-5s 父=0x%-9X z=%-3d \"%s\" %s"
        % (t["h"], t["vis"], t["wsv"], t["par"], t["z"], t["title"], t["addr"]))
_log.close()
