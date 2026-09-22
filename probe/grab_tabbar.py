# -*- coding: utf-8 -*-
"""
只读探针：抓 TabbedExplorer 主窗口的**顶带**（标签条那一行 + 右边那排功能按钮），
用来肉眼核对「标签栏和功能图标堆叠」到底长什么样。

不动鼠标、不抢前台：PrintWindow(PW_RENDERFULLCONTENT) 抓自己的窗口。
用法：python probe/grab_tabbar.py [顶带设备像素高，默认 80]
"""
import ctypes
import sys
from ctypes import wintypes

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
PROCESS_QUERY_LIMITED_INFORMATION = 0x1000


class RECT(ctypes.Structure):
    _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long),
                ("right", ctypes.c_long), ("bottom", ctypes.c_long)]


def proc_name(hwnd):
    pid = wintypes.DWORD()
    user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    h = ctypes.windll.kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid.value)
    if not h:
        return "", pid.value
    buf = ctypes.create_unicode_buffer(700)
    n = ctypes.c_uint(700)
    ctypes.windll.kernel32.QueryFullProcessImageNameW(h, 0, buf, ctypes.byref(n))
    ctypes.windll.kernel32.CloseHandle(h)
    return buf.value, pid.value


def find_windows():
    found = []

    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def cb(h, l):
        if not user32.IsWindowVisible(h):
            return True
        name, pid = proc_name(h)
        if name.lower().endswith("tabbedexplorer.exe"):
            r = RECT()
            user32.GetWindowRect(h, ctypes.byref(r))
            w, hh = r.right - r.left, r.bottom - r.top
            if w > 300 and hh > 200:
                found.append((h, pid, w, hh))
        return True

    user32.EnumWindows(cb, 0)
    return found


def grab(hwnd, w, h):
    hdc = user32.GetWindowDC(hwnd)
    mem = gdi32.CreateCompatibleDC(hdc)
    bmp = gdi32.CreateCompatibleBitmap(hdc, w, h)
    gdi32.SelectObject(mem, bmp)
    user32.PrintWindow(hwnd, mem, PW_RENDERFULLCONTENT)

    class BITMAPINFOHEADER(ctypes.Structure):
        _fields_ = [("biSize", ctypes.c_uint32), ("biWidth", ctypes.c_int32),
                    ("biHeight", ctypes.c_int32), ("biPlanes", ctypes.c_uint16),
                    ("biBitCount", ctypes.c_uint16), ("biCompression", ctypes.c_uint32),
                    ("biSizeImage", ctypes.c_uint32), ("biXPelsPerMeter", ctypes.c_int32),
                    ("biYPelsPerMeter", ctypes.c_int32), ("biClrUsed", ctypes.c_uint32),
                    ("biClrImportant", ctypes.c_uint32)]

    bi = BITMAPINFOHEADER()
    bi.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bi.biWidth = w
    bi.biHeight = -h          # 负数 = top-down
    bi.biPlanes = 1
    bi.biBitCount = 32
    bi.biCompression = 0

    buf = ctypes.create_string_buffer(w * h * 4)
    gdi32.GetDIBits(mem, bmp, 0, h, buf, ctypes.byref(bi), 0)

    gdi32.DeleteObject(bmp)
    gdi32.DeleteDC(mem)
    user32.ReleaseDC(hwnd, hdc)

    img = Image.frombuffer("RGBA", (w, h), buf, "raw", "BGRA", 0, 1).convert("RGB")
    return img


def main():
    band = int(sys.argv[1]) if len(sys.argv) > 1 else 80
    wins = find_windows()
    if not wins:
        print("没找到 TabbedExplorer 的可见主窗口（程序在跑吗？）")
        return 1

    for i, (h, pid, w, hh) in enumerate(wins):
        img = grab(h, w, hh)
        full = "probe/_win%d_full.png" % i
        img.save(full)
        crop = img.crop((0, 0, w, min(band, hh)))
        crop = crop.resize((w * 2, crop.height * 2), Image.NEAREST)   # 2x 放大看细节
        out = "probe/_win%d_tabbar.png" % i
        crop.save(out)
        print("hwnd=0x%X pid=%d 窗口=%dx%d" % (h, pid, w, hh))
        print("  全窗 -> %s" % full)
        print("  顶带 -> %s（%dx%d，2x）" % (out, crop.width, crop.height))
    return 0


if __name__ == "__main__":

    raise SystemExit(main())
