# -*- coding: utf-8 -*-
"""极简：每 2ms 扫一遍所有顶层浏览窗，记录 (hwnd, pid, vis, exStyle, rect)，一变就落一行。

为什么单独写一个：复合探针 `flash_burst.py` 又要装钩子、又要采屏、又要落图，实测会丢事件
（现场日志明明有的 SHOW，它一条都没收到）。要钉的只是一件事 ——
**我们自己那扇 `explorer /n,/separate` 窗，被我们打的 `WS_EX_LAYERED` 到底有没有被 explorer 冲掉。**
那就只干这一件事：纯轮询、不装钩子、不采屏、不做判定。

exStyle 里的关键两位：
  L = WS_EX_LAYERED    （我们 `MakeTransparent` 打的「整窗透明」记号）
  T = WS_EX_TOOLWINDOW （不进任务栏）

用法: python -u exstyle_trace.py [秒数=60] [--fire=<路径>] > probe/_ex.log 2>&1
"""
import ctypes
import ctypes.wintypes as wt
import os
import sys
import threading
import time

u32 = ctypes.windll.user32
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    pass

u32.GetClassNameW.argtypes = [wt.HWND, wt.LPWSTR, ctypes.c_int]
u32.GetWindowRect.argtypes = [wt.HWND, ctypes.POINTER(wt.RECT)]
u32.GetWindowThreadProcessId.argtypes = [wt.HWND, ctypes.POINTER(ctypes.c_ulong)]
u32.IsWindowVisible.argtypes = [wt.HWND]
u32.IsWindowVisible.restype = wt.BOOL
u32.GetWindowLongPtrW.argtypes = [wt.HWND, ctypes.c_int]
u32.GetWindowLongPtrW.restype = ctypes.c_longlong
u32.GetParent.argtypes = [wt.HWND]
u32.GetParent.restype = wt.HWND
u32.GetLayeredWindowAttributes.argtypes = [wt.HWND, ctypes.POINTER(ctypes.c_ulong),
                                           ctypes.POINTER(ctypes.c_ubyte),
                                           ctypes.POINTER(ctypes.c_ulong)]
u32.GetLayeredWindowAttributes.restype = wt.BOOL
EnumProc = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
u32.EnumWindows.argtypes = [EnumProc, wt.LPARAM]

GWL_EXSTYLE = -20
WS_EX_LAYERED = 0x00080000
WS_EX_TOOLWINDOW = 0x00000080
WS_EX_APPWINDOW = 0x00040000
CABS = ("CabinetWClass", "ExploreWClass")

_cls = ctypes.create_unicode_buffer(256)
_rect = wt.RECT()
_pid = ctypes.c_ulong(0)
_pidmap = {}


def stamp():
    return time.strftime("%H:%M:%S", time.localtime()) + ".%03d" % int((time.time() % 1) * 1000)


def cls_of(h):
    u32.GetClassNameW(h, _cls, 256)
    return _cls.value


def rect_of(h):
    u32.GetWindowRect(h, ctypes.byref(_rect))
    return (_rect.left, _rect.top, _rect.right, _rect.bottom)


def pid_of(h):
    u32.GetWindowThreadProcessId(h, ctypes.byref(_pid))
    return _pid.value


def pd(pid):
    if pid in _pidmap:
        return _pidmap[pid]
    name = "pid=%d" % pid
    try:
        h = ctypes.windll.kernel32.OpenProcess(0x1000, False, pid)
        if h:
            buf = ctypes.create_unicode_buffer(512)
            n = ctypes.c_uint(512)
            if ctypes.windll.kernel32.QueryFullProcessImageNameW(h, 0, buf, ctypes.byref(n)):
                name = os.path.basename(buf.value) + "(%d)" % pid
            ctypes.windll.kernel32.CloseHandle(h)
    except Exception:
        pass
    _pidmap[pid] = name
    return name


def flags(h):
    ex = int(u32.GetWindowLongPtrW(h, GWL_EXSTYLE)) & 0xFFFFFFFF
    s = ("L" if ex & WS_EX_LAYERED else "-") + ("T" if ex & WS_EX_TOOLWINDOW else "-")
    if ex & WS_EX_APPWINDOW:
        s += "A"
    # ★ 真正决定「看不看得见」的是 alpha（`IsWindowVisible` 只看样式位，不看这个）：
    #   LAYERED + alpha=0 ⇒ 整窗一点不画；alpha=255（或读不出来）⇒ 照常显示。
    if ex & WS_EX_LAYERED:
        col = ctypes.c_ulong(0)
        al = ctypes.c_ubyte(0)
        fl = ctypes.c_ulong(0)
        if u32.GetLayeredWindowAttributes(h, ctypes.byref(col), ctypes.byref(al),
                                          ctypes.byref(fl)):
            s += "@%d" % al.value
        else:
            s += "@?"
    return s


def scan():
    out = {}

    def cb(h, l):
        c = cls_of(h)
        if c in CABS:
            out[h] = (pid_of(h), bool(u32.IsWindowVisible(h)), flags(h),
                      rect_of(h), u32.GetParent(h))
        return True

    u32.EnumWindows(EnumProc(cb), 0)
    return out


def main():
    seconds, fire = 60.0, None
    for a in sys.argv[1:]:
        if a.startswith("--fire="):
            fire = a.split("=", 1)[1]
        else:
            try:
                seconds = float(a)
            except ValueError:
                pass
    if fire:
        def _f():
            time.sleep(3)
            r = ctypes.windll.shell32.ShellExecuteW(None, "open", fire, None, None, 1)
            print("%s  >>> 触发 %s 返回 %s" % (stamp(), fire, int(r) if r else r))
        threading.Thread(target=_f, daemon=True).start()

    print("%s  开始（%.0f 秒）—— 每 2ms 扫所有顶层浏览窗的 exStyle/可见性" % (stamp(), seconds))
    seen = scan()
    for h, v in seen.items():
        print("%s  基线 cab=0x%X %s vis=%s 样式=%s rect=%s" %
              (stamp(), int(h), pd(v[0]), "是" if v[1] else "否", v[2], v[3]))
    t0 = time.time()
    prev = dict(seen)
    n = 0
    while time.time() - t0 < seconds:
        cur = scan()
        n += 1
        for h, v in cur.items():
            if prev.get(h) != v:
                print("%s  %s cab=0x%X %s vis=%s 样式=%s rect=%s 父=0x%X" %
                      (stamp(), "新" if h not in prev else "变", int(h), pd(v[0]),
                       "是" if v[1] else "否", v[2], v[3], int(v[4] or 0)))
        for h in prev:
            if h not in cur:
                print("%s  没了 cab=0x%X %s" % (stamp(), int(h), pd(prev[h][0])))
        prev = cur
        time.sleep(0.002)
    print("%s  结束（%d 轮，平均 %.1fms/轮）" %
          (stamp(), n, (time.time() - t0) * 1000 / max(1, n)))


main()
