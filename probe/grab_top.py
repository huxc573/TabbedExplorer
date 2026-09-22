"""只看不动的探针：把 TabbedExplorer 主窗口顶部那一条抓下来存成 PNG。

不移动鼠标、不点东西、不改焦点 —— 纯 GetWindowRect + ImageGrab。
用来肉眼核对「标签条在最上面 / 标签只有一行 / 最右边三颗窗口按钮 / 悬停提示配色」这些外观改动。

用法: python probe/grab_top.py [输出文件] [抓多少像素高]
"""
import ctypes, sys
from ctypes import wintypes
from PIL import ImageGrab

u = ctypes.windll.user32
u.SetProcessDpiAwarenessContext(ctypes.c_void_p(-2))

out = sys.argv[1] if len(sys.argv) > 1 else 'probe/top_now.png'
band = int(sys.argv[2]) if len(sys.argv) > 2 else 200

EnumWindows = u.EnumWindows
CB = ctypes.WINFUNCTYPE(ctypes.c_bool, wintypes.HWND, wintypes.LPARAM)

target = []


def cb(h, l):
    pid = wintypes.DWORD()
    u.GetWindowThreadProcessId(h, ctypes.byref(pid))
    n = u.GetWindowTextLengthW(h)
    if n:
        b = ctypes.create_unicode_buffer(n + 1)
        u.GetWindowTextW(h, b, n + 1)
        t = b.value
    else:
        t = ''
    # 主窗口是可见的顶层窗口，标题 = 当前标签名（此电脑/网络/...），属主进程名对不上就靠类名
    cls = ctypes.create_unicode_buffer(256)
    u.GetClassNameW(h, cls, 256)
    if cls.value.startswith('WindowsForms10.Window.8'):
        r = wintypes.RECT()
        u.GetWindowRect(h, ctypes.byref(r))
        w, hh = r.right - r.left, r.bottom - r.top
        if u.IsWindowVisible(h) and w > 600 and hh > 300:
            target.append((h, r, t))
    return True


EnumWindows(CB(cb), 0)
print('候选窗口:', [(hex(h), (r.left, r.top, r.right, r.bottom), t) for h, r, t in target])

if not target:
    print('没找到窗口（可能只驻留托盘、窗口没开）')
    sys.exit(1)

h, r, t = target[0]
box = (r.left, r.top, r.right, r.top + band)
img = ImageGrab.grab(bbox=box)
img.save(out)
print('saved', out, img.size, 'title =', t)
