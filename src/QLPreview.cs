using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;

namespace TabbedExplorer
{
    /// <summary>
    /// 空格键预览（QuickLook）。用户：「适配 QuicLook 的空格键预览 —— 我在原生资源管理器
    /// 明明是没问题的。」
    ///
    /// 为什么原生行、我们不行：QuickLook 的判据**只有一条** —— 读**前台窗口的类名**。
    /// 它的判定表（从本机 `QuickLook.Native64.dll` 里读出来的 UTF-16 字面量）就是
    /// `CabinetWClass` / `ExploreWClass` / `ShellTabWindowClass` / `dopus.lister` /
    /// `EVERYTHING` / `DUIViewWndClassName` / `WorkerW` / `Progman` / `#32770`。
    /// 原生资源管理器是 `CabinetWClass`，它认；我们的宿主是自绘窗体（`WindowsForms10.Window.*`），
    /// 它一律判 `Invalid` —— **整段逻辑（连取选中项）都不会走**。所以这跟「取不到选中项」无关，
    /// 是第一关就过不了。
    ///
    /// ⚠ 「内嵌的那个 `CabinetWClass` 明明是原生的，为什么也不行」—— 因为 `SetParent` 之后它成了
    ///   **子窗口**，而 Windows 里**子窗口永远不能成为前台窗口**：`GetForegroundWindow()` 只返回
    ///   顶层窗口，也就是我们那个自绘宿主。探针实测：10 组 `CabinetWClass` /
    ///   `ShellTabWindowClass` / `DUIViewWndClassName` 全是 `WS_CHILD=1`、顶层祖先都是宿主窗体。
    ///
    /// 我们怎么接：`WinEHook` 那个全局键盘钩子（本来管 Win+E 和标签快捷键）加一条空格分支。
    /// 钩子里**不做 COM / IO**（见那儿的硬规矩），判定只做纯 Win32（见 `WantsSpace`），
    /// 真干活投回 UI 线程（`Do`）。`--classic` 那条老路（`AppContext`）不接这个功能。
    ///
    /// ⚠ **绝不能抢改键工具的功能键**：MyKeymap / AutoHotkey 这类工具拿 CapsLock 当功能键，
    /// 靠它自己的键盘钩子收「CapsLock+X」。而低级键盘钩子是**后装的先拿到事件** —— 我们比它装得晚，
    /// 所以我们一旦在按住 CapsLock 时吞掉空格，**后面那个钩子就再也收不到这一下**
    /// （返回 1 的事件不会再往后传，也不会进目标窗口），用户的 CapsLock+空格 直接哑掉。
    /// 所以 `WantsSpace` 把 CapsLock / Win 也当成「按着就别管」。
    ///
    /// ⚠ **也绝不抢文本框里的空格**：中文输入法上屏就是按空格。在标签里**改文件名**、点**地址栏**、
    /// 用**搜索框**时，焦点在内嵌窗口的一个 `Edit` 上，而前台窗口仍然是我们的宿主窗体 ——
    /// 只看宿主白名单就会把他的中文输入直接吞成预览（用户实测报过）。判据见 `TextInputFocused`：
    /// 用 `GetGUIThreadInfo` 看前台线程里**真正有焦点的控件**，是文本框就整条让路。
    ///
    /// 方向键 / Esc / Enter（2026-09-30 补，用户报「在桌面按空格之后上下左右能换内容，我们不行」）：
    /// 在原生资源管理器里这几键**也是 QuickLook 自己管的** —— 它有个全局键盘钩子
    ///（`KeystrokeDispatcher`），KeyUp 时发 `Switch` / `Close`。但它的第一关是
    /// `Shell32::GetFocusedWindowType()`，**只看前台窗口的类名**（`CabinetWClass` / `ExploreWClass` /
    /// `Progman` / `WorkerW` …），我们自绘宿主过不了 ⇒ 它那一套到这里全哑。
    /// 而它的管道消息里 **`Switch`(`SwitchPreview`) / `Close`(`ClosePreview`) 带路径时根本不看类名**，
    /// 只要求预览窗已经开着（`!_viewerWindow.IsVisible` 就直接 return）⇒ 由**我们替它转发**：
    ///   · 方向键（KeyUp，**不吞键**）：读「当前标签所在文件夹**此刻**的选中项」→ `Switch|路径|`。
    ///     方向键本身仍归内嵌列表管，它自己会移动选中项；我们只是回头把它移动之后的结果接上去 ——
    ///     跟桌面上的观感一致（那边也是「选中项一动，预览跟着换」，见 `getSelectedFromExplorer`）。
    ///   · Esc / Enter → `Close||`（⚠ **不能**发 `RunAndClose`：它内部也先问一遍类名，对我们一律空转）。
    /// ⚠ 「把宿主窗体伪装成 `CabinetWClass`、让 QuickLook 自己认」这条路**绝不能走**：本工程十几处
    ///   （`DesktopHub` / `ExplorerHost` / `EmbedApi`）都是拿类名认 shell 顶层窗的，宿主一旦顶着这个
    ///   类名，我们自己就会把宿主动成「shell 开着的窗口」去收编。
    /// </summary>
    internal static class QLPreview
    {
        private const int VK_RETURN = 0x0D;
        private const int VK_SHIFT = 0x10;
        private const int VK_CONTROL = 0x11;
        private const int VK_MENU = 0x12;      // Alt
        private const int VK_CAPITAL = 0x14;   // CapsLock
        private const int VK_ESCAPE = 0x1B;
        private const int VK_SPACE = 0x20;
        private const int VK_LEFT = 0x25;
        private const int VK_UP = 0x26;
        private const int VK_RIGHT = 0x27;
        private const int VK_DOWN = 0x28;
        private const int VK_LWIN = 0x5B;
        private const int VK_RWIN = 0x5C;

