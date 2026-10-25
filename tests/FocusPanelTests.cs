using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace CodexIsland
{
    internal static class FocusPanelTests
    {
        private sealed class Clock : IFocusClock
        {
            private DateTime now = new DateTime(2026, 10, 23, 0, 0, 0, DateTimeKind.Utc);
            private TimeSpan elapsed;
            public DateTime UtcNow { get { return now; } }
            public TimeSpan Elapsed { get { return elapsed; } }
            internal void Advance(TimeSpan value) { now += value; elapsed += value; }
        }

        public static void Run()
        {
            var clock = new Clock(); var timer = new FocusTimer(clock);
            using (var panel = new FocusPanel(true, 37, timer))
            {
                int saved = 0, saves = 0, notifications = 0;
                panel.FocusMinutesChanged += value => { saved = value; saves++; };
                panel.OnNotify += (title, message) => notifications++;
                Check(panel.FocusMinutes == 37 && timer.Value == TimeSpan.FromMinutes(37) && !panel.IsRunning, "saved focus duration is ready without auto-starting");
                panel.ToggleQuickFocus(); clock.Advance(TimeSpan.FromMinutes(2)); panel.ToggleQuickFocus();
                Check(!timer.IsRunning && timer.Value == TimeSpan.FromMinutes(35) && panel.ActiveTimerLabel == "专注", "quick click starts and pauses the existing focus");
                clock.Advance(TimeSpan.FromMinutes(3));
                Check(timer.Value == TimeSpan.FromMinutes(35), "paused focus does not consume time");
                panel.ToggleQuickFocus(); clock.Advance(TimeSpan.FromMinutes(35)); timer.Poll();
                Check(timer.IsCompleted && timer.CompletedFocusSessions == 1 && notifications == 1, "quick-started focus completes and notifies exactly once");
                panel.ToggleQuickFocus();
                Check(timer.IsRunning && timer.Value == TimeSpan.FromMinutes(37), "completed focus restarts with its saved duration");

                var input = Field<TextBox>(panel, "customMinutes"); var apply = Field<Button>(panel, "applyButton");
                input.Text = "73"; Click(apply);
                Check(panel.FocusMinutes == 73 && saved == 73 && saves == 1 && !timer.IsRunning && timer.Value == TimeSpan.FromMinutes(73), "explicit save resets to a custom focus and publishes persistence event");
                Check(Field<Button>(panel, "focusPreset").Content.ToString().Contains("73"), "focus preset reflects the custom duration");
                panel.ToggleQuickFocus();
                foreach (string invalid in new[] { "", "0", "-1", "1441", "1.5", "hello" })
                {
                    input.Text = invalid; Click(apply);
                    Check(panel.FocusMinutes == 73 && saves == 1 && timer.IsRunning && timer.Value == TimeSpan.FromMinutes(73), "invalid value preserves running focus: " + invalid);
                }
                input.Text = "1"; Click(apply); panel.ToggleQuickFocus(); clock.Advance(TimeSpan.FromMinutes(1)); timer.Poll();
                Check(timer.CompletedFocusSessions == 2 && notifications == 2, "custom one-minute countdown is counted as a focus session");
                input.Text = "1440"; Click(apply);
                Check(panel.FocusMinutes == 1440 && timer.Value == TimeSpan.FromHours(24) && !timer.IsRunning, "custom duration accepts the 24-hour maximum");
                Click(apply); Check(saves == 3, "saving unchanged duration does not emit duplicate preference changes");

                var presets = panel.Children.OfType<Grid>().First(); Click((Button)presets.Children[1]);
                panel.ToggleQuickFocus(); clock.Advance(TimeSpan.FromMinutes(1)); panel.ToggleQuickFocus();
                Check(panel.ActiveTimerLabel == "休息" && !timer.IsRunning && timer.Value == TimeSpan.FromMinutes(4) && panel.FocusMinutes == 1440, "quick action pauses a selected break without replacing it with focus");
                panel.ToggleQuickFocus(); clock.Advance(TimeSpan.FromMinutes(4)); timer.Poll();
                Check(timer.CompletedFocusSessions == 2 && notifications == 3, "break completion does not inflate focus sessions");
                panel.ToggleQuickFocus(); Check(timer.Value == TimeSpan.FromMinutes(5), "completed break restarts as a break");
                var customActions = (Panel)apply.Parent; Click(customActions.Children.OfType<Button>().Single(button => !Object.ReferenceEquals(button, apply)));
                panel.ToggleQuickFocus(); clock.Advance(TimeSpan.FromSeconds(12)); panel.ToggleQuickFocus();
                Check(timer.IsStopwatch && !timer.IsRunning && timer.Value == TimeSpan.FromSeconds(12) && panel.ActiveTimerLabel == "秒表", "quick action controls selected stopwatch without a silent reset");
                panel.ToggleQuickFocus(); clock.Advance(TimeSpan.FromSeconds(3)); panel.ToggleQuickFocus();
                Check(timer.Value == TimeSpan.FromSeconds(15), "quick stopwatch resumes accumulated elapsed time");
                Click(Field<Button>(panel, "focusPreset"));
                Check(!timer.IsStopwatch && timer.Value == TimeSpan.FromHours(24) && !timer.IsRunning, "focus preset restores the saved custom duration");
            }
            using (var defaults = new FocusPanel(true)) Check(defaults.FocusMinutes == 25, "legacy constructor retains the 25-minute default");
            using (var invalid = new FocusPanel(true, 0)) Check(invalid.FocusMinutes == 25, "invalid saved duration recovers to a valid default");
            Console.WriteLine("PASS: custom focus preferences, 1–1440 minute bounds, ready/pause/resume/restart, counted focus sessions and non-destructive break/stopwatch quick actions.");
        }

        private static T Field<T>(object value, string name) { return (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value); }
        private static void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
        private static void Check(bool value, string message) { if (!value) throw new Exception("Focus panel: " + message); }
    }
}
