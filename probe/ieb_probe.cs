// IExplorerBrowser 可行性探针 —— 独立进程、屏幕外窗口、绝不抢前台。
//
// 为什么要有它：
//   程序里那条「老路」（AppContext + MainForm + ExplorerView）就是 IExplorerBrowser 同进程宿主，
//   当年实测硬崩 0x80131506，三次都停在 `navigate: item ok` 之后 —— 也就是崩在
//   IExplorerBrowser::BrowseToObject 这一句里（见 src/Program.cs 的注释）。
//   但 Native.cs 里已经有「out object -> 裸 IntPtr」那条修复注释，说明「崩因」当年可能已经修了、
//   只是老路再没被验证过。这个探针就是把那段序列单独拿出来跑，看它到底还崩不崩。
//
// 怎么做到不打扰用户：
//   窗口建在 (-5000,-5000)，ShowWithoutActivation=true（激活都不激活），
//   全程不动光标、不抢前台、不发任何输入。
//
// 用法：
//   tools\probe_build.bat           编译（走计划任务，见 ENV 记事）
//   起：schtasks 临时任务跑 probe\ieb_probe.exe
//   读：probe\ieb_probe.out.txt
//
// 判读：
//   全绿 = 每一轮都打完 "round N: done"，且进程正常退出（EXIT=0）。
//   崩    = 日志停在某一行（那就是案发现场），后面没有对应的 "done"。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace IebProbe
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left, Top, Right, Bottom;
        public RECT(int l, int t, int r, int b) { Left = l; Top = t; Right = r; Bottom = b; }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FOLDERSETTINGS { public int ViewMode; public int Flags; }

    internal enum EBO
    {
        EBO_SHOWFRAMES = 0x2,
        EBO_ALWAYSNAVIGATE = 0x4
    }

    // 与 src/Native.cs 里的声明**逐字一致**（那份已经把 Initialize/SetOptions/SetRect/Advise 走通了），
    // 这样唯一的变量就是 BrowseToObject —— 探针要问的就是「崩不崩在它那里」。
    [ComImport]
    [Guid("DFD3B6B5-C10C-4BE9-85F6-A66969F402F6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IExplorerBrowser
    {
        [PreserveSig] int Initialize(IntPtr hwndParent, ref RECT prc, ref FOLDERSETTINGS pfs);
        [PreserveSig] int Destroy();
        [PreserveSig] int SetRect(ref IntPtr phdwp, RECT rcBrowser);
        [PreserveSig] int SetPropertyBag([MarshalAs(UnmanagedType.LPWStr)] string pszPropertyBag);
        [PreserveSig] int SetEmptyText([MarshalAs(UnmanagedType.LPWStr)] string pszEmptyText);
        [PreserveSig] int SetFolderSettings(FOLDERSETTINGS pfs);
        [PreserveSig] int Advise(IntPtr psbe, out uint pdwCookie);
        [PreserveSig] int Unadvise(uint dwCookie);
        [PreserveSig] int SetOptions(EBO dwFlag);
        [PreserveSig] int GetOptions(out EBO pdwFlag);
        [PreserveSig] int BrowseToIDList(IntPtr pidl, uint uFlags);
        [PreserveSig] int BrowseToObject(IntPtr punk, uint uFlags);
        [PreserveSig] int FillFromObject(IntPtr punk, int dwFlags);
        [PreserveSig] int RemoveAll();
        [PreserveSig] int GetCurrentView(ref Guid riid, out IntPtr ppv);
    }

    // 4 个方法一个都不能少（见 skill iexplorerbrowser-shell-host 的铁律）。
    [ComImport]
    [Guid("361bbdc7-e6ee-4e13-be58-58e2240c810f")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IExplorerBrowserEvents
    {
        [PreserveSig] int OnNavigationPending(IntPtr pidlFolder);
        [PreserveSig] int OnViewCreated(IntPtr psv);
        [PreserveSig] int OnNavigationComplete(IntPtr pidlFolder);
        [PreserveSig] int OnNavigationFailed(IntPtr pidlFolder);
    }

    [ComVisible(true)]
    internal sealed class Sink : IExplorerBrowserEvents
    {
        public int OnNavigationPending(IntPtr p) { Log("  [sink] OnNavigationPending"); return 0; }
        public int OnViewCreated(IntPtr p) { Log("  [sink] OnViewCreated"); return 0; }
        public int OnNavigationComplete(IntPtr p) { Log("  [sink] OnNavigationComplete"); return 0; }
        public int OnNavigationFailed(IntPtr p) { Log("  [sink] OnNavigationFailed"); return 0; }

        internal static void Log(string s) { Probe.Line(s); }
    }

    /// <summary>ShowWithoutActivation = 连激活都不激活，绝不抢前台。</summary>
    internal sealed class OffscreenHost : Form
    {
        public OffscreenHost()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(-5000, -5000, 900, 600);
            BackColor = Color.White;
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE
                return cp;
            }
        }
    }

    internal static class Probe
    {
        private static readonly List<string> lines = new List<string>();
        private static string outPath;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHCreateItemFromParsingName(
            string pszPath, IntPtr pbc, ref Guid riid, out IntPtr ppv);

        [DllImport("ole32.dll")]
        private static extern void CoTaskMemFree(IntPtr pv);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassNameW(IntPtr h, StringBuilder b, int n);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr h, uint cmd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr h);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        private const uint GW_CHILD = 5, GW_HWNDNEXT = 2;

        internal static void Line(string s)
        {
            string row = DateTime.Now.ToString("HH:mm:ss.fff") + " " + s;
            lines.Add(row);
            try { File.AppendAllText(outPath, row + Environment.NewLine, Encoding.UTF8); }
            catch { }
        }

        [STAThread]
        private static void Main()
        {
            string dir = Path.GetDirectoryName(typeof(Probe).Assembly.Location);
            outPath = Path.Combine(dir, "ieb_probe.out.txt");
            try { File.Delete(outPath); } catch { }

            try { SetProcessDpiAwarenessContext(new IntPtr(-2)); }
            catch { try { SetProcessDPIAware(); } catch { } }

            Line("=== IExplorerBrowser 探针开始 pid=" + System.Diagnostics.Process.GetCurrentProcess().Id + " ===");
            int exit = 0;
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                exit = Run();
            }
            catch (Exception ex)
            {
                Line("★ 托管异常（说明不是那段硬崩）: " + ex);
                exit = 3;
            }
            Line("=== EXIT=" + exit + " ===");
            Console.Out.Flush();
            Environment.Exit(exit);
        }

        private static int Run()
        {
            string[] targets = new string[]
            {
                @"D:\Dev\Workspaces\WorkBuddy\TabbedExplorer",
                @"C:\Windows",
                @"D:\Dev\Workspaces\WorkBuddy\TabbedExplorer\src",
                @"C:\Program Files",
                @"D:\Dev\Workspaces\WorkBuddy\TabbedExplorer\probe",
                @"C:\Windows\System32"
            };

            int rounds = targets.Length;
            for (int i = 0; i < rounds; i++)
            {
                Line("---- round " + (i + 1) + "/" + rounds + " target=" + targets[i] + " ----");
                OffscreenHost host = new OffscreenHost();
                host.CreateControl();
                IntPtr hwnd = host.Handle;
                host.Show();
                Line("  host shown hwnd=0x" + hwnd.ToString("X") + " visible=" + IsWindowVisible(hwnd));
                Application.DoEvents();

                OneRound(hwnd, targets[i]);

                try { host.Close(); host.Dispose(); } catch { }
                Application.DoEvents();
                Line("round " + (i + 1) + ": done（跑到这里说明没崩）");
            }
            return 0;
        }

        private static void OneRound(IntPtr hwnd, string target)
        {
            IExplorerBrowser browser = null;
            IntPtr sinkPtr = IntPtr.Zero;
            try
            {
                Type t = Type.GetTypeFromCLSID(new Guid("71F96385-DDD6-48D3-A0C1-AE06E8B055FB"));
                if (t == null) { Line("  CLSID_ExplorerBrowser 没注册"); return; }
                browser = (IExplorerBrowser)Activator.CreateInstance(t);
                Line("  coclass ok");

                RECT rc = new RECT(0, 0, 900, 600);
                FOLDERSETTINGS fs = new FOLDERSETTINGS();
                fs.ViewMode = 4;   // FVM_DETAILS
                int hr = browser.Initialize(hwnd, ref rc, ref fs);
                Line("  Initialize hr=0x" + hr.ToString("X8"));
                if (hr != 0) return;

                int opt = (int)(EBO.EBO_SHOWFRAMES | EBO.EBO_ALWAYSNAVIGATE);
                hr = browser.SetOptions((EBO)opt);
                Line("  SetOptions(0x" + opt.ToString("X") + ") hr=0x" + hr.ToString("X8"));

                IntPtr hdwp = IntPtr.Zero;
                RECT r2 = new RECT(0, 0, 900, 600);
                hr = browser.SetRect(ref hdwp, r2);
                Line("  SetRect hr=0x" + hr.ToString("X8"));

                Sink sink = new Sink();
                sinkPtr = Marshal.GetComInterfaceForObject(sink, typeof(IExplorerBrowserEvents));
                uint ck;
                hr = browser.Advise(sinkPtr, out ck);
                Line("  Advise hr=0x" + hr.ToString("X8") + " cookie=" + ck);

                Guid iid = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe");
                IntPtr item;
                hr = SHCreateItemFromParsingName(target, IntPtr.Zero, ref iid, out item);
                Line("  SHCreateItemFromParsingName hr=0x" + hr.ToString("X8") + " ptr=0x" + item.ToString("X"));

                if (hr == 0 && item != IntPtr.Zero)
                {
                    Line("  -> BrowseToObject 之前（下一行打不出来就是崩在这里）");
                    hr = browser.BrowseToObject(item, 0);
                    Line("  BrowseToObject hr=0x" + hr.ToString("X8"));
                    Marshal.Release(item);
                }
                else
                {
                    Line("  ★ item 没拿到，跳过 BrowseToObject（也就测不到崩点）");
                }

                // 多泵几轮消息，让 shell 把视图真建出来（frames 生不生效看类树）
                for (int k = 0; k < 12; k++) { Application.DoEvents(); System.Threading.Thread.Sleep(60); }

                Line("  类树: " + Dump(hwnd, 6));
                if (ck != 0) browser.Unadvise(ck);
                browser.Destroy();
            }
            catch (Exception ex)
            {
                Line("  ★ 托管异常: " + ex.Message);
            }
            finally
            {
                if (sinkPtr != IntPtr.Zero) { try { Marshal.Release(sinkPtr); } catch { } }
                if (browser != null) { try { Marshal.ReleaseComObject(browser); } catch { } }
            }
        }

        /// <summary>逐层 GetWindow(GW_CHILD/GW_HWNDNEXT) 打类名 —— 别用 EnumChildWindows（会重复列整棵子树）。</summary>
        private static string Dump(IntPtr root, int depth)
        {
            StringBuilder sb = new StringBuilder();
            Walk(root, depth, 0, sb);
            return sb.ToString();
        }

        private static void Walk(IntPtr parent, int maxDepth, int depth, StringBuilder sb)
        {
            if (depth >= maxDepth) return;
            IntPtr c = GetWindow(parent, GW_CHILD);
            while (c != IntPtr.Zero)
            {
                StringBuilder b = new StringBuilder(256);
                GetClassNameW(c, b, 256);
                sb.Append("  ".PadRight(depth * 2 + 1)).Append(b).Append('|');
                Walk(c, maxDepth, depth + 1, sb);
                c = GetWindow(c, GW_HWNDNEXT);
            }
        }
    }
}
