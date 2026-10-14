# -*- coding: utf-8 -*-
"""只读探针：模拟 TB 的闸逻辑，记录「按空格那一刻」各修饰键状态。

跑起来后在**任意窗口**（先试记事本、再试 TB）按 CapsLock+空格，看输出。
"""
import ctypes, time, sys

u = ctypes.windll.user32
u.GetAsyncKeyState.restype = ctypes.c_short
u.GetAsyncKeyState.argtypes = [ctypes.c_int]

VK = {
    "CTRL": 0x11, "ALT": 0x12, "SHIFT": 0x10,
    "CAPS": 0x14, "LWIN": 0x5B, "RWIN": 0x5C, "SPACE": 0x20,
}

def down(vk):
    return 1 if (u.GetAsyncKeyState(vk) & 0x8000) else 0

print("时刻    Caps  Ctrl  Alt  Shift  LWin  RWin   Space   ← 按空格那一刻的快照")
print("-" * 72)
t0 = time.time()
lastSpace = 0
try:
    while True:
        sp = down(VK["SPACE"])
        if sp and not lastSpace:      # 空格刚按下
            print("%6.2fs   %d     %d     %d    %d      %d     %d      %d"
                  % (time.time() - t0, down(VK["CAPS"]), down(VK["CTRL"]),
                     down(VK["ALT"]), down(VK["SHIFT"]),
                     down(VK["LWIN"]), down(VK["RWIN"]), sp))
        lastSpace = sp
        time.sleep(0.005)
except KeyboardInterrupt:
    print("退出")
