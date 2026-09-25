# -*- coding: utf-8 -*-
"""探针：shell 自己开的那扇文件夹窗，能不能在**建窗那一刻**就让它在屏幕上「什么都不画」。

为什么问这个（2026-09-25 实测量到的数字）：
  · 那扇窗 `SHOW +0ms` 就可见了，而 `ShellWindows.LocationURL` 要 `SHOW +116ms` 才有值；
  · 退一步「一露脸就 `SW_HIDE` 按住」也不行 —— 实测那个跨进程 `ShowWindow` **花了 109ms**
    （同步调用落在 explorer 正忙的线程上，得等它回来处理），我们还是露了 118ms。
  ⇒ 结论：`SHOW` 之后做的任何**同步**窗口操作都赶不上那一帧。只能在 SHOW 之前动手。

两条候选（都在 CREATE 那一刻做；那时离 SHOW 还有 ~430ms，绰绰有余）：
  ① region —— `SetWindowRgn(h, 空区域)`：绘制区域为空 ⇒ 不画任何像素。
     ⚠ 不是样式位（跟那条红线「不置透明 / 不改 exstyle」不冲突），`SetWindowRgn(h, NULL)` 就还原。
  ② move  —— `SetWindowPos` 挪到屏幕外 ⇒ 画了也看不见（但任务栏按钮照样有）。

要量的是：这两种做法**扛不扛得住 explorer 随后那次 ShowWindow**（它可能自己重设位置/区域），
以及「窗口真在屏幕上露脸的样本数」。判据 = 可见 **且** 绘制区不为空 **且** 矩形跟屏幕有交集。
（三个模式都跑一遍才有对照：`none` 是基线。）

写在你自己的窗上的东西只有那一扇**我们触发出来的** shell 窗（收尾 `SC_CLOSE` 前生效），
其它窗口一律只读。用法：
    python shell_blank_probe.py [region|move|none] [秒数=6] [路径]
"""

import ctypes
import ctypes.wintypes as wt
import sys
import time
from ctypes import POINTER, byref, c_byte, c_int, c_long, c_uint, c_ulong, c_ushort, c_void_p

ole32 = ctypes.windll.ole32
oleaut32 = ctypes.windll.oleaut32
gdi32 = ctypes.windll.gdi32
user32 = ctypes.windll.user32

oleaut32.SysAllocString.restype = c_void_p
oleaut32.SysAllocString.argtypes = [ctypes.c_wchar_p]
user32.FindWindowW.restype = wt.HWND
user32.FindWindowW.argtypes = [wt.LPCWSTR, wt.LPCWSTR]
user32.GetClassNameW.argtypes = [wt.HWND, wt.LPWSTR, c_int]
user32.GetWindowThreadProcessId.argtypes = [wt.HWND, POINTER(wt.DWORD)]
user32.IsWindowVisible.argtypes = [wt.HWND]
user32.IsWindow.argtypes = [wt.HWND]
user32.GetWindowRect.argtypes = [wt.HWND, POINTER(wt.RECT)]
user32.SetWindowRgn.argtypes = [wt.HWND, c_void_p, wt.BOOL]
user32.SetWindowRgn.restype = c_int
user32.GetWindowRgn.argtypes = [wt.HWND, c_void_p]
user32.GetWindowRgn.restype = c_int
user32.SetWindowPos.argtypes = [wt.HWND, wt.HWND, c_int, c_int, c_int, c_int, c_uint]
user32.SetWindowPos.restype = wt.BOOL
user32.PostMessageW.argtypes = [wt.HWND, c_uint, c_void_p, c_void_p]
user32.SetWinEventHook.restype = c_void_p
user32.SetWinEventHook.argtypes = [c_uint, c_uint, c_void_p, c_void_p, c_uint, c_uint, c_uint]
user32.PeekMessageW.argtypes = [POINTER(wt.MSG), wt.HWND, c_uint, c_uint, c_uint]
gdi32.CreateRectRgn.argtypes = [c_int, c_int, c_int, c_int]
gdi32.CreateRectRgn.restype = c_void_p

