using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace CodexIsland
{
    internal sealed class TaskWatchPanel : StackPanel, IDisposable
    {
        private readonly TaskWatchBook book = new TaskWatchBook();
        private readonly DispatcherTimer pulse = new DispatcherTimer();
        private readonly TextBox file, pid;
        private readonly ComboBox processes;
        private readonly TextBlock status;
        private readonly StackPanel list = new StackPanel();
        private readonly bool demo;
        private bool disposed, polling, changing, listing;
        private string lastRows;
        private readonly WatchActivityPublisher activities = new WatchActivityPublisher();
        public event Action<string, string> OnNotify;
        public event Action<TaskActivityUpdate> ActivityChanged;
        public TaskWatchPanel(bool demoMode)
        {
            demo = demoMode;
            Children.Add(ToolStyle.Heading("任务守望"));
            Children.Add(ToolStyle.Hint("等待文件生成，或等待指定进程退出。切换页面和收起后仍继续，每秒检查一次。"));
            var fileCard = new StackPanel(); fileCard.Children.Add(ToolStyle.Hint("目标文件完整路径（可以尚不存在）"));
            file = ToolStyle.Input(); file.MaxLength = 32700; file.ToolTip = "例如 D:\\项目\\output.zip"; fileCard.Children.Add(file);
            var browse = IslandWindow.Button("选择目标路径", BrowseFile);
            var addFile = IslandWindow.Button("守望文件出现", () => { string target = file.Text; Change(() => book.AddFile(target)); });
            var fileActions = PanelActionLayout.Create(2, browse, addFile); fileActions.Margin = new Thickness(0, 6, 0, 0); fileCard.Children.Add(fileActions); Children.Add(ToolStyle.Card(fileCard));
            var processCard = new StackPanel(); processCard.Children.Add(ToolStyle.Hint("选择运行进程，或直接输入 PID"));
            processes = UiLocalization.Protect(new ComboBox { FontSize = 12, MaxDropDownHeight = 220, Margin = new Thickness(0, 0, 0, 6) }); processCard.Children.Add(processes);
            processes.SelectionChanged += (s, e) => { var selected = processes.SelectedItem as WatchedProcess; if (selected != null) pid.Text = selected.Pid.ToString(CultureInfo.InvariantCulture); };
            var refresh = IslandWindow.Button("刷新运行进程列表", LoadProcesses);
            pid = ToolStyle.Input(); pid.MaxLength = 10; pid.ToolTip = "进程 PID，例如 12345"; pid.Margin = new Thickness(0, 0, 0, 6); processCard.Children.Add(pid);
            var addProcess = IslandWindow.Button("守望进程退出", () => {
                int value; if (!Int32.TryParse(pid.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value) || value <= 0) { status.Text = "请输入大于 0 的 PID，或选择运行进程。"; return; }
                var selected = processes.SelectedItem as WatchedProcess; long? identity = selected != null && selected.Pid == value ? (long?)selected.StartUtcTicks : null;
                Change(() => book.AddProcess(value, identity));
            }); processCard.Children.Add(PanelActionLayout.Create(2, refresh, addProcess)); Children.Add(ToolStyle.Card(processCard));
            status = ToolStyle.Hint(demo ? "演示模式 · 可添加守望，不弹出外部通知。" : "只在应用运行时监测；不持久化任务，退出应用即取消。受保护进程无法加入。"); Children.Add(status); Children.Add(list);
            Children.Add(ToolStyle.Hint("文件出现即触发一次，不保证文件已写完。启动时已存在的文件或已退出的进程不会补发提醒。已完成项可移除后重新添加。"));
            pulse.Interval = TimeSpan.FromSeconds(1); pulse.Tick += (s, e) => Poll(); Rebuild();
        }
        private void BrowseFile()
        {
            var dialog = new SaveFileDialog { Title = UiLocalization.Localize("指定要等待生成的文件（不会创建文件）"), CheckFileExists = false, OverwritePrompt = false, AddExtension = false, Filter = UiLocalization.FileFilter("所有文件|*.*") };
            if (dialog.ShowDialog(Window.GetWindow(this)) == true) file.Text = dialog.FileName;
        }
        private async void LoadProcesses()
        {
            if (disposed || listing) return; listing = true; status.Text = "正在读取运行进程…";
            try
            {
                var entries = await Task.Run(() => TaskWatchBook.ListProcesses());
                if (disposed) return;
                processes.ItemsSource = entries; status.Text = "已列出 " + entries.Count + " 个可读取身份的进程；受保护进程已略过。";
            }
            catch (Exception error) { if (!disposed) status.Text = "读取进程失败：" + error.Message; }
            finally { listing = false; }
        }
        private async void Change(Func<string> change)
        {
            if (disposed || changing) return;
            // Read WPF inputs on the UI thread before starting background work.
            // Callers capture scalar values; the file path is captured explicitly below.
            changing = true;
            try
            {
                string message = await Task.Run(change);
                if (!disposed) { status.Text = message; Rebuild(); }
            }
            catch (Exception error) { if (!disposed) status.Text = "无法启动守望：" + error.Message; }
            finally { changing = false; }
        }
        private async void Poll()
        {
            if (disposed || polling || book.Items.All(x => x.Completed)) return; polling = true;
            try
            {
                var notices = await Task.Run(() => book.Poll());
                if (disposed) return;
                var retained = book.Items; notices = notices.Where(x => retained.Any(item => item.Id == x.Id && item.Completed)).ToList();
                Rebuild();
                if (notices.Count > 0)
                {
                    status.Text = notices.Count + " 项守望已完成。";
                    var notify = OnNotify;
                    if (!demo && notify != null) notify(notices.Count == 1 ? notices[0].Title : notices.Count + " 项守望已完成", String.Join("\n", notices.Take(3).Select(x => x.Body)) + (notices.Count > 3 ? "\n其余请在任务守望查看。" : ""));
                }
            }
            catch (Exception error) { if (!disposed) status.Text = "检查守望失败：" + error.Message; }
            finally { polling = false; }
        }
        private void Rebuild()
        {
            var items = book.Items;
            foreach (var item in items) PublishActivity(item, item.Completed ? "completed" : "running");
            string rows = String.Join("\n", items.Select(x => x.Id + x.Status));
            if (rows == lastRows) return; lastRows = rows; list.Children.Clear();
            if (items.Count == 0) { list.Children.Add(ToolStyle.Hint("还没有守望任务。")); return; }
            foreach (var item in items)
            {
                string id = item.Id;
                var card = new StackPanel(); var target = IslandWindow.Text(item.Kind == "file" ? item.Target : item.Target + " · PID " + item.Pid, 12, "#E5F2E9", true, false); target.TextWrapping = TextWrapping.Wrap; card.Children.Add(target); card.Children.Add(ToolStyle.Hint(item.Status));
                card.Children.Add(PanelActionLayout.Create(1, IslandWindow.Button(item.Completed ? "移除已完成" : "取消守望", () => RemoveWatch(id)))); list.Children.Add(ToolStyle.Card(card));
            }
        }
        private void RemoveWatch(string id)
        {
            var removed = book.RemoveForActivity(id);
            if (removed != null) PublishActivity(removed, removed.Completed ? "completed" : "cancelled");
            activities.Forget("watch:" + id); Rebuild();
        }
        private void PublishActivity(TaskWatchItem item, string state)
        {
            string title = item.Kind == "file" ? Path.GetFileName(item.Target) : item.Target + " · PID " + item.Pid;
            activities.Publish("watch:" + item.Id, title, state, "任务守望", state == "completed" ? item.Kind == "file" ? "等待的文件已出现" : "守望的进程已退出" : state == "cancelled" ? "已取消" : item.Kind == "file" ? "等待文件出现" : "等待进程退出", ActivityChanged);
        }
        public void Start() { if (!disposed) pulse.Start(); }
        public void Dispose() { if (disposed) return; disposed = true; pulse.Stop(); foreach (var item in book.Items) { var removed = book.RemoveForActivity(item.Id); if (removed != null) PublishActivity(removed, removed.Completed ? "completed" : "cancelled"); } book.Clear(); activities.Clear(); }
    }

    // The task hub reflects lifecycle transitions, not each one-second probe.
    // Titles are metadata; the original watched path/request is left untouched.
    internal sealed class WatchActivityPublisher
    {
        private readonly Dictionary<string, string> states = new Dictionary<string, string>(StringComparer.Ordinal);
        internal void Publish(string id, string title, string state, string source, string message, Action<TaskActivityUpdate> sink)
        {
            if (sink == null) return;
            string previous; if (states.TryGetValue(id, out previous) && previous == state) return;
            var update = new TaskActivityUpdate { Id = id, Title = Title(title), State = state, Source = source, Message = message, Progress = null };
            try { sink(update); states[id] = state; } catch { }
        }
        internal void Forget(string id) { states.Remove(id); }
        internal void Clear() { states.Clear(); }
        internal static string Title(string value)
        {
            var clean = new System.Text.StringBuilder(); value = value ?? "";
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index]; if (Char.IsControl(character)) continue;
                if (Char.IsHighSurrogate(character)) { if (index + 1 < value.Length && Char.IsLowSurrogate(value[index + 1])) { clean.Append(character); clean.Append(value[++index]); } }
                else if (!Char.IsLowSurrogate(character)) clean.Append(character);
            }
            string text = clean.ToString().Trim(); if (text.Length == 0) return "后台任务";
            if (text.Length <= 160) return text;
            int length = 159; if (Char.IsHighSurrogate(text[length - 1])) length--;
            return text.Substring(0, length) + "…";
        }
    }
}
