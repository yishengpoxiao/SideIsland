namespace CodexIsland
{
    internal static partial class UiLocalization
    {
        private static void QuickActionCatalog()
        {
            Map(@"专注时长（分钟）|Focus duration (minutes)
保存并重置|Save and reset
1—1440 分钟，按 Enter 保存并重置为专注|1–1440 minutes. Press Enter to save and reset to focus.
1—1440 分钟；保存会重置当前计时，不会自动开始。|1–1440 minutes. Saving resets the current timer without starting it.
收起后点击计时组件，可开始、暂停或继续当前计时。\n倒计时包含电脑休眠时间；到点后提醒一次。\n切换预设会重置当前计时。退出应用会结束计时。|Click the compact timer to start, pause or resume.\nCountdown includes sleep time and notifies once on completion.\nChanging presets resets the timer. Exiting the app ends it.
点击停止保持唤醒|Click to stop keeping awake
点击查看额度|Click to view usage limits
点击查看通知|Click to view notifications
点击打开日期与时区|Click to open dates and time zones
点击查看系统状态|Click to view system status
按住拖动|Hold to drag
右键打开组件设置|Right-click for widget settings
设置专注时长…|Set focus duration…
设置保持唤醒…|Keep-awake settings…
点击组件快捷操作 · 按住拖动\n悬停星标或空白处展开|Click a widget for its quick action · Hold to drag\nHover the star or blank area to expand");
            Pattern("专注 {0} 分", "Focus {0} min");
            Pattern("点击：{0} {1}", "Click: {0} {1}");
            Pattern("点击保持唤醒 {0} 分钟", "Click to keep awake for {0} minutes");
        }
    }
}
