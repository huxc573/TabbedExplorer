using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace TabbedExplorer
{
    /// <summary>
    /// 面包屑地址栏（跟 Win10 资源管理器一样：此电脑 › Data (D:) › Dev › …）。
    /// 点某一段就跳到那一段；点空白处（或 Ctrl+L）切成可输入的文本框。
    /// </summary>
    internal sealed class Breadcrumb : Control
    {
        public delegate void PathHandler(object sender, string path);
        public event PathHandler Navigate;

        private sealed class Seg
        {
            public string Text;
            public string Path;
            public Rectangle Bounds;
        }

        private readonly List<Seg> segs = new List<Seg>();
        private readonly TextBox editor;
        private bool editing;
        private int hover = -1;
        private string current = "";

        private static readonly Font SegFont = new Font("Segoe UI", 9.5f);

        public Breadcrumb()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = 26;
            BackColor = Theme.InputBack;

            editor = new TextBox();
            editor.BorderStyle = BorderStyle.None;
            editor.Font = SegFont;
            editor.Visible = false;
            editor.KeyDown += EditorKeyDown;
            editor.LostFocus += delegate { if (editing) EndEdit(false); };
            Controls.Add(editor);

            Theme.Changed += delegate { ApplyTheme(); };
        }

        public string Path
        {
            get { return current; }
            set
            {
                if (current == value) return;
                current = value == null ? "" : value;
                Rebuild();
                Invalidate();
            }
        }

        public void ApplyTheme()
        {
            BackColor = Theme.InputBack;
            editor.BackColor = Theme.InputBack;
            editor.ForeColor = Theme.Text;
            Invalidate();
        }

        // ==================================================================
        // 分段
        // ==================================================================
        private void Rebuild()
        {
            segs.Clear();
            string p = current;
            if (p.Length == 0) return;

            if (p.StartsWith("::", StringComparison.Ordinal))
            {
                Add(KnownName(p), p);
                return;
            }
            if (p.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) ||
                p.StartsWith("search-ms:", StringComparison.OrdinalIgnoreCase))
            {
                Add(p.StartsWith("search", StringComparison.OrdinalIgnoreCase) ? "搜索结果" : p, p);
                return;
            }

            Add("此电脑", ExplorerView.ThisPcPath);
            string acc = "";
            string rest = p;

            if (rest.Length >= 2 && rest[1] == ':')
            {
                acc = rest.Substring(0, 2) + "\\";
                Add(DriveLabel(acc), acc);
                rest = rest.Length > 3 ? rest.Substring(3) : "";
            }
            else if (rest.StartsWith("\\\\", StringComparison.Ordinal))
            {
                // UNC：\\server\share\...
                string[] bits = rest.Substring(2).Split('\\');
                if (bits.Length > 0)
                {
                    acc = "\\\\" + bits[0];
                    Add(bits[0], acc);
                }
                if (bits.Length > 1)
                {
                    acc = acc + "\\" + bits[1];
                    Add(bits[1], acc);
                }
                rest = bits.Length > 2 ? string.Join("\\", bits, 2, bits.Length - 2) : "";
            }

            string[] parts = rest.Split('\\');
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length == 0) continue;
                acc = string.IsNullOrEmpty(acc) ? parts[i] : acc + "\\" + parts[i];
                Add(parts[i], acc);
            }
        }

        private void Add(string text, string path)
        {
            Seg s = new Seg();
            s.Text = text;
            s.Path = path;
            segs.Add(s);
        }

        private static string DriveLabel(string root)
        {
            try
            {
                DriveInfo d = new DriveInfo(root);
                string letter = root.Substring(0, 2);
                if (d.IsReady && !string.IsNullOrEmpty(d.VolumeLabel)) return d.VolumeLabel + " (" + letter + ")";
                return "本地磁盘 (" + letter + ")";
            }
            catch { return root; }
        }

        private static string KnownName(string clsid)
        {
            string c = clsid.ToUpperInvariant();
            if (c.IndexOf("20D04FE0-3AEA-1069-A2D8-08002B30309D", StringComparison.Ordinal) >= 0) return "此电脑";
            if (c.IndexOf("645FF040-5081-101B-9F08-00AA002F954E", StringComparison.Ordinal) >= 0) return "回收站";
            if (c.IndexOf("208D2C60-3AEA-1069-A2D7-08002B30309D", StringComparison.Ordinal) >= 0) return "网络";
            if (c.IndexOf("679F85CB-0220-4080-B29B-5540CC05AAB6", StringComparison.Ordinal) >= 0) return "快速访问";
            if (c.IndexOf("F02C1A0D-BE21-4350-88B0-7367FC96EF3C", StringComparison.Ordinal) >= 0) return "网络";
            if (c.IndexOf("031E4825-7B94-4DC3-B131-E946B44C8DD5", StringComparison.Ordinal) >= 0) return "库";
            if (c.IndexOf("22877A6D-37A1-461A-91B0-DBDA5AAEBC99", StringComparison.Ordinal) >= 0) return "最近使用的项目";
            if (c.IndexOf("450D8FBA-AD25-11D0-98A8-0800361B1103", StringComparison.Ordinal) >= 0) return "我的文档";
            return "此电脑";
        }

        // ==================================================================
        // 绘制
        // ==================================================================
        private const int PadX = 8;
        private const int ArrowW = 14;

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (SolidBrush b = new SolidBrush(Theme.InputBack))
                g.FillRectangle(b, ClientRectangle);

            using (Pen p = new Pen(Theme.Border))
                g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);

            if (editing) return;

            // 从右往左排，超宽就丢掉前面几段
            int avail = Width - PadX * 2;
            List<Seg> show = new List<Seg>();
            int total = 0;
            for (int i = segs.Count - 1; i >= 0; i--)
            {
                int w = TextRenderer.MeasureText(segs[i].Text, SegFont).Width + 10;
                int need = w + (show.Count > 0 ? ArrowW : 0);
                if (total + need > avail && show.Count > 0) break;
                total += need;
                show.Insert(0, segs[i]);
            }

            int x = PadX;
            for (int i = 0; i < show.Count; i++)
            {
                int w = TextRenderer.MeasureText(show[i].Text, SegFont).Width + 10;
                Rectangle r = new Rectangle(x, 1, w, Height - 2);
                show[i].Bounds = r;

                bool isHover = (i == hover);
                if (isHover)
                {
                    using (SolidBrush b = new SolidBrush(Theme.Hover))
                        g.FillRectangle(b, r);
                    using (Pen p = new Pen(Theme.Border))
                        g.DrawRectangle(p, r.Left, r.Top, r.Width - 1, r.Height - 1);
                }

                TextRenderer.DrawText(g, show[i].Text, SegFont,
                    new Rectangle(r.Left + 5, r.Top, r.Width - 10, r.Height),
                    Theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

                if (isHover)
                {
                    using (Pen p = new Pen(Theme.Accent))
                        g.DrawLine(p, r.Left + 5, r.Bottom - 4, r.Right - 5, r.Bottom - 4);
                }

                x += w;
                if (i < show.Count - 1)
                {
                    TextRenderer.DrawText(g, "\u203A", SegFont,
                        new Rectangle(x, 1, ArrowW, Height - 2), Theme.TextDim,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    x += ArrowW;
                }
            }

            // 让 hover 命中的段也能被后续 hit-test 用到
            hitSegs.Clear();
            hitSegs.AddRange(show);
        }

        private readonly List<Seg> hitSegs = new List<Seg>();

        // ==================================================================
        // 交互
        // ==================================================================
        private Seg Hit(Point pt)
        {
            for (int i = 0; i < hitSegs.Count; i++)
                if (hitSegs[i].Bounds.Contains(pt)) return hitSegs[i];
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Seg s = Hit(e.Location);
            int idx = s == null ? -1 : hitSegs.IndexOf(s);
            if (idx != hover) { hover = idx; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hover != -1) { hover = -1; Invalidate(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Seg s = Hit(e.Location);
            if (s != null && Navigate != null)
            {
                Navigate(this, s.Path);
                return;
            }
            BeginEdit();
        }

        public void BeginEdit()
        {
            if (editing) return;
            editing = true;
            editor.Text = current;
            editor.Bounds = new Rectangle(3, 3, Math.Max(20, Width - 6), Math.Max(14, Height - 6));
            editor.Visible = true;
            editor.Focus();
            editor.SelectAll();
            Invalidate();
        }

        private void EndEdit(bool commit)
        {
            if (!editing) return;
            editing = false;
            string text = editor.Text.Trim();
            editor.Visible = false;
            Invalidate();
            Focus();
            if (commit && text.Length > 0 && Navigate != null) Navigate(this, text);
        }

        private void EditorKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                EndEdit(true);
            }
            else if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                EndEdit(false);
            }
        }

        public bool IsEditing { get { return editing; } }

        /// <summary>输入框模式下允许外部（地址栏文本框）接管回车。</summary>
        public TextBox Editor { get { return editor; } }
    }
}
