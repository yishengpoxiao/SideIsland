using System;
using System.Windows;

namespace CodexIsland
{
    internal static class WindowGeometryTests
    {
        public static void Run()
        {
            var desktop = new Rect(0, 0, 1920, 1080);
            var leftMonitor = new Rect(-1920, -200, 1920, 1080);
            Same(WindowGeometry.ClampMove(new Rect(500, 100, 72, 220), desktop), new Rect(500, 100, 72, 220), "Collapsed island can occupy the middle of the screen");
            Same(WindowGeometry.ClampMove(new Rect(-2100, -400, 72, 220), leftMonitor), new Rect(-1920, -200, 72, 220), "Move clamps against negative-coordinate monitor origin");
            Same(WindowGeometry.ClampMove(new Rect(100, 900, 72, 220), leftMonitor), new Rect(-72, 660, 72, 220), "Move keeps the whole island in the monitor work area");
            Same(WindowGeometry.ClampMove(new Rect(-100, -50, 2500, 2000), desktop), desktop, "Oversized restored windows fit the available monitor");

            Same(WindowGeometry.SnapToHorizontalEdge(new Rect(24, 123, 72, 220), desktop, 24), new Rect(0, 123, 72, 220), "Left snap includes the threshold and preserves vertical position");
            Same(WindowGeometry.SnapToHorizontalEdge(new Rect(25, 123, 72, 220), desktop, 24), new Rect(25, 123, 72, 220), "Beyond threshold remains freely positioned");
            Same(WindowGeometry.SnapToHorizontalEdge(new Rect(-92, -150, 72, 220), leftMonitor, 24), new Rect(-72, -150, 72, 220), "Right snap works on a negative-coordinate monitor");

            FourEdgeDocking(desktop, leftMonitor);

            var start = new Rect(-1500, 0, 500, 600);
            Rect changed = WindowGeometry.Resize(start, new Point(100, 75), "LT", leftMonitor);
            Same(changed, new Rect(-1400, 75, 400, 525), "Top-left corner changes both dimensions and holds opposite corner");
            Check(changed.Right == start.Right && changed.Bottom == start.Bottom, "Top-left resize preserves both opposite edges");
            Same(WindowGeometry.Resize(start, new Point(1000, 1000), "LT", leftMonitor), new Rect(-1360, 240, 360, 360), "Minimum sizes stop dragged edges before they cross opposite edges");
            Same(WindowGeometry.Resize(start, new Point(-1000, -1000), "LT", leftMonitor), new Rect(-1900, -200, 900, 800), "Expansion respects width maximum and top work-area boundary");
            Same(WindowGeometry.Resize(start, new Point(10000, 10000), "RB", leftMonitor), new Rect(-1500, 0, 900, 880), "Bottom-right expansion respects maximum width and monitor bottom");
            Same(WindowGeometry.Resize(start, new Point(-10000, -10000), "RB", leftMonitor), new Rect(-1500, 0, 360, 360), "Bottom-right shrink preserves top-left corner");
            Same(WindowGeometry.Resize(start, new Point(100, 100), "R", leftMonitor), new Rect(-1500, 0, 600, 600), "Single edge does not resize the other axis");
            Same(WindowGeometry.Resize(start, new Point(100, 100), null, leftMonitor), start, "No supplied edge keeps a valid rectangle unchanged");

            var tall = new Rect(0, 0, 1920, 2160);
            Same(WindowGeometry.Resize(new Rect(100, 100, 500, 600), new Point(3000, 3000), "RB", tall), new Rect(100, 100, 900, 1100), "Maximum width and height apply on large work areas");
            var small = new Rect(-100, 20, 240, 180);
            Same(WindowGeometry.Resize(new Rect(-75, 30, 72, 100), new Point(-1000, -1000), "LT", small), small, "Minimum size falls back to the available small-screen work area");
            Same(WindowGeometry.Resize(new Rect(1700, 900, 100, 100), new Point(0, 0), "RB", desktop), new Rect(1560, 720, 360, 360), "Outdated too-small saved sizes normalize inside the screen");

            foreach (double invalid in new[] { Double.NaN, Double.PositiveInfinity, Double.NegativeInfinity })
            {
                Finite(WindowGeometry.ClampMove(new Rect(invalid, invalid, 72, 220), desktop), "Nonfinite positions recover safely");
                Finite(WindowGeometry.ClampMove(new Rect(0, 0, Double.NaN, Double.NaN), desktop), "Nonfinite sizes recover safely");
                Same(WindowGeometry.Resize(start, new Point(invalid, invalid), "LT", leftMonitor), start, "Nonfinite pointer deltas are ignored");
            }
            Finite(WindowGeometry.ClampMove(Rect.Empty, Rect.Empty), "Empty saved/work rectangles recover to a finite visible rectangle");
            Finite(WindowGeometry.Resize(Rect.Empty, new Point(Double.MaxValue, Double.MaxValue), "RB", new Rect(Double.MaxValue, Double.MaxValue, Double.MaxValue, Double.MaxValue)), "Extreme finite input cannot overflow coordinates");

            // Exercise all eight handles on several monitor layouts without OS/UI activity.
            foreach (Rect work in new[] { desktop, leftMonitor, tall, small })
            foreach (string edges in new[] { "L", "R", "T", "B", "LT", "RT", "LB", "RB" })
            foreach (double displacement in new[] { -5000.0, -20.0, 0.0, 20.0, 5000.0 })
            {
                Rect result = WindowGeometry.Resize(new Rect(work.Left + 40, work.Top + 30, 500, 600), new Point(displacement, displacement), edges, work);
                Finite(result, "All handle results remain finite");
                Check(result.Left >= work.Left && result.Top >= work.Top && result.Right <= work.Right && result.Bottom <= work.Bottom, "All handles stay in their working area");
                Check(result.Width >= Math.Min(360, work.Width) && result.Height >= Math.Min(360, work.Height), "All handles respect the effective minimum");
                Check(result.Width <= Math.Min(900, work.Width) && result.Height <= Math.Min(1100, work.Height), "All handles respect the effective maximum");
            }
        }

