using System;
using System.IO;
using System.Text;

namespace TabbedExplorer
{
    /// <summary>
    /// 全局设置，JSON `&lt;程序目录&gt;\data\settings.json`（**绿色便携**，见 AppPaths）。
    ///
    /// 现在这些项（2026-09-22）：
    ///   capture    = perdesktop | migrate   标签捕获方式（每桌面一个窗口 / 全进程一个窗口 + Win+E 搬过来）
    ///   keeptabs   = 1 | 0                  是否保留标签页（退出后记住、下次还原）
    ///   theme      = system | light | dark  颜色模式
    ///   tabwidth   = &lt;逻辑像素&gt;            标签页宽度
    ///   tabautofit = 1 | 0                  是否自适应宽度（挤不下时自动缩窄）
    ///   favbar     = 1 | 0                  收藏夹栏显不显示（Ctrl+Shift+B）
    ///   captureall = 1 | 0                  是否把「从开始菜单/桌面打开的文件夹」也收成标签
    ///
    /// ⚠ 用 JSON 而不是 `key=value`（2026-09-22 川要求「配置项文件用 json 格式」）：
    /// JSON 本体不支持注释，所以说明写在 `_` 开头的键里 —— 那既是**合法 JSON**（任何工具都读得动），
    /// 又能让川打开文件就看见每一项是什么意思。读的是老 `settings.txt` 也没事：
    /// `Load` 会把它读进来、写成 json，再把老文件改名成 `.migrated` 留着（不删）。
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
        /// <summary>收藏夹栏是否显示（Ctrl+Shift+B）。</summary>
        public static bool FavBar = false;
        /// <summary>
        /// 是否捕获**所有**打开的文件夹窗口（不只是 Win+E）。
        /// 开：从开始菜单 / 桌面双击打开的文件夹也会被收成标签（浏览器行为）。
        /// 关：只接管 Win+E，其他照旧开原生窗口。
        /// </summary>
        public static bool CaptureAll = true;

        /// <summary>标签宽度的合法范围（逻辑像素）。太小就点不中了，太大一屏放不下两个。</summary>
        public const int TabWidthMin = 64;
        public const int TabWidthMax = 240;

        /// <summary>菜单里给的几个预设宽度（逻辑像素）。想要别的值直接改 settings.txt。</summary>
        public static readonly int[] TabWidthPresets = new int[] { 80, 96, 112, 128, 144, 168, 192 };

        /// <summary>设置文件 = `&lt;程序目录&gt;\data\settings.json`（JSON，见类注释）。</summary>
        public static string FileName { get { return AppPaths.File("settings.json"); } }

        /// <summary>老版本的纯文本设置文件 —— 只在迁移时读一次。</summary>
        public static string LegacyFileName { get { return AppPaths.File("settings.txt"); } }

        public static void Load()
        {
            try
            {
                if (File.Exists(FileName))
                {
                    string json = File.ReadAllText(FileName, Encoding.UTF8);
                    Capture    = ParseCapture(JsonGet(json, "capture"));
                    KeepTabs   = ParseBool(JsonGet(json, "keeptabs"), true);
                    Color      = ParseColor(JsonGet(json, "theme"));
                    TabWidth   = ClampWidth(ParseInt(JsonGet(json, "tabwidth"), TabWidth));
                    TabAutoFit = ParseBool(JsonGet(json, "tabautofit"), true);
                    FavBar     = ParseBool(JsonGet(json, "favbar"), false);
                    CaptureAll = ParseBool(JsonGet(json, "captureall"), true);
                    Diag.Step("设置: " + Describe());
                    return;
                }

                // 没 json：有老的 settings.txt 就先读过来、写成 json，再把老文件改名留着
                // （不直接删 —— 万一新格式哪里不对，老的还在）
                if (File.Exists(LegacyFileName))
                {
                    Diag.Step("设置: 发现老的 settings.txt，迁移到 settings.json");
                    LoadLegacyText();
                    Save();
                    try { File.Move(LegacyFileName, LegacyFileName + ".migrated"); } catch { }
                    Diag.Step("设置: 迁移完成 " + Describe());
                    return;
                }

                // 两样都没有 ⇒ 写一份默认的，川打开就能改
                Diag.Step("设置: 还没有 " + FileName + "（写一份默认的）");
                Save();
            }
            catch (Exception ex)
            {
                Diag.Log("设置: 读失败（全用默认）" + ex.Message);
            }
        }

