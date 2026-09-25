# -*- coding: utf-8 -*-
"""探针：对一扇**已经 SetParent 到我们宿主上**的 explorer 窗口，能不能跨进程 Navigate2 到别的目录。

要回答的问题（书签提速那条路的可行性）：备用窗口现在是「预热好但只认此电脑」。
如果能让它**直接导航到用户点的那个书签目标**，就省掉「起进程 + 建窗 + 找窗」这三块
（实测 0.24~1.28 秒）—— 书签 0.73~1.75 秒有望降到 0.2~0.4 秒。

链路：ShellWindows 清单里那扇窗的 IDispatch → IWebBrowser2::Navigate2。
逐条量的风险：
  ① 对**已 SetParent / 已改样式位（WS_CHILD）**的窗还灵不灵？
  ② 导航期间窗会不会被**销毁重建**（换 hwnd / 换 explorer 进程）？换了我们的嵌入就白做。
  ③ 导航一次要多久（对照：起一个新 explorer 进程 0.24~1.28 秒）。
  ④ 导航时窗的可见位 / 父窗会不会被翻动（会不会露脸）。

做法（对照实验，同一扇窗走三步）：
  A 顶层（被我们藏住）时导航一次  → 基线：这条通道本身通不通
  B SetParent 到我们的宿主 + 改 WS_CHILD + 摘 APPWINDOW 打 TOOLWINDOW
  C 再导航一次                     → 真问题：**嵌入之后**还能不能导航
每一步都记 hr / 耗时 / 导航后 LocationURL / 地址栏 / hwnd 是否变 / pid 是否变 / 父窗是否变 / 可见位。

⚠ 本机 `CreateDesktopW` 被全局安全策略硬拦（err 998/1305，挂计划任务也一样），开不了隐藏桌面
   ⇒ 退而用**程序自己那套**：`SetWinEventHook(EVENT_OBJECT_CREATE)` 在窗口诞生的那一刻就
   `SW_HIDE`（跟 DesktopHub 防闪同一招），宿主窗自己从不 SHOW。全程不动鼠标、不抢前台、
   不调 SetForegroundWindow。收尾关掉测试窗 + 杀掉本次新起的 explorer。
⚠ 钩子按「进程不在起跑基线里」过滤 —— 只可能命中我们自己 `explorer.exe /n,/separate` 起的那扇，
   桌面 shell（老进程）与你在探针跑的那 20 秒里打开的文件夹（复用老进程）都不会被它碰。
⚠ 计时注意：ShellWindows 全系统 251 项，扫一遍要上百毫秒 ⇒ **只扫一次**拿到 disp，
   之后轮询只读那一个属性（一次跨进程属性读），别每 tick 重扫。

用法：python navigate_reuse_probe.py [源目录] [目标1] [目标2]
"""
import ctypes
import ctypes.wintypes as wt
import os
import sys
import time
import urllib.parse
from ctypes import POINTER, byref, c_byte, c_int, c_long, c_uint, c_ushort, c_void_p

try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    pass

u = ctypes.windll.user32
k = ctypes.windll.kernel32
ole32 = ctypes.windll.ole32
oleaut = ctypes.windll.oleaut32

u.EnumWindows.argtypes = [ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM), wt.LPARAM]
u.EnumChildWindows.argtypes = [wt.HWND, ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM), wt.LPARAM]
u.GetClassNameW.argtypes = [wt.HWND, wt.LPWSTR, c_int]
u.GetWindowTextW.argtypes = [wt.HWND, wt.LPWSTR, c_int]
u.GetWindowTextLengthW.argtypes = [wt.HWND]
u.GetWindowThreadProcessId.argtypes = [wt.HWND, POINTER(wt.DWORD)]
u.ShowWindow.argtypes = [wt.HWND, c_int]
u.PostMessageW.argtypes = [wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM]
u.IsWindow.argtypes = [wt.HWND]
u.IsWindowVisible.argtypes = [wt.HWND]
u.SendMessageW.argtypes = [wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM]
u.GetParent.argtypes = [wt.HWND]
u.GetParent.restype = wt.HWND
u.GetWindowLongW.argtypes = [wt.HWND, c_int]
u.GetWindowLongW.restype = c_long
u.SetWindowLongW.argtypes = [wt.HWND, c_int, c_long]
u.SetWindowLongW.restype = c_long
u.SetParent.argtypes = [wt.HWND, wt.HWND]
u.SetParent.restype = wt.HWND
u.CreateWindowExW.argtypes = [wt.DWORD, wt.LPCWSTR, wt.LPCWSTR, wt.DWORD,
                              c_int, c_int, c_int, c_int, wt.HWND, wt.HMENU, wt.HINSTANCE, c_void_p]
