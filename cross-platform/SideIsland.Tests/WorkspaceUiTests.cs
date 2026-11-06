using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SideIsland.Core;
using SideIsland.Desktop;
using SideIsland.Desktop.Platforms;

namespace SideIsland.Tests;

internal static class WorkspaceUiTests
{
    private static int checks;
    internal static void Run(string? artifacts)
    {
        checks = 0; string directory = Path.Combine(Path.GetTempPath(), "side-island-workspace-ui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        TaskCompletionSource<bool>? writeGate = null; bool failWrite = false;
        TaskCompletionSource<WorkspaceLaunchResult>? launchGate = null; int launches = 0; CancellationToken launchToken = default; IReadOnlyList<PortableWorkspaceResource>? launched = null;
        TaskCompletionSource<string?>? pickGate = null; int picks = 0;
        var sources = new IntegrationSources(_ => Task.FromResult(new MediaSnapshot(false, "", "", "", null, null, null, false, null)), (_, _) => Task.FromResult<string?>(null), (_, _) => throw new InvalidOperationException("No quota in workspace fixture"))
        {
            SaveWorkspace = async (store, snapshot) => { if (writeGate is not null) await writeGate.Task; if (failWrite) throw new IOException("Owned simulated write failure"); store.Save(snapshot); },
            LaunchWorkspace = (resources, token) => { launches++; launched = resources; launchToken = token; return launchGate!.Task; },
            ChooseWorkspaceTarget = _ => { picks++; return pickGate!.Task; }
        };
        var window = new IslandWindow(true, directory, sources);
        try
        {
            BlockedStoreUi(directory); RecoveryRaceUi(directory);
            window.Show(); window.Expand(); AppearanceTestActions.Set(window, "en", false); window.SelectToolForTest("workspaces"); Wait(window, window.LatestWorkspaceLoad);
            Check(!Find<Button>(window, "workspace-launch").IsEnabled, "empty catalog cannot launch");
            Find<Expander>(window, "workspace-shortcuts").IsExpanded = true; Flush(window);
            Set(window, "workspace-resource-name", "Docs 中文"); Set(window, "workspace-resource-target", "https://example.invalid/docs?q=literal%20space");
            Click(window, "workspace-save-resource"); Wait(window, window.LatestWorkspaceSave);
            var catalog = Data(window); Check(catalog.Resources.Count == 1 && Find<TextBlock>(window, "workspace-feedback").Text == "Saved.", "actual controls persist first reusable shortcut");
            string urlId = catalog.Resources[0].Id;
            Set(window, "workspace-name", "Unsaved scene draft");
            Find<ComboBox>(window, "workspace-resource-picker").SelectedIndex = 0; Flush(window);
            Set(window, "workspace-resource-name", "Editor 参数 😀"); Find<ComboBox>(window, "workspace-resource-kind").SelectedIndex = 2; Flush(window);
            Set(window, "workspace-resource-target", Environment.ProcessPath!); Click(window, "workspace-add-argument"); Click(window, "workspace-add-argument");
            Set(window, "workspace-argument-1", "two words \"quoted\" 😀\nsecond line");
            Click(window, "workspace-save-resource"); Wait(window, window.LatestWorkspaceSave);
            catalog = Data(window); string programId = catalog.Resources[1].Id;
            Check(catalog.Resources[1].Arguments.SequenceEqual(new[] { "", "two words \"quoted\" 😀\nsecond line" }), "argument editor preserves an empty argument and literal quotes, Unicode and newline");
            Check(Find<TextBox>(window, "workspace-name").Text == "Unsaved scene draft", "saving resource preserves an unrelated unsaved scene draft");
            Find<CheckBox>(window, "workspace-include-" + urlId).IsChecked = true; Find<CheckBox>(window, "workspace-include-" + programId).IsChecked = true;
            Set(window, "workspace-name", "Write code 编程"); Find<ComboBox>(window, "workspace-slot-1").SelectedIndex = 4; Find<ComboBox>(window, "workspace-slot-2").SelectedIndex = 3;
            Click(window, "workspace-save-scene"); Wait(window, window.LatestWorkspaceSave); string sceneId = Data(window).Scenes.Single().Id;
            Check(Data(window).Scenes.Single().ResourceIds.SequenceEqual(new[] { urlId, programId }), "scene stores reusable entry references in library order");
            var reloaded = new PortableWorkspaceStore(Path.Combine(directory, "workspace-scenes.json")).Load(); Check(reloaded.Resources.Count == 2 && reloaded.Scenes.Single().Id == sceneId, "actual persisted file can be reopened independently");

            failWrite = true; Set(window, "workspace-name", "unsaved failure edit"); Click(window, "workspace-save-scene"); Wait(window, window.LatestWorkspaceSave);
            Check(Data(window).Scenes.Single().Name == "Write code 编程" && Find<TextBox>(window, "workspace-name").Text == "unsaved failure edit", "failed save preserves previous saved model and edited draft"); failWrite = false;
            writeGate = new(TaskCreationOptions.RunContinuationsAsynchronously); Click(window, "workspace-save-scene"); Task saving = window.LatestWorkspaceSave;
            AppearanceTestActions.Set(window, "zh", true); window.SelectToolForTest("notes"); window.SelectToolForTest("workspaces"); Flush(window);
            Check(!Find<Button>(window, "workspace-save-scene").IsEnabled, "pending save guard survives real theme/language/navigation reconstruction");
            window.Close(); Check(window.IsVisible && !Field<bool>(window, "closing"), "window does not exit midway through an explicit save");
            writeGate.SetResult(true); Wait(window, saving); writeGate = null; AppearanceTestActions.Set(window, "en", false);
            Check(Data(window).Scenes.Single().Name == "unsaved failure edit", "pending save completes once and applies captured draft");
            Check(Field<string>(window, "status") == "", "completed save clears obsolete close-block message");
            Set(window, "workspace-name", "unsaved launch label"); Set(window, "workspace-resource-target", "/unsaved/program");
            launchGate = new(TaskCreationOptions.RunContinuationsAsynchronously); Click(window, "workspace-launch");
            Check(launches == 1 && launched!.Count == 2 && launched[1].Target == Environment.ProcessPath && window.Settings.Slot1 == "clock" && window.Settings.Slot2 == "focus", "launch captures saved resources and applies saved collapsed widgets");
            AppearanceTestActions.Set(window, "zh", true); window.SelectToolForTest("notes"); window.SelectToolForTest("workspaces"); Flush(window); Click(window, "workspace-launch");
            Check(launches == 1 && !Find<Button>(window, "workspace-launch").IsEnabled, "pending launch cannot be duplicated by rebuilding controls");
            Click(window, "workspace-cancel"); Check(launchToken.IsCancellationRequested, "cancel reaches launcher without claiming to stop launched apps");
            launchGate.SetResult(new(new[] { new WorkspaceLaunchItemResult(urlId, "Docs 中文", WorkspaceLaunchStatus.HandedOff), new WorkspaceLaunchItemResult(programId, "Editor 参数 😀", WorkspaceLaunchStatus.Skipped, "cancelled") })); Wait(window, window.LatestWorkspaceLaunch);
            Check(Find<StackPanel>(window, "workspace-results").Children.Count >= 3, "per-resource handoff and skipped results are visible");

            pickGate = new(TaskCreationOptions.RunContinuationsAsynchronously); Click(window, "workspace-choose"); Set(window, "workspace-resource-target", "manually changed"); pickGate.SetResult("selected old path"); Wait(window, window.LatestWorkspaceChoose);
            Check(Find<TextBox>(window, "workspace-resource-target").Text == "manually changed", "late native-picker seam cannot overwrite manual edits");
            pickGate = new(TaskCreationOptions.RunContinuationsAsynchronously); Click(window, "workspace-choose"); window.SelectToolForTest("notes"); window.SelectToolForTest("workspaces"); Flush(window);
            Click(window, "workspace-choose"); Check(picks == 2, "picker guard survives navigation"); pickGate.SetResult("obsolete selection"); Wait(window, window.LatestWorkspaceChoose);
            Check(Find<TextBox>(window, "workspace-resource-target").Text == "manually changed", "detached picker does not overwrite new controls");

            int layouts = Layouts(window, artifacts); Check(layouts == 96, "full populated workspace matrix executed");
            var closingDirectory = Path.Combine(directory, "closing-launch"); var closingStore = new PortableWorkspaceStore(Path.Combine(closingDirectory, "workspace-scenes.json")); closingStore.Load(); closingStore.Save(Data(window));
            var closingWindow = new IslandWindow(true, closingDirectory, sources);
            try
            {
                closingWindow.Show(); closingWindow.Expand(); closingWindow.SelectToolForTest("workspaces"); Wait(closingWindow, closingWindow.LatestWorkspaceLoad);
                launchGate = new(TaskCreationOptions.RunContinuationsAsynchronously); Click(closingWindow, "workspace-launch"); closingWindow.Close();
                Check(launchToken.IsCancellationRequested, "actual close cancels remaining launch requests");
                launchGate.SetResult(new(new[] { new WorkspaceLaunchItemResult(urlId, "late launch", WorkspaceLaunchStatus.HandedOff) })); Wait(closingWindow, closingWindow.LatestWorkspaceLaunch);
                Check(Field<WorkspaceLaunchResult?>(closingWindow, "workspaceResult") is null && !closingWindow.IsVisible, "late launcher result cannot republish or reopen closed window");
            }
            finally { launchGate?.TrySetResult(new([])); closingWindow.Close(); Dispatcher.UIThread.RunJobs(); }
            AppearanceTestActions.Set(window, "en", false); string stored = Path.Combine(directory, "workspace-scenes.json"); byte[] external = File.ReadAllBytes(stored).Concat(new byte[] { 32 }).ToArray(); File.WriteAllBytes(stored, external);
            Set(window, "workspace-name", "must not overwrite external"); Click(window, "workspace-save-scene"); Wait(window, window.LatestWorkspaceSave);
            Check(File.ReadAllBytes(stored).SequenceEqual(external) && Find<TextBlock>(window, "workspace-feedback").Text!.Contains("externally", StringComparison.Ordinal), "external modification is not silently overwritten and feedback is localized");
            Click(window, "workspace-reload"); Wait(window, window.LatestWorkspaceLoad); Check(Find<TextBox>(window, "workspace-name").Text == "unsaved failure edit", "explicit reload takes external disk version");
            Find<Expander>(window, "workspace-shortcuts").IsExpanded = true; Find<ComboBox>(window, "workspace-resource-picker").SelectedIndex = 1; Flush(window);
            Click(window, "workspace-delete-resource"); Wait(window, window.LatestWorkspaceSave);
            Check(Data(window).Resources.Count == 1 && Data(window).Scenes.Single().ResourceIds.SequenceEqual(new[] { programId }), "deleting shared shortcut removes scene reference atomically");
            File.WriteAllText(stored, "{corrupt-owned-fixture"); Click(window, "workspace-reload"); Wait(window, window.LatestWorkspaceLoad);
            Check(Find<Button>(window, "workspace-recover").IsEnabled && File.ReadAllText(stored) == "{corrupt-owned-fixture", "unreadable data stays untouched until explicit recovery");
            Click(window, "workspace-recover"); Wait(window, window.LatestWorkspaceLoad);
            string? backup = Field<PortableWorkspaceStore>(window, "workspaceStore").LastRecoveryPath;
            Check(Data(window).Scenes.Count == 0 && backup is not null && File.ReadAllText(backup) == "{corrupt-owned-fixture", "explicit recovery preserves exact original bytes");
            Console.WriteLine($"PASS: {checks} workspace UI checks, {layouts} populated bilingual theme/size/DPI layouts; actual entry/scene saves, linked widgets, frozen launch, cancel/rebuild, literal arguments, write failure/conflict/recovery and picker lifetime. No real app/browser launched.");
        }
        finally { writeGate?.TrySetResult(true); launchGate?.TrySetResult(new([])); pickGate?.TrySetResult(null); window.Close(); Dispatcher.UIThread.RunJobs(); Directory.Delete(directory, true); }
    }
    private static void RecoveryRaceUi(string root)
    {
        foreach (string stage in new[] { "checked", "committed" })
        {
            string directory = Path.Combine(root, "recovery-" + stage); Directory.CreateDirectory(directory); string file = Path.Combine(directory, "workspace-scenes.json"); File.WriteAllText(file, "{owned corrupt");
            var controlled = new PortableWorkspaceStore(file, point => { if (point == stage) File.WriteAllText(file, "external-at-" + stage); }); controlled.Load();
            var window = new IslandWindow(true, directory);
            try
            {
                window.Show(); window.Expand(); AppearanceTestActions.Set(window, "en", false); window.SelectToolForTest("workspaces"); Wait(window, window.LatestWorkspaceLoad);
                typeof(IslandWindow).GetField("workspaceStore", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, controlled);
                Click(window, "workspace-recover"); Wait(window, window.LatestWorkspaceLoad);
                string feedback = Find<TextBlock>(window, "workspace-feedback").Text ?? "";
                Console.WriteLine("WORKSPACE RECOVERY: " + stage + "; " + feedback);
                Check(controlled.LastRecoveryPath is string backup && File.Exists(backup) && feedback.Contains(backup, StringComparison.Ordinal), "recovery race feedback retains exact backup path: " + stage);
                if (stage == "checked") Check(!controlled.LoadFailed && Data(window).Scenes.Count == 0 && feedback.Contains("written", StringComparison.Ordinal), "confirmed recovery commit reports written state and synchronizes UI snapshot");
                else Check(controlled.NeedsReload && feedback.Contains("could not be confirmed", StringComparison.Ordinal) && !Find<Button>(window, "workspace-recover").IsEnabled, "ambiguous recovery commit requires reload without falsely claiming no write");
            }
            finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
        }
    }
    private static void BlockedStoreUi(string root)
    {
        string directory = Path.Combine(root, "blocked-store"); Directory.CreateDirectory(directory);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); bool armed = false;
        var store = new PortableWorkspaceStore(Path.Combine(directory, "workspace-scenes.json"), point => { if (armed && point == "prepared") { entered.Set(); if (!release.Wait(10000)) throw new TimeoutException("workspace UI gate"); } });
        store.Load(); store.Save(new([], new[] { new PortableWorkspaceScene(Guid.NewGuid().ToString("N"), "Owned empty layout", [], "clock", "focus") }));
        var window = new IslandWindow(true, directory);
        try
        {
            window.Show(); window.Expand(); AppearanceTestActions.Set(window, "en", false); window.SelectToolForTest("workspaces"); Wait(window, window.LatestWorkspaceLoad);
            typeof(IslandWindow).GetField("workspaceStore", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, store);
            Set(window, "workspace-name", "Save held by fixture"); armed = true; Click(window, "workspace-save-scene");
            var wait = Stopwatch.StartNew(); while (!entered.IsSet && wait.ElapsedMilliseconds < 5000) { Flush(window); Thread.Sleep(2); }
            Check(entered.IsSet, "actual store save reached owned blocked I/O checkpoint");
            var safetyRelease = Task.Run(async () => { await Task.Delay(1500); release.Set(); });
            var elapsed = Stopwatch.StartNew(); AppearanceTestActions.Set(window, "zh", true); elapsed.Stop();
            Console.WriteLine($"WORKSPACE RESPONSIVENESS: header transition={elapsed.ElapsedMilliseconds}ms, writer released={release.IsSet}");
            Check(!release.IsSet, "header rebuild must finish while the background store still holds its I/O lock");
            release.Set(); Wait(window, window.LatestWorkspaceSave); safetyRelease.GetAwaiter().GetResult();
        }
        finally { release.Set(); Wait(window, window.LatestWorkspaceSave); window.Close(); Dispatcher.UIThread.RunJobs(); }
    }
    private static int Layouts(IslandWindow window, string? artifacts)
    {
        int count = 0;
        foreach (string language in new[] { "zh", "en" }) foreach (bool light in new[] { false, true }) foreach (int width in new[] { 320, 480, 760 }) foreach (int height in new[] { 360, 720 }) foreach (double scale in new[] { 1d, 1.25, 1.5, 2 })
        {
            window.SetRenderScaling(scale); AppearanceTestActions.Set(window, language, light); window.Width = width; window.Height = height; Flush(window);
            string context = $"{language}/{light}/{width}/{height}/{scale}";
            foreach (var button in window.GetVisualDescendants().OfType<Button>().Where(item => item.IsEffectivelyVisible && item.Bounds.Width > 0))
            {
                if (button.Content is TextBlock text) Check(text.Bounds.Height <= button.Bounds.Height + 2, "caption height " + context);
                var point = button.TranslatePoint(default, window); Check(point is Point p && p.X >= -2 && p.X + button.Bounds.Width <= width + 2, "horizontal button fit " + context);
            }
            Check(Find<TextBox>(window, "workspace-name").Text == "unsaved launch label", "scene draft retained through actual header transitions " + context);
            foreach (var pair in new[] { new[] { "workspace-save-scene", "workspace-delete-scene" }, new[] { "workspace-launch", "workspace-cancel" }, new[] { "workspace-save-resource", "workspace-delete-resource" } })
            {
                var first = Find<Button>(window, pair[0]); var second = Find<Button>(window, pair[1]);
                Check(Math.Abs(first.Bounds.Width - second.Bounds.Width) <= 1 && Math.Abs(first.Bounds.Height - second.Bounds.Height) <= 1 && Math.Abs(first.Bounds.Y - second.Bounds.Y) <= 1, "same-row actions have equal size and alignment " + context);
            }
            if (artifacts is not null && height == 720 && scale == 1 && (language == "en" && light && width == 320 || language == "zh" && !light && width == 480))
            {
                Directory.CreateDirectory(artifacts); AvaloniaHeadlessPlatform.ForceRenderTimerTick(3); Dispatcher.UIThread.RunJobs();
                using var image = new RenderTargetBitmap(new(width, height), new(96, 96)); image.Render(window); image.Save(Path.Combine(artifacts, $"workspace-filled-{language}-{width}.png"), PngBitmapEncoderOptions.Default);
            }
            count++;
        }
        return count;
    }
    private static PortableWorkspaceSnapshot Data(IslandWindow window) => Field<PortableWorkspaceSnapshot>(window, "workspaceData");
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Set(Window window, string name, string text) => Find<TextBox>(window, name).Text = text;
    private static void Click(Window window, string name) { var button = Find<Button>(window, name); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Flush(window); }
    private static void Flush(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
    private static void Wait(Window window, Task task)
    {
        var time = Stopwatch.StartNew(); while (!task.IsCompleted && time.ElapsedMilliseconds < 10000) { Flush(window); Thread.Sleep(2); }
        if (!task.IsCompleted) throw new TimeoutException("Workspace operation did not finish"); task.GetAwaiter().GetResult(); Flush(window);
    }
    private static void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException("Workspace UI: " + message); }
}
