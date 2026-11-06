namespace CodexIsland
{
    internal static partial class UiLocalization
    {
        private static void BuildErrors()
        {
            Map(@"编码内容格式无效，请检查后重试。原文已保留。|Invalid encoded content. Check it and retry; the original text is preserved.
不能除以零。|Cannot divide by zero.
部分提醒无法读取，原文件已保留。请先检查 reminders.json；新增内容会保存当前列表。|Some reminders could not be read. The original file is preserved. Check reminders.json; adding a reminder saves the current list.
部分应用通知设置无法读取；重新保存前会保留原文件备份。|Some notification settings could not be read. The original will be backed up before saving.
场景内容过多，请减少资源。|Workspace content is too large. Reduce the number of resources.
场景数据格式无效。|Invalid workspace data format.
场景文件必须为数组。|The workspace file must contain an array.
场景文件已在外部更改，未覆盖外部版本。请备份修改并重新加载。|The workspace file changed externally. Back up your edits and reload; the external version was not overwritten.
本次场景修改已写入；保存期间出现的外部版本已保留备份。|Your workspace edits were saved. A concurrent external version was preserved in a backup.
备份修改并重新加载|Back up edits & reload
停止启动剩余资源|Stop remaining launches
已停止启动剩余资源。|Remaining launches were stopped.
正在停止启动；已打开的资源不会被关闭。|Stopping remaining launches. Already opened resources will stay open.
目标场景或资源已被移除，此修改未应用。|The workspace or resource was removed. This edit was not applied.
演示模式不读取场景文件。|Demo mode does not read workspace files.
重新加载失败，当前编辑仍保留在本次运行中。|Reload failed. Your current edits remain in this session.
场景已重新加载。|Workspaces reloaded.
原场景文件尚未恢复，未用空列表覆盖。|The original workspace file is still unreadable. It was not replaced with an empty list.
备份或重新加载失败，当前编辑仍保留在本次运行中。|Backup or reload failed. Your current edits remain in this session.
场景文件正在变化，请重试。|The workspace file is changing. Try again.
此时间因夏令时跳转而不存在，请输入另一时间或选择 UTC。|This local time does not exist because of daylight saving. Enter another time or select UTC.
此时间在夏令时回拨时出现两次，请换算并输入明确的 UTC 时间。|This local time occurs twice during the daylight-saving change. Enter an unambiguous UTC time.
存在不支持的字符或多余内容。|Unsupported characters or extra content.
打开灵动岛的任务守望查看。|Open Task watch in Side Island to view it.
打开灵动岛的提醒模块查看。|Open Reminders in Side Island to view it.
打开失败，请检查路径与应用是否可用。|Could not open. Check the path and application.
当地时间不可用|Local time unavailable
当前模块暂时无法保存，已保留窗口和内容。请备份内容，或恢复数据目录权限后重试保存再退出。|This tool could not save. Its window and content remain open. Back up the content, or restore data folder permissions and save before exiting.
电量|Battery
读取 Windows 通知超时，将自动重试|Reading Windows notifications timed out. Retrying automatically.
读取 Windows 通知失败|Could not read Windows notifications
读取已取消|Reading cancelled
端口表大小异常|Unexpected port table size
端口表结构异常|Unexpected port table structure
端口表正在变化，请重试|The port table is changing. Try again.
该时区的日期超出支持范围|The date is outside the supported range for this time zone
唤醒|Awake
换算后的 UTC 时间超出公元 0001 至 9999 年。|The converted UTC time is outside years 0001–9999.
计算失败，请重新选择文件后再试。|Calculation failed. Choose the file again and retry.
结果超出范围或运算无定义。|The result is out of range or undefined.
结果超出支持的日期范围：0001-01-01 至 9999-12-31。|Result is outside the supported date range: 0001-01-01 to 9999-12-31.
结果过长，请分段处理。原文已保留。|The result is too long. Process in sections; the original text is preserved.
截图读取失败。|Could not read the screenshot.
截图未准备好。|The screenshot is not ready.
解码结果不是有效的 UTF-8 文字，原文已保留。|Decoded content is not valid UTF-8 text. The original is preserved.
进程已退出，路径不可用|Process exited; path unavailable
括号不匹配。|Parentheses do not match.
浏览器|Browser
路径名称不能以空格或句点结尾。|Path names cannot end with a space or period.
媒体监控已停止|Media monitoring stopped
媒体控制响应超时|Media control timed out
媒体控制暂不可用，请使用已安装的通知版|Media controls are unavailable. Use the installed notification edition.
每次最多处理 65,536 个文本单元，请分段处理。|Up to 65,536 UTF-16 units per operation. Process in sections.
每个场景都需要 1 至 60 个字符的名称。|Each workspace needs a name with 1–60 characters.
每个场景最多启动 30 个资源。|Launch up to 30 resources per workspace.
名单超过 32,768 个文本单元，请缩短后重试。|The list exceeds 32,768 UTF-16 units. Shorten it and retry.
名单最多允许 500 个非空行（包含重复行）。|The list allows up to 500 nonempty lines, including duplicates.
名单最多允许 500 项。|The list allows up to 500 entries.
其他|Other
切换静默预览|Toggle quiet previews
请从完整发布包运行 install-notifications.cmd 安装通知版。单独的 EXE 可继续监控额度。|Run install-notifications.cmd from the complete release package. The standalone EXE can still monitor quota.
请从应用界面的通知授权按钮开启权限。|Use the app's notification permission button to enable access.
请检查系统权限后重试。|Check Windows permissions and retry.
请框选有效区域，截图最多支持 3200 万像素。|Select a valid region, up to 32 megapixels.
请每次拖入一个文件。|Drop one file at a time.
请输入 256 字以内的算式。|Enter an expression with up to 256 characters.
请输入完整路径，例如 D:\项目\output.zip。|Enter a full path, for example D:\Project\output.zip.
请先选择一个模式。|Select a mode first.
请先选择一个文件。|Select a file first.
请选择 30、60 或 120 分钟。|Choose 30, 60 or 120 minutes.
请选择 JPG 或 PNG 图片。|Choose a JPG or PNG image.
请选择 SHA-256 或 SHA-512。|Choose SHA-256 or SHA-512.
请选择本机文件或文件夹的完整路径。|Choose the full path of a local file or folder.
请选择可用的文字操作。|Choose a supported text operation.
请选择未来的日期和时间|Choose a date and time in the future
请在 Windows 隐私设置中允许灵动岛访问通知。|Allow Side Island to access notifications in Windows privacy settings.
请至少勾选一种字符类型。|Select at least one character type.
区域太小，请重新拖选。|The region is too small. Select again.
取消固定展开|Unpin expanded view
取消麦克风静音|Unmute microphone
取消输出静音|Unmute output
取消守望|Cancel watch
全部|All
权限检查在后台进行，其他工具可继续使用。|Permissions are being checked in the background. Other tools remain available.
任务守望已完成|Watch task completed
日历|Calendar
日期超出支持范围|Date outside supported range
日期请使用 yyyy-MM-dd，例如 2026-10-22。|Use yyyy-MM-dd for the date, for example 2026-10-22.
入口已保存|Shortcut saved
入口已不可用，请重新添加。|The shortcut is unavailable. Add it again.
设置失败，请检查音频设备|Could not change settings. Check the audio device.
设置未能保存，请检查目录权限|Could not save settings. Check folder permissions.
时间戳超出支持的范围：公元 0001 至 9999 年。|Timestamp outside the supported range: years 0001–9999.
时间请使用 yyyy-MM-dd HH:mm:ss，可附加 .fff 毫秒；时区由下方选择。|Use yyyy-MM-dd HH:mm:ss, optionally followed by .fff milliseconds. Select the time zone below.
收纳引用保存失败，请检查本地目录权限。|Could not save file references. Check local folder permissions.
数字或算式不完整。|The number or expression is incomplete.
刷新状态|Refresh status
算式嵌套过深。|The expression is nested too deeply.
所选时区不可用。|The selected time zone is unavailable.
提醒数据无效|Invalid reminder data
通知授权未完成|Notification permission incomplete
通知同步已停止。|Notification sync stopped.
未读|Unread
未能请求通知访问，请在系统设置中检查权限。|Could not request notification access. Check Windows permissions.
未找到 Codex。请先安装 Codex 桌面版或 CLI，或在设置中指定程序路径。|Codex was not found. Install the desktop app or CLI, or specify its executable in settings.
文件在计算期间发生变化，请重试。|The file changed during calculation. Try again.
文字包含不完整的 Unicode 字符，原文已保留。|Text contains an incomplete Unicode character. The original is preserved.
无法打开当前用户的启动设置。|Could not open startup settings for this user.
无法读取屏幕。|Could not read the screen.
无法读取文件：可能已移动、被占用或设备暂不可用。|Could not read the file. It may have moved, be in use, or its device may be unavailable.
无法读取文件：请检查权限或是否选择了文件夹。|Could not read the file. Check permissions and ensure it is not a folder.
无法读取选中区域。|Could not capture the selected region.
无法访问图片或保存位置，请选择其他路径。|Could not access the image or save location. Choose another path.
无法检查通知权限|Could not check notification permission
无法启动安装向导，请手动运行 install-notifications.cmd。|Could not start the installer. Run install-notifications.cmd manually.
无可查询的用户进程路径|No queryable user process path
无权限读取受保护进程路径|No permission to read protected process path
系统 / 已结束连接|System / closed connection
系统随机数暂不可用，请稍后重试。|System random generator unavailable. Try again shortly.
修改尚未保存|Unsaved changes
需要 Windows 10 1607 或更新版本。|Requires Windows 10 version 1607 or later.
已绑定|Bound
已运行的灵动岛暂时未能接收显示请求，请稍后重试。|The running Side Island instance could not receive the show request. Try again shortly.
已暂停|Paused
音乐|Music
应用通知设置暂时无法读取；重新保存前会保留原文件备份。|Notification settings could not be read. The original will be backed up before saving.
原入口文件部分或全部无法读取；保存修改前会先保留原文件备份。|Some or all shortcuts could not be read. The original will be backed up before saving changes.
原收纳文件部分或全部无法读取；修改保存前会先保留备份。|Some or all shelf entries could not be read. The original will be backed up before saving changes.
原文件部分或全部无法读取；保存修改前会先保留原文件备份。|Some or all data could not be read. The original will be backed up before saving changes.
暂时无法读取鼠标位置。|Could not read the pointer position.
找不到指定的 Codex 程序，请选择 codex.exe 或已安装的 npm 启动脚本。|The specified Codex executable was not found. Choose codex.exe or an installed npm launcher.
正在检查路径…|Checking path…
正在检查状态…|Checking status…
支持 sqrt、abs、round、pi、e。|Supports sqrt, abs, round, pi and e.
自定义|Custom
Codex 额度提醒|Codex quota alert
JSON 嵌套超过 48 层，请简化后重试。原文已保留。|JSON is nested beyond 48 levels. Simplify it; the original is preserved.
URL 中的 % 后必须有两位十六进制数字，原文已保留。|Each % in the URL must be followed by two hexadecimal digits. The original is preserved.
Windows 未返回通知状态，将自动重试。|Windows returned no notification status. Retrying automatically.
Windows 无法打开此资源，请检查关联程序或应用安装。|Windows could not open this resource. Check its associated app or installation.
保存失败，原入口保持不变。请检查本地目录权限后重试。|Could not save. Existing shortcuts are unchanged. Check local folder permissions and retry.
保存失败；修改仍保留在本次运行中，稍后可重试。|Could not save. Changes remain for this session. Retry later.
保持唤醒必须在发起请求的同一线程上停止。|Keep awake must be stopped on the thread that started it.
函数参数需要括号。|Function arguments need parentheses.
 · 保留重复行| · Duplicates kept
 · 保留最近 10 次计次| · Last 10 laps kept
 · 部分数据| · Partial data
 · 此类型仅定位| · Show location only for this type
 · 过长行的预览已截短为 2,000 字符。| · Long preview lines are truncated to 2,000 characters.
 · 完整差异已统计，预览仅显示前 600 行。| · Complete differences counted; preview limited to the first 600 rows.
 · 上次同步数据| · Last synced data
 · 未发现本地占用| · No local owner found
 · 已读取的协议中未发现占用（部分读取失败）| · No owner in the protocols read (some reads failed)
本地时间 · |Local time · 
本地时区：|Local time zone: 
状态 |Status ");
            Add("场景草稿备份：", "Workspace draft backup: ");
            Pattern("重新加载失败，当前编辑仍保留在本次运行中。\n场景草稿备份：{0}", "Reload failed. Your current edits remain in this session.\nWorkspace draft backup: {0}");
            Pattern("场景已重新加载。\n场景草稿备份：{0}", "Workspaces reloaded.\nWorkspace draft backup: {0}");
            Pattern("本次场景修改已写入；保存期间出现的外部版本已保留备份。\n{0}", "Your workspace edits were saved. A concurrent external version was preserved in a backup.\n{0}");
            Pattern("{0} 天", "{0} days"); Pattern("{0} 天 {1} 时 {2} 分", "{0}d {1}h {2}m"); Pattern("{0} 小时 {1} 分", "{0}h {1}m");
            Pattern("已收纳 {0} 项", "{0} items on the shelf");
            Pattern("{0} 个非空行 · {1} 个可选项 · 已合并 {2} 个重复行", "{0} nonempty lines · {1} choices · {2} duplicate lines merged");
            Pattern("{0} 个非空行 · {1} 个可选项 · 保留重复行", "{0} nonempty lines · {1} choices · Duplicate lines kept");
            Pattern("端口必须是 1—65535 的整数：{0}", "Port must be an integer from 1 to 65535: {0}");
            Pattern("JSON 格式无效（第 {0} 个文本单元附近）。原文已保留。", "Invalid JSON (near UTF-16 unit {0}). Original text preserved.");
            Pattern("{0}须为 −2,147,483,648 至 2,147,483,647 的整数。", "{0} must be an integer from −2,147,483,648 to 2,147,483,647.");
            Pattern("抽取数量须为 1 至 {0}，且不能超过可选项数。", "Draw count must be from 1 to {0}, and no greater than the number of choices.");
        }
    }
}
