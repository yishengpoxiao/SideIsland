using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodexIsland
{
    internal partial class IslandWindow
    {
        private string compactLayoutKey = "";
        private bool HorizontalDock { get { return settings.Side == "top" || settings.Side == "bottom"; } }
        private Size CompactSize { get { return HorizontalDock ? new Size(238, 88) : new Size(88, 238); } }

        private Rect SavedCompactBounds()
        {
            Rect area = PlacementArea(); Size size = CompactSize;
            double width = Math.Min(size.Width, area.Width), height = Math.Min(size.Height, area.Height);
            double x = area.Left + Math.Max(0, area.Width - width) * settings.HorizontalPosition;
            double y = area.Top + Math.Max(0, area.Height - height) * settings.VerticalPosition;
            if (!settings.FreePosition)
            {
                if (settings.Side == "left") x = area.Left;
                else if (settings.Side == "right") x = area.Right - width;
                else if (settings.Side == "top") y = area.Top;
                else if (settings.Side == "bottom") y = area.Bottom - height;
            }
            return WindowGeometry.ClampMove(new Rect(x, y, width, height), area);
        }

        private void SaveCompactAnchor(Rect bounds)
        {
            Rect area = PlacementArea();
            settings.HorizontalPosition = Math.Max(0, Math.Min(1, (bounds.Left - area.Left) / Math.Max(1, area.Width - bounds.Width)));
            settings.VerticalPosition = Math.Max(0, Math.Min(1, (bounds.Top - area.Top) / Math.Max(1, area.Height - bounds.Height)));
            RememberExperienceMonitorAnchor();
        }

        private bool TouchesDockEdge(Rect bounds, Rect area)
        {
            if (settings.Side == "top") return Math.Abs(bounds.Top - area.Top) < 1;
            if (settings.Side == "bottom") return Math.Abs(bounds.Bottom - area.Bottom) < 1;
            if (settings.Side == "left") return Math.Abs(bounds.Left - area.Left) < 1;
            return Math.Abs(bounds.Right - area.Right) < 1;
        }

        private void UpdateCompactLayout()
        {
            if (compact == null || miniStatus == null || compactAlternate == null) return;
            string key = settings.Side + ":" + settings.CompactTextDirection;
            if (key == compactLayoutKey) return;
            compactLayoutKey = key;
            bool horizontal = HorizontalDock;
            compact.Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical;
            compact.HorizontalAlignment = HorizontalAlignment.Center;
            compact.VerticalAlignment = VerticalAlignment.Center;
            compact.Margin = horizontal ? new Thickness(8, 0, 8, 0) : new Thickness(0, 12, 0, 12);
            compactNotificationIcon.Width = horizontal ? 28 : Double.NaN;
            compactNotificationIcon.Height = 24; compactNotificationIcon.LineHeight = 24;
            compactNotificationIcon.TextAlignment = TextAlignment.Center;
            compactNotificationIcon.VerticalAlignment = VerticalAlignment.Center;
            compactNotificationIcon.Margin = horizontal ? new Thickness(0, 0, 6, 0) : new Thickness(0, 0, 0, 6);
            primaryRing.Margin = secondaryRing.Margin = horizontal ? new Thickness(2, 0, 2, 0) : new Thickness(0);
            primaryRing.VerticalAlignment = secondaryRing.VerticalAlignment = VerticalAlignment.Center;
            miniStatus.Width = horizontal ? 44 : Double.NaN; miniStatus.MaxWidth = horizontal ? 44 : 60;
            miniStatus.VerticalAlignment = VerticalAlignment.Center; miniStatus.TextWrapping = TextWrapping.Wrap; miniStatus.TextAlignment = TextAlignment.Center;
            miniStatus.LineHeight = 12; miniStatus.MaxHeight = 24; miniStatus.TextTrimming = TextTrimming.CharacterEllipsis;
            miniStatus.Margin = horizontal ? new Thickness(6, 0, 0, 0) : new Thickness(0, 6, 0, 0);
            compactAlternate.Width = horizontal ? 118 : 59; compactAlternate.Height = horizontal ? 62 : 133;
            compactAlternateMain.Margin = horizontal ? new Thickness(0, 2, 0, 4) : new Thickness(0, 10, 0, 6);
            foreach (TextBlock label in new[] { compactNotificationIcon, miniStatus, compactAlternateMain, compactAlternateCaption })
            {
                label.RenderTransformOrigin = new Point(.5, .5);
                label.RenderTransform = settings.CompactTextDirection == "rotate180" ? (Transform)new RotateTransform(180) : settings.CompactTextDirection == "mirror" ? new ScaleTransform(-1, 1) : Transform.Identity;
            }
            primaryRing.TextDirection = secondaryRing.TextDirection = settings.CompactTextDirection;
            primaryRing.InvalidateVisual(); secondaryRing.InvalidateVisual();
            LayoutCompactWidgetSlots();
        }

        private void SetCompactTextDirection(string direction)
        {
            settings.CompactTextDirection = direction; UpdateCompactLayout(); UpdateCompactWidgets(); SaveSettings();
        }
    }
}
