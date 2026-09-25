# -*- coding: utf-8 -*-
"""探针：量「备用窗口导航复用」那一下到底多久，以及慢在谁身上。

为什么要量（2026-09-25 程序实测日志）：点书签走导航复用，从点击到内容到位约 **850ms**；
而上一轮同一个探针量到的纯导航只要 **82ms** —— 差一个数量级，说明程序那一下被别的东西拖了。

三个嫌疑：
  ① **抢机器**：拿备用窗口的同时程序又去预热下一个备用窗口（起一个新 explorer 进程，
     实测它自己建窗就要 ~500ms）。导航和「起进程」撞在一起。
  ② **窗的状态**：程序里那扇是「嵌在从不显示的宿主面板里、从没露过脸」的窗；
     探针上一轮量的是顶层隐藏窗。explorer 对这两种的导航开销可能不一样。
  ③ **轮询反噬**：40ms 一次跨进程读 LocationURL 把 explorer 的 UI 线程堵住。

做法：完全照程序那套 —— 藏窗、SetParent 到一个**从不 SHOW** 的宿主、改样式位，
然后量「Navigate2 发出 → LocationURL 变过来 / 窗内地址栏变过来」。两种场景对照：
  A 干净：导航之后旁边什么都不干
  B 抢机器：导航一发出就起一个新 explorer 去开「此电脑」（= 程序里预热下一个备用窗口）
每轮都用**新窗口**（程序每次点击用的也是刚收编不久的新窗口），所以每轮都要现起一个 explorer。

⚠ 全程不动鼠标、不抢前台、不调 SetForegroundWindow；宿主窗从不 SHOW，窗口一诞生就藏。
⚠ 跑之前必须 `TabbedExplorer.exe --quit`（不然它会把这些测试窗收编成标签，量到的是被干扰的）。
⚠ 本机 Bash 直起的进程挂安全垫 ⇒ 走计划任务（`tools/nav_bench.local.bat`）。

用法：python nav_reuse_bench.py [目标目录] [每种场景跑几轮]
"""
import ctypes
import os
import sys
import time
from ctypes import c_long, c_void_p

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import navigate_reuse_probe as P          # 复用里头那套手搓 IDispatch / 窗口工具

u, k = P.u, P.k
wt = ctypes.wintypes

SRC = "shell:MyComputerFolder"            # 备用窗口预热的位置（跟程序里一样）
POLL = 0.025                              # 轮询间隔，跟程序里那条 40ms 轮询一个量级
NAV_WAIT = 6.0

st = {"baseline": set(), "base_wins": set(), "hides": []}
hook_cb = None


def on_create(hook, ev, hwnd, idobj, idchild, thr, t):
    """窗口诞生的那一刻就藏（跟 DesktopHub 防闪同一招）：只碰「pid 不在起跑基线里」的新窗。"""
    if idobj != 0 or idchild != 0 or not hwnd:
        return
    if P.cls(hwnd) not in P.CAB:
        return
    if P.pid_of(hwnd) in st["baseline"]:
        return
    u.ShowWindow(hwnd, P.SW_HIDE)
    st["hides"].append((hwnd, P.pid_of(hwnd)))


def install_hook(say):
    global hook_cb
    HookProc = ctypes.WINFUNCTYPE(None, wt.HANDLE, wt.DWORD, wt.HWND, c_long, c_long,
                                  wt.DWORD, wt.DWORD)
    hook_cb = HookProc(on_create)
    h = u.SetWinEventHook(P.EVENT_OBJECT_CREATE, P.EVENT_OBJECT_CREATE, None, hook_cb, 0, 0,
                          P.WINEVENT_OUTOFCONTEXT | P.WINEVENT_SKIPOWNPROCESS)
    say("CREATE 钩子 = %s" % (("0x%X" % (h or 0)) if h else "装不上(!)"))
    return h


u.FindWindowExW.argtypes = [wt.HWND, wt.HWND, wt.LPCWSTR, wt.LPCWSTR]
u.FindWindowExW.restype = wt.HWND
u.AttachThreadInput.argtypes = [wt.DWORD, wt.DWORD, wt.BOOL]
u.AttachThreadInput.restype = wt.BOOL
u.SetFocus.argtypes = [wt.HWND]
u.SetFocus.restype = wt.HWND
k.GetCurrentThreadId.restype = wt.DWORD


def mimic_focus(cab):
    """照程序里 `Activate` → `ExplorerHost.Focus()` 那一下：AttachThreadInput + SetFocus + 立刻 detach。
    怀疑就是它在导航刚发出时把 explorer / 我们自己的 UI 线程绊住（所以单独量一次）。"""
    our = k.GetCurrentThreadId()
    cab_tid = u.GetWindowThreadProcessId(cab, None)
    if not cab_tid:
        return 0
    tgt = u.FindWindowExW(cab, None, "SHELLDLL_DefView", None)
    if not tgt:
        return 0
    t0 = time.time()
    att = u.AttachThreadInput(our, cab_tid, True)
    u.SetFocus(tgt)
    if att:
        u.AttachThreadInput(our, cab_tid, False)
    return (time.time() - t0) * 1000


