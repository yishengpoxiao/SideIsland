using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace CodexIsland
{
    // Memory-only bitmap fixtures and unshown WPF trees. No screen capture,
    // global input, clipboard access or native window is required here.
    internal static class FloatingSurfaceTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static void Check(bool condition, string description) { if (!condition) throw new Exception("Floating surface: " + description); }
        private static T Read<T>(object instance, string field) { return (T)instance.GetType().GetField(field, Private).GetValue(instance); }
        internal static void Run()
        {
            string language = UiLocalization.Language, theme = UiTheme.Name; int scenarios = 0;
            try
            {
                foreach (string locale in new[] { "zh", "en" }) foreach (string appearance in new[] { "light", "dark" })
                {
                    AppAppearance.Configure(locale, appearance);
                    foreach (double width in new[] { 240.0, 320.0, 540.0, 900.0 })
                    {
                        VerifyPin(width); VerifySelection(width); scenarios += 2;
                    }
                }
                Console.WriteLine("PASS: " + scenarios + " memory-only floating surface layouts; bilingual/light-dark, narrow/wide and current system DPI rounding; no native windows or real screenshot capture.");
            }
            finally { AppAppearance.Configure(language, theme); }
        }
        private static void VerifyPin(double width)
        {
            var pin = new ScreenPinWindow(ImageQuickTests.Sample(24, 18, false));
            try
            {
                var root = (Border)pin.Content; Layout(root, width, 400);
                var toolbar = Read<Grid>(pin, "toolbar"); var zoom = Read<Slider>(pin, "zoom"); var alpha = Read<Slider>(pin, "transparency");
                Check(root.CornerRadius.TopLeft == 16 && root.Padding.Left == 8, "pin uses the shared compact card geometry");
                Check(zoom.Width.Equals(Double.NaN) && alpha.Width.Equals(Double.NaN), "slider groups can stretch within narrow footer columns");
                Check(zoom.ActualWidth >= 70 && Math.Abs(zoom.ActualWidth - alpha.ActualWidth) <= 1.01, "both sliders retain equal usable widths (" + zoom.ActualWidth + ", " + alpha.ActualWidth + " at " + width + ")");
                var close = Descendants<Button>(root).Single(button => Convert.ToString(button.Content) == "×"); Rect closeBounds = Bounds(close, toolbar);
                Check(close.Width == 28 && close.Height == 28 && closeBounds.Right <= toolbar.ActualWidth + 1.01 && closeBounds.Top >= 0, "close target is compact and aligned inside the footer");
                Check(Convert.ToString(close.ToolTip) == UiLocalization.Localize("关闭"), "close tooltip follows the language immediately");
                Check(Convert.ToString(zoom.ToolTip) == UiLocalization.Localize("缩放截图（滚轮也可缩放）") && Convert.ToString(alpha.ToolTip) == UiLocalization.Localize("截图不透明度"), "both slider tooltips follow the language immediately");
                foreach (var caption in Descendants<TextBlock>(toolbar))
                {
                    Rect bounds = Bounds(caption, toolbar);
                    Check(bounds.Left >= -1.01 && bounds.Right <= toolbar.ActualWidth + 1.01 && bounds.Bottom <= toolbar.ActualHeight + 1.01, "pin caption remains inside its footer");
                    if (caption.Text == UiLocalization.Localize("拖动图片 · 滚轮缩放")) Check(caption.TextWrapping == TextWrapping.Wrap, "drag instructions wrap in narrow pins");
                }
                pin.ZoomPercent = 250; pin.OpacityPercent = 40;
                Check(pin.ZoomPercent == 250 && Math.Abs(pin.Opacity - .4) < .001, "restyling retains live zoom and opacity semantics");
                Check(new WindowInteropHelper(pin).Handle == IntPtr.Zero, "pin fixture does not create a native window");
            }
            finally { pin.Close(); Check(pin.Snapshot == null, "closing an unshown pin still releases its bitmap"); }
        }
        private static void VerifySelection(double width)
        {
            var desktop = new Int32Rect(-1920, -480, 3840, 2160);
            var overlay = (ScreenPinSelection)Activator.CreateInstance(typeof(ScreenPinSelection), Private, null, new object[] { desktop }, null);
            try
            {
                overlay.Width = width; overlay.Height = 360;
                var canvas = (Canvas)overlay.Content; var instruction = Read<Border>(overlay, "instructionCard"); var badge = Read<Border>(overlay, "sizeCard");
                typeof(ScreenPinSelection).GetMethod("UpdateSelection", Private).Invoke(overlay, new object[] { new Int32Rect(1780, 1640, 140, 40) });
                Layout(canvas, width, 360);
                Check(instruction.CornerRadius.TopLeft == 12 && badge.CornerRadius.TopLeft == 12, "selection hints use matching rounded compact cards");
                var caption = (TextBlock)instruction.Child;
                Check(caption.Text == UiLocalization.Localize("拖动框选截图区域 · Esc 取消") && caption.TextWrapping == TextWrapping.Wrap, "bilingual selection instruction wraps instead of being cropped");
                Check(Bounds(instruction, canvas).Right <= width + 1.01, "selection instructions fit the overlay at narrow widths");
                Rect badgeBounds = Bounds(badge, canvas);
                Check(badgeBounds.Left >= 0 && badgeBounds.Top >= 0 && badgeBounds.Right <= width + 1.01 && badgeBounds.Bottom <= 360 + 1.01, "measured pixel dimensions remain inside bottom-right monitor bounds");
                UiLocalization.Text((TextBlock)badge.Child, "区域太小，请重新拖选。");
                typeof(ScreenPinSelection).GetMethod("PositionBadge", Private).Invoke(overlay, new object[] { new Rect(width - 2, 1, 0, 0), new Rect(0, 0, width, 360) }); Layout(canvas, width, 360);
                badgeBounds = Bounds(badge, canvas);
                Check(((TextBlock)badge.Child).Text == UiLocalization.Localize("区域太小，请重新拖选。") && badgeBounds.Right <= width + 1.01 && badgeBounds.Top >= 0, "long localized retry hint remains fully contained at monitor edges");
                // Repeat a short-to-long caption change before a dispatcher
                // layout pass; positioning must measure the new text now.
                UiLocalization.Text((TextBlock)badge.Child, "1 × 1");
                typeof(ScreenPinSelection).GetMethod("PositionBadge", Private).Invoke(overlay, new object[] { new Rect(width - 2, 1, 0, 0), new Rect(0, 0, width, 360) }); Layout(canvas, width, 360);
                UiLocalization.Text((TextBlock)badge.Child, "区域太小，请重新拖选。");
                typeof(ScreenPinSelection).GetMethod("PositionBadge", Private).Invoke(overlay, new object[] { new Rect(width - 2, 1, 0, 0), new Rect(0, 0, width, 360) }); Layout(canvas, width, 360);
                badgeBounds = Bounds(badge, canvas);
                Check(badgeBounds.Right <= width + 1.01 && badgeBounds.Bottom <= 360 + 1.01, "synchronous short-to-long caption changes remeasure before edge clamping");
                Check(new WindowInteropHelper(overlay).Handle == IntPtr.Zero, "selection fixture does not create a native window");
            }
            finally { overlay.Close(); }
        }
        private static void Layout(FrameworkElement root, double width, double height) { root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, root is Canvas ? height : root.DesiredSize.Height)); }
        private static Rect Bounds(FrameworkElement element, FrameworkElement root) { return element.TransformToAncestor(root).TransformBounds(new Rect(new Point(), element.RenderSize)); }
        private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
        {
            if (root is T) yield return (T)root;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (T child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
        }
    }
}