        /// <summary>
        /// 「这些键按着的时候，空格不是我们的」。**CapsLock 和 Win 必须在里面**：
        /// Ctrl+空格 是输入法切换、Win+空格 是系统输入法/表情面板、CapsLock+空格 是 MyKeymap
        /// 这类改键工具的组合键（见类注释）。用 `GetAsyncKeyState`（**不是** `GetKeyState`）：
        /// 低级钩子里只有它已经反映「刚刚这一下」。
        /// </summary>
        private static readonly int[] HeldModifiers = new int[]
        {
            VK_CONTROL, VK_MENU, VK_SHIFT, VK_CAPITAL, VK_LWIN, VK_RWIN
        };
        private const uint GA_ROOT = 2;

        /// <summary>QuickLook 的跨进程消息名（照抄它自己的 `PipeMessages` 常量）。</summary>
        private const string MsgToggle = "QuickLook.App.PipeMessages.Toggle";

        /// <summary>
        /// 「换到某个文件」（`ViewWindowManager.SwitchPreview`）。⚠ 与 `Toggle` 的关键差别：**带路径时
        /// 它完全不看前台窗口类名**，只要求预览窗已经活着（没开着就直接 return）—— 就靠这一条，
        /// 我们才能在自绘宿主里接上方向键。
        /// </summary>
        private const string MsgSwitch = "QuickLook.App.PipeMessages.Switch";

        /// <summary>「关掉预览」（`ViewWindowManager.ClosePreview`）。**无条件**关，不看类名也不看前台是谁。</summary>
        private const string MsgClose = "QuickLook.App.PipeMessages.Close";

        /// <summary>
        /// QuickLook 的 WPF 窗口类名里都带这一段（本机实测 `HwndWrapper[QuickLook.exe;;&lt;guid&gt;]`）。
        /// 拿它认「常驻实例的窗口」，见 <see cref="PreviewVisible"/>。
        /// </summary>
        private const string WindowClassMark = "QuickLook.exe";

