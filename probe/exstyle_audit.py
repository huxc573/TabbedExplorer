# 只读探针：审计「扩展样式」有没有留下后遗症。
#
# 为什么要看这个（2026-09-22 防闪返工）：
#   防闪新加了「把窗口置成全透明」（WS_EX_LAYERED + alpha=0）这一层。
#   它有两个**必须**摘掉的出口：① 收编进标签时；② 放它回桌面时。
#   漏掉任一个 = 一扇**隐形窗口**（用户眼里：东西没了，任务栏/进程还在）。
#   这个探针不点鼠标、不动前台，只 Enum 一遍窗口看样式位 —— 随时可以跑。
#
# 判读：
#   · `TabbedExplorer` 主窗口带 ★TOPMOST ⇒ 右键菜单会被压在它底下（菜单「弹了点不动」的老病）
#   · 任何 `CabinetWClass` 带 ★LAYERED ⇒ 那个窗口现在**是隐形的**，就是后遗症
#   · 嵌进来的标签（父窗口是 WindowsForms10*）带 ★LAYERED ⇒ 标签内容是隐形的
import ctypes
from ctypes import wintypes

u32 = ctypes.windll.user32
u32.GetWindowLongW.argtypes = [wintypes.HWND, ctypes.c_int]
u32.GetWindowLongW.restype = ctypes.c_long
u32.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
u32.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
u32.IsWindowVisible.argtypes = [wintypes.HWND]
u32.IsWindowVisible.restype = wintypes.BOOL
u32.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
u32.GetParent.argtypes = [wintypes.HWND]
u32.GetParent.restype = wintypes.HWND
u32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]

GWL_EXSTYLE = -20
WS_EX_TOPMOST = 0x00000008
WS_EX_LAYERED = 0x00080000
WS_EX_TRANSPARENT = 0x00000020

GW_CHILD = 5
GW_HWNDNEXT = 2
u32.GetWindow.argtypes = [wintypes.HWND, ctypes.c_uint]
u32.GetWindow.restype = wintypes.HWND

EnumProc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)


def info(h):
    cls = ctypes.create_unicode_buffer(256)
    u32.GetClassNameW(h, cls, 256)
    txt = ctypes.create_unicode_buffer(512)
    u32.GetWindowTextW(h, txt, 512)
    ex = u32.GetWindowLongW(h, GWL_EXSTYLE) & 0xFFFFFFFF
    r = wintypes.RECT()
    u32.GetWindowRect(h, ctypes.byref(r))
    pid = wintypes.DWORD()
    u32.GetWindowThreadProcessId(h, ctypes.byref(pid))
    return {
        "hwnd": h, "cls": cls.value, "text": txt.value, "ex": ex,
        "vis": bool(u32.IsWindowVisible(h)), "parent": u32.GetParent(h),
        "pid": pid.value,
        "rect": (r.left, r.top, r.right - r.left, r.bottom - r.top),
    }


def flags(ex):
    s = []
    if ex & WS_EX_TOPMOST:
        s.append("★TOPMOST")
    if ex & WS_EX_LAYERED:
        s.append("★LAYERED(隐形风险)")
    if ex & WS_EX_TRANSPARENT:
        s.append("TRANSPARENT")
    return " ".join(s) or "—"


def walk(h, out, depth=0, maxdepth=6):
    """只看**直接孩子**，自己往下递归。
    ⚠ 别用 `EnumChildWindows` —— 它会把**整棵子树**都列一遍，
    再套一层递归就会让同一扇窗在输出里出现很多遍。"""
    out.append((depth, info(h)))
    if depth >= maxdepth:
        return
    c = u32.GetWindow(h, GW_CHILD)
    while c:
        walk(c, out, depth + 1, maxdepth)
        c = u32.GetWindow(c, GW_HWNDNEXT)


tops = []


def on_top(h, _):
    tops.append(h)
    return True


u32.EnumWindows(EnumProc(on_top), 0)

print("屏幕 %dx%d" % (u32.GetSystemMetrics(0), u32.GetSystemMetrics(1)))
print("")

# ---- ① 所有顶层 CabinetWClass / ExploreWClass（含隐形的）------------------
cabs = [info(h) for h in tops if info(h)["cls"] in ("CabinetWClass", "ExploreWClass")]
print("=== 顶层文件夹窗口 %d 个 ===" % len(cabs))
trouble = 0
for w in sorted(cabs, key=lambda x: (x["vis"], x["pid"]), reverse=True):
    if not w["vis"] and not (w["ex"] & WS_EX_LAYERED):
        continue  # 正常隐藏的（我们的标签也是隐藏状态）不刷屏
    bad = w["ex"] & WS_EX_LAYERED
    if bad:
        trouble += 1
    print("  hwnd=0x%X pid=%-6d vis=%-5s rect=%s  %s   title=%r"
          % (w["hwnd"], w["pid"], w["vis"], w["rect"], flags(w["ex"]), w["text"][:40]))
print("")

# ---- ② TabbedExplorer 主窗口 + 它嵌进来的那棵子树 -------------------------
mains = [h for h in tops
         if "WindowsForms10" in info(h)["cls"] and info(h)["vis"]]
print("=== TabbedExplorer 可见顶层窗口 %d 个 ===" % len(mains))
for h in mains:
    w = info(h)
    print("  主窗口 hwnd=0x%X rect=%s  %s" % (h, w["rect"], flags(w["ex"])))
    rows = []
    walk(h, rows, 0, 4)
    for depth, c in rows:
        if c["cls"] in ("CabinetWClass", "ExploreWClass"):
            print("    %s嵌入 cab=0x%X vis=%-5s rect=%s  %s"
                  % ("  " * depth, c["hwnd"], c["vis"], c["rect"], flags(c["ex"])))
print("")
print("结论：%s" % ("发现 %d 个残留的全透明窗口（见上面的 ★LAYERED）" % trouble
                 if trouble else "没有残留的全透明窗口 ✓"))
