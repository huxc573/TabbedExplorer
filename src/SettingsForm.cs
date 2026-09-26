using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace TabbedExplorer
{
    /// <summary>
    /// 程序自己的名号 —— 给设置窗口和日志用。
    ///
    /// ⚠ 版本号的**唯一来源是仓库根的 `VERSION` 文件**（`AppInfo.Version` 就是现读它）；
    /// 程序里**不要再写死一个版本字符串**，否则改版本时必然漏一处。
    /// 读不到（比如只拷了 exe）才退回程序集版本。
    /// </summary>
    internal static class AppInfo
    {
        public const string Name = "TabbedExplorer";

        /// <summary>作者（署名）。git 提交身份、LICENSE 里的版权人、这个常量是同一个名字。</summary>
        public const string Author = "huxc573";

        /// <summary>
        /// 开源地址 —— 设置窗口里显示成可点的一行，点了交给系统默认浏览器。
        /// ⚠ 改地址只改这一处（README 顶部也写着同一个地址，一起改）。
        /// </summary>
        public const string RepoUrl = "https://github.com/huxc573/TabbedExplorer";

        public const string Blurb =
            "给Win10资源管理器加标签页 —— 像浏览器一样用资源管理器。\n"
          + "标签里那套文件列表 / 右键菜单 / 拖放 / 缩略图都是Windows原版的，不是仿制界面。\n"
          + "绿色便携：设置、标签记忆、历史全在程序目录的 data\\ 下，拷走整个文件夹就带走。";

        /// <summary>版本号（读程序目录下的 `VERSION`，单行）。</summary>
        public static string Version
        {
            get
            {
                try
                {
                    string dir = AppDomain.CurrentDomain.BaseDirectory;
                    if (!string.IsNullOrEmpty(dir))
                    {
                        string p = System.IO.Path.Combine(dir, "VERSION");
                        if (System.IO.File.Exists(p))
                        {
                            string[] lines = System.IO.File.ReadAllLines(p, System.Text.Encoding.UTF8);
                            if (lines.Length > 0 && lines[0].Trim().Length > 0) return lines[0].Trim();
                        }
                    }
                }
                catch { }
                try
                {
                    System.Version v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                    return v == null ? "?" : v.ToString(3);
                }
                catch { return "?"; }
            }
        }
    }

    /// <summary>
    /// 独立的设置窗口（用户要的）—— 齿轮按钮、标签条空白右键、托盘「更多选项…」开的都是它。
    /// 托盘图标右键那份菜单**保持原样**（用户明确要求）。
    ///
    /// 窗口内容**不另写一份**：设置项全部由 `SettingsMenu.Spec(hub)` 生成 —— 托盘那份菜单用的也是同一份规格，
    /// 所以以后加设置项不会漏一边（当初「托盘右键没有设置选项」就是两边各写一遍漏出来的）。
    /// 这里只是换一种渲染方式：
    ///   · 同 `Group` 的相邻叶子 → 一组单选按钮（捕获方式 / 颜色模式）
    ///   · 单独的叶子 → 勾选框
    ///   · 数值项（`NumMax &gt; 0`）→ 可自己敲的输入框（标签页宽度）
    ///   · `Checked == null` 且没有动作 → 一行说明文字
    /// 顶部放**程序名 / 版本 / 简介**。
    ///
    ///第二批（用户）：
    ///   · 分成两个 Tab —— **常规**（原来那些设置项）+ **快捷键**（程序自有热键可自己改键）；
    ///   · 标题栏那条白杠：`DWM` 的深色属性以前只在 `OnShown` 里设一次，
    ///     而窗口第一次合成时早就把白框画出来了（切出去再切回来才重取）——
    ///     现在 `OnHandleCreated` 就设 + 每次 `WM_NCACTIVATE` 再补一枪 + 设完逼一次框架重算（见 Theme.ApplyTitleBar）。
    /// </summary>
    internal sealed class SettingsForm : Form
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

        private const int WM_NCACTIVATE = 0x0086;

        private readonly DesktopHub hub;
        /// <summary>「把界面上的值刷回真值」的动作（点完任何一项都要刷一遍，单选/勾选才跟得上）。</summary>
        private readonly List<Action> syncers = new List<Action>();
        /// <summary>正在程序化地改控件值 —— 期间别把事件当成「用户点的」。</summary>
        private bool syncing;
        private bool rebuilding;
        /// <summary>重建之后要补的那句提示（见 ApplyNode —— 重建会把提示行一起重建掉）。</summary>
        private string pendingNotice;
        /// <summary>订阅 Theme.Changed 的那个处理器（关窗时要退订，否则静态事件会把窗口吊着不放）。</summary>
        private EventHandler themeHandler;

        // ==================================================================
        // 「保存」这一套（用户：设置项用保存按钮，有变动在窗口标题提示，关窗时问一句）
        //
        // 口径：**这个窗口开着的期间，所有改动只改内存**（界面 / 功能照旧立刻生效），
        // 落盘要等用户点「保存」。做法是拿 `Settings.BeginHold()` 压住写盘闸（见 Settings 里注释），
        // 点「保存」时放闸写一次、再压回去。
        // ==================================================================

        /// <summary>开窗那一刻的设置快照 —— 判「有没有未保存的改动」和「不保存时回滚」都用它。</summary>
        private Settings.Snapshot baseline;
        /// <summary>`baseline` 序列化成的正文 —— 比「当前值序列化出来」就知道有没有改动。</summary>
        private string baselineJson;
        /// <summary>现在有还没保存的改动（窗口标题上那个提示就是它）。</summary>
        private bool dirty;
        /// <summary>我们正压着 `Settings` 的写盘闸。⚠ 关窗必须放掉，否则以后所有设置都写不进盘。</summary>
        private bool holdOn;
        /// <summary>正在「应用 / 回滚 / 保存」—— 这期间别去算 dirty（会把中间态当成改动）。</summary>
        private bool applying;
        /// <summary>程序要退了（`DesktopHub.Dispose` 顺手关这个窗）：别再弹「要不要保存」。</summary>
        public bool Quitting;

        /// <summary>两个 Tab（自绘 —— 系统画的 Tab 头在深色下是一块白，跟外壳两套皮）。</summary>
        private TabControl tabs;
        /// <summary>重建时保住当前选中的是哪一页（换颜色模式会整窗重建）。</summary>
        private int keepPage;
        /// <summary>底部那行提示（改键成功 / 撞车 / 非法）。</summary>
        private Label notice;
        /// <summary>改键页里那些捕获框：命令 → 控件（改完要刷文字）。</summary>
        private readonly List<KeyValuePair<string, KeyBox>> keyBoxes =
            new List<KeyValuePair<string, KeyBox>>();

        /// <summary>「更新日志」页里那个只读框（页大小变了要把它铺满，见 FitLogBox）。</summary>
        private RichTextBox logBox;
        /// <summary>日志页用到的几套字体 —— 建一次复用，别每行 new 一个。</summary>
        private Font logFont, logFontBold, logH1, logH2;
        /// <summary>「更新日志」那一页（切到它才去读文件、渲染，见 LoadLogIfNeeded）。</summary>
        private TabPage logPage;
        /// <summary>CHANGELOG.md 的原文 —— 每个窗口实例只读一次（读文件不贵，贵的是往框里逐段上样式）。</summary>
        private string logText;
        private bool logRead;
        /// <summary>当前这个只读框里已经填过内容了。换颜色模式会整窗重建、框也换一个，得再填一遍。</summary>
        private bool logFilled;
        /// <summary>页里只渲染**最新这几个版本**，剩下的给个链接（整份 700 多行，全渲染要上百毫秒）。</summary>
        private const int LogVersions = 3;
        private const string LogFullUrl = AppInfo.RepoUrl + "/blob/main/CHANGELOG.md";

        public SettingsForm(DesktopHub hub)
        {
            this.hub = hub;

            Text = AppInfo.Name + " 设置";
            AutoScaleMode = AutoScaleMode.None;   // 尺寸我们全部自己按 DPI 算，别再让它自动缩一遍
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.CenterParent;
            KeyPreview = true;
            Icon = ShellIcon.AppIcon(false);
            BackColor = Theme.Chrome;
            ForeColor = Theme.Text;
            Font = new Font("Segoe UI", Px(12), FontStyle.Regular, GraphicsUnit.Pixel);

            // 压住写盘闸（**必须在 `Build()` 之前**：建界面读到的值也要按「还没落盘」的口径来）。
            // 从这一刻起设置项只改内存，落盘等用户点「保存」——见 `SaveEdits` / `DiscardEdits`。
            baseline = Settings.Snap();
            baselineJson = Settings.ToJson();
            holdOn = true;
            Settings.BeginHold();

            Build();

            // 颜色模式改了：整窗重建一遍（比逐个控件换色靠谱，反正这个窗口很小、开着的概率也低）。
            // 用 BeginInvoke 推后一轮 —— 改颜色模式的点击就发生在**这个窗口的某个控件**的
            // 事件处理里，当场把那个控件 Dispose 掉不安全。
            themeHandler = delegate
            {
                try { if (!IsDisposed && !Disposing) BeginInvoke(new Action(Rebuild)); } catch { }
            };
            Theme.Changed += themeHandler;
        }

        // ==================================================================
        // 深色标题栏（用户报的「打开时那一条是白的，切出去切回来才对」）
        // ==================================================================
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // 尽可能早地设一次（窗口还没显示、更没合成）
            Theme.ApplyTitleBar(Handle);
        }

        protected override void WndProc(ref Message m)
        {
            // 每次激活都补一枪：DWM 是在窗口激活时才重取框架属性的，
            // 之前只在 OnShown 设一次 → 第一帧已经画成白的了，得等 Alt+Tab 回来才刷新。
            if (m.Msg == WM_NCACTIVATE) Theme.ApplyTitleBar(Handle);
            base.WndProc(ref m);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Theme.ApplyTitleBar(Handle);
        }

        /// <summary>
        /// 关窗前问一句：有没保存的改动就「保存 / 不保存 / 取消」，**没改动不打扰**
        /// （用户：「关闭时也询问是否保存、无变动不管」）。
        /// 「取消」= 什么都不做、窗口留着（点 × 或按 Esc 也算取消，见 `Confirm.AskSave`）。
        /// </summary>
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (Quitting)
            {
                // 程序自己在退，别在关机路上弹框（内存里的值反正也随进程一起没了）
                ReleaseHold();
                base.OnFormClosing(e);
                return;
            }
            if (dirty && !applying)
            {
                DialogResult r = Confirm.AskSave(this, AppInfo.Name + " 设置",
                    "有改动还没保存。\r\n\r\n" +
                    "「保存」= 存下来再关；\r\n" +
                    "「不保存」= 改回去（本次已经立刻生效的那几项也会退回来）；\r\n" +
                    "「取消」= 接着改。");
                if (r == DialogResult.Yes) SaveEdits();
                else if (r == DialogResult.No) DiscardEdits();
                else { e.Cancel = true; return; }      // 取消：别关
            }
            ReleaseHold();
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            ReleaseHold();       // 兜底：`OnFormClosing` 没走到也不能把闸一直压着
            if (themeHandler != null)
            {
                try { Theme.Changed -= themeHandler; } catch { }
                themeHandler = null;
            }
            base.OnFormClosed(e);
        }

        // ==================================================================
        private void Rebuild()
        {
            if (rebuilding || IsDisposed || Disposing) return;
            rebuilding = true;
            try
            {
                if (tabs != null && tabs.SelectedIndex >= 0) keepPage = tabs.SelectedIndex;
                syncers.Clear();
                keyBoxes.Clear();
                syncing = true;               // 拆控件会触发一堆事件，全当程序化的
                List<Control> old = new List<Control>();
                foreach (Control c in Controls) old.Add(c);
                Controls.Clear();
                for (int i = 0; i < old.Count; i++) { try { old[i].Dispose(); } catch { } }
                BackColor = Theme.Chrome;
                ForeColor = Theme.Text;
                Build();
                // 重建会把提示行一并重建掉，所以「点完要说一句」的那种提示得等建完再说
                if (pendingNotice != null) { string s = pendingNotice; pendingNotice = null; SetNotice(s); }
            }
            catch (Exception ex) { Diag.Log("设置窗口: 重建失败 " + ex.Message); }
            finally { rebuilding = false; syncing = false; }
        }

        private Label TextLabel(string text, int sizePx, bool bold)
        {
            Label l = new Label();
            l.AutoSize = false;
            l.BackColor = BackColor;
            l.ForeColor = ForeColor;
            l.Font = new Font("Segoe UI", sizePx, bold ? FontStyle.Bold : FontStyle.Regular,
                              GraphicsUnit.Pixel);
            l.Text = text;
            return l;
        }

        /// <summary>一条细分隔线（用 1px 高的 Panel 画，Label 的 BorderStyle 太粗）。</summary>
        private int AddRule(Control parent, int y, int w, int pad)
        {
            Panel p = new Panel();
            p.BackColor = Theme.Border;
            p.SetBounds(pad, y, w - pad * 2, Math.Max(1, Px(1)));
            parent.Controls.Add(p);
            return y + Math.Max(1, Px(1)) + Px(8);
        }

        private Button FlatButton(string text, int x, int y, int w, int h)
        {
            Button b = new Button();
            b.FlatStyle = FlatStyle.Flat;
            b.BackColor = Theme.Hover;
            b.ForeColor = ForeColor;
            b.FlatAppearance.BorderColor = Theme.Border;
            b.Font = Font;
            b.Text = text;
            b.SetBounds(x, y, w, h);
            return b;
        }

        // ==================================================================
        // 主布局
        // ==================================================================
        private void Build()
        {
            int pad = Px(18);
            int w = Px(620);
            int y = pad;

            // ---- 顶部：图标 + 程序名 + 版本 ----
            int iconSize = Px(48);
            int tx = pad;
            try
            {
                Icon ic = ShellIcon.AppIcon(false);
                if (ic != null)
                {
                    PictureBox pic = new PictureBox();
                    pic.Image = ic.ToBitmap();
                    pic.SizeMode = PictureBoxSizeMode.StretchImage;
                    pic.BackColor = BackColor;
                    pic.SetBounds(pad, y, iconSize, iconSize);
                    Controls.Add(pic);
                    tx = pad + iconSize + Px(14);
                }
            }
            catch { }

            Label name = TextLabel(AppInfo.Name, Px(24), true);
            name.SetBounds(tx, y + Px(2), w - tx - pad, Px(30));
            Controls.Add(name);

            // 版本 + 作者一行
            Label ver = TextLabel("版本 " + AppInfo.Version + " · 作者 " + AppInfo.Author, Px(12), false);
            ver.ForeColor = Theme.TextDim;
            ver.SetBounds(tx, y + Px(34), w - tx - pad, Px(18));
            Controls.Add(ver);

            // 开源地址：可点，点了开浏览器（用户：「设置界面增加作者和开源地址，地址可点击打开」）
            LinkLabel repo = new LinkLabel();
            repo.Text = AppInfo.RepoUrl;
            repo.Font = ver.Font;
            repo.AutoSize = false;
            repo.BackColor = BackColor;
            repo.LinkColor = Theme.Accent;
            repo.ActiveLinkColor = Theme.Accent;
            repo.VisitedLinkColor = Theme.Accent;
            repo.LinkBehavior = LinkBehavior.HoverUnderline;
            repo.SetBounds(tx, y + Px(53), w - tx - pad, Px(18));
            repo.LinkClicked += delegate { OpenRepo(); };
            Controls.Add(repo);

            y += Math.Max(iconSize, Px(76)) + Px(10);

            // ---- 简介 ----
            Label blurb = TextLabel(AppInfo.Blurb, Px(12), false);
            blurb.ForeColor = Theme.TextDim;
            int bw = w - pad * 2;
            Size bs = TextRenderer.MeasureText(AppInfo.Blurb, blurb.Font, new Size(bw, 0),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            blurb.SetBounds(pad, y, bw, Math.Max(Px(20), bs.Height + Px(6)));
            Controls.Add(blurb);
            y += blurb.Height + Px(10);

            // ---- 两个 Tab ----
            // 自绘：系统画的 Tab 头是浅色的，深色模式下跟外壳两套皮（跟菜单/标题栏一个道理）。
            int pageW = w - pad * 2 - Px(8);

            TabPage general = NewPage("常规");
            int hGeneral = BuildGeneral(general, pageW);
            TabPage keys = NewPage("快捷键");
            int hKeys = BuildHotkeys(keys, pageW);
            TabPage log = NewPage("更新日志");
            int hLog = BuildLog(log, pageW);

            tabs = new TabHost();
            tabs.Font = Font;
            tabs.Appearance = TabAppearance.FlatButtons;
            tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
            tabs.SizeMode = TabSizeMode.Fixed;
            tabs.ItemSize = new Size(Px(96), Px(30));
            tabs.Padding = new Point(Px(10), Px(4));
            tabs.BackColor = Theme.Chrome;
            tabs.ForeColor = ForeColor;
            tabs.DrawItem += OnDrawTab;
            tabs.Controls.Add(general);
            tabs.Controls.Add(keys);
            tabs.Controls.Add(log);
            // 「更新日志」页**切到它才读文件、才渲染**。
            // 用户报「打开设置窗口也挺慢的，是因为更新日志吗」—— 就是这儿：整份 700 多行逐段上样式
            // 要上百毫秒，而窗口一开用户看的是「常规」页，这一页白花的。
            tabs.SelectedIndexChanged += delegate { LoadLogIfNeeded(); };

            // 高度：内容要多高给多高，但**不许顶出屏幕**（屏幕高度够就全显示，不够就页内滚动）。
            // 「常规」页比「快捷键」页长得多的那种情况下也能看全（用户的屏幕不一定放得下 ~800 逻辑像素）。
            int footerH = Px(22) + Px(28) + pad;
            int work = Px(800);
            try { work = Screen.FromPoint(Cursor.Position).WorkingArea.Height; } catch { }
            // 整个窗口（含边框）不许顶出工作区 —— 用户报的「界面显示不全」：
            // 原来这里是 `Math.Max(Px(240), 剩余高度)`，屏幕一矮就把窗口顶到屏幕外，
            // 底下那截（提示行 / 关闭按钮）永远看不见。现在反过来：**窗口高度封顶**，放不下的交给页内滚动。
            int maxClient = Math.Max(Px(360), work - Px(72));
            // 窗口高度**封顶**（用户：「设置窗口越来越高了，给个合适的高度就行，里面本来就有滚动条」）：
            // 内容再长也不许把窗口撑高，多出来的交给页内滚动（每页都开着 AutoScroll）。
            // 屏幕比这个上限还矮的时候，才轮到屏幕说话。
            int capClient = Math.Min(Px(700), maxClient);
            int maxTabsH = Math.Max(Px(150), capClient - y - footerH);
            int wantTabsH = Math.Max(Math.Max(hGeneral, hKeys), hLog) + Px(14);
            int tabsH = Math.Min(wantTabsH, maxTabsH);
            // 两页**一律**允许滚动 —— 装得下时 WinForms 自己不会画出滚动条，不必再拿一个开关去赌
            // （用户那次就是「没加可滚动」）。
            general.AutoScroll = true;
            keys.AutoScroll = true;
            // 日志页自己那个只读框负责滚动（见 BuildLog）—— 别再叠一层页内滚动，两层会打架。
            log.AutoScroll = false;
            tabs.SetBounds(pad, y, w - pad * 2, tabsH);
            Controls.Add(tabs);

            // 页内那条滚动条是**系统画的**（`TabPage.AutoScroll`），永远按浅色画 ——
            // 深色模式下就是页面右边竖着的一条白（用户截图里那个）。
            // 唯一能让它跟着我们颜色模式走的地方是 uxtheme 的子应用名，见 `Theme.StyleScrollBar`。
            StylePageNative(general);
            StylePageNative(keys);
            StylePageNative(log);      // 递归进只读框，把它那条滚动条也刷成深色
            FitLogBox(log);            // 页面积定下来了，把只读框铺满

            if (keepPage >= 0 && keepPage < tabs.TabCount) tabs.SelectedIndex = keepPage;
            LoadLogIfNeeded();         // 重建时如果正停在这一页，得把它填回来
            y += tabsH + Px(8);

            // ---- 底部：提示行 + 保存 / 关闭 ----
            notice = TextLabel("", Px(11), false);
            notice.ForeColor = Theme.TextDim;
            notice.SetBounds(pad, y, w - pad * 2, Px(18));
            Controls.Add(notice);
            y += Px(22);

            Button close = FlatButton("关闭", w - pad - Px(90), y, Px(90), Px(28));
            close.Click += delegate { Close(); };
            Controls.Add(close);

            // 「保存」摆在「关闭」左边、用强调色当主按钮 —— 用户要的就是这一颗
            Button save = FlatButton("保存", w - pad - Px(90) * 2 - Px(8), y, Px(90), Px(28));
            save.BackColor = Theme.Accent;
            save.ForeColor = Color.White;
            save.FlatAppearance.BorderColor = Theme.Accent;
            save.Click += delegate { SaveEdits(); };
            Controls.Add(save);
            y += Px(28) + pad;

            ClientSize = new Size(w, y);

            SyncAll();
            RefreshTitle();      // 标题上的「有未保存的改动」也跟着重建刷一遍
        }

        /// <summary>
        /// 点「开源地址」→ 交给系统默认浏览器打开。
        /// ⚠ 必须 `UseShellExecute = true`：不然 `Process.Start` 会把 "https://…" 当成
        /// **可执行文件路径**去开，直接抛 Win32Exception（同书签栏双击 URL 那条路）。
        /// </summary>
        private void OpenRepo()
        {
            try
            {
                Diag.Step("设置窗口: 打开开源地址 " + AppInfo.RepoUrl);
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(AppInfo.RepoUrl) { UseShellExecute = true });
                SetNotice("已在浏览器里打开：" + AppInfo.RepoUrl);
            }
            catch (Exception ex) { SetNotice("打不开浏览器：" + ex.Message); }
        }

        private TabPage NewPage(string text)
        {
            TabPage p = new TabPage();
            p.Text = text;
            p.BackColor = Theme.Chrome;
            p.ForeColor = ForeColor;
            p.UseVisualStyleBackColor = false;
            return p;
        }

        /// <summary>
        /// 把这一页（连里面的控件）的原生主题刷成跟当前颜色模式一致 —— 页内滚动条就靠它。
        ///
        /// 为什么要连子控件一起：主题是会在父子窗口之间继承的，既然父级已经转成深色一套，
        /// 干脆把子控件也显式设上，免得出现「滚动条深了、勾选框还是浅的」这种半拉子状态。
        /// 调完系统主题会让窗口重画，所以这一步只放在 `Build()` 末尾（建完才刷，不重复刷）。
        /// </summary>
        private static void StylePageNative(Control c)
        {
            if (c == null) return;
            try
            {
                Theme.StyleScrollBar(c.Handle);
                foreach (Control k in c.Controls) StylePageNative(k);
            }
            catch { }
        }

        /// <summary>Tab 头自绘（平底 + 选中时底下一条强调色，跟浏览器那种一样）。</summary>
        private void OnDrawTab(object sender, DrawItemEventArgs e)
        {
            try
            {
                if (tabs == null || e.Index < 0 || e.Index >= tabs.TabCount) return;
                Rectangle r = tabs.GetTabRect(e.Index);
                bool sel = (e.Index == tabs.SelectedIndex);
                using (SolidBrush b = new SolidBrush(sel ? Theme.Chrome : Theme.TabBar))
                    e.Graphics.FillRectangle(b, r);
                Rectangle t = new Rectangle(r.Left, r.Top, r.Width, r.Height - Px(3));
                TextRenderer.DrawText(e.Graphics, tabs.TabPages[e.Index].Text, Font, t,
                    sel ? Theme.Text : Theme.TextDim,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPadding);
                if (sel)
                {
                    e.Graphics.FillRectangle(new SolidBrush(Theme.Accent),
                        new Rectangle(r.Left, r.Bottom - Px(3), r.Width, Math.Max(2, Px(3))));
                }
            }
            catch { }
        }

        // ==================================================================
        // 「更新日志」页：把程序目录里的 CHANGELOG.md 渲染进来
        // ==================================================================
        private const string ChangeLogName = "CHANGELOG.md";

        /// <summary>
        /// 把 `CHANGELOG.md` 读进一个只读框。**不做通用 markdown 解析** —— 只认这个文件真用到的那几种：
        /// `#`/`##` 标题、`- ` 列表、`**粗体**`、反引号。目标是「能顺眼地读」，不是当 markdown 阅读器。
        ///
        /// 版本号本来就是运行时读 `VERSION` 的，这里同样读文件：找不到（比如只把 exe 拷走了）
        /// 就显示一行提示，不去猜也别硬编码一份旧的进去。
        /// </summary>
        private int BuildLog(TabPage page, int w)
        {
            logFont = Font;
            logFontBold = new Font(Font, FontStyle.Bold);
            logH1 = new Font(Font.FontFamily, Px(18), FontStyle.Bold);
            logH2 = new Font(Font.FontFamily, Px(14), FontStyle.Bold);

            RichTextBox box = new RichTextBox();
            box.ReadOnly = true;
            box.BorderStyle = BorderStyle.None;
            box.BackColor = Theme.Chrome;
            box.ForeColor = Theme.Text;
            box.Font = logFont;
            box.WordWrap = true;
            box.ScrollBars = RichTextBoxScrollBars.Vertical;
            // 尾巴上那条「完整更新日志」的地址要能点 —— 点开交给系统默认浏览器（同「开源地址」那条路）
            box.DetectUrls = true;
            box.LinkClicked += delegate(object s, LinkClickedEventArgs e)
            {
                try
                {
                    Diag.Step("设置窗口: 打开更新日志链接 " + e.LinkText);
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(e.LinkText) { UseShellExecute = true });
                }
                catch (Exception ex) { SetNotice("打不开浏览器：" + ex.Message); }
            };
            box.TabStop = false;
            box.SetBounds(Px(12), Px(10), w - Px(24), Px(360));
            page.Controls.Add(box);
            logBox = box;
            logPage = page;
            logFilled = false;         // 新框是空的：等切到这一页（或重建时本来就在这页）再填
            page.Resize += delegate { FitLogBox(page); };
            return Px(400);
        }

        /// <summary>
        /// 切到「更新日志」页时才真去读、去渲染（设置窗口在「常规」页被打开时这一页不做任何事）。
        /// ⚠ 只能在这儿拦 —— 别改回「建窗口时顺手填一遍」，那正是「打开设置窗口慢」的来源。
        /// </summary>
        private void LoadLogIfNeeded()
        {
            if (logFilled || logBox == null) return;
            if (tabs == null || logPage == null || tabs.SelectedTab != logPage) return;
            logFilled = true;
            try { FillLog(logBox); }
            catch (Exception ex) { Diag.Log("设置窗口: 渲染更新日志失败 " + ex.Message); }
        }

        /// <summary>读 CHANGELOG.md（每个窗口实例只读一次）；读不到返回 null（调用方给提示 + 线上地址）。</summary>
        private string ChangeLogText()
        {
            if (logRead) return logText;
            logRead = true;
            try
            {
                string p = System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory ?? ".", ChangeLogName);
                if (System.IO.File.Exists(p))
                    logText = System.IO.File.ReadAllText(p, System.Text.Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Diag.Log("设置窗口: 读 " + ChangeLogName + " 失败 " + ex.Message);
                logText = null;
            }
            return logText;
        }

        /// <summary>把只读框铺满这一页（页内不滚动，滚动交给框自己那条）。</summary>
        private void FitLogBox(TabPage page)
        {
            if (logBox == null || page == null) return;
            try
            {
                int w = page.ClientSize.Width - Px(24);
                int h = page.ClientSize.Height - Px(20);
                if (w > Px(80) && h > Px(60)) logBox.SetBounds(Px(12), Px(10), w, h);
            }
            catch { }
        }

        private void FillLog(RichTextBox box)
        {
            string text = ChangeLogText();
            if (string.IsNullOrEmpty(text))
            {
                AppendSeg(box, "没找到 " + ChangeLogName + "。\n\n它应该和 TabbedExplorer.exe 放在同一个文件夹里。\n\n",
                    logFont, Theme.TextDim);
                AppendSeg(box, "完整更新日志：" + LogFullUrl + "\n", logFontBold, Theme.Accent);
                return;
            }

            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            int shown = 0;          // 已经渲染了几个版本
            bool more = false;      // 后面还有被截掉的
            foreach (string raw in lines)
            {
                string line = raw.TrimEnd();

                // 版本标题（`## [v1.14.0] - 日期`）：只认最新那几个，再多就停
                if (line.StartsWith("## ", StringComparison.Ordinal))
                {
                    if (shown >= LogVersions) { more = true; break; }
                    shown++;
                    AppendSeg(box, line.Substring(3).Trim() + "\n", logH2, Theme.Accent);
                    continue;
                }
                if (line.Length == 0) { AppendSeg(box, "\n", logFont, Theme.Text); continue; }

                if (line.StartsWith("# ", StringComparison.Ordinal))
                {
                    AppendSeg(box, line.Substring(2).Trim() + "\n", logH1, Theme.Text);
                    continue;
                }

                // 列表项统一换成「•」；缩进的那层也留着 —— 不然看不出哪几行归在上一条下面。
                string s = line.TrimStart();
                string lead = "";
                if (s.StartsWith("- ", StringComparison.Ordinal))
                {
                    lead = (line.Length > s.Length ? "    " : "") + "• ";
                    s = s.Substring(2);
                }
                AppendInline(box, lead + s + "\n");
            }

            // 尾巴：说清上面只是一截，并给出完整的地址（`DetectUrls` 会把它变成可点的链接）
            AppendSeg(box, "\n", logFont, Theme.Text);
            if (more)
                AppendSeg(box, "…上面只是最新 " + LogVersions + " 个版本，历史版本没往这儿搬。\n", logFont, Theme.TextDim);
            AppendSeg(box, "完整更新日志：" + LogFullUrl + "\n", logFontBold, Theme.Accent);
            try { box.SelectionStart = 0; box.SelectionLength = 0; } catch { }
        }

        /// <summary>按 `**粗体**` 和反引号切成几段，逐段上样式；标记不成对就整段当普通文字。</summary>
        private void AppendInline(RichTextBox box, string s)
        {
            int i = 0;
            while (i < s.Length)
            {
                int b = s.IndexOf("**", i, StringComparison.Ordinal);
                int c = s.IndexOf('`', i);
                int next = -1;
                bool bold = false;
                if (b >= 0 && (c < 0 || b <= c)) { next = b; bold = true; }
                else if (c >= 0) { next = c; }

                if (next < 0) { AppendSeg(box, s.Substring(i), logFont, Theme.Text); return; }

                string mark = bold ? "**" : "`";
                int close = s.IndexOf(mark, next + mark.Length, StringComparison.Ordinal);
                if (close < 0) { AppendSeg(box, s.Substring(i), logFont, Theme.Text); return; }

                if (next > i) AppendSeg(box, s.Substring(i, next - i), logFont, Theme.Text);
                string inner = s.Substring(next + mark.Length, close - next - mark.Length);
                AppendSeg(box, inner, bold ? logFontBold : logFont, bold ? Theme.Text : Theme.AccentDim);
                i = close + mark.Length;
            }
        }

        /// <summary>往只读框尾部追加一段带样式的文字。</summary>
        private static void AppendSeg(RichTextBox box, string s, Font f, Color c)
        {
            box.SelectionStart = box.TextLength;
            box.SelectionLength = 0;
            box.SelectionFont = f;
            box.SelectionColor = c;
            box.AppendText(s);
        }

        // ==================================================================
        // 「常规」页：设置项（唯一来源：SettingsMenu.Spec）
        // ==================================================================
        private int BuildGeneral(TabPage page, int w)
        {
            int pad = Px(10);
            int y = Px(8);
            int rowH = Px(26);
            int inner = w - pad * 2;

            List<SettingsMenu.Node> spec = SettingsMenu.Spec(hub);
            string curGroup = null;
            Panel groupBox = null;

            for (int i = 0; i < spec.Count; i++)
            {
                SettingsMenu.Node nd = spec[i];
                if (nd.Text == null) { curGroup = null; groupBox = null; y = AddRule(page, y, w, pad); continue; }

                // ---- 数值项（标签页宽度：可自己敲）----
                if (nd.NumMax > 0)
                {
                    curGroup = null; groupBox = null;
                    Label lab = TextLabel(nd.Text, Px(12), false);
                    lab.SetBounds(pad, y + Px(3), Px(300), Px(22));
                    page.Controls.Add(lab);

                    NumBox num = new NumBox();
                    num.Minimum = nd.NumMin;
                    num.Maximum = nd.NumMax;
                    num.Increment = Math.Max(1, nd.NumStep);
                    num.Font = Font;
                    num.BackColor = Theme.InputBack;
                    num.ForeColor = Theme.Text;
                    num.BorderStyle = BorderStyle.FixedSingle;
                    int nw = Px(90);
                    num.SetBounds(pad + Px(300) + Px(8), y, nw, Px(24));
                    int cur = nd.NumGet != null ? nd.NumGet() : nd.NumMin;
                    if (cur < nd.NumMin) cur = nd.NumMin;
                    if (cur > nd.NumMax) cur = nd.NumMax;
                    num.Value = cur;

                    SettingsMenu.Node node = nd;
                    num.ValueChanged += delegate
                    {
                        if (syncing || node.NumSet == null) return;
                        applying = true;
                        try { node.NumSet((int)num.Value); }
                        finally { applying = false; }
                        // ★ 数值框也要让标题上那个「有未保存的改动」跟上 ——
                        //   它走的是 `NumSet` 而不是 `ApplyNode`，原来这条路上就没刷标题
                        //   （用户报：「透明度和宽度那里，鼠标滚轮滑动变了，没提示变动」）。
                        RefreshTitle();
                    };
                    page.Controls.Add(num);
                    syncers.Add(delegate
                    {
                        if (node.NumGet == null) return;
                        int v = node.NumGet();
                        if (v < num.Minimum) v = (int)num.Minimum;
                        if (v > num.Maximum) v = (int)num.Maximum;
                        if (num.Value != v) num.Value = v;
                    });

                    // 右边跟一句「范围」，省得他敲了没反应不知道为什么
                    Label hint = TextLabel("（" + nd.NumMin + " ~ " + nd.NumMax + "，可直接输入）", Px(11), false);
                    hint.ForeColor = Theme.TextDim;
                    hint.SetBounds(pad + Px(300) + Px(8) + nw + Px(8), y + Px(3), inner - Px(300) - nw - Px(16), Px(20));
                    page.Controls.Add(hint);

                    y += rowH + Px(4);
                    continue;
                }

                // 带子项（现在 Spec 里已经没有这种了，留着兼容）：下拉框
                if (nd.Children != null && nd.Children.Count > 0)
                {
                    curGroup = null; groupBox = null;
                    Label lab = TextLabel(nd.Text, Px(12), false);
                    lab.SetBounds(pad, y + Px(3), Px(110), Px(22));
                    page.Controls.Add(lab);

                    ComboBox cb = new ComboBox();
                    cb.DropDownStyle = ComboBoxStyle.DropDownList;
                    cb.FlatStyle = FlatStyle.Flat;
                    cb.BackColor = Theme.InputBack;
                    cb.ForeColor = Theme.Text;
                    cb.Font = Font;
                    cb.SetBounds(pad + Px(116), y, w - pad * 2 - Px(116), Px(24));
                    for (int k = 0; k < nd.Children.Count; k++) cb.Items.Add(nd.Children[k].Text);

                    SettingsMenu.Node branch = nd;
                    cb.SelectedIndexChanged += delegate
                    {
                        if (syncing) return;
                        int sel = cb.SelectedIndex;
                        if (sel >= 0 && sel < branch.Children.Count) ApplyNode(branch.Children[sel]);
                    };
                    page.Controls.Add(cb);
                    syncers.Add(delegate
                    {
                        for (int k = 0; k < branch.Children.Count; k++)
                        {
                            if (branch.Children[k].Checked != null && branch.Children[k].Checked())
                            { if (cb.SelectedIndex != k) cb.SelectedIndex = k; break; }
                        }
                    });
                    y += rowH + Px(4);
                    continue;
                }

                // 同 Group 的相邻叶子 → 一组单选按钮（RadioButton 只跟**同一个父容器**里的互斥）
                if (nd.Group != null)
                {
                    if (nd.Group != curGroup || groupBox == null)
                    {
                        curGroup = nd.Group;
                        int cnt = 0;
                        for (int k = i; k < spec.Count && spec[k].Group == nd.Group; k++) cnt++;
                        groupBox = new Panel();
                        groupBox.BackColor = BackColor;
                        groupBox.SetBounds(pad, y, inner, cnt * rowH);
                        page.Controls.Add(groupBox);
                        y += cnt * rowH;
                    }

                    RadioButton rb = new RadioButton();
                    rb.FlatStyle = FlatStyle.Standard;
                    rb.BackColor = BackColor;
                    rb.ForeColor = ForeColor;
                    rb.Font = Font;
                    rb.Text = nd.Text;
                    rb.SetBounds(Px(4), groupBox.Controls.Count * rowH, inner - Px(8), rowH);
                    SettingsMenu.Node node = nd;
                    rb.CheckedChanged += delegate { if (!syncing && rb.Checked) ApplyNode(node); };
                    groupBox.Controls.Add(rb);
                    syncers.Add(delegate
                    {
                        bool v = node.Checked != null && node.Checked();
                        if (rb.Checked != v) rb.Checked = v;
                    });
                    continue;
                }

                curGroup = null; groupBox = null;

                // 说明行（不打勾、也没动作）
                if (nd.Checked == null && nd.Click == null)
                {
                    Label info = TextLabel(nd.Text, Px(11), false);
                    info.ForeColor = Theme.TextDim;
                    info.SetBounds(pad, y, inner, Px(18));
                    page.Controls.Add(info);
                    y += Px(22);
                    continue;
                }

                // 纯动作（「记住当前标签」）
                if (nd.Checked == null)
                {
                    Button b = FlatButton(nd.Text, pad, y, Px(320), Px(26));
                    SettingsMenu.Node node = nd;
                    b.Click += delegate { ApplyNode(node); };
                    page.Controls.Add(b);
                    y += rowH + Px(4);
                    continue;
                }

                // 普通勾选框
                CheckBox ck = new CheckBox();
                ck.FlatStyle = FlatStyle.Standard;
                ck.BackColor = BackColor;
                ck.ForeColor = ForeColor;
                ck.Font = Font;
                ck.Text = nd.Text;
                ck.SetBounds(Px(4), y, inner - Px(8), rowH);
                SettingsMenu.Node n2 = nd;
                ck.CheckedChanged += delegate
                {
                    if (syncing) return;
                    if (n2.Checked == null) return;
                    if (ck.Checked != n2.Checked()) ApplyNode(n2);   // 只在跟真值不一致时才算「点了一下」
                };
                page.Controls.Add(ck);
                syncers.Add(delegate
                {
                    bool v = n2.Checked != null && n2.Checked();
                    if (ck.Checked != v) ck.Checked = v;
                });
                y += rowH + Px(4);
            }

            return y + Px(6);
        }

        // ==================================================================
        // 「快捷键」页（用户新增）
        // ==================================================================
        private int BuildHotkeys(TabPage page, int w)
        {
            int pad = Px(10);
            int y = Px(8);
            int rowH = Px(30);
            int inner = w - pad * 2;

            Label head = TextLabel("点一下方框，然后直接按你要的组合键（至少要按住 Ctrl 或 Alt）。Esc 不参与。",
                                   Px(11), false);
            head.ForeColor = Theme.TextDim;
            head.SetBounds(pad, y, inner, Px(18));
            page.Controls.Add(head);
            y += Px(24);

            HotkeySpec[] specs = Hotkeys.Specs;
            for (int i = 0; i < specs.Length; i++)
            {
                HotkeySpec s = specs[i];

                Label lab = TextLabel(s.Label, Px(12), false);
                lab.SetBounds(pad, y + Px(4), Px(160), Px(22));
                page.Controls.Add(lab);

                KeyBox box = new KeyBox();
                box.FlatStyle = FlatStyle.Flat;
                box.BackColor = Theme.InputBack;
                box.ForeColor = ForeColor;
                box.FlatAppearance.BorderColor = Theme.Border;
                box.Font = Font;
                box.TextAlign = ContentAlignment.MiddleLeft;
                box.SetBounds(pad + Px(164), y, Px(190), Px(26));
                box.Text = s.Valid ? Hotkeys.Format(s.Vk, s.Ctrl, s.Shift, s.Alt) : "（无效：" + s.Raw + "）";
                string cmd = s.Cmd;
                box.KeyDown += delegate(object sender, KeyEventArgs e)
                {
                    e.SuppressKeyPress = true;
                    e.Handled = true;
                    if (Hotkeys.IsModifierKey(e.KeyCode)) return;      // 只按了修饰键 —— 等主键
                    if (e.KeyCode == Keys.Escape) return;               // Esc 不当快捷键
                    int vk; bool ctrl, shift, alt;
                    Hotkeys.Split(e.KeyData, out vk, out ctrl, out shift, out alt);
                    if (vk == 0) return;
                    string why;
                    bool ok;
                    applying = true;
                    try { ok = Hotkeys.Assign(cmd, vk, ctrl, shift, alt, out why); }
                    finally { applying = false; }
                    if (ok) SetNotice("已改为：" + Hotkeys.LabelOf(cmd) + " = " + Hotkeys.Format(vk, ctrl, shift, alt));
                    else SetNotice("没改成：" + why);
                    RefreshKeyBoxes();
                    RefreshTitle();
                };
                page.Controls.Add(box);
                keyBoxes.Add(new KeyValuePair<string, KeyBox>(cmd, box));

                Label hint = TextLabel("默认 " + s.Dflt, Px(11), false);
                hint.ForeColor = Theme.TextDim;
                hint.SetBounds(pad + Px(364), y + Px(4), inner - Px(364), Px(20));
                page.Controls.Add(hint);

                y += rowH;
            }

            y += Px(4);
            y = AddRule(page, y, w, pad);

            Button reset = FlatButton("全部恢复默认", pad, y, Px(140), Px(26));
            reset.Click += delegate
            {
                applying = true;
                try { Hotkeys.ResetAll(); } finally { applying = false; }
                RefreshKeyBoxes();
                SetNotice("已全部恢复默认。");
                RefreshTitle();
            };
            page.Controls.Add(reset);
            y += Px(34);

            Label fixedNote = TextLabel(
                "Ctrl+1..9 跳到第 1..9 个标签页是固定的，不在这份列表里。",
                Px(11), false);
            fixedNote.ForeColor = Theme.TextDim;
            fixedNote.SetBounds(pad, y, inner, Px(18));
            page.Controls.Add(fixedNote);
            y += Px(24);

            Label scopeNote = TextLabel(
                "这些快捷键只在 TabbedExplorer 的窗口是前台时生效（别的程序里 Ctrl+W 照旧）。",
                Px(11), false);
            scopeNote.ForeColor = Theme.TextDim;
            scopeNote.SetBounds(pad, y, inner, Px(18));
            page.Controls.Add(scopeNote);
            y += Px(24);

            return y + Px(6);
        }

        private void RefreshKeyBoxes()
        {
            for (int i = 0; i < keyBoxes.Count; i++)
            {
                KeyBox b = keyBoxes[i].Value;
                if (b == null || b.IsDisposed) continue;
                string cmd = keyBoxes[i].Key;
                HotkeySpec[] specs = Hotkeys.Specs;
                for (int k = 0; k < specs.Length; k++)
                {
                    if (!string.Equals(specs[k].Cmd, cmd, StringComparison.OrdinalIgnoreCase)) continue;
                    b.Text = specs[k].Valid ? Hotkeys.Format(specs[k].Vk, specs[k].Ctrl, specs[k].Shift, specs[k].Alt)
                                            : "（无效：" + specs[k].Raw + "）";
                    break;
                }
            }
        }

        private void SetNotice(string s)
        {
            Diag.Step("设置窗口: " + s);
            if (notice != null && !notice.IsDisposed) notice.Text = s;
        }

        // ==================================================================
        /// <summary>
        /// 点了一下某一项：执行它的动作，然后把界面上所有值刷回真值。
        ///
        /// ⚠ 这里**只刷值、不重建控件**：这一跳是在那个控件自己的事件处理里发生的
        /// （勾选框的 CheckedChanged），当场把它 Dispose 掉不安全。
        /// 真要重建（颜色模式改了）由 Theme.Changed 那边用 BeginInvoke 推后一轮做。
        /// </summary>
        private void ApplyNode(SettingsMenu.Node nd)
        {
            if (nd == null) return;
            applying = true;      // 期间 `Settings.Save()` 是空转的（闸压着），dirty 也别在这会儿算
            try
            {
                if (nd.Click != null) nd.Click();
            }
            catch (Exception ex) { Diag.Log("设置窗口: 应用设置失败 " + ex.Message); }
            finally { applying = false; }

            // 值可能连带改了别的项（比如切捕获方式会把记忆搬家），所以整窗刷一遍
            SyncAll();

            // 标题上的「有未保存的改动」跟着变 —— 改了但还没点「保存」时用户能看见
            RefreshTitle();

            // 点完给一句反馈（「清理日志」那种干完什么都不说的动作，不说一句看不出来做过）
            if (nd.RebuildAfter)
            {
                // 文字里带实时数字的项（「日志文件：…（现在 546 KB）」）得重建才看得到新值。
                // 用 BeginInvoke 推后一轮 —— 这一跳就在某个控件的 Click 处理里，
                // 当场把它 Dispose 掉不安全（见 `Rebuild` 上方那段）。
                pendingNotice = nd.Notice;
                try { BeginInvoke(new Action(Rebuild)); } catch { }
            }
            else if (nd.Notice != null) SetNotice(nd.Notice);
        }

        // ==================================================================
        // 「保存」这一套
        // ==================================================================

        /// <summary>放掉写盘闸。**幂等** —— `OnFormClosing` 与 `OnFormClosed` 都会叫它。</summary>
        private void ReleaseHold()
        {
            if (!holdOn) return;
            holdOn = false;
            Settings.EndHold();
        }

        /// <summary>
        /// 算一遍「有没有还没保存的改动」，并把它写进**窗口标题**（用户指定的提示位置）。
        /// 判据 = 当前这些值序列化出来跟开窗那一刻的正文比 —— 比逐项比较可靠（以后加设置项不用改这里）。
        /// </summary>
        private void RefreshTitle()
        {
            if (IsDisposed || Disposing) return;
            dirty = !applying && !string.Equals(Settings.ToJson(), baselineJson, StringComparison.Ordinal);
            Text = AppInfo.Name + " 设置" + (dirty ? "  •  有未保存的改动" : "");
        }

        /// <summary>
        /// 点「保存」：把攒着的改动真写进 `settings.json`。
        /// 顺序是「先放闸、再写、再压回去」—— 闸压着的时候 `Settings.Save()` 是空转的。
        /// </summary>
        private void SaveEdits()
        {
            ReleaseHold();
            try { Settings.Save(); }
            finally { holdOn = true; Settings.BeginHold(); }   // 窗口还开着：后面的改动继续攒

            baseline = Settings.Snap();
            baselineJson = Settings.ToJson();
            dirty = false;
            RefreshTitle();
            SetNotice("已保存。");
            Diag.Step("设置窗口: 已保存 " + Settings.Describe());
        }

        /// <summary>
        /// 「不保存」：把值退回开窗那一刻 —— 包括**已经立刻生效**的那些
        /// （颜色模式 / 标签宽度 / 捕获方式 / 快捷键……都当用户没点过）。
        ///
        /// ⚠ `hub.RestoreSettings` 必须排在 `Settings.ApplySnapshot` **前面**：
        ///   那些 `SetXxx` 第一句都是「值没变就 return」，此刻 `Settings` 里必须还是**改过的值**
        ///   它们才会真的执行（执行时自己会把 `Settings` 写回快照里的值）。详见 `DesktopHub.RestoreSettings`。
        /// </summary>
        private void DiscardEdits()
        {
            if (baseline == null) { dirty = false; return; }
            applying = true;
            try
            {
                hub.RestoreSettings(baseline);
                Settings.ApplySnapshot(baseline);
                Hotkeys.Reload();        // 快捷键是改完立刻生效的，也得跟着退回去
            }
            catch (Exception ex) { Diag.Log("设置窗口: 回滚失败 " + ex.Message); }
            finally { applying = false; }

            SyncAll();
            baselineJson = Settings.ToJson();
            dirty = false;
            RefreshTitle();
            Diag.Step("设置窗口: 已丢弃未保存的改动，退回开窗时的样子");
        }

        private void SyncAll()
        {
            if (IsDisposed || Disposing) return;
            syncing = true;
            try
            {
                for (int i = 0; i < syncers.Count; i++)
                {
                    try { syncers[i](); } catch { }
                }
            }
            finally { syncing = false; }
        }

        // ==================================================================
        // 键盘
        // ==================================================================
        protected override void OnPreviewKeyDown(PreviewKeyDownEventArgs e)
        {
            // 焦点在捕获框里：Tab / 方向键都要当**普通键**收下来（默认会被拿去切焦点，
            // 那样就没法把「Ctrl+Tab」绑成快捷键了）。
            if (ActiveControl is KeyBox) { e.IsInputKey = true; return; }
            base.OnPreviewKeyDown(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            // 正在改键时不要抢键（否则按 Ctrl+W 会因为表单的 KeyPreview 先把窗口关了）
            if (ActiveControl is KeyBox) { base.OnKeyDown(e); return; }
            if (e.KeyCode == Keys.Escape) { Close(); return; }
            base.OnKeyDown(e);
        }
    }

    /// <summary>
    /// 自绘 `TabControl` —— 只管一件事：**把 tab 头带自己刷一遍底色**。
    /// 系统画的 tab 头带（最后一个 tab 右边那一截、最左边那条留白）永远用系统色，
    /// 深色模式下就是一条白 —— 用户报的「tab 背景颜色未适配颜色模式」。
    /// 做法：让系统照常画完（`WM_PAINT`），再往那两条空白上补一刀底色。
    /// </summary>
    internal sealed class TabHost : TabControl
    {
        private const int WM_PAINT = 0x000F;

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg != WM_PAINT || TabCount == 0 || !IsHandleCreated) return;
            try
            {
                using (Graphics g = Graphics.FromHwnd(Handle))
                {
                    Rectangle last = GetTabRect(TabCount - 1);
                    int stripH = Math.Min(Height, last.Bottom);
                    if (stripH <= 0) return;
                    if (last.Right < Width)
                        using (SolidBrush b = new SolidBrush(Theme.TabBar))
                            g.FillRectangle(b, last.Right, 0, Width - last.Right, stripH);
                    Rectangle first = GetTabRect(0);
                    if (first.Left > 0)
                        using (SolidBrush b = new SolidBrush(Theme.TabBar))
                            g.FillRectangle(b, 0, 0, first.Left, stripH);
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// 「按键捕获框」—— 其实就是个按钮，唯一特别的是**所有键都当普通输入收下来**
    /// （`IsInputKey` 返回 true），否则 Tab / 方向键会被对话框当成「移动焦点」，绑不了。
    /// </summary>
    internal sealed class KeyBox : Button
    {
        protected override bool IsInputKey(Keys keyData) { return true; }
    }

    /// <summary>
    /// 数值输入框（标签页宽度 / 侧边栏不透明度）—— **只有鼠标点进去过（自己有焦点）才让滚轮改值**。
    ///
    /// 为什么要盖这一层：net48 的 `NumericUpDown` **没焦点时也吃滚轮**
    /// （这个行为一直到 .NET Core 才改），于是「拿滚轮滚这一页、光标正好路过这个框」
    /// 就把值改了 —— 用户报的 BUG 一半是这个，另一半是改完标题没提示
    /// （见 `BuildGeneral` 里那个 `ValueChanged`）。
    ///
    /// 没焦点时**不调 base** ⇒ 不会把 `HandledMouseEventArgs.Handled` 置上，
    /// 这一滚轮继续往上传给开着 `AutoScroll` 的页面去滚（和「滚轮停在普通 Label 上」一样）。
    /// </summary>
    internal sealed class NumBox : NumericUpDown
    {
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (!ContainsFocus) return;
            base.OnMouseWheel(e);
        }
    }
}
