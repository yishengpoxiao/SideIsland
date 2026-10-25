using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace CodexIsland
{
    internal static class ProductivityV3Tests
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private sealed class Clock : IFocusClock { public DateTime Now = DateTime.UtcNow; public DateTime UtcNow { get { return Now; } } public TimeSpan Elapsed { get { return TimeSpan.Zero; } } }
        internal static void Run()
        {
            Commands(); Cadence(); Completion();
            string previous = Environment.GetEnvironmentVariable("CODEX_ISLAND_DATA_DIR"); string language = UiLocalization.Language, theme = UiTheme.Name;
            var context = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            string folder = Path.Combine(Path.GetTempPath(), "SideIsland.ProductivityV3." + Guid.NewGuid().ToString("N")); IslandWindow window = null;
            try
            {
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", folder); new SettingsStore().Save(new Settings { AutoMusic = false, NotificationsEnabled = false, AlwaysOnTop = false, AutoCollapse = false, HoverExpandEnabled = false });
                window = new IslandWindow(true) { ShowActivated = false, IsHitTestVisible = false }; window.Show(); window.UpdateLayout(); StopTimers(window);
                var settings = Read<Settings>(window, "settings"); var profiles = Read<LayoutProfileStore>(window, "layoutProfiles");
                Call(window, "SetFocusLayoutRestoration", true); Call(window, "ApplyLayoutProfile", "music"); Call(window, "ApplyLayoutProfile", "focus");
                Check(settings.QuietPreviews && profiles.Data.FocusRestoreArmed, "focus layout arms optional restoration");
                Call(window, "RestoreCompletedFocusLayout"); Check(settings.CompactMode == "music" && !settings.QuietPreviews && !profiles.Data.FocusRestoreArmed, "focus completion restores previous widgets and notifications");
                Call(window, "ApplyLayoutProfile", "focus"); settings.CompactMode = "clock"; Call(window, "RestoreCompletedFocusLayout");
                Check(settings.CompactMode == "clock" && !profiles.Data.FocusRestoreArmed, "manual layout changes survive completion");
                Call(window, "ApplyLayoutProfile", "music"); Call(window, "ApplyLayoutProfile", "focus"); Call(window, "ApplyLayoutProfile", "code"); Call(window, "RestoreCompletedFocusLayout");
                Check(settings.CompactSlot2 == "ports", "switching to another scene cancels temporary restoration");
                Call(window, "SelectToolsPage"); window.SetExpanded(true, false); Call(window, "SelectTool", "focus");
                var search = Read<TextBox>(window, "toolSearch"); search.Text = "专注 45 分钟"; Call(window, "ExecuteToolSearch");
                Check(Read<FocusPanel>(window, "focusPanel").IsRunning && settings.FocusMinutes == 45 && Read<string>(window, "selectedTool") == "focus", "focus command updates the real configured countdown and starts it");
                search.Text = "qr https://example.com/fixture"; Call(window, "ExecuteToolSearch");
                Check(Read<string>(window, "selectedTool") == "qr" && Read<TextBox>(Read<QrTransferPanel>(window, "qrTransferPanel"), "input").Text == "https://example.com/fixture", "QR command sends exact text to the existing generator");
                search.Text = "专注 0"; Call(window, "ExecuteToolSearch"); Check(settings.FocusMinutes == 45 && Read<string>(window, "selectedTool") == "qr", "invalid command cannot reset a running timer or replace the selected panel");
                search.Text = "hash"; Call(window, "FilterToolButtons"); Check(Read<TextBlock>(window, "toolCommandHint").Visibility == Visibility.Collapsed, "ordinary search removes stale command suggestions");
                Call(window, "ApplyRuntimePowerMode", PowerModes.Suspend); Check(Read<bool>(window, "runtimeSuspended") && !Read<DispatcherTimer>(window, "displayTimer").IsEnabled, "suspend stops presentation samplers");
                Call(window, "ApplyRuntimePowerMode", PowerModes.Resume); Check(!Read<bool>(window, "runtimeSuspended"), "resume reinstates the current presentation schedule"); StopTimers(window);
                Console.WriteLine("PASS: bounded direct commands, real focus/QR routes, temporary scene restoration/manual overrides, idle cadence and simulated power resume.");
            }
            finally { if (window != null) { StopTimers(window); window.Close(); } SynchronizationContext.SetSynchronizationContext(context); AppAppearance.Configure(language, theme); Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", previous); if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        }
        private static void Commands()
        {
            Check(ToolCommandParser.Parse("专注 45 分钟").Minutes == 45 && ToolCommandParser.Parse("focus 1440m").Minutes == 1440, "focus commands parse in both languages");
            Check(ToolCommandParser.Parse("focus 0").Error != null && ToolCommandParser.Parse("focus 99999999999").Error != null, "duration validation precedes UI actions");
            Check(ToolCommandParser.Parse("查端口8080、3000").Kind == "ports" && ToolCommandParser.Parse("port 65536").Error != null, "port commands share the authoritative parser");
            Check(ToolCommandParser.Parse("端口占用") == null && ToolCommandParser.Parse("专注计时") == null && ToolCommandParser.Parse("random") == null, "tool names remain ordinary searches");
            Check(ToolCommandParser.Parse("qr " + new String('中', 1000)).Error != null && ToolCommandParser.Parse("qr https://example.com?a=1").Argument == "https://example.com?a=1", "QR commands preserve content and reject byte overflow");
            Check(ToolCommandParser.Parse("powershell Remove-Item anything") == null && ToolCommandParser.Parse("复制模板 配置").Argument == "配置", "commands never form an arbitrary shell language");
            string language = UiLocalization.Language; try { UiLocalization.Configure("en"); Check(ToolCommandParser.Parse("QR transfer") == null && ToolCommandParser.Parse("Layout profiles") == null && ToolCommandParser.Parse("Port usage") == null, "exact visible tool names retain search semantics rather than becoming commands"); } finally { UiLocalization.Configure(language); }
        }
        private static void Cadence()
        { Check(RuntimeWorkloadPolicy.TelemetrySeconds(false, false, false) == 30 && RuntimeWorkloadPolicy.TelemetrySeconds(true, false, false) == 15 && RuntimeWorkloadPolicy.TelemetrySeconds(true, true, false) == 2 && RuntimeWorkloadPolicy.TelemetrySeconds(true, false, true) == 2, "idle telemetry slows without slowing visible metrics"); }
        private static void Completion()
        {
            var clock = new Clock(); var timer = new FocusTimer(clock); using (var panel = new FocusPanel(true, 1, timer))
            {
                int completed = 0; panel.FocusCompleted += () => completed++; panel.Start(); panel.StartFocus(1); clock.Now = clock.Now.AddMinutes(2); timer.Poll(); timer.Poll();
                Check(completed == 1, "focus completion fires exactly once across a simulated sleep deadline");
                timer.SetCountdown(TimeSpan.FromMinutes(1), "休息", false); timer.Start(); clock.Now = clock.Now.AddMinutes(2); timer.Poll(); Check(completed == 1, "break completion cannot restore a focus layout");
                Check(!Read<DispatcherTimer>(panel, "pulse").IsEnabled || Read<DispatcherTimer>(panel, "pulse").Interval.TotalMilliseconds >= 1000, "background countdown avoids a fast idle UI timer");
            }
        }
        private static void StopTimers(IslandWindow window) { foreach (var field in typeof(IslandWindow).GetFields(Fields)) if (field.FieldType == typeof(DispatcherTimer)) ((DispatcherTimer)field.GetValue(window)).Stop(); }
        private static T Read<T>(object instance, string name) { return (T)instance.GetType().GetField(name, Fields).GetValue(instance); }
        private static object Call(object instance, string name, params object[] args) { try { return instance.GetType().GetMethod(name, Fields).Invoke(instance, args); } catch (TargetInvocationException error) { throw error.InnerException ?? error; } }
        private static void Check(bool value, string message) { if (!value) throw new Exception("Productivity v3: " + message); }
    }
}
