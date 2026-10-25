using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodexIsland
{
    internal sealed class WorkspaceScenePanel : StackPanel, IDisposable
    {
        private readonly bool demo;
        private readonly WorkspaceSceneStore store;
        private readonly ShortcutStore shortcutStore;
        private readonly IWorkspaceSceneLaunchAdapter launcher;
        private readonly List<WorkspaceScene> scenes;
        private readonly StackPanel sceneList = new StackPanel(), resourceList = new StackPanel(), shortcutList = new StackPanel();
        private readonly TextBlock status = ToolStyle.Hint("");
        private readonly TextBox sceneName = ToolStyle.Input();
        private readonly StackPanel editor = new StackPanel();
        private readonly List<KeyValuePair<CheckBox, QuickShortcut>> importChecks = new List<KeyValuePair<CheckBox, QuickShortcut>>();
        private readonly CancellationTokenSource closing = new CancellationTokenSource();
        private readonly List<Button> launchButtons = new List<Button>();
        private WorkspaceScene selected;
        private bool dirty, disposed, updating, launching;
        private ComboBox layoutSelection;
        private Func<IEnumerable<LayoutProfile>> layoutOptions;
        private Func<string, bool> applyLayout;
        private bool changingLayout;
        internal void ConfigureLayouts(Func<IEnumerable<LayoutProfile>> options, Func<string, bool> apply)
        {
            layoutOptions = options; applyLayout = apply;
            if (layoutSelection == null)
            {
                editor.Children.Add(ToolStyle.Hint("启动时应用场景布局")); layoutSelection = new ComboBox { MinHeight = 32, Margin = new Thickness(0, 0, 0, 6) }; editor.Children.Add(layoutSelection);
                layoutSelection.DropDownOpened += (s, e) => RefreshLayouts();
                layoutSelection.SelectionChanged += (s, e) => { if (changingLayout || selected == null) return; var choice = layoutSelection.SelectedItem as ComboBoxItem; selected.LayoutProfileId = choice == null ? "" : choice.Tag as string; dirty = true; };
            }
            RefreshLayouts();
        }
        private void RefreshLayouts()
        {
            if (layoutSelection == null) return; changingLayout = true;
            try
            {
                layoutSelection.Items.Clear(); layoutSelection.Items.Add(UiLocalization.Content(new ComboBoxItem { Tag = "" }, "不切换布局"));
                if (layoutOptions != null) foreach (var profile in layoutOptions())
                {
                    var choice = new ComboBoxItem { Content = profile.Name, Tag = profile.Id };
                    layoutSelection.Items.Add(LayoutProfileStore.IsBuiltin(profile.Id) ? UiLocalization.Content(choice, profile.Name) : UiLocalization.Protect(choice));
                }
                string id = selected == null ? "" : selected.LayoutProfileId ?? ""; layoutSelection.SelectedIndex = 0;
                bool found = String.IsNullOrEmpty(id);
                foreach (ComboBoxItem choice in layoutSelection.Items) if ((string)choice.Tag == id) { layoutSelection.SelectedItem = choice; found = true; break; }
                if (!found) { var missing = UiLocalization.Content(new ComboBoxItem { Tag = id }, "关联布局已被删除"); layoutSelection.Items.Add(missing); layoutSelection.SelectedItem = missing; }
            }
            finally { changingLayout = false; }
        }
        internal bool HasUnsavedChanges { get { return dirty; } }
        public WorkspaceScenePanel(bool demoMode) : this(demoMode, new WorkspaceSceneStore(), new ShortcutStore(), new SystemWorkspaceSceneLaunchAdapter()) { }
        internal WorkspaceScenePanel(bool demoMode, WorkspaceSceneStore sceneStore, ShortcutStore shortcuts, IWorkspaceSceneLaunchAdapter adapter)
        {
            demo = demoMode; store = sceneStore; shortcutStore = shortcuts; launcher = adapter;
            scenes = demo ? new List<WorkspaceScene> { new WorkspaceScene { Id = Guid.NewGuid().ToString("N"), Name = "写代码", Resources = new List<WorkspaceSceneResource> { new WorkspaceSceneResource { Name = "项目文档", Kind = "url", Target = "https://example.com/docs", Arguments = "" } } } } : store.Load();
            Children.Add(ToolStyle.Heading("工作场景启动"));
            Children.Add(ToolStyle.Hint(demo ? "演示模式 · 不启动软件、不打开网页、不保存修改。" : "将项目目录、程序和网页组成场景，点一次批量打开。最多 20 个场景，每个场景 30 个资源。"));
            Children.Add(PanelActionLayout.Create(1, IslandWindow.Button("＋ 新建场景", NewScene))); Children.Add(status); Children.Add(sceneList);
            editor.Margin = new Thickness(0, 6, 0, 0); editor.Children.Add(ToolStyle.Heading("编辑当前场景")); editor.Children.Add(ToolStyle.Hint("场景名称")); sceneName.MaxLength = 60; editor.Children.Add(sceneName);
            sceneName.TextChanged += (s, e) => { if (updating || selected == null || disposed) return; selected.Name = sceneName.Text; dirty = true; status.Text = "名称已修改，请点击保存场景。"; };
            var actions = PanelActionLayout.Create(2,
                IslandWindow.Button("保存场景", () => { if (TryFlush()) { RebuildScenes(); status.Text = demo ? "演示模式不保存修改。" : "场景已保存。"; } }),
                IslandWindow.Button("＋ 添加资源", () => EditResource(null))); actions.Margin = new Thickness(0, 6, 0, 6); editor.Children.Add(actions); editor.Children.Add(resourceList);
            var importBody = new StackPanel(); importBody.Children.Add(ToolStyle.Hint("勾选已在「快捷启动」中保存的入口，可复制加入当前场景。原入口不受影响。"));
            importBody.Children.Add(PanelActionLayout.Create(2, IslandWindow.Button("刷新快捷入口", RefreshShortcuts), IslandWindow.Button("加入勾选入口", ImportChecked))); importBody.Children.Add(shortcutList);
            var importHeading = IslandWindow.Text("复用已有快捷入口", 12, "#C7E9D7"); importHeading.TextWrapping = TextWrapping.Wrap;
            editor.Children.Add(new Expander { Header = importHeading, Content = importBody, Foreground = Display.Brush("#C7E9D7"), Margin = new Thickness(0, 6, 0, 0) });
            Children.Add(editor); RefreshShortcuts(); RebuildScenes(); SelectScene(scenes.FirstOrDefault());
            if (!demo && store.LoadFailed) status.Text = "场景文件无法读取，原文件会在下次成功保存前先备份。";
        }
        private void NewScene()
        {
            if (disposed) return; if (scenes.Count >= WorkspaceSceneStore.MaximumScenes) { status.Text = "最多创建 20 个场景。"; return; }
            if (selected != null && String.IsNullOrWhiteSpace(selected.Name)) { status.Text = "请先为当前场景填写名称。"; return; }
            var scene = new WorkspaceScene { Id = Guid.NewGuid().ToString("N"), Name = "新场景 " + (scenes.Count + 1), Resources = new List<WorkspaceSceneResource>() };
            scenes.Add(scene); dirty = true; SelectScene(scene); Persist(); RebuildScenes(); sceneName.Focus(); sceneName.SelectAll();
        }
        private void SelectScene(WorkspaceScene scene)
        {
            selected = scene; updating = true; sceneName.Text = scene == null ? "" : scene.Name; updating = false;
            editor.Visibility = scene == null ? Visibility.Collapsed : Visibility.Visible; RebuildResources();
            RefreshLayouts();
        }
        private void RebuildScenes()
        {
            sceneList.Children.Clear(); launchButtons.Clear();
            foreach (WorkspaceScene scene in scenes)
            {
                var captured = scene; var card = new StackPanel(); var title = IslandWindow.Text(scene.Name + " · " + UiLocalization.Localize(scene.Resources.Count + " 项"), 13, "#D5EADC", true, false); title.TextWrapping = TextWrapping.Wrap; card.Children.Add(title);
                var launch = IslandWindow.Button("启动场景", async () => await LaunchSceneAsync(captured)); launch.IsEnabled = !launching; launchButtons.Add(launch);
                var actions = PanelActionLayout.Create(3, launch,
                    IslandWindow.Button("编辑", () => SelectScene(captured)),
                    IslandWindow.Button("删除", () => { scenes.Remove(captured); dirty = true; if (Object.ReferenceEquals(selected, captured)) SelectScene(scenes.FirstOrDefault()); Persist(); RebuildScenes(); })); actions.Margin = new Thickness(0, 6, 0, 0);
                card.Children.Add(actions); sceneList.Children.Add(ToolStyle.Card(card));
            }
            if (scenes.Count == 0) sceneList.Children.Add(ToolStyle.Hint("尚无场景，点击新建开始组合你的常用资源。"));
        }
        private void RebuildResources()
        {
            resourceList.Children.Clear(); if (selected == null) return;
            foreach (WorkspaceSceneResource resource in selected.Resources)
            {
                var captured = resource; var stack = new StackPanel(); var label = IslandWindow.Text(resource.Name, 12, "#DBECE1", true, false); label.TextWrapping = TextWrapping.Wrap; stack.Children.Add(label);
                var target = ToolStyle.Hint(resource.Target + (String.IsNullOrEmpty(resource.Arguments) ? "" : "\n" + UiLocalization.Localize("参数：") + resource.Arguments), false); target.MaxHeight = 64; target.TextTrimming = TextTrimming.CharacterEllipsis; target.ToolTip = resource.Target + (String.IsNullOrEmpty(resource.Arguments) ? "" : "\n" + resource.Arguments); stack.Children.Add(target);
                stack.Children.Add(PanelActionLayout.Create(2, IslandWindow.Button("编辑资源", () => EditResource(captured)), IslandWindow.Button("移除", () => { selected.Resources.Remove(captured); dirty = true; Persist(); RebuildResources(); RebuildScenes(); }))); resourceList.Children.Add(ToolStyle.Card(stack));
            }
            if (selected.Resources.Count == 0) resourceList.Children.Add(ToolStyle.Hint("还没有资源，可添加文件夹、程序、网页，或复用已有快捷入口。"));
        }
        private void RefreshShortcuts()
        {
            importChecks.Clear(); shortcutList.Children.Clear();
            var shortcuts = demo ? new List<QuickShortcut> { new QuickShortcut { Name = "示例网页", Target = "https://example.com/" } } : shortcutStore.Load();
            foreach (var shortcut in shortcuts)
            {
                var check = UiLocalization.Protect(new CheckBox { Content = shortcut.Name, ToolTip = shortcut.Target, Foreground = Display.Brush("#C7DECF"), Margin = new Thickness(0, 5, 0, 5) }); importChecks.Add(new KeyValuePair<CheckBox, QuickShortcut>(check, shortcut)); shortcutList.Children.Add(check);
            }
            if (shortcuts.Count == 0) shortcutList.Children.Add(ToolStyle.Hint("暂无可复用入口，请先到快捷启动中添加。"));
            if (!demo && shortcutStore.LoadFailed) status.Text = "部分快捷入口无法读取，请到快捷启动中检查。原入口没有被修改。";
        }
        private void ImportChecked()
        {
            if (selected == null) return; int added = 0, duplicate = 0; bool full = false;
            foreach (var pair in importChecks.Where(x => x.Key.IsChecked == true))
            {
                WorkspaceSceneResource item;
                try { item = WorkspaceSceneLaunch.FromShortcut(pair.Value); WorkspaceSceneLaunch.ValidateShape(item); } catch (ArgumentException) { continue; }
                if (selected.Resources.Any(x => x.Kind == item.Kind && String.Equals(x.Target, item.Target, item.Kind == "url" ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase) && (x.Arguments ?? "") == (item.Arguments ?? ""))) { duplicate++; continue; }
                if (selected.Resources.Count >= WorkspaceSceneStore.MaximumResources) { full = true; break; }
                selected.Resources.Add(item); added++; pair.Key.IsChecked = false;
            }
            if (added > 0) { dirty = true; if (TryFlush()) status.Text = "已加入 " + added + " 个入口" + (duplicate > 0 ? "，已跳过重复入口。" : "。") + (full ? " 每个场景最多 30 个资源，其余入口未加入。" : "") + (demo ? " 演示模式未保存。" : ""); }
            else if (full) status.Text = "每个场景最多 30 个资源，入口未加入。";
            else if (duplicate > 0) status.Text = "勾选入口已在当前场景中。";
            else status.Text = "请先勾选要加入的快捷入口。";
            RebuildResources(); RebuildScenes();
        }
        private void EditResource(WorkspaceSceneResource existing)
        {
            if (selected == null || disposed) return; if (existing == null && selected.Resources.Count >= WorkspaceSceneStore.MaximumResources) { status.Text = "每个场景最多 30 个资源。"; return; }
            CreateResourceEditor(existing).ShowDialog();
        }
        internal CompactEditorWindow CreateResourceEditor(WorkspaceSceneResource existing)
        {
            WorkspaceScene destination = selected;
            var window = new CompactEditorWindow(existing == null ? "添加场景资源" : "编辑场景资源", 520, Window.GetWindow(this));
            var body = CompactDialogLayout.Card(window);
            var name = CompactDialogLayout.Input(existing == null ? "" : existing.Name, 60); name.Name = "ResourceName";
            var type = UiTheme.CompactChoice(UiLocalization.Combo(new ComboBox { ItemsSource = new[] { "网页 http(s)", "项目文件夹", "程序 .exe", "快捷方式 .lnk" }, SelectedIndex = existing == null ? 0 : Array.IndexOf(new[] { "url", "folder", "program", "shortcut" }, existing.Kind), MinHeight = 32, FontSize = 12, Padding = new Thickness(10, 6, 10, 6) })); type.Name = "ResourceType";
            var target = CompactDialogLayout.Input(existing == null ? "" : existing.Target, 2048); target.Name = "ResourceTarget";
            var arguments = CompactDialogLayout.Input(existing == null ? "" : existing.Arguments ?? "", 4096); arguments.Name = "ResourceArguments"; arguments.IsEnabled = type.SelectedIndex == 2;
            type.SelectionChanged += (s, e) => { arguments.IsEnabled = type.SelectedIndex == 2; if (!arguments.IsEnabled) arguments.Clear(); };
            CompactDialogLayout.Field(body, "资源名称", name); CompactDialogLayout.Field(body, "资源类型", type);
            body = CompactDialogLayout.Card(window); CompactDialogLayout.Field(body, "完整路径或网址", target);
            var browse = CompactDialogLayout.Browse(
                IslandWindow.Button("选择程序 / 快捷方式", () => { var picker = new Microsoft.Win32.OpenFileDialog { Title = UiLocalization.Localize("选择程序 / 快捷方式"), Filter = UiLocalization.FileFilter("程序和快捷方式|*.exe;*.lnk") }; if (picker.ShowDialog(window) == true) { target.Text = picker.FileName; type.SelectedIndex = String.Equals(Path.GetExtension(picker.FileName), ".lnk", StringComparison.OrdinalIgnoreCase) ? 3 : 2; if (String.IsNullOrWhiteSpace(name.Text)) name.Text = Path.GetFileNameWithoutExtension(picker.FileName); } }),
                IslandWindow.Button("选择文件夹", () => { using (var picker = new System.Windows.Forms.FolderBrowserDialog { Description = UiLocalization.Localize("选择文件夹") }) { if (NativeDialogs.ShowFolder(picker, window) == System.Windows.Forms.DialogResult.OK) { target.Text = picker.SelectedPath; type.SelectedIndex = 1; if (String.IsNullOrWhiteSpace(name.Text)) name.Text = Path.GetFileName(picker.SelectedPath); } } })); body.Children.Add(browse);
            body = CompactDialogLayout.Card(window); CompactDialogLayout.Field(body, "程序启动参数（可选，单独传给所选程序）", arguments); var error = window.Error;
            Button save = null; bool validating = false;
            save = IslandWindow.Button("保存资源", async () =>
            {
                if (validating) return;
                var candidate = new WorkspaceSceneResource { Name = name.Text.Trim(), Kind = new[] { "url", "folder", "program", "shortcut" }[Math.Max(0, type.SelectedIndex)], Target = target.Text.Trim(), Arguments = type.SelectedIndex == 2 ? arguments.Text : "" };
                validating = true; save.IsEnabled = false; error.Text = "正在检查资源…";
                try
                {
                    await Task.Run(() => WorkspaceSceneLaunch.CreateStartInfo(candidate)); if (disposed || !window.IsVisible) return;
                    if (existing == null) destination.Resources.Add(candidate); else { existing.Name = candidate.Name; existing.Kind = candidate.Kind; existing.Target = candidate.Target; existing.Arguments = candidate.Arguments; }
                    dirty = true; bool saved = TryFlush(); RebuildResources(); RebuildScenes(); if (saved) window.DialogResult = true; else { error.Text = "保存失败；此修改已保留在本次运行中，可直接关闭窗口后在场景页重试保存。"; existing = existing ?? candidate; }
                }
                catch (Exception failure) { error.Text = failure is ArgumentException ? failure.Message : "无法访问此资源，请检查路径后重试。"; }
                finally { validating = false; save.IsEnabled = true; }
            }); window.SetActions(save); return window;
        }
        internal async Task<bool> LaunchNamedAsync(string name)
        {
            if (disposed || launching) return false;
            var matches = scenes.Where(scene => String.Equals(scene.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            return matches.Count == 1 && await LaunchSceneAsync(matches[0]) != null;
        }
        internal async Task<WorkspaceSceneLaunchResult> LaunchSceneAsync(WorkspaceScene scene)
        {
            if (disposed || launching || scene == null) return null;
            bool requestedLayout = !String.IsNullOrEmpty(scene.LayoutProfileId);
            bool layoutApplied = requestedLayout && applyLayout != null && applyLayout(scene.LayoutProfileId);
            if (scene.Resources.Count == 0) { status.Text = layoutApplied ? "已应用场景布局。" : "场景还没有资源，请先添加。"; return new WorkspaceSceneLaunchResult(); }
            launching = true; foreach (var button in launchButtons) button.IsEnabled = false; status.Text = "正在启动「" + scene.Name + "」…";
            try
            {
                WorkspaceSceneLaunchResult result = await WorkspaceSceneLaunch.RunAsync(scene.Resources, demo, launcher, closing.Token);
                if (requestedLayout && !layoutApplied) result.Errors.Add(UiLocalization.Localize("关联的布局不存在，资源启动继续执行。"));
                if (!disposed) status.Text = (demo ? "演示模式，已检查 " + result.Skipped + " 个资源，未实际打开。" : "已启动 " + result.Opened + " 个资源。") + (result.Errors.Count == 0 ? "" : "\n" + String.Join("\n", result.Errors));
                return result;
            }
            catch (Exception) { if (!disposed) status.Text = "场景启动失败，请检查资源后重试。"; return null; }
            finally { launching = false; if (!disposed) foreach (var button in launchButtons) button.IsEnabled = true; }
        }
        private void Persist() { if (TryFlush()) status.Text = demo ? "演示模式不保存修改。" : "场景已保存。"; }
        public bool TryFlush()
        {
            if (demo || !dirty) return true;
            try { store.Save(scenes); dirty = false; return true; }
            catch (ArgumentException error) { status.Text = error.Message + " 修改仍保留在本次运行中。"; }
            catch (IOException) { status.Text = "场景保存失败；修改仍保留在本次运行中，请检查目录权限后重试保存。"; }
            catch (UnauthorizedAccessException) { status.Text = "场景保存失败；修改仍保留在本次运行中，请检查目录权限后重试保存。"; }
            return false;
        }
        public void Dispose() { if (disposed) return; TryFlush(); disposed = true; closing.Cancel(); }
    }
}