u.CreateWindowExW.restype = wt.HWND
u.PeekMessageW.argtypes = [POINTER(wt.MSG), wt.HWND, wt.UINT, wt.UINT, wt.UINT]
u.TranslateMessage.argtypes = [POINTER(wt.MSG)]
u.DispatchMessageW.argtypes = [POINTER(wt.MSG)]
u.SetWinEventHook.argtypes = [wt.DWORD, wt.DWORD, wt.HMODULE, c_void_p, wt.DWORD, wt.DWORD, wt.DWORD]
u.SetWinEventHook.restype = wt.HANDLE
u.UnhookWinEvent.argtypes = [wt.HANDLE]
k.GetModuleHandleW.argtypes = [wt.LPCWSTR]
k.GetModuleHandleW.restype = wt.HINSTANCE
k.CreateToolhelp32Snapshot.argtypes = [wt.DWORD, wt.DWORD]
k.CreateToolhelp32Snapshot.restype = wt.HANDLE
k.OpenProcess.argtypes = [wt.DWORD, wt.BOOL, wt.DWORD]
k.OpenProcess.restype = wt.HANDLE
k.TerminateProcess.argtypes = [wt.HANDLE, wt.UINT]
k.CloseHandle.argtypes = [wt.HANDLE]

CAB = ("CabinetWClass", "ExploreWClass")
WM_CLOSE, WM_GETTEXT = 0x0010, 0x000D
SW_HIDE = 0
PM_REMOVE = 1
POLL_MS = 25
BUDGET_S = 20.0
NAV_BUDGET_S = 10.0
GWL_STYLE, GWL_EXSTYLE = -16, -20
WS_CHILD = 0x40000000
WS_POPUP_MASK = 0x80000000
WS_VISIBLE = 0x10000000
WS_EX_TOOLWINDOW, WS_EX_APPWINDOW = 0x80, 0x40000
PROCESS_TERMINATE = 0x0001
EVENT_OBJECT_CREATE = 0x8000
WINEVENT_OUTOFCONTEXT = 0x0000
WINEVENT_SKIPOWNPROCESS = 0x0002

# ---------------- COM（手搓 IDispatch，本机没有 pywin32） ----------------
COINIT_APARTMENTTHREADED = 0x2
CLSCTX_LOCAL_SERVER, CLSCTX_INPROC_SERVER = 0x4, 0x1
DISPATCH_METHOD, DISPATCH_PROPERTYGET = 0x1, 0x2
LOCALE_USER_DEFAULT = 0x400
VT_I4, VT_BSTR, VT_VARIANT, VT_I8 = 3, 8, 12, 20
VT_BYREF = 0x4000


class GUID(ctypes.Structure):
    _fields_ = [("d1", ctypes.c_ulong), ("d2", c_ushort), ("d3", c_ushort), ("d4", c_byte * 8)]


def guid(s):
    g = GUID()
    ole32.CLSIDFromString(ctypes.c_wchar_p(s), byref(g))
    return g


class VARIANT(ctypes.Structure):
    class _U(ctypes.Union):
        _fields_ = [("llVal", ctypes.c_longlong), ("lVal", c_long), ("wVal", c_ushort),
                    ("bstrVal", c_void_p), ("punkVal", c_void_p), ("pdispVal", c_void_p)]
    _fields_ = [("vt", c_ushort), ("r1", c_ushort), ("r2", c_ushort), ("r3", c_ushort), ("u", _U)]


class DISPPARAMS(ctypes.Structure):
    _fields_ = [("rgvarg", POINTER(VARIANT)), ("rgdispidNamedArgs", c_void_p),
                ("cArgs", c_uint), ("cNamedArgs", c_uint)]


class EXCEPINFO(ctypes.Structure):
    _fields_ = [("wCode", c_ushort), ("wReserved", c_ushort), ("bstrSource", c_void_p),
                ("bstrDescription", c_void_p), ("bstrHelpFile", c_void_p),
                ("dwHelpContext", ctypes.c_ulong), ("pvReserved", c_void_p),
                ("pfnDeferredFillIn", c_void_p), ("scode", c_long)]


