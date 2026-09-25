using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace TabbedExplorer
{
    /// <summary>
    /// 「嵌入真 explorer 窗口」模式专用的 P/Invoke。
    /// 单独放一个类，不碰 NativeMethods，避免和 IExplorerBrowser 那套打架。
    /// </summary>
    internal static class EmbedApi
    {
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
        public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr h, StringBuilder s, int n);

        [DllImport("user32.dll")]
        public static extern IntPtr GetParent(IntPtr h);

        [DllImport("user32.dll")]
        public static extern IntPtr SetParent(IntPtr child, IntPtr parent);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr h, IntPtr after,
            int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr h, out WRECT r);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr h);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);

        [DllImport("user32.dll")]
        public static extern bool AttachThreadInput(uint attach, uint attachTo, bool fAttach);

        [DllImport("user32.dll")]
        public static extern IntPtr SetFocus(IntPtr h);

        [DllImport("user32.dll")]
        public static extern bool PostMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr h, int cmd);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        // 32/64 位两种取窗口样式的入口；GWL_STYLE = -16
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr64(IntPtr h, int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        private static extern int GetWindowLong32(IntPtr h, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr h, int index, IntPtr val);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong32(IntPtr h, int index, int val);

        public const int GWL_STYLE = -16;

        /// <summary>ShowWindow 的两个常用值（防闪那条路全靠 SW_HIDE）。</summary>
        public const int SW_HIDE = 0;
        public const int SW_SHOW = 5;

        // ---- 窗口样式位 ----
        public const uint WS_CHILD = 0x40000000;
        public const uint WS_MINIMIZE = 0x20000000;
        public const uint WS_VISIBLE = 0x10000000;
        public const uint WS_POPUP = 0x80000000;
        public const uint WS_CAPTION = 0x00C00000;
        public const uint WS_THICKFRAME = 0x00040000;
        public const uint WS_SYSMENU = 0x00080000;
        public const uint WS_MINIMIZEBOX = 0x00020000;
        public const uint WS_MAXIMIZEBOX = 0x00010000;

        // ---- SetWindowPos ----
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_FRAMECHANGED = 0x0020;
        public const uint SWP_SHOWWINDOW = 0x0040;

        public const uint WM_CLOSE = 0x0010;

        // ---- 窗口绘制区（`SetWindowRgn`）----
        // 用途只有一个：把 shell 自己那扇刚要显示的窗临时变成「什么都画不出来」的 —— 见
        // `DesktopHub.BlankShellWindow`。**不是样式位**（那条红线管的是 exstyle / 透明），
        // `SetWindowRgn(h, IntPtr.Zero, true)` 就还原成没区域。
        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateRectRgn(int l, int t, int r, int b);

        [DllImport("user32.dll")]
        public static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);

        /// <summary>问一扇窗现在有没有绘制区。返回值只用得上 <see cref="RGN_NULL"/>。</summary>
        [DllImport("user32.dll")]
        public static extern int GetWindowRgn(IntPtr h, IntPtr rgn);

        /// <summary>`GetWindowRgn` 的返回值：区域是**空的** ⇒ 这扇窗一个像素都不画。</summary>
        public const int RGN_NULL = 1;

        /// <summary>
        /// 把一扇窗变成「画不出东西」的（空绘制区）。返回它原来有没有区域 —— 原来就有的话
        /// 我们不该动它（那说明它自己有用途），调用方据此放弃。
        /// </summary>
        public static bool MakeBlank(IntPtr h)
        {
            if (h == IntPtr.Zero) return false;
            if (GetWindowRgn(h, IntPtr.Zero) != 0) return false;    // 已经有区域了：不碰
            IntPtr r = CreateRectRgn(0, 0, 0, 0);
            if (r == IntPtr.Zero) return false;
            return SetWindowRgn(h, r, true) != 0;
        }

        /// <summary>还原 `MakeBlank`（去掉绘制区限制）。</summary>
        public static void Unblank(IntPtr h)
        {
            if (h == IntPtr.Zero) return;
            try { SetWindowRgn(h, IntPtr.Zero, true); } catch { }
        }

        /// <summary>读窗口样式，统一按 32 位无符号处理（避免 0x80000000 位被符号扩展搞乱）。</summary>
        public static uint GetStyle(IntPtr h)
        {
            long v = (IntPtr.Size == 8) ? GetWindowLongPtr64(h, GWL_STYLE).ToInt64()
                                        : GetWindowLong32(h, GWL_STYLE);
            return unchecked((uint)v);
        }

        public static void SetStyle(IntPtr h, uint style)
        {
            if (IntPtr.Size == 8)
                SetWindowLongPtr64(h, GWL_STYLE, new IntPtr(unchecked((int)style)));
            else
                SetWindowLong32(h, GWL_STYLE, unchecked((int)style));
        }

        // ==================================================================
        // 扩展样式（防闪用，见 DesktopHub.OnWindowShown 里那段「为什么光 SW_HIDE 不够」）
        // ==================================================================

        public const int GWL_EXSTYLE = -20;
        public const uint WS_EX_LAYERED = 0x00080000;
        public const uint LWA_ALPHA = 0x00000002;

        /// <summary>不进任务栏 / 不进 Alt+Tab（见 MakeTransparent 里为什么顺手要加它）。</summary>
        public const uint WS_EX_TOOLWINDOW = 0x00000080;

        /// <summary>「请把我列进任务栏」。explorer 的浏览窗口本来带它 —— 摘掉时要还回去。</summary>
        public const uint WS_EX_APPWINDOW = 0x00040000;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindow(IntPtr h, uint cmd);

        /// <summary>GW_OWNER —— 取属主窗口。</summary>
        public const uint GW_OWNER = 4;

        /// <summary>GW_CHILD —— 第一个子窗口（配 <c>GW_HWNDNEXT</c> 走一圈就是「直接子窗口」列表）。</summary>
        public const uint GW_CHILD = 5;

        /// <summary>GW_HWNDNEXT —— 同层的下一个窗口。</summary>
        public const uint GW_HWNDNEXT = 2;

        public static uint GetExStyle(IntPtr h)
        {
            long v = (IntPtr.Size == 8) ? GetWindowLongPtr64(h, GWL_EXSTYLE).ToInt64()
                                        : GetWindowLong32(h, GWL_EXSTYLE);
            return unchecked((uint)v);
        }

        public static void SetExStyle(IntPtr h, uint ex)
        {
            if (IntPtr.Size == 8)
                SetWindowLongPtr64(h, GWL_EXSTYLE, new IntPtr(unchecked((int)ex)));
            else
                SetWindowLong32(h, GWL_EXSTYLE, unchecked((int)ex));
        }

        /// <summary>
        /// 让窗口「就算被 Show 出来也是全透明的」：加 `WS_EX_LAYERED` + alpha=0。
        ///
        /// ★ 这是防闪的**兜底那一半**。光 `SW_HIDE` 挡不住 —— 日志实测（）每条都是
        /// 「事件后 0ms，**当时已可见**」：WinEvent 是投递到消息队列的，等我们收到 SHOW，
        /// explorer 那一帧**已经画在屏幕上了**。加了这层之后，无论它怎么 Show，画面都是透明的。
        ///
        /// ★ 顺带还管住了**任务栏**（光置透明只解决「画面闪」）：那扇窗在收编之前仍是个正常顶层窗口，
        ///   任务栏上会多出一个「文件资源管理器」按钮，直到 `SetParent` 成子窗口才消失（川报的
        ///   「加载时状态栏显示系统资源管理器图标、完成后消失」）。带 `WS_EX_TOOLWINDOW` 的窗口
        ///   任务栏与 Alt+Tab 都不收，所以顺手打上它、摘掉 `WS_EX_APPWINDOW`。
        ///
        /// ★ `taskbarBits` = 要不要动任务栏那两位。**CREATE 那一次必须传 false**，补法见
        ///   `ScheduleTaskbarBits`：
        ///   ⚠ explorer 建窗时看到 `WS_EX_TOOLWINDOW` 就**不建 Ribbon**，于是退回显示老式菜单栏
        ///   —— 内嵌窗口顶上那条 `文件(F) 编辑(E) 查看(V) 工具(T)` 白条就是这么来的。
        ///   物证：`probe/embed_menubar_state.py` 逐版回代，v1.14.0（还没打这一位）内嵌窗口顶上
        ///   和原生窗口一样是 Ribbon 25px、没有老式菜单栏；从打了这一位的那版起变成
        ///   「没有 Ribbon + 老式菜单栏 20px」，一直到现在。
        ///   SHOW 那一次仍会传 true 兜一下 —— 但那时窗口已经可见，任务栏多半已经按那一帧加了
        ///   按钮，真正的时机是 `ScheduleTaskbarBits` 守的那段空档。
        ///
        /// ⚠ 收编进标签之前 / 放它走之前**必须** `ClearTransparent`，
        ///   否则嵌进来的窗口会永远是隐形的（这个坑比闪一下严重得多）。
        /// </summary>
        public static void MakeTransparent(IntPtr h, bool taskbarBits)
        {
            try
            {
                uint ex = GetExStyle(h);
                uint want = ex | WS_EX_LAYERED;
                if (taskbarBits) want = (want | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW;
                if (want != ex) SetExStyle(h, want);
                if ((ex & WS_EX_LAYERED) == 0) SetLayeredWindowAttributes(h, 0, 0, LWA_ALPHA);
            }
            catch { }
        }

        /// <summary>
        /// 「不进任务栏」那两位的**延后一刻**：等 explorer 把 Ribbon 建出来、而窗口还没露脸时补上。
        ///
        /// 为什么必须延后（`probe/ribbon_timing_probe.py` 量的新窗时间线）：
        ///   +0ms    窗口出现，ex 里没有 `WS_EX_TOOLWINDOW`   ← 此刻 explorer 还没决定建 Ribbon
        ///   +242ms  Ribbon 出现
        ///   +625ms  窗口变可见                                ← 任务栏按这一帧的样式加了按钮
        ///   +698ms  SHOW 事件才到我们手里 ⇒ 那时补已经晚了一帧
        /// 两头都是硬约束：建窗那一次就带 TOOLWINDOW ⇒ explorer 不建 Ribbon（顶上那条白条）；
        /// 等 SHOW 事件才补 ⇒ 任务栏已经按「可见那一帧」加了按钮（图标闪一下）。
        /// 中间那 383ms 是唯一的空档 —— 本函数就守在那儿：轮询到 Ribbon 出现（说明 explorer
        /// 已经决定建它了）且窗口还没显示，立刻补上两位。
        ///
        /// ⚠ 判据用 `WS_EX_LAYERED` 当「我们动过这扇窗」的记号（跟 <see cref="ClearTransparent"/> 一致）：
        ///   窗口已经没了 / 已经被收编成子窗口 / 没被我们置过透明，一律不动 —— 这函数也可能
        ///   被别的路径的候选窗口捎带上，碰错窗口（尤其是 shell 自己那扇）是明令禁止的。
        /// </summary>
        public static void ScheduleTaskbarBits(IntPtr h)
        {
            if (h == IntPtr.Zero) return;
            System.Threading.Thread th = new System.Threading.Thread(delegate()
            {
                try
                {
                    for (int i = 0; i < 60; i++)            // 一轮 20ms，最多等 1.2 秒
                    {
                        if (!NativeMethods.IsWindow(h)) return;
                        if (GetParent(h) != IntPtr.Zero) return;          // 已被收编 ⇒ 任务栏早不管它
                        if (IsWindowVisible(h)) { ApplyTaskbarBits(h); return; }   // 已经露脸，补总比不补强
                        if (WinFind.ByClass(h, "UIRibbonCommandBarDock") != IntPtr.Zero)
                        {
                            ApplyTaskbarBits(h);
                            return;
                        }
                        System.Threading.Thread.Sleep(20);
                    }
                    ApplyTaskbarBits(h);                    // 兜底：这窗口压根不建 Ribbon 的类型
                }
                catch { }
            });
            th.IsBackground = true;
            th.Name = "TBE-任务栏标记";
            th.Start();
        }

        /// <summary>真去改那两位。见 <see cref="ScheduleTaskbarBits"/> 的时机说明。</summary>
        private static void ApplyTaskbarBits(IntPtr h)
        {
            try
            {
                uint ex = GetExStyle(h);
                if ((ex & WS_EX_LAYERED) == 0) return;      // 不是我们置过透明的窗口，别碰
                uint want = (ex | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW;
                if (want != ex) SetExStyle(h, want);
            }
            catch { }
        }

        /// <summary>
        /// 把 <see cref="MakeTransparent"/> 加的那两层去掉：透明与「不进任务栏」（后者只在
        /// 打过 `WS_EX_TOOLWINDOW` 的窗口上才存在，见那边 `taskbarBits` 的注释）。
        /// 顺手把 `WS_EX_APPWINDOW` 还回去 —— explorer 的浏览窗口本来就是任务栏窗口，
        /// 放手让它回桌面时得能重新出现在任务栏里（收编成子窗口后这一位本来也不起作用）。
        /// </summary>
        public static void ClearTransparent(IntPtr h)
        {
            try
            {
                uint ex = GetExStyle(h);
                // ⚠ 只有「被我们置过透明」的窗口才能还它本来面目 —— `WS_EX_LAYERED` 就是那个记号
                //   （`MakeTransparent` 一定同时加上它）。**不能**写成「跟期望值不一样就写」：
                //   这函数在 `AdoptWindow` / `ReleaseIfAbandoned` 里是对**任何候选窗口**调的，
                //   其中就有 shell 自己开的那扇 —— 对它动样式位是明令禁止的（见 TakeOverShellWindow：
                //   改坏了会留在 shell 里、退程序也不恢复，只有重启电脑才解）。
                if ((ex & WS_EX_LAYERED) == 0) return;
                SetExStyle(h, (ex & ~WS_EX_LAYERED & ~WS_EX_TOOLWINDOW) | WS_EX_APPWINDOW);
            }
            catch { }
        }

        // ==================================================================
        // 抓屏（侧边栏「摊开盖在内容上」那一下的底图，见 PaneGlass）
        //
        // 为什么不用 `WS_EX_LAYERED`：子窗口分层在 Win8+ 虽然允许，但它合成的是
        // **宿主窗口的背景**，不是盖住的那个兄弟窗口（我们嵌的是别的进程的 explorer）——
        // 实测「设了不透明度，背景完全看不出效果」。所以改成自己抓底图 + 自己按比例叠。
        // ==================================================================

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetDC(IntPtr h);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int ReleaseDC(IntPtr h, IntPtr dc);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h,
                                         IntPtr src, int sx, int sy, uint rop);

        public const uint SRCCOPY = 0x00CC0020;

        /// <summary>
        /// 抓一块**屏幕**像素（物理坐标；抓的是「屏幕上现在显示的样子」，所以别的进程的窗口也在里面）。
        /// ⚠ 返回的是 `Format32bppRgb`（不带 alpha）—— 屏幕 DC 没有 alpha 通道，
        ///   用带 alpha 的格式会整张透明，贴上去什么都看不见。
        /// </summary>
        public static System.Drawing.Bitmap GrabScreen(int x, int y, int w, int h)
        {
            if (w <= 0 || h <= 0) return null;
            try
            {
                System.Drawing.Bitmap bmp =
                    new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(bmp))
                {
                    IntPtr dst = g.GetHdc();
                    IntPtr src = GetDC(IntPtr.Zero);
                    try { BitBlt(dst, 0, 0, w, h, src, x, y, SRCCOPY); }
                    finally { ReleaseDC(IntPtr.Zero, src); g.ReleaseHdc(dst); }
                }
                return bmp;
            }
            catch { return null; }
        }

        /// <summary>把顶层窗口降级成子窗口：清掉边框类样式、加上 WS_CHILD。
        /// 必须在 SetParent 之前做（MSDN 对 SetParent 的硬要求）。</summary>
        public static uint ToChildStyle(uint style)
        {
            // WS_MINIMIZE 也要清：最小化的窗口变成子窗口后会是个怪状态（我们藏窗口时才不会带它，
            // 但保不齐别处给了最小化状态）。
            uint clear = WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_MINIMIZE;
            return (style & ~clear) | WS_CHILD;
        }

        public static string ClassOf(IntPtr h)
        {
            if (h == IntPtr.Zero) return "";
            StringBuilder sb = new StringBuilder(256);
            GetClassName(h, sb, sb.Capacity);
            return sb.ToString();
        }

        public static string TitleOf(IntPtr h)
        {
            if (h == IntPtr.Zero) return "";
            StringBuilder sb = new StringBuilder(512);
            GetWindowText(h, sb, sb.Capacity);
            return sb.ToString();
        }

        /// <summary>在顶层窗口里找某个 pid 名下、类名匹配的窗口。</summary>
        public static IntPtr FindTopWindow(int pid, string cls)
        {
            return FindTopWindow(pid, cls, true);
        }

        /// <summary>
        /// 同上，但可以指定「要不要只看可见的」。
        /// **我们主动藏起来的窗口也认得出来**（正在等它加载完那种），所以找新窗口时必须传 false。
        /// </summary>
        public static IntPtr FindTopWindow(int pid, string cls, bool visibleOnly)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindowsProc cb = null;
            cb = delegate(IntPtr h, IntPtr l)
            {
                if (visibleOnly && !IsWindowVisible(h)) return true;
                if (ClassOf(h) != cls) return true;
                if (ProcessIdOf(h).ToInt32() == pid) { found = h; return false; }
                return true;
            };
            EnumWindows(cb, IntPtr.Zero);
            GC.KeepAlive(cb);
            return found;
        }

        /// <summary>把所有顶层文件夹窗口的 HWND 收进集合（启动前快照：这些不是我们的）。</summary>
        public static void CollectCabs(HashSet<IntPtr> into)
        {
            EnumWindowsProc cb = null;
            cb = delegate(IntPtr h, IntPtr l)
            {
                string c = ClassOf(h);
                if (c == "CabinetWClass" || c == "ExploreWClass") into.Add(h);
                return true;
            };
            EnumWindows(cb, IntPtr.Zero);
            GC.KeepAlive(cb);
        }

        /// <summary>
        /// 这个 explorer 进程除了 <paramref name="except"/> 那个窗口之外，还有没有「别的靠山」？
        /// 用来回答一件事：关掉这个标签时，能不能把它的 explorer 进程一起结束。
        ///
        /// 认两种「不能动」的：
        ///   · `Shell_TrayWnd` / `Progman` —— 桌面 shell 本体（任务栏、桌面图标都归它），杀了整个外壳都要重启；
        ///   · 还有别的 `CabinetWClass` —— 用户或别的程序还在用它开着文件夹。
        /// 都没有 ⇒ 这个进程就是**我们这一个标签在撑着**，可以收掉。
        ///
        /// ⚠ 判据**不能**用「这个 pid 是不是我启动前就存在的」（老办法 `pidsBefore`）：
        ///   `explorer.exe /n,/separate` 起窗口时，系统**会把请求转交给已存在的 explorer 进程** ——
        ///   那种老进程照样只有我们这一个标签在用。按老办法会判成「不能杀」，于是它留在系统里
        ///   （窗口关了、进程活着），下次启动又被复用，越积越多；而这些「没有窗口的 explorer 进程」
        ///   会让 shell 的「打开资源管理器」（Win+E、开始菜单里那条）去激活一个不存在的窗口
        ///   ⇒ 表现就是按下去什么都没发生。
        /// </summary>
        public static bool ExplorerHasOtherWindows(int pid, IntPtr except)
        {
            if (pid == 0) return true;      // 问不出来就当成「有」，宁可不杀
            bool found = false;
            EnumWindowsProc cb = null;
            cb = delegate(IntPtr h, IntPtr l)
            {
                if (found) return true;
                if (h == except) return true;
                if (ProcessIdOf(h).ToInt32() != pid) return true;
                string c = ClassOf(h);
                if (c == "Shell_TrayWnd" || c == "Progman" ||
                    c == "CabinetWClass" || c == "ExploreWClass")
                {
                    found = true;
                    return false;
                }
                return true;
            };
            EnumWindows(cb, IntPtr.Zero);
            GC.KeepAlive(cb);
            return found;
        }

        /// <summary>桌面 shell 进程的 pid —— 拥有任务栏（`Shell_TrayWnd`）的那个 explorer。</summary>
        public static int ShellExplorerPid()
        {
            IntPtr tray = NativeMethods.FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) tray = NativeMethods.FindWindow("Progman", null);
            if (tray == IntPtr.Zero) return 0;
            return ProcessIdOf(tray).ToInt32();
        }

        /// <summary>
        /// 这个窗口是不是**桌面 shell 进程**开的。是的话，我们什么都不能对它做。
        ///
        /// 为什么要单独挡这一刀（踩过）：shell 进程自己也会开文件夹窗口 —— 用户从开始菜单/
        /// 任务栏/桌面打开的路径，窗口可能就是这个进程建的。这种窗口一旦被我们
        /// `SetParent` 进自己的窗口（或先藏一下），shell 那边「打开资源管理器」那条路
        /// （Win+E、开始菜单里的「文件资源管理器」，两者都走 shell 的同一个入口）就会去
        /// 复用/激活它自己那扇已经不正常的窗口 ⇒ **按下去毫无反应**；
        /// 而按具体路径新开一个窗口不受影响，所以表现是「Win+E 和开始菜单那条打不开，
        /// 开始菜单里点别的文件夹却没事」。
        /// ⚠ 这个损坏**留在 shell 进程里**，我们的程序退了也不会自己恢复，只能重启
        ///   那个 explorer 进程才好 —— 所以必须在动手之前就挡住，不能指望善后。
        /// </summary>
        public static bool IsShellOwned(IntPtr h)
        {
            if (h == IntPtr.Zero) return false;
            int shell = ShellExplorerPid();
            if (shell == 0) return false;          // 问不出 shell 是谁：按「不是」放行（FindWindow 基本不会失败）
            return ProcessIdOf(h).ToInt32() == shell;
        }

        // ==================================================================
        // 「启动时就存在的文件夹窗口」基线（加）
        //
        // 用来回答一个问题：这个文件夹窗口是**新开的**，还是本来就在那儿？
        //   · 本来就在那儿的（用户早就开着、从最小化还原、被别人重新显示）→ **不算新开**，别去动它；
        //   · 新开的 → 归我们收成标签。
        //
        // ⚠ 为什么不能再用「进程是不是启动前就有的」来判（老办法，`pidsBefore`）：
        //   实测 `explorer.exe /n,/separate` 起来的窗口**也可能落在启动前就存在的 explorer 进程里**。
        //   那时老办法会把我们自己的窗口判成「别人的」而不认领，
        //   于是 Hub 那个「谁来都抓」的监听反而把它抓走 —— 日志里就是这么串的：
        //   自己起的第4个窗口被 `Adopt` 当成「用户新开的」收走了（碰巧没错，但结构上就是错的）。
        //   按**窗口**（而不是进程）记基线，就没有这个歧义。
        // ==================================================================

        private static readonly HashSet<IntPtr> baselineCabs = new HashSet<IntPtr>();

        /// <summary>把「现在就已经开着的文件夹窗口」记成基线。程序刚起来时调一次。</summary>
        public static void SnapshotBaseline()
        {
            lock (baselineCabs)
            {
                baselineCabs.Clear();
                CollectCabs(baselineCabs);
            }
            Diag.Step("基线: 启动时已有 " + baselineCabs.Count + " 个文件夹窗口");
        }

        public static bool IsBaseline(IntPtr h)
        {
            lock (baselineCabs) { return baselineCabs.Contains(h); }
        }

        // ==================================================================
        // 「这个文件夹窗口已经归谁了」的登记表（加）
        //
        // 有了「谁来都抓」之后再也不能靠「启动前快照」认自己起的那个窗口了 ——
        // 两个监听会同时看见同一个新窗口（我们自己起的、和用户自己开的），
        // 也可能两个标签同时看见一个（连点两次新建）。这张表就是仲裁：
        // 谁先认领谁负责，另一个看见已登记就放手。
        // ==================================================================

        private static readonly HashSet<IntPtr> claims = new HashSet<IntPtr>();

        // 「用户点名要原生」的那几扇窗 —— 在关掉之前永不收编。
        //
        // 场景 = 标签右键「用原生资源管理器打开」（`DesktopHub.OpenNative`）。那边原来只记一个
        // **到点作废的让行期**，而一扇新窗会先后报两条事件（CREATE / SHOW）—— 第一条把凭据吃掉，
        // 第二条就没人拦了：用户看到的就是「刚开出来的原生窗，一会儿又被收成标签」（川报的就是这个）。
        // 所以凭据之外再认下**窗口本身**。
        //
        // 为什么挂在 `IsClaimed` 上而不是另加一处判定：所有「该不该碰这扇窗」的口
        // （`IsHideCandidate` / `IsShellTakeoverCandidate` / `IsCapturable` / `ScanCabs` / `FindNewCab`）
        // 第一句问的都是它 —— 挂在这儿一处就够，几条路自动全绕开。
        private static readonly Dictionary<IntPtr, DateTime> keeps = new Dictionary<IntPtr, DateTime>();

        /// <summary>认下这一扇「用户要的原生窗口」：直到它关掉为止都不收编（见 keeps 那段）。</summary>
        public static void MarkNativeKeep(IntPtr h)
        {
            if (h == IntPtr.Zero) return;
            lock (keeps)
            {
                // 句柄会被系统复用，所以隔一阵把已经不在的条目清掉 —— 留着就可能认错人
                // （把一扇新窗当成「那扇原生窗」放过去）。
                if (keeps.Count > 16)
                {
                    List<IntPtr> gone = null;
                    foreach (IntPtr k in keeps.Keys)
                    {
                        if (NativeMethods.IsWindow(k)) continue;
                        if (gone == null) gone = new List<IntPtr>();
                        gone.Add(k);
                    }
                    if (gone != null) for (int i = 0; i < gone.Count; i++) keeps.Remove(gone[i]);
                }
                keeps[h] = DateTime.Now;
            }
        }

        private static bool IsNativeKeep(IntPtr h)
        {
            if (h == IntPtr.Zero) return false;
            lock (keeps)
            {
                if (!keeps.ContainsKey(h)) return false;
                if (NativeMethods.IsWindow(h)) return true;
                keeps.Remove(h);        // 窗口已经没了：这一条作废（句柄可能被复用，不能一直认着）
                return false;
            }
        }

        public static bool IsClaimed(IntPtr h)
        {
            lock (claims) { if (claims.Contains(h)) return true; }
            return IsNativeKeep(h);      // 用户点名要原生的也算「已认领」（见 keeps 那段）
        }

        public static void Claim(IntPtr h)
        {
            lock (claims) { if (h != IntPtr.Zero) claims.Add(h); }
        }

        public static void Release(IntPtr h)
        {
            lock (claims) { if (h != IntPtr.Zero) claims.Remove(h); }
        }

        /// <summary>扫到的一扇「可能是我们要的」浏览窗口：句柄、属主 pid、地址栏读出来的路径。</summary>
        public sealed class CabSighting
        {
            public IntPtr H;
            public int Pid;
            /// <summary>地址栏读出来的路径；地址栏还没建好时是 null。</summary>
            public string Path;
        }

        // 这份缓存是「并发起标签页能不能真的变快」的关键，别删。
        //
        // 为什么必须有：等待窗口的那套轮询（`ExplorerHost.OnPoll`，25ms 一跳）**每个标签各一份，
        // 而且全都跑在同一个 UI 线程上**。而一轮扫描要 `EnumWindows` 走一遍全屏顶层窗、
        // 每个窗问一次跨进程 `GetClassNameW` —— 本机实测 723 个顶层窗时一轮 4.5ms。
        // 4 个标签并发等窗口 ⇒ 每 25ms 里 18ms 都花在扫屏上（UI 线程占 72%），
        // 嵌入、布局、重绘全排在它后面 ⇒ 并发起的那几个 explorer 反被自己的轮询拖住，
        // 单窗就绪从 1.0s 涨到 2.9~5.8s，四路并发白干（实测总耗时跟串行一样）。
        // 共用这一份之后同一遍扫描 40ms 内只做一次，占用降到十几%。
        private static List<CabSighting> scanCache;
        private static DateTime scanAt = DateTime.MinValue;
        private static readonly object scanLock = new object();
        private const int ScanFreshMs = 40;

        // ------------------------------------------------------------------
        // 每个「正在等着被认领的窗口」的账 —— 地址栏**不在 UI 线程上读**。
        //
        // 这里有两条实测硬事实：
        //   1. 扫描本身很便宜（一遍 `EnumWindows` + 逐窗类名，723 个顶层窗 4.5ms）；
        //   2. 读地址栏很贵 —— 跨进程 `SendMessage(WM_GETTEXT)`，对方正在建视图那阵子
        //      **一次 100~400ms**（本机量到过一轮扫描 822ms 里 799ms 全在两次读上面）。
        //
        // 而等待窗口的轮询是 25ms 一跳、一屏可能有七八个候选窗口、全跑在同一个 UI 线程上 ⇒
        // 不节流就是每 25ms 拿几十上百毫秒去读它，界面被按在 `SendMessage` 里等对方 ——
        // 用户报的「非激活标签还在加载时程序几乎不可用」就是这个。
        //
        // 所以：**读的动作交给后台线程**（`ProbeWorkerLoop`，默认 `ProbeWorkers` 条），UI 线程只负责
        // 「决定该不该读」和「读回来没有」，一秒都不等它。同时：
        //   · 新窗口先晾 `AddressSettleMs` 再去读（它这会儿正在建视图，读了也是白读）；
        //   · 读到就记住（`Path`），**重读间隔按读到的次数翻倍**（刚看到时值可能是旧值，
        //     勤快点；稳了就别去烦它）；
        //   · 读空就退避着再试（退避上限很小 —— 读空是它没准备好，不是我们付不起）。
        //
        // 归属判据一点没松：还是「地址栏内容 == 我要开的路径」（见 `FindNewCab`），
        // 只是把「谁来读」从 UI 线程换成了后台线程。
        // 窗口被认领/嵌进去之后就离开候选名单，账本跟着清掉（见 ScanCabs 尾部）。
        // ------------------------------------------------------------------
        private sealed class CabProbe
        {
            public DateTime NextProbe;      // 早于这个时刻就别去读它的地址栏
            public string Path;             // 读到过的路径
            public int Oks;                 // 读到过几次（重读间隔按它翻倍）
            public int Fails;               // 读空过几次（退避用）
            public bool Busy;               // 后台线程正拿着它去读，别重复派活
        }
        private static readonly Dictionary<IntPtr, CabProbe> probes = new Dictionary<IntPtr, CabProbe>();

        /// <summary>新出现的窗口先晾这么久再去读地址栏（它这会儿正在建视图，读了也是白读）。</summary>
        private const int AddressSettleMs = 300;
        /// <summary>读到过 1/2/3 次之后，隔多久才允许再读一次（防止读到「还没导航过去时的旧值」）。</summary>
        private static readonly int[] AddressRecheckMs = new int[] { 0, 1000, 2000, 4000 };
        /// <summary>读到第 3 次以后就不再勤读了，按这个间隔兜底。</summary>
        private const int AddressSteadyMs = 8000;
        /// <summary>
        /// 读空（地址栏还没值）之后第一次隔多久再试，以及退避的上限。
        /// ⚠ 上限**不能**开太大：地址栏大约在窗口出现 1 秒后才可读，退避到 3 秒就等于把「发现」
        ///   往后推 3 秒（实测过：一轮还原从 9 秒涨到 10 秒）。读空是它没准备好，不是我们付不起。
        /// </summary>
        private const int AddressRetryMs = 150;
        private const int AddressRetryMaxMs = 600;

        private static readonly object probeLock = new object();
        private static readonly Queue<IntPtr> probeQueue = new Queue<IntPtr>();
        /// <summary>
        /// 派活信号 —— 用**计数信号量**而不是 `AutoResetEvent`：后者一次 `Set` 只唤醒一条线程，
        /// 于是两条读线程里有一条会一直睡到下次入队，白占着不算数。这里「入队几次就 Release 几次」，
        /// 几条线程谁先醒谁取一个，正好按需分配。
        /// </summary>
        private static readonly System.Threading.Semaphore probeSem =
            new System.Threading.Semaphore(0, 1000);
        /// <summary>
        /// 后台读地址栏的线程条数。
        ///
        /// ⚠ 为什么不是 1 条（2026-09-25 实测）：一个候选一次读要么 4~8ms（对方闲）、要么几百 ms
        ///   （对方在建视图）。还原时 4 个 explorer 同时在建视图 ⇒ 4 个候选**串行**读就要 1.2~1.3 秒，
        ///   而「第一次发现窗口」直接决定整轮还原什么时候开始收编。实测单线程时首次发现要等到
        ///   起进程后 2.6 秒，总时长卡在 9.6 秒。
        ///   加线程的收益来自「读的是**不同 explorer 进程**」—— 每个标签一个 explorer，读 A 不会挡住读 B，
        ///   所以并行读是纯赚（同进程的多个候选自然还是被那个进程的 UI 线程串行处理，没坏处）。
        ///   定 2 条：够把首次发现拉近一半，又不会把正在建视图的 explorer 一起压得更慢。
        /// </summary>
        private const int ProbeWorkers = 2;
        private static int probeWorkersStarted;

        private static void EnsureProbeWorker()
        {
            // ⚠ 这个函数是在 `probeLock` **里面**被调的，所以里面绝对不能再取 `probeLock`（会死锁）。
            //   `Interlocked` 天然无锁，够用。
            if (System.Threading.Interlocked.Increment(ref probeWorkersStarted) > ProbeWorkers) return;
            System.Threading.Thread t = new System.Threading.Thread(ProbeWorkerLoop);
            t.IsBackground = true;        // 主程序退出时别拦着
            t.Name = "TBE-地址栏读取";
            t.Start();
        }

        /// <summary>
        /// 后台读地址栏的线程。**取一个、读一个**：取不到就回去等信号。
        /// 读完把结果记回账本上，UI 线程下一轮扫描就能用上。
        /// </summary>
        private static void ProbeWorkerLoop()
        {
            while (true)
            {
                probeSem.WaitOne();
                IntPtr h;
                lock (probeLock)
                {
                    // 可能被另一条线程先取走了（我们两条线程抢同一个队列）—— 那这轮就没活干了
                    if (probeQueue.Count == 0) continue;
                    h = probeQueue.Dequeue();
                }
                string got = null;
                // ★ 先问 shell 自己的浏览器清单（`ShellWindows.LocationURL`，按 hwnd 对上号）：
                //   它建窗后 0.3~0.6 秒就有值，而地址栏要等窗口把那一排工具条建出来、更晚；
                //   而且这条路**不往那扇窗发消息**，对方忙也拖不住我们。
                //   实测这一句就是「重启进程后第一次开标签」里最长那一截的解药：原来「起 explorer」
                //   到「认出自己那扇窗」要等 2.0 秒（地址栏读空后按 150/300/600/1200ms 退避重试，
                //   外加 1.2 秒的负缓存），换上它之后第一轮问就能命中。
                try { got = ShellBrowserReg.PathOfWindow(h); }
                catch { }
                if (got == null)
                {
                    try { got = AddressPathOf(h); }
                    catch { }
                }
                lock (probeLock)
                {
                    CabProbe p;
                    if (probes.TryGetValue(h, out p))
                    {
                        if (got != null)
                        {
                            p.Path = got;
                            p.Oks++;
                            p.NextProbe = DateTime.Now.AddMilliseconds(
                                (p.Oks < AddressRecheckMs.Length) ? AddressRecheckMs[p.Oks] : AddressSteadyMs);
                        }
                        else
                        {
                            p.Fails++;
                            int wait = AddressRetryMs << Math.Min(p.Fails - 1, 3);   // 150 → 300 → 600
                            p.NextProbe = DateTime.Now.AddMilliseconds(Math.Min(wait, AddressRetryMaxMs));
                        }
                        p.Busy = false;
                    }
                }
            }
        }

        /// <summary>一轮扫描里的统计（只用来打日志）。</summary>
        private sealed class ScanStat { public int Disp; public int Busy; }

        /// <summary>
        /// 决定「这一轮要不要去读这个窗口的地址栏」，要读就**派给后台线程**，立刻返回手上有的值（可能为 null）。
        /// 一律不阻塞调用方 —— 调用方是 UI 线程上的轮询。
        /// </summary>
        private static string ProbePath(IntPtr h, ScanStat st)
        {
            lock (probeLock)
            {
                CabProbe p;
                if (!probes.TryGetValue(h, out p))
                {
                    p = new CabProbe();
                    p.NextProbe = DateTime.Now.AddMilliseconds(AddressSettleMs);
                    probes[h] = p;
                }
                st.Busy += p.Busy ? 1 : 0;
                if (p.Busy || DateTime.Now < p.NextProbe) return p.Path;
                // 视图还没建出来的窗口一定读不到（实测文件列表比地址栏早约 0.2 秒；地址栏文本要等视图
                // 建好才有值）—— 这一下不派活也不发消息，白省一轮。
                if (WinFind.ByClass(h, "SHELLDLL_DefView") == IntPtr.Zero) return p.Path;

                // 派活。`NextProbe` 先按「读空」的退避推到最近的一档：万一这条读回来是空，
                // 也不会隔一轮就又派一次；读到值了后台线程会把它改成按倍数拉的间隔。
                p.Busy = true;
                p.NextProbe = DateTime.Now.AddMilliseconds(AddressRetryMs);
                probeQueue.Enqueue(h);
                st.Disp++;
                EnsureProbeWorker();
                probeSem.Release();      // 计数信号量：入队几次就放几个，两条线程谁先醒谁取
                return p.Path;
            }
        }

        /// <summary>
        /// 扫一遍顶层窗口，把「新出现、不是 shell 自己的、还没被谁认领」的文件夹窗口连同
        /// 各自地址栏里的路径一起交出来。结果缓存 `ScanFreshMs`，让同时等窗口的几个标签
        /// 共用同一遍扫描（理由见上面 `scanCache` 那段）。
        ///
        /// 三条刻意的设计：
        ///   · **不看可见性**：我们可能已经把它藏起来了（怕它闪），藏了也得认得出来；
        ///   · **不枚举进程列表**：这个函数 25ms 就要跑一次，`GetProcessesByName` 太贵，
        ///     而窗口只能由进程建出来，直接扫窗口就够了（有窗口 ⇒ 进程必然存在）；
        ///   · 排掉两种「本来就在那儿的东西」：**启动基线**上的窗口（他早就开着的）
        ///     和**已被认领**的窗口（我们自己另一个标签已经收走的）。
        /// </summary>
        public static List<CabSighting> ScanCabs()
        {
            lock (scanLock)
            {
                if (scanCache != null && (DateTime.Now - scanAt).TotalMilliseconds < ScanFreshMs)
                    return scanCache;
            }

            DateTime t0 = DateTime.Now;
            ScanStat st = new ScanStat();
            HashSet<IntPtr> stillThere = new HashSet<IntPtr>();
            List<CabSighting> list = new List<CabSighting>();
            EnumWindowsProc cb = null;
            cb = delegate(IntPtr h, IntPtr l)
            {
                string c = ClassOf(h);
                if (c != "CabinetWClass" && c != "ExploreWClass") return true;
                if (IsBaseline(h)) return true;
                if (IsClaimed(h)) return true;
                // shell 进程自己的窗口绝不能被当成「我们要嵌的那个」——理由见 IsShellOwned。
                // 代价是这种窗口撑不起标签（那一条会超时失败，桌面上留一个原生窗口），
                // 比把桌面外壳弄坏强得多。
                if (IsShellOwned(h)) return true;
                int p = ProcessIdOf(h).ToInt32();
                if (p == 0) return true;

                stillThere.Add(h);
                list.Add(new CabSighting { H = h, Pid = p, Path = ProbePath(h, st) });
                return true;
            };
            EnumWindows(cb, IntPtr.Zero);
            GC.KeepAlive(cb);

            // 这轮没见到的账清掉（窗口关了、或者已经被认领/嵌进去了）—— 账本别无限长
            lock (probeLock)
            {
                if (probes.Count > stillThere.Count)
                {
                    List<IntPtr> gone = null;
                    foreach (IntPtr k in probes.Keys)
                    {
                        if (stillThere.Contains(k)) continue;
                        if (gone == null) gone = new List<IntPtr>();
                        gone.Add(k);
                    }
                    if (gone != null)
                        for (int i = 0; i < gone.Count; i++) probes.Remove(gone[i]);
                }
            }

            lock (scanLock) { scanCache = list; scanAt = DateTime.Now; }
            // 这一笔直接花在 UI 线程上，超过 20ms 就是界面在等它（debug 打开时才写）
            int cost = (int)(DateTime.Now - t0).TotalMilliseconds;
            if (cost >= 20) Diag.Step("EmbedApi: 扫一遍顶层窗 " + cost + "ms（候选 " + list.Count
                + " 扇，派出读地址栏 " + st.Disp + " 次，在途 " + st.Busy + "）");
            return list;
        }

        /// <summary>
        /// 从上面那份扫描结果里挑出「这个标签要的那一扇」。
        ///
        /// `pidsBefore` 参数留着不用了 —— 按进程判会串台（见 `IsBaseline` 那段）。
        /// `relax` 的含义是「不做地址校验」；`wantedPath` 为 null 时也不校验（串行时只有一个候选）。
        ///
        /// <paramref name="matchedByPath"/> = 这一扇是**按地址栏内容**命中的（也就是说已经有一份
        /// 「它就是我要的那个」的硬证据了）。调用方靠它决定还要不要再复核一遍 — 见 `ExplorerHost.OnPoll`。
        /// </summary>
        public static IntPtr FindNewCab(HashSet<IntPtr> cabsBefore, HashSet<int> pidsBefore,
            bool relax, string wantedPath, out int pidOfFound, out bool matchedByPath)
        {
            pidOfFound = 0;
            matchedByPath = false;
            string wanted = (wantedPath != null && !relax) ? PathRules.Store(wantedPath) : null;
            List<CabSighting> list = ScanCabs();
            for (int i = 0; i < list.Count; i++)
            {
                CabSighting c = list[i];
                if (cabsBefore.Contains(c.H)) continue;
                if (IsClaimed(c.H)) continue;
                // 「这扇窗是不是我要开的那个」—— 并发起 explorer 时**必须**判这一条：
                // 一次冒出好几个窗口，「新出现的就是我的」不再成立，谁抢到谁的窗是随机的
                //（那正是从前不能并发的原因）。判据 = 它地址栏读出来的路径正好是我们要开的那个；
                // **读不出来时一律不算**（地址栏比窗口晚约半秒才建好），这样它自己的属主才有机会认领。
                // 串行时（wanted 为 null）不判 —— 只有一个候选，省掉那半秒。
                if (wanted != null)
                {
                    if (string.IsNullOrEmpty(c.Path)) continue;
                    if (!PathRules.Same(c.Path, wanted)) continue;
                    matchedByPath = true;
                }
                pidOfFound = c.Pid;
                return c.H;
            }
            return IntPtr.Zero;
        }

        [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
        private static extern uint GetWindowThreadProcessIdPid(IntPtr h, out uint pid);

        public static IntPtr ProcessIdOf(IntPtr h)
        {
            uint pid;
            GetWindowThreadProcessIdPid(h, out pid);
            return new IntPtr(pid);
        }

        // ==================================================================
        // 自绘标题栏 / 派快捷键 用到的
        // ==================================================================

        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        public static extern IntPtr SendMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);

        [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageText(IntPtr h, uint msg, IntPtr w, StringBuilder l);

        private const uint WM_GETTEXT = 0x000D;

        /// <summary>
        /// 读任意窗口的文本，**跨进程也行**。
        ///
        /// 这里必须走 `WM_GETTEXT`（系统消息，参数会被跨进程编组），
        /// 不能用 `GetWindowText` —— 后者对别的进程只拿得到「标题」那一类，
        /// 像地址栏 `ToolbarWindow32` 这种把文本存在自己内部的地方会永远返回空。
        /// </summary>
        public static string WindowTextOf(IntPtr h)
        {
            if (h == IntPtr.Zero) return "";
            try
            {
                StringBuilder sb = new StringBuilder(1024);
                SendMessageText(h, WM_GETTEXT, new IntPtr(sb.Capacity), sb);
                return sb.ToString();
            }
            catch { return ""; }
        }

        /// <summary>
        /// 找到嵌进来那个 explorer 窗口的**地址栏**。
        ///
        /// 判据只有一条：`ToolbarWindow32` 而且窗口文本里带「: 」——
        /// 地址栏那一条是 `地址: &lt;当前地址&gt;`，而同一个 ReBar 里另外两条
        /// （`导航按钮`、`地址区段工具栏`）都没有冒号，所以「带冒号的 toolbar」就是地址栏，
        /// 不必写死 `地址: ` 这个**跟着系统语言变**的前缀。
        ///
        /// 为什么要找它：这是唯一一条「按我们手上这个 HWND 读、又拿得到当前文件夹」的路
        /// （试过并且不行的两条：ShellWindows/IWebBrowser2 被 SetParent 之后回报的 HWND
        /// 变成我们的顶层窗口，一张桌面上的标签全撞成一个 key；AccessibleObjectFromWindow
        /// 的 OBJID_NATIVEOM 在 CabinetWClass 上直接 E_FAIL）。
        /// </summary>
        public static IntPtr FindAddressBand(IntPtr cab)
        {
            if (cab == IntPtr.Zero) return IntPtr.Zero;
            try
            {
                // 首选：地址栏固定住的那个 `Breadcrumb Parent`（结构判据，跟系统语言无关）
                IntPtr crumb = WinFind.ByClass(cab, "Breadcrumb Parent");
                if (crumb != IntPtr.Zero)
                {
                    IntPtr t = WinFind.ByClass(crumb, "ToolbarWindow32");
                    if (t != IntPtr.Zero && WindowTextOf(t).IndexOf(": ", StringComparison.Ordinal) >= 0) return t;
                }

                // 兜底：整个窗口里「文本带冒号」的那个 toolbar（地址栏是 `地址: <当前地址>`，
                // 同一条 rebar 上另外两条 `导航按钮` / `地址区段工具栏` 都没有冒号）
                foreach (IntPtr h in WinFind.All(cab))
                {
                    if (string.Compare(WinFind.ClassOf(h), "ToolbarWindow32",
                            StringComparison.OrdinalIgnoreCase) != 0) continue;
                    if (WindowTextOf(h).IndexOf(": ", StringComparison.Ordinal) >= 0) return h;
                }
            }
            catch { }
            return IntPtr.Zero;
        }

        /// <summary>
        /// 读一个**别人的**资源管理器窗口现在开着哪个文件夹（不碰它的窗口对象，纯读）。
        ///
        /// 用途：接管 shell 打开的文件夹 —— shell 那条路建出来的窗口不能收进来（见 IsShellOwned），
        /// 所以改成「读出它要去哪儿 → 关掉它 → 用我们自己的 explorer 把同一个文件夹开成标签」。
        /// 地址栏是**唯一一条**「按手上这个 HWND 读、又拿得到当前文件夹」的路，理由见 FindAddressBand。
        ///
        /// ⚠ 地址栏给出来的**不一定是路径**：已知文件夹（「图片」「视频」）与虚拟位置（「此电脑」）
        /// 给的是**显示名**。所以读完先过 `PathRules.Store` 翻一遍，只有「翻完能交给 explorer 开」
        /// 才返回；否则一律 null。「开始菜单点『资源管理器 / 图片 / 视频』毫无反应」就是这里只认
        /// `Directory.Exists` 造成的：显示名连不成路径 ⇒ 全程判定「读不到」⇒ 窗口被放过。
        /// 之所以宁可 null 也不能把原文交出去：调用方是**先关窗再开标签**（`TakeOverShellWindow`），
        /// 交一个开不了的路径进去 = 原生窗没了、标签也没出来，比不动它糟得多。
        /// </summary>
        public static string AddressPathOf(IntPtr cab) { return AddressPathOf(cab, false); }

        /// <summary>
        /// 同上，但 <paramref name="force"/> = `true` 时**不信**「这个窗口还没有地址栏」那条负缓存。
        ///
        /// 给「正盯着一个窗口、等它长出地址栏」的调用方用（shell 转生那种 250ms 轮询）：
        /// 负缓存的寿命是 `BandMissTtlMs`(1200ms)，而 `DrainShell` 的超时也是 1200ms ——
        /// 第一次没读到就把整个等待期盖住了，表现就是「从开始菜单点资源管理器，原生窗先在屏幕上
        /// 待一秒多才闪进我们的程序」（用户报的「捕获变慢好多」）。
        /// </summary>
        public static string AddressPathOf(IntPtr cab, bool force)
        {
            try
            {
                IntPtr band = CachedAddressBand(cab, force);
                if (band == IntPtr.Zero) return null;
                string raw = WindowTextOf(band);
                // 「地址: <当前地址>」——前缀跟着系统语言变，所以只认第一个冒号加空格
                int i = raw.IndexOf(": ", StringComparison.Ordinal);
                if (i < 0) return null;
                string p = raw.Substring(i + 2).Trim();
                if (p.Length == 0) return null;
                string stored = PathRules.Store(p);          // 显示名 → `::` / `shell:` / 真路径
                return PathRules.Restorable(stored) ? stored : null;
            }
            catch { return null; }
        }

        /// <summary>cab → 它的地址栏（`ToolbarWindow32`）；`Band == Zero` = 找过了，这个窗口当时还没有。见 <see cref="CachedAddressBand"/>。</summary>
        private sealed class BandRef { public IntPtr Band; public DateTime At; }
        private static readonly Dictionary<IntPtr, BandRef> bandCache = new Dictionary<IntPtr, BandRef>();
        /// <summary>「这个窗口还没有地址栏」这个结论能信多久 —— 窗口还在长的时候别每轮重找一遍。</summary>
        private const int BandMissTtlMs = 1200;

        /// <summary>
        /// 取（并缓存）某个窗口的地址栏句柄，拿不到返回 `Zero`。
        ///
        /// 为什么要缓存：找它得把整棵子树 `EnumChildWindows` 走一遍 + 读几个 toolbar 的文本
        ///（跨进程 `SendMessage`，对方忙的时候几百毫秒），而这个函数是被**25ms 一轮的扫描**反复调用的
        ///（见 `ScanCabs`）—— 每轮重走一遍纯属白烧。句柄可能被系统回收后复用，所以用之前一律
        /// `IsDescendant` 复核（那个函数本来就是为它写的）。
        ///
        /// ⚠ 加锁是必须的：现在**后台线程**也会调 `AddressPathOf`（见 `ProbePath`），而这个表是两级字典，
        /// 一边读一边写会把它写坏。但**找句柄那一步（`FindAddressBand`）绝不能抱锁做** ——
        /// 它有几百毫秒的跨进程读，抱着锁做就等于 UI 线程反过来被后台线程挡住
        ///（实测：一轮扫描因此涨到 915ms，比不加后台线程还糟）。
        /// </summary>
        private static IntPtr CachedAddressBand(IntPtr cab, bool force)
        {
            DateTime now = DateTime.Now;
            lock (probeLock)
            {
                BandRef r;
                if (bandCache.TryGetValue(cab, out r))
                {
                    if (r.Band != IntPtr.Zero)
                    {
                        if (NativeMethods.IsWindow(r.Band) && IsDescendant(cab, r.Band)) return r.Band;
                        bandCache.Remove(cab);                                  // 句柄没了 / 被复用 → 重找
                    }
                    // 负缓存只服务「一轮扫一大片窗口」那种调用方；`force` 的调用方正等着这个窗口
                    // 长出地址栏，错过这一次就等于把它的等待期整个盖掉（见 AddressPathOf 的注释）。
                    else if (!force && (now - r.At).TotalMilliseconds < BandMissTtlMs) return IntPtr.Zero;
                    else bandCache.Remove(cab);
                }
            }

            IntPtr band = FindAddressBand(cab);           // ← 不抱锁：这一步可能要几百毫秒

            lock (probeLock)
            {
                if (bandCache.Count > 128) bandCache.Clear();   // 窗口换了一茬就整批丢掉，别让它无限长
                // `force` 的调用方不吃自己的负结果：它下一轮（250ms 后）还要再来问，
                // 存进去就变成「自己要等 1.2 秒」，等于 back 到刚修掉的那个 bug。
                if (band != IntPtr.Zero || !force)
                    bandCache[cab] = new BandRef { Band = band, At = DateTime.Now };
            }
            return band;
        }

        /// <summary>
        /// p 是不是 root 的后代（跨进程也能判）。
        /// 用来防「句柄被回收后瞎认」：地址栏那个 toolbar 是我们缓存下来的，
        /// 万一 shell 把它换成了别的窗口而句柄号被复用，至少能保证读到的还是自己那棵树里的东西。
        /// </summary>
        public static bool IsDescendant(IntPtr root, IntPtr p)
        {
            if (root == IntPtr.Zero || p == IntPtr.Zero) return false;
            try
            {
                for (int i = 0; i < 32 && p != IntPtr.Zero; i++)
                {
                    if (p == root) return true;
                    p = GetParent(p);
                }
            }
            catch { }
            return false;
        }

        /// <summary>`地址: D:\xxx` → `D:\xxx`。
        /// 取第一个「: 」之后的全部 —— Windows 的路径/文件名里不可能出现冒号，所以这个切法不会切错。</summary>
        public static string StripAddressPrefix(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int i = s.IndexOf(": ", StringComparison.Ordinal);
            string r = (i >= 0) ? s.Substring(i + 2) : s;
            return r.Trim();
        }

        // ⚠ 64 位下没有 `GetClassLongPtr` 这个导出，必须点 W/A 后缀那颗。
        [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
        private static extern IntPtr GetClassLongPtr(IntPtr h, int index);

        private const uint WM_GETICON = 0x007F;
        private const int ICON_SMALL = 0;
        private const int ICON_BIG = 1;
        private const int ICON_SMALL2 = 2;
        private const int GCLP_HICON = -14;
        private const int GCLP_HICONSM = -34;

        /// <summary>
        /// 窗口**自己**挂着的那颗图标。
        ///
        /// 关键事实（实测）：explorer 会按**当前文件夹**换掉这个窗口的图标 ——
        /// 在「图片」里读出来的是图片文件夹那颗照片图标，把它和 `SHGetFileInfo(该文件夹)` 给的
        /// 32px 图标逐像素比对，**差异 = 0**。所以「标签左边显示当前文件夹的实时图标」
        /// 不需要自己拼路径（也拼不出来：/n,/separate 的窗口不在 shell 的窗口集合里），
        /// 直接读它就行，导航之后它会自己变。
        ///
        /// ⚠ 返回的是**别人进程**的 HICON：可以拿去 DrawIconEx（USER 对象是会话级共享的），
        /// 但**绝对不能 DestroyIcon**，也不要在导航之后继续持有 —— 立刻画进自己的位图再放手。
        /// 优先取大图标(32px)：标签图标是 Px(16)=24@150%，32 缩下来比 16 放大清晰得多。
        /// </summary>
        public static IntPtr WindowIcon(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return IntPtr.Zero;
            try
            {
                IntPtr h = SendMessageW(hwnd, WM_GETICON, new IntPtr(ICON_BIG), IntPtr.Zero);
                if (h == IntPtr.Zero) h = SendMessageW(hwnd, WM_GETICON, new IntPtr(ICON_SMALL2), IntPtr.Zero);
                if (h == IntPtr.Zero) h = SendMessageW(hwnd, WM_GETICON, new IntPtr(ICON_SMALL), IntPtr.Zero);
                if (h == IntPtr.Zero) h = GetClassLongPtr(hwnd, GCLP_HICON);
                if (h == IntPtr.Zero) h = GetClassLongPtr(hwnd, GCLP_HICONSM);
                return h;
            }
            catch { return IntPtr.Zero; }
        }

        [DllImport("user32.dll")]
        public static extern bool IsZoomed(IntPtr h);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetGUIThreadInfo(uint tid, ref GUITHREADINFO info);

        public const int WM_NCLBUTTONDOWN = 0x00A1;
        /// <summary>非客户区双击 —— 补这条给 DefWindowProc 就等于双击标题栏（最大化 / 还原）。</summary>
        public const int WM_NCLBUTTONDBLCLK = 0x00A3;
        public const int HTCAPTION = 2;

        public const uint WM_KEYDOWN = 0x0100;
        public const uint WM_KEYUP = 0x0101;

        public const int VK_SHIFT = 0x10;
        public const int VK_CONTROL = 0x11;
        public const int VK_MENU = 0x12;      // Alt
        public const int VK_DELETE = 0x2E;
        public const int VK_RETURN = 0x0D;
        public const int VK_V = 0x56;
        public const int VK_Y = 0x59;
        public const int VK_Z = 0x5A;
        public const int VK_N = 0x4E;
        public const int VK_F2 = 0x71;

        /// <summary>某个线程当前拿到键盘焦点的窗口（跨进程也能问）。</summary>
        public static IntPtr FocusedWindowOf(uint tid)
        {
            if (tid == 0) return IntPtr.Zero;
            try
            {
                GUITHREADINFO g = new GUITHREADINFO();
                g.cbSize = Marshal.SizeOf(typeof(GUITHREADINFO));
                if (GetGUIThreadInfo(tid, ref g)) return g.hwndFocus;
            }
            catch { }
            return IntPtr.Zero;
        }

        /// <summary>
        /// 嵌入窗口顶部那条「explorer 留给标题栏、但子窗口不会画」的高度。
        ///
        /// 原生窗口里这条就是标题栏 + 快速访问工具栏的位置；降级成 WS_CHILD 之后它里面是空的，
        /// 但布局上**仍然被占着**（实测 45px @150%）。量出来上移裁掉，才能让 ribbon 顶到我们的自绘工具栏下面。
        /// 取「直接子窗口里最靠上的那个」的 top 减掉 cab 的 top。
        ///
        /// 判据用**自身的 WS_VISIBLE 位**，不用 `IsWindowVisible()` —— 后者要求「自己 + 所有祖先」都带
        /// WS_VISIBLE，而防闪流程里 cab 是藏着的，它所有子窗口都会返回 false，循环全被跳过、恒量出 0。
        /// 我们只想排掉「自己故意立了 WS_VISIBLE」的宿主窗（典型 UIRibbonWorkPane），不需要管祖先。
        /// </summary>
        public static int TopBlankOf(IntPtr cab)
        {
            if (cab == IntPtr.Zero) return 0;
            try
            {
                WRECT cr;
                if (!GetWindowRect(cab, out cr)) return 0;
                int min = int.MaxValue;
                foreach (IntPtr k in WinFind.All(cab))
                {
                    if (GetParent(k) != cab) continue;        // 只要直接子
                    if ((GetStyle(k) & WS_VISIBLE) == 0) continue;   // 自己没立 WS_VISIBLE 的（宿主窗）不算
                    if (ClassOf(k) == "UIRibbonWorkPane") continue;   // 隐藏的宿主窗，位置不可信
                    WRECT kr;
                    if (!GetWindowRect(k, out kr)) continue;
                    if (kr.Height <= 0) continue;
                    if (kr.Top < min) min = kr.Top;
                }
                if (min == int.MaxValue) return 0;
                int d = min - cr.Top;
                return d > 0 ? d : 0;
            }
            catch { return 0; }
        }

        // ==================================================================
        // 收起内嵌窗口里那条「文件(F) 编辑(E) 查看(V) 工具(T)」菜单栏
        //
        // 用户报的「多出这个白条，我关不掉」。结构（探针 `probe/cab_tree_dump.py` 实测，
        // 2026-09-25，拿我们的内嵌窗口和同一命令起的原生窗口逐层对表）：
        //
        //   CabinetWClass
        //     ├ WorkerW                      ← 地址栏那一行（ReBar 里有 Travel/Up/Address/Search 四条 band）
        //     └ ShellTabWindowClass          ← 文件视图的容器
        //         ├ DUIViewWndClassName      ← 文件列表
        //         ├ WorkerW (20px)           ← ★ 菜单栏：里面是一条 ReBarWindow32
        //         └ msctls_statusbar32       ← 状态栏（默认收着）
        //
        // 原生窗口里那个 WorkerW 的**高度是 0**（收着的，菜单栏不占位置）。
        // 我们内嵌的窗口里它被撑成 20px —— 因为原生窗口顶上有 Ribbon（`UIRibbonCommandBarDock`
        // 25px），而降级成 `WS_CHILD` 之后 Ribbon 根本没建出来，explorer 就退回「显示菜单栏」。
        //
        // 两条硬事实（都是实测踩出来的）：
        //
        //   ① 光 `ShowWindow(SW_HIDE)` 不行：位置是 explorer 自己摆的，藏掉它文件视图仍留在 y=+20，
        //      那条位置由 `ShellTabWindowClass` 自己刷成一片底色 —— **白条还在**。
        //      所以必须同时把 DUIView 撑回容器整个高度。
        //   ② 得是「**压高度**」而不是 SW_HIDE：菜单栏在 Windows 里是「收了但能叫出来」的东西（按
        //      Alt / F10）。`SW_HIDE` 是把窗口从系统眼里摘掉（清了 `WS_VISIBLE`），explorer 自己那套
        //      菜单逻辑再想把它立起来也白搭 —— 川报的「我再自己打开菜单栏，它也不显示了」
        //      （而且这就是正常行为：原生窗口里它本来就是收着的，只是能叫出来）。
        //      压成 0 高度时 `WS_VISIBLE` 还在，explorer 想显示它时改高度就行。
        //      光靠这一点还不够稳（explorer 未必自己改高度），所以配上 `WinEHook` 里的 Alt/F10 让行：
        //      他按一下，我们主动 `RestoreMenuBar` 还回去；菜单开着期间不收（`InMenuMode`）；
        //      菜单一关（Esc / 选完菜单项）下一次心跳就又收回去。
        //
        // ⚠ 只对我们自己那扇 cab 调（`ExplorerHost`）；shell 的窗口一个字节都不能碰。
        // ==================================================================
        private sealed class MenuBarRef
        {
            public IntPtr Container;     // ShellTabWindowClass
            public IntPtr Menu;          // 菜单栏的宿主 WorkerW
            public IntPtr Bar;           // 里面那条 ReBarWindow32
            public IntPtr View;          // DUIViewWndClassName（文件列表）
            /// <summary>
            /// 菜单栏「自然」高度。按 Alt 还给他时得知道还多少。
            /// ⚠ 不能只看「第一次看见它时它多高」：收编那一刻 explorer 常常还没把菜单栏立起来
            ///   （实测：第一次调进来时它已经是 0），那就永远量不到。所以优先向里面那条 ReBar
            ///   要高度 —— 它不受我们压高度的影响，记的就是 explorer 想要的那个尺寸。
            /// </summary>
            public int NaturalH;
        }

        /// <summary>cab → 它那三个窗口。收过一次就记着：窗口缩放的每一帧都要复核，不能每帧重找一遍。
        /// 句柄失效（窗口关掉 / 句柄被复用）就整条丢掉重找。</summary>
        private static readonly Dictionary<IntPtr, MenuBarRef> menuBarCache = new Dictionary<IntPtr, MenuBarRef>();

        /// <summary>
        /// 收起 <paramref name="cab"/> 里那条菜单栏，并把文件视图补满；**除非用户正要用它** ——
        /// 他按了 Alt / F10，或者菜单已经开着（见下面 wantMenu 那段）。
        /// 幂等：已经收着（原生那种 h=0 的状态）时只做两次进程内查询，不写任何东西。
        /// 返回 true = 这一趟真的动了手（收了 / 还回去了）。
        /// </summary>
        /// <summary>
        /// 菜单栏这块（压高度 + Alt/F10 让行）的总闸。**当前是关的**。
        ///
        /// 关掉的原因：内嵌窗口里「先冒一条白条、过一会儿自己消失」—— 我们这套是**事后**才压的
        /// （收编之后 + 250ms settle + 500ms 心跳），而改容器/子窗口尺寸会让 explorer 惰性重排、
        /// 先把菜单栏立起来，于是先看见那 20px、等下一趟心跳才被压掉。
        /// 停掉这块是为了做对照：白条到底是这段代码引起的，还是收编本身就有的。
        /// 开回来把这里改成 true 即可（`WinEHook` 里 Alt/F10 的递话一并随之失效，它只写让行时刻）。
        /// </summary>
        public static bool MenuBarWork = false;

        public static bool CollapseMenuBar(IntPtr cab)
        {
            if (!MenuBarWork) return false;
            if (cab == IntPtr.Zero) return false;
            try
            {
                MenuBarRef r;
                if (!menuBarCache.TryGetValue(cab, out r)
                    || !NativeMethods.IsWindow(r.Menu) || !NativeMethods.IsWindow(r.View)
                    || !NativeMethods.IsWindow(r.Container))
                {
                    r = FindMenuBar(cab);
                    if (r == null) return false;
                    if (menuBarCache.Count > 64) menuBarCache.Clear();
                    menuBarCache[cab] = r;
                }

                // ★ 他要用菜单栏时**必须把菜单栏还回去**，不能只「停手」：
                //   explorer 那边以为这条菜单栏一直是显示着的（我们只压了高度），光停手它自己不会
                //   把高度改回来 —— 表现就是「按了 Alt 什么都没有」。
                //   两个判据都要「我们的窗口在前台」，免得在别的程序里按 Alt 也把它放出来（Alt+Tab
                //   每天都在按，不能让菜单栏跟着乱冒）。
                bool fg = IsForegroundOurs(cab);
                if (fg && (MenuLetGo() || InMenuMode(cab))) return RestoreMenuBar(r);
                return CollapseMenuBarNow(r);
            }
            catch { return false; }
        }

        /// <summary>收：菜单栏高度压 0（**不隐藏**，见那段注释②），文件视图撑满容器。幂等。</summary>
        private static bool CollapseMenuBarNow(MenuBarRef r)
        {
            WRECT cr, mr;
            if (!GetWindowRect(r.Container, out cr)) return false;
            if (!GetWindowRect(r.Menu, out mr)) return false;

            bool acted = false;
            if (mr.Height > 0 && r.NaturalH <= 0) r.NaturalH = mr.Height;
            if (r.NaturalH <= 0) r.NaturalH = MenuBarH(r);      // 见 NaturalH 的注释：优先问里面那条 ReBar
            if (mr.Height > 0)
            {
                SetWindowPos(r.Menu, IntPtr.Zero, 0, 0, cr.Width, 0,
                    SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
                acted = true;
            }
            WRECT vr;
            if (GetWindowRect(r.View, out vr)
                && (vr.Top != cr.Top || vr.Height != cr.Height || vr.Width != cr.Width))
            {
                SetWindowPos(r.View, IntPtr.Zero, 0, 0, cr.Width, cr.Height,
                    SWP_NOZORDER | SWP_NOACTIVATE);
                acted = true;
            }
            return acted;
        }

        /// <summary>放：菜单栏回它自然的高度，文件视图让出那一条（用户按 Alt / 菜单开着时用）。幂等。</summary>
        private static bool RestoreMenuBar(MenuBarRef r)
        {
            WRECT cr, mr;
            if (!GetWindowRect(r.Container, out cr)) return false;
            if (!GetWindowRect(r.Menu, out mr)) return false;

            int h = r.NaturalH > 0 ? r.NaturalH : MenuBarFallbackH(r.Container);
            bool acted = false;
            if (mr.Height != h || mr.Width != cr.Width)
            {
                SetWindowPos(r.Menu, IntPtr.Zero, 0, 0, cr.Width, h,
                    SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
                acted = true;
            }
            WRECT vr;
            if (GetWindowRect(r.View, out vr)
                && (vr.Top != cr.Top + h || vr.Height != cr.Height - h || vr.Width != cr.Width))
            {
                SetWindowPos(r.View, IntPtr.Zero, 0, h, cr.Width, cr.Height - h,
                    SWP_NOZORDER | SWP_NOACTIVATE);
                acted = true;
            }
            return acted;
        }

        /// <summary>菜单栏的自然高度：优先问里面那条 ReBar（它不受我们压高度影响），问不到再量宿主。</summary>
        private static int MenuBarH(MenuBarRef r)
        {
            WRECT br;
            if (r.Bar != IntPtr.Zero && GetWindowRect(r.Bar, out br) && br.Height > 0) return br.Height;
            return 0;
        }

        // ==================================================================
        // 「把菜单栏还给他」：用户按 Alt / F10 时用（`WinEHook` 里递话）
        // ==================================================================

        /// <summary>
        /// 让行期长度。够他按完 Alt 之后菜单栏出现、并从从容容点开一个菜单；
        /// 菜单真开着时由 `InMenuMode` 接着管（菜单开着就一直不收）。
        /// 给得长一点不危险：一旦他把前台切走（Alt+Tab、点别的程序），
        /// `IsForegroundOurs` 立刻为假，下一次心跳就把菜单栏收回去。
        /// </summary>
        public const int MenuLetGoMs = 6000;

        /// <summary>让行截止时刻。钩子线程写、UI 线程读 —— 用 `int` 就是为了这个（读写天然原子）。</summary>
        private static int menuLetGoUntil;

        /// <summary>「他刚按了 Alt/F10」—— 只写一个字段，钩子回调里能安全调（那里不许做 IO）。</summary>
        public static void LetGoMenuBar(int ms) { menuLetGoUntil = Environment.TickCount + ms; }

        /// <summary>减法比较：`TickCount` 24.9 天翻一次，减法写法在翻越时仍然成立。</summary>
        private static bool MenuLetGo()
        {
            int t = menuLetGoUntil;
            return t != 0 && Environment.TickCount - t < 0;
        }

        // `GUI_INMENUMODE` / `GUI_POPUPMENUMODE` 是 `GUITHREADINFO.flags` 的位；
        // 那个结构体和 `GetGUIThreadInfo` 本文件上面已经有了（那块是给按键/焦点用的），直接接着用。
        private const int GUI_INMENUMODE = 0x00000004;
        private const int GUI_POPUPMENUMODE = 0x00000010;

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr h);

        /// <summary>从来没量到过自然高度时的兜底：菜单栏在 96 DPI 下是 20px。</summary>
        private static int MenuBarFallbackH(IntPtr anyWindow)
        {
            try
            {
                uint dpi = GetDpiForWindow(anyWindow);
                if (dpi > 0) return (int)Math.Round(20.0 * dpi / 96.0);
            }
            catch { }
            return 20;
        }

        /// <summary>
        /// explorer 那个线程是不是正开着菜单（`GUI_INMENUMODE`）。
        /// 他正在点菜单的时候我们绝不能把菜单栏抽走 —— 这一条比时间窗准：菜单开多久就管多久。
        /// </summary>
        private static bool InMenuMode(IntPtr cab)
        {
            try
            {
                uint tid = GetWindowThreadProcessId(cab, IntPtr.Zero);
                if (tid == 0) return false;
                GUITHREADINFO g = new GUITHREADINFO();
                g.cbSize = Marshal.SizeOf(typeof(GUITHREADINFO));
                if (!GetGUIThreadInfo(tid, ref g)) return false;
                return (g.flags & (GUI_INMENUMODE | GUI_POPUPMENUMODE)) != 0;
            }
            catch { return false; }
        }

        /// <summary>`h` 所在的那扇**顶层**窗口是不是现在的前台窗口。</summary>
        private static bool IsForegroundOurs(IntPtr h)
        {
            try
            {
                IntPtr fg = NativeMethods.GetForegroundWindow();
                if (fg == IntPtr.Zero) return false;
                IntPtr t = h, p = GetParent(t);
                int guard = 0;
                while (p != IntPtr.Zero && guard++ < 32) { t = p; p = GetParent(t); }
                return t == fg;
            }
            catch { return false; }
        }

        /// <summary>
        /// 按结构找那三个窗口：`ShellTabWindowClass` 底下「子窗口是 ReBarWindow32 的那个 WorkerW」
        /// 就是菜单栏 —— 用**结构**判，不看类名以外的任何东西（跟系统语言无关，也不看高度阈值）。
        /// </summary>
        private static MenuBarRef FindMenuBar(IntPtr cab)
        {
            IntPtr st = IntPtr.Zero;
            IntPtr c = GetWindow(cab, GW_CHILD);
            int guard = 0;
            while (c != IntPtr.Zero && guard++ < 32)
            {
                if (string.Compare(ClassOf(c), "ShellTabWindowClass", StringComparison.OrdinalIgnoreCase) == 0)
                {
                    st = c;
                    break;
                }
                c = GetWindow(c, GW_HWNDNEXT);
            }
            if (st == IntPtr.Zero) return null;

            IntPtr menu = IntPtr.Zero, view = IntPtr.Zero, bar = IntPtr.Zero;
            c = GetWindow(st, GW_CHILD);
            guard = 0;
            while (c != IntPtr.Zero && guard++ < 32)
            {
                string cn = ClassOf(c);
                if (string.Compare(cn, "DUIViewWndClassName", StringComparison.OrdinalIgnoreCase) == 0)
                    view = c;
                else if (string.Compare(cn, "WorkerW", StringComparison.OrdinalIgnoreCase) == 0)
                {
                    IntPtr b = WinFind.ByClass(c, "ReBarWindow32");
                    if (b != IntPtr.Zero) { menu = c; bar = b; }
                }
                c = GetWindow(c, GW_HWNDNEXT);
            }
            if (menu == IntPtr.Zero || view == IntPtr.Zero) return null;
            return new MenuBarRef { Container = st, Menu = menu, Bar = bar, View = view };
        }

        // ==================================================================
        // 无边框窗口的最大化范围
        // ==================================================================
        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO info);

        private const uint MONITOR_DEFAULTTONEAREST = 2;

        /// <summary>
        /// 无边框窗口最大化会**盖住任务栏**（没有非客户区，系统就按整屏算）。
        /// 在 WM_GETMINMAXINFO 里把「最大化尺寸/位置」改成窗口所在显示器的**工作区**。
        ///
        /// 不用 Form.MaximizedBounds：那个只能钉死一个矩形（写多显示器就错），
        /// 而且窗口被拖到第二块屏上之后它不会跟着变。这里每次最大化都按当前显示器算。
        /// 位置是相对显示器左上角的偏移（不是屏幕坐标）。
        /// </summary>
        public static void ClampMaxToMonitorWork(IntPtr hwnd, IntPtr minmax)
        {
            if (hwnd == IntPtr.Zero || minmax == IntPtr.Zero) return;
            try
            {
                IntPtr mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                if (mon == IntPtr.Zero) return;
                MONITORINFO mi = new MONITORINFO();
                mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
                if (!GetMonitorInfo(mon, ref mi)) return;

                MINMAXINFO mm = (MINMAXINFO)Marshal.PtrToStructure(minmax, typeof(MINMAXINFO));
                mm.ptMaxPosition.X = mi.rcWork.Left - mi.rcMonitor.Left;
                mm.ptMaxPosition.Y = mi.rcWork.Top - mi.rcMonitor.Top;
                mm.ptMaxSize.X = mi.rcWork.Right - mi.rcWork.Left;
                mm.ptMaxSize.Y = mi.rcWork.Bottom - mi.rcWork.Top;
                Marshal.StructureToPtr(mm, minmax, false);
            }
            catch { }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GUITHREADINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public int left, top, right, bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WRECT
    {
        public int Left, Top, Right, Bottom;
        public int Width { get { return Right - Left; } }
        public int Height { get { return Bottom - Top; } }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WPOINT
    {
        public int X, Y;
    }

    /// <summary>WM_GETMINMAXINFO 的 lParam。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MINMAXINFO
    {
        public WPOINT ptReserved;
        public WPOINT ptMaxSize;      // 最大化时的尺寸
        public WPOINT ptMaxPosition;  // 最大化时的位置（相对显示器左上角）
        public WPOINT ptMinTrackSize;
        public WPOINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MONITORINFO
    {
        public int cbSize;
        public WRECT rcMonitor;   // 整块屏
        public WRECT rcWork;      // 去掉任务栏的可用区
        public int dwFlags;
    }
}
