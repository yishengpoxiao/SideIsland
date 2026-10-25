using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace CodexIsland
{
    internal partial class IslandWindow
    {
        private AudioMixerPanel audioMixerPanel;
        private LanTransferPanel lanTransferPanel;
        private BatchFilesPanel batchFilesPanel;
        private TaskActivityPanel taskActivityPanel;
        private TaskActivityHub taskActivities;
        private bool taskActivityRefreshQueued;
        private bool TaskActivityOverridesCompact
        {
            get
            {
                var item = taskActivities == null ? null : taskActivities.Current;
                if (taskActivityPanel == null || !taskActivityPanel.CompactEnabled || item == null || (!item.Active && (DateTime.UtcNow - item.UpdatedUtc).TotalSeconds > 8)) return false;
                var layout = ActiveWidgetLayout;
                return layout == null || (layout.ActivityOverride && CurrentWidgetTransientPriority < layout.ActivityPriority);
            }
        }
        private void BuildNextFeatures(Panel navigation)
        {
            taskActivities = new TaskActivityHub(); taskActivityPanel = new TaskActivityPanel(demo, taskActivities);
            audioMixerPanel = new AudioMixerPanel(demo); lanTransferPanel = new LanTransferPanel(demo); batchFilesPanel = new BatchFilesPanel(demo);
            AddTool(navigation, "audio-mixer", audioMixerPanel); AddTool(navigation, "lan-transfer", lanTransferPanel);
            AddTool(navigation, "batch", batchFilesPanel); AddTool(navigation, "activities", taskActivityPanel);
            batchFilesPanel.ActivityChanged += PublishTaskActivity;
            lanTransferPanel.ActivityChanged += PublishTaskActivity;
            taskWatchPanel.ActivityChanged += PublishTaskActivity;
            serviceReadyPanel.ActivityChanged += PublishTaskActivity;
            taskActivities.Changed += QueueTaskActivityCompact;
            taskActivityPanel.CompactPreferenceChanged += () => UpdateToolkitCompact();
            notesPanel.NotificationTasks.OnNotify += (title, body) => NotifyBackgroundTool("notes", title, body);
        }
        private void StartNextFeatures() { if (notesPanel != null) notesPanel.NotificationTasks.Start(); }
        private void SyncNextFeatureActivity(bool active)
        {
            if (audioMixerPanel != null) audioMixerPanel.SetActive(active && selectedTool == "audio-mixer" && IsToolVisible("audio-mixer"));
            if (lanTransferPanel != null) lanTransferPanel.SetActive(active && selectedTool == "lan-transfer" && IsToolVisible("lan-transfer"));
            if (taskActivityPanel != null) taskActivityPanel.SetActive(active && selectedTool == "activities" && IsToolVisible("activities"));
            if (taskActivityPanel != null) taskActivityPanel.SetPrivacy(settings.HideNotificationBody);
            if (notesPanel != null) notesPanel.NotificationTasks.SetPrivacy(settings.HideNotificationBody);
        }
        private void StopNextFeatures()
        {
            if (taskActivities != null) taskActivities.Changed -= QueueTaskActivityCompact;
            if (audioMixerPanel != null) audioMixerPanel.Dispose(); if (lanTransferPanel != null) lanTransferPanel.Dispose();
            if (batchFilesPanel != null) batchFilesPanel.Dispose(); if (taskActivityPanel != null) taskActivityPanel.Dispose();
        }
        private void PublishTaskActivity(TaskActivityUpdate update)
        {
            if (shuttingDown || taskActivities == null) return;
            try { taskActivities.Publish(update); } catch (Exception error) { if (!demo) StartupDiagnostics.Failure(error); }
        }
        private void QueueTaskActivityCompact()
        {
            if (shuttingDown || Dispatcher.HasShutdownStarted) return;
            lock (taskActivities) { if (taskActivityRefreshQueued) return; taskActivityRefreshQueued = true; }
            try { Dispatcher.BeginInvoke(new Action(() => { lock (taskActivities) taskActivityRefreshQueued = false; if (!shuttingDown) UpdateToolkitCompact(); }), DispatcherPriority.Background); }
            catch (InvalidOperationException) { lock (taskActivities) taskActivityRefreshQueued = false; }
        }
        private void FillTaskActivityWidgetData(CompactWidgetData data)
        {
            var task = taskActivities == null ? null : taskActivities.Current;
            data.ActivityTitle = task == null ? "" : settings.HideNotificationBody ? UiLocalization.Localize("后台任务") : TaskActivityHub.DisplayTitle(task);
            data.ActivityStatus = task == null ? "" : UiLocalization.Localize(TaskActivityHub.StateLabel(task.State));
            data.ActivityProgress = task == null ? null : task.Progress; data.ActivityRunning = task != null && task.Active;
        }
        private WidgetCardDefinition[] ApplyTaskActivityOverride(WidgetCardDefinition[] cards)
        {
            var current = taskActivities == null ? null : taskActivities.Current;
            if (!TaskActivityOverridesCompact || current == null) return cards;
            if (cards == null)
            {
                string first = focusPanel != null && focusPanel.IsRunning ? "focus" : settings.CompactMode == "clock" ? "clock" : settings.CompactMode == "focus" ? "focus" : settings.CompactMode == "music" ? "media" : settings.CompactSlot1;
                cards = new[] { new WidgetCardDefinition { Key = first }, new WidgetCardDefinition { Key = settings.CompactSlot2 } };
            }
            if (cards.Any(card => card.Key == "activities")) return cards;
            return new[] { cards[0], new WidgetCardDefinition { Key = "activities", Size = cards[1].Size } };
        }
        private void RefreshNextFeatureAppearance()
        {
            RefreshSceneAutomationAppearance();
            if (taskActivityPanel != null) taskActivityPanel.RefreshAppearance();
            if (notesPanel != null) notesPanel.NotificationTasks.Refresh();
        }
        private void AttachNotificationTaskMenu(Border card, IslandNotification message)
        {
            // Keep unopened notification cards free of menu/style dictionaries.
            // Handle the routed request ourselves even on the first opening,
            // when WPF would otherwise find the island's ancestor menu.
            card.ContextMenuOpening += (s, e) =>
            {
                e.Handled = true;
                if (shuttingDown) return;
                var menu = EnsureNotificationTaskMenu(card, message);
                menu.PlacementTarget = card;
                menu.Placement = e.CursorLeft == -1 && e.CursorTop == -1 ? PlacementMode.Center : PlacementMode.MousePoint;
                menu.HorizontalOffset = menu.VerticalOffset = 0;
                menu.IsOpen = true;
            };
            UiLocalization.ToolTip(card, "右键可转为待办、稍后提醒或归档到项目。");
        }
        private ContextMenu EnsureNotificationTaskMenu(Border card, IslandNotification message)
        {
            if (card.ContextMenu != null) return card.ContextMenu;
            var menu = new ContextMenu(); UiTheme.Install(menu.Resources);
            AddNotificationTaskAction(menu, "转为待办", message, 0, false, false);
            AddNotificationTaskAction(menu, "15 分钟后提醒", message, 15, false, false);
            AddNotificationTaskAction(menu, "1 小时后提醒", message, 60, false, false);
            AddNotificationTaskAction(menu, "固定为待办", message, 0, true, false);
            AddNotificationTaskAction(menu, "归档到项目…", message, 0, false, true);
            menu.Closed += (s, e) => QueueInteractionRecheck(); card.ContextMenu = menu;
            return menu;
        }
        private void AddNotificationTaskAction(ContextMenu menu, string caption, IslandNotification message, int minutes, bool pin, bool editProject)
        {
            var action = UiLocalization.Header(new MenuItem(), caption);
            action.Click += (s, e) =>
            {
                menu.IsOpen = false;
                if (notesPanel == null || notificationFeed.IsSourceBlocked(message)) return;
                try
                {
                    var item = notesPanel.NotificationTasks.Add(message, minutes);
                    if (pin) notesPanel.NotificationTasks.Book.SetPinned(item.Id, true);
                    notesPanel.NotificationTasks.Refresh();
                    if (minutes == 0) { OpenCompactTool("notes", true); notesPanel.OpenNotificationTasks(); if (editProject) notesPanel.NotificationTasks.EditProject(item.Id); }
                    if (toolkitHint != null) toolkitHint.Text = UiLocalization.Localize(minutes > 0 ? "已保存待办并设置稍后提醒。" : "已转为通知待办。");
                }
                catch (Exception error) { if (toolkitHint != null) toolkitHint.Text = UiLocalization.Localize("无法保存通知待办：") + error.Message; }
            };
            menu.Items.Add(action);
        }
    }
}
