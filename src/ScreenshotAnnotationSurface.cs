using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace CodexIsland
{
    internal sealed class ScreenshotAnnotationSurface : FrameworkElement
    {
        private readonly ScreenshotAnnotationDocument document;
        private Point first;
        private ScreenshotAnnotationMark pending;
        internal bool IsDrawing { get { return pending != null; } }
        internal string Mode { get; set; }
        internal string AnnotationText { get; set; }
        internal event Action PanRequested;
        internal event Action<string> Error;
        internal ScreenshotAnnotationSurface(ScreenshotAnnotationDocument value)
        {
            document = value; Mode = "pan"; Focusable = true; Cursor = Cursors.SizeAll;
            document.Changed += InvalidateVisual;
            MouseLeftButtonDown += Begin; MouseMove += Move; MouseLeftButtonUp += End;
            LostMouseCapture += (s, e) => { if (pending != null) { pending = null; InvalidateVisual(); } };
        }
        internal void SelectMode(string value) { CancelDrawing(); Mode = value; Cursor = value == "pan" ? Cursors.SizeAll : Cursors.Cross; }
        internal Point ToSource(Point point)
        {
            return document.Clamp(new Point(point.X * document.Source.PixelWidth / Math.Max(1, ActualWidth), point.Y * document.Source.PixelHeight / Math.Max(1, ActualHeight)));
        }
        protected override void OnRender(DrawingContext drawing)
        {
            base.OnRender(drawing);
            if (document.Source == null || ActualWidth <= 0 || ActualHeight <= 0) return;
            drawing.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));
            drawing.PushTransform(new ScaleTransform(ActualWidth / document.Source.PixelWidth, ActualHeight / document.Source.PixelHeight));
            document.Draw(drawing, pending, VisualTreeHelper.GetDpi(this).PixelsPerDip); drawing.Pop(); drawing.Pop();
        }
        private void Begin(object sender, MouseButtonEventArgs args)
        {
            if (Mode == "pan") { var pan = PanRequested; if (pan != null) pan(); args.Handled = true; return; }
            Focus(); first = ToSource(args.GetPosition(this));
            ScreenshotAnnotationKind kind;
            if (!Enum.TryParse<ScreenshotAnnotationKind>(Mode, true, out kind)) return;
            if (kind == ScreenshotAnnotationKind.Text || kind == ScreenshotAnnotationKind.Number)
            {
                try { document.Add(kind, first, first, AnnotationText); } catch (Exception error) { Report(error.Message); }
            }
            else { pending = new ScreenshotAnnotationMark { Kind = kind, Start = first, End = first }; CaptureMouse(); InvalidateVisual(); }
            args.Handled = true;
        }
        private void Move(object sender, MouseEventArgs args)
        { if (pending != null) { pending.End = ToSource(args.GetPosition(this)); InvalidateVisual(); args.Handled = true; } }
        private void End(object sender, MouseButtonEventArgs args)
        {
            if (pending == null) return; var mark = pending; mark.End = ToSource(args.GetPosition(this)); pending = null; ReleaseMouseCapture();
            try { document.Add(mark.Kind, mark.Start, mark.End, ""); } catch (Exception error) { Report(error.Message); }
            InvalidateVisual(); args.Handled = true;
        }
        private void Report(string message) { var error = Error; if (error != null) error(message); }
        internal void CancelDrawing() { pending = null; if (IsMouseCaptured) ReleaseMouseCapture(); InvalidateVisual(); }
    }
}