def vcall(p, index, restype, *argtypes):
    vtbl = ctypes.cast(p, POINTER(c_void_p))[0]
    return ctypes.cast(ctypes.cast(vtbl, POINTER(c_void_p))[index],
                       ctypes.WINFUNCTYPE(restype, c_void_p, *argtypes))


_IID_NULL = GUID()


def get_dispid(p, name):
    dispid = c_long(-999999)
    nm = ctypes.c_wchar_p(name)
    hr = vcall(p, 5, c_long, POINTER(GUID), POINTER(ctypes.c_wchar_p), c_uint,
               ctypes.c_ulong, POINTER(c_long))(
        p, byref(_IID_NULL), byref(nm), 1, LOCALE_USER_DEFAULT, byref(dispid))
    return (dispid.value if hr == 0 else None), hr


def invoke(p, dispid, wflags, args=(), named=None, want_result=True):
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
    res, ei, err = VARIANT(), EXCEPINFO(), c_uint()
    hr = vcall(p, 6, c_long, c_long, POINTER(GUID), ctypes.c_ulong, c_ushort,
               POINTER(DISPPARAMS), POINTER(VARIANT), POINTER(EXCEPINFO), POINTER(c_uint))(
        p, dispid, byref(_IID_NULL), LOCALE_USER_DEFAULT, wflags,
        byref(dp), byref(res) if want_result else None, byref(ei), byref(err))
    return hr, res, ei


def vint(i):
    v = VARIANT(); v.vt = VT_I4; v.u.lVal = i; return v


oleaut.SysAllocString.argtypes = [ctypes.c_wchar_p]
oleaut.SysAllocString.restype = c_void_p


def vbstr(s):
    v = VARIANT(); v.vt = VT_BSTR; v.u.bstrVal = oleaut.SysAllocString(ctypes.c_wchar_p(s)); return v


def bstr(v):
    return ctypes.wstring_at(v.u.bstrVal) if v.u.bstrVal else ""


def vref(v):
    out = VARIANT(); out.vt = VT_VARIANT | VT_BYREF
    out.u.punkVal = ctypes.cast(ctypes.pointer(v), c_void_p)
    return out


def prop(disp, name):
    """读一个属性；返回 (值, vt)。字符串读出来是 str，整数是 int。"""
    d, _ = get_dispid(disp, name)
    if d is None:
        return None, -1
    _, rv, _ = invoke(disp, d, DISPATCH_PROPERTYGET)
    if rv.vt == VT_BSTR:
        return bstr(rv), rv.vt
    if rv.vt == VT_I4:
        return rv.u.lVal, rv.vt
    if rv.vt == VT_I8:
        # ⚠ ShellWindows 的 HWND 回来是 VT_I8，不是 VT_I4（按 I4 过滤会把清单滤空）
        return rv.u.llVal, rv.vt
    return None, rv.vt


def new_shell_windows():
    """⚠ 复用的对象看不见之后新加的项 ⇒ 每次要用新对象。"""
    p = c_void_p()
    hr = ole32.CoCreateInstance(byref(guid("{9BA05972-F6A8-11CF-A442-00A0C90A8F39}")), None,
                               CLSCTX_LOCAL_SERVER | CLSCTX_INPROC_SERVER,
                               byref(guid("{00020400-0000-0000-C000-000000000046}")), byref(p))
    return p if hr == 0 and p.value else None


