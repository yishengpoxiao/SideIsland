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
    // Own demo HWND and isolated settings only. Drag coordinates are injected
    // into MoveDrag; these tests never move the real cursor or send system input.
    internal static class CompactDragTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        [DllImport("user32.dll")] private static extern IntPtr GetCapture();
        [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr window);
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        public static void Run()
        {
            string previous = Environment.GetEnvironmentVariable("CODEX_ISLAND_DATA_DIR");
            string directory = Path.Combine(Path.GetTempPath(), "CodexIsland.CompactDragTests." + Guid.NewGuid().ToString("N"));
            IslandWindow window = null;
            try
            {
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", directory);
                new SettingsStore().Save(new Settings {
                    AlwaysOnTop = false, AutoCollapse = false, NotificationsEnabled = false,
                    CustomExpandedSize = true, ExpandedWidth = 440, ExpandedHeight = 520
                });
                window = new IslandWindow(true) { ShowActivated = false };
                window.Loaded += (s, e) => StopTimers(window);
                window.Show(); Pump(1); StopTimers(window);
                Rect area = Area(window);
                Check(area.Width >= 392 && area.Height >= 392, "working area supports own-window drag fixtures");
                var shell = Read<Border>(window, "shell");
                // Capture refreshes report the actual physical button state. Our
                // routed press intentionally changes only the own-window gesture.
                MouseEventHandler ignorePhysicalButton = (s, e) => e.Handled = true;
                shell.PreviewMouseMove += ignorePhysicalButton;
                try
                {
                    foreach (string side in new[] { "left", "right", "top", "bottom" })
                        ExpandedTitleDrag(window, side);
                    StableExpandedShellDoesNotDrag(window);
                    FreePlacementAndLostCapture(window);
                    AnimationDrag(window, true);
                    AnimationDrag(window, false);
                    ExplicitCollapseDrag(window);
                    CompactDrag(window);
                }
                finally { shell.PreviewMouseMove -= ignorePhysicalButton; }
                Console.WriteLine("PASS: compact-only movement from four expanded title bars, threshold and pointer anchor, capture lifecycle, free/edge placement, pinned state and interrupted animation cleanup.");
            }
            finally
            {
                if (window != null)
                {
                    var captured = Mouse.Captured as DependencyObject;
                    if (captured != null && Window.GetWindow(captured) == window) Mouse.Capture(null);
                    window.Close();
                }
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", previous);
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        private static void ExpandedTitleDrag(IslandWindow window, string side)
        {
            Configure(window, side, true);
            window.SetExpanded(true, false); window.UpdateLayout();
            Rect expanded = Bounds(window), visual = window.CurrentVisualBounds();
            var settings = Read<Settings>(window, "settings");
            var shell = Read<Border>(window, "shell");
            var pin = Read<Button>(window, "pinButton");
            pin.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(Read<bool>(window, "pinned"), side + " fixture begins pinned");
            Point press = BeginPress(window, Title(window), visual, side + " expanded title");
            Point small = Offset(window, press, new Vector(SystemParameters.MinimumHorizontalDragDistance * .25,
                SystemParameters.MinimumVerticalDragDistance * .25));
            Check(!Move(window, small), side + " movement within both system thresholds remains a press");
            Check(Read<bool>(window, "expanded") && !Read<bool>(window, "moved"), side + " threshold does not collapse or mark movement");
            Same(Bounds(window), expanded, side + " threshold preserves expanded geometry");
            Check(ReferenceEquals(Mouse.Captured, shell), side + " title press captures persistent shell");

            Vector initialDelta = side == "right" ? new Vector(-70, 30) : side == "bottom" ? new Vector(35, -70) : new Vector(70, 35);
            Point first = Offset(window, press, initialDelta);
            Rect expected = ExpectedFirst(window, visual, press, first, side);
            Check(Move(window, first), side + " movement crosses threshold");
            AssertCompact(window, side, side + " first movement");
            Same(Bounds(window), expected, side + " compact footprint retains normalized pointer grab point");
            Check(ReferenceEquals(Mouse.Captured, shell), side + " hiding title retains mouse capture");
            Check(!Read<bool>(window, "pinned") && (string)pin.Content == "◇", side + " dragging cancels pinned expansion");
            Check(Read<bool>(window, "hoverSuppressed"), side + " dragging suppresses immediate hover reopen");
            Check(settings.ExpandedWidth == 440 && settings.ExpandedHeight == 520 && settings.CustomExpandedSize,
                side + " beginning movement preserves custom expanded settings");

            Rect rebased = Bounds(window);
            Point second = Offset(window, first, new Vector(19, 13));
            Move(window, second);
            Same(Bounds(window), WindowGeometry.ClampMove(new Rect(rebased.Left + 19, rebased.Top + 13, rebased.Width, rebased.Height), Area(window)),
                side + " second move applies displacement once after compact rebase");
            Point third = Offset(window, first, new Vector(31, 22));
            Move(window, third);
            Same(Bounds(window), WindowGeometry.ClampMove(new Rect(rebased.Left + 31, rebased.Top + 22, rebased.Width, rebased.Height), Area(window)),
                side + " subsequent displacement stays relative to first compact position");
            AssertNoInterruption(window, side);

            Rect area = Area(window);
            Rect edgeTarget = WindowGeometry.DockCompact(new Rect(area.Left + area.Width * .5 - rebased.Width * .5,
                area.Top + area.Height * .5 - rebased.Height * .5, rebased.Width, rebased.Height), area, side);
            Move(window, Offset(window, first, new Vector(edgeTarget.Left - rebased.Left, edgeTarget.Top - rebased.Top)));
            Release(window);
            Check(settings.Side == side && !settings.FreePosition, side + " release selects closest requested edge");
            Same(Bounds(window), edgeTarget, side + " release docks correct compact orientation");
            Check(!Read<bool>(window, "dragging") && !ReferenceEquals(Mouse.Captured, shell), side + " release completes capture lifecycle");
            AssertCompact(window, side, side + " release");
            Check(Read<bool>(window, "hoverSuppressed"), side + " release stays collapsed under pointer");
            window.SetExpanded(true, false); window.UpdateLayout();
            Check(Near(window.Width, expanded.Width) && Near(window.Height, expanded.Height), side + " reopen retains custom expanded dimensions");
            StopTimers(window);
        }

        private static void FreePlacementAndLostCapture(IslandWindow window)
        {
            Configure(window, "right", false);
            window.SetExpanded(true, false); window.UpdateLayout();
            Rect visual = window.CurrentVisualBounds();
            Point press = BeginPress(window, Title(window), visual, "free expanded title");
            Point first = Offset(window, press, new Vector(-100, 45));
            Move(window, first);
            Rect before = Bounds(window);
            var shell = Read<Border>(window, "shell");
            Check(ReferenceEquals(Mouse.Captured, shell), "free placement captures shell before loss");
            shell.ReleaseMouseCapture();
            Check(!Read<bool>(window, "dragging") && Mouse.Captured == null, "lost capture finishes movement once");
            AssertCompact(window, "right", "lost capture");
            Same(Bounds(window), before, "auto snap disabled retains freely dragged compact position");
            Check(Read<Settings>(window, "settings").FreePosition, "auto snap disabled persists free placement");
            Release(window);
            Same(Bounds(window), before, "later mouse-up after capture loss does not expand or move");
            window.SetExpanded(true, false); window.SetExpanded(false, false);
            Same(Bounds(window), before, "free compact anchor survives expand and collapse");
            StopTimers(window);
        }

        private static void StableExpandedShellDoesNotDrag(IslandWindow window)
        {
            Configure(window, "right", false);
            window.SetExpanded(true, false); window.UpdateLayout();
            StopTimers(window); InitializeMouseSource(window); StopTimers(window);
            Rect expanded = Bounds(window);
            var shell = Read<Border>(window, "shell");
            Check(Read<bool>(window, "expanded") && !Read<bool>(window, "visualTransitionActive"), "expanded shell rejection fixture is stable");
            shell.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) {
                RoutedEvent = UIElement.MouseLeftButtonDownEvent, Source = shell
            });
            Check(!Read<bool>(window, "dragging") && Mouse.Captured == null && GetCapture() == IntPtr.Zero,
                "stable expanded shell does not capture or begin dragging outside its title");
            Check(!Move(window, Offset(window, window.PointToScreen(new Point(30, 30)), new Vector(70, 35))),
                "stable expanded shell rejects movement without a title gesture");
            Release(window);
            Check(Read<bool>(window, "expanded"), "stable expanded shell release does not collapse content");
            Same(Bounds(window), expanded, "stable expanded shell leaves geometry unchanged");
            StopTimers(window);
        }

        private static void AnimationDrag(IslandWindow window, bool opening)
        {
            Configure(window, "right", false);
            window.SetExpanded(!opening, false); window.SetExpanded(opening, true);
            WaitForIntermediateFrame(window, (opening ? "opening" : "closing") + " fixture"); StopTimers(window);
            Rect visual = window.CurrentVisualBounds();
            if (SystemParameters.ClientAreaAnimation)
                Check(Read<bool>(window, "visualTransitionActive"), (opening ? "opening" : "closing") + " fixture starts in animation");
            var target = opening ? (UIElement)Title(window) : Read<Border>(window, "shell");
            Point press = BeginPress(window, target, visual, opening ? "opening animation" : "closing animation");
            Same(Read<Rect>(window, "motionBounds"), visual, "animated press records visible footprint rather than union HWND");
            Same(window.CurrentVisualBounds(), visual, "animated press does not settle its endpoint");
            Point first = Offset(window, press, new Vector(-75, 26));
            Rect expected = ExpectedFirst(window, visual, press, first, "right");
            Move(window, first);
            AssertCompact(window, "right", (opening ? "opening" : "closing") + " interrupted by movement");
            Same(Bounds(window), expected, "animated drag uses current visible pointer anchor");
            Check(ReferenceEquals(Mouse.Captured, Read<Border>(window, "shell")), "animation cancellation retains shell capture");
            Release(window);
            Rect compact = Bounds(window);
            // Ignore actual mouse-over updates while waiting out the stale clock.
            window.IsHitTestVisible = false;
            Pump(IslandWindow.TransitionMilliseconds + 55); StopTimers(window);
            AssertCompact(window, "right", "after obsolete animation completion deadline");
            Same(Bounds(window), compact, "old animation callback cannot restore expanded bounds");
            window.IsHitTestVisible = true;
        }

        private static void ExplicitCollapseDrag(IslandWindow window)
        {
            Configure(window, "right", false);
            window.SetExpanded(true, false);
            Call(window, "CollapseNow"); WaitForIntermediateFrame(window, "explicit collapse fixture"); StopTimers(window);
            Check(!Read<bool>(window, "expanded") && Read<bool>(window, "hoverSuppressed"),
                "explicit collapse fixture remains logically collapsed with hover suppressed");
            if (SystemParameters.ClientAreaAnimation)
                Check(Read<bool>(window, "visualTransitionActive"), "explicit collapse fixture retains its shrinking animation");
            Rect visual = window.CurrentVisualBounds();
            Point press = BeginPress(window, Read<Border>(window, "shell"), visual, "explicit collapse animation");
            Check(!Read<bool>(window, "expanded") && Read<bool>(window, "hoverSuppressed"),
                "input-source initialization and press cannot reverse explicit collapse");
            Same(window.CurrentVisualBounds(), visual, "explicit collapse press leaves current visual frame intact");
            Point first = Offset(window, press, new Vector(-75, 26));
            Rect expected = ExpectedFirst(window, visual, press, first, "right");
            Check(Move(window, first), "explicit collapse movement crosses threshold");
            AssertCompact(window, "right", "explicit collapse interrupted by movement");
            Same(Bounds(window), expected, "explicit collapse drag retains current visible pointer anchor");
            Check(ReferenceEquals(Mouse.Captured, Read<Border>(window, "shell")), "explicit collapse movement retains shell capture");
            Release(window);
            Rect compact = Bounds(window);
            window.IsHitTestVisible = false;
            Pump(IslandWindow.TransitionMilliseconds + 55); StopTimers(window);
            AssertCompact(window, "right", "explicit collapse old completion deadline");
            Same(Bounds(window), compact, "explicit collapse callback cannot change finished movement");
            window.IsHitTestVisible = true;
        }

        private static void CompactDrag(IslandWindow window)
        {
            Configure(window, "top", false);
            window.SetExpanded(false, false); window.UpdateLayout();
            Rect compact = Bounds(window);
            Point press = BeginPress(window, Read<Border>(window, "shell"), window.CurrentVisualBounds(), "already compact");
            Point first = Offset(window, press, new Vector(40, 55));
            Move(window, first);
            Same(Bounds(window), WindowGeometry.ClampMove(new Rect(compact.Left + 40, compact.Top + 55, compact.Width, compact.Height), Area(window)),
                "already compact drag preserves original grip offset and only moves");
            Release(window);
            AssertCompact(window, "top", "already compact movement release");
            StopTimers(window);
        }

        private static void AssertNoInterruption(IslandWindow window, string label)
        {
            Rect bounds = Bounds(window);
            Call(window, "OnIslandMouseEnter"); Call(window, "OnHoverElapsed");
            window.SetExpanded(true, true);
            Call(window, "ShowPeek", new IslandNotification { Id = "compact-drag", AppId = "fixture.compact-drag", AppName = "测试", Title = "拖动不打断", IsDemo = true });
            Check(!Read<bool>(window, "expanded") && !Read<bool>(window, "peeking"), label + " hover and notification do not reopen during movement");
            Check(!Read<DispatcherTimer>(window, "hoverTimer").IsEnabled && !Read<DispatcherTimer>(window, "collapseTimer").IsEnabled,
                label + " movement leaves hover/collapse timers stopped");
            Same(Bounds(window), bounds, label + " asynchronous mode requests do not change movement footprint");
        }

        private static void AssertCompact(IslandWindow window, string side, string label)
        {
            bool horizontal = side == "top" || side == "bottom";
            Rect area = Area(window);
            Check(!Read<bool>(window, "expanded") && !Read<bool>(window, "peeking"), label + " has compact logical mode");
            Check(Near(window.Width, Math.Min(horizontal ? 238 : 88, area.Width)) && Near(window.Height, Math.Min(horizontal ? 88 : 238, area.Height)), label + " has compact native dimensions");
            Check(!Read<bool>(window, "visualTransitionActive"), label + " has no remaining transition driver");
            Check(Read<Image>(window, "outgoingImage").Source == null, label + " releases obsolete content bitmap");
            Check(Read<StackPanel>(window, "compact").Visibility == Visibility.Visible && Read<ScrollViewer>(window, "detailsScroll").Visibility == Visibility.Collapsed,
                label + " shows compact content and hides expanded content");
            Check(Read<Grid>(window, "resizeLayer").Visibility == Visibility.Collapsed, label + " hides expanded resize handles");
        }

        private static void Configure(IslandWindow window, string side, bool snap)
        {
            var settings = Read<Settings>(window, "settings");
            settings.Side = side; settings.AutoSnapToEdge = snap; settings.FreePosition = false;
            settings.HorizontalPosition = .5; settings.VerticalPosition = .5;
            settings.CustomExpandedSize = true; settings.ExpandedWidth = 440; settings.ExpandedHeight = 520;
            Write(window, "pinned", false); Read<Button>(window, "pinButton").Content = "◇";
            window.IsHitTestVisible = true;
            window.SetExpanded(false, false); StopTimers(window);
        }

        private static Point BeginPress(IslandWindow window, UIElement target, Rect visual, string label)
        {
            string beforeInitialization = GestureState(window, target);
            InitializeMouseSource(window); StopTimers(window);
            string beforeDown = GestureState(window, target);
            Point press = window.PointToScreen(new Point(visual.Left - window.Left + visual.Width * .3,
                visual.Top - window.Top + visual.Height * .16));
            var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) {
                RoutedEvent = UIElement.MouseLeftButtonDownEvent, Source = target
            };
            target.RaiseEvent(down);
            Check(Read<bool>(window, "dragging") && ReferenceEquals(Mouse.Captured, Read<Border>(window, "shell")), label + " routed press captures persistent shell; before-init={" +
                beforeInitialization + "}; before-down={" + beforeDown + "}; after-down={" + GestureState(window, target) + "}; handled=" + down.Handled);
            // The handler reads the physical cursor by design. Replace just the
            // test gesture origin with a point on our own visible island.
            Write(window, "motionStart", press);
            return press;
        }

        private static string GestureState(IslandWindow window, UIElement target)
        {
            var captured = Mouse.Captured as DependencyObject;
            Window owner = captured == null ? null : Window.GetWindow(captured);
            string capture = captured == null ? "null" : captured.GetType().Name + "/own=" + ReferenceEquals(owner, window);
            var shell = Read<Border>(window, "shell");
            return "expanded=" + Read<bool>(window, "expanded") + ", peeking=" + Read<bool>(window, "peeking") +
                ", dragging=" + Read<bool>(window, "dragging") + ", moved=" + Read<bool>(window, "moved") +
                ", resizing=" + Read<bool>(window, "resizing") + ", animation=" + Read<bool>(window, "visualTransitionActive") +
                ", target=" + ElementState(target) + ", shell=" + ElementState(shell) + ", capture=" + capture +
                ", nativeCapture=" + GetCapture() + ", visual=" + window.CurrentVisualBounds() + ", nativeBounds=" + Bounds(window);
        }

        private static string ElementState(UIElement element)
        {
            return element.GetType().Name + "/visible=" + element.IsVisible + "/enabled=" + element.IsEnabled +
                "/hit=" + element.IsHitTestVisible + "/visibility=" + element.Visibility;
        }

        private static Rect ExpectedFirst(IslandWindow window, Rect visual, Point press, Point pointer, string side)
        {
            var source = PresentationSource.FromVisual(window);
            Matrix transform = source == null ? Matrix.Identity : source.CompositionTarget.TransformFromDevice;
            Point origin = transform.Transform(press), current = transform.Transform(pointer);
            double x = Math.Max(0, Math.Min(1, (origin.X - visual.Left) / visual.Width));
            double y = Math.Max(0, Math.Min(1, (origin.Y - visual.Top) / visual.Height));
            bool horizontal = side == "top" || side == "bottom";
            return WindowGeometry.ClampMove(new Rect(current.X - (horizontal ? 238 : 88) * x,
                current.Y - (horizontal ? 88 : 238) * y, horizontal ? 238 : 88, horizontal ? 88 : 238), Area(window));
        }

        private static Point Offset(IslandWindow window, Point point, Vector displacement)
        {
            var source = PresentationSource.FromVisual(window);
            Vector delta = source == null ? displacement : source.CompositionTarget.TransformToDevice.Transform(displacement);
            return point + delta;
        }

        private static StackPanel Title(IslandWindow window)
        {
            var header = Read<Grid>(window, "expandedHeader");
            return header.Children.OfType<StackPanel>().Single(item => item.Cursor == Cursors.SizeAll);
        }

        private static void Release(IslandWindow window)
        {
            var shell = Read<Border>(window, "shell");
            shell.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) {
                RoutedEvent = UIElement.MouseLeftButtonUpEvent, Source = shell
            });
        }

        private static void InitializeMouseSource(IslandWindow window)
        {
            var source = PresentationSource.FromVisual(window) as HwndSource;
            Check(source != null && !source.IsDisposed, "own HWND exists");
            Check(GetCapture() == IntPtr.Zero && Mouse.Captured == null, "no unrelated capture before own fixture gesture");
            SetCapture(source.Handle);
            try { SendMessage(source.Handle, 0x0200, IntPtr.Zero, new IntPtr(40 | (40 << 16))); }
            finally { if (GetCapture() == source.Handle) ReleaseCapture(); }
            Check(ReferenceEquals(Mouse.PrimaryDevice.ActiveSource, source), "own WPF mouse source initialized without global input");
        }

        private static bool Move(IslandWindow window, Point pointer) { return (bool)Call(window, "MoveDrag", pointer); }
        private static Rect Area(IslandWindow window) { return (Rect)Call(window, "PlacementArea"); }
        private static Rect Bounds(Window window) { return new Rect(window.Left, window.Top, window.Width, window.Height); }
        private static T Read<T>(IslandWindow window, string name) { return (T)typeof(IslandWindow).GetField(name, Private).GetValue(window); }
        private static void Write(IslandWindow window, string name, object value) { typeof(IslandWindow).GetField(name, Private).SetValue(window, value); }
        private static object Call(IslandWindow window, string name, params object[] args) { return typeof(IslandWindow).GetMethod(name, Private).Invoke(window, args); }
        private static bool Near(double a, double b) { return Math.Abs(a - b) <= 1; }
        private static void Same(Rect actual, Rect expected, string label) { Check(Near(actual.X, expected.X) && Near(actual.Y, expected.Y) && Near(actual.Width, expected.Width) && Near(actual.Height, expected.Height), label); }
        private static void Check(bool value, string label) { if (!value) throw new Exception("Compact drag: " + label); }
        private static void StopTimers(IslandWindow window)
        {
            foreach (FieldInfo field in typeof(IslandWindow).GetFields(Private))
                if (field.FieldType == typeof(DispatcherTimer)) ((DispatcherTimer)field.GetValue(window)).Stop();
        }
        private static void WaitForIntermediateFrame(IslandWindow window, string label)
        {
            if (!SystemParameters.ClientAreaAnimation) return;
            Rect start = window.CurrentVisualBounds();
            Rect target = Read<Rect>(window, "transitionTargetBounds");
            bool observed = false;
            var frame = new DispatcherFrame();
            // The production transition subscribed first, so sample geometry
            // after it advances this frame. A Background-priority fixed delay
            // can resume only after the entire animation under a busy renderer.
            EventHandler sample = (s, e) => {
                Rect current = window.CurrentVisualBounds();
                bool atStart = Near(current.X, start.X) && Near(current.Y, start.Y) && Near(current.Width, start.Width) && Near(current.Height, start.Height);
                bool atTarget = Near(current.X, target.X) && Near(current.Y, target.Y) && Near(current.Width, target.Width) && Near(current.Height, target.Height);
                if (Read<bool>(window, "visualTransitionActive") && !atStart && !atTarget)
                { observed = true; frame.Continue = false; }
            };
            var watchdog = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromSeconds(2) };
            watchdog.Tick += (s, e) => { watchdog.Stop(); frame.Continue = false; };
            CompositionTarget.Rendering += sample;
            watchdog.Start();
            try { Dispatcher.PushFrame(frame); }
            finally { watchdog.Stop(); CompositionTarget.Rendering -= sample; }
            Check(observed && Read<bool>(window, "visualTransitionActive"), label + " reaches an actual intermediate animated frame before the drag");
        }
        private static void Pump(int milliseconds)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(Math.Max(1, milliseconds)) };
            timer.Tick += (s, e) => { timer.Stop(); frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame);
        }
    }
}
