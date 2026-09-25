"""只读：量「shell 新开的那扇窗」从建窗到消失这段里，每条信息源**什么时候才有值**。

要回答的问题（决定「从开始菜单点文件夹还是会闪」怎么修）：窗口在 +495ms 左右就可见了，
而我们现在等到 +650ms 才读出路径 —— 那到底有没有一条源能更早给出路径？

每 10ms 采一次（ShellWindows 那条贵，30ms 一次并复用根对象 + 只扫队尾几项）：
  · 窗口标题（GetWindowTextW，跨进程但是系统消息，OS 封送，安全）
  · 该 hwnd 在 ShellWindows 里的 LocationURL —— **从队尾往前扫**，因为新项是追加的
  · 可见性 / 扩展样式
触发用 Shell.Application.Explore（shell 进程内开窗，跟开始菜单同路）。
"""
import ctypes
import ctypes.wintypes as wt
import sys
import time
from ctypes import POINTER, byref, c_void_p, c_uint, c_long, c_ushort, c_byte, c_int

ole32 = ctypes.windll.ole32
oleaut32 = ctypes.windll.oleaut32
user32 = ctypes.windll.user32

oleaut32.SysAllocString.restype = c_void_p
oleaut32.SysAllocString.argtypes = [ctypes.c_wchar_p]
user32.FindWindowW.restype = wt.HWND
user32.FindWindowW.argtypes = [wt.LPCWSTR, wt.LPCWSTR]
user32.GetClassNameW.argtypes = [wt.HWND, wt.LPWSTR, c_int]
user32.GetWindowThreadProcessId.argtypes = [wt.HWND, POINTER(wt.DWORD)]
user32.GetWindowTextW.argtypes = [wt.HWND, wt.LPWSTR, c_int]
user32.PostMessageW.argtypes = [wt.HWND, c_uint, ctypes.c_void_p, ctypes.c_void_p]
GetExStyle = user32.GetWindowLongPtrW
GetExStyle.argtypes = [wt.HWND, c_int]
GetExStyle.restype = ctypes.c_longlong
# 「绘制区空 / 窗口在屏外」时，屏幕上其实什么都画不出来 —— 见 shell_blank_probe.py
user32.GetWindowRgn.argtypes = [wt.HWND, c_void_p]
user32.GetWindowRgn.restype = c_int
user32.GetWindowRect.argtypes = [wt.HWND, ctypes.POINTER(wt.RECT)]
user32.GetAncestor.argtypes = [wt.HWND, c_uint]
user32.GetAncestor.restype = wt.HWND
user32.GetSystemMetrics.argtypes = [c_int]
user32.SetWindowRgn.argtypes = [wt.HWND, c_void_p, wt.BOOL]
user32.SetWindowRgn.restype = c_int
user32.WindowFromPoint.argtypes = [wt.POINT]
user32.WindowFromPoint.restype = wt.HWND
user32.SetWindowPos.argtypes = [wt.HWND, wt.HWND, c_int, c_int, c_int, c_int, c_uint]
user32.SetWindowPos.restype = wt.BOOL
SWP_NOSIZE, SWP_NOMOVE, SWP_NOACTIVATE = 0x0001, 0x0002, 0x0010
NULLREGION = 1
BLANK = "--blank" in sys.argv          # 实验组：一看见目标窗就把它置顶 + 拍一下空绘制区
TOPMOST = BLANK or "--topmost" in sys.argv
blank_at = [None]                      # 计划给它设空绘制区的时刻
blanked = [False]


def on_screen(h):
    """窗口矩形跟虚拟桌面有交集吗。"""
    r = wt.RECT()
    user32.GetWindowRect(h, ctypes.byref(r))
    sl, st = user32.GetSystemMetrics(76), user32.GetSystemMetrics(77)
    sw, sh = user32.GetSystemMetrics(78), user32.GetSystemMetrics(79)
    return not (r.right <= sl or r.bottom <= st or r.left >= sl + sw or r.top >= st + sh)


