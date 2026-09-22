using System;
using System.IO;
namespace TabbedExplorer
{
    /// <summary>
    /// 诊断日志。每一步立即落盘（崩溃时能知道死在哪一步）。
    /// 文件：程序目录下 `data\log.txt`（见 AppPaths，绿色便携；写不动才退回 %APPDATA%）。
    ///
    /// ⚠ 2026-09-22 川：「是否写入日志，由设置中的 Debug 模式决定，默认不开，不过我们要开」。
    /// 所以这里有一个 <see cref="Enabled"/> 总闸 —— 关着的时候**一个字节都不写**（连文件都不建），
    /// 开着的时候照旧每一步落盘。开关的值来自 `settings.json` 的 `debug`，见 `Settings.Debug`。
    /// </summary>
    internal static class Diag
    {
        private static readonly object sync = new object();
        private static string file;

        /// <summary>
        /// 写不写日志。**唯一来源是设置里的「Debug 模式」**（<see cref="Settings.Debug"/>）。
        ///
        /// 初值 true 只是**开机前几行的兜底**：`Settings.Load()` 一读完文件就会把这里改成文件里的值
        /// （默认 false）。这样既满足「平时不写盘」，又保证启动阶段 —— 设置还没读到、也最可能崩的
        /// 那一段 —— 留下的痕迹永远在。代价只有十来行，值。
        /// </summary>
        public static bool Enabled = true;

        private static string File_
        {
            get
            {
                if (file == null) file = AppPaths.File("log.txt");
                return file;
            }
        }

        /// <summary>日志文件现在在哪（设置窗口要显示大小 / 清理）。</summary>
        public static string FileName { get { return File_; } }

        public static void Log(string s)
        {
            if (!Enabled) return;                 // Debug 模式关着：不写盘（也不建目录）
            try
            {
                lock (sync)
                {
                    string f = File_;
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(f));
                    System.IO.File.AppendAllText(f,
                        DateTime.Now.ToString("HH:mm:ss.fff") + " " + s + Environment.NewLine);
                }
            }
            catch { }
        }

        /// <summary>带标记的步骤（方便 grep 出"最后一步"）。</summary>
        public static void Step(string s)
        {
            Log("[step] " + s);
        }

        /// <summary>现在这份日志有多大（字节）。没有就是 0。</summary>
        public static long Size()
        {
            try
            {
                FileInfo fi = new FileInfo(File_);
                return fi.Exists ? fi.Length : 0;
            }
            catch { return 0; }
        }

        /// <summary>上一份日志（启动时轮转过来的那个）。</summary>
        private static string PrevFile { get { return File_ + ".prev.txt"; } }

        /// <summary>给人看的体积（设置窗口里那行「日志文件：…（现在 546 KB）」）。</summary>
        public static string HumanSize()
        {
            long n = Size();
            if (n < 1024) return n + " B";
            if (n < 1024 * 1024) return (n / 1024) + " KB";
            return (n / 1024 / 1024.0).ToString("0.0") + " MB";
        }

        /// <summary>
        /// 启动时轮转一次：上一轮的日志已经很大（&gt; 1MB）就先挪成 `log.txt.prev.txt`，
        /// 免得**常年开着 Debug** 时这个文件无限长下去（川要「清理日志」按钮，就是因为它会变很大）。
        /// 轮转只留一份，更老的那份直接丢掉。
        /// </summary>
        public static void Rotate()
        {
            try
            {
                lock (sync)
                {
                    FileInfo fi = new FileInfo(File_);
                    if (!fi.Exists || fi.Length < 1024 * 1024) return;
                    if (File.Exists(PrevFile)) File.Delete(PrevFile);
                    File.Move(File_, PrevFile);
                    Enabled = true;      // 还没读设置，先把这一行写下去（下面这行本身要有地方落）
                    Log("日志: 上一份 " + (fi.Length / 1024) + "KB 已轮转为 log.txt.prev.txt");
                }
            }
            catch { }
        }

        /// <summary>
        /// 清空日志（设置窗口里那个「清理日志」按钮）。返回是否成功。
        /// 主日志和轮转出来那份一起清 —— 否则川点了「清理」还看到 data 目录里躺着几百 KB。
        /// </summary>
        public static bool Clear()
        {
            bool ok = true;
            try
            {
                lock (sync)
                {
                    if (File.Exists(File_)) File.Delete(File_);
                }
            }
            catch (Exception ex)
            {
                ok = false;
                // 删不掉至少留一句。这一句**临时绕过 Debug 闸**（写完就还原）：
                // 清理失败是异常情况，不该因为「日志关着」就连痕迹都不留。
                bool was = Enabled;
                Enabled = true;
                Log("日志: 清空失败 " + ex.Message);
                Enabled = was;
            }
            try { if (File.Exists(PrevFile)) File.Delete(PrevFile); } catch { }
            return ok;
        }
    }
}
