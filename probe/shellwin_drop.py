"""只读 / 只改一个属性 的探针：shell 的「浏览器窗口清单」(ShellWindows) 里有没有我们的窗。

2026-09-24 抓「三方程序点打开文件夹，已有标签就毫无反应」这条 bug 用的：
  · 事实：目标文件夹已经有窗口（我们收编成标签了）时，三方打开它 = 系统里零新窗口、零事件，
    我们压根收不到通知 ⇒ 标签不会切。没有那扇窗时，shell 才会新建一扇 ⇒ 我们才收得到。
  · 假设：shell 是靠这份清单判断「这个文件夹已经开着」的，找到就只激活不新建。
  · 本探针：① list 把清单和我们嵌进来的窗一一对上（inShell=YES/no）；
            ② drop 把某个窗从清单里注销（IWebBrowser2::RegisterAsBrowser = FALSE），
               再去看三方打开它时会不会恢复成「新建一扇」。
纯 ctypes + 手搓 IDispatch（本机没有 pywin32/comtypes）。

用法：
    python shellwin_drop.py list                 只列
    python shellwin_drop.py drop <hwnd 十六进制>  注销某一个
    python shellwin_drop.py drop_embedded        注销我们所有已嵌入的 CabinetWClass 窗
"""
import ctypes
import ctypes.wintypes as wt
import sys
from ctypes import POINTER, byref, c_void_p, c_uint, c_long, c_ushort, c_byte

ole32 = ctypes.windll.ole32
user32 = ctypes.windll.user32

COINIT_APARTMENTTHREADED = 0x2
CLSCTX_LOCAL_SERVER = 0x4
CLSCTX_INPROC_SERVER = 0x1
DISPATCH_METHOD = 0x1
DISPATCH_PROPERTYGET = 0x2
DISPATCH_PROPERTYPUT = 0x4
LOCALE_USER_DEFAULT = 0x400
VT_EMPTY, VT_I4, VT_BSTR, VT_DISPATCH, VT_VARIANT, VT_BOOL, VT_UNKNOWN = 0, 3, 8, 9, 12, 11, 13
VT_BYREF = 0x4000
DISPID_NEWENUM = -4
DISPID_PROPERTYPUT = -3


class GUID(ctypes.Structure):
    _fields_ = [("d1", ctypes.c_ulong), ("d2", ctypes.c_ushort),
                ("d3", ctypes.c_ushort), ("d4", c_byte * 8)]


def guid(s):
    g = GUID()
    ole32.CLSIDFromString(ctypes.c_wchar_p(s), byref(g))
    return g


class VARIANT(ctypes.Structure):
    class _U(ctypes.Union):
        _fields_ = [("llVal", ctypes.c_longlong), ("lVal", c_long),
                    ("wVal", c_ushort), ("bstrVal", c_void_p),
                    ("punkVal", c_void_p), ("pdispVal", c_void_p)]
    _fields_ = [("vt", c_ushort), ("r1", c_ushort), ("r2", c_ushort), ("r3", c_ushort),
                ("u", _U)]


class DISPPARAMS(ctypes.Structure):
    _fields_ = [("rgvarg", POINTER(VARIANT)), ("rgdispidNamedArgs", c_void_p),
                ("cArgs", c_uint), ("cNamedArgs", c_uint)]


class EXCEPINFO(ctypes.Structure):
    _fields_ = [("wCode", c_ushort), ("wReserved", c_ushort),
                ("bstrSource", c_void_p), ("bstrDescription", c_void_p),
                ("bstrHelpFile", c_void_p), ("dwHelpContext", ctypes.c_ulong),
                ("pvReserved", c_void_p), ("pfnDeferredFillIn", c_void_p),
                ("scode", c_long)]


def vcall(p, index, restype, *argtypes):
    vtbl = ctypes.cast(p, POINTER(c_void_p))[0]
    fn = ctypes.cast(ctypes.cast(vtbl, POINTER(c_void_p))[index],
                     ctypes.WINFUNCTYPE(restype, c_void_p, *argtypes))
    return fn


