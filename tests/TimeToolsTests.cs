using System;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace CodexIsland
{
    internal static class TimeToolsTests
    {
        private static void Check(bool value, string label) { if (!value) throw new Exception("Date/time tools: " + label); }
        private static void Throws(Action action, string label) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Date/time tools: expected rejection for " + label); }
        private static DateTime D(string date) { return TimeTools.ParseDate(date); }
        public static void Run()
        {
            Check(D("2024-02-29").Day == 29, "leap day accepted");
            foreach (string value in new[] { "2023-02-29", "2026/10/22", "2026-1-2", "2026-04-31", "", "10000-01-01" }) Throws(() => D(value), "strict calendar input " + value);
            Check(TimeTools.DaysBetween(D("2024-02-28"), D("2024-03-01"), false, false) == 2, "leap day contributes to elapsed days");
            Check(TimeTools.DaysBetween(D("2024-02-28"), D("2024-03-01"), true, false) == 3, "inclusive endpoints");
            Check(TimeTools.DaysBetween(D("2026-10-22"), D("2026-10-22"), false, false) == 0, "same day exclusive");
            Check(TimeTools.DaysBetween(D("2026-10-22"), D("2026-10-22"), true, false) == 1, "same day inclusive");
            Check(TimeTools.DaysBetween(D("2026-10-24"), D("2026-10-24"), true, true) == 0, "inclusive Saturday excluded from weekdays");
            Check(TimeTools.DaysBetween(D("2026-10-23"), D("2026-10-26"), false, true) == 1, "Friday to Monday weekdays exclusive");
            Check(TimeTools.DaysBetween(D("2026-10-23"), D("2026-10-26"), true, true) == 2, "Friday to Monday weekdays inclusive");
            Throws(() => TimeTools.DaysBetween(D("2026-10-23"), D("2026-10-22"), false, false), "reversed interval");
            Check(TimeTools.DaysBetween(DateTime.MinValue, DateTime.MaxValue, true, false) == 3652059, "entire Gregorian date range");
            Check(TimeTools.AddDays(D("2024-02-28"), 1, false) == D("2024-02-29"), "addition across leap day");
            Check(TimeTools.AddDays(D("2026-10-24"), 0, true) == D("2026-10-24"), "zero weekday offset retains weekend");
            Check(TimeTools.AddDays(D("2026-10-24"), 5, true) == D("2026-10-30"), "positive weekend starts skip Saturday");
            Check(TimeTools.AddDays(D("2026-10-25"), -5, true) == D("2026-10-19"), "negative weekend starts skip Sunday");
            Throws(() => TimeTools.AddDays(DateTime.MaxValue, 1, false), "upper date overflow");
            Throws(() => TimeTools.AddDays(DateTime.MinValue, -1, true), "lower weekday overflow");
            Throws(() => TimeTools.AddDays(D("2026-10-22"), Int32.MinValue, true), "large negative business offset");
            Throws(() => TimeTools.AddDays(D("2026-10-22"), Int32.MaxValue, true), "large positive business offset");
            Check(TimeTools.ParseDays("-15") == -15, "negative day parsing"); Throws(() => TimeTools.ParseDays("1.5"), "fractional day parsing");
            CalendarAgainstReference();

            Check(TimeTools.FromUnix("0", false).UtcDateTime == new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc), "Unix epoch");
            Check(TimeTools.FromUnix("-1", false).UtcDateTime == new DateTime(1969, 12, 31, 23, 59, 59, DateTimeKind.Utc), "negative seconds");
            Check(TimeTools.FromUnix("-1", true).UtcDateTime == new DateTime(1969, 12, 31, 23, 59, 59, 999, DateTimeKind.Utc), "negative milliseconds");
            Check(TimeTools.ToUnix(TimeTools.FromUnix("-1", true), false) == -1, "pre-epoch fractions floor instead of truncating");
            foreach (long value in new[] { -62135596800000L, -1234567890L, -1L, 0L, 1L, 1234567890123L, 253402300799999L })
                Check(TimeTools.ToUnix(TimeTools.FromUnix(value.ToString(CultureInfo.InvariantCulture), true), true) == value, "exact millisecond roundtrip " + value);
            foreach (string value in new[] { "1.5", "1e3", "NaN", "", "9223372036854775808", "253402300800", "-62135596801", Int64.MaxValue.ToString(), Int64.MinValue.ToString() }) Throws(() => TimeTools.FromUnix(value, false), "timestamp bounds " + value);
            Check(TimeTools.ParseTime("2024-02-29 23:59:59.999").Millisecond == 999, "millisecond datetime accepted");
            foreach (string value in new[] { "2024-02-29", "2024-02-29T23:59:59Z", "2024-02-29 24:00:00", "2024-02-29 23:59:60" }) Throws(() => TimeTools.ParseTime(value), "strict time input");
            var east = TimeZoneInfo.CreateCustomTimeZone("TestPlus8", TimeSpan.FromHours(8), "Test +8", "Test +8");
            Check(TimeTools.ToUnix(TimeTools.ToUtc(TimeTools.ParseTime("1970-01-01 08:00:00"), east), false) == 0, "wall clock converted with explicit offset");
            Throws(() => TimeTools.ToUtc(DateTime.MinValue, east), "UTC range underflow");
            var west = TimeZoneInfo.CreateCustomTimeZone("TestMinus8", TimeSpan.FromHours(-8), "Test -8", "Test -8");
            Throws(() => TimeTools.ToUtc(DateTime.MaxValue, west), "UTC range overflow");
            Check(TimeTools.FormatInstant(TimeTools.FromUnix("-62135596800000", true), west).Contains("超出"), "local time overflow never clamps silently");
            Check(TimeTools.FormatInstant(TimeTools.FromUnix("0", false), null).Contains("未提供"), "missing time zone is explained");
            Check(TimeTools.FindZone("SideIsland/NotAnInstalledZone") == null, "unknown Windows zone fails gracefully");
            Check(TimeTools.FormatOffset(TimeSpan.FromMinutes(-210)) == "UTC−03:30", "negative fractional hour offset");
            TestDst(); PanelTests();
            Console.WriteLine("PASS: calendar endpoints, weekdays, leap years, Unix precision/bounds, DST ambiguity and WPF date/time controls.");
        }
        private static void CalendarAgainstReference()
        {
            var random = new Random(4021);
            for (int i = 0; i < 350; i++)
            {
                var start = new DateTime(2000, 1, 1).AddDays(random.Next(0, 20000)); int amount = random.Next(-700, 701);
                DateTime reference = start; int left = Math.Abs(amount), direction = amount < 0 ? -1 : 1;
                while (left > 0) { reference = reference.AddDays(direction); if (reference.DayOfWeek != DayOfWeek.Saturday && reference.DayOfWeek != DayOfWeek.Sunday) left--; }
                Check(TimeTools.AddDays(start, amount, true) == reference, "weekday skip agrees with daily enumeration");
                var end = start.AddDays(random.Next(0, 700)); bool inclusive = i % 2 == 0; int count = 0;
                for (var day = inclusive ? start : start.AddDays(1); day <= end; day = day.AddDays(1)) if (day.DayOfWeek != DayOfWeek.Saturday && day.DayOfWeek != DayOfWeek.Sunday) count++;
                Check(TimeTools.DaysBetween(start, end, inclusive, true) == count, "weekday interval agrees with daily enumeration");
            }
        }
        private static void TestDst()
        {
            var start = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday);
            var end = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday);
            var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2020, 1, 1), new DateTime(2030, 12, 31), TimeSpan.FromHours(1), start, end);
            var zone = TimeZoneInfo.CreateCustomTimeZone("TestDst", TimeSpan.FromHours(-5), "Test eastern", "Test standard", "Test daylight", new[] { rule });
            Throws(() => TimeTools.ToUtc(TimeTools.ParseTime("2026-03-08 02:30:00"), zone), "spring missing time");
            Throws(() => TimeTools.ToUtc(TimeTools.ParseTime("2026-11-01 01:30:00"), zone), "autumn ambiguous time");
            Check(TimeTools.ToUtc(TimeTools.ParseTime("2026-07-01 12:00:00"), zone).Hour == 16, "daylight adjustment included");
            Check(TimeTools.ToUtc(TimeTools.ParseTime("2026-01-01 12:00:00"), zone).Hour == 17, "standard adjustment included");
        }
        private static T Field<T>(object owner, string name) { return (T)owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(owner); }
        private static void PanelTests()
        {
            using (var panel = new TimeToolsPanel(true))
            {
                var mode = Field<ComboBox>(panel, "mode"); var result = Field<TextBlock>(panel, "result"); var copy = Field<Button>(panel, "copy");
                Check(mode.SelectedIndex == 0 && result.Visibility == Visibility.Collapsed, "default world clock omits irrelevant copy controls");
                panel.Start(); panel.Start(); Check(Field<DispatcherTimer>(panel, "timer").IsEnabled, "idempotent timer start");
                panel.Stop(); panel.Stop(); Check(!Field<DispatcherTimer>(panel, "timer").IsEnabled, "timer stops when module hidden");
                mode.SelectedIndex = 1; Field<TextBox>(panel, "startDate").Text = "2024-02-28"; Field<TextBox>(panel, "endDate").Text = "2024-03-01";
                Check(result.Text == "2 天" && copy.IsEnabled, "actual controls calculate days");
                Field<CheckBox>(panel, "includeStart").IsChecked = true; Check(result.Text == "3 天", "checkbox immediately updates endpoint rule");
                Field<TextBox>(panel, "endDate").Text = "2023-02-29";
                Check(Field<string>(panel, "copyValue") == null && !copy.IsEnabled && result.Text == "—", "invalid date invalidates old copy result");
                mode.SelectedIndex = 2; Field<TextBox>(panel, "baseDate").Text = "2026-10-23"; Field<TextBox>(panel, "addAmount").Text = "1"; Field<CheckBox>(panel, "addWeekdays").IsChecked = true;
                Check(result.Text == "2026-10-26", "weekday addition controls update immediately");
                Field<TextBox>(panel, "addAmount").Text = "2147483647"; Check(!copy.IsEnabled, "large date operation cannot retain stale result");
                mode.SelectedIndex = 3; Field<TextBox>(panel, "unixInput").Text = "-1"; Field<ComboBox>(panel, "unixUnit").SelectedIndex = 1;
                Check(result.Text.Contains("1969-12-31 23:59:59.999"), "Unix millisecond controls preserve pre-epoch precision");
                Field<ComboBox>(panel, "unixDirection").SelectedIndex = 1; Field<TextBox>(panel, "timeInput").Text = "1970-01-01 00:00:00.001";
                Check(result.Text == "1", "explicit UTC datetime controls convert to milliseconds");
                Field<ComboBox>(panel, "unixUnit").SelectedIndex = 0; Check(result.Text == "0", "unit switch invalidates and recomputes result");
                Field<TextBox>(panel, "timeInput").Text = "1969-12-31 23:59:59.999"; Check(result.Text == "-1", "actual controls floor negative fractional seconds");
                copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Check(Field<TextBlock>(panel, "status").Text.Contains("演示"), "demo copy never writes real clipboard");
                Field<TextBox>(panel, "timeInput").Text = "invalid"; Check(!copy.IsEnabled && Field<string>(panel, "copyValue") == null, "invalid time clears output before copy");
                panel.GetType().GetMethod("FillNow", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(panel, null);
                Check(Field<TextBox>(panel, "timeInput").Text == "2026-10-22 09:30:00.000" && copy.IsEnabled, "current-time button uses the explicitly selected UTC zone and repairs invalid input");
                panel.Dispose(); panel.Start(); Check(!Field<DispatcherTimer>(panel, "timer").IsEnabled, "disposed timer cannot restart");
            }
        }
    }
}
