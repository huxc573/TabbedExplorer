using System;
using System.Collections.Generic;
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
    ///   tabwidth   = &lt;逻辑像素&gt;            标签页宽度（开了 tabautowiden 时=单个标签最宽能到多少）
    ///   tabautowiden = 1 | 0                自适应宽度①：文件夹名过长时自动加宽
    ///   tabautofit = 1 | 0                  自适应宽度②：挤不下时自动缩窄
    ///   favbar     = 1 | 0                  书签栏显不显示（Ctrl+Shift+B）
    ///   captureall = 1 | 0                  是否把「从开始菜单/桌面打开的文件夹」也收成标签
    ///   debug      = 1 | 0                  是否把详细过程写进 data\log.txt（默认关，见 Diag）
    ///
    /// ⚠ 用 JSON 而不是 `key=value`（2026-09-22 川要求「配置项文件用 json 格式」）：
    /// JSON 本体不支持注释，所以说明写在 `_` 开头的键里 —— 那既是**合法 JSON**（任何工具都读得动），
    /// 又能让川打开文件就看见每一项是什么意思。读的是老 `settings.txt` 也没事：
    /// `Load` 会把它读进来、写成 json，再把老文件改名成 `.migrated` 留着（不删）。
    /// 解析统一走 `Json`（src/Json.cs）—— `desktops.json` / `history.json` / `favorites.json` 共用一套。
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
        /// <summary>
        /// 自适应宽度（一）：**文件夹名过长时自动加宽** —— 每个标签按自己那行文字的宽度来定，
        /// 上限就是 `TabWidth`。关了就是所有标签一律 `TabWidth` 宽（名字长了打省略号）。
        /// </summary>
        public static bool TabAutoWiden = true;
        /// <summary>
        /// 自适应宽度（二）：**挤不下时自动缩窄** —— 全排标签加起来超过可用宽度就等比缩到能放下。
        /// 关了就不缩（总宽仍然不越过右边那排按钮，多出来的标签要靠横向滚动才看得到）。
        /// 2026-09-22 川把这一个拆成了两项（原来只有这个「自适应宽度」）。
        /// </summary>
        public static bool TabAutoFit = true;
        /// <summary>书签栏是否显示（Ctrl+Shift+B）。</summary>
        public static bool FavBar = false;
        /// <summary>
        /// 是否捕获**所有**打开的文件夹窗口（不只是 Win+E）。
        /// 开：从开始菜单 / 桌面双击打开的文件夹也会被收成标签（浏览器行为）。
        /// 关：只接管 Win+E，其他照旧开原生窗口。
        /// </summary>
        public static bool CaptureAll = true;
        /// <summary>
        /// Debug 模式：把每一步的详细过程写进 `data\log.txt`。
        ///
        /// 川 2026-09-22：「是否写入日志，由设置中的 Debug 模式决定，默认不开，不过我们要开」。
        /// 默认关（普通用户不需要一个会一直变大的文件），本机自己那份 settings.json 里开着。
        /// 它只影响 `Diag` 写不写盘，**不影响任何功能**。
        /// </summary>
        public static bool Debug = false;

        /// <summary>标签宽度的合法范围（逻辑像素）。太小就点不中了，太大一屏放不下两个。</summary>
        public const int TabWidthMin = 64;
        public const int TabWidthMax = 240;

        /// <summary>
        /// 「名称过长时自动加宽」**最多能加到多宽**（逻辑像素）。
        /// 注意这是**另一个上限**，不是 `TabWidth`：
        ///   开自动加宽时 `TabWidth` 是**基准宽度**，名字放不下才往上加，最多加到这里。
        /// 川 2026-09-22 报「单标签页并未加宽」的根因就是原来把它卡在 `TabWidth` 上 ——
        /// 名字再长，宽也超不过 `TabWidth`，看着就是「没加宽」。
        /// </summary>
        public const int TabWidenMax = 240;

        // ==================================================================
        // 快捷键（川 2026-09-22：设置窗口新增「快捷键」页，程序自己的热键可改）
        //
        // 只存「命令 → 组合键文本」这一层；解析 / 匹配在 `Hotkeys`（src/Hotkeys.cs）。
        // 存成**扁平键** `hotkey_<命令>`（Json.cs 是手写的单层解析，不认嵌套对象）。
        // ==================================================================

        /// <summary>可自定义的命令（顺序 = 设置窗口里显示的顺序）。</summary>
        public static readonly string[] HotkeyKeys = new string[]
        {
            "newtab", "closetab", "nexttab", "prevtab", "history", "reopen", "favbar"
        };

        /// <summary>各项的默认组合键（跟浏览器对齐那一套）。</summary>
        public static readonly string[] HotkeyDefaults = new string[]
        {
            "Ctrl+T", "Ctrl+W", "Ctrl+Tab", "Ctrl+Shift+Tab", "Ctrl+H", "Ctrl+Shift+T", "Ctrl+Shift+B"
        };

        private static readonly Dictionary<string, string> hotkeys =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>某个命令现在绑的组合键文本（没设过 / 设空了 ⇒ 用默认）。</summary>
        public static string Hotkey(string cmd)
        {
            string v;
            if (!string.IsNullOrEmpty(cmd) && hotkeys.TryGetValue(cmd, out v) && !string.IsNullOrEmpty(v))
                return v;
            for (int i = 0; i < HotkeyKeys.Length; i++)
                if (string.Equals(HotkeyKeys[i], cmd, StringComparison.OrdinalIgnoreCase))
                    return HotkeyDefaults[i];
            return "";
        }

        /// <summary>改一条快捷键并立刻落盘。传空串 = 恢复默认。</summary>
        public static void SetHotkey(string cmd, string combo)
        {
            if (string.IsNullOrEmpty(cmd)) return;
            if (string.IsNullOrEmpty(combo)) hotkeys.Remove(cmd);
            else hotkeys[cmd] = combo;
            Save();
        }

        /// <summary>全部恢复默认（设置窗口「快捷键」页那个按钮）。</summary>
        public static void ResetHotkeys()
        {
            hotkeys.Clear();
            Save();
        }

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
                    Capture      = ParseCapture(Json.Get(json, "capture"));
                    KeepTabs     = Json.GetBool(json, "keeptabs", true);
                    Color        = ParseColor(Json.Get(json, "theme"));
                    TabWidth     = ClampWidth(Json.GetInt(json, "tabwidth", TabWidth));
                    TabAutoFit   = Json.GetBool(json, "tabautofit", true);
                    TabAutoWiden = Json.GetBool(json, "tabautowiden", true);
                    FavBar       = Json.GetBool(json, "favbar", false);
                    CaptureAll   = Json.GetBool(json, "captureall", true);
                    Debug        = Json.GetBool(json, "debug", false);
                    Diag.Enabled = Debug;        // 读完才是最终口径（见 Diag.Enabled 的说明）
                    hotkeys.Clear();
                    for (int i = 0; i < HotkeyKeys.Length; i++)
                    {
                        string v = Json.Get(json, "hotkey_" + HotkeyKeys[i]);
                        // 读到的跟默认一样就不存 —— 让文件里只留「川真改过」的那几条
                        if (!string.IsNullOrEmpty(v) &&
                            !string.Equals(v, HotkeyDefaults[i], StringComparison.OrdinalIgnoreCase))
                            hotkeys[HotkeyKeys[i]] = v;
                    }
                    Diag.Step("设置: " + Describe());
                    return;
                }

                // 没 json：有老的 settings.txt 就先读过来、写成 json，再把老文件改名留着
                // （不直接删 —— 万一新格式哪里不对，老的还在）
                if (File.Exists(LegacyFileName))
                {
                    Diag.Step("设置: 发现老的 settings.txt，迁移到 settings.json");
                    LoadLegacyText();
                    Diag.Enabled = Debug;
                    Save();
                    try { File.Move(LegacyFileName, LegacyFileName + ".migrated"); } catch { }
                    Diag.Step("设置: 迁移完成 " + Describe());
                    return;
                }

                // 两样都没有 ⇒ 写一份默认的，川打开就能改
                Diag.Step("设置: 还没有 " + FileName + "（写一份默认的）");
                Save();
                Diag.Enabled = Debug;
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
                    case "tabautofit":   TabAutoFit = ParseBool(v, true); break;
                    case "tabautowiden": TabAutoWiden = ParseBool(v, true); break;
                    case "favbar":       FavBar = ParseBool(v, false); break;
                    case "captureall":   CaptureAll = ParseBool(v, true); break;
                    case "debug":        Debug = ParseBool(v, false); break;
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
                sb.Append("  \"_tabwidth\": \"标签页宽度，逻辑像素，64 ~ 240；关掉 tabautowiden 时所有标签就都是这个宽\",\r\n");
                sb.Append("  \"_tabautowiden\": \"true = 名字太长时这个标签自己加宽（最多 400 逻辑像素）；false = 所有标签一样宽\",\r\n");
                sb.Append("  \"_tabautofit\": \"true = 一排标签挤不下时自动缩窄；false = 不缩，总宽停在右边那排按钮前，多出来的靠滚轮横向滑\",\r\n");
                sb.Append("  \"_captureall\": \"true = 从开始菜单/桌面双击打开的文件夹也收成标签（像浏览器）；false = 只接管 Win+E\",\r\n");
                sb.Append("  \"_debug\": \"true = 把每一步的详细过程写进 data\\\\log.txt（默认 false）。查问题时打开，平时关着不占地方。\",\r\n");
                sb.Append("  \"_hotkeys\": \"程序自己的快捷键，格式 Ctrl+Shift+T / Alt+F4 这样；留空或删掉这一行 = 用默认。Ctrl+1..9 跳标签是固定的、不在这里。\",\r\n");
                sb.Append("  \"capture\": \"").Append(Text(Capture)).Append("\",\r\n");
                sb.Append("  \"keeptabs\": ").Append(KeepTabs ? "true" : "false").Append(",\r\n");
                sb.Append("  \"theme\": \"").Append(Text(Color)).Append("\",\r\n");
                sb.Append("  \"tabwidth\": ").Append(TabWidth).Append(",\r\n");
                sb.Append("  \"tabautowiden\": ").Append(TabAutoWiden ? "true" : "false").Append(",\r\n");
                sb.Append("  \"tabautofit\": ").Append(TabAutoFit ? "true" : "false").Append(",\r\n");
                sb.Append("  \"favbar\": ").Append(FavBar ? "true" : "false").Append(",\r\n");
                sb.Append("  \"captureall\": ").Append(CaptureAll ? "true" : "false").Append(",\r\n");
                sb.Append("  \"debug\": ").Append(Debug ? "true" : "false").Append(",\r\n");
                // 快捷键：只写「跟默认不一样」的那些（默认值不落文件，以后换默认值能跟着走）
                StringBuilder hb = new StringBuilder();
                for (int i = 0; i < HotkeyKeys.Length; i++)
                {
                    string v;
                    if (!hotkeys.TryGetValue(HotkeyKeys[i], out v)) continue;
                    if (string.IsNullOrEmpty(v)) continue;
                    if (string.Equals(v, HotkeyDefaults[i], StringComparison.OrdinalIgnoreCase)) continue;
                    hb.Append("  \"hotkey_").Append(HotkeyKeys[i]).Append("\": \"")
                      .Append(v.Replace("\\", "\\\\").Replace("\"", "\\\""))
                      .Append("\",\r\n");
                }
                sb.Append(hb.ToString());
                // 上面每一项末尾都带逗号，这里补最后一行收尾（JSON 末尾多余逗号不合法）
                string body = sb.ToString();
                int last = body.LastIndexOf(",\r\n");
                sb = new StringBuilder(body.Substring(0, last) + "\r\n");
                sb.Append("}\r\n");

                // 跟记忆一样：先写临时文件再换过去，半途被硬杀不会留下半截文件。
                string tmp = FileName + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(FileName)) File.Delete(FileName);
                File.Move(tmp, FileName);
            }
            catch (Exception ex) { Diag.Log("设置: 写失败 " + ex.Message); }
        }

        // JSON 取值统一走 `Json`（src/Json.cs）—— 四个数据文件（settings / desktops / history / favorites）
        // 共用同一套手写解析，不再各写一份（省得格式一处改一处不改）。

        public static string Describe()
        {
            return string.Format("capture={0} keeptabs={1} theme={2} tabwidth={3} autowiden={4} autofit={5} favbar={6} captureall={7} debug={8} hotkeys={9}",
                Text(Capture), KeepTabs ? 1 : 0, Text(Color), TabWidth,
                TabAutoWiden ? 1 : 0, TabAutoFit ? 1 : 0, FavBar ? 1 : 0, CaptureAll ? 1 : 0,
                Debug ? 1 : 0, hotkeys.Count);
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
        public static void SetTabAutoWiden(bool on) { TabAutoWiden = on; Save(); }
        public static void SetFavBar(bool on) { FavBar = on; Save(); }
        public static void SetCaptureAll(bool on) { CaptureAll = on; Save(); }

        /// <summary>Debug 模式开关：改完立刻生效（`Diag` 每次写之前都看那个闸）。</summary>
        public static void SetDebug(bool on)
        {
            Debug = on;
            Diag.Enabled = on;
            Save();
        }
    }
}
