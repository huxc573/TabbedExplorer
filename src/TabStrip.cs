using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace TabbedExplorer
{
    /// <summary>
    /// 标签条 —— 照浏览器那套做（2026-09-22 川定：整个程序就是「仿浏览器设计、增强 Win10 资源管理器」）。
    ///
    /// 布局（从右往左）：
    ///   [×][□][—]  [齿轮] | [收藏夹栏] [恢复关闭] [历史]  ……空白……  [+ 紧跟最后一个标签]
    /// 最右边那三个是**窗口按钮**（最小化 / 最大化 / 关闭），齿轮单独用一条竖线隔开
    /// （跟 Edge 一样：扩展一块、头像一块）。
    ///
    /// 2026-09-22 第二次改（川报的 bug 2 + 评估 1）：
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

        /// <summary>标签条标准高度（逻辑像素）。**一行**文字，所以比两行那版矮回来。</summary>
        public const int StdHeight = 34;

        private readonly List<TabItem> tabs = new List<TabItem>();
        private readonly List<Rectangle> bounds = new List<Rectangle>();

        /// <summary>
        /// 悬停提示。⚠ `ShowAlways = true` 是必须的：不开的时候**宿主窗口不是前台就不弹** ——
        /// 川报的「未激活时移到标签上没显示名字，可关闭按钮会变色」就是这个（鼠标事件收到了，提示被憋掉了）。
        /// 配色走 `Theme.StyleTip`（自绘，跟着颜色模式走）。
        /// </summary>
        private readonly ToolTip tips = new ToolTip();

        private int hoverIndex = -1;
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
        private static readonly Rectangle[] toolRects = new Rectangle[4];   // 见 Tool 枚举
        private static readonly Rectangle[] wbtnRects = new Rectangle[3];   // 见 WBtn 枚举
        private int dividerX;
        private int toolsLeft;
        /// <summary>标签区右界（画的时候裁到这儿、命中判定也以它为准）。由 EnsureLayout 算一次，两边共用。</summary>
        private int tabsClipRight;

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

        private readonly Font titleFont;      // 标签文字（**不加粗** —— 川 2026-09-22：加粗留给悬停提示）
        private readonly Font tipTitleFont;   // 悬停提示里「文件夹名」那一行的字体（加粗）
        private readonly Font glyphFont;      // 右侧工具按钮的 MDL2 字形
        /// <summary>
        /// 窗口按钮（最小化 / 最大化 / 关闭）的字形 —— **比工具按钮小一号**。
        /// MDL2 里这三个字形（E921/E922/E923/E8BB）是**填满 em 框**的，同样 12px 下看着比
        /// 齿轮 / 星星 / 历史那几个大一圈（川 2026-09-22 报的「三个按钮图标太大了，和其它不统一」）。
        /// 10px 正好是 Win10 原生标题栏里这三个字形的尺寸（96dpi 下量到约 10px）。
        /// </summary>
        private readonly Font wbtnFont;
        /// <summary>
        /// 收藏夹那枚星（E734/E735）单独用大一档的字号 ——
        /// 它是空心/实心五角星，ink 天生比齿轮（自绘）、历史（E81C 圆盘）、恢复（E7A7 弯箭头）小一圈，
        /// 同一个字号并排会明显看着小（川 2026-09-22：「右上三颗窗控图标已经一样大了，收藏夹图标有点小」）。
        /// </summary>
        private readonly Font favGlyphFont;

        /// <summary>窗口没激活（失活）时整体降色 —— 底色和文字都跟原生标题栏一个逻辑。</summary>
        public bool Inactive
        {
            get { return inactive; }
            set
            {
                if (inactive == value) return;
                inactive = value;
                BackColor = BarBack;
                Invalidate();
            }
        }
        private bool inactive;

        /// <summary>标签条底：激活 / 未激活两套色（川要的「主窗口也模拟原生激活逻辑」）。</summary>
        private Color BarBack { get { return inactive ? Theme.TabBarOff : Theme.TabBar; } }

        /// <summary>窗口最大化着没（决定右上角那颗画「最大化」还是「还原」）。</summary>
        public bool Maximized
        {
            get { return maximized; }
            set { if (maximized != value) { maximized = value; Invalidate(); } }
        }
        private bool maximized;

        /// <summary>收藏夹栏现在是开着的（按钮画成实心星）。</summary>
        public bool FavBarOn
        {
            get { return favBarOn; }
            set { if (favBarOn != value) { favBarOn = value; Invalidate(); } }
        }
        private bool favBarOn;

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
        /// <summary>标签条**空白区域**（不是标签、不是按钮）上按了右键。</summary>
        public event Action<Point> BlankRightClicked;
        /// <summary>在标签条上滚滚轮（川 2026-09-22：标签条上滚轮 = 切换前后标签页）。参数是 delta（正=往上滚=上一个）。</summary>
        public event Action<int> TabWheel;

        public TabStrip()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.Selectable, true);
            Height = Px(StdHeight);
            // 标签标题**不加粗**（川：加粗留给悬停提示）；悬停提示里名字那行加粗、路径不加粗。
            titleFont = new Font("Segoe UI", Px(12), FontStyle.Regular, GraphicsUnit.Pixel);
            tipTitleFont = new Font("Segoe UI", Px(12), FontStyle.Bold, GraphicsUnit.Pixel);
            glyphFont = new Font("Segoe MDL2 Assets", Px(12), FontStyle.Regular, GraphicsUnit.Pixel);
            wbtnFont = new Font("Segoe MDL2 Assets", Px(10), FontStyle.Regular, GraphicsUnit.Pixel);
            favGlyphFont = new Font("Segoe MDL2 Assets", Px(14), FontStyle.Regular, GraphicsUnit.Pixel);
            BackColor = Theme.TabBar;
            AllowDrop = true;
            tips.InitialDelay = 350;    // 停一下再弹，别鼠标一扫过就满屏提示
            tips.ReshowDelay = 80;
            tips.AutoPopDelay = 8000;
            tips.ShowAlways = true;     // 见字段注释：不开的话窗口没激活就不弹
            Theme.StyleTip(tips, tipTitleFont, titleFont);   // 名字那行粗体、路径行常规（量尺寸也要按粗体量）
            Theme.Changed += delegate { BackColor = BarBack; tips.BackColor = Theme.MenuBack; tips.ForeColor = Theme.Text; Invalidate(); };
        }

        public IList<TabItem> Tabs { get { return tabs; } }

        public void AddTab(string title)
        {
            tabs.Add(new TabItem { Title = title ?? "", TextW = MeasureTitle(title) });
            Invalidate();
        }

        public void SetTitle(int index, string title)
        {
            if (index < 0 || index >= tabs.Count) return;
            if (tabs[index].Title == title) return;
            tabs[index].Title = title ?? "";
            tabs[index].TextW = MeasureTitle(tabs[index].Title);
            Invalidate();
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
            Invalidate();
        }

        /// <summary>换掉某个标签的图标（当前文件夹的实时图标，导航后上层会再调）。</summary>
        public void SetIcon(int index, Image icon)
        {
            if (index < 0 || index >= tabs.Count) return;
            if (tabs[index].Icon == icon) return;
            tabs[index].Icon = icon;
            Invalidate();
        }

        public void RemoveTab(int index)
        {
            if (index < 0 || index >= tabs.Count) return;
            tabs.RemoveAt(index);
            if (hoverIndex == index) hoverIndex = -1;
            // 不用把 scrollX 归零：下一次 EnsureLayout 会把它夹到新的 maxScroll 上
            // （归零反而会让川刚滑到的位置白滑 —— 关一个标签不该把视口弹回最左边）。
            Invalidate();
        }

        public void SetActive(int index)
        {
            for (int i = 0; i < tabs.Count; i++) tabs[i].Active = (i == index);
            ScrollActiveIntoView();   // 选中的标签不许停在屏幕外（浏览器都这么做）
            Invalidate();
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
            int nx = scrollX;
            if (bounds[i].Left < 0) nx = scrollX + bounds[i].Left;
            else if (bounds[i].Right > tabsClipRight) nx = scrollX + (bounds[i].Right - tabsClipRight);
            if (nx < 0) nx = 0;
            if (nx > maxScroll) nx = maxScroll;
            if (nx == scrollX) return;
            scrollX = nx;
            Invalidate();
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
            Invalidate();
        }

        // ------------------------------------------------------------------
        /// <summary>
        /// 排一次版。右侧那一排是**固定**的（窗口按钮 + 齿轮 + 竖线 + 三个功能按钮），
        /// 标签和「+」只在剩下的宽度里排。尺寸只由 Width + 标签数决定，鼠标事件里可以随手重算。
        ///
        /// 宽度规则（2026-09-22 川要求拆成两项）：
        ///   ① `tabautowiden` 开 → 每个标签按**自己那行文字**的宽度来（夹在 MinTabWidth ~ TabWidth 之间）；
        ///      关 → 一律 `TabWidth`。
        ///   ② `tabautofit` 开 → 全排加起来挤不下就**一起缩窄**（下限 MinTabWidth）；
        ///      关 → 不缩，但**总宽仍然不越过右边那排按钮**：多出来的部分靠横向滚动看（`scrollX`）。
        /// </summary>
        private void EnsureLayout()
        {
            // 最右：窗口按钮（从右往左：关闭 → 最大化 → 最小化）
            wbtnRects[(int)WBtn.Close] = new Rectangle(Width - WBtnWidth, 0, WBtnWidth, Height);
            wbtnRects[(int)WBtn.Maximize] =
                new Rectangle(wbtnRects[(int)WBtn.Close].Left - WBtnWidth, 0, WBtnWidth, Height);
            wbtnRects[(int)WBtn.Minimize] =
                new Rectangle(wbtnRects[(int)WBtn.Maximize].Left - WBtnWidth, 0, WBtnWidth, Height);

            // 再往左：齿轮 → 竖线 → 收藏夹栏 → 恢复关闭 → 历史
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
            // ⚠ 2026-09-22 川报「开启名称过长时自动加宽时，单标签页并未加宽」——
            //   根因：原来把加宽也卡在 `TabWidth` 上（`min(MaxTabWidth, need)`），
            //   于是名字再长、宽也超不过「标签页宽度」那一项，看着就是没加宽。
            //   现在 `TabWidth` 是**基准宽度**：名字放得下就一样宽，放不下才往上加，上限 `TabWidenMax`。
            int[] want = new int[tabs.Count];
            int total = 0;
            for (int i = 0; i < tabs.Count; i++)
            {
                int t = tabs[i].TextW;
                if (t <= 0) t = MeasureTitle(tabs[i].Title);
                int w = MaxTabWidth;
                if (Settings.TabAutoWiden)
                {
                    // 图标 + 左右留白 + 右边给关闭按钮留位
                    int need = Px(TextPadLeft) + IconSize + Px(IconGap) + t + CloseAreaWidth + Px(4);
                    w = Math.Max(MaxTabWidth, Math.Min(WidenMaxWidth, need));
                    // ⚠ 2026-09-22 川报「过长依然出现遮挡问题（没收到滚动条范围内）」：
                    //   原来这里还把 w 再夹一次「标签区可用宽度」（`if (w > avail) w = avail`），
                    //   于是超长名字的标签被硬切成 avail 宽 —— `total == avail`，
                    //   `maxScroll` 只剩 Px(4)，**被吃掉的那一截滚也滚不出来**，看着就是「被遮挡」。
                    //   现在不夹了：名字有多长标签就有多宽（上限 `TabWidenMax`），
                    //   超出可视区的那部分交给横向滚动露出来 —— 这正是「自动加宽 + 滚动」的分工。
                    //   想「谁也别挤谁」就把「自动缩窄」打开，那条路是 `TabAutoFit`，别在这儿夹。
                }
                if (w < MinTabWidth) w = MinTabWidth;
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
            int nx = (tabs.Count > 0) ? x + Px(4) : Px(4);
            int limit = areaRight - NewButtonWidth;
            if (nx > limit) nx = Math.Max(Px(2), limit);
            newRect = new Rectangle(nx, 0, NewButtonWidth, Height);
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
            Invalidate();
            return true;
        }

        private int scrollX;
        private int maxScroll;

        // ---- 横向滚动条（川 2026-09-22：平时隐藏、鼠标进标签条才显示、能点能拖）----
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

        /// <summary>某个工具按钮的位置（历史 / 恢复 / 收藏夹栏 / 齿轮都从这儿取锚点）。</summary>
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
            // ⚠ 「标签和右边那排按钮是同一层」的另一半（川 2026-09-22）：
            //   标签溢出了的话，最后几个标签的矩形是**伸到按钮底下**的（只被裁掉了不画）。
            //   命中判定不过这一刀，在右边空白处右键就会弹「标签右键」（那个 x 坐落在被裁掉的
            //   那半个标签里）—— 川报的「右边空白菜单没反应」就是这个。先按裁剪线切一刀。
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

            for (int i = 0; i < tabs.Count; i++)
            {
                Rectangle tab = bounds[i];
                if (tab.Right < 0 || tab.Left > Width) continue;

                Color fill = tabs[i].Active ? (inactive ? Theme.TabActiveOff : Theme.TabActive)
                             : (i == hoverIndex ? Theme.Hover : bar);
                g.FillRectangle(new SolidBrush(fill), tab);

                if (tabs[i].Active)
                {
                    // 选中标签：顶上一条 2px 蓝线，正好压住这一条的上边缘（原生死白就在这）
                    int accentH = Math.Max(2, Px(2));
                    g.FillRectangle(new SolidBrush(inactive ? Theme.AccentDim : Theme.Accent),
                                    new Rectangle(tab.Left, tab.Top, tab.Width, accentH));
                }
                else if (i > 0)
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

                Color c1 = tabs[i].Active ? (inactive ? Theme.TextInactive : Theme.Text)
                                          : (inactive ? Theme.TextInactive : Theme.TextDim);

                TextRenderer.DrawText(g, tabs[i].Title, titleFont,
                    new Rectangle(textLeft, tab.Top, textW, tab.Height), c1,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding |
                    TextFormatFlags.PreserveGraphicsClipping);

                if (showClose && roomForClose)
                {
                    bool hc = i == hoverCloseIndex;
                    if (hc) g.FillRectangle(new SolidBrush(Color.FromArgb(232, 17, 35)), close);
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

            // ---- 标签溢出时的位置指示条（川 2026-09-22 要的「隐藏进度条」）----
            // 就画在标签区**贴底**一条细条上：底 = 标签区宽度，滑块 = 当前能看到的那一段。
            // 它在裁剪区之外（先 Clip 恢复再画），不然滑块永远只能看到左边一截。
            DrawScrollBar(g);

            g.DrawLine(new Pen(Theme.Border), 0, Height - 1, Width, Height - 1);

            // “+” 新建：紧跟在最后一个标签右边（位置由 EnsureLayout 算）
            Rectangle nb = newRect;
            if (hoverNew) g.FillRectangle(new SolidBrush(Theme.Hover), nb);
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
                if (i == hoverTool) g.FillRectangle(new SolidBrush(Theme.Hover), b);
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
                    g.FillRectangle(new SolidBrush(i == (int)WBtn.Close ? Color.FromArgb(232, 17, 35) : Theme.Hover), b);
                Color fg = (i == hoverWBtn) ? Color.White
                         : (inactive ? Theme.TextInactive : Theme.Text);
                TextRenderer.DrawText(g, glyph[i], wbtnFont, b, fg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        /// <summary>
        /// 算滚动条的**轨道 / 滑块**矩形（没溢出就返回 false）。
        /// ⚠ 画和命中判定都走这一份 —— 两处各算一次的话，看到的滑块和点得着的滑块迟早会差几个像素。
        /// 轨道 = **整条标签条的宽度、贴在最顶上**（川 2026-09-22 指定），
        /// 滑块宽 = 「看得见的那一段 / 全部」的比例，滑块位置 = 滚到哪儿了。
        /// </summary>
        private bool LayoutScrollBar(out Rectangle track, out Rectangle thumb)
        {
            track = Rectangle.Empty; thumb = Rectangle.Empty;
            if (maxScroll <= 0 || tabs.Count < 2) return false;

            // 川 2026-09-22：挪到**顶部**、并且**拉满整条标签条的宽度**
            // （原来是压在标签底下、只占标签区）。平时不显示（`DrawScrollBar` 里判 `pointerIn`），
            // 所以压住选中标签那条蓝线不影响观感。
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
        /// 画标签溢出时那条横向滚动条（川 2026-09-22：**平时隐藏**，鼠标进标签条才显示；
        /// 位置在标签条**最顶上、拉满整条宽度** —— 川后来指定的，放底下不好找）。
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
            Invalidate();
        }

        /// <summary>
        /// 三个功能按钮的图标。用的是 `Segoe MDL2 Assets` 的码位 ——
        /// 这几个都拿 PIL 渲染对照图**看过实物**才写的（E81C 带逆时针箭头的钟 = 历史、
        /// E7A7 回弯箭头 = 恢复、E734/E735 空心/实心星 = 收藏夹栏开没开），别凭记忆改。
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
                case Tool.Fav: return (favBarOn ? "隐藏" : "显示") + "收藏夹栏(Ctrl+Shift+B)";
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
            if (anchor.Right > Width) anchor.X = Math.Max(0, Width - anchor.Width - Px(40));
            // 先把「马上要显示的原文」交给 Theme（它自己量尺寸得知道原文，见 Theme.TipText）
            Theme.TipText(tips, text);
            tips.Show(text, this, anchor.Left, anchor.Bottom + Px(2), 8000);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            pointerIn = true;

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

            if (changed) Invalidate();

            if (dragFromIndex >= 0 && idx >= 0 && idx != dragOverIndex)
            {
                dragOverIndex = idx;
                Invalidate();
            }
        }

        /// <summary>鼠标进标签条 —— 溢出的话滚动条从这儿开始显示（平时是隐的）。</summary>
        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            if (!pointerIn) { pointerIn = true; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hoverIndex = -1; hoverCloseIndex = -1; hoverNew = false; hoverTool = -1; hoverWBtn = -1;
            pointerIn = false; barHot = false; barDrag = false;
            ShowTip(null, null, Rectangle.Empty);
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            ShowTip(null, null, Rectangle.Empty);

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
                    Invalidate();
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
                    //   「为什么功能按钮不能在这一刻响应」。齿轮 / 历史 / 恢复 / 收藏夹栏都走这条路。
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
                // 不在按下时就拖，是为了保住「双击空白 = 新标签」这一条。
                if (e.Button == MouseButtons.Left) { blankDrag = true; blankFrom = e.Location; }
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
            if (TabClicked != null) TabClicked(this, idx);

            dragFromIndex = idx;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);

            if (barDrag)
            {
                barDrag = false;
                Capture = false;
                Invalidate();
                return;
            }

            // ---- 功能按钮：**松手才发**（松手时鼠标还在同一颗按钮上才算一次点击）----
            // ⚠ 2026-09-22 川报「点击历史记录图标，出菜单后闪一下就没了；按快捷键不会」。
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
                Invalidate();
                return;
            }

            // ⚠ 右键菜单必须在 **MouseUp** 里发（2026-09-22 川报「空白处和标签右键功能均没有实现」）：
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
                MoveTab(from, to);
                if (OrderChanged != null) OrderChanged(this, from);
            }
            dragFromIndex = -1; dragOverIndex = -1;
            blankDrag = false;
            Invalidate();
        }

        /// <summary>
        /// 标签条上的滚轮 = **切换前后标签页**（川 2026-09-22）。
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
            if (OnBlank(p))
            {
                if (NewTabClicked != null) NewTabClicked(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// 空白处按住拖动 —— 把「拖标题栏」还给系统：ReleaseCapture 之后补一条
        /// WM_NCLBUTTONDOWN(HTCAPTION)，DefWindowProc 就走原生移动循环（拖动、贴边吸附、双击最大化）。
        /// 自绘标题栏那一行删掉之后，这段就从 TitleBar 搬过来了。
        /// </summary>
        internal void DragWindow()
        {
            try
            {
                Form f = FindForm();
                if (f == null) return;
                EmbedApi.ReleaseCapture();
                EmbedApi.SendMessageW(f.Handle, EmbedApi.WM_NCLBUTTONDOWN,
                    new IntPtr(EmbedApi.HTCAPTION), IntPtr.Zero);
            }
            catch (Exception ex) { Diag.Log("TabStrip: 拖动失败 " + ex.Message); }
        }
    }
}
