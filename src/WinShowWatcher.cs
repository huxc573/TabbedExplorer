using System;
using System.Runtime.InteropServices;

namespace TabbedExplorer
{
    /// <summary>
    /// 「哪个窗口被显示了」的观察者。系统里**任何**窗口将被显示出来时都会通知我们一声。
    ///
    /// 用途：给新标签起 explorer 时，那个窗口在真正被我们嵌进来之前会**在桌面上一闪而过**。
    /// 光靠 25ms 轮询，最快也要等一个 tick 才看得见它 —— 肉眼还是能捕捉到那一下闪。
    /// 有了这个通知，我们能在它刚 `ShowWindow` 的那一刻（同一毫秒）就把它藏掉。
    ///
    /// 三条注意：
    ///   1. 这是**全系统**的 show 事件，回调会被经常叫到 —— 里面只准做「读类名 / 读 pid / 判定」，
    ///      重活（藏窗口、写日志）交给订阅方，且要能承受偶尔被拖慢。
    ///   2. 用 `WINEVENT_OUTOFCONTEXT` 注册：**回调在注册它的那个线程上执行**（我们的 UI 线程），
    ///      不是目标进程的线程。所以回调里别长时间阻塞，否则我们自己界面会卡。
    ///   3. 回调委托必须存字段：被 GC 回收了事件就断了（和钩子一个道理）。
    /// </summary>
    internal sealed class WinShowWatcher : IDisposable
    {
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

        /// <summary>某个窗口被显示出来了（**在 UI 线程上**触发）。</summary>
        public event Action<IntPtr> WindowShown;

        private WinEventDelegate proc;
        private IntPtr hook = IntPtr.Zero;

        public bool Installed { get { return hook != IntPtr.Zero; } }

        public void Start()
        {
            if (hook != IntPtr.Zero) return;
            proc = new WinEventDelegate(Callback);
            hook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, proc, 0, 0,
                WINEVENT_OUTOFCONTEXT);
            if (hook == IntPtr.Zero)
                Diag.Log("WinShowWatcher: 安装失败 err=" + Marshal.GetLastWin32Error());
        }

        private void Callback(IntPtr hHook, uint evt, IntPtr hwnd, int idObject, int idChild,
            uint threadId, uint time)
        {
            // 只要「窗口自身」的显示，不要菜单项/列表项那种子对象的
            if (idObject != OBJID_WINDOW || idChild != CHILDID_SELF) return;
            Action<IntPtr> a = WindowShown;
            if (a != null) a(hwnd);
        }

        public void Dispose()
        {
            try { if (hook != IntPtr.Zero) UnhookWinEvent(hook); } catch { }
            hook = IntPtr.Zero;
        }
    }
}
