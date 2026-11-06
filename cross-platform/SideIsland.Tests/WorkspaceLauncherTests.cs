using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using SideIsland.Core;
using SideIsland.Desktop.Platforms;

namespace SideIsland.Tests;

internal static class WorkspaceLauncherTests
{
    private static int checks;
    public static void Run()
    {
        checks = 0; CommandSemantics().GetAwaiter().GetResult(); SnapshotAndCancellation().GetAwaiter().GetResult(); OwnedProcesses().GetAwaiter().GetResult();
        Console.WriteLine($"PASS: workspace launcher {checks} checks; Mac/Linux command contracts, frozen arguments, per-item outcomes, cancellation skips pending launches, and real owned helper argv/no-kill handoff. No browser or user application opened; native Mac/Linux handoff not implied.");
    }
    public static bool TryRunHelper(string[] args, out int exitCode)
    {
        exitCode = 0; if (args.Length < 4 || args[0] != "--workspace-launch-helper") return false;
        string mode = args[1], report = args[2], release = args[3];
        File.WriteAllText(report + ".tmp", JsonSerializer.Serialize(new { ProcessId = Environment.ProcessId, Arguments = args.Skip(4).ToArray() })); File.Move(report + ".tmp", report);
        if (mode == "fail") { exitCode = 17; return true; }
        if (mode == "hold")
        {
            var elapsed = Stopwatch.StartNew(); while (!File.Exists(release) && elapsed.ElapsedMilliseconds < 8000) Thread.Sleep(10);
            exitCode = File.Exists(release) ? 0 : 70; return true;
        }
        exitCode = mode == "echo" ? 0 : 2; return true;
    }

