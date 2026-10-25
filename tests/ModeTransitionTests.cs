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
using System.Windows.Threading;

namespace CodexIsland
{
    // Only this process's demo window and temporary settings are used. The test
    // invokes WPF's context-menu service without moving the user's cursor.
    internal static class ModeTransitionTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { internal int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out NativeRect bounds);

        public static void Run()
        {
            string previous = Environment.GetEnvironmentVariable("CODEX_ISLAND_DATA_DIR");
            string directory = Path.Combine(Path.GetTempPath(), "CodexIsland.ModeTransitions." + Guid.NewGuid().ToString("N"));
            IslandWindow window = null;
            try
            {
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", directory);
                new SettingsStore().Save(new Settings {
                    AlwaysOnTop = false, AutoCollapse = true, NotificationsEnabled = false,
                    CustomExpandedSize = true, ExpandedWidth = 388, ExpandedHeight = 520
                });
                window = new IslandWindow(true) { ShowActivated = false };
                window.Loaded += (s, e) => StopTimers(window);
                window.Show(); Pump(20); StopTimers(window);
                EmptyContextMenuDoesNotLatchGuard(window);
                PendingCollapseSurvivesSameModeRefresh(window);
                RealMenusReleaseInteraction(window);
                AppearanceDuringModeContinuity(window);
                RepeatedInterruptedModes(window);
                Console.WriteLine("PASS: empty and real menu lifecycle, pending collapse across refresh, 16 actual in-motion appearance continuity cases, and 40 repeated four-edge mode transitions.");
            }
            finally
            {
                if (window != null) { Keyboard.ClearFocus(); window.Close(); }
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", previous);
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        private static void EmptyContextMenuDoesNotLatchGuard(IslandWindow window)
        {
            window.SetExpanded(true, false); window.UpdateLayout(); StopTimers(window);
            var layer = Read<Grid>(window, "resizeLayer");
            var grip = (Thumb)layer.Children[0];
            Check(grip.ContextMenu == null, "resize edge does not have a context menu");
            OpenContextMenu(grip); Pump(20); StopTimers(window);
            Check(!(bool)Call(window, "HasPopupInteraction"), "right-click on a menu-less resize edge cannot leave a permanent mode-switch guard");
        }

        private static void PendingCollapseSurvivesSameModeRefresh(IslandWindow window)
        {
            window.SetExpanded(true, false); window.UpdateLayout(); StopTimers(window);
            // Ignore physical cursor placement for this scheduler regression.
            // This changes only the test window, without sending global input.
            window.IsHitTestVisible = false; Pump(20); StopTimers(window);
            try
            {
                Check(!window.IsMouseOver, "test window does not depend on the user's cursor position");
                var collapse = Read<DispatcherTimer>(window, "collapseTimer");
                Call(window, "OnIslandMouseLeave");
                Check(collapse.IsEnabled, "leaving expanded island schedules collapse");
                // A quota response runs RebuildBuckets -> SetExpanded(true). Switching
                // an already expanded page takes the same path. Neither is a hover.
                Call(window, "RebuildBuckets");
                Check(collapse.IsEnabled, "quota refresh cannot cancel pending mouse-leave collapse");
                Call(window, "SelectPage", false);
                Check(collapse.IsEnabled, "same-mode page layout cannot cancel pending mouse-leave collapse");
                Call(window, "OnCollapseElapsed");
                Check(!Read<bool>(window, "expanded"), "pending mouse-leave still reaches compact mode after background layout changes");
            }
            finally { window.IsHitTestVisible = true; StopTimers(window); }
        }

        private static void RealMenusReleaseInteraction(IslandWindow window)
        {
            var shell = Read<Border>(window, "shell");
            window.SetExpanded(true, false); window.UpdateLayout(); StopTimers(window);
            OpenContextMenu(shell); Pump(20);
            Check(shell.ContextMenu.IsOpen, "island menu opens through WPF context-menu service");
            CloseMenuAndCheckRecovery(window, shell.ContextMenu, () => shell.ContextMenu.IsOpen = false, "island menu");

            Call(window, "SelectToolsPage"); Call(window, "SelectTool", "converter");
            window.SetExpanded(true, false); window.UpdateLayout(); StopTimers(window);
            var panels = Read<Dictionary<string, UIElement>>(window, "toolPanels");
            var input = (TextBox)panels["converter"].GetType().GetField("input", Private).GetValue(panels["converter"]);
            Check(input.ContextMenu == null, "converter input uses the native WPF editing menu");
            // The built-in menu queries Paste capability during construction.
            // Suppress only that command so this lifecycle test never reads the
            // user's clipboard or depends on clipboard access in the sandbox.
            CanExecuteRoutedEventHandler noClipboard = (s, e) => {
                if (e.Command == ApplicationCommands.Paste) { e.CanExecute = false; e.Handled = true; }
            };
            input.AddHandler(CommandManager.PreviewCanExecuteEvent, noClipboard, true);
            try
            {
                OpenContextMenu(input); Pump(20);
                var editorMenu = Mouse.Captured as ContextMenu;
                Check(editorMenu != null && editorMenu.IsOpen && ReferenceEquals(editorMenu.PlacementTarget, input), "default TextBox editing menu owns capture for its real editor");
                CloseMenuAndCheckRecovery(window, editorMenu, () => editorMenu.IsOpen = false, "default TextBox menu");
            }
            finally { input.RemoveHandler(CommandManager.PreviewCanExecuteEvent, noClipboard); }

            var settings = Read<Settings>(window, "settings"); settings.FavoriteTools = "focus,time";
            Call(window, "ConfigureToolkit"); Call(window, "SelectToolsPage");
            window.SetExpanded(true, false); window.UpdateLayout(); StopTimers(window);
            var favorite = Read<WrapPanel>(window, "favoriteTools").Children.OfType<Button>().First();
            var favoriteMenu = favorite.ContextMenu; OpenContextMenu(favorite); Pump(20);
            Check(favoriteMenu.IsOpen, "favorite menu opens through WPF context-menu service");
            CloseMenuAndCheckRecovery(window, favoriteMenu,
                () => typeof(MenuItem).GetMethod("OnClick", Private).Invoke(favoriteMenu.Items[0], null), "detached favorite menu");
            Check(favorite.Parent == null && settings.FavoriteTools == "time", "favorite action detaches the old menu target before close finishes");
        }

        private static void CloseMenuAndCheckRecovery(IslandWindow window, ContextMenu menu, Action close, string label)
        {
            bool closed = false; menu.Closed += (s, e) => closed = true;
            try
            {
                Check((bool)Call(window, "HasPopupInteraction"), label + " protects mode changes while it is open");
                Call(window, "OnCollapseElapsed");
                Check(Read<bool>(window, "expanded"), label + " prevents auto-collapse during selection");
                window.IsHitTestVisible = false;
                close();
                // IsOpen becomes false before native fade/capture cleanup finishes.
                // Wait for Closed and the actual capture handoff, not a fixed sleep.
                var deadline = Stopwatch.StartNew();
                while ((!closed || ReferenceEquals(Mouse.Captured, menu)) && deadline.ElapsedMilliseconds < 1500) Pump(10);
                Check(closed && !ReferenceEquals(Mouse.Captured, menu), label + " completes native fade and releases capture");
                Keyboard.ClearFocus(); Pump(10);
                Check(!(bool)Call(window, "HasPopupInteraction"), label + " leaves no popup interaction guard after cancellation");
                Check(!Read<bool>(window, "expanded") || Read<DispatcherTimer>(window, "collapseTimer").IsEnabled, label + " resumes automatic collapse after closure");
                Call(window, "OnCollapseElapsed");
                Check(!Read<bool>(window, "expanded"), label + " can return to compact mode after closure");
                Call(window, "OnIslandMouseLeave"); Call(window, "OnIslandMouseEnter");
                // Reentry reverses an in-flight shrink immediately. If its
                // first frame has not moved yet, the expanded visual already
                // equals the reverse target and legitimately needs no animation.
                Check(Read<DispatcherTimer>(window, "hoverTimer").IsEnabled || Read<bool>(window, "expanded"),
                    label + " permits hover recovery through immediate reversal or the stable compact delay");
            }
            finally { menu.IsOpen = false; window.IsHitTestVisible = true; Keyboard.ClearFocus(); Pump(10); StopTimers(window); }
        }

        private static void AppearanceDuringModeContinuity(IslandWindow window)
        {
            var settings = Read<Settings>(window, "settings");
            string originalLanguage = settings.Language, originalTheme = settings.Theme, originalSide = settings.Side;
            bool originalAutoCollapse = settings.AutoCollapse, originalFreePosition = settings.FreePosition, originalHitTest = window.IsHitTestVisible;
            int cases = 0, observed = 0;
            Check(SystemParameters.ClientAreaAnimation, "appearance continuity requires real enabled animation frames");
            try
            {
                settings.AutoCollapse = false; settings.FreePosition = false;
                window.IsHitTestVisible = false; Keyboard.ClearFocus(); StopTimers(window);
                foreach (string side in new[] { "left", "right", "top", "bottom" })
                foreach (bool expand in new[] { true, false })
                foreach (string control in new[] { "languageButton", "themeButton" })
                {
                    string context = side + "/" + (expand ? "expand" : "collapse") + "/" + control;
                    settings.Side = side;
                    // Setup may commit a mode. The measured request and its
                    // completion below only use the real production driver.
                    window.SetExpanded(!expand, false); window.UpdateLayout(); StopTimers(window);
                    window.SetExpanded(expand, true);
                    int middleFrames = 0; TimeSpan previous = TimeSpan.MinValue;
                    WaitAppearanceFrames(window, render => {
                        double progress = Read<double>(window, "transitionLastProgress");
                        if (Read<bool>(window, "visualTransitionActive") && progress >= .12 && progress <= .7 && render.RenderingTime != previous)
                        { middleFrames++; previous = render.RenderingTime; }
                        return middleFrames >= 2;
                    }, context + "/two distinct intermediate frames");

                    double beforeProgress = Read<double>(window, "transitionLastProgress");
                    Check(Read<bool>(window, "visualTransitionActive") && beforeProgress >= .12 && beforeProgress <= .7,
                        context + ": appearance action missed the actual intermediate mode");
                    Stopwatch clock = Read<Stopwatch>(window, "transitionWatch");
                    int version = Read<int>(window, "transitionVersion");
                    Rect host = Read<Rect>(window, "transitionHostBounds"), target = Read<Rect>(window, "transitionTargetBounds");
                    Rect native = NativeBounds(window);
                    Check(clock != null && Read<Image>(window, "outgoingImage").Source != null,
                        context + ": fixture must have a real mode clock and outgoing content before replacement");
                    string expectedLanguage = control == "languageButton" ? (settings.Language == "en" ? "zh" : "en") : settings.Language;
                    string expectedTheme = control == "themeButton" ? (settings.Theme == "light" ? "dark" : "light") : settings.Theme;

                    // Inspect only after the atomic production action returns:
                    // ApplyAppearance/native setup may synchronously render
                    // while its language/theme combination is still updating.
                    Read<Button>(window, control).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); StopTimers(window);
                    Check(Read<bool>(window, "visualTransitionActive"), context + ": appearance prematurely commits the mode target");
                    Check(Read<int>(window, "transitionVersion") == version && Object.ReferenceEquals(Read<Stopwatch>(window, "transitionWatch"), clock),
                        context + ": appearance restarts or replaces the geometry clock");
                    Check(Read<Rect>(window, "transitionHostBounds") == host && Read<Rect>(window, "transitionTargetBounds") == target && NativeBounds(window) == native,
                        context + ": appearance changes the current native host or requested target");
                    Check(settings.Language == expectedLanguage && UiLocalization.Language == expectedLanguage && settings.Theme == expectedTheme && UiTheme.Name == expectedTheme,
                        context + ": new language/theme did not take effect immediately");
                    Check(Object.Equals(Read<Button>(window, "languageButton").Content, expectedLanguage == "en" ? "中" : "EN") &&
                        Object.Equals(Read<Button>(window, "themeButton").Content, expectedTheme == "light" ? "☾" : "☼"), context + ": header does not describe the latest appearance");
                    Check(Read<Image>(window, "outgoingImage").Source == null && Read<object>(window, "pendingTransitionSnapshot") == null && Read<Border>(window, "shell").Opacity == 1,
                        context + ": old appearance pixels remain or the live content is left dimmed");

                    int afterFrames = 0; previous = TimeSpan.MinValue;
                    WaitAppearanceFrames(window, render => {
                        if (render.RenderingTime != previous) { afterFrames++; previous = render.RenderingTime; observed++; }
                        Check(Read<bool>(window, "expanded") == expand, context + ": obsolete completion changes the latest logical mode");
                        Check(Read<Image>(window, "outgoingImage").Source == null && Read<object>(window, "pendingTransitionSnapshot") == null && Read<Border>(window, "shell").Opacity == 1,
                            context + ": a subsequent frame restores old pixels or dims the refreshed content");
                        Check(Read<Rect>(window, "transitionTargetBounds") == target, context + ": a later frame replaces the requested target");
                        if (Read<bool>(window, "visualTransitionActive"))
                        {
                            int currentVersion = Read<int>(window, "transitionVersion");
                            Stopwatch currentClock = Read<Stopwatch>(window, "transitionWatch");
                            if (currentVersion != version || !Object.ReferenceEquals(currentClock, clock))
                            {
                                // Natural collapse commits a smaller HWND. Its
                                // native ApplyBounds can synchronously render
                                // between clearing the old clock and releasing
                                // the layout guard. Observe that exact completed
                                // transaction, then wait for its return; it is
                                // not a replacement animation or an early stop.
                                Check(clock.Elapsed.TotalMilliseconds >= IslandWindow.TransitionMilliseconds &&
                                    currentVersion == version + 1 && currentClock == null &&
                                    SameBounds(window.CurrentVisualBounds(), target),
                                    context + ": subsequent frames replace the original geometry driver before natural completion; elapsed=" + clock.Elapsed.TotalMilliseconds +
                                    "; version=" + currentVersion + "; expected=" + version + "; visual=" + window.CurrentVisualBounds() + "; target=" + target);
                                return false;
                            }
                            Check(Read<Rect>(window, "transitionHostBounds") == host && NativeBounds(window) == native,
                                context + ": native host moves before natural completion");
                            return false;
                        }
                        // A slow frame may legitimately cross completion. It
                        // must have reached the original clock's actual end.
                        Check(clock.Elapsed.TotalMilliseconds >= IslandWindow.TransitionMilliseconds,
                            context + ": another callback forcibly completed the retained clock early");
                        return afterFrames >= 2;
                    }, context + "/natural completion after fresh frames");
                    Check(Read<object>(window, "transitionWatch") == null && Read<object>(window, "transitionClip") == null && Read<Border>(window, "shell").Clip == null,
                        context + ": naturally completed mode retained its clock or clip");
                    Check(SameBounds(Bounds(window), target) && SameBounds(window.CurrentVisualBounds(), target),
                        context + ": final native/logical target was not reached naturally");
                    cases++;
                    Console.WriteLine("APPEARANCE CONTINUITY: " + context + "; start-progress=" + beforeProgress.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) + "; post-action-frames=" + afterFrames + "; natural-completion=true");
                }
                Check(cases == 16 && observed >= 32, "appearance continuity matrix did not fully execute");
                Console.WriteLine("PASS: 16 four-edge expand/collapse language/theme actions retain the geometry clock, native host and target, immediately refresh appearance, discard old pixels, and finish naturally across " + observed + " fresh rendering frames.");
            }
            finally
            {
                settings.Language = originalLanguage; settings.Theme = originalTheme; settings.Side = originalSide; settings.FreePosition = originalFreePosition;
                if (UiLocalization.Language != originalLanguage || UiTheme.Name != originalTheme) Call(window, "ApplyAppearance");
                settings.AutoCollapse = originalAutoCollapse; window.IsHitTestVisible = originalHitTest; StopTimers(window);
            }
        }

