using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CodexIsland
{
    internal static class ImageQuickTests
    {
        public static void Run()
        {
            string folder = Path.Combine(Path.GetTempPath(), "island-image-fixture-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            var previous = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try
            {
                string original = Path.Combine(folder, "original.png"), resized = Path.Combine(folder, "resized.png"), jpeg = Path.Combine(folder, "white.jpg");
                var transparent = Sample(32, 24, true); ImageQuickTools.Save(transparent, null, original, 32, 24, "PNG", 82, CancellationToken.None);
                byte[] originalBytes = File.ReadAllBytes(original); BitmapSource loaded = ImageQuickTools.Load(original, CancellationToken.None);
                Check(loaded.IsFrozen && loaded.PixelWidth == 32 && loaded.PixelHeight == 24, "loaded image is detached and frozen");
                using (var exclusive = new FileStream(original, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Check(exclusive.Length > 0, "OnLoad releases original file lock");
                ImageQuickTools.Save(loaded, original, resized, 16, 12, "PNG", 82, CancellationToken.None); var small = ImageQuickTools.Load(resized, CancellationToken.None);
                Check(small.PixelWidth == 16 && small.PixelHeight == 12 && Pixel(small)[3] == 0, "PNG resize has exact dimensions and preserves transparency");
                ImageQuickTools.Save(loaded, original, jpeg, 32, 24, "JPG", 95, CancellationToken.None); var white = Pixel(ImageQuickTools.Load(jpeg, CancellationToken.None));
                Check(white[0] > 245 && white[1] > 245 && white[2] > 245, "JPG flattens transparent pixels onto white");
                Throws<ArgumentException>(() => ImageQuickTools.Save(loaded, original, original, 32, 24, "PNG", 82, CancellationToken.None), "same original path cannot be overwritten");
                Throws<ArgumentException>(() => ImageQuickTools.Save(loaded, original, Path.Combine(folder, "mismatch.jpg"), 32, 24, "PNG", 82, CancellationToken.None), "format and extension must match");
                Throws<ArgumentException>(() => ImageQuickTools.ValidateSize(16001, 1), "overwide dimension rejected");
                Throws<ArgumentException>(() => ImageQuickTools.ValidateSize(6000, 6000), "oversized pixel count rejected");
                Throws<ArgumentException>(() => ImageQuickTools.ValidateSize(0, 8), "zero size rejected");
                using (var canceled = new CancellationTokenSource())
                {
                    canceled.Cancel(); Throws<OperationCanceledException>(() => ImageQuickTools.Load(original, canceled.Token), "canceled decode cannot publish a result");
                    Throws<OperationCanceledException>(() => ImageQuickTools.Save(loaded, original, resized, 16, 12, "PNG", 82, canceled.Token), "canceled encode preserves destination");
                }
                Check(File.ReadAllBytes(original).SequenceEqual(originalBytes), "all processing preserves original bytes");
                File.WriteAllText(Path.Combine(folder, "broken.png"), "not an image"); Throws<Exception>(() => ImageQuickTools.Load(Path.Combine(folder, "broken.png"), CancellationToken.None), "invalid codec data fails safely");
                var noisy = Sample(192, 128, false); string low = Path.Combine(folder, "low.jpg"), high = Path.Combine(folder, "high.jpg");
                ImageQuickTools.Save(noisy, null, low, 192, 128, "JPG", 15, CancellationToken.None); ImageQuickTools.Save(noisy, null, high, 192, 128, "JPG", 95, CancellationToken.None);
                Check(new FileInfo(low).Length < new FileInfo(high).Length, "JPEG quality changes encoded file size");
                Check(!Directory.GetFiles(folder, ".side-island-image-*").Any(), "successful and canceled saves leave no temporary encoder files");
                Task.Run(() => ImageQuickTools.Save(loaded, original, Path.Combine(folder, "worker.png"), 8, 6, "PNG", 82, CancellationToken.None)).GetAwaiter().GetResult();
                using (var panel = new ImageQuickPanel(false))
                {
                    Complete(panel.LoadFileAsync(original)); Check(Field<Image>(panel, "preview").Source != null, "panel publishes selected image preview");
                    Field<TextBox>(panel, "width").Text = "16"; Check(Field<TextBox>(panel, "height").Text == "12", "keep-ratio width edit derives height");
                    Field<TextBox>(panel, "height").Text = "6"; Check(Field<TextBox>(panel, "width").Text == "8", "keep-ratio height edit derives width");
                    string panelTarget = Path.Combine(folder, "panel.png"); Complete(panel.SaveToAsync(panelTarget)); Check(File.Exists(panelTarget), "panel saved a file: " + Field<TextBlock>(panel, "status").Text); var panelResult = ImageQuickTools.Load(panelTarget, CancellationToken.None); Check(panelResult.PixelWidth == 8 && panelResult.PixelHeight == 6, "panel saves selected dimensions to a new file");
                    Complete(panel.SaveToAsync(original)); Check(Field<TextBlock>(panel, "status").Text.Contains("不能覆盖"), "panel contains same-source protection error");
                    Complete(panel.LoadFileAsync(Path.Combine(folder, "absent.png"))); Check(Field<Image>(panel, "preview").Source == null && !Field<Button>(panel, "save").IsEnabled, "failed load clears prior image and disables stale save");
                    Complete(panel.LoadFileAsync(original)); Check(Field<Button>(panel, "save").IsEnabled, "panel recovers after missing image");
                }
                var disposed = new ImageQuickPanel(false); Task loading = disposed.LoadFileAsync(original); disposed.Dispose(); Complete(loading); Check(Field<Image>(disposed, "preview").Source == null, "disposal discards late image and detaches pixels");
                using (var demo = new ImageQuickPanel(true)) { Complete(demo.LoadFileAsync(original)); Check(Field<Image>(demo, "preview").Source == null, "demo never reads a selected real file"); }
                Console.WriteLine("PASS: image JPG/PNG decode, exact resize, alpha flattening, quality, source protection, file release, cancel, preview controls and disposal.");
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); Directory.Delete(folder, true); }
        }
        internal static BitmapSource Sample(int width, int height, bool transparent)
        {
            byte[] pixels = new byte[width * height * 4]; var random = new Random(731);
            for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = (byte)random.Next(256); pixels[i + 1] = (byte)random.Next(256); pixels[i + 2] = 180; pixels[i + 3] = transparent ? (byte)0 : (byte)255; }
            var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4); image.Freeze(); return image;
        }
        private static byte[] Pixel(BitmapSource source) { var image = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0); var pixels = new byte[4]; image.CopyPixels(new System.Windows.Int32Rect(0, 0, 1, 1), pixels, 4, 0); return pixels; }
        internal static void Complete(Task task)
        {
            var watch = Stopwatch.StartNew(); var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5) };
            timer.Tick += (s, e) => { if (task.IsCompleted || watch.ElapsedMilliseconds > 15000) frame.Continue = false; };
            if (!task.IsCompleted) { timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); } }
            Check(task.IsCompleted, "asynchronous image task completed within timeout"); task.GetAwaiter().GetResult();
        }
        private static T Field<T>(object target, string name) { return (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(target); }
        private static void Throws<T>(Action action, string label) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Image quick: " + label); }
        private static void Check(bool value, string label) { if (!value) throw new Exception("Image quick: " + label); }
    }
}
