using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TabbedExplorer
{
    /// <summary>
    /// 让 shell 不再把我们收编的窗口当成「这个文件夹已经开着的那扇窗」。
    ///
    /// 要解决的问题（2026-09-24 实测定性）：三方程序点「打开文件夹」走的是 shell 自己的 open 动词，
    /// shell 先在自己的「浏览器窗口清单」（`ShellWindows`）里找有没有哪个窗口正开着这个文件夹：
    ///   · 找到 → **只激活那扇窗，不建新窗**。可那扇窗早被我们 `SetParent` 收编成标签了：
    ///     激活顶多落到宿主窗体上，而「到底是哪个标签」一个信号都没有 —— 实测 cab 的 WinEvent、
    ///     `WS_VISIBLE` 位、所属线程的 hwndActive/hwndFocus、同级 z 序，四条通道全空。
    ///     用户看到的就是「点了没反应、标签也不切过去」。
    ///   · 没找到 → 老老实实新建一扇 → 我们的钩子照常收编 → `OpenPathAsTab` 的去重（`IndexOfPath`）
    ///     就能把已有标签切过去。这条路一直是好的。
    /// 所以只要让 shell 永远匹配不到我们的窗，就统一并到那条好路上去。
    ///
    /// 手段：给这些窗口的 `IWebBrowser2` 写 `RegisterAsBrowser = TRUE`。名字看着是「登记」，
    /// 实测效果却是「此后不再被复用」：写完再点同一个文件夹，shell 会转而新建窗口
    /// （探针实录：`OBJECT_CREATE CabinetWClass` → 前台切到新窗 → 我们收编 → 切到已有标签。
    /// 同一台机器上不写的那一组：零新窗口、只有宿主窗被激活）。
    /// ⚠ **为什么会这样，官方文档没写**，这是 A/B 实测出来的行为。将来系统升级后若这条 bug 复发，
    ///   先拿 `probe/shellwin_drop.py` + 新窗口录像重测这一步，别急着怀疑别处。
    /// ⚠ 反方向（写 `RegisterAsBrowser = FALSE` 把它注销掉）**走不通**：explorer 直接回 `E_FAIL`，
    ///   清单纹丝不动。别回头试。
    ///
    /// 怎么认领「我们的窗」：清单里每一项的 `HWND` 是那扇浏览器窗的**顶层窗** —— 收编之后就变成
    /// 我们的宿主窗体（实测 7 个标签报回来的是同一个句柄）。所以按句柄分不出谁是谁，但
    /// 「这个句柄属不属于本进程」一问就准，而 shell 自己的窗属于 explorer 进程，正好不会被误伤。
    ///
    /// ⚠ 全程 try/catch 不外抛：摘不干净最多退回「三方点了没反应」，绝不能让收编本身失败。
    /// ⚠ 本工程不引 Microsoft.CSharp（用不了 `dynamic`），也不引 SHDocVw 互操作程序集，
    ///   这里手搓 IDispatch 调用；枚举也只能按 `Count` + `Item(i)` 来（实测 shell 不实现
    ///   `DISPID_NEWENUM`，走 `IEnumVARIANT` 会拿到空清单）。
    /// </summary>
    internal static class ShellBrowserReg
    {
        private static readonly Guid CLSID_ShellWindows = new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
        private static readonly int ownPid = Process.GetCurrentProcess().Id;

        /// <summary>上次全量登记的时间（见 <see cref="ExcludeOurs"/> 里的节流）。</summary>
        private static DateTime lastRun = DateTime.MinValue;
        private static readonly TimeSpan minInterval = TimeSpan.FromSeconds(2);

        /// <summary>
        /// 「这段时间内谁也别登记」的截止时刻（见 <see cref="KeepQuiet"/>）。
        ///
        /// 用**截止时刻**而不是一个布尔开关，是故意的：布尔一旦因为某条早退路径忘了复位，
        /// 登记就永远不再发生 —— 而「登记没做」的后果是记忆 25 那条（三方点打开文件夹
        /// 只激活不新建、看着像没反应）。时间戳自己会过期，坏不掉。
        /// </summary>
        private static DateTime quietUntil = DateTime.MinValue;

        /// <summary>
        /// 批量起标签 / 还原期间把登记挂起来（`ms` 毫秒之内不来）。
        ///
        /// 为什么要挂：这一步实测 **198~1365ms（均值约 660ms）**，而且只能在我们那一个 UI 线程上跑。
        /// 一次还原 8 个标签光它就 5 秒多，摊在每个标签收编那一刻 ⇒ 用户报的「重启程序之后，
        /// 激活那个标签已经好了，非激活还在加载时**整个程序几乎不可用**」就是它。
        /// 挂起来不做的代价只是「晚几秒登记上」，而收编之后本来就跟着 500ms 心跳兜底
        ///（见 `ExplorerHost.OnTitlePoll`）—— 心跳会在这一批结束后 2 秒内补上。
        /// </summary>
        public static void KeepQuiet(int ms)
        {
            quietUntil = DateTime.Now.AddMilliseconds(ms);
        }

        private const int DISPATCH_METHOD = 1;
        private const int DISPATCH_PROPERTYGET = 2;
        private const int DISPATCH_PROPERTYPUT = 4;
        private const int DISPID_PROPERTYPUT = -3;

        private const short VT_I4 = 3;                  // 32 位整数
        private const short VT_BOOL = 11;               // VARIANT_TRUE = -1
        private const short VT_VARIANT = 12;            // 「值是个 VARIANT」
        private const short VT_BYREF = 0x4000;          // 按引用传

        /// <summary>
        /// 把清单里**属于本进程**的窗口统统写一遍 `RegisterAsBrowser = TRUE`。
        ///
        /// 调用点给得很宽（收编时一次，之后跟着每个标签的 500ms 心跳走），所以这里**自己节流**：
        /// 2 秒内再来就直接返回。宁可多写几遍，是因为「登记什么时候会不作数」没弄明白：
        /// 窗口刚被收编那一刻句柄可能还没转过弯，标签换过目录之后先前那次也可能已经失效。
        /// `force` 留给收编那一刻 —— 新窗口得马上摘掉，等不了节流窗口。
        /// </summary>
        public static void ExcludeOurs(bool force = false)
        {
            // 正在批量起标签：这一批全程不做（理由见 KeepQuiet）。到期后心跳会补上。
            if (DateTime.Now < quietUntil) return;
            if (!force && DateTime.Now - lastRun < minInterval) return;
            lastRun = DateTime.Now;
            try
            {
                Application.OleRequired();      // 这个线程上没初始化过 OLE 的话，下面 CoCreateInstance 会失败
                Type t = Type.GetTypeFromCLSID(CLSID_ShellWindows);
                if (t == null) { Diag.Log("ShellReg: 找不到 ShellWindows 组件"); return; }
                IDispatch root = Activator.CreateInstance(t) as IDispatch;
                if (root == null) { Diag.Log("ShellReg: ShellWindows 不是 IDispatch"); return; }

                int total = ReadInt(root, "Count");
                if (total <= 0) { Diag.Log("ShellReg: 清单是空的（Count=" + total + "）"); return; }

                int mine = 0, done = 0;
                for (int i = 0; i < total; i++)
                {
                    IDispatch item = ReadItem(root, i);
                    if (item == null) continue;

                    IntPtr hwnd = ReadHwnd(item);
                    if (hwnd == IntPtr.Zero) continue;      // 取不到句柄的（比如桌面那项）不管
                    if (EmbedApi.ProcessIdOf(hwnd).ToInt32() != ownPid) continue;

                    mine++;
                    if (PutBool(item, "RegisterAsBrowser", true)) done++;
                }

                if (done != mine) Diag.Log(string.Format("ShellReg: 清单 {0} 项，本进程 {1} 项，登记成功 {2} 项", total, mine, done));
                else Diag.Step(string.Format("ShellReg: 清单 {0} 项，本进程 {1} 项已登记", total, mine));
            }
            catch (Exception ex) { Diag.Log("ShellReg: 失败 " + ex.Message); }
        }

        // ==================================================================
        // IDispatch 那点活儿：只有属性读一个整数 / 读 lstItem / 写一个布尔，够用就行
        // ==================================================================

        private static int ReadInt(IDispatch d, string name)
        {
            IntPtr v = Marshal.AllocCoTaskMem(VarSize);
            try
            {
                Zero(v, VarSize);
                object o = Invoke(d, name, DISPATCH_PROPERTYGET, v, IntPtr.Zero, 0);
                return o == null ? 0 : Convert.ToInt32(o);
            }
            finally { Marshal.FreeCoTaskMem(v); }
        }

        /// <summary>
        /// `Item(i)`。它的形参在自动化里是 `VARIANT`，按 OLE 的规矩得传 `VT_VARIANT|VT_BYREF`：
        /// 外面那层 VARIANT 的 vt 标成「按引用」，联合里放的是**真正那个** VARIANT 的地址。
        /// </summary>
        private static IDispatch ReadItem(IDispatch root, int index)
        {
            int id;
            if (!DispIdOf(root, "Item", out id)) return null;

            IntPtr inner = Marshal.AllocCoTaskMem(VarSize);
            IntPtr outer = Marshal.AllocCoTaskMem(VarSize);
            IntPtr result = Marshal.AllocCoTaskMem(VarSize);
            try
            {
                Zero(inner, VarSize);
                Marshal.WriteInt16(inner, 0, VT_I4);
                Marshal.WriteInt32(inner, 8, index);

                Zero(outer, VarSize);
                Marshal.WriteInt16(outer, 0, unchecked((short)(VT_VARIANT | VT_BYREF)));
                Marshal.WriteIntPtr(outer, 8, inner);

                Zero(result, VarSize);
                object o = Invoke(root, id, DISPATCH_METHOD, result, outer, 1);
                return o as IDispatch;
            }
            finally
            {
                Marshal.FreeCoTaskMem(inner);
                Marshal.FreeCoTaskMem(outer);
                Marshal.FreeCoTaskMem(result);
            }
        }

        /// <summary>读 `HWND`（VT_I8）。拿不到 / 是 0 都当「这项不关我们的事」。</summary>
        private static IntPtr ReadHwnd(IDispatch d)
        {
            IntPtr v = Marshal.AllocCoTaskMem(VarSize);
            try
            {
                Zero(v, VarSize);
                object o = Invoke(d, "HWND", DISPATCH_PROPERTYGET, v, IntPtr.Zero, 0);
                if (o == null) return IntPtr.Zero;
                long h = Convert.ToInt64(o);
                return h == 0 ? IntPtr.Zero : new IntPtr(h);
            }
            finally { Marshal.FreeCoTaskMem(v); }
        }

        private static bool PutBool(IDispatch d, string name, bool val)
        {
            int id;
            if (!DispIdOf(d, name, out id)) return false;

            IntPtr arg = Marshal.AllocCoTaskMem(VarSize);
            IntPtr named = Marshal.AllocCoTaskMem(4);
            try
            {
                Zero(arg, VarSize);
                Marshal.WriteInt16(arg, 0, VT_BOOL);
                Marshal.WriteInt16(arg, 8, (short)(val ? -1 : 0));      // VARIANT_TRUE 是 -1，不是 1
                Marshal.WriteInt32(named, 0, DISPID_PROPERTYPUT);       // 「这是属性赋值」的具名参数

                DISPPARAMS dp = new DISPPARAMS();
                dp.rgvarg = arg;
                dp.rgdispidNamedArgs = named;
                dp.cArgs = 1;
                dp.cNamedArgs = 1;
                Guid none = Guid.Empty;
                return d.Invoke(id, ref none, 0, DISPATCH_PROPERTYPUT, ref dp, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) == 0;
            }
            finally { Marshal.FreeCoTaskMem(arg); Marshal.FreeCoTaskMem(named); }
        }

        private static object Invoke(IDispatch d, string name, int flags, IntPtr result, IntPtr args, int nArgs)
        {
            int id;
            if (!DispIdOf(d, name, out id)) return null;
            return Invoke(d, id, flags, result, args, nArgs);
        }

        private static object Invoke(IDispatch d, int id, int flags, IntPtr result, IntPtr args, int nArgs)
        {
            DISPPARAMS dp = new DISPPARAMS();
            dp.rgvarg = args;
            dp.cArgs = nArgs;
            dp.rgdispidNamedArgs = IntPtr.Zero;
            dp.cNamedArgs = 0;
            Guid none = Guid.Empty;
            int hr = d.Invoke(id, ref none, 0, (ushort)flags, ref dp, result, IntPtr.Zero, IntPtr.Zero);
            if (hr != 0)
            {
                // 这一步失败没什么好补救的，但很值得留痕 —— 手搓 IDispatch 出问题时全靠这行看是哪个调用挂了
                Diag.Log(string.Format("ShellReg: Invoke(dispid={0} flags={1}) hr=0x{2:X8}", id, flags, hr));
                return null;
            }
            return Marshal.GetObjectForNativeVariant(result);
        }

        private static bool DispIdOf(IDispatch d, string name, out int id)
        {
            id = 0;
            Guid none = Guid.Empty;
            int[] ids = new int[1];
            int hr = d.GetIDsOfNames(ref none, new string[] { name }, 1, 0, ids);
            if (hr != 0)
            {
                Diag.Log(string.Format("ShellReg: 找不到 {0} hr=0x{1:X8}", name, hr));
                return false;
            }
            id = ids[0];
            return true;
        }

        /// <summary>VARIANT 的本机尺寸：x64 是 24 字节（2+6 头 + 16 联合），x86 是 16。</summary>
        private static int VarSize { get { return IntPtr.Size == 8 ? 24 : 16; } }

        private static void Zero(IntPtr p, int n)
        {
            for (int i = 0; i < n; i++) Marshal.WriteByte(p, i, 0);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPPARAMS
        {
            public IntPtr rgvarg;
            public IntPtr rgdispidNamedArgs;
            public int cArgs;
            public int cNamedArgs;
        }

        [ComImport, Guid("00020400-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDispatch
        {
            [PreserveSig] int GetTypeInfoCount(out uint pctinfo);
            [PreserveSig] int GetTypeInfo(uint iTInfo, uint lcid, out IntPtr ppTInfo);
            [PreserveSig] int GetIDsOfNames(ref Guid riid,
                [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] rgszNames,
                uint cNames, uint lcid, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] int[] rgDispId);
            [PreserveSig] int Invoke(int dispIdMember, ref Guid riid, uint lcid, ushort wFlags,
                ref DISPPARAMS pDispParams, IntPtr pVarResult, IntPtr pExcepInfo, IntPtr puArgErr);
        }
    }
}
