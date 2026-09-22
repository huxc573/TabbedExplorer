using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace TabbedExplorer
{
    /// <summary>
    /// 一个标签 = 一个真 explorer 窗口。
    ///
    /// 做法（已在 Win10 19045 上探针验证）：
    ///   1. `explorer.exe /n,/separate,&lt;路径&gt;` 起一个**独立 explorer 进程**的窗口
    ///      —— 独立进程很关键：万一搬坏了，kill 掉它就行，桌面纹丝不动。
    ///   2. 找到那个进程的 CabinetWClass 顶层窗口。
    ///   3. 把它从顶层窗口**降级成子窗口**（清 WS_POPUP/WS_CAPTION/... 加 WS_CHILD，再 SetParent）
    ///      —— 必须搬整个 CabinetWClass，不能只搬 ShellTabWindowClass：前者会跟随父窗口尺寸，
    ///      后者会自己把尺寸改回去（实测 3 秒后被改回）。
    ///   4. 键盘要跨境，用 AttachThreadInput 把我们的输入队列接到 explorer 那个线程上。
    /// </summary>
    internal sealed class ExplorerHost : IDisposable
    {
        public Panel Host { get; private set; }
        public string TargetPath { get; private set; }
        public IntPtr CabWindow { get; private set; }
        public int ExplorerPid { get; private set; }

        /// <summary>嵌入窗口顶部那条空白的像素高度（explorer 留给标题栏/QAT 的，子窗口画不出来）。</summary>
        public int TopBlank { get; private set; }

        /// <summary>嵌好了（此时才看得到内容）。</summary>
        public event EventHandler Ready;
        /// <summary>起不来（超时等）。</summary>
        public event EventHandler Failed;
        /// <summary>这个标签里 explorer 的标题变了（用户在里面导航、或进了别的目录）。</summary>
        public event EventHandler TitleChanged;
        /// <summary>
        /// 这个标签里的 explorer **自己没了**（最典型：用户在它里面按了 Ctrl+W，explorer 自带“关闭窗口”；
        /// 或者那个 explorer 进程崩了）。这时我们的标签已经是个空壳，上层应该把它收掉。
        /// </summary>
        public event EventHandler Died;

        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;

        private readonly HashSet<int> pidsBefore = new HashSet<int>();
        /// <summary>启动前就存在的文件夹窗口（他手开的原生窗口）—— 别把这些当成我们的标签。</summary>
        private readonly HashSet<IntPtr> cabsBefore = new HashSet<IntPtr>();

        private readonly Timer poll;
        private readonly Timer titlePoll;
        /// <summary>嵌入后再补量一次顶部空白（explorer 有惰性布局，隐藏期间量不准）。</summary>
        private readonly Timer settle;
        private string lastTitle;
        private DateTime startedAt;

        // ---- 防「开标签时窗口闪一下」 ----
        /// <summary>「哪个窗口刚被显示」的系统广播：轮询最快 25ms，它能在 0ms 级就把它藏掉。</summary>
        private readonly WinShowWatcher watcher = new WinShowWatcher();
        private IntPtr pendingCab = IntPtr.Zero;   // 已发现、已藏起、在等它加载完的那个窗口
        private int pendingPid;
        private DateTime cabSeenAt;

        /// <summary>这个标签左边要显示的图标（= 当前文件夹的图标，导航后自己会变）。</summary>
        public Bitmap TabIcon { get { return tabIcon; } }
        /// <summary>图标变了（刚嵌好 / 用户在里导航了）。</summary>
        public event EventHandler IconChanged;

        private Bitmap tabIcon;
        private IntPtr lastIconHandle = IntPtr.Zero;
        private int iconTarget;

        /// <summary>
        /// 这个标签**现在**在哪个文件夹 —— 就是地址栏上那个字符串（真目录时就是完整路径）。
        ///
        /// 为什么不能只用 `TargetPath`：那只是「我们当初让 explorer 打开的」。川在标签里一路点进去之后
        /// 它就不对了，而「记忆标签」记的必须是他**最后停在哪儿**。
        ///
        /// 怎么读到的（2026-09-22 绕了一圈才找对的那条路）：
        ///   1. ShellWindows / `IWebBrowser2.LocationURL` 内容是对的，但窗口被我们 SetParent 之后
        ///      它回报的 HWND 变成**我们的顶层窗口** —— 一张桌面上的所有标签会撞成同一个 key，废；
        ///   2. `AccessibleObjectFromWindow(OBJID_NATIVEOM)` 在 CabinetWClass 上直接 E_FAIL；
        ///   3. 地址栏那个 `ToolbarWindow32` 的**窗口文本就是地址本身**
        ///      （实测 `地址: D:\Dev\Workspaces\WorkBuddy\TabbedExplorer`），
        ///      而它就在我们手上这个 HWND 的子树里 —— 按窗口读，天然不会串台。
        /// </summary>
        public string CurrentPath { get { return currentPath; } }
        /// <summary>当前文件夹变了（刚嵌好 / 用户在里导航了）。</summary>
        public event EventHandler PathChanged;

        private IntPtr addressBand;
        private string currentPath;

        private uint origStyle;
        private WRECT origRect;
        private bool embedded;
        private bool disposed;
        /// <summary>
        /// 这个标签是**接管**来的（川自己从开始菜单/桌面打开的窗口），不是我们起的。
        /// 区别只在收尾：接管的窗口关标签时要**把窗口关掉**（不然桌面上留一个孤儿），
        /// 而且**绝不能 kill 它的 explorer 进程**（多半就是桌面那个 shell 进程）。
        /// </summary>
        private bool adopted;

        public string LastError { get; private set; }

        /// <summary>
        /// 这个标签已经自动重试过一次了（防死循环）。
        /// 用在上层：自己起的窗口 25 秒没等到 —— 多半是被同时开的别的标签抢了 — 允许再来一次。
        /// </summary>
        public bool Retried { get; set; }

        public ExplorerHost()
        {
            Host = new Panel();
            Host.Dock = DockStyle.Fill;
            Host.BackColor = Theme.Pane;
            Host.Resize += delegate { LayoutCab(false); };

            poll = new Timer();
            poll.Interval = 150;
            poll.Tick += OnPoll;

            // 标签标题要跟着 explorer 的导航走。
            // 不要指望窗口自己通知我们：跨进程收不到它的通知，而子窗口也没有 WM_SETTEXT 转发。
            // 但 CabinetWClass 的**标题本身会跟着导航变**（实测：进「视频」后标题就是「视频」），
            // 所以轻量轮询窗口标题就够了。
            titlePoll = new Timer();
            titlePoll.Interval = 500;
            titlePoll.Tick += OnTitlePoll;

            settle = new Timer();
            settle.Interval = 250;
            settle.Tick += OnSettle;
        }

        // ==================================================================
        public void Start(string path)
        {
            TargetPath = path;
            startedAt = DateTime.Now;
            pidsBefore.Clear();
            foreach (Process p in Process.GetProcessesByName("explorer"))
            {
                try { pidsBefore.Add(p.Id); } catch { }
                finally { p.Dispose(); }
            }

            // 也快照一份「已经存在的文件夹窗口」：下面找新窗口时用它排掉他手开的原生窗口
            cabsBefore.Clear();
            EmbedApi.CollectCabs(cabsBefore);

            string arg = CommandLineTarget(path);
            try
            {
                Diag.Step("Embed: 起 explorer " + arg);
                Process.Start("explorer.exe", arg);
            }
            catch (Exception ex)
            {
                LastError = "起 explorer 失败: " + ex.Message;
                Diag.Log("Embed: " + LastError);
                RaiseFailed();
                return;
            }

            // 25ms 一轮：这个窗口要尽快被发现并藏起来（150ms 时肉眼就看得见它闪一下）
            poll.Interval = 25;
            poll.Start();

            // 系统广播那条路更快（0ms 级），两条一起用
            watcher.WindowShown += OnAnyWindowShown;
            watcher.Start();
        }

        /// <summary>
        /// 还没开始起之前先把「要去哪儿」记上。
        /// ⚠ 必须要：起 explorer 现在是**串行排队**的（见 `EmbedForm.PumpLaunch`），
        /// 排在前面的还没轮到时 `TargetPath` 是空的 —— 而「中途保存记忆」是随时会发生的，
        /// 那时 `LivePath` 拿不到路径，这个还没起的标签就会**被从记忆里抹掉**。
        /// </summary>
        public void PresetTarget(string path)
        {
            if (string.IsNullOrEmpty(TargetPath)) TargetPath = path;
        }

        /// <summary>
        /// 接管一个**别人建出来的**文件夹窗口（川从开始菜单 / 桌面双击打开的）。
        ///
        /// 跟自己起那条路的区别：这里不 `Process.Start`（窗口已经有了），
        /// 所以也不会一闪 —— 它可能已经在屏幕上露了一下，我们**立刻把它藏掉**；
        /// 之后就完全汇进同一条流水线（`OnPoll` 等文件列表建好 → `AttachWindow` 嵌进来）。
        ///
        /// `TargetPath` 这时候还不知道（我们没让它开哪儿）：嵌好之后从地址栏读回来就是。
        /// </summary>
        public void Adopt(IntPtr cab, int pid)
        {
            if (disposed || embedded || pendingCab != IntPtr.Zero || cab == IntPtr.Zero) return;

            // ⚠⚠ 这一步是**安全底线**，顺序不能动：
            // 接管的窗口多半属于**桌面那个 shell explorer 进程**。`KillOwnExplorer` 判「该不该杀」
            // 靠的就是 pidsBefore —— 先把当前所有 explorer pid 快照进来，那个 pid 就绝不会被列进
            // 「我们自己起的」。少做这一步，关一个标签就会把整个桌面（explorer.exe）杀掉。
            pidsBefore.Clear();
            foreach (Process p in Process.GetProcessesByName("explorer"))
            {
                try { pidsBefore.Add(p.Id); } catch { }
                finally { p.Dispose(); }
            }

            adopted = true;
            TargetPath = null;
            startedAt = DateTime.Now;
            pendingCab = cab;
            pendingPid = pid;
            ExplorerPid = pid;
            cabSeenAt = DateTime.Now;
            EmbedApi.Claim(cab);
            Diag.Step(string.Format("Embed: 接管他开的窗口 cab=0x{0:X} pid={1}", cab.ToInt64(), pid));
            EmbedApi.ShowWindow(cab, SW_HIDE);
            poll.Interval = 25;
            poll.Start();
        }

        /// <summary>
        /// 这个窗口现在显示的是不是我们要开的那个路径 —— 读它的地址栏来验（跟「标签第二行」同一条路）。
        ///
        /// 三条「问不出来就放行」（返回 true）的规矩，都是为了**别把该嵌的窗口误判掉**：
        ///   · 我们本来就没指定路径（接管别人窗口时）：没法比，放行；
        ///   · 地址栏还没建出来：还没加载到那一步，放行；
        ///   · 读到的字符串翻译不成可比的路径（库/虚拟文件夹）：放行。
        /// 宁可偶尔嵌错（4 秒后会放宽），也不能因为比对不了就永远不开。
        /// </summary>
        private static bool CabMatches(IntPtr cab, string wanted)
        {
            if (string.IsNullOrEmpty(wanted)) return true;
            if (!PathRules.Restorable(wanted)) return true;
            try
            {
                IntPtr ab = EmbedApi.FindAddressBand(cab);
                if (ab == IntPtr.Zero) return true;
                string got = PathRules.Store(
                    EmbedApi.StripAddressPrefix(EmbedApi.WindowTextOf(ab)));
                if (string.IsNullOrEmpty(got)) return true;
                return PathRules.Same(got, wanted);
            }
            catch { return true; }
        }

        /// <summary>把 shell 路径转成 explorer.exe 认的命令行形式。</summary>
        private static string CommandLineTarget(string path)        {
            string t = path;
            if (string.IsNullOrEmpty(t) ||
                string.Equals(t, ExplorerView.ThisPcPath, StringComparison.OrdinalIgnoreCase))
            {
                t = "shell:MyComputerFolder";   // 「此电脑」
            }
            return "/n,/separate,\"" + t + "\"";
        }

        /// <summary>
        /// 等窗口出现 → 发现就藏 → 等它加载完 → 嵌进来。
        ///
        /// 为什么要「藏」：`explorer.exe /n,/separate` 起的窗口会**先在桌面上画出来**，
        /// 我们轮询到它再 SetParent 之间有一段可见时间 —— 川看到的就是「开标签时闪一下」。
        /// 藏起来之后它压根不在桌面上露面，等内部加载好了再作为子窗口现身到我们容器里。
        ///
        /// （为什么不干脆让它「最小化启动」：那个窗口是 DCOM 激活出来的进程建的，
        ///   给 launcher 传的 STARTUPINFO 显示状态到不了它手上，这条走不通。）
        /// </summary>
        private void OnPoll(object sender, EventArgs e)
        {
            if (disposed) { poll.Stop(); return; }

            if (pendingCab == IntPtr.Zero)
            {
                // 4 秒还没找到就放宽「进程必须新」这条（万一 shell 复用了老进程建窗口）
                bool relax = (DateTime.Now - startedAt).TotalSeconds > 4;
                int pid;
                IntPtr cab = EmbedApi.FindNewCab(cabsBefore, pidsBefore, relax, out pid);
                if (cab != IntPtr.Zero && !relax && !CabMatches(cab, TargetPath))
                {
                    // 找到了一个窗口，但地址栏显示的不是我们要开的那个 —— 十有八九是
                    // **同时开了好几个标签**，把别人的窗口扫到自己这儿了（川报的「有时候标签页
                    // 点开是空的」就是这个：真窗口被别的标签嵌走，这个标签就永远等不到东西）。
                    // 先别嵌，继续等；4 秒后 relax 打开就不再挑了（宁可就近凑一个，也别永远空着）。
                    Diag.Step("Embed: 扫到的窗口地址对不上，继续等：" + cab.ToInt64().ToString("X"));
                    cab = IntPtr.Zero;
                }
                if (cab == IntPtr.Zero)
                {
                    if ((DateTime.Now - startedAt).TotalSeconds > 25)
                    {
                        poll.Stop();
                        try { watcher.Dispose(); } catch { }
                        LastError = "等 explorer 窗口超时（25s）";
                        Diag.Log("Embed: " + LastError);
                        RaiseFailed();
                    }
                    return;
                }

                Diag.Step(string.Format("Embed: 发现 cab=0x{0:X} pid={1}{2}，先藏起来（别让它闪）",
                    cab.ToInt64(), pid, relax ? "（已放宽 pid 条件）" : ""));
                pendingCab = cab;
                pendingPid = pid;
                EmbedApi.Claim(cab);       // 登记：Hub 那个「谁来都抓」的监听看见已登记就放手
                ExplorerPid = pid;         // 还没嵌进来就被关掉时，Close() 靠它收进程
                cabSeenAt = DateTime.Now;
            }

            // shell 自己可能又 ShowWindow 一次，所以每一轮都确保它是藏着的
            if (EmbedApi.IsWindowVisible(pendingCab)) EmbedApi.ShowWindow(pendingCab, SW_HIDE);

            // 「真加载完了」的判据：文件列表（SHELLDLL_DefView）已经建出来。
            // 隐藏的窗口有惰性（有的东西要等显示才建），所以最多等 1200ms 就照样嵌，
            // 不能为了好看把开标签拖成两秒。
            bool viewReady = WinFind.ByClass(pendingCab, "SHELLDLL_DefView") != IntPtr.Zero;
            double waited = (DateTime.Now - cabSeenAt).TotalMilliseconds;
            if (!viewReady && waited < 1200) return;

            Diag.Step(string.Format("Embed: 开始嵌入（文件列表{0}，发现后等了 {1}ms）",
                viewReady ? "已就绪" : "还没建好/超时", (int)waited));

            IntPtr cab2 = pendingCab;
            int pid2 = pendingPid;
            pendingCab = IntPtr.Zero;
            AttachWindow(cab2, pid2);
        }

        /// <summary>
        /// 「某个窗口刚被显示」的系统广播（0ms 级）—— 如果是我们那个还没嵌的 explorer 窗口，
        /// 立刻把它藏掉。轮询最快也要 25ms，这一位才是「一点不闪」的关键。
        /// 回调在 UI 线程上（WINEVENT_OUTOFCONTEXT 投递到注册它的那个线程的消息队列）。
        /// </summary>
        private void OnAnyWindowShown(IntPtr h, int tick)
        {
            if (disposed || pendingCab != IntPtr.Zero) return;   // 已经盯上了，轮询那边每轮都会藏

            string c = WinFind.ClassOf(h);
            if (c != "CabinetWClass" && c != "ExploreWClass") return;
            if (cabsBefore.Contains(h)) return;
            // 启动时就在那儿的窗口不算新开（见 EmbedApi 里「基线」那段）；
            // 已经被别的标签收走的更不能抢。
            if (EmbedApi.IsBaseline(h)) return;
            if (EmbedApi.IsClaimed(h)) return;

            int pid = EmbedApi.ProcessIdOf(h).ToInt32();
            if (pid == 0) return;

            // 先藏再写日志（日志是文件 IO，虽然只有零点几毫秒，但防闪这种事越早越好）
            EmbedApi.ShowWindow(h, SW_HIDE);
            Diag.Step(string.Format("Embed: 新窗口刚显示就藏掉 cab=0x{0:X} pid={1}（事件后 {2}ms）",
                h.ToInt64(), pid, Environment.TickCount - tick));
            EmbedApi.Claim(h);          // 先登记再往下走：Hub 的监听（如果插在我们前面）一看已登记就不抢了
            pendingCab = h;
            pendingPid = pid;
            ExplorerPid = pid;
            cabSeenAt = DateTime.Now;
        }

        private void AttachWindow(IntPtr cab, int pid)
        {
            poll.Stop();
            watcher.Dispose();          // 盯上了、要嵌进来了，不用再听系统广播
            try
            {
                ExplorerPid = pid;
                CabWindow = cab;
                origStyle = EmbedApi.GetStyle(cab);
                EmbedApi.GetWindowRect(cab, out origRect);

                // 顺序不能反：先改样式，再 SetParent
                uint child = EmbedApi.ToChildStyle(origStyle);
                EmbedApi.SetStyle(cab, child);
                EmbedApi.SetParent(cab, Host.Handle);
                embedded = true;
                Diag.Step(string.Format("Embed: 接管 cab=0x{0:X} pid={1} style 0x{2:X8}->0x{3:X8}",
                    cab.ToInt64(), pid, origStyle, child));

                // 防闪流程里 cab 一直是藏着的，这时量出来的空白可能是 0，先按它摆一下位置即可；
                // 真正的值以「现身之后」那次为准（下面 + settle 定时器）。
                TopBlank = EmbedApi.TopBlankOf(cab);
                LayoutCab(true);

                // 我们把它藏过（防闪），这里放开 —— 此时父面板可能还不可见，所以不会在桌面上闪出来
                EmbedApi.ShowWindow(cab, SW_SHOW);

                // 现身之后子窗口位置才是最终可信值：立刻补量一次，再让 settle 定时器兜一次惰性布局
                int tb = EmbedApi.TopBlankOf(cab);
                if (tb != TopBlank) { TopBlank = tb; LayoutCab(true); }
                Diag.Step("Embed: 顶部空白 = " + TopBlank + "px");
                settle.Start();
                RefreshIcon(true);          // 接手时先要一颗图标（当前文件夹的）
                RefreshPath();              // 也先要一次当前路径

                Focus();
                lastTitle = CurrentDisplayName;
                titlePoll.Start();
                RaiseReady();
            }
            catch (Exception ex)
            {
                LastError = "嵌入失败: " + ex.Message;
                Diag.Log("Embed: " + LastError);
                RaiseFailed();
            }
        }

        /// <summary>
        /// 嵌入 250ms 后再量一次顶部空白。explorer 对隐藏窗口有惰性布局（有的子窗口要等显示才排），
        /// 所以「现身那一刻」量到的值仍可能是 0；变了就重摆一次，免得那条空白留在容器里变成死白。
        /// </summary>
        private void OnSettle(object sender, EventArgs e)
        {
            settle.Stop();
            if (disposed || !embedded || CabWindow == IntPtr.Zero) return;
            int tb = EmbedApi.TopBlankOf(CabWindow);
            if (tb != TopBlank)
            {
                Diag.Step("Embed: 顶部空白修正 " + TopBlank + " -> " + tb + "px");
                TopBlank = tb;
                LayoutCab(true);
            }
        }

        /// <summary>
        /// 颜色模式变了：把嵌进来的这个 explorer 窗口（含它整棵 shell 子树）重新上一次主题。
        /// **尽力而为** —— 它是独立进程，它自己的进程级深色开关我们改不了，
        /// 逐窗口 SetWindowTheme 只能改到外框/导航窗格，文件列表那一层仍按系统主题画。
        /// </summary>
        public void Restyle()
        {
            if (disposed || !embedded || CabWindow == IntPtr.Zero) return;
            try { Theme.StyleShellTree(CabWindow); }
            catch (Exception ex) { Diag.Log("Embed: 重新上色失败 " + ex.Message); }
        }

        /// <summary>
        /// 把这条标签的 explorer 进程**驻留内存**收一收（非激活标签省内存，川报的优化 3）。
        ///
        /// 做什么：`EmptyWorkingSet` —— 把它当前驻留的物理页尽量换到 standby 列表。
        /// **安全**：进程本身、它开的窗口、里面的状态一点都不动，只是那些页下次被访问时
        /// 按需再缺页读回，所以被切回去时会有极短的一点「加载感」。
        /// 正因为有这点代价，调用方只对**已经凉下来的非激活标签**做（见 EmbedForm.TrimInactiveTabs）。
        ///
        /// 为什么只有这条路真有用：一个标签 = 一个独立 explorer.exe，
        /// 闲着的那几十 MB 全在**别人进程**里，我们自己进程怎么省都省不出这些。
        /// </summary>
        public void TrimMemory()
        {
            if (disposed || ExplorerPid == 0) return;
            // 不是我们起的进程（收编来的窗口常跟桌面外壳共用一个 explorer.exe）——别动，
            // 那里面还跑着他桌面/任务栏，动了会让他整台机器都有感觉。
            if (pidsBefore.Contains(ExplorerPid)) return;

            IntPtr h = IntPtr.Zero;
            try
            {
                h = NativeMethods.OpenProcess(
                    NativeMethods.PROCESS_QUERY_INFORMATION | NativeMethods.PROCESS_SET_QUOTA,
                    false, ExplorerPid);
                if (h == IntPtr.Zero) return;   // 权限不够就安静放弃，不是错
                if (NativeMethods.EmptyWorkingSet(h))
                    Diag.Log("Embed: 已收内存 pid=" + ExplorerPid);
            }
            catch { }
            finally
            {
                if (h != IntPtr.Zero)
                {
                    try { NativeMethods.CloseHandle(h); } catch { }
                }
            }
        }

        /// <summary>上层（标签条）告诉我们图标要画多大（设备像素）。</summary>
        public void SetIconTarget(int px)
        {
            iconTarget = px;
            RefreshIcon(true);
        }

        /// <summary>
        /// 读窗口自己那颗图标并画成我们的位图。
        /// 见 `EmbedApi.WindowIcon`：explorer 会按当前文件夹换掉它，所以这就是「实时文件夹图标」。
        /// force = 忽略「句柄没变」的短路（首次 / 尺寸变了时用）。
        /// </summary>
        private void RefreshIcon(bool force)
        {
            if (disposed || !embedded || CabWindow == IntPtr.Zero || iconTarget <= 0) return;
            try
            {
                IntPtr h = EmbedApi.WindowIcon(CabWindow);
                if (h == IntPtr.Zero) return;
                if (!force && h == lastIconHandle) return;

                Bitmap b = ShellIcon.FromForeignHIcon(h, iconTarget);
                if (b == null) return;
                if (IsBlank(b)) { b.Dispose(); return; }   // 句柄刚好被换掉的瞬间会画不上去，别把好图标盖成空白

                lastIconHandle = h;
                Bitmap old = tabIcon;
                tabIcon = b;
                EventHandler e = IconChanged;
                if (e != null) e(this, EventArgs.Empty);   // 先把新图标交给标签条
                if (old != null) { try { old.Dispose(); } catch { } }   // 此刻它已经没人引用了
            }
            catch (Exception ex) { Diag.Log("Embed: 取标签图标失败 " + ex.Message); }
        }

        /// <summary>
        /// 读一次地址栏，变了就通知上层。
        /// 地址栏那个 toolbar 可能被 shell 在导航时重建，所以句柄失效就重新找一次（找一次很便宜）。
        /// 读不到 / 读出空值一律**保留上一次的值** —— 别让导航中途的空窗把好路径冲掉。
        /// </summary>
        private void RefreshPath()
        {
            if (disposed || !embedded || CabWindow == IntPtr.Zero) return;
            try
            {
                if (addressBand == IntPtr.Zero || !NativeMethods.IsWindow(addressBand) ||
                    !EmbedApi.IsDescendant(CabWindow, addressBand))
                {
                    addressBand = EmbedApi.FindAddressBand(CabWindow);
                    if (addressBand == IntPtr.Zero) return;
                }

                string s = EmbedApi.StripAddressPrefix(EmbedApi.WindowTextOf(addressBand));
                if (string.IsNullOrEmpty(s)) return;
                if (string.Equals(s, currentPath, StringComparison.Ordinal)) return;

                currentPath = s;
                Diag.Step("Embed: 现在在 " + s);
                EventHandler e = PathChanged;
                if (e != null) e(this, EventArgs.Empty);
            }
            catch (Exception ex) { Diag.Log("Embed: 读地址栏失败 " + ex.Message); }
        }

        private static bool IsBlank(Bitmap b)
        {
            try
            {
                for (int y = 0; y < b.Height; y++)
                    for (int x = 0; x < b.Width; x++)
                        if (b.GetPixel(x, y).A != 0) return false;
                return true;
            }
            catch { return true; }
        }

        private void LayoutCab(bool force)
        {
            if (!embedded || CabWindow == IntPtr.Zero) return;
            int w = Host.ClientSize.Width, h = Host.ClientSize.Height;
            if (w <= 0 || h <= 0) return;
            uint flags = EmbedApi.SWP_NOZORDER | EmbedApi.SWP_NOACTIVATE;
            if (force) flags |= EmbedApi.SWP_FRAMECHANGED;
            // 上移一个 TopBlank，把 explorer 留的那条空白（原标题栏位置）顶到容器外面裁掉；
            // 高度补回来，免得底部的状态栏被切。我们的自绘工具栏就画在那条位置上方。
            EmbedApi.SetWindowPos(CabWindow, IntPtr.Zero, 0, -TopBlank, w, h + TopBlank, flags);
        }

        /// <summary>
        /// 给这个标签里的 explorer 派一个快捷键。
        /// 自绘的快速访问工具栏靠它干活：剪切/删除/属性这些动作由 shell 视图自己实现，
        /// 我们只负责把键送到「这个 explorer 线程当前持有焦点的那个窗口」上，行为最正宗。
        /// </summary>
        public void SendCommand(int vk, bool ctrl, bool shift, bool alt)
        {
            if (!embedded || CabWindow == IntPtr.Zero) return;
            uint tid = EmbedApi.GetWindowThreadProcessId(CabWindow, IntPtr.Zero);
            if (tid == 0) return;

            IntPtr target = EmbedApi.FocusedWindowOf(tid);
            if (target == IntPtr.Zero) target = WinFind.ByClass(CabWindow, "SHELLDLL_DefView");
            if (target == IntPtr.Zero) target = CabWindow;

            try
            {
                if (ctrl) EmbedApi.PostMessageW(target, EmbedApi.WM_KEYDOWN, new IntPtr(EmbedApi.VK_CONTROL), IntPtr.Zero);
                if (shift) EmbedApi.PostMessageW(target, EmbedApi.WM_KEYDOWN, new IntPtr(EmbedApi.VK_SHIFT), IntPtr.Zero);
                if (alt) EmbedApi.PostMessageW(target, EmbedApi.WM_KEYDOWN, new IntPtr(EmbedApi.VK_MENU), IntPtr.Zero);

                EmbedApi.PostMessageW(target, EmbedApi.WM_KEYDOWN, new IntPtr(vk), IntPtr.Zero);
                EmbedApi.PostMessageW(target, EmbedApi.WM_KEYUP, new IntPtr(vk), new IntPtr(unchecked((int)0xC0000001)));

                if (alt) EmbedApi.PostMessageW(target, EmbedApi.WM_KEYUP, new IntPtr(EmbedApi.VK_MENU), new IntPtr(unchecked((int)0xC0000001)));
                if (shift) EmbedApi.PostMessageW(target, EmbedApi.WM_KEYUP, new IntPtr(EmbedApi.VK_SHIFT), new IntPtr(unchecked((int)0xC0000001)));
                if (ctrl) EmbedApi.PostMessageW(target, EmbedApi.WM_KEYUP, new IntPtr(EmbedApi.VK_CONTROL), new IntPtr(unchecked((int)0xC0000001)));

                Diag.Step(string.Format("Embed: 派快捷键 vk=0x{0:X2} ctrl={1} shift={2} alt={3} -> 0x{4:X}",
                    vk, ctrl, shift, alt, target.ToInt64()));
            }
            catch (Exception ex) { Diag.Log("Embed: 派快捷键失败 " + ex.Message); }
        }

        /// <summary>
        /// 把键盘焦点交给这个标签里的 explorer。
        /// 跨进程 SetFocus 会被拒，必须临时 AttachThreadInput；
        /// **设完焦点立刻 detach** —— 把两个进程的输入队列长期绑在一起，
        /// 一方卡住另一方也会跟着卡，看着就像“程序崩了”。
        /// </summary>
        public void Focus()
        {
            if (!embedded || CabWindow == IntPtr.Zero) return;
            uint ourTid = EmbedApi.GetCurrentThreadId();
            uint cabTid = EmbedApi.GetWindowThreadProcessId(CabWindow, IntPtr.Zero);
            if (cabTid == 0) return;

            IntPtr target = WinFind.ByClass(CabWindow, "SHELLDLL_DefView");
            if (target == IntPtr.Zero) target = WinFind.ByClass(CabWindow, "DirectUIHWND");
            if (target == IntPtr.Zero) return;

            bool attached = false;
            try
            {
                attached = EmbedApi.AttachThreadInput(ourTid, cabTid, true);
                EmbedApi.SetFocus(target);
            }
            catch (Exception ex)
            {
                Diag.Log("Embed: Focus 失败 " + ex.Message);
            }
            finally
            {
                if (attached)
                {
                    try { EmbedApi.AttachThreadInput(ourTid, cabTid, false); } catch { }
                }
            }
        }

        private void OnTitlePoll(object sender, EventArgs e)
        {
            if (disposed || !embedded || CabWindow == IntPtr.Zero)
            {
                titlePoll.Stop();
                return;
            }

            // 窗口还在不在？不在就是 explorer 把窗口关了（或它崩了）——
            // 不管哪种，这个标签都已经是个空壳，得让上层收掉，否则界面上留一块死的黑区。
            if (!NativeMethods.IsWindow(CabWindow))
            {
                titlePoll.Stop();
                Diag.Step("Embed: 嵌入的 explorer 窗口没了（它自己关了）-> 通知上层收标签");
                EventHandler d = Died;
                if (d != null) d(this, EventArgs.Empty);
                return;
            }

            string t = CurrentDisplayName;

            // 图标：explorer 会按「当前文件夹」换掉窗口自己挂的那颗图标。
            // 每轮都问一下句柄（很便宜），变了才重新画 —— 这样即使图标比标题晚一步才更新，
            // 下个 500ms 也追得上。
            RefreshIcon(false);
            RefreshPath();      // 地址栏那个字符串（记忆标签靠它，见 CurrentPath 的注释）

            if (string.Equals(t, lastTitle, StringComparison.Ordinal)) return;
            lastTitle = t;
            Diag.Step("Embed: 标题变了 -> " + t);
            EventHandler h = TitleChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        /// <summary>当前显示的路径（读窗口标题，最省事也够用）。</summary>
        public string CurrentDisplayName
        {
            get
            {
                string t = EmbedApi.TitleOf(CabWindow);
                return string.IsNullOrEmpty(t) ? "此电脑" : t;
            }
        }

        // ==================================================================
        public void Close(string why = "")
        {
            Diag.Step(string.Format("Embed: Close({0}) cab=0x{1:X} pid={2} embedded={3}",
                string.IsNullOrEmpty(why) ? "未说明" : why, CabWindow.ToInt64(), ExplorerPid, embedded));
            try { poll.Stop(); } catch { }
            try { titlePoll.Stop(); } catch { }
            try { settle.Stop(); } catch { }
            try { watcher.Dispose(); } catch { }

            // 登记表要摘掉（不管是已经嵌进来的还是还在等加载的），否则这个 HWND 会被永久占着
            if (CabWindow != IntPtr.Zero) EmbedApi.Release(CabWindow);
            if (pendingCab != IntPtr.Zero) EmbedApi.Release(pendingCab);
            pendingCab = IntPtr.Zero;   // 还没嵌进来就被关掉：下面 KillOwnExplorer 用 ExplorerPid 收进程

            // 先还原成顶层窗口，再结束进程 —— 避免窗口还挂在我们容器里就被销毁
            if (embedded && CabWindow != IntPtr.Zero)
            {
                try
                {
                    EmbedApi.SetStyle(CabWindow, origStyle);
                    EmbedApi.SetParent(CabWindow, IntPtr.Zero);
                    EmbedApi.SetWindowPos(CabWindow, IntPtr.Zero, origRect.Left, origRect.Top,
                        origRect.Width, origRect.Height,
                        EmbedApi.SWP_NOZORDER | EmbedApi.SWP_NOACTIVATE | EmbedApi.SWP_FRAMECHANGED);
                }
                catch (Exception ex) { Diag.Log("Embed: 还原失败 " + ex.Message); }

                // 关标签就该把**窗口**关掉。两种情况都是「只能关窗口、不能杀进程」：
                //   · 接管来的（不是我们起的进程）；
                //   · 是我们起的，但那个进程**不能杀** —— 实测 `explorer.exe /n,/separate` 起的窗口
                //     有时会落在**启动前就存在的 explorer 进程**里（很可能就是桌面那个 shell）。
                //     之前这种情况只把窗口还原成顶层就完事，结果是：关掉标签，桌面上留一个孤零零的
                //     资源管理器窗口（累积下来还会进下一次启动的「基线」，看着莫名其妙）。
                bool willKill = !adopted && ExplorerPid != 0 && !pidsBefore.Contains(ExplorerPid);
                if (!willKill)
                {
                    try
                    {
                        Diag.Step("Embed: 关掉窗口（不杀进程）cab=0x" + CabWindow.ToInt64().ToString("X"));
                        EmbedApi.PostMessageW(CabWindow, EmbedApi.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    }
                    catch (Exception ex) { Diag.Log("Embed: 关窗口失败 " + ex.Message); }
                }
            }
            embedded = false;
            CabWindow = IntPtr.Zero;
            addressBand = IntPtr.Zero;
            currentPath = null;
            KillOwnExplorer();
        }

        /// <summary>
        /// 只结束「我们自己起的、且不在启动前列表里」的 explorer 进程。
        /// 这个判断是**安全底线**：万一 /separate 没生效、窗口是主 explorer 进程的，
        /// 杀了它整个桌面都会重启。
        /// </summary>
        private void KillOwnExplorer()
        {
            int pid = ExplorerPid;
            ExplorerPid = 0;
            if (pid == 0 || pidsBefore.Contains(pid)) return;
            try
            {
                Process p = Process.GetProcessById(pid);
                p.Kill();
                Diag.Step("Embed: 已结束 explorer pid=" + pid);
                p.Dispose();
            }
            catch (Exception ex)
            {
                Diag.Log("Embed: 结束 explorer pid=" + pid + " 失败 " + ex.Message);
            }
        }

        private void RaiseReady()
        {
            EventHandler h = Ready;
            if (h != null) h(this, EventArgs.Empty);
        }

        private void RaiseFailed()
        {
            EventHandler h = Failed;
            if (h != null) h(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { poll.Dispose(); } catch { }
            try { titlePoll.Dispose(); } catch { }
            try { settle.Dispose(); } catch { }
            try { watcher.Dispose(); } catch { }
            Close();
        }
    }
}
