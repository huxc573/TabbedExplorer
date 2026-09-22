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
    ///   [齿轮] | [收藏夹栏] [恢复关闭] [历史]  ……空白……  [+ 紧跟最后一个标签]
    /// 齿轮单独用一条竖线隔开（跟 Edge 一样：头像一块、扩展一块）。
    ///
    /// 每个标签**两行**：第一行文件夹名（加粗），第二行完整路径。
    ///
    /// 尺寸全部走 DpiScale（硬编码在 150% 下会错位）。
    /// </summary>
    internal sealed class TabStrip : Control
    {
        /// <summary>右侧那排工具按钮（齿轮单独一个，见 EnsureLayout）。</summary>
        public enum Tool { History, Reopen, Fav, Settings }

        public sealed class TabItem
        {
            public string Title = "";
            /// <summary>第二行：完整路径（「此电脑」这类没有真实路径的显示友好名）。</summary>
            public string Path = "";
            public bool Active;
            /// <summary>当前文件夹的图标（由上层从 explorer 窗口读出来，导航后会换）。</summary>
            public Image Icon;
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

        /// <summary>标签条标准高度（逻辑像素）。**两行**文字，所以比原来一条 34 高一些。</summary>
        public const int StdHeight = 44;

        private readonly List<TabItem> tabs = new List<TabItem>();
        private readonly List<Rectangle> bounds = new List<Rectangle>();

        /// <summary>
        /// 悬停提示。⚠ `ShowAlways = true` 是必须的：不开的时候**宿主窗口不是前台就不弹** ——
        /// 川报的「未激活时移到标签上没显示名字，可关闭按钮会变色」就是这个（鼠标事件收到了，提示被憋掉了）。
        /// </summary>
        private readonly ToolTip tips = new ToolTip();

        private int hoverIndex = -1;
        private int hoverCloseIndex = -1;
        private bool hoverNew;
        private int hoverTool = -1;
        /// <summary>现在弹着提示的是谁（`"tab:3"` / `"close:0"` / `"tool:2"` …）。换了才重弹，不然鼠标一动就闪。</summary>
        private string tipKey;
        private int dragFromIndex = -1;
        private int dragOverIndex = -1;

        // 位置全部由 EnsureLayout 算（标签数 / 窗口宽 / 右侧那排按钮都会变）。
        private Rectangle newRect;
        private Rectangle settingsRect;
        private static readonly Rectangle[] toolRects = new Rectangle[4];   // 见 Tool 枚举
        private int dividerX;
        private int toolsLeft;

        // 标签宽度：跟着设置走（逻辑像素 ×DPI）。自适应开着时它当上限。
        private int MinTabWidth { get { return Math.Min(Px(72), MaxTabWidth); } }
        private int MaxTabWidth { get { return Px(Settings.TabWidth); } }
        private int NewButtonWidth { get { return Px(28); } }
        private int ToolButtonWidth { get { return Px(32); } }
        private int SettingsButtonWidth { get { return Px(34); } }
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

        private readonly Font titleFont;      // 第一行：加粗
        private readonly Font pathFont;       // 第二行：小一号
        private readonly Font glyphFont;      // 右侧工具按钮的 MDL2 字形

        /// <summary>窗口没激活（失活）时整体文字降灰 —— 跟原生标题栏一个逻辑。</summary>
        public bool Inactive
        {
            get { return inactive; }
            set { if (inactive != value) { inactive = value; Invalidate(); } }
        }
        private bool inactive;

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
        public event IndexEventHandler TabMiddleClicked;
        public event IndexEventHandler OrderChanged;   // 拖拽排序后：原索引
        /// <summary>标签条**空白区域**（不是标签、不是按钮）上按了右键。</summary>
        public event Action<Point> BlankRightClicked;

        public TabStrip()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.Selectable, true);
            Height = Px(StdHeight);
            titleFont = new Font("Segoe UI", Px(12), FontStyle.Bold, GraphicsUnit.Pixel);
            pathFont = new Font("Segoe UI", Px(10), FontStyle.Regular, GraphicsUnit.Pixel);
            glyphFont = new Font("Segoe MDL2 Assets", Px(14), FontStyle.Regular, GraphicsUnit.Pixel);
            BackColor = Theme.TabBar;
            AllowDrop = true;
            tips.InitialDelay = 350;    // 停一下再弹，别鼠标一扫过就满屏提示
            tips.ReshowDelay = 80;
            tips.AutoPopDelay = 8000;
            tips.ShowAlways = true;     // 见字段注释：不开的话窗口没激活就不弹
            Theme.Changed += delegate { BackColor = Theme.TabBar; Invalidate(); };
        }

        public IList<TabItem> Tabs { get { return tabs; } }

        public void AddTab(string title)
        {
            tabs.Add(new TabItem { Title = title });
            Invalidate();
        }

        public void SetTitle(int index, string title)
        {
            if (index < 0 || index >= tabs.Count) return;
            if (tabs[index].Title == title) return;
            tabs[index].Title = title;
            Invalidate();
        }

        /// <summary>换第二行那个路径（「此电脑」这类没有真实路径的由上层给友好名）。</summary>
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
        /// 排一次版。右侧那一排是**固定**的（齿轮 + 竖线 + 三个功能按钮），
        /// 标签和「+」只在剩下的宽度里排。尺寸只由 Width + 标签数决定，鼠标事件里可以随手重算。
        /// </summary>
        private void EnsureLayout()
        {
            // 右侧固定区（从右往左：齿轮 → 竖线 → 收藏夹栏 → 恢复关闭 → 历史）
            settingsRect = new Rectangle(Width - SettingsButtonWidth, 0, SettingsButtonWidth, Height);
            int divL = settingsRect.Left - DividerWidth;
            dividerX = divL + DividerWidth / 2;
            toolRects[(int)Tool.Fav] = new Rectangle(divL - ToolButtonWidth, 0, ToolButtonWidth, Height);
            toolRects[(int)Tool.Reopen] = new Rectangle(toolRects[(int)Tool.Fav].Left - ToolButtonWidth, 0, ToolButtonWidth, Height);
            toolRects[(int)Tool.History] = new Rectangle(toolRects[(int)Tool.Reopen].Left - ToolButtonWidth, 0, ToolButtonWidth, Height);
            toolsLeft = toolRects[(int)Tool.History].Left;

            bounds.Clear();
            int avail = Math.Max(Px(60), toolsLeft - NewButtonWidth - Px(10));
            int w = MaxTabWidth;
            if (tabs.Count > 0 && Settings.TabAutoFit)
            {
                int need = tabs.Count * MaxTabWidth;
                if (need > avail) w = Math.Max(MinTabWidth, avail / tabs.Count);
            }
            int x = Px(2);
            for (int i = 0; i < tabs.Count; i++)
            {
                bounds.Add(new Rectangle(x, 0, w, Height));
                x += w;
            }

            int nx = (tabs.Count > 0) ? x + Px(4) : Px(4);
            int limit = toolsLeft - NewButtonWidth - Px(2);
            if (nx > limit) nx = Math.Max(Px(2), limit);
            newRect = new Rectangle(nx, 0, NewButtonWidth, Height);
        }

        private Rectangle NewButtonBounds()
        {
            EnsureLayout();
            return newRect;
        }

        /// <summary>齿轮的位置（EmbedForm 弹设置菜单要拿它当锚点）。</summary>
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

        protected override void OnPaint(PaintEventArgs e)
        {
            EnsureLayout();
            Graphics g = e.Graphics;
            Rectangle r = ClientRectangle;

            g.FillRectangle(new SolidBrush(BackColor), r);

            float stroke = Math.Max(1f, DpiScale);

            for (int i = 0; i < tabs.Count; i++)
            {
                Rectangle tab = bounds[i];
                if (tab.Right < 0 || tab.Left > Width) continue;

                Color fill = tabs[i].Active ? Theme.TabActive
                             : (i == hoverIndex ? Theme.Hover : Theme.TabBar);
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

                // ---- 两行文字：第一行文件夹名（加粗）/ 第二行完整路径 ----
                int textLeft = tab.Left + Px(TextPadLeft) + isz + Px(IconGap);
                int textW = textRight - textLeft;
                if (textW < 0) textW = 0;
                int line1H = Px(16), line2H = Px(13);
                int blockTop = tab.Top + Math.Max(0, (tab.Height - (line1H + line2H + Px(2))) / 2);

                Color c1 = tabs[i].Active ? (inactive ? Theme.TextInactive : Theme.Text)
                                          : (inactive ? Theme.TextInactive : Theme.TextDim);
                Color c2 = inactive ? Theme.TextInactive : Theme.TextDim;
                if (!tabs[i].Active && i != hoverIndex) c2 = inactive ? Theme.TextInactive : Theme.TextDim;

                TextRenderer.DrawText(g, tabs[i].Title, titleFont,
                    new Rectangle(textLeft, blockTop, textW, line1H), c1,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

                string pl = tabs[i].Path;
                if (!string.IsNullOrEmpty(pl))
                {
                    TextRenderer.DrawText(g, pl, pathFont,
                        new Rectangle(textLeft, blockTop + line1H + Px(2), textW, line2H),
                        c2,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.PathEllipsis | TextFormatFlags.NoPadding);
                }

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
            Color sBack = hoverTool == (int)Tool.Settings ? Theme.Hover : Theme.TabBar;
            if (hoverTool == (int)Tool.Settings) g.FillRectangle(new SolidBrush(sBack), sbr);
            DrawGear(g, sbr, hoverTool == (int)Tool.Settings ? Theme.Text
                             : (inactive ? Theme.TextInactive : Theme.TextDim), sBack, stroke);
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

        /// <summary>弹提示。同一个目标只弹一次（换了才重弹，否则鼠标一动就闪）。</summary>
        private void ShowTip(string key, string text, Rectangle anchor)
        {
            if (string.Equals(key, tipKey, StringComparison.Ordinal)) return;
            tipKey = key;
            if (key == null) { tips.Hide(this); return; }
            if (anchor.Right > Width) anchor.X = Math.Max(0, Width - anchor.Width - Px(40));
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

            // ---- 悬停提示：一个「目标 key」驱动，标签 / 关闭 / 各按钮都走这一条 ----
            string key = null, text = null;
            Rectangle anchor = Rectangle.Empty;
            if (ht >= 0)
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
                    string p = tabs[idx].Path;
                    // 标签上名字被省略号截了也能看全；顺带把完整路径也带上
                    text = string.IsNullOrEmpty(p) ? tabs[idx].Title
                         : tabs[idx].Title + "\r\n" + p;
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
            hoverIndex = -1; hoverCloseIndex = -1; hoverNew = false; hoverTool = -1;
            ShowTip(null, null, Rectangle.Empty);
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            ShowTip(null, null, Rectangle.Empty);

            if (e.Button == MouseButtons.Right)
            {
                int ri = HitTest(e.Location);
                if (ri >= 0)
                {
                    if (TabRightClicked != null) TabRightClicked(this, ri);
                }
                else if (ToolAt(e.Location) < 0 && !NewButtonBounds().Contains(e.Location))
                {
                    // 空白区域（不是标签、不是按钮）
                    if (BlankRightClicked != null) BlankRightClicked(e.Location);
                }
                return;
            }

            if (e.Button == MouseButtons.Left)
            {
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
            if (idx < 0) return;

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
            if (dragFromIndex >= 0 && dragOverIndex >= 0 && dragFromIndex != dragOverIndex)
            {
                int from = dragFromIndex, to = dragOverIndex;
                MoveTab(from, to);
                if (OrderChanged != null) OrderChanged(this, from);
            }
            dragFromIndex = -1; dragOverIndex = -1;
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (tips != null) tips.Dispose();
                if (titleFont != null) titleFont.Dispose();
                if (pathFont != null) pathFont.Dispose();
                if (glyphFont != null) glyphFont.Dispose();
            }
            base.Dispose(disposing);
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            base.OnDoubleClick(e);
            Point p = PointToClient(Cursor.Position);
            if (HitTest(p) < 0 && !NewButtonBounds().Contains(p) && ToolAt(p) < 0)
            {
                if (NewTabClicked != null) NewTabClicked(this, EventArgs.Empty);
            }
        }
    }
}
