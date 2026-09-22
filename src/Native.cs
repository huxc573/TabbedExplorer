using System;
using System.Runtime.InteropServices;
using System.Text;

namespace TabbedExplorer
{
    // ------------------------------------------------------------------
    // GUID
    // ------------------------------------------------------------------
    internal static class Guids
    {
        public const string CLSID_ExplorerBrowser = "71F96385-DDD6-48D3-A0C1-AE06E8B055FB";
        public const string IID_IExplorerBrowser = "DFD3B6B5-C10C-4BE9-85F6-A66969F402F6";
        public const string IID_IExplorerBrowserEvents = "361bbdc7-e6ee-4e13-be58-58e2240c810f";
        public const string IID_IServiceProvider = "6d5140c1-7436-11ce-8034-00aa006009fa";
        public const string IID_IShellItem = "43826d1e-e718-42ee-bc55-a1e261c37bfe";

        public const string CLSID_VirtualDesktopManager = "aa509086-5ca9-4c25-8f95-589d3c07b48a";
        public const string IID_IVirtualDesktopManager = "a5cd92ff-29be-454c-8d04-d82879fb3f1b";

        // 未公开的虚拟桌面接口（只用来问「当前是哪张桌面」—— 公开 API 根本没这个查询）。
        // 配方与 vtable 序号来自 MScholtes/VirtualDesktop（Win10 1809~22H2），已在本机实测跑通。
        public const string CLSID_ImmersiveShell = "c2f03a33-21f5-47fa-b4bb-156362a2f239";
        public const string CLSID_VirtualDesktopManagerInternal = "c5e0cdca-7b6e-41b2-9fc4-d93975cc467b";
        public const string IID_IVirtualDesktopManagerInternal = "f31574d6-b682-4cdc-bd56-1827860abec6";
        public const string IID_IVirtualDesktop = "ff72ffdd-be7e-43fc-9c03-ad81681e88e4";
    }

    // ------------------------------------------------------------------
    // 结构 / 枚举
    // ------------------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left, Top, Right, Bottom;
        public RECT(int l, int t, int r, int b) { Left = l; Top = t; Right = r; Bottom = b; }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FOLDERSETTINGS
    {
        public int ViewMode;
        public int Flags;
    }

    internal enum ExplorerBrowserOptions
    {
        EBO_DEFAULT = 0,
        EBO_NAVIGATEONCE = 0x00000001,
        EBO_SHOWFRAMES = 0x00000002,
        EBO_ALWAYSNAVIGATE = 0x00000004,
        EBO_NOTRAVELLOG = 0x00000008,
        EBO_NOWRAPPERWINDOW = 0x00000010,
        EBO_HIDEBROWSERTOOLBAR = 0x00000020,
        EBO_NOTOOLBAR = 0x00000040,
        EBO_NOPERSISTVIEWSTATE = 0x00000080
    }

    internal enum FolderViewMode { Details = 4 }

    internal enum FolderFlags
    {
        FWF_AUTOARRANGE = 0x00000001,
        FWF_SHOWSELALWAYS = 0x00000008
    }

    internal enum SIGDN : uint
    {
        NORMALDISPLAY = 0x00000000,
        DESKTOPABSOLUTEPARSING = 0x80028000,
        DESKTOPABSOLUTEEDITING = 0x8004c000,
        FILESYSPATH = 0x80058000,
        URL = 0x80068000
    }

