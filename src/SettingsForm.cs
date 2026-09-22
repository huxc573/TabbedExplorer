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
    /// 独立的设置窗口（川 2026-09-22 要的）—— 齿轮按钮和标签条空白处右键的「设置」开的就是它。
    /// 托盘图标右键那份菜单**保持原样**（川明确要求）。
    ///
    /// 窗口内容**不另写一份**：全部由 `SettingsMenu.Spec(hub)` 生成 —— 托盘那份菜单用的也是同一份规格，
    /// 所以以后加设置项不会漏一边（当初「托盘右键没有设置选项」就是两边各写一遍漏出来的）。
    /// 这里只是换一种渲染方式：
    ///   · 同 `Group` 的相邻叶子 → 一组单选按钮（捕获方式 / 颜色模式）
    ///   · 单独的叶子 → 勾选框
    ///   · 带子项的节点（标签页宽度）→ 下拉框
    ///   · `Checked == null` 且没有动作 → 一行说明文字
    /// 顶部放**程序名 / 版本 / 简介**。
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

        private readonly DesktopHub hub;
        /// <summary>「把界面上的值刷回真值」的动作（点完任何一项都要刷一遍，单选/勾选才跟得上）。</summary>
        private readonly List<Action> syncers = new List<Action>();
        /// <summary>正在程序化地改控件值 —— 期间别把事件当成「用户点的」。</summary>
        private bool syncing;
        private bool rebuilding;
        /// <summary>订阅 Theme.Changed 的那个处理器（关窗时要退订，否则静态事件会把窗口吊着不放）。</summary>
        private EventHandler themeHandler;

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
                syncers.Clear();
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
        private int AddRule(int y, int w, int pad)
        {
            Panel p = new Panel();
            p.BackColor = Theme.Border;
            p.SetBounds(pad, y, w - pad * 2, Math.Max(1, Px(1)));
            Controls.Add(p);
            return y + Math.Max(1, Px(1)) + Px(8);
        }

        private void Build()
        {
            int pad = Px(18);
            int w = Px(600);
            int y = pad;
            int rowH = Px(26);

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
            y += blurb.Height + Px(12);

            y = AddRule(y, w, pad);

            // ---- 设置项（唯一来源：SettingsMenu.Spec）----
            List<SettingsMenu.Node> spec = SettingsMenu.Spec(hub);
            string curGroup = null;
            Panel groupBox = null;

            for (int i = 0; i < spec.Count; i++)
            {
                SettingsMenu.Node nd = spec[i];
                if (nd.Text == null) { curGroup = null; groupBox = null; y = AddRule(y, w, pad); continue; }

                // 带子项 → 下拉框
                if (nd.Children != null && nd.Children.Count > 0)
                {
                    curGroup = null; groupBox = null;
                    Label lab = TextLabel(nd.Text, Px(12), false);
                    lab.SetBounds(pad, y + Px(3), Px(110), Px(22));
                    Controls.Add(lab);

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
                    Controls.Add(cb);
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
                        groupBox.SetBounds(pad, y, w - pad * 2, cnt * rowH);
                        Controls.Add(groupBox);
                        y += cnt * rowH;
                    }

                    RadioButton rb = new RadioButton();
                    rb.FlatStyle = FlatStyle.Standard;
                    rb.BackColor = BackColor;
                    rb.ForeColor = ForeColor;
                    rb.Font = Font;
                    rb.Text = nd.Text;
                    rb.SetBounds(Px(4), groupBox.Controls.Count * rowH,
                                 w - pad * 2 - Px(8), rowH);
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
                    info.SetBounds(pad, y, w - pad * 2, Px(18));
                    Controls.Add(info);
                    y += Px(22);
                    continue;
                }

                // 纯动作（「记住当前标签」）
                if (nd.Checked == null)
                {
                    Button b = new Button();
                    b.FlatStyle = FlatStyle.Flat;
                    b.BackColor = Theme.Hover;
                    b.ForeColor = ForeColor;
                    b.FlatAppearance.BorderColor = Theme.Border;
                    b.Font = Font;
                    b.Text = nd.Text;
                    b.SetBounds(pad, y, Px(150), Px(26));
                    SettingsMenu.Node node = nd;
                    b.Click += delegate { ApplyNode(node); };
                    Controls.Add(b);
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
                ck.SetBounds(Px(4), y, w - pad * 2 - Px(8), rowH);
                SettingsMenu.Node n2 = nd;
                ck.CheckedChanged += delegate
                {
                    if (syncing) return;
                    if (n2.Checked == null) return;
                    if (ck.Checked != n2.Checked()) ApplyNode(n2);   // 只在跟真值不一致时才算「点了一下」
                };
                Controls.Add(ck);
                syncers.Add(delegate
                {
                    bool v = n2.Checked != null && n2.Checked();
                    if (ck.Checked != v) ck.Checked = v;
                });
                y += rowH + Px(4);
            }

            // ---- 底部：关闭 ----
            y += Px(6);
            y = AddRule(y, w, pad);

            Button close = new Button();
            close.FlatStyle = FlatStyle.Flat;
            close.BackColor = Theme.Hover;
            close.ForeColor = ForeColor;
            close.FlatAppearance.BorderColor = Theme.Border;
            close.Font = Font;
            close.Text = "关闭";
            int cw = Px(90), ch = Px(28);
            close.SetBounds(w - pad - cw, y, cw, ch);
            close.Click += delegate { Close(); };
            Controls.Add(close);
            y += ch + pad;

            ClientSize = new Size(w, y);

            SyncAll();
        }

        /// <summary>
        /// 点了一下某一项：执行它的动作，然后把界面上所有值刷回真值。
        ///
        /// ⚠ 这里**只刷值、不重建控件**：这一跳是在那个控件自己的事件处理里发生的
        /// （勾选框的 CheckedChanged），当场把它 Dispose 掉不安步。
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

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Theme.ApplyTitleBar(Handle);   // 深色标题栏要等窗口真显示之后再设一次（跟主窗口一个道理）
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { Close(); return; }
            base.OnKeyDown(e);
        }
    }
}
