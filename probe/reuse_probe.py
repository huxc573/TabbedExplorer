"""拆开「shell 复用已开着的文件夹窗口」这条通道 —— 它到底对那扇窗做了什么。

背景（2026-09-24 排查 TabbedExplorer「三方点打开文件夹不切标签」）：
  已证实：目标文件夹已有窗口时，shell 不新建进程、不新建顶层窗、前台不动。
  但这**证伪不了**下面这条：shell 确实匹配到了那扇窗、也确实下了「显示/激活」，
  只是那扇窗本来就可见 —— ShowWindow(SW_SHOW) 落到已可见的窗上是无操作、不发消息，
  于是从外面看就是「什么都没发生」。

  本探针把这条通道单独拆出来：
    ① 先把目标窗**藏起来**（ShowWindow SW_HIDE，跨进程可以）。
       —— 藏了之后，任何「显示/激活」指令都会留下可见痕迹（窗自己冒出来）。
    ② 再从「三方路径」触发（ShellExecuteW 'open' 那个路径）。
    ③ 全程用 out-of-context 的 SetWinEventHook 记录落在目标窗/浏览器窗上的事件
       （跨进程、不需要往别人进程里注入 DLL），并轮询可见位 / 前台 / 顶层浏览器窗数量。

  结果怎么读：
    - 目标窗自己冒出来 ⇒ shell 匹配到了、且下了显示指令 ⇒ 「复用 = 我们被静默地
      put_Visible(TRUE)/激活」成立 ⇒ 修复方向 = 让那一下变得可观测。
    - 目标窗一直藏着、却多出一扇新的浏览器窗 ⇒ shell 的匹配门槛包含「可见」。
    - 两个都不发生 ⇒ shell 匹配到了却**一点动作都没做** ⇒ 从 shell 侧拿不到任何信号，
      必须换思路（让 shell 匹配不到我们）。

用法：
    python reuse_probe.py <target> <路径> [秒数=12] [延迟=3] [--nohide]
      target: auto（自动找顶层浏览器窗里地址==路径的那扇）/ 0x十六进制句柄
    --nohide: 不做第 ① 步（对照实验），直接观察。

副作用：会把目标窗藏起来，脚本结束前恢复显示（SW_SHOW）。只读之外只动这一下。
"""
import ctypes
import ctypes.wintypes as wt
import os
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
u32.GetForegroundWindow.restype = wt.HWND
u32.IsWindowVisible.argtypes = [wt.HWND]
u32.IsWindowVisible.restype = wt.BOOL
u32.ShowWindow.argtypes = [wt.HWND, ctypes.c_int]
u32.EnumWindows.argtypes = [ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM), wt.LPARAM]
u32.EnumChildWindows.argtypes = [wt.HWND,
                                 ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM),
                                 wt.LPARAM]
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

CAB_CLASSES = ("CabinetWClass", "ExploreWClass")
SW_HIDE, SW_SHOW = 0, 5

WINEVENT_OUTOFCONTEXT = 0x0000
PM_REMOVE = 0x0001

EVT = {
    0x0003: "SYSTEM_FOREGROUND", 0x0008: "SYSTEM_CAPTURESTART", 0x0009: "SYSTEM_CAPTUREEND",
    0x0016: "SYSTEM_MINIMIZESTART", 0x0017: "SYSTEM_MINIMIZEEND",
    0x8000: "OBJECT_CREATE", 0x8001: "OBJECT_DESTROY", 0x8002: "OBJECT_SHOW",
    0x8003: "OBJECT_HIDE", 0x8004: "OBJECT_REORDER", 0x8005: "OBJECT_FOCUS",
    0x800A: "OBJECT_STATECHANGE", 0x800B: "OBJECT_LOCATIONCHANGE",
    0x800C: "OBJECT_NAMECHANGE", 0x800E: "OBJECT_VALUECHANGE",
    0x800F: "OBJECT_PARENTCHANGE", 0x8013: "OBJECT_INVOKED",
}

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


def address_of(root):
    """地址栏那个 ToolbarWindow32 的窗口文本就是当前地址。"""
    for h in sub_tree(root):
        if cls(h) == "ToolbarWindow32":
            t = text(h)
            if ":\\" in t:
                return t
    return ""


def cab_windows():
    """所有顶层浏览器窗 (hwnd, pid, vis, 地址)。"""
    out = []
    for h in tops():
        if cls(h) in CAB_CLASSES:
            out.append((h, pid_of(h), bool(u32.IsWindowVisible(h)), address_of(h)))
    return out


def norm(p):
    return p.replace("/", "\\").rstrip("\\").lower()


# ---------------- 解析参数 ----------------
args = [a for a in sys.argv[1:] if not a.startswith("--")]
flags = {a for a in sys.argv[1:] if a.startswith("--")}
if len(args) < 2:
    print(__doc__)
    sys.exit(1)

target_arg, path = args[0], args[1]
secs = int(args[2]) if len(args) > 2 else 12
delay = float(args[3]) if len(args) > 3 else 3.0
do_hide = "--nohide" not in flags

# 计划任务里 CWD 是 System32，写不动；用脚本自身目录的绝对路径
_log = open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "reuse_probe.log"),
            "a", encoding="utf-8")
say("=" * 78)
say("目标参数=%s 路径=%s 观察=%ss 触发延迟=%.1fs 隐藏=%s"
    % (target_arg, path, secs, delay, do_hide))

# ---------------- 找目标窗 ----------------
target = 0
if target_arg.lower() == "auto":
    for h, p, v, a in cab_windows():
        if a and norm(a) == norm(path):
            target = h
            say("auto 命中 0x%X pid=%d 可见=%s 地址=%s" % (h, p, v, a))
            break
    if not target:
        say("auto 没找到地址==%s 的顶层浏览器窗；当前有：" % path)
        for h, p, v, a in cab_windows():
            say("   0x%-10X pid=%-7d vis=%-5s %s" % (h, p, v, a))