        /// <summary>
        /// 两次「换预览」之间至少隔这么久（毫秒）。`SelectedPathsIn` 一次全量枚举 16~41ms 且只能在
        /// UI 线程跑，而方向键长按会被连发 —— 超了就丢掉这一下（QuickLook 侧本来也会把上一次未执行的
        /// 派发任务作废，见 `PipeServerManager._lastOperation`）。
        /// </summary>
        private const int NavMinGapMs = 90;

        /// <summary>
        /// 连管道的超时（毫秒）。**必须给** —— `NamedPipeClientStream.Connect()` 不传超时是
        /// **无限等**，QuickLook 没在跑时会把 UI 线程挂死。
        /// </summary>
        private const int PipeConnectMs = 1200;

        /// <summary>命名管道名（只算一次；SID 不会变）。</summary>
        private static string pipeName;

        /// <summary>上次找到的 exe（**只给回退路径用**，找到才缓存）。</summary>
        private static string cachedExe;

        /// <summary>
        /// 宿主窗体句柄的一份快照，**只在 UI 线程重建**（`DesktopHub.SyncHosts`）。钩子线程只读它
        /// 一次 —— 做成整份替换而不是共享一个可变集合：钩子读的时候不会撞上别人正在改，
        /// 也不用在钩子里加锁（低级钩子有超时限制，里面不能干慢活）。
        ///
        /// 为什么非要这个白名单：光判「前台窗口属于本进程」不够 —— 设置窗口、通知气泡也是我们的窗口，
        /// 而**在那些地方空格是正常操作**（勾复选框），绝不能被吞。
        /// </summary>
        private static volatile IntPtr[] hosts = new IntPtr[0];

        /// <summary>这一次按下的空格已经处理过了（防长按那串重复的 `WM_KEYDOWN` 连着弹好几次预览）。</summary>
        private static bool spaceDown;

        /// <summary>上一次「换预览」尝试的时刻（见 `NavMinGapMs`）。只在 UI 线程读写。</summary>
        private static int lastNavTick;

        /// <summary>上一次发出去的预览路径（`Toggle` / `Switch` 都记）。选择没动就别再发一遍。只在 UI 线程读写。</summary>
        private static string lastNavPath;

        /// <summary>把宿主窗体句柄同步进来。只在 UI 线程调（`DesktopHub.SyncHosts`）。</summary>
        public static void SetHosts(List<IntPtr> list)
        {
            hosts = (list == null) ? new IntPtr[0] : list.ToArray();
        }

        /// <summary>空格松开时清标志（钩子线程调）。长按就只会弹一次。</summary>
        public static void NoteKeyUp(int vk)
        {
            if (vk == VK_SPACE) spaceDown = false;
        }

        /// <summary>
        /// 这次空格该不该归我们管（**钩子线程**：只读字段 + 纯 Win32，不做 COM / IO）。
        /// 四条：开关开着、**没按着别的键**（见 `HeldModifiers` —— Ctrl+空格、Win+空格、
        /// CapsLock+空格 一个都不能抢）、这一次按下还没处理过、前台是我们某个宿主窗体
        /// **且焦点不在文本框里**（否则是在改名字/打地址，空格归输入法）。
        /// </summary>
        public static bool WantsSpace()
        {
            if (spaceDown) return false;
            if (!OurGate(true)) return false;      // ★ 空格按「深查」算（见 OurGate 的形参说明）
            spaceDown = true;
            return true;
        }

        /// <summary>
        /// 方向键 / Esc / Enter 该不该归我们转发（**钩子线程**：只读字段 + 纯 Win32）。
        /// 跟空格同一套闸（见 <see cref="OurGate"/>），但**不碰 `spaceDown`** —— 那是空格专用的
        /// 「这一次按下已经处理过」标记。
        /// </summary>
        public static bool WantsNav()
        {
            return OurGate(false);                 // ★ 导航键走「浅查」：这一类键太频繁，见形参说明
        }

