# -*- coding: utf-8 -*-
"""实测 Navigate2 到底收不收「真文件夹路径」、以及收下之后 LocationURL 什么时候变。

背景：TB 里「备用窗口导航复用」走的正是 IWebBrowser2::Navigate2。
日志显示 NavigateTo 返回成功（没打失败行），但 reveal 30 拍都读不到目标路径。

只读探针：起一个 explorer 窗口（会被 TB 收编或自己消失），
拿它的 ShellWindows 项发 Navigate2，然后每 100ms 读 LocationURL，看要多久才变。
"""
import ctypes, subprocess, time, sys
from ctypes import wintypes

TARGET = sys.argv[1] if len(sys.argv) > 1 else "D:\\Dev\\AI"

ole32 = ctypes.windll.ole32
ole32.CoInitialize(None)


class G(ctypes.Structure):
    _fields_ = [("d1", ctypes.c_ulong), ("d2", ctypes.c_ushort),
                ("d3", ctypes.c_ushort), ("d4", ctypes.c_ubyte * 8)]


def guid(s):
    from uuid import UUID
    x = UUID(s); g = G()
    g.d1, g.d2, g.d3 = x.time_low, x.time_mid, x.time_hi_version
    b = x.bytes
    for i in range(8):
        g.d4[i] = b[8 + i]
    return g


class V(ctypes.Structure):
    _fields_ = [("vt", ctypes.c_ushort), ("a", ctypes.c_ushort), ("b", ctypes.c_ushort),
                ("c", ctypes.c_ushort), ("p1", ctypes.c_void_p), ("p2", ctypes.c_void_p)]


ole32.CoCreateInstance.argtypes = [ctypes.POINTER(G), ctypes.c_void_p, wintypes.DWORD,
                                   ctypes.POINTER(G), ctypes.POINTER(ctypes.c_void_p)]
ole32.CoCreateInstance.restype = ctypes.c_long

NIL = G()


def shell_windows():
    p = ctypes.c_void_p()
    hr = ole32.CoCreateInstance(ctypes.byref(guid('{9BA05972-F6A8-11CF-A442-00A0C90A8F39}')),
                                None, 0x17,
                                ctypes.byref(guid('{00020400-0000-0000-C000-000000000046}')),
                                ctypes.byref(p))
    if hr != 0:
        print("ShellWindows hr=0x%08X" % (hr & 0xFFFFFFFF)); return None
    return p


def methods(p):
    vt = ctypes.cast(p, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p))).contents
    GIDN = ctypes.WINFUNCTYPE(ctypes.c_long, ctypes.c_void_p, ctypes.POINTER(G),
                              ctypes.POINTER(ctypes.c_wchar_p), ctypes.c_uint,
                              ctypes.c_uint, ctypes.POINTER(ctypes.c_long))(vt[5])
    INV = ctypes.WINFUNCTYPE(ctypes.c_long, ctypes.c_void_p, ctypes.c_long, ctypes.POINTER(G),
                             ctypes.c_uint, ctypes.c_ushort, ctypes.POINTER(V), ctypes.c_void_p,
                             ctypes.c_void_p, ctypes.c_uint, ctypes.POINTER(V))(vt[6])
    return GIDN, INV


def dispid(GIDN, p, name):
    nm = (ctypes.c_wchar_p * 1)(); nm[0] = name
    i = ctypes.c_long(-1)
    GIDN(p, ctypes.byref(NIL), nm, 1, 0x400, ctypes.byref(i))
    return i.value


def getprop(GIDN, INV, p, name):
    r = V()
    INV(p, dispid(GIDN, p, name), ctypes.byref(NIL), 0x400, 2, ctypes.byref(r), None, None, 0, None)
    return r


def item(GIDN, INV, p, i):
    nm = (ctypes.c_wchar_p * 1)(); nm[0] = "Item"
    outer = V(); inner = V()
    inner.vt = 3
    ctypes.memmove(ctypes.byref(inner, 8), ctypes.byref(ctypes.c_long(i)), 4)
    outer.vt = 0x400C          # VT_VARIANT|VT_BYREF
    outer.p1 = ctypes.cast(ctypes.byref(inner), ctypes.c_void_p)
    dn = dispid(GIDN, p, "Item")
    r = V()
    INV(p, dn, ctypes.byref(NIL), 0x400, 2, ctypes.byref(r), ctypes.byref(outer), None, 1, None)
    return r


def location_of(GIDN, INV, p):
    r = getprop(GIDN, INV, p, "LocationURL")
    if r.vt == 8 and r.p1:
        return ctypes.wstring_at(r.p1)
    return None


def hwnd_of(GIDN, INV, p):
    r = getprop(GIDN, INV, p, "HWND")
    return r.p1 if r.vt == 3 else None


# --- 1) 起一个窗口，记下它出现前后的 ShellWindows 快照 ---
root = shell_windows()
if not root:
    raise SystemExit
GIDN, INV = methods(root)
before = getprop(GIDN, INV, root, "Count").p1 or 0
print("先有 %d 个 ShellWindows 项" % before)

print("起 explorer 到「此电脑」…")
subprocess.Popen(["explorer.exe", "/n,/separate,shell:MyComputerFolder"])
time.sleep(4)

after = getprop(GIDN, INV, root, "Count").p1 or 0
print("现在 %d 个" % after)

target_item = None
for k in range(after - 1, max(-1, before - 3), -1):
    it = item(GIDN, INV, root, k)
    if it.vt != 9 or not it.p1:
        continue
    u = location_of(GIDN, INV, ctypes.c_void_p(it.p1))
    h = hwnd_of(GIDN, INV, ctypes.c_void_p(it.p1))
    print("  [%d] HWND=%s URL=%r" % (k, h, u))
    if u and "MyComputer" in u or (u and u.startswith("file:") and u.rstrip("/").endswith("AI")):
        pass
    if target_item is None and u and "mycomputer" in u.lower():
        target_item = it.p1

if target_item is None:
    # 退一步：取最后一个带 HWND 的
    for k in range(after - 1, max(-1, before - 3), -1):
        it = item(GIDN, INV, root, k)
        if it.vt == 9 and it.p1 and hwnd_of(GIDN, INV, ctypes.c_void_p(it.p1)):
            target_item = it.p1
            break

if target_item is None:
    print("找不到刚起的那扇窗"); raise SystemExit

d = ctypes.c_void_p(target_item)
dGIDN, dINV = methods(d)
print("\n选中项 URL =", location_of(dGIDN, dINV, d))

# --- 2) 发 Navigate2，看 hr 和 LocationURL 什么时候变 ---
dn = dispid(dGIDN, d, "Navigate2")
print("Navigate2 dispid =", dn)

url = ctypes.create_unicode_buffer(TARGET)
inner = V(); inner.vt = 8; inner.p1 = ctypes.cast(url, ctypes.c_void_p)
outer = V(); outer.vt = 0x400C; outer.p1 = ctypes.cast(ctypes.byref(inner), ctypes.c_void_p)
r = V()
hr = dINV(d, dn, ctypes.byref(NIL), 0x400, 1, ctypes.byref(r), ctypes.byref(outer), None, 1, None)
print("Navigate2(%r) hr=0x%08X" % (TARGET, hr & 0xFFFFFFFF))

t0 = time.time()
for n in range(60):
    u = location_of(dGIDN, dINV, d)
    print("  +%4dms URL=%r" % (int((time.time() - t0) * 1000), u))
    if u and TARGET.replace("\\", "/").lower() in u.replace("\\", "/").lower():
        print("  -> 到站")
        break
    time.sleep(0.1)
