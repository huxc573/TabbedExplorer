# 只读探针：把每个 CabinetWClass（我们的内嵌窗口 + 原生窗口）里的
# 「ReBar 的每一条 band」和「所有 ToolbarWindow32」打出来，用来回答两个问题：
#   ① 那条白色的「文件(F) 编辑(E) 查看(V) 工具(T)」到底是什么窗口、在不在 ReBar 的某条 band 上；
#   ② 我们的内嵌窗口和原生窗口差在哪（band 的 RBBS_HIDDEN 位 / 子窗口可见性）。
#
# ⚠ 全程只读：不发鼠标键盘消息、不 ShowWindow、不抢前台、不动光标。
#   唯一会「发消息」的地方是 SendMessageTimeoutW(WM_GETTEXT) —— 带 300ms 超时 + SMTO_ABORTIFHUNG，
#   对方忙就放弃，绝不卡住（跨进程 GetWindowTextW 读不到 toolbar 的文本，必须走这条）。
#
# 用法：
#   python probe/menubar_probe.py                 # 扫全系统所有 CabinetWClass
#   python probe/menubar_probe.py --pid 21568     # 只看某个进程的窗口（连它的子树一起）
import ctypes
import sys
from ctypes import wintypes

u32 = ctypes.windll.user32
k32 = ctypes.windll.kernel32

u32.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
u32.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
u32.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
u32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
u32.GetWindow.argtypes = [wintypes.HWND, ctypes.c_uint]
u32.GetWindow.restype = wintypes.HWND
u32.GetParent.argtypes = [wintypes.HWND]
u32.GetParent.restype = wintypes.HWND
u32.IsWindowVisible.argtypes = [wintypes.HWND]
u32.IsWindowVisible.restype = wintypes.BOOL
u32.GetWindowLongW.argtypes = [wintypes.HWND, ctypes.c_int]
u32.GetWindowLongW.restype = ctypes.c_uint
u32.SendMessageTimeoutW.argtypes = [wintypes.HWND, ctypes.c_uint, ctypes.c_size_t,
                                    ctypes.c_ssize_t, ctypes.c_uint, ctypes.c_uint,
                                    ctypes.POINTER(ctypes.c_size_t)]

GW_CHILD, GW_HWNDNEXT = 5, 2
GWL_STYLE, GWL_EXSTYLE = -16, -20
WS_CHILD, WS_VISIBLE = 0x40000000, 0x10000000
WS_CAPTION, WS_POPUP = 0x00C00000, 0x80000000
WS_EX_LAYERED, WS_EX_TOOLWINDOW, WS_EX_APPWINDOW = 0x00080000, 0x00000080, 0x00040000

WM_GETTEXT = 0x000D
WM_USER = 0x0400
RB_GETBANDCOUNT = WM_USER + 3          # 0x0403
RB_GETBANDINFOW = WM_USER + 4          # 0x0404
RBBIM_CHILD, RBBIM_STYLE = 0x0010, 0x0001
RBBS_HIDDEN = 0x0008
SMTO_ABORTIFHUNG = 0x0002

TH32CS_SNAPPROCESS = 0x2


class PROCESSENTRY32(ctypes.Structure):
    _fields_ = [('dwSize', wintypes.DWORD), ('cntUsage', wintypes.DWORD),
                ('th32ProcessID', wintypes.DWORD),
                ('th32DefaultHeapID', ctypes.POINTER(ctypes.c_ulong)),
                ('th32ModuleID', wintypes.DWORD), ('cntThreads', wintypes.DWORD),
                ('th32ParentProcessID', wintypes.DWORD),
                ('pcPriClassBase', ctypes.c_long), ('dwFlags', wintypes.DWORD),
                ('szExeFile', ctypes.c_char * 260)]


class REBARBANDINFOW(ctypes.Structure):
    # x64 布局（ctypes 自己按自然对齐排，字段顺序和 SDK 里一致）
    _fields_ = [('cbSize', wintypes.UINT), ('fMask', wintypes.UINT),
                ('fStyle', wintypes.UINT), ('clrFore', wintypes.DWORD),
                ('clrBack', wintypes.DWORD), ('lpText', ctypes.c_void_p),
                ('cch', wintypes.UINT), ('iImage', ctypes.c_int),
                ('hwndChild', wintypes.HWND), ('cxMinChild', wintypes.UINT),
                ('cyMinChild', wintypes.UINT), ('cx', wintypes.UINT),
                ('hbmBack', ctypes.c_void_p), ('wID', wintypes.UINT),
                ('cyChild', wintypes.UINT), ('cyMaxChild', wintypes.UINT),
                ('cyIntegral', wintypes.UINT), ('cxIdeal', wintypes.UINT),
                ('lParam', ctypes.c_ssize_t), ('cxHeader', wintypes.UINT),
                ('rcChevronLocation', wintypes.RECT),
                ('uChevronState', wintypes.UINT)]


def pids_of(name):
    snap = k32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
    e = PROCESSENTRY32()
    e.dwSize = ctypes.sizeof(PROCESSENTRY32)
    out = []
    if k32.Process32First(snap, ctypes.byref(e)):
        while True:
            if e.szExeFile.decode('mbcs', 'ignore').lower() == name.lower():
                out.append(e.th32ProcessID)
            if not k32.Process32Next(snap, ctypes.byref(e)):
                break
    k32.CloseHandle(snap)
    return out


