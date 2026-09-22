using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TabbedExplorer
{
    /// <summary>
    /// 按 shell 自己声明的那份「图标源」取图标，不自己画。
    ///
    /// 两件事都是实测出来的，别改着玩：
    ///
    /// 1) 图标源：explorer 的命令在 shell 命令库里声明图标时用「负号 + 资源 ID」
    ///    （HKLM\...\Explorer\CommandStore\shell\Windows.undo 的 Icon = "imageres.dll,-5315"）。
    ///    ExtractIconEx 的 nIconIndex 传负数时正好按资源 ID 取，跟 explorer 取到的是同一颗。
    ///
    /// 2) 取 **small（16px）** 那颗再放到目标尺寸，不要用 large（32px）缩小：
    ///    原生 QAT 就是这个取法 —— 实测「属性」「新建文件夹」用 small 与原生窗口
    ///    逐像素 IoU = 1.000（连墨迹像素个数都一模一样），用 large 只有 0.88。
    ///
    /// 3) 撤销/重做/删除 这三颗，命令库给的是**彩色新版**（蓝箭头、红叉），
    ///    而原生 QAT 画的是**单色灰版**，所以这几颗要按亮度转灰（gray = true）。
    ///    「删除」转灰后与原生逐像素完全一致（墨迹框 18x18、142 个墨迹像素、最暗 (58,58,58) 全对上）。
    ///    属性/新建文件夹**不能转灰** —— 原生保留了白纸和黄色的颜色。
    /// </summary>
    internal static class ShellIcon
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "ExtractIconExW")]
        private static extern uint ExtractIconEx(string file, int index,
            IntPtr[] large, IntPtr[] small, uint count);

        [DllImport("user32.dll")]
        private static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr hIcon,
            int cx, int cy, uint istep, IntPtr hbrFlickerFree, uint flags);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        // ---- SHGetFileInfo：向 shell 要「一个文件夹长什么样」 ----
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
            ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        private const uint SHGFI_ICON = 0x000000100;
        private const uint SHGFI_LARGEICON = 0x000000000;   // ⚠ 真的是 0，不是 1
        private const uint SHGFI_SMALLICON = 0x000000001;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;

        private const uint DI_NORMAL = 0x0003;

        private static string UnderSystem32(string file)
        {
            return System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), file);
        }

        // ==================================================================
        // 图标缓存（用户：「程序本身运行时内存占用高，尝试优化下」——
        //   这是里面最实在的一刀，而且同时省的是**磁盘查询 + 内存**两样东西）
        //
        // 为什么非加不可：`PathIcon` 每一次调用都是 **SHGetFileInfo（要碰磁盘 / 问 shell）
        //   + new Bitmap**，而两个管理器（书签 / 历史）是在 `OnPaint` 里**逐行**调它的 ——
        //   窗口一动、鼠标一划、拖一下大小，就是几十次磁盘查询 + 几十张新位图，
        //   全堆在 GC 堆上等着收。原先唯一的缓存是文件夹图标，而且是**单槽**的
        //   （`folderCache.Width == target` 才算命中）——标签用 16、管理器用 15/16，
        //   几个尺寸来回一套就把它顶掉，等于没有。
        //
        // 现在一张表按「尺寸 + 路径」缓存，上限 `CacheMax`，超了整表丢掉重来
        // （不做 LRU —— 命中的永远是眼前这几行，一刀切最省事）。
        // ⚠ 缓存里的位图**一律不 Dispose**：谁要用谁自己留着引用（`FavBar.Item.Icon` 就是），
        //   真没人引用了 GC 会收掉（Bitmap 有自己的终结器去释放 GDI+ 那一份）。
        //   反过来说：**调用方拿到缓存里的那张就别 Dispose**，不然会把别人的图标一起干掉。
        // ==================================================================

        private const int CacheMax = 400;
        private static readonly System.Collections.Generic.Dictionary<string, Bitmap> cache =
            new System.Collections.Generic.Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);

        private static Bitmap CacheGet(string key)
        {
            Bitmap b;
            return cache.TryGetValue(key, out b) ? b : null;
        }

        private static void CachePut(string key, Bitmap b)
        {
            if (b == null) return;
            if (cache.Count >= CacheMax) cache.Clear();
            cache[key] = b;
        }

        /// <summary>标准灰度（ITU-R 601-2，和 PIL 的 convert("L") 同权重）。</summary>
        private static ImageAttributes GrayAttributes()
        {
            ColorMatrix m = new ColorMatrix(new float[][]
            {
                new float[] { 0.299f, 0.299f, 0.299f, 0f, 0f },
                new float[] { 0.587f, 0.587f, 0.587f, 0f, 0f },
                new float[] { 0.114f, 0.114f, 0.114f, 0f, 0f },
                new float[] { 0f, 0f, 0f, 1f, 0f },
                new float[] { 0f, 0f, 0f, 0f, 1f },
            });
            ImageAttributes a = new ImageAttributes();
            a.SetColorMatrix(m, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
            return a;
        }

        /// <summary>
        /// 取图标并渲染成 target×target 的 32bppArgb 位图（保留 alpha）。
        /// rid 传负数（资源 ID）。gray = 按亮度转成单色（原生 QAT 对撤销/重做/删除就是这么干的）。
        /// 取不到返回 null。
        /// </summary>
        public static Bitmap LoadBitmap(string file, int rid, int target, bool gray)
        {
            IntPtr[] big = new IntPtr[1];
            IntPtr[] small = new IntPtr[1];
            uint got = ExtractIconEx(UnderSystem32(file), rid, big, small, 1);
            if (got == 0) return null;

            // 优先 small：原生用的就是这颗（见类注释第 2 条）
            IntPtr src = small[0] != IntPtr.Zero ? small[0] : big[0];
            if (src == IntPtr.Zero) src = big[0];

            Bitmap result = null;
            try
            {
                if (src != IntPtr.Zero) result = RenderIcon(src, target, gray);
            }
            catch (Exception ex)
            {
                Diag.Log("ShellIcon: 渲染 " + file + "," + rid + " 失败 " + ex.Message);
            }
            finally
            {
                if (big[0] != IntPtr.Zero) DestroyIcon(big[0]);
                if (small[0] != IntPtr.Zero) DestroyIcon(small[0]);
            }
            return result;
        }

        /// <summary>
        /// 取「文件夹」图标（标签页左边那颗）。
        ///
        /// 用 `SHGetFileInfo` + `SHGFI_USEFILEATTRIBUTES` + 「目录」属性：
        /// **只看属性、不碰磁盘** —— 不会因为路径是网络盘/不存在而卡住或返回空白图标，
        /// 拿到的就是 shell 自己的通用文件夹图标（跟资源管理器里长的一样）。
        ///
        /// ⚠ 取 large(32px) 还是 small(16px)：**目标大于 16 就取 32 那颗**。
        /// 标签图标是 24px（150% DPI 下的 Px(16)），拿 16px 的放大会块状；
        /// 拿 32px 的缩下来才细腻。目标正好 16（100% DPI）时才用 small，那时是像素级相等。
        /// </summary>
        public static Bitmap FolderIcon(int target)
        {
            string key = "folder|" + target;
            Bitmap hit = CacheGet(key);
            if (hit != null) return hit;
            try
            {
                uint sizeFlag = target > 16 ? SHGFI_LARGEICON : SHGFI_SMALLICON;
                SHFILEINFO fi = new SHFILEINFO();
                IntPtr r = SHGetFileInfo("dir", FILE_ATTRIBUTE_DIRECTORY, ref fi,
                    (uint)Marshal.SizeOf(typeof(SHFILEINFO)),
                    SHGFI_ICON | sizeFlag | SHGFI_USEFILEATTRIBUTES);
                if (r != IntPtr.Zero && fi.hIcon != IntPtr.Zero)
                {
                    Bitmap b = null;
                    try { b = RenderIcon(fi.hIcon, target, false); }
                    finally { DestroyIcon(fi.hIcon); }
                    if (b != null) { CachePut(key, b); return b; }
                }
            }
            catch (Exception ex) { Diag.Log("ShellIcon: 取文件夹图标失败 " + ex.Message); }
            return null;
        }

        /// <summary>
        /// 取「某个具体东西长什么样」的图标 —— 书签栏那一排用。
        /// 跟 `FolderIcon` 的区别是**这次要碰磁盘**（不带 `SHGFI_USEFILEATTRIBUTES`）：
        ///   - `.lnk` 快捷方式 → shell 会把目标解析掉，给的就是它指向那个文件夹/程序的图标；
        ///   - 普通目录 → 自定义图标（有 desktop.ini 的）也能拿到；
        ///   - 认不出来的路径 → 退回通用文件夹图标。
        /// 目标大于 16 就取 32px 那颗再缩（跟 FolderIcon 同一个理由：16px 放大是块状的）。
        /// </summary>
        public static Bitmap PathIcon(string path, int target)
        {
            if (string.IsNullOrEmpty(path)) return FolderIcon(target);
            string key = target + "|" + path;
            Bitmap hit = CacheGet(key);
            if (hit != null) return hit;
            try
            {
                uint sizeFlag = target > 16 ? SHGFI_LARGEICON : SHGFI_SMALLICON;
                SHFILEINFO fi = new SHFILEINFO();
                IntPtr r = SHGetFileInfo(path, 0, ref fi,
                    (uint)Marshal.SizeOf(typeof(SHFILEINFO)), SHGFI_ICON | sizeFlag);
                if (r != IntPtr.Zero && fi.hIcon != IntPtr.Zero)
                {
                    Bitmap b = null;
                    try { b = RenderIcon(fi.hIcon, target, false); }
                    finally { DestroyIcon(fi.hIcon); }
                    if (b != null) { CachePut(key, b); return b; }
                }
            }
            catch (Exception ex) { Diag.Log("ShellIcon: 取路径图标失败 " + path + " " + ex.Message); }
            return FolderIcon(target);
        }

        // ==================================================================
        // 「书签文件夹」—— 我们自己的分组文件夹专用图标
        //   （用户：「书签自有文件夹换个图标，避免和系统文件夹图标重复」）
        //
        // 为什么非自己画不可：书签树里的文件夹以前借的就是**系统那颗黄色文件夹**，
        // 于是「书签里的分组」和「磁盘上真实的文件夹」在管理器和书签栏上长得一模一样，
        // 一眼分不出哪个是书签分组、哪个是真路径。
        //
        // 样子：蓝色文件夹（比系统那颗的曼尼拉黄冷）+ 右下角一颗金星（书签 / 收藏那层意思）。
        // 颜色**故意不跟主题走** —— 它是「这是书签」的标识，深色浅色下都得是同一个样子；
        // 蓝底 + 金徽在两种背景上都看得清。
        //
        // 画法：先在 `f` 倍大的画布上用 GDI+ 画好（开抗锯齿），再缩到目标尺寸 ——
        // 15px 这种尺寸上直接画没有抗锯齿的余地，只有「大图缩下来」才干净（同 `RenderIcon` 的思路）。
        // ⚠ 传出去的位图**不 Dispose**：它进的是上面那张 `cache`，调用方拿到缓存里那张就别再动它。
        // ==================================================================

        private static readonly Color FavFolderBody = Color.FromArgb(59, 130, 246);    // 主体蓝
        private static readonly Color FavFolderTab = Color.FromArgb(125, 176, 252);    // 耳（亮一档，小尺寸下靠它认出是文件夹）
        private static readonly Color FavFolderEdge = Color.FromArgb(29, 78, 216);     // 描边
        private static readonly Color FavFolderStar = Color.FromArgb(255, 197, 61);    // 金徽

        /// <summary>
        /// 书签自己的文件夹图标（`FavNode.IsFolder` 那种）。
        /// 画不出来就退回系统那颗 —— 难看总比空白强。
        /// </summary>
        public static Bitmap FavFolderIcon(int target)
        {
            if (target <= 0) return null;
            string key = "favfolder|" + target;
            Bitmap hit = CacheGet(key);
            if (hit != null) return hit;
            try
            {
                Bitmap b = DrawFavFolder(target);
                if (b != null) { CachePut(key, b); return b; }
            }
            catch (Exception ex) { Diag.Log("ShellIcon: 画书签文件夹失败 " + ex.Message); }
            return FolderIcon(target);
        }

        private static Bitmap DrawFavFolder(int target)
        {
            // 小图标多重采样、大图标少采样（32px 以上再乘 8 就是 256px 的白费劲）
            int f = target <= 32 ? 8 : (target <= 64 ? 4 : 2);
            int s = target * f;
            Bitmap big = new Bitmap(s, s, PixelFormat.Format32bppArgb);
            try
            {
                using (Graphics g = Graphics.FromImage(big))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);

                    // 先两层深色打底（耳 + 主体），再把亮色压上去 —— 1px 级别的描边不用 Pen 画，
                    // 直接「大一圈的深色 + 小一圈的亮色」最稳。
                    float e = s * 0.02f;               // 描边厚度
                    float L = s * 0.045f, R = s * 0.955f;          // 主体左右
                    float T = s * 0.190f, B = s * 0.880f;          // 主体上下
                    float TL = s * 0.100f, TR = s * 0.360f, TX = s * 0.500f;   // 耳

                    FillRound(g, L - e, TL - e, TX + e, TR + e, s * 0.070f, FavFolderEdge);
                    FillRound(g, L - e, T - e, R + e, B + e, s * 0.110f, FavFolderEdge);
                    FillRound(g, L, TL, TX, TR, s * 0.065f, FavFolderTab);
                    FillRound(g, L, T, R, B, s * 0.105f, FavFolderBody);

                    // 右下角那颗金星：正五角、尖朝上
                    float cx = s * 0.745f, cy = s * 0.735f, ro = s * 0.235f, ri = s * 0.100f;
                    PointF[] pts = new PointF[10];
                    for (int i = 0; i < 10; i++)
                    {
                        double a = -Math.PI / 2 + i * Math.PI / 5;
                        double r = (i % 2 == 0) ? ro : ri;
                        pts[i] = new PointF((float)(cx + r * Math.Cos(a)), (float)(cy + r * Math.Sin(a)));
                    }
                    using (SolidBrush b = new SolidBrush(FavFolderStar)) g.FillPolygon(b, pts);
                }

                Bitmap result = new Bitmap(target, target, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(result))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.Clear(Color.Transparent);
                    g.DrawImage(big, new Rectangle(0, 0, target, target));
                }
                return result;
            }
            finally { big.Dispose(); }
        }

        /// <summary>画一个圆角矩形（给的是左上 / 右下两个角，r = 圆角半径）。</summary>
        private static void FillRound(Graphics g, float x1, float y1, float x2, float y2,
                                      float r, Color c)
        {
            float w = x2 - x1, h = y2 - y1;
            if (w <= 0 || h <= 0) return;
            float d = Math.Min(r * 2f, Math.Min(w, h));
            using (GraphicsPath p = new GraphicsPath())
            {
                p.AddArc(x1, y1, d, d, 180, 90);
                p.AddArc(x2 - d, y1, d, d, 270, 90);
                p.AddArc(x2 - d, y2 - d, d, d, 0, 90);
                p.AddArc(x1, y2 - d, d, d, 90, 90);
                p.CloseFigure();
                using (SolidBrush b = new SolidBrush(c)) g.FillPath(b, p);
            }
        }

        /// <summary>把一个 HICON 画成 target×target 的 32bppArgb 位图（保留 alpha）。</summary>
        private static Bitmap RenderIcon(IntPtr hIcon, int target, bool gray)
        {
            Bitmap raw = new Bitmap(target, target, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(raw))
            {
                g.Clear(Color.Transparent);
                IntPtr hdc = g.GetHdc();
                try { DrawIconEx(hdc, 0, 0, hIcon, target, target, 0, IntPtr.Zero, DI_NORMAL); }
                finally { g.ReleaseHdc(hdc); }
            }
            if (!gray) return raw;

            Bitmap result = new Bitmap(target, target, PixelFormat.Format32bppArgb);
            try
            {
                using (Graphics g = Graphics.FromImage(result))
                {
                    g.Clear(Color.Transparent);
                    using (ImageAttributes ia = GrayAttributes())
                    {
                        g.DrawImage(raw, new Rectangle(0, 0, target, target),
                            0, 0, target, target, GraphicsUnit.Pixel, ia);
                    }
                }
            }
            catch { }
            finally { raw.Dispose(); }
            return result;
        }

        /// <summary>
        /// 把一个**别人的** HICON 画成我们的位图（典型：读 explorer 窗口自己挂的那颗文件夹图标，
        /// 见 `EmbedApi.WindowIcon`）。
        /// ⚠ **不 DestroyIcon** —— 句柄不是我们创建的；调用方拿到位图后请立刻不再持有原句柄。
        /// </summary>
        public static Bitmap FromForeignHIcon(IntPtr hIcon, int target)
        {
            if (hIcon == IntPtr.Zero || target <= 0) return null;
            try { return RenderIcon(hIcon, target, false); }
            catch (Exception ex)
            {
                Diag.Log("ShellIcon: 渲染外部 HICON 失败 " + ex.Message);
                return null;
            }
        }

        private static Icon appSmall, appBig;

        /// <summary>
        /// **程序自己的图标** —— exe 里用 `/win32icon:app.ico` 编进去的那颗。
        /// 托盘、任务栏、Alt+Tab 全用它；取不到才退回系统默认图标。
        /// （以前托盘借的是 shell32.dll 的「新建文件夹」，那是资源管理器的图标，不是我们的。）
        /// </summary>
        public static Icon AppIcon(bool small)
        {
            try
            {
                if (small && appSmall != null) return appSmall;
                if (!small && appBig != null) return appBig;

                Icon src = null;
                try { src = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
                catch { }
                if (src == null) return SystemIcons.Application;

                int sz = small ? SystemInformation.SmallIconSize.Width : 32;
                Icon pick;
                try { pick = new Icon(src, sz, sz); }
                catch { pick = (Icon)src.Clone(); }

                if (small) appSmall = pick; else appBig = pick;
                return pick;
            }
            catch { return SystemIcons.Application; }
        }

        /// <summary>取图标做成 Icon（托盘用）。取不到返回 null。</summary>
        public static Icon LoadIcon(string file, int rid)
        {
            IntPtr[] big = new IntPtr[1];
            IntPtr[] small = new IntPtr[1];
            uint got = ExtractIconEx(UnderSystem32(file), rid, big, small, 1);
            Icon result = null;
            if (got != 0)
            {
                IntPtr src = small[0] != IntPtr.Zero ? small[0] : big[0];
                if (src != IntPtr.Zero)
                {
                    try { result = (Icon)Icon.FromHandle(src).Clone(); }
                    catch (Exception ex) { Diag.Log("ShellIcon: 转 Icon 失败 " + ex.Message); }
                }
            }
            if (big[0] != IntPtr.Zero) DestroyIcon(big[0]);
            if (small[0] != IntPtr.Zero) DestroyIcon(small[0]);
            return result;
        }
    }
}
