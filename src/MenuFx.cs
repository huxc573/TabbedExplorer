using System;
using System.Drawing;
using System.Windows.Forms;

namespace TabbedExplorer
{
    /// <summary>
    /// 弹出菜单的**统一出口** —— 两件事：
    ///   1. **自绘**（勾选列 + 文字 + 分隔线 + 子菜单箭头全走我们的调色板）。
    ///      为什么非自绘不可（用户报）：用文字前缀 `✓ ` 表示「勾上了」的时候，
    ///      这一项比同级项多两个字符 → 看着就是**没对齐**（他原话「显示书签里没和其它选项一样居左对齐」）；
    ///      而用 `MenuItem.Checked` 那个系统小方块，深色主题下经常是「黑勾画在黑底上」= 勾了看不见。
    ///      自己画一个独立的勾选列，两个毛病一起消掉。
    ///   2. **弹之前 / 之后各打一行日志**（`菜单: 弹出 xxx` / `菜单: 关闭 xxx`）。
    ///      菜单是模态循环，卡没卡、有没有真弹出来，看这两行一目了然 ——
    ///      这正是之前「右键菜单点了没反应」查不出原因的地方（日志里什么都不留）。
    /// </summary>
    internal static class MenuFx
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

        /// <summary>勾选列的宽度（逻辑像素）。所有项都用它当文字起点 —— 这就是「对齐」的保证。</summary>
        private const int CheckCol = 22;

        /// <summary>MDL2 字形：对勾 / 子菜单箭头。</summary>
        private const string GlyphCheck = "\uE73E";
        private const string GlyphArrow = "\uE76C";

        /// <summary>建一个自绘菜单。`items` 里的分隔线就是 `new MenuItem("-")`。</summary>
        public static ContextMenu Build(MenuItem[] items)
        {
            ContextMenu m = new ContextMenu(items ?? new MenuItem[0]);
            // ⚠ 老式 `ContextMenu`（基类 `Menu`）**没有 `OwnerDraw` 属性** —— 自绘是
            //   **逐 MenuItem** 开的（见 Hook），菜单自己那圈外框仍由系统画。
            //   这就是当初选 `ContextMenu` 而不是 `ContextMenuStrip` 的代价：
            //   后者的 `Show()` 不阻塞、又不挂 owner，会把鼠标捕获抢走（界面看着像死了）。
            if (items != null)
            {
                for (int i = 0; i < items.Length; i++) Hook(items[i]);
            }
            return m;
        }

        /// <summary>给一棵菜单树挂自绘（含所有子菜单 —— 托盘那颗「设置」里还有一层）。</summary>
        public static void Hook(ContextMenu m)
        {
            if (m == null) return;
            for (int i = 0; i < m.MenuItems.Count; i++) Hook(m.MenuItems[i]);
        }

        private static void Hook(MenuItem mi)
        {
            if (mi == null) return;
            mi.OwnerDraw = true;
            mi.DrawItem -= OnDraw;
            mi.DrawItem += OnDraw;
            mi.MeasureItem -= OnMeasure;
            mi.MeasureItem += OnMeasure;
            for (int i = 0; i < mi.MenuItems.Count; i++) Hook(mi.MenuItems[i]);
        }

        /// <summary>
        /// 量菜单项。
        ///
        /// ⚠⚠ **宽度也必须自己给** —— 这是用户报的「所有右键菜单显示不全」的根因。
        /// WinForms 拿 `ItemWidth` / `ItemHeight` 去定菜单尺寸，而 `MeasureItemEventArgs` 的初值是 **0**；
        /// 只填高度、宽度留 0 ⇒ 菜单项窄成一条 ⇒ 文字被截。
        /// （老版本没自绘、由系统量，所以不会 —— 自绘是这一批新加的。）
        ///
        /// 宽度**整张菜单取同一个值**（取最宽那一项）而不是各算各的：
        /// 自绘项的矩形就是它自己报的宽度，各不相同的话选中/hover 那块底色会一块宽一块窄，很难看。
        /// </summary>
        private static void OnMeasure(object sender, MeasureItemEventArgs e)
        {
            try
            {
                MenuItem mi = sender as MenuItem;
                if (mi == null) return;
                e.ItemWidth = MenuWidth(mi);
                e.ItemHeight = IsSeparator(mi) ? Px(9) : Px(24);
            }
            catch { }
        }

        /// <summary>这张菜单该多宽：固定勾选列 + 最宽的文字 + （有子菜单时的箭头位）+ 右边距。</summary>
        private static int MenuWidth(MenuItem mi)
        {
            Menu m = mi.Parent;
            int textW = 0;
            bool anyKids = false;

            if (m != null)
            {
                for (int i = 0; i < m.MenuItems.Count; i++)
                {
                    MenuItem it = m.MenuItems[i];
                    if (it == null || IsSeparator(it)) continue;
                    if (it.MenuItems.Count > 0) anyKids = true;
                    int w = TextW(it.Text);
                    if (w > textW) textW = w;
                }
            }
            else
            {
                anyKids = mi.MenuItems.Count > 0;
                textW = TextW(mi.Text);
            }

            return Px(CheckCol) + textW + (anyKids ? Px(18) : Px(6)) + Px(10);
        }

