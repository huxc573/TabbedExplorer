# -*- coding: utf-8 -*-
"""验证完整链路：此电脑 → EnumObjects 找设备 → BindToObject → 逐级 ParseDisplayName
→ 得到目标目录的**绝对 PIDL**。只读：不打开窗口、不碰前台。

若这一步通过，「点书签开 MTP 标签」就有了唯一可行实现：
  枚举找设备（ParseDisplayName 在设备那一层之后就能用了，因为设备自己是 IShellFolder）
"""
import ctypes
from ctypes import wintypes, byref, POINTER, c_void_p

shell32 = ctypes.windll.shell32
ole32 = ctypes.windll.ole32
ole32.CoInitialize(None)

class _GUID(ctypes.Structure):
    _fields_ = [("d1", ctypes.c_ulong), ("d2", ctypes.c_ushort),
                ("d3", ctypes.c_ushort), ("d4", ctypes.c_ubyte * 8)]

def guid(s):
    from uuid import UUID
    x = UUID(s); g = _GUID()
    g.d1, g.d2, g.d3 = x.time_low, x.time_mid, x.time_hi_version
    b = x.bytes
    for i in range(8): g.d4[i] = b[8 + i]
    return g

IID_IShellFolder = guid("{000214E6-0000-0000-C000-000000000046}")
IID_IShellFolder2 = guid("{93F2F68C-1D1B-11D3-A30E-00C04F79ABD1}")

shell32.SHParseDisplayName.argtypes = [wintypes.LPCWSTR, c_void_p,
                                       POINTER(c_void_p), wintypes.ULONG,
                                       POINTER(wintypes.ULONG)]
shell32.SHParseDisplayName.restype = ctypes.c_long
shell32.SHBindToObject.argtypes = [c_void_p, c_void_p, c_void_p,
                                   POINTER(_GUID), POINTER(c_void_p)]
shell32.SHBindToObject.restype = ctypes.c_long
shell32.ILCombine.argtypes = [c_void_p, c_void_p]
shell32.ILCombine.restype = ctypes.c_void_p
shell32.ILFree.argtypes = [c_void_p]
shell32.ILFree.restype = None
shell32.SHGetNameFromIDList.argtypes = [c_void_p, ctypes.c_int, POINTER(c_void_p)]
shell32.SHGetNameFromIDList.restype = ctypes.c_long
ole32.CoTaskMemFree.argtypes = [c_void_p]
ole32.CoTaskMemFree.restype = None

def slot(p, i):
    vt = ctypes.cast(p, POINTER(POINTER(c_void_p))).contents
    return vt[i]

def release(p):
    if p:
        ctypes.WINFUNCTYPE(ctypes.c_ulong, c_void_p)(slot(p, 2))(p)

def name_of(pidl, sigdn=0x80028000):   # DESKTOPABSOLUTEPARSING
    p = c_void_p()
    if shell32.SHGetNameFromIDList(pidl, sigdn, byref(p)) == 0 and p.value:
        v = ctypes.wstring_at(p.value)
        ole32.CoTaskMemFree(p)
        return v
    return None

def parse_into(sf, text):
    """IShellFolder::ParseDisplayName（vtable 3）—— 返回相对 PIDL。"""
    fn = ctypes.WINFUNCTYPE(ctypes.c_long, c_void_p, c_void_p, wintypes.LPCWSTR,
                            POINTER(ctypes.c_ulong), POINTER(c_void_p),
                            ctypes.c_void_p)(slot(sf, 3))
    eaten = ctypes.c_ulong(0)
    out = c_void_p()
    hr = fn(sf, None, text, byref(eaten), byref(out), None)
    return hr, out, eaten.value

def bind(sf, pidl):
    out = c_void_p()
    hr = shell32.SHBindToObject(sf, pidl, None, byref(IID_IShellFolder), byref(out))
    return hr, out

TARGET = "motorola edge 20 pro\\内部共享存储空间\\!573\\APPS\\Eta\\projects\\los-enhance\\refs\\official-rom"

# ① 此电脑
pidl_pc = c_void_p()
hr = shell32.SHParseDisplayName("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", None,
                                byref(pidl_pc), 0, None)
assert hr == 0, "此电脑解析失败"

sf_pc = c_void_p()
hr = shell32.SHBindToObject(None, pidl_pc, None, byref(IID_IShellFolder), byref(sf_pc))
assert hr == 0, "此电脑绑定失败"
print("① 此电脑 IShellFolder ok")

# ② 枚举找设备
ppenum = c_void_p()
EnumObjects = ctypes.WINFUNCTYPE(ctypes.c_long, c_void_p, c_void_p,
                                 wintypes.DWORD, POINTER(c_void_p))(slot(sf_pc, 4))
