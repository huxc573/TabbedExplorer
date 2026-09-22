using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
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
        private const int WM_NCACTIVATE = 0x0086;
        private const int WM_GETMINMAXINFO = 0x0024;
        private const int WM_SETTINGCHANGE = 0x001A;
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
        private readonly List<ExplorerHost> hosts = new List<ExplorerHost>();
        private int activeIndex = -1;
        private bool restored;

        /// <summary>齿轮菜单 + 它开着没开着的标记（防止点两下弹出两层）。</summary>
        private ContextMenu gearMenu;
        private bool gearMenuOpen;

        /// <summary>刚关掉的标签路径（后进先出）—— Ctrl+Shift+T / 恢复按钮从这儿往回取。</summary>
        private readonly List<string> closedTabs = new List<string>();
        private const int ClosedKeep = 20;

        /// <summary>收藏夹栏（Ctrl+Shift+B 开关）。</summary>
        private readonly FavBar favBar;
        private bool favBarOn;

        /// <summary>标签条空白处右键时鼠标在哪儿 —— 菜单要弹在那个点上。</summary>
        private Point blankAt;

        /// <summary>「收进托盘」那条提示只弹一次（原来靠 Hub 的 once 参数，现在提示归我们自己管）。</summary>
        private bool trayTipShown;

        // ---- 起 explorer 的串行队列（见 PumpLaunch）----
        private sealed class Launch
        {
            public ExplorerHost Host;
            public string Path;
        }
        private readonly Queue<Launch> launchQueue = new Queue<Launch>();
        /// <summary>现在正在起（还没 Ready / Failed）的那个 —— 一次只允许有一个。</summary>
        private ExplorerHost launching;

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
            BackColor = Theme.Chrome;   // 无边框后，四周那圈就是这个色，当边框用（深色下 = 纯黑，跟标签条同色）
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
            // 右侧那排：齿轮（设置）/ 历史 / 恢复关闭 / 收藏夹栏
            tabStrip.ToolClicked += delegate(TabStrip.Tool t)
            {
                switch (t)
                {
                    case TabStrip.Tool.Settings:
                        Diag.Step("EmbedForm: 点击设置按钮");
                        Defer(ShowSettingsMenu);
                        break;
                    case TabStrip.Tool.History:
                        Diag.Step("EmbedForm: 点击历史按钮");
                        Defer(ShowHistoryMenu);
                        break;
                    case TabStrip.Tool.Reopen:
                        Diag.Step("EmbedForm: 点击恢复关闭按钮");
                        ReopenClosedTab();
                        break;
                    case TabStrip.Tool.Fav:
                        Diag.Step("EmbedForm: 点击收藏夹栏按钮");
                        if (hub != null) hub.SetFavBar(!favBarOn);
                        break;
                }
            };
            // 标签右键（复制名称 / 复制完整路径 / 关闭）
            tabStrip.TabRightClicked += delegate(object s, int i)
            {
                int idx = i;
                Diag.Step("EmbedForm: 标签右键 idx=" + idx);
                Defer(delegate { ShowTabMenu(idx); });
            };
            // 标签条空白处右键（把三个功能也放一份在这儿）
            tabStrip.BlankRightClicked += delegate(Point p)
            {
                Diag.Step("EmbedForm: 标签条空白处右键 " + p.X + "," + p.Y);
                blankAt = p;
                Defer(ShowBlankMenu);
            };

            favBar = new FavBar();
            favBar.ItemClicked += delegate(string path)
            {
                Diag.Step("EmbedForm: 收藏夹 -> " + path);
                NewTab(path);
            };
            // 收藏夹栏上右键「隐藏收藏夹栏」：交给 Hub（它要同时改设置、刷托盘菜单、刷所有窗口）
            favBar.HideRequested += delegate
            {
                Diag.Step("EmbedForm: 收藏夹栏右键 -> 隐藏");
                if (hub != null) hub.SetFavBar(false);
            };
            favBar.Visible = false;

            content = new Panel();
            content.BackColor = Theme.Chrome;

            // 位置全部手算（DoLayout）：无边框窗口的四周留一圈自己的边框，
            // 顶部还要多一根自绘标题栏 —— 靠 Dock 拼不出这个形状。
            // 底部**不再有状态栏**（川：最下面的文件夹名去掉，标签上已经显示了）。
            Controls.Add(titleBar);
            Controls.Add(tabStrip);
            Controls.Add(favBar);
            Controls.Add(content);

            ApplyTheme();
            Theme.Changed += delegate { OnThemeChanged(); };
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

                int top = r.Top;
                titleBar.SetBounds(r.Left, top, r.Width, hTitle);
                top += hTitle;
                tabStrip.SetBounds(r.Left, top, r.Width, hTab);
                top += hTab;

                // 收藏夹栏（Ctrl+Shift+B 开）：夹在标签条和内容之间，跟浏览器一样
                if (favBar != null)
                {
                    if (favBarOn)
                    {
                        int hFav = Px(FavBar.StdHeight);
                        favBar.SetBounds(r.Left, top, r.Width, hFav);
                        top += hFav;
                    }
                    favBar.Visible = favBarOn;
                }

                // 内容直接吃到窗口底（以前底下还压着一条 22px 的状态栏）
                int hContent = r.Bottom - top;
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
            // 系统换「应用模式」时会广播 WM_SETTINGCHANGE("ImmersiveColorSet")。
            // 只有「跟随系统」这一档要管 —— 强制浅/深的时候系统爱怎么变都不关我们事。
            if (m.Msg == WM_SETTINGCHANGE && m.LParam != IntPtr.Zero)
            {
                try
                {
                    string s = Marshal.PtrToStringUni(m.LParam);
                    if (string.Equals(s, "ImmersiveColorSet", StringComparison.OrdinalIgnoreCase)
                        && Settings.Color == Settings.ColorMode.System)
                    {
                        Diag.Step("EmbedForm: 系统颜色变了 -> 重读注册表");
                        Theme.Reload();
                        DarkMode.SetEnabled(Theme.IsDark);
                        Theme.RaiseChanged();     // 各控件的 Theme.Changed 会自己重画
                    }
                }
                catch { }
            }
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
            if (m.Msg == WM_NCACTIVATE)
            {
                // 激活 / 失活：把「未激活」状态发给自绘的标题栏和标签条。
                // wParam 非 0 = 正在激活（-1 那种「不要重画」也算激活）。
                SetInactive(m.WParam == IntPtr.Zero);
            }
            base.WndProc(ref m);
        }

        private void ApplyTheme()
        {
            // 窗口可能已经被销毁（多窗口之后 Theme.Changed 的订阅者不止一个，没法逐条退订），
            // 对着已释放的控件设颜色会抛 ObjectDisposedException。
            if (IsDisposed || Disposing) return;
            BackColor = Theme.Chrome;
            content.BackColor = Theme.Chrome;
            titleBar.BackColor = Theme.Chrome;
            titleBar.Invalidate();
            tabStrip.Invalidate();
        }

        /// <summary>
        /// 颜色模式变了（或系统主题变了）：自己的外壳重画 + 标题栏重设。
        /// 强制浅/深时（不是「跟随系统」）再去动嵌进来的 explorer ——
        /// 它是**独立进程**，我们只能逐窗口 SetWindowTheme 尽力而为，
        /// 文件列表那一层仍按它自己的系统主题画，这条限制要在界面上说清楚。
        /// </summary>
        private void OnThemeChanged()
        {
            if (IsDisposed || Disposing) return;
            ApplyTheme();
            Theme.ApplyTitleBar(Handle);
            if (Settings.Color == Settings.ColorMode.System) return;
            foreach (ExplorerHost h in hosts)
            {
                if (h != null) h.Restyle();
            }
        }

        /// <summary>外面（Hub）改完设置让我重刷外观。</summary>
        internal void ApplyThemeNow()
        {
            if (IsDisposed || Disposing) return;
            ApplyTheme();
            Theme.ApplyTitleBar(Handle);
        }

        /// <summary>标签宽度 / 自适应改了：标签条每次重算布局，invalidate 一下就行。</summary>
        internal void RefreshTabs()
        {
            if (IsDisposed || Disposing) return;
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
            Diag.Step("EmbedForm: ShowForUser newTab=" + newTab + " 桌面=" + DesktopKey
                      + " 模式=" + (hub == null ? "?" : Settings.Text(hub.Capture)));
            try
            {
                bool migrating = (hub != null && hub.Capture == Settings.CaptureMode.Migrate);
                if (migrating)
                {
                    // v1.0.0 那套：全进程就这一个窗口，Win+E 时把它**搬到当前桌面**再显。
                    // 搬到才算数 —— 搬不过去就只显示、不抢前台，免得反而把人拽走。
                    VdOutcome o = VirtualDesktop.EnsureOnCurrentDesktop(Handle);
                    Diag.Step("EmbedForm: 迁移模式，搬窗口结果=" + o);
                    if (o == VdOutcome.Failed)
                    {
                        if (!Visible) Show();
                        Toast.Show("打不开：窗口在别的虚拟桌面",
                            "没能把这个窗口搬到当前桌面。回到它所在的桌面再试。");
                        return;
                    }
                }
                else if (!VirtualDesktop.IsOnCurrentDesktop(Handle))
                {
                    Diag.Step("EmbedForm: 窗口不在当前桌面 -> 不显示、不抢前台（不切走）");
                    Toast.Show("打不开：窗口在别的虚拟桌面",
                        "这个窗口属于另一张虚拟桌面。回到那张桌面再按 Win+E。");
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
        /// 「川自己打开的文件夹窗口被收进来了」—— 需要现身把它露出来。
        ///
        /// 跟 `ShowForUser` 的区别（**别混用**）：
        ///   · 不 `EnsureFirstTab`（标签刚收进来就是第一个，不能再自作主张开一个「此电脑」）；
        ///   · 不判虚拟桌面（那个窗口本来就开在川眼前，他在哪张桌面我们就在哪张）；
        ///   · 不 `newTab`（收进来的那个就是要看的那个）。
        /// 但**要**抢一下前台：他刚双击文件夹，本该有一扇窗弹到最前面；
        /// 我们把那扇窗藏了收进标签，就得由我们把窗口顶上来，不然他眼前像是「什么都没发生」。
        /// </summary>
        internal void ShowForCapture()
        {
            if (IsDisposed || Disposing) return;
            try
            {
                if (!Visible) Show();
                if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                ActivateToFront();
                MarkDirty();
            }
            catch (Exception ex) { Diag.Log("EmbedForm: ShowForCapture 失败 " + ex.Message); }
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
            if (!Settings.KeepTabs)
            {
                Diag.Step("记忆: 「保留标签页」关着 -> 不还原，直接开一个「此电脑」");
                b = null;
            }
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
                    // 同一个路径已经有标签了就别再开一个。
                    // 记忆文件里偶尔会有重复行（老版本并发开标签时写坏的），去重放在这儿最稳：
                    // 不管文件脏成什么样，界面上都不会冒出两个一模一样的标签。
                    if (IndexOfPath(p) >= 0)
                    {
                        skipped++;
                        Diag.Step("记忆: 「" + p + "」已经有标签了，跳过重复项");
                        continue;
                    }
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
            if (!trayTipShown)
            {
                trayTipShown = true;
                Toast.Show("TabbedExplorer 还在后台", "按 Win+E 随时打开；右键托盘图标可以退出。");
            }
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
            // ---- 2026-09-22 新加的三个（都跟浏览器对齐）----
            if (what == "Ctrl+H")
            {
                Diag.Step("EmbedForm: 热键 Ctrl+H -> 历史记录");
                if (!Visible) Show();
                Defer(ShowHistoryMenu);
                return;
            }
            if (what == "Ctrl+Shift+T")
            {
                Diag.Step("EmbedForm: 热键 Ctrl+Shift+T -> 恢复关闭的标签");
                ReopenClosedTab();
                return;
            }
            if (what == "Ctrl+Shift+B")
            {
                Diag.Step("EmbedForm: 热键 Ctrl+Shift+B -> 收藏夹栏开关");
                if (hub != null) hub.SetFavBar(!favBarOn);
                return;
            }
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
            ExplorerHost h = AddHost();
            int i = hosts.IndexOf(h);
            // 第二行先摆上要去的路径 —— explorer 要 3 秒才起得来，这 3 秒里也别让第二行空着
            tabStrip.SetPath(i, TabStrip.PathLine(PathRules.Store(path)));
            h.PresetTarget(path);     // 排队期间也得知道要去哪儿（否则中途保存记忆会把它丢掉）
            Diag.Step("EmbedForm: 排入队列 " + path);
            launchQueue.Enqueue(new Launch { Host = h, Path = path });
            PumpLaunch();
            Activate(i);
            MarkDirty();
        }

        /// <summary>
        /// 起 explorer —— **一次只起一个**，排成队依次来。
        ///
        /// 这是「有时候标签页点开是空的」的**根治**（2026-09-22）：
        /// 原来还原 6 个标签就是**一口气起 6 个 explorer**，6 个窗口几乎同时冒出来，
        /// 而每个标签都只能靠「扫一个刚出现的新窗口」来认自己那个 —— 于是互相认错，
        /// 或者谁都认不到、空等 25 秒超时（日志里就是这么演的）。
        /// 串起来之后，任何时刻只有一个标签在找窗口，**一一对应**是确定的，不用再猜。
        ///
        /// 代价是「还原 6 个标签」要 6 次 × 大约一两秒。可以接受：标签是**立刻**就出来的
        /// （显示「打开中…」+ 目标路径），内容各填各的 —— 浏览器也是这个样子。
        /// </summary>
        private void PumpLaunch()
        {
            if (launching != null || IsDisposed || Disposing) return;
            while (launchQueue.Count > 0)
            {
                Launch j = launchQueue.Dequeue();
                if (j.Host == null || !hosts.Contains(j.Host)) continue;   // 排队期间被关掉了
                launching = j.Host;
                Diag.Step("EmbedForm: 起 explorer（串行）" + j.Path);
                j.Host.Start(j.Path);
                return;
            }
        }

        /// <summary>一个标签起完了（成了 / 失败了）—— 轮到队列里的下一个。</summary>
        private void LaunchDone(ExplorerHost h)
        {
            if (launching != h) return;
            launching = null;
            PumpLaunch();
        }

        /// <summary>
        /// 开一个「接管别人窗口」的标签 —— 川从开始菜单 / 桌面双击打开的那个文件夹（Bug 1）。
        /// 跟 `NewTab` 唯一的区别：**不起新 explorer**，直接把已经存在的那个窗口收进来。
        /// 所以它**不进队列**（队列的意义是「别同时起两个进程」，这条不进程）。
        /// </summary>
        internal bool NewAdoptedTab(IntPtr cab, int pid)
        {
            if (IsDisposed || Disposing || cab == IntPtr.Zero) return false;
            foreach (ExplorerHost e in hosts)
            {
                if (e != null && e.CabWindow == cab) return false;   // 已经收过了
            }
            ExplorerHost h = AddHost();
            int i = hosts.IndexOf(h);
            tabStrip.SetPath(i, "（收进来的窗口）");
            h.Adopt(cab, pid);
            Activate(i);
            MarkDirty();
            return true;
        }

        /// <summary>
        /// 建一个标签 + 一个空的 shell 容器，把事件全接好。**怎么把它开起来由调用方决定**：
        /// 自己起一个（`NewTab` → `Start`）还是接管现成的（`NewAdoptedTab` → `Adopt`）。
        /// 两条路后面完全一样：等 explorer 加载完 → Ready → 嵌进来。
        /// </summary>
        private ExplorerHost AddHost()
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
            return h;
        }

        private void OnHostReady(ExplorerHost h)
        {
            int i = hosts.IndexOf(h);
            if (i < 0) return;                       // 已经关掉了
            tabStrip.SetTitle(i, h.CurrentDisplayName);
            tabStrip.SetIcon(i, h.TabIcon);
            tabStrip.SetPath(i, TabStrip.PathLine(LivePath(h)));
            History.Add(LivePath(h));                // 真打开了才算「去过」
            if (i == activeIndex)
            {
                h.Host.Visible = true;
                Text = tabStrip.Tabs[i].Title;
                titleBar.Title = Text;
                h.Focus();
            }
            MarkDirty();     // 嵌好了 = 可以记了（TabPaths 会跳过还没嵌好的）
            LaunchDone(h);   // 这一个起完了，队列里的下一个可以动了
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
            }
        }

        /// <summary>标签图标变了（explorer 按当前文件夹换掉了窗口自己那颗图标）。</summary>
        private void OnHostIconChanged(ExplorerHost h)
        {
            int i = hosts.IndexOf(h);
            if (i < 0) return;
            tabStrip.SetIcon(i, h.TabIcon);
        }

        /// <summary>
        /// 这个标签导航到了别的文件夹 —— 标题、第二行路径、记忆里的路径都要跟着变。
        /// （`TitleChanged` 和 `PathChanged` 是两条独立事件，导航时不一定同时到，所以两边都刷一遍。）
        /// </summary>
        private void OnHostPathChanged(ExplorerHost h)
        {
            int i = hosts.IndexOf(h);
            if (i >= 0)
            {
                string p = LivePath(h);
                tabStrip.SetPath(i, TabStrip.PathLine(p));
                History.Add(p);
            }
            MarkDirty();
        }

        private void OnHostFailed(ExplorerHost h)
        {
            int i = hosts.IndexOf(h);
            if (i < 0) return;
            string want = h.TargetPath;
            string why = h.LastError ?? "（没有错误文本）";
            tabStrip.SetTitle(i, "打开失败");
            tabStrip.SetPath(i, why);
            Diag.Log("EmbedForm: 标签打开失败 " + why);

            LaunchDone(h);   // 先让队列往前走（这一个已经结束了）

            // 干等 25 秒 —— 多半是那个窗口被别人抢了 / explorer 这次没给新建窗口。
            // 自动再来一次，别把一个黑标签留在那儿等川自己发现。
            // 只重试一次（Retried 标记），重试还不行就老实报错。重试也走队列，别破坏串行。
            if (!h.Retried && !string.IsNullOrEmpty(want) && PathRules.Restorable(want))
            {
                h.Retried = true;
                Diag.Step("EmbedForm: 自动重试一次「" + want + "」");
                tabStrip.SetTitle(i, "重试中…");
                tabStrip.SetPath(i, TabStrip.PathLine(PathRules.Store(want)));
                launchQueue.Enqueue(new Launch { Host = h, Path = want });
                PumpLaunch();
            }
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

            // 记进「刚关掉的」栈 —— Ctrl+Shift+T 要按「后进先出」往回捞：
            // 点一下恢复最近关的那个、再点一下恢复上上个（川的要求）。
            string gone = LivePath(h);
            if (PathRules.Restorable(gone))
            {
                closedTabs.RemoveAll(delegate(string s) { return PathRules.Same(s, gone); });
                closedTabs.Add(gone);
                while (closedTabs.Count > ClosedKeep) closedTabs.RemoveAt(0);
                Diag.Step("EmbedForm: 记下关掉的标签「" + gone + "」（栈里 " + closedTabs.Count + " 个）");
            }

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

            // 关掉的正好是「正在起」的那个：队列得往前走，不然后面排着的全卡住
            if (launching == h)
            {
                launching = null;
                Diag.Step("EmbedForm: 正在起的那个被关了，队列继续");
                PumpLaunch();
            }

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

        // ==================================================================
        // 设置（标签条最右边那枚齿轮）—— 内容在 SettingsMenu，跟托盘右键共用同一份
        // ==================================================================

        /// <summary>
        /// 齿轮弹出来的设置菜单。真正的内容由 `SettingsMenu` 统一生成（托盘右键也是它），
        /// 这里只负责「什么时候弹、弹在哪儿」。
        ///
        /// ⚠ 卡死的两个坑都在这段里，2026-09-22 连踩三次才收干净：
        ///   ① **不能在 MouseDown 里同步弹** —— 鼠标消息没走完就切鼠标捕获，菜单的模态循环
        ///      跟控件的捕获互相等。所以调用方一律走 `BeginInvoke`（见构造函数里的订阅）。
        ///   ② 菜单类型必须是 **`ContextMenu`**（跟托盘同一个类），**不能**用 `ContextMenuStrip`
        ///      再「不挂 owner 直接给屏幕坐标」—— 那个组合弹出来是非模态的、还抢着鼠标捕获，
        ///      从用户角度就是界面上什么都不响应（而日志里 `Show` 早就返回了，看着一切正常）。
        /// `ContextMenu.Show(owner, point)` 走 WinForms 的模态菜单循环：**一直阻塞到菜单关掉**，
        /// 所以「弹菜单返回」这行日志出现时菜单一定已经关了，卡没卡一眼可辨。
        /// </summary>
        private void ShowSettingsMenu()
        {
            if (hub == null || gearMenuOpen || IsDisposed || Disposing) return;
            gearMenuOpen = true;
            ContextMenu m = null;
            try
            {
                Diag.Step("EmbedForm: 建设置菜单");
                m = SettingsMenu.BuildGear(hub);
                gearMenu = m;

                // 菜单宽度量不出来（Menu 没有 GetPreferredSize），所以把锚点放在齿轮**中心**：
                // 菜单从那儿往右铺，超出屏幕时 TrackPopupMenu 自己会挪回来。
                Rectangle sb = tabStrip.SettingsButtonBounds();
                Point at = tabStrip.PointToScreen(new Point(sb.Left + sb.Width / 2, sb.Bottom));
                Point local = tabStrip.PointToClient(at);
                Diag.Step(string.Format("EmbedForm: 弹菜单（模态）锚点 {0},{1}", at.X, at.Y));
                m.Show(tabStrip, local);
                Diag.Step("EmbedForm: 菜单已关");
            }
            catch (Exception ex)
            {
                Diag.Log("EmbedForm: 设置菜单失败 " + ex);
            }
            finally
            {
                gearMenuOpen = false;
                gearMenu = null;
                if (m != null) { try { m.Dispose(); } catch { } }
            }
        }

        // ==================================================================
        // 历史 / 恢复关闭 / 右键菜单 / 收藏夹栏（2026-09-22 川点名的三个新功能）
        // ==================================================================

        /// <summary>
        /// 把一件事推到「当前这轮消息处理完之后」再干。
        ///
        /// 为什么一律这么走（就是齿轮卡死那个坑的根）：弹菜单、关标签这种动作如果在控件的
        /// `MouseDown` 里同步做，鼠标消息还没走完就切了鼠标捕获，菜单的模态循环跟控件的捕获互相等
        /// —— 界面就死了。跳出一个消息循环再动手，两个循环永远不会叠在一起。
        /// </summary>
        private void Defer(Action a)
        {
            if (a == null || IsDisposed || Disposing) return;
            try { BeginInvoke(a); }
            catch (Exception ex) { Diag.Log("EmbedForm: Defer 失败 " + ex.Message); }
        }

        /// <summary>Ctrl+H / 历史按钮：列出去过的文件夹，挑一个开成新标签。</summary>
        private void ShowHistoryMenu()
        {
            if (IsDisposed || Disposing) return;
            ShowPopupAtTool(TabStrip.Tool.History, History.BuildMenu(delegate(string p)
            {
                Diag.Step("EmbedForm: 历史 -> " + p);
                Defer(delegate
                {
                    if (!Visible) Show();
                    int dup = IndexOfPath(p);
                    if (dup >= 0) Activate(dup);
                    else NewTab(p);
                });
            }));
        }

        /// <summary>
        /// 在某个工具按钮正下方弹一份菜单。
        /// 锚点跟齿轮那条同一个算法：按**按钮中心**定位，菜单自己会往回挪（不会跑出屏幕）。
        /// </summary>
        private void ShowPopupAtTool(TabStrip.Tool tool, MenuItem[] items)
        {
            if (items == null || items.Length == 0) return;
            Rectangle b = tabStrip.ToolButtonBounds(tool);
            Point at = tabStrip.PointToScreen(new Point(b.Left + b.Width / 2, b.Bottom));
            ContextMenu m = new ContextMenu(items);
            try { m.Show(tabStrip, tabStrip.PointToClient(at)); }
            catch (Exception ex) { Diag.Log("EmbedForm: 弹菜单失败 " + ex); }
            finally { try { m.Dispose(); } catch { } }
        }

        /// <summary>Ctrl+Shift+T / 恢复按钮：把最近关掉的那个标签开回来（后进先出）。</summary>
        private void ReopenClosedTab()
        {
            if (closedTabs.Count == 0)
            {
                Diag.Step("EmbedForm: 恢复关闭的标签，但栈是空的");
                Toast.Show("没有可恢复的标签页", "这次运行里还没关过标签。");
                return;
            }
            string p = closedTabs[closedTabs.Count - 1];
            closedTabs.RemoveAt(closedTabs.Count - 1);
            Diag.Step("EmbedForm: 恢复关闭的标签「" + p + "」（栈里还剩 " + closedTabs.Count + "）");
            if (!Visible) Show();
            int dup = IndexOfPath(p);
            if (dup >= 0) Activate(dup);   // 已经开着（历史/收藏夹又开过）就切过去，别开两个一样的
            else NewTab(p);
        }

        /// <summary>标签上右键：复制文件夹名 / 复制完整路径 / 关闭（川点名的三条）。</summary>
        private void ShowTabMenu(int idx)
        {
            if (idx < 0 || idx >= hosts.Count || IsDisposed || Disposing) return;
            string title = tabStrip.Tabs[idx].Title;
            string live = LivePath(hosts[idx]);
            string target = PathRules.Restorable(live) ? live : hosts[idx].TargetPath;

            List<MenuItem> m = new List<MenuItem>();
            m.Add(new MenuItem("复制文件夹名", delegate { CopyText(title, "文件夹名"); }));
            m.Add(new MenuItem("复制完整路径", delegate { CopyText(target, "完整路径"); }));
            m.Add(new MenuItem("-"));
            m.Add(new MenuItem("在新标签页打开", delegate
            {
                if (!PathRules.Restorable(target))
                {
                    Toast.Show("开不了", "这个位置没有真实路径（库/虚拟文件夹）。");
                    return;
                }
                string p = target;
                Defer(delegate { NewTab(p); });
            }));
            m.Add(new MenuItem("重新打开刚关闭的标签页(Ctrl+Shift+T)",
                delegate { Defer(ReopenClosedTab); }));
            m.Add(new MenuItem("-"));
            m.Add(new MenuItem("关闭标签页(Ctrl+W)", delegate { Defer(delegate { CloseTab(idx); }); }));

            Rectangle b = tabStrip.TabBounds(idx);
            Point at = tabStrip.PointToScreen(new Point(b.Left + b.Width / 2, b.Bottom));
            ContextMenu cm = new ContextMenu(m.ToArray());
            try { cm.Show(tabStrip, tabStrip.PointToClient(at)); }
            catch (Exception ex) { Diag.Log("EmbedForm: 标签右键菜单失败 " + ex); }
            finally { try { cm.Dispose(); } catch { } }
        }

        /// <summary>
        /// 标签条空白处右键 —— 那三个新功能也在这儿放一份（川要的），
        /// 顺带把「新建标签页」写出来（这块空白本来双击就是新建，写明白更好）。
        /// </summary>
        private void ShowBlankMenu()
        {
            if (IsDisposed || Disposing) return;
            List<MenuItem> m = new List<MenuItem>();
            m.Add(new MenuItem("新建标签页(Ctrl+T)", delegate { Defer(delegate { NewTab(CurrentPath()); }); }));
            m.Add(new MenuItem("-"));
            m.Add(new MenuItem("历史记录(Ctrl+H)", delegate { Defer(ShowHistoryMenu); }));
            m.Add(new MenuItem("恢复关闭的标签页(Ctrl+Shift+T)", delegate { Defer(ReopenClosedTab); }));

            bool on = favBarOn;
            m.Add(new MenuItem((on ? "✓ " : "   ") + "显示收藏夹栏(Ctrl+Shift+B)", delegate
            {
                if (hub != null) hub.SetFavBar(!on);
            }));

            m.Add(new MenuItem("-"));
            m.Add(new MenuItem("设置", delegate { Defer(ShowSettingsMenu); }));

            ContextMenu cm = new ContextMenu(m.ToArray());
            Point at = tabStrip.PointToScreen(blankAt);
            try { cm.Show(tabStrip, tabStrip.PointToClient(at)); }
            catch (Exception ex) { Diag.Log("EmbedForm: 空白右键菜单失败 " + ex); }
            finally { try { cm.Dispose(); } catch { } }
        }

        private void CopyText(string text, string what)
        {
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                Clipboard.SetText(text);
                Toast.Show("已复制" + what, text);
            }
            catch (Exception ex) { Diag.Log("EmbedForm: 复制失败 " + ex.Message); }
        }

        /// <summary>
        /// 收藏夹栏开关。**所有入口都汇到这一条**（Ctrl+Shift+B、按钮、设置菜单、空白右键、栏上右键），
        /// 免得像当初「托盘没有设置项」那样漏一边。由 Hub 调（它要同时刷所有窗口 + 托盘菜单）。
        /// </summary>
        internal void SetFavBarOn(bool on)
        {
            if (IsDisposed || Disposing) return;
            favBarOn = on;
            tabStrip.FavBarOn = on;
            if (on) favBar.Reload();
            Diag.Step("EmbedForm: 收藏夹栏 -> " + (on ? "显示" : "隐藏"));
            DoLayout();
        }

        internal bool FavBarOn { get { return favBarOn; } }

        /// <summary>
        /// 激活 / 失活（Bug 6：未激活时窗口颜色要跟 Windows 原本的逻辑一致）。
        /// 原生标题栏失活会把标题字变灰、强调色变暗，我们照做：
        /// 收到 `WM_NCACTIVATE` 就切自绘标题栏和标签条的 Inactive，它们自己重画。
        /// </summary>
        private void SetInactive(bool inactive)
        {
            if (titleBar != null) titleBar.Inactive = inactive;
            if (tabStrip != null) tabStrip.Inactive = inactive;
            if (favBar != null) favBar.Invalidate();
        }

        // ==================================================================
        /// <summary>
        /// 兜底路径：按键只有在我们**自己的控件**（标签条 / 标题栏）持有焦点时才会走到这里。
        /// 焦点在嵌进来的 explorer 子进程里时根本走不到 —— 那条路全靠 WinEHook + Hub（见 DesktopHub.SetupHook）。
        /// </summary>
        protected override void OnPreviewKeyDown(PreviewKeyDownEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.T)
            {
                // Ctrl+Shift+T（恢复关闭的标签）跟 Ctrl+T 只差一个 Shift，先判带 Shift 的那个
                if (e.Shift) ReopenClosedTab(); else HotkeyNewTab();
                e.IsInputKey = true;
                return;
            }
            if (e.Control && e.KeyCode == Keys.W) { HotkeyCloseTab(); e.IsInputKey = true; return; }
            if (e.Control && e.KeyCode == Keys.H)
            {
                Defer(ShowHistoryMenu);
                e.IsInputKey = true;
                return;
            }
            if (e.Control && e.Shift && e.KeyCode == Keys.B)
            {
                if (hub != null) hub.SetFavBar(!favBarOn);
                e.IsInputKey = true;
                return;
            }
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
            if (gearMenu != null)
            {
                try { gearMenu.Dispose(); } catch { }
                gearMenu = null;
            }
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
