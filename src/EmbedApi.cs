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

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindow(IntPtr h, uint cmd);

        /// <summary>GW_OWNER —— 取属主窗口。</summary>
        public const uint GW_OWNER = 4;

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
        /// ⚠ 收编进标签之前 / 放它走之前**必须** `ClearTransparent`，
        ///   否则嵌进来的窗口会永远是隐形的（这个坑比闪一下严重得多）。
        /// </summary>
        public static void MakeTransparent(IntPtr h)
        {
            try
            {
                uint ex = GetExStyle(h);
                if ((ex & WS_EX_LAYERED) != 0) return;      // 已经是分层的，别重复设
                SetExStyle(h, ex | WS_EX_LAYERED);
                SetLayeredWindowAttributes(h, 0, 0, LWA_ALPHA);
            }
            catch { }
        }

        /// <summary>把 <see cref="MakeTransparent"/> 加的那层去掉（本来就带 WS_EX_LAYERED 的窗口不动）。</summary>
        public static void ClearTransparent(IntPtr h)
        {
            try
            {
                uint ex = GetExStyle(h);
                if ((ex & WS_EX_LAYERED) == 0) return;
                SetExStyle(h, ex & ~(uint)WS_EX_LAYERED);
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

        public static bool IsClaimed(IntPtr h)
        {
            lock (claims) { return claims.Contains(h); }
        }

        public static void Claim(IntPtr h)
        {
            lock (claims) { if (h != IntPtr.Zero) claims.Add(h); }
        }

        public static void Release(IntPtr h)
        {
            lock (claims) { if (h != IntPtr.Zero) claims.Remove(h); }
        }

        /// <summary>
        /// 扫顶层窗口，找出「刚冒出来的」文件夹窗口 —— 也就是**我们刚叫起来的那个**。
        /// 三条刻意的设计：
        ///   · **不看可见性**：我们可能已经把它藏起来了（怕它闪），藏了也得认得出来；
        ///   · **不枚举进程列表**：这个函数 25ms 就要跑一次，`GetProcessesByName` 太贵，
        ///     而窗口只能由进程建出来，直接扫窗口就够了（有窗口 ⇒ 进程必然存在）；
        ///   · 排掉两种「本来就在那儿的东西」：**启动基线**上的窗口（他早就开着的）
        ///     和**已被认领**的窗口（我们自己另一个标签已经收走的）。
        ///     `pidsBefore` 参数留着不用了 —— 见上面基线那段，按进程判会串台；
        ///     `relax` 的含义现在只是「不做地址校验」（见 ExplorerHost.OnPoll）。
        /// </summary>
        public static IntPtr FindNewCab(HashSet<IntPtr> cabsBefore, HashSet<int> pidsBefore,
            bool relax, out int pidOfFound)
        {
            IntPtr found = IntPtr.Zero;
            int foundPid = 0;
            EnumWindowsProc cb = null;
            cb = delegate(IntPtr h, IntPtr l)
            {
                string c = ClassOf(h);
                if (c != "CabinetWClass" && c != "ExploreWClass") return true;
                if (cabsBefore.Contains(h)) return true;
                if (IsBaseline(h)) return true;
                if (IsClaimed(h)) return true;
                // shell 进程自己的窗口绝不能被当成「我们要嵌的那个」——理由见 IsShellOwned。
                // 代价是这种窗口撑不起标签（那一条会超时失败，桌面上留一个原生窗口），
                // 比把桌面外壳弄坏强得多。
                if (IsShellOwned(h)) return true;
                int p = ProcessIdOf(h).ToInt32();
                if (p == 0) return true;
                found = h; foundPid = p;
                return false;
            };
            EnumWindows(cb, IntPtr.Zero);
            GC.KeepAlive(cb);
            pidOfFound = foundPid;
            return found;
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
        /// 读不到 / 读出来不是真目录（「此电脑」「主文件夹」这类虚拟位置）一律返回 null，
        /// 调用方据此走「放手」那条路 —— 绝不能因为读不出来就把窗口晾成隐形的。
        /// </summary>
        public static string AddressPathOf(IntPtr cab)
        {
            try
            {
                IntPtr band = FindAddressBand(cab);
                if (band == IntPtr.Zero) return null;
                string raw = WindowTextOf(band);
                // 「地址: <当前地址>」——前缀跟着系统语言变，所以只认第一个冒号加空格
                int i = raw.IndexOf(": ", StringComparison.Ordinal);
                if (i < 0) return null;
                string p = raw.Substring(i + 2).Trim();
                if (p.Length == 0) return null;
                return System.IO.Directory.Exists(p) ? p : null;
            }
            catch { return null; }
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
