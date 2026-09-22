using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace TabbedExplorer
{
    /// <summary>
    /// 「哪个窗口被显示了」的观察者。系统里**任何**窗口将被显示出来时都会通知我们一声。
    ///
    /// 用途：给新标签起 explorer 时，那个窗口在真正被我们嵌进来之前会**在桌面上一闪而过**。
    /// 光靠 25ms 轮询，最快也要等一个 tick 才看得见它 —— 肉眼还是能捕捉到那一下闪。
    /// 有了这个通知，我们能在它刚 `ShowWindow` 的那一刻就把它藏掉。
    ///
    /// 两种装法（2026-09-22 加第二种）：
    ///   · `Start()` —— 装在**当前线程**（注册完就返回，靠调用方自己的消息循环收事件）。
    ///     `ExplorerHost` 用这个：它要动自己的 UI 状态（hosts / 队列），本来就得在 UI 线程上。
    ///   · `StartDedicated(name)` —— 自己起一条**专用的消息循环线程**。
    ///     Hub 那条「看见别人开的文件夹就抢过来」的路用这个，原因见下面那段。
    ///
    /// ⚠⚠ 为什么 Hub 那条非要另起线程（川 2026-09-22 报「从桌面/开始菜单打开文件夹还是闪一下」）：
    ///   `WINEVENT_OUTOFCONTEXT` 的回调是**投到注册它的那个线程的消息队列**上的。
    ///   装在 UI 线程上时，只要我们的 UI 线程手上正忙（菜单模态循环、起 explorer、算布局……），
    ///   这条「某某窗口显示了」的通知就得排队 —— 而那个窗口**已经画在屏幕上了**，肉眼看到的就是闪一下。
    ///   专用线程永远是空闲的（它只干这件事），事件一投过来就能当场 `SW_HIDE`，延迟从「看运气」压到毫秒级。
    ///
    /// 三条注意：
    ///   1. 这是**全系统**的 show / create 事件，回调会被经常叫到 —— 里面只准做「读类名 / 读 pid / 判定」，
    ///      重活（藏窗口、写日志）交给订阅方，且要能承受偶尔被拖慢。
    ///   2. 用 `WINEVENT_OUTOFCONTEXT` 注册：**回调在注册它的那个线程上执行**。所以回调里别长时间阻塞。
    ///   3. 回调委托必须存字段：被 GC 回收了事件就断了（和钩子一个道理）。
    ///
    /// 2026-09-22 又补了一件事：`StartDedicated` 那条还会一起听 **CREATE**（0x8000）。
    ///   理由：有的窗口是「创建时就带 WS_VISIBLE」，创建那一刻就已经在屏幕上了；
    ///   只听 SHOW 会晚一步（SHOW 在创建完之后才报）。两个都听，谁先到就先藏。
    ///   判定逻辑完全一样，所以只是「多一次更早的机会」。
    ///   （`Start()` 那条**只听 SHOW** —— 它装在 UI 线程上，而建窗口的事件非常密，白添负担。）
    /// </summary>
    internal sealed class WinShowWatcher : IDisposable
    {
        private const uint EVENT_OBJECT_CREATE = 0x8000;
        private const uint EVENT_OBJECT_SHOW = 0x8002;
        private const int OBJID_WINDOW = 0;     // idObject = 窗口本身
        private const int CHILDID_SELF = 0;
        private const uint WINEVENT_OUTOFCONTEXT = 0;

        private delegate void WinEventDelegate(IntPtr hHook, uint evt, IntPtr hwnd,
            int idObject, int idChild, uint threadId, uint time);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmod,
            WinEventDelegate proc, uint pid, uint tid, uint flags);

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hHook);

        /// <summary>
        /// 某个窗口刚被创建 / 被显示了（在注册它的那个线程上触发）。
        /// 第二个参数是**系统报这个事件的时刻**（`GetTickCount` 的口径），
        /// 用来量「从窗口出现在屏幕上，到我们动手」中间隔了多少毫秒 —— 防闪到底快不快，看它就够了。
        /// </summary>
        public event Action<IntPtr, int> WindowShown;

        private WinEventDelegate proc;
        private IntPtr hook = IntPtr.Zero;

        private Thread thread;
        private ApplicationContext ctx;

        public bool Installed { get { return hook != IntPtr.Zero; } }

        /// <summary>装在当前线程上（调用方得有消息循环）。只听 SHOW。</summary>
        public void Start()
        {
            Install(false);
        }

        /// <summary>
        /// 自己起一条专用线程来装（回调在**那条线程**上触发，不占 UI 线程）。
        /// 再加听 **CREATE** —— CREATE 比 SHOW 早（有的窗口一创建就带 WS_VISIBLE，创建完就已经在屏幕上了），
        /// 早一步看见就早一步藏，防闪才稳。这条只在 Hub 那条防闪路上开：
        /// 系统里建窗口的事件很密，装在 UI 线程上会白添负担（ExplorerHost 那条仍只听 SHOW）。
        /// </summary>
        public void StartDedicated(string threadName)
        {
            thread = new Thread(delegate()
            {
                Install(true);
                if (hook == IntPtr.Zero) return;
                ctx = new ApplicationContext();
                Application.Run(ctx);
                try { UnhookWinEvent(hook); } catch { }
                hook = IntPtr.Zero;
            });
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Name = threadName;
            thread.Start();
        }

        private void Install(bool withCreate)
        {
            if (hook != IntPtr.Zero) return;
            proc = new WinEventDelegate(Callback);      // 存字段：委托被回收了钩子就哑了
            uint min = withCreate ? EVENT_OBJECT_CREATE : EVENT_OBJECT_SHOW;
            hook = SetWinEventHook(min, EVENT_OBJECT_SHOW, IntPtr.Zero, proc, 0, 0,
                WINEVENT_OUTOFCONTEXT);
            if (hook == IntPtr.Zero)
                Diag.Log("WinShowWatcher: 安装失败 err=" + Marshal.GetLastWin32Error());
        }

        private void Callback(IntPtr hHook, uint evt, IntPtr hwnd, int idObject, int idChild,
            uint threadId, uint time)
        {
            // 只要「窗口自身」的显示/创建，不要菜单项/列表项那种子对象的
            if (idObject != OBJID_WINDOW || idChild != CHILDID_SELF) return;
            Action<IntPtr, int> a = WindowShown;
            if (a != null) a(hwnd, (int)time);
        }

        public void Dispose()
        {
            try { if (hook != IntPtr.Zero) UnhookWinEvent(hook); } catch { }
            hook = IntPtr.Zero;
            try { if (ctx != null) ctx.ExitThread(); } catch { }
            ctx = null;
        }
    }
}