        private static void WaitAppearanceFrames(IslandWindow window, Func<RenderingEventArgs, bool> predicate, string context)
        {
            var frame = new DispatcherFrame(); bool satisfied = false, expired = false; Exception failure = null;
            var timeout = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(1500) };
            EventHandler rendering = (sender, args) => {
                try { StopTimers(window); if (predicate((RenderingEventArgs)args)) { satisfied = true; frame.Continue = false; } }
                catch (Exception error) { failure = error; frame.Continue = false; }
            };
            timeout.Tick += (sender, args) => { expired = true; frame.Continue = false; };
            CompositionTarget.Rendering += rendering; timeout.Start();
            try { Dispatcher.PushFrame(frame); }
            finally { timeout.Stop(); CompositionTarget.Rendering -= rendering; StopTimers(window); }
            if (failure != null) throw new Exception(context, failure);
            Check(satisfied && !expired, context + ": required actual rendering state not reached within 1500ms");
        }

        private static Rect NativeBounds(IslandWindow window)
        {
            NativeRect bounds; Check(GetWindowRect(new WindowInteropHelper(window).Handle, out bounds), "own native window bounds unavailable");
            return new Rect(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
        }

        private static void RepeatedInterruptedModes(IslandWindow window)
        {
            var settings = Read<Settings>(window, "settings");
            bool autoCollapse = settings.AutoCollapse; settings.AutoCollapse = false;
            bool hitTestVisible = window.IsHitTestVisible;
            bool tracingFinalWait = false;
            bool trace = Environment.GetEnvironmentVariable("CODEX_ISLAND_MODE_TRACE") == "1";
            MouseEventHandler mouseTrace = (sender, args) => {
                if (trace && tracingFinalWait) Console.WriteLine("MODE TRACE " + args.RoutedEvent.Name + ": " + DescribeState(window));
            };
            SizeChangedEventHandler sizeTrace = (sender, args) => {
                if (trace && tracingFinalWait) Console.WriteLine("MODE TRACE SizeChanged: " + DescribeState(window));
            };
            window.MouseEnter += mouseTrace; window.MouseLeave += mouseTrace; window.SizeChanged += sizeTrace;
            try
            {
                // This checks programmatic cancellation, not user hover. A
                // physical Leave/Enter during the final wait is a valid new
                // expansion request, even when AutoCollapse is disabled.
                // Real hover/reentry is covered by the interaction/frame tests.
                window.IsHitTestVisible = false; Pump(20); StopTimers(window);
                Check(!window.IsMouseOver, "interrupted-mode fixture is independent of physical cursor placement");
                foreach (string side in new[] { "left", "right", "top", "bottom" })
                {
                    settings.Side = side; settings.FreePosition = false;
                    settings.HorizontalPosition = .4; settings.VerticalPosition = .3;
                    window.SetExpanded(false, false); window.UpdateLayout(); StopTimers(window);
                    Rect anchor = Bounds(window);
                    for (int round = 0; round < 10; round++)
                    {
                        window.SetExpanded(true, true); Pump(5); StopTimers(window);
                        window.SetExpanded(false, true); Pump(5); StopTimers(window);
                        Call(window, "ShowPeek", new IslandNotification { AppId = "mode.test", AppName = "交互测试", Title = "测试通知", Body = "隔离测试", IsDemo = true });
                        Pump(5); StopTimers(window);
                        Check(Read<bool>(window, "peeking"), side + " preview can interrupt collapse on round " + round);
                        window.SetExpanded(true, false); window.UpdateLayout();
                        Check(Read<bool>(window, "expanded") && !Read<bool>(window, "peeking") && Read<ScrollViewer>(window, "detailsScroll").IsVisible,
                            side + " explicit expansion wins over preview on round " + round);
                        window.SetExpanded(false, false); window.UpdateLayout(); StopTimers(window);
                        Check(!Read<bool>(window, "expanded") && !Read<bool>(window, "peeking") && SameBounds(anchor, Bounds(window)),
                            side + " compact state and anchor survive interrupted mode round " + round);
                    }
                    tracingFinalWait = true;
                    if (trace) Console.WriteLine("MODE TRACE " + side + " before final wait anchor=" + anchor + ": " + DescribeState(window));
                    Pump(IslandWindow.TransitionMilliseconds + 50); window.UpdateLayout();
                    if (trace) Console.WriteLine("MODE TRACE " + side + " after final wait anchor=" + anchor + ": " + DescribeState(window));
                    tracingFinalWait = false; StopTimers(window);
                    Check(SameBounds(anchor, Bounds(window)) && !Read<bool>(window, "expanded") && !Read<bool>(window, "peeking"),
                        side + " no obsolete animation completion can overwrite the final compact mode; anchor=" + anchor + "; " + DescribeState(window));
                }
            }
            finally
            {
                window.MouseEnter -= mouseTrace; window.MouseLeave -= mouseTrace; window.SizeChanged -= sizeTrace;
                settings.AutoCollapse = autoCollapse; window.IsHitTestVisible = hitTestVisible; StopTimers(window);
            }
        }

        private static string DescribeState(IslandWindow window)
        {
            return "bounds=" + Bounds(window) + ", visual=" + window.CurrentVisualBounds() +
                ", expanded=" + Read<bool>(window, "expanded") + ", peeking=" + Read<bool>(window, "peeking") +
                ", hoverSuppressed=" + Read<bool>(window, "hoverSuppressed") +
                ", hoverTimer=" + Read<DispatcherTimer>(window, "hoverTimer").IsEnabled +
                ", collapseTimer=" + Read<DispatcherTimer>(window, "collapseTimer").IsEnabled +
                ", mouseOver=" + window.IsMouseOver + ", hitTest=" + window.IsHitTestVisible +
                ", active=" + Read<bool>(window, "visualTransitionActive") +
                ", transitionVersion=" + Read<int>(window, "transitionVersion");
        }

        private static Rect Bounds(IslandWindow window) { return new Rect(window.Left, window.Top, window.Width, window.Height); }
        private static bool SameBounds(Rect a, Rect b) { return Math.Abs(a.Left - b.Left) <= 1 && Math.Abs(a.Top - b.Top) <= 1 && Math.Abs(a.Width - b.Width) <= 1 && Math.Abs(a.Height - b.Height) <= 1; }
        private static void OpenContextMenu(IInputElement target)
        {
            var popupType = typeof(ContextMenu).Assembly.GetType("System.Windows.Controls.PopupControlService");
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            var opening = popupType.GetMethod("RaiseContextMenuOpeningEvent", flags, null,
                new[] { typeof(IInputElement), typeof(double), typeof(double), typeof(bool) }, null);
            object service = popupType.GetMethod("get_Current", flags).Invoke(null, null);
            opening.Invoke(service, new object[] { target, -1.0, -1.0, true });
        }

        private static T Read<T>(IslandWindow window, string name) { return (T)typeof(IslandWindow).GetField(name, Private).GetValue(window); }
        private static object Call(IslandWindow window, string name, params object[] values)
        {
            try { return typeof(IslandWindow).GetMethod(name, Private).Invoke(window, values); }
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
        private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    }
}
