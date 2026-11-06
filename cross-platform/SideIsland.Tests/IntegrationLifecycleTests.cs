using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CodexIsland;
using SideIsland.Desktop;
using SideIsland.Desktop.Platforms;
using SideIsland.Core;

namespace SideIsland.Tests;

internal static class IntegrationLifecycleTests
{
    public static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "side-island-lifecycle-" + Guid.NewGuid().ToString("N"));
        var source = new DelayedSources(); var window = new IslandWindow(true, directory, source.Create());
        try
        {
            window.Settings.Slot1 = "music"; window.Show(); window.Expand(); AppearanceTestActions.Set(window, "en", window.Settings.LightTheme); window.SelectToolForTest("media"); Flush(window);
            var connect = FindButton(window, "Connect / refresh"); Click(connect);
            Check(source.Media.Count == 1, "media request started by real connect handler");
            Click(FindButton(window, "Disconnect")); source.Media[0].Complete(Media("late after disconnect")); Wait(window, () => connect.IsEnabled);
            bool mediaStayedDisconnected = Field<MediaSnapshot?>(window, "media") is null && !window.Settings.MediaEnabled;
            Console.WriteLine("OBSERVATION: media stayed disconnected after controlled late reply = " + mediaStayedDisconnected);
            window.SelectToolForTest("quota"); Flush(window); var quotaConnect = FindButton(window, "Connect / refresh"); Click(quotaConnect);
            Check(source.Quota.Count == 1 && source.Quota[0].Session.Reads.Count == 1, "quota request started by real connect handler");
            Click(FindButton(window, "Disconnect")); source.Quota[0].Session.Reads[0].Complete(Quota(20)); Wait(window, () => quotaConnect.IsEnabled);
            bool quotaStayedDisconnected = Field<UsageSnapshot?>(window, "quota") is null && !window.Settings.CodexEnabled;
            Console.WriteLine("OBSERVATION: quota stayed disconnected after controlled late reply = " + quotaStayedDisconnected);
            Check(mediaStayedDisconnected && quotaStayedDisconnected, "late media and quota replies must not restore disconnected sessions");
            Check(source.Media[0].Token.IsCancellationRequested, "disconnect cancels old media request");
            Check(source.Quota[0].Session.Disposed && source.Quota[0].Session.Reads[0].Token.IsCancellationRequested, "quota disconnect cancels and disposes its own session");
            MediaReconnect(window, source); QuotaReconnect(window, source); AsyncActionEligibility(window); ClosePending(window, source); DraftLifecycle(directory);
            Console.WriteLine("PASS: deterministic delayed media/quota replies, disconnect/reconnect, reverse completion, obsolete errors, controls during refresh, captured quota session, and close cancellation. Synthetic providers deliberately ignore cancellation; no native integration claim.");
        }
        finally { window.Close(); source.CompletePending(); Dispatcher.UIThread.RunJobs(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    private static void DraftLifecycle(string directory)
    {
        string noteDirectory = Path.Combine(directory, "note-close"), batchDirectory = Path.Combine(directory, "batch-input");
        var notesWindow = new IslandWindow(true, noteDirectory);
        try
        {
            notesWindow.Show(); notesWindow.Expand(); notesWindow.SelectToolForTest("notes"); Flush(notesWindow);
            var text = notesWindow.GetVisualDescendants().OfType<TextBox>().Single(box => box.AcceptsReturn); text.Text = "same-turn close 中文😀";
            notesWindow.Close();
            Check(new PreferenceStore(noteDirectory).Load().Notes == text.Text, "same-turn notes edit is persisted before window close");
            Dispatcher.UIThread.RunJobs(); Check(!Field<DispatcherTimer>(notesWindow, "saveDebounce").IsEnabled, "closed notes view cannot restart save debounce through a deferred text event");
        }
        finally { notesWindow.Close(); Dispatcher.UIThread.RunJobs(); }
        var batchWindow = new IslandWindow(true, batchDirectory);
        try
        {
            batchWindow.Show(); batchWindow.Expand(); AppearanceTestActions.Set(batchWindow, "en", batchWindow.Settings.LightTheme); batchWindow.SelectToolForTest("batch-files"); Flush(batchWindow);
            string original = Path.Combine(directory, "preview-only.txt"); File.WriteAllText(original, "synthetic preview fixture");
            using var engine = new PortableBatchFiles(batchDirectory); var plan = engine.Preview([original], new() { Template = "{name}_before" });
            typeof(IslandWindow).GetField("batchPlan", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(batchWindow, plan);
            batchWindow.SelectToolForTest("batch-files"); Flush(batchWindow);
            var template = batchWindow.GetVisualDescendants().OfType<TextBox>().Single(box => box.PlaceholderText == "Template: {name}_{n}");
            template.Text = "{name}_after";
            Check(Field<PortableRenamePlan?>(batchWindow, "batchPlan") is null, "same-turn batch input change invalidates previous rename plan before any click");
            Check(File.Exists(original) && !File.Exists(plan.Items[0].DestinationPath), "preview invalidation never renames the fixture file");
        }
        finally { batchWindow.Close(); Dispatcher.UIThread.RunJobs(); }
    }
    private static void MediaReconnect(IslandWindow window, DelayedSources source)
    {
        window.SelectToolForTest("media"); Flush(window);
        int first = source.Media.Count; var old = Invoke(window, "ConnectMedia"); Click(FindButton(window, "Disconnect")); var latest = Invoke(window, "ConnectMedia");
        Check(source.Media.Count == first + 2 && source.Media[first].Token.IsCancellationRequested, "reconnect starts immediately without waiting for canceled media provider");
        source.Media[first + 1].Complete(Media("new connection")); Wait(window, () => latest.IsCompleted); latest.GetAwaiter().GetResult();
        source.Media[first].Complete(Media("old connection")); Wait(window, () => old.IsCompleted); old.GetAwaiter().GetResult();
        Check(Field<MediaSnapshot>(window, "media").Title == "new connection", "new media connection wins reverse completion order");

        first = source.Media.Count; old = Invoke(window, "ConnectMedia"); latest = Invoke(window, "ConnectMedia");
        Check(Field<TextBlock>(window, "mediaTitle").Text == "Connecting to player…", "reconnect clears the previously visible track immediately");
        source.Media[first].Fail(new IOException("obsolete media fixture failure")); Wait(window, () => old.IsCompleted); old.GetAwaiter().GetResult();
        Check(Field<bool>(window, "readingMedia") && Field<MediaSnapshot?>(window, "media") is null && !Field<string>(window, "status").Contains("obsolete", StringComparison.Ordinal), "obsolete media finally/error cannot clear new request busy state or report old failure");
        source.Media[first + 1].Complete(Media("latest pending connection")); Wait(window, () => latest.IsCompleted);
        int commands = source.Commands.Count, reads = source.Media.Count; var command = Invoke(window, "MediaAction", "next");
        Click(FindButton(window, "Disconnect")); source.Commands[commands].Complete("obsolete control failure"); Wait(window, () => command.IsCompleted); command.GetAwaiter().GetResult();
        Check(source.Commands[commands].Token.IsCancellationRequested && source.Media.Count == reads && !Field<string>(window, "status").Contains("obsolete", StringComparison.Ordinal), "late disconnected control cannot restart metadata polling or report old error");
        Invoke(window, "MediaAction", "next").GetAwaiter().GetResult(); Check(source.Commands.Count == commands + 1, "disconnected control cannot launch a helper");

        first = source.Media.Count; old = Invoke(window, "ConnectMedia"); commands = source.Commands.Count; command = Invoke(window, "MediaAction", "next");
        source.Commands[commands].Complete(null); Wait(window, () => source.Media.Count == first + 2);
        Check(source.Media[first].Token.IsCancellationRequested, "control completion invalidates metadata requested before control");
        source.Media[first + 1].Complete(Media("after control")); Wait(window, () => command.IsCompleted); command.GetAwaiter().GetResult();
        source.Media[first].Complete(Media("before control")); Wait(window, () => old.IsCompleted); old.GetAwaiter().GetResult();
        Check(Field<MediaSnapshot>(window, "media").Title == "after control", "pre-control metadata cannot overwrite post-control track");
        Click(FindButton(window, "Disconnect"));
    }
    private static void QuotaReconnect(IslandWindow window, DelayedSources source)
    {
        window.SelectToolForTest("quota"); Flush(window);
        int first = source.Quota.Count; var old = Invoke(window, "ConnectQuota", "old-executable", "old-home"); var latest = Invoke(window, "ConnectQuota", "new-executable", "new-home");
        Check(source.Quota.Count == first + 2 && source.Quota[first].Executable == "old-executable" && source.Quota[first + 1].Executable == "new-executable" && source.Quota[first + 1].Home == "new-home", "each quota read captures the client created from its own connection settings");
        Check(source.Quota[first].Session.Disposed && source.Quota[first].Session.Reads[0].Token.IsCancellationRequested, "reconnect disposes old quota client without waiting for its result");
        source.Quota[first].Session.Reads[0].Fail(new IOException("obsolete quota failure")); Wait(window, () => old.IsCompleted); old.GetAwaiter().GetResult();
        Check(Field<bool>(window, "readingQuota") && Field<UsageSnapshot?>(window, "quota") is null && !Field<string>(window, "status").Contains("obsolete", StringComparison.Ordinal), "old quota finally/error cannot reset newer request");
        var expected = Quota(35); source.Quota[first + 1].Session.Reads[0].Complete(expected); Wait(window, () => latest.IsCompleted); latest.GetAwaiter().GetResult();
        Check(ReferenceEquals(Field<UsageSnapshot>(window, "quota"), expected), "latest quota session result is applied");

        first = source.Quota.Count; old = Invoke(window, "ConnectQuota", "third", "third-home"); latest = Invoke(window, "ConnectQuota", "fourth", "fourth-home");
        Check(!Field<TextBlock>(window, "quotaText").Text!.Contains("65%", StringComparison.Ordinal), "new quota connection clears visible values from prior settings");
        expected = Quota(75); source.Quota[first + 1].Session.Reads[0].Complete(expected); Wait(window, () => latest.IsCompleted);
        source.Quota[first].Session.Reads[0].Complete(Quota(5)); Wait(window, () => old.IsCompleted);
        Check(ReferenceEquals(Field<UsageSnapshot>(window, "quota"), expected), "older quota result cannot overwrite newer reply");
        Click(FindButton(window, "Disconnect"));
    }
    private static void AsyncActionEligibility(IslandWindow window)
    {
        bool available = true, resumed = false; int calls = 0;
        var release = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<Task> action = async () => { calls++; await release.Task; resumed = true; };
        var factory = typeof(IslandWindow).GetMethod("AsyncButton", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var button = (Button)factory.Invoke(window, ["fixture", action, new Func<bool>(() => available)])!;
        Click(button); Click(button); Check(calls == 1 && !button.IsEnabled, "disabled async action cannot start twice");
        available = false; release.SetResult(null); Wait(window, () => resumed);
        Check(!button.IsEnabled, "completion rechecks eligibility instead of enabling stale copy/save action");
        var disabled = (Button)factory.Invoke(window, ["fixture unavailable", new Func<Task>(() => { calls++; return Task.CompletedTask; }), new Func<bool>(() => false)])!;
        Click(disabled); Check(calls == 1 && !disabled.IsEnabled, "initially unavailable async action rejects programmatic clicks");
    }
    private static void ClosePending(IslandWindow window, DelayedSources source)
    {
        int mediaIndex = source.Media.Count, commandIndex = source.Commands.Count, quotaIndex = source.Quota.Count;
        var mediaRead = Invoke(window, "ConnectMedia"); var control = Invoke(window, "MediaAction", "play-pause"); var quotaRead = Invoke(window, "ConnectQuota", "close-executable", "close-home");
        string status = Field<string>(window, "status"); window.Close();
        Check(source.Media[mediaIndex].Token.IsCancellationRequested && source.Commands[commandIndex].Token.IsCancellationRequested && source.Quota[quotaIndex].Session.Reads[0].Token.IsCancellationRequested && source.Quota[quotaIndex].Session.Disposed, "closing cancels all integration operations and disposes the quota client");
        source.Media[mediaIndex].Complete(Media("reply after close")); source.Commands[commandIndex].Fail(new IOException("command after close")); source.Quota[quotaIndex].Session.Reads[0].Complete(Quota(1));
        Wait(window, () => mediaRead.IsCompleted && control.IsCompleted && quotaRead.IsCompleted);
        mediaRead.GetAwaiter().GetResult(); control.GetAwaiter().GetResult(); quotaRead.GetAwaiter().GetResult();
        Check(Field<MediaSnapshot?>(window, "media") is null && Field<UsageSnapshot?>(window, "quota") is null && Field<string>(window, "status") == status && source.Media.Count == mediaIndex + 1, "late close callbacks cannot mutate data/status or start a replacement read");
    }
    private sealed class Pending<T>(CancellationToken token)
    {
        private readonly TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; } = token;
        public Task<T> Task => completion.Task;
        public void Complete(T value) => completion.TrySetResult(value);
        public void Fail(Exception error) => completion.TrySetException(error);
    }
    private sealed class QuotaFixture : IQuotaSession
    {
        public readonly List<Pending<UsageSnapshot>> Reads = new();
        public bool Disposed { get; private set; }
        public Task<UsageSnapshot> ReadAsync(CancellationToken token) { var pending = new Pending<UsageSnapshot>(token); Reads.Add(pending); return pending.Task; }
        public void Dispose() => Disposed = true;
    }
    private sealed class DelayedSources
    {
        public readonly List<Pending<MediaSnapshot>> Media = new();
        public readonly List<Pending<string?>> Commands = new();
        public readonly List<(string Executable, string Home, QuotaFixture Session)> Quota = new();
        public IntegrationSources Create() => new(
            token => { var pending = new Pending<MediaSnapshot>(token); Media.Add(pending); return pending.Task; },
            (_, token) => { var pending = new Pending<string?>(token); Commands.Add(pending); return pending.Task; },
            (executable, home) => { var session = new QuotaFixture(); Quota.Add((executable, home, session)); return session; });
        public void CompletePending() { foreach (var item in Media) item.Complete(IntegrationLifecycleTests.Media("cleanup")); foreach (var item in Commands) item.Complete(null); foreach (var item in Quota) foreach (var read in item.Session.Reads) read.Complete(new()); }
    }
    private static MediaSnapshot Media(string title) => new(true, "fixture", title, "synthetic artist", null, 0, 120, true, null);
    private static UsageSnapshot Quota(double used) => new() { PlanType = "synthetic", Buckets = [new() { Primary = new() { UsedPercent = used }, Secondary = new() { UsedPercent = used } }] };
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static Task Invoke(object instance, string name, params object[] values) => (Task)instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, values)!;
    private static Button FindButton(Window window, string caption) => window.GetVisualDescendants().OfType<Button>().First(button => button.Content is TextBlock text && text.Text == caption);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Flush(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
    private static void Wait(Window window, Func<bool> done)
    {
        var elapsed = Stopwatch.StartNew(); while (!done() && elapsed.Elapsed < TimeSpan.FromSeconds(5)) { Flush(window); Thread.Sleep(2); }
        Flush(window); Check(done(), "controlled provider continuation completed before deadline");
    }
    private static void Check(bool condition, string text) { if (!condition) throw new InvalidOperationException("Integration lifecycle: " + text); }
}
