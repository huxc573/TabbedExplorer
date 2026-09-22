using System;
using System.Runtime.InteropServices;

namespace TabbedExplorer
{
    /// <summary>
    /// Win10 的「进程级深色模式」开关。
    ///
    /// 为什么需要它：shell 视图（文件列表）本身是 shell 在自己的代码里创建的，
    /// 光对窗口调 SetWindowTheme("DarkMode_Explorer") 只能改到外框，
    /// 真正画列表的 DirectUIHWND 那一层仍然是白的 —— 表现就是
    /// 「外壳深、文件列表白」。要让它变深，必须让**进程**声明允许深色。
    ///
    /// 这套开关是 uxtheme.dll 的**未公开序数导出**（本机 19045 上已核实）：
    ///   #135 SetPreferredAppMode(1=AllowDark / 3=ForceLight)
    ///   #133 AllowDarkModeForWindow(hwnd, allow)
    ///   #104 RefreshImmersiveColorPolicyState()
    ///
    /// 两个坑：
    ///   1) #137 不是 Refresh，是 IsDarkModeAllowedForWindow(hwnd) —— 它要参数、有返回值，
    ///      当无参函数调会跳坏栈，进程直接消失。
    ///   2) #135 在 18362 之前叫 AllowDarkModeForApp(bool)，语义不同（18334 起被移除）；
    ///      Win11 23H2 之后又换了新入口（#145）。
    /// 序数调错 = AccessViolation（try/catch 拦不住、进程直接消失），所以先用 build 号
    /// 卡一道，只在我们验证过的这一段里绑定。
    /// </summary>
    internal static class DarkMode
    {
        private const int BuildMin = 18362;   // Win10 1903：#135 从此是 SetPreferredAppMode
        private const int BuildMax = 22621;   // Win11 22H2：再往后换了新入口

        private delegate int SetPreferredAppModeFn(int mode);
        private delegate bool AllowDarkModeForWindowFn(IntPtr hwnd, bool allow);
        private delegate void RefreshPolicyFn();

        private static SetPreferredAppModeFn setPreferredAppMode;
        private static AllowDarkModeForWindowFn allowDarkModeForWindow;
        private static RefreshPolicyFn refreshPolicy;
        private static bool inited;

        /// <summary>必须在创建任何窗口之前调用（进程级开关要早于窗口创建生效）。</summary>
        public static void Init()
        {
            if (inited) return;
            inited = true;
            try
            {
                int build = BuildNumber();
                Diag.Log("DarkMode: windows build=" + build);
                if (build < BuildMin || build > BuildMax)
                {
                    Diag.Log("DarkMode: build 不在已验证范围，跳过（宁可不变深，也别调错序数把进程干掉）");
                    return;
                }

                IntPtr ux = NativeMethods.GetModuleHandleW("uxtheme.dll");
                if (ux == IntPtr.Zero) ux = NativeMethods.LoadLibraryW("uxtheme.dll");
                if (ux == IntPtr.Zero)
                {
                    Diag.Log("DarkMode: uxtheme 加载失败");
                    return;
                }

                setPreferredAppMode = Bind<SetPreferredAppModeFn>(ux, 135);
                allowDarkModeForWindow = Bind<AllowDarkModeForWindowFn>(ux, 133);

                refreshPolicy = Bind<RefreshPolicyFn>(ux, 104);

                Diag.Log("DarkMode: 绑定结果 SetPreferredAppMode=" + (setPreferredAppMode != null)
                         + " AllowDarkModeForWindow=" + (allowDarkModeForWindow != null)
                         + " RefreshPolicy=" + (refreshPolicy != null));
            }
            catch (Exception ex)
            {
                Diag.Log("DarkMode: Init 异常 " + ex.Message);
            }
        }

        private static T Bind<T>(IntPtr module, int ordinal) where T : class
        {
            try
            {
                IntPtr p = NativeMethods.GetProcAddress(module, new IntPtr(ordinal));
                if (p == IntPtr.Zero) return null;
                return (T)(object)Marshal.GetDelegateForFunctionPointer(p, typeof(T));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>dark=true 走 AllowDark(1)，false 走 ForceLight(3)。</summary>
        public static void SetEnabled(bool dark)
        {
            try
            {
                if (setPreferredAppMode != null) setPreferredAppMode(dark ? 1 : 3);
                if (refreshPolicy != null) refreshPolicy();
            }
            catch
            {
            }
        }

        /// <summary>让某个窗口（含 shell 视图的子窗口）接受深色主题。</summary>
        public static void AllowWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                if (allowDarkModeForWindow != null) allowDarkModeForWindow(hwnd, true);
            }
            catch
            {
            }
        }

        private static int BuildNumber()
        {
            try
            {
                RTL_OSVERSIONINFOEXW v = new RTL_OSVERSIONINFOEXW();
                v.dwOSVersionInfoSize = Marshal.SizeOf(typeof(RTL_OSVERSIONINFOEXW));
                if (NativeMethods.RtlGetVersion(ref v) == 0) return v.dwBuildNumber;
            }
            catch
            {
            }
            return 0;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct RTL_OSVERSIONINFOEXW
    {
        public int dwOSVersionInfoSize;
        public int dwMajorVersion;
        public int dwMinorVersion;
        public int dwBuildNumber;
        public int dwPlatformId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szCSDVersion;
    }
}
