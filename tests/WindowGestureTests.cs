using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexIsland
{
    // Own demo window only. Does not move the system cursor, send system input,
    // launch Codex, read notifications, register hotkeys, or use the real data directory.
    // Reuses the caller's STA dispatcher without creating/shutting down Application.
    internal static class WindowGestureTests
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        [DllImport("user32.dll")] private static extern IntPtr GetCapture();
        [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr window);
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        public static void Run()
        {
            string previousData = Environment.GetEnvironmentVariable("CODEX_ISLAND_DATA_DIR");
            string testData = Path.Combine(Path.GetTempPath(), "CodexIsland.GestureTests." + Guid.NewGuid().ToString("N"));
            IslandWindow window = null;
            try
            {
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", testData);
                new SettingsStore().Save(new Settings {
                    AlwaysOnTop = false, AutoCollapse = false, NotificationsEnabled = false,
                    CustomExpandedSize = true, ExpandedWidth = 360, ExpandedHeight = 360,
                    HiddenTools = "media,focus,notes,launch,clipboard,calculator,system"
                });
                window = new IslandWindow(true);
                window.ShowActivated = false;
                bool loaded = false;
                window.Loaded += (s, e) => { loaded = true; StopWindowTimers(window); };
                window.Show(); DrainDispatcher();
                Check(loaded, "Demo gesture window loaded on its own dispatcher");
                StopWindowTimers(window);
                Rect area = (Rect)Call(window, "PlacementArea");
                Check(area.Width >= 392 && area.Height >= 392, "Gesture tests require a working area of at least 392 x 392 DIP");
                ResizeAtEdge(window, "left", "RB", new Vector(24, 24));
                ResizeAtEdge(window, "right", "LB", new Vector(-24, 24));
                ResizeAtEdge(window, "top", "RB", new Vector(24, 24));
                ResizeAtEdge(window, "bottom", "RT", new Vector(24, -24));
                CheckContentHits(window);
                Console.WriteLine("PASS: four dock-edge Thumb gestures, capture lifecycle, persistent resize bounds and content-button hit testing.");
            }
            finally
            {
                if (window != null)
                {
                    // Only release capture belonging to this test window.
                    var captured = Mouse.Captured as DependencyObject;
                    if (captured != null && Window.GetWindow(captured) == window) Mouse.Capture(null);
                    window.Close();
                }
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", previousData);
                if (Directory.Exists(testData)) Directory.Delete(testData, true);
            }
        }

        private static void ResizeAtEdge(IslandWindow window, string side, string corner, Vector delta)
        {
            var settings = (Settings)Read(window, "settings");
            settings.Side = side; settings.FreePosition = false; settings.AutoSnapToEdge = true;
            settings.HorizontalPosition = .5; settings.VerticalPosition = .25;
            settings.CustomExpandedSize = true; settings.ExpandedWidth = 360; settings.ExpandedHeight = 360;
            window.SetExpanded(false, false); window.SetExpanded(true, false); window.UpdateLayout();
            var layer = (Grid)Read(window, "resizeLayer");
            var grip = layer.Children.OfType<Thumb>().Single(item => (string)item.Tag == corner);
            Check(grip.IsVisible && grip.IsEnabled, side + " inward resize corner available");
            Check(HitAncestor<Thumb>(window, grip) == grip, side + " inward corner can be hit");
            InitializeMouseSource(window);
            Rect before = Bounds(window);
            int started = 0, changed = 0, completed = 0;
            bool canceled = true;
            DragStartedEventHandler onStarted = (s, e) => started++;
            DragDeltaEventHandler onChanged = (s, e) => changed++;
            DragCompletedEventHandler onCompleted = (s, e) => { completed++; canceled = e.Canceled; };
            grip.DragStarted += onStarted; grip.DragDelta += onChanged; grip.DragCompleted += onCompleted;
            try
            {
                // Routed mouse events target this Thumb only. Thumb itself captures
                // and releases the mouse and raises its normal start/completion events.
                grip.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) {
                    RoutedEvent = UIElement.MouseLeftButtonDownEvent, Source = grip
                });
                Check(started == 1 && grip.IsDragging && ReferenceEquals(Mouse.Captured, grip), side + " drag started with capture");
                Check((bool)Read(window, "resizing"), side + " window entered resize mode");
                var source = PresentationSource.FromVisual(window);
                Vector deviceDelta = source == null ? delta : source.CompositionTarget.TransformToDevice.Transform(delta);
                var pointer = System.Windows.Forms.Cursor.Position;
                Write(window, "motionStart", new Point(pointer.X - deviceDelta.X, pointer.Y - deviceDelta.Y));
                grip.RaiseEvent(new DragDeltaEventArgs(delta.X, delta.Y) { RoutedEvent = Thumb.DragDeltaEvent });
                window.UpdateLayout();
                Rect resized = Bounds(window);
                Check(changed == 1 && resized.Width > before.Width + 5 && resized.Height > before.Height + 5,
                    side + " inward corner grows both dimensions");
                Check(corner.Contains("L") ? Near(resized.Right, before.Right) : Near(resized.Left, before.Left), side + " opposite horizontal edge stays fixed");
                Check(corner.Contains("T") ? Near(resized.Bottom, before.Bottom) : Near(resized.Top, before.Top), side + " opposite vertical edge stays fixed");
                Check(ReferenceEquals(Mouse.Captured, grip), side + " capture survives size changes");
                grip.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) {
                    RoutedEvent = UIElement.MouseLeftButtonUpEvent, Source = grip
                });
                Check(completed == 1 && !canceled && !grip.IsDragging, side + " normal drag completion raised");
                Check(!ReferenceEquals(Mouse.Captured, grip) && !(bool)Read(window, "resizing"), side + " completion releases capture and resize state");
                Call(window, "Dock"); window.UpdateLayout();
                Check(SameBounds(Bounds(window), resized), side + " docking does not undo resize");
                window.SetExpanded(false, false); window.SetExpanded(true, false); window.UpdateLayout();
                Check(Near(window.Width, resized.Width) && Near(window.Height, resized.Height), side + " collapse/reopen retains custom size");
                CheckContentHits(window);
            }
            finally
            {
                grip.DragStarted -= onStarted; grip.DragDelta -= onChanged; grip.DragCompleted -= onCompleted;
                if (ReferenceEquals(Mouse.Captured, grip)) grip.ReleaseMouseCapture();
            }
        }

        private static void CheckContentHits(IslandWindow window)
        {
            window.UpdateLayout();
            foreach (string name in new[] { "pinButton", "notificationsTab", "statusTab", "toolsTab" })
            {
                var button = (Button)Read(window, name);
                Check(button.IsVisible && button.ActualWidth > 0 && button.ActualHeight > 0, name + " is visible");
                Check(HitAncestor<Button>(window, button) == button, name + " remains clickable below resize overlay");
            }
        }

        private static T HitAncestor<T>(Window window, FrameworkElement element) where T : DependencyObject
        {
            Point center = element.TranslatePoint(new Point(element.ActualWidth / 2, element.ActualHeight / 2), window);
            DependencyObject hit = window.InputHitTest(center) as DependencyObject;
            while (hit != null)
            {
                T match = hit as T; if (match != null) return match;
                hit = VisualTreeHelper.GetParent(hit);
            }
            return null;
        }

        private static void StopWindowTimers(IslandWindow window)
        {
            foreach (FieldInfo field in typeof(IslandWindow).GetFields(Fields))
                if (field.FieldType == typeof(DispatcherTimer)) ((DispatcherTimer)field.GetValue(window)).Stop();
        }
        private static void InitializeMouseSource(IslandWindow window)
        {
            // RaiseEvent does not initialize MouseDevice.ActiveSource. Without an
            // input report, CaptureMouse depends on whether the user's actual cursor
            // happened to enter this window before the test. Prime only our own HWND,
            // then release native capture before checking the Thumb's normal capture.
            // This sends no button event and never changes the system cursor position.
            var source = PresentationSource.FromVisual(window) as HwndSource;
            Check(source != null && !source.IsDisposed, "Own window has a live mouse input source");
            Check(GetCapture() == IntPtr.Zero && Mouse.Captured == null, "No prior test mouse capture remains");
            SetCapture(source.Handle);
            try
            {
                Check(GetCapture() == source.Handle, "Test setup captures only its own window");
                SendMessage(source.Handle, 0x0200, IntPtr.Zero, new IntPtr(40 | (40 << 16)));
            }
            finally { if (GetCapture() == source.Handle) ReleaseCapture(); }
            Check(ReferenceEquals(Mouse.PrimaryDevice.ActiveSource, source), "Own WPF mouse input source initialized");
            Check(GetCapture() == IntPtr.Zero && Mouse.Captured == null, "Test setup releases capture before gesture");
        }
        private static void DrainDispatcher()
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
        private static object Read(IslandWindow window, string name) { return typeof(IslandWindow).GetField(name, Fields).GetValue(window); }
        private static void Write(IslandWindow window, string name, object value) { typeof(IslandWindow).GetField(name, Fields).SetValue(window, value); }
        private static object Call(IslandWindow window, string name) { return typeof(IslandWindow).GetMethod(name, Fields).Invoke(window, null); }
        private static Rect Bounds(Window window) { return new Rect(window.Left, window.Top, window.Width, window.Height); }
        private static bool Near(double a, double b) { return Math.Abs(a - b) <= 1; }
        private static bool SameBounds(Rect a, Rect b) { return Near(a.Left, b.Left) && Near(a.Top, b.Top) && Near(a.Width, b.Width) && Near(a.Height, b.Height); }
        private static void Check(bool value, string description) { if (!value) throw new Exception("Window gesture: " + description); }
    }
}
