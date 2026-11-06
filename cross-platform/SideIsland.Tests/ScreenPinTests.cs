using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SideIsland.Desktop;
using SideIsland.Desktop.Platforms;
using Capture = SideIsland.Desktop.Platforms.ScreenCaptureService;

namespace SideIsland.Tests;

internal static class ScreenPinTests
{
    private static int checks;
    // A real 1x1 PNG; tests never read the desktop or ask for native consent.
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
    public static void Run()
    {
        checks = 0;
        State(); Arguments(); Task.Run(CaptureBehaviorAsync).GetAwaiter().GetResult(); PinLayouts(); ScreenPinLifecycleTests.Run();
        Console.WriteLine($"PASS: {checks} screenshot pin behavior and actual headless layouts; owned PNG cleanup, portal cancellation/permission policy, safe command arguments, zoom/opacity, zh/en and light/dark. No macOS/Ubuntu capture or compositor claim.");
    }
    private static void State()
    {
        var state = new ScreenPinState();
        Near(state.Zoom, 1, "initial zoom"); Near(state.Alpha, 1, "initial alpha");
        state.ZoomBy(1); Check(state.Zoom > 1, "wheel zoom increases"); state.ZoomBy(-1); Near(state.Zoom, 1, "opposite wheel restores zoom");
        state.SetZoom(200); Near(state.Zoom, 4, "zoom upper bound"); state.SetZoom(-200); Near(state.Zoom, .05, "zoom lower bound");
        state.SetZoom(double.NaN); Near(state.Zoom, 1, "invalid zoom reset"); state.ZoomBy(double.PositiveInfinity); Near(state.Zoom, 1, "invalid wheel ignored");
        state.SetAlpha(0); Near(state.Alpha, .2, "close controls remain visible"); state.SetAlpha(4); Near(state.Alpha, 1, "alpha upper bound"); state.SetAlpha(double.NaN); Near(state.Alpha, 1, "invalid alpha reset");
        Near(ScreenPinState.FitZoom(2000, 1000, 500, 500), .25, "landscape fits width");
        Near(ScreenPinState.FitZoom(1000, 2000, 500, 500), .25, "portrait fits height");
        Near(ScreenPinState.FitZoom(20, 10, 500, 500), 1, "tiny image is not enlarged automatically");
        Near(ScreenPinState.FitZoom(0, 10, 500, 500), 1, "invalid source dimensions");
    }
    private static void Arguments()
    {
        const string output = "/tmp/image with spaces;$(never execute) 中文.png";
        foreach (var backend in Enum.GetValues<Capture.Backend>())
        {
            var start = Capture.BuildStartInfo(backend, "/usr/bin/test", output);
            Check(!start.UseShellExecute && start.ArgumentList.Last() == output && string.IsNullOrEmpty(start.Arguments), "path is exactly one argument for " + backend);
            Check(start.RedirectStandardError && start.RedirectStandardOutput, "drained command output " + backend);
        }
        Check(Capture.BuildStartInfo(Capture.Backend.MacOS, "/usr/sbin/screencapture", output).ArgumentList.SequenceEqual(new[] { "-i", "-x", "-t", "png", output }), "Mac uses interactive silent PNG capture");
        Check(Capture.BuildStartInfo(Capture.Backend.Gnome, "gnome-screenshot", output).ArgumentList.Contains("--area"), "GNOME selects a region");
        Check(Capture.BuildStartInfo(Capture.Backend.Spectacle, "spectacle", output).ArgumentList.Contains("--region"), "Spectacle selects a region");
        Check(Capture.TryLocalPortalPath(new Uri(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "image 中文.png"))).AbsoluteUri, out var local) && Path.IsPathFullyQualified(local!), "portal accepts an encoded local file URI");
        foreach (string? invalid in new[] { null, "", "https://example.com/a.png", "file://server/share/a.png", "file:///tmp/a.png#fragment", "file:///tmp/a.png?query=1", "../image.png" })
            Check(!Capture.TryLocalPortalPath(invalid, out _), "portal rejects nonlocal or nonfile URI");
    }
    private static async Task CaptureBehaviorAsync()
    {
        string folder = Path.Combine(Path.GetTempPath(), "side-island-pin-test-" + Guid.NewGuid().ToString("N"));
        string temp = Path.Combine(folder, "screen-pin-temp"), source = Path.Combine(folder, "portal-source.png");
        int commands = 0, portals = 0, finds = 0;
        Task<Capture.CommandResult> NeverRun(System.Diagnostics.ProcessStartInfo info, CancellationToken token) => throw new InvalidOperationException("Unexpected process start");
        Task<Capture.PortalResult> NeverPortal(CancellationToken token) => throw new InvalidOperationException("Unexpected portal request");
        string? NeverFind(string name) => throw new InvalidOperationException("Unexpected tool discovery");
        var simulated = new Capture(folder, Capture.CapturePlatform.MacOS, NeverRun, NeverPortal, NeverFind, true);
        Check((await simulated.CaptureRegionAsync()).Status == ScreenCaptureStatus.Cancelled && !Directory.Exists(folder), "test mode touches neither filesystem nor desktop");
        var unsupported = new Capture(folder, Capture.CapturePlatform.Unsupported, NeverRun, NeverPortal, NeverFind);
        Check((await unsupported.CaptureRegionAsync()).Status == ScreenCaptureStatus.Unavailable && !Directory.Exists(folder), "unsupported platform exits without side effects");
        using (var cancelled = new CancellationTokenSource()) { cancelled.Cancel(); Check((await simulated.CaptureRegionAsync(cancelled.Token)).Status == ScreenCaptureStatus.Cancelled, "pre-cancelled request stays silent"); }
        try
        {
            var success = new Capture(folder, Capture.CapturePlatform.MacOS, async (info, token) => { commands++; await File.WriteAllBytesAsync(info.ArgumentList.Last(), Png, token); return new(0, ""); }, NeverPortal, NeverFind);
            var result = await success.CaptureRegionAsync();
            Check(result.Status == ScreenCaptureStatus.Completed && result.Capture is not null && commands == 1, "native adapter validates a generated PNG");
            string owned = result.Capture!.Path;
            Check(Path.GetDirectoryName(owned) == temp && Path.GetFileName(owned).StartsWith("capture-", StringComparison.Ordinal), "capture is generated inside the app's temporary directory");
            result.Capture.Dispose(); result.Capture.Dispose(); Check(!File.Exists(owned), "owned capture is removed exactly once");
            bool disposed = false; try { _ = result.Capture.Path; } catch (ObjectDisposedException) { disposed = true; } Check(disposed, "disposed capture cannot be reused");
            var cancel = new Capture(folder, Capture.CapturePlatform.MacOS, (_, _) => Task.FromResult(new Capture.CommandResult(1, "")), NeverPortal, NeverFind);
            Check((await cancel.CaptureRegionAsync()).Status == ScreenCaptureStatus.Cancelled && Directory.GetFiles(temp).Length == 0, "system selection cancellation is silent and leaves no image");
            var denied = new Capture(folder, Capture.CapturePlatform.MacOS, (_, _) => Task.FromResult(new Capture.CommandResult(1, "Screen Recording permission denied")), NeverPortal, NeverFind);
            Check((await denied.CaptureRegionAsync()).Status == ScreenCaptureStatus.Failed, "permission failure is not reported as success/cancel");
            var corrupt = new Capture(folder, Capture.CapturePlatform.MacOS, async (info, token) => { await File.WriteAllBytesAsync(info.ArgumentList.Last(), new byte[60], token); return new(0, ""); }, NeverPortal, NeverFind);
            Check((await corrupt.CaptureRegionAsync()).Status == ScreenCaptureStatus.Failed && Directory.GetFiles(temp).Length == 0, "corrupt tool output is rejected and removed");
            Directory.CreateDirectory(folder); await File.WriteAllBytesAsync(source, Png);
            var portal = new Capture(folder, Capture.CapturePlatform.Linux, NeverRun, _ => { portals++; return Task.FromResult(new Capture.PortalResult(ScreenCaptureStatus.Completed, source)); }, NeverFind);
            var fromPortal = await portal.CaptureRegionAsync();
            Check(fromPortal.Status == ScreenCaptureStatus.Completed && fromPortal.Capture!.Path != source && portals == 1, "portal image is copied to an owned file");
            fromPortal.Capture!.Dispose(); Check(File.Exists(source) && File.ReadAllBytes(source).SequenceEqual(Png), "portal/user source is never changed or deleted");
            foreach (var status in new[] { ScreenCaptureStatus.Cancelled, ScreenCaptureStatus.Failed })
            {
                var stop = new Capture(folder, Capture.CapturePlatform.Linux, NeverRun, _ => Task.FromResult(new Capture.PortalResult(status, Error: status == ScreenCaptureStatus.Failed ? "Permission denied" : null)), NeverFind);
                Check((await stop.CaptureRegionAsync()).Status == status, "portal cancellation/denial never launches another picker");
            }
            var fallback = new Capture(folder, Capture.CapturePlatform.Linux, async (info, token) => { Check(info.FileName == "/usr/bin/gnome-screenshot", "uses discovered tool path"); await File.WriteAllBytesAsync(info.ArgumentList.Last(), Png, token); return new(0, ""); }, _ => Task.FromResult(new Capture.PortalResult(ScreenCaptureStatus.Unavailable)), name => { finds++; return name == "gnome-screenshot" ? "/usr/bin/gnome-screenshot" : null; });
            var fallbackResult = await fallback.CaptureRegionAsync(); Check(fallbackResult.Status == ScreenCaptureStatus.Completed && finds == 1, "only unavailable portal permits installed-tool fallback"); fallbackResult.Capture!.Dispose();
            var waiting = new Capture(folder, Capture.CapturePlatform.MacOS, async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new(0, ""); }, NeverPortal, NeverFind);
            using var during = new CancellationTokenSource(TimeSpan.FromMilliseconds(10));
            Check((await waiting.CaptureRegionAsync(during.Token)).Status == ScreenCaptureStatus.Cancelled && Directory.GetFiles(temp).Length == 0, "in-flight cancellation leaves no partial capture");
            var largeHeader = Png.ToArray(); System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(largeHeader.AsSpan(16, 4), 50000); await File.WriteAllBytesAsync(source, largeHeader);
            bool rejected = false; try { await Capture.ValidatePngAsync(source, default); } catch (InvalidDataException) { rejected = true; } Check(rejected, "oversized dimensions rejected before bitmap decoding");
        }
        finally
        {
            // Delete only this test's explicitly named files/directories. Never
            // sweep a user's capture folder or a portal-owned source directory.
            if (File.Exists(source)) File.Delete(source);
            if (Directory.Exists(temp)) Directory.Delete(temp, false);
            if (Directory.Exists(folder)) Directory.Delete(folder, false);
        }
    }
    private static void PinLayouts()
    {
        foreach (string language in new[] { "zh", "en" }) foreach (bool light in new[] { false, true })
        foreach (var size in new[] { new Size(250, 190), new Size(320, 260), new Size(800, 600) })
        {
            using var source = new MemoryStream(Png, false);
            var pin = new ScreenPinWindow(new Bitmap(source), language, light, true);
            try
            {
                pin.Show(); pin.Width = size.Width; pin.Height = size.Height; Dispatcher.UIThread.RunJobs(); pin.UpdateLayout();
                var before = pin.Bounds.Size; pin.ChangeZoom(5); Dispatcher.UIThread.RunJobs(); pin.UpdateLayout();
                Check(pin.Bounds.Size == before, "zoom does not jump the native window");
                foreach (var button in pin.GetVisualDescendants().OfType<Button>().Where(button => button.IsVisible && button.Bounds.Width > 0))
                {
                    var point = button.TranslatePoint(default, pin);
                    Check(point is Point p && p.X >= -1 && p.X + button.Bounds.Width <= pin.Bounds.Width + 1, "pin button stays horizontally inside " + language + "/" + size);
                    if (button.Content is TextBlock label) Check(label.Bounds.Height <= button.Bounds.Height + 1, "pin caption is not vertically clipped");
                }
                var image = pin.GetVisualDescendants().OfType<Image>().Single();
                Check(image.Width > 0 && image.Height > 0, "decoded image remains visible after zoom");
                pin.FitImage(); Check(pin.PinState.Zoom <= 1, "fit preserves small image clarity");
            }
            finally { pin.Close(); Dispatcher.UIThread.RunJobs(); }
        }
    }
    private static void Near(double actual, double expected, string message) => Check(Math.Abs(actual - expected) < .000001, message);
    private static void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException("Screen pin: " + message); }
}
