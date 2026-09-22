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

        // 位置全部由 EnsureLayout 算（标签数 / 窗口宽 / 右侧那排按钮都会变）。
        private Rectangle newRect;
        private Rectangle settingsRect;
        private static readonly Rectangle[] toolRects = new Rectangle[4];   // 见 Tool 枚举
        private static readonly Rectangle[] wbtnRects = new Rectangle[3];   // 见 WBtn 枚举
        private int dividerX;
        private int toolsLeft;

        // 标签宽度：跟着设置走（逻辑像素 ×DPI）。自适应开着时它当上限。
        private int MinTabWidth { get { return Math.Min(Px(72), MaxTabWidth); } }
        private int MaxTabWidth { get { return Px(Settings.TabWidth); } }
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
        private readonly Font glyphFont;      // 右侧工具按钮 / 窗口按钮的 MDL2 字形

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
            scrollX = 0;                 // 标签少了一个，底下那些也可能能露出来了
            Invalidate();
        }

        public void SetActive(int index)
        {
            for (int i = 0; i < tabs.Count; i++) tabs[i].Active = (i == index);
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

            // ① 每个标签想要的宽度
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
                    w = Math.Max(MinTabWidth, Math.Min(MaxTabWidth, need));
                }
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
            get { EnsureLayout(); return Math.Max(0, toolsLeft - NewButtonWidth - Px(2)); }
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
            g.SetClip(new Rectangle(0, 0, Math.Max(1, TabsClipRight), Height));

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
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

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
                TextRenderer.DrawText(g, GlyphOf((Tool)i), glyphFont, b, fg,
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
                TextRenderer.DrawText(g, glyph[i], glyphFont, b, fg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
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

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hoverIndex = -1; hoverCloseIndex = -1; hoverNew = false; hoverTool = -1; hoverWBtn = -1;
            ShowTip(null, null, Rectangle.Empty);
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            ShowTip(null, null, Rectangle.Empty);

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
                    dragFromIndex = -1;
                    if (ToolClicked != null) ToolClicked((Tool)t);
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
