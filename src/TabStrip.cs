using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace TabbedExplorer
{
    /// <summary>
    /// 标签条 —— 照浏览器那套做（用户定：整个程序就是「仿浏览器设计、增强 Win10 资源管理器」）。
    ///
    /// 布局（从右往左）：
    ///   [×][□][—]  [齿轮] | [书签栏] [恢复关闭] [历史]  ……空白……  [+ 紧跟最后一个标签]
    /// 最右边那三个是**窗口按钮**（最小化 / 最大化 / 关闭），齿轮单独用一条竖线隔开
    /// （跟 Edge 一样：扩展一块、头像一块）。
    ///
    ///第二次改（用户报的 bug 2 + 评估 1）：
    ///   · 标签**只有一行** —— 就显示文件夹名（上一轮做成两行是改错了）；
    ///     完整路径挪到**悬停提示**里（提示里名字和路径一样时就只显示名字，别重复两遍）。
    ///   · **自绘标题栏整条去掉了**（原来那排「模仿资源管理器快速访问工具栏」的图标点了没反应），
    ///     标签条因此升到最顶上，窗口按钮并到它最右边。见 TitleBar 已删除的说明。
    /// 整条标签条的**空白处**现在兼任标题栏：按住可以拖窗口（贴边吸附、双击最大化由系统做）。
    ///
    /// 尺寸全部走 DpiScale（硬编码在 150% 下会错位）。
    /// </summary>
    internal sealed class TabStrip : Control
    {
        /// <summary>右侧那排功能按钮（齿轮单独一个，见 EnsureLayout）。</summary>
        public enum Tool { History, Reopen, Fav, Settings }
        /// <summary>最右边的窗口按钮（顺序照原生：最小化、最大化/还原、关闭）。</summary>
        public enum WBtn { Minimize, Maximize, Close }

        public sealed class TabItem
        {
            public string Title = "";
            /// <summary>完整路径 —— **不显示在标签上**，只给悬停提示用（「此电脑」这类是友好名）。</summary>
            public string Path = "";
            public bool Active;
            /// <summary>当前文件夹的图标（由上层从 explorer 窗口读出来，导航后会换）。</summary>
            public Image Icon;
            /// <summary>标题文字的像素宽（只在换标题时量一次 —— 自适应宽度每个鼠标事件都要用它算布局）。</summary>
            public int TextW;
            /// <summary>置顶了（右键「置顶标签页」）—— 排在最前面，名字前面多一枚小图钉。</summary>
            public bool Pinned;
        }

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
        /// 标签条标准高度（逻辑像素）。**一行**文字，所以比两行那版矮回来。
        /// 30 = 跟垂直侧边栏的折叠宽度（`EmbedForm.PaneCollapsedL`）、书签栏（`FavBar.StdHeight`）**同一个数**：
        /// 三处的图标都是 `IconSize` 一样大，横向那条比侧栏宽一截就显得「太厚」（用户：「水平状态感觉太高了，
        /// 不和谐……垂直时宽度倒是挺合适的」）。要再矮就得先看顶部那条滚动条（`LayoutScrollBar`，高 `Px(4)`，
        /// 压在 y=0）还够不够点。
        /// </summary>
        public const int StdHeight = 30;

        /// <summary>自己那份标签模型（没挂镜像时用的就是它）。</summary>
        private readonly List<TabItem> ownTabs = new List<TabItem>();
        /// <summary>
        /// 当前用的标签模型 —— 默认指向 `ownTabs`。
        /// 垂直模式下左侧窗格**是另一个 TabStrip 实例**，它 `MirrorFrom(顶部那条)` 之后这个字段
        /// 就指向**同一个 List**：上层增删改只调一次，两个视图看到的永远是同一份数据，
        /// 不必写「两个标签条之间的同步」那种一定会漏的代码。
        /// </summary>
        private List<TabItem> tabs;
        /// <summary>镜像对端 —— 这边重画时那边跟着重画（见 Redraw）。</summary>
        private TabStrip peer;
        private readonly List<Rectangle> bounds = new List<Rectangle>();

        /// <summary>
        /// 悬停提示。⚠ `ShowAlways = true` 是必须的：不开的时候**宿主窗口不是前台就不弹** ——
        /// 用户报的「未激活时移到标签上没显示名字，可关闭按钮会变色」就是这个（鼠标事件收到了，提示被憋掉了）。
        /// 配色走 `Theme.StyleTip`（自绘，跟着颜色模式走）。
        /// </summary>
        private readonly ToolTip tips = new ToolTip();

        private int hoverIndex = -1;
        /// <summary>
        /// 多选（Ctrl 单个加/减、Shift 连一段）—— 用户 2026-09-25 要的「挑一批一起关」。
        /// 空集合 = 没多选，一切行为跟从前完全一样。
        /// ⚠ 它跟「当前标签」（`TabItem.Active`）**是两件事**：多选不动当前标签，当前标签也不自动进多选。
        /// ⚠ 标签增删移之后下标会错位，所以这三处一律把选择清掉（见 AddTab / RemoveTab / MoveTab）。
        /// </summary>
        private readonly HashSet<int> sel = new HashSet<int>();
        /// <summary>Shift 连选那一段的锚点（最近一次落在哪个标签上）。-1 = 还没有锚点。</summary>
        private int selAnchor = -1;
        private int hoverCloseIndex = -1;
        private bool hoverNew;
        private int hoverTool = -1;
        private int hoverWBtn = -1;
        /// <summary>现在弹着提示的是谁（`"tab:3"` / `"close:0"` / `"tool:2"` …）。换了才重弹，不然鼠标一动就闪。</summary>
        private string tipKey;
        private int dragFromIndex = -1;
        private int dragOverIndex = -1;
        /// <summary>在空白处按下左键后，鼠标有没有真的移动过（决定松手时是「拖窗口」还是「什么都没干」）。</summary>
        private bool blankDrag;
        /// <summary>空白处按下的位置（拖窗口前要比一下走了多远）。</summary>
        private Point blankFrom;
        /// <summary>
        /// 按在右侧功能按钮上、但**还没松手**的那一颗。
        /// 功能按钮的事件只在松手时发（见 OnMouseUp 里那段「为什么不能在这一刻响应」）。
        /// </summary>
        private int pendingTool = -1;

        // 位置全部由 EnsureLayout 算（标签数 / 窗口宽 / 右侧那排按钮都会变）。
        private Rectangle newRect;
        private Rectangle settingsRect;
        // ⚠ 这两个**必须是实例字段**，不能是 static：垂直模式下左侧窗格和顶部那条是
        //   **同一个类的两个实例**，static 会让两边互相把对方的排版矩形冲掉。
        private readonly Rectangle[] toolRects = new Rectangle[4];   // 见 Tool 枚举
        private readonly Rectangle[] wbtnRects = new Rectangle[3];   // 见 WBtn 枚举
        private int dividerX;
        private int toolsLeft;
        /// <summary>标签区右界（画的时候裁到这儿、命中判定也以它为准）。由 EnsureLayout 算一次，两边共用。</summary>
        private int tabsClipRight;

        // ---- 垂直窗格的排法结果（横向那条不读这几个）----
        /// <summary>垂直窗格：标签区上界 = 顶部那排工具按钮的下沿。</summary>
        private int tabsTopV;
        /// <summary>垂直窗格：标签区下界（画和命中判定都裁到这儿，下面要么是书签区要么是窗口按钮）。</summary>
        private int tabsBottomV;
        /// <summary>垂直窗格：底部窗口按钮那一块的上沿。</summary>
        private int winTopV;
        /// <summary>垂直窗格：书签区**实际留出**的高度（可能被挤压，见 BookmarkBand）。</summary>
        private int favBandH;

        // 标签宽度：跟着设置走（逻辑像素 ×DPI）。
        //   `MaxTabWidth` = 「标签页宽度」那一项，也就是**基准宽度**；
        //   `WidenMaxWidth` = 开「名称过长时自动加宽」后**最多能加到多宽**（见 EnsureLayout ①）。
        private int MinTabWidth { get { return Math.Min(Px(72), MaxTabWidth); } }
        private int MaxTabWidth { get { return Px(Settings.TabWidth); } }
        private int WidenMaxWidth { get { return Math.Max(MaxTabWidth, Px(Settings.TabWidenMax)); } }
        private int NewButtonWidth { get { return Px(28); } }
        private int ToolButtonWidth { get { return Px(32); } }
        private int SettingsButtonWidth { get { return Px(34); } }
        /// <summary>窗口按钮宽度。跟原生标题栏一个尺寸（46 逻辑像素）。</summary>
        private int WBtnWidth { get { return Px(46); } }
        /// <summary>齿轮左边那条竖线占的宽度（含两侧留白）。</summary>
        private int DividerWidth { get { return Px(11); } }
        private int CloseAreaWidth { get { return Px(22); } }
        private int CloseBoxSize { get { return Px(16); } }

        private static int IconSize { get { return Px(16); } }
        /// <summary>置顶标签名前那枚小图钉占的宽度（含右侧留白）。</summary>
        private int PinAreaWidth { get { return Px(15); } }
        /// <summary>标签图标的目标尺寸（设备像素）。外面给 ExplorerHost 设尺寸时用这个。</summary>
        public static int TabIconSize { get { return IconSize; } }
        private const int TextPadLeft = 6;
        private const int IconGap = 5;
        private static Bitmap folderIcon;
        private static bool folderTried;

        private static bool EnsureFolderIcon()
        {
            if (folderIcon == null && !folderTried)
            {
                folderTried = true;
                folderIcon = ShellIcon.FolderIcon(IconSize);
            }
            return folderIcon != null;
        }

        private readonly Font titleFont;      // 标签文字（**不加粗** —— 用户：加粗留给悬停提示）
        private readonly Font tipTitleFont;   // 悬停提示里「文件夹名」那一行的字体（加粗）
        private readonly Font glyphFont;      // 右侧工具按钮的 MDL2 字形
        /// <summary>
        /// 窗口按钮（最小化 / 最大化 / 关闭）的字形 —— **比工具按钮小一号**。
        /// MDL2 里这三个字形（E921/E922/E923/E8BB）是**填满 em 框**的，同样 12px 下看着比
        /// 齿轮 / 星星 / 历史那几个大一圈（用户报的「三个按钮图标太大了，和其它不统一」）。
        /// 10px 正好是 Win10 原生标题栏里这三个字形的尺寸（96dpi 下量到约 10px）。
        /// </summary>
        private readonly Font wbtnFont;
        /// <summary>
        /// 书签那枚星（E734/E735）单独用大一档的字号 ——
        /// 它是空心/实心五角星，ink 天生比齿轮（自绘）、历史（E81C 圆盘）、恢复（E7A7 弯箭头）小一圈，
        /// 同一个字号并排会明显看着小（用户：「右上三颗窗控图标已经一样大了，书签图标有点小」）。
        /// </summary>
        private readonly Font favGlyphFont;
        /// <summary>
        /// 置顶标签上那枚小图钉（MDL2 `\uE718`）的字号。比工具按钮再小一号 ——
        /// 它只是个「这个标签被钉住了」的标记，不该跟文件夹名抢视觉重量（用户：小标志、不占地方）。
        /// </summary>
        private readonly Font pinFont;
        /// <summary>窗口没激活（失活）时整体降色 —— 底色和文字都跟原生标题栏一个逻辑。</summary>
        public bool Inactive
        {
            get { return inactive; }
            set
            {
                if (inactive == value) return;
                inactive = value;
                BackColor = BarBack;
                Redraw();
            }
        }
        private bool inactive;

        /// <summary>标签条底：激活 / 未激活两套色（用户要的「主窗口也模拟原生激活逻辑」）。</summary>
        private Color BarBack { get { return inactive ? Theme.TabBarOff : Theme.TabBar; } }

        /// <summary>窗口最大化着没（决定右上角那颗画「最大化」还是「还原」）。</summary>
        public bool Maximized
        {
            get { return maximized; }
            set { if (maximized != value) { maximized = value; Redraw(); } }
        }
        private bool maximized;

        /// <summary>书签栏现在是开着的（按钮画成实心星）。</summary>
        public bool FavBarOn
        {
            get { return favBarOn; }
            set { if (favBarOn != value) { favBarOn = value; Redraw(); } }
        }
        private bool favBarOn;

        /// <summary>
        /// 垂直模式：这个标签条当**左侧窗格**用 —— 整个窗口就靠这一条，
        /// 工具按钮 / 加号 / 标签行 / 书签区位 / 窗口按钮全在里面从上往下摞
        /// （这时顶部那条横向的**整条隐藏**，见 EmbedForm.DoLayout）。
        /// </summary>
        public bool Vertical { get; set; }

        /// <summary>
        /// 垂直模式的折叠态（默认）：整条只有顶上「＋」、底下「×」两颗，标签行只画图标 ——
        /// 鼠标移进来窗格摊开才显示其余工具、标题和书签段（Edge 的「折叠窗格」就是这意思）。
        /// </summary>
        public bool Collapsed
        {
            get { return collapsed; }
            set { if (collapsed != value) { collapsed = value; Redraw(); } }
        }
        private bool collapsed;

        /// <summary>
        /// 垂直窗格顶部那枚图钉现在是「开」还是「关」（开 = 折叠窗格：焦点不在就只显示图标）。
        /// 只影响画成什么颜色，折叠与否由上层（EmbedForm）算完窗格宽度再告诉我们。
        /// </summary>
        public bool PinOn
        {
            get { return pinOn; }
            set { if (pinOn != value) { pinOn = value; Redraw(); } }
        }
        private bool pinOn;

        /// <summary>
        /// 垂直窗格底部留给**书签区**的高度（0 = 不留）。
        /// 垂直模式下书签栏是另一个控件（`FavBar`），由上层摆进这块地方里 ——
        /// 这里只负责给它腾位子、并且让标签列表别画到它头上。两边互不认识对方。
        /// </summary>
        public int BookmarkBand
        {
            get { return bookmarkBand; }
            set { if (bookmarkBand != value) { bookmarkBand = value; Redraw(); } }
        }
        private int bookmarkBand;

        /// <summary>书签区那块矩形（窗格客户坐标）—— 上层拿它摆书签控件。</summary>
        public Rectangle BookmarkBandBounds
        {
            get
            {
                EnsureLayout();
                return new Rectangle(0, winTopV - favBandH, Width, Math.Max(0, favBandH));
            }
        }

        /// <summary>底部窗口按钮那一块的上沿（上层要在它上面摆东西时用得到）。</summary>
        public int WindowRowTop
        {
            get { EnsureLayout(); return winTopV; }
        }

        /// <summary>垂直窗格顶部那枚图钉被点了（上层拿它开关「折叠窗格」）。</summary>
        public event Action PinClicked;

        /// <summary>重画自己 **+ 镜像对端** —— 两边渲染同一份模型，一边变了另一边必须跟着重算。</summary>
        private void Redraw()
        {
            Invalidate();
            TabStrip p = peer;
            if (p != null && !p.IsDisposed) p.Invalidate();
        }

        /// <summary>
        /// 把标签模型挂到**另一个标签条**上（垂直窗格用的就是这条）。
        /// 挂上之后两边共用同一个 `List&lt;TabItem&gt;`，上层只调一次就够了。
        /// ⚠ 镜像方是**只读**的：别对镜像实例调 AddTab / RemoveTab / MoveTab（那会动到源的头）。
        /// </summary>
        public void MirrorFrom(TabStrip source)
        {
            tabs = (source == null) ? ownTabs : source.tabs;
            peer = source;
            if (source != null) source.peer = this;
            Redraw();
        }

        public delegate void IndexEventHandler(object sender, int index);

        public event IndexEventHandler TabClicked;
        public event IndexEventHandler TabCloseClicked;
        public event EventHandler NewTabClicked;
        /// <summary>标签上按了右键（要弹「复制 / 关闭 / 复制路径」）。</summary>
        public event IndexEventHandler TabRightClicked;
        /// <summary>右侧那排按钮被点了（含齿轮）。</summary>
        public event Action<Tool> ToolClicked;
        /// <summary>最右边的窗口按钮被点了（最小化 / 最大化还原 / 关闭）。</summary>
        public event Action<WBtn> WindowButtonClicked;
        public event IndexEventHandler TabMiddleClicked;
        public event IndexEventHandler OrderChanged;   // 拖拽排序后：原索引
        /// <summary>
        /// 拖拽排序**即将**生效（`from` → `to`）。跟 `OrderChanged` 的区别是它带上了目标位置 ——
        /// 上层要挪自己那份平行列表（EmbedForm 的 `hosts`）就得知道挪到哪儿。
        /// 在 `MoveTab` **之前**发：上层先挪它那份、我们再做视图这一侧，不会两边各挪一次。
        /// </summary>
        public event Action<TabStrip, int, int> OrderMoved;
        /// <summary>标签条**空白区域**（不是标签、不是按钮）上按了右键。</summary>
        public event Action<Point> BlankRightClicked;
        /// <summary>在标签条上滚滚轮（用户：标签条上滚轮 = 切换前后标签页）。参数是 delta（正=往上滚=上一个）。</summary>
        public event Action<int> TabWheel;

        public TabStrip()
        {
            tabs = ownTabs;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.Selectable, true);
            Height = Px(StdHeight);
            // 标签标题**不加粗**（用户：加粗留给悬停提示）；悬停提示里名字那行加粗、路径不加粗。
            titleFont = new Font("Segoe UI", Px(12), FontStyle.Regular, GraphicsUnit.Pixel);
            tipTitleFont = new Font("Segoe UI", Px(12), FontStyle.Bold, GraphicsUnit.Pixel);
            glyphFont = new Font("Segoe MDL2 Assets", Px(12), FontStyle.Regular, GraphicsUnit.Pixel);
            wbtnFont = new Font("Segoe MDL2 Assets", Px(10), FontStyle.Regular, GraphicsUnit.Pixel);
            favGlyphFont = new Font("Segoe MDL2 Assets", Px(14), FontStyle.Regular, GraphicsUnit.Pixel);
            // 置顶标签上那枚小图钉：比工具按钮再小一号 —— 它只是个标记，不该抢标题的视觉重量
            pinFont = new Font("Segoe MDL2 Assets", Px(11), FontStyle.Regular, GraphicsUnit.Pixel);
            BackColor = Theme.TabBar;
            AllowDrop = true;
            tips.InitialDelay = 350;    // 停一下再弹，别鼠标一扫过就满屏提示
            tips.ReshowDelay = 80;
            tips.AutoPopDelay = 8000;
            tips.ShowAlways = true;     // 见字段注释：不开的话窗口没激活就不弹
            Theme.StyleTip(tips, tipTitleFont, titleFont);   // 名字那行粗体、路径行常规（量尺寸也要按粗体量）
            Theme.Changed += delegate { BackColor = BarBack; tips.BackColor = Theme.MenuBack; tips.ForeColor = Theme.Text; Redraw(); };
        }

        public IList<TabItem> Tabs { get { return tabs; } }

        public void AddTab(string title)
        {
            tabs.Add(new TabItem { Title = title ?? "", TextW = MeasureTitle(title) });
            sel.Clear(); selAnchor = -1;   // 下标全变了，选择作废（见 sel 那段说明）
            Redraw();
        }

        public void SetTitle(int index, string title)
        {
            if (index < 0 || index >= tabs.Count) return;
            if (tabs[index].Title == title) return;
            tabs[index].Title = title ?? "";
            tabs[index].TextW = MeasureTitle(tabs[index].Title);
            Redraw();
        }

        /// <summary>标题文字有多宽（自适应宽度要用）。空标题给个最小宽度，别缩成一条缝。</summary>
        private int MeasureTitle(string s)
        {
            if (string.IsNullOrEmpty(s)) return Px(40);
            try
            {
                Size sz = TextRenderer.MeasureText(s, titleFont, new Size(Px(800), Px(40)),
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                return Math.Max(Px(30), sz.Width);
            }
            catch { return Px(60); }
        }

        /// <summary>换完整路径 —— 只在悬停提示里用（「此电脑」这类没有真实路径的由上层给友好名）。</summary>
        public void SetPath(int index, string path)
        {
            if (index < 0 || index >= tabs.Count) return;
            if (tabs[index].Path == path) return;
            tabs[index].Path = path ?? "";
            Redraw();
        }

        /// <summary>换掉某个标签的图标（当前文件夹的实时图标，导航后上层会再调）。</summary>
        public void SetIcon(int index, Image icon)
        {
            if (index < 0 || index >= tabs.Count) return;
            if (tabs[index].Icon == icon) return;
            tabs[index].Icon = icon;
            Redraw();
        }

        /// <summary>改某个标签的「置顶」标记（排序由上层做，这里只管那枚小图钉）。</summary>
        public void SetPinned(int index, bool pinned)
        {
            if (index < 0 || index >= tabs.Count) return;
            if (tabs[index].Pinned == pinned) return;
            tabs[index].Pinned = pinned;
            Redraw();
        }

        public void RemoveTab(int index)
        {
            if (index < 0 || index >= tabs.Count) return;
            tabs.RemoveAt(index);
            sel.Clear(); selAnchor = -1;   // 后面的下标都往前挪了一位，选择作废
            if (hoverIndex == index) hoverIndex = -1;
            // 不用把 scrollX 归零：下一次 EnsureLayout 会把它夹到新的 maxScroll 上
            // （归零反而会让用户刚滑到的位置白滑 —— 关一个标签不该把视口弹回最左边）。
            Redraw();
        }

        public void SetActive(int index)
        {
            for (int i = 0; i < tabs.Count; i++) tabs[i].Active = (i == index);
            ScrollActiveIntoView();   // 选中的标签不许停在屏幕外（浏览器都这么做）
            Redraw();
        }

        // ------------------------------------------------------------------
        // 多选（Ctrl / Shift）
        // ------------------------------------------------------------------

        /// <summary>选中的标签个数（0 = 没多选）。</summary>
        public int SelectedCount { get { return sel.Count; } }

        /// <summary>现在是不是有多个标签被选中（调用方据此决定要不要给「一批一起关」）。</summary>
        public bool HasMultiSelection { get { return sel.Count > 1; } }

        /// <summary>选中的标签下标（从小到大）。关的时候**从后往前**用，下标才不会错位。</summary>
        public int[] SelectedIndices
        {
            get
            {
                int[] a = new int[sel.Count];
                sel.CopyTo(a);
                Array.Sort(a);
                return a;
            }
        }

        /// <summary>某个下标现在被多选选中了吗。</summary>
        public bool IsSelected(int index) { return sel.Contains(index); }

        /// <summary>清掉多选。返回「本来有没有选中的」（`false` 就不用重画了）。</summary>
        public bool ClearSelection()
        {
            selAnchor = -1;
            if (sel.Count == 0) return false;
            sel.Clear();
            Redraw();
            return true;
        }

        /// <summary>
        /// Ctrl / Shift 点在一个标签上要做什么（横排竖排共用一个入口）。
        /// 返回 true = 这一下是「挑标签」，调用方别再当成「切到它」、也别当拖排序。
        /// </summary>
        private bool ApplyMultiSelect(int idx)
        {
            bool ctrl = (ModifierKeys & Keys.Control) == Keys.Control;
            bool shift = (ModifierKeys & Keys.Shift) == Keys.Shift;
            if (!ctrl && !shift) return false;

            if (shift && selAnchor >= 0)
            {
                // Shift：从锚点连到这儿一段（锚点 = 上一次点过的那个）
                sel.Clear();
                int a = Math.Min(selAnchor, idx), b = Math.Max(selAnchor, idx);
                for (int i = a; i <= b; i++) sel.Add(i);
            }
            else
            {
                // Ctrl（或还没有锚点时的 Shift）：单个加 / 减
                if (!sel.Remove(idx)) sel.Add(idx);
                selAnchor = idx;
            }
            dragFromIndex = -1;
            dragOverIndex = -1;
            Redraw();
            return true;
        }

        /// <summary>普通点一下标签 / 点空白 = 退出多选（浏览器都这样）。</summary>
        private void DropSelectionOnPlainClick()
        {
            selAnchor = -1;
            if (sel.Count == 0) return;
            sel.Clear();
            Redraw();
        }

        /// <summary>
        /// 把当前选中的标签**滚进可视区**。
        /// 少了这一步就会出现「点了个被挡住的标签，人还在原地、看着像什么都没发生」。
        /// 只在溢出时动 scrollX，刚好露不完全就贴边，别每次都居中（那样鼠标下会乱跳）。
        /// </summary>
        public void ScrollActiveIntoView()
        {
            EnsureLayout();
            if (maxScroll <= 0) return;
            int i = ActiveIndex;
            if (i < 0 || i >= bounds.Count) return;
            if (Vertical)
            {
                // 竖排：把选中的那一行拉进标签区（工具行 / 书签区那两块不算可视区）
                int ny = scrollX;
                if (bounds[i].Top < tabsTopV) ny = scrollX + (bounds[i].Top - tabsTopV);
                else if (bounds[i].Bottom > tabsBottomV) ny = scrollX + (bounds[i].Bottom - tabsBottomV);
                if (ny < 0) ny = 0;
                if (ny > maxScroll) ny = maxScroll;
                if (ny == scrollX) return;
                scrollX = ny;
                Redraw();
                return;
            }
            int nx = scrollX;
            if (bounds[i].Left < 0) nx = scrollX + bounds[i].Left;
            else if (bounds[i].Right > tabsClipRight) nx = scrollX + (bounds[i].Right - tabsClipRight);
            if (nx < 0) nx = 0;
            if (nx > maxScroll) nx = maxScroll;
            if (nx == scrollX) return;
            scrollX = nx;
            Redraw();
        }

        public int ActiveIndex
        {
            get { for (int i = 0; i < tabs.Count; i++) if (tabs[i].Active) return i; return -1; }
        }

        public void MoveTab(int from, int to)
        {
            if (from < 0 || from >= tabs.Count || to < 0 || to >= tabs.Count || from == to) return;
            TabItem it = tabs[from];
            tabs.RemoveAt(from);
            tabs.Insert(to, it);
            sel.Clear(); selAnchor = -1;   // 顺序变了，选择作废
            Redraw();
        }

        // ------------------------------------------------------------------
        /// <summary>
        /// 排一次版。右侧那一排是**固定**的（窗口按钮 + 齿轮 + 竖线 + 三个功能按钮），
        /// 标签和「+」只在剩下的宽度里排。尺寸只由 Width + 标签数决定，鼠标事件里可以随手重算。
        ///
        /// 宽度规则（用户要求拆成两项）：
        ///   ① `tabautowiden` 开 → 每个标签按**自己那行文字**的宽度来（夹在 MinTabWidth ~ TabWidth 之间）；
        ///      关 → 一律 `TabWidth`。
        ///   ② `tabautofit` 开 → 全排加起来挤不下就**一起缩窄**（下限 MinTabWidth）；
        ///      关 → 不缩，但**总宽仍然不越过右边那排按钮**：多出来的部分靠横向滚动看（`scrollX`）。
        /// </summary>
        private void EnsureLayout()
        {
            // 垂直窗格和顶部那条是**同一套代码的两种排法**（见 Vertical）：
            // 一个控件只可能是其中一种，这里分一下路，两边互不打扰。
            if (Vertical) { EnsureLayoutV(); return; }
            EnsureLayoutH();
        }

        /// <summary>横向（顶部那条）的排法。</summary>
        private void EnsureLayoutH()
        {
            // 最右：窗口按钮（从右往左：关闭 → 最大化 → 最小化）
            wbtnRects[(int)WBtn.Close] = new Rectangle(Width - WBtnWidth, 0, WBtnWidth, Height);
            wbtnRects[(int)WBtn.Maximize] =
                new Rectangle(wbtnRects[(int)WBtn.Close].Left - WBtnWidth, 0, WBtnWidth, Height);
            wbtnRects[(int)WBtn.Minimize] =
                new Rectangle(wbtnRects[(int)WBtn.Maximize].Left - WBtnWidth, 0, WBtnWidth, Height);

            // 再往左：齿轮 → 竖线 → 书签栏 → 恢复关闭 → 历史
            int right = wbtnRects[(int)WBtn.Minimize].Left;
            settingsRect = new Rectangle(right - SettingsButtonWidth, 0, SettingsButtonWidth, Height);
            int divL = settingsRect.Left - DividerWidth;
            dividerX = divL + DividerWidth / 2;
            toolRects[(int)Tool.Fav] = new Rectangle(divL - ToolButtonWidth, 0, ToolButtonWidth, Height);
            toolRects[(int)Tool.Reopen] = new Rectangle(toolRects[(int)Tool.Fav].Left - ToolButtonWidth, 0, ToolButtonWidth, Height);
            toolRects[(int)Tool.History] = new Rectangle(toolRects[(int)Tool.Reopen].Left - ToolButtonWidth, 0, ToolButtonWidth, Height);
            toolsLeft = toolRects[(int)Tool.History].Left;

            bounds.Clear();
            // 标签区右界：「+」也得排得下，所以再让出一个「+」的宽度
            int areaRight = Math.Max(Px(40), toolsLeft - NewButtonWidth - Px(2));
            int avail = Math.Max(Px(30), areaRight - Px(2));
            tabsClipRight = Math.Max(0, areaRight);

            // ① 每个标签想要的宽度
            //
            // ⚠ 用户报「开启名称过长时自动加宽时，单标签页并未加宽」——
            //   根因：原来把加宽也卡在 `TabWidth` 上（`min(MaxTabWidth, need)`），
            //   于是名字再长、宽也超不过「标签页宽度」那一项，看着就是没加宽。
            //   现在 `TabWidth` 是**基准宽度**：名字放得下就一样宽，放不下才往上加，上限 `TabWidenMax`。
            int[] want = new int[tabs.Count];
            int total = 0;
            for (int i = 0; i < tabs.Count; i++)
            {
                int t = tabs[i].TextW;
                if (t <= 0) t = MeasureTitle(tabs[i].Title);
                int pin = tabs[i].Pinned ? PinAreaWidth : 0;
                int w = MaxTabWidth;
                if (Settings.TabAutoWiden)
                {
                    // 图标 + 左右留白 + 右边给关闭按钮留位
                    int need = Px(TextPadLeft) + IconSize + Px(IconGap) + pin + t + CloseAreaWidth + Px(4);
                    w = Math.Max(MaxTabWidth, Math.Min(WidenMaxWidth, need));
                    // ⚠ 用户报「过长依然出现遮挡问题（没收到滚动条范围内）」：
                    //   原来这里还把 w 再夹一次「标签区可用宽度」（`if (w > avail) w = avail`），
                    //   于是超长名字的标签被硬切成 avail 宽 —— `total == avail`，
                    //   `maxScroll` 只剩 Px(4)，**被吃掉的那一截滚也滚不出来**，看着就是「被遮挡」。
                    //   现在不夹了：名字有多长标签就有多宽（上限 `TabWidenMax`），
                    //   超出可视区的那部分交给横向滚动露出来 —— 这正是「自动加宽 + 滚动」的分工。
                    //   想「谁也别挤谁」就把「自动缩窄」打开，那条路是 `TabAutoFit`，别在这儿夹。
                }
                if (w < MinTabWidth) w = MinTabWidth;
                // 置顶那枚小图钉要占地方的 —— 不补这一段，图钉就会把标题挤掉一截
                w += pin;
                want[i] = w;
                total += w;
            }

            // ② 挤不下就一起缩窄（这一项单独有开关）
            if (tabs.Count > 0 && Settings.TabAutoFit && total > avail)
            {
                int w = Math.Max(MinTabWidth, avail / tabs.Count);
                for (int i = 0; i < want.Length; i++) want[i] = w;
                total = w * tabs.Count;
            }

            // ③ 没开缩窄：总宽也不许越过右边按钮 —— 多的部分靠横向滚动
            maxScroll = Math.Max(0, total + Px(4) - avail);
            if (scrollX > maxScroll) scrollX = maxScroll;
            if (scrollX < 0) scrollX = 0;
            overflow = (maxScroll > 0);

            int x = Px(2) - scrollX;
            for (int i = 0; i < tabs.Count; i++)
            {
                bounds.Add(new Rectangle(x, 0, want[i], Height));
                x += want[i];
            }

            // 「+」紧跟在最后一个标签右边；挤到边上了就贴在按钮左边（浏览器就是这样）
            // ⚠ 用户报「标签占满后，关闭按钮和加号堆叠」：
            //   原来这里是 `limit = areaRight - NewButtonWidth`，可 `areaRight` 上面**已经**扣过一个
            //   `NewButtonWidth` 了（见 357 行注释）—— 又扣一次，「+」被硬推到最后一个标签的关闭
            //   按钮底下，两个按钮就重在一起。areaRight 本身就是「标签区右界」，「+」只要不越过它即可。
            int nx = (tabs.Count > 0) ? x + Px(4) : Px(4);
            int limit = areaRight;
            if (nx > limit) nx = Math.Max(Px(2), limit);
            newRect = new Rectangle(nx, 0, NewButtonWidth, Height);
        }

        // ==================================================================
        // 垂直窗格（用户：打开/关闭垂直侧边栏 Ctrl+Shift+,）
        //
        // 就是**同一个类的另一种排法**，而且是垂直模式下**唯一**的一条：
        // 展开时从上往下是 工具行（加号 + 历史 / 恢复 + 齿轮，图钉在右端）→ 标签行 → 书签段 →
        // 窗口按钮（底部**左起**：关闭 / 放大 / 缩小）。
        // 顶部那条横向的在垂直模式下**整条隐藏**（不是留着当标题栏）——
        // 用户：「我就是觉得有一行空的很丑」，所以窗口最上面一行就是内容。
        // 折叠态（默认）只留**顶上「＋」和底下「×」**两颗，中间是图标版的标签行；
        // 展开（鼠标移进来）才是图标 + 标题 + 关闭按钮 + 书签段。
        // ==================================================================

        /// <summary>垂直模式每行标签的高度（逻辑像素）。</summary>
        private const int VRowHeightL = 32;
        /// <summary>垂直模式顶部工具行的下沿（两种状态共用 —— 摊开时标签列表不上下跳）。</summary>
        private const int VToolRowL = 34;
        /// <summary>垂直模式底部窗口按钮那一块的上沿（两种状态共用）。</summary>
        private const int VWinRowL = 34;
        /// <summary>垂直模式里一个图标格子的大小 —— 工具按钮 / 图钉 / 窗口按钮都用它。</summary>
        private const int VCellL = 26;

        private Rectangle pinRect;
        private bool hoverPin;

        private int VRowH { get { return Px(VRowHeightL); } }
        private int VCell { get { return Px(VCellL); } }
        private int VGap { get { return Px(2); } }

        /// <summary>
        /// 垂直排法。整条左栏从上往下：工具行 → 标签行 → 书签段 → 窗口按钮。
        ///
        /// **折叠态（默认）整条只留两颗**：顶上一个「＋」、底下一颗「×」，中间那段全给标签图标；
        /// 鼠标移进来窗格摊开（`EmbedForm.PaneShowWidth` 把宽度放大）才排其余工具、显示标题和书签段。
        /// 两条**不许破的**几何约束：
        /// ① 上下 —— 两种状态共用同一套边界（`tabsTopV` / `winTopV`）；
        /// ② 左右 —— 两种状态共用同一个图标列 x（`iconX`，按**折叠态宽度**算，不看当前 Width）。
        /// 摊开时 ＋ / × 原地不动，只是中间「多出来」东西、旁边多出几颗按钮。
        ///
        /// ⚠ 书签段那一块**不是我们画的** —— 上层把书签控件摆进来，这里只留空位
        /// （`BookmarkBand`）并把标签区裁到它上面为止；`favBandH` 是**实际**留出来的高度。
        /// </summary>
        private void EnsureLayoutV()
        {
            bounds.Clear();
            int pad = Px(4);
            int cell = VCell;
            int gap = VGap;

            // 图标列：**两种状态共用同一个 x** —— 折叠态窗格就只有 EmbedForm.PaneCollapsedL 宽，
            // 图标（＋ / × / 标签图标）都钉在这一条的中线上；展开后窗格宽多了，这一列**不跟着 Width 变**。
            // ⚠ 关键就在这：算 x 要用**折叠态宽度**而不是当前 Width。要是拿 Width 居中，
            //   鼠标一进出窗格 ＋ 和 × 就横着跳一下（用户：「展开前后加号和关闭图标不在同一位置，
            //   这肯定不行的，就算修改收缩时宽度，也要达到在同一位置的要求」）。
            int iconX = Math.Max(0, (Px(EmbedForm.PaneCollapsedL) - cell) / 2);
            int iconY = Px(4);
            int winY = Height - Px(VWinRowL) + Px(4);

            // ⚠ 每轮先把矩形**全部清空**再排：折叠态只有 ＋ 和 × 两颗，
            //   留着上一轮的矩形就会画在（也能点到）本该不存在的地方。
            newRect = Rectangle.Empty;
            settingsRect = Rectangle.Empty;
            pinRect = Rectangle.Empty;
            dividerX = 0;
            for (int i = 0; i < toolRects.Length; i++) toolRects[i] = Rectangle.Empty;
            for (int i = 0; i < wbtnRects.Length; i++) wbtnRects[i] = Rectangle.Empty;

            if (collapsed)
            {
                newRect = new Rectangle(iconX, iconY, cell, cell);
                wbtnRects[(int)WBtn.Close] = new Rectangle(iconX, winY, cell, cell);
            }
            else
            {
                // ---- 顶部工具行：从图标列起横着排一行（加号 + 历史 / 恢复 + 齿轮），图钉钉在右端 ----
                int y = iconY;
                int x = iconX;
                newRect = new Rectangle(x, y, cell, cell); x += cell + gap;
                toolRects[(int)Tool.History] = new Rectangle(x, y, cell, cell); x += cell + gap;
                toolRects[(int)Tool.Reopen] = new Rectangle(x, y, cell, cell); x += cell + gap;
                // 书签那枚星**不排**：书签段自己带标题行，点标题就摊开 / 收起（见 BookmarkBand）
                dividerX = x + Px(4);
                x += Px(10);
                settingsRect = new Rectangle(x, y, cell, cell);
                // 图钉钉在右端 —— 不跟着工具排，否则窗格一窄就跟齿轮叠上了。
                // 工具排完到右端之间本来就留着一截（展开态窗格固定 Px(210) 宽），放得下。
                pinRect = new Rectangle(Width - pad - cell, y, cell, cell);

                // ---- 底部窗口按钮：**左对齐**、也从同一个图标列起排 ----
                // 用户：「展开时这排窗口按钮要挪到左下」，顺序按「关闭 / 放大 / 缩小」。
                // 这样折叠态那颗 × 的落点，正好就是展开态这颗关闭按钮的落点，展开前后不位移。
                int wx = iconX;
                wbtnRects[(int)WBtn.Close] = new Rectangle(wx, winY, cell, cell); wx += cell + gap;
                wbtnRects[(int)WBtn.Maximize] = new Rectangle(wx, winY, cell, cell); wx += cell + gap;
                wbtnRects[(int)WBtn.Minimize] = new Rectangle(wx, winY, cell, cell);
            }

            // 这几样在竖排里没有意义。**必须清掉** —— 菜单锚点 / 命中判定都会读它们，
            // 留着上一轮（或另一个实例）的矩形，就会点到看不见的东西上。
            toolsLeft = 0;              // 滚轮那边靠它判「是不是在按钮区」：竖排一律当标签区
            tabsClipRight = Width;

            // ---- 上下边界：折叠态和展开态取**同一套值**（摊开时 ＋ / × 不动）----
            tabsTopV = Px(VToolRowL);
            winTopV = Height - Px(VWinRowL);
            // 书签段最多只准吃到「工具行以下」的一半、且必须给标签区留两行 ——
            // 不然窗口一矮，标签就全被书签挤没了（宁可书签段少显示几行，它自己能滚）。
            // 折叠态那条窄缝连标题都放不下，直接不给。
            int below = Math.Max(0, winTopV - tabsTopV);
            int cap = Math.Max(0, below - VRowH * 2);
            favBandH = collapsed ? 0 : Math.Min(Math.Max(0, bookmarkBand), cap);
            tabsBottomV = winTopV - favBandH;

            // ---- 标签行 ----
            int avail = Math.Max(0, tabsBottomV - tabsTopV);
            int total = tabs.Count * VRowH;
            maxScroll = Math.Max(0, total - avail);
            if (scrollX > maxScroll) scrollX = maxScroll;
            if (scrollX < 0) scrollX = 0;
            overflow = (maxScroll > 0);

            int tx = pad;
            int tw = Math.Max(Px(16), Width - pad * 2);
            int ty = tabsTopV - scrollX;
            for (int i = 0; i < tabs.Count; i++)
            {
                // 被书签段 / 窗口按钮挡住的那些**不排** —— 命中判定是按 bounds 走的，
                // 排出来就等于把那些地方变成了可点标签。
                if (ty >= tabsBottomV) break;
                bounds.Add(new Rectangle(tx, ty, tw, VRowH));
                ty += VRowH;
            }
        }

        /// <summary>垂直窗格里鼠标落在第几个标签上（-1 = 不在标签上）。</summary>
        private int VHitTest(Point p)
        {
            EnsureLayout();
            // 工具行 / 书签区 / 窗口按钮那三块都不算标签 ——
            // 少了这道判，点加号就会点到一个标签上（它们的矩形是同一套坐标系）
            if (p.Y < tabsTopV || p.Y >= tabsBottomV) return -1;
            for (int i = 0; i < bounds.Count; i++)
                if (bounds[i].Contains(p)) return i;
            return -1;
        }

        /// <summary>垂直窗格里某一行的关闭按钮位置。</summary>
        private Rectangle VCloseBounds(Rectangle row)
        {
            int s = CloseBoxSize;
            return new Rectangle(row.Right - CloseAreaWidth + (CloseAreaWidth - s) / 2,
                                 row.Top + (row.Height - s) / 2, s, s);
        }

        /// <summary>标签区能画到哪儿（右边那排按钮的左边）。画标签时要按它裁，不能压到按钮上。</summary>
        private int TabsClipRight
        {
            get { EnsureLayout(); return Math.Max(0, tabsClipRight); }
        }

        /// <summary>
        /// 这个 x 是不是落在**右边那排按钮**上（竖向分割线、齿轮、窗口按钮都算）。
        /// 给滚轮分派用：标签条上滚轮 = 切标签，**按钮区**上滚轮 = 横向滑标签。
        /// ⚠ 可能在钩子线程上被调 —— 只读一个已经算好的 int，不触发重排（见 WheelHook 的三条约束）。
        /// </summary>
        public bool InButtonArea(int x)
        {
            return toolsLeft > 0 && x >= toolsLeft;
        }

        /// <summary>标签有没有多到需要横向滚动（没开自动缩窄时会出现）。</summary>
        public bool HasOverflow { get { return maxScroll > 0; } }

        /// <summary>
        /// 同上，但**不重算布局** —— 只读上一次排版的结果。
        /// 给滚轮钩子的判定用（钩子线程上只准读、不准触发重排，见 MouseWheelHook 类注释）。
        /// </summary>
        public bool OverflowCached { get { return overflow; } }

        private volatile bool overflow;

        /// <summary>横向滚动标签区（滚轮在**非标签条**区域时用它）。返回是否真的动了。</summary>
        public bool ScrollTabsBy(int dx)
        {
            EnsureLayout();
            if (maxScroll <= 0) return false;
            int old = scrollX;
            scrollX += dx;
            if (scrollX > maxScroll) scrollX = maxScroll;
            if (scrollX < 0) scrollX = 0;
            if (scrollX == old) return false;
            Redraw();
            return true;
        }

        private int scrollX;
        private int maxScroll;

        // ---- 横向滚动条（用户：平时隐藏、鼠标进标签条才显示、能点能拖）----
        /// <summary>鼠标在不在标签条里 —— 决定这条滚动条显不显示（平时是隐的）。</summary>
        private bool pointerIn;
        /// <summary>鼠标压在滚动条轨道上（亮一点）。</summary>
        private bool barHot;
        /// <summary>正在拖滑块。</summary>
        private bool barDrag;
        /// <summary>按下滑块时，光标离滑块左边缘多远（拖的时候保持这个偏移，手感才不跳）。</summary>
        private int barGrabDX;

        private Rectangle NewButtonBounds()
        {
            EnsureLayout();
            return newRect;
        }

        /// <summary>齿轮的位置（EmbedForm 要用它当锚点）。</summary>
        public Rectangle SettingsButtonBounds()
        {
            EnsureLayout();
            return settingsRect;
        }

        /// <summary>某个工具按钮的位置（历史 / 恢复 / 书签栏 / 齿轮都从这儿取锚点）。</summary>
        public Rectangle ToolButtonBounds(Tool t)
        {
            EnsureLayout();
            if (t == Tool.Settings) return settingsRect;
            return toolRects[(int)t];
        }

        /// <summary>某个标签的位置（标签右键菜单拿它当锚点）。越界给个空矩形，别抛。</summary>
        public Rectangle TabBounds(int index)
        {
            EnsureLayout();
            if (index < 0 || index >= bounds.Count) return Rectangle.Empty;
            return bounds[index];
        }

        /// <summary>鼠标在哪个工具按钮上（-1 = 不在）。</summary>
        private int ToolAt(Point p)
        {
            EnsureLayout();
            for (int i = 0; i < toolRects.Length; i++)
                if (i != (int)Tool.Settings && toolRects[i].Contains(p)) return i;
            if (settingsRect.Contains(p)) return (int)Tool.Settings;
            return -1;
        }

        /// <summary>鼠标在哪个窗口按钮上（-1 = 不在）。</summary>
        private int WBtnAt(Point p)
        {
            EnsureLayout();
            for (int i = 0; i < wbtnRects.Length; i++)
                if (wbtnRects[i].Contains(p)) return i;
            return -1;
        }

        /// <summary>
        /// 自绘一个齿轮（设置按钮用）。
        /// MDL2 里那颗齿轮（\uE713）靠字体渲染，可标签条用的是普通字体、字形不一定出得来，
        /// 所以直接画：16 个顶点在高/低半径之间交替 = 8 个齿，中心再挖个洞就是齿轮环。
        /// 洞用按钮自身的底色填 —— 这样它在黑底和 hover 底上都成立。
        /// </summary>
        private static void DrawGear(Graphics g, Rectangle r, Color fg, Color hole, float stroke)
        {
            int cx = r.Left + r.Width / 2;
            int cy = r.Top + r.Height / 2;
            int R = Math.Max(5, Px(8) - (int)stroke);
            int ri = (int)(R * 0.72);
            int rr = (int)(R * 0.42);
            const int teeth = 8;
            PointF[] pts = new PointF[teeth * 2];
            for (int i = 0; i < pts.Length; i++)
            {
                double a = Math.PI * i / teeth - Math.PI / 2;
                double rad = (i % 2 == 0) ? R : ri;
                pts[i] = new PointF((float)(cx + rad * Math.Cos(a)), (float)(cy + rad * Math.Sin(a)));
            }
            using (SolidBrush b = new SolidBrush(fg)) g.FillPolygon(b, pts);
            if (rr > 0)
            {
                using (SolidBrush b = new SolidBrush(hole))
                    g.FillEllipse(b, cx - rr, cy - rr, rr * 2, rr * 2);
            }
        }

        private Rectangle CloseBounds(Rectangle tab)
        {
            int s = CloseBoxSize;
            return new Rectangle(tab.Right - CloseAreaWidth + (CloseAreaWidth - s) / 2,
                                 tab.Top + (tab.Height - s) / 2, s, s);
        }

        private int HitTest(Point p)
        {
            EnsureLayout();     // 位置随时可能变（标签增删 / 窗口改宽），先重算一遍
            // ⚠ 「标签和右边那排按钮是同一层」的另一半（用户）：
            //   标签溢出了的话，最后几个标签的矩形是**伸到按钮底下**的（只被裁掉了不画）。
            //   命中判定不过这一刀，在右边空白处右键就会弹「标签右键」（那个 x 坐落在被裁掉的
            //   那半个标签里）—— 用户报的「右边空白菜单没反应」就是这个。先按裁剪线切一刀。
            if (p.X >= Math.Max(0, tabsClipRight)) return -1;
            for (int i = 0; i < bounds.Count; i++)
            {
                if (bounds[i].Contains(p)) return i;
            }
            return -1;
        }

        private bool OnCloseButton(int index, Point p)
        {
            if (index < 0 || index >= bounds.Count) return false;
            return CloseBounds(bounds[index]).Contains(p);
        }

        /// <summary>空白处（不是标签、不是任何按钮）—— 这块地方现在兼任标题栏，能拖窗口。</summary>
        private bool OnBlank(Point p)
        {
            if (HitTest(p) >= 0) return false;
            if (NewButtonBounds().Contains(p)) return false;
            if (ToolAt(p) >= 0) return false;
            if (WBtnAt(p) >= 0) return false;
            return true;
        }

        /// <summary>标签第二行要显示什么：真目录就是完整路径，「此电脑」这类给个友好名。</summary>
        public static string PathLine(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return "";
            if (stored.StartsWith("::", StringComparison.Ordinal))
            {
                if (string.Equals(stored, ExplorerView.ThisPcPath, StringComparison.OrdinalIgnoreCase)) return "此电脑";
                return "系统文件夹";
            }
            return stored;
        }

        /// <summary>
        /// 悬停提示文字：**名字 + 完整路径**；两者一样（比如「此电脑」）就只留一个，别重复。
        /// </summary>
        private static string TipTextFor(TabItem t)
        {
            string title = t.Title ?? "";
            string p = t.Path ?? "";
            if (string.IsNullOrEmpty(p) || string.Equals(p, title, StringComparison.OrdinalIgnoreCase))
                return title;
            return title + "\r\n" + p;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Vertical) { OnPaintV(e); return; }
            EnsureLayout();
            Graphics g = e.Graphics;
            Rectangle r = ClientRectangle;
            Color bar = BarBack;

            g.FillRectangle(new SolidBrush(bar), r);

            float stroke = Math.Max(1f, DpiScale);

            // 标签一律不许压到右边那排按钮上（没开自动缩窄时总宽可能超出可视区）—— 裁一刀。
            Region oldClip = g.Clip;
            int clipR = Math.Max(1, TabsClipRight);
            g.SetClip(new Rectangle(0, 0, clipR, Height));

            // ⚠ 上界取 bounds.Count 而不是 tabs.Count：**排不下的标签不进 bounds**
            //   （竖排被书签区挤掉的那些就是这样），还按 tabs.Count 循环就会越界。
            for (int i = 0; i < bounds.Count && i < tabs.Count; i++)
            {
                Rectangle tab = bounds[i];
                if (tab.Right < 0 || tab.Left > Width) continue;

                // 多选中的标签跟**激活标签**用同一个样式（用户：多选条和激活条要一个样式，不然太丑）。
                // 两者仍分得出来：贴边那条指示条只画在激活的那一个上（见下面 accent 那一段）。
                bool lit = tabs[i].Active || sel.Contains(i);
                Color fill = lit ? (inactive ? Theme.TabActiveOff : Theme.TabActive)
                                 : (i == hoverIndex ? Theme.Hover : bar);
                g.FillRectangle(new SolidBrush(fill), tab);

                // 选中标签那条蓝线**不在这儿画** —— 见循环后面那一段（挪到底部，避开顶部滚动条）。
                if (!tabs[i].Active && i > 0)
                {
                    g.DrawLine(new Pen(Theme.Border),
                               tab.Left, tab.Top + Px(6), tab.Left, tab.Bottom - Px(6));
                }

                bool showClose = tabs[i].Active || i == hoverIndex;
                Rectangle close = CloseBounds(tab);
                bool roomForClose = tab.Width > Px(80);
                int textRight = tab.Right - (showClose && roomForClose ? CloseAreaWidth : Px(8));

                // 标签左边那颗图标：优先用 explorer 窗口给的那颗（当前文件夹的实时图标），
                // 还没拿到就先用通用文件夹顶着。
                int isz = IconSize;
                Image ic = tabs[i].Icon;
                if (ic == null)
                {
                    EnsureFolderIcon();
                    ic = folderIcon;
                }
                if (ic != null)
                {
                    g.DrawImage(ic,
                        new Rectangle(tab.Left + Px(TextPadLeft), tab.Top + (tab.Height - isz) / 2, isz, isz));
                }

                // ---- 一行文字：就文件夹名（完整路径进悬停提示，见 TipTextFor）----
                int textLeft = tab.Left + Px(TextPadLeft) + isz + Px(IconGap);
                int textW = textRight - textLeft;
                if (textW < 0) textW = 0;

                Color c1 = lit ? (inactive ? Theme.TextInactive : Theme.Text)
                               : (inactive ? Theme.TextInactive : Theme.TextDim);

                // 置顶标记：紧挨着文件夹图标右边那枚小图钉（用户：小标志，别太占地方）。
                // 画在图标和标题之间而不是右边 —— 右边那格是关闭按钮的地盘，两个图标叠在一起会很挤。
                if (tabs[i].Pinned)
                {
                    int ps = Px(12);
                    int pxx = tab.Left + Px(TextPadLeft) + isz + Px(IconGap);
                    g.FillRectangle(new SolidBrush(bar), new Rectangle(pxx, tab.Top, PinAreaWidth, tab.Height));
                    TextRenderer.DrawText(g, "\uE718", pinFont,
                        new Rectangle(pxx, tab.Top + (tab.Height - ps) / 2, ps, ps),
                        lit ? (inactive ? Theme.AccentDim : Theme.Accent) : c1,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping);
                    textLeft = pxx + PinAreaWidth;
                    textW = textRight - textLeft;
                    if (textW < 0) textW = 0;
                }

                TextRenderer.DrawText(g, tabs[i].Title, titleFont,
                    new Rectangle(textLeft, tab.Top, textW, tab.Height), c1,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding |
                    TextFormatFlags.PreserveGraphicsClipping);

                if (showClose && roomForClose)
                {
                    bool hc = i == hoverCloseIndex;
                    if (hc) g.FillRectangle(new SolidBrush(BG(Color.FromArgb(232, 17, 35))), close);
                    Color penColor = hc ? Color.White : (inactive ? Theme.TextInactive : Theme.TextDim);
                    Pen pen = new Pen(penColor, stroke * 1.4f);
                    int inset = Math.Max(3, Px(4));
                    int s2 = close.Width - inset * 2;
                    int cx = close.Left + inset, cy = close.Top + inset;
                    g.DrawLine(pen, cx, cy, cx + s2, cy + s2);
                    g.DrawLine(pen, cx + s2, cy, cx, cy + s2);
                    pen.Dispose();
                }

                if (dragOverIndex == i && dragFromIndex != i)
                {
                    g.DrawLine(new Pen(Theme.Accent, stroke * 2),
                        i < dragFromIndex ? tab.Left : tab.Right, tab.Top,
                        i < dragFromIndex ? tab.Left : tab.Right, tab.Bottom);
                }
            }

            // 底部与容器分隔
            g.Clip = oldClip;      // 右边那排按钮 / 加号不受上面那一刀的影响

            g.DrawLine(new Pen(Theme.Border), 0, Height - 1, Width, Height - 1);

            // ---- 选中标签的蓝色指示条：**贴在标签条底部**（用户指定）----
            // 原来画在顶上，会跟顶部的滚动条轨道抢同一排像素。挪到底部后各占一边，互不打架。
            // ⚠ 必须**画在上面那条分隔线之后**：分隔线压在 Height-1，先画蓝线会被它盖掉一像素。
            int accentH = Math.Max(2, Px(2));
            Region clipForAccent = g.Clip;
            g.SetClip(new Rectangle(0, 0, clipR, Height));
            for (int i = 0; i < bounds.Count && i < tabs.Count; i++)
            {
                if (!tabs[i].Active) continue;
                Rectangle ab = bounds[i];
                if (ab.Right < 0 || ab.Left > Width) continue;
                g.FillRectangle(new SolidBrush(inactive ? Theme.AccentDim : Theme.Accent),
                                new Rectangle(ab.Left, ab.Bottom - accentH, ab.Width, accentH));
            }
            g.Clip = clipForAccent;

            // ---- 标签溢出时的位置指示条（用户要的「隐藏进度条」）----
            // 就画在标签条**最顶上、拉满整条宽度**：底 = 标签区宽度，滑块 = 当前能看到的那一段。
            // 它在裁剪区之外（先 Clip 恢复再画），不然滑块永远只能看到左边一截。
            DrawScrollBar(g);

            // “+” 新建：紧跟在最后一个标签右边（位置由 EnsureLayout 算）
            Rectangle nb = newRect;
            if (hoverNew) g.FillRectangle(new SolidBrush(BG(Theme.Hover)), nb);
            int mx = nb.Left + nb.Width / 2, my = nb.Top + nb.Height / 2;
            int arm = Math.Max(4, Px(5));
            Pen p2 = new Pen(hoverNew ? Theme.Text : (inactive ? Theme.TextInactive : Theme.TextDim), stroke * 1.6f);
            g.DrawLine(p2, mx - arm, my, mx + arm, my);
            g.DrawLine(p2, mx, my - arm, mx, my + arm);
            p2.Dispose();

            // ---- 右侧固定区：三个功能按钮 | 齿轮 ----
            for (int i = 0; i < toolRects.Length; i++)
            {
                if (i == (int)Tool.Settings) continue;
                Rectangle b = toolRects[i];
                if (b.Right <= 0) continue;
                if (i == hoverTool) g.FillRectangle(new SolidBrush(BG(Theme.Hover)), b);
                Color fg = (i == hoverTool) ? Theme.Text : (inactive ? Theme.TextInactive : Theme.TextDim);
                if (i == (int)Tool.Fav && favBarOn) fg = inactive ? Theme.AccentDim : Theme.Accent;
                Font gf = (i == (int)Tool.Fav) ? favGlyphFont : glyphFont;
                TextRenderer.DrawText(g, GlyphOf((Tool)i), gf, b, fg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }

            // 齿轮左边那条竖线（跟 Edge 一样把齿轮单独隔开）
            g.DrawLine(new Pen(Theme.Border), dividerX, Px(9), dividerX, Height - Px(9));

            Rectangle sbr = settingsRect;
            Color sBack = hoverTool == (int)Tool.Settings ? Theme.Hover : bar;
            if (hoverTool == (int)Tool.Settings) g.FillRectangle(new SolidBrush(sBack), sbr);
            DrawGear(g, sbr, hoverTool == (int)Tool.Settings ? Theme.Text
                             : (inactive ? Theme.TextInactive : Theme.TextDim), sBack, stroke);

            // ---- 最右边：窗口按钮（最小化 / 最大化还原 / 关闭）----
            // 这三个是窗口装饰，原生由 DWM/主题画、shell 命令库里取不到图标，只能用字形画；
            // 码位取自 Segoe MDL2 Assets，形状与 Win10 标题栏一致。
            string[] glyph = new string[wbtnRects.Length];
            glyph[(int)WBtn.Minimize] = "\uE921";
            glyph[(int)WBtn.Maximize] = maximized ? "\uE923" : "\uE922";
            glyph[(int)WBtn.Close] = "\uE8BB";
            for (int i = 0; i < wbtnRects.Length; i++)
            {
                Rectangle b = wbtnRects[i];
                if (b.Right <= 0) continue;
                if (i == hoverWBtn)
                    g.FillRectangle(new SolidBrush(BG(i == (int)WBtn.Close ? Color.FromArgb(232, 17, 35) : Theme.Hover)), b);
                Color fg = (i == hoverWBtn) ? Color.White
                         : (inactive ? Theme.TextInactive : Theme.Text);
                TextRenderer.DrawText(g, glyph[i], wbtnFont, b, fg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        /// <summary>侧边栏半透明那层（`EmbedForm` 摆进来；横排模式下永远是 null）。</summary>
        public PaneGlass Glass;

        /// <summary>底色 / 高亮色的填充色：半透明态下带 alpha。图标、文字别用它（见 `GlassPaint`）。</summary>
        private Color BG(Color c) { return GlassPaint.Wash(Glass, c); }

        /// <summary>
        /// 垂直窗格的画法：从上往下 工具行（折叠态只有「＋」）→ 标签行 → 书签段 → 窗口按钮行
        /// 单独开一个方法而不是在 OnPaint 里塞分支 —— 两种排法的绘制几乎不共用，
        /// 混在一起只会让「改横向的顺手弄坏竖向的」。
        ///
        /// 摊开盖在内容上时（`Glass`）**只让背景透明**：先铺「底下长什么样」当底，
        /// 底色 / 高亮色再带 alpha 盖上去；图标、文字、线条照旧不透明（见 `GlassPaint`）。
        /// </summary>
        private void OnPaintV(PaintEventArgs e)
        {
            EnsureLayout();
            Graphics g = e.Graphics;
            Color bar = BarBack;
            GlassPaint.Backdrop(g, this, Glass, bar);
            float stroke = Math.Max(1f, DpiScale);
            int isz = IconSize;

            DrawVTools(g, bar, stroke);

            // 标签行裁到标签区里：上面别盖工具行、下面别盖书签区和窗口按钮
            Region oldClip = g.Clip;
            g.SetClip(new Rectangle(0, tabsTopV, Width, Math.Max(0, tabsBottomV - tabsTopV)));

            // ⚠ 上界取 bounds.Count：地方不够时 EnsureLayoutV 会提前 break，
            //   bounds 比 tabs 短 —— 还按 tabs.Count 循环就会越界。
            for (int i = 0; i < bounds.Count && i < tabs.Count; i++)
            {
                Rectangle row = bounds[i];
                if (row.Bottom <= tabsTopV || row.Top >= tabsBottomV) continue;

                // 多选中的标签跟**激活标签**用同一个样式（用户：多选条和激活条要一个样式，不然太丑）。
                // 两者仍分得出来：贴边那条指示条只画在激活的那一个上（见下面 accent 那一段）。
                bool lit = tabs[i].Active || sel.Contains(i);
                Color fill = lit ? (inactive ? Theme.TabActiveOff : Theme.TabActive)
                                 : (i == hoverIndex ? Theme.Hover : bar);
                // 半透明态下「底色那一档」不再补一刀：底下已经按不透明度铺过底色了，
                // 再叠一次那几行就更实（标签区比工具行更不透，一眼就看出来）；只补高亮那一档。
                if (Glass == null || !Glass.On || fill != bar)
                    g.FillRectangle(new SolidBrush(BG(fill)), row);

                Image ic = tabs[i].Icon;
                if (ic == null) { EnsureFolderIcon(); ic = folderIcon; }
                // 图标横坐标**两种状态取同一个**：都挂在折叠态窗格那条中线上 ——
                // 跟顶上的 ＋、底下的 × 同一条竖线。以前展开态是「靠左 Px(6)」，
                // 鼠标一进出窗格，整列标签图标要横跳 4~5 像素（用户：「展开与否都不会错位」）。
                int ix = Px(EmbedForm.PaneCollapsedL) / 2 - isz / 2;
                if (ic != null)
                    g.DrawImage(ic, new Rectangle(ix, row.Top + (row.Height - isz) / 2, isz, isz));

                if (collapsed) continue;    // 折叠态到此为止：一行只有一个图标

                bool showClose = tabs[i].Active || i == hoverIndex;
                bool roomForClose = row.Width > Px(80);
                Rectangle close = VCloseBounds(row);
                int textRight = row.Right - (showClose && roomForClose ? CloseAreaWidth : Px(8));

                Color c1 = lit ? (inactive ? Theme.TextInactive : Theme.Text)
                               : (inactive ? Theme.TextInactive : Theme.TextDim);

                int textLeft = ix + isz + Px(IconGap);
                int textW = textRight - textLeft;
                if (textW < 0) textW = 0;
                TextRenderer.DrawText(g, tabs[i].Title, titleFont,
                    new Rectangle(textLeft, row.Top, textW, row.Height), c1,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding |
                    TextFormatFlags.PreserveGraphicsClipping);

                // 置顶标记：缩在最左边那条窄带上（图标左侧），不跟标题抢地方
                if (tabs[i].Pinned)
                {
                    TextRenderer.DrawText(g, "\uE718", pinFont,
                        new Rectangle(row.Left, row.Top, Px(12), row.Height),
                        lit ? (inactive ? Theme.AccentDim : Theme.Accent) : c1,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping);
                }

                if (showClose && roomForClose)
                {
                    bool hc = i == hoverCloseIndex;
                    if (hc) g.FillRectangle(new SolidBrush(BG(Color.FromArgb(232, 17, 35))), close);
                    Color penColor = hc ? Color.White : (inactive ? Theme.TextInactive : Theme.TextDim);
                    Pen pen = new Pen(penColor, stroke * 1.4f);
                    int inset = Math.Max(3, Px(4));
                    int s2 = close.Width - inset * 2;
                    int cx = close.Left + inset, cy = close.Top + inset;
                    g.DrawLine(pen, cx, cy, cx + s2, cy + s2);
                    g.DrawLine(pen, cx + s2, cy, cx, cy + s2);
                    pen.Dispose();
                }
            }

            // 选中标签的指示条：竖向靠**左**（横向那条是贴底，两边各占一边，不抢地方）
            for (int i = 0; i < bounds.Count && i < tabs.Count; i++)
            {
                if (!tabs[i].Active) continue;
                Rectangle ab = bounds[i];
                if (ab.Bottom <= tabsTopV || ab.Top >= tabsBottomV) continue;
                g.FillRectangle(new SolidBrush(inactive ? Theme.AccentDim : Theme.Accent),
                    new Rectangle(0, ab.Top, Math.Max(2, Px(2)), ab.Height));
            }

            // 拖动排序的落点：竖排画在那一行的**上沿**（横排是在左右缝上划线）
            if (dragFromIndex >= 0 && dragOverIndex >= 0 && dragOverIndex < bounds.Count && dragFromIndex != dragOverIndex)
            {
                Rectangle dr = bounds[dragOverIndex];
                Pen dp = new Pen(Theme.Accent, Math.Max(2f, DpiScale * 2));
                g.DrawLine(dp, dr.Left, dragOverIndex < dragFromIndex ? dr.Top : dr.Bottom,
                               dr.Right, dragOverIndex < dragFromIndex ? dr.Top : dr.Bottom);
                dp.Dispose();
            }
            g.Clip = oldClip;

            DrawVWindowRow(g);

            // 书签区上沿一条淡淡的线：它是「另一件事」，跟标签列表分开
            if (favBandH > 0) g.DrawLine(new Pen(Theme.Border), 0, tabsBottomV, Width, tabsBottomV);
            // 右边一条竖线：跟内容区分开
            g.DrawLine(new Pen(Theme.Border), Width - 1, 0, Width - 1, Height);
        }

        /// <summary>
        /// 垂直窗格顶上那一块：加号、历史 / 恢复关闭、齿轮、图钉。
        /// 折叠态只排到「＋」一颗（其余矩形是 Empty，见 EnsureLayoutV），展开态才是横排一行 ——
        /// 图标和悬停色跟横向那条**同一套**（用户：「横向是图标的东西，垂直也用图标吧」）。
        /// </summary>
        private void DrawVTools(Graphics g, Color bar, float stroke)
        {
            // “+” 新建：位置由 EnsureLayoutV 算
            Rectangle nb = newRect;
            if (nb.Width <= 0) return;      // 折叠态只有它一颗，正常不会为空；空了一律不画
            if (hoverNew) g.FillRectangle(new SolidBrush(BG(Theme.Hover)), nb);
            int mx = nb.Left + nb.Width / 2, my = nb.Top + nb.Height / 2;
            int arm = Math.Max(4, Px(5));
            Pen p2 = new Pen(hoverNew ? Theme.Text : (inactive ? Theme.TextInactive : Theme.TextDim), stroke * 1.6f);
            g.DrawLine(p2, mx - arm, my, mx + arm, my);
            g.DrawLine(p2, mx, my - arm, mx, my + arm);
            p2.Dispose();

            for (int i = 0; i < toolRects.Length; i++)
            {
                if (i == (int)Tool.Settings) continue;
                Rectangle b = toolRects[i];
                if (b.Width <= 0) continue;
                if (i == hoverTool) g.FillRectangle(new SolidBrush(BG(Theme.Hover)), b);
                Color fg = (i == hoverTool) ? Theme.Text : (inactive ? Theme.TextInactive : Theme.TextDim);
                if (i == (int)Tool.Fav && favBarOn) fg = inactive ? Theme.AccentDim : Theme.Accent;
                Font gf = (i == (int)Tool.Fav) ? favGlyphFont : glyphFont;
                TextRenderer.DrawText(g, GlyphOf((Tool)i), gf, b, fg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }

            // 齿轮左边那条竖线：只有展开态横排时才有地方放（折叠态整条只有 ＋ 和 ×，没有齿轮）
            if (dividerX > 0)
                g.DrawLine(new Pen(Theme.Border), dividerX, settingsRect.Top - Px(2), dividerX, settingsRect.Bottom + Px(2));

            Rectangle sbr = settingsRect;
            if (sbr.Width > 0)
            {
                bool hset = (hoverTool == (int)Tool.Settings);
                Color sBack = hset ? Theme.Hover : bar;
                if (hset) g.FillRectangle(new SolidBrush(BG(sBack)), sbr);
                DrawGear(g, sbr, hset ? Theme.Text : (inactive ? Theme.TextInactive : Theme.TextDim), sBack, stroke);
            }

            // 图钉 = 「折叠窗格」开关（Edge 那个）。
            // ⚠ 它必须在**展开态**下能点到（折叠态整条只有 ＋ 和 ×，见 EnsureLayoutV）。
            Rectangle pr = pinRect;
            if (pr.Width > 0)
            {
                if (hoverPin) g.FillRectangle(new SolidBrush(BG(Theme.Hover)), pr);
                Color pfg = pinOn ? (inactive ? Theme.AccentDim : Theme.Accent)
                                  : (hoverPin ? Theme.Text : (inactive ? Theme.TextInactive : Theme.TextDim));
                TextRenderer.DrawText(g, "\uE718", glyphFont, pr, pfg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        /// <summary>垂直窗格底部的窗口按钮 —— 横向那条最右边那三颗，竖过来。</summary>
        private void DrawVWindowRow(Graphics g)
        {
            string[] glyph = new string[wbtnRects.Length];
            glyph[(int)WBtn.Minimize] = "\uE921";
            glyph[(int)WBtn.Maximize] = maximized ? "\uE923" : "\uE922";
            glyph[(int)WBtn.Close] = "\uE8BB";

            // 这里原来还有一条横线（`winTopV - 1`）把窗口按钮跟标签区分开 —— 去掉。
            // 用户：「当垂直时，关闭上方不用分隔条了，不然看着别扭」：窗格本来就窄，
            // 这条线横在「×」（折叠态就它一颗）上方，看着像把底下截掉一块。
            for (int i = 0; i < wbtnRects.Length; i++)
            {
                Rectangle b = wbtnRects[i];
                if (b.Width <= 0) continue;
                if (i == hoverWBtn)
                    g.FillRectangle(new SolidBrush(BG(i == (int)WBtn.Close ? Color.FromArgb(232, 17, 35) : Theme.Hover)), b);
                Color fg = (i == hoverWBtn) ? Color.White
                         : (inactive ? Theme.TextInactive : Theme.Text);
                TextRenderer.DrawText(g, glyph[i], wbtnFont, b, fg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        /// <summary>
        /// 算滚动条的**轨道 / 滑块**矩形（没溢出就返回 false）。
        /// ⚠ 画和命中判定都走这一份 —— 两处各算一次的话，看到的滑块和点得着的滑块迟早会差几个像素。
        /// 轨道 = **整条标签条的宽度、贴在最顶上**（用户指定），
        /// 滑块宽 = 「看得见的那一段 / 全部」的比例，滑块位置 = 滚到哪儿了。
        /// </summary>
        private bool LayoutScrollBar(out Rectangle track, out Rectangle thumb)
        {
            track = Rectangle.Empty; thumb = Rectangle.Empty;
            if (maxScroll <= 0 || tabs.Count < 2) return false;

            // 用户：挪到**顶部**、并且**拉满整条标签条的宽度**
            // （原来是压在标签底下、只占标签区）。平时不显示（`DrawScrollBar` 里判 `pointerIn`）；
            // 选中标签那条蓝线已经挪到底部了（见 `OnPaint`），两边各占一头、不再抢像素。
            int x0 = 0;
            int w = Width;
            if (w < Px(24)) return false;

            int h = Math.Max(3, Px(4));
            int y = 0;
            track = new Rectangle(x0, y, w, h);

            int content = w + maxScroll;
            if (content <= 0) return false;

            int thumbW = (int)((long)w * w / content);
            int minThumb = Math.Min(Px(28), w);
            if (thumbW < minThumb) thumbW = minThumb;
            int span = Math.Max(1, maxScroll);
            int thumbX = x0 + (int)((long)(w - thumbW) * Math.Min(scrollX, span) / span);
            thumb = new Rectangle(thumbX, y, thumbW, h);
            return true;
        }

        /// <summary>
        /// 画标签溢出时那条横向滚动条（用户：**平时隐藏**，鼠标进标签条才显示；
        /// 位置在标签条**最顶上、拉满整条宽度** —— 用户后来指定的，放底下不好找）。
        /// 没溢出 / 鼠标不在条里就不画 —— 没超出屏幕时画一条只会是干扰。
        /// </summary>
        private void DrawScrollBar(Graphics g)
        {
            Rectangle track, thumb;
            if (!LayoutScrollBar(out track, out thumb)) return;
            if (!pointerIn && !barDrag) return;      // 平时是隐的

            bool hot = barHot || barDrag;
            int alpha = hot ? (inactive ? 90 : 130) : (inactive ? 52 : 80);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(alpha, Theme.Text)))
                g.FillRectangle(b, track);
            using (SolidBrush b = new SolidBrush(hot ? (inactive ? Theme.AccentDim : Theme.Accent)
                                                     : Color.FromArgb(inactive ? 150 : 210,
                                                         inactive ? Theme.AccentDim : Theme.Accent)))
                g.FillRectangle(b, thumb);
        }

        /// <summary>
        /// 把滑块挪到「左边缘 = wantLeft」对应的滚动位置。
        /// ⚠ 滑块宽 ≠ 可视宽（有缩放比），所以要按 **轨道可走距离 ↔ 内容可滚距离** 换算回来。
        /// </summary>
        private void ScrollThumbTo(int wantLeft, Rectangle track, Rectangle thumb)
        {
            int travel = track.Width - thumb.Width;
            if (travel <= 0) return;
            int left = wantLeft - track.Left;
            if (left < 0) left = 0;
            if (left > travel) left = travel;

            int span = Math.Max(1, maxScroll);
            int nx = (int)((long)left * span / travel);
            if (nx < 0) nx = 0;
            if (nx > maxScroll) nx = maxScroll;
            if (nx == scrollX) return;
            scrollX = nx;
            Redraw();
        }

        /// <summary>
        /// 三个功能按钮的图标。用的是 `Segoe MDL2 Assets` 的码位 ——
        /// 这几个都拿 PIL 渲染对照图**看过实物**才写的（E81C 带逆时针箭头的钟 = 历史、
        /// E7A7 回弯箭头 = 恢复、E734/E735 空心/实心星 = 书签栏开没开），别凭记忆改。
        /// </summary>
        private string GlyphOf(Tool t)
        {
            switch (t)
            {
                case Tool.History: return "\uE81C";
                case Tool.Reopen: return "\uE7A7";
                case Tool.Fav: return favBarOn ? "\uE735" : "\uE734";
            }
            return "";
        }

        private string TipOf(Tool t)
        {
            switch (t)
            {
                case Tool.History: return "历史记录(Ctrl+H)";
                case Tool.Reopen: return "恢复关闭的标签页(Ctrl+Shift+T)";
                case Tool.Fav: return (favBarOn ? "隐藏" : "显示") + "书签栏(Ctrl+Shift+B)";
                case Tool.Settings: return "设置";
            }
            return "";
        }

        private string TipOf(WBtn b)
        {
            switch (b)
            {
                case WBtn.Minimize: return "最小化";
                case WBtn.Maximize: return maximized ? "向下还原" : "最大化";
                case WBtn.Close: return "关闭窗口（收进托盘，不退出程序）";
            }
            return "";
        }

        /// <summary>弹提示。同一个目标只弹一次（换了才重弹，否则鼠标一动就闪）。</summary>
        private void ShowTip(string key, string text, Rectangle anchor)
        {
            if (string.Equals(key, tipKey, StringComparison.Ordinal)) return;
            tipKey = key;
            if (key == null) { tips.Hide(this); return; }
            int x, y;
            if (Vertical)
            {
                // 竖排：提示一律贴到窗格**右边**（横向那条是贴下边）——
                // 窗格本身很窄，提示放下面会压住下一个标签。
                x = anchor.Right + Px(4);
                y = anchor.Top;
            }
            else
            {
                if (anchor.Right > Width) anchor.X = Math.Max(0, Width - anchor.Width - Px(40));
                x = anchor.Left;
                y = anchor.Bottom + Px(2);
            }
            // 先把「马上要显示的原文」交给 Theme（它自己量尺寸得知道原文，见 Theme.TipText）
            Theme.TipText(tips, text);
            tips.Show(text, this, x, y, 8000);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            pointerIn = true;
            if (Vertical) { VMouseMove(e); return; }

            // ---- 拖滚动条最优先（滑块就压在标签底下那一条上）----
            if (barDrag)
            {
                Rectangle tr, th;
                if (LayoutScrollBar(out tr, out th)) ScrollThumbTo(e.Location.X - barGrabDX, tr, th);
                return;
            }

            int idx = HitTest(e.Location);
            bool changed = false;

            if (idx != hoverIndex) { hoverIndex = idx; changed = true; }
            int hc = OnCloseButton(idx, e.Location) ? idx : -1;
            if (hc != hoverCloseIndex) { hoverCloseIndex = hc; changed = true; }
            bool hn = NewButtonBounds().Contains(e.Location);
            if (hn != hoverNew) { hoverNew = hn; changed = true; }
            int ht = ToolAt(e.Location);
            if (ht != hoverTool) { hoverTool = ht; changed = true; }
            int hw = WBtnAt(e.Location);
            if (hw != hoverWBtn) { hoverWBtn = hw; changed = true; }

            bool bh = false;
            if (maxScroll > 0)
            {
                Rectangle bt, bth;
                if (LayoutScrollBar(out bt, out bth)) bh = bt.Contains(e.Location);
            }
            if (bh != barHot) { barHot = bh; changed = true; }

            // 空白处按住并真的拖了（超过几个像素才算拖，否则双击空白那一下会被当成拖窗口）
            if (blankDrag && (e.Button & MouseButtons.Left) != 0)
            {
                int dx = e.Location.X - blankFrom.X, dy = e.Location.Y - blankFrom.Y;
                if (dx * dx + dy * dy >= Px(4) * Px(4))
                {
                    blankDrag = false;
                    DragWindow();
                }
            }

            // ---- 悬停提示：一个「目标 key」驱动，标签 / 关闭 / 各按钮都走这一条 ----
            string key = null, text = null;
            Rectangle anchor = Rectangle.Empty;
            if (hw >= 0)
            {
                key = "wbtn:" + hw;
                text = TipOf((WBtn)hw);
                anchor = wbtnRects[hw];
            }
            else if (ht >= 0)
            {
                key = "tool:" + ht;
                text = TipOf((Tool)ht);
                anchor = ht == (int)Tool.Settings ? settingsRect : toolRects[ht];
            }
            else if (hn)
            {
                key = "new";
                text = "新建标签页(Ctrl+T)";
                anchor = newRect;
            }
            else if (idx >= 0 && idx < tabs.Count && idx < bounds.Count)
            {
                anchor = bounds[idx];
                if (hc == idx)
                {
                    key = "close:" + idx;
                    text = "关闭标签页(Ctrl+W)";
                    anchor = CloseBounds(anchor);
                }
                else
                {
                    key = "tab:" + idx;
                    // 名字（被省略号截了也能看全）+ 完整路径；两者一样就只留一个
                    text = TipTextFor(tabs[idx]);
                }
            }
            ShowTip(key, text, anchor);

            if (changed) Redraw();

            if (dragFromIndex >= 0 && idx >= 0 && idx != dragOverIndex)
            {
                dragOverIndex = idx;
                Redraw();
            }
        }

        /// <summary>鼠标进标签条 —— 溢出的话滚动条从这儿开始显示（平时是隐的）。</summary>
        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            if (!pointerIn) { pointerIn = true; Redraw(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hoverIndex = -1; hoverCloseIndex = -1; hoverNew = false; hoverTool = -1; hoverWBtn = -1;
            hoverPin = false;
            pointerIn = false; barHot = false; barDrag = false;
            ShowTip(null, null, Rectangle.Empty);
            Redraw();
        }

        // ---- 垂直窗格上的鼠标：跟横向那条几乎不共用，所以单独一组，用 Vertical 分路 ----

        /// <summary>竖排里这一点算不算「空白」（既不是标签也不是任何按钮）—— 空白处就是标题栏。</summary>
        private bool VOnBlank(Point p)
        {
            if (VHitTest(p) >= 0) return false;
            if (NewButtonBounds().Contains(p)) return false;
            if (ToolAt(p) >= 0) return false;
            if (WBtnAt(p) >= 0) return false;
            if (pinRect.Contains(p)) return false;
            return true;
        }

        private void VMouseMove(MouseEventArgs e)
        {
            EnsureLayout();
            int idx = VHitTest(e.Location);
            bool hp = pinRect.Contains(e.Location);
            int hc = (idx >= 0 && !collapsed && VCloseBounds(bounds[idx]).Contains(e.Location)) ? idx : -1;
            int ht = ToolAt(e.Location);
            int hw = WBtnAt(e.Location);
            bool hn = NewButtonBounds().Contains(e.Location);

            string key = null, text = null;
            Rectangle anchor = Rectangle.Empty;
            if (hw >= 0)
            {
                key = "vwbtn:" + hw;
                text = TipOf((WBtn)hw);
                anchor = wbtnRects[hw];
            }
            else if (ht >= 0)
            {
                key = "vtool:" + ht;
                text = TipOf((Tool)ht);
                anchor = ht == (int)Tool.Settings ? settingsRect : toolRects[ht];
            }
            else if (hn)
            {
                key = "vnew";
                text = "新建标签页(Ctrl+T)";
                anchor = newRect;
            }
            else if (hp)
            {
                key = "vpin";
                text = pinOn ? "折叠窗格：开（鼠标不在时只留 ＋ 和 ×）" : "折叠窗格：关（一直展开）";
                anchor = pinRect;
            }
            else if (idx >= 0 && idx < tabs.Count && idx < bounds.Count)
            {
                anchor = bounds[idx];
                if (hc == idx) { key = "vclose:" + idx; text = "关闭标签页(Ctrl+W)"; anchor = VCloseBounds(anchor); }
                else { key = "vtab:" + idx; text = TipTextFor(tabs[idx]); }
            }

            if (idx != hoverIndex || hp != hoverPin || hc != hoverCloseIndex ||
                ht != hoverTool || hw != hoverWBtn || hn != hoverNew)
            {
                hoverIndex = idx;
                hoverPin = hp;
                hoverCloseIndex = hc;
                hoverTool = ht;
                hoverWBtn = hw;
                hoverNew = hn;
                Redraw();
            }
            ShowTip(key, text, anchor);

            // ⚠ 判「左键还按着没」必须用 `Control.MouseButtons`：WinForms 在 MouseMove 的 e.Button 里
            //   经常给 None，拿它判会永远判成「没按」，拖动根本不触发。
            if (blankDrag && (Control.MouseButtons & MouseButtons.Left) != 0)
            {
                int dx = e.Location.X - blankFrom.X, dy = e.Location.Y - blankFrom.Y;
                if (dx * dx + dy * dy >= Px(4) * Px(4)) { blankDrag = false; DragWindow(); }
            }
            // 竖向拖标签排序（横排那套的竖版：落点是**行号**）
            if (dragFromIndex >= 0 && idx >= 0 && idx != dragOverIndex)
            {
                dragOverIndex = idx;
                Redraw();
            }
        }

        private void VMouseDown(MouseEventArgs e)
        {
            EnsureLayout();
            // 右键一律交给 MouseUp 发（在 MouseDown 里弹菜单会被紧接着的「右键抬起」当场关掉）
            if (e.Button != MouseButtons.Left) return;
            ShowTip(null, null, Rectangle.Empty);
            dragFromIndex = -1;

            // 顺序跟横排一致：窗口按钮 → 工具按钮 → 加号 → 图钉 → 标签 → 空白
            int w = WBtnAt(e.Location);
            if (w >= 0) { pendingTool = -1; if (WindowButtonClicked != null) WindowButtonClicked((WBtn)w); return; }
            int t = ToolAt(e.Location);
            if (t >= 0)
            {
                // ⚠ **只记下来**，事件推迟到 VMouseUp 再发 —— 跟横排同一个理由（见 OnMouseUp 那段）：
                //   按着键弹菜单，一松手系统就把鼠标捕获收走，看门狗会把菜单关掉。
                pendingTool = t;
                return;
            }
            if (NewButtonBounds().Contains(e.Location))
            {
                if (NewTabClicked != null) NewTabClicked(this, EventArgs.Empty);
                return;
            }
            if (pinRect.Contains(e.Location)) return;      // 松手才算一次点击（见 VMouseUp）

            int idx = VHitTest(e.Location);
            if (idx < 0)
            {
                // 窗格空白（工具行右侧、标签下面那些地方）—— 也当标题栏拖一把
                blankDrag = true;
                blankFrom = e.Location;
                DropSelectionOnPlainClick();
                return;
            }
            // 按住 Ctrl / Shift 是要「挑一批一起关」，不是要拖排序（本条的响应在 VMouseUp）
            dragFromIndex = ((ModifierKeys & (Keys.Control | Keys.Shift)) != Keys.None) ? -1 : idx;
        }

        private void VMouseUp(MouseEventArgs e)
        {
            EnsureLayout();

            if (e.Button == MouseButtons.Right)
            {
                int ri = VHitTest(e.Location);
                if (ri >= 0) { if (TabRightClicked != null) TabRightClicked(this, ri); }
                else if (VOnBlank(e.Location) && BlankRightClicked != null) BlankRightClicked(e.Location);
                return;
            }
            if (e.Button == MouseButtons.Middle)
            {
                int mi = VHitTest(e.Location);
                if (mi >= 0 && TabMiddleClicked != null) TabMiddleClicked(this, mi);
                return;
            }
            if (e.Button != MouseButtons.Left) return;

            // 功能按钮：松手时鼠标还在同一颗上才算一次点击（跟横排同一套）
            if (pendingTool >= 0)
            {
                int t = pendingTool;
                pendingTool = -1;
                if (ToolAt(e.Location) == t && ToolClicked != null) ToolClicked((Tool)t);
                Redraw();
                return;
            }

            if (pinRect.Contains(e.Location))
            {
                blankDrag = false;
                if (PinClicked != null) PinClicked();
                return;
            }

            // 拖动排序：松手才改数据（拖动中只画落点线，跟横排一样）
            if (dragFromIndex >= 0 && dragOverIndex >= 0 && dragFromIndex != dragOverIndex)
            {
                int from = dragFromIndex, to = dragOverIndex;
                dragFromIndex = -1;
                dragOverIndex = -1;
                blankDrag = false;
                if (OrderMoved != null) OrderMoved(this, from, to);
                MoveTab(from, to);
                if (OrderChanged != null) OrderChanged(this, from);
                Redraw();
                return;
            }
            dragFromIndex = -1;
            dragOverIndex = -1;

            int idx = VHitTest(e.Location);
            if (idx < 0) { blankDrag = false; DropSelectionOnPlainClick(); return; }
            if (!collapsed && VCloseBounds(bounds[idx]).Contains(e.Location))
            {
                if (TabCloseClicked != null) TabCloseClicked(this, idx);
                return;
            }
            if (ApplyMultiSelect(idx)) { blankDrag = false; return; }
            DropSelectionOnPlainClick();
            blankDrag = false;
            if (TabClicked != null) TabClicked(this, idx);
            selAnchor = idx;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            ShowTip(null, null, Rectangle.Empty);
            if (Vertical) { VMouseDown(e); return; }

            // ---- 滚动条优先：它现在是贴顶那一整条，命中判定必须排在标签 / 按钮前面 ----
            // ⚠ 轨道拉满整宽之后，「窗口按钮顶上那 4 逻辑像素」也会被它吃掉（拖滚动 vs 点关闭）。
            //   只有标签真的溢出（overflow）时才存在，代价可接受；不想吃就把轨道改成到 wbtn 左边为止。
            if (e.Button == MouseButtons.Left && overflow)
            {
                Rectangle tr, th;
                if (LayoutScrollBar(out tr, out th) && tr.Contains(e.Location))
                {
                    barDrag = true;
                    barHot = true;
                    if (th.Contains(e.Location))
                        barGrabDX = e.Location.X - th.Left;      // 按在滑块上：保持抓取偏移
                    else
                    {
                        barGrabDX = th.Width / 2;                // 按在轨道上：滑块中心跟到这儿
                        ScrollThumbTo(e.Location.X - barGrabDX, tr, th);
                    }
                    Capture = true;                              // 拖出标签条也要继续跟手
                    Redraw();
                    return;
                }
            }

            if (e.Button == MouseButtons.Right)
            {
                // 右键一律交给 OnMouseUp 发（见那里的说明：在 MouseDown 里发会被紧接着的
                // 「右键抬起」消息当场关掉菜单）。这里什么都不做。
                return;
            }

            if (e.Button == MouseButtons.Left)
            {
                // 窗口按钮最优先（最右边那三颗）
                int w = WBtnAt(e.Location);
                if (w >= 0)
                {
                    dragFromIndex = -1;
                    if (WindowButtonClicked != null) WindowButtonClicked((WBtn)w);
                    return;
                }
                int t = ToolAt(e.Location);
                if (t >= 0)
                {
                    // ⚠ **只记下来**，事件推迟到 OnMouseUp 再发 —— 见 OnMouseUp 里
                    //   「为什么功能按钮不能在这一刻响应」。齿轮 / 历史 / 恢复 / 书签栏都走这条路。
                    dragFromIndex = -1;
                    pendingTool = t;
                    return;
                }
            }

            if (NewButtonBounds().Contains(e.Location))
            {
                if (NewTabClicked != null) NewTabClicked(this, EventArgs.Empty);
                return;
            }

            int idx = HitTest(e.Location);
            if (idx < 0)
            {
                // 空白处按下 —— 先记着位置；真拖动了（OnMouseMove 里过阈值）才走系统那套
                // 「拖标题栏」（贴边吸附、双击最大化都白拿）。
                // 不在按下时就拖，是为了保住「双击空白 = 最大化 / 还原」这一条。
                if (e.Button == MouseButtons.Left)
                {
                    blankDrag = true; blankFrom = e.Location;
                    DropSelectionOnPlainClick();   // 点空白也算「退出多选」
                }
                return;
            }

            if (e.Button == MouseButtons.Middle)
            {
                if (TabMiddleClicked != null) TabMiddleClicked(this, idx);
                return;
            }
            if (OnCloseButton(idx, e.Location))
            {
                if (TabCloseClicked != null) TabCloseClicked(this, idx);
                return;
            }
            if (ApplyMultiSelect(idx)) return;     // Ctrl / Shift = 挑标签：不切过去、也不拖排序
            DropSelectionOnPlainClick();
            if (TabClicked != null) TabClicked(this, idx);

            selAnchor = idx;                        // Shift 连选从这儿开始数
            dragFromIndex = idx;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (Vertical) { VMouseUp(e); return; }

            if (barDrag)
            {
                barDrag = false;
                Capture = false;
                Redraw();
                return;
            }

            // ---- 功能按钮：**松手才发**（松手时鼠标还在同一颗按钮上才算一次点击）----
            // ⚠ 用户报「点击历史记录图标，出菜单后闪一下就没了；按快捷键不会」。
            //   根因：按钮原来在 **MouseDown** 里就发事件，菜单紧接着就弹出来了 —— 那一刻
            //   **左键还按着**。我们的菜单窗口不是前台窗口，Windows 只允许「有按键按着的时候」
            //   抓着鼠标捕获；用户一松手，捕获当场被系统收走，菜单的看门狗（250ms）以为捕获被
            //   外人抢了，就把菜单收了（日志里每次都是 ~294ms 后一行「菜单: 关闭」，而快捷键
            //   那条路没有按键参与，活得好好的）。
            //   修法：跟右键那条一样，**等松手再弹** —— 松手时没有按键按着，捕获不会被收走。
            if (pendingTool >= 0)
            {
                int t = pendingTool;
                pendingTool = -1;
                if (ToolAt(e.Location) == t && ToolClicked != null) ToolClicked((Tool)t);
                Redraw();
                return;
            }

            // ⚠ 右键菜单必须在 **MouseUp** 里发（用户报「空白处和标签右键功能均没有实现」）：
            // 在 MouseDown 里叫起菜单时，紧接着那条「右键抬起」消息会投到刚弹出来的菜单窗口上
            // （菜单自己抓着鼠标捕获），菜单把它当成「点在别处」→ 当场关掉。
            // 于是：菜单一闪而过、或者用户点哪个条目都对不上 —— 看着就是「右键没功能」。
            // 等到按钮抬起来再弹，就没有这条多余的消息了。
            if (e.Button == MouseButtons.Right)
            {
                int ri = HitTest(e.Location);
                if (ri >= 0)
                {
                    if (TabRightClicked != null) TabRightClicked(this, ri);
                }
                else if (ToolAt(e.Location) < 0 && WBtnAt(e.Location) < 0 &&
                         !NewButtonBounds().Contains(e.Location))
                {
                    // 空白区域（不是标签、不是按钮）
                    if (BlankRightClicked != null) BlankRightClicked(e.Location);
                }
                return;
            }

            if (dragFromIndex >= 0 && dragOverIndex >= 0 && dragFromIndex != dragOverIndex)
            {
                int from = dragFromIndex, to = dragOverIndex;
                if (OrderMoved != null) OrderMoved(this, from, to);
                MoveTab(from, to);
                if (OrderChanged != null) OrderChanged(this, from);
            }
            dragFromIndex = -1; dragOverIndex = -1;
            blankDrag = false;
            Redraw();
        }

        /// <summary>
        /// 标签条上的滚轮 = **切换前后标签页**（用户）。
        /// 横向滚动标签条是另一个入口：滚轮在非标签条区域时由上层（EmbedForm）转发到 `ScrollTabsBy`。
        /// </summary>
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (tabs.Count < 2) return;
            if (TabWheel != null) TabWheel(e.Delta);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (tips != null) tips.Dispose();
                if (pinFont != null) pinFont.Dispose();
                if (titleFont != null) titleFont.Dispose();
                if (tipTitleFont != null) tipTitleFont.Dispose();
                if (glyphFont != null) glyphFont.Dispose();
                if (favGlyphFont != null) favGlyphFont.Dispose();
                if (wbtnFont != null) wbtnFont.Dispose();
            }
            base.Dispose(disposing);
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            base.OnDoubleClick(e);
            Point p = PointToClient(Cursor.Position);
            // 空白处双击 = 最大化 / 还原，**横排竖排一样**（用户 2026-09-25：横排那条从前是
            // 「新开一个标签页」，他不认 —— 标题栏该有的动作就是放大缩小；开标签页本来就有那颗 `+`
            // 和 Ctrl+T）。竖排整条窗格是标题栏，横排这条标签带也是。
            // 不自己改 WindowState，而是补一条 WM_NCLBUTTONDBLCLK(HTCAPTION) 交给系统 ——
            // 跟拖拽那条路一样走原生标题栏处理，行为（以及以后的调整）都跟真标题栏一致。
            if (Vertical ? VOnBlank(p) : OnBlank(p)) MaximizeFromTitle();
        }

        /// <summary>
        /// 空白处按住拖动 —— 把「拖标题栏」还给系统（见 DragOrMaximize）。
        /// 自绘标题栏那一行删掉之后，这段就从 TitleBar 搬过来了。
        /// </summary>
        internal void DragWindow()
        {
            DragOrMaximize(EmbedApi.WM_NCLBUTTONDOWN);
        }

        /// <summary>空白处双击 = 最大化 / 还原（补一条原生标题栏消息，见 OnDoubleClick）。</summary>
        private void MaximizeFromTitle()
        {
            DragOrMaximize(EmbedApi.WM_NCLBUTTONDBLCLK);
        }

        /// <summary>
        /// ReleaseCapture 之后补一条「非客户区按下」给 DefWindowProc ——
        /// 拖动 / 双击最大化 / 贴边吸附全白拿，不用自己实现。
        /// </summary>
        private void DragOrMaximize(uint msg)
        {
            try
            {
                Form f = FindForm();
                if (f == null) return;
                EmbedApi.ReleaseCapture();
                EmbedApi.SendMessageW(f.Handle, msg, new IntPtr(EmbedApi.HTCAPTION), IntPtr.Zero);
            }
            catch (Exception ex) { Diag.Log("TabStrip: 标题栏动作失败 " + ex.Message); }
        }
    }
}
