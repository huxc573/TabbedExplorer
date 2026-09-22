"""列出进程父子关系（只看 explorer.exe / TabbedExplorer.exe）。

用途：换 exe 前要杀掉「我们自己嵌进来的那些 explorer」，但**绝不能碰 shell 的
那个 explorer**（杀掉 = 桌面和任务栏一起消失）。纯 ctypes toolhelp，不依赖
wmic/PowerShell（本机 PowerShell 的 CIM 查询返回空）。
"""
import ctypes
import ctypes.wintypes as w

TH32CS_SNAPPROCESS = 0x2
MAX_PATH = 260


class PROCESSENTRY32(ctypes.Structure):
    _fields_ = [("dwSize", w.DWORD), ("cntUsage", w.DWORD),
                ("th32ProcessID", w.DWORD),
                ("th32DefaultHeapID", ctypes.POINTER(ctypes.c_ulong)),
                ("th32ModuleID", w.DWORD), ("cntThreads", w.DWORD),
                ("th32ParentProcessID", w.DWORD),
                ("pcPriClassBase", ctypes.c_long),
                ("dwFlags", w.DWORD),
                ("szExeFile", ctypes.c_char * MAX_PATH)]


def snapshot():
    k = ctypes.windll.kernel32
    snap = k.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
    e = PROCESSENTRY32()
    e.dwSize = ctypes.sizeof(PROCESSENTRY32)
    rows = []
    ok = k.Process32First(snap, ctypes.byref(e))
    while ok:
        rows.append((e.th32ProcessID, e.th32ParentProcessID,
                     e.szExeFile.decode('mbcs', 'replace')))
        ok = k.Process32Next(snap, ctypes.byref(e))
    k.CloseHandle(snap)
    return rows


def main():
    rows = snapshot()
    by_pid = {p: (pp, n) for p, pp, n in rows}
    mine = None
    for p, pp, n in rows:
        if n.lower() == 'tabbedexplorer.exe':
            mine = p
    for p, pp, n in rows:
        if n.lower() in ('tabbedexplorer.exe', 'explorer.exe'):
            parent = by_pid.get(pp, (None, '?'))[1]
            flag = ''
            if mine is not None:
                if p == mine:
                    flag = '   <== 我们自己的主程序'
                elif pp == mine:
                    flag = '   <== 我们嵌进来的 explorer（可杀）'
            print("%-22s pid=%-7d ppid=%-7d parent=%-22s%s" % (n, p, pp, parent, flag))
    if mine is not None:
        print("OUR_PID=%d" % mine)


if __name__ == '__main__':
    main()
