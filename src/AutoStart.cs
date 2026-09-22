using System;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TabbedExplorer
{
    /// <summary>
    /// 开机自启（2026-09-22 川要的设置项）。
    ///
    /// 实现就一行注册表：`HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 里的 `TabbedExplorer` 值。
    /// 写的是**当前用户**的启动项，不需要管理员权限，也不碰 HKLM、不建计划任务。
    ///
    /// ⚠ 状态**只认注册表**，不在 `settings.json` 里再存一份：
    /// 两份状态迟早会对不上（川自己在「任务管理器 → 启动」里禁用一个项，注册表值还在、
    /// 但开不起来了），那时菜单里打的勾就是假的。菜单每次现问 `IsEnabled()` 最省事也最准。
    ///
    /// 启动命令行带 `--tray`：开机只**常驻托盘 + 装 Win+E 钩子**，不弹窗口
    /// （窗口等第一次 Win+E 才出现，见 Program / DesktopHub）。
    /// </summary>
    internal static class AutoStart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "TabbedExplorer";

        /// <summary>写进启动项的完整命令行（exe 全路径 + `--tray`）。拿不到 exe 路径时返回 null。</summary>
        public static string CommandLine
        {
            get
            {
                string exe = ExePath();
                if (string.IsNullOrEmpty(exe)) return null;
                // 路径一定要带引号：川的目录名里带空格（D:\Dev\Workspaces\...）就跑不起来了
                return "\"" + exe + "\" --tray";
            }
        }

        public static string ExePath()
        {
            try
            {
                Assembly a = Assembly.GetEntryAssembly();
                if (a != null && !string.IsNullOrEmpty(a.Location)) return a.Location;
            }
            catch { }
            try { return Application.ExecutablePath; } catch { }
            return null;
        }

        /// <summary>现在是不是开着（启动项里有这个值）。</summary>
        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (k == null) return false;
                    return k.GetValue(ValueName) != null;
                }
            }
            catch (Exception ex)
            {
                Diag.Log("AutoStart: 读注册表失败 " + ex.Message);
                return false;
            }
        }

        /// <summary>设成开 / 关。返回 true = 注册表真的写成功了（调用方据此报成功或失败）。</summary>
        public static bool Set(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (k == null) return false;
                    if (on)
                    {
                        string cmd = CommandLine;
                        if (string.IsNullOrEmpty(cmd)) return false;
                        k.SetValue(ValueName, cmd, RegistryValueKind.String);
                        Diag.Step("AutoStart: 已写入启动项 " + cmd);
                    }
                    else
                    {
                        // 本来就没有这个值也算成功（幂等），别让川看到「改不了」
                        k.DeleteValue(ValueName, false);
                        Diag.Step("AutoStart: 已移除启动项");
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Diag.Log("AutoStart: 写注册表失败 " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 开着的话，把启动项里的路径刷新成「现在这个 exe」—— 程序目录被挪过也能对上。
        /// 启动时调一次；没开就什么都不做（绝不替川打开）。
        /// </summary>
        public static void Sync()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    object v = k.GetValue(ValueName);
                    if (v == null) return;
                    string want = CommandLine;
                    if (string.IsNullOrEmpty(want)) return;
                    if (string.Equals(Convert.ToString(v), want, StringComparison.OrdinalIgnoreCase)) return;
                    k.SetValue(ValueName, want, RegistryValueKind.String);
                    Diag.Step("AutoStart: 启动项路径已刷新 -> " + want);
                }
            }
            catch (Exception ex) { Diag.Log("AutoStart: sync 失败 " + ex.Message); }
        }
    }
}
