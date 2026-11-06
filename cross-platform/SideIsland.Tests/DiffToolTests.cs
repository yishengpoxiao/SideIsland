using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SideIsland.Desktop;

namespace SideIsland.Tests;
internal static class DiffToolTests
{
    public static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "island-diff-ui-" + Guid.NewGuid().ToString("N"));
        var window = new IslandWindow(true, directory);
        try
        {
            window.Settings.Language = "en"; window.Show(); window.Expand(); window.SelectToolForTest("diff"); Flush(window);
            var left = Find<TextBox>(window, "diff-left"); var right = Find<TextBox>(window, "diff-right");
            var compare = Find<Button>(window, "diff-compare"); var copy = Find<Button>(window, "diff-copy"); var save = Find<Button>(window, "diff-save"); var state = Find<TextBlock>(window, "diff-status");
            Check(!copy.IsEnabled && !save.IsEnabled, "no export exists before comparison");
            left.Text = "old😀\n"; right.Text = "new😀\n"; Click(compare); Wait(window, () => compare.IsEnabled);
            Check(copy.IsEnabled && save.IsEnabled && state.Text!.Contains("Changed 1", StringComparison.Ordinal), "completed comparison enables complete patch export");
            Click(copy); Click(save); Flush(window);
            Check(copy.IsEnabled && save.IsEnabled, "demo copy/save exercise actions without filesystem or clipboard side effects");
            right.Text = "newest\n"; Flush(window); Check(!copy.IsEnabled && !save.IsEnabled, "editing immediately invalidates previous export");
            Click(compare); right.Text = "latest\n"; Wait(window, () => compare.IsEnabled);
            Check(!copy.IsEnabled && !save.IsEnabled && state.Text == "Inputs changed. Compare again.", "late comparison cannot restore stale export after input changes");
            left.Text = "same\n"; right.Text = "same\n"; Click(compare); Wait(window, () => compare.IsEnabled);
            Check(!copy.IsEnabled && !save.IsEnabled && state.Text == "Both texts are identical.", "identical input has no export actions");
            right.Text = "different\n"; Click(compare); window.SelectToolForTest("notes"); window.SelectToolForTest("diff"); Flush(window); Wait(window, () => compare.IsEnabled);
            Check(Find<TextBox>(window, "diff-left").Text == "same\n" && Find<TextBox>(window, "diff-right").Text == "different\n" && !Find<Button>(window, "diff-copy").IsEnabled, "page rebuild preserves drafts but never detached results: left=" + Find<TextBox>(window, "diff-left").Text + "; right=" + Find<TextBox>(window, "diff-right").Text + "; enabled=" + Find<Button>(window, "diff-copy").IsEnabled);
            var current = Find<Button>(window, "diff-compare"); Click(current);
            var language = window.GetVisualDescendants().OfType<Button>().First(b => b.Content is TextBlock t && t.Text == "中"); Click(language); Flush(window); Wait(window, () => current.IsEnabled);
            Check(window.Settings.Language == "zh" && !Find<Button>(window, "diff-copy").IsEnabled, "language change cancels old view work");
            current = Find<Button>(window, "diff-compare"); Click(current); Wait(window, () => current.IsEnabled);
            Check(Find<Button>(window, "diff-save").IsEnabled && Find<TextBlock>(window, "diff-status").Text!.Contains("修改 1", StringComparison.Ordinal), "new language view remains usable");
            Console.WriteLine("PASS: actual diff controls, complete-export availability, demo isolation, input/page/language late-result suppression; headless desktop only.");
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Flush(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
    private static void Wait(Window window, Func<bool> done) { var watch = Stopwatch.StartNew(); while (!done() && watch.Elapsed < TimeSpan.FromSeconds(10)) { Flush(window); Thread.Sleep(5); } Flush(window); Check(done(), "background work completed"); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException("Diff UI: " + message); }
}
