using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodexIsland
{
    internal partial class IslandWindow
    {
        private Grid compactSlot1, compactSlot2;
        private CompactWidgetVisual compactWidget1, compactWidget2;

        private void EnsureCompactWidgetSlots()
        {
            if (compactWidget1 != null || compact == null || primaryRing == null || secondaryRing == null) return;
            compactSlot1 = WrapCompactWidget(primaryRing, out compactWidget1);
            compactSlot2 = WrapCompactWidget(secondaryRing, out compactWidget2);
        }

        private Grid WrapCompactWidget(QuotaRing ring, out CompactWidgetVisual widget)
        {
            int index = compact.Children.IndexOf(ring);
            compact.Children.Remove(ring);
            var slot = new Grid { Width = 54, Height = 67, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent };
            // Keep the original rings and their quota data intact for state-card
            // consumers. Only this rendering layer changes with slot selection.
            ring.Opacity = 0; ring.IsHitTestVisible = false; ring.Margin = new Thickness(0);
            widget = new CompactWidgetVisual(); slot.Children.Add(ring); slot.Children.Add(widget);
            compact.Children.Insert(index, slot);
            return slot;
        }

        private void LayoutCompactWidgetSlots()
        {
            if (compactSlot1 == null) return;
            compactSlot1.Margin = compactSlot2.Margin = HorizontalDock ? new Thickness(2, 0, 2, 0) : new Thickness(0);
            primaryRing.Margin = secondaryRing.Margin = new Thickness(0);
        }

        internal void UpdateCompactWidgets()
        {
            EnsureCompactWidgetSlots();
            if (compactWidget1 == null) return;
            var configuredCards = ApplyTaskActivityOverride(ResolveWidgetCards(DateTime.UtcNow));
            bool customPool = configuredCards != null;
            if (widgetLayoutEditor != null) widgetLayoutEditor.RefreshLanguage();
            LayoutCompactWidgetSlots();
            bool music = ShowCompactMusic && !customPool;
            compactMusicTarget.Visibility = music ? Visibility.Visible : Visibility.Collapsed;
            bool alternate = !customPool && !music && (settings.CompactMode == "clock" || settings.CompactMode == "focus");
            compactAlternate.Visibility = alternate ? Visibility.Visible : Visibility.Collapsed;
            SetWidgetCardSize(compactSlot1, compactWidget1, customPool ? configuredCards[0] : null);
            SetWidgetCardSize(compactSlot2, compactWidget2, customPool ? configuredCards[1] : null);
            compactSlot1.Visibility = compactSlot2.Visibility = alternate || music ? Visibility.Collapsed : Visibility.Visible;
            if (music)
            {
                UpdateMusicVisual();
                if (miniStatus != null && notificationFeed.UnreadCount == 0 && (focusPanel == null || !focusPanel.IsRunning) && (keepAwakePanel == null || !keepAwakePanel.IsRunning))
                    miniStatus.Text = UiLocalization.Localize("音乐");
                return;
            }
            if (alternate) { ConfigureCompactActionTarget(compactAlternate, settings.CompactMode, settings.CompactMode == "focus" ? focusPanel.CompactStatus : UiLocalization.Localize("时钟与日期")); return; }
            var bucket = CurrentBucket;
            var first = bucket == null ? null : bucket.Primary;
            var second = bucket == null ? null : bucket.Secondary;
            bool stale = failure != null || (snapshot != null && (DateTime.UtcNow - snapshot.FetchedAt.ToUniversalTime()).TotalSeconds > settings.RefreshSeconds * 2 + 30);
            var data = new CompactWidgetData {
                Language = settings.Language, LocalTime = DateTime.Now,
                PrimaryRemaining = first == null ? null : first.Remaining, SecondaryRemaining = second == null ? null : second.Remaining,
                PrimaryMinutes = first == null ? null : first.WindowMinutes, SecondaryMinutes = second == null ? null : second.WindowMinutes,
                QuotaStale = stale, AlertThreshold = settings.AlertThreshold,
                CpuPercent = stats.CpuPercent, MemoryPercent = stats.MemoryPercent, BatteryPercent = stats.BatteryPercent, OnAc = stats.OnAc,
                FocusStatus = focusPanel == null ? null : focusPanel.CompactStatus, FocusRunning = focusPanel != null && focusPanel.IsRunning,
                FocusLabel = focusPanel == null ? null : focusPanel.ActiveTimerLabel,
                AwakeStatus = keepAwakePanel == null ? null : keepAwakePanel.CompactStatus, AwakeRunning = keepAwakePanel != null && keepAwakePanel.IsRunning,
                UnreadCount = notificationFeed.UnreadCount,
                Media = nowPlaying == null ? null : nowPlaying.Snapshot, Lyric = nowPlaying == null ? "" : nowPlaying.Lyric,
                PortsStatus = portMonitorPanel == null ? null : portMonitorPanel.CompactStatus
            };
            FillTaskActivityWidgetData(data);
            var firstState = CompactWidgetState.Create(customPool ? configuredCards[0].Key : CompactWidgetCatalog.Normalize(settings.CompactSlot1, "codex-primary"), data);
            var secondState = CompactWidgetState.Create(customPool ? configuredCards[1].Key : CompactWidgetCatalog.Normalize(settings.CompactSlot2, "codex-secondary"), data);
            if (customPool && DateTime.UtcNow < widgetFocusUntil)
            {
                if (firstState.Key == "focus") { firstState.Value = "✓"; firstState.Caption = UiLocalization.Localize("专注完成"); }
                if (secondState.Key == "focus") { secondState.Value = "✓"; secondState.Caption = UiLocalization.Localize("专注完成"); }
            }
            int firstRevision = compactWidget1.ContentRevision, secondRevision = compactWidget2.ContentRevision;
            compactWidget1.Update(firstState, settings.CompactTextDirection, settings.Theme == "light", CanAnimateCompactMediaContent);
            compactWidget2.Update(secondState, settings.CompactTextDirection, settings.Theme == "light", CanAnimateCompactMediaContent);
            RetainCompactMediaParentClock(firstRevision, compactWidget1.ContentRevision);
            RetainCompactMediaParentClock(secondRevision, compactWidget2.ContentRevision);
            ConfigureCompactActionTarget(compactSlot1, firstState.Key, firstState.Tooltip); ConfigureCompactActionTarget(compactSlot2, secondState.Key, secondState.Tooltip);
            if (miniStatus != null && notificationFeed.UnreadCount == 0 && (focusPanel == null || !focusPanel.IsRunning) && (keepAwakePanel == null || !keepAwakePanel.IsRunning))
            {
                bool onlyQuotas = firstState.Key.StartsWith("codex-", StringComparison.Ordinal) && secondState.Key.StartsWith("codex-", StringComparison.Ordinal);
                bool english = settings.Language == "en";
                miniStatus.Text = !onlyQuotas ? (english ? "Widgets" : "自定义") : demo ? (english ? "Demo" : "演示") : stale ? (english ? "Stale" : "未同步") : busy && snapshot == null ? (english ? "Connecting" : "连接中") : snapshot == null ? (english ? "Offline" : "未连接") : (english ? "Left %" : "剩余 %");
                miniStatus.Foreground = Display.Brush(onlyQuotas && stale ? "#E9B883" : "#85928E");
            }
        }
    }
}
