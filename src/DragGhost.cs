using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TabbedExplorer
{
    /// <summary>
    /// 拖标签时**跟着鼠标跑的那张小浮窗**（用户：「拖动出来时好像没什么交互提示啊，我都不知道我拖到哪里了」）。
    ///
    /// 一行是被拖的那个标签名（相当于把手里的标签"举"起来给人看），
    /// 第二行是**松手会发生什么**（并入这个窗口 / 移到这个位置 / 在这里开一扇新窗口 / 取消）——
    /// 这一行才是真正回答「我现在拖到哪儿了」的东西。
    ///
    /// 为什么不画在标签条上：跨窗口拖拽时鼠标早就跑到别的窗口 / 桌面上去了，源控件根本画不到那儿。
    ///
    /// 两条硬要求（照 `Toast` 抄）：
    ///   1. **不许抢焦点**（`WS_EX_NOACTIVATE` + `ShowWithoutActivation`）——
    ///      拖拽途中把前台顶掉，鼠标捕获可能当场被系统收走，整个拖拽就断了；
    ///   2. **别挡住拖拽落点**：窗口摆在光标右下角偏移一点，光标永远不落在它身上 ——
    ///      落点判定走的是 `WindowFromPoint`，被它挡住就会把「并进另一个窗口」误判成「在桌面空白处」。
    /// </summary>
    internal sealed class DragGhost : Form
    {
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

        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        /// <summary>窗口摆在光标右下角这么远 —— 保证光标不落在影窗上（见类注释第 2 条）。</summary>
        private const int OffX = 14;
        private const int OffY = 12;

        private string title = "";
        private string hint = "";

        private readonly Font titleFont = new Font("Segoe UI", Px(12), FontStyle.Regular, GraphicsUnit.Pixel);
        private readonly Font hintFont = new Font("Segoe UI", Px(11), FontStyle.Regular, GraphicsUnit.Pixel);

        public DragGhost()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Theme.TabActive;
            DoubleBuffered = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            // 先给个尺寸，免得第一次 Show 时是个 0x0 的窗
            ClientSize = new Size(Px(220), Px(46));
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;   // 不激活、不进 Alt+Tab
                return cp;
            }
        }

        /// <summary>开始拖：摆到光标那儿露脸。`hint` 可以为空（那一行就不占位置）。</summary>
        public void Begin(string tabTitle, string hintText, Point screen)
        {
            title = tabTitle ?? "";
            hint = hintText ?? "";
            Relayout(screen);
            if (!Visible) Show();
            BringToTop();
            Redraw();
        }

        /// <summary>拖动中：跟着光标走，顺手换第二行那句提示（变了才重排尺寸）。</summary>
        public void MoveTo(Point screen, string hintText)
        {
            if (IsDisposed || Disposing) return;
            string h = hintText ?? "";
            bool resized = !string.Equals(hint, h, StringComparison.Ordinal);
            hint = h;
            if (resized) Relayout(screen);      // Relayout 里会算 Location，所以这一支不用再摆一次
            else Location = ClampPos(screen, Size);
            if (!Visible) Show();
            BringToTop();
            if (resized) Redraw();
        }

        /// <summary>拖完了（松手 / 取消）：收起来，留着下次用。</summary>
        public void HideIt()
        {
            if (IsDisposed || Disposing) return;
            try { Hide(); } catch { }
        }

        private void Relayout(Point screen)
        {
            Size ts = TextRenderer.MeasureText(title, titleFont, new Size(Px(520), Px(24)),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            Size hs = string.IsNullOrEmpty(hint)
                ? Size.Empty
                : TextRenderer.MeasureText(hint, hintFont, new Size(Px(520), Px(22)),
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

            int w = Math.Max(ts.Width, hs.Width) + Px(3) + Px(12) + Px(12);   // 3 = 左边那条强调条
            w = Math.Min(w, Px(420));
            int h = Px(7) + ts.Height + (hs.Height > 0 ? Px(3) + hs.Height : 0) + Px(7);

            ClientSize = new Size(w, h);
            Location = ClampPos(screen, Size);
        }

        /// <summary>摆在光标右下角，但别跑出屏幕（多显示器时只夹进"光标所在那块屏"）。</summary>
        private static Point ClampPos(Point screen, Size size)
        {
            Rectangle wa;
            try { wa = Screen.FromPoint(screen).WorkingArea; }
            catch { wa = Screen.PrimaryScreen.WorkingArea; }
            int x = screen.X + Px(OffX);
            int y = screen.Y + Px(OffY);
            if (x + size.Width > wa.Right) x = screen.X - Px(OffX) - size.Width;   // 右边挤不下就翻到左边
            if (y + size.Height > wa.Bottom) y = screen.Y - Px(OffY) - size.Height;
            if (x < wa.Left) x = wa.Left;
            if (y < wa.Top) y = wa.Top;
            return new Point(x, y);
        }

        private void BringToTop()
        {
            try
            {
                EmbedApi.SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0,
                    EmbedApi.SWP_NOSIZE | EmbedApi.SWP_NOMOVE |
                    EmbedApi.SWP_NOACTIVATE | EmbedApi.SWP_SHOWWINDOW);
            }
            catch { }
        }

        private void Redraw()
        {
            if (IsDisposed || Disposing) return;
            try { Invalidate(); } catch { }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (SolidBrush b = new SolidBrush(Theme.TabActive))
                g.FillRectangle(b, new Rectangle(0, 0, Width, Height));
            // 左边一条强调色竖条：跟选中标签那条指示条一个色系，一眼知道"这是我手里那个标签"
            using (SolidBrush b = new SolidBrush(Theme.Accent))
                g.FillRectangle(b, new Rectangle(0, 0, Math.Max(2, Px(3)), Height));
            using (Pen p = new Pen(Theme.Border))
                g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);

            int left = Math.Max(2, Px(3)) + Px(12);
            int right = Width - Px(12);
            int y = Px(7);
            Size ts = TextRenderer.MeasureText(title, titleFont, new Size(Px(520), Px(24)),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            TextRenderer.DrawText(g, title, titleFont,
                new Rectangle(left, y, right - left, ts.Height), Theme.Text,
                TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            y += ts.Height;
            if (!string.IsNullOrEmpty(hint))
            {
                y += Px(3);
                Size hs = TextRenderer.MeasureText(hint, hintFont, new Size(Px(520), Px(22)),
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                TextRenderer.DrawText(g, hint, hintFont,
                    new Rectangle(left, y, right - left, hs.Height), Theme.TextDim,
                    TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // 全自绘：背景在 OnPaint 里一次画完，这里什么都不做（免得闪一下系统底色）
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                titleFont.Dispose();
                hintFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
