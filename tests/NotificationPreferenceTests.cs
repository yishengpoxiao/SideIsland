using System;
using System.IO;
using System.Linq;

namespace CodexIsland
{
    internal static class NotificationPreferenceTests
    {
        public static void Run()
        {
            string directory = Path.Combine(Path.GetTempPath(), "sideisland-notifications-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string path = Path.Combine(directory, "preferences.json");
                var store = new NotificationPreferences(false, path);
                var app = new IslandNotification { Id = "synthetic-notification", AppId = "Vendor.App!Main", AppName = "原始名称", Title = "NEVER_SAVE_THIS_TITLE", Body = "NEVER_SAVE_THIS_BODY", CreatedAt = DateTime.UtcNow };
                var duplicateName = new IslandNotification { Id = "other", AppId = "Vendor.Other!Main", AppName = "原始名称", CreatedAt = DateTime.UtcNow };
                store.Observe(new[] { app, duplicateName });
                Check(store.SetBlocked(NotificationPreferences.SourceKey(app), true), "Known source switch changes");
                string saved = File.ReadAllText(path);
                Check(!saved.Contains(app.Title) && !saved.Contains(app.Body) && !saved.Contains(app.Id), "Source preferences never persist notification contents or IDs");
                var restored = new NotificationPreferences(false, path);
                Check(restored.Sources.Count == 2 && restored.Sources.Single(source => source.AppId == app.AppId).Blocked && !restored.Sources.Single(source => source.AppId == duplicateName.AppId).Blocked, "App-specific block is persisted without blocking a same-name app");
                app.AppName = "更改后的名称"; restored.Observe(new[] { app });
                Check(restored.Sources.Single(source => source.AppId == app.AppId).Blocked && restored.Sources.Single(source => source.AppId == app.AppId).AppName == app.AppName, "Renamed apps retain their stable AppId switch");
                restored.Sources[0].Blocked = false;
                Check(restored.Sources.Single(source => source.AppId == app.AppId).Blocked, "Source list is detached from live preferences");
                var restartedFeed = new NotificationFeed();
                foreach (var source in restored.Sources.Where(source => source.Blocked)) restartedFeed.SetSourceBlocked(source.Key, true);
                Check(restartedFeed.Merge(new[] { app }, true).Count == 0 && restartedFeed.Items.Count == 0, "Restart restores filtering before the first OS baseline");
                restored.SetBlocked(NotificationPreferences.SourceKey(app), false);
                Check(!new NotificationPreferences(false, path).Sources.Single(source => source.AppId == app.AppId).Blocked, "Unblocking persists after restart");
                byte[] beforeDemo = File.ReadAllBytes(path);
                var demo = new NotificationPreferences(true, path);
                Check(demo.Sources.Count == 0, "Demo mode does not read real source preferences");
                demo.Observe(new[] { app }); demo.SetBlocked(NotificationPreferences.SourceKey(app), true);
                Check(beforeDemo.SequenceEqual(File.ReadAllBytes(path)), "Demo changes never write the user's preference file");
                var sample = new IslandNotification { Id = "demo", AppId = "demo.wechat", AppName = "微信", IsDemo = true };
                byte[] beforeSample = File.ReadAllBytes(path);
                restored.Observe(new[] { sample }); restored.SetBlocked(NotificationPreferences.SourceKey(sample), true);
                Check(new NotificationPreferences(false, path).Sources.All(source => source.AppId != sample.AppId), "Sample notification sources are not persisted");
                Check(beforeSample.SequenceEqual(File.ReadAllBytes(path)), "Sample source changes do not even rewrite the real preferences file");
                using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    restored.SetBlocked(NotificationPreferences.SourceKey(app), true);
                    Check(restored.LastError.Length > 0 && restored.Sources.Single(source => source.AppId == app.AppId).Blocked, "A locked preference file keeps filtering active and reports an unsaved change");
                    Check(!new NotificationPreferences(false, path).Sources.Single(source => source.AppId == app.AppId).Blocked, "Failed replacement preserves the previous saved switch");
                }
                restored.Observe(new[] { app });
                Check(restored.LastError.Length == 0 && new NotificationPreferences(false, path).Sources.Single(source => source.AppId == app.AppId).Blocked, "Unchanged OS snapshots retry a pending save after the file lock is released");
                File.WriteAllText(path, "{bad");
                var damaged = new NotificationPreferences(false, path);
                Check(damaged.LastError.Length > 0, "Corrupt settings leave the app usable with a visible error");
                damaged.Observe(new[] { app });
                Check(Directory.GetFiles(directory, "*.bak").Any(backup => File.ReadAllText(backup) == "{bad"), "Auto-discovery after a corrupt preference file preserves the original metadata");
                foreach (string partial in new[] { "{\"Sources\":{}}", "{\"Sources\":[{\"AppId\":\"valid\",\"AppName\":\"valid\",\"Blocked\":true},{\"AppId\":123}]}" })
                {
                    File.WriteAllText(path, partial); damaged = new NotificationPreferences(false, path);
                    Check(damaged.LastError.Length > 0, "Wrong collection types and partially damaged sources surface a recovery error");
                    damaged.Observe(new[] { app });
                    Check(Directory.GetFiles(directory, "*.bak").Any(backup => File.ReadAllText(backup) == partial), "Partially recovered notification metadata is backed up before replacement");
                }
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        private static void Check(bool value, string message)
        {
            if (!value) throw new Exception("Notification preferences test failed: " + message);
        }
    }
}
