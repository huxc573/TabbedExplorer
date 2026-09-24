"""只读探针：盯着**新出生的进程**（5ms 一次），并把它的命令行读出来。

为什么需要它：「三方应用点打开文件夹」那条链上，最关键的判据是
**到底有没有新起一个 explorer.exe**（哪怕它只是「转发」一下就退出）。
窗口是看不见的、前台可能不变，但进程出生一定看得见 —— 命令行里还带着目标路径。

用法：python proc_birth_watch.py [秒数=20]
纯 ctypes、只读。
"""
import ctypes
import sys
import time
from ctypes import wintypes

k32 = ctypes.windll.kernel32
ntdll = ctypes.windll.ntdll

k32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
k32.OpenProcess.restype = wintypes.HANDLE
k32.ReadProcessMemory.argtypes = [wintypes.HANDLE, ctypes.c_void_p, ctypes.c_void_p,
                                 ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]
k32.CloseHandle.argtypes = [wintypes.HANDLE]
k32.CreateToolhelp32Snapshot.argtypes = [wintypes.DWORD, wintypes.DWORD]
k32.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
ntdll.NtQueryInformationProcess.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p,
                                           ctypes.c_ulong, ctypes.POINTER(ctypes.c_ulong)]

TH32CS_SNAPPROCESS = 0x00000002
Q = 0x1000 | 0x0010


class PROCESSENTRY32W(ctypes.Structure):
    _fields_ = [("dwSize", wintypes.DWORD), ("cntUsage", wintypes.DWORD),
                ("th32ProcessID", wintypes.DWORD), ("th32DefaultHeapID", ctypes.c_size_t),
                ("th32ModuleID", wintypes.DWORD), ("cntThreads", wintypes.DWORD),
                ("th32ParentProcessID", wintypes.DWORD), ("pcPriClassBase", ctypes.c_long),
                ("dwFlags", wintypes.DWORD), ("szExeFile", ctypes.c_wchar * 260)]


class PBI(ctypes.Structure):
    _fields_ = [("Reserved1", ctypes.c_void_p), ("PebBaseAddress", ctypes.c_void_p),
                ("Reserved2", ctypes.c_void_p * 2), ("UniqueProcessId", ctypes.c_void_p),
                ("InheritedFromUniqueProcessId", ctypes.c_void_p)]


def cmdline(pid):
    h = k32.OpenProcess(Q, False, pid)
    if not h:
        return None
    try:
        pbi = PBI()
        got = ctypes.c_ulong(0)
        if ntdll.NtQueryInformationProcess(h, 0, ctypes.byref(pbi), ctypes.sizeof(pbi),
                                           ctypes.byref(got)) != 0:
            return None
        pp, n = ctypes.c_void_p(0), ctypes.c_size_t(0)
        if not k32.ReadProcessMemory(h, ctypes.c_void_p(pbi.PebBaseAddress + 0x20),
                                     ctypes.byref(pp), 8, ctypes.byref(n)):
            return None
        ln, buf = ctypes.c_ushort(0), ctypes.c_void_p(0)
        if not k32.ReadProcessMemory(h, ctypes.c_void_p(pp.value + 0x70),
                                     ctypes.byref(ln), 2, ctypes.byref(n)):
            return None
        if not k32.ReadProcessMemory(h, ctypes.c_void_p(pp.value + 0x78),
                                     ctypes.byref(buf), 8, ctypes.byref(n)):
            return None
        if not ln.value or not buf.value:
            return ""
        raw = ctypes.create_unicode_buffer(ln.value // 2 + 1)
        k32.ReadProcessMemory(h, ctypes.c_void_p(buf.value), raw, ln.value, ctypes.byref(n))
        return raw.value
    except Exception:
        return None
    finally:
        k32.CloseHandle(h)


def snap():
    out = {}
    s = k32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
    if not s or s == ctypes.c_void_p(-1).value:
        return out
    try:
        pe = PROCESSENTRY32W()
        pe.dwSize = ctypes.sizeof(PROCESSENTRY32W)
        ok = k32.Process32FirstW(s, ctypes.byref(pe))
        while ok:
            out[pe.th32ProcessID] = (pe.szExeFile, pe.th32ParentProcessID)
            ok = k32.Process32NextW(s, ctypes.byref(pe))
    finally:
        k32.CloseHandle(s)
    return out


secs = int(sys.argv[1]) if len(sys.argv) > 1 else 20
known = dict(snap())
print("开始盯新进程 %d 秒（现有 %d 个）" % (secs, len(known)), flush=True)
t0 = time.time()
born = {}
while time.time() - t0 < secs:
    cur = snap()
    for p in sorted(set(cur) - set(known)):
        cl_ = cmdline(p)
        born[p] = time.time()
        print("[%6.2f] 出生 %-22s pid=%-6d 父=%-6d 命令行=%s"
              % (time.time() - t0, cur[p][0], p, cur[p][1], repr(cl_)), flush=True)
    for p in sorted(set(known) - set(cur)):
        print("[%6.2f] 退出 pid=%-6d %s 存活 %.2fs"
              % (time.time() - t0, p, known[p][0], time.time() - born.get(p, t0)), flush=True)
    known = cur
    time.sleep(0.005)
print("结束")
