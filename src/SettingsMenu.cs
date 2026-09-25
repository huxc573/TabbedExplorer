using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace TabbedExplorer
{
    /// <summary>
    /// 设置项的**唯一一份规格** —— 三个入口都从这儿渲染，别各写一遍：
    ///   - 托盘图标右键：`BuildTraySettings`（WinForms 1.x 的 `MenuItem`）
    ///   - 齿轮 / 标签条空白右键：`SettingsForm`（独立窗口，用户要的）
    ///
    /// 这就是当初「托盘右键漏了设置项」的根治办法：加一项只需要在 `Spec` 里加一行。
    ///
    /// 两份菜单的「打勾」画法不一样，原因在渲染那边：
    ///   - 菜单：用 `MenuItem.Checked` + **自绘**（`MenuFx`）—— 勾画在我们自己的勾选列里，
    ///     既不会像「文字前缀 `✓ `」那样把这一项顶得比同级项突出一块（用户报的「没和其它选项一样居左对齐」），
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
            /// **只在设置窗口里出现**，托盘菜单里不排它（用户：/// 「删除一些不方便以菜单形式设置的内容」）。
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
            /// <summary>
            /// 点完要在**设置窗口底部那行提示**里说一句（纯动作项用，托盘菜单不看这个）。
            /// null = 不说话。见 `SettingsForm.ApplyNode`。
            /// </summary>
            public string Notice;
            /// <summary>
            /// 点完把设置窗口**整窗重建**一遍。
            /// 「清理日志（当前 546 KB）」这种文字里带着实时数字的项必须重建才看得到新值 ——
            /// 重建本身是个正常的公开动作（切颜色模式就是这么做的），只是会推后一轮跑。
            /// </summary>
            public bool RebuildAfter;
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
        /// 值很别扭（用户：「标签页宽度也可自己输入数值」）。
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

        /// <summary>
        /// 标成「托盘菜单里不排」。用户：托盘右键的设置项太多了，去掉一些、设置窗口里有就行。
        /// 一次性设置（主题 / 捕获方式 / 窗口记忆）、纯动作、能在别处点到的那些全归这里 ——
        /// 托盘里那棵子树原来挤了二十来行，想找哪一项得从头数一遍。
        /// </summary>
        private static Node WinOnly(Node nd) { nd.WindowOnly = true; return nd; }

        /// <summary>
        /// 纯动作项（没有勾选状态，点了就干活）—— 设置窗口里画成一个按钮，托盘菜单里是一条正常可点的项。
        /// `notice` 会给点完的界面一句反馈（设置窗口底部提示行），并且顺带把窗口重建一遍
        /// （文字里带实时数字的项要）。
        /// </summary>
        private static Node Act(string text, Action a, string notice)
        {
            return new Node { Text = text, Click = a, Notice = notice, RebuildAfter = true };
        }

        // ==================================================================
        // 内容（顺序 = 用户要的顺序：捕获方式 → 颜色模式 → 保留标签 → 标签宽度 → 自适应 → 书签栏 → 捕获所有 → 开机自启）
        // ==================================================================

        internal static List<Node> Spec(DesktopHub hub)
        {
            List<Node> n = new List<Node>();

            // ① 标签捕获方式（互斥）
            n.Add(WinOnly(Leaf(Settings.Label(Settings.CaptureMode.PerDesktop),
                       () => Settings.Capture == Settings.CaptureMode.PerDesktop,
                       () => hub.SetCaptureMode(Settings.CaptureMode.PerDesktop), "capture")));
            n.Add(WinOnly(Leaf(Settings.Label(Settings.CaptureMode.Migrate),
                       () => Settings.Capture == Settings.CaptureMode.Migrate,
                       () => hub.SetCaptureMode(Settings.CaptureMode.Migrate), "capture")));
            n.Add(Sep());

            // ② 颜色模式（互斥）
            n.Add(WinOnly(Leaf(Settings.Label(Settings.ColorMode.System),
                       () => Settings.Color == Settings.ColorMode.System,
                       () => hub.SetColorMode(Settings.ColorMode.System), "theme")));
            n.Add(WinOnly(Leaf(Settings.Label(Settings.ColorMode.Light),
                       () => Settings.Color == Settings.ColorMode.Light,
                       () => hub.SetColorMode(Settings.ColorMode.Light), "theme")));
            n.Add(WinOnly(Leaf(Settings.Label(Settings.ColorMode.Dark),
                       () => Settings.Color == Settings.ColorMode.Dark,
                       () => hub.SetColorMode(Settings.ColorMode.Dark), "theme")));
            n.Add(Sep());

            // ③ 是否保留标签页
            n.Add(WinOnly(Leaf("保留标签页（退出后记住、下次还原）",
                       () => Settings.KeepTabs,
                       () => hub.SetKeepTabs(!Settings.KeepTabs))));
            // ③′ 懒加载（默认关）：还原时只真起「当时选中那个」，其余先摆占位、点到才起。
            //     跟「保留标签页」是同一件事的两半（关掉保留就无所谓懒不懒），所以紧挨着排。
            //     它只影响**下次还原**，改完不用重建窗口。
            n.Add(WinOnly(Leaf("懒加载标签页（还原时先只开当前那个，其它点开才加载）",
                       () => Settings.LazyTabs,
                       () => hub.SetLazyTabs(!Settings.LazyTabs))));
            // ③″ 并发起 explorer（默认开）：一次同时起多个，开标签 / 还原标签快得多。
            //     关了就是「一个一个来」（慢，但归属判定最简单）—— 留着它当退路。
            n.Add(WinOnly(Leaf("并发起标签页（一次同时起多个，开标签更快）",
                       () => Settings.ParallelLaunch,
                       () => hub.SetParallelLaunch(!Settings.ParallelLaunch))));
            n.Add(Sep());

            // ③′ 窗口尺寸记忆（用户：做成常规选项、默认启用；托盘菜单里再加一条「恢复默认」）。
            //    「恢复默认」是个动作而不是设置项，但一样收在 Spec 里 ——
            //    托盘菜单和设置窗口两处都能点到，不用各写一遍（这正是 Spec 存在的意义）。
            n.Add(WinOnly(Leaf("记住窗口位置和大小（退出后下次照原样打开）",
                       () => Settings.WindowSize,
                       () => hub.SetWindowSize(!Settings.WindowSize))));
            n.Add(WinOnly(Act("恢复默认窗口位置和大小", delegate { hub.RestoreDefaultWindow(); },
                      "窗口已恢复默认位置和大小。")));
            n.Add(Sep());

            // ③″ 垂直侧边栏（用户：打开/关闭 Ctrl+Shift+,）。样式参考 Edge：
            //    「折叠窗格」开着时，鼠标不在窗格上就收缩成纯图标、进来才临时展开成完整样式。
            //    ⚠ 名字从「垂直标签页」改成「垂直侧边栏」：它现在装的不只是标签 ——
            //      工具、书签段、窗口按钮全在里头，叫「标签页」已经名不副实。
            n.Add(Leaf("垂直侧边栏（左栏：标签 / 书签 / 窗口按钮，Ctrl+Shift+,）",
                       () => Settings.VTabs,
                       () => hub.SetVerticalTabs(!Settings.VTabs)));
            n.Add(Leaf("侧边栏折叠：鼠标不在时只显示图标",
                       () => Settings.VTabsCollapse,
                       () => hub.SetVTabCollapse(!Settings.VTabsCollapse)));
            // 摊开时它盖在内容上 —— 给个半透明能看见后面那个文件夹（数值项，托盘菜单里不排）
            n.Add(Num("侧边栏展开时的不透明度（%，100 = 完全不透明）",
                      Settings.VPaneAlphaMin, Settings.VPaneAlphaMax, 5,
                      delegate { return Settings.VPaneAlpha; },
                      delegate(int a) { hub.SetVPaneAlpha(a); }));
            n.Add(Sep());

            // ④ 标签页宽度（用户：「也可自己输入数值」）
            //    原来是「80/96/112/…」一串预设的子菜单 —— 只能挑不能敲，而且托盘菜单里挂着一层子菜单很难点。
            //    现在改成设置窗口里的数字输入框（64~240 逻辑像素），菜单里不排它。
            n.Add(Num("标签页宽度（逻辑像素，" + Settings.TabWidthMin + " ~ " + Settings.TabWidthMax + "）",
                      Settings.TabWidthMin, Settings.TabWidthMax, 8,
                      delegate { return Settings.TabWidth; },
                      delegate(int w) { hub.SetTabWidth(w); }));

            // ⑤ 自适应宽度（用户要拆成两项：加宽 / 缩窄）
            n.Add(WinOnly(Leaf("自适应宽度：名称过长时自动加宽",
                       () => Settings.TabAutoWiden,
                       () => hub.SetTabAutoWiden(!Settings.TabAutoWiden))));
            n.Add(WinOnly(Leaf("自适应宽度：挤不下时自动缩窄",
                       () => Settings.TabAutoFit,
                       () => hub.SetTabAutoFit(!Settings.TabAutoFit))));
            n.Add(Sep());

            // ⑥ 书签栏（Ctrl+Shift+B）
            n.Add(Leaf("显示书签栏（Ctrl+Shift+B）",
                       () => Settings.FavBar,
                       () => hub.SetFavBar(!Settings.FavBar)));
            // 用户：原来这条是「打开书签栏数据目录」，改成**管理书签** ——
            // 数据目录在他眼里只是个 json 文件，改不动也看不懂；给他一扇仿浏览器的管理窗更实用
            // （树 + 搜索 + 重命名 + 嵌套 + 「设为书签栏」）。窗口里那个「打开 json」按钮仍然直达文件。
            n.Add(WinOnly(Leaf("管理书签…", null, () => hub.OpenFavManager())));
            n.Add(Sep());

            // ⑦ 是否连「从开始菜单 / 桌面双击打开的文件夹」也收成标签
            n.Add(Leaf("捕获所有打开的文件夹（不只 Win+E）",
                       () => Settings.CaptureAll,
                       () => hub.SetCaptureAll(!Settings.CaptureAll)));
            // ⑦′ 接管**桌面 shell 进程开的**那批（默认关，得实测过才敢默认开）：
            //     开始菜单 / 任务栏 / 桌面双击 / 第三方程序（下载器的「打开文件夹」）都是这一类。
            //     这种窗口不能收编（会把 shell 那条 Win+E 入口弄坏），所以走
            //     「读出它的路径 → 关掉它 → 用我们自己的 explorer 重开成标签」（见 DesktopHub）。
            n.Add(Leaf("接管 shell 打开的文件夹（开始菜单 / 第三方程序的「打开文件夹」）",
                       () => Settings.CaptureShell,
                       () => hub.SetCaptureShell(!Settings.CaptureShell)));
            n.Add(Sep());

            // ⑧ 开机自启（用户要的）。状态现问注册表，见 AutoStart。
            //    ⚠ 标签就写「开机自启」四个字，不加括号说明（用户明确说的）。
            n.Add(Leaf("开机自启",
                       () => AutoStart.IsEnabled(),
                       () => hub.SetAutoStart(!AutoStart.IsEnabled())));
            n.Add(Sep());

            // ⑨ 诊断（用户：「是否写入日志，由设置中的 Debug 模式决定，默认不开，
            //    不过我们要开。增加清理日志按钮。」）
            //    日志本身也归到 Spec 里 —— 否则又是「托盘菜单和设置窗口各写一遍」那个老毛病。
            n.Add(Leaf("Debug 模式（把详细过程写进 data\\log.txt）",
                       () => Settings.Debug,
                       () => hub.SetDebug(!Settings.Debug)));
            n.Add(Info("日志文件：data\\log.txt（现在 " + Diag.HumanSize() + "）"));
            n.Add(WinOnly(Act("清理日志", delegate { Diag.Clear(); }, "日志已清空。")));
            n.Add(Sep());

            // ⑩ 常用动作 + 说明
            // 用户问「记住当前标签功能是干嘛的」—— 说明这个标签没讲清自己。
            // 它跟上面「保留标签页」不是一回事：那个是**开关**（开=以后才记），
            // 这个是**动作**（现在立刻把当前各桌面的标签存一次）。自动保存本来就有
            // （改动攒 800ms 落盘 + 退出前再存），所以手动这一下只在「怕它没来得及存」时用。
            n.Add(WinOnly(Leaf("立即记住当前标签（平时自动记，这个是手动存一次）", null, () => hub.RememberNow())));
            // 重启本程序：改完设置想让它从头走一遍（比如验「懒加载」到底有没有生效）时点它。
            // 它是**动作**不是开关；记忆由退出那条路落盘，重启后原样还原。
            // ⚠ notice 给 null：重启就是当场退出，那句提示根本来不及显示（RebuildAfter 会推后一轮重建，
            //    而退出消息已经在队里了）。
            n.Add(WinOnly(Act("重启本程序", delegate { hub.RestartApp(); }, null)));
            n.Add(Info("数据目录：程序目录\\data（settings / desktops / history / favorites 四个 json）"));
            // 用户问「自带资源管理器左上角的功能不能一起捕获吗」—— 答案是不能。
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
            // 用户：「更多选项放到第一条」。菜单里只留几个顺手能切的东西，其余全在设置窗口里，
            // 所以这个入口必须一眼就在最上面 —— 原来压在二十来条下面，找它得先把整个菜单扫一遍。
            ts.Root.MenuItems.Add(MenuFx.Item("更多选项…（打开设置窗口）", delegate { hub.OpenSettings(); }));
            ts.Root.MenuItems.Add(MenuFx.Sep());
            ts.Root.MenuItems.AddRange(ToMenus(Spec(hub), ts));
            return ts;
        }

        private static MenuItem[] ToMenus(List<Node> nodes, TraySettings ts)
        {
            List<MenuItem> r = new List<MenuItem>();
            List<bool> sep = new List<bool>();                    // 与 r 一一对应：这一条是不是分隔条
            foreach (Node nd in nodes)
            {
                if (nd.WindowOnly) continue;                      // 只在设置窗口里出现（数值项 / 说明行 / 一次性设置）

                if (nd.Text == null)
                {
                    // 被 WindowOnly 摘掉的项一撤，原来夹在两段之间的分隔条就挨到一起了（甚至顶到最前、
                    // 留在最后）—— 菜单里连着两条横线看着像出了 bug，这里合掉：首尾不留、连着只留一条。
                    if (r.Count == 0 || sep[sep.Count - 1]) continue;
                    r.Add(MenuFx.Sep());
                    sep.Add(true);
                    continue;
                }

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
                sep.Add(false);
            }

            while (r.Count > 0 && sep[sep.Count - 1]) { r.RemoveAt(r.Count - 1); sep.RemoveAt(sep.Count - 1); }
            return r.ToArray();
        }
    }
}
