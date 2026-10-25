using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace CodexIsland
{
    internal partial class IslandWindow
    {
        // Default dwell; each window applies the user's saved hover preference.
        internal const int HoverDelayMilliseconds = 650, CollapseDelayMilliseconds = 180, TransitionMilliseconds = 220;
        private bool hoverSuppressed, interactionRecheckQueued;
        private int transitionVersion;
        private bool visualTransitionActive;
        private Rect transitionHostBounds, transitionTargetBounds;
        private RectangleGeometry transitionClip;
        private Stopwatch transitionWatch;
        private Rect transitionFromSurface, transitionToSurface, transitionFromClip, transitionToClip;
        private double transitionFromRadius, transitionToRadius;
        private bool transitionIncomingOpacityOverride;
        private double transitionLastProgress, transitionIncomingFromProgress, transitionIncomingFromOpacity;
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowEnabled(IntPtr window);

        private void ConfigureHoverExpansion()
        {
            int delay = Math.Max(Settings.MinimumHoverDelayMilliseconds, Math.Min(Settings.MaximumHoverDelayMilliseconds, settings.HoverDelayMilliseconds));
            if (!settings.HoverExpandEnabled || hoverTimer.Interval.TotalMilliseconds != delay) hoverTimer.Stop();
            hoverTimer.Interval = TimeSpan.FromMilliseconds(delay);
            if (compact != null) UiLocalization.ToolTip(compact, settings.HoverExpandEnabled
                ? "点击组件快捷操作 · 按住拖动\n悬停星标或空白处展开"
                : "点击组件快捷操作 · 按住拖动\n点击星标或空白处展开（悬停已关闭）");
        }

        private void OnIslandMouseEnter()
        {
            collapseTimer.Stop();
            if (!settings.HoverExpandEnabled) { hoverTimer.Stop(); return; }
            if (IsCompactActionPointerOver()) { hoverTimer.Stop(); return; }
            if (!expanded && !dragging && !resizing && !hoverSuppressed && !HasInteractionGuard())
            {
                // Returning while an automatic collapse is still visible is a
                // continuation of that interaction, not a fresh hover request.
                if (visualTransitionActive && !peeking && Mouse.LeftButton != MouseButtonState.Pressed)
                { hoverTimer.Stop(); SetExpanded(true, true); }
                else hoverTimer.Start();
            }
        }

        private void QueueInteractionRecheck()
        {
            if (shuttingDown || interactionRecheckQueued) return;
            interactionRecheckQueued = true;
            // WPF raises ContextMenuOpening even when no menu exists. Wait for
            // the service to finish, then use real popup/capture state instead
            // of latching a flag that may never receive a matching Closing.
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => {
                interactionRecheckQueued = false;
                if (shuttingDown || !IsVisible || dragging || resizing) return;
                if (IsMouseOver) OnIslandMouseEnter();
                else if (expanded) collapseTimer.Start();
            }));
        }

        private void OnIslandMouseLeave()
        {
            hoverTimer.Stop(); hoverSuppressed = false;
            if (!dragging && !resizing && !peeking) collapseTimer.Start();
        }

        private void OnHoverElapsed()
        {
            hoverTimer.Stop();
            if (settings.HoverExpandEnabled && IsMouseOver && !expanded && !IsCompactActionPointerOver() && !hoverSuppressed && !dragging && !resizing && !HasInteractionGuard() && Mouse.LeftButton != MouseButtonState.Pressed) SetExpanded(true, true);
        }

        private void OnCollapseElapsed()
        {
            collapseTimer.Stop();
            if (shuttingDown || !expanded || peeking || pinned || !settings.AutoCollapse || IsMouseOver || dragging || resizing) return;
            // Popups have a separate HWND; leaving the island to choose an item is
            // still an interaction with this window. Retry after the popup closes.
            if (HasInteractionGuard()) { collapseTimer.Start(); return; }
            SetExpanded(false, true);
        }

        private bool HasInteractionGuard()
        {
            var editor = Keyboard.FocusedElement as TextBoxBase;
            return HasPopupInteraction() || (IsActive && editor != null && editor.IsVisible && Window.GetWindow(editor) == this);
        }

        private bool HasPopupInteraction()
        {
            if (dialogOpen || OwnedWindows.Count > 0) return true;
            // Native file/folder dialogs disable their owner without appearing in
            // WPF OwnedWindows. Keep the island stable while those dialogs run.
            var source = PresentationSource.FromVisual(this) as HwndSource;
            if (source != null && !IsWindowEnabled(source.Handle)) return true;
            var captured = Mouse.Captured as DependencyObject;
            if (captured != null && Window.GetWindow(captured) == this) return true;
            var menu = captured as ContextMenu;
            if (menu != null && menu.PlacementTarget != null && Window.GetWindow(menu.PlacementTarget) == this) return true;
            return HasOpenPopup(this);
        }

        private static bool HasOpenPopup(DependencyObject parent)
        {
            var element = parent as FrameworkElement;
            if (element != null)
            {
                if (element.ContextMenu != null && element.ContextMenu.IsOpen) return true;
                if (!element.IsVisible) return false;
            }
            var combo = parent as ComboBox; if (combo != null && combo.IsDropDownOpen) return true;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                if (HasOpenPopup(VisualTreeHelper.GetChild(parent, i))) return true;
            return false;
        }

        private void CollapseNow()
        {
            pinned = false; pinButton.Content = "◇"; pinButton.ToolTip = "固定展开";
            hoverSuppressed = true;
            SetExpanded(false, true);
        }

        private void TransitionSize(double targetWidth, double targetHeight, bool animate)
        {
            ContentSnapshot outgoing = pendingTransitionSnapshot; pendingTransitionSnapshot = null;
            Rect from = CurrentVisualBounds();
            double fromRadius = islandBackdrop.CornerRadius;
            Rect target = WindowGeometry.PlaceFromCompact(SavedCompactBounds(), new Size(targetWidth, targetHeight), settings.Side, PlacementArea());
            // A refresh of the page already being revealed must not restart its
            // time source or replace the in-progress content crossfade.
            if (animate && SystemParameters.ClientAreaAnimation && visualTransitionActive && Math.Abs(target.X - transitionTargetBounds.X) < .5 &&
                Math.Abs(target.Y - transitionTargetBounds.Y) < .5 && Math.Abs(target.Width - transitionTargetBounds.Width) < .5 &&
                Math.Abs(target.Height - transitionTargetBounds.Height) < .5) return;
            ResetIncomingOpacityOverride();
            // Rendering a layered HWND while its Width/Height are animated
            // repeatedly resizes the native surface and reflows every page. Keep
            // one native surface until completion and reveal fixed-size content.
            StopVisualTransition(false);
            ++transitionVersion;
            bool changed = Math.Abs(from.X - target.X) > .5 || Math.Abs(from.Y - target.Y) > .5 ||
                Math.Abs(from.Width - target.Width) > .5 || Math.Abs(from.Height - target.Height) > .5;
            if (!animate || !SystemParameters.ClientAreaAnimation || !IsVisible || !changed)
            {
                // Nonanimated layout/startup and reduced-motion requests commit
                // at once. Prepare target-local painting before the native commit,
                // which may synchronously render even without an animation.
                transitionHostBounds = transitionTargetBounds = target;
                visualTransitionActive = true;
                islandBackdrop.VisualBounds = SurfaceBounds(target, target);
                islandBackdrop.CornerRadius = shell.CornerRadius.TopLeft;
                UpdateNotificationViewport();
                try { ApplyBounds(target); }
                finally { visualTransitionActive = false; }
                SyncWindowSurface(); UpdateResizeChrome();
                return;
            }

            transitionHostBounds = Rect.Union(from, target);
            transitionTargetBounds = target;
            visualTransitionActive = true;
            resizeLayer.Visibility = Visibility.Collapsed;
            Rect fromLocal = SurfaceBounds(from, transitionHostBounds);
            Rect targetLocal = SurfaceBounds(target, transitionHostBounds);
            // Set the first visible rectangle before growing the HWND. Setting
            // the final base bounds first would flash the full expanded panel.
            islandBackdrop.VisualBounds = fromLocal;
            islandBackdrop.CornerRadius = fromRadius;
            shell.Width = targetLocal.Width; shell.Height = targetLocal.Height;
            shell.Margin = new Thickness(0);
            shell.HorizontalAlignment = HorizontalAlignment.Left;
            shell.VerticalAlignment = VerticalAlignment.Top;
            shell.RenderTransform = new TranslateTransform(targetLocal.X, targetLocal.Y);
            var fromClip = new Rect(fromLocal.X - targetLocal.X, fromLocal.Y - targetLocal.Y, fromLocal.Width, fromLocal.Height);
            transitionClip = new RectangleGeometry(fromClip, fromRadius, fromRadius);
            shell.Clip = transitionClip;
            shell.Opacity = outgoing == null ? 1 : 0;
            // The list viewport is the final content viewport, not the temporary
            // native union. Its layout is stable throughout the reveal.
            UpdateNotificationViewport();

            double radius = shell.CornerRadius.TopLeft;
            ShowOutgoingContent(outgoing, fromLocal, targetLocal, fromRadius);
            transitionFromSurface = fromLocal; transitionToSurface = targetLocal;
            transitionFromClip = fromClip; transitionToClip = new Rect(0, 0, targetLocal.Width, targetLocal.Height);
            transitionFromRadius = fromRadius; transitionToRadius = radius;
            ApplyBounds(transitionHostBounds);
            // Native layered-window setup may synchronously render. Start only
            // after it finishes, and give the first stable frame time zero.
            // One time source drives the outline, both clips and both content
            // opacities, so a reentrant TimeManager tick cannot desynchronize them.
            transitionWatch = null;
            CompositionTarget.Rendering += OnTransitionRendering;
        }

        private void OnTransitionRendering(object sender, EventArgs e)
        {
            if (!visualTransitionActive || shuttingDown || transitionClip == null) { CompositionTarget.Rendering -= OnTransitionRendering; transitionWatch = null; return; }
            if (transitionWatch == null) transitionWatch = Stopwatch.StartNew();
            double elapsed = Math.Min(1, transitionWatch.Elapsed.TotalMilliseconds / TransitionMilliseconds);
            // Sine ease-in/out starts and finishes at rest. Unlike a 100 ms
            // cubic ease-out, the first presented frame cannot consume half
            // the entire expansion before the eye can follow its contour.
            double progress = (1 - Math.Cos(Math.PI * elapsed)) / 2;
            transitionLastProgress = progress;
            double radius = transitionFromRadius + (transitionToRadius - transitionFromRadius) * progress;
            islandBackdrop.VisualBounds = InterpolateTransitionRect(transitionFromSurface, transitionToSurface, progress);
            islandBackdrop.CornerRadius = radius;
            transitionClip.Rect = InterpolateTransitionRect(transitionFromClip, transitionToClip, progress);
            transitionClip.RadiusX = transitionClip.RadiusY = radius;
            bool hasOutgoing = outgoingImage != null && outgoingImage.Source != null;
            if (transitionIncomingOpacityOverride)
            {
                // Replacing a preview discards its previous pixels while the
                // same outline/clip clock keeps running. Continue only the live
                // incoming alpha from the last painted frame, without a child
                // fade or restarting the native mode transition.
                double remaining = Math.Max(.000001, 1 - transitionIncomingFromProgress);
                double continuation = Math.Max(0, Math.Min(1, (progress - transitionIncomingFromProgress) / remaining));
                shell.Opacity = transitionIncomingFromOpacity + (1 - transitionIncomingFromOpacity) * continuation;
            }
            else shell.Opacity = hasOutgoing ? progress : 1;
            UpdateOutgoingContent(progress, radius);
            if (elapsed >= 1) StopVisualTransition(true);
        }

        private static Rect InterpolateTransitionRect(Rect from, Rect to, double progress)
        {
            return new Rect(from.X + (to.X - from.X) * progress, from.Y + (to.Y - from.Y) * progress,
                from.Width + (to.Width - from.Width) * progress, from.Height + (to.Height - from.Height) * progress);
        }

        private void RetainIncomingOpacityForPeekReplacement()
        {
            double opacity = shell == null ? 1 : Math.Max(0, Math.Min(1, shell.Opacity));
            double progress = transitionLastProgress;
            pendingTransitionSnapshot = null;
            ClearOutgoingContent();
            if (!visualTransitionActive || transitionClip == null)
            { ResetIncomingOpacityOverride(); return; }
            // Read the currently drawn values rather than advancing Stopwatch:
            // rapid replacements must begin at exactly the visible alpha.
            transitionIncomingOpacityOverride = true;
            transitionIncomingFromOpacity = opacity;
            transitionIncomingFromProgress = progress;
            shell.Opacity = opacity;
        }

        private void ResetIncomingOpacityOverride()
        {
            transitionIncomingOpacityOverride = false;
            transitionIncomingFromOpacity = 1; transitionIncomingFromProgress = 0; transitionLastProgress = 0;
            if (shell != null) shell.Opacity = 1;
        }

        internal Rect CurrentVisualBounds()
        {
            if (!visualTransitionActive || islandBackdrop == null) return CurrentBounds();
            Rect surface = islandBackdrop.VisualBounds;
            return new Rect(transitionHostBounds.X + surface.X - 8, transitionHostBounds.Y + surface.Y - 8, surface.Width + 16, surface.Height + 16);
        }

        private static Rect SurfaceBounds(Rect bounds, Rect host)
        {
            return new Rect(bounds.X - host.X + 8, bounds.Y - host.Y + 8, Math.Max(0, bounds.Width - 16), Math.Max(0, bounds.Height - 16));
        }

        private void SyncWindowSurface()
        {
            if (islandBackdrop == null || visualTransitionActive) return;
            islandBackdrop.VisualBounds = new Rect(8, 8, Math.Max(0, Width - 16), Math.Max(0, Height - 16));
            islandBackdrop.CornerRadius = shell.CornerRadius.TopLeft;
        }

        private void StopVisualTransition(bool commitTarget)
        {
            ResetIncomingOpacityOverride();
            if (!visualTransitionActive) return;
            ++transitionVersion;
            CompositionTarget.Rendering -= OnTransitionRendering;
            transitionWatch = null;
            ClearOutgoingContent();
            islandBackdrop.BeginAnimation(IslandBackdrop.VisualBoundsProperty, null);
            islandBackdrop.BeginAnimation(IslandBackdrop.CornerRadiusProperty, null);
            if (transitionClip != null)
            {
                transitionClip.BeginAnimation(RectangleGeometry.RectProperty, null);
                transitionClip.BeginAnimation(RectangleGeometry.RadiusXProperty, null);
                transitionClip.BeginAnimation(RectangleGeometry.RadiusYProperty, null);
                transitionClip = null;
            }
            shell.BeginAnimation(UIElement.OpacityProperty, null);
            shell.Opacity = 1; shell.Clip = null; shell.RenderTransform = Transform.Identity;
            shell.Width = Double.NaN; shell.Height = Double.NaN; shell.Margin = new Thickness(8);
            shell.HorizontalAlignment = HorizontalAlignment.Stretch; shell.VerticalAlignment = VerticalAlignment.Stretch;
            if (commitTarget)
            {
                // Prepare the target-local visuals before the native move can
                // synchronously render. Keep layout/docking guards active until
                // WM_SIZE and WM_MOVE have observed the atomic target bounds.
                transitionHostBounds = transitionTargetBounds;
                islandBackdrop.VisualBounds = SurfaceBounds(transitionTargetBounds, transitionTargetBounds);
                islandBackdrop.CornerRadius = shell.CornerRadius.TopLeft;
                ApplyBounds(transitionTargetBounds);
            }
            visualTransitionActive = false;
            SyncWindowSurface(); UpdateResizeChrome();
            if (commitTarget) UpdateNotificationViewport();
        }
    }
}
