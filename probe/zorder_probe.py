"""决定性实验：shell 复用我们已收编的标签窗时，会不会把那扇 cab **提到同级 z 序最上**。

为什么之前测不出来：
  `probe/reuse_app_watch.py` 一直在盯每个 cab 的 z 位次，但**每个宿主窗里只有 cab 这一个
  子窗** —— 唯一子窗的 z 恒为 0，「提到最上」对它来说是无操作，于是一次 `BringWindowToTop`
  也看不出任何变化。那次「z 没变」是空转，什么都没证明。

  而 `BringWindowToTop(child)` 的语义恰好和我们已观测到的现象完全吻合：
  **换同级 z 序 + 激活父顶层窗** —— 实测里正是「我们的宿主窗被激活、cab 本身毫无痕迹」
  （cab 的 WS_VISIBLE / 位置 / 线程态全都没动）。

  所以本探针给**每个标签的宿主窗**塞一个隐藏的哑子窗（0×0 STATIC），把 cab 顶到 z=1；
  这样一旦 shell 真的把某个 cab 提到最上，它的 z 就会 0←1，而其它标签纹丝不动
  —— 直接带出「用户点的是哪个文件夹」。

  结果怎么读：
    · 只有一个 cab 的 z 从 1 变 0 ⇒ **拿到了身份**：谁跳到最上就是谁 ⇒ 可据此切标签（真修法）。
    · 所有 cab 的 z 都不动 ⇒ shell 连 z 序都不碰，那就真的是「只剩激活宿主窗」这一个信号。

用法：python zorder_probe.py <路径> [秒数=16] [延迟=5]
副作用：给每个宿主加一个 0×0 隐藏子窗，结束前 DestroyWindow 清掉。只读之外只有这一处。
⚠ ShellExecuteW 在 Bash 里会被沙箱吃掉，必须走计划任务跑。
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
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    pass

u32.GetClassNameW.argtypes = [wt.HWND, wt.LPWSTR, ctypes.c_int]
u32.GetWindowTextW.argtypes = [wt.HWND, wt.LPWSTR, ctypes.c_int]
u32.GetWindow.argtypes = [wt.HWND, ctypes.c_uint]
u32.GetWindow.restype = wt.HWND
u32.GetParent.argtypes = [wt.HWND]
u32.GetParent.restype = wt.HWND
u32.IsWindow.argtypes = [wt.HWND]
u32.IsWindow.restype = wt.BOOL
u32.GetForegroundWindow.restype = wt.HWND
u32.EnumWindows.argtypes = [ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM), wt.LPARAM]
u32.EnumChildWindows.argtypes = [wt.HWND,
                                 ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM), wt.LPARAM]
u32.CreateWindowExW.argtypes = [wt.DWORD, wt.LPCWSTR, wt.LPCWSTR, wt.DWORD,
                                ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int,
                                wt.HWND, wt.HMENU, wt.HINSTANCE, ctypes.c_void_p]
u32.CreateWindowExW.restype = wt.HWND
u32.DestroyWindow.argtypes = [wt.HWND]
u32.SetWindowPos.argtypes = [wt.HWND, wt.HWND, ctypes.c_int, ctypes.c_int, ctypes.c_int,
                             ctypes.c_int, ctypes.c_uint]
u32.SetWindowPos.restype = wt.BOOL
u32.SetWinEventHook.argtypes = [ctypes.c_uint, ctypes.c_uint, wt.HMODULE,
                                ctypes.c_void_p, ctypes.c_ulong, ctypes.c_ulong,
                                ctypes.c_uint]
u32.SetWinEventHook.restype = ctypes.c_void_p
u32.PeekMessageW.argtypes = [ctypes.c_void_p, wt.HWND, ctypes.c_uint, ctypes.c_uint,
                             ctypes.c_uint]
shell32.ShellExecuteW.argtypes = [wt.HWND, wt.LPCWSTR, wt.LPCWSTR, wt.LPCWSTR,
                                  wt.LPCWSTR, ctypes.c_int]
shell32.ShellExecuteW.restype = wt.HINSTANCE

WS_CHILD = 0x40000000
GW_HWNDPREV = 3
CAB_CLASSES = ("CabinetWClass", "ExploreWClass")
PM_REMOVE = 0x0001
WINEVENT_OUTOFCONTEXT = 0x0000

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


def kids_of(h):
    out = []

    def cb(k, l):
        k = k or 0
        if u32.GetParent(k) == h:
            out.append(k)
        return True

    u32.EnumChildWindows(h, EnumProc(cb), 0)
    return out


def sub_tree(root):
    out = [root]

    def cb(h, l):
        out.append(h or 0)
        return True

    u32.EnumChildWindows(root, EnumProc(cb), 0)
    return out


def z_of(h):
    """h 在它自己那一层里的 z 位次（0 = 最上）。"""
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


# ---------------- 参数 ----------------
if len(sys.argv) < 2:
    print(__doc__)
    sys.exit(1)
path = sys.argv[1]
secs = int(sys.argv[2]) if len(sys.argv) > 2 else 16
delay = float(sys.argv[3]) if len(sys.argv) > 3 else 5.0
_log = open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "zorder_probe.log"),
            "a", encoding="utf-8")

target_pids = set()
rows = subprocess.run(["tasklist", "/FI", "IMAGENAME eq TabbedExplorer.exe",
                       "/FO", "CSV", "/NH"], capture_output=True, text=True,
                      errors="replace").stdout
for row in rows.splitlines():
    p = [x.strip('"') for x in row.split('","')]
    if len(p) > 1 and p[1].isdigit():
        target_pids.add(int(p[1]))

say("=" * 78)
say("路径=%s 观察=%ss 延迟=%.1fs TabbedExplorer pid=%s" % (path, secs, delay, sorted(target_pids)))
if not target_pids:
    say("TabbedExplorer 没在跑")
    sys.exit(1)

# ---------------- 找 cab 与宿主 ----------------
cabs = []          # [{h, host, pid, title, addr}]
for top in tops():
    if pid_of(top) not in target_pids:
        continue
    for h in sub_tree(top):
        if cls(h) in CAB_CLASSES:
            cabs.append({"h": h, "host": u32.GetParent(h), "pid": pid_of(h),
                         "title": text(h)[:22], "addr": address_of(h)})
for c in cabs:
    say("cab 0x%-8X 宿主=0x%-8X pid=%-6d z=%d \"%s\" %s"
        % (c["h"], c["host"], c["pid"], z_of(c["h"]), c["title"], c["addr"]))
if not cabs:
    say("没找到 cab")
    sys.exit(2)

# 跨进程**不能**把外来子窗抬到 cab 上面（SetWindowPos(dummy, HWND_TOP) 实测无效），
# 但**可以**把 cab 自己压到同级最底（实测有效）—— 效果一样：让 cab 的 z 从 0 变 1，
# 之后 shell 一旦对它调 BringWindowToTop / SetWindowPos(HWND_TOP)，z 就会 1 -> 0，看得见。
SWP_NOSIZE, SWP_NOMOVE, SWP_NOACTIVATE = 0x1, 0x2, 0x10
HWND_BOTTOM = wt.HWND(1)
dummies = []
for c in cabs:
    d = u32.CreateWindowExW(0, "STATIC", "", WS_CHILD, 0, 0, 0, 0,
                            c["host"], None, None, None)
    dummies.append(d)
    ok = u32.SetWindowPos(c["h"], HWND_BOTTOM, 0, 0, 0, 0,
                          SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE)
    say("宿主 0x%-8X 加哑子窗 0x%-8X；把 cab 0x%-8X 压到最底 -> %s，cab z = %d"
        % (c["host"], d or 0, c["h"], bool(ok), z_of(c["h"])))

# ---------------- WinEvent 钩子：只报我们这批 cab 上的重排 ----------------
watch = set(c["h"] for c in cabs)


def on_event(hook, ev, hwnd, idobj, idchild, tid, ts):
    hwnd = hwnd or 0
    if hwnd in watch and ev in (0x8004, 0x800F, 0x0003, 0x8002, 0x8003, 0x8005):
        evn = {0x8004: "OBJECT_REORDER", 0x800F: "OBJECT_PARENTCHANGE",
               0x0003: "SYSTEM_FOREGROUND", 0x8002: "OBJECT_SHOW",
               0x8003: "OBJECT_HIDE", 0x8005: "OBJECT_FOCUS"}.get(ev, hex(ev))
        say("EVENT %-20s cab=0x%-8X  z 现在=%d" % (evn, hwnd, z_of(hwnd)))


cb_ref = WinEventProc(on_event)
hook = u32.SetWinEventHook(0x0001, 0x8018, None, ctypes.cast(cb_ref, ctypes.c_void_p),
                           0, 0, WINEVENT_OUTOFCONTEXT)
say("WinEvent 钩子 = 0x%X" % (hook or 0))


def trigger():
    time.sleep(delay)
    r = shell32.ShellExecuteW(None, "open", path, None, None, 1)
    say(">>> 触发 ShellExecuteW('open', %s) 返回 %s" % (path, int(r) if r else r))


threading.Thread(target=trigger, daemon=True).start()

msg = wt.MSG()
last = dict((c["h"], z_of(c["h"])) for c in cabs)
last_fg = u32.GetForegroundWindow()
say("开始观察 %d 秒（%.1fs 后触发）；基线 z：%s"
    % (secs, delay, ", ".join("0x%X=%d" % (h, z) for h, z in sorted(last.items()))))

t0 = time.time()
while time.time() - t0 < secs:
    try:
        while u32.PeekMessageW(ctypes.byref(msg), None, 0, 0, PM_REMOVE):
            u32.TranslateMessage(ctypes.byref(msg))
            u32.DispatchMessageW(ctypes.byref(msg))
        for c in cabs:
            z = z_of(c["h"])
            if z != last[c["h"]]:
                say("★ z 变了！cab 0x%X \"%s\"（%s）z: %d -> %d"
                    % (c["h"], c["title"], c["addr"], last[c["h"]], z))
                last[c["h"]] = z
        # ⚠ GetForegroundWindow 在「前台正在切换」时会返回 NULL；声明成 HWND(c_void_p)
        #    时 ctypes 会把它翻成 None —— 直接 %X 会 TypeError（上一版就是这么崩的）。
        fg = u32.GetForegroundWindow() or 0
        if fg != last_fg:
            if fg:
                say("前台 -> 0x%X cls=%s pid=%d \"%s\""
                    % (fg, cls(fg), pid_of(fg), text(fg)[:36]))
            else:
                say("前台 -> (NULL) —— 前台窗口正在切换")
            last_fg = fg
    except Exception:
        import traceback
        say("!!! 本轮异常（继续）：" + traceback.format_exc().replace("\n", " | "))
    time.sleep(0.004)

say("观察结束")
for c in cabs:
    say("cab 0x%-8X \"%s\" 终态 z=%d" % (c["h"], c["title"], z_of(c["h"])))

if hook:
    u32.UnhookWinEvent(hook)
for d in dummies:
    if d and u32.IsWindow(d):
        u32.DestroyWindow(d)
say("哑子窗已清掉；剩余子窗数：%s"
    % ", ".join("0x%X=%d" % (c["host"], len(kids_of(c["host"]))) for c in cabs))
_log.close()
