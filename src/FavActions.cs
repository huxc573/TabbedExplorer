using System;
using System.Windows.Forms;

namespace TabbedExplorer
{
    /// <summary>
    /// 书签相关的**共用动作** —— 书签栏和书签管理器两边都要的功能写一份，免得两头跑偏。
    ///
    /// 目前就一件事：**「全部打开（N 书签）」**（川 2026-09-22 要的）。
    ///   · 只在**文件夹**（含子文件夹）上有这一条，N 是**递归**数出来的书签数量；
    ///   · N &gt; ConfirmAbove 时先弹一句确认 —— 一口气开一屏标签不是小事，
    ///     右键误触的代价太大（川原话：「超过 7 时需要再次确认避免误触」）。
    ///
    /// 这里只负责「数 + 问」，「开」交给宿主（书签栏 → `EmbedForm`；管理器 → `OpenPath` 事件）
    /// —— 开标签这件事只有装着 explorer 的那个窗口做得了。
    /// </summary>
    internal static class FavActions
    {
        /// <summary>超过这个数就要再确认一次（川定的 7）。</summary>
        public const int ConfirmAbove = 7;

        /// <summary>
        /// 造一条「全部打开（N 书签）」菜单项。N = 0 时是灰的（点了没反应）。
        /// </summary>
        public static PopItem OpenAllItem(IWin32Window owner, FavNode folder, Action<FavNode> run)
        {
            int n = FavStore.CountIn(folder);
            string text = "全部打开（" + n + " 书签）";
            if (n <= 0) return PopMenu.It(text, null);

            return PopMenu.It(text, delegate
            {
                if (n > ConfirmAbove)
                {
                    bool go = Confirm.Ask(owner, "全部打开",
                        "「" + FavStore.NameOf(folder) + "」里有 " + n + " 项书签（含子文件夹），确定全部打开？",
                        "全部打开（" + n + "）");
                    if (!go)
                    {
                        Diag.Step("书签: 全部打开被取消（" + n + " 项）");
                        return;
                    }
                }
                if (run != null) run(folder);
            });
        }
    }
}
