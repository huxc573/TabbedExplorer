using System;
using System.Collections.Generic;
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

        /// <summary>全局开关（目前只有捕获方式）。</summary>
        private readonly Settings settings = new Settings();

        /// <summary>迁移模式（v1.0.0）下，全进程唯一那个窗口在登记表里的键。</summary>
        private const string SingleKey = "single";

        /// <summary>钩子回调在别的线程上，动界面之前得先转回 UI 线程 —— 就是它。</summary>
        private readonly Control syncTarget = new Control();

        /// <summary>记忆不急着每改一下就写盘，攒一下再写（导航一次会连改好几项）。</summary>
        private readonly System.Windows.Forms.Timer saveTimer = new System.Windows.Forms.Timer();

        private NotifyIcon tray;
        private WinEHook hook;
        private RegisteredWaitHandle sigWait;
        private RegisteredWaitHandle quitWait;
        private EventWaitHandle quitEvent;
        private bool quitting;
        private bool trayTipShown;
        private bool disposed;

        public DesktopHub()
        {
            settings.Load();
            memory.Load();

            saveTimer.Interval = 800;
            saveTimer.Tick += delegate { saveTimer.Stop(); SaveNow("攒够了一下"); };

            IntPtr h = syncTarget.Handle;   // 逼出句柄，之后才收得到 BeginInvoke
            GC.KeepAlive(h);

            SetupTray();
            SetupHook();

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

            MenuItem miShow = new MenuItem("打开窗口（Win+E）");
            miShow.Click += delegate { OnWinE(); };
            MenuItem miSave = new MenuItem("记住当前标签");
            miSave.Click += delegate { RememberNow(); };
            MenuItem miQuit = new MenuItem("退出");
            miQuit.Click += delegate { Quit("托盘菜单"); };

            tray.ContextMenu = new ContextMenu(new MenuItem[] { miShow, miSave, miQuit });
            tray.DoubleClick += delegate { OnWinE(); };
        }

        private void SetupHook()
        {
            hook = new WinEHook();
            hook.WinE += delegate { Post(OnWinE); };
            hook.NewTabKey += delegate { Post(delegate { Hotkey("Ctrl+T"); }); };
            hook.CloseTabKey += delegate { Post(delegate { Hotkey("Ctrl+W"); }); };
            hook.NextTabKey += delegate { Post(delegate { Hotkey("Ctrl+Tab"); }); };
            hook.PrevTabKey += delegate { Post(delegate { Hotkey("Ctrl+Shift+Tab"); }); };
            hook.Start();

            // 第二个实例被启动（双击 exe）时只会 set 一下这个事件，由我们现身。
            // 事件挂在字段上、不能 using 掉 —— 注册等待之后句柄要一直活着。
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

        /// <summary>把动作丢回 UI 线程（钩子 / 线程池的回调都在别的线程上）。</summary>
        private void Post(Action a)
        {
            if (a == null || quitting) return;
            try { syncTarget.BeginInvoke(a); } catch { }
        }

        public void Notify(string title, string text, bool once)
        {
            if (tray == null) return;
            try
            {
                if (once && trayTipShown) return;
                trayTipShown = true;
                tray.BalloonTipTitle = title;
                tray.BalloonTipText = text;
                tray.ShowBalloonTip(4000);
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

            if (settings.Capture == Settings.CaptureMode.Migrate)
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
        public Settings.CaptureMode Capture { get { return settings.Capture; } }

        /// <summary>
        /// 换捕获方式 —— 立刻生效，并且**把记忆搬个家**，免得川切一下发现标签「没了」：
        ///   - 切到迁移模式：把当前桌面那一套搬进 `single`（`single` 已有内容就不动）；
        ///     只留前台那个窗口，其余收掉 —— 它们的标签已经各自落进自己桌面的桶里。
        ///   - 切回分桌面模式：把 `single` 搬进当前桌面那张的桶（那张已有内容就不动），
        ///     窗口也从 `single` 改登记到当前桌面。
        /// </summary>
        public void SetCaptureMode(Settings.CaptureMode m)
        {
            if (settings.Capture == m) return;
            Diag.Step("Hub: 捕获方式 " + Settings.Text(settings.Capture) + " -> " + Settings.Text(m));
            settings.Capture = m;
            settings.Save();

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
            Notify("捕获方式已换", Settings.Label(m) + (m == Settings.CaptureMode.Migrate
                ? "：全进程只一个窗口，Win+E 时搬到当前桌面。"
                : "：每张虚拟桌面各一个窗口、各记一套标签。"), false);
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

        /// <summary>「记住当前标签」：立刻把各桌面的标签写盘（托盘菜单和窗口里的设置菜单都走它）。</summary>
        public void RememberNow()
        {
            try { saveTimer.Stop(); } catch { }
            SaveNow("手动");
            Notify("已记住", "各虚拟桌面的标签已写进 desktops.txt。", false);
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
