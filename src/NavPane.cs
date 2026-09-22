using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace TabbedExplorer
{
    /// <summary>
    /// 左侧导航窗格：快速访问 / 此电脑 / 网络。
    /// 目录是按需展开的（展开时才去列子目录），所以不会一开窗就卡。
    /// </summary>
    internal sealed class NavPane : Panel
    {
        public delegate void PathHandler(object sender, string path);
        public event PathHandler PathChosen;

        private readonly TreeView tree;
        private bool suppress;

        public NavPane()
        {
            Width = 208;
            BackColor = Theme.NavBack;

            tree = new TreeView();
            tree.Dock = DockStyle.Fill;
            tree.BorderStyle = BorderStyle.None;
            tree.ShowLines = false;
            tree.ShowRootLines = true;
            tree.ShowPlusMinus = true;
            tree.HideSelection = false;
            tree.FullRowSelect = false;
            tree.DrawMode = TreeViewDrawMode.OwnerDrawText;
            tree.ItemHeight = 22;
            tree.Font = new Font("Segoe UI", 9f);
            tree.BackColor = Theme.NavBack;
            tree.ForeColor = Theme.Text;
            tree.DrawNode += TreeDrawNode;
            tree.AfterSelect += TreeAfterSelect;
            tree.BeforeExpand += TreeBeforeExpand;
            Controls.Add(tree);

            Paint += PanePaint;
            Build();
            Theme.Changed += delegate { ApplyTheme(); };
        }

        private void PanePaint(object sender, PaintEventArgs e)
        {
            using (Pen p = new Pen(Theme.Border))
                e.Graphics.DrawLine(p, Width - 1, 0, Width - 1, Height);
        }

        public void ApplyTheme()
        {
            BackColor = Theme.NavBack;
            tree.BackColor = Theme.NavBack;
            tree.ForeColor = Theme.Text;
            if (tree.IsHandleCreated) Theme.StyleShellWindow(tree.Handle);
            tree.Invalidate();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyTheme();
        }

        // ==================================================================
        private void Build()
        {
            tree.BeginUpdate();
            tree.Nodes.Clear();

            // ---- 快速访问 ----
            TreeNode qa = new TreeNode("快速访问");
            qa.Tag = null;
            TreeNode desk = Leaf("桌面", Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
            TreeNode dl = Leaf("下载", Path.Combine(Profile(), "Downloads"));
            TreeNode doc = Leaf("文档", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            TreeNode pic = Leaf("图片", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
            TreeNode mus = Leaf("音乐", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
            TreeNode vid = Leaf("视频", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
            qa.Nodes.AddRange(new TreeNode[] { desk, dl, doc, pic, mus, vid });

            // ---- 此电脑 ----
            TreeNode pc = new TreeNode("此电脑");
            pc.Tag = ExplorerView.ThisPcPath;
            pc.Nodes.Add(Leaf("3D 对象", Path.Combine(Profile(), "3D Objects")));
            pc.Nodes.Add(Leaf("视频", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)));
            pc.Nodes.Add(Leaf("图片", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)));
            pc.Nodes.Add(Leaf("文档", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)));
            pc.Nodes.Add(Leaf("下载", Path.Combine(Profile(), "Downloads")));
            pc.Nodes.Add(Leaf("音乐", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)));
            pc.Nodes.Add(Leaf("桌面", Environment.GetFolderPath(Environment.SpecialFolder.Desktop)));

            // ---- 网络 ----
            TreeNode net = new TreeNode("网络");
            net.Tag = "::{208D2C60-3AEA-1069-A2D7-08002B30309D}";

            tree.Nodes.AddRange(new TreeNode[] { qa, pc, net });
            qa.Expand();
            pc.Expand();
            tree.EndUpdate();
        }

        private static string Profile()
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        private static TreeNode Placeholder()
        {
            TreeNode t = new TreeNode("");
            t.Tag = "placeholder";
            return t;
        }

        private static TreeNode Leaf(string text, string path)
        {
            TreeNode t = new TreeNode(text);
            t.Tag = path;
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            {
                t.Nodes.Add(Placeholder());
            }
            return t;
        }

        private void TreeBeforeExpand(object sender, TreeViewCancelEventArgs e)
        {
            TreeNode n = e.Node;
            if (n == null) return;
            if (n.Nodes.Count == 1 && n.Nodes[0].Tag as string == "placeholder")
            {
                Fill(n);
            }
        }

        private void Fill(TreeNode n)
        {
            n.Nodes.Clear();
            string path = n.Tag as string;

            // 此电脑：列驱动器
            if (path == ExplorerView.ThisPcPath)
            {
                try
                {
                    DriveInfo[] drives = DriveInfo.GetDrives();
                    for (int i = 0; i < drives.Length; i++)
                    {
                        try
                        {
                            string letter = drives[i].Name.TrimEnd('\\');
                            string label = drives[i].IsReady ? drives[i].VolumeLabel : "";
                            string text = string.IsNullOrEmpty(label)
                                ? letter : label + " (" + letter + ")";
                            TreeNode d = new TreeNode(text);
                            d.Tag = drives[i].Name;
                            d.Nodes.Add(Placeholder());
                            n.Nodes.Add(d);
                        }
                        catch { }
                    }
                }
                catch (Exception ex) { Diag.Log("列驱动器失败: " + ex.Message); }
                return;
            }

            if (string.IsNullOrEmpty(path) || path.StartsWith("::")) return;
            if (!Directory.Exists(path)) return;

            try
            {
                string[] dirs = Directory.GetDirectories(path);
                Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < dirs.Length; i++)
                {
                    string name = Path.GetFileName(dirs[i]);
                    if (string.IsNullOrEmpty(name)) continue;
                    FileAttributes at;
                    try { at = File.GetAttributes(dirs[i]); }
                    catch { continue; }
                    if ((at & FileAttributes.Hidden) != 0 || (at & FileAttributes.System) != 0) continue;

                    TreeNode d = new TreeNode(name);
                    d.Tag = dirs[i];
                    d.Nodes.Add(Placeholder());
                    n.Nodes.Add(d);
                }
            }
            catch (Exception ex)
            {
                Diag.Log("导航窗格展开失败: " + ex.Message);
            }
        }

        private void TreeAfterSelect(object sender, TreeViewEventArgs e)
        {
            if (suppress) return;
            string path = e.Node == null ? null : e.Node.Tag as string;
            if (string.IsNullOrEmpty(path) || path == "placeholder") return;
            if (PathChosen != null) PathChosen(this, path);
        }

        // ==================================================================
        // 自绘节点（图标 + 文字，颜色跟主题）
        // ==================================================================
        private void TreeDrawNode(object sender, DrawTreeNodeEventArgs e)
        {
            e.DrawDefault = false;
            Rectangle b = e.Bounds;
            Graphics g = e.Graphics;

            bool selected = (e.State & TreeNodeStates.Selected) != 0;
            Rectangle full = new Rectangle(0, b.Top, tree.Width, b.Height);

            if (selected)
            {
                using (SolidBrush sb = new SolidBrush(Theme.IsDark ? Color.FromArgb(70, 70, 70) : Color.FromArgb(206, 229, 250)))
                    g.FillRectangle(sb, full);
            }

            string path = e.Node.Tag as string;
            bool isGroup = string.IsNullOrEmpty(path) || path == "placeholder";

            // 图标
            Rectangle ir = new Rectangle(b.Left - 18, b.Top + 4, 13, 13);
            using (Pen p = new Pen(Theme.TextDim, 1.2f))
            using (SolidBrush sb = new SolidBrush(Theme.IsDark ? Color.FromArgb(240, 196, 110) : Color.FromArgb(226, 178, 92)))
            {
                if (isGroup && !string.IsNullOrEmpty(path) && path.StartsWith("::"))
                {
                    g.DrawRectangle(p, ir.Left, ir.Top + 1, ir.Width - 1, ir.Height - 2);
                    g.DrawLine(p, ir.Left + 2, ir.Top + 5, ir.Right - 2, ir.Top + 5);
                }
                else if (isGroup)
                {
                    g.DrawRectangle(p, ir.Left, ir.Top, ir.Width - 1, ir.Height - 1);
                }
                else
                {
                    // 文件夹：小梯形 + 主体
                    g.FillRectangle(sb, ir.Left, ir.Top + 3, ir.Width, ir.Height - 3);
                    g.FillRectangle(sb, ir.Left, ir.Top + 1, 6, 3);
                }
            }

            Color fg = Theme.Text;
            if (selected) fg = Theme.Text;
            TextRenderer.DrawText(g, e.Node.Text, tree.Font, b, fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }

        /// <summary>跟随当前标签页高亮（找不到就不动）。</summary>
        public void SyncTo(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            suppress = true;
            try
            {
                TreeNode n = Find(tree.Nodes, path);
                if (n != null)
                {
                    tree.SelectedNode = n;
                    TreeNode p = n.Parent;
                    while (p != null) { p.Expand(); p = p.Parent; }
                    n.EnsureVisible();
                }
            }
            catch { }
            suppress = false;
        }

        private static TreeNode Find(TreeNodeCollection nodes, string path)
        {
            for (int i = 0; i < nodes.Count; i++)
            {
                if (nodes[i].Tag as string == path) return nodes[i];
                // 路径不完全一致（比如带尾斜杠）时做个宽松比较
                string tag = nodes[i].Tag as string;
                if (!string.IsNullOrEmpty(tag) && tag != "placeholder" &&
                    string.Equals(tag.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    return nodes[i];
                TreeNode deep = Find(nodes[i].Nodes, path);
                if (deep != null) return deep;
            }
            return null;
        }

        public bool PaneVisible
        {
            get { return Visible; }
            set { Visible = value; }
        }
    }
}
