using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace CodexIsland
{
    internal static class CompactWidgetTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        public static void Run() { RunModels(); RunWindow(); }

        internal static void RunModels()
        {
            string[] expectedKeys = { "codex-primary", "codex-secondary", "cpu", "memory", "battery", "clock", "focus", "awake", "notifications", "media", "ports", "activities" };
            Check(CompactWidgetCatalog.Keys.Length == 12 && CompactWidgetCatalog.Keys.Distinct().Count() == 12 && CompactWidgetCatalog.Keys.SequenceEqual(expectedKeys), "all twelve specific independent widget keys retain their stable order, including task activities");
            foreach (string key in CompactWidgetCatalog.Keys)
            {
                Check(CompactWidgetCatalog.Normalize(key, "codex-primary") == key, "catalog accepts " + key);
                Check(!String.IsNullOrWhiteSpace(CompactWidgetCatalog.LabelZhEn(key, "zh")) && !String.IsNullOrWhiteSpace(CompactWidgetCatalog.LabelZhEn(key, "en")), "both language labels for " + key);
            }
            Check(CompactWidgetCatalog.Normalize(null, "codex-primary") == "codex-primary" && CompactWidgetCatalog.Normalize("unknown", "codex-secondary") == "codex-secondary", "invalid choices preserve each slot's default");
            var data = new CompactWidgetData { LocalTime = new DateTime(2026, 10, 23, 17, 42, 3), PrimaryRemaining = 76, SecondaryRemaining = 48, PrimaryMinutes = 300, SecondaryMinutes = 10080, CpuPercent = 12, MemoryPercent = 64, BatteryPercent = 9, OnAc = false, FocusStatus = "专注 25:00", AwakeStatus = "唤醒 1:59:59", AwakeRunning = true, UnreadCount = 7 };
            var primary = CompactWidgetState.Create("codex-primary", data); var secondary = CompactWidgetState.Create("codex-secondary", data);
            Check(primary.Value == "76" && primary.Fraction == .76 && primary.Caption == "5h" && secondary.Value == "48" && secondary.Caption == "7d", "short and weekly quotas remain independent and show actual window duration");
            var cpu = CompactWidgetState.Create("cpu", data); var memory = CompactWidgetState.Create("memory", data);
            Check(cpu.Value == "12%" && cpu.Fraction == .12 && memory.Value == "64%" && memory.Fraction == .64, "CPU and memory show usage instead of quota remaining");
            Check(!cpu.Warning && CompactWidgetState.Create("battery", data).Warning, "usage and battery warnings use the correct direction");
            var clock = CompactWidgetState.Create("clock", data); var focus = CompactWidgetState.Create("focus", data); var awake = CompactWidgetState.Create("awake", data); var unread = CompactWidgetState.Create("notifications", data);
            Check(clock.Value == "17:42" && clock.Caption == "10/23" && !clock.ShowRing && !clock.Fraction.HasValue, "time uses a clock value and date without a percentage ring");
            Check(focus.Value == "25:00" && awake.Value == "1:59:59" && unread.Value == "7" && new[] { focus, awake, unread }.All(item => !item.ShowRing && !item.Fraction.HasValue), "timers and unread counts do not imply percentages");
            data.Language = "en"; data.AwakeRunning = false; data.OnAc = true; data.BatteryPercent = null;
            Check(CompactWidgetState.Create("awake", data).Value == "Off" && CompactWidgetState.Create("notifications", data).Caption == "Unread", "English widget states are translated");
            var ac = CompactWidgetState.Create("battery", data);
            Check(ac.Value == "AC" && !ac.ShowRing && !ac.Warning && ac.Tooltip.Contains("plugged in"), "desktop AC power does not invent a battery percentage");
            data.CpuPercent = 95; data.MemoryPercent = Double.NaN; data.PrimaryRemaining = Double.PositiveInfinity; data.QuotaStale = true;
            Check(CompactWidgetState.Create("cpu", data).Warning && CompactWidgetState.Create("memory", data).Value == "—", "high usage warns and nonfinite telemetry stays unknown");
            primary = CompactWidgetState.Create("codex-primary", data);
            Check(primary.Value == "—" && !primary.Fraction.HasValue && primary.ShowRing && primary.Stale, "unknown stale quota keeps the neutral ring without inventing data");
            data.CpuPercent = 999; data.BatteryPercent = -5; data.UnreadCount = -10; data.FocusStatus = "unavailable";
            Check(CompactWidgetState.Create("cpu", data).Value == "100%" && CompactWidgetState.Create("battery", data).Value == "0%", "percentage presentation clamps out-of-range input");
            Check(CompactWidgetState.Create("notifications", data).Value == "0" && CompactWidgetState.Create("focus", data).Value == "—", "invalid counts and timer status are safe");
            Console.WriteLine("PASS: complete compact widget catalog, data semantics, independent quotas, clock/timers/counts, bilingual states and missing telemetry.");
        }

        private static void RunWindow()
        {
            string previous = Environment.GetEnvironmentVariable("CODEX_ISLAND_DATA_DIR");
            string folder = Path.Combine(Path.GetTempPath(), "SideIsland.CompactWidgets." + Guid.NewGuid().ToString("N"));
            IslandWindow window = null;
            try
            {
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", folder);
                new SettingsStore().Save(new Settings { AlwaysOnTop = false, AutoCollapse = false, NotificationsEnabled = false, CompactSlot1 = "codex-secondary", CompactSlot2 = "cpu" });
                window = new IslandWindow(true) { ShowActivated = false, IsHitTestVisible = false };
                window.Loaded += (s, e) => StopTimers(window); window.Show(); window.UpdateLayout(); StopTimers(window);
                var settings = Read<Settings>(window, "settings");
                Call(window, "UpdateToolkitCompact"); window.SetExpanded(false, false); window.UpdateLayout();
                var first = Read<CompactWidgetVisual>(window, "compactWidget1"); var second = Read<CompactWidgetVisual>(window, "compactWidget2");
                var originalPrimary = Read<QuotaRing>(window, "primaryRing"); var originalSecondary = Read<QuotaRing>(window, "secondaryRing");
                double? firstQuota = originalPrimary.Remaining, secondQuota = originalSecondary.Remaining;
                Check(first.State.Key == "codex-secondary" && first.State.Caption == "7d" && second.State.Key == "cpu", "saved weekly-quota plus CPU composition is displayed on startup");
                int children = Read<StackPanel>(window, "compact").Children.Count;
                foreach (string left in CompactWidgetCatalog.Keys)
                foreach (string right in CompactWidgetCatalog.Keys)
                {
                    settings.CompactSlot1 = left; settings.CompactSlot2 = right;
                    double width = window.Width, height = window.Height;
                    window.UpdateCompactWidgets(); window.UpdateLayout();
                    Check(first.State.Key == left && second.State.Key == right, "arbitrary and duplicate slot composition " + left + "/" + right);
                    Check(window.Width == width && window.Height == height, "widget refresh keeps compact native dimensions stable");
                    Check(originalPrimary.Remaining == firstQuota && originalSecondary.Remaining == secondQuota, "custom slots retain original quota data");
                }
                Check(Read<StackPanel>(window, "compact").Children.Count == children, "refreshes reuse exactly two widget slots");
                foreach (string edge in new[] { "left", "right", "top", "bottom" })
                {
                    Call(window, "ChangeSide", edge); window.SetExpanded(false, false); window.UpdateLayout();
                    Check(Read<StackPanel>(window, "compact").Orientation == (edge == "top" || edge == "bottom" ? Orientation.Horizontal : Orientation.Vertical), "widget orientation follows " + edge);
                    Check(first.ActualWidth == 54 && first.ActualHeight == 67 && second.ActualWidth == 54 && second.ActualHeight == 67, "both widget footprints survive docking");
                    foreach (string direction in new[] { "auto", "rotate180", "mirror" })
                    {
                        Call(window, "SetCompactTextDirection", direction);
                        Check(Read<string>(first, "direction") == direction && Read<string>(second, "direction") == direction, "text direction updates both slots immediately");
                    }
                }
                settings.CompactMode = "clock"; Call(window, "UpdateToolkitCompact");
                Check(Read<Grid>(window, "compactSlot1").Visibility == Visibility.Collapsed && Read<StackPanel>(window, "compactAlternate").Visibility == Visibility.Visible, "legacy large clock mode stays compatible");
                settings.CompactMode = "focus"; Call(window, "UpdateToolkitCompact");
                Check(Read<Grid>(window, "compactSlot2").Visibility == Visibility.Collapsed, "legacy focus mode stays compatible");
                settings.CompactMode = "quota"; settings.Language = "en"; settings.Theme = "light"; settings.CompactSlot1 = "notifications"; settings.CompactSlot2 = "awake"; Call(window, "UpdateToolkitCompact");
                Check(first.State.Caption == "Unread" && second.State.Value == "Off" && Read<bool>(first, "light"), "both widgets immediately use language and light theme");
                Check(Read<Grid>(window, "compactSlot1").Visibility == Visibility.Visible && originalPrimary.Visibility == Visibility.Visible, "returning to widgets restores slots and legacy ring visibility");
                Console.WriteLine("PASS: all " + (CompactWidgetCatalog.Keys.Length * CompactWidgetCatalog.Keys.Length) + " two-slot compositions, persisted weekly quota, fixed dimensions, four edges, text directions, legacy modes and language/theme refresh.");
            }
            finally
            {
                if (window != null) { StopTimers(window); window.Close(); }
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", previous);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }
        private static void StopTimers(IslandWindow window) { foreach (var field in typeof(IslandWindow).GetFields(Private)) if (field.FieldType == typeof(DispatcherTimer)) ((DispatcherTimer)field.GetValue(window)).Stop(); }
        private static T Read<T>(object value, string name) { return (T)value.GetType().GetField(name, Private).GetValue(value); }
        private static object Call(object value, string name, params object[] args) { return value.GetType().GetMethod(name, Private).Invoke(value, args); }
        private static void Check(bool value, string message) { if (!value) throw new Exception("Compact widgets: " + message); }
    }
}
