using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace CodexIsland
{
    internal partial class IslandWindow
    {
        private bool resizing, applyingBounds;
        private Point motionStart;
        private Rect motionBounds, resizeArea;
        private Grid resizeLayer;
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

        private Point PointerPosition()
        {
            var position = Forms.Cursor.Position;
            return new Point(position.X, position.Y);
        }

        private Vector PointerDelta(Point from, Point to)
        {
            var source = PresentationSource.FromVisual(this);
            Matrix scale = source == null ? Matrix.Identity : source.CompositionTarget.TransformFromDevice;
            return scale.Transform(to - from);
        }

        private Rect PlacementArea()
        {
            Rect work = WorkArea();
            return new Rect(work.Left + 2, work.Top + 6, Math.Max(1, work.Width - 4), Math.Max(1, work.Height - 12));
        }

        private Rect CurrentBounds() { return new Rect(Left, Top, Width, Height); }

        private void FreezeSizeAnimation()
        {
            StopPageTransition();
            StopVisualTransition(true);
            transitionVersion++;
            double width = Width, height = Height;
            applyingBounds = true;
            try { BeginAnimation(WidthProperty, null); BeginAnimation(HeightProperty, null); Width = width; Height = height; }
            finally { applyingBounds = false; }
        }

        private void ApplyBounds(Rect bounds)
        {
            applyingBounds = true;
            try
            {
                var source = PresentationSource.FromVisual(this) as HwndSource;
                if (source != null && source.CompositionTarget != null)
                {
                    Matrix scale = source.CompositionTarget.TransformToDevice;
                    Point position = scale.Transform(bounds.TopLeft);
                    Vector size = scale.Transform(new Vector(bounds.Width, bounds.Height));
                    // Four WPF setters issue separate native moves/resizes. A
                    // layered window can render between them, briefly exposing
                    // a narrow full-height panel or the old screen position.
                    // WM_SIZE/WM_MOVE feed this one native commit back to WPF.
                    if (SetWindowPos(source.Handle, IntPtr.Zero, (int)Math.Round(position.X), (int)Math.Round(position.Y),
                        Math.Max(1, (int)Math.Round(size.X)), Math.Max(1, (int)Math.Round(size.Y)), 0x0004 | 0x0010)) return;
                }
                Width = bounds.Width; Height = bounds.Height; Left = bounds.Left; Top = bounds.Top;
            }
            finally { applyingBounds = false; }
        }

        private void WireDrag(UIElement target, bool compactOnly = false)
        {
            target.MouseLeftButtonDown += (s, e) => {
                // A returning pointer can reverse a collapse just before its
                // press. The transitioning island must still accept that drag.
                if (resizing || dragging || (compactOnly && (peeking || (expanded && !visualTransitionActive)))) return;
                StopPageTransition();
                // A press may become a click that reverses the current motion.
                // Settle geometry only after movement crosses the drag threshold.
                dragging = true; moved = false; hoverTimer.Stop(); collapseTimer.Stop();
                pendingCompactAction = FindCompactActionAt(e.OriginalSource as DependencyObject, e.GetPosition(this));
                motionStart = PointerPosition(); motionBounds = CurrentVisualBounds();
                BeginExperienceDragSnapshot();
                // The title disappears when moving collapses the island. Keep
                // capture on the persistent shell from the initial press.
                if (!shell.CaptureMouse()) { dragging = false; pendingCompactAction = null; experienceDragSnapshot = null; return; }
                e.Handled = true;
            };
            // Every drag source shares the shell's move/up/lost-capture route;
            // a title hidden by collapse must not own gesture completion.
            if (!ReferenceEquals(target, shell)) return;
            target.MouseMove += (s, e) => {
                if (!dragging || Mouse.Captured != target) return;
                if (e.LeftButton != MouseButtonState.Pressed) { FinishDrag(target, false); return; }
                if (MoveDrag(PointerPosition())) e.Handled = true;
            };
            target.MouseLeftButtonUp += (s, e) => { if (dragging && Mouse.Captured == target) { FinishDrag(target, true); e.Handled = true; } };
            target.LostMouseCapture += (s, e) => { if (dragging && ReferenceEquals(e.OriginalSource, target)) FinishDrag(target, false); };
        }

        private bool MoveDrag(Point pointer)
        {
            if (!dragging || Mouse.Captured != shell || applyingBounds) return false;
            Vector delta = PointerDelta(motionStart, pointer);
            if (!moved && Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return false;
            ChangeExperienceDragMonitor(Forms.Screen.FromPoint(new System.Drawing.Point((int)pointer.X, (int)pointer.Y)).DeviceName);
            if (!moved)
            {
                Vector press = PointerDelta(new Point(), motionStart), current = PointerDelta(new Point(), pointer);
                double grabX = Math.Max(0, Math.Min(1, (press.X - motionBounds.Left) / Math.Max(1, motionBounds.Width)));
                double grabY = Math.Max(0, Math.Min(1, (press.Y - motionBounds.Top) / Math.Max(1, motionBounds.Height)));
                Rect compactBounds = WindowGeometry.ClampMove(new Rect(current.X - CompactSize.Width * grabX,
                    current.Y - CompactSize.Height * grabY, CompactSize.Width, CompactSize.Height), PlacementArea());
                moved = true; pendingCompactAction = null; pinned = false; pinButton.Content = "◇"; pinButton.ToolTip = "固定展开";
                // Commit the compact footprint at the grab point in one move.
                // Do not first finish an expanding union at its large endpoint.
                settings.FreePosition = true; SaveCompactAnchor(compactBounds);
                SetExpandedCore(false, false);
                hoverSuppressed = true;
                motionStart = pointer; motionBounds = CurrentBounds();
                return true;
            }
            ApplyBounds(WindowGeometry.ClampMove(new Rect(motionBounds.Left + delta.X, motionBounds.Top + delta.Y, motionBounds.Width, motionBounds.Height), PlacementArea()));
            return true;
        }

        private void FinishDrag(UIElement target, bool clickAllowed)
        {
            hoverTimer.Stop();
            string action = pendingCompactAction; pendingCompactAction = null;
            dragging = false;
            if (Mouse.Captured == target) target.ReleaseMouseCapture();
            if (moved) { RememberPlacement(ExperienceDragShouldSnap()); hoverSuppressed = true; }
            else if (clickAllowed) RunExperienceCompactClick(action);
            experienceDragSnapshot = null;
            // Do not restart hover expansion on release; a completed drag leaves the island collapsed.
        }

        private void RememberPlacement(bool snap)
        {
            Rect area = PlacementArea();
            Rect bounds = WindowGeometry.ClampMove(CurrentBounds(), area);
            Rect anchor;
            if (snap && settings.AutoSnapToEdge)
            {
                settings.Side = WindowGeometry.NearestEdge(bounds, area, settings.Side);
                settings.FreePosition = false;
                anchor = WindowGeometry.DockCompact(bounds, area, settings.Side);
            }
            else
            {
                if (snap || !TouchesDockEdge(bounds, area)) settings.FreePosition = true;
                anchor = expanded || peeking ? WindowGeometry.CompactFromExpanded(bounds, settings.Side, area) : bounds;
            }
            SaveCompactAnchor(anchor); UpdateCompactLayout(); UpdateToolkitCompact();
            if (!expanded && !peeking) ApplyBounds(anchor);
            else if (snap && settings.AutoSnapToEdge) ApplyBounds(WindowGeometry.PlaceFromCompact(anchor, new Size(bounds.Width, bounds.Height), settings.Side, area));
            else ApplyBounds(bounds);
            UpdateResizeChrome();
            SaveSettings();
        }

        private void BuildResizeHandles(Grid root)
        {
            resizeLayer = new Grid { Visibility = Visibility.Collapsed };
            root.Children.Add(resizeLayer);
            AddResizeHandle("L", Cursors.SizeWE, HorizontalAlignment.Left, VerticalAlignment.Stretch, 14, Double.NaN, new Thickness(6, 38, 0, 38));
            AddResizeHandle("R", Cursors.SizeWE, HorizontalAlignment.Right, VerticalAlignment.Stretch, 14, Double.NaN, new Thickness(0, 38, 6, 38));
            AddResizeHandle("T", Cursors.SizeNS, HorizontalAlignment.Stretch, VerticalAlignment.Top, Double.NaN, 14, new Thickness(38, 6, 38, 0));
            AddResizeHandle("B", Cursors.SizeNS, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, Double.NaN, 14, new Thickness(38, 0, 38, 6));
            AddResizeHandle("LT", Cursors.SizeNWSE, HorizontalAlignment.Left, VerticalAlignment.Top, 32, 32, new Thickness(6, 6, 0, 0));
            AddResizeHandle("RT", Cursors.SizeNESW, HorizontalAlignment.Right, VerticalAlignment.Top, 32, 32, new Thickness(0, 6, 6, 0));
            AddResizeHandle("LB", Cursors.SizeNESW, HorizontalAlignment.Left, VerticalAlignment.Bottom, 32, 32, new Thickness(6, 0, 0, 6));
            AddResizeHandle("RB", Cursors.SizeNWSE, HorizontalAlignment.Right, VerticalAlignment.Bottom, 32, 32, new Thickness(0, 0, 6, 6));
        }

        private void AddResizeHandle(string edges, Cursor cursor, HorizontalAlignment horizontal, VerticalAlignment vertical, double width, double height, Thickness margin)
        {
            var grip = new Thumb { Tag = edges, Cursor = cursor, Width = width, Height = height, HorizontalAlignment = horizontal, VerticalAlignment = vertical, Margin = margin, ToolTip = "拖动拉伸 · 朝屏幕内侧拖动可放大", Focusable = false, Background = Brushes.Transparent };
            var template = new ControlTemplate(typeof(Thumb));
            var border = new FrameworkElementFactory(typeof(Border)); border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty)); border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
            template.VisualTree = border; grip.Template = template; resizeLayer.Children.Add(grip);
            grip.DragStarted += (s, e) => { if (!expanded || dragging) return; SetExpanded(true, false); resizing = true; hoverTimer.Stop(); collapseTimer.Stop(); FreezeSizeAnimation(); motionStart = PointerPosition(); motionBounds = CurrentBounds(); resizeArea = PlacementArea(); };
            grip.DragDelta += (s, e) => { if (!resizing) return; Vector delta = PointerDelta(motionStart, PointerPosition()); ApplyBounds(WindowGeometry.Resize(motionBounds, new Point(delta.X, delta.Y), edges, resizeArea)); };
            grip.DragCompleted += (s, e) => {
                if (!resizing) return;
                resizing = false; settings.CustomExpandedSize = true;
                settings.ExpandedWidth = (int)Math.Round(Width); settings.ExpandedHeight = (int)Math.Round(Height);
                RememberPlacement(false); if (!IsMouseOver) collapseTimer.Start();
            };
        }

        private void UpdateResizeChrome()
        {
            if (resizeLayer == null) return;
            resizeLayer.Visibility = expanded && !peeking && !visualTransitionActive ? Visibility.Visible : Visibility.Collapsed;
        }

        private bool measuringNotificationViewport;
        private void UpdateNotificationViewport()
        {
            if (measuringNotificationViewport || notificationListScroll == null || !expanded || expandedHost == null || expandedTabs == null) return;
            measuringNotificationViewport = true;
            try
            {
                double width = visualTransitionActive ? transitionTargetBounds.Width : Width;
                double height = visualTransitionActive ? transitionTargetBounds.Height : Height;
                double hostWidth = Math.Max(1, width - 16 - shell.BorderThickness.Left - shell.BorderThickness.Right - expandedHost.Margin.Left - expandedHost.Margin.Right);
                double contentWidth = Math.Max(1, hostWidth - 10);
                MeasureNotificationCountFloor(contentWidth);
                MeasureNotificationStatic(expandedHeader, hostWidth);
                MeasureNotificationStatic(expandedTabs, hostWidth);
                double remaining = height - 16 - shell.BorderThickness.Top - shell.BorderThickness.Bottom - expandedHost.Margin.Top - expandedHost.Margin.Bottom - expandedHeader.DesiredSize.Height - expandedTabs.DesiredSize.Height;
                // Measure only the small static controls around the virtualized
                // list. Captions can wrap differently across language and DPI.
                foreach (FrameworkElement child in notificationPage.Children)
                    if (!ReferenceEquals(child, notificationListScroll))
                    { MeasureNotificationStatic(child, contentWidth); remaining -= child.DesiredSize.Height; }
                if (expandedHint != null) { MeasureNotificationStatic(expandedHint, hostWidth); remaining -= expandedHint.DesiredSize.Height; }
                double maximum = Math.Max(72, remaining);
                if (Math.Abs(notificationListScroll.MaxHeight - maximum) > .5) notificationListScroll.MaxHeight = maximum;
            }
            finally { measuringNotificationViewport = false; }
        }

        private static void MeasureNotificationStatic(FrameworkElement element, double width)
        {
            // Visibility/text changes invalidate their immediate layout parent.
            // Refresh this small static subtree before reading its size, even
            // when the outer Border still has a cached same-width measurement.
            // The virtualized notification list never enters this traversal.
            InvalidateNotificationStatic(element);
            element.Measure(new Size(width, Double.PositiveInfinity));
        }

        private static void InvalidateNotificationStatic(DependencyObject element)
        {
            for (int child = 0; child < VisualTreeHelper.GetChildrenCount(element); child++)
                InvalidateNotificationStatic(VisualTreeHelper.GetChild(element, child));
            var layout = element as UIElement;
            if (layout != null) layout.InvalidateMeasure();
        }

        private void ResetExpandedSize()
        {
            StopPageTransition(); expandedSessionDefaultHeight = Double.NaN;
            settings.CustomExpandedSize = false; settings.ExpandedWidth = 388; settings.ExpandedHeight = 680;
            SaveSettings(); if (expanded) SetExpanded(true);
        }

        private void RunInteractionSmoke()
        {
            settings.Side = "right"; settings.AutoSnapToEdge = false; SetExpanded(false, false); Rect area = PlacementArea();
            Rect free = new Rect(area.Left + (area.Width - 88) * .35, area.Top + (area.Height - 238) * .3, 88, 238);
            ApplyBounds(free); RememberPlacement(true);
            if (!settings.FreePosition) throw new Exception("Free placement snapped away from edges");
            SetExpanded(true, false); SetExpanded(false, false);
            if (Math.Abs(Left - free.Left) > 1 || Math.Abs(Top - free.Top) > 1) throw new Exception("Free compact position was not retained");
            dragging = true;
            ShowPeek(new IslandNotification { AppId = "drag-test", AppName = "测试", Title = "不打断拖动", IsDemo = true });
            SetExpanded(true, false);
            if (peeking || expanded) throw new Exception("Notification or hover interrupted compact drag");
            dragging = false;
            SetExpanded(true, false);
            Rect size = WindowGeometry.Resize(CurrentBounds(), new Point(130, -95), "RB", area);
            ApplyBounds(size); settings.CustomExpandedSize = true; settings.ExpandedWidth = (int)Math.Round(size.Width); settings.ExpandedHeight = (int)Math.Round(size.Height); RememberPlacement(false);
            SelectPage(false); SetExpanded(true, false); SetExpanded(false, false); SetExpanded(true, false);
            if (Math.Abs(Width - size.Width) > 1 || Math.Abs(Height - size.Height) > 1) throw new Exception("Custom size reset by tab or expansion");
            UpdateLayout(); if (resizeLayer.Visibility != Visibility.Visible || resizeLayer.Children.Count != 8) throw new Exception("Eight resize handles missing");
            var message = new IslandNotification { Id = "source-ui-test", AppId = "demo.source-ui", AppName = "屏蔽测试", Title = "示例", CreatedAt = DateTime.UtcNow, IsDemo = true };
            notificationPreferences.Observe(new[] { message }); notificationFeed.Merge(new[] { message }, false, false);
            SetNotificationSourceBlocked(NotificationPreferences.SourceKey(message), true);
            SetExpanded(false, false); ShowPeek(message);
            if (peeking || notificationFeed.Items.Any(item => item.AppId == message.AppId)) throw new Exception("Blocked source leaked into preview or feed");
            SetNotificationSourceBlocked(NotificationPreferences.SourceKey(message), false); SetExpanded(true, false); ShowPeek(message);
            if (!peeking || resizeLayer.Visibility != Visibility.Collapsed) throw new Exception("Preview retained expanded resize handles");
            SetExpanded(false, false);
            RunFourEdgeSmoke();
        }

        private void RunFourEdgeSmoke()
        {
            Rect area = PlacementArea();
            foreach (string edge in new[] { "left", "right", "top", "bottom" })
            {
                ChangeSide("right");
                double x = area.Left + area.Width * (edge == "left" ? .05 : edge == "right" ? .95 : .5);
                double y = area.Top + area.Height * (edge == "top" ? .05 : edge == "bottom" ? .95 : .5);
                ApplyBounds(WindowGeometry.ClampMove(new Rect(x - Width / 2, y - Height / 2, Width, Height), area));
                RememberPlacement(true); UpdateLayout();
                if (settings.Side != edge || settings.FreePosition || !TouchesDockEdge(CurrentBounds(), area)) throw new Exception("Nearest edge snap " + edge);
                if (Math.Abs(Width - CompactSize.Width) > 1 || Math.Abs(Height - CompactSize.Height) > 1) throw new Exception("Compact orientation " + edge);
                Rect compactBounds = CurrentBounds();
                if ((compact.Orientation == Orientation.Horizontal) != HorizontalDock) throw new Exception("Compact layout " + edge);
                SetExpanded(true, false); if (!TouchesDockEdge(CurrentBounds(), area)) throw new Exception("Inward expansion " + edge);
                SetExpanded(false, false);
                if (Math.Abs(Left - compactBounds.Left) > 1 || Math.Abs(Top - compactBounds.Top) > 1) throw new Exception("Dock anchor shifted on expansion " + edge);
            }
            SetCompactTextDirection("rotate180"); if (Math.Abs(compactNotificationIcon.RenderTransform.Value.M22 + 1) > .01 || primaryRing.TextDirection != "rotate180") throw new Exception("Rotated compact text");
            SetCompactTextDirection("mirror"); if (compactNotificationIcon.RenderTransform.Value.M11 != -1 || primaryRing.TextDirection != "mirror") throw new Exception("Mirrored compact text");
            SetCompactTextDirection("auto"); if (!compactNotificationIcon.RenderTransform.Value.IsIdentity) throw new Exception("Restored compact text");
        }
    }
}
