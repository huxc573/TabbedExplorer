using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;

namespace TabbedExplorer
{
    /// <summary>
    /// 一条可自定义的快捷键：命令 → 组合键。
    ///
    /// 解析**只做一次**（`Hotkeys.Reload` 时把文本拆成 vk + 三个修饰位），
    /// 之后键盘钩子线程上只拿整数比一比 —— 钩子回调里不能分配内存、不能做字符串活（见 WinEHook 注释）。
    /// </summary>
    internal sealed class HotkeySpec
    {
        /// <summary>命令标识（settings.json 里 `hotkey_<cmd>` 的后半段）。</summary>
        public string Cmd;
        /// <summary>设置窗口里显示的名字。</summary>
        public string Label;
        /// <summary>默认组合键文本。</summary>
        public string Dflt;
        /// <summary>设置文件里现在写的原文（显示用；解析失败时也给用户看，好知道哪儿写错了）。</summary>
        public string Raw;
        /// <summary>解析成功没。false = 这一条**不生效**（钩子会跳过它）。</summary>
        public bool Valid;
        /// <summary>状态位图还是分开存：钩子里比整数最快，也不会有装箱。</summary>
        public int Vk;
        public bool Ctrl, Shift, Alt;
    }

    /// <summary>
    /// 程序自有快捷键的解析 / 规范化 / 匹配。
    ///
    /// 覆盖的命令就是 `Settings.HotkeyKeys` 那 7 条（新建/关闭/前后标签/历史/恢复/书签栏）；
    /// **Ctrl+1..9 跳第 N 个标签是写死的**（没法绑「一串」键，见 WinEHook.Handle），文档里也是这么写的。
    ///
    /// 文本格式：`Ctrl+Shift+T`、`Alt+F4`、`Ctrl+Tab`、`F2`、`Ctrl+Alt+Delete`……
    ///   修饰键只认 Ctrl / Shift / Alt（Win 不作为修饰键 —— Win+E 是硬接管，不参与这套）；
    ///   键名认 A-Z、0-9、F1-F24 和 Tab / Space / Enter / Back / Delete / Insert / Home / End /
    ///   PageUp / PageDown / 方向键。认不出来就算无效（不生效，但设置窗口里能看到是什么文本）。
    /// </summary>
    internal static class Hotkeys
    {
        /// <summary>解析好的 7 条（`Reload` 之后整体换掉；钩子线程读的是**同一个引用**，换的是数组不是元素）。</summary>
        private static volatile HotkeySpec[] specs = Build();

        public static HotkeySpec[] Specs { get { return specs; } }

        /// <summary>照着设置文件重新解析一遍（设置窗口改完 / 启动时各调一次）。</summary>
        public static void Reload()
        {
            specs = Build();
            Diag.Step("快捷键: " + Describe());
        }

        private static HotkeySpec[] Build()
        {
            HotkeySpec[] r = new HotkeySpec[Settings.HotkeyKeys.Length];
            for (int i = 0; i < r.Length; i++)
            {
                HotkeySpec s = new HotkeySpec();
                s.Cmd = Settings.HotkeyKeys[i];
                s.Dflt = Settings.HotkeyDefaults[i];
                s.Label = LabelOf(s.Cmd);
                s.Raw = Settings.Hotkey(s.Cmd);
                Apply(s, s.Raw);
                r[i] = s;
            }
            return r;
        }

        /// <summary>把「Ctrl+Shift+T」这种文本解析进 spec（解析失败就只把 Raw 留着，Valid=false）。</summary>
        private static void Apply(HotkeySpec s, string text)
        {
            s.Valid = false; s.Vk = 0; s.Ctrl = s.Shift = s.Alt = false;
            if (s == null || string.IsNullOrEmpty(text)) return;

            string[] parts = text.Split('+');
            int vk = 0;
            bool ctrl = false, shift = false, alt = false;
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim();
                if (p.Length == 0) return;
                string low = p.ToLowerInvariant();
                if (low == "ctrl" || low == "control") { ctrl = true; continue; }
                if (low == "shift") { shift = true; continue; }
                if (low == "alt" || low == "menu") { alt = true; continue; }
                if (vk != 0) return;                       // 两个主键 → 非法
                vk = KeyOf(p);
                if (vk == 0) return;                       // 不认识的键名 → 非法
            }
            // 光一个键、或者只有修饰键：会挡住正常打字，不接受
            if (vk == 0 || (!ctrl && !shift && !alt)) return;
            s.Vk = vk; s.Ctrl = ctrl; s.Shift = shift; s.Alt = alt; s.Valid = true;
        }

