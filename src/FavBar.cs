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
    /// 用户改的几条：
    ///   1. **内容程序自己记** —— 数据在 `data\favorites.json`，结构是树（见 `FavStore`）。
    ///      栏上显示的是「书签栏」那个文件夹的直接孩子；里面**带孩子的节点**（子文件夹）
    ///      点一下会**往下列一层**（浏览器就是这么干的）。
    ///   2. **最左边一枚固定的书签图标** —— 这条栏的「名牌」，不跟着内容横向滚动；
    ///      点它开**书签管理器**（原来点它是开数据目录，用户改成了「管理书签」）。
    ///   3. 项上右键可以**重命名**（改我们自己这份 json 里记的显示名，磁盘上那个文件夹不动）。
    ///
    /// 点击行为：文件夹 → 开成新标签；文件 → 交给系统（默认程序）打开。
    ///
    /// **竖排**（`Vertical`）：垂直侧边栏模式下它住在左栏里当一段 ——
    /// 顶上一行是「★ 书签」，**点它摊开 / 收起**这一段（收起时这条只剩那一行）；下面一行一个书签。
    /// 两种排法共用同一份 `items`，坐标全部经 `EnsureLayout` / `BoundsOf` / `LeadBounds`，
    /// 所以只要这三个分支了，命中判定 / 画 / 拖动落点就全跟着对。
    /// </summary>
    internal sealed class FavBar : Control
    {
        /// <summary>书签栏高度（逻辑像素）。够放一行 18px 图标 + 文字。</summary>
        public const int StdHeight = 30;

        /// <summary>竖排时一行书签的高度（逻辑像素）。</summary>
        private const int VRowL = 26;
        /// <summary>竖排时顶上那一行（星标 + 「书签」）的高度。</summary>
        private const int VHeadL = 28;
        /// <summary>竖排时右侧滚动条的宽度（逻辑像素）。</summary>
        private const int VScrollWL = 7;
        /// <summary>竖排时**顶上那几像素**算「可拖的分割条」（逻辑像素）。</summary>
        private const int VResizeHotL = 5;

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

        /// <summary>
        /// **一行** —— 竖排按展开状态摊平之后的一行；横排时就是顶层那一排（`Depth` 恒 0）。
        /// ⚠ 画 / 命中 / 拖动落点**全按下标走这一份**。以前横排读 `items`、竖排另算一套，
        ///   一旦加了树形展开就会出现「点这行开那行」。
        /// </summary>
        private sealed class Row
        {
            public FavNode Node;
            /// <summary>嵌套层级（0 = 书签栏的直接孩子）。</summary>
            public int Depth;
            /// <summary>
            /// 这一行的**根祖先**在 `items` 里的下标。调顺序（拖动排序）要的是顶层下标，
            /// 而命中给的是行下标 —— 展开之后就叉了，所以建行的时候顺手存上（靠 node 反查不出来）。
            /// </summary>
            public int TopIndex;
            /// <summary>竖排：这个文件夹现在是不是原地摊开着。</summary>
            public bool Expanded;
            /// <summary>不是文件而是文件夹 —— 点它就是「摊开 / 收起」，右边要画一个三角。</summary>
            public bool IsFolder { get { return Node != null && Node.IsFolder; } }
            public string Name { get { return Node == null ? "" : FavStore.NameOf(Node); } }
            /// <summary>横排时这一行占多宽（竖排不看，一律铺满）。</summary>
            public int W;
        }

        /// <summary>顶层那一排（= 书签栏文件夹的直接孩子）—— 拖进来 / 移出去 / 同层排序都用它。</summary>
        private readonly List<FavNode> items = new List<FavNode>();
        /// <summary>当前真正画出来的行（横排 = `items` 原样，竖排 = 按展开状态摊平的树）。</summary>
        private readonly List<Row> rows = new List<Row>();
        /// <summary>
        /// 竖排：哪些文件夹是**原地摊开**的（用户点文件夹就是切它）。
        /// 按**节点引用**记 —— `FavStore.roots` 在进程内不会重读，节点是稳定的，
        /// 所以改名 / 拖动排序之后展开状态不会莫名其妙地全部弹回去。
        /// </summary>
        private readonly HashSet<FavNode> expanded = new HashSet<FavNode>();
        /// <summary>
        /// 图标缓存（按节点）。节点在进程内稳定，所以这份缓存一直有效；
        /// `Reload` 会清掉它（**不 Dispose** —— `ShellIcon` 自己会复用位图，dispose 了会坑到别处）。
        /// </summary>
        private readonly Dictionary<FavNode, Bitmap> icons = new Dictionary<FavNode, Bitmap>();
        private readonly ToolTip tips = new ToolTip();

        private int hoverIndex = -1;
        private bool hoverLead;
        private int scrollX;                 // 内容滚了多少（横排=左移，竖排=上移）

        // ---- 竖排：顶上的可拖分割条 + 右侧的纵向滚动条 ----
        private bool resizable;              // 现在允许拖分割条吗（只有书签段摊开着才允许）
        private bool hoverResize;            // 鼠标压在那根线上
        private bool resizeDrag;             // 正在拖那根线
        // ⚠ 拖的那一下**控件自己在变高变矮**（顶边跟着鼠标走），拿控件坐标算必然自激
        //   ⇒ 一律用屏幕坐标跟按下那一刻的高度算。
        private int resizeY0;                // 按下时的屏幕 Y
        private int resizeH0;                // 按下时本控件的高度（= 那一刻的书签段高）
        private bool thumbDrag;              // 正在拖滚动条滑块
        private int thumbY0, thumbScroll0, thumbSpan, thumbMax;
        private int contentWidth;
        private int contentHeight;
        private bool vertical;
        private string tipKey;

        /// <summary>
        /// 竖排（垂直侧边栏那个左栏里的一截）。
        /// ⚠ 竖排时 `scrollX` 当**纵向**滚动量用 —— 两种排法不可能同时出现，
        ///   没必要再开一个字段（多一个字段就多一处忘了同步的地方）。
        /// </summary>
        public bool Vertical
        {
            get { return vertical; }
            set
            {
                if (vertical == value) return;
                vertical = value;
                scrollX = 0;
                tips.Hide(this);
                tipKey = null;
                Invalidate();
            }
        }

        private int VRowH { get { return Px(VRowL); } }
        private int VHeadH { get { return Px(VHeadL); } }

        /// <summary>
        /// 竖排「收起态」要多高 —— 只剩顶上那行标题（用户：书签段默认收缩、可展开收缩，记住上次的行为）。
        /// 上层拿它给书签段留位置（见 `EmbedForm.DoLayout`）。
        /// </summary>
        public int HeaderHeight { get { return VHeadH; } }

        /// <summary>
        /// 竖排书签段的**最小**高度（标题行 + 一行书签）。拖分割条时就卡在这 ——
        /// 让它能拖到 0 的话，这一段整个消失，就再也没地方把那根线抓回来了。
        /// </summary>
        public int MinBandHeight { get { return VHeadH + VRowH; } }

        /// <summary>
        /// 现在允许拖顶上那根分割条吗（上层按「书签段摊开着」设，见 `EmbedForm.DoLayout`）。
        /// 收起态那一截只剩个标题行，拖它没有意义；不给的话顶上那几像素照旧算标题行的命中。
        /// </summary>
        public bool Resizable
        {
            get { return resizable; }
            set
            {
                if (resizable == value) return;
                resizable = value;
                if (!value) hoverResize = false;
                Invalidate();
            }
        }

        // ---- 竖排纵向滚动条：画 / 命中 / 拖滑块**共用这两份矩形**（各算一次迟早错位）----

        private int VScrollW { get { return Px(VScrollWL); } }

        /// <summary>整条轨道的矩形（装得下、或不是竖排 → Empty）。</summary>
        private Rectangle VTrack()
        {
            if (!vertical) return Rectangle.Empty;
            if (Height - VHeadH < Px(16)) return Rectangle.Empty;
            if (contentHeight <= Height) return Rectangle.Empty;   // 装得下就别摆一根多余的条
            return new Rectangle(Width - VScrollW - Px(1), VHeadH + Px(1),
                                 VScrollW, Height - VHeadH - Px(2));
        }

        /// <summary>滑块的矩形（没轨道 → Empty）。</summary>
        private Rectangle VThumb()
        {
            Rectangle t = VTrack();
            if (t.IsEmpty) return Rectangle.Empty;
            int maxScroll = Math.Max(1, contentHeight - Height);
            int th = (int)((long)t.Height * Height / Math.Max(1, contentHeight));
            if (th < Px(20)) th = Px(20);
            if (th > t.Height) th = t.Height;
            int ty = t.Top + (int)((long)(t.Height - th) * scrollX / maxScroll);
            return new Rectangle(t.Left, ty, t.Width, th);
        }

        /// <summary>竖排里鼠标是不是压在那根**可拖的分割条**上（书签段顶边那几像素）。</summary>
        private bool OnDividerHot(Point p)
        {
            if (!vertical || !resizable) return false;
            return p.Y < Px(VResizeHotL);
        }

        /// <summary>
        /// 竖排时书签段摊开着没。只影响**画**（箭头方向、提示文案）——
        /// 高度由上层按 `BookmarkBand` 决定，两边不能各算一次。
        /// </summary>
        public bool SectionOpen
        {
            get { return sectionOpen; }
            set { if (sectionOpen != value) { sectionOpen = value; Invalidate(); } }
        }
        private bool sectionOpen;

        // ---- 拖动（用户：栏上的项要能调顺序 / 拖进子文件夹）----
        // 按下时只记状态，**动作留到 MouseUp** —— 不这样就没法跟拖动区分（见 OnMouseDown 注释）。
        private int dragIndex = -1;          // 按下时命中的那一项
        private Point dragStart;
        private bool dragging;
        private int dropIndex = -1;          // 插到第几**行**之前（= rows.Count 表示插到末尾）
        private bool dropAfter;              // `dropIndex` 那行的**下半 / 右半** = 插到它后面
        private int dropInto = -1;           // 放进第几行（那一行必须是文件夹）

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

        /// <summary>
        /// 项上松手（文件夹书签）= 让宿主把它开成新标签。
        /// 传**节点**而不是光一个路径：标签条首帧要显示的就是 `Node.Display` —— 用户在手改过书签名之后，
        /// 他认的是书签上那个名字，不是目录名（文件夹名往往还带个 `-dist` 之类的后缀）。
        /// </summary>
        public delegate void NodeEventHandler(FavNode node);
        /// <summary>点了某一**项书签**（文件夹）—— 宿主把它开成新标签页。</summary>
        public event NodeEventHandler ItemClicked;
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
        /// <summary>
        /// 竖排：用户在**标签区与书签段之间那根横条**上拖，报出**新的书签段高度**（设备像素）。
        /// 拖动全程一路报（宿主拿它实时重排），松手才落盘 —— 见 `BandResizeEnded`。
        /// </summary>
        public event Action<int> BandResized;
        /// <summary>那根横条拖完了 —— 宿主这时候才把高度写进设置、广播给其余窗口。</summary>
        public event Action BandResizeEnded;

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
                icons.Clear();           // 图标也是按主题色画的（文件夹那枚金徽）
                if (!IsDisposed) Invalidate();
            };
            // 数据一变（改名 / 增删 / 管理器里拖来拖去）这边就跟着重读
            FavStore.Changed += delegate
            {
                if (!IsDisposed && Visible) Reload();
            };
        }

        /// <summary>现在顶层有几项。</summary>
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
            icons.Clear();
            rows.Clear();
            hoverIndex = -1;
            scrollX = 0;
            try
            {
                foreach (FavNode n in FavStore.BarItems) items.Add(n);
                Diag.Step("书签栏: 载入 " + items.Count + " 项");
            }
            catch (Exception ex) { Diag.Log("书签栏: 载入失败 " + ex.Message); }

            Invalidate();
            if (Reloaded != null) Reloaded(this, EventArgs.Empty);
        }

        /// <summary>节点的图标（缓存）。书签自己的文件夹用我们那颗（蓝文件夹 + 金徽），别跟磁盘上的真文件夹撞脸。</summary>
        private Bitmap IconOf(FavNode n)
        {
            if (n == null) return null;
            Bitmap b;
            if (icons.TryGetValue(n, out b)) return b;
            try { b = n.IsFolder ? ShellIcon.FavFolderIcon(Px(18)) : ShellIcon.PathIcon(n.Path, Px(18)); }
            catch { b = null; }
            icons[n] = b;
            return b;
        }

        // ------------------------------------------------------------------

        private void EnsureLayout()
        {
            // 先把行摊平（横排 = 顶层那一排；竖排 = 按 `expanded` 展开的树）
            rows.Clear();
            for (int i = 0; i < items.Count; i++) AddRow(items[i], 0, i);

            if (vertical)
            {
                // 竖排：不看宽度看高度 —— 一行一个书签，装不下靠纵向滚
                contentHeight = VHeadH + rows.Count * VRowH + Px(4);
                return;
            }
            contentWidth = Px(6);
            for (int i = 0; i < rows.Count; i++)
            {
                Size t = TextRenderer.MeasureText(rows[i].Name, font, new Size(Px(400), Px(20)),
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                int w = Px(6) + Px(18) + Px(5) + Math.Min(t.Width, Px(140)) + Px(8);
                rows[i].W = w;
                contentWidth += w;
            }
            contentWidth += Px(6);
        }

        /// <summary>把一个节点按当前展开状态摊进 `rows`（摊开的文件夹递归往下）。</summary>
        private void AddRow(FavNode n, int depth, int topIndex)
        {
            if (n == null) return;
            Row r = new Row();
            r.Node = n;
            r.Depth = depth;
            r.TopIndex = topIndex;
            r.Expanded = n.IsFolder && expanded.Contains(n);
            rows.Add(r);
            if (r.Expanded)
                for (int i = 0; i < n.Kids.Count; i++) AddRow(n.Kids[i], depth + 1, topIndex);
        }

        /// <summary>
        /// 竖排时这一截「想要多高」（上层拿它给书签区留位置）。
        /// 行数 × 行高 + 顶上那行，不超过 `maxH`（装不下就靠它自己的纵向滚）。
        /// </summary>
        public int PreferredVerticalHeight(int maxH)
        {
            EnsureLayout();     // 摊开状态会改行数，得先摊平再数
            int need = VHeadH + Math.Max(1, rows.Count) * VRowH + Px(4);
            int cap = Math.Max(Px(24), maxH);
            return Math.Max(Px(24), Math.Min(need, cap));
        }

        /// <summary>
        /// 第 i **行**的矩形。
        /// ⚠ `i` 是 `rows` 的下标（横排下 `rows` 和 `items` 一一对应，所以两边通用）。
        /// 竖排还要让出三样：右侧滚动条、嵌套缩进（每层 `Px(12)`，最多 6 层 ——
        /// 不封顶的话层数一深、窗格又窄，文字宽度就成了负数）。
        /// </summary>
        private Rectangle BoundsOf(int i)
        {
            if (i < 0 || i >= rows.Count) return Rectangle.Empty;
            if (vertical)
            {
                // 有滚动条时把书签行让到它左边 —— 画和命中**必须用同一个矩形**，
                // 不然文字会被压在滚动条下面，或者点到看不见的地方去。
                int sb = VTrack().IsEmpty ? 0 : VScrollW + Px(1);
                int ind = Math.Min(rows[i].Depth, 6) * Px(12);
                int room = Math.Max(0, Width - Px(8) - sb - Px(48));
                if (ind > room) ind = room;
                int w = Math.Max(Px(16), Width - Px(8) - sb - ind);
                return new Rectangle(Px(4) + ind, VHeadH + i * VRowH - scrollX, w, VRowH);
            }
            int x = LeadWidth + Px(6) - scrollX;
            for (int k = 0; k < i; k++) x += rows[k].W;
            return new Rectangle(x, Px(4), rows[i].W, Height - Px(8));
        }

        private Rectangle LeadBounds()
        {
            if (vertical)
            {
                // 竖排的「拦名牌」就是顶上那一行：星标 + 「书签」（窗格窄到放不下字时只剩星标）
                return new Rectangle(Px(2), Px(2), Math.Max(Px(16), Width - Px(4)), VHeadH - Px(4));
            }
            return new Rectangle(Px(2), Px(3), LeadWidth - Px(4), Height - Px(6));
        }

        private int HitTest(Point p)
        {
            if (LeadBounds().Contains(p)) return -2;      // -2 = 左边那枚书签图标
            EnsureLayout();
            for (int i = 0; i < rows.Count; i++)
            {
                if (BoundsOf(i).Contains(p)) return i;
            }
            return -1;
        }

        private void ClampScroll()
        {
            if (vertical)
            {
                int maxY = Math.Max(0, contentHeight - Height);
                if (scrollX > maxY) scrollX = maxY;
                if (scrollX < 0) scrollX = 0;
                return;
            }
            int maxX = Math.Max(0, contentWidth - (Width - LeadWidth));
            if (scrollX > maxX) scrollX = maxX;
            if (scrollX < 0) scrollX = 0;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (vertical) { OnPaintV(e); return; }
            EnsureLayout();
            Graphics g = e.Graphics;
            g.FillRectangle(new SolidBrush(TheBack), ClientRectangle);

            // ---- 左边那枚固定的「书签」图标（用户要的）----
            Rectangle lead = LeadBounds();
            if (hoverLead) g.FillRectangle(new SolidBrush(BG(Theme.Hover)), lead);
            Image li = LeadImage();
            if (li != null)
                g.DrawImage(li, new Rectangle(lead.Left + (lead.Width - Px(20)) / 2,
                                              lead.Top + (lead.Height - Px(20)) / 2, Px(20), Px(20)));
            // 图标右边一条淡竖线，跟书签项隔开
            using (Pen p = new Pen(Theme.Border))
                g.DrawLine(p, lead.Right + Px(3), Px(6), lead.Right + Px(3), Height - Px(7));

            // ---- 书签项 ----
            if (rows.Count == 0)
            {
                TextRenderer.DrawText(g, "把文件夹或文件拖到这条栏上就能加进书签",
                    font, new Rectangle(LeadWidth + Px(10), 0, Math.Max(1, Width - LeadWidth - Px(20)), Height),
                    Theme.TextDim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
            else
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    Rectangle r = BoundsOf(i);
                    if (r.Right < LeadWidth || r.Left > Width) continue;
                    if (i == hoverIndex) g.FillRectangle(new SolidBrush(BG(Theme.Hover)), r);

                    Bitmap ic = IconOf(rows[i].Node);
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
                    TextRenderer.DrawText(g, rows[i].Name, font,
                        new Rectangle(tx, r.Top, tw, r.Height),
                        i == hoverIndex ? Theme.Text : Theme.TextDim,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                }

                // 拖动中的落点提示：放进文件夹 = 整项罩一层蓝；调顺序 = 在缝上画一条蓝竖线
                for (int i = 0; i < rows.Count; i++)
                {
                    Rectangle r = BoundsOf(i);
                    if (dropInto == i)
                    {
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(70, Theme.Accent)))
                            g.FillRectangle(b, r);
                    }
                    if (dropIndex == i)
                    {
                        // `dropAfter` = 落在那行的**右半** ⇒ 竖线画在它右边
                        int lx = dropAfter ? r.Right : r.Left;
                        using (Pen p = new Pen(Theme.Accent, Math.Max(2f, DpiScale * 2)))
                            g.DrawLine(p, lx, r.Top, lx, r.Bottom);
                    }
                }
                if (dropIndex >= rows.Count && rows.Count > 0)
                {
                    Rectangle last = BoundsOf(rows.Count - 1);
                    using (Pen p = new Pen(Theme.Accent, Math.Max(2f, DpiScale * 2)))
                        g.DrawLine(p, last.Right, last.Top, last.Right, last.Bottom);
                }
            }

            // 底下一条淡淡的线，跟内容区分开
            g.DrawLine(new Pen(Theme.Border), 0, Height - 1, Width, Height - 1);
        }

        /// <summary>侧边栏半透明那层（`EmbedForm` 摆进来；横排 / 不透明时是 null）。</summary>
        public PaneGlass Glass;

        /// <summary>底色 / 高亮色的填充色：半透明态下带 alpha。图标、文字别用它（见 `GlassPaint`）。</summary>
        private Color BG(Color c) { return GlassPaint.Wash(Glass, c); }

        /// <summary>
        /// 竖排的画法：顶上一行「★ 书签」，下面一行一个书签。
        /// 单开一个方法而不是在 OnPaint 里塞分支 —— 两种排法的绘制几乎不共用，
        /// 混在一起只会让「改横排的顺手弄坏竖排的」。
        ///
        /// 竖排时它住在侧边栏里，摊开那一下也会压到内容上 —— 所以跟窗格走同一套
        /// 「**只让背景透明**，图标文字不透明」（见 `GlassPaint`）。
        /// </summary>
        private void OnPaintV(PaintEventArgs e)
        {
            EnsureLayout();
            Graphics g = e.Graphics;
            GlassPaint.Backdrop(g, this, Glass, TheBack);

            // ---- 顶上那行：星标 + 「书签」（点星标开管理器）----
            Rectangle lead = LeadBounds();
            if (hoverLead) g.FillRectangle(new SolidBrush(BG(Theme.Hover)), lead);
            Image li = LeadImage();
            if (li != null)
                g.DrawImage(li, new Rectangle(lead.Left + Px(5), lead.Top + (lead.Height - Px(16)) / 2, Px(16), Px(16)));
            if (Width > Px(80))
            {
                TextRenderer.DrawText(g, "书签", font,
                    new Rectangle(lead.Left + Px(26), lead.Top, Math.Max(1, lead.Width - Px(46)), lead.Height),
                    Theme.TextDim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
            // 右端一枚小箭头 —— 说明这一行点了能摊开 / 收起（收起态方向朝右，摊开朝下）
            if (Width > Px(80))
            {
                int ax = Width - Px(14), ay = VHeadH / 2;
                Pen ap = new Pen(Theme.TextDim, Math.Max(1f, DpiScale));
                if (sectionOpen) { g.DrawLine(ap, ax - Px(5), ay - Px(2), ax, ay + Px(2)); g.DrawLine(ap, ax, ay + Px(2), ax + Px(5), ay - Px(2)); }
                else { g.DrawLine(ap, ax - Px(2), ay - Px(5), ax + Px(2), ay); g.DrawLine(ap, ax + Px(2), ay, ax - Px(2), ay + Px(5)); }
                ap.Dispose();
            }
            g.DrawLine(new Pen(Theme.Border), 0, VHeadH - 1, Width, VHeadH - 1);

            // ---- 顶上那根**可拖的分割条**：压上去或正在拖时点亮 ----
            // 平时它就是那一条普通边框线（不额外画），鼠标压上来才泛蓝，告诉用户「这条能拖」。
            if (resizable && (hoverResize || resizeDrag))
                using (SolidBrush b = new SolidBrush(Theme.Accent))
                    g.FillRectangle(b, 0, 0, Math.Max(0, Width - 1), Math.Max(1, Px(2)));

            Region oldClip = g.Clip;
            g.SetClip(new Rectangle(0, VHeadH, Width, Math.Max(0, Height - VHeadH)));

            if (rows.Count == 0)
            {
                TextRenderer.DrawText(g, "把文件夹或文件拖到这里就能加进书签", font,
                    new Rectangle(Px(6), VHeadH + Px(2), Math.Max(1, Width - Px(12)),
                                  Math.Max(0, Height - VHeadH - Px(4))),
                    Theme.TextDim, TextFormatFlags.Left | TextFormatFlags.Top |
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            }
            else
            {
                int ic = Px(16);
                // 每行左边先留一列给「▸ / ▾」—— 文件夹画三角、文件留空。
                // 留白是故意的：留了图标才能竖着对齐，不然树的每一层都错位。
                int chev = Px(10);
                bool narrow = Width < Px(120);   // 折叠窗格：一行只剩图标（跟标签行一个规矩）
                for (int i = 0; i < rows.Count; i++)
                {
                    Rectangle r = BoundsOf(i);
                    if (r.Bottom <= VHeadH || r.Top >= Height) continue;
                    if (i == hoverIndex) g.FillRectangle(new SolidBrush(BG(Theme.Hover)), r);

                    // ---- 文件夹的展开三角（▸ 收起 / ▾ 摊开）----
                    if (rows[i].IsFolder)
                    {
                        int cx = r.Left + Px(5), cy = r.Top + r.Height / 2;
                        int a = Px(2), b2 = Px(4);
                        Point[] tri = rows[i].Expanded
                            ? new Point[] { new Point(cx - b2, cy - a), new Point(cx + b2, cy - a), new Point(cx, cy + a) }
                            : new Point[] { new Point(cx - a, cy - b2), new Point(cx + a, cy), new Point(cx - a, cy + b2) };
                        using (SolidBrush sb = new SolidBrush(Theme.TextDim)) g.FillPolygon(sb, tri);
                    }

                    Bitmap bm = IconOf(rows[i].Node);
                    if (bm == null)
                    {
                        if (folderFallback == null) folderFallback = ShellIcon.FolderIcon(Px(16));
                        bm = folderFallback;
                    }
                    int ix = narrow ? r.Left + (r.Width - ic) / 2 : r.Left + chev + Px(2);
                    if (bm != null) g.DrawImage(bm, new Rectangle(ix, r.Top + (r.Height - ic) / 2, ic, ic));
                    if (narrow) continue;

                    int tx = ix + ic + Px(5);
                    int tw = r.Right - Px(8) - tx;
                    if (tw <= 0) continue;
                    TextRenderer.DrawText(g, rows[i].Name, font,
                        new Rectangle(tx, r.Top, tw, r.Height),
                        i == hoverIndex ? Theme.Text : Theme.TextDim,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                }

                // 拖动中的落点提示：放进文件夹 = 整行罩一层蓝；调顺序 = 在那一行的**上沿**画一条蓝横线
                for (int i = 0; i < rows.Count; i++)
                {
                    Rectangle r = BoundsOf(i);
                    if (dropInto == i)
                    {
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(70, Theme.Accent)))
                            g.FillRectangle(b, r);
                    }
                    if (dropIndex == i)
                    {
                        // `dropAfter` = 落在那行的**下半** ⇒ 横线画在它下沿
                        int ly = dropAfter ? r.Bottom : r.Top;
                        using (Pen p = new Pen(Theme.Accent, Math.Max(2f, DpiScale * 2)))
                            g.DrawLine(p, r.Left, ly, r.Right, ly);
                    }
                }
                if (dropIndex >= rows.Count && rows.Count > 0)
                {
                    Rectangle last = BoundsOf(rows.Count - 1);
                    using (Pen p = new Pen(Theme.Accent, Math.Max(2f, DpiScale * 2)))
                        g.DrawLine(p, last.Left, last.Bottom, last.Right, last.Bottom);
                }
            }
            g.Clip = oldClip;

            // ---- 右侧那根纵向滚动条（书签装不下才有）----
            // 滚轮一直能用，但没有条子用户不知道「下面还有」；装上条子才能一眼看出装了多少。
            Rectangle track = VTrack();
            if (!track.IsEmpty)
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(38, Theme.TextDim)))
                    g.FillRectangle(b, track);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(130, Theme.TextDim)))
                    g.FillRectangle(b, VThumb());
            }

            // 右边一条竖线：跟内容区分开（上边那条分隔线归标签区画，这里不重复）
            g.DrawLine(new Pen(Theme.Border), Width - 1, 0, Width - 1, Height);
        }

        /// <summary>
        /// 「书签」那枚图标（MDL2 的实心星星）。
        /// 用户：「用已打开书签栏的那个蓝色图标」+「有点小」——
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

            // ---- 正在拖滚动条滑块（竖排）----
            // 拖动量一律拿**屏幕**坐标算：滑块自己会跟着滚，用控件坐标（相对本控件）会自激。
            if (thumbDrag)
            {
                int dy = Control.MousePosition.Y - thumbY0;
                scrollX = thumbSpan > 0 ? thumbScroll0 + dy * thumbMax / thumbSpan : thumbScroll0;
                ClampScroll();
                Invalidate();
                return;
            }

            // ---- 正在拖顶上那根分割条（竖排）----
            // 这时本控件正跟着变高变矮（顶边贴着鼠标走），同样只能看屏幕坐标；
            // 新高度 = 按下时的高度 - 鼠标往下走了多少（往下拖 = 书签段变矮）。
            if (resizeDrag)
            {
                int newH = resizeH0 - (Control.MousePosition.Y - resizeY0);
                if (newH < MinBandHeight) newH = MinBandHeight;
                if (BandResized != null) BandResized(newH);
                return;
            }

            // ---- 拖动：拖栏上的项调顺序；拖到某个文件夹项上 = **放进那个文件夹** ----
            // （用户：原来拖到书签栏文件夹上只会并排加一个同级项，不是加进文件夹。）
            // ⚠ 这里必须问 `Control.MouseButtons`（现读物理按键状态），**不能用 `e.Button`** ——
            //   WinForms 的 `MouseMove` 事件里那个字段经常是 `None`（它只对 Down/Up 才填得准），
            //   拿它判「左键还按着没」会一直判成「没按」，拖动就永远触发不了。
            if (dragIndex >= 0 && dragIndex < rows.Count && (Control.MouseButtons & MouseButtons.Left) != 0)
            {
                if (!dragging &&
                    (Math.Abs(e.X - dragStart.X) > Px(4) || Math.Abs(e.Y - dragStart.Y) > Px(4)))
                {
                    dragging = true;
                    Diag.Step("书签栏: 开始拖动「" + rows[dragIndex].Name + "」");
                }
                if (dragging)
                {
                    UpdateDrop(e.Location);
                    tips.Hide(this);          // 拖着的时候别弹提示挡视线
                    tipKey = null;
                    return;
                }
            }

            // ---- 竖排：顶上那根分割条 / 右侧那根滚动条 —— 光标形状 + 悬停点亮 ----
            // ⚠ 这一段必须排在 `HitTest` **前面**：分割条压在标题行上、滚动条压在书签行右边，
            //   不先判它们的话鼠标形状和命中都会落到书签项上去。
            EnsureLayout();
            bool hot = OnDividerHot(e.Location);
            Rectangle trk = VTrack();
            Rectangle thb = trk.IsEmpty ? Rectangle.Empty : VThumb();
            bool overBar = !trk.IsEmpty && (trk.Contains(e.Location) || thb.Contains(e.Location));
            if (hot != hoverResize) { hoverResize = hot; Invalidate(); }
            Cursor want = (hot || overBar) ? Cursors.SizeNS : Cursors.Default;
            if (Cursor != want) Cursor = want;
            if (hot || overBar)
            {
                hoverIndex = -1;
                hoverLead = false;
                tips.Hide(this);
                tipKey = null;
                return;
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
                    // 竖排时提示贴到右边（栏本身窄，放下面会压住下一行）
                    int lx = vertical ? r.Right + Px(4) : r.Left;
                    int ly = vertical ? r.Top : r.Bottom + Px(2);
                    // 竖排里这一行是**书签段的标题**：点它摊开 / 收起（管理器挪到右键菜单里）
                    string body = vertical
                        ? (sectionOpen ? "点一下收起书签段" : "点一下摊开书签段")
                        : "把文件夹或文件拖到这条栏上就能加进来；点这儿管理书签";
                    tips.Show("书签\r\n" + body, this, lx, ly, 8000);
                }
                else
                {
                    Rectangle r = BoundsOf(i);
                    int x = r.Left;                       // 横排：提示贴在那项下面、左边对齐
                    int y = r.Bottom + Px(2);
                    if (vertical) { x = r.Right + Px(4); y = r.Top; }
                    else if (x + Px(240) > Width) x = Math.Max(0, Width - Px(240));
                    string body;
                    if (rows[i].IsFolder)
                    {
                        body = "文件夹，里面有 " + rows[i].Node.Kids.Count + " 项";
                        // 竖排里点文件夹 = **原地摊开 / 收起**（用户：「书签里面的文件和文件夹
                        // 就可以直接在原地展开了，这样使用起来比较方便」）；
                        // 横排那条只有 30 像素高，摊不下，还是弹飞到旁边的子菜单。
                        if (vertical) body += rows[i].Expanded ? "（点一下收起）" : "（点一下原地展开）";
                    }
                    else body = rows[i].Node.Path;
                    tips.Show(rows[i].Name + "\r\n" + body, this, x, y, 8000);
                }
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            // 拖着呢（抓着鼠标捕获）—— 这时「离开控件」是假象，状态不能清
            if (resizeDrag || thumbDrag) return;
            hoverIndex = -1;
            hoverLead = false;
            tipKey = null;
            if (hoverResize) hoverResize = false;
            Cursor = Cursors.Default;
            tips.Hide(this);
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            EnsureLayout();
            if (vertical)
            {
                // 收起态这条只剩顶上那行标题，没有可滚的内容 —— 不挡一下的话滚轮会去滚看不见的列表
                if (Height <= VHeadH) return;
                if (contentHeight <= Height) return;
                scrollX -= e.Delta / 3;
                ClampScroll();
                Invalidate();
                return;
            }
            if (contentWidth <= Width - LeadWidth) return;
            scrollX -= e.Delta / 3;     // 一格滚一点，别一滚就飞到头
            ClampScroll();
            Invalidate();
        }

        // ------------------------------------------------------------------
        // 拖放：从文件列表里拖文件夹 / 文件过来（用户要的）
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
                    // 收起态时这个控件只剩顶上那行标题，不加这一句就成了「拖进来了却什么都看不见」。
                    // 竖排里 LeadClicked 的含义就是「摊开 / 收起书签段」（见 EmbedForm 的接线）。
                    if (vertical && !sectionOpen && LeadClicked != null) LeadClicked(this, EventArgs.Empty);
                    // 用户：「像已加入书签这种页面直接有反馈的，也不用右下角通知」——
                    // 新项**立刻出现在这条栏上**，那就是反馈，不再弹气泡。
                    // 下面两条「重复 / 收不了」是**真的什么都没发生**，不说一句就成了「点了没反应」。
                }
                else if (dup > 0 && bad == 0) Toast.Show(NotifyItems.FavDup, "书签", dup == 1 ? "这一项已经在里面了。" : "这些都已经在里面了。");
                else Toast.Show(NotifyItems.FavMarkNoPath, "书签", "这些位置没有真实路径（库 / 虚拟文件夹），收不了。");
            }
            catch (Exception ex)
            {
                Diag.Log("书签栏: 拖放失败 " + ex.Message);
                Toast.Show(NotifyItems.FavAddFail, "书签", "加入失败：" + ex.Message);
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
            dropAfter = false;

            EnsureLayout();

            // ---- 竖排顶上那根分割条：命中它就别去碰书签项了 ----
            if (OnDividerHot(e.Location))
            {
                resizeDrag = true;
                resizeY0 = Control.MousePosition.Y;
                resizeH0 = Height;
                Capture = true;             // 控件自己在动，必须抓住鼠标才能一路收到 MOVE
                return;
            }

            // ---- 竖排右侧那根滚动条：滑块 = 拖着走，轨道 = 翻一页 ----
            Rectangle trk = VTrack();
            if (!trk.IsEmpty)
            {
                Rectangle thb = VThumb();
                if (thb.Contains(e.Location))
                {
                    thumbDrag = true;
                    thumbY0 = Control.MousePosition.Y;
                    thumbScroll0 = scrollX;
                    thumbMax = Math.Max(0, contentHeight - Height);
                    thumbSpan = Math.Max(1, trk.Height - thb.Height);
                    Capture = true;
                    return;
                }
                if (trk.Contains(e.Location))
                {
                    // 点空白轨道 = 往那一侧翻差不多一屏（跟系统滚动条一个手感）
                    scrollX += (e.Y < thb.Top ? -1 : 1) * Math.Max(Px(VRowL), Height * 9 / 10);
                    ClampScroll();
                    Invalidate();
                    return;
                }
            }

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
            //      鼠标捕获收走，看门狗会把菜单关掉 —— 用户报过的「闪一下就没了」就是这个。
            dragIndex = i;
            dragStart = e.Location;
        }

        /// <summary>项上松手（没拖动）= 普通点击 —— 原来这一套写在 `OnMouseDown` 里。</summary>
        private void ActivateItem(int i)
        {
            if (i < 0 || i >= rows.Count) return;
            Row row = rows[i];
            // 文件夹：**竖排原地摊开 / 收起**（用户：「书签里面的文件和文件夹就可以直接在原地展开了，
            // 这样使用起来比较方便」）；横排那条只有 30 像素高，摊不下，还是弹飞到旁边的子菜单。
            if (row.IsFolder)
            {
                if (vertical) { ToggleExpand(row.Node); return; }
                ShowSubMenu(i);
                return;
            }

            string p = row.Node.Path;
            if (FavStore.IsFolder(p))
            {
                if (ItemClicked != null) ItemClicked(row.Node);      // 文件夹 → 新标签
                return;
            }
            try
            {
                // 文件（含 .lnk）→ 交给系统默认程序
                Diag.Step("书签栏: 打开文件 " + p);
                Process.Start(new ProcessStartInfo(p) { UseShellExecute = true });
            }
            catch (Exception ex) { Toast.Show(NotifyItems.FavOpenFail, "打不开", ex.Message); }
        }

        /// <summary>
        /// 竖排：把一个文件夹原地摊开 / 收起，然后重排。
        /// 摊开后如果那一块装不下（屏幕底下剩下的不够），就把这个文件夹**滚进视线** ——
        /// 不然点一下只有底下冒出一行、展开的子项全在屏幕外，看着像「没反应」。
        /// </summary>
        private void ToggleExpand(FavNode folder)
        {
            if (folder == null || !folder.IsFolder) return;

            // ⚠ 先按**现在**的行集找它在第几行，再去切展开状态 ——
            //   反过来（先切再找）拿到的是切完之后的行号，滚动定位会差一截。
            int idx = -1;
            EnsureLayout();
            for (int i = 0; i < rows.Count; i++) if (rows[i].Node == folder) { idx = i; break; }

            bool on = !expanded.Contains(folder);
            if (on) expanded.Add(folder); else expanded.Remove(folder);
            hoverIndex = -1;
            tipKey = null;
            tips.Hide(this);

            EnsureLayout();               // 行集变了，重新摊平
            if (on && idx >= 0)
            {
                // 坐标口径：第 i 行相对「内容区顶部」的位置 = `i*VRowH - scrollX`，
                // 可视高度 = Height - VHeadH。
                int avail = Math.Max(0, Height - VHeadH);
                int rest = (rows.Count - idx) * VRowH;      // 这个文件夹连同它底下全部内容要占多少
                int top = idx * VRowH - scrollX;
                if (top < 0) scrollX = idx * VRowH;         // 它在视线**上面** ⇒ 拉下来
                else if (avail > 0)
                {
                    int want = idx * VRowH + rest - avail;  // 底下装不下 ⇒ 让内容底对齐可视底
                    if (want > 0) scrollX = want;
                }
            }
            ClampScroll();
            Invalidate();
            Diag.Step("书签栏: " + (on ? "原地展开" : "收起") + "「" + FavStore.NameOf(folder) +
                      "」（现在 " + rows.Count + " 行）");
        }

        /// <summary>
        /// 拖动中：算出落点（放进哪个文件夹 / 插到第几项之前）。
        /// 只改状态 + 重画，**不动数据** —— 真挪动在 MouseUp 里做一次。
        /// </summary>
        private void UpdateDrop(Point p)
        {
            EnsureLayout();
            int ni = -1, nInto = -1;
            bool nAfter = false;
            bool overItem = false;

            for (int i = 0; i < rows.Count; i++)
            {
                Rectangle r = BoundsOf(i);
                if (!r.Contains(p)) continue;
                overItem = true;
                if (i == dragIndex) break;                     // 拖回自己身上：什么也不提示
                if (rows[i].IsFolder)
                    nInto = i;                                 // 落点在文件夹上（哪一层都行）= 放进它里面
                else
                {
                    // 横排：左半 / 右半；竖排：上半 / 下半
                    nAfter = vertical ? (p.Y >= r.Top + r.Height / 2) : (p.X >= r.Left + r.Width / 2);
                    ni = i;
                }
                break;
            }

            // 落在最后一行外面那块空白里 = 挪到最末尾
            if (!overItem && rows.Count > 0 && dragIndex >= 0)
            {
                Rectangle last = BoundsOf(rows.Count - 1);
                if (vertical) { if (p.Y >= last.Bottom) { ni = rows.Count; nAfter = false; } }
                else if (p.X >= last.Right) { ni = rows.Count; nAfter = false; }
            }

            if (ni != dropIndex || nInto != dropInto || nAfter != dropAfter)
            {
                dropIndex = ni;
                dropInto = nInto;
                dropAfter = nAfter;
                Invalidate();
            }
        }

        /// <summary>
        /// 把「插到第 `at` 行（之前 / 之后）」换算成「插到**顶层**第几项之前」。
        /// ⚠ 展开之后**行号和顶层下标不是一回事** —— 拿行号直接调 `FavStore.Move` 会挪错位置
        /// （表现是「拖到这儿，结果跑到上面去了」）。
        /// 落在哪一行的下半 / 右半 ⇒ 插到它所属那棵顶层书签的**后面**。
        /// 返回 -1 = 没有有效落点。
        /// </summary>
        private int DropTopIndex(int at, bool after)
        {
            if (at < 0) return -1;
            if (at >= rows.Count) return items.Count;      // 落到末尾
            return rows[at].TopIndex + (after ? 1 : 0);
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
                // 分割条 / 滚动条滑块 —— 收尾（这时才通知宿主存盘，拖动中途只实时重排）
                if (resizeDrag)
                {
                    resizeDrag = false;
                    Capture = false;
                    if (BandResizeEnded != null) BandResizeEnded();
                    return;
                }
                if (thumbDrag)
                {
                    thumbDrag = false;
                    Capture = false;
                    return;
                }

                int di = dragIndex;
                bool wasDrag = dragging;
                int into = dropInto, at = dropIndex;
                bool after = dropAfter;
                dragIndex = -1;
                dragging = false;
                dropInto = -1;
                dropIndex = -1;
                dropAfter = false;

                if (wasDrag)
                {
                    Invalidate();
                    FavNode node = (di >= 0 && di < rows.Count) ? rows[di].Node : null;
                    bool ok = false;
                    if (node != null)
                    {
                        if (into >= 0 && into < rows.Count)            // 放进那个文件夹（追加到末尾）
                            ok = FavStore.Move(node, rows[into].Node, -1);
                        else
                        {
                            int top = DropTopIndex(at, after);
                            if (top >= 0)                              // 调顺序（一律按**顶层**位置算）
                            {
                                FavNode bar = FavStore.BarFolder;
                                ok = bar != null && FavStore.Move(node, bar, top);
                            }
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
            if (index < 0 || index >= rows.Count) return;
            FavNode nd = rows[index].Node;
            PopItem[] kids = ItemsOf(nd);
            if (kids.Length == 0) { Toast.Show(NotifyItems.FavEmptyFolder, nd.Display, "这个文件夹里还没有书签。"); return; }
            Rectangle r = BoundsOf(index);
            // 竖排时子菜单往**右边**弹（栏本身就在最左边，往下列会盖住下一行）
            Point at = vertical ? new Point(r.Right + Px(1), r.Top) : new Point(r.Left, r.Bottom + Px(1));
            string what = "书签子文件夹 " + nd.Display;
            // ⚠ 「推后一轮再弹」这一步现在收在 `PopMenu.Show` 里（用户报的「点书签栏文件夹，
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
                        if (FavStore.IsFolder(p)) { if (ItemClicked != null) ItemClicked(k); }
                        else
                        {
                            try { Process.Start(new ProcessStartInfo(p) { UseShellExecute = true }); }
                            catch (Exception ex) { Toast.Show(NotifyItems.FavOpenFail, "打不开", ex.Message); }
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
                Row it = rows[i];
                string p = it.Node.Path;
                if (it.IsFolder)
                {
                    FavNode nd = it.Node;
                    // 用户：文件夹（含它下面的子文件夹）能「全部打开」，超过 7 项先问一句
                    m.Add(FavActions.OpenAllItem(this, nd, delegate(FavNode f)
                    {
                        if (OpenAllRequested != null) OpenAllRequested(f);
                    }));
                    // 竖排里这一层是**原地**摊开的（跟左键一样），横排那条只有 30 像素高，
                    // 摊不下，还是弹飞到旁边的子菜单。
                    if (vertical)
                        m.Add(PopMenu.It(it.Expanded ? "收起这一层" : "原地展开这一层",
                            delegate { ToggleExpand(nd); }));
                    else
                        m.Add(PopMenu.It("展开这一层", delegate { ShowSubMenu(i); }));
                    m.Add(PopMenu.It("重命名…", delegate { Rename(nd); }));
                    m.Add(PopMenu.Split());
                }
                else
                {
                    if (FavStore.IsFolder(p))
                        m.Add(PopMenu.It("在新标签页打开", delegate { if (ItemClicked != null) ItemClicked(it.Node); }));
                    else
                        m.Add(PopMenu.It("用默认程序打开", delegate
                        {
                            try { Process.Start(new ProcessStartInfo(p) { UseShellExecute = true }); }
                            catch (Exception ex) { Toast.Show(NotifyItems.FavOpenFail, "打不开", ex.Message); }
                        }));
                    m.Add(PopMenu.It("复制完整路径", delegate
                    {
                        try { Clipboard.SetText(p); }
                        catch (Exception ex) { Diag.Log("书签栏: 复制失败 " + ex.Message); }
                    }));
                    // 用户要的：栏上的项能改名（改的是**我们自己这份 json 里记的显示名**，
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
            string title = i >= 0 ? ("书签项右键 " + rows[i].Name) : "书签栏右键";
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
