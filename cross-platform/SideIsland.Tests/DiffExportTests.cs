using System.Diagnostics;
using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SideIsland.Core;
using SideIsland.Desktop;
using SideIsland.Desktop.Platforms;
using CodexIsland;

namespace SideIsland.Tests;

internal static class DiffExportTests
{
    private static int checks;
    public static void Run(string? artifacts = null)
    {
        checks = 0; ObsoleteConfirmation(); PickerInputChanged(); FilesAndConfirmation(); PickerLifetime(); PreparationLifetime(); WriteLifetime(); PublicationOutcomes(); ClipboardLifetime();
        int layouts = Layouts(artifacts);
        Console.WriteLine($"PASS: {checks} actual diff export checks; {layouts} populated bilingual theme/size/DPI layouts. Revision-bound pickers, explicit observed-version overwrite confirmation, background prepare/save, global guards, cancellation, actual committed/unknown fault checkpoints and clipboard snapshots. Owned files and injected providers only; no native Mac/Linux or real clipboard claim.");
    }

    private static void ObsoleteConfirmation()
    {
        using var fixture = new Fixture(); var window = fixture.Window;
        string first = fixture.File("old-confirmation.patch"), second = fixture.File("new-confirmation.patch");
        System.IO.File.WriteAllText(first, "first original"); System.IO.File.WriteAllText(second, "second original");
        Task old = fixture.Begin(); var oldExportCancel = Find<Button>(window, "diff-export-cancel"); fixture.Pickers[^1].SetResult(first); fixture.WaitConfirmation();
        var oldReplace = Find<Button>(window, "diff-overwrite-confirm"); var oldCancel = Find<Button>(window, "diff-overwrite-cancel");
        window.SelectToolForTest("notes"); Wait(window, old); window.SelectToolForTest("diff"); fixture.Compare();
        Task current = fixture.Begin(); fixture.Pickers[^1].SetResult(second); fixture.WaitConfirmation();
        var decision = Field<TaskCompletionSource<bool>>(window, "diffOverwriteDecision");
        Check(!decision.Task.IsCompleted, "second target genuinely awaits its own confirmation");
        oldReplace.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Console.WriteLine("OBSERVATION: detached old Replace control resolved new destination confirmation=" + decision.Task.IsCompleted);
        Check(!decision.Task.IsCompleted, "an old detached Replace control cannot confirm a different current destination");
        oldCancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(!decision.Task.IsCompleted && !Field<CancellationTokenSource>(window, "diffExportCancellation").IsCancellationRequested, "an old detached Cancel control cannot cancel a new export");
        oldExportCancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(!decision.Task.IsCompleted && !Field<CancellationTokenSource>(window, "diffExportCancellation").IsCancellationRequested, "an old detached preparation Cancel control cannot cancel a new confirmation");
        Click(window, "diff-overwrite-cancel"); Wait(window, current);
        Check(System.IO.File.ReadAllText(first) == "first original" && System.IO.File.ReadAllText(second) == "second original", "only current confirmation controls can affect the selected target");
    }