def cls(h):
    b = ctypes.create_unicode_buffer(256)
    u32.GetClassNameW(h, b, 256)
    return b.value


def txt_self(h):
    b = ctypes.create_unicode_buffer(512)
    u32.GetWindowTextW(h, b, 512)
    return b.value


def txt_any(h):
    """跨进程文本：GetWindowTextW 对别人的 toolbar 读不到，必须走 WM_GETTEXT。"""
    b = ctypes.create_unicode_buffer(1024)
    got = ctypes.c_size_t(0)
    u32.SendMessageTimeoutW(h, WM_GETTEXT, 1024, ctypes.cast(b, ctypes.c_void_p).value or 0,
                            SMTO_ABORTIFHUNG, 300, ctypes.byref(got))
    return b.value


def rect(h):
    r = wintypes.RECT()
    u32.GetWindowRect(h, ctypes.byref(r))
    return (r.left, r.top, r.right - r.left, r.bottom - r.top)


def style(h):
    return u32.GetWindowLongW(h, GWL_STYLE)


def exstyle(h):
    return u32.GetWindowLongW(h, GWL_EXSTYLE)


def pid_of(h):
    q = wintypes.DWORD()
    u32.GetWindowThreadProcessId(h, ctypes.byref(q))
    return q.value


def child(h):
    return u32.GetWindow(h, GW_CHILD)


def nextsib(h):
    return u32.GetWindow(h, GW_HWNDNEXT)


def all_desc(h, limit=4000):
    out = []
    stack = [child(h)]
    while stack:
        c = stack.pop()
        if not c or len(out) > limit:
            continue
        out.append(c)
        stack.append(nextsib(c))
        stack.append(child(c))
    return out


def band_dump(rebar):
    """
    ⚠⚠ 这里**只能**用「不传指针」的那几条 ReBar 消息。

    2026-09-25 实测踩过：`RB_GETBANDINFOW`（WM_USER+4）跨进程发出去时，
    系统**不会**帮我们把 `lParam` 那个 `REBARBANDINFOW*` 封送到对方地址空间 ——
    对方直接按我们这个进程的地址去写，那边根本没映射 ⇒ **access violation，explorer 当场死**。
    一次探针把内嵌的两个 explorer 进程全带走了（程序日志里就是
    「ShellReg: 找不到 HWND hr=0x800706BE」+「嵌入的 explorer 窗口没了」）。

    `RB_GETBANDCOUNT` 的 wParam/lParam 都是 0，不涉及指针，可以发；
    想知道每条 band 的 `RBBS_HIDDEN` 就得换条路（比如 `GetWindowLong` 看子窗口可见性）。
    """
    got = ctypes.c_size_t(0)
    n = u32.SendMessageTimeoutW(rebar, RB_GETBANDCOUNT, 0, 0, SMTO_ABORTIFHUNG, 300,
                                ctypes.byref(got))
    print('      ReBar 0x%X: band 数=%d（RB_GETBANDINFOW 不发了，见 band_dump 注释）' % (rebar, n))


def walk_toolbars(h):
    for d in all_desc(h):
        c = cls(d)
        if c in ('ReBarWindow32', 'ToolbarWindow32', 'msctls_statusbar32',
                 'DUIViewWndClassName', 'SHELLDLL_DefView', 'SysTreeView32'):
            s, ex = style(d), exstyle(d)
            print('    %-22s 0x%-10X rect=%-22s vis=%d style=0x%08X ex=0x%08X %r'
                  % (c, d, rect(d), u32.IsWindowVisible(d), s, ex, txt_any(d)[:60]))
        if c == 'ReBarWindow32':
            band_dump(d)


def main():
    want_pid = 0
    args = sys.argv[1:]
    for i, a in enumerate(args):
        if a == '--pid' and i + 1 < len(args):
            want_pid = int(args[i + 1])

    ours = set(pids_of('TabbedExplorer.exe'))
    print('TabbedExplorer.exe pids =', sorted(ours))
    shell_pids = set(pids_of('explorer.exe'))

    # ⚠ 内嵌的 CabinetWClass 已经不是顶层窗口了（被 SetParent 成我们窗体的子窗口），
    #   所以必须从每个顶层窗口往下走一遍才能找到它 —— 只 EnumWindows 顶层是找不到的。
    tops = []
    EnumProc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

    def on(h, _):
        tops.append(h)
        return True

    u32.EnumWindows(EnumProc(on), 0)

    shown = 0
    for t in tops:
        if not u32.IsWindowVisible(t):
            continue
        cabs = [t] if cls(t) in ('CabinetWClass', 'ExploreWClass') else [
            d for d in all_desc(t) if cls(d) in ('CabinetWClass', 'ExploreWClass')]
        for cab in cabs:
            p = pid_of(cab)
            if want_pid and p != want_pid and pid_of(t) != want_pid:
                continue
            shown += 1
            kind = ('我们的' if pid_of(t) in ours or p in ours
                    else ('shell' if p in shell_pids else '第三方'))
            print('')
            print('=== %s Cab 0x%X pid=%d rect=%s vis=%d parent=0x%X style=0x%08X ex=0x%08X %r'
                  % (kind, cab, p, rect(cab), u32.IsWindowVisible(cab), u32.GetParent(cab),
                     style(cab), exstyle(cab), txt_any(cab)[:60]))
            walk_toolbars(cab)
    print('')
    print('共列出 %d 个 CabinetWClass' % shown)
    return 0


if __name__ == '__main__':
    sys.exit(main())