EVENT_OBJECT_CREATE, EVENT_OBJECT_SHOW = 0x8000, 0x8002
WINEVENT_OUTOFCONTEXT = 0x0000
SWP_NOSIZE, SWP_NOZORDER, SWP_NOACTIVATE = 0x0001, 0x0004, 0x0010
NULLREGION = 1                      # 绘制区是空的 ⇒ 什么都不画
OFFSCREEN = -30000
CAB = ("CabinetWClass", "ExploreWClass")
SC_CLOSE, WM_SYSCOMMAND = 0xF060, 0x0112
DISPATCH_METHOD, DISPATCH_PROPERTYGET = 1, 2
LOCALE_USER_DEFAULT = 0x400
CLSCTX_LOCAL_INPROC = 0x5
VT_BSTR, VT_I4, VT_VARIANT, VT_DISPATCH = 8, 3, 12, 9
VT_BYREF = 0x4000

MODE = "region"
VERBOSE = False
t0 = time.time()
marks = {}
target = [None, 0]                  # [hwnd, 原始 rect]
probe_log = []


class GUID(ctypes.Structure):
    _fields_ = [("d1", c_ulong), ("d2", c_ushort), ("d3", c_ushort), ("d4", c_byte * 8)]


def guid(s):
    g = GUID()
    ole32.CLSIDFromString(ctypes.c_wchar_p(s), byref(g))
    return g


class VARIANT(ctypes.Structure):
    class _U(ctypes.Union):
        _fields_ = [("llVal", ctypes.c_longlong), ("lVal", c_int), ("bstrVal", c_void_p),
                    ("pdispVal", c_void_p), ("pad", c_byte * 16)]
    _fields_ = [("vt", c_ushort), ("r1", c_ushort), ("r2", c_ushort), ("r3", c_ushort),
                ("u", _U)]


class DISPPARAMS(ctypes.Structure):
    _fields_ = [("rgvarg", c_void_p), ("rgdispidNamedArgs", c_void_p),
                ("cArgs", c_uint), ("cNamedArgs", c_uint)]


class EXCEPINFO(ctypes.Structure):
    _fields_ = [("wCode", c_ushort), ("wReserved", c_ushort), ("bstrSource", c_void_p),
                ("bstrDescription", c_void_p), ("bstrHelpFile", c_void_p),
                ("dwHelpContext", c_ulong), ("pvReserved", c_void_p),
                ("pfnDeferredFillIn", c_void_p), ("scode", c_int)]


def vcall(p, index, restype, *at):
    vt = ctypes.cast(p, POINTER(c_void_p))[0]
    return ctypes.cast(ctypes.cast(vt, POINTER(c_void_p))[index],
                       ctypes.WINFUNCTYPE(restype, c_void_p, *at))


def dispid(p, name):
    iid = GUID()
    d = c_int()
    nm = ctypes.c_wchar_p(name)
    hr = vcall(p, 5, c_long, POINTER(GUID), POINTER(ctypes.c_wchar_p), c_uint, c_ulong,
               POINTER(c_int))(p, byref(iid), byref(nm), 1, LOCALE_USER_DEFAULT, byref(d))
    return hr, d.value


def invoke_raw(p, did, flags, rgv, n):
    dp = DISPPARAMS()
    dp.rgvarg = rgv
    dp.cArgs = n
    res = VARIANT()
    ei = EXCEPINFO()
    err = c_uint()
    iid = GUID()
    hr = vcall(p, 6, c_long, c_int, POINTER(GUID), c_ulong, c_ushort, POINTER(DISPPARAMS),
               POINTER(VARIANT), POINTER(EXCEPINFO), POINTER(c_uint))(
        p, did, byref(iid), LOCALE_USER_DEFAULT, flags, byref(dp), byref(res), byref(ei),
        byref(err))
    return hr, res


def vstr(s):
    v = VARIANT()
    v.vt = VT_BSTR
    v.u.bstrVal = oleaut32.SysAllocString(s)
    return v


