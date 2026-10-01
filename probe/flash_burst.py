# -*- coding: utf-8 -*-
"""只读：第三方 `/select,` 那一下「原生资源管理器闪一下」到底是谁画的。

思路（前两版探针失败的教训）：
  · 「整屏找异常」不行 —— 用户正常用电脑时整屏一直在剧烈变化（300 秒里 2 万格以上的
    全屏切换一二十次），阈值法区分不出那一下闪。
  · 「整屏 20ms 帧率」也不行 —— 那扇窗可见只有 15~31ms（应用日志：`已按住（耗时 16ms）`），
    整屏一帧就要 20ms 以上，容易整帧跳过。

所以：**以窗口为触发点，命中后换成 1ms 粒度的定点采样**。
  1) 平时：每 ~20ms 一帧整屏小图（`StretchBlt` 缩到 1/5，几毫秒），
     顺带用 `FindWindowExW` 按类名捞顶层 `CabinetWClass` / `ExploreWClass`。
     ⚠ 我们自己的窗被 SetParent 收编成子窗、不会出现在这里 ⇒ 报出来的一定是
       **还没被收编的**、也就是说「正可能画在屏幕上」的那一扇。
  2) 一冒出新的浏览窗（或已见过的换矩形/可见性）：落一张出现前的整屏 PNG，然后进 2 秒
     **1ms 采样**：在它矩形内部取 8x8 个点的颜色指纹 + 可见性，**一变就记一行**。
     空白绘制区的窗（我们 `SetWindowRgn` 清过的）里面露的是它背后的桌面 —— 指纹是死的；
     只要 explorer 真画了一帧，指纹立刻跳变。这就把「那一下闪的是不是它」钉死了。

只读：不碰任何窗口、不 SetWindowRgn、不发消息、不 SetParent。

用法: python -u flash_burst.py [秒数=90] > probe/_flash.log 2>&1   （必须 -u）
"""
import collections
import ctypes
import ctypes.wintypes as wt
import os
import sys
import threading
import time
from ctypes import byref, c_void_p, c_int, c_uint, c_long, c_ulong, POINTER

from PIL import Image, ImageChops

user32 = ctypes.windll.user32
gdi32 = ctypes.windll.gdi32
kernel32 = ctypes.windll.kernel32

user32.GetDC.restype = c_void_p
user32.GetDC.argtypes = [c_void_p]
user32.ReleaseDC.argtypes = [c_void_p, c_void_p]
user32.GetSystemMetrics.argtypes = [c_int]
user32.GetClassNameW.argtypes = [wt.HWND, wt.LPWSTR, c_int]
user32.GetClassNameW.restype = c_int
user32.GetWindowThreadProcessId.argtypes = [wt.HWND, POINTER(wt.DWORD)]
user32.GetWindowRect.argtypes = [wt.HWND, POINTER(wt.RECT)]
user32.IsWindowVisible.argtypes = [wt.HWND]
user32.IsWindowVisible.restype = wt.BOOL
user32.FindWindowW.argtypes = [wt.LPCWSTR, wt.LPCWSTR]
user32.FindWindowW.restype = wt.HWND
user32.FindWindowExW.argtypes = [wt.HWND, wt.HWND, wt.LPCWSTR, wt.LPCWSTR]
user32.FindWindowExW.restype = wt.HWND
user32.IsWindow.argtypes = [wt.HWND]
user32.IsWindow.restype = wt.BOOL

# ---- WinEvent 钩子：把「发现新窗」从 20ms 轮询升级成「创建/显示那一刻就报」 ----
#   上一版的硬伤就是发现太晚（轮询 + 落图，等到手时窗已 `SW_HIDE`，所有采样点
#   都是 `vis=否`）—— 那正是「闪在哪」最该看的一段，结果整段漏掉。
user32.SetWinEventHook.argtypes = [c_uint, c_uint, c_void_p, c_void_p, c_ulong, c_ulong, c_uint]
user32.SetWinEventHook.restype = c_void_p
user32.UnhookWinEvent.argtypes = [c_void_p]
user32.PeekMessageW.argtypes = [c_void_p, c_void_p, c_uint, c_uint, c_uint]
user32.TranslateMessage.argtypes = [c_void_p]
user32.DispatchMessageW.argtypes = [c_void_p]
user32.GetWindowLongPtrW.argtypes = [wt.HWND, c_int]
user32.GetWindowLongPtrW.restype = ctypes.c_longlong
user32.GetWindow.argtypes = [wt.HWND, c_uint]
user32.GetWindow.restype = wt.HWND

