namespace CodexIsland
{
    internal static partial class UiLocalization
    {
        private static void SettingsBackupCatalog()
        {
            Map(@"设置备份与迁移|Settings backup and migration
导出布局、手势、快捷键、通知规则及音乐偏好。备份不包含账户路径、剪贴板、便签和文本模板；导入保留当前账户路径和开机启动设置。|Export layouts, gestures, hotkeys, notification rules and music preferences. Backups exclude account paths, clipboard content, notes and text templates. Import preserves this computer's account paths and startup setting.
导出偏好设置|Export preferences
选择并预览备份|Choose and preview a backup
安排下次启动时导入|Import on next startup
取消待导入设置|Cancel pending import
导入在退出后再次启动时应用，避免当前运行的旧设置覆盖导入结果。原偏好设置会保留一份本地恢复副本。|Imports apply after closing and reopening the app, so the running instance cannot overwrite them with older preferences. A local recovery copy preserves the original files.
已安排导入，退出后重新打开灵动岛生效。|Import scheduled. Close and reopen Side Island to apply it.
偏好设置已导出。|Preferences exported.
将导入以下偏好文件：|The following preference files will be imported:
待导入已取消。|Pending import cancelled.
演示模式不会读写本机偏好设置。|Demo mode does not read or write local preferences.
Side Island 偏好设置|Side Island preferences
偏好设置操作失败，请检查文件与目录权限。|The preference operation failed. Check file and folder permissions.
偏好设置字段、类型或范围无效。|A preference field, type or value is invalid.
备份格式或版本无效。|Invalid backup format or version.
备份超过 1 MB 限制。|The backup exceeds the 1 MB limit.
备份包含不支持的文件或大小超限。|The backup contains an unsupported file or exceeds a size limit.
设置内容必须是 JSON 对象。|Preference content must be a JSON object.
设置文件超过大小限制。|The preference file exceeds the size limit.
读取设置期间文件发生变化。|The preference file changed while being read.
请选择应用数据目录以外的位置，避免覆盖当前偏好设置。|Choose a location outside the application data folder to avoid overwriting current preferences.
上次导入尚未完成，请重新启动后重试。|The previous import is unfinished. Restart the app and try again.
导入未完成，部分文件无法恢复；原文件恢复副本已保留，请重新启动以重试恢复。|Import failed and some files could not be restored. The original recovery copy remains available. Restart to retry recovery.
备份路径不能使用符号链接或目录联接，请选择普通文件夹。|Choose a regular folder. Backup paths cannot use symbolic links or directory junctions.
原文件恢复副本与待导入事务不匹配，请保留文件并检查数据目录。|The original recovery copy does not match the pending import. Keep the files and check the data folder.");
        }
    }
}
