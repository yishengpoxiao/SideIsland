using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace CodexIsland
{
    internal sealed class TaskActivityPreferences { public bool CompactEnabled; }
    internal sealed class TaskActivityPanel : StackPanel, IDisposable
    {
        private readonly TaskActivityHub hub;
        private readonly TaskActivityApi api;
        private readonly TextBox port, search;
        private readonly TextBlock status, address, count;
        private readonly StackPanel list = new StackPanel();
        private readonly Dictionary<string, Row> rows = new Dictionary<string, Row>();
        private readonly DispatcherTimer timer = new DispatcherTimer();
        private readonly CheckBox compact;
        private readonly LocalDataFile<TaskActivityPreferences> preferencesFile;
        private readonly TaskActivityPreferences preferences;
        private bool active, disposed, refreshQueued;
        private readonly bool demo;
        private bool privacy;
        public event Action CompactPreferenceChanged;
        public bool CompactEnabled { get { return compact.IsChecked == true; } }
        private sealed class Row { internal Border Card; internal TextBlock Title, Detail, Elapsed, State; internal ProgressBar Progress; }
        public TaskActivityPanel(bool demo, TaskActivityHub model)
        {
            this.demo = demo; hub = model; api = new TaskActivityApi(hub);
            preferencesFile = new LocalDataFile<TaskActivityPreferences>("task-activity-preferences.json", demo);
            preferences = preferencesFile.Load(new TaskActivityPreferences());
            Children.Add(ToolStyle.Heading("任务都在这里继续"));
            Children.Add(ToolStyle.Hint("统一查看文件处理、传输与脚本任务。进度未知时显示活动状态，不估算百分比。"));
            compact = new CheckBox { Content = "收起时显示当前任务", IsChecked = preferences.CompactEnabled, Margin = new Thickness(0, 4, 0, 8), FontSize = 12 };
            compact.Checked += (s, e) => Preference(); compact.Unchecked += (s, e) => Preference(); Children.Add(compact);
            search = ToolStyle.Input(); search.MaxLength = 160; UiLocalization.ToolTip(search, "搜索任务标题、来源或说明"); search.TextChanged += (s, e) => Refresh(); Children.Add(search);
            count = ToolStyle.Hint(""); Children.Add(count);
            var viewport = new ScrollViewer { Content = list, MaxHeight = 340, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            Children.Add(viewport);
            Children.Add(PanelActionLayout.Create(1, IslandWindow.Button("清除已结束记录", () => hub.ClearFinished())));
            var connection = new StackPanel(); connection.Children.Add(ToolStyle.Heading("脚本本地接入"));
            connection.Children.Add(ToolStyle.Hint("手动开启后仅监听本机 127.0.0.1，需要访问令牌。停止后旧令牌失效，不运行命令或访问文件。"));
            connection.Children.Add(ToolStyle.Hint("端口（0 为自动选择）")); port = ToolStyle.Input("0"); port.MaxLength = 5; connection.Children.Add(port);
            connection.Children.Add(PanelActionLayout.Create(2, IslandWindow.Button("开启本地接入", StartApi), IslandWindow.Button("停止接入", StopApi)));
            address = UiLocalization.Protect(ToolStyle.Hint("")); address.TextWrapping = TextWrapping.Wrap; connection.Children.Add(address);
            connection.Children.Add(PanelActionLayout.Create(2, IslandWindow.Button("复制访问令牌", () => Copy(api.Running ? api.Token : null)), IslandWindow.Button("复制请求示例", CopyExample)));
            Children.Add(ToolStyle.Card(connection));
            status = ToolStyle.Hint("本地接入未开启。任务记录仅保留本次运行，最多 80 项。移除记录不会停止外部任务。"); Children.Add(status);
            hub.Changed += QueueRefresh;
            timer.Interval = TimeSpan.FromSeconds(1); timer.Tick += (s, e) => UpdateElapsed();
            Refresh();
        }
        private void Preference()
        {
            preferences.CompactEnabled = CompactEnabled;
            try { preferencesFile.Save(preferences); } catch (Exception) { if (status != null) status.Text = "任务显示偏好未能保存，请检查数据目录。"; }
            var handler = CompactPreferenceChanged; if (handler != null) handler();
        }
        internal void SetCompactEnabled(bool value) { compact.IsChecked = value; }
        public void SetActive(bool value) { active = value && !disposed; if (active) { Refresh(); timer.Start(); } else timer.Stop(); }
        internal void SetPrivacy(bool value) { if (privacy != value) { privacy = value; search.Clear(); search.IsEnabled = !privacy; Refresh(); } }
        internal void RefreshAppearance() { Refresh(); }
        private void StartApi()
        {
            if (demo) { status.Text = "演示模式不启动本地接入。"; return; }
            int value; if (!Int32.TryParse(port.Text, NumberStyles.None, CultureInfo.InvariantCulture, out value) || value < 0 || value > 65535) { status.Text = "端口应为 0—65535。"; return; }
            try { api.Start(value); address.Text = api.Url; status.Text = "本地任务接入已开启，仅本机可访问。令牌仅保留在内存中。"; }
            catch (Exception error) { status.Text = UiLocalization.Localize("无法开启本地接入：") + error.Message; }
        }
        private void StopApi() { api.Stop(); address.Text = ""; status.Text = "本地接入已停止，旧令牌已失效。"; }
        private void Copy(string value)
        {
            if (String.IsNullOrEmpty(value)) { status.Text = "请先开启本地接入。"; return; }
            if (demo) { status.Text = "演示模式不修改剪贴板。"; return; }
            try { Clipboard.SetText(value); status.Text = "已复制。"; } catch (Exception) { status.Text = "剪贴板暂时不可用，请重试。"; }
        }
        private void CopyExample()
        {
            Copy(api.Running ? "Invoke-RestMethod -Method Post -Uri '" + api.Url + "' -Headers @{Authorization='Bearer " + api.Token + "'} -ContentType 'application/json; charset=utf-8' -Body '{\"id\":\"build\",\"title\":\"Build\",\"state\":\"running\",\"progress\":25}'" : null);
        }
        private void QueueRefresh()
        {
            if (disposed || Dispatcher.HasShutdownStarted) return;
            lock (rows) { if (refreshQueued) return; refreshQueued = true; }
            try { Dispatcher.BeginInvoke(new Action(() => { lock (rows) refreshQueued = false; if (!disposed && active) Refresh(); }), DispatcherPriority.Background); }
            catch (InvalidOperationException) { lock (rows) refreshQueued = false; }
        }
        private void Refresh()
        {
            if (disposed) return;
            string query = search == null ? "" : search.Text.Trim();
            var all = hub.Snapshot(); var items = all.Where(x => query.Length == 0 || (x.Title + " " + x.Source + " " + x.Message + " " + TaskActivityHub.DisplayTitle(x) + " " + TaskActivityHub.DisplaySource(x) + " " + TaskActivityHub.DisplayMessage(x)).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            foreach (var id in rows.Keys.Where(id => !items.Any(x => x.Id == id)).ToArray()) { list.Children.Remove(rows[id].Card); rows.Remove(id); }
            for (int index = 0; index < items.Count; index++)
            {
                var item = items[index]; Row row;
                if (!rows.TryGetValue(item.Id, out row))
                {
                    row = new Row(); var content = new StackPanel();
                    row.Title = UiLocalization.Protect(IslandWindow.Text("", 12, "#D7E8DE", true)); row.Title.TextWrapping = TextWrapping.Wrap; content.Children.Add(row.Title);
                    var line = new Grid(); line.ColumnDefinitions.Add(new ColumnDefinition()); line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.State = ToolStyle.Hint(""); line.Children.Add(row.State); row.Elapsed = UiLocalization.Protect(ToolStyle.Hint("")); Grid.SetColumn(row.Elapsed, 1); line.Children.Add(row.Elapsed); content.Children.Add(line);
                    row.Progress = new ProgressBar { Minimum = 0, Maximum = 100, Height = 4, Margin = new Thickness(0, 3, 0, 6) }; content.Children.Add(row.Progress);
                    row.Detail = UiLocalization.Protect(ToolStyle.Hint("")); row.Detail.TextWrapping = TextWrapping.Wrap; row.Detail.MaxHeight = 64; row.Detail.TextTrimming = TextTrimming.CharacterEllipsis; content.Children.Add(row.Detail);
                    string id = item.Id; content.Children.Add(PanelActionLayout.Create(1, IslandWindow.Button("移除记录", () => hub.Remove(id))));
                    row.Card = ToolStyle.Card(content); rows.Add(id, row); list.Children.Add(row.Card);
                }
                int old = list.Children.IndexOf(row.Card); if (old != index) { list.Children.Remove(row.Card); list.Children.Insert(index, row.Card); }
                row.Title.Text = privacy ? UiLocalization.Localize("后台任务内容已隐藏") : TaskActivityHub.DisplayTitle(item);
                row.State.Text = UiLocalization.Localize(TaskActivityHub.StateLabel(item.State)) + (item.Progress.HasValue ? " · " + item.Progress.Value.ToString("0.#", CultureInfo.InvariantCulture) + "%" : "");
                row.Progress.IsIndeterminate = item.Active && !item.Progress.HasValue; row.Progress.Value = item.Progress ?? 0;
                row.Progress.Visibility = item.Active || item.Progress.HasValue ? Visibility.Visible : Visibility.Collapsed;
                row.Detail.Text = privacy ? "" : TaskActivityHub.DisplaySource(item) + (String.IsNullOrWhiteSpace(item.Message) ? "" : " · " + TaskActivityHub.DisplayMessage(item)); row.Detail.ToolTip = row.Detail.Text; row.Detail.Visibility = privacy ? Visibility.Collapsed : Visibility.Visible;
                row.Elapsed.Text = TaskActivityHub.Elapsed(item, DateTime.UtcNow);
            }
            if (count != null) count.Text = UiLocalization.Format("活动 {0} 项 · 共 {1} 项", all.Count(x => x.Active), all.Count);
        }
        private void UpdateElapsed() { foreach (var item in hub.Snapshot()) { Row row; if (rows.TryGetValue(item.Id, out row)) row.Elapsed.Text = TaskActivityHub.Elapsed(item, DateTime.UtcNow); } }
        public void Dispose() { if (disposed) return; disposed = true; timer.Stop(); hub.Changed -= QueueRefresh; api.Dispose(); }
    }
}
