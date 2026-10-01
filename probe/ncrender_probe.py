# -*- coding: utf-8 -*-
"""对照实验（会**主动操作我起的那扇测试窗**，不碰别的窗）：

  ① 起一扇独立 explorer 窗（/n,/separate,"C:\\Windows"，不吃 shell 复用）→ 抓右上角 + 客户区中央
  ② 对它 `SetWindowRgn(空区域)`（= 我们 `BlankShellWindow` 干的事）→ 再抓
     预期：客户区没了，但**标题栏/右上角三个按钮还在** ⇒ 这就是川看到的「只剩三个按钮」
  ③ 再叠加 `DwmSetWindowAttribute(DWMWA_NCRENDERING_POLICY, DWMNCRP_DISABLED)` → 再抓
     预期：按钮也没了 ⇒ 这就是修法
  ④ 全部还原（区域 + NCRENDERING）、关掉这扇窗

用法: python -u ncrender_probe.py > probe/_ncr.log 2>&1
"""
import ctypes
import ctypes.wintypes as wt
import os
import time
from ctypes import byref, c_void_p, c_int, c_uint

u32 = ctypes.windll.user32
gdi32 = ctypes.windll.gdi32
dwmapi = ctypes.windll.dwmapi
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    pass

u32.GetDC.restype = c_void_p
u32.GetDC.argtypes = [c_void_p]
u32.ReleaseDC.argtypes = [c_void_p, c_void_p]
u32.GetSystemMetrics.argtypes = [c_int]
u32.GetClassNameW.argtypes = [wt.HWND, wt.LPWSTR, c_int]
u32.GetWindowRect.argtypes = [wt.HWND, ctypes.POINTER(wt.RECT)]
u32.IsWindow.argtypes = [wt.HWND]
u32.IsWindow.restype = wt.BOOL
u32.IsWindowVisible.argtypes = [wt.HWND]
u32.IsWindowVisible.restype = wt.BOOL
u32.FindWindowExW.argtypes = [wt.HWND, wt.HWND, wt.LPCWSTR, wt.LPCWSTR]
u32.FindWindowExW.restype = wt.HWND
u32.SetWindowRgn.argtypes = [wt.HWND, c_void_p, wt.BOOL]
u32.SetWindowRgn.restype = c_int
u32.GetWindowThreadProcessId.argtypes = [wt.HWND, ctypes.POINTER(ctypes.c_ulong)]
u32.PostMessageW.argtypes = [wt.HWND, c_uint, wt.WPARAM, wt.LPARAM]
dwmapi.DwmSetWindowAttribute.argtypes = [wt.HWND, c_uint, c_void_p, c_uint]
dwmapi.DwmSetWindowAttribute.restype = ctypes.c_long
gdi32.CreateRectRgn.argtypes = [c_int, c_int, c_int, c_int]
gdi32.CreateRectRgn.restype = c_void_p
gdi32.CreateCompatibleDC.restype = c_void_p
gdi32.CreateCompatibleDC.argtypes = [c_void_p]
gdi32.CreateDIBSection.restype = c_void_p
gdi32.CreateDIBSection.argtypes = [c_void_p, c_void_p, c_uint, ctypes.POINTER(c_void_p), c_void_p, c_uint]
gdi32.SelectObject.restype = c_void_p
gdi32.SelectObject.argtypes = [c_void_p, c_void_p]
gdi32.BitBlt.argtypes = [c_void_p, c_int, c_int, c_int, c_int, c_void_p, c_int, c_int, c_uint]

SRCCOPY = 0x00CC0020
DWMWA_NCRENDERING_POLICY = 2
DWMNCRP_USEWINDOWSTYLE = 0
DWMNCRP_DISABLED = 1

SHOTS = os.path.join(os.path.dirname(os.path.abspath(__file__)), "shots")
_rect = wt.RECT()


def stamp():
    return time.strftime("%H:%M:%S", time.localtime()) + ".%03d" % int((time.time() % 1) * 1000)


class BIH(ctypes.Structure):
    _fields_ = [("biSize", c_uint), ("biWidth", c_int), ("biHeight", c_int),
                ("biPlanes", ctypes.c_ushort), ("biBitCount", ctypes.c_ushort),
                ("biCompression", c_uint), ("biSizeImage", c_uint),
                ("biXPelsPerMeter", c_int), ("biYPelsPerMeter", c_int),
                ("biClrUsed", c_uint), ("biClrImportant", c_uint)]


class BI(ctypes.Structure):
    _fields_ = [("bmiHeader", BIH), ("bmiColors", c_uint * 3)]


screen = u32.GetDC(None)
CW, CH = 260, 60