gdi32.GetPixel.argtypes = [c_void_p, c_int, c_int]
gdi32.GetPixel.restype = c_uint
gdi32.CreateCompatibleDC.restype = c_void_p
gdi32.CreateCompatibleDC.argtypes = [c_void_p]
gdi32.DeleteDC.argtypes = [c_void_p]
gdi32.SelectObject.restype = c_void_p
gdi32.SelectObject.argtypes = [c_void_p, c_void_p]
gdi32.DeleteObject.argtypes = [c_void_p]
gdi32.BitBlt.argtypes = [c_void_p, c_int, c_int, c_int, c_int, c_void_p, c_int, c_int, c_uint]
gdi32.StretchBlt.argtypes = [c_void_p, c_int, c_int, c_int, c_int, c_void_p, c_int, c_int, c_int, c_int, c_uint]
gdi32.SetStretchBltMode.argtypes = [c_void_p, c_int]
gdi32.CreateDIBSection.restype = c_void_p
gdi32.CreateDIBSection.argtypes = [c_void_p, c_void_p, c_uint, POINTER(c_void_p), c_void_p, c_uint]

SRCCOPY = 0x00CC0020
DIB_RGB_COLORS = 0
BROWSER = ("CabinetWClass", "ExploreWClass")
TRACE_SEC = 2.0
K = 5
# 标题栏那一角（右上角三个按钮就在这儿）。2026-10-01 川给的观测：闪的是
# 「原生资源管理器右上角那三个按钮」—— 于是把这一角单独做成一路指纹：
# 绘制区被清空的窗，客户区是死的，但**非客户区（DWM 画的标题栏/按钮）可能照样画出来**，
# 只看客户区的老探针会整个漏掉。这一角变了就落一张裁好的小图，肉眼一看就认。
CAPW, CAPH = 220, 48
SHOTS = os.path.join(os.path.dirname(os.path.abspath(__file__)), "shots")

PM_REMOVE = 0x0001
WINEVENT_OUTOFCONTEXT = 0x0000
GWL_EXSTYLE = -20
GWL_STYLE = -16
GW_OWNER = 4
WS_EX_LAYERED = 0x00080000
WS_EX_TOOLWINDOW = 0x00000080
WS_EX_APPWINDOW = 0x00040000
WS_VISIBLE = 0x10000000

# 关心的那几条（CREATE / SHOW / HIDE / DESTROY / FOCUS / PARENTCHANGE）。
# 装备时注册 0x0001~0x8018 全量（跨进程零成本），回调里只留这几条，免得被 LOCATIONCHANGE 刷屏。
EVT = {
    0x0003: "FG", 0x0016: "MINSTART", 0x0017: "MINEND",
    0x8000: "CREATE", 0x8001: "DESTROY", 0x8002: "SHOW", 0x8003: "HIDE",
    0x8005: "FOCUS", 0x800B: "LOCCHG", 0x800F: "PARENTCHG",
}
WATCH_EV = {0x8000, 0x8001, 0x8002, 0x8003, 0x8005, 0x800F}

WinEventProc = ctypes.WINFUNCTYPE(None, c_void_p, c_uint, c_void_p,
                                  c_long, c_long, c_ulong, c_ulong)

# 钩子回调**不做任何重活**（跨进程回调里多花一毫秒就可能卡住系统）——
# 只往这个队列里塞一条，主循环再取出来处理。
_evq = collections.deque()
_msg = wt.MSG()


def pump():
    """把投递到本线程的 WinEvent 消息放行（回调是 OUTOFCONTEXT，进的是本线程队列）。"""
    while user32.PeekMessageW(byref(_msg), None, 0, 0, PM_REMOVE):
        user32.TranslateMessage(byref(_msg))
        user32.DispatchMessageW(byref(_msg))


