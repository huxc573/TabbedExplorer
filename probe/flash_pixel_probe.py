"""只读：**用屏幕像素**判「从开始菜单点文件夹，那扇原生窗到底有没有在屏幕上露过脸」。

为什么不能只看 `IsWindowVisible`（实测踩到）：窗口「可见」不等于「画出来了」——
绘制区被清空（`SetWindowRgn(h, 空区域)`）时它照样「可见」，但一个像素都不画。
反过来，`WindowFromPoint` 也判不出来：我们自己的容器窗正好压在它上面，点中测试恒为「否」。

所以直接看像素：盯着那扇窗的矩形那块屏幕，
  · 采一张**基线**（它还没可见时）；
  · 从它可见起每 ~20ms 采一张，跟基线比，记最大差异；
  · 它消失后再采一张，跟基线比 —— 应该回到 0 附近。
判据（实测标定）：拿掉空绘制区时「刚可见前 50ms」同一位置差 **489** 处 = 真闪；
      设了空绘制区只有 **16** 处（噪声）= 不闪。阈值取 60。
      干扰项两处，都踩过：
        · 采样矩形必须**冻结在它还没可见那一刻** —— 它显示时会往上挪 11px，重读就成「拿两块屏幕比」；
        · 别整块窗口比，只比**最左边 200px 那条**（我们的容器窗从 x=540 起，这条归它独有），
          否则「我们把容器窗顶到前台 / 标签条换选中项」也会被算成差异。

用法: python flash_pixel_probe.py [目标路径] [观察秒数]
      默认 D:\\Users\\a\\Desktop（= 从开始菜单点「桌面」那条路）
"""
import ctypes
import ctypes.wintypes as wt
import sys
import time
from ctypes import POINTER, byref, c_void_p, c_uint, c_int, c_byte, c_ushort, c_ulong, c_longlong

ole32 = ctypes.windll.ole32
oleaut32 = ctypes.windll.oleaut32
user32 = ctypes.windll.user32
gdi32 = ctypes.windll.gdi32

oleaut32.SysAllocString.restype = c_void_p
oleaut32.SysAllocString.argtypes = [ctypes.c_wchar_p]
user32.FindWindowW.restype = wt.HWND
user32.FindWindowW.argtypes = [wt.LPCWSTR, wt.LPCWSTR]
user32.GetClassNameW.argtypes = [wt.HWND, wt.LPWSTR, c_int]
user32.GetWindowThreadProcessId.argtypes = [wt.HWND, POINTER(wt.DWORD)]
user32.GetWindowRect.argtypes = [wt.HWND, POINTER(wt.RECT)]
user32.IsWindowVisible.argtypes = [wt.HWND]
user32.IsWindowVisible.restype = wt.BOOL
user32.GetDC.argtypes = [wt.HWND]
user32.GetDC.restype = c_void_p
user32.ReleaseDC.argtypes = [wt.HWND, c_void_p]
gdi32.DeleteDC.argtypes = [c_void_p]
gdi32.CreateCompatibleDC.argtypes = [c_void_p]
gdi32.CreateCompatibleDC.restype = c_void_p
gdi32.CreateCompatibleBitmap.argtypes = [c_void_p, c_int, c_int]
gdi32.CreateCompatibleBitmap.restype = c_void_p
gdi32.SelectObject.argtypes = [c_void_p, c_void_p]
gdi32.SelectObject.restype = c_void_p
gdi32.BitBlt.argtypes = [c_void_p, c_int, c_int, c_int, c_int, c_void_p, c_int, c_int, c_uint]
gdi32.BitBlt.restype = wt.BOOL
gdi32.DeleteObject.argtypes = [c_void_p]
gdi32.CreateRectRgn.argtypes = [c_int, c_int, c_int, c_int]
gdi32.CreateRectRgn.restype = c_void_p
user32.SetWindowRgn.argtypes = [wt.HWND, c_void_p, wt.BOOL]
user32.SetWindowRgn.restype = c_int
gdi32.GetDIBits.argtypes = [c_void_p, c_void_p, c_uint, c_uint, c_void_p, c_void_p, c_uint]
gdi32.GetDIBits.restype = c_int

