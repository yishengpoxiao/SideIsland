using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CodexIsland;
using SideIsland.Core;
using SideIsland.Desktop;
using SideIsland.Desktop.Platforms;

namespace SideIsland.Tests;

internal static class BatchHashUiTests
{
    private static int checks;
    public static void Run(string? artifacts = null)
    {
        checks = 0; RealFilesAndExport(artifacts); CancellationAndRebuild(); DelayedPicker(); ExportOutcomes();
        Console.WriteLine($"PASS: {checks} batch checksum UI checks, owned real files, cancellation, export and 96 populated zh/en/theme/size/headless-DPI layouts. No target-native desktop claim.");
    }
    private static void RealFilesAndExport(string? artifacts)
    {
        using var f = new Fixture(); var w = f.Window;
        f.Choose(); var expected = Find<TextBox>(w, "batch-hash-expected");
        expected.Text = FileHashTools.CreateManifest(f.Digest(f.First), "SHA256", f.First) + FileHashTools.CreateManifest(f.Digest(f.Second), "SHA256", f.Second);
        f.Compute(); Check(Texts(w).Count(text => text == "Checksum matches") == 2, "two real files match their exact Unicode GNU entries");
        Check(Find<Button>(w, "batch-hash-save").IsEnabled, "complete unique results exportable");
        f.Act("batch-hash-copy"); Check(f.Copied == expected.Text, "combined manifest copied exactly");
        string target = f.Path("saved.sha256"); f.Destination = target; f.Act("batch-hash-save");
        Check(File.ReadAllText(target) == expected.Text, "manifest saved to real new file");
        File.WriteAllText(target, "external existing content"); f.Act("batch-hash-save");
        Check(File.ReadAllText(target) == "external existing content" && Find<TextBlock>(w, "batch-hash-status").Text!.Contains("Not saved", StringComparison.Ordinal), "existing destination remains byte-for-byte unchanged and feedback is truthful");
        byte[] original = File.ReadAllBytes(f.First); File.Delete(f.First); f.Destination = f.First; f.Act("batch-hash-save");
        Check(!File.Exists(f.First) && Find<TextBlock>(w, "batch-hash-status").Text!.Contains("source file paths", StringComparison.Ordinal), "export never recreates a disappeared source path with unrelated manifest bytes");
        File.WriteAllBytes(f.First, original);
        expected.Text = new string('0', 64); f.Compute();
        Check(Texts(w).Count(text => text == "Multiple files require a list with filenames") == 2, "single digest is not applied to every selected file");
        expected.Text = ""; Find<ComboBox>(w, "batch-hash-algorithm").SelectedIndex = 1; f.Compute();
        Check(w.GetVisualDescendants().OfType<TextBox>().Count(box => box.Text?.Length == 128) == 2, "real SHA512 digests retained");
        f.Selection = [f.First, f.Path("missing.txt")]; f.Choose(); f.Compute();
        Check(Texts(w).Contains("File not found") && Texts(w).Contains("Calculated") && !Find<Button>(w, "batch-hash-save").IsEnabled, "per-file failure retains successful digest and blocks incomplete manifest");
        string duplicate = f.Path("other/下载 文件.txt"); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(duplicate)!); File.WriteAllText(duplicate, "other");
        f.Selection = [f.First, duplicate]; f.Choose(); expected.Text = FileHashTools.CreateManifest(f.Digest(f.First), "SHA256", f.First); Find<ComboBox>(w, "batch-hash-algorithm").SelectedIndex = 0; f.Compute();
        Check(Texts(w).Count(text => text.Contains("Selected filenames are duplicated", StringComparison.Ordinal)) == 2 && !Find<Button>(w, "batch-hash-copy").IsEnabled, "same basenames are never presented as unique manifest matches");
        foreach (string language in new[] { "en", "zh" }) foreach (bool light in new[] { false, true })
        foreach (int width in new[] { 320, 480, 760 }) foreach (int height in new[] { 360, 720 }) foreach (double scale in new[] { 1d, 1.25, 1.5, 2 })
        {
            AppearanceTestActions.Set(w, language, light); w.SetRenderScaling(scale); w.Width = width; w.Height = height; Flush(w);
            Check(Texts(w).Any(text => text.Contains(language == "en" ? "Selected filenames are duplicated" : "所选文件重名", StringComparison.Ordinal)), "retained results relocalize after actual appearance action");
            foreach (var control in w.GetVisualDescendants().OfType<Control>().Where(c => c.IsVisible && c.Name?.StartsWith("batch-hash-", StringComparison.Ordinal) == true))
            {
                var location = control.TranslatePoint(default, w);
                Check(location is Point p && p.X >= -1 && p.X + control.Bounds.Width <= width + 1, $"batch controls fit window width: {control.Name}, {location}, {control.Bounds}, {language}/{light}/{width}/{height}/{scale}");
            }
            foreach (string[] pair in new[] { new[] { "batch-hash-choose", "batch-hash-clear" }, new[] { "batch-hash-compute", "batch-hash-cancel" }, new[] { "batch-hash-copy", "batch-hash-save" } })
            {
                var first = Find<Button>(w, pair[0]); var second = Find<Button>(w, pair[1]);
                Check(Math.Abs(first.Bounds.Width - second.Bounds.Width) < 1 && Math.Abs(first.Bounds.Height - second.Bounds.Height) < 1 && Math.Abs(first.Bounds.Y - second.Bounds.Y) < 1, "action pairs share size and alignment");
                foreach (var button in new[] { first, second }) if (button.Content is TextBlock caption) Check(caption.Bounds.Height + 1 >= caption.DesiredSize.Height, "wrapped button caption not clipped");
            }
            if (artifacts is not null && !light && width == 320 && height == 720 && scale == 1)
            {
                Directory.CreateDirectory(artifacts); using var image = new RenderTargetBitmap(new(width, height), new(96, 96)); image.Render(w); image.Save(System.IO.Path.Combine(artifacts, $"batch-checksums-{language}.png"), PngBitmapEncoderOptions.Default);
            }
        }
        AppearanceTestActions.Set(w, "en", false); f.Selection = Enumerable.Repeat(f.First, 201).ToArray(); f.Choose();
        Check(Find<TextBlock>(w, "batch-hash-status").Text!.Contains("up to 200", StringComparison.Ordinal), "oversized selection is rejected without truncation");
        Click(Find<Button>(w, "batch-hash-clear")); Check(!Find<Button>(w, "batch-hash-compute").IsEnabled && !Find<Button>(w, "batch-hash-save").IsEnabled, "clear removes results and disables actions");
    }
    private static void CancellationAndRebuild()
    {
        using var f = new Fixture(); var w = f.Window; f.Choose(); f.PauseAfterFirst = true;
        Click(Find<Button>(w, "batch-hash-compute")); var running = w.LatestBatchChecksumOperation;
        WaitUntil(w, () => f.FirstDone.IsSet); Check(!Find<Button>(w, "batch-hash-choose").IsEnabled, "pending run locks selection");
        Click(Find<Button>(w, "batch-hash-cancel")); Wait(w, running);
        Check(Texts(w).Contains("Calculated") && Find<TextBlock>(w, "batch-hash-status").Text!.StartsWith("Cancelled", StringComparison.Ordinal), "cancel retains completed first file instead of discarding batch");
        Check(!Find<Button>(w, "batch-hash-save").IsEnabled && Find<Button>(w, "batch-hash-compute").IsEnabled, "partial result cannot export but can rerun");
        f.FirstDone.Reset(); Click(Find<Button>(w, "batch-hash-compute")); running = w.LatestBatchChecksumOperation; WaitUntil(w, () => f.FirstDone.IsSet);
        AppearanceTestActions.Set(w, "zh", true); Wait(w, running);
        Check(Find<TextBlock>(w, "batch-hash-status").Text!.StartsWith("已取消", StringComparison.Ordinal) && Texts(w).Contains("已计算"), "appearance rebuild cancels old work and displays retained results in new language");
        f.PauseAfterFirst = false; f.Compute(); Check(Find<Button>(w, "batch-hash-save").IsEnabled, "new run works after cancellation and rebuild");
        Click(Find<Button>(w, "hash-single-mode")); Check(Find<TextBox>(w, "hash-path") is not null, "single file tool remains accessible");
        Click(Find<Button>(w, "hash-batch-mode")); Check(Find<Button>(w, "batch-hash-save").IsEnabled, "batch results survive switching modes");
    }
    private static void DelayedPicker()
    {
        using var f = new Fixture(); var w = f.Window;
        f.DelayedSelection = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Click(Find<Button>(w, "batch-hash-choose")); Task pending = w.LatestBatchChecksumOperation;
        w.SelectToolForTest("notes"); w.SelectToolForTest("hash"); Flush(w);
        Check(!Find<Button>(w, "batch-hash-choose").IsEnabled, "window-level picker guard survives page rebuild");
        f.DelayedSelection.SetResult([f.First]); Wait(w, pending);
        Check(string.IsNullOrEmpty(Find<TextBox>(w, "batch-hash-paths").Text), "late selection cannot modify rebuilt page");
        f.DelayedSelection = null; f.Choose(); f.Compute();
        f.DelayedSave = new(TaskCreationOptions.RunContinuationsAsynchronously); Click(Find<Button>(w, "batch-hash-save")); pending = w.LatestBatchChecksumOperation;
        string target = f.Path("stale.sha256"); AppearanceTestActions.Set(w, "zh", true); f.DelayedSave.SetResult(target); Wait(w, pending);
        Check(!File.Exists(target) && Find<Button>(w, "batch-hash-save").IsEnabled, "late export picker cancelled by rebuild cannot publish stale data");
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly string DirectoryPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "side-island-batch-hash-ui-" + Guid.NewGuid().ToString("N"));
        internal readonly IslandWindow Window;
        internal readonly string First, Second;
        internal string[] Selection; internal string? Copied, Destination;
        internal TaskCompletionSource<IReadOnlyList<string>>? DelayedSelection;
        internal TaskCompletionSource<string?>? DelayedSave;
        internal bool PauseAfterFirst;
        internal Func<string, string, CancellationToken, Task>? SaveWriter;
        internal readonly ManualResetEventSlim FirstDone = new();
        internal Fixture()
        {
            Directory.CreateDirectory(DirectoryPath); First = Path("下载 文件.txt"); Second = Path("second.txt"); File.WriteAllText(First, "abc"); File.WriteAllText(Second, "内容 😀"); Selection = [First, Second];
            var sources = new IntegrationSources(_ => Task.FromResult(new MediaSnapshot(false, "", "", "", null, null, null, false, null)), (_, _) => Task.FromResult<string?>(null), (_, _) => throw new InvalidOperationException("No quota in batch checksum tests"))
            {
                ChooseBatchChecksumFiles = () => DelayedSelection?.Task ?? Task.FromResult<IReadOnlyList<string>>(Selection),
                ChooseBatchChecksumDestination = _ => DelayedSave?.Task ?? Task.FromResult(Destination),
                WriteChecksumText = text => { Copied = text; return Task.CompletedTask; },
                SaveChecksumManifest = (path, text, token) => SaveWriter is { } writer ? writer(path, text, token) : Task.Run(() => FileHashTools.SaveManifestNew(path, text, (target, bytes) => PortableFilePublication.WriteNew(target, bytes, token))),
                ComputeBatchChecksums = (paths, kind, expected, token, progress) => Task.Run(() => PortableBatchHash.Run(paths, kind, expected, token, progress, item => { if (PauseAfterFirst && item.Index == 0) { FirstDone.Set(); token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10)); } }))
            };
            Window = new IslandWindow(true, Path("settings"), sources); Window.Show(); Window.Expand(); AppearanceTestActions.Set(Window, "en", false); Window.SelectToolForTest("hash"); Click(Find<Button>(Window, "hash-batch-mode")); Flush(Window);
        }
        internal string Path(string name) => System.IO.Path.Combine(DirectoryPath, name);
        internal string Digest(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        internal void Choose() => Act("batch-hash-choose");
        internal void Compute() => Act("batch-hash-compute");
        internal void Act(string name) { var control = Find<Button>(Window, name); Check(control.IsEnabled, name + " available"); Click(control); Wait(Window, Window.LatestBatchChecksumOperation); }
        public void Dispose()
        {
            DelayedSelection?.TrySetResult([]); DelayedSave?.TrySetResult(null); Window.Close(); Wait(Window, Window.LatestBatchChecksumOperation); FirstDone.Dispose();
            string full = System.IO.Path.GetFullPath(DirectoryPath), parent = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()).TrimEnd(System.IO.Path.DirectorySeparatorChar);
            if (!string.Equals(System.IO.Path.GetDirectoryName(full), parent, StringComparison.OrdinalIgnoreCase) || !System.IO.Path.GetFileName(full).StartsWith("side-island-batch-hash-ui-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected fixture path");
            Directory.Delete(full, true);
        }
    }
    private static void ExportOutcomes()
    {
        foreach (var state in new[] { FilePublicationState.NotPublished, FilePublicationState.OutcomeUnknown })
        {
            using var f = new Fixture(); var w = f.Window; f.Choose(); f.Compute();
            string target = f.Path("unconfirmed.sha256"), retained = f.Path("retained.tmp"); f.Destination = target;
            f.SaveWriter = (path, text, token) => { File.WriteAllText(retained, text); throw new FilePublicationException(state, path, retained, "Controlled publication failure after writing actual retained file"); };
            f.Act("batch-hash-save");
            Check(Find<TextBlock>(w, "batch-hash-status").Text!.Contains(retained, StringComparison.Ordinal) && File.Exists(retained) && !File.Exists(target), "retained copy is real and its path is shown for " + state);
            AppearanceTestActions.Set(w, "zh", true); Check(Find<TextBlock>(w, "batch-hash-status").Text!.Contains(retained, StringComparison.Ordinal), "retained result paths survive relocalization");
        }
        foreach (bool close in new[] { false, true })
        {
            using var f = new Fixture(); var w = f.Window; f.Choose(); f.Compute(); string target = f.Path("committed.sha256"); f.Destination = target;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken seen = default;
            f.SaveWriter = async (path, text, token) => { seen = token; FileHashTools.SaveManifestNew(path, text, (p, bytes) => PortableFilePublication.WriteNew(p, bytes)); entered.SetResult(); await gate.Task; };
            Click(Find<Button>(w, "batch-hash-save")); Task pending = w.LatestBatchChecksumOperation; Wait(w, entered.Task);
            if (close) w.Close(); else AppearanceTestActions.Set(w, "zh", true);
            Check(seen.IsCancellationRequested, "close/rebuild cancels pending export token"); gate.SetResult(); Wait(w, pending);
            Check(File.Exists(target), "a confirmed commit is never undone by later cancellation");
            if (!close) Check(Find<TextBlock>(w, "batch-hash-status").Text == "校验清单已另存为新文件。", "confirmed save is not misreported as cancelled after rebuild");
        }
    }
    private static List<string> Texts(Window w) => w.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text ?? "").ToList();
    private static T Find<T>(Window w, string name) where T : Control => w.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs(); }
    private static void Flush(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
    private static void Wait(Window w, Task task) { WaitUntil(w, () => task.IsCompleted); task.GetAwaiter().GetResult(); Flush(w); }
    private static void WaitUntil(Window w, Func<bool> done) { var clock = Stopwatch.StartNew(); while (!done() && clock.Elapsed < TimeSpan.FromSeconds(15)) { Flush(w); Thread.Sleep(2); } Check(done(), "operation settled before deadline"); Flush(w); }
    private static void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException("Batch checksum UI: " + message); }
}
