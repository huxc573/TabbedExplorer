using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace TabbedExplorer
{
    /// <summary>
    /// 收藏夹栏（Ctrl+Shift+B 开关）—— 夹在标签条和内容之间，跟浏览器那条书签栏一个位置。
    ///
    /// 内容直接读**系统那个收藏夹**：`%USERPROFILE%\Links`（就是资源管理器左边「收藏夹」里
    /// 那几个东西，本机是 `D:\Users\a\Links`，里面两个 `.lnk`：桌面 / 下载）。
    /// 好处是川在资源管理器里往「收藏夹」拖一个文件夹，我们这边不用做任何界面就多一项 ——
    /// 不搞自己一套收藏数据，免得两处不同步。
    ///
    /// `.lnk` 要解析出真实目标：我们要的是**那个文件夹**，不是快捷方式文件本身。
    /// 解析走 `ResolveShortcut`，见那里的注释（为什么不用 COM）。
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
            public string Name;
            public string Path;      // 解析后的真实目标（要开的那个文件夹）
            public string Source;    // 磁盘上那个条目本身（.lnk 原样，取图标用）
            public Bitmap Icon;
            /// <summary>这一项占多宽（EnsureLayout 算，鼠标命中测试要用）。</summary>
            public int LayoutW;
        }

        private readonly List<Item> items = new List<Item>();
        private readonly ToolTip tips = new ToolTip();

        private int hoverIndex = -1;
        private int scrollX;                 // 内容左移了多少（挤不下时靠它翻）
        private int contentWidth;
        private string tipKey;

        private readonly Font font;
        private static Bitmap folderFallback;

        public delegate void PathEventHandler(string path);
        /// <summary>点了某一项 —— 参数是**解析后的目标路径**（新标签页开它）。</summary>
        public event PathEventHandler ItemClicked;
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
            tips.InitialDelay = 350;
            tips.AutoPopDelay = 8000;
            tips.ShowAlways = true;      // 窗口没激活也照弹（跟标签条一个理由）
            Theme.Changed += delegate
            {
                BackColor = Theme.Chrome;
                if (!IsDisposed) Invalidate();
            };
        }

        /// <summary>现在有 bookmark 吗（没有就画一行灰字提示怎么加）。</summary>
        public int Count { get { return items.Count; } }

        /// <summary>收藏夹在磁盘上的位置（右键「打开收藏夹文件夹」和提示文字里用）。</summary>
        public static string LinksDir
        {
            get
            {
                try
                {
                    return Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Links");
                }
                catch { return ""; }
            }
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            // 每次显出来都重读一遍：川在资源管理器里刚拖进去的项，切一下就能看到。
            if (Visible) Reload();
        }

        /// <summary>重读收藏夹目录。</summary>
        public void Reload()
        {
            items.Clear();
            hoverIndex = -1;
            scrollX = 0;
            try
            {
                string dir = LinksDir;
                if (Directory.Exists(dir))
                {
                    List<string> entries = new List<string>(Directory.GetFileSystemEntries(dir));
                    entries.Sort(StringComparer.OrdinalIgnoreCase);
                    foreach (string raw in entries)
                    {
                        string name = Path.GetFileName(raw);
                        if (string.Equals(name, "desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                        // 隐藏/系统项不摆上来（收藏夹里偶尔会有残留）
                        try
                        {
                            FileAttributes fa = File.GetAttributes(raw);
                            if ((fa & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                        }
                        catch { }

                        string target = ResolveShortcut(raw);
                        Item it = new Item();
                        it.Source = raw;
                        it.Path = target;
                        it.Name = DisplayName(raw, target);
                        it.Icon = ShellIcon.PathIcon(raw, Px(16));
                        items.Add(it);
                    }
                }
                Diag.Step("收藏夹栏: 读到 " + items.Count + " 项（" + dir + "）");
            }
            catch (Exception ex) { Diag.Log("收藏夹栏: 读失败 " + ex.Message); }

            Invalidate();
            if (Reloaded != null) Reloaded(this, EventArgs.Empty);
        }

        /// <summary>快捷方式显示什么名字：优先磁盘上那个名字（`.lnk` 去掉后缀），没有才用目标的末级目录名。</summary>
        private static string DisplayName(string source, string target)
        {
            try
            {
                string n = Path.GetFileName(source);
                if (n.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                    n = n.Substring(0, n.Length - 4);
                if (!string.IsNullOrEmpty(n)) return n;
            }
            catch { }
            try
            {
                string d = target.TrimEnd('\\', '/');
                int i = d.LastIndexOf('\\');
                if (i >= 0 && i < d.Length - 1) return d.Substring(i + 1);
            }
            catch { }
            return target ?? "";
        }

        /// <summary>
        /// `.lnk` → 它指向的文件夹。**不走 COM**（`IShellLink` 要自己声明 vtable，
        /// 本机没法调试，一旦接错就是进程级崩溃）。改成一个「够用」的办法：
        ///
        /// `.lnk` 的 `LinkInfo` 段里存着目标的**本地路径字符串**（ANSI 和/或 UTF-16），
        /// 直接扫字节把它捞出来，再用 `Directory.Exists` 验一下 —— 验不过就当解析失败。
        /// 本机实测（`Desktop.lnk` / `Downloads.lnk`）都能扫到 `D:\Users\Desktop` 这种。
        ///
        /// 扫不到就**原样返回 `.lnk` 路径**：`explorer.exe` 自己是会解析快捷方式的，
        /// 开还是开得对，只是万一失败时表现会差一点。绝不猜、绝不返回空。
        /// </summary>
        private static string ResolveShortcut(string raw)
        {
            try
            {
                if (Directory.Exists(raw) && !raw.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                    return raw;                                  // 本来就是个目录
                if (!raw.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return raw;

                byte[] b = File.ReadAllBytes(raw);

                string ansi = ScanPath(b, false);
                if (IsDir(ansi)) return Normalize(ansi);

                string wide = ScanPath(b, true);
                if (IsDir(wide)) return Normalize(wide);

                // 相对路径那种（`..\..\Desktop`）：按 lnk 所在目录算一下也算数
                string rel = ScanRelative(b);
                if (!string.IsNullOrEmpty(rel))
                {
                    try
                    {
                        string full = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(raw), rel));
                        if (IsDir(full)) return Normalize(full);
                    }
                    catch { }
                }
            }
            catch (Exception ex) { Diag.Log("收藏夹栏: 解析 " + raw + " 失败 " + ex.Message); }
            return raw;
        }

        private static bool IsDir(string p)
        {
            try { return !string.IsNullOrEmpty(p) && Directory.Exists(p); }
            catch { return false; }
        }

        private static string Normalize(string p)
        {
            try { return p.TrimEnd('\0', ' ') ; } catch { return p; }
        }

        /// <summary>在字节流里找 `X:\...` 形式的绝对路径（`wide=false` 找 ANSI，`true` 找 UTF-16LE）。</summary>
        private static string ScanPath(byte[] b, bool wide)
        {
            int step = wide ? 2 : 1;
            for (int i = 0; i + 4 < b.Length; i++)
            {
                // 盘符：字母 + ':' + '\'
                bool letter = (b[i] >= 'A' && b[i] <= 'Z') || (b[i] >= 'a' && b[i] <= 'z');
                if (!letter) continue;
                if (b[i + 1] != ':') { i += step - 1; continue; }
                if (wide)
                {
                    if (b[i + 2] != 0 || b[i + 3] != '\\' ) { i += 1; continue; }
                }
                else
                {
                    if (b[i + 2] != '\\') continue;
                }

                StringBuilder sb = new StringBuilder();
                int j = i;
                while (j < b.Length - (wide ? 1 : 0))
                {
                    char c = wide ? (char)(b[j] | (b[j + 1] << 8)) : (char)b[j];
                    if (c == '\0') break;
                    if (c < ' ') break;
                    if (c == '\\' || c == '/' || c == ':' || c == '.' || c == '_' || c == '-' ||
                        c == ' ' || c == '(' || c == ')' || c == '#' || c == '+' || c == '&' ||
                        c == '\'' || c == ',' || c == '~' || c == '!' || c == '@' || c == '$' ||
                        c == '=' || c == '{' || c == '}' || c == '[' || c == ']' || c == '^' ||
                        (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                        c > 127)
                    {
                        sb.Append(c);
                        j += wide ? 2 : 1;
                    }
                    else break;
                }
                string s = sb.ToString();
                if (s.Length >= 4) return s.TrimEnd('\\', '/');
            }
            return null;
        }

        /// <summary>找 `..\..\Foo` 这种相对路径（`LinkInfo` 里常见，UTF-16 存）。</summary>
        private static string ScanRelative(byte[] b)
        {
            for (int i = 0; i + 8 < b.Length; i++)
            {
                if (b[i] != '.' || b[i + 1] != 0) continue;
                if (b[i + 2] != '.' || b[i + 3] != 0) continue;
                if (b[i + 4] != '\\' || b[i + 5] != 0) continue;
                StringBuilder sb = new StringBuilder();
                int j = i;
                while (j + 1 < b.Length)
                {
                    char c = (char)(b[j] | (b[j + 1] << 8));
                    if (c == '\0') break;
                    if (c < ' ' || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' ||
                        c == '>' || c == '|') break;
                    sb.Append(c);
                    j += 2;
                }
                string s = sb.ToString();
                if (s.Length > 3 && s.IndexOf('\\') >= 0) return s;
            }
            return null;
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
            int x = Px(6) - scrollX;
            for (int k = 0; k < i; k++) x += items[k].LayoutW;
            return new Rectangle(x, Px(4), items[i].LayoutW, Height - Px(8));
        }

        private int HitTest(Point p)
        {
            EnsureLayout();
            for (int i = 0; i < items.Count; i++)
            {
                if (BoundsOf(i).Contains(p)) return i;
            }
            return -1;
        }

        private void ClampScroll()
        {
            int max = Math.Max(0, contentWidth - Width);
            if (scrollX > max) scrollX = max;
            if (scrollX < 0) scrollX = 0;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            EnsureLayout();
            Graphics g = e.Graphics;
            g.FillRectangle(new SolidBrush(Theme.Chrome), ClientRectangle);

            if (items.Count == 0)
            {
                TextRenderer.DrawText(g, "收藏夹栏（把文件夹拖进 " + LinksDir + " 就会出现在这儿）",
                    font, new Rectangle(Px(10), 0, Width - Px(20), Height),
                    Theme.TextDim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
            else
            {
                for (int i = 0; i < items.Count; i++)
                {
                    Rectangle r = BoundsOf(i);
                    if (r.Right < 0 || r.Left > Width) continue;
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

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int i = HitTest(e.Location);
            if (i != hoverIndex) { hoverIndex = i; Invalidate(); }

            string key = i >= 0 ? ("fav:" + i) : null;
            if (!string.Equals(key, tipKey, StringComparison.Ordinal))
            {
                tipKey = key;
                if (i < 0) tips.Hide(this);
                else
                {
                    Rectangle r = BoundsOf(i);
                    // 提示贴在那一项下面、左边对齐（太长就自己往左挪，别跑出屏幕）
                    int x = r.Left;
                    if (x + Px(200) > Width) x = Math.Max(0, Width - Px(200));
                    tips.Show(items[i].Name + "\r\n" + items[i].Path, this, x, r.Bottom + Px(2), 8000);
                }
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hoverIndex = -1;
            tipKey = null;
            tips.Hide(this);
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            EnsureLayout();
            if (contentWidth <= Width) return;
            scrollX -= e.Delta / 3;     // 一格滚一点，别一滚就飞到头
            ClampScroll();
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            tips.Hide(this);
            tipKey = null;
            int i = HitTest(e.Location);

            if (e.Button == MouseButtons.Left)
            {
                if (i >= 0 && ItemClicked != null) ItemClicked(items[i].Path);
                return;
            }

            if (e.Button == MouseButtons.Right)
            {
                ContextMenu m = new ContextMenu();
                if (i >= 0)
                {
                    Item it = items[i];
                    m.MenuItems.Add(new MenuItem("在新标签页打开", delegate
                    {
                        if (ItemClicked != null) ItemClicked(it.Path);
                    }));
                    m.MenuItems.Add(new MenuItem("复制完整路径", delegate
                    {
                        try { Clipboard.SetText(it.Path); Toast.Show("已复制", it.Path); }
                        catch (Exception ex) { Diag.Log("收藏夹栏: 复制失败 " + ex.Message); }
                    }));
                    m.MenuItems.Add(new MenuItem("-"));
                }
                m.MenuItems.Add(new MenuItem("刷新收藏夹栏", delegate { Reload(); }));
                m.MenuItems.Add(new MenuItem("打开收藏夹文件夹", delegate
                {
                    try { System.Diagnostics.Process.Start("explorer.exe", "\"" + LinksDir + "\""); }
                    catch (Exception ex) { Toast.Show("打不开收藏夹文件夹", ex.Message); }
                }));
                m.MenuItems.Add(new MenuItem("-"));
                MenuItem hide = new MenuItem("隐藏收藏夹栏（Ctrl+Shift+B）");
                hide.Click += delegate { if (HideRequested != null) HideRequested(this, EventArgs.Empty); };
                m.MenuItems.Add(hide);

                // ⚠ 别在 MouseDown 里同步弹（就是齿轮卡死那个坑）：鼠标消息没走完就切鼠标捕获，
                // 菜单的模态循环跟控件的捕获互相等。推一轮消息再弹。
                Point at = e.Location;
                BeginInvoke((MethodInvoker)delegate
                {
                    try { m.Show(this, at); }
                    catch (Exception ex) { Diag.Log("收藏夹栏: 弹菜单失败 " + ex.Message); }
                    finally { try { m.Dispose(); } catch { } }
                });
            }
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
