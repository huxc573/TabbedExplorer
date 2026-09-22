using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace TabbedExplorer
{
    internal sealed class AppContext : ApplicationContext
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int VK_LWIN = 0x5B;
        private const int VK_RWIN = 0x5C;
        private const int VK_E = 0x45;
        private const uint LLKHF_INJECTED = 0x10;

        private readonly NotifyIcon tray;
        private readonly Form hidden;
        private readonly List<MainForm> forms = new List<MainForm>();
        private readonly object sync = new object();

        private IntPtr hHook = IntPtr.Zero;
        private HookProc hookProc;
        private ApplicationContext hookThreadContext;
        private Thread hookThread;

        private IVirtualDesktopManager vdm;
        private bool vdmTried;

        private readonly string sessionFile;
        private readonly List<SessionEntry> loadedSessions = new List<SessionEntry>();
        private readonly HashSet<int> consumedSessions = new HashSet<int>();
        private System.Windows.Forms.Timer saveTimer;

        public AppContext()
        {
            Application.ApplicationExit += (s, e) => Shutdown();

            hidden = new Form();
            hidden.CreateControl();
            IntPtr dummy = hidden.Handle;   // 强制建句柄，用于跨线程 Invoke

            sessionFile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "TabbedExplorer", "sessions.txt");
            LoadSessions();

            tray = new NotifyIcon();
            tray.Icon = SystemIcons.Application;
            tray.Text = "TabbedExplorer（接管 Win+E）";
            tray.Visible = true;

            MenuItem miOpen = new MenuItem("新建窗口");
            miOpen.Click += (s, e) => OpenForDesktop(ExplorerView.ThisPcPath, true);
            MenuItem miExit = new MenuItem("退出");
            miExit.Click += (s, e) => { SaveSessions(); Application.Exit(); };
            tray.ContextMenu = new ContextMenu(new MenuItem[] { miOpen, miExit });
            tray.DoubleClick += (s, e) => OpenForDesktop(ExplorerView.ThisPcPath, true);

            // 注意：必须写全名 TabbedExplorer.MainForm —— 本类继承 ApplicationContext，
            // 而它自带一个 MainForm（Form 类型）属性，光写 MainForm 会被解析成那个属性。
            TabbedExplorer.MainForm.RequestNewWindow =
                delegate { OpenForDesktop(ExplorerView.ThisPcPath, true); };

            StartHook();

            if (Program.AutoOpen)
            {
                // 重装 / 调试用：起来就开窗，别等用户按 Win+E。
                // 走 BeginInvoke 排到消息循环启动之后再开，避免过早碰 shell 控件。
                hidden.BeginInvoke(new Action(delegate
                {
                    OpenForDesktop(ExplorerView.ThisPcPath, true);
                }));
            }
        }

        // ==================================================================
        // Win+E 钩子
        // ==================================================================
        private void StartHook()
        {
            hookThread = new Thread(HookThreadProc);
            hookThread.IsBackground = true;
            hookThread.SetApartmentState(ApartmentState.STA);
            hookThread.Start();
        }

        private void HookThreadProc()
        {
            hookProc = new HookProc(HookCallback);
            hHook = NativeMethods.SetWindowsHookEx(WH_KEYBOARD_LL, hookProc, IntPtr.Zero, 0);
            if (hHook == IntPtr.Zero)
            {
                Log("钩子安装失败");
                return;
            }
            hookThreadContext = new ApplicationContext();
            Application.Run(hookThreadContext);   // 本线程消息循环
            NativeMethods.UnhookWindowsHookEx(hHook);
            hHook = IntPtr.Zero;
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            // 这里只允许做：读字段 + 判定 + 投递。绝不做 COM / IO / 开窗。
            try
            {
                if (nCode >= 0)
                {
                    int msg = wParam.ToInt32();
                    if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                    {
                        KBDLLHOOKSTRUCT st = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(
                            lParam, typeof(KBDLLHOOKSTRUCT));

                        bool injected = (st.flags & LLKHF_INJECTED) != 0;
                        if (!injected && st.vkCode == VK_E)
                        {
                            bool winDown =
                                (NativeMethods.GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 ||
                                (NativeMethods.GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;

                            if (winDown)
                            {
                                // 交给 UI 线程处理，同时吞掉这次按键，阻止原版资源管理器打开
                                BeginUi(delegate { OpenForDesktop(ExplorerView.ThisPcPath, false); });
                                return (IntPtr)1;
                            }
                        }
                    }
                }
            }
            catch { }
            return NativeMethods.CallNextHookEx(hHook, nCode, wParam, lParam);
        }

        private void BeginUi(Action a)
        {
            try
            {
                if (hidden != null && hidden.IsHandleCreated) hidden.BeginInvoke(a);
                else a();
            }
            catch { }
        }

        // ==================================================================
        // 窗口管理
        // ==================================================================
        /// <summary>
        /// Win+E 入口：找到当前虚拟桌面上已有的窗口 → 合并（新增标签）；
        /// 没有就新建一个窗口（并恢复该桌面记忆的标签）。
        /// </summary>
        public void OpenForDesktop(string path, bool forceNewWindow)
        {
            Diag.Step("OpenForDesktop path=" + path + " forceNew=" + forceNewWindow);
            MainForm target = null;

            if (!forceNewWindow)
            {
                target = FindFormOnCurrentDesktop();
            }

            if (target != null)
            {
                target.OpenInNewTab(path);
                ActivateForm(target);
                return;
            }

            MainForm f = new MainForm(null, 0);
            f.DesktopId = TryGetDesktopId();
            f.SessionDirty += (s, e) => ScheduleSave();

            // 关窗前先快照 —— FormClosed 之后 forms 里就没它了，而 SaveSessions 是延迟 1.5 秒
            // 才跑的，那时再遍历 forms 根本写不到它。（"窗口大小记不住"的根因就在这）
            f.FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                lock (sync) { CaptureForm((TabbedExplorer.MainForm)s); }
            };
            f.FormClosed += (s, e) =>
            {
                lock (sync) { forms.Remove((TabbedExplorer.MainForm)s); }
                ScheduleSave();
            };

            // 恢复该桌面的记忆
            SessionEntry entry = TakeSession(f.DesktopId);
            if (entry != null)
            {
                RestoreInto(f, entry);
            }
            else
            {
                f.OpenInNewTab(path);
            }

            lock (sync) { forms.Add(f); }
            f.Show();
            ActivateForm(f);
            EnsureOnCurrentDesktop(f);
            ScheduleSave();
        }

        /// <summary>把窗口弄到前台。最小化时先还原 —— 光调 SetForegroundWindow 会被
        /// Windows 的前台锁定拒掉（表现为只闪任务栏、窗口不上来）。</summary>
        private void ActivateForm(Form f)
        {
            IntPtr h = f.Handle;
            if (NativeMethods.IsIconic(h)) NativeMethods.ShowWindow(h, 9);   // SW_RESTORE
            else NativeMethods.ShowWindow(h, 5);                             // SW_SHOW

            // 借当前前台线程的输入队列一用，才有资格把窗口提到最前
            uint fgThread = NativeMethods.GetWindowThreadProcessId(
                NativeMethods.GetForegroundWindow(), IntPtr.Zero);
            uint myThread = NativeMethods.GetCurrentThreadId();
            bool attached = false;
            if (fgThread != 0 && fgThread != myThread)
                attached = NativeMethods.AttachThreadInput(fgThread, myThread, true);
            try
            {
                NativeMethods.BringWindowToTop(h);
                NativeMethods.SetForegroundWindow(h);
            }
            finally
            {
                if (attached) NativeMethods.AttachThreadInput(fgThread, myThread, false);
            }
        }

        /// <summary>当前桌面上是否有本程序的窗口（公开 API，最可靠）。</summary>
        private MainForm FindFormOnCurrentDesktop()
        {
            IVirtualDesktopManager m = GetVdm();
            lock (sync)
            {
                if (m == null)
                {
                    return forms.Count > 0 ? forms[forms.Count - 1] : null;
                }
                foreach (MainForm f in forms)
                {
                    if (!f.IsHandleCreated || !f.Visible) continue;
                    bool on;
                    int hr = m.IsWindowOnCurrentVirtualDesktop(f.Handle, out on);
                    if (hr == 0 && on) return f;
                }
            }
            return null;
        }

        private Guid TryGetDesktopId()
        {
            Diag.Step("TryGetDesktopId: enter");
            IVirtualDesktopManager m = GetVdm();
            if (m == null) { Diag.Step("TryGetDesktopId: 没有 vdm"); return Guid.Empty; }

            try
            {
                Guid g;
                IntPtr fg = NativeMethods.GetForegroundWindow();
                Diag.Step("TryGetDesktopId: fg=" + fg);
                if (fg != IntPtr.Zero && NativeMethods.IsWindow(fg))
                {
                    int hr = m.GetWindowDesktopId(fg, out g);
                    Diag.Step("TryGetDesktopId: fg hr=0x" + hr.ToString("X8") + " g=" + ShortGuid(g));
                    if (hr == 0 && g != Guid.Empty) return g;
                }

                IntPtr shell = NativeMethods.GetShellWindow();
                Diag.Step("TryGetDesktopId: shell=" + shell);
                if (shell != IntPtr.Zero && NativeMethods.IsWindow(shell))
                {
                    int hr = m.GetWindowDesktopId(shell, out g);
                    Diag.Step("TryGetDesktopId: shell hr=0x" + hr.ToString("X8") + " g=" + ShortGuid(g));
                    if (hr == 0 && g != Guid.Empty) return g;
                }
            }
            catch (Exception ex)
            {
                Diag.Log("TryGetDesktopId 异常: " + ex.Message);
            }

            Diag.Step("TryGetDesktopId: 拿不到，按未知处理");
            return Guid.Empty;
        }

        private static string ShortGuid(Guid g)
        {
            return g == Guid.Empty ? "(empty)" : g.ToString().Substring(0, 8);
        }

        private void EnsureOnCurrentDesktop(MainForm f)
        {
            // 新窗口一般就落在当前桌面；拿不到 GUID 时不做任何搬运。
            Guid want = f.DesktopId;
            if (want == Guid.Empty) return;
            IVirtualDesktopManager m = GetVdm();
            if (m == null) return;

            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = 400;
            t.Tick += delegate
            {
                t.Stop();
                t.Dispose();
                try
                {
                    if (!f.IsHandleCreated || !f.Visible) return;
                    Guid now;
                    if (m.GetWindowDesktopId(f.Handle, out now) == 0 &&
                        now != Guid.Empty && now != want)
                    {
                        m.MoveWindowToDesktop(f.Handle, ref want);
                        Log("窗口搬回目标桌面 " + want.ToString().Substring(0, 8));
                    }
                }
                catch { }
            };
            t.Start();
        }

        private IVirtualDesktopManager GetVdm()
        {
            if (vdm != null) return vdm;
            if (vdmTried) return null;
            vdmTried = true;
            try
            {
                Type t = Type.GetTypeFromCLSID(new Guid(Guids.CLSID_VirtualDesktopManager));
                vdm = (IVirtualDesktopManager)Activator.CreateInstance(t);
                Log("虚拟桌面接口已就绪");
            }
            catch (Exception ex)
            {
                Log("虚拟桌面接口不可用: " + ex.Message);
                vdm = null;
            }
            return vdm;
        }

        // ==================================================================
        // 会话：按虚拟桌面分别记忆
        // ==================================================================
        private sealed class SessionEntry
        {
            public Guid Desktop = Guid.Empty;
            public int Active = 0;
            public int X = 100, Y = 100, W = 1100, H = 700;
            public bool Maximized = false;
            public List<string> Tabs = new List<string>();
        }

        private void RestoreInto(MainForm f, SessionEntry e)
        {
            try
            {
                if (e.W > 200 && e.H > 150)
                {
                    Rectangle r = Screen.GetWorkingArea(new Point(e.X, e.Y));
                    f.Bounds = new Rectangle(
                        Math.Max(r.Left - 8, Math.Min(e.X, r.Right - 100)),
                        Math.Max(r.Top, Math.Min(e.Y, r.Bottom - 100)),
                        Math.Min(e.W, r.Width), Math.Min(e.H, r.Height));
                }
                if (e.Maximized) f.WindowState = FormWindowState.Maximized;
            }
            catch { }

            foreach (string p in e.Tabs) f.NewTab(p, false);
            if (e.Tabs.Count == 0) f.OpenInNewTab(ExplorerView.ThisPcPath);
            if (e.Active >= 0 && e.Active < e.Tabs.Count) f.ActivateTab(e.Active);
        }

        private SessionEntry TakeSession(Guid desktop)
        {
            if (desktop != Guid.Empty)
            {
                for (int i = 0; i < loadedSessions.Count; i++)
                {
                    if (!consumedSessions.Contains(i) && loadedSessions[i].Desktop == desktop)
                    {
                        consumedSessions.Add(i);
                        return loadedSessions[i];
                    }
                }
            }
            // 桌面 GUID 变了（重启后常见）：按记录顺序消费
            for (int i = 0; i < loadedSessions.Count; i++)
            {
                if (!consumedSessions.Contains(i))
                {
                    consumedSessions.Add(i);
                    return loadedSessions[i];
                }
            }
            return null;
        }

        private void LoadSessions()
        {
            loadedSessions.Clear();
            try
            {
                if (!File.Exists(sessionFile)) return;
                SessionEntry cur = null;
                foreach (string line in File.ReadAllLines(sessionFile))
                {
                    if (line.StartsWith("DESKTOP ", StringComparison.Ordinal))
                    {
                        cur = new SessionEntry();
                        loadedSessions.Add(cur);
                        string[] parts = line.Split('\t');
                        if (parts.Length > 1)
                        {
                            Guid g;
                            if (Guid.TryParse(parts[1], out g)) cur.Desktop = g;
                        }
                        if (parts.Length > 2) int.TryParse(parts[2], out cur.Active);
                        if (parts.Length > 6)
                        {
                            int.TryParse(parts[3], out cur.X);
                            int.TryParse(parts[4], out cur.Y);
                            int.TryParse(parts[5], out cur.W);
                            int.TryParse(parts[6], out cur.H);
                        }
                        if (parts.Length > 7) cur.Maximized = parts[7] == "1";
                    }
                    else if (line.StartsWith("TAB ", StringComparison.Ordinal) && cur != null)
                    {
                        cur.Tabs.Add(line.Substring(4));
                    }
                }
            }
            catch (Exception ex)
            {
                Log("读取会话失败: " + ex.Message);
            }
        }

        private void ScheduleSave()
        {
            if (saveTimer == null)
            {
                saveTimer = new System.Windows.Forms.Timer();
                saveTimer.Interval = 1500;
                saveTimer.Tick += delegate { saveTimer.Stop(); SaveSessions(); };
            }
            saveTimer.Stop();
            saveTimer.Start();
        }

        private void SaveSessions()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(sessionFile));
                lock (sync)
                {
                    // 1) 活着的窗口 → 更新进会话表（已经关掉的窗口之前被 CaptureForm 存过，别覆盖）
                    foreach (MainForm f in forms)
                    {
                        if (!f.Visible && f.WindowState != FormWindowState.Minimized) continue;
                        CaptureForm(f);
                    }

                    // 2) 写整张表，而不是写 forms —— 这样已经关掉的窗口的状态也能留住
                    using (System.IO.StreamWriter w = new StreamWriter(sessionFile, false, System.Text.Encoding.UTF8))
                    {
                        foreach (SessionEntry e in loadedSessions)
                        {
                            if (e.Desktop == Guid.Empty || e.Tabs.Count == 0) continue;
                            w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                                "DESKTOP\t{0}\t{1}\t{2}\t{3}\t{4}\t{5}\t{6}",
                                e.Desktop, e.Active, e.X, e.Y, e.W, e.H, e.Maximized ? 1 : 0));
                            foreach (string p in e.Tabs) w.WriteLine("TAB\t" + p);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log("保存会话失败: " + ex.Message);
            }
        }

        /// <summary>把某个窗口当前的几何 + 标签列表快照进会话表。</summary>
        private void CaptureForm(MainForm f)
        {
            try
            {
                bool max = f.WindowState == FormWindowState.Maximized;
                Rectangle b = max ? f.RestoreBounds : f.Bounds;
                if (b.Width <= 200 || b.Height <= 150) return;

                SessionEntry e = FindOrCreateEntry(f.DesktopId);
                e.X = b.X;
                e.Y = b.Y;
                e.W = b.Width;
                e.H = b.Height;
                e.Maximized = max;
                e.Active = f.ActiveTabIndex;

                e.Tabs.Clear();
                List<string> paths = f.SnapshotPaths();
                for (int i = 0; i < paths.Count; i++)
                {
                    if (!string.IsNullOrEmpty(paths[i])) e.Tabs.Add(paths[i]);
                }
            }
            catch { }
        }

        private SessionEntry FindOrCreateEntry(Guid desktop)
        {
            if (desktop != Guid.Empty)
            {
                for (int i = 0; i < loadedSessions.Count; i++)
                {
                    if (loadedSessions[i].Desktop == desktop) return loadedSessions[i];
                }
            }
            SessionEntry e = new SessionEntry();
            e.Desktop = desktop;
            loadedSessions.Add(e);
            return e;
        }

        private void Shutdown()
        {
            // 记一笔：下次日志里"最后一行是正常退出"和"日志戛然而止"就能区分开
            Log("正常退出");
            try
            {
                if (hookThreadContext != null) hookThreadContext.ExitThread();
                if (tray != null) { tray.Visible = false; tray.Dispose(); }
            }
            catch { }
            SaveSessions();
        }

        private static void Log(string s)
        {
            Diag.Log(s);
        }
    }
}
