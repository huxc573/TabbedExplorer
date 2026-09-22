# 菜单冒烟测试 —— **全程只用 PostMessage，不动光标、不抢前台、不切窗口**。
#
# 为什么要有它（2026-09-22）：
#   右键菜单连着两代实现都是「弹得出来、点不动」，甚至把 UI 线程卡死。
#   用户的规矩是不许跑会动他前台的自动化测试，所以这里用 PostMessage 合成
#   「WM_RBUTTONDOWN/UP」和「WM_LBUTTONDOWN/UP」——消息直接投进目标窗口的消息队列，
#   系统光标一动不动，前台窗口也不变。菜单会在屏幕上闪一下（没法避免），但不影响他手上的事。
#
# 用法：
#   python probe/menu_smoke.py list              只列主窗口的子窗口（先看清哪个是收藏夹栏）
#   python probe/menu_smoke.py click <hwnd> <x> <y>   往那个子窗口右键一下，然后点它菜单里第 1 项
#
# 判读（对着 data/log.txt）：
#   成功 = 出现「菜单: 弹出 …」→「菜单项: …」→「菜单: 关闭 …」三行，
#          并且中途 SendMessageTimeout 一直能收到回应（说明 UI 线程没卡）。
import ctypes, sys, time
from ctypes import wintypes

u32 = ctypes.windll.user32
k32 = ctypes.windll.kernel32

u32.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
u32.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
u32.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
u32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
u32.GetWindow.argtypes = [wintypes.HWND, ctypes.c_uint]
u32.GetWindow.restype = wintypes.HWND
u32.IsWindowVisible.argtypes = [wintypes.HWND]
u32.IsWindowVisible.restype = wintypes.BOOL
u32.SendMessageTimeoutW.argtypes = [wintypes.HWND, ctypes.c_uint, ctypes.c_size_t,
                                    ctypes.c_ssize_t, ctypes.c_uint, ctypes.c_uint,
                                    ctypes.POINTER(ctypes.c_size_t)]
u32.PostMessageW.argtypes = [wintypes.HWND, ctypes.c_uint, ctypes.c_size_t, ctypes.c_ssize_t]

WM_RBUTTONDOWN, WM_RBUTTONUP = 0x0204, 0x0205
WM_LBUTTONDOWN, WM_LBUTTONUP = 0x0201, 0x0202
WM_MOUSEMOVE = 0x0200
MK_LBUTTON, MK_RBUTTON = 0x0001, 0x0002
GW_CHILD, GW_HWNDNEXT = 5, 2
SMTO_ABORTIFHUNG = 0x0002

TH32CS_SNAPPROCESS = 0x2


class PROCESSENTRY32(ctypes.Structure):
    _fields_ = [('dwSize', wintypes.DWORD), ('cntUsage', wintypes.DWORD),
                ('th32ProcessID', wintypes.DWORD), ('th32DefaultHeapID', ctypes.POINTER(ctypes.c_ulong)),
                ('th32ModuleID', wintypes.DWORD), ('cntThreads', wintypes.DWORD),
                ('th32ParentProcessID', wintypes.DWORD), ('pcPriClassBase', ctypes.c_long),
                ('dwFlags', wintypes.DWORD), ('szExeFile', ctypes.c_char * 260)]


def find_pid(name="TabbedExplorer.exe"):
    snap = k32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
    e = PROCESSENTRY32()
    e.dwSize = ctypes.sizeof(PROCESSENTRY32)
    pids = []
    if k32.Process32First(snap, ctypes.byref(e)):
        while True:
            if e.szExeFile.decode('mbcs', 'ignore').lower() == name.lower():
                pids.append(e.th32ProcessID)
            if not k32.Process32Next(snap, ctypes.byref(e)):
                break
    k32.CloseHandle(snap)
    return pids


def cls(h):
    b = ctypes.create_unicode_buffer(256)
    u32.GetClassNameW(h, b, 256)
    return b.value


def txt(h):
    b = ctypes.create_unicode_buffer(512)
    u32.GetWindowTextW(h, b, 512)
    return b.value


def rect(h):
    r = wintypes.RECT()
    u32.GetWindowRect(h, ctypes.byref(r))
    return (r.left, r.top, r.right - r.left, r.bottom - r.top)


def children(h):
    out, c = [], u32.GetWindow(h, GW_CHILD)
    while c:
        out.append(c)
        c = u32.GetWindow(c, GW_HWNDNEXT)
    return out


def responsive(h):
    res = ctypes.c_size_t(0)
    return bool(u32.SendMessageTimeoutW(h, 0, 0, 0, SMTO_ABORTIFHUNG, 1500, ctypes.byref(res)))


