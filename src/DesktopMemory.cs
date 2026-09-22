using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TabbedExplorer
{
    /// <summary>
    /// 「地址栏上那个字符串」和「能存能还原的路径」之间的翻译。
    ///
    /// 地址栏给的**不是**永远都是路径：
    ///   - 真目录 → 完整路径（实测 `地址: D:\Dev\Workspaces\WorkBuddy\TabbedExplorer`）；
    ///   - `此电脑` / `回收站` / `网络` 这种虚拟文件夹 → 就是那个**显示名**，拿去喂 explorer 是错的；
    ///   - 库（比如 `视频`）→ 也是显示名，而且它压根不对应某个目录。
    ///
    /// 所以存之前先归一到 shell 认的写法（`::` / `shell:`），还原之前再验一遍
    /// 「这玩意儿开得开吗」—— 宁可不还原那一个标签，也别丢一个开不了的路径给 explorer
    /// （`explorer.exe "视频"` 会当成当前目录下的一个相对路径，指不定开到哪里去）。
    /// </summary>
    internal static class PathRules
    {
        private static readonly Dictionary<string, string> virtuals =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "此电脑",            ExplorerView.ThisPcPath },
                { "This PC",          ExplorerView.ThisPcPath },
                { "回收站",            "shell:RecycleBinFolder" },
                { "Recycle Bin",      "shell:RecycleBinFolder" },
                { "网络",              "shell:NetworkPlacesFolder" },
                { "Network",          "shell:NetworkPlacesFolder" },
                { "下载",              "shell:Downloads" },
                { "Downloads",        "shell:Downloads" },
                { "桌面",              "shell:Desktop" },
                { "Desktop",          "shell:Desktop" },
            };

        /// <summary>地址栏文字 → 存进记忆的值。认不出来的原样存（还原时再判）。</summary>
        public static string Store(string addr)
        {
            if (string.IsNullOrEmpty(addr)) return null;
            addr = addr.Trim();
            if (addr.Length == 0) return null;
            string v;
            if (virtuals.TryGetValue(addr, out v)) return v;
            return addr;
        }

        /// <summary>这个值能不能直接交给 explorer.exe 去开。</summary>
        public static bool Restorable(string p)
        {
            if (string.IsNullOrEmpty(p)) return false;
            p = p.Trim();
            if (p.Length == 0) return false;
            if (p.StartsWith("::") || p.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) return true;
            // 只认**绝对且真实存在**的目录：相对路径会相对当前工作目录解析，那是撞运气。
            try { return Path.IsPathRooted(p) && Directory.Exists(p); }
            catch { return false; }
        }

        /// <summary>比路径用：`c:\Users\a\` 和 `C:\Users\a` 必须算同一个。</summary>
        public static string Norm(string p)
        {
            if (string.IsNullOrEmpty(p)) return "";
            p = p.Trim();
            if (p.Length > 3) p = p.TrimEnd('\\', '/');
            return p.ToLowerInvariant();
        }

        public static bool Same(string a, string b)
        {
            return string.Equals(Norm(a), Norm(b), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 按虚拟桌面记住标签页 —— 也就是川要的「记忆那个桌面关闭程序窗口时的标签页，下次打开」。
    ///
    /// 为什么用桌面 GUID 当 key：`IVirtualDesktop::GetId` 给的 GUID 是 shell 自己生成并持久化的，
    /// 重启、重排桌面都还是同一个；而「第 1/2/3 张桌面」这种序号一重排就全错位了
    /// （本机实测：090EFE42=Daily、ABF524EA=Game、EC74F739=Other）。
    ///
    /// 存的是**地址栏上的那个字符串**、不是我们当初传给 explorer 的路径 —— 川在标签里一路点进去
    /// 之后，要记的是他最后停在哪儿。
    ///
    /// 文件是纯文本 `%APPDATA%\TabbedExplorer\desktops.txt`，一行一个标签：
    ///   <code>
    ///   # TabbedExplorer desktops v1
    ///   [090efe42-....]
    ///   * D:\FB.Data
    ///     ::{20D04FE0-3AEA-1069-A2D8-08002B30309D}
    ///   </code>
    /// `*` 是那张桌面上「当时选中的那个标签」。故意不用 JSON：本机没有 NuGet，
    /// 手写解析越简单越好，而且出问题时川自己打开就能看、能改、能删。
    /// </summary>
    internal sealed class DesktopMemory
    {
        public sealed class Bucket
        {
            public readonly List<string> Paths = new List<string>();
            /// <summary>当时选中的那个标签（还原完一并切过去）。</summary>
            public string Active;
        }

        private const string Header = "# TabbedExplorer desktops v1";

        public static string Folder
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TabbedExplorer");
            }
        }

        public static string FileName { get { return Path.Combine(Folder, "desktops.txt"); } }

        private readonly Dictionary<string, Bucket> map =
            new Dictionary<string, Bucket>(StringComparer.OrdinalIgnoreCase);

        public int Count { get { return map.Count; } }

        /// <summary>某张桌面记过的标签；没记过返回 null（**不要**凭空造空桶，否则会把已有记忆洗掉）。</summary>
        public Bucket Find(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            Bucket b;
            return map.TryGetValue(key, out b) ? b : null;
        }

        /// <summary>要往这张桌面上写记忆了，拿一个桶（没有就新建）。</summary>
        public Bucket Ensure(string key)
        {
            if (string.IsNullOrEmpty(key)) key = "unknown";
            Bucket b;
            if (!map.TryGetValue(key, out b)) { b = new Bucket(); map[key] = b; }
            return b;
        }

        /// <summary>读盘。文件不在、或读坏了，都只当「没记过」—— 记忆坏了绝不该拦住启动。</summary>
        public void Load()
        {
            map.Clear();
            try
            {
                string f = FileName;
                if (!File.Exists(f)) { Diag.Step("记忆: 还没有 " + f + "（第一次跑）"); return; }

                string cur = null;
                int n = 0;
                foreach (string raw in File.ReadAllLines(f, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;

                    if (line[0] == '[' && line[line.Length - 1] == ']')
                    {
                        cur = line.Substring(1, line.Length - 2).Trim();
                        Ensure(cur);
                        continue;
                    }
                    if (cur == null) continue;

                    bool active = (line[0] == '*');
                    string p = (active ? line.Substring(1) : line).Trim();
                    if (p.Length == 0) continue;

                    Bucket b = Ensure(cur);
                    b.Paths.Add(p);
                    if (active) b.Active = p;
                    n++;
                }
                Diag.Step("记忆: 读到 " + map.Count + " 张桌面 / " + n + " 个标签 <- " + f);
            }
            catch (Exception ex)
            {
                Diag.Log("记忆: 读失败（当没记过）" + ex.Message);
                map.Clear();
            }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                StringBuilder sb = new StringBuilder();
                sb.Append(Header).Append("\r\n");
                foreach (KeyValuePair<string, Bucket> kv in map)
                {
                    if (kv.Value == null || kv.Value.Paths.Count == 0) continue;   // 没标签的桌面就别留段落
                    sb.Append('[').Append(kv.Key).Append("]\r\n");
                    foreach (string p in kv.Value.Paths)
                    {
                        bool act = !string.IsNullOrEmpty(kv.Value.Active) && PathRules.Same(p, kv.Value.Active);
                        sb.Append(act ? "* " : "  ").Append(p).Append("\r\n");
                    }
                }

                // 先写临时文件再换过去：写到一半被硬杀也不会把好文件截断成半截。
                string tmp = FileName + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(FileName)) File.Delete(FileName);
                File.Move(tmp, FileName);
            }
            catch (Exception ex) { Diag.Log("记忆: 写失败 " + ex.Message); }
        }
    }
}
