using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace TabbedExplorer
{
    /// <summary>
    /// 历史记录管理器（川 2026-09-22 要的：「打开历史记录文件」改成「打开历史记录管理器」，
    /// 界面模仿书签管理器）。
    ///
    /// 版式跟 `FavManagerForm` 一套（同一个程序、同一套 Theme 调色板、同一套自绘行），
    /// 左边那条表 = 去过的地方（新的在前），右边 = 选中那一条的详情 + 能干什么。
    /// 上面一样是「搜索框 + 一排动作按钮」。
    ///
    /// 行为：
    ///   · 点一行 = 选中；双击 = 在当前窗口开成新标签（跟从历史菜单里点一条一样）
    ///   · 行尾的 ✕ / 右键 / 顶上的按钮 = 从历史里删掉这一条
    ///   · 顶上的搜索框跨全表过滤（路径或名字里带这个词的都留下）
    ///   · 拖文件夹进来？不需要 —— 历史是**自动记**的，没有「添加」这个动作
    ///
    /// ⚠ 所有动作都只动 `data\history.json`，**磁盘上一个字节都不动**（删记录 ≠ 删文件夹）。
    /// </summary>
    internal sealed class HistoryManagerForm : Form
    {
        /// <summary>双击 / 「打开」一条 —— 交给主窗口开成新标签。</summary>
        public event Action<string> OpenPath;

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

        // 跟 FavManagerForm 一样：全部走 Px()，并给小标题留出 CaptionH（不然标题和第一行会字压字）。
        private static int TopH { get { return Px(40); } }
        private static int LeftW { get { return Px(320); } }
        private static int ListRowH { get { return Px(30); } }
        private static int CaptionH { get { return Px(26); } }

        private List<string> view = new List<string>();
        private readonly List<Rectangle> rects = new List<Rectangle>();

        private int sel = -1;
        private int hover = -1;
        private bool hoverClose;
        private string filter = "";

        private readonly TextBox search;
        private readonly Font font, fontDim;

        public HistoryManagerForm()
        {
            Text = "历史记录";
            Icon = ShellIcon.AppIcon(false);
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(Px(920), Px(560));
            MinimumSize = new Size(Px(600), Px(360));
            BackColor = Theme.Chrome;
            ForeColor = Theme.Text;
            font = new Font("Segoe UI", Px(12), FontStyle.Regular, GraphicsUnit.Pixel);
            fontDim = new Font("Segoe UI", Px(11), FontStyle.Regular, GraphicsUnit.Pixel);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            int x = Px(10), y = Px(8);
            search = new TextBox();
            search.BorderStyle = BorderStyle.FixedSingle;
            search.BackColor = Theme.InputBack;
            search.ForeColor = Theme.Text;
            search.Font = font;
            search.SetBounds(x, y + Px(1), Px(220), Px(24));
            search.TextChanged += delegate { filter = search.Text.Trim(); Rebuild(); Invalidate(); };
            Controls.Add(search);

            int bx = x + Px(232);
            bx = AddButton("打开", bx, y, delegate { OpenSelected(); });
            bx = AddButton("复制完整路径", bx, y, delegate
            {
                if (sel < 0 || sel >= view.Count) return;
                try { Clipboard.SetText(view[sel]); Toast.Show("已复制", view[sel]); } catch { }
            });
            bx = AddButton("删除这一条", bx, y, delegate { DeleteSelected(); });
            bx = AddButton("打开 json", bx, y, delegate
            {
                try { Process.Start(History.FileName); }
                catch (Exception ex) { Toast.Show("打不开", ex.Message); }
            });
            bx = AddButton("清空历史", bx, y, delegate
            {
                History.Clear();
                sel = -1;
                Rebuild();
                Invalidate();
                Toast.Show("历史记录", "已清空（只清记录，磁盘上的文件夹没动）。");
            });

            Theme.Changed += OnTheme;
            FormClosed += delegate { try { Theme.Changed -= OnTheme; } catch { } };
        }

        private void OnTheme(object sender, EventArgs e)
        {
            BackColor = Theme.Chrome;
            ForeColor = Theme.Text;
            if (search != null) { search.BackColor = Theme.InputBack; search.ForeColor = Theme.Text; }
            Invalidate(true);
        }

        private int AddButton(string text, int x, int y, Action a)
        {
            Button b = new Button();
            b.Text = text;
            b.FlatStyle = FlatStyle.Flat;
            b.BackColor = Theme.Hover;
            b.ForeColor = Theme.Text;
            b.Font = fontDim;
            b.FlatAppearance.BorderColor = Theme.Border;
            b.UseVisualStyleBackColor = false;
            Size sz = TextRenderer.MeasureText(text, fontDim, new Size(Px(400), Px(24)),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            int w = sz.Width + Px(18);
            b.SetBounds(x, y, w, Px(26));
            b.Click += delegate { try { a(); } catch (Exception ex) { Diag.Log("历史管理器: " + ex.Message); } };
            Controls.Add(b);
            return x + w + Px(6);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.ApplyTitleBar(Handle);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Theme.ApplyTitleBar(Handle);
            Rebuild();
            Invalidate();
        }

        // ==================================================================
        // 排版
        // ==================================================================

        private void Rebuild()
        {
            List<string> all = History.Recent;
            view = new List<string>();
            for (int i = 0; i < all.Count; i++)
            {
                if (filter.Length == 0 ||
                    all[i].IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                    view.Add(all[i]);
            }

            rects.Clear();
            int y = TopH + CaptionH;
            for (int i = 0; i < view.Count; i++)
            {
                rects.Add(new Rectangle(Px(8), y, Math.Max(1, LeftW - Px(18)), ListRowH));
                y += ListRowH;
            }

            if (sel >= view.Count) sel = view.Count - 1;
            if (sel < 0 && view.Count > 0) sel = 0;
        }

        private string Current { get { return (sel >= 0 && sel < view.Count) ? view[sel] : null; } }

        // ==================================================================
        // 画
        // ==================================================================

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.FillRectangle(new SolidBrush(Theme.Chrome), ClientRectangle);

            using (SolidBrush b = new SolidBrush(Theme.TabBar))
                g.FillRectangle(b, 0, 0, Width, TopH);
            using (Pen p = new Pen(Theme.Border))
            {
                g.DrawLine(p, 0, TopH, Width, TopH);
                g.DrawLine(p, LeftW, TopH, LeftW, Height);
            }

            DrawCaption(g, filter.Length > 0
                ? ("历史记录（筛出 " + view.Count + " 条）")
                : ("历史记录（" + view.Count + " 条）"), Px(12), TopH + Px(4));
            DrawCaption(g, "详情", LeftW + Px(12), TopH + Px(4), true);

            for (int i = 0; i < rects.Count && i < view.Count; i++) DrawRow(g, i);

            if (view.Count == 0)
                TextRenderer.DrawText(g, filter.Length > 0 ? "没有匹配的记录" : "还没有历史记录（去几个文件夹就自动记上了）",
                    fontDim, new Rectangle(Px(14), TopH + CaptionH + Px(6), Math.Max(1, LeftW - Px(30)), Px(24)),
                    Theme.TextDim, TextFormatFlags.Left | TextFormatFlags.NoPadding);

            DrawDetail(g);
        }

        private void DrawCaption(Graphics g, string text, int x, int y) { DrawCaption(g, text, x, y, false); }

        private void DrawCaption(Graphics g, string text, int x, int y, bool dim)
        {
            TextRenderer.DrawText(g, text, fontDim, new Point(x, y), dim ? Theme.TextDim : Theme.Text,
                TextFormatFlags.NoPadding);
        }

        private void DrawRow(Graphics g, int i)
        {
            string p = view[i];
            Rectangle r = rects[i];

            if (i == sel) g.FillRectangle(new SolidBrush(Theme.Hover), r);
            else if (i == hover) g.FillRectangle(new SolidBrush(Theme.Hover), r);

            // 行尾的 ✕
            Rectangle close = CloseRect(r);
            if (hoverClose && i == hover)
            {
                using (Pen pen = new Pen(Theme.TextDim, Math.Max(1f, DpiScale)))
                {
                    int s = Px(4);
                    g.DrawLine(pen, close.Left + close.Width / 2 - s, close.Top + close.Height / 2 - s,
                                    close.Left + close.Width / 2 + s, close.Top + close.Height / 2 + s);
                    g.DrawLine(pen, close.Left + close.Width / 2 + s, close.Top + close.Height / 2 - s,
                                    close.Left + close.Width / 2 - s, close.Top + close.Height / 2 + s);
                }
            }

            int x = r.Left + Px(4);
            Image ic = IconFor(p);
            if (ic != null)
            {
                g.DrawImage(ic, new Rectangle(x, r.Top + (r.Height - Px(16)) / 2, Px(16), Px(16)));
                x += Px(20);
            }

            int rightRoom = close.Width + Px(10);
            int tw = r.Right - x - rightRoom;
            if (tw > 0)
                TextRenderer.DrawText(g, p, font, new Rectangle(x, r.Top, tw, r.Height),
                    i == sel ? Theme.Text : Theme.TextDim,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        /// <summary>右边的详情：这一条到底在哪儿、还开不开得了。</summary>
        private void DrawDetail(Graphics g)
        {
            string p = Current;
            int x = LeftW + Px(14);
            int w = Math.Max(1, Width - x - Px(20));
            int y = TopH + CaptionH;

            if (p == null)
            {
                TextRenderer.DrawText(g, "左边选一条，这里显示它的完整路径。",
                    fontDim, new Point(x, y), Theme.TextDim, TextFormatFlags.NoPadding);
                return;
            }

            Line(g, "完整路径：", p, x, ref y, w);
            Line(g, "类型：", KindOf(p), x, ref y, w);
            Line(g, "在历史里的位置：", ("第 " + (sel + 1) + " 条，共 " + view.Count + " 条（越靠前越近）"),
                x, ref y, w);

            y += Px(6);
            TextRenderer.DrawText(g, "双击左边一行 = 在当前窗口开成新标签。",
                fontDim, new Point(x, y), Theme.TextDim, TextFormatFlags.NoPadding);
        }

        /// <summary>画一行「标签：值」，值长了会折行；画完把 y 推到下一行。</summary>
        private void Line(Graphics g, string label, string value, int x, ref int y, int w)
        {
            TextRenderer.DrawText(g, label, fontDim, new Point(x, y), Theme.TextDim, TextFormatFlags.NoPadding);
            int lw = TextRenderer.MeasureText(label, fontDim, new Size(Px(400), Px(20)),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;

            Rectangle r = new Rectangle(x + lw + Px(4), y - Px(2), Math.Max(Px(60), w - lw - Px(4)), Px(400));
            TextRenderer.DrawText(g, value, font, r, Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);

            int h = TextRenderer.MeasureText(g, value, font, new Size(r.Width, Px(400)),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;
            y += Math.Max(Px(24), h + Px(10));
        }

        private static string KindOf(string p)
        {
            if (string.IsNullOrEmpty(p)) return "";
            if (p.StartsWith("::", StringComparison.Ordinal) || p.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
                return "系统位置（此电脑 / 库这类）";
            try
            {
                if (Directory.Exists(p)) return "文件夹";
                if (File.Exists(p)) return "文件";
            }
            catch { }
            return "已经不存在了（点了会打不开）";
        }

        private static Image IconFor(string p)
        {
            try
            {
                Image ic = ShellIcon.PathIcon(p, Px(16));
                if (ic != null) return ic;
            }
            catch { }
            return ShellIcon.FolderIcon(Px(16));
        }

        private static Rectangle CloseRect(Rectangle row)
        {
            int s = Px(20);
            return new Rectangle(row.Right - s - Px(4), row.Top + (row.Height - s) / 2, s, s);
        }

        // ==================================================================
        // 鼠标
        // ==================================================================

        private int RowAt(Point p)
        {
            for (int i = 0; i < rects.Count; i++) if (rects[i].Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int i = RowAt(e.Location);
            bool hc = (i >= 0) && CloseRect(rects[i]).Contains(e.Location);
            if (i != hover || hc != hoverClose) { hover = i; hoverClose = hc; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hover = -1; hoverClose = false;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            int i = RowAt(e.Location);
            if (i < 0) return;
            if (CloseRect(rects[i]).Contains(e.Location)) { History.Remove(view[i]); Reload(); return; }
            sel = i;
            Rebuild();
            Invalidate();
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            // 双击行尾的 ✕ 不算「打开」（不然会连删两条）
            int i = RowAt(e.Location);
            if (i >= 0 && CloseRect(rects[i]).Contains(e.Location)) return;
            OpenSelected();
        }

        private void OpenSelected()
        {
            string p = Current;
            if (p == null) return;
            Diag.Step("历史管理器: 打开 " + p);
            if (OpenPath != null) OpenPath(p);
        }

        private void DeleteSelected()
        {
            string p = Current;
            if (p == null) return;
            History.Remove(p);
            Reload();
        }

        /// <summary>删完 / 清空之后刷一下（同时把 sel 收进合法范围）。</summary>
        private void Reload()
        {
            Rebuild();
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Right) return;
            int i = RowAt(e.Location);
            if (i < 0) return;
            sel = i;

            string p = view[i];
            List<PopItem> m = new List<PopItem>();
            m.Add(PopMenu.It("打开（新标签）", delegate { OpenSelected(); }));
            m.Add(PopMenu.It("复制完整路径", delegate
            {
                try { Clipboard.SetText(p); } catch { }
            }));
            m.Add(PopMenu.Split());
            m.Add(PopMenu.It("从历史里删掉这一条", delegate { History.Remove(p); Reload(); }));

            PopMenu.Show(m.ToArray(), this, e.Location, "历史管理器右键");
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (font != null) font.Dispose();
                if (fontDim != null) fontDim.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
