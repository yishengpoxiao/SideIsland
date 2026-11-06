using System;

namespace CodexIsland
{
    internal static partial class UiLocalization
    {
        private static void BuildCatalog()
        {
            Map(@"灵动岛|Side Island
消息|Messages
微信|WeChat
状态|Status
工具|Tools
设置|Settings
退出|Exit
关闭|Close
确定|OK
缩放|Zoom
导出启动诊断|Export startup diagnostics
文本文件|Text files
选择 Codex 程序|Choose the Codex executable
取消|Cancel
保存|Save
复制|Copy
粘贴|Paste
添加|Add
移除|Remove
删除|Delete
浏览|Browse
编辑|Edit
应用|Apply
完成|Done
打开|Open
恢复|Resume
暂停|Pause
开始|Start
结束|Stop
重置|Reset
刷新|Refresh
重试保存|Retry saving
重新检查|Check again
清空|Clear
清空结果|Clear results
清空文本|Clear text
清空引用|Clear references
清除已完成|Clear completed
移除已完成|Remove completed
所在位置|Show location
复制路径|Copy path
复制结果|Copy result
复制数值|Copy value
结果作为输入|Use result as input
立即刷新|Refresh now
立即保存|Save now
保存设置|Save settings
打开设置|Open settings
模块与外观设置|Tools and appearance
显示灵动岛|Show Side Island
展开 / 收起|Expand / collapse
固定展开|Keep expanded
立即收起（Esc）|Collapse (Esc)
暂时隐藏（继续监控）|Hide temporarily (keep monitoring)
恢复默认展开大小|Reset expanded size
恢复显示|Show again
停靠左侧|Dock left
停靠右侧|Dock right
停靠上方|Dock top
停靠下方|Dock bottom
开启最近边缘吸附|Snap to nearest edge
自由摆放（关闭吸附）|Free placement (disable snapping)
收起文字方向|Compact text direction
自动正向|Automatic
旋转 180°|Rotate 180°
左右镜像|Mirror horizontally
消息、状态，抬眼即见|Messages & status
工具箱|Toolbox
日常小事，一座岛就够|Everyday tools, all on one island
切换工具|Switch tool
搜索工具名称|Search tools
输入名称或用途筛选 · Ctrl + K 搜索 · Enter 打开\n可直接输入：专注 45 分钟 / 查端口 8080 / 生成二维码 网址 / 复制模板 名称|Filter by name or purpose · Ctrl + K to search · Enter to open\nCommands: focus 45 min / port 8080 / qr URL / template name
没有匹配的工具，可清空关键词或检查模块设置。|No tools match. Clear the search or check tool settings.
输入名称筛选 · Ctrl + K 搜索 · Enter 打开|Filter by name · Ctrl + K to search · Enter to open
☆ 固定当前工具|☆ Pin current tool
★ 取消固定当前工具|★ Unpin current tool
最多固定 6 个常用工具；也可右键工具按钮固定或移除。|Pin up to 6 favorite tools. Right-click a tool to pin or remove it.
常用工具 · 右键可移除|Favorite tool · Right-click to remove
固定到常用工具|Add to favorites
从常用工具移除|Remove from favorites
常用工具最多 6 个，请先右键移除一个。|Up to 6 favorites. Right-click one to remove it first.
所有模块已隐藏，可在下方设置中重新开启。|All tools are hidden. Enable them in settings below.
所有模块已隐藏，可在模块与外观设置中重新开启。|All tools are hidden. Enable them in Modules & appearance.
部分快捷键被其他应用占用，可继续使用托盘菜单。|Some shortcuts are used by another app. The tray menu is still available.
Ctrl + Alt + Space 展开 / 收起 · Ctrl + Alt + P 隐私模式|Ctrl + Alt + Space expand / collapse · Ctrl + Alt + P privacy
Codex Island · 设置|Side Island · Settings
Codex Island · 灵动岛|Side Island
Codex Island · 后台监控|Side Island · Monitoring
Codex Island · 连接待恢复|Side Island · Reconnecting
让灵动岛停在顺手的位置|Make the island feel at home
语言|Language
语言 / Language|语言 / Language
界面语言|Interface language
中文|中文
English|English
外观|Appearance
主题|Theme
亮色|Light
暗色|Dark
亮色模式|Light mode
暗色模式|Dark mode
切换语言|Switch language
切换亮暗模式|Toggle light / dark
消息与通知|Messages and notifications
汇集 Windows 通知（需要系统授权）|Collect Windows notifications (permission required)
新消息到来时短暂展开预览|Briefly expand when new messages arrive
隐私模式：隐藏发送者和消息正文|Privacy: hide senders and message text
静默预览：继续收消息，暂不展开预览|Quiet previews: receive messages without expanding
微信、QQ 等应用需发送系统通知；消息历史仅保留在本次运行的内存中。|WeChat, QQ and other apps must send Windows notifications. History is held in memory for this session only.
停靠位置|Dock position
屏幕左侧|Left edge
屏幕右侧|Right edge
屏幕上方|Top edge
屏幕下方|Bottom edge
自由位置（拖动调整）|Free position (drag to move)
拖动收起的灵动岛后，吸附最近的屏幕边缘|Snap to the nearest screen edge after dragging
松手时自动选择上、下、左、右边缘；关闭后可自由摆放。|Release to snap to the nearest edge. Disable this for free placement.
显示器|Display
主显示器（自动）|Primary display (automatic)
收起时的主内容|Compact content
收起时的文字方向|Compact text direction
只影响收起状态，展开面板始终保持正常阅读方向。|Only affects the compact view. Expanded content keeps its normal reading direction.
窗口保持置顶|Always on top
窗口不透明度|Window opacity
工具箱显示模块|Visible tools
隐藏专注或提醒模块不会停止计时和待触发的提醒。提醒需要程序运行。快捷键：Ctrl+Alt+Space 展开，Ctrl+Alt+P 隐私模式。|Hiding focus or reminders does not stop timers. Reminders require the app to stay running. Ctrl+Alt+Space expands; Ctrl+Alt+P toggles privacy.
自动刷新频率|Refresh interval
30 秒|30 seconds
60 秒|60 seconds
2 分钟|2 minutes
5 分钟|5 minutes
10 分钟|10 minutes
鼠标移开后自动收起|Collapse when the pointer leaves
登录 Windows 时启动|Start when signing in to Windows
低额度提醒阈值|Low quota alert threshold
Codex 程序路径（留空自动查找）|Codex executable (blank for automatic detection)
Codex 数据目录（留空自动查找）|Codex data folder (blank for automatic detection)
使用该目录中已有的 Codex 登录状态。若额度无法读取，请先登录 Codex，或指定已登录账户的数据目录。|Uses the existing Codex sign-in in this folder. Sign in to Codex or select a signed-in account's data folder if quota cannot be read.
所选 Codex 程序不存在。|The selected Codex executable does not exist.
所选 Codex 数据目录不存在。|The selected Codex data folder does not exist.
无法修改登录启动项，请检查系统权限。|Could not change startup settings. Check Windows permissions.
选择包含 Codex 登录状态的目录|Select the folder containing the Codex sign-in
收起时的第一个组件|First compact widget
收起时的第二个组件|Second compact widget
组件一|Widget 1
组件二|Widget 2
左侧组件|First widget
右侧组件|Second widget
Codex 额度|Codex quota
短期额度|Short-term quota
长期额度|Long-term quota
每周额度|Weekly quota
短期|Short-term
长期|Long-term
额度|Quota
剩余 %|Remaining %
重置时间未知|Reset time unknown
等待同步新的额度|Waiting for refreshed quota
当前账户未提供此周期|This quota window is unavailable for this account
额度暂不可用|Quota unavailable
数据待同步|Awaiting refresh
正在获取额度|Reading quota
刷新额度|Refresh quota
未同步|Not synced
●  等待连接 Codex|●  Waiting for Codex
●  正在连接 Codex…|●  Connecting to Codex…
●  正在连接本机 Codex…|●  Connecting to local Codex…
●  数据待同步 · 显示上次获取的额度|●  Awaiting refresh · Showing last known quota
●  当前账户未返回可用的额度信息|●  No quota information is available for this account
●  演示数据 · 仅用于预览界面|●  Demo data · Interface preview only
暂时无法读取额度，请检查 Codex 设置。|Could not read quota. Check Codex settings.
Codex 返回的额度数据格式不正确。|Codex returned an invalid quota format.
检查超时 · 稍后点击刷新重试|Check timed out · Try refreshing later
●  检查通知连接|●  Check notification connection
●  正在检查通知连接…|●  Checking notifications…
●  系统通知已连接|●  Windows notifications connected
●  等待通知访问授权|●  Waiting for notification permission
●  通知访问未获允许|●  Notification access is not allowed
●  通知汇集已关闭|●  Notification collection is off
●  安装通知版后即可连接|●  Install the notification edition to connect
●  此系统不支持通知读取|●  This Windows version cannot read notifications
●  通知连接暂不可用|●  Notification connection unavailable
●  通知同步暂时失败|●  Notification sync temporarily failed
●  正在同步系统通知…|●  Syncing Windows notifications…
●  演示模式 · 消息为虚构示例|●  Demo mode · Fictional messages
连接系统通知|Connect Windows notifications
安装通知版|Install notification edition
重新同步通知|Resync notifications
打开通知权限设置|Open notification permissions
查看消息预览示例|Preview a sample notification
清空岛内记录|Clear island history
管理应用|Manage apps
最近通知|Recent notifications
新通知|New notification
未读消息|Unread messages
岛内已读|Read in the island
只清除灵动岛未读标记，不改变微信或 QQ 的聊天状态|Clears island unread markers; does not change chat read status
只清空本次运行的岛内历史，不删除系统通知|Clears this session's island history; Windows notifications stay available
收到一条新通知|A new notification arrived
消息内容已隐藏|Message content hidden
此刻安静，消息来了会告诉你|All quiet. New messages will appear here.
应用需开启系统通知。聊天软件自己的独立弹窗可能无法接入。|Enable Windows notifications in each app. Custom chat popups may not be accessible.
微信、QQ 和其他应用的通知汇集在这里。|Notifications from WeChat, QQ and other apps appear here.
可在设置中开启，额度监控仍会继续。|Enable in settings. Quota monitoring continues.
自动汇集微信、QQ 等应用发出的系统通知。|Collects Windows notifications sent by WeChat, QQ and other apps.
点击下方连接，并在 Windows 提示中允许访问。|Connect below and allow access in the Windows prompt.
Windows 要求通知访问使用已注册的应用。安装后，在这里授权连接。|Windows notification access requires a registered app. Install it, then allow access here.
通知读取未完成，稍后会自动重试。|Notification reading has not finished. Retrying automatically.
通知连接暂不可用，将自动重试；也可检查系统通知访问权限。|Notification connection unavailable. Retrying automatically; check Windows permissions if needed.
上次通知读取仍在结束处理中，已暂缓重试；稍后会自动恢复。|The previous notification read is still closing. Retrying when it finishes.
上次通知连接仍未结束，已暂缓重试；其他工具可继续使用。|The previous connection is still closing. Other tools remain available.
连接 Windows 通知超时，将自动重试；其他工具可继续使用。|Windows notification connection timed out. Retrying; other tools remain available.
首次读取正在后台进行，其他工具可继续使用。|The first read is running in the background. Other tools remain available.
稍后会自动重试，也可检查系统通知访问权限。|Retrying automatically. You can also check Windows notification access.
请打开 Windows 设置 → 隐私和安全性 → 通知。|Open Windows Settings → Privacy & security → Notifications.
管理应用通知 · 灵动岛|App notifications · Side Island
选择灵动岛显示的通知|Choose notifications shown in the island
选择哪些软件的通知在灵动岛内显示；不改变 Windows 或软件自己的通知设置|Choose which app notifications appear here. Windows and app notification settings stay the same.
搜索软件名称或应用标识|Search app names or identifiers
搜索应用名称或标识|Search app names or identifiers
只看已禁用|Disabled apps only
应用通知|App notifications
没有符合条件的应用。|No apps match the filter.
允许在岛内显示|Show in the island
已在岛内禁用|Disabled in the island
屏蔽此应用|Block this app
禁用通知|Disable notifications
禁用后，该软件的通知、未读数量和消息预览都会从岛内隐藏。Windows 和软件本身的通知不受影响。|Disabling hides this app's notifications, unread count and previews here. Windows and the app's own notifications stay unchanged.
更改立即生效并自动保存。恢复后只提醒新通知；示例应用的开关不保存。|Changes apply immediately and save automatically. Re-enabling alerts only for new messages. Demo app choices are not saved.
收到软件的系统通知后，它会出现在这里。\n也可以直接在消息卡片上屏蔽应用。|Apps appear here after a Windows notification arrives.\nYou can also block an app from its notification card.
更改已在本次运行生效，但暂时无法保存。请检查数据目录权限。|Changes apply for this session but could not be saved. Check data folder permissions.
无应用标识 · 按此名称匹配|No app identifier · Matched by this name
未知应用|Unknown app
未知|Unknown
♫ 媒体与声音|♫ Media and sound
媒体控制与音量|Media controls and volume
音乐与声音|Music and sound
暂无正在播放的媒体|Nothing is playing
打开音乐或视频应用后自动显示|Open a music or video app to show media here
打开支持 Windows 媒体控制的音乐或视频应用|Open an app that supports Windows media controls
未发现媒体会话|No media session found
没有可控制的媒体|No controllable media
未提供媒体标题|No media title
未提供作者|No artist provided
正在获取媒体状态|Reading media status
系统媒体|System media
播放|Play
上一首|Previous
下一首|Next
正在播放|Playing
就绪 / 已暂停|Ready / paused
已暂停 / 已停止|Paused / stopped
此媒体未提供时长|Duration unavailable
系统音量|System volume
静音输出|Mute output
默认输出设备|Default output device
未检测到默认输出设备|No default output device
默认输出已静音|Default output muted
正在检测音频设备|Detecting audio devices
静音麦克风|Mute microphone
未检测到默认麦克风|No default microphone
正在检测默认通信麦克风|Detecting default communications microphone
默认通信麦克风已静音|Default communications microphone muted
默认通信麦克风未静音|Default communications microphone unmuted
控制 Windows 默认通信麦克风；其他输入设备请在应用内设置。|Controls the default Windows communications microphone. Configure other inputs in their apps.
媒体应用需支持 Windows 系统媒体控制。|The media app must support Windows media controls.
当前应用不支持此控制|This app does not support that control
应用未接受此控制|The app did not accept this control
已发送媒体控制|Media command sent
音量已应用|Volume applied
默认输出静音状态已切换|Output mute toggled
默认通信麦克风状态已切换|Microphone mute toggled
媒体会话已改变，请稍后重试|Media session changed. Try again shortly.
媒体会话暂不可用，请稍后重试|Media session unavailable. Try again shortly.
媒体或音频设备暂不可用，稍后重试|Media or audio device unavailable. Try again shortly.
媒体响应超时，稍后重试|Media response timed out. Try again shortly.
音量设置失败，请检查音频设备|Could not set volume. Check the audio device.
◷ 专注计时|◷ Focus timer
番茄钟 / 倒计时 / 秒表|Pomodoro / countdown / stopwatch
专注与计时|Focus and timers
专注计时|Focus timer
专注|Focus
休息|Break
长休|Long break
倒计时|Countdown
秒表|Stopwatch
专注 25 分|Focus 25 min
休息 5 分|Break 5 min
长休 15 分|Long break 15 min
开始 / 继续|Start / resume
计次|Lap
准备开始|Ready to start
未开始|Not started
未开启|Off
收起灵动岛或切换页面，计时仍会继续。|Timers continue when the island is collapsed or another page is open.
倒计时包含电脑休眠时间；到点后提醒一次。\n切换预设会重置当前计时。退出应用会结束计时。|Countdown includes time spent asleep and alerts once.\nChanging the preset resets the timer. Exiting the app stops it.
请输入 1—1440 分钟|Enter 1–1440 minutes
分钟|Minutes
时间到了，准备开始下一件事吧。|Time is up. Ready for the next task?
站起来活动一下|Time to stretch
这一轮专注已完成，起来活动一下吧。|Focus session complete. Stand up and stretch.
☷ 待办便签|☷ To-dos and notes
待办与便签|To-dos and notes
待办|To-dos
随手记|Notes
仅保存在这台电脑；输入后自动保存。|Stored on this computer only. Saves automatically as you type.
输入待办，按 Enter 添加（最多 30 项）|Enter a to-do and press Enter (up to 30)
Enter 添加 · 勾选完成 · × 删除|Enter to add · Check to complete · × to delete
便签仅本机保存，最多 6000 字符|Local notes, up to 6,000 characters
自动保存已启用|Automatic saving enabled
写下一件小事，从这里开始。|Write down one small task to get started.
删除这项待办|Delete this to-do
先写下要做的事。|Enter a task first.
已到 30 项上限，请先清理已完成的待办。|The 30-task limit is reached. Clear completed tasks first.
正在编辑…|Editing…
已恢复保存。|Saving restored.
保存失败，内容仍在内存中。请稍后点“立即保存”。|Could not save. Content remains in memory. Click Save now to retry.
◴ 提醒闹钟|◴ Reminders
提醒与闹钟|Reminders and alarms
提醒闹钟|Reminders
到点提醒，不必一直记着|A reminder when you need it
写好标题，选择多久以后提醒|Enter a title and choose when to be reminded
提醒标题（最多 80 字）|Reminder title (up to 80 characters)
10 分钟后再提醒|Remind me in 10 minutes
15 分钟后开始|Start in 15 minutes
或指定日期与时间（24 小时制）|Or choose a date and time (24-hour clock)
时间：HH:mm|Time: HH:mm
每天重复（从上述日期开始）|Repeat daily (starting on the date above)
添加指定时间提醒|Add scheduled reminder
提醒保存在本机，关闭应用后不会触发。|Reminders are stored locally and require the app to be running.
应用运行时提醒；休眠或重启后，过期提醒只补一次。每日提醒按本地时间重复。|Alerts while the app runs. After sleep or restart, overdue reminders alert once. Daily reminders use local time.
暂停后恢复：过期单次提醒改为 10 分钟后；每日提醒从下一个时刻继续。夏令时跳过的时刻会顺延，重复的时刻只提醒一次。|Resuming an overdue one-time reminder schedules it 10 minutes later; daily reminders resume at the next time. Daylight-saving gaps move forward; repeated times alert once.
还没有提醒。可以先为喝水、会议或休息设一个。|No reminders yet. Try one for water, a meeting or a break.
请输入 1—80 字的提醒标题|Enter a title with 1–80 characters
提醒时间需在 1 分钟到 1 年之间|Choose a time from 1 minute to 1 year away
最多保留 30 条提醒，请先删除已完成的提醒|Up to 30 reminders. Remove completed reminders first.
请按 yyyy-MM-dd 和 HH:mm 输入日期时间，例如 2026-10-23、09:30。|Enter the date as yyyy-MM-dd and time as HH:mm, for example 2026-10-23 and 09:30.
已添加提醒|Reminder added
已删除提醒|Reminder deleted
提醒已恢复|Reminder resumed
提醒已暂停|Reminder paused
提醒到点|Reminder due
提醒到时间了|A reminder is due
已恢复，改为 10 分钟后提醒|Resumed; reminder moved to 10 minutes from now
已恢复，从下一个每日时刻开始|Resumed from the next daily occurrence
提醒已到点；已更新下一次提醒状态。|Reminder due; the next occurrence has been updated.
尚未保存：请检查本地目录权限或磁盘空间。到期提醒将在保存成功后发出。|Not saved. Check folder permissions or disk space. Due reminders alert after saving succeeds.
◷ 日期与时区|◷ Dates and time zones
世界时钟 / 日期 / 时间戳|World clocks / dates / timestamps
跨时区，也算得明白|Dates and times across time zones
世界时钟|World clocks
日期差|Date difference
日期加减|Add / subtract days
Unix 时间戳|Unix timestamp
起始日期 · yyyy-MM-dd|Start date · yyyy-MM-dd
开始日期 · yyyy-MM-dd|Start date · yyyy-MM-dd
结束日期 · yyyy-MM-dd|End date · yyyy-MM-dd
两端都计入（含起始日和结束日）|Include both start and end dates
只计周一至周五|Weekdays only (Monday–Friday)
交换日期|Swap dates
默认不计起始日、计入结束日；同一天默认算 0 天。周一至周五计算不扣除法定节假日，也不计周末调休。|By default, excludes the start and includes the end; the same day is 0 days. Weekday mode does not account for public holidays or weekend workdays.
加减天数 · 正数向后，负数向前|Days to add · Positive moves forward; negative moves back
起始日不计为第 1 天；输入 0 保持原日期。只计周一至周五时，不扣除法定节假日，也不计周末调休。|The starting day is not day 1; 0 keeps the date. Weekday mode does not account for holidays or weekend workdays.
时间 → 时间戳|Date/time → timestamp
时间戳 → 时间|Timestamp → date/time
Unix 时间戳 · 整数，可为负数|Unix timestamp · Integer, including negatives
时间戳单位（不会自动猜测）|Timestamp unit (selected explicitly)
以上输入使用的时区|Time zone for the input above
秒（s）|Seconds (s)
毫秒（ms）|Milliseconds (ms)
本地时区|Local time zone
填入当前时间|Use current time
按 Windows 时区规则自动处理夏令时；仅面板可见时刷新。|Uses Windows time zone and daylight-saving rules. Refreshes only while visible.
Unix 起点为 1970-01-01 00:00:00 UTC。秒结果向下取整；本地夏令时不存在或重复的时间会提示错误。|Unix epoch: 1970-01-01 00:00:00 UTC. Seconds are rounded down. Ambiguous or nonexistent local times show an error.
不计起始日、计入结束日|Start excluded, end included
两端都计入|Both dates included
北京|Beijing
东京|Tokyo
伦敦|London
纽约|New York
本地|Local
此设备未提供该时区|This time zone is unavailable on this device
结束日期不能早于开始日期，可点击交换日期。|The end date must not precede the start date. Try Swap dates.
天数请输入整数，负数表示向前推算。|Enter a whole number of days; a negative value moves back.
时间戳请输入整数；请明确选择秒或毫秒。|Enter an integer timestamp and explicitly choose seconds or milliseconds.
请先输入有效内容。|Enter valid content first.
请先输入可换算的数值。|Enter a value to convert first.
↗ 快捷启动|↗ Quick launch
快捷启动|Quick launch
一步打开，少找一会儿|Open it in one step
固定常用应用、文件夹和网址。点击后在系统中打开。|Pin apps, folders and websites. Click to open with Windows.
文件管理|File Explorer
桌面|Desktop
截图|Screenshot
任务管理器|Task Manager
蓝牙|Bluetooth
显示设置|Display settings
声音设置|Sound settings
我的快捷入口|My shortcuts
＋ 固定应用 / 文件夹 / 网址|＋ Pin app / folder / website
固定快捷入口|Pin a shortcut
完整网址、应用路径或文件夹路径|Full URL, application path or folder path
选应用|Choose app
选文件夹|Choose folder
移除此入口|Remove shortcut
还没有固定入口。|No pinned shortcuts yet.
最多固定 20 个入口。|Pin up to 20 shortcuts.
请输入名称和有效的应用、文件夹或 http(s) 网址。|Enter a name and a valid app, folder or http(s) URL.
暂时无法打开，请检查应用或路径是否可用。|Could not open. Check the app or path.
▱ 文件收纳|▱ File shelf
文件拖放收纳|Drag-and-drop file shelf
文件随手放|Keep files within reach
＋  把文件拖到这里|＋  Drop files here
选择文件|Choose file
选择文件夹|Choose folder
收纳文件引用|Add file reference
收纳本地文件夹引用（不会扫描内容）|Add a local folder reference (contents are not scanned)
拖入文件或文件夹，随时打开与定位。仅保存引用，不复制、移动或删除原文件；支持本机路径。|Drop files or folders to open or locate them later. Stores references only; original files are not copied, moved or deleted. Local paths are supported.
收纳架还是空的。可以拖入文件，或用上方按钮选择。|The shelf is empty. Drop files here or use the buttons above.
原文件保持原位|Original files stay in place
文件夹 · 不扫描内容|Folder · Contents not scanned
使用 Windows 默认应用打开|Open with the default Windows app
路径不可用，或此类型仅支持打开所在位置|Path unavailable, or this type only supports Show location
文件暂不可用|File unavailable
链接路径不直接打开 · 请打开所在位置|Links are not opened directly · Use Show location
网络磁盘不支持 · 可移除引用|Network drives are not supported · Remove the reference if needed
最多收纳 30 项，请先移除一些引用。|Up to 30 entries. Remove some references first.
引用已保存|References saved
此路径已在收纳架中。|This path is already on the shelf.
已打开所在文件夹|Opened containing folder
已交给 Windows 打开|Sent to Windows to open
# 文件校验|# File checksum
文件 SHA-256 / SHA-512 校验|SHA-256 / SHA-512 file verification
文件是否一致，一眼核对|Verify a file at a glance
选择或拖入一个文件，再计算 SHA-256 / SHA-512。可与下载页面提供的校验值比较。|Choose or drop a file to calculate SHA-256 / SHA-512 and compare it with a published checksum.
选择要校验的文件|Choose a file to verify
尚未选择文件|No file selected
粘贴预期校验值（可选，忽略首尾空白和大小写）|Expected checksum (optional; ignores surrounding spaces and case)
开始计算|Calculate
复制校验值|Copy checksum
计算在后台进行，可随时取消。只读取所选文件，不修改文件或上传内容；校验一致仅表示字节一致，不能替代来源判断。|Runs in the background and can be cancelled. Only reads the chosen file; nothing is modified or uploaded. A matching checksum verifies bytes, not the source's trustworthiness.
尚未选择图片|No image selected
文件已选择，点击开始计算。|File selected. Click Calculate.
正在计算…|Calculating…
正在取消…|Cancelling…
计算完成|Calculation complete
✓ 校验值一致|✓ Checksums match
校验值不一致，请检查文件和校验算法。|Checksums differ. Check the file and selected algorithm.
格式正确，计算完成后会进行比较。|Valid format. It will be compared after calculation.
SHA-256 需要 64 位十六进制字符。|SHA-256 requires 64 hexadecimal characters.
SHA-512 需要 128 位十六进制字符。|SHA-512 requires 128 hexadecimal characters.
已取消计算，可以重新开始。|Calculation cancelled. You can start again.
请先选择文件。|Choose a file first.
▤ 文字收纳|▤ Text shelf
手动剪贴板收纳|Manual clipboard shelf
临时文字收纳|Temporary text shelf
收纳当前剪贴板|Capture clipboard text
清空收纳|Clear shelf
只在点击收纳时读取剪贴板；最多 15 条，退出即清空。|Reads the clipboard only when clicked. Up to 15 entries; cleared on exit.
收纳区还是空的。|The text shelf is empty.
文字超过 8000 字，请先缩短后再收纳。|Text exceeds 8,000 characters. Shorten it first.
剪贴板文本为空。|The clipboard text is empty.
剪贴板中没有文本。|No text on the clipboard.
已收纳 · 仅保存在本次运行内存中|Captured · Stored in memory for this session only
≡ 文字工具|≡ Text tools
文字处理工具|Text processing tools
文字处理工作台|Text workbench
输入只留在本次运行内存中，不自动读取剪贴板。每次最多 65,536 个文本单元。|Input stays in memory for this session. The clipboard is not read automatically. Up to 65,536 UTF-16 units per operation.
处理|Process
生成 GUID|Generate GUID
生成新的 GUID|Generate a new GUID
JSON 格式化|Format JSON
JSON 压缩|Minify JSON
Base64 编码（UTF-8）|Base64 encode (UTF-8)
Base64 解码（UTF-8）|Base64 decode (UTF-8)
URL 参数编码|Encode URL parameter
URL 参数解码|Decode URL parameter
转换为大写|Uppercase
转换为小写|Lowercase
清理每行首尾空格|Trim each line
合并完全一致的重复行|Remove exact duplicate lines
处理完成 · 原文保留在上方|Done · Original text is kept above
输入已更改，请重新处理。|Input changed. Process it again.
已清空本面板文字|Panel text cleared
请先完成文字处理。|Process the text first.
还没有可复制的结果。|No result to copy yet.
结果已放入输入框|Result moved to the input box
结果超过输入上限，请复制后分段处理。|Result exceeds the input limit. Copy it and process it in sections.
已生成新的 GUID|New GUID generated
URL 按单个参数值处理，+ 保留为加号。JSON 保留原数字与转义文本，拒绝重复字段名；不做代码执行。|URL operations handle one parameter value; + stays a plus sign. JSON preserves numeric and escaped text and rejects duplicate keys. No code is executed.
＋ 计算器|＋ Calculator
计算器|Calculator
随手算一算|Quick calculations
输入表达式后按 Enter 计算。|Enter an expression and press Enter.
计算 ↵|Calculate ↵
支持 + − * /、括号、^ 幂、% 百分数，及 sqrt / abs / round。|Supports + − * /, parentheses, ^ powers, % percentages, and sqrt / abs / round.
本地计算 · % 表示除以 100|Calculated locally · % divides by 100
结果为近似值，显示最多 15 位有效数字。|Approximate result, shown to 15 significant digits.
输入已更改，按 Enter 重新计算。|Input changed. Press Enter to calculate again.
⇄ 单位换算|⇄ Unit converter
单位换算|Unit converter
常用单位，一步换算|Convert everyday units
输入数值与原单位|Input value and unit
换算为|Convert to
换算|Convert
交换单位|Swap units
10 类固定单位 · 离线计算。KB 按 1000，KiB 按 1024。|10 fixed unit categories · Offline calculation. KB uses 1000; KiB uses 1024.
长度|Length
质量|Mass
面积|Area
体积|Volume
温度|Temperature
速度|Speed
时间|Time
数据容量|Data size
能量|Energy
压力|Pressure
⚄ 随机与密码|⚄ Random and passwords
密码生成 / 随机数 / 名单抽签|Passwords / random numbers / draw lots
随机与密码|Random and passwords
密码长度|Password length
密码长度 · 8 至 128|Password length · 8–128
大写 A–Z|Uppercase A–Z
小写 a–z|Lowercase a–z
数字 0–9|Digits 0–9
符号|Symbols
排除易混字符 I / l / 1 / O / 0|Exclude ambiguous characters I / l / 1 / O / 0
生成密码|Generate password
请设置密码参数。|Choose password options.
密码长度须为 8 至 128。|Password length must be 8–128.
每种勾选类型至少出现一次。生成后请妥善保存；关闭本应用后无法找回。|Includes at least one character from each selected type. Save the result; it cannot be recovered after the app closes.
随机整数|Random integer
最小值|Minimum
最大值|Maximum
最小值与最大值 · 均包含在结果范围内|Minimum and maximum · Both included
生成整数|Generate integer
再来一次|Again
支持 −2,147,483,648 至 2,147,483,647。每次生成一个整数；范围相等时返回该值。|Supports −2,147,483,648 to 2,147,483,647. Generates one integer; equal bounds return that value.
名单抽签 / 打乱|Draw lots / shuffle
每行一项 · 忽略空白行，保留每行原文与空格|One entry per line · Blank lines ignored; text and spaces preserved
抽取数量|Draw count
抽取数量 · 同一行在本次抽取中最多出现一次|Draw count · Each entry is drawn at most once
抽取名单|Draw entries
打乱全部|Shuffle all
最多 500 个非空行、32,768 个文本单元。不合并重复行时，同名内容可能多次出现；再次抽取会重新使用完整名单。|Up to 500 nonempty lines and 32,768 UTF-16 units. Without deduplication, identical names may appear more than once. Each draw uses the full list again.
设置参数后点击生成；更换模式会清空旧结果。|Set options and generate. Switching modes clears the previous result.
点击后使用系统加密随机数生成。内容不保存、不上传，也不自动读取剪贴板。|Uses the system cryptographic random generator when clicked. Nothing is saved or uploaded; the clipboard is not read automatically.
选项已更改，请重新生成。|Options changed. Generate again.
输入已更改，请重新生成。|Input changed. Generate again.
请先生成结果。|Generate a result first.
已生成范围内整数（含两端）|Generated an integer within the inclusive range
最小值不能大于最大值。|Minimum must not exceed maximum.
请先输入名单，每行一项。|Enter a list first, one item per line.
◈ 屏幕取色|◈ Color picker
屏幕取色|Color picker
取一点颜色，留住灵感|Capture a color
点击取色后，将鼠标移到目标位置，3 秒后读取指针下的一个像素。也可输入 HEX 色值。|After clicking, move the pointer to a target. Reads one pixel after 3 seconds. You can also enter a HEX color.
3 秒后屏幕取色|Pick screen color in 3 seconds
#RGB 或 #RRGGBB|#RGB or #RRGGBB
查看 RGB 反色|Show RGB complement
仅手动取色，不连续监控、不保存截图。受保护画面或 HDR 内容的色值可能不可用或存在差异。|Manual sampling only; no continuous monitoring or saved screenshots. Protected or HDR content may be unavailable or differ.
请输入 3 位或 6 位 HEX，例如 #8AC 或 #88AACC。|Enter a 3- or 6-digit HEX color, e.g. #8AC or #88AACC.
已更新颜色|Color updated
3 秒后取色，请将鼠标移到目标位置…|Picking in 3 seconds. Move the pointer to the target…
取色已超时，请重新点击。|Color picking timed out. Click again.
已读取指针下的颜色|Color sampled beneath the pointer
暂时无法读取该位置，请换个位置重试。|Could not sample this position. Try another position.
黑色|black
白色|white
↕ 系统详情|↕ System details
网络与系统详情|Network and system details
此刻 · 系统状态|System status now
网络与设备|Network and device
正在读取网络状态…|Reading network status…
↓ 下载|↓ Download
↑ 上传|↑ Upload
采样中|Sampling
最近 30 次采样 · 物理网卡合计|Last 30 samples · Physical adapters combined
本机 IPv4 · 读取中|Local IPv4 · Reading
本机 IPv4 · 隐私模式已隐藏|Local IPv4 · Hidden for privacy
开机时长|Uptime
运行进程|Running processes
系统盘|System drive
读取中|Reading
暂时无法读取设备信息|Device information unavailable
网络状态暂不可用|Network status unavailable
部分网卡状态不可用|Some adapter status is unavailable
没有已连接的物理网卡|No connected physical network adapter
● 已连接网络|● Network connected
磁盘信息暂不可用|Disk information unavailable
物理网卡链路状态；不测试互联网连接。|Physical adapter link status; does not test internet access.
⌁ 端口占用|⌁ Port monitor
端口占用 / 程序与 PID|Port usage / programs and PIDs
端口被谁占用了|Which app is using this port?
输入本地端口，查看 TCP / UDP、IPv4 / IPv6 的占用程序、PID 和路径。|Enter local ports to see TCP / UDP and IPv4 / IPv6 owners, PIDs and program paths.
多个端口用逗号、空格或顿号分隔|Separate ports with commas or spaces
查询 / 刷新|Query / refresh
每 3 秒监测|Monitor every 3 seconds
可选择并复制程序路径|Program paths can be selected and copied
点击查询后读取系统端口表，不会结束任何进程。|Reads the Windows port table when queried. No processes are stopped.
受保护进程可能无法读取路径。连接随时会变化；未发现绑定也可能是进程刚退出，或该协议读取失败。|Protected process paths may be inaccessible. Connections can change at any time; no result may mean a process just exited or a protocol could not be read.
端口已修改，点击查询或等待下次监测。|Ports changed. Query now or wait for the next monitor update.
请输入端口，例如 3000、8080。|Enter ports, for example 3000, 8080.
一次最多监测 64 个端口。|Monitor up to 64 ports at once.
正在读取…|Reading…
▣ 截图钉屏|▣ Screenshot pin
区域截图与悬浮钉屏|Region capture and floating pins
截图钉在桌面，边看边操作|Pin a screenshot while you work
框选截图并钉屏|Capture region and pin
点击后拖动框选屏幕区域，Esc 取消。截图会悬浮在桌面上；可拖动、滚轮缩放，并调整透明度。|Click and drag to select a region; Esc cancels. The screenshot floats on the desktop. Drag it, use the wheel to zoom, or adjust opacity.
关闭全部钉屏|Close all pins
当前没有钉屏截图|No pinned screenshots
只有点击并完成框选才会截取该区域。截图仅保留在内存，关闭即移除；最多同时保留 8 张。受保护的视频画面可能无法截取。|Captures only after you complete a selection. Screenshots stay in memory until closed, up to 8 at once. Protected video may not be captured.
截图不透明度|Screenshot opacity
缩放截图（滚轮也可缩放）|Screenshot zoom (or use the wheel)
拖动图片 · 滚轮缩放|Drag to move · Wheel to zoom
Side Island 截图钉屏|Side Island screenshot pin
拖动框选截图区域 · Esc 取消|Drag to select a screenshot region · Esc to cancel
已取消截图。|Screenshot cancelled.
截图已钉屏，可拖动、滚轮缩放或调整透明度。|Screenshot pinned. Drag it, zoom with the wheel, or adjust opacity.
最多保留 8 张钉屏，请先关闭一张。|Up to 8 pins. Close one first.
截图未完成，请重新框选；受保护画面可能不可读取。|Capture did not finish. Select again; protected content may be unavailable.
▧ 图片处理|▧ Image tools
图片压缩 / 分辨率 / 格式转换|Compression / resolution / format conversion
图片快处理|Quick image tools
拖入一张 JPG / PNG，调整尺寸和格式后另存为新文件。图片仅在本机处理。|Drop a JPG / PNG, adjust size and format, then save as a new file. Images are processed locally.
选择图片|Choose image
未加载图片|No image loaded
输出宽度 × 高度（像素）|Output width × height (pixels)
输出宽度|Output width
输出高度|Output height
保持比例|Keep aspect ratio
JPG · 可压缩 / 透明铺白|JPG · Adjustable quality / white transparency background
PNG · 无损 / 保留透明|PNG · Lossless / preserves transparency
JPG 质量：82（越低文件通常越小）|JPG quality: 82 (lower usually means smaller files)
另存新图片|Save as new image
另存处理后的图片|Save processed image as
PNG 为无损编码，缩小分辨率可减少体积；JPG 可调质量。最多 64 MB / 2400 万像素，不覆盖原图。|PNG is lossless; lower resolution reduces size. JPG quality is adjustable. Up to 64 MB / 24 megapixels. Original files are not overwritten.
正在读取图片…|Loading image…
图片已就绪。调整参数后另存新文件。|Image ready. Adjust options and save as a new file.
图片尚未加载完成。|Image is still loading.
宽度和高度请输入正整数。|Enter positive integers for width and height.
图片尺寸需为 1–16000 像素，总像素不超过 2400 万。|Dimensions must be 1–16,000 pixels, up to 24 megapixels total.
JPG 质量需为 1–100。|JPG quality must be 1–100.
请另存为新文件，不能覆盖原图。|Save as a new file. The original cannot be overwritten.
文件扩展名与所选输出格式不一致。|The file extension does not match the selected output format.
正在处理并保存…|Processing and saving…
已取消处理。|Processing cancelled.
仅支持 JPG 和 PNG 格式。|Only JPG and PNG are supported.
图片处理失败，请确认文件是有效的 JPG / PNG 图片。|Processing failed. Check that the file is a valid JPG / PNG image.
图片可能已移动、被占用或保存位置不可用。|The image may have moved, be in use, or the save location may be unavailable.
图片过大，暂时无法处理，请尝试更小的图片。|The image is too large to process. Try a smaller one.
图片文件不能超过 64 MB。|Image files must be no larger than 64 MB.
文件内容不是 JPG 或 PNG 图片。|The file is not a JPG or PNG image.
无法读取拖入的图片。|Could not read the dropped image.
请每次拖入一张图片。|Drop one image at a time.
≠ 文字对照|≠ Text comparison
文字差异对照|Text difference comparison
文字对照|Text comparison
左边放原文，右边放新文本。自动按行对照：绿色新增、红色删除、橙色修改。文本仅在本次运行内存中处理。|Original on the left, new text on the right. Compares lines automatically: green additions, red deletions, orange changes. Text stays in memory for this session.
原文|Original
新文本|New text
原文差异|Original differences
新文本差异|New text differences
交换左右|Swap sides
每侧最多 120,000 字符、1,600 行；预览前 600 行，每行最多 2,000 字符，摘要统计完整文本。|Up to 120,000 characters and 1,600 lines per side. Previews the first 600 rows, up to 2,000 characters each. Summary covers the complete text.
正在等待输入完成…|Waiting for input to finish…
正在对照…|Comparing…
两侧文本一致（忽略换行符格式）。|The texts are identical (line-ending format ignored).
对照暂时失败，请减少文本后重试。|Comparison failed. Try less text.
每侧最多支持 1,600 行，请分段对照。|Up to 1,600 lines per side. Compare in sections.
每侧最多支持 120,000 个字符，请缩短文本后再对照。|Up to 120,000 characters per side. Shorten the text first.
◉ 任务守望|◉ Task watch
文件生成 / 进程退出提醒|File creation / process exit alerts
等待文件生成，或等待指定进程退出。切换页面和收起后仍继续，每秒检查一次。|Wait for a file to appear or a process to exit. Keeps checking every second while collapsed or on another page.
目标文件完整路径（可以尚不存在）|Full target file path (may not exist yet)
选择目标路径|Choose target path
指定要等待生成的文件（不会创建文件）|Choose a file to wait for (does not create it)
等待文件出现|Wait for file
选择运行进程，或直接输入 PID|Choose a running process or enter a PID
刷新运行进程列表|Refresh process list
进程 PID，例如 12345|Process PID, for example 12345
等待进程退出|Wait for process exit
文件出现即触发一次，不保证文件已写完。启动时已存在的文件或已退出的进程不会补发提醒。已完成项可移除后重新添加。|Alerts once when a file appears, even if writing is incomplete. Existing files or exited processes do not trigger retroactive alerts. Remove completed entries to watch again.
只在应用运行时监测；不持久化任务，退出应用即取消。受保护进程无法加入。|Watches only while the app runs. Tasks are not persisted and are cancelled on exit. Protected processes cannot be added.
还没有守望任务。|No watch tasks yet.
请输入大于 0 的 PID，或选择运行进程。|Enter a PID greater than 0 or choose a running process.
正在读取运行进程…|Reading running processes…
已开始等待文件出现。文件存在即可提醒，不代表写入已经完成。|Waiting for the file. Its appearance triggers an alert even if writing is incomplete.
文件已经存在，未启动守望；只有启动后出现的文件才会提醒。|The file already exists. Only files appearing after a watch starts trigger an alert.
该进程已经退出，未启动守望。|The process has already exited. No watch was started.
这个进程已经在守望中。|This process is already being watched.
这个文件已经在守望中。|This file is already being watched.
最多保留 20 条守望，请先移除已完成的条目。|Up to 20 watches. Remove completed entries first.
文件已出现 · 已提醒|File appeared · Alert sent
进程已退出 · 已提醒|Process exited · Alert sent
进行中|In progress
已完成|Completed
等待的文件已出现|The awaited file appeared
守望的进程已退出|The watched process exited
☕ 保持唤醒|☕ Keep awake
临时保持唤醒|Temporary keep awake
下载、任务运行或演示时，临时防止电脑因空闲自动睡眠。到期或退出灵动岛时自动释放。|Temporarily prevents idle sleep during downloads, tasks or presentations. Ends automatically when time is up or the app exits.
30 分钟|30 minutes
60 分钟|60 minutes
120 分钟|120 minutes
停止保持唤醒|Stop keeping awake
未保持唤醒|Not keeping awake
尚未开启。再次选择时长会从现在重新计时。|Not active. Choosing a duration again restarts the countdown.
允许屏幕按原设置关闭。手动睡眠、合盖和电量不足的系统操作仍由 Windows 决定。|The display may turn off normally. Windows still controls manual sleep, lid actions and low-battery behavior.
已停止，恢复 Windows 原来的自动睡眠行为。|Stopped. Normal Windows idle sleep behavior restored.
时间已到，已恢复 Windows 原来的自动睡眠行为。|Time is up. Normal Windows idle sleep behavior restored.
系统未接受保持唤醒请求。|Windows did not accept the keep-awake request.
释放保持唤醒请求失败，请重试停止。|Could not release the keep-awake request. Try stopping again.
▦ 二维码传话|▦ QR transfer
网址与文字二维码|Link and text QR codes
二维码传话|QR transfer
把网址或短文本交给手机扫码。内容在本机生成，不上传，也不自动打开链接。|Send a link or text to your phone by scanning. Generated locally; no upload or automatic link opening.
请输入网页链接或短文本。|Enter a web link or text.
输入后自动生成二维码。|Generates a QR code automatically as you type.
生成二维码|Generate QR code
复制图片|Copy image
保存 PNG|Save PNG
请先生成二维码。|Generate a QR code first.
已生成 · 手机相机或微信扫一扫即可获取内容|Generated · Scan with your phone camera or WeChat
二维码图片已复制|QR image copied
二维码暂时无法生成，请缩短内容后重试。|Could not generate a QR code. Try shorter content.
↗ 工作场景|↗ Workspaces
组合快捷入口批量启动|Launch shortcut groups
工作场景启动|Workspace launcher
将项目目录、程序和网页组成场景，点一次批量打开。最多 20 个场景，每个场景 30 个资源。|Group project folders, apps and websites into a workspace and launch them together. Up to 20 workspaces and 30 resources each.
＋ 新建场景|＋ New workspace
编辑当前场景|Edit current workspace
启动场景|Launch workspace
场景名称|Workspace name
保存场景|Save workspace
＋ 添加资源|＋ Add resource
复用已有快捷入口|Reuse existing shortcuts
刷新快捷入口|Refresh shortcuts
加入勾选入口|Add checked shortcuts
勾选已在「快捷启动」中保存的入口，可复制加入当前场景。原入口不受影响。|Select shortcuts saved in Quick launch to copy into this workspace. Existing shortcuts are unchanged.
尚无场景，点击新建开始组合你的常用资源。|No workspaces yet. Create one to group your resources.
还没有资源，可添加文件夹、程序、网页，或复用已有快捷入口。|No resources yet. Add folders, apps and websites or reuse existing shortcuts.
暂无可复用入口，请先到快捷启动中添加。|No shortcuts to reuse. Add some in Quick launch first.
每个场景最多 30 个资源。|Up to 30 resources per workspace.
每个场景最多 30 个资源，入口未加入。|Workspace limit of 30 resources reached; shortcut not added.
最多创建 20 个场景。|Create up to 20 workspaces.
最多保存 20 个场景。|Save up to 20 workspaces.
请先为当前场景填写名称。|Name the current workspace first.
名称已修改，请点击保存场景。|Name changed. Click Save workspace.
场景已保存。|Workspace saved.
场景还没有资源，请先添加。|Add resources to this workspace first.
请先勾选要加入的快捷入口。|Check the shortcuts to add first.
勾选入口已在当前场景中。|The checked shortcuts are already in this workspace.
添加场景资源|Add workspace resource
编辑场景资源|Edit workspace resource
编辑资源|Edit resource
资源类型|Resource type
资源名称|Resource name
完整路径或网址|Full path or URL
网页 http(s)|Website http(s)
项目文件夹|Project folder
程序 .exe|Program .exe
快捷方式 .lnk|Shortcut .lnk
程序启动参数（可选，单独传给所选程序）|Launch arguments (optional; passed only to the chosen program)
选择程序 / 快捷方式|Choose program / shortcut
保存资源|Save resource
正在检查资源…|Checking resource…
请输入有效名称、路径或网址。|Enter a valid name, path or URL.
网页地址必须为完整的 http 或 https 网址。|Enter a complete http or https URL.
程序或快捷方式不存在，请重新选择。|Program or shortcut does not exist. Choose again.
文件夹不存在或不可访问。|Folder does not exist or is inaccessible.
只有程序入口支持单独的启动参数。|Only program resources support launch arguments.
无法访问此资源，请检查路径后重试。|Could not access this resource. Check its path.
场景启动失败，请检查资源后重试。|Workspace launch failed. Check its resources.
场景保存失败；修改仍保留在本次运行中，请检查目录权限后重试保存。|Could not save workspace. Changes remain for this session. Check folder permissions and try saving again.
保存失败；此修改已保留在本次运行中，可直接关闭窗口后在场景页重试保存。|Could not save. Changes remain for this session. Close this window and retry saving on the workspace page.
场景文件无法读取，原文件会在下次成功保存前先备份。|Could not read the workspace file. The original will be backed up before the next successful save.
部分快捷入口无法读取，请到快捷启动中检查。原入口没有被修改。|Some shortcuts could not be read. Check Quick launch; original entries are unchanged.
已复制|Copied
已复制结果|Result copied
已复制结果到系统剪贴板|Result copied to clipboard
已复制路径|Path copied
已复制数值|Value copied
已复制校验值|Checksum copied
复制失败，请重试。|Could not copy. Try again.
剪贴板被占用，请重试。|Clipboard is busy. Try again.
剪贴板正忙，请重试。|Clipboard is busy. Try again.
剪贴板正忙，请稍后再试。|Clipboard is busy. Try again shortly.
剪贴板正忙，请稍后重试。|Clipboard is busy. Try again shortly.
剪贴板正被其他应用占用，请稍后再试。|Clipboard is in use by another app. Try again shortly.
没有访问权限。|Access denied.
没有权限读取此路径|No permission to read this path
当前没有访问此路径的权限。|No permission to access this path.
请使用完整绝对路径。|Use a complete absolute path.
路径格式不受支持。|Unsupported path format.
暂不支持超过 259 个字符的路径。|Paths longer than 259 characters are not supported.
暂不支持网络共享或设备路径，请选择本地文件。|Network shares and device paths are not supported. Choose a local file.
无法打开，请检查路径、权限和默认应用。|Could not open. Check the path, permissions and default application.
所在文件夹暂不可用。|Containing folder unavailable.
无法读取本次拖放，请使用“选择文件”。|Could not read the dropped item. Use Choose file.
无法读取此次拖放，请使用选择文件。|Could not read the dropped item. Use Choose file.
演示模式不保存修改。|Demo mode does not save changes.
演示模式不写入剪贴板。|Demo mode does not write to the clipboard.
演示模式不修改剪贴板。|Demo mode does not modify the clipboard.
演示模式不写入文件。|Demo mode does not write files.
演示模式不打开文件。|Demo mode does not open files.
演示模式不打开外部应用。|Demo mode does not open external apps.
演示模式不会访问本机文件。|Demo mode does not access local files.
演示模式不会保存|Demo mode does not save
演示模式不读取本机图片。|Demo mode does not read local images.
演示模式不读取屏幕。|Demo mode does not read the screen.
演示模式不截取真实屏幕。|Demo mode does not capture the screen.
演示模式不读取屏幕，请使用 HEX 输入体验。|Demo mode does not sample the screen. Try HEX input instead.
演示模式 · 修改不会保存|Demo mode · Changes are not saved
演示内容 · 修改不会保存|Demo content · Changes are not saved
演示模式 · 可试用计时|Demo mode · Timers are available
演示模式 · 不写入文件|Demo mode · Does not write files
演示模式 · 不读取或写入系统剪贴板|Demo mode · Does not read or write the system clipboard
演示模式 · 不读取屏幕或写入剪贴板|Demo mode · Does not read the screen or write to the clipboard
演示模式 · 快捷操作不会执行|Demo mode · Shortcuts are not executed
演示模式 · 提醒不写入文件，也不弹出系统通知|Demo mode · Reminders are not saved and do not send Windows notifications
演示模式 · 不访问文件或剪贴板，修改不会保存|Demo mode · Does not access files or clipboard; changes are not saved
演示模式 · 仅计算固定文字样本，不读取本机文件或修改剪贴板|Demo mode · Calculates a fixed sample only; no local file or clipboard access
演示模式 · 下方为示例，不写入剪贴板。|Demo mode · Sample content below; clipboard is unchanged
演示模式 · 可添加守望，不弹出外部通知。|Demo mode · Watches are available; no external notifications
演示模式 · 倒计时可试用，不改变电脑唤醒状态。|Demo mode · Countdown is available; the computer's sleep state is unchanged
演示模式 · 不启动软件、不打开网页、不保存修改。|Demo mode · Does not launch apps, open websites or save changes
演示模式：显示示例，不读取系统端口。|Demo mode: shows samples without reading system ports.
演示密码示例，请勿用于实际账户。|Demo password; do not use for a real account.
演示数据 · 所有真实控制均已禁用|Demo data · All real controls are disabled
演示模式使用固定文字样本。|Demo mode uses a fixed text sample.
演示样本计算完成|Demo sample calculation complete
演示|Demo
示例|Sample
未连接|Disconnected
连接中|Connecting
同步中|Syncing
待提醒|Pending
已提醒|Alert sent
暂无|None
未加载|Not loaded
透明度|Opacity
内存|Memory
电源|Power
已接电|Plugged in
计时|Timer
时钟与日期|Clock and date");
            BuildUnits();
            BuildErrors();
            BuildTemplates();
            Map(@"星期一|Monday
星期二|Tuesday
星期三|Wednesday
星期四|Thursday
星期五|Friday
星期六|Saturday
星期日|Sunday
星期天|Sunday
日期间隔|Date difference
去重行（保留首次出现）|Deduplicate lines (keep first)
去除空白行|Remove blank lines
SHA-256 摘要|SHA-256 digest
正在生成…|Generating…
请输入网页链接或文字。|Enter a web link or text.
二维码暂时无法生成，请重试。|Could not generate a QR code. Try again.
文字中包含不完整的 Unicode 字符。|Text contains an incomplete Unicode character.
结果已清空。|Results cleared.
请输入有限数值，使用小数点；支持 1.5e3。不要使用千位分隔符。|Enter a finite number using a decimal point. Scientific notation such as 1.5e3 is supported; do not use thousands separators.
请选择同一类别的有效单位。|Select valid units from the same category.
数值必须是有限数。|The value must be finite.
温度不能低于绝对零度（−273.15 °C / −459.67 °F / 0 K）。|Temperature cannot be below absolute zero (−273.15 °C / −459.67 °F / 0 K).
换算结果超出范围，请减小数值。|Converted value is out of range. Use a smaller value.
文件路径无效。|Invalid file path.
请选择文件路径，不能选择文件夹。|Choose a file path, not a folder.
请输入要等待生成的文件完整路径。|Enter the full path of the file to wait for.
PID 必须是大于 0 的整数。|PID must be an integer greater than 0.
选择的进程已退出，PID 已被其他进程使用；请刷新列表。|The selected process exited and its PID was reused. Refresh the list.
 剩余| remaining
剩余 |Remaining 
岛内未读 |Unread here 
第 |No. 
本次运行已完成 0 个专注|Completed 0 focus sessions this run
不打断拖动|Does not interrupt dragging
测试|Test
取消处理|Cancel processing
取消计算|Cancel calculation
任务守望|Task watch
日期：yyyy-MM-dd|Date: yyyy-MM-dd
日期时间 · yyyy-MM-dd HH:mm:ss[.fff]|Date/time · yyyy-MM-dd HH:mm:ss[.fff]
入口名称|Shortcut name
守望进程退出|Watch process exit
守望文件出现|Watch file appearance
拖动标题移动  /  拖动边缘或四角调整大小|Drag the title to move / Drag edges or corners to resize
拖动拉伸 · 朝屏幕内侧拖动可放大|Drag to resize · Drag inward to enlarge
按住岛身自由拖动 · 松手吸附最近的上 / 下 / 左 / 右边缘\n悬停展开 · 右键调整文字方向|Drag the island freely · Release to snap to the nearest edge\nHover to expand · Right-click to change text direction
消息中心 · 悬停展开|Message center · Hover to expand
应用音量|App volume
Codex Island · 已同步 |Side Island · Updated 
中 / English|中 / English
亮色 / 暗色|Light / dark
切换为英文|Switch to English
切换为中文|Switch to Chinese
切换为亮色模式|Switch to light mode
切换为暗色模式|Switch to dark mode
点击查看|Click to view
简体中文|简体中文
外观模式|Appearance
双组件自定义|Two custom widgets
第一个组件|First widget
第二个组件|Second widget
两个组件可自由组合，也可以选择相同内容。|Combine any two widgets, including the same widget twice.
组件仅在双组件自定义模式显示。|Widgets are shown in Two custom widgets mode.
Codex 短期额度|Codex short-term quota
Codex 长期 / 7d 额度|Codex long-term / 7d quota
CPU 使用率|CPU usage
内存使用率|Memory usage
电池电量|Battery level
当前时间|Current time
保持唤醒|Keep awake
未读通知|Unread notifications
文字仅在本机生成单张二维码，不上传。容量受二维码标准限制；L 可容纳更多，较高纠错级别更耐遮挡。|Generates one QR code locally without uploading. Capacity is limited by the QR standard: L holds more; higher error correction tolerates more damage.
L · 最大容量（2,952 字节）|L · Maximum capacity (2,952 bytes)
M · 均衡纠错（2,330 字节）|M · Balanced correction (2,330 bytes)
Q · 较高纠错（1,662 字节）|Q · Higher correction (1,662 bytes)
H · 最高纠错（1,272 字节）|H · Highest correction (1,272 bytes)
密集二维码建议保存原图后放大扫码。字节数包含中文与表情的 UTF-8 编码，不是字数。|For dense codes, save the original image and enlarge it before scanning. Capacity counts UTF-8 bytes, including Chinese and emoji, rather than characters.
请缩短内容。|Shorten the content.
可切换 L 级容纳更多文字。|Switch to L to hold more text.");
            Pattern("超过单张二维码容量：当前纠错级别最多支持 {0} 个 UTF-8 字节。请缩短内容。", "Single QR code capacity exceeded: this correction level supports up to {0} UTF-8 bytes. Shorten the content.");
            Pattern("超过单张二维码容量：当前纠错级别最多支持 {0} 个 UTF-8 字节。可切换 L 级容纳更多文字。", "Single QR code capacity exceeded: this correction level supports up to {0} UTF-8 bytes. Switch to L to hold more text.");
            Pattern("{0} / {1} UTF-8 字节", "{0} / {1} UTF-8 bytes");
            Map(@"演示输出设备|Demo output device
演示麦克风已静音|Demo microphone muted");
            Pattern("待办 {0} / {1}", "To-dos {0} / {1}");
            Pattern("{0} / 30 项", "{0} / 30 items");
            Pattern("每天 {0}\n下次：{1}", "Daily {0}\nNext: {1}");
            Pattern("{0} 时 {1} 分", "{0}h {1}m");
            Pattern("{0} 分 {1} 秒", "{0}m {1}s");
            foreach (string key in CompactWidgetCatalog.Keys) Add(CompactWidgetCatalog.Label(key, "zh"), CompactWidgetCatalog.Label(key, "en"));
            foreach (var category in ConversionTools.Categories) foreach (var unit in category.Units)
            {
                string translated; if (English.TryGetValue(unit.Name, out translated)) Pattern("{0}\n" + unit.Name, "{0}\n" + translated);
            }
            NativeCatalog();
            QuickActionCatalog();
            MusicCatalog();
            HoverCatalog();
            MusicV2Catalog();
            HistoryLogsCatalog();
            DesktopExperienceCatalog();
            ExperienceCatalog();
            OcrDropCatalog();
            MediaSessionsCatalog();
            TextTemplatesCatalog();
            ProductivityV3Catalog();
            ServiceReadyCatalog();
            SettingsBackupCatalog();
            MediaLiveCatalog();
            BuildAutoSceneWidgetCatalog();
            MixerLanCatalog();
            AnnotationBatchCatalog();
            NextFeatureCatalog();
            PortsCatalog();
            BatchHashCatalog();
        }
        private static void BuildUnits()
        {
            Map(@"毫米 mm|Millimeters mm
厘米 cm|Centimeters cm
米 m|Meters m
千米 km|Kilometers km
英寸 in|Inches in
英尺 ft|Feet ft
码 yd|Yards yd
英里 mi|Miles mi
海里 nmi|Nautical miles nmi
毫克 mg|Milligrams mg
克 g|Grams g
千克 kg|Kilograms kg
吨 t|Tonnes t
盎司 oz|Ounces oz
磅 lb|Pounds lb
市斤|Chinese jin
平方米 m²|Square meters m²
平方厘米 cm²|Square centimeters cm²
平方千米 km²|Square kilometers km²
公顷 ha|Hectares ha
亩|Chinese mu
平方英尺 ft²|Square feet ft²
英亩 acre|Acres acre
毫升 mL|Milliliters mL
升 L|Liters L
立方厘米 cm³|Cubic centimeters cm³
立方米 m³|Cubic meters m³
美制加仑 gal|US gallons gal
英制加仑 gal|Imperial gallons gal
美制液盎司 fl oz|US fluid ounces fl oz
摄氏|Celsius
华氏|Fahrenheit
摄氏度 °C|Celsius °C
华氏度 °F|Fahrenheit °F
开尔文 K|Kelvin K
米/秒 m/s|Meters/second m/s
千米/小时 km/h|Kilometers/hour km/h
英里/小时 mph|Miles/hour mph
节 kn|Knots kn
英尺/秒 ft/s|Feet/second ft/s
毫秒 ms|Milliseconds ms
秒 s|Seconds s
分钟 min|Minutes min
小时 h|Hours h
天 d|Days d
周|Weeks
位 bit|Bits bit
字节 B|Bytes B
焦耳 J|Joules J
千焦 kJ|Kilojoules kJ
卡 cal (热化学)|Calories cal (thermochemical)
千卡 kcal|Kilocalories kcal
瓦时 Wh|Watt-hours Wh
千瓦时 kWh|Kilowatt-hours kWh
帕 Pa|Pascals Pa
千帕 kPa|Kilopascals kPa
兆帕 MPa|Megapascals MPa
巴 bar|Bar bar
标准大气压 atm|Standard atmospheres atm
磅/平方英寸 psi|Pounds/square inch psi");
        }
        private static void BuildTemplates()
        {
            Pattern("消息  {0}", "Messages {0}");
            Pattern("剩余 {0}% 时提醒", "Alert at {0}% remaining");
            Pattern("{0} 天额度", "{0}-day quota"); Pattern("{0} 小时额度", "{0}-hour quota"); Pattern("{0} 分钟额度", "{0}-minute quota");
            Pattern("{0} 天 {1} 小时后重置", "Resets in {0}d {1}h"); Pattern("{0} 小时 {1} 分后重置", "Resets in {0}h {1}m"); Pattern("{0} 分钟后重置", "Resets in {0}m");
            Pattern("●  后台监控中 · 每 {0} 秒更新", "●  Monitoring · Refreshing every {0}s");
            Pattern("Codex Island · 已同步 {0}", "Side Island · Updated {0}"); Pattern("更新于 {0}", "Updated {0}"); Pattern("上次成功 {0}", "Last success {0}");
            Pattern("最近通知  ·  {0} 条  ·  岛内未读 {1}", "Recent notifications · {0} messages · {1} unread here");
            Pattern("{0} 条 · 点击或悬停查看", "{0} messages · Click or hover to view"); Pattern("岛内未读 {0}", "Unread here {0}");
            Pattern("{0} 个应用 · {1} 个已禁用", "{0} apps · {1} disabled");
            Pattern("{0}  ·  示例", "{0} · Sample");
            Pattern("{0}  ·  切换工具（{1}）", "{0} · Switch tool ({1})"); Pattern("切换工具（{0}）", "Switch tool ({0})");
            Pattern("本次运行已完成 {0} 个专注", "Completed {0} focus sessions this run");
            Pattern("待办 {0} / 30", "To-dos {0} / 30"); Pattern("{0} / 6000 字符", "{0} / 6000 characters");
            Pattern("已保存 · {0}", "Saved · {0}"); Pattern("已保存：{0}", "Saved: {0}");
            Pattern("提醒 {0} / 30 · 待提醒 {1}", "Reminders {0} / 30 · Pending {1}");
            Pattern("每天 {0}", "Daily {0}"); Pattern("{0} 分钟后", "In {0} minutes");
            Pattern("已复制 {0}", "Copied {0}");
            Pattern("{0} 字符 · {1} 词段 · {2} 行\n词段按连续字母、数字分组；字符按文本元素计数。", "{0} characters · {1} word groups · {2} lines\nWords group consecutive letters and digits; characters count text elements.");
            Pattern("{0} 个非空行 · {1} 个重复行", "{0} nonempty lines · {1} duplicate lines");
            Pattern("已生成 {0} 位密码 · 不会自动保存", "Generated {0}-character password · Not saved automatically");
            Pattern("已打乱全部 {0} 项 · 原名单保留", "Shuffled all {0} entries · Original list kept");
            Pattern("已从 {0} 项中抽取 {1} 项 · 原名单保留", "Drew {1} from {0} entries · Original list kept");
            Pattern("{0} 秒后取色，请保持鼠标位置…", "Picking in {0}s. Keep the pointer still…");
            Pattern("与黑色对比 {0}:1 · 与白色对比 {1}:1\n搭配黑色文字更清晰。", "Contrast with black {0}:1 · with white {1}:1\nBlack text is easier to read.");
            Pattern("与黑色对比 {0}:1 · 与白色对比 {1}:1\n搭配白色文字更清晰。", "Contrast with black {0}:1 · with white {1}:1\nWhite text is easier to read.");
            Pattern("{0} 个", "{0} processes"); Pattern("{0} 系统盘", "{0} system drive");
            Pattern("可用 {0} / 共 {1}", "Free {0} / total {1}"); Pattern("本机 IPv4 · {0}", "Local IPv4 · {0}");
            Pattern("当前钉屏 {0} 张 · 退出灵动岛时自动关闭", "{0} pinned screenshots · Closed when Side Island exits");
            Pattern("JPG 质量：{0}（越低文件通常越小）", "JPG quality: {0} (lower usually means smaller files)");
            Pattern("已另存 {0}", "Saved as {0}");
            Pattern("新增 {0} 行 · 删除 {1} 行 · 修改 {2} 行", "{0} added lines · {1} removed · {2} changed");
            Pattern("已列出 {0} 个可读取身份的进程；受保护进程已略过。", "Listed {0} identifiable processes; protected processes were skipped.");
            Pattern("已开始守望 {0}", "Watching {0}"); Pattern("{0} 项守望已完成。", "{0} watch tasks completed.");
            Pattern("已开启 {0} 分钟，到期自动停止。", "Keeping awake for {0} minutes; stops automatically.");
            Pattern("唤醒 {0}", "Awake {0}");
            Pattern("正在启动「{0}」…", "Launching “{0}”…"); Pattern("已启动 {0} 个资源。", "Launched {0} resources.");
            Pattern("演示模式，已检查 {0} 个资源，未实际打开。", "Demo mode: checked {0} resources without opening them.");
            Pattern("查询失败：{0}", "Query failed: {0}"); Pattern("保存失败：{0}", "Save failed: {0}");
            Pattern("读取进程失败：{0}", "Could not read processes: {0}"); Pattern("无法启动守望：{0}", "Could not start watch: {0}"); Pattern("检查守望失败：{0}", "Watch check failed: {0}");
        }
    }
}