    private static void FilesAndConfirmation()
    {
        using var fixture = new Fixture(); var window = fixture.Window;
        string expected = fixture.Patch, target = fixture.File("new patch 中文 😀.patch"); Task saving = fixture.Begin(); fixture.Pickers[^1].SetResult(target); Wait(window, saving);
        Check(System.IO.File.ReadAllBytes(target).SequenceEqual(new UTF8Encoding(false, true).GetBytes(expected)), "new export is exact complete UTF8 patch with no BOM");
        Check(Feedback(window).StartsWith("Diff patch saved.", StringComparison.Ordinal) && Feedback(window).Contains(target, StringComparison.Ordinal), "successful export names actual saved target");
        Check(fixture.PrepareThreads.All(id => id != fixture.UiThread) && fixture.SaveThreads.All(id => id != fixture.UiThread), "both actual preparation and publication execute off UI thread");

        byte[] original = Encoding.UTF8.GetBytes("existing user-selected fixture\r\n"); System.IO.File.WriteAllBytes(target, original);
        saving = fixture.Begin(); fixture.Pickers[^1].SetResult(target); fixture.WaitConfirmation();
        Check(System.IO.File.ReadAllBytes(target).SequenceEqual(original) && !saving.IsCompleted, "selecting existing target does not replace before explicit confirmation");
        Check(Find<TextBlock>(window, "diff-overwrite-path").Text == target && Find<TextBlock>(window, "diff-overwrite-size").Text!.Contains(original.Length.ToString(), StringComparison.Ordinal), "confirmation identifies observed target and size");
        Click(window, "diff-overwrite-cancel"); Wait(window, saving);
        Check(System.IO.File.ReadAllBytes(target).SequenceEqual(original), "cancel keeps existing file unchanged");

        saving = fixture.Begin(); fixture.Pickers[^1].SetResult(target); fixture.WaitConfirmation(); Click(window, "diff-overwrite-confirm"); Wait(window, saving);
        Check(System.IO.File.ReadAllText(target) == expected, "explicit confirmation preserves the intentional overwrite feature");

        saving = fixture.Begin(); fixture.Pickers[^1].SetResult(target); fixture.WaitConfirmation();
        System.IO.File.WriteAllText(target, "external version after observing"); Click(window, "diff-overwrite-confirm"); Wait(window, saving);
        Check(System.IO.File.ReadAllText(target) == "external version after observing" && Feedback(window).StartsWith("Patch not saved:", StringComparison.Ordinal), "changed destination is rejected rather than overwriting the unconfirmed newer version");
        Check(!Feedback(window).Contains("could not be confirmed", StringComparison.Ordinal), "precommit destination conflict is not mislabeled unknown publication");

        saving = fixture.Begin(); fixture.Pickers[^1].SetResult(target); fixture.WaitConfirmation(); System.IO.File.Delete(target); Click(window, "diff-overwrite-confirm"); Wait(window, saving);
        Check(!System.IO.File.Exists(target), "deleted observed file is not silently recreated by an old confirmation");
        saving = fixture.Begin(); fixture.Pickers[^1].SetResult(fixture.DirectoryPath); Wait(window, saving);
        Check(Feedback(window).StartsWith("Patch not saved:", StringComparison.Ordinal) && !Feedback(window).Contains("could not be confirmed", StringComparison.Ordinal), "read-only Prepare failure is not classified as uncertain write");
        saving = fixture.Begin(); fixture.Pickers[^1].SetResult(null); Wait(window, saving);
        Check(Feedback(window) == "" && Find<Button>(window, "diff-save").IsEnabled, "picker cancellation restores actions without an error or stale success");
        saving = fixture.Begin(); fixture.Pickers[^1].SetException(new IOException("owned picker error")); Wait(window, saving);
        Check(Feedback(window).StartsWith("Patch not saved:", StringComparison.Ordinal) && !Feedback(window).Contains("owned picker error", StringComparison.Ordinal), "picker failure is actionable and localized without raw exception text");
    }

    private static void PickerLifetime()
    {
        foreach (string change in new[] { "left", "right-restore", "compare", "page", "language", "theme", "cancel", "close" })
        {
            using var fixture = new Fixture(); var window = fixture.Window; Task saving = fixture.Begin(); string target = fixture.File("stale-" + change + ".patch");
            Mutate(fixture, change);
            if (change is "page" or "language" or "theme")
            {
                window.SelectToolForTest("diff"); fixture.Compare(false); int count = fixture.Pickers.Count; Click(window, "diff-save");
                Check(fixture.Pickers.Count == count && !Find<Button>(window, "diff-save").IsEnabled, "global export guard survives replacement controls: " + change);
            }
            fixture.Pickers[^1].SetResult(target); Wait(window, saving);
            Check(!System.IO.File.Exists(target) && fixture.PrepareCalls == 0 && fixture.SaveCalls == 0, "stale picker cannot inspect or publish after " + change);
            if (change == "close") Check(!window.IsVisible, "late picker callback does not reopen a closed window");
        }
        foreach (string change in new[] { "left", "page", "language", "theme", "close" })
        {
            using var fixture = new Fixture(); var window = fixture.Window; string target = fixture.File("confirmation-" + change + ".patch");
            System.IO.File.WriteAllText(target, "keep confirmed-version fixture"); Task saving = fixture.Begin(); fixture.Pickers[^1].SetResult(target); fixture.WaitConfirmation();
            var obsoleteReplace = Find<Button>(window, "diff-overwrite-confirm"); Mutate(fixture, change); obsoleteReplace.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Wait(window, saving);
            Check(System.IO.File.ReadAllText(target) == "keep confirmed-version fixture" && fixture.SaveCalls == 0, "detached or invalidated confirmation cannot publish: " + change);
        }
    }

