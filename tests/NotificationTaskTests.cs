using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;

namespace CodexIsland
{
    internal static class NotificationTaskTests
    {
        internal static void Run()
        {
            string previous = Environment.GetEnvironmentVariable("CODEX_ISLAND_DATA_DIR"), folder = Path.Combine(Path.GetTempPath(), "SideIsland.NotificationTaskTests." + Guid.NewGuid().ToString("N"));
            try
            {
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", folder);
                var book = new NotificationTaskBook(false); DateTime now = new DateTime(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc);
                var notification = new IslandNotification { AppId = "fixture.app", Id = "a", AppName = "Fixture", Title = "需要回复😀", Body = "原始消息\n第二行", CreatedAt = now };
                var task = book.Add(notification, 15, now); book.Add(notification, 0, now);
                Check(book.Snapshot().Count == 1 && task.Body == notification.Body, "same notification adds idempotently and preserves source content");
                notification.Title = "changed in feed"; Check(book.Snapshot()[0].Title == "需要回复😀", "saved task does not reference mutable notification");
                book.SetProject(task.Id, "Project A"); book.SetPinned(task.Id, true);
                var reloaded = new NotificationTaskBook(false); Check(reloaded.Snapshot()[0].Project == "Project A" && reloaded.Snapshot()[0].Pinned && reloaded.Snapshot()[0].DueUtcTicks == now.AddMinutes(15).Ticks, "project/pin/due survive actual disk reload");
                Check(reloaded.TakeDue(now.AddMinutes(14)).Count == 0, "not reminded before due");
                Check(reloaded.TakeDue(now.AddMinutes(15)).Count == 1 && reloaded.TakeDue(now.AddHours(1)).Count == 0, "due reminder delivered once");
                Check(new NotificationTaskBook(false).TakeDue(now.AddDays(1)).Count == 0, "delivery marker persists across restart");
                reloaded.SetDone(task.Id, true); reloaded.Add(new IslandNotification { AppId = "x", Id = "b", AppName = "Other", Title = "other", CreatedAt = now }, 0, now);
                Check(reloaded.Snapshot()[0].Title == "other", "open tasks sort before completed pinned tasks");
                bool invalid = false; try { reloaded.SetProject(task.Id, new string('x', 61)); } catch (ArgumentException) { invalid = true; }
                Check(invalid && reloaded.Snapshot().First(x => x.Id == task.Id).Project == "Project A", "invalid project has no partial mutation");
                reloaded.Remove(task.Id); Check(!new NotificationTaskBook(false).Snapshot().Any(x => x.Id == task.Id), "remove persists without changing original feed");
                using (var panel = new NotificationTasksPanel(true))
                {
                    var saved = panel.Add(new IslandNotification { Id = "demo", AppName = "原软件", Title = "Private title", Body = "private", CreatedAt = now }, 0);
                    var editor = (FrameworkElement)typeof(NotificationTasksPanel).GetField("projectCard", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(panel);
                    panel.EditProject(saved.Id); Check(editor.Visibility == Visibility.Visible, "explicit project action opens editor");
                    panel.SetPrivacy(true); panel.EditProject(saved.Id); Check(editor.Visibility == Visibility.Collapsed, "privacy also blocks indirect project actions");
                    panel.SetPrivacy(false); panel.Refresh();
                }
                NotificationTaskMenuTests.Run();
                Console.WriteLine("PASS: real notification-task persistence, immutable source copy, idempotence, pin/project ordering, due/reminder restart semantics, validation rollback, privacy refresh and lazy task-menu interaction.");
            }
            finally { Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", previous); if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        }
        private static void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); }
    }
}
