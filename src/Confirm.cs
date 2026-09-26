using System;
using System.Drawing;
using System.Windows.Forms;

namespace TabbedExplorer
{
    /// <summary>
    /// 一个跟着颜色模式走的「是 / 否」确认框。跟 `InputBox` 同一套路子（自绘的 `Form`，
    /// 配色读 `Theme`），**不用 `MessageBox`** —— 那个在深色下是一整块惨白，跟程序其它部分不搭。
    ///
    /// 目前只有一处用：书签文件夹的「全部打开」在数量多的时候问一句
    /// （用户：超过 7 项要再确认一次，免得右键误触一口气开一屏标签）。
    /// </summary>
    internal static class Confirm
    {
        /// <summary>返回 true = 用户点了主按钮（继续）。关掉 / 取消 / 按 Esc 都算 false。</summary>
        public static bool Ask(IWin32Window owner, string title, string text, string okText)
        {
            return AskButtons(owner, title, text,
                       new string[] { okText, "取消" },
                       new DialogResult[] { DialogResult.OK, DialogResult.Cancel })
                   == DialogResult.OK;
        }

        /// <summary>
        /// 「有未保存的改动」关窗时问的那一句：**保存 / 不保存 / 取消**。
        ///
        /// 为什么要第三个按钮：只有「是 / 否」的话，手一抖点到「否」就把刚改的一堆设置
        /// 白改了（或者点右上角 × 当成了「取消」）；而且 ×/Esc 在 `YesNo` 上会被当成「否」。
        /// 这里 × 和 Esc 一律 = **取消**（什么都不做、窗口留着），只有明点「不保存」才真丢。
        /// </summary>
        public static DialogResult AskSave(IWin32Window owner, string title, string text)
        {
            return AskButtons(owner, title, text,
                       new string[] { "保存", "不保存", "取消" },
                       new DialogResult[] { DialogResult.Yes, DialogResult.No, DialogResult.Cancel });
        }

        /// <summary>
        /// 通用确认框。`buttons` **从右往左**摆（第 0 个是最右边那个主按钮 = 高亮）；
        /// `results` 跟它一一对应（`DialogResult.Cancel` 那个接 Esc / 当取消键）。
        /// 关掉窗口（×）也返回 `DialogResult.Cancel` —— 模态 `ShowDialog` 里没有
        /// `DialogResult` 的 `Close()` 就是这个结果。
        /// </summary>
        private static DialogResult AskButtons(IWin32Window owner, string title, string text,
                                               string[] buttons, DialogResult[] results)
        {
            try
            {
                using (Form f = new Form())
                {
                    f.Text = title;
                    f.Icon = ShellIcon.AppIcon(false);
                    f.FormBorderStyle = FormBorderStyle.FixedDialog;
                    f.MinimizeBox = false;
                    f.MaximizeBox = false;
                    f.ShowInTaskbar = false;
                    f.StartPosition = FormStartPosition.CenterParent;
                    f.BackColor = Theme.Chrome;
                    f.ForeColor = Theme.Text;

                    Font font = new Font("Segoe UI", Px(12), FontStyle.Regular, GraphicsUnit.Pixel);
                    Font fontDim = new Font("Segoe UI", Px(11), FontStyle.Regular, GraphicsUnit.Pixel);

                    int w = Px(440);
                    int pad = Px(16);
                    int inner = w - pad * 2;

                    // 正文先量高 —— 文案长短不一样，窗口得跟着长，不能写死
                    Size measured = TextRenderer.MeasureText(text, font, new Size(inner, Px(600)),
                        TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                    int textH = Math.Max(Px(20), measured.Height);

                    int btnH = Px(30), btnW = Px(92), btnY = pad + textH + Px(18);
                    f.ClientSize = new Size(w, btnY + btnH + pad);

                    Label lb = new Label();
                    lb.Text = text;
                    lb.AutoSize = false;
                    lb.SetBounds(pad, pad, inner, textH);
                    lb.ForeColor = Theme.Text;
                    lb.BackColor = Theme.Chrome;
                    lb.Font = font;
                    f.Controls.Add(lb);

                    DialogResult result = DialogResult.Cancel;
                    for (int i = 0; i < buttons.Length; i++)
                    {
                        bool primary = (i == 0);
                        DialogResult r = (i < results.Length) ? results[i] : DialogResult.Cancel;
                        Button b = MakeButton(buttons[i], primary, fontDim);
                        b.SetBounds(w - pad - btnW - i * (btnW + Px(8)), btnY, btnW, btnH);
                        b.Click += delegate { result = r; f.DialogResult = r; f.Close(); };
                        f.Controls.Add(b);
                        if (primary) f.AcceptButton = b;
                        if (r == DialogResult.Cancel) f.CancelButton = b;
                    }

                    f.HandleCreated += delegate { Theme.ApplyTitleBar(f.Handle); };
                    f.Shown += delegate
                    {
                        Theme.ApplyTitleBar(f.Handle);
                        // 焦点给「取消」：回车走主按钮（AcceptButton），Esc / 关窗都算取消
                        foreach (Control c in f.Controls)
                        {
                            if (c is Button && ReferenceEquals(f.CancelButton, c)) { c.Focus(); break; }
                        }
                    };

                    f.ShowDialog(owner);
                    return result;
                }
            }
            catch (Exception ex)
            {
                // 弹不出来也不能把调用方卡死：问不到就当用户说「别继续」
                Diag.Log("确认框: 弹出失败 " + ex.Message);
                return DialogResult.Cancel;
            }
        }

        private static Button MakeButton(string text, bool primary, Font font)
        {
            Button b = new Button();
            b.Text = text;
            b.Font = font;
            b.FlatStyle = FlatStyle.Flat;
            b.BackColor = primary ? Theme.Accent : Theme.Hover;
            b.ForeColor = primary ? Color.White : Theme.Text;
            b.FlatAppearance.BorderColor = Theme.Border;
            b.UseVisualStyleBackColor = false;
            return b;
        }

        private static float DpiScale
        {
            get
            {
                try
                {
                    uint d = NativeMethods.GetDpiForSystem();
                    if (d >= 96) return d / 96f;
                }
                catch { }
                return 1f;
            }
        }

        private static int Px(int v) { return (int)Math.Round(v * DpiScale); }
    }
}
