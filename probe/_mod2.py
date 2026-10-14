# -*- coding: utf-8 -*-
"""只读探针 v2：用低级键盘钩子抓「空格按下」那一刻的修饰键快照，实时落盘。"""
import ctypes, ctypes.wintypes as w, time, io, os

u = ctypes.windll.user32
k = ctypes.windll.kernel32
u.GetAsyncKeyState.restype = ctypes.c_short
u.GetAsyncKeyState.argtypes = [ctypes.c_int]

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "_mod2_out.txt")
f = io.open(OUT, "w", encoding="utf-8", buffering=1)

VK = {"CAPS":0x14,"CTRL":0x11,"ALT":0x12,"SHIFT":0x10,"LWIN":0x5B,"RWIN":0x5C,"SPACE":0x20,"A":0x41}
def dn(v): return 1 if (u.GetAsyncKeyState(v) & 0x8000) else 0

WH_KEYBOARD_LL = 13
WM_KEYDOWN, WM_SYSKEYDOWN = 0x100, 0x104
class KBDLLHOOKSTRUCT(ctypes.Structure):
    _fields_ = [("vkCode", ctypes.c_ulong), ("scanCode", ctypes.c_ulong),
                ("flags", ctypes.c_ulong), ("time", ctypes.c_ulong),
                ("dwExtraInfo", ctypes.POINTER(ctypes.c_ulong))]

t0 = time.time()
HOOKPROC = ctypes.WINFUNCTYPE(ctypes.c_void_p, ctypes.c_int, ctypes.c_void_p, ctypes.c_void_p)
def cb(nCode, wParam, lParam):
    if nCode >= 0 and wParam in (WM_KEYDOWN, WM_SYSKEYDOWN):
        st = ctypes.cast(lParam, ctypes.POINTER(KBDLLHOOKSTRUCT)).contents
        vk = st.vkCode
        if vk == VK["SPACE"]:
            f.write("%6.2fs SPACE↓ caps=%d ctrl=%d alt=%d shift=%d lwin=%d rwin=%d\n"
                    % (time.time()-t0, dn(0x14), dn(0x11), dn(0x12), dn(0x10),
                       dn(0x5B), dn(0x5C)))
        elif vk == VK["CAPS"]:
            f.write("%6.2fs CAPS↓  caps=%d\n" % (time.time()-t0, dn(0x14)))
    return u.CallNextHookEx(None, nCode, wParam, lParam)

hp = HOOKPROC(cb)
hh = u.SetWindowsHookExW(WH_KEYBOARD_LL, hp, None, 0)
f.write("hook=%s  就绪，请按 CapsLock(按住)+空格\n" % hex(hh))

msg = w.MSG()
while u.GetMessageW(ctypes.byref(msg), None, 0, 0) != 0:
    u.TranslateMessage(ctypes.byref(msg)); u.DispatchMessageW(ctypes.byref(msg))
