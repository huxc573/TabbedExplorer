using System;
using System.Drawing;
using System.Windows.Forms;

namespace TabbedExplorer
{
    /// <summary>
    /// 自绘的标题栏那一行：左边**快速访问工具栏**，中间标题，右边窗口按钮。
    ///
    /// 为什么必须自己画：
    /// 嵌入进来的 explorer 窗口是子窗口，**子窗口没有标题栏**；而 explorer 的
    /// 快速访问工具栏正是画在「标题栏」那一条里的 —— 清掉 WS_CAPTION 让它变成子窗口
    /// 之后那一条就空了，而且**拿不回来**（把 WS_CAPTION 加回去也无效，实测顶部像素毫无变化）。
    ///
    /// 图标**不自己画**：explorer 的每个命令在 shell 命令库里都声明了图标源
    /// （HKLM\...\Explorer\CommandStore\shell\Windows.undo 的 Icon = "imageres.dll,-5315" 这种，
    /// 负号表示«资源 ID»不是序号）。这里照抄同一份源，取同一颗图标，所以长得和原生一模一样。
    /// 与真实 explorer 窗口标题栏逐像素比 IoU：属性 .89 / 新建文件夹 .88 / 撤销 .63 /
    /// 重做 .59 / 删除 .83 / 重命名 .78（撤销、重做是细笔画，差一像素就掉分，形状本身重合）。
    ///
    /// 按钮动作也不自己实现，统一由 ExplorerHost 把快捷键派给那个 explorer 线程当前
    /// 持有焦点的窗口，行为跟 explorer 自己的一模一样。
    /// </summary>
    internal sealed class TitleBar : Control
    {
        public enum Qat { Properties, NewFolder, Undo, Redo, Delete, Rename }
        public enum WBtn { Minimize, Maximize, Close }

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

        /// <summary>一个快速访问按钮：动作 + 图标来源（dll, 负号资源 ID）+ 提示 + 是否转单色。</summary>
        private struct QatDef
        {
            public Qat Action;
            public string File;     // imageres.dll / shell32.dll
            public int Rid;         // 负号 = 资源 ID，照抄命令库里 Icon 那串
            public string Tip;
            public bool Gray;       // 原生 QAT 画成单色的那几颗（撤销/重做/删除/重命名）
            public QatDef(Qat a, string f, int rid, string tip, bool gray)
            { Action = a; File = f; Rid = rid; Tip = tip; Gray = gray; }
        }

        // 源照抄 HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\CommandStore\shell
        // 里各命令的 Icon 值；顺序照原生 QAT 默认排列（属性、新建文件夹、撤销、重做、删除、重命名）。
        //
        // Gray 的取法也是照原生来的：命令库给的撤销/重做是**蓝**箭头、删除是**红**叉，
        // 而原生 QAT 上这几颗是**单色灰**的（逐像素量下来墨迹是中性灰），所以要转灰；
        // 属性（白纸+黄铅笔）和新建文件夹（黄文件夹）原生保留颜色，**不能**转灰。
        private static readonly QatDef[] QatDefs = new QatDef[]
        {
            new QatDef(Qat.Properties, "imageres.dll", -5367, "属性",     false),
            new QatDef(Qat.NewFolder,  "shell32.dll",   -319, "新建文件夹", false),
            new QatDef(Qat.Undo,       "imageres.dll", -5315, "撤销",     true),
            new QatDef(Qat.Redo,       "imageres.dll", -5311, "重做",     true),
            new QatDef(Qat.Delete,     "shell32.dll",   -240, "删除",     true),
            new QatDef(Qat.Rename,     "shell32.dll",   -242, "重命名",   true),
        };

        private const int QatCount = 6;
        private const int WBtnCount = 3;

        // 与原生实测对齐：这条高 45px @150%，按钮间距约 32px，图标 24px。
        private static int IconSize { get { return Px(16); } }
        private static int QatButtonWidth { get { return Px(21); } }

        private string title = "";
        private bool maximized;

        private int hoverQat = -1;
        private int pressQat = -1;
        private int hoverBtn = -1;

        private readonly ToolTip tips = new ToolTip();
        private Font iconFont;
        private Font textFont;

