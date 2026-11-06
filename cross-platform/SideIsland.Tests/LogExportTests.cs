using System.Diagnostics;
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

namespace SideIsland.Tests;

internal static class LogExportTests
{
    private static int checks;
    public static void Run(string? artifacts = null)
    {
        checks = 0;
        SnapshotAndFiles(); PickerLifetime(); WriteLifetime(); PublicationOutcomes();
        int layouts = Layouts(artifacts);
        Console.WriteLine($"PASS: {checks} log export checks, {layouts} populated bilingual theme/size/DPI layouts; actual Save shown controls, bounded filtered UTF8 snapshots, source/existing-file protection, late picker rejection, background cancellation and truthful committed/unknown outcomes. Owned fixtures only; native Mac/Linux pickers not exercised.");
    }

    private static void SnapshotAndFiles()
    {
        using var fixture = new Fixture(false); var window = fixture.Window;
        Check(!Find<Button>(window, "log-save").IsEnabled, "empty view cannot be saved");
        fixture.Start(); Find<TextBox>(window, "log-search").Text = "ERROR";
        string captured = Output(window); Task saving = fixture.Begin();
        int pickers = fixture.Pickers.Count; Click(window, "log-save");
        Check(fixture.Pickers.Count == pickers && !Find<Button>(window, "log-save").IsEnabled, "repeated click has one global picker");
        Check(fixture.Suggestions.Single().StartsWith("log-shown-", StringComparison.Ordinal) && fixture.Suggestions.Single().EndsWith(".txt", StringComparison.Ordinal), "snapshot suggests a safe text filename");
        File.AppendAllText(fixture.Source, "ERROR after click\n", new UTF8Encoding(false)); Wait(window, window.PollLogAsync());
        Check(Output(window).Contains("after click", StringComparison.Ordinal), "new log lines really arrive while picker is pending");
        string destination = fixture.File("filtered snapshot 中文.txt"); fixture.Pickers[^1].SetResult(destination); Wait(window, saving);
        Check(File.ReadAllBytes(destination).SequenceEqual(new UTF8Encoding(false, true).GetBytes(captured)), "saved file is exact click-time filtered snapshot without BOM or added headers");
        Check(!File.ReadAllText(destination).Contains("ready", StringComparison.Ordinal) && !File.ReadAllText(destination).Contains("after click", StringComparison.Ordinal), "hidden and later lines are excluded");
        Check(Feedback(window).Contains("snapshot captured at", StringComparison.Ordinal) && Feedback(window).Contains(destination, StringComparison.Ordinal), "success explicitly identifies the earlier capture and target");

        byte[] before = File.ReadAllBytes(destination); saving = fixture.Begin(); fixture.Pickers[^1].SetResult(destination); Wait(window, saving);
        Check(File.ReadAllBytes(destination).SequenceEqual(before) && Feedback(window).StartsWith("Not saved", StringComparison.Ordinal), "existing destination retained with actionable failure");
        byte[] source = File.ReadAllBytes(fixture.Source); saving = fixture.Begin(); fixture.Pickers[^1].SetResult(fixture.Source); Wait(window, saving);
        Check(File.ReadAllBytes(fixture.Source).SequenceEqual(source) && Feedback(window).Contains("source log path", StringComparison.Ordinal), "source path is protected");
        File.Delete(fixture.Source); saving = fixture.Begin(); fixture.Pickers[^1].SetResult(fixture.Source); Wait(window, saving);
        Check(!File.Exists(fixture.Source) && Feedback(window).Contains("source log path", StringComparison.Ordinal), "deleted source path still cannot become an export destination");
        File.WriteAllBytes(fixture.Source, source);
        saving = fixture.Begin(); fixture.Pickers[^1].SetResult(null); Wait(window, saving);
        Check(Feedback(window) == "" && Find<Button>(window, "log-save").IsEnabled, "picker cancel restores controls without false success");
        saving = fixture.Begin(); fixture.Pickers[^1].SetException(new IOException("owned picker failure")); Wait(window, saving);
        Check(Feedback(window).StartsWith("Not saved", StringComparison.Ordinal), "picker failure cannot leave a permanent saving label");

        File.WriteAllText(fixture.Source, string.Join('\n', Enumerable.Range(0, 1400).Select(index => $"{index:0000} bounded 中文 😀")) + "\n", new UTF8Encoding(false));
        Find<TextBox>(window, "log-search").Text = ""; Click(window, "log-start"); Wait(window, window.LatestLogPoll);
        string bounded = Output(window); Check(bounded.Split(Environment.NewLine).Length <= 1000 && !bounded.Contains("0000 bounded", StringComparison.Ordinal), "actual visible log history is bounded before export");
        saving = fixture.Begin(); string boundedPath = fixture.File("bounded.txt"); fixture.Pickers[^1].SetResult(boundedPath); Wait(window, saving);
        Check(File.ReadAllText(boundedPath) == bounded && new FileInfo(boundedPath).Length < new FileInfo(fixture.Source).Length, "export writes shown bounded tail instead of rereading entire source");
    }

