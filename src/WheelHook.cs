using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace TabbedExplorer
{
    /// <summary>
    /// 一个窗口（每张虚拟桌面一个 EmbedForm）在滚轮这件事上的登记项。
    ///
    /// 钩子线程会读它，所以这里的东西必须是**只读 / 线程安全**的：
    ///   · `StripWheel` 只往 UI 线程投递（`Defer`），自己不动界面；
    ///   · 它自己读的那些状态（`InButtonArea` / `overflow`）都是**上一次排版算好的结果** ——
    ///     绝不能在钩子里触发重排布局（见 MouseWheelHook 的三条约束）。
    /// </summary>
    internal sealed class WheelTarget
    {
        public IntPtr Form;
        public IntPtr TabStrip;
        /// <summary>垂直侧边栏那条窗格（横向模式下它不可见，所以两个都登记上）。</summary>
        public IntPtr Pane;
        public IntPtr FavBar;
        /// <summary>
        /// 滚轮落在**标签条**上。参数 =（标签条客户区 x, 原始 delta），返回值 = 吞不吞。
        /// 为什么要给 x：同一条标签条上两种行为 —— **标签区**滚轮 = 切前后标签，
        /// **右边那排按钮**上的滚轮 = 横向滑标签（用户要的，见 TabStrip.InButtonArea）。
        /// </summary>
        public Func<int, int, bool> StripWheel;
    }

    /// <summary>按窗口句柄找登记项（钩子里用，加锁保护）。</summary>
    internal static class WheelRouter
    {
        private static readonly List<WheelTarget> list = new List<WheelTarget>();

        public static void Register(WheelTarget t)
        {
            if (t == null || t.Form == IntPtr.Zero) return;
            lock (list)
            {
                UnregisterLocked(t.Form);
                list.Add(t);
            }
        }

        public static void Unregister(IntPtr form)
        {
            lock (list) { UnregisterLocked(form); }
        }

        private static void UnregisterLocked(IntPtr form)
        {
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i].Form == form) list.RemoveAt(i);
            }
        }

        /// <summary>这个句柄（可以是子控件）属于哪个窗口的登记项；没有就 null。</summary>
        public static WheelTarget Find(IntPtr hwnd, IntPtr root)
        {
            lock (list)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    WheelTarget t = list[i];
                    if (hwnd == t.TabStrip || hwnd == t.Pane || hwnd == t.FavBar || hwnd == t.Form) return t;
                }
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i].Form == root) return list[i];
                }
            }
            return null;
        }
    }

    /// <summary>
    /// 全局低级鼠标钩子 —— 只为滚轮一件事（用户要的），而且**只管我们自己画的那些地方**：
    ///   · 滚轮在**标签**上（含垂直侧边栏整条窗格）= 切换前后标签页；
    ///   · 滚轮在**右边那排按钮**上 = 横向滑标签（溢出了才有意义）。
    ///
    /// ⚠ **内容区一律不碰**（2026-09-28 川报 BUG 后定的）：标签装不下时，滚轮落在嵌进来的
    /// 原生资源管理器上，原来会被我们抢去横滚标签条 —— 结果是**文件列表根本滚不动**。
    /// 用户的口径：「这套只在我们自己窗口的**非标签内容**部分实现」。标签条自己那条滚动条
    /// （鼠标进标签条才显形、能拖能点轨道）加上按钮区那一路，已经够滚了。
    ///
    /// 为什么要用钩子而不是控件的 `OnMouseWheel`：内容区是**跨进程嵌进来的真 explorer 窗口**，
    /// 滚轮消息直接投给它（鼠标在谁身上就归谁）。要**在我们自己的控件**上截一刀，
    /// `WH_MOUSE_LL` 是唯一能在消息派发**之前**看到滚轮、并且决定吞不吞的地方。
    ///
    /// 顺带：Shift+滚轮在文件列表里 = **shell 原生的横向滚动** —— 我们不再碰内容区，
    /// 所以这个原生行为永远在。
    ///
    /// 三条自我约束（跟 WinEHook 一个道理，抄过来）：
    ///   1. 回调里**只准读状态 + 判定 + 投递**，不写日志、不碰界面；超时会被系统悄悄摘钩子。
    ///   2. 装在**独立线程**的消息循环上，不占 UI 线程。
    ///   3. **只在真有意义时**才吞（按钮区没溢出就放行）—— 否则消息会莫名其妙失效。
    /// </summary>
    internal sealed class MouseWheelHook : IDisposable
    {
        private const int WH_MOUSE_LL = 14;
        private const int WM_MOUSEWHEEL = 0x020A;
        private const uint LLMHF_INJECTED = 0x01;
        private const uint GA_ROOT = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x; public int y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT pt);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

        [DllImport("user32.dll")]
        private static extern bool ScreenToClient(IntPtr hWnd, ref POINT pt);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        private readonly int ourPid = Process.GetCurrentProcess().Id;

        private Thread thread;
        private HookProc proc;
        private IntPtr hHook = IntPtr.Zero;
        private ApplicationContext ctx;

        public bool Installed { get { return hHook != IntPtr.Zero; } }

        public void Start()
        {
            thread = new Thread(ThreadProc);
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Name = "TabbedExplorer.WheelHook";
            thread.Start();
        }

        private void ThreadProc()
        {
            proc = new HookProc(Callback);
            hHook = NativeMethods.SetWindowsHookEx(WH_MOUSE_LL, proc, IntPtr.Zero, 0);
            if (hHook == IntPtr.Zero)
            {
                Diag.Log("WheelHook: 安装失败 err=" + Marshal.GetLastWin32Error());
                return;
            }
            Diag.Step("WheelHook: 已安装（标签上滚轮=切标签；右边按钮区=横滚标签条；内容区一律不碰）");
            ctx = new ApplicationContext();
            Application.Run(ctx);
            if (hHook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(hHook);
            hHook = IntPtr.Zero;
        }

        private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0 && wParam.ToInt32() == WM_MOUSEWHEEL)
                {
                    MSLLHOOKSTRUCT st = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(
                        lParam, typeof(MSLLHOOKSTRUCT));
                    if ((st.flags & LLMHF_INJECTED) == 0)
                    {
                        int delta = (short)((st.mouseData >> 16) & 0xFFFF);
                        if (Handle(st.pt, delta)) return (IntPtr)1;   // 吞掉
                    }
                }
            }
            catch { }
            return NativeMethods.CallNextHookEx(hHook, nCode, wParam, lParam);
        }

        /// <summary>要不要吞。只读状态 + 投递，别的什么都不做。</summary>
        private bool Handle(POINT pt, int delta)
        {
            if (delta == 0) return false;

            IntPtr w = WindowFromPoint(pt);
            if (w == IntPtr.Zero) return false;
            IntPtr root = GetAncestor(w, GA_ROOT);
            if (root == IntPtr.Zero) return false;

            uint pid;
            GetWindowThreadProcessId(root, out pid);
            if ((int)pid != ourPid) return false;      // 不是我们的窗口，别管

            WheelTarget t = WheelRouter.Find(w, root);
            if (t == null) return false;

            if (w == t.TabStrip || w == t.Pane)
            {
                // 标签条上，分左右两半：标签区 = 切标签；右边那排按钮 = 横滑标签（溢出了才吞）。
                // 垂直窗格那条没有「按钮区」这一说（工具排在顶上、不是右边），
                // 它的 InButtonArea 恒 false —— 落在窗格上就是切标签。
                POINT c = pt;
                ScreenToClient(w, ref c);
                if (t.StripWheel != null) return t.StripWheel(c.x, delta);
                return false;
            }
            if (w == t.FavBar) return false;           // 书签栏自己有滚动（横排横滑、竖排纵滑），别抢

            // 其余 = **内容区**（跨进程嵌进来的真 explorer）：一律放行。
            // ⚠ 原来这里是「标签溢出就抢过来横滚标签条」—— 结果是文件列表根本滚不动
            //   （2026-09-28 川报：「标签占满了，滚轮在原生资源管理器那里不生效，只是滑动标签栏」）。
            //   这套只该管我们自己画的标签条 / 按钮区。
            return false;
        }

        public void Dispose()
        {
            try { if (ctx != null) ctx.ExitThread(); } catch { }
            try { if (hHook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(hHook); } catch { }
            hHook = IntPtr.Zero;
        }
    }
}