else:
    target = int(target_arg, 16)

if not target or not u32.IsWindow(target):
    say("目标窗无效，退出")
    sys.exit(2)

say("目标窗 0x%X 类=%s pid=%d 标题=%s 可见=%s"
    % (target, cls(target), pid_of(target), text(target)[:30],
       bool(u32.IsWindowVisible(target))))
tgt_pid = pid_of(target)

# ---------------- WinEvent 钩子（跨进程、免注入） ----------------
counts = {}


def on_event(hook, ev, hwnd, idobj, idchild, tid, ts):
    hwnd = hwnd or 0
    if hwnd == 0:
        return
    counts[ev] = counts.get(ev, 0) + 1
    interesting = False
    why = ""
    if hwnd == target:
        interesting, why = True, "★目标窗"
    elif ev in (0x0003, 0x0016, 0x0017, 0x8002, 0x8003, 0x800F, 0x8001):
        k = cls(hwnd)
        if hwnd in (u32.GetForegroundWindow(),) or k in CAB_CLASSES or k in (
                "Shell_TrayWnd", "Progman", "WorkerW"):
            interesting, why = True, "顶层"
    elif ev == 0x8004 and cls(hwnd) in CAB_CLASSES:
        interesting, why = True, "浏览器窗重排"
    if ev == 0x800B and hwnd != target:        # 位置变化太吵，除非就是目标窗
        interesting = False
    if interesting:
        say("EVENT %-22s hwnd=0x%-8X cls=%-18s pid=%-6d tid=%-6d obj=%d/%d  %s"
            % (EVT.get(ev, hex(ev)), hwnd, cls(hwnd)[:18], pid_of(hwnd), tid,
               idobj, idchild, why))


cb_ref = WinEventProc(on_event)
hook = u32.SetWinEventHook(0x0001, 0x8018, None, ctypes.cast(cb_ref, ctypes.c_void_p),
                           0, 0, WINEVENT_OUTOFCONTEXT)
say("WinEvent 钩子 = 0x%X（0 = 失败）" % (hook or 0))

# ---------------- 触发线程 ----------------
trigger_t = [None]


def trigger():
    time.sleep(delay)
    trigger_t[0] = time.time() - _t0
    r = shell32.ShellExecuteW(None, "open", path, None, None, 1)
    say(">>> 触发 ShellExecuteW('open', %s) 返回 %s" % (path, int(r) if r else r))


threading.Thread(target=trigger, daemon=True).start()

# ---------------- 主循环 ----------------
hidden = False
base_cabs = cab_windows()
say("起始顶层浏览器窗 %d 个：" % len(base_cabs))
for h, p, v, a in base_cabs:
    say("   0x%-10X pid=%-7d vis=%-5s %s" % (h, p, v, a))

last_fg = u32.GetForegroundWindow()
last_vis = bool(u32.IsWindowVisible(target))
last_cnt = len(base_cabs)
say("开始观察 %d 秒" % secs)
say("先不做任何事，%.1fs 后隐藏目标窗，再过 %.1fs 触发" % (1.0, delay - 1.0))

msg = wt.MSG()
t0 = time.time()
while time.time() - t0 < secs:
    # 抽干消息队列 —— out-of-context 钩子靠这个线程泵消息才会回调
    while u32.PeekMessageW(ctypes.byref(msg), None, 0, 0, PM_REMOVE):
        u32.TranslateMessage(ctypes.byref(msg))
        u32.DispatchMessageW(ctypes.byref(msg))

    now = time.time() - t0

    if do_hide and not hidden and now >= 1.0 and not trigger_t[0]:
        u32.ShowWindow(target, SW_HIDE)
        hidden = True
        say(">>> 已隐藏目标窗 0x%X，现在它的可见位=%s"
            % (target, bool(u32.IsWindowVisible(target))))

    vis = bool(u32.IsWindowVisible(target))
    if vis != last_vis:
        say("目标窗可见位 %s -> %s" % (last_vis, vis))
        last_vis = vis

    fg = u32.GetForegroundWindow()
    if fg != last_fg:
        say("前台 -> 0x%X cls=%s pid=%d \"%s\""
            % (fg, cls(fg), pid_of(fg), text(fg)[:40]))
        last_fg = fg

    cabs = cab_windows()
    if len(cabs) != last_cnt:
        say("顶层浏览器窗数量 %d -> %d：" % (last_cnt, len(cabs)))
        for h, p, v, a in cabs:
            say("   0x%-10X pid=%-7d vis=%-5s %s" % (h, p, v, a))
        last_cnt = len(cabs)

    time.sleep(0.008)

say("观察结束")
if hidden and u32.IsWindow(target):
    u32.ShowWindow(target, SW_SHOW)
    say(">>> 已恢复显示目标窗 0x%X（可见位=%s）"
        % (target, bool(u32.IsWindowVisible(target))))
say("事件计数：" + ", ".join("%s=%d" % (EVT.get(k, hex(k)), v)
                             for k, v in sorted(counts.items())))
if hook:
    u32.UnhookWinEvent(hook)
say("目标窗此刻：存在=%s 可见=%s 标题=%s"
    % (bool(u32.IsWindow(target)), bool(u32.IsWindowVisible(target)),
       text(target)[:30]))
say("结束态顶层浏览器窗：")
for h, p, v, a in cab_windows():
    say("   0x%-10X pid=%-7d vis=%-5s %s" % (h, p, v, a))
_log.close()
