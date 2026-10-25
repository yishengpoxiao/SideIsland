using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace CodexIsland
{
    internal static class Display
    {
        public static Brush Brush(string hex) { return UiTheme.Brush(hex); }
        public static string Percent(double? value) { return value.HasValue ? Math.Max(0, Math.Min(100, value.Value)).ToString("0") + "%" : "—"; }
        public static string Window(int? minutes, string fallback)
        {
            if (!minutes.HasValue || minutes <= 0) return fallback;
            int m = minutes.Value;
            if (m == 10080) return "每周额度";
            if (m % 1440 == 0) return (m / 1440) + " 天额度";
            if (m % 60 == 0) return (m / 60) + " 小时额度";
            return m + " 分钟额度";
        }
        public static string ShortWindow(int? minutes, string fallback)
        {
            if (!minutes.HasValue || minutes <= 0) return fallback;
            if (minutes % 1440 == 0) return (minutes / 1440) + "d";
            if (minutes % 60 == 0) return (minutes / 60) + "h";
            return minutes + "m";
        }
        public static string Reset(long? timestamp)
        {
            if (!timestamp.HasValue) return "重置时间未知";
            double seconds = timestamp.Value - (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
            if (seconds <= 0) return "等待同步新的额度";
            long m = (long)Math.Ceiling(seconds / 60);
            if (m >= 1440) return (m / 1440) + " 天 " + (m % 1440 / 60) + " 小时后重置";
            if (m >= 60) return (m / 60) + " 小时 " + (m % 60) + " 分后重置";
            return m + " 分钟后重置";
        }
    }

    internal class QuotaRing : FrameworkElement
    {
        public double? Remaining;
        public string Caption = "5h";
        public string Accent = "#9BE9CA";
        public bool Stale;
        public int AlertThreshold = 15;
        public string TextDirection = "auto";
        public QuotaRing() { Width = 54; Height = 67; }
        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            var center = new Point(27, 27);
            dc.DrawEllipse(null, new Pen(Display.Brush("#2A3033"), 3), center, 23, 23);
            if (Remaining.HasValue)
            {
                double fraction = Math.Max(0, Math.Min(100, Remaining.Value)) / 100.0;
                var pen = new Pen(Display.Brush(Stale ? "#717B7E" : Remaining <= AlertThreshold ? "#FFBE83" : Accent), 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                if (fraction >= .999) dc.DrawEllipse(null, pen, center, 23, 23);
                else if (fraction > 0)
                {
                    double angle = fraction * Math.PI * 2 - Math.PI / 2;
                    var figure = new PathFigure { StartPoint = new Point(27, 4), IsClosed = false };
                    figure.Segments.Add(new ArcSegment(new Point(27 + Math.Cos(angle) * 23, 27 + Math.Sin(angle) * 23), new Size(23, 23), 0, fraction > .5, SweepDirection.Clockwise, true));
                    dc.DrawGeometry(null, pen, new PathGeometry(new[] { figure }));
                }
            }
            string value = Remaining.HasValue ? Math.Max(0, Math.Min(100, Remaining.Value)).ToString("0") : "—";
            CompactText.Draw(dc, this, value, new Rect(2.5, 13, 49, 28), 16, Display.Brush("#EDF3F0"), true, TextDirection);
            CompactText.Draw(dc, this, Caption, new Rect(1, 53, 52, 14), 9.5, Display.Brush("#8C9994"), false, TextDirection);
        }
    }
}
