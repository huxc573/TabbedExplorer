# -*- coding: utf-8 -*-
"""干净实测：不装任何钩子，纯轮询 CapsLock / Space 的状态位，变化就落盘。

目的：确认「按住 CapsLock」时，系统层的按下位（GetAsyncKeyState 的 0x8000）
到底是不是 1 —— 这一条决定我们能不能靠它判「CapsLock 组合键」。
"""
import ctypes, time, io, os

u = ctypes.windll.user32
u.GetAsyncKeyState.restype = ctypes.c_short
u.GetAsyncKeyState.argtypes = [ctypes.c_int]

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "_caps_hold_out.txt")
f = io.open(OUT, "w", encoding="utf-8", buffering=1)

VK_CAP, VK_SPACE = 0x14, 0x20
t0 = time.time()
f.write("t=0 开始（30 秒窗口，纯轮询、无钩子）\n")
last = None
while time.time() - t0 < 30:
    c = u.GetAsyncKeyState(VK_CAP) & 0xFFFF
    s = u.GetAsyncKeyState(VK_SPACE) & 0xFFFF
    cur = (1 if (c & 0x8000) else 0, c & 1, 1 if (s & 0x8000) else 0)
    if cur != last:
        f.write("%6.2fs  caps按下=%d   caps锁定=%d   space按下=%d\n"
                % (time.time() - t0, cur[0], cur[1], cur[2]))
        last = cur
    time.sleep(0.004)
f.write("t=30 结束\n")
f.close()
