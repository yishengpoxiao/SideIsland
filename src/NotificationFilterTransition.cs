using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodexIsland
{
    internal partial class IslandWindow
    {
        internal const int NotificationFilterTransitionMilliseconds = 150;
        private bool notificationFilterTransitionActive;
        private Stopwatch notificationFilterTransitionWatch;
        private int notificationFilterTransitionVersion;
        private NotificationFilter notificationFilterTransitionFilter;
        private double notificationFilterTransitionFrom;
        private string notificationCountLayoutKey;
        private double notificationCountLayoutWidth = Double.NaN, notificationCountLayoutHeight;

        private bool CanAnimateNotificationFilter
        {
            get
            {
                return expanded && notificationPageSelected && !toolsPageSelected && !peeking &&
                    !visualTransitionActive && !pageTransitionActive && IsLoaded && IsVisible && Opacity > 0 &&
                    !shuttingDown && !dragging && !resizing && notificationList != null && notificationList.IsVisible;
            }
        }

        private void SelectNotificationFilter(NotificationFilter filter)
        {
            // A queued list filter must not resize or restart a visible preview.
            // If its first mode transition is still running, remove old pixels
            // while retaining that mode's currently painted incoming alpha.
            bool previewMode = peeking && visualTransitionActive;
            if (previewMode) RetainIncomingOpacityForPeekReplacement();
            if (filter == notificationFilter) return;
            StopNotificationCardTransition();
            bool animate = CanAnimateNotificationFilter;
            double from = notificationFilterTransitionActive && notificationList != null ? notificationList.Opacity : .35;
            // An interrupted mode transition may still own outgoing pixels.
            // Explicit filtering removes those immediately, while an ordinary
            // page fade keeps control of its incoming content and clock.
            if (previewMode) StopNotificationFilterTransition();
            else if (visualTransitionActive || (outgoingImage != null && outgoingImage.Source != null)) DiscardTransitionSnapshot();
            else StopNotificationFilterTransition();
            notificationFilter = filter;
            UpdateNotificationListForFilter();
            var scroll = FindNotificationListScroll(notificationList);
            if (scroll != null) scroll.ScrollToTop();
            BeginNotificationFilterTransition(animate, from);
        }

        private void BeginNotificationFilterTransition(bool animate, double from)
        {
            StopNotificationCounterTransitions();
            StopNotificationFilterTransition();
            if (!animate || !SystemParameters.ClientAreaAnimation || !CanAnimateNotificationFilter) return;
            notificationFilterTransitionFilter = notificationFilter;
            notificationFilterTransitionFrom = Math.Max(.35, Math.Min(1, from));
            notificationFilterTransitionActive = true; notificationFilterTransitionWatch = null;
            ++notificationFilterTransitionVersion;
            notificationList.Opacity = notificationCount.Opacity = notificationFilterTransitionFrom;
            // Only current live controls fade: no outgoing notification image,
            // deferred collection commit, or stale filter result is retained.
            CompositionTarget.Rendering += OnNotificationFilterRendering;
        }

        private void OnNotificationFilterRendering(object sender, EventArgs e)
        {
            if (!notificationFilterTransitionActive || !CanAnimateNotificationFilter ||
                notificationFilterTransitionFilter != notificationFilter || !SystemParameters.ClientAreaAnimation)
            { StopNotificationFilterTransition(); return; }
            if (notificationFilterTransitionWatch == null) notificationFilterTransitionWatch = Stopwatch.StartNew();
            double progress = Math.Min(1, notificationFilterTransitionWatch.Elapsed.TotalMilliseconds / NotificationFilterTransitionMilliseconds);
            double opacity = notificationFilterTransitionFrom + (1 - notificationFilterTransitionFrom) * (.5 - Math.Cos(Math.PI * progress) / 2);
            notificationList.Opacity = notificationCount.Opacity = opacity;
            if (progress >= 1) StopNotificationFilterTransition();
        }

        private void StopNotificationFilterTransition()
        {
            CompositionTarget.Rendering -= OnNotificationFilterRendering;
            bool active = notificationFilterTransitionActive || notificationFilterTransitionWatch != null;
            if (notificationList != null) notificationList.Opacity = 1;
            if (notificationCount != null) notificationCount.Opacity = 1;
            notificationFilterTransitionActive = false; notificationFilterTransitionWatch = null;
            if (active) ++notificationFilterTransitionVersion;
        }

        private static ScrollViewer FindNotificationListScroll(DependencyObject root)
        {
            if (root == null) return null;
            var viewer = root as ScrollViewer;
            if (viewer != null) return viewer;
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            {
                viewer = FindNotificationListScroll(VisualTreeHelper.GetChild(root, index));
                if (viewer != null) return viewer;
            }
            return null;
        }

        private void MeasureNotificationCountFloor(double width)
        {
            if (notificationCount == null) return;
            string key = UiLocalization.Language + "|" + notificationCount.FontFamily.Source + "|" +
                notificationCount.FontSize + "|" + notificationCount.FontWeight + "|" + notificationCount.FontStyle + "|" +
                notificationCount.FontStretch + "|" + notificationCount.LineHeight + "|" + notificationCount.UseLayoutRounding + "|" +
                TextOptions.GetTextFormattingMode(notificationCount) + "|" + VisualTreeHelper.GetDpi(notificationCount).PixelsPerDip;
            if (key != notificationCountLayoutKey || Math.Abs(width - notificationCountLayoutWidth) > .01)
            {
                var sample = new TextBlock { FontFamily = notificationCount.FontFamily, FontSize = notificationCount.FontSize,
                    FontWeight = notificationCount.FontWeight, FontStyle = notificationCount.FontStyle, FontStretch = notificationCount.FontStretch,
                    FlowDirection = notificationCount.FlowDirection, TextWrapping = notificationCount.TextWrapping,
                    LineHeight = notificationCount.LineHeight, LineStackingStrategy = notificationCount.LineStackingStrategy,
                    UseLayoutRounding = notificationCount.UseLayoutRounding, SnapsToDevicePixels = notificationCount.SnapsToDevicePixels };
                TextOptions.SetTextFormattingMode(sample, TextOptions.GetTextFormattingMode(notificationCount));
                VisualTreeHelper.SetRootDpi(sample, VisualTreeHelper.GetDpi(notificationCount));
                double height = 0;
                // Retained history is bounded to 50. Reserve the widest possible
                // two-digit caption so filtering never adds/removes a text line.
                // Wide windows still use the actual single-line height.
                for (int digit = 0; digit < 10; digit++)
                {
                    sample.Text = UiLocalization.Format("最近通知  ·  {0} 条  ·  岛内未读 {1}", digit * 11, digit * 11);
                    sample.Measure(new Size(Math.Max(1, width), Double.PositiveInfinity));
                    height = Math.Max(height, sample.DesiredSize.Height);
                }
                notificationCountLayoutKey = key; notificationCountLayoutWidth = width; notificationCountLayoutHeight = height;
            }
            if (Math.Abs(notificationCount.MinHeight - notificationCountLayoutHeight) > .1) notificationCount.MinHeight = notificationCountLayoutHeight;
        }
    }
}
