using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace TabbedExplorer
{
    /// <summary>
    /// Win10 风格标签条：扁平、浅灰底、选中标签白底 + 顶部蓝线，右侧一个“+”新建按钮。
    ///
    /// 尺寸全部走 DpiScale：以前这里是硬编码（TabHeight=32 之类），150% DPI 下标签条本身
    /// 是 51px 而标签只画到 28px，下面白着 23px —— 就是「文件和此电脑中间空了太多」那一条。
    /// 现在标签按整条高度铺满，不再留白。
    /// </summary>
    internal sealed class TabStrip : Control
    {
        public sealed class TabItem
        {
            public string Title = "";
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

        /// <summary>标签条标准高度（逻辑像素）。外面布局也照这个数来。</summary>
        public const int StdHeight = 34;

        private readonly List<TabItem> tabs = new List<TabItem>();
        private readonly List<Rectangle> bounds = new List<Rectangle>();

        /// <summary>鼠标停在某个标签上时，显示**完整文件夹名**（标题被省略号截了也看得到）。</summary>
        private readonly ToolTip tips = new ToolTip();

        private int hoverIndex = -1;
        private int hoverCloseIndex = -1;
        private bool hoverNew;
        private bool hoverSettings;
        /// <summary>当前弹着提示的那个标签（换标签/离开才重弹，不然鼠标一动就闪）。</summary>
        private int tipIndex = -1;
        private int dragFromIndex = -1;
        private int dragOverIndex = -1;

        // 「+」和设置按钮的位置由 EnsureLayout 算出来（+ 得跟着标签跑），
        // 不能再像以前那样只用 Width 减一下 —— 那样它永远是钉在整条最右边的。
        private Rectangle newRect;
        private Rectangle settingsRect;

        // 标签宽度：文件夹名一般不长，190 太奢侈（川：一半就够）。
        // 按宽度不够时自动压到 Min，再挤就继续缩（标题会省略号）。
        // 加文件夹图标后往上补了图标占的那一点（6+16+5 = 27），保证**文字可用宽度**不缩水。
        private int MinTabWidth { get { return Math.Min(Px(72), MaxTabWidth); } }
        /// <summary>
        /// 标签宽度。跟着设置走（逻辑像素 ×DPI）—— 川说的「可设置标签页宽度」。
        /// 自适应开着时它当上限：挤不下就从它往下缩（下限见 MinTabWidth）。
        /// 自适应关掉时就固定这个宽度（标签靠左排，多出来的被设置按钮盖住）。
        /// </summary>
        private int MaxTabWidth { get { return Px(Settings.TabWidth); } }
        /// <summary>「+」新建：现在紧跟在最后一个标签右边（浏览器那样），所以窄一点。</summary>
        private int NewButtonWidth { get { return Px(28); } }
        /// <summary>最右边那枚固定不动的设置按钮。</summary>
        private int SettingsButtonWidth { get { return Px(34); } }
        private int CloseAreaWidth { get { return Px(22); } }
        private int CloseBoxSize { get { return Px(16); } }

        // 标签左边那颗图标：正常由上层从 explorer 窗口读（= 当前文件夹的实时图标）。
        // 这个通用文件夹只是「还没拿到」时的占位，以及取不到时的兼底。
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

        public delegate void IndexEventHandler(object sender, int index);

        public event IndexEventHandler TabClicked;
        public event IndexEventHandler TabCloseClicked;
        public event EventHandler NewTabClicked;
        /// <summary>最右边那枚设置按钮被点了（菜单由上层弹）。</summary>
        public event EventHandler SettingsClicked;
        public event IndexEventHandler TabMiddleClicked;
        public event IndexEventHandler OrderChanged;   // 拖拽排序后：原索引

        public TabStrip()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.Selectable, true);
            Height = Px(StdHeight);
            Font = new Font("Segoe UI", Px(12), FontStyle.Regular, GraphicsUnit.Pixel);
            BackColor = Theme.TabBar;
            AllowDrop = true;
            tips.InitialDelay = 350;    // 停一下再弹，别鼠标一扫过就满屏提示
            tips.ReshowDelay = 80;
            tips.AutoPopDelay = 8000;
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
        /// 排一次版：标签从左往右铺，「+」紧跟最后一个标签，设置按钮钉在整条最右边。
        /// 尺寸只由 Width + 标签数决定，所以鼠标事件里可以随手重算。
        /// </summary>
        private void EnsureLayout()
        {
            bounds.Clear();

            // 最右边固定一枚设置按钮 —— 谁都不许压过去（川：最右边固定放一个设置按钮）
            settingsRect = new Rectangle(Width - SettingsButtonWidth, 0, SettingsButtonWidth, Height);

            // 标签能用的宽度 = 整条 - 设置按钮 - 「+」- 起点/间隔那几px
            int avail = Math.Max(Px(60), Width - SettingsButtonWidth - NewButtonWidth - Px(8));
            int w = MaxTabWidth;
            // 自适应关掉时就固定宽度，不缩 —— 标签多于放得下的数量时，后面的会被设置按钮盖住。
            if (tabs.Count > 0 && Settings.TabAutoFit)
            {
                int need = tabs.Count * MaxTabWidth;
                if (need > avail) w = Math.Max(MinTabWidth, avail / tabs.Count);
            }
            // 标签铺满整条高度 —— 上下都不留白
            int x = Px(2);
            for (int i = 0; i < tabs.Count; i++)
            {
                bounds.Add(new Rectangle(x, 0, w, Height));
                x += w;
            }

            // 「+」紧跟在最后一个标签右边（浏览器就是这样）；一个标签都没有时贴左边。
            // 标签挤到极限时它会顶到设置按钮前面停住，不越界。
            int nx = (tabs.Count > 0) ? x + Px(4) : Px(4);
            int limit = settingsRect.Left - NewButtonWidth - Px(2);
            if (nx > limit) nx = Math.Max(Px(2), limit);
            newRect = new Rectangle(nx, 0, NewButtonWidth, Height);
        }

        private Rectangle NewButtonBounds()
        {
            EnsureLayout();
            return newRect;
        }

        private Rectangle SettingsBounds()
        {
            EnsureLayout();
            return settingsRect;
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
                    g.FillRectangle(new SolidBrush(Theme.Accent),
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

                int textLeft = tab.Left + Px(TextPadLeft) + isz + Px(IconGap);
                Rectangle textRect = new Rectangle(textLeft, tab.Top,
                                                   textRight - textLeft, tab.Height);
                if (textRect.Width < 0) textRect.Width = 0;

                TextRenderer.DrawText(g, tabs[i].Title, Font, textRect,
                    tabs[i].Active ? Theme.Text : Theme.TextDim,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

                if (showClose && roomForClose)
                {
                    bool hc = i == hoverCloseIndex;
                    if (hc) g.FillRectangle(new SolidBrush(Color.FromArgb(232, 17, 35)), close);
                    Color penColor = hc ? Color.White : Theme.TextDim;
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
            Pen p2 = new Pen(hoverNew ? Theme.Text : Theme.TextDim, stroke * 1.6f);
            g.DrawLine(p2, mx - arm, my, mx + arm, my);
            g.DrawLine(p2, mx, my - arm, mx, my + arm);
            p2.Dispose();

            // 最右边固定：设置按钮（左边一条竖线，跟标签区分开）
            Rectangle sbr = settingsRect;
            Color sBack = hoverSettings ? Theme.Hover : Theme.TabBar;
            if (hoverSettings) g.FillRectangle(new SolidBrush(sBack), sbr);
            g.DrawLine(new Pen(Theme.Border), sbr.Left, Px(6), sbr.Left, Height - Px(6));
            DrawGear(g, sbr, hoverSettings ? Theme.Text : Theme.TextDim, sBack, stroke);
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
            bool hs = SettingsBounds().Contains(e.Location);
            if (hs != hoverSettings) { hoverSettings = hs; changed = true; }

            // 停在标签上就报完整文件夹名（提示贴着标签下边出来）
            if (idx != tipIndex)
            {
                tipIndex = idx;
                if (idx >= 0 && idx < tabs.Count && idx < bounds.Count)
                {
                    Rectangle tb = bounds[idx];
                    tips.Show(tabs[idx].Title, this, tb.Left, tb.Bottom + Px(2), 8000);
                }
                else tips.Hide(this);
            }

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
            hoverIndex = -1; hoverCloseIndex = -1; hoverNew = false; hoverSettings = false;
            tipIndex = -1;
            tips.Hide(this);
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            tips.Hide(this);
            tipIndex = -1;
            if (e.Button == MouseButtons.Left && SettingsBounds().Contains(e.Location))
            {
                if (SettingsClicked != null) SettingsClicked(this, EventArgs.Empty);
                return;
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
            if (disposing && tips != null) tips.Dispose();
            base.Dispose(disposing);
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            base.OnDoubleClick(e);
            Point p = PointToClient(Cursor.Position);
            if (HitTest(p) < 0 && !NewButtonBounds().Contains(p) && !SettingsBounds().Contains(p))
            {
                if (NewTabClicked != null) NewTabClicked(this, EventArgs.Empty);
            }
        }
    }
}
