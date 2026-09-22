using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TabbedExplorer
{
    /// <summary>
    /// 收藏夹的**数据层**（川 2026-09-22 要的：收藏夹不再用系统那个 Links，程序自己记）。
    ///
    /// 存在 `&lt;程序目录&gt;\data\favorites.json`（跟设置/记忆/历史一个地方，绿色便携）：
    ///   <code>
    ///   { "favorites": ["D:\\Shortcut", "D:\\Dev\\Workspaces", "D:\\x\\说明.txt"] }
    ///   </code>
    /// 就存**路径**，不存名字 —— 名字随时从路径末级算（`NameOf`），这样文件被改名/挪走也不会
    /// 显示成一个对不上的旧名字。文件夹和单个文件都可以放（文件点了用默认程序打开）。
    ///
    /// 为什么不用系统那个 `%USERPROFILE%\Links`（原来就是这么干的）：
    ///   · 那是**系统收藏夹**，川在别处执行李一样往里塞东西，我们这边跟着变，他也管不住；
    ///   · 往里加一项得写 .lnk（要 COM 的 IShellLink），而他说的是「拖进来就算收藏」。
    /// 首次运行会**从系统收藏夹播种一次**（把里面那几个 .lnk 解析成真实路径抄过来），
    /// 之后两边就再没关系了 —— 免得他觉得「原来那几个怎么没了」。
    /// </summary>
    internal static class FavStore
    {
        private static List<string> items;
        private static readonly object gate = new object();

        public static string FileName { get { return AppPaths.File("favorites.json"); } }

        /// <summary>系统收藏夹目录 —— 只在「首次播种」时读一次。</summary>
        public static string LegacyLinksDir
        {
            get
            {
                try
                {
                    return Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Links");
                }
                catch { return ""; }
            }
        }

        public static List<string> Items
        {
            get { lock (gate) { EnsureLoaded(); return new List<string>(items); } }
        }

        public static int Count { get { lock (gate) { EnsureLoaded(); return items.Count; } } }

        private static void EnsureLoaded()
        {
            if (items != null) return;
            items = new List<string>();
            try
            {
                if (File.Exists(FileName))
                {
                    foreach (string p in Json.Strings(Json.GetBlock(File.ReadAllText(FileName, Encoding.UTF8),
                                                                    "favorites")))
                    {
                        if (string.IsNullOrEmpty(p)) continue;
                        if (!items.Contains(p)) items.Add(p);
                    }
                    Diag.Step("收藏夹: 读到 " + items.Count + " 项");
                    return;
                }

                if (SeedFromLinks())
                {
                    Save();
                    Diag.Step("收藏夹: 首次运行，已从系统收藏夹播种 " + items.Count + " 项");
                    return;
                }
                Save();   // 生成一份空的（带说明），川打开就能看懂格式
            }
            catch (Exception ex) { Diag.Log("收藏夹: 读失败 " + ex.Message); }
        }

        /// <summary>把系统收藏夹里的东西抄一份进来（.lnk 解析成真实目标）。没有任何一项时返回 false。</summary>
        private static bool SeedFromLinks()
        {
            try
            {
                string dir = LegacyLinksDir;
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;

                List<string> entries = new List<string>(Directory.GetFileSystemEntries(dir));
                entries.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (string raw in entries)
                {
                    string n = Path.GetFileName(raw);
                    if (string.Equals(n, "desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        FileAttributes fa = File.GetAttributes(raw);
                        if ((fa & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    }
                    catch { }

                    string t = ResolveShortcut(raw);
                    if (!string.IsNullOrEmpty(t) && !items.Contains(t)) items.Add(t);
                }
                return items.Count > 0;
            }
            catch (Exception ex)
            {
                Diag.Log("收藏夹: 播种失败 " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 加一项。返回 false = 没加进去，此时 `name` 是「已经在收藏夹里的那一项的名字」
        /// （空字符串表示这个位置根本不能收藏：没真实路径）。
        /// </summary>
        public static bool Add(string path, out string name)
        {
            name = null;
            string p = NormalizePath(path);
            if (p == null) return false;
            name = NameOf(p);

            lock (gate)
            {
                EnsureLoaded();
                for (int i = 0; i < items.Count; i++)
                {
                    if (PathRules.Same(items[i], p)) return false;   // 已经有了
                }
                items.Add(p);
                Save();
            }
            return true;
        }

        /// <summary>移除一项（只从我们这份 json 里去掉，**不动磁盘上那个文件/文件夹**）。</summary>
        public static bool Remove(string path)
        {
            string p = NormalizePath(path);
            if (p == null) return false;
            lock (gate)
            {
                EnsureLoaded();
                for (int i = 0; i < items.Count; i++)
                {
                    if (!PathRules.Same(items[i], p)) continue;
                    items.RemoveAt(i);
                    Save();
                    return true;
                }
            }
            return false;
        }

        /// <summary>整条清空。</summary>
        public static void Clear()
        {
            lock (gate)
            {
                EnsureLoaded();
                items.Clear();
                Save();
            }
        }

        /// <summary>显示名：取路径末级（`.lnk` 去掉后缀）；虚拟文件夹给友好名。</summary>
        public static string NameOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            if (path.StartsWith("::", StringComparison.Ordinal))
                return PathRules.Same(path, ExplorerView.ThisPcPath) ? "此电脑" : "系统文件夹";
            try
            {
                string p = path.TrimEnd('\\', '/');
                int i = p.LastIndexOf('\\');
                string n = (i >= 0 && i < p.Length - 1) ? p.Substring(i + 1) : p;
                if (n.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                    n = n.Substring(0, n.Length - 4);
                return n;
            }
            catch { return path; }
        }

        /// <summary>是不是文件夹（决定点击行为：文件夹开标签、文件用默认程序打开）。</summary>
        public static bool IsFolder(string path)
        {
            try { return !string.IsNullOrEmpty(path) && Directory.Exists(path); }
            catch { return false; }
        }

        /// <summary>
        /// 收进来的路径先归一：能收藏的只有「真实存在的文件/文件夹」和 shell 虚拟路径。
        /// 库（视频 / 图片 这种）没有路径，收不了 —— 返回 null。
        /// </summary>
        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string p = path.Trim().Trim('"');
            if (p.Length == 0) return null;
            if (p.StartsWith("::", StringComparison.Ordinal) ||
                p.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) return p;
            try
            {
                if (File.Exists(p) || Directory.Exists(p)) return Path.GetFullPath(p);
            }
            catch { }
            return null;
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                StringBuilder sb = new StringBuilder();
                sb.Append("{\r\n");
                sb.Append("  \"_note\": \"TabbedExplorer 的收藏夹栏。就存路径，名字从路径末级算。把文件夹/文件拖到收藏夹栏上就会出现在这儿。\",\r\n");
                sb.Append("  \"_hint\": \"删掉某一项就少一个（也可以在收藏夹项上右键选「从收藏夹移除」）。只影响这个列表，不动磁盘上的文件。\",\r\n");
                sb.Append("  \"favorites\": ").Append(Json.Array(items)).Append("\r\n");
                sb.Append("}\r\n");

                string tmp = FileName + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(FileName)) File.Delete(FileName);
                File.Move(tmp, FileName);
            }
            catch (Exception ex) { Diag.Log("收藏夹: 写失败 " + ex.Message); }
        }

        // ==================================================================
        // .lnk → 真实目标（只在「从系统收藏夹播种」时用得上）
        //
        // **不走 COM**（`IShellLink` 要自己声明 vtable，本机没法调试，接错就是进程级崩溃）：
        // `.lnk` 的 `LinkInfo` 段里存着目标的本地路径字符串（ANSI 和/或 UTF-16），
        // 直接扫字节把它捞出来、再用 `Directory.Exists` 验一下，验不过就当解析失败。
        // 扫不到就**原样返回 `.lnk` 路径** —— 双击/点开时 shell 自己会解析，绝不猜、绝不返回空。
        // ==================================================================

        public static string ResolveShortcut(string raw)
        {
            try
            {
                if (Directory.Exists(raw) && !raw.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                    return raw;                                  // 本来就是个目录
                if (!raw.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return raw;

                byte[] b = File.ReadAllBytes(raw);

                string ansi = ScanPath(b, false);
                if (IsDir(ansi)) return ansi.TrimEnd('\0', ' ');

                string wide = ScanPath(b, true);
                if (IsDir(wide)) return wide.TrimEnd('\0', ' ');

                // 相对路径那种（`..\..\Desktop`）：按 lnk 所在目录算一下也算数
                string rel = ScanRelative(b);
                if (!string.IsNullOrEmpty(rel))
                {
                    try
                    {
                        string full = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(raw), rel));
                        if (IsDir(full)) return full;
                    }
                    catch { }
                }
            }
            catch (Exception ex) { Diag.Log("收藏夹: 解析 " + raw + " 失败 " + ex.Message); }
            return raw;
        }

        private static bool IsDir(string p)
        {
            try { return !string.IsNullOrEmpty(p) && Directory.Exists(p); }
            catch { return false; }
        }

        /// <summary>在字节流里找 `X:\...` 形式的绝对路径（`wide=false` 找 ANSI，`true` 找 UTF-16LE）。</summary>
        private static string ScanPath(byte[] b, bool wide)
        {
            int step = wide ? 2 : 1;
            for (int i = 0; i + 4 < b.Length; i++)
            {
                bool letter = (b[i] >= 'A' && b[i] <= 'Z') || (b[i] >= 'a' && b[i] <= 'z');
                if (!letter) continue;
                if (b[i + 1] != ':') { i += step - 1; continue; }
                if (wide)
                {
                    if (b[i + 2] != 0 || b[i + 3] != '\\') { i += 1; continue; }
                }
                else
                {
                    if (b[i + 2] != '\\') continue;
                }

                StringBuilder sb = new StringBuilder();
                int j = i;
                while (j < b.Length - (wide ? 1 : 0))
                {
                    char c = wide ? (char)(b[j] | (b[j + 1] << 8)) : (char)b[j];
                    if (c == '\0') break;
                    if (c < ' ') break;
                    if (c == '\\' || c == '/' || c == ':' || c == '.' || c == '_' || c == '-' ||
                        c == ' ' || c == '(' || c == ')' || c == '#' || c == '+' || c == '&' ||
                        c == '\'' || c == ',' || c == '~' || c == '!' || c == '@' || c == '$' ||
                        c == '=' || c == '{' || c == '}' || c == '[' || c == ']' || c == '^' ||
                        (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                        c > 127)
                    {
                        sb.Append(c);
                        j += wide ? 2 : 1;
                    }
                    else break;
                }
                string s = sb.ToString();
                if (s.Length >= 4) return s.TrimEnd('\\', '/');
            }
            return null;
        }

        /// <summary>找 `..\..\Foo` 这种相对路径（`LinkInfo` 里常见，UTF-16 存）。</summary>
        private static string ScanRelative(byte[] b)
        {
            for (int i = 0; i + 8 < b.Length; i++)
            {
                if (b[i] != '.' || b[i + 1] != 0) continue;
                if (b[i + 2] != '.' || b[i + 3] != 0) continue;
                if (b[i + 4] != '\\' || b[i + 5] != 0) continue;
                StringBuilder sb = new StringBuilder();
                int j = i;
                while (j + 1 < b.Length)
                {
                    char c = (char)(b[j] | (b[j + 1] << 8));
                    if (c == '\0') break;
                    if (c < ' ' || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' ||
                        c == '>' || c == '|') break;
                    sb.Append(c);
                    j += 2;
                }
                string s = sb.ToString();
                if (s.Length > 3 && s.IndexOf('\\') >= 0) return s;
            }
            return null;
        }
    }
}
