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

                    int w = Px(420);
                    int pad = Px(16);
                    int inner = w - pad * 2;

                    // 正文先量高 —— 文案长短不一样，窗口得跟着长，不能写死
                    Size measured = TextRenderer.MeasureText(text, font, new Size(inner, Px(600)),
                        TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                    int textH = Math.Max(Px(20), measured.Height);

                    int btnH = Px(30), btnW = Px(96), btnY = pad + textH + Px(18);
                    f.ClientSize = new Size(w, btnY + btnH + pad);

                    Label lb = new Label();
                    lb.Text = text;
                    lb.AutoSize = false;
                    lb.SetBounds(pad, pad, inner, textH);
                    lb.ForeColor = Theme.Text;
                    lb.BackColor = Theme.Chrome;
                    lb.Font = font;
                    f.Controls.Add(lb);

                    bool ok = false;
                    Button yes = MakeButton(okText, true, fontDim);
                    yes.SetBounds(w - pad - btnW, btnY, btnW, btnH);
                    yes.Click += delegate { ok = true; f.DialogResult = DialogResult.OK; f.Close(); };
                    f.Controls.Add(yes);
                    f.AcceptButton = yes;

                    Button no = MakeButton("取消", false, fontDim);
                    no.SetBounds(w - pad - btnW - Px(8) - btnW, btnY, btnW, btnH);
                    no.DialogResult = DialogResult.Cancel;
                    f.Controls.Add(no);
                    f.CancelButton = no;

                    f.HandleCreated += delegate { Theme.ApplyTitleBar(f.Handle); };
                    f.Shown += delegate { Theme.ApplyTitleBar(f.Handle); no.Focus(); };

                    f.ShowDialog(owner);
                    return ok;
                }
            }
            catch (Exception ex)
            {
                // 弹不出来也不能把「全部打开」这条路卡死：问不到就当用户说「别开」
                Diag.Log("确认框: 弹出失败 " + ex.Message);
                return false;
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
