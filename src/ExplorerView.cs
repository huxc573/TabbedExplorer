using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TabbedExplorer
{
    /// <summary>
    /// 一个标签页 = 一个 IExplorerBrowser 实例，宿主在一个 Panel 里。
    /// 文件列表、右键菜单、拖放、缩略图全部由 Windows shell 自己提供。
    /// </summary>
    internal sealed class ExplorerView : IDisposable
    {
        public const string ThisPcPath = "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}";

        private IExplorerBrowser browser;
        private ExplorerBrowserEventsSink sink;
        private IntPtr sinkPtr;
        private uint cookie;
        private bool created;
        private bool disposed;
        private bool themeLogged;

        private readonly List<string> history = new List<string>();
        private int historyIndex = -1;

        public Panel Host { get; private set; }
        public string CurrentPath { get; private set; }
        public string CurrentDisplayName { get; private set; }

        /// <summary>导航「意图」路径，在 Navigate() 里同步赋值。
        /// CurrentPath 要等 shell 回调才更新，做去重必须用这个，否则连按 Win+E 会漏判。</summary>
        public string TargetPath { get; private set; }

        /// <summary>导航完成（路径或标题变了）。</summary>
        public event EventHandler Navigated;

        public ExplorerView()
        {
            Host = new Panel();
            Host.Dock = DockStyle.None;
            Host.BackColor = System.Drawing.Color.White;
            Host.Resize += HostResize;
            Host.HandleCreated += HostHandleCreated;
            Host.Disposed += HostDisposed;
        }

        private void HostHandleCreated(object sender, EventArgs e)
        {
            // 关键：**不能**在句柄创建过程中初始化 shell 控件 —— 时机太早会以
            // 0x80131506（CLR 内部错误）直接终止进程，而且 try/catch 拦不住。
            // 推迟到当前消息处理完之后再建。
            try { Host.BeginInvoke(new Action(CreateBrowserDeferred)); }
            catch { CreateBrowserDeferred(); }
        }

        private void HostDisposed(object sender, EventArgs e)
        {
            Dispose();
        }

        private void CreateBrowserDeferred()
        {
            if (created || disposed || browser != null) return;
            created = true;

            try
            {
                Diag.Step("CreateBrowser: begin host=" + Host.Handle);
                Type t = Type.GetTypeFromCLSID(new Guid(Guids.CLSID_ExplorerBrowser));
                if (t == null)
                {
                    Diag.Log("CLSID_ExplorerBrowser 没注册");
                    created = false;
                    return;
                }
                browser = (IExplorerBrowser)Activator.CreateInstance(t);
                Diag.Step("CreateBrowser: coclass ok");

                RECT rc = new RECT(0, 0,
                    Math.Max(1, Host.ClientSize.Width), Math.Max(1, Host.ClientSize.Height));
                FOLDERSETTINGS fs = new FOLDERSETTINGS();
                fs.ViewMode = 4;      // FVM_DETAILS
                fs.Flags = 0;

                int hr = browser.Initialize(Host.Handle, ref rc, ref fs);
                Diag.Step("CreateBrowser: Initialize hr=0x" + hr.ToString("X8"));
                if (hr != 0)
                {
                    Marshal.ReleaseComObject(browser);
                    browser = null;
                    created = false;
                    return;
                }

                ExplorerBrowserOptions opts = ExplorerBrowserOptions.EBO_ALWAYSNAVIGATE;
                if (Program.ShowFrames) opts |= ExplorerBrowserOptions.EBO_SHOWFRAMES;
                browser.SetOptions(opts);
                Diag.Step("CreateBrowser: SetOptions ok opts=0x" + ((int)opts).ToString("X"));
                ApplyRect();

                sink = new ExplorerBrowserEventsSink(this);
                sinkPtr = Marshal.GetComInterfaceForObject(sink, typeof(IExplorerBrowserEvents));
                uint ck;
                hr = browser.Advise(sinkPtr, out ck);
                cookie = hr == 0 ? ck : 0;
                Diag.Step("CreateBrowser: Advise hr=0x" + hr.ToString("X8"));
            }
            catch (Exception ex)
            {
                Diag.Log("CreateBrowser 异常: " + ex);
                browser = null;
                created = false;
            }

            // 建浏览器期间挂起的导航，现在补做
            if (pendingTarget != null)
            {
                string t2 = pendingTarget;
                bool h2 = pendingHistory;
                pendingTarget = null;
                DoNavigate(t2, h2);
            }
        }

        private void HostResize(object sender, EventArgs e)
        {
            if (browser == null) return;
            ApplyRect();
        }

        private void ApplyRect()
        {
            IntPtr hdwp = IntPtr.Zero;
            RECT rc = new RECT(0, 0, Host.ClientSize.Width, Host.ClientSize.Height);
            if (rc.Right <= 0 || rc.Bottom <= 0)
            {
                rc = new RECT(0, 0, Math.Max(1, rc.Right), Math.Max(1, rc.Bottom));
            }
            browser.SetRect(ref hdwp, rc);
        }

        /// <summary>shell 视图的根窗口（SHELLDLL_DefView），找不到返回 0。</summary>
        public IntPtr ShellViewWindow
        {
            get
            {
                try { return WinFind.ByClass(Host.Handle, "SHELLDLL_DefView"); }
                catch { return IntPtr.Zero; }
            }
        }

        /// <summary>派发按键前要先聚焦的目标（shell 视图内部的 DirectUI 那一层）。</summary>
        public IntPtr FocusTarget
        {
            get
            {
                try
                {
                    IntPtr def = ShellViewWindow;
                    if (def == IntPtr.Zero) return Host.Handle;
                    IntPtr dui = WinFind.ByClass(def, "DirectUIHWND");
                    return dui != IntPtr.Zero ? dui : def;
                }
                catch { return Host.Handle; }
            }
        }

        /// <summary>
        /// 让 shell 自己的窗口跟着深浅色。
        /// 注意这不是「设一下外框主题」就完事：shell 的文件列表是它自己建的子窗口树
        /// （ExplorerBrowserControl → SHELLDLL_DefView → DirectUIHWND），
        /// 每个都得 AllowDarkModeForWindow + SetWindowTheme，靠 Theme.StyleShellTree 递归做。
        /// </summary>
        public void ApplyTheme()
        {
            try
            {
                Host.BackColor = Theme.Pane;
                DarkMode.AllowWindow(Host.Handle);
                Theme.StyleShellTree(Host.Handle);
            }
            catch { }
        }

        /// <summary>导航到目标（路径、"::{CLSID}" 形式、或 "shell:xxx"）。</summary>
        public void Navigate(string target, bool addToHistory)
        {
            TargetPath = target;
            if (browser == null)
            {
                // 浏览器还没建好（句柄刚创建 / 推迟中）：先记下来，
                // CreateBrowserDeferred 收尾时会补做这次导航。
                pendingTarget = target;
                pendingHistory = addToHistory;
                return;
            }
            DoNavigate(target, addToHistory);
        }

        private string pendingTarget;
        private bool pendingHistory;

        private void DoNavigate(string target, bool addToHistory)
        {
            TargetPath = target;
            Diag.Step("navigate -> " + target);
            try
            {
                IntPtr item = ShellUtil.ItemPtrFromPath(target);
                Diag.Step("navigate: item ok ptr=" + item);
                int hr = browser.BrowseToObject(item, 0);
                Diag.Step("navigate: BrowseToObject hr=0x" + hr.ToString("X8"));
                Marshal.Release(item);
            }
            catch (Exception ex)
            {
                Diag.Log("navigate 异常: " + ex.Message);
                return;
            }

            if (addToHistory)
            {
                if (historyIndex < history.Count - 1)
                {
                    history.RemoveRange(historyIndex + 1, history.Count - historyIndex - 1);
                }
                if (history.Count == 0 || history[history.Count - 1] != target)
                {
                    history.Add(target);
                    historyIndex = history.Count - 1;
                }
            }
        }

        public bool CanGoBack { get { return historyIndex > 0; } }
        public bool CanGoForward { get { return historyIndex >= 0 && historyIndex < history.Count - 1; } }

        public void GoBack()
        {
            if (!CanGoBack) return;
            historyIndex--;
            DoNavigate(history[historyIndex], false);
        }

        public void GoForward()
        {
            if (!CanGoForward) return;
            historyIndex++;
            DoNavigate(history[historyIndex], false);
        }

        /// <summary>上一级（Desktop 的父就是 This PC）。</summary>
        public void GoUp()
        {
            if (string.IsNullOrEmpty(CurrentPath)) return;
            IntPtr p = IntPtr.Zero;
            try
            {
                p = ShellUtil.ItemPtrFromPath(CurrentPath);
                if (p == IntPtr.Zero) return;

                object o = Marshal.GetObjectForIUnknown(p);
                IShellItem si = o as IShellItem;
                if (si == null) return;

                IShellItem parent;
                si.GetParent(out parent);
                if (parent == null) return;

                IntPtr psz;
                parent.GetDisplayName(SIGDN.DESKTOPABSOLUTEPARSING, out psz);
                if (psz == IntPtr.Zero) return;
                string path = Marshal.PtrToStringUni(psz);
                NativeMethods.CoTaskMemFree(psz);

                if (!string.IsNullOrEmpty(path)) DoNavigate(path, true);
            }
            catch (Exception ex)
            {
                Diag.Log("goup 异常: " + ex.Message);
            }
            finally
            {
                if (p != IntPtr.Zero) Marshal.Release(p);
            }
        }

        public void Refresh()
        {
            if (browser == null || string.IsNullOrEmpty(CurrentPath)) return;
            DoNavigate(CurrentPath, false);
        }

        /// <summary>由 sink 回调（导航完成）。</summary>
        internal void OnNavigationComplete(IntPtr pidl)
        {
            string path = ShellUtil.ParsingPathFromPidl(pidl);
            string display = ShellUtil.DisplayNameFromPidl(pidl);
            if (string.IsNullOrEmpty(display))
            {
                display = string.IsNullOrEmpty(path) ? "此电脑" : path;
            }

            bool changed = CurrentPath != path;
            CurrentPath = string.IsNullOrEmpty(path) ? ThisPcPath : path;
            TargetPath = CurrentPath;
            CurrentDisplayName = display;

            if (!themeLogged)
            {
                themeLogged = true;
                Diag.Log("shell 视图窗口: " + WinFind.Dump(Host.Handle, 14));
            }
            ApplyTheme();

            NavigatedHandler(changed);
        }

        private void NavigatedHandler(bool changed)
        {
            EventHandler h = Navigated;
            if (h == null) return;
            Control c = Host;
            if (c != null && c.IsHandleCreated && c.InvokeRequired)
            {
                try { c.BeginInvoke(new Action(delegate { h(this, EventArgs.Empty); })); }
                catch { }
            }
            else
            {
                h(this, EventArgs.Empty);
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try
            {
                if (browser != null)
                {
                    if (cookie != 0) { browser.Unadvise(cookie); cookie = 0; }
                    browser.Destroy();
                    Marshal.ReleaseComObject(browser);
                    browser = null;
                }
                if (sinkPtr != IntPtr.Zero) { Marshal.Release(sinkPtr); sinkPtr = IntPtr.Zero; }
            }
            catch { }
        }

        /// <summary>会话保存用：当前历史路径列表。</summary>
        public List<string> HistorySnapshot { get { return new List<string>(history); } }
    }

    /// <summary>
    /// IExplorerBrowserEvents 事件接收器。
    /// **4 个方法一个都不能少**（顺序与原生接口一致）：缺一个，CCW 的 vtable 就比原生
    /// 短，shell 回调到缺的槽位时会跳到野地址把进程打掉（0x80131506）。
    /// </summary>
    [ComVisible(true)]
    internal sealed class ExplorerBrowserEventsSink : IExplorerBrowserEvents
    {
        private readonly ExplorerView owner;
        public ExplorerBrowserEventsSink(ExplorerView owner) { this.owner = owner; }

        public int OnNavigationPending(IntPtr pidlFolder) { return 0; }

        /// <summary>shell 已建好 IShellView（这里不接管，直接放行）。</summary>
        public int OnViewCreated(IntPtr psv) { return 0; }

        public int OnNavigationComplete(IntPtr pidlFolder)
        {
            try { owner.OnNavigationComplete(pidlFolder); }
            catch { }
            return 0;
        }

        public int OnNavigationFailed(IntPtr pidlFolder)
        {
            Diag.Log("navigate: OnNavigationFailed");
            return 0;
        }
    }
}
