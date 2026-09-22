using System;
using System.IO;
using System.Text;

namespace TabbedExplorer
{
    /// <summary>
    /// 全局设置，纯文本 `&lt;程序目录&gt;\data\settings.txt`（**绿色便携**，见 AppPaths）。
    ///
    /// 一共五项（2026-09-22 川定的）：
    ///   capture    = perdesktop | migrate   标签捕获方式（每桌面一个窗口 / 全进程一个窗口 + Win+E 搬过来）
    ///   keeptabs   = 1 | 0                  是否保留标签页（退出后记住、下次还原）
    ///   theme      = system | light | dark  颜色模式
    ///   tabwidth   = &lt;逻辑像素&gt;            标签页宽度
    ///   tabautofit = 1 | 0                  是否自适应宽度（挤不下时自动缩窄）
    ///
    /// 故意跟记忆文件一样手写纯文本：本机没有 NuGet，出问题时川自己打开就能看、能改。
    /// 老版本（v1，只有 capture 一项）的文件照样读，缺的项一律取默认值。
    /// </summary>
    internal static class Settings
    {
        public enum CaptureMode
        {
            /// <summary>按虚拟桌面分别捕获。</summary>
            PerDesktop,
            /// <summary>捕获并迁移到当前桌面（v1.0.0 的老行为）。</summary>
            Migrate
        }

        public enum ColorMode
        {
            /// <summary>跟随系统「应用模式」。</summary>
            System,
            Light,
            Dark
        }

        private const string Header = "# TabbedExplorer settings v2";

        // ---- 默认值 ----
        public static CaptureMode Capture = CaptureMode.PerDesktop;
        public static bool KeepTabs = true;
        public static ColorMode Color = ColorMode.System;
        public static int TabWidth = 112;
        public static bool TabAutoFit = true;

        /// <summary>标签宽度的合法范围（逻辑像素）。太小就点不中了，太大一屏放不下两个。</summary>
        public const int TabWidthMin = 64;
        public const int TabWidthMax = 240;

        /// <summary>菜单里给的几个预设宽度（逻辑像素）。想要别的值直接改 settings.txt。</summary>
        public static readonly int[] TabWidthPresets = new int[] { 80, 96, 112, 128, 144, 168, 192 };

        public static string FileName { get { return AppPaths.File("settings.txt"); } }

        public static void Load()
        {
            try
            {
                if (!File.Exists(FileName))
                {
                    // 没有就**写一份默认的**（带注释）：这样「程序目录\data\settings.txt」
                    // 一装好就存在，川想手改直接打开看得见。
                    Diag.Step("设置: 还没有 " + FileName + "（写一份默认的，带注释）");
                    Save();
                    return;
                }
                foreach (string raw in File.ReadAllLines(FileName, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string v = line.Substring(eq + 1).Trim();
                    switch (k)
                    {
                        case "capture":    Capture = ParseCapture(v); break;
                        case "keeptabs":   KeepTabs = ParseBool(v, true); break;
                        case "theme":      Color = ParseColor(v); break;
                        case "tabwidth":   TabWidth = ClampWidth(ParseInt(v, TabWidth)); break;
                        case "tabautofit": TabAutoFit = ParseBool(v, true); break;
                    }
                }
                Diag.Step("设置: " + Describe());
            }
            catch (Exception ex)
            {
                Diag.Log("设置: 读失败（全用默认）" + ex.Message);
            }
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                StringBuilder sb = new StringBuilder();
                sb.Append(Header).Append("\r\n");
                sb.Append("# 这个文件就在程序目录的 data\\ 下，拷走整个文件夹就带走了设置和标签记忆。\r\n");
                sb.Append("# capture    = perdesktop | migrate    标签捕获方式\r\n");
                sb.Append("#              perdesktop = 每张虚拟桌面各一个窗口、各记一套标签\r\n");
                sb.Append("#              migrate    = 全进程只一个窗口，Win+E 把它搬到当前桌面\r\n");
                sb.Append("# keeptabs   = 1 | 0                  是否保留标签页（关掉就是每次开都只有一个「此电脑」）\r\n");
                sb.Append("# theme      = system | light | dark  颜色模式\r\n");
                sb.Append("# tabwidth   = 64 ~ 240               标签页宽度（逻辑像素）\r\n");
                sb.Append("# tabautofit = 1 | 0                  挤不下时是否自动缩窄\r\n");
                sb.Append("capture=").Append(Text(Capture)).Append("\r\n");
                sb.Append("keeptabs=").Append(KeepTabs ? "1" : "0").Append("\r\n");
                sb.Append("theme=").Append(Text(Color)).Append("\r\n");
                sb.Append("tabwidth=").Append(TabWidth).Append("\r\n");
                sb.Append("tabautofit=").Append(TabAutoFit ? "1" : "0").Append("\r\n");

                // 跟记忆一样：先写临时文件再换过去，半途被硬杀不会留下半截文件。
                string tmp = FileName + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(FileName)) File.Delete(FileName);
                File.Move(tmp, FileName);
            }
            catch (Exception ex) { Diag.Log("设置: 写失败 " + ex.Message); }
        }

