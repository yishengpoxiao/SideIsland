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
        internal const int NotificationCardTransitionMilliseconds = 180;
        private Grid notificationListHost;
        private Canvas notificationCardOverlay;
        private bool notificationCardTransitionActive, notificationCardUpdatesSuppressed, notificationCardSafeExplicitTransition;
        private Stopwatch notificationCardTransitionWatch;
        private int notificationCardTransitionVersion;
        private readonly List<NotificationCardMotion> notificationCardMotions = new List<NotificationCardMotion>();

        private sealed class NotificationCardPosition
        {
            internal Border Card;
            internal string Key;
            internal Rect Bounds;
            internal double Opacity;
            internal IslandNotification Message;
            internal UIElement RemovedContent;
            internal double RemovedEndsAt;
        }
        private sealed class NotificationCardLayout
        {
            internal Border[] Items;
            internal List<NotificationCardPosition> Visible;
            internal List<NotificationCardPosition> Removed;
            internal double Offset;
            internal bool AllowRemoved;
        }
        private sealed class NotificationCardMotion
        {
            internal Border Card;
            internal TranslateTransform Translation;
            internal Transform OriginalTransform;
            internal double FromX, FromY, ToY, FromOpacity;
            internal bool Removed, OriginalHitTest;
            internal double RemovedEndsAt, RemovedStartsAt;
        }

        private bool CanAnimateNotificationCards
        {
            get
            {
                return !notificationCardUpdatesSuppressed && expanded && notificationPageSelected && !toolsPageSelected && !peeking &&
                    !visualTransitionActive && !pageTransitionActive && !notificationFilterTransitionActive && IsLoaded && IsVisible &&
                    Opacity > 0 && !shuttingDown && !dragging && !resizing && notificationList != null && notificationList.IsVisible &&
                    notificationListHost != null && notificationCardOverlay != null && SystemParameters.ClientAreaAnimation;
            }
        }

        private void MarkAllNotificationsRead()
        {
            notificationFeed.MarkAllRead(); UpdateNotificationListCore(false);
        }
        private void ClearNotificationsWithMotion()
        {
            DismissNotificationPeek(true);
            notificationFeed.Clear(); notificationCardSafeExplicitTransition = true;
            try { UpdateNotificationList(); }
            finally { notificationCardSafeExplicitTransition = false; }
        }
        private void UpdateNotificationListForFilter()
        {
            bool previous = notificationCardUpdatesSuppressed; notificationCardUpdatesSuppressed = true;
            try { UpdateNotificationListCore(false); }
            finally { notificationCardUpdatesSuppressed = previous; }
        }

        private NotificationCardLayout CaptureNotificationCardLayout(bool allowRemoved, bool allowMotion)
        {
            if (!allowMotion || !CanAnimateNotificationCards) return null;
            var scroll = FindNotificationListScroll(notificationList);
            return new NotificationCardLayout { Items = notificationList.Items.OfType<Border>().ToArray(),
                Visible = VisibleNotificationCards(), Removed = allowRemoved ? CaptureRemovedNotificationCards() : new List<NotificationCardPosition>(),
                Offset = scroll == null ? 0 : scroll.VerticalOffset, AllowRemoved = allowRemoved };
        }

        private static double NotificationMotionTime { get { return Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency; } }
        private List<NotificationCardPosition> CaptureRemovedNotificationCards()
        {
            var output = new List<NotificationCardPosition>();
            double now = NotificationMotionTime;
            foreach (var motion in notificationCardMotions.Where(item => item.Removed && item.Card.Child != null && item.RemovedEndsAt > now))
            {
                Rect bounds = motion.Card.TransformToAncestor(notificationListHost).TransformBounds(new Rect(motion.Card.RenderSize));
                output.Add(new NotificationCardPosition { Card = motion.Card, Key = Convert.ToString(motion.Card.Tag), Bounds = bounds,
                    Opacity = motion.Card.Opacity, RemovedContent = motion.Card.Child, RemovedEndsAt = motion.RemovedEndsAt });
            }
            return output;
        }

        private List<NotificationCardPosition> VisibleNotificationCards()
        {
            var output = new List<NotificationCardPosition>();
            var viewport = new Rect(0, 0, notificationListHost.ActualWidth, notificationListHost.ActualHeight);
            if (viewport.Width <= 0 || viewport.Height <= 0) return output;
            foreach (Border card in notificationList.Items.OfType<Border>())
            {
                // Iterate identities (at most fifty), but inspect and move only
                // containers the virtualizing panel already generated.
                var container = notificationList.ItemContainerGenerator.ContainerFromItem(card) as ListBoxItem;
                if (container == null || !container.IsVisible || card.ActualWidth <= 0 || card.ActualHeight <= 0) continue;
                try
                {
                    Rect bounds = card.TransformToAncestor(notificationListHost).TransformBounds(new Rect(card.RenderSize));
                    NotificationCardEntry entry;
                    string key = Convert.ToString(card.Tag);
                    notificationCards.TryGetValue(key, out entry);
                    if (bounds.IntersectsWith(viewport)) output.Add(new NotificationCardPosition { Card = card,
                        Key = key, Bounds = bounds, Opacity = card.Opacity, Message = entry == null ? null : entry.Message });
                }
                catch (InvalidOperationException) { }
                if (output.Count >= 12) break;
            }
            return output;
        }

        private void CommitNotificationCardLayout(NotificationCardLayout before, IList<Border> desired)
        {
            if (before == null || !CanAnimateNotificationCards) return;
            if (before.Items.Length == desired.Count && before.Items.Where((card, index) => !Object.ReferenceEquals(card, desired[index])).Any() == false)
                return; // A repeated background snapshot never restarts a motion.

            StopNotificationCardTransition();
            notificationList.UpdateLayout();
            var after = VisibleNotificationCards();
            var currentKeys = new HashSet<string>(desired.Select(card => Convert.ToString(card.Tag)), StringComparer.Ordinal);
            var previous = before.Visible.GroupBy(item => item.Key).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var scroll = FindNotificationListScroll(notificationList);
            // While reading older history, preserve the first surviving visible
            // identity rather than pushing the user back to the newest arrival.
            if (before.Offset > 1 && scroll != null)
            {
                var anchor = before.Visible.FirstOrDefault(item => currentKeys.Contains(item.Key));
                var target = anchor == null ? null : after.FirstOrDefault(item => item.Key == anchor.Key);
                if (anchor != null && target == null)
                {
                    // An insertion can move the old anchor just outside the
                    // generated range. Realize that identity's viewport only,
                    // then restore its exact previous pixel position below.
                    var anchorItem = desired.FirstOrDefault(card => Convert.ToString(card.Tag) == anchor.Key);
                    if (anchorItem != null)
                    {
                        notificationList.ScrollIntoView(anchorItem); notificationList.UpdateLayout();
                        after = VisibleNotificationCards(); target = after.FirstOrDefault(item => item.Key == anchor.Key);
                    }
                }
                if (target != null)
                {
                    double correction = target.Bounds.Top - anchor.Bounds.Top;
                    if (Math.Abs(correction) > .1)
                    {
                        scroll.ScrollToVerticalOffset(Math.Max(0, Math.Min(scroll.ScrollableHeight, scroll.VerticalOffset + correction)));
                        notificationList.UpdateLayout(); after = VisibleNotificationCards();
                    }
                }
            }
            foreach (var position in after)
            {
                NotificationCardPosition old;
                bool known = previous.TryGetValue(position.Key, out old);
                bool replacement = known && !Object.ReferenceEquals(old.Card, position.Card);
                double x = known ? old.Bounds.Left - position.Bounds.Left : 0;
                double y = known ? old.Bounds.Top - position.Bounds.Top : 8;
                double opacity = !known || replacement ? .4 : old.Opacity;
                if (known && !replacement && Math.Abs(x) < .1 && Math.Abs(y) < .1 && opacity > .999) continue;
                AddNotificationCardMotion(position.Card, x, y, 0, opacity, false);
            }
            if (before.AllowRemoved)
            {
                foreach (var removed in before.Visible.Where(item => !currentKeys.Contains(item.Key)).Concat(before.Removed).Take(12))
                {
                    Border card;
                    if (removed.RemovedContent != null) { card = removed.Card; card.Child = removed.RemovedContent; }
                    else
                    {
                        if (removed.Message == null) continue;
                        // A recycled ContentPresenter can still own its old
                        // visual until template regeneration. Leave it intact;
                        // only a few visible removals get a live overlay copy.
                        card = NotificationCard(removed.Message); card.Tag = removed.Key;
                    }
                    card.Width = removed.Bounds.Width; card.Height = removed.Bounds.Height; card.Margin = new Thickness(0);
                    Canvas.SetLeft(card, removed.Bounds.Left); Canvas.SetTop(card, removed.Bounds.Top);
                    notificationCardOverlay.Children.Add(card);
                    AddNotificationCardMotion(card, 0, 0, -8, removed.Opacity, true);
                    notificationCardMotions[notificationCardMotions.Count - 1].RemovedEndsAt = removed.RemovedEndsAt > 0 ? removed.RemovedEndsAt : NotificationMotionTime + NotificationCardTransitionMilliseconds;
                }
            }
            if (notificationCardMotions.Count == 0) return;
            notificationCardTransitionActive = true; notificationCardTransitionWatch = null;
            ++notificationCardTransitionVersion;
            CompositionTarget.Rendering += OnNotificationCardRendering;
        }

        private void AddNotificationCardMotion(Border card, double x, double y, double toY, double opacity, bool removed)
        {
            var translation = new TranslateTransform(x, y);
            notificationCardMotions.Add(new NotificationCardMotion { Card = card, Translation = translation, OriginalTransform = card.RenderTransform,
                FromX = x, FromY = y, ToY = toY, FromOpacity = Math.Max(0, Math.Min(1, opacity)), Removed = removed,
                RemovedStartsAt = NotificationMotionTime, OriginalHitTest = card.IsHitTestVisible });
            card.RenderTransform = translation; card.Opacity = Math.Max(0, Math.Min(1, opacity)); card.IsHitTestVisible = false;
        }
        private void OnNotificationCardRendering(object sender, EventArgs e)
        {
            if (!notificationCardTransitionActive || !CanAnimateNotificationCards)
            { StopNotificationCardTransition(); return; }
            if (notificationCardTransitionWatch == null) notificationCardTransitionWatch = Stopwatch.StartNew();
            double progress = Math.Min(1, notificationCardTransitionWatch.Elapsed.TotalMilliseconds / NotificationCardTransitionMilliseconds);
            double eased = .5 - Math.Cos(Math.PI * progress) / 2;
            foreach (var motion in notificationCardMotions)
            {
                double local = eased;
                if (motion.Removed)
                {
                    double duration = Math.Max(1, motion.RemovedEndsAt - motion.RemovedStartsAt);
                    double elapsed = Math.Max(0, NotificationMotionTime - motion.RemovedStartsAt);
                    local = .5 - Math.Cos(Math.PI * Math.Min(1, elapsed / duration)) / 2;
                }
                motion.Translation.X = motion.FromX * (1 - local);
                motion.Translation.Y = motion.FromY + (motion.ToY - motion.FromY) * local;
                motion.Card.Opacity = motion.FromOpacity + ((motion.Removed ? 0 : 1) - motion.FromOpacity) * local;
            }
            if (progress >= 1) StopNotificationCardTransition();
        }
        private void StopNotificationCardTransition()
        {
            CompositionTarget.Rendering -= OnNotificationCardRendering;
            bool active = notificationCardTransitionActive || notificationCardTransitionWatch != null;
            foreach (var motion in notificationCardMotions)
            {
                motion.Card.RenderTransform = motion.OriginalTransform; motion.Card.Opacity = 1; motion.Card.IsHitTestVisible = motion.OriginalHitTest;
                // Removed controls exist only in this bounded, noninteractive
                // live overlay. Clear their content as soon as they leave it.
                if (motion.Removed && !notificationList.Items.Contains(motion.Card)) motion.Card.Child = null;
            }
            notificationCardMotions.Clear();
            if (notificationCardOverlay != null) notificationCardOverlay.Children.Clear();
            notificationCardTransitionActive = false; notificationCardTransitionWatch = null;
            if (active) ++notificationCardTransitionVersion;
        }
    }
}
