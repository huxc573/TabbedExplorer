using System;
using System.Collections.Generic;
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

        /// <summary>
        /// <see cref="PathOfWindow"/> 从清单**队尾**往前找几项。
        ///
        /// 为什么是队尾（实测 `probe/shellwindows_timing_probe.py`）：新开的浏览窗口是
        /// **追加在清单末尾**的 —— 触发 shell 开一个文件夹，`Count` 从 130 变 131，
        /// 第 130 项就是它，而且那一刻 `LocationURL` 已经有值、**窗口还没可见**。
        /// 从头扫一遍纯属白花：一次全量枚举 16~41ms，而这一问要在 25ms 一轮的盯梢里反复做。
        /// 留 8 项余量：还原标签那阵子我们自己的窗口也在往队尾追加，会把刚出现的 shell 窗口往后挤。
        /// </summary>
        private const int Lookback = 8;

        /// <summary>
        /// <see cref="SelectedPathOfWindow"/> 从清单队尾往前找几项。比 <see cref="Lookback"/> 宽，因为那 800ms 里
        /// 我们自己开标签的窗口也在往队尾追加，会把目标往后挤。只翻这么多项就是为了把「反复轮询」压到 2~4ms。
        /// </summary>
        private const int RevealLookback = 24;

        /// <summary>上次全量登记的时间（见 <see cref="ExcludeOurs"/> 里的节流）。</summary>
        private static DateTime lastRun = DateTime.MinValue;
        private static readonly TimeSpan minInterval = TimeSpan.FromSeconds(2);

        /// <summary>上一遍登记的数字 + 上一次写日志的时刻（见下面那行日志的节流）。</summary>
        private static int lastTotal = -1, lastMine = -1;
        private static DateTime lastLog = DateTime.MinValue;

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
        private const short VT_BSTR = 8;                // 宽字符串（BSTR）
        private const short VT_DISPATCH = 9;            // 一个 IDispatch*（`SelectItem` 要的 FolderItem）
        private const short VT_BOOL = 11;               // VARIANT_TRUE = -1
        private const short VT_VARIANT = 12;            // 「值是个 VARIANT」
        private const short VT_BYREF = 0x4000;          // 按引用传
        private const short VT_VARIANT_BYREF = unchecked((short)(VT_VARIANT | VT_BYREF));

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

                if (done != mine)
                {
                    Diag.Log(string.Format("ShellReg: 清单 {0} 项，本进程 {1} 项，登记成功 {2} 项", total, mine, done));
                    lastLog = DateTime.Now;
                }
                // ⚠ 这行原来**每 2 秒写一遍**（本函数挂在 500ms 心跳上、内部 2 秒节流），而清单那两百多项
                //   里绝大多数跟我们无关 —— 平时它只是把同样的数字刷进 log.txt，每行一次 open/write/close。
                //   改成「数字变了、或隔了半分钟」才写：真出事时照样看得到它在跑，平时不吵（也少一笔落盘）。
                else if (total != lastTotal || mine != lastMine || (DateTime.Now - lastLog).TotalSeconds >= 30)
                {
                    Diag.Step(string.Format("ShellReg: 清单 {0} 项，本进程 {1} 项已登记", total, mine));
                    lastLog = DateTime.Now;
                }
                lastTotal = total;
                lastMine = mine;
            }
            catch (Exception ex) { Diag.Log("ShellReg: 失败 " + ex.Message); }
        }

        /// <summary>
        /// 问 shell「你这扇窗口现在开着哪个文件夹」（`LocationURL`），按 HWND 对上号。
        ///
        /// 为什么要多这一条路：接管 shell 自己开的窗口时，地址栏那条（`EmbedApi.AddressPathOf`）
        /// 得**等窗口把地址栏建出来**——实测 shell 先把窗显示出来、地址栏才慢慢填好，那扇窗
        /// 因此在屏幕上实打实露半秒到一秒（用户报的「从开始菜单点文件夹还是会闪」）。
        /// 这条不问窗口本体、直接问 shell 的浏览器清单，建窗那一刻就有答案，于是能在它露脸
        /// **之前**就把 `SC_CLOSE` 发出去。**纯读，一个字节都不改它。**
        ///
        /// ⚠ 只能用于**我们没碰过的窗口**（shell 自己开的那些）：清单里 `HWND` 报的是浏览器窗的
        ///   顶层窗，被我们 `SetParent` 收编之后它会变成我们的宿主窗体——一张桌面上的标签全撞成
        ///   同一个 key（见 `ExplorerHost` 里那段）。shell 那条路我们从不 SetParent，句柄才对得上。
        /// ⚠ 回来还要过同一套筛子（`PathRules.Store` → `Restorable`）：`LocationURL` 对「此电脑」
        ///   这类虚拟位置给的不是 `file:///` 路径，直接交出去会让调用方「先关窗再开标签」两头空。
        ///
        /// ⚠ **每次都要重新 CoCreateInstance**，不能把一个根对象留着反复用：复用的那个对象
        ///   **看不见之后新加的项**（实测：留着用的那份一直只有旧项，每轮新建的立刻就能看到新窗）。
        ///   好在这一步本身只要 6ms 上下。
        /// ⚠ 会被**别的线程**调（`DesktopHub` 的 shell 盯梢线程），所以这里不碰任何可变静态字段。
        /// </summary>
        internal static string PathOfWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return null;
            try
            {
                Application.OleRequired();
                Type t = Type.GetTypeFromCLSID(CLSID_ShellWindows);
                if (t == null) return null;
                IDispatch root = Activator.CreateInstance(t) as IDispatch;
                if (root == null) return null;

                int total = ReadInt(root, "Count");
                // 从队尾往前找（理由见 Lookback）。
                for (int i = 0; i < Lookback && i < total; i++)
                {
                    IDispatch item = ReadItem(root, total - 1 - i);
                    if (item == null) continue;
                    if (ReadHwnd(item) != hwnd) continue;      // 不是这一扇
                    string p = FileUrlToPath(ReadString(item, "LocationURL"));
                    if (p == null) return null;
                    string stored = PathRules.Store(p);
                    return PathRules.Restorable(stored) ? stored : null;
                }
            }
            catch (Exception ex) { Diag.Log("ShellReg: 读窗口当前目录失败 " + ex.Message); }
            return null;
        }

        /// <summary>
        /// 在一扇窗**还是顶层窗**的时候，把它在 `ShellWindows` 里的那一项抓住，留着以后让它换目录
        /// （见 <see cref="NavigateTo"/>）。抓不到就返回 null（调用方下一轮再来）。
        ///
        /// 为什么要趁早抓：窗口被我们 `SetParent` 收编之后，清单报回来的 `HWND` 就变成**我们的宿主窗体**了
        /// —— 一张桌面上的标签全撞成同一个句柄，再也分不出谁是谁（类注释里那条）。只有「它还是顶层窗」
        /// 这一刻能用 `HWND` 对上号。
        ///
        /// ⚠ 新窗刚起来那阵子在清单里是**幽灵项**（`Item(i)` 回空指针），实测窗口出现后约 **+1 秒**
        ///   才读得到。所以调用方是「一轮试一次、过一会儿再来」，不是一次不成就算了。
        /// ⚠ 抓到的这一项**只能在同一个线程上接着用**（清单项是个 STA 对象，跨线程得自己封送）。
        ///   本工程里「抓」在收编那条链上（UI 线程）、「用」在 `EmbedForm.UseReserve`（也是 UI 线程），对得上。
        /// </summary>
        internal static object GrabWindowEntry(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return null;
            try
            {
                Application.OleRequired();
                Type t = Type.GetTypeFromCLSID(CLSID_ShellWindows);
                if (t == null) return null;
                IDispatch root = Activator.CreateInstance(t) as IDispatch;
                if (root == null) return null;

                int total = ReadInt(root, "Count");
                // 从队尾往前找（新窗追加在队尾，理由见 Lookback）
                for (int i = 0; i < Lookback && i < total; i++)
                {
                    IDispatch item = ReadItem(root, total - 1 - i);
                    if (item == null) continue;
                    if (ReadHwnd(item) != hwnd) continue;
                    return item;            // 留着 —— 以后 NavigateTo 直接拿它用
                }
            }
            catch (Exception ex) { Diag.Log("ShellReg: 抓窗口项失败 " + ex.Message); }
            return null;
        }

        /// <summary>
        /// 让 <see cref="GrabWindowEntry"/> 抓到的那扇窗换到 `path`（`IWebBrowser2::Navigate2`）。
        ///
        /// 用途：预热好的备用窗口本来停在「此电脑」上，用户点了别的文件夹（书签 / 历史 / 转生）时，
        /// 与其再起一个 explorer 进程（实测 0.24~1.28 秒），不如直接让它导航过去（实测 82~105ms）。
        ///
        /// 返回 true 只代表 **shell 收下了这个请求**（`hr=0`）：导航是异步的，内容稍后自己换。
        /// 调用方要判断「到位没有」，读 `LocationURL` 就是（见 <see cref="PathOfEntry"/>）。
        /// </summary>
        internal static bool NavigateTo(object target, string path)
        {
            IDispatch d = target as IDispatch;
            if (d == null || string.IsNullOrEmpty(path)) return false;

            int id;
            // `Navigate2` 优先（`Navigate` 是它的前身，参数是裸 BSTR）；两个都没有就作罢
            if (!DispIdOf(d, "Navigate2", out id) && !DispIdOf(d, "Navigate", out id)) return false;

            IntPtr inner = Marshal.AllocCoTaskMem(VarSize);
            IntPtr outer = Marshal.AllocCoTaskMem(VarSize);
            IntPtr url = IntPtr.Zero;
            try
            {
                // URL 形参在自动化里是个 `VARIANT`，按 OLE 的规矩要传 `VT_VARIANT|VT_BYREF`
                //（外层标「按引用」、联合里放真正那个 VARIANT 的地址）—— 跟 `Item(i)` 同一个套路。
                url = Marshal.StringToBSTR(path);
                Zero(inner, VarSize);
                Marshal.WriteInt16(inner, 0, VT_BSTR);
                Marshal.WriteIntPtr(inner, 8, url);

                Zero(outer, VarSize);
                Marshal.WriteInt16(outer, 0, VT_VARIANT_BYREF);
                Marshal.WriteIntPtr(outer, 8, inner);

                DISPPARAMS dp = new DISPPARAMS();
                dp.rgvarg = outer;
                dp.cArgs = 1;
                Guid none = Guid.Empty;
                int hr = d.Invoke(id, ref none, 0, DISPATCH_METHOD, ref dp, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (hr != 0)
                {
                    Diag.Log(string.Format("ShellReg: 导航到 {0} 失败 hr=0x{1:X8}", path, hr));
                    return false;
                }
                return true;
            }
            catch (Exception ex) { Diag.Log("ShellReg: 导航失败 " + ex.Message); return false; }
            finally
            {
                if (url != IntPtr.Zero) { try { Marshal.FreeBSTR(url); } catch { } }
                Marshal.FreeCoTaskMem(inner);
                Marshal.FreeCoTaskMem(outer);
            }
        }

        /// <summary>
        /// 某个文件夹窗口**现在选中了哪些项**（完整路径）。给「空格预览」（`QLPreview`）用。
        ///
        /// 怎么对上号：收编之后清单里每一项报回来的 `HWND` **全是同一个宿主窗体**（见类注释），
        /// 按句柄分不出谁是谁 —— 但 `LocationURL` 一直是准的（实测），所以拿它跟
        /// 「当前激活标签现在停在的文件夹」比。⚠ 同一个目录开了两个标签时会撞：
        /// 小概率取到另一个标签的选中项，先接受（预览出来的还是这个目录里的东西）。
        ///
        /// 优先「选中项」（`Document.SelectedItems`），一个都没有再退回「焦点项」（`FocusedItem`）
        /// —— 键盘上下移动时只有焦点没有选中，那会儿按空格也该预览得到。
        ///
        /// ⚠ 只认 `file:///` 的真目录（虚拟位置 / 别的协议返回空表）—— 调用方拿不到东西就不动作。
        /// ⚠ 会被 UI 线程调（必须在 STA 上，`Application.OleRequired` 就是为这个）；一次全量枚举
        ///   实测 16~41ms，按空格是人的动作、这个量级无所谓，所以不做节流。
        /// </summary>
        internal static List<string> SelectedPathsIn(string folderPath)
        {
            List<string> res = new List<string>();
            if (string.IsNullOrEmpty(folderPath)) return res;
            try
            {
                Application.OleRequired();
                Type t = Type.GetTypeFromCLSID(CLSID_ShellWindows);
                if (t == null) return res;
                IDispatch root = Activator.CreateInstance(t) as IDispatch;
                if (root == null) return res;

                string want = folderPath.TrimEnd('\\');
                int total = ReadInt(root, "Count");
                for (int i = 0; i < total; i++)
                {
                    IDispatch item = ReadItem(root, i);
                    if (item == null) continue;
                    string p = FileUrlToPath(ReadString(item, "LocationURL"));
                    if (p == null) continue;
                    if (!string.Equals(p.TrimEnd('\\'), want, StringComparison.OrdinalIgnoreCase)) continue;

                    ReadSelection(item, res);
                    return res;         // 找到那一项了，不管有没有选中都收工
                }
            }
            catch (Exception ex) { Diag.Log("ShellReg: 读选中项失败 " + ex.Message); }
            return res;
        }

        /// <summary>
        /// 某扇 shell 窗口**现在选中了什么**（第一个，完整路径）。给「三方在资源管理器里显示某个文件/文件夹」
        /// 那条用：原生那一下是**在父目录里把目标选中**的，我们只把目录开成标签就把这个意图丢了
        /// （见 `DesktopHub.TakeOverShellWindow` / `ScheduleReveal`）。
        ///
        /// 跟 <see cref="SelectedPathsIn"/> 只差「怎么找到那一项」：这里按 **HWND** 找 —— 调用它的
        /// 那一刻那扇窗**还是顶层窗**（shell 自己的窗我们从不 SetParent），所以句柄对得上。
        /// ⚠ 只在它**还活着**的时候才有答案：shell 一关，清单里那一项也就没了。它活多久见
        ///   `DesktopHub.ShellSettleMs`（读到路径后还有 800ms）。
        ///
        /// 从**队尾**往前找（理由同 <see cref="Lookback"/>）：要问的那扇窗是刚建出来的、必在末尾几项里，
        /// 只翻 `RevealLookback` 项实测 2~4ms —— 而全量枚举是 16~41ms，而这一条要被**反复轮询**。
        /// ⚠ 余量比 `Lookback` 给得宽：那 800ms 里我们自己开标签的窗口也在往队尾追加，会把目标往后挤。
        /// </summary>
        internal static string SelectedPathOfWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return null;
            try
            {
                Application.OleRequired();
                Type t = Type.GetTypeFromCLSID(CLSID_ShellWindows);
                if (t == null) return null;
                IDispatch root = Activator.CreateInstance(t) as IDispatch;
                if (root == null) return null;

                int total = ReadInt(root, "Count");
                for (int i = 0; i < RevealLookback && i < total; i++)
                {
                    IDispatch item = ReadItem(root, total - 1 - i);
                    if (item == null) continue;
                    if (ReadHwnd(item) != hwnd) continue;
                    List<string> res = new List<string>();
                    ReadSelection(item, res);
                    return res.Count > 0 ? res[0] : null;
                }
            }
            catch (Exception ex) { Diag.Log("ShellReg: 读窗口选中项失败 " + ex.Message); }
            return null;
        }

        /// <summary>
        /// 让**某个已经开着 <paramref name="folderPath"/> 的窗口**选中 <paramref name="itemPath"/>。
        ///
        /// 怎么调（照文档 `shellfolderview-selectitem`）：
        ///   `Document`（就是 `ShellFolderView`，本文件取 `SelectedItems` 用的就是它）
        ///   → `Document.Folder.ParseName(&lt;名字&gt;)` 拿到 `FolderItem`
        ///   → `Document.SelectItem(folderItem, 29)`。`29 = 1|4|8|16` = 选中 + 只留它 + 滚进视线 + 给焦点。
        /// ⚠ 形参要一个 **FolderItem 对象**，不是字符串（文档写明）—— 网上流传的「直接传路径」不是文档口径。
        /// ⚠ 找哪扇窗：清单项的 `HWND` 对**我们的**标签全是宿主窗体（见类注释），只能按 `LocationURL` 认；
        ///   同一个目录开着两个标签时会撞 —— 先接受（选中的还是这个目录里的东西）。
        ///   ★ **必须排掉 <paramref name="exclude"/> 那扇窗**：那正是刚把用户引过来的 shell 自己的窗，
        ///   它开着同一个目录、而且 `/select,` 本来就把它选中了 ⇒ 摆到它身上会「核对轻松通过」，
        ///   可它下一秒就被关掉，用户什么也没看到（实测 2026-10-01：标签的 explorer 还在加载、
        ///   目录下根本没有我们的窗，于是唯一匹配就是它 —— 摆完 272ms 后就被关了）。
        /// ⚠ `SelectItem` 没有返回值，只能**读回来核对**（选中项对不对得上）。
        /// </summary>
        internal static bool SelectItemInWindow(string folderPath, string itemPath, IntPtr exclude)
        {
            if (string.IsNullOrEmpty(folderPath) || string.IsNullOrEmpty(itemPath)) return false;
            try
            {
                Application.OleRequired();
                Type t = Type.GetTypeFromCLSID(CLSID_ShellWindows);
                if (t == null) return false;
                IDispatch root = Activator.CreateInstance(t) as IDispatch;
                if (root == null) return false;

                string want = folderPath.TrimEnd('\\');
                string name = LastSegment(itemPath);
                if (string.IsNullOrEmpty(name)) return false;

                // 先挑窗、再动手：队尾往前第一扇**正显示着**的（我们的标签如果在那儿，它就是用户看的那扇），
                // 一扇可见的都没有才退回队尾第一扇（可能还在加载 / 被藏在后台）。
                IDispatch item = null;
                IntPtr pickedHwnd = IntPtr.Zero;
                IDispatch fallback = null;
                IntPtr fallbackHwnd = IntPtr.Zero;
                int total = ReadInt(root, "Count");
                for (int i = 0; i < total; i++)
                {
                    IDispatch it = ReadItem(root, total - 1 - i);        // 队尾往前
                    if (it == null) continue;
                    string p = FileUrlToPath(ReadString(it, "LocationURL"));
                    if (p == null) continue;
                    if (!string.Equals(p.TrimEnd('\\'), want, StringComparison.OrdinalIgnoreCase)) continue;
                    IntPtr hw = ReadHwnd(it);
                    if (exclude != IntPtr.Zero && hw == exclude) continue;      // ★ 绝不摆到它身上
                    if (fallback == null) { fallback = it; fallbackHwnd = hw; }
                    if (EmbedApi.IsWindowVisible(hw)) { item = it; pickedHwnd = hw; break; }
                }
                if (item == null) { item = fallback; pickedHwnd = fallbackHwnd; }
                if (item == null) return false;      // 目录下还没有我们的窗（标签还在加载）—— 交给调用方重试

                IDispatch doc = ReadObject(item, "Document");
                if (doc == null) { Diag.Log("ShellReg: 拿不到 Document"); return false; }
                IDispatch folder = ReadObject(doc, "Folder");
                if (folder == null) { Diag.Log("ShellReg: 拿不到 Document.Folder"); return false; }
                IDispatch fi = Call1String(folder, "ParseName", name);
                if (fi == null) { Diag.Log("ShellReg: ParseName 找不到 " + name); return false; }
                if (!CallDispatchInt(doc, "SelectItem", fi, 29)) return false;

                // ⚠ 核对要分得清「真选中了」和「只是焦点落上去了」：`SelectItem` 没有返回值，
                //   而 `SelectedItems()` 为空时 `FocusedItem` 仍会报回目标 —— 两个混着读会把
                //   「高亮没上」判成成功（用户看到的就成了「日志说摆了、可没选中」）。分开读、分开记。
                List<string> now = new List<string>();
                ReadSelected(doc, now);
                if (Holds(now, itemPath))
                {
                    Diag.Step(string.Format("ShellReg: 摆选中项到 cab=0x{0:X} -> {1}", pickedHwnd.ToInt64(), itemPath));
                    return true;
                }
                List<string> foc = new List<string>();
                ReadFocused(doc, foc);
                if (Holds(foc, itemPath))
                {
                    Diag.Log("ShellReg: SelectItem 只有焦点项对上、SelectedItems 是空的（高亮可能没上）目标 " + itemPath);
                    return true;
                }
                Diag.Log("ShellReg: SelectItem 调过了、目标却没对上（SelectedItems=" +
                         (now.Count == 0 ? "空" : now[0]) + "，FocusedItem=" +
                         (foc.Count == 0 ? "空" : foc[0]) + "）目标 " + itemPath);
                return false;
            }
            catch (Exception ex) { Diag.Log("ShellReg: 选中项失败 " + ex.Message); }
            return false;
        }

        /// <summary>路径的最后一段（`Folder.ParseName` 要的是名字，不是整条路径）。</summary>
        private static string LastSegment(string p)
        {
            if (string.IsNullOrEmpty(p)) return null;
            string s = p.TrimEnd('\\', '/');
            int i = s.LastIndexOfAny(new char[] { '\\', '/' });
            return i >= 0 ? s.Substring(i + 1) : s;
        }

        /// <summary>从一扇窗的清单项里读「选中的那些」；一个都没选中就退回「焦点项」（见 `SelectedPathsIn`）。</summary>
        private static void ReadSelection(IDispatch item, List<string> res)
        {
            IDispatch doc = ReadObject(item, "Document");
            if (doc == null) return;
            ReadSelected(doc, res);
            if (res.Count == 0) ReadFocused(doc, res);
        }

        /// <summary>只读 `Document.SelectedItems()`（**不含**焦点项兜底）—— 核对「真选中」用，见 `SelectItemInWindow`。</summary>
        private static void ReadSelected(IDispatch doc, List<string> res)
        {
            IDispatch items = InvokeGet(doc, "SelectedItems");
            int n = items == null ? 0 : ReadInt(items, "Count");
            for (int j = 0; j < n; j++)
            {
                IDispatch fi = ReadItem(items, j);
                string sp = fi == null ? null : ReadString(fi, "Path");
                if (!string.IsNullOrEmpty(sp)) res.Add(sp);
            }
        }

        /// <summary>只读 `Document.FocusedItem`（键盘上下移动时只有焦点、没有选中）。</summary>
        private static void ReadFocused(IDispatch doc, List<string> res)
        {
            IDispatch foc = ReadObject(doc, "FocusedItem");
            string fp = foc == null ? null : ReadString(foc, "Path");
            if (!string.IsNullOrEmpty(fp)) res.Add(fp);
        }

        /// <summary>这张表里有没有它（路径按大小写不敏感比）。</summary>
        private static bool Holds(List<string> list, string path)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i], path, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>读一个**对象**属性（`Document` / `FocusedItem`）：拿到的 RCW 按 QI 转成 `IDispatch`。</summary>
        private static IDispatch ReadObject(IDispatch d, string name)
        {
            IntPtr v = Marshal.AllocCoTaskMem(VarSize);
            try
            {
                Zero(v, VarSize);
                object o = Invoke(d, name, DISPATCH_PROPERTYGET, v, IntPtr.Zero, 0);
                return o as IDispatch;
            }
            finally { Marshal.FreeCoTaskMem(v); }
        }

        /// <summary>调一个**无参方法**（`SelectedItems()`），返回值同样当对象看。</summary>
        private static IDispatch InvokeGet(IDispatch d, string name)
        {
            IntPtr v = Marshal.AllocCoTaskMem(VarSize);
            try
            {
                Zero(v, VarSize);
                object o = Invoke(d, name, DISPATCH_METHOD, v, IntPtr.Zero, 0);
                return o as IDispatch;
            }
            finally { Marshal.FreeCoTaskMem(v); }
        }

        /// <summary>调一个「一个字符串参数」的方法（`Folder.ParseName`），返回值当对象看。</summary>
        private static IDispatch Call1String(IDispatch d, string name, string arg)
        {
            int id;
            if (!DispIdOf(d, name, out id)) return null;

            IntPtr a = Marshal.AllocCoTaskMem(VarSize);
            IntPtr result = Marshal.AllocCoTaskMem(VarSize);
            IntPtr bstr = IntPtr.Zero;
            try
            {
                bstr = Marshal.StringToBSTR(arg);
                Zero(a, VarSize);
                Marshal.WriteInt16(a, 0, VT_BSTR);
                Marshal.WriteIntPtr(a, 8, bstr);
                Zero(result, VarSize);
                object o = Invoke(d, id, DISPATCH_METHOD, result, a, 1);
                return o as IDispatch;
            }
            finally
            {
                if (bstr != IntPtr.Zero) { try { Marshal.FreeBSTR(bstr); } catch { } }
                Marshal.FreeCoTaskMem(a);
                Marshal.FreeCoTaskMem(result);
            }
        }

        /// <summary>
        /// 调一个「`IDispatch` + 整数」两个形参的方法（`ShellFolderView.SelectItem(vItem, dwFlags)`）。
        /// ⚠ `DISPPARAMS.rgvarg` 是**倒序**的（第 0 个放的是**最后**一个形参）⇒ flags 在前、item 在后。
        /// </summary>
        private static bool CallDispatchInt(IDispatch d, string name, IDispatch arg, int flag)
        {
            int id;
            if (!DispIdOf(d, name, out id)) return false;

            IntPtr buf = Marshal.AllocCoTaskMem(VarSize * 2);
            IntPtr punk = IntPtr.Zero;
            try
            {
                punk = Marshal.GetComInterfaceForObject(arg, typeof(IDispatch));
                Zero(buf, VarSize * 2);
                Marshal.WriteInt16(buf, 0, VT_I4);                  // [0] = dwFlags（最后一个形参）
                Marshal.WriteInt32(buf, 8, flag);
                Marshal.WriteInt16(buf, VarSize, VT_DISPATCH);      // [1] = vItem
                Marshal.WriteIntPtr(buf, VarSize + 8, punk);

                // ⚠ 结果缓冲区给 IntPtr.Zero：SelectItem 没有返回值（pVarResult 允许为 NULL）
                DISPPARAMS dp = new DISPPARAMS();
                dp.rgvarg = buf;
                dp.cArgs = 2;
                Guid none = Guid.Empty;
                int hr = d.Invoke(id, ref none, 0, DISPATCH_METHOD, ref dp,
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (hr != 0)
                {
                    Diag.Log(string.Format("ShellReg: SelectItem hr=0x{0:X8}", hr));
                    return false;
                }
                return true;
            }
            finally
            {
                if (punk != IntPtr.Zero) { try { Marshal.Release(punk); } catch { } }
                Marshal.FreeCoTaskMem(buf);
            }
        }

        /// <summary>
        /// 那扇窗**现在**开着哪个文件夹（`LocationURL` → 真路径）。给「等它导航到位」那条用（见 `NavigateTo`）。
        /// ⚠ 只认 `file:///` 那一种（虚拟位置返回 null）—— 调用方拿它跟一个真文件夹比，比不了就算还没到位。
        /// </summary>
        internal static string PathOfEntry(object target)
        {
            IDispatch d = target as IDispatch;
            if (d == null) return null;
            try { return FileUrlToPath(ReadString(d, "LocationURL")); }
            catch { return null; }
        }

        /// <summary>
        /// `file:///D:/Dev/!tmp/` → `D:\Dev\!tmp`。认不出来的（UNC / 虚拟位置 / 别的协议）返回 null ——
        /// 宁可退回地址栏那条路，也不要交一个可能开不出来的字符串出去。
        ///
        /// ⚠ **盘符根目录会只剩 `D:`（长度 2）**：`file:///D:/` 去掉尾部反斜杠就是它。
        ///   所以这里只能判 `Length >= 2`——判 `>= 3` 会把 `C:\` / `D:\` 这类根目录当成非法，
        ///   于是 <see cref="SelectedPathsIn"/> 里 `p == null` 直接 `continue`、**永远匹配不上那扇窗**，
        ///   表现就是「停在盘符根目录时读不到选中项 ⇒ 空格不预览 / Alt+Enter 没反应」
        ///   （2026-10-02 实测踩到：目录一深就正常，一停在 `D:\` 必哑）。
        /// </summary>
        private static string FileUrlToPath(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            if (!url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return null;
            string s = url.Substring(5);
            if (s.StartsWith("///")) s = s.Substring(3);      // file:///D:/… → D:/…
            else return null;                                  // file://server/… 之类：不猜，交给地址栏
            s = Uri.UnescapeDataString(s);
            s = s.Replace('/', '\\').TrimEnd('\\');
            return (s.Length >= 2 && s[1] == ':') ? s : null;  // 盘符开头（`D:` 这种根目录也算）
        }

        // ==================================================================
        // IDispatch 那点活儿：只有属性读一个整数 / 读 lstItem / 读一个字符串 / 写一个布尔，够用就行
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

        /// <summary>读一个 `BSTR` 属性（`LocationURL`）。拿不到就当空。</summary>
        private static string ReadString(IDispatch d, string name)
        {
            IntPtr v = Marshal.AllocCoTaskMem(VarSize);
            try
            {
                Zero(v, VarSize);
                object o = Invoke(d, name, DISPATCH_PROPERTYGET, v, IntPtr.Zero, 0);
                return o as string;
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
