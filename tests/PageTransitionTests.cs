using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    // Only this process's demo HWND, temporary settings and synthetic messages.
    // Rendering events observe real page opacity; no global input is sent.
    internal static class PageTransitionTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { internal int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out NativeRect rectangle);
        private sealed class LayoutProbe : FrameworkElement
        {
            internal int Measures, Arranges;
            protected override Size MeasureOverride(Size available) { Measures++; return new Size(); }
            protected override Size ArrangeOverride(Size final) { Arranges++; return final; }
        }
        private static int measuredTransitions, frames;

        internal static void Run()
        {
            string previous = Environment.GetEnvironmentVariable("CODEX_ISLAND_DATA_DIR");
            string folder = Path.Combine(Path.GetTempPath(), "CodexIsland.PageTransitions." + Guid.NewGuid().ToString("N"));
            IslandWindow window = null; measuredTransitions = frames = 0;
            try
            {
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", folder);
                new SettingsStore().Save(new Settings { AlwaysOnTop = false, AutoCollapse = false, NotificationsEnabled = false,
                    HoverExpandEnabled = false, AutoMusic = false, CustomExpandedSize = false, ExpandedWidth = 440, ExpandedHeight = 600 });
                window = new IslandWindow(true) { ShowActivated = false, IsHitTestVisible = false };
                window.Loaded += (s, e) => StopTimers(window);
                window.Show(); Settle(window); StopTimers(window);
                var probe = new LayoutProbe(); Read<StackPanel>(window, "details").Children.Add(probe);
                foreach (string side in new[] { "left", "right", "top", "bottom" })
                foreach (bool custom in new[] { false, true })
                    StableFrames(window, probe, side, custom);
                RapidAndSamePage(window);
                BackgroundRefresh(window);
                DuringModeTransition(window);
                PrivacyAndFeed(window);
                Interruptions(window);
                SessionHeight(window);
                Select(window, 1); WaitForPage(window); Select(window, 0);
                window.Close();
                Clean(window, "closing"); Pump(230); Clean(window, "closed page cannot resume");
                Check(Read<bool>(window, "shuttingDown"), "fixture closed its demo window");
                Console.WriteLine("PASS: " + measuredTransitions + " real page fades and " + frames + " rendering frames; three-page/default-custom/four-edge stable HWND geometry, 40 rapid requests, same-page/background/mode-clock preservation, privacy/feed gates and interruption cleanup. No global input or desktop capture.");
            }
            finally
            {
                if (window != null && !Read<bool>(window, "shuttingDown")) { StopTimers(window); window.Close(); }
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", previous);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }

        private static void StableFrames(IslandWindow window, LayoutProbe probe, string side, bool custom)
        {
            var settings = Read<Settings>(window, "settings");
            window.SetExpanded(false, false); Call(window, "ChangeSide", side);
            settings.CustomExpandedSize = custom; settings.ExpandedWidth = 440; settings.ExpandedHeight = 600;
            Select(window, 0); window.SetExpanded(true, false); Settle(window);
            Rect expected = NativeBounds(window); double height = window.Height;
            int[][] pairs = { new[] { 0, 1 }, new[] { 1, 2 }, new[] { 2, 0 }, new[] { 0, 2 }, new[] { 2, 1 }, new[] { 1, 0 } };
            foreach (int[] pair in pairs)
            {
                Select(window, pair[0]); WaitForPage(window); window.UpdateLayout();
                var target = Page(window, pair[1]); var opacities = new List<double>(); double initialOpacity = 1;
                EventHandler rendered = (s, e) => {
                    opacities.Add(target.Opacity); frames++;
                    Check(NativeBounds(window) == expected, side + "/" + custom + ": page frame resizes or moves native HWND");
                    Check(Math.Abs(window.Height - height) < .01, "page frame changes logical height");
                    Check(Read<Border>(window, "shell").Opacity == 1 && Read<ScrollViewer>(window, "detailsScroll").Opacity == 1,
                        "page transition fades the outer shell or scroll host");
                    foreach (string button in new[] { "notificationsTab", "statusTab", "toolsTab", "pinButton" })
                        Check(Read<Button>(window, button).Opacity == 1, "fixed header/tab fades with its page");
                };
                CompositionTarget.Rendering += rendered;
                try
                {
                    Select(window, pair[1]); initialOpacity = target.Opacity;
                    Check(target.IsVisible && target.IsEnabled, "new page is not available immediately during fade");
                    if (SystemParameters.ClientAreaAnimation) Check(initialOpacity >= .349 && initialOpacity < 1, "new page starts blank or has no softened first frame");
                    window.UpdateLayout(); StopTimers(window);
                    probe.Measures = probe.Arranges = 0;
                    WaitForPage(window);
                }
                finally { CompositionTarget.Rendering -= rendered; }
                Check(target.IsVisible && Math.Abs(target.Opacity - 1) < .001, "selected page reaches fully visible state");
                Check(Page(window, pair[0]).Visibility == Visibility.Collapsed, "previous page remains collapsed");
                if (SystemParameters.ClientAreaAnimation)
                    Check(opacities.Any(value => value > initialOpacity + .01 && value < .99), "three-page switch has no genuine intermediate rendered opacity");
                for (int i = 1; i < opacities.Count; i++) Check(opacities[i] + .001 >= opacities[i - 1], "incoming page opacity reverses");
                Check(probe.Measures == 0 && probe.Arranges == 0, "page fade reflows the window after its initial page layout");
                Clean(window, "completed page fade"); measuredTransitions++;
            }
        }

        private static void RapidAndSamePage(IslandWindow window)
        {
            Select(window, 0); WaitForPage(window); Rect bounds = NativeBounds(window);
            Select(window, 1); Pump(35); StopTimers(window);
            if (SystemParameters.ClientAreaAnimation)
            {
                Check(Active(window), "same-page fixture has an active fade");
                int version = Read<int>(window, "pageTransitionVersion"); object clock = Read<object>(window, "pageTransitionWatch");
                double opacity = Page(window, 1).Opacity;
                Select(window, 1);
                Check(Read<int>(window, "pageTransitionVersion") == version && Object.ReferenceEquals(clock, Read<object>(window, "pageTransitionWatch")), "clicking selected page restarts its fade");
                Check(Math.Abs(Page(window, 1).Opacity - opacity) < .001, "same-page selection flashes opacity");
            }
            for (int request = 0; request < 40; request++)
            {
                if (request % 5 == 0)
                    Read<Button>(window, request % 3 == 0 ? "notificationsTab" : request % 3 == 1 ? "statusTab" : "toolsTab").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                else Select(window, request % 3);
                Pump(3); StopTimers(window);
                Check(NativeBounds(window) == bounds, "rapid page request changes native geometry");
                Check(Page(window, request % 3).IsVisible, "rapid request does not select latest page");
            }
            Select(window, 2); WaitForPage(window); Pump(230); StopTimers(window);
            Check(Page(window, 2).IsVisible && !Page(window, 0).IsVisible && !Page(window, 1).IsVisible, "stale page completion restores an older page");
            Clean(window, "rapid page completion");
        }

        private static void BackgroundRefresh(IslandWindow window)
        {
            Select(window, 0); WaitForPage(window); Select(window, 1); Pump(30); StopTimers(window);
            Rect bounds = NativeBounds(window); object clock = Read<object>(window, "pageTransitionWatch"); int version = Read<int>(window, "pageTransitionVersion");
            Call(window, "RebuildBuckets");
            Check(NativeBounds(window) == bounds, "quota refresh changes session height after page switch");
            if (SystemParameters.ClientAreaAnimation)
                Check(Active(window) && version == Read<int>(window, "pageTransitionVersion") && ReferenceEquals(clock, Read<object>(window, "pageTransitionWatch")), "quota refresh cancels or restarts active page fade");
            Call(window, "UpdateNotificationListCore", false);
            if (SystemParameters.ClientAreaAnimation)
                Check(Active(window) && version == Read<int>(window, "pageTransitionVersion") && ReferenceEquals(clock, Read<object>(window, "pageTransitionWatch")), "ordinary notification poll restarts active page fade");
            WaitForPage(window);
        }

        private static void DuringModeTransition(IslandWindow window)
        {
            if (!SystemParameters.ClientAreaAnimation) return;
            window.SetExpanded(false, false); Select(window, 0); window.SetExpanded(true, true); Pump(45); StopTimers(window);
            Check(Read<bool>(window, "visualTransitionActive"), "mode fixture has genuine active expansion");
            int version = Read<int>(window, "transitionVersion"); object clock = Read<object>(window, "transitionWatch"); Rect before = window.CurrentVisualBounds();
            Select(window, 2);
            Check(Read<bool>(window, "visualTransitionActive") && version == Read<int>(window, "transitionVersion") && ReferenceEquals(clock, Read<object>(window, "transitionWatch")), "page selection restarts current expand clock");
            Check(window.CurrentVisualBounds() == before && !Active(window), "page selection starts competing content fade during expansion");
            Pump(300); StopTimers(window); Clean(window, "mode-expanded page");
        }

        private static void PrivacyAndFeed(IslandWindow window)
        {
            var settings = Read<Settings>(window, "settings"); settings.Language = "zh"; Call(window, "ApplyAppearance");
            var feed = Read<NotificationFeed>(window, "notificationFeed"); var preferences = Read<NotificationPreferences>(window, "notificationPreferences");
            var message = new IslandNotification { Id = "page-private-" + Guid.NewGuid().ToString("N"), AppId = "fixture.page.privacy", AppName = "隔离测试", Title = "PRIVATE_PAGE_TITLE", Body = "PRIVATE_PAGE_BODY", IsDemo = true, CreatedAt = DateTime.UtcNow };
            string key = NotificationPreferences.SourceKey(message);
            Action prepare = () => {
                settings.NotificationsEnabled = true; settings.HideNotificationBody = false;
                preferences.Observe(new[] { message }); preferences.SetBlocked(key, false); feed.SetSourceBlocked(key, false);
                feed.Clear(); message.Id = "page-private-" + Guid.NewGuid().ToString("N"); feed.Merge(new[] { message }, false, false); Call(window, "UpdateNotificationList");
                Select(window, 1); window.SetExpanded(true, false); WaitForPage(window); Select(window, 0);
                if (SystemParameters.ClientAreaAnimation) Check(Active(window), "notification privacy fixture has an active page fade");
            };
            prepare(); settings.HideNotificationBody = true; Call(window, "ConfigureNotifications"); Clean(window, "privacy change");
            prepare(); Call(window, "SetNotificationSourceBlocked", key, true); Clean(window, "blocked source");
            prepare();
            var clear = LogicalDescendants<Button>(Read<StackPanel>(window, "notificationPage")).Single(item => (string)item.Content == "清空岛内记录");
            clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Clean(window, "cleared messages"); Check(feed.Items.Count == 0, "clear action retains no messages");
            prepare(); settings.NotificationsEnabled = false; Call(window, "ConfigureNotifications"); Clean(window, "disabled notifications");
            Pump(220); Clean(window, "privacy cleanup cannot be undone by an old completion");
        }

        private static void Interruptions(IslandWindow window)
        {
            Action prepare = () => { window.Show(); window.SetExpanded(true, false); Select(window, 1); WaitForPage(window); Select(window, 2); };
            prepare(); window.SetExpanded(false, true); Clean(window, "collapse"); Pump(270); StopTimers(window);
            prepare(); window.Hide(); Clean(window, "hide"); window.Show(); Settle(window);
            prepare(); Call(window, "ApplyAppearance"); Clean(window, "appearance change");
            prepare(); Call(window, "FreezeSizeAnimation"); Clean(window, "resize geometry freeze");
            prepare(); Call(window, "BeginPageTransition", Page(window, 2), false); Clean(window, "disabled page animation branch");
            prepare();
            var header = Read<Grid>(window, "expandedHeader");
            var title = header.Children.OfType<StackPanel>().First();
            title.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent, Source = title });
            Clean(window, "drag preparation");
            var shell = Read<Border>(window, "shell");
            if (Read<bool>(window, "dragging")) Call(window, "FinishDrag", shell, false);
            if (ReferenceEquals(Mouse.Captured, shell)) shell.ReleaseMouseCapture();
            prepare();
            Call(window, "ShowPeek", new IslandNotification { Id = "page-peek", AppId = "fixture.page.peek", AppName = "隔离测试", Title = "Preview", Body = "Fixture", IsDemo = true });
            Clean(window, "notification preview"); window.SetExpanded(false, false); StopTimers(window);
            Select(window, 1); Clean(window, "compact selection is immediate");
        }

        private static void SessionHeight(IslandWindow window)
        {
            var settings = Read<Settings>(window, "settings"); settings.CustomExpandedSize = false;
            window.SetExpanded(false, false); Select(window, 0); window.SetExpanded(true, false); Settle(window);
            double first = window.Height; Select(window, 2); WaitForPage(window); Call(window, "RebuildBuckets");
            Check(Math.Abs(window.Height - first) < .01, "background refresh reverts stable default session height");
            window.SetExpanded(false, false); window.SetExpanded(true, false); Settle(window);
            double available = ((Rect)Call(window, "PlacementArea")).Height;
            Check(Math.Abs(window.Height - Math.Min(680, available)) < 1.1, "new expanded session does not use current page's default height");
            settings.CustomExpandedSize = true; settings.ExpandedHeight = 540; window.SetExpanded(true, false); Settle(window);
            Check(Math.Abs(window.Height - Math.Min(540, available)) < 1.1, "custom size is ignored by cached default session height");
            Select(window, 1); WaitForPage(window); Call(window, "ResetExpandedSize"); Settle(window);
            double defaultStatus = 618 + (Read<UsageSnapshot>(window, "snapshot") != null && Read<UsageSnapshot>(window, "snapshot").Buckets.Count > 1 ? 42 : 0) + (Read<string>(window, "failure") != null ? 32 : 0);
            Check(Math.Abs(window.Height - Math.Min(defaultStatus, available)) < 1.1, "reset expanded size keeps an obsolete cached page height");
        }

        private static void Select(IslandWindow window, int page) { if (page == 2) Call(window, "SelectToolsPage"); else Call(window, "SelectPage", page == 0); }
        private static FrameworkElement Page(IslandWindow window, int index) { return Read<FrameworkElement>(window, index == 0 ? "notificationPage" : index == 1 ? "statusPage" : "toolsPage"); }
        private static bool Active(IslandWindow window) { return Read<bool>(window, "pageTransitionActive"); }
        private static void Clean(IslandWindow window, string context)
        {
            Check(!Active(window) && Read<object>(window, "pageTransitionWatch") == null && Read<object>(window, "pageTransitionPage") == null, context + ": page animation retains its clock or target");
            foreach (int index in new[] { 0, 1, 2 }) Check(Math.Abs(Page(window, index).Opacity - 1) < .001, context + ": page opacity remains dimmed");
        }
        private static void WaitForPage(IslandWindow window)
        {
            var elapsed = Stopwatch.StartNew();
            do { Pump(20); StopTimers(window); } while (Active(window) && elapsed.ElapsedMilliseconds < 1500);
            window.UpdateLayout(); Check(!Active(window), "page fade does not finish within bounded wait");
        }
        private static void Settle(IslandWindow window) { StopTimers(window); window.UpdateLayout(); Pump(20); StopTimers(window); window.UpdateLayout(); }
        private static void StopTimers(IslandWindow window) { foreach (var field in typeof(IslandWindow).GetFields(Private)) if (field.FieldType == typeof(DispatcherTimer)) ((DispatcherTimer)field.GetValue(window)).Stop(); }
        private static Rect NativeBounds(Window window) { NativeRect bounds; Check(GetWindowRect(new WindowInteropHelper(window).Handle, out bounds), "own native bounds unavailable"); return new Rect(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top); }
        private static T Read<T>(object value, string name) { return (T)value.GetType().GetField(name, Private).GetValue(value); }
        private static object Call(object value, string name, params object[] arguments) { try { return value.GetType().GetMethod(name, Private).Invoke(value, arguments); } catch (TargetInvocationException error) { throw error.InnerException ?? error; } }
        private static IEnumerable<T> LogicalDescendants<T>(DependencyObject root) where T : DependencyObject { foreach (object value in LogicalTreeHelper.GetChildren(root)) { var child = value as DependencyObject; if (child == null) continue; if (child is T) yield return (T)child; foreach (var nested in LogicalDescendants<T>(child)) yield return nested; } }
        private static void Check(bool value, string message) { if (!value) throw new Exception("Page transition: " + message); }
        private static void Pump(int milliseconds) { var frame = new DispatcherFrame(); var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) }; timer.Tick += (s, e) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame); }
    }
}