GRAB = "--grab" in sys.argv
gdi32 = ctypes.windll.gdi32
user32.GetDC.argtypes = [wt.HWND]
user32.GetDC.restype = ctypes.c_void_p
user32.ReleaseDC.argtypes = [wt.HWND, ctypes.c_void_p]
gdi32.DeleteDC.argtypes = [ctypes.c_void_p]
gdi32.CreateCompatibleDC.argtypes = [ctypes.c_void_p]
gdi32.CreateCompatibleDC.restype = ctypes.c_void_p
gdi32.CreateCompatibleBitmap.argtypes = [ctypes.c_void_p, c_int, c_int]
gdi32.CreateCompatibleBitmap.restype = ctypes.c_void_p
gdi32.SelectObject.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
gdi32.SelectObject.restype = ctypes.c_void_p
gdi32.BitBlt.argtypes = [ctypes.c_void_p, c_int, c_int, c_int, c_int, ctypes.c_void_p,
                         c_int, c_int, ctypes.c_uint]
gdi32.BitBlt.restype = wt.BOOL
gdi32.DeleteObject.argtypes = [ctypes.c_void_p]
gdi32.GetDIBits.argtypes = [ctypes.c_void_p, ctypes.c_void_p, c_uint, c_uint,
                            ctypes.c_void_p, ctypes.c_void_p, c_uint]
gdi32.GetDIBits.restype = c_int


class _BIH(ctypes.Structure):
    _fields_ = [("biSize", ctypes.c_uint32), ("biWidth", ctypes.c_int32),
                ("biHeight", ctypes.c_int32), ("biPlanes", ctypes.c_uint16),
                ("biBitCount", ctypes.c_uint16), ("biCompression", ctypes.c_uint32),
                ("biSizeImage", ctypes.c_uint32), ("biXPelsPerMeter", ctypes.c_int32),
                ("biYPelsPerMeter", ctypes.c_int32), ("biClrUsed", ctypes.c_uint32),
                ("biClrImportant", ctypes.c_uint32)]


def grab_screen(h):
    """把窗口矩形那块**屏幕**抓下来（BitBlt，不用 PIL）。回来的是 bytes。"""
    r = wt.RECT()
    user32.GetWindowRect(h, ctypes.byref(r))
    w, ht = r.right - r.left, r.bottom - r.top
    if w <= 0 or ht <= 0 or w * ht > 40000000:
        return None
    hdc = user32.GetDC(None)
    mem = gdi32.CreateCompatibleDC(hdc)
    bmp = gdi32.CreateCompatibleBitmap(hdc, w, ht)
    gdi32.SelectObject(mem, bmp)
    gdi32.BitBlt(mem, 0, 0, w, ht, hdc, r.left, r.top, 0x00CC0020)      # SRCCOPY
    bi = _BIH()
    bi.biSize = ctypes.sizeof(_BIH)
    bi.biWidth = w
    bi.biHeight = -ht
    bi.biPlanes = 1
    bi.biBitCount = 32
    buf = ctypes.create_string_buffer(w * ht * 4)
    gdi32.GetDIBits(mem, bmp, 0, ht, buf, ctypes.byref(bi), 0)
    gdi32.DeleteObject(bmp)
    gdi32.DeleteDC(mem)
    user32.ReleaseDC(None, hdc)
    return buf.raw


def diff(a, b):
    """两张图差多少（每 997 个字节抽一个采样，省时间）。"""
    if not a or not b or len(a) != len(b):
        return -1
    n = 0
    for i in range(0, len(a), 997 * 4):
        if a[i:i + 4] != b[i:i + 4]:
            n += 1
    return n


