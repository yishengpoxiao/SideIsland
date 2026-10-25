using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CodexIsland
{
    internal sealed class WidgetLayoutEditorPanel : StackPanel
    {
        private readonly WidgetLayoutStore store;
        private readonly Func<IEnumerable<LayoutProfile>> profiles;
        private readonly Func<Settings> settings;
        private readonly Action changed;
        private readonly ComboBox scope = Combo(), key = Combo(), size = Combo();
        private readonly CheckBox enabled = Check("启用此组件布局"), notify = Check("新通知可临时置顶"), focus = Check("专注完成可临时置顶"), activity = Check("任务状态可临时置顶");
        private readonly TextBox rotation = ToolStyle.Input(), notifyPriority = ToolStyle.Input(), focusPriority = ToolStyle.Input(), activityPriority = ToolStyle.Input();
        private readonly WrapPanel preview = new WrapPanel();
        private readonly TextBlock status = ToolStyle.Hint("");
        private WidgetLayoutDefinition draft;
        private string selectedCard, language = "", theme = "";
        private bool loading;
        private readonly string dragToken = Guid.NewGuid().ToString("N");
        private Point? dragStart;
        internal WidgetLayoutEditorPanel(WidgetLayoutStore source, Func<IEnumerable<LayoutProfile>> availableProfiles, Func<Settings> current, Func<string> activeProfile, Action onChanged)
        {
            store = source; profiles = availableProfiles; settings = current; changed = onChanged;
            Children.Add(ToolStyle.Heading("组件布局编辑器"));
            Children.Add(ToolStyle.Hint("每个场景可保存 2—6 个组件。收起时同时显示两个，其他组件可定时轮播；拖动预览卡片或使用方向按钮排序。"));
            Children.Add(ToolStyle.Hint("编辑的场景布局")); UiLocalization.Protect(scope); Children.Add(scope);
            scope.SelectionChanged += (s, e) => { if (!loading) LoadScope(scope.SelectedValue as string ?? ""); };
            Children.Add(enabled);
            Children.Add(preview);
            var editor = new StackPanel();
            editor.Children.Add(ToolStyle.Hint("选中组件")); editor.Children.Add(key); UiLocalization.Protect(key);
            editor.Children.Add(ToolStyle.Hint("组件尺寸")); size.Items.Add("小"); size.Items.Add("标准"); size.Items.Add("大"); editor.Children.Add(size);
            key.SelectionChanged += (s, e) => { if (!loading && CurrentCard != null) { CurrentCard.Key = key.SelectedValue as string ?? "clock"; RebuildPreview(); } };
            size.SelectionChanged += (s, e) => { if (!loading && CurrentCard != null) { CurrentCard.Size = size.SelectedIndex == 0 ? "small" : size.SelectedIndex == 2 ? "large" : "standard"; RebuildPreview(); } };
            editor.Children.Add(PanelActionLayout.Create(2, IslandWindow.Button("前移组件", () => Move(-1)), IslandWindow.Button("后移组件", () => Move(1))));
            var addRow = PanelActionLayout.Create(2, IslandWindow.Button("添加组件", Add), IslandWindow.Button("移除组件", Remove)); addRow.Margin = new Thickness(0, 6, 0, 0); editor.Children.Add(addRow);
            Children.Add(ToolStyle.Card(editor));
            Children.Add(ToolStyle.Hint("轮播间隔（秒；0 为固定前两个）")); rotation.MaxLength = 2; Children.Add(rotation);
            Children.Add(notify); Children.Add(ToolStyle.Hint("通知显示优先级 0—100")); notifyPriority.MaxLength = 3; Children.Add(notifyPriority);
            Children.Add(focus); Children.Add(ToolStyle.Hint("专注完成显示优先级 0—100")); focusPriority.MaxLength = 3; Children.Add(focusPriority);
            Children.Add(activity); Children.Add(ToolStyle.Hint("任务显示优先级 0—100")); activityPriority.MaxLength = 3; Children.Add(activityPriority);
            Children.Add(ToolStyle.Hint("通知和专注完成显示 8 秒后恢复；任务进行中可持续显示，结束后 8 秒恢复。优先级较高者先显示；任务还需在任务动态中开启收起显示。"));
            Children.Add(ToolStyle.Hint("组件布局启用时优先于整块时钟、专注和自动音乐。"));
            var actions = PanelActionLayout.Create(2, IslandWindow.Button("保存组件布局", Save), IslandWindow.Button("取消未保存修改", () => LoadScope(scope.SelectedValue as string ?? ""))); actions.Margin = new Thickness(0, 6, 0, 0); Children.Add(actions); Children.Add(status);
            RefreshScopes(activeProfile()); RefreshLanguage(); LoadScope(scope.SelectedValue as string ?? "");
            IsVisibleChanged += (s, e) => { if (IsVisible) { RefreshScopes(scope.SelectedValue as string); RefreshLanguage(); } };
        }
        private static ComboBox Combo() { return new ComboBox { MinHeight = 32, FontSize = 12, Margin = new Thickness(0, 0, 0, 6) }; }
        private static CheckBox Check(string caption) { return new CheckBox { Content = IslandWindow.Text(caption, 11, "#DDE9E2"), Margin = new Thickness(0, 5, 0, 5) }; }
        private WidgetCardDefinition CurrentCard { get { return draft == null ? null : draft.Cards.FirstOrDefault(c => c.Id == selectedCard); } }
        private void RefreshScopes(string selected)
        {
            loading = true;
            scope.Items.Clear(); scope.SelectedValuePath = "Tag";
            var global = new ComboBoxItem { Content = "所有场景的默认布局", Tag = "" }; UiLocalization.Mark(global, ContentControl.ContentProperty, "所有场景的默认布局"); scope.Items.Add(global);
            foreach (var profile in profiles())
            {
                var item = new ComboBoxItem { Content = profile.Name, Tag = profile.Id };
                if (LayoutProfileStore.IsBuiltin(profile.Id)) UiLocalization.Mark(item, ContentControl.ContentProperty, profile.Name); else UiLocalization.Protect(item);
                scope.Items.Add(item);
            }
            scope.SelectedValue = selected ?? ""; if (scope.SelectedIndex < 0) scope.SelectedIndex = 0;
            loading = false;
        }
        private void LoadScope(string id)
        {
            var saved = store.Data.Layouts.FirstOrDefault(l => l.ProfileId == id);
            var inherited = store.Data.Layouts.FirstOrDefault(l => l.ProfileId.Length == 0);
            draft = saved == null ? inherited == null ? WidgetLayoutPolicy.Default(id, settings()) : inherited.Copy() : saved.Copy();
            draft.ProfileId = id;
            enabled.IsChecked = draft.Enabled; rotation.Text = draft.RotateSeconds.ToString(CultureInfo.InvariantCulture);
            notify.IsChecked = draft.NotificationOverride; focus.IsChecked = draft.FocusOverride; activity.IsChecked = draft.ActivityOverride; notifyPriority.Text = draft.NotificationPriority.ToString(CultureInfo.InvariantCulture); focusPriority.Text = draft.FocusPriority.ToString(CultureInfo.InvariantCulture); activityPriority.Text = draft.ActivityPriority.ToString(CultureInfo.InvariantCulture);
            selectedCard = draft.Cards[0].Id; SyncSelection(); RebuildPreview(); status.Text = "";
        }
        private void SyncSelection()
        {
            loading = true; var current = CurrentCard;
            key.SelectedValue = current == null ? "clock" : current.Key;
            size.SelectedIndex = current == null || current.Size == "standard" ? 1 : current.Size == "small" ? 0 : 2;
            loading = false;
        }
        internal void RefreshLanguage()
        {
            if (language == UiLocalization.Language && theme == UiTheme.Name) return;
            language = UiLocalization.Language; theme = UiTheme.Name;
            loading = true; string selected = CurrentCard == null ? "clock" : CurrentCard.Key;
            key.Items.Clear(); key.SelectedValuePath = "Tag";
            foreach (string value in CompactWidgetCatalog.Keys) key.Items.Add(new ComboBoxItem { Content = CompactWidgetCatalog.Label(value, UiLocalization.Language), Tag = value });
            key.SelectedValue = selected; loading = false; RebuildPreview();
        }
        private void RebuildPreview()
        {
            preview.Children.Clear(); if (draft == null) return;
            foreach (var card in draft.Cards)
            {
                var local = card; var body = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                var visual = new CompactWidgetVisual(); visual.LayoutTransform = new ScaleTransform(card.Scale, card.Scale);
                visual.Update(CompactWidgetState.Create(card.Key, new CompactWidgetData { Language = UiLocalization.Language, LocalTime = DateTime.Now, PrimaryRemaining = 76, SecondaryRemaining = 54, PrimaryMinutes = 300, SecondaryMinutes = 10080, CpuPercent = 22, MemoryPercent = 48, BatteryPercent = 80, OnAc = false, FocusStatus = "25:00", FocusLabel = "专注", AwakeStatus = "30:00", AwakeRunning = true, UnreadCount = 3, PortsStatus = "2" }), "auto", UiTheme.Name == "light");
                body.Children.Add(visual);
                var label = IslandWindow.Text(CompactWidgetCatalog.Label(card.Key, UiLocalization.Language), 10, "#8FA89A", false, false); label.TextWrapping = TextWrapping.Wrap; label.TextAlignment = TextAlignment.Center; label.MaxWidth = 98; body.Children.Add(label);
                var border = new Border { Child = body, Padding = new Thickness(8), Margin = new Thickness(0, 0, 6, 6), MinWidth = 84, Background = Display.Brush(card.Id == selectedCard ? "#365649" : "#202C27"), CornerRadius = new CornerRadius(12), AllowDrop = true, Cursor = Cursors.Hand };
                border.MouseLeftButtonDown += (s, e) => { selectedCard = local.Id; dragStart = e.GetPosition(this); SyncSelection(); foreach (Border sibling in preview.Children) sibling.Background = Display.Brush(ReferenceEquals(sibling, border) ? "#365649" : "#202C27"); };
                border.MouseMove += (s, e) => { if (e.LeftButton != MouseButtonState.Pressed || !dragStart.HasValue) return; Point now = e.GetPosition(this); if ((now - dragStart.Value).Length < 6) return; dragStart = null; DragDrop.DoDragDrop(border, new DataObject("SideIsland.WidgetCard", dragToken + ":" + local.Id), DragDropEffects.Move); };
                border.DragOver += (s, e) => { e.Effects = e.Data.GetDataPresent("SideIsland.WidgetCard") ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
                border.Drop += (s, e) => { string token = e.Data.GetData("SideIsland.WidgetCard") as string; if (token == null || !token.StartsWith(dragToken + ":", StringComparison.Ordinal)) return; string id = token.Substring(dragToken.Length + 1); Reorder(id, local.Id); e.Handled = true; };
                preview.Children.Add(border);
            }
        }
        internal void Reorder(string sourceId, string targetId)
        {
            var source = draft.Cards.FirstOrDefault(c => c.Id == sourceId); int target = draft.Cards.FindIndex(c => c.Id == targetId);
            if (source == null || target < 0 || sourceId == targetId) return;
            draft.Cards.Remove(source); draft.Cards.Insert(Math.Min(target, draft.Cards.Count), source); selectedCard = source.Id; RebuildPreview(); status.Text = "组件顺序已调整，保存后生效。";
        }
        private void Move(int delta)
        {
            int index = draft.Cards.FindIndex(c => c.Id == selectedCard), target = index + delta;
            if (index < 0 || target < 0 || target >= draft.Cards.Count) return;
            var value = draft.Cards[index]; draft.Cards.RemoveAt(index); draft.Cards.Insert(target, value); RebuildPreview(); status.Text = "组件顺序已调整，保存后生效。";
        }
        private void Add()
        {
            if (draft.Cards.Count >= 6) { status.Text = "组件池最多包含 6 个组件。"; return; }
            var value = new WidgetCardDefinition { Key = key.SelectedValue as string ?? "clock" }; draft.Cards.Add(value); selectedCard = value.Id; SyncSelection(); RebuildPreview();
        }
        private void Remove()
        {
            if (draft.Cards.Count <= 2) { status.Text = "请至少保留两个组件。"; return; }
            draft.Cards.RemoveAll(c => c.Id == selectedCard); selectedCard = draft.Cards[0].Id; SyncSelection(); RebuildPreview();
        }
        private void Save()
        {
            int seconds, notificationOrder, focusOrder, activityOrder;
            if (!Int32.TryParse(rotation.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds) || !Int32.TryParse(notifyPriority.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out notificationOrder) || !Int32.TryParse(focusPriority.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out focusOrder) || !Int32.TryParse(activityPriority.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out activityOrder)) { status.Text = "轮播间隔和优先级必须是整数。"; return; }
            var saved = draft.Copy(); saved.Enabled = enabled.IsChecked == true; saved.RotateSeconds = seconds; saved.NotificationOverride = notify.IsChecked == true; saved.FocusOverride = focus.IsChecked == true; saved.ActivityOverride = activity.IsChecked == true; saved.NotificationPriority = notificationOrder; saved.FocusPriority = focusOrder; saved.ActivityPriority = activityOrder;
            if (!WidgetLayoutPolicy.Valid(saved)) { status.Text = "轮播为 0 或 5—60 秒，优先级为 0—100；请检查组件设置。"; return; }
            try { store.Save(saved); draft = saved.Copy(); changed(); status.Text = "组件布局已保存。"; }
            catch (Exception error) { if (!(error is System.IO.IOException || error is UnauthorizedAccessException || error is ArgumentException)) throw; status.Text = "组件布局未能保存，编辑内容仍保留。"; }
        }
    }
}
