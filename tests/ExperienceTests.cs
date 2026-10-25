using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace CodexIsland
{
    internal static class ExperienceTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        internal static void Run()
        {
            string previous = Environment.GetEnvironmentVariable("CODEX_ISLAND_DATA_DIR");
            string language = UiLocalization.Language, theme = UiTheme.Name;
            var context = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            string folder = Path.Combine(Path.GetTempPath(), "SideIsland.Experience." + Guid.NewGuid().ToString("N"));
            IslandWindow window = null;
            try
            {
                Directory.CreateDirectory(folder); Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", folder);
                Notifications(folder); Profiles(); Search(); Diagnostics();
                new SettingsStore().Save(new Settings { AlwaysOnTop = false, AutoCollapse = false, NotificationsEnabled = false, AutoMusic = false, HoverExpandEnabled = false, HoverDelayMilliseconds = 1375 });
                window = new IslandWindow(true) { ShowActivated = false, IsHitTestVisible = false };
                window.Loaded += (s, e) => StopTimers(window); window.Show(); window.UpdateLayout(); StopTimers(window);
                var settings = Field<Settings>(window, "settings");
                Check((bool)Call(window, "ApplyLayoutProfile", "code") && settings.CompactSlot1 == "codex-secondary" && settings.CompactSlot2 == "ports", "coding profile applies real port and weekly quota widgets");
                Check(!settings.HoverExpandEnabled && settings.HoverDelayMilliseconds == 1375, "profiles preserve hover preferences");
                Check((bool)Call(window, "ApplyLayoutProfile", "music") && settings.CompactMode == "music" && settings.AutoMusic, "music profile activates full compact music");
                Check((bool)Call(window, "ApplyLayoutProfile", "focus") && settings.CompactMode == "focus" && settings.QuietPreviews, "focus profile selects countdown and quiet notifications");
                Call(window, "RestoreLayoutProfile"); Check(settings.CompactMode == "music" && !settings.QuietPreviews, "previous layout and quiet preference restore together");
                Check(!(bool)Call(window, "ApplyLayoutProfile", "missing"), "missing profile cannot replace settings");
                var scenes = Field<WorkspaceScenePanel>(window, "workspaceScenePanel");
                var scene = new WorkspaceScene { Id = Guid.NewGuid().ToString("N"), Name = "Layout only", LayoutProfileId = "focus", Resources = new System.Collections.Generic.List<WorkspaceSceneResource>() };
                var task = scenes.LaunchSceneAsync(scene);
                Check(task.IsCompleted && settings.CompactMode == "focus" && settings.QuietPreviews, "workspace launch applies an associated layout even without resources");
                Check(Field<System.Collections.Generic.Dictionary<string, System.Windows.UIElement>>(window, "toolPanels").Count == ToolCatalog.All.Length, "every new module is actually reachable");
                settings.QuietPreviews = false; window.SetExpanded(false, false);
                Call(window, "ShowPeek", new IslandNotification { AppName = "Log fixture", AppId = "sideisland.logwatch", Id = "log-fixture", Title = "Fixture", Body = "Fake log", CreatedAt = DateTime.UtcNow });
                Call(window, "OpenPeekTarget"); Check(Field<string>(window, "selectedTool") == "logwatch", "log alerts open the log tracker instead of the process watcher");
                Console.WriteLine("PASS: notification exact-repeat grouping/priority/identity, profile and scene persistence/application/restore, purpose search and content-free startup diagnostics.");
            }
            finally
            {
                if (window != null) { StopTimers(window); window.Close(); }
                AppAppearance.Configure(language, theme); Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", previous);
                SynchronizationContext.SetSynchronizationContext(context);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }
        private static void Notifications(string folder)
        {
            DateTime now = DateTime.UtcNow;
            var feed = new NotificationFeed(() => now);
            var values = new[] { Notice("1", "one", "private-fixture-message", now), Notice("2", "one", "private-fixture-message", now.AddSeconds(-10)), Notice("3", "two", "other", now.AddSeconds(-5)) };
            var prefs = new NotificationPreferences(false, Path.Combine(folder, "priority.json")); prefs.Observe(values);
            prefs.SetPriority(NotificationPreferences.SourceKey(values[2]), 1);
            feed.Merge(values); var grouped = NotificationPresentation.Project(feed.Items, prefs);
            Check(grouped.Count == 2 && grouped.First().AppId == "two" && grouped.Single(n => n.AppId == "one").RepeatCount == 2, "important app sorts first and exact repeats combine");
            Check(feed.UnreadCount == 3 && feed.Merge(values).Count == 0, "grouping preserves raw unread count and consumed identities");
            prefs.SetMergeDuplicates(false); Check(NotificationPresentation.Project(feed.Items, prefs).Count == 3, "grouping can be disabled");
            var loaded = new NotificationPreferences(false, Path.Combine(folder, "priority.json")); Check(!loaded.MergeDuplicates && loaded.PriorityFor(values[2]) == 1, "notification policy persists");
            Check(!File.ReadAllText(Path.Combine(folder, "priority.json")).Contains("private-fixture-message"), "notification preferences never persist bodies");
            prefs.SetMergeDuplicates(true);
            var distant = new[] { Notice("a", "one", "same", now), Notice("b", "one", "same", now.AddSeconds(-61)) };
            Check(NotificationPresentation.Project(distant, prefs).Count == 2, "repeat window boundary keeps older notices distinct");
            feed.SetSourceBlocked(NotificationPreferences.SourceKey(values[0]), true); Check(feed.UnreadCount == 1, "blocking a grouped source still removes all raw notices");
        }
        private static IslandNotification Notice(string id, string app, string body, DateTime time)
        { return new IslandNotification { Id = id, AppId = app, AppName = app, Title = "fixture", Body = body, CreatedAt = time }; }
        private static void Profiles()
        {
            var store = new LayoutProfileStore(false); var settings = new Settings { CompactSlot1 = "cpu", CompactSlot2 = "ports", QuietPreviews = true };
            var profile = LayoutProfile.Capture(settings, "Custom fixture"); store.Data.Profiles.Add(profile); store.Data.ActiveId = profile.Id; store.Save();
            var loaded = new LayoutProfileStore(false); Check(loaded.Find(profile.Id).Slot2 == "ports" && loaded.Data.ActiveId == profile.Id, "custom layout persists independently of account settings");
            var scene = new WorkspaceScene { Id = Guid.NewGuid().ToString("N"), Name = "Linked", LayoutProfileId = profile.Id, Resources = new System.Collections.Generic.List<WorkspaceSceneResource>() };
            var scenes = new WorkspaceSceneStore(); scenes.Save(new[] { scene }); Check(scenes.Load().Single().LayoutProfileId == profile.Id && scene.Copy().LayoutProfileId == profile.Id, "scene copies and persistence retain layout linkage");
        }
        private static void Search()
        { Check(ToolCatalog.Matches("logwatch", "错误") && ToolCatalog.Matches("desktop", "fullscreen") && ToolCatalog.Matches("media", "microphone") && ToolCatalog.Matches("ports", "3000"), "purpose keywords locate relevant features"); Check(!ToolCatalog.Matches("ports", "nonexistent-feature"), "unrelated keywords do not match"); }
        private static void Diagnostics()
        {
            StartupDiagnostics.Begin(true); StartupDiagnostics.Failure(new InvalidOperationException("secret user fixture path")); StartupDiagnostics.Mark("界面已显示", true);
            var snapshot = StartupDiagnostics.Snapshot(); Check(snapshot.Ready && snapshot.Errors.Count == 1 && !snapshot.Errors[0].Contains("secret"), "diagnostic reports exclude exception messages and private data");
        }
        private static void StopTimers(IslandWindow window) { foreach (var field in typeof(IslandWindow).GetFields(Private)) if (field.FieldType == typeof(DispatcherTimer)) ((DispatcherTimer)field.GetValue(window)).Stop(); }
        private static T Field<T>(object target, string name) { return (T)target.GetType().GetField(name, Private).GetValue(target); }
        private static object Call(object target, string name, params object[] args) { try { return target.GetType().GetMethod(name, Private).Invoke(target, args); } catch (TargetInvocationException error) { throw error.InnerException ?? error; } }
        private static void Check(bool condition, string message) { if (!condition) throw new Exception("Experience: " + message); }
    }
}