    private static void PreparationLifetime()
    {
        foreach (bool createLateTarget in new[] { false, true })
        {
            using var fixture = new Fixture(); var window = fixture.Window; var entered = fixture.Gate(); var release = fixture.Gate();
            fixture.Preparer = (path, patch, token) =>
            {
                PortablePatchTarget target = PortablePatchFiles.Prepare(path, patch, token); entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException("Owned preparation gate blocked UI");
                return Task.FromResult(target); // Deliberately ignores cancellation after observing.
            };
            string destination = fixture.File("late-created.patch"); Task saving = fixture.Begin(); fixture.Pickers[^1].SetResult(destination); WaitUntil(window, () => entered.IsSet);
            try
            {
                var clock = Stopwatch.StartNew();
                if (createLateTarget) System.IO.File.WriteAllText(destination, "new entry created after observation");
                else { AppearanceTestActions.Set(window, "zh", true); window.SelectToolForTest("diff"); fixture.Compare(false); }
                Check(clock.ElapsedMilliseconds < 1200, "slow target inspection does not freeze actual appearance or comparison controls");
            }
            finally { release.Set(); }
            Wait(window, saving);
            Check(createLateTarget ? System.IO.File.ReadAllText(destination) == "new entry created after observation" : !System.IO.File.Exists(destination), "new entry collision or obsolete preparation cannot publish stale bytes");
            if (!createLateTarget) Check(fixture.SaveCalls == 0, "late uncancellable preparation is discarded before save begins");
        }
    }

    private static void WriteLifetime()
    {
        foreach (string change in new[] { "input", "theme", "cancel", "close" })
        {
            using var fixture = new Fixture(); var window = fixture.Window; var entered = fixture.Gate(); var release = fixture.Gate(); CancellationToken captured = default;
            fixture.Saver = (target, token) =>
            {
                captured = token; entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException("Owned save gate blocked UI");
                target.Save(token); return Task.CompletedTask;
            };
            string destination = fixture.File("cancel-before-commit-" + change + ".patch"); Task saving = fixture.Begin(); fixture.Pickers[^1].SetResult(destination); WaitUntil(window, () => entered.IsSet);
            try
            {
                var clock = Stopwatch.StartNew(); Mutate(fixture, change == "input" ? "left" : change);
                Check(clock.ElapsedMilliseconds < 1200 && captured.IsCancellationRequested, "slow save remains responsive and receives cancellation after " + change);
            }
            finally { release.Set(); }
            Wait(window, saving); Check(!System.IO.File.Exists(destination), "cancel before commit prevents file creation after " + change);
        }
        using (var fixture = new Fixture())
        {
            var window = fixture.Window; var entered = fixture.Gate(); var release = fixture.Gate(); string expected = fixture.Patch;
            fixture.Saver = (target, token) => { target.Save(token); entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException("Owned committed gate blocked UI"); return Task.CompletedTask; };
            string destination = fixture.File("committed-before-edit.patch"); Task saving = fixture.Begin(); fixture.Pickers[^1].SetResult(destination); WaitUntil(window, () => entered.IsSet);
            Find<TextBox>(window, "diff-right").Text = "edit after actual commit\n"; release.Set(); Wait(window, saving);
            Check(System.IO.File.ReadAllText(destination) == expected && Feedback(window).StartsWith("The earlier compared patch was saved", StringComparison.Ordinal), "post-commit edits cannot turn real success into cancellation or imply newer text was saved");
            Check(!Find<Button>(window, "diff-save").IsEnabled, "successful old export does not enable stale patch after editing");
        }
    }

