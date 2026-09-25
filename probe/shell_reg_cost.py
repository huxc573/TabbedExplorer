# -*- coding: utf-8 -*-
"""量一笔「直接照 ShellBrowserReg.ExcludeOurs 的做法走一遍 ShellWindows 清单」要多久。

为什么要量：程序里这笔活儿挂在每个标签的 500ms 心跳上（内部 2 秒节流），
也就是说**只要程序开着，每 2 秒就会在它那唯一一个 UI 线程上做一遍**。
收编那一刻那几遍已经量到 198~1365ms 了（`Embed: 收编耗时 … shell登记`），
这里量的是「平时这一遍」到底多贵。

复用 probe/shellwin_drop.py 里那套手搓 IDispatch（把它的 main() 摘掉再 exec）。
只读、不起窗口、不写任何东西（写 RegisterAsBrowser 的次数用 -w 控制，默认 0）。
    python shell_reg_cost.py [遍数，默认 5]
"""
import io
import sys
import time

SRC = r"D:\Dev\Workspaces\WorkBuddy\TabbedExplorer\probe\shellwin_drop.py"

src = io.open(SRC, encoding="utf-8").read()
assert "\nmain()\n" in src, "shellwin_drop.py 的入口变了，先去看一眼"
src = src.replace("\nmain()\n", "\n")
ns = {}
exec(compile(src, SRC, "exec"), ns)

# 摘出需要的家伙什
guid = ns["guid"]
invoke = ns["invoke"]
get_dispid = ns["get_dispid"]
vint = ns["vint"]
vref = ns["vref"]
VARIANT = ns["VARIANT"]
byref = ns["byref"]
c_void_p = ns["c_void_p"]
ole32 = ns["ole32"]
CLSCTX_LOCAL_SERVER = ns["CLSCTX_LOCAL_SERVER"]
CLSCTX_INPROC_SERVER = ns["CLSCTX_INPROC_SERVER"]
DISPATCH_METHOD = ns["DISPATCH_METHOD"]
DISPATCH_PROPERTYGET = ns["DISPATCH_PROPERTYGET"]
VT_I4 = ns["VT_I4"]
VT_BOOL = ns["VT_BOOL"]
VT_VARIANT = ns["VT_VARIANT"]
VT_BYREF = ns["VT_BYREF"]


def root():
    clsid = guid("{9BA05972-F6A8-11CF-A442-00A0C90A8F39}")
    iid_disp = guid("{00020400-0000-0000-C000-000000000046}")
    p = c_void_p()
    hr = ole32.CoCreateInstance(byref(clsid), None,
                                CLSCTX_LOCAL_SERVER | CLSCTX_INPROC_SERVER,
                                byref(iid_disp), byref(p))
    return p if hr == 0 and p.value else None


def mine_set():
    """我们收编进来的那批窗口（被 SetParent 成了子窗口，靠 EnumChildWindows 才看得到）。"""
    try:
        return set(ns["embedded_cabinet_windows"]())
    except Exception:
        return set()


def one_pass(do_write, ours):
    """照 ExcludeOurs 的做法：Count -> 逐项 Item -> 读 HWND（-> 对我们的窗写 RegisterAsBrowser）。"""
    p = root()
    if p is None:
        return None
    cd, _ = get_dispid(p, "Count")
    hr, cv, _ = invoke(p, cd, DISPATCH_PROPERTYGET)
    total = cv.u.lVal
    idm, _ = get_dispid(p, "Item")

    t0 = time.perf_counter()
    mine = done = 0
    for i in range(total):
        hr, item, _ = invoke(p, idm, DISPATCH_METHOD, args=[vref(vint(i))])
        q = item.u.pdispVal or item.u.punkVal
        if hr != 0 or not q:
            continue
        hd, _ = get_dispid(q, "HWND")
        if hd is None:
            continue
        hv = VARIANT()
        invoke(q, hd, DISPATCH_PROPERTYGET)
        # ⚠ HWND 这一项回来是 VT_I8（不是 VT_I4）—— 别按 VT_I4 判，会全读成 0、
        #   于是「我们自己那些窗一个都认不出来」。句柄都在 32 位内，取联合体的 lVal 就行。
        hwnd = hv.u.lVal if hv.vt in (VT_I4, 20) else 0
        if not do_write or hwnd not in ours:
            continue
        mine += 1
        wd, _ = get_dispid(q, "RegisterAsBrowser")
        if wd is not None:
            hr2, _, _ = invoke(q, wd, 4, args=[vbool(True)],
                               named=[ns["DISPID_PROPERTYPUT"]], want_result=False)
            if hr2 == 0:
                done += 1
    dt = (time.perf_counter() - t0) * 1000
    return total, dt, mine, done


def main():
    n = int(sys.argv[1]) if len(sys.argv) > 1 else 5
    p = root()
    cd, _ = get_dispid(p, "Count")
    hr, cv, _ = invoke(p, cd, DISPATCH_PROPERTYGET)
    ours = mine_set()
    print("ShellWindows 清单现在 %d 项；识别到我们自己收编的窗口 %d 个" % (cv.u.lVal, len(ours)))
    print("（识别不到就是程序没在跑 / 没有标签，那 ② 只会空转，① 照样有效）\n")

    print("① 只读：Count + 逐项 Item + 读 HWND（心跳每一遍都要做的那些）")
    for k in range(n):
        t, dt, mine, done = one_pass(False, ours)
        print("   第%d遍 清单 %d 项  %.0f ms" % (k + 1, t, dt))

    print("\n② 带上写：对我们自己的窗口写 RegisterAsBrowser")
    for k in range(min(n, 3)):
        t, dt, mine, done = one_pass(True, ours)
        print("   第%d遍 清单 %d 项  我们自己 %d 项（写了 %d）  %.0f ms" % (k + 1, t, mine, done, dt))


main()
