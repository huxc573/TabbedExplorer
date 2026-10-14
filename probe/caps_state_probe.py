# -*- coding: utf-8 -*-
"""只读探针：实时打印 CapsLock 的 GetAsyncKeyState 状态位。

用法：跑起来后，**按住 CapsLock** 保持住，看输出里的 0x8000 位有没有出现。
"""
import ctypes, time, sys

u = ctypes.windll.user32
u.GetAsyncKeyState.restype = ctypes.c_short
u.GetAsyncKeyState.argtypes = [ctypes.c_int]

VK_CAPITAL = 0x14
VK_SPACE = 0x20

print("按住 CapsLock（别松），观察 0x8000 位。Ctrl+C 退出。")
print("时刻         CapsLock(raw)  down?   Space(raw)  down?")
last = None
t0 = time.time()
try:
    while True:
        c = u.GetAsyncKeyState(VK_CAPITAL) & 0xFFFF
        s = u.GetAsyncKeyState(VK_SPACE) & 0xFFFF
        cur = (c, s)
        if cur != last:
            print("%7.2fs  caps=0x%04x  down=%d   space=0x%04x  down=%d"
                  % (time.time() - t0, c, 1 if (c & 0x8000) else 0,
                     s, 1 if (s & 0x8000) else 0))
            last = cur
        time.sleep(0.02)
except KeyboardInterrupt:
    print("\n退出")
