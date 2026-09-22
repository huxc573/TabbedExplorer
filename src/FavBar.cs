using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace TabbedExplorer
{
    /// <summary>
    /// 书签栏（Ctrl+Shift+B 开关）—— 夹在标签条和内容之间，跟浏览器那条书签栏一个位置。
    ///
    /// 2026-09-22 川改的几条：
    ///   1. **内容程序自己记** —— 数据在 `data\favorites.json`，结构是树（见 `FavStore`）。
    ///      栏上显示的是「书签栏」那个文件夹的直接孩子；里面**带孩子的节点**（子文件夹）
    ///      点一下会**往下列一层**（浏览器就是这么干的）。
    ///   2. **最左边一枚固定的书签图标** —— 这条栏的「名牌」，不跟着内容横向滚动；
    ///      点它开**书签管理器**（原来点它是开数据目录，川改成了「管理书签」）。
    ///   3. 项上右键可以**重命名**（改我们自己这份 json 里记的显示名，磁盘上那个文件夹不动）。
    ///
    /// 点击行为：文件夹 → 开成新标签；文件 → 交给系统（默认程序）打开。
    /// </summary>
    internal sealed class FavBar : Control
    {
        /// <summary>书签栏高度（逻辑像素）。够放一行 18px 图标 + 文字。</summary>
        public const int StdHeight = 30;

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

        private sealed class Item
        {
            public FavNode Node;     // 树里的真节点（改名字直接改它）
            public Bitmap Icon;
            /// <summary>这一项占多宽（EnsureLayout 算，鼠标命中测试要用）。</summary>
            public int LayoutW;
            public string Name { get { return FavStore.NameOf(Node); } }
            public bool IsFolder { get { return Node != null && Node.IsFolder; } }
        }

        private readonly List<Item> items = new List<Item>();
        private readonly ToolTip tips = new ToolTip();

        private int hoverIndex = -1;
        private bool hoverLead;
        private int scrollX;                 // 内容左移了多少（挤不下时靠它翻）
        private int contentWidth;
        private string tipKey;

        // ---- 拖动（川 2026-09-22：栏上的项要能调顺序 / 拖进子文件夹）----
        // 按下时只记状态，**动作留到 MouseUp** —— 不这样就没法跟拖动区分（见 OnMouseDown 注释）。
        private int dragIndex = -1;          // 按下时命中的那一项
        private Point dragStart;
        private bool dragging;
        private int dropIndex = -1;          // 插到第几项之前（= items.Count 表示插到末尾）
        private int dropInto = -1;           // 放进第几项（那一项必须是文件夹）

        private readonly Font font;
        private static Bitmap folderFallback;
        private static Bitmap leadIcon;

        /// <summary>窗口失活时底色换成 ChromeOff（跟标签条、主窗口一个逻辑）。</summary>
        public bool Inactive
        {
            get { return inactive; }
            set
            {
                if (inactive == value) return;
                inactive = value;
                BackColor = TheBack;
                Invalidate();
            }
        }
        private bool inactive;
        private Color TheBack { get { return inactive ? Theme.ChromeOff : Theme.Chrome; } }

        public delegate void PathEventHandler(string path);
        /// <summary>点了某一项 —— 参数是那个**文件夹**路径（新标签页开它）。</summary>
        public event PathEventHandler ItemClicked;
        /// <summary>点了最左边那枚书签图标（EmbedForm 拿它开书签管理器）。</summary>
        public event EventHandler LeadClicked;
        /// <summary>右键选了「管理书签…」。</summary>
        public event EventHandler ManageRequested;
        /// <summary>右键选了「全部打开（N 书签）」—— 宿主拿这个文件夹里的东西去开标签。</summary>
        public event Action<FavNode> OpenAllRequested;
        /// <summary>上面那一排要重新读了（右键「刷新」）。</summary>
        public event EventHandler Reloaded;
        /// <summary>右键选了「隐藏书签栏」—— 真正隐藏由 Hub 做（它要同时刷托盘菜单和所有窗口）。</summary>
        public event EventHandler HideRequested;

        public FavBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = Px(StdHeight);
            font = new Font("Segoe UI", Px(12), FontStyle.Regular, GraphicsUnit.Pixel);
            BackColor = Theme.Chrome;
            AllowDrop = true;            // 往栏上拖文件夹 / 文件（见 OnDragDrop）
            tips.InitialDelay = 350;
            tips.AutoPopDelay = 8000;
            tips.ShowAlways = true;      // 窗口没激活也照弹（跟标签条一个理由）
            Theme.StyleTip(tips);        // 背景 / 字体跟着颜色模式
            Theme.Changed += delegate
            {
                BackColor = TheBack;
                leadIcon = null;         // 蓝色星标按主题色画，换主题要重画
                folderFallback = null;
                if (!IsDisposed) Invalidate();
            };
            // 数据一变（改名 / 增删 / 管理器里拖来拖去）这边就跟着重读
            FavStore.Changed += delegate
            {
                if (!IsDisposed && Visible) Reload();
            };
        }

        /// <summary>现在有几项。</summary>
        public int Count { get { return items.Count; } }

        /// <summary>最左边那枚图标占的宽度（固定，不参与滚动）。</summary>
        private int LeadWidth { get { return Px(30); } }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) Reload();
        }

        /// <summary>按 `FavStore` 里的树重建（拖进来 / 移除之后都要调一次）。</summary>
        public void Reload()
        {
            items.Clear();
            hoverIndex = -1;
            scrollX = 0;
            try
            {
                foreach (FavNode n in FavStore.BarItems)
                {
                    Item it = new Item();
                    it.Node = n;
                    it.Icon = n.IsFolder ? null : ShellIcon.PathIcon(n.Path, Px(18));
                    items.Add(it);
                }
                Diag.Step("书签栏: 载入 " + items.Count + " 项");
            }
            catch (Exception ex) { Diag.Log("书签栏: 载入失败 " + ex.Message); }

            Invalidate();
            if (Reloaded != null) Reloaded(this, EventArgs.Empty);
        }

        // ------------------------------------------------------------------

        private void EnsureLayout()
        {
            contentWidth = Px(6);
            for (int i = 0; i < items.Count; i++)
            {
                Size t = TextRenderer.MeasureText(items[i].Name, font, new Size(Px(400), Px(20)),
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                int w = Px(6) + Px(18) + Px(5) + Math.Min(t.Width, Px(140)) + Px(8);
                items[i].LayoutW = w;
                contentWidth += w;
            }
            contentWidth += Px(6);
        }

        private Rectangle BoundsOf(int i)
        {
            int x = LeadWidth + Px(6) - scrollX;
            for (int k = 0; k < i; k++) x += items[k].LayoutW;
            return new Rectangle(x, Px(4), items[i].LayoutW, Height - Px(8));
        }

        private Rectangle LeadBounds()
        {
            return new Rectangle(Px(2), Px(3), LeadWidth - Px(4), Height - Px(6));
        }

        private int HitTest(Point p)
        {
            if (LeadBounds().Contains(p)) return -2;      // -2 = 左边那枚书签图标
            EnsureLayout();
            for (int i = 0; i < items.Count; i++)
            {
                if (BoundsOf(i).Contains(p)) return i;
            }
            return -1;
        }

        private void ClampScroll()
        {
            int max = Math.Max(0, contentWidth - (Width - LeadWidth));
            if (scrollX > max) scrollX = max;
            if (scrollX < 0) scrollX = 0;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            EnsureLayout();
            Graphics g = e.Graphics;
            g.FillRectangle(new SolidBrush(TheBack), ClientRectangle);

            // ---- 左边那枚固定的「书签」图标（川 2026-09-22 要的）----
            Rectangle lead = LeadBounds();
            if (hoverLead) g.FillRectangle(new SolidBrush(Theme.Hover), lead);
            Image li = LeadImage();
            if (li != null)
                g.DrawImage(li, new Rectangle(lead.Left + (lead.Width - Px(20)) / 2,
                                              lead.Top + (lead.Height - Px(20)) / 2, Px(20), Px(20)));
            // 图标右边一条淡竖线，跟书签项隔开
            using (Pen p = new Pen(Theme.Border))
                g.DrawLine(p, lead.Right + Px(3), Px(6), lead.Right + Px(3), Height - Px(7));

            // ---- 书签项 ----
            if (items.Count == 0)
            {
                TextRenderer.DrawText(g, "把文件夹或文件拖到这条栏上就能加进书签",
                    font, new Rectangle(LeadWidth + Px(10), 0, Math.Max(1, Width - LeadWidth - Px(20)), Height),
                    Theme.TextDim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
            else
            {
                for (int i = 0; i < items.Count; i++)
                {
                    Rectangle r = BoundsOf(i);
                    if (r.Right < LeadWidth || r.Left > Width) continue;
                    if (i == hoverIndex) g.FillRectangle(new SolidBrush(Theme.Hover), r);

                    Bitmap ic = items[i].Icon;
                    if (ic == null)
                    {
                        if (folderFallback == null) folderFallback = ShellIcon.FolderIcon(Px(18));
                        ic = folderFallback;
                    }
                    if (ic != null)
                        g.DrawImage(ic, new Rectangle(r.Left + Px(6), r.Top + (r.Height - Px(18)) / 2, Px(18), Px(18)));

                    int tx = r.Left + Px(6) + Px(18) + Px(5);
                    int tw = r.Right - Px(8) - tx;
                    if (tw <= 0) continue;
                    TextRenderer.DrawText(g, items[i].Name, font,
                        new Rectangle(tx, r.Top, tw, r.Height),
                        i == hoverIndex ? Theme.Text : Theme.TextDim,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                }

                // 拖动中的落点提示：放进文件夹 = 整项罩一层蓝；调顺序 = 在缝上画一条蓝竖线
                for (int i = 0; i < items.Count; i++)
                {
                    Rectangle r = BoundsOf(i);
                    if (dropInto == i)
                    {
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(70, Theme.Accent)))
                            g.FillRectangle(b, r);
                    }
                    if (dropIndex == i)
                    {
                        using (Pen p = new Pen(Theme.Accent, Math.Max(2f, DpiScale * 2)))
                            g.DrawLine(p, r.Left, r.Top, r.Left, r.Bottom);
                    }
                }
                if (dropIndex >= items.Count && items.Count > 0)
                {
                    Rectangle last = BoundsOf(items.Count - 1);
                    using (Pen p = new Pen(Theme.Accent, Math.Max(2f, DpiScale * 2)))
                        g.DrawLine(p, last.Right, last.Top, last.Right, last.Bottom);
                }
            }

            // 底下一条淡淡的线，跟内容区分开
            g.DrawLine(new Pen(Theme.Border), 0, Height - 1, Width, Height - 1);
        }

        /// <summary>
        /// 「书签」那枚图标（MDL2 的实心星星）。
        /// 川 2026-09-22：「用已打开书签栏的那个蓝色图标」+「有点小」——
        /// 于是改成跟标签条上那枚**开了书签栏时一样**的蓝色实心星（E735 + Theme.Accent），
        /// 尺寸 16 → 20（字形 ink 比字号小，同字号下星星看着比齿轮/历史那几个都小）。
        /// </summary>
        private static Image LeadImage()
        {
            if (leadIcon != null) return leadIcon;
            try
            {
                Bitmap b = new Bitmap(Px(20), Px(20));
                using (Graphics g = Graphics.FromImage(b))
                using (Font f = new Font("Segoe MDL2 Assets", Px(20), FontStyle.Regular, GraphicsUnit.Pixel))
                {
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                    TextRenderer.DrawText(g, "\uE735", f, new Rectangle(0, 0, Px(20), Px(20)),
                        Theme.Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.NoPadding);
                }
                leadIcon = b;
            }
            catch { }
            return leadIcon;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            // ---- 拖动：拖栏上的项调顺序；拖到某个文件夹项上 = **放进那个文件夹** ----
            // （川 2026-09-22：原来拖到书签栏文件夹上只会并排加一个同级项，不是加进文件夹。）
            // ⚠ 这里必须问 `Control.MouseButtons`（现读物理按键状态），**不能用 `e.Button`** ——
            //   WinForms 的 `MouseMove` 事件里那个字段经常是 `None`（它只对 Down/Up 才填得准），
            //   拿它判「左键还按着没」会一直判成「没按」，拖动就永远触发不了。
            if (dragIndex >= 0 && (Control.MouseButtons & MouseButtons.Left) != 0)
            {
                if (!dragging &&
                    (Math.Abs(e.X - dragStart.X) > Px(4) || Math.Abs(e.Y - dragStart.Y) > Px(4)))
                {
                    dragging = true;
                    Diag.Step("书签栏: 开始拖动「" + items[dragIndex].Name + "」");
                }
                if (dragging)
                {
                    UpdateDrop(e.Location);
                    tips.Hide(this);          // 拖着的时候别弹提示挡视线
                    tipKey = null;
                    return;
                }
            }

            int i = HitTest(e.Location);
            bool hl = (i == -2);
            if (hl != hoverLead) { hoverLead = hl; Invalidate(); }
            if (i != hoverIndex) { hoverIndex = i; Invalidate(); }

            string key = i >= 0 ? ("fav:" + i) : (hl ? "fav:lead" : null);
            if (!string.Equals(key, tipKey, StringComparison.Ordinal))
            {
                tipKey = key;
                if (i < 0 && !hl) tips.Hide(this);
                else if (hl)
                {
                    Rectangle r = LeadBounds();
                    tips.Show("书签\r\n把文件夹或文件拖到这条栏上就能加进来；点这儿管理书签",
                        this, r.Left, r.Bottom + Px(2), 8000);
                }
                else
                {
                    Rectangle r = BoundsOf(i);
                    int x = r.Left;                       // 提示贴在那项下面、左边对齐
                    if (x + Px(240) > Width) x = Math.Max(0, Width - Px(240));
                    string body = items[i].IsFolder
                        ? ("子文件夹，里面有 " + items[i].Node.Kids.Count + " 项")
                        : items[i].Node.Path;
                    tips.Show(items[i].Name + "\r\n" + body, this, x, r.Bottom + Px(2), 8000);
                }
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hoverIndex = -1;
            hoverLead = false;
            tipKey = null;
            tips.Hide(this);
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            EnsureLayout();
            if (contentWidth <= Width - LeadWidth) return;
            scrollX -= e.Delta / 3;     // 一格滚一点，别一滚就飞到头
            ClampScroll();
            Invalidate();
        }

        // ------------------------------------------------------------------
        // 拖放：从文件列表里拖文件夹 / 文件过来（川 2026-09-22 要的）
        // ------------------------------------------------------------------

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

                int added = 0, dup = 0, bad = 0;
                string last = null;
                foreach (string p in paths)
                {
                    string name;
                    if (FavStore.Add(p, out name)) { added++; last = name; }
                    else if (string.IsNullOrEmpty(name)) bad++;
                    else dup++;
                }
                Diag.Step(string.Format("书签栏: 拖入 {0} 项 -> 新增 {1} / 重复 {2} / 收不了 {3}",
                    paths.Length, added, dup, bad));
                if (added > 0)
                {
                    Reload();
                    // 川 2026-09-22：「像已加入书签这种页面直接有反馈的，也不用右下角通知」——
                    // 新项**立刻出现在这条栏上**，那就是反馈，不再弹气泡。
                    // 下面两条「重复 / 收不了」是**真的什么都没发生**，不说一句就成了「点了没反应」。
                }
                else if (dup > 0 && bad == 0) Toast.Show("书签", dup == 1 ? "这一项已经在里面了。" : "这些都已经在里面了。");
                else Toast.Show("书签", "这些位置没有真实路径（库 / 虚拟文件夹），收不了。");
            }
            catch (Exception ex)
            {
                Diag.Log("书签栏: 拖放失败 " + ex.Message);
                Toast.Show("书签", "加入失败：" + ex.Message);
            }
        }

        // ------------------------------------------------------------------

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            tips.Hide(this);
            tipKey = null;
            // 右键不用在这儿响应：等 MouseUp 再弹菜单（见下面的说明）。
            if (e.Button != MouseButtons.Left) return;

            // 先把上一次的拖动状态清干净（按在最左那枚图标上时会直接 return，
            // 不在前面清的话会留下一份**上次的** dragIndex，松手就点错项）。
            dragIndex = -1;
            dragging = false;
            dropIndex = -1;
            dropInto = -1;

            int i = HitTest(e.Location);
            if (i == -2)
            {
                if (LeadClicked != null) LeadClicked(this, EventArgs.Empty);
                return;
            }
            if (i < 0) return;

            // ⚠ 项上的「打开」动作**推迟到 MouseUp**（原来在 MouseDown 里），两个理由：
            //   ① 要跟拖动分开 —— 按下就跳走的话没机会拖；
            //   ② 弹菜单那条硬规矩：菜单必须在**松手之后**弹。按着键弹菜单，一松手系统就把
            //      鼠标捕获收走，看门狗会把菜单关掉 —— 川报过的「闪一下就没了」就是这个。
            dragIndex = i;
            dragStart = e.Location;
        }

        /// <summary>项上松手（没拖动）= 普通点击 —— 原来这一套写在 `OnMouseDown` 里。</summary>
        private void ActivateItem(int i)
        {
            if (i < 0 || i >= items.Count) return;
            // 子文件夹：点一下往下列一层（浏览器书签栏就是这么干的）
            if (items[i].IsFolder)
            {
                ShowSubMenu(i);
                return;
            }

            string p = items[i].Node.Path;
            if (FavStore.IsFolder(p))
            {
                if (ItemClicked != null) ItemClicked(p);      // 文件夹 → 新标签
                return;
            }
            try
            {
                // 文件（含 .lnk）→ 交给系统默认程序
                Diag.Step("书签栏: 打开文件 " + p);
                Process.Start(new ProcessStartInfo(p) { UseShellExecute = true });
            }
            catch (Exception ex) { Toast.Show("打不开", ex.Message); }
        }

        /// <summary>
        /// 拖动中：算出落点（放进哪个文件夹 / 插到第几项之前）。
        /// 只改状态 + 重画，**不动数据** —— 真挪动在 MouseUp 里做一次。
        /// </summary>
        private void UpdateDrop(Point p)
        {
            EnsureLayout();
            int ni = -1, nInto = -1;
            bool overItem = false;

            for (int i = 0; i < items.Count; i++)
            {
                Rectangle r = BoundsOf(i);
                if (!r.Contains(p)) continue;
                overItem = true;
                if (i == dragIndex) break;                     // 拖回自己身上：什么也不提示
                if (items[i].IsFolder)
                    nInto = i;                                 // 落点在文件夹项上 = 放进它里面
                else
                    ni = (p.X < r.Left + r.Width / 2) ? i : i + 1;   // 左半 / 右半 = 插前 / 插后
                break;
            }

            // 落在最后一项右边的空白里 = 挪到最末尾
            if (!overItem && items.Count > 0 && dragIndex >= 0)
            {
                Rectangle last = BoundsOf(items.Count - 1);
                if (p.X >= last.Right) ni = items.Count;
            }

            if (ni != dropIndex || nInto != dropInto)
            {
                dropIndex = ni;
                dropInto = nInto;
                Invalidate();
            }
        }

        /// <summary>
        /// 右键菜单 —— **在 MouseUp 里弹**。写在 MouseDown 里的话，紧接着那条「右键抬起」消息会投到
        /// 刚弹出来的菜单窗口上（菜单抓着鼠标捕获），菜单把它当成「点在别处」当场关掉。
        /// </summary>
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);

            // 左键：先把拖动收尾，没拖动才算「点击」
            if (e.Button == MouseButtons.Left)
            {
                int di = dragIndex;
                bool wasDrag = dragging;
                int into = dropInto, at = dropIndex;
                dragIndex = -1;
                dragging = false;
                dropInto = -1;
                dropIndex = -1;

                if (wasDrag)
                {
                    Invalidate();
                    FavNode node = (di >= 0 && di < items.Count) ? items[di].Node : null;
                    bool ok = false;
                    if (node != null)
                    {
                        if (into >= 0 && into < items.Count)          // 放进那个文件夹（追加到末尾）
                            ok = FavStore.Move(node, items[into].Node, -1);
                        else if (at >= 0)                             // 同一层里调顺序
                        {
                            FavNode bar = FavStore.BarFolder;
                            ok = bar != null && FavStore.Move(node, bar, at);
                        }
                    }
                    Diag.Step("书签栏: 拖动落点 -> " + (ok ? ("已挪动「" + (node != null ? FavStore.NameOf(node) : "?") + "」")
                                                              : "没动（落点无效）"));
                    return;
                }

                ActivateItem(di);
                return;
            }

            if (e.Button != MouseButtons.Right) return;

            ShowItemMenu(e.Location);
        }

        /// <summary>把子文件夹里的东西列出来（支持继续往下嵌套）。</summary>
        private void ShowSubMenu(int index)
        {
            if (index < 0 || index >= items.Count) return;
            FavNode nd = items[index].Node;
            PopItem[] kids = ItemsOf(nd);
            if (kids.Length == 0) { Toast.Show(nd.Display, "这个文件夹里还没有书签。"); return; }
            Rectangle r = BoundsOf(index);
            Point at = new Point(r.Left, r.Bottom + Px(1));
            string what = "书签子文件夹 " + nd.Display;
            // ⚠ 「推后一轮再弹」这一步现在收在 `PopMenu.Show` 里（川报的「点书签栏文件夹，
            //   里面的子项点不动」的根就在那儿）—— 这里不用自己 Defer。
            PopMenu.Show(kids, this, at, what);
        }

        /// <summary>把一个节点的孩子变成菜单项（文件夹继续往下嵌套一层）。</summary>
        private PopItem[] ItemsOf(FavNode folder)
        {
            List<PopItem> r = new List<PopItem>();
            for (int i = 0; i < folder.Kids.Count; i++)
            {
                FavNode k = folder.Kids[i];
                if (k.IsFolder) r.Add(PopMenu.Sub(k.Display, ItemsOf(k)));
                else
                {
                    string p = k.Path;
                    r.Add(PopMenu.It(k.Display, delegate
                    {
                        if (FavStore.IsFolder(p)) { if (ItemClicked != null) ItemClicked(p); }
                        else
                        {
                            try { Process.Start(new ProcessStartInfo(p) { UseShellExecute = true }); }
                            catch (Exception ex) { Toast.Show("打不开", ex.Message); }
                        }
                    }));
                }
            }
            return r.ToArray();
        }

        /// <summary>
        /// 栏上右键那一份菜单（项上 = 项菜单，空白处 = 栏菜单）。由 `OnMouseUp` 调 ——
        /// 理由见 `OnMouseUp` 上方那段注释（菜单必须在松手之后弹）。
        /// </summary>
        private void ShowItemMenu(Point at)
        {
            int i = HitTest(at);
            List<PopItem> m = new List<PopItem>();

            if (i >= 0)
            {
                Item it = items[i];
                string p = it.Node.Path;
                if (it.IsFolder)
                {
                    FavNode nd = it.Node;
                    // 川 2026-09-22：文件夹（含它下面的子文件夹）能「全部打开」，超过 7 项先问一句
                    m.Add(FavActions.OpenAllItem(this, nd, delegate(FavNode f)
                    {
                        if (OpenAllRequested != null) OpenAllRequested(f);
                    }));
                    m.Add(PopMenu.It("展开这一层", delegate { ShowSubMenu(i); }));
                    m.Add(PopMenu.It("重命名…", delegate { Rename(nd); }));
                    m.Add(PopMenu.Split());
                }
                else
                {
                    if (FavStore.IsFolder(p))
                        m.Add(PopMenu.It("在新标签页打开", delegate { if (ItemClicked != null) ItemClicked(p); }));
                    else
                        m.Add(PopMenu.It("用默认程序打开", delegate
                        {
                            try { Process.Start(new ProcessStartInfo(p) { UseShellExecute = true }); }
                            catch (Exception ex) { Toast.Show("打不开", ex.Message); }
                        }));
                    m.Add(PopMenu.It("复制完整路径", delegate
                    {
                        try { Clipboard.SetText(p); }
                        catch (Exception ex) { Diag.Log("书签栏: 复制失败 " + ex.Message); }
                    }));
                    // 川 2026-09-22 要的：栏上的项能改名（改的是**我们自己这份 json 里记的显示名**，
                    // 磁盘上那个文件夹/文件一个字节都不动）
                    FavNode nd = it.Node;
                    m.Add(PopMenu.It("重命名…", delegate { Rename(nd); }));
                    m.Add(PopMenu.Split());
                }

                FavNode node = it.Node;
                m.Add(PopMenu.It("从书签移除", delegate
                {
                    // 只从我们这份 json 里去掉，**不动磁盘上那个文件/文件夹**
                    FavStore.Edit(delegate(List<FavNode> l) { RemoveFrom(l, node); });
                    Diag.Step("书签栏: 移除 " + FavStore.NameOf(node));
                }));
                m.Add(PopMenu.Split());
            }

            m.Add(PopMenu.It("刷新书签栏", delegate { Reload(); }));
            m.Add(PopMenu.It("管理书签…", delegate
            {
                if (ManageRequested != null) ManageRequested(this, EventArgs.Empty);
            }));
            m.Add(PopMenu.Split());
            // 快捷键文本跟着设置走（可自定义，别写死）
            m.Add(PopMenu.It("隐藏书签栏（" + Hotkeys.Combo("favbar") + "）",
                delegate { if (HideRequested != null) HideRequested(this, EventArgs.Empty); }));

            PopItem[] menu = m.ToArray();
            string title = i >= 0 ? ("书签项右键 " + items[i].Name) : "书签栏右键";
            PopMenu.Show(menu, this, at, title);   // 「推后一轮」在 PopMenu.Show 里做
        }
        /// <summary>按节点删除（文件夹连里面的东西一起删，磁盘上不动）。</summary>
        private static void RemoveFrom(List<FavNode> l, FavNode node)
        {
            for (int i = 0; i < l.Count; i++)
            {
                if (l[i] == node) { l.RemoveAt(i); return; }
                if (l[i].IsFolder) RemoveFrom(l[i].Kids, node);
            }
        }

        /// <summary>改显示名（点小输入框，改完存盘）。</summary>
        private void Rename(FavNode node)
        {
            string old = FavStore.NameOf(node);
            string nn = InputBox.Ask(this, "重命名字签", "显示名", old);
            if (nn == null) return;
            nn = nn.Trim();
            if (nn.Length == 0 || nn == old) return;
            FavStore.Edit(delegate(List<FavNode> l) { node.Name = nn; });
            Diag.Step("书签栏: 重命名 " + old + " -> " + nn);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (tips != null) tips.Dispose();
                if (font != null) font.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