hr = EnumObjects(sf_pc, None, 0x20 | 0x40 | 0x80, byref(ppenum))
Next = ctypes.WINFUNCTYPE(ctypes.c_long, c_void_p, ctypes.c_ulong,
                          POINTER(c_void_p), POINTER(ctypes.c_ulong))(slot(ppenum, 3))
dev_rel = None
while True:
    one = c_void_p(); got = ctypes.c_ulong(0)
    if Next(ppenum, 1, byref(one), byref(got)) != 0 or got.value == 0:
        break
    a = shell32.ILCombine(pidl_pc, one)
    nm = name_of(a, 0x00000000)            # NORMALDISPLAY
    shell32.ILFree(a)
    if nm and "motorola" in nm.lower():
        dev_rel = ctypes.string_at(one.value, ctypes.cast(
            one, POINTER(ctypes.c_ushort)).contents.value)
        print("② 找到设备：%r  相对PIDL %d 字节" % (nm, len(dev_rel)))
        break
release(ppenum)
assert dev_rel, "没找到设备"

# ③ 设备 PIDL = 此电脑绝对 PIDL + 设备相对 PIDL
dev_abs = None
buf = ctypes.create_string_buffer(dev_rel)
tmp = ctypes.cast(buf, c_void_p)
# 用 ILCombine 更稳（但 one 已经被释放了）—— 重新枚举一次拿它
ppenum2 = c_void_p()
EnumObjects(sf_pc, None, 0x20 | 0x40 | 0x80, byref(ppenum2))
Next2 = ctypes.WINFUNCTYPE(ctypes.c_long, c_void_p, ctypes.c_ulong,
                           POINTER(c_void_p), POINTER(ctypes.c_ulong))(slot(ppenum2, 3))
while True:
    one = c_void_p(); got = ctypes.c_ulong(0)
    if Next2(ppenum2, 1, byref(one), byref(got)) != 0 or got.value == 0:
        break
    a = shell32.ILCombine(pidl_pc, one)
    nm = name_of(a, 0x00000000)
    if nm and "motorola" in nm.lower():
        dev_abs = a
        break
    shell32.ILFree(a)
release(ppenum2)
assert dev_abs, "拿不到设备绝对 PIDL"
print("③ 设备绝对 PIDL =", name_of(dev_abs, 0x80028000))

# ④ 绑到设备的 IShellFolder，再逐级解析
#    ⚠ 这里必须用**桌面**的 IShellFolder 绑绝对 PIDL，不是「此电脑」的
sf_desk = c_void_p()
hr = shell32.SHGetDesktopFolder(byref(sf_desk)) if hasattr(shell32, "SHGetDesktopFolder") else (0, None)
if not sf_desk.value:
    shell32.SHBindToObject.argtypes = [c_void_p, c_void_p, c_void_p,
                                       POINTER(_GUID), POINTER(c_void_p)]
    # 桌面的 PIDL 是空（NULL），用 NULL+NULL 绑不到；改走 SHBindToObject(None, 空pidl)
    hr, sf_desk = bind(None, None)
print("  桌面 folder =", hex(sf_desk.value or 0))
hr, sf_dev = bind(sf_desk, dev_abs)
print("④ 绑定设备 hr=0x%08X" % (hr & 0xFFFFFFFF))
if hr != 0:
    # 退路：直接对设备绝对 PIDL 走 SHParseDisplayName 拿它自己的 IShellFolder
    h2, sf_try = 0, c_void_p()
    print("   退路：试 SHBindToObject(此电脑 folder, dev_abs) …… 已试过；"
          "改试 SHCreateItemFromIDList 路径")
assert hr == 0, "设备绑定失败"

cur_sf = sf_dev
cur_abs = dev_abs
segs = TARGET.split("\\")[1:]          # 跳过设备名本身
for s in segs:
    hr, rel, eaten = parse_into(cur_sf, s)
    if hr != 0:
        print("   ✗ 解析 %r 失败 hr=0x%08X（吃了 %d 字符）" % (s, hr & 0xFFFFFFFF, eaten))
        break
    new_abs = shell32.ILCombine(cur_abs, rel)
    print("   ✓ %-42r -> %s" % (s, name_of(new_abs, 0x80028000)))
    shell32.ILFree(rel)
    if cur_abs != dev_abs:
        shell32.ILFree(cur_abs)
    cur_abs = new_abs
    if s != segs[-1]:
        hr2, nxt = bind(cur_sf, cur_abs)
        if hr2 != 0:
            print("   ✗ 绑定 %r 失败 hr=0x%08X" % (s, hr2 & 0xFFFFFFFF))
            break
        if cur_sf != sf_dev:
            release(cur_sf)
        cur_sf = nxt

print()
print("★ 目标绝对 PIDL =", name_of(cur_abs, 0x80028000))
print("   这串就是交给 IWebBrowser2::Navigate2 的东西（按 PIDL 传，不是文本）。")