    private static async Task CommandSemantics()
    {
        string root = Path.Combine(Path.GetTempPath(), "workspace-contract-only");
        string app = Path.Combine(root, "Editor 中文.app"), exe = Path.Combine(root, "native program"), folder = Path.Combine(root, "project $ literal");
        string[] args = ["space in one argument", "\"literal quote\"", "line1\nline2", "中文😀", "", "--option", "$(never-executed)"];
        var adapter = new FakeAdapter { Helper = "/usr/bin/open" }; adapter.Directories.UnionWith([app, folder]); adapter.Files.Add(exe); adapter.Executable.Add(exe);
        var resources = new[] { Resource("program", app, args), Resource("program", exe, args), Resource("folder", folder), Resource("url", "https://example.invalid/路径?q=a%20b&literal=$(value)") };
        var result = await new WorkspaceLauncher(WorkspaceLaunchPlatform.MacOS, adapter).RunAsync(resources, default);
        Check(result.Items.All(item => item.Status == WorkspaceLaunchStatus.HandedOff), "Mac command contracts hand off only valid resources");
        Check(adapter.Commands[0].Executable == "/usr/bin/open" && adapter.Commands[0].ObserveHelper && adapter.Commands[0].Arguments.SequenceEqual(new[] { "-a", app, "--args" }.Concat(args)), "Mac bundle uses open -a and a separate --args boundary");
        Check(adapter.Commands[1].Executable == exe && !adapter.Commands[1].ObserveHelper && adapter.Commands[1].Arguments.SequenceEqual(args), "native executable receives separate verbatim arguments");
        Check(adapter.Commands[2].Arguments.SequenceEqual([folder]) && adapter.Commands[3].Arguments.SequenceEqual([resources[3].Target]), "folder and URL remain exactly one helper argument");
        string appWithSeparator = app + Path.DirectorySeparatorChar; adapter.Directories.Add(appWithSeparator);
        result = await new WorkspaceLauncher(WorkspaceLaunchPlatform.MacOS, adapter).RunAsync([Resource("program", appWithSeparator)], default);
        Check(result.Items[0].Status == WorkspaceLaunchStatus.HandedOff && adapter.Commands[^1].Arguments.SequenceEqual(["-a", appWithSeparator, "--args"]), "Mac bundle with a trailing directory separator retains its literal path");
        adapter.Commands.Clear(); adapter.Helper = "/usr/bin/xdg-open";
        result = await new WorkspaceLauncher(WorkspaceLaunchPlatform.Linux, adapter).RunAsync([resources[2], resources[3], resources[1]], default);
        Check(adapter.Commands.Take(2).All(command => command.Executable == "/usr/bin/xdg-open" && command.Arguments.Count == 1 && command.ObserveHelper), "Ubuntu folders and URLs use xdg-open without shell syntax");
        Check(adapter.Commands[2].Executable == exe && adapter.Commands[2].Arguments.SequenceEqual(args), "Ubuntu native program uses direct executable and argv");
        result = await new WorkspaceLauncher(WorkspaceLaunchPlatform.Linux, adapter).RunAsync([resources[0]], default);
        Check(result.Items[0].ErrorCode == "invalid-program", "an app bundle is not treated as an Ubuntu native executable");

        adapter.Helper = null; int before = adapter.Commands.Count;
        result = await new WorkspaceLauncher(WorkspaceLaunchPlatform.Linux, adapter).RunAsync([resources[2]], default);
        Check(result.Items[0].ErrorCode == "helper-missing" && adapter.Commands.Count == before, "missing desktop opener is a clear failure, with no guessed shell fallback");
        adapter.Helper = "/usr/bin/xdg-open"; adapter.Executable.Clear();
        result = await new WorkspaceLauncher(WorkspaceLaunchPlatform.Linux, adapter).RunAsync([resources[1], Resource("program", Path.Combine(root, "missing"))], default);
        Check(result.Items.Select(item => item.ErrorCode).SequenceEqual(["not-executable", "target-missing"]), "missing targets and missing execute permission are distinct failures");
        result = await new WorkspaceLauncher(WorkspaceLaunchPlatform.Unsupported, adapter).RunAsync([resources[3]], default);
        Check(result.Items[0].ErrorCode == "unsupported-platform", "unsupported host does not guess a platform command");
        adapter.Executable.Add(exe); adapter.Start = (_, _) => Task.FromResult(new WorkspaceProcessOutcome(4));
        result = await new WorkspaceLauncher(WorkspaceLaunchPlatform.Linux, adapter).RunAsync([resources[2], resources[1]], default);
        Check(result.Items[0].Status == WorkspaceLaunchStatus.Failed && result.Items[0].ExitCode == 4 && result.Items[1].Status == WorkspaceLaunchStatus.HandedOff, "helper failure is reported per item, while direct task exit is not interpreted as task completion");
        adapter.Start = (_, _) => Task.FromResult(new WorkspaceProcessOutcome(3));
        result = await new WorkspaceLauncher(WorkspaceLaunchPlatform.Linux, adapter).RunAsync([resources[2]], default);
        Check(result.Items[0].ErrorCode == "helper-missing" && result.Items[0].ExitCode == 3, "documented xdg-open missing-required-tool exit gets a specific error");
        adapter.Start = (_, _) => Task.FromResult(new WorkspaceProcessOutcome(null));
        result = await new WorkspaceLauncher(WorkspaceLaunchPlatform.Linux, adapter).RunAsync([resources[2]], default);
        Check(result.Items[0].Status == WorkspaceLaunchStatus.Unconfirmed && result.Items[0].ErrorCode == "helper-pending", "long-lived helper is not falsely reported as accepted or completed");
        int attempts = 0; adapter.Start = (_, _) => ++attempts == 1 ? throw new Win32Exception(2) : Task.FromResult(new WorkspaceProcessOutcome(0));
        result = await new WorkspaceLauncher(WorkspaceLaunchPlatform.Linux, adapter).RunAsync([resources[2], resources[3]], default);
        Check(result.Items[0].ErrorCode == "helper-missing" && result.Items[1].Status == WorkspaceLaunchStatus.HandedOff, "helper disappearing at start does not prevent later entries from being reported");
        before = adapter.Commands.Count;
        result = await new WorkspaceLauncher(WorkspaceLaunchPlatform.Linux, adapter).RunAsync([Resource("url", "javascript:alert(1)"), Resource("program", exe, ["bad\0argument"]), Resource("program", "relative")], default);
        Check(result.Items.All(item => item.ErrorCode == "invalid-resource") && adapter.Commands.Count == before, "invalid URLs, NUL arguments and relative program paths never launch");
    }

