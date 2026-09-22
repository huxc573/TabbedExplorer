using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace TabbedExplorer
{
    /// <summary>
    /// 「嵌入真 explorer 窗口」模式的窗口。
    /// 和 MainForm 那套（自绘外壳 + IExplorerBrowser）完全独立，靠 --embed 启动。
    ///
    /// 这里我们只负责：顶部一条自绘标题栏 + 一条标签条 + 一个容器。
    /// Ribbon、地址栏、导航窗格、文件列表、状态栏**全部是 explorer 自己的**，
    /// 所以外观和系统资源管理器一模一样。
    ///
    /// 常驻后台：这个窗口是「Win+E 的落点」，所以**关掉它不等于退出程序** ——
    /// 点 X、关掉最后一个标签都只是收进托盘，进程留着接 Win+E；
    /// 真正退出走托盘右键「退出」。
    /// </summary>
    internal sealed class EmbedForm : Form
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

        private const int WM_NCHITTEST = 0x0084;
        private const int WM_GETMINMAXINFO = 0x0024;
        private const int HTCLIENT = 1;
        private const int HTLEFT = 10;
        private const int HTRIGHT = 11;
        private const int HTTOP = 12;
        private const int HTTOPLEFT = 13;
        private const int HTTOPRIGHT = 14;
        private const int HTBOTTOM = 15;
        private const int HTBOTTOMLEFT = 16;
        private const int HTBOTTOMRIGHT = 17;

        private readonly TitleBar titleBar;
        private readonly TabStrip tabStrip;
        private readonly Panel content;
        private readonly Label status;
        private readonly List<ExplorerHost> hosts = new List<ExplorerHost>();
        private int activeIndex = -1;

        /// <summary>自己做的窗口边框厚度（只在不最大化时有）。</summary>
        private readonly int ResizeBorder;
        private bool inLayout;

        // ---- 常驻后台 ----
        private NotifyIcon tray;
        private WinEHook hook;
        private RegisteredWaitHandle sigWait;
        private EventWaitHandle quitEvent;      // `--quit` 的信号（真退出，区别于点 X 的收托盘）
        private RegisteredWaitHandle quitWait;
        private bool quitting;          // 真退出中（托盘「退出」/系统关机），别再拦关闭
        private bool trayTipShown;

        public EmbedForm()
        {
            Text = "此电脑";
            BackColor = Theme.RibbonBack;   // 无边框后，四周那圈就是这个色，当边框用
            Size = new Size(Px(1200), Px(760));
            MinimumSize = new Size(Px(640), Px(420));
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;

            // 无边框 + 自绘标题栏。
            // 嵌入进来的 explorer 是子窗口、没有标题栏，而它的快速访问工具栏恰好画在
            // 「标题栏」那一条里 —— 那条丢了就再也拿不回来（实测加回 WS_CAPTION 无效）。
            FormBorderStyle = FormBorderStyle.None;
            ResizeBorder = Px(4);
            SyncChrome();

            titleBar = new TitleBar();
            titleBar.QatClicked += OnQatClicked;
            titleBar.WindowButtonClicked += OnWindowButtonClicked;

            tabStrip = new TabStrip();
            tabStrip.TabClicked += delegate(object s, int i) { Activate(i); };
            tabStrip.TabCloseClicked += delegate(object s, int i)
            {
                Diag.Step("EmbedForm: 点击标签关闭按钮 idx=" + i);
                CloseTab(i);
            };
            tabStrip.TabMiddleClicked += delegate(object s, int i)
            {
                Diag.Step("EmbedForm: 中键关闭标签 idx=" + i);
                CloseTab(i);
            };
            tabStrip.NewTabClicked += delegate
            {
                Diag.Step("EmbedForm: 点击新建标签");
                NewTab(CurrentPath());
            };

            content = new Panel();
            content.BackColor = Theme.Pane;

            status = new Label();
            status.Height = Px(22);
            status.TextAlign = ContentAlignment.MiddleLeft;
            status.Padding = new Padding(Px(8), 0, 0, 0);
            status.AutoSize = false;

            // 位置全部手算（DoLayout）：无边框窗口的四周留一圈自己的边框，
            // 顶部还要多一根自绘标题栏 —— 靠 Dock 拼不出这个形状。
            Controls.Add(titleBar);
            Controls.Add(tabStrip);
            Controls.Add(content);
            Controls.Add(status);

            ApplyTheme();
            Theme.Changed += delegate { ApplyTheme(); };
            DoLayout();

            // 常驻后台模式（--tray，开机自启走这条）：起来不建标签、不显窗口，
            // 等 Win+E 或双击 exe 才现身。
            if (!Program.StartHidden) NewTab(ExplorerView.ThisPcPath);
        }

        // ==================================================================
        // 无边框窗口的«窗口管理»：布局 / 边框 / 最大化
        // ==================================================================

        /// <summary>
        /// 手动布局：自绘标题栏 → 标签条 → 内容 → 状态栏。
        /// 全部摆在内边距（DisplayRectangle）里，四周那一圈（Padding）留给我们自己做可拖拽边框 ——
        /// 只有**没有被子控件盖住**的地方，窗体的 WM_NCHITTEST 才收得到。
        /// </summary>
        private void DoLayout()
        {
            if (inLayout || titleBar == null) return;
            inLayout = true;
            try
            {
                Rectangle r = DisplayRectangle;
                int hTitle = Px(30);
                int hTab = Px(TabStrip.StdHeight);
                int hStatus = Px(22);

                int top = r.Top;
                titleBar.SetBounds(r.Left, top, r.Width, hTitle);
                top += hTitle;
                tabStrip.SetBounds(r.Left, top, r.Width, hTab);
                top += hTab;
                status.SetBounds(r.Left, r.Bottom - hStatus, r.Width, hStatus);

                int hContent = r.Bottom - hStatus - top;
                if (hContent < 0) hContent = 0;
                content.SetBounds(r.Left, top, r.Width, hContent);
            }
            finally { inLayout = false; }
        }

        /// <summary>最大化时不留边框（那时也没法拖拽改大小，留着只会在屏幕边上多一条）。</summary>
        private void SyncChrome()
        {
            bool max = (WindowState == FormWindowState.Maximized);
            int b = max ? 0 : ResizeBorder;
            if (Padding.Left != b || Padding.Top != b || Padding.Right != b || Padding.Bottom != b)
            {
                Padding = new Padding(b);
                DoLayout();
            }
            if (titleBar != null) titleBar.Maximized = max;
        }

        /// <summary>
        /// 快速访问工具栏点了一下。
        /// 动作**不自己实现**：把对应的快捷键派给那个 explorer 线程正在用的窗口，行为跟原版一模一样。
        /// 快捷键按 Win10 资源管理器的实际键位来。
        /// </summary>
        private void OnQatClicked(TitleBar.Qat q)
        {
            if (activeIndex < 0 || activeIndex >= hosts.Count) return;
            ExplorerHost h = hosts[activeIndex];
            switch (q)
            {
                case TitleBar.Qat.Properties: h.SendCommand(EmbedApi.VK_RETURN, false, false, true); break; // Alt+Enter
                case TitleBar.Qat.NewFolder:  h.SendCommand(EmbedApi.VK_N, true, true, false); break;       // Ctrl+Shift+N
                case TitleBar.Qat.Undo:       h.SendCommand(EmbedApi.VK_Z, true, false, false); break;      // Ctrl+Z
                case TitleBar.Qat.Redo:       h.SendCommand(EmbedApi.VK_Y, true, false, false); break;      // Ctrl+Y
                case TitleBar.Qat.Delete:     h.SendCommand(EmbedApi.VK_DELETE, false, false, false); break; // Delete
                case TitleBar.Qat.Rename:     h.SendCommand(EmbedApi.VK_F2, false, false, false); break;    // F2
            }
            h.Focus();   // 点完工具栏，焦点交还给文件列表
        }

        private void OnWindowButtonClicked(TitleBar.WBtn b)
        {
            switch (b)
            {
                case TitleBar.WBtn.Minimize: WindowState = FormWindowState.Minimized; break;
                case TitleBar.WBtn.Maximize:
                    WindowState = (WindowState == FormWindowState.Maximized)
                        ? FormWindowState.Normal : FormWindowState.Maximized;
                    break;
                case TitleBar.WBtn.Close: HideToTray(); break;   // 收进托盘，不退进程
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            SyncChrome();
            DoLayout();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_GETMINMAXINFO)
            {
                base.WndProc(ref m);
                // 无边框窗口最大化默认按«整块屏»算，会把任务栏一起盖掉 —— 改成显示器工作区。
                EmbedApi.ClampMaxToMonitorWork(Handle, m.LParam);
                return;
            }
            if (m.Msg == WM_NCHITTEST && ResizeBorder > 0)
            {
                base.WndProc(ref m);
                if (m.Result.ToInt32() == HTCLIENT)
                {
                    // 屏幕坐标在 lParam 的低/高 16 位，按**有符号**取（多显示器会有负坐标）
                    long lp = m.LParam.ToInt64();
                    Point sp = new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
                    Point p = PointToClient(sp);
                    int b = (WindowState == FormWindowState.Normal) ? ResizeBorder : 0;
                    int w = ClientSize.Width, h = ClientSize.Height;
                    bool left = p.X < b, right = b > 0 && p.X >= w - b;
                    bool top = b > 0 && p.Y < b, bottom = b > 0 && p.Y >= h - b;
                    int hit = 0;
                    if (top && left) hit = HTTOPLEFT;
                    else if (top && right) hit = HTTOPRIGHT;
                    else if (bottom && left) hit = HTBOTTOMLEFT;
                    else if (bottom && right) hit = HTBOTTOMRIGHT;
                    else if (left) hit = HTLEFT;
                    else if (right) hit = HTRIGHT;
                    else if (top) hit = HTTOP;
                    else if (bottom) hit = HTBOTTOM;
                    if (hit != 0) m.Result = new IntPtr(hit);
                }
                return;
            }
            base.WndProc(ref m);
        }

        private void ApplyTheme()
        {
            BackColor = Theme.RibbonBack;
            content.BackColor = Theme.Pane;
            status.BackColor = Theme.StatusBack;
            status.ForeColor = Theme.TextDim;
            titleBar.BackColor = Theme.RibbonBack;
            titleBar.Invalidate();
            tabStrip.Invalidate();
        }

        // ==================================================================
        // 常驻后台：托盘 + Win+E 接管
        // ==================================================================

        /// <summary>把窗口句柄建出来（隐藏启动时也要，否则 BeginInvoke / 托盘都不好使）。</summary>
        public void ForceHandle()
        {
            IntPtr h = Handle;
            GC.KeepAlive(h);
        }

        private void SetupResident()
        {
            // 托盘图标就用「新建文件夹」那颗（shell32.dll,-319），一眼认得出是资源管理器。
            Icon icon = ShellIcon.LoadIcon("shell32.dll", -319);
            tray = new NotifyIcon();
            tray.Icon = icon != null ? icon : SystemIcons.Application;
            tray.Text = "TabbedExplorer（接 Win+E）";
            tray.Visible = true;

            MenuItem miShow = new MenuItem("打开窗口（Win+E）");
            miShow.Click += delegate { ShowFromTray(true); };
            MenuItem miQuit = new MenuItem("退出");
            miQuit.Click += delegate
            {
                Diag.Step("EmbedForm: 托盘「退出」");
                quitting = true;
                Close();
            };
            tray.ContextMenu = new ContextMenu(new MenuItem[] { miShow, miQuit });
            tray.DoubleClick += delegate { ShowFromTray(true); };

            // 键盘钩子：Win+E **无条件**接管；Ctrl+T / Ctrl+W / Ctrl+Tab **只在我们窗口是前台时**接管。
            // 为什么这两个也得走钩子：真正持有键盘焦点的是**嵌进来的 explorer 子进程**，
            // 按键不会流进我们的窗体 —— `KeyPreview` / `OnPreviewKeyDown` 一条都收不到。
            // 川報的「Ctrl+T 完全没效果、Ctrl+W 直接把程序关掉」就是这个原因：
            // Ctrl+T explorer 没这个键所以没反应；Ctrl+W 是 explorer 自带的「关闭窗口」，它把自己关了。
            // 回调在钩子线程上，必须转到 UI 线程再动界面。
            hook = new WinEHook();
            hook.MainWindow = Handle;
            hook.WinE += delegate { Post(delegate { ShowFromTray(true); }); };
            hook.NewTabKey += delegate { Post(HotkeyNewTab); };
            hook.CloseTabKey += delegate { Post(HotkeyCloseTab); };
            hook.NextTabKey += delegate { Post(delegate { CycleTab(1); }); };
            hook.PrevTabKey += delegate { Post(delegate { CycleTab(-1); }); };
            hook.Start();

            // 摸一次虚拟桌面接口，把结果写进日志（只读，不搬窗、不切桌面）。
            try { VirtualDesktop.WarmUp(); } catch (Exception ex) { Diag.Log("虚拟桌面: 预热异常 " + ex.Message); }

            // 第二个实例被启动（双击 exe）时，它只会 set 一下这个命名事件，由我们现身。
            if (Program.ShowSignal != null)
            {
                try
                {
                    sigWait = ThreadPool.RegisterWaitForSingleObject(Program.ShowSignal,
                        delegate
                        {
                            try { BeginInvoke(new Action(delegate { ShowFromTray(true); })); }
                            catch { }
                        }, null, -1, false);
                }
                catch (Exception ex) { Diag.Log("EmbedForm: 注册唤醒事件失败 " + ex.Message); }
            }

            // `--quit`：命令行版「退出」，这次是**真退**（会把各标签里的 explorer 都收干净再走）。
            // 事件挂在字段上、不能 using 掉 —— 注册等待之后句柄要一直活着。
            try
            {
                quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.QuitSignalName);
                quitWait = ThreadPool.RegisterWaitForSingleObject(quitEvent,
                    delegate { Post(delegate { quitting = true; Close(); }); }, null, -1, false);
            }
            catch (Exception ex) { Diag.Log("EmbedForm: 注册退出事件失败 " + ex.Message); }
        }

        /// <summary>把动作丢回 UI 线程（钩子 / 线程池的回调都在别的线程上）。</summary>
        private void Post(Action a)
        {
            try { BeginInvoke(a); } catch { }
        }

        /// <summary>Ctrl+T：在本窗口开个新标签（不是新开一个窗口）。</summary>
        private void HotkeyNewTab()
        {
            Diag.Step("EmbedForm: 热键 Ctrl+T -> 新标签");
            if (!Visible) Show();
            NewTab(CurrentPath());
        }

        /// <summary>Ctrl+W：关当前标签；这是最后一个就收进托盘（不退进程）。</summary>
        private void HotkeyCloseTab()
        {
            int i = activeIndex >= 0 ? activeIndex : hosts.Count - 1;
            Diag.Step("EmbedForm: 热键 Ctrl+W -> 关标签 idx=" + i);
            if (i < 0) { HideToTray(); return; }
            CloseTab(i);
        }

        /// <summary>Ctrl+Tab / Ctrl+Shift+Tab：在标签之间循环。</summary>
        private void CycleTab(int delta)
        {
            if (hosts.Count < 2) return;
            int n = hosts.Count;
            int i = ((activeIndex + delta) % n + n) % n;
            Diag.Step("EmbedForm: 热键 Ctrl+Tab -> 切到 idx=" + i);
            Activate(i);
        }

        /// <summary>路径规范化：比路径时用，别让 `c:\Users\a\` 与 `C:\Users\a` 当成两个。</summary>
        private static string NormPath(string p)
        {
            if (string.IsNullOrEmpty(p)) return "";
            p = p.Trim();
            if (p.Length > 3) p = p.TrimEnd('\\');
            return p.ToLowerInvariant();
        }

        /// <summary>已经有标签开着这个路径就返回它的下标，否则 -1。</summary>
        private int IndexOfPath(string path)
        {
            string want = NormPath(path);
            for (int i = 0; i < hosts.Count; i++)
            {
                if (NormPath(hosts[i].TargetPath) == want) return i;
            }
            return -1;
        }

        /// <summary>
        /// Win+E / 托盘 / 第二个实例的统一入口：把窗口摆到前台，并开一个「此电脑」标签。
        /// （和 QTTabBar 一样，Win+E 总是给一个新标签，不会把你正在看的东西顶掉。）
        /// </summary>
        private void ShowFromTray(bool newTab)
        {
            Diag.Step("EmbedForm: ShowFromTray newTab=" + newTab);
            try
            {
                // **先搬桌面再抢前台**：窗口要是属于别的虚拟桌面，SetForegroundWindow 会把川
                // 直接拽回那个桌面（他报的「切了虚拟桌面又被跳回去」）。
                // 必须放在抢焦点之前 —— 一抢到我们就成了前台窗口，问出来的桌面就不是他看的那个了。
                VdOutcome vd = VirtualDesktop.EnsureOnCurrentDesktop(Handle);

                if (vd == VdOutcome.Failed)
                {
                    // 已知窗口在别的桌面、而且没搬过来 ⇒ **绝不能抢前台**（一抢就把他拽走）。
                    // 什么都不做 + 告诉他一声，是这里唯一不伤人的选择。
                    Diag.Step("EmbedForm: 窗口搬不到当前桌面 -> 不显示、不抢前台（不切走）");
                    if (tray != null)
                    {
                        tray.BalloonTipTitle = "打不开：窗口在别的虚拟桌面";
                        tray.BalloonTipText = "这个窗口属于另一张虚拟桌面，暂时搬不过来。再按一次 Win+E 试试。";
                        tray.ShowBalloonTip(3000);
                    }
                    return;
                }

                if (!Visible) Show();
                if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                ActivateToFront();

                if (hosts.Count == 0)
                {
                    NewTab(ExplorerView.ThisPcPath);
                }
                else if (newTab)
                {
                    // 去重：这个路径已经开着（比如一直按 Win+E），切过去就行，
                    // 别再刷一屏「此电脑」—— 原生 Win10 也是复用已有窗口。
                    int dup = IndexOfPath(ExplorerView.ThisPcPath);
                    if (dup >= 0)
                    {
                        Diag.Step("EmbedForm: 「此电脑」已经开着 idx=" + dup + "，切过去（不新建）");
                        Activate(dup);
                    }
                    else NewTab(ExplorerView.ThisPcPath);
                }
                else Activate(activeIndex);
            }
            catch (Exception ex) { Diag.Log("EmbedForm: ShowFromTray 失败 " + ex.Message); }
        }

        /// <summary>
        /// 把窗口提到前台。最小化时先还原 —— 光调 SetForegroundWindow 会被 Windows 的前台锁定
        /// 拒掉（表现就是只闪任务栏、窗口不上来）；借当前前台线程的输入队列一用才有资格。
        /// </summary>
        private void ActivateToFront()
        {
            IntPtr h = Handle;
            if (NativeMethods.IsIconic(h)) NativeMethods.ShowWindow(h, 9);   // SW_RESTORE
            else NativeMethods.ShowWindow(h, 5);                             // SW_SHOW

            uint fgThread = NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), IntPtr.Zero);
            uint myThread = NativeMethods.GetCurrentThreadId();
            bool attached = false;
            if (fgThread != 0 && fgThread != myThread)
                attached = EmbedApi.AttachThreadInput(fgThread, myThread, true);
            try
            {
                NativeMethods.BringWindowToTop(h);
                NativeMethods.SetForegroundWindow(h);
            }
            finally
            {
                if (attached) EmbedApi.AttachThreadInput(fgThread, myThread, false);
            }
        }

        /// <summary>
        /// 收进托盘。**不是退出** —— 进程留着，Win+E 才有落点。
        /// 第一次收起来时弹个气泡说明一下，免得以为程序关了。
        /// </summary>
        private void HideToTray()
        {
            Diag.Step("EmbedForm: 收进托盘（进程常驻，继续接 Win+E）");
            Visible = false;
            if (tray != null && !trayTipShown)
            {
                trayTipShown = true;
                try
                {
                    tray.BalloonTipTitle = "TabbedExplorer 还在后台";
                    tray.BalloonTipText = "按 Win+E 随时打开；右键托盘图标可以退出。";
                    tray.ShowBalloonTip(4000);
                }
                catch { }
            }
        }

        // ==================================================================
        private void NewTab(string path)
        {
            ExplorerHost h = new ExplorerHost();
            h.Host.Dock = DockStyle.Fill;
            h.Host.Visible = false;
            content.Controls.Add(h.Host);
            hosts.Add(h);
            tabStrip.AddTab("打开中…");

            h.Ready += delegate(object s, EventArgs e) { OnHostReady(h); };
            h.Failed += delegate(object s, EventArgs e) { OnHostFailed(h); };
            h.TitleChanged += delegate(object s, EventArgs e) { OnHostTitleChanged(h); };
            h.Died += OnHostDied;

            SetStatus("正在打开 " + path + " …（新 explorer 窗口约需 3 秒）");
            h.Start(path);
            Activate(hosts.IndexOf(h));
        }

        private void OnHostReady(ExplorerHost h)
        {
            int i = hosts.IndexOf(h);
            if (i < 0) return;                       // 已经关掉了
            tabStrip.SetTitle(i, h.CurrentDisplayName);
            if (i == activeIndex)
            {
                h.Host.Visible = true;
                Text = tabStrip.Tabs[i].Title;
                titleBar.Title = Text;
                h.Focus();
                SetStatus(tabStrip.Tabs[i].Title);
            }
        }

        /// <summary>
        /// explorer 在里面导航了，把标签标题跟上。
        /// 否则就是川報的那个 bug：“换了目录，标签页也没变”。
        /// </summary>
        private void OnHostTitleChanged(ExplorerHost h)
        {
            int i = hosts.IndexOf(h);
            if (i < 0) return;
            string t = h.CurrentDisplayName;
            tabStrip.SetTitle(i, t);
            if (i == activeIndex)
            {
                Text = t;
                titleBar.Title = t;
                SetStatus(t);
            }
        }

        private void OnHostFailed(ExplorerHost h)
        {
            int i = hosts.IndexOf(h);
            if (i < 0) return;
            tabStrip.SetTitle(i, "打开失败");
            SetStatus(string.IsNullOrEmpty(h.LastError) ? "打开失败" : h.LastError);
        }

        /// <summary>
        /// 标签里的 explorer 把窗口关了（最典型：在里面按 Ctrl+W）—— 把标签一并收掉，
        /// 不然界面上会留一块打不开的黑区。关到最后一个就走收托盘。
        /// </summary>
        private void OnHostDied(object sender, EventArgs e)
        {
            ExplorerHost h = sender as ExplorerHost;
            if (h == null) return;
            int i = hosts.IndexOf(h);
            Diag.Step("EmbedForm: 标签里的 explorer 自己退了 idx=" + i + " -> 收掉这个标签");
            if (i >= 0) CloseTab(i);
        }

        private void Activate(int idx)
        {
            if (idx < 0 || idx >= hosts.Count) return;
            activeIndex = idx;
            for (int i = 0; i < hosts.Count; i++) hosts[i].Host.Visible = (i == idx);
            tabStrip.SetActive(idx);
            hosts[idx].Focus();
            Text = tabStrip.Tabs[idx].Title;
            titleBar.Title = Text;
        }

        private void CloseTab(int idx)
        {
            if (idx < 0 || idx >= hosts.Count) return;
            ExplorerHost h = hosts[idx];
            hosts.RemoveAt(idx);
            activeIndex = -1;                        // 索引全变了，重新算
            Diag.Step(string.Format("EmbedForm: CloseTab idx={0}，剩 {1} 个", idx, hosts.Count));
            try
            {
                h.Close("关标签 idx=" + idx);
                content.Controls.Remove(h.Host);
                h.Host.Dispose();
                h.Dispose();
            }
            catch (Exception ex) { Diag.Log("EmbedForm: 关标签失败 " + ex.Message); }

            tabStrip.RemoveTab(idx);

            if (hosts.Count == 0)
            {
                // 最后一个标签被关 ⇒ 收进托盘（**不是退出**）。
                // 进程一退 Win+E 就没人接了，系统就会去开原生资源管理器 —— 正是川報的那个问题。
                Diag.Step("EmbedForm: 最后一个标签被关，收进托盘");
                HideToTray();
                return;
            }
            Activate(Math.Min(idx, hosts.Count - 1));
        }

        private string CurrentPath()
        {
            if (activeIndex < 0 || activeIndex >= hosts.Count) return ExplorerView.ThisPcPath;
            string p = hosts[activeIndex].TargetPath;
            return string.IsNullOrEmpty(p) ? ExplorerView.ThisPcPath : p;
        }

        private void SetStatus(string s)
        {
            status.Text = s ?? "";
        }

        // ==================================================================
        /// <summary>
        /// 兜底路径：按键只有在我们**自己的控件**（标签条 / 标题栏）持有焦点时才会走到这里。
        /// 焦点在嵌进来的 explorer 子进程里时根本走不到 —— 那条路全靠 WinEHook（见 SetupResident）。
        /// </summary>
        protected override void OnPreviewKeyDown(PreviewKeyDownEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.T) { HotkeyNewTab(); e.IsInputKey = true; return; }
            if (e.Control && e.KeyCode == Keys.W) { HotkeyCloseTab(); e.IsInputKey = true; return; }
            if (e.Control && e.KeyCode == Keys.Tab)
            {
                CycleTab(e.Shift ? -1 : 1);
                e.IsInputKey = true;
                return;
            }
            base.OnPreviewKeyDown(e);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // 点 X / Alt+F4 只是收进托盘；只有托盘「退出」或系统关机才真的关。
            bool systemShutdown = e.CloseReason == CloseReason.WindowsShutDown
                               || e.CloseReason == CloseReason.TaskManagerClosing;
            if (!quitting && !systemShutdown && e.CloseReason == CloseReason.UserClosing)
            {
                Diag.Step("EmbedForm: 收到关闭请求 reason=" + e.CloseReason + " -> 只收进托盘，不退进程");
                e.Cancel = true;
                HideToTray();
                return;
            }

            Diag.Step(string.Format("EmbedForm: OnFormClosing reason={0}，还有 {1} 个标签",
                e.CloseReason, hosts.Count));
            base.OnFormClosing(e);
            for (int i = hosts.Count - 1; i >= 0; i--)
            {
                try
                {
                    hosts[i].Close("窗体关闭 reason=" + e.CloseReason);
                    hosts[i].Dispose();
                }
                catch (Exception ex) { Diag.Log("EmbedForm: 退出清理失败 " + ex.Message); }
            }
            hosts.Clear();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Diag.Step("EmbedForm: OnFormClosed reason=" + e.CloseReason);
            if (sigWait != null) { try { sigWait.Unregister(null); } catch { } sigWait = null; }
            if (quitWait != null) { try { quitWait.Unregister(null); } catch { } quitWait = null; }
            if (quitEvent != null) { try { quitEvent.Close(); } catch { } quitEvent = null; }
            if (hook != null) { try { hook.Dispose(); } catch { } hook = null; }
            if (tray != null)
            {
                try { tray.Visible = false; tray.Dispose(); } catch { }
                tray = null;
            }
            base.OnFormClosed(e);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.ApplyTitleBar(Handle);
            if (tray == null) SetupResident();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // 深色标题栏必须在窗口**真正显示之后**再设一次。
            // OnHandleCreated 时 DWM 还没合成这个窗口，调用会被丢掉 —— 表现就是标题栏一直白，
            // 而这个窗口其他部分都是深色，那条白杠特别刺眼。
            Theme.ApplyTitleBar(Handle);
            SyncChrome();
            DoLayout();
        }
    }
}
