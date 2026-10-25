using System;
using SideIsland.Core;

namespace SideIsland.Tests;

internal static class PlacementTests
{
    private static int checks;
    public static void Run()
    {
        checks = 0;
        RectTests(); NormalEdges(); NotchGeometry(); RejectedNotches(); MultipleDisplaysAndScaling(); InputBounds(); PropertyMatrix();
        Console.WriteLine($"PASS: {checks} placement checks; four edges, notch-safe content and click wings, negative screen coordinates, scale invariance, small screens, invalid inputs and center-preserving expansion.");
    }

    private static void RectTests()
    {
        var rectangle = new RectD(-100, -50, 200, 100);
        Check(rectangle.Right == 100 && rectangle.Bottom == 50 && rectangle.CenterX == 0 && rectangle.CenterY == 0, "rectangle arithmetic");
        Check(rectangle.Contains(-100, -50) && rectangle.Contains(100, 50), "rectangle includes its boundary");
        Check(!rectangle.Contains(101, 0) && !rectangle.Contains(double.NaN, 0), "points outside or nonfinite are not contained");
        Check(rectangle.Contains(new RectD(-20, -20, 40, 40)) && !rectangle.Contains(new RectD(-20, -20, 400, 40)), "rectangle containment uses both corners");
        Check(!new RectD(0, 0, double.PositiveInfinity, 20).IsFinite, "nonfinite bounds are invalid");
    }

    private static void NormalEdges()
    {
        var display = new DisplayArea("main", new RectD(0, 0, 1920, 1080), new RectD(40, 28, 1880, 1012), 1, true);
        foreach (DockEdge edge in new[] { DockEdge.Left, DockEdge.Right, DockEdge.Top, DockEdge.Bottom })
        {
            var compact = PlacementPolicy.Place(display, edge, false, false, alongEdge: .5);
            var expanded = PlacementPolicy.Place(display, edge, false, true, alongEdge: .5);
            Check(compact.Edge == edge && expanded.Edge == edge && !compact.UsesNotch && compact.Occlusion == null, "ordinary edge has no synthetic notch");
            Check(display.WorkArea.Contains(compact.Bounds) && display.WorkArea.Contains(expanded.Bounds), "both sizes stay in the work area");
            Check(expanded.Bounds.Width == 480 && expanded.Bounds.Height == 640, "ordinary expanded default dimensions");
            AssertContact(compact.Bounds, display.WorkArea, edge); AssertContact(expanded.Bounds, display.WorkArea, edge);
            if (edge is DockEdge.Left or DockEdge.Right)
            {
                Check(compact.Bounds.Width == PlacementPolicy.VerticalCompactWidth && compact.Bounds.Height == PlacementPolicy.VerticalCompactHeight, "vertical compact dimensions");
                Near(compact.Bounds.CenterY, expanded.Bounds.CenterY, "vertical center preserved through expansion");
            }
            else
            {
                Check(compact.Bounds.Width == PlacementPolicy.HorizontalCompactWidth && compact.Bounds.Height == PlacementPolicy.HorizontalCompactHeight, "horizontal compact dimensions");
                Near(compact.Bounds.CenterX, expanded.Bounds.CenterX, "horizontal center preserved through expansion");
            }
        }
        Check(PlacementPolicy.Place(display, DockEdge.Auto, true, false).Edge == DockEdge.Right, "auto without a notch falls back to right");
        Check(PlacementPolicy.NearestEdge(display.WorkArea, 41, 500) == DockEdge.Left, "nearest left");
        Check(PlacementPolicy.NearestEdge(display.WorkArea, 1919, 500) == DockEdge.Right, "nearest right");
        Check(PlacementPolicy.NearestEdge(display.WorkArea, 900, 29) == DockEdge.Top, "nearest top");
        Check(PlacementPolicy.NearestEdge(display.WorkArea, 900, 1039) == DockEdge.Bottom, "nearest bottom");
        Check(PlacementPolicy.NearestEdge(new RectD(0, 0, 100, 100), 50, 50) == DockEdge.Left, "edge ties are deterministic");
    }