COINIT_APARTMENTTHREADED = 0x2
CLSCTX_INPROC_SERVER, CLSCTX_LOCAL_SERVER = 0x1, 0x4
DISPATCH_METHOD = 0x1
LOCALE_USER_DEFAULT = 0x400
VT_I4, VT_BSTR, VT_DISPATCH, VT_VARIANT, VT_BYREF = 3, 8, 9, 12, 0x4000


class GUID(ctypes.Structure):
    _fields_ = [("d1", ctypes.c_ulong), ("d2", ctypes.c_ushort),
                ("d3", ctypes.c_ushort), ("d4", ctypes.c_byte * 8)]


class VARIANT(ctypes.Structure):
    class _U(ctypes.Union):
        _fields_ = [("llVal", ctypes.c_longlong), ("lVal", c_int), ("bstrVal", c_void_p),
                    ("pdispVal", c_void_p), ("pad", c_byte * 16)]
    _fields_ = [("vt", ctypes.c_ushort), ("r1", ctypes.c_ushort), ("r2", ctypes.c_ushort),
                ("r3", ctypes.c_ushort), ("u", _U)]


class DISPPARAMS(ctypes.Structure):
    _fields_ = [("rgvarg", c_void_p), ("rgdispidNamedArgs", c_void_p),
                ("cArgs", c_uint), ("cNamedArgs", c_uint)]


class EXCEPINFO(ctypes.Structure):
    _fields_ = [("wCode", ctypes.c_ushort), ("wReserved", ctypes.c_ushort), ("bstrSource", c_void_p),
                ("bstrDescription", c_void_p), ("bstrHelpFile", c_void_p),
                ("dwHelpContext", ctypes.c_ulong), ("pvReserved", c_void_p),
                ("pfnDeferredFillIn", c_void_p), ("scode", c_int)]


class _BIH(ctypes.Structure):
    _fields_ = [("biSize", ctypes.c_uint32), ("biWidth", ctypes.c_int32),
                ("biHeight", ctypes.c_int32), ("biPlanes", ctypes.c_uint16),
                ("biBitCount", ctypes.c_uint16), ("biCompression", ctypes.c_uint32),
                ("biSizeImage", ctypes.c_uint32), ("biXPelsPerMeter", ctypes.c_int32),
                ("biYPelsPerMeter", ctypes.c_int32), ("biClrUsed", ctypes.c_uint32),
                ("biClrImportant", ctypes.c_uint32)]


def guid(s):
    g = GUID()
    ole32.CLSIDFromString(ctypes.c_wchar_p(s), byref(g))
    return g


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
    return vcall(p, 6, ctypes.c_long, c_int, POINTER(GUID), ctypes.c_ulong, c_ushort,
                 POINTER(DISPPARAMS), POINTER(VARIANT), POINTER(EXCEPINFO), POINTER(c_uint))(
        p, did, byref(iid), LOCALE_USER_DEFAULT, flags, byref(dp), byref(res), byref(ei), byref(err)), res


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
    clsid = guid("{13709620-C279-11CE-A49E-444553540000}")
    iid = guid("{00020400-0000-0000-C000-000000000046}")
    p = c_void_p()
    hr = ole32.CoCreateInstance(byref(clsid), None,
                                CLSCTX_LOCAL_SERVER | CLSCTX_INPROC_SERVER, byref(iid), byref(p))
    if hr != 0:
        print("  CoCreateInstance(Shell.Application) hr=0x%08X" % (hr & 0xFFFFFFFF))
        return
    _, did = dispid(p.value, "Explore")
    hr2, _ = invoke_raw(p.value, did, DISPATCH_METHOD,
                        ctypes.cast((VARIANT * 1)(vstr(path.replace('/', '\\'))), c_void_p).value, 1)
    print("  Explore hr=0x%08X" % (hr2 & 0xFFFFFFFF))


BUFS = {}


# 只盯那扇 shell 窗**最左边这一条**：它跟我们的容器窗重叠，整块比会把
# 「我们把窗口顶到前台 / 标签条换了选中项」也算成差异 —— 那是我们自己的动作，不是它露脸。
# 实测几何：shell 窗 245,337-1845,1211，我们的容器窗 540,353-1800,1140 ⇒ 左边这 290px 归它独有。
STRIP = 200