        private static void FourEdgeDocking(Rect desktop, Rect leftMonitor)
        {
            Check(WindowGeometry.NearestEdge(new Rect(10, 400, 88, 238), desktop) == "left", "Dragging near the left edge chooses left");
            Check(WindowGeometry.NearestEdge(new Rect(1822, 400, 88, 238), desktop) == "right", "Dragging near the right edge chooses right");
            Check(WindowGeometry.NearestEdge(new Rect(800, 0, 88, 238), desktop) == "top", "Dragging near the top edge chooses top");
            Check(WindowGeometry.NearestEdge(new Rect(800, 842, 88, 238), desktop) == "bottom", "Dragging near the bottom edge chooses bottom");
            Check(WindowGeometry.NearestEdge(new Rect(-1910, 100, 88, 238), leftMonitor) == "left", "Nearest edge supports negative monitor coordinates");
            Check(WindowGeometry.NearestEdge(new Rect(-2100, 100, 88, 238), leftMonitor) == "left", "A pointer position outside the work area still chooses the nearest edge");
            var square = new Rect(0, 0, 1000, 1000);
            var center = new Rect(456, 381, 88, 238);
            foreach (string edge in new[] { "left", "right", "top", "bottom" })
                Check(WindowGeometry.NearestEdge(center, square, edge) == edge, "Equal distances retain the preferred edge");
            Check(WindowGeometry.NearestEdge(center, square, "invalid") == "right", "Invalid preferred edge has a stable default");
            Check(WindowGeometry.NearestEdge(new Rect(56, -19, 88, 238), square, "right") == "left", "A tie excluding the preferred edge has stable left-before-top ordering");

            var horizontal = new Rect(-1500, 100, 238, 88);
            Same(WindowGeometry.DockCompact(horizontal, leftMonitor, "left"), new Rect(-1920, 25, 88, 238), "Left docking rotates the footprint and preserves its vertical center");
            Same(WindowGeometry.DockCompact(horizontal, leftMonitor, "right"), new Rect(-88, 25, 88, 238), "Right docking rotates the footprint and preserves its vertical center");
            var vertical = new Rect(-1500, 100, 88, 238);
            Same(WindowGeometry.DockCompact(vertical, leftMonitor, "top"), new Rect(-1575, -200, 238, 88), "Top docking rotates the footprint and preserves its horizontal center");
            Same(WindowGeometry.DockCompact(vertical, leftMonitor, "bottom"), new Rect(-1575, 792, 238, 88), "Bottom docking rotates the footprint and preserves its horizontal center");
            Same(WindowGeometry.DockCompact(new Rect(-3000, -1000, 88, 238), leftMonitor, "top"), new Rect(-1920, -200, 238, 88), "Horizontal docking clamps the along-edge position at a corner");
            Same(WindowGeometry.DockCompact(new Rect(2000, 2000, 88, 238), leftMonitor, "right"), new Rect(-88, 642, 88, 238), "Vertical docking clamps the along-edge position at a corner");

            string[] edges = { "left", "right", "top", "bottom" };
            Rect[] compacts = { new Rect(-1920, 281, 88, 238), new Rect(-88, 281, 88, 238),
                new Rect(-1079, -200, 238, 88), new Rect(-1079, 792, 238, 88) };
            Rect[] expanded = { new Rect(-1920, 100, 600, 600), new Rect(-600, 100, 600, 600),
                new Rect(-1260, -200, 600, 600), new Rect(-1260, 280, 600, 600) };
            for (int i = 0; i < edges.Length; i++)
            {
                Same(WindowGeometry.PlaceFromCompact(compacts[i], new Size(600, 600), edges[i], leftMonitor), expanded[i], "Expansion preserves its docked edge and extends into the screen");
                Same(WindowGeometry.CompactFromExpanded(expanded[i], edges[i], leftMonitor), compacts[i], "Unclamped expansion and collapse preserve the same anchor");
            }

            Same(WindowGeometry.PlaceFromCompact(new Rect(0, 0, 238, 88), new Size(600, 600), "top", desktop), new Rect(0, 0, 600, 600), "Expansion near a corner is kept fully on-screen");
            Same(WindowGeometry.CompactFromExpanded(new Rect(100, 200, 600, 600), "bottom", desktop), new Rect(281, 712, 238, 88), "Inverse placement preserves the selected expanded edge before any explicit docking");
            var tiny = new Rect(-100, 20, 40, 60);
            foreach (string edge in edges)
            {
                Same(WindowGeometry.DockCompact(vertical, tiny, edge), tiny, "Compact footprint shrinks to fit tiny work areas on every edge");
                Same(WindowGeometry.PlaceFromCompact(vertical, new Size(2000, 2000), edge, tiny), tiny, "Expanded footprint shrinks to fit tiny work areas on every edge");
                Same(WindowGeometry.CompactFromExpanded(expanded[0], edge, tiny), tiny, "Collapse remains inside tiny work areas");
                foreach (Rect invalid in new[] { Rect.Empty, new Rect(Double.NaN, Double.PositiveInfinity, Double.NaN, Double.NaN),
                    new Rect(Double.MaxValue, Double.MaxValue, Double.MaxValue, Double.MaxValue) })
                {
                    Finite(WindowGeometry.DockCompact(invalid, leftMonitor, edge), "Invalid drag geometry recovers to a finite compact footprint");
                    Finite(WindowGeometry.PlaceFromCompact(invalid, new Size(Double.NaN, Double.NaN), edge, leftMonitor), "Invalid expansion geometry recovers safely");
                    Finite(WindowGeometry.CompactFromExpanded(invalid, edge, leftMonitor), "Invalid collapse geometry recovers safely");
                    Check(Array.IndexOf(edges, WindowGeometry.NearestEdge(invalid, leftMonitor, edge)) >= 0, "Invalid geometry still selects a real edge");
                }
            }
        }

        private static void Same(Rect actual, Rect expected, string message)
        {
            Check(Math.Abs(actual.X - expected.X) < 0.00001 && Math.Abs(actual.Y - expected.Y) < 0.00001 &&
                Math.Abs(actual.Width - expected.Width) < 0.00001 && Math.Abs(actual.Height - expected.Height) < 0.00001, message);
        }

        private static void Finite(Rect value, string message)
        {
            foreach (double component in new[] { value.X, value.Y, value.Width, value.Height, value.Right, value.Bottom })
                Check(!Double.IsNaN(component) && !Double.IsInfinity(component), message);
            Check(value.Width > 0 && value.Height > 0, message);
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception("Window geometry: " + message);
        }
    }
}
