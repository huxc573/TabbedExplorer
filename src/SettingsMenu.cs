using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace TabbedExplorer
{
    /// <summary>
    /// 设置菜单的内容 —— 窗口里那枚齿轮和托盘右键用的是**同一份**，只是换了两种菜单元件：
    ///   - 齿轮：`ContextMenuStrip`（能吃我们自己的深色渲染器）
    ///   - 托盘：WinForms 1.x 的 `MenuItem`（托盘本来就用的这套）
    /// 两边都别各写一遍，否则加一项就会漏一边（川报的「托盘右键没有设置选项」就是这么来的）。
    ///
    /// 勾选状态一律用**文字前缀** `✓ `，不用 `MenuItem.Checked` / `ToolStripMenuItem.Checked`：
    /// 那个小方块是渲染器画的位图，深色下经常「勾了但看不见」。前缀跟着前景色走，深浅都成立。
    /// </summary>
    internal static class SettingsMenu
    {
        private const string On = "✓ ";
        private const string Off = "   ";

        /// <summary>菜单项规格。Checked 传一个**取值函数**（不是快照），刷新时重算。</summary>
        /// <remarks>
        /// internal 而不是 private：`TraySettings.Bind` 的参数是它，
        /// 两者可访问性不一致就是 CS0051。外层类已经是 internal，这里外露不出去。
        /// </remarks>
        internal sealed class Node
        {
            public string Text;
            public Func<bool> Checked;      // null = 这一项不打勾（「关于」那种）
            public Action Click;
            public List<Node> Children;
        }

        private static Node Sep() { return new Node(); }

        private static Node Leaf(string text, Func<bool> check, Action click)
        {
            return new Node { Text = text, Checked = check, Click = click };
        }

        private static Node Branch(string text, List<Node> kids)
        {
            return new Node { Text = text, Children = kids };
        }

        // ==================================================================
        // 内容（顺序 = 川要的顺序：捕获方式 → 颜色模式 → 保留标签 → 标签宽度 → 自适应）
        // ==================================================================

        private static List<Node> Spec(DesktopHub hub)
        {
            List<Node> n = new List<Node>();

            // ① 标签捕获方式
            n.Add(Leaf(Settings.Label(Settings.CaptureMode.PerDesktop),
                       () => Settings.Capture == Settings.CaptureMode.PerDesktop,
                       () => hub.SetCaptureMode(Settings.CaptureMode.PerDesktop)));
            n.Add(Leaf(Settings.Label(Settings.CaptureMode.Migrate),
                       () => Settings.Capture == Settings.CaptureMode.Migrate,
                       () => hub.SetCaptureMode(Settings.CaptureMode.Migrate)));
            n.Add(Sep());

            // ② 颜色模式
            n.Add(Leaf(Settings.Label(Settings.ColorMode.System),
                       () => Settings.Color == Settings.ColorMode.System,
                       () => hub.SetColorMode(Settings.ColorMode.System)));
            n.Add(Leaf(Settings.Label(Settings.ColorMode.Light),
                       () => Settings.Color == Settings.ColorMode.Light,
                       () => hub.SetColorMode(Settings.ColorMode.Light)));
            n.Add(Leaf(Settings.Label(Settings.ColorMode.Dark),
                       () => Settings.Color == Settings.ColorMode.Dark,
                       () => hub.SetColorMode(Settings.ColorMode.Dark)));
            n.Add(Sep());

            // ③ 是否保留标签页
            n.Add(Leaf("保留标签页（退出后记住、下次还原）",
                       () => Settings.KeepTabs,
                       () => hub.SetKeepTabs(!Settings.KeepTabs)));
            n.Add(Sep());

            // ④ 标签页宽度（预设几个常用值；想要别的直接改 settings.txt）
            List<Node> widths = new List<Node>();
            foreach (int w in Settings.TabWidthPresets)
            {
                int val = w;
                widths.Add(Leaf(val + " 像素", () => Settings.TabWidth == val,
                                () => hub.SetTabWidth(val)));
            }
            n.Add(Branch("标签页宽度", widths));

            // ⑤ 自适应宽度
            n.Add(Leaf("自适应宽度（挤不下时自动缩窄）",
                       () => Settings.TabAutoFit,
                       () => hub.SetTabAutoFit(!Settings.TabAutoFit)));
            n.Add(Sep());

            // ⑥ 常用动作 + 关于
            n.Add(Leaf("记住当前标签", null, () => hub.RememberNow()));
            n.Add(Leaf("配置文件：程序目录\\data\\settings.txt", null, null));
            return n;
        }

        // ==================================================================
        // 齿轮：ContextMenuStrip
        // ==================================================================

        public static ContextMenuStrip BuildGear(DesktopHub hub)
        {
            ContextMenuStrip m = new ContextMenuStrip();
            m.ShowImageMargin = false;
            AddStrip(m.Items, Spec(hub));
            Theme.StyleMenu(m);
            return m;
        }

        private static void AddStrip(ToolStripItemCollection items, List<Node> nodes)
        {
            foreach (Node nd in nodes)
            {
                if (nd.Text == null) { items.Add(new ToolStripSeparator()); continue; }

                ToolStripMenuItem mi = new ToolStripMenuItem(TextOf(nd));
                if (nd.Click != null)
                {
                    Action a = nd.Click;
                    mi.Click += delegate { a(); };
                }
                else mi.Enabled = false;
                if (nd.Children != null && nd.Children.Count > 0) AddStrip(mi.DropDownItems, nd.Children);
                items.Add(mi);
            }
        }

        // ==================================================================
        // 托盘：MenuItem（结构建一次，设置变了只刷文字）
        // ==================================================================

        /// <summary>托盘那棵「设置」子树。设置变了调一次 Refresh 就够，不用重建菜单。</summary>
        public sealed class TraySettings
        {
            public MenuItem Root;

            private readonly List<MenuItem> items = new List<MenuItem>();
            private readonly List<Node> nodes = new List<Node>();

            internal void Bind(MenuItem mi, Node nd)
            {
                items.Add(mi);
                nodes.Add(nd);
            }

            /// <summary>把带勾选前缀的文字重刷一遍（勾选状态是活的，菜单别缓存）。</summary>
            public void Refresh()
            {
                for (int i = 0; i < items.Count; i++)
                {
                    Node nd = nodes[i];
                    if (nd.Checked == null) continue;
                    items[i].Text = TextOf(nd);
                }
            }
        }

        public static TraySettings BuildTraySettings(DesktopHub hub)
        {
            TraySettings ts = new TraySettings();
            ts.Root = new MenuItem("设置");
            ts.Root.MenuItems.AddRange(ToMenus(Spec(hub), ts));
            return ts;
        }

        private static MenuItem[] ToMenus(List<Node> nodes, TraySettings ts)
        {
            List<MenuItem> r = new List<MenuItem>();
            foreach (Node nd in nodes)
            {
                if (nd.Text == null) { r.Add(new MenuItem("-")); continue; }

                MenuItem mi = new MenuItem(TextOf(nd));
                if (nd.Click != null)
                {
                    Action a = nd.Click;
                    mi.Click += delegate { a(); };
                }
                else mi.Enabled = false;

                if (nd.Children != null && nd.Children.Count > 0)
                    mi.MenuItems.AddRange(ToMenus(nd.Children, ts));
                else if (nd.Checked != null) ts.Bind(mi, nd);   // 只登记「会打勾」的那些

                r.Add(mi);
            }
            return r.ToArray();
        }

        private static string TextOf(Node nd)
        {
            if (nd.Checked == null) return nd.Text;
            return (nd.Checked() ? On : Off) + nd.Text;
        }
    }
}