def on_event(hook, ev, hwnd, idobj, idchild, tid, ts):
    if hwnd:
        _evq.append((ev, hwnd or 0, tid, ts))


def exstyle(h):
    return int(user32.GetWindowLongPtrW(h, GWL_EXSTYLE)) & 0xFFFFFFFF


def style_vis(h):
    return bool(user32.GetWindowLongPtrW(h, GWL_STYLE) & WS_VISIBLE)


def exflags(h):
    """把跟本次排查相关的两位翻成人话：LAYERED（我们置透明打的）、TOOLWINDOW（不进任务栏）。"""
    ex = exstyle(h)
    return "%s%s" % ("L" if ex & WS_EX_LAYERED else "-",
                     "T" if ex & WS_EX_TOOLWINDOW else "-")


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [("biSize", c_uint), ("biWidth", c_int), ("biHeight", c_int),
                ("biPlanes", ctypes.c_ushort), ("biBitCount", ctypes.c_ushort),
                ("biCompression", c_uint), ("biSizeImage", c_uint),
                ("biXPelsPerMeter", c_int), ("biYPelsPerMeter", c_int),
                ("biClrUsed", c_uint), ("biClrImportant", c_uint)]


class BITMAPINFO(ctypes.Structure):
    _fields_ = [("bmiHeader", BITMAPINFOHEADER), ("bmiColors", c_uint * 3)]


_cls = ctypes.create_unicode_buffer(256)
_rect = wt.RECT()
_pid = wt.DWORD()


def cls_of(h):
    user32.GetClassNameW(h, _cls, 256)
    return _cls.value


def stamp():
    return time.strftime("%H:%M:%S", time.localtime()) + ".%03d" % int((time.time() % 1) * 1000)


_pidmap = {}


def pd(pid):
    if pid in _pidmap:
        return _pidmap[pid]
    name = "pid=%d" % pid
    try:
        h = kernel32.OpenProcess(0x1000, False, pid)
        if h:
            buf = ctypes.create_unicode_buffer(512)
            n = c_uint(512)
            if kernel32.QueryFullProcessImageNameW(h, 0, buf, byref(n)):
                name = os.path.basename(buf.value) + "(%d)" % pid
            kernel32.CloseHandle(h)
    except Exception:
        pass
    _pidmap[pid] = name
    return name


def wrect(h):
    user32.GetWindowRect(h, byref(_rect))
    return (_rect.left, _rect.top, _rect.right, _rect.bottom)


def whow(h):
    user32.GetWindowThreadProcessId(h, byref(_pid))
    return pd(_pid.value)


def scan_browsers():
    found = []
    for cls in BROWSER:
        h = 0
        while True:
            h = user32.FindWindowExW(None, h, cls, None)
            if not h:
                break
            found.append((h, cls, whow(h), wrect(h), bool(user32.IsWindowVisible(h))))
    return found


