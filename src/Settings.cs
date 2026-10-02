using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TabbedExplorer
{
    /// <summary>
    /// 全局设置，JSON `&lt;程序目录&gt;\data\settings.json`（**绿色便携**，见 AppPaths）。
    ///
    /// 现在这些项（）：
    ///   capture    = perdesktop | migrate   标签捕获方式（每桌面一个窗口 / 全进程一个窗口 + Win+E 搬过来）
    ///   keeptabs   = 1 | 0                  是否保留标签页（退出后记住、下次还原）
    ///   theme      = system | light | dark  颜色模式
    ///   tabwidth   = &lt;逻辑像素&gt;            标签页宽度（开了 tabautowiden 时=单个标签最宽能到多少）
    ///   tabautowiden = 1 | 0                自适应宽度①：文件夹名过长时自动加宽
    ///   tabautofit = 1 | 0                  自适应宽度②：挤不下时自动缩窄
    ///   favbar     = 1 | 0                  书签栏显不显示（Ctrl+Shift+B）
    ///   captureall = 1 | 0                  是否把「从开始菜单/桌面打开的文件夹」也收成标签
    ///   winsize    = 1 | 0                  退出后记住窗口位置和大小（默认开，按虚拟桌面分别记）
    ///   vtabs      = 1 | 0                  垂直侧边栏（标签竖排在左边窗格，Ctrl+Shift+,）
    ///   vtabscollapse = 1 | 0               垂直窗格的「折叠窗格」：鼠标不在窗格上时只显示图标
    ///   vpanealpha = 60 ~ 100               侧边栏盖在内容上那一下的不透明度（%，100 = 不透明）
    ///   autopreload = 1 | 0                 预加载：程序起来后**没开窗口也**先把本桌面记着的标签在后台起出来
    ///   autosleep  = 1 | 0                  非激活标签自动休眠（切走的标签停留够久就收回它的驻留内存）
    ///   winepc     = 1 | 0                  Win+E 时顺手开一个「此电脑」标签（默认关：只把窗口唤到前面，现有标签一个都不动）
    ///   debug      = 1 | 0                  是否把详细过程写进 data\log.txt（默认关，见 Diag）
    ///
    /// ⚠ 用 JSON 而不是 `key=value`（用户要求「配置项文件用 json 格式」）：
    /// JSON 本体不支持注释，所以说明写在 `_` 开头的键里 —— 那既是**合法 JSON**（任何工具都读得动），
    /// 又能让用户打开文件就看见每一项是什么意思。读的是老 `settings.txt` 也没事：
    /// `Load` 会把它读进来、写成 json，再把老文件改名成 `.migrated` 留着（不删）。
    /// 解析统一走 `Json`（src/Json.cs）—— `desktops.json` / `history.json` / `favorites.json` 共用一套。
    /// </summary>
    internal static class Settings
    {
        public enum CaptureMode
        {
            /// <summary>按虚拟桌面分别捕获。</summary>
            PerDesktop,
            /// <summary>捕获并迁移到当前桌面（v1.0.0 的老行为）。</summary>
            Migrate
        }

        public enum ColorMode
        {
            /// <summary>跟随系统「应用模式」。</summary>
            System,
            Light,
            Dark
        }

        private const string Header = "# TabbedExplorer settings v2";

        // ---- 默认值 ----
        public static CaptureMode Capture = CaptureMode.PerDesktop;
        public static bool KeepTabs = true;
        /// <summary>
        /// 懒加载标签页（默认**关**）。
        ///
        /// 开：还原记忆里的标签时只把「当时选中的那个」真起出来，其余先摆成占位（只有标题和路径），
        /// 点到它才去起 explorer。省的是**开程序那一下的等待** —— 起 explorer 是串行的（见
        /// `EmbedForm.PumpLaunch`），一个约 1.2 秒，7 个标签就是八秒多。
        /// 关：按顺序把每个标签都起起来（跟以前一样）。
        ///
        /// 为什么做成开关而不是一直开着：用户先试过「一律懒加载」，觉得点到还没加载的标签要等一下、
        /// 体验不好，所以留成选择项、默认关。
        /// </summary>
        public static bool LazyTabs = false;
        /// <summary>
        /// 预加载（默认**开**）：程序起来之后，**就算还没打开窗口**，也在后台把「当前这张桌面」记着的标签
        /// 摆好、把 explorer 起出来 —— 用户第一次按 Win+E 时窗口一出来就是热的，不用再等那一两秒。
        ///
        /// 跟 <see cref="LazyTabs"/> 正好是两头：那个是「还原时别急着起」，这个是「还没打开就先起」。
        /// 两者不冲突 —— 预加载走的就是**正常还原**那条路（`EmbedForm.PreloadTabs` 直接调
        /// `RestoreRememberedTabs`），所以懒加载开着时一样只起选中那个。
        ///
        /// ⚠ 它**不**破坏「非激活标签自动休眠」（见 <see cref="AutoSleep"/>）：预加载出来的那批标签
        /// 一样算「非激活」，一样会在停留够久之后被收掉驻留内存 —— 进程还活着，只是那些页被换出去了，
        /// 切回去时按需读回。这正是用户要的「预加载但不破坏自动休眠」。
        /// ⚠ 只预加载**当前这张虚拟桌面**：别的桌面的窗口得建在别的桌面上（`EmbedForm` 的归属按它实际
        /// 挂在哪张桌面认，见 `DesktopHub.EnsureForm`），在这儿替它们建会建错桌面、反而乱。
        /// </summary>
        public static bool AutoPreload = true;
        /// <summary>
        /// 非激活标签自动休眠（默认**开**）：切走的标签停留够久（默认 3 秒，见 <see cref="SleepDelaySec"/>），
        /// 就把它那个**独立 explorer 进程**的驻留内存收一收（`EmptyWorkingSet`）——
        /// 进程本身、它开的窗口、里面的状态**一动都不动**，只是那些页下次被访问时按需读回，
        /// 所以切回去会有极短的一点「加载感」。见 `EmbedForm.TrimInactiveTabs` / `ExplorerHost.TrimMemory`。
        ///
        /// 关掉 = 一个都不收（常驻内存更高，但切标签永远是最快的）。
        /// </summary>
        public static bool AutoSleep = true;
        /// <summary>
        /// 非激活标签**停留多久之后**才收内存（秒，默认 3 —— 见 <see cref="AutoSleep"/>）。
        ///
        /// 为什么要有这个数：收内存（`EmptyWorkingSet`）本身是好事，但**收完再切回去要按需读回**，
        /// 有一点点「加载感」。所以只有「切走够久」的标签才值得收。用户来回对比两个目录就调大，
        /// 想省内存就调小。范围 <see cref="SleepDelaySecMin"/> ~ <see cref="SleepDelaySecMax"/>。
        /// 实时生效：`EmbedForm.ArmTrim` 每次重排都现读这个值（见 `DesktopHub.SetSleepDelay`）。
        /// </summary>
        public static int SleepDelaySec = 3;
        /// <summary>「非激活标签多久之后收内存」的合法范围（秒）。</summary>
        public const int SleepDelaySecMin = 1;
        public const int SleepDelaySecMax = 300;
        /// <summary>
        /// 空格键预览（默认**开**）：把「在标签里按空格调 QuickLook 预览选中项」接过来。
        ///
        /// 为什么得由我们自己接：QuickLook 的判据**只有一条** —— 读**前台窗口的类名**
        /// （`CabinetWClass` / `ExploreWClass` / `ShellTabWindowClass` / `dopus.lister` /
        /// `EVERYTHING` / `DUIViewWndClassName` / `WorkerW` / `Progman` / `#32770`）。
        /// 原生资源管理器是 `CabinetWClass`，它认；我们的宿主是自绘窗体，它一律判 `Invalid`、
        /// 整段逻辑根本不触发 —— 用户「我在原生资源管理器明明是没问题的」就是这个原因。
        ///
        /// 怎么接、怎么转发见 `QLPreview` 的类注释。关掉 = 空格照常落到内嵌的列表上。
        /// ⚠ 只在「前台是我们自己的窗口」且「真选中了东西」且「找得到 QuickLook.exe」时才动作；
        ///   Ctrl / Alt / Shift + 空格 一律不碰（Ctrl+空格 是输入法切换）。
        /// </summary>
        public static bool QLPreview = true;
        /// <summary>
        /// 「右下角通知」（默认**开**）：操作反馈那种气泡弹不弹（「已复制完整路径」「留一个」
        /// 「TabbedExplorer 还在后台」…… 就是屏幕右下角冒一下的那一条）。
        ///
        /// 关掉**只**影响这些气泡的显示 —— 功能一件不少，只是不再弹那一下。
        /// 总闸挂在 `Toast.Show` 头上：全项目所有气泡都从那儿出去，所以开关一处就全关。
        ///
        /// 总闸下面是**逐条**的开关：每一条通知是一个 `NotifyItem`（22 条，见 `NotifyItems`），
        /// 条目按大类在设置页里成节显示（用户：「不够细，既然都做通知管理了，比如「出错与失败」
        /// 算一列，用一条线隔开，然后具体在里面的明细可以多选」）。
        ///
        /// ⚠ 存储刻意**不**做成 22 个字段：只有一张覆盖表 `NotifyOver`（json 里是 `notifyover`），
        ///   键 = 条目 id、值 = 用户明确设过的状态；**不在表里 = 跟随默认**
        ///   （默认 = `DefNotify(Debug, 条目自己的 Important)`，见 `NotifyItemBase`）。好处：
        ///   ① 以后加条目不用动文件格式 ② 文件里只留用户真动过的那几条
        ///   ③ Debug 一开，没动过的条目自动全开（用户：「Debug 模式默认开启全部，
        ///   非 Debug 模式只默认开启重要的部分」）。
        /// </summary>
        public static bool Notify = true;
        /// <summary>
        /// 逐条覆盖表：`条目 id -> 用户设的状态`。**只存跟默认不一样的**（跟默认一样就把键删掉，
        /// 见 `NotifyItemSet`）—— 这样 Debug 那种「默认全开」还能继续跟着走。
        /// </summary>
        public static readonly Dictionary<string, bool> NotifyOver =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        /// <summary>
        /// 一条气泡**停留多少秒**后自己收（默认 4，「通知管理」页那个数字框）。
        /// 原来是写死的 4200ms；用户要「通知管理」页里能调。
        /// ⚠ 从**弹的那一刻**现读（见 `Toast.SetText`）⇒ 改了立刻生效，已经在屏幕上的那条不变。
        /// </summary>
        public static int NotifySec = 4;
        /// <summary>「停留多久」的合法范围（秒）。</summary>
        public const int NotifySecMin = 1;
        public const int NotifySecMax = 30;
        /// <summary>
        /// 并发起 explorer（默认**开**）。
        ///
        /// 关着的时候是一个一个来（`EmbedForm.PumpLaunch` 从前只放一个）：开标签慢，但「新出现的窗口就是我的」
        /// 这条不证自明，不用判归属。开着就一次放好几个 —— 快得多（实测 7 个标签从 8.4s 降到 ~2s），
        /// 代价是每个标签得**自己证明那个窗口是它的**：等到地址栏能读了、内容正好是它要开的那个才认领
        /// （见 `EmbedApi.FindNewCab` 的 wantedPath）。所以并发下每次起窗口会多等约 0.5s（等地址栏）。
        /// </summary>
        public static bool ParallelLaunch = true;
        public static ColorMode Color = ColorMode.System;
        public static int TabWidth = 112;
        /// <summary>
        /// 自适应宽度（一）：**文件夹名过长时自动加宽** —— 每个标签按自己那行文字的宽度来定，
        /// 上限就是 `TabWidth`。关了就是所有标签一律 `TabWidth` 宽（名字长了打省略号）。
        /// </summary>
        public static bool TabAutoWiden = true;
        /// <summary>
        /// 自适应宽度（二）：**挤不下时自动缩窄** —— 全排标签加起来超过可用宽度就等比缩到能放下。
        /// 关了就不缩（总宽仍然不越过右边那排按钮，多出来的标签要靠横向滚动才看得到）。
        /// 用户把这一个拆成了两项（原来只有这个「自适应宽度」）。
        /// </summary>
        public static bool TabAutoFit = true;
        /// <summary>
        /// 新建标签开在**当前标签旁边**（默认**关** = 跟在最末尾，也就是一直以来的行为）。
        ///
        /// 用户：「新建标签出现在现标签页旁边，还是最后面」。
        /// 开 = 插到当前标签右边（`EmbedForm.NextTabIndex` → `activeIndex + 1`），连着开一串
        /// 就会挤在同一个地方长大；关 = 一律追加到末尾。
        ///
        /// 只影响**新建**的标签。两条不受它管：
        ///   ① 「恢复关闭的标签页」永远回**它原来那个下标**（见 `EmbedForm.ReopenClosedTab`）；
        ///   ② 还原记忆标签按记忆里的顺序摆（追加），跟这个开关无关。
        /// 实时生效：`NextTabIndex` 每次新建时现读。
        /// </summary>
        public static bool NewTabBeside = false;
        /// <summary>
        /// Win+E 时**顺手开一个「此电脑」标签**（默认**关**）。
        ///
        /// 关（默认）：Win+E 只把窗口唤到最前面 —— **一个标签都不动**，原来停在哪个标签就还是哪个。
        ///   想开新页直接 `Ctrl+T`，不打断手上这条。
        /// 开：跟老行为一样，唤窗口的同时开一个「此电脑」（已经开着就切过去）。
        ///
        /// ⚠ 跟这个开关**无关**的两处：「第一次 Win+E」（窗口还空着、也没有记忆）照样会给一个
        ///   「此电脑」，那是 `EmbedForm.EnsureFirstTab` 的兜底 —— 不然窗口空着没法用；
        ///   托盘 / 双击的那条路也照旧。
        ///
        /// 用户：「把关闭我们程序窗口后按 Win+E 打开新的『此电脑』做成可选项，默认关闭 ——
        ///   Win+E 唤醒后直接 Ctrl+T 更方便，还不影响原来激活的标签页，转移注意力。」
        /// </summary>
        public static bool WinENewPc = false;
        /// <summary>
        /// **启动时就把主窗口打开**（默认**开**）。
        ///
        /// 管的是「人主动启动这个程序」的两条路，**口径一致**（2026-10-02 川：
        /// 「从图标启动并没有默认打开我们的主程序。重启却打开了。加个开关统一一下，默认都打开。」）：
        ///   · 双击图标 / 快捷方式 / 裸起 exe（命令行里既没 `--open` 也没 `--tray`）；
        ///   · 托盘 / 设置里的「重启本程序」（`DesktopHub.RestartApp`）。
        /// 关掉 = 这两条都只驻托盘（装托盘图标 + Win+E 钩子），窗口等第一次 Win+E 才出来。
        ///
        /// ⚠ **开机自启不受这里管**：那条路命令行固定带 `--tray`（见 `AutoStart.CommandLine`），
        ///   开机永远不弹窗 —— 川明确要求「开机自启不用打开」。
        /// ⚠ `--open` / `--tray` 这两个显式参数**优先于**本开关（先后顺序在 `Program.Main` 里定）。
        /// </summary>
        public static bool StartOpenWindow = true;
        /// <summary>书签栏是否显示（Ctrl+Shift+B）。</summary>
        public static bool FavBar = false;
        /// <summary>
        /// 是否捕获**所有**打开的文件夹窗口（不只是 Win+E）。
        /// 开：从开始菜单 / 桌面双击打开的文件夹也会被收成标签（浏览器行为）。
        /// 关：只接管 Win+E，其他照旧开原生窗口。
        /// </summary>
        public static bool CaptureAll = true;
        /// <summary>
        /// 接管**桌面 shell 进程开的**文件夹窗口（默认开）。
        ///
        /// 背景：从开始菜单 / 任务栏 / 桌面双击 / 第三方程序（Rayburst 这类）打开文件夹，
        /// 窗口是**桌面 shell 那个 explorer 进程**建的（`EmbedApi.IsShellOwned`）——
        /// 收编这种窗口（SetParent 进我们的窗口）会让 shell 那条「打开资源管理器」入口
        /// （Win+E 与开始菜单共用）去激活一扇坏窗口，而且退了程序也不恢复，所以这条路只读、只关：
        /// 把真实路径从地址栏读出来 → 像用户点 × 一样关掉它（SC_CLOSE）→ 用我们自己的
        /// `explorer /n,/separate` 把同一个文件夹开成标签（见 DesktopHub.TakeOverShellWindow）。
        /// **全程不碰它的窗口对象**（不 SetParent、不改样式位）：代价只是那扇原生窗会可见地闪一下。
        /// 关：维持 v1.13.1 的行为，这种窗口一个都不收（从开始菜单/桌面双击的文件夹也就进不了标签）。
        /// </summary>
        public static bool CaptureShell = true;
        /// <summary>
        /// 退出后**记住窗口位置和大小**（默认开）。真源在 `desktops.json` 每张桌面的 `bounds` 里 ——
        /// 这里只是一个开关：关了就不记、也不还原（下次起来还是默认尺寸）。
        /// </summary>
        public static bool WindowSize = true;
        /// <summary>垂直侧边栏（Ctrl+Shift+,）：标签竖排在左边窗格，参考 Edge 的垂直侧边栏。</summary>
        public static bool VTabs = false;
        /// <summary>
        /// 垂直窗格的「折叠窗格」（窗格顶部那枚图钉）：开 = 鼠标不在窗格上时收缩成纯图标、
        /// 鼠标一进来临时展开成完整（图标 + 标题）；关 = 一直显示完整标题。
        /// </summary>
        public static bool VTabsCollapse = true;
        /// <summary>
        /// 垂直侧边栏「临时摊开、盖在内容上」那一下的不透明度（%）：100 = 完全不透明，越小越透。
        /// 折叠窗格开着时窗格会比占位宽、盖住内容一截（见 `PaneShowWidth`）——
        /// 透一点能看见后面那个文件夹，不至于像一块板子糊在脸上。只在「盖住内容」时生效。
        /// </summary>
        public static int VPaneAlpha = 80;

        /// <summary>
        /// 不透明度的合法范围（%）。**下限就是 0**（用户：「不要限制范围」）——
        /// 0 = 完全透明，那时窗格上的字基本看不见了，但那是用户自己的选择。
        /// </summary>
        public const int VPaneAlphaMin = 0;
        public const int VPaneAlphaMax = 100;

        /// <summary>
        /// 垂直侧边栏里**书签段**的高度（逻辑像素）。
        /// 用户：「书签的占比小了，这里可以加一个可以调整的横条，调整整个书签项目的上下位置」——
        /// 在标签区与书签段之间那根横条上拖出来的值，拖完记住（所有窗口一起用）。
        /// **0 = 自动**（按书签条数算，最多占窗格高的三分之一）—— 用户没拖过就是这个。
        /// </summary>
        public static int FavBandHeight = 0;

        /// <summary>书签段高度的范围（逻辑像素）。0 单独当「自动」用，别跟最小值搅在一起。</summary>
        public const int FavBandHeightMin = 0;
        public const int FavBandHeightMax = 900;

        /// <summary>
        /// Debug 模式：把每一步的详细过程写进 `data\log.txt`。
        ///
        /// 用户：「是否写入日志，由设置中的 Debug 模式决定，默认不开，不过我们要开」。
        /// 默认关（普通用户不需要一个会一直变大的文件），本机自己那份 settings.json 里开着。
        /// 它只影响 `Diag` 写不写盘，**不影响任何功能**。
        /// </summary>
        public static bool Debug = false;

        /// <summary>标签宽度的合法范围（逻辑像素）。太小就点不中了，太大一屏放不下两个。</summary>
        public const int TabWidthMin = 64;
        public const int TabWidthMax = 240;

        /// <summary>
        /// 「名称过长时自动加宽」**最多能加到多宽**（逻辑像素）。
        /// 注意这是**另一个上限**，不是 `TabWidth`：
        ///   开自动加宽时 `TabWidth` 是**基准宽度**，名字放不下才往上加，最多加到这里。
        /// 用户报「单标签页并未加宽」的根因就是原来把它卡在 `TabWidth` 上 ——
        /// 名字再长，宽也超不过 `TabWidth`，看着就是「没加宽」。
        /// </summary>
        public const int TabWidenMax = 240;

        // ==================================================================
        // 快捷键（用户：设置窗口新增「快捷键」页，程序自己的热键可改）
        //
        // 只存「命令 → 组合键文本」这一层；解析 / 匹配在 `Hotkeys`（src/Hotkeys.cs）。
        // 存成**扁平键** `hotkey_<命令>`（Json.cs 是手写的单层解析，不认嵌套对象）。
        // ==================================================================

        /// <summary>可自定义的命令（顺序 = 设置窗口里显示的顺序）。</summary>
        public static readonly string[] HotkeyKeys = new string[]
        {
            "newtab", "newwin", "closetab", "nexttab", "prevtab", "history", "reopen", "favbar", "vtabs"
        };

        /// <summary>各项的默认组合键（跟浏览器对齐那一套）。</summary>
        public static readonly string[] HotkeyDefaults = new string[]
        {
            "Ctrl+T", "Ctrl+N", "Ctrl+W", "Ctrl+Tab", "Ctrl+Shift+Tab", "Ctrl+H", "Ctrl+Shift+T", "Ctrl+Shift+B",
            // 垂直侧边栏：跟 Edge 对齐 —— Ctrl+Shift+,（用户指定）
            "Ctrl+Shift+,"
        };

        private static readonly Dictionary<string, string> hotkeys =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>某个命令现在绑的组合键文本（没设过 / 设空了 ⇒ 用默认）。</summary>
        public static string Hotkey(string cmd)
        {
            string v;
            if (!string.IsNullOrEmpty(cmd) && hotkeys.TryGetValue(cmd, out v) && !string.IsNullOrEmpty(v))
                return v;
            for (int i = 0; i < HotkeyKeys.Length; i++)
                if (string.Equals(HotkeyKeys[i], cmd, StringComparison.OrdinalIgnoreCase))
                    return HotkeyDefaults[i];
            return "";
        }

        /// <summary>改一条快捷键并立刻落盘。传空串 = 恢复默认。</summary>
        public static void SetHotkey(string cmd, string combo)
        {
            if (string.IsNullOrEmpty(cmd)) return;
            if (string.IsNullOrEmpty(combo)) hotkeys.Remove(cmd);
            else hotkeys[cmd] = combo;
            Save();
        }

        /// <summary>全部恢复默认（设置窗口「快捷键」页那个按钮）。</summary>
        public static void ResetHotkeys()
        {
            hotkeys.Clear();
            Save();
        }

        /// <summary>设置文件 = `&lt;程序目录&gt;\data\settings.json`（JSON，见类注释）。</summary>
        public static string FileName { get { return AppPaths.File("settings.json"); } }

        /// <summary>老版本的纯文本设置文件 —— 只在迁移时读一次。</summary>
        public static string LegacyFileName { get { return AppPaths.File("settings.txt"); } }

        public static void Load()
        {
            try
            {
                if (File.Exists(FileName))
                {
                    string json = File.ReadAllText(FileName, Encoding.UTF8);
                    Capture      = ParseCapture(Json.Get(json, "capture"));
                    KeepTabs     = Json.GetBool(json, "keeptabs", true);
                    LazyTabs     = Json.GetBool(json, "lazytabs", false);
                    ParallelLaunch = Json.GetBool(json, "parallel", true);
                    AutoPreload  = Json.GetBool(json, "autopreload", true);
                    AutoSleep    = Json.GetBool(json, "autosleep", true);
                    SleepDelaySec = ClampSleepSec(Json.GetInt(json, "sleepdelay", SleepDelaySec));
                    QLPreview    = Json.GetBool(json, "qlpreview", true);
                    NewTabBeside = Json.GetBool(json, "newtabbeside", false);
                    WinENewPc    = Json.GetBool(json, "winepc", false);
                    StartOpenWindow = Json.GetBool(json, "startopen", true);
                    // ⚠ Debug 得**先**读 —— 下面通知那四类的默认值要看它（用户：「Debug 模式默认
                    //   开启全部，非 Debug 模式只默认开启重要的部分」），不能等到底下那一行。
                    bool dbg = Json.GetBool(json, "debug", false);
                    Debug        = dbg;          // 先落地 —— 下面通知每一条的默认值要看它
                    Notify       = Json.GetBool(json, "notify", true);
                    NotifySec    = ClampNotifySec(Json.GetInt(json, "notifysec", NotifySec));
                    NotifyOver.Clear();
                    LoadNotifyOver(Json.Get(json, "notifyover"));
                    // 兼容 2026-09-28 那版「只有四个大类开关」的 json：整类套一遍
                    LegacyNotifyKind(json, "notifyerr",  ToastKind.Err);
                    LegacyNotifyKind(json, "notifysys",  ToastKind.Sys);
                    LegacyNotifyKind(json, "notifyok",   ToastKind.Ok);
                    LegacyNotifyKind(json, "notifyhint", ToastKind.Hint);
                    Color        = ParseColor(Json.Get(json, "theme"));
                    TabWidth     = ClampWidth(Json.GetInt(json, "tabwidth", TabWidth));
                    TabAutoFit   = Json.GetBool(json, "tabautofit", true);
                    TabAutoWiden = Json.GetBool(json, "tabautowiden", true);
                    FavBar       = Json.GetBool(json, "favbar", false);
                    CaptureAll   = Json.GetBool(json, "captureall", true);
                    CaptureShell = Json.GetBool(json, "captureshell", true);
                    WindowSize   = Json.GetBool(json, "winsize", true);
                    VTabs        = Json.GetBool(json, "vtabs", false);
                    VTabsCollapse = Json.GetBool(json, "vtabscollapse", true);
                    VPaneAlpha   = ClampAlpha(Json.GetInt(json, "vpanealpha", VPaneAlpha));
                    FavBandHeight = ClampFavBand(Json.GetInt(json, "favbandheight", FavBandHeight));
                    Diag.Enabled = Debug;        // 读完才是最终口径（见 Diag.Enabled 的说明）
                    hotkeys.Clear();
                    for (int i = 0; i < HotkeyKeys.Length; i++)
                    {
                        string v = Json.Get(json, "hotkey_" + HotkeyKeys[i]);
                        // 读到的跟默认一样就不存 —— 让文件里只留「用户真改过」的那几条
                        if (!string.IsNullOrEmpty(v) &&
                            !string.Equals(v, HotkeyDefaults[i], StringComparison.OrdinalIgnoreCase))
                            hotkeys[HotkeyKeys[i]] = v;
                    }
                    Diag.Step("设置: " + Describe());
                    return;
                }

                // 没 json：有老的 settings.txt 就先读过来、写成 json，再把老文件改名留着
                // （不直接删 —— 万一新格式哪里不对，老的还在）
                if (File.Exists(LegacyFileName))
                {
                    Diag.Step("设置: 发现老的 settings.txt，迁移到 settings.json");
                    LoadLegacyText();
                    Diag.Enabled = Debug;
                    Save();
                    try { File.Move(LegacyFileName, LegacyFileName + ".migrated"); } catch { }
                    Diag.Step("设置: 迁移完成 " + Describe());
                    return;
                }

                // 两样都没有 ⇒ 写一份默认的，用户打开就能改
                Diag.Step("设置: 还没有 " + FileName + "（写一份默认的）");
                Save();
                Diag.Enabled = Debug;
            }
            catch (Exception ex)
            {
                Diag.Log("设置: 读失败（全用默认）" + ex.Message);
            }
        }

        /// <summary>读老格式（`key=value` 纯文本，v1/v2）。</summary>
        private static void LoadLegacyText()
        {
            string[] lines = File.ReadAllLines(LegacyFileName, Encoding.UTF8);
            // ⚠ 先扫一遍 debug —— 通知那四类的默认值要看它（同 `Load` 里的说明）；
            //   老的 settings.txt 里 debug 可能排在 notify 后面，边读边判就来不及。
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                if (line.Substring(0, eq).Trim().ToLowerInvariant() == "debug")
                    Debug = ParseBool(line.Substring(eq + 1).Trim(), false);
            }
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                string v = line.Substring(eq + 1).Trim();
                switch (k)
                {
                    case "capture":    Capture = ParseCapture(v); break;
                    case "keeptabs":   KeepTabs = ParseBool(v, true); break;
                    case "lazytabs":   LazyTabs = ParseBool(v, false); break;
                    case "parallel":   ParallelLaunch = ParseBool(v, true); break;
                    case "autopreload": AutoPreload = ParseBool(v, true); break;
                    case "autosleep":   AutoSleep = ParseBool(v, true); break;
                    case "sleepdelay":  SleepDelaySec = ClampSleepSec(ParseInt(v, SleepDelaySec)); break;
                    case "qlpreview":   QLPreview = ParseBool(v, true); break;
                    case "newtabbeside": NewTabBeside = ParseBool(v, false); break;
                    case "winepc":      WinENewPc = ParseBool(v, false); break;
                    case "startopen":   StartOpenWindow = ParseBool(v, true); break;
                    case "notify":      Notify = ParseBool(v, true); break;
                    case "notifyover":  LoadNotifyOver(v); break;
                    case "notifysec":   NotifySec = ClampNotifySec(ParseInt(v, NotifySec)); break;
                    case "notifyerr":   NotifyKindApply(ToastKind.Err,  ParseBool(v, DefNotify(Debug, true))); break;
                    case "notifysys":   NotifyKindApply(ToastKind.Sys,  ParseBool(v, DefNotify(Debug, true))); break;
                    case "notifyok":    NotifyKindApply(ToastKind.Ok,   ParseBool(v, DefNotify(Debug, false))); break;
                    case "notifyhint":  NotifyKindApply(ToastKind.Hint, ParseBool(v, DefNotify(Debug, false))); break;
                    case "theme":      Color = ParseColor(v); break;
                    case "tabwidth":   TabWidth = ClampWidth(ParseInt(v, TabWidth)); break;
                    case "tabautofit":   TabAutoFit = ParseBool(v, true); break;
                    case "tabautowiden": TabAutoWiden = ParseBool(v, true); break;
                    case "favbar":       FavBar = ParseBool(v, false); break;
                    case "captureall":   CaptureAll = ParseBool(v, true); break;
                    case "captureshell": CaptureShell = ParseBool(v, true); break;
                    case "winsize":      WindowSize = ParseBool(v, true); break;
                    case "vtabs":        VTabs = ParseBool(v, false); break;
                    case "vtabscollapse": VTabsCollapse = ParseBool(v, true); break;
                    case "vpanealpha":    VPaneAlpha = ClampAlpha(ParseInt(v, VPaneAlpha)); break;
                    case "favbandheight": FavBandHeight = ClampFavBand(ParseInt(v, FavBandHeight)); break;
                    case "debug":        Debug = ParseBool(v, false); break;
                }
            }
        }

        /// <summary>
        /// 设置窗口里改一项是**先攒着**的：`SettingsForm.ApplyNode` 在那一次同步点击里
        /// `BeginHold()`..`EndHold()`，这期间的 `Save()` 全部空转，等用户点「保存」才真落盘。
        /// 托盘菜单那条路也在同一根界面线程上、不可能跟它交错，所以不会被误挡。
        /// </summary>
        private static int saveHold;

        public static void BeginHold() { saveHold++; }
        public static void EndHold() { if (saveHold > 0) saveHold--; }

        /// <summary>
        /// 一份设置值的快照 —— **设置窗口**开窗时取一份当底（`SettingsForm.baseline`）：
        ///   ① 判「有没有还没保存的改动」时拿它跟当前值比；
        ///   ② 用户点「不保存」时用它把值回滚回去。
        ///
        /// ⚠ 「开机自启」不在里面：它的真相在注册表（见 `DesktopHub.SetAutoStart`），
        ///   不落 settings.json，所以本来就不属于「攒着等保存」这一套。
        /// </summary>
        internal sealed class Snapshot
        {
            public CaptureMode Capture; public ColorMode Color;
            public bool KeepTabs, LazyTabs, ParallelLaunch, TabAutoWiden, TabAutoFit,
                        FavBar, CaptureAll, CaptureShell, WindowSize, VTabs, VTabsCollapse, Debug,
                        AutoPreload, AutoSleep, Notify, QLPreview, NewTabBeside, WinENewPc, StartOpenWindow;
            public int TabWidth, VPaneAlpha, FavBandHeight, SleepDelaySec, NotifySec;
            /// <summary>逐条覆盖表的一份拷贝（`Snap` 抄一份出来、`ApplySnapshot` 抄回去）。</summary>
            public readonly Dictionary<string, bool> NotifyOver =
                new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, string> Hotkeys =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>把**现在**的值抄一份（不碰磁盘、不碰 hub）。</summary>
        public static Snapshot Snap()
        {
            Snapshot s = new Snapshot();
            s.Capture = Capture; s.Color = Color;
            s.KeepTabs = KeepTabs; s.LazyTabs = LazyTabs; s.ParallelLaunch = ParallelLaunch;
            s.AutoPreload = AutoPreload; s.AutoSleep = AutoSleep; s.SleepDelaySec = SleepDelaySec;
            s.QLPreview = QLPreview;
            s.Notify = Notify;
            s.NotifySec = NotifySec;
            foreach (KeyValuePair<string, bool> kv in NotifyOver) s.NotifyOver[kv.Key] = kv.Value;
            s.TabAutoWiden = TabAutoWiden; s.TabAutoFit = TabAutoFit; s.NewTabBeside = NewTabBeside;
            s.WinENewPc = WinENewPc;
            s.StartOpenWindow = StartOpenWindow;
            s.FavBar = FavBar; s.CaptureAll = CaptureAll; s.CaptureShell = CaptureShell;
            s.WindowSize = WindowSize; s.VTabs = VTabs; s.VTabsCollapse = VTabsCollapse;
            s.Debug = Debug; s.TabWidth = TabWidth; s.VPaneAlpha = VPaneAlpha;
            s.FavBandHeight = FavBandHeight;
            s.SleepDelaySec = SleepDelaySec;
            foreach (KeyValuePair<string, string> kv in hotkeys) s.Hotkeys[kv.Key] = kv.Value;
            return s;
        }

        /// <summary>
        /// 把一份快照写回静态字段（**不落盘**，`Save()` 该空转还空转）。
        ///
        /// ⚠ 它只管 `Settings` 自己这一层。用户点「不保存」时「让已经立刻生效的那些改动退回去」
        /// 还得先叫 `DesktopHub.RestoreSettings(snap)`（趁 `Settings` 里还是改过的值、
        /// 每个 `SetXxx` 才会真的执行），顺序见 `SettingsForm.DiscardEdits`。
        /// </summary>
        public static void ApplySnapshot(Snapshot s)
        {
            if (s == null) return;
            Capture = s.Capture; Color = s.Color;
            KeepTabs = s.KeepTabs; LazyTabs = s.LazyTabs; ParallelLaunch = s.ParallelLaunch;
            AutoPreload = s.AutoPreload; AutoSleep = s.AutoSleep; SleepDelaySec = s.SleepDelaySec;
            QLPreview = s.QLPreview;
            Notify = s.Notify;
            NotifySec = s.NotifySec;
            NotifyOver.Clear();
            foreach (KeyValuePair<string, bool> kv in s.NotifyOver) NotifyOver[kv.Key] = kv.Value;
            TabAutoWiden = s.TabAutoWiden; TabAutoFit = s.TabAutoFit; NewTabBeside = s.NewTabBeside;
            WinENewPc = s.WinENewPc;
            StartOpenWindow = s.StartOpenWindow;
            FavBar = s.FavBar; CaptureAll = s.CaptureAll; CaptureShell = s.CaptureShell;
            WindowSize = s.WindowSize; VTabs = s.VTabs; VTabsCollapse = s.VTabsCollapse;
            Debug = s.Debug; TabWidth = s.TabWidth; VPaneAlpha = s.VPaneAlpha;
            FavBandHeight = s.FavBandHeight;
            hotkeys.Clear();
            foreach (KeyValuePair<string, string> kv in s.Hotkeys) hotkeys[kv.Key] = kv.Value;
        }

        /// <summary>
        /// 当前这些值序列化成 settings.json 的那份正文（`Save()` 写的就是它）。
        /// 另一处用处：设置窗口拿它跟**开窗时的快照**比对，判「有没有还没保存的改动」。
        /// </summary>
        public static string ToJson()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                // 说明写在 `_` 开头的键里 —— 既是**合法 JSON**（任何 JSON 工具都读得动），
                // 又能让用户打开文件就看见每一项是什么意思（JSON 本体不支持注释）。
                sb.Append("{\r\n");
                sb.Append("  \"_note\": \"TabbedExplorer 设置。就在程序目录的 data\\\\ 下，拷走整个文件夹就带走了设置和标签记忆。\",\r\n");
                sb.Append("  \"_capture\": \"perdesktop = 每张虚拟桌面各一个窗口、各记一套标签；migrate = 全进程只一个窗口，Win+E 把它搬到当前桌面\",\r\n");
                sb.Append("  \"_theme\": \"system = 跟随系统应用模式；light / dark = 强制\",\r\n");
                sb.Append("  \"_tabwidth\": \"标签页宽度，逻辑像素，64 ~ 240；关掉 tabautowiden 时所有标签就都是这个宽\",\r\n");
                sb.Append("  \"_tabautowiden\": \"true = 名字太长时这个标签自己加宽（最多 400 逻辑像素）；false = 所有标签一样宽\",\r\n");
                sb.Append("  \"_tabautofit\": \"true = 一排标签挤不下时自动缩窄；false = 不缩，总宽停在右边那排按钮前，多出来的靠滚轮横向滑\",\r\n");
                sb.Append("  \"_captureall\": \"true = 从开始菜单/桌面双击打开的文件夹也收成标签（像浏览器）；false = 只接管 Win+E\",\r\n");
                sb.Append("  \"_captureshell\": \"true = 连桌面 shell 进程开的文件夹窗口也接管（先读出它的路径、像点 × 一样关掉它，再用我们自己的 explorer 开成标签）；false = 一个都不碰（从开始菜单/桌面双击打开的文件夹就不会进标签了）\",\r\n");
                sb.Append("  \"_parallel\": \"true = 一次同时起多个 explorer（开标签快得多；每个标签靠地址栏内容证明那个窗口是它的）；false = 一个一个来（慢但最简单）\",\r\n");
                sb.Append("  \"_lazytabs\": \"true = 还原标签时只把当时选中那个真起出来、其它点开才加载（省开程序那一下的等待）；false = 一次全起出来（默认）\",\r\n");
                sb.Append("  \"_autopreload\": \"true = 程序起来后就算还没打开窗口，也先在后台把本桌面记着的标签起出来（第一次 Win+E 就是热的）；false = 等按 Win+E 再还原\",\r\n");
                sb.Append("  \"_autosleep\": \"true = 切走的标签停留够久就把它那个 explorer 进程的驻留内存收一收、切回来重新读回；false = 一个都不收（常驻内存更高，切标签最快）\",\r\n");
                sb.Append("  \"_sleepdelay\": \"非激活标签停留多少秒之后才收它那个 explorer 进程的驻留内存，1 ~ 300；调大 = 来回切标签更顺，调小 = 更省内存\",\r\n");
                sb.Append("  \"_qlpreview\": \"true = 在标签里按空格调 QuickLook 预览选中的文件（需已安装并常驻 QuickLook）；false = 空格照旧落在文件列表上。Ctrl/Alt/Shift+空格 一律不管\",\r\n");
                sb.Append("  \"_newtabbeside\": \"true = 新建的标签开在**当前标签旁边**（插到它右边）；false = 一律开在最末尾（默认）。只管新开的，「恢复关闭的标签页」永远回它原来的位置\",\r\n");
                sb.Append("  \"_winepc\": \"true = Win+E 时顺手开一个「此电脑」标签（已经开着就切过去）；false = 只把窗口唤到最前面、一个标签都不动（默认）。想开新页直接 Ctrl+T。跟「第一次 Win+E / 窗口还空着」的兜底无关，那种情况照样给一个「此电脑」\",\r\n");
                sb.Append("  \"_notify\": \"true = 操作反馈弹一条右下角气泡（已复制 / 留一个 / 还在后台 这类）；false = 一个都不弹（功能一件不少）\",\r\n");
                sb.Append("  \"_notifyover\": \"逐条通知开关的覆盖表，格式 id=1 / id=0 用逗号隔开；**只写用户改过的那些**，没写到的 = 跟随默认（debug 全开，平时只开「出错与失败」和「启动与后台状态」里那几条）。条目 id 见设置窗口「通知管理」页或代码里的 NotifyItems\",\r\n");
                sb.Append("  \"_notifysec\": \"一条气泡停留多少秒后自己收（默认 4，1 ~ 30）。调大 = 看得更清楚，调小 = 少挡视线\",\r\n");
                sb.Append("  \"_winsize\": \"true = 退出时记住窗口位置和大小，下次起来照原样摆（按虚拟桌面分别记在 desktops.json 的 bounds 里）\",\r\n");
                sb.Append("  \"_vtabs\": \"true = 垂直侧边栏（标签竖排在左边窗格，Ctrl+Shift+,）；false = 标签横排在顶上（默认）\",\r\n");
                sb.Append("  \"_vtabscollapse\": \"true = 垂直窗格的「折叠窗格」：鼠标不在窗格上时只显示图标，移进去临时展开；false = 一直显示完整标题\",\r\n");
                sb.Append("  \"_debug\": \"true = 把每一步的详细过程写进 data\\\\log.txt（默认 false）。查问题时打开，平时关着不占地方。\",\r\n");
                sb.Append("  \"_hotkeys\": \"程序自己的快捷键，格式 Ctrl+Shift+T / Alt+F4 这样；留空或删掉这一行 = 用默认。Ctrl+1..9 跳标签是固定的、不在这里。\",\r\n");
                sb.Append("  \"_vpanealpha\": \"侧边栏摊开盖在内容上那一下的不透明度，%，0 ~ 100；100 = 完全不透明，0 = 完全透明（只在折叠窗格开着、鼠标移进去盖住内容时生效）\",\r\n");
                sb.Append("  \"_favbandheight\": \"垂直侧边栏里书签段的高度，逻辑像素；0 = 自动（按书签条数算，最多占窗格高的三分之一）。在标签区和书签段之间那根横条上拖一下就会写在这里\",\r\n");
                sb.Append("  \"_startopen\": \"双击图标 / 重启本程序时要不要直接把主窗口打开；默认 true。关了就只驻托盘（第一次 Win+E 才出窗口）。开机自启不受它管 —— 那条固定带 --tray，永远不会弹窗\",\r\n");
                sb.Append("  \"capture\": \"").Append(Text(Capture)).Append("\",\r\n");
                sb.Append("  \"keeptabs\": ").Append(KeepTabs ? "true" : "false").Append(",\r\n");
                sb.Append("  \"lazytabs\": ").Append(LazyTabs ? "true" : "false").Append(",\r\n");
                sb.Append("  \"parallel\": ").Append(ParallelLaunch ? "true" : "false").Append(",\r\n");
                sb.Append("  \"autopreload\": ").Append(AutoPreload ? "true" : "false").Append(",\r\n");
                sb.Append("  \"autosleep\": ").Append(AutoSleep ? "true" : "false").Append(",\r\n");
                sb.Append("  \"sleepdelay\": ").Append(SleepDelaySec).Append(",\r\n");
                sb.Append("  \"qlpreview\": ").Append(QLPreview ? "true" : "false").Append(",\r\n");
                sb.Append("  \"notify\": ").Append(Notify ? "true" : "false").Append(",\r\n");
                sb.Append("  \"notifyover\": \"").Append(NotifyOverText()).Append("\",\r\n");
                sb.Append("  \"notifysec\": ").Append(NotifySec).Append(",\r\n");
                sb.Append("  \"theme\": \"").Append(Text(Color)).Append("\",\r\n");
                sb.Append("  \"tabwidth\": ").Append(TabWidth).Append(",\r\n");
                sb.Append("  \"tabautowiden\": ").Append(TabAutoWiden ? "true" : "false").Append(",\r\n");
                sb.Append("  \"tabautofit\": ").Append(TabAutoFit ? "true" : "false").Append(",\r\n");
                sb.Append("  \"newtabbeside\": ").Append(NewTabBeside ? "true" : "false").Append(",\r\n");
                sb.Append("  \"winepc\": ").Append(WinENewPc ? "true" : "false").Append(",\r\n");
                sb.Append("  \"startopen\": ").Append(StartOpenWindow ? "true" : "false").Append(",\r\n");
                sb.Append("  \"favbar\": ").Append(FavBar ? "true" : "false").Append(",\r\n");
                sb.Append("  \"captureall\": ").Append(CaptureAll ? "true" : "false").Append(",\r\n");
                sb.Append("  \"captureshell\": ").Append(CaptureShell ? "true" : "false").Append(",\r\n");
                sb.Append("  \"winsize\": ").Append(WindowSize ? "true" : "false").Append(",\r\n");
                sb.Append("  \"vtabs\": ").Append(VTabs ? "true" : "false").Append(",\r\n");
                sb.Append("  \"vtabscollapse\": ").Append(VTabsCollapse ? "true" : "false").Append(",\r\n");
                sb.Append("  \"vpanealpha\": ").Append(VPaneAlpha).Append(",\r\n");
                sb.Append("  \"favbandheight\": ").Append(FavBandHeight).Append(",\r\n");
                sb.Append("  \"debug\": ").Append(Debug ? "true" : "false").Append(",\r\n");
                // 快捷键：只写「跟默认不一样」的那些（默认值不落文件，以后换默认值能跟着走）
                StringBuilder hb = new StringBuilder();
                for (int i = 0; i < HotkeyKeys.Length; i++)
                {
                    string v;
                    if (!hotkeys.TryGetValue(HotkeyKeys[i], out v)) continue;
                    if (string.IsNullOrEmpty(v)) continue;
                    if (string.Equals(v, HotkeyDefaults[i], StringComparison.OrdinalIgnoreCase)) continue;
                    hb.Append("  \"hotkey_").Append(HotkeyKeys[i]).Append("\": \"")
                      .Append(v.Replace("\\", "\\\\").Replace("\"", "\\\""))
                      .Append("\",\r\n");
                }
                sb.Append(hb.ToString());
                // 上面每一项末尾都带逗号，这里补最后一行收尾（JSON 末尾多余逗号不合法）
                string body = sb.ToString();
                int last = body.LastIndexOf(",\r\n");
                sb = new StringBuilder(body.Substring(0, last) + "\r\n");
                sb.Append("}\r\n");

                return sb.ToString();
            }
            catch (Exception ex) { Diag.Log("设置: 序列化失败 " + ex.Message); return ""; }
        }

        public static void Save()
        {
            if (saveHold > 0) return;        // ★ 攒着，等用户点「保存」
            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                // 跟记忆一样：先写临时文件再换过去，半途被硬杀不会留下半截文件。
                string tmp = FileName + ".tmp";
                File.WriteAllText(tmp, ToJson(), new UTF8Encoding(false));
                if (File.Exists(FileName)) File.Delete(FileName);
                File.Move(tmp, FileName);
            }
            catch (Exception ex) { Diag.Log("设置: 写失败 " + ex.Message); }
        }

        // JSON 取值统一走 `Json`（src/Json.cs）—— 四个数据文件（settings / desktops / history / favorites）
        // 共用同一套手写解析，不再各写一份（省得格式一处改一处不改）。

        public static string Describe()
        {
            return string.Format("capture={0} keeptabs={1} lazytabs={2} parallel={3} theme={4} tabwidth={5} autowiden={6} autofit={7} favbar={8} captureall={9} captureshell={10} winsize={11} vtabs={12} vtabsfold={13} vpanealpha={14} favbandh={15} debug={16} hotkeys={17} autopreload={18} autosleep={19} sleepdelay={20} notify={21} notifyover={22} notifysec={23} qlpreview={24} newtabbeside={25} winepc={26} startopen={27}",
                Text(Capture), KeepTabs ? 1 : 0, LazyTabs ? 1 : 0, ParallelLaunch ? 1 : 0, Text(Color), TabWidth,
                TabAutoWiden ? 1 : 0, TabAutoFit ? 1 : 0, FavBar ? 1 : 0, CaptureAll ? 1 : 0,
                CaptureShell ? 1 : 0, WindowSize ? 1 : 0, VTabs ? 1 : 0, VTabsCollapse ? 1 : 0,
                VPaneAlpha, FavBandHeight, Debug ? 1 : 0, hotkeys.Count, AutoPreload ? 1 : 0, AutoSleep ? 1 : 0, SleepDelaySec, Notify ? 1 : 0,
                NotifyOver.Count, NotifySec, QLPreview ? 1 : 0, NewTabBeside ? 1 : 0, WinENewPc ? 1 : 0,
                StartOpenWindow ? 1 : 0);
        }

        // ==================================================================
        // 解析 / 序列化
        // ==================================================================

        public static int ClampWidth(int w)
        {
            if (w < TabWidthMin) return TabWidthMin;
            if (w > TabWidthMax) return TabWidthMax;
            return w;
        }

        public static int ClampAlpha(int a)
        {
            if (a < VPaneAlphaMin) return VPaneAlphaMin;
            if (a > VPaneAlphaMax) return VPaneAlphaMax;
            return a;
        }

        /// <summary>书签段高度（逻辑像素）。0 保留 =「自动」，其余夹进合法范围。</summary>
        public static int ClampFavBand(int h)
        {
            if (h <= FavBandHeightMin) return 0;
            if (h > FavBandHeightMax) return FavBandHeightMax;
            return h;
        }

        public static int ClampSleepSec(int s)
        {
            if (s < SleepDelaySecMin) return SleepDelaySecMin;
            if (s > SleepDelaySecMax) return SleepDelaySecMax;
            return s;
        }

        public static int ClampNotifySec(int s)
        {
            if (s < NotifySecMin) return NotifySecMin;
            if (s > NotifySecMax) return NotifySecMax;
            return s;
        }

        private static bool ParseBool(string v, bool dflt)
        {
            if (string.IsNullOrEmpty(v)) return dflt;
            v = v.Trim().ToLowerInvariant();
            if (v == "1" || v == "true" || v == "yes" || v == "on" || v == "开") return true;
            if (v == "0" || v == "false" || v == "no" || v == "off" || v == "关") return false;
            return dflt;
        }

        private static int ParseInt(string v, int dflt)
        {
            int n;
            if (int.TryParse(v.Trim(), out n)) return n;
            return dflt;
        }

        public static CaptureMode ParseCapture(string v)
        {
            if (string.Equals(v, "migrate", StringComparison.OrdinalIgnoreCase)) return CaptureMode.Migrate;
            if (string.Equals(v, "single", StringComparison.OrdinalIgnoreCase)) return CaptureMode.Migrate;
            return CaptureMode.PerDesktop;
        }

        public static ColorMode ParseColor(string v)
        {
            if (string.Equals(v, "light", StringComparison.OrdinalIgnoreCase)) return ColorMode.Light;
            if (string.Equals(v, "dark", StringComparison.OrdinalIgnoreCase)) return ColorMode.Dark;
            if (string.Equals(v, "浅色", StringComparison.OrdinalIgnoreCase)) return ColorMode.Light;
            if (string.Equals(v, "深色", StringComparison.OrdinalIgnoreCase)) return ColorMode.Dark;
            return ColorMode.System;
        }

        public static string Text(CaptureMode m)
        {
            return m == CaptureMode.Migrate ? "migrate" : "perdesktop";
        }

        public static string Text(ColorMode m)
        {
            if (m == ColorMode.Dark) return "dark";
            if (m == ColorMode.Light) return "light";
            return "system";
        }

        public static string Label(CaptureMode m)
        {
            return m == CaptureMode.Migrate ? "捕获并迁移到当前桌面" : "按虚拟桌面分别捕获";
        }

        public static string Label(ColorMode m)
        {
            if (m == ColorMode.Dark) return "深色";
            if (m == ColorMode.Light) return "浅色";
            return "跟随系统";
        }

        /// <summary>
        /// 通知四类各自的默认值（json 里没这个键时用它）。
        /// 用户：「Debug 模式默认开启全部，非 Debug 模式只默认开启重要的部分」。
        /// </summary>
        private static bool DefNotify(bool debug, bool important) { return debug || important; }

        /// <summary>
        /// 这**一条**通知本身开着没有（**不看总闸** —— 设置页画那个勾、算大类的三态要用它）：
        /// 用户明确设过就按他设的，没设过按默认（`DefNotify` + 条目自己的 `Important`）。
        /// </summary>
        public static bool NotifyItemBase(NotifyItem it)
        {
            if (it == null) return false;
            bool ov;
            if (NotifyOver.TryGetValue(it.Id, out ov)) return ov;
            return DefNotify(Debug, it.Important);
        }

        /// <summary>
        /// 这一条现在该不该弹（**总闸 × 这一条自己的开关**）。`Toast.Show` 就调它 ——
        /// 每次现读，所以设置里一改立刻生效、不用重启。
        /// </summary>
        public static bool NotifyItemOn(NotifyItem it) { return Notify && NotifyItemBase(it); }

        /// <summary>
        /// 这一大类现在是全开 / 部分 / 全关（2 / 1 / 0）—— 设置页里那个节标题的勾就画三态。
        /// </summary>
        public static int NotifyKindState(ToastKind k)
        {
            List<NotifyItem> list = NotifyItems.Of(k);
            int on = 0;
            for (int i = 0; i < list.Count; i++) if (NotifyItemBase(list[i])) on++;
            if (on == 0) return 0;
            return on == list.Count ? 2 : 1;
        }

        /// <summary>
        /// 这个大类「已开 N/M」—— 设置页里节标题右边那行小字。
        /// 明细默认是**收起**的，不摊开看不见里面谁开着，所以把数摆出来。
        /// </summary>
        public static string NotifyKindCountText(ToastKind k)
        {
            List<NotifyItem> list = NotifyItems.Of(k);
            int on = 0;
            for (int i = 0; i < list.Count; i++) if (NotifyItemBase(list[i])) on++;
            return "已开 " + on + "/" + list.Count;
        }

        /// <summary>把一大类**整批**设成开 / 关（设置页点节标题那个三态勾）。不落盘。</summary>
        private static void NotifyKindApply(ToastKind k, bool on)
        {
            List<NotifyItem> list = NotifyItems.Of(k);
            for (int i = 0; i < list.Count; i++) NotifyItemSet(list[i], on);
        }

        /// <summary>
        /// 设**这一条**（不落盘）。跟默认一样就把键删掉 —— 让「默认」继续跟着 Debug 走
        /// （否则 Debug 一开，用户之前明确关掉的那条会留在文件里、不跟着变）。
        /// </summary>
        private static void NotifyItemSet(NotifyItem it, bool on)
        {
            if (it == null) return;
            if (on == DefNotify(Debug, it.Important)) NotifyOver.Remove(it.Id);
            else NotifyOver[it.Id] = on;
        }

        /// <summary>把覆盖表拼成 `id=1,id2=0` 一行（json 里就一个字符串键，好读好手改）。按 id 排序输出，文件稳定。</summary>
        private static string NotifyOverText()
        {
            if (NotifyOver.Count == 0) return "";
            List<string> ids = new List<string>(NotifyOver.Keys);
            ids.Sort(StringComparer.Ordinal);
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < ids.Count; i++)
            {
                if (sb.Length > 0) sb.Append(',');
                sb.Append(ids[i]).Append(NotifyOver[ids[i]] ? "=1" : "=0");
            }
            return sb.ToString();
        }

        /// <summary>读 `notifyover`。认不出来的片段直接跳过 —— 手改坏了不该让程序起不来。</summary>
        private static void LoadNotifyOver(string text)
        {
            NotifyOver.Clear();
            if (string.IsNullOrEmpty(text)) return;
            string[] parts = text.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                string s = parts[i].Trim();
                if (s.Length == 0) continue;
                int eq = s.IndexOf('=');
                string id = (eq > 0 ? s.Substring(0, eq) : s).Trim();
                if (id.Length == 0) continue;
                string v = eq > 0 ? s.Substring(eq + 1).Trim() : "1";
                NotifyOver[id] = (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase)
                                     || v.Equals("on", StringComparison.OrdinalIgnoreCase));
            }
        }

        /// <summary>兼容 2026-09-28 那版只写了四个大类开关（`notifyerr` 等）的 settings.json。</summary>
        private static void LegacyNotifyKind(string json, string key, ToastKind k)
        {
            string v = Json.Get(json, key);
            if (string.IsNullOrEmpty(v)) return;
            NotifyKindApply(k, ParseBool(v, true));
        }

        // ==================================================================
        // 改一项就存一次（都从菜单里点，频率极低，不用攒）
        // ==================================================================

        public static void SetCapture(CaptureMode m) { Capture = m; Save(); }
        public static void SetColor(ColorMode m) { Color = m; Save(); }
        public static void SetKeepTabs(bool on) { KeepTabs = on; Save(); }
        public static void SetLazyTabs(bool on) { LazyTabs = on; Save(); }
        public static void SetParallelLaunch(bool on) { ParallelLaunch = on; Save(); }
        public static void SetAutoPreload(bool on) { AutoPreload = on; Save(); }
        public static void SetAutoSleep(bool on) { AutoSleep = on; Save(); }
        public static void SetSleepDelay(int sec) { SleepDelaySec = ClampSleepSec(sec); Save(); }
        /// <summary>空格键预览（QuickLook）总开关。改完立刻生效 —— 钩子每次现读这个闸。</summary>
        public static void SetQLPreview(bool on) { QLPreview = on; Save(); }
        public static void SetNotify(bool on) { Notify = on; Save(); }
        /// <summary>「一条气泡停留多少秒」（设置窗口「通知管理」页那个数字框）。</summary>
        public static void SetNotifySec(int sec) { NotifySec = ClampNotifySec(sec); Save(); }
        /// <summary>设**一条**通知的开关（设置页里最小那一行）。</summary>
        public static void SetNotifyItem(NotifyItem it, bool on) { NotifyItemSet(it, on); Save(); }
        /// <summary>设**一大类**（设置页点节标题那个三态勾：整批开 / 整批关）。</summary>
        public static void SetNotifyKind(ToastKind k, bool on) { NotifyKindApply(k, on); Save(); }
        public static void SetTabWidth(int w) { TabWidth = ClampWidth(w); Save(); }
        public static void SetTabAutoFit(bool on) { TabAutoFit = on; Save(); }
        /// <summary>新建标签开在当前标签旁边（`true`）/ 开在最末尾（`false`，默认）。新建时现读，无需刷新。</summary>
        public static void SetNewTabBeside(bool on) { NewTabBeside = on; Save(); }
        /// <summary>Win+E 时顺手开一个「此电脑」标签（`true`）/ 只把窗口唤到最前面（`false`，默认）。唤窗时现读。</summary>
        public static void SetWinENewPc(bool on) { WinENewPc = on; Save(); }
        /// <summary>启动时打开主窗口（`true`，默认）/ 只驻托盘（`false`）。只影响**下次启动 / 重启**，当场没有副作用。</summary>
        public static void SetStartOpenWindow(bool on) { StartOpenWindow = on; Save(); }
        public static void SetTabAutoWiden(bool on) { TabAutoWiden = on; Save(); }
        public static void SetFavBar(bool on) { FavBar = on; Save(); }
        public static void SetCaptureAll(bool on) { CaptureAll = on; Save(); }
        public static void SetCaptureShell(bool on) { CaptureShell = on; Save(); }
        public static void SetWindowSize(bool on) { WindowSize = on; Save(); }
        public static void SetVTabs(bool on) { VTabs = on; Save(); }
        public static void SetVTabsCollapse(bool on) { VTabsCollapse = on; Save(); }
        public static void SetVPaneAlpha(int a) { VPaneAlpha = ClampAlpha(a); Save(); }
        /// <summary>垂直侧边栏里书签段的高度（逻辑像素，0 = 自动）。</summary>
        public static void SetFavBandHeight(int h) { FavBandHeight = ClampFavBand(h); Save(); }

        /// <summary>Debug 模式开关：改完立刻生效（`Diag` 每次写之前都看那个闸）。</summary>
        public static void SetDebug(bool on)
        {
            Debug = on;
            Diag.Enabled = on;
            Save();
        }
    }
}
