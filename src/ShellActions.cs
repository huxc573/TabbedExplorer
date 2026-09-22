using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TabbedExplorer
{
    /// <summary>
    /// 对当前标签页里的 shell 视图执行命令。
    /// 思路：能用 shell 自己做的一律交给它（按键派发 → 它自己的剪贴板/回收站/重命名逻辑），
    /// 我们只做它没有的（全局文件夹选项、ZIP 压缩、面包屑）。
    /// </summary>
    internal static class ShellActions
    {
        private const byte VK_CTRL = 0x11;
        private const byte VK_SHIFT = 0x10;
        private const byte VK_MENU = 0x12;
        private const uint KEYUP = 0x0002;

        private const uint WM_LBUTTONDOWN = 0x0201;
        private const uint WM_LBUTTONUP = 0x0202;
        private const uint HDM_GETITEMCOUNT = 0x1200;
        private const uint HDM_GETITEMRECT = 0x1207;

        public static void Pump(int times)
        {
            for (int i = 0; i < times; i++)
            {
                try { Application.DoEvents(); }
                catch { }
            }
        }

        /// <summary>把一个组合键派发给 shell 视图自己处理。</summary>
        public static void Send(IntPtr focusTarget, Keys key, bool ctrl, bool shift, bool alt)
        {
            try
            {
                if (focusTarget != IntPtr.Zero) NativeMethods.SetFocus(focusTarget);
                if (ctrl) NativeMethods.keybd_event(VK_CTRL, 0, 0, IntPtr.Zero);
                if (shift) NativeMethods.keybd_event(VK_SHIFT, 0, 0, IntPtr.Zero);
                if (alt) NativeMethods.keybd_event(VK_MENU, 0, 0, IntPtr.Zero);
                byte k = (byte)key;
                NativeMethods.keybd_event(k, 0, 0, IntPtr.Zero);
                NativeMethods.keybd_event(k, 0, KEYUP, IntPtr.Zero);
                if (alt) NativeMethods.keybd_event(VK_MENU, 0, KEYUP, IntPtr.Zero);
                if (shift) NativeMethods.keybd_event(VK_SHIFT, 0, KEYUP, IntPtr.Zero);
                if (ctrl) NativeMethods.keybd_event(VK_CTRL, 0, KEYUP, IntPtr.Zero);
                Pump(3);
            }
            catch (Exception ex)
            {
                Diag.Log("Send 异常: " + ex.Message);
            }
        }

        // ---- 剪贴板 / 组织 ----
        public static void Cut(IntPtr t) { Send(t, Keys.X, true, false, false); }
        public static void Copy(IntPtr t) { Send(t, Keys.C, true, false, false); }
        public static void Paste(IntPtr t) { Send(t, Keys.V, true, false, false); }
        public static void Delete(IntPtr t) { Send(t, Keys.Delete, false, false, false); }
        public static void Rename(IntPtr t) { Send(t, Keys.F2, false, false, false); }
        public static void OpenItem(IntPtr t) { Send(t, Keys.Enter, false, false, false); }
        public static void Properties(IntPtr t) { Send(t, Keys.Enter, false, false, true); }
        public static void SelectAll(IntPtr t) { Send(t, Keys.A, true, false, false); }
        public static void NewFolder(IntPtr t) { Send(t, Keys.N, true, true, false); }

        /// <summary>视图方式 0..7 = 超大图标/大图标/中图标/小图标/列表/详细信息/平铺/内容。</summary>
        public static void SetViewMode(IntPtr t, int mode)
        {
            if (mode < 0) mode = 0;
            if (mode > 7) mode = 7;
            Send(t, (Keys)((int)Keys.D1 + mode), true, true, false);
        }

        /// <summary>点列头排序（列 0=名称 1=修改日期 2=类型 3=大小…；同列再点一次反向）。</summary>
        public static bool SortByColumn(IntPtr root, int column)
        {
            try
            {
                IntPtr hdr = WinFind.ByClass(root, new string[] { "SysHeader32", "Header" });
                if (hdr == IntPtr.Zero)
                {
                    Diag.Log("排序：没找到列头控件");
                    return false;
                }
                Int32 count = NativeMethods.SendMessage(hdr, HDM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero).ToInt32();
                if (count <= 0 || column >= count)
                {
                    Diag.Log("排序：列 " + column + " 超出范围（共 " + count + "）");
                    return false;
                }
                IntPtr buf = Marshal.AllocHGlobal(16);
                int x, y;
                try
                {
                    RECT r = new RECT();
                    Marshal.StructureToPtr(r, buf, false);
                    NativeMethods.SendMessage(hdr, HDM_GETITEMRECT, (IntPtr)column, buf);
                    r = (RECT)Marshal.PtrToStructure(buf, typeof(RECT));
                    x = (r.Left + r.Right) / 2;
                    y = (r.Top + r.Bottom) / 2;
                }
                finally { Marshal.FreeHGlobal(buf); }

                IntPtr lp = (IntPtr)(((y & 0xFFFF) << 16) | (x & 0xFFFF));
                NativeMethods.PostMessage(hdr, WM_LBUTTONDOWN, (IntPtr)1, lp);
                NativeMethods.PostMessage(hdr, WM_LBUTTONUP, IntPtr.Zero, lp);
                Diag.Log("排序：点列头 col=" + column + " x=" + x + " y=" + y);
                return true;
            }
            catch (Exception ex)
            {
                Diag.Log("排序异常: " + ex.Message);
                return false;
            }
        }

        /// <summary>复制选中项路径为纯文本（复制粘贴板 → 读回文件列表 → 写成文本）。</summary>
        public static int CopySelectedPaths(IntPtr t)
        {
            try
            {
                Send(t, Keys.C, true, false, false);
                System.Threading.Thread.Sleep(80);
                Pump(5);

                string[] files = null;
                try
                {
                    IDataObject o = Clipboard.GetDataObject();
                    if (o != null && o.GetDataPresent(DataFormats.FileDrop))
                        files = o.GetData(DataFormats.FileDrop) as string[];
                }
                catch { }

                if (files == null || files.Length == 0)
                {
                    Diag.Log("复制路径：剪贴板里没有文件列表");
                    return 0;
                }
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < files.Length; i++)
                {
                    if (i > 0) sb.Append("\r\n");
                    sb.Append(files[i]);
                }
                Clipboard.SetText(sb.ToString());
                return files.Length;
            }
            catch (Exception ex)
            {
                Diag.Log("复制路径异常: " + ex.Message);
                return 0;
            }
        }

        // ==================================================================
        // 全局文件夹选项（和资源管理器「查看」选项卡里那几个开关是同一份设置）
        // ==================================================================
        private const string AdvKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

        private static int ReadDword(string name, int def)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(AdvKey))
                {
                    if (k == null) return def;
                    object v = k.GetValue(name);
                    if (v is int) return (int)v;
                }
            }
            catch { }
            return def;
        }

        private static void WriteDword(string name, int value)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(AdvKey))
                {
                    if (k != null) k.SetValue(name, value, RegistryValueKind.DWord);
                }
            }
            catch (Exception ex)
            {
                Diag.Log("写 " + name + " 失败: " + ex.Message);
            }
        }

        public static bool ShowExtensions { get { return ReadDword("HideFileExt", 1) == 0; } }
        public static void SetShowExtensions(bool on) { WriteDword("HideFileExt", on ? 0 : 1); ShellVerbs.NotifyAssocChanged(); }

        public static bool ShowHidden { get { return ReadDword("Hidden", 2) == 1; } }
        public static void SetShowHidden(bool on) { WriteDword("Hidden", on ? 1 : 2); ShellVerbs.NotifyAssocChanged(); }

        public static bool ShowCheckBoxes { get { return ReadDword("AutoCheckSelect", 0) == 1; } }
        public static void SetShowCheckBoxes(bool on) { WriteDword("AutoCheckSelect", on ? 1 : 0); ShellVerbs.NotifyAssocChanged(); }

        // ==================================================================
        // 新建 / 压缩
        // ==================================================================
        public static string UniquePath(string want)
        {
            if (!File.Exists(want) && !Directory.Exists(want)) return want;
            string dir = Path.GetDirectoryName(want);
            string name = Path.GetFileNameWithoutExtension(want);
            string ext = Path.GetExtension(want);
            for (int i = 2; i < 1000; i++)
            {
                string p = Path.Combine(dir, name + " (" + i + ")" + ext);
                if (!File.Exists(p) && !Directory.Exists(p)) return p;
            }
            return want;
        }

        /// <summary>新建一个空文本文件（名字和资源管理器一样）。</summary>
        public static string NewTextFile(string folder)
        {
            try
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return null;
                string p = UniquePath(Path.Combine(folder, "新建文本文档.txt"));
                using (FileStream fs = File.Create(p)) { }
                return p;
            }
            catch (Exception ex)
            {
                Diag.Log("新建文本文档失败: " + ex.Message);
                return null;
            }
        }

        /// <summary>把选中项压缩成 zip，放在当前文件夹里。</summary>
        public static string CompressToZip(string[] paths, string folder)
        {
            if (paths == null || paths.Length == 0) return null;
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return null;
            try
            {
                string baseName;
                if (paths.Length == 1)
                {
                    baseName = Path.GetFileNameWithoutExtension(paths[0]);
                    if (string.IsNullOrEmpty(baseName)) baseName = "压缩包";
                }
                else baseName = "新建压缩文件夹";

                string zip = UniquePath(Path.Combine(folder, baseName + ".zip"));
                using (FileStream fs = new FileStream(zip, FileMode.CreateNew, FileAccess.Write))
                using (ZipArchive za = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    for (int i = 0; i < paths.Length; i++)
                    {
                        if (Directory.Exists(paths[i])) AddFolder(za, paths[i], Path.GetFileName(paths[i]));
                        else if (File.Exists(paths[i])) AddFile(za, paths[i], Path.GetFileName(paths[i]));
                    }
                }
                return zip;
            }
            catch (Exception ex)
            {
                Diag.Log("压缩失败: " + ex.Message);
                return null;
            }
        }

        private static void AddFolder(ZipArchive za, string dir, string entryRoot)
        {
            string[] files = Directory.GetFiles(dir);
            for (int i = 0; i < files.Length; i++) AddFile(za, files[i], entryRoot + "/" + Path.GetFileName(files[i]));

            string[] subs = Directory.GetDirectories(dir);
            for (int i = 0; i < subs.Length; i++) AddFolder(za, subs[i], entryRoot + "/" + Path.GetFileName(subs[i]));
        }

        private static void AddFile(ZipArchive za, string file, string entryName)
        {
            ZipArchiveEntry e = za.CreateEntry(entryName, CompressionLevel.Optimal);
            using (Stream dst = e.Open())
            using (FileStream src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                src.CopyTo(dst);
            }
        }
    }
}
