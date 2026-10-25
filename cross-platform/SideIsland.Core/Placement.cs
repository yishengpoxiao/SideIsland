using System;

namespace SideIsland.Core;

/// <summary>Device-independent screen coordinates, with the origin at the top left.</summary>
public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width)
        && double.IsFinite(Height) && double.IsFinite(Right) && double.IsFinite(Bottom);
    public bool Contains(double x, double y) => IsFinite && Width >= 0 && Height >= 0
        && double.IsFinite(x) && double.IsFinite(y) && x >= X && x <= Right && y >= Y && y <= Bottom;
    public bool Contains(RectD other) => other.IsFinite && other.Width >= 0 && other.Height >= 0
        && Contains(other.X, other.Y) && Contains(other.Right, other.Bottom);
}

public enum DockEdge { Auto, Top, Bottom, Left, Right }

/// <summary>All rectangles use the same global DIP coordinate system. Scale is metadata, not a second multiplier.</summary>
public sealed record DisplayArea(string Id, RectD Bounds, RectD WorkArea, double Scale, bool Primary, RectD? Notch = null);

/// <summary>Occlusion is window-local DIP; ContentTop is the safe top inset for expanded controls.</summary>
public sealed record IslandPlacement(RectD Bounds, DockEdge Edge, RectD? Occlusion, double ContentTop, bool UsesNotch);

public static class PlacementPolicy
{
    public const double MinimumNotchWing = 76;
    public const double HorizontalCompactWidth = 232;
    public const double HorizontalCompactHeight = 48;
    public const double VerticalCompactWidth = 76;
    public const double VerticalCompactHeight = 218;

    /// <summary>
    /// Places an island without changing coordinate systems. AlongEdge is the desired window center
    /// as a fraction of the work area's relevant axis; preserving it preserves the center on expansion.
    /// Auto prefers a valid notch when requested, otherwise the right edge. An explicit side/bottom
    /// choice takes precedence over notch preference.
    /// </summary>
    public static IslandPlacement Place(DisplayArea screen, DockEdge edge, bool preferNotch, bool expanded,
        double expandedWidth = 480, double expandedHeight = 640, double alongEdge = .45)
    {
        ArgumentNullException.ThrowIfNull(screen);
        RequireArea(screen.Bounds, nameof(screen.Bounds));
        RequireArea(screen.WorkArea, nameof(screen.WorkArea));
        if (!double.IsFinite(screen.Scale) || screen.Scale <= 0)
            throw new ArgumentOutOfRangeException(nameof(screen.Scale), "Display scaling must be finite and positive.");
        if (!Enum.IsDefined(edge)) throw new ArgumentOutOfRangeException(nameof(edge));
        RectD work = Intersection(screen.Bounds, screen.WorkArea);
        RequireArea(work, nameof(screen.WorkArea));

        double width = Dimension(expandedWidth, 480, 320, 900);
        double height = Dimension(expandedHeight, 640, 360, 900);
        double fraction = double.IsFinite(alongEdge) ? Math.Clamp(alongEdge, 0, 1) : .45;
        if (preferNotch && (edge == DockEdge.Auto || edge == DockEdge.Top)
            && TryNotch(screen.Bounds, work, screen.Notch, out RectD notch))
        {
            double compactWidth = Math.Max(HorizontalCompactWidth, notch.Width + 2 * MinimumNotchWing);
            double contentTop = notch.Bottom - screen.Bounds.Y + 8;
            double availableHeight = Math.Min(work.Bottom, screen.Bounds.Bottom) - screen.Bounds.Y;
            double notchWidth = expanded ? Math.Max(width, compactWidth) : compactWidth;
            double notchHeight = expanded ? Math.Max(height, contentTop) : Math.Max(42, contentTop);
            notchWidth = Math.Min(notchWidth, screen.Bounds.Width);
            notchHeight = Math.Min(notchHeight, availableHeight);
            double x = Math.Clamp(notch.CenterX - notchWidth / 2, screen.Bounds.X, screen.Bounds.Right - notchWidth);
            var window = new RectD(x, screen.Bounds.Y, notchWidth, notchHeight);
            var localNotch = new RectD(notch.X - window.X, notch.Y - window.Y, notch.Width, notch.Height);
            return new IslandPlacement(window, DockEdge.Top, localNotch, contentTop, true);
        }

        DockEdge actual = edge == DockEdge.Auto ? DockEdge.Right : edge;
        bool horizontal = actual is DockEdge.Top or DockEdge.Bottom;
        double windowWidth = Math.Min(expanded ? width : horizontal ? HorizontalCompactWidth : VerticalCompactWidth, work.Width);
        double windowHeight = Math.Min(expanded ? height : horizontal ? HorizontalCompactHeight : VerticalCompactHeight, work.Height);
        double left = actual == DockEdge.Left ? work.X : actual == DockEdge.Right ? work.Right - windowWidth
            : PositionAlong(work.X, work.Width, windowWidth, fraction);
        double top = actual == DockEdge.Top ? work.Y : actual == DockEdge.Bottom ? work.Bottom - windowHeight
            : PositionAlong(work.Y, work.Height, windowHeight, fraction);
        return new IslandPlacement(new RectD(left, top, windowWidth, windowHeight), actual, null, 0, false);
    }