        /// <summary>读老格式（`key=value` 纯文本，v1/v2）。</summary>
        private static void LoadLegacyText()
        {
            foreach (string raw in File.ReadAllLines(LegacyFileName, Encoding.UTF8))
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
                    case "favbar":     FavBar = ParseBool(v, false); break;
                    case "captureall": CaptureAll = ParseBool(v, true); break;
                }
            }
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                StringBuilder sb = new StringBuilder();
                // 说明写在 `_` 开头的键里 —— 既是**合法 JSON**（任何 JSON 工具都读得动），
                // 又能让川打开文件就看见每一项是什么意思（JSON 本体不支持注释）。
                sb.Append("{\r\n");
                sb.Append("  \"_note\": \"TabbedExplorer 设置。就在程序目录的 data\\\\ 下，拷走整个文件夹就带走了设置和标签记忆。\",\r\n");
                sb.Append("  \"_capture\": \"perdesktop = 每张虚拟桌面各一个窗口、各记一套标签；migrate = 全进程只一个窗口，Win+E 把它搬到当前桌面\",\r\n");
                sb.Append("  \"_theme\": \"system = 跟随系统应用模式；light / dark = 强制\",\r\n");
                sb.Append("  \"_tabwidth\": \"标签页宽度，逻辑像素，64 ~ 240\",\r\n");
                sb.Append("  \"_captureall\": \"true = 从开始菜单/桌面双击打开的文件夹也收成标签（像浏览器）；false = 只接管 Win+E\",\r\n");
                sb.Append("  \"capture\": \"").Append(Text(Capture)).Append("\",\r\n");
                sb.Append("  \"keeptabs\": ").Append(KeepTabs ? "true" : "false").Append(",\r\n");
                sb.Append("  \"theme\": \"").Append(Text(Color)).Append("\",\r\n");
                sb.Append("  \"tabwidth\": ").Append(TabWidth).Append(",\r\n");
                sb.Append("  \"tabautofit\": ").Append(TabAutoFit ? "true" : "false").Append(",\r\n");
                sb.Append("  \"favbar\": ").Append(FavBar ? "true" : "false").Append(",\r\n");
                sb.Append("  \"captureall\": ").Append(CaptureAll ? "true" : "false").Append("\r\n");
                sb.Append("}\r\n");

                // 跟记忆一样：先写临时文件再换过去，半途被硬杀不会留下半截文件。
                string tmp = FileName + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(FileName)) File.Delete(FileName);
                File.Move(tmp, FileName);
            }
            catch (Exception ex) { Diag.Log("设置: 写失败 " + ex.Message); }
        }

        /// <summary>
        /// 极简 JSON 取值：只认「顶层 `"key": 值`」，值是字符串 / 数字 / true / false。
        /// 我们没有 NuGet（也不打算引），而设置就这么几个平铺的键，手写一个够用且好查。
        /// 读不到 / 格式不对一律返回 null，调用方取默认值 —— 手改 JSON 改坏了也不会开不了程序。
        /// </summary>
        private static string JsonGet(string text, string key)
        {
            if (string.IsNullOrEmpty(text)) return null;
            int i = text.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (i < 0) return null;
            int colon = text.IndexOf(':', i + key.Length + 2);
            if (colon < 0) return null;
            int j = colon + 1;
            while (j < text.Length && char.IsWhiteSpace(text[j])) j++;
            if (j >= text.Length) return null;
            if (text[j] == '"')
            {
                int end = text.IndexOf('"', j + 1);
                if (end < 0) return null;
                return text.Substring(j + 1, end - j - 1);
            }
            int k = j;
            while (k < text.Length && text[k] != ',' && text[k] != '}' &&
                   text[k] != '\n' && text[k] != '\r') k++;
            return text.Substring(j, k - j).Trim();
        }

        public static string Describe()
        {
            return string.Format("capture={0} keeptabs={1} theme={2} tabwidth={3} tabautofit={4} favbar={5} captureall={6}",
                Text(Capture), KeepTabs ? 1 : 0, Text(Color), TabWidth,
                TabAutoFit ? 1 : 0, FavBar ? 1 : 0, CaptureAll ? 1 : 0);
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
        public static void SetFavBar(bool on) { FavBar = on; Save(); }
        public static void SetCaptureAll(bool on) { CaptureAll = on; Save(); }
    }
}
