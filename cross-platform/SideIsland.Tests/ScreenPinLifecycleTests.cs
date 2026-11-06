using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SideIsland.Desktop;
using SideIsland.Desktop.Platforms;

namespace SideIsland.Tests;

internal static class ScreenPinLifecycleTests
{
    private static int checks;
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
    internal static void Run()
    {
        checks = 0;
        string directory = Path.Combine(Path.GetTempPath(), "side-island-pin-lifecycle-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try { Task.Run(() => FilePublication(directory)).GetAwaiter().GetResult(); CaptureAndDecode(); OpenAndClose(); SaveUi(directory); }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine($"PASS: {checks} screenshot lifecycle/publication checks; single pending capture/open/decode, late cancellation/disposal, actual bounded PNG snapshots, new-file-only atomic PNG, real temporary targets and injected selectors; no desktop capture or native picker.");
    }
    private static async Task FilePublication(string directory)
    {
        string source = Path.Combine(directory, "原图.png"); File.WriteAllBytes(source, Png);
        byte[] snapshot = await ScreenCaptureService.ReadPngSnapshotAsync(source, default); File.WriteAllBytes(source, new byte[40]);
        using (var decodedSnapshot = new Bitmap(new MemoryStream(snapshot, false))) Check(decodedSnapshot.PixelSize.Width == 1, "snapshot remains a valid original after path replacement");
        bool corrupt = false; try { using var decoded = await ScreenPinImageFiles.DecodeAsync(source, default); } catch (InvalidDataException) { corrupt = true; } Check(corrupt, "current corrupt file is rejected before native decode");
        File.WriteAllBytes(source, Png);
        using var image = await ScreenPinImageFiles.DecodeAsync(source, default);
        string output = Path.Combine(directory, "new.png"); ScreenPinImageFiles.SaveNew(image, output, default);
        using (var saved = await ScreenPinImageFiles.DecodeAsync(output, default)) Check(saved.PixelSize == image.PixelSize, "actual published PNG has original pixels");
        using var encoded = new MemoryStream(); image.Save(encoded, PngBitmapEncoderOptions.Default);
        Check(File.ReadAllBytes(output).SequenceEqual(encoded.ToArray()), "saved PNG has exactly encoded bytes and no old trailing data");
        string byteOutput = Path.Combine(directory, "encoded-bytes.png"); ScreenPinImageFiles.SaveNew(Png, byteOutput, default);
        Check(File.ReadAllBytes(byteOutput).SequenceEqual(Png), "encoded byte overload publishes exactly one complete PNG without re-encoding");
        using (var savedBytes = await ScreenPinImageFiles.DecodeAsync(byteOutput, default)) Check(savedBytes.PixelSize == image.PixelSize, "encoded byte publication is an actual decodable PNG");
        Reject<IOException>(() => ScreenPinImageFiles.SaveNew(Png, byteOutput, default), "encoded byte overload refuses existing target");
        Check(File.ReadAllBytes(byteOutput).SequenceEqual(Png), "refused byte publication preserves existing PNG exactly");
        byte[] original = Png.Concat(Enumerable.Repeat((byte)0x6a, 4096)).ToArray(); File.WriteAllBytes(source, original);
        Reject<IOException>(() => ScreenPinImageFiles.SaveNew(image, source, default), "original or existing file cannot be overwritten");
        Check(File.ReadAllBytes(source).SequenceEqual(original), "refused existing image preserves every byte including old tail");
        Reject<IOException>(() => ScreenPinImageFiles.SaveNew(image, directory, default), "existing directory cannot be replaced");
        string failed = Path.Combine(directory, "failed.png");
        Reject<IOException>(() => ScreenPinImageFiles.PublishNew(failed, stream => { stream.Write(Png); throw new IOException("encoder fixture failed"); }, default), "failed encoding cannot publish partial file");
        Check(!File.Exists(failed), "failed encoder leaves final path absent");
        string raced = Path.Combine(directory, "raced.png");
        Reject<IOException>(() => ScreenPinImageFiles.PublishNew(raced, stream => { stream.Write(Png); File.WriteAllText(raced, "other writer"); }, default), "late-created final target defeats non-overwriting publication");
        Check(File.ReadAllText(raced) == "other writer", "late-created target is never replaced or deleted");
        using (var cancel = new CancellationTokenSource())
        {
            string cancelled = Path.Combine(directory, "cancelled.png");
            Reject<OperationCanceledException>(() => ScreenPinImageFiles.PublishNew(cancelled, stream => { stream.Write(Png); cancel.Cancel(); }, cancel.Token), "cancel during encoder suppresses publication");
            Check(!File.Exists(cancelled), "cancelled encoder leaves no final image");
            Reject<OperationCanceledException>(() => ScreenPinImageFiles.SaveNew(image, cancelled, cancel.Token), "pre-cancelled encoder never starts");
            Reject<OperationCanceledException>(() => ScreenPinImageFiles.SaveNew(Png, cancelled, cancel.Token), "pre-cancelled byte publication never starts");
        }
        string concurrent = Path.Combine(directory, "concurrent.png"); int won = 0, refused = 0;
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => { using var own = Bitmap(); try { ScreenPinImageFiles.SaveNew(own, concurrent, default); Interlocked.Increment(ref won); } catch (IOException) { Interlocked.Increment(ref refused); } })));
        Check(won == 1 && refused == 5 && File.ReadAllBytes(concurrent).SequenceEqual(encoded.ToArray()), "concurrent writers publish one complete PNG");
        foreach (bool dangling in new[] { false, true })
        {
            string link = Path.Combine(directory, dangling ? "dangling.png" : "link.png"), target = dangling ? Path.Combine(directory, "missing.png") : source;
            try { File.CreateSymbolicLink(link, target); }
            catch (Exception error) when (error is UnauthorizedAccessException or PlatformNotSupportedException or IOException) { Console.WriteLine("SKIP: screenshot symlink fixture unavailable: " + error.GetType().Name); continue; }
            try { Reject<IOException>(() => ScreenPinImageFiles.SaveNew(image, link, default), "symlink destination cannot be replaced"); Check((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0 && (dangling ? !File.Exists(target) : File.ReadAllBytes(target).SequenceEqual(original)), "symlink identity and target remain intact"); }
            finally { File.Delete(link); }
        }
        string oversized = Path.Combine(directory, "oversized.png"); using (var stream = File.Create(oversized)) stream.SetLength(ScreenCaptureService.MaximumPngBytes + 1);
        bool sizeRejected = false; try { _ = await ScreenCaptureService.ReadPngSnapshotAsync(oversized, default); } catch (InvalidDataException) { sizeRejected = true; } Check(sizeRejected, "snapshot rejects a file over 64 MiB before allocating/decoding"); File.Delete(oversized);
        Check(Directory.GetFiles(directory, ".side-island-pin-*.tmp").Length == 0, "success, encoder error, late collision and cancellation clean only owned staging files");
    }
    private static void CaptureAndDecode()
    {
        using var fixture = new Fixture(); var window = fixture.Window;
        Click(Find<Button>(window, "pin-capture")); Task operation = window.LatestScreenPinOperation;
        Wait(window, () => fixture.Captures.Count == 1);
        Check(!window.IsVisible, "island is hidden while injected system capture is pending");
        string owned = fixture.Owned("capture-one.png"); fixture.Captures[0].Reply.SetResult(new(ScreenCaptureStatus.Completed, new ScreenCaptureFile(owned), "fixture", null));
        Wait(window, () => fixture.Decodes.Count == 1);
        Check(window.IsVisible && !Find<Button>(window, "pin-capture").IsEnabled && !Find<Button>(window, "pin-open").IsEnabled, "restored island retains global guard through delayed decode");
        window.SelectToolForTest("notes"); window.SelectToolForTest("screen-pin"); Flush(window);
        AppearanceTestActions.Set(window, "zh", false); AppearanceTestActions.Set(window, "en", true);
        Click(Find<Button>(window, "pin-capture")); Click(Find<Button>(window, "pin-open"));
        Check(fixture.Captures.Count == 1 && fixture.Choosers.Count == 0 && !Find<Button>(window, "pin-capture").IsEnabled, "navigation and real appearance rebuild cannot start duplicate capture or file picker");
        Click(Find<Button>(window, "pin-cancel")); var late = Bitmap(); fixture.Decodes[0].Reply.SetResult(late); Await(window, operation);
        Check(fixture.Decodes[0].Token.IsCancellationRequested && Pins(window).Count == 0 && Disposed(late) && !File.Exists(owned), "cancel during decode suppresses late pin and releases bitmap plus owned file");
        Check(Find<Button>(window, "pin-capture").IsEnabled && Find<Button>(window, "pin-open").IsEnabled, "drained cancellation restores current-page controls");
        Click(Find<Button>(window, "pin-capture")); operation = window.LatestScreenPinOperation; Wait(window, () => fixture.Captures.Count == 2);
        // A provider may finish after cancellation; its generated file still belongs to this operation.
        Invoke(window, "CancelPinOperation"); owned = fixture.Owned("capture-late.png"); fixture.Captures[1].Reply.SetResult(new(ScreenCaptureStatus.Completed, new ScreenCaptureFile(owned), "fixture", null)); Await(window, operation);
        Check(!File.Exists(owned) && fixture.Decodes.Count == 1 && Pins(window).Count == 0 && window.IsVisible, "late capture reply is cleaned without decoding and island restores");
        Click(Find<Button>(window, "pin-capture")); operation = window.LatestScreenPinOperation; Wait(window, () => fixture.Captures.Count == 3);
        owned = fixture.Owned("capture-success.png"); fixture.Captures[2].Reply.SetResult(new(ScreenCaptureStatus.Completed, new ScreenCaptureFile(owned), "fixture", null)); Wait(window, () => fixture.Decodes.Count == 2);
        window.SelectToolForTest("notes"); var active = Bitmap(); fixture.Decodes[1].Reply.SetResult(active); Await(window, operation);
        Check(Pins(window).Count == 1 && !File.Exists(owned) && Field<string>(window, "selected") == "notes", "explicit pin finishes across navigation without replacing active page");
        window.SelectToolForTest("screen-pin"); Flush(window); Click(Find<Button>(window, "pin-close-all")); Check(Pins(window).Count == 0 && Disposed(active), "close all releases the actual pin bitmap");
    }
    private static void OpenAndClose()
    {
        using var fixture = new Fixture(); var window = fixture.Window; string source = fixture.Owned("borrowed-user.png");
        Click(Find<Button>(window, "pin-open")); Task operation = window.LatestScreenPinOperation; Check(fixture.Choosers.Count == 1, "actual open button starts one selector");
        window.SelectToolForTest("notes"); window.SelectToolForTest("screen-pin"); Flush(window); Click(Find<Button>(window, "pin-open")); Check(fixture.Choosers.Count == 1 && !Find<Button>(window, "pin-open").IsEnabled, "pending file picker guard spans page reconstruction");
        Click(Find<Button>(window, "pin-close-all")); fixture.Choosers[0].Reply.SetResult(source); Await(window, operation);
        Check(fixture.Choosers[0].Token.IsCancellationRequested && fixture.Decodes.Count == 0 && File.ReadAllBytes(source).SequenceEqual(Png), "close all cancels late file selection and preserves borrowed original");
        Click(Find<Button>(window, "pin-open")); operation = window.LatestScreenPinOperation; fixture.Choosers[1].Reply.SetResult(source); Wait(window, () => fixture.Decodes.Count == 1);
        window.Close(); var late = Bitmap(); fixture.Decodes[0].Reply.SetResult(late); Await(window, operation);
        Check(fixture.Decodes[0].Token.IsCancellationRequested && Disposed(late) && Pins(window).Count == 0 && !window.IsVisible && File.ReadAllBytes(source).SequenceEqual(Png), "actual app close cancels decode, disposes late image and never restores island or changes source");
    }
    private static void SaveUi(string directory)
    {
        var chooser = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously); int selections = 0, writes = 0; CancellationToken selectedToken = default;
        string target = Path.Combine(directory, "pin-ui.png"), original = Path.Combine(directory, "pin-original.png"); File.WriteAllBytes(original, Png);
        ScreenPinSaveSources sources = new() { ChooseDestination = token => { selectedToken = token; selections++; return chooser.Task; }, SavePng = (bitmap, path, token) => { writes++; return Task.Run(() => ScreenPinImageFiles.SaveNew(bitmap, path, token), token); } };
        var firstImage = Bitmap(); var pin = new ScreenPinWindow(firstImage, "en", false, true, sources, original);
        try
        {
            pin.Show(); Flush(pin); Click(Find<Button>(pin, "pin-save")); Task save = pin.LatestSave;
            Click(Find<Button>(pin, "pin-save")); Invoke(pin, "RequestSave");
            Check(selections == 1 && ReferenceEquals(save, pin.LatestSave) && !Find<Button>(pin, "pin-save").IsEnabled, "buttons/menu/hotkeys share one pending save and stable task identity");
            pin.Close(); chooser.SetResult(target); Await(pin, save);
            Check(selectedToken.IsCancellationRequested && writes == 0 && !File.Exists(target) && Disposed(firstImage), "pin close during picker suppresses late save and releases image");
        }
        finally { pin.Close(); Flush(pin); }
        chooser = new(TaskCreationOptions.RunContinuationsAsynchronously); var secondImage = Bitmap();
        pin = new ScreenPinWindow(secondImage, "en", true, true, sources, original);
        try
        {
            pin.Show(); Flush(pin); Click(Find<Button>(pin, "pin-save")); File.Delete(original); chooser.SetResult(original); Await(pin, pin.LatestSave);
            Check(writes == 0 && !File.Exists(original) && Field<TextBlock>(pin, "message").Text!.Contains("original", StringComparison.Ordinal), "source path remains protected if original disappears while picker is open");
        }
        finally { pin.Close(); Flush(pin); }
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); CancellationToken writingToken = default;
        var thirdImage = Bitmap(); pin = new ScreenPinWindow(thirdImage, "en", false, true, new() { ChooseDestination = _ => Task.FromResult<string?>(target), SavePng = async (bitmap, path, token) => { writingToken = token; entered.SetResult(); await release.Task; token.ThrowIfCancellationRequested(); await Task.Run(() => ScreenPinImageFiles.SaveNew(bitmap, path, token)); } }, null);
        try
        {
            pin.Show(); Flush(pin); Click(Find<Button>(pin, "pin-save")); Task save = pin.LatestSave; Wait(pin, () => entered.Task.IsCompleted);
            bool dispatched = false; Dispatcher.UIThread.Post(() => dispatched = true); Wait(pin, () => dispatched); Check(!Disposed(thirdImage), "in-flight encode keeps image alive while UI remains responsive");
            pin.Close(); Check(writingToken.IsCancellationRequested, "pin close cancels in-flight encoder"); release.SetResult(); Await(pin, save); Check(!File.Exists(target) && Disposed(thirdImage), "cancelled writer publishes no image and releases deferred bitmap");
        }
        finally { release.TrySetResult(); pin.Close(); Flush(pin); }
        var finalImage = Bitmap(); pin = new ScreenPinWindow(finalImage, "zh", false, true, new() { ChooseDestination = _ => Task.FromResult<string?>(target) }, null);
        try
        {
            pin.Show(); Flush(pin); pin.ChangeZoom(4); Click(Find<Button>(pin, "pin-save")); Await(pin, pin.LatestSave);
            using var saved = new Bitmap(target); Check(saved.PixelSize == finalImage.PixelSize && Find<Button>(pin, "pin-save").IsEnabled, "actual default writer saves original pixels after preview zoom and restores action");
            byte[] before = File.ReadAllBytes(target); Click(Find<Button>(pin, "pin-save")); Await(pin, pin.LatestSave); Check(File.ReadAllBytes(target).SequenceEqual(before) && Field<TextBlock>(pin, "message").Text!.StartsWith("保存失败", StringComparison.Ordinal), "actual save handler refuses existing file with localized feedback");
        }
        finally { pin.Close(); Flush(pin); }
        using var demoImage = Bitmap(); var demo = new ScreenPinWindow(demoImage, "en", false, true);
        try { demo.Show(); Flush(demo); Click(Find<Button>(demo, "pin-save")); Check(demo.LatestSave.IsCompletedSuccessfully && !Field<bool>(demo, "saving"), "demo save without injection does not start a system selector or filesystem work"); }
        finally { demo.Close(); Flush(demo); }
    }

    private sealed class Pending<T>(CancellationToken token) { internal readonly CancellationToken Token = token; internal readonly TaskCompletionSource<T> Reply = new(TaskCreationOptions.RunContinuationsAsynchronously); }
    private sealed class Fixture : IDisposable
    {
        internal readonly List<Pending<ScreenCaptureResult>> Captures = []; internal readonly List<Pending<string?>> Choosers = []; internal readonly List<Pending<Bitmap>> Decodes = [];
        private readonly string directory = Path.Combine(Path.GetTempPath(), "side-island-pin-ui-" + Guid.NewGuid().ToString("N"));
        internal readonly IslandWindow Window;
        internal Fixture()
        {
            Directory.CreateDirectory(directory);
            IntegrationSources sources = new(_ => throw new InvalidOperationException("Unexpected media"), (_, _) => throw new InvalidOperationException("Unexpected control"), (_, _) => throw new InvalidOperationException("Unexpected quota")) { ScreenPins = new() {
                Capture = token => { var item = new Pending<ScreenCaptureResult>(token); Captures.Add(item); return item.Reply.Task; },
                ChoosePng = token => { var item = new Pending<string?>(token); Choosers.Add(item); return item.Reply.Task; },
                DecodePng = (_, token) => { var item = new Pending<Bitmap>(token); Decodes.Add(item); return item.Reply.Task; }
            } };
            Window = new(true, directory, sources); Window.Show(); Window.Expand(); AppearanceTestActions.Set(Window, "en", false); Window.SelectToolForTest("screen-pin"); Flush(Window);
        }
        internal string Owned(string name) { string path = Path.Combine(directory, name); File.WriteAllBytes(path, Png); return path; }
        public void Dispose()
        {
            Window.Close(); foreach (var item in Captures) item.Reply.TrySetResult(new(ScreenCaptureStatus.Cancelled, null, "fixture", null)); foreach (var item in Choosers) item.Reply.TrySetResult(null);
            foreach (var item in Decodes) if (!item.Reply.Task.IsCompleted) item.Reply.SetResult(Bitmap());
            Await(Window, Window.LatestScreenPinOperation); Flush(Window); Directory.Delete(directory, true);
        }
    }
    private static Bitmap Bitmap() => new(new MemoryStream(Png, false));
    private static bool Disposed(Bitmap bitmap) { try { using var output = new MemoryStream(); bitmap.Save(output, PngBitmapEncoderOptions.Default); return false; } catch (ObjectDisposedException) { return true; } }
    private static List<ScreenPinWindow> Pins(IslandWindow window) => Field<List<ScreenPinWindow>>(window, "screenPins");
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Invoke(object value, string name) => value.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(value, null);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Flush(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
    private static void Wait(Window window, Func<bool> condition) { var elapsed = Stopwatch.StartNew(); while (!condition() && elapsed.Elapsed < TimeSpan.FromSeconds(10)) { Flush(window); Thread.Sleep(3); } Flush(window); Check(condition(), "controlled screenshot operation completed within bound"); }
    private static void Await(Window window, Task task) { Wait(window, () => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void Reject<T>(Action action, string message) where T : Exception { bool rejected = false; try { action(); } catch (T) { rejected = true; } Check(rejected, message); }
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException("Screen pin lifecycle: " + message); }
}
