using System;
using System.IO;

namespace TabbedExplorer
{
    /// <summary>
    /// 数据放哪儿 —— **绿色便携**：设置、记忆、日志都在**程序目录下的 `data\`**，
    /// 整个文件夹拷到别的机器/U 盘就能带走，不往系统里写东西。
    ///
    /// 兜底：万一程序目录写不动（放在 Program Files、只读介质），
    /// 退回 `%APPDATA%\TabbedExplorer`，功能照常，只是不再便携。
    ///
    /// 从老位置搬过来：第一次用新位置时，把 `%APPDATA%\TabbedExplorer` 里的
    /// `desktops.txt` / `settings.txt` 抄一份（**只在新位置还没有这个文件时**），
    /// 免得用户觉得「记忆丢了」。
    /// </summary>
    internal static class AppPaths
    {
        private const string DataFolderName = "data";

        private static string dir;
        private static bool decided;

        /// <summary>数据目录（已确保存在）。</summary>
        public static string DataDir
        {
            get
            {
                if (!decided) Decide();
                return dir;
            }
        }

        public static string File(string name)
        {
            return Path.Combine(DataDir, name);
        }

        /// <summary>老位置（只有搬家时才用得上）。</summary>
        public static string LegacyDir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TabbedExplorer");
            }
        }

        private static void Decide()
        {
            decided = true;
            string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory ?? ".", DataFolderName);
            if (CanWrite(local))
            {
                dir = local;
                return;
            }
            // 先定下 dir 再写日志：Diag 会回头问 DataDir，dir 还空着就成了死循环。
            dir = LegacyDir;
            try { Directory.CreateDirectory(dir); } catch { }
            try { Diag.Log("便携: 程序目录写不动，数据退回 " + LegacyDir); } catch { }
        }

        private static bool CanWrite(string d)
        {
            try
            {
                Directory.CreateDirectory(d);
                string probe = Path.Combine(d, "writable.tmp");
                System.IO.File.WriteAllText(probe, "ok");
                System.IO.File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        /// <summary>把老位置的数据抄进新位置（只补缺，不覆盖）。返回抄了几个文件。</summary>
        public static int MigrateFromLegacy()
        {
            int n = 0;
            try
            {
                string old = LegacyDir;
                if (string.Equals(Path.GetFullPath(old).TrimEnd('\\'),
                                  Path.GetFullPath(DataDir).TrimEnd('\\'),
                                  StringComparison.OrdinalIgnoreCase)) return 0;
                if (!Directory.Exists(old)) return 0;

                foreach (string name in new string[] { "desktops.txt", "settings.txt" })
                {
                    string src = Path.Combine(old, name);
                    string dst = Path.Combine(DataDir, name);
                    if (!System.IO.File.Exists(src) || System.IO.File.Exists(dst)) continue;
                    Directory.CreateDirectory(DataDir);
                    System.IO.File.Copy(src, dst);
                    n++;
                }
                if (n > 0) Diag.Step("便携: 从 " + old + " 搬过来 " + n + " 个文件 -> " + DataDir);
            }
            catch (Exception ex) { Diag.Log("便携: 搬家失败（不影响使用）" + ex.Message); }
            return n;
        }
    }
}