    private static void NotchGeometry()
    {
        var notch = new RectD(663.5, 0, 185, 32);
        var display = new DisplayArea("notched", new RectD(0, 0, 1512, 982), new RectD(0, 32, 1512, 900), 2, true, notch);
        var compact = PlacementPolicy.Place(display, DockEdge.Auto, true, false);
        var expanded = PlacementPolicy.Place(display, DockEdge.Top, true, true);
        foreach (var placement in new[] { compact, expanded })
        {
            Check(placement.UsesNotch && placement.Edge == DockEdge.Top && placement.Occlusion.HasValue, "real notch wins auto/top preference");
            Check(display.Bounds.Contains(placement.Bounds), "notch island stays inside full screen bounds");
            Near(placement.Bounds.Y, display.Bounds.Y, "notch top touches screen with no gap");
            Check(placement.Bounds.Bottom <= display.WorkArea.Bottom, "notch island avoids bottom dock");
            var local = placement.Occlusion!.Value;
            Near(local.X + placement.Bounds.X, notch.X, "occlusion x is local to window");
            Near(local.Y + placement.Bounds.Y, notch.Y, "occlusion y is local to window");
            Near(local.Width, notch.Width, "camera width is detected, not replaced with a constant");
            Check(local.X >= PlacementPolicy.MinimumNotchWing && placement.Bounds.Width - local.Right >= PlacementPolicy.MinimumNotchWing, "both visible click wings meet their minimum");
            Check(placement.ContentTop >= local.Bottom + 8 && placement.Bounds.Height >= placement.ContentTop, "expanded content avoids camera pixels");
        }
        Near(compact.Bounds.Width, 337, "compact width fits actual camera plus two wings");
        Check(compact.Bounds.Height >= notch.Height + 8 && expanded.Bounds.Height > compact.Bounds.Height, "expand grows below the camera");
        Check(!PlacementPolicy.Place(display, DockEdge.Top, false, false).UsesNotch, "notch preference can be disabled");
        foreach (DockEdge edge in new[] { DockEdge.Left, DockEdge.Right, DockEdge.Bottom })
            Check(!PlacementPolicy.Place(display, edge, true, false).UsesNotch, "explicit side/bottom selection takes priority over automatic notch");

        // A valid camera housing need not be exactly centered in a virtual display.
        var shifted = display with { Notch = new RectD(76, 0, 30, 32) };
        var shiftedResult = PlacementPolicy.Place(shifted, DockEdge.Auto, true, false);
        Check(shiftedResult.UsesNotch && shiftedResult.Occlusion!.Value.X >= 76, "near-edge camera preserves both wings while clamping the host");
        var otherWidth = display with { Notch = new RectD(646, 0, 220, 38) };
        var otherResult = PlacementPolicy.Place(otherWidth, DockEdge.Auto, true, false);
        Near(otherResult.Bounds.Width, 372, "a different actual notch produces a different host width");
        Check(otherResult.ContentTop == 46 && otherResult.Bounds.Height >= 46, "a different actual safe area sets the content inset");
    }

    private static void RejectedNotches()
    {
        var bounds = new RectD(0, 0, 1440, 900);
        var work = new RectD(0, 30, 1440, 830);
        foreach (RectD? invalid in new RectD?[] { null, new RectD(0, 0, 0, 0), new RectD(double.NaN, 0, 200, 32),
            new RectD(600, 100, 200, 32), new RectD(600, 0, 200, 200), new RectD(20, 0, 200, 32),
            new RectD(600, 0, 1000, 32), new RectD(50, 0, 800, 32), new RectD(600, -1, 200, 32),
            new RectD(600, 0, -10, 32), new RectD(600, 0, 200, double.PositiveInfinity) })
        {
            var result = PlacementPolicy.Place(new DisplayArea("invalid", bounds, work, 1, true, invalid), DockEdge.Auto, true, true);
            Check(!result.UsesNotch && result.Occlusion == null && result.Edge == DockEdge.Right && work.Contains(result.Bounds), "invalid/oversized camera geometry safely uses ordinary docking");
        }
        var shortScreen = new DisplayArea("short", new RectD(0, 0, 400, 35), new RectD(0, 32, 400, 3), 1, true, new RectD(100, 0, 160, 32));
        Check(!PlacementPolicy.Place(shortScreen, DockEdge.Auto, true, false).UsesNotch, "camera plus padding cannot exceed the actual available height");
    }

    private static void MultipleDisplaysAndScaling()
    {
        var bounds = new RectD(-2560, -400, 2560, 1440);
        var work = new RectD(-2520, -370, 2520, 1370);
        var camera = new RectD(-1370, -400, 180, 32);
        var reference = new DisplayArea("left", bounds, work, 1, false, camera);
        foreach (double scale in new[] { 1.0, 1.25, 1.5, 2.0 })
        {
            var screen = reference with { Scale = scale };
            foreach (DockEdge edge in Enum.GetValues<DockEdge>()) foreach (bool expanded in new[] { false, true })
            {
                var result = PlacementPolicy.Place(screen, edge, true, expanded);
                var expected = PlacementPolicy.Place(reference, edge, true, expanded);
                Check(result == expected, "DIP coordinates are not multiplied by scale a second time");
                Check((result.UsesNotch ? bounds : work).Contains(result.Bounds), "negative multi-display coordinates remain on their chosen display");
            }
        }
        var right = new DisplayArea("right", new RectD(1920, 200, 1920, 1080), new RectD(1920, 230, 1920, 1010), 1.5, false);
        var placement = PlacementPolicy.Place(right, DockEdge.Top, true, false, alongEdge: .5);
        Near(placement.Bounds.CenterX, right.WorkArea.CenterX, "external display without notch has an ordinary top-center fallback");
    }