        private readonly Image[] iconBmp = new Image[QatCount];
        private bool iconsTried;

        public event Action<Qat> QatClicked;
        public event Action<WBtn> WindowButtonClicked;

        public TitleBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.RibbonBack;
            Height = Px(30);
            tips.InitialDelay = 500;
            tips.ReshowDelay = 200;
            Theme.Changed += delegate { RebuildFonts(); Invalidate(); };
            RebuildFonts();
        }

        private void RebuildFonts()
        {
            if (iconFont != null) iconFont.Dispose();
            if (textFont != null) textFont.Dispose();
            // MDL2 的字形墨迹高度 = 字号 1:1（实测），原生窗口按钮墨迹 15px @150% ⇒ 字号 Px(10)。
            iconFont = new Font("Segoe MDL2 Assets", Px(10), FontStyle.Regular, GraphicsUnit.Pixel);
            textFont = new Font("Segoe UI", Px(12), FontStyle.Regular, GraphicsUnit.Pixel);
        }

        // ==================================================================
        // 图标：走 shell 自己声明的那份源（见 ShellIcon），不自己画
        // ==================================================================
        private void EnsureIcons()
        {
            if (iconsTried) return;
            iconsTried = true;
            int size = IconSize;
            for (int i = 0; i < QatCount; i++)
            {
                try
                {
                    iconBmp[i] = ShellIcon.LoadBitmap(QatDefs[i].File, QatDefs[i].Rid, size, QatDefs[i].Gray);
                    if (iconBmp[i] == null)
                        Diag.Log("TitleBar: 取不到图标 " + QatDefs[i].File + "," + QatDefs[i].Rid);
                }
                catch (Exception ex)
                {
                    Diag.Log("TitleBar: 取图标失败 " + QatDefs[i].File + " " + ex.Message);
                }
            }
        }

        private void DisposeIcons()
        {
            for (int i = 0; i < QatCount; i++)
            {
                if (iconBmp[i] != null) { iconBmp[i].Dispose(); iconBmp[i] = null; }
            }
        }

        public string Title
        {
            get { return title; }
            set { if (title != value) { title = value ?? ""; Invalidate(); } }
        }

        public bool Maximized
        {
            get { return maximized; }
            set { if (maximized != value) { maximized = value; Invalidate(); } }
        }

        // ------------------------------------------------------------------
        private int WBtnWidth { get { return Px(46); } }

        private Rectangle QatBounds(int i)
        {
            int pad = Px(4);
            int w = QatButtonWidth;
            return new Rectangle(pad + i * w, 0, w, Height);
        }

        private Rectangle WBtnBounds(int i)   // 0=min 1=max 2=close
        {
            int w = WBtnWidth;
            return new Rectangle(Width - (WBtnCount - i) * w, 0, w, Height);
        }

