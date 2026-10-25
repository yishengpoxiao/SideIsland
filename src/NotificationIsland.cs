using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace CodexIsland
{
    internal partial class IslandWindow
    {
        private readonly NotificationListenerService notificationService = new NotificationListenerService();
        private NotificationSyncRunner notificationSync;
        private NotificationAccessState? notificationCachedAccess;
        private readonly NotificationFeed notificationFeed = new NotificationFeed();
        private NotificationPreferences notificationPreferences;
        private readonly DispatcherTimer notificationTimer = new DispatcherTimer();
        private readonly DispatcherTimer peekTimer = new DispatcherTimer();
        private StackPanel statusPage, notificationPage;
        private ListBox notificationList;
        private FrameworkElement notificationListScroll;
        private readonly Dictionary<string, NotificationCardEntry> notificationCards = new Dictionary<string, NotificationCardEntry>(StringComparer.Ordinal);
        private Border peekPanel;
        private TextBlock notificationState, notificationExplanation, notificationCount, compactNotificationIcon, peekSource, peekTitle, peekBody, peekKind, peekHint;
        private Button notificationAccess, notificationsTab, statusTab;
        private readonly Dictionary<NotificationFilter, Button> filterButtons = new Dictionary<NotificationFilter, Button>();
        private NotificationFilter notificationFilter = NotificationFilter.All;
        private bool notificationPageSelected = true, notificationPolling, notificationBaseline = true, peeking;
        private bool notificationHasSynced, notificationAuthorizing;
        private string notificationReadFailure;
        private string peekAppId = "";
        private IslandNotification peekMessage;
        private int notificationGeneration;
        private bool NotificationDemo { get { return demo && notificationSync == null; } }

        // Internal dependency injection keeps slow-system regressions completely
        // isolated from real notifications and the Windows permission service.
        internal IslandWindow(bool demoMode, NotificationSyncRunner notificationSource) : this(demoMode) { notificationSync = notificationSource; }

        private void InitializeCompactNotifications(TextBlock brand)
        {
            compactNotificationIcon = brand;
            brand.ToolTip = "消息中心 · 悬停展开";
        }

        private void BuildNotificationInterface(StackPanel parent, Grid container)
        {
            notificationPreferences = new NotificationPreferences(demo);
            foreach (var source in notificationPreferences.Sources)
                if (source.Blocked) notificationFeed.SetSourceBlocked(source.Key, true);
            notificationsTab = Button("消息", () => SelectPage(true));
            statusTab = Button("状态", () => SelectPage(false));
            toolsTab = Button("工具", SelectToolsPage);
            var tabs = PanelActionLayout.Create(3, notificationsTab, statusTab, toolsTab);
            foreach (var tab in new[] { notificationsTab, statusTab, toolsTab }) tab.Padding = new Thickness(2, 5, 2, 5);
            expandedTabs = new Border { Child = tabs, Background = Display.Brush("#202D29"), Padding = new Thickness(4), CornerRadius = new CornerRadius(14), Margin = new Thickness(0, 0, 10, 10) };
            Grid.SetRow(expandedTabs, 1); expandedHost.Children.Add(expandedTabs);
            notificationPage = new StackPanel(); parent.Children.Add(notificationPage);
            // Translation and template layout can change static caption heights
            // after the initial fit. Refit as that live layout settles, rather
            // than leaving a stale limit until the first filter is selected.
            notificationPage.SizeChanged += (s, e) => UpdateNotificationViewport();

            var connection = new StackPanel();
            notificationState = Text("●  检查通知连接", 12, "#A1EACF", true); notificationState.TextWrapping = TextWrapping.Wrap; notificationState.LineHeight = 17; connection.Children.Add(notificationState);
            notificationExplanation = Text("", 11, "#99ADA2"); notificationExplanation.TextWrapping = TextWrapping.Wrap; notificationExplanation.LineHeight = 16; notificationExplanation.Margin = new Thickness(0, 4, 0, 0); connection.Children.Add(notificationExplanation);
            notificationAccess = PanelActionLayout.Caption(Button("连接系统通知", ConnectNotifications)); notificationAccess.MinHeight = 32; notificationAccess.Padding = new Thickness(8, 5, 8, 5); notificationAccess.Margin = new Thickness(0, 6, 0, 0); connection.Children.Add(notificationAccess);
            notificationPage.Children.Add(new Border { Background = Display.Brush("#1B2824"), CornerRadius = new CornerRadius(13), Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 8), Child = connection });

            var filterValues = new[] { NotificationFilter.All, NotificationFilter.WeChat, NotificationFilter.QQ, NotificationFilter.Other };
            var filterLabels = new[] { "全部", "微信", "QQ", "其他" };
            var filterActions = new Button[filterValues.Length];
            for (int i = 0; i < filterValues.Length; i++)
            {
                var selected = filterValues[i];
                var button = Button(filterLabels[i], () => SelectNotificationFilter(selected));
                filterActions[i] = button; filterButtons[selected] = button;
            }
            var filters = PanelActionLayout.Create(4, 52, filterActions); filters.Margin = new Thickness(0, 0, 0, 8);
            foreach (var button in filterActions) button.Padding = new Thickness(6, 5, 6, 5);
            notificationPage.Children.Add(filters);
            notificationCount = Text("最近通知", 10, "#94A69B"); notificationCount.TextWrapping = TextWrapping.Wrap; notificationCount.LineHeight = 15; notificationCount.Margin = new Thickness(0, 0, 0, 6); notificationPage.Children.Add(notificationCount);
            notificationList = new ListBox { MaxHeight = 232, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), Focusable = false, IsTabStop = false };
            // User scrolling owns the viewport immediately. The removed-card
            // canvas uses viewport coordinates, so do not leave ghosts pinned
            // over content the user is moving. Keep the input event untouched.
            notificationList.PreviewMouseWheel += (s, e) => StopNotificationCardTransition();
            notificationList.PreviewTouchDown += (s, e) => StopNotificationCardTransition();
            // A finite, reserved viewport keeps the actions below the list in
            // place when a filter returns one card, no cards, or fifty cards.
            // MaxHeight is fitted to the live window and static controls.
            notificationListHost = new Grid { MaxHeight = 232, ClipToBounds = true };
            notificationList.SetBinding(FrameworkElement.MaxHeightProperty, new Binding("MaxHeight") { Source = notificationListHost });
            notificationList.SetBinding(FrameworkElement.HeightProperty, new Binding("MaxHeight") { Source = notificationListHost });
            ScrollViewer.SetCanContentScroll(notificationList, true);
            ScrollViewer.SetVerticalScrollBarVisibility(notificationList, ScrollBarVisibility.Hidden);
            ScrollViewer.SetHorizontalScrollBarVisibility(notificationList, ScrollBarVisibility.Disabled);
            ScrollViewer.SetPanningMode(notificationList, PanningMode.VerticalOnly);
            VirtualizingPanel.SetIsVirtualizing(notificationList, true);
            VirtualizingPanel.SetVirtualizationMode(notificationList, VirtualizationMode.Recycling);
            VirtualizingPanel.SetScrollUnit(notificationList, ScrollUnit.Pixel);
            VirtualizingPanel.SetCacheLength(notificationList, new VirtualizationCacheLength(0));
            notificationList.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
            var itemStyle = new Style(typeof(ListBoxItem));
            itemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
            itemStyle.Setters.Add(new Setter(UIElement.FocusableProperty, false));
            var itemTemplate = new ControlTemplate(typeof(ListBoxItem));
            itemTemplate.VisualTree = new FrameworkElementFactory(typeof(ContentPresenter));
            itemStyle.Setters.Add(new Setter(Control.TemplateProperty, itemTemplate));
            notificationList.ItemContainerStyle = itemStyle;
            notificationCardOverlay = new Canvas { IsHitTestVisible = false, ClipToBounds = true };
            notificationListHost.Children.Add(notificationList); notificationListHost.Children.Add(notificationCardOverlay);
            notificationListScroll = notificationListHost;
            notificationPage.Children.Add(notificationListScroll);

            var read = Button("岛内已读", MarkAllNotificationsRead);
            read.ToolTip = "只清除灵动岛未读标记，不改变微信或 QQ 的聊天状态";
            var clear = Button("清空岛内记录", ClearNotificationsWithMotion); clear.ToolTip = "只清空本次运行的岛内历史，不删除系统通知";
            var preferences = Button("管理应用", ManageNotificationApps); preferences.ToolTip = "选择哪些软件的通知在灵动岛内显示；不改变 Windows 或软件自己的通知设置";
            var test = Button("查看消息预览示例", () => AddDemoNotifications(true)); test.Background = Brushes.Transparent; test.Foreground = Display.Brush("#87A397");
            var actions = PanelActionLayout.Create(2, read, clear, preferences, test); actions.Margin = new Thickness(0, 8, 0, 0); notificationPage.Children.Add(actions);

            var peekStack = peekContent = new StackPanel();
            peekTranslation = new TranslateTransform(); peekStack.RenderTransform = peekTranslation;
            // Independent columns keep a long application name from pushing the
            // localized action hint off the edge. Notification text stays raw.
            var peekHeader = new Grid();
            peekHeader.ColumnDefinitions.Add(new ColumnDefinition());
            peekHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            peekHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            peekSource = Text("", 11, "#A1EACF", true, false); peekSource.TextTrimming = TextTrimming.CharacterEllipsis; peekSource.LineHeight = 16; peekSource.LineStackingStrategy = LineStackingStrategy.BlockLineHeight; peekSource.VerticalAlignment = VerticalAlignment.Center; peekSource.Margin = new Thickness(0, 0, 6, 0); peekHeader.Children.Add(peekSource);
            peekKind = Text("", 9, "#99ADA2", false, false); peekKind.LineHeight = 12; peekKind.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            var peekKindBadge = new Border { Background = Display.Brush("#202D29"), CornerRadius = new CornerRadius(6), Padding = new Thickness(5, 2, 5, 2), VerticalAlignment = VerticalAlignment.Center, Child = peekKind, Margin = new Thickness(0, 0, 8, 0) }; Grid.SetColumn(peekKindBadge, 1); peekHeader.Children.Add(peekKindBadge);
            peekHint = Text("点击查看", 10, "#879690"); peekHint.LineHeight = 16; peekHint.LineStackingStrategy = LineStackingStrategy.BlockLineHeight; peekHint.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(peekHint, 2); peekHeader.Children.Add(peekHint); peekStack.Children.Add(peekHeader);
            peekTitle = Text("", 14, "#F0F5F2", true, false); peekTitle.TextWrapping = TextWrapping.Wrap; peekTitle.LineHeight = 18; peekTitle.LineStackingStrategy = LineStackingStrategy.BlockLineHeight; peekTitle.MaxHeight = 36; peekTitle.TextTrimming = TextTrimming.CharacterEllipsis; peekTitle.Margin = new Thickness(0, 6, 0, 0); peekStack.Children.Add(peekTitle);
            peekBody = Text("", 12, "#A7BAB0", false, false); peekBody.TextWrapping = TextWrapping.Wrap; peekBody.LineHeight = 17; peekBody.LineStackingStrategy = LineStackingStrategy.BlockLineHeight; peekBody.MaxHeight = 34; peekBody.TextTrimming = TextTrimming.CharacterEllipsis; peekBody.Margin = new Thickness(0, 4, 0, 0); peekStack.Children.Add(peekBody);
            peekPanel = new Border { Padding = new Thickness(16, 12, 16, 12), Child = peekStack, Visibility = Visibility.Collapsed, Background = Brushes.Transparent, Cursor = Cursors.Hand };
            peekPanel.MouseLeftButtonUp += (s, e) => { OpenPeekTarget(); e.Handled = true; };
            container.Children.Add(peekPanel);
        }

        private void SelectPage(bool notifications)
        {
            SelectMainPage(false, notifications);
        }

        private void UpdatePageTabs()
        {
            if (notificationsTab == null || statusTab == null || toolsTab == null) return;
            var tabs = new[] { notificationsTab, statusTab, toolsTab };
            for (int i = 0; i < tabs.Length; i++)
            {
                bool selected = i == 2 ? toolsPageSelected : !toolsPageSelected && (i == 0) == notificationPageSelected;
                tabs[i].Background = selected ? Display.Brush("#31594B") : Brushes.Transparent;
                tabs[i].Foreground = Display.Brush(selected ? "#A1EACF" : "#8FA89A");
                tabs[i].BorderBrush = Brushes.Transparent;
                // Selection uses color and fill. Changing the weight also
                // changes centered glyph advances, even in an unchanged box.
                tabs[i].FontWeight = FontWeights.SemiBold;
            }
        }

        private void StartNotificationMonitor()
        {
            notificationTimer.Interval = TimeSpan.FromSeconds(3);
            notificationTimer.Tick += (s, e) => PollNotifications();
            peekTimer.Interval = TimeSpan.FromSeconds(6);
            peekTimer.Tick += (s, e) => { peekTimer.Stop(); if (peeking) SetExpanded(false, true); };
            if (!NotificationDemo) notificationTimer.Start();
            UpdateNotificationConnection(); UpdateNotificationList();
            if (!NotificationDemo) PollNotifications();
        }

        private void StopNotificationMonitor() { notificationTimer.Stop(); EndPeek(); notificationGeneration++; if (notificationSync != null) notificationSync.Dispose(); }

        private void ConfigureNotifications()
        {
            DismissNotificationPeek(true);
            DiscardTransitionSnapshot();
            if (systemDashboard != null) systemDashboard.SetPrivacy(settings.HideNotificationBody);
            notificationGeneration++; notificationBaseline = true;
            if (!settings.NotificationsEnabled) { notificationFeed.Clear(); notificationHasSynced = false; notificationReadFailure = null; }
            UpdateNotificationConnection(); UpdateNotificationList(); PollNotifications();
        }

        private async void PollNotifications()
        {
            if (NotificationDemo || shuttingDown || notificationPolling || notificationAuthorizing) return;
            if (!settings.NotificationsEnabled) { UpdateNotificationConnection(); return; }
            notificationPolling = true; int version = notificationGeneration;
            UpdateNotificationConnection();
            var source = notificationSync ?? (notificationSync = new NotificationSyncRunner(notificationService.ReadSnapshotAsync));
            try
            {
                var result = await source.SynchronizeAsync();
                if (shuttingDown || version != notificationGeneration || !settings.NotificationsEnabled) return;
                if (result.Access.HasValue) notificationCachedAccess = result.Access;
                if (!String.IsNullOrEmpty(result.Error)) { notificationReadFailure = result.Error; UpdateNotificationConnection(); return; }
                // A revocation or disabled permission is a connection state, never
                // an empty successful snapshot. Do not publish any accompanying data.
                if (result.Access != NotificationAccessState.Allowed)
                {
                    notificationHasSynced = false; notificationReadFailure = null; UpdateNotificationConnection(); return;
                }
                if (!result.ReadSucceeded) { notificationReadFailure = "通知读取未完成，稍后会自动重试。"; UpdateNotificationConnection(); return; }
                notificationPreferences.Observe(result.Messages);
                var added = notificationFeed.Merge(result.Messages, notificationBaseline); notificationBaseline = false;
                notificationHasSynced = true; notificationReadFailure = null;
                UpdateNotificationConnection(); UpdateNotificationListCore(false);
                var preview = added.Where(n => notificationPreferences.PriorityFor(n) == 1).OrderByDescending(n => n.CreatedAt).FirstOrDefault();
                if (preview == null && peeking && peekMessage != null)
                {
                    // OS replacement notifications retain their feed identity and
                    // are not arrivals. Keep an existing preview current without
                    // reopening an ended preview or superseding a new arrival.
                    var current = peekMessage;
                    string currentSource = String.IsNullOrWhiteSpace(current.AppId) ? current.AppName : current.AppId;
                    preview = notificationFeed.Items.FirstOrDefault(n => n.Id == current.Id && n.IsDemo == current.IsDemo &&
                        n.CreatedAt.Ticks == current.CreatedAt.Ticks && String.Equals(currentSource,
                        String.IsNullOrWhiteSpace(n.AppId) ? n.AppName : n.AppId, StringComparison.Ordinal));
                    // ShowPeek copies the latest model before de-duplicating its
                    // displayed signature. Identical or privacy-masked content
                    // therefore neither replays the transition nor renews timeout.
                }
                if (preview != null && settings.NotificationPreview && !settings.QuietPreviews && !expanded && !dialogOpen && IsVisible) ShowPeek(preview);
            }
            catch
            {
                if (!shuttingDown && version == notificationGeneration && settings.NotificationsEnabled)
                {
                    notificationReadFailure = "稍后会自动重试，也可检查系统通知访问权限。";
                    ApplyNotificationReadState(notificationHasSynced, true, notificationReadFailure);
                }
            }
            finally
            {
                notificationPolling = false;
                if (!shuttingDown)
                {
                    UpdateNotificationConnection();
                    if (version != notificationGeneration && settings.NotificationsEnabled) PollNotifications();
                }
            }
        }

        private void ApplyNotificationReadState(bool hasSnapshot, bool reading, string error)
        {
            notificationAccess.Content = "重新同步通知"; notificationAccess.IsEnabled = !reading && !notificationAuthorizing;
            if (!String.IsNullOrEmpty(error))
            {
                // Permission being allowed does not mean the last read succeeded.
                // Keep a failure visible during retries until a fresh snapshot arrives.
                notificationState.Text = "●  通知同步暂时失败"; notificationExplanation.Text = error;
                notificationAccess.Visibility = Visibility.Visible;
            }
            else
            {
                notificationState.Text = hasSnapshot ? "●  系统通知已连接" : "●  正在同步系统通知…";
                notificationExplanation.Text = hasSnapshot ? "自动汇集微信、QQ 等应用发出的系统通知。" : "首次读取正在后台进行，其他工具可继续使用。";
                notificationAccess.Visibility = Visibility.Collapsed;
            }
            UpdateNotificationViewport();
        }

        private void UpdateNotificationConnection()
        {
            UpdateNotificationConnectionCore();
            // Every permission/loading branch changes the surrounding controls.
            // Fit the list after their final text and visibility are known.
            UpdateNotificationViewport();
        }

        private void UpdateNotificationConnectionCore()
        {
            if (notificationState == null) return;
            notificationAccess.Visibility = Visibility.Visible;
            notificationAccess.IsEnabled = !notificationPolling && !notificationAuthorizing;
            if (NotificationDemo)
            {
                notificationState.Text = "●  演示模式 · 消息为虚构示例";
                notificationExplanation.Text = "微信、QQ 和其他应用的通知汇集在这里。";
                notificationAccess.Visibility = Visibility.Collapsed; return;
            }
            if (!settings.NotificationsEnabled)
            {
                notificationState.Text = "●  通知汇集已关闭"; notificationExplanation.Text = "可在设置中开启，额度监控仍会继续。"; notificationAccess.Content = "打开设置"; return;
            }
            if (!String.IsNullOrEmpty(notificationReadFailure)) { ApplyNotificationReadState(notificationHasSynced, notificationPolling, notificationReadFailure); return; }
            if (!notificationCachedAccess.HasValue)
            {
                notificationState.Text = "●  正在检查通知连接…";
                notificationExplanation.Text = "权限检查在后台进行，其他工具可继续使用。";
                notificationAccess.Visibility = Visibility.Collapsed; return;
            }
            switch (notificationCachedAccess.Value)
            {
                case NotificationAccessState.Allowed:
                    ApplyNotificationReadState(notificationHasSynced, notificationPolling, notificationReadFailure); break;
                case NotificationAccessState.NeedsPackage:
                    notificationState.Text = "●  安装通知版后即可连接";
                    notificationExplanation.Text = "Windows 要求通知访问使用已注册的应用。安装后，在这里授权连接。"; notificationAccess.Content = "安装通知版"; break;
                case NotificationAccessState.NotGranted:
                    notificationState.Text = "●  等待通知访问授权";
                    notificationExplanation.Text = "点击下方连接，并在 Windows 提示中允许访问。"; notificationAccess.Content = "连接系统通知"; break;
                case NotificationAccessState.Denied:
                    notificationState.Text = "●  通知访问未获允许";
                    notificationExplanation.Text = "请在 Windows 隐私设置中允许灵动岛访问通知。"; notificationAccess.Content = "打开通知权限设置"; break;
                case NotificationAccessState.Unsupported:
                    notificationState.Text = "●  此系统不支持通知读取"; notificationExplanation.Text = "需要 Windows 10 1607 或更新版本。"; notificationAccess.Visibility = Visibility.Collapsed; break;
                default:
                    notificationState.Text = "●  通知连接暂不可用"; notificationExplanation.Text = String.IsNullOrEmpty(notificationReadFailure) ? "请检查系统权限后重试。" : notificationReadFailure; notificationAccess.Content = "重新检查"; break;
            }
        }

        private async void ConnectNotifications()
        {
            if (!settings.NotificationsEnabled) { OpenSettings(); return; }
            if (demo || notificationPolling || notificationAuthorizing || shuttingDown) return;
            var status = notificationCachedAccess;
            if (!status.HasValue || !String.IsNullOrEmpty(notificationReadFailure) || status == NotificationAccessState.Allowed || status == NotificationAccessState.Error) { PollNotifications(); return; }
            if (status == NotificationAccessState.NeedsPackage)
            {
                string root = AppDomain.CurrentDomain.BaseDirectory;
                string installer = Path.Combine(root, "install-notifications.cmd");
                if (!File.Exists(installer)) installer = Path.Combine(Directory.GetParent(root.TrimEnd(Path.DirectorySeparatorChar)).FullName, "install-notifications.cmd");
                if (File.Exists(installer))
                {
                    try { Process.Start(new ProcessStartInfo(installer) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(installer) }); }
                    catch { AppNotice.Show(this, UiLocalization.Localize("无法启动安装向导，请手动运行 install-notifications.cmd。"), UiLocalization.Localize("灵动岛"), MessageBoxImage.Warning); }
                }
                else AppNotice.Show(this, UiLocalization.Localize("请从完整发布包运行 install-notifications.cmd 安装通知版。单独的 EXE 可继续监控额度。"), UiLocalization.Localize("灵动岛"));
                return;
            }
            if (status == NotificationAccessState.Denied) { OpenSystemNotificationSettings(); return; }
            if (status != NotificationAccessState.NotGranted) { PollNotifications(); return; }
            int version = notificationGeneration;
            notificationAuthorizing = true; notificationAccess.IsEnabled = false; dialogOpen = true; collapseTimer.Stop();
            try
            {
                await notificationService.RequestAccessAsync();
                if (!shuttingDown && version == notificationGeneration && settings.NotificationsEnabled)
                {
                    notificationBaseline = true; notificationCachedAccess = null; notificationReadFailure = null;
                }
            }
            catch { if (!shuttingDown && version == notificationGeneration && settings.NotificationsEnabled) notificationReadFailure = "未能请求通知访问，请在系统设置中检查权限。"; }
            finally
            {
                notificationAuthorizing = false; dialogOpen = false;
                if (!shuttingDown) { UpdateNotificationConnection(); PollNotifications(); }
            }
        }

        private void OpenSystemNotificationSettings()
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:privacy-notifications") { UseShellExecute = true }); }
            catch { AppNotice.Show(this, UiLocalization.Localize("请打开 Windows 设置 → 隐私和安全性 → 通知。"), UiLocalization.Localize("灵动岛")); }
        }

        private void UpdateNotificationList()
        {
            UpdateNotificationListCore(true);
        }

        private void UpdateNotificationListCore(bool discardTransition)
        {
            // Routine background synchronization must not finish a crossfade.
            // Explicit privacy, block and clear actions still discard
            // the outgoing image immediately through UpdateNotificationList.
            bool privacyChanged = notificationCards.Values.Any(entry => entry.HideBody != settings.HideNotificationBody);
            var before = CaptureNotificationCardLayout(!discardTransition, !privacyChanged && (!discardTransition || notificationCardSafeExplicitTransition));
            if (discardTransition || privacyChanged) DiscardTransitionSnapshot();
            if (notificationList == null) return;
            var messages = NotificationPresentation.Project(notificationFeed.GetItems(notificationFilter), notificationPreferences);
            UpdateNotificationCountText("最近通知  ·  " + messages.Count + " 条  ·  岛内未读 " + notificationFeed.UnreadCount, messages.Count, notificationFeed.UnreadCount, !discardTransition || notificationCardSafeExplicitTransition);
            foreach (var pair in filterButtons)
            {
                bool selected = pair.Key == notificationFilter;
                pair.Value.Background = Display.Brush(selected ? "#31594B" : "#1E2725");
                pair.Value.Foreground = Display.Brush(selected ? "#A1EACF" : "#8FA89A");
                pair.Value.BorderBrush = Brushes.Transparent;
            }
            var desired = new List<Border>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            if (messages.Count == 0)
            {
                var empty = new StackPanel { Margin = new Thickness(12) };
                var symbol = Text("○", 20, "#6EAA91"); symbol.HorizontalAlignment = HorizontalAlignment.Center; empty.Children.Add(symbol);
                var title = Text("此刻安静，消息来了会告诉你", 12, "#CADBD0"); title.TextWrapping = TextWrapping.Wrap; title.TextAlignment = TextAlignment.Center; title.LineHeight = 17; title.Margin = new Thickness(0, 6, 0, 4); empty.Children.Add(title);
                var hint = Text("应用需开启系统通知。聊天软件自己的独立弹窗可能无法接入。", 11, "#839C8C"); hint.TextWrapping = TextWrapping.Wrap; hint.TextAlignment = TextAlignment.Center; hint.LineHeight = 16; empty.Children.Add(hint);
                var existingEmpty = notificationList.Items.Count == 1 ? notificationList.Items[0] as Border : null;
                desired.Add(existingEmpty != null && (string)existingEmpty.Tag == "empty" ? existingEmpty : new Border { Tag = "empty", Child = empty, CornerRadius = new CornerRadius(15), Background = Display.Brush("#1A2023") });
            }
            foreach (var message in messages)
            {
                // Match feed identity exactly. The preferences key intentionally
                // rejects unmanageable sources; an empty switch key must not merge
                // two otherwise distinct notification cards.
                string source = String.IsNullOrWhiteSpace(message.AppId) ? message.AppName : message.AppId;
                string key = (message.IsDemo ? "demo:" : "os:") + source.Length + ":" + source + ":" + message.Id.Length + ":" + message.Id + ":" + message.CreatedAt.Ticks;
                keys.Add(key);
                NotificationCardEntry entry;
                if (!notificationCards.TryGetValue(key, out entry) || !entry.Matches(message, settings.HideNotificationBody))
                {
                    var card = NotificationCard(message); card.Tag = key;
                    entry = new NotificationCardEntry { Message = message, HideBody = settings.HideNotificationBody, Card = card };
                    notificationCards[key] = entry;
                }
                desired.Add(entry.Card);
            }
            foreach (string stale in notificationCards.Keys.Where(key => !keys.Contains(key)).ToArray()) notificationCards.Remove(stale);
            // Retain unchanged cards and their scroll anchor. A new/changed message
            // must not replace fifty controls or measure every historical body.
            for (int i = 0; i < desired.Count; i++)
            {
                if (i < notificationList.Items.Count && ReferenceEquals(notificationList.Items[i], desired[i])) continue;
                int previous = notificationList.Items.IndexOf(desired[i]);
                if (previous >= 0) notificationList.Items.RemoveAt(previous);
                var current = i < notificationList.Items.Count ? notificationList.Items[i] as Border : null;
                if (current != null && Equals(current.Tag, desired[i].Tag)) notificationList.Items[i] = desired[i];
                else notificationList.Items.Insert(i, desired[i]);
            }
            while (notificationList.Items.Count > desired.Count) notificationList.Items.RemoveAt(notificationList.Items.Count - 1);
            UpdateNotificationBadge();
            UpdateNotificationViewport();
            CommitNotificationCardLayout(before, desired);
        }

        private sealed class NotificationCardEntry
        {
            public IslandNotification Message;
            public bool HideBody;
            public Border Card;
            public bool Matches(IslandNotification value, bool hideBody)
            {
                return HideBody == hideBody && Message.AppId == value.AppId && Message.AppName == value.AppName && Message.Title == value.Title && Message.Body == value.Body && Message.IsRead == value.IsRead && Message.IsDemo == value.IsDemo && Message.RepeatCount == value.RepeatCount && Message.PresentationPriority == value.PresentationPriority;
            }
        }

        private Border NotificationCard(IslandNotification message)
        {
            string accent = NotificationColor(message);
            var stack = new StackPanel(); var heading = new Grid(); heading.ColumnDefinitions.Add(new ColumnDefinition()); heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var source = Text((message.IsRead ? "" : "●  ") + (message.PresentationPriority == 1 ? "★ " : "") + message.AppName + (message.RepeatCount > 1 ? " ×" + message.RepeatCount : "") + (message.IsDemo ? "  ·  " + UiLocalization.Localize("示例") : ""), 11, accent, true, false); source.TextTrimming = TextTrimming.CharacterEllipsis; source.ToolTip = source.Text; source.Margin = new Thickness(0, 0, 6, 0); heading.Children.Add(source);
            var time = Text(message.CreatedAt.ToLocalTime().ToString("HH:mm"), 10, "#7E9587"); Grid.SetColumn(time, 1); heading.Children.Add(time); stack.Children.Add(heading);
            var title = Text(settings.HideNotificationBody ? UiLocalization.Localize("收到一条新通知") : message.Title, 13, "#E3EDE7", true, false); title.TextWrapping = TextWrapping.Wrap; title.LineHeight = 18; title.MaxHeight = 36; title.TextTrimming = TextTrimming.CharacterEllipsis; title.ToolTip = title.Text; title.Margin = new Thickness(0, 6, 0, 4); title.Visibility = String.IsNullOrWhiteSpace(title.Text) ? Visibility.Collapsed : Visibility.Visible; stack.Children.Add(title);
            var body = Text(settings.HideNotificationBody ? UiLocalization.Localize("消息内容已隐藏") : message.Body, 11, "#A5B8AC", false, false); body.TextWrapping = TextWrapping.Wrap; body.LineHeight = 16; body.MaxHeight = 48; body.TextTrimming = TextTrimming.CharacterEllipsis; body.ToolTip = body.Text; body.Visibility = String.IsNullOrWhiteSpace(body.Text) ? Visibility.Collapsed : Visibility.Visible; stack.Children.Add(body);
            if (NotificationPreferences.SourceKey(message).Length > 0)
            {
                var block = PanelActionLayout.Caption(Button("屏蔽此应用", () => SetNotificationSourceBlocked(NotificationPreferences.SourceKey(message), true)));
                block.FontSize = 10; block.MinHeight = 28; block.Padding = new Thickness(8, 4, 8, 4); block.Margin = new Thickness(0, 6, 0, 0); block.HorizontalAlignment = HorizontalAlignment.Right;
                block.Foreground = Display.Brush("#95ABA0"); block.ToolTip = "仅在灵动岛内隐藏“" + message.AppName + "”的通知；可在“管理应用”中恢复"; stack.Children.Add(block);
            }
            var result = new Border { Child = stack, CornerRadius = new CornerRadius(16), Background = Display.Brush("#1A2023"), BorderBrush = Display.Brush(message.IsRead ? "#26312C" : "#365343"), BorderThickness = new Thickness(1), Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 6) };
            AttachNotificationTaskMenu(result, message); return result;
        }

        private string NotificationColor(IslandNotification notification)
        {
            switch (NotificationFeed.Classify(notification)) { case NotificationFilter.WeChat: return "#9BE9AD"; case NotificationFilter.QQ: return "#93CCFA"; default: return "#C5B7F8"; }
        }

        private void UpdateNotificationBadge()
        {
            if (compactNotificationIcon == null || miniStatus == null) return;
            int unread = notificationFeed.UnreadCount;
            BeginNotificationCounterUpdate(unread);
            var latest = notificationFeed.Items.FirstOrDefault();
            compactNotificationIcon.Text = unread > 0 ? (unread > 99 ? "99+" : unread.ToString()) : "✦";
            compactNotificationIcon.FontSize = unread > 0 ? 12 : 18;
            compactNotificationIcon.Foreground = Display.Brush(latest != null && unread > 0 ? NotificationColor(latest) : "#A1EACF");
            compactNotificationIcon.ToolTip = UiLocalization.Format(settings.HoverExpandEnabled
                ? "岛内未读 {0} 条 · 点击或悬停查看" : "岛内未读 {0} 条 · 点击查看（悬停已关闭）", unread);
            if (unread > 0) { miniStatus.Text = latest != null && latest.IsDemo ? "示例消息" : "未读消息"; miniStatus.Foreground = Display.Brush("#A1EACF"); }
            if (notificationsTab != null) notificationsTab.Content = "消息" + (unread > 0 ? "  " + unread : "");
            UpdateCompactWidgets();
            EndNotificationCounterUpdate(unread);
        }

        private void ShowPeek(IslandNotification message)
        {
            if (message == null || shuttingDown || dragging || resizing || HasInteractionGuard() || notificationFeed.IsSourceBlocked(message)) return;
            if (peeking && peekPanel != null && peekPanel.Visibility == Visibility.Visible)
            {
                string signature = NotificationPeekSignature(message);
                bool changed = !String.Equals(signature, peekRenderedSignature, StringComparison.Ordinal);
                double alpha = peekTransitionActive && peekContent != null ? peekContent.Opacity : .35;
                double offset = peekTransitionActive && peekTranslation != null ? peekTranslation.Y : 2;
                peekMessage = CopyPeekNotification(message); peekAppId = message.AppId ?? "";
                if (!changed) return;
                // Latest data replaces only the live preview. In particular,
                // never hide it or create another native mode/snapshot cycle.
                RefreshPeekText();
                if (visualTransitionActive)
                {
                    // Keep the parent's geometry clock and union HWND. Its old
                    // pixels cannot contain the newly selected notification.
                    RetainIncomingOpacityForPeekReplacement(); StopNotificationPeekTransition();
                }
                else
                {
                    pendingTransitionSnapshot = null; ClearOutgoingContent();
                    BeginNotificationPeekTransition(alpha, offset);
                }
                collapseTimer.Stop(); hoverTimer.Stop(); peekTimer.Stop(); peekTimer.Start();
                return;
            }
            CaptureTransitionContent(true);
            StopPageTransition(); expandedSessionDefaultHeight = Double.NaN;
            EndPeek(); peeking = true; expanded = false;
            UpdateResizeChrome();
            peekAppId = message.AppId ?? ""; peekMessage = CopyPeekNotification(message); RefreshPeekText();
            compact.Visibility = Visibility.Collapsed; detailsScroll.Visibility = Visibility.Collapsed; expandedHost.Visibility = Visibility.Collapsed; peekPanel.Visibility = Visibility.Visible; shell.CornerRadius = new CornerRadius(24);
            TransitionSize(Math.Min(332, PlacementArea().Width), Math.Min(142, PlacementArea().Height), true);
            collapseTimer.Stop(); hoverTimer.Stop(); peekTimer.Stop(); peekTimer.Start();
            SyncToolkitActivity();
        }

        private void EndPeek()
        {
            peeking = false; peekTimer.Stop(); StopNotificationPeekTransition();
            if (peekPanel != null) peekPanel.Visibility = Visibility.Collapsed;
            ClearNotificationPeekContent();
        }

        private void RefreshPeekText()
        {
            if (peekMessage == null || peekSource == null) return;
            string signature = NotificationPeekSignature(peekMessage);
            if (!String.Equals(signature, peekRenderedSignature, StringComparison.Ordinal)) StopNotificationPeekTransition();
            peekSource.Text = String.IsNullOrWhiteSpace(peekMessage.AppName) ? UiLocalization.Localize("其他") : peekMessage.AppName;
            peekSource.Foreground = Display.Brush(NotificationColor(peekMessage));
            peekKind.Text = UiLocalization.Localize(peekMessage.IsDemo ? "示例" : "新通知");
            peekHint.Text = UiLocalization.Localize("点击查看");
            peekTitle.Text = settings.HideNotificationBody ? UiLocalization.Localize("收到一条新通知") : peekMessage.Title;
            peekBody.Text = settings.HideNotificationBody ? UiLocalization.Localize("消息内容已隐藏") : peekMessage.Body;
            peekTitle.Visibility = String.IsNullOrEmpty(peekTitle.Text) ? Visibility.Collapsed : Visibility.Visible;
            peekBody.Visibility = String.IsNullOrEmpty(peekBody.Text) ? Visibility.Collapsed : Visibility.Visible;
            peekBody.Margin = new Thickness(0, peekTitle.Visibility == Visibility.Visible ? 4 : 6, 0, 0);
            peekSource.ToolTip = peekSource.Text; peekKind.ToolTip = peekKind.Text; peekTitle.ToolTip = peekTitle.Text; peekBody.ToolTip = peekBody.Text;
            peekRenderedSignature = signature;
        }

        private void OpenPeekTarget()
        {
            if (!peeking || peekMessage == null) return;
            string route = peekAppId;
            if (route == "sideisland.timer" || route == "sideisland.reminders" || route == "sideisland.watch" || route == "sideisland.logwatch" || route == "sideisland.ready")
            {
                string tool = route == "sideisland.timer" ? "focus" : route == "sideisland.watch" ? "watch" : route == "sideisland.logwatch" ? "logwatch" : route == "sideisland.ready" ? "ready" : "reminders";
                if (!IsToolVisible(tool)) { settings.HiddenTools = String.Join(",", (settings.HiddenTools ?? "").Split(',').Where(key => key != tool)); SaveSettings(); ConfigureToolkit(); }
                SelectToolsPage(); SelectTool(tool);
            }
            else SelectPage(true);
            RequestExpanded(true);
        }

        private void AddDemoNotifications(bool preview)
        {
            var now = DateTime.UtcNow;
            var items = new[] {
                new IslandNotification { Id = "demo-wechat", AppId = "demo.wechat", AppName = "微信", Title = "项目讨论", Body = "新的设计稿已经准备好，方便时一起看一下。", CreatedAt = now, IsDemo = true },
                new IslandNotification { Id = "demo-qq", AppId = "demo.qq", AppName = "QQ", Title = "开发交流群", Body = "今晚的版本已更新，欢迎查看更新说明。", CreatedAt = now.AddMinutes(-2), IsDemo = true },
                new IslandNotification { Id = "demo-calendar", AppId = "demo.calendar", AppName = "日历", Title = "15 分钟后开始", Body = "产品讨论 · 会议提醒", CreatedAt = now.AddMinutes(-4), IsDemo = true }
            };
            notificationPreferences.Observe(items);
            notificationFeed.Merge(items, false, false); UpdateNotificationListCore(false);
            if (preview) { pinned = false; pinButton.Content = "◇"; ShowPeek(items[0]); }
        }
    }
}
