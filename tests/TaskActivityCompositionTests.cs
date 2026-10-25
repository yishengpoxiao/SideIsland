using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace CodexIsland
{
    // Uses one actual own-process demo HWND and isolated settings. Resolves
    // logical hit-test targets and invokes their existing action route; never
    // moves the native cursor, captures global input, or starts a real task.
    internal static class TaskActivityCompositionTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        internal static void Run()
        {
            string previous = Environment.GetEnvironmentVariable("CODEX_ISLAND_DATA_DIR");
            string language = UiLocalization.Language, theme = UiTheme.Name;
            string folder = Path.Combine(Path.GetTempPath(), "SideIsland.TaskActivityComposition." + Guid.NewGuid().ToString("N"));
            IslandWindow window = null;
            try
            {
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", folder);
                new SettingsStore().Save(new Settings { AlwaysOnTop = false, AutoCollapse = false, AutoMusic = false, NotificationsEnabled = false, NotificationPreview = false, HoverExpandEnabled = false, Language = language, Theme = theme });
                window = new IslandWindow(true) { ShowActivated = false };
                window.Loaded += (s, e) => StopTimers(window);
                window.Show(); Pump(); StopTimers(window); window.SetExpanded(false, false); window.UpdateLayout();
                foreach (string edge in new[] { "right", "top" })
                {
                    Read<Settings>(window, "settings").Side = edge;
                    ActivityActions(window); ExistingActivityCards(window); RotationActions(window); RestoreAlternate(window);
                }
                Console.WriteLine("PASS: actual demo-window activity override action/hover targets in quota/clock/focus modes, rotated widget tags, activity de-duplication and size preservation, and direct-refresh clock/focus restoration after removal or 8-second terminal expiry; vertical/horizontal docks, isolated settings, no global input or real tasks.");
            }
            finally
            {
                if (window != null) { StopTimers(window); window.Close(); }
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", previous);
                AppAppearance.Configure(language, theme);
                string full = Path.GetFullPath(folder), temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (Directory.Exists(full) && full.StartsWith(temporary, StringComparison.OrdinalIgnoreCase)) Directory.Delete(full, true);
            }
        }
        private static void ActivityActions(IslandWindow window)
        {
            foreach (string mode in new[] { "quota", "clock", "focus", "music" })
            {
                Configure(window, mode, "cpu", "memory");
                var hub = Read<TaskActivityHub>(window, "taskActivities");
                Read<TaskActivityPanel>(window, "taskActivityPanel").SetCompactEnabled(true);
                hub.Publish(new TaskActivityUpdate { Id = "composition", Title = "Fixture task", State = "running" });
                Refresh(window);
                Check(Read<Grid>(window, "compactSlot2").IsVisible && Read<CompactWidgetVisual>(window, "compactWidget2").State.Key == "activities", mode + " task occupies actual second slot");
                string first = mode == "clock" ? "clock" : mode == "focus" ? "focus" : mode == "music" ? "media" : "cpu";
                Check(Read<CompactWidgetVisual>(window, "compactWidget1").State.Key == first, mode + " fallback retains selected primary content");
                AssertAction(window, 2, "activities", mode + " activity override");
                AssertHover(window, Read<Grid>(window, "compactSlot2"), true, mode + " override slot hover");
                AssertHover(window, Read<StackPanel>(window, "compactAlternate"), false, mode + " hidden alternate hover");
                string action = ActionAt(window, 2);
                Call(window, "RunCompactAction", action);
                Check(Read<bool>(window, "expanded") && Read<bool>(window, "toolsPageSelected") && Read<string>(window, "selectedTool") == "activities", mode + " resolved click route opens task activities rather than configured memory");
                window.SetExpanded(false, false); StopTimers(window);
            }
        }
        private static void ExistingActivityCards(IslandWindow window)
        {
            Configure(window, "quota", "cpu", "memory");
            var hub = Read<TaskActivityHub>(window, "taskActivities");
            Read<TaskActivityPanel>(window, "taskActivityPanel").SetCompactEnabled(true);
            hub.Publish(new TaskActivityUpdate { Id = "composition", Title = "Fixture task" });
            foreach (bool first in new[] { true, false })
            {
                var cards = new[] { new WidgetCardDefinition { Key = first ? "activities" : "clock", Size = "small" }, new WidgetCardDefinition { Key = first ? "memory" : "activities", Size = "large" } };
                SaveLayout(window, cards, 0); Refresh(window);
                var resolved = (WidgetCardDefinition[])Call(window, "ApplyTaskActivityOverride", (object)cards);
                Check(Object.ReferenceEquals(resolved, cards), "existing activity leaves visible definitions and their IDs/sizes intact");
                Check(WidgetKeys(window).Count(key => key == "activities") == 1, "existing first/second activity is not duplicated");
                CheckSize(window, 1, .84, "existing first card retains small size"); CheckSize(window, 2, 1.06, "existing second card retains large size");
                AssertAction(window, first ? 1 : 2, "activities", "existing activity action");
            }
            SaveLayout(window, new[] { new WidgetCardDefinition { Key = "cpu", Size = "small" }, new WidgetCardDefinition { Key = "memory", Size = "large" } }, 0);
            Refresh(window); Check(WidgetKeys(window).SequenceEqual(new[] { "cpu", "activities" }), "task replaces only second non-task card");
            CheckSize(window, 2, 1.06, "injected task inherits replaced second card size");
            Read<WidgetLayoutStore>(window, "widgetLayouts").Data.Layouts.Clear();
            foreach (bool first in new[] { true, false })
            {
                var settings = Read<Settings>(window, "settings"); settings.CompactSlot1 = first ? "activities" : "cpu"; settings.CompactSlot2 = first ? "memory" : "activities";
                Refresh(window); Check(WidgetKeys(window).Count(key => key == "activities") == 1, "legacy configured activity is not duplicated without a custom pool");
                AssertAction(window, first ? 1 : 2, "activities", "legacy activity action");
            }
        }
        private static void RotationActions(IslandWindow window)
        {
            Configure(window, "quota", "codex-primary", "codex-secondary");
            Read<TaskActivityPanel>(window, "taskActivityPanel").SetCompactEnabled(false);
            SaveLayout(window, new[] { new WidgetCardDefinition { Key = "clock" }, new WidgetCardDefinition { Key = "awake" }, new WidgetCardDefinition { Key = "cpu" }, new WidgetCardDefinition { Key = "ports" } }, 5);
            Write(window, "widgetRotationOrigin", DateTime.UtcNow.AddSeconds(-5.1));
            Refresh(window);
            Check(WidgetKeys(window).SequenceEqual(new[] { "cpu", "ports" }), "actual custom layout rotates to the next pair");
            AssertAction(window, 1, "cpu", "rotated first slot"); AssertAction(window, 2, "ports", "rotated second slot");
            AssertHover(window, Read<Grid>(window, "compactSlot1"), true, "rotated visible first slot hover");
            Read<Settings>(window, "settings").CompactMode = "clock"; Refresh(window);
            AssertAction(window, 2, "ports", "custom pool action remains actual tag over legacy clock setting");
            AssertHover(window, Read<Grid>(window, "compactSlot2"), true, "custom pool slot hover over legacy clock setting");
        }
        private static void RestoreAlternate(IslandWindow window)
        {
            foreach (string mode in new[] { "clock", "focus" })
            foreach (bool expired in new[] { false, true })
            {
                Configure(window, mode, "cpu", "memory");
                var hub = Read<TaskActivityHub>(window, "taskActivities"); Read<TaskActivityPanel>(window, "taskActivityPanel").SetCompactEnabled(true);
                hub.Publish(new TaskActivityUpdate { Id = "composition", Title = "Fixture task", State = "running" }); Refresh(window);
                Check(Read<StackPanel>(window, "compactAlternate").Visibility == Visibility.Collapsed, mode + " task temporarily hides full-size alternate");
                if (expired) hub.Publish(new TaskActivityUpdate { Id = "composition", Title = "Fixture task", State = "completed" }, DateTime.UtcNow.AddSeconds(-9));
                else hub.Remove("composition");
                // Intentionally bypass UpdateToolkitCompact and its clock tick.
                // No dispatcher pump may run the queued full refresh first.
                window.UpdateCompactWidgets(); window.UpdateLayout(); StopTimers(window);
                Check(Read<StackPanel>(window, "compactAlternate").IsVisible && !Read<Grid>(window, "compactSlot1").IsVisible && !Read<Grid>(window, "compactSlot2").IsVisible, mode + " direct widget refresh restores alternate after " + (expired ? "8-second terminal expiry" : "task removal"));
                var alternate = Read<StackPanel>(window, "compactAlternate");
                Point point = alternate.TranslatePoint(new Point(alternate.ActualWidth / 2, alternate.ActualHeight / 2), window);
                Check((string)Call(window, "FindCompactActionAt", Read<TextBlock>(window, "compactAlternateMain"), point) == mode, mode + " restored alternate action is correct");
                AssertHover(window, alternate, true, mode + " restored alternate hover");
                AssertHover(window, Read<Grid>(window, "compactSlot2"), false, mode + " hidden old activity slot hover");
            }
        }
        private static void Configure(IslandWindow window, string mode, string first, string second)
        {
            Read<WidgetLayoutStore>(window, "widgetLayouts").Data.Layouts.Clear();
            var hub = Read<TaskActivityHub>(window, "taskActivities"); foreach (var item in hub.Snapshot()) hub.Remove(item.Id);
            var settings = Read<Settings>(window, "settings"); settings.CompactMode = mode; settings.CompactSlot1 = first; settings.CompactSlot2 = second; settings.AutoMusic = false;
            Read<TaskActivityPanel>(window, "taskActivityPanel").SetCompactEnabled(false);
            window.SetExpanded(false, false); Refresh(window);
        }
        private static void SaveLayout(IslandWindow window, WidgetCardDefinition[] cards, int seconds)
        {
            string profile = Read<LayoutProfileStore>(window, "layoutProfiles").Data.ActiveId ?? "";
            Read<WidgetLayoutStore>(window, "widgetLayouts").Save(new WidgetLayoutDefinition { ProfileId = profile, Enabled = true, RotateSeconds = seconds, NotificationOverride = false, FocusOverride = false, ActivityOverride = true, Cards = new List<WidgetCardDefinition>(cards) });
            Write(window, "widgetResolvedProfile", profile); Write(window, "widgetRotationOrigin", DateTime.UtcNow);
        }
        private static string[] WidgetKeys(IslandWindow window) { return new[] { Read<CompactWidgetVisual>(window, "compactWidget1").State.Key, Read<CompactWidgetVisual>(window, "compactWidget2").State.Key }; }
        private static void CheckSize(IslandWindow window, int slot, double scale, string label)
        { var target = Read<Grid>(window, "compactSlot" + slot); Check(Math.Abs(target.Width - 54 * scale) < .01 && Math.Abs(target.Height - 67 * scale) < .01, label); }
        private static string ActionAt(IslandWindow window, int slot)
        {
            var target = Read<Grid>(window, "compactSlot" + slot);
            Point center = target.TranslatePoint(new Point(target.ActualWidth / 2, target.ActualHeight / 2), window);
            return (string)Call(window, "FindCompactActionAt", Read<CompactWidgetVisual>(window, "compactWidget" + slot), center);
        }
        private static void AssertAction(IslandWindow window, int slot, string expected, string label)
        {
            Check(ActionAt(window, slot) == expected && Object.Equals(Read<Grid>(window, "compactSlot" + slot).Tag, expected), label + " resolves actual visible widget tag");
        }
        private static void AssertHover(IslandWindow window, FrameworkElement target, bool expected, string label)
        {
            MethodInfo changeOver = typeof(MouseDevice).GetMethod("ChangeMouseOver", Private, null, new[] { typeof(IInputElement), typeof(int) }, null);
            Check(changeOver != null, "own WPF mouse-over fixture exists"); IInputElement before = Mouse.DirectlyOver;
            try
            {
                changeOver.Invoke(Mouse.PrimaryDevice, new object[] { null, Environment.TickCount });
                changeOver.Invoke(Mouse.PrimaryDevice, new object[] { target, Environment.TickCount });
                Check((bool)Call(window, "IsCompactActionPointerOver") == expected, label);
            }
            finally { changeOver.Invoke(Mouse.PrimaryDevice, new object[] { before, Environment.TickCount }); StopTimers(window); }
        }
        private static void Refresh(IslandWindow window) { Call(window, "UpdateToolkitCompact"); window.UpdateLayout(); StopTimers(window); }
        private static void StopTimers(IslandWindow window)
        {
            foreach (FieldInfo field in typeof(IslandWindow).GetFields(Private)) if (field.FieldType == typeof(DispatcherTimer)) { var timer = (DispatcherTimer)field.GetValue(window); if (timer != null) timer.Stop(); }
            var focus = Read<FocusPanel>(window, "focusPanel"); if (focus != null) focus.Stop();
        }
        private static void Pump()
        {
            var frame = new DispatcherFrame(); var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1) };
            timer.Tick += (s, e) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
        }
        private static T Read<T>(object value, string field) { return (T)value.GetType().GetField(field, Private).GetValue(value); }
        private static void Write(object value, string field, object replacement) { value.GetType().GetField(field, Private).SetValue(value, replacement); }
        private static object Call(object value, string name, params object[] args)
        {
            try { return value.GetType().GetMethod(name, Private).Invoke(value, args); }
            catch (TargetInvocationException error) { throw error.InnerException ?? error; }
        }
        private static void Check(bool value, string label) { if (!value) throw new InvalidOperationException("Task activity composition: " + label); }
    }
}
