using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace TabbedExplorer
{
    // ==================================================================
    // 第二批 P/Invoke（主题、子窗口查找、按键派发、Shell 动词）
    // ==================================================================
    internal static partial class NativeMethods
    {
        // ---- 主题 ----
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        public static extern int SetWindowTheme(IntPtr hwnd, string pszSubAppName, string pszSubIdList);

        // ---- 窗口查找 ----
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetFocus();

        // ---- 抢前台（Win+E 恢复最小化窗口用）----
        [DllImport("user32.dll")]
        public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll")]
        public static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);

        /// <summary>系统 DPI（Win10 1607+）。自绘尺寸要按它放大。</summary>
        [DllImport("user32.dll")]
        public static extern uint GetDpiForSystem();

        // ---- 进程级深色模式用（uxtheme 的深色开关只有序数导出，见 DarkMode.cs）----
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr GetModuleHandleW(string lpModuleName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr LoadLibraryW(string lpFileName);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GetProcAddress(IntPtr hModule, IntPtr lpProcName);

        [DllImport("ntdll.dll")]
        public static extern int RtlGetVersion(ref RTL_OSVERSIONINFOEXW lpVersionInformation);

        // ---- 按键派发（由 shell 视图自己实现剪贴板/删除/重命名，行为最正宗）----
        [DllImport("user32.dll")]
        public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, IntPtr dwExtraInfo);

        // ---- 消息 ----
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        /// <summary>带字符串 lParam 的版本（EM_SETCUEBANNER 之类）。</summary>
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")]
        public static extern IntPtr SendMessageStr(IntPtr hWnd, uint Msg, IntPtr wParam, string lParam);

        // ---- Shell 动词 ----
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);

        [DllImport("shell32.dll")]
        public static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
    }

    internal delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string lpVerb;
        public string lpFile;
        public string lpParameters;
        public string lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        public string lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    internal static class ShellVerbs
    {
        public const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;
        public const uint SEE_MASK_FLAG_NO_UI = 0x00000400;
        public const uint SEE_MASK_NOCLOSEPROCESS = 0x00000040;

        /// <summary>对一个项目执行 shell 动词（print / SendTo / Burn / properties …）。</summary>
        public static bool Invoke(string verb, string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                SHELLEXECUTEINFO sei = new SHELLEXECUTEINFO();
                sei.cbSize = Marshal.SizeOf(typeof(SHELLEXECUTEINFO));
                sei.fMask = SEE_MASK_INVOKEIDLIST | SEE_MASK_FLAG_NO_UI;
                sei.lpVerb = verb;
                sei.lpFile = path;
                sei.nShow = 1;
                bool ok = NativeMethods.ShellExecuteEx(ref sei);
                if (!ok) Diag.Log("ShellExecuteEx 失败 verb=" + verb + " err=" + Marshal.GetLastWin32Error());
                return ok;
            }
            catch (Exception ex)
            {
                Diag.Log("ShellExecuteEx 异常: " + ex.Message);
                return false;
            }
        }

        /// <summary>刷新 Explorer 的全局文件夹视图（改过隐藏/扩展名之后用）。</summary>
        public static void NotifyAssocChanged()
        {
            try { NativeMethods.SHChangeNotify(0x08000000 /*SHCNE_ASSOCCHANGED*/, 0 /*SHCNF_IDLIST*/, IntPtr.Zero, IntPtr.Zero); }
            catch { }
        }
    }

    // ==================================================================
    // 在某个窗口下面找子窗口（ExplorerBrowser 里的 shell 视图/列头都靠它）
    // ==================================================================
    internal static class WinFind
    {
        public static string ClassOf(IntPtr h)
        {
            if (h == IntPtr.Zero) return "";
            StringBuilder sb = new StringBuilder(512);
            NativeMethods.GetClassName(h, sb, sb.Capacity);
            return sb.ToString();
        }

        /// <summary>在 parent 的所有后代里找第一个类名匹配（不区分大小写）的窗口。</summary>
        public static IntPtr ByClass(IntPtr parent, string cls)
        {
            if (parent == IntPtr.Zero) return IntPtr.Zero;
            IntPtr found = IntPtr.Zero;
            EnumChildProc cb = null;
            cb = delegate(IntPtr h, IntPtr p)
            {
                if (string.Compare(ClassOf(h), cls, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    found = h;
                    return false;
                }
                return true;
            };
            try { NativeMethods.EnumChildWindows(parent, cb, IntPtr.Zero); }
            catch { }
            GC.KeepAlive(cb);
            return found;
        }

        /// <summary>在多个候选类名里找第一个命中的。</summary>
        public static IntPtr ByClass(IntPtr parent, string[] classes)
        {
            for (int i = 0; i < classes.Length; i++)
            {
                IntPtr h = ByClass(parent, classes[i]);
                if (h != IntPtr.Zero) return h;
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// parent 下的全部后代窗口（EnumChildWindows 本身就是递归枚举所有后代的）。
        /// 给 shell 视图上深色主题要逐窗口调 AllowDarkModeForWindow，得先把它们全捞出来。
        /// </summary>
        public static List<IntPtr> All(IntPtr parent)
        {
            List<IntPtr> result = new List<IntPtr>();
            if (parent == IntPtr.Zero) return result;
            EnumChildProc cb = null;
            cb = delegate(IntPtr h, IntPtr p)
            {
                result.Add(h);
                return true;
            };
            try { NativeMethods.EnumChildWindows(parent, cb, IntPtr.Zero); }
            catch { }
            GC.KeepAlive(cb);
            return result;
        }

        /// <summary>把 parent 下的窗口类名树 dump 出来（诊断用）。</summary>
        public static string Dump(IntPtr parent, int max)
        {
            List<string> names = new List<string>();
            EnumChildProc cb = null;
            cb = delegate(IntPtr h, IntPtr p)
            {
                if (names.Count < max) names.Add(ClassOf(h));
                return true;
            };
            try { NativeMethods.EnumChildWindows(parent, cb, IntPtr.Zero); }
            catch { }
            GC.KeepAlive(cb);
            return string.Join(" | ", names.ToArray());
        }
    }
}
