using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

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

        // ---- 整棵窗口树重画（换颜色模式后把残留像素刷掉；见 Theme.StyleShellTree）----
        public const uint WM_THEMECHANGED = 0x031A;
        public const uint RDW_INVALIDATE = 0x0001;
        public const uint RDW_ERASE = 0x0004;
        public const uint RDW_FRAME = 0x0400;
        public const uint RDW_ALLCHILDREN = 0x0080;

        /// <summary>
        /// 让窗口（含整棵子树）重画。
        /// **故意不带 RDW_UPDATENOW** —— 那会同步等对方进程走完 WM_PAINT，
        /// 嵌进来的 explorer 是别人的进程，忙的时候会把我们卡住。
        /// 只发「失效」通知，让它自己挑时间画，够用了。
        /// </summary>
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

        // ---- 非激活标签省内存（见 ExplorerHost.TrimMemory）----
        public const uint PROCESS_QUERY_INFORMATION = 0x0400;
        public const uint PROCESS_SET_QUOTA = 0x0100;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr hObject);

        /// <summary>把一个进程的驻留页尽量换出去（psapi）。不动进程本身，只是让它按需再缺页读回。</summary>
        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EmptyWorkingSet(IntPtr hProcess);

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

        // ---- 「属性」（Alt+Enter；走 explorer 自己那条右键菜单，见 ShellVerbs.InvokeProperties）----
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

        [DllImport("shell32.dll")]
        public static extern int SHBindToParent(IntPtr pidl, ref Guid riid, out IntPtr ppv, out IntPtr ppidlLast);

        [DllImport("shell32.dll")]
        public static extern IntPtr ILClone(IntPtr pidl);

        [DllImport("shell32.dll")]
        public static extern bool ILRemoveLastID(IntPtr pidl);

        [DllImport("shell32.dll")]
        public static extern bool ILIsEqual(IntPtr pidl1, IntPtr pidl2);

        [DllImport("shell32.dll")]
        public static extern void ILFree(IntPtr pidl);

        // ---- 属性表那条要用：`QueryContextMenu` 得先在一个**真实菜单**上跑一遍（见 `ShellVerbs`）----
        [DllImport("user32.dll")]
        public static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyMenu(IntPtr hMenu);

        // ---- 「打开某对象的属性」：shell 自己的**直通**入口，不走右键菜单那条（见 `ShellVerbs.RunProperties`）----
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SHObjectProperties(IntPtr hwnd, uint shopObjectType,
                                                     string pszObjectName, string pszPropertyPage);

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

    /// <summary>
    /// `IContextMenu::InvokeCommand` 的参数（`CMINVOKECOMMANDINFOEX`，x64 下 104 字节）。
    ///
    /// ⚠ 字符串字段一律用 `IntPtr` 自己分配/释放：这个结构里**既有 ANSI 又有宽字符**字段
    ///   （`lpVerb` 是 `LPCSTR`、`lpVerbW` 是 `LPCWSTR`），用 `string` + `CharSet` 会让两半打架。
    /// ⚠ `cbSize` 必须是**本结构的大小**（shell 会拿它当版本号判），不能写成 `CMINVOKECOMMANDINFO` 的大小。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;           // LPCSTR
        public IntPtr lpParameters;     // LPCSTR
        public IntPtr lpDirectory;      // LPCSTR
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr lpTitle;          // LPCSTR
        public IntPtr lpVerbW;          // LPCWSTR
        public IntPtr lpParametersW;    // LPCWSTR
        public IntPtr lpDirectoryW;     // LPCWSTR
        public IntPtr lpTitleW;         // LPCWSTR
        public int ptInvokeX;
        public int ptInvokeY;
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

        // ==================================================================
        // 「属性」（Alt+Enter —— 内嵌窗口里 explorer 自己那个组合是哑的，见 `WinEHook` / `DesktopHub`）
        // ==================================================================

        private static readonly Guid IID_IShellFolder = new Guid("000214E6-0000-0000-C000-000000000046");
        private static readonly Guid IID_IContextMenu = new Guid("000214E4-0000-0000-C000-000000000046");

        /// <summary>`CMIC_MASK_UNICODE` —— 告诉 shell「认 `lpVerbW`，别认 `lpVerb`」。</summary>
        private const uint CMIC_MASK_UNICODE = 0x00004000;

        /// <summary>`CMF_NORMAL` —— `QueryContextMenu` 的常规旗标（0）。</summary>
        private const uint CMF_NORMAL = 0x00000000;

        /// <summary>`SHObjectProperties` 的 `shopObjectType`：`pszObjectName` 是文件系统路径。</summary>
        private const uint SHOP_FILEPATH = 2;

        /// <summary>`WM_COMMAND`。</summary>
        private const uint WM_COMMAND = 0x0111;

        /// <summary>
        /// `FCIDM_SHVIEW_PROPERTIES`（`shlobj.h`）—— shell 视图自己的「属性」命令号。
        /// `FCIDM_SHVIEWFIRST=0x7000` 起算；我们实测 `GetCommandString` 里 `properties` 的偏移正好是 19
        /// （`0x7013-0x7000`），与 `delete=0x7011`、`cut=0x7018`、`copy=0x7019` 一致。
        /// </summary>
        private const int FCIDM_SHVIEW_PROPERTIES = 0x7013;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetUIObjectOfFn(IntPtr psf, IntPtr hwndOwner, uint cidl, IntPtr apidl,
                                             ref Guid riid, IntPtr rgfReserved, out IntPtr ppv);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int QueryContextMenuFn(IntPtr pcm, IntPtr hmenu, uint indexMenu,
                                               uint idCmdFirst, uint idCmdLast, uint uFlags);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int InvokeCommandFn(IntPtr pcm, ref CMINVOKECOMMANDINFOEX pici);

        /// <summary>这个 COM 接口的第 index 个方法地址（0/1/2 = QueryInterface/AddRef/Release）。</summary>
        private static IntPtr VtblSlot(IntPtr pUnk, int index)
        {
            IntPtr vtbl = Marshal.ReadIntPtr(pUnk);
            return Marshal.ReadIntPtr(vtbl, index * IntPtr.Size);
        }

        /// <summary>
        /// 叫出系统那张属性表（= 右键 → 属性）。
        ///
        /// ⭐ **首选是让内嵌那扇真 DefView 自己执行**（`NativeProperties`）：单/多选都对，框在 explorer.exe 里弹，
        ///   我们进程不用接管模态循环。下面那条「自己拼属性表」的路只当兜底。
        ///
        /// ⚠ 兜底那条**必须另起一条 STA 线程**：属性表是**在我们进程里**弹的模态框（shell32 自己那套），
        ///   在 UI 线程上直接调 = 我们的消息循环被它的模态循环接管，框没关之前标签页全停在那儿
        ///   （看着像“点了没反应”）。扔给一条一次性 STA 线程之后 UI 一切照常。
        /// 返回 true = 活儿接下了（框成功没成功是它自己的事，日志里看）。
        /// </summary>
        public static bool InvokeProperties(List<string> paths, IntPtr owner)
        {
            if (paths == null || paths.Count == 0) return false;

            // ⭐ 原生命令：把这扇窗口里那扇真 DefView 的命令走一遍，行为跟右键「属性」完全一致。
            if (owner != IntPtr.Zero && NativeProperties(owner)) return true;

            List<string> copy = new List<string>(paths);
            IntPtr own = owner;
            try
            {
                Thread t = new Thread(delegate() { RunProperties(copy, own); });
                t.IsBackground = true;
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
                return true;
            }
            catch (Exception ex) { Diag.Log("属性: 起线程失败 " + ex.Message); return false; }
        }

        /// <summary>
        /// 让**内嵌那扇真 DefView 自己**执行原生「属性」：往它那儿投 `WM_COMMAND` + `FCIDM_SHVIEW_PROPERTIES`。
        ///
        /// ✅ 2026-10-02 台架实测（`props_live_probe.py`，目标就是被收回我们程序里的那扇 DefView）：
        ///   直接发 → **真属性表**；视图里全选后再发 → **合并的多选属性表**（标题「MuMu共享文件夹, ... 属性」）。
        ///   桌面那扇 DefView 同样有效（`props_defview_probe.py`）。⇒ **单/多选都通**，
        ///   而且框是在 explorer.exe 里弹的 ⇒ 我们进程不接管模态循环、不用另起线程。
        ///
        /// ⚠ 这才是「原生的调用」：不自己拼属性表，只把命令递给那扇**有真 site** 的 view。
        ///   自己拼那条路（`ContextMenuProperties`）在这台机器上必弹坏框，详见它的注释。
        /// ⚠ 消息是 `PostMessage` 过去的（跨进程只带一个整数，安全）；找不到 view 就返回 false 让上层兜底。
        /// </summary>
        public static bool NativeProperties(IntPtr ownerWindow)
        {
            try
            {
                IntPtr view = WinFind.ByClass(ownerWindow, "SHELLDLL_DefView");
                if (view == IntPtr.Zero) { Diag.Log("属性: 这扇窗口里找不到 SHELLDLL_DefView"); return false; }
                EmbedApi.PostMessageW(view, WM_COMMAND, (IntPtr)FCIDM_SHVIEW_PROPERTIES, IntPtr.Zero);
                Diag.Step("属性: 让内嵌 DefView 自己执行原生属性（WM_COMMAND 0x7013）");
                return true;
            }
            catch (Exception ex) { Diag.Log("属性: 投原生命令异常 " + ex.Message); return false; }
        }

        private static void RunProperties(List<string> paths, IntPtr owner)
        {
            try { Application.OleRequired(); } catch { }        // 这条线程上的 shell 调用要 COM 先备好
            try
            {
                // ⭐ 单选：走 shell 自己那条**直通**入口。2026-10-02 台架实测（`props_shape_probe.py`）：
                //   `SHObjectProperties(0/hwnd, SHOP_FILEPATH, path, NULL)` → **真属性表**（完整的「常规」页）。
                //   而下面那条右键菜单路线**必出坏框**（标题「桌面」/「此项目的属性未知。」）。
                if (paths.Count == 1)
                {
                    if (NativeMethods.SHObjectProperties(owner, SHOP_FILEPATH, paths[0], null))
                    {
                        Diag.Step("属性: 走 SHObjectProperties（1 项），已交 shell");
                        return;
                    }
                    Diag.Log("属性: SHObjectProperties 返回 FALSE（err=" + Marshal.GetLastWin32Error()
                             + "），退回右键菜单那条");
                }

                if (ContextMenuProperties(paths, owner))
                {
                    Diag.Step(string.Format("属性: 走右键菜单那条，已交 shell（{0} 项）", paths.Count));
                    return;
                }
                Diag.Log("属性: 右键菜单那条没走通，退回只弹第一项");
                Invoke("properties", paths[0]);
            }
            catch (Exception ex) { Diag.Log("属性: " + ex.Message); }
        }

        /// <summary>
        /// 走 **explorer 自己那条右键菜单**：父目录 `IShellFolder::GetUIObjectOf(IID_IContextMenu)`
        /// 拿到「这批项」的上下文菜单，**先 `QueryContextMenu` 把动词表装好**（菜单不弹出来，
        /// 找个临时 `CreatePopupMenu` 让它跑一遍就行），再 `InvokeCommand("properties")`。
        ///
        /// ⛔ **这条路在这台机器上（Win10）出不来正常属性表** —— 一律弹标题「桌面」、正文
        ///   「此项目的属性未知。」的坏框（2026-10-02 台架实测，`props_site_probe2.py` / `props_itemarray_probe.py`）。
        ///   控件其实全对：父目录解析对（`data`）、数据对象里 `aoffset[0]` 指向对、动词表里 `properties`
        ///   明明在（第 19 条）。对照出来的结论是**路线本身**的问题，不是我们写错：
        ///     | 路线 | 结果 |
        ///     | 我们这条 / `IShellItemArray`→`IContextMenu`→`InvokeCommand` | ❌ 坏框 |
        ///     | `SHObjectProperties(hwnd, SHOP_FILEPATH, path, NULL)` | ✅ 真属性表 |
        ///     | `ShellExecuteEx("properties", SEE_MASK_INVOKEIDLIST)` | ✅ 真属性表 |
        ///     | `SHMultiFileProperties`（两种造数据对象写法、1 项/2 项） | ❌ 坏框 |
        ///   ⇒ **单选走 `SHObjectProperties`**（见 `RunProperties`），这条路只留给多选（当前没好办法）。
        ///   ⇒ 多选那条 `SHMultiFileProperties` 挂在 **shell 层面**，不是我们能绕的；「自己造 site」
        ///     （`CreateViewObject` + `IUnknown_SetSite`）**实测也不行**，别再往那个方向投时间。
        ///
        /// ⚠ 症状相同 ≠ 坏法相同：这条和我们早期那条错路弹的是同一个框，别照症状反推原因。
        ///
        /// ⚠ `SHBindToParent` 给的「相对子 PIDL」**就指向那个绝对 PIDL 内部**（探针实测偏移 =
        ///   父目录 PIDL 长度），所以绝对 PIDL 必须活到 `InvokeCommand` 返回。
        /// ⚠ 这几项必须同属一个父目录（同一列表里选出来的必然是；搜索结果那种跨目录的直接放弃，
        ///   否则会拿第一项的父目录去解其他项的子 PIDL ⇒ 解出一堆不存在的东西）。
        /// </summary>
        private static bool ContextMenuProperties(List<string> paths, IntPtr owner)
        {
            IntPtr[] abs = new IntPtr[paths.Count];
            IntPtr[] rel = new IntPtr[paths.Count];
            IntPtr psf = IntPtr.Zero, pcm = IntPtr.Zero, arr = IntPtr.Zero;
            IntPtr hmenu = IntPtr.Zero, verbA = IntPtr.Zero, verbW = IntPtr.Zero, parentRef = IntPtr.Zero;
            try
            {
                for (int i = 0; i < paths.Count; i++)
                {
                    uint eaten;
                    if (NativeMethods.SHParseDisplayName(paths[i], IntPtr.Zero, out abs[i], 0, out eaten) != 0
                        || abs[i] == IntPtr.Zero) return false;

                    Guid sf = IID_IShellFolder;
                    IntPtr one = IntPtr.Zero, child = IntPtr.Zero;
                    if (NativeMethods.SHBindToParent(abs[i], ref sf, out one, out child) != 0
                        || one == IntPtr.Zero || child == IntPtr.Zero) return false;
                    rel[i] = child;
                    if (i == 0) psf = one;                      // 第一项的父目录 IShellFolder 留用
                    else { try { Marshal.Release(one); } catch { } }   // 同一个目录，多的那份放手

                    // 父目录对不对得上：把这一项的绝对 PIDL 掐掉最后一段，跟第一项的父目录比
                    IntPtr p = NativeMethods.ILClone(abs[i]);
                    if (p == IntPtr.Zero) return false;
                    try
                    {
                        NativeMethods.ILRemoveLastID(p);
                        if (parentRef == IntPtr.Zero) { parentRef = p; p = IntPtr.Zero; }
                        else if (!NativeMethods.ILIsEqual(p, parentRef)) return false;   // 不在同一个父目录
                    }
                    finally { if (p != IntPtr.Zero) { try { NativeMethods.ILFree(p); } catch { } } }
                }

                // 相对子 PIDL 数组（delegate 不做数组封送，自己排一块内存）
                arr = Marshal.AllocHGlobal(IntPtr.Size * rel.Length);
                for (int i = 0; i < rel.Length; i++) Marshal.WriteIntPtr(arr, i * IntPtr.Size, rel[i]);

                GetUIObjectOfFn getObj = (GetUIObjectOfFn)Marshal.GetDelegateForFunctionPointer(
                    VtblSlot(psf, 10), typeof(GetUIObjectOfFn));       // IShellFolder 第 10 个 = GetUIObjectOf
                Guid icm = IID_IContextMenu;
                int hr = getObj(psf, owner, (uint)rel.Length, arr, ref icm, IntPtr.Zero, out pcm);
                if (hr != 0 || pcm == IntPtr.Zero)
                {
                    Diag.Log("属性: GetUIObjectOf(IContextMenu) hr=0x" + hr.ToString("X8"));
                    return false;
                }

                hmenu = NativeMethods.CreatePopupMenu();
                if (hmenu == IntPtr.Zero) { Diag.Log("属性: CreatePopupMenu 失败"); return false; }
                QueryContextMenuFn query = (QueryContextMenuFn)Marshal.GetDelegateForFunctionPointer(
                    VtblSlot(pcm, 3), typeof(QueryContextMenuFn));       // IContextMenu 第 3 个 = QueryContextMenu
                int hrQ = query(pcm, hmenu, 0, 1, 0x7FFF, CMF_NORMAL);
                if (hrQ < 0)
                {
                    Diag.Log("属性: QueryContextMenu hr=0x" + hrQ.ToString("X8"));
                    return false;
                }

                verbA = Marshal.StringToHGlobalAnsi("properties");
                verbW = Marshal.StringToHGlobalUni("properties");
                CMINVOKECOMMANDINFOEX ci = new CMINVOKECOMMANDINFOEX();
                ci.cbSize = Marshal.SizeOf(typeof(CMINVOKECOMMANDINFOEX));
                ci.fMask = CMIC_MASK_UNICODE;
                ci.hwnd = owner;
                ci.lpVerb = verbA;
                ci.lpVerbW = verbW;
                ci.nShow = 1;                                        // SW_SHOWNORMAL
                InvokeCommandFn invoke = (InvokeCommandFn)Marshal.GetDelegateForFunctionPointer(
                    VtblSlot(pcm, 4), typeof(InvokeCommandFn));       // IContextMenu 第 4 个 = InvokeCommand
                hr = invoke(pcm, ref ci);
                if (hr != 0)
                {
                    Diag.Log("属性: InvokeCommand(properties) hr=0x" + hr.ToString("X8"));
                    return false;
                }
                return true;
            }
            catch (Exception ex) { Diag.Log("属性: 右键菜单那条异常 " + ex.Message); return false; }
            finally
            {
                if (hmenu != IntPtr.Zero) { try { NativeMethods.DestroyMenu(hmenu); } catch { } }
                if (verbA != IntPtr.Zero) { try { Marshal.FreeHGlobal(verbA); } catch { } }
                if (verbW != IntPtr.Zero) { try { Marshal.FreeHGlobal(verbW); } catch { } }
                if (arr != IntPtr.Zero) { try { Marshal.FreeHGlobal(arr); } catch { } }
                if (pcm != IntPtr.Zero) { try { Marshal.Release(pcm); } catch { } }
                if (psf != IntPtr.Zero) { try { Marshal.Release(psf); } catch { } }
                if (parentRef != IntPtr.Zero) { try { NativeMethods.ILFree(parentRef); } catch { } }
                for (int i = 0; i < abs.Length; i++)
                    if (abs[i] != IntPtr.Zero) { try { NativeMethods.ILFree(abs[i]); } catch { } }
            }
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
