using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace TabbedExplorer
{
    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        /// <summary>带 --open 启动时，起来就把窗口显示出来（重装后直接看效果用，不必等 Win+E）。</summary>
        public static bool AutoOpen;

        /// <summary>
        /// 带 --tray 启动：**常驻后台、不显窗口**（开机自启走这条）。
        /// 起来只装托盘图标 + Win+E 钩子，窗口等第一次 Win+E 才出现。
        /// </summary>
        public static bool StartHidden;

        /// <summary>
        /// 「叫醒已经在跑的那个实例」用的命名事件。
        /// 第二个实例起来只会 Set 一下就走，窗口由第一个实例摆出来 —— 这就是双击 exe 能唤出窗口的原理。
        /// </summary>
        public static EventWaitHandle ShowSignal;

        /// <summary>
        /// 命令行 `--quit`：叫已经在跑的那个实例**真退出**（托盘「退出」的命令行版）。
        ///
        /// 为什么需要它：点 X / Ctrl+W 现在只是**收进托盘**，而且 `taskkill /PID`（不带 /F）
        /// 发的也是 WM_CLOSE，同样会被我们拦下来 —— 就再没有“优雅退出”的路了，
        /// 只能 /F 强杀，而强杀会把嵌进来的 explorer 子进程漏成孤儿。
        /// 所以留一条明确表示“这次是真退”的通道（重启换 exe 也走这条）。
        /// </summary>
        public static bool QuitRequested;

        public static string QuitSignalName
        {
            get { return EmbedMode ? "TabbedExplorer.Quit.v1" : "TabbedExplorer.Quit.classic.v1"; }
        }

        /// <summary>单实例互斥体的名字（embed 模式和老路各一份，互不干扰）。</summary>
        public static string MutexName
        {
            get { return EmbedMode ? "TabbedExplorer.Embed.v1" : "TabbedExplorer.SingleInstance.v1"; }
        }

        public static string SignalName
        {
            get { return EmbedMode ? "TabbedExplorer.Show.v1" : "TabbedExplorer.Show.classic.v1"; }
        }

        /// <summary>
        /// 给 IExplorerBrowser 加 EBO_SHOWFRAMES，让 shell 自己带出原生导航窗格 + 命令模块窗格。
        /// **默认开**（实测 Win10 19045 有效）：这样「快速访问 / 此电脑 / 网络」整棵树、卷标、
        /// 系统图标、右键菜单、视图切换按钮组都是 shell 原生的，不用我们硬编码目录、手绘图标。
        /// 加 --plain 可以关掉（退回只用一个视图对象，配自绘 NavPane，用于对比排查）。
        /// </summary>
        public static bool ShowFrames = true;

        /// <summary>
        /// 「嵌入真 explorer 窗口」模式（EmbedForm）：每个标签是一个真的 explorer 窗口，界面 100% 原生。
        ///
        /// **默认开**，裸启动 exe / run.bat 也走这条。原因是老路
        /// （AppContext + 自绘外壳 + IExplorerBrowser）实测会**硬崩**：
        /// .NET Runtime 事件 1023，退出码 0x80131506（COR_E_EXECUTIONENGINE，CLR 内部错误/执行引擎错误），
        /// 三次都崩在 CreateBrowser→navigate 之后、日志停在 `navigate: item ok`
        /// （09:56:52 / 10:11:19 / 10:20:08）—— 表现就是「程序崩了，我都没打开成功」。
        ///
        /// 要回去跑老路做对比排查，加 --classic。
        /// </summary>
        public static bool EmbedMode = true;

        [STAThread]
        private static void Main(string[] args)
        {
            // System-DPI 感知：让 WinForms 自己做缩放，避免界面发虚
            try
            {
                // -2 = DPI_AWARENESS_CONTEXT_SYSTEM_AWARE
                if (!SetProcessDpiAwarenessContext(new IntPtr(-2)))
                {
                    SetProcessDPIAware();
                }
            }
            catch
            {
                try { SetProcessDPIAware(); } catch { }
            }

            // 数据全部在**程序目录的 data\** 下（绿色便携，见 AppPaths）；
            // 第一次跑先把老位置（%APPDATA%）的设置/记忆搬过来，别让用户觉得「记忆丢了」。
            AppPaths.MigrateFromLegacy();

            // 上一轮日志攒得太大就先挪走一份（常年开着 Debug 时它会一直长）。
            // 必须在 `Settings.Load()` **之前** —— Load 会把日志闸按设置定稿（见 Diag.Enabled）。
            Diag.Rotate();

            Settings.Load();

            // 开机自启开着的话，把启动项里的 exe 路径刷成现在这个（程序目录被挪过也能对上）。
            // 没开就什么都不做 —— 绝不替用户打开。
            try { AutoStart.Sync(); } catch { }

            Theme.Mode = Settings.Color;   // 跟随系统 / 浅色 / 深色
            Theme.Reload();                // 先定下深浅色，后面所有自绘都按它来

            // 快捷键（可自定义，存在 settings.json 的 hotkey_*）—— 必须在**装钩子之前**解析好：
            // WinEHook 的判定是拿这张表比整数（见 Hotkeys）。
            try { Hotkeys.Reload(); } catch (Exception ex) { LogFatal("解析快捷键失败: " + ex.Message); }

            // 进程级深色必须在**创建任何窗口之前**声明：uxtheme 的 SetPreferredAppMode
            // 是进程级开关，窗口建好之后才设，shell 的文件列表那一层（DirectUIHWND）
            // 不会跟着变 —— 只改到外框，表现就是「外壳深、列表白」。
            DarkMode.Init();
            DarkMode.SetEnabled(Theme.IsDark);

            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--open", StringComparison.OrdinalIgnoreCase)) AutoOpen = true;
                if (string.Equals(args[i], "--frames", StringComparison.OrdinalIgnoreCase)) ShowFrames = true;
                if (string.Equals(args[i], "--plain", StringComparison.OrdinalIgnoreCase)) ShowFrames = false;
                if (string.Equals(args[i], "--embed", StringComparison.OrdinalIgnoreCase)) EmbedMode = true;
                if (string.Equals(args[i], "--classic", StringComparison.OrdinalIgnoreCase)) EmbedMode = false;
                if (string.Equals(args[i], "--tray", StringComparison.OrdinalIgnoreCase)) StartHidden = true;
                if (string.Equals(args[i], "--quit", StringComparison.OrdinalIgnoreCase)) QuitRequested = true;
            }
            if (AutoOpen) StartHidden = false;   // --open 想看效果，就别藏着

            // --quit：不启界面，只把“真退出”的信号发给已经在跑的那个，然后自己就退。
            // 放在判重之前 —— 它本来就不需要拿单实例锁。
            if (QuitRequested)
            {
                try
                {
                    using (EventWaitHandle q = new EventWaitHandle(false, EventResetMode.AutoReset, QuitSignalName))
                    {
                        q.Set();
                    }
                    LogFatal("--quit: 已发出退出信号（若没有实例在跑，这行是唯一痕迹）");
                }
                catch (Exception ex) { LogFatal("--quit: 发退出信号失败 " + ex.Message); }
                return;
            }

            bool createdNew;
            Mutex mutex = new Mutex(true, MutexName, out createdNew);

            // 先建好「叫醒事件」再判重：第二个实例要能立刻 Set 它。
            try
            {
                ShowSignal = new EventWaitHandle(false, EventResetMode.AutoReset, SignalName);
            }
            catch (Exception ex) { LogFatal("建唤醒事件失败: " + ex.Message); }

            if (!createdNew)
            {
                // 已经在跑了：把跑着的那个叫出来（它会新开一个标签），自己退出。
                Diag.Step("已有实例在跑，Set 唤醒事件后退出");
                if (ShowSignal != null) { try { ShowSignal.Set(); } catch { } }
                mutex.Close();
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 任何未捕获异常都写进 %APPDATA%\TabbedExplorer\log.txt，别让窗口一闪就没了
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
            {
                LogFatal("UI 线程异常: " + e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                LogFatal("未处理异常: " + e.ExceptionObject);
            };

            LogFatal("启动 pid=" + System.Diagnostics.Process.GetCurrentProcess().Id);

            if (EmbedMode)
            {
                // 常驻的是 **DesktopHub**（托盘 + Win+E 钩子），窗口按虚拟桌面按需新建：
                // 起来的时候一个窗口都没有，第一次 Win+E 才给「当前那张桌面」建一个。
                // 所以 --tray / --open 现在只是「起不起来就开窗」的差别（--open 用来看效果）。
                Diag.Step("DesktopHub 启动 --tray=" + StartHidden + " --open=" + AutoOpen);
                Application.Run(new DesktopHub());
            }
            else
            {
                Application.Run(new AppContext());
            }

            GC.KeepAlive(mutex);
        }

        private static void LogFatal(string s)
        {
            try
            {
                string dir = AppPaths.DataDir;
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(dir, "log.txt"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + s + Environment.NewLine);
            }
            catch { }
        }
    }
}