        private bool InWindowButtons(Point p)
        {
            return p.X >= Width - WBtnCount * WBtnWidth;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            EnsureIcons();
            Graphics g = e.Graphics;
            g.FillRectangle(new SolidBrush(Theme.RibbonBack), ClientRectangle);

            // ---- 快速访问工具栏 ----
            for (int i = 0; i < QatCount; i++)
            {
                Rectangle b = QatBounds(i);
                if (i == pressQat) g.FillRectangle(new SolidBrush(Theme.Press), b);
                else if (i == hoverQat) g.FillRectangle(new SolidBrush(Theme.Hover), b);

                int s = IconSize;
                Rectangle ib = new Rectangle(b.Left + (b.Width - s) / 2,
                                             b.Top + (b.Height - s) / 2, s, s);
                if (iconBmp[i] != null)
                {
                    g.DrawImage(iconBmp[i], ib);
                }
                else
                {
                    // 兜底：取不到图标时退回一个方形占位，别让按钮变成空白
                    using (Pen p = new Pen(Theme.TextDim))
                        g.DrawRectangle(p, ib.Left + 2, ib.Top + 2, s - 5, s - 5);
                }
            }

            // 分隔线（原生在工具栏和标题之间也有一条）
            int sx = QatBounds(QatCount - 1).Right + Px(3);
            g.DrawLine(new Pen(Theme.Border), sx, Px(6), sx, Height - Px(6));

            // ---- 标题 ----
            int titleLeft = sx + Px(8);
            int titleRight = Width - WBtnCount * WBtnWidth - Px(6);
            if (titleRight > titleLeft)
            {
                Rectangle tr = new Rectangle(titleLeft, 0, titleRight - titleLeft, Height);
                TextRenderer.DrawText(g, title, textFont, tr, Theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }

            // ---- 窗口按钮 ----
            // 这三个是窗口装饰（原生由 DWM/主题画，不是 shell 命令、取不到图标），
            // 只能用字形画；码位取自 Segoe MDL2 Assets，形状与 Win10 标题栏一致。
            string[] glyph = new string[WBtnCount];
            glyph[0] = "\uE921";                                   // 最小化
            glyph[1] = maximized ? "\uE923" : "\uE922";             // 还原 / 最大化
            glyph[2] = "\uE8BB";                                   // 关闭
            for (int i = 0; i < WBtnCount; i++)
            {
                Rectangle b = WBtnBounds(i);
                if (i == hoverBtn)
                {
                    g.FillRectangle(new SolidBrush(i == 2 ? Color.FromArgb(232, 17, 35) : Theme.Hover), b);
                }
                Color fg = (i == hoverBtn) ? Color.White : Theme.Text;
                TextRenderer.DrawText(g, glyph[i], iconFont, b, fg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        // ------------------------------------------------------------------
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int hq = -1;
            for (int i = 0; i < QatCount; i++) if (QatBounds(i).Contains(e.Location)) hq = i;
            int hb = -1;
            if (InWindowButtons(e.Location))
            {
                for (int i = 0; i < WBtnCount; i++) if (WBtnBounds(i).Contains(e.Location)) hb = i;
            }
            if (hq != hoverQat || hb != hoverBtn)
            {
                hoverQat = hq;
                hoverBtn = hb;
                // 提示贴着按钮下边出来，文字就俩字
                if (hoverQat >= 0)
                {
                    Rectangle b = QatBounds(hoverQat);
                    tips.Show(QatDefs[hoverQat].Tip, this, b.Left, b.Bottom + Px(2), 2500);
                }
                else tips.Hide(this);
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hoverQat = -1; hoverBtn = -1;
            tips.Hide(this);
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (tips != null) tips.Dispose();
                if (iconFont != null) iconFont.Dispose();
                if (textFont != null) textFont.Dispose();
                DisposeIcons();
            }
            base.Dispose(disposing);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;

            if (InWindowButtons(e.Location))
            {
                for (int i = 0; i < WBtnCount; i++)
                {
                    if (WBtnBounds(i).Contains(e.Location))
                    {
                        WBtn wb = i == 0 ? WBtn.Minimize : (i == 1 ? WBtn.Maximize : WBtn.Close);
                        if (WindowButtonClicked != null) WindowButtonClicked(wb);
                        return;
                    }
                }
                return;
            }

            for (int i = 0; i < QatCount; i++)
            {
                if (QatBounds(i).Contains(e.Location))
                {
                    pressQat = i;
                    Invalidate();
                    if (QatClicked != null) QatClicked(QatDefs[i].Action);
                    return;
                }
            }

            // 空白处/标题处：交给系统走「拖动标题栏」那套（顺便白拿贴边吸附 + 双击最大化）
            DragWindow();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (pressQat >= 0) { pressQat = -1; Invalidate(); }
        }

        /// <summary>
        /// 无边框窗口自己把「拖标题栏」还给系统：ReleaseCapture 之后补一条 WM_NCLBUTTONDOWN(HTCAPTION)，
        /// DefWindowProc 就会走原生移动循环 —— 拖动、贴边吸附、双击最大化全都回来了，不用自己实现。
        /// </summary>
        private void DragWindow()
        {
            try
            {
                Form f = FindForm();
                if (f == null) return;
                EmbedApi.ReleaseCapture();
                EmbedApi.SendMessageW(f.Handle, EmbedApi.WM_NCLBUTTONDOWN,
                    new IntPtr(EmbedApi.HTCAPTION), IntPtr.Zero);
            }
            catch (Exception ex) { Diag.Log("TitleBar: 拖动失败 " + ex.Message); }
        }
    }
}
