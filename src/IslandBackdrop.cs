using System;
using System.Windows;
using System.Windows.Media;

namespace CodexIsland
{
    // Only this small, content-free visual receives the shadow. Its animated
    // rectangle affects rendering, never the measure/arrange of page content.
    internal sealed class IslandBackdrop : FrameworkElement
    {
        internal static readonly DependencyProperty VisualBoundsProperty = DependencyProperty.Register(
            "VisualBounds", typeof(Rect), typeof(IslandBackdrop), new FrameworkPropertyMetadata(Rect.Empty, FrameworkPropertyMetadataOptions.AffectsRender));
        internal static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
            "CornerRadius", typeof(double), typeof(IslandBackdrop), new FrameworkPropertyMetadata(32.0, FrameworkPropertyMetadataOptions.AffectsRender));
        // Painting resolves the current frozen palette without mutating a
        // brush already referenced by retired native RenderData.
        internal IslandBackdrop() { IsHitTestVisible = false; }
        internal Rect VisualBounds { get { return (Rect)GetValue(VisualBoundsProperty); } set { SetValue(VisualBoundsProperty, value); } }
        internal double CornerRadius { get { return (double)GetValue(CornerRadiusProperty); } set { SetValue(CornerRadiusProperty, value); } }
        protected override void OnRender(DrawingContext drawing)
        {
            Rect bounds = VisualBounds;
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return;
            // Inset the stroke so transparent areas outside the island remain
            // truly transparent in the layered HWND's hit-test surface.
            bounds.Inflate(-.5, -.5);
            var outline = new Pen(Display.Brush("#30383B"), 1); outline.Freeze();
            drawing.DrawRoundedRectangle(Display.Brush("#111518"), outline, bounds, CornerRadius, CornerRadius);
        }
    }
}
