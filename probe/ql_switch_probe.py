# -*- coding: utf-8 -*-
"""只读探针：空格调出 QL 之后「切到别的程序再回来按空格」时,
QuickLook 那扇预览窗到底是什么状态 —— 关闭了? 还是开着但被 TB 压在底下看不见?

⚠ 纯轮询（EnumWindows + GetForegroundWindow + WindowFromPoint），**不装任何键盘钩子**
   —— 按项目惯例，挂钩子的探针会打乱 TB / MyKeymap 的按键链，一律不用。
⚠ 不抢前台、无窗口、不改任何东西。

判定「用户看不看得见」：取 QL 窗**中心点**做 WindowFromPoint，拿回来的 hwnd 的顶层祖先
是不是它自己 —— 不是就说明被别的窗口（TB / 浏览器）压住了，API 说 IsWindowVisible=True
但用户看不见。这一步就是本次取证的核心。

用法：起本脚本 → 按提示操作 → 输出写 _ql_switch_out.txt
"""
import ctypes
import ctypes.wintypes as w
import io
import os
import time

u = ctypes.windll.user32
u.GetWindowTextLengthW.restype = ctypes.c_int
u.WindowFromPoint.restype = w.HWND
u.WindowFromPoint.argtypes = [w.POINT]

EnumWindows = u.EnumWindows
CB = ctypes.WINFUNCTYPE(ctypes.c_bool, w.HWND, w.LPARAM)
GA_ROOT = 2

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "_ql_switch_out.txt")
f = io.open(OUT, "w", encoding="utf-8", buffering=1)


def title_of(h):
    n = u.GetWindowTextLengthW(h)
    if n <= 0:
        return ""
    b = ctypes.create_unicode_buffer(n + 1)
    u.GetWindowTextW(h, b, n + 1)
    return b.value


def class_of(h):
    b = ctypes.create_unicode_buffer(256)
    u.GetClassNameW(h, b, 256)
    return b.value


def pid_of(h):
    p = w.DWORD()
    u.GetWindowThreadProcessId(h, ctypes.byref(p))
    return p.value


def on_top(h, r):
    """这扇窗在自己的中心点上是不是最顶的那个（= 用户真看得见）。"""
    if r[2] <= 0 or r[3] <= 0:
        return False
    pt = w.POINT(r[0] + r[2] // 2, r[1] + r[3] // 2)
    hit = u.WindowFromPoint(pt)
    if not hit:
        return False
    root = u.GetAncestor(hit, GA_ROOT) or hit
    return root == h


def snapshot():
    fg = u.GetForegroundWindow()
    fg_desc = (pid_of(fg), class_of(fg), title_of(fg)[:36])
    ql = []

    def cb(h, l):
        if "QuickLook.exe" in class_of(h):
            r = w.RECT()
            u.GetWindowRect(h, ctypes.byref(r))
            rect = (r.left, r.top, r.right - r.left, r.bottom - r.top)
            vis = bool(u.IsWindowVisible(h))
            top = bool(u.GetWindowLongW(h, -20) & 0x8) if vis else False   # WS_EX_TOPMOST
            ql.append((hex(h), vis, top, on_top(h, rect) if vis else False, rect))
        return True

    EnumWindows(CB(cb), 0)
    return fg_desc, ql


t0 = time.time()
last = None
f.write("=== 探针就绪（纯轮询，无钩子）===\n")
f.write("列含义: QL窗句柄 可见 置顶 在最上层 rect\n")
while time.time() - t0 < 180:
    fg, ql = snapshot()
    key = (fg, tuple((a, b, c, d) for a, b, c, d, _ in ql))
    if key != last:
        last = key
        line = "%7.2fs 前台 pid=%-6s %-28s %r" % (time.time() - t0, fg[0], fg[1], fg[2])
        if ql:
            line += "  ||  " + "  ".join(
                "%s vis=%d top=%d 面=%d %s" % (h, v, t, o, r) for h, v, t, o, r in ql)
        else:
            line += "  ||  (没有 QuickLook 窗)"
        f.write(line + "\n")
    time.sleep(0.05)
f.write("=== 结束 ===\n")
f.close()
