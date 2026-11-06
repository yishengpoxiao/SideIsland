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

namespace SideIsland.Tests;

internal static class LogToolTests
{
    private static int checks;
    internal static void Run(string? artifacts)
    {
        string directory = Path.Combine(Path.GetTempPath(), "side-island-log-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); string path = Path.Combine(directory, "日志 中文 😀.log");
        File.WriteAllText(path, "historical ERROR\nready\n", new UTF8Encoding(false)); string? copied = null;
        TaskCompletionSource<string?>? chooser = null; int chooserCalls = 0;
        var sources = new IntegrationSources(_ => Task.FromResult(new MediaSnapshot(false, "", "", "", null, null, null, false, null)), (_, _) => Task.FromResult<string?>(null), (_, _) => throw new InvalidOperationException("No quota access in log fixture"))
        { WriteClipboardText = text => { copied = text; return Task.CompletedTask; }, ChooseLogPath = () => { chooserCalls++; return chooser!.Task; } };
        var window = new IslandWindow(true, Path.Combine(directory, "preferences"), sources);
        try
        {
            window.Show(); window.Expand(); AppearanceTestActions.Set(window, "en", false); window.SelectToolForTest("logs"); Flush(window);
            Check(!Find<Button>(window, "log-stop").IsEnabled && !Find<Button>(window, "log-clear").IsEnabled && !Find<Button>(window, "log-copy").IsEnabled, "initial actions reflect no active session");
            var target = Find<TextBox>(window, "log-path"); target.Text = "relative.log"; Click(window, "log-start");
            Check(Field<string>(window, "status").Contains("absolute", StringComparison.Ordinal), "invalid path localized without I/O");
            chooser = new(TaskCreationOptions.RunContinuationsAsynchronously); Click(window, "log-choose"); target.Text = "edited while choosing";
            chooser.SetResult(path); Wait(window, window.LatestLogChoose);
            Check(target.Text == "edited while choosing", "late picker does not overwrite a newer manual edit");
            chooser = new(TaskCreationOptions.RunContinuationsAsynchronously); Click(window, "log-choose"); window.SelectToolForTest("notes"); window.SelectToolForTest("logs"); Flush(window);
            Check(!Find<Button>(window, "log-choose").IsEnabled, "reconstructed page preserves one pending picker"); Click(window, "log-choose");
            Check(chooserCalls == 2, "rapid reconstructed choose cannot open another picker"); chooser.SetResult(path); Wait(window, window.LatestLogChoose);
            target = Find<TextBox>(window, "log-path"); Check(target.Text == "edited while choosing" && Find<Button>(window, "log-choose").IsEnabled, "detached picker suppressed and replacement control reenabled");
            target.Text = path; Click(window, "log-start"); Wait(window, window.LatestLogPoll);
            Check(Output(window).Contains("historical ERROR", StringComparison.Ordinal) && Find<TextBlock>(window, "log-state").Text == "Watching", "actual Unicode file tail appears");
            Check(Field<string>(window, "status") == "", "valid start clears obsolete path error");
            Check(Field<NotificationCenter?>(window, "notificationCenter") is null, "history does not create an alert or start a notification source");
            var keywords = Find<TextBox>(window, "log-keywords"); keywords.Text = new string('x', 101); Flush(window);
            Check(Find<TextBlock>(window, "log-keywords-hint").Text!.Contains("limit exceeded", StringComparison.Ordinal), "invalid incremental keyword input is contained with actionable feedback");
            Click(window, "log-start"); Check(Output(window).Contains("historical ERROR", StringComparison.Ordinal), "invalid keyword restart preserves active session");
            keywords.Text = "error, exception"; Flush(window);
            File.AppendAllText(path, "new ERROR: fixture only\n", Encoding.UTF8); Wait(window, window.PollLogAsync());
            var center = Field<NotificationCenter>(window, "notificationCenter"); Check(center.Items.Count == 1, "new keyword line produces one local alert");
            Wait(window, window.PollLogAsync()); Check(center.Items.Count == 1, "idle poll does not replay alert");
            Find<TextBox>(window, "log-search").Text = "new error"; Flush(window); Click(window, "log-copy");
            Check(copied == "new ERROR: fixture only" && Output(window) == copied, "filter is case-insensitive; copy captures only displayed text");
            target.Text = Path.Combine(directory, "future.log"); Flush(window);
            Check(Find<TextBlock>(window, "log-draft-hint").IsVisible, "unapplied path edit is explicit");
            File.AppendAllText(path, "new error still old target\n"); Wait(window, window.PollLogAsync());
            Check(Output(window).Contains("still old target", StringComparison.Ordinal), "editing path does not silently switch active file");
            Click(window, "log-start"); Wait(window, window.LatestLogPoll);
            Check(Find<TextBlock>(window, "log-state").Text == "Waiting for file" && Output(window) == "", "missing file waits and clears previous target");
            File.WriteAllText(target.Text!, "created history ERROR\n"); Wait(window, window.PollLogAsync());
            Check(center.Items.Count == 1, "file arrival initial history is not retroactive alert");
            Find<TextBox>(window, "log-search").Text = "";
            center.SetApplicationEnabled("side-island", false);
            File.AppendAllText(target.Text!, "ERROR newly appended while blocked\n"); Wait(window, window.PollLogAsync());
            Check(center.Items.Count == 0, "blocking removes prior messages and suppresses new log alerts");

            string active = target.Text!;
            window.Collapse(); File.AppendAllText(active, "collapsed background\n"); Wait(window, window.PollLogAsync()); window.Expand(); Flush(window);
            Check(Output(window).Contains("collapsed background", StringComparison.Ordinal), "collapsed background read becomes visible on expand");
            window.SelectToolForTest("notes"); File.AppendAllText(active, "other page background\n"); Wait(window, window.PollLogAsync());
            window.SelectToolForTest("logs"); Flush(window);
            Check(Output(window).Contains("other page background", StringComparison.Ordinal), "switching tools keeps session and shows current snapshot");
            foreach (string language in new[] { "zh", "en" }) foreach (bool light in new[] { true, false })
            {
                AppearanceTestActions.Set(window, language, light);
                Check(Output(window).Contains("other page background", StringComparison.Ordinal) && Find<Button>(window, "log-stop").IsEnabled, "real header reconstruction retains live log session");
            }
            Click(window, "log-stop"); string stopped = Output(window); File.AppendAllText(active, "after stop\n"); Wait(window, window.PollLogAsync());
            Check(Output(window) == stopped && !Find<Button>(window, "log-stop").IsEnabled, "stop retains previous lines and ignores later append");
            Click(window, "log-clear"); Check(Output(window) == "" && File.ReadAllText(active).Contains("after stop", StringComparison.Ordinal), "clear view never mutates source file");
            Click(window, "log-start"); Wait(window, window.LatestLogPoll); Click(window, "log-clear"); Wait(window, window.PollLogAsync());
            Check(Output(window) == "", "running clear does not replay existing history");
            File.AppendAllText(active, "after clear\n"); Wait(window, window.PollLogAsync()); Check(Output(window) == "after clear", "clear follows subsequent appends from original offset");

            File.AppendAllText(active, string.Join("\n", Enumerable.Range(0, 65).Select(n => $"{n:000} 任务完成 中文 / Completed task 😀 — diagnostic fixture")) + "\n");
            Wait(window, window.PollLogAsync()); int layouts = Layouts(window, artifacts);
            Check(layouts == 96, "populated log matrix is complete");
            var controller = Field<PortableLogController>(window, "logController"); window.Close();
            Check(!controller.Snapshot.Running, "window close stops controller without UI wait");
            Console.WriteLine($"PASS: {checks} log UI checks, {layouts} populated zh/en light/dark size/DPI layouts; real owned files, actual header/actions, filtered clipboard injection, hidden/closed lifetime and notification rules. No real clipboard, native screenshot or target-desktop claim.");
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); Directory.Delete(directory, true); }
    }

