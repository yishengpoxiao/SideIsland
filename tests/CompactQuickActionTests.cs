using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexIsland
{
    // All input stays inside this process's own demo HWND. Never move the real
    // cursor, write the clipboard, or acquire a real Windows power request.
    internal static class CompactQuickActionTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        [DllImport("user32.dll")] private static extern IntPtr GetCapture();
        [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr window);
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowEnabled(IntPtr window);
        [DllImport("user32.dll")] private static extern short GetKeyState(int key);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);

        public static void Run()
        {
            string previous = Environment.GetEnvironmentVariable("CODEX_ISLAND_DATA_DIR");
            string folder = Path.Combine(Path.GetTempPath(), "SideIsland.CompactQuickActions." + Guid.NewGuid().ToString("N"));
            IslandWindow window = null;
            MouseEventHandler ignorePhysicalButton = (s, e) => e.Handled = true;
            try
            {
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", folder);
                var initial = new Settings {
                    AlwaysOnTop = false, AutoCollapse = false, NotificationsEnabled = false,
                    AutoSnapToEdge = false, CompactSlot1 = "focus", CompactSlot2 = "clock"
                };
                Check(initial.FocusMinutes == 25, "new settings retain the 25-minute default");
                new SettingsStore().Save(initial);
                window = OpenWindow();
                var shell = Read<Border>(window, "shell");
                shell.PreviewMouseMove += ignorePhysicalButton;
                try
                {
                    PersistCustomMinutes(window);
                    FocusClicksOnEveryEdge(window);
                    InitialPressOwnsTheAction(window);
                    CancelledGestures(window);
                    AwakeClicks(window);
                    DestinationActions(window);
                    ComponentHoverDoesNotStealClicks(window);
                }
                finally { shell.PreviewMouseMove -= ignorePhysicalButton; }
                ReleaseOwnCapture(window); StopTimers(window); window.Close(); window = null;

                window = OpenWindow();
                var restarted = Read<FocusPanel>(window, "focusPanel");
                Check(restarted.FocusMinutes == 37 && restarted.CompactStatus.EndsWith("37:00") && !restarted.IsRunning,
                    "restart restores the selected duration without starting a timer");
                Check(Read<Settings>(window, "settings").FocusMinutes == 37, "root settings retain the custom duration on restart");
                Console.WriteLine("PASS: compact focus start/pause/resume on four edges and both slots, legacy mode, release-only activation, drag/capture cancellation, demo awake toggle, destination routes, hover guards and persisted 37-minute focus.");
            }
            finally
            {
                if (window != null) { ReleaseOwnCapture(window); StopTimers(window); window.Close(); }
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", previous);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }

        private static IslandWindow OpenWindow()
        {
            var window = new IslandWindow(true) { ShowActivated = false };
            window.Loaded += (s, e) => StopTimers(window);
            window.Show(); Pump(1); window.UpdateLayout(); StopTimers(window);
            return window;
        }

        private static void PersistCustomMinutes(IslandWindow window)
        {
            var panel = Read<FocusPanel>(window, "focusPanel");
            Check(panel.FocusMinutes == 25 && !panel.IsRunning, "fresh root starts with an idle 25-minute focus");
            Read<TextBox>(panel, "customMinutes").Text = "37";
            Call(panel, "ApplyCustom");
            Check(panel.FocusMinutes == 37 && panel.CompactStatus.EndsWith("37:00"), "custom duration resets the focus to 37 minutes");
            var settings = Read<Settings>(window, "settings");
            Check(settings.FocusMinutes == 37, "focus change reaches the root settings through its change event");
            // Demo windows intentionally skip automatic writes. Persist only
            // this isolated fixture explicitly to verify the restart contract.
            new SettingsStore().Save(settings);
            Check(new SettingsStore().Load().FocusMinutes == 37, "isolated settings round-trip retains the custom focus duration");
            Keyboard.ClearFocus();
        }

        private static void FocusClicksOnEveryEdge(IslandWindow window)
        {
            var focus = Read<FocusPanel>(window, "focusPanel");
            int changes = 0; Action onChange = () => changes++;
            focus.StatusChanged += onChange;
            try
            {
                foreach (string edge in new[] { "left", "right", "top", "bottom" })
                foreach (bool second in new[] { false, true })
                {
                    Configure(window, edge, "quota", second ? "clock" : "focus", second ? "focus" : "clock");
                    var slot = Slot(window, second);
                    Point center = slot.TranslatePoint(new Point(slot.ActualWidth / 2, slot.ActualHeight / 2), window);
                    var hit = window.InputHitTest(center) as DependencyObject;
                    Check((string)Call(window, "FindCompactAction", hit) == "focus", edge + " actual slot center hits its configured action");
                    Check((string)Call(window, "FindCompactAction", slot) == "focus", edge + " slot resolves the selected focus action");
                    Check((string)Call(window, "FindCompactAction", Read<CompactWidgetVisual>(window, second ? "compactWidget2" : "compactWidget1")) == "focus",
                        edge + " widget descendant resolves its containing slot");
                    for (int repeat = 0; repeat < 3; repeat++)
                    {
                        bool before = focus.IsRunning; int priorChanges = changes;
                        Press(window, slot);
                        Check(focus.IsRunning == before && changes == priorChanges, "press alone never starts or pauses focus");
                        Check(Read<string>(window, "pendingCompactAction") == "focus", "press remembers the selected focus component");
                        Release(window, Read<Border>(window, "shell"));
                        Check(focus.IsRunning != before && changes == priorChanges + 1, "one release performs exactly one focus transition");
                        Check(String.IsNullOrEmpty(Read<string>(window, "pendingCompactAction")), "release clears its pending action");
                        Compact(window, edge + " quick focus release");
                    }
                    if (focus.IsRunning) focus.ToggleQuickFocus();
                }

                Configure(window, "right", "focus", "cpu", "memory");
                var alternate = Read<StackPanel>(window, "compactAlternate");
                Check((string)Call(window, "FindCompactAction", Read<TextBlock>(window, "compactAlternateMain")) == "focus",
                    "legacy large focus descendant resolves the same quick action");
                Click(window, alternate); Check(focus.IsRunning, "legacy large focus starts on click"); Compact(window, "legacy focus start");
                Click(window, alternate); Check(!focus.IsRunning, "legacy large focus pauses on its next click"); Compact(window, "legacy focus pause");
            }
            finally { focus.StatusChanged -= onChange; if (focus.IsRunning) focus.ToggleQuickFocus(); }
        }

        private static void InitialPressOwnsTheAction(IslandWindow window)
        {
            Configure(window, "top", "quota", "focus", "notifications");
            var focus = Read<FocusPanel>(window, "focusPanel");
            Press(window, Slot(window, false));
            // Capture can report the eventual release over a different widget.
            // A genuine click belongs to the component pressed, not that one.
            Release(window, Slot(window, true));
            Check(focus.IsRunning, "release over a different slot executes the original focus press");
            Compact(window, "release over another slot");
            focus.ToggleQuickFocus();
        }

        private static void CancelledGestures(IslandWindow window)
        {
            Configure(window, "right", "quota", "focus", "clock");
            var focus = Read<FocusPanel>(window, "focusPanel");
            var shell = Read<Border>(window, "shell");
            Point origin = Press(window, Slot(window, false));
            Check((bool)Call(window, "MoveDrag", Offset(window, origin, new Vector(40, 35))), "component movement crosses the real drag threshold");
            Check(String.IsNullOrEmpty(Read<string>(window, "pendingCompactAction")), "actual movement cancels the pending component action");
            Release(window, shell);
            Check(!focus.IsRunning, "drag release never starts a focus timer"); Compact(window, "component drag");

            Press(window, Slot(window, false)); shell.ReleaseMouseCapture();
            Check(!Read<bool>(window, "dragging") && String.IsNullOrEmpty(Read<string>(window, "pendingCompactAction")), "lost capture cancels the gesture and action");
            Release(window, shell);
            Check(!focus.IsRunning, "a delayed release after capture loss cannot start focus"); Compact(window, "capture loss");

            Configure(window, "right", "quota", "focus", "clock");
            origin = Press(window, Slot(window, false));
            Check(!(bool)Call(window, "MoveDrag", Offset(window, origin, new Vector(.25, .25))), "subthreshold movement remains a component click");
            Release(window, shell); Check(focus.IsRunning, "small pointer jitter still starts focus once"); Compact(window, "jitter release");
            focus.ToggleQuickFocus();

            Click(window, shell);
            Check(Read<bool>(window, "expanded") && !focus.IsRunning, "blank shell click still expands and cannot replay a stale focus action");
            window.SetExpanded(false, false); StopTimers(window);
        }

        private static void AwakeClicks(IslandWindow window)
        {
            Configure(window, "bottom", "quota", "awake", "focus");
            var awake = Read<KeepAwakePanel>(window, "keepAwakePanel");
            Check(!awake.IsRunning && awake.QuickMinutes == 30, "demo wake component starts idle with its 30-minute quick duration");
            Click(window, Slot(window, false)); Check(awake.IsRunning, "demo wake component starts on release"); Compact(window, "wake start");
            Click(window, Slot(window, false)); Check(!awake.IsRunning, "demo wake component stops on its next release"); Compact(window, "wake stop");
        }

        private static void DestinationActions(IslandWindow window)
        {
            foreach (string key in new[] { "codex-primary", "codex-secondary", "cpu", "memory", "battery", "clock", "notifications" })
            {
                Configure(window, "left", "quota", key, "focus");
                Click(window, Slot(window, false));
                Check(Read<bool>(window, "expanded"), key + " click opens its details");
                if (key == "notifications") Check(Read<bool>(window, "notificationPageSelected") && !Read<bool>(window, "toolsPageSelected"), "unread component opens the message page");
                else if (key == "cpu" || key == "memory" || key == "battery" || key == "clock")
                    Check(Read<bool>(window, "toolsPageSelected") && Read<string>(window, "selectedTool") == (key == "clock" ? "time" : "system"), key + " opens the associated visible tool");
                else Check(Read<StackPanel>(window, "statusPage").Visibility == Visibility.Visible && !Read<bool>(window, "toolsPageSelected"), key + " opens quota status");
            }
            var settings = Read<Settings>(window, "settings"); settings.HiddenTools = "system,time"; Call(window, "ConfigureToolkit");
            foreach (string key in new[] { "cpu", "memory", "battery", "clock" })
            {
                Configure(window, "right", "quota", key, "focus"); Click(window, Slot(window, false));
                Check(Read<bool>(window, "expanded") && Read<StackPanel>(window, "statusPage").Visibility == Visibility.Visible,
                    key + " falls back to status when its target module is hidden");
                Check(settings.HiddenTools == "system,time", "quick details respect the hidden-module preference");
            }
            settings.HiddenTools = ""; Call(window, "ConfigureToolkit");
            Configure(window, "bottom", "clock", "focus", "awake"); Click(window, Read<StackPanel>(window, "compactAlternate"));
            Check(Read<bool>(window, "expanded") && Read<string>(window, "selectedTool") == "time", "legacy large clock opens the time tool");
            window.SetExpanded(false, false); StopTimers(window);
        }

        private static void ComponentHoverDoesNotStealClicks(IslandWindow window)
        {
            MethodInfo changeOver = typeof(MouseDevice).GetMethod("ChangeMouseOver", Private, null, new[] { typeof(IInputElement), typeof(int) }, null);
            Check(changeOver != null, "own-process WPF hover fixture is available");
            IInputElement prior = Mouse.DirectlyOver;
            try
            {
                foreach (string mode in new[] { "quota", "focus", "clock" })
                {
                    Configure(window, "right", mode, "focus", "awake");
                    foreach (UIElement component in mode == "quota" ? new UIElement[] { Slot(window, false), Slot(window, true) } : new UIElement[] { Read<StackPanel>(window, "compactAlternate") })
                    {
                        changeOver.Invoke(Mouse.PrimaryDevice, new object[] { null, Environment.TickCount });
                        Write(window, "hoverSuppressed", false);
                        changeOver.Invoke(Mouse.PrimaryDevice, new object[] { component, Environment.TickCount });
                        Call(window, "RecheckCompactActionHover");
                        Check((bool)Call(window, "IsCompactActionPointerOver"), mode + " component is recognized by the live hover guard");
                        Check(!Read<DispatcherTimer>(window, "hoverTimer").IsEnabled, mode + " component does not arm delayed expansion");
                        Call(window, "OnHoverElapsed"); Compact(window, mode + " stale hover callback");
                    }
                    // This assertion covers released-button hover. A real
                    // desktop click may overlap the long-running suite; wait
                    // for that input to finish, without generating any input.
                    var released = Stopwatch.StartNew();
                    while (Mouse.LeftButton == MouseButtonState.Pressed && released.ElapsedMilliseconds < 2000) { Pump(10); StopTimers(window); }
                    Check(Mouse.LeftButton == MouseButtonState.Released, "released-hover fixture is blocked by a held mouse button; " + DescribeHover(window));
                    changeOver.Invoke(Mouse.PrimaryDevice, new object[] { null, Environment.TickCount });
                    changeOver.Invoke(Mouse.PrimaryDevice, new object[] { (IInputElement)Read<TextBlock>(window, "compactNotificationIcon"), Environment.TickCount });
                    Write(window, "hoverSuppressed", false); Call(window, "RecheckCompactActionHover");
                    Check(!(bool)Call(window, "IsCompactActionPointerOver") && Read<DispatcherTimer>(window, "hoverTimer").IsEnabled,
                        mode + " moving to the brand re-enables hover expansion without leaving the island");
                    string beforeHover = DescribeHover(window);
                    Call(window, "OnHoverElapsed"); Check(Read<bool>(window, "expanded"), mode + " brand hover still expands; before=" + beforeHover + "; after=" + DescribeHover(window));
                    if (Environment.GetEnvironmentVariable("CODEX_ISLAND_HOVER_TRACE") == "1") Console.WriteLine("HOVER GUARD " + mode + ": " + beforeHover + "; after=" + DescribeHover(window));
                    window.SetExpanded(false, false); StopTimers(window);
                }
            }
            finally { changeOver.Invoke(Mouse.PrimaryDevice, new object[] { prior, Environment.TickCount }); StopTimers(window); }
        }

        private static string DescribeHover(IslandWindow window)
        {
            var source = PresentationSource.FromVisual(window) as HwndSource;
            var over = Mouse.DirectlyOver as FrameworkElement;
            var capture = Mouse.Captured as FrameworkElement;
            var focused = Keyboard.FocusedElement as FrameworkElement;
            return "enabled=" + (source == null ? "no-source" : IsWindowEnabled(source.Handle).ToString()) +
                ", visible=" + window.IsVisible + ", active=" + window.IsActive + ", over=" + window.IsMouseOver +
                ", directly=" + (over == null ? "null" : over.GetType().Name + "/" + over.Name) +
                ", actionOver=" + Call(window, "IsCompactActionPointerOver") + ", left=" + Mouse.LeftButton +
                ", queueLeft=" + GetKeyState(1).ToString("X4") + ", asyncLeft=" + GetAsyncKeyState(1).ToString("X4") +
                ", expanded=" + Read<bool>(window, "expanded") + ", peeking=" + Read<bool>(window, "peeking") +
                ", dragging=" + Read<bool>(window, "dragging") + ", resizing=" + Read<bool>(window, "resizing") +
                ", suppressed=" + Read<bool>(window, "hoverSuppressed") + ", hoverEnabled=" + Read<Settings>(window, "settings").HoverExpandEnabled +
                ", guard=" + Call(window, "HasInteractionGuard") + ", popup=" + Call(window, "HasPopupInteraction") +
                ", dialog=" + Read<bool>(window, "dialogOpen") + ", owned=" + window.OwnedWindows.Count +
                ", capture=" + (capture == null ? "null" : capture.GetType().Name + "/" + capture.Name) +
                ", focused=" + (focused == null ? "null" : focused.GetType().Name + "/" + focused.Name) +
                ", visualTransition=" + Read<bool>(window, "visualTransitionActive") +
                ", pageTransition=" + Read<bool>(window, "pageTransitionActive");
        }

        private static void Configure(IslandWindow window, string edge, string mode, string first, string second)
        {
            var settings = Read<Settings>(window, "settings");
            settings.Side = edge; settings.CompactMode = mode; settings.CompactSlot1 = first; settings.CompactSlot2 = second;
            settings.FreePosition = false; settings.HorizontalPosition = .5; settings.VerticalPosition = .5;
            Write(window, "pinned", false); Write(window, "hoverSuppressed", false);
            window.SetExpanded(false, false); Call(window, "UpdateToolkitCompact"); window.UpdateLayout(); StopTimers(window);
        }

        private static Grid Slot(IslandWindow window, bool second) { return Read<Grid>(window, second ? "compactSlot2" : "compactSlot1"); }
        private static void Click(IslandWindow window, UIElement target) { Press(window, target); Release(window, Read<Border>(window, "shell")); }
        private static Point Press(IslandWindow window, UIElement target)
        {
            InitializeMouseSource(window); StopTimers(window);
            // The button-specific event is direct. Real input first bubbles
            // MouseDown; WPF raises MouseLeftButtonDown at each route element.
            target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) {
                RoutedEvent = Mouse.MouseDownEvent, Source = target
            });
            Check(Read<bool>(window, "dragging") && ReferenceEquals(Mouse.Captured, Read<Border>(window, "shell")),
                "own component press captures the persistent shell (target=" + target.GetType().Name +
                ", visible=" + target.IsVisible + ", enabled=" + target.IsEnabled + ", expanded=" + Read<bool>(window, "expanded") +
                ", dragging=" + Read<bool>(window, "dragging") + ", captured=" + (Mouse.Captured == null ? "null" : Mouse.Captured.GetType().Name) + ")");
            Point point = target.PointToScreen(new Point(Math.Max(1, target.RenderSize.Width * .5), Math.Max(1, target.RenderSize.Height * .5)));
            Write(window, "motionStart", point);
            return point;
        }
        private static void Release(IslandWindow window, UIElement target)
        {
            target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) {
                RoutedEvent = Mouse.MouseUpEvent, Source = target
            });
            StopTimers(window);
        }
        private static void InitializeMouseSource(IslandWindow window)
        {
            var source = PresentationSource.FromVisual(window) as HwndSource;
            Check(source != null && !source.IsDisposed && GetCapture() == IntPtr.Zero && Mouse.Captured == null, "fixture starts with an own HWND and no mouse capture");
            SetCapture(source.Handle);
            try { SendMessage(source.Handle, 0x0200, IntPtr.Zero, new IntPtr(40 | (40 << 16))); }
            finally { if (GetCapture() == source.Handle) ReleaseCapture(); }
            Check(ReferenceEquals(Mouse.PrimaryDevice.ActiveSource, source), "mouse reports target only the own test window");
        }
        private static Vector ToDevice(IslandWindow window, Vector offset)
        {
            var source = PresentationSource.FromVisual(window);
            return source == null ? offset : source.CompositionTarget.TransformToDevice.Transform(offset);
        }
        private static Point Offset(IslandWindow window, Point point, Vector offset) { return point + ToDevice(window, offset); }
        private static void Compact(IslandWindow window, string label)
        {
            Check(!Read<bool>(window, "expanded") && !Read<bool>(window, "peeking") && !Read<bool>(window, "visualTransitionActive"), label + " remains fully compact");
            Check(Read<StackPanel>(window, "compact").Visibility == Visibility.Visible && Read<ScrollViewer>(window, "detailsScroll").Visibility == Visibility.Collapsed,
                label + " keeps expanded content hidden");
        }
        private static void ReleaseOwnCapture(IslandWindow window)
        {
            var captured = Mouse.Captured as DependencyObject;
            if (captured != null && Window.GetWindow(captured) == window) Mouse.Capture(null);
        }
        private static void StopTimers(IslandWindow window)
        {
            foreach (FieldInfo field in typeof(IslandWindow).GetFields(Private))
                if (field.FieldType == typeof(DispatcherTimer)) ((DispatcherTimer)field.GetValue(window)).Stop();
            var focus = Read<FocusPanel>(window, "focusPanel"); if (focus != null) focus.Stop();
        }
        private static void Pump(int milliseconds)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
            timer.Tick += (s, e) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
        }
        private static T Read<T>(object value, string name) { return (T)value.GetType().GetField(name, Private).GetValue(value); }
        private static void Write(object value, string name, object fieldValue) { value.GetType().GetField(name, Private).SetValue(value, fieldValue); }
        private static object Call(object value, string name, params object[] arguments)
        {
            var method = value.GetType().GetMethod(name, Private);
            if (method == null) throw new Exception("Missing compact quick-action method: " + name);
            try { return method.Invoke(value, arguments); }
            catch (TargetInvocationException error) { throw error.InnerException ?? error; }
        }
        private static void Check(bool value, string message) { if (!value) throw new Exception("Compact quick actions: " + message); }
    }
}