def hit_test(h):
    """窗口中心那个点，现在“属于”目标窗口吗 —— 用来看它到底有没有占着那块屏幕。

    ⚠ `WindowFromPoint` 回来的是**那个点上的最深子窗**（explorer 的 DirectUIHWND 之类），
      所以要拿 `GetAncestor(.., GA_ROOT)` 找回顶层再比。
    ⚠ 它**不理会窗口的绘制区**（只按矩形/hit-test），所以这个判据能看出窗口占没占着屏幕，
      但看不出它画没画东西 —— 想验证空绘制区有没有用，得看它到底变不变。
    """
    r = wt.RECT()
    user32.GetWindowRect(h, ctypes.byref(r))
    pt = wt.POINT((r.left + r.right) // 2, (r.top + r.bottom) // 2)
    hit = user32.WindowFromPoint(pt)
    if not hit:
        return 0
    root = user32.GetAncestor(hit, 2)          # GA_ROOT
    return root or hit


COINIT_APARTMENTTHREADED = 0x2
CLSCTX_INPROC_SERVER = 0x1
CLSCTX_LOCAL_SERVER = 0x4
CLSCTX_ALL = 0x17
DISPATCH_METHOD = 0x1
DISPATCH_PROPERTYGET = 0x2
LOCALE_USER_DEFAULT = 0x400
VT_I4, VT_BSTR, VT_DISPATCH, VT_VARIANT = 3, 8, 9, 12
VT_BYREF = 0x4000
GWL_EXSTYLE = -20
WS_EX_TOOLWINDOW = 0x80
WS_EX_APPWINDOW = 0x40000
WS_EX_LAYERED = 0x80000


class GUID(ctypes.Structure):
    _fields_ = [("d1", ctypes.c_ulong), ("d2", c_ushort), ("d3", c_ushort), ("d4", c_byte * 8)]


def guid(s):
    g = GUID()
    ole32.CLSIDFromString(ctypes.c_wchar_p(s), byref(g))
    return g


class VARIANT(ctypes.Structure):
    class _U(ctypes.Union):
        _fields_ = [("llVal", ctypes.c_longlong), ("lVal", c_int), ("bstrVal", c_void_p),
                    ("pdispVal", c_void_p), ("pad", c_byte * 16)]
    _fields_ = [("vt", c_ushort), ("r1", c_ushort), ("r2", c_ushort), ("r3", c_ushort), ("u", _U)]


class DISPPARAMS(ctypes.Structure):
    _fields_ = [("rgvarg", c_void_p), ("rgdispidNamedArgs", c_void_p),
                ("cArgs", c_uint), ("cNamedArgs", c_uint)]


class EXCEPINFO(ctypes.Structure):
    _fields_ = [("wCode", c_ushort), ("wReserved", c_ushort), ("bstrSource", c_void_p),
                ("bstrDescription", c_void_p), ("bstrHelpFile", c_void_p),
                ("dwHelpContext", ctypes.c_ulong), ("pvReserved", c_void_p),
                ("pfnDeferredFillIn", c_void_p), ("scode", c_int)]


def vcall(p, index, restype, *at):
    vt = ctypes.cast(p, POINTER(c_void_p))[0]
    return ctypes.cast(ctypes.cast(vt, POINTER(c_void_p))[index],
                       ctypes.WINFUNCTYPE(restype, c_void_p, *at))


def dispid(p, name):
    iid = GUID()
    d = c_int()
    nm = ctypes.c_wchar_p(name)
    hr = vcall(p, 5, ctypes.c_long, POINTER(GUID), POINTER(ctypes.c_wchar_p), c_uint,
               ctypes.c_ulong, POINTER(c_int))(p, byref(iid), byref(nm), 1,
                                               LOCALE_USER_DEFAULT, byref(d))
    return hr, d.value


def invoke_raw(p, did, flags, rgv, n):
    dp = DISPPARAMS()
    dp.rgvarg = rgv
    dp.cArgs = n
    res = VARIANT()
    ei = EXCEPINFO()
    err = c_uint()
    iid = GUID()
    hr = vcall(p, 6, ctypes.c_long, c_int, POINTER(GUID), ctypes.c_ulong, c_ushort,
               POINTER(DISPPARAMS), POINTER(VARIANT), POINTER(EXCEPINFO), POINTER(c_uint))(
        p, did, byref(iid), LOCALE_USER_DEFAULT, flags, byref(dp), byref(res), byref(ei), byref(err))
    return hr, res


def vstr(s):
    v = VARIANT()
    v.vt = VT_BSTR
    v.u.bstrVal = oleaut32.SysAllocString(s)
    return v


def shell_pid():
    h = user32.FindWindowW("Shell_TrayWnd", None)
    pid = wt.DWORD()
    user32.GetWindowThreadProcessId(h, byref(pid))
    return pid.value


def open_in_shell(path):
    """让 shell 自己开一个文件夹窗（= 开始菜单那条路）。"""
    clsid = guid("{13709620-C279-11CE-A49E-444553540000}")
    iid = guid("{00020400-0000-0000-C000-000000000046}")
    p = c_void_p()
    hr = ole32.CoCreateInstance(byref(clsid), None,
                                CLSCTX_LOCAL_SERVER | CLSCTX_INPROC_SERVER,
                                byref(iid), byref(p))
    if hr != 0:
        print("  CoCreateInstance(Shell.Application) hr=0x%08X" % (hr & 0xFFFFFFFF))
        return
    win = path.replace('/', '\\')
    _, did = dispid(p.value, "Explore")
    t = time.time()
    hr2, _ = invoke_raw(p.value, did, DISPATCH_METHOD,
                        ctypes.cast((VARIANT * 1)(vstr(win)), c_void_p).value, 1)
    print("  Explore(%s) hr=0x%08X（%0.0fms）" % (win, hr2 & 0xFFFFFFFF, (time.time() - t) * 1000))


CPID = shell_pid()
path = sys.argv[1] if len(sys.argv) > 1 else r"D:\Dev\!tmp"
duration = float(sys.argv[2]) if len(sys.argv) > 2 else 8.0
print("shell pid=%d  目标=%s" % (CPID, path))

# ---- 打开 ShellWindows 根对象（复用，别每次重建）----
ole32.CoInitializeEx(None, COINIT_APARTMENTTHREADED)
t0 = time.time()
cl = guid("{9BA05972-F6A8-11CF-A442-00A0C90A8F39}")
iid = guid("{00020400-0000-0000-C000-000000000046}")
shw = c_void_p()
hr = ole32.CoCreateInstance(byref(cl), None, CLSCTX_ALL, byref(iid), byref(shw))
print("CoCreateInstance(ShellWindows, CLSCTX_ALL) hr=0x%08X（%0.1fms）"
      % (hr & 0xFFFFFFFF, (time.time() - t0) * 1000))
_, did_count = dispid(shw.value, "Count")
_, did_item = dispid(shw.value, "Item")
inner = VARIANT()
inner.vt = VT_I4
outer = VARIANT()
outer.vt = VT_VARIANT | VT_BYREF
outer.u.pdispVal = ctypes.cast(byref(inner), c_void_p).value
argbuf = (VARIANT * 1)()
argbuf[0] = outer
ARGP = ctypes.cast(argbuf, c_void_p).value

HWND_DISP = {}
URL_DISP = {}


def shw_url_for(hwnd, lookback=6):
    """从队尾往前扫 lookback 项，找 HWND == hwnd 的那项，返回它的 LocationURL。"""
    hr, cnt = invoke_raw(shw.value, did_count, DISPATCH_PROPERTYGET, None, 0)
    if hr != 0:
        return None, None
    total = cnt.u.lVal
    seen = 0
    for k in range(total - 1, max(-1, total - 1 - lookback), -1):
        inner.u.lVal = k
        hr2, res = invoke_raw(shw.value, did_item, DISPATCH_METHOD, ARGP, 1)
        if hr2 != 0 or res.vt != VT_DISPATCH or not res.u.pdispVal:
            continue
        q = res.u.pdispVal
        if q not in HWND_DISP:
            HWND_DISP[q], d = dispid(q, "HWND")
            URL_DISP[q], _ = dispid(q, "LocationURL")
        hr3, r3 = invoke_raw(q, HWND_DISP[q], DISPATCH_PROPERTYGET, None, 0)
        hv = r3.u.llVal if r3.vt == 20 else (r3.u.lVal if r3.vt == 3 else None)
        if hv != hwnd:
            continue
        hr4, r4 = invoke_raw(q, URL_DISP[q], DISPATCH_PROPERTYGET, None, 0)
        u = ctypes.wstring_at(r4.u.bstrVal) if (r4.vt == VT_BSTR and r4.u.bstrVal) else ""
        return (u or None), total
    return None, total


ENUMPROC = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
user32.EnumWindows.argtypes = [ENUMPROC, wt.LPARAM]


def cabs():
    out = []

    def cb(h, _):
        b = ctypes.create_unicode_buffer(64)
        user32.GetClassNameW(h, b, 64)
        if b.value in ("CabinetWClass", "ExploreWClass"):
            p = wt.DWORD()
            user32.GetWindowThreadProcessId(h, byref(p))
            out.append((h, p.value))
        return True

    user32.EnumWindows(ENUMPROC(cb), 0)
    return out


base = set(h for h, _ in cabs())
print("触发前 %d 扇 CabinetWClass" % len(base))

target = [None]
fired = False
t0 = time.time()
marks = {}
hollow_bad = [0]        # 「可见 且 真占着屏幕」的采样数 —— 这就是用户看到的那一下
hit_state = [None]
grabbed = [False]
title_hist = []
last_shw_poll = 0.0

while time.time() - t0 < duration:
    el = time.time() - t0
    if not fired and el >= 1.0:
        fired = True
        print("%7.3fs 触发" % el)
        open_in_shell(path)
    if fired:
        now = time.time() - t0
        for h, pid in cabs():
            if h in base or pid != CPID:
                continue
            if target[0] is None:
                target[0] = h
                print("%7.3fs ★ 目标窗口 hwnd=0x%X" % (now, h))
                if TOPMOST:
                    user32.SetWindowPos(h, wt.HWND(-1), 0, 0, 0, 0,
                                        SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE)
                    print("%7.3fs ☞ 把它置顶（只为量得准，不抢前台）" % now)
            if h != target[0]:
                continue
            vis = bool(user32.IsWindowVisible(h))
            ex = GetExStyle(h, GWL_EXSTYLE) & 0xFFFFFFFF
            if BLANK and not blanked[0]:
                blanked[0] = True
                gg = ctypes.windll.gdi32.CreateRectRgn(0, 0, 0, 0)
                ok = user32.SetWindowRgn(h, gg, True)
                print("%7.3fs ☆ 实验：给它设了空绘制区（SetWindowRgn=%d）" % (now, ok))
            tb = ctypes.create_unicode_buffer(512)
            user32.GetWindowTextW(h, tb, 512)
            if tb.value != (title_hist[-1][1] if title_hist else None):
                title_hist.append((now, tb.value))
                if not vis:
                    print("%7.3fs   标题=\"%s\"（还没可见）" % (now, tb.value))
            if "vis" not in marks and vis:
                marks["vis"] = now
                print("%7.3fs ● 首次可见（ex=0x%08X 标题=\"%s\"）" % (now, ex, tb.value))
            # 可见 ≠ 会露脸：绘制区被清空 / 窗口挪到了屏外时，屏幕上其实什么都不画（我们现在的做法）。
            # 只有「可见 + 有绘制区 + 跟屏幕有交集」才是用户真能看见的那一下。
            if GRAB and vis and not grabbed[0]:
                grabbed[0] = True
                print("%7.3fs ==== 空绘制区到底管不管用：抓屏幕比对 ====" % now)
                user32.SetWindowPos(h, wt.HWND(-1), 0, 0, 0, 0,
                                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE)
                time.sleep(0.30)
                a1 = grab_screen(h)
                time.sleep(0.15)
                a2 = grab_screen(h)
                gg = ctypes.windll.gdi32.CreateRectRgn(0, 0, 0, 0)
                ok = user32.SetWindowRgn(h, gg, True)
                time.sleep(0.15)
                b1 = grab_screen(h)
                user32.SetWindowRgn(h, None, True)
                time.sleep(0.15)
                c1 = grab_screen(h)
                print("  基线（不动它，前后两张屏幕）：%d 处不同" % diff(a1, a2))
                print("  设了空绘制区之后        ：%d 处不同（SetWindowRgn=%d）" % (diff(a2, b1), ok))
                print("  再撤掉绘制区            ：%d 处不同" % diff(b1, c1))
                print("  ⇒ 「设了差得多、撤掉又变回来」= 绘制区真的把这块屏幕让出去了")
            if vis:
                onscr = on_screen(h)
                hit = hit_test(h) == h
                if hit_state[0] is None:
                    hit_state[0] = hit
                    print("%7.3fs 点中测试（%s）：%s" % (now, "刚露脸" if hit else "已经被盖住/没占着",
                                                        "是" if hit else "否"))
                elif hit != hit_state[0]:
                    hit_state[0] = hit
                    print("%7.3fs 点中测试变成：%s" % (now, "是" if hit else "否"))
                if not hit or not onscr:
                    if "hollow" not in marks:
                        marks["hollow"] = now
                        print("%7.3fs ● 首次可见（点中=%s 在屏内=%s ⇒ 它没占着那块屏幕）"
                              % (now, hit, onscr))
                else:
                    hollow_bad[0] += 1
                    if "paint" not in marks:
                        marks["paint"] = now
                        print("%7.3fs ★★ 首次「真占着屏幕」的可见（点中=%s 在屏内=%s）"
                              % (now, hit, onscr))
            # ShellWindows 那条贵：30ms 一次
            if now - last_shw_poll >= 0.03:
                last_shw_poll = now
                u, total = shw_url_for(h)
                if u and "url" not in marks:
                    marks["url"] = now
                    print("%7.3fs ◆ ShellWindows 首次给出 LocationURL = %s（清单 %d 项，可见=%s）"
                          % (now, u, total, vis))
            if not vis and "vis" in marks and "gone" not in marks:
                marks["gone"] = now
                print("%7.3fs ○ 已不可见" % now)
    time.sleep(0.01)

print("\n-- 汇总 --")
if marks.get("vis") is not None:
    print("首次可见            +%.0fms" % (marks["vis"] * 1000))
if marks.get("hollow") is not None:
    print("首次可见（没占着屏幕）      +%.0fms" % (marks["hollow"] * 1000))
if marks.get("paint") is not None:
    print("★★ 首次真占着屏幕的可见    +%.0fms（之后还有 %d 次采样占着）"
          % (marks["paint"] * 1000, hollow_bad[0] - 1))
else:
    print("★ 全程没有「真占着屏幕」的可见 —— 屏幕上一下也没露（想要的结果）")
if marks.get("url") is not None:
    print("首个 LocationURL      +%.0fms" % (marks["url"] * 1000))
print("标题变化历史：", [(round(t * 1000), s) for t, s in title_hist[:8]])

# 收尾：这扇窗是我们（探针）招来的，采样完像用户点 × 一样关掉它 —— 别在桌面上留垃圾。
if target[0] is not None and user32.IsWindow(target[0]):
    user32.PostMessageW(target[0], 0x0112, 0xF060, 0)     # WM_SYSCOMMAND / SC_CLOSE
    print("已请求关闭 hwnd=0x%X" % target[0])
