using System;
using System.Windows;

namespace CodexIsland
{
    // Pure DIP geometry, independent of the active monitor or a real HWND.
    public static class WindowGeometry
    {
        public const double MinWidth = 360;
        public const double MinHeight = 360;
        public const double MaxWidth = 900;
        public const double MaxHeight = 1100;
        public const double CompactShortSide = 88;
        public const double CompactLongSide = 238;

        // Compare the center to all four work-area edges. The preferred edge wins
        // exact ties, avoiding orientation changes while dragging along a diagonal.
        public static string NearestEdge(Rect window, Rect work, string preferred = "right")
        {
            work = ValidWork(work);
            window = FiniteWindow(window, work);
            double centerX = window.Left + window.Width / 2;
            double centerY = window.Top + window.Height / 2;
            string[] edges = { "left", "right", "top", "bottom" };
            double[] distances = { Math.Abs(centerX - work.Left), Math.Abs(centerX - work.Right),
                Math.Abs(centerY - work.Top), Math.Abs(centerY - work.Bottom) };
            preferred = ValidEdge(preferred);
            int best = Array.IndexOf(edges, preferred);
            for (int i = 0; i < edges.Length; i++)
                if (distances[i] < distances[best]) best = i;
            return edges[best];
        }

        // Rotate the compact footprint for horizontal edges while preserving the
        // dragged center along the selected edge. A tiny work area shrinks it to fit.
        public static Rect DockCompact(Rect dragged, Rect work, string edge)
        {
            work = ValidWork(work);
            dragged = FiniteWindow(dragged, work);
            edge = ValidEdge(edge);
            Size compact = CompactSize(edge, work);
            double x = dragged.Left + (dragged.Width - compact.Width) / 2;
            double y = dragged.Top + (dragged.Height - compact.Height) / 2;
            if (edge == "left") x = work.Left;
            else if (edge == "right") x = work.Right - compact.Width;
            else if (edge == "top") y = work.Top;
            else y = work.Bottom - compact.Height;
            return ClampMove(new Rect(x, y, compact.Width, compact.Height), work);
        }

        // Expand inward from the compact edge. Along that edge, both footprints
        // share their center unless the work-area bounds require clamping.
        public static Rect PlaceFromCompact(Rect compact, Size expanded, string edge, Rect work)
        {
            work = ValidWork(work);
            compact = FiniteWindow(compact, work);
            edge = ValidEdge(edge);
            double width = Math.Min(Positive(expanded.Width, MinWidth), work.Width);
            double height = Math.Min(Positive(expanded.Height, MinHeight), work.Height);
            return AlignFootprint(compact, new Size(width, height), edge, work);
        }

        // Reverse the footprint conversion. Callers retaining the original compact
        // anchor can restore it exactly even after expansion was clamped at a corner.
        public static Rect CompactFromExpanded(Rect expanded, string edge, Rect work)
        {
            work = ValidWork(work);
            expanded = FiniteWindow(expanded, work);
            edge = ValidEdge(edge);
            return AlignFootprint(expanded, CompactSize(edge, work), edge, work);
        }

        private static Rect AlignFootprint(Rect from, Size size, string edge, Rect work)
        {
            double x = from.Left + (from.Width - size.Width) / 2;
            double y = from.Top + (from.Height - size.Height) / 2;
            if (edge == "left") x = from.Left;
            else if (edge == "right") x = from.Right - size.Width;
            else if (edge == "top") y = from.Top;
            else y = from.Bottom - size.Height;
            return ClampMove(new Rect(x, y, size.Width, size.Height), work);
        }

        private static Size CompactSize(string edge, Rect work)
        {
            bool horizontal = edge == "top" || edge == "bottom";
            return new Size(Math.Min(horizontal ? CompactLongSide : CompactShortSide, work.Width),
                Math.Min(horizontal ? CompactShortSide : CompactLongSide, work.Height));
        }

        private static string ValidEdge(string edge)
        {
            edge = (edge ?? String.Empty).ToLowerInvariant();
            return edge == "left" || edge == "top" || edge == "bottom" ? edge : "right";
        }

        private static Rect FiniteWindow(Rect window, Rect work)
        {
            return new Rect(Coordinate(window.X, work.Left), Coordinate(window.Y, work.Top),
                Positive(window.Width, CompactShortSide), Positive(window.Height, CompactLongSide));
        }

