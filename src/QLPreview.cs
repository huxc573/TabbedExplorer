using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;

namespace TabbedExplorer
{
    /// <summary>
    /// 空格键预览（QuickLook）。用户：「适配 QuicLook 的空格键预览 —— 我在原生资源管理器
    /// 明明是没问题的。」
    ///
    /// 为什么原生行、我们不行：QuickLook 的判据**只有一条** —— 读**前台窗口的类名**。
    /// 它的判定表（从本机 `QuickLook.Native64.dll` 里读出来的 UTF-16 字面量）就是
    /// `CabinetWClass` / `ExploreWClass` / `ShellTabWindowClass` / `dopus.lister` /
    /// `EVERYTHING` / `DUIViewWndClassName` / `WorkerW` / `Progman` / `#32770`。
    /// 原生资源管理器是 `CabinetWClass`，它认；我们的宿主是自绘窗体（`WindowsForms10.Window.*`），
    /// 它一律判 `Invalid` —— **整段逻辑（连取选中项）都不会走**。所以这跟「取不到选中项」无关，
    /// 是第一关就过不了。
    ///
    /// ⚠ 「内嵌的那个 `CabinetWClass` 明明是原生的，为什么也不行」—— 因为 `SetParent` 之后它成了
    ///   **子窗口**，而 Windows 里**子窗口永远不能成为前台窗口**：`GetForegroundWindow()` 只返回
    ///   顶层窗口，也就是我们那个自绘宿主。探针实测：10 组 `CabinetWClass` /
    ///   `ShellTabWindowClass` / `DUIViewWndClassName` 全是 `WS_CHILD=1`、顶层祖先都是宿主窗体。
    ///
    /// 我们怎么接：`WinEHook` 那个全局键盘钩子（本来管 Win+E 和标签快捷键）加一条空格分支。
    /// 钩子里**不做 COM / IO**（见那儿的硬规矩），判定只做纯 Win32（见 `WantsSpace`），
    /// 真干活投回 UI 线程（`Do`）。`--classic` 那条老路（`AppContext`）不接这个功能。
    /// </summary>
    internal static class QLPreview
    {
        private const int VK_SHIFT = 0x10;
        private const int VK_CONTROL = 0x11;
        private const int VK_MENU = 0x12;      // Alt
        private const int VK_SPACE = 0x20;
        private const uint GA_ROOT = 2;

        /// <summary>QuickLook 的跨进程消息名（照抄它自己的 `PipeMessages` 常量）。</summary>
        private const string MsgToggle = "QuickLook.App.PipeMessages.Toggle";

        /// <summary>
        /// 连管道的超时（毫秒）。**必须给** —— `NamedPipeClientStream.Connect()` 不传超时是
        /// **无限等**，QuickLook 没在跑时会把 UI 线程挂死。
        /// </summary>
        private const int PipeConnectMs = 1200;

        /// <summary>命名管道名（只算一次；SID 不会变）。</summary>
        private static string pipeName;

        /// <summary>上次找到的 exe（**只给回退路径用**，找到才缓存）。</summary>
        private static string cachedExe;

        /// <summary>
        /// 宿主窗体句柄的一份快照，**只在 UI 线程重建**（`DesktopHub.SyncHosts`）。钩子线程只读它
        /// 一次 —— 做成整份替换而不是共享一个可变集合：钩子读的时候不会撞上别人正在改，
        /// 也不用在钩子里加锁（低级钩子有超时限制，里面不能干慢活）。
        ///
        /// 为什么非要这个白名单：光判「前台窗口属于本进程」不够 —— 设置窗口、通知气泡也是我们的窗口，
        /// 而**在那些地方空格是正常操作**（勾复选框），绝不能被吞。
        /// </summary>
        private static volatile IntPtr[] hosts = new IntPtr[0];

        /// <summary>这一次按下的空格已经处理过了（防长按那串重复的 `WM_KEYDOWN` 连着弹好几次预览）。</summary>
        private static bool spaceDown;

        /// <summary>把宿主窗体句柄同步进来。只在 UI 线程调（`DesktopHub.SyncHosts`）。</summary>
        public static void SetHosts(List<IntPtr> list)
        {
            hosts = (list == null) ? new IntPtr[0] : list.ToArray();
        }

        /// <summary>空格松开时清标志（钩子线程调）。长按就只会弹一次。</summary>
        public static void NoteKeyUp(int vk)
        {
            if (vk == VK_SPACE) spaceDown = false;
        }

