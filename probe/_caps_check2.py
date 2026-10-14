# -*- coding: utf-8 -*-
"""读 AHK 的 KeyDown 表 vs GetAsyncKeyState —— 只读。"""
import ctypes
u = ctypes.windll.user32
u.GetAsyncKeyState.restype = ctypes.c_short
u.GetAsyncKeyState.argtypes=[ctypes.c_int]
u.GetKeyState.restype = ctypes.c_short
u.GetKeyState.argtypes=[ctypes.c_int]
VK=0x14
print("GetAsyncKeyState(0x14) =", hex(u.GetAsyncKeyState(VK)&0xFFFF))
print("GetKeyState(0x14)      =", hex(u.GetKeyState(VK)&0xFFFF))
print("AHK KeyDown 表 (= 钩子内部维护) 得问 AHK 自己，这里只列 Win32 两法")
# 附带：AHK 还有一个办法 —— 用挂在自己身上的 GetKeyboardState
buf = (ctypes.c_ubyte*256)()
u.GetKeyboardState(ctypes.byref(buf))
print("GetKeyboardState[0x14] =", buf[VK], "(0x80=按下)")
