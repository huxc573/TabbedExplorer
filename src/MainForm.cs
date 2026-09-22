using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TabbedExplorer
{
    /// <summary>
    /// 一个窗口 = 一个虚拟桌面上的一组标签。
    /// 版式照着 Win10 资源管理器来：
    ///   标签条 → 功能区（文件/主页/共享/查看）→ 地址栏 → (导航窗格 | 文件列表) → 状态栏
    /// 文件列表本身是 Windows 官方的 shell 视图，右键菜单/拖放/缩略图都是原生的。
    /// </summary>
    internal sealed class MainForm : Form
    {
        public const uint WM_APP_OPEN = 0x8001;
        private const int WM_SETTINGCHANGE = 0x001A;

        private readonly TabStrip tabStrip;
        private readonly Ribbon ribbon;
        private readonly NavPane navPane;
        private readonly Panel addrRow;
        private readonly Button btnBack, btnForward, btnUp, btnRefresh;
        private readonly Breadcrumb crumb;
        private readonly TextBox searchBox;
        private readonly Panel viewArea;
        private readonly Panel content;
        private readonly Panel statusBar;
        private readonly Label statusLabel;
        private readonly ToolTip tip = new ToolTip();

        private readonly List<ExplorerView> views = new List<ExplorerView>();
        private int activeIndex = -1;

        /// <summary>本窗口归属的虚拟桌面 GUID（GUID.Empty = 未知）。</summary>
        public Guid DesktopId = Guid.Empty;

        /// <summary>Ctrl+N 请求新窗口；由 AppContext 挂上（这样新窗口也归它管、会被记住）。</summary>
        public static Action RequestNewWindow;

        public event EventHandler SessionDirty;

        public MainForm(IEnumerable<string> restorePaths, int restoreActive)
        {
            Text = "资源管理器";
            StartPosition = FormStartPosition.Manual;

            // 窗口尺寸要跟着 DPI 放大 —— 功能区按钮是硬编码像素宽度（而字体按 pt 会自己变大），
            // 窗口不放大就装不下那一排按钮，右边的组会被直接裁掉。
            float dpiScale = 1f;
            try
            {
                uint sysDpi = NativeMethods.GetDpiForSystem();
                if (sysDpi >= 72 && sysDpi <= 480) dpiScale = sysDpi / 96f;
            }
            catch { }

            Size = new Size((int)(1120 * dpiScale), (int)(720 * dpiScale));
            MinimumSize = new Size((int)(620 * dpiScale), (int)(420 * dpiScale));
            Font = new Font("Segoe UI", 9f);
            BackColor = Theme.Pane;
            KeyPreview = true;

            // 首开居中（以前固定落在左上角）；会话里有记录的话后面会被还原覆盖
            Rectangle wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(
                wa.Left + Math.Max(0, (wa.Width - Width) / 2),
                wa.Top + Math.Max(0, (wa.Height - Height) / 3));

            // ---------------- 标签条 ----------------
            tabStrip = new TabStrip();
            tabStrip.Dock = DockStyle.Top;
            tabStrip.TabClicked += delegate(object s, int i) { ActivateTab(i); };
            tabStrip.TabCloseClicked += delegate(object s, int i) { CloseTab(i); };
            tabStrip.TabMiddleClicked += delegate(object s, int i) { CloseTab(i); };
            tabStrip.NewTabClicked += delegate { NewTab(ExplorerView.ThisPcPath, true); };
            tabStrip.OrderChanged += delegate { SyncOrder(); };
            tabStrip.AllowDrop = true;
            tabStrip.DragEnter += OnDragEnter;
            tabStrip.DragDrop += OnDragDrop;

            // ---------------- 功能区 ----------------
            ribbon = new Ribbon();
            ribbon.Dock = DockStyle.Top;
            ribbon.Command += RibbonCommand;
            ribbon.SetPages(BuildPages());

            // ---------------- 地址栏行 ----------------
            addrRow = new Panel();
            addrRow.Dock = DockStyle.Top;
            addrRow.Height = 36;
            addrRow.Padding = new Padding(0);   // 里面全部手动摆位，别让 Padding 再叠一层

            btnBack = MakeIconButton("\uE72B", "后退 (Alt+←)");
            btnForward = MakeIconButton("\uE72A", "前进 (Alt+→)");
            btnUp = MakeIconButton("\uE74A", "上一级 (Alt+↑)");
            btnRefresh = MakeIconButton("\uE72C", "刷新 (F5)");
            btnBack.Click += delegate { ExplorerView v = Current; if (v != null) v.GoBack(); };
            btnForward.Click += delegate { ExplorerView v = Current; if (v != null) v.GoForward(); };
            btnUp.Click += delegate { ExplorerView v = Current; if (v != null) v.GoUp(); };
            btnRefresh.Click += delegate { ExplorerView v = Current; if (v != null) v.Refresh(); };

            crumb = new Breadcrumb();
            crumb.Navigate += delegate(object s, string p) { NavigateActive(p, true); };

            searchBox = new TextBox();
            searchBox.Width = 190;
            searchBox.BorderStyle = BorderStyle.FixedSingle;
            searchBox.Font = new Font("Segoe UI", 9.5f);
            searchBox.KeyDown += SearchKeyDown;
            searchBox.HandleCreated += delegate { SetCue(searchBox, "搜索"); };

            addrRow.Controls.Add(btnBack);
            addrRow.Controls.Add(btnForward);
            addrRow.Controls.Add(btnUp);
            addrRow.Controls.Add(crumb);
            addrRow.Controls.Add(btnRefresh);
            addrRow.Controls.Add(searchBox);
            addrRow.Resize += delegate { LayoutAddrRow(); };

            // ---------------- 内容：导航窗格 + 视图区 ----------------
            viewArea = new Panel();
            viewArea.Dock = DockStyle.Fill;
            viewArea.BackColor = Theme.Pane;
            viewArea.AllowDrop = true;
            viewArea.DragEnter += OnDragEnter;
            viewArea.DragDrop += OnDragDrop;

            navPane = new NavPane();
            navPane.Dock = DockStyle.Left;
            navPane.BackColor = Theme.NavBack;
            navPane.PathChosen += delegate(object s, string p) { NavigateActive(p, true); };
            // frames 模式下左侧那棵树由 shell 自己提供（带快速访问 pin、真实卷标、系统图标），
            // 自绘的就别显示了 —— 两个并存会变成并排两棵树。
            navPane.Visible = !Program.ShowFrames;

            content = new Panel();
            content.Dock = DockStyle.Fill;
            content.BackColor = Theme.Pane;
            content.Controls.Add(viewArea);
            content.Controls.Add(navPane);

            // ---------------- 状态栏 ----------------
            statusLabel = new Label();
            statusLabel.AutoSize = false;
            statusLabel.Dock = DockStyle.Fill;
            statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            statusLabel.Padding = new Padding(8, 0, 0, 0);

            statusBar = new Panel();
            statusBar.Dock = DockStyle.Bottom;
            statusBar.Height = 24;
            statusBar.Controls.Add(statusLabel);

            // 添加顺序决定停靠优先次序（后加的先停靠）：标签条在最上、状态栏在最下
            Controls.Add(content);
            Controls.Add(statusBar);
            Controls.Add(addrRow);
            Controls.Add(ribbon);
            Controls.Add(tabStrip);

            ApplyTheme();
            LayoutAddrRow();

            if (restorePaths != null)
            {
                foreach (string p in restorePaths) NewTab(p, false);
            }
            ActivateTab(Math.Max(0, Math.Min(restoreActive, views.Count - 1)));
        }

        private Button MakeIconButton(string glyph, string hint)
        {
            Button b = new Button();
            b.Text = glyph;
            b.Font = new Font("Segoe MDL2 Assets", 11f);
            b.Width = 30;
            b.Height = 26;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.TabStop = false;
            tip.SetToolTip(b, hint);
            return b;
        }

        private static void SetCue(TextBox box, string cue)
        {
            try { NativeMethods.SendMessageStr(box.Handle, 0x1501 /*EM_SETCUEBANNER*/, (IntPtr)1, cue); }
            catch { }
        }

        private void LayoutAddrRow()
        {
            int x = 6;
            int y = 5;
            Control[] nav = new Control[] { btnBack, btnForward, btnUp };
            for (int i = 0; i < nav.Length; i++)
            {
                nav[i].Location = new Point(x, y + 1);
                x += nav[i].Width + 1;
            }
            x += 6;

            int right = addrRow.ClientSize.Width - 6;
            searchBox.Location = new Point(right - searchBox.Width, y + 1);
            searchBox.Height = 26;
            btnRefresh.Location = new Point(searchBox.Left - btnRefresh.Width - 6, y + 1);

            int crumbW = Math.Max(120, btnRefresh.Left - 8 - x);
            crumb.Location = new Point(x, y);
            crumb.Size = new Size(crumbW, 26);
        }

        // ==================================================================
        // 功能区内容（分组照着 Win10 资源管理器的功能区来）
        // ==================================================================
        private static List<RibbonPage> BuildPages()
        {
            List<RibbonPage> pages = new List<RibbonPage>();

            RibbonPage home = new RibbonPage("主页");
            home.Add(new RibbonGroup("剪贴板")
                .Add(new RibbonButton("clip.cut", "剪切", "\uE8C6", null, false))
                .Add(new RibbonButton("clip.copy", "复制", "\uE8C8", null, false))
                .Add(new RibbonButton("clip.paste", "粘贴", "\uE77F", null, false))
                .Add(new RibbonButton("clip.path", "路径", "\uE8A5", null, false)));
            home.Add(new RibbonGroup("组织")
                .Add(new RibbonButton("org.delete", "删除", "\uE74D", null, false))
                .Add(new RibbonButton("org.rename", "重命名", "\uE70F", null, false))
                .Add(new RibbonButton("org.props", "属性", "\uE713", null, false)));
            home.Add(new RibbonGroup("新建")
                .Add(new RibbonButton("new.folder", "新建文件夹", "\uE8F4", null, false))
                .Add(new RibbonButton("new.text", "文本文档", "\uE7C3", null, false)));
            home.Add(new RibbonGroup("打开")
                .Add(new RibbonButton("open.item", "打开", "\uE8A7", null, false))
                .Add(new RibbonButton("open.parent", "上一级", "\uE898", null, false)));
            home.Add(new RibbonGroup("选择")
                .Add(new RibbonButton("sel.all", "全选", "\uE8B3", null, false)));
            home.Add(new RibbonGroup("共享")
                .Add(new RibbonButton("share.mail", "电子邮件", "\uE715", null, false))
                .Add(new RibbonButton("share.zip", "压缩", "\uE838", null, false))
                .Add(new RibbonButton("share.print", "打印", "\uE749", null, false)));
            pages.Add(home);

            RibbonPage share = new RibbonPage("共享");
            share.Add(new RibbonGroup("共享")
                .Add(new RibbonButton("share.mail", "电子邮件", "\uE715", null, false))
                .Add(new RibbonButton("share.zip", "压缩", "\uE838", null, false))
                .Add(new RibbonButton("share.print", "打印", "\uE749", null, false))
                .Add(new RibbonButton("share.burn", "刻录", "\uE8B2", null, false)));
            pages.Add(share);

            RibbonPage view = new RibbonPage("查看");
            if (!Program.ShowFrames)
            {
                view.Add(new RibbonGroup("窗格")
                    .Add(new RibbonButton("view.navpane", "导航窗格", "\uE700", null, false)));
            }
            view.Add(new RibbonGroup("布局")
                .Add(new RibbonButton("view.mode", "视图方式", "\uE8A9", null, true)));
            view.Add(new RibbonGroup("当前视图")
                .Add(new RibbonButton("view.sort", "排序方式", "\uE8CB", null, true))
                .Add(new RibbonButton("view.refresh", "刷新", "\uE72C", null, false)));
            view.Add(new RibbonGroup("显示/隐藏")
                .Add(new RibbonButton("view.checkbox", "复选框", "\uE73E", null, false))
                .Add(new RibbonButton("view.ext", "扩展名", "\uE7C3", null, false))
                .Add(new RibbonButton("view.hidden", "隐藏项", "\uE7B3", null, false)));
            pages.Add(view);

            return pages;
        }

        // ==================================================================
        // 命令分发
        // ==================================================================
        private void RibbonCommand(object sender, string id, Point screen)
        {
            ExplorerView v = Current;
            IntPtr target = v == null ? IntPtr.Zero : v.FocusTarget;

            try
            {
                switch (id)
                {
                    case "file.menu":
                        ShowFileMenu(screen);
                        return;

                    case "clip.cut": ShellActions.Cut(target); return;
                    case "clip.copy": ShellActions.Copy(target); return;
                    case "clip.paste": ShellActions.Paste(target); return;
                    case "clip.path":
                        int n = ShellActions.CopySelectedPaths(target);
                        SetStatus(n > 0 ? "已复制 " + n + " 项路径" : "没有选中项");
                        return;

                    case "org.delete": ShellActions.Delete(target); return;
                    case "org.rename": ShellActions.Rename(target); return;
                    case "org.props": ShellActions.Properties(target); return;

                    case "new.folder": ShellActions.NewFolder(target); return;
                    case "new.text":
                        {
                            string dir = CurrentFolder(v);
                            if (dir == null) { SetStatus("当前位置不是普通文件夹"); return; }
                            string made = ShellActions.NewTextFile(dir);
                            if (made != null) ShellActions.Send(target, Keys.F5, false, false, false);
                            else SetStatus("新建文本文档失败");
                            return;
                        }

                    case "open.item": ShellActions.OpenItem(target); return;
                    case "open.parent": if (v != null) v.GoUp(); return;
                    case "sel.all": ShellActions.SelectAll(target); return;

                    case "share.mail": ShareVerb(v, "SendTo"); return;
                    case "share.print": ShareVerb(v, "print"); return;
                    case "share.burn": ShareVerb(v, "Burn"); return;
                    case "share.zip":
                        {
                            string dir = CurrentFolder(v);
                            if (dir == null) { SetStatus("当前位置不是普通文件夹"); return; }
                            string[] sel = SelectedPaths(target);
                            if (sel.Length == 0) { SetStatus("先选中要压缩的项目"); return; }
                            string zip = ShellActions.CompressToZip(sel, dir);
                            SetStatus(zip == null ? "压缩失败" : "已生成 " + Path.GetFileName(zip));
                            ShellActions.Send(target, Keys.F5, false, false, false);
                            return;
                        }

                    case "view.navpane":
                        navPane.Visible = !navPane.Visible;
                        SetStatus(navPane.Visible ? "已显示导航窗格" : "已隐藏导航窗格");
                        return;
                    case "view.mode": ShowViewModeMenu(screen); return;
                    case "view.sort": ShowSortMenu(screen); return;
                    case "view.refresh": if (v != null) v.Refresh(); return;

                    case "view.checkbox":
                        ShellActions.SetShowCheckBoxes(!ShellActions.ShowCheckBoxes);
                        SetStatus("项目复选框：" + (ShellActions.ShowCheckBoxes ? "开" : "关"));
                        if (v != null) v.Refresh();
                        return;
                    case "view.ext":
                        ShellActions.SetShowExtensions(!ShellActions.ShowExtensions);
                        SetStatus("文件扩展名：" + (ShellActions.ShowExtensions ? "显示" : "隐藏"));
                        if (v != null) v.Refresh();
                        return;
                    case "view.hidden":
                        ShellActions.SetShowHidden(!ShellActions.ShowHidden);
                        SetStatus("隐藏的项目：" + (ShellActions.ShowHidden ? "显示" : "隐藏"));
                        if (v != null) v.Refresh();
                        return;
                }
            }
            catch (Exception ex)
            {
                Diag.Log("功能区命令(" + id + ")异常: " + ex.Message);
                SetStatus("命令执行失败：" + ex.Message);
            }
        }

        private void ShareVerb(ExplorerView v, string verb)
        {
            string[] sel = SelectedPaths(v == null ? IntPtr.Zero : v.FocusTarget);
            if (sel.Length == 0) { SetStatus("先选中一个项目"); return; }
            if (!ShellVerbs.Invoke(verb, sel[0])) SetStatus("这个动词系统不支持：" + verb);
        }

        private string CurrentFolder(ExplorerView v)
        {
            if (v == null) return null;
            string p = v.CurrentPath;
            if (string.IsNullOrEmpty(p)) return null;
            if (p.Length >= 2 && p[1] == ':' && Directory.Exists(p)) return p;
            if (p.StartsWith("\\\\", StringComparison.Ordinal) && Directory.Exists(p)) return p;
            return null;
        }

        /// <summary>取当前选中项的路径：借剪贴板拿（和资源管理器「复制路径」同一个后果）。</summary>
        private static string[] SelectedPaths(IntPtr target)
        {
            try
            {
                IDataObject old = null;
                try { old = Clipboard.GetDataObject(); } catch { }
                ShellActions.Copy(target);
                System.Threading.Thread.Sleep(60);
                ShellActions.Pump(4);

                string[] files = null;
                try
                {
                    IDataObject o = Clipboard.GetDataObject();
                    if (o != null && o.GetDataPresent(DataFormats.FileDrop))
                        files = o.GetData(DataFormats.FileDrop) as string[];
                }
                catch { }

                if (old != null) { try { Clipboard.SetDataObject(old, true); } catch { } }
                return files == null ? new string[0] : files;
            }
            catch { return new string[0]; }
        }

        private void ShowFileMenu(Point p)
        {
            ContextMenuStrip m = new ContextMenuStrip();
            m.ShowImageMargin = false;
            AddMenuItem(m, "打开新窗口", "Ctrl+N", "file.newwindow");
            AddMenuItem(m, "在新标签页中打开", "Ctrl+T", "file.newtab");
            m.Items.Add(new ToolStripSeparator());
            AddMenuItem(m, "打开 Windows PowerShell", null, "file.powershell");
            m.Items.Add(new ToolStripSeparator());
            AddMenuItem(m, "更改文件夹和搜索选项", null, "file.options");
            AddMenuItem(m, "帮助", null, "file.help");
            m.Items.Add(new ToolStripSeparator());
            AddMenuItem(m, "关闭", "Ctrl+W", "file.close");
            Theme.StyleMenu(m);
            m.Closed += delegate { m.Dispose(); };
            m.Show(p);
        }

        private void AddMenuItem(ContextMenuStrip m, string text, string shortcut, string id)
        {
            ToolStripMenuItem it = new ToolStripMenuItem(text);
            if (!string.IsNullOrEmpty(shortcut)) it.ShortcutKeyDisplayString = shortcut;
            it.Click += delegate { RunFileCommand(id); };
            m.Items.Add(it);
        }

        private void RunFileCommand(string id)
        {
            ExplorerView v = Current;
            try
            {
                switch (id)
                {
                    case "file.newwindow":
                        if (RequestNewWindow != null) RequestNewWindow();
                        else
                        {
                            MainForm f = new MainForm(null, 0);
                            f.OpenInNewTab(ExplorerView.ThisPcPath);
                            f.Show();
                        }
                        return;
                    case "file.newtab":
                        NewTab(ExplorerView.ThisPcPath, true);
                        return;
                    case "file.powershell":
                        {
                            string dir = CurrentFolder(v);
                            if (dir == null) dir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                            System.Diagnostics.ProcessStartInfo psi =
                                new System.Diagnostics.ProcessStartInfo("powershell.exe");
                            psi.WorkingDirectory = dir;
                            psi.UseShellExecute = true;
                            System.Diagnostics.Process.Start(psi);
                            return;
                        }
                    case "file.options":
                        System.Diagnostics.Process.Start("control.exe", "folders");
                        return;
                    case "file.help":
                        System.Diagnostics.Process.Start("https://support.microsoft.com/windows");
                        return;
                    case "file.close":
                        CloseTab(activeIndex);
                        return;
                }
            }
            catch (Exception ex)
            {
                Diag.Log("文件菜单(" + id + ")异常: " + ex.Message);
                SetStatus("执行失败：" + ex.Message);
            }
        }

        private void ShowViewModeMenu(Point p)
        {
            ContextMenuStrip m = new ContextMenuStrip();
            m.ShowImageMargin = false;
            string[] names = new string[] { "超大图标", "大图标", "中图标", "小图标", "列表", "详细信息", "平铺", "内容" };
            for (int i = 0; i < names.Length; i++)
            {
                int mode = i;
                ToolStripMenuItem it = new ToolStripMenuItem(names[i]);
                it.Click += delegate
                {
                    ExplorerView cur = Current;
                    if (cur != null) ShellActions.SetViewMode(cur.FocusTarget, mode);
                };
                m.Items.Add(it);
            }
            Theme.StyleMenu(m);
            m.Closed += delegate { m.Dispose(); };
            m.Show(p);
        }

        private void ShowSortMenu(Point p)
        {
            ContextMenuStrip m = new ContextMenuStrip();
            m.ShowImageMargin = false;
            string[] names = new string[] { "名称", "修改日期", "类型", "大小" };
            for (int i = 0; i < names.Length; i++)
            {
                int col = i;
                ToolStripMenuItem it = new ToolStripMenuItem(names[i]);
                it.Click += delegate
                {
                    ExplorerView cur = Current;
                    if (cur == null) return;
                    if (!ShellActions.SortByColumn(cur.Host.Handle, col))
                        SetStatus("排序失败（先切到「详细信息」视图再试）");
                };
                m.Items.Add(it);
            }
            Theme.StyleMenu(m);
            m.Closed += delegate { m.Dispose(); };
            m.Show(p);
        }

        private void SetStatus(string s)
        {
            statusLabel.Text = s;
        }

        // ==================================================================
        // 标签管理
        // ==================================================================
        public void NewTab(string path, bool activate)
        {
            ExplorerView v = new ExplorerView();
            v.Host.Dock = DockStyle.Fill;
            v.Host.Visible = false;
            v.Navigated += ViewNavigated;
            viewArea.Controls.Add(v.Host);
            views.Add(v);
            tabStrip.AddTab("新标签页");

            v.Navigate(path, true);

            if (activate) ActivateTab(views.Count - 1);
            else if (activeIndex < 0) ActivateTab(views.Count - 1);
            MarkDirty();
        }

        public void CloseTab(int index)
        {
            if (index < 0 || index >= views.Count) return;
            ExplorerView v = views[index];
            v.Navigated -= ViewNavigated;
            viewArea.Controls.Remove(v.Host);
            v.Dispose();
            views.RemoveAt(index);
            tabStrip.RemoveTab(index);

            if (views.Count == 0)
            {
                Close();
                return;
            }
            if (activeIndex > index) activeIndex--;
            ActivateTab(Math.Max(0, Math.Min(activeIndex, views.Count - 1)));
            MarkDirty();
        }

        public void ActivateTab(int index)
        {
            if (index < 0 || index >= views.Count) return;
            activeIndex = index;
            for (int i = 0; i < views.Count; i++) views[i].Host.Visible = (i == index);
            tabStrip.SetActive(index);
            ExplorerView v = views[index];
            v.Host.BringToFront();
            UpdateChrome(v);
            if (v.Host.IsHandleCreated) v.Host.Focus();
        }

        /// <summary>找已经停在该路径上的标签；-1 = 没找到。忽略大小写与末尾反斜杠。</summary>
        public int IndexOfPath(string path)
        {
            string key = NormKey(path);
            if (key.Length == 0) return -1;
            for (int i = 0; i < views.Count; i++)
            {
                string vp = views[i].TargetPath;
                if (string.IsNullOrEmpty(vp)) vp = views[i].CurrentPath;
                if (NormKey(vp) == key) return i;
            }
            return -1;
        }

        private static string NormKey(string p)
        {
            if (string.IsNullOrEmpty(p)) return "";
            string s = p.Trim();
            while (s.Length > 3 && s[s.Length - 1] == '\\') s = s.Substring(0, s.Length - 1);
            return s.ToLowerInvariant();
        }

        private void SyncOrder()
        {
            ActivateTab(tabStrip.ActiveIndex >= 0 ? tabStrip.ActiveIndex : activeIndex);
            MarkDirty();
        }

        private void ViewNavigated(object sender, EventArgs e)
        {
            ExplorerView v = (ExplorerView)sender;
            int idx = views.IndexOf(v);
            if (idx >= 0)
            {
                string title = v.CurrentDisplayName;
                if (string.IsNullOrEmpty(title)) title = v.CurrentPath;
                tabStrip.SetTitle(idx, title);
            }
            if (idx == activeIndex) UpdateChrome(v);
            MarkDirty();
        }

        private void UpdateChrome(ExplorerView v)
        {
            if (v == null) return;
            Text = string.IsNullOrEmpty(v.CurrentDisplayName) ? "资源管理器" : v.CurrentDisplayName;
            crumb.Path = v.CurrentPath;
            navPane.SyncTo(v.CurrentPath);
            btnBack.Enabled = v.CanGoBack;
            btnForward.Enabled = v.CanGoForward;
            btnUp.Enabled = !string.IsNullOrEmpty(v.CurrentPath);
            UpdateStatus(v);
        }

        private void UpdateStatus(ExplorerView v)
        {
            string s = "";
            string p = v == null ? null : v.CurrentPath;
            if (!string.IsNullOrEmpty(p))
            {
                string dir = null;
                if (p.Length >= 2 && p[1] == ':') dir = p;
                else if (p.StartsWith("\\\\", StringComparison.Ordinal)) dir = p;
                if (dir != null && Directory.Exists(dir))
                {
                    try { s = Directory.GetFileSystemEntries(dir).Length + " 个项目"; }
                    catch { }
                }
            }
            statusLabel.Text = s;
        }

        private void MarkDirty()
        {
            if (SessionDirty != null) SessionDirty(this, EventArgs.Empty);
        }

        // ==================================================================
        // 导航
        // ==================================================================
        private ExplorerView Current
        {
            get { return activeIndex >= 0 && activeIndex < views.Count ? views[activeIndex] : null; }
        }

        private void NavigateActive(string target, bool addToHistory)
        {
            string t = ResolveTarget(target);
            if (t == null)
            {
                MessageBox.Show(this, "打不开：" + target, "TabbedExplorer",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            ExplorerView v = Current;
            if (v == null) { NewTab(t, true); return; }

            // 这个文件夹已经在别的标签里开着 → 切过去，不重复开一份
            int hit = IndexOfPath(t);
            if (hit >= 0 && hit != activeIndex) { ActivateTab(hit); return; }

            v.Navigate(t, addToHistory);
        }

        private static string ResolveTarget(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            raw = raw.Trim();
            if (raw.StartsWith("::", StringComparison.Ordinal) ||
                raw.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) ||
                raw.StartsWith("search-ms:", StringComparison.OrdinalIgnoreCase))
            {
                return raw;
            }
            string expanded = Environment.ExpandEnvironmentVariables(raw);
            if (Directory.Exists(expanded) || File.Exists(expanded)) return expanded;
            if (Directory.Exists(raw) || File.Exists(raw)) return raw;
            if (raw.Length >= 2 && raw[1] == ':') return null;
            return expanded;
        }

        private void SearchKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            string q = searchBox.Text.Trim();
            if (q.Length == 0) return;
            ExplorerView v = Current;
            string loc = v == null ? "" : v.CurrentPath;
            string uri = "search-ms:query=" + Uri.EscapeDataString(q);
            if (!string.IsNullOrEmpty(loc) && loc.Length >= 2 && loc[1] == ':')
                uri += "&crumb=location:" + Uri.EscapeDataString(loc);
            NavigateActive(uri, true);
        }

        /// <summary>供外部（Win+E / 拖放 / 托盘）调用：打开某路径。
        /// 已经有标签停在该路径就切过去，不重复开。
        /// 例外：Ctrl+T、点"+"、功能区"新建标签页"走 NewTab，不去重。</summary>
        public void OpenInNewTab(string path)
        {
            string t = ResolveTarget(path);
            int hit = IndexOfPath(t == null ? path : t);
            if (hit >= 0) { ActivateTab(hit); return; }
            NewTab(path, true);
        }

        public List<string> SnapshotPaths()
        {
            List<string> list = new List<string>();
            for (int i = 0; i < views.Count; i++)
            {
                string p = views[i].CurrentPath;
                if (string.IsNullOrEmpty(p)) p = ExplorerView.ThisPcPath;
                list.Add(p);
            }
            return list;
        }

        public int ActiveTabIndex { get { return activeIndex; } }
        public int TabCount { get { return views.Count; } }

        // ==================================================================
        // 主题
        // ==================================================================
        private void ApplyTheme()
        {
            BackColor = Theme.Pane;
            content.BackColor = Theme.Pane;
            viewArea.BackColor = Theme.Pane;
            addrRow.BackColor = Theme.Pane;
            statusBar.BackColor = Theme.StatusBack;
            statusLabel.ForeColor = Theme.TextDim;
            searchBox.BackColor = Theme.InputBack;
            searchBox.ForeColor = Theme.Text;

            Button[] btns = new Button[] { btnBack, btnForward, btnUp, btnRefresh };
            for (int i = 0; i < btns.Length; i++)
            {
                btns[i].BackColor = Theme.Pane;
                btns[i].ForeColor = Theme.Text;
                btns[i].FlatAppearance.MouseOverBackColor = Theme.Hover;
                btns[i].FlatAppearance.MouseDownBackColor = Theme.Press;
            }
            crumb.ApplyTheme();
            navPane.ApplyTheme();
            ribbon.Invalidate();
            tabStrip.Invalidate();
            // 进程级深色开关（切换到浅色时要设回去，否则一直停留在上次的深色）
            DarkMode.SetEnabled(Theme.IsDark);
            if (IsHandleCreated)
            {
                DarkMode.AllowWindow(Handle);
                Theme.ApplyTitleBar(Handle);
            }
            for (int i = 0; i < views.Count; i++)
            {
                views[i].ApplyTheme();
                views[i].Host.Invalidate(true);
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // 别在构造函数里强建句柄：保持“窗体 Show 时才真正开 shell 视图”这个已验证的时序
            Theme.ApplyTitleBar(Handle);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_SETTINGCHANGE && m.LParam != IntPtr.Zero)
            {
                string what = null;
                try { what = Marshal.PtrToStringUni(m.LParam); }
                catch { }
                if (what == "ImmersiveColorSet")
                {
                    Theme.Reload();
                    Diag.Log("系统主题切换 → " + (Theme.IsDark ? "深色" : "浅色"));
                    ApplyTheme();
                }
            }
            else if (m.Msg == WM_APP_OPEN)
            {
                OpenInNewTab(ExplorerView.ThisPcPath);
                if (NativeMethods.IsIconic(Handle)) NativeMethods.ShowWindow(Handle, 9);
                NativeMethods.SetForegroundWindow(Handle);
                return;
            }
            base.WndProc(ref m);
        }

        // ==================================================================
        // 拖放
        // ==================================================================
        private void OnDragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
        }

        private void OnDragDrop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files == null || files.Length == 0) return;

            foreach (string f in files)
            {
                string target = Directory.Exists(f) ? f : Path.GetDirectoryName(f);
                if (!string.IsNullOrEmpty(target)) OpenInNewTab(target);
            }
        }

        // ==================================================================
        // 快捷键
        // ==================================================================
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            const int WM_KEYDOWN = 0x0100;
            if (msg.Msg == WM_KEYDOWN)
            {
                Keys k = keyData & Keys.KeyCode;
                bool ctrl = (keyData & Keys.Control) == Keys.Control;
                bool shift = (keyData & Keys.Shift) == Keys.Shift;
                bool alt = (keyData & Keys.Alt) == Keys.Alt;

                if (ctrl && !alt && !shift && k == Keys.T) { NewTab(ExplorerView.ThisPcPath, true); return true; }
                if (ctrl && !alt && !shift && k == Keys.W) { CloseTab(activeIndex); return true; }
                if (ctrl && !alt && k == Keys.Tab)
                {
                    if (views.Count == 0) return true;
                    int next = shift ? activeIndex - 1 : activeIndex + 1;
                    if (next < 0) next = views.Count - 1;
                    if (next >= views.Count) next = 0;
                    ActivateTab(next);
                    return true;
                }
                if (ctrl && !alt && !shift && k >= Keys.D1 && k <= Keys.D9)
                {
                    int idx = (int)k - (int)Keys.D1;
                    if (idx < views.Count) { ActivateTab(idx); return true; }
                }
                if (ctrl && !alt && !shift && k == Keys.L) { crumb.BeginEdit(); return true; }
                if (ctrl && !alt && !shift && k == Keys.N)
                {
                    if (RequestNewWindow != null) RequestNewWindow();
                    else
                    {
                        MainForm f = new MainForm(null, 0);
                        f.OpenInNewTab(ExplorerView.ThisPcPath);
                        f.Show();
                    }
                    return true;
                }
                if (!ctrl && !shift && k == Keys.F5)
                {
                    ExplorerView v = Current;
                    if (v != null) v.Refresh();
                    return true;
                }
                if (alt && !ctrl && k == Keys.Left) { ExplorerView v = Current; if (v != null) v.GoBack(); return true; }
                if (alt && !ctrl && k == Keys.Right) { ExplorerView v = Current; if (v != null) v.GoForward(); return true; }
                if (alt && !ctrl && k == Keys.Up) { ExplorerView v = Current; if (v != null) v.GoUp(); return true; }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // 深色标题栏要在窗口真正显示之后再设一次 —— OnHandleCreated 时 DWM 还没合成这个
            // 窗口，那时的调用会被丢掉，标题栏就一直是白的。
            Theme.ApplyTitleBar(Handle);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && views.Count > 0)
            {
                MarkDirty();
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            foreach (ExplorerView v in views) v.Dispose();
            views.Clear();
            base.OnFormClosed(e);
        }
    }
}
