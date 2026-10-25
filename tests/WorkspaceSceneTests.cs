using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Threading;

namespace CodexIsland
{
    internal static class WorkspaceSceneTests
    {
        private sealed class FakeLauncher : IWorkspaceSceneLaunchAdapter
        {
            public readonly List<ProcessStartInfo> Calls = new List<ProcessStartInfo>();
            public void Start(ProcessStartInfo info) { if (info.FileName.Contains("fail.example")) throw new System.ComponentModel.Win32Exception(); Calls.Add(info); }
        }
        public static void Run()
        {
            string directory = Path.Combine(Path.GetTempPath(), "CodexIslandScenesTests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            try
            {
                string executable = Path.Combine(directory, "程序 With Space.exe"), shortcut = Path.Combine(directory, "shortcut.lnk"), json = Path.Combine(directory, "workspace-scenes.json");
                File.WriteAllText(executable, "fixture only, never executed"); File.WriteAllText(shortcut, "fixture only, never executed");
                var scene = Scene("写代码", new[] { Item("url", "https://example.com/docs?a=1&b=2"), Item("folder", directory), Item("program", executable, "--title \"参数 with space\""), Item("shortcut", shortcut) });
                var store = new WorkspaceSceneStore(json); Check(store.Load().Count == 0 && !File.Exists(json), "Missing scene file does not write defaults"); store.Save(new[] { scene }); var loaded = store.Load();
                Check(loaded.Count == 1 && loaded[0].Name == "写代码" && loaded[0].Resources[2].Arguments == "--title \"参数 with space\"", "Unicode scenes, ordered resources and separate arguments round trip");
                var copy = loaded[0].Copy(); copy.Resources[0].Name = "copy"; Check(loaded[0].Resources[0].Name != "copy", "Scene snapshots do not mutate persisted editor resources");
                var programInfo = WorkspaceSceneLaunch.CreateStartInfo(scene.Resources[2]);
                Check(programInfo.FileName == executable && programInfo.Arguments == "--title \"参数 with space\"" && programInfo.WorkingDirectory == directory, "Executable and argument fields remain separate without command concatenation");
                Check(WorkspaceSceneLaunch.CreateStartInfo(scene.Resources[0]).UseShellExecute && WorkspaceSceneLaunch.CreateStartInfo(scene.Resources[1]).FileName == directory, "HTTP URLs and existing folder resources resolve to Windows opening requests");
                foreach (string target in new[] { "javascript:alert(1)", "file:///C:/test.exe", "ftp://example.com/file", "https://", "example.com", "https://example.com/\nfoo" }) Reject(() => WorkspaceSceneLaunch.CreateStartInfo(Item("url", target)), "Unsafe URL resource is rejected");
                foreach (string target in new[] { "tool.exe", executable + " --bad", directory, Path.Combine(directory, "missing.exe"), executable + "\0" }) Reject(() => WorkspaceSceneLaunch.CreateStartInfo(Item("program", target)), "Malformed, missing or combined program path is rejected");
                Reject(() => WorkspaceSceneLaunch.CreateStartInfo(Item("folder", Path.Combine(directory, "missing"))), "Missing project folder is reported");
                Reject(() => WorkspaceSceneLaunch.CreateStartInfo(Item("url", "https://example.com", "--bad")), "Arguments are forbidden for web or directory targets");
                Check(WorkspaceSceneLaunch.FromShortcut(new QuickShortcut { Name = "old", Target = executable }).Kind == "program" && WorkspaceSceneLaunch.FromShortcut(new QuickShortcut { Name = "old", Target = shortcut }).Kind == "shortcut", "Existing shortcuts retain executable and shortcut type");
                var fake = new FakeLauncher(); var mixed = scene.Resources.Concat(new[] { Item("url", "https://fail.example/"), Item("folder", Path.Combine(directory, "missing")), Item("url", "https://example.com/final") }).ToList();
                var result = WorkspaceSceneLaunch.RunAsync(mixed, false, fake, CancellationToken.None).Result;
                Check(result.Opened == 5 && fake.Calls.Count == 5 && result.Errors.Count == 2, "Batch launch continues after individual failures and reports every failed item");
                var demo = new FakeLauncher(); var preview = WorkspaceSceneLaunch.RunAsync(scene.Resources, true, demo, CancellationToken.None).Result; Check(demo.Calls.Count == 0 && preview.Opened == 0 && preview.Skipped == 4, "Demo mode never invokes the system launch adapter");
                var cancelled = new CancellationTokenSource(); cancelled.Cancel(); var stopped = WorkspaceSceneLaunch.RunAsync(scene.Resources, false, demo, cancelled.Token).Result; cancelled.Dispose(); Check(demo.Calls.Count == 0 && stopped.Skipped == 4, "Cancelled batch does not open remaining resources");
                Reject(() => store.Save(Enumerable.Range(0, 21).Select(i => Scene("scene", new WorkspaceSceneResource[0]))), "Scene count is bounded");
                Reject(() => store.Save(new[] { Scene("scene", Enumerable.Range(0, 31).Select(i => Item("url", "https://example.com/"))) }), "Resource count is bounded");
                foreach (string invalid in new[] { "{broken", "null", "{}", "42", "[null]", "[{\"Id\":\"bad\",\"Name\":\"bad\",\"Resources\":[]}]" }) { File.WriteAllText(json, invalid); Check(store.Load().Count == 0 && store.LoadFailed, "Corrupt scene file is rejected without overwriting it: " + invalid); }
                string preserved = File.ReadAllText(json); store.Save(new[] { scene }); Check(Directory.GetFiles(directory, "*.bak").Any(path => File.ReadAllText(path) == preserved), "Unreadable scene metadata is preserved before an edit replaces it");
                PanelTests(directory, scene);
            }
            finally { Directory.Delete(directory, true); }
            Console.WriteLine("PASS: workspace scene persistence, structured batch launch, demo isolation and save recovery tests.");
        }
        private static void PanelTests(string directory, WorkspaceScene scene)
        {
            var previous = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try
            {
            string json = Path.Combine(directory, "panel-scenes.json"), shortcutsFile = Path.Combine(directory, "shortcuts.json"); var store = new WorkspaceSceneStore(json); store.Save(new[] { scene });
            var shortcuts = new ShortcutStore(shortcutsFile); shortcuts.Save(new[] { new QuickShortcut { Name = "复用网页", Target = "https://example.com/reuse" } }); string original = File.ReadAllText(shortcutsFile);
            var fake = new FakeLauncher();
            using (var panel = new WorkspaceScenePanel(false, store, shortcuts, fake))
            {
                var name = Field<TextBox>(panel, "sceneName");
                using (var locked = new FileStream(json, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    name.Text = "未能保存的名称"; Check(!panel.TryFlush() && panel.HasUnsavedChanges && name.Text == "未能保存的名称", "Save failure preserves pending editor text and reports unsaved state");
                    Check(new WorkspaceSceneStore(json).Load()[0].Name == "写代码", "Failed save leaves previous file intact");
                }
                Check(panel.TryFlush() && !panel.HasUnsavedChanges && store.Load()[0].Name == "未能保存的名称", "Retry flush persists pending edits after access is restored");
                var checks = Field<List<KeyValuePair<CheckBox, QuickShortcut>>>(panel, "importChecks"); checks[0].Key.IsChecked = true; Method(panel, "ImportChecked");
                Check(store.Load()[0].Resources.Count == scene.Resources.Count + 1 && File.ReadAllText(shortcutsFile) == original, "Import copies checked existing entries and preserves original shortcuts file");
                checks[0].Key.IsChecked = true; Method(panel, "ImportChecked"); Check(store.Load()[0].Resources.Count == scene.Resources.Count + 1, "Import avoids duplicate entries");
                WorkspaceScene selected = Field<WorkspaceScene>(panel, "selected"); Task<WorkspaceSceneLaunchResult> launch = panel.LaunchSceneAsync(selected); Pump(launch); Check(launch.Result.Opened == selected.Resources.Count && fake.Calls.Count == selected.Resources.Count, "Panel launches all selected scene resources through injected adapter");
                name.Text = ""; Check(!panel.TryFlush() && panel.HasUnsavedChanges, "Blank pending scene name cannot be silently discarded on exit"); name.Text = "恢复"; Check(panel.TryFlush(), "Valid edit recovers from validation failure");
            }
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }
        private static WorkspaceScene Scene(string name, IEnumerable<WorkspaceSceneResource> resources) { return new WorkspaceScene { Id = Guid.NewGuid().ToString("N"), Name = name, Resources = resources.ToList() }; }
        private static WorkspaceSceneResource Item(string kind, string target, string arguments = "") { return new WorkspaceSceneResource { Name = kind, Kind = kind, Target = target, Arguments = arguments }; }
        private static T Field<T>(object value, string name) { return (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value); }
        private static void Method(object value, string name) { value.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(value, null); }
        private static void Pump(Task task)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(8);
            while (!task.IsCompleted && DateTime.UtcNow < deadline) { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); Thread.Sleep(1); }
            Check(task.IsCompleted && !task.IsFaulted, "Scene async action completes without freezing the UI dispatcher");
        }
        private static void Reject(Action action, string label) { bool rejected = false; try { action(); } catch (ArgumentException) { rejected = true; } Check(rejected, label); }
        private static void Check(bool condition, string label) { if (!condition) throw new Exception(label); }
    }
}