# ⚠ 采样矩形**冻结在「它还没可见」那一刻**，之后一直按那个屏幕矩形取 —— 别每帧重读
#   `GetWindowRect`：实测这扇窗在显示那一刻会**往上挪 11px**（337 → 326），重读就成了
#   「拿两块不同的屏幕比」，明明什么都没画也会报出百来处差异（踩过这个坑）。
FROZEN = [None]


def grab(h, w, ht):
    """抓（冻结的）那块屏幕左边缘 STRIP 宽的一条。"""
    w = min(STRIP, w)
    if w <= 0 or ht <= 0:
        return None
    hdc = user32.GetDC(None)
    mem = gdi32.CreateCompatibleDC(hdc)
    bmp = gdi32.CreateCompatibleBitmap(hdc, w, ht)
    gdi32.SelectObject(mem, bmp)
    if FROZEN[0] is not None:
        left, top = FROZEN[0][0], FROZEN[0][1]
    else:
        r = wt.RECT()
        user32.GetWindowRect(h, byref(r))
        left, top = r.left, r.top
    gdi32.BitBlt(mem, 0, 0, w, ht, hdc, left, top, 0x00CC0020)
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


def diff(a, b, step=97):
    """抽样比两张图（每 step 个像素取一个）。"""
    if not a or not b or len(a) != len(b):
        return -1
    n = 0
    for i in range(0, len(a), step * 4):
        if a[i:i + 4] != b[i:i + 4]:
            n += 1
    return n


