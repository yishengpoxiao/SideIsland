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

internal static class PublicationUiTests
{
    private static int checks;
    internal static void Run(string? artifacts = null)
    {
        checks = 0;
        string root = Path.Combine(Path.GetTempPath(), "side-island-publication-ui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            foreach (string language in new[] { "en", "zh" }) foreach (string tool in new[] { "hash", "qr", "pin" }) UnknownResult(root, language, tool);
            PinFeedbackLayouts(root, artifacts);
            foreach (string change in new[] { "edit", "recalculate", "page", "language", "theme", "close" }) CancelChecksum(root, change);
            Console.WriteLine($"PASS: {checks} publication UI checks; actual checksum/QR/pin unknown-result handlers, bilingual target/recovery paths, and checksum writer cancellation on edits/recalculation/navigation/appearance/close. Owned files and injected writers; no native picker or clipboard.");
        }
        finally { Directory.Delete(root, true); }
    }

    // This standalone selection also runs against the captured pre-fix pin UI.
    internal static void RunPinBaseline()
    {
        string root = Path.Combine(Path.GetTempPath(), "side-island-publication-baseline-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { UnknownResult(root, "en", "pin"); }
        finally { Directory.Delete(root, true); }
    }

    private static IntegrationSources Sources() => new(
        _ => Task.FromResult(new MediaSnapshot(false, "", "", "", null, null, null, false, null)),
        (_, _) => Task.FromResult<string?>(null), (_, _) => throw new InvalidOperationException("Unused test quota"));

    private static void UnknownResult(string root, string language, string tool)
    {
        string target = Path.Combine(root, tool + "-" + language + "-result.txt"), recovery = Path.Combine(root, tool + "-" + language + "-retained.tmp");
        File.WriteAllText(target, "owned external bytes after a publication"); File.WriteAllText(recovery, "owned prepared bytes");
        var failure = new FilePublicationException(FilePublicationState.OutcomeUnknown, target, recovery, "owned unconfirmed publication");
        string text;
        if (tool == "pin")
        {
            using var bytes = new MemoryStream(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
            var pin = new ScreenPinWindow(new Bitmap(bytes), language, false, true, new() { ChooseDestination = _ => Task.FromResult<string?>(target), SavePng = (_, _, _) => Task.FromException(failure) }, null);
            try { pin.Show(); Flush(pin); Click(pin, "pin-save"); Wait(pin, pin.LatestSave); text = ((TextBlock)typeof(ScreenPinWindow).GetField("message", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pin)!).Text ?? ""; }
            finally { pin.Close(); Flush(pin); }
        }
        else
        {
            var sources = Sources() with
            {
                ChooseChecksumDestination = _ => Task.FromResult<string?>(target), SaveChecksumManifest = (_, _, _) => Task.FromException(failure),
                ChooseQrDestination = _ => Task.FromResult<string?>(target), SaveQrPng = (_, _, _) => Task.FromException(failure)
            };
            var window = new IslandWindow(true, Path.Combine(root, tool + language + "-prefs"), sources);
            try
            {
                window.Show(); window.Expand(); AppearanceTestActions.Set(window, language, false); window.SelectToolForTest(tool); Flush(window);
                if (tool == "hash")
                {
                    string source = Path.Combine(root, language + "-source.txt"); File.WriteAllText(source, "owned source"); Calculate(window, source);
                    Click(window, "hash-save-manifest"); Wait(window, window.LatestChecksumExport); text = Find<TextBlock>(window, "hash-status").Text ?? "";
                }
                else
                {
                    Find<TextBox>(window, "qr-input").Text = "owned QR"; Click(window, "qr-generate"); Click(window, "qr-save"); Wait(window, window.LatestQrSave); text = Find<TextBlock>(window, "qr-status").Text ?? "";
                }
            }
            finally { window.Close(); Flush(window); }
        }
        Console.WriteLine($"PUBLICATION FEEDBACK {tool}/{language}: {text.Replace(Environment.NewLine, " | ").Replace("\n", " | ")}");
        Check(text.Contains(language == "en" ? "could not be confirmed" : "无法确认保存结果", StringComparison.Ordinal), tool + " must report uncertain state instead of claiming not saved: " + text);
        Check(text.Contains(target, StringComparison.Ordinal) && text.Contains(recovery, StringComparison.Ordinal), tool + " keeps exact destination and retained-copy paths");
        Check(File.ReadAllText(target) == "owned external bytes after a publication" && File.ReadAllText(recovery) == "owned prepared bytes", tool + " feedback never modifies either version");
    }

    private static void CancelChecksum(string root, string change)
    {
        string source = Path.Combine(root, change + "-source.txt"), destination = Path.Combine(root, change + "-cancelled.sha256"); File.WriteAllText(source, "owned checksum source");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken captured = default; int writes = 0;
        var sources = Sources() with
        {
            ChooseChecksumDestination = _ => Task.FromResult<string?>(destination),
            SaveChecksumManifest = async (path, manifest, token) => { captured = token; writes++; entered.SetResult(); await release.Task; await Task.Run(() => PortableFilePublication.WriteNew(path, new UTF8Encoding(false, true).GetBytes(manifest), token)); }
        };
        var window = new IslandWindow(true, Path.Combine(root, change + "-prefs"), sources);
        try
        {
            window.Show(); window.Expand(); AppearanceTestActions.Set(window, "en", false); window.SelectToolForTest("hash"); Calculate(window, source);
            Click(window, "hash-save-manifest"); Task saving = window.LatestChecksumExport; Wait(window, entered.Task); Click(window, "hash-save-manifest");
            Check(writes == 1 && !Find<Button>(window, "hash-save-manifest").IsEnabled, "pending write cannot be started twice: " + change);
            switch (change)
            {
                case "edit": Find<TextBox>(window, "hash-expected").Text = "0"; break;
                case "recalculate": Click(window, "hash-compute"); break;
                case "page": window.SelectToolForTest("notes"); break;
                case "language": AppearanceTestActions.Set(window, "zh", false); break;
                case "theme": AppearanceTestActions.Set(window, "en", true); break;
                case "close": window.Close(); break;
            }
            Flush(window); Check(captured.IsCancellationRequested, "obsolete pending write is cancelled: " + change);
            release.SetResult(); Wait(window, saving);
            if (change == "recalculate") Until(window, () => Find<Button>(window, "hash-compute").IsEnabled);
            Check(!File.Exists(destination), "cancelled write does not publish a stale manifest: " + change);
        }
        finally { release.TrySetResult(); window.Close(); Flush(window); }
    }

    private static void PinFeedbackLayouts(string root, string? artifacts)
    {
        int layouts = 0;
        foreach (string language in new[] { "en", "zh" }) foreach (bool light in new[] { false, true })
        foreach (var size in new[] { new Size(250, 190), new Size(320, 260), new Size(800, 600) }) foreach (double scale in new[] { 1d, 1.25, 1.5, 2 })
        {
            string target = Path.Combine(root, "long diagnostic folder", "本机导出路径-" + new string('x', 50) + ".png");
            var failure = new FilePublicationException(FilePublicationState.OutcomeUnknown, target, target + ".retained.tmp", "owned layout fixture");
            using var bytes = new MemoryStream(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
            var pin = new ScreenPinWindow(new Bitmap(bytes), language, light, true, new() { ChooseDestination = _ => Task.FromResult<string?>(target), SavePng = (_, _, _) => Task.FromException(failure) }, null);
            try
            {
                pin.SetRenderScaling(scale); pin.Show(); pin.Width = size.Width; pin.Height = size.Height; Flush(pin);
                Click(pin, "pin-save"); Wait(pin, pin.LatestSave);
                var message = (TextBlock)typeof(ScreenPinWindow).GetField("message", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pin)!;
                var scroll = pin.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(view => ReferenceEquals(view.Content, message));
                Control visible = scroll is null ? message : scroll;
                var position = visible.TranslatePoint(default, pin);
                if (artifacts is not null && scale == 1 && (language == "en" && light && size.Width == 250 || language == "zh" && !light && size.Width == 320))
                {
                    Directory.CreateDirectory(artifacts); AvaloniaHeadlessPlatform.ForceRenderTimerTick(3); Flush(pin);
                    using var image = new RenderTargetBitmap(new((int)size.Width, (int)size.Height), new(96, 96)); image.Render(pin); image.Save(Path.Combine(artifacts, $"pin-publication-{language}-{size.Width}.png"), PngBitmapEncoderOptions.Default);
                }
                string context = $"{language}/{light}/{size}/{scale}";
                Check(position is Point point && point.Y >= -1 && point.Y + visible.Bounds.Height <= pin.Bounds.Height + 1, "pin diagnostic is inside small window " + context + "; origin=" + position + "; size=" + visible.Bounds.Size);
                if (scroll is not null) { scroll.ScrollToEnd(); Flush(pin); Check(scroll.Offset.Y + scroll.Viewport.Height >= scroll.Extent.Height - 2, "retained path remains reachable " + context); }
                Check(message.Text!.Contains(failure.RetainedTemporaryPath!, StringComparison.Ordinal), "full recovery path is retained " + context);
                foreach (var button in pin.GetVisualDescendants().OfType<Button>().Where(button => button.IsEffectivelyVisible))
                {
                    var origin = button.TranslatePoint(default, pin);
                    Check(origin is Point p && p.X >= -1 && p.Y >= -1 && p.X + button.Bounds.Width <= pin.Bounds.Width + 1 && p.Y + button.Bounds.Height <= pin.Bounds.Height + 1, "error feedback does not displace controls " + context);
                }
                layouts++;
            }
            finally { pin.Close(); Flush(pin); }
        }
        Check(layouts == 48, "complete pin diagnostic size/DPI matrix");
        Console.WriteLine("PASS: 48 populated pin diagnostic layouts; full recovery paths remain reachable at minimum size, both languages/themes and four headless scales.");
    }

    private static void Calculate(IslandWindow window, string source)
    {
        Find<TextBox>(window, "hash-path").Text = source; Find<TextBox>(window, "hash-expected").Text = ""; Click(window, "hash-compute");
        Until(window, () => Find<Button>(window, "hash-compute").IsEnabled); Check(Find<Button>(window, "hash-save-manifest").IsEnabled, "calculation enables export");
    }
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Click(Window window, string name) { Find<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Flush(window); }
    private static void Flush(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
    private static void Until(Window window, Func<bool> ready)
    {
        var elapsed = Stopwatch.StartNew(); while (!ready() && elapsed.ElapsedMilliseconds < 10000) { Flush(window); Thread.Sleep(2); }
        if (!ready()) throw new TimeoutException("Publication UI did not finish"); Flush(window);
    }
    private static void Wait(Window window, Task task) { Until(window, () => task.IsCompleted); task.GetAwaiter().GetResult(); Flush(window); }
    private static void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException("Publication UI: " + message); }
}