    private static void PublicationOutcomes()
    {
        foreach (bool unknown in new[] { false, true })
        {
            using var fixture = new Fixture(); var window = fixture.Window; string target = fixture.File(unknown ? "unknown.patch" : "warning.patch");
            string previous = "exact previous observed patch\r\n"; System.IO.File.WriteAllText(target, previous); string expected = fixture.Patch;
            fixture.Preparer = (path, patch, token) => Task.FromResult(PortablePatchFiles.Prepare(path, patch, token, stage =>
            {
                if (stage != "committed") return;
                if (unknown) System.IO.File.WriteAllText(path, "owned writer after actual commit");
                throw new IOException("Owned post-commit inspection fault");
            }));
            Task saving = fixture.Begin(); fixture.Pickers[^1].SetResult(target); fixture.WaitConfirmation(); Click(window, "diff-overwrite-confirm"); Wait(window, saving);
            string backup = Field<string>(window, "diffExportBackup");
            Check(System.IO.File.Exists(backup) && System.IO.File.ReadAllText(backup) == previous, "actual post-commit fault retains exact observed original: " + unknown);
            Check(Feedback(window).Contains(target, StringComparison.Ordinal) && Feedback(window).Contains(backup, StringComparison.Ordinal), "post-commit feedback exposes actual target and retained-copy path");
            if (unknown)
                Check(System.IO.File.ReadAllText(target) == "owned writer after actual commit" && Feedback(window).StartsWith("The patch save result could not be confirmed", StringComparison.Ordinal), "real unverifiable destination is not mislabeled not-saved or success");
            else
                Check(System.IO.File.ReadAllText(target) == expected && Feedback(window).StartsWith("Patch saved;", StringComparison.Ordinal), "real confirmed publication survives a follow-up exception as saved warning");
        }
    }

