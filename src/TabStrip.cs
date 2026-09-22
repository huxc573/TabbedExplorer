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

        private int hoverIndex = -1;
        private int hoverCloseIndex = -1;
        private bool hoverNew;
        private int dragFromIndex = -1;
        private int dragOverIndex = -1;

        // 标签宽度：文件夹名一般不长，190 太奢侈（川：一半就够）。
        // 按宽度不够时自动压到 Min，再挤就继续缩（标题会省略号）。
        // 加文件夹图标后往上补了图标占的那一点（6+16+5 = 27），保证**文字可用宽度**不缩水。
        private int MinTabWidth { get { return Px(72); } }
        private int MaxTabWidth { get { return Px(112); } }
        private int NewButtonWidth { get { return Px(34); } }
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
        private void EnsureLayout()
        {
            bounds.Clear();
            int x = Px(2);
            int avail = Math.Max(Px(60), Width - NewButtonWidth - Px(4));
            int w = MaxTabWidth;
            if (tabs.Count > 0)
            {
                int need = tabs.Count * MaxTabWidth;
                if (need > avail) w = Math.Max(MinTabWidth, avail / tabs.Count);
            }
            // 标签铺满整条高度 —— 上下都不留白
            for (int i = 0; i < tabs.Count; i++)
            {
                bounds.Add(new Rectangle(x, 0, w, Height));
                x += w;
            }
        }

        private Rectangle NewButtonBounds()
        {
            return new Rectangle(Width - NewButtonWidth, 0, NewButtonWidth, Height);
        }

        private Rectangle CloseBounds(Rectangle tab)
        {
            int s = CloseBoxSize;
            return new Rectangle(tab.Right - CloseAreaWidth + (CloseAreaWidth - s) / 2,
                                 tab.Top + (tab.Height - s) / 2, s, s);
        }

        private int HitTest(Point p)
        {
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

            // “+” 新建
            Rectangle nb = NewButtonBounds();
            if (hoverNew) g.FillRectangle(new SolidBrush(Theme.Hover), nb);
            int mx = nb.Left + nb.Width / 2, my = nb.Top + nb.Height / 2;
            int arm = Math.Max(4, Px(5));
            Pen p2 = new Pen(Theme.TextDim, stroke * 1.6f);
            g.DrawLine(p2, mx - arm, my, mx + arm, my);
            g.DrawLine(p2, mx, my - arm, mx, my + arm);
            p2.Dispose();
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
            hoverIndex = -1; hoverCloseIndex = -1; hoverNew = false;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            int idx = HitTest(e.Location);

            if (NewButtonBounds().Contains(e.Location))
            {
                if (NewTabClicked != null) NewTabClicked(this, EventArgs.Empty);
                return;
            }
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

        protected override void OnDoubleClick(EventArgs e)
        {
            base.OnDoubleClick(e);
            Point p = PointToClient(Cursor.Position);
            if (HitTest(p) < 0 && !NewButtonBounds().Contains(p))
            {
                if (NewTabClicked != null) NewTabClicked(this, EventArgs.Empty);
            }
        }
    }
}
