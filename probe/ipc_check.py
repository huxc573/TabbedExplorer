"""只读探针：检查单实例互斥体和两个命名事件是否还在（判断「已经在跑的实例」有没有被回收/丢失）。
只 Open + Close，不 Set、不建窗口、不动前台。
"""
import ctypes

k = ctypes.windll.kernel32
SYNCHRONIZE = 0x00100000


def probe_open(fn, name):
    h = fn(SYNCHRONIZE, False, name)
    print("%-34s 存在=%s (handle=%s)" % (name, bool(h), h))
    if h:
        k.CloseHandle(h)


probe_open(k.OpenMutexW, "TabbedExplorer.Embed.v1")
probe_open(k.OpenMutexW, "TabbedExplorer.SingleInstance.v1")
probe_open(k.OpenEventW, "TabbedExplorer.Show.v1")
probe_open(k.OpenEventW, "TabbedExplorer.Quit.v1")
