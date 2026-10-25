namespace CodexIsland
{
    internal static partial class UiLocalization
    {
        private static void BuildAutoSceneWidgetCatalog()
        {
            Map(@"自动场景规则|Automatic scene rules
按当前应用、时间、显示器和电源切换布局；多个条件需同时满足。默认关闭，手动切换布局后暂停自动切换 10 分钟。|Switch layouts by app, time, display and power. All conditions must match. Disabled by default; manual layout changes pause rules for 10 minutes.
启用自动场景切换|Enable automatic scene switching
保存全部规则|Save all rules
取消未保存修改|Discard unsaved changes
暂停 30 分钟|Pause for 30 minutes
恢复自动规则|Resume rules
规则编辑|Rule editor
规则名称|Rule name
启用此规则|Enable this rule
程序名称（留空不限）|Executable name (blank for any)
填入最近的前台应用|Use the last foreground app
开始时间 HH:mm（留空不限）|Start HH:mm (blank for any time)
结束时间 HH:mm（可跨午夜）|End HH:mm (may cross midnight)
显示器名称（留空不限）|Display name (blank for any)
不限电源|Any power source
接通电源|Plugged in
使用电池|On battery
电源条件|Power condition
目标场景布局|Target layout
优先级 0—100（较高优先）|Priority 0–100 (higher wins)
切换冷却时间（秒）|Switch cooldown (seconds)
添加／更新规则|Add / update rule
新建规则|New rule
规则标识无效。|Invalid rule ID.
规则字段无效。|Invalid rule fields.
请输入不超过 60 字符的规则名称。|Enter a rule name up to 60 characters.
应用条件只填写程序名称，例如 Code.exe。|Enter an executable name only, such as Code.exe.
显示器名称无效。|Invalid display name.
时间条件需同时填写有效的 HH:mm 开始与结束时间。|Enter both start and end times in HH:mm format.
电源条件无效。|Invalid power condition.
请选择目标场景布局。|Choose a target layout.
优先级范围为 0—100，冷却时间为 5—3600 秒。|Priority must be 0–100; cooldown must be 5–3600 seconds.
至少设置一个应用、时间、显示器或电源条件。|Set at least one app, time, display or power condition.
最多保存 40 条自动规则。|You can save up to 40 rules.
暂未取得其他前台应用，请切换到目标应用后再回来。|No recent foreground app is available. Switch to the target app, then return.
优先级和冷却时间必须是整数。|Priority and cooldown must be whole numbers.
目标布局已不存在，请重新选择。|The target layout no longer exists. Choose another.
规则已加入编辑列表，点击“保存全部规则”后生效。|Rule added to the draft. Save all rules to apply it.
规则已移出编辑列表，保存后生效。|Rule removed from the draft. Save to apply.
自动规则已保存。|Automatic rules saved.
自动规则未能保存，编辑内容仍保留。|Could not save automatic rules. Your draft is preserved.
自动规则已关闭。|Automatic rules are off.
自动规则监测中；应用稳定后再切换。|Monitoring rules; waiting for a stable app.
自动布局暂时无法应用，已暂停 30 秒。|Could not apply the automatic layout. Paused for 30 seconds.
组件布局编辑器|Widget layout editor
每个场景可保存 2—6 个组件。收起时同时显示两个，其他组件可定时轮播；拖动预览卡片或使用方向按钮排序。|Save 2–6 widgets per layout. Two are visible at once; the rest can rotate. Drag preview cards or use the move buttons to reorder.
编辑的场景布局|Layout to edit
启用此组件布局|Enable this widget layout
所有场景的默认布局|Default for all layouts
选中组件|Selected widget
组件尺寸|Widget size
小|Small
标准|Standard
大|Large
前移组件|Move earlier
后移组件|Move later
添加组件|Add widget
移除组件|Remove widget
轮播间隔（秒；0 为固定前两个）|Rotation interval (seconds; 0 fixes the first two)
新通知可临时置顶|Temporarily show new notifications
通知显示优先级 0—100|Notification priority 0–100
专注完成可临时置顶|Temporarily show completed focus
专注完成显示优先级 0—100|Focus completion priority 0–100
任务状态可临时置顶|Temporarily show task activity
任务显示优先级 0—100|Task activity priority 0–100
通知和专注完成显示 8 秒后恢复；任务进行中可持续显示，结束后 8 秒恢复。优先级较高者先显示；任务还需在任务动态中开启收起显示。|Notifications and completed focus restore after 8 seconds. Active tasks remain visible and restore 8 seconds after ending. Higher priority wins; enable collapsed task display in Task activities too.
组件布局启用时优先于整块时钟、专注和自动音乐。|Enabled widget layouts override full clock, focus and automatic music views.
保存组件布局|Save widget layout
组件顺序已调整，保存后生效。|Widget order changed. Save to apply.
组件池最多包含 6 个组件。|The widget pool supports up to 6 widgets.
请至少保留两个组件。|Keep at least two widgets.
轮播间隔和优先级必须是整数。|Rotation interval and priorities must be whole numbers.
轮播为 0 或 5—60 秒，优先级为 0—100；请检查组件设置。|Use 0 or 5–60 seconds for rotation and 0–100 for priorities.
组件布局无效。|Invalid widget layout.
组件布局数量超过限制。|Too many widget layouts.
组件布局已保存。|Widget layout saved.
组件布局未能保存，编辑内容仍保留。|Could not save the widget layout. Your draft is preserved.");
            Pattern("自动切换暂停至 {0}", "Automatic switching paused until {0}");
            Pattern("已自动应用规则：{0}", "Automatically applied rule: {0}");
        }
    }
}