        /// <summary>键名 → 虚拟键码（0 = 不认识）。</summary>
        private static int KeyOf(string p)
        {
            if (p.Length == 1)
            {
                char c = char.ToUpperInvariant(p[0]);
                if (c >= 'A' && c <= 'Z') return (int)c;
                if (c >= '0' && c <= '9') return (int)c;      // '0'=0x30 正好是 VK_0
                return 0;
            }
            string low = p.ToLowerInvariant();
            if (low.Length >= 2 && low[0] == 'f')
            {
                int n;
                if (int.TryParse(low.Substring(1), out n) && n >= 1 && n <= 24) return 0x6F + n;   // VK_F1 = 0x70
                return 0;
            }
            switch (low)
            {
                case "tab": return 0x09;
                case "space": case "空格": return 0x20;
                case "enter": case "return": case "回车": return 0x0D;
                case "back": case "backspace": return 0x08;
                case "delete": case "del": return 0x2E;
                case "insert": case "ins": return 0x2D;
                case "home": return 0x24;
                case "end": return 0x23;
                case "pageup": case "pgup": return 0x21;
                case "pagedown": case "pgdn": return 0x22;
                case "left": return 0x25;
                case "right": return 0x27;
                case "up": return 0x26;
                case "down": return 0x28;
            }
            return 0;
        }

