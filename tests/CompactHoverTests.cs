using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace CodexIsland
{
    // These fixtures modify only this process's demo WPF input tree. They never
    // move the real pointer, read account data or control another application.
    internal static class CompactHoverTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static readonly MethodInfo ChangeMouseOver = typeof(MouseDevice).GetMethod("ChangeMouseOver", Private, null, new[] { typeof(IInputElement), typeof(int) }, null);
        [DllImport("user32.dll")] private static extern IntPtr GetCapture();
        [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr window);
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        internal static void Run()
        {
            string previous = Environment.GetEnvironmentVariable("CODEX_ISLAND_DATA_DIR");
            string folder = Path.Combine(Path.GetTempPath(), "SideIsland.CompactHover." + Guid.NewGuid().ToString("N"));
            IInputElement prior = Mouse.DirectlyOver;
            IslandWindow window = null;
            MouseEventHandler ignorePhysicalButton = (s, e) => e.Handled = true;
            try
            {
                Check(ChangeMouseOver != null, "own-process WPF mouse-over fixture is available");
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", folder);
                new SettingsStore().Save(new Settings {
                    AlwaysOnTop = false, AutoCollapse = false, NotificationsEnabled = false,
                    AutoSnapToEdge = false, AutoMusic = false, CompactSlot1 = "focus", CompactSlot2 = "awake"
                });
                window = new IslandWindow(true) { ShowActivated = false };
                window.Loaded += (s, e) => StopTimers(window);
                window.Show(); Pump(1); window.UpdateLayout(); StopTimers(window);
                var shell = Read<Border>(window, "shell");
                window.PreviewMouseMove += ignorePhysicalButton;
                try
                {
                    InitialHoverWaits(window);
                    LeaveCancelsAndReentryRestarts(window);
                    PressProtectsPendingDrag(window);
                }
                finally { window.PreviewMouseMove -= ignorePhysicalButton; }
                Console.WriteLine("PASS: compact blank/brand hover waits 650 ms on four edges; leaving cancels and reentry restarts the dwell; held drag preparation, stale hover callback, movement and release all stay compact.");
            }
            finally
            {
                Over(null);
                if (window != null)
                {
                    var captured = Mouse.Captured as DependencyObject;
                    if (captured != null && Window.GetWindow(captured) == window) Mouse.Capture(null);
                    StopTimers(window); window.Close();
                }
                Over(prior);
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", previous);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }

        private static void InitialHoverWaits(IslandWindow window)
        {
            foreach (string edge in new[] { "left", "right", "top", "bottom" })
            {
                Configure(window, edge);
                UIElement target = edge == "left" || edge == "top" ? (UIElement)Read<Border>(window, "shell") : Read<TextBlock>(window, "compactNotificationIcon");
                Enter(window, target);
                var timer = Read<DispatcherTimer>(window, "hoverTimer");
                Check(timer.Interval.TotalMilliseconds >= 650, edge + " hover requires a deliberate dwell rather than the old 100 ms trigger");
                Check(timer.IsEnabled, edge + " blank/brand hover arms its real delay timer");
                Pump(300);
                Compact(window, edge + " brief 300 ms approach");
                WaitFor(() => Read<bool>(window, "expanded"), 1200, edge + " sustained hover eventually expands");
                Pump(IslandWindow.TransitionMilliseconds + 70);
                Check(!Read<bool>(window, "visualTransitionActive"), edge + " delayed expansion still settles normally");
            }
        }

        private static void LeaveCancelsAndReentryRestarts(IslandWindow window)
        {
            Configure(window, "right");
            var brand = Read<TextBlock>(window, "compactNotificationIcon");
            Enter(window, brand); Pump(260); Compact(window, "first short hover");
            Over(null);
            Check(!Read<DispatcherTimer>(window, "hoverTimer").IsEnabled, "leaving cancels the pending expansion timer");
            Pump(IslandWindow.HoverDelayMilliseconds + 100);
            Compact(window, "after leaving beyond the old pending deadline");
            Enter(window, brand); Pump(300);
            Compact(window, "reentry gets a full fresh dwell instead of retaining prior elapsed time");
            WaitFor(() => Read<bool>(window, "expanded"), 1200, "fresh sustained reentry eventually expands");
        }

        private static void PressProtectsPendingDrag(IslandWindow window)
        {
            Configure(window, "bottom");
            // Initializing WPF's native mouse source queues a position refresh.
            // Drain that own-window input before assigning synthetic hover;
            // otherwise it clears MouseOver during the subsequent dwell.
            InitializeMouseSource(window); Pump(60); StopTimers(window);
            var shell = Read<Border>(window, "shell");
            var brand = Read<TextBlock>(window, "compactNotificationIcon");
            Enter(window, brand); Pump(220);
            Check(Read<DispatcherTimer>(window, "hoverTimer").IsEnabled, "hover is pending immediately before the drag press");
            Point origin = Press(window, brand);
            Check(!Read<DispatcherTimer>(window, "hoverTimer").IsEnabled, "press cancels hover before the movement threshold");
            Pump(IslandWindow.HoverDelayMilliseconds + 100);
            Compact(window, "held press beyond the dwell deadline");
            Call(window, "OnHoverElapsed");
            Compact(window, "stale hover callback while a press is captured");
            Vector delta = new Vector(45, 35);
            var source = PresentationSource.FromVisual(window);
            if (source != null) delta = source.CompositionTarget.TransformToDevice.Transform(delta);
            Check((bool)Call(window, "MoveDrag", origin + delta), "the held press can cross the real drag threshold");
            Compact(window, "actual drag movement");
            Release(window, shell); Pump(IslandWindow.HoverDelayMilliseconds + 80);
            Compact(window, "drag release never queues automatic expansion");
        }

        private static void Configure(IslandWindow window, string edge)
        {
            Over(null); StopTimers(window);
            var settings = Read<Settings>(window, "settings");
            settings.Side = edge; settings.CompactMode = "quota"; settings.FreePosition = false;
            settings.HorizontalPosition = .5; settings.VerticalPosition = .5;
            Write(window, "pinned", false); Write(window, "hoverSuppressed", false);
            window.SetExpanded(false, false); Call(window, "UpdateToolkitCompact"); window.UpdateLayout(); StopTimers(window);
        }

        private static void Enter(IslandWindow window, UIElement target)
        {
            Over(null); StopTimers(window); Write(window, "hoverSuppressed", false);
            Over(target); Call(window, "RecheckCompactActionHover");
            Check(window.IsMouseOver, "own window receives synthetic hover in its WPF input tree");
        }

        private static Point Press(IslandWindow window, UIElement target)
        {
            // The artificial dwell pumps native source refreshes. Re-establish
            // this fixture's input source at the actual synthetic press, just as
            // the quick-action/drag fixtures do, without moving the real pointer.
            InitializeMouseSource(window);
            Over(target);
            target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) {
                RoutedEvent = Mouse.MouseDownEvent, Source = target
            });
            Check(Read<bool>(window, "dragging") && ReferenceEquals(Mouse.Captured, Read<Border>(window, "shell")), "press captures only the persistent own shell (dragging=" + Read<bool>(window, "dragging") + ", captured=" + (Mouse.Captured == null ? "null" : Mouse.Captured.GetType().Name) + ", expanded=" + Read<bool>(window, "expanded") + ", peeking=" + Read<bool>(window, "peeking") + ", resizing=" + Read<bool>(window, "resizing") + ", native capture=" + GetCapture() + ", enabled=" + window.IsEnabled + ", visible=" + window.IsVisible + ")");
            Point origin = target.PointToScreen(new Point(Math.Max(1, target.RenderSize.Width / 2), Math.Max(1, target.RenderSize.Height / 2)));
            Write(window, "motionStart", origin);
            return origin;
        }

        private static void Release(IslandWindow window, UIElement target)
        {
            target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) {
                RoutedEvent = Mouse.MouseUpEvent, Source = target
            });
        }

        private static void InitializeMouseSource(IslandWindow window)
        {
            var source = PresentationSource.FromVisual(window) as HwndSource;
            Check(source != null && GetCapture() == IntPtr.Zero && Mouse.Captured == null, "input fixture begins with its own HWND and no mouse capture");
            SetCapture(source.Handle);
            try { SendMessage(source.Handle, 0x0200, IntPtr.Zero, new IntPtr(40 | (40 << 16))); }
            finally { if (GetCapture() == source.Handle) ReleaseCapture(); }
            Check(ReferenceEquals(Mouse.PrimaryDevice.ActiveSource, source), "synthetic native input targets only the own test HWND");
        }

        private static void Compact(IslandWindow window, string label)
        {
            Check(!Read<bool>(window, "expanded") && !Read<bool>(window, "peeking") && !Read<bool>(window, "visualTransitionActive"), label + " remains fully compact");
        }
        private static void Over(IInputElement target) { if (ChangeMouseOver != null) ChangeMouseOver.Invoke(Mouse.PrimaryDevice, new object[] { target, Environment.TickCount }); }
        private static void StopTimers(IslandWindow window)
        {
            foreach (FieldInfo field in typeof(IslandWindow).GetFields(Private))
                if (field.FieldType == typeof(DispatcherTimer)) ((DispatcherTimer)field.GetValue(window)).Stop();
        }
        private static void WaitFor(Func<bool> condition, int timeout, string label)
        {
            var watch = Stopwatch.StartNew();
            while (!condition() && watch.ElapsedMilliseconds < timeout) Pump(20);
            Check(condition(), label + " (timeout " + timeout + " ms)");
        }
        private static void Pump(int milliseconds)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
            timer.Tick += (s, e) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
        }
        private static T Read<T>(object value, string name) { return (T)value.GetType().GetField(name, Private).GetValue(value); }
        private static void Write(object value, string name, object data) { value.GetType().GetField(name, Private).SetValue(value, data); }
        private static object Call(object value, string name, params object[] arguments)
        {
            try { return value.GetType().GetMethod(name, Private).Invoke(value, arguments); }
            catch (TargetInvocationException error) { throw error.InnerException ?? error; }
        }
        private static void Check(bool value, string message) { if (!value) throw new Exception("Compact hover: " + message); }
    }
}
