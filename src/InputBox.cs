using System;
using System.Drawing;
using System.Windows.Forms;

namespace TabbedExplorer
{
    /// <summary>
    /// 一个跟着颜色模式走的「输入一行字」小对话框（书签重命名 / 新建文件夹都用它）。
    ///
    /// 为什么不用 `Microsoft.VisualBasic.Interaction.InputBox`：它跟系统主题走 ——
    /// 深色模式下会弹出一整块惨白的窗口，和程序其它部分完全不搭。
    /// 这里就用 `Form` + `TextBox` 自己拼一个，配色读 `Theme`，自绘那几个按钮。
    /// </summary>
    internal static class InputBox
    {
        /// <summary>返回 null = 取消了。</summary>
        public static string Ask(IWin32Window owner, string title, string label, string value)
        {
            using (Form f = new Form())
            {
                f.Text = title;
                f.Icon = ShellIcon.AppIcon(false);   // 也带上程序图标（原来标题栏是空的）
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.MinimizeBox = false;
                f.MaximizeBox = false;
                f.ShowInTaskbar = false;
                f.StartPosition = FormStartPosition.CenterParent;
                f.ClientSize = new Size(Px(400), Px(128));
                f.BackColor = Theme.Chrome;
                f.ForeColor = Theme.Text;
                f.Font = new Font("Segoe UI", Px(12), FontStyle.Regular, GraphicsUnit.Pixel);

                Label lb = new Label();
                lb.Text = label;
                lb.AutoSize = false;
                lb.SetBounds(Px(14), Px(12), Px(372), Px(20));
                lb.ForeColor = Theme.TextDim;
                lb.BackColor = Theme.Chrome;
                f.Controls.Add(lb);

                TextBox tb = new TextBox();
                tb.Text = value ?? "";
                tb.SetBounds(Px(14), Px(36), Px(372), Px(24));
                tb.BackColor = Theme.InputBack;
                tb.ForeColor = Theme.Text;
                tb.BorderStyle = BorderStyle.FixedSingle;
                tb.SelectAll();
                f.Controls.Add(tb);

                Button ok = MakeButton("确定", true);
                ok.SetBounds(Px(400 - 14 - 88), Px(78), Px(88), Px(30));
                f.Controls.Add(ok);
                f.AcceptButton = ok;

                Button cancel = MakeButton("取消", false);
                cancel.SetBounds(Px(400 - 14 - 88 - 8 - 88), Px(78), Px(88), Px(30));
                cancel.DialogResult = DialogResult.Cancel;
                f.Controls.Add(cancel);
                f.CancelButton = cancel;

                string result = null;
                ok.Click += delegate { result = tb.Text; f.DialogResult = DialogResult.OK; f.Close(); };

                // 标题栏也跟主题（深色下别那条白杠）
                f.HandleCreated += delegate { Theme.ApplyTitleBar(f.Handle); };
                f.Shown += delegate { Theme.ApplyTitleBar(f.Handle); tb.Focus(); tb.SelectAll(); };

                f.ShowDialog(owner);
                return result;
            }
        }

        private static Button MakeButton(string text, bool primary)
        {
            Button b = new Button();
            b.Text = text;
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
