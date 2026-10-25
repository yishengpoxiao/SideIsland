using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace CodexIsland
{
    // All compact drawings use whole lines, even at larger lyric sizes. The
    // original value remains on the model/tooltip; only its drawing is bounded.
    internal static class CompactText
    {
        private static readonly FontFamily Family = new FontFamily("Segoe UI, Microsoft YaHei UI");
        private static readonly Typeface Regular = new Typeface(Family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        private static readonly Typeface Strong = new Typeface(Family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        internal static void Draw(DrawingContext dc, Visual owner, string value, Rect bounds, double size, Brush brush,
            bool strong, string direction, bool wrap = false, bool center = true, string language = null, bool shrink = false)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0) return;
            value = Clean(value);
            CultureInfo culture = language == "en" ? CultureInfo.GetCultureInfo("en-US") : language == "zh" ? CultureInfo.GetCultureInfo("zh-CN") : CultureInfo.CurrentCulture;
            Func<double, FormattedText> format = fontSize => new FormattedText(value, culture, FlowDirection.LeftToRight, strong ? Strong : Regular, fontSize, brush, VisualTreeHelper.GetDpi(owner).PixelsPerDip);
            var text = format(size);
            if (shrink && text.Width > bounds.Width) { size = Math.Max(8, size * bounds.Width / text.Width); text = format(size); }
            double lineHeight = Math.Min(bounds.Height, size * 1.35);
            int lines = wrap ? Math.Max(1, (int)Math.Floor((bounds.Height + .01) / lineHeight)) : 1;
            text.LineHeight = lineHeight;
            text.MaxTextWidth = bounds.Width;
            text.MaxLineCount = lines;
            // Fractional font metrics can round above LineHeight at 125/150%
            // DPI. Give the formatter the complete slot; MaxLineCount still
            // prevents a partial extra line, without dropping the first line.
            text.MaxTextHeight = bounds.Height;
            text.Trimming = TextTrimming.CharacterEllipsis;
            text.TextAlignment = center ? TextAlignment.Center : TextAlignment.Left;
            double y = bounds.Top + Math.Max(0, (bounds.Height - text.Height) / 2);
            double x = bounds.Left + bounds.Width / 2, centerY = bounds.Top + bounds.Height / 2;
            dc.PushClip(new RectangleGeometry(bounds));
            bool transform = direction == "rotate180" || direction == "mirror";
            if (transform) dc.PushTransform(direction == "rotate180" ? (Transform)new RotateTransform(180, x, centerY) : new ScaleTransform(-1, 1, x, centerY));
            dc.DrawText(text, new Point(bounds.Left, y));
            if (transform) dc.Pop();
            dc.Pop();
        }

        internal static string Clean(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return "";
            if (value.Length > 512) value = value.Substring(0, Char.IsHighSurrogate(value[511]) ? 511 : 512);
            return value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
        }
    }
}
