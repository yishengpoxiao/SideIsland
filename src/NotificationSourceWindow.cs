using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodexIsland
{
    internal partial class IslandWindow
    {
        private void ManageNotificationApps()
        {
            notificationPreferences.Observe(notificationFeed.Items);
            collapseTimer.Stop(); hoverTimer.Stop(); dialogOpen = true;
            DismissNotificationPeek(true);
            try
            {
                var manager = new NotificationSourceWindow(notificationPreferences, SetNotificationSourceBlocked, () => { notificationCards.Clear(); UpdateNotificationList(); }) { Owner = this, Topmost = Topmost };
                manager.ShowDialog();
            }
            finally { dialogOpen = false; UpdateNotificationList(); }
        }

        private void SetNotificationSourceBlocked(string sourceKey, bool blocked)
        {
            if (notificationPreferences == null || !notificationPreferences.SetBlocked(sourceKey, blocked)) return;
            // Discard a read that began before this click. The feed also gates every
            // candidate against its current switches, protecting later snapshots.
            notificationGeneration++;
            notificationFeed.SetSourceBlocked(sourceKey, blocked);
            DismissNotificationPeek(true);
            UpdateNotificationList();
            if (notificationPreferences.LastError.Length > 0)
                notificationExplanation.Text = notificationPreferences.LastError;
            if (!blocked) PollNotifications();
        }
    }

    internal sealed class NotificationSourceWindow : Window
    {
        private readonly NotificationPreferences preferences;
        private readonly Action<string, bool> change;
        private readonly StackPanel rows;
        private readonly TextBox search;
        private readonly TextBlock summary, saved;
        private readonly CheckBox onlyBlocked;
        private readonly Action policyChanged;

        public NotificationSourceWindow(NotificationPreferences store, Action<string, bool> changeSource, Action changePolicy = null)
        {
            preferences = store; change = changeSource; policyChanged = changePolicy;
            Title = "管理应用通知 · 灵动岛"; Width = 540; Height = 620; MinWidth = 320; MinHeight = 360;
            MaxWidth = Math.Max(MinWidth, SystemParameters.WorkArea.Width - 40); MaxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height - 40);
            WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            Background = Display.Brush("#111518"); Foreground = Display.Brush("#EAF4EE");
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"); FontSize = 12; UseLayoutRounding = true; SnapsToDevicePixels = true;
            UiLocalization.Mark(this, Window.TitleProperty, Title); UiTheme.Install(Resources);
            var root = new Grid { Margin = new Thickness(16) };
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Content = root;
            // Let the whole form scroll in a short window. A fixed header used to
            // consume almost all of the app list at 360 px; completion stays visible.
            var body = new StackPanel();
            var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Visible, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            root.Children.Add(scroll);
            var heading = IslandWindow.Text("选择灵动岛显示的通知", 18, "#EFF8F3", true); heading.TextWrapping = TextWrapping.Wrap; body.Children.Add(heading);
            var intro = IslandWindow.Text("禁用后，该软件的通知、未读数量和消息预览都会从岛内隐藏。Windows 和软件本身的通知不受影响。", 12, "#A6B8AF");
            intro.TextWrapping = TextWrapping.Wrap; intro.Margin = new Thickness(0, 6, 0, 8); body.Children.Add(intro);
            var policy = new StackPanel();
            body.Children.Add(new Border { Child = policy, Background = Display.Brush("#1A2323"), BorderBrush = Display.Brush("#293A32"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(11), Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 8) });
            var merge = PanelActionLayout.Caption(UiLocalization.Content(new CheckBox { IsChecked = preferences.MergeDuplicates, Foreground = Display.Brush("#C3D4CA"), Margin = new Thickness(0, 0, 0, 6), VerticalContentAlignment = VerticalAlignment.Center }, "合并同一应用 60 秒内的重复消息"));
            merge.SetResourceReference(FrameworkElement.StyleProperty, "Switch.CheckBox");
            merge.Checked += (s, e) => { preferences.SetMergeDuplicates(true); RefreshSaveStatus(); if (policyChanged != null) policyChanged(); };
            merge.Unchecked += (s, e) => { preferences.SetMergeDuplicates(false); RefreshSaveStatus(); if (policyChanged != null) policyChanged(); }; policy.Children.Add(merge);
            var policyHint = IslandWindow.Text("普通应用只累计未读；重要应用优先显示，并可触发消息预览。", 11, "#8EA799"); policyHint.TextWrapping = TextWrapping.Wrap; policy.Children.Add(policyHint);
            var searchLabel = IslandWindow.Text("搜索应用名称或标识", 10, "#8EA799"); searchLabel.TextWrapping = TextWrapping.Wrap; searchLabel.Margin = new Thickness(0, 0, 0, 6); body.Children.Add(searchLabel);
            search = UiLocalization.ToolTip(new TextBox { MinHeight = 32, Padding = new Thickness(8, 5, 8, 5), VerticalContentAlignment = VerticalAlignment.Center }, "搜索软件名称或应用标识");
            body.Children.Add(search);
            var filter = new Grid { Margin = new Thickness(0, 6, 0, 6) }; filter.ColumnDefinitions.Add(new ColumnDefinition()); filter.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) }); filter.ColumnDefinitions.Add(new ColumnDefinition()); body.Children.Add(filter);
            summary = IslandWindow.Text("", 11, "#94AD9F"); summary.VerticalAlignment = VerticalAlignment.Center; summary.TextWrapping = TextWrapping.Wrap; filter.Children.Add(summary);
            onlyBlocked = PanelActionLayout.Caption(UiLocalization.Content(new CheckBox { Foreground = Display.Brush("#C3D4CA"), VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center }, "只看已禁用")); Grid.SetColumn(onlyBlocked, 2); filter.Children.Add(onlyBlocked);
            rows = new StackPanel();
            body.Children.Add(rows);
            var footer = new StackPanel { Margin = new Thickness(0, 8, 0, 0) }; Grid.SetRow(footer, 1); root.Children.Add(footer);
            saved = IslandWindow.Text("", 11, "#8EA799"); saved.TextWrapping = TextWrapping.Wrap;
            footer.Children.Add(new ScrollViewer { Content = saved, MaxHeight = 64, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
            var done = IslandWindow.Button("完成", Close); done.Background = Display.Brush("#3B6755"); done.Foreground = Display.Brush("#A1EACF");
            var completion = PanelActionLayout.Create(1, done); completion.Margin = new Thickness(0, 6, 0, 0); footer.Children.Add(completion);
            search.TextChanged += (s, e) => Rebuild(); onlyBlocked.Checked += (s, e) => Rebuild(); onlyBlocked.Unchecked += (s, e) => Rebuild();
            Rebuild();
        }

        private void RefreshSaveStatus()
        {
            UiLocalization.Text(saved, preferences.LastError.Length > 0 ? preferences.LastError : "更改立即生效并自动保存。恢复后只提醒新通知；示例应用的开关不保存。");
            saved.Foreground = Display.Brush(preferences.LastError.Length > 0 ? "#E9BA83" : "#8EA799");
        }
        private void Rebuild()
        {
            List<NotificationSourcePreference> sources = preferences.Sources;
            int count = sources.Count(source => source.Blocked);
            UiLocalization.Text(summary, sources.Count + " 个应用 · " + count + " 个已禁用");
            RefreshSaveStatus();
            rows.Children.Clear();
            string query = (search.Text ?? "").Trim();
            foreach (var source in sources.Where(source => (onlyBlocked.IsChecked != true || source.Blocked) && (query.Length == 0 || source.AppName.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0 || source.AppId.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)))
            {
                var labels = new StackPanel();
                var name = IslandWindow.Text(source.AppName + (source.IsDemo ? " · " + UiLocalization.Localize("示例") : ""), 13, "#E5F0E8", true, false); name.TextWrapping = TextWrapping.Wrap; name.ToolTip = source.AppName; labels.Children.Add(name);
                bool missingIdentity = String.IsNullOrWhiteSpace(source.AppId);
                string identity = missingIdentity ? "无应用标识 · 按此名称匹配" : NotificationFeed.SanitizeText(source.AppId, 160);
                var id = IslandWindow.Text(identity, 10, "#7F988A", false, missingIdentity); id.TextWrapping = TextWrapping.Wrap; id.ToolTip = missingIdentity ? UiLocalization.Localize(identity) : identity; id.Margin = new Thickness(0, 4, 0, 6); labels.Children.Add(id);
                var priority = UiTheme.CompactChoice(new ComboBox { MinHeight = 32, Padding = new Thickness(8, 5, 8, 5), HorizontalContentAlignment = HorizontalAlignment.Stretch });
                foreach (string caption in new[] { "普通（仅累计未读）", "重要（优先显示与预览）" })
                {
                    var option = PanelActionLayout.Caption(UiLocalization.Content(new ComboBoxItem(), caption));
                    option.SetBinding(FrameworkElement.MaxWidthProperty, new System.Windows.Data.Binding("ActualWidth") { Source = priority });
                    priority.Items.Add(option);
                }
                priority.SelectedIndex = source.Priority == 1 ? 1 : 0;
                string priorityKey = source.Key; priority.SelectionChanged += (s, e) => { preferences.SetPriority(priorityKey, priority.SelectedIndex); RefreshSaveStatus(); if (policyChanged != null) policyChanged(); }; labels.Children.Add(priority);
                var actions = new Grid { Margin = new Thickness(0, 6, 0, 0) }; actions.ColumnDefinitions.Add(new ColumnDefinition()); actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) }); actions.ColumnDefinitions.Add(new ColumnDefinition()); labels.Children.Add(actions);
                var status = IslandWindow.Text(source.Blocked ? "已在岛内禁用" : "允许在岛内显示", 11, source.Blocked ? "#E9BA83" : "#91C7A9"); status.TextWrapping = TextWrapping.Wrap; status.VerticalAlignment = VerticalAlignment.Center; actions.Children.Add(status);
                string key = source.Key; bool blocked = source.Blocked;
                var toggle = IslandWindow.Button(blocked ? "恢复显示" : "禁用通知", () => { change(key, !blocked); Rebuild(); }); toggle.FontSize = 11;
                var toggleGroup = PanelActionLayout.Create(1, toggle); Grid.SetColumn(toggleGroup, 2); actions.Children.Add(toggleGroup);
                rows.Children.Add(new Border { Child = labels, Padding = new Thickness(12), CornerRadius = new CornerRadius(12), Background = Display.Brush("#1A2323"), BorderBrush = Display.Brush("#293A32"), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 6) });
            }
            if (rows.Children.Count == 0)
            {
                var empty = IslandWindow.Text(sources.Count == 0 ? "收到软件的系统通知后，它会出现在这里。\n也可以直接在消息卡片上屏蔽应用。" : "没有符合条件的应用。", 12, "#8EA799"); empty.TextWrapping = TextWrapping.Wrap; empty.TextAlignment = TextAlignment.Center; empty.Margin = new Thickness(12, 12, 12, 6); rows.Children.Add(empty);
            }
        }
    }
}
