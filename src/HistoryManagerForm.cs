using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace TabbedExplorer
{
    /// <summary>
    /// 历史记录管理器（用户要的：「打开历史记录文件」改成「打开历史记录管理器」，
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
            /// <summary>标题行专用：这一堆被收起了（收起后只留标题，不列条目）。</summary>
            public bool Collapsed;
            /// <summary>标题行专用：这一堆有几条（收起时也让人知道里面有多少）。</summary>
            public int Count;
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
        //第二轮：把逻辑尺寸调紧一档（行 30->24、窗口 920x560 -> 860x500），
        // 免得 150% 屏上整张窗口过于空旷（用户：「自有窗口好像也变长了」）。
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

        // ---- 滚动 + 日期分堆收起（用户：「管理器左侧没有滚动条」「历史不能按日期展开、收缩」）----
        /// <summary>内容往上滚了多少像素（0 = 顶部）。行坐标按「内容坐标」存，画/命中时才加偏移。</summary>
        private int scroll;
        /// <summary>整块内容（含分堆标题）有多高 —— 用来决定要不要出滚动条。</summary>
        private int contentH;
        /// <summary>被收起的日期分堆（存标题文字）。</summary>
        private readonly List<string> collapsed = new List<string>();
        private VScrollBar vbar;

        private readonly TextBox search;
        private readonly Font font, fontDim;

        // ---- 多选（用户：「管理器没有多选功能」）----
        // 跟资源管理器一套做法：点 = 只选它；Ctrl+点 = 把它加进去 / 拿出去；
        // Shift+点 = 从上次点的那条到这条一整段；Ctrl+A = 全选。
        // 存的是**路径**而不是下标 —— `view` 是过滤后的子集，一改搜索词下标全变了，
        // 存路径才能在重排 / 重新过滤之后还对得上（比路径统一走 `PathRules.Same`）。
        private readonly List<string> multi = new List<string>();
        /// <summary>Shift 选范围的锚点（上次普通/Ctrl 点的那条）。</summary>
        private string anchorPath;

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

            // 左边那条列表是**自绘**的（不是 ListBox），系统不会替我们出滚动条 —— 自己挂一个。
            // 颜色跟着颜色模式走：原生滚动条属非客户区、不吃自绘配色，得走 Theme.StyleScrollBar。
            vbar = new VScrollBar();
            vbar.Visible = false;
            vbar.Scroll += delegate
            {
                if (scroll == vbar.Value) return;
                scroll = vbar.Value;
                Invalidate();
            };
            Controls.Add(vbar);

            int bx = x + Px(232);
            bx = AddButton("打开", bx, y, delegate { OpenSelected(); });
            bx = AddButton("复制完整路径", bx, y, delegate
            {
                List<string> ps = TargetPaths();
                if (ps.Count == 0) return;
                try
                {
                    Clipboard.SetText(string.Join("\r\n", ps.ToArray()));
                    // 复制是看不见的操作（不在页面上留任何痕迹），所以这一条**保留**右下角提示。
                    Toast.Show(ps.Count == 1 ? "已复制" : ("已复制 " + ps.Count + " 条"),
                               ps.Count == 1 ? ps[0] : "路径已按行拼好，直接粘就行。");
                }
                catch { }
            });
            bx = AddButton("删除所选", bx, y, delegate { DeleteSelected(); });
            bx = AddButton("打开 json", bx, y, delegate
            {
                try { Process.Start(History.FileName); }
                catch (Exception ex) { Toast.Show("打不开", ex.Message); }
            });
            bx = AddButton("清空历史", bx, y, delegate
            {
                History.Clear();
                sel = -1;
                multi.Clear();
                Rebuild();
                Invalidate();
                // 用户：「像已加入书签这种页面直接有反馈的，也不用右下角通知」——
                // 整张表当场就空了，这就是反馈，不再弹气泡。
            });

            Theme.Changed += OnTheme;
            FormClosed += delegate { try { Theme.Changed -= OnTheme; } catch { } };
        }

        private void OnTheme(object sender, EventArgs e)
        {
            BackColor = Theme.Chrome;
            ForeColor = Theme.Text;
            if (search != null) { search.BackColor = Theme.InputBack; search.ForeColor = Theme.Text; }
            if (vbar != null && vbar.IsHandleCreated) Theme.StyleScrollBar(vbar.Handle);
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
            if (vbar != null && vbar.IsHandleCreated) Theme.StyleScrollBar(vbar.Handle);
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

            // 右边给滚动条留出宽度 —— 不然行尾的 ✕ / 时间会被压到滚动条底下。
            int avail = Math.Max(1, LeftW - ScrollBarW);

            // 行坐标一律用**内容坐标**（y 从 0 起）；真正画/命中时再加 TopH+CaptionH 并减 scroll。
            rows.Clear();
            int y = 0;
            string lastDay = null;
            bool skip = false;
            for (int i = 0; i < view.Count; i++)
            {
                // 用户：按日期归类 —— 换了一天就插一条灰标题；标题可点，收起后这堆的条目就不列了
                string day = History.DayLabel(view[i].At);
                if (day != lastDay)
                {
                    Row hr = new Row();
                    hr.Head = day;
                    hr.Collapsed = IsCollapsed(day);
                    hr.Count = CountOfDay(i, day);
                    hr.Rect = new Rectangle(Px(8), y, Math.Max(1, avail - Px(10)), HeadRowH);
                    rows.Add(hr);
                    y += HeadRowH;
                    lastDay = day;
                    skip = hr.Collapsed;
                }
                if (skip) continue;
                Row r = new Row();
                r.Item = view[i];
                r.Index = i;
                r.Rect = new Rectangle(Px(16), y, Math.Max(1, avail - Px(18)), ListRowH);
                rows.Add(r);
                y += ListRowH;
            }
            contentH = y + Px(6);

            if (sel >= view.Count) sel = view.Count - 1;
            if (sel < 0 && view.Count > 0) sel = 0;

            // 多选里的路径可能已经不在 view 里了（改了搜索词 / 历史自己变了）—— 把掉队的清掉，
            // 不然「已选 N 条」那个数字会越说越大，删的时候还删不到东西。
            PruneMulti();

            LayoutScrollBar();
        }

        /// <summary>view 里从第 i 条起、连着同一个日期分堆的一共几条。</summary>
        private int CountOfDay(int i, string day)
        {
            int n = 0;
            for (int k = i; k < view.Count; k++)
            {
                if (!string.Equals(History.DayLabel(view[k].At), day, StringComparison.Ordinal)) break;
                n++;
            }
            return n;
        }

        private bool IsCollapsed(string day)
        {
            for (int i = 0; i < collapsed.Count; i++)
                if (string.Equals(collapsed[i], day, StringComparison.Ordinal)) return true;
            return false;
        }

        private void ToggleCollapsed(string day)
        {
            for (int i = 0; i < collapsed.Count; i++)
            {
                if (!string.Equals(collapsed[i], day, StringComparison.Ordinal)) continue;
                collapsed.RemoveAt(i);
                Rebuild();
                Invalidate();
                return;
            }
            collapsed.Add(day);
            Rebuild();
            Invalidate();
        }

        private static int ScrollBarW { get { return SystemInformation.VerticalScrollBarWidth; } }

        /// <summary>左边那列列表在窗口里的矩形（滚动条、裁剪、滚轮都用它）。</summary>
        private Rectangle ListRect
        {
            get { return new Rectangle(0, TopH + CaptionH, LeftW, Math.Max(1, Height - TopH - CaptionH)); }
        }

        /// <summary>内容坐标 → 窗口坐标。</summary>
        private Rectangle RowRect(Row row)
        {
            return new Rectangle(row.Rect.X, row.Rect.Y + TopH + CaptionH - scroll,
                                 row.Rect.Width, row.Rect.Height);
        }

        /// <summary>内容比列表高就出滚动条；顺带把 scroll 夹回合法范围。</summary>
        private void LayoutScrollBar()
        {
            if (vbar == null) return;
            Rectangle lr = ListRect;
            if (contentH <= lr.Height) { vbar.Visible = false; scroll = 0; return; }
            if (!vbar.Visible) vbar.Visible = true;
            vbar.SetBounds(LeftW - ScrollBarW, lr.Top, ScrollBarW, lr.Height);

            int max = contentH - lr.Height;                 // 最多能滚多少像素
            if (scroll > max) scroll = max;
            if (scroll < 0) scroll = 0;

            // VScrollBar 的 Value 只能取到 Maximum - LargeChange + 1，所以 Maximum 得把它加回来。
            vbar.LargeChange = Math.Max(1, lr.Height);
            vbar.Maximum = max + vbar.LargeChange - 1;
            vbar.SmallChange = Math.Max(1, ListRowH);
            if (vbar.Value != scroll) vbar.Value = scroll;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutScrollBar();
            Invalidate();
        }

        /// <summary>滚轮滚列表（搜索框有焦点时事件会冒泡到这儿）。</summary>
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (vbar == null || !vbar.Visible) return;
            int step = SystemInformation.MouseWheelScrollLines * ListRowH;
            if (step <= 0) step = ListRowH * 3;
            int v = vbar.Value - (e.Delta * step / 120);
            int hi = vbar.Maximum - vbar.LargeChange + 1;
            if (v > hi) v = hi;
            if (v < vbar.Minimum) v = vbar.Minimum;
            vbar.Value = v;
        }

        // ==================================================================
        // 多选
        // ==================================================================

        private bool IsMulti(string p)
        {
            for (int i = 0; i < multi.Count; i++) if (PathRules.Same(multi[i], p)) return true;
            return false;
        }

        private void ToggleMulti(string p)
        {
            for (int i = 0; i < multi.Count; i++)
            {
                if (!PathRules.Same(multi[i], p)) continue;
                multi.RemoveAt(i);
                return;
            }
            multi.Add(p);
        }

        /// <summary>把从 anchorPath 到第 idx 条（按 view 的顺序）整段加进多选。</summary>
        private void AddRangeTo(int idx)
        {
            int from = -1;
            if (anchorPath != null)
            {
                for (int i = 0; i < view.Count; i++)
                {
                    if (PathRules.Same(view[i].Path, anchorPath)) { from = i; break; }
                }
            }
            if (from < 0) { from = idx; anchorPath = view[idx].Path; }
            int a = Math.Min(from, idx), b = Math.Max(from, idx);
            for (int i = a; i <= b && i < view.Count; i++)
                if (!IsMulti(view[i].Path)) multi.Add(view[i].Path);
        }

        /// <summary>整组选中（点日期分堆标题）。</summary>
        private void SelectDay(string day)
        {
            for (int i = 0; i < view.Count; i++)
            {
                if (!string.Equals(History.DayLabel(view[i].At), day, StringComparison.Ordinal)) continue;
                if (!IsMulti(view[i].Path)) multi.Add(view[i].Path);
            }
        }

        private void SelectAll()
        {
            for (int i = 0; i < view.Count; i++)
                if (!IsMulti(view[i].Path)) multi.Add(view[i].Path);
        }

        private void PruneMulti()
        {
            for (int i = multi.Count - 1; i >= 0; i--)
            {
                bool found = false;
                for (int k = 0; k < view.Count; k++)
                {
                    if (!PathRules.Same(view[k].Path, multi[i])) continue;
                    found = true; break;
                }
                if (!found) multi.RemoveAt(i);
            }
        }

        /// <summary>这次动作要作用于哪些条目 —— 多选非空就用多选，否则就是当前选中那一条。</summary>
        private List<string> TargetPaths()
        {
            List<string> r = new List<string>();
            if (multi.Count > 0) { r.AddRange(multi); return r; }
            string p = Current;
            if (p != null) r.Add(p);
            return r;
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
                : ("历史记录（" + view.Count + " 条）" + (multi.Count > 1 ? ("　·　已选 " + multi.Count + " 条") : "")),
                Px(12), TopH + Px(3));
            DrawCaption(g, "详情", LeftW + Px(12), TopH + Px(3), true);

            // 行只画在列表区里（内容超出时不要压到上面那条标题栏上），顺带把看不见的行跳掉。
            Rectangle list = ListRect;
            System.Drawing.Drawing2D.GraphicsState st = g.Save();
            g.SetClip(list, System.Drawing.Drawing2D.CombineMode.Intersect);
            for (int i = 0; i < rows.Count; i++)
            {
                Rectangle rr = RowRect(rows[i]);
                if (rr.Bottom < list.Top || rr.Top > list.Bottom) continue;
                DrawRow(g, i);
            }
            g.Restore(st);

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
            Rectangle r = RowRect(row);

            // ---- 日期分堆标题：灰字 + 一个 ▾/▸ 小三角（点它收起 / 展开这一堆）----
            if (row.Item == null)
            {
                int cy = r.Top + r.Height / 2;
                int s = Math.Max(2, Px(3));
                using (SolidBrush b = new SolidBrush(Theme.TextDim))
                {
                    Point[] tri = row.Collapsed
                        ? new Point[] { new Point(r.Left + Px(2), cy - s),
                                        new Point(r.Left + Px(2) + s, cy),
                                        new Point(r.Left + Px(2), cy + s) }
                        : new Point[] { new Point(r.Left + Px(1), cy - s),
                                        new Point(r.Left + Px(1) + s * 2, cy - s),
                                        new Point(r.Left + Px(1) + s, cy + s) };
                    g.FillPolygon(b, tri);
                }
                string head = row.Head + "（" + row.Count + " 条）";
                TextRenderer.DrawText(g, head, fontDim,
                    new Rectangle(r.Left + Px(14), r.Top, Math.Max(1, r.Width - Px(14)), r.Height),
                    Theme.TextDim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                return;
            }

            if (row.Index == sel) g.FillRectangle(new SolidBrush(Theme.Hover), r);
            else if (i == hover) g.FillRectangle(new SolidBrush(Theme.Hover), r);

            // 多选中的一行：左边加一条强调色短竖条（选中色跟 `sel` 一样用 Hover，
            // 靠这条竖条区分「刚点的那一个」和「一起被选上的那几个」）。
            if (IsMulti(row.Item.Path))
            {
                g.FillRectangle(new SolidBrush(Theme.Hover), r);
                using (SolidBrush b = new SolidBrush(Theme.Accent))
                    g.FillRectangle(b, new Rectangle(Px(10), r.Top + Px(3),
                        Math.Max(2, Px(3)), Math.Max(1, r.Height - Px(6))));
            }

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
            if (multi.Count > 1)
                Line(g, "已选：", multi.Count + " 条（删除 / 复制都会作用于这几条）", x, ref y, w);

            y += Px(6);
            TextRenderer.DrawText(g, "双击左边一行 = 在当前窗口开成新标签；Ctrl / Shift 点 = 多选。",
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
            if (!ListRect.Contains(p)) return -1;      // 点在滚动条 / 上方标题栏上不算命中
            for (int i = 0; i < rows.Count; i++)
                if (RowRect(rows[i]).Contains(p)) return i;
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
            if (i < 0) { if (multi.Count > 0) { multi.Clear(); Invalidate(); } return; }
            Row row = rows[i];

            // ---- 日期分堆标题：点一下 = 收起 / 展开这一堆（整组选中、整组删除在右键菜单里）----
            if (row.Item == null)
            {
                ToggleCollapsed(row.Head);
                return;
            }

            if (CloseRect(row.Rect).Contains(e.Location)) { History.Remove(row.Item.Path); Reload(); return; }

            // ---- 多选：Ctrl 加减、Shift 选一段、光点只选它自己 ----
            Keys mod = Control.ModifierKeys;
            if ((mod & Keys.Shift) != 0 && anchorPath != null)
            {
                AddRangeTo(row.Index);
            }
            else if ((mod & Keys.Control) != 0)
            {
                ToggleMulti(row.Item.Path);
                anchorPath = row.Item.Path;
            }
            else
            {
                multi.Clear();
                anchorPath = row.Item.Path;
            }
            sel = row.Index;
            Invalidate();
        }

        /// <summary>Ctrl+A 全选 / Esc 取消多选（列表里没焦点时才会到这儿，搜索框里的 Ctrl+A 归搜索框）。</summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.A))
            {
                SelectAll();
                Invalidate();
                return true;
            }
            if (keyData == Keys.Escape && multi.Count > 0)
            {
                multi.Clear();
                Invalidate();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
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
            List<string> ps = TargetPaths();
            if (ps.Count == 0) return;
            int gone = History.RemoveMany(ps);
            multi.Clear();
            if (gone > 0) sel = -1;
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
            Row row = rows[i];

            // ---- 日期分堆标题上右键 = 整组删（用户要的）----
            if (row.Item == null)
            {
                string day = row.Head;
                List<string> all = History.PathsOfDay(day);
                if (all.Count == 0) return;
                List<PopItem> hm = new List<PopItem>();
                bool col = IsCollapsed(day);
                hm.Add(PopMenu.It(col ? ("展开「" + day + "」") : ("收起「" + day + "」"),
                    delegate { ToggleCollapsed(day); }));
                hm.Add(PopMenu.It("选中「" + day + "」这一组（" + all.Count + " 条）",
                    delegate { SelectDay(day); anchorPath = null; Invalidate(); }));
                hm.Add(PopMenu.Split());
                hm.Add(PopMenu.It("删除「" + day + "」这一组（" + all.Count + " 条）", delegate
                {
                    int gone = History.RemoveMany(all);
                    multi.Clear();
                    sel = -1;
                    Reload();
                    Diag.Step("历史管理器: 按日期删掉「" + day + "」" + gone + " 条");
                }));
                PopMenu.Show(hm.ToArray(), this, e.Location, "历史管理器日期右键 " + day);
                return;
            }

            // 右键落在一条**没在多选里**的条目上 = 把选择收成只有它（跟资源管理器一样）；
            // 落在已选中的条目上 = 保留整份多选，菜单动作作用于全部。
            if (!IsMulti(row.Item.Path))
            {
                multi.Clear();
                multi.Add(row.Item.Path);
                anchorPath = row.Item.Path;
            }
            sel = row.Index;

            string p = row.Item.Path;
            int n = multi.Count;
            string tail = n > 1 ? ("（共 " + n + " 条）") : "";
            List<PopItem> m = new List<PopItem>();
            m.Add(PopMenu.It("打开（新标签）", delegate { OpenSelected(); }));
            m.Add(PopMenu.It("复制完整路径" + tail, delegate
            {
                List<string> ps = TargetPaths();
                try { Clipboard.SetText(string.Join("\r\n", ps.ToArray())); } catch { }
            }));
            m.Add(PopMenu.Split());
            m.Add(PopMenu.It("从历史里删掉" + tail, delegate { DeleteSelected(); }));

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