    private static void InputBounds()
    {
        var screen = new DisplayArea("small", new RectD(0, 0, 240, 180), new RectD(0, 20, 240, 140), 1, true);
        foreach (DockEdge edge in Enum.GetValues<DockEdge>())
        {
            var result = PlacementPolicy.Place(screen, edge, false, true, 1_000_000, 1_000_000, 100);
            Check(result.Bounds.Width == 240 && result.Bounds.Height == 140 && screen.WorkArea.Contains(result.Bounds), "small screens take precedence over nominal minimums");
        }
        var large = new DisplayArea("large", new RectD(0, 0, 3000, 2000), new RectD(0, 20, 3000, 1940), 1, true);
        var low = PlacementPolicy.Place(large, DockEdge.Top, false, true, -2, -1, -99);
        Check(low.Bounds.Width == 320 && low.Bounds.Height == 360 && low.Bounds.X == large.WorkArea.X, "expanded dimensions and negative anchor clamp");
        var high = PlacementPolicy.Place(large, DockEdge.Bottom, false, true, 5000, 5000, 99);
        Check(high.Bounds.Width == 900 && high.Bounds.Height == 900 && high.Bounds.Right == large.WorkArea.Right, "maximum dimensions and overflowing anchor clamp");
        var fallback = PlacementPolicy.Place(large, DockEdge.Right, false, true, double.NaN, double.PositiveInfinity, double.NaN);
        Check(fallback.Bounds.Width == 480 && fallback.Bounds.Height == 640 && large.WorkArea.Contains(fallback.Bounds), "nonfinite preferences return finite defaults");
        var overlappingWork = large with { WorkArea = new RectD(-100, -100, 3200, 2200) };
        Check(large.Bounds.Contains(PlacementPolicy.Place(overlappingWork, DockEdge.Left, false, true).Bounds), "stale overlarge work area is intersected with the physical display");
        Throws(() => PlacementPolicy.Place(null!, DockEdge.Auto, true, false), "null display rejected");
        Throws(() => PlacementPolicy.Place(screen with { Bounds = new RectD(0, 0, 0, 10) }, DockEdge.Auto, true, false), "empty physical bounds rejected");
        Throws(() => PlacementPolicy.Place(screen with { Bounds = new RectD(0, 0, double.PositiveInfinity, 10) }, DockEdge.Auto, true, false), "nonfinite physical bounds rejected");
        Throws(() => PlacementPolicy.Place(screen with { WorkArea = new RectD(1000, 1000, 20, 20) }, DockEdge.Auto, true, false), "disjoint work area rejected");
        foreach (double scale in new[] { 0.0, -1.0, double.NaN, double.PositiveInfinity })
            Throws(() => PlacementPolicy.Place(screen with { Scale = scale }, DockEdge.Auto, true, false), "invalid display scale rejected");
        Throws(() => PlacementPolicy.Place(screen, (DockEdge)200, true, false), "unknown dock enum rejected");
        Throws(() => PlacementPolicy.NearestEdge(new RectD(0, 0, -1, 20), 0, 0), "invalid snap bounds rejected");
        Throws(() => PlacementPolicy.NearestEdge(screen.WorkArea, double.NaN, 0), "nonfinite snap center rejected");
        Check(PlacementPolicy.SafeHoverDelay(int.MinValue) == 100 && PlacementPolicy.SafeHoverDelay(int.MaxValue) == 5000
            && PlacementPolicy.SafeHoverDelay(650) == 650, "hover setting has safe millisecond limits");
    }

    private static void PropertyMatrix()
    {
        var random = new Random(77191);
        for (int i = 0; i < 120; i++)
        {
            double x = random.Next(-6000, 6000), y = random.Next(-2000, 2000);
            double width = random.Next(70, 6000), height = random.Next(70, 2500);
            var work = new RectD(x + 3, y + 10, width - 6, height - 20);
            var screen = new DisplayArea("matrix", new RectD(x, y, width, height), work, new[] { 1.0, 1.25, 1.5, 2.0 }[i % 4], false);
            foreach (DockEdge edge in new[] { DockEdge.Top, DockEdge.Bottom, DockEdge.Left, DockEdge.Right })
            foreach (bool expanded in new[] { false, true })
            foreach (double ratio in new[] { -.2, .0, .45, 1.0, 1.4 })
            {
                var result = PlacementPolicy.Place(screen, edge, true, expanded, random.Next(-400, 1400), random.Next(-400, 1400), ratio);
                Check(result.Bounds.IsFinite && result.Bounds.Width > 0 && result.Bounds.Height > 0 && work.Contains(result.Bounds), "all randomized ordinary placements are finite, positive and contained");
                AssertContact(result.Bounds, work, edge);
            }
        }
    }

    private static void AssertContact(RectD window, RectD work, DockEdge edge)
    {
        if (edge == DockEdge.Left) Near(window.X, work.X, "left contact");
        else if (edge == DockEdge.Right) Near(window.Right, work.Right, "right contact");
        else if (edge == DockEdge.Top) Near(window.Y, work.Y, "top contact");
        else Near(window.Bottom, work.Bottom, "bottom contact");
    }
    private static void Near(double actual, double expected, string message) => Check(Math.Abs(actual - expected) < .00001, message);
    private static void Throws(Action action, string message)
    {
        bool thrown = false;
        try { action(); } catch (ArgumentException) { thrown = true; }
        Check(thrown, message);
    }
    private static void Check(bool condition, string message)
    { checks++; if (!condition) throw new InvalidOperationException("Placement: " + message); }
}
