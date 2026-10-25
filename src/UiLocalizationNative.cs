using System;

namespace CodexIsland
{
    internal static partial class UiLocalization
    {
        // Windows file filters alternate display labels and matching patterns.
        // Only the display labels belong to the translation catalog.
        internal static string FileFilter(string value)
        {
            if (String.IsNullOrEmpty(value)) return value ?? "";
            string[] parts = value.Split('|');
            for (int i = 0; i + 1 < parts.Length; i += 2) parts[i] = Localize(parts[i]);
            return String.Join("|", parts);
        }

        private static void NativeCatalog()
        {
            Map(@"所有文件|All files
Codex 程序|Codex program
应用与快捷方式|Applications and shortcuts
程序和快捷方式|Programs and shortcuts
JPG / PNG 图片|JPG / PNG images
JPG 图片|JPG images
PNG 图片|PNG images
修改尚未保存|Unsaved changes
当前模块暂时无法保存，已保留窗口和内容。请备份内容，或恢复数据目录权限后重试保存再退出。|This module could not save. The window and content remain open. Back up your content, or restore access to the data folder and retry saving before exiting.
灵动岛未能完成启动：|Side Island could not start: 
灵动岛遇到错误：|Side Island encountered an error: 
已运行的灵动岛暂时未能接收显示请求，请稍后重试。|The running Side Island could not receive the show request. Please try again shortly.
无法启动安装向导，请手动运行 install-notifications.cmd。|The installer could not start. Please run install-notifications.cmd manually.
请从完整发布包运行 install-notifications.cmd 安装通知版。单独的 EXE 可继续监控额度。|Run install-notifications.cmd from the complete release package to install notification support. The standalone EXE can continue monitoring usage limits.
请打开 Windows 设置 → 隐私和安全性 → 通知。|Open Windows Settings > Privacy and security > Notifications.");
        }
    }
}