        public static string Describe()
        {
            return string.Format("capture={0} keeptabs={1} theme={2} tabwidth={3} tabautofit={4}",
                Text(Capture), KeepTabs ? 1 : 0, Text(Color), TabWidth, TabAutoFit ? 1 : 0);
        }

        // ==================================================================
        // 解析 / 序列化
        // ==================================================================

        public static int ClampWidth(int w)
        {
            if (w < TabWidthMin) return TabWidthMin;
            if (w > TabWidthMax) return TabWidthMax;
            return w;
        }

        private static bool ParseBool(string v, bool dflt)
        {
            if (string.IsNullOrEmpty(v)) return dflt;
            v = v.Trim().ToLowerInvariant();
            if (v == "1" || v == "true" || v == "yes" || v == "on" || v == "开") return true;
            if (v == "0" || v == "false" || v == "no" || v == "off" || v == "关") return false;
            return dflt;
        }

        private static int ParseInt(string v, int dflt)
        {
            int n;
            if (int.TryParse(v.Trim(), out n)) return n;
            return dflt;
        }

        public static CaptureMode ParseCapture(string v)
        {
            if (string.Equals(v, "migrate", StringComparison.OrdinalIgnoreCase)) return CaptureMode.Migrate;
            if (string.Equals(v, "single", StringComparison.OrdinalIgnoreCase)) return CaptureMode.Migrate;
            return CaptureMode.PerDesktop;
        }

        public static ColorMode ParseColor(string v)
        {
            if (string.Equals(v, "light", StringComparison.OrdinalIgnoreCase)) return ColorMode.Light;
            if (string.Equals(v, "dark", StringComparison.OrdinalIgnoreCase)) return ColorMode.Dark;
            if (string.Equals(v, "浅色", StringComparison.OrdinalIgnoreCase)) return ColorMode.Light;
            if (string.Equals(v, "深色", StringComparison.OrdinalIgnoreCase)) return ColorMode.Dark;
            return ColorMode.System;
        }

        public static string Text(CaptureMode m)
        {
            return m == CaptureMode.Migrate ? "migrate" : "perdesktop";
        }

        public static string Text(ColorMode m)
        {
            if (m == ColorMode.Dark) return "dark";
            if (m == ColorMode.Light) return "light";
            return "system";
        }

        public static string Label(CaptureMode m)
        {
            return m == CaptureMode.Migrate ? "捕获并迁移到当前桌面" : "按虚拟桌面分别捕获";
        }

        public static string Label(ColorMode m)
        {
            if (m == ColorMode.Dark) return "深色";
            if (m == ColorMode.Light) return "浅色";
            return "跟随系统";
        }

        // ==================================================================
        // 改一项就存一次（都从菜单里点，频率极低，不用攒）
        // ==================================================================

        public static void SetCapture(CaptureMode m) { Capture = m; Save(); }
        public static void SetColor(ColorMode m) { Color = m; Save(); }
        public static void SetKeepTabs(bool on) { KeepTabs = on; Save(); }
        public static void SetTabWidth(int w) { TabWidth = ClampWidth(w); Save(); }
        public static void SetTabAutoFit(bool on) { TabAutoFit = on; Save(); }
    }
}
