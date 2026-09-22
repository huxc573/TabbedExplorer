using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace TabbedExplorer
{
    /// <summary>
    /// 结果四态。**这个区分是「不把人拽走」的全部依据** —— 只有前两种才允许抢前台。
    /// </summary>
    internal enum VdOutcome
    {
        /// <summary>一开始就在当前桌面（无需搬）。可以抢前台。</summary>
        OnCurrentDesktop,
        /// <summary>搬过去了，并已回读确认。可以抢前台。</summary>
        MovedAndVerified,
        /// <summary>接口不可用 / 什么都判不了。只好照老办法抢前台。</summary>
        Unknown,
        /// <summary>**已知窗口在别的桌面、而且没搬过去**。绝不能抢前台（一抢就把人拽走）。</summary>
        Failed
    }

    /// <summary>
    /// 虚拟桌面：让窗口「跟着你当前正在看的那个桌面」出现。
    ///
    /// 川报的现象：在别的虚拟桌面上按 Win+E，窗口没出现，反而把人**拽回原来的桌面** ——
    /// 因为窗口属于它被创建时那个桌面，而 SetForegroundWindow 会让系统切到那个桌面去。
    ///
    /// 做法：动手抢前台**之前**先问「你现在在哪个桌面」，把窗口搬过去再显示。
    ///
    /// ⚠ 2026-09-22 修「在 Other 桌面还是会切走」：原来「当前桌面」只能靠
    /// `GetWindowDesktopId(前台窗口)` 反推，而这个读**会间歇性失败**（日志实证：
    /// 16:10:24 成功、16:10:26 与 16:10:36 取不到，紧接着抢前台把人拽回 Daily）。
    /// 三处一起修：
    ///   ① 「当前桌面」改走未公开的 `IVirtualDesktopManagerInternal::GetCurrentDesktop`（权威、
    ///      已在本机实测可用且与公开反推一致），公开反推降为兜底并加重试；
    ///   ② 搬完**必须回读复核**（`IsWindowOnCurrentVirtualDesktop`），没确认不算数；
    ///   ③ 上层只有拿到 `OnCurrentDesktop` / `MovedAndVerified` 才抢前台，
    ///      拿到 `Failed` 就**只显示、不激活** —— 结构上杜绝「把人拽走」。
    ///
    /// 用公开的 IVirtualDesktopManager 只能搬**本进程**的窗口；搬别人的会 E_ACCESSDENIED
    /// （那要另一套未公开接口，见 skill `win10-vd-move-foreign-window`）。
    /// 我们的嵌入子窗口挂在主窗口底下，跟着父窗口走，不用单独搬。
    /// </summary>
    internal static class VirtualDesktop
    {
        /// <summary>同一个 RCW 被多线程共用会静默打崩（skill 里记着的老坑）—— 所有调用一律串行。</summary>
        private static readonly object gate = new object();

        private static IVirtualDesktopManager vdm;
        private static IVirtualDesktopManagerInternal vdmi;
        private static object shellObj;          // 保活：ImmersiveShell 实例
        private static bool triedPublic;
        private static bool triedInternal;

        private static IVirtualDesktopManager Manager
        {
            get
            {
                if (triedPublic) return vdm;
                triedPublic = true;
                try
                {
                    Type t = Type.GetTypeFromCLSID(new Guid(Guids.CLSID_VirtualDesktopManager));
                    vdm = (IVirtualDesktopManager)Activator.CreateInstance(t);
                    Diag.Step("虚拟桌面: 公开接口就绪");
                }
                catch (Exception ex)
                {
                    Diag.Log("虚拟桌面: 公开接口不可用 " + ex.Message);
                    vdm = null;
                }
                return vdm;
            }
        }

        /// <summary>未公开的「当前桌面」权威来源。拿不到就返回 null（自动降级到公开反推）。</summary>
        private static IVirtualDesktopManagerInternal Internal
        {
            get
            {
                if (triedInternal) return vdmi;
                triedInternal = true;
                try
                {
                    Type t = Type.GetTypeFromCLSID(new Guid(Guids.CLSID_ImmersiveShell));
                    shellObj = Activator.CreateInstance(t);
                    IServiceProviderCom sp = (IServiceProviderCom)shellObj;

                    Guid svc = new Guid(Guids.CLSID_VirtualDesktopManagerInternal);
                    Guid iid = new Guid(Guids.IID_IVirtualDesktopManagerInternal);
                    IntPtr p;
                    int hr = sp.QueryService(ref svc, ref iid, out p);
                    if (hr == 0 && p != IntPtr.Zero)
                    {
                        try { vdmi = (IVirtualDesktopManagerInternal)Marshal.GetObjectForIUnknown(p); }
                        finally { Marshal.Release(p); }
                        Diag.Step("虚拟桌面: 未公开 GetCurrentDesktop 就绪（当前桌面的权威来源）");
                    }
                    else
                    {
                        Diag.Step("虚拟桌面: 未公开接口不可用 hr=0x" + hr.ToString("X8") + "，降级为公开反推");
                    }
                }
                catch (Exception ex)
                {
                    Diag.Log("虚拟桌面: 未公开接口异常 " + ex.Message + "，降级为公开反推");
                    vdmi = null;
                }
                return vdmi;
            }
        }

        private static string ShortGuid(Guid g)
        {
            return g == Guid.Empty ? "(空)" : g.ToString().Substring(0, 8);
        }

        /// <summary>
        /// 启动时先摸一次接口，把「公开 / 未公开 各自通不通 + 当前桌面」写进日志。
        /// 以后再出现「切了桌面被拽回去」，这两行就是第一手证据：
        /// 到底是接口没接上（降级成不靠谱的公开反推），还是搬不动。
        /// </summary>
        public static void WarmUp()
        {
            lock (gate)
            {
                IVirtualDesktopManager m = Manager;
                IVirtualDesktopManagerInternal it = Internal;
                Diag.Step(string.Format("虚拟桌面: 预热 公开={0} 未公开={1} 当前桌面={2}",
                    m != null ? "通" : "不通",
                    it != null ? "通" : "不通",
                    ShortGuid(CurrentDesktopId())));
            }
        }

        /// <summary>窗口现在在不在当前桌面。取不到状态返回 null。</summary>
        private static bool? OnCurrent(IVirtualDesktopManager m, IntPtr hwnd)
        {
            try
            {
                bool on;
                if (m.IsWindowOnCurrentVirtualDesktop(hwnd, out on) == 0) return on;
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 当前「正在看的」桌面 GUID。优先未公开的权威接口，失败再靠前台窗口反推（带重试）。
        /// 🔴 **必须在抢前台之前调** —— 一旦我们成了前台窗口，反推出来的就是老的那个。
        /// </summary>
        public static Guid CurrentDesktopId()
        {
            // ① 权威：IVirtualDesktopManagerInternal::GetCurrentDesktop
            IVirtualDesktopManagerInternal it = Internal;
            if (it != null)
            {
                try
                {
                    IVirtualDesktop d = null;
                    if (it.GetCurrentDesktop(out d) == 0 && d != null)
                    {
                        Guid id;
                        if (d.GetId(out id) == 0 && id != Guid.Empty) return id;
                    }
                }
                catch (Exception ex) { Diag.Log("虚拟桌面: 未公开取当前桌面失败 " + ex.Message); }
            }

            // ② 兜底：从前台窗口反推。实测这个读会间歇性失败，所以重试几轮（~40ms 上限）。
            IVirtualDesktopManager m = Manager;
            if (m == null) return Guid.Empty;
            for (int i = 0; i < 10; i++)
            {
                Guid g = DeriveFromForeground(m);
                if (g != Guid.Empty) return g;
                Thread.Sleep(4);
            }
            return Guid.Empty;
        }

        private static Guid DeriveFromForeground(IVirtualDesktopManager m)
        {
            try
            {
                Guid g;
                // 前台窗口必然在当前桌面 —— 反推最可靠的一手
                IntPtr fg = NativeMethods.GetForegroundWindow();
                if (fg != IntPtr.Zero && NativeMethods.IsWindow(fg) &&
                    m.GetWindowDesktopId(fg, out g) == 0 && g != Guid.Empty)
                    return g;

                // 前台是某个 owned/tool 窗口时它自己可能没有桌面 ID，退到它的顶层窗口再问一次。
                // （实测 GetShellWindow() 那条**恒失败** hr=0x8002802B TYPE_E_ELEMENTNOTFOUND，
                //   所以别把兜底全押在它身上。）
                if (fg != IntPtr.Zero && NativeMethods.IsWindow(fg))
                {
                    IntPtr root = NativeMethods.GetAncestor(fg, 2);   // GA_ROOT
                    if (root != IntPtr.Zero && root != fg &&
                        m.GetWindowDesktopId(root, out g) == 0 && g != Guid.Empty)
                        return g;
                }
            }
            catch { }
            return Guid.Empty;
        }

        /// <summary>
        /// 把窗口弄到当前桌面。**必须在抢前台之前调**。
        /// 返回结果四态，上层据此决定敢不敢抢前台（只有前两种敢）。
        /// </summary>
        public static VdOutcome EnsureOnCurrentDesktop(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return VdOutcome.Unknown;
            IVirtualDesktopManager m = Manager;
            if (m == null) return VdOutcome.Unknown;     // 没接口 ⇒ 判不了 ⇒ 上层按老办法来

            lock (gate)
            {
                try
                {
                    bool? on = OnCurrent(m, hwnd);

                    Guid want = CurrentDesktopId();
                    if (want == Guid.Empty)
                    {
                        // 问不出当前桌面：既不能搬、也无法确认。保守当成「不安全」——
                        // 此刻窗口很可能在别的桌面上，而抢前台就会把人拽走。
                        if (on == true)
                        {
                            Diag.Step("虚拟桌面: 窗口已在当前桌面（问不出目标桌面，但无需搬）");
                            return VdOutcome.OnCurrentDesktop;
                        }
                        Diag.Step("虚拟桌面: 取不到当前桌面 -> 不搬、也不抢前台");
                        return VdOutcome.Failed;
                    }

                    if (on == true) { Diag.Step("虚拟桌面: 窗口已在当前桌面（无需搬）"); return VdOutcome.OnCurrentDesktop; }

                    for (int attempt = 1; attempt <= 3; attempt++)
                    {
                        Guid now;
                        if (m.GetWindowDesktopId(hwnd, out now) != 0) now = Guid.Empty;
                        if (now == want) break;

                        Guid target = want;
                        int hr = m.MoveWindowToDesktop(hwnd, ref target);
                        Thread.Sleep(attempt * 8);          // 搬完给 shell 一点时间落定

                        bool? after = OnCurrent(m, hwnd);
                        Diag.Step(string.Format("虚拟桌面: 搬窗口 第{0}次 {1} -> {2} hr=0x{3:X8} 复核={4}",
                            attempt, ShortGuid(now), ShortGuid(want), hr,
                            after == null ? "读不到" : (after.Value ? "在当前桌面" : "还没到")));
                        if (after == true) return VdOutcome.MovedAndVerified;
                    }

                    Diag.Step("虚拟桌面: 三轮都没搬过去 -> 不抢前台（不切走）");
                    return VdOutcome.Failed;
                }
                catch (Exception ex)
                {
                    Diag.Log("虚拟桌面: 搬窗口异常 " + ex.Message + " -> 不抢前台");
                    return VdOutcome.Failed;
                }
            }
        }
    }
}