        /// <summary>方向键（发 `Switch`：换到某个文件）。</summary>
        public static bool IsArrowKey(int vk)
        {
            return vk == VK_LEFT || vk == VK_RIGHT || vk == VK_UP || vk == VK_DOWN;
        }

        /// <summary>Esc / Enter（发 `Close`：关掉预览）。原生里这两键也是 QuickLook 管的。</summary>
        public static bool IsCloseKey(int vk)
        {
            return vk == VK_ESCAPE || vk == VK_RETURN;
        }

        /// <summary>
        /// 空格与导航键共用的四道闸：开关开着、**没按着别的键**（见 `HeldModifiers` —— Ctrl+空格、
        /// Win+空格、CapsLock+空格 一个都不能抢）、前台是我们某个宿主窗体、**且焦点不在文本框里**
        /// （否则是在改名字/打地址，键归文本和输入法）。
        ///
        /// ⚠ `deep` = 要不要**连内嵌 explorer 那边**一起查（见 `TextInputFocused`）：
        ///   · 空格（<see cref="WantsSpace"/>）要 —— 吞错一下就是用户中文打不进去，值这个开销；
        ///   · 方向键这种 KeyUp 每下都要问的走浅查就够（内嵌列表那边的箭头只影响预览切不切，
        ///     而且 <see cref="DoSwitch"/> 自己还有「预览没开就一眼不看」那道闸）。
        /// </summary>
        private static bool OurGate(bool deep)
        {
            if (!Settings.QLPreview) return false;
            for (int i = 0; i < HeldModifiers.Length; i++)
            {
                if ((NativeMethods.GetAsyncKeyState(HeldModifiers[i]) & 0x8000) != 0) return false;
            }

            IntPtr fg = NativeMethods.GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            // `SetParent` 之后内嵌的 shell 窗口是**子窗口**，前台窗口永远是顶层的宿主窗体；
            // 但焦点可能落在子窗口上，所以两头都认（`GA_ROOT` 一路走到最外层父窗口）。
            IntPtr root = NativeMethods.GetAncestor(fg, GA_ROOT);
            IntPtr[] hs = hosts;
            bool ours = false;
            for (int i = 0; i < hs.Length && !ours; i++)
            {
                if (hs[i] == fg || hs[i] == root) ours = true;
            }
            if (!ours) return false;

            // 焦点在文本框里（重命名框 / 地址栏 / 搜索框）= 在打字 —— 键归文本和输入法，绝不碰。
            return !TextInputFocused(fg, deep);
        }

        /// <summary>
        /// 焦点是不是落在「会吃掉空格的文本输入」上：内嵌资源管理器的**重命名框**、地址栏、搜索框，
        /// 以及任何挂在里面的输入框。
        ///
        /// ⚠ **必须判这个**：中文输入法**上屏就是按空格** —— 我们一吞，用户连字都打不进去
        /// （用户实测报过：改文件名时按空格，直接给他弹了预览；2026-10-02 又报了一次：
        ///  在资源管理器自己的**搜索框**里打中文，一按空格跳出了 QuickLook）。
        ///
        /// 怎么判：跨进程 `GetFocus()` 拿不到（只对本线程有效），只能拿线程去问
        /// `GetGUIThreadInfo`。⚠ 但**「前台窗口那条线程」不等于「拿着焦点的线程」** ——
        /// shell 那块被 `SetParent` 成子窗口之后，前台窗口永远是我们的宿主窗体，它的线程是**我们**的，
        /// 问出来的是我们自己的焦点。所以分两步（<paramref name="deep"/> 就是这件事的开关）：
        ///   ① 前台窗口那条线程 —— 我们自己的框（面包屑编辑框 / 我们自己的搜索框）；
        ///   ② 宿主窗口树里**别的进程**的线程（每扇 cab 一条）—— 内嵌 explorer 那几个框。
        /// 判据见 <see cref="ThreadHasTextFocus"/>；**判不出来一律放行**（宁可偶尔多预览一次，
        /// 也不能因为查不到就整个功能失灵）。
        /// </summary>
        private static bool TextInputFocused(IntPtr fg, bool deep)
        {
            // ① 前台窗口**那个线程**的焦点 —— 我们自己进程里的框走这条（面包屑编辑框 / 我们自己的搜索框）。
            if (ThreadHasTextFocus(NativeMethods.GetWindowThreadProcessId(fg, IntPtr.Zero))) return true;

            // ② ★ 内嵌 explorer 那几个框：焦点在**别人的线程**上。
            //    `SetParent` 之后 shell 那块是子窗口，前台窗口**永远是我们的宿主窗体**，所以拿它的
            //    线程去问，问到的是我们自己的焦点 —— 在资源管理器的搜索框里打字时，这一问必然落空
            //    （用户报：「搜索框里输入中文，一按空格就调了 QuickLook」）。
            //    而窗口树里那些 explorer 的子窗口各自属于**它们自己那条线程**，得挨个问。
            if (!deep) return false;
            return HostTreeHasTextFocus(fg);
        }

