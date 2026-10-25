using System;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace CodexIsland
{
    internal static class RuntimeWorkloadPolicy
    {
        internal static int TelemetrySeconds(bool visible, bool expanded, bool widget)
        { return !visible ? 30 : expanded || widget ? 2 : 15; }
    }
    internal partial class IslandWindow
    {
        private bool runtimeStarted, runtimeSuspended, runtimeTelemetryReading;
        private int runtimeTelemetryGeneration;
        private string migrationError;
        private void PrepareSettingsMigration()
        {
            if (demo) return;
            try { new SettingsBackupService(System.IO.Path.GetDirectoryName(store.FilePath)).ApplyPending(); }
            catch (Exception error) { migrationError = "待导入设置未能应用，已保留原偏好；可在设置迁移中取消或重试。"; StartupDiagnostics.Failure(error); }
        }
        private void StartRuntimePerformance()
        {
            if (runtimeStarted) return; runtimeStarted = true;
            if (!demo) SystemEvents.PowerModeChanged += OnRuntimePowerChanged;
            ConfigureRuntimeWorkload();
            if (migrationError != null && toolkitHint != null) toolkitHint.Text = migrationError;
        }
        private void StopRuntimePerformance()
        {
            if (!runtimeStarted) return; runtimeStarted = false; runtimeTelemetryGeneration++;
            if (!demo) SystemEvents.PowerModeChanged -= OnRuntimePowerChanged;
        }
        private void ConfigureRuntimeWorkload()
        {
            if (!runtimeStarted || shuttingDown) return;
            bool widget = WidgetPoolContains("cpu") || WidgetPoolContains("memory") || WidgetPoolContains("battery") || (!ShowCompactMusic && settings.CompactMode == "quota" && (settings.CompactSlot1 == "cpu" || settings.CompactSlot2 == "cpu" || settings.CompactSlot1 == "memory" || settings.CompactSlot2 == "memory" || settings.CompactSlot1 == "battery" || settings.CompactSlot2 == "battery"));
            bool statusVisible = expanded && !toolsPageSelected && !notificationPageSelected;
            var interval = TimeSpan.FromSeconds(RuntimeWorkloadPolicy.TelemetrySeconds(IsVisible, statusVisible, widget));
            if (displayTimer.Interval != interval) displayTimer.Interval = interval;
            if (runtimeSuspended) displayTimer.Stop(); else displayTimer.Start();
            if (IsVisible && !runtimeSuspended) compactClock.Start(); else compactClock.Stop();
        }
        private async void RefreshRuntimeTelemetry()
        {
            if (demo || shuttingDown || runtimeSuspended || runtimeTelemetryReading) return;
            runtimeTelemetryReading = true; int version = runtimeTelemetryGeneration;
            try
            {
                var value = await Task.Run(() => SystemStatsReader.Read());
                if (!shuttingDown && !runtimeSuspended && version == runtimeTelemetryGeneration) { stats = value; UpdateDisplay(); }
            }
            catch (Exception error) { StartupDiagnostics.Failure(error); }
            finally { runtimeTelemetryReading = false; }
        }
        private void OnRuntimePowerChanged(object sender, PowerModeChangedEventArgs args)
        {
            if (shuttingDown || Dispatcher.HasShutdownStarted) return;
            try { Dispatcher.BeginInvoke(new Action(() => ApplyRuntimePowerMode(args.Mode))); } catch (InvalidOperationException) { }
        }
        private void ApplyRuntimePowerMode(PowerModes mode)
        {
            if (!runtimeStarted || shuttingDown) return;
            if (mode == PowerModes.Suspend)
            {
                runtimeSuspended = true; runtimeTelemetryGeneration++; displayTimer.Stop(); compactClock.Stop();
                musicUiTimer.Stop(); SettleCompactMediaContentTransition();
                if (nowPlaying != null) nowPlaying.Stop(); if (mediaPanel != null) mediaPanel.Stop(); if (systemDashboard != null) systemDashboard.Stop();
            }
            else if (mode == PowerModes.Resume)
            {
                runtimeSuspended = false; ConfigureRuntimeWorkload(); SyncToolkitActivity();
                RefreshRuntimeTelemetry(); if (!demo) { Refresh(); PollNotifications(); }
            }
        }
    }
}