    private static void ClipboardLifetime()
    {
        using var fixture = new Fixture(); var window = fixture.Window; string expected = fixture.Patch;
        var reply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); fixture.Copier = _ => reply.Task;
        fixture.Cleanup.Add(() => reply.TrySetResult());
        Click(window, "diff-copy"); Task copying = window.LatestDiffCopy;
        Check(fixture.Copied.Single() == expected, "copy receives the exact complete compared patch snapshot");
        Find<TextBox>(window, "diff-right").Text = "new input while copying\n"; window.SelectToolForTest("notes"); window.SelectToolForTest("diff"); fixture.Compare();
        Check(!Find<Button>(window, "diff-copy").IsEnabled, "copy guard survives input and page rebuilding"); Click(window, "diff-copy"); Check(fixture.Copied.Count == 1, "no overlapping clipboard writes from rebuilt controls");
        reply.SetException(new IOException("obsolete synthetic clipboard failure")); Wait(window, copying);
        Check(Find<Button>(window, "diff-copy").IsEnabled && !Field<string>(window, "status").Contains("obsolete", StringComparison.Ordinal), "old clipboard error cannot become a stale footer or disable current result");
        fixture.Copier = _ => Task.FromException(new IOException("current synthetic clipboard failure")); Click(window, "diff-copy"); Wait(window, window.LatestDiffCopy);
        Check(Field<string>(window, "status") == "Could not copy the diff. Try again.", "current clipboard failure is localized without exposing raw provider error");
    }

    private static int Layouts(string? artifacts)
    {
        using var fixture = new Fixture(); var window = fixture.Window; int count = 0;
        foreach (string language in new[] { "en", "zh" }) foreach (bool light in new[] { false, true })
        foreach (int width in new[] { 320, 480, 760 }) foreach (int height in new[] { 360, 720 }) foreach (double scale in new[] { 1d, 1.25, 1.5, 2 })
        {
            AppearanceTestActions.Set(window, language, light); window.SetRenderScaling(scale); window.Width = width; window.Height = height; Flush(window); fixture.Compare();
            string context = $"{language}/{light}/{width}/{height}/{scale}"; string target = fixture.File("layout-" + count + "-existing patch 中文 😀.patch"); System.IO.File.WriteAllText(target, "owned existing content");
            Task saving = fixture.Begin(); fixture.Pickers[^1].SetResult(target); fixture.WaitConfirmation();
            foreach (var names in new[] { new[] { "diff-copy", "diff-save" }, new[] { "diff-overwrite-confirm", "diff-overwrite-cancel" } })
            {
                var first = Find<Button>(window, names[0]); var second = Find<Button>(window, names[1]); var a = first.TranslatePoint(default, window); var b = second.TranslatePoint(default, window);
                Check(Math.Abs(first.Bounds.Width - second.Bounds.Width) <= 1 && Math.Abs(first.Bounds.Height - second.Bounds.Height) <= 1, "equal grouped action sizes " + context);
                Check(a is Point p && b is Point q && Math.Abs(p.Y - q.Y) <= 1 && Math.Abs(q.X - p.X - first.Bounds.Width - 6) <= 1, "compact aligned action row " + context);
                foreach (var button in new[] { first, second })
                {
                    var caption = (TextBlock)button.Content!; var point = button.TranslatePoint(default, window);
                    Check(caption.Bounds.Height > 0 && caption.Bounds.Height + button.Padding.Top + button.Padding.Bottom <= button.Bounds.Height + 2 && point is Point origin && origin.X >= -1 && origin.X + button.Bounds.Width <= width + 1, "wrapped caption is not clipped or horizontally outside viewport " + context);
                }
            }
            Check(Find<TextBlock>(window, "diff-overwrite-path").Text == target && Find<TextBlock>(window, "diff-overwrite-path").Bounds.Height > 0, "confirmation displays actual long Unicode path " + context);
            Check(((TextBlock)Find<Button>(window, "diff-overwrite-confirm").Content!).Text == (language == "en" ? "Replace this file" : "替换该文件"), "confirmation follows actual language change " + context);
            if (artifacts is not null && height == 720 && scale == 1 && (language == "en" && light && width == 320 || language == "zh" && !light && width == 480))
            {
                Find<Button>(window, "diff-overwrite-confirm").BringIntoView(); Flush(window); Directory.CreateDirectory(artifacts); AvaloniaHeadlessPlatform.ForceRenderTimerTick(3); Dispatcher.UIThread.RunJobs();
                using var bitmap = new RenderTargetBitmap(new(width, height), new(96, 96)); bitmap.Render(window); bitmap.Save(Path.Combine(artifacts, $"diff-export-{language}-{width}.png"), PngBitmapEncoderOptions.Default);
            }
            Click(window, "diff-overwrite-cancel"); Wait(window, saving);
            Check(System.IO.File.ReadAllText(target) == "owned existing content", "layout confirmation cancellation preserves target " + context); count++;
        }
        Check(count == 96, "complete populated diff export layout matrix"); return count;
    }

    private static void Mutate(Fixture fixture, string change)
    {
        var window = fixture.Window;
        switch (change)
        {
            case "left": Find<TextBox>(window, "diff-left").Text = "edited original\n"; break;
            case "right-restore": var right = Find<TextBox>(window, "diff-right"); string? before = right.Text; right.Text = "temporary edit\n"; right.Text = before; break;
            case "compare": fixture.Compare(false); break;
            case "page": window.SelectToolForTest("notes"); break;
            case "language": AppearanceTestActions.Set(window, "zh", window.Settings.LightTheme); break;
            case "theme": AppearanceTestActions.Set(window, window.Settings.Language, !window.Settings.LightTheme); break;
            case "cancel": Click(window, "diff-export-cancel"); break;
            case "close": window.Close(); break;
            default: throw new ArgumentOutOfRangeException(nameof(change));
        }
        Flush(window);
    }

    private static void PickerInputChanged()
    {
        using var fixture = new Fixture(); var window = fixture.Window;
        Task saving = fixture.Begin();
        Find<TextBox>(window, "diff-right").Text = "newer edit while picker is open\n";
        Check(!Find<Button>(window, "diff-copy").IsEnabled, "input really invalidates the previously compared patch before picker returns");
        string target = fixture.File("late-old-snapshot.patch"); fixture.Pickers[^1].SetResult(target); Wait(window, saving);
        bool written = System.IO.File.Exists(target);
        Console.WriteLine($"OBSERVATION: picker returned after actual input edit; stale patch written={written}; status={Find<TextBlock>(window, "diff-status").Text}");
        Check(!written, "a late picker must not save the invalidated old patch after input edits");
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "side-island-diff-export-" + Guid.NewGuid().ToString("N"));
        internal readonly List<TaskCompletionSource<string?>> Pickers = new();
        internal readonly List<ManualResetEventSlim> Gates = new();
        internal readonly List<int> PrepareThreads = new(), SaveThreads = new();
        internal readonly List<string> Copied = new();
        internal readonly List<Action> Cleanup = new();
        internal readonly int UiThread = Environment.CurrentManagedThreadId;
        internal Func<string, string, CancellationToken, Task<PortablePatchTarget>>? Preparer;
        internal Func<PortablePatchTarget, CancellationToken, Task>? Saver;
        internal Func<string, Task>? Copier;
        internal int PrepareCalls, SaveCalls;
        internal readonly IslandWindow Window;
        internal Fixture()
        {
            Directory.CreateDirectory(DirectoryPath); new PreferenceStore(DirectoryPath).Save(new() { Language = "en", LastTool = "diff" });
            var sources = new IntegrationSources(_ => Task.FromResult(new MediaSnapshot(false, "", "", "", null, null, null, false, null)), (_, _) => Task.FromResult<string?>(null), (_, _) => throw new InvalidOperationException("No quota access in diff fixture"))
            {
                ChooseDiffDestination = _ => { var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously); Pickers.Add(pending); return pending.Task; },
                PrepareDiffPatch = (path, patch, token) => { Interlocked.Increment(ref PrepareCalls); PrepareThreads.Add(Environment.CurrentManagedThreadId); return Preparer is { } prepare ? prepare(path, patch, token) : Task.FromResult(PortablePatchFiles.Prepare(path, patch, token)); },
                SaveDiffPatch = (target, token) => { Interlocked.Increment(ref SaveCalls); SaveThreads.Add(Environment.CurrentManagedThreadId); if (Saver is { } save) return save(target, token); target.Save(token); return Task.CompletedTask; },
                WriteDiffText = value => { Copied.Add(value); return Copier?.Invoke(value) ?? Task.CompletedTask; }
            };
            Window = new IslandWindow(true, DirectoryPath, sources); Window.Show(); Window.Expand(); Window.SelectToolForTest("diff"); Flush(Window);
            Find<TextBox>(Window, "diff-left").Text = "old 中文 😀\n"; Find<TextBox>(Window, "diff-right").Text = "new 中文 😀\n"; Compare();
        }
        internal string File(string name) => Path.Combine(DirectoryPath, name);
        internal string Patch => TextDiff.ExportUnified(Find<TextBox>(Window, "diff-left").Text!, Find<TextBox>(Window, "diff-right").Text!, CancellationToken.None);
        internal ManualResetEventSlim Gate() { var gate = new ManualResetEventSlim(false); Gates.Add(gate); return gate; }
        internal void WaitConfirmation()
        {
            WaitUntil(Window, () => Find<Border>(Window, "diff-overwrite-card").IsVisible || Window.LatestDiffExport.IsCompleted);
            Check(Find<Border>(Window, "diff-overwrite-card").IsVisible && !Window.LatestDiffExport.IsCompleted, "existing file reaches actual pending overwrite confirmation: " + Feedback(Window));
        }
        internal void Compare(bool exportAvailable = true)
        {
            var button = Find<Button>(Window, "diff-compare"); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); WaitUntil(Window, () => button.IsEnabled);
            Check(Find<Button>(Window, "diff-save").IsEnabled == exportAvailable, "actual comparison creates a patch with expected export guard");
        }
        internal Task Begin()
        {
            var save = Find<Button>(Window, "diff-save"); Check(save.IsEnabled, "actual export action is available"); int count = Pickers.Count;
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Check(Pickers.Count == count + 1, "export opens exactly one injected picker"); return Window.LatestDiffExport;
        }
        public void Dispose()
        {
            foreach (var release in Cleanup) release(); foreach (var gate in Gates) gate.Set(); foreach (var picker in Pickers) picker.TrySetResult(null);
            var cancellation = Field<CancellationTokenSource?>(Window, "diffExportCancellation"); cancellation?.Cancel();
            Wait(Window, Window.LatestDiffExport); Wait(Window, Window.LatestDiffCopy); Window.Close(); Flush(Window); foreach (var gate in Gates) gate.Dispose();
            string full = Path.GetFullPath(DirectoryPath), parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(Path.GetDirectoryName(full), parent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("side-island-diff-export-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected diff fixture cleanup path");
            Directory.Delete(full, true);
        }
    }
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static string Feedback(Window window) => Find<TextBlock>(window, "diff-export-feedback").Text ?? "";
    private static void Click(Window window, string name) => Find<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Flush(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
    private static void Wait(Window window, Task task) { WaitUntil(window, () => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void WaitUntil(Window window, Func<bool> done)
    {
        var timer = Stopwatch.StartNew(); while (!done() && timer.ElapsedMilliseconds < 15000) { Flush(window); Thread.Sleep(2); }
        if (!done()) throw new TimeoutException("Diff export fixture did not finish"); Flush(window);
    }
    private static void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException("Diff export: " + message); }
}
