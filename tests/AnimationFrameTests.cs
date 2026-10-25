using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CodexIsland
{
    // Exercises real animation-clock frames, native HWND sizes and layout work.
    // Demo data and this fixture's windows only; no global input or screen capture.
    internal static class AnimationFrameTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);
        [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int maximum);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll")] private static extern IntPtr GetThreadDpiAwarenessContext();
        [DllImport("user32.dll")] private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out NativeRect value, int size);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr GetCapture();
        [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr window);
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        private sealed class LayoutProbe : FrameworkElement
        {
            public int Measures, Arranges;
            protected override Size MeasureOverride(Size available) { Measures++; return new Size(0, 0); }
            protected override Size ArrangeOverride(Size final) { Arranges++; return final; }
        }
        private sealed class Frame
        {
            public Rect Visual, Native, Drawn;
            public bool Active;
            public double ContentWidth, ContentOpacity, OutgoingOpacity, ElapsedMilliseconds, DriverMilliseconds;
        }

        public static void Run(string renderDirectory = null)
        {
            string previous = Environment.GetEnvironmentVariable("CODEX_ISLAND_DATA_DIR");
            string directory = Path.Combine(Path.GetTempPath(), "CodexIsland.AnimationFrames." + Guid.NewGuid().ToString("N"));
            IslandWindow window = null;
            try
            {
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", directory);
                new SettingsStore().Save(new Settings { AlwaysOnTop = false, AutoCollapse = false, NotificationsEnabled = false,
                    CustomExpandedSize = true, ExpandedWidth = 440, ExpandedHeight = 600 });
                window = new IslandWindow(true) { ShowActivated = false, IsHitTestVisible = false };
                window.Loaded += (s, e) => StopTimers(window);
                window.Show(); Pump(30); StopTimers(window);
                var probe = new LayoutProbe(); Read<StackPanel>(window, "details").Children.Add(probe);
                foreach (string side in new[] { "left", "right", "top", "bottom" })
                {
                    SetSide(window, side);
                    Rect compact = VisualBounds(window);
                    TransitionFrames(window, probe, side, true);
                    Rect expanded = VisualBounds(window);
                    TransitionFrames(window, probe, side, false);
                    Same(VisualBounds(window), compact, side + " final compact anchor");
                    RapidReversal(window, side, compact, expanded);
                }
                ImmediateCancellation(window);
                HideCleanup(window);
                RefreshPreservesTransition(window);
                HoverReentryReversesSmoothly(window);
                CrossfadeContent(window);
                SnapshotPrivacy(window);
                if (renderDirectory != null) RenderFrames(window, renderDirectory);
                TransparentNativeHit(window);
                window.SetExpanded(false, false); window.SetExpanded(true, true); Pump(20); window.Close();
                Check(!Active(window), "closing clears the active animation immediately");
                NoSnapshot(window, "closing releases the previous content bitmap");
                Pump(IslandWindow.TransitionMilliseconds + 30);
                Check(!Active(window), "closed window cannot resume an old animation");
                Console.WriteLine("PASS: actual animated visual bounds are monotonic, HWND and text layout remain fixed between frames, four-edge anchors and interrupted animations recover.");
            }
            finally
            {
                if (window != null && !Read<bool>(window, "shuttingDown")) { Keyboard.ClearFocus(); window.Close(); }
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", previous);
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        private static void TransitionFrames(IslandWindow window, LayoutProbe probe, string side, bool expand)
        {
            Rect from = VisualBounds(window);
            var source = (HwndSource)PresentationSource.FromVisual(window);
            int nativeSizes = 0;
            HwndSourceHook hook = (IntPtr h, int message, IntPtr w, IntPtr l, ref bool handled) => { if (message == 0x0005) nativeSizes++; return IntPtr.Zero; };
            source.AddHook(hook);
            var frames = new List<Frame>();
            var elapsed = Stopwatch.StartNew();
            EventHandler onFrame = (s, e) => {
                var driver = Read<Stopwatch>(window, "transitionWatch");
                frames.Add(new Frame { Visual = VisualBounds(window), Native = NativeBounds(window), Drawn = DrawnBounds(window), Active = Active(window),
                    ContentWidth = Read<ScrollViewer>(window, "detailsScroll").ActualWidth, ContentOpacity = Read<Border>(window, "shell").Opacity,
                    OutgoingOpacity = Read<Image>(window, "outgoingImage").Opacity, ElapsedMilliseconds = elapsed.Elapsed.TotalMilliseconds,
                    DriverMilliseconds = driver == null ? -1 : driver.Elapsed.TotalMilliseconds });
            };
            try
            {
                CompositionTarget.Rendering += onFrame;
                window.SetExpanded(expand, true);
                // Retain setup/reentrant-frame coverage, then observe behind
                // the newly registered rendering driver during stable motion.
                CompositionTarget.Rendering -= onFrame; CompositionTarget.Rendering += onFrame;
                Check(!DependencyPropertyHelper.GetValueSource(window, FrameworkElement.WidthProperty).IsAnimated &&
                    !DependencyPropertyHelper.GetValueSource(window, FrameworkElement.HeightProperty).IsAnimated,
                    side + " must not animate native Width/Height");
                bool animated = Active(window);
                if (animated) Same(VisualBounds(window), from, side + " first frame must not flash the target bounds");
                window.UpdateLayout();
                probe.Measures = probe.Arranges = 0;
                Pump(IslandWindow.TransitionMilliseconds + 100);
                CompositionTarget.Rendering -= onFrame;
                window.UpdateLayout(); StopTimers(window);
                Rect target = VisualBounds(window);
                Check(!Active(window), side + " animation reaches stable state");
                Same(target, Bounds(window), side + " final HWND matches visual bounds");
                Check(nativeSizes <= 2, side + " transition resizes native HWND at most at start/end, observed " + nativeSizes);
                if (animated)
                {
                    Check(frames.Any(f => !NearRect(f.Visual, from) && !NearRect(f.Visual, target)), side + " has genuine intermediate rendered visual bounds");
                    Rect previous = from;
                    Frame firstActive = frames.First(f => f.Active);
                    double previousProgress = 0, previousTime = 0;
                    foreach (Frame frame in frames)
                    {
                        Same(frame.Drawn, frame.Visual, side + " painted rectangle follows actual native origin, including completion frames");
                        Check(expand ? frame.Drawn.Width + .75 >= previous.Width && frame.Drawn.Height + .75 >= previous.Height
                            : frame.Drawn.Width <= previous.Width + .75 && frame.Drawn.Height <= previous.Height + .75,
                            side + " visual animation cannot reverse direction between frames");
                        Check(Math.Abs(Edge(frame.Drawn, side) - Edge(target, side)) <= 1,
                            side + " docked visual edge remains fixed throughout animation");
                        if (frame.Active && !NearRect(frame.Drawn, target)) Same(frame.Native, firstActive.Native, side + " native union HWND is constant between intermediate frames");
                        if (!frame.Active) Same(frame.Drawn, target, side + " completed frame never flashes a stale host size or position");
                        if (expand && frame.ContentOpacity > .001) Check(Math.Abs(frame.ContentWidth - Read<ScrollViewer>(window, "detailsScroll").ActualWidth) <= .5, side + " expanded text is laid out once at target width");
                        double progress = Math.Abs(target.Width - from.Width) > 1 ? (frame.Drawn.Width - from.Width) / (target.Width - from.Width)
                            : (frame.Drawn.Height - from.Height) / (target.Height - from.Height);
                        double span = Math.Max(0, frame.ElapsedMilliseconds - previousTime);
                        Check(progress - previousProgress <= 1.9 * span / IslandWindow.TransitionMilliseconds + .06,
                            side + " visible displacement must follow elapsed time, not jump through most of the animation in a few milliseconds");
                        if (frame.Active && frame.DriverMilliseconds >= 0)
                        {
                            double normalized = Math.Min(1, frame.DriverMilliseconds / IslandWindow.TransitionMilliseconds);
                            double expected = (1 - Math.Cos(Math.PI * normalized)) / 2;
                            Check(Math.Abs(progress - expected) <= .04, side + " actual geometry follows the shared ease-in/out clock");
                            Check(Math.Abs(frame.ContentOpacity - progress) <= .01 && Math.Abs(frame.OutgoingOpacity + progress - 1) <= .01,
                                side + " incoming/outgoing opacity stays synchronized with the actual outline");
                        }
                        previousProgress = progress; previousTime = frame.ElapsedMilliseconds;
                        previous = frame.Drawn;
                    }
                    Check(frames.First(frame => !frame.Active).ElapsedMilliseconds >= IslandWindow.TransitionMilliseconds * .8,
                        side + " animation duration cannot collapse into a few dispatcher ticks");
                    Check(probe.Measures <= 1 && probe.Arranges <= 1,
                        side + " text does not repeatedly measure/arrange during animation: " + probe.Measures + "/" + probe.Arranges);
                }
                Console.WriteLine("Animation frame fixture: " + side + " " + (expand ? "expand" : "collapse") +
                    ", intermediate frames=" + frames.Count(f => f.Active) + ", native sizes=" + nativeSizes + ", text measure/arrange=" + probe.Measures + "/" + probe.Arranges);
            }
            finally { CompositionTarget.Rendering -= onFrame; source.RemoveHook(hook); }
        }

        private static void RapidReversal(IslandWindow window, string side, Rect compact, Rect expanded)
        {
            for (int i = 0; i < 4; i++)
            {
                window.SetExpanded(true, true); Pump(24);
                Rect before = VisualBounds(window); bool running = Active(window);
                window.SetExpanded(false, true);
                if (running && Active(window)) Same(VisualBounds(window), before, side + " collapse reversal starts at current visual frame");
                Pump(24); before = VisualBounds(window); running = Active(window);
                window.SetExpanded(true, true);
                if (running && Active(window)) Same(VisualBounds(window), before, side + " expand reversal starts at current visual frame");
                Pump(18);
                window.SetExpanded(false, false); Pump(IslandWindow.TransitionMilliseconds + 40); StopTimers(window);
                Same(VisualBounds(window), compact, side + " explicit click cancels every old completion");
                Check(!Active(window), side + " immediate transition has no active animation");
            }
            window.SetExpanded(true, true); Pump(24); window.SetExpanded(false, true); Pump(20);
            Call(window, "ShowPeek", new IslandNotification { Id = "animation", AppId = "fixture", AppName = "动画测试", Title = "隔离通知", Body = "测试预览打断收起", IsDemo = true });
            Pump(IslandWindow.TransitionMilliseconds + 40); StopTimers(window);
            Check(Read<bool>(window, "peeking") && !Active(window), side + " notification preview completes when interrupting collapse");
            window.SetExpanded(true, true); Pump(IslandWindow.TransitionMilliseconds + 40); StopTimers(window);
            Same(VisualBounds(window), expanded, side + " preview to expansion restores expected final size");
            window.SetExpanded(false, false);
        }

        private static void ImmediateCancellation(IslandWindow window)
        {
            SetSide(window, "right"); window.SetExpanded(true, false); window.UpdateLayout();
            Rect expanded = Bounds(window);
            window.SetExpanded(false, true); Pump(24); Call(window, "CollapseNow");
            Check(!Read<bool>(window, "expanded") && Read<bool>(window, "hoverSuppressed"), "explicit collapse changes the mode immediately and suppresses hover reopen");
            if (SystemParameters.ClientAreaAnimation) Check(Active(window), "explicit collapse retains smooth visual motion");
            window.SetExpanded(true, true); Pump(20);
            var grip = Read<Grid>(window, "resizeLayer").Children.OfType<Thumb>().First();
            grip.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            Check(Read<bool>(window, "resizing") && !Active(window), "resize start settles the animation before capturing bounds");
            Same(Read<Rect>(window, "motionBounds"), expanded, "resize starts at final expanded geometry");
            grip.RaiseEvent(new DragCompletedEventArgs(0, 0, true) { RoutedEvent = Thumb.DragCompletedEvent });
            StopTimers(window);
            window.SetExpanded(false, false); window.SetExpanded(true, true); Pump(20); window.SetExpanded(false, false);
            Rect compact = Bounds(window); Pump(IslandWindow.TransitionMilliseconds + 40);
            Same(Bounds(window), compact, "immediate click cannot be overwritten by previous completion");
            window.IsHitTestVisible = true;
            var source = (HwndSource)PresentationSource.FromVisual(window);
            Check(GetCapture() == IntPtr.Zero && Mouse.Captured == null, "no unrelated capture exists before own-window drag");
            window.SetExpanded(true, false); Call(window, "CollapseNow"); Pump(65);
            SetCapture(source.Handle);
            try { SendMessage(source.Handle, 0x0200, IntPtr.Zero, new IntPtr(40 | (40 << 16))); }
            finally { if (GetCapture() == source.Handle) ReleaseCapture(); }
            var shell = Read<Border>(window, "shell");
            MouseEventHandler syntheticMove = (s, e) => { if (Mouse.LeftButton != MouseButtonState.Pressed) e.Handled = true; };
            shell.PreviewMouseMove += syntheticMove;
            try
            {
                Rect beforePress = VisualBounds(window);
                Check(!Read<bool>(window, "expanded") && !NearRect(beforePress, expanded) && !NearRect(beforePress, compact),
                    "click reversal fixture starts from a genuinely intermediate collapse frame");
                shell.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) {
                    RoutedEvent = UIElement.MouseLeftButtonDownEvent, Source = shell });
                Same(VisualBounds(window), beforePress, "mouse press during collapse cannot jump to its compact endpoint");
                if (SystemParameters.ClientAreaAnimation) Check(Active(window), "a press that may become a reversing click leaves the animation running");
                Check(Read<bool>(window, "dragging") && ReferenceEquals(Mouse.Captured, shell), "press captures own shell while preserving visible motion");
                shell.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) {
                    RoutedEvent = UIElement.MouseLeftButtonUpEvent, Source = shell });
                Check(Read<bool>(window, "expanded"), "release without a drag immediately requests reverse expansion");
                Same(VisualBounds(window), beforePress, "reversing click begins at the pre-press visual frame");
            }
            finally
            {
                shell.PreviewMouseMove -= syntheticMove;
                if (ReferenceEquals(Mouse.Captured, shell)) shell.ReleaseMouseCapture();
                window.IsHitTestVisible = false; StopTimers(window);
            }
            window.SetExpanded(false, false);
        }

        private static void RefreshPreservesTransition(IslandWindow window)
        {
            if (!SystemParameters.ClientAreaAnimation) return;
            SetSide(window, "right"); window.SetExpanded(true, true); Pump(55);
            foreach (string refresh in new[] { "RebuildBuckets", "SelectPage", "SelectToolsPage" })
            {
                Check(Active(window), "same-target refresh fixture is still revealing");
                Rect before = VisualBounds(window);
                object clock = Read<object>(window, "transitionWatch");
                int version = Read<int>(window, "transitionVersion");
                if (refresh == "SelectPage") Call(window, refresh, false); else Call(window, refresh);
                Check(Active(window) && Read<int>(window, "transitionVersion") == version && ReferenceEquals(clock, Read<object>(window, "transitionWatch")),
                    refresh + " keeps the active transition and its original elapsed clock");
                Same(VisualBounds(window), before, refresh + " cannot snap the outline to its endpoint");
            }
            window.SetExpanded(false, false); Call(window, "SelectPage", true);
            window.SetExpanded(true, true); Pump(55);
            double opacity = Read<Border>(window, "shell").Opacity;
            ImageSource outgoing = Read<Image>(window, "outgoingImage").Source;
            Check(outgoing != null && opacity > 0 && opacity < .9, "ordinary-poll fixture has a genuinely mixed content frame");
            Call(window, "UpdateNotificationListCore", false);
            Check(ReferenceEquals(outgoing, Read<Image>(window, "outgoingImage").Source) && Math.Abs(opacity - Read<Border>(window, "shell").Opacity) < .001,
                "ordinary notification poll preserves the current content blend instead of jumping opacity to one");
            Pump(IslandWindow.TransitionMilliseconds + 40); StopTimers(window);
            Check(!Active(window), "same-target refreshes do not extend the transition indefinitely");
            Call(window, "SelectPage", true); window.SetExpanded(false, false);
            Console.WriteLine("PASS: quota/page/tool refresh and ordinary notification polls preserve the active transition and crossfade.");
        }

        private static void HoverReentryReversesSmoothly(IslandWindow window)
        {
            if (!SystemParameters.ClientAreaAnimation) return;
            window.SetExpanded(true, false); window.SetExpanded(false, true); Pump(55);
            Rect before = VisualBounds(window);
            Call(window, "OnIslandMouseEnter");
            Check(Read<bool>(window, "expanded") && Active(window), "hover reentry during automatic collapse reverses immediately without waiting for a new hover delay");
            Same(VisualBounds(window), before, "hover reversal begins from the current shrinking frame");
            Call(window, "CollapseNow"); before = VisualBounds(window);
            Call(window, "OnIslandMouseEnter");
            Check(!Read<bool>(window, "expanded") && Read<bool>(window, "hoverSuppressed"), "explicit minus/Escape collapse does not automatically reverse under the same pointer");
            Same(VisualBounds(window), before, "suppressed hover does not jump or restart explicit collapse");
            Check(!Read<DispatcherTimer>(window, "hoverTimer").IsEnabled, "suppressed hover does not schedule a later unwanted expansion");
            window.SetExpanded(false, false); StopTimers(window);
        }

        private static void HideCleanup(IslandWindow window)
        {
            window.SetExpanded(true, true); Pump(20); window.Hide();
            Check(!Active(window), "hiding clears the transition immediately");
            NoSnapshot(window, "hiding releases the previous content bitmap");
            window.Show(); StopTimers(window); window.SetExpanded(false, false); window.UpdateLayout();
            Check(Read<Border>(window, "shell").Clip == null, "stable shell has no stale animated clip after hide/show");
            Check(Double.IsNaN(Read<Border>(window, "shell").Width), "stable shell resumes normal automatic layout");
        }

        private static void CrossfadeContent(IslandWindow window)
        {
            if (!SystemParameters.ClientAreaAnimation) return;
            var outgoing = Read<Image>(window, "outgoingImage");
            var layer = Read<Grid>(window, "contentLayer");
            int childCount = layer.Children.Count;
            Check(!outgoing.IsHitTestVisible, "outgoing content cannot intercept clicks");
            foreach (string side in new[] { "left", "right", "top", "bottom" })
            {
                SetSide(window, side);
                foreach (bool expand in new[] { true, false })
                {
                    window.UpdateLayout();
                    int before = BrightPixels(window);
                    Check(before > 100, side + " initial content fixture contains visible text");
                    window.SetExpanded(expand, true); window.UpdateLayout();
                    Check(outgoing.Source != null && outgoing.Opacity > .1 && outgoing.IsVisible,
                        side + " mode change retains visible outgoing content on its first frame");
                    int first = BrightPixels(window);
                    Check(first >= before * .6, side + " first frame retains rendered content instead of flashing an empty shell: " + before + " -> " + first);
                    Check(outgoing.RenderTransform.Value.M11 == 1 && outgoing.RenderTransform.Value.M22 == 1,
                        side + " outgoing text has no scale transform");
                    Pump(IslandWindow.TransitionMilliseconds + 35); StopTimers(window);
                    NoSnapshot(window, side + " completed transition releases content snapshot");
                }
                window.SetExpanded(true, true); Pump(25);
                for (int turn = 0; turn < 4; turn++)
                {
                    ImageSource before = outgoing.Source;
                    int shown = BrightPixels(window);
                    window.SetExpanded(turn % 2 == 1, true); window.UpdateLayout();
                    Check(outgoing.Source != null && !ReferenceEquals(outgoing.Source, before), side + " reversal snapshots the current composite once");
                    int retained = BrightPixels(window);
                    Check(retained >= shown * .6, side + " reversal retains the previously visible mixture (turn " + turn + ": " + shown + " -> " + retained + ")");
                    Check(layer.Children.Count == childCount, side + " reversals do not accumulate outgoing visual layers");
                    Pump(24);
                }
                window.SetExpanded(false, false);
                NoSnapshot(window, side + " immediate click clears both active and pending snapshots");
                Pump(IslandWindow.TransitionMilliseconds + 25);
                NoSnapshot(window, side + " old completion cannot reattach a snapshot");
            }
            Console.WriteLine("PASS: four-edge first rendered frames retain text, reversals preserve current content without scaling, and snapshots are released after completion or interruption.");
        }

        private static void SnapshotPrivacy(IslandWindow window)
        {
            if (!SystemParameters.ClientAreaAnimation) return;
            var settings = Read<Settings>(window, "settings");
            var feed = Read<NotificationFeed>(window, "notificationFeed");
            var preferences = Read<NotificationPreferences>(window, "notificationPreferences");
            var original = feed.Items.ToList();
            bool previousEnabled = settings.NotificationsEnabled, previousPrivacy = settings.HideNotificationBody;
            var fixture = new IslandNotification { Id = "animation-privacy", AppId = "fixture.animation.privacy", AppName = "隔离测试",
                Title = "PRIVATE FIXTURE TITLE", Body = "PRIVATE FIXTURE BODY", CreatedAt = DateTime.UtcNow, IsDemo = true };
            string key = NotificationPreferences.SourceKey(fixture);
            Action prepare = () => {
                window.SetExpanded(false, false);
                settings.NotificationsEnabled = true; settings.HideNotificationBody = false;
                preferences.Observe(new[] { fixture }); preferences.SetBlocked(key, false); feed.SetSourceBlocked(key, false);
                feed.Clear(); feed.Merge(new[] { fixture }, false, false);
                Call(window, "UpdateNotificationList"); Call(window, "SelectPage", true);
                window.SetExpanded(true, false); window.UpdateLayout();
                window.SetExpanded(false, true);
                Check(Read<Image>(window, "outgoingImage").Source != null, "privacy fixture starts with an outgoing notification snapshot");
            };
            try
            {
                prepare(); settings.HideNotificationBody = true; Call(window, "ConfigureNotifications");
                NoSnapshot(window, "privacy hotkey immediately removes old plaintext snapshot");
                prepare(); Call(window, "SetNotificationSourceBlocked", key, true);
                NoSnapshot(window, "blocking a notification source immediately removes its old snapshot");
                prepare();
                var clear = LogicalDescendants<Button>(Read<StackPanel>(window, "notificationPage")).Single(button => (string)button.Content == "清空岛内记录");
                clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(feed.Items.Count == 0, "actual clear button empties the synthetic notification feed");
                NoSnapshot(window, "clearing notification history immediately removes its old snapshot");
                prepare(); settings.NotificationsEnabled = false; Call(window, "ConfigureNotifications");
                NoSnapshot(window, "disabling notifications immediately removes their old snapshot");
                Pump(IslandWindow.TransitionMilliseconds + 35); StopTimers(window);
                NoSnapshot(window, "old completion cannot restore a discarded private snapshot");
            }
            finally
            {
                window.SetExpanded(false, false);
                preferences.SetBlocked(key, false); feed.SetSourceBlocked(key, false);
                settings.NotificationsEnabled = previousEnabled; settings.HideNotificationBody = previousPrivacy;
                feed.Clear(); feed.Merge(original, false, false); Call(window, "UpdateNotificationList"); StopTimers(window);
            }
            Console.WriteLine("PASS: privacy, blocked notification sources, cleared history and disabled notifications immediately discard outgoing image data.");
        }

        private static void NoSnapshot(IslandWindow window, string label)
        {
            var outgoing = Read<Image>(window, "outgoingImage");
            Check(outgoing.Source == null && Read<object>(window, "pendingTransitionSnapshot") == null, label);
            Check(!outgoing.HasAnimatedProperties, label + " and detaches its animation clocks");
        }

        private static int BrightPixels(IslandWindow window)
        {
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            byte[] pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            int count = 0;
            for (int i = 0; i < pixels.Length; i += 4)
                if (pixels[i + 3] > 64 && Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2])) > 85) count++;
            return count;
        }

        private static IEnumerable<T> LogicalDescendants<T>(DependencyObject root) where T : DependencyObject
        {
            foreach (object child in LogicalTreeHelper.GetChildren(root))
            {
                var dependency = child as DependencyObject;
                if (dependency == null) continue;
                var match = dependency as T; if (match != null) yield return match;
                foreach (var descendant in LogicalDescendants<T>(dependency)) yield return descendant;
            }
        }

        private static void TransparentNativeHit(IslandWindow window)
        {
            if (!SystemParameters.ClientAreaAnimation) return;
            SetSide(window, "left");
            var alphaSettings = Read<Settings>(window, "settings");
            bool previousFreePosition = alphaSettings.FreePosition;
            bool previousAlwaysOnTop = alphaSettings.AlwaysOnTop, priorTopmost = window.Topmost;
            double previousHorizontal = alphaSettings.HorizontalPosition, previousVertical = alphaSettings.VerticalPosition;
            // Exercise alpha within the work area, away from edge overlays.
            alphaSettings.FreePosition = true;
            // Both test-owned windows must use the same topmost band. Apply
            // the fixture preference as well as the WPF property so a queued
            // settings refresh cannot demote the island beneath its own probe.
            alphaSettings.AlwaysOnTop = true; Call(window, "ConfigureToolkit");
            window.SetExpanded(true, false); window.UpdateLayout();
            Rect target = Bounds(window); window.SetExpanded(false, false); window.UpdateLayout();
            var underneath = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                ShowActivated = false, ShowInTaskbar = false, Topmost = true, Background = Brushes.SlateGray,
                Left = target.Left, Top = target.Top, Width = target.Width, Height = target.Height };
            try
            {
                underneath.Show(); underneath.UpdateLayout(); Pump(40);
                window.Topmost = true; window.IsHitTestVisible = true;
                IntPtr underneathHandle = new WindowInteropHelper(underneath).Handle;
                IntPtr islandHandle = new WindowInteropHelper(window).Handle;
                Rect compact = Rect.Empty;
                Point baselineUnder = new Point(), baselineInside = new Point();
                IntPtr baselineUnderHit = IntPtr.Zero, baselineInsideHit = IntPtr.Zero;
                bool unobscured = false; int placements = 0;
                // Other applications can own higher-band overlays even above
                // ordinary topmost HWNDs. Relocate only our own fixtures until
                // the baseline and all future outside probe corners are clear.
                // The actual native inside/outside assertions below are kept.
                foreach (double y in new[] { .5, .1, .9, .3, .7 })
                {
                    foreach (double x in new[] { .5, .1, .9, .3, .7 })
                    {
                        placements++;
                        alphaSettings.HorizontalPosition = x; alphaSettings.VerticalPosition = y;
                        window.SetExpanded(true, false); window.UpdateLayout(); target = Bounds(window);
                        underneath.Left = target.Left; underneath.Top = target.Top; underneath.Width = target.Width; underneath.Height = target.Height;
                        window.SetExpanded(false, false); window.UpdateLayout(); compact = Bounds(window);
                        Check(SetWindowPos(underneathHandle, new IntPtr(-1), 0, 0, 0, 0, 0x0013), "underlying alpha fixture can be raised");
                        Check(SetWindowPos(islandHandle, new IntPtr(-1), 0, 0, 0, 0, 0x0013), "island alpha fixture can move above its underlying window");
                        Pump(75); StopTimers(window);
                        baselineUnder = underneath.PointToScreen(new Point(22, 22));
                        baselineInside = window.PointToScreen(new Point(compact.Width / 2, compact.Height / 2));
                        baselineUnderHit = WindowFromPoint(new NativePoint { X = (int)baselineUnder.X, Y = (int)baselineUnder.Y });
                        baselineInsideHit = WindowFromPoint(new NativePoint { X = (int)baselineInside.X, Y = (int)baselineInside.Y });
                        if (baselineUnderHit != underneathHandle || baselineInsideHit != islandHandle) continue;
                        var corners = new[] { new Point(22, 22), new Point(target.Width - 22, 22), new Point(22, target.Height - 22), new Point(target.Width - 22, target.Height - 22) };
                        unobscured = corners.All(point => {
                            Point screen = underneath.PointToScreen(point);
                            IntPtr hit = WindowFromPoint(new NativePoint { X = (int)screen.X, Y = (int)screen.Y });
                            return hit == underneathHandle || hit == islandHandle;
                        });
                        if (unobscured) break;
                    }
                    if (unobscured) break;
                }
                bool traceAlpha = Environment.GetEnvironmentVariable("CODEX_ISLAND_ALPHA_TRACE") == "1";
                Check(NativeContains(underneathHandle, baselineUnder) && NativeContains(islandHandle, baselineInside),
                    "alpha probe physical coordinates lie inside their intended fixture HWNDs");
                if (traceAlpha || baselineUnderHit != underneathHandle || baselineInsideHit != islandHandle)
                    Console.WriteLine("ALPHA BASELINE outside=" + baselineUnder + " inside=" + baselineInside + " island=" + AlphaDpiState(islandHandle) + " under=" + AlphaDpiState(underneathHandle));
                Check(unobscured && baselineUnderHit == underneathHandle && baselineInsideHit == islandHandle,
                    "native alpha fixture starts unobscured: outside=" + AlphaHitClass(baselineUnderHit, islandHandle, underneathHandle) +
                    ", inside=" + AlphaHitClass(baselineInsideHit, islandHandle, underneathHandle) +
                    ", island=" + AlphaWindowState(islandHandle) + ", underneath=" + AlphaWindowState(underneathHandle));
                Console.WriteLine("Native alpha fixture: unobscured placement found after " + placements + " candidate(s).");
                int tested = 0, renderingCallbacks = 0;
                bool outsidePassed = true, insidePassed = true;
                var alphaTrace = new List<string>();
                Rect previousDrawn = DrawnBounds(window);
                var sampleClock = Stopwatch.StartNew();
                EventHandler sample = (s, e) => {
                    if (!Active(window)) return;
                    Rect drawn = DrawnBounds(window), priorDrawn = previousDrawn; previousDrawn = drawn;
                    // Rendering is raised before WPF submits that frame. The
                    // first callback still sees the previous HWND bitmap's alpha
                    // map; subsequent callbacks can test the presented union.
                    if (++renderingCallbacks == 1) return;
                    Rect shown = VisualBounds(window), host = Bounds(window);
                    var candidates = new[] { new Point(host.Left + 22, host.Top + 22), new Point(host.Right - 22, host.Top + 22),
                        new Point(host.Left + 22, host.Bottom - 22), new Point(host.Right - 22, host.Bottom - 22) };
                    Rect avoid = shown; avoid.Inflate(16, 16);
                    Point outside = candidates.FirstOrDefault(point => !avoid.Contains(point));
                    if (outside == default(Point)) return;
                    Point nativeOutside = window.PointToScreen(new Point(outside.X - window.Left, outside.Y - window.Top));
                    // Compact center lies inside every expansion frame. The hit
                    // test therefore stays valid before the new frame is presented.
                    Point nativeInside = window.PointToScreen(new Point(compact.Left + compact.Width / 2 - window.Left, compact.Top + compact.Height / 2 - window.Top));
                    tested++;
                    IntPtr outsideHit = WindowFromPoint(new NativePoint { X = (int)nativeOutside.X, Y = (int)nativeOutside.Y });
                    IntPtr insideHit = WindowFromPoint(new NativePoint { X = (int)nativeInside.X, Y = (int)nativeInside.Y });
                    outsidePassed &= outsideHit == underneathHandle;
                    insidePassed &= insideHit == new WindowInteropHelper(window).Handle;
                    if (traceAlpha || outsideHit != underneathHandle || insideHit != new WindowInteropHelper(window).Handle)
                    {
                        string outsideClass = AlphaHitClass(outsideHit, new WindowInteropHelper(window).Handle, underneathHandle);
                        string insideClass = AlphaHitClass(insideHit, new WindowInteropHelper(window).Handle, underneathHandle);
                        NativeRect underNative; GetWindowRect(underneathHandle, out underNative);
                        double compositionTime = ((RenderingEventArgs)e).RenderingTime.TotalMilliseconds;
                        alphaTrace.Add("ALPHA TRACE callback=" + renderingCallbacks + " wallMs=" + sampleClock.Elapsed.TotalMilliseconds.ToString("F2") +
                            " compositionMs=" + compositionTime.ToString("F2") + " point=" + nativeOutside + " hit=" + outsideClass + " inside=" + insideClass +
                            " currentDrawn=" + drawn + " previousDrawn=" + priorDrawn + " native=" + NativeBounds(window) +
                            " underNative=" + underNative.Left + "," + underNative.Top + "," + underNative.Right + "," + underNative.Bottom +
                            " ownRenderedAlpha=" + (outsideHit != underneathHandle ? OwnAlphaAt(window, nativeOutside) : -2) +
                            " islandState=" + AlphaWindowState(new WindowInteropHelper(window).Handle) + " underState=" + AlphaWindowState(underneathHandle));
                    }
                };
                CompositionTarget.Rendering += sample;
                try { window.SetExpanded(true, true); Pump(IslandWindow.TransitionMilliseconds + 50); }
                finally { CompositionTarget.Rendering -= sample; }
                foreach (string diagnostic in alphaTrace) Console.WriteLine(diagnostic);
                Check(tested > 0, "native alpha probe samples a genuine intermediate animation frame");
                Check(outsidePassed, "native layered-window hit testing passes the transparent union area to the fixture window below");
                Check(insidePassed, "native hit testing still reaches the visible animated island");
                Console.WriteLine("PASS: native alpha hit testing reaches our underlying fixture through the transparent transition area without moving the pointer.");
            }
            finally
            {
                alphaSettings.FreePosition = previousFreePosition;
                alphaSettings.HorizontalPosition = previousHorizontal; alphaSettings.VerticalPosition = previousVertical;
                window.SetExpanded(false, false); alphaSettings.AlwaysOnTop = previousAlwaysOnTop; Call(window, "ConfigureToolkit"); window.Topmost = priorTopmost; window.IsHitTestVisible = false;
                underneath.Close(); StopTimers(window);
            }
        }

        private static string AlphaHitClass(IntPtr hit, IntPtr island, IntPtr under)
        {
            if (hit == island) return "island";
            if (hit == under) return "underneath";
            if (hit == IntPtr.Zero) return "none";
            IntPtr root = GetAncestor(hit, 2);
            if (root == island) return "island-child";
            if (root == under) return "underneath-child";
            uint process; GetWindowThreadProcessId(hit, out process);
            var className = new System.Text.StringBuilder(160); GetClassName(hit, className, className.Capacity);
            return (process == (uint)Process.GetCurrentProcess().Id ? "other-own-process:" : "other-process:") + className;
        }

        private static bool NativeContains(IntPtr handle, Point point)
        {
            NativeRect bounds;
            return GetWindowRect(handle, out bounds) && point.X >= bounds.Left && point.X < bounds.Right && point.Y >= bounds.Top && point.Y < bounds.Bottom;
        }

        private static string AlphaWindowState(IntPtr handle)
        {
            int preceding = 0; IntPtr above = GetWindow(handle, 3);
            while (above != IntPtr.Zero && preceding < 100) { preceding++; above = GetWindow(above, 3); }
            return "visible:" + IsWindowVisible(handle) + "/enabled:" + IsWindowEnabled(handle) +
                "/topmost:" + ((GetWindowLong(handle, -20) & 8) != 0) + "/preceding:" + preceding;
        }

        private static string AlphaDpiState(IntPtr handle)
        {
            NativeRect native, physical; GetWindowRect(handle, out native);
            int result = DwmGetWindowAttribute(handle, 9, out physical, Marshal.SizeOf(typeof(NativeRect)));
            IntPtr context = GetThreadDpiAwarenessContext();
            return "native:" + native.Left + "," + native.Top + "," + native.Right + "," + native.Bottom +
                "/dwm:" + physical.Left + "," + physical.Top + "," + physical.Right + "," + physical.Bottom + "/dwmResult:" + result +
                "/dpi:" + GetDpiForWindow(handle) + "/threadContext:" + context + "/awareness:" + GetAwarenessFromDpiAwarenessContext(context);
        }

        private static int OwnAlphaAt(IslandWindow window, Point nativePoint)
        {
            Point local = window.PointFromScreen(nativePoint);
            var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(window.ActualWidth)), Math.Max(1, (int)Math.Ceiling(window.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            int x = (int)Math.Floor(local.X), y = (int)Math.Floor(local.Y);
            if (x < 0 || y < 0 || x >= bitmap.PixelWidth || y >= bitmap.PixelHeight) return -1;
            byte[] pixel = new byte[4]; bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0); return pixel[3];
        }

        // Runs after performance assertions: rasterizing PNGs must not pollute frame timings.
        private static void RenderFrames(IslandWindow window, string directory)
        {
            Directory.CreateDirectory(directory);
            var timestamps = new List<string> { "file,elapsedBeforeCaptureMilliseconds" };
            foreach (string side in new[] { "left", "right", "top", "bottom" })
            {
                SetSide(window, side);
                foreach (bool expand in new[] { true, false })
                {
                    var captureClock = Stopwatch.StartNew();
                    window.SetExpanded(expand, true); window.UpdateLayout();
                    int[] moments = { 0, 45, 90, 135, 180, IslandWindow.TransitionMilliseconds + 40 };
                    for (int i = 0; i < moments.Length; i++)
                    {
                        int remaining = moments[i] - (int)captureClock.ElapsedMilliseconds;
                        if (remaining > 0) Pump(remaining);
                        string name = side + "-" + (expand ? "expand" : "collapse") + "-" + moments[i].ToString("000") + ".png";
                        timestamps.Add(name + "," + captureClock.ElapsedMilliseconds);
                        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(window);
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using (var stream = File.Create(Path.Combine(directory, name))) encoder.Save(stream);
                    }
                    Pump(IslandWindow.TransitionMilliseconds + 30); StopTimers(window);
                }
            }
            File.WriteAllLines(Path.Combine(directory, "capture-times.csv"), timestamps.ToArray());
        }

        private static void SetSide(IslandWindow window, string side)
        {
            var settings = Read<Settings>(window, "settings");
            settings.Side = side; settings.FreePosition = false; settings.AutoSnapToEdge = true;
            settings.HorizontalPosition = settings.VerticalPosition = .5;
            window.SetExpanded(false, false); window.UpdateLayout(); StopTimers(window);
        }
        private static bool Active(IslandWindow window) { return Read<bool>(window, "visualTransitionActive"); }
        private static Rect VisualBounds(IslandWindow window) { return (Rect)Call(window, "CurrentVisualBounds"); }
        private static Rect Bounds(IslandWindow window) { return new Rect(window.Left, window.Top, window.Width, window.Height); }
        private static Rect NativeBounds(IslandWindow window)
        {
            NativeRect value; Check(GetWindowRect(new WindowInteropHelper(window).Handle, out value), "own HWND has bounds");
            return new Rect(value.Left, value.Top, value.Right - value.Left, value.Bottom - value.Top);
        }
        private static Rect DrawnBounds(IslandWindow window)
        {
            var source = (HwndSource)PresentationSource.FromVisual(window);
            Rect native = NativeBounds(window);
            Point origin = source.CompositionTarget.TransformFromDevice.Transform(native.TopLeft);
            var backdrop = Read<FrameworkElement>(window, "islandBackdrop");
            Rect painted = (Rect)backdrop.GetType().GetProperty("VisualBounds", Private).GetValue(backdrop, null);
            painted = backdrop.TransformToAncestor(window).TransformBounds(painted);
            return new Rect(origin.X + painted.X - 8, origin.Y + painted.Y - 8, painted.Width + 16, painted.Height + 16);
        }
        private static double Edge(Rect r, string side) { return side == "left" ? r.Left : side == "right" ? r.Right : side == "top" ? r.Top : r.Bottom; }
        private static bool NearRect(Rect a, Rect b) { return Math.Abs(a.X - b.X) <= 1 && Math.Abs(a.Y - b.Y) <= 1 && Math.Abs(a.Width - b.Width) <= 1 && Math.Abs(a.Height - b.Height) <= 1; }
        private static void Same(Rect actual, Rect expected, string label) { Check(NearRect(actual, expected), label + ": actual=" + actual + ", expected=" + expected); }
        private static T Read<T>(object target, string name) { return (T)target.GetType().GetField(name, Private).GetValue(target); }
        private static object Call(object target, string name, params object[] values)
        {
            try { return target.GetType().GetMethod(name, Private).Invoke(target, values); }
            catch (TargetInvocationException error) { throw error.InnerException ?? error; }
        }
        private static void StopTimers(IslandWindow window)
        {
            foreach (FieldInfo field in typeof(IslandWindow).GetFields(Private))
                if (field.FieldType == typeof(DispatcherTimer)) ((DispatcherTimer)field.GetValue(window)).Stop();
        }
        private static void Pump(int milliseconds)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
            timer.Tick += (s, e) => { timer.Stop(); frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame);
        }
        private static void Check(bool value, string message) { if (!value) throw new Exception("Animation frames: " + message); }
    }
}