def kill_explorers(say, baseline):
    for rnd in (1, 2, 3):
        diff = sorted(P.explorer_pids() - baseline)
        if not diff:
            return []
        for p in diff:
            hp = k.OpenProcess(P.PROCESS_TERMINATE, False, p)
            if hp:
                k.TerminateProcess(hp, 0)
                k.CloseHandle(hp)
        say("     清理第 %d 轮：结束 %s" % (rnd, diff))
        time.sleep(0.8)
    return sorted(P.explorer_pids() - baseline)


def trial(say, dst, mode, host, settle_ms=400):
    """起一扇新窗 → 藏 → 等加载好 → 抓清单项 → 嵌进从不 SHOW 的宿主 → 导航 dst 并计时。

    mode="clean"：导航后只轮询（URL 每 25ms、地址栏也读）。
    mode="focus"：补上程序那一下 `Focus()`（AttachThreadInput+SetFocus），再用 40ms 轮询 URL。
    """
    st["baseline"] = P.explorer_pids()
    st["base_wins"] = set(P.cabs())

    pi = P.create_process('explorer.exe /n,/separate,"%s"' % SRC)
    if pi is None:
        return None

    t0 = time.time()
    cab, t_appear, t_view = None, None, None
    while time.time() - t0 < P.BUDGET_S:
        P.pump()
        if cab is None:
            for h in P.cabs():
                if h in st["base_wins"] or P.pid_of(h) in st["baseline"]:
                    continue
                cab = h
                t_appear = (time.time() - t0) * 1000
                u.ShowWindow(h, P.SW_HIDE)
                break
        elif u.IsWindow(cab) and t_view is None and P.has_defview(cab):
            t_view = (time.time() - t0) * 1000
            break
        time.sleep(POLL)
    if cab is None:
        say("     没等到新窗口")
        return None

    # 抓清单项（程序里是趁收编之前抓的；这里同样趁它还是顶层窗）
    disp, t_read = None, None
    tr = time.time()
    while time.time() - tr < P.SCAN_WAIT_S:
        P.pump()
        d0, how0, _ = P.scan_for(cab)
        if d0 is not None:
            disp, t_read = d0, (time.time() - tr) * 1000
            break
        time.sleep(0.2)
    if disp is None:
        say("     清单里一直读不到这扇窗")
        return None

    # 嵌入：SetParent 到从不 SHOW 的宿主 + 改样式位（跟程序里那三下一样）
    u.SetParent(cab, host)
    s = u.GetWindowLongW(cab, P.GWL_STYLE) & 0xFFFFFFFF
    u.SetWindowLongW(cab, P.GWL_STYLE, P.to_signed32((s | P.WS_CHILD) & ~P.WS_POPUP_MASK))
    e = u.GetWindowLongW(cab, P.GWL_EXSTYLE) & 0xFFFFFFFF
    u.SetWindowLongW(cab, P.GWL_EXSTYLE, P.to_signed32((e | P.WS_EX_TOOLWINDOW) & ~P.WS_EX_APPWINDOW))

    time.sleep(settle_ms / 1000.0)          # 模拟「备用窗口就绪之后停了一会儿才被点」

    want = dst.lower().rstrip("\\")
    m, hr, scode, call_ms = P.navigate(disp, dst)

    t_focus = None
    if mode == "focus":
        # ★ 照程序那一下：导航刚发出就 `Activate` → `Focus()`（AttachThreadInput + SetFocus）
        t_focus = mimic_focus(cab)

    period = 0.040 if mode == "focus" else POLL
    t1 = time.time()
    t_url = t_addr = None
    calls = []
    while time.time() - t1 < NAV_WAIT:
        P.pump()
        if t_url is None:
            tc = time.time()
            cu, _ = P.prop(disp, "LocationURL")
            calls.append((time.time() - tc) * 1000)
            p2 = P.url_to_path(cu)
            if p2 and p2.lower().startswith(want):
                t_url = (time.time() - t1) * 1000
        if mode == "clean":
            if t_addr is None:
                rp = P.read_path(cab)
                if rp and rp.lower().startswith(want):
                    t_addr = (time.time() - t1) * 1000
            if t_url is not None and t_addr is not None:
                break
        else:
            if t_url is not None:
                break
        time.sleep(period)

    pid = P.pid_of(cab)
    say("     出现+%s 文件列表+%s 可读+%s | Navigate2 调用%dms%s | Url变 %s / 地址栏变 %s"
        % (("%dms" % t_appear) if t_appear is not None else "-",
           ("%dms" % t_view) if t_view is not None else "-",
           ("%dms" % t_read) if t_read is not None else "-",
           call_ms,
           (" | Focus %dms" % t_focus) if t_focus is not None else "",
           ("%dms" % t_url) if t_url is not None else "超时",
           ("%dms" % t_addr) if t_addr is not None else "（本场景不读）"))
    say("     读一次 LocationURL 的耗时（前 8 次）：[%s]"
        % ", ".join("%.0f" % c for c in calls[:8]))

    # 收尾这一轮：关掉窗 + 杀掉本次新起的 explorer
    if u.IsWindow(cab):
        u.PostMessageW(cab, P.WM_CLOSE, 0, 0)
        P.pump()
        time.sleep(0.5)
    if u.IsWindow(cab):
        u.ShowWindow(cab, P.SW_HIDE)
    kill_explorers(say, st["baseline"])

    return {"t_appear": t_appear, "t_view": t_view, "t_read": t_read,
            "hr": hr, "call": call_ms, "url": t_url, "addr": t_addr,
            "focus": t_focus, "calls": calls, "pid": pid}