    private static int Layouts(IslandWindow window, string? artifacts)
    {
        int count = 0;
        foreach (string language in new[] { "zh", "en" }) foreach (bool light in new[] { false, true })
        foreach (int width in new[] { 320, 480, 760 }) foreach (int height in new[] { 360, 720 }) foreach (double scale in new[] { 1d, 1.25, 1.5, 2 })
        {
            window.SetRenderScaling(scale); AppearanceTestActions.Set(window, language, light); window.Width = width; window.Height = height; Flush(window);
            string context = $"{language}/{light}/{width}/{height}/{scale}";
            foreach (var button in window.GetVisualDescendants().OfType<Button>().Where(b => b.IsVisible && b.Bounds.Width > 0))
            {
                if (button.Content is TextBlock label) Check(label.Bounds.Height <= button.Bounds.Height + 2, "button height " + context);
                Point? point = button.TranslatePoint(default, window);
                Check(point is Point p && p.X >= -2 && p.X + button.Bounds.Width <= width + 2, "button horizontal fit " + context);
            }
            var output = Find<TextBox>(window, "log-output"); Check(output.Bounds.Width > 100 && output.Bounds.Width <= width && output.Bounds.Height <= 241, "bounded log viewport " + context);
            Check(Output(window).Contains("diagnostic fixture", StringComparison.Ordinal), "filled snapshot survives appearance transition " + context);
            if (artifacts is not null && height == 720 && scale == 1 && (language == "en" && light && width == 320 || language == "zh" && !light && width == 480))
            {
                Directory.CreateDirectory(artifacts); AvaloniaHeadlessPlatform.ForceRenderTimerTick(3); Dispatcher.UIThread.RunJobs();
                using var image = new RenderTargetBitmap(new(width, height), new(96, 96)); image.Render(window); image.Save(Path.Combine(artifacts, $"log-filled-{language}-{width}.png"), PngBitmapEncoderOptions.Default);
            }
            count++;
        }
        return count;
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static string Output(Window window) => Find<TextBox>(window, "log-output").Text ?? "";
    private static void Click(Window window, string name) => Find<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Flush(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
    private static void Wait(Window window, Task task)
    {
        var time = Stopwatch.StartNew(); while (!task.IsCompleted && time.ElapsedMilliseconds < 10000) { Flush(window); Thread.Sleep(2); }
        if (!task.IsCompleted) throw new TimeoutException("Log UI operation did not finish"); task.GetAwaiter().GetResult(); Flush(window);
    }
    private static void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException("Log UI: " + message); }
}
