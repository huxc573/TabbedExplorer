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

        /// <summary>
        /// **额外窗口**（用户要的多窗口：把标签拖出标签条 = 再开一个窗口，像浏览器那样）。
        ///
        /// 为什么不塞进 `forms`：那张表是「一张虚拟桌面 → 一个窗口」，三件事同时靠它 ——
        /// Win+E 找落点、记忆按键分桶、`VirtualDesktop.WindowDesktopId` 自愈认领。
        /// 一张桌面塞两个进去，这三件事一起坏（最后一个写入的会把前一个的标签覆盖掉）。
        /// 所以主窗口照旧，额外窗口在这儿另排一队：
        ///   · Win+E **永远只给主窗口**（额外窗口不参与）——「主窗口是这张桌面的正门」；
        ///   · 记忆：额外窗口的标签**合并**写进它那张桌面的桶里（见 `SaveNow`），标签一个不丢；
        ///   · 设置广播 / 前台判定 / 空格预览白名单：两边一起算（见 `AllForms`）。
        /// </summary>
        private readonly List<EmbedForm> extras = new List<EmbedForm>();

        /// <summary>
        /// **所有**活着的窗口：主窗口（`forms`）在前、额外窗口在后。
        /// 顺序有意义 —— `SaveNow` 要先把主窗口的标签当正本写进去，再把额外窗口的追加在后面。
        /// </summary>
        private List<EmbedForm> AllForms()
        {
            List<EmbedForm> r = new List<EmbedForm>();
            foreach (EmbedForm f in forms.Values)
                if (f != null && !f.IsDisposed) r.Add(f);
            for (int i = 0; i < extras.Count; i++)
            {
                EmbedForm f = extras[i];
                if (f == null || f.IsDisposed) continue;
                if (r.Contains(f)) continue;
                r.Add(f);
            }
            return r;
        }

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

        /// <summary>
        /// 预加载：**一次性**的延时器 —— 起来之后隔一会儿再去后台把本桌面的标签起出来（见 Settings.AutoPreload）。
        /// 为什么不立刻做：启动那一下要先把托盘图标、键盘钩子、图标缓存摆好，别跟它抢（用户能感觉到「启动慢」）。
        /// </summary>
        private System.Windows.Forms.Timer preloadTimer;

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
        /// <summary>
        /// 盯梢线程已经抢出来的路径（值 = 路径 + 当时的事件号），等 UI 线程来兑现（见 <see cref="WatchShellWindow"/>）。
        /// 单独一份的原因：盯梢线程抢到路径时，那扇窗**已经被关掉了** —— 不能靠「窗口还活着」这个前提。
        /// </summary>
        private readonly Dictionary<IntPtr, ShellCandidate> shellReady =
            new Dictionary<IntPtr, ShellCandidate>();
        /// <summary>正在被盯的 shell 窗口（避免 CREATE / SHOW 两条事件各起一条线程）。</summary>
        private readonly HashSet<IntPtr> shellWatched = new HashSet<IntPtr>();
        /// <summary>被我们清空过绘制区的 shell 窗口（收尾要还原，见 <see cref="BlankShellWindow"/>）。</summary>
        private readonly HashSet<IntPtr> shellBlanked = new HashSet<IntPtr>();
        /// <summary>
        /// 「转生之后还得替用户把选中项摆回去」的那批活儿（见 <see cref="ScheduleReveal"/>）。
        ///
        /// 三方（wb / VS Code / 各种「在资源管理器里显示」）点开一个文件夹，走的其实是
        /// `explorer /select,"&lt;目标&gt;"` —— 原生那一下是**父目录 + 把目标选中**。
        /// 我们只把它开成「父目录」这一个标签，选中项就丢了（用户报的「没有选中指定目录，原生是选中的」）。
        /// </summary>
        private readonly List<RevealTask> reveals = new List<RevealTask>();
        /// <summary>摆选中项的节拍器（见 <see cref="DrainReveal"/>）。⚠ 用 `System.Threading.Timer` 而不是
        /// `System.Windows.Forms.Timer`：后者走 `WM_TIMER`，只在消息队列空的时候才发 —— 而这段正是
        /// 起 explorer 最忙的时候，会被饿死（详见 `MEMORY-internals` 里那条）。</summary>
        private System.Threading.Timer revealTimer;
        /// <summary>
        /// 摆选中项的节拍。给得短是因为**第一段能用的时间只有几百毫秒**：转生那一刻到那扇 shell 窗
        /// 被关掉之间（`ShellSettleMs` 减去 `DrainShell` 的等待），要问出「选中了什么」。
        /// 一次 `SelectedPathOfWindow` 实测 2~4ms，这个节拍无所谓。
        /// </summary>
        private const int RevealTickMs = 100;
        /// <summary>补交键盘焦点的间隔与拍数（见 <see cref="TryFocusRevealed"/>）。</summary>
        private const int FocusRetryMs = 500;
        private const int FocusRetryRounds = 3;
        /// <summary>
        /// 第一段（等盯梢线程把「用户要的是哪一个」问出来）的上限。
        /// 答案由盯梢线程在「读到路径 + `ShellSettleMs`」那 800ms 里抓，所以这里给得比它宽 ——
        /// 这段只是「别让单子永远挂着」，不是「窗口还活着多久」（见 <see cref="WatchShellWindow"/>）。
        /// </summary>
        private const int RevealAskMs = 1500;
        /// <summary>
        /// 第二段（把选中项摆进**我们自己**的标签）的总时长。给得宽：新建的标签要等 explorer
        /// 真把目录列出来才认得 `ParseName`（实测那一步 1.5~5 秒，慢盘 / 网络盘更久）。
        /// </summary>
        private const int RevealApplyMs = 8000;
        /// <summary>第二段的重试间隔。摆一次只要几毫秒，失败了下一拍再来。</summary>
        private const int RevealApplyGapMs = 400;

        /// <summary>
        /// 盯梢线程问出来的「那扇 shell 窗选中了什么」（hwnd → 结果）。见 <see cref="WatchShellWindow"/>。
        ///
        /// 为什么要单开一份、不让 UI 线程自己去问：**问它的机会只有那 800ms** —— 窗口一关，
        /// `IShellWindows` 里那一项就没了。而转生那一刻 UI 线程正被收编 / 起 explorer 塞满，
        /// `Post` 回来的节拍能晚到一秒以上，等它跑起来窗口早没了（实锤：日志
        /// `Hub: 那扇 shell 窗已经没了，问不出选中项`）。盯梢线程自己 25ms 一拍、不受 UI 线程影响，
        /// 问着了就存这儿，UI 线程什么时候来取都行。
        /// </summary>
        private readonly Dictionary<IntPtr, ShellCandidate> shellSelected =
            new Dictionary<IntPtr, ShellCandidate>();
        /// <summary>
        /// 已经转生过的 shell 窗口（hwnd → 转生那一刻的 TickCount）。
        ///
        /// 为什么非要挡：同一条命令会在几十~几百毫秒里被报**好几遍**（CREATE 一次、SHOW 一次、
        /// 盯梢线程把路径交上来又一次），每一遍都能凑齐「窗口 + 路径」再走进 <see cref="TakeOverShellWindow"/>。
        /// 不挡就是**同一个文件夹开两个标签**（实测 21:51:45 那次：第一次用掉预热标签、第二次又新起一个
        /// explorer 进程），而多出来的这一下正好压在用户要点开的那扇窗上、把它的 SHOW 事件顶后 47ms ——
        /// 用户看到的就是那一下闪（同一命令的前几次 SHOW 都是 0ms 处理、不闪）。
        /// </summary>
        private readonly Dictionary<IntPtr, int> shellTaken = new Dictionary<IntPtr, int>();
        /// <summary>`shellTaken` 记多久。窗口句柄会被系统复用，记太久会误挡后来的窗。</summary>
        private const int TakenRememberMs = 10000;
        /// <summary>本进程 pid（`IsCapturable` 在 watcher 线程上也要用，别每次现问）。</summary>
        private readonly int ourPid = Process.GetCurrentProcess().Id;
        private System.Windows.Forms.Timer captureTimer;
        /// <summary>候选窗口要「晾」多久才收。够短，用户感觉不出来；够长，让标签先把自己起的窗口认领掉。</summary>
        private const int CaptureDelayMs = 700;
        /// <summary>
        /// shell 窗口「等它报出目标目录」的上限。从 CREATE 就开始等（那 ~0.7 秒是白捡的），
        /// 超时按**最近一次事件**重新起算，免得早那一次先把点耗光。
        ///
        /// 给得这么宽是因为那扇窗从 CREATE 起就被我们按住（`SW_HIDE`，见 `HushShellWindow`）——
        /// 用户看不到它，慢一点无所谓；反倒是逼得太紧会误判。到点还读不出就把窗口**放开**
        /// （`UnhushShellWindow`，不留在 shell 进程里），当没这回事：它还是那个正常窗口，用户自己关。
        /// </summary>
        private const int ShellResolveMs = 1200;
        /// <summary>
        /// 盯梢线程读到路径之后**再等这么久才 `SC_CLOSE`**（2026-09-25 实测定案，见 <see cref="WatchShellWindow"/>）。
        ///
        /// 那扇窗从开出来到被抽掉太快，会让系统在**关窗后约 0.6s** 发一声提示音（用户报的「咚」——
        /// 音频会话探头按「出声率」量的：立刻关 21 次响 9 次，等 800ms 再关 **16 次响 0 次**；
        /// 同一轮里换成 `WM_CLOSE`、或干脆不 `SW_HIDE`，都照样响 ⇒ 与**关法**无关，只与**关得太早**有关）。
        ///
        /// 代价：一扇已经被按住（`SW_HIDE`，隐形）+ 绘制区已清空的窗，在 shell 进程里多活 800ms ——
        /// 用户看不见，标签也**不会**因此晚开（路径在关窗之前就交给 UI 线程了，见 `WatchShellWindow`）。
        /// </summary>
        private const int ShellSettleMs = 800;
        /// <summary>
        /// 「用原生资源管理器打开」的让行期（见 <see cref="OpenNative"/>）。
        ///
        /// 用户在标签右键里点这一项，要的是**一扇真正的原生窗口**；可我们默认「只要是新冒出来的
        /// 文件夹窗口就收成标签」，不挡一下就把这个功能本身吃掉了。
        /// 写 `int` 而不是 `DateTime`：这字段会被 watcher 线程读、UI 线程写，int 的读写天然是原子的。
        /// </summary>
        private int nativeOpenUntilTick;
        /// <summary>
        /// 让行期的长度。只用来兜底「一个窗都没等到」的情况（比如 shell 直接复用了已有的原生窗、
        /// 根本不建新窗）—— 真等到窗口就立刻作废，所以不会一直压着后面的事件。
        /// </summary>
        private const int NativeOpenMs = 4000;
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
            else if (Settings.AutoPreload)
            {
                // 预加载（见 Settings.AutoPreload）：窗口都还没开，先在后台把本桌面记着的标签起出来。
                // 隔一秒再干 —— 启动那一下的活（托盘图标 / 钩子 / 图标缓存）先让完，
                // 不然用户会觉得「这程序怎么越起越慢」。到点也只管**当前这张桌面**。
                preloadTimer = new System.Windows.Forms.Timer();
                preloadTimer.Interval = 1000;
                preloadTimer.Tick += delegate
                {
                    preloadTimer.Stop();
                    preloadTimer.Dispose();
                    preloadTimer = null;
                    PreloadCurrentDesktop();
                };
                preloadTimer.Start();
                Diag.Step("Hub: 预加载已排上（1 秒后后台起本桌面记着的标签）");
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
            // 多窗口（用户：体验跟浏览器一样）：再开一个窗口，标签可以拖过来拖过去。
            // 「打开窗口」给的是**主窗口**（Win+E 的落点），这一条是额外的那个。
            MenuItem miNewWin = new MenuItem("新建窗口", delegate { OpenExtraWindow(null); });
            // 用户：托盘里那条「记住当前标签」去掉，换成两个管理器 ——
            // 手动存标签本来就用不上（改动攒 800ms 自己落盘 + 退出前再存一次，见 RememberNow），
            // 而两个管理器以前只在窗口内够得着（书签栏左端 / 标签条右键），放托盘里更好摸。
            // ⚠ 设置窗口里那条「立即记住当前标签」**保留**（用户明确说的：只删托盘这条）。
            MenuItem miFav = new MenuItem("书签管理器", delegate { OpenFavManager(); });
            MenuItem miHist = new MenuItem("历史记录管理器", delegate { OpenHistoryManager(); });
            // 重启本程序放在**主菜单**、紧挨着「退出」上面（用户点名要的位置）：
            // 改完设置 / 换完皮肤想让程序从头走一遍时，它就在手边。
            // ⚠ `SettingsMenu.Spec` 里那一条套了 `WinOnly`（托盘里不排）—— 就是为了不在这里再排一遍。
            MenuItem miRestart = new MenuItem("重启本程序", delegate { RestartApp(); });
            MenuItem miQuit = new MenuItem("退出", delegate { Quit("托盘菜单"); });

            // 设置子菜单跟齿轮那份**同一份内容**（SettingsMenu 里生成），别各写一遍 ——
            // 用户报的「托盘右键没有设置选项」就是两边各写一遍漏出来的。
            traySettings = SettingsMenu.BuildTraySettings(this);

            trayMenu = new ContextMenu(new MenuItem[]
            {
                miShow, miNewWin, miFav, miHist, traySettings.Root, new MenuItem("-"), miRestart, miQuit
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
            // 空格（「空格键预览」）：钩子线程只判「前台是不是我们某个宿主窗体」，
            // 真干活（问 shell 要选中项 + 起 QuickLook）投回 UI 线程，见 `OnSpacePreview`。
            hook.SpacePreview += delegate { Post(OnSpacePreview); };
            // 方向键 / Esc / Enter（「换预览内容 / 关预览」，见 QLPreview）：同理，钩子线程只判前台，
            // 真干活投回 UI 线程，见 `OnPreviewKey`。
            hook.PreviewKey += delegate(int vk)
            {
                int k = vk;
                Post(delegate { OnPreviewKey(k); });
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

        /// <summary>一条「把选中项摆回去」的单子（见 <see cref="ScheduleReveal"/>）。整条只在 UI 线程上动。</summary>
        private sealed class RevealTask
        {
            /// <summary>那扇 shell 窗 —— 我们要**趁它还活着**问出它选中了什么。</summary>
            public IntPtr Shell;
            /// <summary>它开着的目录 = 我们标签开着的那个。</summary>
            public string Folder;
            /// <summary>问出来的目标（问不到就一直空着，`RevealAskMs` 到点作废）。</summary>
            public string Item;
            /// <summary>起算时刻（TickCount，用来算两段活儿各自的上限）。</summary>
            public int Started;
            /// <summary>下一次试着摆的时刻（TickCount）。</summary>
            public int NextApply;
        }

        /// <summary>shell 窗口转生候选：等它的地址栏出现真路径（见 ShellResolveMs）。</summary>
        private sealed class ShellCandidate
        {
            public DateTime SeenAt;
            public int ReactMs;
            /// <summary>盯梢线程抢出来的路径（只有 <see cref="shellReady"/> 里那份才用）。</summary>
            public string Path;
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
                bool candidate = shellTake || IsHideCandidate(h);
                // ★ 用户刚点了「用原生资源管理器打开」：这几秒里冒出来的候选窗口就是他要的 ——
                //   原样放过，**而且认下这一扇**（`MarkNativeKeep`，挂在 `IsClaimed` 上）。
                //   放在 candidate 之后判：只有「本来会被我们动手」的窗口才吃这笔凭据，
                //   否则让行期会被一个不相干的事件（比如一个对话框）白白耗掉。
                //   ⚠ 不能只「吃一次凭据就作废」：一扇新窗会先后报 CREATE / SHOW 两条事件，
                //     第一条把凭据吃掉，第二条就没人拦了 —— 用户看到的就是「原生窗开出来一会儿
                //     又被收成标签」（川报的就是这个）。认下窗口本身，它关掉之前谁都别碰。
                if (candidate && NativeOpenPeriod())
                {
                    EmbedApi.MarkNativeKeep(h);
                    Diag.Step(string.Format(
                        "Hub: 让行（用原生资源管理器打开）cab=0x{0:X} —— 这扇窗在关掉之前都不收编",
                        h.ToInt64()));
                    return;
                }
                if (!candidate)
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
                    //   对 shell 的窗口**不能动样式位** —— 不置透明、不摘 APPWINDOW、不 SetParent。
                    //   shell 会预建一些浏览器窗口备着而根本不显示（探针实测到 `vis=0` 的 CabinetWClass），
                    //   而防闪那层透明是在 CREATE 那一刻就上的；那个窗口永远不会 SHOW ⇒ 永远走不到「撕透明」，
                    //   等于在 shell 进程里留了一个**隐形窗口**。Win+E 与开始菜单那条入口只去「激活」
                    //   它自己的窗口、不新建 ⇒ 激活到一个隐形的就是「按下去毫无反应」，而且透明是加在
                    //   shell 的窗口上的，退程序也不恢复。
                    // ★ 但 `SW_HIDE` 不在这条线上（2026-09-25 改）：它**不改任何样式位**、完全可逆，
                    //   而且我们只在「它已经可见」那一刻按下去，收尾必定「关掉它」或「原样 SW_SHOW 放开」
                    //   （见 HushShellWindow / WatchShellWindow 的 finally）——绝不会留隐形窗。
                    //   为什么非按不可：实测那扇窗的 `LocationURL` 要等**它已经可见之后** 90~116ms 才读得出来，
                    //   「先读路径、再关窗」根本来不及 —— 屏幕上实打实露的那一百多毫秒就是川说的那一下闪。
                    //   ⇒ shell 这条路上我们只做三件可逆的事：建窗那一刻清掉绘制区、露脸那一下按住、`SC_CLOSE` 关掉。
                    //
                    // ⚠ 两个动作必须排在**任何写日志之前**：日志是每行 open/write/close 落盘的
                    //   （见 `Diag`），实测那一行能把后面的动作推迟一百多毫秒 —— 而我们抢的就是这几十毫秒。
                    if (!isShow) BlankShellWindow(h);      // CREATE：离 SHOW 还有 ~430ms
                    long hz = Environment.TickCount;
                    bool hushed = (isShow || wasVisible) && HushShellWindow(h);   // 露脸了：按住
                    int hms = Environment.TickCount - (int)hz;
                    // ★ SHOW 之后**再清一次绘制区**，而且排在「按住」后面 —— SHOW 那一刻窗已经画在
                    //   屏幕上了，先把可见性压掉最要紧。这一下兜的是「我们设的空绘制区被谁重置了」：
                    //   `SetWindowRgn` 是别人进程的窗也能设的，但 explorer 自己后续那轮 UI 重建完全可能
                    //   把它顶掉。重设是幂等的（还是空区域，屏幕上一点差别都看不出来）。
                    if (isShow) BlankShellWindow(h);
                    // ⚠ 别用 `GetWindowRgn` 去验证：它对**别的进程**的窗口恒返回 0（实测），
                    //   看上去像「没设上」。我们自己的账（shellBlanked）才是真的；
                    //   「空绘制区确实让出屏幕」是用 `probe/startmenu_timeline_probe.py --grab`
                    //   抓屏幕前后比对证实的（设上之后那块屏差 80 处、基线只差 1 处）。
                    bool blankNow = IsShellBlanked(h);
                    Diag.Step(string.Format(
                        "Hub: 新窗口 -> shell 自己的（{0}，事件后 {1}ms，{2}）cab=0x{3:X}",
                        isShow ? "SHOW" : "CREATE", react,
                        (blankNow ? "绘制区已清空（不画任何东西）" : "绘制区没清掉") +
                        (isShow ? (hushed ? "，已按住（耗时 " + hms + "ms）" : "，按住没生效") : ""),
                        h.ToInt64()));
                    // ★ 另起一条线程去抢时间关窗（见 WatchShellWindow）：贴回 UI 线程做这一步
                    //   在冷启动时会排 400ms 队，而窗口 495ms 就可见了。
                    WatchShellWindow(h, react);
                }
                else if (!wasVisible && HiddenByUs(h))
                {
                    // 已经被前一个回调处理过了。同一次「窗口显示」会被 **N 个** watcher 各报一遍
                    // （每个标签一份 `WinShowWatcher`，外加 Hub 自己那份），而我们排到时常常已经晚了一秒多
                    // （前面十几个回调加上 UI 线程上正在跑的收编一起排队），那时窗口早被藏掉了。
                    // 这一刀不再重复发**跨进程**的 `MakeTransparent` / `ShowWindow`、也不再写日志：
                    // 实测那一串重复的「Hub: 新窗口 -> 藏起来（SHOW，事件后 985~1140ms）」就是这么来的，
                    // 一轮还原能刷出几十行，全压在 UI 线程上 ⇒ 反过来把后面的事件处理堵住一秒多。
                    //
                    // ⚠ 判据必须是「已被我们藏过 **且** 现在不可见」两件一起看：
                    //   只看 `!wasVisible` 会把 CREATE 那次唯一的早机会误杀 —— CREATE 时窗口本来就不可见，
                    //   而那一刻的 `MakeTransparent` 才是「一点不闪」的关键（见 OnWindowShown 类注释①）。
                    //   只看 `HiddenByUs` 也不行：用户手动重新显示那扇窗时它还在集合里，那一下必须再藏。
                }
                else
                {
                    // ⚠ SHOW 那一下**先按下去、再补透明兜底**：那扇窗从「可见」到我们动手之间隔着
                    //   一次事件投递 + 两三次跨进程调用 —— 用户看到的就是这一段（实测同一台机器上
                    //   0ms ~ 47ms 不等，UI 线程一忙就往 47ms 靠）。`SW_HIDE` 才是止闪的那一下，
                    //   所以它排最前；顺序反一下等于白晾几十毫秒出来（用户报的「原生资源管理器闪一下」）。
                    bool hid;
                    if (isShow)
                    {
                        hid = EmbedApi.ShowWindow(h, EmbedApi.SW_HIDE);
                        EmbedApi.MakeTransparent(h, true);
                    }
                    else
                    {
                        // CREATE 那一次反过来：它还没显示，`SW_HIDE` 本来就按不着，
                        // 真正管用的是先置透明（见 OnWindowShown 类注释①）。
                        // ⚠ 这一次**不能**打「不进任务栏」那两位 —— explorer 见着它就**不建 Ribbon**、
                        //   退回老式菜单栏（内嵌窗口顶上那条白条，见 MakeTransparent）；
                        //   可等 SHOW 事件到了才补又太晚（任务栏已经按「可见那一帧」加了按钮、图标闪一下）。
                        //   中间那段时机交给 ScheduleTaskbarBits：Ribbon 一建出来就补（见那边的实测时间线）。
                        EmbedApi.MakeTransparent(h, false);
                        EmbedApi.ScheduleTaskbarBits(h);
                        hid = EmbedApi.ShowWindow(h, EmbedApi.SW_HIDE);
                    }
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
            // ★ 此刻没有标签在起 ⇒ 不用等那一跳 250ms 的定时器，当场收（`IsCapturable` 那套
            //   已经判过它确实是我们该收的窗口）。实测省下约 0.25 秒 —— 从开始菜单打开的那扇窗，
            //   早一拍收下去就早一拍出内容。
            if (!AnyLaunchInFlight()) DrainCapture();
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

        /// <summary>
        /// 在**专用线程**上盯 shell 刚开的那扇窗：路径一出来就立刻把它关掉（点 × 走的就是这条），
        /// 抢在它画到屏幕上之前。
        ///
        /// 为什么必须另开一条线程（实测 2026-09-25）：
        ///   · shell 那扇窗 `CREATE +16ms` 我们就知道了（watcher 线程），但「读路径 + 关窗」原来
        ///     是 `Post` 回 UI 线程做的 —— **冷启动那一趟 UI 线程正忙着建窗口 / 读书签**，
        ///     实测这一 Post 排了 **400ms** 才轮到，而窗口在 `CREATE +495ms` 就已经可见了：
        ///     用户看到的还是「闪一下」（实测那一段可见 169ms）。
        ///   · 读路径本身现在不贵也不阻塞（`ShellBrowserReg.PathOfWindow` 从队尾倒扫，
        ///     一次约 10ms），放在这条线程上每 25ms 问一次很划算。
        ///
        /// 关闭用 `WM_SYSCOMMAND`/`SC_CLOSE`，**一个字节的样式都不碰** —— 这条红线见 `OnWindowShown` 里那段教训。
        ///
        /// ⚠ 不闪靠的**不是**这条线程：真正管用的是建窗那一刻清掉绘制区（`BlankShellWindow`）。
        ///   实测 `LocationURL` 要到**它已经可见之后** 90~116ms 才读得出来，而 `SHOW` 之后的同步
        ///   窗口操作（`SW_HIDE` 实测花了 109ms）都赶不上那一帧。这里只负责：**一露脸就按住**
        ///   （把任务栏按钮也收掉）、等路径、`SC_CLOSE`、把路径存进 `shellReady`。
        ///
        /// ⚠ 收尾必做（`finally` 里那句）：只要我们按过它，就必须让它**要么没了、要么恢复可见** ——
        ///   在 shell 进程里留一扇隐形窗就是「Win+E / 开始菜单按下去毫无反应」那个坑
        ///   （见 `OnWindowShown` 里那段教训）。
        ///
        /// ⚠ 这条线程**只负责按住 + 关窗 + 把路径存进 `shellReady`**，不做转生：转生依旧排回 UI 线程
        ///   （`RegisterShellCandidate` / `DrainShell`），那两个地方动手前会先看 `shellReady`。
        ///   这样「关窗」和「开标签」两条道上不会各自开一个标签，也不会让用户那个文件夹凭空消失。
        ///
        /// ⚠ **绘制区要等它真没了才还**（`finally` 里那段，2026-09-25 实测补上的一层）：
        ///   先前是「一读到路径就还」—— 那一下正好落在 shell 把窗 `ShowWindow` 出来之前，
        ///   于是它一露脸就是**有绘制区**的，实打实画了 ~110ms（我们那一下 `SW_HIDE` 同步落在
        ///   explorer 正忙的线程上，实测就要 110ms）。这就是用户报的「从开始菜单点文件夹还是会闪」
        ///   剩下的那一层：清空绘制区只负责到「关掉它」为止。
        ///
        /// ⚠ **关窗要等它「落定」**（`ShellSettleMs`，2026-09-25 实测定案）：shell 那扇窗刚开出来
        ///   就被我们 `SC_CLOSE` 抽掉，会让系统在**关窗后约 0.6s**发出一声提示音（`pid=0` 的
        ///   «System Sounds» 会话，用户报的「咚」）。用音频会话探头按出声率量：
        ///   读到路径即关 = 21 次里响 9 次；**推迟 800ms 再关 = 16 次里响 0 次**。
        ///   同一轮里「换 `WM_CLOSE`」「不 `SW_HIDE`」都照样响 ⇒ 与关法的种类无关，**只与关得太早有关**。
        ///   ⚠ 推迟期间**必须继续轮询 + 继续按住**（所以不是一句 `Sleep`）：shell 有把窗再显出来的路径，
        ///     不接着按就会让它重新露脸（那正是当初要按它的原因）。
        /// </summary>
        private void WatchShellWindow(IntPtr h, int react)
        {
            lock (shellWatched) { if (!shellWatched.Add(h)) return; }
            Thread t = new Thread(delegate()
            {
                bool hushed = false;
                bool asked = false;          // 已经发过 `SC_CLOSE`
                string picked = null;        // 问出来的「用户要的是哪一个」（见 shellSelected）
                try
                {
                    DateTime deadline = DateTime.Now.AddMilliseconds(ShellResolveMs);
                    int closeAt = 0;         // 0 = 还没读到路径；否则 = 「落定」之后再关的时刻（TickCount）
                    string path = null;
                    while (true)
                    {
                        if (quitting) return;
                        if (!NativeMethods.IsWindow(h)) return;      // 它自己没了（用户关了）
                        // 它露脸了（或 shell 又把它显出来）：按住。见 HushShellWindow。
                        // ⚠ 这一句在「等它落定」那段时间里也得一直跑 —— 不接着按，shell 会把它再显出来。
                        if (HushShellWindow(h) && !hushed)
                        {
                            hushed = true;
                            Diag.Step(string.Format("Hub: 盯梢中把 shell 那扇窗按住 cab=0x{0:X}", h.ToInt64()));
                        }
                        if (closeAt == 0)
                        {
                            string p = ShellBrowserReg.PathOfWindow(h);
                            if (p != null)
                            {
                                // ★ 路径先交出去（UI 线程这就去开标签）—— 推迟关窗**不会**拖慢标签。
                                path = p;
                                lock (shellReady) shellReady[h] = new ShellCandidate { Path = p, ReactMs = react };
                                closeAt = Environment.TickCount + ShellSettleMs;
                            }
                            else if (DateTime.Now >= deadline) break;   // 等不到路径：放开它，当没这回事
                        }
                        else
                        {
                            // ★ 顺手把「用户要的是哪一个」问出来。
                            //   三方（wb / VS Code / 各种「在资源管理器中显示」）点开一个文件夹，走的其实是
                            //   `explorer /select,"<目标>"` —— 原生那一下是**父目录 + 把目标选中**；
                            //   我们只把父目录开成标签，这个意图就丢了（用户报的「没有选中指定目录」）。
                            //   而这段窗口期（`ShellSettleMs`）是**唯一**问得到的机会：窗口一关，
                            //   `IShellWindows` 里那一项就没了。所以放在这条线程上 —— 它 25ms 一拍、
                            //   不受 UI 线程忙不忙影响（那正是 UI 线程那条路会错过的地方）。
                            if (picked == null)
                            {
                                string sel = ShellBrowserReg.SelectedPathOfWindow(h);
                                // 只认「它的父目录正是这扇窗开着的目录」的那些：shell 换过目录、或者
                                // 只有焦点没有选中时报回来的东西（比如目录自己）一律不算。
                                if (sel != null && PathRules.Same(PathRules.ParentOf(sel), path))
                                {
                                    picked = sel;
                                    lock (shellSelected)
                                        shellSelected[h] = new ShellCandidate { Path = sel, SeenAt = DateTime.Now };
                                    Diag.Step(string.Format(
                                        "Hub: 盯梢中问出三方要的是选中 {0} cab=0x{1:X}", sel, h.ToInt64()));
                                }
                            }
                            if (Environment.TickCount >= closeAt)
                            {
                                EmbedApi.PostMessageW(h, 0x0112, (IntPtr)0xF060, IntPtr.Zero);   // WM_SYSCOMMAND / SC_CLOSE
                                Diag.Step(string.Format(
                                    "Hub: 关掉 shell 那扇窗（当时{0}）cab=0x{1:X} -> {2}",
                                    hushed ? "被我们按着" : "还没显示", h.ToInt64(), path));
                                asked = true;
                                break;
                            }
                        }
                        Thread.Sleep(25);
                    }
                }
                catch (Exception ex) { Diag.Log("Hub: shell 盯梢线程失败 " + ex.Message); }
                finally
                {
                    // ★ 发过关窗请求就等它**真没了**再走（上限 2 秒）：`SC_CLOSE` 只是「请求」，
                    //   而它可见的那一段里绘制区还得继续压着（见本方法上面那段教训）。
                    if (asked)
                    {
                        DateTime bye = DateTime.Now.AddMilliseconds(2000);
                        while (DateTime.Now < bye && NativeMethods.IsWindow(h)) Thread.Sleep(50);
                    }
                    // 收尾必做：到这儿它要是还活着，就说明我们没关掉它 —— 那它必须是一扇
                    // **正常可见的窗**，绝不能是被我们按住 / 被我们清空绘制区的状态
                    //（在 shell 进程里留隐形窗 = v1.13.1 那个「Win+E 按下去毫无反应」，见 `OnWindowShown`）。
                    // ⚠ 但先验明正身：上面那 2 秒等待足够让这个句柄被系统回收给别人，
                    //   那样下面这两句就砸在一扇无辜的窗上了（见 `StillShellBrowser`）。
                    if (StillShellBrowser(h))
                    {
                        UnblankShellWindow(h);
                        UnhushShellWindow(h, asked ? "关窗请求没被理" : "等不到路径");
                    }
                    else
                    {
                        ForgetBlanked(h);
                        UnmarkHidden(h);
                    }
                    lock (shellWatched) shellWatched.Remove(h);
                }
            });
            t.IsBackground = true;
            t.Name = "TBE-shell窗口盯梢";
            // ★ 必须是 STA：这条线程现在要问「它选中了什么」，那一路要走 `IShellWindows` 的自动化
            //   （`ShellBrowserReg.SelectedPathOfWindow`），而它里面 `Application.OleRequired()`
            //   在 MTA 线程上会直接抛 —— `new Thread` 默认就是 MTA。
            try { t.SetApartmentState(ApartmentState.STA); } catch { }
            t.Start();
        }

        /// <summary>取走盯梢线程问出来的选中项（取了就清）。见 <see cref="shellSelected"/>。</summary>
        private ShellCandidate TakeShellSelected(IntPtr h)
        {
            lock (shellSelected)
            {
                ShellCandidate c;
                if (!shellSelected.TryGetValue(h, out c)) return null;
                shellSelected.Remove(h);
                return c;
            }
        }

        /// <summary>
        /// 把 shell 那扇**刚建出来、还没显示**的窗变成「画不出任何东西」的（清空绘制区）。
        ///
        /// 为什么非得在 CREATE 做（实测 2026-09-25）：
        ///   · 那扇窗 `SHOW +0ms` 就可见了，而它的目标路径要到 `SHOW +116ms` 才读得出来；
        ///   · 退一步「一露脸就 `SW_HIDE`」也不行 —— 跨进程 `ShowWindow` 实测花了 **109ms**
        ///     （同步调用落在 explorer 正忙的线程上，得等它回到消息循环），我们还是露了 118ms。
        ///   ⇒ `SHOW` 之后的任何同步窗口操作都赶不上那一帧；只有 CREATE 那一刻
        ///     （离 SHOW 还有 ~430ms）来得及。
        ///
        /// ⚠ 这不是样式位：`SetWindowRgn` 只改绘制区，一句 `SetWindowRgn(h, NULL)` 就还原 ——
        ///   和那条红线（不置透明 / 不改 exstyle / 不 SetParent）不是一回事。
        ///   收尾必做：关掉它，或者 `UnblankShellWindow` 还原（见 `WatchShellWindow` 的 finally）。
        /// </summary>
        private bool BlankShellWindow(IntPtr h)
        {
            if (h == IntPtr.Zero) return false;
            // ★ 非客户区要**单独**按住：`SetWindowRgn` 管不到 DWM 画出来的标题栏/右上角那三个按钮
            //   （2026-10-02 对照实验坐实）。它跟绘制区是两码事，所以**不管 MakeBlank 成不成都要做**。
            EmbedApi.BlankNonClient(h);
            bool ok = EmbedApi.MakeBlank(h);
            lock (shellBlanked) shellBlanked.Add(h);      // 记账改成「我们动过这扇窗」的记号（供还原）
            return ok;
        }

        /// <summary>去掉我们加在 shell 窗口上的绘制区限制和非客户区按住（没加过 / 窗已经没了就什么都不做）。</summary>
        private void UnblankShellWindow(IntPtr h)
        {
            lock (shellBlanked) { if (!shellBlanked.Remove(h)) return; }
            EmbedApi.Unblank(h);
            EmbedApi.RestoreNonClient(h);
        }

        /// <summary>只把「这扇窗被我清过绘制区」的账抹掉，**一个字节都不往那扇窗上写**。
        /// 什么时候用：句柄可能已经被系统回收给别人了（见 `StillShellBrowser`）。</summary>
        private void ForgetBlanked(IntPtr h)
        {
            lock (shellBlanked) shellBlanked.Remove(h);
        }

        /// <summary>
        /// 这个句柄**还是不是**我们当初那扇 shell 浏览窗。
        ///
        /// 为什么必须有：**hwnd 会被系统飞快复用**（实测 2026-10-01 22:36:30.680 关掉 `0x240D78`，
        /// 222ms 后同一个值已经戴在一扇新窗头上），而盯梢线程发出关窗请求后还要等最多 2 秒才收尾 ——
        /// 这段时间足够那个句柄改姓。收尾那两句（还绘制区 / 恢复显示）都是**会改动别人窗口状态**的：
        /// 砸在一扇无辜的窗上就是「它平白被抠掉一块绘制区」或「一扇本来藏着的窗被我们 `SW_SHOW` 出来」。
        /// 所以动手之前先验明正身。
        /// </summary>
        private static bool StillShellBrowser(IntPtr h)
        {
            if (h == IntPtr.Zero || !NativeMethods.IsWindow(h)) return false;
            string c = EmbedApi.ClassOf(h);
            if (c != "CabinetWClass" && c != "ExploreWClass") return false;
            if (EmbedApi.GetParent(h) != IntPtr.Zero) return false;      // 已经被谁嵌走了
            return EmbedApi.IsShellOwned(h);
        }

        /// <summary>这扇 shell 窗口的绘制区现在是不是被我们清空的（只管我们自己记的账）。</summary>
        private bool IsShellBlanked(IntPtr h)
        {
            lock (shellBlanked) return shellBlanked.Contains(h);
        }

        /// <summary>
        /// 把 shell 那扇**已经可见**的窗当场按下去（`SW_HIDE`）。
        ///
        /// 为什么值当（实测 2026-09-25）：那扇窗从「可见」到「能读出 `LocationURL`」之间隔着
        /// 90~116ms，而它可见的那一段里任务栏会多出一个按钮。绘制区已被清空（`BlankShellWindow`），
        /// 屏幕上本来就不画东西；这里再把**任务栏按钮**一并收掉。
        ///
        /// ⚠ 只按已经可见的窗：shell 那批「建出来备着、从不显示」的窗我们碰都不碰。
        ///   `ShowWindow` 的返回值正好就是「它原来可不可见」—— 返回 false 说明它本来就藏着（我们什么都没做）。
        /// ⚠ 这是**可逆**动作，和那条红线（不置透明 / 不改扩展样式位）不是一回事：
        ///   样式位改了会永久留在 shell 进程里（退程序都不恢复），而 `SW_HIDE` 一次 `SW_SHOW` 就回去了。
        ///   所有按下去的路径都保证收尾（关掉 or 放开），见 `WatchShellWindow` 的 finally 与 `UnhushShellWindow`。
        /// </summary>
        /// <returns>这一下真按到了（它之前是可见的）</returns>
        private bool HushShellWindow(IntPtr h)
        {
            if (h == IntPtr.Zero) return false;
            try
            {
                if (!EmbedApi.IsWindowVisible(h)) return false;
                return EmbedApi.ShowWindow(h, EmbedApi.SW_HIDE);
            }
            catch (Exception ex) { Diag.Log("Hub: 按住 shell 窗口失败 " + ex.Message); return false; }
        }

        /// <summary>
        /// 放开被按住的 shell 窗口（它已经没了 / 已经自己又显出来，就什么都不做）。
        ///
        /// 收尾必做，而且是**无条件**叫一次（见 `WatchShellWindow` 的 `finally`）：按住它的一共两条道
        /// （盯梢线程、UI 线程的 `OnWindowShown`），谁按的都算 —— 只要它还活着、还是隐形的，
        /// 就必须恢复可见。在 shell 进程里留一扇隐形窗就是 v1.13.1 那个
        /// 「Win+E / 开始菜单按下去毫无反应」，而那条路只有重启电脑能出来。
        /// </summary>
        private void UnhushShellWindow(IntPtr h, string why)
        {
            try
            {
                if (!NativeMethods.IsWindow(h)) return;
                if (EmbedApi.IsWindowVisible(h)) return;
                EmbedApi.ShowWindow(h, EmbedApi.SW_SHOW);
                Diag.Log("Hub: shell 那扇窗没能关掉（" + why + "），已恢复显示 —— 不留隐形窗");
            }
            catch (Exception ex) { Diag.Log("Hub: 恢复 shell 窗口失败 " + ex.Message); }
        }

        /// <summary>取走盯梢线程交上来的路径（取了就清）。见 <see cref="WatchShellWindow"/>。</summary>
        private ShellCandidate TakeShellReady(IntPtr h)
        {
            lock (shellReady)
            {
                ShellCandidate c;
                if (!shellReady.TryGetValue(h, out c)) return null;
                shellReady.Remove(h);
                return c;
            }
        }

        /// <summary>登记一个「等地址栏」的 shell 窗口（UI 线程）。</summary>
        private void RegisterShellCandidate(IntPtr h, int react)
        {
            if (quitting || !Settings.CaptureAll || !Settings.CaptureShell) return;
            // ★ 先兑现盯梢线程抢出来的路径：它可能**已经把窗关掉了**，所以这句必须排在 `IsWindow` 前面 ——
            //   窗口没了不代表这事完了，用户要的那个标签还没开。
            ShellCandidate fast = TakeShellReady(h);
            if (fast != null)
            {
                Diag.Step(string.Format("Hub: shell 窗口路径就绪（盯梢线程抢的，当时{0}）cab=0x{1:X} -> {2}",
                    EmbedApi.IsWindowVisible(h) ? "已可见" : "还没显示", h.ToInt64(), fast.Path));
                TakeOverShellWindow(h, fast.Path, fast.ReactMs);
                return;
            }
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
            // ★ 先问 shell 自己的浏览器清单（`ShellWindows.LocationURL`）：这条在**建窗那一刻**
            //   就有答案，而地址栏得等窗口把那一排工具条建出来。多这一问，就是为了在那扇窗
            //   露脸**之前**把它 SC_CLOSE 掉 —— 用户报的「从开始菜单点文件夹还是会闪」就是
            //   只读地址栏造成的（实测地址栏就绪时它早就可见了）。
            string now = ShellBrowserReg.PathOfWindow(h);
            if (now == null) now = EmbedApi.AddressPathOf(h, true);
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
                // ★ 先看盯梢线程有没有把路径抢出来 —— 它可能**已经把窗关掉了**，所以这句必须排在
                //   `IsWindow` 前面（窗口没了不代表不转生了，用户要的那个标签还没开）。
                ShellCandidate fast = TakeShellReady(h);
                if (fast != null)
                {
                    pendingShell.Remove(h);
                    Diag.Step(string.Format("Hub: shell 窗口路径就绪（盯梢线程抢的，当时{0}）cab=0x{1:X} -> {2}",
                        EmbedApi.IsWindowVisible(h) ? "已可见" : "还没显示", h.ToInt64(), fast.Path));
                    TakeOverShellWindow(h, fast.Path, fast.ReactMs);
                    continue;
                }
                if (!NativeMethods.IsWindow(h))
                {
                    pendingShell.Remove(h);
                    continue;                                 // 它自己没了（被关掉）——没什么可做的
                }
                // force：别吃「这个窗口还没有地址栏」那条 1.2 秒的负缓存 —— 我们正 250ms 一轮
                // 盯着它看，而超时也是 1.2 秒，缓存一命中就注定读不出来（见 AddressPathOf）。
                // 先问清单（快、建窗即有），再退回地址栏。
                string path = ShellBrowserReg.PathOfWindow(h);
                if (path == null) path = EmbedApi.AddressPathOf(h, true);
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
                    "Hub: shell 窗口路径就绪（登记后 {0}ms，当时{1}）cab=0x{2:X} -> {3}",
                    (int)(now - kv.Value.SeenAt).TotalMilliseconds,
                    EmbedApi.IsWindowVisible(h) ? "已可见" : "还没显示", h.ToInt64(), path));
                TakeOverShellWindow(h, path, kv.Value.ReactMs);
            }
        }

        /// <summary>
        /// 「转生」：shell 进程开的文件夹窗口不归我们（不能 SetParent、也不能改它的样式），但它的目标是我们的。
        /// 读出目标目录 → 把它关掉 → 用我们自己的 `explorer /n,/separate` 把同一个目录开成标签。
        /// 这样既拿到了标签，又没碰过它的窗口对象 —— v1.13.1 那个「Win+E 按下去没反应」不会发生。
        ///
        /// 关它用 `WM_SYSCOMMAND`/`SC_CLOSE`（点 × 走的就是这条），不用手写 `WM_CLOSE` ——
        /// 关一个**别人的**窗口，越接近正常操作越好（shell 那边可能还有它自己的账要结）。
        /// ⚠ **这里通常不发它**：那扇窗正被 <see cref="WatchShellWindow"/> 盯着，它自己会在
        ///   「读到路径 + `ShellSettleMs`」那一刻关（那条实测不出声；这里再补一刀就变成**重复关**，
        ///   关完约 0.6s 会响一声「咚」，见 `ShellSettleMs` 的注释）。只有没人在盯的时候才在这里兜底关。
        ///   顺带，那 800ms 也正好是 `/select,` 把「选中目标」落实下来的窗口期 —— 见 <see cref="ScheduleReveal"/>。
        ///
        /// ⚠ 我们**没有**给它置过透明 / 动过样式位（见 OnWindowShown 里那段教训）——只可能清过它的
        ///   绘制区、`SW_HIDE` 按过它一下；两者都有自己的收尾（`WatchShellWindow` 的 finally）。
        ///   它要是没关掉，也是留在屏幕上的一个**正常窗口**，不是隐形窗口。
        /// </summary>
        private void TakeOverShellWindow(IntPtr h, string path, int react)
        {
            // ★ 同一条命令会被报好几遍（CREATE / SHOW / 盯梢线程交路径），每一遍都能走到这儿。
            //   不挡就是同一个文件夹开两个标签 + 多起一个 explorer，还会把用户要点开的那扇窗的
            //   SHOW 事件顶后几十毫秒（＝那一下闪）。见 `shellTaken` 的注释。
            lock (shellTaken)
            {
                int tick0 = Environment.TickCount;
                List<IntPtr> stale = null;
                foreach (KeyValuePair<IntPtr, int> kv in shellTaken)
                {
                    if (tick0 - kv.Value >= TakenRememberMs)
                    {
                        if (stale == null) stale = new List<IntPtr>();
                        stale.Add(kv.Key);
                    }
                }
                if (stale != null) foreach (IntPtr k in stale) shellTaken.Remove(k);
                if (shellTaken.ContainsKey(h))
                {
                    Diag.Step(string.Format(
                        "Hub: 这扇 shell 窗已经转生过了，跳过（别再多开一个标签）cab=0x{0:X} -> {1}", h.ToInt64(), path));
                    return;
                }
                shellTaken[h] = tick0;
            }
            try
            {
                Guid d = VirtualDesktop.WindowDesktopId(h);
                if (d == Guid.Empty) d = VirtualDesktop.CurrentDesktopId();
                Diag.Step(string.Format(
                    "Hub: shell 开的文件夹 -> {0}（pid={1}，反应 {2}ms）=> 关掉它、用自己的标签重开",
                    path, EmbedApi.ProcessIdOf(h).ToInt32(), react));
                bool watched;
                lock (shellWatched) watched = shellWatched.Contains(h);
                if (!watched)
                {
                    EmbedApi.PostMessageW(h, 0x0112, (IntPtr)0xF060, IntPtr.Zero);  // WM_SYSCOMMAND / SC_CLOSE
                }
                else
                {
                    Diag.Step("Hub: 那扇 shell 窗有盯梢线程在，关窗交给它（顺带给 /select 留出落定时间）");
                }
                EmbedForm f = EnsureForm(d);
                if (f == null) { Diag.Log("Hub: shell 窗口转生失败：没有可用的窗口"); return; }
                // 这扇窗是**为了外面那个文件夹**才现建出来的：先把本桌面记着的标签摆回来，再加上它。
                // 少了这一步，用户原来那一整排标签会被「只有这一个」的窗盖掉（退出时还会存进去）。
                // ⚠ 记忆里那批**只摆名字**：用户要的这个得第一个起（它在 `OpenPathAsTab` 里进队、
                //   排到队头），其余等它落定再排（见 StartRestLaunches）。原来那顺序
                //   「先把 8 个全摆上、全塞进队，再 OpenPathAsTab」等于把它排在第 8 位，
                //   要等 7 个 explorer 起完才轮到 —— 转生一条路实测 6 秒以上。
                int staged = f.StageRememberedTabs();
                Diag.Step(string.Format(
                    "Hub: 转生目标窗口 桌面={0} 摆上 {1} 个记忆标签 当时标签={2} 可见={3} 当前标签={4}",
                    d, staged, f.TabCount, f.Visible, f.ActiveIdx));
                f.OpenPathAsTab(path);
                f.StartRestLaunches(path, true);   // 用户在等这一个：其余等它落定再排
                Diag.Step(string.Format("Hub: 转生完成 标签={0} 当前标签={1}", f.TabCount, f.ActiveIdx));
                // ★ 三方的「打开文件夹」多半是 `/select,` 语义（父目录 + 选中目标）：目录开出来了，
                //   可用户要的是「那个东西被选中」。趁那扇 shell 窗还没被关掉，把它选中了什么问出来、
                //   再摆进我们自己的标签里（见 ScheduleReveal）。
                ScheduleReveal(h, path);
                // 这一步不能省：用户是在**别的程序**里点的「打开文件夹」，本该有一扇窗弹到最前面。
                // 只 `OpenPathAsTab` 的话窗口只是被 `Show()` 出来、还压在那个程序后面，
                // 用户看到的就只有「原生窗闪了一下 + 任务栏图标闪」= 像什么都没发生。
                f.ShowForCapture();
            }
            catch (Exception ex) { Diag.Log("Hub: shell 窗口转生失败 " + ex.Message); }
        }

        /// <summary>
        /// 记下一件「把选中项摆回去」的活儿（见 <see cref="DrainReveal"/>）。
        ///
        /// 什么时候需要它：用户在**别的程序**里点「打开文件夹」时，系统走的是
        /// `explorer /select,"&lt;目标&gt;"` —— 造出来的是「父目录的窗 + 目标被选中」。
        /// 我们读到的是**父目录**（`ShellBrowserReg.PathOfWindow` 报的是地址栏那个），
        /// 于是只开出一个「父目录」标签，选中项整个丢了。
        /// 用户看到的差别：原生是「到了那个目录、那个文件夹高亮着」，我们这边是「到了那个目录」。
        ///
        /// 为什么不在转生时直接开 `explorer /n,/select,"&lt;目标&gt;"`：那一刻我们还**不知道**目标是谁
        /// （`/select` 是 shell 导航完之后才落实的，读路径那一瞬读它必为空），而且实测用户点开的父目录
        /// **多半已经开着标签**（日志：`当时标签=9 … 转生完成 标签=9`，一个 explorer 都没起）——
        /// 那种情况下连窗口都没有，只能把选中项**摆进已有的标签**里。
        /// </summary>
        private void ScheduleReveal(IntPtr shell, string folder)
        {
            if (shell == IntPtr.Zero || string.IsNullOrEmpty(folder)) return;
            // 顺手清掉没人来取的（那扇窗的转生被 `shellTaken` 去重挡了 / 早就过期了）
            lock (shellSelected)
            {
                List<IntPtr> gone = null;
                foreach (KeyValuePair<IntPtr, ShellCandidate> kv in shellSelected)
                {
                    if ((DateTime.Now - kv.Value.SeenAt).TotalMilliseconds >= TakenRememberMs)
                    {
                        if (gone == null) gone = new List<IntPtr>();
                        gone.Add(kv.Key);
                    }
                }
                if (gone != null) foreach (IntPtr k in gone) shellSelected.Remove(k);
            }
            lock (reveals)
            {
                for (int i = 0; i < reveals.Count; i++)
                {
                    if (reveals[i].Shell == shell) return;      // 同一扇窗那条已经在了（CREATE/SHOW 各来一次）
                }
                reveals.Add(new RevealTask
                {
                    Shell = shell,
                    Folder = folder,
                    Started = Environment.TickCount
                });
            }
            Diag.Step(string.Format("Hub: 记下「要把选中项摆回去」 {0}（那扇 shell 窗 cab=0x{1:X}）",
                folder, shell.ToInt64()));
            if (revealTimer == null)
            {
                revealTimer = new System.Threading.Timer(
                    delegate { Post(delegate { DrainReveal(); }); }, null, RevealTickMs, RevealTickMs);
            }
            else
            {
                revealTimer.Change(RevealTickMs, RevealTickMs);
            }
        }

        /// <summary>
        /// 摆选中项的节拍（`RevealTickMs` 一拍，跑在 UI 线程上）。分两段：
        ///
        /// ① **问**：那扇 shell 窗还活着的那几百毫秒里，问它「你现在选中了什么」
        ///    （`ShellBrowserReg.SelectedPathOfWindow`，按 HWND 找它 —— 它还是顶层窗，句柄对得上）。
        ///    只认「它的父目录＝我们开的这个目录」的那些选中项：shell 换过目录、或者只有焦点没有选中时
        ///    报回来的东西（比如目录自己）一律不算，宁可退化成「就是打开这个目录」。
        /// ② **摆**：把问出来的目标摆进**我们自己**的标签里（`ShellBrowserReg.SelectItemInWindow`）。
        ///    这是个重试：新建的标签要等 explorer 真把目录列出来才认得 `ParseName`（实测 1.5~5 秒）。
        ///
        /// ⚠ 两段都有上限（`RevealAskMs` / `RevealApplyMs`），到点就把单子丢掉 —— 这是个**锦上添花**的
        ///   功能，任何一段失败都退化成「功能没实现前的老样子」，绝不能反过来影响开标签本身。
        /// </summary>
        private void DrainReveal()
        {
            int now = Environment.TickCount;
            lock (reveals)
            {
                if (reveals.Count == 0) { StopRevealTimer(); return; }
                for (int i = reveals.Count - 1; i >= 0; i--)
                {
                    RevealTask t = reveals[i];

                    // ---- 第一段：等盯梢线程把「用户要的是哪一个」交上来 ----
                    //  ⚠ 这里**不再自己问**：问它的机会只有那 800ms，而这段节拍跑在 UI 线程上，
                    //    转生那一刻会被收编 / 起 explorer 顶到一秒开外 —— 等它跑起来窗口早没了
                    //    （实测栽过一次）。改成盯梢线程自己问、存进 shellSelected（见那边的注释）。
                    if (t.Item == null)
                    {
                        ShellCandidate got = TakeShellSelected(t.Shell);
                        if (got != null)
                        {
                            t.Item = got.Path;
                            t.NextApply = now;
                            Diag.Step("Hub: 三方要的其实是选中 " + got.Path +
                                      "（它的父目录正是我们开的 " + t.Folder + "）");
                        }
                        else if (now - t.Started >= RevealAskMs)
                        {
                            Diag.Step("Hub: 没问出选中项（就是打开这个目录，没选中别的东西）-> 不摆 " + t.Folder);
                            reveals.RemoveAt(i);
                        }
                        continue;
                    }

                    // ---- 第二段：摆进我们自己的标签里 ----
                    if (now < t.NextApply) continue;
                    // ⚠ 第三个形参是**要排掉的那扇窗**：就是刚把用户引过来的 shell 自己的窗。
                    //   它开着同一个目录、而且 `/select,` 本来就把它选中了 —— 不排掉的话，标签还在加载
                    //   那几百毫秒里它是唯一匹配，摆上去「核对还能通过」，然后它被关掉、选中一起没
                    //   （实测 2026-10-01：摆完 272ms 后那扇窗就被关了，用户什么也没看到）。
                        if (ShellBrowserReg.SelectItemInWindow(t.Folder, t.Item, t.Shell))
                        {
                            Diag.Step("Hub: 已把选中项摆回去 " + t.Item);
                            // ★ 摆完还得把**键盘焦点**交过去。原生那一下之所以紧接着就能上下左右挪、
                            //   用 Enter 进去，靠的是文件列表拿着键盘焦点；只画个高亮是白画
                            //   （用户报的「原生自动选中文件夹后，我可以上下左右或者 enter 进去，你这个不行」）。
                            TryFocusRevealed(t.Folder, 0);
                            reveals.RemoveAt(i);
                            continue;
                        }
                    if (now - t.Started >= RevealApplyMs)
                    {
                        Diag.Step("Hub: 摆选中项超时，放弃 " + t.Item);
                        reveals.RemoveAt(i);
                        continue;
                    }
                    t.NextApply = now + RevealApplyGapMs;      // 标签可能还没加载完，下一拍再来
                }
                if (reveals.Count == 0) StopRevealTimer();
            }
        }

        /// <summary>
        /// 把键盘焦点交给「正开在 <paramref name="folder"/> 这个目录上」的那个标签
        /// （实现在 `EmbedForm.FocusFolder`，那边带两条防抢焦点的闸）。
        /// 返回有没有真交出去。
        /// </summary>
        private bool FocusRevealedTab(string folder)
        {
            foreach (EmbedForm f in AllForms())
            {
                try { if (f.FocusFolder(folder)) return true; }
                catch (Exception ex) { Diag.Log("Hub: 交键盘焦点失败 " + ex.Message); }
            }
            return false;
        }

        /// <summary>
        /// 补交键盘焦点（见 <see cref="FocusRevealedTab"/>）。
        ///
        /// 为什么要补（2026-10-01 实测，用户报「上下左右只生效了一下」）：
        ///   · 摆选中项是**每一拍都在试**的，而焦点只在成功那一拍交一次 —— 可那一刻标签常常
        ///     **还没把新目录认下来**（`ExplorerHost` 的地址栏是轮询读的，比外壳窗口晚约 0.9 秒），
        ///     `FocusFolder` 找不到那个标签就静默失败；等下一拍路径对上了，却已经没人再交焦点；
        ///   · 另外 shell 那扇窗要过几百毫秒才真被关掉，它一消失，系统可能把前台 / 焦点重新分配一次。
        /// 所以成功之后再补一拍「确认」，失败就接着试 —— 上限 3 拍、合计约 1.5 秒。
        /// 让路条件只有一个：这会儿我们不是前台（用户已经点回别的程序了，别把人拽回来）。
        /// </summary>
        private void TryFocusRevealed(string folder, int round)
        {
            if (quitting) return;
            bool ok = FocusRevealedTab(folder);
            if (ok)
                Diag.Step(round == 0
                    ? "Hub: 键盘焦点已交给那个标签（接着就能上下左右 / Enter）"
                    : "Hub: 键盘焦点补交成功（第 " + round + " 拍）");
            if (round >= FocusRetryRounds)
            {
                if (!ok) Diag.Step("Hub: 键盘焦点一直没交上（试到第 " + round + " 拍）：" + folder);
                return;
            }
            System.Threading.Timer t = null;
            t = new System.Threading.Timer(delegate(object o)
            {
                try { t.Dispose(); } catch { }
                Post(delegate { TryFocusRevealed(folder, round + 1); });
            }, null, FocusRetryMs, System.Threading.Timeout.Infinite);
        }

        /// <summary>摆选中项那批活儿都完了 —— 停掉节拍器（下次再有活儿 `Change` 把它叫醒）。</summary>
        private void StopRevealTimer()
        {
            if (revealTimer == null) return;
            try { revealTimer.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
        }

        /// <summary>
        /// 「用原生资源管理器打开」—— 把 path 交给系统的资源管理器开一扇**独立的原生窗口**。
        ///
        /// 为什么需要它：我们默认「只要是新冒出来的文件夹窗口就收成标签」（`Settings.CaptureAll`），
        /// 所以直接 `Process.Start("explorer.exe", path)` 的话，那扇窗会在零点几秒后被自己收编 ——
        /// 用户右键点一下就为了看一眼原生的样子，结果什么都没看到。
        /// 所以先开一个短暂的「让行期」（见 `nativeOpenUntilTick`），这段时间里新出现的候选窗口
        /// 一律不藏、不登记、不转生，原样留在桌面上。
        ///
        /// 让行期是**按时间**算的（`NativeOpenMs`）：这几秒里冒出来的候选窗口一律当他的原生窗认下
        /// （见 `EmbedApi.MarkNativeKeep`）—— 认下之后那扇窗在关掉之前都不会再被收编。
        /// 早先写的是「等到一扇就作废」，结果一扇新窗会先后报 CREATE / SHOW 两条事件，
        /// 第一条把凭据吃掉、第二条就没人拦了：原生窗还是被收成了标签。
        /// </summary>
        public void OpenNative(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                nativeOpenUntilTick = Environment.TickCount + NativeOpenMs;
                Diag.Step("Hub: 用原生资源管理器打开 " + path);
                ProcessStartInfo si = new ProcessStartInfo("explorer.exe");
                si.Arguments = path.IndexOf(' ') >= 0 ? "\"" + path + "\"" : path;
                si.UseShellExecute = false;
                Process.Start(si);
            }
            catch (Exception ex)
            {
                nativeOpenUntilTick = 0;
                Diag.Log("Hub: 原生打开失败 " + ex.Message);
            }
        }

        /// <summary>
        /// 让行凭据还有效吗（`OpenNative` 起算的那几秒）。**不再「吃掉就作废」** ——
        /// 见 `OnWindowShown` 里那段：一扇新窗会先后报 CREATE / SHOW 两条事件，凭据只够拦第一条。
        /// 窗口本身由 `EmbedApi.MarkNativeKeep` 认着，这里只负责「哪几秒里冒出来的窗算他的」。
        /// 用减法和 `Environment.TickCount` 比较：它 24.9 天会翻一次，减法写法在翻越时仍然成立。
        /// </summary>
        private bool NativeOpenPeriod()
        {
            int until = nativeOpenUntilTick;
            if (until == 0) return false;
            if (Environment.TickCount - until >= 0) { nativeOpenUntilTick = 0; return false; }
            return true;
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
            // 并发起 explorer 期间**先别收**：那批窗口有大约半秒是「还没认领」的 ——
            // 并发的归属判定要等地址栏读出来（见 EmbedApi.FindNewCab），所以「700ms 之后它肯定已被
            // 自己那个标签认领」这条前提在那半秒里不成立，照收就会把我们自己起的窗口当成
            // 「用户新开的」再收一个重复标签进来。等这一批起完再收，代价只是我们把
            // 用户自己开的那个窗口多晾一两秒（它本来就在屏幕上，用户看得到）。
            if (!AnyLaunchInFlight())
            {
                foreach (KeyValuePair<IntPtr, CaptureCandidate> kv in new List<KeyValuePair<IntPtr, CaptureCandidate>>(pendingCapture))
                {
                    // ⚠ 这里**不再等** `CaptureDelayMs`：那 700ms 原本是给「我们自己刚起的窗口还没
                    //   被认领」留的缓冲，而上面那道闸已经保证「此刻没有任何标签在起」—— 不存在那种窗口
                    //   （我们起的窗口在 `Start` 之前就 `Claim` 了，见 EmbedApi.IsClaimed）。
                    //   从开始菜单打开的那扇窗因此能立刻收 —— 实测这一刀省掉整整 0.7 秒：
                    //   原来「触发 -> 我们的窗口出来」要 1.9 秒，其中 0.7 秒就是这么白等的。
                    if (ready == null) ready = new List<IntPtr>();
                    ready.Add(kv.Key);
                }
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
            EmbedForm f = null;
            string incoming = null;
            try
            {
                if (!IsCapturable(h)) { ReleaseIfAbandoned(h); return; }   // 这一轮里可能已经变了（被认领 / 关掉 / 藏了）
                int pid = EmbedApi.ProcessIdOf(h).ToInt32();

                Guid d = VirtualDesktop.WindowDesktopId(h);
                if (d == Guid.Empty) d = VirtualDesktop.CurrentDesktopId();
                f = EnsureForm(d);
                if (f == null) { ReleaseIfAbandoned(h); return; }

                // ★ 记忆里那批标签**只摆名字、先不起 explorer**（见 EmbedForm.StageRememberedTabs）：
                //   两个原因 —— ① 判重（下面 `HasTabForPath`）得靠那些名字先存在；
                //   ② 用户此刻要的是**这一扇窗**，8 个 explorer 一起起舞会把 UI 线程占住 400ms、
                //      还把它挤到队尾。其余的等它落定再排（见本函数最后的 finally）。
                f.StageRememberedTabs();

                // ★ 这扇新窗的目标**本来就是我们某个标签**（他原来就开着这个文件夹，只是没切到前面）：
                //   shell 不会去复用那扇窗，而是又开一扇；我们照单全收就成了**两个一模一样的标签**，
                //   而他真正要的是「切到原来那个」（用户报的就是这个）。
                //   所以：把这扇现建出来的窗关掉，切过去、把窗口顶到前台。
                //   ⚠ 必须排在 `StageRememberedTabs` 之后 —— 那个「原来的标签」刚被摆回来。
                //   ⚠ 也排在防闪那两步之前：这里只**关**、不藏；万一没关成，留在屏幕上的是一扇正常窗，
                //     而不是一扇隐形窗（隐形窗会毒坏 shell，见 v1.13.1 那个「Win+E 没反应」的教训）。
                incoming = EmbedApi.AddressPathOf(h, true);
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
            finally
            {
                // ★ 收尾那一刀：不管上面是收下了、判重切过去了、还是失败了，记忆里其余标签
                //   这时才排进启动队列 —— 用户要的那一扇已经落定，不跟它抢（用户原话：
                //   「优先处理激活标签，延迟后续并发伪懒加载」）。
                if (f != null && !f.IsDisposed)
                {
                    try { f.StartRestLaunches(incoming, true); } catch { }
                }
            }
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
        /// `item` 是「这是哪一条通知」（谁该弹、默认开不开，见 `Settings.NotifyItemOn`）。
        /// </summary>
        public void Notify(NotifyItem item, string title, string text, bool once)
        {
            try
            {
                if (once && trayTipShown) return;
                trayTipShown = true;
                Toast.Show(item, title, text);
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
            // 顺手开不开一个「此电脑」看设置（默认**不开**，见 Settings.WinENewPc）：
            // 关了就是「只把窗口唤到最前面，一个标签都不动」—— 想开新页自己 Ctrl+T。
            f.ShowForUser(Settings.WinENewPc);
        }

        /// <summary>
        /// 把「当前这张桌面」记着的标签在后台先起出来（见 `Settings.AutoPreload`）。
        ///
        /// 跟 `OnWinE` 的差别就一条：**不 Show、不抢前台** —— 窗口还是等用户第一次 Win+E 才出现，
        /// 只是那时候标签已经是热的。所以这个方法不能调 `ShowForUser`，只调 `EmbedForm.PreloadTabs`。
        ///
        /// 只管当前桌面：别的桌面的窗口得建在别的桌面上（`EmbedForm` 的归属按它实际挂在哪张桌面认），
        /// 在这儿替它们建会建错桌面。
        /// </summary>
        private void PreloadCurrentDesktop()
        {
            if (quitting || !Settings.AutoPreload) return;
            try
            {
                Guid d = VirtualDesktop.CurrentDesktopId();
                EmbedForm f = EnsureForm(d);
                if (f == null) return;
                f.PreloadTabs();
            }
            catch (Exception ex)
            {
                Diag.Log("Hub: 预加载失败 " + ex.Message);
            }
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
            SyncHosts();                      // 上面那行已经把句柄逼出来了，正好同步给空格钩子
            nf.SetFavBarOn(Settings.FavBar);  // 新窗口要跟上当前的书签栏开关（设置存在文件里，窗口自己不知道）
            return nf;
        }

        // ==================================================================
        // 多窗口（用户：标签页也能拖到另一个程序窗口，体验跟浏览器一样）
        // ==================================================================

        /// <summary>
        /// 只建窗口 + 登记，**先不 Show** —— 拖出来的那个标签要先挂进去再露面
        /// （不然 `ShowExtra` 会先兜一个「此电脑」，白起一个 explorer）。
        /// 落在**当前这张虚拟桌面**上，登记在 `extras`（不参与 Win+E，见那张表的说明）。
        /// </summary>
        /// <param name="bounds">拖出来的窗口就开在鼠标松手那儿；null = 默认尺寸、居中。</param>
        internal EmbedForm CreateExtraWindow(Rectangle? bounds)
        {
            Guid d = VirtualDesktop.CurrentDesktopId();
            EmbedForm nf = new EmbedForm(this, KeyOf(d));
            nf.IsExtra = true;
            if (bounds.HasValue)
            {
                // ⚠ 窗体默认 `StartPosition = CenterScreen`，不改成 Manual 的话 Show 那一刻会**居中**
                //   把这里摆好的位置整个盖掉（拖出来的窗口就跑不到鼠标那儿了）。
                nf.StartPosition = FormStartPosition.Manual;
                nf.Bounds = bounds.Value;
            }
            extras.Add(nf);
            nf.FormClosed += delegate { OnFormGone(nf); };
            IntPtr force = nf.Handle;         // 逼出句柄 —— 下面 `SyncHosts` 要靠它把新窗口加进空格预览白名单
            SyncHosts();
            nf.SetFavBarOn(Settings.FavBar);  // 新窗口跟上当前的书签栏开关
            return nf;
        }

        /// <summary>托盘「新建窗口」/ Ctrl+N：开一个空窗口（没标签时 `ShowExtra` 会兜一个「此电脑」）。</summary>
        public EmbedForm OpenExtraWindow(string path) { return OpenExtraWindow(path, null); }

        public EmbedForm OpenExtraWindow(string path, Rectangle? bounds)
        {
            EmbedForm nf = CreateExtraWindow(bounds);
            Diag.Step("Hub: 新建窗口（现有 " + extras.Count + " 个额外窗口）");
            nf.ShowExtra(path);
            return nf;
        }

        /// <summary>
        /// 把一个额外窗口先从登记表摘掉（它要真关了）。
        /// ⚠ 必须排在 `SaveNow` **之前**：它的标签马上要被关掉，先摘掉才不会在保存那一刻
        ///   被合并进记忆里（那就成了「关掉了却还在」）。
        /// </summary>
        internal void DropExtra(EmbedForm f)
        {
            if (f == null) return;
            if (extras.Remove(f))
            {
                SyncHosts();
                Diag.Step("Hub: 摘掉额外窗口（还剩 " + extras.Count + " 个）");
            }
        }

        /// <summary>
        /// 把一批「刚关掉的标签」记到**这张桌面的主窗口**的恢复栈里。
        /// 额外窗口整体关掉时用：它自己那个栈随窗口一起没了，标签得能从主窗口 Ctrl+Shift+T 捞回来
        /// （用户选的口径：「一起关掉，需要的话可以从『恢复关闭的标签页』一条条捞回来」）。
        /// 记的**不带下标**（-1 = 恢复时按 `newtabbeside` 落到末尾）：它们本来在另一个窗口里，
        /// 原位置在这个窗口没有意义。
        /// </summary>
        internal void RememberClosedTabs(string desktopKey, List<string> paths)
        {
            if (paths == null || paths.Count == 0) return;
            foreach (EmbedForm f in new List<EmbedForm>(forms.Values))
            {
                if (f == null || f.IsDisposed) continue;
                if (!string.Equals(f.DesktopKey, desktopKey, StringComparison.OrdinalIgnoreCase)) continue;
                f.PushClosedTabs(paths);
                return;
            }
            Diag.Step("Hub: 想把这批关掉的标签记进恢复栈，但这张桌面没有主窗口可记");
        }

        // ---- 跨窗口拖标签（浏览器那套：拖到别的窗口并进去 / 拖出去另开一个）----

        /// <summary>
        /// 屏幕点上是不是**我们自己**的某个窗口 —— 是就返回它，不是（桌面 / 别的程序）返回 null。
        /// ⚠ 拿到的是子窗口句柄（可能正好指着嵌进来的 explorer），得先 `GetAncestor(…, GA_ROOT)`
        ///   一路走到最顶层窗口，再拿 `Handle` 去认。
        /// </summary>
        internal EmbedForm FormAtScreen(Point screen)
        {
            try
            {
                IntPtr w = NativeMethods.WindowFromPoint(new POINT(screen.X, screen.Y));
                if (w == IntPtr.Zero) return null;
                IntPtr root = NativeMethods.GetAncestor(w, 2);   // GA_ROOT
                if (root == IntPtr.Zero) return null;
                foreach (EmbedForm f in AllForms())
                {
                    if (f == null || f.IsDisposed) continue;
                    if (f.Handle != root) continue;
                    // 保险：认出来的这扇窗**必须真的盖住这个点**。
                    // 万一 `WindowFromPoint` 给回来的根句柄是别人的（拖拽期间系统/别的进程插进来的
                    // 临时窗、贴边预览之类），按句柄认亲会认错窗口 —— 那就把标签并进一扇不该进去的窗。
                    if (!f.Bounds.Contains(screen)) continue;
                    return f;
                }
            }
            catch (Exception ex) { Diag.Log("Hub: 落点判定失败 " + ex.Message); }
            return null;
        }

        /// <summary>标签被拖出标签条（跨窗口拖拽开始）。这里只记一笔，真正的高亮在 `UpdateTabDrag` 里画。</summary>
        internal void BeginTabDrag(EmbedForm src)
        {
            Diag.Step("Hub: 标签拖出标签条 -> 开始跨窗口拖拽");
        }

        /// <summary>
        /// 拖动中：跟着鼠标把「该插到哪个窗口的第几位」实时画出来，
        /// 并**返回一句人话**告诉用户现在松手会发生什么（显示在拖拽影窗上，见 `DragGhost`）。
        /// </summary>
        internal string UpdateTabDrag(EmbedForm src, Point screen)
        {
            EmbedForm t = FormAtScreen(screen);
            foreach (EmbedForm f in AllForms())
            {
                if (f == null || f.IsDisposed) continue;
                f.SetDropHint(f != src && f == t ? f.DropIndexAt(screen) : -1);
            }

            if (t != null && t != src) return "松手：并入这个窗口";
            int away = src == null || src.IsDisposed ? int.MaxValue : src.DistanceFromTabBar(screen);
            if (away == 0) return "松手：移到这个位置";
            if (t != null && away < TypeSlop) return "松手：取消（离标签条太近）";
            return "松手：在这里开一扇新窗口";
        }

        /// <summary>拖到一半又拖回自家标签条上（在自己这条上松手 = 走正常的条内重排，跨窗口这条作废）。</summary>
        internal void CancelTabDrag(EmbedForm src)
        {
            foreach (EmbedForm f in AllForms())
                if (f != null && !f.IsDisposed) f.SetDropHint(-1);
        }

        /// <summary>
        /// 在标签条之外松手。两条路（跟浏览器一致）：
        ///   ① 松在**另一个**我们的窗口上 → 并进去（落在标签条上就插到那一条缝，落在内容区就追加到末尾）；
        ///   ② 别的任何地方 —— 桌面、别的程序、**以及自己窗口的内容区** —— 都在松手的位置
        ///      **另开一扇窗口**把标签搬过去。
        ///
        /// ⚠ 为什么「自己窗口的内容区」也算 ②：`TabStrip` 那边的出界判定是「离开标签条」，
        ///   所以往下一拖（进了自己的内容区）就已经算拖出去了。窗口一最大化，屏幕上一块空地都没有，
        ///   只认「落在窗口之外」等于这个功能永远够不着 —— 用户报的「拖不出新窗口」就是这一条。
        ///   落回**标签条**上根本走不到这儿（`TrackDragOut` 会先把 `dragOut` 清掉，那条路是条内排序）。
        /// </summary>
        internal void EndTabDrag(EmbedForm src, int idx, Point screen)
        {
            foreach (EmbedForm f in AllForms())
                if (f != null && !f.IsDisposed) f.SetDropHint(-1);

            if (src == null || src.IsDisposed || src.Disposing) return;
            EmbedForm t = FormAtScreen(screen);

            // ① 并进另一个窗口
            if (t != null && t != src)
            {
                int to = t.DropIndexAt(screen);
                Diag.Step("Hub: 标签并入另一个窗口 idx=" + idx + " -> 位置 " + to);
                src.MoveTabTo(t, idx, to);
                if (!t.IsDisposed && t.Visible) t.ActivateToFront();
                return;
            }

            // 手一抖、只是从标签条上滑下来一点点（还在自己窗口里、离标签条很近）：当取消，别开窗。
            int away = src.DistanceFromTabBar(screen);
            if (t == src && away < TypeSlop)
            {
                Diag.Step("Hub: 拖拽落点离标签条只有 " + away + "px，当取消（不开新窗口）");
                return;
            }

            // ② 在鼠标位置另开一扇，把标签搬过去
            Diag.Step("Hub: 标签拖出标签条（落点 " + screen.X + "," + screen.Y
                + "，离标签条 " + away + "px）-> 另开一扇");
            Rectangle b = DetachedBounds(src, screen);
            EmbedForm nf;
            try
            {
                nf = CreateExtraWindow(b);
            }
            catch (Exception ex)
            {
                Diag.Log("Hub: 新建窗口失败 " + ex.GetType().Name + ": " + ex.Message);
                return;
            }
            try
            {
                src.MoveTabTo(nf, idx, -1);
                nf.ShowExtra(null);
            }
            catch (Exception ex)
            {
                Diag.Log("Hub: 把标签搬进新窗口失败 " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>离标签条多近算「手一抖」—— 在这个距离以内松手当取消，别开新窗口。</summary>
        private const int TypeSlop = 24;

        /// <summary>
        /// 拖出去的窗口开在哪儿：**鼠标松手点当标题栏**（跟浏览器一样，窗口正好在指针下方），
        /// 大小跟来源窗口一样，再夹进主屏工作区里别跑出去。
        /// </summary>
        private static Rectangle DetachedBounds(EmbedForm src, Point screen)
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            // 大小跟来源窗口一样；它最大化过的话取**还原态**尺寸（不然新窗口跟着铺满整屏）
            Rectangle sb = (src != null && !src.IsDisposed) ? src.RestoreBounds : Rectangle.Empty;
            int w = Math.Min(sb.Width > 200 ? sb.Width : 960, (int)(wa.Width * 0.9));
            int h = Math.Min(sb.Height > 200 ? sb.Height : 620, (int)(wa.Height * 0.9));
            int x = screen.X - Math.Min(160, w / 3);
            int y = screen.Y - Math.Min(18, h / 8);
            if (x + w > wa.Right) x = wa.Right - w;
            if (y + h > wa.Bottom) y = wa.Bottom - h;
            if (x < wa.Left) x = wa.Left;
            if (y < wa.Top) y = wa.Top;
            return new Rectangle(x, y, w, h);
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
                // ⚠ 多窗口：迁移模式的定义就是「全进程只留一个窗口」⇒ 额外窗口也一并收掉
                foreach (EmbedForm f in AllForms())
                {
                    if (f == null || f.IsDisposed || f == keep) continue;
                    Diag.Step("Hub: 迁移模式，收掉多余窗口 " + f.DesktopKey + (f.IsExtra ? "（额外窗口）" : ""));
                    try { f.Quitting = true; f.Close(); }
                    catch (Exception ex) { Diag.Log("Hub: 收窗口失败 " + ex.Message); }
                }
                forms.Clear();
                extras.Clear();

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

        /// <summary>
        /// 并发起 explorer。**立刻生效**（看下一次起标签），所以只刷一下菜单勾选。
        /// </summary>
        public void SetParallelLaunch(bool on)
        {
            if (Settings.ParallelLaunch == on) return;
            Settings.SetParallelLaunch(on);
            RefreshTrayMenu();
            Diag.Step("Hub: 并发起 explorer -> " + (on ? "开" : "关"));
        }

        /// <summary>
        /// 预加载（默认开，见 `Settings.AutoPreload`）。
        /// ⚠ 它是**启动时的一次性动作** —— 改完只影响**下次**启动；当前这次已经预加载出来的标签不动
        ///   （跟「懒加载」一个道理，那个也是「只影响下次还原」）。
        /// </summary>
        public void SetAutoPreload(bool on)
        {
            if (Settings.AutoPreload == on) return;
            Settings.SetAutoPreload(on);
            RefreshTrayMenu();
            Diag.Step("Hub: 预加载 -> " + (on ? "开（下次启动生效）" : "关"));
        }

        /// <summary>
        /// 非激活标签自动休眠（默认开）。**立刻生效** —— `EmbedForm.TrimInactiveTabs` 每次现读这个闸，
        /// 所以关掉之后下一次切标签起就不会再收内存了（已经收掉的也会在切回去时自然读回）。
        /// </summary>
        public void SetAutoSleep(bool on)
        {
            if (Settings.AutoSleep == on) return;
            Settings.SetAutoSleep(on);
            RefreshTrayMenu();
            Diag.Step("Hub: 非激活标签自动休眠 -> " + (on ? "开" : "关"));
        }

        /// <summary>
        /// 空格键预览（QuickLook）总开关。**立刻生效** —— `AppContext` 那个键盘钩子每次现读这个闸。
        /// </summary>
        public void SetQLPreview(bool on)
        {
            if (Settings.QLPreview == on) return;
            Settings.SetQLPreview(on);
            RefreshTrayMenu();
            Diag.Step("Hub: 空格键预览 -> " + (on ? "开" : "关"));
        }

        /// <summary>
        /// 非激活标签**停留多久之后**收内存（秒，见 `Settings.SleepDelaySec`）。**立刻生效** ——
        /// 除了写进设置，还要把已经开着的窗口那个定时器重排一下（不然得等下次切标签才用上新值）。
        /// </summary>
        public void SetSleepDelay(int sec)
        {
            int v = Settings.ClampSleepSec(sec);
            if (Settings.SleepDelaySec == v) return;
            Settings.SetSleepDelay(v);
            foreach (EmbedForm f in AllForms())
            {
                if (f == null || f.IsDisposed) continue;
                f.ApplySleepDelay();
            }
            Diag.Step("Hub: 非激活标签多久之后收内存 -> " + Settings.SleepDelaySec + " 秒");
        }

        /// <summary>
        /// 「右下角通知」（默认开，见 `Settings.Notify`）：屏幕右下角那种操作反馈气泡弹不弹。
        /// **立刻生效** —— 总闸就在 `Toast.Show` 头上，每次现读，关掉之后下一条就不弹了。
        /// </summary>
        public void SetNotify(bool on)
        {
            if (Settings.Notify == on) return;
            Settings.SetNotify(on);
            RefreshTrayMenu();
            Diag.Step("Hub: 右下角通知 -> " + (on ? "开" : "关"));
        }

        /// <summary>
        /// 「一条气泡停留多少秒」（设置窗口「通知管理」页那个数字框，见 `Settings.NotifySec`）。
        /// **立刻生效** —— 每次弹之前现读；已经在屏幕上的那一条不变。
        /// </summary>
        public void SetNotifySec(int sec)
        {
            int v = Settings.ClampNotifySec(sec);
            if (Settings.NotifySec == v) return;
            Settings.SetNotifySec(v);
            Diag.Step("Hub: 通知停留时间 -> " + Settings.NotifySec + " 秒");
        }

        /// <summary>
        /// 设**一条**通知的开关（设置页里最小那一行，见 `Settings.NotifyItemBase`）。
        /// **立刻生效** —— 每次弹之前现读，没有缓存；这些是 `WinOnly`，托盘菜单里不排。
        /// </summary>
        public void SetNotifyItem(NotifyItem it, bool on)
        {
            if (it == null || Settings.NotifyItemBase(it) == on) return;
            Settings.SetNotifyItem(it, on);
            Diag.Step("Hub: 通知明细 " + it.Id + " -> " + (on ? "开" : "关"));
        }

        /// <summary>设**一大类**（设置页点节标题那个三态勾：整批开 / 整批关）。</summary>
        public void SetNotifyKind(ToastKind k, bool on)
        {
            Settings.SetNotifyKind(k, on);
            Diag.Step("Hub: 通知大类 " + NotifyItems.KindTitle(k) + " -> " + (on ? "全开" : "全关"));
        }

        /// <summary>
        /// 重启本程序（设置里那一项）。
        ///
        /// 顺序不能反：**先起新进程、再退自己** —— 单实例锁是我们退出那一刻才放开的，
        /// 所以新进程带 `--restart-wait &lt;我们的 pid&gt;`，它会先等我们退干净再抢锁
        /// （不然它把自己当成「第二个实例」，转头去 Set 唤醒事件然后自杀，看着就是「点了重启、程序没了」）。
        /// 标签记忆由 `Quit()` 落盘，这里不用另存。
        ///
        /// 新进程的可见性跟着现在走：窗口正开着就 `--open`（重启完还在眼前，正好看效果 / 验懒加载），
        /// 收在托盘里就 `--tray`（不无缘无故弹窗、不抢前台）。
        /// </summary>
        public void RestartApp()
        {
            try
            {
                bool visible = false;
                foreach (EmbedForm f in AllForms())
                {
                    if (f != null && !f.IsDisposed && f.Visible) { visible = true; break; }
                }
                int pid = Process.GetCurrentProcess().Id;
                string exe = Application.ExecutablePath;
                string args = (visible ? "--open" : "--tray") + " --embed --restart-wait " + pid;
                Diag.Step("Hub: 重启本程序 -> " + args);
                Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false });
            }
            catch (Exception ex)
            {
                // 新进程没起来就**先别退**，否则用户两头空（旧程序没了、新的也没来）
                Diag.Log("Hub: 重启失败 " + ex.Message);
                Toast.Show(NotifyItems.RestartFail, "重启失败", ex.Message);
                return;
            }
            Quit("重启本程序");
        }

        /// <summary>现在有标签正等着 explorer 起来吗（任何一张桌面）。见 DrainCapture 的那道闸。</summary>
        private bool AnyLaunchInFlight()
        {
            foreach (EmbedForm f in AllForms())
            {
                if (f != null && !f.IsDisposed && f.LaunchInFlight) return true;
            }
            return false;
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

        /// <summary>
        /// 新标签开在**当前标签旁边**（`true`）还是**最末尾**（`false`，默认）。
        /// 没有副作用要补 —— `EmbedForm.NextTabIndex` 每次新建标签时现读这个值，所以改了立刻生效；
        /// 已经在的那排标签一个都不动（只管往后新开的）。
        /// </summary>
        public void SetNewTabBeside(bool on)
        {
            if (Settings.NewTabBeside == on) return;
            Settings.SetNewTabBeside(on);
            RefreshTrayMenu();
            Diag.Step("Hub: 新标签位置 -> " + (on ? "当前标签旁边" : "最末尾"));
        }

        /// <summary>
        /// Win+E 时开不开一个「此电脑」标签（默认**关**，见 `Settings.WinENewPc`）。
        /// 没有副作用要补 —— `OnWinE` 每次唤窗口时现读这个值，所以改了立刻生效。
        /// </summary>
        public void SetWinENewPc(bool on)
        {
            if (Settings.WinENewPc == on) return;
            Settings.SetWinENewPc(on);
            RefreshTrayMenu();
            Diag.Step("Hub: Win+E 开「此电脑」-> " + (on ? "开" : "关（只唤回窗口）"));
        }

        private void RetabAll()
        {
            foreach (EmbedForm f in AllForms())
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
            foreach (EmbedForm f in AllForms())
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

        /// <summary>
        /// 垂直侧边栏里**书签段**的高度（逻辑像素，0 = 自动）—— 用户在标签区与书签段之间
        /// 那根分割条上拖出来的，拖完记住。数值项，改完所有窗口一起重排。
        /// 不进托盘菜单（它就靠拖，没有对应的菜单项），所以不用 `RefreshTrayMenu`。
        /// </summary>
        public void SetFavBandHeight(int h)
        {
            if (Settings.FavBandHeight == h) return;
            Settings.SetFavBandHeight(h);
            VerticalAll();
            Diag.Step("Hub: 书签段高度 -> " + Settings.FavBandHeight + "（逻辑像素）");
        }

        private void VerticalAll()
        {
            foreach (EmbedForm f in AllForms())
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
                foreach (EmbedForm x in AllForms())
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
                Notify(NotifyItems.AutoStartFail, "开机自启", "改不了启动项（注册表写不进去），还是原样。", false);
                return;
            }
            Diag.Step("Hub: 开机自启 -> " + (on ? "开" : "关"));
        }

        /// <summary>
        /// 把每个设置项**再推一遍**（设置窗口点「不保存」时用它把已经立刻生效的改动退回去）。
        ///
        /// ⚠ 顺序上必须**先调这个、再 `Settings.ApplySnapshot`**：这些 `SetXxx` 第一句都是
        /// 「值没变就 return」，所以此刻 `Settings` 里必须还是**改过的值**，它们才会真的执行；
        /// 而它们执行时自己会把 `Settings` 那一项写回快照里的值（`Settings.SetXxx`）。
        /// 详见 `SettingsForm.DiscardEdits`。
        ///
        /// 已知的不可逆项：**捕获方式**那一项切换时会搬「标签记忆」（`MoveMemory`），
        /// 从 migrate 退回 perdesktop 只会把记忆落到当前桌面、不会还原成原来分散在各桌面的样子。
        /// 这一点在开窗那一刻点下去就已经发生了，跟「保存 / 不保存」无关。
        /// </summary>
        public void RestoreSettings(Settings.Snapshot s)
        {
            if (s == null) return;
            SetCaptureMode(s.Capture);
            SetColorMode(s.Color);
            SetKeepTabs(s.KeepTabs);
            SetLazyTabs(s.LazyTabs);
            SetParallelLaunch(s.ParallelLaunch);
            SetAutoPreload(s.AutoPreload);
            SetAutoSleep(s.AutoSleep);
            SetSleepDelay(s.SleepDelaySec);
            // ⚠ 这两个以前漏在这儿了（空格键预览是加进来时忘了补）—— 它们没有副作用要补，
            //   紧随其后的 `Settings.ApplySnapshot` 也能把值抄回去，但日志里缺一行就分不清
            //   「没恢复」和「恢复了但没打印」，索性一起补上。
            SetQLPreview(s.QLPreview);
            SetNewTabBeside(s.NewTabBeside);
            SetWinENewPc(s.WinENewPc);
            SetNotify(s.Notify);
            // ⚠ 通知的**逐条开关表**在这儿**不用**管（以前这里有四行 `SetNotifyPart`）：
            //   它是纯数据、没有副作用（不像捕获方式会搬记忆、休眠要重排定时器），
            //   而紧随其后的 `Settings.ApplySnapshot` 会把整张表抄回去 —— 抄一遍就够了。
            SetNotifySec(s.NotifySec);
            SetTabWidth(s.TabWidth);
            SetTabAutoWiden(s.TabAutoWiden);
            SetTabAutoFit(s.TabAutoFit);
            SetFavBar(s.FavBar);
            SetCaptureAll(s.CaptureAll);
            SetCaptureShell(s.CaptureShell);
            SetWindowSize(s.WindowSize);
            SetVerticalTabs(s.VTabs);
            SetVTabCollapse(s.VTabsCollapse);
            SetVPaneAlpha(s.VPaneAlpha);
            SetFavBandHeight(s.FavBandHeight);
            SetDebug(s.Debug);
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
            // 「新建窗口」不依赖「哪个窗口在前台」这个上下文 —— 直接在 Hub 这一层接掉。
            // （其余命令都得知道是**哪个**窗口的标签，所以照旧派给前台那个。）
            if (string.Equals(cmd, "newwin", StringComparison.OrdinalIgnoreCase))
            {
                OpenExtraWindow(null);
                return;
            }
            EmbedForm f = ForegroundForm();
            if (f == null) { Diag.Step("Hub: 热键 " + cmd + " 但没有我们的前台窗口，忽略"); return; }
            f.HandleHotkey(cmd);
        }

        private EmbedForm ForegroundForm()
        {
            IntPtr fg = NativeMethods.GetForegroundWindow();
            if (fg == IntPtr.Zero) return null;
            IntPtr root = NativeMethods.GetAncestor(fg, 2);   // GA_ROOT
            foreach (EmbedForm f in AllForms())     // 额外窗口也算（多窗口下前台可能是它）
            {
                if (f == null || f.IsDisposed) continue;
                IntPtr h = f.Handle;
                if (h == fg || (root != IntPtr.Zero && root == h)) return f;
            }
            return null;
        }

        /// <summary>
        /// 空格预览（QuickLook，见 `QLPreview` 的类注释）：拿**前台那个窗口**当前激活标签停在的
        /// 文件夹，向 shell 问它现在选中了什么，把第一个喂给 QuickLook。
        /// 在 UI 线程上跑（钩子线程通过 `Post` 投过来）。
        /// </summary>
        private void OnSpacePreview()
        {
            EmbedForm f = ForegroundForm();
            if (f == null) return;
            QLPreview.Do(f.ActiveTabPath);
        }

        /// <summary>
        /// 方向键 / Esc / Enter（见 `QLPreview.PreviewKey`）：原生资源管理器里这几键也是 QuickLook 管的，
        /// 但它的判据是**前台窗口的类名**，自绘宿主它不认 ⇒ 由我们替它转发。在 UI 线程上跑。
        ///   · 方向键 ⇒ `DoSwitch`：读当前标签所在文件夹**此刻**的选中项发过去
        ///     （卡点：钩子报的是**松开**，那会儿内嵌列表已经自己把选中项移完了）。
        ///   · Esc / Enter ⇒ `DoClose`：关掉预览。Enter 仍然照常打开文件 —— 那是内嵌列表自己的事。
        /// </summary>
        private void OnPreviewKey(int vk)
        {
            EmbedForm f = ForegroundForm();
            if (f == null) return;
            if (QLPreview.IsCloseKey(vk)) { QLPreview.DoClose(); return; }
            QLPreview.DoSwitch(f.ActiveTabPath);
        }

        /// <summary>
        /// 把「宿主窗体句柄」同步给 `QLPreview`。空格钩子拿它判「前台是不是我们自己的窗口」——
        /// 光判进程不够：设置窗口 / 通知气泡也是我们的窗口，而**在那些地方空格是正常操作**
        /// （勾复选框）。只在 UI 线程调，`forms` 一变就调一次。
        /// </summary>
        private void SyncHosts()
        {
            List<IntPtr> hs = new List<IntPtr>();
            // ⚠ 多窗口：**每个**窗口的句柄都要进白名单 —— 漏一个，在它里面按空格就不再预览
            foreach (EmbedForm f in AllForms())
            {
                if (f == null || f.IsDisposed || !f.IsHandleCreated) continue;
                hs.Add(f.Handle);
            }
            QLPreview.SetHosts(hs);
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
        ///
        /// ⚠ 多窗口：**一张桌面只有一个桶**。主窗口那份是正本（`Clear` 再写），
        ///   额外窗口的标签接在后面追加（去重）—— 重启回到一个窗口，但标签一个不丢。
        /// </summary>
        public void SaveNow(string why)
        {
            try
            {
                int live = 0, tabs = 0;
                List<EmbedForm> all = AllForms();

                // ① 主窗口先写：它们的键就是桌面键，桶里这一份是「这张桌面的正本」（Clear 从这里来）
                foreach (EmbedForm f in all)
                {
                    if (f == null || f.IsDisposed || f.IsExtra) continue;
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
                    // ⚠ 只记**主窗口**的：多窗口下额外窗口是临时分组，不该把正门那块地挤掉。
                    if (Settings.WindowSize)
                    {
                        string bs = f.BoundsString;
                        if (!string.IsNullOrEmpty(bs)) b.Bounds = bs;
                    }
                    live++;
                }

                // ② 额外窗口把标签**追加**进同一个桶（用户选的口径：合并记一份）——
                //    重启后回到一个窗口，但标签一个不丢。去重因为两个窗口可能都开着同一个文件夹。
                foreach (EmbedForm f in all)
                {
                    if (f == null || f.IsDisposed || !f.IsExtra) continue;
                    DesktopMemory.Bucket b = memory.Ensure(f.DesktopKey);
                    string active = f.ActiveTabPath;
                    foreach (string p in f.TabPaths())
                    {
                        if (string.IsNullOrEmpty(p)) continue;
                        if (b.Paths.Exists(delegate(string s) { return PathRules.Same(s, p); })) continue;
                        b.Paths.Add(p);
                        if (PathRules.Same(p, active)) b.Active = p;
                        tabs++;
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

            List<EmbedForm> all = AllForms();
            forms.Clear();
            extras.Clear();
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
            extras.Remove(f);     // 额外窗口（见 `extras` 那张表）也要从这队里摘掉
            SyncHosts();
            Diag.Step("Hub: 窗口没了，还剩 " + forms.Count + " 个主窗口 / " + extras.Count + " 个额外窗口");
            MarkDirty();
        }

        private void Prune()
        {
            foreach (string k in new List<string>(forms.Keys))
            {
                EmbedForm f = forms[k];
                if (f == null || f.IsDisposed) forms.Remove(k);
            }
            extras.RemoveAll(delegate(EmbedForm f) { return f == null || f.IsDisposed; });
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
            if (settingsForm != null)
            {
                // ⚠ 先标记「程序在退」—— 不然关它的时候会弹「有改动还没保存」的确认框，
                //   而这一路是关机流程，弹出来就卡在那儿了（见 `SettingsForm.OnFormClosing`）。
                try { settingsForm.Quitting = true; settingsForm.Close(); } catch { }
                settingsForm = null;
            }
            if (favManager != null) { try { favManager.Close(); } catch { } favManager = null; }
            if (captureTimer != null) { try { captureTimer.Dispose(); } catch { } captureTimer = null; }
            if (revealTimer != null) { try { revealTimer.Dispose(); } catch { } revealTimer = null; }
            if (preloadTimer != null) { try { preloadTimer.Stop(); preloadTimer.Dispose(); } catch { } preloadTimer = null; }
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
