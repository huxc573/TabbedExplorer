using System;
using System.IO;

namespace TabbedExplorer
{
    /// <summary>
    /// 诊断日志。每一步立即落盘（崩溃时能知道死在哪一步）。
    /// 文件：%APPDATA%\TabbedExplorer\log.txt
    /// </summary>
    internal static class Diag
    {
        private static readonly object sync = new object();
        private static string dir;
        private static string file;

        private static string Dir
        {
            get
            {
                if (dir == null)
                {
                    dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "TabbedExplorer");
                }
                return dir;
            }
        }

        private static string File_
        {
            get
            {
                if (file == null) file = Path.Combine(Dir, "log.txt");
                return file;
            }
        }

        public static void Log(string s)
        {
            try
            {
                lock (sync)
                {
                    Directory.CreateDirectory(Dir);
                    System.IO.File.AppendAllText(File_,
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
    }
}
