# -*- coding: utf-8 -*-
"""
只读探针：列出 TabbedExplorer 主窗口的**直接子窗口**（类名 + 矩形），
并把指定的那个子窗口单独 PrintWindow 出来。

为什么不用父窗口整张抓：PW_RENDERFULLCONTENT 走 DWM，
窗口被别的窗口（比如同一程序的另一个窗口）压住时，抓回来的像素可能混进别人的内容，
看着像「标签条上叠了别的字」。直接抓子窗口自己的 DC 才是它真正画的东西。

不动鼠标、不抢前台。
用法：python probe/grab_child.py [序号，默认 0 = 第一个全宽的子窗口]
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


def children(h):
    out = []
    c = user32.GetWindow(h, GW_CHILD)
    while c:
        out.append(c)
        c = user32.GetWindow(c, GW_HWNDNEXT)
    return out


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
    pick = int(sys.argv[1]) if len(sys.argv) > 1 else 0
    mains = find_mains()
    if not mains:
        print("没找到 TabbedExplorer 可见主窗口")
        return 1

    for mi, (h, pid, w, hh) in enumerate(mains):
        pr = rect_of(h)
        print("== 窗口 #%d hwnd=0x%X pid=%d %dx%d 屏幕(%d,%d)" % (mi, h, pid, w, hh, pr.left, pr.top))
        kids = children(h)
        full = []
        for c in kids:
            r = rect_of(c)
            cw, ch = r.right - r.left, r.bottom - r.top
            if not user32.IsWindowVisible(c):
                continue
            cls = class_of(c)
            print("   child 0x%-8X %-42s %4dx%-4d @(%d,%d) y=%d  text=%r"
                  % (c, cls[:42], cw, ch, r.left, r.top, r.top - pr.top, text_of(c)[:30]))
            if cw >= w - 40 and ch > 20:
                full.append(c)

        print("   -> 全宽子窗口 %d 个" % len(full))
        for k, c in enumerate(full):
            r = rect_of(c)
            cw, ch = r.right - r.left, r.bottom - r.top
            img = grab(c, cw, ch)
            out = "probe/_child%d_%d.png" % (mi, k)
            img.save(out)
            big = img.resize((img.width * 2, img.height * 2), Image.NEAREST)
            big.save(out.replace(".png", "_2x.png"))
            print("      [%d] 0x%X %dx%d -> %s" % (k, c, cw, ch, out))

        if mi == 0 and pick < len(full):
            pass
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
