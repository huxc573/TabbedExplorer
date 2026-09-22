using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace TabbedExplorer
{
    /// <summary>
    /// 「嵌入真 explorer 窗口」模式的窗口 —— **每张虚拟桌面一个**。
    /// 和 MainForm 那套（自绘外壳 + IExplorerBrowser）完全独立。
    ///
    /// 这里我们只负责：顶部一条自绘标题栏 + 一条标签条 + 一个容器。
    /// Ribbon、地址栏、导航窗格、文件列表、状态栏**全部是 explorer 自己的**，
    /// 所以外观和系统资源管理器一模一样。
    ///
    /// 托盘图标和键盘钩子**不在这个类里**了 —— 全进程只能有一份，所以搬去了 DesktopHub。
    /// 这个窗口只管「自己这张桌面的标签」：
    ///   - 起来（第一次现身）时按 `desktops.txt` 里**本桌面**记的那几个路径把标签摆回来；
    ///   - 标签有变动就告诉 Hub 攒一下写盘；
    ///   - 关窗口（X / Ctrl+W 关到最后一个）= 收进托盘，进程不退，标签也还在（真退出才落盘）。
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

        private readonly DesktopHub hub;
        private readonly TitleBar titleBar;
        private readonly TabStrip tabStrip;
        private readonly Panel content;
        private readonly Label status;
        private readonly List<ExplorerHost> hosts = new List<ExplorerHost>();
        private int activeIndex = -1;
        private bool restored;

        /// <summary>这个窗口算哪张虚拟桌面（Hub 的登记键）。窗口被挪到别的桌面时 Hub 会改掉它。</summary>
        internal string DesktopKey { get; set; }

        /// <summary>真退出中（托盘「退出」/`--quit`/系统关机），别再拦关闭。</summary>
        internal bool Quitting { get; set; }

        /// <summary>自己做的窗口边框厚度（只在不最大化时有）。</summary>
        private readonly int ResizeBorder;
        private bool inLayout;

        public EmbedForm(DesktopHub owner, string desktopKey)
        {
            hub = owner;
            DesktopKey = desktopKey;

            Text = "此电脑";
            BackColor = Theme.RibbonBack;   // 无边框后，四周那圈就是这个色，当边框用
            Size = new Size(Px(1200), Px(760));
            MinimumSize = new Size(Px(640), Px(420));
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            Icon = ShellIcon.AppIcon(false);   // 任务栏 / Alt+Tab 用程序自己的图标

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

            // 标签**不在这里开**：要等第一次现身时才知道该还原什么
            // （见 EnsureFirstTab：按本桌面记着的路径把标签摆回来，没记过才开一个「此电脑」）。
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
            // 窗口可能已经被销毁（多窗口之后 Theme.Changed 的订阅者不止一个，没法逐条退订），
            // 对着已释放的控件设颜色会抛 ObjectDisposedException。
            if (IsDisposed || Disposing) return;
            BackColor = Theme.RibbonBack;
            content.BackColor = Theme.Pane;
            status.BackColor = Theme.StatusBack;
            status.ForeColor = Theme.TextDim;
            titleBar.BackColor = Theme.RibbonBack;
            titleBar.Invalidate();
            tabStrip.Invalidate();
        }

        // ==================================================================
        // 现身 / 收托盘
        // ==================================================================

        /// <summary>把窗口句柄建出来（隐藏启动时也要，否则 BeginInvoke 不好使）。</summary>
        public void ForceHandle()
        {
            IntPtr h = Handle;
            GC.KeepAlive(h);
        }

        /// <summary>
        /// Win+E / 托盘 / 双击 exe 的统一入口：把窗口摆出来，并按需要开一个新标签。
        ///
        /// **不再搬虚拟桌面了**：这个窗口本来就属于它那张桌面（由 Hub 按当前桌面创建/认领），
        /// 原来那套「先搬桌面再抢前台」连带把「按 Win+E 被拽到别的桌面」这个毛病一起删掉了。
        /// 这里只留一道保险：万一它确实落在别的桌面上，就**只显示、不抢前台**。
        /// </summary>
        public void ShowForUser(bool newTab)
        {
            Diag.Step("EmbedForm: ShowForUser newTab=" + newTab + " 桌面=" + DesktopKey);
            try
            {
                if (!VirtualDesktop.IsOnCurrentDesktop(Handle))
                {
                    Diag.Step("EmbedForm: 窗口不在当前桌面 -> 不显示、不抢前台（不切走）");
                    if (hub != null)
                        hub.Notify("打不开：窗口在别的虚拟桌面",
                            "这个窗口属于另一张虚拟桌面。回到那张桌面再按 Win+E。", false);
                    return;
                }

                if (!Visible) Show();
                if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                ActivateToFront();

                EnsureFirstTab();

                if (newTab)
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
                else if (activeIndex >= 0) Activate(activeIndex);

                MarkDirty();
            }
            catch (Exception ex) { Diag.Log("EmbedForm: ShowForUser 失败 " + ex.Message); }
        }

        /// <summary>
        /// 第一次现身时把**本桌面**记着的标签摆回来；没记过（或都开不了）才开一个「此电脑」。
        /// 只做一次 —— 之后这个窗口的标签就是活的，关了再按 Win+E 还是原来那些。
        /// </summary>
        private void EnsureFirstTab()
        {
            if (restored || hosts.Count > 0) return;
            restored = true;

            DesktopMemory.Bucket b = (hub == null) ? null : hub.MemoryOf(DesktopKey);
            if (b != null && b.Paths.Count > 0)
            {
                int skipped = 0, wantIdx = -1;
                foreach (string p in b.Paths)
                {
                    if (!PathRules.Restorable(p))
                    {
                        skipped++;
                        Diag.Step("记忆: 跳过开不了的项「" + p + "」（可能是个库/虚拟文件夹，没有真实路径）");
                        continue;
                    }
                    if (wantIdx < 0 && !string.IsNullOrEmpty(b.Active) && PathRules.Same(p, b.Active))
                        wantIdx = hosts.Count;    // 记下的「当时选中那个」是第几个
                    NewTab(p);
                }
                Diag.Step(string.Format("记忆: 桌面 {0} 还原 {1} 个标签（跳过 {2} 个）",
                    DesktopKey, hosts.Count, skipped));
                if (hosts.Count > 0)
                {
                    if (wantIdx >= 0) Activate(Math.Min(wantIdx, hosts.Count - 1));
                    return;
                }
            }

            NewTab(ExplorerView.ThisPcPath);
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
        /// 收进托盘。**不是退出** —— 进程留着，Win+E 才有落点，标签也原样留着。
        /// 第一次收起来时弹个气泡说明一下，免得以为程序关了。
        /// </summary>
        private void HideToTray()
        {
            Diag.Step("EmbedForm: 收进托盘（进程常驻，继续接 Win+E）");
            Visible = false;
            MarkDirty();
            if (hub != null)
                hub.Notify("TabbedExplorer 还在后台",
                    "按 Win+E 随时打开；右键托盘图标可以退出。", true);
        }

        // ==================================================================
        // 热键 / 标签
        // ==================================================================

        /// <summary>Hub 把热键派过来（钩子那边只知道「前台是我们」，具体哪个窗口由它找）。</summary>
        internal void HandleHotkey(string what)
        {
            if (what == "Ctrl+T") { HotkeyNewTab(); return; }
            if (what == "Ctrl+W") { HotkeyCloseTab(); return; }
            if (what == "Ctrl+Tab") { CycleTab(1); return; }
            if (what == "Ctrl+Shift+Tab") { CycleTab(-1); return; }
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

        /// <summary>
        /// 这个标签**现在**在哪个文件夹：优先地址栏实时值（用户在里导航过就以它为准），
        /// 还没读到时退回「我们当初让它打开的路径」。返回值已经过 PathRules 归一（`此电脑` → `::{…}`）。
        /// </summary>
        private static string LivePath(ExplorerHost h)
        {
            if (h == null) return "";
            string p = h.CurrentPath;
            if (string.IsNullOrEmpty(p)) p = h.TargetPath;
            return PathRules.Store(p) ?? "";
        }

        /// <summary>已经有标签开着这个路径就返回它的下标，否则 -1。</summary>
        private int IndexOfPath(string path)
        {
            string want = PathRules.Norm(path);
            for (int i = 0; i < hosts.Count; i++)
            {
                if (PathRules.Norm(LivePath(hosts[i])) == want) return i;
            }
            return -1;
        }

        /// <summary>
        /// 按顺序记下本窗口所有标签的路径（给 Hub 写记忆用）。
        /// **正在起 explorer 的标签也算**：那种还没嵌好、`CurrentPath` 是空的，
        /// `LivePath` 会退回「我们让它开的那个路径」——否则一次中途保存就会把还没加载完的标签从记忆里抹掉。
        /// </summary>
        internal List<string> TabPaths()
        {
            List<string> r = new List<string>();
            foreach (ExplorerHost h in hosts)
            {
                if (h == null) continue;
                string p = LivePath(h);
                if (!string.IsNullOrEmpty(p)) r.Add(p);
            }
            return r;
        }

        /// <summary>当前选中的那个标签的路径（还原时一并切过去）。</summary>
        internal string ActiveTabPath
        {
            get
            {
                if (activeIndex < 0 || activeIndex >= hosts.Count) return null;
                return LivePath(hosts[activeIndex]);
            }
        }

        private void MarkDirty()
        {
            if (hub != null) hub.MarkDirty();
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
            h.SetIconTarget(TabStrip.TabIconSize);      // 告诉它图标画多大（设备像素）

            h.Ready += delegate(object s, EventArgs e) { OnHostReady(h); };
            h.Failed += delegate(object s, EventArgs e) { OnHostFailed(h); };
            h.TitleChanged += delegate(object s, EventArgs e) { OnHostTitleChanged(h); };
            h.IconChanged += delegate(object s, EventArgs e) { OnHostIconChanged(h); };
            h.PathChanged += delegate(object s, EventArgs e) { OnHostPathChanged(h); };
            h.Died += OnHostDied;

            SetStatus("正在打开 " + path + " …（新 explorer 窗口约需 3 秒）");
            h.Start(path);
            Activate(hosts.IndexOf(h));
            MarkDirty();
        }

        private void OnHostReady(ExplorerHost h)
        {
            int i = hosts.IndexOf(h);
            if (i < 0) return;                       // 已经关掉了
            tabStrip.SetTitle(i, h.CurrentDisplayName);
            tabStrip.SetIcon(i, h.TabIcon);
            if (i == activeIndex)
            {
                h.Host.Visible = true;
                Text = tabStrip.Tabs[i].Title;
                titleBar.Title = Text;
                h.Focus();
                SetStatus(tabStrip.Tabs[i].Title);
            }
            MarkDirty();     // 嵌好了 = 可以记了（TabPaths 会跳过还没嵌好的）
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
            tabStrip.SetIcon(i, h.TabIcon);          // 导航后文件夹图标也会换（同一个窗口，换的是它自己挂的图标）
            if (i == activeIndex)
            {
                Text = t;
                titleBar.Title = t;
                SetStatus(t);
            }
        }

        /// <summary>标签图标变了（explorer 按当前文件夹换掉了窗口自己那颗图标）。</summary>
        private void OnHostIconChanged(ExplorerHost h)
        {
            int i = hosts.IndexOf(h);
            if (i < 0) return;
            tabStrip.SetIcon(i, h.TabIcon);
        }

        /// <summary>这个标签导航到了别的文件夹 —— 记忆里的路径要跟着变（标题多半同时也会变）。</summary>
        private void OnHostPathChanged(ExplorerHost h)
        {
            MarkDirty();
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
            MarkDirty();     // 「当时选中那个」也要记
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
            string p = LivePath(hosts[activeIndex]);
            // 库里那种「显示名」不能喂给 explorer（会被当成本目录下的相对路径），退回「此电脑」。
            return PathRules.Restorable(p) ? p : ExplorerView.ThisPcPath;
        }

        private void SetStatus(string s)
        {
            status.Text = s ?? "";
        }

        // ==================================================================
        /// <summary>
        /// 兜底路径：按键只有在我们**自己的控件**（标签条 / 标题栏）持有焦点时才会走到这里。
        /// 焦点在嵌进来的 explorer 子进程里时根本走不到 —— 那条路全靠 WinEHook + Hub（见 DesktopHub.SetupHook）。
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
            // 点 X / Alt+F4 只是收进托盘；只有托盘「退出」/ `--quit` / 系统关机才真的关。
            bool systemShutdown = e.CloseReason == CloseReason.WindowsShutDown
                               || e.CloseReason == CloseReason.TaskManagerClosing;
            if (!Quitting && !systemShutdown && e.CloseReason == CloseReason.UserClosing)
            {
                Diag.Step("EmbedForm: 收到关闭请求 reason=" + e.CloseReason + " -> 只收进托盘，不退进程");
                e.Cancel = true;
                HideToTray();
                return;
            }

            Diag.Step(string.Format("EmbedForm: OnFormClosing reason={0}，还有 {1} 个标签",
                e.CloseReason, hosts.Count));
            base.OnFormClosing(e);

            // 真关之前先把记忆落盘（系统关机这条路上 Hub 的 Quit 不一定会走到）。
            if (hub != null) hub.SaveNow("窗口真关 reason=" + e.CloseReason);

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
            Diag.Step("EmbedForm: OnFormClosed reason=" + e.CloseReason + " 桌面=" + DesktopKey);
            base.OnFormClosed(e);   // 托盘/钩子/事件都不在这个类里（在 DesktopHub）
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.ApplyTitleBar(Handle);
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