def diff_bbox(a, b, w, step=97):
    """差异都落在哪块（屏幕像素坐标，相对图左上角）—— 判「闪的是标题栏还是内容区」。"""
    if not a or not b or len(a) != len(b) or w <= 0:
        return None
    mnx = mny = 10 ** 9
    mxx = mxy = -1
    for i in range(0, len(a), step * 4):
        if a[i:i + 4] != b[i:i + 4]:
            px = (i // 4) % w
            py = (i // 4) // w
            mnx = min(mnx, px); mxx = max(mxx, px)
            mny = min(mny, py); mxy = max(mxy, py)
    return None if mxx < 0 else (mnx, mny, mxx, mxy)


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


CPID = shell_pid()
path = sys.argv[1] if len(sys.argv) > 1 else r"D:\Users\a\Desktop"
duration = float(sys.argv[2]) if len(sys.argv) > 2 else 10.0
print("shell pid=%d  目标=%s" % (CPID, path))

ole32.CoInitializeEx(None, COINIT_APARTMENTTHREADED)
base = set(h for h, _ in cabs())
print("触发前 %d 扇 CabinetWClass" % len(base))

BLANK = "--blank" in sys.argv          # 实验组 A：每一轮都重压一遍空绘制区（对付 explorer 自己抹掉）
BLANK_ONCE = "--blank-once" in sys.argv  # 实验组 B：只在「找到窗」那一刻压一次（= 程序现在的做法）


def set_blank(h):
    """给窗口设一个**空绘制区** —— 跟 `EmbedApi.MakeBlank` 一样。返回区域句柄（收尾要删）。"""
    gg = gdi32.CreateRectRgn(0, 0, 0, 0)
    ok = user32.SetWindowRgn(h, gg, True)
    print("    ☐ 已设空绘制区 SetWindowRgn=%d" % ok)
    return gg


target = [None]
ref = [None]
rect0 = [None]
worst = [0]
worst_at = [0.0]
fired = False
vis_since = [None]
samples = [0]
rgn = [None]
early = [0]        # 只统计「刚可见那 50ms」里的最大差异 —— 程序的做法是 SHOW 后 32ms 内就把它按住，
early_at = [0.0]  # 所以真正决定「闪不闪」的就是这一段（后面的不管，那时它早被关掉了）
early_bb = [None]
t0 = time.time()
last_sample = 0.0

while time.time() - t0 < duration:
    el = time.time() - t0
    if not fired and el >= 1.0:
        fired = True
        print("%7.3fs 触发" % el)
        open_in_shell(path)
    if fired:
        now = time.time() - t0
        if target[0] is None:
            for h, pid in cabs():
                if h in base or pid != CPID:
                    continue
                target[0] = h
                r = wt.RECT()
                user32.GetWindowRect(h, byref(r))
                print("%7.3fs ★ 目标窗口 hwnd=0x%X 矩形 %d,%d-%d,%d（还没可见）"
                      % (now, h, r.left, r.top, r.right, r.bottom))
                # 它还没可见 —— 先把这块屏幕记下来当作基线
                rect0[0] = (r.left, r.top, r.right, r.bottom)
                FROZEN[0] = rect0[0]
                ref[0] = grab(h, r.right - r.left, r.bottom - r.top)
                if BLANK or BLANK_ONCE:
                    rgn[0] = set_blank(h)
        else:
            h = target[0]
            if user32.IsWindow(target[0]):
                r = wt.RECT()
                user32.GetWindowRect(h, byref(r))
                w, ht = r.right - r.left, r.bottom - r.top
                vis = bool(user32.IsWindowVisible(h))
                if vis and vis_since[0] is None:
                    vis_since[0] = now
                    print("%7.3fs ● 可见了（矩形 %d,%d-%d,%d；基线时是 %s）—— 开始逐帧比像素"
                          % (now, r.left, r.top, r.right, r.bottom, rect0[0]))
                # 实验组：每一轮都重新压一遍空绘制区 —— 探「explorer 建窗过程中会不会把它自己抹掉」
                if BLANK and rgn[0] is None:
                    rgn[0] = set_blank(h)
                elif BLANK:
                    user32.SetWindowRgn(h, rgn[0], True)
                # 每 20ms 抽一帧：绘制区被清空时应该跟基线一模一样
                if vis and w > 0 and ht > 0 and now - last_sample >= 0.02:
                    last_sample = now
                    cur = grab(h, w, ht)
                    d = diff(ref[0], cur)
                    samples[0] += 1
                    if d > worst[0]:
                        worst[0] = d
                        worst_at[0] = now
                    if vis_since[0] is not None and now - vis_since[0] < 0.05 and d > early[0]:
                        early[0] = d
                        early_at[0] = now - vis_since[0]
                        early_bb[0] = diff_bbox(ref[0], cur, min(STRIP, w))
                    if d > 60:
                        bb = diff_bbox(ref[0], cur, min(STRIP, w))
                        print("%7.3fs ★★ 这块屏幕被重画了（%d 处不同，集中在 x%d..%d y%d..%d）—— 就是「闪一下」"
                              % (now, d, bb[0], bb[2], bb[1], bb[3]))
                        early_bb[0] = diff_bbox(ref[0], cur, min(STRIP, w))
                    if d > 60:
                        print("%7.3fs ★★ 这块屏幕被重画了（%d 处不同）—— 就是「闪一下」"
                              % (now, d))
            else:
                print("%7.3fs ○ 窗口没了（总共比了 %d 帧）" % (now, samples[0]))
                break
    time.sleep(0.005)

print("\n-- 汇总 --")
print("STRIP=%dpx；取样帧数 %d；可见那一段里跟基线的**最大差异** %d 处（出现在 +%.0fms）"
      % (STRIP, samples[0], worst[0], worst_at[0] * 1000))
print("其中**它刚可见的前 50ms** 里最大 %d 处 —— 那才是用户能看见的那一下（+%.0fms，位置 %s）"
      % (early[0], early_at[0] * 1000, early_bb[0]))
if samples[0] == 0:
    print("△ 一下都没采到「可见」的帧（它还没来得及可见就被关掉了）—— 这本身就是没露脸")
elif early[0] <= 60:
    print("★ 刚可见那一下屏幕上没变 —— 就是「不闪」（参照：拿掉空绘制区时同一位置 489 处）")
else:
    print("⚠ 刚可见那一下屏幕就变了 —— 用户能看见「闪一下」")
if rgn[0]:
    user32.SetWindowRgn(target[0], None, True)      # 把绘制区还给人家（我们只是量一下）
    gdi32.DeleteObject(rgn[0])
if target[0] is not None and user32.IsWindow(target[0]):
    user32.PostMessageW(target[0], 0x0112, 0xF060, 0)
    print("已请求关闭 hwnd=0x%X" % target[0])
