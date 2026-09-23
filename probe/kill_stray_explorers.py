"""清掉游离的 explorer.exe —— 只杀「彻底没有窗口」的那些。

为什么不能只看「有没有顶层窗口」：
  程序正在跑时，每个标签的 explorer 窗口被跨进程 SetParent 成了 EmbedForm 的**子窗口**，
  EnumWindows 看不见它们 —— 于是「没有顶层窗口」这个判据会把用户正在用的标签一起判成游离。
所以正确判据是：把**所有顶层窗口 + 它们的全部后代窗口**都扫一遍，凡是出现在里面的
explorer 进程就是「还有窗口的」，一律保护；真正游离的是那些**一个窗口都不剩**的。

shell 本体额外保护（Progman / Shell_TrayWnd 的宿主）。

先打印待杀名单；加 --yes 才真杀。
"""
import ctypes
import subprocess
import sys
from ctypes import wintypes

u32 = ctypes.windll.user32
# ⚠ 不声明 DPI 感知时 Windows 会按缩放比把坐标虚拟化（本机 150%：2880×1800 读成 1920×1200）
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    pass
k32 = ctypes.windll.kernel32
u32.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
u32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
k32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
k32.OpenProcess.restype = wintypes.HANDLE
k32.TerminateProcess.argtypes = [wintypes.HANDLE, wintypes.UINT]

SHELL_CLASSES = {"Progman", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"}
PROCESS_TERMINATE = 0x0001
EnumProc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

live = set()          # 有窗口的进程（保护）
protected = set()     # shell（保护）


def pid_of(h):
    pid = wintypes.DWORD()
    u32.GetWindowThreadProcessId(h, ctypes.byref(pid))
    return pid.value


def walk(hwnd, _):
    live.add(pid_of(hwnd))
    cls = ctypes.create_unicode_buffer(256)
    u32.GetClassNameW(hwnd, cls, 256)
    if cls.value in SHELL_CLASSES:
        protected.add(pid_of(hwnd))

    def child(h, __):
        live.add(pid_of(h))
        return True

    u32.EnumChildWindows(hwnd, EnumProc(child), 0)
    return True


u32.EnumWindows(EnumProc(walk), 0)

out = subprocess.run(["tasklist", "/FO", "CSV", "/NH"], capture_output=True, text=True,
                     errors="replace").stdout
all_pids = []
for line in out.splitlines():
    parts = [p.strip('"') for p in line.split('","')]
    if len(parts) >= 2 and parts[0].lower() == "explorer.exe" and parts[1].isdigit():
        all_pids.append(int(parts[1]))

stray = [p for p in all_pids if p not in live]
# --force：有窗口但人工确认是孤儿（典型是「窗口被 SW_HIDE 藏起来的孤儿文件夹窗口」）。
# 这些不能被 live 规则自动判成游离，所以必须显式点名，并在杀之前把它的窗口打出来核对。
forced = []
if "--force" in sys.argv:
    for a in sys.argv[sys.argv.index("--force") + 1].split(","):
        a = a.strip()
        if a.isdigit() and int(a) in all_pids:
            forced.append(int(a))
print("显式点名（--force）: %s" % sorted(forced))
stray = sorted(set(stray) | set(forced))
print("shell 进程（保护）: %s" % sorted(protected))
print("有窗口的 explorer（保护）: %s" % sorted(p for p in all_pids if p in live))
print("explorer 总数 %d，其中游离（一个窗口都没有）%d: %s"
      % (len(all_pids), len(stray), sorted(stray)))

# 把被点名进程的窗口打出来核对（确认确实是「藏起来的孤儿」而不是正在用的标签）
if forced:
    rows = []

    def show(hwnd, _):
        pid = pid_of(hwnd)
        if pid in forced:
            cls = ctypes.create_unicode_buffer(256)
            u32.GetClassNameW(hwnd, cls, 256)
            rows.append((pid, hwnd, cls.value, bool(u32.IsWindowVisible(hwnd))))
        return True

    u32.EnumWindows(EnumProc(show), 0)
    for pid, hwnd, cls, vis in sorted(rows):
        print("  点名进程 pid=%d 顶层窗口 0x%06X cls=%s 可见=%s" % (pid, hwnd, cls, vis))

if not stray:
    raise SystemExit("没有游离进程，收工")
if "--yes" not in sys.argv:
    print("（仅预演，未杀任何进程；确认后加 --yes）")
    raise SystemExit(0)

killed, failed = [], []
for pid in stray:
    h = k32.OpenProcess(PROCESS_TERMINATE, False, pid)
    if not h:
        failed.append((pid, "OpenProcess 失败"))
        continue
    if k32.TerminateProcess(h, 1):
        killed.append(pid)
    else:
        failed.append((pid, "TerminateProcess 失败"))
    k32.CloseHandle(h)

print("已杀 %d 个: %s" % (len(killed), sorted(killed)))
if failed:
    print("失败 %d 个: %s" % (len(failed), failed))