        private static int TextW(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            Size s = TextRenderer.MeasureText(text, MenuFont(), new Size(4096, 4096),
                TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            return s.Width;
        }

        /// <summary>
        /// 菜单文字用的字体 —— **量的时候和画的时候必须是同一套**，
        /// 所以不用 `DrawItemEventArgs.Font`（那是系统/父控件的字体，跟我们对不上就会量少了 ⇒ 又被截）。
        /// </summary>
        private static Font menuFont;
        private static Font MenuFont()
        {
            if (menuFont == null)
                menuFont = new Font("Segoe UI", Px(12), FontStyle.Regular, GraphicsUnit.Pixel);
            return menuFont;
        }

        private static bool IsSeparator(MenuItem mi)
        {
            return mi != null && (mi.Text == "-" || string.IsNullOrEmpty(mi.Text));
        }

        private static void OnDraw(object sender, DrawItemEventArgs e)
        {
            try
            {
                MenuItem mi = sender as MenuItem;
                if (mi == null || e.Graphics == null) return;

                Graphics g = e.Graphics;
                Rectangle r = e.Bounds;

                // ---- 背景（选中 = hover 色）----
                bool sel = (e.State & DrawItemState.Selected) != 0;
                bool dis = (e.State & DrawItemState.Disabled) != 0 || !mi.Enabled;
                using (SolidBrush b = new SolidBrush(sel && !dis ? Theme.MenuHover : Theme.MenuBack))
                    g.FillRectangle(b, r);

                // ---- 分隔线 ----
                if (IsSeparator(mi))
                {
                    int y = r.Top + r.Height / 2;
                    using (Pen p = new Pen(Theme.Border))
                        g.DrawLine(p, r.Left + Px(4), y, r.Right - Px(4), y);
                    return;
                }

                int col = Px(CheckCol);

                // ---- 勾选列（**固定宽度**，不管有勾没勾都占这么大 —— 这样所有项的文字都对齐）----
                if (mi.Checked)
                {
                    Rectangle cr = new Rectangle(r.Left, r.Top, col, r.Height);
                    TextRenderer.DrawText(g, GlyphCheck, GlyphFont(), cr,
                        dis ? Theme.TextDim : (sel ? Theme.Text : Theme.Accent),
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.NoPadding);
                }

                // ---- 文字 ----
                bool hasKids = mi.MenuItems.Count > 0;
                int arrowW = hasKids ? Px(18) : Px(6);
                Rectangle tr = new Rectangle(r.Left + col, r.Top,
                                             Math.Max(1, r.Width - col - arrowW), r.Height);
                TextRenderer.DrawText(g, mi.Text, MenuFont(), tr,
                    dis ? Theme.TextDim : Theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

                // ---- 子菜单箭头（自绘之后系统不再帮我们画这个）----
                if (hasKids)
                {
                    Rectangle ar = new Rectangle(r.Right - arrowW, r.Top, arrowW, r.Height);
                    TextRenderer.DrawText(g, GlyphArrow, GlyphFont(), ar,
                        dis ? Theme.TextDim : Theme.Text,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.NoPadding);
                }
            }
            catch (Exception ex) { Diag.Log("菜单: 自绘失败 " + ex.Message); }
        }

        private static Font glyphFont;
        private static Font GlyphFont()
        {
            if (glyphFont == null)
                glyphFont = new Font("Segoe MDL2 Assets", Px(10), FontStyle.Regular, GraphicsUnit.Pixel);
            return glyphFont;
        }

        /// <summary>
        /// 建一条菜单项：点下去**先在日志里记一笔**再执行。
        ///
        /// 用户连着两轮报「右键菜单功能没实现」—— 加这一行是为了以后不用猜：
        /// 「菜单弹出来了但点了没反应」和「点了、动作自己失败了」在日志里是两回事
        /// （前者只会有「弹出/关闭」，后者一定会留下 `菜单项: xxx`）。
        /// **全程序的右键菜单都从这一条建**（菜单项别自己 `new MenuItem`）。
        /// `a == null` 就是一条灰着的项（当前做不了的动作，比如「关闭左边标签页」在没有左邻居时）。
        /// </summary>
        public static MenuItem Item(string text, Action a)
        {
            MenuItem m = new MenuItem(text);
            if (a == null) { m.Enabled = false; return m; }
            m.Click += delegate
            {
                Diag.Step("菜单项: " + text);
                try { a(); }
                catch (Exception ex) { Diag.Log("菜单项失败: " + text + " " + ex.Message); }
            };
            return m;
        }

        /// <summary>分隔线（老式 MenuItem 用 `"-"` 表示分隔线）。</summary>
        public static MenuItem Sep()
        {
            MenuItem m = new MenuItem("-");
            m.Enabled = false;
            return m;
        }

        /// <summary>
        /// 弹菜单（**模态**：一直阻塞到菜单关掉）。所有调用点都走这儿 ——
        /// 免得又出现「某处忘了记日志、出问题查不出来」。
        /// </summary>
        public static void Show(ContextMenu m, Control owner, Point at, string what)
        {
            if (m == null) return;
            try
            {
                Diag.Step("菜单: 弹出 " + what + " at " + at.X + "," + at.Y + "（共 " + m.MenuItems.Count + " 项）");
                m.Show(owner, at);
                Diag.Step("菜单: 关闭 " + what);
            }
            catch (Exception ex) { Diag.Log("菜单: 弹出失败 " + what + " " + ex.Message); }
            finally { try { m.Dispose(); } catch { } }
        }
    }
}