def shell_pid():
    h = user32.FindWindowW("Shell_TrayWnd", None)
    p = wt.DWORD()
    user32.GetWindowThreadProcessId(h, byref(p))
    return p.value


def cls_of(h):
    b = ctypes.create_unicode_buffer(128)
    user32.GetClassNameW(h, b, 128)
    return b.value


def pid_of(h):
    p = wt.DWORD()
    user32.GetWindowThreadProcessId(h, byref(p))
    return p.value


def rect_of(h):
    r = wt.RECT()
    user32.GetWindowRect(h, byref(r))
    return (r.left, r.top, r.right, r.bottom)


def rgn_of(h):
    """1 = 空区域（不画东西）；0 = 读不到/没区域；2/3 = 有区域。"""
    return user32.GetWindowRgn(h, None)


def on_screen(r):
    """矩形跟虚拟桌面有交集吗。"""
    l, t, rr, b = r
    sl, st = user32.GetSystemMetrics(76), user32.GetSystemMetrics(77)
    sw, sh = user32.GetSystemMetrics(78), user32.GetSystemMetrics(79)
    return not (rr <= sl or b <= st or l >= sl + sw or t >= st + sh)


def note(k, extra=""):
    if k in marks:
        return
    marks[k] = time.time() - t0
    print("%7.3fs %-24s %s" % (marks[k], k, extra), flush=True)


def blank_it(h):
    target[1] = rect_of(h)
    if MODE == "none":
        note("不做处理(none)", "rect=%s" % (target[1],))
        return
    if MODE == "region":
        t = time.perf_counter()
        g = gdi32.CreateRectRgn(0, 0, 0, 0)
        ok = user32.SetWindowRgn(h, g, True)
        ms = (time.perf_counter() - t) * 1000
        # 空 region 的句柄交给系统了，别删
        note("SetWindowRgn(空)", "ok=%d 之后 rgn=%d 花 %.0fms" % (ok, rgn_of(h), ms))
    else:
        t = time.perf_counter()
        user32.SetWindowPos(h, None, OFFSCREEN, OFFSCREEN, 0, 0,
                            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE)
        ms = (time.perf_counter() - t) * 1000
        note("SetWindowPos(屏外)", "之后 rect=%s 花 %.0fms" % (rect_of(h), ms))


EVENTPROC = ctypes.WINFUNCTYPE(None, c_void_p, c_uint, wt.HWND, c_int, c_int, c_uint, c_uint)


def on_event(hook, evt, hwnd, idobj, idchild, thread, when):
    try:
        if VERBOSE:
            print("    [evt] 0x%04X hwnd=0x%X idobj=%d cls=%s pid=%d"
                  % (evt, hwnd, idobj, cls_of(hwnd), pid_of(hwnd)), flush=True)
        if idobj != 0:                                     # 只要窗口本身，不要子对象事件
            return
        if cls_of(hwnd) not in CAB:
            return
        if pid_of(hwnd) != target_pid[0]:
            return
        if target[0] is None:
            target[0] = hwnd
            note("CREATE", "hwnd=0x%X" % hwnd)
            blank_it(hwnd)
        elif hwnd != target[0]:
            return
        if evt == EVENT_OBJECT_SHOW:
            note("SHOW", "vis=%d rgn=%d rect=%s onscreen=%s" % (
                bool(user32.IsWindowVisible(hwnd)), rgn_of(hwnd), rect_of(hwnd),
                on_screen(rect_of(hwnd))))
    except Exception as e:                                  # noqa: BLE001
        print("  ! 回调出错:", e, flush=True)


