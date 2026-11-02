using System;
using System.Drawing;
using System.Windows.Forms;

namespace TabbedExplorer
{
    /// <summary>
    /// 一个跟着颜色模式走的「输入一行字」小对话框（书签编辑 / 新建文件夹都用它）。
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

        /// <summary>
        /// 「编辑」用：一次改**显示名**和**目标路径**两栏。
        /// `pathValue` 传 null ⇒ 不显示路径那栏（文件夹只能改名，没有目标路径可改）。
        /// 返回 `{name, path}`；取消返回 null。
        ///
        /// ⚠ 路径那栏**不做即时校验**：收不收得下由调用方过 `FavStore.EditItem` 判
        ///   （「什么算能收的路径」只有那一处判据）。这里只管要两个字。
        /// </summary>
        public static string[] AskEdit(IWin32Window owner, string title,
                                       string nameValue, string pathValue)
        {
            bool withPath = pathValue != null;
            using (Form f = new Form())
            {
                f.Text = title;
                f.Icon = ShellIcon.AppIcon(false);
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.MinimizeBox = false;
                f.MaximizeBox = false;
                f.ShowInTaskbar = false;
                f.StartPosition = FormStartPosition.CenterParent;
                f.ClientSize = new Size(Px(460), Px(withPath ? 186 : 128));
                f.BackColor = Theme.Chrome;
                f.ForeColor = Theme.Text;
                f.Font = new Font("Segoe UI", Px(12), FontStyle.Regular, GraphicsUnit.Pixel);

                Label lb1 = new Label();
                lb1.Text = "显示名（留空 = 用路径末级）";
                lb1.AutoSize = false;
                lb1.SetBounds(Px(14), Px(12), Px(432), Px(20));
                lb1.ForeColor = Theme.TextDim;
                lb1.BackColor = Theme.Chrome;
                f.Controls.Add(lb1);

                TextBox tbName = new TextBox();
                tbName.Text = nameValue ?? "";
                tbName.SetBounds(Px(14), Px(34), Px(432), Px(24));
                tbName.BackColor = Theme.InputBack;
                tbName.ForeColor = Theme.Text;
                tbName.BorderStyle = BorderStyle.FixedSingle;
                f.Controls.Add(tbName);

                TextBox tbPath = null;
                if (withPath)
                {
                    Label lb2 = new Label();
                    lb2.Text = "路径（文件 / 文件夹 / shell: 虚拟位置 / 手机里的位置）";
                    lb2.AutoSize = false;
                    lb2.SetBounds(Px(14), Px(68), Px(432), Px(20));
                    lb2.ForeColor = Theme.TextDim;
                    lb2.BackColor = Theme.Chrome;
                    f.Controls.Add(lb2);

                    tbPath = new TextBox();
                    tbPath.Text = pathValue ?? "";
                    tbPath.SetBounds(Px(14), Px(90), Px(432), Px(24));
                    tbPath.BackColor = Theme.InputBack;
                    tbPath.ForeColor = Theme.Text;
                    tbPath.BorderStyle = BorderStyle.FixedSingle;
                    f.Controls.Add(tbPath);
                }

                int by = withPath ? 136 : 78;
                Button ok = MakeButton("确定", true);
                ok.SetBounds(Px(460 - 14 - 88), Px(by), Px(88), Px(30));
                f.Controls.Add(ok);
                f.AcceptButton = ok;

                Button cancel = MakeButton("取消", false);
                cancel.SetBounds(Px(460 - 14 - 88 - 8 - 88), Px(by), Px(88), Px(30));
                cancel.DialogResult = DialogResult.Cancel;
                f.Controls.Add(cancel);
                f.CancelButton = cancel;

                string[] result = null;
                ok.Click += delegate
                {
                    result = new string[] { tbName.Text, withPath ? tbPath.Text : null };
                    f.DialogResult = DialogResult.OK;
                    f.Close();
                };

                f.HandleCreated += delegate { Theme.ApplyTitleBar(f.Handle); };
                f.Shown += delegate
                {
                    Theme.ApplyTitleBar(f.Handle);
                    // 名字清空时让光标落在路径上（那种情况名字本来就没意义）
                    if (string.IsNullOrEmpty(tbName.Text) && tbPath != null) { tbPath.Focus(); tbPath.SelectAll(); }
                    else { tbName.Focus(); tbName.SelectAll(); }
                };

                f.ShowDialog(owner);
                return result;
            }
        }

        private static Button MakeButton(string text, bool primary)        {
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
