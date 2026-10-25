using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace CodexIsland
{
    internal sealed class PortMonitorDisplayRow
    {
        public int Port, Connections;
        public PortOwner Owner;
    }
    internal sealed class PortMonitorDisplaySnapshot
    {
        public readonly List<PortMonitorDisplayRow> Rows = new List<PortMonitorDisplayRow>();
        public readonly List<string> Errors = new List<string>();
        public int TotalBindings, TotalGroups, OmittedRows;
        public string Signature;
    }
    internal static class PortMonitorPresentation
    {
        internal const int MaximumCards = 200;
        // Pure background projection: connection-heavy servers need one owner card,
        // not one WPF subtree for each accepted connection.
        internal static PortMonitorDisplaySnapshot Build(PortMonitorResult source, IEnumerable<int> requested)
        {
            var snapshot = new PortMonitorDisplaySnapshot { TotalBindings = source.Owners.Count };
            snapshot.Errors.AddRange(source.Errors);
            var groups = source.Owners.GroupBy(x => new { x.Port, x.Protocol, x.Address, x.Pid })
                .Select(group => {
                    var first = group.First();
                    return new PortMonitorDisplayRow { Port = first.Port, Connections = group.Count(), Owner = new PortOwner {
                        Port = first.Port, Protocol = first.Protocol, Address = first.Address, Pid = first.Pid, Program = first.Program, Path = first.Path,
                        State = group.Any(x => String.Equals(x.State, "LISTEN", StringComparison.OrdinalIgnoreCase)) ? "LISTEN" : String.Join(" / ", group.Select(x => x.State).Distinct().OrderBy(x => x, StringComparer.Ordinal).Take(3))
                    } };
                }).ToLookup(x => x.Port);
            int totalRows = 0;
            foreach (int port in requested.Distinct().OrderBy(x => x))
            {
                var owners = groups[port].OrderByDescending(x => x.Owner.State == "LISTEN")
                    .ThenBy(x => x.Owner.Protocol, StringComparer.Ordinal).ThenBy(x => x.Owner.Address, StringComparer.Ordinal).ThenBy(x => x.Owner.Pid).ToArray();
                snapshot.TotalGroups += owners.Length;
                if (owners.Length == 0)
                {
                    totalRows++; if (snapshot.Rows.Count < MaximumCards) snapshot.Rows.Add(new PortMonitorDisplayRow { Port = port });
                }
                foreach (var owner in owners) { totalRows++; if (snapshot.Rows.Count < MaximumCards) snapshot.Rows.Add(owner); }
            }
            snapshot.OmittedRows = totalRows - snapshot.Rows.Count;
            var signature = new StringBuilder();
            foreach (var row in snapshot.Rows)
            {
                Add(signature, row.Port.ToString()); Add(signature, row.Connections.ToString());
                if (row.Owner == null) Add(signature, snapshot.Errors.Count == 0 ? "free" : "partial");
                else foreach (string field in new[] { row.Owner.Protocol, row.Owner.Address, row.Owner.Pid.ToString(), row.Owner.Program, row.Owner.Path, row.Owner.State }) Add(signature, field);
            }
            snapshot.Signature = signature.ToString(); return snapshot;
        }
        private static void Add(StringBuilder target, string value) { value = value ?? ""; target.Append(value.Length).Append(':').Append(value); }
    }

    internal sealed class PortMonitorPanel : StackPanel, IDisposable
    {
        private readonly TextBox ports;
        private readonly TextBlock status;
        private readonly StackPanel results = new StackPanel();
        private readonly CheckBox monitor;
        private readonly DispatcherTimer pulse = new DispatcherTimer();
        private readonly bool demo;
        private bool busy, disposed;
        private int generation;
        private PortMonitorDisplaySnapshot cachedSnapshot;
        private string cachedStatus, renderedSignature;
        private bool compactMonitoring;
        internal string CompactStatus { get { return cachedSnapshot == null ? "—" : cachedSnapshot.TotalGroups.ToString() + (cachedSnapshot.Errors.Count > 0 ? "?" : ""); } }
        internal void QueryPorts(string value) { if (disposed) return; ports.Text = value; Refresh(); }
        internal void SetCompactMonitoring(bool enabled) { bool changed = compactMonitoring != enabled; compactMonitoring = enabled; if (enabled && (changed || cachedSnapshot == null)) Refresh(); }
        public PortMonitorPanel(bool demoMode)
        {
            demo = demoMode;
            Children.Add(ToolStyle.Heading("端口被谁占用了"));
            Children.Add(ToolStyle.Hint("输入本地端口，查看 TCP / UDP、IPv4 / IPv6 的占用程序、PID 和路径。"));
            ports = ToolStyle.Input("3000, 8080"); ports.MaxLength = 500; ports.ToolTip = "多个端口用逗号、空格或顿号分隔"; Children.Add(ports);
            ports.KeyDown += (s, e) => { if (e.Key == Key.Enter) { Refresh(); e.Handled = true; } };
            ports.TextChanged += (s, e) => { generation++; cachedSnapshot = null; renderedSignature = null; SetMessage("端口已修改，点击查询或等待下次监测。"); if (IsVisible) results.Children.Clear(); };
            var actions = new Grid { Margin = new Thickness(0, 6, 0, 0) }; actions.ColumnDefinitions.Add(new ColumnDefinition()); actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) }); actions.ColumnDefinitions.Add(new ColumnDefinition());
            actions.Children.Add(PanelActionLayout.Create(1, IslandWindow.Button("查询 / 刷新", Refresh)));
            monitor = UiLocalization.Content(new CheckBox { Foreground = Display.Brush("#B4CABB"), VerticalAlignment = VerticalAlignment.Center, FontSize = 11 }, "每 3 秒监测"); Grid.SetColumn(monitor, 2); actions.Children.Add(monitor); Children.Add(actions);
            monitor.Checked += (s, e) => Refresh();
            status = ToolStyle.Hint(demo ? "演示模式：显示示例，不读取系统端口。" : "点击查询后读取系统端口表，不会结束任何进程。"); Children.Add(status); Children.Add(results);
            cachedStatus = status.Text;
            Children.Add(ToolStyle.Hint("受保护进程可能无法读取路径。连接随时会变化；未发现绑定也可能是进程刚退出，或该协议读取失败。"));
            pulse.Interval = TimeSpan.FromSeconds(3); pulse.Tick += (s, e) => { if (monitor.IsChecked == true || compactMonitoring) Refresh(); };
            IsVisibleChanged += (s, e) => { if (IsVisible && !disposed) RenderCached(); };
        }
        private async void Refresh()
        {
            if (disposed || busy) return;
            int[] requested;
            try { requested = PortMonitor.ParsePorts(ports.Text); }
            catch (ArgumentException error) { SetMessage(error.Message); return; }
            int ticket = ++generation; busy = true; SetMessage("正在读取…");
            try
            {
                PortMonitorDisplaySnapshot snapshot = await Task.Run(() => {
                    if (!demo) return PortMonitorPresentation.Build(PortMonitor.Read(requested), requested);
                    var sample = new PortMonitorResult(); sample.Owners.Add(new PortOwner { Port = requested[0], Protocol = "TCP IPv4", Address = "127.0.0.1", State = "LISTEN", Pid = 12345, Program = "示例程序", Path = @"C:\Example\server.exe" }); return PortMonitorPresentation.Build(sample, requested);
                });
                if (disposed || ticket != generation) return;
                cachedSnapshot = snapshot;
                cachedStatus = (demo ? "演示数据 · " : "") + DateTime.Now.ToString("HH:mm:ss") + " · " + snapshot.TotalBindings + " 条绑定 / 连接，合并为 " + snapshot.TotalGroups + " 个占用项"
                    + (snapshot.OmittedRows > 0 ? "\n最多显示 " + PortMonitorPresentation.MaximumCards + " 张卡片，另有 " + snapshot.OmittedRows + " 项未显示。可减少查询端口。" : "")
                    + (snapshot.Errors.Count == 0 ? "" : "\n" + String.Join("\n", snapshot.Errors));
                if (IsVisible) RenderCached();
            }
            catch (Exception error) { if (!disposed && ticket == generation) SetMessage("查询失败：" + error.Message); }
            finally { busy = false; }
        }
        private void SetMessage(string message) { cachedStatus = message; if (IsVisible) status.Text = message; }
        private void RenderCached()
        {
            if (disposed || !IsVisible) return;
            status.Text = cachedStatus ?? "";
            if (cachedSnapshot == null) { results.Children.Clear(); renderedSignature = null; return; }
            if (renderedSignature == cachedSnapshot.Signature) return;
            results.Children.Clear();
            foreach (var item in cachedSnapshot.Rows)
            {
                var owner = item.Owner;
                if (owner == null) { results.Children.Add(ToolStyle.Card(ToolStyle.Hint(item.Port + (cachedSnapshot.Errors.Count == 0 ? " · 未发现本地占用" : " · 已读取的协议中未发现占用（部分读取失败）")))); continue; }
                var row = new StackPanel(); var heading = IslandWindow.Text(owner.Port + " · " + owner.Program + "  · PID " + owner.Pid, 12, "#E5F2E9", true, false); heading.TextWrapping = TextWrapping.Wrap; row.Children.Add(heading);
                row.Children.Add(ToolStyle.Hint(owner.Protocol + " · " + owner.Address + " · " + owner.State + " · " + UiLocalization.Localize(item.Connections + " 条绑定 / 连接"), false));
                var path = ToolStyle.Input(owner.Path); path.IsReadOnly = true; path.TextWrapping = TextWrapping.Wrap; path.ToolTip = "可选择并复制程序路径"; row.Children.Add(path); results.Children.Add(ToolStyle.Card(row));
            }
            renderedSignature = cachedSnapshot.Signature;
        }
        public void Start() { if (!disposed) pulse.Start(); }
        public void Dispose() { disposed = true; generation++; pulse.Stop(); cachedSnapshot = null; cachedStatus = null; renderedSignature = null; }
    }
}
