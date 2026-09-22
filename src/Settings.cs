using System;
using System.IO;
using System.Text;

namespace TabbedExplorer
{
    /// <summary>
    /// 全局开关，纯文本 `%APPDATA%\TabbedExplorer\settings.txt`。
    ///
    /// 现在只有一项：**标签捕获方式**（川要的设置菜单第一项）。
    ///   - <c>perdesktop</c>：每张虚拟桌面各一个窗口、各记一套标签（v1.1.0 起的行为）；
    ///   - <c>migrate</c>   ：全进程只有一个窗口，按 Win+E 把它搬到当前桌面（v1.0.0 的行为）。
    ///
    /// 故意跟记忆文件一样手写纯文本：本机没有 NuGet，出问题时川自己打开就能看、能改。
    /// </summary>
    internal sealed class Settings
    {
        public enum CaptureMode
        {
            /// <summary>按虚拟桌面分别捕获。</summary>
            PerDesktop,
            /// <summary>捕获并迁移到当前桌面（v1.0.0 的老行为）。</summary>
            Migrate
        }

        private const string Header = "# TabbedExplorer settings v1";

        public CaptureMode Capture = CaptureMode.PerDesktop;

        public static string FileName
        {
            get { return Path.Combine(DesktopMemory.Folder, "settings.txt"); }
        }

        public void Load()
        {
            try
            {
                if (!File.Exists(FileName))
                {
                    Diag.Step("设置: 还没有 " + FileName + "（全用默认）");
                    return;
                }
                foreach (string raw in File.ReadAllLines(FileName, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string v = line.Substring(eq + 1).Trim().ToLowerInvariant();
                    if (k == "capture") Capture = Parse(v);
                }
                Diag.Step("设置: capture=" + Text(Capture));
            }
            catch (Exception ex)
            {
                Diag.Log("设置: 读失败（全用默认）" + ex.Message);
            }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(DesktopMemory.Folder);
                StringBuilder sb = new StringBuilder();
                sb.Append(Header).Append("\r\n");
                sb.Append("# capture = perdesktop | migrate\r\n");
                sb.Append("#   perdesktop = 每张虚拟桌面各一个窗口、各记一套标签\r\n");
                sb.Append("#   migrate    = 全进程只一个窗口，Win+E 把它搬到当前桌面\r\n");
                sb.Append("capture=").Append(Text(Capture)).Append("\r\n");

                // 跟记忆一样：先写临时文件再换过去，半途被硬杀不会留下半截文件。
                string tmp = FileName + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(FileName)) File.Delete(FileName);
                File.Move(tmp, FileName);
            }
            catch (Exception ex) { Diag.Log("设置: 写失败 " + ex.Message); }
        }

        public static CaptureMode Parse(string v)
        {
            if (string.Equals(v, "migrate", StringComparison.OrdinalIgnoreCase)) return CaptureMode.Migrate;
            if (string.Equals(v, "single", StringComparison.OrdinalIgnoreCase)) return CaptureMode.Migrate;
            return CaptureMode.PerDesktop;
        }

        public static string Text(CaptureMode m)
        {
            return m == CaptureMode.Migrate ? "migrate" : "perdesktop";
        }

        /// <summary>菜单上显示的名字。</summary>
        public static string Label(CaptureMode m)
        {
            return m == CaptureMode.Migrate ? "捕获并迁移到当前桌面" : "按虚拟桌面分别捕获";
        }
    }
}
