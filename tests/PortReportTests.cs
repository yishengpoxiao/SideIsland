using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace CodexIsland
{
    internal static class PortReportTests
    {
        private static int checks;
        private sealed class Request
        {
            internal int[] Ports;
            internal CancellationToken Cancellation;
            internal readonly TaskCompletionSource<PortMonitorResult> Completion = new TaskCompletionSource<PortMonitorResult>();
        }
        private sealed class Reader
        {
            internal readonly List<Request> Requests = new List<Request>();
            internal Task<PortMonitorResult> Read(int[] ports, CancellationToken cancellation)
            { var request = new Request { Ports = ports.ToArray(), Cancellation = cancellation }; Requests.Add(request); return request.Completion.Task; }
        }
        internal static void Run()
        {
            Formatting();
            var context = SynchronizationContext.Current; string language = UiLocalization.Language;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try { LatestRequest(); Disposal(); FailureAndDemo(); LabelsAndLayout(); }
            finally { UiLocalization.Configure(language); SynchronizationContext.SetSynchronizationContext(context); }
            Console.WriteLine("PASS: " + checks + " full port report, bounded export, bilingual labels and deterministic latest-query/cancellation/disposal checks; no real clipboard writes or shown windows.");
        }
        private static PortMonitorResult Data(int port, int owners = 1)
        {
            var result = new PortMonitorResult();
            for (int i = 0; i < owners; i++) result.Owners.Add(new PortOwner { Port = port, Pid = i + 100, Protocol = "TCP IPv4", Address = "127.0.0.1", Program = "worker-" + i, Path = @"C:\端口\worker-" + i + ".exe", State = "LISTEN" });
            return result;
        }
        private static void Formatting()
        {
            var data = Data(3000, 250); int[] ports = { 8080, 3000 };
            var snapshot = PortMonitorPresentation.Build(data, ports); ports[0] = 9999;
            Check(snapshot.Rows.Count == 200 && snapshot.ReportRows.Count == 251 && snapshot.OmittedRows == 51, "all report rows survive the display card cap, including empty queried ports");
            Check(snapshot.RequestedPorts.SequenceEqual(new[] { 3000, 8080 }) && snapshot.ObservedUtc.Kind == DateTimeKind.Utc, "snapshot owns normalized query and UTC observation timestamp");
            data.Owners[0].Path = "changed after snapshot";
            string english, chinese;
            Check(PortMonitorReport.TryFormat(snapshot, true, out english) && PortMonitorReport.TryFormat(snapshot, false, out chinese), "both report languages format successfully");
            // Definite assignment for the short-circuit expression above.
            PortMonitorReport.TryFormat(snapshot, false, out chinese);
            Check(english.Contains("worker-249") && english.Contains("8080\t\t\t\t\t\tNo local owner found") && english.Contains("grouped owners: 250"), "English report includes last owner and empty port beyond 200 cards");
            Check(english.Contains(@"C:\端口\worker-0.exe") && !english.Contains("changed after snapshot") && chinese.Contains("合并占用项：250") && chinese.Contains("查询端口：3000, 8080"), "source mutation and language selection do not rewrite captured user paths");
            data = Data(3000); data.Owners[0].State = "ESTABLISHED";
            data.Owners.Add(new PortOwner { Port = 3000, Pid = 100, Protocol = "TCP IPv4", Address = "127.0.0.1", Program = "worker-0", Path = @"C:\端口\worker-0.exe", State = "LISTEN" });
            snapshot = PortMonitorPresentation.Build(data, new[] { 3000 });
            PortMonitorReport.TryFormat(snapshot, true, out english);
            Check(snapshot.Rows[0].Owner.State == "LISTEN" && english.Contains("ESTABLISHED / LISTEN\t2"), "report preserves every grouped state while display prioritizes listener");
            data.Owners[0].Program = "name\twith\n\"quotes\""; data.Owners[0].State = "已绑定";
            data.Owners.RemoveAt(1); data.Errors.Add("TCP IPv6: own fixture denied"); data.Errors.Add("UDP IPv4: fixture unavailable");
            snapshot = PortMonitorPresentation.Build(data, new[] { 3000, 8080 }); PortMonitorReport.TryFormat(snapshot, true, out english);
            Check(english.Contains("\"name\twith\n\"\"quotes\"\"\"") && english.Contains("\tBound\t1"), "TSV quotes raw name correctly and translates only known state");
            Check(english.Contains("Partial read:") && english.Contains("TCP IPv6: own fixture denied") && english.Contains("UDP IPv4: fixture unavailable") && english.Contains("8080\t\t\t\t\t\tUnknown:"), "all read failures appear and missing owners are unknown after partial failure");
            data.Owners.Clear(); snapshot = PortMonitorPresentation.Build(data, new[] { 8080 }); PortMonitorReport.TryFormat(snapshot, false, out chinese);
            Check(chinese.Contains("读取不完整") && chinese.Contains("未知：协议读取不完整") && chinese.Contains("读取错误\t"), "Chinese all-failure report never claims a free port");
            data = Data(3000); data.Owners[0].Path = new string('x', PortMonitorReport.MaximumCharacters);
            Check(!PortMonitorReport.TryFormat(PortMonitorPresentation.Build(data, new[] { 3000 }), true, out english) && english == null, "oversized report is refused without exposing a truncated export");
        }
        private static void LatestRequest()
        {
            var reader = new Reader(); var copied = new List<string>();
            using (var panel = new PortMonitorPanel(false, reader.Read, copied.Add))
            {
                panel.QueryPorts("3000"); panel.QueryPorts("8080"); panel.QueryPorts("9090");
                Check(reader.Requests.Count == 1 && reader.Requests[0].Cancellation.IsCancellationRequested && !panel.CopyReport(), "edits cancel stale request and never copy while busy");
                reader.Requests[0].Completion.SetResult(Data(3000));
                Pump(() => reader.Requests.Count == 2);
                Check(reader.Requests[1].Ports.SequenceEqual(new[] { 9090 }), "rapid edits coalesce into one latest query without losing it");
                reader.Requests[1].Completion.SetResult(Data(9090)); Pump(() => !Field<bool>(panel, "busy"));
                Check(Field<PortMonitorDisplaySnapshot>(panel, "cachedSnapshot").RequestedPorts.SequenceEqual(new[] { 9090 }) && panel.CopyReport(), "latest result becomes the only copyable snapshot");
                Check(copied.Count == 1 && copied[0].Contains("9090") && !copied[0].Contains("8080") && !copied[0].Contains("3000"), "copy uses completed query identity, not obsolete editor requests");
                Field<TextBox>(panel, "ports").Text = "443";
                Check(!panel.CopyReport() && !Field<Button>(panel, "copy").IsEnabled && reader.Requests.Count == 2, "editing alone invalidates copy without unsolicited querying");
                panel.QueryPorts("443"); panel.QueryPorts("invalid"); reader.Requests[2].Completion.SetResult(Data(443)); Pump(() => !Field<bool>(panel, "busy"));
                Check(reader.Requests.Count == 3 && !panel.CopyReport() && panel.CompactStatus == "—", "latest invalid input cancels prior result without starting a bad query");
            }
        }
        private static void Disposal()
        {
            var reader = new Reader(); int copies = 0; var panel = new PortMonitorPanel(false, reader.Read, value => copies++);
            panel.QueryPorts("3000"); panel.QueryPorts("8080"); panel.Dispose();
            Check(reader.Requests[0].Cancellation.IsCancellationRequested && !panel.CopyReport(), "dispose cancels in-flight read and blocks copying");
            reader.Requests[0].Completion.SetResult(Data(3000)); Pump(() => !Field<bool>(panel, "busy"));
            Check(reader.Requests.Count == 1 && Field<PortMonitorDisplaySnapshot>(panel, "cachedSnapshot") == null && copies == 0, "disposed panel never services queued query or publishes late data");
        }
        private static void FailureAndDemo()
        {
            var reader = new Reader(); bool clipboardBusy = false; int copies = 0;
            using (var panel = new PortMonitorPanel(false, reader.Read, value => { if (clipboardBusy) throw new IOException("fixture clipboard unavailable"); copies++; }))
            {
                panel.QueryPorts("3000"); reader.Requests[0].Completion.SetResult(Data(3000)); Pump(() => !Field<bool>(panel, "busy"));
                clipboardBusy = true; Check(!panel.CopyReport() && Field<Button>(panel, "copy").IsEnabled, "clipboard failure retains valid report for retry");
                clipboardBusy = false; Check(panel.CopyReport() && copies == 1, "clipboard retry uses the same captured report");
                panel.QueryPorts("3000"); reader.Requests[1].Completion.SetException(new IOException("fixture read failure")); Pump(() => !Field<bool>(panel, "busy"));
                Check(!panel.CopyReport() && panel.CompactStatus == "—" && Field<string>(panel, "cachedErrors").Contains("fixture read failure"), "failed refresh cannot export the previous successful snapshot");
                panel.QueryPorts("8080"); reader.Requests[2].Completion.SetResult(Data(8080)); Pump(() => !Field<bool>(panel, "busy"));
                UiLocalization.Configure("en"); Check(panel.CopyReport() && copies == 2, "query recovers after failure and copies current-language report");
            }
            reader = new Reader();
            using (var panel = new PortMonitorPanel(true, reader.Read, value => copies++))
            {
                panel.QueryPorts("3000"); reader.Requests[0].Completion.SetResult(Data(3000)); Pump(() => !Field<bool>(panel, "busy"));
                Check(!panel.CopyReport() && copies == 2, "demo mode never invokes even the injected clipboard writer");
            }
        }
        private static void LabelsAndLayout()
        {
            foreach (string language in new[] { "zh", "en" })
            {
                UiLocalization.Configure(language);
                using (var panel = new PortMonitorPanel(true))
                {
                    foreach (int width in new[] { 280, 440, 720 })
                    {
                        panel.Measure(new Size(width, Double.PositiveInfinity)); panel.Arrange(new Rect(0, 0, width, panel.DesiredSize.Height)); panel.UpdateLayout();
                        var copy = Field<Button>(panel, "copy"); var buttons = ((Grid)copy.Parent).Children.OfType<Button>().ToArray();
                        Check(buttons.Length == 2 && Math.Abs(buttons[0].ActualWidth - buttons[1].ActualWidth) < .1 && copy.ActualWidth > 80, "same-group actions align at " + language + " " + width);
                    }
                }
                string value = UiLocalization.Localize("12:00:00 · 10 条绑定 / 连接，合并为 2 个占用项 · 另有 51 项未显示，完整报告包含全部记录。");
                Check(language == "zh" ? value.Contains("绑定") : value.Contains("10 bindings / connections") && value.Contains("51 more rows") && !value.Contains("绑定"), "summary and hidden-row counts translate together");
                Check(language == "zh" ? UiLocalization.Localize("演示数据 · 12:00:00 · 1 条绑定 / 连接，合并为 1 个占用项").StartsWith("演示数据") : UiLocalization.Localize("演示数据 · 12:00:00 · 1 条绑定 / 连接，合并为 1 个占用项").StartsWith("Demo data"), "demo prefix translates before the broad count template");
                Check(language == "zh" ? UiLocalization.Localize("TCP IPv4 · 127.0.0.1 · 已绑定 · 3 条绑定 / 连接").Contains("已绑定") : UiLocalization.Localize("TCP IPv4 · 127.0.0.1 · 已绑定 · 3 条绑定 / 连接").Contains("Bound · 3 bindings / connections"), "known states and connection counts localize without changing address");
            }
        }
        private static T Field<T>(object value, string name) { return (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value); }
        private static void Pump(Func<bool> done)
        {
            if (done()) return;
            var elapsed = Stopwatch.StartNew(); var frame = new DispatcherFrame(); var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(5) };
            timer.Tick += (sender, args) => { if (done() || elapsed.ElapsedMilliseconds > 10000) frame.Continue = false; };
            timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
            Check(done(), "deterministic async operation completed within timeout");
        }
        private static void Check(bool value, string label) { checks++; if (!value) throw new Exception("Port report: " + label); }
    }
}
