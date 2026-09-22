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
    ///   - 菜单：用 `MenuItem.Checked` + **自绘**（`MenuFx`）—— 勾画在我们自己的勾选列里，
    ///     既不会像「文字前缀 `✓ `」那样把这一项顶得比同级项突出一块（川报的「没和其它选项一样居左对齐」），
    ///     也解决了自带的那个小方块在深色下「勾了但看不见」的老毛病。
    ///   - 窗口：就是标准的单选 / 勾选框，不用前缀。
    /// </summary>
    internal static class SettingsMenu
    {
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
            /// <summary>
            /// **只在设置窗口里出现**，托盘菜单里不排它（川 2026-09-22：
            /// 「删除一些不方便以菜单形式设置的内容」）。
            /// 典型是数值输入项（标签页宽度 → 菜单里塞一排预设值很别扭）和纯说明行。
            /// </summary>
            public bool WindowOnly;
            /// <summary>
            /// 数值项（设置窗口里画成数字输入框，可自己敲）。`NumMax &gt; 0` 表示「这是一条数值项」。
            /// 托盘菜单里一律不排（`WindowOnly` 会自动置上）。
            /// </summary>
            public int NumMin, NumMax, NumStep;
            /// <summary>数值项现在的值。</summary>
            public Func<int> NumGet;
            /// <summary>数值项改一下要干什么。</summary>
            public Action<int> NumSet;
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

        /// <summary>
        /// 数值项（设置窗口里画成可自己敲的数字输入框）。托盘菜单里不排 —— 菜单里塞一排预设
        /// 值很别扭（川 2026-09-22：「标签页宽度也可自己输入数值」）。
        /// </summary>
        private static Node Num(string text, int min, int max, int step, Func<int> get, Action<int> set)
        {
            return new Node
            {
                Text = text, NumMin = min, NumMax = max, NumStep = step,
                NumGet = get, NumSet = set, WindowOnly = true
            };
        }

        /// <summary>只在设置窗口里出现的纯说明行（菜单里列一行点了没反应的灰字没意义）。</summary>
        private static Node Info(string text)
        {
            return new Node { Text = text, WindowOnly = true };
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

            // ④ 标签页宽度（川 2026-09-22：「也可自己输入数值」）
            //    原来是「80/96/112/…」一串预设的子菜单 —— 只能挑不能敲，而且托盘菜单里挂着一层子菜单很难点。
            //    现在改成设置窗口里的数字输入框（64~240 逻辑像素），菜单里不排它。
            n.Add(Num("标签页宽度（逻辑像素，" + Settings.TabWidthMin + " ~ " + Settings.TabWidthMax + "）",
                      Settings.TabWidthMin, Settings.TabWidthMax, 8,
                      delegate { return Settings.TabWidth; },
                      delegate(int w) { hub.SetTabWidth(w); }));

            // ⑤ 自适应宽度（2026-09-22 川要拆成两项：加宽 / 缩窄）
            n.Add(Leaf("自适应宽度：名称过长时自动加宽",
                       () => Settings.TabAutoWiden,
                       () => hub.SetTabAutoWiden(!Settings.TabAutoWiden)));
            n.Add(Leaf("自适应宽度：挤不下时自动缩窄",
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
            // 川 2026-09-22 问「记住当前标签功能是干嘛的」—— 说明这个标签没讲清自己。
            // 它跟上面「保留标签页」不是一回事：那个是**开关**（开=以后才记），
            // 这个是**动作**（现在立刻把当前各桌面的标签存一次）。自动保存本来就有
            // （改动攒 800ms 落盘 + 退出前再存），所以手动这一下只在「怕它没来得及存」时用。
            n.Add(Leaf("立即记住当前标签（平时自动记，这个是手动存一次）", null, () => hub.RememberNow()));
            n.Add(Info("数据目录：程序目录\\data（settings / desktops / history / favorites 四个 json）"));
            // 川 2026-09-22 问「自带资源管理器左上角的功能不能一起捕获吗」—— 答案是不能。
            // 他后来又说「抓不回来就放弃，程序中不用写相关文字，文档里提一下就行」：
            // 这里**不再写这行说明**，要了解去 README/CHANGELOG 看（那条记在 README 的已知限制里）。
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

            /// <summary>把勾选状态重刷一遍（状态是活的，菜单别缓存）。</summary>
            public void Refresh()
            {
                for (int i = 0; i < items.Count; i++)
                {
                    Node nd = nodes[i];
                    if (nd.Checked == null) continue;
                    items[i].Checked = nd.Checked();
                }
            }
        }

        public static TraySettings BuildTraySettings(DesktopHub hub)
        {
            TraySettings ts = new TraySettings();
            ts.Root = new MenuItem("设置");
            ts.Root.MenuItems.AddRange(ToMenus(Spec(hub), ts));

            // 川 2026-09-22：「右键设置 菜单最后加：更多选项」——
            // 菜单里只留「在菜单里设着顺手」的那些，别的都去设置窗口；这一条就是入口。
            // 同时也是「删掉那些不方便以菜单形式设置的内容」的兜底：删掉的东西窗口里都还能改。
            ts.Root.MenuItems.Add(MenuFx.Sep());
            ts.Root.MenuItems.Add(MenuFx.Item("更多选项…（打开设置窗口）", delegate { hub.OpenSettings(); }));
            return ts;
        }

        private static MenuItem[] ToMenus(List<Node> nodes, TraySettings ts)
        {
            List<MenuItem> r = new List<MenuItem>();
            foreach (Node nd in nodes)
            {
                if (nd.WindowOnly) continue;                      // 只在设置窗口里出现（数值项 / 纯说明行）
                if (nd.Text == null) { r.Add(MenuFx.Sep()); continue; }

                MenuItem mi = new MenuItem(nd.Text);
                if (nd.Checked != null) mi.Checked = nd.Checked();   // 自绘时按这个画勾（见 MenuFx）
                if (nd.Click != null)
                {
                    Action a = nd.Click;
                    string label = nd.Text;
                    mi.Click += delegate { Diag.Step("菜单项: " + label); a(); };
                }
                else mi.Enabled = false;

                if (nd.Children != null && nd.Children.Count > 0)
                    mi.MenuItems.AddRange(ToMenus(nd.Children, ts));
                else if (nd.Checked != null && ts != null) ts.Bind(mi, nd);   // 只登记「会打勾」的那些

                r.Add(mi);
            }
            return r.ToArray();
        }
    }
}
