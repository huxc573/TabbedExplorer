using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace TabbedExplorer
{
    /// <summary>
    /// 收藏夹栏（Ctrl+Shift+B 开关）—— 夹在标签条和内容之间，跟浏览器那条书签栏一个位置。
    ///
    /// 2026-09-22 川改的两条（原来那版是「直接读系统 %USERPROFILE%\Links」）：
    ///   1. **内容程序自己记** —— 数据在 `data\favorites.json`，见 `FavStore`。
    ///      往栏上**拖文件夹 / 文件**就加一项；某一项上右键可以「从收藏夹移除」。
    ///   2. **最左边一枚固定的收藏夹图标** —— 它是这条栏的「名牌」，不跟着内容横向滚动。
    ///
    /// 点击行为：文件夹 → 开成新标签；文件 → 交给系统（默认程序）打开。
    /// </summary>
    internal sealed class FavBar : Control
    {
        /// <summary>收藏夹栏高度（逻辑像素）。够放一行 16px 图标 + 文字。</summary>
        public const int StdHeight = 30;

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

        private sealed class Item
        {
            public string Path;      // 收藏的那个路径（文件夹或文件）
            public string Name;
            public Bitmap Icon;
            /// <summary>这一项占多宽（EnsureLayout 算，鼠标命中测试要用）。</summary>
            public int LayoutW;
        }

        private readonly List<Item> items = new List<Item>();
        private readonly ToolTip tips = new ToolTip();

        private int hoverIndex = -1;
        private bool hoverLead;
        private int scrollX;                 // 内容左移了多少（挤不下时靠它翻）
        private int contentWidth;
        private string tipKey;

        private readonly Font font;
        private static Bitmap folderFallback;
        private static Bitmap leadIcon;

        /// <summary>窗口失活时底色换成 ChromeOff（跟标签条、主窗口一个逻辑）。</summary>
        public bool Inactive
        {
            get { return inactive; }
            set
            {
                if (inactive == value) return;
                inactive = value;
                BackColor = TheBack;
                Invalidate();
            }
        }
        private bool inactive;
        private Color TheBack { get { return inactive ? Theme.ChromeOff : Theme.Chrome; } }

        public delegate void PathEventHandler(string path);
        /// <summary>点了某一项 —— 参数是那个**文件夹**路径（新标签页开它）。</summary>
        public event PathEventHandler ItemClicked;
        /// <summary>点了最左边那枚收藏夹图标（EmbedForm 拿它开数据目录）。</summary>
        public event EventHandler LeadClicked;
        /// <summary>上面那一排要重新读了（右键「刷新」）。</summary>
        public event EventHandler Reloaded;
        /// <summary>在收藏夹栏上右键点了「隐藏收藏夹栏」—— 真正隐藏由 Hub 做（它要同时刷托盘菜单和所有窗口）。</summary>
        public event EventHandler HideRequested;

        public FavBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = Px(StdHeight);
            font = new Font("Segoe UI", Px(12), FontStyle.Regular, GraphicsUnit.Pixel);
            BackColor = Theme.Chrome;
            AllowDrop = true;            // 往栏上拖文件夹 / 文件（见 OnDragDrop）
            tips.InitialDelay = 350;
            tips.AutoPopDelay = 8000;
            tips.ShowAlways = true;      // 窗口没激活也照弹（跟标签条一个理由）
            Theme.StyleTip(tips);        // 背景 / 字体跟着颜色模式
            Theme.Changed += delegate
            {
                BackColor = TheBack;
                if (!IsDisposed) Invalidate();
            };
        }

        /// <summary>现在有几项。</summary>
        public int Count { get { return items.Count; } }

        /// <summary>最左边那枚图标占的宽度（固定，不参与滚动）。</summary>
        private int LeadWidth { get { return Px(30); } }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) Reload();
        }

        /// <summary>按 `FavStore` 里的列表重建（拖进来 / 移除之后都要调一次）。</summary>
        public void Reload()
        {
            items.Clear();
            hoverIndex = -1;
            scrollX = 0;
            try
            {
                foreach (string p in FavStore.Items)
                {
                    Item it = new Item();
                    it.Path = p;
                    it.Name = FavStore.NameOf(p);
                    it.Icon = ShellIcon.PathIcon(p, Px(16));
                    items.Add(it);
                }
                Diag.Step("收藏夹栏: 载入 " + items.Count + " 项");
            }
            catch (Exception ex) { Diag.Log("收藏夹栏: 载入失败 " + ex.Message); }

            Invalidate();
            if (Reloaded != null) Reloaded(this, EventArgs.Empty);
        }

        // ------------------------------------------------------------------

        private void EnsureLayout()
        {
            contentWidth = Px(6);
            for (int i = 0; i < items.Count; i++)
            {
                Size t = TextRenderer.MeasureText(items[i].Name, font, new Size(Px(400), Px(20)),
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                int w = Px(6) + Px(16) + Px(5) + Math.Min(t.Width, Px(140)) + Px(8);
                items[i].LayoutW = w;
                contentWidth += w;
            }
            contentWidth += Px(6);
        }

        private Rectangle BoundsOf(int i)
        {
            int x = LeadWidth + Px(6) - scrollX;
            for (int k = 0; k < i; k++) x += items[k].LayoutW;
            return new Rectangle(x, Px(4), items[i].LayoutW, Height - Px(8));
        }

        private Rectangle LeadBounds()
        {
            return new Rectangle(Px(2), Px(3), LeadWidth - Px(4), Height - Px(6));
        }

        private int HitTest(Point p)
        {
            if (LeadBounds().Contains(p)) return -2;      // -2 = 左边那枚收藏夹图标
            EnsureLayout();
            for (int i = 0; i < items.Count; i++)
            {
                if (BoundsOf(i).Contains(p)) return i;
            }
            return -1;
        }

        private void ClampScroll()
        {
            int max = Math.Max(0, contentWidth - (Width - LeadWidth));
            if (scrollX > max) scrollX = max;
            if (scrollX < 0) scrollX = 0;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            EnsureLayout();
            Graphics g = e.Graphics;
            g.FillRectangle(new SolidBrush(TheBack), ClientRectangle);

            float stroke = Math.Max(1f, DpiScale);

            // ---- 左边那枚固定的「收藏夹」图标（川 2026-09-22 要的）----
            Rectangle lead = LeadBounds();
            if (hoverLead) g.FillRectangle(new SolidBrush(Theme.Hover), lead);
            Image li = LeadImage();
            if (li != null)
                g.DrawImage(li, new Rectangle(lead.Left + (lead.Width - Px(16)) / 2,
                                              lead.Top + (lead.Height - Px(16)) / 2, Px(16), Px(16)));
            // 图标右边一条淡竖线，跟收藏项隔开
            using (Pen p = new Pen(Theme.Border))
                g.DrawLine(p, lead.Right + Px(3), Px(6), lead.Right + Px(3), Height - Px(7));

            // ---- 收藏项 ----
            if (items.Count == 0)
            {
                TextRenderer.DrawText(g, "把文件夹或文件拖到这条栏上就能收藏",
                    font, new Rectangle(LeadWidth + Px(10), 0, Math.Max(1, Width - LeadWidth - Px(20)), Height),
                    Theme.TextDim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
            else
            {
                for (int i = 0; i < items.Count; i++)
                {
                    Rectangle r = BoundsOf(i);
                    if (r.Right < LeadWidth || r.Left > Width) continue;
                    if (i == hoverIndex) g.FillRectangle(new SolidBrush(Theme.Hover), r);

                    Bitmap ic = items[i].Icon;
                    if (ic == null)
                    {
                        if (folderFallback == null) folderFallback = ShellIcon.FolderIcon(Px(16));
                        ic = folderFallback;
                    }
                    if (ic != null)
                        g.DrawImage(ic, new Rectangle(r.Left + Px(6), r.Top + (r.Height - Px(16)) / 2, Px(16), Px(16)));

                    int tx = r.Left + Px(6) + Px(16) + Px(5);
                    int tw = r.Right - Px(8) - tx;
                    if (tw <= 0) continue;
                    TextRenderer.DrawText(g, items[i].Name, font,
                        new Rectangle(tx, r.Top, tw, r.Height),
                        i == hoverIndex ? Theme.Text : Theme.TextDim,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                }
            }

            // 底下一条淡淡的线，跟内容区分开
            g.DrawLine(new Pen(Theme.Border), 0, Height - 1, Width, Height - 1);
        }

        /// <summary>「收藏夹」那枚图标（MDL2 的星星）。取不到字形就退成系统那颗星星图标。</summary>
        private static Image LeadImage()
        {
            if (leadIcon != null) return leadIcon;
            try
            {
                Bitmap b = new Bitmap(Px(16), Px(16));
                using (Graphics g = Graphics.FromImage(b))
                using (Font f = new Font("Segoe MDL2 Assets", Px(13), FontStyle.Regular, GraphicsUnit.Pixel))
                {
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                    TextRenderer.DrawText(g, "\uE734", f, new Rectangle(0, 0, Px(16), Px(16)),
                        Theme.TextDim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.NoPadding);
                }
                leadIcon = b;
            }
            catch { }
            return leadIcon;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int i = HitTest(e.Location);
            bool hl = (i == -2);
            if (hl != hoverLead) { hoverLead = hl; Invalidate(); }
            if (i != hoverIndex) { hoverIndex = i; Invalidate(); }

            string key = i >= 0 ? ("fav:" + i) : (hl ? "fav:lead" : null);
            if (!string.Equals(key, tipKey, StringComparison.Ordinal))
            {
                tipKey = key;
                if (i < 0 && !hl) tips.Hide(this);
                else if (hl)
                {
                    Rectangle r = LeadBounds();
                    tips.Show("收藏夹\r\n把文件夹或文件拖到这条栏上就能加进来；点这儿打开数据目录",
                        this, r.Left, r.Bottom + Px(2), 8000);
                }
                else
                {
                    Rectangle r = BoundsOf(i);
                    int x = r.Left;                       // 提示贴在那项下面、左边对齐
                    if (x + Px(240) > Width) x = Math.Max(0, Width - Px(240));
                    tips.Show(items[i].Name + "\r\n" + items[i].Path, this, x, r.Bottom + Px(2), 8000);
                }
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hoverIndex = -1;
            hoverLead = false;
            tipKey = null;
            tips.Hide(this);
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            EnsureLayout();
            if (contentWidth <= Width - LeadWidth) return;
            scrollX -= e.Delta / 3;     // 一格滚一点，别一滚就飞到头
            ClampScroll();
            Invalidate();
        }

        // ------------------------------------------------------------------
        // 拖放：从文件列表里拖文件夹 / 文件过来（川 2026-09-22 要的）
        // ------------------------------------------------------------------

        protected override void OnDragEnter(DragEventArgs e)
        {
            base.OnDragEnter(e);
            e.Effect = HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        protected override void OnDragOver(DragEventArgs e)
        {
            base.OnDragOver(e);
            e.Effect = HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        private static bool HasFiles(DragEventArgs e)
        {
            try { return e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop); }
            catch { return false; }
        }

        protected override void OnDragDrop(DragEventArgs e)
        {
            base.OnDragDrop(e);
            try
            {
                string[] paths = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (paths == null || paths.Length == 0) return;

                int added = 0, dup = 0, bad = 0;
                string last = null;
                foreach (string p in paths)
                {
                    string name;
                    if (FavStore.Add(p, out name)) { added++; last = name; }
                    else if (string.IsNullOrEmpty(name)) bad++;
                    else dup++;
                }
                Diag.Step(string.Format("收藏夹栏: 拖入 {0} 项 -> 新增 {1} / 重复 {2} / 收不了 {3}",
                    paths.Length, added, dup, bad));
                if (added > 0)
                {
                    Reload();
                    Toast.Show("已加入收藏夹", added == 1 ? last : ("共 " + added + " 项"));
                }
                else if (dup > 0 && bad == 0) Toast.Show("收藏夹", dup == 1 ? "这一项已经在里面了。" : "这些都已经在里面了。");
                else Toast.Show("收藏夹", "这些位置没有真实路径（库 / 虚拟文件夹），收不了。");
            }
            catch (Exception ex)
            {
                Diag.Log("收藏夹栏: 拖放失败 " + ex.Message);
                Toast.Show("收藏夹", "加入失败：" + ex.Message);
            }
        }

        // ------------------------------------------------------------------

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            tips.Hide(this);
            tipKey = null;
            // 右键不用在这儿响应：等 MouseUp 再弹菜单（见下面的说明）。
            if (e.Button != MouseButtons.Left) return;

            int i = HitTest(e.Location);
            if (i == -2)
            {
                if (LeadClicked != null) LeadClicked(this, EventArgs.Empty);
                return;
            }
            if (i < 0) return;

            string p = items[i].Path;
            if (FavStore.IsFolder(p))
            {
                if (ItemClicked != null) ItemClicked(p);      // 文件夹 → 新标签
                return;
            }
            try
            {
                // 文件（含 .lnk）→ 交给系统默认程序
                Diag.Step("收藏夹栏: 打开文件 " + p);
                Process.Start(new ProcessStartInfo(p) { UseShellExecute = true });
            }
            catch (Exception ex) { Toast.Show("打不开", ex.Message); }
        }

        /// <summary>
        /// 右键菜单 —— **在 MouseUp 里弹**。写在 MouseDown 里的话，紧接着那条「右键抬起」消息会投到
        /// 刚弹出来的菜单窗口上（菜单抓着鼠标捕获），菜单把它当成「点在别处」当场关掉
        /// —— 表现就是「右键菜单一闪、点哪个条目都没反应」（川报的 bug 2 就是这么来的）。
        /// </summary>
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Right) return;

            int i = HitTest(e.Location);
            List<MenuItem> m = new List<MenuItem>();

            if (i >= 0)
            {
                string p = items[i].Path;
                if (FavStore.IsFolder(p))
                {
                    m.Add(new MenuItem("在新标签页打开", delegate { if (ItemClicked != null) ItemClicked(p); }));
                }
                else
                {
                    m.Add(new MenuItem("用默认程序打开", delegate
                    {
                        try { Process.Start(new ProcessStartInfo(p) { UseShellExecute = true }); }
                        catch (Exception ex) { Toast.Show("打不开", ex.Message); }
                    }));
                }
                m.Add(new MenuItem("复制完整路径", delegate
                {
                    try { Clipboard.SetText(p); }
                    catch (Exception ex) { Diag.Log("收藏夹栏: 复制失败 " + ex.Message); }
                }));
                m.Add(new MenuItem("-"));
                m.Add(new MenuItem("从收藏夹移除", delegate
                {
                    // 只从我们这份 json 里去掉，**不动磁盘上那个文件/文件夹**
                    if (FavStore.Remove(p)) { Reload(); Diag.Step("收藏夹栏: 移除 " + p); }
                }));
                m.Add(new MenuItem("-"));
            }

            m.Add(new MenuItem("刷新收藏夹栏", delegate { Reload(); }));
            m.Add(new MenuItem("打开收藏夹数据目录", delegate
            {
                try { Process.Start("explorer.exe", "\"" + AppPaths.DataDir + "\""); }
                catch (Exception ex) { Toast.Show("打不开数据目录", ex.Message); }
            }));
            m.Add(new MenuItem("-"));
            MenuItem hide = new MenuItem("隐藏收藏夹栏（Ctrl+Shift+B）");
            hide.Click += delegate { if (HideRequested != null) HideRequested(this, EventArgs.Empty); };
            m.Add(hide);

            Point at = e.Location;
            MenuFx.Show(MenuFx.Build(m.ToArray()), this, at,
                i >= 0 ? ("收藏夹项右键 " + items[i].Name) : "收藏夹栏右键");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (tips != null) tips.Dispose();
                if (font != null) font.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