    private static async Task SnapshotAndCancellation()
    {
        string exe = Path.Combine(Path.GetTempPath(), "workspace-owned-contract-program");
        var adapter = new FakeAdapter(); adapter.Files.Add(exe); adapter.Executable.Add(exe);
        var arguments = new List<string> { "original second argument" };
        var first = Resource("program", exe, ["first"]); var second = Resource("program", exe, arguments);
        var resources = new List<PortableWorkspaceResource> { first, second };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource<WorkspaceProcessOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        adapter.Start = (_, _) => { if (adapter.Commands.Count == 1) { entered.SetResult(); return release.Task; } return Task.FromResult(new WorkspaceProcessOutcome(null)); };
        Task<WorkspaceLaunchResult> run = new WorkspaceLauncher(WorkspaceLaunchPlatform.Linux, adapter).RunAsync(resources, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); arguments[0] = "edited after launch"; resources[1] = Resource("program", exe, ["replaced entry"]);
        release.SetResult(new(null)); var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
        Check(adapter.Commands.Count == 2 && adapter.Commands[1].Arguments.SequenceEqual(["original second argument"]) && result.Items[1].SourceId == second.Id, "resource list and nested mutable arguments are frozen before the first await");

        adapter.Commands.Clear(); using var cancellation = new CancellationTokenSource();
        entered = new(TaskCreationOptions.RunContinuationsAsynchronously); release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        adapter.Start = (_, _) => { entered.SetResult(); return release.Task; };
        run = new WorkspaceLauncher(WorkspaceLaunchPlatform.Linux, adapter).RunAsync([first, second], cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel(); release.SetResult(new(null)); result = await run.WaitAsync(TimeSpan.FromSeconds(5));
        Check(result.Items[0].Status == WorkspaceLaunchStatus.HandedOff && result.Items[1].Status == WorkspaceLaunchStatus.Skipped && adapter.Commands.Count == 1, "cancel after a program starts preserves its handoff and skips unstarted entries");
        adapter.Commands.Clear(); result = await new WorkspaceLauncher(WorkspaceLaunchPlatform.Linux, adapter).RunAsync([first, second], cancellation.Token);
        Check(result.Items.All(item => item.Status == WorkspaceLaunchStatus.Skipped) && adapter.Commands.Count == 0, "pre-cancelled scene opens nothing");

        bool rejected = false; try { await new WorkspaceLauncher(WorkspaceLaunchPlatform.Linux, adapter).RunAsync(Enumerable.Range(0, 31).Select(_ => Resource("program", exe)).ToArray(), default); } catch (WorkspaceProblem error) { rejected = error.Code == WorkspaceProblemCode.TooManySceneResources; }
        Check(rejected && adapter.Commands.Count == 0, "scene limit enforced before any launch");
    }

    private static async Task OwnedProcesses()
    {
        string directory = Path.Combine(Path.GetTempPath(), "side-island-workspace-launch-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var children = new List<Process>(); var releases = new List<string>(); var starts = new List<ProcessStartInfo>();
        Process Start(ProcessStartInfo info)
        {
            starts.Add(info); var child = Process.Start(info) ?? throw new InvalidOperationException("Owned helper did not start"); children.Add(Process.GetProcessById(child.Id)); return child;
        }
        (string Executable, string[] Arguments, string Report, string Release) Command(string mode, params string[] payload)
        {
            string key = Guid.NewGuid().ToString("N"), report = Path.Combine(directory, key + ".json"), release = Path.Combine(directory, key + ".release"); releases.Add(release);
            string executable = Environment.ProcessPath ?? throw new InvalidOperationException("No own process path");
            string[] args = ["--workspace-launch-helper", mode, report, release, .. payload];
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) args = [Assembly.GetEntryAssembly()!.Location, .. args];
            return (executable, args, report, release);
        }
        try
        {
            string sentinel = Path.Combine(directory, "must-not-be-created");
            string[] payload = ["one argument with spaces", "literal \"quote\"", "line one\nline two", "中文😀", "", "backslash\\ending\\", "$(touch " + sentinel + ")", "; touch " + sentinel, "& echo should-not-run", "--flag=value"];
            var echo = Command("echo", payload); var adapter = new SystemWorkspaceLaunchAdapter(Start);
            var result = await new WorkspaceLauncher(WorkspaceLaunchPlatform.Linux, adapter).RunAsync([Resource("program", echo.Executable, echo.Arguments)], default);
            await WaitForFile(echo.Report); using (var json = JsonDocument.Parse(File.ReadAllText(echo.Report))) Check(json.RootElement.GetProperty("Arguments").EnumerateArray().Select(value => value.GetString()).SequenceEqual(payload), "actual owned executable receives spaces, quotes, newline, Unicode, empty argument and shell metacharacters literally");
            Check(result.Items.Single().Status == WorkspaceLaunchStatus.HandedOff && !File.Exists(sentinel), "actual direct handoff does not run any shell metacharacter command");
            Check(starts.All(info => !info.UseShellExecute && info.Arguments.Length == 0 && info.ArgumentList.Count > 0 && !info.RedirectStandardError && !info.RedirectStandardOutput), "actual start uses ArgumentList, no command string, shell or child output pipes");

            var hold = Command("hold", "still running"); using var cancellation = new CancellationTokenSource();
            result = await new WorkspaceLauncher(WorkspaceLaunchPlatform.Linux, adapter).RunAsync([Resource("program", hold.Executable, hold.Arguments)], cancellation.Token);
            await WaitForFile(hold.Report); cancellation.Cancel();
            Check(result.Items.Single().Status == WorkspaceLaunchStatus.HandedOff && !children[^1].HasExited, "cancel after direct handoff does not terminate the user-owned application process");
            File.WriteAllText(hold.Release, "release owned helper"); await children[^1].WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));

            var fail = Command("fail"); var observer = new SystemWorkspaceLaunchAdapter(Start, TimeSpan.FromSeconds(5));
            var outcome = await observer.StartAsync(new(fail.Executable, fail.Arguments, true), default);
            Check(outcome.ExitCode == 17, "real owned opener-style early failure preserves its exit code");
            var bounded = Command("hold"); observer = new(Start, TimeSpan.FromMilliseconds(30));
            outcome = await observer.StartAsync(new(bounded.Executable, bounded.Arguments, true), default); await WaitForFile(bounded.Report);
            Check(outcome.ExitCode is null && !children[^1].HasExited, "bounded opener observation does not wait for or kill a long-running application");
            File.WriteAllText(bounded.Release, "release owned helper"); await children[^1].WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var interrupted = Command("hold"); observer = new(Start, TimeSpan.FromSeconds(5)); using var stopObservation = new CancellationTokenSource();
            var observing = observer.StartAsync(new(interrupted.Executable, interrupted.Arguments, true), stopObservation.Token); await WaitForFile(interrupted.Report); stopObservation.Cancel(); outcome = await observing.WaitAsync(TimeSpan.FromSeconds(5));
            Check(outcome.ExitCode is null && outcome.CancelledAfterStart && !children[^1].HasExited, "cancelled opener observation reports unknown handoff and leaves the launched process running");
            File.WriteAllText(interrupted.Release, "release owned helper"); await children[^1].WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            foreach (string release in releases) File.WriteAllText(release, "fixture cleanup");
            foreach (var child in children)
            {
                try { if (!child.HasExited) await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(9)); }
                finally { child.Dispose(); }
            }
            string full = Path.GetFullPath(directory), parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (Path.GetDirectoryName(full) != parent || !Path.GetFileName(full).StartsWith("side-island-workspace-launch-", StringComparison.Ordinal)) throw new InvalidOperationException("Owned workspace fixture escaped temp root.");
            Directory.Delete(full, true);
        }
    }
    private static PortableWorkspaceResource Resource(string kind, string target, IReadOnlyList<string>? arguments = null) => new(Guid.NewGuid().ToString("N"), "Synthetic shortcut", kind, target, arguments ?? Array.Empty<string>());
    private static async Task WaitForFile(string path)
    {
        var elapsed = Stopwatch.StartNew(); while ((!File.Exists(path) || new FileInfo(path).Length == 0) && elapsed.ElapsedMilliseconds < 5000) await Task.Delay(10);
        Check(File.Exists(path) && new FileInfo(path).Length > 0, "owned helper publishes its argument report before the bounded deadline");
    }
    private static void Check(bool condition, string label) { checks++; if (!condition) throw new InvalidOperationException("Workspace launcher: " + label); }
    private sealed class FakeAdapter : IWorkspaceLaunchAdapter
    {
        internal readonly HashSet<string> Files = new(StringComparer.Ordinal), Directories = new(StringComparer.Ordinal), Executable = new(StringComparer.Ordinal);
        internal readonly List<WorkspaceLaunchCommand> Commands = [];
        internal string? Helper = "/usr/bin/xdg-open";
        internal Func<WorkspaceLaunchCommand, CancellationToken, Task<WorkspaceProcessOutcome>> Start = (_, _) => Task.FromResult(new WorkspaceProcessOutcome(0));
        public bool FileExists(string path) => Files.Contains(path);
        public bool DirectoryExists(string path) => Directories.Contains(path);
        public bool IsExecutable(string path) => Executable.Contains(path);
        public string? FindHelper(WorkspaceLaunchPlatform platform) => Helper;
        public Task<WorkspaceProcessOutcome> StartAsync(WorkspaceLaunchCommand command, CancellationToken cancellationToken)
        { Commands.Add(command); return Start(command, cancellationToken); }
    }
}