    // ------------------------------------------------------------------
    // COM 接口
    // ------------------------------------------------------------------
    [ComImport]
    [Guid(Guids.IID_IExplorerBrowser)]
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
        [PreserveSig] int SetOptions(ExplorerBrowserOptions dwFlag);
        [PreserveSig] int GetOptions(out ExplorerBrowserOptions pdwFlag);
        [PreserveSig] int BrowseToIDList(IntPtr pidl, uint uFlags);
        [PreserveSig] int BrowseToObject(IntPtr punk, uint uFlags);
        [PreserveSig] int FillFromObject(IntPtr punk, int dwFlags);
        [PreserveSig] int RemoveAll();
        [PreserveSig] int GetCurrentView(ref Guid riid, out IntPtr ppv);
    }

    // 注意：**原生 IExplorerBrowserEvents 有 4 个方法，必须一个不漏地按顺序声明**。
    // 少声明一个，Marshal.GetComInterfaceForObject 生成的 CCW vtable 就短一截；
    // shell 导航时按原生 vtable 顺序回调，调到不存在的槽位 = 跳到野地址，
    // 进程当场以 0x80131506（CLR 执行引擎错误）死掉，且 try/catch 拦不住。
    // 症状：日志停在 BrowseToObject 之前那一行，下一行永远打不出来。
    [ComImport]
    [Guid(Guids.IID_IExplorerBrowserEvents)]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IExplorerBrowserEvents
    {
        [PreserveSig] int OnNavigationPending(IntPtr pidlFolder);
        [PreserveSig] int OnViewCreated(IntPtr psv);
        [PreserveSig] int OnNavigationComplete(IntPtr pidlFolder);
        [PreserveSig] int OnNavigationFailed(IntPtr pidlFolder);
    }

    [ComImport]
    [Guid(Guids.IID_IServiceProvider)]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IServiceProviderCom
    {
        [PreserveSig] int QueryService(ref Guid guidService, ref Guid riid, out IntPtr ppvObject);
    }

    [ComImport]
    [Guid(Guids.IID_IShellItem)]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IShellItem ppsi);
        [PreserveSig] int GetDisplayName(SIGDN sigdnName, out IntPtr ppszName);
        [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig] int Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport]
    [Guid(Guids.IID_IVirtualDesktopManager)]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IVirtualDesktopManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, out bool onCurrentDesktop);
        [PreserveSig] int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);
        [PreserveSig] int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
    }

    /// <summary>
    /// 未公开：拿「当前桌面」的那张 IVirtualDesktop。
    /// ⚠ 声明顺序 = vtable 顺序（IUnknown 占 0..2），**中间的方法一个都不能省**，
    /// 少写一个后面全体错位 → 调下去就是访问违例（不可 try/catch）。
    /// 序号：3 GetCount / 4 MoveViewToDesktop / 5 CanViewMoveDesktops / **6 GetCurrentDesktop**。
    /// 我们只调 6，其余三个纯粹为占坑而声明（永不调用）。
    /// </summary>
    [ComImport]
    [Guid(Guids.IID_IVirtualDesktopManagerInternal)]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IVirtualDesktopManagerInternal
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int MoveViewToDesktop(IntPtr view, IntPtr desktop);
        [PreserveSig] int CanViewMoveDesktops(IntPtr view);
        [PreserveSig] int GetCurrentDesktop(out IVirtualDesktop desktop);
    }

    /// <summary>未公开：桌面对象。序号：3 IsViewVisible / **4 GetId**。</summary>
    [ComImport]
    [Guid(Guids.IID_IVirtualDesktop)]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IVirtualDesktop
    {
        [PreserveSig] int IsViewVisible(IntPtr view, out bool visible);
        [PreserveSig] int GetId(out Guid id);
    }

    // ------------------------------------------------------------------
    // P/Invoke
    // ------------------------------------------------------------------
    internal static partial class NativeMethods
    {
        // 注意：**一律用 IntPtr（裸指针），不要用 out object**。
        // out object 会走 IDispatch 包装，把这个对象再传回 COM（BrowseToObject）时
        // CLR 可能给出与对象不匹配的接口指针 —— 实测直接 0x80131506（CLR 执行引擎错误）
        // 终止进程，且 try/catch 拦不住。裸指针 + 手工 Release 才安全。
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        public static extern void SHCreateItemFromParsingName(
            string pszPath, IntPtr pbc, [In] ref Guid riid, out IntPtr ppv);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SHGetNameFromIDList(IntPtr pidl, SIGDN sigdnName, out IntPtr ppszName);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SHAutoComplete(IntPtr hwndEdit, uint flags);

        [DllImport("ole32.dll")]
        public static extern void CoTaskMemFree(IntPtr pv);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern IntPtr GetShellWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern ushort RegisterClassW(ref WNDCLASS lpWndClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateWindowExW(
            uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
            int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern IntPtr GetModuleHandleW(IntPtr lpModuleName);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string lpszMenuName;
        public string lpszClassName;
    }

    internal delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
    internal delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    internal struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    // ------------------------------------------------------------------
    // 小工具
    // ------------------------------------------------------------------
    internal static class ShellUtil
    {
        public static readonly Guid IID_IShellItem = new Guid(Guids.IID_IShellItem);

        /// <summary>解析路径得到 IShellItem 的裸指针（用完必须 Marshal.Release）。</summary>
        public static IntPtr ItemPtrFromPath(string path)
        {
            IntPtr p;
            Guid iid = IID_IShellItem;
            NativeMethods.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out p);
            return p;
        }

        /// <summary>PIDL 转成可显示/可解析的名字；失败返回 null。</summary>
        public static string NameFromPidl(IntPtr pidl, SIGDN sigdn)
        {
            if (pidl == IntPtr.Zero) return null;
            IntPtr psz;
            int hr = NativeMethods.SHGetNameFromIDList(pidl, sigdn, out psz);
            if (hr != 0 || psz == IntPtr.Zero) return null;
            string s = Marshal.PtrToStringUni(psz);
            NativeMethods.CoTaskMemFree(psz);
            return s;
        }

        /// <summary>取 PIDL 的解析路径（优先文件系统路径，虚拟文件夹退回绝对解析名）。</summary>
        public static string ParsingPathFromPidl(IntPtr pidl)
        {
            string s = NameFromPidl(pidl, SIGDN.FILESYSPATH);
            if (string.IsNullOrEmpty(s)) s = NameFromPidl(pidl, SIGDN.DESKTOPABSOLUTEPARSING);
            return s;
        }

        /// <summary>取 PIDL 的显示名（标签标题用）。</summary>
        public static string DisplayNameFromPidl(IntPtr pidl)
        {
            string s = NameFromPidl(pidl, SIGDN.NORMALDISPLAY);
            if (string.IsNullOrEmpty(s)) s = ParsingPathFromPidl(pidl);
            return s;
        }
    }
}