_IID_NULL = GUID()


def get_dispid(p, name):
    dispid = c_long(-999999)
    nm = ctypes.c_wchar_p(name)
    hr = vcall(p, 5, c_long, POINTER(GUID), POINTER(ctypes.c_wchar_p),
               c_uint, ctypes.c_ulong, POINTER(c_long))(
        p, byref(_IID_NULL), byref(nm), 1, LOCALE_USER_DEFAULT, byref(dispid))
    return (dispid.value if hr == 0 else None), hr


def invoke(p, dispid, wflags, args=(), named=None, want_result=True):
    """args: VARIANT 数组（按调用顺序给，内部反转）；named: dispid 列表（属性赋值用）。"""
    arr = (VARIANT * max(1, len(args)))()
    for i, a in enumerate(reversed(args)):
        arr[i] = a
    na = (c_long * max(1, len(named or [])))()
    for i, d in enumerate(named or []):
        na[i] = d
    dp = DISPPARAMS()
    dp.rgvarg = ctypes.cast(arr, POINTER(VARIANT))
    dp.rgdispidNamedArgs = ctypes.cast(na, c_void_p) if named else None
    dp.cArgs = len(args)
    dp.cNamedArgs = len(named or [])
    res = VARIANT()
    ei = EXCEPINFO()
    err = c_uint()
    hr = vcall(p, 6, c_long, c_long, POINTER(GUID), ctypes.c_ulong, c_ushort,
               POINTER(DISPPARAMS), POINTER(VARIANT), POINTER(EXCEPINFO), POINTER(c_uint))(
        p, dispid, byref(_IID_NULL), LOCALE_USER_DEFAULT, wflags,
        byref(dp), byref(res) if want_result else None, byref(ei), byref(err))
    return hr, res, ei


def vint(i):
    v = VARIANT()
    v.vt = VT_I4
    v.u.lVal = i
    return v


def vbool(b):
    v = VARIANT()
    v.vt = VT_BOOL
    v.u.lVal = -1 if b else 0
    return v


_oleaut = ctypes.windll.oleaut32
_oleaut.SysAllocString.argtypes = [ctypes.c_wchar_p]
_oleaut.SysAllocString.restype = c_void_p


def vbstr(s):
    v = VARIANT()
    v.vt = VT_BSTR
    v.u.bstrVal = _oleaut.SysAllocString(ctypes.c_wchar_p(s))
    return v


def bstr(v):
    return ctypes.wstring_at(v.u.bstrVal) if v.u.bstrVal else ""


def vref(v):
    """按 OLE Automation 的规矩：形参是 VARIANT（按值）时，rgvarg 里要放 VT_VARIANT|VT_BYREF。"""
    out = VARIANT()
    out.vt = VT_VARIANT | VT_BYREF
    out.u.punkVal = ctypes.cast(ctypes.pointer(v), c_void_p)
    return out