def lp(x, y):
    return (y << 16) | (x & 0xFFFF)


def main():
    pids = find_pid()
    if not pids:
        print("没找到 TabbedExplorer.exe 在跑")
        return 1
    pid = pids[0]
    print("pid =", pid)

    tops = []

    EnumProc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

    def on(h, _):
        p = wintypes.DWORD()
        u32.GetWindowThreadProcessId(h, ctypes.byref(p))
        if p.value == pid and 'WindowsForms10' in cls(h) and u32.IsWindowVisible(h):
            x, y, w, hh = rect(h)
            if w > 400 and hh > 300:
                tops.append(h)
        return True

    u32.EnumWindows(EnumProc(on), 0)
    if not tops:
        print("没找到主窗口（可能收进托盘了）")
        return 1
    main_w = tops[0]
    print("主窗口 hwnd=0x%X rect=%s  %r" % (main_w, rect(main_w), txt(main_w)))
    print("响应:", "OK" if responsive(main_w) else "★卡住")
    print("")

    mode = sys.argv[1] if len(sys.argv) > 1 else "list"

    if mode == "list":
        print("=== 主窗口的子窗口 ===")
        for c in children(main_w):
            print("  hwnd=0x%-8X %-46s rect=%-26s %r"
                  % (c, cls(c)[:46], rect(c), txt(c)[:24]))
        return 0

    # ---- click 模式：hwnd x y [menuY] [--left] ----
    args = [a for a in sys.argv[2:] if not a.startswith("--")]
    use_left = "--left" in sys.argv
    target = int(args[0], 16) if args[0].startswith("0x") else int(args[0])
    x, y = int(args[1]), int(args[2])
    print("往 hwnd=0x%X 的 (%d,%d) %s键一下" % (target, x, y, "左" if use_left else "右"))

    before = set(children(main_w))
    u32.PostMessageW(target, WM_MOUSEMOVE, 0, lp(x, y))
    time.sleep(0.05)
    if use_left:
        u32.PostMessageW(target, WM_LBUTTONDOWN, MK_LBUTTON, lp(x, y))
        time.sleep(0.05)
        u32.PostMessageW(target, WM_LBUTTONUP, 0, lp(x, y))
    else:
        u32.PostMessageW(target, WM_RBUTTONDOWN, MK_RBUTTON, lp(x, y))
        time.sleep(0.05)
        u32.PostMessageW(target, WM_RBUTTONUP, 0, lp(x, y))
    time.sleep(0.7)

    print("主窗口响应:", "OK" if responsive(main_w) else "★卡住 —— 就是老毛病犯了")

    # 找刚弹出来的菜单窗口（本进程、新的顶层窗口）
    new = []

    def on2(h, _):
        p = wintypes.DWORD()
        u32.GetWindowThreadProcessId(h, ctypes.byref(p))
        if p.value == pid and h not in before and u32.IsWindowVisible(h) and 'WindowsForms10' in cls(h):
            new.append(h)
        return True

    u32.EnumWindows(EnumProc(on2), 0)
    menus = [h for h in new if rect(h)[2] < 800 and rect(h)[3] < 900]
    if not menus:
        print("★没看到新弹出的菜单窗口")
        return 2

    m = menus[0]
    mx, my, mw, mh = rect(m)
    print("菜单 hwnd=0x%X rect=%s（%dx%d）" % (m, (mx, my, mw, mh), mw, mh))

    # 点菜单里哪一行：
    #   默认第 1 行（行高按逻辑 26px × DPI，取行中心）；
    #   想点别的行就在命令行给第 5 个参数 = 菜单客户区的 y（分隔线占 7px，自己算）。
    click_x = mw // 2
    if len(args) > 3:
        click_y = int(args[3])
    else:
        dpi = u32.GetDpiForSystem() / 96.0
        click_y = round(4 * dpi) + round(26 * dpi) // 2
    print("在菜单客户区 (%d,%d) 左键点" % (click_x, click_y))

    u32.PostMessageW(m, WM_MOUSEMOVE, 0, lp(click_x, click_y))
    time.sleep(0.15)
    u32.PostMessageW(m, WM_LBUTTONDOWN, MK_LBUTTON, lp(click_x, click_y))
    time.sleep(0.05)
    u32.PostMessageW(m, WM_LBUTTONUP, 0, lp(click_x, click_y))
    time.sleep(0.6)

    print("主窗口响应:", "OK" if responsive(main_w) else "★卡住")
    print("")
    print("现在去看 data/log.txt 末尾：应该有「菜单: 弹出」「菜单项: 」「菜单: 关闭」三行。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
