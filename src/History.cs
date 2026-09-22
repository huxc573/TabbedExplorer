using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace TabbedExplorer
{
    /// <summary>历史里的一条：去过哪儿 + 什么时候去的（`At` 为空 = 老数据，时间不知道）。</summary>
    internal sealed class HistoryItem
    {
        public string Path;
        /// <summary>本地时间 `yyyy-MM-dd HH:mm`。空字符串 = 迁移过来的老记录。</summary>
        public string At;

        public HistoryItem(string path, string at) { Path = path; At = at; }
    }

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
    ///   { "history": [ { "path": "D:\\FB.Data", "at": "2026-09-22 21:30" } ] }
    ///   </code>
    /// 新的在前。重复访问只把那条提到最前面（并刷新时间），不会刷屏。
    ///
    /// ⚠ 2026-09-22 川要「历史记录按日期归类」—— 原来这里**故意不带时间戳**
    ///   （当时的理由：「挑一个再去一次」，不是考古）。要按日期归类就必须有时间，
    ///   所以这一版给每条加上 `at`，菜单/管理器按「今天 / 昨天 / M月d日」分堆。
    ///   老数据（裸字符串数组）读进来 `at` 留空，归到「更早」那一堆，不会被丢掉。
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

        private static readonly List<HistoryItem> items = new List<HistoryItem>();
        private static bool loaded;

        public static string FileName { get { return AppPaths.File("history.json"); } }

        /// <summary>老版本的纯文本历史 —— 只在迁移时读一次。</summary>
        public static string LegacyFileName { get { return AppPaths.File("history.txt"); } }

        /// <summary>最近去过的地方（新的在前）。返回的是内部列表的拷贝，外面随便用。</summary>
        public static List<HistoryItem> Recent
        {
            get { EnsureLoaded(); return new List<HistoryItem>(items); }
        }

        /// <summary>只要路径（有些地方不关心时间）。</summary>
        public static List<string> RecentPaths
        {
            get
            {
                List<HistoryItem> l = Recent;
                List<string> r = new List<string>(l.Count);
                for (int i = 0; i < l.Count; i++) r.Add(l[i].Path);
                return r;
            }
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

        /// <summary>
        /// 读 json。**两种格式都认** ——
        /// 新的是 `{ "path": …, "at": … }` 对象，老的是裸字符串（时间留空）。
        /// 用 `FavStore.JsonLite` 那个只认自家格式的小解析器（它顺手就把对象 / 字符串都读出来了）。
        /// </summary>
        private static void LoadJson(string json)
        {
            List<object> arr = FavStore.JsonLite.GetArray(Json.GetBlock(json, "history"));
            for (int i = 0; i < arr.Count; i++)
            {
                object o = arr[i];
                Dictionary<string, object> d = o as Dictionary<string, object>;
                if (d != null)
                {
                    object pv, av;
                    string p = d.TryGetValue("path", out pv) ? pv as string : null;
                    string at = d.TryGetValue("at", out av) ? av as string : null;
                    AddRaw(p, at);
                }
                else AddRaw(o as string, null);
                if (items.Count >= Max) break;
            }
        }

        /// <summary>收一条（读盘用，不去重地往后追加；`At` 空 = 时间未知）。</summary>
        private static void AddRaw(string path, string at)
        {
            if (string.IsNullOrEmpty(path)) return;
            for (int i = 0; i < items.Count; i++)
                if (PathRules.Same(items[i].Path, path)) return;      // 老文件里有重复：只留第一条
            items.Add(new HistoryItem(path, at ?? ""));
        }

        private static void LoadLegacyText()
        {
            foreach (string raw in File.ReadAllLines(LegacyFileName, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                AddRaw(line, null);
                if (items.Count >= Max) break;
            }
        }

        /// <summary>
        /// 记一条。**在 UI 线程上调**（会写盘）。
        /// 已经有的话只提到最前面（并刷新时间）—— 这样「最近去过的」总是排在前面，而且不会越积越多。
        /// </summary>
        public static void Add(string path)
        {
            string p = PathRules.Store(path);
            if (string.IsNullOrEmpty(p)) return;
            if (!PathRules.Restorable(p)) return;      // 开不了的东西不值得记（重启后点了会打不开）

            EnsureLoaded();
            string now = Stamp();
            int at = -1;
            for (int i = 0; i < items.Count; i++)
            {
                if (PathRules.Same(items[i].Path, p)) { at = i; break; }
            }
            if (at == 0) { items[0].At = now; Save(); return; }   // 已经是最新那条：只把时间往前提
            if (at > 0) items.RemoveAt(at);
            items.Insert(0, new HistoryItem(p, now));
            while (items.Count > Max) items.RemoveAt(items.Count - 1);
            Save();
        }

        private static string Stamp() { return DateTime.Now.ToString("yyyy-MM-dd HH:mm"); }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                StringBuilder sb = new StringBuilder();
                sb.Append("{\r\n");
                sb.Append("  \"_note\": \"TabbedExplorer 的历史记录（去过哪些文件夹），新的在前。at 是本地时间；删掉某一项就少一条记录。\",\r\n");
                sb.Append("  \"history\": [\r\n");
                for (int i = 0; i < items.Count; i++)
                {
                    sb.Append("    { \"path\": ").Append(Json.Str(items[i].Path))
                      .Append(", \"at\": ").Append(Json.Str(items[i].At)).Append(" }")
                      .Append(i < items.Count - 1 ? ",\r\n" : "\r\n");
                }
                sb.Append("  ]\r\n");
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

        /// <summary>删掉一条（历史管理器里用）。返回有没有删到。只动 json，磁盘上不碰。</summary>
        public static bool Remove(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            EnsureLoaded();
            for (int i = 0; i < items.Count; i++)
            {
                if (PathRules.Same(items[i].Path, path))
                {
                    items.RemoveAt(i);
                    Save();
                    Diag.Step("历史: 删掉一条 " + path);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 一次删掉一批（管理器里多选 / 按日期分组删都用它）。返回真删掉了几条。
        ///
        /// 为什么不一件事调一次 `Remove`：那样每删一条就写一次盘，删 30 条写 30 次。
        /// 这里只落一次盘，而且**按路径比**（不是按下标）——`view` 是过滤后的子集，
        /// 下标跟 `items` 对不上，只能比路径。
        /// </summary>
        public static int RemoveMany(IList<string> paths)
        {
            if (paths == null || paths.Count == 0) return 0;
            EnsureLoaded();
            int gone = 0;
            for (int k = 0; k < paths.Count; k++)
            {
                string p = paths[k];
                if (string.IsNullOrEmpty(p)) continue;
                for (int i = items.Count - 1; i >= 0; i--)
                {
                    if (!PathRules.Same(items[i].Path, p)) continue;
                    items.RemoveAt(i);
                    gone++;
                    break;                       // 同一个路径只有一条
                }
            }
            if (gone == 0) return 0;
            Save();
            Diag.Step("历史: 一次删掉 " + gone + " 条");
            return gone;
        }

        /// <summary>某一堆日期里的全部路径（历史管理器按日期分组删时用）。</summary>
        public static List<string> PathsOfDay(string day)
        {
            EnsureLoaded();
            List<string> r = new List<string>();
            for (int i = 0; i < items.Count; i++)
            {
                if (string.Equals(DayLabel(items[i].At), day, StringComparison.Ordinal))
                    r.Add(items[i].Path);
            }
            return r;
        }

        // ==================================================================
        // 按日期归类（川 2026-09-22：历史记录按日期归类）
        // ==================================================================

        /// <summary>
        /// 一条记录的日期分堆标题：「今天」「昨天」「9月20日」；时间未知的老记录归「更早」。
        /// 菜单和管理器都用这一个，两处口径才不会不一样。
        /// </summary>
        public static string DayLabel(string at)
        {
            if (string.IsNullOrEmpty(at) || at.Length < 10) return "更早（时间未知）";
            string d = at.Substring(0, 10);
            DateTime t;
            if (!DateTime.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                       DateTimeStyles.None, out t))
                return d;
            DateTime today = DateTime.Today;
            if (t == today) return "今天";
            if (t == today.AddDays(-1)) return "昨天";
            if (t.Year == today.Year) return t.Month + "月" + t.Day + "日";
            return t.ToString("yyyy年M月d日");
        }

        /// <summary>只要时间那一段（`HH:mm`），列表行尾显示用。</summary>
        public static string TimeOf(string at)
        {
            if (string.IsNullOrEmpty(at) || at.Length < 16) return "";
            return at.Substring(11, 5);
        }

        // ==================================================================
        // 菜单
        // ==================================================================

        /// <summary>
        /// 拼出「历史记录」那份菜单 —— 走 `PopMenu`。
        /// 2026-09-22 从老的 `ContextMenu`/`MenuItem` 换过来：那条路上**点条目不触发 Click**
        /// （川报的「历史记录点开后所有功能都不可用」），原因见 `PopMenu` 类注释。
        /// `open == null` 的条目就是灰着的标题/说明行。
        /// `openManager` = 「打开历史记录管理器」（川 2026-09-22 把原来那条「打开历史记录文件」换成了它 ——
        /// 直接把 json 丢给记事本太糙，管理器里能搜、能挑、能手删）。
        ///
        /// 川 2026-09-22 追加：**按日期归类** —— 同一堆的前面插一条灰标题（今天 / 昨天 / …）。
        /// </summary>
        public static PopItem[] BuildMenu(Action<string> open, Action openManager)
        {
            List<PopItem> r = new List<PopItem>();
            List<HistoryItem> list = Recent;

            r.Add(PopMenu.It("历史记录（" + Hotkeys.Combo("history") + "）", null));
            r.Add(PopMenu.Split());

            if (list.Count == 0)
            {
                r.Add(PopMenu.It("（还没有记录）", null));
            }
            else
            {
                int n = Math.Min(list.Count, MenuMax);
                string lastDay = null;
                for (int i = 0; i < n; i++)
                {
                    HistoryItem h = list[i];
                    string day = DayLabel(h.At);
                    if (day != lastDay)
                    {
                        r.Add(PopMenu.It("── " + day + " ──", null));   // 日期分堆标题（灰的，点不动）
                        lastDay = day;
                    }
                    string target = h.Path;
                    // ⚠ 这个三元要显式转成 Action：`null` 和匿名方法之间没有隐式转换（CS0173）。
                    Action act = open == null ? (Action)null : delegate { open(target); };
                    string t = TimeOf(h.At);
                    r.Add(PopMenu.It(Elide(target, 72) + (t.Length > 0 ? ("    " + t) : ""), act));
                }
                if (list.Count > n)
                    r.Add(PopMenu.It("（还有 " + (list.Count - n) + " 条更早的，打开管理器查看）", null));
            }

            r.Add(PopMenu.Split());
            // 川 2026-09-22：「打开历史记录文件」改成「打开历史记录管理器」（界面模仿书签管理器）。
            r.Add(PopMenu.It("打开历史记录管理器", openManager));
            r.Add(PopMenu.It("清空历史记录", delegate
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
