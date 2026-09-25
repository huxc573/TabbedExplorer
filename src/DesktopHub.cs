using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace TabbedExplorer
{
    /// <summary>
    /// 常驻后台的「总机」：托盘图标 + Win+E 钩子 + **每张虚拟桌面一个窗口**。
    ///
    /// 为什么要从 EmbedForm 里把「托盘 + 钩子」拆出来（）：
    /// 原来是谁先建窗口谁就兼任常驻，于是全进程只有**一个**窗口 ——
    /// 三张虚拟桌面共用一套标签，Win+E 每次都把那个窗口搬到当前桌面来。
    /// 用户要的是「每个虚拟桌面单独捕获合并并记忆那个桌面关闭程序窗口时的标签页」，
    /// 那前提就是**每张桌面各有一个窗口**；而托盘图标和低级键盘钩子全进程只能有一份，
    /// 它们必须住在窗口之外 —— 就是这个 DesktopHub。
    ///
    /// 顺带消掉一整类老毛病：窗口不再被搬来搬去，
    /// 「按 Win+E 反而把人拽到另一张虚拟桌面」从结构上就不可能发生了
    /// （窗口在哪张桌面，就是给哪张桌面用的；要别的桌面就新建一个）。
    ///
    /// 关窗口（X / Ctrl+W 关到最后一个标签）= 收进托盘，进程不退；
    /// 真退出只有托盘菜单「退出」和命令行 `--quit`（两者都会先把各桌面的标签写进记忆）。
    /// </summary>
    internal sealed class DesktopHub : ApplicationContext
    {
        /// <summary>按虚拟桌面 GUID 登记窗口（Guid.Empty = 问不出桌面时的兜底桶）。</summary>
        private readonly Dictionary<string, EmbedForm> forms =
            new Dictionary<string, EmbedForm>(StringComparer.OrdinalIgnoreCase);

        private readonly DesktopMemory memory = new DesktopMemory();

        /// <summary>托盘右键菜单里那棵「设置」子树（设置变了只刷文字，不重建菜单）。</summary>
        private SettingsMenu.TraySettings traySettings;
        private ContextMenu trayMenu;

        /// <summary>迁移模式（v1.0.0）下，全进程唯一那个窗口在登记表里的键。</summary>
        private const string SingleKey = "single";

        /// <summary>钩子回调在别的线程上，动界面之前得先转回 UI 线程 —— 就是它。</summary>
        private readonly Control syncTarget = new Control();

        /// <summary>记忆不急着每改一下就写盘，攒一下再写（导航一次会连改好几项）。</summary>
        private readonly System.Windows.Forms.Timer saveTimer = new System.Windows.Forms.Timer();

        private NotifyIcon tray;
        private WinEHook hook;
        /// <summary>
        /// 全局滚轮钩子（按钮区横滚标签条 / 内容区 Shift+滚轮）。
        ///
        /// ⚠ 用户报「按钮和内容区以及 Shift 滚轮都没生效」—— 根因就是这个类**全项目
        /// 谁都没 new 过**：`WheelRouter` 的登记表、`TabStrip` 的判定、`EmbedForm` 的回调
        /// 全都写好了，就是没把它装起来，所以失效得很彻底（三个入口一起不动）。
        /// 现在在 Hub 里装一次，全进程共用（`EmbedForm` 只管往 `WheelRouter` 登记）。
        /// </summary>
        private MouseWheelHook wheelHook;
        /// <summary>「谁被显示出来了」的系统广播 —— 用来抓用户自己打开的文件夹窗口（Bug 1）。</summary>
        private WinShowWatcher captureWatch;
        /// <summary>看见了、但还没到点去收的候选窗口（值 = 第一次看见的时刻 + 用来写日志的事件号）。见 TryCapture。</summary>
        private readonly Dictionary<IntPtr, CaptureCandidate> pendingCapture =
            new Dictionary<IntPtr, CaptureCandidate>();
        /// <summary>
        /// 「我们主动藏起来、还没收编的」窗口 —— 防闪用（用户：从桌面/开始菜单打开的会闪一下）。
        /// ⚠：藏这件事挪到了 watcher 自己的线程上做（见 OnWindowShown），
        /// 所以这个集合现在是**两个线程都会碰**的 —— 一律走 HiddenByUs / MarkHidden / UnmarkHidden 这三个口，别直接动。
        /// </summary>
        private readonly HashSet<IntPtr> hiddenByUs = new HashSet<IntPtr>();
        private readonly object hideLock = new object();
        /// <summary>
        /// 「shell 进程开的、等它地址栏填好就转生」的窗口（只在开了接管 shell 时才用）。
        /// 它们不能收编（见 EmbedApi.IsShellOwned），所以要等地址栏出现真路径，再关掉重开。
        /// </summary>
        private readonly Dictionary<IntPtr, ShellCandidate> pendingShell =
            new Dictionary<IntPtr, ShellCandidate>();
        /// <summary>本进程 pid（`IsCapturable` 在 watcher 线程上也要用，别每次现问）。</summary>
        private readonly int ourPid = Process.GetCurrentProcess().Id;
        private System.Windows.Forms.Timer captureTimer;
        /// <summary>候选窗口要「晾」多久才收。够短，用户感觉不出来；够长，让标签先把自己起的窗口认领掉。</summary>
        private const int CaptureDelayMs = 700;
        /// <summary>
        /// shell 窗口「等地址栏填好」的上限。shell 通常是**先导航、再显示**，所以 SHOW 那一刻地址栏
        /// 往往已经填好了（RegisterShellCandidate 里会当场试一次）。现在从 CREATE 就开始等
        /// （那 ~0.7 秒是白捡的），超时按**最近一次事件**重新起算，免得早那一次先把点耗光。
        /// 到点还读不出就当没这回事 —— 我们本来就没碰过它，它还是那个正常窗口，用户自己关。
        /// </summary>
        private const int ShellResolveMs = 1200;
        private RegisteredWaitHandle sigWait;
        private RegisteredWaitHandle quitWait;
        private EventWaitHandle quitEvent;
        private bool quitting;
        private bool trayTipShown;
        private bool disposed;

        public DesktopHub()
        {
            // 设置已经在 Program.Main 里 Load 过了（颜色模式得赶在 Theme 之前定下来）。
            memory.Load();

            saveTimer.Interval = 800;
            saveTimer.Tick += delegate { saveTimer.Stop(); SaveNow("攒够了一下"); };

            IntPtr h = syncTarget.Handle;   // 逼出句柄，之后才收得到 BeginInvoke
            GC.KeepAlive(h);

            SetupTray();
            SetupHook();
            SetupWheel();
            SetupCapture();

            try { VirtualDesktop.WarmUp(); } catch (Exception ex) { Diag.Log("虚拟桌面: 预热异常 " + ex.Message); }

            if (Program.AutoOpen)
            {
                Diag.Step("Hub: --open -> 立刻在当前桌面开一个窗口");
                OnWinE();
            }
        }

        // ==================================================================
        // 托盘 + 钩子 + 两个命名事件
        // ==================================================================

        private void SetupTray()
        {
            // 托盘图标 = 程序自己的图标（exe 里编进去的那颗），不是 shell32 借来的。
            tray = new NotifyIcon();
            tray.Icon = ShellIcon.AppIcon(true);
            tray.Text = "TabbedExplorer（接 Win+E）";
            tray.Visible = true;

            MenuItem miShow = new MenuItem("打开窗口（Win+E）", delegate { OnWinE(); });
            // 用户：托盘里那条「记住当前标签」去掉，换成两个管理器 ——
            // 手动存标签本来就用不上（改动攒 800ms 自己落盘 + 退出前再存一次，见 RememberNow），
            // 而两个管理器以前只在窗口内够得着（书签栏左端 / 标签条右键），放托盘里更好摸。
            // ⚠ 设置窗口里那条「立即记住当前标签」**保留**（用户明确说的：只删托盘这条）。
            MenuItem miFav = new MenuItem("书签管理器", delegate { OpenFavManager(); });
            MenuItem miHist = new MenuItem("历史记录管理器", delegate { OpenHistoryManager(); });
            MenuItem miQuit = new MenuItem("退出", delegate { Quit("托盘菜单"); });

            // 设置子菜单跟齿轮那份**同一份内容**（SettingsMenu 里生成），别各写一遍 ——
            // 用户报的「托盘右键没有设置选项」就是两边各写一遍漏出来的。
            traySettings = SettingsMenu.BuildTraySettings(this);

            trayMenu = new ContextMenu(new MenuItem[]
            {
                miShow, miFav, miHist, traySettings.Root, new MenuItem("-"), miQuit
            });
            // 自绘：勾选列独立（跟同级项左对齐）+ 深色下也看得见勾（用户报的「没和其它选项一样居左对齐」）
            MenuFx.Hook(trayMenu);
            tray.ContextMenu = trayMenu;
            tray.DoubleClick += delegate { OnWinE(); };
        }

        /// <summary>设置变了：把托盘菜单里带勾选前缀的文字重刷一遍（菜单结构不动）。</summary>
        private void RefreshTrayMenu()
        {
            try { if (traySettings != null) traySettings.Refresh(); }
            catch (Exception ex) { Diag.Log("Hub: 刷托盘菜单失败 " + ex.Message); }
        }

        private void SetupHook()
        {
            hook = new WinEHook();
            hook.WinE += delegate { Post(OnWinE); };
            // 7 条可自定义命令合成一个事件（绑定在 settings.json 的 hotkey_*，见 Hotkeys）。
            // 这里把「命令标识」翻成 EmbedForm 认识的那个词 —— 两边都用标识，不再用组合键文本。
            hook.Command += delegate(string cmd) { Post(delegate { Hotkey(cmd); }); };
            // Ctrl+1..9 = 跳到第 N 个标签（参数是 0 基；这一条不参与自定义）
            hook.GotoTabKey += delegate(int i)
            {
                int n = i;
                Post(delegate { Hotkey("goto:" + n); });
            };
            hook.Start();

            // 第二个实例被启动（双击 exe）时只会 set 一下这个事件，由我们现身。            // 事件挂在字段上、不能 using 掉 —— 注册等待之后句柄要一直活着。
            if (Program.ShowSignal != null)
            {
                try
                {
                    sigWait = ThreadPool.RegisterWaitForSingleObject(Program.ShowSignal,
                        delegate { Post(OnWinE); }, null, -1, false);
                }
                catch (Exception ex) { Diag.Log("Hub: 注册唤醒事件失败 " + ex.Message); }
            }

            // `--quit`：命令行版「退出」，真退（先存记忆、再把各标签里的 explorer 收干净）。
            try
            {
                quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.QuitSignalName);
                quitWait = ThreadPool.RegisterWaitForSingleObject(quitEvent,
                    delegate { Post(delegate { Quit("--quit"); }); }, null, -1, false);
            }
            catch (Exception ex) { Diag.Log("Hub: 注册退出事件失败 " + ex.Message); }
        }

        /// <summary>
        /// 装全局滚轮钩子。
        ///
        /// 为什么得用低级钩子而不是控件的 `OnMouseWheel`：**内容区是跨进程嵌进来的真 explorer 窗口**，
        /// 滚轮消息直接投给它（鼠标在谁身上就归谁），我们的窗体根本收不到。
        /// `WH_MOUSE_LL` 是唯一能在消息派发**之前**看到滚轮、并且决定吞不吞的地方。
        /// 钩子自己跑在一个独立线程上（不占 UI 线程，也不写日志 —— 超时会被系统默默摘掉）。
        /// </summary>
        private void SetupWheel()
        {
            try
            {
                wheelHook = new MouseWheelHook();
                wheelHook.Start();
            }
            catch (Exception ex) { Diag.Log("Hub: 装滚轮钩子失败 " + ex.Message); }
        }

        // ==================================================================
        // 设置窗口（全进程只开一个）
        //
        // 原来窗口是 EmbedForm 自己 new + Show 的，于是「齿轮开的」和「托盘菜单『更多选项』开的」
        // 会变成两个窗口。现在收到 Hub 这一处：谁想开都调 OpenSettings()。
        // ==================================================================

        private SettingsForm settingsForm;
        private FavManagerForm favManager;
        private HistoryManagerForm historyManager;

        /// <summary>打开（或提到前面）设置窗口。齿轮、标签条空白右键、托盘菜单「更多选项」都走这儿。</summary>
        public void OpenSettings()
        {
            try
            {
                if (settingsForm != null && !settingsForm.IsDisposed)
                {
                    Diag.Step("Hub: 设置窗口已经开着 -> 提到前面");
                    if (settingsForm.WindowState == FormWindowState.Minimized)
                        settingsForm.WindowState = FormWindowState.Normal;
                    settingsForm.Activate();
                    return;
                }
                Diag.Step("Hub: 打开设置窗口");
                settingsForm = new SettingsForm(this);
                settingsForm.FormClosed += delegate { settingsForm = null; };
                // 有前台窗口就认它当 owner（居中到它上面、也跟着它一起最小化）；
                // 没有（比如从托盘菜单点进来）就独立显示。
                EmbedForm owner = ForegroundForm();
                if (owner != null && !owner.IsDisposed) settingsForm.Show(owner);
                else settingsForm.Show();
            }
            catch (Exception ex) { Diag.Log("Hub: 打开设置窗口失败 " + ex); }
        }

        /// <summary>
        /// 打开（或提到前面）**书签管理器**（用户要的新窗口：仿浏览器那个书签管理器）。
        /// 跟设置窗口一样，全进程只开一个 —— 书签栏左端的星标、栏上右键、菜单里都走这一个门。
        /// </summary>
        public void OpenFavManager()
        {
            try
            {
                if (favManager != null && !favManager.IsDisposed)
                {
                    Diag.Step("Hub: 书签管理器已经开着 -> 提到前面");
                    if (favManager.WindowState == FormWindowState.Minimized)
                        favManager.WindowState = FormWindowState.Normal;
                    favManager.Activate();
                    return;
                }
                Diag.Step("Hub: 打开书签管理器");
                favManager = new FavManagerForm(
                    delegate(bool on) { SetFavBar(on); },
                    delegate { return Settings.FavBar; });
                // 管理器里双击一个文件夹 = 在当前窗口开一个新标签
                favManager.OpenPath += delegate(string p)
                {
                    EmbedForm f = ForegroundForm();
                    if (f != null && !f.IsDisposed) f.OpenPathAsTab(p);
                };
                favManager.FormClosed += delegate { favManager = null; };
                EmbedForm owner = ForegroundForm();
                if (owner != null && !owner.IsDisposed) favManager.Show(owner);
                else favManager.Show();
            }
            catch (Exception ex) { Diag.Log("Hub: 打开书签管理器失败 " + ex); }
        }

        /// <summary>
        /// 打开（或提到前面）**历史记录管理器**（用户：「打开历史记录文件」改成它，
        /// 界面模仿书签管理器）。跟设置窗口 / 书签管理器一样，全进程只开一个。
        /// </summary>
        public void OpenHistoryManager()
        {
            try
            {
                if (historyManager != null && !historyManager.IsDisposed)
                {
                    Diag.Step("Hub: 历史记录管理器已经开着 -> 提到前面");
                    if (historyManager.WindowState == FormWindowState.Minimized)
                        historyManager.WindowState = FormWindowState.Normal;
                    historyManager.Activate();
                    return;
                }
                Diag.Step("Hub: 打开历史记录管理器");
                historyManager = new HistoryManagerForm();
                // 管理器里双击一条 = 在当前窗口开一个新标签
                historyManager.OpenPath += delegate(string p)
                {
                    EmbedForm f = ForegroundForm();
                    if (f != null && !f.IsDisposed) f.OpenPathAsTab(p);
                };
                historyManager.FormClosed += delegate { historyManager = null; };
                EmbedForm owner = ForegroundForm();
                if (owner != null && !owner.IsDisposed) historyManager.Show(owner);
                else historyManager.Show();
            }
            catch (Exception ex) { Diag.Log("Hub: 打开历史记录管理器失败 " + ex); }
        }

        // ==================================================================
        // 捕获「所有」打开的文件夹（Bug 1）
        //
        // 用户的原话：「只捕获了 Win+E 这个按键，而不是所有资源管理器打开的文件夹
        // （比如从开始菜单、从桌面打开的）」。也就是**只要是资源管理器打开了一个文件夹，就该变成
        // 我们窗口里的一个标签** —— 跟浏览器一样，新窗口都归到标签里去。
        //
        // 做法：听系统的「某某窗口刚被显示」广播（跟防闪用的是同一个 WinShowWatcher），
        // 认出**新出现的** CabinetWClass 顶层窗口 → 交给当前桌面那个窗口收成标签
        // （`ExplorerHost.Adopt`：不起新进程，直接把现成的窗口收进来，所以不会闪也没延迟）。
        // ==================================================================

        private void SetupCapture()
        {
            SweepStrayExplorers();                // 先把上次没退干净留下的 explorer 进程清掉（见方法注释）
            EmbedApi.SnapshotBaseline();          // 再记下「现在就已经开着的」，这些不算新开

            // 候选窗口先搁一下再收 —— 见 TryCapture 里那段说明。
            captureTimer = new System.Windows.Forms.Timer();
            captureTimer.Interval = 250;
            captureTimer.Tick += delegate { DrainCapture(); };

            captureWatch = new WinShowWatcher();
            captureWatch.WindowShown += OnWindowShown;
            // ★ 装在自己的线程上（不是 UI 线程）—— 用户报的「外部打开的文件夹还是闪一下」就卡在这里，
            //   详见 WinShowWatcher 类注释里那段「为什么 Hub 那条非要另起线程」。
            captureWatch.StartDedicated("TabbedExplorer.CaptureWatch");
            Diag.Step("Hub: 已开始监听「新打开的文件夹窗口」（专用线程，延迟 " + CaptureDelayMs + "ms 接收）");
        }

        /// <summary>
        /// 清一次「上次没退干净」留下的 explorer 进程。
        ///
        /// 正常退出时每个标签都会在 `ExplorerHost.Close` 里收掉自己的进程，只有**闪退**
        /// （未处理异常 / 被强杀）来不及收。留下的那些进程「活着但一个窗口都没有」，
        /// 会让 shell 的「打开资源管理器」（Win+E、开始菜单里那条）去**激活一个不存在的窗口**
        /// —— 表现就是按下去什么都没发生（开始菜单里点别的文件夹是好的，那走的是另一条路）。
        ///
        /// 判据只认「有进程、但既不是桌面 shell、也没有任何浏览窗口」：
        ///   · 有 `Shell_TrayWnd` / `Progman` ⇒ 桌面 shell 本体，不碰；
        ///   · 有 `CabinetWClass` ⇒ 用户或别的程序正在用，不碰；
        ///   · 刚起来不到几秒的也放过 —— 别和「正在启动、窗口还没建出来」的抢时间。
        /// Win10 的虚拟桌面共用同一个窗口站，别的虚拟桌面上的窗口照样数得到，所以不会误杀。
        /// </summary>
        private void SweepStrayExplorers()
        {
            int n = 0;
            foreach (Process p in Process.GetProcessesByName("explorer"))
            {
                try
                {
                    if ((DateTime.Now - p.StartTime).TotalSeconds < 5) continue;
                    if (EmbedApi.ExplorerHasOtherWindows(p.Id, IntPtr.Zero)) continue;
                    Diag.Step("Hub: 清掉没有窗口的 explorer pid=" + p.Id);
                    p.Kill();
                    n++;
                }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }
            if (n > 0) Diag.Step("Hub: 本次启动清掉 " + n + " 个残留 explorer 进程");
        }

        /// <summary>候选窗口：看见的时刻 + 收到事件时它已经可见了多久（诊断用，见 OnWindowShown）。</summary>
        private sealed class CaptureCandidate
        {
            public DateTime SeenAt;
            public int ReactMs;
        }

        /// <summary>shell 窗口转生候选：等它的地址栏出现真路径（见 ShellResolveMs）。</summary>
        private sealed class ShellCandidate
        {
            public DateTime SeenAt;
            public int ReactMs;
        }

        /// <summary>
        /// ★★ 看见一个新窗口 —— **在 watcher 自己的线程上同步藏**，不绕 UI 线程。
        ///
        /// 第三轮返工（用户第三次报「从桌面/开始菜单开文件夹还闪」）。
        /// 前两轮做对了两件事：① 藏这件事挪到 watcher 自己的线程上（不等 UI 线程）；② 一起听 CREATE。
        /// **但两件都白做了**，日志摊开一看就明白：
        ///   每条都是「事件后 0ms，**当时已可见**」——
        ///   · 0ms 说明我们反应已经最快（事件一到就动手）；
        ///   · 「已可见」说明**系统报给我们的时候，那扇窗早画在屏幕上了**。
        ///   WinEvent 终究是往消息队列投递的，快不过 explorer 自己的 ShowWindow。
        ///
        /// 两个根因：
        ///   ① 播 CREATE 的那条路**被判定拦掉了**：藏这件事原来复用 `IsCapturable`，
        ///      而它有一句「不可见又没被我们藏过 → 不算数」—— CREATE 时窗口本来就没可见，
        ///      于是永远被判「不用管」，只能等 SHOW，而 SHOW 已经晚了。
        ///      ⇒ 现在藏用 `IsHideCandidate`（不要求可见），收仍用 `IsCapturable`（要求可见）。
        ///   ② 光 `SW_HIDE` 本来就是挡不住的（见 ①）。所以再加一层兜底：`MakeTransparent`
        ///      （WS_EX_LAYERED + alpha=0）—— 就算 explorer 随后的 ShowWindow 把它显出来，
        ///      那一帧也是**全透明**的，肉眼什么都看不到。
        ///
        /// ⚠ 收编 / 放手时**必须** `ClearTransparent`（`AdoptWindow` / `ReleaseIfAbandoned` 里做了），
        ///   忘了就是「嵌进来的窗口永远是隐形的」—— 比闪一下严重得多。
        /// </summary>
        private void OnWindowShown(IntPtr h, int tick, uint evt)
        {
            try
            {
                if (quitting || h == IntPtr.Zero) return;
                if (!Settings.CaptureAll) return;
                // 桌面 shell 进程自己开的窗口：默认（v1.13.1 起）一个都不碰；
                // 只有开了「接管 shell 打开的文件夹」才走另一条路 —— **不 SetParent、也不改它的样式**，
                // 只是读出它要去哪个目录、像用户点 × 一样关掉它，再用我们自己的 explorer 重开成标签
                // （见 TakeOverShellWindow）。
                bool shellTake = Settings.CaptureShell && IsShellTakeoverCandidate(h);
                if (!shellTake && !IsHideCandidate(h))
                {
                    // 「看着像用户新开的文件夹窗口、却被整个放过」原来是全黑的 —— 川报「点打开文件夹没反应」
                    // 时根本分不清是没收还是没收到。只记 shell 自己那种浏览窗口：我们自己的窗口、
                    // 启动前就在的老窗口都会被 `IsHideCandidate` 就地挡掉，记它们纯噪声。
                    string cls0 = EmbedApi.ClassOf(h);
                    if (!shellTake && (cls0 == "CabinetWClass" || cls0 == "ExploreWClass") && EmbedApi.IsShellOwned(h))
                        Diag.Step(string.Format(
                            "Hub: 放过 shell 的浏览窗口 cab=0x{0:X}（接管开关={1} 基线={2} 已认领={3} 已嵌={4}）",
                            h.ToInt64(), Settings.CaptureShell ? "开" : "关",
                            EmbedApi.IsBaseline(h) ? "是" : "否",
                            EmbedApi.IsClaimed(h) ? "是" : "否",
                            EmbedApi.GetParent(h) != IntPtr.Zero ? "是" : "否"));
                    return;
                }

                int react = Environment.TickCount - tick;      // 系统报事件 → 我们动手，差了多少毫秒
                bool wasVisible = EmbedApi.IsWindowVisible(h);
                bool isShow = (evt == WinShowWatcher.EVENT_OBJECT_SHOW);

                if (shellTake)
                {
                    // ★ 实测教训（2026-09-24，川报「退出程序后 Win+E / 开始菜单打不开资源管理器」）：
                    //   对 shell 的窗口**一个字节都不能改** —— 不置透明、不 SW_HIDE。
                    //   shell 会预建一些浏览器窗口备着而根本不显示（探针实测到 `vis=0` 的 CabinetWClass），
                    //   而防闪那层透明是在 CREATE 那一刻就上的；那个窗口永远不会 SHOW ⇒ 永远走不到「撕透明」，
                    //   等于在 shell 进程里留了一个**隐形窗口**。Win+E 与开始菜单那条入口只去「激活」
                    //   它自己的窗口、不新建 ⇒ 激活到一个隐形的就是「按下去毫无反应」，而且透明是加在
                    //   shell 的窗口上的，退程序也不恢复。
                    //   ⇒ shell 这条路上我们只读、只关，不碰样式；代价是那扇窗会可见一小会儿（见 TakeOverShellWindow）。
                    Diag.Step(string.Format(
                        "Hub: 新窗口 -> shell 自己的（不碰样式）（{0}，事件后 {1}ms）cab=0x{2:X}",
                        isShow ? "SHOW" : "CREATE", react, h.ToInt64()));
                }
                else
                {
                    // 兜底层先上（不管它现在可不可见），再补一刀 SW_HIDE
                    EmbedApi.MakeTransparent(h);
                    bool hid = EmbedApi.ShowWindow(h, EmbedApi.SW_HIDE);
                    if (isShow) MarkHidden(h);      // 只有「真被显示过」的才当作候选去收

                    Diag.Step(string.Format(
                        "Hub: 新窗口 -> {0}（{1}，事件后 {2}ms，当时{3}）cab=0x{4:X}",
                        hid ? "藏起来" : "SW_HIDE 没生效但已置为透明",
                        isShow ? "SHOW" : "CREATE", react,
                        wasVisible ? "已可见" : "还没画出来", h.ToInt64()));
                }

                // 后面的账（pendingCapture / 起定时器）回 UI 线程做 —— 那两个是 UI 线程的状态
                // ⚠ shell 那条**从 CREATE 就开始登记**：从 CREATE 到 SHOW 实测隔着 ~0.7 秒，那段时间窗口
                //   还没画出来，越早读到地址栏就越可能在它露脸之前把 SC_CLOSE 发出去
                //   （用户报的「多闪一下原生资源管理器」就是非等 SHOW 不可造成的）。
                //   收编那条仍然只听 SHOW —— 那扇窗在 CREATE 就已经被我们置透明了，不急。
                if (isShow || shellTake)
                    Post(delegate { if (shellTake) RegisterShellCandidate(h, react); else RegisterCandidate(h, react); });
            }
            catch (Exception ex) { Diag.Log("Hub: 处理新窗口事件失败 " + ex.Message); }
        }

        /// <summary>
        /// 「值不值得先藏一下」的粗筛 —— 跟 `IsCapturable` **故意不一样：不要求窗口已经可见**。
        /// 理由见 OnWindowShown 的类注释①：CREATE 那一刻窗口还没显示，
        /// 加了可见性要求就等于把唯一的早机会扔掉了。只碰锁保护的集合，两个线程都能调。
        /// </summary>
        private bool IsHideCandidate(IntPtr h)
        {
            string c = EmbedApi.ClassOf(h);
            if (c != "CabinetWClass" && c != "ExploreWClass") return false;
            if (EmbedApi.IsClaimed(h)) return false;          // 我们自己起的 / 已经被某个标签收了
            if (EmbedApi.IsBaseline(h)) return false;         // 启动前就在那儿的老窗口（还原、重新显示不算新开）
            if (EmbedApi.GetParent(h) != IntPtr.Zero) return false;   // 已经是子窗口 → 被谁嵌走了
            // 有属主的（对话框 / 弹出窗）不是文件夹框。这一条也是**防漏**：没这一刀的话，
            // 「创建了但从没显示」的隐藏壳子会被我们永久置成透明（跟孤儿一样赖着）。
            if (EmbedApi.GetWindow(h, EmbedApi.GW_OWNER) != IntPtr.Zero) return false;
            int pid = EmbedApi.ProcessIdOf(h).ToInt32();
            if (pid == 0) return false;
            if (pid == ourPid) return false;
            // 桌面 shell 进程自己开的文件夹窗口**完全不碰**（连藏都不藏）—— 理由见
            // EmbedApi.IsShellOwned：碰了会让 Win+E / 开始菜单那条「文件资源管理器」失灵，
            // 而且退程序也不恢复。
            if (EmbedApi.IsShellOwned(h)) return false;
            return true;
        }

        /// <summary>
        /// 把候选窗口登记进去，等 700ms 后再收。
        ///
        /// 为什么要拖一下（实测踩到）：我们给标签起 explorer 时，那个窗口也是「新出现的」，
        /// 而 Hub 这边跟标签那边的监听是**两个独立的钩子**，系统先叫谁不保证。
        /// 曾经直接就地收，日志里就出现过「我们自己起的第5个窗口被 Hub 当成用户新开的抓走了」——
        /// 一旦这样，那个标签会去抢别人的窗口，最后就是一个标签空着（正是用户报的「有时候标签页点开是空的」）。
        ///
        /// 拖这几百毫秒之后，情况就很干净：
        ///   · 是我们自己起的窗口 → 那个标签的 25ms 轮询早就把它**认领**了，这里一看已认领就放手；
        ///   · 是用户自己开的窗口 → 没人认领，到点就收。
        /// ⚠ 注意「拖」的只是**收**（AdoptWindow），**藏**是在 OnWindowShown 里当场做的 ——
        ///   不然这 700ms 里那个原生窗口就明晃晃地摆在屏幕上，用户看到的就是「闪一下」。
        /// </summary>
        private void RegisterCandidate(IntPtr h, int react)
        {
            if (quitting || !Settings.CaptureAll) return;
            if (pendingCapture.ContainsKey(h)) return;
            if (!EmbedApi.IsWindowVisible(h) && !HiddenByUs(h))
            {
                // 已经不在了（被关掉/被别人接管）—— 没登记的藏也别留着
                ReleaseIfAbandoned(h);
                return;
            }
            pendingCapture[h] = new CaptureCandidate { SeenAt = DateTime.Now, ReactMs = react };
            if (captureTimer != null && !captureTimer.Enabled) captureTimer.Start();
        }

        // hiddenByUs 是双线程访问的（watcher 线程藏、UI 线程收尾）—— 一律走这三个口
        private bool HiddenByUs(IntPtr h) { lock (hideLock) return hiddenByUs.Contains(h); }
        private void MarkHidden(IntPtr h) { lock (hideLock) hiddenByUs.Add(h); }
        private bool UnmarkHidden(IntPtr h) { lock (hideLock) return hiddenByUs.Remove(h); }

        /// <summary>这个窗口现在看起来值不值得收（粗筛，真正的收在 AdoptWindow 里再判一次）。
        /// **可能在两个线程上被调**（watcher 线程先筛、UI 线程再复核），所以只碰锁保护的集合。</summary>
        private bool IsCapturable(IntPtr h)
        {
            string c = EmbedApi.ClassOf(h);
            if (c != "CabinetWClass" && c != "ExploreWClass") return false;
            if (EmbedApi.IsClaimed(h)) return false;          // 我们自己起的 / 已经被某个标签收了
            if (EmbedApi.IsBaseline(h)) return false;         // 启动前就在那儿的老窗口（还原、重新显示不算新开）
            // 藏着的多半是我们还没嵌好的 —— 但**我们自己藏起来的那批要认**（防闪就是把它们藏了）
            if (!EmbedApi.IsWindowVisible(h) && !HiddenByUs(h)) return false;
            if (EmbedApi.GetParent(h) != IntPtr.Zero) return false;   // 已经是子窗口 → 被谁嵌走了
            int pid = EmbedApi.ProcessIdOf(h).ToInt32();
            if (pid == 0) return false;
            if (pid == ourPid) return false;
            if (EmbedApi.IsShellOwned(h)) return false;     // 见 IsHideCandidate 那条注释
            return true;
        }

        /// <summary>
        /// 「值不值得把它转生」的粗筛：**跟 `IsHideCandidate` 就差最后那一条（正好相反）** ——
        /// 这里要的恰恰是「桌面 shell 进程自己开的」那种。
        /// 其余条件一样：必须还是个没被人认领、没被嵌走、没属主的新浏览窗口。
        /// </summary>
        private bool IsShellTakeoverCandidate(IntPtr h)
        {
            string c = EmbedApi.ClassOf(h);
            if (c != "CabinetWClass" && c != "ExploreWClass") return false;
            if (EmbedApi.IsClaimed(h)) return false;          // 我们自己起的 / 已经被某个标签收了
            if (EmbedApi.IsBaseline(h)) return false;         // 启动前就在那儿的老窗口
            if (EmbedApi.GetParent(h) != IntPtr.Zero) return false;
            if (EmbedApi.GetWindow(h, EmbedApi.GW_OWNER) != IntPtr.Zero) return false;
            int pid = EmbedApi.ProcessIdOf(h).ToInt32();
            if (pid == 0 || pid == ourPid) return false;
            return EmbedApi.IsShellOwned(h);                  // ★ 就是这条跟 IsHideCandidate 反着来
        }

        /// <summary>登记一个「等地址栏」的 shell 窗口（UI 线程）。</summary>
        private void RegisterShellCandidate(IntPtr h, int react)
        {
            if (quitting || !Settings.CaptureAll || !Settings.CaptureShell) return;
            if (!NativeMethods.IsWindow(h)) return;
            if (pendingShell.ContainsKey(h))
            {
                // 已经登记过了（CREATE 那一次）：SHOW 再来一次就把「等地址栏」的超时**重新起算** ——
                // 早那一次可能一个字节都没读到，别让它的计时先耗光。
                pendingShell[h].SeenAt = DateTime.Now;
                return;
            }
            // 先当场试一次：shell 是先导航后显示，SHOW 那一刻地址栏多半已经填好了 ——
            // 能读出来就直接收，那扇窗在屏幕上只短暂露一下。
            // （CREATE 那一刻也走这条路：那时地址栏通常还没填，于是进 pendingShell、每 250ms 再看。）
            string now = EmbedApi.AddressPathOf(h);
            if (now != null) { TakeOverShellWindow(h, now, react); return; }
            pendingShell[h] = new ShellCandidate { SeenAt = DateTime.Now, ReactMs = react };
            if (captureTimer != null && !captureTimer.Enabled) captureTimer.Start();
        }

        /// <summary>
        /// 等 shell 窗口的地址栏填上真路径：拿到了就转生，超时就还回去。
        /// 轮询而不是等通知 —— 地址栏那个 toolbar 的文本不会给我们发消息（跟标签那边读当前路径是一回事）。
        /// </summary>
        private void DrainShell()
        {
            if (pendingShell.Count == 0) return;
            DateTime now = DateTime.Now;
            foreach (KeyValuePair<IntPtr, ShellCandidate> kv in new List<KeyValuePair<IntPtr, ShellCandidate>>(pendingShell))
            {
                IntPtr h = kv.Key;
                if (!NativeMethods.IsWindow(h))
                {
                    pendingShell.Remove(h);
                    continue;                                 // 它自己没了（被关掉）——没什么可做的
                }
                string path = EmbedApi.AddressPathOf(h);
                if (path == null)
                {
                    // 还没填好地址栏（少见：正常是 SHOW 之前就填好了）—— 再看看，到点就不管它了
                    if ((now - kv.Value.SeenAt).TotalMilliseconds >= ShellResolveMs)
                    {
                        pendingShell.Remove(h);
                        Diag.Step(string.Format("Hub: shell 开的窗口读不出路径，不接管 cab=0x{0:X}", h.ToInt64()));
                    }
                    continue;
                }
                pendingShell.Remove(h);
                // 这行专为回答「到底有没有赶在它露脸之前动手」：报「还没显示」就是抢在了 SHOW 前面
                Diag.Step(string.Format(
                    "Hub: shell 窗口地址栏就绪（登记后 {0}ms，当时{1}）cab=0x{2:X} -> {3}",
                    (int)(now - kv.Value.SeenAt).TotalMilliseconds,
                    EmbedApi.IsWindowVisible(h) ? "已可见" : "还没显示", h.ToInt64(), path));
                TakeOverShellWindow(h, path, kv.Value.ReactMs);
            }
        }

        /// <summary>
        /// 「转生」：shell 进程开的文件夹窗口不归我们（不能 SetParent、也不能改它的样式），但它的目标是我们的。
        /// 读出目标目录 → **像用户点 × 一样**把它关掉 → 用我们自己的 `explorer /n,/separate` 把同一个目录
        /// 开成标签。这样既拿到了标签，又没碰过它的窗口对象 —— v1.13.1 那个「Win+E 按下去没反应」不会发生。
        ///
        /// 关它用 `WM_SYSCOMMAND`/`SC_CLOSE`（点 × 走的就是这条），不用手写 `WM_CLOSE` ——
        /// 关一个**别人的**窗口，越接近正常操作越好（shell 那边可能还有它自己的账要结）。
        ///
        /// ⚠ 我们**没有**藏过它 / 置过透明（见 OnWindowShown 里那段教训），所以这里不需要任何还原动作；
        ///   万一它没关掉，留在屏幕上的也是一个**正常窗口**，而不是隐形窗口。
        /// </summary>
        private void TakeOverShellWindow(IntPtr h, string path, int react)
        {
            try
            {
                Guid d = VirtualDesktop.WindowDesktopId(h);
                if (d == Guid.Empty) d = VirtualDesktop.CurrentDesktopId();
                Diag.Step(string.Format(
                    "Hub: shell 开的文件夹 -> {0}（pid={1}，反应 {2}ms）=> 关掉它、用自己的标签重开",
                    path, EmbedApi.ProcessIdOf(h).ToInt32(), react));
                EmbedApi.PostMessageW(h, 0x0112, (IntPtr)0xF060, IntPtr.Zero);      // WM_SYSCOMMAND / SC_CLOSE
                EmbedForm f = EnsureForm(d);
                if (f == null) { Diag.Log("Hub: shell 窗口转生失败：没有可用的窗口"); return; }
                // 这扇窗是**为了外面那个文件夹**才现建出来的：先把本桌面记着的标签摆回来，再加上它。
                // 少了这一步，用户原来那一整排标签会被「只有这一个」的窗盖掉（退出时还会存进去）。
                bool fresh = f.RestoreRememberedTabs();
                Diag.Step(string.Format(
                    "Hub: 转生目标窗口 桌面={0} 记忆还原={1} 当时标签={2} 可见={3} 当前标签={4}",
                    d, fresh ? "是" : "否（本来就有标签）", f.TabCount, f.Visible, f.ActiveIdx));
                f.OpenPathAsTab(path);
                Diag.Step(string.Format("Hub: 转生完成 标签={0} 当前标签={1}", f.TabCount, f.ActiveIdx));
                // 这一步不能省：用户是在**别的程序**里点的「打开文件夹」，本该有一扇窗弹到最前面。
                // 只 `OpenPathAsTab` 的话窗口只是被 `Show()` 出来、还压在那个程序后面，
                // 用户看到的就只有「原生窗闪了一下 + 任务栏图标闪」= 像什么都没发生。
                f.ShowForCapture();
            }
            catch (Exception ex) { Diag.Log("Hub: shell 窗口转生失败 " + ex.Message); }
        }

        /// <summary>shell 窗口的转生开关。跟捕获开关一样：开关立刻生效，不用装/卸钩子。</summary>
        public void SetCaptureShell(bool on)
        {
            if (Settings.CaptureShell == on) return;
            Settings.SetCaptureShell(on);
            RefreshTrayMenu();
            Diag.Step("Hub: 接管 shell 打开的文件夹 -> " + (on ? "开" : "关"));
        }

        /// <summary>到点的那批 —— 还活着、还没被认领的，收；其余的丢掉。</summary>
        private void DrainCapture()
        {
            DrainShell();
            if (pendingCapture.Count == 0 && pendingShell.Count == 0) { captureTimer.Stop(); return; }
            DateTime now = DateTime.Now;
            List<IntPtr> ready = null;
            foreach (KeyValuePair<IntPtr, CaptureCandidate> kv in new List<KeyValuePair<IntPtr, CaptureCandidate>>(pendingCapture))
            {
                if ((now - kv.Value.SeenAt).TotalMilliseconds < CaptureDelayMs) continue;
                if (ready == null) ready = new List<IntPtr>();
                ready.Add(kv.Key);
            }
            if (ready == null) return;
            foreach (IntPtr h in ready)
            {
                int react = pendingCapture[h].ReactMs;
                pendingCapture.Remove(h);
                AdoptWindow(h, react);
            }
        }

        /// <summary>真收：把这个窗口接成当前（它所在那）桌面窗口里的一个新标签。</summary>
        private void AdoptWindow(IntPtr h, int react)
        {
            if (quitting || !Settings.CaptureAll) { ReleaseIfAbandoned(h); return; }
            try
            {
                if (!IsCapturable(h)) { ReleaseIfAbandoned(h); return; }   // 这一轮里可能已经变了（被认领 / 关掉 / 藏了）
                int pid = EmbedApi.ProcessIdOf(h).ToInt32();

                Guid d = VirtualDesktop.WindowDesktopId(h);
                if (d == Guid.Empty) d = VirtualDesktop.CurrentDesktopId();
                EmbedForm f = EnsureForm(d);
                if (f == null) { ReleaseIfAbandoned(h); return; }

                f.RestoreRememberedTabs();      // 同上：新窗口先把本桌面记着的标签摆回来，再收下这一个

                // ★ 这扇新窗的目标**本来就是我们某个标签**（他原来就开着这个文件夹，只是没切到前面）：
                //   shell 不会去复用那扇窗，而是又开一扇；我们照单全收就成了**两个一模一样的标签**，
                //   而他真正要的是「切到原来那个」（用户报的就是这个）。
                //   所以：把这扇现建出来的窗关掉，切过去、把窗口顶到前台。
                //   ⚠ 必须排在 `RestoreRememberedTabs` 之后 —— 那个「原来的标签」可能刚被记忆摆回来。
                //   ⚠ 也排在防闪那两步之前：这里只**关**、不藏；万一没关成，留在屏幕上的是一扇正常窗，
                //     而不是一扇隐形窗（隐形窗会毒坏 shell，见 v1.13.1 那个「Win+E 没反应」的教训）。
                string incoming = EmbedApi.AddressPathOf(h);
                if (incoming != null && f.HasTabForPath(incoming))
                {
                    Diag.Step(string.Format(
                        "Hub: 新窗的文件夹「{0}」已经有标签 -> 关掉这扇新窗、切到原来的标签", incoming));
                    EmbedApi.ClearTransparent(h);
                    EmbedApi.PostMessageW(h, EmbedApi.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    UnmarkHidden(h);            // 关掉了，别在「我们藏过的那批」里留个野句柄
                    f.OpenPathAsTab(incoming);  // 已有 → 只会切过去（见 OpenPathAsTab）
                    f.ShowForCapture();
                    return;
                }

                // ⚠ 交出去之前先把「防闪那层透明」去掉 —— 先确保它是藏的，再去透明。
                // 顺序反了的话，一个 SW_HIDE 没生效的窗口会在去透明那一瞬间弹出来（比闪一下还难看）。
                EmbedApi.ShowWindow(h, EmbedApi.SW_HIDE);
                EmbedApi.ClearTransparent(h);

                Diag.Step(string.Format("Hub: 收下这个新开的文件夹窗口 cab=0x{0:X} pid={1}（防闪反应 {2}ms）",
                    h.ToInt64(), pid, react));

                if (!f.NewAdoptedTab(h, pid))
                {
                    Diag.Step("Hub: 这个窗口没能收进来（已经收过了 / 失败），保持原样");
                    ReleaseIfAbandoned(h);
                    return;
                }
                UnmarkHidden(h);      // 归标签了，后面由标签负责显示 / 关闭
                f.ShowForCapture();
            }
            catch (Exception ex) { Diag.Log("Hub: 捕获新窗口失败 " + ex.Message); }
        }

        /// <summary>
        /// 我们把它藏过、结果没收成（被别的标签认领了 / 关了 / 收编失败）—— 不能就这么丢着：
        /// 一个被我们藏起来的窗口在用户眼里就是「窗口没了」的死东西。
        /// 已经把窗口收进某个标签的（`IsClaimed`）保持藏着，那个标签自己会显示它、关它。
        /// </summary>
        private void ReleaseIfAbandoned(IntPtr h)
        {
            // ⚠ 去透明必须排在**所有 return 前面**：漏了的话，放回去的就是一个隐形窗口
            // （用户眼里 = 「一扇窗没了，任务栏还留着」）。
            try { EmbedApi.ClearTransparent(h); } catch { }
            if (!UnmarkHidden(h)) return;
            try
            {
                if (EmbedApi.IsClaimed(h)) return;                  // 归标签了，标签管
                if (!NativeMethods.IsWindow(h)) return;             // 已经关了
                if (EmbedApi.GetParent(h) != IntPtr.Zero) return;    // 已经被嵌走了
                Diag.Step(string.Format("Hub: 这个窗口没收成，还它本来面目 cab=0x{0:X}", h.ToInt64()));
                EmbedApi.ShowWindow(h, EmbedApi.SW_SHOW);
            }
            catch (Exception ex) { Diag.Log("Hub: 恢复窗口失败 " + ex.Message); }
        }

        /// <summary>把动作丢回 UI 线程（钩子 / 线程池的回调都在别的线程上）。</summary>
        private void Post(Action a)
        {
            if (a == null || quitting) return;
            try { syncTarget.BeginInvoke(a); } catch { }
        }

        /// <summary>
        /// 弹一条提示。
        ///
        /// ⚠改了实现（用户报「显示提醒的背景和字体颜色没适配颜色模式」）：
        /// 原来走 `NotifyIcon.ShowBalloonTip`，那个气泡是**系统画的**，配色跟系统主题走，
        /// 我们强制浅色/深色时它不认 —— 外壳和气泡两套皮。现在换成自己画的 `Toast`。
        /// 方法签名留着不动，调用点一个都不用改。
        /// </summary>
        public void Notify(string title, string text, bool once)
        {
            try
            {
                if (once && trayTipShown) return;
                trayTipShown = true;
                Toast.Show(title, text);
            }
            catch { }
        }

        // ==================================================================
        // Win+E：找到「当前这张桌面」的窗口
        // ==================================================================

        public void OnWinE()
        {
            if (quitting) return;
            Guid d = VirtualDesktop.CurrentDesktopId();
            Diag.Step("Hub: Win+E，当前桌面=" + Short(d));
            EmbedForm f = EnsureForm(d);
            if (f == null) return;
            f.ShowForUser(true);
        }

        /// <summary>
        /// 当前桌面的窗口。两种模式两条路：
        ///
        /// **按虚拟桌面分别捕获**（perdesktop，默认）：① 先按「窗口实际挂在哪张桌面」认领
        /// （用户用 Win+Ctrl+Shift+方向键把窗口挪到别的桌面之后，这样能自愈）；② 再按登记表；
        /// ③ 都没有就新建一个。新建的窗口天然落在当前桌面 —— 所以永远不需要「搬窗口」。
        ///
        /// **捕获并迁移到当前桌面**（migrate，v1.0.0 那套）：全进程就应该只有一个窗口，
        /// 不管现在在哪张桌面 —— 直接拿它，搬桌面的事交给 EmbedForm（ShowForUser 里做）。
        /// </summary>
        private EmbedForm EnsureForm(Guid desktop)
        {
            Prune();

            if (Settings.Capture == Settings.CaptureMode.Migrate)
            {
                EmbedForm only;
                if (forms.TryGetValue(SingleKey, out only) && only != null && !only.IsDisposed) return only;
                // 刚从严桌面模式换过来、手上已经有窗口：随便认一个当「唯一那个」，别又多开一个
                foreach (EmbedForm f in new List<EmbedForm>(forms.Values))
                {
                    if (f == null || f.IsDisposed) continue;
                    return Adopt(f, SingleKey);
                }
                Diag.Step("Hub: 迁移模式，建唯一窗口");
                return NewForm(SingleKey);
            }

            string key = KeyOf(desktop);

            if (desktop != Guid.Empty)
            {
                foreach (EmbedForm f in new List<EmbedForm>(forms.Values))
                {
                    if (f == null || f.IsDisposed) continue;
                    Guid g = VirtualDesktop.WindowDesktopId(f.Handle);
                    if (g != Guid.Empty && g == desktop) return Adopt(f, key);
                }
            }

            EmbedForm hit;
            if (forms.TryGetValue(key, out hit) && hit != null && !hit.IsDisposed) return hit;

            Diag.Step(string.Format("Hub: 给桌面 {0} 建窗口（现有 {1} 个）", key, forms.Count));
            return NewForm(key);
        }

        private EmbedForm NewForm(string key)
        {
            EmbedForm nf = new EmbedForm(this, key);
            forms[key] = nf;
            nf.FormClosed += delegate { OnFormGone(nf); };
            hook.MainWindow = nf.Handle;      // OursIsForeground 的兜底分支要用
            nf.SetFavBarOn(Settings.FavBar);  // 新窗口要跟上当前的书签栏开关（设置存在文件里，窗口自己不知道）
            return nf;
        }

        /// <summary>窗口被挪到别的桌面（或换了捕获模式）：登记改到新的键，**标签跟着窗口走**。</summary>
        private EmbedForm Adopt(EmbedForm f, string key)
        {
            if (string.Equals(f.DesktopKey, key, StringComparison.OrdinalIgnoreCase)) return f;
            Diag.Step("Hub: 窗口改登记 " + f.DesktopKey + " -> " + key);
            if (forms.ContainsKey(f.DesktopKey) && forms[f.DesktopKey] == f) forms.Remove(f.DesktopKey);
            f.DesktopKey = key;
            forms[key] = f;
            MarkDirty();
            return f;
        }

        // ==================================================================
        // 捕获方式（设置菜单第一项）
        // ==================================================================

        /// <summary>当前的标签捕获方式（EmbedForm 也要看，决定 Win+E 时搬不搬窗口）。</summary>
        public Settings.CaptureMode Capture { get { return Settings.Capture; } }

        /// <summary>
        /// 换捕获方式 —— 立刻生效，并且**把记忆搬个家**，免得用户切一下发现标签「没了」：
        ///   - 切到迁移模式：把当前桌面那一套搬进 `single`（`single` 已有内容就不动）；
        ///     只留前台那个窗口，其余收掉 —— 它们的标签已经各自落进自己桌面的桶里。
        ///   - 切回分桌面模式：把 `single` 搬进当前桌面那张的桶（那张已有内容就不动），
        ///     窗口也从 `single` 改登记到当前桌面。
        /// </summary>
        public void SetCaptureMode(Settings.CaptureMode m)
        {
            if (Settings.Capture == m) return;
            Diag.Step("Hub: 捕获方式 " + Settings.Text(Settings.Capture) + " -> " + Settings.Text(m));
            Settings.SetCapture(m);

            EmbedForm keep = ForegroundForm();
            if (keep == null || keep.IsDisposed)
            {
                foreach (EmbedForm f in new List<EmbedForm>(forms.Values))
                {
                    if (f != null && !f.IsDisposed) { keep = f; break; }
                }
            }

            Guid cur = VirtualDesktop.CurrentDesktopId();
            string dk = KeyOf(cur);

            if (m == Settings.CaptureMode.Migrate)
            {
                foreach (EmbedForm f in new List<EmbedForm>(forms.Values))
                {
                    if (f == null || f.IsDisposed || f == keep) continue;
                    Diag.Step("Hub: 迁移模式，收掉多余窗口 " + f.DesktopKey);
                    try { f.Quitting = true; f.Close(); }
                    catch (Exception ex) { Diag.Log("Hub: 收窗口失败 " + ex.Message); }
                }
                forms.Clear();

                if (keep != null && !keep.IsDisposed)
                {
                    MoveMemory(keep.DesktopKey, SingleKey);
                    keep.DesktopKey = SingleKey;
                    forms[SingleKey] = keep;
                    hook.MainWindow = keep.Handle;
                }
            }
            else if (keep != null && !keep.IsDisposed)
            {
                MoveMemory(SingleKey, dk);
                forms.Remove(SingleKey);
                keep.DesktopKey = dk;
                forms[dk] = keep;
            }

            MarkDirty();
            RefreshTrayMenu();
        }

        // ==================================================================
        // 其余设置（颜色模式 / 保留标签 / 标签宽度 / 自适应）
        // ==================================================================

        /// <summary>颜色模式：跟随系统 / 浅色 / 深色。改完立刻重算调色板并让各窗口重刷。</summary>
        public void SetColorMode(Settings.ColorMode m)
        {
            if (Settings.Color == m) return;
            Settings.SetColor(m);
            Theme.SetMode(m);        // RaiseChanged -> 各窗口自己重刷（含 explorer 的逐窗口主题）
            RefreshTrayMenu();
            // 不再弹提示（用户：非重要变更不用右下角弹窗）。
            // ⚠ 仍然要记住的限制：嵌进来的 explorer 是**独立进程**，它那块文件列表按系统主题画，
            //   我们只能逐窗口 SetWindowTheme 尽力而为。这一条写在设置窗口的说明里。
        }

        /// <summary>是否保留标签页。关掉 = 不还原记忆，每次打开都是全新一个「此电脑」。</summary>
        public void SetKeepTabs(bool on)
        {
            if (Settings.KeepTabs == on) return;
            Settings.SetKeepTabs(on);
            RefreshTrayMenu();
        }

        /// <summary>
        /// 懒加载标签页。**只影响下次还原**（这一次已经摆好的标签不动）——
        /// 所以改完只刷一下菜单勾选，不重建任何窗口。
        /// </summary>
        public void SetLazyTabs(bool on)
        {
            if (Settings.LazyTabs == on) return;
            Settings.SetLazyTabs(on);
            RefreshTrayMenu();
        }

        /// <summary>标签页宽度（逻辑像素）。立刻重排所有标签条。</summary>
        public void SetTabWidth(int w)
        {
            int v = Settings.ClampWidth(w);
            if (Settings.TabWidth == v) return;
            Settings.SetTabWidth(v);
            RefreshTrayMenu();
            RetabAll();
        }

        /// <summary>自适应宽度①：文件夹名过长时自动加宽（用户把这个和「缩窄」拆开了）。</summary>
        public void SetTabAutoWiden(bool on)
        {
            if (Settings.TabAutoWiden == on) return;
            Settings.SetTabAutoWiden(on);
            RefreshTrayMenu();
            RetabAll();
        }

        /// <summary>自适应宽度②：挤不下时是否自动缩窄。</summary>
        public void SetTabAutoFit(bool on)
        {
            if (Settings.TabAutoFit == on) return;
            Settings.SetTabAutoFit(on);
            RefreshTrayMenu();
            RetabAll();
        }

        private void RetabAll()
        {
            foreach (EmbedForm f in new List<EmbedForm>(forms.Values))
            {
                if (f == null || f.IsDisposed) continue;
                f.RefreshTabs();
            }
        }

        /// <summary>
        /// 书签栏开关（Ctrl+Shift+B）。**所有入口最终都汇到这儿**：热键、标签条上的按钮、
        /// 设置菜单里那一项、标签条空白右键、书签栏自己的右键 —— 免得像当初「托盘漏了设置项」那样漏一边。
        /// </summary>
        public void SetFavBar(bool on)
        {
            if (Settings.FavBar == on) return;
            Settings.SetFavBar(on);
            RefreshTrayMenu();
            FavBarAll(on);
        }

        private void FavBarAll(bool on)
        {
            foreach (EmbedForm f in new List<EmbedForm>(forms.Values))
            {
                if (f == null || f.IsDisposed) continue;
                f.SetFavBarOn(on);
            }
        }

        /// <summary>
        /// 是否捕获**所有**打开的文件夹（Bug 1 要的那个行为）。
        /// 监听一直是装着的（见 SetupCapture），这里只是改一个开关 —— `TryCapture` 每次都会看它，
        /// 所以开关是立刻生效的，不用装/卸钩子。
        /// </summary>
        public void SetCaptureAll(bool on)
        {
            if (Settings.CaptureAll == on) return;
            Settings.SetCaptureAll(on);
            RefreshTrayMenu();
        }

        /// <summary>
        /// 退出后是否记住窗口位置和大小（默认开）。改了立刻生效，但**只影响下次还原** ——
        /// 关掉它不会去动手上那个窗口，也不会把已经记着的那份删掉（随时再打开就又能用）。
        /// </summary>
        public void SetWindowSize(bool on)
        {
            if (Settings.WindowSize == on) return;
            Settings.SetWindowSize(on);
            RefreshTrayMenu();
            Diag.Step("Hub: 记住窗口位置和大小 -> " + (on ? "开" : "关"));
        }

        /// <summary>
        /// 垂直侧边栏开关（用户：Ctrl+Shift+, / 设置菜单）。
        /// 所有窗口一起切 —— 它是窗口外壳的排法，不是某张桌面的事。
        /// </summary>
        public void SetVerticalTabs(bool on)
        {
            if (Settings.VTabs == on) return;
            Settings.SetVTabs(on);
            RefreshTrayMenu();
            VerticalAll();
            Diag.Step("Hub: 垂直侧边栏 -> " + (on ? "开" : "关"));
        }

        /// <summary>
        /// 垂直窗格的「折叠窗格」开关（窗格顶上那枚图钉）。
        /// 开 = 鼠标不在窗格上时收缩成纯图标、进来才临时展开；关 = 一直显示完整标题。
        /// </summary>
        public void SetVTabCollapse(bool on)
        {
            if (Settings.VTabsCollapse == on) return;
            Settings.SetVTabsCollapse(on);
            RefreshTrayMenu();
            VerticalAll();
            Diag.Step("Hub: 垂直窗格折叠 -> " + (on ? "开（平时只显示图标）" : "关（一直显示标题）"));
        }

        /// <summary>
        /// 侧边栏「摊开盖在内容上」那一下的不透明度（%，100 = 完全不透明）。
        /// 数值项，改完所有窗口一起重排（`ApplyVertical` 会把新值读到布局里）。
        /// </summary>
        public void SetVPaneAlpha(int pct)
        {
            if (Settings.VPaneAlpha == pct) return;
            Settings.SetVPaneAlpha(pct);
            RefreshTrayMenu();
            VerticalAll();
            Diag.Step("Hub: 侧边栏不透明度 -> " + Settings.VPaneAlpha + "%");
        }

        private void VerticalAll()
        {
            foreach (EmbedForm f in new List<EmbedForm>(forms.Values))
            {
                if (f == null || f.IsDisposed) continue;
                try { f.ApplyVertical(); }
                catch (Exception ex) { Diag.Log("Hub: 切垂直侧边栏失败 " + ex.Message); }
            }
        }

        /// <summary>忘掉某张桌面记着的窗口位置和大小（托盘菜单「恢复默认窗口位置和大小」用）。</summary>
        public void ForgetBounds(string key)
        {
            DesktopMemory.Bucket b = memory.Find(key);
            if (b == null || string.IsNullOrEmpty(b.Bounds)) return;
            b.Bounds = null;
            memory.Save();
            Diag.Step("Hub: 已忘掉桌面 " + key + " 记着的窗口尺寸");
        }

        /// <summary>
        /// 托盘菜单「恢复默认窗口位置和大小」：把前台那个窗口（没前台就找任何一个显示着的）
        /// 摆回默认大小并居中，同时把记忆里那份尺寸去掉。
        /// 一个窗口都没有就先按 Win+E 开一个 —— 新开的本来就是默认尺寸，用户要的结果一样。
        /// </summary>
        public void RestoreDefaultWindow()
        {
            EmbedForm f = ForegroundForm();
            if (f == null)
            {
                foreach (EmbedForm x in new List<EmbedForm>(forms.Values))
                {
                    if (x != null && !x.IsDisposed && x.Visible) { f = x; break; }
                }
            }
            if (f == null) { Diag.Step("Hub: 恢复默认窗口位置和大小 -> 现在没窗口，先开一个"); OnWinE(); return; }
            try { f.RestoreDefaultBounds(); }
            catch (Exception ex) { Diag.Log("Hub: 恢复窗口尺寸失败 " + ex.Message); }
        }

        /// <summary>
        /// Debug 模式（用户：「是否写入日志，由设置中的 Debug 模式决定，默认不开，
        /// 不过我们要开」）。改了立刻生效 —— `Diag` 每次写之前都现看那个闸，不用重启。
        /// </summary>
        public void SetDebug(bool on)
        {
            if (Settings.Debug == on) return;
            Settings.SetDebug(on);
            RefreshTrayMenu();
            // 这一句要在开关**生效之后**记：打开时会落盘；关掉时它自己也写不进去 —— 本来就该如此。
            Diag.Step("Hub: Debug 模式 -> " + (on ? "开（后面每一步都写 data\\log.txt）" : "关"));
        }

        /// <summary>
        /// 开机自启（用户要的设置项）。
        ///
        /// 实现的**唯一真相在注册表**：`HKCU\...\Run` 里的 `TabbedExplorer` 值（见 AutoStart），
        /// 不往 settings.json 里再存一份 —— 两份状态一旦对不上（用户自己用任务管理器禁用了启动项），
        /// 菜单里打的勾就是假的。所以这个开关没有 `Settings.XXX` 字段，每次都现问注册表。
        ///
        /// 启动方式带 `--tray`：只驻留托盘 + 装 Win+E 钩子，**不弹窗口**。
        /// </summary>
        public void SetAutoStart(bool on)
        {
            bool ok = AutoStart.Set(on);
            RefreshTrayMenu();
            if (!ok)
            {
                // 改不动是「出错」，得说一声；成功就不弹了（用户：非重要变更不用弹窗）
                Notify("开机自启", "改不了启动项（注册表写不进去），还是原样。", false);
                return;
            }
            Diag.Step("Hub: 开机自启 -> " + (on ? "开" : "关"));
        }

        /// <summary>把 from 桶的内容搬进 to 桶 —— **只在 to 还空着的时候**搬，不覆盖已记过的。</summary>
        private void MoveMemory(string from, string to)
        {
            if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to)) return;
            if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return;
            DesktopMemory.Bucket toB = memory.Find(to);
            if (toB != null && toB.Paths.Count > 0) return;
            DesktopMemory.Bucket fromB = memory.Find(from);
            if (fromB == null || fromB.Paths.Count == 0) return;

            DesktopMemory.Bucket dst = memory.Ensure(to);
            dst.Paths.Clear();
            dst.Paths.AddRange(fromB.Paths);
            dst.Active = fromB.Active;
            Diag.Step(string.Format("Hub: 记忆搬家 {0} -> {1}（{2} 个标签）", from, to, dst.Paths.Count));
        }

        /// <summary>「记住当前标签」：立刻把各桌面的标签写盘（托盘菜单和设置窗口里都有）。
        /// 平时是攒 800ms 自动写 + 退出前再写，这个按钮就是「现在立刻写一次」。</summary>
        public void RememberNow()
        {
            try { saveTimer.Stop(); } catch { }
            SaveNow("手动");
        }

        /// <summary>Ctrl+T / Ctrl+W / Ctrl+Tab：派给「前台那个窗口」。
        /// 钩子只在「我们进程是前台」时才拦这些键，所以这里找得到就一定是自己人。
        /// 参数是**命令标识**（`newtab` …）或 `goto:<0 基下标>`。</summary>
        private void Hotkey(string cmd)
        {
            EmbedForm f = ForegroundForm();
            if (f == null) { Diag.Step("Hub: 热键 " + cmd + " 但没有我们的前台窗口，忽略"); return; }
            f.HandleHotkey(cmd);
        }

        private EmbedForm ForegroundForm()
        {
            IntPtr fg = NativeMethods.GetForegroundWindow();
            if (fg == IntPtr.Zero) return null;
            IntPtr root = NativeMethods.GetAncestor(fg, 2);   // GA_ROOT
            foreach (EmbedForm f in new List<EmbedForm>(forms.Values))
            {
                if (f == null || f.IsDisposed) continue;
                IntPtr h = f.Handle;
                if (h == fg || (root != IntPtr.Zero && root == h)) return f;
            }
            return null;
        }

        // ==================================================================
        // 记忆
        // ==================================================================

        public DesktopMemory.Bucket MemoryOf(string key)
        {
            return memory.Find(key);
        }

        /// <summary>标签有变动（开/关/导航/切换）—— 攒 800ms 再写盘，免得一路点进去写十几次。</summary>
        public void MarkDirty()
        {
            if (quitting) return;
            try { saveTimer.Stop(); saveTimer.Start(); } catch { }
        }

        /// <summary>
        /// 把每个**活着的**窗口现在的标签写进它那张桌面的桶里，然后落盘。
        ///
        /// 只覆盖「有窗口在的桌面」—— 这一轮没碰过的那些桌面，读进来什么样就写回去什么样，不会被清空。
        /// 另外**不动已经关掉的窗口**的桶：那次会话最后长什么样就留着什么样的标签，下次还在。
        /// </summary>
        public void SaveNow(string why)
        {
            try
            {
                int live = 0, tabs = 0;
                foreach (EmbedForm f in new List<EmbedForm>(forms.Values))
                {
                    if (f == null || f.IsDisposed) continue;
                    DesktopMemory.Bucket b = memory.Ensure(f.DesktopKey);
                    b.Paths.Clear();
                    b.Active = null;

                    string active = f.ActiveTabPath;
                    foreach (string p in f.TabPaths())
                    {
                        if (string.IsNullOrEmpty(p)) continue;
                        b.Paths.Add(p);
                        if (PathRules.Same(p, active)) b.Active = p;
                        tabs++;
                    }
                    // 窗口位置和大小（用户：完全退出后下次照原样打开）。
                    // ⚠ 只有「真给用户看过」的窗口才有值（空 = 不动记忆里那份）——
                    //   否则启动时预建但一直没露面的窗口会用它构造时的默认尺寸把旧记忆洗掉。
                    if (Settings.WindowSize)
                    {
                        string bs = f.BoundsString;
                        if (!string.IsNullOrEmpty(bs)) b.Bounds = bs;
                    }
                    live++;
                }
                memory.Save();
                Diag.Step(string.Format("Hub: 记忆已保存（{0}）：{1} 个窗口 / {2} 个标签 / 共记着 {3} 张桌面",
                    why, live, tabs, memory.Count));
            }
            catch (Exception ex) { Diag.Log("Hub: 保存记忆失败 " + ex.Message); }
        }

        /// <summary>真退出。先把记忆落盘，再让每个窗口把标签里的 explorer 收干净。</summary>
        public void Quit(string why)
        {
            if (quitting) return;
            quitting = true;
            Diag.Step("Hub: 退出（" + why + "）");
            try { saveTimer.Stop(); } catch { }
            SaveNow("退出前");

            List<EmbedForm> all = new List<EmbedForm>(forms.Values);
            forms.Clear();
            foreach (EmbedForm f in all)
            {
                try { f.Quitting = true; f.Close(); }
                catch (Exception ex) { Diag.Log("Hub: 关窗口失败 " + ex.Message); }
            }
            ExitThread();
        }

        private void OnFormGone(EmbedForm f)
        {
            foreach (string k in new List<string>(forms.Keys))
            {
                if (forms[k] == f) forms.Remove(k);
            }
            Diag.Step("Hub: 窗口没了，还剩 " + forms.Count + " 个");
            MarkDirty();
        }

        private void Prune()
        {
            foreach (string k in new List<string>(forms.Keys))
            {
                EmbedForm f = forms[k];
                if (f == null || f.IsDisposed) forms.Remove(k);
            }
        }

        private static string KeyOf(Guid g)
        {
            return g == Guid.Empty ? "unknown" : g.ToString("D");
        }

        private static string Short(Guid g)
        {
            return g == Guid.Empty ? "(问不出)" : g.ToString().Substring(0, 8);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposed) { base.Dispose(disposing); return; }
            disposed = true;
            Diag.Step("Hub: Dispose");

            try { saveTimer.Dispose(); } catch { }
            if (sigWait != null) { try { sigWait.Unregister(null); } catch { } sigWait = null; }
            if (quitWait != null) { try { quitWait.Unregister(null); } catch { } quitWait = null; }
            if (quitEvent != null) { try { quitEvent.Close(); } catch { } quitEvent = null; }
            if (captureWatch != null) { try { captureWatch.Dispose(); } catch { } captureWatch = null; }
            if (settingsForm != null) { try { settingsForm.Close(); } catch { } settingsForm = null; }
            if (favManager != null) { try { favManager.Close(); } catch { } favManager = null; }
            if (captureTimer != null) { try { captureTimer.Dispose(); } catch { } captureTimer = null; }
            if (hook != null) { try { hook.Dispose(); } catch { } hook = null; }
            if (wheelHook != null) { try { wheelHook.Dispose(); } catch { } wheelHook = null; }
            if (tray != null)
            {
                try { tray.Visible = false; tray.Dispose(); } catch { }
                tray = null;
            }
            try { syncTarget.Dispose(); } catch { }

            base.Dispose(disposing);
        }
    }
}
