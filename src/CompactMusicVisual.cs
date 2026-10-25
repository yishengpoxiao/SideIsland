using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexIsland
{
    // A fixed-size drawing keeps media changes from resizing the compact island.
    // Artwork is drawn independently of the user's text rotation/mirroring option.
    internal sealed partial class CompactMusicVisual : FrameworkElement
    {
        private string title = "", artist = "", lyric = "", direction = "auto", language = "zh";
        private BitmapSource artwork;
        private bool horizontal = true, light, available, playing, timelineAvailable;
        private bool canToggle, canPrevious, canNext;
        private int lyricFontSize = 10;
        private double progress;
        private static readonly Typeface Regular = new Typeface("Segoe UI, Microsoft YaHei UI");
        private static readonly Typeface Strong = new Typeface(new FontFamily("Segoe UI, Microsoft YaHei UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        // Resolve the current immutable palette when drawing. Keeping an old
        // snapshot here would retain the previous theme after a live switch.
        private static Brush TextBrush { get { return Display.Brush("#E3EFE7"); } }
        private static Brush MutedBrush { get { return Display.Brush("#879690"); } }
        private static Brush AccentBrush { get { return Display.Brush("#A1EACF"); } }
        private static Brush TrackBrush { get { return Display.Brush("#30383B"); } }
        private static Brush CoverBrush { get { return Display.Brush("#1A2023"); } }

        internal bool Horizontal { get { return horizontal; } }
        internal bool LightTheme { get { return light; } }
        internal string Lyric { get { return lyric; } }
        private string DisplayLine { get { return lyric.Length > 0 ? lyric : !available ? (language == "en" ? "Waiting for player" : "等待播放器") : playing ? (language == "en" ? "Now playing" : "正在播放") : (language == "en" ? "Paused" : "已暂停"); } }

        internal CompactMusicVisual()
        {
            Width = 118; Height = 62; IsHitTestVisible = false;
            SnapsToDevicePixels = true;
            Unloaded += (s, e) => SettleContentTransition();
            IsVisibleChanged += (s, e) => { if (!IsVisible) SettleContentTransition(); };
        }

        internal void Update(MediaSnapshot snapshot, string currentLyric, bool isHorizontal, string textDirection, bool lightTheme, string displayLanguage, int requestedLyricFontSize = 10, bool hasLyricsDocument = false, bool synchronizedLyrics = true, bool animate = false)
        {
            bool nextAvailable = snapshot != null && snapshot.Available;
            bool nextPlaying = nextAvailable && snapshot.Playing;
            string nextLanguage = displayLanguage == "en" ? "en" : "zh";
            bool english = nextLanguage == "en";
            string nextTitle = nextAvailable && !String.IsNullOrWhiteSpace(snapshot.Title) ? snapshot.Title : english ? "No media playing" : "暂无播放媒体";
            string nextArtist = nextAvailable && !String.IsNullOrWhiteSpace(snapshot.Artist) ? snapshot.Artist : english ? "System media" : "系统媒体";
            bool nextTimeline = nextAvailable && snapshot.HasUsableTimeline;
            // The compact card is for music, not lyric diagnostics. Keep the
            // detailed reason and full, untimed document in the expanded panel.
            string nextLyric = nextTimeline && synchronizedLyrics ? CleanLine(currentLyric) : "";
            string nextDirection = textDirection == "rotate180" || textDirection == "mirror" ? textDirection : "auto";
            BitmapSource nextArtwork = nextAvailable ? snapshot.Artwork : null;
            double nextProgress = nextTimeline ? MediaSnapshot.Progress(snapshot.EstimatePosition(DateTime.UtcNow), snapshot.Duration) / 100 : 0;
            // Progress is quantized to rendered pixels; idle snapshots do not redraw.
            double width = isHorizontal ? 118 : 59;
            nextProgress = Math.Round(Math.Max(0, Math.Min(1, nextProgress)) * width) / width;
            int nextFontSize = Math.Max(9, Math.Min(16, requestedLyricFontSize));
            bool nextToggle = nextAvailable && snapshot.CanToggle, nextPrevious = nextAvailable && snapshot.CanPrevious, nextNext = nextAvailable && snapshot.CanNext;
            string nextIdentity = NowPlayingMonitor.Identity(snapshot);
            bool trackChanged = !String.Equals(trackIdentity, nextIdentity, StringComparison.Ordinal);
            bool contentChanged = trackChanged || title != nextTitle || artist != nextArtist || artwork != nextArtwork;
            bool lyricChanged = trackChanged || lyric != nextLyric;
            bool appearanceChanged = direction != nextDirection || language != nextLanguage || horizontal != isHorizontal || light != lightTheme || lyricFontSize != nextFontSize;
            bool changed = title != nextTitle || artist != nextArtist || lyric != nextLyric || direction != nextDirection || language != nextLanguage || artwork != nextArtwork || horizontal != isHorizontal || light != lightTheme || available != nextAvailable || playing != nextPlaying || timelineAvailable != nextTimeline || progress != nextProgress || lyricFontSize != nextFontSize || canToggle != nextToggle || canPrevious != nextPrevious || canNext != nextNext;
            bool initialized = contentInitialized;
            if (contentChanged || lyric != nextLyric) ++contentRevision;
            title = nextTitle; artist = nextArtist; lyric = nextLyric; direction = nextDirection; language = nextLanguage; artwork = nextArtwork;
            horizontal = isHorizontal; light = lightTheme; available = nextAvailable; playing = nextPlaying; timelineAvailable = nextTimeline; progress = nextProgress;
            lyricFontSize = nextFontSize; canToggle = nextToggle; canPrevious = nextPrevious; canNext = nextNext;
            trackIdentity = nextIdentity; contentInitialized = true;
            double height = isHorizontal ? 62 : 133;
            if (Width != width) Width = width;
            if (Height != height) Height = height;
            UpdateContentTransition(contentChanged, lyricChanged, animate && initialized && !appearanceChanged);
            if (changed) InvalidateVisual();
        }

        internal Rect CoverBounds { get { return horizontal ? new Rect(0, 1, 36, 36) : new Rect(5.5, 0, 48, 48); } }
        internal Rect PreviousBounds { get { return horizontal ? new Rect(43, 29, 25, 12) : new Rect(2, 88, 24, 13); } }
        internal Rect NextBounds { get { return horizontal ? new Rect(88, 29, 25, 12) : new Rect(33, 88, 24, 13); } }
        internal string ActionAt(Point point)
        {
            if (canToggle && CoverBounds.Contains(point)) return "music-toggle";
            if (canPrevious && PreviousBounds.Contains(point)) return "music-previous";
            if (canNext && NextBounds.Contains(point)) return "music-next";
            return null;
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            Brush text = TextBrush, muted = MutedBrush, accent = AccentBrush;
            Rect cover = CoverBounds;
            BeginContentDrawing(dc, cover, contentMotion, false);
            DrawArtwork(dc, cover);
            EndContentDrawing(dc);
            if (available) DrawPlayingIndicator(dc, new Point(cover.Right - 3, cover.Bottom - 3), accent);
            string line = DisplayLine;
            if (horizontal)
            {
                BeginContentDrawing(dc, new Rect(42, 0, 76, 28), contentMotion);
                DrawText(dc, title, new Rect(42, 0, 76, 14), 10, text, true, false);
                DrawText(dc, artist, new Rect(42, 14, 76, 14), 9, muted, false, false);
                EndContentDrawing(dc);
                BeginContentDrawing(dc, new Rect(0, 41, 118, 19), lyricMotion);
                DrawText(dc, line, new Rect(0, 41, 118, 19), lyricFontSize, lyric.Length > 0 ? accent : muted, false, false);
                EndContentDrawing(dc);
            }
            else
            {
                BeginContentDrawing(dc, new Rect(0, 50, 59, 38), contentMotion);
                DrawText(dc, title, new Rect(0, 50, 59, 26), 9.5, text, true, true);
                DrawText(dc, artist, new Rect(0, 76, 59, 12), 8.5, muted, false, false);
                EndContentDrawing(dc);
                BeginContentDrawing(dc, new Rect(0, 103, 59, 25), lyricMotion);
                DrawText(dc, line, new Rect(0, 103, 59, 25), lyricFontSize, lyric.Length > 0 ? accent : muted, false, true);
                EndContentDrawing(dc);
            }
            DrawTransport(dc, PreviousBounds, true, canPrevious, accent);
            DrawTransport(dc, NextBounds, false, canNext, accent);
            if (timelineAvailable)
            {
                double y = horizontal ? 60 : 130;
                dc.DrawRoundedRectangle(TrackBrush, null, new Rect(0, y, Width, 2), 1, 1);
                if (progress > 0) dc.DrawRoundedRectangle(accent, null, new Rect(0, y, Math.Max(1, Width * progress), 2), 1, 1);
            }
        }

        private void DrawTransport(DrawingContext dc, Rect rectangle, bool previous, bool enabled, Brush accent)
        {
            if (!enabled) dc.PushOpacity(.3);
            dc.DrawRoundedRectangle(CoverBrush, null, rectangle, 4, 4);
            double x = rectangle.X + rectangle.Width / 2, y = rectangle.Y + rectangle.Height / 2;
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(new Point(x + (previous ? -3 : 3), y), true, true);
                context.LineTo(new Point(x + (previous ? 2 : -2), y - 3), true, false);
                context.LineTo(new Point(x + (previous ? 2 : -2), y + 3), true, false);
            }
            dc.DrawGeometry(accent, null, geometry);
            dc.DrawRectangle(accent, null, new Rect(x + (previous ? -5 : 4), y - 3, 1, 6));
            if (!enabled) dc.Pop();
        }

        private void DrawArtwork(DrawingContext dc, Rect rectangle)
        {
            dc.PushClip(new RectangleGeometry(rectangle, 8, 8));
            dc.DrawRectangle(CoverBrush, null, rectangle);
            if (artwork != null && artwork.Width > 0 && artwork.Height > 0)
            {
                double scale = Math.Max(rectangle.Width / artwork.Width, rectangle.Height / artwork.Height);
                double width = artwork.Width * scale, height = artwork.Height * scale;
                dc.DrawImage(artwork, new Rect(rectangle.X + (rectangle.Width - width) / 2, rectangle.Y + (rectangle.Height - height) / 2, width, height));
            }
            else
            {
                Brush accent = AccentBrush;
                var note = Format("\u266B", rectangle.Width * .48, accent, false);
                dc.DrawText(note, new Point(rectangle.X + (rectangle.Width - note.Width) / 2, rectangle.Y + (rectangle.Height - note.Height) / 2));
            }
            dc.Pop();
        }

        private void DrawPlayingIndicator(DrawingContext dc, Point center, Brush accent)
        {
            dc.DrawEllipse(CoverBrush, null, center, 6, 6);
            if (playing)
            {
                dc.DrawRoundedRectangle(accent, null, new Rect(center.X - 2.5, center.Y - 2.5, 1.5, 5), .5, .5);
                dc.DrawRoundedRectangle(accent, null, new Rect(center.X + 1, center.Y - 2.5, 1.5, 5), .5, .5);
            }
            else
            {
                var play = new StreamGeometry();
                using (var context = play.Open())
                {
                    context.BeginFigure(new Point(center.X - 1.5, center.Y - 3), true, true);
                    context.LineTo(new Point(center.X + 3, center.Y), true, false);
                    context.LineTo(new Point(center.X - 1.5, center.Y + 3), true, false);
                }
                dc.DrawGeometry(accent, null, play);
            }
        }

        private void DrawText(DrawingContext dc, string value, Rect rectangle, double fontSize, Brush brush, bool bold, bool wrap)
        {
            CompactText.Draw(dc, this, value, rectangle, fontSize, brush, bold, direction, wrap, !horizontal, language);
        }

        private FormattedText Format(string value, double fontSize, Brush brush, bool bold)
        {
            return new FormattedText(value ?? "", language == "en" ? CultureInfo.GetCultureInfo("en-US") : CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight, bold ? Strong : Regular, fontSize, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        }

        private static string CleanLine(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return "";
            // Only a few visible characters fit here. Bound external metadata so
            // an unusually large title/lyric cannot make compact drawing stall.
            if (value.Length > 512)
            {
                int length = Char.IsHighSurrogate(value[511]) ? 511 : 512;
                value = value.Substring(0, length);
            }
            return value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
        }

    }
}