def open_in_shell(path):
    clsid = guid("{13709620-C279-11CE-A49E-444553540000}")
    iid = guid("{00020400-0000-0000-C000-000000000046}")
    p = c_void_p()
    hr = ole32.CoCreateInstance(byref(clsid), None, CLSCTX_LOCAL_INPROC, byref(iid), byref(p))
    if hr != 0 or not p.value:
        print("  ! CoCreateInstance(Shell.Application) hr=0x%08X" % (hr & 0xFFFFFFFF), flush=True)
        return
    _, did = dispid(p.value, "Explore")
    if not did:
        print("  ! 拿不到 Explore 的 dispid", flush=True)
        return
    t = time.time()
    hr2, _ = invoke_raw(p.value, did, DISPATCH_METHOD,
                        ctypes.cast((VARIANT * 1)(vstr(path)), c_void_p).value, 1)
    print("  Explore(%s) hr=0x%08X（%0.0fms）" % (path, hr2 & 0xFFFFFFFF,
                                                 (time.time() - t) * 1000), flush=True)


def main():
    global MODE, VERBOSE
    MODE = sys.argv[1] if len(sys.argv) > 1 else "region"
    VERBOSE = "--v" in sys.argv
    secs = float(sys.argv[2]) if len(sys.argv) > 2 else 6.0
    path = sys.argv[3] if len(sys.argv) > 3 else r"D:\Users\a\Desktop"
    target_pid[0] = shell_pid()
    print("模式=%s 时长=%.0fs 目标=%s shell pid=%d" % (MODE, secs, path, target_pid[0]), flush=True)

    proc = EVENTPROC(on_event)
    hook = user32.SetWinEventHook(EVENT_OBJECT_CREATE, EVENT_OBJECT_SHOW, None, proc,
                                  0, 0, WINEVENT_OUTOFCONTEXT)
    print("钩子 = 0x%X" % (hook or 0), flush=True)

    msg = wt.MSG()
    fired = False
    flash_samples = 0
    onscreen_vis_samples = 0
    ever_visible = False
    while time.time() - t0 < secs:
        el = time.time() - t0
        if not fired and el >= 1.0:
            fired = True
            note("触发", path)
            open_in_shell(path)
        while user32.PeekMessageW(byref(msg), None, 0, 0, 1):
            user32.TranslateMessage(byref(msg))
            user32.DispatchMessageW(byref(msg))
        h = target[0]
        if h and user32.IsWindow(h):
            vis = bool(user32.IsWindowVisible(h))
            r = rect_of(h)
            rg = rgn_of(h)
            if vis:
                if not ever_visible:
                    ever_visible = True
                    note("★ 首次可见", "rgn=%d rect=%s onscreen=%s" % (rg, r, on_screen(r)))
                # 「真的会在屏幕上画东西」= 可见 + 有绘制区（不是空区域）+ 跟屏幕有交集
                if rg != NULLREGION and on_screen(r):
                    onscreen_vis_samples += 1
                elif rg == NULLREGION or not on_screen(r):
                    flash_samples += 1
        time.sleep(0.005)

    h = target[0]
    print("\n-- 汇总（模式=%s）--" % MODE)
    for k in ("触发", "CREATE", "SetWindowRgn(空)", "SetWindowPos(屏外)", "不做处理(none)",
              "SHOW", "★ 首次可见"):
        if k in marks:
            print("  %-24s +%.0fms" % (k, marks[k] * 1000))
    print("  可见期间被「挡住了」的采样：%d 次（绘制区被清空 / 挪到了屏外）" % flash_samples)
    print("  可见期间**仍会在屏幕上画东西**的采样：%d 次  ← 想要 0" % onscreen_vis_samples)
    if h and user32.IsWindow(h):
        print("  收尾前：vis=%d rgn=%d rect=%s" % (
            bool(user32.IsWindowVisible(h)), rgn_of(h), rect_of(h)))
        # 还原我们动过的东西，再请 shell 关掉它
        if MODE == "region":
            user32.SetWindowRgn(h, None, True)
        elif MODE == "move":
            l, t, rr, b = target[1]
            user32.SetWindowPos(h, None, l, t, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE)
        user32.PostMessageW(h, WM_SYSCOMMAND, SC_CLOSE, 0)
        print("  已还原 + 已请求关闭 hwnd=0x%X" % h)
    else:
        print("  没抓到目标窗口")


target_pid = [0]
main()
