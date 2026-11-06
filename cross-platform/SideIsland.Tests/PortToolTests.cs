using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SideIsland.Desktop;
using SideIsland.Desktop.Platforms;

namespace SideIsland.Tests;

internal static class PortToolTests
{
    private static readonly DateTimeOffset Observed = new(2026, 11, 6, 12, 34, 56, TimeSpan.Zero);
    public static void Run()
    {
        const string ss = "tcp LISTEN 0 128 127.0.0.1:3000 0.0.0.0:* users:((\"node  中文\",pid=12345,fd=7))\n" +
            "tcp ESTAB 0 0 127.0.0.1:3000 127.0.0.1:53000 users:((\"node  中文\",pid=12345,fd=8))\n";
        int paths = 0;
        var complete = CommandPlatformService.ParsePortCommand("ss", 0, ss, "", false, [3000,8080], Observed, _ => { paths++; return "/fixture/node\tquoted\"path"; });
        Check(complete.Complete && complete.Owners.Count == 2 && paths == 1, "complete ss fixture retains separate connections and caches path per PID");
        Check(complete.Owners[1].PeerAddress == "127.0.0.1:53000" && complete.Owners[0].State == "LISTEN" && complete.Owners[0].Family == "IPv4" && complete.Owners[0].Name == "node  中文", "ss metadata preserves endpoints, state, family and name whitespace");
        var malformed = Parse("ss", 0, ss + "unrecognized socket format\n", "");
        Check(!malformed.Complete && malformed.Owners.Count == 2 && malformed.Issues.Any(issue => issue.Code == "parse"), "unknown output never silently becomes a complete empty result");
        var denied = Parse("ss", 1, ss, "netlink: Permission denied\n");
        Check(denied.Owners.Count == 2 && !denied.Complete && denied.Issues.Any(issue => issue.Detail == "netlink: Permission denied\n"), "nonzero exit/stderr retains usable rows and original diagnostic");
        var truncated = CommandPlatformService.ParsePortCommand("ss", -1, ss + "tcp LISTEN 0", "output limit", true, [3000,8080], Observed, _ => "/fixture/node");
        Check(truncated.Owners.Count == 2 && truncated.Issues.Any(issue => issue.Code == "truncated") && truncated.Issues.Any(issue => issue.Code == "parse"), "truncation preserves only useful records and marks incomplete");
        var hidden = Parse("ss", 0, "udp UNCONN 0 0 [::]:8080 [::]:*\n", "");
        Check(hidden.Owners.Single().Pid is null && hidden.Owners[0].Family == "IPv6" && !hidden.Complete, "unavailable process ownership is explicit");
        var overflowPid = Parse("ss", 0, "tcp LISTEN 0 0 *:3000 *:* users:((\"x\",pid=999999999999999999,fd=1))\n", "");
        Check(overflowPid.Owners.Count == 1 && overflowPid.Owners[0].Pid is null && overflowPid.Issues.Any(issue => issue.Code == "parse"), "invalid PID keeps visible socket but never invents ownership");
        var brokenOwner = Parse("ss", 0, "tcp LISTEN 0 0 *:3000 *:* users:((\"good\",pid=20,fd=1),(\"broken\",pid=21\n", "");
        Check(brokenOwner.Owners.Count == 1 && brokenOwner.Owners[0].Pid == 20 && !brokenOwner.Complete, "partial multi-owner record cannot masquerade as complete");
        const string lsof = "p12345\0cname  中文\0\nf9\0tIPv6\0PTCP\0n[::1]:3000->[::1]:53000\0TST=ESTABLISHED\0\nf10\0tIPv4\0PUDP\0n*:8080\0\n";
        var mac = Parse("lsof", 0, lsof, "");
        Check(mac.Complete && mac.Owners.Count == 2 && mac.Owners[0].Address == "[::1]:3000" && mac.Owners[0].PeerAddress == "[::1]:53000" && mac.Owners[0].State == "ESTABLISHED", "lsof NUL process/file sets retain native endpoint/state metadata");
        var macTruncated = CommandPlatformService.ParsePortCommand("lsof", -1, lsof + "f11\0PTCP\0n*:30", "timeout", true, [3000,8080], Observed, _ => "/fixture/process");
        Check(macTruncated.Owners.Count == 2 && !macTruncated.Complete, "unterminated lsof field cannot invent a different local port");
        Check(!Parse("lsof", 0, "p12345\0cname\0", "").Complete, "process-only lsof output is an incomplete file selection");
        Check(Parse("lsof", 1, "", "").Complete && !Parse("lsof", 1, "", "permission denied").Complete, "lsof empty-selection exit one differs from warned/error exit");
        Check(!CommandPlatformService.ParsePortCommand("ss", 0, ss, "", false, [3000], Observed, _ => "").Complete, "missing executable path remains a collection issue");
        var missing = Parse("ss", -1, "", "ss was not found");
        foreach (bool english in new[] { false, true })
        {
            Check(PortQueryReport.TryFormat(complete, english, out var report) && report!.Contains(Observed.ToString("o"), StringComparison.Ordinal) && report.Contains("3000, 8080", StringComparison.Ordinal) && report.Contains("127.0.0.1:53000", StringComparison.Ordinal) && report.Contains("\"/fixture/node\tquoted\"\"path\"", StringComparison.Ordinal), "complete bilingual report retains query/time/all socket metadata and quotes TSV fields");
            Check(PortQueryReport.TryFormat(missing, english, out report) && report!.Contains(english ? "Unknown: collection incomplete" : "未知：采集不完整", StringComparison.Ordinal) && report.Contains("ss was not found", StringComparison.Ordinal), "missing command report cannot claim unoccupied ports");
        }
        var many = complete with { Owners = Enumerable.Range(0, 250).Select(index => new PortOwner(3000, "TCP", index + 1, "owner-" + index, "/all/" + index, "*:3000", "peer:" + index, "ESTAB", "IPv4")).ToArray() };
        Check(PortQueryReport.TryFormat(many, true, out var full) && full!.Contains("owner-249", StringComparison.Ordinal), "report includes records beyond display card cap");
        Check(!PortQueryReport.TryFormat(many with { Owners = [new(3000, "TCP", 1, "n", new string('x', PortQueryReport.MaximumCharacters))] }, true, out full) && full is null, "oversized report fails without copying a silently shortened report");
        Check(PortQueryReport.ParsePorts("8080，3000、8080; 65535\n1").SequenceEqual(new[] {1,3000,8080,65535}), "query parser normalizes common Chinese/English separators");
        foreach (string invalid in new[] { "", "0", "65536", "-1", "3000xyz", "1.0", string.Join(",", Enumerable.Range(1,65)) })
        { bool rejected = false; try { PortQueryReport.ParsePorts(invalid); } catch (ArgumentException) { rejected = true; } Check(rejected, "invalid query rejected before command execution"); }
        HelperTests().GetAwaiter().GetResult();
        Console.WriteLine("PASS: ss/lsof command/parser fixtures, partial failures and truncated output, original metadata, bounded complete bilingual reports; no native socket command execution.");
    }
    public static bool TryRunHelper(string[] args, out int exitCode)
    {
        exitCode = 0; if (args.Length != 2 || args[0] != "--port-helper") return false;
        Console.WriteLine("tcp LISTEN 0 0 127.0.0.1:3000 *:* users:((\"fixture\",pid=42,fd=1))"); Console.Out.Flush();
        switch (args[1])
        {
            case "failure": Console.Error.WriteLine("synthetic helper failure"); exitCode = 7; break;
            case "overflow": Console.Write(new string('x', 50000)); Console.Out.Flush(); break;
            case "timeout": Thread.Sleep(5000); break;
            case "success": break;
            default: exitCode = 2; break;
        }
        return true;
    }
    private static async Task HelperTests()
    {
        using var service = new CommandPlatformService();
        var method = typeof(CommandPlatformService).GetMethod("RunResolvedAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        async Task<(int Exit, string Output, string Error, bool Incomplete)> Run(string mode,int timeout,int limit,CancellationToken token=default)
        {
            string executable = Environment.ProcessPath ?? throw new InvalidOperationException("No process executable");
            string[] arguments = Path.GetFileNameWithoutExtension(executable).Equals("dotnet",StringComparison.OrdinalIgnoreCase) ? [typeof(PortToolTests).Assembly.Location,"--port-helper",mode] : ["--port-helper",mode];
            var task=(Task)method.Invoke(service,[executable,"fixture",arguments,token,timeout,limit])!; await task.WaitAsync(TimeSpan.FromSeconds(8));
            object result=task.GetType().GetProperty("Result")!.GetValue(task)!; var type=result.GetType();
            return ((int)type.GetProperty("ExitCode")!.GetValue(result)!, (string)type.GetProperty("Output")!.GetValue(result)!, (string)type.GetProperty("Error")!.GetValue(result)!, (bool)type.GetProperty("Incomplete")!.GetValue(result)!);
        }
        var success=await Run("success",3000,4096); Check(success.Exit==0&&!success.Incomplete&&success.Output.Contains("fixture",StringComparison.Ordinal),"real isolated helper success drained completely");
        var failure=await Run("failure",3000,4096); Check(failure.Exit==7&&failure.Output.Contains("tcp LISTEN",StringComparison.Ordinal)&&failure.Error.Contains("synthetic helper failure",StringComparison.Ordinal),"real failed helper preserves stdout and stderr");
        var overflow=await Run("overflow",3000,256); Check(overflow.Incomplete&&overflow.Output.Length<=256&&overflow.Output.StartsWith("tcp LISTEN",StringComparison.Ordinal),"real output-limit termination retains bounded diagnostic prefix");
        var timeout=await Run("timeout",1500,4096); Check(timeout.Incomplete&&timeout.Output.StartsWith("tcp LISTEN",StringComparison.Ordinal)&&timeout.Error.Contains("timed out",StringComparison.Ordinal),"real timeout kills owned helper but retains completed output");
        using var cancellation=new CancellationTokenSource(150); bool cancelled=false;
        try { await Run("timeout",3000,4096,cancellation.Token); } catch(OperationCanceledException){cancelled=true;}
        Check(cancelled,"caller cancellation remains cancellation rather than a misleading empty snapshot");
        Console.WriteLine("PASS: real owned test-helper success/failure/output-limit/timeout/cancellation through production command runner; no ss/lsof or user process queried.");
    }
    private static PortQuerySnapshot Parse(string source, int exit, string output, string diagnostic) => CommandPlatformService.ParsePortCommand(source, exit, output, diagnostic, false, [3000,8080], Observed, _ => "/fixture/process");
    private sealed class Pending(int[] ports, CancellationToken token)
    {
        public int[] Ports { get; } = ports;
        public CancellationToken Token { get; } = token;
        public readonly TaskCompletionSource<PortQuerySnapshot> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    public static void RunUi()
    {
        string directory = Path.Combine(Path.GetTempPath(), "side-island-port-ui-" + Guid.NewGuid().ToString("N"));
        var pending = new List<Pending>(); var copied = new List<string>();
        var sources = new IntegrationSources(_ => Task.FromResult(new MediaSnapshot(false,"","","",null,null,null,false,null)), (_,_) => Task.FromResult<string?>(null), (_,_) => throw new InvalidOperationException("quota not used"))
        { ReadPorts = (ports, token) => { var item = new Pending(ports,token); pending.Add(item); return item.Completion.Task; }, WritePortReport = report => { copied.Add(report); return Task.CompletedTask; } };
        var window = new IslandWindow(true,directory,sources);
        try
        {
            window.Show(); window.Expand(); AppearanceTestActions.Set(window, "en", window.Settings.LightTheme); window.SelectToolForTest("ports"); Flush(window);
            var input = Find<TextBox>(window,"ports-input"); var copy = Find<Button>(window,"ports-copy"); var state = Find<TextBlock>(window,"ports-status");
            Click(Find<Button>(window,"ports-query")); var old = window.LatestPortQuery; input.Text = "8080";
            Check(pending[0].Token.IsCancellationRequested && !copy.IsEnabled, "editing query cancels old work and clears copy immediately");
            Click(Find<Button>(window,"ports-query")); var latest = window.LatestPortQuery;
            var snapshot = new PortQuerySnapshot([8080], Observed, "fixture", [new(8080,"TCP",42,"new owner","/new/path","[::1]:8080","peer:9","LISTEN","IPv6")], []);
            pending[1].Completion.SetResult(snapshot); Await(window,latest); Click(copy);
            Check(copied.Count == 1 && copied[0].Contains("Queried ports: 8080",StringComparison.Ordinal) && copied[0].Contains("/new/path",StringComparison.Ordinal) && copied[0].Contains(Observed.ToString("o"),StringComparison.Ordinal), "copy uses captured query/time/metadata and injected writer only");
            pending[0].Completion.SetResult(snapshot with { Owners = [new(3000,"UDP",1,"OLD","/old")] }); Await(window,old);
            Click(copy); Check(copied.Count == 2 && !copied[1].Contains("OLD",StringComparison.Ordinal), "older delayed reply cannot replace current copied report");
            Click(Find<Button>(window,"ports-query")); old = window.LatestPortQuery; Click(Find<Button>(window,"ports-cancel")); pending[2].Completion.SetException(new IOException("stale failure")); Await(window,old);
            Check(!copy.IsEnabled && state.Text == "Query cancelled.", "cancel suppresses late error and stale export");
            input.Text = "3000"; Click(Find<Button>(window,"ports-query")); old = window.LatestPortQuery;
            window.SelectToolForTest("notes"); window.SelectToolForTest("ports"); Flush(window); pending[3].Completion.SetResult(snapshot); Await(window,old);
            Check(pending[3].Token.IsCancellationRequested && Find<TextBox>(window,"ports-input").Text == "3000" && !Find<Button>(window,"ports-copy").IsEnabled, "page detach cancels old provider without losing query draft");
            FilledLayouts(window,pending,snapshot);
            int index = pending.Count; Click(Find<Button>(window,"ports-query")); old = window.LatestPortQuery; window.Close(); pending[index].Completion.SetResult(snapshot); Await(window,old);
            Check(pending[index].Token.IsCancellationRequested, "window close cancels in-flight port read");
            Console.WriteLine("PASS: deterministic delayed port UI reads, edit/requery/reverse completion/cancel/detach/close, captured report copy via injected writer and 96 populated layouts; no clipboard or native desktop claim.");
        }
        finally { window.Close(); foreach(var item in pending) item.Completion.TrySetCanceled(); Dispatcher.UIThread.RunJobs(); if(Directory.Exists(directory))Directory.Delete(directory,true); }
    }
    private static void FilledLayouts(IslandWindow window,List<Pending> pending,PortQuerySnapshot source)
    {
        foreach(string language in new[]{"en","zh"}) foreach(bool light in new[]{false,true}) foreach(int width in new[]{320,480,760}) foreach(int height in new[]{360,720}) foreach(double scale in new[]{1d,1.25,1.5,2})
        {
            AppearanceTestActions.Set(window, language, light);
            window.SetRenderScaling(scale); window.SelectToolForTest("ports"); window.Width=width; window.Height=height; Flush(window);
            Find<TextBox>(window,"ports-input").Text="3000, 8080"; int index=pending.Count; Click(Find<Button>(window,"ports-query")); var task=window.LatestPortQuery;
            pending[index].Completion.SetResult(source with { RequestedPorts=[3000,8080], Issues=[new("diagnostic","Permission denied · 合成提示")], Owners=[new(8080,"TCP",42,"中文 program with a long name","/a/long/程序路径/with-spaces/app","[2001:db8::1]:8080","peer:54321","ESTABLISHED","IPv6")] }); Await(window,task);
            Check(Find<Button>(window,"ports-copy").IsEnabled, "partial query still offers complete diagnostic report");
            foreach(var control in window.GetVisualDescendants().OfType<Control>().Where(control=>control.Name?.StartsWith("ports-",StringComparison.Ordinal)==true))
            { var point=control.TranslatePoint(default,window); Check(point is Point position && position.X>=-1 && position.X+control.Bounds.Width<=width+1,"populated port controls stay within window width"); }
            foreach(var button in Find<StackPanel>(window,"ports-results").GetVisualDescendants().OfType<TextBlock>()) Check(button.Bounds.Height+1>=button.DesiredSize.Height,"owner/partial-error text is not cropped");
        }
    }
    private static T Find<T>(Window window,string name) where T:Control => window.GetVisualDescendants().OfType<T>().Single(control=>control.Name==name);
    private static void Click(Button button)=>button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Flush(Window window){Dispatcher.UIThread.RunJobs();window.UpdateLayout();}
    private static void Await(Window window,Task task){var watch=Stopwatch.StartNew();while(!task.IsCompleted&&watch.Elapsed<TimeSpan.FromSeconds(5)){Flush(window);Thread.Sleep(2);}Flush(window);Check(task.IsCompleted,"controlled query completion before deadline");task.GetAwaiter().GetResult();}
    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException("Ports: "+message);}
}
