namespace CodexIsland
{
    internal static partial class UiLocalization
    {
        private static void NextFeatureCatalog()
        {
            Map(@"⇌ 自动场景|⇌ Automatic scenes
按应用 / 时间 / 显示器切换场景|Switch scenes by app / time / display
▦ 组件编辑|▦ Widget editor
组件顺序 / 尺寸 / 优先级|Widget order / size / priority
♫ 应用音量|♫ App volumes
分应用音量 / 输出设备|Per-app volume / Output device
↔ 局域网传输|↔ LAN transfer
扫码传文件到手机|Scan to transfer files to your phone
▱ 批量文件|▱ Batch files
批量重命名 / 图片处理|Batch rename / Image processing
◉ 任务动态|◉ Task activities
任务进度 / 本地接入|Task progress / Local input
任务动态|Task activities
任务都在这里继续|Your tasks continue here
统一查看文件处理、传输与脚本任务。进度未知时显示活动状态，不估算百分比。|View file, transfer and script tasks together. Unknown progress stays indeterminate.
收起时显示当前任务|Show the current task while collapsed
搜索任务标题、来源或说明|Search task titles, sources or details
清除已结束记录|Clear finished records
脚本本地接入|Local script input
手动开启后仅监听本机 127.0.0.1，需要访问令牌。停止后旧令牌失效，不运行命令或访问文件。|Enable manually to listen on 127.0.0.1 only. A token is required and expires when stopped. This input reports status only.
端口（0 为自动选择）|Port (0 selects automatically)
开启本地接入|Start local input
停止接入|Stop input
复制访问令牌|Copy access token
复制请求示例|Copy request example
本地接入未开启。任务记录仅保留本次运行，最多 80 项。移除记录不会停止外部任务。|Local input is off. Up to 80 tasks are kept for this run. Removing a record does not stop its task.
任务显示偏好未能保存，请检查数据目录。|Task display preferences could not be saved. Check the data folder.
端口应为 0—65535。|The port must be between 0 and 65535.
本地任务接入已开启，仅本机可访问。令牌仅保留在内存中。|Local task input is on for this computer only. Its token is kept in memory.
无法开启本地接入：|Could not start local input: 
本地接入已停止，旧令牌已失效。|Local input stopped. The old token has expired.
请先开启本地接入。|Start local input first.
已复制。|Copied.
剪贴板暂时不可用，请重试。|The clipboard is temporarily unavailable. Try again.
演示模式不启动本地接入。|Demo mode does not start local input.
演示模式不修改剪贴板。|Demo mode does not change the clipboard.
移除记录|Remove record
后台任务内容已隐藏|Task content is hidden
后台任务|Background task
本地接口|Local input
活动 {0} 项 · 共 {1} 项|{0} active · {1} total
等待中|Queued
进行中|Running
已完成|Completed
已取消|Cancelled
失败|Failed
通知待办|Notification tasks
右键通知卡片可转为待办、固定或稍后提醒。通知被清空后，已保存的待办仍保留。|Right-click a notification to save, pin or snooze it. Saved tasks remain after notifications are cleared.
搜索标题、来源或项目|Search titles, sources or projects
为选中的通知待办设置项目|Set a project for the selected notification task
保存项目|Save project
关闭编辑|Close editor
已保存待办并设置稍后提醒。|Task saved with a reminder.
已转为通知待办。|Saved as a notification task.
通知待办内容已隐藏|Notification task content is hidden
恢复待办|Reopen task
标记完成|Complete task
设置项目|Set project
移除待办|Remove task
还没有匹配的通知待办。|No matching notification tasks.
通知待办 {0} 项 · 未完成 {1} 项|{0} notification tasks · {1} open
通知待办已保存。|Notification task saved.
无法保存通知待办：|Could not save the notification task: 
通知待办到时间了|Notification task is due
打开通知待办查看。|Open notification tasks to view it.
通知待办提醒暂时无法保存，请检查数据目录。|Notification reminder state could not be saved. Check the data folder.
通知待办已达 100 项，请先移除已完成事项。|There are already 100 notification tasks. Remove finished items first.
项目名称最多 60 字，不能包含控制字符。|Project names allow up to 60 characters without control characters.
待办不存在。|The task no longer exists.
转为待办|Save as task
15 分钟后提醒|Remind in 15 min
1 小时后提醒|Remind in 1 hour
固定为待办|Save and pin task
归档到项目…|Assign to project…
右键可转为待办、稍后提醒或归档到项目。|Right-click to save as a task, snooze or assign to a project.
点击查看任务动态|Click to view task activities
提醒时间|Reminder time
已提醒|Reminded");
            Map("批处理正在安全结束，请等待当前操作完成后再退出。|The batch is finishing safely. Wait for the operation to finish before exiting.");
            Map("批量处理文件|Batch process files\n局域网分享文件|Share files over LAN\n无法接收文件：|Could not accept the files: ");
            Map("等待的文件已出现|The watched file appeared\n守望的进程已退出|The watched process exited\n服务就绪|Service readiness\n等待检查|Waiting for a check");
            Pattern("活动 {0} 项 · 共 {1} 项", "{0} active · {1} total");
            Pattern("通知待办 {0} 项 · 未完成 {1} 项", "{0} notification tasks · {1} open");
            Pattern("提醒时间 · {0}", "Reminder time · {0}");
            Pattern("已提醒 · {0}", "Reminded · {0}");
        }
    }
}
