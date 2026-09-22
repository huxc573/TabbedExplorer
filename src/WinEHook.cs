using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace TabbedExplorer
{
    /// <summary>
    /// 全局低级键盘钩子。干两类事：
    ///
    ///   1) **无条件**吞掉 Win+E，改由我们开窗 —— Win+E 是 explorer 在**按键层面**处理的，
    ///      没有 shell verb、没有注册表项可以改（试过改 CommandStore 之类都拦不住），
    ///      唯一能在它之前截住的办法就是 WH_KEYBOARD_LL。
    ///   2) **只在我们窗口是前台时**接管标签快捷键（新建/关闭/前后标签/历史/恢复/收藏夹栏 + Ctrl+1..9）。
    ///      这些绑定是**可自定义**的，真源在 settings.json 的 `hotkey_*`（解析在 Hotkeys）。
    ///
    /// 为什么标签快捷键也得走钩子（2026-09-22 川報「Ctrl+T 完全没效果，Ctrl+W 直接把程序关掉了」）：
    /// 真正持有键盘焦点的是**嵌进来的 explorer 子进程**（跨进程 SetParent 进来的那个窗口树），
    /// 按键根本不会流进我们的窗体 —— `Form.KeyPreview` / `OnPreviewKeyDown` 一条都收不到。
    /// 于是：Ctrl+T（explorer 没这个键）什么都不发生；Ctrl+W（explorer 自带「关闭窗口」）
    /// 被 explorer 自己吃掉，把它的 CabinetWClass 关了，我们那个标签就剩个空壳。
    ///
    /// 三条硬规矩（踩过）：
    ///   1. 回调里**只准读字段 + 判定 + 投递**，不做 COM / 文件 IO / 开窗 / **写日志** ——
    ///      超过系统的 LowLevelHooksTimeout（默认 300ms）系统会把钩子**悄悄摘掉**，
    ///      表现就是「一开始能接管，过了一会儿 Win+E 又变成原生资源管理器了」。
    ///      所以日志一律写在下游（UI 线程的处理器里）。
    ///   2. 钩子装在一个**独立线程**的消息循环上，不占 UI 线程 —— UI 线程一旦被
    ///      「起 explorer 等窗口」之类的事占住，同样会被系统摘钩子。
    ///   3. 事件在**钩子线程**上触发，订阅者必须自己 BeginInvoke 回 UI 线程。
    /// </summary>
    internal sealed class WinEHook : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int VK_SHIFT = 0x10;
        private const int VK_CONTROL = 0x11;
        private const int VK_MENU = 0x12;      // Alt
        private const int VK_E = 0x45;
        private const int VK_1 = 0x31;      // 主键盘的 1..9（Ctrl+1..9 = 第 N 个标签）
        private const int VK_9 = 0x39;
        private const int VK_LWIN = 0x5B;
        private const int VK_RWIN = 0x5C;
        private const uint GA_ROOT = 2;
        private const uint LLKHF_INJECTED = 0x10;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

        /// <summary>吞到 Win+E 了。**在钩子线程上触发**，订阅者自己往 UI 线程转。</summary>
        public event Action WinE;

        /// <summary>
        /// 吞到了一条**可自定义**的命令。参数是命令标识（`newtab` / `closetab` / `nexttab` /
        /// `prevtab` / `history` / `reopen` / `favbar` —— 就是 `Settings.HotkeyKeys` 那 7 条），
        /// 绑的组合键由 `Hotkeys`（settings.json 里可改）说了算。
        /// 2026-09-22 改的：原来是一条命令一个事件、组合键写死在本文件里；
        /// 现在合成一个事件，加/改绑定只动 settings.json 和设置窗口。
        /// </summary>
        public event Action<string> Command;

        /// <summary>吞到 Ctrl+1..9（跳到第 N 个标签）。**这一条不参与自定义**（没法绑「一串」键）。参数是 0 基下标。</summary>
        public event Action<int> GotoTabKey;

        /// <summary>
        /// 我们的一个窗口句柄（只是「其中一个」）。**只有我们进程是前台时才接管快捷键** ——
        /// 否则就成了全局霸占 Ctrl+W（浏览器里关标签、别的编辑器里存盘都会被我吃掉）。
        /// 每张虚拟桌面一个窗口之后，这里不再拿它当唯一判据，见 OursIsForeground。
        /// </summary>
        public IntPtr MainWindow;

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
            thread.Name = "TabbedExplorer.WinEHook";
            thread.Start();
        }

        private void ThreadProc()
        {
            proc = new HookProc(Callback);   // 存字段：委托被回收了钩子就哑了
            hHook = NativeMethods.SetWindowsHookEx(WH_KEYBOARD_LL, proc, IntPtr.Zero, 0);
            if (hHook == IntPtr.Zero)
            {
                Diag.Log("WinEHook: 安装失败 err=" + Marshal.GetLastWin32Error());
                return;
            }
            Diag.Step("WinEHook: 已安装（Win+E 无条件；其余快捷键仅我们前台时，绑定见 settings.json 的 hotkey_*）");
            ctx = new ApplicationContext();
            Application.Run(ctx);            // 本线程的消息循环，钩子靠它活着
            if (hHook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(hHook);
            hHook = IntPtr.Zero;
        }

        private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0)
                {
                    int msg = wParam.ToInt32();
                    if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                    {
                        KBDLLHOOKSTRUCT st = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(
                            lParam, typeof(KBDLLHOOKSTRUCT));

                        // 只吞「真人按的键」：注入的键放行，免得挡住自己的 SendCommand / 远程工具
                        // （KBDLLHOOKSTRUCT 的字段都是 uint，GetAsyncKeyState 要 int —— 这里统一成 int）
                        if ((st.flags & LLKHF_INJECTED) == 0 && Handle((int)st.vkCode))
                            return (IntPtr)1;
                    }
                }
            }
            catch { }
            return NativeMethods.CallNextHookEx(hHook, nCode, wParam, lParam);
        }

        /// <summary>这个键要不要吞掉。**只读状态 + 投递事件**，别的什么都不做（见类注释第 1 条）。</summary>
        /// <remarks>
        /// 组合键现在是**可自定义**的（`Hotkeys.Match`，见 settings.json 的 `hotkey_*`）：
        /// 这里只把「当前按下的键 + 修饰键状态」拿去表里比一下，命中就报命令名。
        /// 为了不给系统添负担，先做**最便宜的早退**（一个 Ctrl/Alt 都没按就直接放行），
        /// 再去问「前台是不是我们」—— `OursIsForeground` 要调几个 Win32，别每次按键都做。
        /// </remarks>
        private bool Handle(int vk)
        {
            bool win = Down(VK_LWIN) || Down(VK_RWIN);

            // Win+E：不管谁在前台都归我们 —— 这就是「接管」的意义
            if (vk == VK_E && win)
            {
                Raise(WinE);
                return true;
            }

            bool ctrl = Down(VK_CONTROL);
            bool alt = Down(VK_MENU);
            // 有效绑定至少带一个 Ctrl 或 Alt（Shift 可以一起按，但不能是唯一修饰键，见 Hotkeys.Apply）
            if ((!ctrl && !alt) || win) return false;
            if (!OursIsForeground()) return false;             // 别的程序里按 Ctrl+W 必须原样放行

            string cmd = Hotkeys.Match(vk, ctrl, Down(VK_SHIFT), alt);
            if (cmd != null)
            {
                Action<string> a = Command;
                if (a != null) a(cmd);
                return true;
            }

            // Ctrl+1..9 = 跳到第 N 个标签（浏览器那套；固定，不参与自定义）。只吞没按 Shift / Alt 的。
            if (ctrl && !alt && !Down(VK_SHIFT) && vk >= VK_1 && vk <= VK_9)
            {
                int n = vk - VK_1;
                Action<int> a = GotoTabKey;
                if (a != null) a(n);
                return true;
            }
            return false;
        }

        private static bool Down(int vk)
        {
            return (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;
        }

        private static void Raise(Action a)
        {
            if (a != null) a();
        }

        /// <summary>
        /// 前台是不是我们。注意「焦点在嵌进来的 explorer 子窗口里」也算：
        /// 那时 GetForegroundWindow() 返回的仍是**我们的顶层窗口**（子窗口不能成为前台窗口），
        /// 所以这里再做两层兜底 —— 前台窗口属于我们进程、或它的顶层祖先是我们。
        /// </summary>
        private bool OursIsForeground()
        {
            IntPtr fg = NativeMethods.GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;

            IntPtr main = MainWindow;
            if (main != IntPtr.Zero && fg == main) return true;

            // 前台窗口属于我们这个进程 ⇒ 一定是我们自己的窗口。
            // 这条才是主力判据：每张虚拟桌面各有一个窗口，MainWindow 只是「其中一个」。
            // 另外「焦点在嵌进来的 explorer 子窗口里」也走这条 —— 子窗口当不了前台窗口，
            // 那时 GetForegroundWindow() 返回的仍是我们的顶层窗口。
            uint pid;
            GetWindowThreadProcessId(fg, out pid);
            if ((int)pid == ourPid) return true;

            return main != IntPtr.Zero && GetAncestor(fg, GA_ROOT) == main;
        }

        public void Dispose()
        {
            try
            {
                if (ctx != null) ctx.ExitThread();
            }
            catch { }
            try
            {
                if (hHook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(hHook);
            }
            catch { }
            hHook = IntPtr.Zero;
        }
    }
}
