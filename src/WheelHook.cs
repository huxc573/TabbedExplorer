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
    ///   · `HasOverflow` 只读一个已算好的 bool 字段（**不能**在钩子里触发重排布局）；
    ///   · `SwitchTab` / `ScrollStrip` 只往 UI 线程投递（`Defer`），自己不动界面。
    /// </summary>
    internal sealed class WheelTarget
    {
        public IntPtr Form;
        public IntPtr TabStrip;
        public IntPtr FavBar;
        /// <summary>标签是不是多到需要横向滚动（没开自动缩窄时才会 true）。</summary>
        public Func<bool> HasOverflow;
        /// <summary>滚轮在标签条上：切前后标签（参数 = 原始 delta）。</summary>
        public Action<int> SwitchTab;
        /// <summary>滚轮在内容区：横向滚标签条。</summary>
        public Action<int> ScrollStrip;
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
                    if (hwnd == t.TabStrip || hwnd == t.FavBar || hwnd == t.Form) return t;
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
    /// 全局低级鼠标钩子 —— 只为滚轮一件事（川 2026-09-22 要的）：
    ///   · 滚轮在**标签条**上 = 切换前后标签页；
    ///   · 滚轮在**非标签条**区域（内容区）= 横向滚动标签条（没开自动缩窄、标签溢出时才有意义）。
    ///
    /// 为什么要用钩子而不是控件的 `OnMouseWheel`：内容区是**跨进程嵌进来的真 explorer 窗口**，
    /// 滚轮消息直接投给它（鼠标在谁身上就归谁），我们的窗体根本收不到 —— 也没法在它之前插一脚。
    /// WH_MOUSE_LL 是唯一能在消息派发**之前**看到滚轮、并且决定吞不吞的地方。
    ///
    /// 三条自我约束（跟 WinEHook 一个道理，抄过来）：
    ///   1. 回调里**只准读状态 + 判定 + 投递**，不写日志、不碰界面；超时会被系统悄悄摘钩子。
    ///   2. 装在**独立线程**的消息循环上，不占 UI 线程。
    ///   3. **只在标签确实溢出时**才吞内容区的滚轮 —— 否则文件列表的滚动会莫名其妙失效。
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
            Diag.Step("WheelHook: 已安装（标签条上滚轮=切标签；内容区滚轮=横向滚标签）");
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

            if (w == t.TabStrip)
            {
                // 标签条上：切换前后标签页
                if (t.SwitchTab != null) t.SwitchTab(delta);
                return true;
            }
            if (w == t.FavBar) return false;           // 收藏夹栏自己有横向滚动，别抢

            // 内容区：只有标签真的溢出时才接管（否则会把文件列表的滚动弄没了）
            if (t.HasOverflow == null || !t.HasOverflow()) return false;
            if (t.ScrollStrip != null) t.ScrollStrip(delta);
            return true;
        }

        public void Dispose()
        {
            try { if (ctx != null) ctx.ExitThread(); } catch { }
            try { if (hHook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(hHook); } catch { }
            hHook = IntPtr.Zero;
        }
    }
}