def mk(w, h):
    mem = gdi32.CreateCompatibleDC(screen)
    bi = BI()
    bi.bmiHeader.biSize = ctypes.sizeof(BIH)
    bi.bmiHeader.biWidth = w
    bi.bmiHeader.biHeight = -h
    bi.bmiHeader.biPlanes = 1
    bi.bmiHeader.biBitCount = 32
    p = c_void_p()
    d = gdi32.CreateDIBSection(mem, byref(bi), 0, byref(p), None, 0)
    gdi32.SelectObject(mem, d)
    return mem, d, p


_mem, _dib, _ppv = mk(CW, CH)
from PIL import Image


def grab(tag, x0, y0):
    gdi32.BitBlt(_mem, 0, 0, CW, CH, screen, x0, y0, SRCCOPY)
    im = Image.frombuffer("RGBA", (CW, CH), ctypes.string_at(_ppv, CW * CH * 4),
                          "raw", "BGRA", 0, 1).convert("RGB")
    im.save(os.path.join(SHOTS, tag + ".png"))
    # 同时给个「亮不亮」的数值指纹，方便日志直接判读：非背景色像素数
    px = im.load()
    lit = 0
    for j in range(0, CH, 3):
        for i in range(0, CW, 3):
            r, g, b = px[i, j]
            if r + g + b > 120:      # 明显亮于深色背景
                lit += 1
    return lit


def cls_of(h):
    b = ctypes.create_unicode_buffer(256)
    u32.GetClassNameW(h, b, 256)
    return b.value


def tops_cabs():
    out = []
    h = 0
    while True:
        h = u32.FindWindowExW(None, h, "CabinetWClass", None)
        if not h:
            break
        out.append(h)
    return out


def main():
    if not os.path.isdir(SHOTS):
        os.makedirs(SHOTS)
    before = set(tops_cabs())
    print("%s  已有浏览窗 %d 个" % (stamp(), len(before)))
    print("%s  起一扇独立 explorer 窗 ..." % stamp())
    ctypes.windll.shell32.ShellExecuteW(None, "open", "C:\\Windows", None, None, 1)

    h = 0
    for _ in range(100):
        time.sleep(0.1)
        for x in tops_cabs():
            if x not in before:
                h = x
                break
        if h:
            break
    if not h:
        print("%s  ✗ 没等到新窗" % stamp())
        return
    time.sleep(1.0)
    u32.GetWindowRect(h, byref(_rect))
    r = (_rect.left, _rect.top, _rect.right, _rect.bottom)
    print("%s  测试窗 cab=0x%X rect=%s 可见=%s" %
          (stamp(), int(h), r, bool(u32.IsWindowVisible(h))))

    cx = (r[0] + r[2]) // 2 - CW // 2
    cy = (r[1] + r[3]) // 2 - CH // 2
    rx = r[2] - CW
    ry = r[1]

    # ① 原样
    print("%s  ① 原样：右上角亮=%d 客户区亮=%d" %
          (stamp(), grab("ncr_1_右上角_原样", rx, ry), grab("ncr_1_客户区_原样", cx, cy)))

    # ② SetWindowRgn(空)
    rgn = gdi32.CreateRectRgn(0, 0, 0, 0)
    ok = u32.SetWindowRgn(h, rgn, True)
    time.sleep(0.6)
    print("%s  ② SetWindowRgn(空) 返回=%s：右上角亮=%d 客户区亮=%d" %
          (stamp(), ok != 0, grab("ncr_2_右上角_清绘制区", rx, ry),
           grab("ncr_2_客户区_清绘制区", cx, cy)))

    # ③ + DWM 关非客户区渲染
    pol = c_int(DWMNCRP_DISABLED)
    hr = dwmapi.DwmSetWindowAttribute(h, DWMWA_NCRENDERING_POLICY, byref(pol), 4)
    time.sleep(0.6)
    print("%s  ③ +DWMNCRP_DISABLED hr=0x%X：右上角亮=%d 客户区亮=%d" %
          (stamp(), hr & 0xFFFFFFFF, grab("ncr_3_右上角_DWM关非客户区", rx, ry),
           grab("ncr_3_客户区_DWM关非客户区", cx, cy)))

    # ④ 还原
    pol0 = c_int(DWMNCRP_USEWINDOWSTYLE)
    dwmapi.DwmSetWindowAttribute(h, DWMWA_NCRENDERING_POLICY, byref(pol0), 4)
    u32.SetWindowRgn(h, None, True)
    time.sleep(0.6)
    print("%s  ④ 还原：右上角亮=%d 客户区亮=%d" %
          (stamp(), grab("ncr_4_右上角_还原", rx, ry), grab("ncr_4_客户区_还原", cx, cy)))

    # 关掉这扇测试窗
    u32.PostMessageW(h, 0x0010, 0, 0)     # WM_CLOSE
    print("%s  已发 WM_CLOSE 给测试窗 0x%X" % (stamp(), int(h)))


main()
