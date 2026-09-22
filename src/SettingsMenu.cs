using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace TabbedExplorer
{
    /// <summary>
    /// 设置项的**唯一一份规格** —— 三个入口都从这儿渲染，别各写一遍：
    ///   - 托盘图标右键：`BuildTraySettings`（WinForms 1.x 的 `MenuItem`）
    ///   - 齿轮 / 标签条空白右键：`SettingsForm`（独立窗口，川 2026-09-22 要的）
    ///
    /// 这就是当初「托盘右键漏了设置项」的根治办法：加一项只需要在 `Spec` 里加一行。
    ///
    /// 两份菜单的「打勾」画法不一样，原因在渲染那边：
    ///   - 菜单：用文字前缀 `✓ `（`MenuItem.Checked` 的小方块在深色下经常「勾了但看不见」）
    ///   - 窗口：就是标准的单选 / 勾选框，不用前缀
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
            /// <summary>勾选状态（**活的**取值函数）。null = 这一项不打勾（「配置文件在哪」那种说明项）。</summary>
            public Func<bool> Checked;
            public Action Click;
            public List<Node> Children;
            /// <summary>
            /// 互斥分组名 —— **设置窗口**用它决定「这几个画成一组单选」。
            /// 挨着的、名字一样的叶子 = 一组（比如「捕获方式」那两项、「颜色模式」那三项）；
            /// null 就是独立的一项（画成勾选框）。
            /// 托盘菜单不看这个字段（它统一用 `✓ ` 前缀表示「当前是哪个 / 开没开」）。
            /// </summary>
            public string Group;
        }

        private static Node Sep() { return new Node(); }

        private static Node Leaf(string text, Func<bool> check, Action click)
        {
            return new Node { Text = text, Checked = check, Click = click };
        }

        private static Node Leaf(string text, Func<bool> check, Action click, string group)
        {
            return new Node { Text = text, Checked = check, Click = click, Group = group };
        }

        private static Node Branch(string text, List<Node> kids)
        {
            return new Node { Text = text, Children = kids };
        }

        // ==================================================================
        // 内容（顺序 = 川要的顺序：捕获方式 → 颜色模式 → 保留标签 → 标签宽度 → 自适应 → 收藏夹栏 → 捕获所有 → 开机自启）
        // ==================================================================

        internal static List<Node> Spec(DesktopHub hub)
        {
            List<Node> n = new List<Node>();

            // ① 标签捕获方式（互斥）
            n.Add(Leaf(Settings.Label(Settings.CaptureMode.PerDesktop),
                       () => Settings.Capture == Settings.CaptureMode.PerDesktop,
                       () => hub.SetCaptureMode(Settings.CaptureMode.PerDesktop), "capture"));
            n.Add(Leaf(Settings.Label(Settings.CaptureMode.Migrate),
                       () => Settings.Capture == Settings.CaptureMode.Migrate,
                       () => hub.SetCaptureMode(Settings.CaptureMode.Migrate), "capture"));
            n.Add(Sep());

            // ② 颜色模式（互斥）
            n.Add(Leaf(Settings.Label(Settings.ColorMode.System),
                       () => Settings.Color == Settings.ColorMode.System,
                       () => hub.SetColorMode(Settings.ColorMode.System), "theme"));
            n.Add(Leaf(Settings.Label(Settings.ColorMode.Light),
                       () => Settings.Color == Settings.ColorMode.Light,
                       () => hub.SetColorMode(Settings.ColorMode.Light), "theme"));
            n.Add(Leaf(Settings.Label(Settings.ColorMode.Dark),
                       () => Settings.Color == Settings.ColorMode.Dark,
                       () => hub.SetColorMode(Settings.ColorMode.Dark), "theme"));
            n.Add(Sep());

            // ③ 是否保留标签页
            n.Add(Leaf("保留标签页（退出后记住、下次还原）",
                       () => Settings.KeepTabs,
                       () => hub.SetKeepTabs(!Settings.KeepTabs)));
            n.Add(Sep());

            // ④ 标签页宽度（预设几个常用值；想要别的直接改 settings.json）
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

            // ⑥ 收藏夹栏（Ctrl+Shift+B）
            n.Add(Leaf("显示收藏夹栏（Ctrl+Shift+B）",
                       () => Settings.FavBar,
                       () => hub.SetFavBar(!Settings.FavBar)));
            n.Add(Sep());

            // ⑦ 是否连「从开始菜单 / 桌面双击打开的文件夹」也收成标签
            n.Add(Leaf("捕获所有打开的文件夹（不只 Win+E）",
                       () => Settings.CaptureAll,
                       () => hub.SetCaptureAll(!Settings.CaptureAll)));
            n.Add(Sep());

            // ⑧ 开机自启（川 2026-09-22 要的）。状态现问注册表，见 AutoStart。
            //    ⚠ 标签就写「开机自启」四个字，不加括号说明（川明确说的）。
            n.Add(Leaf("开机自启",
                       () => AutoStart.IsEnabled(),
                       () => hub.SetAutoStart(!AutoStart.IsEnabled())));
            n.Add(Sep());

            // ⑨ 常用动作 + 说明
            n.Add(Leaf("记住当前标签", null, () => hub.RememberNow()));
            n.Add(Leaf("配置文件：程序目录\\data\\settings.json", null, null));
            return n;
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
                else if (nd.Checked != null && ts != null) ts.Bind(mi, nd);   // 只登记「会打勾」的那些

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
