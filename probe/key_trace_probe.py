# -*- coding: utf-8 -*-
"""只读探针 v3：记录所有 CapsLock / Space 的 KeyDown/Up 事件，看谁吞了。

跑起来后：按住 CapsLock 按空格；再单按 CapsLock；再在记事本里同样操作。
"""
import ctypes, ctypes.wintypes as w, time, io, os

u = ctypes.windll.user32
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "_mod3_out.txt")
f = io.open(OUT, "w", encoding="utf-8", buffering=1)

WH_KEYBOARD_LL = 13
WM_KEYDOWN, WM_KEYUP, WM_SYSKEYDOWN, WM_SYSKEYUP = 0x100, 0x101, 0x104, 0x105
VK_CAP, VK_SPACE = 0x14, 0x20
LLKHF_INJECTED = 0x10

class KBDLLHOOKSTRUCT(ctypes.Structure):
    _fields_ = [("vkCode", ctypes.c_ulong), ("scanCode", ctypes.c_ulong),
                ("flags", ctypes.c_ulong), ("time", ctypes.c_ulong),
                ("dwExtraInfo", ctypes.POINTER(ctypes.c_ulong))]

t0 = time.time()
HOOKPROC = ctypes.WINFUNCTYPE(ctypes.c_void_p, ctypes.c_int, ctypes.c_void_p, ctypes.c_void_p)

def cb(nCode, wParam, lParam):
    if nCode >= 0:
        m = wParam
        if m in (WM_KEYDOWN, WM_KEYUP, WM_SYSKEYDOWN, WM_SYSKEYUP):
            st = ctypes.cast(lParam, ctypes.POINTER(KBDLLHOOKSTRUCT)).contents
            vk = st.vkCode
            if vk in (VK_CAP, VK_SPACE):
                nm = {VK_CAP: "CAPS ", VK_SPACE: "SPACE"}[vk]
                act = "DN" if m in (WM_KEYDOWN, WM_SYSKEYDOWN) else "UP"
                inj = "INJ" if (st.flags & LLKHF_INJECTED) else "   "
                f.write("%7.2fs %s %s %s scan=0x%02x\n"
                        % (time.time() - t0, nm, act, inj, st.scanCode))
    return u.CallNextHookEx(None, nCode, wParam, lParam)

hp = HOOKPROC(cb)
hh = u.SetWindowsHookExW(WH_KEYBOARD_LL, hp, None, 0)
f.write("hook=%s 就绪。操作：①按住CapsLock按空格 ②单按CapsLock\n" % hex(hh))

msg = w.MSG()
while u.GetMessageW(ctypes.byref(msg), None, 0, 0) != 0:
    u.TranslateMessage(ctypes.byref(msg)); u.DispatchMessageW(ctypes.byref(msg))
