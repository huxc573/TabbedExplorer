using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace TabbedExplorer
{
    /// <summary>功能区的一个按钮。</summary>
    internal sealed class RibbonButton
    {
        public string Id;
        public string Text;
        public string Glyph;     // Segoe MDL2 Assets 里 30 个最稳的码位
        public string Shape;     // 手绘图标（Glyph 为空时用），避免缺字变方块
        public bool HasMenu;     // 右下角带小三角
        public bool Enabled = true;

        public RibbonButton(string id, string text, string glyph, string shape, bool hasMenu)
        {
            Id = id;
            Text = text;
            Glyph = glyph;
            Shape = shape;
            HasMenu = hasMenu;
        }
    }

    internal sealed class RibbonGroup
    {
        public string Title;
        public readonly List<RibbonButton> Buttons = new List<RibbonButton>();

        public RibbonGroup(string title) { Title = title; }

        public RibbonGroup Add(RibbonButton b) { Buttons.Add(b); return this; }
    }

    internal sealed class RibbonPage
    {
        public string Title;
        public readonly List<RibbonGroup> Groups = new List<RibbonGroup>();

        public RibbonPage(string title) { Title = title; }

        public RibbonPage Add(RibbonGroup g) { Groups.Add(g); return this; }
    }

    /// <summary>
    /// Win10 资源管理器风格的「功能区」：上面一排 文件/主页/共享/查看 选项卡，
    /// 下面一片命令按钮 + 分组标题。全部自绘，所以深浅色完全跟着 Theme 走。
    /// 「文件」是下拉菜单，不是常驻页。
    /// </summary>
    internal sealed class Ribbon : Control
    {
        public delegate void CommandHandler(object sender, string id, Point screenPoint);
        public event CommandHandler Command;

        // 硬编码的像素尺寸必须自己按 DPI 放大：字体是按 pt 走的、会随 DPI 自动变大，
        // 而 GDI+ 里的像素坐标不会。系统如果是 150%，不缩放就会把按钮文字挤掉。
        private static readonly float DpiScale = ReadSystemDpi();

        private static float ReadSystemDpi()
        {
            try
            {
                uint d = NativeMethods.GetDpiForSystem();
                if (d >= 72 && d <= 480) return d / 96f;
            }
            catch { }
            return 1f;
        }

        private static int Px(int v) { return (int)System.Math.Round(v * DpiScale); }

        private static readonly int TabRowH = Px(30);
        private static readonly int GroupTitleH = Px(18);
        private static readonly int BtnW = Px(64);
        private static readonly int BtnH = Px(62);
        private static readonly int FileTabW = Px(46);

        private readonly List<RibbonPage> pages = new List<RibbonPage>();
        private int activePage;

        private readonly List<Rectangle> tabRects = new List<Rectangle>();
        private readonly List<string> tabIds = new List<string>();
        private Rectangle fileRect;

        private readonly List<Rectangle> btnRects = new List<Rectangle>();
        private readonly List<RibbonButton> btnRefs = new List<RibbonButton>();

        private int hoverTab = -1;
        private int hoverBtn = -1;
        private bool downBtn;
        private readonly ToolTip tip = new ToolTip();

        private static readonly Font TabFont = new Font("Segoe UI", 9.5f);
        private static readonly Font BtnFont = new Font("Segoe UI", 8.25f);
        private static readonly Font GlyphFont = new Font("Segoe MDL2 Assets", 17f);
        private static readonly Font SmallGlyphFont = new Font("Segoe MDL2 Assets", 8f);
        private static readonly Font GroupFont = new Font("Segoe UI", 8.25f);

        public Ribbon()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = TabRowH + Px(8) + BtnH + GroupTitleH + Px(6);
            Font = new Font("Segoe UI", 9f);
        }

        public void SetPages(List<RibbonPage> list)
        {
            pages.Clear();
            pages.AddRange(list);
            activePage = pages.Count > 0 ? 0 : -1;
            Invalidate();
        }

        public int ActivePage { get { return activePage; } }

        // ==================================================================
        // 布局
        // ==================================================================
        private void Layout()
        {
            tabRects.Clear();
            tabIds.Clear();
            btnRects.Clear();
            btnRefs.Clear();

            int x = 0;
            fileRect = new Rectangle(x, 0, FileTabW, TabRowH);
            x += FileTabW;

            for (int i = 0; i < pages.Count; i++)
            {
                int w = TextRenderer.MeasureText(pages[i].Title, TabFont).Width + Px(26);
                tabRects.Add(new Rectangle(x, 0, w, TabRowH));
                tabIds.Add(pages[i].Title);
                x += w;
            }

            if (activePage < 0 || activePage >= pages.Count) return;

            RibbonPage p = pages[activePage];
            int cx = Px(6);
            int by = TabRowH + Px(5);
            for (int g = 0; g < p.Groups.Count; g++)
            {
                RibbonGroup grp = p.Groups[g];
                for (int b = 0; b < grp.Buttons.Count; b++)
                {
                    btnRects.Add(new Rectangle(cx, by, BtnW, BtnH));
                    btnRefs.Add(grp.Buttons[b]);
                    cx += BtnW;
                }
                cx += Px(10);
            }
        }

        private int HitTab(Point pt)
        {
            for (int i = 0; i < tabRects.Count; i++)
                if (tabRects[i].Contains(pt)) return i;
            return -1;
        }

        private int HitBtn(Point pt)
        {
            for (int i = 0; i < btnRects.Count; i++)
                if (btnRects[i].Contains(pt)) return i;
            return -1;
        }

        // ==================================================================
        // 绘制
        // ==================================================================
        protected override void OnPaint(PaintEventArgs e)
        {
            Layout();
            Graphics g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            // 命令区底
            using (SolidBrush b = new SolidBrush(Theme.RibbonGroup))
                g.FillRectangle(b, new Rectangle(0, TabRowH, Width, Height - TabRowH));
            // 选项卡条底
            using (SolidBrush b = new SolidBrush(Theme.TabBar))
                g.FillRectangle(b, new Rectangle(0, 0, Width, TabRowH));

            // 当前页的选项卡：底和命令区连成一片 + 顶部一条蓝线
            if (activePage >= 0 && activePage < tabRects.Count)
            {
                Rectangle tr = tabRects[activePage];
                using (SolidBrush b = new SolidBrush(Theme.RibbonGroup))
                    g.FillRectangle(b, tr);
                using (SolidBrush b = new SolidBrush(Theme.Accent))
                    g.FillRectangle(b, new Rectangle(tr.Left, 0, tr.Width, Px(2)));
            }

            // 文件
            if (hoverTab == -2)
            {
                using (SolidBrush b = new SolidBrush(Theme.Hover))
                    g.FillRectangle(b, fileRect);
            }
            DrawTabText(g, fileRect, "文件", false);

            for (int i = 0; i < tabRects.Count; i++)
            {
                bool act = i == activePage;
                if (!act && hoverTab == i)
                {
                    using (SolidBrush b = new SolidBrush(Theme.Hover))
                        g.FillRectangle(b, tabRects[i]);
                }
                DrawTabText(g, tabRects[i], pages[i].Title, act);
            }

            // 命令按钮
            if (activePage >= 0 && activePage < pages.Count)
                DrawPage(g, pages[activePage]);

            // 底部线
            using (Pen p = new Pen(Theme.Border))
                g.DrawLine(p, 0, Height - 1, Width, Height - 1);
        }

        private void DrawTabText(Graphics g, Rectangle r, string text, bool active)
        {
            if (string.IsNullOrEmpty(text)) return;
            Color c = active ? Theme.Text : Theme.Text;
            TextRenderer.DrawText(g, text, TabFont, r, c,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }

        private void DrawPage(Graphics g, RibbonPage page)
        {
            int idx = 0;
            int cx = Px(6);
            for (int gi = 0; gi < page.Groups.Count; gi++)
            {
                RibbonGroup grp = page.Groups[gi];
                for (int bi = 0; bi < grp.Buttons.Count; bi++)
                {
                    int i = idx++;
                    if (i >= btnRects.Count) break;
                    DrawButton(g, btnRects[i], btnRefs[i], i == hoverBtn, i == hoverBtn && downBtn);
                    cx = btnRects[i].Right;
                }
                cx += Px(10);
                // 组标题
                int gw = grp.Buttons.Count * BtnW;
                int gx = cx - Px(10) - gw;
                Rectangle gt = new Rectangle(gx, TabRowH + Px(5) + BtnH + Px(1), gw, GroupTitleH);
                TextRenderer.DrawText(g, grp.Title, GroupFont, gt, Theme.TextDim,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

                // 组分隔线
                if (gi < page.Groups.Count - 1)
                {
                    using (Pen p = new Pen(Theme.Border))
                        g.DrawLine(p, cx - Px(5), TabRowH + Px(8), cx - Px(5), Height - GroupTitleH - Px(3));
                }
            }
        }

        private void DrawButton(Graphics g, Rectangle r, RibbonButton b, bool hover, bool down)
        {
            if (hover)
            {
                Color fill = down ? Theme.Press : Theme.Hover;
                using (SolidBrush br = new SolidBrush(fill))
                    g.FillRectangle(br, r);
                using (Pen p = new Pen(Theme.Border))
                    g.DrawRectangle(p, r.Left, r.Top, r.Width - 1, r.Height - 1);
            }

            Color fg = b.Enabled ? Theme.Text : Theme.TextDim;
            Rectangle iconRect = new Rectangle(r.Left, r.Top + Px(8), r.Width, Px(24));

            if (!string.IsNullOrEmpty(b.Glyph))
            {
                TextRenderer.DrawText(g, b.Glyph, GlyphFont, iconRect, fg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPadding);
            }
            else if (!string.IsNullOrEmpty(b.Shape))
            {
                RoundedIcons.Draw(g, b.Shape, iconRect, fg);
            }

            if (b.HasMenu)
            {
                Rectangle cave = new Rectangle(r.Left + r.Width - Px(16), r.Top + Px(18), Px(12), Px(12));
                TextRenderer.DrawText(g, "\uE70D", SmallGlyphFont, cave, fg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPadding);
            }

            Rectangle textRect = new Rectangle(r.Left + Px(2), r.Top + Px(34), r.Width - Px(4), r.Height - Px(38));
            TextRenderer.DrawText(g, b.Text, BtnFont, textRect, fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top |
                TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        }

        // ==================================================================
        // 交互
        // ==================================================================
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int ht = fileRect.Contains(e.Location) ? -2 : HitTab(e.Location);
            int hb = HitBtn(e.Location);
            if (ht != hoverTab || hb != hoverBtn)
            {
                hoverTab = ht;
                hoverBtn = hb;
                if (hb >= 0 && hb < btnRefs.Count)
                {
                    string t = btnRefs[hb].Text;
                    tip.Show(t, this, e.X + 8, e.Y + 18, 2500);
                }
                else if (ht == -2) tip.Show("文件", this, e.X + 8, e.Y + 18, 2000);
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hoverTab = -1;
            hoverBtn = -1;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;

            if (fileRect.Contains(e.Location))
            {
                Fire("file.menu", e.Location);
                return;
            }
            int ht = HitTab(e.Location);
            if (ht >= 0)
            {
                if (ht != activePage)
                {
                    activePage = ht;
                    hoverBtn = -1;
                    Invalidate();
                }
                return;
            }
            int hb = HitBtn(e.Location);
            if (hb >= 0 && hb < btnRefs.Count)
            {
                downBtn = true;
                Invalidate();
                Fire(btnRefs[hb].Id, e.Location);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            downBtn = false;
            Invalidate();
        }

        private void Fire(string id, Point clientPt)
        {
            if (Command == null) return;
            Point screen = PointToScreen(clientPt);
            try { Command(this, id, screen); }
            catch (Exception ex) { Diag.Log("功能区命令异常(" + id + "): " + ex.Message); }
        }
    }

    /// <summary>
    /// 手绘的小图标。用矢量画而不是字体，免得某个码位在这台机器上缺字变成一个方块。
    /// </summary>
    internal static class RoundedIcons
    {
        public static void Draw(Graphics g, string shape, Rectangle r, Color fg)
        {
            int cx = r.Left + r.Width / 2;
            int cy = r.Top + r.Height / 2;
            using (SolidBrush b = new SolidBrush(fg))
            using (Pen p = new Pen(fg, 1.4f))
            {
                switch (shape)
                {
                    case "xl":
                        g.FillRectangle(b, cx - 9, cy - 9, 18, 18);
                        break;
                    case "lg":
                        g.FillRectangle(b, cx - 7, cy - 7, 14, 14);
                        break;
                    case "md":
                        g.FillRectangle(b, cx - 8, cy - 8, 7, 7);
                        g.FillRectangle(b, cx + 1, cy - 8, 7, 7);
                        g.FillRectangle(b, cx - 8, cy + 1, 7, 7);
                        g.FillRectangle(b, cx + 1, cy + 1, 7, 7);
                        break;
                    case "sm":
                        for (int i = 0; i < 4; i++)
                            g.FillRectangle(b, cx - 9 + i * 5, cy - 4, 3, 8);
                        break;
                    case "list":
                        for (int i = 0; i < 4; i++)
                        {
                            g.FillRectangle(b, cx - 9, cy - 8 + i * 5, 3, 3);
                            p.Width = 1.2f;
                            g.DrawLine(p, cx - 4, cy - 6 + i * 5, cx + 9, cy - 6 + i * 5);
                        }
                        break;
                    case "details":
                        g.FillRectangle(b, cx - 9, cy - 8, 18, 3);
                        for (int i = 0; i < 3; i++)
                            g.DrawLine(p, cx - 9, cy - 2 + i * 5, cx + 9, cy - 2 + i * 5);
                        break;
                    case "tiles":
                        g.FillRectangle(b, cx - 9, cy - 9, 11, 11);
                        g.FillRectangle(b, cx + 3, cy - 9, 6, 5);
                        g.FillRectangle(b, cx + 3, cy - 3, 6, 5);
                        g.FillRectangle(b, cx - 9, cy + 3, 18, 6);
                        break;
                    case "content":
                        for (int i = 0; i < 3; i++)
                        {
                            g.FillRectangle(b, cx - 9, cy - 8 + i * 6, 7, 4);
                            p.Width = 1.2f;
                            g.DrawLine(p, cx + 1, cy - 6 + i * 6, cx + 9, cy - 6 + i * 6);
                        }
                        break;
                    case "navpane":
                        p.Width = 1.4f;
                        g.DrawRectangle(p, cx - 10, cy - 8, 20, 16);
                        g.DrawLine(p, cx - 4, cy - 8, cx - 4, cy + 8);
                        break;
                    case "checkbox":
                        p.Width = 1.4f;
                        g.DrawRectangle(p, cx - 8, cy - 8, 16, 16);
                        p.Width = 2f;
                        g.DrawLine(p, cx - 5, cy, cx - 1, cy + 5);
                        g.DrawLine(p, cx - 1, cy + 5, cx + 6, cy - 5);
                        break;
                    case "ext":
                        p.Width = 1.4f;
                        g.DrawRectangle(p, cx - 7, cy - 9, 13, 18);
                        g.FillRectangle(b, cx - 4, cy + 1, 3, 3);
                        g.FillRectangle(b, cx + 1, cy + 1, 3, 3);
                        g.DrawLine(p, cx - 4, cy - 4, cx + 4, cy - 4);
                        break;
                    case "hidden":
                        p.Width = 1.4f;
                        g.DrawRectangle(p, cx - 9, cy - 6, 18, 12);
                        g.FillRectangle(b, cx - 3, cy - 3, 6, 6);
                        g.DrawLine(p, cx - 10, cy + 8, cx + 10, cy - 8);
                        break;
                    case "sort":
                        for (int i = 0; i < 3; i++)
                        {
                            p.Width = 1.4f;
                            g.DrawLine(p, cx - 9, cy - 6 + i * 6, cx + 3 - i * 3, cy - 6 + i * 6);
                        }
                        break;
                    default:
                        p.Width = 1.4f;
                        g.DrawRectangle(p, cx - 8, cy - 8, 16, 16);
                        break;
                }
            }
        }
    }
}
