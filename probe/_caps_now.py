# -*- coding: utf-8 -*-
import ctypes, time
u = ctypes.windll.user32
u.GetAsyncKeyState.restype = ctypes.c_short
u.GetAsyncKeyState.argtypes = [ctypes.c_int]
VK_CAPITAL = 0x14
print("当前 CapsLock GetAsyncKeyState =", hex(u.GetAsyncKeyState(VK_CAPITAL) & 0xFFFF))
print("高字节(0x80=此刻按着) =", 1 if (u.GetAsyncKeyState(VK_CAPITAL) & 0x8000) else 0)
print("低字节(0x01=自上次调用后按过) =", 1 if (u.GetAsyncKeyState(VK_CAPITAL) & 0x0001) else 0)
# 顺便看看 GetKeyState 的说法
print("GetKeyState(VK_CAPITAL) =", hex(u.GetKeyState(VK_CAPITAL) & 0xFFFF))
print("返回值 bit0(切换态) =", u.GetKeyState(VK_CAPITAL) & 1)