def main():
    seconds = 0.0
    fire = None
    for a in sys.argv[1:]:
        if a.startswith("--fire="):
            fire = a.split("=", 1)[1]
        else:
            try:
                seconds = float(a)
            except ValueError:
                pass
    if seconds <= 0:
        seconds = 90.0
    sw = user32.GetSystemMetrics(0)
    sh = user32.GetSystemMetrics(1)
    W = (sw + K - 1) // K
    H = (sh + K - 1) // K

    hshell = user32.FindWindowW("Shell_TrayWnd", None)
    sid = wt.DWORD()
    if hshell:
        user32.GetWindowThreadProcessId(hshell, byref(sid))
    print("整屏 %dx%d  小图 %dx%d  shell 进程 = %s" % (sw, sh, W, H, pd(sid.value)))

    screen = user32.GetDC(None)

    def mk_dib(w, h):
        mem = gdi32.CreateCompatibleDC(screen)
        bi = BITMAPINFO()
        bi.bmiHeader.biSize = ctypes.sizeof(BITMAPINFOHEADER)
        bi.bmiHeader.biWidth = w
        bi.bmiHeader.biHeight = -h
        bi.bmiHeader.biPlanes = 1
        bi.bmiHeader.biBitCount = 32
        bi.bmiHeader.biCompression = 0
        p = c_void_p()
        d = gdi32.CreateDIBSection(mem, byref(bi), DIB_RGB_COLORS, byref(p), None, 0)
        gdi32.SelectObject(mem, d)
        return mem, d, p

    mem_s, dib_s, ppv_s = mk_dib(W, H)
    gdi32.SetStretchBltMode(mem_s, 3)
    mem_f, dib_f, ppv_f = mk_dib(sw, sh)
    nb_s, nb_f = W * H * 4, sw * sh * 4
    # 局部小 DIB：抓「右上角那一块」专用。整屏 BitBlt 要 ~100ms，采样根本跟不上；
    # 只抓 220x48 那一块 <1ms —— 这是能看清「三个按钮什么时候出现」的前提。
    mem_c, dib_c, ppv_c = mk_dib(CAPW, CAPH)

    def grab_small():
        gdi32.StretchBlt(mem_s, 0, 0, W, H, screen, 0, 0, sw, sh, SRCCOPY)
        return Image.frombuffer("RGBA", (W, H), ctypes.string_at(ppv_s, nb_s),
                                "raw", "BGRA", 0, 1)

    def grab_full():
        gdi32.BitBlt(mem_f, 0, 0, sw, sh, screen, 0, 0, SRCCOPY)
        return Image.frombuffer("RGBA", (sw, sh), ctypes.string_at(ppv_f, nb_f),
                                "raw", "BGRA", 0, 1)

    def shot(tag):
        try:
            grab_full().convert("RGB").save(os.path.join(SHOTS, tag + ".png"))
        except Exception as e:
            print("   存图失败", e)

    def shot_crop(tag, rect):
        """按屏幕坐标抓一小块存图（**局部 BitBlt**，<1ms）—— 整屏图上看不出右上角那三个按钮。"""
        try:
            x0, y0, x1, y1 = rect
            x0 = max(0, min(sw, x0)); y0 = max(0, min(sh, y0))
            x1 = max(0, min(sw, x1)); y1 = max(0, min(sh, y1))
            w = min(x1 - x0, CAPW)
            h = min(y1 - y0, CAPH)
            if w < 2 or h < 2:
                return
            gdi32.BitBlt(mem_c, 0, 0, w, h, screen, x0, y0, SRCCOPY)
            Image.frombuffer("RGBA", (CAPW, CAPH), ctypes.string_at(ppv_c, CAPW * CAPH * 4),
                             "raw", "BGRA", 0, 1).convert("RGB").crop((0, 0, w, h)).save(
                os.path.join(SHOTS, tag + ".png"))
        except Exception as e:
            print("   存图失败", e)

    def patch(x0, y0, x1, y1, n=8):
        """矩形内部 8x8 点的颜色指纹（GetPixel，一次两微秒）。"""
        if x1 <= x0 + 4 or y1 <= y0 + 4:
            return None
        vals = []
        for i in range(n):
            x = x0 + 4 + (x1 - x0 - 8) * i // max(1, n - 1)
            for j in range(n):
                y = y0 + 4 + (y1 - y0 - 8) * j // max(1, n - 1)
                vals.append(gdi32.GetPixel(screen, x, y))
        return tuple(vals)

    # ★ 非阻塞采样：**不在循环里阻塞**。上一版 `trace()` 一口气睡 2 秒，
    #   期间钩子事件（尤其 SHOW）明显被挤掉/丢了 —— 结果「我们自己的那扇窗」
    #   （`explorer /n,/separate`，pid 换了新进程的那个）CREATE/SHOW 的样式时间线整段没抓到。
    #   现在改成：登记一个监控目标，主循环每轮采一次（~2ms），谁也不堵。
    watch = {}          # hwnd -> 状态字典

    def add_watch(h, tag, cls0, name):
        print("%s  ★%s cab=0x%X cls=%s %s vis=%s rect=%s 样式=%s | %s ==纳入监控(%.0fs)==" %
              (stamp(), tag, int(h), cls0, name,
               "是" if user32.IsWindowVisible(h) else "否", wrect(h), exflags(h),
               stamp(), TRACE_SEC))
        if h in watch:
            return
        watch[h] = {"t0": time.time(), "pv": None, "ncap": 0}

    def sample_once(h, st):
        r = wrect(h)
        visx = bool(user32.IsWindowVisible(h))
        ex = exstyle(h)
        px = patch(*r)
        # 右上角那一小片：`SetWindowRgn` 只清客户区，DWM 画的非客户区
        #（标题栏 + 最小化/最大化/关闭三个按钮）照样在 ⇒ 这一路专门盯它。
        cx0 = max(0, r[2] - CAPW)
        cy0 = max(0, r[1])
        cappx = patch(cx0, cy0, r[2], cy0 + CAPH)
        key = (px, cappx, visx, r, ex)
        if key == st["pv"]:
            return
        pex = st["pv"][4] if st["pv"] else None
        st["pv"] = key
        note = ""
        if pex is not None and pex != ex:
            flags = []
            if (ex & WS_EX_LAYERED) and not (pex & WS_EX_LAYERED):
                flags.append("+LAYERED")
            elif (pex & WS_EX_LAYERED) and not (ex & WS_EX_LAYERED):
                flags.append("-LAYERED")
            if (ex & WS_EX_TOOLWINDOW) and not (pex & WS_EX_TOOLWINDOW):
                flags.append("+TOOLWIN")
            elif (pex & WS_EX_TOOLWINDOW) and not (ex & WS_EX_TOOLWINDOW):
                flags.append("-TOOLWIN")
            note = "(样式 " + " ".join(flags) + ")" if flags else "(样式变)"
        print("%s    t+%.0fms vis=%s 样式=%s%s rect=%s 客户区=%s 右上角=%s" %
              (stamp(), (time.time() - st["t0"]) * 1000, "是" if visx else "否",
               exflags(h), note, r,
               "空" if px is None else "%08X/%08X/%08X" % (px[0], px[27], px[63]),
               "空" if cappx is None else "%08X/%08X/%08X" % (cappx[0], cappx[27], cappx[63])))
        # 右上角那一小块**每一变都落**（局部 BitBlt <1ms）——「三个按钮有没有出现过」的唯一证据
        if st["ncap"] < 40:
            shot_crop("cap_%X_%02d" % (int(h), st["ncap"]), (cx0, cy0, r[2], cy0 + CAPH))
            st["ncap"] += 1

    if not os.path.isdir(SHOTS):
        os.makedirs(SHOTS)

    print("开始（%.0f 秒）—— 期间去做「从 wb 打开文件夹」就行" % seconds)
    t0 = time.time()
    prev_small = grab_small()
    prev_sb = prev_small.tobytes()
    seen = {}
    for (h, cls, name, rect, vis) in scan_browsers():
        seen[h] = (rect, vis)
        print("%s  基线浏览窗 cab=0x%X %s %s vis=%s" % (stamp(), int(h), cls, name, "是" if vis else "否"))

    # ---- 装 WinEvent 钩子（发现时机从 20ms 轮询升级成「创建/显示那一刻」）----
    cb_ref = WinEventProc(on_event)
    hook = user32.SetWinEventHook(0x0001, 0x8018, None,
                                  ctypes.cast(cb_ref, c_void_p), 0, 0, WINEVENT_OUTOFCONTEXT)
    print("%s  WinEvent 钩子 = 0x%X（0 = 失败）" % (stamp(), hook or 0))

    if fire:
        def _fire():
            time.sleep(3)
            try:
                r = ctypes.windll.shell32.ShellExecuteW(None, "open", fire, None, None, 1)
                print("%s  >>> 触发 ShellExecuteW('open', %s) 返回 %s"
                      % (stamp(), fire, int(r) if r else r))
            except Exception as e:
                print("%s  >>> 自触发失败（沙箱拦了？）：%s" % (stamp(), e))
        threading.Thread(target=_fire, daemon=True).start()

    frames = 0
    tick = 0
    while time.time() - t0 < seconds:
        pump()
        # ---- ① 钩子报来的事件：最早的发现机会 ----
        while _evq:
            ev, hwnd, tid, ts = _evq.popleft()
            if ev not in WATCH_EV or ev == 0x800B:
                continue
            c = cls_of(hwnd)
            user32.GetWindowThreadProcessId(hwnd, byref(_pid))
            pnum = _pid.value
            who = pd(pnum)
            if c not in BROWSER:
                # ★ 非浏览窗也要看一眼：用户报「任务栏没按钮」= 窗上有 WS_EX_TOOLWINDOW，
                #   而那两位**只有我们自己的窗**才打（`MakeTransparent(h,true)`）——
                #   也就是说「闪的可能根本不是 shell 那扇 CabinetWClass，而是我们自己起的窗」。
                #   只记「一出现就可见 + 够大 + 无属主」的，免得被菜单/提示框刷屏。
                if ev not in (0x8000, 0x8002):
                    continue
                if not user32.IsWindowVisible(hwnd):
                    continue
                r = wrect(hwnd)
                if (r[2] - r[0]) < 260 or (r[3] - r[1]) < 200:
                    continue
                if user32.GetWindow(hwnd, GW_OWNER) != 0:
                    continue
                print("%s   钩子 %-9s cab=0x%X cls=%s %s vis=是 样式=%s rect=%s" %
                      (stamp(), EVT[ev], int(hwnd), c, who, exflags(hwnd), r))
                add_watch(hwnd, "钩子" + EVT[ev], c, who)
                continue
            if ev == 0x8001:
                print("%s   钩子 DESTROY cab=0x%X %s" % (stamp(), int(hwnd), who))
                continue
            print("%s   钩子 %-9s cab=0x%X cls=%s %s vis=%s 样式=%s rect=%s" %
                  (stamp(), EVT[ev], int(hwnd), c, who,
                   "是" if user32.IsWindowVisible(hwnd) else "否", exflags(hwnd), wrect(hwnd)))
            if ev in (0x8000, 0x8002):
                add_watch(hwnd, "钩子" + EVT[ev], c, who)

        # ---- ② 轮询兜底（钩子万一漏了这条窗）----
        cur_win = {}
        events = []
        for (h, cls, name, rect, vis) in scan_browsers():
            cur_win[h] = (rect, vis)
            if h not in seen:
                events.append(("new", h, cls, name))
            elif seen[h] != (rect, vis) and (not seen[h][1]) and vis:
                events.append(("shown", h, cls, name))
        for h in list(seen):
            if h not in cur_win:
                print("%s   浏览窗 cab=0x%X 没了（关了/被收编了）" % (stamp(), int(h)))
        seen = cur_win
        for (kind, h, cls, name) in events:
            add_watch(h, "轮询" + ("新" if kind == "new" else "现身"), cls, name)

        # ---- ③ 采样所有监控目标（非阻塞：每轮采一次，谁也不堵）----
        now = time.time()
        for h in list(watch):
            st = watch[h]
            if not user32.IsWindow(h) or now - st["t0"] > TRACE_SEC:
                print("%s  == 监控结束 cab=0x%X ==" % (stamp(), int(h)))
                del watch[h]
                continue
            sample_once(h, st)

        # ---- ④ 整屏小图（背景帧率参考；降频到每 10 轮一次，省 CPU 好让它长跑）----
        tick += 1
        if tick % 10 == 0:
            cur_small = grab_small()
            sb = cur_small.tobytes()
            frames += 1
            if sb != prev_sb:
                dl = ImageChops.difference(cur_small, prev_small).convert("L")
                ch = sum(dl.histogram()[8:])
                if ch >= 6000:                  # 小图 384x240=92160 格；6000 格 ≈ 整屏 15 万像素
                    print("%s  大屏动 %6d 小图包围盒=%s" % (stamp(), ch, dl.getbbox()))
            prev_small = cur_small
            prev_sb = sb
        time.sleep(0.002)

    if hook:
        user32.UnhookWinEvent(hook)

    dt = time.time() - t0
    print("%s  结束（%d 帧，平均 %.0fms/帧）" % (stamp(), frames, dt * 1000 / max(1, frames)))
    gdi32.DeleteObject(dib_s)
    gdi32.DeleteObject(dib_f)
    gdi32.DeleteDC(mem_s)
    gdi32.DeleteDC(mem_f)
    user32.ReleaseDC(None, screen)


main()
