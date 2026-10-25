using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodexIsland
{
    internal partial class IslandWindow
    {
        internal const int NotificationCounterTransitionMilliseconds = 150;
        private bool notificationCounterTransitionActive;
        private Stopwatch notificationCounterTransitionWatch;
        private int notificationCounterTransitionVersion;
        private int notificationCounterLastVisible = -1, notificationCounterCountUnread = -1, notificationCounterLastUnread = -1;
        private bool notificationCounterBadgeChanged;
        private double notificationCounterBadgeDirection = 1;
        private readonly Dictionary<UIElement, NotificationCounterVisual> notificationCounterTargets = new Dictionary<UIElement, NotificationCounterVisual>();

        private sealed class NotificationCounterVisual
        {
            internal UIElement Target;
            internal Transform OriginalTransform;
            internal TransformGroup Composite;
            internal TranslateTransform Translation;
            internal Stopwatch Watch;
            internal double FromOpacity, FromY;
        }

        private bool CanAnimateNotificationCounters
        {
            get
            {
                return IsLoaded && IsVisible && Opacity > 0 && SystemParameters.ClientAreaAnimation &&
                    !shuttingDown && !dragging && !resizing && !peeking && !visualTransitionActive &&
                    !pageTransitionActive && !notificationFilterTransitionActive;
            }
        }

        private void UpdateNotificationCountText(string text, int visibleCount, int unreadCount, bool animate)
        {
            bool initialized = notificationCounterLastVisible >= 0;
            bool changed = visibleCount != notificationCounterLastVisible || unreadCount != notificationCounterCountUnread;
            double direction = visibleCount != notificationCounterLastVisible
                ? visibleCount >= notificationCounterLastVisible ? 1 : -1
                : unreadCount >= notificationCounterCountUnread ? 1 : -1;
            notificationCounterLastVisible = visibleCount; notificationCounterCountUnread = unreadCount;
            // Data stays current even before the first rendered animation frame.
            // The registered localization property translates this canonical UI
            // caption; comparison uses numeric values, never translated text.
            if (notificationCount != null) notificationCount.Text = text;
            // Generic explicit refreshes also serve privacy/disable/import paths.
            // Their count commits immediately; actual clear/read/arrival routes
            // explicitly opt into the new safe incoming-number transition.
            if (changed && initialized && animate) QueueNotificationCounter(notificationCount, direction * 2);
        }

        private void BeginNotificationCounterUpdate(int unread)
        {
            notificationCounterBadgeChanged = notificationCounterLastUnread >= 0 && unread != notificationCounterLastUnread;
            notificationCounterBadgeDirection = unread >= notificationCounterLastUnread ? 1 : -1;
            notificationCounterLastUnread = unread;
        }

        private void EndNotificationCounterUpdate(int unread)
        {
            if (!notificationCounterBadgeChanged) return;
            notificationCounterBadgeChanged = false;
            // Compact text can already be mirrored or rotated by the user's
            // setting. Opacity leaves that transform and its fixed hit map alone.
            QueueNotificationCounter(compactNotificationIcon, 0);
            // Navigation captions keep a fixed baseline, including when a page
            // switch interrupts a counter fade. Only the incoming number fades.
            QueueNotificationCounter(NotificationTabCaption(), 0);
            if (compactWidget1 != null && compactWidget1.State != null && compactWidget1.State.Key == "notifications")
                QueueNotificationCounter(compactWidget1, 0);
            if (compactWidget2 != null && compactWidget2.State != null && compactWidget2.State.Key == "notifications")
                QueueNotificationCounter(compactWidget2, 0);
        }

        private TextBlock NotificationTabCaption()
        {
            if (notificationsTab == null || !notificationsTab.IsVisible) return null;
            notificationsTab.ApplyTemplate();
            return FindNotificationCounterCaption(notificationsTab);
        }

        private static TextBlock FindNotificationCounterCaption(DependencyObject root)
        {
            var text = root as TextBlock;
            if (text != null) return text;
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            {
                text = FindNotificationCounterCaption(VisualTreeHelper.GetChild(root, index));
                if (text != null) return text;
            }
            return null;
        }

        private void QueueNotificationCounter(UIElement target, double offset)
        {
            if (target == null) return;
            if (!CanAnimateNotificationCounters || !target.IsVisible)
            {
                RemoveNotificationCounter(target);
                return;
            }
            NotificationCounterVisual value;
            if (!notificationCounterTargets.TryGetValue(target, out value))
            {
                value = new NotificationCounterVisual { Target = target, FromOpacity = .65, FromY = offset };
                if (offset != 0)
                {
                    value.OriginalTransform = target.RenderTransform;
                    value.Translation = new TranslateTransform(0, offset);
                    value.Composite = new TransformGroup();
                    if (value.OriginalTransform != null) value.Composite.Children.Add(value.OriginalTransform);
                    value.Composite.Children.Add(value.Translation);
                    target.RenderTransform = value.Composite;
                }
                notificationCounterTargets[target] = value;
            }
            else
            {
                // Latest incoming data continues from the currently painted
                // alpha/offset. Other targets keep their independent clocks.
                value.FromOpacity = target.Opacity;
                value.FromY = value.Translation == null ? 0 : value.Translation.Y;
            }
            value.Watch = null;
            target.Opacity = value.FromOpacity;
            ++notificationCounterTransitionVersion;
            if (notificationCounterTransitionActive) return;
            notificationCounterTransitionActive = true; notificationCounterTransitionWatch = null;
            CompositionTarget.Rendering += OnNotificationCounterRendering;
        }

        private void OnNotificationCounterRendering(object sender, EventArgs e)
        {
            if (!notificationCounterTransitionActive || !CanAnimateNotificationCounters)
            { StopNotificationCounterTransitions(); return; }
            if (notificationCounterTransitionWatch == null) notificationCounterTransitionWatch = Stopwatch.StartNew();
            foreach (var pair in notificationCounterTargets.ToArray())
            {
                var value = pair.Value;
                var widget = value.Target as CompactWidgetVisual;
                if (widget != null && (widget.State == null || widget.State.Key != "notifications"))
                { RemoveNotificationCounter(value.Target); continue; }
                if (!value.Target.IsVisible) { RemoveNotificationCounter(value.Target); continue; }
                if (value.Watch == null) value.Watch = Stopwatch.StartNew();
                double progress = Math.Min(1, value.Watch.Elapsed.TotalMilliseconds / NotificationCounterTransitionMilliseconds);
                double eased = .5 - Math.Cos(Math.PI * progress) / 2;
                value.Target.Opacity = value.FromOpacity + (1 - value.FromOpacity) * eased;
                if (value.Translation != null) value.Translation.Y = value.FromY * (1 - eased);
                if (progress >= 1) RemoveNotificationCounter(value.Target);
            }
            if (notificationCounterTargets.Count == 0) StopNotificationCounterTransitions();
        }

        private void RemoveNotificationCounter(UIElement target)
        {
            NotificationCounterVisual value;
            if (!notificationCounterTargets.TryGetValue(target, out value)) return;
            target.Opacity = 1;
            if (value.Composite != null && Object.ReferenceEquals(target.RenderTransform, value.Composite))
                target.RenderTransform = value.OriginalTransform ?? Transform.Identity;
            notificationCounterTargets.Remove(target);
        }

        private void StopNotificationCounterTransitions()
        {
            CompositionTarget.Rendering -= OnNotificationCounterRendering;
            bool active = notificationCounterTransitionActive || notificationCounterTransitionWatch != null;
            foreach (var target in notificationCounterTargets.Keys.ToArray()) RemoveNotificationCounter(target);
            notificationCounterTransitionActive = false; notificationCounterTransitionWatch = null;
            if (active) ++notificationCounterTransitionVersion;
        }
    }
}