        /// <summary>
        /// 这次空格该不该归我们管（**钩子线程**：只读字段 + 纯 Win32，不做 COM / IO）。
        /// 四条：开关开着、没按 Ctrl / Alt / Shift（Ctrl+空格 是输入法切换，绝不能抢）、
        /// 这一次按下还没处理过、前台是我们某个宿主窗体。
        /// </summary>
        public static bool WantsSpace()
        {
            if (!Settings.QLPreview) return false;
            if (spaceDown) return false;
            if ((NativeMethods.GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0) return false;
            if ((NativeMethods.GetAsyncKeyState(VK_MENU) & 0x8000) != 0) return false;
            if ((NativeMethods.GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0) return false;

            IntPtr fg = NativeMethods.GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            // `SetParent` 之后内嵌的 shell 窗口是**子窗口**，前台窗口永远是顶层的宿主窗体；
            // 但焦点可能落在子窗口上，所以两头都认（`GA_ROOT` 一路走到最外层父窗口）。
            IntPtr root = NativeMethods.GetAncestor(fg, GA_ROOT);
            IntPtr[] hs = hosts;
            for (int i = 0; i < hs.Length; i++)
            {
                if (hs[i] == fg || hs[i] == root)
                {
                    spaceDown = true;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 干活的地方（**UI 线程**，由 `DesktopHub.Post` 投过来）：把 `folder` 里现在选中的
        /// 第一个喂给 QuickLook。
        /// ⚠ 没选中东西 / 停在虚拟位置 —— 一律什么都不做。
        /// </summary>
        public static void Do(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return;

            List<string> sel = ShellBrowserReg.SelectedPathsIn(folder);
            if (sel.Count == 0) return;

            Preview(sel[0]);
            Diag.Step("QLPreview: 预览 " + sel[0]);   // ⚠ 记账放在预览**之后**：这条落盘是
                                                     // 每行一次 `AppendAllText`（开→写→关），
                                                     // Debug 开着时别让它挡在预览前面。
        }

        /// <summary>
        /// 让那个常驻的 QuickLook 预览一个路径。
        ///
        /// ⚠ **不走** `QuickLook.exe &lt;路径&gt;`（起第二实例）那条路：那是 QuickLook 官方支持的外部
        /// 入口，但代价是**每按一次空格创建一个进程** —— 实测就是它把「比在原生资源管理器里按」
        /// 拖成了肉眼可见的慢（起进程几十到几百毫秒；管道 0.1~0.3ms）。
        ///
        /// 协议是从 QuickLook 自己的 `PipeServerManager.PostMessage` 抄的，就一行文本：
        ///   管道名  `QuickLook.App.Pipe.&lt;用户 SID&gt;`
        ///   内容    `QuickLook.App.PipeMessages.Toggle|&lt;路径&gt;|` + 换行（UTF-8 无 BOM）
        /// 服务端是 `StreamReader.ReadLine()` —— 换行**必须有**，而且**绝不能发空行**
        /// （它会拿 null 去 `Split`、把自己那条读线程搞崩）。
        ///
        /// 管道连不上（QuickLook 没在跑、或者哪天协议改了）就**回退**到起第二实例那条老路。
        /// </summary>
        public static void Preview(string path)
        {
            if (SendViaPipe(path)) return;

            string exe = FindExe();
            if (exe == null) { Diag.Step("QLPreview: 管道不通，也找不到 QuickLook.exe"); return; }
            LegacyStart(exe, path);
        }

        /// <summary>往常驻实例的命名管道写一行消息。连上并写完返回 true。</summary>
        private static bool SendViaPipe(string path)
        {
            string name = PipeNameOf();
            if (string.IsNullOrEmpty(name)) return false;
            try
            {
                using (NamedPipeClientStream c = new NamedPipeClientStream(".", name, PipeDirection.Out))
                {
                    c.Connect(PipeConnectMs);
                    using (StreamWriter w = new StreamWriter(c, new UTF8Encoding(false)))
                    {
                        w.WriteLine(MsgToggle + "|" + path + "|");
                        w.Flush();
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Diag.Step("QLPreview: 管道没走通（" + ex.GetType().Name + "），改用第二实例");
                return false;
            }
        }

        /// <summary>回退路径：起一个短命的第二实例，它自己会把消息转发给常驻实例然后退出。</summary>
        private static void LegacyStart(string exe, string path)
        {
            try
            {
                ProcessStartInfo si = new ProcessStartInfo(exe, "\"" + path + "\"");
                si.UseShellExecute = false;
                Process.Start(si);
            }
            catch (Exception ex) { Diag.Log("QLPreview: 起 QuickLook 失败 " + ex.Message); }
        }

        /// <summary>
        /// 常驻那个 QuickLook 的命名管道名（`QuickLook.App.Pipe.&lt;用户 SID&gt;`）。
        /// 拿不到 SID 返回 null（调用方回退）。
        /// </summary>
        private static string PipeNameOf()
        {
            if (pipeName != null) return pipeName;
            try
            {
                string sid = WindowsIdentity.GetCurrent().User.Value;
                if (!string.IsNullOrEmpty(sid)) pipeName = "QuickLook.App.Pipe." + sid;
            }
            catch (Exception ex) { Diag.Log("QLPreview: 取 SID 失败 " + ex.Message); }
            return pipeName;
        }

        /// <summary>
        /// QuickLook.exe 在哪（**只给回退路径用**）。找不到返回 null。
        ///
        /// 为什么不用注册表：本机（以及不少人）用的是便携版，装的时候没写卸载项、也没写 App Paths，
        /// 注册表里根本查不到。而 QuickLook 要能预览就**必须常驻**，所以「正在跑的那个 QuickLook
        /// 进程的可执行文件路径」是最靠得住的一条。
        /// ⚠ `MainModule` 对位数不同的进程会抛（QuickLook 主程序是 64 位，我们也是，正常可读）；
        ///   抛了换下一个候选，全都不行就返回 null。
        /// </summary>
        private static string FindExe()
        {
            if (!string.IsNullOrEmpty(cachedExe) && File.Exists(cachedExe)) return cachedExe;

            string found = null;
            try
            {
                Process[] ps = Process.GetProcessesByName("QuickLook");
                try
                {
                    for (int i = 0; i < ps.Length && found == null; i++)
                    {
                        try
                        {
                            string f = ps[i].MainModule.FileName;
                            if (!string.IsNullOrEmpty(f) && File.Exists(f)) found = f;
                        }
                        catch { }
                    }
                }
                finally
                {
                    for (int i = 0; i < ps.Length; i++) { try { ps[i].Dispose(); } catch { } }
                }
            }
            catch { }

            // 找到就缓存（省掉几毫秒的进程枚举）；没找到**不缓存** —— 用户可能等下才开 QuickLook
            if (found != null) cachedExe = found;
            return found;
        }
    }
}
