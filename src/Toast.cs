using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TabbedExplorer
{
    /// <summary>
    /// 气泡的**大类** —— 设置窗口「通知管理」页里一个「节」就是它（节标题 + 一条横线隔开）。
    ///
    /// 为什么分：出错必须让人看见；「我干了活」那种反馈（已复制 / 已加入书签）看多了反而烦。
    /// 大类的默认值只管**它下面每一条**的默认（见 `NotifyItem.Important`）——
    /// 真正落地的粒度是**逐条**（`Settings.NotifyItemOn`），要更细就自己在设置页里勾。
    /// </summary>
    internal enum ToastKind
    {
        /// <summary>出错 / 失败（打开不了、复制不了、加入失败、重启失败…）。**重要**。</summary>
        Err,
        /// <summary>启动与后台状态（「程序还在后台运行」这类）。**重要**。</summary>
        Sys,
        /// <summary>操作结果（已复制、已加入书签、换了书签栏…）。非 Debug 模式默认关。</summary>
        Ok,
        /// <summary>轻提示（已经在了、至少留一个、这个文件夹没书签…）。非 Debug 模式默认关。</summary>
        Hint
    }

    /// <summary>
    /// 自己画的提示气泡 —— 替代 `NotifyIcon.ShowBalloonTip`。
    ///
    /// 为什么不用系统的（用户报「显示提醒的背景和字体颜色没适配颜色模式」）：
    /// 那个气泡是**系统画的**，配色跟着**系统**「应用模式」走；我们强制浅色/深色的时候它不认，
    /// 于是外壳是浅色、气泡还是深色，看着就是两套皮。能自己控制配色的只有自己画一个。
    ///
    /// 两条硬要求：
    ///   1. **不许抢焦点**（`WS_EX_NOACTIVATE` + `ShowWithoutActivation`）——
    ///      提示弹一下就把人正在打字的窗口顶掉，比不弹还糟。
    ///   2. 几秒自动消失、点一下也消失。
    /// </summary>
    internal sealed class Toast : Form
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

        private static Toast current;

        private string head = "";
        private string body = "";
        private readonly Timer life = new Timer();
        private readonly Font headFont = new Font("Segoe UI", Px(12), FontStyle.Bold, GraphicsUnit.Pixel);
        private readonly Font bodyFont = new Font("Segoe UI", Px(11), FontStyle.Regular, GraphicsUnit.Pixel);

        /// <summary>
        /// 弹一条提示。`item` = 这是**哪一条**通知 —— 设置窗口「通知管理」页里的开关就是逐条管的
        /// （见 `NotifyItem` / `Settings.NotifyItemOn`）。
        ///
        /// ⚠ 只留这一个入口：全项目所有气泡都从这儿出去，所以「总闸 + 这一条自己的开关」一处判完；
        ///   散在二十几个调用点各判一次，早晚会漏。第几大类不用传 —— 那是条目的固有属性。
        /// 任何线程都能调 —— 不在 UI 线程时自己转过去。
        /// </summary>
        public static void Show(NotifyItem item, string title, string text)
        {
            try
            {
                if (!Settings.NotifyItemOn(item)) return;
                Post(title, text);
            }
            catch { }
        }

        /// <summary>
        /// **不管任何开关**直接弹一条 —— 设置窗口「通知管理」页那个「试弹一条通知」按钮用它。
        /// 那按钮的目的就是让人看样式和停留时间，被自己的开关拦住就没意义了。
        /// </summary>
        public static void Preview(string title, string text)
        {
            try { Post(title, text); } catch { }
        }

        /// <summary>真弹出去。不在 UI 线程时自己转过去。</summary>
        private static void Post(string title, string text)
        {
            if (current != null && !current.IsDisposed && current.InvokeRequired)
            {
                Toast f = current;
                f.BeginInvoke((MethodInvoker)delegate { ShowOnUi(title, text); });
                return;
            }
            ShowOnUi(title, text);
        }

        private static void ShowOnUi(string title, string text)
        {
            try
            {
                if (current == null || current.IsDisposed) current = new Toast();
                current.SetText(title, text);
            }
            catch (Exception ex) { Diag.Log("Toast: 弹提示失败 " + ex.Message); }
        }

        private Toast()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Theme.MenuBack;
            DoubleBuffered = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            life.Interval = 4200;        // 只是个初值，每次弹之前按 `Settings.NotifySec` 重设（见 SetText）
            life.Tick += delegate { life.Stop(); Close(); };
            Theme.Changed += delegate { if (!IsDisposed) { BackColor = Theme.MenuBack; Invalidate(); } };
        }

        /// <summary>不该把焦点抢过来（提示窗的底线）。</summary>
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

        private void SetText(string title, string text)
        {
            head = title ?? "";
            body = text ?? "";

            // 高度跟着正文行数走（正文经常是两句解释，写死高会截掉）
            int w = Px(360);
            Size sz = TextRenderer.MeasureText(body, bodyFont,
                new Size(w - Px(34), Px(400)), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            int h = Math.Max(Px(72), Px(20) + Px(18) + sz.Height + Px(16));
            ClientSize = new Size(w, h);

            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - Width - Px(12), wa.Bottom - Height - Px(12));

            Invalidate();
            if (!Visible) Show();
            BringToTopNoActivate();
            // ★ 停留时间**每次现读**（设置里改了立刻生效，不用重启，已经在屏幕上的那条不变）：
            //   1 秒是底线 —— 再短根本看不清；上限见 `Settings.NotifySecMax`。
            life.Stop();
            life.Interval = Math.Max(1000, Settings.NotifySec * 1000);
            life.Start();
        }

        /// <summary>
        /// 置顶显示但**别激活**：`SetWindowPos` + `SWP_NOACTIVATE` + `HWND_TOPMOST`。
        /// 必须先声明 argtypes —— 不声明时 64 位下 -1 被当 32 位传，调用静默失败（见 skill）。
        /// </summary>
        private void BringToTopNoActivate()
        {
            try
            {
                EmbedApi.SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0,
                    EmbedApi.SWP_NOSIZE | EmbedApi.SWP_NOMOVE |
                    EmbedApi.SWP_NOACTIVATE | EmbedApi.SWP_SHOWWINDOW);
            }
            catch { }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width, Height);
            using (SolidBrush b = new SolidBrush(Theme.MenuBack)) g.FillRectangle(b, r);

            // 左边一条强调色竖条（跟我们的选中标签一个色系）
            using (SolidBrush b = new SolidBrush(Theme.Accent))
                g.FillRectangle(b, new Rectangle(0, 0, Px(3), Height));

            using (Pen p = new Pen(Theme.MenuBorder))
                g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);

            int left = Px(14), right = Width - Px(14);
            TextRenderer.DrawText(g, head, headFont,
                new Rectangle(left, Px(10), right - left, Px(18)), Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            TextRenderer.DrawText(g, body, bodyFont,
                new Rectangle(left, Px(10) + Px(18), right - left, Height - Px(10) - Px(18) - Px(8)),
                Theme.TextDim,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak |
                TextFormatFlags.NoPadding);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            life.Stop();
            Close();          // 点一下就收起来
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (life != null) life.Dispose();
                if (headFont != null) headFont.Dispose();
                if (bodyFont != null) bodyFont.Dispose();     // 字体是实例字段，不会串到下一实例
            }
            base.Dispose(disposing);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // 全部在 OnPaint 里画（避免闪烁）
        }
    }
}