def shell_windows():
    """返回 [(disp_ptr, hwnd, url, name)] —— shell 不支持 DISPID_NEWENUM，只能 Count + Item。"""
    clsid = guid("{9BA05972-F6A8-11CF-A442-00A0C90A8F39}")
    iid_disp = guid("{00020400-0000-0000-C000-000000000046}")
    p = c_void_p()
    hr = ole32.CoCreateInstance(byref(clsid), None, CLSCTX_LOCAL_SERVER | CLSCTX_INPROC_SERVER,
                               byref(iid_disp), byref(p))
    if hr != 0 or not p.value:
        print("CoCreateInstance(ShellWindows) hr=0x%08X" % (hr & 0xFFFFFFFF))
        return []
    cd, hr = get_dispid(p, "Count")
    if cd is None:
        print("Count 取不到 hr=0x%08X" % (hr & 0xFFFFFFFF))
        return []
    hr, cv, ei = invoke(p, cd, DISPATCH_PROPERTYGET)
    if hr != 0 or cv.vt != VT_I4:
        print("Count hr=0x%08X vt=%d" % (hr & 0xFFFFFFFF, cv.vt))
        return []
    total = cv.u.lVal
    idm, _ = get_dispid(p, "Item")
    out = []
    for i in range(total):
        hr, item, ei = invoke(p, idm, DISPATCH_METHOD, args=[vref(vint(i))])
        q = item.u.pdispVal or item.u.punkVal
        if hr != 0 or not q:
            continue
        hd, _ = get_dispid(q, "HWND")
        hv = VARIANT()
        if hd is not None:
            _, hv, _ = invoke(q, hd, DISPATCH_PROPERTYGET)
        ud, _ = get_dispid(q, "LocationURL")
        uv = VARIANT()
        if ud is not None:
            _, uv, _ = invoke(q, ud, DISPATCH_PROPERTYGET)
        nd, _ = get_dispid(q, "LocationName")
        nv = VARIANT()
        if nd is not None:
            _, nv, _ = invoke(q, nd, DISPATCH_PROPERTYGET)
        out.append((q, hv.u.lVal if hv.vt == VT_I4 else None,
                    bstr(uv) if uv.vt == VT_BSTR else "?",
                    bstr(nv) if nv.vt == VT_BSTR else "?"))
    return out


def embedded_cabinet_windows():
    """我们收编进来的那批（parent != 0 的 CabinetWClass）。
    ⚠ EnumChildWindows 本来就是递归的，回调里**不能**再调一次（会指数级重复）。"""
    found = []
    seen = set()

    @ctypes.WINFUNCTYPE(ctypes.c_bool, c_void_p, ctypes.c_void_p)
    def child_cb(h, l):
        cls = ctypes.create_unicode_buffer(256)
        user32.GetClassNameW(h, cls, 256)
        if cls.value in ("CabinetWClass", "ExploreWClass") and int(h) not in seen:
            seen.add(int(h))
            found.append(int(h))
        return True

    @ctypes.WINFUNCTYPE(ctypes.c_bool, c_void_p, ctypes.c_void_p)
    def top_cb(h, l):
        user32.EnumChildWindows(h, child_cb, None)
        return True

    user32.EnumWindows(top_cb, None)
    return found


def drop_one(disp_ptr, hwnd):
    did, hr = get_dispid(disp_ptr, "RegisterAsBrowser")
    if did is None:
        print("  GetIDsOfNames(RegisterAsBrowser) hr=0x%08X" % (hr & 0xFFFFFFFF))
        return False
    hr, _, ei = invoke(disp_ptr, did, DISPATCH_PROPERTYPUT,
                       args=[vbool(False)], named=[DISPID_PROPERTYPUT], want_result=False)
    print("  注销 cab=0x%X -> hr=0x%08X%s" % (
        hwnd, hr & 0xFFFFFFFF, "" if hr == 0 else " scode=0x%08X" % (ei.scode & 0xFFFFFFFF)))
    return hr == 0


