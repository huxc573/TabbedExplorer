"""只读探针：列出所有 explorer.exe 的启动时间（好判断哪些是「我们起的」）。

`tasklist` 不给启动时间；PowerShell 在这台机器上取 CIM 拿不到输出。
所以用纯 ctypes 走 toolhelp 拿 pid、再 OpenProcess+GetProcessTimes 拿启动时间。
"""
import ctypes, datetime, sys
from ctypes import wintypes

TH32CS_SNAPPROCESS = 0x2
PROCESS_QUERY_LIMITED_INFORMATION = 0x1000


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


k = ctypes.windll.kernel32
snap = k.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
pe = PROCESSENTRY32()
pe.dwSize = ctypes.sizeof(PROCESSENTRY32)
rows = []
ok = k.Process32First(snap, ctypes.byref(pe))
while ok:
    name = pe.szExeFile.decode('mbcs', 'replace')
    if name.lower() == 'explorer.exe':
        h = k.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pe.th32ProcessID)
        ct = None
        if h:
            c, e, kt, u = FILETIME(), FILETIME(), FILETIME(), FILETIME()
            if k.GetProcessTimes(h, ctypes.byref(c), ctypes.byref(e),
                                 ctypes.byref(kt), ctypes.byref(u)):
                v = (c.dwHighDateTime << 32) | c.dwLowDateTime
                ct = datetime.datetime(1601, 1, 1) + datetime.timedelta(microseconds=v // 10)
            k.CloseHandle(h)
        rows.append((ct, pe.th32ProcessID, pe.th32ParentProcessID))
    ok = k.Process32Next(snap, ctypes.byref(pe))
k.CloseHandle(snap)

rows.sort(key=lambda r: (r[0] is None, r[0]))
for ct, pid, ppid in rows:
    print('%s  pid=%-6d ppid=%d' % (ct.strftime('%H:%M:%S') if ct else '   ?    ', pid, ppid))
print('共 %d 个 explorer.exe' % len(rows))
