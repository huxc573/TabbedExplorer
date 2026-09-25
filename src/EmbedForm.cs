using System;
using System.Collections.Generic;
using System.Drawing;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TabbedExplorer
{
    /// <summary>
    /// 「嵌入真 explorer 窗口」模式的窗口 —— **每张虚拟桌面一个**。
    /// 和 MainForm 那套（自绘外壳 + IExplorerBrowser）完全独立。
    ///
    /// 这里我们只负责：顶部一条自绘标题栏 + 一条标签条 + 一个容器。
    /// Ribbon、地址栏、导航窗格、文件列表、状态栏**全部是 explorer 自己的**，
    /// 所以外观和系统资源管理器一模一样。
    ///
    /// 托盘图标和键盘钩子**不在这个类里**了 —— 全进程只能有一份，所以搬去了 DesktopHub。
    /// 这个窗口只管「自己这张桌面的标签」：
    ///   - 起来（第一次现身）时按 `desktops.txt` 里**本桌面**记的那几个路径把标签摆回来；
    ///   - 标签有变动就告诉 Hub 攒一下写盘；
    ///   - 关窗口（X / Ctrl+W 关到最后一个）= 收进托盘，进程不退，标签也还在（真退出才落盘）。
    /// </summary>
    internal sealed class EmbedForm : Form
    {
        private static readonly float DpiScale = ReadDpi();

        private static float ReadDpi()
        {
            try
            {
                uint d = NativeMethods.GetDpiForSystem();
                if (d >= 96) return d / 96f;
            }
            catch { }
            return 1f;
        }

        private static int Px(int v) { return (int)Math.Round(v * DpiScale); }

        /// <summary>
        /// 一格滚轮横向滑标签条滑多远（逻辑像素）。不除以 120 —— 一格滚轮就是一步，
        /// 滑一小段能看清，多了会「哗」地跳过去。
        /// </summary>
        private static int TabScrollStep { get { return Px(60); } }

        private const int WM_NCHITTEST = 0x0084;
        private const int WM_NCACTIVATE = 0x0086;
        private const int WM_GETMINMAXINFO = 0x0024;
        private const int WM_SETTINGCHANGE = 0x001A;
        private const int HTCLIENT = 1;
        private const int HTLEFT = 10;
        private const int HTRIGHT = 11;
        private const int HTTOP = 12;
        private const int HTTOPLEFT = 13;
        private const int HTTOPRIGHT = 14;
        private const int HTBOTTOM = 15;
        private const int HTBOTTOMLEFT = 16;
        private const int HTBOTTOMRIGHT = 17;

        private readonly DesktopHub hub;
        private readonly TabStrip tabStrip;
        private readonly Panel content;
        private readonly List<ExplorerHost> hosts = new List<ExplorerHost>();
        private int activeIndex = -1;
        private bool restored;
        /// <summary>`StageRememberedTabs` 摆下的那串路径（按记忆里的顺序）/ 其中当时选中的那个 ——
        /// 给 `StartRestLaunches` 决定「谁先起」用。</summary>
        private List<string> stagedPaths;
        private string stagedActive;
        /// <summary>第二步（真起 explorer）已经走过了 —— 别让异常路径把它走两遍。</summary>
        private bool restLaunched;
        /// <summary>记忆里其余的标签还等着「优先那个落定」再排队（见 StartRestLaunches）。</summary>
        private bool restPending;
        private ExplorerHost restFirstHost;
        private System.Windows.Forms.Timer restFallback;
        /// <summary>刚被收编进来的那个标签（用户在等的就是它）。收编进来的 host 还没读过地址栏，
        /// 按路径查不到，所以 `StartRestLaunches` 靠这个引用认它。</summary>
        private ExplorerHost lastAdopted;
        /// <summary>窗口的位置和大小已经从记忆里还原过了（一次性的，别每次按 Win+E 都去搬）。</summary>
        private bool boundsDone;
        /// <summary>这个窗口真给用户看过（决定要不要把它的位置大小记进记忆 —— 没见过的窗口不许覆盖旧值）。</summary>
        private bool windowShown;

        /// <summary>刚关掉的标签路径（后进先出）—— Ctrl+Shift+T / 恢复按钮从这儿往回取。</summary>
        private readonly List<string> closedTabs = new List<string>();
        private const int ClosedKeep = 20;

        /// <summary>书签栏（Ctrl+Shift+B 开关）。</summary>
        private readonly FavBar favBar;
        private bool favBarOn;

        // ---- 垂直侧边栏（Ctrl+Shift+,）----
        /// <summary>
        /// 左侧那个竖排的标签窗格。它是**同一个 `TabStrip` 类的另一个实例**，
        /// `MirrorFrom(tabStrip)` 之后两边共用同一份标签模型 —— 所以上面那些
        /// SetTitle / AddTab / MoveTab 都只调一次，两个视图一起变（不做副本同步）。
        /// </summary>
        private readonly TabStrip vPane;
        /// <summary>现在是不是垂直侧边栏模式。</summary>
        private bool verticalOn;
        /// <summary>鼠标在垂直窗格里 —— 「折叠窗格」临时展开的判据（见 PaneShowWidth）。</summary>
        private bool paneHover;
        /// <summary>竖排书签区已经读过一次数据了（书签一个都没有时也置位，免得每次排版都重读）。</summary>
        private bool favBandLoaded;

        /// <summary>标签条空白处右键时鼠标在哪儿 —— 菜单要弹在那个点上。</summary>
        private Point blankAt;
        /// <summary>那个「点」是哪个控件上的客户坐标（垂直模式下是左边窗格，不是顶部那条）。</summary>
        private Control blankOwner;

        /// <summary>「收进托盘」那条提示只弹一次（原来靠 Hub 的 once 参数，现在提示归我们自己管）。</summary>
        private bool trayTipShown;

        // ---- 起 explorer 的队列（见 PumpLaunch）----
        private sealed class Launch
        {
            public ExplorerHost Host;
            public string Path;
            /// <summary>这一趟起的是**备用窗口**（预热），不是某个标签 —— 见 WarmUp。</summary>
            public bool Warm;
            /// <summary>这一趟是什么时候发出去的 —— 并发窗口靠它按时间放行，见 <see cref="SpawnSlotMs"/>。</summary>
            public DateTime SpawnedAt = DateTime.MinValue;
        }
        private readonly Queue<Launch> launchQueue = new Queue<Launch>();

        /// <summary>
        /// 「起进程」这一格被谁占着（值 = 它什么时候开工的）。同时最多 <see cref="MaxConcurrentLaunch"/> 格。
        ///
        /// 从前这里是「一个标签一个格，认到窗口才算用完」。改成**按时间**放行（见 <see cref="SpawnSlotMs"/>），
        /// 因为占着格等「认到窗口」会让实际并发度衰减成 1 —— 那正是用户看到的
        /// 「等一批加载完，再处理另一批」。
        /// </summary>
        private readonly Dictionary<ExplorerHost, DateTime> launching = new Dictionary<ExplorerHost, DateTime>();

        /// <summary>
        /// 一批最多同时**起进程**几个。不设上限的话「还原 20 个标签」会一口气冒出 20 个
        /// explorer.exe（每个几十 MB），而且它们全在同一秒里抢着建窗口，判定压力也白涨。
        /// </summary>
        private const int MaxConcurrentLaunch = 4;

        /// <summary>
        /// 一格并发窗口最多占多久 —— 到点就放行下一个，**不等**「认到窗口」。
        ///
        /// 为什么必须按时间放行：认到窗口要等地址栏可读，而地址栏要等 explorer 把视图建完 ——
        /// 本机实测从起进程到认领 0.5~1.3 秒。原来占着格等认领 ⇒ 头一批 4 个之后，
        /// 后面每个都得等前一个加载完才开工，实际并发度衰减成 1（用户报的
        /// 「它是等一批加载完，再处理另一批，不应该是动态窗口的并发吗」就是它）。
        ///
        /// 这一格管的是**起进程**（真正的资源开销在这儿），跟「谁认到哪个窗口」无关：
        /// 窗口认领是各标签自己按地址栏内容认的（见 `EmbedApi.FindNewCab`），同时多漂几个不会认错。
        ///
        /// **450ms**（2026-09-25 从 900 收紧）：900 是按「窗口最晚 1.3 秒出现」配的，那前提是
        /// 头一批 explorer 一起抢 CPU。现在「优先那个」独占着先起、落定之后其余才排队
        ///（见 `StartRestLaunches` 的 waitForHead），所以这一格只管**其余标签多快排完** ——
        /// 收紧到 450 只是把排队的那几个提前半格，不会去抢用户正在等的那个。
        /// 实测（`probe/restore_timeline.py`，9 个记忆标签）：第一个标签可用 **1.6 秒**，
        /// 全部落定冒 11.4 秒；900 那会儿第一个要 4.5 秒（当时「优先那个」还跟其余一起挤）。
        /// ⚠ 真要再调之前先量一遍：别再把它调回去当「防抢 CPU」的旋钮，那个旋钮现在在 waitForHead 上。
        /// </summary>
        private const int SpawnSlotMs = 450;

        /// <summary>起进程的格到点放行：队列里还有人时每 200ms 推一把（见 <see cref="SpawnSlotMs"/>）。</summary>
        private readonly Timer pumpTimer = new Timer();

        /// <summary>
        /// 预热好的备用窗口（已经嵌好、藏在容器里不显示）。
        /// 用户报「新建标签页反应慢」—— 慢的就是「起 explorer 进程 + 等它加载」这两步，
        /// 它们跟用户按没按 + 号毫无关系，所以提前做掉。
        /// </summary>
        private ExplorerHost reserve;
        /// <summary>备用窗口已经加载好了（可以用了）。</summary>
        private bool reserveReady;

        /// <summary>
        /// 备用窗口被「导航复用」之后，等它真的换到目标目录再露面的那一小段（见 <see cref="UseReserve"/>）。
        /// 不等的话，点书签会先闪一下它原来那个目录（此电脑）、再换成目标 —— 那一下很难看。
        /// </summary>
        private ExplorerHost revealPending;
        private string revealWant;
        private DateTime revealAt;
        /// <summary>等它换目录最多等多久；到点还没换过来就先露面（宁可有一下过渡，也别让用户干等）。</summary>
        private const int RevealMaxMs = 1500;
        /// <summary>查「换过来没有」的节拍。只读一次清单项的 LocationURL，很便宜。</summary>
        private System.Threading.Timer revealBeat;
        /// <summary>0/1：上一拍还没轮到就别再排（否则界面卡一下会攒出一串，见 <see cref="revealBeat"/>）。</summary>
        private int revealPosting;
        /// <summary>已经问过几拍了（只在头几拍写日志，见 <see cref="OnRevealTick"/>）。</summary>
        private int revealTick;

        /// <summary>
        /// 连着几次「抓不到备用窗口的 shell 清单项」。抓不到时要白等一个超时（见 `ExplorerHost` 里
        /// 那一段），而等待期间 `LaunchInFlight` 一直是真 —— 那会顺带把「收编用户新开的窗口」也推迟。
        /// 所以连栽两次就不再试了，退回「备用窗口只给此电脑用」那套（预热也快回来）。
        /// </summary>
        private int shellTargetMisses;

        /// <summary>
        /// 预热失败之后的冷却期到什么时候（见 <see cref="CanWarm"/>）。
        /// 没有它的话「起失败 → 立刻再起」就是个死循环：起一次失败要干等 25 秒超时，
        /// 于是每 25 秒白烧一个 explorer 进程。
        /// </summary>
        private DateTime warmRetryAt = DateTime.MinValue;

        /// <summary>
        /// 切完标签等一会儿再收非激活标签的内存（见 TrimInactiveTabs）。
        /// 用延时是为了别在 Ctrl+Tab 快速来回切时反复「收了又读回来」——
        /// 那比不省内存还糟（每次切回来都要重新缺页）。
        /// </summary>
        private readonly Timer trimTimer = new Timer();

        /// <summary>
        /// 「临时摊开侧边栏」那个定时器（Ctrl+Shift+B 这类看不见的开关给个反馈，见 PeekPane）。
        /// 每隔 `peekTickMs` 检查一次：鼠标不在窗格上就收回去，在就撒手（归正常的进出跟踪管）。
        /// </summary>
        private readonly Timer peekTimer = new Timer();
        private int peekTicks;
        private const int PeekTickMs = 160;

        /// <summary>窗口起来后 Hub 同步过一次书签栏设置了 —— 第一次不算「用户在按」（见 SetFavBarOn）。</summary>
        private bool favBarEverSet;

        /// <summary>这个窗口算哪张虚拟桌面（Hub 的登记键）。窗口被挪到别的桌面时 Hub 会改掉它。</summary>
        internal string DesktopKey { get; set; }

        /// <summary>真退出中（托盘「退出」/`--quit`/系统关机），别再拦关闭。</summary>
        internal bool Quitting { get; set; }

        /// <summary>自己做的窗口边框厚度（只在不最大化时有）。</summary>
        private readonly int ResizeBorder;

        /// <summary>
        /// 没记过尺寸时的窗口大小（也是托盘菜单「恢复默认窗口位置和大小」用的那一份）。
        /// ⚠ 名字别叫 `DefaultSize` —— 那是 `Form` 的 protected virtual 成员，撞名会出 CS0114 警告，
        /// 而且看起来像是在重写框架的默认尺寸逻辑，其实完全无关。
        /// </summary>
        private static Size DefaultWinSize { get { return new Size(Px(1200), Px(760)); } }
        private bool inLayout;
        /// <summary>窗口是不是失活的 —— 外壳配色跟着它换（见 SetInactive）。</summary>
        private bool inactive;

        public EmbedForm(DesktopHub owner, string desktopKey)
        {
            hub = owner;
            DesktopKey = desktopKey;

            Text = "此电脑";
            BackColor = Theme.Chrome;   // 无边框后，四周那圈就是这个色，当边框用（深色下 = 纯黑，跟标签条同色）
            Size = DefaultWinSize;
            MinimumSize = new Size(Px(640), Px(420));
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            Icon = ShellIcon.AppIcon(false);   // 任务栏 / Alt+Tab 用程序自己的图标

            // 无边框 + **标签条顶在最上面**。
            // 原来上面还有一条自绘标题栏（模仿资源管理器的快速访问工具栏），删掉了：
            // 那条里的按钮是「我们自己画的图标 + 把快捷键派给 explorer」，实测点了没反应；
            // 而真正的命令本来就在嵌进来的 explorer 自己的功能区里（文件/主页/共享/查看），一模就响。
            // 删掉之后窗口多出 30 像素给内容，窗口按钮并在标签条最右边。
            FormBorderStyle = FormBorderStyle.None;
            ResizeBorder = Px(4);
            SyncChrome();

            tabStrip = new TabStrip();
            tabStrip.WindowButtonClicked += OnWindowButtonClicked;
            tabStrip.TabClicked += delegate(object s, int i) { Activate(i); };
            tabStrip.TabCloseClicked += delegate(object s, int i)
            {
                Diag.Step("EmbedForm: 点击标签关闭按钮 idx=" + i);
                CloseTab(i);
            };
            tabStrip.TabMiddleClicked += delegate(object s, int i)
            {
                Diag.Step("EmbedForm: 中键关闭标签 idx=" + i);
                CloseTab(i);
            };
            tabStrip.NewTabClicked += OnNewTabClicked;
            // 右侧那排：齿轮（设置）/ 历史 / 恢复关闭 / 书签栏
            tabStrip.ToolClicked += OnToolClicked;
            // 拖标签排序：先把 `hosts` 挪到同一个位置 ——
            // 两边顺序一旦错位，点标签就会切到别的标签上的文件夹（见 OrderMoved 的说明）。
            tabStrip.OrderMoved += delegate(TabStrip from, int f, int t) { MoveHostOnly(f, t); };
            // 标签右键（复制名称 / 复制完整路径 / 关闭）
            tabStrip.TabRightClicked += delegate(object s, int i)
            {
                int idx = i;
                Diag.Step("EmbedForm: 标签右键 idx=" + idx);
                Defer(delegate { ShowTabMenu(idx); });
            };
            // 标签条空白处右键（把三个功能也放一份在这儿）
            tabStrip.BlankRightClicked += delegate(Point p)
            {
                Diag.Step("EmbedForm: 标签条空白处右键 " + p.X + "," + p.Y);
                blankOwner = tabStrip;
                blankAt = p;
                Defer(ShowBlankMenu);
            };
            // 标签条上滚轮 = 切换前后标签页（用户）
            tabStrip.TabWheel += delegate(int delta)
            {
                CycleTab(delta > 0 ? -1 : 1);
            };

            favBar = new FavBar();
            favBar.ItemClicked += delegate(FavNode nd)
            {
                Diag.Step("EmbedForm: 书签 -> " + nd.Path);
                // ★ 首帧就把**书签上那个名字**摆上去（起 explorer 实测 0.7~1.8 秒，标签条上显示
                //   「打开中…」还是显示书签名，直接决定用户觉得快不快）—— 跟还原记忆标签同一招。
                NewTab(nd.Path, nd.Display, true);
            };
            // 顶上 / 最左边那枚书签图标：
            //   横排 = 开**书签管理器**（用户：原来点是开数据目录，改成「管理书签」）；
            //   竖排 = 摊开 / 收起**书签段** —— 用户：「书签页可展开收缩…这样那个单独书签按钮也可以去掉了」，
            //          所以竖排的工具行里不再放书签那枚星（见 TabStrip.EnsureLayoutV），
            //          管理器挪到这一行的右键菜单里（ManageRequested）。
            favBar.LeadClicked += delegate
            {
                if (verticalOn)
                {
                    Diag.Step("EmbedForm: 竖排书签标题 -> " + (favBarOn ? "收起" : "摊开"));
                    // 走 Hub：要同时改设置、刷托盘菜单、刷所有窗口（跟那颗星同一个入口）
                    if (hub != null) hub.SetFavBar(!favBarOn);
                    return;
                }
                Diag.Step("EmbedForm: 书签图标 -> 管理书签");
                if (hub != null) hub.OpenFavManager();
            };
            favBar.ManageRequested += delegate
            {
                if (hub != null) hub.OpenFavManager();
            };
            // 书签栏文件夹上右键「全部打开（N 书签）」（用户）
            favBar.OpenAllRequested += delegate(FavNode f) { OpenAllFromFavBar(f); };
            // 书签栏上右键「隐藏书签栏」：交给 Hub（它要同时改设置、刷托盘菜单、刷所有窗口）
            favBar.HideRequested += delegate
            {
                Diag.Step("EmbedForm: 书签栏右键 -> 隐藏");
                if (hub != null) hub.SetFavBar(false);
            };
            favBar.Visible = false;

            // ---- 垂直侧边栏的左侧窗格 ----
            // 垂直模式下**整个窗口只靠这一条**：工具按钮 / 加号 / 标签行 / 书签区位 / 窗口按钮
            // 全在它里面（顶部那条横向的整条隐藏，见 DoLayout），所以事件得接全套。
            vPane = new TabStrip();
            vPane.Vertical = true;
            vPane.Visible = false;
            vPane.MirrorFrom(tabStrip);
            // 工具 / 加号 / 窗口按钮 / 滚轮切标签：**跟横向那条共用同一份处理**
            vPane.ToolClicked += OnToolClicked;
            vPane.NewTabClicked += OnNewTabClicked;
            vPane.WindowButtonClicked += OnWindowButtonClicked;
            vPane.TabWheel += delegate(int delta) { CycleTab(delta > 0 ? -1 : 1); };
            vPane.OrderMoved += delegate(TabStrip from, int f, int t) { MoveHostOnly(f, t); };
            vPane.TabClicked += delegate(object s, int i) { Activate(i); };
            vPane.TabCloseClicked += delegate(object s, int i)
            {
                Diag.Step("EmbedForm: 垂直窗格点关闭按钮 idx=" + i);
                CloseTab(i);
            };
            vPane.TabMiddleClicked += delegate(object s, int i)
            {
                Diag.Step("EmbedForm: 垂直窗格中键关闭标签 idx=" + i);
                CloseTab(i);
            };
            vPane.TabRightClicked += delegate(object s, int i)
            {
                int idx = i;
                Diag.Step("EmbedForm: 垂直窗格标签右键 idx=" + idx);
                Defer(delegate { ShowTabMenu(idx); });
            };
            vPane.BlankRightClicked += delegate(Point p)
            {
                Diag.Step("EmbedForm: 垂直窗格空白处右键 " + p.X + "," + p.Y);
                blankOwner = vPane;
                blankAt = p;
                Defer(ShowBlankMenu);
            };
            // 窗格顶部那枚图钉 = 「折叠窗格」开关（Edge 那个）。改的是全局设置，交给 Hub 广播。
            vPane.PinClicked += delegate
            {
                Diag.Step("EmbedForm: 垂直窗格图钉 -> 折叠窗格开关");
                if (hub != null) hub.SetVTabCollapse(!Settings.VTabsCollapse);
            };
            // 鼠标进出窗格 = 「临时展开成完整样式」的判据（Edge 那套折叠窗格）
            vPane.MouseEnter += delegate { PaneMouseMoved(true); };
            vPane.MouseLeave += delegate { PaneMouseMoved(false); };
            // ⚠ 书签段是**盖在窗格留出来的那一块上**的另一个控件，视觉上属于窗格 ——
            //   不把它算进来，鼠标一挪到书签上窗格就当着人的面缩回去（点都点不着）。
            favBar.MouseEnter += delegate { PaneMouseMoved(true); };
            favBar.MouseLeave += delegate { PaneMouseMoved(false); };

            content = new Panel();
            content.BackColor = Theme.Chrome;

            // 位置全部手算（DoLayout）：无边框窗口的四周留一圈自己的边框，
            // 顶部就是标签条（原来上面的自绘标题栏已经删掉了）。
            // 底部**不再有状态栏**（用户：最下面的文件夹名去掉，标签上已经显示了）。
            Controls.Add(tabStrip);
            Controls.Add(favBar);
            Controls.Add(content);
            Controls.Add(vPane);
            // 垂直窗格要盖在内容之上：折叠窗格开着时它「临时摊开」那一下比占位宽，
            // 摊开的那截是**盖在内容上**的（Edge 也这么干）——见 DoLayout 里的 slot / show。
            vPane.BringToFront();

            ApplyTheme();
            Theme.Changed += delegate { OnThemeChanged(); };
            // 收在 ApplyVertical 里：它会把「垂直侧边栏 / 折叠窗格」两个设置读出来、
            // 把窗格和顶部那条的状态摆对，最后调 DoLayout（这三件事必须一起做，分两处迟早漏一处）
            ApplyVertical();

            // 滚轮这件事登记给全局钩子（内容区是跨进程嵌进来的窗口，我们的窗体收不到它的滚轮消息）。
            // 回调都在**钩子线程**上被调用 —— 这里只读状态 / 往 UI 线程投递，绝不动界面。
            WheelRouter.Register(new WheelTarget
            {
                Form = Handle,
                TabStrip = tabStrip.Handle,
                // 垂直侧边栏那条也是「标签条」：滚轮落在窗格里走的还是切标签（见 WheelHook）。
                Pane = vPane.Handle,
                FavBar = favBar.Handle,
                // ⚠ 这两个都可能在**钩子线程**上被求值，所以只能读已算好的状态（见 WheelHook 的三条约束）。
                //   `TabBar` 只是选一下是哪一个实例，`OverflowCached` 是个 volatile bool。
                HasOverflow = delegate { return TabBar.OverflowCached; },
                // 标签条上：左半边（标签）切前后标签，右半边（那排按钮）横向滑标签（用户）
                StripWheel = delegate(int x, int delta)
                {
                    TabStrip bar = TabBar;
                    if (bar.InButtonArea(x))
                    {
                        // 没溢出就没什么可滑的 —— 别吞，让消息落到它该去的地方
                        if (!bar.OverflowCached) return false;
                        Defer(delegate { bar.ScrollTabsBy(delta > 0 ? -TabScrollStep : TabScrollStep); });
                        return true;
                    }
                    Defer(delegate { CycleTab(delta > 0 ? -1 : 1); });
                    return true;
                },
                ScrollStrip = delegate(int delta) { Defer(delegate { TabBar.ScrollTabsBy(delta > 0 ? -TabScrollStep : TabScrollStep); }); }
            });

            // 起进程的并发窗口按**时间**放行，所以得有个人定期推一把队列
            //（放行本身不产生事件，光靠 LaunchDone 推不动它，见 SpawnSlotMs）。
            pumpTimer.Interval = 200;
            // 每 200ms 推一次，**推什么由 PumpLaunch 自己决定**（放行到点的格 / 把预热补上），
            // 停不停也在它那儿判 —— 别在这儿拿「队列空不空」提前停：
            // 队列空之后还有一件周期性的事（备用窗口还没备好），见 PumpLaunch 尾部。
            pumpTimer.Tick += delegate { PumpLaunch(); };

            // 备用窗口导航之后，等它换到目标目录再露面（见 UseReserve 里那段）。
            // 40ms 一问：这一问只是读一次清单项的 LocationURL（跨进程的一次属性读），很便宜；
            // 用它而不是用标签那条 500ms 心跳，是因为心跳太粗 —— 那会让新标签白等半秒才露面。
            // ⚠ 定时器必须用**后台**的 + `BeginInvoke`，不能用 `Timer`：`WM_TIMER` 是低优先级消息，
            //   界面一忙就被饿死（实测第一拍迟到 **799ms** —— 一个 40ms 的表本该 40ms 就响，那一拍
            //   直接变成「点书签 800ms」的全部差额，且它既不阻塞、也不报错，只能靠打点看出来）。
            //   `BeginInvoke` 交的是投递队列，界面只要还在抽消息就一定轮得到。
            //   ⚠ 读 `LocationURL` 仍然在界面线程做（跨进程 COM，不能在别的线程用同一个代理）。
            revealBeat = new System.Threading.Timer(delegate
            {
                if (System.Threading.Interlocked.CompareExchange(ref revealPosting, 1, 0) != 0) return;
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        try { OnRevealTick(); }
                        finally { revealPosting = 0; }
                    });
                }
                catch { revealPosting = 0; }   // 窗体句柄还没建 / 正在销毁
            }, null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);

            // 非激活标签的内存：切完标签 3 秒后收一次（见 TrimInactiveTabs）。
            trimTimer.Interval = 3000;
            trimTimer.Tick += delegate
            {
                trimTimer.Stop();
                TrimInactiveTabs();
            };

            // 侧边栏临时摊开一下再收（见 PeekPane）。
            peekTimer.Interval = PeekTickMs;
            peekTimer.Tick += delegate
            {
                if (--peekTicks > 0) return;
                peekTimer.Stop();
                // 到点了：鼠标还压在窗格上就让它摊着（后面归正常的进出跟踪管），否则收回去
                if (PaneHasCursor()) return;
                paneHover = false;
                DoLayout();
            };

            // 标签**不在这里开**：要等第一次现身时才知道该还原什么
            // （见 EnsureFirstTab：按本桌面记着的路径把标签摆回来，没记过才开一个「此电脑」）。
        }

        // ==================================================================
        // 无边框窗口的«窗口管理»：布局 / 边框 / 最大化
        // ==================================================================

        /// <summary>
        /// 手动布局，两种模式各走一路：
        ///   横向（默认）：标签条 →（书签栏）→ 内容；
        ///   垂直（Ctrl+Shift+,）：左栏一整列（工具行 / 标签 / 书签区 / 窗口按钮都归窗格）+ 右侧内容，
        ///   顶部那条横向的**整条隐藏** —— 窗口最上面一行直接是内容。
        /// 全部摆在内边距（DisplayRectangle）里，四周那一圈（Padding）留给我们自己做可拖拽边框 ——
        /// 只有**没有被子控件盖住**的地方，窗体的 WM_NCHITTEST 才收得到。
        /// </summary>
        private void DoLayout()
        {
            // ⚠ 构造函数里 `Size = ...` 就会触发 OnSizeChanged -> 这里，
            // 那时 tabStrip / content 还没建出来。原来靠 `titleBar == null` 挡住，
            // 自绘标题栏删掉之后必须改成判 tabStrip。
            if (inLayout || tabStrip == null || content == null) return;
            inLayout = true;
            try
            {
                Rectangle r = DisplayRectangle;

                // ---- 垂直模式：整条左栏归窗格，顶部那条横向的**整条隐藏** ----
                // 用户：「我就是觉得有一行空的很丑」—— 所以窗口最上面一行直接是内容。
                if (verticalOn && vPane != null)
                {
                    tabStrip.Visible = false;

                    // ⚠ 「占位」（slot）和「实际画多宽」（show）是两回事：折叠窗格开着时
                    //   平时只占窄窄一条，鼠标进来才临时摊开 —— 摊开那下比占位宽，盖在内容上。
                    //   要是让内容跟着一起缩，鼠标每进出一次都得 SetWindowPos 那个**跨进程**
                    //   嵌进来的 explorer 窗口，看着就会一卡一卡的。
                    int slot = PaneSlotWidth();
                    int show = PaneShowWidth();
                    vPane.Collapsed = (show < Px(120));
                    vPane.PinOn = Settings.VTabsCollapse;

                    // ---- 摊开那一下会压住内容：先抓一张底图，好给它做半透明（见 PaneGlass）----
                    // ⚠ 必须抓在**改 bounds 之前** —— 那一刻这块地儿还是内容（窗格只有 slot 宽），
                    //   抓下来才是「窗格要是透明的、底下能看见什么」。抓晚了就把窗格自己抓进去了。
                    // 只在「不压内容 -> 压内容」那一翻抓；一直压着时不再抓（再抓就是拍自己）。
                    bool over = show > slot;
                    if (over && !paneWasOver) GrabGlass(slot, show, r.Height);
                    else if (!over || glassW != show - slot || glassH != r.Height) DropGlass();
                    paneWasOver = over;

                    // 书签段摆进窗格里：先问它要多高，窗格才知道给标签区留多少。
                    // 两边共用同一套坐标（都是这个窗体的子控件），算出来的矩形直接能用。
                    // 收起态只留顶上那一行标题（点它能摊开，见 FavBar.SectionOpen）；
                    // 折叠窗格那条窄缝里连标题都放不下，整段不给（展开鼠标移到窗格上才出来）。
                    int band = 0;
                    if (favBar != null && !vPane.Collapsed)
                    {
                        favBar.Vertical = true;                       // 必须是「竖排」才能算高度
                        favBar.SectionOpen = favBarOn;
                        // 第一次摆进来时还没读过数据 —— 不先读一次就算不出它要多高。
                        // ⚠ 只读一次：书签一个都没有时 Count 恒为 0，不拿标记挡住就会每次排版都重读一遍。
                        if (!favBandLoaded) { favBandLoaded = true; favBar.Reload(); }
                        band = favBarOn ? favBar.PreferredVerticalHeight(Math.Max(0, r.Height / 3))
                                        : favBar.HeaderHeight;
                    }
                    vPane.BookmarkBand = band;
                    vPane.SetBounds(r.Left, r.Top, show, r.Height);
                    vPane.Visible = true;
                    vPane.BringToFront();

                    if (favBar != null)
                    {
                        if (band > 0)
                        {
                            Rectangle bb = vPane.BookmarkBandBounds;
                            favBar.SetBounds(r.Left + bb.Left, r.Top + bb.Top, bb.Width, bb.Height);
                            favBar.Visible = true;
                            favBar.BringToFront();    // 盖在窗格留出来的那一块上
                        }
                        else
                        {
                            favBar.Vertical = false;  // 没书签区时把它当横向那条收起来（免得它按竖排重算）
                            favBar.Visible = false;
                        }
                    }

                    content.SetBounds(r.Left + slot, r.Top, Math.Max(0, r.Width - slot), r.Height);

                    // 半透明：底图 + 每块控件自己的落点，交给它们画（见 PaneGlass / GlassPaint）。
                    // ⚠ 必须放在 favBar 摆完、位置定下来之后，否则书签段的落点算不准。
                    paneGlass.Pct = favGlass.Pct = Settings.VPaneAlpha;
                    paneGlass.Back = glassBmp;
                    paneGlass.X = slot;
                    paneGlass.Y = 0;
                    vPane.Glass = paneGlass;
                    if (favBar != null)
                    {
                        // 底图是按**窗格**坐标抓的 —— 书签段要减掉自己在窗格里的偏移（Y 是负的，往上够）
                        favGlass.Back = glassBmp;
                        favGlass.X = slot - (favBar.Left - vPane.Left);
                        favGlass.Y = -(favBar.Top - vPane.Top);
                        favBar.Glass = favGlass;
                    }
                    vPane.Invalidate();      // 半透明那层变了，得重画一遍才看得见
                    if (favBar != null && favBar.Visible) favBar.Invalidate();
                    return;
                }

                // ---- 横向模式（原来的排法）----
                tabStrip.Visible = true;
                int hTab = Px(TabStrip.StdHeight);

                // 标签条就在最上面（自绘标题栏那一行已经删了，见 TabStrip 类注释）
                int top = r.Top;
                tabStrip.SetBounds(r.Left, top, r.Width, hTab);
                top += hTab;

                // 书签栏（Ctrl+Shift+B 开）：夹在标签条和内容之间，跟浏览器一样
                if (favBar != null)
                {
                    favBar.Vertical = false;
                    if (favBarOn)
                    {
                        int hFav = Px(FavBar.StdHeight);
                        favBar.SetBounds(r.Left, top, r.Width, hFav);
                        top += hFav;
                    }
                    favBar.Visible = favBarOn;
                }

                // 内容直接吃到窗口底（以前底下还压着一条 22px 的状态栏）
                int bodyH = r.Bottom - top;
                if (bodyH < 0) bodyH = 0;

                if (vPane != null) vPane.Visible = false;
                DropGlass();      // 横排没有「盖在内容上」这回事，底图要放掉
                content.SetBounds(r.Left, top, r.Width, bodyH);
            }
            finally { inLayout = false; }
        }

        /// <summary>最大化时不留边框（那时也没法拖拽改大小，留着只会在屏幕边上多一条）。</summary>
        private void SyncChrome()
        {
            bool max = (WindowState == FormWindowState.Maximized);
            int b = max ? 0 : ResizeBorder;
            if (Padding.Left != b || Padding.Top != b || Padding.Right != b || Padding.Bottom != b)
            {
                Padding = new Padding(b);
                DoLayout();
            }
            if (tabStrip != null) tabStrip.Maximized = max;
            if (vPane != null) vPane.Maximized = max;
        }

        /// <summary>
        /// 最右边那三个窗口按钮（最小化 / 最大化还原 / 关闭）。
        /// 从自绘标题栏搬过来的 —— 标题栏没了，但那三颗还得有地方放。
        /// </summary>
        private void OnWindowButtonClicked(TabStrip.WBtn b)
        {
            switch (b)
            {
                case TabStrip.WBtn.Minimize: WindowState = FormWindowState.Minimized; break;
                case TabStrip.WBtn.Maximize:
                    WindowState = (WindowState == FormWindowState.Maximized)
                        ? FormWindowState.Normal : FormWindowState.Maximized;
                    MarkDirty();   // 最大化 / 还原也算尺寸变了（记的是还原后那块地，见 BoundsString）
                    break;
                case TabStrip.WBtn.Close: HideToTray(); break;   // 收进托盘，不退进程
            }
        }

        /// <summary>「+」新建标签页 = 开一个「此电脑」，**不是**复制当前标签（用户报的 bug 4）。</summary>
        private void OnNewTabClicked(object sender, EventArgs e)
        {
            Diag.Step("EmbedForm: 点击新建标签");
            NewTab(ExplorerView.ThisPcPath);
        }

        /// <summary>
        /// 右侧那排工具按钮（齿轮 / 历史 / 恢复关闭 / 书签栏）。
        /// 横向那条和垂直窗格**共用这一个处理** —— 两边各写一份，迟早会有一边漏改。
        /// </summary>
        private void OnToolClicked(TabStrip.Tool t)
        {
            switch (t)
            {
                case TabStrip.Tool.Settings:
                    Diag.Step("EmbedForm: 点击设置按钮");
                    Defer(ShowSettingsWindow);
                    break;
                case TabStrip.Tool.History:
                    Diag.Step("EmbedForm: 点击历史按钮");
                    Defer(ShowHistoryMenu);
                    break;
                case TabStrip.Tool.Reopen:
                    Diag.Step("EmbedForm: 点击恢复关闭按钮");
                    ReopenClosedTab();
                    break;
                case TabStrip.Tool.Fav:
                    Diag.Step("EmbedForm: 点击书签栏按钮");
                    if (hub != null) hub.SetFavBar(!favBarOn);
                    break;
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            SyncChrome();
            DoLayout();
        }

        // ==================================================================
        // 记住窗口位置和大小（用户：完全退出后，下次照原样打开）
        //
        // 真源在 `desktops.json` 里**每张桌面**的 `bounds`，不另开一份，理由两条：
        //   ① 每张虚拟桌面的窗口各记各的，互相不打架；
        //   ② 退出时 `DesktopHub.SaveNow` 本来就要遍历所有活着的窗口写记忆，
        //      顺手把 `BoundsString` 写进去即可 —— 不必为「记住尺寸」再加一条保存链路。
        // 格式 `x,y,w,h`（屏幕像素），最大化时末尾再加一个 `1`（好知道下次该不该直接最大化）。
        // 关掉设置里的「记住窗口位置和大小」= 既不还原也不写回，记忆里那份原样留着，随时再打开。
        // ==================================================================

        /// <summary>
        /// 要记进记忆的窗口位置大小。
        /// ⚠ 这个窗口**没真给用户看过**就返回 null —— 启动时预建、一直没露面的窗口，
        /// 它的 Bounds 是构造函数里的默认值。照写的话，一次保存就会把用户上次调好的尺寸洗掉。
        /// </summary>
        internal string BoundsString
        {
            get
            {
                if (IsDisposed || Disposing || !windowShown) return null;
                // 最大化 / 最小化时 `Bounds` 是整屏（或最小化的那个怪矩形），要的是「还原后该占的那块地」
                Rectangle r = (WindowState == FormWindowState.Normal) ? Bounds : RestoreBounds;
                if (r.Width <= 0 || r.Height <= 0) return null;
                string s = r.X + "," + r.Y + "," + r.Width + "," + r.Height;
                if (WindowState == FormWindowState.Maximized) s += ",1";
                return s;
            }
        }

        /// <summary>第一次现身时把上次退出时的位置和大小摆回去（只做一次）。</summary>
        private void ApplyRememberedBounds()
        {
            if (boundsDone) return;
            boundsDone = true;
            if (!Settings.WindowSize)
            {
                Diag.Step("窗口: 设置里没开「记住窗口位置和大小」-> 用默认尺寸");
                return;
            }
            DesktopMemory.Bucket b = (hub == null) ? null : hub.MemoryOf(DesktopKey);
            if (b == null || string.IsNullOrEmpty(b.Bounds)) return;

            string[] p = b.Bounds.Split(',');
            int x, y, w, h;
            if (p.Length < 4 ||
                !int.TryParse(p[0], out x) || !int.TryParse(p[1], out y) ||
                !int.TryParse(p[2], out w) || !int.TryParse(p[3], out h))
            {
                Diag.Log("窗口: 记忆里的 bounds 读不动，忽略：" + b.Bounds);
                return;
            }
            // 窗口太小（老版本记下的、或者被手改坏了）不接受：至少得有最小尺寸
            if (w < MinimumSize.Width || h < MinimumSize.Height) return;

            // 显示器拔了、换了分辨率、分辨率调小了之后，那个位置可能整个跑到屏幕外 ——
            // 至少要有 80×80 落在某块屏幕的**工作区**里才认，否则宁可回到默认位置。
            Rectangle r = new Rectangle(x, y, w, h);
            bool onScreen = false;
            foreach (Screen sc in Screen.AllScreens)
            {
                Rectangle it = Rectangle.Intersect(sc.WorkingArea, r);
                if (it.Width >= Px(80) && it.Height >= Px(80)) { onScreen = true; break; }
            }
            if (!onScreen)
            {
                Diag.Step("窗口: 记忆里的位置已经不在任何屏幕上了 -> 用默认位置");
                return;
            }

            if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
            Bounds = r;
            bool max = (p.Length >= 5 && p[4].Trim() == "1");
            Diag.Step("窗口: 还原位置和大小 " + b.Bounds);
            if (max) WindowState = FormWindowState.Maximized;
        }

        /// <summary>
        /// 托盘菜单「恢复默认窗口位置和大小」：**把记着的那份忘掉**（下次启动也用默认的），
        /// 然后把窗口摆回默认大小并居中到当前显示器。
        /// </summary>
        internal void RestoreDefaultBounds()
        {
            Diag.Step("窗口: 恢复默认位置和大小（并忘掉记忆里的那份）");
            if (hub != null) hub.ForgetBounds(DesktopKey);

            if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
            Rectangle wa = Screen.FromHandle(Handle).WorkingArea;
            Size sz = DefaultWinSize;
            int w = Math.Min(sz.Width, wa.Width), h = Math.Min(sz.Height, wa.Height);
            Size = new Size(w, h);
            Location = new Point(wa.Left + (wa.Width - w) / 2, wa.Top + (wa.Height - h) / 2);
            windowShown = true;
            MarkDirty();
        }

        /// <summary>用户拖完 / 拉完窗口大小（WM_EXITSIZEMOVE）—— 攒一下写进记忆。</summary>
        protected override void OnResizeEnd(EventArgs e)
        {
            base.OnResizeEnd(e);
            MarkDirty();
        }

        protected override void WndProc(ref Message m)
        {
            // 系统换「应用模式」时会广播 WM_SETTINGCHANGE("ImmersiveColorSet")。
            // 只有「跟随系统」这一档要管 —— 强制浅/深的时候系统爱怎么变都不关我们事。
            if (m.Msg == WM_SETTINGCHANGE && m.LParam != IntPtr.Zero)
            {
                try
                {
                    string s = Marshal.PtrToStringUni(m.LParam);
                    if (string.Equals(s, "ImmersiveColorSet", StringComparison.OrdinalIgnoreCase)
                        && Settings.Color == Settings.ColorMode.System)
                    {
                        Diag.Step("EmbedForm: 系统颜色变了 -> 重读注册表");
                        Theme.Reload();
                        DarkMode.SetEnabled(Theme.IsDark);
                        Theme.RaiseChanged();     // 各控件的 Theme.Changed 会自己重画
                    }
                }
                catch { }
            }
            if (m.Msg == WM_GETMINMAXINFO)
            {
                base.WndProc(ref m);
                // 无边框窗口最大化默认按«整块屏»算，会把任务栏一起盖掉 —— 改成显示器工作区。
                EmbedApi.ClampMaxToMonitorWork(Handle, m.LParam);
                return;
            }
            if (m.Msg == WM_NCHITTEST && ResizeBorder > 0)
            {
                base.WndProc(ref m);
                if (m.Result.ToInt32() == HTCLIENT)
                {
                    // 屏幕坐标在 lParam 的低/高 16 位，按**有符号**取（多显示器会有负坐标）
                    long lp = m.LParam.ToInt64();
                    Point sp = new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
                    Point p = PointToClient(sp);
                    int b = (WindowState == FormWindowState.Normal) ? ResizeBorder : 0;
                    int w = ClientSize.Width, h = ClientSize.Height;
                    bool left = p.X < b, right = b > 0 && p.X >= w - b;
                    bool top = b > 0 && p.Y < b, bottom = b > 0 && p.Y >= h - b;
                    int hit = 0;
                    if (top && left) hit = HTTOPLEFT;
                    else if (top && right) hit = HTTOPRIGHT;
                    else if (bottom && left) hit = HTBOTTOMLEFT;
                    else if (bottom && right) hit = HTBOTTOMRIGHT;
                    else if (left) hit = HTLEFT;
                    else if (right) hit = HTRIGHT;
                    else if (top) hit = HTTOP;
                    else if (bottom) hit = HTBOTTOM;
                    if (hit != 0) m.Result = new IntPtr(hit);
                }
                return;
            }
            if (m.Msg == WM_NCACTIVATE)
            {
                // 激活 / 失活：把「未激活」状态发给自绘的标题栏和标签条。
                // wParam 非 0 = 正在激活（-1 那种「不要重画」也算激活）。
                SetInactive(m.WParam == IntPtr.Zero);
            }
            base.WndProc(ref m);
        }

        private void ApplyTheme()
        {
            // 窗口可能已经被销毁（多窗口之后 Theme.Changed 的订阅者不止一个，没法逐条退订），
            // 对着已释放的控件设颜色会抛 ObjectDisposedException。
            if (IsDisposed || Disposing) return;
            // 激活 / 失活两套外壳底色（用户：主窗口也要模拟原生那套激活逻辑）
            Color chrome = inactive ? Theme.ChromeOff : Theme.Chrome;
            BackColor = chrome;
            content.BackColor = chrome;
            tabStrip.Invalidate();
            if (vPane != null) vPane.Invalidate();
            if (favBar != null) favBar.Invalidate();
        }

        /// <summary>
        /// 颜色模式变了（或系统主题变了）：自己的外壳重画 + 标题栏重设 +
        /// 把嵌进来的 explorer 逐个重新上一遍主题。
        ///
        /// ⚠ 每一次切换都要走完，**包括切回「跟随系统」**。
        /// 早先这里碰到 System 就直接 return，于是「强制深色 -> 跟随系统(而系统是浅色)」
        /// 这一步只把外壳刷成浅色、嵌进来的 explorer 还停在深色 ——
        /// 就是用户截图里那个「一半深一半浅」。
        /// System 不等于「不用管」，它只是把目标值换成「当前系统值」而已。
        /// </summary>
        private void OnThemeChanged()
        {
            if (IsDisposed || Disposing) return;
            ApplyTheme();
            Theme.ApplyTitleBar(Handle);
            foreach (ExplorerHost h in hosts)
            {
                if (h != null) h.Restyle();
            }
            // 预热好的备用窗口不在 hosts 里，但它也是嵌好的真窗口，一样要跟上。
            if (reserve != null) reserve.Restyle();
            // 容器自己的底：嵌进来那块我们刷不到（别人的进程），至少别让接缝露旧色。
            try { content.Invalidate(true); } catch { }
        }

        /// <summary>外面（Hub）改完设置让我重刷外观。</summary>
        internal void ApplyThemeNow()
        {
            if (IsDisposed || Disposing) return;
            ApplyTheme();
            Theme.ApplyTitleBar(Handle);
        }

        /// <summary>标签宽度 / 自适应改了：标签条每次重算布局，invalidate 一下就行。</summary>
        internal void RefreshTabs()
        {
            if (IsDisposed || Disposing) return;
            tabStrip.Invalidate();
            if (vPane != null) vPane.Invalidate();
        }

        // ==================================================================
        // 垂直侧边栏（用户：打开/关闭用 Ctrl+Shift+,；样式参考 Edge 的折叠窗格）
        //
        // 两个控件：横向的那条（tabStrip）和垂直的左栏（vPane）。标签模型只有一份，
        // 挂在 tabStrip 上，vPane 只是镜像它 —— 垂直模式下 tabStrip **整条隐藏**，
        // 工具 / 加号 / 标签 / 窗口按钮全部由 vPane 自己排（见 TabStrip.EnsureLayoutV）。
        // ==================================================================

        /// <summary>标签现在画在哪个控件上（菜单锚点 / 坐标换算都用它）。</summary>
        private TabStrip TabBar { get { return (vPane != null && verticalOn) ? vPane : tabStrip; } }

        /// <summary>
        /// 折叠态窗格宽度（逻辑像素）：**只比一个图标格子宽几个像素**。
        /// 折叠态整条只有顶上「＋」和底下「×」两颗，中间是图标版的标签行（见 TabStrip.EnsureLayoutV），
        /// 所以宽度 = 一个图标格子 + 两侧各一丁点留白就够 —— 用户：「未展开时太宽」。
        /// 30 逻辑 ≈ 45 物理像素，跟原生标题栏那条黑的一样高（实测 43 物理像素 / 150% 缩放），
        /// 图标列左右各留 3 物理像素，不至于贴着边。
        /// ⚠ 这个值**不是随便调的**：`TabStrip.EnsureLayoutV` 拿它算图标列的 x，
        ///   展开态虽然窗格宽得多，但 ＋ / × / 窗口按钮都挂在同一列上 —— 改了这里，两边一起动。
        /// </summary>
        internal const int PaneCollapsedL = 30;

        /// <summary>
        /// 垂直窗格的**占位宽度** —— 真正从内容里挖走的那块。
        /// 折叠窗格开着时只占窄窄一条（平时收着），关了就一直占满。
        /// </summary>
        private static int PaneSlotWidth()
        {
            return Settings.VTabsCollapse ? Px(PaneCollapsedL) : Px(210);
        }

        /// <summary>
        /// 垂直窗格**现在实际画多宽**。
        /// 折叠窗格开着时：鼠标不在窗格里就收缩成「纯图标」（用户要的那一条），鼠标一进来临时展开成
        /// 「图标 + 标题 + 书签段」；关了就是一直展开。
        /// </summary>
        private int PaneShowWidth()
        {
            int full = Px(210);
            if (!Settings.VTabsCollapse) return full;
            return paneHover ? full : Px(PaneCollapsedL);
        }

        /// <summary>
        /// 侧边栏「摊开、盖在内容上」那一下的半透明（`Settings.VPaneAlpha`，100 = 不透明）。
        ///
        /// 做法：摊开**之前**从屏幕上抓一张那块地儿的图（那一刻还是内容），
        /// 画的时候先铺底图、再把自己的画按不透明度盖上去（见 `PaneGlass` / `GlassPaint`）。
        /// 为什么不用 `WS_EX_LAYERED`：子窗口分层合成的是**宿主窗口的背景**，
        /// 不是它压着的那个兄弟窗口（我们嵌的是别的进程的 explorer）——
        /// 实测就是「设了不透明度，背景完全看不出效果」。
        /// ⚠ 底图是**共享**的（`glassBmp`），两个视图（`paneGlass` / `favGlass`）只是引用它，
        ///   只有这里释放 —— 谁都不能自己 Dispose。
        /// </summary>
        private readonly PaneGlass paneGlass = new PaneGlass();
        private readonly PaneGlass favGlass = new PaneGlass();
        private Bitmap glassBmp;
        private int glassW, glassH;
        /// <summary>上一轮排版时窗格是不是正压着内容（用来卡「翻的那一下才抓图」）。</summary>
        private bool paneWasOver;

        private void GrabGlass(int slot, int show, int h)
        {
            if (Settings.VPaneAlpha >= 100) { DropGlass(); return; }   // 不透明就根本不用抓
            int w = show - slot;
            if (w <= 0 || h <= 0 || vPane == null || !vPane.IsHandleCreated) { DropGlass(); return; }
            if (glassBmp != null && glassW == w && glassH == h) return;
            DropGlass();
            Point sp = vPane.PointToScreen(new Point(slot, 0));
            glassBmp = EmbedApi.GrabScreen(sp.X, sp.Y, w, h);
            if (glassBmp == null) return;
            glassW = w;
            glassH = h;
        }

        /// <summary>放掉底图（切横排 / 收起窗格 / 窗口尺寸变了抓不到干净底的时候）。</summary>
        private void DropGlass()
        {
            // 先把两个控件的引用摘掉再释放 —— 反过来的话，下一次重画会去画一张已经销毁的图
            if (vPane != null) vPane.Glass = null;
            if (favBar != null) favBar.Glass = null;
            paneGlass.Back = null;
            favGlass.Back = null;
            if (glassBmp != null) { Bitmap b = glassBmp; glassBmp = null; b.Dispose(); }
            glassW = 0;
            glassH = 0;
        }

        /// <summary>
        /// 鼠标在不在「窗格这一块」里 —— 窗格本身 **或** 盖在它上面的书签段。
        /// 折叠窗格的临时展开就靠这一条：鼠标走开就缩回去，但挪到书签段上不算走开
        /// （书签段虽然盖在窗格上，可它是另一个控件，窗格自己只会收到 MouseLeave）。
        /// ⚠ 判「在不在」得拿**实际鼠标位置**再核一遍矩形，不能光信「谁发的 MouseLeave」：
        ///   书签段一收起（高度缩回只剩标题行 / 干脆 Visible=false），它的 MouseLeave 会跟着来一发，
        ///   可那会儿鼠标其实还压在窗格上 —— 照着它收缩就成了「点一下书签标题、窗格自己缩回去」，
        ///   用户报的正是这个（「明明鼠标还在窗格上」）。窗格把内容区那一截盖住是**本来就该算在里面**的。
        /// </summary>
        private void PaneMouseMoved(bool inside)
        {
            if (!verticalOn) return;
            if (!inside && PaneHasCursor()) inside = true;
            if (paneHover == inside) return;
            paneHover = inside;
            DoLayout();
        }

        /// <summary>
        /// 鼠标现在是不是压在「窗格这一块」上 —— 窗格本身 **或** 盖在它上面的书签段。
        /// ⚠ 一律拿**实际光标位置**核矩形，不信「谁发的 MouseEnter / MouseLeave」：
        ///   书签段一收起（高度缩回只剩标题行），它就会给窗格补一发 MouseLeave，
        ///   可那会儿鼠标其实还压在窗格上（用户报的「点一下书签标题、窗格自己缩回去」）。
        /// 窗格把内容区那一截盖住时，那块本来就该算「在窗格里」。
        /// </summary>
        private bool PaneHasCursor()
        {
            Point c = PointToClient(Cursor.Position);
            if (vPane != null && vPane.Visible && vPane.Bounds.Contains(c)) return true;
            if (favBar != null && favBar.Visible && favBar.Bounds.Contains(c)) return true;
            return false;
        }

        /// <summary>
        /// 把侧边栏**临时摊开**一下再收回去 —— 给「Ctrl+Shift+B」这类屏幕上看不出变化的开关一个反馈。
        /// 竖排时书签段就住在侧边栏里，侧边栏收着的话按完热键画面上纹丝不动，
        /// 用户根本分不清「是按了没生效」还是「生效了但看不见」（用户原话）。
        /// 鼠标本来就在窗格上时不用折腾（它已经摊着了）。
        /// </summary>
        private void PeekPane()
        {
            if (!verticalOn || vPane == null || vPane.IsDisposed) return;
            if (!vPane.Collapsed) return;                 // 已经摊着 / 折叠窗格关着 —— 没什么可展示的
            paneHover = true;
            DoLayout();
            peekTicks = 10;                               // 160ms x 10 ≈ 1.6 秒
            peekTimer.Stop();
            peekTimer.Start();
        }

        /// <summary>
        /// 垂直侧边栏相关的设置（模式 / 折叠窗格）变了 —— 重算外观和布局。
        /// Hub 改完设置会广播给每一个窗口（见 DesktopHub.VerticalAll）。
        /// </summary>
        internal void ApplyVertical()
        {
            if (IsDisposed || Disposing) return;
            verticalOn = Settings.VTabs;
            paneHover = false;
            if (tabStrip != null)
            {
                // 垂直模式下顶部那条**整条不显示**（不是留着当标题栏）：窗口最上面一行直接是内容
                tabStrip.Visible = !verticalOn;
                tabStrip.Maximized = (WindowState == FormWindowState.Maximized);
            }
            if (vPane != null)
            {
                vPane.PinOn = Settings.VTabsCollapse;
                vPane.FavBarOn = favBarOn;      // 竖排里那枚书签星标也得是实心
                vPane.Maximized = (WindowState == FormWindowState.Maximized);
                vPane.Visible = verticalOn;
                if (verticalOn)
                {
                    vPane.BringToFront();
                    vPane.Invalidate();
                }
            }
            DoLayout();
        }

        // ==================================================================
        // 现身 / 收托盘
        // ==================================================================

        /// <summary>把窗口句柄建出来（隐藏启动时也要，否则 BeginInvoke 不好使）。</summary>
        public void ForceHandle()
        {
            IntPtr h = Handle;
            GC.KeepAlive(h);
        }

        /// <summary>
        /// Win+E / 托盘 / 双击 exe 的统一入口：把窗口摆出来，并按需要开一个新标签。
        ///
        /// **不再搬虚拟桌面了**：这个窗口本来就属于它那张桌面（由 Hub 按当前桌面创建/认领），
        /// 原来那套「先搬桌面再抢前台」连带把「按 Win+E 被拽到别的桌面」这个毛病一起删掉了。
        /// 这里只留一道保险：万一它确实落在别的桌面上，就**只显示、不抢前台**。
        /// </summary>
        public void ShowForUser(bool newTab)
        {
            Diag.Step("EmbedForm: ShowForUser newTab=" + newTab + " 桌面=" + DesktopKey
                      + " 模式=" + (hub == null ? "?" : Settings.Text(hub.Capture)));
            try
            {
                bool migrating = (hub != null && hub.Capture == Settings.CaptureMode.Migrate);
                if (migrating)
                {
                    // v1.0.0 那套：全进程就这一个窗口，Win+E 时把它**搬到当前桌面**再显。
                    // 搬到才算数 —— 搬不过去就只显示、不抢前台，免得反而把人拽走。
                    VdOutcome o = VirtualDesktop.EnsureOnCurrentDesktop(Handle);
                    Diag.Step("EmbedForm: 迁移模式，搬窗口结果=" + o);
                    if (o == VdOutcome.Failed)
                    {
                        if (!Visible) Show();
                        Toast.Show("打不开：窗口在别的虚拟桌面",
                            "没能把这个窗口搬到当前桌面。回到它所在的桌面再试。");
                        return;
                    }
                }
                else if (!VirtualDesktop.IsOnCurrentDesktop(Handle))
                {
                    Diag.Step("EmbedForm: 窗口不在当前桌面 -> 不显示、不抢前台（不切走）");
                    Toast.Show("打不开：窗口在别的虚拟桌面",
                        "这个窗口属于另一张虚拟桌面。回到那张桌面再按 Win+E。");
                    return;
                }

                ApplyRememberedBounds();
                if (!Visible) Show();
                windowShown = true;
                if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                ActivateToFront();

                EnsureFirstTab();

                if (newTab)
                {
                    // 去重：这个路径已经开着（比如一直按 Win+E），切过去就行，
                    // 别再刷一屏「此电脑」—— 原生 Win10 也是复用已有窗口。
                    int dup = IndexOfPath(ExplorerView.ThisPcPath);
                    if (dup >= 0)
                    {
                        Diag.Step("EmbedForm: 「此电脑」已经开着 idx=" + dup + "，切过去（不新建）");
                        Activate(dup);
                    }
                    else NewTab(ExplorerView.ThisPcPath);
                }
                else if (activeIndex >= 0) Activate(activeIndex);

                MarkDirty();
            }
            catch (Exception ex) { Diag.Log("EmbedForm: ShowForUser 失败 " + ex.Message); }
        }

        /// <summary>
        /// 「用户在外面开了一个文件夹，窗体现在在我们手里」—— 需要现身把它露出来。
        /// 两个入口：① 收编用户自己开出来的窗口（`AdoptWindow`）；② shell 的窗口「转生」
        /// 过来之后（`DesktopHub.TakeOverShellWindow`）—— 那一下同样是他刚在别的程序里点了
        /// 「打开文件夹」，窗口一样得顶到前面。
        ///
        /// 跟 `ShowForUser` 的区别（**别混用**）：
        ///   · 不 `EnsureFirstTab`（标签刚收进来就是第一个，不能再自作主张开一个「此电脑」）；
        ///   · 不判虚拟桌面（那个窗口本来就开在用户眼前，他在哪张桌面我们就在哪张）；
        ///   · 不 `newTab`（收进来的那个就是要看的那个）。
        /// 但**要**抢一下前台：他刚双击文件夹，本该有一扇窗弹到最前面；
        /// 我们把那扇窗藏了收进标签，就得由我们把窗口顶上来，不然他眼前像是「什么都没发生」。
        /// </summary>
        internal void ShowForCapture()
        {
            if (IsDisposed || Disposing) return;
            try
            {
                if (!Visible) Show();
                windowShown = true;
                if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                ActivateToFront();
                MarkDirty();
            }
            catch (Exception ex) { Diag.Log("EmbedForm: ShowForCapture 失败 " + ex.Message); }
        }

        /// <summary>
        /// 第一次现身时把**本桌面**记着的标签摆回来；没记过（或都开不了）才开一个「此电脑」。
        /// 只做一次 —— 之后这个窗口的标签就是活的，关了再按 Win+E 还是原来那些。
        /// </summary>
        private void EnsureFirstTab()
        {
            // 已经有标签就直接收工。**这句不能省**：`RestoreRememberedTabs` 在「窗口里已经有标签」时
            // 同样返回 false（它只在窗口为空时才动手），把那句 false 读成「没有记忆、得兜一个」
            // 就会**每按一次 Win+E 白开一个「此电脑」标签**（用户连按几次就刷出一屏），
            // 而调用点下面那句「已经开着、切过去」的判重在它之后，拦不住（日志里先「用掉备用标签」
            // 再「已经开着 idx=3」就是这个顺序）。只在窗口真的空着时才需要兜底。
            if (hosts.Count > 0) return;

            if (RestoreRememberedTabs()) return;
            NewTab(ExplorerView.ThisPcPath);
        }

        /// <summary>
        /// 把本桌面记着的标签摆回来，返回「真摆上了没有」。**不兜底开「此电脑」** ——
        /// 给「窗口是为了外面某个文件夹才现建出来」的那两条路用（shell 窗口转生、收编用户开的窗口）：
        /// 它们接着就会把目标标签加上，再兜一个「此电脑」就是白多一页。
        ///
        /// ⚠ 这两条路以前漏了这一步：用户看到的是「一扇只有刚才那个文件夹的窗」，
        /// 而且**退出时这 1 个标签会把记忆里原来的整套标签盖掉** —— 下次开就只剩这一个了。
        /// </summary>
        // ==================================================================
        // 记忆标签的还原拆成「摆」和「起」两步 —— 见 StageRememberedTabs / StartRestLaunches
        //
        // 为什么拆（用户报「从开始菜单打开目录，加载慢得有点离谱」时量的）：
        //   收编 / 转生那条路原来是「先把本桌面记着的 N 个标签全摆出来、全塞进启动队列，
        //   再去收用户那扇窗」：8 个标签一起起舞、UI 线程被占 400ms，而用户要的那扇窗排在
        //   队尾 —— 转生那条路得等 6 秒以上才轮到它。
        //   拆开之后：**先把名字摆出来**（判重需要它们先存在，用户第一眼也能看到完整标签条），
        //   收完用户那扇窗，**才**让其余的排进启动队列。
        // ==================================================================

        /// <summary>
        /// 第一步：把本桌面记着的标签**摆成占位**（标题/路径立刻写好，explorer 先不起）。
        /// 返回摆上了几个。⚠ 只做一次，`restored` 守着。
        /// </summary>
        internal int StageRememberedTabs()
        {
            if (restored) return 0;
            restored = true;

            DesktopMemory.Bucket b = (hub == null) ? null : hub.MemoryOf(DesktopKey);
            if (!Settings.KeepTabs)
            {
                Diag.Step("记忆: 「保留标签页」关着 -> 不还原，直接开一个「此电脑」");
                b = null;
            }
            if (b == null || b.Paths.Count == 0) return 0;

            int skipped = 0;

            // ---- 先把记忆里的值过一遍筛子，得到「真能开出来、且不重复」的那一串 ----
            // ⚠ 记忆里的值**不能直接拿去判能不能开**：地址栏给的常常是**显示名**（「视频」「此电脑」
            //   「下载」），而 `Restorable` 只认「`::` / `shell:` 前缀」或「绝对且真实存在的目录」——
            //   显示名两条都不占，会被整条跳过（用户报的「还原时有个标签报错」就是这个）。
            //   所以一律先 `Store`（显示名 → 真路径 / shell 标识）再判。
            List<string> want = new List<string>();
            for (int i = 0; i < b.Paths.Count; i++)
            {
                string p = PathRules.Store(b.Paths[i]);
                if (!PathRules.Restorable(p))
                {
                    skipped++;
                    Diag.Step("记忆: 跳过开不了的项「" + b.Paths[i] + "」（可能是个库/虚拟文件夹，没有真实路径）");
                    continue;
                }
                // 同一个路径已经有标签了就别再开一个。
                // 记忆文件里偶尔会有重复行（老版本并发开标签时写坏的），去重放在这儿最稳：
                // 不管文件脏成什么样，界面上都不会冒出两个一模一样的标签。
                if (IndexOfPath(p) >= 0)
                {
                    skipped++;
                    Diag.Step("记忆: 「" + p + "」已经有标签了，跳过重复项");
                    continue;
                }
                want.Add(p);
            }
            if (want.Count == 0) return 0;

            // **按记忆里的顺序**摆 —— 标签条从第一帧起就是用户上次离开时的样子；
            // 「谁先起」交给第二步，不再靠「先建一个再挪回去」。
            for (int i = 0; i < want.Count; i++) AddDeferredTab(want[i]);
            stagedPaths = want;
            stagedActive = PathRules.Store(b.Active);

            Diag.Step(string.Format("记忆: 桌面 {0} 摆上 {1} 个标签占位（跳过 {2} 个），explorer 等第二步再起",
                DesktopKey, want.Count, skipped));
            return want.Count;
        }

        /// <summary>
        /// 第二步：把摆好的占位按优先级真起 explorer。
        ///
        /// <paramref name="first"/> = 用户**此刻正等着**的那一个（收编 / 转生那条路传进来的路径），
        /// 它排第一个；其余按记忆里的顺序跟在后面 —— 这就是用户说的「优先处理激活标签、
        /// 延迟后续并发伪懒加载」：名字早摆好了，内容一个个填（一轮一格，见 `SpawnSlotMs`）。
        /// 不传就退回「记忆里当时选中的那个」优先。
        ///
        /// <paramref name="waitForHead"/> = 其余的**等第一个落定**再放。三个调用点（`DesktopHub`
        /// 的收编 / 转生、`RestoreRememberedTabs`）全都是「用户正在等一扇窗」—— **一律传 true**。
        /// 提前放会让十几个 explorer 一起抢 CPU，把用户等的那个的收编拖慢（实测 196ms → 687ms），
        /// 更要命的是它连「第一个可用的标签」都要拖到 4.5 秒才出来（用户盯着的正是那一个）。
        /// 真出了问题也不会干等：落定不了有 4 秒兜底（见 `StartRestFallback`）。
        /// </summary>
        internal void StartRestLaunches(string first, bool waitForHead)
        {
            if (restLaunched) return;
            if (stagedPaths == null || stagedPaths.Count == 0) return;
            restLaunched = true;

            int firstIdx = -1;
            // ★ 刚从外面收进来的那扇窗就是用户此刻在等的：先认它。
            //   （收编进来的 host 还没读过地址栏 —— `LivePath` 拿不到路径，按路径查不到它。）
            if (lastAdopted != null) { firstIdx = hosts.IndexOf(lastAdopted); lastAdopted = null; }
            if (firstIdx < 0 && !string.IsNullOrEmpty(first)) firstIdx = IndexOfPath(PathRules.Store(first));
            if (firstIdx < 0) firstIdx = IndexOfPath(stagedActive);

            if (Settings.LazyTabs)
            {
                // 真懒加载：只起选中那个，其余保持占位、点到才起（见 StartDeferred）
                if (firstIdx >= 0) StartDeferred(firstIdx);
                Diag.Step("记忆: 懒加载模式，只起第 " + (firstIdx + 1) + " 个");
                return;
            }

            if (firstIdx < 0) firstIdx = 0;
            if (hosts.Count == 0) return;
            ExplorerHost head = hosts[firstIdx];
            StartDeferred(firstIdx, true);    // 不管它排第几，先起（收编进来的不是占位，会自动跳过）

            // ★ 其余的**等它落定**再放。放早了就是十几个 explorer 一起起舞抢 CPU，
            //   它自己的收编会被拖慢（实测 196ms → 687ms —— 用户看到的就是「还是在打开中」）。
            restFirstHost = head;
            restPending = true;
            if (!waitForHead || head == null || head.Settled) { StartRestNow(); return; }
            StartRestFallback();              // 它要是一直不回来（起失败卡超时），别把其余饿死
        }

        /// <summary>
        /// 兜底：优先那个迟迟没落定（起失败卡 25 秒超时之类），照旧把其余的放出去。
        ///
        /// 4 秒而不是更久：这一步的代价是**全队列干等**。实测一次「优先那个」因为目标写错
        ///（`shell:Desktop`，见 `PathRules.virtuals` 那段）认不到窗口，10 秒里一个标签都没起来 ——
        /// 用户等的那个既没出来，其余 9 个也被按着不动。正常情况优先那个 1~3 秒就落定，
        /// 4 秒足够；真出了问题越早放越好。
        /// </summary>
        private void StartRestFallback()
        {
            if (restFallback != null) return;
            restFallback = new System.Windows.Forms.Timer();
            restFallback.Interval = 4000;
            restFallback.Tick += delegate
            {
                restFallback.Stop();
                restFallback.Dispose();
                restFallback = null;
                if (restPending) { Diag.Step("记忆: 优先那个迟迟没落定，其余标签照常出发"); StartRestNow(); }
            };
            restFallback.Start();
        }

        /// <summary>把记忆里剩余的占位标签排进启动队列（优先那个落定之后才调，见 StartRestLaunches）。</summary>
        private void StartRestNow()
        {
            if (!restPending) return;
            restPending = false;
            if (Settings.LazyTabs) return;
            for (int i = 0; i < hosts.Count; i++)
                if (hosts[i] != restFirstHost) StartDeferred(i);
            Diag.Step("记忆: 优先那个已落定，其余标签开始排队（共 " + hosts.Count + " 个）");
            // ★ 顺手把备用窗口也排到队尾（见 WarmUpQueued）：等这一批全落定再预热要十几秒，
            //   那段时间里按 `+` 都得现起 explorer。
            WarmUpQueued();
        }

        /// <summary>
        /// 把「备用窗口」直接排进**队尾**，不等这一批标签全起完（见 <see cref="StartRestNow"/>）。
        ///
        /// 为什么值当：`+` 号全靠备用窗口才「秒开」（见 <see cref="UseReserve"/>），而备用窗口
        /// 原来只有等**所有**标签都落定后 `PumpLaunch` 才去预热 —— 实测从「从开始菜单打开目录」
        /// 那一刻算起，备用窗口要 15 秒才就绪（预热在 +11.3s、就绪在 +14.9s），这十几秒里按 `+`
        /// 都得现起一个 explorer（用户报的「按+号也好慢」）。排进队尾之后它跟着这一批一起轮转，
        /// 标签结束前后就有货了。
        ///
        /// ⚠ 只在「队列里已经有活」时叫它。`CanWarm` 那条 `!LaunchInFlight` 防的是「本批标签还在
        ///   收编时又塞一个 explorer 进去抢 CPU」；排进**队尾**不存在抢在谁前面——起进程本来就是
        ///   900ms 一格按时间放行的（见 SpawnSlotMs）。
        /// </summary>
        private void WarmUpQueued()
        {
            if (reserve != null || reserveReady) return;
            if (IsDisposed || Disposing || Quitting) return;
            if (DateTime.Now < warmRetryAt) return;      // 刚失败过：别在冷却期里再来一个
            ExplorerHost h = CreateHost();
            reserve = h;
            reserveReady = false;
            h.PresetTarget(ExplorerView.ThisPcPath);
            Diag.Step("EmbedForm: 备用标签排进队尾（让 + 号早点能秒开）");
            launchQueue.Enqueue(new Launch { Host = h, Path = ExplorerView.ThisPcPath, Warm = true });
            PumpLaunch();
        }

        /// <summary>
        /// 还原记忆标签（老入口，把两步串起来）——「摆名字 + 记忆里选中那个优先起」。
        /// ⚠ 收编 / 转生那条路**不要**用它：它们要在两步之间插一次「收下用户那扇窗」，
        ///   见 `DesktopHub.AdoptWindow` / `TakeOverShellWindow`。
        /// ⚠ 它的唯一调用点是 `EnsureFirstTab`，也就是**用户刚按了 Win+E、窗口已经亮在眼前**的那一刻 ——
        ///   所以这里传 `waitForHead = true`：先让**当前选中的那个标签**独占着起，落定之后再放其余的。
        ///   （原来传 false「越早并上越好」，实测是反的：9 个 explorer 一起起舞，第一个可用的标签
        ///   要 4.5 秒才出来，而**用户盯着的正是那一个**。见 `StartRestLaunches` 的 waitForHead。）
        /// </summary>
        internal bool RestoreRememberedTabs()
        {
            if (StageRememberedTabs() == 0) return false;
            StartRestLaunches(null, true);
            if (hosts.Count == 0) return false;
            int act = IndexOfPath(stagedActive);
            Activate(act >= 0 ? act : 0);
            return true;
        }

        /// <summary>
        /// 把窗口提到前台。最小化时先还原 —— 光调 SetForegroundWindow 会被 Windows 的前台锁定
        /// 拒掉（表现就是只闪任务栏、窗口不上来）；借当前前台线程的输入队列一用才有资格。
        /// </summary>
        private void ActivateToFront()
        {
            IntPtr h = Handle;
            if (NativeMethods.IsIconic(h)) NativeMethods.ShowWindow(h, 9);   // SW_RESTORE
            else NativeMethods.ShowWindow(h, 5);                             // SW_SHOW

            uint fgThread = NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), IntPtr.Zero);
            uint myThread = NativeMethods.GetCurrentThreadId();
            bool attached = false;
            if (fgThread != 0 && fgThread != myThread)
                attached = EmbedApi.AttachThreadInput(fgThread, myThread, true);
            bool ok;
            try
            {
                NativeMethods.BringWindowToTop(h);
                ok = NativeMethods.SetForegroundWindow(h);
            }
            finally
            {
                if (attached) EmbedApi.AttachThreadInput(fgThread, myThread, false);
            }
            // 没抢到就等于「窗口上不来、只闪任务栏」——那是用户看得见的症状，所以留一行日志好对账
            Diag.Step(string.Format("EmbedForm: 顶到前台 -> {0}（前台线程 {1}，借队列 {2}）",
                ok ? "成功" : "被拒", fgThread, attached ? "是" : "否"));
        }

        /// <summary>
        /// 收进托盘。**不是退出** —— 进程留着，Win+E 才有落点，标签也原样留着。
        /// 第一次收起来时弹个气泡说明一下，免得以为程序关了。
        /// </summary>
        private void HideToTray()
        {
            Diag.Step("EmbedForm: 收进托盘（进程常驻，继续接 Win+E）");
            Visible = false;
            MarkDirty();
            // 窗口都收起来了，正是收本进程驻留内存的好时候（用户的优化 5）
            ExplorerHost.TrimSelf();
            if (!trayTipShown)
            {
                trayTipShown = true;
                Toast.Show("TabbedExplorer 还在后台", "按 Win+E 随时打开；右键托盘图标可以退出。");
            }
        }

        // ==================================================================
        // 热键 / 标签
        // ==================================================================

        /// <summary>
        /// Hub 把热键派过来（钩子那边只知道「前台是我们」，具体哪个窗口由它找）。
        ///
        /// 参数是**命令标识**（`newtab` / `closetab` / `nexttab` / `prevtab` / `history` / `reopen` /
        /// `favbar`，或 `goto:<0 基下标>`）—— 不再是「Ctrl+T」这种文本：
        /// 组合键现在可自定义（见 Hotkeys），写成文本的话这儿就得到处跟着改。
        /// </summary>
        internal void HandleHotkey(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return;

            // Ctrl+1..9 这一族带参数，单独接
            if (cmd.StartsWith("goto:", StringComparison.Ordinal))
            {
                int n;
                if (int.TryParse(cmd.Substring(5), out n)) GotoTab(n);
                return;
            }

            switch (cmd)
            {
                case "newtab": HotkeyNewTab(); return;
                case "closetab": HotkeyCloseTab(); return;
                case "nexttab": CycleTab(1); return;
                case "prevtab": CycleTab(-1); return;
                case "history":
                    Diag.Step("EmbedForm: 热键 " + Hotkeys.Combo(cmd) + " -> 历史记录");
                    if (!Visible) Show();
                    Defer(ShowHistoryMenu);
                    return;
                case "reopen":
                    Diag.Step("EmbedForm: 热键 " + Hotkeys.Combo(cmd) + " -> 恢复关闭的标签");
                    ReopenClosedTab();
                    return;
                case "favbar":
                    Diag.Step("EmbedForm: 热键 " + Hotkeys.Combo(cmd) + " -> 书签栏开关");
                    if (hub != null) hub.SetFavBar(!favBarOn);
                    return;
                case "vtabs":
                    Diag.Step("EmbedForm: 热键 " + Hotkeys.Combo(cmd) + " -> 垂直侧边栏开关");
                    if (hub != null) hub.SetVerticalTabs(!Settings.VTabs);
                    return;
            }
            Diag.Log("EmbedForm: 不认识的热键命令 " + cmd);
        }

        /// <summary>Ctrl+T：在本窗口开个新标签（不是新开一个窗口）。目标是「此电脑」（用户报的 bug 4）。</summary>
        private void HotkeyNewTab()
        {
            Diag.Step("EmbedForm: 热键 " + Hotkeys.Combo("newtab") + " -> 新标签（此电脑）");
            if (!Visible) Show();
            NewTab(ExplorerView.ThisPcPath);
        }

        /// <summary>Ctrl+1..9：跳到第 N 个标签（0 基传入）。不够那么多标签就什么都不干。</summary>
        private void GotoTab(int zeroBased)
        {
            if (hosts.Count == 0 || zeroBased < 0 || zeroBased >= hosts.Count) return;
            Diag.Step("EmbedForm: 热键 Ctrl+" + (zeroBased + 1) + " -> 切到 idx=" + zeroBased);
            Activate(zeroBased);
        }

        /// <summary>Ctrl+W：关当前标签；这是最后一个就收进托盘（不退进程）。
        /// 标签条上 Ctrl / Shift 挑了一批的话，这一下关的是**那一批**（用户 2026-09-25）。</summary>
        private void HotkeyCloseTab()
        {
            if (tabStrip.HasMultiSelection) { CloseSelectedTabs(); return; }
            int i = activeIndex >= 0 ? activeIndex : hosts.Count - 1;
            Diag.Step("EmbedForm: 热键 " + Hotkeys.Combo("closetab") + " -> 关标签 idx=" + i);
            if (i < 0) { HideToTray(); return; }
            CloseTab(i);
        }

        /// <summary>Ctrl+Tab / Ctrl+Shift+Tab：在标签之间循环（滚轮切标签也走这儿 —— 日志别写成「热键」）。</summary>
        private void CycleTab(int delta)
        {
            if (hosts.Count < 2) return;
            int n = hosts.Count;
            int i = ((activeIndex + delta) % n + n) % n;
            Diag.Step("EmbedForm: 切标签 -> idx=" + i + "（" + (delta > 0 ? "下一个" : "上一个") + "）");
            Activate(i);
        }

        /// <summary>
        /// 这个标签**现在**在哪个文件夹：优先地址栏实时值（用户在里导航过就以它为准），
        /// 还没读到时退回「我们当初让它打开的路径」。返回值已经过 PathRules 归一（`此电脑` → `::{…}`）。
        /// </summary>
        private static string LivePath(ExplorerHost h)
        {
            if (h == null) return "";
            // ⚠ 备用窗口刚被「导航复用」过去、还没换到位时，`CurrentPath` 还停在旧目录（此电脑）——
            //   这时它的路径得算**要去的那一个**，否则 `IndexOfPath` 认不出这个新标签：
            //   用户从开始菜单连开两次同一个文件夹就会开出两个标签（实测 182~411ms 内连着两次
            //   「没有对应标签」，第二次还写着「现有 7 个」—— 第一个明明已经进来了）。
            string p = h.PendingPath;
            if (string.IsNullOrEmpty(p)) p = h.CurrentPath;
            if (string.IsNullOrEmpty(p)) p = h.TargetPath;
            return PathRules.Store(p) ?? "";
        }

        /// <summary>已经有标签开着这个路径就返回它的下标，否则 -1。</summary>
        private int IndexOfPath(string path)
        {
            // 进来的是**地址栏原文**（外面传的也是地址栏里那个字符串），可能是「下载」「此电脑」
            // 这种虚拟名字；而标签里存的是 `Store` 过的那套（`shell:Downloads`）。
            // 不 Store 一次，这两边永远比不相等 —— 明明开着同一个文件夹，还要再开一个标签。
            string want = PathRules.Norm(PathRules.Store(path));
            if (want.Length == 0) return -1;
            for (int i = 0; i < hosts.Count; i++)
            {
                if (PathRules.Norm(LivePath(hosts[i])) == want) return i;
            }
            return -1;
        }

        /// <summary>
        /// 按顺序记下本窗口所有标签的路径（给 Hub 写记忆用）。
        /// **正在起 explorer 的标签也算**：那种还没嵌好、`CurrentPath` 是空的，
        /// `LivePath` 会退回「我们让它开的那个路径」——否则一次中途保存就会把还没加载完的标签从记忆里抹掉。
        /// </summary>
        internal List<string> TabPaths()
        {
            List<string> r = new List<string>();
            foreach (ExplorerHost h in hosts)
            {
                if (h == null) continue;
                string p = LivePath(h);
                if (!string.IsNullOrEmpty(p)) r.Add(p);
            }
            return r;
        }

        /// <summary>当前选中的那个标签的路径（还原时一并切过去）。</summary>
        internal string ActiveTabPath
        {
            get
            {
                if (activeIndex < 0 || activeIndex >= hosts.Count) return null;
                return LivePath(hosts[activeIndex]);
            }
        }

        private void MarkDirty()
        {
            if (hub != null) hub.MarkDirty();
        }

        /// <summary>
        /// 把第 from 个标签挪到 to —— **`hosts` 和标签条一起挪**（两边的顺序必须始终一致）。
        /// 只给「还原记忆」用：先把该激活的那个建出来（抢到串行队列的头名），再挪回它该在的位置。
        /// </summary>
        private void MoveTabSynced(int from, int to)
        {
            if (from == to) return;
            if (from < 0 || from >= hosts.Count) return;
            if (to < 0 || to >= hosts.Count) return;
            MoveHostOnly(from, to);
            tabStrip.MoveTab(from, to);
        }

        /// <summary>
        /// 只挪 `hosts`（标签条那一侧由 `TabStrip` 自己做）。
        /// ⚠ `hosts` 和标签列表的顺序**必须始终一致** —— `Activate(i)` / `SetPinned(i)` /
        ///   `hosts[i]` 全是按同一个下标同时读两边的，错位就会「点这个标签、开那个文件夹」。
        ///   拖标签排序走的是 `OrderMoved`（上层先挪 hosts，标签条再挪自己那份）。
        /// </summary>
        private void MoveHostOnly(int from, int to)
        {
            if (from == to) return;
            if (from < 0 || from >= hosts.Count) return;
            if (to < 0 || to >= hosts.Count) return;
            ExplorerHost h = hosts[from];
            hosts.RemoveAt(from);
            hosts.Insert(to, h);
        }

        /// <summary>
        /// 标签右键「置顶 / 取消置顶」（用户新增）。
        /// 置顶的标签一律排到最前面（跟浏览器一样）；取消置顶退到「置顶区」右边第一个位置 ——
        /// 不回原位，因为那一轮排序已经把原位置丢掉了，回哪儿都是猜。
        /// </summary>
        private void SetTabPinned(int idx, bool pinned)
        {
            if (idx < 0 || idx >= hosts.Count) return;
            ExplorerHost h = hosts[idx];
            if (h.Pinned == pinned) return;
            h.Pinned = pinned;
            Diag.Step("EmbedForm: " + (pinned ? "置顶" : "取消置顶") + "标签 idx=" + idx);

            // 目标位置 = 「除自己之外」的置顶标签个数。
            //   置顶：正好落在现有置顶区末尾；取消：正好是置顶区右边第一个。两边共用一个算法。
            // ⚠ 得先把 h.Pinned 改完再数 —— 但数的时候要**跳过自己**，否则刚置顶的它会被自己多数一次。
            int target = 0;
            for (int i = 0; i < hosts.Count; i++)
            {
                if (hosts[i] == h) continue;
                if (hosts[i].Pinned) target++;
            }
            MoveTabSynced(idx, target);
            SyncPins();
            MarkDirty();
        }

        /// <summary>把每个 host 的置顶标记同步到标签条（标签条只管画那枚小图钉）。</summary>
        private void SyncPins()
        {
            int n = Math.Min(hosts.Count, tabStrip.Tabs.Count);
            for (int i = 0; i < n; i++) tabStrip.SetPinned(i, hosts[i].Pinned);
        }

        // ==================================================================
        /// <summary>
        /// 用户按 + / Ctrl+T、或从书签 / 历史 / 「转生」开一个 —— 起个新标签并立刻切过去。
        ///
        /// 首帧标题给「这个文件夹的名字」，而不是留在 `AddHost` 的「打开中…」：起 explorer 到内容
        /// 出来实测 **0.7~1.8 秒**，这段时间标签条上显示什么，直接决定用户觉得快不快（跟还原
        /// 记忆标签同一个道理，见 `RestoreRememberedTabs`）。书签那条路还会用**书签自己的名字**盖过去
        /// （见上面 `favBar.ItemClicked`）。
        /// </summary>
        private void NewTab(string path) { NewTab(path, PathRules.Friendly(PathRules.Store(path)), true); }

        /// <summary>
        /// 开一个新标签（内容由 `launchQueue` 在后台填）。
        ///
        /// <paramref name="initialTitle"/> = 标签条上**第一帧**就显示的名字。还原记忆标签时传
        /// 「这个文件夹的名字」（见 `RestoreRememberedTabs`）—— 用户要的「先显示标签名，后台实际在
        /// 并发加载，这样界面没什么变化」：先把名字摆好，内容后填，标签条从头到尾不再变样。
        /// null = 「打开中…」（用户当场开新标签，他看见的就是这一步在转）。
        ///
        /// <paramref name="activate"/> = false 时不切过去（还原时一次摆 N 个，只在最后切一次）。
        /// </summary>
        private ExplorerHost NewTab(string path, string initialTitle, bool activate)
        {
            // 有预热好的备用窗口就直接用（几乎瞬时），没有才现起一个
            if (UseReserve(path, initialTitle)) return null;

            ExplorerHost h = AddHost();
            int i = hosts.IndexOf(h);
            if (!string.IsNullOrEmpty(initialTitle)) tabStrip.SetTitle(i, initialTitle);
            // 第二行先摆上要去的路径 —— explorer 要 3 秒才起得来，这 3 秒里也别让第二行空着
            tabStrip.SetPath(i, TabStrip.PathLine(PathRules.Store(path)));
            h.PresetTarget(path);     // 排队期间也得知道要去哪儿（否则中途保存记忆会把它丢掉）
            Diag.Step("EmbedForm: 排入队列 " + path);
            // ★ 用户当场要的那一个插队头：还原期间后面还排着十几个记忆标签（见 EnqueueFront）
            EnqueueFront(new Launch { Host = h, Path = path });
            PumpLaunch();
            if (activate) Activate(i);
            MarkDirty();
            return h;
        }

        /// <summary>
        /// 把一条启动请求**插到队头**。用户当场要的那一个走这儿（按 `+` / Ctrl+T、点到还没加载的标签）。
        ///
        /// 为什么值当：还原那阵子队列里排着十几个记忆标签，`Enqueue` 到队尾意味着用户那一按
        /// 要等前面全部起完（一轮 900ms 一格，见 `SpawnSlotMs`）—— 用户报的「按+号也好慢」就是这个。
        /// 队列里的先后**不影响归属判定**：每个标签认的是「地址栏内容 == 我要开的路径」那个窗口。
        /// </summary>
        private void EnqueueFront(Launch j)
        {
            Queue<Launch> rest = new Queue<Launch>();
            while (launchQueue.Count > 0) rest.Enqueue(launchQueue.Dequeue());
            launchQueue.Enqueue(j);
            while (rest.Count > 0) launchQueue.Enqueue(rest.Dequeue());
        }

        /// <summary>
        /// 把某个还没轮到的标签**提到队头**。
        ///
        /// 用在「用户点到了一个还在排队的标签」：他点它 = 现在就要它（伪懒加载下这是常态，
        /// 标签名字先摆着、内容还在后台排队）。不提的话它得等前面几个全起完 —— 点了跟没点一样。
        /// 队列里的地位换了，但归属判定不受影响（每个标签认的是自己那条路径的窗口）。
        /// </summary>
        private void PromoteLaunch(ExplorerHost h)
        {
            if (h == null || launchQueue.Count == 0) return;
            if (launchQueue.Peek().Host == h) return;
            bool present = false;
            foreach (Launch r in launchQueue) { if (r.Host == h) { present = true; break; } }
            if (!present) return;      // 不在队里 = 已经开工了（或已经被关掉），没什么可提的

            Queue<Launch> rest = new Queue<Launch>();
            Launch hit = null;
            while (launchQueue.Count > 0)
            {
                Launch j = launchQueue.Dequeue();
                if (hit == null && j.Host == h) { hit = j; continue; }
                rest.Enqueue(j);
            }
            launchQueue.Enqueue(hit);
            while (rest.Count > 0) launchQueue.Enqueue(rest.Dequeue());
            Diag.Step("EmbedForm: 用户点到了还在排队的标签，把它提到队头");
        }

        /// <summary>
        /// 摆一个**懒加载占位标签**：标题和路径先摆上，explorer 先不起（`Settings.LazyTabs` 开着时才走这儿）。
        /// 用户点到它才真起 —— 见 `StartDeferred`。
        /// </summary>
        private void AddDeferredTab(string path)
        {
            ExplorerHost h = AddHost();
            h.Deferred = true;
            h.PresetTarget(path);      // 排队/占位期间也得知道要去哪儿（否则中途保存记忆会把它丢掉）
            int i = hosts.IndexOf(h);
            string stored = PathRules.Store(path);
            tabStrip.SetTitle(i, PathRules.Friendly(stored));
            tabStrip.SetPath(i, TabStrip.PathLine(stored));
        }

        /// <summary>
        /// 把懒加载的占位标签真起起来（用户切到它了）。
        /// 照样进**同一条队列** —— 已经在起别的标签时就排在后面，不会两个 explorer 互相认错。
        /// ⚠ 标题**不改成「打开中…」**：名字早在摆占位的时候就写好了（`AddDeferredTab`），
        ///   加载完 `OnHostReady` 写回来的还是同一个名字。这中间一改反而让标签条闪一下 ——
        ///   用户要的就是「界面没什么变化」。
        /// </summary>
        private void StartDeferred(int idx) { StartDeferred(idx, false); }

        /// <param name="urgent">true = 插到队头。用户**当场**要的那一个用（见 `EnqueueFront`）。</param>
        private void StartDeferred(int idx, bool urgent)
        {
            if (idx < 0 || idx >= hosts.Count) return;
            ExplorerHost h = hosts[idx];
            if (h == null || !h.Deferred) return;
            h.Deferred = false;
            Diag.Step("EmbedForm: 标签开始加载「" + h.TargetPath + "」");
            Launch j = new Launch { Host = h, Path = h.TargetPath };
            if (urgent) EnqueueFront(j); else launchQueue.Enqueue(j);
            PumpLaunch();
        }

        /// <summary>
        /// 把队列里的 explorer 放出去 —— 一次最多 <see cref="MaxConcurrentLaunch"/> 个在飞。
        ///
        /// 历史上这里是「一次只起一个，排成队依次来」，为的是修「有时候标签页点开是空的」：
        /// 一口气起 6 个 explorer、6 个窗口同时冒出来，而每个标签只能靠「扫一个刚出现的新窗口」
        /// 认自己那个 ⇒ 互相认错、或者谁都认不到空等 25 秒超时。
        /// 现在并发能开，是因为归属判据换成了硬的：**地址栏内容必须等于我要开的路径**
        ///（见 `EmbedApi.FindNewCab` / `ExplorerHost.OnPoll`），一次冒几个都不会认错。
        /// 关掉 `Settings.ParallelLaunch` 就退回那套最简（也最慢）的串行。
        ///
        /// 队满之后**什么时候腾位置**是这套并发快不快的分水岭：见 <see cref="SpawnSlotMs"/>
        /// （按时间放行，不按「认到窗口」）。
        ///
        /// 队列空了就去**预热一个备用窗口**（见 WarmUp）：下次「新建标签页」不用再等进程起来。
        /// </summary>
        private void PumpLaunch()
        {
            if (IsDisposed || Disposing) return;
            ReleaseSpawnSlots();

            int cap = Settings.ParallelLaunch ? MaxConcurrentLaunch : 1;
            while (launchQueue.Count > 0 && launching.Count < cap)
            {
                Launch j = launchQueue.Dequeue();
                if (j.Host == null) continue;
                // 备用窗口不在 hosts 里，别把它当「排队期间被关掉了」扔掉
                if (!j.Warm && !hosts.Contains(j.Host)) continue;
                if (launching.ContainsKey(j.Host)) continue;
                j.SpawnedAt = DateTime.Now;
                launching[j.Host] = j.SpawnedAt;
                Diag.Step("EmbedForm: 起 explorer" + (Settings.ParallelLaunch ? "（并发 " + launching.Count + "/" + cap + "）" : "（串行）") + j.Path);
                j.Host.Start(j.Path);
            }
            // 只要有标签还在「起」这条道上，就把那笔昂贵的 shell 登记挂起来（见 ShellBrowserReg.KeepQuiet）。
            // 还原 8 个标签时它累计要 5 秒多、还全压在一个 UI 线程上 —— 那就是「非激活标签还在加载时
            // 整个程序几乎不可用」。挂起来之后由 500ms 心跳在这一批结束的 2 秒内补上。
            if (launchQueue.Count > 0 || launching.Count > 0) ShellBrowserReg.KeepQuiet(1500);
            // 队列空、手里也没有在起的 → 预热下一个「新建标签页」的窗口
            if (launchQueue.Count == 0 && launching.Count == 0) WarmUp();
            // 什么时候还要 200ms 回来一趟：
            //   · 队列里还有活 —— 回来放行「到点的格」（见 SpawnSlotMs）
            //   · 队列空了但手里没备用窗口 —— 回来把预热补上（预热失败进了冷却期时全靠它自愈）
            // 手里已经备着一个（或在备）就停：那时没有任何周期性的事要做。
            if (launchQueue.Count > 0 || reserve == null) pumpTimer.Start();
            else pumpTimer.Stop();
        }

        /// <summary>
        /// 把占了 `SpawnSlotMs` 的那些格放掉（窗口早该出来了，格不该继续被占着）。
        /// 见 <see cref="SpawnSlotMs"/> —— 这一步就是「动态窗口的并发」和「等一批加载完再下一批」的分界。
        /// </summary>
        private void ReleaseSpawnSlots()
        {
            if (launching.Count == 0) return;
            // 串行模式（用户关掉了并发）就老老实实等「认到窗口」再放行 —— 那正是那个开关的含义
            //（「一次只起一个」），不能靠时间偷偷并起来。
            if (!Settings.ParallelLaunch) return;
            DateTime now = DateTime.Now;
            List<ExplorerHost> done = null;
            foreach (KeyValuePair<ExplorerHost, DateTime> kv in launching)
            {
                if ((now - kv.Value).TotalMilliseconds < SpawnSlotMs) continue;
                if (done == null) done = new List<ExplorerHost>();
                done.Add(kv.Key);
            }
            if (done == null) return;
            for (int i = 0; i < done.Count; i++)
            {
                launching.Remove(done[i]);
                Diag.Step("EmbedForm: 起进程的格到点放行（这一格占了 " + SpawnSlotMs + "ms）");
            }
        }

        /// <summary>
        /// 一个标签起完了（成了 / 失败了 / 认到窗口了）—— 给它腾位置，并且**无条件推一把队列**。
        ///
        /// ⚠ 别写成「不在表里就直接 return」：并行起标签时格是**按时间**放的（见 SpawnSlotMs），
        ///   一个标签的格早在它收编完之前就到期放掉了 —— 等它最后 `Ready` 回来，表里已经没有它。
        ///   这时候早退就等于把「这一批彻底结束了」这个收尾信号吃掉，`PumpLaunch` 尾部那句
        ///   `WarmUp` 永远轮不到 ⇒ **备用窗口再也预热不出来** ⇒ 用户报的
        ///   「Win+E 没有预加载、单个标签打开也变慢」（每个新标签都得现起 explorer）。
        /// </summary>
        private void LaunchDone(ExplorerHost h)
        {
            launching.Remove(h);
            PumpLaunch();
        }

        /// <summary>
        /// 还有标签正等着 explorer 起来吗（Hub 收编窗口前要看这个，见 `DrainCapture`）。
        ///
        /// 判据是「**有没有标签还没落定**」而不是「起进程的格有没有占满」：窗口认下来（更别说嵌好）
        /// 之前它都还漂在桌面上、没被登记成我们的，这时候去收编别人的窗口就可能把我们自己刚起的
        /// 那个当成「用户新开的」再收一个重复标签回来。落定 = 嵌好或失败（见 `ExplorerHost.Settled`）。
        /// </summary>
        internal bool LaunchInFlight
        {
            get
            {
                if (launchQueue.Count > 0) return true;
                for (int i = 0; i < hosts.Count; i++)
                {
                    ExplorerHost h = hosts[i];
                    if (h == null || h.Deferred || h.Settled) continue;
                    return true;
                }
                // 备用窗口不在 hosts 里，它那张也得算 —— 不然预热那一秒里它会被 Hub 收成一张真标签
                return reserve != null && !reserve.Settled;
            }
        }

        // ------------------------------------------------------------------
        // 备用窗口（预热）
        //
        // 用户报「新建标签页反应慢」。慢在哪（日志实测）：一次点击到内容出来大约 1.7 秒，
        // 其中「起 explorer 进程 + 等它把文件列表建出来」占掉 0.8 秒左右 ——
        // 这两步跟用户按没按 + 号毫无关系，那就提前做掉：
        // 每批标签都起完之后，在后台（也走串行队列）再起一个**藏着的**窗口备着，
        // 加载完也不显示；用户一按 + / Ctrl+T，把它直接显出来即可，几乎瞬时。用掉立刻再备一个。
        //
        // 只在目标是「此电脑」时才用得上（备用窗口加载的就是它）—— 而「新建标签页」现在就是「此电脑」。
        // ------------------------------------------------------------------

        private bool CanWarm()
        {
            if (IsDisposed || Disposing || Quitting) return false;
            if (reserve != null || reserveReady) return false;
            // 刚失败过一次就先别急着重来（起失败要干等 25 秒超时，立刻重试就是死循环），见 warmRetryAt
            if (DateTime.Now < warmRetryAt) return false;
            // 还有标签没落定（在起 / 在等窗口 / 在收编）就一律不预热。
            // 判据跟 Hub 那道闸同源（见 `LaunchInFlight`）—— 收编是整条流水线最贵的一步，
            // 这时候再塞一个备用窗口进去，就是在一堆刚起来的 explorer 上又加一个，
            // 只会把最后几个标签拖慢。
            // ⚠ 从前这里判的是「起进程的格有没有占满」，那已经不够了：格是按时间放的
            //   （见 SpawnSlotMs），放掉之后窗口可能还在外面漂着。
            return !LaunchInFlight;
        }

        private void WarmUp()
        {
            if (!CanWarm()) return;
            ExplorerHost h = CreateHost();
            reserve = h;
            reserveReady = false;
            // 让它在收编之前把 shell 清单里那一项抓住 —— 用户点了**别的**文件夹时，
            // 这扇备用窗口直接导航过去就是（省掉再起一个 explorer 进程那 0.24~1.28 秒），见 UseReserve。
            // 连着栽两次就别再试了（理由见 shellTargetMisses）。
            h.WantShellTarget = shellTargetMisses < 2;
            h.PresetTarget(ExplorerView.ThisPcPath);
            Diag.Step("EmbedForm: 预热备用标签（此电脑）");
            launchQueue.Enqueue(new Launch { Host = h, Path = ExplorerView.ThisPcPath, Warm = true });
            PumpLaunch();
        }

        /// <summary>把没用上的备用窗口收干净（起失败 / 它自己没了 / 窗口要关了）。</summary>
        private void KillReserve()
        {
            ExplorerHost h = reserve;
            reserve = null;
            reserveReady = false;
            if (h == null) return;
            launching.Remove(h);
            try
            {
                content.Controls.Remove(h.Host);
                h.Close("丢弃备用窗口");
                h.Dispose();
            }
            catch (Exception ex) { Diag.Log("EmbedForm: 丢弃备用窗口失败 " + ex.Message); }
        }

        /// <summary>
        /// 新建标签页 / 点书签 / 点历史 / 转生时优先用它。返回 true = 已经开好了，调用方不用再起 explorer。
        ///
        /// 从前这个窗口只能用在「此电脑」上（它就是预热在那个目录的），所以只有按 + 才快。
        /// 现在**别的文件夹也能用**：直接让 shell 把它**导航过去**（`ShellBrowserReg.NavigateTo`，
        /// 实测嵌入之后 82ms、还是顶层时 105ms），省掉「起 explorer 进程 + 它自己建窗/导航/SHOW」
        /// 那一整段（实测 0.24~1.28 秒）—— 点书签觉得慢，慢的就是这一段。
        /// 虚拟位置（回收站 / 网络 / 此电脑）不吃这条：那些是 shell 别名，导航过去未必认，照老路现起。
        /// </summary>
        private bool UseReserve(string path, string initialTitle)
        {
            if (!reserveReady || reserve == null) return false;

            ExplorerHost h = reserve;
            if (h.CabWindow == IntPtr.Zero || !NativeMethods.IsWindow(h.CabWindow))
            {
                Diag.Step("EmbedForm: 备用窗口已经没了，丢掉");
                KillReserve();
                return false;
            }

            string stored = PathRules.Store(path) ?? "";
            bool isThisPc = PathRules.Same(stored, ExplorerView.ThisPcPath);
            // 只有「真·文件夹」（`D:\…` 这种）才谈得上导航过去
            bool realFolder = !isThisPc && PathRules.Restorable(stored)
                && !stored.StartsWith("::", StringComparison.Ordinal)
                && !stored.StartsWith("shell:", StringComparison.OrdinalIgnoreCase);
            if (!isThisPc && !realFolder) return false;

            bool navigated = false;
            Stopwatch swR = Stopwatch.StartNew();
            if (realFolder)
            {
                if (!ShellBrowserReg.NavigateTo(h.ShellTarget, stored))
                {
                    // 导航这条路走不通（没抓到清单项 / shell 不收这个请求）：**别将它就**，
                    // 也别把这个备用窗口扔掉 —— 它对「此电脑」照样好用，留着。这一次照老路现起。
                    Diag.Step("EmbedForm: 备用窗口导航不了，照老路现起 explorer（备用窗口留着）");
                    return false;
                }
                navigated = true;
            }
            long tNav = swR.ElapsedMilliseconds;

            reserve = null;
            reserveReady = false;
            hosts.Add(h);
            int i = hosts.Count - 1;
            // 标题用调用方给的那个（书签上的名字 / 路径现算的名字）：此刻窗口还停在旧目录上，
            // 问它要名字只会拿到「此电脑」。
            tabStrip.AddTab(string.IsNullOrEmpty(initialTitle) ? h.CurrentDisplayName : initialTitle);
            tabStrip.SetIcon(i, h.TabIcon);
            string line = stored.Length > 0 ? stored : LivePath(h);
            tabStrip.SetPath(i, TabStrip.PathLine(line));
            History.Add(line);
            Diag.Step("EmbedForm: 用掉预热好的备用标签 idx=" + i + (navigated ? "（导航复用）" : "（秒开）"));
            if (navigated)
            {
                // 导航是 explorer 那边的异步动作，立刻现身会先闪一下它原来那个目录（此电脑）。
                // 标签条上它已经选中、名字也对了，只是先把内容藏起来，等它真换过去再放出来（见 OnRevealTick）。
                // ⚠ 表**先起**：这一段等的是 explorer 换目录，不该排在下面 `Activate` 那些界面活后面
                //   —— 否则「已经在等的」和「还没开始等」会白白差掉一整个 Activate 的耗时。
                RevealNow();       // 上一个还在等露面的先放出来（只记得住一个，不放开它就永远藏着）
                // 换到位之前，这个标签的路径就算「要去的那一个」（不记会开出重复标签，见 LivePath）
                h.PendingPath = stored;
                revealPending = h;
                revealWant = stored;
                revealAt = DateTime.Now;
                revealTick = 0;
                BeatReveal(true);
            }
            long tPreAct = swR.ElapsedMilliseconds;
            Activate(i);
            long tAct = swR.ElapsedMilliseconds;
            if (navigated)
            {
                // 上面那句 Activate 把它设成可见了，按回去；但若表已经在 Activate 里提前放出来了就别再藏
                if (revealPending == h) h.Host.Visible = false;
                Diag.Step("EmbedForm: 导航复用拆账 发导航" + tNav + "ms 记账" + (tPreAct - tNav)
                    + "ms Activate" + (tAct - tPreAct) + "ms");
            }
            MarkDirty();
            PumpLaunch();      // 立刻再备一个
            return true;
        }

        /// <summary>
        /// 等「导航复用的备用窗口」真的换到目标目录，再把它露出来（见 <see cref="UseReserve"/>）。
        /// 到点还没换过来就先露面 —— 那扇窗里的内容至少是能看的，比一直空着强。
        /// </summary>
        private void OnRevealTick()
        {
            ExplorerHost h = revealPending;
            if (h == null)
            {
                BeatReveal(false);
                return;
            }
            revealTick++;

            Stopwatch swRead = Stopwatch.StartNew();
            string got = ShellBrowserReg.PathOfEntry(h.ShellTarget);
            long tRead = swRead.ElapsedMilliseconds;
            bool ok = got != null && PathRules.Same(got, revealWant);
            if (!ok && (DateTime.Now - revealAt).TotalMilliseconds < RevealMaxMs)
            {
                // 前 4 拍记一下：能看出「表多久才响第一下」和「读一次 LocationURL 多贵」
                if (revealTick <= 4)
                    Diag.Step("EmbedForm: reveal 第 " + revealTick + " 拍 @"
                        + (long)(DateTime.Now - revealAt).TotalMilliseconds + "ms 读URL" + tRead
                        + "ms got=" + (string.IsNullOrEmpty(got) ? "(空)" : got));
                return;
            }

            Diag.Step("EmbedForm: 备用窗口换目录" + (ok ? "到位" : "超时（先露面）") + " " + revealWant
                + "（第 " + revealTick + " 拍 @" + (long)(DateTime.Now - revealAt).TotalMilliseconds + "ms）");
            RevealNow();
        }

        /// <summary>把「在等导航到位才露面」的那个标签放出来（到点了 / 超时了 / 又来一个要等）。</summary>
        private void RevealNow()
        {
            ExplorerHost h = revealPending;
            revealPending = null;
            BeatReveal(false);
            if (h != null) h.PendingPath = null;   // 到位了，路径以地址栏为准（见 LivePath）
            if (h == null || IsDisposed || Disposing) return;
            if (!hosts.Contains(h)) return;
            h.Host.Visible = true;
            if (hosts.IndexOf(h) == activeIndex) h.Focus();
        }

        /// <summary>
        /// 起 / 停那支「等备用窗口换目录」的拍子（见 <see cref="revealBeat"/>）。
        /// 包一层 try 是因为窗体销毁之后还可能有一拍排在那里 —— 直接 `Change` 会抛，那就成了崩溃。
        /// </summary>
        private void BeatReveal(bool on)
        {
            if (revealBeat == null) return;
            int ms = on ? 40 : System.Threading.Timeout.Infinite;
            try { revealBeat.Change(ms, ms); } catch { }
        }

        /// <summary>
        /// 开一个「接管别人窗口」的标签 —— 用户从开始菜单 / 桌面双击打开的那个文件夹（Bug 1）。
        /// 跟 `NewTab` 唯一的区别：**不起新 explorer**，直接把已经存在的那个窗口收进来。
        /// 所以它**不进队列**（队列的意义是「别同时起两个进程」，这条不进程）。
        /// </summary>
        internal bool NewAdoptedTab(IntPtr cab, int pid)
        {
            if (IsDisposed || Disposing || cab == IntPtr.Zero) return false;
            foreach (ExplorerHost e in hosts)
            {
                if (e != null && e.CabWindow == cab) return false;   // 已经收过了
            }
            ExplorerHost h = AddHost();
            int i = hosts.IndexOf(h);
            tabStrip.SetPath(i, "（收进来的窗口）");
            h.Adopt(cab, pid);
            lastAdopted = h;          // 见 StartRestLaunches：用户在等的就是它
            Activate(i);
            MarkDirty();
            return true;
        }

        /// <summary>
        /// 建一个 shell 容器 + 把事件接好（**不加标签、不进 hosts**）。
        /// 两条路都从这儿出发：进 hosts 的就是一个标签（`AddHost`），
        /// 不进的是那个预热用的备用窗口（`WarmUp`）。
        /// </summary>
        private ExplorerHost CreateHost()
        {
            ExplorerHost h = new ExplorerHost();
            h.Host.Dock = DockStyle.Fill;
            h.Host.Visible = false;
            content.Controls.Add(h.Host);
            h.SetIconTarget(TabStrip.TabIconSize);      // 告诉它图标画多大（设备像素）

            h.Ready += delegate(object s, EventArgs e) { OnHostReady(h); };
            h.Claimed += delegate(object s, EventArgs e)
            {
                // 认到窗口 = 这一批还在走：把那笔昂贵的 shell 登记挂起来，等这一批完了由心跳补上
                //（理由和数字见 ShellBrowserReg.KeepQuiet）。外部自己开的窗（转生/收编）不走这里，
                // 那条路照旧收编那一刻就登记 —— 那条路是「用户当场在等」，不能拖。
                ShellBrowserReg.KeepQuiet(1500);
                LaunchDone(h);
            };
            h.Failed += delegate(object s, EventArgs e) { OnHostFailed(h); };
            h.TitleChanged += delegate(object s, EventArgs e) { OnHostTitleChanged(h); };
            h.IconChanged += delegate(object s, EventArgs e) { OnHostIconChanged(h); };
            h.PathChanged += delegate(object s, EventArgs e) { OnHostPathChanged(h); };
            h.Died += OnHostDied;
            return h;
        }

        /// <summary>
        /// 建一个标签 + 一个空的 shell 容器。**怎么把它开起来由调用方决定**：
        /// 自己起一个（`NewTab` → `Start`）还是接管现成的（`NewAdoptedTab` → `Adopt`）。
        /// 两条路后面完全一样：等 explorer 加载完 → Ready → 嵌进来。
        /// </summary>
        private ExplorerHost AddHost()
        {
            ExplorerHost h = CreateHost();
            hosts.Add(h);
            tabStrip.AddTab("打开中…");
            return h;
        }

        private void OnHostReady(ExplorerHost h)
        {
            // 备用窗口不在 hosts 里 —— 它到这就绪，等用户按 + 号的时候直接显出来
            if (h == reserve)
            {
                reserveReady = true;
                // 抓到没抓到那一项，决定这个备用窗口往后能不能给「别的目录」用（见 UseReserve）
                if (h.ShellTarget == null) shellTargetMisses++; else shellTargetMisses = 0;
                Diag.Step("EmbedForm: 备用标签已就绪（下次新建标签页秒开）");
                LaunchDone(h);
                return;
            }

            int i = hosts.IndexOf(h);
            if (i < 0) { LaunchDone(h); return; }     // 已经关掉了（也得让队列往前走）

            tabStrip.SetTitle(i, h.CurrentDisplayName);
            tabStrip.SetIcon(i, h.TabIcon);
            tabStrip.SetPath(i, TabStrip.PathLine(LivePath(h)));
            History.Add(LivePath(h));                // 真打开了才算「去过」
            if (i == activeIndex)
            {
                h.Host.Visible = true;
                Text = tabStrip.Tabs[i].Title;       // 任务栏 / Alt+Tab 的显示名
                h.Focus();
            }
            // ★ 用户要的那一个**落定**了 ⇒ 这时候才让记忆里其余的标签排进启动队列
            //   （见 StartRestLaunches）：提前放会把它自己的收编拖慢近 0.5 秒。
            if (restPending && h == restFirstHost) StartRestNow();
            MarkDirty();     // 嵌好了 = 可以记了（TabPaths 会跳过还没嵌好的）
            LaunchDone(h);   // 兜底：正常早就在 `Claimed` 那一步放行过了（见那个事件的注释）
        }

        /// <summary>
        /// explorer 在里面导航了，把标签标题跟上。
        /// 否则就是用户報的那个 bug：“换了目录，标签页也没变”。
        /// </summary>
        private void OnHostTitleChanged(ExplorerHost h)
        {
            int i = hosts.IndexOf(h);
            if (i < 0) return;
            string t = h.CurrentDisplayName;
            tabStrip.SetTitle(i, t);
            tabStrip.SetIcon(i, h.TabIcon);          // 导航后文件夹图标也会换（同一个窗口，换的是它自己挂的图标）
            if (i == activeIndex) Text = t;
        }

        /// <summary>标签图标变了（explorer 按当前文件夹换掉了窗口自己那颗图标）。</summary>
        private void OnHostIconChanged(ExplorerHost h)
        {
            int i = hosts.IndexOf(h);
            if (i < 0) return;
            tabStrip.SetIcon(i, h.TabIcon);
        }

        /// <summary>
        /// 这个标签导航到了别的文件夹 —— 标题、第二行路径、记忆里的路径都要跟着变。
        /// （`TitleChanged` 和 `PathChanged` 是两条独立事件，导航时不一定同时到，所以两边都刷一遍。）
        /// </summary>
        private void OnHostPathChanged(ExplorerHost h)
        {
            int i = hosts.IndexOf(h);
            if (i >= 0)
            {
                string p = LivePath(h);
                tabStrip.SetPath(i, TabStrip.PathLine(p));
                History.Add(p);
            }
            MarkDirty();
        }

        private void OnHostFailed(ExplorerHost h)
        {
            if (h == reserve)
            {
                Diag.Log("EmbedForm: 备用窗口没起起来，丢掉（下次新建标签页走老路）");
                warmRetryAt = DateTime.Now.AddSeconds(30);   // 冷却：别每 25 秒烧一个 explorer（见 CanWarm）
                KillReserve();
                LaunchDone(h);
                return;
            }

            int i = hosts.IndexOf(h);
            if (i < 0) { LaunchDone(h); return; }
            string want = h.TargetPath;
            string why = h.LastError ?? "（没有错误文本）";
            tabStrip.SetTitle(i, "打开失败");
            tabStrip.SetPath(i, why);
            Diag.Log("EmbedForm: 标签打开失败 " + why);

            LaunchDone(h);   // 先让队列往前走（这一个已经结束了）

            // 干等 25 秒 —— 多半是那个窗口被别人抢了 / explorer 这次没给新建窗口。
            // 自动再来一次，别把一个黑标签留在那儿等用户自己发现。
            // 只重试一次（Retried 标记），重试还不行就老实报错。重试也走队列，别破坏串行。
            if (!h.Retried && !string.IsNullOrEmpty(want) && PathRules.Restorable(want))
            {
                h.Retried = true;
                Diag.Step("EmbedForm: 自动重试一次「" + want + "」");
                tabStrip.SetTitle(i, "重试中…");
                tabStrip.SetPath(i, TabStrip.PathLine(PathRules.Store(want)));
                launchQueue.Enqueue(new Launch { Host = h, Path = want });
                PumpLaunch();
            }
        }

        /// <summary>
        /// 标签里的 explorer 把窗口关了（最典型：在里面按 Ctrl+W）—— 把标签一并收掉，
        /// 不然界面上会留一块打不开的黑区。关到最后一个就走收托盘。
        /// </summary>
        private void OnHostDied(object sender, EventArgs e)
        {
            ExplorerHost h = sender as ExplorerHost;
            if (h == null) return;
            if (h == reserve)
            {
                Diag.Step("EmbedForm: 备用窗口自己没了，丢掉");
                KillReserve();
                return;
            }
            int i = hosts.IndexOf(h);
            Diag.Step("EmbedForm: 标签里的 explorer 自己退了 idx=" + i + " -> 收掉这个标签");
            if (i >= 0) CloseTab(i);
        }

        private void Activate(int idx)
        {
            if (idx < 0 || idx >= hosts.Count) return;
            activeIndex = idx;
            // 懒加载的占位标签：切到它才算「要用它」，这时候才真起 explorer。
            // 得排在下面那句 `Host.Visible` 之前 —— 先把队列上的活派出去，界面这一帧先显示空面板，
            // 内容出来时 `OnHostReady` 会把标题 / 图标 / 路径再刷一遍。
            // urgent：用户点的就是「现在要」——插队头，别排在还原那十几个后面（见 EnqueueFront）。
            Stopwatch sw = Stopwatch.StartNew();
            if (hosts[idx].Deferred) StartDeferred(idx, true);
            // 伪懒加载下「标签已经在、内容还在后台排队」是常态，用户点它 = 现在就要它，提到队头。
            else PromoteLaunch(hosts[idx]);
            long tPrep = sw.ElapsedMilliseconds;
            // ⚠ 还在等导航到位的那个标签（见 UseReserve / OnRevealTick）**先别露面**：
            //   让它 Visible 一下再藏回去，白付一次「跨进程把 explorer 窗口显示出来」的钱（实测 85~165ms），
            //   而那一下还正好撞在 explorer 忙的时候。到点露面时 `RevealNow` 会把它放出来。
            for (int i = 0; i < hosts.Count; i++)
                hosts[i].Host.Visible = (i == idx) && hosts[i] != revealPending;
            tabStrip.SetActive(idx);
            if (vPane != null && verticalOn) vPane.ScrollActiveIntoView();   // 竖排那份也要把选中的那行拉进视线
            long tUi = sw.ElapsedMilliseconds;
            // 同理：内容还没换过来，这时候把键盘焦点塞给它是白等一次跨进程 SetFocus（实测 70~172ms）；
            // 它露面时 `RevealNow` 会补上（那一下它已经是 active 了）。
            if (hosts[idx] != revealPending) hosts[idx].Focus();
            long tFocus = sw.ElapsedMilliseconds;
            Text = tabStrip.Tabs[idx].Title;   // 任务栏 / Alt+Tab 的显示名（自绘标题栏删了，就剩这一处用途）
            MarkDirty();     // 「当时选中那个」也要记
            // 刚离开的那个标签先别动：过 3 秒还没被切回来，才当它真的凉了。
            trimTimer.Stop();
            trimTimer.Start();
            long tEnd = sw.ElapsedMilliseconds;
            // 只记慢的：正常切标签是几毫秒，超过 30ms 就说明某一步卡住了（点书签为什么慢的拆账）
            if (tEnd >= 30)
                Diag.Step("EmbedForm: Activate 拆账 派活" + tPrep + "ms 可见性+标签条" + (tUi - tPrep)
                    + "ms Focus" + (tFocus - tUi) + "ms 收尾" + (tEnd - tFocus) + "ms（合计 " + tEnd + "ms）");
        }

        /// <summary>
        /// 把**非当前**标签的 explorer 进程驻留内存收一收（用户的优化 3）。
        ///
        /// 为什么值当：一个标签 = 一个独立 explorer.exe，非激活那几十 MB 全在别人进程里，
        /// 我们自己进程怎么省都省不出这一块。
        /// 为什么延时 3 秒：来回切（对比两个目录）时收完马上又读回来更亏，
        /// 所以只在「停留够久」之后做一次。
        /// </summary>
        private void TrimInactiveTabs()
        {
            if (IsDisposed || Disposing) return;
            int n = 0;
            for (int i = 0; i < hosts.Count; i++)
            {
                if (i == activeIndex) continue;      // 当前标签留着，不然切回去要重新载入
                ExplorerHost h = hosts[i];
                if (h == null) continue;
                h.TrimMemory();
                n++;
            }
            if (n > 0) Diag.Step("EmbedForm: 收了 " + n + " 个非激活标签的内存");
            // 标签那边收完，把我们自己这一份也收一收（见 ExplorerHost.TrimSelf）。
            // 不放在 `if (n > 0)` 里 —— 只有一个标签的时候本进程照样会涨（图标、历史、重绘的位图）。
            ExplorerHost.TrimSelf();
        }

        private void CloseTab(int idx) { CloseTab(idx, true); }

        /// <summary>
        /// 关一个标签。<paramref name="activate"/> = false 时**不**做收尾的 `Activate`
        /// （批量关要走这条，见 `CloseTabsQuiet`——中间那几次 Activate 就是「闪一下」的来源）。
        /// </summary>
        private void CloseTab(int idx, bool activate)
        {
            if (idx < 0 || idx >= hosts.Count) return;
            ExplorerHost h = hosts[idx];

            // 记进「刚关掉的」栈 —— Ctrl+Shift+T 要按「后进先出」往回捞：
            // 点一下恢复最近关的那个、再点一下恢复上上个（用户的要求）。
            string gone = LivePath(h);
            if (PathRules.Restorable(gone))
            {
                closedTabs.RemoveAll(delegate(string s) { return PathRules.Same(s, gone); });
                closedTabs.Add(gone);
                while (closedTabs.Count > ClosedKeep) closedTabs.RemoveAt(0);
                Diag.Step("EmbedForm: 记下关掉的标签「" + gone + "」（栈里 " + closedTabs.Count + " 个）");
            }

            hosts.RemoveAt(idx);
            activeIndex = -1;                        // 索引全变了，重新算
            Diag.Step(string.Format("EmbedForm: CloseTab idx={0}，剩 {1} 个", idx, hosts.Count));
            try
            {
                h.Close("关标签 idx=" + idx);
                content.Controls.Remove(h.Host);
                h.Host.Dispose();
                h.Dispose();
            }
            catch (Exception ex) { Diag.Log("EmbedForm: 关标签失败 " + ex.Message); }

            // 关掉的正好占着「起进程」那一格：放掉它、给队列里的下一个腾位置，不然后面排着的全卡住
            if (launching.Remove(h))
            {
                Diag.Step("EmbedForm: 正在起的那个被关了，队列继续");
                PumpLaunch();
            }

            tabStrip.RemoveTab(idx);

            if (hosts.Count == 0)
            {
                // 最后一个标签被关 ⇒ 收进托盘（**不是退出**）。
                // 进程一退 Win+E 就没人接了，系统就会去开原生资源管理器 —— 正是用户報的那个问题。
                Diag.Step("EmbedForm: 最后一个标签被关，收进托盘");
                HideToTray();
                return;
            }
            if (activate || !closingBatch) Activate(Math.Min(idx, hosts.Count - 1));
        }

        // ==================================================================
        // 设置（标签条最右边那枚齿轮 / 标签条空白处右键）
        // ==================================================================

        /// <summary>
        /// 「设置」——**开一个独立的设置窗口**（用户要的）。
        ///
        /// 原来是就地弹一份菜单，换成窗口有两个理由：① 菜单里放不下东西（标签宽度只能做成子菜单、
        /// 说明文字根本没处写）；② 窗口里能一并把**程序名 / 版本 / 简介**摆出来。
        /// 托盘图标右键那份菜单**保持原样**（用户明确要求），所以内容的唯一来源还是 `SettingsMenu.Spec`，
        /// 窗口只是换一种渲染方式 —— 以后加设置项不会漏一边。
        ///
        /// 仍然走 `Defer`（BeginInvoke）—— 当初弹菜单连着卡死三次是同一个道理：
        /// 不能在任何控件的 MouseDown 里同步开窗 / 弹菜单。
        /// </summary>
        private void ShowSettingsWindow()
        {
            if (hub == null || IsDisposed || Disposing) return;
            // 窗口的所有权收到 Hub（全进程只开一个）—— 齿轮、空白右键、托盘菜单「更多选项」都是这一个。
            hub.OpenSettings();
        }

        // ==================================================================
        // 历史 / 恢复关闭 / 右键菜单 / 书签栏（用户点名的三个新功能）
        // ==================================================================

        /// <summary>
        /// 把一件事推到「当前这轮消息处理完之后」再干。
        ///
        /// 为什么一律这么走（就是齿轮卡死那个坑的根）：弹菜单、关标签这种动作如果在控件的
        /// `MouseDown` 里同步做，鼠标消息还没走完就切了鼠标捕获，菜单的模态循环跟控件的捕获互相等
        /// —— 界面就死了。跳出一个消息循环再动手，两个循环永远不会叠在一起。
        /// </summary>
        private void Defer(Action a)
        {
            if (a == null || IsDisposed || Disposing) return;
            try { BeginInvoke(a); }
            catch (Exception ex) { Diag.Log("EmbedForm: Defer 失败 " + ex.Message); }
        }

        /// <summary>Ctrl+H / 历史按钮：列出去过的文件夹，挑一个开成新标签。</summary>
        private void ShowHistoryMenu()
        {
            if (IsDisposed || Disposing) return;
            ShowPopupAtTool(TabStrip.Tool.History,
                History.BuildMenu(OpenFromHistory,
                    delegate { if (hub != null) hub.OpenHistoryManager(); }),
                "历史记录");
        }

        /// <summary>
        /// 书签栏上「全部打开（N 书签）」—— 把这个文件夹里（含子文件夹）的书签一项一项开出来。
        /// **必须串行排队**：开 explorer 是串行队列（见 `PumpLaunch`），并发起 N 个会互相认错、
        /// 标签空等超时。文件夹开成新标签，文件交给系统默认程序。
        /// </summary>
        private void OpenAllFromFavBar(FavNode folder)
        {
            if (folder == null || IsDisposed || Disposing) return;
            FavNode[] items = FavStore.ItemsIn(folder);
            Diag.Step("EmbedForm: 全部打开「" + FavStore.NameOf(folder) + "」共 " + items.Length + " 项");
            if (!Visible) Show();
            for (int i = 0; i < items.Length; i++)
            {
                string p = items[i] == null ? null : items[i].Path;
                if (string.IsNullOrEmpty(p)) continue;
                if (FavStore.IsFolder(p)) NewTab(p);
                else
                {
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(p) { UseShellExecute = true }); }
                    catch (Exception ex) { Diag.Log("EmbedForm: 全部打开，跳过 " + p + "：" + ex.Message); }
                }
            }
        }

        /// <summary>
        /// 从「历史记录 / 书签」挑了一个位置 —— 开成新标签（已经开着就切过去）。
        /// 单独抽出来是为了能记日志：用户报过「历史里选了条目没打开」，有日志才查得下去。
        /// </summary>
        private void OpenFromHistory(string p)
        {
            Diag.Step("EmbedForm: 历史/书签 -> " + p);
            Defer(delegate
            {
                if (IsDisposed || Disposing) return;
                if (!Visible) Show();
                if (!PathRules.Restorable(p))
                {
                    Diag.Step("EmbedForm: 这个位置开不了（没有真实路径），跳过：" + p);
                    return;
                }
                int dup = IndexOfPath(p);
                if (dup >= 0)
                {
                    Diag.Step("EmbedForm: 这个位置已经有标签了，切过去 idx=" + dup);
                    Activate(dup);
                }
                else NewTab(p);
            });
        }

        /// <summary>
        /// 在某个工具按钮正下方弹一份菜单。
        /// 锚点跟齿轮那条同一个算法：按**按钮中心**定位，菜单自己会往回挪（不会跑出屏幕）。
        /// 菜单一律走 `MenuFx`（自绘 + 前后各一行日志）。
        /// </summary>
        private void ShowPopupAtTool(TabStrip.Tool tool, PopItem[] items, string what)
        {
            if (items == null || items.Length == 0) return;
            TabStrip bar = TabBar;
            Rectangle b = bar.ToolButtonBounds(tool);
            // 竖排时菜单往**右边**弹（栏本身就窄，往下弹会被标签盖住）
            Point at = verticalOn
                ? new Point(b.Right + Px(4), b.Top)
                : new Point(b.Left + b.Width / 2, b.Bottom);
            PopMenu.Show(items, bar, at, what);
        }

        /// <summary>Ctrl+Shift+T / 恢复按钮：把最近关掉的那个标签开回来（后进先出）。</summary>
        private void ReopenClosedTab()
        {
            if (closedTabs.Count == 0)
            {
                Diag.Step("EmbedForm: 恢复关闭的标签，但栈是空的");
                Toast.Show("没有可恢复的标签页", "这次运行里还没关过标签。");
                return;
            }
            string p = closedTabs[closedTabs.Count - 1];
            closedTabs.RemoveAt(closedTabs.Count - 1);
            Diag.Step("EmbedForm: 恢复关闭的标签「" + p + "」（栈里还剩 " + closedTabs.Count + "）");
            if (!Visible) Show();
            int dup = IndexOfPath(p);
            if (dup >= 0) Activate(dup);   // 已经开着（历史/书签又开过）就切过去，别开两个一样的
            else NewTab(p);
        }

        /// <summary>
        /// 建一条菜单项：点下去**先在日志里记一笔**再执行（实现在 `MenuFx.Item`，书签栏那份菜单也用同一个）。
        /// 用户连着两轮报「右键菜单功能没实现」—— 这里加一行日志是为了以后不用猜：
        /// 「菜单弹出来了但点了没反应」和「点了、动作自己失败了」在日志里是两回事。
        /// </summary>
        private static PopItem Mi(string text, Action a) { return PopMenu.It(text, a); }

        /// <summary>带勾选的菜单项（「显示书签栏」那种）。</summary>
        private static PopItem Mi(string text, Action a, bool on) { return PopMenu.It(text, a, on); }

        /// <summary>分隔线（菜单项一律走 Mi，分隔线也收在这儿）。</summary>
        private static PopItem SepItem() { return PopMenu.Split(); }

        /// <summary>某个 ExplorerHost 现在在 hosts 里排第几（关标签会把索引挪位，按引用找）。</summary>
        private int IndexOfHost(ExplorerHost h)
        {
            for (int i = 0; i < hosts.Count; i++) if (hosts[i] == h) return i;
            return -1;
        }

        /// <summary>
        /// 「关掉它前面 / 后面的」那一项里那个方位词。
        /// 横排看的是左右，竖排侧边栏里看的是上下 —— 竖排还写成「左边」就是在指错方向（用户报的）。
        /// 日志也用同一个词，免得回头分不清用户点的是哪一项。
        /// </summary>
        private static string SideWord(bool before)
        {
            if (Settings.VTabs) return before ? "上方" : "下方";
            return before ? "左边" : "右边";
        }

        /// <summary>标签上右键：复制 / 打开 / 加进书签 / 关（含「关闭其它 / 前 / 后」—— 用户新增）。</summary>
        private void ShowTabMenu(int idx)
        {
            if (idx < 0 || idx >= hosts.Count || IsDisposed || Disposing) return;
            string title = tabStrip.Tabs[idx].Title;
            string live = LivePath(hosts[idx]);
            string target = PathRules.Restorable(live) ? live : hosts[idx].TargetPath;

            List<PopItem> m = new List<PopItem>();
            // 置顶（用户新增）：菜单文字随当前状态变，点一下就在两边切
            bool pinNow = hosts[idx].Pinned;
            m.Add(Mi("复制文件夹名", delegate { CopyText(title, "文件夹名"); }));
            m.Add(Mi("复制完整路径", delegate { CopyText(target, "完整路径"); }));
            m.Add(SepItem());
            m.Add(Mi(pinNow ? "取消置顶标签页" : "置顶标签页", delegate
            {
                int k = idx;
                bool want = !pinNow;
                Defer(delegate { SetTabPinned(k, want); });
            }));
            m.Add(Mi("在新标签页打开", delegate
            {
                if (!PathRules.Restorable(target))
                {
                    Toast.Show("开不了", "这个位置没有真实路径（库/虚拟文件夹）。");
                    return;
                }
                string p = target;
                Defer(delegate { NewTab(p); });
            }));
            m.Add(Mi("复制标签页", delegate
            {
                if (!PathRules.Restorable(target))
                {
                    Toast.Show("复制不了", "这个位置没有真实路径（库/虚拟文件夹）。");
                    return;
                }
                string p = target;
                Defer(delegate { NewTab(p); });
            }));
            // 交给系统开一扇**原生**窗口（不是我们的标签）：用户要拿它跟我们的嵌法做对照。
            // 关键是 Hub 那边要开一个短暂的让行期 —— 否则这扇窗会在零点几秒后被我们自己的
            // 捕获逻辑收编成标签，点一下就白点了（见 DesktopHub.OpenNative）。
            // 所以这里不判 `Restorable`：`shell:Downloads` / `::{GUID}` 这类虚拟位置
            // explorer.exe 自己也认得，交出去它就能开。
            m.Add(Mi("用原生资源管理器打开", string.IsNullOrEmpty(target)
                ? (Action)null
                : delegate
                {
                    string p = target;
                    Defer(delegate { if (hub != null) hub.OpenNative(p); });
                }));
            m.Add(Mi("添加到书签栏", delegate { Defer(delegate { AddToFavorites(target); }); }));
            // 跟空白右键、工具行那一项用**同一个名字**：三处说的是同一件事，
            // 名字不一致会让人以为是两个功能（用户报过）。
            m.Add(Mi("恢复关闭的标签页(" + Hotkeys.Combo("reopen") + ")",
                delegate { Defer(ReopenClosedTab); }));
            m.Add(SepItem());
            m.Add(Mi("关闭标签页(" + Hotkeys.Combo("closetab") + ")", delegate { Defer(delegate { CloseTab(idx); }); }));
            // ---- 多选了一批（Ctrl / Shift 点出来的）→ 给一条「一次关掉它们」----
            // 只在「右键点的这一个也在选中里」时出现 —— 否则用户会以为关的是他点的那一个。
            if (tabStrip.HasMultiSelection && tabStrip.IsSelected(idx))
                m.Add(Mi("关闭选中的 " + tabStrip.SelectedCount + " 个标签页",
                    delegate { Defer(CloseSelectedTabs); }));
            // ---- 用户新增的三条（跟浏览器右键对表）----
            // 只剩一个标签 / 当前就在最前（最后）时置灰 —— 点了什么也不发生的项还不如直接灰着
            m.Add(Mi("关闭其它标签页", hosts.Count > 1
                ? (Action)delegate { Defer(delegate { CloseOtherTabs(idx); }); } : null));
            m.Add(Mi("关闭" + SideWord(true) + "标签页", idx > 0
                ? (Action)delegate { Defer(delegate { CloseTabsBefore(idx); }); } : null));
            m.Add(Mi("关闭" + SideWord(false) + "标签页", idx < hosts.Count - 1
                ? (Action)delegate { Defer(delegate { CloseTabsAfter(idx); }); } : null));
            m.Add(SepItem());
            m.Add(Mi("更多选项（设置窗口）", delegate { Defer(ShowSettingsWindow); }));

            Rectangle b = TabBar.TabBounds(idx);
            Point at = TabBar.PointToScreen(new Point(b.Left + b.Width / 2, b.Bottom));
            PopMenu.Show(m.ToArray(), TabBar, TabBar.PointToClient(at),
                "标签右键 idx=" + idx);
        }

        /// <summary>
        /// 关掉标签条上被 Ctrl / Shift 挑中的那一批（用户 2026-09-25 要的「选中一批一起关」）。
        /// 走 `CloseTabsQuiet` —— 中间那几次 `Activate` 是「关一个闪一下」的来源；
        /// 而且要**从后往前关**，下标才不会在关的过程中错位。
        /// </summary>
        private void CloseSelectedTabs()
        {
            int[] selIdx = tabStrip.SelectedIndices;
            if (selIdx.Length == 0) return;
            // 全选中 = 至少留一个（跟「关闭其它标签页」同一个规矩：窗口不能没有标签）
            if (selIdx.Length >= hosts.Count)
            {
                tabStrip.ClearSelection();
                Toast.Show("留一个", "标签全被选中了，至少得留一个。");
                return;
            }
            // 当前标签不在这一批里的话，关完要切回它（在的话就就近落一个）
            ExplorerHost keep = (activeIndex >= 0 && activeIndex < hosts.Count
                                 && !tabStrip.IsSelected(activeIndex)) ? hosts[activeIndex] : null;
            tabStrip.ClearSelection();
            Diag.Step("EmbedForm: 关掉选中的 " + selIdx.Length + " 个标签页");
            CloseTabsQuiet(delegate
            {
                for (int k = selIdx.Length - 1; k >= 0; k--)
                {
                    int i = selIdx[k];
                    if (i >= 0 && i < hosts.Count) CloseTab(i, false);
                }
            });
            int n = keep == null ? -1 : IndexOfHost(keep);
            if (n < 0) n = hosts.Count - 1;
            if (n >= 0) Activate(n);
            MarkDirty();
        }

        /// <summary>关闭除了 keep 之外的所有标签。</summary>
        private void CloseOtherTabs(int keep)
        {
            if (keep < 0 || keep >= hosts.Count || hosts.Count <= 1) return;
            ExplorerHost k = hosts[keep];
            Diag.Step("EmbedForm: 关闭其它标签页（保留 idx=" + keep + "）");
            // ⚠ 批量关的时候**中间那一堆 Activate 全不要**（见 CloseTabsQuiet 的注释）。
            CloseTabsQuiet(delegate
            {
                for (int i = hosts.Count - 1; i >= 0; i--)
                {
                    if (hosts[i] != k && i < hosts.Count) CloseTab(i, false);
                }
            });
            int n = IndexOfHost(k);
            if (n >= 0) Activate(n);
        }

        /// <summary>关闭 idx 前面（不含）的所有标签。</summary>
        private void CloseTabsBefore(int idx)
        {
            if (idx <= 0 || idx >= hosts.Count) return;
            Diag.Step("EmbedForm: 关闭" + SideWord(true) + "标签页（idx=" + idx + " 前面共 " + idx + " 个）");
            int from = idx - 1;
            CloseTabsQuiet(delegate
            {
                for (int i = from; i >= 0; i--) CloseTab(i, false);
            });
            if (hosts.Count > 0) Activate(0);
        }

        /// <summary>关闭 idx 后面（不含）的所有标签。</summary>
        private void CloseTabsAfter(int idx)
        {
            if (idx < 0 || idx >= hosts.Count - 1) return;
            Diag.Step("EmbedForm: 关闭" + SideWord(false) + "标签页（idx=" + idx + " 后面共 " + (hosts.Count - 1 - idx) + " 个）");
            CloseTabsQuiet(delegate
            {
                for (int i = hosts.Count - 1; i > idx; i--) CloseTab(i, false);
            });
            if (hosts.Count > 0) Activate(Math.Min(idx, hosts.Count - 1));
        }

        /// <summary>
        /// 批量关标签：
        ///
        /// ⚠ 用户：「有多个其它标签页关闭时，当前标签页整个画面会闪烁」。
        /// 两个原因，这一个是主要的：`CloseTab` 本来**每关一个就 `Activate` 一次**，
        /// 批量关时就变成「关张三 → 显示李四 → 关李四 → 显示王五 → …」——
        /// 每一下都是把一个**真 explorer 窗口**现出来再藏掉，屏幕上看到的就是整个内容区在闪。
        /// 现在批量这条路把中间的 Activate 全按掉，循环跑完只定一次最终那个。
        /// （另一个原因在 `ExplorerHost.Close`：还回桌面之前忘了先 `SW_HIDE`。）
        /// </summary>
        private void CloseTabsQuiet(Action body)
        {
            if (body == null) return;
            bool old = closingBatch;
            closingBatch = true;
            try { body(); }
            finally { closingBatch = old; }
        }

        /// <summary>批量关标签期间为 true —— 这时候 `CloseTab` 不做收尾的 `Activate`。</summary>
        private bool closingBatch;

        /// <summary>把这个位置加进书签栏（书签是我们自己那份 data\favorites.json，见 FavStore）。</summary>
        private void AddToFavorites(string path)
        {
            string name;
            if (!FavStore.Add(path, out name))
            {
                Toast.Show("加不进书签", string.IsNullOrEmpty(name)
                    ? "这个位置没有真实路径（库 / 虚拟文件夹），书签放不了。"
                    : "「" + name + "」已经在书签里了。");
                return;
            }
            Diag.Step("EmbedForm: 加入书签 -> " + path);
            // 用户：「像已加入书签这种页面直接有反馈的，也不用右下角通知」——
            // 书签栏开着的话，那一项会**立刻出现在标签条下面那条栏上**，那就够了，不再弹气泡。
            // 只有书签栏是关着的时候才提示一句（那时界面上真的什么都没发生，不说一声就成了「点了没反应」）。
            if (favBar == null || !favBar.Visible) Toast.Show("已加入书签", name);
        }

        /// <summary>
        /// 标签条**空白处**右键（标签右边的空条 + 标签左边的空条都算）。
        ///
        /// 用户报「右边空白菜单的功能还没实现」—— 两个原因，都在这儿收掉：
        ///   ① 真的定位错了：标签溢出时最后半个标签的矩形伸到了按钮底下，右键落在那一块被
        ///      `HitTest` 认成「标签」而不是「空白」（修在 TabStrip.HitTest）；
        ///   ② 菜单里的东西太少。现在把新建 / 历史 / 恢复 / 书签栏 / 垂直侧边栏 / 设置都放进来。
        ///
        /// 「关闭其它 / 前 / 后」三条**只在标签右键里**（用户报：空白处不该有）—— 那三条是对
        /// 某一个标签说的，摆在空白处还得让人猜作用于哪个；空白处换成布局开关更顺。
        /// 每条都从 `Mi` 建 —— 点下去日志里会留一行，以后不用再猜「到底点没点中」。
        /// </summary>
        private void ShowBlankMenu()
        {
            if (IsDisposed || Disposing) return;
            List<PopItem> m = new List<PopItem>();
            m.Add(Mi("新建标签页(" + Hotkeys.Combo("newtab") + ")",
                delegate { Defer(delegate { NewTab(ExplorerView.ThisPcPath); }); }));
            m.Add(SepItem());
            m.Add(Mi("历史记录(" + Hotkeys.Combo("history") + ")", delegate { Defer(ShowHistoryMenu); }));
            m.Add(Mi("恢复关闭的标签页(" + Hotkeys.Combo("reopen") + ")", delegate { Defer(ReopenClosedTab); }));

            bool on = favBarOn;
            // 勾选走 `PopItem.On`（PopMenu 把它落到 `Checked`，菜单自己画勾）——
            // 不再用「✓ 」文字前缀：那会让这一行比同级项多两个字符、看着没对齐（用户报过）。
            m.Add(Mi("显示书签栏(" + Hotkeys.Combo("favbar") + ")", delegate
            {
                if (hub != null) hub.SetFavBar(!on);
            }, on));

            // 三个关标签的动作只在**标签右键**里有（它作用于某一个标签）。空白处放布局开关：
            // 这一条跟设置里的「垂直侧边栏」是同一个开关，勾选状态现问 Settings。
            m.Add(Mi("切换垂直侧边栏(" + Hotkeys.Combo("vtabs") + ")", delegate
            {
                if (hub != null) hub.SetVerticalTabs(!Settings.VTabs);
            }, Settings.VTabs));

            m.Add(SepItem());
            m.Add(Mi("更多选项（设置窗口）", delegate { Defer(ShowSettingsWindow); }));

            Control ow = blankOwner ?? tabStrip;
            Point at = ow.PointToScreen(blankAt);
            PopMenu.Show(m.ToArray(), ow, ow.PointToClient(at),
                "标签条空白右键");
        }

        private void CopyText(string text, string what)
        {
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                Clipboard.SetText(text);
                Toast.Show("已复制" + what, text);
            }
            catch (Exception ex) { Diag.Log("EmbedForm: 复制失败 " + ex.Message); }
        }

        /// <summary>
        /// 外面（书签管理器）让这个窗口把一个路径开成新标签。
        /// 跟 `OpenFromHistory` 同一条路：已经有一样的标签就切过去，别开两个。
        /// </summary>
        internal void OpenPathAsTab(string p)
        {
            if (IsDisposed || Disposing || string.IsNullOrEmpty(p)) return;
            if (!Visible) Show();
            if (!PathRules.Restorable(p))
            {
                Diag.Step("EmbedForm: 这个位置开不了（没有真实路径），跳过：" + p);
                return;
            }
            int dup = IndexOfPath(p);
            if (dup >= 0)
            {
                Diag.Step(string.Format("EmbedForm: 「{0}」已经有标签（第 {1} 个，共 {2} 个）-> 切过去", p, dup + 1, hosts.Count));
                Activate(dup);
            }
            else
            {
                Diag.Step(string.Format("EmbedForm: 「{0}」没有对应标签（现有 {1} 个）-> 新开一个", p, hosts.Count));
                NewTab(p);
            }
        }

        /// <summary>
        /// 某个路径是不是已经有标签了。
        /// 给「收编外面新开的窗口」用的：外面开的那扇窗如果**本来就是我们的标签**，
        /// 收下去就是第二个重复标签，应该改成切过去（见 `DesktopHub.AdoptWindow`）。
        /// </summary>
        internal bool HasTabForPath(string p)
        {
            if (IsDisposed || Disposing) return false;
            return IndexOfPath(p) >= 0;
        }

        /// <summary>标签个数（诊断日志用）。</summary>
        internal int TabCount { get { return hosts.Count; } }

        /// <summary>当前选中的标签下标（诊断日志用），没有标签时 -1。</summary>
        internal int ActiveIdx { get { return activeIndex; } }

        /// <summary>
        /// 书签栏开关。**所有入口都汇到这一条**（Ctrl+Shift+B、按钮、设置菜单、空白右键、栏上右键），
        /// 免得像当初「托盘没有设置项」那样漏一边。由 Hub 调（它要同时刷所有窗口 + 托盘菜单）。
        /// </summary>
        internal void SetFavBarOn(bool on)
        {
            if (IsDisposed || Disposing) return;
            // 窗口刚起来时 Hub 会用它同步一次当前设置 —— 那不是用户在按，不弹侧边栏
            bool syncOnly = !favBarEverSet;
            favBarEverSet = true;
            favBarOn = on;
            tabStrip.FavBarOn = on;
            if (vPane != null) vPane.FavBarOn = on;
            if (on) favBar.Reload();
            Diag.Step("EmbedForm: 书签栏 -> " + (on ? "显示" : "隐藏"));
            DoLayout();
            if (!syncOnly) PeekPane();
        }

        internal bool FavBarOn { get { return favBarOn; } }

        /// <summary>
        /// 激活 / 失活（Bug 6：未激活时窗口颜色要跟 Windows 原本的逻辑一致）。
        /// 原生标题栏失活会把标题字变灰、强调色变暗，我们照做：
        /// 收到 `WM_NCACTIVATE` 就切自绘标题栏和标签条的 Inactive，它们自己重画。
        /// </summary>
        private void SetInactive(bool inactive)
        {
            this.inactive = inactive;
            if (tabStrip != null) tabStrip.Inactive = inactive;
            if (vPane != null) vPane.Inactive = inactive;
            if (favBar != null) favBar.Inactive = inactive;
            ApplyTheme();    // 外壳底色也换成激活 / 失活那一套
        }

        // ==================================================================
        /// <summary>
        /// 兜底路径：按键只有在我们**自己的控件**（标签条 / 标题栏）持有焦点时才会走到这里。
        /// 焦点在嵌进来的 explorer 子进程里时根本走不到 —— 那条路全靠 WinEHook + Hub（见 DesktopHub.SetupHook）。
        /// </summary>
        protected override void OnPreviewKeyDown(PreviewKeyDownEventArgs e)
        {
            // 可自定义的那 7 条走同一张绑定表（`Hotkeys`）—— 跟钩子那条主路用的是**同一份**配置，
            // 不然改完快捷键会出现「explorer 里好使、点在我们自己的标签条上就不好使」这种怪事。
            string cmd = Hotkeys.MatchKeyData(e.KeyData);
            if (cmd != null)
            {
                HandleHotkey(cmd);
                e.IsInputKey = true;
                return;
            }
            // Ctrl+1..9 = 第 1..9 个标签（焦点在我们自己控件上时的兼顾路径；
            // 焦点在嵌进来的 explorer 里那条主路是 WinEHook + Hub，见 DesktopHub.SetupHook）
            if (e.Control && e.KeyCode >= Keys.D1 && e.KeyCode <= Keys.D9)
            {
                GotoTab(e.KeyCode - Keys.D1);
                e.IsInputKey = true;
                return;
            }
            base.OnPreviewKeyDown(e);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // 点 X / Alt+F4 只是收进托盘；只有托盘「退出」/ `--quit` / 系统关机才真的关。
            bool systemShutdown = e.CloseReason == CloseReason.WindowsShutDown
                               || e.CloseReason == CloseReason.TaskManagerClosing;
            if (!Quitting && !systemShutdown && e.CloseReason == CloseReason.UserClosing)
            {
                Diag.Step("EmbedForm: 收到关闭请求 reason=" + e.CloseReason + " -> 只收进托盘，不退进程");
                e.Cancel = true;
                HideToTray();
                return;
            }

            Diag.Step(string.Format("EmbedForm: OnFormClosing reason={0}，还有 {1} 个标签",
                e.CloseReason, hosts.Count));
            base.OnFormClosing(e);

            // 真关之前先把记忆落盘（系统关机这条路上 Hub 的 Quit 不一定会走到）。
            if (hub != null) hub.SaveNow("窗口真关 reason=" + e.CloseReason);

            for (int i = hosts.Count - 1; i >= 0; i--)
            {
                try
                {
                    hosts[i].Close("窗体关闭 reason=" + e.CloseReason);
                    hosts[i].Dispose();
                }
                catch (Exception ex) { Diag.Log("EmbedForm: 退出清理失败 " + ex.Message); }
            }
            hosts.Clear();

            // 备用窗口不在 hosts 里，得单独收干净 —— 不收就漏一个 explorer 进程 + 一个孤儿窗口
            KillReserve();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Diag.Step("EmbedForm: OnFormClosed reason=" + e.CloseReason + " 桌面=" + DesktopKey);
            // 滚轮登记表要摘掉 —— 不摘的话 `WheelRouter` 会一直攥着这个窗口句柄，
            // 句柄被复用时新窗口的滚轮会走错门。
            try { WheelRouter.Unregister(Handle); } catch { }
            // 设置窗口现在归 Hub 管（它会在自己 Dispose 时收）—— 这里不再碰。
            try { trimTimer.Stop(); trimTimer.Dispose(); } catch { }
            try { peekTimer.Stop(); peekTimer.Dispose(); } catch { }
            try { if (revealBeat != null) revealBeat.Dispose(); } catch { }
            DropGlass();
            base.OnFormClosed(e);   // 托盘/钩子/事件都不在这个类里（在 DesktopHub）
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.ApplyTitleBar(Handle);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // 深色标题栏必须在窗口**真正显示之后**再设一次。
            // OnHandleCreated 时 DWM 还没合成这个窗口，调用会被丢掉 —— 表现就是标题栏一直白，
            // 而这个窗口其他部分都是深色，那条白杠特别刺眼。
            Theme.ApplyTitleBar(Handle);
            SyncChrome();
            DoLayout();
        }
    }
}
