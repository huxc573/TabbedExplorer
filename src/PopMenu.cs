using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace TabbedExplorer
{
    /// <summary>一条菜单项的**规格**（不是控件）—— 调用方只描述「有什么、点了干什么」。</summary>
    internal sealed class PopItem
    {
        public string Text;
        public Action Run;
        public bool On;        // 打勾（勾选列）
        public bool IsSep;     // 分隔线
        public PopItem[] Kids; // 子菜单（非 null 就是一层下拉）
        /// <summary>
        /// 悬停浮窗提示（两行：显示名 \r\n 完整路径 / 项数）。
        /// 菜单里的项原来没有任何提示（用户：「手动展开后也没有浮窗信息，只有在书签栏图标上才有」）。
        /// 空 = 不提示。
        /// </summary>
        public string Tip;
        /// <summary>
        /// 宿主自己的东西（书签栏放 `FavNode`）—— 右键菜单 / 「拖出菜单」/ 拖文件落点都靠它。
        /// </summary>
        public object Tag;
    }

    /// <summary>
    /// 弹出菜单的**出口**。给调用方的 API 只有三个：`Show` / `It` / `Split` / `Sub`。
    ///
    /// ══════════════════════════════════════════════════════════════════════
    /// 为什么这里**不用** WinForms 那两套菜单（这是第三轮返工，把两次失败都记下来）
    ///
    /// 用户报过三轮：「右键依然无效」→「历史记录点开后，所有功能都不可用」。
    /// 日志 + 探针把结论钉死了（22:01 那次）：
    ///   · 79 次「菜单: 弹出」/ 78 次「菜单: 关闭」—— 菜单**弹得出来也关得掉**；
    ///   · 可 **一条「菜单项: xxx」都没有**（托盘那份菜单有，因为它走的是另一条路）；
    ///   · 而且探针一测，主窗口连 `WM_NULL` 都不回 —— **UI 线程卡死了**，不是「点了没反应」。
    /// 两代实现两种死法：
    ///   · 老的 `ContextMenu` + `MeasureItem` 自绘（`MenuFx.Show`）：`Show()` 是**模态**的，
    ///     模态期间**主窗口被禁用** —— 菜单项又收不到点击，于是菜单关不掉、整个窗口一直是死的。
    ///     用户那句「点开后所有功能都不可用」就是这个，一字不差。
    ///   · 换 `ContextMenuStrip` + `ToolStripProfessionalRenderer` 之后更糟：直接**真卡死**。
    ///
    /// 为什么这两套在这个窗口里都失灵：**我们主窗口里面嵌着别的进程的子窗口**（每个标签一个
    /// explorer.exe）。ToolStrip / ContextMenu 那套弹窗依赖「抢鼠标捕获 + 转前台 + 模态过滤链」，
    /// 而捕获和前台在「宿主窗口里有外来子窗口」的情况下会被搅乱 —— 具体表现就是上面那两种。
    ///
    /// 所以现在**一律自己画**：一个无边框 `Form` 当菜单（`PopMenuWindow`），
    ///   · 非模态 `Show()`（**绝不进嵌套消息循环**，卡死这条路从根上没有）；
    ///   · 自己 `Capture = true` 收鼠标，点哪儿都由我们分发（点菜单外面 = 关掉，跟原生一样）；
    ///   · 自己 `OnPaint`（配色跟程序一套，深色不会漏白底）；
    ///   · 自己按 `PopItem.Kids` 开下一层（子菜单 = 挂在上一层名下的另一个窗体，输入由根那一层转发）。
    /// 用的全是这个程序里已经被验证过的东西（`Form` / `OnPaint` / 鼠标事件），不碰任何 ToolStrip。
    /// ══════════════════════════════════════════════════════════════════════
    /// </summary>
    internal static class PopMenu
    {
        // ------------------------------------------------------------------
        // 规格
        // ------------------------------------------------------------------

        /// <summary>现在有没有一摞菜单开着（书签栏的「悬停展开」靠它判断菜单生死）。</summary>
        internal static bool IsOpen { get { return PopMenuWindow.HasOpen; } }

        /// <summary>
        /// 现在开着的那一摞是不是**拖拽期间的落点菜单**（`dropMode`）。见 `PopMenuWindow.DropModeOpen`。
        /// </summary>
        internal static bool DropModeOpen { get { return PopMenuWindow.DropModeOpen; } }

        /// <summary>
        /// 落地之后要「这个文件夹现在有哪些项」—— 拖拽落点菜单**原地换列表**用
        /// （见 `PopMenuWindow.ReplaceItems`）。参数 = 菜单的 `dropFolder`（书签节点）；
        /// 返回 null = 认不出，菜单就照旧收掉。宿主（书签栏）负责接线。
        /// </summary>
        internal static Func<object, PopItem[]> ItemsFor;

        /// <summary>
        /// 鼠标挪回了**锚控件**上（在菜单外面）——「悬停展开」的菜单抓着鼠标捕获，
        /// 锚控件收不到 MouseMove，得把这次移动转发回去（栏上换目标 = 换菜单 / 收起）。
        /// 参数 = （锚控件，屏幕坐标）。
        /// </summary>
        internal static Action<Control, Point> OwnerHover;

        /// <summary>
        /// 悬停菜单开着时用户在锚控件上**按下**了 —— 转发给栏自己判（点同一个文件夹 = 钉住，
        /// 点别处 = 收掉吞掉）。不转发的话这一下会被当「点在菜单外」吞掉，用户感觉点了空。
        /// </summary>
        internal static Action<Control, Point> OwnerClick;

        /// <summary>
        /// 有文件被拖到**菜单**上松手了（参数 = 落点节点，拖进来的路径们，插到那一层的第几位（负 = 末尾））。
        /// 落点规则跟书签栏那份 `UpdateDrop` **同一套**：
        ///   · 指针在**文件夹那一项**上 = 进那个文件夹（整行罩蓝）；
        ///   · 在**别的项**的上半 / 下半 = 插到它前 / 后（缝上画一条蓝横线），层 = 这个菜单代表的文件夹 `dropFolder`；
        ///   · 在最后一行底下的空白里 = 落到这一层末尾。
        /// ⚠ **整块菜单都要能接**（哪怕指针在文件项上）：只让文件夹行接的话，指针稍偏一点光标就变成
        ///   「不允许」、松手什么都不发生（用户实测栽在这儿）。
        /// </summary>
        internal static Action<object, string[], int> FilesDropped;

        // ---- 菜单里的**书签项**要能像栏上那样右键 / 拖出去，可菜单抓着捕获、栏收不到鼠标事件，
        //      所以跟 `OwnerHover` / `OwnerClick` 一个路数：由菜单把事件转发回锚控件，参数带上项的 `Tag` ----

        /// <summary>
        /// 某一项被**越过了拖动阈值**（真开始拖了）—— 这一刻把拖动**交给锚控件自己跑**：
        /// 宿主接管鼠标捕获，后面完全用它自己那套拖动逻辑（参数 = 屏幕坐标 + 项的 `Tag`）。
        /// ⚠ 别在菜单里自己算落点再转发：菜单的屏幕坐标跟宿主客户区之间隔着窗口和滚动，
        ///   转发过去十有八九判成「落在栏外」（这是量过的一版失败：日志里只有「没动（落点在栏外或无效）」）。
        /// </summary>
        internal static Action<Control, Point, object> OwnerItemDragBegin;

        /// <summary>
        /// 要某一项的**右键菜单项表**（宿主给，菜单负责摆出来）。
        /// ⚠ 摆法是**在那一层菜单自己名下开一份子级菜单**：上面那几层不拆
        ///   （用户：「右键后，临时展开的也收回去了，正常不是鼠标移开才收吗」），
        ///   鼠标挪回上面那几层时由 `Route` 的 `CloseDeeper()` 收掉 —— 就是「移开才收」。
        /// </summary>
        internal static Func<Control, object, PopItem[]> ItemMenuFor;

        public static PopItem It(string text, Action a) { return It(text, a, false); }

        public static PopItem It(string text, Action a, bool on)
        {
            PopItem i = new PopItem();
            i.Text = text;
            i.Run = a;
            i.On = on;
            return i;
        }

        /// <summary>一根分隔线。</summary>
        public static PopItem Split()
        {
            PopItem i = new PopItem();
            i.IsSep = true;
            return i;
        }

        /// <summary>一条带下拉的项。</summary>
        public static PopItem Sub(string text, params PopItem[] kids)
        {
            PopItem i = new PopItem();
            i.Text = text;
            i.Kids = kids;
            return i;
        }

        // ------------------------------------------------------------------
        // 弹出
        // ------------------------------------------------------------------

        /// <summary>
        /// 弹菜单。**不阻塞** —— 把要干的事记在菜单里，窗全关掉之后才执行。
        /// `at` 是 `owner` 的**客户区坐标**（内部换算成屏幕坐标）。
        ///
        /// ⚠⚠ 这里**一律推后一轮**（`BeginInvoke`）再真正开窗，别嫌多一跳 —— 这是
        /// 「菜单弹出来了、可里面点不动」这一类毛病的**根治办法**，也是唯一一处需要修的地方：
        ///
        ///   调用方十有八九是在某个控件的 `MouseUp` / `MouseDown` 里弹菜单。那一刻 WinForms
        ///   还攥着那个控件的鼠标捕获，而且**它会在事件处理返回之后才把捕获放掉** ——
        ///   放的那一刻正好把刚弹出来那个菜单窗口的 `Capture = true` 抢走。菜单一没捕获，
        ///   鼠标消息就散了：悬停不亮、点了不响应，看门狗过一会儿再把它收掉。
        ///   日志里的证据很干净：走 `Defer` 的历史记录 / 齿轮那份菜单一直好使，
        ///   而**同步弹**的那些（书签栏文件夹的子菜单 17 次弹出只命中 1 次）全都时好时坏。
        ///
        ///   推后一轮，这条鼠标消息（含那次捕获释放）已经走完，菜单的捕获就没人抢了。
        ///   本来是要求「每个调用点自己在 MouseUp 里 `BeginInvoke`」，现在收在**这一个口**上，
        ///   以后新增调用点不必再各自记这条规矩。
        /// </summary>
        public static void Show(PopItem[] items, Control owner, Point at, string what)
        { Show(items, owner, at, what, false, false, null); }

        public static void Show(PopItem[] items, Control owner, Point at, string what, bool hoverManaged)
        { Show(items, owner, at, what, hoverManaged, false, null); }

        /// <param name="dropMode">
        /// **拖文件时开的**菜单：不抢鼠标捕获（拖拽循环在那个进程手里，抢过来就把人家的拖动打断了），
        /// 改由拖放消息（`DragOver` / `DragLeave` / `DragDrop`）驱动高亮和子菜单展开。
        /// </param>
        /// <param name="dropFolder">
        /// 这一层菜单代表的那个文件夹（书签栏的文件夹节点）—— 拖文件到这一层上时默认落进它。
        /// 不是书签菜单（历史 / 设置那些）传 null。
        /// </param>
        public static void Show(PopItem[] items, Control owner, Point at, string what,
                                bool hoverManaged, bool dropMode, object dropFolder)
        {
            if (owner == null || items == null || items.Length == 0) return;

            try
            {
                if (owner.IsHandleCreated && !owner.IsDisposed)
                {
                    PopItem[] its = items;
                    Control o = owner;
                    Point p = at;
                    string w = what;
                    bool m = hoverManaged;
                    bool d = dropMode;
                    object df = dropFolder;
                    owner.BeginInvoke((MethodInvoker)delegate { ShowNow(its, o, p, w, m, d, df); });
                    return;
                }
            }
            catch (Exception ex) { Diag.Log("菜单: Defer 失败 " + what + " " + ex.Message); }

            ShowNow(items, owner, at, what, hoverManaged, dropMode, dropFolder);   // 拿不到窗口句柄（理论上不该走到）就同步弹，别干脆不弹
        }

        /// <summary>真正弹出来。只在 `Show` 里被调 —— 那里已经把「推后一轮」这步做完了。</summary>
        private static void ShowNow(PopItem[] items, Control owner, Point at, string what,
                                    bool hoverManaged, bool dropMode, object dropFolder)
        {
            try
            {
                if (owner == null || owner.IsDisposed || items == null || items.Length == 0) return;
                Point screen;
                try { screen = owner.PointToScreen(at); }
                catch { screen = new Point(at.X, at.Y); }
                PopMenuWindow.Open(items, owner, screen, what, hoverManaged, dropMode, dropFolder);
            }
            catch (Exception ex)
            {
                Diag.Log("菜单: 弹出失败 " + what + " " + ex.Message);
            }
        }
    }

    /// <summary>
    /// 菜单的**一层**（一层 = 一个无边框窗体）。见 `PopMenu` 类注释里那段「为什么自己画」。
    ///
    /// 分工：
    ///   · 最底下那层（`root`）拿鼠标捕获，`OnMouseMove` / `OnMouseDown` 都归它；
    ///   · 它把屏幕坐标**从最深一层往浅一层**找，谁包含这个点谁处理（所以子菜单不用自己收消息）；
    ///   · 选中的项不给 `root` 直接执行 —— 先关掉所有层，再执行（免得动作里又弹窗、踩到自己的状态）。
    /// </summary>
    internal sealed class PopMenuWindow : Form
    {
        // ---- 尺寸（逻辑像素，乘 DPI 用）----
        private const int RowH = 26;
        private const int SepH = 7;
        private const int PadV = 4;
        private const int CheckW = 22;      // 左边勾选列
        private const int TextPadL = 8;
        private const int TextPadR = 12;
        private const int ArrowW = 16;      // 右边子菜单箭头列
        private const int MinW = 132;
        private const int MaxW = 620;

        private static readonly float DpiScale = ReadDpi();
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

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

        // ---- 全局（同时只允许一份菜单）----
        private static PopMenuWindow root;

        /// <summary>
        /// 现在开着的这一摞是不是**拖拽期间的落点菜单**（`dropMode`）。
        /// ⚠ 宿主刷新列表时（`FavBar.Reload` / 拖回栏上的 `OnDragEnter`）不能顺手把它收掉 ——
        ///   它得留到鼠标移开才收（用户：「拖进去后临时展开的窗口收起，之前我都说了鼠标移开才收起」）。
        /// </summary>
        internal static bool DropModeOpen
        {
            get { PopMenuWindow r = root; return r != null && r.dropMode; }
        }

        /// <summary>现在有没有一摞菜单开着（书签栏的悬停展开靠它判断生死）。</summary>
        internal static bool HasOpen { get { return root != null; } }
        private static string openWhat;
        private static Action pending;

        private PopItem[] items;
        private readonly string what;
        /// <summary>锚控件（谁把我弹出来的）。悬停展开的菜单拿它判「鼠标是不是挪回了栏上」。</summary>
        private readonly Control anchor;
        /// <summary>
        /// **悬停展开**模式（书签栏文件夹）：鼠标出了「菜单 + 锚控件」两头就自动收摊、
        /// 挪回锚控件上就把移动转发回去（`PopMenu.OwnerHover`）。点击开的菜单不管 —— 手动的照旧等点击。
        /// </summary>
        private readonly bool managed;
        /// <summary>
        /// **拖文件时开的**菜单：不抢捕获，输入靠拖放消息（见 `PopMenu.Show` 的 `dropMode` 说明）。
        /// </summary>
        private readonly bool dropMode;
        /// <summary>这一层菜单代表的文件夹（拖文件落在这一层上默认进它；见 `PopMenu.FilesDropped`）。</summary>
        private readonly object dropFolder;
        private int[] rowTop;
        private int[] rowH;
        private readonly Font font;
        private readonly Font glyph;

        private int hover = -1;
        private PopMenuWindow child;     // 展开的下一层
        private int childOwner = -1;     // 下一层是哪一项开出来的
        /// <summary>
        /// 开出下一层的那一项的 `Tag`（书签节点）。
        /// ⚠ 列表原地刷新后（插入/删除）行号会挪位，`childOwner` 这个下标可能指到别的项 ——
        ///   重新贴子层时靠这个把那一项**重新找到**（找不到 = 那项被删了，才收掉更深的）。
        /// </summary>
        private object childOwnerTag;
        private PopMenuWindow parent;    // 我在哪一层下面（root 为 null）——「只收自己、留着上层」要用
        private Timer watch;
        /// <summary>
        /// 看门狗上一拍看到的鼠标位置。
        /// ⚠ 「鼠标移开才收」不能只看「光标现在在不在菜单里」：刚点完一项 / 刚原地刷新完那一瞬，
        ///   菜单可能变矮了、光标恰好落在它外边 —— 那不是「用户把鼠标移开了」。靠这个位置比对
        ///   （`moved`）挡住那种误判。动作 / 刷新收尾处用 `MarkMouseNow()` 把它抹成当前光标。
        /// </summary>
        private Point lastWatchMouse;

        // ---- 按在「书签项」上之后（松手才算点击 / 拖出去 = 挪位置）----
        private object pressTag;         // 按着的那一项的 `PopItem.Tag`（非空 = 正按着）
        /// <summary>
        /// 按着的那一项**住在哪一层**。
        /// ⚠ 鼠标事件只有 root 收得到（子层的输入靠 root 转发），所以 OnMouseUp 里 `this`/`parent`
        ///   一律是 root 的 —— 要判「这一下是不是点在项菜单里」只能靠这个字段。
        /// </summary>
        private PopMenuWindow pressOwner;
        private Point pressScreen;
        private Action pendingRun;       // 松手没挪动就执行它（点这一项的动作）
        private bool itemDragging;       // 已经拖起来了（超过阈值）

        // ---- 右键待弹的项菜单（松手之后才发，理由见 Route 里那段）----
        private object pendingMenuTag;
        private Point pendingMenuScreen;
        private PopMenuWindow pendingMenuOwner;   // 那一项住哪一层（子级菜单挂在它名下）

        /// <summary>不激活 —— 抢前台会把主窗口的「激活态配色」抖一下，而且菜单本来也不需要焦点。</summary>
        protected override bool ShowWithoutActivation { get { return true; } }

        /// <summary>跟系统菜单一样带一层淡投影，不然贴在深色内容上分不出边界。</summary>
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000;   // CS_DROPSHADOW
                return cp;
            }
        }

        private PopMenuWindow(PopItem[] items, string what, Control anchor, bool managed, bool dropMode, object dropFolder)
        {
            this.items = items;
            this.what = what;
            this.anchor = anchor;
            this.managed = managed;
            this.dropMode = dropMode;
            this.dropFolder = dropFolder;
            AllowDrop = true;          // 拖文件到这一层上松手 = 加进 dropFolder（见 OnDragDrop）

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;                      // 菜单就是该压在最上面（主窗口自己不带 TOPMOST）
            KeyPreview = true;
            BackColor = Theme.MenuBack;
            ForeColor = Theme.Text;
            font = new Font("Segoe UI", Px(12), FontStyle.Regular, GraphicsUnit.Pixel);
            glyph = new Font("Segoe MDL2 Assets", Px(11), FontStyle.Regular, GraphicsUnit.Pixel);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            // ---- 量高度 / 量宽度（只在开窗 / 原地换列表时做一次；鼠标事件里不用再算）----
            rowTop = new int[items.Length];
            rowH = new int[items.Length];
            LayoutItems();
        }

        /// <summary>按当前 `items` 重算行顶 / 行高 / 总宽高（开窗一次；原地换列表再一次）。</summary>
        private void LayoutItems()
        {
            int y = Px(PadV);
            for (int i = 0; i < items.Length; i++)
            {
                rowTop[i] = y;
                rowH[i] = (items[i] == null || items[i].IsSep) ? Px(SepH) : Px(RowH);
                y += rowH[i];
            }
            y += Px(PadV);

            int w = MinW;
            for (int i = 0; i < items.Length; i++)
            {
                PopItem it = items[i];
                if (it == null || it.IsSep) continue;
                int tw = TextW(it.Text);
                int need = Px(2) + Px(CheckW) + Px(TextPadL) + tw +
                           ((HasKids(it)) ? Px(ArrowW) : Px(4)) + Px(TextPadR);
                if (need > w) w = need;
            }
            if (w > MaxW) w = MaxW;
            ClientSize = new Size(w, y);
        }

        /// <summary>
        /// 原地把这一层的列表换成新的 —— 拖文件落地之后用：这一摞**留着不收**（鼠标移开才收），
        /// 但列表得跟上，不然「拖进去了列表里看不见」（里面还有刚加进去那一项）。
        /// 行数 / 列宽都可能变，所以行几何整个重算。
        /// </summary>
        internal void ReplaceItems(PopItem[] newItems)
        {
            ReplaceItems(newItems, true);
        }

        /// <param name="closeDeeper">
        /// true = 顺手把比这一层深的都收掉（默认：拖文件落到这一层后，它下面那几层多半已经不对了）。
        /// ⚠ false = **原样留着下面那几层并把它们重新贴到对应行上** —— 宿主刷新
        ///   （`FavBar.Reload` → `RefreshOpen`）必须是这个模式：否则「拖进展开的子菜单 / 删掉一项」
        ///   会连展开着的那张子菜单一起被收掉（用户报的「拖到子文件夹新增书签后也是收起」的真凶）。
        /// </param>
        internal void ReplaceItems(PopItem[] newItems, bool closeDeeper)
        {
            if (newItems == null || newItems.Length == 0) return;
            if (closeDeeper) CloseDeeper();   // 层数可能变了，比这一层深的那几层先收
            items = newItems;
            rowTop = new int[items.Length];
            rowH = new int[items.Length];
            hover = -1;
            tipRow = -1;
            dropRow = -1; dropInto = -1; dropAfter = false;
            LayoutItems();
            Invalidate();
            if (!closeDeeper) RepositionChild();   // 留着子层：把子层重新贴到它那一行上
            Diag.Step("菜单: 原地刷新（" + items.Length + " 项，" + (what ?? "") + "）");
        }

        /// <summary>
        /// 这一层换过列表之后，把**留着的那张子菜单**重新贴到它原来那一项（`childOwner`）旁边。
        /// 行数 / 行高可能变了（新增/删除项），所以位置得跟着重算；那一项要是没了就把更深的收掉。
        /// </summary>
        private void RepositionChild()
        {
            if (child == null || child.IsDisposed) { child = null; childOwner = -1; childOwnerTag = null; return; }
            // 行可能因为增 / 删挪了位：先按「是哪一项开的」（Tag）重新找行，找不到才收
            if (childOwnerTag != null)
            {
                int found = -1;
                for (int i = 0; i < items.Length; i++)
                    if (items[i] != null && items[i].Tag == childOwnerTag) { found = i; break; }
                if (found < 0) { CloseDeeper(); return; }
                childOwner = found;
            }
            if (childOwner < 0 || childOwner >= items.Length) { CloseDeeper(); return; }
            try
            {
                Point p = new Point(Right - Px(3), Top + rowTop[childOwner]);
                Screen scr;
                try { scr = Screen.FromPoint(p); } catch { scr = Screen.PrimaryScreen; }
                if (p.X + child.Width > scr.WorkingArea.Right)
                    p.X = Math.Max(scr.WorkingArea.Left, Left - child.Width + Px(3));
                if (p.Y + child.Height > scr.WorkingArea.Bottom)
                    p.Y = Math.Max(scr.WorkingArea.Top, scr.WorkingArea.Bottom - child.Height);
                child.PlaceAt(p);
            }
            catch { }
        }

        /// <summary>
        /// 这一层按 `dropFolder` 原地刷新，但**保留**下面那几层（见 `ReplaceItems` 的 `closeDeeper` 注释）。
        /// 返回 false = 刷不出来。
        /// </summary>
        private bool RefreshItemsKeepDeeper()
        {
            if (IsDisposed) return false;
            if (dropFolder == null || PopMenu.ItemsFor == null) return false;
            PopItem[] fresh = null;
            try { fresh = PopMenu.ItemsFor(dropFolder); } catch { }
            if (fresh == null || fresh.Length == 0) return false;
            ReplaceItems(fresh, false);
            return true;
        }

        /// <summary>
        /// 按 `dropFolder` 重新生成这一层的列表（表由宿主给）。
        /// 返回 false = 刷不出来（不是书签菜单 / 这一层已经空了）—— 收不收由调用方定。
        /// </summary>
        private bool RefreshItems()
        {
            if (IsDisposed) return false;
            if (dropFolder == null || PopMenu.ItemsFor == null) return false;
            PopItem[] fresh = null;
            try { fresh = PopMenu.ItemsFor(dropFolder); } catch { }
            if (fresh == null || fresh.Length == 0) return false;
            ReplaceItems(fresh);
            return true;
        }

        /// <summary>从这一层（含）往下全收掉。</summary>
        private void CloseFromHere()
        {
            try
            {
                if (parent != null && !parent.IsDisposed) parent.CloseDeeper();
                else CloseAll(false);
            }
            catch { }
        }

        /// <summary>
        /// 宿主的数据变了（加 / 删 / 改名）：把开着的那一摞**原地刷新**而不是收掉
        /// （用户要的「鼠标移开才收」——原话「删除后展开收起的问题，怎么又出现了？」）。
        /// 逐层按 `dropFolder` 重新生成；刷不出来的那层（不是书签菜单的 / 里面的项空了）
        /// 就从那一层往下收掉。由 `FavBar.Reload` 调。
        /// </summary>
        internal static void RefreshOpen()
        {
            PopMenuWindow r = root;
            if (r == null) return;
            for (PopMenuWindow w = r; w != null; )
            {
                if (w.IsDisposed) return;
                // ⚠ 用「保留子层」那种刷新：刷这一层时**不许**把下面展开着的那几层收掉 ——
                //   不然「拖进展开的子菜单 / 删掉一项」都会顺手把子菜单收了。
                if (!w.RefreshItemsKeepDeeper())
                {
                    Diag.Step("菜单: 刷不出来，从「" + (w.what ?? "") + "」往下收掉");
                    w.CloseFromHere();
                    return;
                }
                w = w.child;          // 子层已经被重新贴好了（RepositionChild），接着往下刷
            }
            MarkMouseNow();           // 菜单刚变过形，别让看门狗把「变矮」当成「鼠标移开」
        }

        private static bool HasKids(PopItem it)
        {
            return it != null && it.Kids != null && it.Kids.Length > 0;
        }

        private static bool IsEnabled(PopItem it)
        {
            if (it == null || it.IsSep) return false;
            return it.Run != null || HasKids(it);
        }

        private int TextW(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            try
            {
                return TextRenderer.MeasureText(s, font, new Size(Px(4096), Px(30)),
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
            }
            catch { return Px(60); }
        }

        // ==================================================================
        // 开 / 关
        // ==================================================================

        /// <summary>弹一份菜单。`atScreen` 是屏幕坐标。</summary>
        public static void Open(PopItem[] items, Control anchor, Point atScreen, string what)
        { Open(items, anchor, atScreen, what, false, false, null); }

        public static void Open(PopItem[] items, Control anchor, Point atScreen, string what, bool hoverManaged)
        { Open(items, anchor, atScreen, what, hoverManaged, false, null); }

        public static void Open(PopItem[] items, Control anchor, Point atScreen, string what,
                                bool hoverManaged, bool dropMode, object dropFolder)
        {
            CloseAll(false);      // 防御：上一份还开着就先收掉（别叠两层）

            PopMenuWindow w = new PopMenuWindow(items, what, anchor, hoverManaged, dropMode, dropFolder);
            w.PlaceAt(atScreen);
            openWhat = what;

            Form owner = (anchor == null) ? null : anchor.FindForm();
            if (owner != null && owner.IsHandleCreated) w.Show(owner);
            else w.Show();

            root = w;
            // ⚠ 拖文件那条路上**绝不能**抢捕获：拖动循环（另一个进程）正攥着鼠标捕获，
            //   抢过来 = 人家的拖动当场断掉（点不动也放不下）。dropMode 里改由拖放消息驱动。
            if (dropMode) w.StartDropWatch();
            else w.BeginCapture();

            Diag.Step("菜单: 弹出 " + what + " at " + atScreen.X + "," + atScreen.Y
                      + "（共 " + items.Length + " 项）");
        }

        /// <summary>放到鼠标点那儿；贴边就自己翻方向，别跑出屏幕。</summary>
        private void PlaceAt(Point screen)
        {
            Screen scr;
            try { scr = Screen.FromPoint(screen); } catch { scr = Screen.PrimaryScreen; }
            Rectangle wa = scr.WorkingArea;
            int x = screen.X, y = screen.Y;
            if (x + Width > wa.Right) x = Math.Max(wa.Left, wa.Right - Width);
            if (y + Height > wa.Bottom) y = Math.Max(wa.Top, screen.Y - Height);
            if (y < wa.Top) y = wa.Top;
            Location = new Point(x, y);
        }

        /// <summary>抢鼠标捕获 + 挂看门狗。可以重复调用（自愈时会再调一次）。</summary>
        private void BeginCapture()
        {
            try
            {
                Capture = true;
                PushTopMost();
            }
            catch { }

            StartWatch();
        }

        /// <summary>
        /// 拖拽中开的菜单（`dropMode`）不抢捕获 —— 只把窗口压到最上面 + 挂看门狗。
        /// 看门狗在这里管的是另一件事：**左键一松（拖动结束 / 被 Esc 取消）就收摊**，
        /// 因为这种情况收不到 `DragLeave`，没有它这摞菜单会粘在屏幕上。
        /// </summary>
        private void StartDropWatch()
        {
            try { PushTopMost(); } catch { }
            StartWatch();
        }

        private void PushTopMost()
        {
            EmbedApi.SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0,
                EmbedApi.SWP_NOMOVE | EmbedApi.SWP_NOSIZE | EmbedApi.SWP_NOACTIVATE |
                EmbedApi.SWP_SHOWWINDOW);
        }

        /// <summary>看门狗（250ms）：捕获 / 拖拽的兜底，见 `Watchdog`。</summary>
        private void StartWatch()
        {
            if (watch == null)
            {
                watch = new Timer();
                watch.Interval = 250;
                watch.Tick += delegate { Watchdog(); };
                watch.Start();
            }
        }

        /// <summary>自愈次数（只抢回来几次，真抢不到就别死循环）。</summary>
        private int healTries;

        /// <summary>
        /// 看门狗：捕获没了就收摊（不然菜单关不掉 = 粘在屏幕上）。
        ///
        /// ⚠ 但「捕获没了」有两种，得分清（用户报「点历史图标，菜单闪一下就没了」）：
        ///   · **真的被别的窗口抢走了** → 该关（这时鼠标通常已经不在菜单上）。
        ///   · **系统在松开按键的那一瞬间收走的** —— 我们的菜单窗口不是前台窗口，
        ///     Windows 只允许「有按键按着的时候」抓着鼠标捕获；在「按着按钮弹菜单」的那条路上，
        ///     松手会把捕获带走，但**鼠标还在菜单里、用户正要挑条目**。这种不能关，得抢回来。
        /// （根治还是得让调用方「松手之后再弹菜单」，但看门狗这里也要宽容一点，
        ///   否则以后再有别的入口在按下时弹，会以一模一样的症状再发作一次。）
        /// </summary>
        private void Watchdog()
        {
            if (root != this) return;
            if (watchSuspend > 0) return;    // 动作在跑（可能是模态弹窗）—— 这期间绝不收菜单

            Point m;
            try { m = Cursor.Position; } catch { m = new Point(int.MinValue, int.MinValue); }
            // 「鼠标移开了没」要看**是不是真挪过**：刚点完一项 / 刚刷新完那一瞬，
            // 菜单可能变矮了、光标恰好落在它外边 —— 那不是「移开」（见下面两段的注释）。
            bool moved = Math.Abs(m.X - lastWatchMouse.X) > Px(2) || Math.Abs(m.Y - lastWatchMouse.Y) > Px(2);

            if (dropMode)
            {
                // 拖拽中开的菜单没捕获可查 —— 拿「左键还按着没」当拖动的生死：
                // 松手 / Esc 取消之后这一摞得自己收掉（那两种情况都收不到 DragLeave）。
                // ⚠ 用 `GetAsyncKeyState` 而不是 `Control.MouseButtons`：拖动循环在**别的进程**手里，
                //   我们这条 UI 线程收不到鼠标消息，`GetKeyState` 那一路（线程队列口径）会读到陈旧的「没按」。
                bool down;
                try { down = (NativeMethods.GetAsyncKeyState(0x01 /*VK_LBUTTON*/) & 0x8000) != 0; }
                catch { down = true; }
                if (down || ContainsScreen(m)) { lastWatchMouse = m; return; }
                if (!moved) return;                       // 左键松了但鼠标没挪过 ⇒ 还没「移开」
                lastWatchMouse = m;
                if (autoArmed) return;                    // 落地后的 3 秒窗口内不动手：只由 autoTimer 收
                Diag.Log("菜单: 拖动结束 + 鼠标移开了，收摊（" + (openWhat ?? "") + "）");
                CloseAll(false);
                return;
            }

            if (Capture) { healTries = 0; lastWatchMouse = m; return; }

            // 按着某一项但捕获在路上丢了：把按压状态清掉（否则悬停高亮会一直不响应）。
            // 不清 pendingRun 的执行 —— 那一下点击已经没了，别放它去执行（免得用户莫名其妙被打开了东西）。
            if (pressTag != null)
            {
                bool held;
                try { held = (NativeMethods.GetAsyncKeyState(0x01) & 0x8000) != 0; }
                catch { held = true; }
                if (!held) { pressTag = null; pressOwner = null; pendingRun = null; itemDragging = false; }
            }

            // ⚠ 收不收**只看鼠标有没有真挪出去**（用户：「鼠标移开才收」，说了三遍）：
            //   · 还在菜单（或锚控件 = 栏）里 ⇒ **永远不收**；捕获抢不回来也只当降级（这条路上
            //     本来就不靠捕获判生死，看的是几何），不再拿「抢失败几次」当判死依据 ——
            //     点一下菜单项、Windows 就会把捕获从我们这儿拿走，抢不回来是常态，不能因此收菜单。
            if (ContainsScreen(m) || InAnchor(m))
            {
                if (healTries <= 3)
                {
                    healTries++;
                    Diag.Log("菜单: 捕获被收走，鼠标还在菜单里 -> 抢回来（第 " + healTries + " 次）");
                    BeginCapture();
                }
                lastWatchMouse = m;
                return;
            }
            if (!moved) return;                            // 没挪过 ⇒ 不算「移开」（菜单刚变矮那一瞬）

            lastWatchMouse = m;
            // 动作 / 落地之后的 3 秒窗口内，**不**拿「鼠标移开」抢收 —— 菜单刚变矮、光标恰好落到外边
            // 是常态（删掉一项、列表就短了），那不算用户想收（川：「删完反应不过来」）。交给 autoTimer。
            if (autoArmed) return;
            Diag.Log("菜单: 鼠标移开了，收摊（" + (openWhat ?? "") + "）");
            CloseAll(true);
        }

        /// <summary>屏幕坐标是不是落在**锚控件**（栏）上 —— 悬停菜单把「挪回栏上」当成还在自己这一亩三分地。</summary>
        private bool InAnchor(Point p)
        {
            if (anchor == null) return false;
            try { return anchor.RectangleToScreen(anchor.ClientRectangle).Contains(p); } catch { return false; }
        }

        /// <summary>
        /// 刚跑完一个动作 / 刚拖放落地：把「上次看到的鼠标位置」按当前光标抹平，
        /// 这样看门狗下一拍不会把「菜单刚变矮、光标恰好落到外边」误判成「鼠标移开」。
        /// </summary>
        internal static void MarkMouseNow()
        {
            PopMenuWindow r = root;
            if (r == null) return;
            try { r.lastWatchMouse = Cursor.Position; } catch { }
        }

        /// <summary>屏幕坐标是不是落在这一摞菜单的任意一层里。</summary>
        internal static bool ContainsScreen(Point p)
        {
            for (PopMenuWindow w = root; w != null; w = w.child)
            {
                try { if (w.Bounds.Contains(p)) return true; } catch { }
            }
            return false;
        }

        /// <summary>关掉整摞菜单。<paramref name="runPending"/> = 关完要不要执行选中那项的动作。</summary>
        internal static void CloseAll(bool runPending)
        {
            PopMenuWindow r = root;
            root = null;
            if (r != null)
            {
                r.CloseChain();
                Diag.Step("菜单: 关闭 " + openWhat);
            }
            Action a = runPending ? pending : null;
            pending = null;
            if (a != null)
            {
                try { a(); }
                catch (Exception ex) { Diag.Log("菜单项失败: " + ex.Message); }
            }
        }

        /// <summary>自己 + 所有更深层的窗体一起关掉（深的先关）。</summary>
        // ---- 悬停模式的「出了两头」延迟收摊 ----
        // 立即收的話跨栏→菜单那 1px 缝时（鼠标两头都不在）菜单会在用户眼前闪没。
        private Timer leaveTimer;

        private void DelayClose()
        {
            if (leaveTimer != null) return;          // 已经在倒计时
            Timer t = new Timer();
            leaveTimer = t;
            t.Interval = 250;
            t.Tick += delegate
            {
                t.Stop();
                try { t.Dispose(); } catch { }
                if (leaveTimer == t) leaveTimer = null;
                if (root != this) return;            // 别的菜单已经接管
                if (watchSuspend > 0) return;        // 有动作在跑（模态弹窗）：别在这个节骨眼上收
                if (root != null && root.autoArmed) return;   // 动作 / 落地后的 2 秒窗口：只由 autoTimer 收
                Point m = Cursor.Position;
                if (ContainsScreen(m)) return;       // 又回菜单里了
                try
                {
                    if (anchor != null && anchor.RectangleToScreen(anchor.ClientRectangle).Contains(m)) return;
                }
                catch { }
                CloseAll(false);
            };
            t.Start();
        }

        private void CancelDelayClose()
        {
            if (leaveTimer == null) return;
            Timer t = leaveTimer;
            leaveTimer = null;
            try { t.Stop(); t.Dispose(); } catch { }
        }

        // ---- 「动作 / 拖放落地之后，停留一会儿自动收」----
        // 以前是「非得鼠标移开才收」= 菜单会一直杵在屏幕上（用户：「现在感觉体验还是很差」）。
        // 改成：动作跑完 / 拖放落地后留 2 秒让用户看清刷新结果，到点自己收；
        // 期间鼠标还在菜单里动（挑下一项 / 再拖一个进来）= 续命（重新计时）。
        // ⚠ 这 3 秒窗口内**不许**看门狗靠「鼠标移开」抢收 —— 菜单刚变矮、光标恰好落到外边，
        //   用户根本反应不过来（川：「删完反应不过来」）。窗口内只由这个计时器收。
        // 只有「动作 / 落地」会把这一条**启用**（`autoArmed`）—— 普通右键菜单照旧等点击，不会自己收。
        // ⚠ 动作可能是**模态弹窗**（重命名 `InputBox` / 合并的确认框）：弹窗期间看门狗会被挂起
        //   （`SuspendWatch`），否则弹窗一冒出来、鼠标不在菜单里，整摞就被收了
        //   （川：「像重命名这样单独窗口也没效果」—— 日志里那句 `菜单: 鼠标移开了，收摊` 就是它）。
        private const int AutoCloseMs = 2000;
        private Timer autoTimer;
        private bool autoArmed;

        /// <summary>启用 / 重置「停留一会儿自动收」的倒计时（只在 root 那层生效）。</summary>
        private void ArmAutoClose()
        {
            if (root != this) return;
            autoArmed = true;
            if (autoTimer == null)
            {
                Timer t = new Timer();
                autoTimer = t;
                t.Interval = AutoCloseMs;
                t.Tick += delegate
                {
                    t.Stop();
                    if (root != this || IsDisposed) return;
                    Diag.Log("菜单: 动作后停留 " + AutoCloseMs + "ms 没动，收摊（" + (openWhat ?? "") + "）");
                    CloseAll(false);
                };
            }
            autoTimer.Stop();     // 重新计时（续命）
            autoTimer.Start();
        }

        /// <summary>动作跑完 / 拖放落地：留一会儿（`AutoCloseMs`）然后自己收。</summary>
        internal static void AutoCloseLater()
        {
            PopMenuWindow r = root;
            if (r != null) r.ArmAutoClose();
        }

        /// <summary>鼠标又回到菜单里（或还在里面动）—— 还给用户 1.5 秒（只在已经启用时续命）。</summary>
        internal static void TouchAutoClose()
        {
            PopMenuWindow r = root;
            if (r != null && r.autoArmed) r.ArmAutoClose();
        }

        private void StopAutoClose()
        {
            autoArmed = false;
            if (autoTimer == null) return;
            try { autoTimer.Stop(); autoTimer.Dispose(); } catch { }
            autoTimer = null;
        }

        // ---- 动作执行期间挂起看门狗 ----
        // 动作可能弹模态对话框（重命名 / 确认框）：弹窗期间消息循环照跑，看门狗也在跑，而鼠标
        // 一定不在菜单里 —— 不挂起的话，弹窗一冒出来整摞菜单就被收了（川：「像重命名这样单独窗口也没效果」）。
        private static int watchSuspend;

        internal static void SuspendWatch() { watchSuspend++; }
        internal static void ResumeWatch() { if (watchSuspend > 0) watchSuspend--; }

        /// <summary>让外面（书签栏）也能喊一嗓子「延迟收摊」—— 拖拽离开锚控件时用。</summary>
        internal static void DelayedClose()
        {
            PopMenuWindow r = root;
            if (r != null) r.DelayClose();
        }

        // ==================================================================
        // 拖文件到菜单项上（`dropMode`）
        // ==================================================================
        // 拖拽期间**没有 MouseMove**（鼠标被那个进程的拖动循环拿走了），所以高亮 / 展开子菜单
        // 全靠拖放消息驱动：DragOver 当 MouseMove 用，DragLeave 当 MouseLeave 用。
        // 另外开菜单时**不抢捕获** —— 抢了就把人家的拖动打断了。

        private Timer dropOpen;          // 悬停在「带子项的那一项」上 → 过一会儿展开下一层
        private int dropOpenRow = -1;
        private int dropRow = -1;        // 蓝横线画在第几行的上/下沿（== items.Length = 落到这一层末尾）
        private bool dropAfter;          // 横线画在那一行的**下沿**（= 插到它后面）
        private int dropInto = -1;       // 放进第几行（那一行是文件夹，整行罩蓝）

        /// <summary>`DragEventArgs.X/Y` 官方是屏幕坐标，但有的容器给的就是客户区坐标 —— 两头都认，
        /// 优先按官方口径（屏幕坐标）转，转出来不在客户区里再当客户区使。</summary>
        private Point DragClientPoint(DragEventArgs e)
        {
            Point raw = new Point(e.X, e.Y);
            Point conv;
            try { conv = PointToClient(raw); } catch { return raw; }
            if (ClientRectangle.Contains(conv)) return conv;
            if (ClientRectangle.Contains(raw)) return raw;
            return conv;
        }

        private static bool HasFileDrop(DragEventArgs e)
        {
            try { return e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop); }
            catch { return false; }
        }

        /// <summary>鼠标底下的行号（不在项上返回 -1）。</summary>
        private int RowAt(DragEventArgs e)
        {
            if (!HasFileDrop(e)) return -1;
            Point c = DragClientPoint(e);
            if (!ClientRectangle.Contains(c)) return -1;
            return HitRow(c);
        }

        /// <summary>
        /// 拖文件时算落点 —— 跟书签栏那份 `UpdateDrop` **同一套口径**：
        /// 文件夹行 = 进那个文件夹；别的行的上半 / 下半 = 插到它前 / 后（层 = `dropFolder`）；
        /// 最后一行底下的空白 = 落到这一层末尾。只改状态 + 重画，落地在 `OnDragDrop` 里做一次。
        /// </summary>
        private void UpdateDropMark(Point c)
        {
            int ni = -1, nInto = -1;
            bool nAfter = false;

            for (int i = 0; i < items.Length; i++)
            {
                PopItem it = items[i];
                if (it == null || it.IsSep) continue;
                if (c.Y < rowTop[i] || c.Y >= rowTop[i] + rowH[i]) continue;
                FavNode f = it.Tag as FavNode;
                if (f != null && f.IsFolder)
                    nInto = i;                                   // 文件夹行 = 进那个文件夹（跟栏上一致）
                else
                {
                    nAfter = (c.Y >= rowTop[i] + rowH[i] / 2);   // 上半 = 插它前面，下半 = 插它后面
                    ni = i;
                }
                break;
            }

            // 落在最后一行底下的空白里 = 落到这一层末尾
            if (ni < 0 && nInto < 0 && ClientRectangle.Contains(c))
            {
                int last = LastRow();
                if (last >= 0 && c.Y >= rowTop[last] + rowH[last]) ni = items.Length;
            }

            if (ni != dropRow || nAfter != dropAfter || nInto != dropInto)
            {
                dropRow = ni;
                dropAfter = nAfter;
                dropInto = nInto;
                Invalidate();
            }
        }

        /// <summary>最后一个真行（跳过 null / 分隔条）；没有返回 -1。</summary>
        private int LastRow()
        {
            for (int i = items.Length - 1; i >= 0; i--)
                if (items[i] != null && !items[i].IsSep) return i;
            return -1;
        }

        /// <summary>把落点标识换算成「插到 `dropFolder` 这一层的第几位」（-1 = 末尾 / 认不出）。</summary>
        private int DropIndexFromMark()
        {
            if (dropRow < 0 || dropRow >= items.Length) return -1;   // 没落点 / 末尾 = 追加
            FavNode f = dropFolder as FavNode;
            // 行号和这一层的孩子下标必须一一对应 —— 对不齐（万一以后菜单里加了分隔条）就别猜，退回追加。
            if (f == null || items.Length != f.Kids.Count) return -1;
            return dropAfter ? dropRow + 1 : dropRow;
        }

        protected override void OnDragEnter(DragEventArgs e)
        {
            base.OnDragEnter(e);
            CancelDelayClose();       // 拖回菜单里了：作废「出两头自动收摊」的倒计时
            TouchAutoClose();         // 又拖着东西回到菜单上 —— 动作后的自动收再给 1.5 秒
            e.Effect = (HasFileDrop(e) && PopMenu.FilesDropped != null && dropFolder != null)
                       ? DragDropEffects.Copy : DragDropEffects.None;
        }

        protected override void OnDragOver(DragEventArgs e)
        {
            base.OnDragOver(e);
            CancelDelayClose();
            TouchAutoClose();

            int i = RowAt(e);
            PopItem it = (i >= 0) ? items[i] : null;
            // ⚠ **整块菜单都要能接**：只让文件夹行接的话，指针稍偏一点（落到文件行上）光标就变成
            //   「不允许」、松手什么都不发生 —— 用户实测就是这么栽的。落进哪一层由 `UpdateDropMark` 定。
            bool canDrop = HasFileDrop(e) && PopMenu.FilesDropped != null && dropFolder != null;
            e.Effect = canDrop ? DragDropEffects.Copy : DragDropEffects.None;

            if (!canDrop) { SetHover(-1); dropRow = -1; dropInto = -1; CloseDeeper(); StopDropOpen(); return; }

            // 落点标识跟书签栏同一套：文件夹行 = 进它（整行罩蓝）；别的行看上 / 下半 = 插到它前 / 后（缝上画横线）。
            // 不用 hover 高亮 —— 两种标识叠在同一行上，分不清是「进它」还是「插到它旁边」。
            SetHover(-1);
            UpdateDropMark(DragClientPoint(e));
            if (it != null && HasKids(it))
            {
                // 悬停在子文件夹那一项上 = 过一会儿把这个子文件夹也展开当落点（继续往深一层）
                if (!HasOpenChild(i))
                {
                    if (dropOpenRow != i) { StopDropOpen(); dropOpenRow = i; StartDropOpen(); }
                }
                else StopDropOpen();
            }
            else
            {
                StopDropOpen();
                CloseDeeper();
            }
        }

        protected override void OnDragLeave(EventArgs e)
        {
            base.OnDragLeave(e);
            StopDropOpen();
            SetHover(-1);
            if (dropRow >= 0 || dropInto >= 0) { dropRow = -1; dropInto = -1; dropAfter = false; Invalidate(); }
            // 拖出这一层：可能是拖去隔壁那一层（父子菜单之间来回），也可能整摞都不要了 ——
            // 交给延迟复核（鼠标还在「菜单 + 锚控件」两头就不收）。dropMode 才管，手动的照旧等点击。
            if (dropMode) DelayClose();
        }

        protected override void OnDragDrop(DragEventArgs e)
        {
            base.OnDragDrop(e);
            StopDropOpen();

            // 兜底：万一没收到 Over（拖动循环有时只给 Drop），现算一遍落点
            if (dropRow < 0 && dropInto < 0) UpdateDropMark(DragClientPoint(e));

            object node;
            int index;
            if (dropInto >= 0 && dropInto < items.Length)
            {
                node = items[dropInto].Tag;      // 文件夹行 = 进那个文件夹（追加到它末尾）
                index = -1;
            }
            else
            {
                node = dropFolder;               // 这一层 = 这个菜单代表的那个文件夹
                index = DropIndexFromMark();
            }

            string[] paths = null;
            try { paths = (string[])e.Data.GetData(DataFormats.FileDrop); } catch { }

            if (node != null && paths != null && paths.Length > 0 && PopMenu.FilesDropped != null)
            {
                string where = (dropInto >= 0 && dropInto < items.Length)
                             ? ("进「" + items[dropInto].Text + "」")
                             : (index >= 0 ? ("插到这一层第 " + index + " 位") : "落这一层末尾");
                Diag.Step("菜单: 拖入 " + paths.Length + " 项 -> " + where + "（" + (what ?? "") + "）");
                try { PopMenu.FilesDropped(node, paths, index); }
                catch (Exception ex) { Diag.Log("菜单: 拖放失败 " + ex.Message); }
            }
            // ⚠ 落地之后**不收菜单**（用户：「拖进去后临时展开的窗口收起，之前我都说了鼠标移开才收起」）。
            //   这一摞是拖拽期间开的（`dropMode`），没有捕获可查 —— 看门狗那条
            //   「左键松了 + 鼠标不在菜单里 → 收摊」正好就是这个语义：松手时鼠标还在菜单里 ⇒ 不收
            //   （可以接着再拖一个进来），鼠标一离开 ⇒ 250ms 内收掉。
            //   手动点开的那份菜单不归这条管，照旧收掉（它的生死本来归点击 / 捕获那套）。
            if (dropMode)
            {
                dropRow = -1; dropInto = -1; dropAfter = false;
                SetHover(-1);
                Invalidate();
                // 列表得跟上：留着不收，但刚加进去的那一项要出现在里面（不然「拖进去了列表里看不见」）。
                // ⚠ 用**保留子层**的那种：落进这层只改这层的列表，下面展开着的子菜单内容没变，不该收
                //   （收了就是「拖到 1 级和拖到 2 级效果不一样」—— 拖 2 级时它自己就是最深的）。
                PopItem[] fresh = null;
                try { if (PopMenu.ItemsFor != null && dropFolder != null) fresh = PopMenu.ItemsFor(dropFolder); }
                catch { }
                if (fresh != null && fresh.Length > 0) ReplaceItems(fresh, false);
                MarkMouseNow();       // 列表刚变过形，别让看门狗把「变矮」当成「鼠标移开」
                // ⚠ 落地**不**起超时（川：「拖进去的动作不能单独管理，不要2秒的超时」→ 定「鼠标移开才收」）：
                //   交给看门狗的 dropMode 分支 —— 鼠标还在菜单里就永不收，一移开就收。
                //   2 秒那条只归「点删除/重命名」那种动作（见 RunInChildAndKeepAncestors）。
                return;
            }
            CloseAll(false);
        }

        private void StartDropOpen()
        {
            if (dropOpen == null)
            {
                Timer t = new Timer();
                dropOpen = t;
                t.Interval = 500;
                t.Tick += delegate
                {
                    t.Stop();
                    int i = dropOpenRow;
                    dropOpenRow = -1;
                    if (IsDisposed || !Visible) return;
                    if (i < 0 || i >= items.Length) return;
                    if (!HasKids(items[i])) return;
                    if (HasOpenChild(i)) return;
                    if (OpenChild(i)) ShowTip(i);
                };
            }
            dropOpen.Start();
        }

        private bool HasOpenChild(int i) { return child != null && childOwner == i; }

        private void StopDropOpen()
        {
            dropOpenRow = -1;
            if (dropOpen != null) dropOpen.Stop();
        }

        private void CloseChain()
        {
            PopMenuWindow c = child;
            child = null; childOwner = -1;
            if (c != null) c.CloseChain();
            try { if (watch != null) { watch.Stop(); watch.Dispose(); watch = null; } } catch { }
            CancelDelayClose();
            if (tips != null) { try { tips.Hide(this); } catch { } }
            if (tipRow >= 0) tipRow = -1;
            try { Capture = false; } catch { }
            try { Close(); } catch { }
        }

        /// <summary>
        /// 某一层（不是根那层）里跑一个动作：**只收掉那一层**，它上面那层留着并刷列表。
        /// 顺序：先收那一层（这时上层那份还完整）→ 再跑动作（动作改数据 → 宿主 `Reload` →
        /// 那边会调 `RefreshOpen` 把开着的这层原地刷一遍）→ 回来自己再刷一次保险（幂等）。
        /// </summary>
        private void RunInChildAndKeepAncestors(PopMenuWindow own, Action run)
        {
            PopMenuWindow up = own.parent;
            Diag.Step("菜单: 只收这一层（" + (own.what ?? "") + "），留着「" + (up != null ? up.what : "") + "」并刷新");
            try
            {
                if (up != null && !up.IsDisposed) up.CloseDeeper();   // 摘掉那一层（连更深的）
                else own.CloseDeeper();
            }
            catch { }
            try
            {
                SuspendWatch();       // ⚠ 动作可能弹模态对话框：这期间看门狗不许收菜单
                try { if (run != null) run(); }
                catch (Exception ex) { Diag.Log("菜单项失败: " + ex.Message); }
                finally { ResumeWatch(); }
            }
            catch { }
            try { if (up != null && !up.IsDisposed) up.RefreshItems(); } catch { }
            MarkMouseNow();   // 刚跑完动作、列表刚刷过，别让看门狗把「变矮」当成「鼠标移开」
            AutoCloseLater(); // 然后留 1.5 秒（鼠标还在菜单里动就续命），到点自己收
        }

        /// <summary>把比这一层更深的都收掉（鼠标挪回了浅的地方）。</summary>
        private void CloseDeeper()
        {
            if (child == null) return;
            PopMenuWindow c = child;
            child = null; childOwner = -1; childOwnerTag = null;
            c.CloseChain();
        }

        /// <summary>
        /// 在**这一层自己名下**开一份子级菜单（右键某一项那份）。
        /// 跟 `OpenChild` 的区别：不绑在某一项上（`childOwner = -1`）、位置由调用方给（鼠标那儿），
        /// 而且**上面那几层照旧开着** —— 鼠标挪回上面那几层时 `Route` 会 `CloseDeeper()` 收掉它，
        /// 就是用户要的「移开才收」。
        /// </summary>
        private void OpenSubMenuAt(PopItem[] items, Point atScreen)
        {
            if (items == null || items.Length == 0) return;
            CloseDeeper();
            StopDropOpen();
            PopMenuWindow c = new PopMenuWindow(items, what + " ▸ 项菜单", null, false, false, null);
            c.PlaceAt(atScreen);
            c.Show(this);
            child = c;
            childOwner = -1;
            childOwnerTag = null;
            c.parent = this;
            Diag.Step("菜单: 项右键菜单（共 " + items.Length + " 项）");
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            StopDropOpen();
            StopAutoClose();
            CancelDelayClose();
            try { if (font != null) font.Dispose(); } catch { }
            try { if (glyph != null) glyph.Dispose(); } catch { }
            try { if (tips != null) { tips.Dispose(); tips = null; } } catch { }
            try { if (dropOpen != null) { dropOpen.Dispose(); dropOpen = null; } } catch { }
            base.OnFormClosed(e);
        }

        // ==================================================================
        // 子菜单
        // ==================================================================

        /// <summary>开下一层。返回是不是**真的开了**（同一项已经开着就什么都不做，返 false）。</summary>
        private bool OpenChild(int index)
        {
            if (child != null && childOwner == index) return false;
            CloseDeeper();

            PopItem it = items[index];
            PopMenuWindow c = new PopMenuWindow(it.Kids, what + " ▸ " + it.Text, null, false, dropMode,
                                                (it.Tag is FavNode) ? it.Tag : dropFolder);
            Point p = new Point(Right - Px(3), Top + rowTop[index]);
            Screen scr;
            try { scr = Screen.FromPoint(p); } catch { scr = Screen.PrimaryScreen; }
            if (p.X + c.Width > scr.WorkingArea.Right) p.X = Math.Max(scr.WorkingArea.Left, Left - c.Width + Px(3));
            if (p.Y + c.Height > scr.WorkingArea.Bottom)
                p.Y = Math.Max(scr.WorkingArea.Top, scr.WorkingArea.Bottom - c.Height);
            c.PlaceAt(p);
            c.Show(this);               // 挂在**这一层**名下（z 序 / 生命周期跟着走）
            child = c; childOwner = index; childOwnerTag = it.Tag;
            c.parent = this;
            return true;
        }

        // ==================================================================
        // 输入（只有 root 收得到；子层由 root 转发）
        // ==================================================================

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (this != root) return;
            Route(PointToScreen(e.Location), false, MouseButtons.None);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (this != root) return;
            Route(PointToScreen(e.Location), true, e.Button);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (this != root) return;

            // ① 按着的书签项松手了：拖出去 = 已经交给宿主了（见 Route 的阈值分支）；没挪动 = 一次普通点击
            if (pressTag != null)
            {
                object tag = pressTag;
                Action run = pendingRun;
                PopMenuWindow own = pressOwner;
                pressTag = null;
                pressOwner = null;
                pendingRun = null;
                itemDragging = false;
                if (run != null)
                {
                    Diag.Step("菜单项: " + openWhat);
                    // ⚠ 「鼠标移开才收」：**项菜单**里点一项，只收掉项菜单这一层；
                    //   上面那层书签子菜单留着，并把列表原地刷新（删了一项 / 改完名要跟着变）。
                    //   用户：「删除后展开收起的问题，怎么又出现了？」「干脆改成点下右键菜单不收起窗口」。
                    //   ⚠ 判据必须看 `pressOwner`（那一项住在哪一层）—— 鼠标事件只有 root 收得到，
                    //     `this` / `parent` 都是 root 的，看不出来（上一版就栽在这儿）。
                    if (own != null && own != root && !own.IsDisposed) RunInChildAndKeepAncestors(own, run);
                    else { pending = run; CloseAll(true); }   // 那一项就在根那层（栏上右键那种）：老规矩
                }
                return;
            }

            // ② 右键点过某一项：现在才摆它的右键菜单（摆成**这一层的子级菜单**，上面几层不拆）
            if (pendingMenuTag != null)
            {
                object tag = pendingMenuTag;
                Point sp = pendingMenuScreen;
                PopMenuWindow ow = pendingMenuOwner;
                pendingMenuTag = null;
                pendingMenuOwner = null;

                Func<Control, object, PopItem[]> f = PopMenu.ItemMenuFor;
                PopItem[] its = (f != null && anchor != null) ? f(anchor, tag) : null;
                if (its == null || its.Length == 0) { CloseAll(false); return; }
                if (ow == null || ow.IsDisposed) { CloseAll(false); return; }
                ow.OpenSubMenuAt(its, sp);
                return;
            }
        }

        /// <summary>把一次鼠标事件送到「命中那一层」。</summary>
        private void Route(Point screen, bool down, MouseButtons btn)
        {
            // 按在书签项之后：这条路上只剩「拖出去了没」这一件事
            //（按着键期间不做悬停高亮 —— 原生菜单也是这个脾气）。
            if (pressTag != null)
            {
                if (down) return;
                if (!itemDragging)
                {
                    Size ds;
                    try { ds = SystemInformation.DragSize; } catch { ds = new Size(4, 4); }
                    if (Math.Abs(screen.X - pressScreen.X) > ds.Width / 2 ||
                        Math.Abs(screen.Y - pressScreen.Y) > ds.Height / 2)
                    {
                        itemDragging = true;
                        Diag.Step("菜单: 开始把这一项拖出去（" + (openWhat ?? "") + "）");
                        // 交给锚控件自己拖：它会收掉这一摞菜单、自己抢捕获，
                        // 之后跟栏内拖动走完全同一条路（落点高亮 / 松手落地都在栏那边）。
                        object tag = pressTag;
                        pressTag = null;
                        pressOwner = null;
                        pendingRun = null;
                        itemDragging = false;
                        Action<Control, Point, object> h = PopMenu.OwnerItemDragBegin;
                        if (h != null && anchor != null) h(anchor, screen, tag);
                    }
                }
                return;
            }

            // 拖拽中开的菜单（dropMode）不吃鼠标消息：这会儿鼠标归那个进程的拖动循环，
            // 我们这边的 MouseMove 只是零星的残渣 —— 顺着它走会把菜单在拖动中途收掉（闪一下又弹回来）。
            // 拖动结束自有着门狗收摊（见 `Watchdog`）。
            if (dropMode)
            {
                bool held;
                try { held = (NativeMethods.GetAsyncKeyState(0x01) & 0x8000) != 0; }
                catch { held = true; }
                if (held) return;
            }

            // 从最深一层往浅的找 —— 子菜单压在父菜单上面，得先问它
            List<PopMenuWindow> chain = new List<PopMenuWindow>();
            for (PopMenuWindow w = root; w != null; w = w.child) chain.Add(w);

            PopMenuWindow target = null;
            int idx = -1;
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                Point c;
                try { c = chain[i].PointToClient(screen); } catch { continue; }
                if (chain[i].ClientRectangle.Contains(c)) { target = chain[i]; idx = chain[i].HitRow(c); break; }
            }

            if (target == null)
            {
                bool inAnchor = false;
                if (managed && anchor != null)
                {
                    try { inAnchor = anchor.RectangleToScreen(anchor.ClientRectangle).Contains(screen); }
                    catch { }
                }

                if (down)
                {
                    // 悬停菜单开着时在栏上**按下**：菜单抓着捕获，这一下本来会被当「点在菜单外」
                    // 吞掉（菜单一关，用户感觉点了空 = 「手动点击没有固定展开」）。转发给栏自己判：
                    // 点在同一个文件夹上 = 把菜单**钉住**（转手动）；点别处 = 收掉吞掉（跟原生菜单一致）。
                    if (inAnchor)
                    {
                        Action<Control, Point> h = PopMenu.OwnerClick;
                        if (h != null) { CancelDelayClose(); h(anchor, screen); return; }
                    }
                    CloseAll(false);
                    return;
                }

                // 悬停展开的菜单（书签栏文件夹）：
                //   · 鼠标挪回了锚控件（栏）上 —— 菜单抓着捕获，栏收不到 MouseMove，
                //     把这次移动转发回去（栏上换目标 = 换菜单 / 收起，见 `FavBar.OnFlyOwnerHover`）；
                //   · 挪出了「栏 + 菜单」两头 —— **延迟**收摊（「移出后自动收起」就靠这条）。
                //     不立即收是因为栏和菜单之间有缝（菜单比栏顶下沿低 1px 起），
                //     跨缝那几帧鼠标两头都不在 —— 立即收 = 菜单永远够不着。点击开的菜单（managed=false）
                //     不动，照旧等点击 / 看门狗。
                if (managed && anchor != null)
                {
                    if (inAnchor)
                    {
                        CancelDelayClose();
                        Action<Control, Point> h = PopMenu.OwnerHover;
                        if (h != null) h(anchor, screen);
                        return;
                    }
                    DelayClose();
                }
                return;
            }

            CancelDelayClose();       // 回到菜单里了：倒计时作废
            TouchAutoClose();         // 还在菜单里挑（动作后的自动收）—— 再给 1.5 秒

            if (idx < 0) { target.CloseDeeper(); target.SetHover(-1); return; }
            PopItem it = target.items[idx];

            if (!IsEnabled(it)) { target.CloseDeeper(); target.SetHover(-1); return; }
            target.SetHover(idx);

            // 右键：**先别弹也别关** —— 等松手再弹这项的右键菜单。按着键就弹的话，
            // 紧接着那条「松开右键」消息会投到刚弹出来的菜单上，被它当「点在别处」当场关掉
            // （书签栏上那条硬规矩在这儿一样成立）。宿主没接这个钩子就照旧收摊。
            if (down && btn != MouseButtons.Left)
            {
                if (it.Tag != null && PopMenu.ItemMenuFor != null)
                {
                    pendingMenuTag = it.Tag;
                    pendingMenuScreen = screen;
                    pendingMenuOwner = target;
                }
                else CloseAll(false);
                return;
            }

            // 左键按下：带 `Tag` 的项（书签项）**先不执行** —— 等松手才知道是「拖出去换位置」还是「点这一下」。
            // 放在「有没有子菜单」之前：子文件夹那一项也要能拖出去。
            if (down && btn == MouseButtons.Left && it.Tag != null && PopMenu.OwnerItemDragBegin != null)
            {
                pressTag = it.Tag;
                pressOwner = target;          // 这一项住在哪一层（见 OnMouseUp 里那段）
                pressScreen = screen;
                pendingRun = it.Run;
                itemDragging = false;
                Diag.Step("菜单: 按住项「" + it.Text + "」（松手没挪动 = 点它，挪了 = 拖出去）");
                return;
            }

            if (HasKids(it))
            {
                // 悬停就展开（原生菜单也是）。
                // ⚠ **这里绝不能先 `CloseDeeper()`**：鼠标在「带子项的那一项」上稍微动一下就是一次
                //   MouseMove，先关再开 = 每移动一次就**销毁并重建**一个子菜单窗体 ——
                //   表现就是「移到有子项的子文件夹上卡顿」+「有时子项根本不出现」（刚建好又被下一步关掉）。
                //   该不该关交给 `OpenChild` 自己判（是同一项开的就原样留着，什么都不做）。
                // ⚠ 开子菜单那一下会把刚挂的浮窗提示收掉 —— 只在**真开了**的那一次补挂（同位置同文本，
                //   看不出闪）；千万别每次移动都补：原生 Show 同参反复挂 = 川看到的「一闪一闪」。
                if (target.OpenChild(idx)) target.ShowTip(idx);
                return;
            }

            target.CloseDeeper();       // 悬停在普通项上：更深的那几层收掉
            if (!down) return;

            if (btn == MouseButtons.Left)
            {
                Diag.Step("菜单项: " + it.Text);
                // ⚠ 「鼠标移开才收」（川：「干脆改成点下右键菜单不收起窗口」）：**不在根那层的项菜单**里
                //   点一个动作（从书签移除 / 重命名…），只收掉项菜单这一层，上面那层书签子菜单
                //   留着并原地刷新（删了一项 / 改完名要跟着变）。在根那层（栏上右键那种）走老规矩。
                //   ⚠ 判据必须看 `target`（鼠标底下那一层）—— 鼠标事件只有 root 收得到，`this` 看不出来。
                if (target != root && target.parent != null) RunInChildAndKeepAncestors(target, it.Run);
                else { pending = it.Run; CloseAll(true); }
            }
            else
            {
                CloseAll(false);                    // 右键 / 中键点在我们身上：收掉就行
            }
        }

        private int HitRow(Point client)
        {
            for (int i = 0; i < rowTop.Length; i++)
            {
                if (items[i] == null || items[i].IsSep) continue;
                if (client.Y >= rowTop[i] && client.Y < rowTop[i] + rowH[i]) return i;
            }
            return -1;
        }

        private void SetHover(int i)
        {
            if (hover == i) return;
            hover = i;
            Invalidate();
            // 拖拽中不弹提示：提示窗会飘在项下面那块**落点**上，拖动循环拿它做命中测试时
            // 光标会忽然变成「不可放」—— 高亮已经够说明落在哪一项了。
            if (!dropMode) ShowTip(i);
        }

        // ---- 悬停浮窗提示（菜单里的项原来什么提示都没有）----
        // 用 `ToolTip.Show` 手动弹（跟书签栏自己那套一个路数），不用 SetToolTip ——
        // 项是我们自己画的，ToolTip 的自动命中认不出它们。
        private ToolTip tips;
        private int tipRow = -1;

        private void ShowTip(int i)
        {
            if (tips == null)
            {
                tips = new ToolTip();
                tips.InitialDelay = 0;
                tips.ReshowDelay = 0;
                tips.ShowAlways = true;
                Theme.StyleTip(tips);
            }
            bool valid = i >= 0 && i < items.Length && items[i] != null && !items[i].IsSep
                         && !string.IsNullOrEmpty(items[i].Tip);
            if (!valid)
            {
                if (tipRow >= 0) { try { tips.Hide(this); } catch { } tipRow = -1; }
                return;
            }
            if (tipRow >= 0 && tipRow != i) { try { tips.Hide(this); } catch { } }
            try
            {
                // 同一项重复 Show 是故意的：开子菜单那一下会把原生的提示收掉，
                // 每次移动都重新挂一遍（位置 / 文本不变，看不出闪）才留得住。
                tips.Show(items[i].Tip, this, Px(10), rowTop[i] + rowH[i] + Px(2), 5000);
                tipRow = i;
            }
            catch { }
        }

        // ==================================================================
        // 画
        // ==================================================================

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.FillRectangle(new SolidBrush(Theme.MenuBack), ClientRectangle);

            for (int i = 0; i < items.Length; i++) DrawRow(g, i);

            // 拖文件的落点标识（跟书签栏同一套）：进文件夹 = 整行罩蓝；插到中间 = 缝上一条蓝横线
            if (dropInto >= 0 && dropInto < items.Length)
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(70, Theme.Accent)))
                    g.FillRectangle(b, new Rectangle(0, rowTop[dropInto], Width, rowH[dropInto]));
            }
            if (dropRow >= 0)
            {
                int ly;
                if (dropRow >= items.Length)
                {
                    int last = LastRow();
                    ly = (last < 0) ? Px(PadV) : (rowTop[last] + rowH[last] - 1);
                }
                else ly = dropAfter ? (rowTop[dropRow] + rowH[dropRow] - 1) : rowTop[dropRow];
                using (Pen p = new Pen(Theme.Accent, Math.Max(2f, DpiScale * 2)))
                    g.DrawLine(p, 0, ly, Width, ly);
            }

            using (Pen p2 = new Pen(Theme.MenuBorder))
                g.DrawRectangle(p2, 0, 0, Width - 1, Height - 1);
        }

        private void DrawRow(Graphics g, int i)
        {
            PopItem it = items[i];
            Rectangle r = new Rectangle(0, rowTop[i], Width, rowH[i]);

            if (it == null || it.IsSep)
            {
                using (Pen p = new Pen(Theme.MenuBorder))
                    g.DrawLine(p, Px(8), r.Top + r.Height / 2, Width - Px(8), r.Top + r.Height / 2);
                return;
            }

            bool on = (i == hover);
            if (on) g.FillRectangle(new SolidBrush(Theme.MenuHover), r);

            bool en = IsEnabled(it);
            Color fg = en ? Theme.Text : Theme.TextDim;

            int x = Px(2);
            if (it.On)
            {
                TextRenderer.DrawText(g, "\uE73E", glyph,
                    new Rectangle(x, r.Top, Px(CheckW), r.Height), en ? Theme.Accent : Theme.TextDim,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            x += Px(CheckW) + Px(TextPadL);

            int right = HasKids(it) ? Px(ArrowW) : Px(4);
            int tw = Width - x - right - Px(TextPadR);
            if (tw > 0)
                TextRenderer.DrawText(g, it.Text, font,
                    new Rectangle(x, r.Top, tw, r.Height), fg,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            if (HasKids(it))
                TextRenderer.DrawText(g, "\uE76C", glyph,
                    new Rectangle(Width - Px(ArrowW) - Px(2), r.Top, Px(ArrowW), r.Height),
                    en ? Theme.TextDim : Theme.TextDim,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}
