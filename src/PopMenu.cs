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
    }

    /// <summary>
    /// 弹出菜单的**出口**。给调用方的 API 只有三个：`Show` / `It` / `Split` / `Sub`。
    ///
    /// ══════════════════════════════════════════════════════════════════════
    /// 为什么这里**不用** WinForms 那两套菜单（这是第三轮返工，把两次失败都记下来）
    ///
    /// 川报过三轮：「右键依然无效」→「历史记录点开后，所有功能都不可用」。
    /// 日志 + 探针把结论钉死了（2026-09-22 22:01 那次）：
    ///   · 79 次「菜单: 弹出」/ 78 次「菜单: 关闭」—— 菜单**弹得出来也关得掉**；
    ///   · 可 **一条「菜单项: xxx」都没有**（托盘那份菜单有，因为它走的是另一条路）；
    ///   · 而且探针一测，主窗口连 `WM_NULL` 都不回 —— **UI 线程卡死了**，不是「点了没反应」。
    /// 两代实现两种死法：
    ///   · 老的 `ContextMenu` + `MeasureItem` 自绘（`MenuFx.Show`）：`Show()` 是**模态**的，
    ///     模态期间**主窗口被禁用** —— 菜单项又收不到点击，于是菜单关不掉、整个窗口一直是死的。
    ///     川那句「点开后所有功能都不可用」就是这个，一字不差。
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
        /// </summary>
        public static void Show(PopItem[] items, Control owner, Point at, string what)
        {
            if (owner == null || items == null || items.Length == 0) return;
            try
            {
                Point screen;
                try { screen = owner.PointToScreen(at); }
                catch { screen = new Point(at.X, at.Y); }
                PopMenuWindow.Open(items, owner, screen, what);
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
        private static string openWhat;
        private static Action pending;

        private readonly PopItem[] items;
        private readonly string what;
        private readonly int[] rowTop;
        private readonly int[] rowH;
        private readonly Font font;
        private readonly Font glyph;

        private int hover = -1;
        private PopMenuWindow child;     // 展开的下一层
        private int childOwner = -1;     // 下一层是哪一项开出来的
        private Timer watch;

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

        private PopMenuWindow(PopItem[] items, string what)
        {
            this.items = items;
            this.what = what;

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

            // ---- 量高度 / 量宽度（只做一次；量完就是死数，鼠标事件里不用再算）----
            rowTop = new int[items.Length];
            rowH = new int[items.Length];
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
        {
            CloseAll(false);      // 防御：上一份还开着就先收掉（别叠两层）

            PopMenuWindow w = new PopMenuWindow(items, what);
            w.PlaceAt(atScreen);
            openWhat = what;

            Form owner = (anchor == null) ? null : anchor.FindForm();
            if (owner != null && owner.IsHandleCreated) w.Show(owner);
            else w.Show();

            root = w;
            w.BeginCapture();

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
                EmbedApi.SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0,
                    EmbedApi.SWP_NOMOVE | EmbedApi.SWP_NOSIZE | EmbedApi.SWP_NOACTIVATE |
                    EmbedApi.SWP_SHOWWINDOW);
            }
            catch { }

            // 看门狗：捕获被别人抢走（外来子窗口、输入法、别的程序）我们就收摊 ——
            // 没有它的话，一旦丢了捕获，这份菜单就永远关不掉了（「菜单粘在屏幕上」的经典死法）。
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
        /// ⚠ 但「捕获没了」有两种，得分清（2026-09-22 川报「点历史图标，菜单闪一下就没了」）：
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
            if (Capture) return;

            Point m;
            try { m = Cursor.Position; } catch { m = new Point(int.MinValue, int.MinValue); }

            if (ContainsScreen(m) && healTries < 3)
            {
                healTries++;
                Diag.Log("菜单: 捕获被收走，鼠标还在菜单里 -> 抢回来（第 " + healTries + " 次）");
                BeginCapture();
                return;
            }

            Diag.Log("菜单: 捕获丢了且鼠标不在菜单里，收摊（" + (openWhat ?? "") + "）");
            CloseAll(true);
        }

        /// <summary>屏幕坐标是不是落在这一摞菜单的任意一层里。</summary>
        private static bool ContainsScreen(Point p)
        {
            for (PopMenuWindow w = root; w != null; w = w.child)
            {
                try { if (w.Bounds.Contains(p)) return true; } catch { }
            }
            return false;
        }

        /// <summary>关掉整摞菜单。<paramref name="runPending"/> = 关完要不要执行选中那项的动作。</summary>
        private static void CloseAll(bool runPending)
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
        private void CloseChain()
        {
            PopMenuWindow c = child;
            child = null; childOwner = -1;
            if (c != null) c.CloseChain();
            try { if (watch != null) { watch.Stop(); watch.Dispose(); watch = null; } } catch { }
            try { Capture = false; } catch { }
            try { Close(); } catch { }
        }

        /// <summary>把比这一层更深的都收掉（鼠标挪回了浅的地方）。</summary>
        private void CloseDeeper()
        {
            if (child == null) return;
            PopMenuWindow c = child;
            child = null; childOwner = -1;
            c.CloseChain();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try { if (font != null) font.Dispose(); } catch { }
            try { if (glyph != null) glyph.Dispose(); } catch { }
            base.OnFormClosed(e);
        }

        // ==================================================================
        // 子菜单
        // ==================================================================

        private void OpenChild(int index)
        {
            if (child != null && childOwner == index) return;
            CloseDeeper();

            PopItem it = items[index];
            PopMenuWindow c = new PopMenuWindow(it.Kids, what + " ▸ " + it.Text);
            Point p = new Point(Right - Px(3), Top + rowTop[index]);
            Screen scr;
            try { scr = Screen.FromPoint(p); } catch { scr = Screen.PrimaryScreen; }
            if (p.X + c.Width > scr.WorkingArea.Right) p.X = Math.Max(scr.WorkingArea.Left, Left - c.Width + Px(3));
            if (p.Y + c.Height > scr.WorkingArea.Bottom)
                p.Y = Math.Max(scr.WorkingArea.Top, scr.WorkingArea.Bottom - c.Height);
            c.PlaceAt(p);
            c.Show(this);               // 挂在**这一层**名下（z 序 / 生命周期跟着走）
            child = c; childOwner = index;
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

        /// <summary>把一次鼠标事件送到「命中那一层」。</summary>
        private void Route(Point screen, bool down, MouseButtons btn)
        {
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
                // 点在整摞菜单外面：关掉，并且**吞掉这一下**（跟原生菜单一样，不算点到别处）
                if (down) CloseAll(false);
                return;
            }

            target.CloseDeeper();       // 挪回浅的地方了，更深的那几层收掉

            if (idx < 0) { target.SetHover(-1); return; }
            PopItem it = target.items[idx];

            if (!IsEnabled(it)) { target.SetHover(-1); return; }
            target.SetHover(idx);

            if (HasKids(it))
            {
                target.OpenChild(idx);              // 悬停就展开（原生菜单也是）
                return;
            }
            if (!down) return;

            if (btn == MouseButtons.Left)
            {
                pending = it.Run;                   // 先记下来，关完再执行
                Diag.Step("菜单项: " + it.Text);
                CloseAll(true);
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
        }

        // ==================================================================
        // 画
        // ==================================================================

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.FillRectangle(new SolidBrush(Theme.MenuBack), ClientRectangle);

            for (int i = 0; i < items.Length; i++) DrawRow(g, i);

            using (Pen p = new Pen(Theme.MenuBorder))
                g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
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
