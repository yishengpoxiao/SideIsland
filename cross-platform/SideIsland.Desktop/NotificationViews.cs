using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.Input.Platform;
using SideIsland.Core;
using SideIsland.Desktop.Platforms;

namespace SideIsland.Desktop;
public sealed partial class IslandWindow
{
    private NotificationCenter? notificationCenter;
    private NotificationBridge? notificationBridge;
    private NotificationFilter notificationFilter;
    private string notificationSearch = "";
    private StackPanel? notificationRows;
    private TextBlock? notificationSummary;
    private int notificationDirty;

    private void EnsureNotifications()
    {
        if (notificationCenter is not null) return;
        notificationCenter = new NotificationCenter(Path.Combine(store.DirectoryPath, "notifications.json"));
        notificationBridge = new NotificationBridge(notificationCenter);
        notificationCenter.Changed += () => Interlocked.Exchange(ref notificationDirty, 1);
        notificationBridge.Changed += () => Dispatcher.UIThread.Post(() => { if (!closing) Report(notificationBridge.Status); });
    }
    private void DisposeNotifications() { notificationBridge?.Dispose(); notificationCenter?.Save(); }
    private void PublishInternalNotification(string title, string message)
    {
        EnsureNotifications(); notificationCenter!.Publish(new("side-island", "Side Island", title, message, DateTimeOffset.UtcNow, Internal: true));
    }
    private void RenderNotificationCenter()
    {
        EnsureNotifications();
        body.Children.Add(Hint(T("已接入来源的消息与岛内提醒。读取范围取决于系统接口；通知接入在工具中主动开启。", "Messages from connected sources and island reminders. System access determines coverage; enable connections explicitly.")));
        notificationSummary = Text("", 16); body.Children.Add(notificationSummary);
        var filter = new ComboBox { ItemsSource = new[] { T("全部消息", "All messages"), T("未读消息", "Unread"), T("重要消息", "Important") }, SelectedIndex = (int)notificationFilter, Margin = new(0, 6) };
        filter.SelectionChanged += (_, _) => { notificationFilter = (NotificationFilter)Math.Max(0, filter.SelectedIndex); RefreshNotificationRows(); };
        body.Children.Add(filter);
        var search = new TextBox { Text = notificationSearch, PlaceholderText = T("搜索应用、标题或内容", "Search app, title or message"), Margin = new(0, 0, 0, 6) };
        search.TextChanged += (_, _) => { notificationSearch = search.Text ?? ""; RefreshNotificationRows(); }; body.Children.Add(search);
        body.Children.Add(Actions(Button(T("全部已读", "Read all"), () => { notificationCenter!.MarkAllRead(); RefreshNotificationRows(); }), Button(T("清空消息", "Clear"), () => { notificationCenter!.Clear(); RefreshNotificationRows(); }), Button(T("接入与规则", "Sources / rules"), () => { selected = "notifications"; page = "tools"; RenderPage(); })));
        notificationRows = new StackPanel { Spacing = 2 }; body.Children.Add(notificationRows); RefreshNotificationRows();
    }
    private void RefreshNotificationRows()
    {
        if (notificationRows is null || notificationCenter is null) return;
        notificationRows.Children.Clear();
        var messages = notificationCenter.Filter(notificationFilter).Where(item => notificationSearch.Length == 0 ||
            (item.SourceName + "\n" + item.Title + "\n" + item.Body).Contains(notificationSearch, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (notificationSummary is not null) notificationSummary.Text = T($"未读 {notificationCenter.UnreadCount} · 当前 {messages.Length}", $"{notificationCenter.UnreadCount} unread · {messages.Length} shown");
        foreach (var item in messages.Take(150))
        {
            var panel = new StackPanel { Spacing = 4 };
            panel.Children.Add(Hint(item.SourceName + " · " + item.UpdatedAt.ToLocalTime().ToString("HH:mm") + (item.Count > 1 ? " · ×" + item.Count : "") + (item.Priority > 0 ? " · ★" : "")));
            panel.Children.Add(Text(item.Title, 14)); if (item.Body.Length > 0) panel.Children.Add(Text(item.Body, 12));
            panel.Children.Add(Button(item.Read ? T("标为未读", "Mark unread") : T("标为已读", "Mark read"), () => { notificationCenter.MarkRead(item.Id, !item.Read); RefreshNotificationRows(); }));
            notificationRows.Children.Add(Card(panel));
        }
        if (messages.Length == 0) notificationRows.Children.Add(Card(Hint(T("没有匹配的消息。", "No matching messages."))));
        if (messages.Length > 150) notificationRows.Children.Add(Hint(T("页面显示最新 150 条，使用搜索进一步筛选。", "The page shows the latest 150 matches. Search to narrow the list.")));
    }
    private void RenderNotificationSettings()
    {
        EnsureNotifications(); var center = notificationCenter!; var bridge = notificationBridge!;
        body.Children.Add(Hint(T("读取默认关闭，每次启动由你主动开启。禁用某应用只影响岛内显示，不修改系统通知设置。", "Reading starts off each launch. Disabling an app affects island display and does not change system notification settings.")));
        var sources = new StackPanel { Spacing = 6 };
        if (OperatingSystem.IsMacOS())
        {
            sources.Children.Add(Hint(T("Mac 使用辅助功能读取通知中心当前可见的通知；不会读取受保护的通知数据库。打开通知中心后可读取其可见列表，后台历史不保证可见。", "Mac Accessibility reads visible notification banners and the open Notification Center. It does not read the protected notification database; hidden history is not guaranteed.")));
            sources.Children.Add(AsyncButton(bridge.IsMacEnabled ? T("停止 Mac 通知读取", "Stop Mac reader") : T("开启 Mac 通知读取", "Enable Mac reader"), async () => { if (testMode) return; var error = await bridge.SetMacAccessibilityAsync(!bridge.IsMacEnabled, lifetime.Token); Report(error ?? bridge.Status); RenderPage(); }));
        }
        else if (OperatingSystem.IsLinux())
        {
            sources.Children.Add(Hint(T("Ubuntu 监听会话总线中的新通知。桌面总线可能拒绝监测；不会替换原通知服务或自动更改策略。", "Ubuntu monitors new notifications on the session bus. Desktop policy may deny monitoring; the notification service and policy are not replaced.")));
            sources.Children.Add(AsyncButton(bridge.IsLinuxEnabled ? T("停止 Ubuntu 通知读取", "Stop Ubuntu reader") : T("开启 Ubuntu 通知读取", "Enable Ubuntu reader"), async () => { if (testMode) return; var error = await bridge.SetLinuxMonitorAsync(!bridge.IsLinuxEnabled, lifetime.Token); Report(error ?? bridge.Status); RenderPage(); }));
        }
        else sources.Children.Add(Hint(T("当前为跨平台界面验证主机；系统通知桥只在 Mac / Ubuntu 上启用。", "This host validates the portable interface; native notification readers run on Mac / Ubuntu.")));
        sources.Children.Add(Hint(bridge.Status)); body.Children.Add(Card(sources));
        var local = new StackPanel { Spacing = 6 };
        local.Children.Add(Text(T("本机通知桥", "Local notification bridge"), 14));
        local.Children.Add(Hint(T("供你自己的脚本或桌面扩展发送消息。只绑定 127.0.0.1，必须携带本次启动的令牌；停止后令牌失效。", "For your scripts or desktop extensions. Binds to 127.0.0.1 and requires this session's token; stopping expires it.")));
        local.Children.Add(AsyncButton(bridge.IsHttpEnabled ? T("停止本机接入", "Stop local input") : T("开启本机接入", "Start local input"), async () => { if (testMode) return; var error = await bridge.SetHttpAsync(!bridge.IsHttpEnabled, 0, lifetime.Token); Report(error ?? bridge.Status); RenderPage(); }));
        if (bridge.IsHttpEnabled)
        {
            local.Children.Add(Hint(bridge.Url));
            local.Children.Add(AsyncButton(T("复制接入示例", "Copy request example"), async () => { if (!testMode && Clipboard is not null) await Clipboard.SetTextAsync("curl -X POST '" + bridge.Url + "' -H 'Authorization: Bearer " + bridge.Token + "' -H 'Content-Type: application/json' --data '{\"sourceId\":\"my-script\",\"sourceName\":\"My script\",\"title\":\"Build finished\",\"body\":\"Ready\"}'"); }));
        }
        body.Children.Add(Card(local));
        body.Children.Add(Check(T("在本机保存通知正文历史", "Save notification history locally"), center.PersistHistory, value => { center.SetPersistHistory(value); if (center.LastError is string error) Report(error); RenderPage(); }));
        if (center.LastError is string saveError) body.Children.Add(Hint(saveError));
        body.Children.Add(Hint(T("默认只保存应用规则。开启历史后，消息正文会写入本机数据目录。", "Only app rules are saved by default. Enabling history writes message content to the local data directory.")));
        foreach (var app in center.Applications)
        {
            var group = new StackPanel { Spacing = 6 };
            group.Children.Add(Check(app.Name, app.Enabled, value => center.SetApplicationEnabled(app.SourceId, value)));
            var priority = new ComboBox { ItemsSource = new[] { T("普通", "Normal"), T("重要", "Important"), T("最高优先", "Highest priority") }, SelectedIndex = app.Priority };
            priority.SelectionChanged += (_, _) => center.SetApplicationPriority(app.SourceId, Math.Max(0, priority.SelectedIndex)); group.Children.Add(priority); body.Children.Add(Card(group));
        }
        if (center.Applications.Count == 0) body.Children.Add(Hint(T("收到来源消息后，可在这里禁用该应用或设置优先级。", "Once an app sends a message, disable it or set its priority here.")));
        body.Children.Add(Button(T("打开消息中心", "Open message center"), () => SelectPage("messages")));
    }
}
