using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexIsland
{
    internal static class ScreenshotAnnotationTests
    {
        internal static BitmapSource Sample(int width, int height)
        {
            byte[] pixels = new byte[width * height * 4];
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) { int at = (y * width + x) * 4; byte value = (byte)((x + y) % 2 == 0 ? 20 : 220); pixels[at] = value; pixels[at + 1] = value; pixels[at + 2] = value; pixels[at + 3] = 255; }
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4); bitmap.Freeze(); return bitmap;
        }
        private static byte[] Pixels(BitmapSource image) { var bytes = new byte[image.PixelWidth * image.PixelHeight * 4]; image.CopyPixels(bytes, image.PixelWidth * 4, 0); return bytes; }
        internal static void Run()
        {
            var source = Sample(96, 72); byte[] original = Pixels(source);
            using (var document = new ScreenshotAnnotationDocument(source))
            {
                document.Add(ScreenshotAnnotationKind.Rectangle, new Point(10, 10), new Point(70, 55), "");
                document.Add(ScreenshotAnnotationKind.Arrow, new Point(8, 60), new Point(65, 20), "");
                document.Add(ScreenshotAnnotationKind.Text, new Point(4, 4), new Point(4, 4), "A中");
                document.Add(ScreenshotAnnotationKind.Number, new Point(70, 35), new Point(70, 35), "");
                Check(document.Count == 4, "all editable annotation types retained");
                var flattened = document.Flatten(); Check(flattened.IsFrozen && flattened.PixelWidth == 96 && flattened.PixelHeight == 72, "flatten keeps exact source dimensions and detached pixels");
                Check(!Pixels(flattened).SequenceEqual(original) && Pixels(source).SequenceEqual(original), "export includes marks without changing the original");
                document.Undo(); Check(document.Count == 3, "undo removes only the latest mark"); document.Clear(); Check(document.Count == 0, "clear removes all marks");
                document.Add(ScreenshotAnnotationKind.Mosaic, new Point(12, 12), new Point(48, 48), "");
                var mosaic = Pixels(document.Flatten()); int first = (15 * 96 + 15) * 4, second = (20 * 96 + 20) * 4;
                Check(mosaic[first] == mosaic[second] && mosaic[first] > 50 && mosaic[first] < 200, "mosaic averages source pixels inside each block");
                Check(mosaic[0] == original[0] && Pixels(source).SequenceEqual(original), "mosaic leaves outside area and source untouched");
                document.Clear(); document.Add(ScreenshotAnnotationKind.Rectangle, new Point(-10, -10), new Point(200, 200), "");
                Check(document.Marks.Single().Start == new Point(0, 0) && document.Marks.Single().End == new Point(96, 72), "marks clamp to pixel bounds");
                var surface = new ScreenshotAnnotationSurface(document) { Width = 320, Height = 240 }; surface.Measure(new Size(320, 240)); surface.Arrange(new Rect(0, 0, 320, 240));
                Check(surface.ToSource(new Point(160, 120)) == new Point(48, 36), "logical position maps to source pixels at zoom/DPI-independent size");
                foreach (int dpi in new[] { 96, 120, 144, 192 })
                {
                    VisualTreeHelper.SetRootDpi(surface, new DpiScale(dpi / 96d, dpi / 96d)); surface.InvalidateVisual(); surface.Measure(new Size(320, 240)); surface.Arrange(new Rect(0, 0, 320, 240)); surface.UpdateLayout();
                    var raster = new RenderTargetBitmap((int)Math.Ceiling(320 * dpi / 96d), (int)Math.Ceiling(240 * dpi / 96d), dpi, dpi, PixelFormats.Pbgra32); raster.Render(surface);
                    Check(surface.ToSource(new Point(160, 120)) == new Point(48, 36) && document.Flatten().PixelWidth == 96, "preview DPI never changes source-coordinate or export pixel size: " + dpi);
                }
                bool invalid = false; try { document.Add(ScreenshotAnnotationKind.Text, new Point(1, 1), new Point(1, 1), " "); } catch (ArgumentException) { invalid = true; } Check(invalid, "empty text rejected");
            }
            Console.WriteLine("PASS: real in-memory annotation render, arrows/rectangles/text/number/mosaic, undo, source immutability and source-pixel coordinate mapping; no screen or clipboard access.");
        }
        private static void Check(bool value, string message) { if (!value) throw new Exception("Screenshot annotations: " + message); }
    }
}
