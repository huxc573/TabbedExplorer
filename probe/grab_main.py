# -*- coding: utf-8 -*-
"""
只读探针：把 TabbedExplorer 主窗口整张 PrintWindow 出来（含自绘部分），
并列出它的直接子窗口（类名 + 矩形 + 可见性），用来定位「多出来的那条白条」。

不动鼠标、不抢前台。
用法：python probe/grab_main.py [输出文件名后缀]
"""
import ctypes
import sys
from ctypes import wintypes

try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
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
GW_CHILD = 5
GW_HWNDNEXT = 2


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


def rect_of(h):
    r = RECT()
    user32.GetWindowRect(h, ctypes.byref(r))
    return r


def class_of(h):
    buf = ctypes.create_unicode_buffer(300)
    user32.GetClassNameW(h, buf, 300)
    return buf.value


def text_of(h):
    buf = ctypes.create_unicode_buffer(300)
    user32.GetWindowTextW(h, buf, 300)
    return buf.value


def find_mains():
    found = []

    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def cb(h, l):
        if not user32.IsWindowVisible(h):
            return True
        name, pid = proc_name(h)
        if name.lower().endswith("tabbedexplorer.exe"):
            r = rect_of(h)
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

    class BIH(ctypes.Structure):
        _fields_ = [("biSize", ctypes.c_uint32), ("biWidth", ctypes.c_int32),
                    ("biHeight", ctypes.c_int32), ("biPlanes", ctypes.c_uint16),
                    ("biBitCount", ctypes.c_uint16), ("biCompression", ctypes.c_uint32),
                    ("biSizeImage", ctypes.c_uint32), ("biXPelsPerMeter", ctypes.c_int32),
                    ("biYPelsPerMeter", ctypes.c_int32), ("biClrUsed", ctypes.c_uint32),
                    ("biClrImportant", ctypes.c_uint32)]

    bi = BIH()
    bi.biSize = ctypes.sizeof(BIH)
    bi.biWidth = w
    bi.biHeight = -h
    bi.biPlanes = 1
    bi.biBitCount = 32
    buf = ctypes.create_string_buffer(w * h * 4)
    gdi32.GetDIBits(mem, bmp, 0, h, buf, ctypes.byref(bi), 0)

    gdi32.DeleteObject(bmp)
    gdi32.DeleteDC(mem)
    user32.ReleaseDC(hwnd, hdc)
    return Image.frombuffer("RGBA", (w, h), buf, "raw", "BGRA", 0, 1).convert("RGB")


def main():
    tag = sys.argv[1] if len(sys.argv) > 1 else "0"
    mains = find_mains()
    if not mains:
        print("没找到 TabbedExplorer 可见主窗口")
        return 1

    for mi, (h, pid, w, hh) in enumerate(mains):
        pr = rect_of(h)
        print("== 窗口 #%d hwnd=0x%X pid=%d %dx%d 屏幕(%d,%d)  ex=0x%08X"
              % (mi, h, pid, w, hh, pr.left, pr.top,
                 user32.GetWindowLongW(h, -20) & 0xFFFFFFFF))

        c = user32.GetWindow(h, GW_CHILD)
        while c:
            r = rect_of(c)
            cw, ch = r.right - r.left, r.bottom - r.top
            print("   child 0x%-8X %-34s %4dx%-4d @(%d,%d) y=%d vis=%d  text=%r"
                  % (c, class_of(c)[:34], cw, ch, r.left, r.top, r.top - pr.top,
                     user32.IsWindowVisible(c), text_of(c)[:30]))
            c = user32.GetWindow(c, GW_HWNDNEXT)

        img = grab(h, w, hh)
        out = "probe/_main%s_%d.png" % (tag, mi)
        img.save(out)
        img.resize((img.width, img.height), Image.NEAREST).save(out)
        # 顶部 200px 单独放大一份，看那条白条
        top = img.crop((0, 0, img.width, min(200, img.height)))
        top.resize((top.width, top.height * 2), Image.NEAREST).save(
            "probe/_main%s_%d_top.png" % (tag, mi))
        print("   -> %s" % out)
        print("   -> probe/_main%s_%d_top.png（顶部 200px 两倍放大）" % (tag, mi))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
