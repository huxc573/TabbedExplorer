using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace TabbedExplorer
{
    /// <summary>
    /// 收藏夹里的一个节点：**要么是文件夹（有 Kids），要么是一项收藏（有 Path）**。
    /// 文件夹可以无限层嵌套 —— 川 2026-09-22 要的「支持文件夹嵌套」。
    /// </summary>
    internal sealed class FavNode
    {
        /// <summary>显示名。文件夹必填；收藏项留空就从路径末级算（`FavStore.NameOf`）。</summary>
        public string Name;
        /// <summary>收藏的目标路径。**为 null = 这是个文件夹**。</summary>
        public string Path;
        /// <summary>这个文件夹是不是「收藏夹栏」那一份（横向那条栏显示它下面的东西）。</summary>
        public bool Bar;

        public List<FavNode> Kids = new List<FavNode>();

        public bool IsFolder { get { return Path == null; } }

        public string Display { get { return FavStore.NameOf(this); } }
    }

    /// <summary>
    /// 收藏夹的**数据层**（川 2026-09-22：不再用系统那个 Links，程序自己记）。
    ///
    /// 存在 `&lt;程序目录&gt;\data\favorites.json`，结构就是**树**、缩进对齐，方便他手改：
    ///   <code>
    ///   {
    ///     "_note": "…",
    ///     "favorites": [
    ///       {
    ///         "bar": true,                       ← 这个文件夹 = 横向那条收藏夹栏
    ///         "name": "收藏夹栏",
    ///         "children": [
    ///           { "path": "D:\\Server" },                       ← 只用路径，名字从末级算
    ///           { "name": "学习", "path": "D:\\Learn" },         ← 手改过名字
    ///           { "name": "娱乐", "children": [                   ← 子文件夹（嵌套）
    ///               { "path": "D:\\Sign" } ] }
    ///         ]
    ///       },
    ///       { "name": "工作区", "children": [ { "path": "D:\\A" } ] }
    ///     ]
    ///   }
    ///   </code>
    ///
    /// ⚠ 老格式（`"favorites": ["D:\\A", "D:\\B"]` 一串纯路径）读进来会自动包成
    ///   「收藏夹栏」文件夹 —— 川那一份十几项不用手动改。
    ///
    /// 为什么不用系统那个 `%USERPROFILE%\Links`（原来就是这么干的）：
    ///   · 那是**系统收藏夹**，他在别处往里塞东西我们这边跟着变，他管不住；
    ///   · 往里加一项得写 .lnk（要 COM 的 IShellLink），而他要的是「拖进来就算收藏」。
    /// 首次运行会**从系统收藏夹播种一次**，之后两边再没关系。
    /// </summary>
    internal static class FavStore
    {
        private static List<FavNode> roots;
        private static readonly object gate = new object();

        public static string FileName { get { return AppPaths.File("favorites.json"); } }

        /// <summary>树变了（增删改 / 换名）—— 收藏夹栏和已经开着的管理器窗口都听它刷新。</summary>
        public static event Action Changed;

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

        // ==================================================================
        // 读
        // ==================================================================

        public static FavNode[] Tree
        {
            get { lock (gate) { EnsureLoaded(); return roots.ToArray(); } }
        }

        /// <summary>「收藏夹栏」那个文件夹（横向那条栏显示的就是它下面的东西）。</summary>
        public static FavNode BarFolder
        {
            get
            {
                lock (gate)
                {
                    EnsureLoaded();
                    for (int i = 0; i < roots.Count; i++)
                        if (roots[i].Bar) return roots[i];
                    // 没有就现建一个（老 json / 手改坏了都不至于没栏可用）
                    FavNode b = new FavNode();
                    b.Name = "收藏夹栏";
                    b.Bar = true;
                    roots.Insert(0, b);
                    return b;
                }
            }
        }

        /// <summary>栏上要显示的那一排（收藏夹栏文件夹的直接孩子）。</summary>
        public static FavNode[] BarItems
        {
            get { lock (gate) { return BarFolder.Kids.ToArray(); } }
        }

        private static void EnsureLoaded()
        {
            if (roots != null) return;
            roots = new List<FavNode>();
            try
            {
                if (File.Exists(FileName))
                {
                    string txt = File.ReadAllText(FileName, Encoding.UTF8);
                    List<object> arr = JsonLite.GetArray(Json.GetBlock(txt, "favorites"));
                    if (arr.Count > 0)
                    {
                        for (int i = 0; i < arr.Count; i++)
                        {
                            FavNode n = FromJson(arr[i]);
                            if (n != null) roots.Add(n);
                        }
                    }
                    else
                    {
                        // 老格式：一串纯路径 → 包成「收藏夹栏」
                        List<string> flat = Json.Strings(Json.GetBlock(txt, "favorites"));
                        if (flat.Count > 0)
                        {
                            FavNode b = new FavNode();
                            b.Name = "收藏夹栏";
                            b.Bar = true;
                            for (int i = 0; i < flat.Count; i++)
                            {
                                string p = NormalizePath(flat[i]);
                                if (p == null) continue;
                                FavNode it = new FavNode();
                                it.Path = p;
                                b.Kids.Add(it);
                            }
                            roots.Add(b);
                            SaveLocked();
                            Diag.Step("收藏夹: 老的纯路径格式已升级成树（" + b.Kids.Count + " 项）");
                        }
                    }
                    if (roots.Count > 0 && BarFolder != null)
                        Diag.Step("收藏夹: 读到 " + CountAll(roots) + " 项（树 " + roots.Count + " 个顶层文件夹）");
                    return;
                }

                if (SeedFromLinks())
                {
                    SaveLocked();
                    Diag.Step("收藏夹: 首次运行，已从系统收藏夹播种 " + CountAll(roots) + " 项");
                    return;
                }
                SaveLocked();   // 生成一份空的（带说明），川打开就能看懂格式
            }
            catch (Exception ex) { Diag.Log("收藏夹: 读失败 " + ex.Message); }
        }

        public static int Count { get { lock (gate) { return CountAll(Tree0()); } } }

        private static List<FavNode> Tree0() { EnsureLoaded(); return roots; }

        private static int CountAll(List<FavNode> l)
        {
            int n = 0;
            for (int i = 0; i < l.Count; i++)
            {
                if (l[i].IsFolder) n += CountAll(l[i].Kids);
                else n++;
            }
            return n;
        }

        // ==================================================================
        // 改（都在锁里做，改完存盘 + 通知）
        // ==================================================================

        /// <summary>
        /// 改树：`f` 拿到根列表，随便改（增删 / 换名 / 嵌套），返回后自动存盘 + 通知。
        /// 管理器窗口和收藏夹栏的右键都走这一个口，免得两处各写一套存盘逻辑。
        /// </summary>
        public static void Edit(Action<List<FavNode>> f)
        {
            if (f == null) return;
            lock (gate)
            {
                EnsureLoaded();
                f(roots);
                SaveLocked();
            }
            RaiseChanged();
        }

        private static void RaiseChanged()
        {
            try { Action h = Changed; if (h != null) h(); }
            catch (Exception ex) { Diag.Log("收藏夹: 通知刷新失败 " + ex.Message); }
        }

        /// <summary>
        /// 加一项到收藏夹栏。返回 false = 没加进去，此时 `name` 是「已经在里面的那一项的名字」
        /// （空字符串表示这个位置根本不能收藏：没有真实路径）。
        /// </summary>
        public static bool Add(string path, out string name)
        {
            name = null;
            string p = NormalizePath(path);
            if (p == null) return false;
            name = NameOfPath(p);

            bool added = false;
            Edit(delegate(List<FavNode> l)
            {
                EnsureLoaded();
                FavNode bar = BarFolder;
                if (bar == null) return;
                if (FindItem(bar.Kids, p) != null) return;      // 已经有了
                FavNode it = new FavNode();
                it.Path = p;
                bar.Kids.Add(it);
                added = true;
            });
            return added;
        }

        /// <summary>移除第一处匹配的收藏项（只从我们这份 json 里去掉，**不动磁盘上那个文件/文件夹**）。</summary>
        public static bool Remove(string path)
        {
            string p = NormalizePath(path);
            if (p == null) return false;
            bool ok = false;
            Edit(delegate(List<FavNode> l)
            {
                EnsureLoaded();
                ok = RemoveItem(roots, p);
            });
            return ok;
        }

        private static bool RemoveItem(List<FavNode> l, string p)
        {
            for (int i = 0; i < l.Count; i++)
            {
                FavNode n = l[i];
                if (!n.IsFolder && PathRules.Same(n.Path, p)) { l.RemoveAt(i); return true; }
                if (n.IsFolder && RemoveItem(n.Kids, p)) return true;
            }
            return false;
        }

        /// <summary>给某一项改显示名（磁盘上那个文件夹/文件一个字节都不动）。</summary>
        public static bool SetName(string path, string name)
        {
            string p = NormalizePath(path);
            if (p == null) return false;
            bool ok = false;
            Edit(delegate(List<FavNode> l)
            {
                EnsureLoaded();
                FavNode it = FindItem(roots, p);
                if (it == null) return;
                it.Name = string.IsNullOrEmpty(name) || name == NameOfPath(p) ? null : name.Trim();
                ok = true;
            });
            return ok;
        }

        /// <summary>找某一项（全树找第一个匹配的）。`null` 表示没找到。</summary>
        public static FavNode Find(string path)
        {
            string p = NormalizePath(path);
            if (p == null) return null;
            lock (gate) { EnsureLoaded(); return FindItem(roots, p); }
        }

        private static FavNode FindItem(List<FavNode> l, string p)
        {
            for (int i = 0; i < l.Count; i++)
            {
                FavNode n = l[i];
                if (!n.IsFolder && PathRules.Same(n.Path, p)) return n;
                if (n.IsFolder)
                {
                    FavNode r = FindItem(n.Kids, p);
                    if (r != null) return r;
                }
            }
            return null;
        }

        /// <summary>整条清空（只留一个空的「收藏夹栏」）。</summary>
        public static void Clear()
        {
            Edit(delegate(List<FavNode> l)
            {
                EnsureLoaded();
                roots.Clear();
                FavNode b = new FavNode();
                b.Name = "收藏夹栏";
                b.Bar = true;
                roots.Add(b);
            });
        }

        // ==================================================================
        // 名字 / 路径
        // ==================================================================

        /// <summary>显示名：手改过的名字优先，否则取路径末级（`.lnk` 去后缀）；虚拟文件夹给友好名。</summary>
        public static string NameOf(FavNode n)
        {
            if (n == null) return "";
            if (!string.IsNullOrEmpty(n.Name)) return n.Name;
            if (n.IsFolder) return "文件夹";
            return NameOfPath(n.Path);
        }

        public static string NameOfPath(string path)
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

        // ==================================================================
        // 存（缩进对齐，照着就能手改）
        // ==================================================================

        private static void SaveLocked()
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                StringBuilder sb = new StringBuilder();
                sb.Append("{\r\n");
                sb.Append("  \"_note\": \"TabbedExplorer 的收藏夹。结构就是一棵树：文件夹带 name + children，收藏项带 path。\"\r\n");
                sb.Append("  \"_hint\": \"bar=true 的那个文件夹就是标签条下面那条横向收藏夹栏。名字不写就按路径末级算；删一项不会动磁盘上的文件。\",\r\n");
                sb.Append("  \"_edited\": \"").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("\",\r\n");
                sb.Append("  \"favorites\": [\r\n");
                for (int i = 0; i < roots.Count; i++)
                    WriteNode(sb, roots[i], 2, i < roots.Count - 1);
                sb.Append("  ]\r\n");
                sb.Append("}\r\n");

                string tmp = FileName + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(FileName)) File.Delete(FileName);
                File.Move(tmp, FileName);
            }
            catch (Exception ex) { Diag.Log("收藏夹: 写失败 " + ex.Message); }
        }

        private static void WriteNode(StringBuilder sb, FavNode n, int indent, bool comma)
        {
            string pad = new string(' ', indent * 2);
            string pad2 = new string(' ', (indent + 1) * 2);
            sb.Append(pad).Append("{\r\n");
            if (n.Bar) sb.Append(pad2).Append("\"bar\": true,\r\n");
            sb.Append(pad2).Append("\"name\": ").Append(Json.Str(DisplayNameForSave(n))).Append(",\r\n");
            if (!n.IsFolder) sb.Append(pad2).Append("\"path\": ").Append(Json.Str(n.Path)).Append("\r\n");
            else
            {
                sb.Append(pad2).Append("\"children\": [");
                if (n.Kids.Count == 0) sb.Append("]\r\n");
                else
                {
                    sb.Append("\r\n");
                    for (int i = 0; i < n.Kids.Count; i++)
                        WriteNode(sb, n.Kids[i], indent + 2, i < n.Kids.Count - 1);
                    sb.Append(pad2).Append("]\r\n");
                }
            }
            sb.Append(pad).Append("}").Append(comma ? ",\r\n" : "\r\n");
        }

        /// <summary>存盘时的名字：文件夹一定写（手改坏了也得有个名字），收藏项只在改过名时写。</summary>
        private static string DisplayNameForSave(FavNode n)
        {
            if (n.IsFolder) return string.IsNullOrEmpty(n.Name) ? "文件夹" : n.Name;
            return n.Name;      // 可能是 null → Json.Str 会写成 ""，读回来按「没改过名」处理
        }

        // ==================================================================
        // 一个**只认我们自己这份格式**的极简 JSON 读入器
        // （Json.cs 那套只认「平铺的字符串数组 / 平铺对象」，读不了嵌套树；
        //   这里的格式是我们自己写出去的，键名固定，不用做通用 JSON 解析器。）
        // ==================================================================

        private static FavNode FromJson(object o)
        {
            Dictionary<string, object> d = o as Dictionary<string, object>;
            if (d == null) return null;      // 老格式的裸字符串走另一条路

            FavNode n = new FavNode();
            n.Name = Str(d, "name");
            n.Path = NormalizePath(Str(d, "path"));
            n.Bar = Bool(d, "bar");

            object kids;
            if (d.TryGetValue("children", out kids))
            {
                List<object> arr = kids as List<object>;
                if (arr != null)
                {
                    for (int i = 0; i < arr.Count; i++)
                    {
                        FavNode k = FromJson(arr[i]);
                        if (k != null) n.Kids.Add(k);
                    }
                }
                n.Path = null;               // 有孩子就是文件夹（手改时写了 path 也以文件夹算）
            }
            if (n.IsFolder && string.IsNullOrEmpty(n.Name)) n.Name = "文件夹";
            if (!n.IsFolder && n.Path == null) return null;
            if (!string.IsNullOrEmpty(n.Name) && !string.IsNullOrEmpty(n.Path) &&
                n.Name == NameOfPath(n.Path)) n.Name = null;   // 和路径算出来的一样就当没改过
            return n;
        }

        private static string Str(Dictionary<string, object> d, string k)
        {
            object v;
            if (d.TryGetValue(k, out v)) return v as string;
            return null;
        }

        private static bool Bool(Dictionary<string, object> d, string k)
        {
            object v;
            if (!d.TryGetValue(k, out v)) return false;
            string s = v as string;
            return s != null && string.Equals(s, "true", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 够用就行的 JSON：`{}` / `[]` / 字符串 / 裸字（true/false/数字）。
        /// 读不了就返回空 —— 收藏夹读不出来不至于让程序起不来。
        /// </summary>
        internal static class JsonLite
        {
            /// <summary>把一个 `[ ... ]` 块读成列表（元素是字符串 / 字典 / 列表）。</summary>
            public static List<object> GetArray(string block)
            {
                List<object> r = new List<object>();
                if (string.IsNullOrEmpty(block)) return r;
                int i = block.IndexOf('[');
                if (i < 0) return r;
                i++;
                try { ParseArray(block, ref i, r); }
                catch (Exception ex) { Diag.Log("收藏夹: json 读失败 " + ex.Message); r.Clear(); }
                return r;
            }

            private static void ParseArray(string s, ref int i, List<object> into)
            {
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return; }
                while (i < s.Length)
                {
                    into.Add(ParseValue(s, ref i));
                    SkipWs(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == ']') { i++; return; }
                    return;
                }
            }

            private static void ParseObject(string s, ref int i)
            {
                // 不该走到这儿（对象由 ParseValue 处理）
            }

            private static object ParseValue(string s, ref int i)
            {
                SkipWs(s, ref i);
                if (i >= s.Length) return null;
                char c = s[i];
                if (c == '{')
                {
                    Dictionary<string, object> d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    i++;
                    SkipWs(s, ref i);
                    if (i < s.Length && s[i] == '}') { i++; return d; }
                    while (i < s.Length)
                    {
                        SkipWs(s, ref i);
                        if (s[i] != '"') return d;
                        string k = ParseString(s, ref i);
                        SkipWs(s, ref i);
                        if (i < s.Length && s[i] == ':') i++;
                        object v = ParseValue(s, ref i);
                        d[k] = v;
                        SkipWs(s, ref i);
                        if (i < s.Length && s[i] == ',') { i++; continue; }
                        if (i < s.Length && s[i] == '}') { i++; return d; }
                        return d;
                    }
                    return d;
                }
                if (c == '[')
                {
                    i++;
                    List<object> l = new List<object>();
                    ParseArray(s, ref i, l);
                    return l;
                }
                if (c == '"') return ParseString(s, ref i);

                int st = i;
                while (i < s.Length && s[i] != ',' && s[i] != '}' && s[i] != ']' && s[i] != '\r' && s[i] != '\n')
                    i++;
                return s.Substring(st, i - st).Trim();
            }

            private static string ParseString(string s, ref int i)
            {
                if (i >= s.Length || s[i] != '"') return null;
                i++;
                StringBuilder sb = new StringBuilder();
                while (i < s.Length)
                {
                    char c = s[i++];
                    if (c == '"') break;
                    if (c != '\\') { sb.Append(c); continue; }
                    if (i >= s.Length) break;
                    char e = s[i++];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (i + 4 <= s.Length)
                            {
                                int cp;
                                if (int.TryParse(s.Substring(i, 4), NumberStyles.HexNumber,
                                                 CultureInfo.InvariantCulture, out cp))
                                    sb.Append((char)cp);
                                i += 4;
                            }
                            break;
                        default: sb.Append(e); break;
                    }
                }
                return sb.ToString();
            }

            private static void SkipWs(string s, ref int i)
            {
                while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
            }
        }

        // ==================================================================
        // 首次播种：把系统收藏夹里的东西抄一份进来
        // ==================================================================

        private static bool SeedFromLinks()
        {
            try
            {
                string dir = LegacyLinksDir;
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;

                FavNode bar = new FavNode();
                bar.Name = "收藏夹栏";
                bar.Bar = true;

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

                    string t = NormalizePath(ResolveShortcut(raw));
                    if (string.IsNullOrEmpty(t)) continue;
                    if (FindItem(bar.Kids, t) != null) continue;
                    FavNode it = new FavNode();
                    it.Path = t;
                    bar.Kids.Add(it);
                }

                if (bar.Kids.Count == 0) return false;
                roots.Add(bar);
                return true;
            }
            catch (Exception ex)
            {
                Diag.Log("收藏夹: 播种失败 " + ex.Message);
                return false;
            }
        }

        // ==================================================================
        // .lnk → 真实目标（只在「从系统收藏夹播种」时用得上）
        //
        // **不走 COM**（`IShellLink` 要自己声明 vtable，本机没法调试，接错就是进程级崩溃）：
        // `.lnk` 的 `LinkInfo` 段里存着目标的本地路径字符串（ANSI 和/或 UTF-16），
        // 直接扫字节把它捞出来、再用 `Directory.Exists` 验一下，验不过就当解析失败。
        // 扫不到就**原样返回 `.lnk` 路径** —— 双点/点开时 shell 自己会解析，绝不猜、绝不返回空。
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