        /// <summary>
        /// 宿主窗口树里，有没有哪个**别的进程**的线程正把焦点放在文本输入上（见 `TextInputFocused` ②）。
        ///
        /// 怎么找：把宿主窗体底下的窗口全捞出来（`WinFind.All` 是递归的），按线程去重，只问**不是我们
        /// 这条线程**的那些 —— 一扇 cab 一条线程，最多问「标签数 + 几个」次 `GetGUIThreadInfo`。
        /// 一次全量枚举实测不到 2ms，而空格是人的动作，这个量级无所谓。
        /// </summary>
        private static bool HostTreeHasTextFocus(IntPtr fg)
        {
            try
            {
                uint ourTid = NativeMethods.GetWindowThreadProcessId(fg, IntPtr.Zero);
                List<IntPtr> all = WinFind.All(fg);
                List<uint> seen = new List<uint>(8);
                for (int i = 0; i < all.Count; i++)
                {
                    uint tid = NativeMethods.GetWindowThreadProcessId(all[i], IntPtr.Zero);
                    if (tid == 0 || tid == ourTid) continue;      // 我们这条线程上面第 ① 步已经问过
                    bool dup = false;
                    for (int j = 0; j < seen.Count; j++) if (seen[j] == tid) { dup = true; break; }
                    if (dup) continue;
                    seen.Add(tid);
                    if (ThreadHasTextFocus(tid)) return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// 那条线程现在是不是把焦点放在「会吃掉空格的文本输入」上。两条判据：
        ///   ① 焦点窗口类名里含 `edit`（`Edit` / `EditBoxWrapper` / `SearchEditBoxWrapperClass`……
        ///      资源管理器这几个框都是这么套的）；
        ///   ② 焦点窗口**身上有插入符**（`GUITHREADINFO.hwndCaret`）—— 自绘输入框类名认不出来时靠它兜底。
        /// ⚠ 问不出来 / 没有焦点一律 false（= 不拦着预览）：宁可偶尔多预览一次，也不能因为查不到就整个功能失灵。
        /// </summary>
        private static bool ThreadHasTextFocus(uint tid)
        {
            if (tid == 0) return false;
            try
            {
                bool caret;
                IntPtr h = EmbedApi.FocusWindowOf(tid, out caret);
                if (h == IntPtr.Zero) return false;

                StringBuilder sb = new StringBuilder(64);
                if (NativeMethods.GetClassName(h, sb, sb.Capacity) > 0)
                {
                    string cls = sb.ToString();
                    if (cls.IndexOf("edit", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                    // 万一焦点跑到了输入法自己那扇窗上（组合期间可能发生）
                    if (cls == "IME" || cls == "MSCTFIME UI" || cls == "Default IME") return true;
                }
                return caret;
            }
            catch { return false; }
        }

        /// <summary>
        /// 干活的地方（**UI 线程**，由 `DesktopHub.Post` 投过来）：把 `folder` 里现在选中的
        /// 第一个喂给 QuickLook。
        /// ⚠ 没选中东西 / 停在虚拟位置 —— 一律什么都不做。
        /// </summary>
        public static void Do(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return;

            List<string> sel = ShellBrowserReg.SelectedPathsIn(folder);
            if (sel.Count == 0)
            {
                // 记账：分得清「没选中项 / 焦点只停在目录上」（原生 QuickLook 也不预览）
                // 和「选中了却没出来」。2026-10-02 川报「空格没反应」时要靠这行定位。
                Diag.Step("QLPreview: 没选中项，不预览（" + folder + "）");
                return;
            }

            lastNavPath = sel[0];
            Preview(sel[0]);
            Diag.Step("QLPreview: 预览 " + sel[0]);   // ⚠ 记账放在预览**之后**：这条落盘是
                                                     // 每行一次 `AppendAllText`（开→写→关），
                                                     // Debug 开着时别让它挡在预览前面。
        }

        /// <summary>
        /// 方向键（**UI 线程**，由 `DesktopHub` 从钩子投过来）：把 `folder` 里**此刻**选中的那一项
        /// 用 `Switch` 发给常驻实例 —— 等于替 QuickLook 做了它自己做不了的那一步（见类注释）。
        ///
        /// 为什么读「此刻的选中项」而不是自己算「下一个」：QuickLook 在原生资源管理器里也**不是**自己算的
        /// —— 它靠它自己的钩子收到方向键后重新问前台窗口选了什么（`Shell32::GetCurrentSelection`）。
        /// 而方向键本身是**内嵌列表**去移动选中项的（我们不吞键），所以松开时读一次拿到的就是它移动后的
        /// 结果，与桌面上按方向键的观感一致。
        ///
        /// ⚠ 必须在**松开**时读：按下那一刻列表还没处理这个键（它得先收到 `WM_KEYDOWN`），那时候问到的
        ///   还是旧选中项。QuickLook 自己也是绑的 KeyUp（`KeystrokeDispatcher`）。
        ///
        /// 两道省钱闸（`SelectedPathsIn` 一次枚举 16~41ms，长按方向键会被连发）：
        ///   · <see cref="PreviewVisible"/> —— 预览没开就一眼都不看（最常见的情形：平时拿方向键翻目录）；
        ///   · <see cref="NavMinGapMs"/> —— 最短间隔，超了就丢掉这一下。
        /// </summary>
        public static void DoSwitch(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return;
            if (!PreviewVisible()) return;

            int now = Environment.TickCount;
            if (unchecked(now - lastNavTick) < NavMinGapMs) return;
            lastNavTick = now;

            List<string> sel = ShellBrowserReg.SelectedPathsIn(folder);
            if (sel.Count == 0) return;
            if (string.Equals(sel[0], lastNavPath, StringComparison.OrdinalIgnoreCase)) return;  // 选择没动，白跑一趟
            lastNavPath = sel[0];

            if (Send(MsgSwitch, sel[0])) Diag.Step("QLPreview: 切换 " + sel[0]);
        }

        /// <summary>
        /// Esc / Enter（**UI 线程**）：关掉预览。
        /// ⚠ 这里**不能**发 `RunAndClose`：它内部先问一遍 `GetFocusedWindowType()`，对我们一律是 `Invalid`，
        ///   于是跳过「关窗、交给系统开」那条路，又撞上 `IsForegroundWindowBelongToSelf()`（前台是我们的宿主，
        ///   不是 QuickLook 自己）⇒ 什么都不做。`Close` 是**无条件**关窗的，正是我们要的。
        /// </summary>
        public static void DoClose()
        {
            if (!PreviewVisible()) return;
            lastNavPath = null;
            Send(MsgClose, "");
        }

        /// <summary>
        /// 常驻实例**现在是不是开着预览窗**。
        ///
        /// 判据：QuickLook 的窗口都是 WPF 的，类名长 `HwndWrapper[QuickLook.exe;;&lt;guid&gt;]`
        /// （本机实测：常驻进程有 3 扇这种窗，闲置时全都不可见；预览一露脸就看得到可见的那一扇）。
        /// 只认「可见」，**不认标题** —— 标题会跟着预览的文件变。
        ///
        /// 为什么非要这道闸：`SelectedPathsIn` 一次 16~41ms 且只能在 UI 线程跑，而**平时拿方向键翻目录
        /// 根本不关预览的事** —— 没它的话，每次方向键都要白花这几十毫秒，界面会一格一格地顿。
        /// ⚠ 判错的代价是不对称的：判成「开着」只是多枚举一次（无害），判成「没开」则方向键不换内容
        ///   ⇒ 所以**宁可宽一点**（任何可见的 QuickLook WPF 窗都算数）。
        /// </summary>
        private static bool PreviewVisible()
        {
            try
            {
                bool vis = false;
                EmbedApi.EnumWindowsProc cb = null;
                cb = delegate(IntPtr h, IntPtr l)
                {
                    if (!EmbedApi.IsWindowVisible(h)) return true;
                    string c = EmbedApi.ClassOf(h);
                    if (c == null || c.IndexOf(WindowClassMark, StringComparison.OrdinalIgnoreCase) < 0) return true;
                    vis = true;
                    return false;
                };
                EmbedApi.EnumWindows(cb, IntPtr.Zero);
                GC.KeepAlive(cb);
                return vis;
            }
            catch { return false; }
        }

        /// <summary>
        /// 让那个常驻的 QuickLook 预览一个路径。
        ///
        /// ⚠ **不走** `QuickLook.exe &lt;路径&gt;`（起第二实例）那条路：那是 QuickLook 官方支持的外部
        /// 入口，但代价是**每按一次空格创建一个进程** —— 实测就是它把「比在原生资源管理器里按」
        /// 拖成了肉眼可见的慢（起进程几十到几百毫秒；管道 0.1~0.3ms）。
        ///
        /// 协议是从 QuickLook 自己的 `PipeServerManager.PostMessage` 抄的，就一行文本：
        ///   管道名  `QuickLook.App.Pipe.&lt;用户 SID&gt;`
        ///   内容    `QuickLook.App.PipeMessages.Toggle|&lt;路径&gt;|` + 换行（UTF-8 无 BOM）
        /// 服务端是 `StreamReader.ReadLine()` —— 换行**必须有**，而且**绝不能发空行**
        /// （它会拿 null 去 `Split`、把自己那条读线程搞崩）。
        ///
        /// 管道连不上（QuickLook 没在跑、或者哪天协议改了）就**回退**到起第二实例那条老路。
        /// </summary>
        public static void Preview(string path)
        {
            Send(MsgToggle, path);
        }

        /// <summary>
        /// 发一条消息给常驻实例。先走管道；管道不通再回退到「起第二实例」那条老路
        /// （第二实例会把 `Toggle` 转给常驻的那个；对 `Switch` 也够用 —— 预览已经开着时
        /// `Toggle(别的文件)` 的语义就是「切过去」，见 `ViewWindowManager.TogglePreview`）。
        /// ⚠ 唯独 `Close` **不走回退**：第二实例关不掉那个常驻窗口，起了也是白发一个进程。
        /// </summary>
        private static bool Send(string msg, string path)
        {
            if (SendViaPipe(msg, path)) return true;
            if (msg == MsgClose) return false;

            string exe = FindExe();
            if (exe == null) { Diag.Step("QLPreview: 管道不通，也找不到 QuickLook.exe"); return false; }
            LegacyStart(exe, path);
            return true;
        }

        /// <summary>往常驻实例的命名管道写一行消息。连上并写完返回 true。</summary>
        private static bool SendViaPipe(string msg, string path)
        {
            string name = PipeNameOf();
            if (string.IsNullOrEmpty(name)) return false;
            try
            {
                using (NamedPipeClientStream c = new NamedPipeClientStream(".", name, PipeDirection.Out))
                {
                    c.Connect(PipeConnectMs);
                    using (StreamWriter w = new StreamWriter(c, new UTF8Encoding(false)))
                    {
                        // 协议是 `消息|路径|选项`：路径/选项可以为空，但那两根竖线必须都在
                        //（服务端按 `Split('|')` 取，因为 `Split.Length <= 1` 会被当成非法消息丢掉）。
                        // ⚠ `Close` 发出去的是 `Close||`，不是空行 —— 空行会让它 `ReadLine()` 拿到 null。
                        w.WriteLine(msg + "|" + path + "|");
                        w.Flush();
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Diag.Step("QLPreview: 管道没走通（" + ex.GetType().Name + "），改用第二实例");
                return false;
            }
        }

        /// <summary>回退路径：起一个短命的第二实例，它自己会把消息转发给常驻实例然后退出。</summary>
        private static void LegacyStart(string exe, string path)
        {
            try
            {
                ProcessStartInfo si = new ProcessStartInfo(exe, "\"" + path + "\"");
                si.UseShellExecute = false;
                Process.Start(si);
            }
            catch (Exception ex) { Diag.Log("QLPreview: 起 QuickLook 失败 " + ex.Message); }
        }

        /// <summary>
        /// 常驻那个 QuickLook 的命名管道名（`QuickLook.App.Pipe.&lt;用户 SID&gt;`）。
        /// 拿不到 SID 返回 null（调用方回退）。
        /// </summary>
        private static string PipeNameOf()
        {
            if (pipeName != null) return pipeName;
            try
            {
                string sid = WindowsIdentity.GetCurrent().User.Value;
                if (!string.IsNullOrEmpty(sid)) pipeName = "QuickLook.App.Pipe." + sid;
            }
            catch (Exception ex) { Diag.Log("QLPreview: 取 SID 失败 " + ex.Message); }
            return pipeName;
        }

        /// <summary>
        /// QuickLook.exe 在哪（**只给回退路径用**）。找不到返回 null。
        ///
        /// 为什么不用注册表：本机（以及不少人）用的是便携版，装的时候没写卸载项、也没写 App Paths，
        /// 注册表里根本查不到。而 QuickLook 要能预览就**必须常驻**，所以「正在跑的那个 QuickLook
        /// 进程的可执行文件路径」是最靠得住的一条。
        /// ⚠ `MainModule` 对位数不同的进程会抛（QuickLook 主程序是 64 位，我们也是，正常可读）；
        ///   抛了换下一个候选，全都不行就返回 null。
        /// </summary>
        private static string FindExe()
        {
            if (!string.IsNullOrEmpty(cachedExe) && File.Exists(cachedExe)) return cachedExe;

            string found = null;
            try
            {
                Process[] ps = Process.GetProcessesByName("QuickLook");
                try
                {
                    for (int i = 0; i < ps.Length && found == null; i++)
                    {
                        try
                        {
                            string f = ps[i].MainModule.FileName;
                            if (!string.IsNullOrEmpty(f) && File.Exists(f)) found = f;
                        }
                        catch { }
                    }
                }
                finally
                {
                    for (int i = 0; i < ps.Length; i++) { try { ps[i].Dispose(); } catch { } }
                }
            }
            catch { }

            // 找到就缓存（省掉几毫秒的进程枚举）；没找到**不缓存** —— 用户可能等下才开 QuickLook
            if (found != null) cachedExe = found;
            return found;
        }
    }
}
