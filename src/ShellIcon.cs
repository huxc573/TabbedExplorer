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
            if (folderCache != null && folderCache.Width == target) return folderCache;
            try
            {
                uint sizeFlag = target > 16 ? SHGFI_LARGEICON : SHGFI_SMALLICON;
                SHFILEINFO fi = new SHFILEINFO();
                IntPtr r = SHGetFileInfo("dir", FILE_ATTRIBUTE_DIRECTORY, ref fi,
                    (uint)Marshal.SizeOf(typeof(SHFILEINFO)),
                    SHGFI_ICON | sizeFlag | SHGFI_USEFILEATTRIBUTES);
                if (r != IntPtr.Zero && fi.hIcon != IntPtr.Zero)
                {
                    try { folderCache = RenderIcon(fi.hIcon, target, false); }
                    finally { DestroyIcon(fi.hIcon); }
                }
            }
            catch (Exception ex) { Diag.Log("ShellIcon: 取文件夹图标失败 " + ex.Message); }
            return folderCache;
        }

        private static Bitmap folderCache;

        /// <summary>
        /// 取「某个具体东西长什么样」的图标 —— 收藏夹栏那一排用。
        /// 跟 `FolderIcon` 的区别是**这次要碰磁盘**（不带 `SHGFI_USEFILEATTRIBUTES`）：
        ///   - `.lnk` 快捷方式 → shell 会把目标解析掉，给的就是它指向那个文件夹/程序的图标；
        ///   - 普通目录 → 自定义图标（有 desktop.ini 的）也能拿到；
        ///   - 认不出来的路径 → 退回通用文件夹图标。
        /// 目标大于 16 就取 32px 那颗再缩（跟 FolderIcon 同一个理由：16px 放大是块状的）。
        /// </summary>
        public static Bitmap PathIcon(string path, int target)
        {
            if (string.IsNullOrEmpty(path)) return FolderIcon(target);
            try
            {
                uint sizeFlag = target > 16 ? SHGFI_LARGEICON : SHGFI_SMALLICON;
                SHFILEINFO fi = new SHFILEINFO();
                IntPtr r = SHGetFileInfo(path, 0, ref fi,
                    (uint)Marshal.SizeOf(typeof(SHFILEINFO)), SHGFI_ICON | sizeFlag);
                if (r != IntPtr.Zero && fi.hIcon != IntPtr.Zero)
                {
                    try { return RenderIcon(fi.hIcon, target, false); }
                    finally { DestroyIcon(fi.hIcon); }
                }
            }
            catch (Exception ex) { Diag.Log("ShellIcon: 取路径图标失败 " + path + " " + ex.Message); }
            return FolderIcon(target);
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
