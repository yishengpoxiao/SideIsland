using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
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
        public string States;
    }
    internal sealed class PortMonitorDisplaySnapshot
    {
        public readonly List<PortMonitorDisplayRow> Rows = new List<PortMonitorDisplayRow>();
        public readonly List<PortMonitorDisplayRow> ReportRows = new List<PortMonitorDisplayRow>();
        public readonly List<string> Errors = new List<string>();
        public int TotalBindings, TotalGroups, OmittedRows;
        public int[] RequestedPorts;
        public DateTime ObservedUtc;
        public string Signature;
    }
    internal static class PortMonitorPresentation
    {
        internal const int MaximumCards = 200;
        // Pure background projection: connection-heavy servers need one owner card,
        // not one WPF subtree for each accepted connection.
        internal static PortMonitorDisplaySnapshot Build(PortMonitorResult source, IEnumerable<int> requested)
        {
            var snapshot = new PortMonitorDisplaySnapshot { TotalBindings = source.Owners.Count, RequestedPorts = requested.Distinct().OrderBy(x => x).ToArray(), ObservedUtc = DateTime.UtcNow };
            snapshot.Errors.AddRange(source.Errors);
            var groups = source.Owners.GroupBy(x => new { x.Port, x.Protocol, x.Address, x.Pid })
                .Select(group => {
                    var first = group.First();
                    return new PortMonitorDisplayRow { Port = first.Port, Connections = group.Count(), States = String.Join(" / ", group.Select(x => x.State).Distinct().OrderBy(x => x, StringComparer.Ordinal)), Owner = new PortOwner {
                        Port = first.Port, Protocol = first.Protocol, Address = first.Address, Pid = first.Pid, Program = first.Program, Path = first.Path,
                        State = group.Any(x => String.Equals(x.State, "LISTEN", StringComparison.OrdinalIgnoreCase)) ? "LISTEN" : String.Join(" / ", group.Select(x => x.State).Distinct().OrderBy(x => x, StringComparer.Ordinal).Take(3))
                    } };
                }).ToLookup(x => x.Port);
            int totalRows = 0;
            foreach (int port in snapshot.RequestedPorts)
            {
                var owners = groups[port].OrderByDescending(x => x.Owner.State == "LISTEN")
                    .ThenBy(x => x.Owner.Protocol, StringComparer.Ordinal).ThenBy(x => x.Owner.Address, StringComparer.Ordinal).ThenBy(x => x.Owner.Pid).ToArray();
                snapshot.TotalGroups += owners.Length;
                if (owners.Length == 0)
                {
                    totalRows++; snapshot.ReportRows.Add(new PortMonitorDisplayRow { Port = port });
                }
                foreach (var owner in owners) { totalRows++; snapshot.ReportRows.Add(owner); }
            }
            snapshot.Rows.AddRange(snapshot.ReportRows.Take(MaximumCards));
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

    internal static class PortMonitorReport
    {
        internal const int MaximumCharacters = 1024 * 1024;
        internal static bool TryFormat(PortMonitorDisplaySnapshot snapshot, bool english, out string report)
        {
            report = null; if (snapshot == null) return false;
            var output = new StringBuilder();
            if (!Line(output, english ? "Side Island - port report" : "Side Island - 端口报告") ||
                !Line(output, (english ? "Observed UTC: " : "采集时间 UTC：") + snapshot.ObservedUtc.ToString("o", CultureInfo.InvariantCulture)) ||
                !Line(output, (english ? "Queried ports: " : "查询端口：") + String.Join(", ", snapshot.RequestedPorts.Select(x => x.ToString(CultureInfo.InvariantCulture)))) ||
                !Line(output, (english ? "Bindings/connections: " : "绑定 / 连接：") + snapshot.TotalBindings.ToString(CultureInfo.InvariantCulture) + (english ? "; grouped owners: " : "；合并占用项：") + snapshot.TotalGroups.ToString(CultureInfo.InvariantCulture)) ||
                !Line(output, english ? "All grouped owner records; the 200-card display limit does not truncate this report." : "包含全部合并占用项，不受界面 200 张卡片限制。") ||
                !Line(output, snapshot.Errors.Count == 0 ? (english ? "All four protocol tables were read. Bindings can change after capture." : "已读取四种协议表；绑定可能在采集后变化。") : (english ? "Partial read: missing records do not prove that a port is free." : "读取不完整：没有记录不代表端口空闲。"))) return false;
            foreach (string error in snapshot.Errors) if (!Line(output, (english ? "Read error\t" : "读取错误\t") + Field(error))) return false;
            if (!Line(output, english ? "Port\tProtocol\tAddress\tPID\tProgram\tPath\tStates\tBindings" : "端口\t协议\t地址\tPID\t程序\t路径\t状态\t绑定数")) return false;
            foreach (var row in snapshot.ReportRows)
            {
                if (row.Owner == null)
                {
                    string message = snapshot.Errors.Count == 0 ? (english ? "No local owner found" : "未发现本地占用") : (english ? "Unknown: protocol reads were incomplete" : "未知：协议读取不完整");
                    if (!Line(output, row.Port.ToString(CultureInfo.InvariantCulture) + "\t\t\t\t\t\t" + message + "\t0")) return false;
                }
                else
                {
                    var owner = row.Owner;
                    string states = String.Join(" / ", (row.States ?? owner.State ?? "").Split(new[] { " / " }, StringSplitOptions.None).Select(state => english && state == "已绑定" ? "Bound" : english && state == "未知" ? "Unknown" : state));
                    if (!Line(output, String.Join("\t", new[] { row.Port.ToString(CultureInfo.InvariantCulture), Field(owner.Protocol), Field(owner.Address), owner.Pid.ToString(CultureInfo.InvariantCulture), Field(owner.Program), Field(owner.Path), Field(states), row.Connections.ToString(CultureInfo.InvariantCulture) }))) return false;
                }
            }
            report = output.ToString(); return true;
        }
        private static bool Line(StringBuilder output, string value)
        { if (value.Length > MaximumCharacters - output.Length - Environment.NewLine.Length) return false; output.Append(value).Append(Environment.NewLine); return true; }
        // TSV quoting preserves user-controlled names/paths without allowing tabs
        // and line breaks to impersonate another owner record.
        private static string Field(string value)
        { value = value ?? ""; return value.IndexOfAny(new[] { '\t', '\r', '\n', '"' }) < 0 ? value : "\"" + value.Replace("\"", "\"\"") + "\""; }
    }

    internal sealed class PortMonitorPanel : StackPanel, IDisposable
    {
        private readonly TextBox ports;
        private readonly TextBlock status;
        private readonly TextBlock errors;
        private readonly Button copy;
        private readonly StackPanel results = new StackPanel();
        private readonly CheckBox monitor;
        private readonly DispatcherTimer pulse = new DispatcherTimer();
        private readonly bool demo;
        private readonly Func<int[], CancellationToken, Task<PortMonitorResult>> read;
        private readonly Action<string> writeClipboard;
        private bool busy, disposed, refreshQueued;
        private CancellationTokenSource pending;
        private int generation;
        private PortMonitorDisplaySnapshot cachedSnapshot;
        private string cachedStatus, cachedErrors, renderedSignature;
        private bool compactMonitoring;
        internal string CompactStatus { get { return cachedSnapshot == null ? "—" : cachedSnapshot.TotalGroups.ToString() + (cachedSnapshot.Errors.Count > 0 ? "?" : ""); } }
        internal void QueryPorts(string value) { if (disposed) return; ports.Text = value; Refresh(); }
        internal void SetCompactMonitoring(bool enabled) { bool changed = compactMonitoring != enabled; compactMonitoring = enabled; if (enabled && (changed || cachedSnapshot == null)) Refresh(); }
        public PortMonitorPanel(bool demoMode) : this(demoMode, null, null) { }
        internal PortMonitorPanel(bool demoMode, Func<int[], CancellationToken, Task<PortMonitorResult>> reader, Action<string> clipboardWriter)
        {
            demo = demoMode;
            read = reader ?? ((requested, cancellation) => Task.Run(() => {
                cancellation.ThrowIfCancellationRequested();
                if (!demo) return PortMonitor.Read(requested);
                var sample = new PortMonitorResult(); sample.Owners.Add(new PortOwner { Port = requested[0], Protocol = "TCP IPv4", Address = "127.0.0.1", State = "LISTEN", Pid = 12345, Program = "Example", Path = @"C:\Example\server.exe" }); return sample;
            }, cancellation));
            writeClipboard = clipboardWriter ?? (value => Clipboard.SetText(value));
            Children.Add(ToolStyle.Heading("端口被谁占用了"));
            Children.Add(ToolStyle.Hint("输入本地端口，查看 TCP / UDP、IPv4 / IPv6 的占用程序、PID 和路径。"));
            ports = ToolStyle.Input("3000, 8080"); ports.MaxLength = 500; ports.ToolTip = "多个端口用逗号、空格或顿号分隔"; Children.Add(ports);
            ports.KeyDown += (s, e) => { if (e.Key == Key.Enter) { Refresh(); e.Handled = true; } };
            ports.TextChanged += (s, e) => { generation++; cachedSnapshot = null; renderedSignature = null; if (pending != null) pending.Cancel(); SetMessage("端口已修改，点击查询或等待下次监测。"); UpdateCopy(); if (IsVisible) results.Children.Clear(); };
            copy = IslandWindow.Button("复制完整报告", () => { CopyReport(); }); copy.IsEnabled = false;
            var actions = PanelActionLayout.Create(2, IslandWindow.Button("查询 / 刷新", Refresh), copy); actions.Margin = new Thickness(0, 6, 0, 0); Children.Add(actions);
            monitor = UiLocalization.Content(new CheckBox { Foreground = Display.Brush("#B4CABB"), VerticalAlignment = VerticalAlignment.Center, FontSize = 11, Margin = new Thickness(0, 6, 0, 0) }, "每 3 秒监测"); Children.Add(monitor);
            monitor.Checked += (s, e) => Refresh();
            status = ToolStyle.Hint(demo ? "演示模式：显示示例，不读取系统端口。" : "点击查询后读取系统端口表，不会结束任何进程。"); errors = ToolStyle.Hint("", false); Children.Add(status); Children.Add(errors); Children.Add(results);
            cachedStatus = status.Text;
            Children.Add(ToolStyle.Hint("受保护进程可能无法读取路径。连接随时会变化；未发现绑定也可能是进程刚退出，或该协议读取失败。"));
            pulse.Interval = TimeSpan.FromSeconds(3); pulse.Tick += (s, e) => { if (!busy && (monitor.IsChecked == true || compactMonitoring)) Refresh(); };
            IsVisibleChanged += (s, e) => { if (IsVisible && !disposed) RenderCached(); };
        }
        private async void Refresh()
        {
            if (disposed) return;
            if (busy) { refreshQueued = true; return; }
            busy = true; UpdateCopy();
            try
            {
                do
                {
                    refreshQueued = false;
                    int[] requested;
                    try { requested = PortMonitor.ParsePorts(ports.Text); }
                    catch (ArgumentException error) { cachedSnapshot = null; renderedSignature = null; SetMessage(error.Message); if (IsVisible) RenderCached(); break; }
                    int ticket = ++generation; var cancellation = new CancellationTokenSource(); pending = cancellation; SetMessage("正在读取…");
                    try
                    {
                        var source = await read(requested, cancellation.Token);
                        var snapshot = await Task.Run(() => PortMonitorPresentation.Build(source, requested), cancellation.Token);
                        if (disposed || ticket != generation || cancellation.IsCancellationRequested) continue;
                        cachedSnapshot = snapshot;
                        cachedStatus = (demo ? "演示数据 · " : "") + snapshot.ObservedUtc.ToLocalTime().ToString("HH:mm:ss") + " · " + snapshot.TotalBindings + " 条绑定 / 连接，合并为 " + snapshot.TotalGroups + " 个占用项"
                            + (snapshot.OmittedRows > 0 ? " · 另有 " + snapshot.OmittedRows + " 项未显示，完整报告包含全部记录。" : "");
                        cachedErrors = String.Join("\n", snapshot.Errors);
                        if (IsVisible) RenderCached();
                    }
                    catch (OperationCanceledException) { if (!disposed && ticket == generation && !cancellation.IsCancellationRequested) { cachedSnapshot = null; renderedSignature = null; SetMessage("查询失败，请重试。"); if (IsVisible) RenderCached(); } }
                    catch (Exception error) { if (!disposed && ticket == generation) { cachedSnapshot = null; renderedSignature = null; SetMessage("查询失败，请重试。"); cachedErrors = error.Message; if (IsVisible) RenderCached(); } }
                    finally { if (Object.ReferenceEquals(pending, cancellation)) pending = null; cancellation.Dispose(); }
                } while (refreshQueued && !disposed);
            }
            finally { busy = false; UpdateCopy(); }
        }
        private void SetMessage(string message) { cachedStatus = message; cachedErrors = ""; if (IsVisible) { status.Text = message; errors.Text = ""; } }
        private void UpdateCopy() { if (copy != null) copy.IsEnabled = !disposed && !busy && cachedSnapshot != null; }
        internal bool CopyReport()
        {
            if (disposed || busy || cachedSnapshot == null) return false;
            if (demo) { SetMessage("演示模式不修改剪贴板。"); return false; }
            string report;
            if (!PortMonitorReport.TryFormat(cachedSnapshot, UiLocalization.IsEnglish, out report)) { SetMessage("报告超过 1 Mi 字符，请减少查询端口后重试。"); return false; }
            try { writeClipboard(report); SetMessage("已复制完整端口报告。"); return true; }
            catch (Exception) { SetMessage("剪贴板正忙，请重试。"); return false; }
        }
        private void RenderCached()
        {
            if (disposed || !IsVisible) return;
            status.Text = cachedStatus ?? "";
            errors.Text = cachedErrors ?? "";
            if (cachedSnapshot == null) { results.Children.Clear(); renderedSignature = null; return; }
            if (renderedSignature == cachedSnapshot.Signature) return;
            results.Children.Clear();
            foreach (var item in cachedSnapshot.Rows)
            {
                var owner = item.Owner;
                if (owner == null) { results.Children.Add(ToolStyle.Card(ToolStyle.Hint(item.Port + (cachedSnapshot.Errors.Count == 0 ? " · 未发现本地占用" : " · 已读取的协议中未发现占用（部分读取失败）")))); continue; }
                var row = new StackPanel(); var heading = IslandWindow.Text(owner.Port + " · " + owner.Program + "  · PID " + owner.Pid, 12, "#E5F2E9", true, false); heading.TextWrapping = TextWrapping.Wrap; row.Children.Add(heading);
                row.Children.Add(ToolStyle.Hint(owner.Protocol + " · " + owner.Address + " · " + owner.State + " · " + item.Connections + " 条绑定 / 连接"));
                var path = ToolStyle.Input(owner.Path); path.IsReadOnly = true; path.TextWrapping = TextWrapping.Wrap; path.ToolTip = "可选择并复制程序路径"; row.Children.Add(path); results.Children.Add(ToolStyle.Card(row));
            }
            renderedSignature = cachedSnapshot.Signature;
        }
        public void Start() { if (!disposed) pulse.Start(); }
        public void Dispose() { disposed = true; generation++; refreshQueued = false; if (pending != null) pending.Cancel(); pulse.Stop(); cachedSnapshot = null; cachedStatus = null; cachedErrors = null; renderedSignature = null; UpdateCopy(); }
    }
}
