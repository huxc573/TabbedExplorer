using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace TabbedExplorer
{
    /// <summary>
    /// 书签管理器（川 2026-09-22 要的「管理书签」）—— 仿浏览器那个书签管理器：
    /// 左边是**文件夹树**（可无限嵌套），右边是这个文件夹里的东西，顶上一条搜索框 + 几个动作。
    ///
    /// 风格按程序来（自绘行 + Theme 调色板），不用 TreeView / ListView ——
    /// 那两个在深色下要么灰底要么得跟 uxtheme 斗，自绘反而短。
    ///
    /// 行为：
    ///   · 左树：点一行 = 选中那个文件夹；点左边的三角 = 展开/收起；双击 = 打开这个文件夹（新标签）
    ///   · 右列：双击 = 打开（文件夹开新标签 / 文件交给系统）；行尾的 ✕ = 从书签移出
    ///   · 右键：重命名 / 新建文件夹 / 删除 / **设为书签栏**（哪个文件夹喂给横向那条栏）
    ///   · 从资源管理器**拖文件夹进来** = 加进当前选中的文件夹（这就是嵌套的做法）
    ///   · 搜索框：输入就跨全树过滤，右边列变成搜索结果（显示它在哪个文件夹里）
    ///
    /// ⚠ 这里所有增删改都只动 `data\favorites.json`，**磁盘上的文件夹 / 文件一个字节都不动**。
    /// </summary>
    internal sealed class FavManagerForm : Form
    {
        /// <summary>双击一个真实文件夹 —— 交给主窗口开成新标签。</summary>
        public event Action<string> OpenPath;

        private sealed class Row
        {
            public FavNode Node;
            public int Depth;
            public Rectangle Rect;
            // 拖放要用：这一行挂在哪个文件夹下、是第几项。
            // ⚠ 搜索结果的 `Parent` 是 null —— 那个列表不属于任何文件夹，
            //   所以在搜索结果里只能「拖进文件夹」，不能在列表内调顺序。
            public FavNode Parent;
            public int Index = -1;
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

        // ⚠ 这几个原来是**裸像素**（没乘 DPI）—— 150% 下整张窗口挤成一团：
        //   顶部动作条（TopH=40 设备像素 = 26 逻辑像素）装不下高 Px(26)=39 的按钮，按钮溢到标题行上；
        //   行高 26/30 设备像素也只有 17/20 逻辑像素，文字上下贴边。
        //   2026-09-22 川报的「无图标 + 文字堆叠错位」是两个毛病叠在一起：
        //   ① 小标题（“书签” / 选中文件夹名）画在 `TopH + Px(4)`，第一行却从 `TopH + Px(6)` 开始
        //      ⇒ 两者**同一行**，字压字（截图里“书签书签栏（书签…”、“新建文件夹新建文件夹”）；
        //   ② 行离谱地矮。现在一律走 Px()，并给小标题留出 CaptionH。
        //
        // ⚠ 2026-09-22 **第二轮**（川：「字体是不是变大了？自有窗口好像也变长了」）：
        //   字体一个都没动 —— 变大的是**行高 / 窗口**。上面那轮把裸像素换成 Px()，
        //   150% 屏上整张窗口和每一行都直接放大了 1.5 倍（行 30 -> 45 设备像素、窗口 880x560 -> 1320x840）。
        //   现在把**逻辑尺寸**调紧一档，让观感贴近资源管理器（图标与文字大小没变，只是不再那么空）。
        private static int TopH { get { return Px(34); } }        // 顶部动作条
        private static int LeftW { get { return Px(240); } }      // 左树宽度
        private static int TreeRowH { get { return Px(22); } }
        private static int ListRowH { get { return Px(24); } }
        /// <summary>小标题（左树 / 右列各一条）占的高度 —— 行必须从它下面开始，不然字压字。</summary>
        private static int CaptionH { get { return Px(22); } }

        private readonly List<Row> treeRows = new List<Row>();
        private readonly List<Row> listRows = new List<Row>();
        private readonly HashSet<FavNode> collapsed = new HashSet<FavNode>();

        private FavNode sel;                 // 右列显示哪个文件夹的孩子
        private FavNode hoverTree, hoverList;
        private bool hoverClose;
        private Row pressed;                 // 双击判定用
        private string filter = "";

        // ---- 多选（川 2026-09-22：「管理器没有多选功能」）----
        // 只作用于**右列**（左树选中哪个文件夹是另一件事，`sel`）。
        // 统一走 `FavNode` 引用比（树上的节点本来就是同一批对象，不像历史那边存路径）。
        private readonly List<FavNode> multi = new List<FavNode>();
        /// <summary>Shift 选范围的锚点。</summary>
        private FavNode anchorNode;

        // ---- 拖动（川 2026-09-22：管理器和书签栏都要能拖）----
        // 跟书签栏一套做法：按下只记状态，MouseMove 超阈值才算拖动，MouseUp 才真改数据。
        private FavNode dragNode;
        private Point dragStart;
        private bool dragging;
        private FavNode dropIntoNode;        // 放进这个文件夹
        private FavNode dropParentNode;      // 插到这个文件夹下面（null = 顶层）
        private int dropAt = -1;             // 插到第几项之前
        private bool dropInList;             // 落点在右列（决定提示画在哪个窗格）

        private readonly TextBox search;
        private readonly Font font, fontDim;
        private readonly Action<bool> setFavBar;
        private readonly Func<bool> getFavBar;

        public FavManagerForm(Action<bool> setFavBar, Func<bool> getFavBar)
        {
            this.setFavBar = setFavBar;
            this.getFavBar = getFavBar;

            Text = "管理书签";
            Icon = ShellIcon.AppIcon(false);   // 标题栏 / Alt+Tab 用程序自己的图标（原来这里是空的）
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(Px(840), Px(500));
            MinimumSize = new Size(Px(520), Px(330));
            BackColor = Theme.Chrome;
            ForeColor = Theme.Text;
            font = new Font("Segoe UI", Px(12), FontStyle.Regular, GraphicsUnit.Pixel);
            fontDim = new Font("Segoe UI", Px(11), FontStyle.Regular, GraphicsUnit.Pixel);
            AllowDrop = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            // 顶部动作条（真按钮，省得再造一套命中判定）
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
            bx = AddButton("添加文件夹", bx, y, delegate
            {
                FavNode f = NewFolder();
                if (f != null) { favBarChanged(); }
            });
            bx = AddButton("添加书签", bx, y, delegate
            {
                string p = InputBox.Ask(this, "添加书签", "文件夹或文件的完整路径", "");
                if (string.IsNullOrEmpty(p)) return;
                string name;
                // 川 2026-09-22：「像已加入书签这种页面直接有反馈的，也不用右下角通知」——
                // 加成功了新项**当场就出现在右边这一列里**，那就是反馈，所以成功这条路什么都不弹；
                // 只有没加进去（重复 / 没有真实路径）才说一句。
                if (!FavStore.Add(p, out name))
                    Toast.Show("加不了", string.IsNullOrEmpty(name) ? "这个位置没有真实路径。" : ("「" + name + "」已经在书签里了。"));
            });
            bx = AddButton("删除所选", bx, y, delegate { DeleteTargets(); });
            bx = AddButton("打开 json", bx, y, delegate
            {
                try { Process.Start(FavStore.FileName); }
                catch (Exception ex) { Toast.Show("打不开", ex.Message); }
            });
            bx = AddButton("回到书签栏", bx, y, delegate { sel = FavStore.BarFolder; Rebuild(); Invalidate(); });

            Theme.Changed += OnTheme;
            FavStore.Changed += OnStore;
            FormClosed += delegate
            {
                try { Theme.Changed -= OnTheme; } catch { }
                try { FavStore.Changed -= OnStore; } catch { }
            };
        }

        private void OnTheme(object sender, EventArgs e)
        {
            BackColor = Theme.Chrome;
            ForeColor = Theme.Text;
            if (search != null) { search.BackColor = Theme.InputBack; search.ForeColor = Theme.Text; }
            Invalidate(true);
        }

        private void OnStore()
        {
            if (IsDisposed) return;
            Rebuild();
            Invalidate();
        }

        private void favBarChanged()
        {
            Rebuild();
            Invalidate();
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
            b.Click += delegate { try { a(); } catch (Exception ex) { Diag.Log("书签管理器: " + ex.Message); } };
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
            if (sel == null) sel = FavStore.BarFolder;
            Rebuild();
            Invalidate();
        }

        // ==================================================================
        // 排版
        // ==================================================================

        private void Rebuild()
        {
            treeRows.Clear();
            listRows.Clear();
            if (sel == null) sel = FavStore.BarFolder;

            // 左树：拍平（收起来的文件夹不展开）
            int y = TopH + CaptionH;
            FavNode[] roots = FavStore.Tree;
            for (int i = 0; i < roots.Length; i++) Flatten(roots[i], null, i, 0, ref y);

            // 右列：某个文件夹的孩子，或者（搜索时）全树的过滤结果
            y = TopH + CaptionH;
            if (filter.Length > 0)
            {
                List<FavNode> hits = new List<FavNode>();
                for (int i = 0; i < roots.Length; i++) Search(roots[i], hits);
                for (int i = 0; i < hits.Count; i++)
                {
                    Row r = new Row();
                    r.Node = hits[i];
                    r.Parent = null;      // 搜索结果不属于任何文件夹，只能「拖进文件夹」
                    r.Index = -1;
                    r.Rect = new Rectangle(LeftW + Px(12), y, Math.Max(1, ClientSize.Width - LeftW - Px(24)), ListRowH);
                    listRows.Add(r);
                    y += ListRowH;
                }
            }
            else
            {
                for (int i = 0; i < sel.Kids.Count; i++)
                {
                    Row r = new Row();
                    r.Node = sel.Kids[i];
                    r.Parent = sel;       // 拖放在列表内调顺序靠这两个
                    r.Index = i;
                    r.Rect = new Rectangle(LeftW + Px(12), y, Math.Max(1, ClientSize.Width - LeftW - Px(24)), ListRowH);
                    listRows.Add(r);
                    y += ListRowH;
                }
            }

            // 选中项如果被删了/移走了，退回书签栏
            if (sel != null && !InTree(sel)) sel = FavStore.BarFolder;

            // 多选里的东西可能已经不在当前这一列里了（换了文件夹 / 搜索词变了 / 被删了）——
            // 把掉队的清掉，不然「已选 N」那个数字会越说越大，删的时候还删不到东西。
            if (multi.Count > 0)
            {
                for (int i = multi.Count - 1; i >= 0; i--)
                {
                    bool found = false;
                    for (int k = 0; k < listRows.Count; k++)
                    {
                        if (listRows[k].Node != multi[i]) continue;
                        found = true; break;
                    }
                    if (!found) multi.RemoveAt(i);
                }
            }
        }

        private void Flatten(FavNode n, FavNode parent, int indexInParent, int depth, ref int y)
        {
            if (n == null) return;
            Row r = new Row();
            r.Node = n;
            r.Parent = parent;
            r.Index = indexInParent;
            r.Depth = depth;
            r.Rect = new Rectangle(Px(8) + depth * Px(14), y, Math.Max(1, LeftW - Px(18) - depth * Px(14)), TreeRowH);
            treeRows.Add(r);
            y += TreeRowH;
            if (n.IsFolder && !collapsed.Contains(n))
                for (int i = 0; i < n.Kids.Count; i++) Flatten(n.Kids[i], n, i, depth + 1, ref y);
        }

        private void Search(FavNode n, List<FavNode> hits)
        {
            if (n == null) return;
            if (!n.IsFolder)
            {
                string nm = n.Display;
                if (nm.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (n.Path != null && n.Path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0))
                    hits.Add(n);
                return;
            }
            for (int i = 0; i < n.Kids.Count; i++) Search(n.Kids[i], hits);
        }

        private static bool InTree(FavNode n)
        {
            FavNode[] roots = FavStore.Tree;
            for (int i = 0; i < roots.Length; i++) if (Find(roots[i], n) != null) return true;
            return false;
        }

        private static FavNode Find(FavNode root, FavNode target)
        {
            if (root == target) return root;
            for (int i = 0; i < root.Kids.Count; i++)
            {
                FavNode r = Find(root.Kids[i], target);
                if (r != null) return r;
            }
            return null;
        }

        // ==================================================================
        // 画
        // ==================================================================

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.FillRectangle(new SolidBrush(Theme.Chrome), ClientRectangle);

            // 顶部条 + 两条分隔线
            using (SolidBrush b = new SolidBrush(Theme.TabBar))
                g.FillRectangle(b, 0, 0, Width, TopH);
            using (Pen p = new Pen(Theme.Border))
            {
                g.DrawLine(p, 0, TopH, Width, TopH);
                g.DrawLine(p, LeftW, TopH, LeftW, Height);
            }

            // 左树 / 右列的标题
            DrawCaption(g, "书签", Px(12), TopH + Px(3));
            string right = filter.Length > 0
                ? ("搜索结果（" + listRows.Count + "）" + (multi.Count > 1 ? ("　·　已选 " + multi.Count) : ""))
                : (sel != null ? FavStore.NameOf(sel) + "（" + listRows.Count + " 项）"
                                        + (multi.Count > 1 ? ("　·　已选 " + multi.Count) : "")
                               : "");
            DrawCaption(g, right, LeftW + Px(12), TopH + Px(3), true);

            for (int i = 0; i < treeRows.Count; i++) DrawTreeRow(g, treeRows[i]);
            for (int i = 0; i < listRows.Count; i++) DrawListRow(g, listRows[i]);

            DrawDropHint(g);      // 拖动中的落点提示（画在行上面）

            if (listRows.Count == 0)
                TextRenderer.DrawText(g, filter.Length > 0 ? "没有匹配的书签" : "这个文件夹里还没有书签（拖文件夹进来，或点上面的「添加」）",
                    fontDim, new Rectangle(LeftW + Px(14), TopH + CaptionH + Px(6), Math.Max(1, Width - LeftW - Px(30)), Px(24)),
                    Theme.TextDim, TextFormatFlags.Left | TextFormatFlags.NoPadding);
        }

        // ==================================================================
        // 多选（右列）
        // ==================================================================

        private bool IsMulti(FavNode n)
        {
            return n != null && multi.Contains(n);
        }

        private void ToggleMulti(FavNode n)
        {
            if (n == null) return;
            if (!multi.Remove(n)) multi.Add(n);
        }

        /// <summary>把从 anchorNode 到 n 这一段（按右列的顺序）加进多选。</summary>
        private void AddRangeTo(FavNode n)
        {
            int from = -1, to = -1;
            for (int i = 0; i < listRows.Count; i++)
            {
                if (listRows[i].Node == anchorNode) from = i;
                if (listRows[i].Node == n) to = i;
            }
            if (to < 0) return;
            if (from < 0) { from = to; anchorNode = n; }
            int a = Math.Min(from, to), b = Math.Max(from, to);
            for (int i = a; i <= b && i < listRows.Count; i++)
                if (!multi.Contains(listRows[i].Node)) multi.Add(listRows[i].Node);
        }

        private void SelectAllList()
        {
            for (int i = 0; i < listRows.Count; i++)
                if (!multi.Contains(listRows[i].Node)) multi.Add(listRows[i].Node);
        }

        /// <summary>Ctrl+A / Esc（列表里没焦点时才会到这儿）。</summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.A))
            {
                SelectAllList();
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

        /// <summary>
        /// 「删除」这个动作要删哪些：**多选了就删多选**，没多选就还是删左树选中那个文件夹（老行为）。
        /// </summary>
        private void DeleteTargets()
        {
            List<FavNode> ns = new List<FavNode>();
            if (multi.Count > 0) ns.AddRange(multi);
            else if (sel != null) ns.Add(sel);
            DeleteNodes(ns);
        }

        /// <summary>
        /// 一次删一批（**一次 Edit、一次落盘**）。
        /// 「书签栏」那一层是横着那条栏的根，删了栏就空了 —— 那一项跳过并说一声，其余的照删。
        /// </summary>
        private void DeleteNodes(List<FavNode> ns)
        {
            if (ns == null || ns.Count == 0) return;
            List<FavNode> ok = new List<FavNode>();
            bool blocked = false;
            for (int i = 0; i < ns.Count; i++)
            {
                FavNode n = ns[i];
                if (n == null) continue;
                if (n.Bar && n.IsFolder) { blocked = true; continue; }
                ok.Add(n);
            }
            if (blocked) Toast.Show("删不了", "「书签栏」这一层是横向那条栏的根，它没被删。");
            if (ok.Count == 0) return;

            FavNode[] arr = ok.ToArray();
            FavStore.Edit(delegate(List<FavNode> l)
            {
                for (int i = 0; i < arr.Length; i++) RemoveNode(l, arr[i]);
            });
            for (int i = 0; i < arr.Length; i++) if (sel == arr[i]) sel = FavStore.BarFolder;
            multi.Clear();
            Diag.Step("书签管理器: 删掉 " + arr.Length + " 项");
            favBarChanged();
        }

        private void DrawCaption(Graphics g, string text, int x, int y)
        {
            DrawCaption(g, text, x, y, false);
        }

        private void DrawCaption(Graphics g, string text, int x, int y, bool dim)
        {
            TextRenderer.DrawText(g, text, fontDim, new Point(x, y), dim ? Theme.TextDim : Theme.Text,
                TextFormatFlags.NoPadding);
        }

        private void DrawTreeRow(Graphics g, Row r)
        {
            bool isSel = (r.Node == sel);
            bool hov = (r.Node == hoverTree);
            if (isSel) g.FillRectangle(new SolidBrush(Theme.Hover), r.Rect);
            else if (hov) g.FillRectangle(new SolidBrush(Theme.Hover), r.Rect);

            int x = r.Rect.Left + Px(2);
            // 展开三角
            if (r.Node.IsFolder && r.Node.Kids.Count > 0)
            {
                string gl = collapsed.Contains(r.Node) ? "\uE76C" : "\uE70D";
                using (Font f = new Font("Segoe MDL2 Assets", Px(10), FontStyle.Regular, GraphicsUnit.Pixel))
                    TextRenderer.DrawText(g, gl, f, new Rectangle(x, r.Rect.Top, Px(12), r.Rect.Height),
                        Theme.TextDim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            x += Px(15);

            // 图标：书签栏那层用蓝色星，普通文件夹用文件夹图标
            Image ic = null;
            if (r.Node.Bar) ic = BarStar();
            else if (r.Node.IsFolder) ic = ShellIcon.FolderIcon(Px(15));
            else ic = ShellIcon.PathIcon(r.Node.Path, Px(15));
            if (ic != null) g.DrawImage(ic, new Rectangle(x, r.Rect.Top + (r.Rect.Height - Px(15)) / 2, Px(15), Px(15)));
            x += Px(19);

            string label = FavStore.NameOf(r.Node);
            // 栏根那行加个尾巴说清它是谁；名字本身已经叫「书签栏」时就不重复了
            if (r.Node.Bar && label != "书签栏") label += "（书签栏）";
            TextRenderer.DrawText(g, label, font,
                new Rectangle(x, r.Rect.Top, Math.Max(1, r.Rect.Right - x - Px(4)), r.Rect.Height),
                isSel ? Theme.Text : (r.Node.IsFolder ? Theme.Text : Theme.TextDim),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        private void DrawListRow(Graphics g, Row r)
        {
            bool hov = (r.Node == hoverList);
            if (hov) g.FillRectangle(new SolidBrush(Theme.Hover), r.Rect);

            // 多选中的一行：左边一条强调色短竖条（底色跟悬停一样用 Hover，靠竖条区分）
            if (IsMulti(r.Node))
            {
                g.FillRectangle(new SolidBrush(Theme.Hover), r.Rect);
                using (SolidBrush b = new SolidBrush(Theme.Accent))
                    g.FillRectangle(b, new Rectangle(r.Rect.Left - Px(2), r.Rect.Top + Px(3),
                        Math.Max(2, Px(3)), Math.Max(1, r.Rect.Height - Px(6))));
            }

            Image ic;
            if (r.Node.IsFolder) ic = ShellIcon.FolderIcon(Px(16));
            else ic = ShellIcon.PathIcon(r.Node.Path, Px(16));
            if (ic != null)
                g.DrawImage(ic, new Rectangle(r.Rect.Left + Px(4), r.Rect.Top + (r.Rect.Height - Px(16)) / 2, Px(16), Px(16)));

            // 行尾的 ✕
            Rectangle close = CloseRect(r.Rect);
            if (hoverClose && hov)
            {
                using (Pen p = new Pen(Theme.TextDim, Math.Max(1f, DpiScale)))
                {
                    int s = Px(4);
                    g.DrawLine(p, close.Left + close.Width / 2 - s, close.Top + close.Height / 2 - s,
                                  close.Left + close.Width / 2 + s, close.Top + close.Height / 2 + s);
                    g.DrawLine(p, close.Left + close.Width / 2 + s, close.Top + close.Height / 2 - s,
                                  close.Left + close.Width / 2 - s, close.Top + close.Height / 2 + s);
                }
            }

            int x = r.Rect.Left + Px(28);
            int rightRoom = close.Width + Px(14);
            string name = FavStore.NameOf(r.Node);
            string sub = r.Node.IsFolder ? ("子文件夹，里面 " + r.Node.Kids.Count + " 项")
                                        : (r.Node.Path != null ? r.Node.Path : "");

            int nameW = TextRenderer.MeasureText(name, font, new Size(Px(4096), Px(20)),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
            nameW = Math.Min(nameW, Math.Max(Px(60), r.Rect.Width - Px(28) - rightRoom - Px(120)));
            TextRenderer.DrawText(g, name, font, new Rectangle(x, r.Rect.Top, nameW, r.Rect.Height),
                Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            int sx = x + nameW + Px(10);
            int sw = r.Rect.Right - sx - rightRoom;
            if (sw > Px(20))
                TextRenderer.DrawText(g, sub, fontDim, new Rectangle(sx, r.Rect.Top, sw, r.Rect.Height),
                    Theme.TextDim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        private Rectangle CloseRect(Rectangle row)
        {
            int s = Px(18);
            return new Rectangle(row.Right - s - Px(6), row.Top + (row.Height - s) / 2, s, s);
        }

        private static Bitmap barStar;
        private static Image BarStar()
        {
            if (barStar != null) return barStar;
            int s = Px(15);
            Bitmap b = new Bitmap(s, s);
            using (Graphics g = Graphics.FromImage(b))
            using (Font f = new Font("Segoe MDL2 Assets", Px(15), FontStyle.Regular, GraphicsUnit.Pixel))
                TextRenderer.DrawText(g, "\uE735", f, new Rectangle(0, 0, s, s), Theme.Accent,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            barStar = b;
            return barStar;
        }

        // ==================================================================
        // 鼠标
        // ==================================================================

        private Row RowAt(Point p)
        {
            for (int i = 0; i < treeRows.Count; i++) if (treeRows[i].Rect.Contains(p)) return treeRows[i];
            for (int i = 0; i < listRows.Count; i++) if (listRows[i].Rect.Contains(p)) return listRows[i];
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            // 拖动中：只更新落点与提示，数据留到 MouseUp 才动
            // ⚠ 「左键还按着没」必须问 `Control.MouseButtons`（现读物理按键），不能用 `e.Button` ——
            //   WinForms 的 MouseMove 里那个字段经常是 `None`，拿它判会一直判成「没按」，拖动永远不触发。
            if (dragNode != null && (Control.MouseButtons & MouseButtons.Left) != 0)
            {
                if (!dragging &&
                    (Math.Abs(e.X - dragStart.X) > Px(4) || Math.Abs(e.Y - dragStart.Y) > Px(4)))
                {
                    dragging = true;
                    Diag.Step("书签管理器: 开始拖动「" + FavStore.NameOf(dragNode) + "」");
                }
                if (dragging)
                {
                    UpdateDrop(e.Location);
                    return;
                }
            }

            Row r = RowAt(e.Location);
            FavNode t = (r != null && r.Depth >= 0 && treeRows.Contains(r)) ? r.Node : null;
            FavNode l = (r != null && listRows.Contains(r)) ? r.Node : null;
            bool hc = (l != null) && CloseRect(r.Rect).Contains(e.Location);
            if (t != hoverTree || l != hoverList || hc != hoverClose)
            {
                hoverTree = t; hoverList = l; hoverClose = hc;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hoverTree = null; hoverList = null; hoverClose = false;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;

            dragNode = null; dragStart = Point.Empty; dragging = false;
            dropIntoNode = null; dropParentNode = null; dropAt = -1;

            for (int i = 0; i < treeRows.Count; i++)
            {
                Row r = treeRows[i];
                if (!r.Rect.Contains(e.Location)) continue;
                Rectangle tri = new Rectangle(r.Rect.Left + Px(2), r.Rect.Top, Px(14), r.Rect.Height);
                if (r.Node.IsFolder && r.Node.Kids.Count > 0 && tri.Contains(e.Location))
                {
                    if (!collapsed.Remove(r.Node)) collapsed.Add(r.Node);
                    Rebuild();
                    Invalidate();
                    return;
                }
                sel = r.Node.IsFolder ? r.Node : sel;
                pressed = r;
                dragNode = r.Node;
                dragStart = e.Location;
                Rebuild();
                Invalidate();
                return;
            }
            for (int i = 0; i < listRows.Count; i++)
            {
                Row r = listRows[i];
                if (!r.Rect.Contains(e.Location)) continue;
                pressed = r;
                if (CloseRect(r.Rect).Contains(e.Location)) { DeleteOne(r.Node); return; }

                // ---- 多选：Ctrl 加减、Shift 选一段、光点只选它自己 ----
                Keys mod = Control.ModifierKeys;
                if ((mod & Keys.Shift) != 0 && anchorNode != null) AddRangeTo(r.Node);
                else if ((mod & Keys.Control) != 0) { ToggleMulti(r.Node); anchorNode = r.Node; }
                else { multi.Clear(); anchorNode = r.Node; }

                dragNode = r.Node;
                dragStart = e.Location;
                Invalidate();
                return;
            }
        }

        /// <summary>
        /// 拖动中：算出落点。两种落点（跟资源管理器一样）：
        ///   · **行中间**（上下各留 1/4）= 放进这个文件夹；
        ///   · **行的上/下边缘** = 插到这一项前/后（同一层里调顺序）。
        /// 只算状态，不动数据。
        /// </summary>
        private void UpdateDrop(Point p)
        {
            FavNode into = null, parent = null;
            int at = -1;
            bool inList = false;

            Row r = RowAt(p);
            if (r != null && r.Node != dragNode)
            {
                inList = listRows.Contains(r);
                int h = Math.Max(1, r.Rect.Height);
                int dy = p.Y - r.Rect.Top;
                bool intoZone = dy > h / 4 && dy < h * 3 / 4;

                if (r.Node.IsFolder && intoZone && FavStore.CanDropInto(dragNode, r.Node))
                {
                    into = r.Node;
                }
                else if (r.Index >= 0 && !FavStore.InSubtree(r.Node, dragNode))
                {
                    parent = r.Parent;                    // null = 顶层
                    at = (dy < h / 2) ? r.Index : r.Index + 1;
                }
            }
            else if (r == null && sel != null && listRows.Count > 0 && p.X > LeftW)
            {
                // 右列最后一行下面的空白 = 追加到当前文件夹末尾
                Rectangle last = listRows[listRows.Count - 1].Rect;
                if (p.Y > last.Bottom)
                {
                    parent = sel;
                    at = sel.Kids.Count;
                    inList = true;
                }
            }

            if (into != dropIntoNode || parent != dropParentNode || at != dropAt || inList != dropInList)
            {
                dropIntoNode = into;
                dropParentNode = parent;
                dropAt = at;
                dropInList = inList;
                Invalidate();
            }
        }

        /// <summary>某个节点对应那一行的矩形（两列都找）。</summary>
        private Rectangle RowRectOf(FavNode n)
        {
            if (n == null) return Rectangle.Empty;
            for (int i = 0; i < treeRows.Count; i++) if (treeRows[i].Node == n) return treeRows[i].Rect;
            for (int i = 0; i < listRows.Count; i++) if (listRows[i].Node == n) return listRows[i].Rect;
            return Rectangle.Empty;
        }

        /// <summary>拖动时那条落点提示（放进文件夹 = 整行罩蓝；调顺序 = 一条蓝线）。</summary>
        private void DrawDropHint(Graphics g)
        {
            if (dragNode == null || !dragging) return;

            if (dropIntoNode != null)
            {
                Rectangle rr = RowRectOf(dropIntoNode);
                if (rr != Rectangle.Empty)
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(70, Theme.Accent)))
                        g.FillRectangle(b, rr);
                return;
            }
            if (dropAt < 0) return;

            List<Row> rows = dropInList ? listRows : treeRows;
            Rectangle line = Rectangle.Empty;
            for (int i = 0; i < rows.Count; i++)
            {
                Row r = rows[i];
                if (r.Parent != dropParentNode) continue;
                if (r.Index == dropAt - 1)
                    line = new Rectangle(r.Rect.Left, r.Rect.Bottom, r.Rect.Width, 0);
                if (r.Index == dropAt)
                {
                    line = new Rectangle(r.Rect.Left, r.Rect.Top, r.Rect.Width, 0);
                    break;
                }
            }
            if (line == Rectangle.Empty) return;
            using (Pen p = new Pen(Theme.Accent, Math.Max(2f, DpiScale * 2)))
                g.DrawLine(p, line.Left, line.Top, line.Right, line.Top);
        }

        /// <summary>「全部打开」—— 文件夹交给宿主开标签，文件交给系统（跟双击一条一个路子）。</summary>
        private void OpenAll(FavNode folder)
        {
            FavNode[] items = FavStore.ItemsIn(folder);
            Diag.Step("书签管理器: 全部打开「" + FavStore.NameOf(folder) + "」共 " + items.Length + " 项");
            for (int i = 0; i < items.Length; i++)
            {
                string p = items[i] == null ? null : items[i].Path;
                if (string.IsNullOrEmpty(p)) continue;
                if (FavStore.IsFolder(p))
                {
                    if (OpenPath != null) OpenPath(p);
                }
                else
                {
                    try { Process.Start(new ProcessStartInfo(p) { UseShellExecute = true }); }
                    catch (Exception ex) { Diag.Log("书签管理器: 全部打开，跳过 " + p + "：" + ex.Message); }
                }
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (pressed == null) return;
            FavNode n = pressed.Node;
            if (n.IsFolder)
            {
                if (!treeRows.Contains(pressed)) { sel = n; Rebuild(); }
                else { sel = n; Rebuild(); }
                Invalidate();
                return;
            }
            OpenNode(n);
        }

        private void OpenNode(FavNode n)
        {
            if (n == null || n.Path == null) return;
            if (FavStore.IsFolder(n.Path))
            {
                if (OpenPath != null) OpenPath(n.Path);
                Diag.Step("书签管理器: 打开 " + n.Path);
            }
            else
            {
                try { Process.Start(new ProcessStartInfo(n.Path) { UseShellExecute = true }); }
                catch (Exception ex) { Toast.Show("打不开", ex.Message); }
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);

            // 左键：拖动收尾（真挪动只在这一刻做一次）
            if (e.Button == MouseButtons.Left)
            {
                bool wasDrag = dragging;
                FavNode node = dragNode;
                FavNode into = dropIntoNode, parent = dropParentNode;
                int at = dropAt;
                dragNode = null; dragging = false;
                dropIntoNode = null; dropParentNode = null; dropAt = -1; dropInList = false;
                if (!wasDrag) return;
                Invalidate();

                bool ok = false;
                if (node != null)
                {
                    if (into != null) ok = FavStore.Move(node, into, -1);          // 放进文件夹
                    else if (at >= 0) ok = FavStore.Move(node, parent, at);        // 同层调顺序（parent 可为 null = 顶层）
                }
                Diag.Step("书签管理器: 拖动落点 -> " + (ok ? ("已挪动「" + (node != null ? FavStore.NameOf(node) : "?") + "」")
                                                             : "没动（落点无效）"));
                return;
            }

            if (e.Button != MouseButtons.Right) return;
            Row r = RowAt(e.Location);
            if (r == null) return;

            // 右键落在一条**没在多选里**的项上 = 把选择收成只有它（跟资源管理器一样）；
            // 落在已选中的项上 = 保留整份多选，菜单动作作用于全部。
            if (listRows.Contains(r) && !IsMulti(r.Node))
            {
                multi.Clear();
                multi.Add(r.Node);
                anchorNode = r.Node;
            }
            else if (listRows.Contains(r))
            {
                anchorNode = r.Node;
            }

            List<PopItem> m = new List<PopItem>();
            FavNode n = r.Node;

            if (!n.IsFolder)
            {
                m.Add(Mi("打开", delegate { OpenNode(n); }));
                m.Add(Mi("复制完整路径", delegate
                {
                    try { Clipboard.SetText(n.Path); } catch { }
                }));
                m.Add(PopMenu.Split());
            }            else
            {
                // 川 2026-09-22：文件夹（含子文件夹）能「全部打开」，超过 7 项先问一句
                m.Add(FavActions.OpenAllItem(this, n, delegate(FavNode f) { OpenAll(f); }));
                m.Add(Mi("在这个文件夹里新建文件夹", delegate { NewFolder(n); }));
                m.Add(PopMenu.Split());
            }

            m.Add(Mi("重命名…", delegate
            {
                string nn = InputBox.Ask(this, "重命名", "显示名", FavStore.NameOf(n));
                if (nn == null) return;
                nn = nn.Trim();
                if (nn.Length == 0) return;
                FavStore.Edit(delegate(List<FavNode> l) { n.Name = nn; });
                Invalidate();
            }));

            if (n.IsFolder && !n.Bar)
                m.Add(Mi("设为书签栏", delegate
                {
                    FavNode oldBar = FavStore.BarFolder;
                    FavStore.Edit(delegate(List<FavNode> l)
                    {
                        if (oldBar != null) oldBar.Bar = false;
                        n.Bar = true;
                    });
                    Toast.Show("书签栏", "现在横着那条栏显示的是「" + FavStore.NameOf(n) + "」。");
                    Rebuild();
                }));

            if (!(n.Bar && n.IsFolder))
                m.Add(Mi("从书签移出" + (multi.Count > 1 ? ("（共 " + multi.Count + " 项）") : ""),
                         delegate { DeleteTargets(); }));

            PopMenu.Show(m.ToArray(), this, e.Location, "书签管理器右键 " + FavStore.NameOf(n));
        }

        private PopItem Mi(string text, Action a) { return PopMenu.It(text, a); }

        /// <summary>删一个节点（文件夹连里面的东西一起，**磁盘上不动**）。</summary>
        private void DeleteOne(FavNode n)
        {
            if (n == null) return;
            DeleteNodes(new List<FavNode>(new FavNode[] { n }));
        }

        private static void RemoveNode(List<FavNode> l, FavNode target)
        {
            for (int i = 0; i < l.Count; i++)
            {
                if (l[i] == target) { l.RemoveAt(i); return; }
                if (l[i].IsFolder) RemoveNode(l[i].Kids, target);
            }
        }

        /// <summary>在 sel（或指定文件夹）里新建一个空文件夹；返回新节点。</summary>
        private FavNode NewFolder() { return NewFolder(sel); }

        private FavNode NewFolder(FavNode parent)
        {
            if (parent == null || !parent.IsFolder) parent = FavStore.BarFolder;
            string name = InputBox.Ask(this, "新建文件夹", "文件夹名字", "新建文件夹");
            if (string.IsNullOrEmpty(name)) return null;
            name = name.Trim();
            if (name.Length == 0) return null;
            FavNode n = new FavNode();
            n.Name = name;
            FavNode p = parent;
            FavStore.Edit(delegate(List<FavNode> l) { p.Kids.Add(n); });
            Rebuild();
            Invalidate();
            return n;
        }

        // ==================================================================
        // 拖文件夹进来 = 加进光标底下那个文件夹（这就是「嵌套」的做法）
        // ==================================================================

        protected override void OnDragEnter(DragEventArgs e)
        {
            base.OnDragEnter(e);
            e.Effect = HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        protected override void OnDragOver(DragEventArgs e)
        {
            base.OnDragOver(e);
            e.Effect = HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        private static bool HasFiles(DragEventArgs e)
        {
            try { return e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop); }
            catch { return false; }
        }

        protected override void OnDragDrop(DragEventArgs e)
        {
            base.OnDragDrop(e);
            try
            {
                string[] paths = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (paths == null || paths.Length == 0) return;

                Point p = PointToClient(new Point(e.X, e.Y));
                Row r = RowAt(p);
                FavNode parent = (r != null && r.Node.IsFolder) ? r.Node : sel;
                if (parent == null) parent = FavStore.BarFolder;

                int added = 0;
                foreach (string raw in paths)
                {
                    string nm;
                    if (AddInto(parent, raw, out nm)) added++;
                }
                Diag.Step("书签管理器: 拖入 " + paths.Length + " 项 -> 加进「" + FavStore.NameOf(parent) + "」" + added + " 项");
                // 川 2026-09-22：「像已加入书签这种页面直接有反馈的，也不用右下角通知」——
                // 新项**当场就出现在右边这一列里**了，那就是反馈，不再弹气泡。
                Rebuild();
                Invalidate();
            }
            catch (Exception ex) { Diag.Log("书签管理器: 拖放失败 " + ex.Message); }
        }

        /// <summary>把一个路径加进指定文件夹（重复的不再加）。</summary>
        private static bool AddInto(FavNode folder, string rawPath, out string name)
        {
            name = null;
            string p = rawPath == null ? null : rawPath.Trim().Trim('"');
            if (string.IsNullOrEmpty(p)) return false;
            bool isDir = FavStore.IsFolder(p);
            if (!isDir)
            {
                try { if (!System.IO.File.Exists(p)) return false; }
                catch { return false; }
            }
            string full;
            try { full = System.IO.Path.GetFullPath(p); }
            catch { return false; }
            name = FavStore.NameOfPath(full);

            bool added = false;
            FavStore.Edit(delegate(List<FavNode> l)
            {
                for (int i = 0; i < folder.Kids.Count; i++)
                    if (!folder.Kids[i].IsFolder && PathRules.Same(folder.Kids[i].Path, full)) return;
                FavNode n = new FavNode();
                n.Path = full;
                folder.Kids.Add(n);
                added = true;
            });
            return added;
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
