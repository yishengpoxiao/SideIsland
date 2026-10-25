using System;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexIsland
{
    // Widgets have the same fixed footprint as quota rings. Data refreshes only
    // invalidate drawing, so clocks and telemetry never resize the native island.
    internal sealed class CompactWidgetVisual : FrameworkElement
    {
        private CompactWidgetState state;
        private string direction = "auto";
        private bool light;
        internal const int ContentTransitionMilliseconds = 150;
        private bool contentTransitionActive;
        private Stopwatch contentTransitionWatch;
        private double contentOpacity = 1, contentOffsetY;
        private double contentFromOpacity, contentFromY;
        private int contentRevision, contentTransitionVersion;
        private string mediaCaption;
        private BitmapSource mediaArtwork;
        internal CompactWidgetState State { get { return state; } }
        internal bool LightTheme { get { return light; } }
        internal int ContentRevision { get { return contentRevision; } }
        internal bool ContentTransitionActive { get { return contentTransitionActive; } }
        internal int ContentTransitionVersion { get { return contentTransitionVersion; } }
        internal double ContentOpacity { get { return contentOpacity; } }
        internal double ContentOffsetY { get { return contentOffsetY; } }

        public CompactWidgetVisual()
        {
            Width = 54; Height = 67; IsHitTestVisible = false;
            IsVisibleChanged += (s, e) => { if (!IsVisible) SettleContentTransition(); };
            Unloaded += (s, e) => SettleContentTransition();
            SizeChanged += (s, e) => SettleContentTransition();
        }

        public void Update(CompactWidgetState value, string textDirection, bool lightTheme, bool animate = false)
        {
            bool wasMedia = state != null && state.Key == "media";
            bool media = value != null && value.Key == "media";
            bool changed = (wasMedia || media) && (wasMedia != media ||
                !String.Equals(mediaCaption, media ? value.Caption : null, StringComparison.Ordinal) ||
                !ReferenceEquals(mediaArtwork, media ? value.Artwork : null));
            bool presentationChanged = direction != textDirection || light != lightTheme;
            double alpha = contentTransitionActive ? contentOpacity : .35;
            double offset = contentTransitionActive ? contentOffsetY : 2;
            // Only the current state is retained. No outgoing model, artwork or
            // message bitmap can linger after a track or widget selection.
            state = value; direction = textDirection; light = lightTheme;
            mediaCaption = media ? value.Caption : null; mediaArtwork = media ? value.Artwork : null;
            if (changed) ++contentRevision;
            if (!animate || presentationChanged || !CanAnimateContent || !media)
                SettleContentTransition();
            else if (changed && wasMedia)
                BeginContentTransition(alpha, offset);
            InvalidateVisual();
        }

        private bool CanAnimateContent
        {
            get { return IsLoaded && IsVisible && Opacity > 0 && SystemParameters.ClientAreaAnimation; }
        }

        private void BeginContentTransition(double opacity, double offset)
        {
            SettleContentTransition();
            contentFromOpacity = Math.Max(.35, Math.Min(1, opacity));
            contentFromY = Math.Max(-2, Math.Min(2, offset));
            contentOpacity = contentFromOpacity; contentOffsetY = contentFromY;
            contentTransitionActive = true; contentTransitionWatch = null; ++contentTransitionVersion;
            CompositionTarget.Rendering += OnContentRendering;
        }

        private void OnContentRendering(object sender, EventArgs e)
        {
            if (!contentTransitionActive || !CanAnimateContent || state == null || state.Key != "media")
            { SettleContentTransition(); return; }
            if (contentTransitionWatch == null) contentTransitionWatch = Stopwatch.StartNew();
            double progress = Math.Min(1, contentTransitionWatch.Elapsed.TotalMilliseconds / ContentTransitionMilliseconds);
            double ease = .5 - Math.Cos(Math.PI * progress) / 2;
            contentOpacity = contentFromOpacity + (1 - contentFromOpacity) * ease;
            contentOffsetY = contentFromY * (1 - ease);
            InvalidateVisual();
            if (progress >= 1) SettleContentTransition();
        }

        internal void SettleContentTransition()
        {
            CompositionTarget.Rendering -= OnContentRendering;
            bool changed = contentTransitionActive || contentTransitionWatch != null || contentOpacity != 1 || contentOffsetY != 0;
            if (contentTransitionActive || contentTransitionWatch != null) ++contentTransitionVersion;
            contentTransitionActive = false; contentTransitionWatch = null;
            contentOpacity = 1; contentOffsetY = 0;
            if (changed) InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            if (state == null) return;
            if (state.Key == "media")
            {
                dc.PushClip(new RectangleGeometry(new Rect(0, 0, Width, Height)));
                dc.PushOpacity(contentOpacity);
                dc.PushTransform(new TranslateTransform(0, contentOffsetY));
                var cover = new Rect(7, 3, 40, 40);
                dc.DrawRoundedRectangle(Display.Brush("#30383B"), null, cover, 8, 8);
                if (state.Artwork != null)
                {
                    dc.PushClip(new RectangleGeometry(cover, 8, 8)); dc.DrawImage(state.Artwork, cover); dc.Pop();
                }
                else CompactText.Draw(dc, this, "♪", cover, 25, Display.Brush("#8FCFF5"), false, "auto");
                CompactText.Draw(dc, this, state.Caption, new Rect(1, 49, 52, 16), 9.5, Display.Brush("#EDF3F0"), false, direction);
                dc.Pop(); dc.Pop(); dc.Pop();
                return;
            }
            string accent = state.Stale ? "#717B7E" : state.Warning ? "#FFBE83" : state.Accent;
            if (state.ShowRing)
            {
                var center = new Point(27, 27);
                dc.DrawEllipse(null, new Pen(Display.Brush("#2A3033"), 3), center, 23, 23);
                var pen = new Pen(Display.Brush(accent), 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                double fraction = state.Fraction.HasValue ? Math.Max(0, Math.Min(1, state.Fraction.Value)) : 0;
                if (fraction >= .999) dc.DrawEllipse(null, pen, center, 23, 23);
                else if (fraction > 0)
                {
                    double angle = fraction * Math.PI * 2 - Math.PI / 2;
                    var figure = new PathFigure { StartPoint = new Point(27, 4), IsClosed = false };
                    figure.Segments.Add(new ArcSegment(new Point(27 + Math.Cos(angle) * 23, 27 + Math.Sin(angle) * 23), new Size(23, 23), 0, fraction > .5, SweepDirection.Clockwise, true));
                    dc.DrawGeometry(null, pen, new PathGeometry(new[] { figure }));
                }
            }
            double fontSize = state.ShowRing ? 16 : state.Key == "clock" ? 17 : 18;
            CompactText.Draw(dc, this, state.Value, new Rect(2.5, 13, 49, 28), fontSize, Display.Brush("#EDF3F0"), true, direction, false, true, null, true);
            CompactText.Draw(dc, this, state.Caption, new Rect(1, 53, 52, 14), 9.5, Display.Brush("#8C9994"), false, direction);
        }
    }
}