def scan_for(cab, leaf):
    """扫一遍 ShellWindows，找那扇窗的 IDispatch。只扫这一次。
    新窗追加在清单**末尾**（见 MEMORY 24）⇒ 从队尾倒扫。"""
    p = new_shell_windows()
    if p is None:
        return None, "no-instance", "CoCreateInstance(ShellWindows) 失败"
    total = 0
    cd, _ = get_dispid(p, "Count")
    if cd is not None:
        _, cv, _ = invoke(p, cd, DISPATCH_PROPERTYGET)
        if cv.vt == VT_I4:
            total = cv.u.lVal
    idm, _ = get_dispid(p, "Item")
    fallback, n_disp, n_int, head = None, 0, 0, []
    for i in range(total - 1, -1, -1):
        hr, item, _ = invoke(p, idm, DISPATCH_METHOD, args=[vref(vint(i))])
        q = item.u.pdispVal or item.u.punkVal
        if hr != 0 or not q:
            continue
        n_disp += 1
        h, _vt = prop(q, "HWND")
        if h is not None:
            n_int += 1
        url, _ = prop(q, "LocationURL")
        if len(head) < 6:
            head.append((i, hex(h) if h else None, (url or "?")[:46]))
        if h == cab:
            return q, "hwnd(i=%d)" % i, "清单 %d 项 / 有效对象 %d / HWND 可读 %d" % (total, n_disp, n_int)
        if fallback is None and url and leaf and leaf in url.lower():
            fallback = q
    if fallback is not None:
        return fallback, "url", "清单 %d 项 / 有效对象 %d / HWND 可读 %d" % (total, n_disp, n_int)
    return None, "miss", "清单 %d 项 / 有效对象 %d / HWND 可读 %d；队尾 6 项：%s" % (
        total, n_disp, n_int, head)


def navigate(disp, url):
    """先 Navigate2（VARIANT* URL），不行退回 Navigate（BSTR）。"""
    for name in ("Navigate2", "Navigate"):
        d, _ = get_dispid(disp, name)
        if d is not None:
            t0 = time.time()
            hr, _, ei = invoke(disp, d, DISPATCH_METHOD, args=[vref(vbstr(url))], want_result=False)
            return name, hr, ei.scode, (time.time() - t0) * 1000
    return "(没有 Navigate/Navigate2)", -1, 0, 0.0


def url_to_path(s):
    if not s:
        return None
    t, low = s, s.lower()
    if low.startswith("file:///"):
        t = t[8:]
    elif low.startswith("file://"):
        t = t[7:]
    elif low.startswith("file:"):
        t = t[5:]
    return urllib.parse.unquote(t).replace("/", "\\").rstrip("\\")


# ---------------- 窗口工具 ----------------
def cls(h):
    b = ctypes.create_unicode_buffer(256)
    u.GetClassNameW(h, b, 256)
    return b.value


def text(h):
    n = u.GetWindowTextLengthW(h) + 1
    b = ctypes.create_unicode_buffer(n)
    u.GetWindowTextW(h, b, n)
    return b.value


def text_x(h):
    b = ctypes.create_unicode_buffer(1024)
    u.SendMessageW(h, WM_GETTEXT, 1024, ctypes.addressof(b))
    return b.value


def pid_of(h):
    p = wt.DWORD()
    u.GetWindowThreadProcessId(h, byref(p))
    return p.value


def children(h):
    got = []

    def cb(c, lp):
        got.append(c)
        return True
    u.EnumChildWindows(h, ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)(cb), 0)
    return got


def all_tops():
    got = []

    def cb(h, lp):
        got.append(h)
        return True
    u.EnumWindows(ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)(cb), 0)
    return got


def cabs():
    return [h for h in all_tops() if cls(h) in CAB]


def read_path(cab):
    band = None
    for c in children(cab):
        if cls(c) == "Breadcrumb Parent":
            for t in children(c):
                if cls(t) == "ToolbarWindow32":
                    band = t
                    break
    if band is None:
        for c in children(cab):
            if cls(c) == "ToolbarWindow32" and ": " in text_x(c):
                band = c
                break
    if band is None:
        return None
    _, _, rest = text_x(band).partition(": ")
    return rest.strip() or None


def has_defview(cab):
    return any(cls(c) == "SHELLDLL_DefView" for c in children(cab))


def pump():
    """WINEVENT_OUTOFCONTEXT 的回调靠消息队列投递 ⇒ 轮询里必须泵一下。"""
    msg = wt.MSG()
    while u.PeekMessageW(byref(msg), None, 0, 0, PM_REMOVE):
        u.TranslateMessage(byref(msg))
        u.DispatchMessageW(byref(msg))


class PROCESSENTRY32W(ctypes.Structure):
    _fields_ = [("dwSize", wt.DWORD), ("cntUsage", wt.DWORD), ("th32ProcessID", wt.DWORD),
                ("th32DefaultHeapID", POINTER(ctypes.c_ulong)), ("th32ModuleID", wt.DWORD),
                ("cntThreads", wt.DWORD), ("th32ParentProcessID", wt.DWORD),
                ("pcPriClassBase", c_long), ("dwFlags", wt.DWORD), ("szExeFile", ctypes.c_wchar * 260)]


