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
    /// 左边那条表 = 去过的地方（新的在前，**按日期分堆**），右边 = 选中那一条的详情 + 能干什么。
    /// 上面一样是「搜索框 + 一排动作按钮」。
    ///
    /// 行为：
    ///   · 点一行 = 选中；双击 = 在当前窗口开成新标签（跟从历史菜单里点一条一样）
    ///   · 行尾的 ✕ / 右键 / 顶上的按钮 = 从历史里删掉这一条
    ///   · 顶上的搜索框跨全表过滤（路径里带这个词的都留下）
    ///   · 拖文件夹进来？不需要 —— 历史是**自动记**的，没有「添加」这个动作
    ///
    /// ⚠ 所有动作都只动 `data\history.json`，**磁盘上一个字节都不动**（删记录 ≠ 删文件夹）。
    /// </summary>
    internal sealed class HistoryManagerForm : Form
    {
        /// <summary>双击 / 「打开」一条 —— 交给主窗口开成新标签。</summary>
        public event Action<string> OpenPath;

        /// <summary>一行 = 一条记录，或者一条日期分堆标题（`Item == null`）。</summary>
        private sealed class Row
        {
            public HistoryItem Item;
            public string Head;
            public int Index = -1;        // 在 view 里的下标（标题行是 -1）
            public Rectangle Rect;
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

        // 跟 FavManagerForm 一样：全部走 Px()，并给小标题留出 CaptionH（不然标题和第一行会字压字）。
        // 2026-09-22 第二轮：把逻辑尺寸调紧一档（行 30->24、窗口 920x560 -> 860x500），
        // 免得 150% 屏上整张窗口过于空旷（川：「自有窗口好像也变长了」）。
        private static int TopH { get { return Px(34); } }
        private static int LeftW { get { return Px(300); } }
        private static int ListRowH { get { return Px(24); } }
        private static int HeadRowH { get { return Px(22); } }
        private static int CaptionH { get { return Px(22); } }

        private List<HistoryItem> view = new List<HistoryItem>();
        private readonly List<Row> rows = new List<Row>();

        private int sel = -1;                 // 选中第几条（**view 的下标**，不是行号）
        private int hover = -1;               // 悬停那一行的下标（rows 的下标）
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
            ClientSize = new Size(Px(860), Px(500));
            MinimumSize = new Size(Px(560), Px(330));
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
            search.SetBounds(x, y + Px(1), Px(220), Px(22));
            search.TextChanged += delegate { filter = search.Text.Trim(); Rebuild(); Invalidate(); };
            Controls.Add(search);

            int bx = x + Px(232);
            bx = AddButton("打开", bx, y, delegate { OpenSelected(); });
            bx = AddButton("复制完整路径", bx, y, delegate
            {
                string p = Current;
                if (p == null) return;
                try { Clipboard.SetText(p); Toast.Show("已复制", p); } catch { }
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
            b.SetBounds(x, y, w, Px(24));
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
            List<HistoryItem> all = History.Recent;
            view = new List<HistoryItem>();
            for (int i = 0; i < all.Count; i++)
            {
                if (filter.Length == 0 ||
                    (all[i].Path != null &&
                     all[i].Path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0))
                    view.Add(all[i]);
            }

            rows.Clear();
            int y = TopH + CaptionH;
            string lastDay = null;
            for (int i = 0; i < view.Count; i++)
            {
                // 川 2026-09-22：按日期归类 —— 换了一天就插一条灰标题
                string day = History.DayLabel(view[i].At);
                if (day != lastDay)
                {
                    Row hr = new Row();
                    hr.Head = day;
                    hr.Rect = new Rectangle(Px(8), y, Math.Max(1, LeftW - Px(18)), HeadRowH);
                    rows.Add(hr);
                    y += HeadRowH;
                    lastDay = day;
                }
                Row r = new Row();
                r.Item = view[i];
                r.Index = i;
                r.Rect = new Rectangle(Px(16), y, Math.Max(1, LeftW - Px(26)), ListRowH);
                rows.Add(r);
                y += ListRowH;
            }

            if (sel >= view.Count) sel = view.Count - 1;
            if (sel < 0 && view.Count > 0) sel = 0;
        }

        private string Current
        {
            get { return (sel >= 0 && sel < view.Count) ? view[sel].Path : null; }
        }

        private HistoryItem CurrentItem
        {
            get { return (sel >= 0 && sel < view.Count) ? view[sel] : null; }
        }

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
                : ("历史记录（" + view.Count + " 条）"), Px(12), TopH + Px(3));
            DrawCaption(g, "详情", LeftW + Px(12), TopH + Px(3), true);

            for (int i = 0; i < rows.Count; i++) DrawRow(g, i);

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
            Row row = rows[i];
            Rectangle r = row.Rect;

            // ---- 日期分堆标题：一条灰字，不参与选中 ----
            if (row.Item == null)
            {
                TextRenderer.DrawText(g, row.Head, fontDim, new Rectangle(r.Left, r.Top, r.Width, r.Height),
                    Theme.TextDim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                return;
            }

            if (row.Index == sel) g.FillRectangle(new SolidBrush(Theme.Hover), r);
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

            string p = row.Item.Path;
            int x = r.Left + Px(4);
            Image ic = IconFor(p);
            if (ic != null)
            {
                g.DrawImage(ic, new Rectangle(x, r.Top + (r.Height - Px(15)) / 2, Px(15), Px(15)));
                x += Px(19);
            }

            // 末尾：时间 + ✕ 占的位置
            string tm = History.TimeOf(row.Item.At);
            int rightRoom = close.Width + Px(10);
            int timeW = 0;
            if (tm.Length > 0)
            {
                timeW = TextRenderer.MeasureText(tm, fontDim, new Size(Px(200), Px(18)),
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + Px(8);
                TextRenderer.DrawText(g, tm, fontDim,
                    new Rectangle(r.Right - rightRoom - timeW, r.Top, timeW, r.Height),
                    Theme.TextDim, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }

            int tw = r.Right - x - rightRoom - timeW;
            if (tw > 0)
                TextRenderer.DrawText(g, p, font, new Rectangle(x, r.Top, tw, r.Height),
                    row.Index == sel ? Theme.Text : Theme.TextDim,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        /// <summary>右边的详情：这一条到底在哪儿、什么时候去的、还开不开得了。</summary>
        private void DrawDetail(Graphics g)
        {
            HistoryItem it = CurrentItem;
            int x = LeftW + Px(14);
            int w = Math.Max(1, Width - x - Px(20));
            int y = TopH + CaptionH;

            if (it == null)
            {
                TextRenderer.DrawText(g, "左边选一条，这里显示它的完整路径。",
                    fontDim, new Point(x, y), Theme.TextDim, TextFormatFlags.NoPadding);
                return;
            }

            Line(g, "完整路径：", it.Path, x, ref y, w);
            Line(g, "类型：", KindOf(it.Path), x, ref y, w);
            Line(g, "去过时间：", string.IsNullOrEmpty(it.At) ? "（老记录，时间未知）" : it.At, x, ref y, w);
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
            y += Math.Max(Px(22), h + Px(10));
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
                Image ic = ShellIcon.PathIcon(p, Px(15));
                if (ic != null) return ic;
            }
            catch { }
            return ShellIcon.FolderIcon(Px(15));
        }

        private static Rectangle CloseRect(Rectangle row)
        {
            int s = Px(18);
            return new Rectangle(row.Right - s - Px(4), row.Top + (row.Height - s) / 2, s, s);
        }

        // ==================================================================
        // 鼠标
        // ==================================================================

        /// <summary>哪个**行号**（rows 的下标）在这个点上；-1 = 不在任何行上。</summary>
        private int RowAt(Point p)
        {
            for (int i = 0; i < rows.Count; i++) if (rows[i].Rect.Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int i = RowAt(e.Location);
            bool hc = (i >= 0) && rows[i].Item != null && CloseRect(rows[i].Rect).Contains(e.Location);
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
            if (i < 0 || rows[i].Item == null) return;      // 日期标题行点了没反应
            if (CloseRect(rows[i].Rect).Contains(e.Location)) { History.Remove(rows[i].Item.Path); Reload(); return; }
            sel = rows[i].Index;
            Invalidate();
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            // 双击行尾的 ✕ 不算「打开」（不然会连删两条）
            int i = RowAt(e.Location);
            if (i >= 0 && rows[i].Item != null && CloseRect(rows[i].Rect).Contains(e.Location)) return;
            if (i >= 0 && rows[i].Item != null) { sel = rows[i].Index; Invalidate(); }
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
            if (i < 0 || rows[i].Item == null) return;
            sel = rows[i].Index;

            string p = rows[i].Item.Path;
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