def main(dst, rounds):
    log = []

    def say(s):
        print(s)
        log.append(s)

    P.ole32.CoInitializeEx(None, P.COINIT_APARTMENTTHREADED)
    running = P.procs_named("TabbedExplorer.exe")
    if running:
        say("!! 有 TabbedExplorer 在跑（pid %s）—— 先 --quit 再来，不然量到的是被它干扰过的。" % running)
        return 3
    if not os.path.isdir(dst):
        say("!! 不是目录：%s" % dst)
        return 2

    base = P.explorer_pids()
    say("起跑：现存 explorer = %s；桌面 shell pid=%d" % (sorted(base), P.pid_of(u.FindWindowW("Shell_TrayWnd", None) or 0)))
    hook = install_hook(say)
    host = u.CreateWindowExW(0, "Static", "TBENavBenchHost", P.WS_POPUP_MASK, 0, 0, 800, 600,
                            None, None, k.GetModuleHandleW(None), None)
    say("宿主窗 host=0x%X（从不 SHOW）" % (host or 0))

    res = {"A": [], "C": []}
    for i in range(rounds):
        for tag, mode in (("A", "clean"), ("C", "focus")):
            say("-- 第 %d 轮 / 场景 %s（%s）--" % (i + 1, tag,
                "干净：只轮询" if mode == "clean" else "照程序：先 Focus 再 40ms 轮询"))
            r = trial(say, dst, mode, host)
            if r:
                res[tag].append(r)

    def stats(k2):
        v = [r[k2] for t in res for r in res[t] if r[k2] is not None]
        return v

    say("")
    say("==== 汇总（目标 %s，各场景 %d 轮）====" % (dst, rounds))
    for tag in ("A", "C"):
        for r in res[tag]:
            cs = r["calls"]
            say("  %s: 调用 %.0fms%s  Url变 %s  地址栏变 %s  单次读URL 均 %.0f/最大 %.0fms"
                % (tag, r["call"],
                   ("  Focus %.0fms" % r["focus"]) if r["focus"] is not None else "",
                   ("%.0fms" % r["url"]) if r["url"] is not None else "超时",
                   ("%.0fms" % r["addr"]) if r["addr"] is not None else "（未读）",
                   (sum(cs) / len(cs)) if cs else 0, max(cs) if cs else 0))
    for k2, label in (("url", "LocationURL 变过来"), ("addr", "窗内地址栏变过来")):
        for tag, nm in (("A", "A（干净）"), ("C", "C（照程序）")):
            v = [r[k2] for r in res[tag] if r[k2] is not None]
            if v:
                say("  %s %s：min %.0f / 均 %.0f / max %.0f ms"
                    % (nm, label, min(v), sum(v) / len(v), max(v)))

    # ---- 收尾 ----
    if hook:
        try:
            u.UnhookWinEvent(hook)
        except Exception:
            pass
    if u.IsWindow(host):
        u.DestroyWindow(host)
    time.sleep(0.4)
    left = kill_explorers(say, base)
    stray = [h for h in P.cabs() if P.pid_of(h) in set(left)]
    say("收尾：残留 explorer %s；残留 cabinet 窗 %s" % (left, [hex(h) for h in stray]))
    out = os.path.join(HERE, "nav_reuse_bench.out.txt")
    with open(out, "w", encoding="utf-8") as f:
        f.write("\n".join(log) + "\n")
    say("结果已写 %s" % out)
    return 0


if __name__ == "__main__":
    d = sys.argv[1] if len(sys.argv) > 1 else r"D:\Shortcut"
    n = int(sys.argv[2]) if len(sys.argv) > 2 else 3
    sys.exit(main(d, n))
