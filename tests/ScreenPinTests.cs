using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CodexIsland
{
    internal static class ScreenPinTests
    {
        public static void Run()
        {
            Int32Rect desktop = new Int32Rect(-1920, -480, 4480, 1920);
            Check(ScreenPinTools.Selection(new Point(600, 300), new Point(-800, -300), desktop) == new Int32Rect(-800, -300, 1400, 600), "reverse selection crosses negative-coordinate monitors");
            Check(ScreenPinTools.Selection(new Point(-4000, -1000), new Point(4000, 3000), desktop) == desktop, "selection clamps to full virtual desktop");
            Check(ScreenPinTools.Selection(new Point(8000, 8000), new Point(9000, 9000), desktop).IsEmpty, "out-of-desktop selection is empty");
            var mapped = ScreenPinTools.ToOverlay(new Int32Rect(-1500, -300, 900, 600), desktop, desktop.Width / 1.5, desktop.Height / 1.5);
            Check(Math.Abs(mapped.X - 280) < .01 && Math.Abs(mapped.Y - 120) < .01 && Math.Abs(mapped.Width - 600) < .01 && Math.Abs(mapped.Height - 400) < .01, "physical pixels map consistently to 150 percent DPI overlay units");
            bool rejected = false; try { ScreenPinTools.ValidateCapture(new Int32Rect(0, 0, 10000, 10000)); } catch (ArgumentException) { rejected = true; } Check(rejected, "excessive screenshot allocation rejected");
            var previous = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try
            {
                int captures = 0, selects = 0; var events = new List<bool>();
                Func<Int32Rect, CancellationToken, BitmapSource> fakeCapture = (rect, token) => { captures++; Check(rect.X == -50 && rect.Y == -20 && rect.Width == 80 && rect.Height == 60, "capture delegate receives exact physical negative coordinates"); return ImageQuickTests.Sample(80, 60, false); };
                Func<CancellationToken, Task<Int32Rect?>> fakeSelect = token => { selects++; return Task.FromResult<Int32Rect?>(new Int32Rect(-50, -20, 80, 60)); };
                using (var panel = new ScreenPinPanel(false, fakeCapture, fakeSelect))
                {
                    panel.CaptureStateChanged += value => events.Add(value); ImageQuickTests.Complete(panel.BeginCaptureAsync());
                    Check(captures == 1 && selects == 1 && panel.PinCount == 1 && !panel.IsCapturing && events.SequenceEqual(new[] { true, false }), "explicit capture creates one own synthetic pin and balanced island hide events");
                    var pin = Application.Current == null ? null : Application.Current.Windows.OfType<ScreenPinWindow>().FirstOrDefault();
                    if (pin != null) { pin.ZoomPercent = 150; pin.OpacityPercent = 45; Check(pin.ZoomPercent == 150 && Math.Abs(pin.Opacity - .45) < .01, "pin supports live zoom and opacity controls"); }
                    panel.Dispose(); Check(panel.PinCount == 0, "disposing closes own screenshot windows");
                }
                using (var canceled = new ScreenPinPanel(false, fakeCapture, token => Task.FromResult<Int32Rect?>(null)))
                {
                    ImageQuickTests.Complete(canceled.BeginCaptureAsync()); Check(canceled.PinCount == 0 && captures == 1 && !canceled.IsCapturing, "Esc-equivalent selection cancellation never reads the screen");
                }
                using (var demo = new ScreenPinPanel(true, fakeCapture, fakeSelect)) { ImageQuickTests.Complete(demo.BeginCaptureAsync()); Check(captures == 1 && selects == 1, "demo construction and activation never capture or show selection"); }
                var disposed = new ScreenPinPanel(false, fakeCapture, fakeSelect); events.Clear(); disposed.CaptureStateChanged += value => events.Add(value); Task work = disposed.BeginCaptureAsync(); disposed.Dispose(); ImageQuickTests.Complete(work);
                Check(disposed.PinCount == 0 && captures == 1 && events.SequenceEqual(new[] { true, false }), "dispose cancels pending capture and restores island state");
                var image = ImageQuickTests.Sample(24, 18, false); var ownWindow = new ScreenPinWindow(image); ownWindow.ZoomPercent = 200; ownWindow.OpacityPercent = 60;
                Check(ownWindow.Snapshot == image && Math.Abs(ownWindow.Opacity - .6) < .01 && ownWindow.ZoomPercent == 200, "standalone pin has memory-only image and adjustable scale/alpha"); ownWindow.Close(); Check(ownWindow.Snapshot == null, "closing pin detaches bitmap source");
                var large = new ScreenPinWindow(ImageQuickTests.Sample(1600, 1000, false));
                try { large.Show(); large.ZoomPercent = 300; large.UpdateLayout(); Check(large.ActualWidth <= SystemParameters.WorkArea.Width && large.ActualHeight <= SystemParameters.WorkArea.Height, "large synthetic screenshot at 300 percent keeps window and toolbar inside working area"); }
                finally { large.Close(); }
                Console.WriteLine("PASS: screenshot negative-coordinate / DPI geometry, synthetic capture flow, cancellation, drag-ready pin zoom/alpha and shutdown cleanup; no real screen captured.");
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }
        private static void Check(bool value, string label) { if (!value) throw new Exception("Screen pin: " + label); }
    }
}
