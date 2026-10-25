using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CodexIsland
{
    internal static class ReminderTests
    {
        public static void Run()
        {
            DeliveryTests(); PauseTests(); SaveFailureTests(); CalendarTests(); StoreTests(); DemoTests();
        }
        private static void DeliveryTests()
        {
            DateTime now = new DateTime(2026, 10, 22, 0, 0, 0, DateTimeKind.Utc);
            List<ReminderItem> disk = new List<ReminderItem>();
            var book = new ReminderBook(disk, data => disk = data, () => now, () => TimeZoneInfo.Utc);
            book.AddMinutes("Once", 10);
            book.AddLocal("Daily", new DateTime(2026, 10, 22, 0, 20, 0), true);
            now = now.AddMinutes(9);
            Check(book.Poll().Count == 0, "A reminder never fires early");
            now = now.AddDays(4);
            var due = book.Poll();
            Check(due.Count == 2 && due.Any(x => x.Title == "Once") && due.Any(x => x.Title == "Daily"), "Wake after four days emits one delivery per reminder");
            Check(book.Poll().Count == 0, "A second tick does not repeat delivery");
            Check(disk.First(x => x.Title == "Once").Completed && !disk.First(x => x.Title == "Once").Enabled, "One-shot completion is durable before delivery");
            var daily = disk.First(x => x.Title == "Daily");
            Check(daily.DueUtc == new DateTime(2026, 10, 26, 0, 20, 0, DateTimeKind.Utc), "Missed daily occurrences advance directly to next future occurrence");
            var restarted = new ReminderBook(disk, data => disk = data, () => now, () => TimeZoneInfo.Utc);
            Check(restarted.Poll().Count == 0, "A restart after delivery cannot repeat notifications");
            now = daily.DueUtc;
            Check(restarted.Poll().Count == 1 && restarted.Poll().Count == 0, "Daily fires once at its next precise deadline");
            now = now.AddHours(-2);
            Check(restarted.Poll().Count == 0, "A backwards wall-clock correction cannot duplicate a delivered reminder");
            bool rejected = false; try { restarted.AddLocal("Past", now.AddMinutes(-1), false); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "New one-shot reminders cannot be scheduled in the past");
        }
        private static void PauseTests()
        {
            DateTime now = new DateTime(2026, 10, 22, 0, 0, 0, DateTimeKind.Utc);
            var book = new ReminderBook(null, delegate { }, () => now, () => TimeZoneInfo.Utc);
            book.AddMinutes("Paused", 10); string id = book.Items[0].Id, explanation;
            book.SetEnabled(id, false, out explanation); now = now.AddHours(2);
            Check(book.Poll().Count == 0, "Paused reminders never trigger");
            book.SetEnabled(id, true, out explanation);
            Check(book.Items[0].DueUtc == now.AddMinutes(10) && explanation.Contains("10") && book.Poll().Count == 0, "Resuming an expired one-shot explicitly schedules ten minutes later");
            now = now.AddMinutes(10); Check(book.Poll().Count == 1, "Resumed one-shot fires at the new deadline");
            book.AddLocal("Paused daily", now.AddHours(1), true); id = book.Items[1].Id;
            book.SetEnabled(id, false, out explanation); now = now.AddDays(5);
            book.SetEnabled(id, true, out explanation);
            Check(book.Items[1].DueUtc > now && book.Poll().Count == 0, "Resumed daily reminders skip paused occurrences instead of firing immediately");
            var copy = book.Items; copy[0].Title = "mutated"; copy.Clear();
            Check(book.Items.Count == 2 && book.Items[0].Title == "Paused", "Callers cannot mutate persisted scheduling state via snapshots");
            while (book.Items.Count < ReminderStore.MaxItems) book.AddMinutes("Bounded", 5);
            bool rejected = false; try { book.AddMinutes("Too many", 5); } catch (ArgumentException) { rejected = true; }
            Check(rejected && book.Items.Count == ReminderStore.MaxItems, "Reminder count is bounded");
        }
        private static void SaveFailureTests()
        {
            DateTime now = new DateTime(2026, 10, 22, 0, 0, 0, DateTimeKind.Utc); bool fail = false; int writes = 0;
            List<ReminderItem> disk = null;
            var book = new ReminderBook(null, data => { writes++; if (fail) throw new IOException("Simulated disk failure"); disk = data; }, () => now, () => TimeZoneInfo.Utc);
            book.AddMinutes("Disk recovery", 1); fail = true; now = now.AddMinutes(1);
            Check(book.Poll().Count == 0 && book.Dirty && book.LastError != null && !book.Items[0].Completed, "Failed completion save defers notification without losing due state");
            int attempts = writes;
            for (int i = 0; i < 20; i++) { now = now.AddSeconds(1); book.Poll(); }
            Check(writes == attempts, "Storage failure backs off rather than writing every timer tick");
            Check(!book.TryFlush() && book.Dirty, "Exit flush truthfully reports unsaved state");
            fail = false; now = now.AddSeconds(31);
            Check(book.Poll().Count == 1 && !book.Dirty && book.LastError == null && disk[0].Completed, "Disk recovery persists completion and emits one deferred delivery");
            Check(book.Poll().Count == 0, "Recovered delivery cannot repeat");
            fail = true;
            Check(!book.AddMinutes("Unsaved edit", 10) && book.Dirty && book.Items.Count == 2, "User edits survive in memory when saving fails");
            fail = false;
            Check(book.TryFlush() && !book.Dirty && disk.Count == 2, "Retry flush preserves unsaved edits");
        }
        private static void CalendarTests()
        {
            var begin = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday);
            var end = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday);
            var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2020, 1, 1), new DateTime(2030, 12, 31), TimeSpan.FromHours(1), begin, end);
            var eastern = TimeZoneInfo.CreateCustomTimeZone("Reminder-Test-Zone", TimeSpan.FromHours(-5), "Test", "Test Standard", "Test Daylight", new[] { rule });
            Check(ReminderBook.LocalToUtc(new DateTime(2026, 3, 8, 2, 30, 0), eastern) == new DateTime(2026, 3, 8, 7, 0, 0, DateTimeKind.Utc), "A spring-forward gap shifts to the first valid local minute");
            Check(ReminderBook.LocalToUtc(new DateTime(2026, 11, 1, 1, 30, 0), eastern) == new DateTime(2026, 11, 1, 6, 30, 0, DateTimeKind.Utc), "A repeated autumn time chooses the later occurrence exactly once");
            var before = new DateTime(2026, 3, 7, 14, 0, 0, DateTimeKind.Utc);
            Check(ReminderBook.NextDaily(before, 9, 0, eastern) == new DateTime(2026, 3, 8, 13, 0, 0, DateTimeKind.Utc), "Daily local time remains nine AM across daylight savings, not a fixed 24-hour interval");
            var china = TimeZoneInfo.CreateCustomTimeZone("Reminder-UTC8", TimeSpan.FromHours(8), "UTC8", "UTC8");
            Check(ReminderBook.NextDaily(new DateTime(2026, 10, 22, 16, 1, 0, DateTimeKind.Utc), 0, 0, china) == new DateTime(2026, 10, 23, 16, 0, 0, DateTimeKind.Utc), "Calendar rollover uses local midnight rather than UTC date");
        }
        private static void StoreTests()
        {
            string directory = Path.Combine(Path.GetTempPath(), "SideIslandReminderTests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "reminders.json");
            try
            {
                var store = new ReminderStore(path); Check(store.Load().Count == 0 && !File.Exists(path), "Loading absent reminders does not write files");
                var book = new ReminderBook(null, store.Save); book.AddMinutes("中文提醒", 10); var first = store.Load();
                Check(first.Count == 1 && first[0].Title == "中文提醒" && first[0].DueUtcTicks == book.Items[0].DueUtcTicks && first[0].Enabled, "Reminder content and UTC deadline survive restart without precision loss");
                string explanation; book.SetEnabled(first[0].Id, false, out explanation);
                Check(!store.Load()[0].Enabled && Directory.GetFiles(directory, "*.tmp").Length == 0, "Atomic replacement preserves pause state and leaves no temporary file");
                string malformed = "{bad json"; File.WriteAllText(path, malformed);
                Check(store.Load().Count == 0 && store.LoadFailed && File.ReadAllText(path) == malformed, "Unreadable data is not changed on load");
                store.Save(first); string[] backups = Directory.GetFiles(directory, "*.bak");
                Check(backups.Length == 1 && File.ReadAllText(backups[0]) == malformed && store.Load().Count == 1, "A later edit preserves unreadable source in a recovery backup");
                File.WriteAllText(path, "[{\"Id\":\"invalid\",\"Title\":\"x\",\"DueUtcTicks\":-4}]");
                Check(store.Load().Count == 0 && store.LoadFailed, "Invalid IDs and dates never enter active scheduling");
                File.WriteAllBytes(path, new byte[ReminderStore.MaxFileBytes + 1]);
                Check(store.Load().Count == 0 && store.LoadFailed, "Oversized local data is rejected before deserialization");
            }
            finally { Directory.Delete(directory, true); }
        }
        private static void DemoTests()
        {
            string previous = Environment.GetEnvironmentVariable("CODEX_ISLAND_DATA_DIR");
            string directory = Path.Combine(Path.GetTempPath(), "SideIslandReminderDemo-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", directory);
            try
            {
                var panel = new ReminderPanel(true); int notifications = 0; panel.OnNotify += (a, b) => notifications++;
                panel.Start(); Check(panel.TryFlush(), "Demo flush succeeds without storage"); panel.Dispose();
                Check(notifications == 0 && !Directory.Exists(directory), "Demo neither reads/writes application data nor emits notification events");
            }
            finally { Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", previous); }
        }
        private static void Check(bool condition, string message) { if (!condition) throw new Exception("Reminders: " + message); }
    }
}
