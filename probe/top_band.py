# -*- coding: utf-8 -*-
"""
只读探针：抓 TabbedExplorer 主窗口的一条纵向色带，检查「标签条 → explorer 内容区」
之间还有没有那条该死的浅灰死白（防闪流程曾经因为量不到 TopBlank 把它留在容器里）。

不动鼠标、不抢焦点：用 PrintWindow(PW_RENDERFULLCONTENT) 抓自己的窗口。
"""
import ctypes
from ctypes import wintypes
import sys
import os

try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)   # PER_MONITOR_AWARE_V2
except Exception:
    try:
        ctypes.windll.user32.SetProcessDPIAware()
    except Exception:
        pass

from PIL import Image

user32 = ctypes.windll.user32
gdi32 = ctypes.windll.gdi32

PW_RENDERFULLCONTENT = 2


class RECT(ctypes.Structure):
    _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long),
                ("right", ctypes.c_long), ("bottom", ctypes.c_long)]


def find_main():
    """按标题找我们的窗（无边框窗口标题就是当前标签名，所以逐个比对类名 + 尺寸）。"""
    hwnds = []

    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def cb(h, l):
        pid = wintypes.DWORD()
        user32.GetWindowThreadProcessId(h, ctypes.byref(pid))
        buf = ctypes.create_unicode_buffer(256)
        user32.GetClassNameW(h, buf, 256)
        if buf.value == "WindowsForms10.Window.8.app.0.141b42a_r6_ad1" or "WindowsForms10" in buf.value:
            if user32.IsWindowVisible(h):
                r = RECT()
                user32.GetWindowRect(h, ctypes.byref(r))
                w, hh = r.right - r.left, r.bottom - r.top
                if w > 600 and hh > 400:
                    hwnds.append((h, pid.value, r))
        return True

    user32.EnumWindows(cb, 0)
    return hwnds


def grab(hwnd, r):
    w, h = r.right - r.left, r.bottom - r.top
    hdc = user32.GetWindowDC(hwnd)
    mdc = gdi32.CreateCompatibleDC(hdc)
    bmp = gdi32.CreateCompatibleBitmap(hdc, w, h)
    gdi32.SelectObject(mdc, bmp)
    ok = user32.PrintWindow(hwnd, mdc, PW_RENDERFULLCONTENT)

    class BITMAPINFOHEADER(ctypes.Structure):
        _fields_ = [("biSize", wintypes.DWORD), ("biWidth", ctypes.c_long),
                    ("biHeight", ctypes.c_long), ("biPlanes", wintypes.WORD),
                    ("biBitCount", wintypes.WORD), ("biCompression", wintypes.DWORD),
                    ("biSizeImage", wintypes.DWORD), ("biXPelsPerMeter", ctypes.c_long),
                    ("biYPelsPerMeter", ctypes.c_long), ("biClrUsed", wintypes.DWORD),
                    ("biClrImportant", wintypes.DWORD)]

    bi = BITMAPINFOHEADER()
    bi.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bi.biWidth = w
    bi.biHeight = -h          # top-down
    bi.biPlanes = 1
    bi.biBitCount = 32
    bi.biCompression = 0

    buf = ctypes.create_string_buffer(w * h * 4)
    gdi32.GetDIBits(mdc, bmp, 0, h, buf, ctypes.byref(bi), 0)
    gdi32.DeleteObject(bmp)
    gdi32.DeleteDC(mdc)
    user32.ReleaseDC(hwnd, hdc)

    img = Image.frombuffer("RGBA", (w, h), buf, "raw", "BGRA", 0, 1)
    return ok, img.convert("RGB")


def main():
    hw = find_main()
    if not hw:
        print("找不到主窗口")
        return 1
    for h, pid, r in hw:
        print("hwnd=0x%X pid=%d rect=(%d,%d)-(%d,%d) %dx%d"
              % (h, pid, r.left, r.top, r.right, r.bottom,
                 r.right - r.left, r.bottom - r.top))
    h, pid, r = max(hw, key=lambda t: (t[2].right - t[2].left) * (t[2].bottom - t[2].top))
    ok, img = grab(h, r)
    print("PrintWindow ok=", ok, "size=", img.size)

    out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "top_band_check.png")
    img.save(out)
    print("saved:", out)

    # 沿内容区左侧一段竖直扫，打印每一行的颜色（只看前 180 行，够到标签条 + 内容区）
    x = 380
    print("--- 纵向色带 x=%d（0..180）---" % x)
    prev = None
    for y in range(0, min(180, img.size[1])):
        c = img.getpixel((x, y))
        if c != prev:
            print("  y=%3d  RGB%s" % (y, (c,)))
            prev = c
    return 0


if __name__ == "__main__":
    sys.exit(main())
