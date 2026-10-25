using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace CodexIsland
{
    internal sealed class ServiceReadyPanel : StackPanel, IDisposable
    {
        private readonly bool demo;
        private readonly ServiceReadyMonitor monitor;
        private readonly DispatcherTimer pulse = new DispatcherTimer();
        private readonly ComboBox kind;
        private readonly TextBox name = ToolStyle.Input(), target = ToolStyle.Input("localhost"), port = ToolStyle.Input("3000"), stable = ToolStyle.Input("5");
        private readonly StackPanel rows = new StackPanel();
        private readonly TextBlock status = ToolStyle.Hint("");
        private readonly FrameworkElement portLabel, stableLabel;
        private bool disposed, started, renderQueued;
        private readonly WatchActivityPublisher activities = new WatchActivityPublisher();
        internal event Action<string, string> OnNotify;
        public event Action<TaskActivityUpdate> ActivityChanged;
        public ServiceReadyPanel(bool demoMode) : this(demoMode, new ServiceReadyMonitor(demoMode)) { }
        internal ServiceReadyPanel(bool demoMode, ServiceReadyMonitor source)
        {
            demo = demoMode; monitor = source;
            Children.Add(ToolStyle.Heading("等待服务或文件就绪"));
            Children.Add(ToolStyle.Hint("手动开始后监测 TCP 连接、HTTP／HTTPS 的 2xx 状态，或文件大小和修改时间持续稳定。每项只提醒一次，可取消或移除；退出后停止，不自动重启监测。"));
            Children.Add(ToolStyle.Hint("提醒名称（可选）")); name.MaxLength = 60; Children.Add(name);
            kind = UiLocalization.Combo(new ComboBox { ItemsSource = new[] { "TCP 主机与端口", "HTTP／HTTPS 地址", "文件持续稳定" }, SelectedIndex = 0, Padding = new Thickness(7), Margin = new Thickness(0, 6, 0, 7) }); Children.Add(kind);
            Children.Add(ToolStyle.Hint("主机、完整网址或文件绝对路径")); target.MaxLength = 2048; Children.Add(target);
            portLabel = ToolStyle.Hint("TCP 端口（1—65535）"); Children.Add(portLabel); port.MaxLength = 5; Children.Add(port);
            stableLabel = ToolStyle.Hint("文件稳定时长（1—3600 秒）"); Children.Add(stableLabel); stable.MaxLength = 4; Children.Add(stable);
            kind.SelectionChanged += (s, e) => UpdateInputs(); UpdateInputs();
            var add = IslandWindow.Button("开始就绪提醒", Add);
            var choose = IslandWindow.Button("选择目标文件", () => { if (demo) { status.Text = "演示模式不连接网络或读取文件。"; return; } var picker = new Microsoft.Win32.OpenFileDialog { CheckFileExists = false, Title = UiLocalization.Localize("选择要等待稳定的文件") }; if (picker.ShowDialog(Window.GetWindow(this)) == true) { kind.SelectedIndex = 2; target.Text = picker.FileName; } });
            var actions = PanelActionLayout.Create(2, add, choose); actions.Margin = new Thickness(0, 6, 0, 0); Children.Add(actions);
            Children.Add(ToolStyle.Hint("HTTP 只检查响应状态，不读取正文、不跟随重定向，也不发送 Windows 凭据。单次检查约 2.5 秒超时；TCP 可连接只代表端口开始接受连接。"));
            Children.Add(status); Children.Add(rows);
            monitor.Changed += QueueRender; monitor.Ready += OnReady;
            pulse.Interval = TimeSpan.FromSeconds(1); pulse.Tick += async (s, e) => { if (disposed) return; try { await monitor.PollAsync(); } catch { if (!disposed) status.Text = "暂时无法检查，稍后重试。"; } };
            Rebuild();
        }
        private void UpdateInputs()
        {
            bool tcp = kind.SelectedIndex == 0, file = kind.SelectedIndex == 2;
            port.Visibility = portLabel.Visibility = tcp ? Visibility.Visible : Visibility.Collapsed;
            stable.Visibility = stableLabel.Visibility = file ? Visibility.Visible : Visibility.Collapsed;
        }
        private async void Add()
        {
            if (disposed) return;
            if (demo) { status.Text = "演示模式不连接网络或读取文件。"; return; }
            int tcpPort = 3000, stableSeconds = 5;
            if (kind.SelectedIndex == 0 && !Int32.TryParse(port.Text, NumberStyles.None, CultureInfo.InvariantCulture, out tcpPort)) { status.Text = "请输入有效主机名称或 IP，以及 1 至 65535 的端口。"; return; }
            if (kind.SelectedIndex == 2 && !Int32.TryParse(stable.Text, NumberStyles.None, CultureInfo.InvariantCulture, out stableSeconds)) { status.Text = "文件稳定时长应为 1 至 3600 秒。"; return; }
            try
            {
                monitor.Add(new ServiceReadyRequest { Name = name.Text, Kind = new[] { "tcp", "http", "file" }[Math.Max(0, kind.SelectedIndex)], Target = target.Text, Port = tcpPort, StableSeconds = stableSeconds });
                status.Text = "已开始等待就绪，每项完成后仅提醒一次。"; if (started) await monitor.PollAsync();
            }
            catch (ArgumentException error) { status.Text = error.Message; }
            catch { if (!disposed) status.Text = "暂时无法检查，稍后重试。"; }
        }
        private void QueueRender()
        {
            if (disposed || Dispatcher.HasShutdownStarted) return;
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(QueueRender)); return; }
            if (renderQueued) return; renderQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { renderQueued = false; if (!disposed) Rebuild(); }));
        }
        private void Rebuild()
        {
            rows.Children.Clear(); var values = monitor.Snapshot;
            foreach (var value in values) PublishActivity(value, ActivityState(value));
            foreach (var value in values)
            {
                var watch = value; var body = new StackPanel(); body.Children.Add(IslandWindow.Text(watch.Request.Name, 13, "#E3EFE7", true, false));
                string destination = watch.Request.Target + (watch.Request.Kind == "tcp" ? ":" + watch.Request.Port : ""); body.Children.Add(ToolStyle.Hint(destination, false)); body.Children.Add(ToolStyle.Hint(watch.Status));
                var remove = IslandWindow.Button("移除", () => RemoveWatch(watch.Id));
                body.Children.Add(watch.Running ? PanelActionLayout.Create(2, IslandWindow.Button("取消监测", () => monitor.Cancel(watch.Id)), remove) : PanelActionLayout.Create(1, remove)); rows.Children.Add(ToolStyle.Card(body));
            }
            if (values.Count == 0) rows.Children.Add(ToolStyle.Hint("尚未开始就绪提醒。填写目标后手动开始。"));
        }
        private static string ActivityState(ServiceReadyWatch watch) { return watch.State == "ready" ? "completed" : watch.State == "canceled" ? "cancelled" : "running"; }
        private void PublishActivity(ServiceReadyWatch watch, string state)
        {
            activities.Publish("ready:" + watch.Id, watch.Request.Name, state, "服务就绪", state == "completed" ? "服务或文件已就绪" : state == "cancelled" ? "已取消" : "等待检查", ActivityChanged);
        }
        private void RemoveWatch(string id)
        {
            var removed = monitor.RemoveForActivity(id);
            if (removed != null) PublishActivity(removed, removed.Running ? "cancelled" : ActivityState(removed));
            activities.Forget("ready:" + id); Rebuild();
        }
        private void OnReady(ServiceReadyWatch watch)
        {
            if (disposed || demo || Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke(new Action(() => { if (disposed || demo) return; var notify = OnNotify; if (notify != null) notify(UiLocalization.Localize("服务或文件已就绪"), watch.Request.Name); }));
        }
        public void Start() { if (disposed || started) return; started = true; if (!demo) pulse.Start(); }
        public void Dispose() { if (disposed) return; disposed = true; pulse.Stop(); monitor.Changed -= QueueRender; monitor.Ready -= OnReady; foreach (var watch in monitor.Snapshot) { var removed = monitor.RemoveForActivity(watch.Id); if (removed != null) PublishActivity(removed, removed.Running ? "cancelled" : ActivityState(removed)); } monitor.Dispose(); activities.Clear(); }
    }
}
