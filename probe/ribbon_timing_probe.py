"""只读探针：给一个新开的文件夹窗口记一条「建窗时序」。

要回答的问题：explorer 到底在第几秒把 Ribbon 建出来、第几秒把窗口显示出来。

只要「Ribbon 出现的时刻」**早于**「窗口可见的时刻」，中间就存在一个空档 ——
在那段里补上「不进任务栏」标记（`WS_EX_TOOLWINDOW`）就能两全：
  · 打早了（建窗那一次就带）⇒ explorer 不建 Ribbon，退回老式菜单栏 = 顶上那条白条；
  · 打晚了（窗口已经可见）⇒ 任务栏按「可见那一帧」的样式给它加了按钮 = 图标闪一下。

用法：python ribbon_timing_probe.py [秒数] [输出文件]
⚠ 纯读：不点鼠标、不发消息、不抢前台。别往这里加任何写操作。
"""

import ctypes
import sys
import time
from ctypes import wintypes

u32 = ctypes.windll.user32

GWL_EXSTYLE = -20

ENUMPROC = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

for fn, args in (
    ("EnumWindows", [ENUMPROC, wintypes.LPARAM]),
    ("EnumChildWindows", [wintypes.HWND, ENUMPROC, wintypes.LPARAM]),
    ("GetClassNameW", [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]),
    ("GetWindowTextW", [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]),
    ("IsWindowVisible", [wintypes.HWND]),
    ("GetWindowThreadProcessId", [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]),
):
    getattr(u32, fn).argtypes = args

# 64 位下不声明 restype，句柄会被当 32 位截断
u32.GetWindowLongPtrW.argtypes = [wintypes.HWND, ctypes.c_int]
u32.GetWindowLongPtrW.restype = ctypes.c_longlong


def cls(h):
    b = ctypes.create_unicode_buffer(256)
    u32.GetClassNameW(h, b, 256)
    return b.value


def descendants(h):
    out = []

    def cb(c, _):
        out.append(c)
        return True

    u32.EnumChildWindows(h, ENUMPROC(cb), 0)
    return out


def tops():
    out = []

    def cb(h, _):
        out.append(h)
        return True

    u32.EnumWindows(ENUMPROC(cb), 0)
    return out


def pid_of(h):
    p = wintypes.DWORD()
    u32.GetWindowThreadProcessId(h, ctypes.byref(p))
    return p.value


def main():
    secs = float(sys.argv[1]) if len(sys.argv) > 1 else 40.0
    out_path = sys.argv[2] if len(sys.argv) > 2 else None
    lines = []

    def log(t, tag, h, extra):
        lines.append("  %6.3fs %-14s h=0x%08X pid=%-6d %s" % (t, tag, h, pid_of(h), extra))

    t0 = time.time()
    state = {}
    while time.time() - t0 < secs:
        t = time.time() - t0
        for h in tops():
            c = cls(h)
            if c != "CabinetWClass" and c != "ExploreWClass":
                continue
            subs = [cls(k) for k in descendants(h)]
            ribbon = "UIRibbonCommandBarDock" in subs
            menubar = "ReBarWindow32" in subs          # 老式菜单栏的骨头
            vis = bool(u32.IsWindowVisible(h))
            ex = u32.GetWindowLongPtrW(h, GWL_EXSTYLE) & 0xFFFFFFFF
            prev = state.get(h)
            if prev is None:
                state[h] = dict(vis=vis, ex=ex, ribbon=ribbon, menubar=menubar)
                log(t, "首次出现", h,
                    "vis=%d ex=0x%08X Ribbon=%s 老式菜单栏=%s"
                    % (vis, ex, "有" if ribbon else "无", "有" if menubar else "无"))
                continue
            if vis != prev["vis"]:
                state[h]["vis"] = vis
                log(t, "可见变化", h, "vis=%d -> %d   ex=0x%08X" % (prev["vis"], vis, ex))
            if ex != prev["ex"]:
                state[h]["ex"] = ex
                log(t, "样式变化", h, "0x%08X -> 0x%08X" % (prev["ex"], ex))
            if ribbon != prev["ribbon"]:
                state[h]["ribbon"] = ribbon
                log(t, "Ribbon", h, "出现" if ribbon else "消失")
            if menubar != prev["menubar"]:
                state[h]["menubar"] = menubar
                log(t, "老式菜单栏", h, "出现" if menubar else "消失")
        time.sleep(0.012)

    text = "\n".join(lines) if lines else "（没看到 CabinetWClass）"
    if out_path:
        with open(out_path, "w", encoding="utf-8") as f:
            f.write(text + "\n")
    print(text)


if __name__ == "__main__":
    main()
