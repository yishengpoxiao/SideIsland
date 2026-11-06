using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace CodexIsland
{
    internal static class WorkspaceSceneRecoveryTests
    {
        private static int checks;
        private sealed class Launcher : IWorkspaceSceneLaunchAdapter, IDisposable
        {
            internal readonly List<string> Calls = new List<string>();
            internal readonly ManualResetEvent Entered = new ManualResetEvent(false), Release = new ManualResetEvent(false);
            internal bool Block;
            public void Start(ProcessStartInfo info)
            {
                lock (Calls) Calls.Add(info.FileName);
                Entered.Set(); if (Block && !Release.WaitOne(5000)) throw new IOException("Fixture release timed out.");
            }
            public void Dispose() { Release.Set(); Entered.Dispose(); Release.Dispose(); }
        }
        public static void Run()
        {
            checks = 0; string directory = Path.Combine(Path.GetTempPath(), "SideIslandSceneRecovery-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            var context = SynchronizationContext.Current; string language = UiLocalization.Language;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try { UiLocalization.Configure("zh"); Persistence(directory); PanelRecovery(directory); LaunchLifetime(directory); Appearance(directory); }
            finally { UiLocalization.Configure(language); SynchronizationContext.SetSynchronizationContext(context); Directory.Delete(directory, true); }
            Console.WriteLine("PASS: " + checks + " workspace recovery, concurrency, launch lifetime and compact bilingual checks.");
        }
        private static void Persistence(string directory)
        {
            string path = Path.Combine(directory, "store.json"); var store = new WorkspaceSceneStore(path); var original = Scene("original");
            store.Load(); store.Save(new[] { original }); var pending = store.Load(); pending[0].Name = "pending";
            byte[] external = Json(Scene("external")); File.WriteAllBytes(path, external);
            var conflict = Conflict(() => store.Save(pending)); Check(!conflict.DraftCommitted && Same(path, external), "Ordinary external edits are rejected before mutation");
            store.Load(); File.Delete(path); Conflict(() => store.Save(pending)); Check(!File.Exists(path), "External deletion is not silently recreated");
            store.Load(); File.WriteAllBytes(path, external); Conflict(() => store.Save(pending)); Check(Same(path, external), "External creation after missing baseline is preserved");
            store.Load(); store.Save(pending); Check(store.Load()[0].Name == "pending", "Explicit reload permits a subsequent saved edit");
            Check(Directory.GetFiles(directory, "store.json.previous-*.bak").Length == 0 && Directory.GetFiles(directory, "*.tmp").Length == 0, "Normal successful saves clean their transient backup and staging file");

            foreach (byte[] invalid in new[] { Encoding.UTF8.GetBytes("{broken"), Encoding.UTF8.GetBytes("null"), new byte[] { 0xc3, 0x28 } })
            {
                File.WriteAllBytes(path, invalid); Check(store.Load().Count == 0 && store.LoadFailed, "Unreadable JSON/UTF8 returns a flagged fallback");
                store.Save(new WorkspaceScene[0]); Check(Same(path, invalid) && store.LoadFailed, "Saving unchanged empty fallback never replaces original bytes");
                store.Save(new[] { original }); Check(!store.LoadFailed && Directory.GetFiles(directory, "store.json.unreadable-*.bak").Any(file => Same(file, invalid)), "Explicit nonempty recovery atomically retains exact unreadable bytes");
            }
            store.Load();
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Check(store.Load().Count == 0 && store.LoadFailed, "Transient read lock is treated as a failed load, not a missing file");
            Conflict(() => store.Save(new[] { Scene("fallback edit") })); Check(new WorkspaceSceneStore(path).Load()[0].Name == "original", "Unlocked valid original cannot be overwritten by a failed-load fallback");
            Check(store.Load().Count == 1 && !store.LoadFailed, "Successful reload clears failed-read state");

            byte[] brokenA = Encoding.UTF8.GetBytes("first broken"), brokenB = Encoding.UTF8.GetBytes("second broken");
            File.WriteAllBytes(path, brokenA); store.Load(); int backups = Directory.GetFiles(directory, "store.json.unreadable-*.bak").Length;
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                Io(() => store.Save(new[] { original }), "Replacement blocked by an open handle reports failure");
            Check(Same(path, brokenA) && store.LoadFailed && Directory.GetFiles(directory, "store.json.unreadable-*.bak").Length == backups, "Failed replacement never advances recovery state or makes a premature backup");
            File.WriteAllBytes(path, brokenB); Conflict(() => store.Save(new[] { original })); Check(Same(path, brokenB), "Recovery retry detects newly changed original instead of discarding it");
            store.Load(); store.Save(new[] { original }); Check(Directory.GetFiles(directory, "store.json.unreadable-*.bak").Any(file => Same(file, brokenB)), "Reloaded recovery preserves the newest exact original");

            string racePath = Path.Combine(directory, "race.json"); File.WriteAllBytes(racePath, Json(original)); bool race = true;
            var raceStore = new WorkspaceSceneStore(racePath, () => { if (race) { race = false; File.WriteAllBytes(racePath, external); } }); raceStore.Load();
            var late = Conflict(() => raceStore.Save(new[] { Scene("draft committed") }));
            Check(late.DraftCommitted && late.BackupPath != null && Same(late.BackupPath, external), "Late noncooperating writer is preserved by exact displaced-file backup");
            Check(new WorkspaceSceneStore(racePath).Load()[0].Name == "draft committed", "Late conflict accurately reports that the draft was committed");
            File.WriteAllBytes(racePath, Json(Scene("newest"))); Conflict(() => raceStore.Save(new[] { original }));
            Check(new WorkspaceSceneStore(racePath).Load()[0].Name == "newest", "After a committed conflict, another external write is never silently replaced");
            Check(Directory.GetFiles(directory, "*.tmp").Length == 0, "Conflict and failure paths clean only their staging files");

            var unfinished = Scene(""); byte[] beforeDraft = File.ReadAllBytes(path); string draft = store.SaveDraft(new[] { unfinished });
            Check(Same(path, beforeDraft) && File.ReadAllText(draft).Contains("\"Name\":\"\""), "Recovery draft preserves unfinished editing without changing active file");
            store.Save(new[] { original }); Check(Same(path, beforeDraft), "Draft rescue leaves the active save baseline unchanged");
        }
        private static void PanelRecovery(string directory)
        {
            string path = Path.Combine(directory, "panel.json"), shortcutsPath = Path.Combine(directory, "shortcuts.json");
            new WorkspaceSceneStore(path).Save(new[] { Scene("initial") }); var shortcuts = new ShortcutStore(shortcutsPath);
            shortcuts.Save(new[] { new QuickShortcut { Name = "copy", Target = "https://copy.invalid/" } });
            using (var launcher = new Launcher()) using (var panel = new WorkspaceScenePanel(false, new WorkspaceSceneStore(path), shortcuts, launcher))
            {
                Field<TextBox>(panel, "sceneName").Text = "unfinished"; byte[] external = Json(Scene("external panel")); File.WriteAllBytes(path, external);
                Check(!panel.TryFlush() && panel.HasUnsavedChanges && Same(path, external), "Panel failed save retains dirty model and external file");
                Check(Field<TextBlock>(panel, "status").Text.Contains("外部"), "Panel reports the concrete external conflict");
                Method(panel, "ReloadScenes");
                Check(!panel.HasUnsavedChanges && Field<WorkspaceScene>(panel, "selected").Name == "external panel", "Recovery reload replaces editor only after draft rescue and successful read");
                Check(Directory.GetFiles(directory, "panel.json.draft-*.json").Any(file => File.ReadAllText(file).Contains("unfinished")), "Unsaved edit is recoverable from a distinct new draft file");
                string chinese = Field<TextBlock>(panel, "status").Text, draftName = chinese.Split('\n').Last().Substring("场景草稿备份：".Length);
                UiLocalization.Configure("en"); Check(Field<TextBlock>(panel, "status").Text == "Workspaces reloaded.\nWorkspace draft backup: " + draftName, "Reload result and exact draft filename translate together");
                UiLocalization.Configure("zh"); Check(Field<TextBlock>(panel, "status").Text == chinese, "Language toggle restores result without rewriting draft filename");
                Field<TextBox>(panel, "sceneName").Text = "keep during lock";
                using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Method(panel, "ReloadScenes");
                Check(panel.HasUnsavedChanges && Field<WorkspaceScene>(panel, "selected").Name == "keep during lock", "Failed reload retains current dirty edit");
                Method(panel, "ReloadScenes"); Check(!panel.HasUnsavedChanges && Field<WorkspaceScene>(panel, "selected").Name == "external panel", "Retry reload restores readable external version");
                var checks = Field<List<KeyValuePair<CheckBox, QuickShortcut>>>(panel, "importChecks"); checks[0].Key.IsChecked = true;
                using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) Method(panel, "ImportChecked");
                Check(panel.HasUnsavedChanges && Field<WorkspaceScene>(panel, "selected").Resources.Count == 2 && !Field<TextBlock>(panel, "status").Text.StartsWith("已加入"), "Import save failure retains imported edit and does not report saved success");
                Check(panel.TryFlush(), "Imported pending changes save after lock release");
                using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    FindButtons(Field<StackPanel>(panel, "sceneList")).Single(button => (string)button.Content == UiLocalization.Localize("删除")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(panel.HasUnsavedChanges && Field<List<WorkspaceScene>>(panel, "scenes").Count == 0, "Failed deletion save retains explicit pending empty list");
                }
                Check(panel.TryFlush() && new WorkspaceSceneStore(path).Load().Count == 0, "Pending deletion persists after lock release");
            }
            bool race = true; File.WriteAllBytes(path, Json(Scene("start"))); byte[] raced = Json(Scene("raced"));
            var store = new WorkspaceSceneStore(path, () => { if (race) { race = false; File.WriteAllBytes(path, raced); } });
            using (var launcher = new Launcher()) using (var panel = new WorkspaceScenePanel(false, store, shortcuts, launcher))
            {
                Field<TextBox>(panel, "sceneName").Text = "saved draft"; Method(panel, "Persist");
                Check(!panel.HasUnsavedChanges && Field<TextBlock>(panel, "status").Text.Contains("外部版本"), "Already committed recovery warning survives generic success status");
                Check(new WorkspaceSceneStore(path).Load()[0].Name == "saved draft", "Panel committed-conflict model agrees with saved draft");
                UiLocalization.Configure("en"); Check(Field<TextBlock>(panel, "status").Text.StartsWith("Your workspace edits were saved.") && Field<TextBlock>(panel, "status").Text.EndsWith(".bak"), "Committed conflict and exact backup path localize as one result"); UiLocalization.Configure("zh");
            }
        }
        private static void LaunchLifetime(string directory)
        {
            string path = Path.Combine(directory, "launch.json"); var scene = Scene("launch"); scene.LayoutProfileId = "test";
            new WorkspaceSceneStore(path).Save(new[] { scene });
            using (var launcher = new Launcher()) using (var panel = Panel(path, directory, launcher))
            {
                var selected = Field<WorkspaceScene>(panel, "selected"); int callbacks = 0; Task<WorkspaceSceneLaunchResult> nested = null;
                panel.ConfigureLayouts(() => new LayoutProfile[0], id => { callbacks++; nested = panel.LaunchSceneAsync(selected); selected.Resources[0].Target = "https://mutated.invalid/"; Method(panel, "RebuildScenes"); return true; });
                var task = panel.LaunchSceneAsync(selected); Pump(task);
                Check(callbacks == 1 && nested != null && nested.Result == null && launcher.Calls.SequenceEqual(new[] { "https://original.invalid/" }), "Guard precedes layout callback and resources are frozen before callback mutation");
                panel.ConfigureLayouts(() => new LayoutProfile[0], id => { throw new IOException("fixture"); });
                var failure = panel.LaunchSceneAsync(selected); Pump(failure); Check(failure.Result == null && launcher.Calls.Count == 1 && !Field<bool>(panel, "launching"), "Throwing layout callback does not launch resources or retain pending guard");
            }
            scene.LayoutProfileId = null; scene.Resources.Add(Item("https://second.invalid/")); new WorkspaceSceneStore(path).Save(new[] { scene });
            using (var launcher = new Launcher { Block = true }) using (var panel = Panel(path, directory, launcher))
            {
                var task = panel.LaunchSceneAsync(Field<WorkspaceScene>(panel, "selected")); Check(launcher.Entered.WaitOne(3000), "Blocking fixture starts without blocking UI dispatcher");
                Method(panel, "RebuildScenes"); Check(Field<List<Button>>(panel, "launchButtons").All(button => !button.IsEnabled), "Rebuilt launch controls stay disabled while launch is pending");
                Check(panel.LaunchSceneAsync(scene).Result == null, "A pending launch rejects duplicate invocation");
                Method(panel, "CancelLaunch"); Check(!task.IsCompleted && !Field<Button>(panel, "cancelLaunch").IsEnabled, "Cancellation is immediate and does not pretend a pending native launch has completed");
                launcher.Release.Set(); Pump(task);
                Check(task.Result.Cancelled && task.Result.Opened == 1 && task.Result.Skipped == 1 && launcher.Calls.Count == 1, "Cancel stops remaining resources and leaves already started resource alone");
                Check(!Field<bool>(panel, "launching") && Field<Button>(panel, "cancelLaunch").Visibility == Visibility.Collapsed, "Completion resets launch controls after cancellation");
            }
            using (var launcher = new Launcher { Block = true })
            {
                var panel = Panel(path, directory, launcher); var task = panel.LaunchSceneAsync(Field<WorkspaceScene>(panel, "selected")); Check(launcher.Entered.WaitOne(3000), "Close fixture enters first injected launch");
                panel.Dispose(); string status = Field<TextBlock>(panel, "status").Text; launcher.Release.Set(); Pump(task);
                Check(task.Result.Cancelled && launcher.Calls.Count == 1 && Field<TextBlock>(panel, "status").Text == status, "Close cancels remaining resources and suppresses late status updates");
                Check(Field<List<Button>>(panel, "launchButtons").All(button => !button.IsEnabled), "Late launch completion cannot reenable a disposed panel");
            }
            using (var launcher = new Launcher())
            {
                var panel = Panel(path, directory, launcher); var selected = Field<WorkspaceScene>(panel, "selected"); selected.LayoutProfileId = "close";
                panel.ConfigureLayouts(() => new LayoutProfile[0], id => { panel.Dispose(); return true; }); var task = panel.LaunchSceneAsync(selected); Pump(task);
                Check(task.Result == null && launcher.Calls.Count == 0, "Layout-triggered close never starts a delayed resource");
            }
        }
        private static void Appearance(string directory)
        {
            foreach (string language in new[] { "zh", "en" })
            {
                UiLocalization.Configure(language);
                using (var launcher = new Launcher()) using (var panel = Panel(Path.Combine(directory, "appearance.json"), directory, launcher))
                {
                    foreach (double width in new[] { 280.0, 360.0, 520.0 })
                    {
                        panel.Measure(new Size(width, Double.PositiveInfinity)); panel.Arrange(new Rect(0, 0, width, panel.DesiredSize.Height)); panel.UpdateLayout();
                        var row = panel.Children[2] as Panel; var buttons = FindButtons(row).ToList();
                        Check(buttons.Count == 2 && Math.Abs(buttons[0].ActualWidth - buttons[1].ActualWidth) < 0.01 && buttons.All(button => button.ActualHeight >= 30), "Recovery and create actions retain equal compact cells in " + language + " at " + width);
                    }
                    Check(UiLocalization.Localize("停止启动剩余资源") != "" && (language != "en" || !UiLocalization.Localize("备份修改并重新加载").Any(c => c >= 0x4e00 && c <= 0x9fff)), "New action captions are localized in " + language);
                }
            }
        }
        private static WorkspaceScenePanel Panel(string path, string directory, Launcher launcher) { return new WorkspaceScenePanel(false, new WorkspaceSceneStore(path), new ShortcutStore(Path.Combine(directory, "empty-shortcuts.json")), launcher); }
        private static WorkspaceScene Scene(string name) { return new WorkspaceScene { Id = Guid.NewGuid().ToString("N"), Name = name, Resources = new List<WorkspaceSceneResource> { Item("https://original.invalid/") } }; }
        private static WorkspaceSceneResource Item(string target) { return new WorkspaceSceneResource { Name = "resource", Kind = "url", Target = target, Arguments = "" }; }
        private static byte[] Json(WorkspaceScene scene) { return new UTF8Encoding(false, true).GetBytes(new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new[] { scene })); }
        private static bool Same(string path, byte[] bytes) { return File.ReadAllBytes(path).SequenceEqual(bytes); }
        private static WorkspaceSceneConflictException Conflict(Action action) { try { action(); } catch (WorkspaceSceneConflictException error) { checks++; return error; } throw new Exception("Expected workspace conflict"); }
        private static void Io(Action action, string label) { try { action(); } catch (IOException) { Check(true, label); return; } catch (UnauthorizedAccessException) { Check(true, label); return; } throw new Exception(label); }
        private static T Field<T>(object value, string name) { return (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value); }
        private static void Method(object value, string name) { value.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(value, null); }
        private static IEnumerable<Button> FindButtons(DependencyObject value)
        {
            if (value is Button) { yield return (Button)value; yield break; }
            foreach (object child in LogicalTreeHelper.GetChildren(value)) if (child is DependencyObject) foreach (var button in FindButtons((DependencyObject)child)) yield return button;
        }
        private static void Pump(Task task)
        {
            var deadline = DateTime.UtcNow.AddSeconds(8); while (!task.IsCompleted && DateTime.UtcNow < deadline) { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); Thread.Sleep(1); }
            Check(task.IsCompleted && !task.IsFaulted, "Injected asynchronous scene action completes");
        }
        private static void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; }
    }
}
