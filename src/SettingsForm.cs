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
    /// 独立的设置窗口（川 2026-09-22 要的）—— 齿轮按钮、标签条空白右键、托盘「更多选项…」开的都是它。
    /// 托盘图标右键那份菜单**保持原样**（川明确要求）。
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
    /// 2026-09-22 第二批（川）：
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
        /// <summary>订阅 Theme.Changed 的那个处理器（关窗时要退订，否则静态事件会把窗口吊着不放）。</summary>
        private EventHandler themeHandler;

        /// <summary>两个 Tab（自绘 —— 系统画的 Tab 头在深色下是一块白，跟外壳两套皮）。</summary>
        private TabControl tabs;
        /// <summary>重建时保住当前选中的是哪一页（换颜色模式会整窗重建）。</summary>
        private int keepPage;
        /// <summary>底部那行提示（改键成功 / 撞车 / 非法）。</summary>
        private Label notice;
        /// <summary>改键页里那些捕获框：命令 → 控件（改完要刷文字）。</summary>
        private readonly List<KeyValuePair<string, KeyBox>> keyBoxes =
            new List<KeyValuePair<string, KeyBox>>();

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
        // 深色标题栏（川 2026-09-22 报的「打开时那一条是白的，切出去切回来才对」）
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

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
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

            Label ver = TextLabel("版本 " + AppInfo.Version, Px(12), false);
            ver.ForeColor = Theme.TextDim;
            ver.SetBounds(tx, y + Px(34), w - tx - pad, Px(18));
            Controls.Add(ver);

            y += Math.Max(iconSize, Px(56)) + Px(12);

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

            tabs = new TabControl();
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

            // 高度：内容要多高给多高，但**不许顶出屏幕**（屏幕高度够就全显示，不够就页内滚动）。
            // 「常规」页比「快捷键」页长得多的那种情况下也能看全（川的屏幕不一定放得下 ~800 逻辑像素）。
            int footerH = Px(22) + Px(28) + pad;
            int avail = Px(40);
            try { avail = Screen.FromPoint(Cursor.Position).WorkingArea.Height; } catch { }
            int maxTabsH = Math.Max(Px(240), avail - y - footerH - Px(30));
            int wantTabsH = Math.Max(hGeneral, hKeys) + Px(14);
            int tabsH = Math.Min(wantTabsH, maxTabsH);
            bool scrolls = wantTabsH > tabsH;
            general.AutoScroll = scrolls;          // 放不下才给滚动条（平时不出现）
            keys.AutoScroll = scrolls;
            tabs.SetBounds(pad, y, w - pad * 2, tabsH);
            Controls.Add(tabs);
            if (keepPage >= 0 && keepPage < tabs.TabCount) tabs.SelectedIndex = keepPage;
            y += tabsH + Px(8);

            // ---- 底部：提示行 + 关闭 ----
            notice = TextLabel("", Px(11), false);
            notice.ForeColor = Theme.TextDim;
            notice.SetBounds(pad, y, w - pad * 2, Px(18));
            Controls.Add(notice);
            y += Px(22);

            Button close = FlatButton("关闭", w - pad - Px(90), y, Px(90), Px(28));
            close.Click += delegate { Close(); };
            Controls.Add(close);
            y += Px(28) + pad;

            ClientSize = new Size(w, y);

            SyncAll();
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

                    NumericUpDown num = new NumericUpDown();
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
                        node.NumSet((int)num.Value);
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
        // 「快捷键」页（川 2026-09-22 新增）
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
                    if (Hotkeys.Assign(cmd, vk, ctrl, shift, alt, out why)) SetNotice("已保存：" + Hotkeys.LabelOf(cmd) + " = " + Hotkeys.Format(vk, ctrl, shift, alt));
                    else SetNotice("没改成：" + why);
                    RefreshKeyBoxes();
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
            reset.Click += delegate { Hotkeys.ResetAll(); RefreshKeyBoxes(); SetNotice("已全部恢复默认。"); };
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
            try
            {
                if (nd.Click != null) nd.Click();
            }
            catch (Exception ex) { Diag.Log("设置窗口: 应用设置失败 " + ex.Message); }

            // 值可能连带改了别的项（比如切捕获方式会把记忆搬家），所以整窗刷一遍
            SyncAll();
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
    /// 「按键捕获框」—— 其实就是个按钮，唯一特别的是**所有键都当普通输入收下来**
    /// （`IsInputKey` 返回 true），否则 Tab / 方向键会被对话框当成「移动焦点」，绑不了。
    /// </summary>
    internal sealed class KeyBox : Button
    {
        protected override bool IsInputKey(Keys keyData) { return true; }
    }
}
