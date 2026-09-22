using System;
using System.IO;
namespace TabbedExplorer
{
    /// <summary>
    /// 诊断日志。每一步立即落盘（崩溃时能知道死在哪一步）。
    /// 文件：程序目录下 `data\log.txt`（见 AppPaths，绿色便携；写不动才退回 %APPDATA%）。
    /// </summary>
    internal static class Diag
    {
        private static readonly object sync = new object();
        private static string file;

        private static string File_
        {
            get
            {
                if (file == null) file = AppPaths.File("log.txt");
                return file;
            }
        }

        public static void Log(string s)
        {
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
    }
}
