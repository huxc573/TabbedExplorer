# 只读探针：把某个进程（默认 TabbedExplorer.exe）的顶层窗口 + 子树类名打出来。
#
# 用途：验「shell 的 frames 到底有没有生效」——
#   关掉 EBO_SHOWFRAMES：ExplorerBrowserControl | SHELLDLL_DefView | DirectUIHWND | ...
#   开着 EBO_SHOWFRAMES：ExplorerBrowserControl | DUIViewWndClassName | DirectUIHWND
#                          | SHELLDLL_DefView | DirectUIHWND | ...
#   多出来的 DUIViewWndClassName 那一层（内含 NamespaceTreeControl → SysTreeView32 原生导航树）
#   就是 frames 生效的铁证。
#
# 用法：
#   python probe/class_tree.py                 # 自动找 TabbedExplorer.exe
#   python probe/class_tree.py --pid 21568
#   python probe/class_tree.py --depth 8
#
# ⚠ 枚举子窗口用逐层 GetWindow(GW_CHILD/GW_HWNDNEXT)，**不用 EnumChildWindows**
#   （后者会把整棵子树重复列出来）。全程只读，不动光标、不抢前台。
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
u32.IsWindowVisible.argtypes = [wintypes.HWND]
u32.IsWindowVisible.restype = wintypes.BOOL

GW_CHILD, GW_HWNDNEXT = 5, 2
TH32CS_SNAPPROCESS = 0x2


class PROCESSENTRY32(ctypes.Structure):
    _fields_ = [('dwSize', wintypes.DWORD), ('cntUsage', wintypes.DWORD),
                ('th32ProcessID', wintypes.DWORD),
                ('th32DefaultHeapID', ctypes.POINTER(ctypes.c_ulong)),
                ('th32ModuleID', wintypes.DWORD), ('cntThreads', wintypes.DWORD),
                ('th32ParentProcessID', wintypes.DWORD),
                ('pcPriClassBase', ctypes.c_long), ('dwFlags', wintypes.DWORD),
                ('szExeFile', ctypes.c_char * 260)]


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


def txt(h):
    b = ctypes.create_unicode_buffer(256)
    u32.GetWindowTextW(h, b, 256)
    return b.value


def rect(h):
    r = wintypes.RECT()
    u32.GetWindowRect(h, ctypes.byref(r))
    return (r.left, r.top, r.right - r.left, r.bottom - r.top)


def child(h):
    return u32.GetWindow(h, GW_CHILD)


def nextsib(h):
    return u32.GetWindow(h, GW_HWNDNEXT)


def walk(h, depth, maxdepth, out):
    c = child(h)
    while c:
        vis = '' if u32.IsWindowVisible(c) else '·'
        out.append('%s%s%s  [%s] rect=%s %s'
                   % ('  ' * depth, vis, cls(c), hex(c), rect(c), txt(c)[:24]))
        if depth < maxdepth:
            walk(c, depth + 1, maxdepth, out)
        c = nextsib(c)


def main():
    pid = 0
    depth = 10
    args = sys.argv[1:]
    for i, a in enumerate(args):
        if a == '--pid' and i + 1 < len(args):
            pid = int(args[i + 1])
        if a == '--depth' and i + 1 < len(args):
            depth = int(args[i + 1])

    if not pid:
        found = pids_of('TabbedExplorer.exe')
        if not found:
            print('没找到 TabbedExplorer.exe 在跑')
            return 1
        print('TabbedExplorer.exe pids =', found)

    targets = [pid] if pid else found
    EnumProc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

    for p in targets:
        print('')
        print('================ pid=%d ================' % p)
        tops = []

        def on(h, _):
            q = wintypes.DWORD()
            u32.GetWindowThreadProcessId(h, ctypes.byref(q))
            if q.value == p:
                tops.append(h)
            return True

        u32.EnumWindows(EnumProc(on), 0)
        for t in tops:
            w, hh = rect(t)[2], rect(t)[3]
            if w * hh == 0:
                continue
            print('--- top %s %s rect=%s %r' % (cls(t), hex(t), rect(t), txt(t)[:40]))
            out = []
            walk(t, 1, depth, out)
            for line in out:
                print('    ' + line)
    return 0


if __name__ == '__main__':
    sys.exit(main())