    private static void PickerLifetime()
    {
        foreach (string change in new[] { "path", "path-restore", "filter", "filter-restore", "keywords", "clear", "restart", "page", "language", "theme", "close" })
        {
            using var fixture = new Fixture(); var window = fixture.Window; Task saving = fixture.Begin();
            string destination = fixture.File("stale-" + change + ".txt"); Mutate(fixture, change);
            if (change is "page" or "language" or "theme")
            {
                window.SelectToolForTest("logs"); Flush(window); int calls = fixture.Pickers.Count;
                Check(!Find<Button>(window, "log-save").IsEnabled, "rebuild retains pending guard: " + change); Click(window, "log-save");
                Check(fixture.Pickers.Count == calls, "rebuild cannot start a duplicate save: " + change);
            }
            fixture.Pickers[^1].SetResult(destination); Wait(window, saving);
            Check(!File.Exists(destination) && fixture.WriteCalls == 0, "late picker does not export after " + change);
            if (change == "close") Check(!window.IsVisible, "late picker never reopens closed window");
        }
    }

    private static void WriteLifetime()
    {
        foreach (string change in new[] { "path", "filter", "clear", "restart", "page", "language", "theme", "close" })
        {
            using var fixture = new Fixture(); var window = fixture.Window;
            var release = new ManualResetEventSlim(); fixture.Cleanup.Add(release.Set); fixture.Owned.Add(release);
            var staged = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Saver = (destination, bytes, token) => Task.Run(() => PortableFilePublication.WriteNew(destination, stream =>
            {
                stream.Write(bytes); staged.TrySetResult(token);
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Owned log staging fixture timed out");
            }, token));
            Task saving = fixture.Begin(); string destination = fixture.File("cancel-" + change + ".txt"); fixture.Pickers[^1].SetResult(destination);
            Wait(window, staged.Task); Mutate(fixture, change);
            Check(staged.Task.Result.IsCancellationRequested, "changing view cancels staged publication: " + change);
            if (change is "page" or "language" or "theme")
            {
                window.SelectToolForTest("logs"); Flush(window); Check(!Find<Button>(window, "log-save").IsEnabled, "active writer guard survives rebuild: " + change);
                Click(window, "log-save"); Check(fixture.Pickers.Count == 1, "active writer cannot launch a second picker: " + change);
            }
            release.Set(); Wait(window, saving);
            Check(!File.Exists(destination), "cancelled staged writer does not publish after " + change);
            Check(fixture.WriteCalls == 1, "only one writer started for " + change);
        }

        foreach (string change in new[] { "filter", "page", "language", "theme", "close" })
        {
            using var fixture = new Fixture();
            var window = fixture.Window; var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Cleanup.Add(() => complete.TrySetResult()); var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Saver = async (destination, bytes, token) =>
            {
                await Task.Run(() => PortableFilePublication.WriteNew(destination, bytes, token)); committed.TrySetResult(); await complete.Task;
            };
            string captured = Output(window); Task saving = fixture.Begin(); string destination = fixture.File("committed-before-change.txt"); fixture.Pickers[^1].SetResult(destination);
            Wait(window, committed.Task); Mutate(fixture, change);
            if (change == "page") { window.SelectToolForTest("logs"); Flush(window); }
            complete.TrySetResult(); Wait(window, saving);
            Check(File.ReadAllText(destination) == captured, "late cancellation preserves already committed snapshot: " + change);
            if (change == "close") Check(!window.IsVisible, "committed callback cannot reopen a closed window");
            else
            {
                Check(Feedback(window).Contains(window.Settings.Language == "en" ? "earlier snapshot" : "先前", StringComparison.Ordinal), "cancellation after confirmed commit is reported as saved earlier snapshot: " + change);
                Check(!Feedback(window).Contains("cancelled", StringComparison.Ordinal) && !Feedback(window).Contains("取消", StringComparison.Ordinal), "committed file is never falsely described as cancelled: " + change);
            }
        }

        using (var fixture = new Fixture())
        {
            var window = fixture.Window; var release = new ManualResetEventSlim(); fixture.Cleanup.Add(release.Set); fixture.Owned.Add(release);
            var staged = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Saver = (destination, bytes, token) => Task.Run(() => PortableFilePublication.WriteNew(destination, stream =>
            { stream.Write(bytes); staged.TrySetResult(token); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); }, token));
            string captured = Output(window); Task saving = fixture.Begin(); string destination = fixture.File("append-while-saving.txt"); fixture.Pickers[^1].SetResult(destination); Wait(window, staged.Task);
            File.AppendAllText(fixture.Source, "arrived during save\n"); Wait(window, window.PollLogAsync());
            Check(!staged.Task.Result.IsCancellationRequested, "ordinary incoming lines do not cancel a selected snapshot"); release.Set(); Wait(window, saving);
            Check(File.ReadAllText(destination) == captured, "background writer retains exact captured bytes while view updates");
        }
    }

    private static void PublicationOutcomes()
    {
        foreach (bool change in new[] { false, true })
        {
            using var fixture = new Fixture(); var window = fixture.Window;
            var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); fixture.Cleanup.Add(() => complete.TrySetResult());
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); string retained = fixture.File("retained snapshot.tmp");
            File.WriteAllText(retained, "owned retained fixture");
            fixture.Saver = async (destination, _, _) => { started.TrySetResult(); await complete.Task; throw new FilePublicationException(FilePublicationState.OutcomeUnknown, destination, retained, "owned unknown result"); };
            Task saving = fixture.Begin(); string destination = fixture.File("unknown-" + change + ".txt"); fixture.Pickers[^1].SetResult(destination); Wait(window, started.Task);
            if (change) { window.SelectToolForTest("notes"); window.SelectToolForTest("logs"); AppearanceTestActions.Set(window, "zh", true); }
            complete.TrySetResult(); Wait(window, saving);
            string feedback = Feedback(window);
            Check(feedback.Contains(destination, StringComparison.Ordinal) && feedback.Contains(retained, StringComparison.Ordinal), "unknown outcome names actual target and retained temporary file");
            Check(!feedback.StartsWith("Not saved", StringComparison.Ordinal) && !feedback.StartsWith("未保存", StringComparison.Ordinal), "unknown outcome is not mislabeled as an untouched destination");
            Check(Find<Button>(window, "log-save").IsEnabled, "unknown outcome restores global export guard");
        }
    }

    private static int Layouts(string? artifacts)
    {
        using var fixture = new Fixture(); var window = fixture.Window;
        File.AppendAllText(fixture.Source, string.Join('\n', Enumerable.Range(0, 70).Select(index => $"{index:000} ERROR 模拟长日志 / diagnostic export fixture 😀")) + "\n");
        Wait(window, window.PollLogAsync()); Find<TextBox>(window, "log-search").Text = "ERROR";
        int count = 0;
        foreach (string language in new[] { "en", "zh" }) foreach (bool light in new[] { false, true })
        foreach (int width in new[] { 320, 480, 760 }) foreach (int height in new[] { 360, 720 }) foreach (double scale in new[] { 1d, 1.25, 1.5, 2 })
        {
            AppearanceTestActions.Set(window, language, light); window.SetRenderScaling(scale); window.Width = width; window.Height = height; Flush(window);
            string context = $"{language}/{light}/{width}/{height}/{scale}";
            Check(Output(window).Contains("diagnostic export fixture", StringComparison.Ordinal), "filled filtered view survives actual appearance transition " + context);
            Task saving = fixture.Begin(context); string destination = fixture.File("layout-export-" + count + ".txt"); fixture.Pickers[^1].SetResult(destination); Wait(window, saving); Flush(window);
            Check(File.ReadAllText(destination) == Output(window), "actual export works after appearance rebuild " + context);
            foreach (var pair in new[] { new[] { "log-copy", "log-save" }, new[] { "log-stop", "log-clear" } })
            {
                var first = Find<Button>(window, pair[0]); var second = Find<Button>(window, pair[1]);
                Check(Math.Abs(first.Bounds.Width - second.Bounds.Width) <= 1 && Math.Abs(first.Bounds.Height - second.Bounds.Height) <= 1, "group buttons share size " + context);
                var a = first.TranslatePoint(default, window); var b = second.TranslatePoint(default, window);
                Check(a is Point p && b is Point q && Math.Abs(p.Y - q.Y) <= 1 && Math.Abs(q.X - (p.X + first.Bounds.Width) - 6) <= 1, "group alignment and 6px gap " + context);
                foreach (var button in new[] { first, second })
                {
                    var label = (TextBlock)button.Content!; var point = button.TranslatePoint(default, window);
                    Check(label.Bounds.Width > 0 && label.Bounds.Height <= button.Bounds.Height + 1 && point is Point origin && origin.X >= -1 && origin.X + button.Bounds.Width <= width + 1, "no button text clipping or horizontal overflow " + context);
                }
            }
            var feedback = Find<TextBlock>(window, "log-export-feedback");
            Check(feedback.Text!.Contains(destination, StringComparison.Ordinal) && feedback.Bounds.Width <= width && feedback.Bounds.Height > 0, "wrapped saved feedback remains visible " + context);
            if (artifacts is not null && height == 720 && scale == 1 && (language == "en" && light && width == 320 || language == "zh" && !light && width == 480))
            {
                feedback.BringIntoView(); Flush(window);
                Directory.CreateDirectory(artifacts); AvaloniaHeadlessPlatform.ForceRenderTimerTick(3); Dispatcher.UIThread.RunJobs();
                using var image = new RenderTargetBitmap(new(width, height), new(96, 96)); image.Render(window); image.Save(Path.Combine(artifacts, $"log-export-{language}-{width}.png"), PngBitmapEncoderOptions.Default);
            }
            count++;
        }
        Check(count == 96, "complete export layout matrix"); return count;
    }

    private static void Mutate(Fixture fixture, string change)
    {
        var window = fixture.Window;
        switch (change)
        {
            case "path": Find<TextBox>(window, "log-path").Text = fixture.Source + ".other"; break;
            case "path-restore": Find<TextBox>(window, "log-path").Text = fixture.Source + ".other"; Find<TextBox>(window, "log-path").Text = fixture.Source; break;
            case "filter": Find<TextBox>(window, "log-search").Text = "ERROR"; break;
            case "filter-restore": Find<TextBox>(window, "log-search").Text = "ERROR"; Find<TextBox>(window, "log-search").Text = ""; break;
            case "keywords": Find<TextBox>(window, "log-keywords").Text = "different"; break;
            case "clear": Click(window, "log-clear"); break;
            case "restart": Click(window, "log-start"); Wait(window, window.LatestLogPoll); break;
            case "page": window.SelectToolForTest("notes"); break;
            case "language": AppearanceTestActions.Set(window, "zh", window.Settings.LightTheme); break;
            case "theme": AppearanceTestActions.Set(window, window.Settings.Language, !window.Settings.LightTheme); break;
            case "close": window.Close(); break;
            default: throw new ArgumentOutOfRangeException(nameof(change));
        }
        Flush(window);
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "side-island-log-export-" + Guid.NewGuid().ToString("N"));
        internal string Source { get; }
        internal IslandWindow Window { get; }
        internal readonly List<TaskCompletionSource<string?>> Pickers = new();
        internal readonly List<string> Suggestions = new();
        internal readonly List<Action> Cleanup = new();
        internal readonly List<IDisposable> Owned = new();
        internal Func<string, byte[], CancellationToken, Task>? Saver;
        internal int WriteCalls;
        internal Fixture(bool start = true)
        {
            Directory.CreateDirectory(DirectoryPath); Source = File("日志 source 中文 😀.log");
            System.IO.File.WriteAllText(Source, "ready\nERROR first 中文 😀\ninfo one\nERROR second\n", new UTF8Encoding(false));
            var sources = new IntegrationSources(_ => Task.FromResult(new MediaSnapshot(false, "", "", "", null, null, null, false, null)), (_, _) => Task.FromResult<string?>(null), (_, _) => throw new InvalidOperationException("No quota access in export fixture"))
            {
                ChooseLogDestination = suggestion => { Suggestions.Add(suggestion); var picker = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously); Pickers.Add(picker); return picker.Task; },
                SaveLogSnapshot = (destination, bytes, token) => { WriteCalls++; return Saver is null ? Task.Run(() => PortableFilePublication.WriteNew(destination, bytes, token), token) : Saver(destination, bytes, token); }
            };
            Window = new IslandWindow(true, File("preferences"), sources); Window.Show(); Window.Expand(); AppearanceTestActions.Set(Window, "en", false); Window.SelectToolForTest("logs"); Flush(Window);
            if (start) Start();
        }
        internal string File(string name) => Path.Combine(DirectoryPath, name);
        internal void Start() { Find<TextBox>(Window, "log-path").Text = Source; Click(Window, "log-start"); Wait(Window, Window.LatestLogPoll); }
        internal Task Begin(string context = "interaction")
        {
            int before = Pickers.Count; var button = Find<Button>(Window, "log-save"); bool enabled = button.IsEnabled;
            Click(Window, "log-save");
            if (Pickers.Count != before + 1)
            {
                var fields = typeof(IslandWindow).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                string state = string.Join(", ", fields.Where(field => field.Name is "savingLog" or "closing" or "logExportState" or "logExportRevision").Select(field => field.Name + "=" + field.GetValue(Window)));
                throw new InvalidOperationException($"Log export did not open a new picker: {context}; enabledBefore={enabled}; expanded={Window.IsExpanded}; outputAttached={Find<TextBox>(Window, "log-output").IsAttachedToVisualTree()}; length={Output(Window).Length}; {state}");
            }
            return Window.LatestLogExport;
        }
        public void Dispose()
        {
            foreach (var release in Cleanup) release(); foreach (var picker in Pickers) picker.TrySetResult(null);
            Window.Close(); Wait(Window, Window.LatestLogExport); Wait(Window, Window.LatestLogPoll); Dispatcher.UIThread.RunJobs();
            foreach (var owned in Owned) owned.Dispose();
            string full = Path.GetFullPath(DirectoryPath), parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(Path.GetDirectoryName(full), parent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("side-island-log-export-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected fixture cleanup path");
            Directory.Delete(full, true);
        }
    }
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static string Output(Window window) => Find<TextBox>(window, "log-output").Text ?? "";
    private static string Feedback(Window window) => Find<TextBlock>(window, "log-export-feedback").Text ?? "";
    private static void Click(Window window, string name) => Find<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Flush(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
    private static void Wait(Window window, Task task)
    {
        var clock = Stopwatch.StartNew(); while (!task.IsCompleted && clock.ElapsedMilliseconds < 15000) { Flush(window); Thread.Sleep(2); }
        if (!task.IsCompleted) throw new TimeoutException("Log export fixture did not finish"); task.GetAwaiter().GetResult(); Flush(window);
    }
    private static void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException("Log export: " + message); }
}
