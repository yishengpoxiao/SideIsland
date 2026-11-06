using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SideIsland.Desktop;
using SideIsland.Desktop.Platforms;

namespace SideIsland.Tests;

internal static class PowerToolTests
{
    public static void Run() => HelperLifecycle().GetAwaiter().GetResult();
    public static bool TryRunHelper(string[] args, out int exitCode)
    {
        exitCode = 0; if (args.Length != 4 || args[0] != "--power-helper") return false;
        File.WriteAllText(args[3], Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (args[1] == "early") { Console.Error.WriteLine("Synthetic early helper exit"); exitCode = 5; return true; }
        if (args[1] != "wait") { exitCode = 2; return true; }
        Thread.Sleep(int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) * 1000); return true;
    }
    private static async Task HelperLifecycle()
    {
        string directory = Path.Combine(Path.GetTempPath(), "side-island-power-helper-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var modes = new Queue<string>(["wait", "early", "wait", "wait"]); var markers = new List<string>();
        AwakeCommand Resolve(int seconds)
        {
            string marker = Path.Combine(directory, "owned-" + markers.Count + ".pid"); markers.Add(marker);
            string executable = Environment.ProcessPath ?? throw new InvalidOperationException("No own executable");
            string[] arguments = ["--power-helper", modes.Count > 0 ? modes.Dequeue() : "wait", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), marker];
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) arguments = [Assembly.GetEntryAssembly()!.Location, .. arguments];
            return new(executable, arguments);
        }
        try
        {
            using (var service = new CommandPlatformService(Resolve))
            {
                Check(await service.SetAwakeAsync(true, TimeSpan.FromSeconds(4), default) is null && service.AwakeState.Active, "real owned helper becomes active after startup confirmation");
                var first = service.AwakeState; DateTime launched = Field<Process>(service, "awakeProcess").StartTime.ToUniversalTime();
                Check(first.UntilUtc == launched.AddSeconds(4), "helper deadline uses actual process launch time, without a scheduling-sensitive wall-clock threshold");
                Check(await service.SetAwakeAsync(true, TimeSpan.FromSeconds(4), default) is not null && service.AwakeState.Active && service.AwakeState.UntilUtc == first.UntilUtc, "failed replacement preserves the previous helper and deadline");
                using (var cancel = new CancellationTokenSource(50)) await ExpectCancelled(service.SetAwakeAsync(true, TimeSpan.FromSeconds(4), cancel.Token), "canceled startup");
                Check(service.AwakeState.Active && service.AwakeState.UntilUtc == first.UntilUtc, "canceled startup never stops the pre-existing owned helper");
                Check(await service.SetAwakeAsync(true, TimeSpan.FromSeconds(4), default) is null && service.AwakeState.Active && service.AwakeState.UntilUtc > first.UntilUtc, "successful replacement installs the new helper");
                Check(await service.SetAwakeAsync(false, TimeSpan.Zero, default) is null && !service.AwakeState.Active && service.AwakeState.UntilUtc == default, "explicit stop clears helper identity and deadline");
                await WaitUntil(() => OwnedCount(service) == 0, "stopped helper readers/process handles released");
            }
            using (var service = new CommandPlatformService(Resolve))
            {
                var starting = service.SetAwakeAsync(true, TimeSpan.FromSeconds(4), default);
                await WaitUntil(() => OwnedCount(service) > 0, "owned startup helper created"); service.Dispose();
                await ExpectCancelled(starting, "dispose during startup"); await WaitUntil(() => OwnedCount(service) == 0, "dispose kills and releases only owned startup helper");
                Check(!service.AwakeState.Active, "disposed startup cannot hand off a live helper");
            }
            using (var service = new CommandPlatformService())
            {
                var awakeGate = Field<SemaphoreSlim>(service, "awakeChanges"); var volumeGate = Field<SemaphoreSlim>(service, "volumeChanges"); await awakeGate.WaitAsync(); await volumeGate.WaitAsync();
                try
                {
                    var stop = service.SetAwakeAsync(false, TimeSpan.Zero, default); var read = service.ReadVolumeAsync(default); var write = service.SetVolumeAsync(35, true, default);
                    service.Dispose(); await ExpectCancelled(stop, "disposed queued awake request"); await ExpectCancelled(read, "disposed queued volume read"); await ExpectCancelled(write, "disposed queued volume write");
                }
                finally { awakeGate.Release(); volumeGate.Release(); }
            }
            Console.WriteLine("PASS: real test-owned helper startup/early exit/cancel/replace/stop/dispose, launch-based deadline and service-disposal cancellation of queued volume/awake requests. Helpers only sleep/write their own PID fixture; no native awake/volume command executed.");
        }
        finally { Directory.Delete(directory, true); }
    }
    private static int OwnedCount(CommandPlatformService service) { lock (Field<object>(service, "gate")) return Field<HashSet<Process>>(service, "children").Count; }
    private static async Task WaitUntil(Func<bool> done, string message)
    { var elapsed = Stopwatch.StartNew(); while (!done() && elapsed.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10); Check(done(), message); }
    private static async Task ExpectCancelled(Task task, string message)
    { bool canceled = false; try { await task.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { canceled = true; } Check(canceled, message); }
    public static void RunUi()
    {
        var failures = new List<Exception>();
        foreach (Action test in new Action[] { VolumeReadPreservesEdits, AwakeStopSupersedesStart, AwakeDeadlineIsAuthoritative, VolumePageAndWriteGuard, VolumeClose, AwakeCloseAndReverseReplies })
            try { test(); } catch (Exception error) { Console.WriteLine("OBSERVATION FAIL: " + error.Message); failures.Add(error); }
        if (failures.Count > 0) throw new AggregateException("Power tool UI lifecycle failures", failures);
        Console.WriteLine("PASS: delayed real volume/awake handlers preserve edited values, stop supersedes old start, and countdown uses provider deadline. Synthetic providers only; system volume and sleep policy unchanged.");
    }
    private static void VolumeReadPreservesEdits()
    {
        using var fixture = new Fixture("volume"); var window = fixture.Window; var source = fixture.Source;
        var read = FindButton(window, "Read volume"); var slider = window.GetVisualDescendants().OfType<Slider>().Single(); var mute = window.GetVisualDescendants().OfType<CheckBox>().Single();
        Click(read); Check(source.Reads.Count == 1, "volume read was explicitly requested"); slider.Value = 82; mute.IsChecked = true;
        source.Reads[0].Reply.SetResult(new(true, 20, false, null)); Wait(window, () => read.IsEnabled);
        Console.WriteLine("OBSERVATION: volume after user edit and older read = " + slider.Value + ", muted=" + mute.IsChecked);
        Check(slider.Value == 82 && mute.IsChecked == true, "late volume read must not replace newer slider/mute edits");
        Check(source.Reads[0].Token.IsCancellationRequested, "editing cancels obsolete read work");
        Click(read); source.Reads[1].Reply.SetResult(new(true, 56, false, null)); Wait(window, () => read.IsEnabled);
        Check(slider.Value == 56 && mute.IsChecked == false, "a fresh explicitly requested read still updates both controls");
    }
    private static void AwakeStopSupersedesStart()
    {
        using var fixture = new Fixture("awake"); var window = fixture.Window; var source = fixture.Source;
        var start = FindButton(window, "30 min"); var stop = FindButton(window, "Stop keeping awake");
        Click(start); Click(stop); Check(source.Awake.Count == 2 && !source.Awake[1].Enabled, "start and explicit stop requests captured");
        source.Awake[1].Pending.Reply.SetResult(null); Wait(window, () => stop.IsEnabled);
        source.Awake[0].Pending.Reply.SetResult(null); Wait(window, () => start.IsEnabled);
        DateTime until = Field<DateTime>(window, "awakeUntil"); Console.WriteLine("OBSERVATION: countdown after stop then stale start reply = " + until.ToString("o"));
        Check(until == default, "late start completion must not restart stopped countdown");
        Check(source.Awake[0].Pending.Token.IsCancellationRequested, "stop cancels old startup request");
    }
    private static void AwakeDeadlineIsAuthoritative()
    {
        using var fixture = new Fixture("awake"); var window = fixture.Window; var source = fixture.Source;
        var expected = DateTime.UtcNow.AddMinutes(12); source.State = new(true, expected, null);
        var start = FindButton(window, "60 min"); Click(start); source.Awake[0].Pending.Reply.SetResult(null); Wait(window, () => start.IsEnabled);
        DateTime actual = Field<DateTime>(window, "awakeUntil"); Console.WriteLine("OBSERVATION: actual deadline=" + actual.ToString("o") + "; provider=" + expected.ToString("o"));
        Check(actual == expected, "countdown must use active helper deadline, not restart selected duration after await");
    }
    private static void VolumePageAndWriteGuard()
    {
        using var fixture = new Fixture("volume"); var window = fixture.Window; var source = fixture.Source;
        var slider = window.GetVisualDescendants().OfType<Slider>().Single(); slider.Value = 73;
        var read = FindButton(window, "Read volume"); Click(read); var apply = FindButton(window, "Apply"); Click(apply);
        Check(source.Writes.Count == 1 && source.Writes[0].Percent == 73 && source.Reads[0].Token.IsCancellationRequested, "apply captures selected value and invalidates old read");
        source.Reads[0].Reply.SetResult(new(true, 10, true, null)); Wait(window, () => read.IsEnabled); Check(slider.Value == 73, "late pre-write read cannot change selected value");
        window.SelectToolForTest("notes"); window.SelectToolForTest("volume"); Flush(window);
        AppearanceTestActions.Set(window, "zh", !window.Settings.LightTheme); AppearanceTestActions.Set(window, "en", !window.Settings.LightTheme);
        var replacementApply = FindButton(window, "Apply"); Check(!replacementApply.IsEnabled, "pending write guard survives page reconstruction"); Click(replacementApply); Check(source.Writes.Count == 1, "navigation cannot enqueue additional writes while one is pending");
        source.Writes[0].Pending.Reply.SetResult(null); Wait(window, () => replacementApply.IsEnabled);
        var current = window.GetVisualDescendants().OfType<Slider>().Single(); current.Value = 41; read = FindButton(window, "Read volume"); Click(read);
        window.SelectToolForTest("notes"); source.Reads[1].Reply.SetResult(new(false, 0, false, "obsolete-volume-error")); Wait(window, () => read.IsEnabled);
        Check(source.Reads[1].Token.IsCancellationRequested && !Field<string>(window, "status").Contains("obsolete-volume-error", StringComparison.Ordinal), "detached read is canceled and cannot overwrite current-page status");
    }
    private static void VolumeClose()
    {
        using var fixture = new Fixture("volume"); var window = fixture.Window; var source = fixture.Source;
        Click(FindButton(window, "Apply")); Check(source.Writes.Count == 1, "volume write started explicitly");
        window.Close(); string status = Field<string>(window, "status"); source.Writes[0].Pending.Reply.SetResult("late-volume-failure");
        Wait(window, () => !Field<bool>(window, "volumeApplying"));
        Check(source.Writes[0].Pending.Token.IsCancellationRequested && Field<string>(window, "status") == status, "closed volume write is canceled and cannot publish late errors");
    }
    private static void AwakeCloseAndReverseReplies()
    {
        using var fixture = new Fixture("awake"); var window = fixture.Window; var source = fixture.Source;
        var older = window.ChangeAwakeAsync(true, TimeSpan.FromMinutes(30)); var newer = window.ChangeAwakeAsync(true, TimeSpan.FromMinutes(120));
        source.State = new(true, DateTime.UtcNow.AddMinutes(120), null); source.Awake[1].Pending.Reply.SetResult(null); Wait(window, () => newer.IsCompleted); newer.GetAwaiter().GetResult();
        DateTime correct = Field<DateTime>(window, "awakeUntil"); source.Awake[0].Pending.Reply.SetResult("obsolete-awake-error"); Wait(window, () => older.IsCompleted); older.GetAwaiter().GetResult();
        Check(Field<DateTime>(window, "awakeUntil") == correct && !Field<string>(window, "status").Contains("obsolete-awake-error", StringComparison.Ordinal), "newer awake request wins reverse success/error completion order");
        var pending = window.ChangeAwakeAsync(true, TimeSpan.FromMinutes(60)); string status = Field<string>(window, "status"); window.Close();
        source.Awake[2].Pending.Reply.SetResult(null); Wait(window, () => pending.IsCompleted); pending.GetAwaiter().GetResult();
        Check(source.Awake[2].Pending.Token.IsCancellationRequested && Field<DateTime>(window, "awakeUntil") == correct && Field<string>(window, "status") == status, "closing cancels awake work and discards late countdown/status replies");
    }
    private sealed class Pending<T>(CancellationToken token)
    {
        internal readonly CancellationToken Token = token;
        internal readonly TaskCompletionSource<T> Reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class DelayedPower
    {
        internal readonly List<Pending<VolumeSnapshot>> Reads = [];
        internal readonly List<(int Percent, bool Muted, Pending<string?> Pending)> Writes = [];
        internal readonly List<(bool Enabled, TimeSpan Duration, Pending<string?> Pending)> Awake = [];
        internal AwakeSnapshot State = new(false, default, null);
        internal IntegrationSources Sources() => new(_ => throw new InvalidOperationException("Unexpected media fixture call"), (_, _) => throw new InvalidOperationException("Unexpected media fixture call"), (_, _) => throw new InvalidOperationException("Unexpected quota fixture call"))
        {
            ReadVolume = token => { var pending = new Pending<VolumeSnapshot>(token); Reads.Add(pending); return pending.Reply.Task; },
            SetVolume = (percent, muted, token) => { var pending = new Pending<string?>(token); Writes.Add((percent, muted, pending)); return pending.Reply.Task; },
            SetAwake = (enabled, duration, token) => { var pending = new Pending<string?>(token); Awake.Add((enabled, duration, pending)); return pending.Reply.Task; },
            ReadAwake = () => State
        };
        internal void CompletePending()
        {
            foreach (var read in Reads) read.Reply.TrySetResult(new(false, 0, false, "Fixture closed"));
            foreach (var write in Writes) write.Pending.Reply.TrySetResult(null);
            foreach (var request in Awake) request.Pending.Reply.TrySetResult(null);
        }
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly DelayedPower Source = new();
        internal readonly IslandWindow Window;
        private readonly string directory = Path.Combine(Path.GetTempPath(), "side-island-power-ui-" + Guid.NewGuid().ToString("N"));
        internal Fixture(string tool)
        {
            Window = new(true, directory, Source.Sources()); Window.Show(); Window.Expand(); AppearanceTestActions.Set(Window, "en", Window.Settings.LightTheme); Window.SelectToolForTest(tool); Flush(Window);
        }
        public void Dispose() { Window.Close(); Source.CompletePending(); Dispatcher.UIThread.RunJobs(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static Button FindButton(Window window, string caption) => window.GetVisualDescendants().OfType<Button>().Single(button => button.Content is TextBlock text && text.Text == caption);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Flush(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
    private static void Wait(Window window, Func<bool> done) { var elapsed = Stopwatch.StartNew(); while (!done() && elapsed.Elapsed < TimeSpan.FromSeconds(10)) { Flush(window); Thread.Sleep(5); } Flush(window); Check(done(), "controlled power request completed"); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("Power tools: " + message); }
}