k.Process32FirstW.argtypes = [wt.HANDLE, POINTER(PROCESSENTRY32W)]
k.Process32NextW.argtypes = [wt.HANDLE, POINTER(PROCESSENTRY32W)]


def explorer_pids():
    s = k.CreateToolhelp32Snapshot(0x2, 0)
    out = set()
    try:
        e = PROCESSENTRY32W()
        e.dwSize = ctypes.sizeof(PROCESSENTRY32W)
        ok = k.Process32FirstW(s, byref(e))
        while ok:
            if e.szExeFile.lower() == "explorer.exe":
                out.add(e.th32ProcessID)
            ok = k.Process32NextW(s, byref(e))
    finally:
        k.CloseHandle(s)
    return out


class STARTUPINFOW(ctypes.Structure):
    _fields_ = [("cb", wt.DWORD), ("lpReserved", wt.LPWSTR), ("lpDesktop", wt.LPWSTR),
                ("lpTitle", wt.LPWSTR), ("dwX", wt.DWORD), ("dwY", wt.DWORD),
                ("dwXSize", wt.DWORD), ("dwYSize", wt.DWORD), ("dwXCountChars", wt.DWORD),
                ("dwYCountChars", wt.DWORD), ("dwFillAttribute", wt.DWORD), ("dwFlags", wt.DWORD),
                ("wShowWindow", wt.WORD), ("cbReserved2", wt.WORD),
                ("lpReserved2", POINTER(c_byte)), ("hStdInput", wt.HANDLE),
                ("hStdOutput", wt.HANDLE), ("hStdError", wt.HANDLE)]


class PROCESS_INFORMATION(ctypes.Structure):
    _fields_ = [("hProcess", wt.HANDLE), ("hThread", wt.HANDLE),
                ("dwProcessId", wt.DWORD), ("dwThreadId", wt.DWORD)]


k.CreateProcessW.argtypes = [wt.LPCWSTR, wt.LPWSTR, c_void_p, c_void_p, wt.BOOL, wt.DWORD,
                             c_void_p, wt.LPCWSTR, POINTER(STARTUPINFOW), POINTER(PROCESS_INFORMATION)]


def create_process(cmd):
    si = STARTUPINFOW()
    si.cb = ctypes.sizeof(STARTUPINFOW)
    pi = PROCESS_INFORMATION()
    buf = ctypes.create_unicode_buffer(cmd)
    if not k.CreateProcessW(None, buf, None, None, False, 0, None, None, byref(si), byref(pi)):
        return None
    k.CloseHandle(pi.hThread)
    return pi


def to_signed32(v):
    v &= 0xFFFFFFFF
    return v - 0x100000000 if v >= 0x80000000 else v


