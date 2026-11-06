using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using SideIsland.Desktop.Platforms;

namespace SideIsland.Desktop;

public sealed partial class IslandWindow
{
    // Allows diagnostics to await the exact initiated query, including a stale
    // request whose provider deliberately does not cooperate with cancellation.
    internal Task LatestPortQuery { get; private set; } = Task.CompletedTask;
    private void RenderPorts()
    {
        var content = new StackPanel { Spacing = 6 };
        var input = Input("ports", "3000, 8080"); input.Name = "ports-input"; input.MaxLength = 500;
        var state = Hint(T("点击查询后读取本地套接字，不会结束进程。", "Query local sockets without stopping any process.")); state.Name = "ports-status";
        var results = new StackPanel { Spacing = 6, Name = "ports-results" };
        var monitor = new CheckBox { Name = "ports-monitor", Content = Text(T("每 3 秒监测", "Monitor every 3 seconds"), 12), IsChecked = drafts.GetValueOrDefault("ports-monitor") == "true" };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        long generation = 0; bool detached = false, copying = false; CancellationTokenSource? pending = null; PortQuerySnapshot? snapshot = null;
        Button? copy = null, cancel = null;
        bool CanCopy() => !closing && !detached && !copying && snapshot is not null && pending is null;
        void UpdateActions() { if (copy is not null) copy.IsEnabled = CanCopy(); if (cancel is not null) cancel.IsEnabled = pending is not null && !detached; }
        void Invalidate(string message)
        {
            generation++; var previous = pending; pending = null; previous?.Cancel(); snapshot = null; results.Children.Clear(); state.Text = message; UpdateActions();
        }
        void RenderSnapshot(PortQuerySnapshot value)
        {
            results.Children.Clear();
            var groups = value.Owners.GroupBy(owner => (owner.Port, owner.Protocol, owner.Family, owner.Address, owner.Pid, owner.Name, owner.Path)).ToArray();
            state.Text = value.ObservedUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) + T($" · {value.Owners.Count} 条记录，{groups.Length} 个占用项", $" · {value.Owners.Count} records, {groups.Length} owners") + (value.Complete ? "" : T(" · 读取不完整", " · Partial read"));
            foreach (var issue in value.Issues.Take(8)) results.Children.Add(Hint(PortQueryReport.IssueText(issue, prefs.Language == "en") + (issue.Detail.Length == 0 ? "" : "\n" + issue.Detail)));
            if (value.Issues.Count > 8) results.Children.Add(Hint(T("更多读取问题见完整报告。", "More collection issues are included in the full report.")));
            int cards = 0;
            foreach (int port in value.RequestedPorts)
            {
                var owners = groups.Where(group => group.Key.Port == port).ToArray();
                if (owners.Length == 0 && cards++ < 200) results.Children.Add(Card(Text(port.ToString(CultureInfo.InvariantCulture) + " · " + (value.Complete ? T("未观察到匹配记录", "No matching record observed") : T("未知：读取不完整", "Unknown: incomplete read")), 12)));
                foreach (var group in owners)
                {
                    if (cards++ >= 200) continue; var owner = group.Key;
                    results.Children.Add(Card(new StackPanel { Spacing = 3, Children =
                    {
                        Text(owner.Port + " · " + owner.Protocol + " " + owner.Family + " · " + (owner.Name.Length == 0 ? T("程序未知", "Unknown process") : owner.Name), 13),
                        Hint("PID " + (owner.Pid?.ToString(CultureInfo.InvariantCulture) ?? "—") + T($" · {group.Count()} 条记录", $" · {group.Count()} records")),
                        Hint(owner.Address + " · " + string.Join(" / ", group.Select(item => item.State).Where(item => item.Length != 0).Distinct().Take(3))),
                        Hint(owner.Path.Length == 0 ? T("程序路径无法读取", "Executable path unavailable") : owner.Path)
                    } }));
                }
            }
            if (cards > 200) results.Children.Add(Hint(T($"另有 {cards - 200} 项未显示；复制报告包含全部记录。", $"{cards - 200} more cards omitted; the copied report contains every record.")));
        }
        async Task Query()
        {
            if (closing || detached) return;
            Invalidate(T("正在查询…", "Querying…")); long request = generation;
            int[] requested;
            try { requested = PortQueryReport.ParsePorts(input.Text ?? ""); }
            catch (ArgumentException) { state.Text = T("输入 1–65535 的端口，最多 64 项。", "Enter up to 64 ports between 1 and 65535."); return; }
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); pending = cancellation; UpdateActions();
            bool Current() => !closing && !detached && request == generation && ReferenceEquals(pending, cancellation) && !cancellation.IsCancellationRequested;
            try
            {
                var value = testMode && integrations.ReadPorts is null ? new PortQuerySnapshot(requested, DateTimeOffset.UtcNow, "demo", [], [new("demo", "")])
                    : await (integrations.ReadPorts ?? platform.ReadPortsAsync)(requested.ToArray(), cancellation.Token);
                if (!Current()) return;
                snapshot = value with { RequestedPorts = requested, Owners = value.Owners.ToArray(), Issues = value.Issues.ToArray() }; RenderSnapshot(snapshot);
            }
            catch (OperationCanceledException) { if (Current()) state.Text = T("查询已取消。", "Query cancelled."); }
            catch (Exception error)
            {
                if (Current()) { snapshot = new(requested, DateTimeOffset.UtcNow, "query", [], [new("command", error.Message)]); RenderSnapshot(snapshot); }
            }
            finally { if (ReferenceEquals(pending, cancellation)) pending = null; if (!closing && !detached) UpdateActions(); }
        }
        void StartQuery() => LatestPortQuery = Query();
        var query = Button(T("查询 / 刷新", "Query / refresh"), StartQuery); query.Name = "ports-query";
        cancel = Button(T("取消", "Cancel"), () => Invalidate(T("查询已取消。", "Query cancelled."))); cancel.Name = "ports-cancel";
        copy = AsyncButton(T("复制完整报告", "Copy full report"), async () =>
        {
            var captured = snapshot; long request = generation; if (captured is null) return;
            if (!PortQueryReport.TryFormat(captured, prefs.Language == "en", out var report)) { state.Text = T("报告超过 1 Mi 字符，请减少查询端口。", "Report exceeds 1 Mi characters. Query fewer ports."); return; }
            copying = true; UpdateActions();
            try
            {
                if (integrations.WritePortReport is not null) await integrations.WritePortReport(report!);
                else if (testMode) { state.Text = T("演示模式不修改剪贴板。", "Preview mode does not modify the clipboard."); return; }
                else if (Clipboard is not null) await Clipboard.SetTextAsync(report!);
                else throw new InvalidOperationException("Clipboard unavailable");
                if (!closing && !detached && request == generation && ReferenceEquals(snapshot, captured)) state.Text = T("已复制完整端口报告。", "Full port report copied.");
            }
            catch (Exception) { if (!closing && !detached && request == generation) state.Text = T("剪贴板正忙，请重试。", "Clipboard unavailable. Try again."); }
            finally { copying = false; if (!closing && !detached) UpdateActions(); }
        }, CanCopy); copy.Name = "ports-copy";
        input.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) Invalidate(T("端口已修改，请查询或等待下次监测。", "Ports changed. Query again or wait for monitoring.")); };
        input.KeyDown += (_, args) => { if (args.Key == Key.Enter) { StartQuery(); args.Handled = true; } };
        monitor.IsCheckedChanged += (_, _) => { drafts["ports-monitor"] = monitor.IsChecked == true ? "true" : "false"; if (monitor.IsChecked == true && !detached) { timer.Start(); if (pending is null) StartQuery(); } else timer.Stop(); };
        timer.Tick += (_, _) => { if (closing || detached) { timer.Stop(); return; } if (pending is null && content.IsEffectivelyVisible) StartQuery(); };
        void ClosingView(object? sender, WindowClosingEventArgs args) { if (!args.Cancel) DisposeView(); }
        void DisposeView() { if (detached) return; detached = true; timer.Stop(); generation++; var previous = pending; pending = null; previous?.Cancel(); snapshot = null; Closing -= ClosingView; UpdateActions(); }
        content.AttachedToVisualTree += (_, _) => { Closing += ClosingView; if (monitor.IsChecked == true && !detached) { timer.Start(); StartQuery(); } };
        content.DetachedFromVisualTree += (_, _) => DisposeView();
        content.Children.Add(input); content.Children.Add(Actions(query, copy)); content.Children.Add(Actions(monitor, cancel)); content.Children.Add(state); content.Children.Add(results);
        content.Children.Add(Hint(T("Mac 使用 lsof，Ubuntu 使用 ss。结果受权限及工具可见范围限制，没有记录不能保证端口空闲。", "Mac uses lsof; Ubuntu uses ss. Tool visibility and permissions limit results; no record does not guarantee a free port.")));
        UpdateActions(); body.Children.Add(content);
    }
}
