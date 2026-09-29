using System;
using System.Collections.Generic;

namespace TabbedExplorer
{
    /// <summary>
    /// 一条**具体的**通知（右下角气泡）—— 设置窗口「通知管理」页里那一行就是它。
    ///
    /// 为什么要细到这一步（用户：「不够细，既然都做通知管理了，比如「出错与失败」算一列，
    /// 用一条线隔开，然后具体在里面的明细可以多选」）：
    /// 原来只有四个**大类**开关，嫌吵时只能把一整类关掉 —— 可真正烦人的常常就那一两条
    /// （「已复制」这种），关掉整类会把「加入失败」一起吞掉。现在每条一个开关，默认值
    /// 仍然按大类来（`Important`），要更细就自己在设置页里勾。
    ///
    /// ⚠ 调用点一律传**对象**（`Toast.Show(NotifyItems.CopyDone, …)`）而不是字符串 id ——
    /// 拼错字符串编译器不管，拼错这个当场编译不过。
    /// ⚠ `Id` 是写进 `settings.json` 的 `notifyover` 的键：**改了 id 就等于换了一条通知**，
    /// 用户之前勾掉的那条会回到默认。要改就改 `Text`（只影响界面文字）。
    /// </summary>
    internal sealed class NotifyItem
    {
        /// <summary>`settings.json` 里 `notifyover` 用的键（稳定标识，别乱动）。</summary>
        public readonly string Id;
        /// <summary>属于哪个大类（大类在设置页里就是一个「节」）。</summary>
        public readonly ToastKind Kind;
        /// <summary>非 Debug 模式下的默认值：true = 默认开（重要的那几类）。</summary>
        public readonly bool Important;
        /// <summary>设置页里那一行显示的文字。</summary>
        public readonly string Text;

        internal NotifyItem(string id, ToastKind kind, bool important, string text)
        {
            Id = id; Kind = kind; Important = important; Text = text;
        }
    }

    /// <summary>
    /// **全部**通知的清单 —— 有哪几条、各属哪个大类、默认开不开，只有这一份。
    ///
    /// 加一条通知 = ① 在这里 `N("新id", …)` ② 调用点 `Toast.Show(新条目, …)`，别处不用动：
    /// 设置窗口「通知管理」页是按大类遍历 `All` 生成的，新增的自带一个明细勾选框。
    /// ⚠ 新条目默认开不开由 `important` 定：非 Debug 模式下 `true` 才默认开
    ///   （用户：「Debug 模式默认开启全部，非 Debug 模式只默认开启重要的部分」）。
    /// </summary>
    internal static class NotifyItems
    {
        private static NotifyItem N(string id, ToastKind kind, bool important, string text)
        {
            return new NotifyItem(id, kind, important, text);
        }

        // ==================================================================
        // ① 出错与失败（重要 —— 默认开）
        // ==================================================================
        public static readonly NotifyItem OpenOtherVd =
            N("open-othervd", ToastKind.Err, true, "打不开：窗口在别的虚拟桌面");
        public static readonly NotifyItem OpenNoPath =
            N("open-nopath", ToastKind.Err, true, "开不了：这个位置没有真实路径（库 / 虚拟文件夹）");
        public static readonly NotifyItem CopyNoPath =
            N("copy-nopath", ToastKind.Err, true, "复制不了：这个位置没有真实路径");
        public static readonly NotifyItem FavMarkNoPath =
            N("fav-mark-nopath", ToastKind.Err, true, "书签：这些位置没有真实路径，收不了");
        public static readonly NotifyItem FavAddFail =
            N("fav-add-fail", ToastKind.Err, true, "书签：加入失败（带出错信息）");
        public static readonly NotifyItem FavAddNoPath =
            N("fav-add-nopath", ToastKind.Err, true, "加不进书签：这个位置没有真实路径");
        public static readonly NotifyItem FavOpenFail =
            N("fav-open-fail", ToastKind.Err, true, "书签：打不开");
        public static readonly NotifyItem MgrAddFail =
            N("mgr-add-fail", ToastKind.Err, true, "书签管理器：加不了（没路径 / 已经在里面了）");
        public static readonly NotifyItem MgrOpenFail =
            N("mgr-open-fail", ToastKind.Err, true, "书签管理器：打不开");
        public static readonly NotifyItem HistOpenFail =
            N("hist-open-fail", ToastKind.Err, true, "历史记录：打不开");
        public static readonly NotifyItem RestartFail =
            N("restart-fail", ToastKind.Err, true, "重启失败");
        public static readonly NotifyItem AutoStartFail =
            N("autostart-fail", ToastKind.Err, true, "开机自启：改不了启动项");

