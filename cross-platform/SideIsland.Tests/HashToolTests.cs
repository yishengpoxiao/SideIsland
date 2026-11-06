using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SideIsland.Desktop;

namespace SideIsland.Tests;

internal static class HashToolTests
{
    public static void Run(string? artifactDirectory = null)
    {
        string directory = Path.Combine(Path.GetTempPath(), "side-island-hash-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); string file = Path.Combine(directory, "下载 file.txt"), other = Path.Combine(directory, "empty.txt");
        byte[] data = Encoding.UTF8.GetBytes("abc"); File.WriteAllBytes(file, data); File.WriteAllBytes(other, []);
        string sha256 = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(), sha512 = Convert.ToHexString(SHA512.HashData(data)).ToLowerInvariant();
        var window = new IslandWindow(true, Path.Combine(directory, "settings"));
        try
        {
            window.Show(); window.Expand(); AppearanceTestActions.Set(window, "en", window.Settings.LightTheme); window.SelectToolForTest("hash"); Flush(window);
            var path = Find<TextBox>(window, "hash-path"); var expected = Find<TextBox>(window, "hash-expected"); var algorithm = Find<ComboBox>(window, "hash-algorithm");
            var result = Find<TextBox>(window, "hash-result"); var status = Find<TextBlock>(window, "hash-status");
            var compute = Find<Button>(window, "hash-compute"); var cancel = Find<Button>(window, "hash-cancel");
            path.Text = file; expected.Text = new string('0', 64) + "  other.txt\n" + sha256 + " *下载 file.txt";
            Click(compute); Wait(window, () => compute.IsEnabled);
            Check(result.Text == sha256 && status.Text == "Checksum matches", "GNU manifest selects real Unicode filename and computes SHA-256: " + status.Text + " / " + result.Text);
            Check(Find<ProgressBar>(window, "hash-progress").Value == 100, "completed progress remains at 100 after queued callbacks");
            algorithm.SelectedIndex = 1; expected.Text = "SHA512 (下载 file.txt) = " + sha512;
            Click(compute); Wait(window, () => compute.IsEnabled);
            Check(result.Text == sha512 && status.Text == "Checksum matches", "BSD manifest and SHA-512 real-file verification");
            expected.Text = new string('0', 128); Click(compute); Wait(window, () => compute.IsEnabled);
            Check(result.Text == sha512 && status.Text!.StartsWith("Checksum mismatch", StringComparison.Ordinal), "mismatch retains actual digest and never reports success");
            expected.Text = sha512 + "  first/下载 file.txt\n" + sha512 + "  second/下载 file.txt";
            Click(compute); Wait(window, () => compute.IsEnabled);
            Check(string.IsNullOrEmpty(result.Text) && status.Text!.Contains("conflicting filename", StringComparison.Ordinal), "ambiguous filenames rejected before verification");
            expected.Text = new string('x', 131073); Click(compute); Wait(window, () => compute.IsEnabled);
            Check(expected.Text.Length == 131073 && string.IsNullOrEmpty(result.Text) && status.Text!.Contains("too large", StringComparison.Ordinal), "oversized manifest is rejected without silently truncating pasted input");
            expected.Text = ""; path.Text = Path.Combine(directory, "missing.txt"); Click(compute); Wait(window, () => compute.IsEnabled);
            Check(string.IsNullOrEmpty(result.Text) && status.Text == "The selected file was not found. Check its path.", "filesystem failure uses selected UI language");
            path.Text = file;
            expected.Text = ""; Click(compute); path.Text = other; Wait(window, () => compute.IsEnabled);
            Check(string.IsNullOrEmpty(result.Text) && status.Text == "Inputs changed. Calculate again.", "input change prevents an already-started result from being applied");
            path.Text = file; Click(compute); Click(cancel); Wait(window, () => compute.IsEnabled);
            Check(string.IsNullOrEmpty(result.Text) && status.Text == "Cancelled." && !cancel.IsEnabled, "cancel handles worker completion racing with UI continuation");
            Click(compute); window.SelectToolForTest("notes"); window.SelectToolForTest("hash"); Flush(window); Wait(window, () => compute.IsEnabled);
            Check(Find<TextBox>(window, "hash-path").Text == file && Find<ComboBox>(window, "hash-algorithm").SelectedIndex == 1 && string.IsNullOrEmpty(Find<TextBox>(window, "hash-result").Text), "page switch preserves inputs but discards detached result");
            var currentCompute = Find<Button>(window, "hash-compute"); Click(currentCompute);
            var language = window.GetVisualDescendants().OfType<Button>().First(button => button.Content is TextBlock text && text.Text == "中");
            Click(language); Flush(window); Wait(window, () => currentCompute.IsEnabled);
            Check(window.Settings.Language == "zh" && Find<TextBox>(window, "hash-path").Text == file && string.IsNullOrEmpty(Find<TextBox>(window, "hash-result").Text), "language rebuild cancels detached work and preserves drafts");
            var finalCompute = Find<Button>(window, "hash-compute"); Click(finalCompute); Wait(window, () => finalCompute.IsEnabled);
            Check(Find<TextBox>(window, "hash-result").Text == sha512 && Find<TextBlock>(window, "hash-status").Text!.StartsWith("计算完成", StringComparison.Ordinal), "new Chinese view remains usable after cancellation and rebuild");
            Click(finalCompute); bool previousTheme = window.Settings.LightTheme;
            var theme = window.GetVisualDescendants().OfType<Button>().First(button => button.Content is TextBlock text && text.Text == (previousTheme ? "☾" : "☼"));
            Click(theme); Flush(window); Wait(window, () => finalCompute.IsEnabled);
            Check(window.Settings.LightTheme != previousTheme && Find<TextBox>(window, "hash-path").Text == file && string.IsNullOrEmpty(Find<TextBox>(window, "hash-result").Text), "theme rebuild cancels detached work without stale output");
            int layouts = FilledLayouts(window, file, sha256);
            Check(layouts == 96, "filled checksum layout matrix is complete");
            Console.WriteLine("PASS: real-file SHA-256/SHA-512 checksum UI, GNU/BSD manifests, Unicode filenames, mismatch/ambiguity, cancel/completion race, stale path/page/language suppression; headless UI, no native desktop claim.");
            Console.WriteLine("PASS: 96 populated checksum layouts with multiline manifest and conflict message; zh/en, light/dark, 320/480/760 widths, 360/720 heights and 100/125/150/200% headless scaling.");
            HashExportTests.Run(artifactDirectory);
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); Directory.Delete(directory, true); }
    }
    private static int FilledLayouts(IslandWindow window, string file, string digest)
    {
        int count = 0;
        string manifest = string.Concat(Enumerable.Range(0, 40).Select(index => digest + "  long-directory/中文-download-" + index + ".zip\n")) + digest + "  first/下载 file.txt\n" + digest + "  second/下载 file.txt";
        foreach (string language in new[] { "en", "zh" }) foreach (bool light in new[] { false, true })
        foreach (int width in new[] { 320, 480, 760 }) foreach (int height in new[] { 360, 720 }) foreach (double scale in new[] { 1d, 1.25, 1.5, 2 })
        {
            AppearanceTestActions.Set(window, language, light);
            window.SetRenderScaling(scale); window.SelectToolForTest("hash"); window.Width = width; window.Height = height; Flush(window);
            Find<TextBox>(window, "hash-path").Text = file; Find<ComboBox>(window, "hash-algorithm").SelectedIndex = 0; Find<TextBox>(window, "hash-expected").Text = manifest;
            Click(Find<Button>(window, "hash-compute")); Flush(window);
            var state = Find<TextBlock>(window, "hash-status");
            Check(state.Text == (language == "en" ? "The list has conflicting filename entries. Use an unambiguous entry." : "清单中存在同名冲突，请使用唯一条目。"), "filled layout retains correct localized conflict message");
            Check(state.Bounds.Width > 0 && state.Bounds.Height + 1 >= state.DesiredSize.Height, "conflict text is not vertically cropped");
            Check(Find<TextBox>(window, "hash-expected").Bounds.Height <= 181, "long manifest stays compact and scrollable");
            foreach (var field in window.GetVisualDescendants().OfType<Control>().Where(control => control.Name?.StartsWith("hash-", StringComparison.Ordinal) == true))
            {
                var position = field.TranslatePoint(default, window);
                Check(position is Point point && point.X >= -1 && point.X + field.Bounds.Width <= width + 1, "populated checksum controls remain inside small window width");
            }
            count++;
        }
        return count;
    }
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Flush(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
    private static void Wait(Window window, Func<bool> done)
    {
        var timer = Stopwatch.StartNew();
        while (!done() && timer.Elapsed < TimeSpan.FromSeconds(10)) { Flush(window); Thread.Sleep(5); }
        Flush(window); Check(done(), "worker finishes before test deadline");
    }
    private static void Check(bool value, string label) { if (!value) throw new InvalidOperationException("Checksum UI: " + label); }
}
