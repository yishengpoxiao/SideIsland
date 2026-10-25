using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace CodexIsland
{
    internal partial class IslandWindow
    {
        internal const int PageTransitionMilliseconds = 150;
        private FrameworkElement pageTransitionPage;
        private Stopwatch pageTransitionWatch;
        private bool pageTransitionActive;
        private int pageTransitionVersion;
        private double expandedSessionDefaultHeight = Double.NaN;

        private FrameworkElement CurrentMainPage
        {
            get { return toolsPageSelected ? toolsPage : notificationPageSelected ? notificationPage : statusPage; }
        }

        private double DefaultExpandedHeight()
        {
            return toolsPageSelected ? 680 : notificationPageSelected ? 638 :
                (snapshot != null && snapshot.Buckets.Count > 1 ? 660 : 618) + (failure != null ? 32 : 0);
        }

        private void SelectMainPage(bool tools, bool notifications)
        {
            bool changed = toolsPageSelected != tools || (!tools && notificationPageSelected != notifications);
            bool animate = changed && expanded && !peeking && !visualTransitionActive &&
                IsLoaded && IsVisible && !shuttingDown && !dragging && !resizing;
            if (changed) StopPageTransition();
            toolsPageSelected = tools; notificationPageSelected = !tools && notifications;
            if (notificationPage != null) notificationPage.Visibility = !tools && notifications ? Visibility.Visible : Visibility.Collapsed;
            if (statusPage != null) statusPage.Visibility = !tools && !notifications ? Visibility.Visible : Visibility.Collapsed;
            if (toolsPage != null) toolsPage.Visibility = tools ? Visibility.Visible : Visibility.Collapsed;
            if (changed && detailsScroll != null) detailsScroll.ScrollToTop();
            UpdatePageTabs();
            // Tabs replace content inside one expanded session. They do not
            // resize its HWND or touch a pending mouse-leave collapse request.
            if (expanded && visualTransitionActive) SetExpanded(true, true);
            else
            {
                if (expanded) UpdateNotificationViewport();
                SyncToolkitActivity();
            }
            if (changed) BeginPageTransition(CurrentMainPage, animate);
        }

        private void BeginPageTransition(FrameworkElement page, bool animate)
        {
            StopPageTransition();
            if (!animate || !SystemParameters.ClientAreaAnimation || page == null || !expanded || peeking ||
                visualTransitionActive || !IsVisible || !IsLoaded || shuttingDown || dragging || resizing) return;
            pageTransitionPage = page; pageTransitionActive = true; pageTransitionWatch = null;
            ++pageTransitionVersion;
            // Only live incoming content fades. No old message bitmap is kept,
            // and the header, tabs, window outline and hit regions stay fixed.
            page.Opacity = .35;
            CompositionTarget.Rendering += OnPageTransitionRendering;
        }

        private void OnPageTransitionRendering(object sender, EventArgs e)
        {
            if (!pageTransitionActive || pageTransitionPage == null || shuttingDown || !IsVisible || Opacity <= 0 ||
                !expanded || peeking || dragging || resizing || visualTransitionActive || !SystemParameters.ClientAreaAnimation ||
                !ReferenceEquals(pageTransitionPage, CurrentMainPage))
            { StopPageTransition(); return; }
            if (pageTransitionWatch == null) pageTransitionWatch = Stopwatch.StartNew();
            double elapsed = Math.Min(1, pageTransitionWatch.Elapsed.TotalMilliseconds / PageTransitionMilliseconds);
            pageTransitionPage.Opacity = .35 + .65 * (1 - Math.Cos(Math.PI * elapsed)) / 2;
            if (elapsed >= 1) CompletePageTransition();
        }

        private void StopPageTransition()
        {
            SettleCompactMediaContentTransition();
            StopNotificationPeekTransition();
            StopNotificationCardTransition();
            StopNotificationCounterTransitions();
            StopNotificationFilterTransition();
            SettleToolPickerMotion();
            StopToolTransition();
            CompletePageTransition();
        }

        private void CompletePageTransition()
        {
            // Finishing this fade does not cancel local motions that the user
            // started after the page became interactive (notably the chooser).
            // Global interruption paths still use StopPageTransition above.
            CompositionTarget.Rendering -= OnPageTransitionRendering;
            bool hadTransition = pageTransitionActive || pageTransitionPage != null || pageTransitionWatch != null;
            if (pageTransitionPage != null) pageTransitionPage.Opacity = 1;
            pageTransitionPage = null; pageTransitionWatch = null; pageTransitionActive = false;
            if (hadTransition) ++pageTransitionVersion;
        }
    }
}
