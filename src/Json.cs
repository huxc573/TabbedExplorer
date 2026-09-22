using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TabbedExplorer
{
    /// <summary>
    /// 极简 JSON 读写 —— 就为了「配置一律用 json」（用户定的）。
    ///
    /// 为什么手写而不是引 Newtonsoft：本机没有 NuGet、也不想为了几个平铺的键拉一个库进来。
    /// 我们**只写自己产出的那一小撮形状**：平铺键值 + 字符串数组 + 「对象数组」（每个对象里
    /// 又是几个平铺键值）。够用、好查、零依赖，能直接编译进 exe。
    ///
    /// 读的一侧刻意的「宽容」：读不到 / 格式不对一律返回默认值，绝不抛 ——
    /// 用户手改 json 改坏了，程序也照常起得来、只是那一项回到默认，不会「打不开」。
    ///
    /// ⚠ 只认**顶层**的 `"key":`（不做真正的嵌套解析）。我们的文件都是自己写的，
    /// 形状固定，扫一遍字符串就够了。
    /// </summary>
    internal static class Json
    {
        // ==================================================================
        // 取标量
        // ==================================================================

        /// <summary>取一个标量的原始文本：字符串返回**去引号且反转义**的内容，数字/true/false 原样。</summary>
        public static string Get(string text, string key)
        {
            int v = ValueAt(text, key);
            if (v < 0) return null;
            if (text[v] == '"') { int _; return ReadString(text, v, out _); }

            int k = v;
            while (k < text.Length && text[k] != ',' && text[k] != '}' &&
                   text[k] != ']' && text[k] != '\n' && text[k] != '\r') k++;
            string s = text.Substring(v, k - v).Trim();
            return s.Length == 0 ? null : s;
        }

        public static bool GetBool(string text, string key, bool dflt)
        {
            string v = Get(text, key);
            if (string.IsNullOrEmpty(v)) return dflt;
            v = v.Trim().Trim('"').ToLowerInvariant();
            if (v == "true" || v == "1" || v == "yes" || v == "on" || v == "开") return true;
            if (v == "false" || v == "0" || v == "no" || v == "off" || v == "关") return false;
            return dflt;
        }

        public static int GetInt(string text, string key, int dflt)
        {
            string v = Get(text, key);
            int n;
            if (!string.IsNullOrEmpty(v) && int.TryParse(v.Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out n)) return n;
            return dflt;
        }

        // ==================================================================
        // 取块（数组 / 对象）
        // ==================================================================

        /// <summary>
        /// 取 `"key"` 后面那个 `[...]` 或 `{...}` 的**完整文本（含最外那对括号）**。
        /// 用括号配平扫，字符串里的括号不算数。
        /// </summary>
        public static string GetBlock(string text, string key)
        {
            int v = ValueAt(text, key);
            if (v < 0) return null;
            char open = text[v];
            if (open != '[' && open != '{') return null;
            char close = (open == '[') ? ']' : '}';

            int depth = 0;
            for (int i = v; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '"') { int e; ReadString(text, i, out e); i = e - 1; continue; }
                if (c == open) depth++;
                else if (c == close)
                {
                    depth--;
                    if (depth == 0) return text.Substring(v, i - v + 1);
                }
            }
            return null;
        }

        /// <summary>`["a","b"]` → 那几个字符串（顶层，顺序保留）。</summary>
        public static List<string> Strings(string arrayBlock)
        {
            List<string> r = new List<string>();
            if (string.IsNullOrEmpty(arrayBlock)) return r;
            for (int i = 0; i < arrayBlock.Length; i++)
            {
                if (arrayBlock[i] != '"') continue;
                int end;
                string s = ReadString(arrayBlock, i, out end);
                r.Add(s);
                i = end - 1;
            }
            return r;
        }

        /// <summary>`[{...},{...}]` → 每个对象块的文本（顶层，顺序保留）。</summary>
        public static List<string> Objects(string arrayBlock)
        {
            List<string> r = new List<string>();
            if (string.IsNullOrEmpty(arrayBlock)) return r;
            int depth = 0, start = -1;
            for (int i = 0; i < arrayBlock.Length; i++)
            {
                char c = arrayBlock[i];
                if (c == '"') { int e; ReadString(arrayBlock, i, out e); i = e - 1; continue; }
                if (c == '{')
                {
                    if (depth == 0) start = i;
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0 && start >= 0)
                    {
                        r.Add(arrayBlock.Substring(start, i - start + 1));
                        start = -1;
                    }
                }
            }
            return r;
        }

        // ==================================================================
        // 写
        // ==================================================================

        /// <summary>把一段文字转成 JSON 字符串字面量（含两侧引号）。</summary>
        public static string Str(string s)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append('"');
            if (s != null)
            {
                for (int i = 0; i < s.Length; i++)
                {
                    char c = s[i];
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                            else sb.Append(c);
                            break;
                    }
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>`["a","b"]` 这种数组字面量。</summary>
        public static string Array(IEnumerable<string> items)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append('[');
            bool first = true;
            if (items != null)
            {
                foreach (string it in items)
                {
                    if (!first) sb.Append(", ");
                    sb.Append(Str(it));
                    first = false;
                }
            }
            sb.Append(']');
            return sb.ToString();
        }

        // ==================================================================
        // 内部
        // ==================================================================

        /// <summary>找到 `"key"` 之后那个值的起始下标（跳过冒号和空白）。找不到返回 -1。</summary>
        private static int ValueAt(string text, string key)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(key)) return -1;
            int i = text.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (i < 0) return -1;
            int colon = text.IndexOf(':', i + key.Length + 2);
            if (colon < 0) return -1;
            int j = colon + 1;
            while (j < text.Length && char.IsWhiteSpace(text[j])) j++;
            return (j < text.Length) ? j : -1;
        }

        /// <summary>从 `start`（必须是 `"`）读一个字符串，返回内容；end = 关闭引号之后的位置。</summary>
        private static string ReadString(string text, int start, out int end)
        {
            end = start + 1;
            StringBuilder sb = new StringBuilder();
            int i = start + 1;
            while (i < text.Length)
            {
                char c = text[i];
                if (c == '\\' && i + 1 < text.Length)
                {
                    char n = text[i + 1];
                    switch (n)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (i + 5 < text.Length)
                            {
                                int code;
                                if (int.TryParse(text.Substring(i + 2, 4), NumberStyles.HexNumber,
                                        CultureInfo.InvariantCulture, out code))
                                {
                                    sb.Append((char)code);
                                    i += 4;
                                }
                            }
                            break;
                        default: sb.Append(n); break;   // \" \\ \/ 都归这儿
                    }
                    i += 2;
                    continue;
                }
                if (c == '"') { end = i + 1; return sb.ToString(); }
                sb.Append(c);
                i++;
            }
            end = text.Length;
            return sb.ToString();
        }
    }
}