def main():
    ole32.CoInitializeEx(None, COINIT_APARTMENTTHREADED)
    mode = sys.argv[1] if len(sys.argv) > 1 else "list"
    wins = shell_windows()
    print("ShellWindows 清单：%d 个" % len(wins))
    ours = embedded_cabinet_windows()
    by_hwnd = dict((h, (q, u, n)) for q, h, u, n in wins if h)
    for q, h, u, n in wins:
        mark = "  <== 我们嵌进来的" if h in ours else ""
        print("  hwnd=%-12s url=%-52s name=%s%s" % (
            hex(h) if h else None, u[:52], n[:18], mark))
    print("已嵌入的 CabinetWClass：%s" % [hex(x) for x in ours])

    if mode == "list":
        return
    if mode == "drop_url":
        key = sys.argv[2].lower()
        hit = [(h, q) for q, h, u, n in wins if key in u.lower()]
        print("按 url 命中 %d 个（%s）" % (len(hit), sys.argv[2]))
        for h, q in hit:
            drop_one(q, h or 0)
        print("再看一遍清单：%d 个" % len(shell_windows()))
        return
    if mode == "putb":
        # putb <url 子串> <属性名> <0|1> —— 通用 BOOL 属性写，用来对照「是我的调用姿势不对，还是 explorer 不让改」
        key, prop, val = sys.argv[2].lower(), sys.argv[3], sys.argv[4] == "1"
        for q, h, u, n in wins:
            if key not in u.lower():
                continue
            did, hr = get_dispid(q, prop)
            if did is None:
                print("  GetIDsOfNames(%s) hr=0x%08X" % (prop, hr & 0xFFFFFFFF))
                continue
            hr, _, ei = invoke(q, did, DISPATCH_PROPERTYPUT, args=[vbool(val)],
                               named=[DISPID_PROPERTYPUT], want_result=False)
            print("  put %s=%s on %s -> hr=0x%08X scode=0x%08X" % (
                prop, val, u, hr & 0xFFFFFFFF, ei.scode & 0xFFFFFFFF))
        return
    if mode == "getp":
        # getp <url 子串> <属性名> —— 探某个属性到底存不存在、现在是什么（用来判 LocationURL 能不能写）
        key, prop = sys.argv[2].lower(), sys.argv[3]
        for q, h, u, n in wins:
            if key not in u.lower():
                continue
            did, hr = get_dispid(q, prop)
            if did is None:
                print("  GetIDsOfNames(%s) hr=0x%08X  <== 这个属性不存在" % (prop, hr & 0xFFFFFFFF))
                continue
            hr, rv, ei = invoke(q, did, DISPATCH_PROPERTYGET)
            val = bstr(rv) if rv.vt == VT_BSTR else "vt=%d" % rv.vt
            print("  get %s on %s -> hr=0x%08X 值=%s" % (prop, u, hr & 0xFFFFFFFF, val))
        return
    if mode == "puts":
        # puts <url 子串> <属性名> <字符串值> —— 试写字符串属性（LocationURL / LocationName）
        key, prop, val = sys.argv[2].lower(), sys.argv[3], sys.argv[4]
        for q, h, u, n in wins:
            if key not in u.lower():
                continue
            did, hr = get_dispid(q, prop)
            if did is None:
                print("  GetIDsOfNames(%s) hr=0x%08X  <== 这个属性不存在，写不了" % (prop, hr & 0xFFFFFFFF))
                continue
            hr, _, ei = invoke(q, did, DISPATCH_PROPERTYPUT, args=[vbstr(val)],
                               named=[DISPID_PROPERTYPUT], want_result=False)
            print("  put %s=%r on %s -> hr=0x%08X scode=0x%08X" % (
                prop, val, u, hr & 0xFFFFFFFF, ei.scode & 0xFFFFFFFF))
            hd, _ = get_dispid(q, prop)
            if hd is not None:
                _, gv, _ = invoke(q, hd, DISPATCH_PROPERTYGET)
                print("     写完再读：%s" % (bstr(gv) if gv.vt == VT_BSTR else "vt=%d" % gv.vt))
        print("再看一遍清单：")
        for q, h, u, n in shell_windows():
            print("  url=%-56s name=%s" % (u[:56], n[:18]))
        return
    targets = ours if mode == "drop_embedded" else [int(sys.argv[2], 16)]
    for h in targets:
        if h not in by_hwnd:
            print("  cab=0x%X 不在清单里（没登记？）" % h)
            continue
        print("cab=0x%X url=%s" % (h, by_hwnd[h][1]))
        drop_one(by_hwnd[h][0], h)
    print("再看一遍清单：%d 个" % len(shell_windows()))


main()
