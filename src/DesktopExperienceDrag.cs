using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace CodexIsland
{
    internal sealed class ExperienceDragSnapshot
    {
        internal string ScreenName, Side;
        internal double X, Y;
        internal bool Free, AutoSnap, Expanded, Pinned;
        internal List<MonitorAnchor> Anchors;
    }
    internal partial class IslandWindow
    {
        private ExperienceDragSnapshot experienceDragSnapshot;
        internal Func<ModifierKeys> ExperienceDragModifiers = null;

        private void BeginExperienceDragSnapshot()
        {
            experienceDragSnapshot = new ExperienceDragSnapshot { ScreenName = settings.ScreenName, Side = settings.Side, X = settings.HorizontalPosition,
                Y = settings.VerticalPosition, Free = settings.FreePosition, AutoSnap = settings.AutoSnapToEdge, Expanded = expanded, Pinned = pinned,
                Anchors = desktopPreferences == null ? null : desktopPreferences.Monitors.Select(anchor => new MonitorAnchor { Device = anchor.Device, Side = anchor.Side, X = anchor.X, Y = anchor.Y, Free = anchor.Free }).ToList() };
        }
        private bool ExperienceDragShouldSnap()
        {
            ModifierKeys modifiers = ExperienceDragModifiers == null ? Keyboard.Modifiers : ExperienceDragModifiers();
            return (modifiers & ModifierKeys.Alt) == 0;
        }
        private bool TryCancelExperienceDrag()
        {
            if (!dragging || resizing || experienceDragSnapshot == null) return false;
            var original = experienceDragSnapshot; experienceDragSnapshot = null;
            // Clear gesture state before releasing capture: the resulting lost-
            // capture event must neither commit the move nor dispatch a click.
            dragging = false; moved = false; pendingCompactAction = null;
            hoverTimer.Stop(); collapseTimer.Stop();
            if (Mouse.Captured == shell) shell.ReleaseMouseCapture();
            StopVisualTransition(false);
            settings.ScreenName = original.ScreenName; settings.Side = original.Side; settings.HorizontalPosition = original.X; settings.VerticalPosition = original.Y;
            settings.FreePosition = original.Free; settings.AutoSnapToEdge = original.AutoSnap;
            if (desktopPreferences != null && original.Anchors != null) desktopPreferences.Monitors = original.Anchors;
            pinned = original.Pinned; pinButton.Content = pinned ? "◆" : "◇"; pinButton.ToolTip = pinned ? "取消固定展开" : "固定展开";
            SetExpandedCore(original.Expanded, false); hoverSuppressed = !original.Expanded;
            UpdateCompactLayout(); UpdateToolkitCompact(); SaveSettings(); RememberExperienceMonitorAnchor();
            return true;
        }
    }
}
