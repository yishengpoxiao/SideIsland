using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SideIsland.Core;
using SideIsland.Desktop;
using CodexIsland;

namespace SideIsland.Tests;

internal static class WatchTests
{
    private static int checks;
    public static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "side-island-watch-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var book = new WatchBook(); string path = Path.Combine(directory, "created.txt");
            var entry = book.AddFile(path); Reject(() => book.AddFile(path), "duplicate");
            Check(book.Poll().Count == 0, "missing file stays pending"); File.WriteAllText(path, "own fixture");
            Check(book.Poll().Single().Id == entry.Id && book.Poll().Count == 0, "real file appearance alerts exactly once");
            Check(book.ClearFinished() == 1 && book.Snapshot.Length == 0, "finished entries can be cleared");
            Reject(() => book.AddFile(path), "exists"); Reject(() => book.AddFile(directory), "directory"); Reject(() => book.AddFile("relative.txt"), "path"); Reject(() => book.AddProcess(0), "pid");
            var current = WatchBook.ReadProcess(Environment.ProcessId); Check(current is not null && current.Pid == Environment.ProcessId && current.StartedUtcTicks > 0, "actual current process identity is readable without process control");
            Check(WatchBook.ReadProcess(int.MaxValue) is null, "nonexistent PID is explicit exit evidence");
            Capacity(directory); ProcessErrors(); RemovalRace(directory); Cancellation(directory); SchedulerFairness(directory);
            string probed = ""; var exact = new WatchBook(value => { probed = value; return false; }, _ => false, _ => null!);
            string spaced = Path.Combine(directory, "meaningful-space "); exact.AddFile(spaced); Check(probed == Path.GetFullPath(spaced) && (OperatingSystem.IsWindows() || probed == spaced), "target follows native path semantics and preserves meaningful Unix trailing whitespace");
            Console.WriteLine("PASS: " + checks + " watch lifecycle checks, real temporary file/current process identity, 24-active/24-finished bounds, duplicate/existing targets, PID reuse, metadata errors, cancellation and late-removal races.");
        }
        finally { Directory.Delete(directory, true); }
    }
    private static void Capacity(string directory)
    {
        bool exists = false; var book = new WatchBook(_ => exists, _ => false, _ => null!);
        for (int round = 0; round < 4; round++)
        {
            exists = false;
            for (int index = 0; index < 24; index++) book.AddFile(Path.Combine(directory, round + "-" + index));
            Reject(() => book.AddFile(Path.Combine(directory, "overflow")), "capacity");
            Check(book.Snapshot.Count(item => !item.Completed) == 24 && book.Snapshot.Length <= 48, "active and retained bounds are independent");
            exists = true; Check(book.Poll().Count == 24 && book.Snapshot.Length == 24 && book.Snapshot.All(item => item.Completed), "each completed round frees all active slots and bounds history");
        }
        Check(book.ClearFinished() == 24 && book.ClearFinished() == 0, "clear finished is idempotent");
        var appeared = new HashSet<string>(); book = new WatchBook(appeared.Contains, _ => false, _ => null!);
        var original = book.AddFile(Path.Combine(directory, "oldest-active"));
        for (int index = 0; index < 24; index++) { var item = book.AddFile(Path.Combine(directory, "later-" + index)); appeared.Add(item.Target); book.Poll(); }
        appeared.Add(original.Target); Check(book.Poll().Single().Id == original.Id && book.Snapshot.Any(item => item.Id == original.Id), "history eviction follows completion order, not start order");
    }
    private static void ProcessErrors()
    {
        WatchProcess? process = new(42, "fixture", 100); Exception? error = null;
        var book = new WatchBook(_ => false, _ => false, _ => { if (error is not null) throw error; return process!; });
        var entry = book.AddProcess(42); Reject(() => book.AddProcess(42), "duplicate");
        foreach (var failure in new Exception[] { new Win32Exception(5), new UnauthorizedAccessException("fixture denied"), new InvalidOperationException("fixture metadata unavailable"), new ArgumentException("fixture metadata argument failure") })
        {
            error = failure; Check(book.Poll().Count == 0 && !book.Snapshot.Single().Completed && book.Snapshot.Single().Error.Length > 0, "unreadable process metadata is not proof of exit: " + failure.GetType().Name);
        }
        error = null; Check(book.Poll().Count == 0 && book.Snapshot.Single().Error.Length == 0, "successful retry clears transient error");
        process = process with { StartedUtcTicks = 101 }; Check(book.Poll().Single().Id == entry.Id, "PID reuse completes the original identity");
        var replacement = book.AddProcess(42); process = null; Check(book.Poll().Single().Id == replacement.Id && book.Poll().Count == 0, "explicit missing process completes once"); Reject(() => book.AddProcess(42), "exited");
    }
    private static void RemovalRace(string directory)
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); bool poll = false;
        var book = new WatchBook(_ => { if (!poll) return false; entered.Set(); if (!release.Wait(5000)) throw new TimeoutException(); return true; }, _ => false, _ => null!);
        var entry = book.AddFile(Path.Combine(directory, "late")); poll = true; var work = Task.Run(() => book.Poll());
        try { Check(entered.Wait(3000), "delayed probe entered"); Check(book.Snapshot.Length == 1, "snapshot stays responsive during blocked filesystem probe"); Check(book.Remove(entry.Id)?.Id == entry.Id, "cancel removes active watch without waiting on IO"); }
        finally { release.Set(); }
        Check(work.GetAwaiter().GetResult().Count == 0 && book.Snapshot.Length == 0, "late completion cannot resurrect a canceled watch");
    }
    private static void Cancellation(string directory)
    {
        using var source = new CancellationTokenSource();
        var book = new WatchBook(_ => { source.Cancel(); return false; }, _ => false, _ => null!);
        bool canceled = false; try { book.AddFile(Path.Combine(directory, "cancel-add"), source.Token); } catch (OperationCanceledException) { canceled = true; }
        Check(canceled && book.Snapshot.Length == 0, "lifetime cancellation during a probe prevents late addition");
    }
    private static void SchedulerFairness(string directory)
    {
        using var enteredOne = new ManualResetEventSlim(); using var releaseOne = new ManualResetEventSlim();
        using var enteredTwo = new ManualResetEventSlim(); using var releaseTwo = new ManualResetEventSlim(); using var lifetime = new CancellationTokenSource();
        var blocked = new System.Collections.Concurrent.ConcurrentDictionary<string, (ManualResetEventSlim Entered, ManualResetEventSlim Release)>();
        var ready = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>();
        var calls = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        var reports = new System.Collections.Concurrent.ConcurrentQueue<WatchProbeResult>();
        var book = new WatchBook(path => { calls.AddOrUpdate(path, 1, (_, count) => count + 1); if (blocked.TryGetValue(path, out var wait)) { wait.Entered.Set(); if (!wait.Release.Wait(10000)) throw new TimeoutException("controlled watch probe"); return true; } return ready.ContainsKey(path); }, _ => false, _ => null);
        var scheduler = new WatchProbeScheduler(book, 2); scheduler.Probed += result => { if (result?.CompletedNow == true) reports.Enqueue(result); };
        var slow = book.AddFile(Path.Combine(directory, "slow-one")); blocked[slow.Target] = (enteredOne, releaseOne);
        var fast = book.AddFile(Path.Combine(directory, "fast-one")); ready[fast.Target] = true;
        Task first = scheduler.PollAsync(lifetime.Token), second = Task.CompletedTask;
        try
        {
            Check(enteredOne.Wait(4000) && SpinWait.SpinUntil(() => reports.Any(result => result.Entry.Id == fast.Id), 4000), "fast completion is reported while an unrelated system probe is blocked");
            Check(!first.IsCompleted && scheduler.InFlight == 1, "aggregate wait does not delay individual results");
            for (int index = 0; index < 20; index++) scheduler.PollAsync(lifetime.Token).GetAwaiter().GetResult();
            Check(calls[slow.Target] == 2 && scheduler.InFlight == 1, "repeated ticks never duplicate an in-flight target probe");
            book.Remove(slow.Id);
            var slowTwo = book.AddFile(Path.Combine(directory, "slow-two")); blocked[slowTwo.Target] = (enteredTwo, releaseTwo); second = scheduler.PollAsync(lifetime.Token);
            Check(enteredTwo.Wait(4000), "second bounded slot entered");
            var queued = book.AddFile(Path.Combine(directory, "queued-fast")); ready[queued.Target] = true;
            scheduler.PollAsync(lifetime.Token).GetAwaiter().GetResult();
            Check(scheduler.InFlight == 2 && scheduler.WaitingForCapacity == 1 && calls[queued.Target] == 1, "removed blocked probes keep their slots; new work reports capacity instead of launching more threads");
            releaseOne.Set(); Check(first.Wait(4000), "removed slow probe returned");
            scheduler.PollAsync(lifetime.Token).GetAwaiter().GetResult();
            Check(reports.Any(result => result.Entry.Id == queued.Id) && !reports.Any(result => result.Entry.Id == slow.Id), "queued work resumes after slot release; canceled target produces no completion");
            lifetime.Cancel(); releaseTwo.Set(); Check(second.Wait(4000) && scheduler.InFlight == 0 && !reports.Any(result => result.Entry.Id == slowTwo.Id), "lifetime cancellation retains slot until native return and suppresses late notifications");
        }
        finally { releaseOne.Set(); releaseTwo.Set(); Task.WhenAll(first, second).GetAwaiter().GetResult(); }
        calls.Clear(); book = new WatchBook(path => { calls.AddOrUpdate(path, 1, (_, count) => count + 1); return false; }, _ => false, _ => null);
        for (int index = 0; index < 6; index++) book.AddFile(Path.Combine(directory, "round-robin-" + index)); calls.Clear();
        scheduler = new(book, 2); for (int index = 0; index < 3; index++) scheduler.PollAsync().GetAwaiter().GetResult();
        Check(calls.Count == 6 && calls.Values.All(count => count == 1), "least-recently-checked scheduling prevents fast pending tasks starving later tasks");
    }
    public static void RunUi()
    {
        string directory = Path.Combine(Path.GetTempPath(), "side-island-watch-ui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var window = new IslandWindow(true, directory);
        try
        {
            window.Settings.Language = "en"; window.Show(); window.Expand(); window.SelectToolForTest("watch"); Flush(window);
            typeof(IslandWindow).GetMethod("EnsureNotifications", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            var notifications = Field<NotificationCenter>(window, "notificationCenter");
            notifications.Publish(new("side-island", "Side Island", "Synthetic source registration", "Own fixture", DateTimeOffset.UtcNow, Internal: true));
            notifications.SetApplicationEnabled("side-island", false);
            Check(notifications.Applications.Single(app => app.SourceId == "side-island").Enabled == false, "actual internal source is disabled before completion test");
            var book = Field<WatchBook>(window, "watchBook"); string target = Path.Combine(directory, "appear.txt");
            var path = Find<TextBox>(window, "watch-file"); path.Text = target; var add = Find<Button>(window, "watch-add-file"); Click(add); Wait(window, () => add.IsEnabled);
            Check(book.Snapshot.Length == 1 && Find<StackPanel>(window, "watch-rows").Children.Count == 1, "actual add handler renders running card");
            Click(add); Wait(window, () => add.IsEnabled); Check(book.Snapshot.Length == 1 && Field<string>(window, "status").Contains("already being watched", StringComparison.Ordinal), "duplicate handler gives localized feedback without extra task");
            var cancel = window.GetVisualDescendants().OfType<Button>().Single(button => button.Name?.StartsWith("watch-remove-", StringComparison.Ordinal) == true); Click(cancel); Check(book.Snapshot.Length == 0, "actual cancel button removes pending watch");
            Click(add); Wait(window, () => add.IsEnabled); File.WriteAllText(target, "UI fixture"); var polling = window.PollWatchBookAsync(); Wait(window, () => polling.IsCompleted); polling.GetAwaiter().GetResult();
            Check(book.Snapshot.Single().Completed && Find<Button>(window, "watch-clear-finished").IsEnabled, "file completion enables finished management");
            Check(notifications.Items.Count == 0 && Find<StackPanel>(window, "watch-rows").GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "Completed") && !Find<StackPanel>(window, "watch-rows").GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text?.Contains("notified", StringComparison.Ordinal) == true), "disabled notification source does not produce a false delivery claim");
            foreach (var language in new[] { "zh", "en" }) foreach (bool light in new[] { false, true }) foreach (int width in new[] { 320, 480, 760 })
            {
                window.Settings.Language = language; window.Settings.LightTheme = light; window.Width = width; window.SelectToolForTest("watch"); Flush(window);
                var rows = Find<StackPanel>(window, "watch-rows"); Check(rows.Children.Count == 1, "language/theme rebuild preserves watch state");
                foreach (var button in rows.GetVisualDescendants().OfType<Button>()) foreach (var caption in button.GetVisualDescendants().OfType<TextBlock>())
                    Check(caption.DesiredSize.Width <= caption.Bounds.Width + 1 && caption.DesiredSize.Height <= caption.Bounds.Height + 1, "populated watch action caption fits compact layout");
            }
            string firstCompleted = book.Snapshot.Single().Id, secondTarget = Path.Combine(directory, "second.txt");
            path = Find<TextBox>(window, "watch-file"); path.Text = secondTarget; add = Find<Button>(window, "watch-add-file"); Click(add); Wait(window, () => add.IsEnabled);
            File.WriteAllText(secondTarget, "second UI fixture"); polling = window.PollWatchBookAsync(); Wait(window, () => polling.IsCompleted); polling.GetAwaiter().GetResult();
            var activities = Field<TaskActivityHub>(window, "tasks"); activities.ClearFinished(); Check(activities.Snapshot().Count == 0, "completed activity history cleared independently of watch history");
            Click(Find<Button>(window, "watch-remove-" + firstCompleted)); Check(book.Snapshot.Length == 1 && book.Snapshot.Single().Target == secondTarget, "individual finished removal leaves other records intact");
            Check(activities.Snapshot().Count == 0, "removing an old finished watch does not recreate cleared activity history");
            Click(Find<Button>(window, "watch-clear-finished")); Check(book.Snapshot.Length == 0 && !Find<Button>(window, "watch-clear-finished").IsEnabled, "actual clear-finished action empties list");
            Check(activities.Snapshot().Count == 0, "clearing finished watch history does not recreate cleared activities");
            path = Find<TextBox>(window, "watch-file"); path.Text = target; add = Find<Button>(window, "watch-add-file"); Click(add); Wait(window, () => add.IsEnabled);
            Check(book.Snapshot.Length == 0 && Field<string>(window, "status").Contains("already exists", StringComparison.Ordinal), "existing files do not start retroactive alerts");
            var pid = Find<TextBox>(window, "watch-pid"); pid.Text = "not a pid"; add = Find<Button>(window, "watch-add-process"); Click(add); Wait(window, () => add.IsEnabled);
            Check(book.Snapshot.Length == 0 && Field<string>(window, "status").Contains("PID greater", StringComparison.Ordinal), "invalid process input stays contained and translated");
            AdditionGuardUi(directory); CompletionRemovalUi(directory);
            Console.WriteLine("PASS: actual watch add/duplicate/cancel/completion/clear controls, existing-file and PID feedback, populated zh/en light/dark compact layouts; no target process started or terminated.");
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); Directory.Delete(directory, true); }
    }
    private static void AdditionGuardUi(string directory)
    {
        foreach (bool closeBeforeReply in new[] { false, true })
        {
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); int calls = 0;
            var book = new WatchBook(_ => { Interlocked.Increment(ref calls); entered.Set(); if (!release.Wait(10000)) throw new TimeoutException("delayed watch addition"); return false; }, _ => false, _ => null);
            var window = new IslandWindow(true, Path.Combine(directory, "guard-" + closeBeforeReply));
            typeof(IslandWindow).GetField("watchBook", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, book);
            try
            {
                window.Settings.Language = "en"; window.Show(); window.Expand(); window.SelectToolForTest("watch"); Flush(window);
                Find<TextBox>(window, "watch-file").Text = Path.Combine(directory, "guard-target"); Click(Find<Button>(window, "watch-add-file"));
                Wait(window, () => entered.IsSet); Check(!Find<Button>(window, "watch-add-file").IsEnabled && !Find<Button>(window, "watch-add-process").IsEnabled, "one pending addition disables both add actions");
                window.SelectToolForTest("notes"); window.SelectToolForTest("watch"); Flush(window);
                var file = Find<Button>(window, "watch-add-file"); var process = Find<Button>(window, "watch-add-process");
                Check(!file.IsEnabled && !process.IsEnabled, "page reconstruction preserves global addition guard");
                Click(file); Click(process); Check(calls == 1, "navigation and repeated clicks cannot launch more blocked add probes");
                if (closeBeforeReply) window.Close(); release.Set(); Wait(window, () => !Field<bool>(window, "addingWatch"));
                Check(closeBeforeReply ? book.Snapshot.Length == 0 : book.Snapshot.Length == 1 && file.IsEnabled && process.IsEnabled, "late addition respects window closure or restores current-page controls");
            }
            finally { release.Set(); window.Close(); Dispatcher.UIThread.RunJobs(); }
        }
    }
    private static void CompletionRemovalUi(string directory)
    {
        foreach (string action in new[] { "remove", "clear", "evict" })
        {
            var window = new IslandWindow(true, Path.Combine(directory, "completion-" + action));
            try
            {
                window.Settings.Language = "en"; window.Show(); window.Expand(); window.SelectToolForTest("watch"); Flush(window);
                var book = Field<WatchBook>(window, "watchBook"); var tasks = Field<TaskActivityHub>(window, "tasks");
                typeof(IslandWindow).GetMethod("EnsureNotifications", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
                var notices = Field<NotificationCenter>(window, "notificationCenter");
                if (action == "clear")
                {
                    string seed = Path.Combine(directory, "clear-seed"); var item = book.AddFile(seed); File.WriteAllText(seed, "seed");
                    var completed = book.Probe(item.Id); typeof(IslandWindow).GetMethod("ApplyWatchProbe", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [completed]);
                    Check(Find<Button>(window, "watch-clear-finished").IsEnabled, "clear is genuinely available before racing completion");
                }
                string target = Path.Combine(directory, "completion-gap-" + action); Find<TextBox>(window, "watch-file").Text = target;
                var add = Find<Button>(window, "watch-add-file"); Click(add); Wait(window, () => add.IsEnabled);
                var entry = book.Snapshot.Single(item => !item.Completed); File.WriteAllText(target, "completed before UI callback"); var result = book.Probe(entry.Id);
                Check(result?.CompletedNow == true && tasks.Snapshot().Single(item => item.Id == entry.Id).State == "running", "controlled model/UI publication gap established");
                int beforeMessages = notices.Items.Count;
                Dispatcher.UIThread.Post(() => typeof(IslandWindow).GetMethod("ApplyWatchProbe", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [result]));
                if (action == "remove") Click(Find<Button>(window, "watch-remove-" + entry.Id));
                else if (action == "clear") Click(Find<Button>(window, "watch-clear-finished"));
                else
                {
                    for (int index = 0; index < WatchBook.MaximumFinished; index++)
                    {
                        string later = Path.Combine(directory, "eviction-" + index); var added = book.AddFile(later); File.WriteAllText(later, "later completion"); book.Probe(added.Id);
                    }
                }
                Check(!book.Snapshot.Any(item => item.Id == entry.Id), "completed row removed before queued callback: " + action);
                if (action != "evict") Check(tasks.Snapshot().Single(item => item.Id == entry.Id).State == "completed", "explicit removal settles terminal activity immediately");
                if (action != "evict") tasks.ClearFinished();
                Flush(window);
                Check((action == "evict" ? tasks.Snapshot().Single(item => item.Id == entry.Id).State == "completed" : !tasks.Snapshot().Any(item => item.Id == entry.Id)) && notices.Items.Count == beforeMessages, "late callback settles evicted running activity but never recreates cleared history or notification: " + action);
            }
            finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
        }
    }
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Flush(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
    private static void Wait(Window window, Func<bool> done) { var elapsed = Stopwatch.StartNew(); while (!done() && elapsed.Elapsed < TimeSpan.FromSeconds(10)) { Flush(window); Thread.Sleep(5); } Flush(window); Check(done(), "background watch action completed"); }
    private static void Reject(Action action, string code) { bool rejected = false; try { action(); } catch (WatchProblem problem) { rejected = problem.Code == code; } Check(rejected, "reject " + code); }
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException("Watch: " + message); }
}