        // Keep the entire window inside the selected monitor's working area.
        // Oversized windows shrink to fit; a collapsed island otherwise keeps its size.
        public static Rect ClampMove(Rect window, Rect work)
        {
            work = ValidWork(work);
            double width = Math.Min(Positive(window.Width, work.Width), work.Width);
            double height = Math.Min(Positive(window.Height, work.Height), work.Height);
            return new Rect(
                Clamp(Coordinate(window.X, work.Left), work.Left, work.Right - width),
                Clamp(Coordinate(window.Y, work.Top), work.Top, work.Bottom - height),
                width, height);
        }

        // Snap only near the left/right edges. Call this after choosing the monitor
        // under the pointer, so dragging between monitors is not constrained here.
        public static Rect SnapToHorizontalEdge(Rect window, Rect work, double threshold)
        {
            work = ValidWork(work);
            Rect result = ClampMove(window, work);
            threshold = IsFinite(threshold) ? Math.Max(0, threshold) : 0;
            double leftDistance = result.Left - work.Left;
            double rightDistance = work.Right - result.Right;
            if (leftDistance <= threshold && leftDistance <= rightDistance)
                result.X = work.Left;
            else if (rightDistance <= threshold)
                result.X = work.Right - result.Width;
            return result;
        }

        // Edges contains L/R/T/B (for example R, B or LT). Delta is the pointer's
        // total displacement from mouse-down, not its displacement since the last event.
        // A valid start keeps the opposite edge fixed. An invalid/old saved size is
        // normalized first. R/B take precedence if both sides of one axis are supplied.
        public static Rect Resize(Rect start, Point delta, string edges, Rect work)
        {
            work = ValidWork(work);
            start = ClampMove(start, work);
            double minWidth = Math.Min(MinWidth, work.Width);
            double minHeight = Math.Min(MinHeight, work.Height);
            double width = Clamp(start.Width, minWidth, Math.Min(MaxWidth, work.Width));
            double height = Clamp(start.Height, minHeight, Math.Min(MaxHeight, work.Height));
            double x = Clamp(start.X, work.Left, work.Right - width);
            double y = Clamp(start.Y, work.Top, work.Bottom - height);
            edges = (edges ?? String.Empty).ToUpperInvariant();
            ResizeAxis(ref x, ref width, FiniteDelta(delta.X), edges.IndexOf('L') >= 0, edges.IndexOf('R') >= 0,
                work.Left, work.Right, minWidth, MaxWidth);
            ResizeAxis(ref y, ref height, FiniteDelta(delta.Y), edges.IndexOf('T') >= 0, edges.IndexOf('B') >= 0,
                work.Top, work.Bottom, minHeight, MaxHeight);
            return new Rect(x, y, width, height);
        }

        private static void ResizeAxis(ref double position, ref double size, double delta, bool leading, bool trailing,
            double workStart, double workEnd, double minimum, double maximum)
        {
            if (trailing)
            {
                size = Clamp(size + delta, minimum, Math.Min(maximum, workEnd - position));
            }
            else if (leading)
            {
                double opposite = position + size;
                size = Clamp(size - delta, minimum, Math.Min(maximum, opposite - workStart));
                position = opposite - size;
            }
        }

        // Corrupt persisted coordinates must never feed NaN/Infinity to WPF. This
        // limit is far outside real desktop coordinates and also prevents overflow.
        private const double CoordinateLimit = 1000000000;

        private static Rect ValidWork(Rect work)
        {
            return new Rect(Coordinate(work.X, 0), Coordinate(work.Y, 0),
                Positive(work.Width, 1), Positive(work.Height, 1));
        }

        private static double Coordinate(double value, double fallback)
        {
            return IsFinite(value) ? Clamp(value, -CoordinateLimit, CoordinateLimit) : fallback;
        }

        private static double Positive(double value, double fallback)
        {
            return IsFinite(value) && value > 0 ? Math.Min(value, CoordinateLimit) : fallback;
        }

        private static double FiniteDelta(double value) { return IsFinite(value) ? value : 0; }
        private static bool IsFinite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
        private static double Clamp(double value, double minimum, double maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }
    }
}