    /// <summary>Returns the nearest edge by distance from the supplied center. Exact ties prefer left, right, top, bottom.</summary>
    public static DockEdge NearestEdge(RectD working, double centerX, double centerY)
    {
        RequireArea(working, nameof(working));
        if (!double.IsFinite(centerX)) throw new ArgumentOutOfRangeException(nameof(centerX));
        if (!double.IsFinite(centerY)) throw new ArgumentOutOfRangeException(nameof(centerY));
        DockEdge result = DockEdge.Left;
        double best = Math.Abs(centerX - working.X);
        Consider(DockEdge.Right, Math.Abs(centerX - working.Right));
        Consider(DockEdge.Top, Math.Abs(centerY - working.Y));
        Consider(DockEdge.Bottom, Math.Abs(centerY - working.Bottom));
        return result;

        void Consider(DockEdge candidate, double distance)
        { if (distance < best) { result = candidate; best = distance; } }
    }

    /// <summary>Safe configurable hover delay in milliseconds, matching the desktop preference's bounds.</summary>
    public static double SafeHoverDelay(int value) => Math.Clamp(value, 100, 5000);

    private static bool TryNotch(RectD bounds, RectD work, RectD? candidate, out RectD notch)
    {
        notch = candidate.GetValueOrDefault();
        if (!candidate.HasValue || !notch.IsFinite || notch.Width <= 0 || notch.Height <= 0 || !bounds.Contains(notch)) return false;
        // A camera cutout touches the top of the physical display. Other safe-area or dock
        // rectangles must not be mistaken for it; an impossible gap falls back to ordinary docking.
        if (notch.Y - bounds.Y > 1 || notch.Height > Math.Min(96, bounds.Height * .2)) return false;
        if (notch.Width > bounds.Width / 2 || notch.Width + 2 * MinimumNotchWing > Math.Min(900, bounds.Width)) return false;
        if (notch.X - bounds.X < MinimumNotchWing || bounds.Right - notch.Right < MinimumNotchWing) return false;
        return notch.Bottom - bounds.Y + 8 <= Math.Min(bounds.Bottom, work.Bottom) - bounds.Y;
    }

    private static double PositionAlong(double start, double span, double length, double fraction)
        => Math.Clamp(start + span * fraction - length / 2, start, start + span - length);

    private static double Dimension(double value, double fallback, double minimum, double maximum)
        => Math.Clamp(double.IsFinite(value) ? value : fallback, minimum, maximum);

    private static RectD Intersection(RectD left, RectD right)
    {
        double x = Math.Max(left.X, right.X), y = Math.Max(left.Y, right.Y);
        return new RectD(x, y, Math.Min(left.Right, right.Right) - x, Math.Min(left.Bottom, right.Bottom) - y);
    }

    private static void RequireArea(RectD area, string name)
    {
        if (!area.IsFinite || area.Width <= 0 || area.Height <= 0)
            throw new ArgumentOutOfRangeException(name, "Display rectangles must have finite coordinates and a positive area.");
    }
}