        // ==================================================================
        // ② 启动与后台状态（重要 —— 默认开）
        // ==================================================================
        public static readonly NotifyItem BgRunning =
            N("bg-running", ToastKind.Sys, true, "程序还在后台运行（「按 Win+E 随时打开」那句）");

        // ==================================================================
        // ③ 操作结果（烦人 —— 非 Debug 默认关）
        // ==================================================================
        public static readonly NotifyItem FavAdded =
            N("fav-added", ToastKind.Ok, false, "已加入书签");
        public static readonly NotifyItem CopyDone =
            N("copy-done", ToastKind.Ok, false, "已复制路径 / 名称 / 文件夹名");
        public static readonly NotifyItem HistCopy =
            N("hist-copy", ToastKind.Ok, false, "已复制历史记录里的条目");
        public static readonly NotifyItem FavBarSwitch =
            N("favbar-switch", ToastKind.Ok, false, "书签栏换了（现在横着那条显示的是哪一个）");

        // ==================================================================
        // ④ 轻提示（烦人 —— 非 Debug 默认关）
        // ==================================================================
        public static readonly NotifyItem NoUndo =
            N("no-undo", ToastKind.Hint, false, "没有可恢复的标签页");
        public static readonly NotifyItem KeepOne =
            N("keep-one", ToastKind.Hint, false, "标签全被选中了，至少得留一个");
        public static readonly NotifyItem FavDup =
            N("fav-dup", ToastKind.Hint, false, "书签：这一项已经在里面了");
        public static readonly NotifyItem FavEmptyFolder =
            N("fav-empty-folder", ToastKind.Hint, false, "书签：这个文件夹里还没有书签");
        public static readonly NotifyItem FavBarRootDel =
            N("favbar-root-del", ToastKind.Hint, false, "书签栏根那一层删不了");

        /// <summary>全部条目 —— 顺序 = 设置页里从上到下的顺序（先按大类，再按这里声明的次序）。</summary>
        public static readonly NotifyItem[] All =
        {
            OpenOtherVd, OpenNoPath, CopyNoPath, FavMarkNoPath, FavAddFail, FavAddNoPath,
            FavOpenFail, MgrAddFail, MgrOpenFail, HistOpenFail, RestartFail, AutoStartFail,
            BgRunning,
            FavAdded, CopyDone, HistCopy, FavBarSwitch,
            NoUndo, KeepOne, FavDup, FavEmptyFolder, FavBarRootDel,
        };

        /// <summary>设置页里大类的先后顺序。</summary>
        public static readonly ToastKind[] KindOrder =
        {
            ToastKind.Err, ToastKind.Sys, ToastKind.Ok, ToastKind.Hint
        };

        private static readonly List<NotifyItem>[] byKind = BuildIndex();

        private static List<NotifyItem>[] BuildIndex()
        {
            List<NotifyItem>[] r = new List<NotifyItem>[KindOrder.Length];
            for (int i = 0; i < r.Length; i++) r[i] = new List<NotifyItem>();
            for (int i = 0; i < All.Length; i++) r[(int)All[i].Kind].Add(All[i]);
            return r;
        }

        /// <summary>这一大类下面那几条（设置页一节的内容）。</summary>
        public static List<NotifyItem> Of(ToastKind k) { return byKind[(int)k]; }

        /// <summary>大类的名字（设置页里那个节的标题）。</summary>
        public static string KindTitle(ToastKind k)
        {
            if (k == ToastKind.Err) return "出错与失败";
            if (k == ToastKind.Sys) return "启动与后台状态";
            if (k == ToastKind.Ok) return "操作结果";
            return "轻提示";
        }

        /// <summary>大类下面那句小字说明。</summary>
        public static string KindNote(ToastKind k)
        {
            if (k == ToastKind.Err) return "打不开 / 复制不了 / 加入失败 这类 —— 建议一直开着";
            if (k == ToastKind.Sys) return "程序自己在后台跑着时的那句提醒";
            if (k == ToastKind.Ok) return "「我干了活」的反馈，看多了烦 —— 默认关";
            return "点空了 / 已经在了 这类 —— 默认关";
        }
    }
}
