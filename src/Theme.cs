using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TabbedExplorer
{
    /// <summary>
    /// 深浅色：跟随系统「设置 - 个性化 - 颜色 - 应用模式」。
    /// 我们自己的绘制全部按这里的调色板来；
    /// shell 那一块（文件列表）用 SetWindowTheme("DarkMode_Explorer") 尽量带暗，
    /// Win10 没给第三方宿主公开的变暗接口，带不动就保持原样、不报错。
    /// </summary>
    internal static class Theme
    {
        public static bool IsDark;

        // ---- 外壳 ----
        public static Color TabBar;      // 标签条底
        public static Color TabActive;   // 选中标签底
        public static Color Chrome;      // 外壳底色：自绘标题栏 / 无边框窗口四周那圈 / 内容容器底
        public static Color RibbonBack;  // 功能区底
        public static Color RibbonGroup; // 命令区底（与标签条区分）
        public static Color Text;
        public static Color TextDim;
        /// <summary>窗口**没激活**时的文字色（原生标题栏失活就降灰，我们的自绘外壳照这个来）。</summary>
        public static Color TextInactive;
        /// <summary>失活时的强调线（选中标签顶上那条）。</summary>
        public static Color AccentDim;
        public static Color Border;
        public static Color Hover;
        public static Color Press;
        public static Color Accent;      // Win10 蓝
        public static Color Pane;        // 地址栏/内容底
        public static Color InputBack;   // 输入框底
        public static Color StatusBack;
        public static Color NavBack;
        public static Color MenuBack;
        public static Color MenuHover;
        public static Color MenuBorder;

        // ---- 窗口**未激活**时的那几个色 ----
        /// <summary>
        /// 外壳底色 / 标签条底 / 选中标签底的「失活版」。
        ///
        /// 2026-09-22 川：原生资源管理器激活与未激活的颜色是不一样的，我们的外壳要跟着模拟
        /// （原来是不管激活没有，一律用设置的那种底色）。
        /// 差值故意做小 —— 我们这几行颜色必须和嵌进来的 explorer 功能区**连成一整片**，
        /// 差太多反而会露出一条接缝。
        /// </summary>
        public static Color ChromeOff;
        public static Color TabBarOff;
        public static Color TabActiveOff;

        public static event EventHandler Changed;

        /// <summary>
        /// 颜色模式（设置里那三个）：System = 读注册表跟随系统；Light / Dark = 强制定死。
        /// 程序启动时先设它再 Reload；运行时改走 SetMode。
        /// </summary>
        public static Settings.ColorMode Mode = Settings.ColorMode.System;

        static Theme()
        {
            Reload();
        }

        /// <summary>模式 + 系统状态 ⇒ 到底用不用深色。</summary>
        private static bool ReadDark()
        {
            if (Mode == Settings.ColorMode.Dark) return true;
            if (Mode == Settings.ColorMode.Light) return false;
            return ReadSystemDark();
        }

        /// <summary>
        /// 换颜色模式（菜单里点的那三个）。立刻重算调色板并通知所有自绘控件重画，
        /// 顺便把进程级的 shell 深色开关也重设一遍（浅色必须显式设回 ForceLight，否则停在深色）。
        ///
        /// ⚠ 嵌入进来的 explorer 是**独立进程**，它自己按**系统**主题画，我们设不了别人进程的开关。
        /// 所以强制浅/深只改得动我们自己画的外壳（标题栏/标签条/菜单），文件列表仍跟系统 ——
        /// 调用方要重做一遍逐窗口的 SetWindowTheme 做尽力而为，并在界面里把这一点说清楚。
        /// </summary>
        public static void SetMode(Settings.ColorMode m)
        {
            Mode = m;
            Reload();
            try { DarkMode.SetEnabled(IsDark); } catch { }
            Diag.Step("Theme: 颜色模式=" + Settings.Text(m) + " -> dark=" + IsDark);
            RaiseChanged();
        }

        /// <summary>读注册表 / 强制模式决定深浅，并重算调色板。返回是否发生了变化。</summary>
        public static bool Reload()
        {
            bool dark = ReadDark();
            bool changed = (dark != IsDark) || !initialized;
            IsDark = dark;
            initialized = true;

            if (dark)
            {
                // 参照 Win10 深色资源管理器
                // 标签条底 = 纯黑：和 explorer 自己的功能区（命令栏那一行，「文件/主页/共享/查看」）
                // 取同一个色，两块拼起来才是连续的一整片（川：标签也不够黑，要和查看那一页一样黑）。
                TabBar = Color.FromArgb(0, 0, 0);
                TabActive = Color.FromArgb(51, 51, 51);
                // 外壳底色也是纯黑：和标签条、explorer 自己的功能区连成一整片，
                // 否则自绘标题栏那一行（原本 43,43,43）会在两条黑之间露出一道灰接缝。
                Chrome = Color.FromArgb(0, 0, 0);
                RibbonBack = Color.FromArgb(43, 43, 43);
                RibbonGroup = Color.FromArgb(51, 51, 51);
                Text = Color.FromArgb(240, 240, 240);
                TextDim = Color.FromArgb(165, 165, 165);
                TextInactive = Color.FromArgb(130, 130, 130);
                AccentDim = Color.FromArgb(80, 130, 160);
                Border = Color.FromArgb(70, 70, 70);
                Hover = Color.FromArgb(66, 66, 66);
                Press = Color.FromArgb(82, 82, 82);
                Accent = Color.FromArgb(76, 194, 255);
                Pane = Color.FromArgb(32, 32, 32);
                InputBack = Color.FromArgb(43, 43, 43);
                StatusBack = Color.FromArgb(32, 32, 32);
                NavBack = Color.FromArgb(43, 43, 43);
                MenuBack = Color.FromArgb(43, 43, 43);
                MenuHover = Color.FromArgb(62, 62, 62);
                MenuBorder = Color.FromArgb(70, 70, 70);
                // 失活版：底色 0 -> 18（抬一点，别死黑），选中标签 51 -> 58
                ChromeOff = Color.FromArgb(18, 18, 18);
                TabBarOff = Color.FromArgb(18, 18, 18);
                TabActiveOff = Color.FromArgb(58, 58, 58);
            }
            else
            {
                TabBar = Color.FromArgb(243, 243, 243);
                TabActive = Color.FromArgb(249, 249, 249);
                Chrome = Color.FromArgb(243, 243, 243);
                RibbonBack = Color.FromArgb(243, 243, 243);
                RibbonGroup = Color.FromArgb(249, 249, 249);
                Text = Color.FromArgb(30, 30, 30);
                TextDim = Color.FromArgb(108, 108, 108);
                TextInactive = Color.FromArgb(145, 145, 145);
                AccentDim = Color.FromArgb(150, 185, 215);
                Border = Color.FromArgb(219, 219, 219);
                Hover = Color.FromArgb(229, 229, 229);
                Press = Color.FromArgb(213, 213, 213);
                Accent = Color.FromArgb(0, 120, 212);
                Pane = Color.White;
                InputBack = Color.White;
                StatusBack = Color.FromArgb(243, 243, 243);
                NavBack = Color.FromArgb(243, 243, 243);
                MenuBack = Color.FromArgb(249, 249, 249);
                MenuHover = Color.FromArgb(225, 235, 245);
                MenuBorder = Color.FromArgb(200, 200, 200);
                // 失活版：243 -> 228（压暗一点，跟原生失活标题栏一个观感）
                ChromeOff = Color.FromArgb(228, 228, 228);
                TabBarOff = Color.FromArgb(228, 228, 228);
                TabActiveOff = Color.FromArgb(240, 240, 240);
            }
            return changed;
        }

        private static bool initialized;

        public static void RaiseChanged()
        {
            EventHandler h = Changed;
            if (h != null) h(null, EventArgs.Empty);
        }

        private static bool ReadSystemDark()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (k == null)
                    {
                        Diag.Log("Theme: Personalize 子键打不开，按浅色处理");
                        return false;
                    }
                    object v = k.GetValue("AppsUseLightTheme");
                    if (v == null)
                    {
                        Diag.Log("Theme: AppsUseLightTheme 值不存在，按浅色处理");
                        return false;
                    }
                    // 值类型不一定是 int（也可能是 string / long），统一转一下再判，
                    // 之前用 `v is int` 一旦类型不符就静默退化成浅色，很难查。
                    int n = Convert.ToInt32(v.ToString());
                    Diag.Log("Theme: AppsUseLightTheme=" + n
                             + " (CLR 类型 " + v.GetType().Name + ") -> dark=" + (n == 0));
                    return n == 0;
                }
            }
            catch (Exception ex)
            {
                Diag.Log("Theme: 读注册表失败 " + ex.Message);
            }
            return false;
        }

        // ==================================================================
        // 深色标题栏
        // ==================================================================
        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public static void ApplyTitleBar(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                int v = IsDark ? 1 : 0;
                // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE（1809+）；老版本用 19
                if (DwmSetWindowAttribute(hwnd, 20, ref v, 4) != 0)
                    DwmSetWindowAttribute(hwnd, 19, ref v, 4);
            }
            catch { }
        }

        // ==================================================================
        // 尽量把 shell 自己的窗口带暗
        // ==================================================================
        public static void StyleShellWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                string sub = IsDark ? "DarkMode_Explorer" : "Explorer";
                NativeMethods.SetWindowTheme(hwnd, sub, null);
            }
            catch { }
        }

        /// <summary>
        /// 把 shell 视图整棵子窗口树带暗。顺序很重要：
        /// 先 AllowDarkModeForWindow（逐窗口授权），再 SetWindowTheme（它本身会触发
        /// WM_THEMECHANGED 重新取主题，不用我们手动发）。
        ///
        /// 只对最外层 SetWindowTheme 的话，真正画文件列表的 DirectUIHWND 那一层
        /// 仍按浅色画 —— 表现就是「外框/导航窗格变深了、文件列表还是白的」，
        /// 也就是用户截图里那个现象。
        /// </summary>
        public static void StyleShellTree(IntPtr root)
        {
            if (root == IntPtr.Zero) return;
            try
            {
                AllowAndTheme(root);
                List<IntPtr> all = WinFind.All(root);
                for (int i = 0; i < all.Count; i++) AllowAndTheme(all[i]);

                // 换完主题再要求整棵树重画一遍（异步，不等它画完）。
                // 为什么需要：SetWindowTheme 只在**子应用名真的变了**的时候才让窗口重画，
                // 来回切颜色时会碰到「目标值跟现状一样」的那一步，它觉得无事可做，
                // 上一轮留下的像素就摆在屏幕上 —— 川截图里那个「一半深一半浅」。
                NativeMethods.RedrawWindow(root, IntPtr.Zero, IntPtr.Zero,
                    NativeMethods.RDW_INVALIDATE | NativeMethods.RDW_ERASE
                    | NativeMethods.RDW_FRAME | NativeMethods.RDW_ALLCHILDREN);

                Diag.Log("Theme: shell 子窗口上色 " + (all.Count + 1) + " 个，dark=" + IsDark);
            }
            catch { }
        }

        private static void AllowAndTheme(IntPtr h)
        {
            if (h == IntPtr.Zero) return;
            // 明确传「要不要深」：强制浅色时必须让它回到浅色主题，否则停在深色。
            DarkMode.AllowWindow(h, IsDark);
            // 先设成**反的那一边**，再设目标值 —— 保证这一轮真的发生了两次主题切换。
            // 同值重复设 = 空操作（它连 WM_THEMECHANGED 都不发），
            // 而「深色 -> 跟随系统(深)」这种切法目标值恰好等于现状，光设一次等于什么都没做。
            try
            {
                NativeMethods.SetWindowTheme(h, IsDark ? "Explorer" : "DarkMode_Explorer", null);
            }
            catch { }
            StyleShellWindow(h);
        }

        // ==================================================================
        // 弹出菜单配色
        // ==================================================================
        private sealed class Palette : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return MenuBack; } }
            public override Color MenuItemSelected { get { return MenuHover; } }
            public override Color MenuItemSelectedGradientBegin { get { return MenuHover; } }
            public override Color MenuItemSelectedGradientEnd { get { return MenuHover; } }
            public override Color MenuItemBorder { get { return MenuBorder; } }
            public override Color MenuBorder { get { return MenuBorder; } }
            public override Color ImageMarginGradientBegin { get { return MenuBack; } }
            public override Color ImageMarginGradientMiddle { get { return MenuBack; } }
            public override Color ImageMarginGradientEnd { get { return MenuBack; } }
            public override Color SeparatorDark { get { return Border; } }
            public override Color SeparatorLight { get { return Border; } }
        }

        public static void StyleMenu(ContextMenuStrip menu)
        {
            if (menu == null) return;
            menu.Renderer = new ToolStripProfessionalRenderer(new Palette());
            menu.BackColor = MenuBack;
            menu.ForeColor = Text;
            for (int i = 0; i < menu.Items.Count; i++) StyleMenuItem(menu.Items[i]);
        }

        private static void StyleMenuItem(ToolStripItem it)
        {
            if (it == null) return;
            it.ForeColor = Text;
            ToolStripMenuItem mi = it as ToolStripMenuItem;
            if (mi != null && mi.DropDownItems.Count > 0)
            {
                for (int i = 0; i < mi.DropDownItems.Count; i++) StyleMenuItem(mi.DropDownItems[i]);
            }
        }

        // ==================================================================
        // 悬停提示（ToolTip）
        // ==================================================================

        /// <summary>
        /// 让一个 `ToolTip` 的背景色 / 字体颜色跟着颜色模式走（2026-09-22 川报的 bug）。
        ///
        /// 光设 `BackColor` / `ForeColor` **不够稳**：系统那套 tooltip（comctl32）在开着视觉样式时
        /// 经常不认 `TTM_SETTIPBKCOLOR`，表现就是「设了却还是那块淡黄底」。
        /// 所以这里直接开 `OwnerDraw`，整块提示由我们画 —— 底色、边框、文字全是我们自己的调色板，
        /// 深浅两套都成立，也不用管系统主题。
        ///
        /// 画的时候颜色是**当场读** Theme 的字段，所以颜色模式一切换，下次弹出来就是新配色，
        /// 不用挨个 ToolTip 重新设一遍。
        ///
        /// ⚠ 只对「普通提示」成立：气泡（balloon）样式下 comctl 不叫 OwnerDraw。
        /// </summary>
        public static void StyleTip(ToolTip t) { StyleTip(t, null); }

        /// <summary>
        /// 同上，但可以指定**第一行**用哪套字体（川 2026-09-22：悬停提示里文件夹名加粗、路径不加粗）。
        ///
        /// 两个细节：
        ///   · `ToolTip` **没有 `Font` 属性**（跟 Label 那些控件不一样），改不了系统那套字体，
        ///     所以尺寸也得自己量、自己交回去 —— 见 `OnTipPopup`。不然粗体那一行会被截掉。
        ///   · 字体是按 ToolTip 实例记的（`OnTipDraw` / `OnTipPopup` 都是静态处理器，
        ///     得知道这次是哪个提示在画）。
        /// </summary>
        public static void StyleTip(ToolTip t, Font firstLineFont)
        {
            if (t == null) return;
            t.OwnerDraw = true;
            t.BackColor = MenuBack;
            t.ForeColor = Text;
            if (firstLineFont != null) tipFirstLine[t] = firstLineFont;
            t.Draw -= OnTipDraw;        // 幂等：重复 Style 同一个对象不会挂两遍
            t.Draw += OnTipDraw;
            t.Popup -= OnTipPopup;
            t.Popup += OnTipPopup;
        }

        /// <summary>
        /// 自己量两行提示的尺寸（`ToolTip` 没有 `Font`，comctl 那套尺寸是照系统字体算的，
        /// 我们第一行是粗体、比系统宽，不改尺寸会被截掉）。
        /// 宽 = 两行里更宽的那个 + 左右内缩；高 = 两行行高 + 行距 + 上下内缩。
        /// 数字要跟 `OnTipDraw` 里的内缩保持一致（左右 6、上下 4、行距 2）。
        /// 单行提示不插手 —— 交给系统自己量。
        /// </summary>
        private static void OnTipPopup(object sender, PopupEventArgs e)
        {
            try
            {
                ToolTip src = sender as ToolTip;
                if (src == null || e.AssociatedControl == null) return;

                Font first;
                if (!tipFirstLine.TryGetValue(src, out first) || first == null) return;

                string text = src.GetToolTip(e.AssociatedControl);
                if (string.IsNullOrEmpty(text)) return;
                int br = text.IndexOf("\r\n", StringComparison.Ordinal);
                if (br < 0) return;

                Font body = e.AssociatedControl.Font;
                if (body == null) body = first;

                string line1 = text.Substring(0, br);
                string line2 = text.Substring(br + 2);

                TextFormatFlags mf = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding
                                   | TextFormatFlags.SingleLine;
                Size s1 = TextRenderer.MeasureText(line1, first,
                    new Size(int.MaxValue, int.MaxValue), mf);
                Size s2 = TextRenderer.MeasureText(line2, body,
                    new Size(int.MaxValue, int.MaxValue), mf);

                e.ToolTipSize = new Size(
                    Math.Max(s1.Width, s2.Width) + 12,
                    s1.Height + s2.Height + 2 + 8);
            }
            catch { }
        }

        /// <summary>哪个提示的第一行该用粗体（没登记的就整块用 `e.Font`）。</summary>
        private static readonly Dictionary<ToolTip, Font> tipFirstLine = new Dictionary<ToolTip, Font>();

        private static void OnTipDraw(object sender, DrawToolTipEventArgs e)
        {
            try
            {
                Color back = MenuBack, border = MenuBorder, fore = Text;
                Rectangle r = e.Bounds;

                using (SolidBrush b = new SolidBrush(back)) e.Graphics.FillRectangle(b, r);
                using (Pen p = new Pen(border))
                    e.Graphics.DrawRectangle(p, r.Left, r.Top, r.Width - 1, r.Height - 1);

                // 文字贴着边框内缩几个像素，别顶到线上
                Rectangle tr = new Rectangle(r.Left + 6, r.Top + 4,
                                             Math.Max(1, r.Width - 12), Math.Max(1, r.Height - 8));

                string text = e.ToolTipText ?? "";
                ToolTip src = sender as ToolTip;
                Font first = null;
                if (src != null) tipFirstLine.TryGetValue(src, out first);

                int br = text.IndexOf("\r\n", StringComparison.Ordinal);
                if (first == null || br < 0)
                {
                    TextRenderer.DrawText(e.Graphics, text, first ?? e.Font, tr, fore,
                        TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPrefix |
                        TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                    return;
                }

                // 两行：第一行（文件夹名）用粗体，后面（完整路径）用细体
                string line1 = text.Substring(0, br);
                string line2 = text.Substring(br + 2);
                Size s1 = TextRenderer.MeasureText(line1, first, new Size(tr.Width, 0),
                    TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                TextRenderer.DrawText(e.Graphics, line1, first,
                    new Rectangle(tr.Left, tr.Top, tr.Width, Math.Max(1, s1.Height)), fore,
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPrefix |
                    TextFormatFlags.NoPadding);
                TextRenderer.DrawText(e.Graphics, line2, e.Font,
                    new Rectangle(tr.Left, tr.Top + s1.Height + 2,
                                  tr.Width, Math.Max(1, tr.Height - s1.Height - 2)), fore,
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPrefix |
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            }
            catch { }
        }
    }
}