        /// <summary>虚拟键码 → 显示名（规范化用，都写成大写）；认不出返回 ""。</summary>
        public static string NameOf(int vk)
        {
            if (vk >= 'A' && vk <= 'Z') return ((char)vk).ToString();
            if (vk >= '0' && vk <= '9') return ((char)vk).ToString();
            if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x6F);          // F1..F24
            switch (vk)
            {
                case 0x09: return "Tab";
                case 0x20: return "Space";
                case 0x0D: return "Enter";
                case 0x08: return "Back";
                case 0x2E: return "Delete";
                case 0x2D: return "Insert";
                case 0x24: return "Home";
                case 0x23: return "End";
                case 0x21: return "PageUp";
                case 0x22: return "PageDown";
                case 0x25: return "Left";
                case 0x27: return "Right";
                case 0x26: return "Up";
                case 0x28: return "Down";
            }
            return "";
        }

        /// <summary>拼成规范文本（`Ctrl+Shift+T`）。修饰键顺序固定 Ctrl → Shift → Alt。</summary>
        public static string Format(int vk, bool ctrl, bool shift, bool alt)
        {
            string k = NameOf(vk);
            if (k.Length == 0) return "";
            StringBuilder sb = new StringBuilder();
            if (ctrl) sb.Append("Ctrl+");
            if (shift) sb.Append("Shift+");
            if (alt) sb.Append("Alt+");
            sb.Append(k);
            return sb.ToString();
        }

        /// <summary>
        /// **钩子线程**上判定：这一组修饰键 + 这个 vk 命中哪条命令（null = 不是我们的，放行）。
        /// 只读数组 + 整数比较，不分配内存。
        /// </summary>
        public static string Match(int vk, bool ctrl, bool shift, bool alt)
        {
            HotkeySpec[] a = specs;
            if (a == null) return null;
            for (int i = 0; i < a.Length; i++)
            {
                HotkeySpec s = a[i];
                if (s == null || !s.Valid) continue;
                if (s.Vk != vk) continue;
                if (s.Ctrl != ctrl || s.Shift != shift || s.Alt != alt) continue;
                return s.Cmd;
            }
            return null;
        }

        /// <summary>这条命令现在绑的文本（显示用）。</summary>
        public static string Combo(string cmd)
        {
            HotkeySpec[] a = specs;
            if (a != null)
            {
                for (int i = 0; i < a.Length; i++)
                    if (a[i] != null && string.Equals(a[i].Cmd, cmd, StringComparison.OrdinalIgnoreCase))
                        return a[i].Valid ? Format(a[i].Vk, a[i].Ctrl, a[i].Shift, a[i].Alt) : a[i].Raw;
            }
            return Settings.Hotkey(cmd);
        }

        /// <summary>
        /// 改一条（设置窗口的按键捕获框调它）。文本非法 / 跟别的命令撞了 → 返回 false 并说明原因。
        /// 校验通过才写设置文件 + 重新解析。
        /// </summary>
        public static bool Assign(string cmd, int vk, bool ctrl, bool shift, bool alt, out string why)
        {
            why = null;
            string text = Format(vk, ctrl, shift, alt);
            if (text.Length == 0) { why = "这个键不能用来做快捷键。"; return false; }

            HotkeySpec probe = new HotkeySpec();
            Apply(probe, text);
            if (!probe.Valid) { why = "至少要按住 Ctrl 或 Alt（只按 Shift 不够，会跟打字打架）。"; return false; }

            HotkeySpec[] a = specs;
            if (a != null)
            {
                for (int i = 0; i < a.Length; i++)
                {
                    if (a[i] == null || string.Equals(a[i].Cmd, cmd, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!a[i].Valid) continue;
                    if (a[i].Vk == probe.Vk && a[i].Ctrl == probe.Ctrl &&
                        a[i].Shift == probe.Shift && a[i].Alt == probe.Alt)
                    {
                        why = "跟「" + a[i].Label + "」的 " + Format(a[i].Vk, a[i].Ctrl, a[i].Shift, a[i].Alt) + " 撞了。";
                        return false;
                    }
                }
            }

            Settings.SetHotkey(cmd, text);
            Reload();
            Diag.Step("快捷键: " + LabelOf(cmd) + " -> " + text);
            return true;
        }

        /// <summary>恢复默认（设置窗口那个按钮）。</summary>
        public static void ResetAll()
        {
            Settings.ResetHotkeys();
            Reload();
            Diag.Step("快捷键: 全部恢复默认");
        }

        public static string LabelOf(string cmd)
        {
            switch (cmd)
            {
                case "newtab": return "新建标签页";
                case "closetab": return "关闭标签页";
                case "nexttab": return "下一个标签页";
                case "prevtab": return "上一个标签页";
                case "history": return "历史记录";
                case "reopen": return "恢复关闭的标签页";
                case "favbar": return "显示/隐藏书签栏";
            }
            return cmd;
        }

        /// <summary>日志用（「快捷键: 新建标签页=Ctrl+T …」）。</summary>
        public static string Describe()
        {
            HotkeySpec[] a = specs;
            if (a == null) return "(空)";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < a.Length; i++)
            {
                if (i > 0) sb.Append(" ");
                sb.Append(LabelOf(a[i].Cmd)).Append("=");
                sb.Append(a[i].Valid ? Format(a[i].Vk, a[i].Ctrl, a[i].Shift, a[i].Alt)
                                     : (a[i].Raw ?? "") + "(无效)");
            }
            return sb.ToString();
        }

        /// <summary>
        /// 把一个 WinForms 按键事件翻成「命令」—— 给 `EmbedForm.OnPreviewKeyDown` 那条兜底路用
        /// （焦点在我们自己的控件上时，钩子那条虽然也拦得住，但两条路要用同一套绑定）。
        /// </summary>
        public static string MatchKeyData(Keys keyData)
        {
            Keys k = keyData & Keys.KeyCode;
            if (k == Keys.None) return null;
            bool ctrl = (keyData & Keys.Control) != 0;
            bool shift = (keyData & Keys.Shift) != 0;
            bool alt = (keyData & Keys.Alt) != 0;
            return Match((int)k, ctrl, shift, alt);
        }

        /// <summary>这个 Keys 是不是「纯修饰键」（捕获框里要忽略掉）。</summary>
        public static bool IsModifierKey(Keys k)
        {
            return k == Keys.ControlKey || k == Keys.ShiftKey || k == Keys.Menu ||
                   k == Keys.LControlKey || k == Keys.RControlKey ||
                   k == Keys.LShiftKey || k == Keys.RShiftKey ||
                   k == Keys.LMenu || k == Keys.RMenu ||
                   k == Keys.LWin || k == Keys.RWin;
        }

        /// <summary>捕获框里把 WinForms 的 Keys 翻成 (vk, ctrl, shift, alt)。</summary>
        public static void Split(Keys keyData, out int vk, out bool ctrl, out bool shift, out bool alt)
        {
            vk = (int)(keyData & Keys.KeyCode);
            ctrl = (keyData & Keys.Control) != 0;
            shift = (keyData & Keys.Shift) != 0;
            alt = (keyData & Keys.Alt) != 0;
        }
    }
}
