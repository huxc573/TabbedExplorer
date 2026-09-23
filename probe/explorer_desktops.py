"""只读探针：判断每个 explorer.exe 挂在哪个「桌面」上。

EnumWindows 只能看到调用线程所在桌面的窗口；截图实验在隐藏桌面上起过 explorer，
那批进程在当前桌面「没有窗口」但进程还在——本探针按线程取 GetThreadDesktop 再读桌面名，
用来区分「Default 桌面上的孤儿」和「隐藏桌面上残留的实验进程」。

PowerShell 在这台机器上取 CIM 没输出，所以照样走 toolhelp + ctypes。
"""
import ctypes
from ctypes import wintypes

TH32CS_SNAPPROCESS, TH32CS_SNAPTHREAD = 0x2, 0x4
THREAD_QUERY_INFORMATION = 0x40
UOI_NAME = 2


class PROCESSENTRY32(ctypes.Structure):
    _fields_ = [
        ('dwSize', wintypes.DWORD), ('cntUsage', wintypes.DWORD),
        ('th32ProcessID', wintypes.DWORD), ('th32DefaultHeapID', ctypes.POINTER(ctypes.c_ulong)),
        ('th32ModuleID', wintypes.DWORD), ('cntThreads', wintypes.DWORD),
        ('th32ParentProcessID', wintypes.DWORD), ('pcPriClassBase', ctypes.c_long),
        ('dwFlags', wintypes.DWORD), ('szExeFile', ctypes.c_char * 260),
    ]


class THREADENTRY32(ctypes.Structure):
    _fields_ = [
        ('dwSize', wintypes.DWORD), ('cntUsage', wintypes.DWORD),
        ('th32ThreadID', wintypes.DWORD), ('th32OwnerProcessID', wintypes.DWORD),
        ('tpBasePri', ctypes.c_long), ('tpDeltaPri', ctypes.c_long), ('dwFlags', wintypes.DWORD),
    ]


k = ctypes.windll.kernel32
u32 = ctypes.windll.user32
# ⚠ 不声明 DPI 感知时 Windows 会按缩放比把坐标虚拟化（本机 150%：2880×1800 读成 1920×1200）
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    pass
k.OpenThread.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
k.OpenThread.restype = wintypes.HANDLE   # 不声明 restype 会按 32 位收，句柄被截断
u32.GetThreadDesktop.restype = wintypes.HANDLE
u32.GetUserObjectInformationW.argtypes = [wintypes.HANDLE, ctypes.c_int, wintypes.LPVOID,
                                          wintypes.DWORD, ctypes.POINTER(wintypes.DWORD)]
u32.GetThreadDesktop.argtypes = [wintypes.HANDLE]   # 64 位下必须用 HANDLE，用 DWORD 会把句柄截断

pids = set()
snap = k.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
pe = PROCESSENTRY32()
pe.dwSize = ctypes.sizeof(PROCESSENTRY32)
ok = k.Process32First(snap, ctypes.byref(pe))
while ok:
    if pe.szExeFile.decode('mbcs', 'replace').lower() == 'explorer.exe':
        pids.add(pe.th32ProcessID)
    ok = k.Process32Next(snap, ctypes.byref(pe))
k.CloseHandle(snap)

info = {p: set() for p in pids}
nthreads = {p: 0 for p in pids}
errs = {}
snap = k.CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0)
te = THREADENTRY32()
te.dwSize = ctypes.sizeof(THREADENTRY32)
ok = k.Thread32First(snap, ctypes.byref(te))
while ok:
    p = te.th32OwnerProcessID
    if p in pids:
        nthreads[p] += 1
        k.SetLastError(0)
        ht = k.OpenThread(THREAD_QUERY_INFORMATION, False, te.th32ThreadID)
        if not ht:
            errs.setdefault(ctypes.get_last_error(), 0)
            errs[ctypes.get_last_error()] += 1
        if ht:
            hd = u32.GetThreadDesktop(ht)
            if hd:
                buf = ctypes.create_unicode_buffer(256)
                need = wintypes.DWORD()
                if u32.GetUserObjectInformationW(hd, UOI_NAME, buf, 512, ctypes.byref(need)):
                    info[p].add(buf.value)
            k.CloseHandle(ht)
    ok = k.Thread32Next(snap, ctypes.byref(te))
k.CloseHandle(snap)

for p in sorted(pids):
    names = ",".join(sorted(info[p])) or "?"
    print("pid=%-6d threads=%-3d desktop=%s%s"
          % (p, nthreads[p], names, "   <== 非 Default" if names != "Default" else ""))
print("explorer.exe 共 %d 个" % len(pids))
if errs:
    print("OpenThread 失败码统计: %s" % errs)
