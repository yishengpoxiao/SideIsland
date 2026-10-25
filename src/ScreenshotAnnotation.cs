using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexIsland
{
    internal enum ScreenshotAnnotationKind { Arrow, Rectangle, Number, Text, Mosaic }
    internal sealed class ScreenshotAnnotationMark
    {
        internal ScreenshotAnnotationKind Kind;
        internal Point Start, End;
        internal string Text = "";
        internal int Number;
        internal BitmapSource MosaicImage;
        internal Rect Bounds { get { return new Rect(Start, End); } }
    }

    // All coordinates are source pixels, independent of the pin's DPI or zoom.
    internal sealed class ScreenshotAnnotationDocument : IDisposable
    {
        private BitmapSource source;
        private readonly List<ScreenshotAnnotationMark> marks = new List<ScreenshotAnnotationMark>();
        internal event Action Changed;
        internal BitmapSource Source { get { return source; } }
        internal int Count { get { return marks.Count; } }
        internal IEnumerable<ScreenshotAnnotationMark> Marks { get { return marks.AsReadOnly(); } }
        internal ScreenshotAnnotationDocument(BitmapSource snapshot)
        {
            if (snapshot == null || !snapshot.IsFrozen) throw new ArgumentException("截图未准备好。");
            source = snapshot;
        }
        internal Point Clamp(Point point)
        { return new Point(Math.Max(0, Math.Min(source.PixelWidth, point.X)), Math.Max(0, Math.Min(source.PixelHeight, point.Y))); }
        internal void Add(ScreenshotAnnotationKind kind, Point first, Point last, string text)
        {
            if (source == null) throw new ObjectDisposedException("ScreenshotAnnotationDocument");
            if (!Enum.IsDefined(typeof(ScreenshotAnnotationKind), kind) || Double.IsNaN(first.X) || Double.IsNaN(first.Y) || Double.IsNaN(last.X) || Double.IsNaN(last.Y) || Double.IsInfinity(first.X) || Double.IsInfinity(first.Y) || Double.IsInfinity(last.X) || Double.IsInfinity(last.Y)) throw new ArgumentException("标注坐标或工具无效。");
            first = Clamp(first); last = Clamp(last);
            if (marks.Count >= 200) throw new InvalidOperationException("一张截图最多保留 200 个标注。");
            if (kind == ScreenshotAnnotationKind.Text && String.IsNullOrWhiteSpace(text)) throw new ArgumentException("请先输入标注文字。");
            if (kind != ScreenshotAnnotationKind.Text && kind != ScreenshotAnnotationKind.Number && (last - first).Length < 2) return;
            if ((kind == ScreenshotAnnotationKind.Rectangle || kind == ScreenshotAnnotationKind.Mosaic) && (Math.Abs(last.X - first.X) < 1 || Math.Abs(last.Y - first.Y) < 1)) return;
            if (text != null && text.Length > 200) throw new ArgumentException("标注文字最多 200 字符。");
            if (kind == ScreenshotAnnotationKind.Text)
            {
                try { new UTF8Encoding(false, true).GetByteCount(text); } catch (EncoderFallbackException) { throw new ArgumentException("标注文字包含无效字符。"); }
                if (text.Any(ch => Char.IsControl(ch) && ch != '\r' && ch != '\n' && ch != '\t')) throw new ArgumentException("标注文字包含无效字符。");
            }
            var added = new ScreenshotAnnotationMark { Kind = kind, Start = first, End = last, Text = text ?? "", Number = marks.Count(mark => mark.Kind == ScreenshotAnnotationKind.Number) + 1 };
            if (kind == ScreenshotAnnotationKind.Mosaic) added.MosaicImage = Mosaic(added.Bounds);
            marks.Add(added);
            RaiseChanged();
        }
        internal void Undo() { if (marks.Count == 0) return; marks.RemoveAt(marks.Count - 1); RaiseChanged(); }
        internal void Clear() { if (marks.Count == 0) return; marks.Clear(); RaiseChanged(); }
        private void RaiseChanged() { var callback = Changed; if (callback != null) callback(); }
        internal BitmapSource Flatten()
        {
            if (source == null) throw new ObjectDisposedException("ScreenshotAnnotationDocument");
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen()) Draw(drawing, null);
            var output = new RenderTargetBitmap(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            output.Render(visual); output.Freeze(); return output;
        }
        internal void Draw(DrawingContext drawing, ScreenshotAnnotationMark preview, double pixelsPerDip = 1.0)
        {
            drawing.DrawImage(source, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
            foreach (var mark in marks) DrawMark(drawing, mark, false, pixelsPerDip);
            if (preview != null) DrawMark(drawing, preview, true, pixelsPerDip);
        }
        private void DrawMark(DrawingContext drawing, ScreenshotAnnotationMark mark, bool preview, double pixelsPerDip)
        {
            double thickness = Math.Max(2, Math.Min(source.PixelWidth, source.PixelHeight) / 180.0);
            var red = new SolidColorBrush(Color.FromRgb(255, 59, 48)); red.Freeze();
            var blue = new SolidColorBrush(Color.FromRgb(0, 122, 255)); blue.Freeze();
            var pen = new Pen(red, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }; pen.Freeze();
            Rect bounds = mark.Bounds;
            if (mark.Kind == ScreenshotAnnotationKind.Mosaic)
            {
                if (preview) drawing.DrawRectangle(new SolidColorBrush(Color.FromArgb(80, 80, 80, 80)), pen, bounds);
                else if (bounds.Width >= 1 && bounds.Height >= 1) drawing.DrawImage(mark.MosaicImage, bounds);
            }
            else if (mark.Kind == ScreenshotAnnotationKind.Rectangle) drawing.DrawRectangle(null, pen, bounds);
            else if (mark.Kind == ScreenshotAnnotationKind.Arrow)
            {
                drawing.DrawLine(pen, mark.Start, mark.End);
                Vector direction = mark.Start - mark.End;
                if (direction.Length > 0)
                {
                    direction.Normalize(); double length = thickness * 5;
                    Vector normal = new Vector(-direction.Y, direction.X);
                    drawing.DrawLine(pen, mark.End, mark.End + direction * length + normal * length * .5);
                    drawing.DrawLine(pen, mark.End, mark.End + direction * length - normal * length * .5);
                }
            }
            else
            {
                double fontSize = Math.Max(16, Math.Min(72, thickness * 8));
                string value = mark.Kind == ScreenshotAnnotationKind.Number ? mark.Number.ToString(CultureInfo.InvariantCulture) : mark.Text;
                var text = new FormattedText(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), fontSize, mark.Kind == ScreenshotAnnotationKind.Number ? Brushes.White : red, pixelsPerDip);
                if (mark.Kind == ScreenshotAnnotationKind.Number)
                {
                    double radius = Math.Max(text.Width, text.Height) * .65;
                    Point center = new Point(Math.Max(radius, Math.Min(source.PixelWidth - radius, mark.Start.X)), Math.Max(radius, Math.Min(source.PixelHeight - radius, mark.Start.Y)));
                    drawing.DrawEllipse(blue, null, center, radius, radius);
                    drawing.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
                }
                else
                {
                    text.MaxTextWidth = Math.Max(1, source.PixelWidth - Math.Min(mark.Start.X, source.PixelWidth - 1));
                    text.MaxTextHeight = Math.Max(1, source.PixelHeight - Math.Min(mark.Start.Y, source.PixelHeight - 1));
                    drawing.DrawText(text, mark.Start);
                }
            }
        }
        private BitmapSource Mosaic(Rect rectangle)
        {
            int x = Math.Max(0, (int)Math.Floor(rectangle.X)), y = Math.Max(0, (int)Math.Floor(rectangle.Y));
            int width = Math.Max(1, Math.Min(source.PixelWidth - x, (int)Math.Ceiling(rectangle.Width))), height = Math.Max(1, Math.Min(source.PixelHeight - y, (int)Math.Ceiling(rectangle.Height)));
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int stride = checked(width * 4); byte[] pixels = new byte[checked(stride * height)]; converted.CopyPixels(new Int32Rect(x, y, width, height), pixels, stride, 0);
            const int block = 12;
            for (int top = 0; top < height; top += block) for (int left = 0; left < width; left += block)
            {
                int right = Math.Min(width, left + block), bottom = Math.Min(height, top + block), count = (right - left) * (bottom - top); long b = 0, g = 0, r = 0, a = 0;
                for (int row = top; row < bottom; row++) for (int col = left; col < right; col++) { int index = row * stride + col * 4; b += pixels[index]; g += pixels[index + 1]; r += pixels[index + 2]; a += pixels[index + 3]; }
                for (int row = top; row < bottom; row++) for (int col = left; col < right; col++) { int index = row * stride + col * 4; pixels[index] = (byte)(b / count); pixels[index + 1] = (byte)(g / count); pixels[index + 2] = (byte)(r / count); pixels[index + 3] = (byte)(a / count); }
            }
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride); bitmap.Freeze(); return bitmap;
        }
        public void Dispose() { marks.Clear(); source = null; Changed = null; }
    }
}
