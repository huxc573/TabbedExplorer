"""只读探针：把 explorer.exe / TabbedExplorer.exe 的进程属主链和窗口归属列出来。

回答三个问题：
  1. 这些 explorer.exe 是谁的孩子（ppid 的进程名）—— 我们起的还是 shell 的？
  2. 它们的真实创建时间（`explorer_ages.py` 只给"跑了多久"，容易被误读）。
  3. `TabbedExplorer` 名下有几个顶层窗、各带几个子窗口（子窗口里那些 `CabinetWClass`
     就是"被嵌进来的 explorer"，据此判断标签是嵌着的还是飘在桌面上）。

纯 ctypes，标准库。64 位句柄一律声明类型（默认 c_int 会静默截断）。
"""
import ctypes
import datetime
from ctypes import wintypes

TH32CS_SNAPPROCESS = 0x2
PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
GW_OWNER = 4


class PROCESSENTRY32(ctypes.Structure):
    _fields_ = [
        ('dwSize', wintypes.DWORD),
        ('cntUsage', wintypes.DWORD),
        ('th32ProcessID', wintypes.DWORD),
        ('th32DefaultHeapID', ctypes.POINTER(ctypes.c_ulong)),
        ('th32ModuleID', wintypes.DWORD),
        ('cntThreads', wintypes.DWORD),
        ('th32ParentProcessID', wintypes.DWORD),
        ('pcPriClassBase', ctypes.c_long),
        ('dwFlags', wintypes.DWORD),
        ('szExeFile', ctypes.c_char * 260),
    ]


class FILETIME(ctypes.Structure):
    _fields_ = [('dwLowDateTime', wintypes.DWORD), ('dwHighDateTime', wintypes.DWORD)]


k32 = ctypes.windll.kernel32
u32 = ctypes.windll.user32

u32.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
u32.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
u32.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
u32.IsWindowVisible.argtypes = [wintypes.HWND]
u32.GetParent.argtypes = [wintypes.HWND]
u32.GetParent.restype = wintypes.HWND
u32.GetWindow.argtypes = [wintypes.HWND, wintypes.UINT]
u32.GetWindow.restype = wintypes.HWND
k32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
k32.OpenProcess.restype = wintypes.HANDLE
k32.GetProcessTimes.argtypes = [wintypes.HANDLE, ctypes.POINTER(FILETIME), ctypes.POINTER(FILETIME),
                               ctypes.POINTER(FILETIME), ctypes.POINTER(FILETIME)]
u32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]

WNDENUMPROC = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
# 不声明 argtypes 的话，64 位下 HWND 会按 c_int 传 → 高 32 位被截掉 → 枚举回来恒空
u32.EnumWindows.argtypes = [WNDENUMPROC, wintypes.LPARAM]
u32.EnumWindows.restype = wintypes.BOOL
u32.EnumChildWindows.argtypes = [wintypes.HWND, WNDENUMPROC, wintypes.LPARAM]
u32.EnumChildWindows.restype = wintypes.BOOL
k32.CloseHandle.argtypes = [wintypes.HANDLE]


def class_of(h):
    b = ctypes.create_unicode_buffer(256)
    u32.GetClassNameW(h, b, 256)
    return b.value


def title_of(h):
    b = ctypes.create_unicode_buffer(512)
    u32.GetWindowTextW(h, b, 512)
    return b.value


def pid_of(h):
    p = wintypes.DWORD(0)
    u32.GetWindowThreadProcessId(h, ctypes.byref(p))
    return p.value


def rect_of(h):
    r = wintypes.RECT()
    u32.GetWindowRect(h, ctypes.byref(r))
    return (r.left, r.top, r.right - r.left, r.bottom - r.top)


def created_at(pid):
    h = k32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    if not h:
        return None
    try:
        c = FILETIME()
        e = FILETIME()
        kt = FILETIME()
        ut = FILETIME()
        if not k32.GetProcessTimes(h, ctypes.byref(c), ctypes.byref(e),
                                  ctypes.byref(kt), ctypes.byref(ut)):
            return None
        ticks = (c.dwHighDateTime << 32) | c.dwLowDateTime
        # FILETIME 的 100ns 单位 / 1970 差 11644473600 秒
        return datetime.datetime.fromtimestamp(ticks / 1e7 - 11644473600)
    finally:
        k32.CloseHandle(h)


# ---- 进程表 ----------------------------------------------------------------
snap = k32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
pe = PROCESSENTRY32()
pe.dwSize = ctypes.sizeof(PROCESSENTRY32)
procs = {}
ok = k32.Process32First(snap, ctypes.byref(pe))
while ok:
    procs[pe.th32ProcessID] = (pe.szExeFile.decode('mbcs', 'replace'),
                              pe.th32ParentProcessID)
    ok = k32.Process32Next(snap, ctypes.byref(pe))

targets = sorted(pid for pid, (nm, _) in procs.items()
                 if nm.lower() in ('explorer.exe', 'tabbedexplorer.exe'))
print('=== 目标进程（按 pid） ===')
for pid in targets:
    nm, ppid = procs[pid]
    pnm, _ = procs.get(ppid, ('(已退出)', 0))
    print('pid=%-6d %-22s 父=%-6d %-22s 创建=%s'
          % (pid, nm, ppid, pnm, created_at(pid)))

# ---- 窗口归属 --------------------------------------------------------------
print()
print('=== 我们的顶层窗 + 子树里的 CabinetWClass（嵌入证据） ===')
ours = [pid for pid, (nm, _) in procs.items() if nm.lower() == 'tabbedexplorer.exe']
found = []


def top_cb(h, l):
    if pid_of(h) in ours:
        found.append(h)
    return True


u32.EnumWindows(WNDENUMPROC(top_cb), 0)

kids = []


def collect(h, l):
    kids.append((h, pid_of(h), class_of(h), rect_of(h), bool(u32.IsWindowVisible(h))))
    return True


for t in found:
    print('顶层 0x%X pid=%d 可见=%s rect=%s cls=%s title=%r'
          % (t, pid_of(t), bool(u32.IsWindowVisible(t)), rect_of(t),
             class_of(t), title_of(t)))
    kids = []
    u32.EnumChildWindows(t, WNDENUMPROC(collect), 0)
    cabs = [x for x in kids if x[2] in ('CabinetWClass', 'ExploreWClass')]
    print('     子树共 %d 个窗口，其中 CabinetWClass %d 个'
          % (len(kids), len(cabs)))
    for h, p, cls, rc, vis in cabs:
        print('       cab=0x%-8X pid=%-6d 可见=%-5s rect=%s'
              % (h, p, vis, rc))

# ---- 飘在外面的 CabinetWClass（没被收的） -----------------------------------
print()
print('=== 顶层 CabinetWClass（应为我们之外的、没被收的） ===')
stray = []


def stray_cb(h, l):
    if class_of(h) in ('CabinetWClass', 'ExploreWClass'):
        stray.append(h)
    return True


u32.EnumWindows(WNDENUMPROC(stray_cb), 0)
if not stray:
    print('（没有）')
for h in stray:
    print('0x%-8X pid=%-6d 可见=%-5s parent=0x%-8X rect=%s title=%r'
          % (h, pid_of(h), bool(u32.IsWindowVisible(h)), u32.GetParent(h),
             rect_of(h), title_of(h)))
