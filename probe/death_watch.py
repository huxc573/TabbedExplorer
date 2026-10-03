# -*- coding: utf-8 -*-
"""TabbedExplorer 退出码哨兵。

盯 TabbedExplorer.exe 的生灭：进程一退出就记 时间戳+退出码 到镜像 data\death_watch.log。
纯 stdlib + ctypes（toolhelp 快照枚举 + OpenProcess(SYNCHRONIZE|QUERY_LIMITED) 等待）。
用法：pythonw death_watch.py   （常驻，无窗口）
"""
import ctypes
from ctypes import wintypes
import os
import threading
import time

PROC_NAME = "TabbedExplorer.exe"
LOG = r"D:\Software\!Sync\TabbedExplorer\data\death_watch.log"

TH32CS_SNAPPROCESS = 0x2
SYNCHRONIZE = 0x00100000
PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
STILL_ACTIVE = 259
INFINITE = 0xFFFFFFFF

EXIT_NOTES = {
    0: "正常退出",
    0xC0000005: "访问违例 AV",
    0xC0000409: "fastfail/栈缓冲溢出",
    0xC000041D: "内核回调异常",
    0xC000013A: "Ctrl+C/关机打断",
    0xC0000142: "DLL 初始化失败",
    0xC000000D: "STATUS_INVALID_PARAMETER(常见于外部杀)",
    0x40010004: "调试器终止",
    0xE0434352: ".NET 未处理异常",
    1: "通用失败码(外部杀常用)",
}

_k32 = ctypes.WinDLL("kernel32", use_last_error=True)


class PROCESSENTRY32W(ctypes.Structure):
    _fields_ = [
        ("dwSize", wintypes.DWORD),
        ("cntUsage", wintypes.DWORD),
        ("th32ProcessID", wintypes.DWORD),
        ("th32DefaultHeapID", ctypes.c_size_t),
        ("th32ModuleID", wintypes.DWORD),
        ("cntThreads", wintypes.DWORD),
        ("th32ParentProcessID", wintypes.DWORD),
        ("pcPriClassBase", ctypes.c_long),
        ("dwFlags", wintypes.DWORD),
        ("szExeFile", ctypes.c_wchar * 260),
    ]


def log(msg):
    try:
        with open(LOG, "a", encoding="utf-8") as f:
            f.write(time.strftime("%Y-%m-%d %H:%M:%S ") + msg + "\n")
    except OSError:
        pass


def find_pids():
    snap = _k32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
    if snap == -1 or snap == 0xFFFFFFFFFFFFFFFF:
        return []
    pids = []
    e = PROCESSENTRY32W()
    e.dwSize = ctypes.sizeof(e)
    ok = _k32.Process32FirstW(snap, ctypes.byref(e))
    while ok:
        if e.szExeFile == PROC_NAME:
            pids.append(e.th32ProcessID)
        ok = _k32.Process32NextW(snap, ctypes.byref(e))
    _k32.CloseHandle(snap)
    return pids


def watch_pid(pid):
    """等 pid 退出并记退出码。必须在进程活着时打开句柄，死后打不开。"""
    h = _k32.OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    if not h:
        log("pid=%d 打开句柄失败 err=%d（多半刚退出，退出码丢失）" % (pid, ctypes.get_last_error()))
        return
    _k32.WaitForSingleObject(h, INFINITE)
    code = wintypes.DWORD(0)
    _k32.GetExitCodeProcess(h, ctypes.byref(code))
    _k32.CloseHandle(h)
    c = code.value
    note = EXIT_NOTES.get(c, "")
    log("pid=%d 已退出 exit=0x%08X %s" % (pid, c, note))


def main():
    log("=== 哨兵启动 pid=%d ===" % os.getpid())
    seen = set()
    for p in find_pids():
        seen.add(p)
        log("pid=%d 已在运行，开始盯" % p)
        threading.Thread(target=watch_pid, args=(p,), daemon=True).start()
    while True:
        time.sleep(2)
        for p in find_pids():
            if p not in seen:
                seen.add(p)
                log("pid=%d 出现，开始盯" % p)
                threading.Thread(target=watch_pid, args=(p,), daemon=True).start()


if __name__ == "__main__":
    main()
