using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace TabbedExplorer
{
    /// <summary>
    /// 「去过哪些文件夹」—— Ctrl+H / 标签条上的历史按钮用。
    ///
    /// 和 `DesktopMemory` 的区别（两者都在记路径，别搞混）：
    ///   - `DesktopMemory` 记的是**每张虚拟桌面当前开着哪些标签**，程序退出再打开要照着还原；
    ///   - `History` 记的是**一路去过哪儿**，是只增不减的一条流水，关标签也不会消失 ——
    ///     就是浏览器那个「历史记录」。
    ///
    /// 存 `<程序目录>\data\history.json`（绿色便携，见 AppPaths）：
    ///   <code>
    ///   { "history": ["D:\\FB.Data", "::{20D04FE0-...}", "shell:Downloads"] }
    ///   </code>
    /// 新的在前。**故意不带时间戳**：川要的是「挑一个再去一次」，不是考古；带时间反而让文件变脏、还得处理时区。
    /// 重复访问只把那条提到最前面，不会刷屏。
    ///
    /// 2026-09-22 川要求「配置一律 json」：原来那版是 `history.txt`，现在见到老文件会读过来、写成 json，
    /// 再把老文件改名成 `.migrated` 留着（不删）。
    /// </summary>
    internal static class History
    {
        /// <summary>最多留多少条。够翻就行，文件也小。</summary>
        private const int Max = 120;

        /// <summary>菜单里最多列几条（再多就翻不动了）。</summary>
        public const int MenuMax = 15;

        private static readonly List<string> items = new List<string>();
        private static bool loaded;

        public static string FileName { get { return AppPaths.File("history.json"); } }

        /// <summary>老版本的纯文本历史 —— 只在迁移时读一次。</summary>
        public static string LegacyFileName { get { return AppPaths.File("history.txt"); } }

        /// <summary>最近去过的地方（新的在前）。返回的是内部列表的拷贝，外面随便用。</summary>
        public static List<string> Recent
        {
            get { EnsureLoaded(); return new List<string>(items); }
        }

        private static void EnsureLoaded()
        {
            if (loaded) return;
            loaded = true;
            try
            {
                if (File.Exists(FileName))
                {
                    LoadJson(File.ReadAllText(FileName, Encoding.UTF8));
                    Diag.Step("历史: 读进 " + items.Count + " 条");
                    return;
                }
                if (File.Exists(LegacyFileName))
                {
                    Diag.Step("历史: 发现老的 history.txt，迁移到 history.json");
                    LoadLegacyText();
                    Save();
                    try { File.Move(LegacyFileName, LegacyFileName + ".migrated"); } catch { }
                    Diag.Step("历史: 迁移完成 " + items.Count + " 条");
                }
            }
            catch (Exception ex) { Diag.Log("历史: 读失败 " + ex.Message); }
        }

        private static void LoadJson(string json)
        {
            foreach (string p in Json.Strings(Json.GetBlock(json, "history")))
            {
                if (string.IsNullOrEmpty(p)) continue;
                if (!items.Contains(p)) items.Add(p);
                if (items.Count >= Max) break;
            }
        }

        private static void LoadLegacyText()
        {
            foreach (string raw in File.ReadAllLines(LegacyFileName, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                if (!items.Contains(line)) items.Add(line);
                if (items.Count >= Max) break;
            }
        }

        /// <summary>
        /// 记一条。**在 UI 线程上调**（会写盘）。
        /// 已经有的话只提到最前面 —— 这样「最近去过的」总是排在前面，而且不会越积越多。
        /// </summary>
        public static void Add(string path)
        {
            string p = PathRules.Store(path);
            if (string.IsNullOrEmpty(p)) return;
            if (!PathRules.Restorable(p)) return;      // 开不了的东西不值得记（重启后点了会打不开）

            EnsureLoaded();
            int at = -1;
            for (int i = 0; i < items.Count; i++)
            {
                if (PathRules.Same(items[i], p)) { at = i; break; }
            }
            if (at == 0) return;                       // 已经就是最近那条，什么都不用动
            if (at > 0) items.RemoveAt(at);
            items.Insert(0, p);
            while (items.Count > Max) items.RemoveAt(items.Count - 1);
            Save();
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                StringBuilder sb = new StringBuilder();
                sb.Append("{\r\n");
                sb.Append("  \"_note\": \"TabbedExplorer 的历史记录（去过哪些文件夹），新的在前。删掉某一项就少一条记录。\",\r\n");
                sb.Append("  \"history\": ").Append(Json.Array(items)).Append("\r\n");
                sb.Append("}\r\n");
                string tmp = FileName + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(FileName)) File.Delete(FileName);
                File.Move(tmp, FileName);
            }
            catch (Exception ex) { Diag.Log("历史: 写失败 " + ex.Message); }
        }

        public static void Clear()
        {
            EnsureLoaded();
            items.Clear();
            Save();
            Diag.Step("历史: 已清空");
        }

        /// <summary>
        /// 拼出「历史记录」那份菜单。
        /// 用 `MenuItem`（跟托盘/齿轮同一套），不是 `ContextMenuStrip` —— 见 SettingsMenu 里那段坑的说明。
        /// </summary>
        public static MenuItem[] BuildMenu(Action<string> open)
        {
            List<MenuItem> r = new List<MenuItem>();
            List<string> list = Recent;

            MenuItem top = new MenuItem("历史记录（Ctrl+H）");
            top.Enabled = false;
            r.Add(top);
            r.Add(new MenuItem("-"));

            if (list.Count == 0)
            {
                MenuItem none = new MenuItem("（还没有记录）");
                none.Enabled = false;
                r.Add(none);
            }
            else
            {
                int n = Math.Min(list.Count, MenuMax);
                for (int i = 0; i < n; i++)
                {
                    string p = list[i];
                    MenuItem mi = new MenuItem(Elide(p, 72));
                    if (open != null)
                    {
                        string target = p;
                        mi.Click += delegate { open(target); };
                    }
                    r.Add(mi);
                }
                if (list.Count > n)
                {
                    MenuItem more = new MenuItem("（还有 " + (list.Count - n) + " 条更早的，看 data\\history.json）");
                    more.Enabled = false;
                    r.Add(more);
                }
            }

            r.Add(new MenuItem("-"));
            // 直接打开那个 json（系统默认程序），比弹一个「在资源管理器里定位」省事，
            // 也避免我们自己又去起一个 explorer 窗口被自己的捕获逻辑再抓一遍。
            r.Add(new MenuItem("打开历史记录文件", delegate
            {
                try { System.Diagnostics.Process.Start(FileName); }
                catch (Exception ex) { Toast.Show("打不开历史文件", ex.Message); }
            }));
            r.Add(new MenuItem("清空历史记录", delegate
            {
                Clear();
            }));
            return r.ToArray();
        }

        /// <summary>
        /// 长了就从**中间**截掉（`D:\很长的…\末级目录`）——
        /// 前面是盘符/根、后面是最后停在哪一层，两头都比中间有用。
        /// </summary>
        private static string Elide(string p, int max)
        {
            if (string.IsNullOrEmpty(p) || p.Length <= max) return p;
            int keepTail = max * 6 / 10;
            int keepHead = max - keepTail - 5;
            return p.Substring(0, keepHead) + "  …  " + p.Substring(p.Length - keepTail);
        }
    }
}