# ==================== 主流程 ====================
def main(src, dst1, dst2):
    log = []

    def say(s):
        print(s)
        log.append(s)

    ole32.CoInitializeEx(None, COINIT_APARTMENTTHREADED)
    baseline = explorer_pids()
    # ⚠ 桌面 shell（pid 5712）自己就挂着好几扇可见顶层 CabinetWClass（桌面×2/文档/SelfDeviceCheck），
    #   所以「新窗」必须按**起跑前的窗口快照**判，另外再加一道「pid 不在起跑基线里」，
    #   免得把 shell 自己的窗当成我们的测试窗（那会当场把桌面/文档窗口藏掉）。
    base_wins = set(cabs())
    say("桌面 shell explorer pid = %d；现存 explorer = %s；起跑前已有 %d 扇 CabinetWClass"
        % (pid_of(u.FindWindowW("Shell_TrayWnd", None) or 0), sorted(baseline), len(base_wins)))

    # ---- 防闪：窗口诞生的那一刻就藏（跟 DesktopHub 同一招）----
    hides = []
    HookProc = ctypes.WINFUNCTYPE(None, wt.HANDLE, wt.DWORD, wt.HWND, c_long, c_long,
                                  wt.DWORD, wt.DWORD)

    def on_create(hook, ev, hwnd, idobj, idchild, thr, t):
        if idobj != 0 or idchild != 0 or not hwnd:
            return
        if cls(hwnd) not in CAB:
            return
        p = pid_of(hwnd)
        if p in baseline:
            return                      # 桌面 shell / 你正在用的窗口：一律不碰
        u.ShowWindow(hwnd, SW_HIDE)
        hides.append((hwnd, p, time.time()))
    cb = HookProc(on_create)
    hook = u.SetWinEventHook(EVENT_OBJECT_CREATE, EVENT_OBJECT_CREATE, None, cb, 0, 0,
                             WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS)
    say("CREATE 钩子 = %s" % (("0x%X" % (hook or 0)) if hook else "装不上(!)"))

    pi = create_process('explorer.exe /n,/separate,"%s"' % src)
    if pi is None:
        say("!! CreateProcessW 失败 err=%d" % k.GetLastError())
        return 2
    say("起 explorer 的 launcher pid=%d" % pi.dwProcessId)

    t0 = time.time()
    cab, t_appear, t_view = None, None, None
    while time.time() - t0 < BUDGET_S:
        pump()
        if cab is None:
            for h in cabs():
                if h in base_wins or pid_of(h) in baseline:
                    continue
                cab = h
                t_appear = (time.time() - t0) * 1000
                u.ShowWindow(h, SW_HIDE)
                break
        elif u.IsWindow(cab) and t_view is None and has_defview(cab):
            t_view = (time.time() - t0) * 1000
            break
        time.sleep(POLL_MS / 1000.0)
    pump()
    if cab is None:
        say("!! 没等到新窗口")
        return 2
    say("窗口 cab=0x%X pid=%d 出现+%dms 文件列表+%s（这段就是按 + 之前要先等的那 0.24~1.28 秒）"
        % (cab, pid_of(cab), t_appear, ("%dms" % t_view) if t_view is not None else "-"))
    say("CREATE 钩子一共藏了 %d 次：%s" % (
        len(hides), [(hex(h), p, "%dms" % ((ts - t0) * 1000)) for h, p, ts in hides]))

    leaf = os.path.basename(src).lower()
    t_scan = time.time()
    disp, how, note = scan_for(cab, leaf)
    say("ShellWindows 扫描：%dms，命中方式=%s（%s）" % ((time.time() - t_scan) * 1000, how, note))
    if disp is None:
        say("!! 清单里没有这扇窗 ⇒ 后面无从谈起")
        return 2

    host = u.CreateWindowExW(0, "Static", "TBEProbeHost", WS_POPUP_MASK, 0, 0, 800, 600,
                             None, None, k.GetModuleHandleW(None), None)
    say("宿主窗 host=0x%X（从不 SHOW）" % (host or 0))

    def stat(tag):
        st = u.GetWindowLongW(cab, GWL_STYLE) & 0xFFFFFFFF
        ex = u.GetWindowLongW(cab, GWL_EXSTYLE) & 0xFFFFFFFF
        hw, _ = prop(disp, "HWND")
        say("   [%s] hwnd=0x%X pid=%d parent=0x%X style=0x%08X ex=0x%08X "
            "WS_VISIBLE=%s IsWindowVisible=%s host可见=%s defview=%s 清单报HWND=%s"
            % (tag, cab, pid_of(cab), u.GetParent(cab) or 0, st, ex,
               bool(st & WS_VISIBLE), bool(u.IsWindowVisible(cab)),
               bool(host and u.IsWindowVisible(host)),
               has_defview(cab) if u.IsWindow(cab) else "-", hex(hw) if hw else None))

    def step(name, url):
        pid_before, parent_before = pid_of(cab), u.GetParent(cab)
        say("  [%s] 导航前 LocationURL=%s 地址栏=%s"
            % (name, (prop(disp, "LocationURL")[0] or "?")[:68], read_path(cab)))
        m, hr, scode, call_ms = navigate(disp, url)
        say("  [%s] %s('%s') -> hr=0x%08X scode=0x%08X 调用本身 %dms"
            % (name, m, url, hr & 0xFFFFFFFF, scode & 0xFFFFFFFF, call_ms))
        t1 = time.time()
        t_url = t_addr = None
        want = url.lower().rstrip("\\")
        while time.time() - t1 < NAV_BUDGET_S:
            pump()
            if not u.IsWindow(cab):
                say("  [%s] !!! 窗没了" % name)
                return False
            if t_url is None:
                cu, _ = prop(disp, "LocationURL")
                p = url_to_path(cu)
                if p and p.lower().startswith(want):
                    t_url = (time.time() - t1) * 1000
            if t_addr is None:
                rp = read_path(cab)
                if rp and rp.lower().startswith(want):
                    t_addr = (time.time() - t1) * 1000
            if t_url is not None and t_addr is not None:
                break
            time.sleep(POLL_MS / 1000.0)
        say("  [%s] 耗时：清单 LocationURL 变过来 %s / 窗内地址栏变过来 %s"
            % (name, ("%dms" % t_url) if t_url is not None else "超时未变",
               ("%dms" % t_addr) if t_addr is not None else "超时未变"))
        say("  [%s] 导航后 LocationURL=%s" % (name, (prop(disp, "LocationURL")[0] or "?")[:68]))
        say("  [%s] 同一扇窗？ hwnd=0x%X pid=%d(%s) parent=0x%X(%s)"
            % (name, cab, pid_of(cab), "未变" if pid_of(cab) == pid_before else "变了(换进程!)",
               u.GetParent(cab) or 0, "未变" if u.GetParent(cab) == parent_before else "变了!"))
        stat(name + "-后")
        return t_url is not None

    say("-- A 顶层（已被藏住）时导航 --")
    stat("A-前")
    a_ok = step("A", dst1)

    say("-- B 嵌入：SetParent + WS_CHILD + 摘 APPWINDOW 打 TOOLWINDOW --")
    tb = time.time()
    u.SetParent(cab, host)
    st = u.GetWindowLongW(cab, GWL_STYLE) & 0xFFFFFFFF
    u.SetWindowLongW(cab, GWL_STYLE, to_signed32((st | WS_CHILD) & ~WS_POPUP_MASK))
    ex = u.GetWindowLongW(cab, GWL_EXSTYLE) & 0xFFFFFFFF
    u.SetWindowLongW(cab, GWL_EXSTYLE, to_signed32((ex | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW))
    say("   嵌入耗时 %dms（对照：程序里样式+SetParent 20~149ms）" % ((time.time() - tb) * 1000))
    stat("B-后")

    say("-- C 嵌入之后再导航 --")
    c_ok = step("C", dst2)

    say("-- 结论 --")
    say("A（顶层能导航）=%s   C（嵌入后能导航）=%s" % (a_ok, c_ok))
    if c_ok:
        say("=> 备用窗口导航复用**可行**：同一扇窗、同一进程，直接 Navigate2 换目录。")
    elif a_ok:
        say("=> 通道本身通，但**嵌入之后就废了** ⇒ 要么导航时临时放回顶层，要么放弃这条路。")
    else:
        say("=> 连顶层都导航不了 ⇒ ShellWindows 那套 Navigate2 指望不上，换思路。")

    # ---- 收尾 ----
    if u.UnhookWinEvent and hook:
        u.UnhookWinEvent(hook)
    if u.IsWindow(cab):
        u.PostMessageW(cab, WM_CLOSE, 0, 0)
    time.sleep(0.8)
    pump()
    if u.IsWindow(cab):
        u.PostMessageW(cab, WM_CLOSE, 0, 0)
    time.sleep(0.6)
    if host and u.IsWindow(host):
        u.PostMessageW(host, WM_CLOSE, 0, 0)
    say("STILL_OPEN: %d" % (1 if u.IsWindow(cab) else 0))
    killed = []
    for p in sorted(explorer_pids() - baseline):
        hp = k.OpenProcess(PROCESS_TERMINATE, False, p)
        if hp:
            k.TerminateProcess(hp, 0)
            k.CloseHandle(hp)
            killed.append(p)
    time.sleep(0.8)
    say("清理：结束 %d 个本次新起的 explorer，残留 %s" % (len(killed), sorted(explorer_pids() - baseline)))
    out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "navigate_reuse.out.txt")
    with open(out, "w", encoding="utf-8") as f:
        f.write("\n".join(log) + "\n")
    return 0


if __name__ == "__main__":
    argv = sys.argv[1:]
    s = argv[0] if len(argv) > 0 else r"D:\Shortcut"
    d1 = argv[1] if len(argv) > 1 else r"D:\Dev"
    d2 = argv[2] if len(argv) > 2 else r"D:\Software\!Sync"
    for p in (s, d1, d2):
        if not os.path.isdir(p):
            print("!! 不是目录：%s" % p)
            sys.exit(2)
    sys.exit(main(s, d1, d2))
