namespace CodexIsland
{
    internal static partial class UiLocalization
    {
        private static void HistoryLogsCatalog()
        {
            Map(@"剪贴板历史|Clipboard history
默认暂停；开启后记录新复制的文字和图片。最近 50 项仅保存在内存，收藏会保存在本机。退出后再次开启才会继续记录。|Paused by default. Enable to capture newly copied text and images. The latest 50 items stay in memory; favorites are stored locally. Recording must be enabled again after restarting.
开始记录|Start recording
正在读取收藏，请稍后重试。|Loading favorites. Please try again shortly.
剪贴板监听不可用，请重启应用后重试。|Clipboard listener unavailable. Restart the app and try again.
暂停记录|Pause recording
搜索文字或来源应用|Search text or source application
仅显示收藏|Favorites only
清空未收藏记录|Clear non-favorites
排除应用（进程名，用逗号分隔，例如 KeePass.exe, 1Password.exe）|Excluded applications (comma-separated process names, e.g. KeePass.exe, 1Password.exe)
保存排除列表|Save exclusions
排除列表已保存。|Exclusions saved.
记录已暂停。不会读取当前剪贴板。|Recording paused. The current clipboard is not read.
正在记录新复制的文字和图片。|Recording newly copied text and images.
单条文字最多 65536 字，图片缩至最长边 1600 像素、最多 1 MB；总计最多 8 MB，最多收藏 20 项。系统标记为不允许历史记录的内容会略过。排除应用依据剪贴板来源进程，来源无法识别时请暂停记录。|Up to 65,536 characters per text and 1 MB per image, resized to a maximum edge of 1,600 pixels. Total limit: 8 MB and 20 favorites. Content marked to exclude from history is skipped. Application exclusions use the clipboard owner process; pause recording when the source cannot be identified.
收藏文件无法读取，已保留原文件。新保存时会创建备份。|Favorites could not be read. The original file is preserved and will be backed up before a new save.
没有匹配的剪贴板记录。|No matching clipboard entries.
图片无法显示。|Unable to display image.
取消收藏|Unfavorite
收藏|Favorite
请输入进程名称，例如 KeePass.exe。|Enter a process name, such as KeePass.exe.
最多排除 40 个应用。|Up to 40 applications can be excluded.
最多收藏 20 项。|Up to 20 items can be favorited.
图片内容无效。|Invalid image content.
图片内容过多。|Image content is too large.
图片尺寸过大。|Image dimensions are too large.
图片超过 1 MB，已略过。|Image exceeds 1 MB and was skipped.
日志实时追踪|Live log tracking
选择日志后点击开始。首次读取末尾 64 KB，随后每秒检查新增内容；收起后继续监测，退出应用即停止。|Choose a log and click Start. Reads the final 64 KB first, then checks new content every second. Continues while collapsed and stops when the app exits.
日志文件完整路径（可以尚不存在）|Full log file path (the file need not exist yet)
选择日志文件|Choose log file
提醒关键词（逗号分隔，留空仅查看；每 10 秒最多提醒一次）|Alert keywords (comma-separated; leave blank to view only; at most one alert per 10 seconds)
开始追踪|Start tracking
暂停追踪|Pause tracking
继续追踪|Resume tracking
更新提醒关键词|Update alert keywords
搜索当前日志（不影响关键词提醒）|Search displayed logs (does not affect keyword alerts)
自动滚动到最新|Scroll to latest
清空当前视图|Clear current view
尚未开始追踪，不会自动读取文件。|Not tracking. Files are not read automatically.
支持 UTF-8，以及带 BOM 的 UTF-16 / UTF-32。最多显示 1000 行、512 KB 字符，单行最多 4000 字。文件轮换、截断或暂时不可读时会自动恢复；不会修改日志原文件。|Supports UTF-8 and BOM-marked UTF-16 / UTF-32. Keeps up to 1,000 lines or 512 KB of characters, with 4,000 characters per line. Recovers after rotation, truncation or temporary read errors. Source logs are never modified.
日志与文本|Log and text files
追踪已暂停。|Tracking paused.
开始追踪时会使用这些关键词。|These keywords will be applied when tracking starts.
提醒关键词已更新。|Alert keywords updated.
日志关键词提醒|Log keyword alert
当前视图已清空，原文件未修改。|Current view cleared. The source file is unchanged.
监测已停止。|Monitoring stopped.
正在监测新增日志。|Monitoring new log content.
日志已轮换，正在读取新文件。|Log rotated. Reading the new file.
日志已截断，已重新读取。|Log truncated. Reading from the beginning.
文件增长较快，已跳到末尾。|Log is growing quickly. Jumped to the tail.
文件尚不存在，等待出现…|File not found yet. Waiting for it to appear…
请输入日志文件完整路径。|Enter the full path to a log file.
最多 20 个关键词，每个最多 100 字。|Up to 20 keywords, with at most 100 characters each.");
            Pattern("已记录 {0} 项。", "Recorded {0} items.");
            Pattern("剪贴板监听不可用：{0}", "Clipboard listener unavailable: {0}");
            Pattern("读取收藏失败：{0}", "Could not load favorites: {0}");
            Pattern("本次内容已略过：{0}", "Clipboard content skipped: {0}");
            Pattern("保存收藏失败：{0}", "Could not save favorites: {0}");
            Pattern("无法开始追踪：{0}", "Could not start tracking: {0}");
            Pattern("读取日志失败：{0}", "Could not read log: {0}");
            Pattern("暂时无法读取，将自动重试：{0}", "Temporarily unreadable; will retry: {0}");
        }
    }
}
