using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace TabbedExplorer
{
    /// <summary>
    /// 常驻后台的「总机」：托盘图标 + Win+E 钩子 + **每张虚拟桌面一个窗口**。
    ///
    /// 为什么要从 EmbedForm 里把「托盘 + 钩子」拆出来（2026-09-22）：
    /// 原来是谁先建窗口谁就兼任常驻，于是全进程只有**一个**窗口 ——
    /// 三张虚拟桌面共用一套标签，Win+E 每次都把那个窗口搬到当前桌面来。
    /// 川要的是「每个虚拟桌面单独捕获合并并记忆那个桌面关闭程序窗口时的标签页」，
    /// 那前提就是**每张桌面各有一个窗口**；而托盘图标和低级键盘钩子全进程只能有一份，
    /// 它们必须住在窗口之外 —— 就是这个 DesktopHub。
    ///
    /// 顺带消掉一整类老毛病：窗口不再被搬来搬去，
    /// 「按 Win+E 反而把人拽到另一张虚拟桌面」从结构上就不可能发生了
    /// （窗口在哪张桌面，就是给哪张桌面用的；要别的桌面就新建一个）。
    ///
    /// 关窗口（X / Ctrl+W 关到最后一个标签）= 收进托盘，进程不退；
    /// 真退出只有托盘菜单「退出」和命令行 `--quit`（两者都会先把各桌面的标签写进记忆）。
    /// </summary>
    internal sealed class DesktopHub : ApplicationContext
    {
        /// <summary>按虚拟桌面 GUID 登记窗口（Guid.Empty = 问不出桌面时的兜底桶）。</summary>
        private readonly Dictionary<string, EmbedForm> forms =
            new Dictionary<string, EmbedForm>(StringComparer.OrdinalIgnoreCase);

        private readonly DesktopMemory memory = new DesktopMemory();

        /// <summary>托盘右键菜单里那棵「设置」子树（设置变了只刷文字，不重建菜单）。</summary>
        private SettingsMenu.TraySettings traySettings;
        private ContextMenu trayMenu;

        /// <summary>迁移模式（v1.0.0）下，全进程唯一那个窗口在登记表里的键。</summary>
        private const string SingleKey = "single";

        /// <summary>钩子回调在别的线程上，动界面之前得先转回 UI 线程 —— 就是它。</summary>
        private readonly Control syncTarget = new Control();

        /// <summary>记忆不急着每改一下就写盘，攒一下再写（导航一次会连改好几项）。</summary>
        private readonly System.Windows.Forms.Timer saveTimer = new System.Windows.Forms.Timer();

        private NotifyIcon tray;
        private WinEHook hook;
        /// <summary>「谁被显示出来了」的系统广播 —— 用来抓川自己打开的文件夹窗口（Bug 1）。</summary>
        private WinShowWatcher captureWatch;
        /// <summary>看见了、但还没到点去收的候选窗口（值 = 第一次看见的时刻）。见 TryCapture。</summary>
        private readonly Dictionary<IntPtr, DateTime> pendingCapture = new Dictionary<IntPtr, DateTime>();
        /// <summary>「我们主动藏起来、还没收编的」窗口 —— 防闪用（川 2026-09-22：从桌面/开始菜单打开的会闪一下）。</summary>
        private readonly HashSet<IntPtr> hiddenByUs = new HashSet<IntPtr>();
        private System.Windows.Forms.Timer captureTimer;
        /// <summary>候选窗口要「晾」多久才收。够短，川感觉不出来；够长，让标签先把自己起的窗口认领掉。</summary>
        private const int CaptureDelayMs = 700;
        private RegisteredWaitHandle sigWait;
        private RegisteredWaitHandle quitWait;
        private EventWaitHandle quitEvent;
        private bool quitting;
        private bool trayTipShown;
        private bool disposed;

        public DesktopHub()
        {
            // 设置已经在 Program.Main 里 Load 过了（颜色模式得赶在 Theme 之前定下来）。
            memory.Load();

            saveTimer.Interval = 800;
            saveTimer.Tick += delegate { saveTimer.Stop(); SaveNow("攒够了一下"); };

            IntPtr h = syncTarget.Handle;   // 逼出句柄，之后才收得到 BeginInvoke
            GC.KeepAlive(h);

            SetupTray();
            SetupHook();
            SetupCapture();

            try { VirtualDesktop.WarmUp(); } catch (Exception ex) { Diag.Log("虚拟桌面: 预热异常 " + ex.Message); }

            if (Program.AutoOpen)
            {
                Diag.Step("Hub: --open -> 立刻在当前桌面开一个窗口");
                OnWinE();
            }
        }

        // ==================================================================
        // 托盘 + 钩子 + 两个命名事件
        // ==================================================================

        private void SetupTray()
        {
            // 托盘图标 = 程序自己的图标（exe 里编进去的那颗），不是 shell32 借来的。
            tray = new NotifyIcon();
            tray.Icon = ShellIcon.AppIcon(true);
            tray.Text = "TabbedExplorer（接 Win+E）";
            tray.Visible = true;

            MenuItem miShow = new MenuItem("打开窗口（Win+E）", delegate { OnWinE(); });
            MenuItem miSave = new MenuItem("记住当前标签", delegate { RememberNow(); });
            MenuItem miQuit = new MenuItem("退出", delegate { Quit("托盘菜单"); });

            // 设置子菜单跟齿轮那份**同一份内容**（SettingsMenu 里生成），别各写一遍 ——
            // 川报的「托盘右键没有设置选项」就是两边各写一遍漏出来的。
            traySettings = SettingsMenu.BuildTraySettings(this);

            trayMenu = new ContextMenu(new MenuItem[]
            {
                miShow, miSave, traySettings.Root, new MenuItem("-"), miQuit
            });
            // 自绘：勾选列独立（跟同级项左对齐）+ 深色下也看得见勾（川报的「没和其它选项一样居左对齐」）
            MenuFx.Hook(trayMenu);
            tray.ContextMenu = trayMenu;
            tray.DoubleClick += delegate { OnWinE(); };
        }

        /// <summary>设置变了：把托盘菜单里带勾选前缀的文字重刷一遍（菜单结构不动）。</summary>
        private void RefreshTrayMenu()
        {
            try { if (traySettings != null) traySettings.Refresh(); }
            catch (Exception ex) { Diag.Log("Hub: 刷托盘菜单失败 " + ex.Message); }
        }

        private void SetupHook()
        {
            hook = new WinEHook();
            hook.WinE += delegate { Post(OnWinE); };
            hook.NewTabKey += delegate { Post(delegate { Hotkey("Ctrl+T"); }); };
            hook.CloseTabKey += delegate { Post(delegate { Hotkey("Ctrl+W"); }); };
            hook.NextTabKey += delegate { Post(delegate { Hotkey("Ctrl+Tab"); }); };
            hook.PrevTabKey += delegate { Post(delegate { Hotkey("Ctrl+Shift+Tab"); }); };
            // 2026-09-22 新加的三个（历史 / 恢复关闭 / 收藏夹栏），跟浏览器对齐
            hook.HistoryKey += delegate { Post(delegate { Hotkey("Ctrl+H"); }); };
            hook.ReopenTabKey += delegate { Post(delegate { Hotkey("Ctrl+Shift+T"); }); };
            hook.FavBarKey += delegate { Post(delegate { Hotkey("Ctrl+Shift+B"); }); };
            // Ctrl+1..9 = 跳到第 N 个标签（参数是 0 基）
            hook.GotoTabKey += delegate(int i)
            {
                int n = i;
                Post(delegate { Hotkey("Ctrl+" + (n + 1)); });
            };
            hook.Start();

            // 第二个实例被启动（双击 exe）时只会 set 一下这个事件，由我们现身。            // 事件挂在字段上、不能 using 掉 —— 注册等待之后句柄要一直活着。
            if (Program.ShowSignal != null)
            {
                try
                {
                    sigWait = ThreadPool.RegisterWaitForSingleObject(Program.ShowSignal,
                        delegate { Post(OnWinE); }, null, -1, false);
                }
                catch (Exception ex) { Diag.Log("Hub: 注册唤醒事件失败 " + ex.Message); }
            }

            // `--quit`：命令行版「退出」，真退（先存记忆、再把各标签里的 explorer 收干净）。
            try
            {
                quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.QuitSignalName);
                quitWait = ThreadPool.RegisterWaitForSingleObject(quitEvent,
                    delegate { Post(delegate { Quit("--quit"); }); }, null, -1, false);
            }
            catch (Exception ex) { Diag.Log("Hub: 注册退出事件失败 " + ex.Message); }
        }

        // ==================================================================
        // 捕获「所有」打开的文件夹（Bug 1）
        //
        // 川的原话：「只捕获了 Win+E 这个按键，而不是所有资源管理器打开的文件夹
        // （比如从开始菜单、从桌面打开的）」。也就是**只要是资源管理器打开了一个文件夹，就该变成
        // 我们窗口里的一个标签** —— 跟浏览器一样，新窗口都归到标签里去。
        //
        // 做法：听系统的「某某窗口刚被显示」广播（跟防闪用的是同一个 WinShowWatcher），
        // 认出**新出现的** CabinetWClass 顶层窗口 → 交给当前桌面那个窗口收成标签
        // （`ExplorerHost.Adopt`：不起新进程，直接把现成的窗口收进来，所以不会闪也没延迟）。
        // ==================================================================

        private void SetupCapture()
        {
            EmbedApi.SnapshotBaseline();          // 先记下「现在就已经开着的」，这些不算新开

            // 候选窗口先搁一下再收 —— 见 TryCapture 里那段说明。
            captureTimer = new System.Windows.Forms.Timer();
            captureTimer.Interval = 250;
            captureTimer.Tick += delegate { DrainCapture(); };

            captureWatch = new WinShowWatcher();
            captureWatch.WindowShown += delegate(IntPtr h)
            {
                // 事件在 UI 线程上（OUTOFCONTEXT 投递到注册它的线程），Post 只是再保险一层
                Post(delegate { TryCapture(h); });
            };
            captureWatch.Start();
            Diag.Step("Hub: 已开始监听「新打开的文件夹窗口」（捕获所有打开的文件夹，延迟 " + CaptureDelayMs + "ms 接收）");
        }

        /// <summary>
        /// 看见一个可能是「川新开的文件夹窗口」的显示事件 —— 先**登记，不当场收**。
        ///
        /// 为什么要拖一下（2026-09-22 实测踩到）：我们给标签起 explorer 时，那个窗口也是「新出现的」，
        /// 而 Hub 这边跟标签那边的监听是**两个独立的钩子**，系统先叫谁不保证。
        /// 曾经直接就地收，日志里就出现过「我们自己起的第5个窗口被 Hub 当成川新开的抓走了」——
        /// 一旦这样，那个标签会去抢别人的窗口，最后就是一个标签空着（正是川报的「有时候标签页点开是空的」）。
        ///
        /// 拖这几百毫秒之后，情况就很干净：
        ///   · 是我们自己起的窗口 → 那个标签的 25ms 轮询早就把它**认领**了，这里一看已认领就放手；
        ///   · 是川自己开的窗口 → 没人认领，到点就收。
        /// 代价是川自己双击打开的文件夹会先露一小会儿（几百毫秒）才并进标签里，这个可以接受。
        /// </summary>
        private void TryCapture(IntPtr h)
        {
            if (quitting || h == IntPtr.Zero) return;
            if (!Settings.CaptureAll) return;
            if (!IsCapturable(h)) return;
            if (pendingCapture.ContainsKey(h)) return;

            // ★ 发现就**立刻藏起来**（川 2026-09-22 报「从桌面或开始菜单打开文件夹，还是会闪一下」）。
            //   原来这里只登记、等 700ms 才去收 —— 这 700ms 里那个原生窗口就明晃晃地摆在屏幕上，
            //   用户看到的就是「闪一下，然后被吸进我们窗口」。Win+E 那条路之所以不闪，
            //   就是因为标签那边（ExplorerHost.OnAnyWindowShown）一发现就先 SW_HIDE。这里补齐同一套。
            //   藏了冒充自己的窗口怎么办？不用担心：粗筛已经排除了「我们已经认领的」和「启动基线」，
            //   而且真要是我们自己起的那一个，标签那边本来也会把它藏起来。
            if (EmbedApi.IsWindowVisible(h))
            {
                EmbedApi.ShowWindow(h, EmbedApi.SW_HIDE);
                hiddenByUs.Add(h);
                Diag.Step(string.Format("Hub: 发现新窗口，先藏起来（别让它闪）cab=0x{0:X}", h.ToInt64()));
            }
            pendingCapture[h] = DateTime.Now;
            if (captureTimer != null && !captureTimer.Enabled) captureTimer.Start();
        }

        /// <summary>这个窗口现在看起来值不值得收（粗筛，真正的收在 AdoptWindow 里再判一次）。</summary>
        private bool IsCapturable(IntPtr h)
        {
            string c = EmbedApi.ClassOf(h);
            if (c != "CabinetWClass" && c != "ExploreWClass") return false;
            if (EmbedApi.IsClaimed(h)) return false;          // 我们自己起的 / 已经被某个标签收了
            if (EmbedApi.IsBaseline(h)) return false;         // 启动前就在那儿的老窗口（还原、重新显示不算新开）
            // 藏着的多半是我们还没嵌好的 —— 但**我们自己藏起来的那批要认**（防闪就是把它们藏了）
            if (!EmbedApi.IsWindowVisible(h) && !hiddenByUs.Contains(h)) return false;
            if (EmbedApi.GetParent(h) != IntPtr.Zero) return false;   // 已经是子窗口 → 被谁嵌走了
            int pid = EmbedApi.ProcessIdOf(h).ToInt32();
            if (pid == 0) return false;
            if (pid == Process.GetCurrentProcess().Id) return false;
            return true;
        }

        /// <summary>到点的那批 —— 还活着、还没被认领的，收；其余的丢掉。</summary>
        private void DrainCapture()
        {
            if (pendingCapture.Count == 0) { captureTimer.Stop(); return; }
            DateTime now = DateTime.Now;
            List<IntPtr> ready = null;
            foreach (KeyValuePair<IntPtr, DateTime> kv in new List<KeyValuePair<IntPtr, DateTime>>(pendingCapture))
            {
                if ((now - kv.Value).TotalMilliseconds < CaptureDelayMs) continue;
                if (ready == null) ready = new List<IntPtr>();
                ready.Add(kv.Key);
            }
            if (ready == null) return;
            foreach (IntPtr h in ready)
            {
                pendingCapture.Remove(h);
                AdoptWindow(h);
            }
        }

        /// <summary>真收：把这个窗口接成当前（它所在那）桌面窗口里的一个新标签。</summary>
        private void AdoptWindow(IntPtr h)
        {
            if (quitting || !Settings.CaptureAll) { ReleaseIfAbandoned(h); return; }
            try
            {
                if (!IsCapturable(h)) { ReleaseIfAbandoned(h); return; }   // 这一轮里可能已经变了（被认领 / 关掉 / 藏了）
                int pid = EmbedApi.ProcessIdOf(h).ToInt32();
                Diag.Step(string.Format("Hub: 收下这个新开的文件夹窗口 cab=0x{0:X} pid={1}", h.ToInt64(), pid));

                Guid d = VirtualDesktop.WindowDesktopId(h);
                if (d == Guid.Empty) d = VirtualDesktop.CurrentDesktopId();
                EmbedForm f = EnsureForm(d);
                if (f == null) { ReleaseIfAbandoned(h); return; }

                if (!f.NewAdoptedTab(h, pid))
                {
                    Diag.Step("Hub: 这个窗口没能收进来（已经收过了 / 失败），保持原样");
                    ReleaseIfAbandoned(h);
                    return;
                }
                hiddenByUs.Remove(h);      // 归标签了，后面由标签负责显示 / 关闭
                f.ShowForCapture();
            }
            catch (Exception ex) { Diag.Log("Hub: 捕获新窗口失败 " + ex.Message); }
        }

        /// <summary>
        /// 我们把它藏过、结果没收成（被别的标签认领了 / 关了 / 收编失败）—— 不能就这么丢着：
        /// 一个被我们藏起来的窗口在用户眼里就是「窗口没了」的死东西。
        /// 已经把窗口收进某个标签的（`IsClaimed`）保持藏着，那个标签自己会显示它、关它。
        /// </summary>
        private void ReleaseIfAbandoned(IntPtr h)
        {
            if (!hiddenByUs.Remove(h)) return;
            try
            {
                if (EmbedApi.IsClaimed(h)) return;                  // 归标签了，标签管
                if (!NativeMethods.IsWindow(h)) return;             // 已经关了
                if (EmbedApi.GetParent(h) != IntPtr.Zero) return;    // 已经被嵌走了
                Diag.Step(string.Format("Hub: 这个窗口没收成，还它本来面目 cab=0x{0:X}", h.ToInt64()));
                EmbedApi.ShowWindow(h, EmbedApi.SW_SHOW);
            }
            catch (Exception ex) { Diag.Log("Hub: 恢复窗口失败 " + ex.Message); }
        }

        /// <summary>把动作丢回 UI 线程（钩子 / 线程池的回调都在别的线程上）。</summary>
        private void Post(Action a)
        {
            if (a == null || quitting) return;
            try { syncTarget.BeginInvoke(a); } catch { }
        }

        /// <summary>
        /// 弹一条提示。
        ///
        /// ⚠ 2026-09-22 改了实现（川报「显示提醒的背景和字体颜色没适配颜色模式」）：
        /// 原来走 `NotifyIcon.ShowBalloonTip`，那个气泡是**系统画的**，配色跟系统主题走，
        /// 我们强制浅色/深色时它不认 —— 外壳和气泡两套皮。现在换成自己画的 `Toast`。
        /// 方法签名留着不动，调用点一个都不用改。
        /// </summary>
        public void Notify(string title, string text, bool once)
        {
            try
            {
                if (once && trayTipShown) return;
                trayTipShown = true;
                Toast.Show(title, text);
            }
            catch { }
        }

        // ==================================================================
        // Win+E：找到「当前这张桌面」的窗口
        // ==================================================================

        public void OnWinE()
        {
            if (quitting) return;
            Guid d = VirtualDesktop.CurrentDesktopId();
            Diag.Step("Hub: Win+E，当前桌面=" + Short(d));
            EmbedForm f = EnsureForm(d);
            if (f == null) return;
            f.ShowForUser(true);
        }

        /// <summary>
        /// 当前桌面的窗口。两种模式两条路：
        ///
        /// **按虚拟桌面分别捕获**（perdesktop，默认）：① 先按「窗口实际挂在哪张桌面」认领
        /// （川用 Win+Ctrl+Shift+方向键把窗口挪到别的桌面之后，这样能自愈）；② 再按登记表；
        /// ③ 都没有就新建一个。新建的窗口天然落在当前桌面 —— 所以永远不需要「搬窗口」。
        ///
        /// **捕获并迁移到当前桌面**（migrate，v1.0.0 那套）：全进程就应该只有一个窗口，
        /// 不管现在在哪张桌面 —— 直接拿它，搬桌面的事交给 EmbedForm（ShowForUser 里做）。
        /// </summary>
        private EmbedForm EnsureForm(Guid desktop)
        {
            Prune();

            if (Settings.Capture == Settings.CaptureMode.Migrate)
            {
                EmbedForm only;
                if (forms.TryGetValue(SingleKey, out only) && only != null && !only.IsDisposed) return only;
                // 刚从严桌面模式换过来、手上已经有窗口：随便认一个当「唯一那个」，别又多开一个
                foreach (EmbedForm f in new List<EmbedForm>(forms.Values))
                {
                    if (f == null || f.IsDisposed) continue;
                    return Adopt(f, SingleKey);
                }
                Diag.Step("Hub: 迁移模式，建唯一窗口");
                return NewForm(SingleKey);
            }

            string key = KeyOf(desktop);

            if (desktop != Guid.Empty)
            {
                foreach (EmbedForm f in new List<EmbedForm>(forms.Values))
                {
                    if (f == null || f.IsDisposed) continue;
                    Guid g = VirtualDesktop.WindowDesktopId(f.Handle);
                    if (g != Guid.Empty && g == desktop) return Adopt(f, key);
                }
            }

            EmbedForm hit;
            if (forms.TryGetValue(key, out hit) && hit != null && !hit.IsDisposed) return hit;

            Diag.Step(string.Format("Hub: 给桌面 {0} 建窗口（现有 {1} 个）", key, forms.Count));
            return NewForm(key);
        }

        private EmbedForm NewForm(string key)
        {
            EmbedForm nf = new EmbedForm(this, key);
            forms[key] = nf;
            nf.FormClosed += delegate { OnFormGone(nf); };
            hook.MainWindow = nf.Handle;      // OursIsForeground 的兜底分支要用
            nf.SetFavBarOn(Settings.FavBar);  // 新窗口要跟上当前的收藏夹栏开关（设置存在文件里，窗口自己不知道）
            return nf;
        }

        /// <summary>窗口被挪到别的桌面（或换了捕获模式）：登记改到新的键，**标签跟着窗口走**。</summary>
        private EmbedForm Adopt(EmbedForm f, string key)
        {
            if (string.Equals(f.DesktopKey, key, StringComparison.OrdinalIgnoreCase)) return f;
            Diag.Step("Hub: 窗口改登记 " + f.DesktopKey + " -> " + key);
            if (forms.ContainsKey(f.DesktopKey) && forms[f.DesktopKey] == f) forms.Remove(f.DesktopKey);
            f.DesktopKey = key;
            forms[key] = f;
            MarkDirty();
            return f;
        }

        // ==================================================================
        // 捕获方式（设置菜单第一项）
        // ==================================================================

        /// <summary>当前的标签捕获方式（EmbedForm 也要看，决定 Win+E 时搬不搬窗口）。</summary>
        public Settings.CaptureMode Capture { get { return Settings.Capture; } }

        /// <summary>
        /// 换捕获方式 —— 立刻生效，并且**把记忆搬个家**，免得川切一下发现标签「没了」：
        ///   - 切到迁移模式：把当前桌面那一套搬进 `single`（`single` 已有内容就不动）；
        ///     只留前台那个窗口，其余收掉 —— 它们的标签已经各自落进自己桌面的桶里。
        ///   - 切回分桌面模式：把 `single` 搬进当前桌面那张的桶（那张已有内容就不动），
        ///     窗口也从 `single` 改登记到当前桌面。
        /// </summary>
        public void SetCaptureMode(Settings.CaptureMode m)
        {
            if (Settings.Capture == m) return;
            Diag.Step("Hub: 捕获方式 " + Settings.Text(Settings.Capture) + " -> " + Settings.Text(m));
            Settings.SetCapture(m);

            EmbedForm keep = ForegroundForm();
            if (keep == null || keep.IsDisposed)
            {
                foreach (EmbedForm f in new List<EmbedForm>(forms.Values))
                {
                    if (f != null && !f.IsDisposed) { keep = f; break; }
                }
            }

            Guid cur = VirtualDesktop.CurrentDesktopId();
            string dk = KeyOf(cur);

            if (m == Settings.CaptureMode.Migrate)
            {
                foreach (EmbedForm f in new List<EmbedForm>(forms.Values))
                {
                    if (f == null || f.IsDisposed || f == keep) continue;
                    Diag.Step("Hub: 迁移模式，收掉多余窗口 " + f.DesktopKey);
                    try { f.Quitting = true; f.Close(); }
                    catch (Exception ex) { Diag.Log("Hub: 收窗口失败 " + ex.Message); }
                }
                forms.Clear();

                if (keep != null && !keep.IsDisposed)
                {
                    MoveMemory(keep.DesktopKey, SingleKey);
                    keep.DesktopKey = SingleKey;
                    forms[SingleKey] = keep;
                    hook.MainWindow = keep.Handle;
                }
            }
            else if (keep != null && !keep.IsDisposed)
            {
                MoveMemory(SingleKey, dk);
                forms.Remove(SingleKey);
                keep.DesktopKey = dk;
                forms[dk] = keep;
            }

            MarkDirty();
            RefreshTrayMenu();
        }

        // ==================================================================
        // 其余设置（颜色模式 / 保留标签 / 标签宽度 / 自适应）
        // ==================================================================

        /// <summary>颜色模式：跟随系统 / 浅色 / 深色。改完立刻重算调色板并让各窗口重刷。</summary>
        public void SetColorMode(Settings.ColorMode m)
        {
            if (Settings.Color == m) return;
            Settings.SetColor(m);
            Theme.SetMode(m);        // RaiseChanged -> 各窗口自己重刷（含 explorer 的逐窗口主题）
            RefreshTrayMenu();
            // 不再弹提示（川：非重要变更不用右下角弹窗）。
            // ⚠ 仍然要记住的限制：嵌进来的 explorer 是**独立进程**，它那块文件列表按系统主题画，
            //   我们只能逐窗口 SetWindowTheme 尽力而为。这一条写在设置窗口的说明里。
        }

        /// <summary>是否保留标签页。关掉 = 不还原记忆，每次打开都是全新一个「此电脑」。</summary>
        public void SetKeepTabs(bool on)
        {
            if (Settings.KeepTabs == on) return;
            Settings.SetKeepTabs(on);
            RefreshTrayMenu();
        }

        /// <summary>标签页宽度（逻辑像素）。立刻重排所有标签条。</summary>
        public void SetTabWidth(int w)
        {
            int v = Settings.ClampWidth(w);
            if (Settings.TabWidth == v) return;
            Settings.SetTabWidth(v);
            RefreshTrayMenu();
            RetabAll();
        }

        /// <summary>自适应宽度①：文件夹名过长时自动加宽（川 2026-09-22 把这个和「缩窄」拆开了）。</summary>
        public void SetTabAutoWiden(bool on)
        {
            if (Settings.TabAutoWiden == on) return;
            Settings.SetTabAutoWiden(on);
            RefreshTrayMenu();
            RetabAll();
        }

        /// <summary>自适应宽度②：挤不下时是否自动缩窄。</summary>
        public void SetTabAutoFit(bool on)
        {
            if (Settings.TabAutoFit == on) return;
            Settings.SetTabAutoFit(on);
            RefreshTrayMenu();
            RetabAll();
        }

        private void RetabAll()
        {
            foreach (EmbedForm f in new List<EmbedForm>(forms.Values))
            {
                if (f == null || f.IsDisposed) continue;
                f.RefreshTabs();
            }
        }

        /// <summary>
        /// 收藏夹栏开关（Ctrl+Shift+B）。**所有入口最终都汇到这儿**：热键、标签条上的按钮、
        /// 设置菜单里那一项、标签条空白右键、收藏夹栏自己的右键 —— 免得像当初「托盘漏了设置项」那样漏一边。
        /// </summary>
        public void SetFavBar(bool on)
        {
            if (Settings.FavBar == on) return;
            Settings.SetFavBar(on);
            RefreshTrayMenu();
            FavBarAll(on);
        }

        private void FavBarAll(bool on)
        {
            foreach (EmbedForm f in new List<EmbedForm>(forms.Values))
            {
                if (f == null || f.IsDisposed) continue;
                f.SetFavBarOn(on);
            }
        }

        /// <summary>
        /// 是否捕获**所有**打开的文件夹（Bug 1 要的那个行为）。
        /// 监听一直是装着的（见 SetupCapture），这里只是改一个开关 —— `TryCapture` 每次都会看它，
        /// 所以开关是立刻生效的，不用装/卸钩子。
        /// </summary>
        public void SetCaptureAll(bool on)
        {
            if (Settings.CaptureAll == on) return;
            Settings.SetCaptureAll(on);
            RefreshTrayMenu();
        }

        /// <summary>
        /// 开机自启（川 2026-09-22 要的设置项）。
        ///
        /// 实现的**唯一真相在注册表**：`HKCU\...\Run` 里的 `TabbedExplorer` 值（见 AutoStart），
        /// 不往 settings.json 里再存一份 —— 两份状态一旦对不上（川自己用任务管理器禁用了启动项），
        /// 菜单里打的勾就是假的。所以这个开关没有 `Settings.XXX` 字段，每次都现问注册表。
        ///
        /// 启动方式带 `--tray`：只驻留托盘 + 装 Win+E 钩子，**不弹窗口**。
        /// </summary>
        public void SetAutoStart(bool on)
        {
            bool ok = AutoStart.Set(on);
            RefreshTrayMenu();
            if (!ok)
            {
                // 改不动是「出错」，得说一声；成功就不弹了（川：非重要变更不用弹窗）
                Notify("开机自启", "改不了启动项（注册表写不进去），还是原样。", false);
                return;
            }
            Diag.Step("Hub: 开机自启 -> " + (on ? "开" : "关"));
        }

        /// <summary>把 from 桶的内容搬进 to 桶 —— **只在 to 还空着的时候**搬，不覆盖已记过的。</summary>
        private void MoveMemory(string from, string to)
        {
            if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to)) return;
            if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return;
            DesktopMemory.Bucket toB = memory.Find(to);
            if (toB != null && toB.Paths.Count > 0) return;
            DesktopMemory.Bucket fromB = memory.Find(from);
            if (fromB == null || fromB.Paths.Count == 0) return;

            DesktopMemory.Bucket dst = memory.Ensure(to);
            dst.Paths.Clear();
            dst.Paths.AddRange(fromB.Paths);
            dst.Active = fromB.Active;
            Diag.Step(string.Format("Hub: 记忆搬家 {0} -> {1}（{2} 个标签）", from, to, dst.Paths.Count));
        }

        /// <summary>「记住当前标签」：立刻把各桌面的标签写盘（托盘菜单和设置窗口里都有）。
        /// 平时是攒 800ms 自动写 + 退出前再写，这个按钮就是「现在立刻写一次」。</summary>
        public void RememberNow()
        {
            try { saveTimer.Stop(); } catch { }
            SaveNow("手动");
        }

        /// <summary>Ctrl+T / Ctrl+W / Ctrl+Tab：派给「前台那个窗口」。
        /// 钩子只在「我们进程是前台」时才拦这些键，所以这里找得到就一定是自己人。</summary>
        private void Hotkey(string what)
        {
            EmbedForm f = ForegroundForm();
            if (f == null) { Diag.Step("Hub: " + what + " 但没有我们的前台窗口，忽略"); return; }
            f.HandleHotkey(what);
        }

        private EmbedForm ForegroundForm()
        {
            IntPtr fg = NativeMethods.GetForegroundWindow();
            if (fg == IntPtr.Zero) return null;
            IntPtr root = NativeMethods.GetAncestor(fg, 2);   // GA_ROOT
            foreach (EmbedForm f in new List<EmbedForm>(forms.Values))
            {
                if (f == null || f.IsDisposed) continue;
                IntPtr h = f.Handle;
                if (h == fg || (root != IntPtr.Zero && root == h)) return f;
            }
            return null;
        }

        // ==================================================================
        // 记忆
        // ==================================================================

        public DesktopMemory.Bucket MemoryOf(string key)
        {
            return memory.Find(key);
        }

        /// <summary>标签有变动（开/关/导航/切换）—— 攒 800ms 再写盘，免得一路点进去写十几次。</summary>
        public void MarkDirty()
        {
            if (quitting) return;
            try { saveTimer.Stop(); saveTimer.Start(); } catch { }
        }

        /// <summary>
        /// 把每个**活着的**窗口现在的标签写进它那张桌面的桶里，然后落盘。
        ///
        /// 只覆盖「有窗口在的桌面」—— 这一轮没碰过的桌面（比如川今天没去 Game 桌面）
        /// 读进来什么样就写回去什么样，不会被清空。
        /// 另外**不动已经关掉的窗口**的桶：那次会话最后长什么样就留着什么样的标签，下次还在。
        /// </summary>
        public void SaveNow(string why)
        {
            try
            {
                int live = 0, tabs = 0;
                foreach (EmbedForm f in new List<EmbedForm>(forms.Values))
                {
                    if (f == null || f.IsDisposed) continue;
                    DesktopMemory.Bucket b = memory.Ensure(f.DesktopKey);
                    b.Paths.Clear();
                    b.Active = null;

                    string active = f.ActiveTabPath;
                    foreach (string p in f.TabPaths())
                    {
                        if (string.IsNullOrEmpty(p)) continue;
                        b.Paths.Add(p);
                        if (PathRules.Same(p, active)) b.Active = p;
                        tabs++;
                    }
                    live++;
                }
                memory.Save();
                Diag.Step(string.Format("Hub: 记忆已保存（{0}）：{1} 个窗口 / {2} 个标签 / 共记着 {3} 张桌面",
                    why, live, tabs, memory.Count));
            }
            catch (Exception ex) { Diag.Log("Hub: 保存记忆失败 " + ex.Message); }
        }

        /// <summary>真退出。先把记忆落盘，再让每个窗口把标签里的 explorer 收干净。</summary>
        public void Quit(string why)
        {
            if (quitting) return;
            quitting = true;
            Diag.Step("Hub: 退出（" + why + "）");
            try { saveTimer.Stop(); } catch { }
            SaveNow("退出前");

            List<EmbedForm> all = new List<EmbedForm>(forms.Values);
            forms.Clear();
            foreach (EmbedForm f in all)
            {
                try { f.Quitting = true; f.Close(); }
                catch (Exception ex) { Diag.Log("Hub: 关窗口失败 " + ex.Message); }
            }
            ExitThread();
        }

        private void OnFormGone(EmbedForm f)
        {
            foreach (string k in new List<string>(forms.Keys))
            {
                if (forms[k] == f) forms.Remove(k);
            }
            Diag.Step("Hub: 窗口没了，还剩 " + forms.Count + " 个");
            MarkDirty();
        }

        private void Prune()
        {
            foreach (string k in new List<string>(forms.Keys))
            {
                EmbedForm f = forms[k];
                if (f == null || f.IsDisposed) forms.Remove(k);
            }
        }

        private static string KeyOf(Guid g)
        {
            return g == Guid.Empty ? "unknown" : g.ToString("D");
        }

        private static string Short(Guid g)
        {
            return g == Guid.Empty ? "(问不出)" : g.ToString().Substring(0, 8);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposed) { base.Dispose(disposing); return; }
            disposed = true;
            Diag.Step("Hub: Dispose");

            try { saveTimer.Dispose(); } catch { }
            if (sigWait != null) { try { sigWait.Unregister(null); } catch { } sigWait = null; }
            if (quitWait != null) { try { quitWait.Unregister(null); } catch { } quitWait = null; }
            if (quitEvent != null) { try { quitEvent.Close(); } catch { } quitEvent = null; }
            if (captureWatch != null) { try { captureWatch.Dispose(); } catch { } captureWatch = null; }
            if (captureTimer != null) { try { captureTimer.Dispose(); } catch { } captureTimer = null; }
            if (hook != null) { try { hook.Dispose(); } catch { } hook = null; }
            if (tray != null)
            {
                try { tray.Visible = false; tray.Dispose(); } catch { }
                tray = null;
            }
            try { syncTarget.Dispose(); } catch { }

            base.Dispose(disposing);
        }
    }
}
