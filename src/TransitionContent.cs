using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace CodexIsland
{
    internal partial class IslandWindow
    {
        private Grid contentLayer;
        private Image outgoingImage;
        private RectangleGeometry outgoingClip;
        private Rect outgoingFromClip, outgoingToClip;
        private ContentSnapshot pendingTransitionSnapshot;
        internal double LastTransitionSnapshotMilliseconds;

        private sealed class ContentSnapshot
        {
            internal BitmapSource Source;
            internal Rect Bounds;
        }

        private void CaptureTransitionContent(bool animate)
        {
            pendingTransitionSnapshot = null;
            LastTransitionSnapshotMilliseconds = 0;
            if (!animate || !SystemParameters.ClientAreaAnimation || !IsVisible || contentLayer == null ||
                contentLayer.ActualWidth <= 0 || contentLayer.ActualHeight <= 0) return;
            var source = PresentationSource.FromVisual(this);
            Matrix scale = source == null ? Matrix.Identity : source.CompositionTarget.TransformToDevice;
            int width = (int)Math.Ceiling(contentLayer.ActualWidth * scale.M11);
            int height = (int)Math.Ceiling(contentLayer.ActualHeight * scale.M22);
            // Keep transient memory bounded even on an unusually large desktop.
            // A failed/oversized capture shows the incoming content immediately.
            if (width <= 0 || height <= 0 || (long)width * height > 12000000) return;
            var watch = Stopwatch.StartNew();
            try
            {
                var bitmap = new RenderTargetBitmap(width, height, 96 * scale.M11, 96 * scale.M22, PixelFormats.Pbgra32);
                // This is only our content layer: no desktop, backdrop, shadow,
                // or resize chrome. A reversal captures the current crossfade.
                // It exists only in memory and is never written to disk.
                bitmap.Render(contentLayer); bitmap.Freeze();
                pendingTransitionSnapshot = new ContentSnapshot { Source = bitmap, Bounds = CurrentBounds() };
            }
            catch (InvalidOperationException) { }
            catch (ArgumentException) { }
            finally { LastTransitionSnapshotMilliseconds = watch.Elapsed.TotalMilliseconds; }
        }

        private void ShowOutgoingContent(ContentSnapshot snapshot, Rect fromSurface, Rect targetSurface, double fromRadius)
        {
            if (snapshot == null) return;
            double x = snapshot.Bounds.Left - transitionHostBounds.Left;
            double y = snapshot.Bounds.Top - transitionHostBounds.Top;
            outgoingImage.Source = snapshot.Source;
            outgoingImage.Width = snapshot.Bounds.Width; outgoingImage.Height = snapshot.Bounds.Height;
            outgoingImage.RenderTransform = new TranslateTransform(x, y);
            outgoingFromClip = new Rect(fromSurface.X - x, fromSurface.Y - y, fromSurface.Width, fromSurface.Height);
            outgoingToClip = new Rect(targetSurface.X - x, targetSurface.Y - y, targetSurface.Width, targetSurface.Height);
            outgoingClip = new RectangleGeometry(outgoingFromClip, fromRadius, fromRadius);
            outgoingImage.Clip = outgoingClip;
            outgoingImage.Opacity = 1; outgoingImage.Visibility = Visibility.Visible;
        }

        private void UpdateOutgoingContent(double progress, double radius)
        {
            if (outgoingClip == null || outgoingImage.Source == null) return;
            outgoingClip.Rect = InterpolateTransitionRect(outgoingFromClip, outgoingToClip, progress);
            outgoingClip.RadiusX = outgoingClip.RadiusY = radius;
            outgoingImage.Opacity = 1 - progress;
        }

        private void ClearOutgoingContent()
        {
            if (outgoingImage == null) return;
            outgoingImage.BeginAnimation(UIElement.OpacityProperty, null);
            if (outgoingClip != null)
            {
                outgoingClip.BeginAnimation(RectangleGeometry.RectProperty, null);
                outgoingClip.BeginAnimation(RectangleGeometry.RadiusXProperty, null);
                outgoingClip.BeginAnimation(RectangleGeometry.RadiusYProperty, null);
                outgoingClip = null;
            }
            outgoingImage.Source = null; outgoingImage.Clip = null;
            outgoingImage.Visibility = Visibility.Collapsed;
            outgoingImage.RenderTransform = Transform.Identity; outgoingImage.Opacity = 1;
        }

        private void DiscardTransitionSnapshot()
        {
            StopPageTransition();
            double outlineProgress = transitionLastProgress;
            ResetIncomingOpacityOverride();
            // Content invalidation does not restart the moving outline. Its
            // recorded progress must still describe the currently painted clip.
            if (visualTransitionActive) transitionLastProgress = outlineProgress;
            pendingTransitionSnapshot = null;
            ClearOutgoingContent();
            // Privacy, blocking and clearing must never leave old message pixels
            // on screen while the live notification controls are being updated.
            if (shell != null) { shell.BeginAnimation(UIElement.OpacityProperty, null); shell.Opacity = 1; }
        }
    }
}
