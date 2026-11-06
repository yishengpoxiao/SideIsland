using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using SideIsland.Core;
using SideIsland.Desktop.Platforms;

namespace SideIsland.Desktop;

public sealed partial class IslandWindow
{
    private PortableWorkspaceStore? workspaceStore;
    private PortableWorkspaceSnapshot workspaceData = new([], []);
    private bool workspaceLoaded, workspaceLoading, workspaceSaving, workspaceChoosing, resourceExpanded;
    private bool workspaceLoadFailed, workspaceNeedsReload, workspaceCanRecover;
    private bool workspaceClosePending;
    private CancellationTokenSource? workspaceLaunch;
    private WorkspaceLaunchResult? workspaceResult;
    private string workspaceLaunchName = "", workspaceMessage = "", workspaceRecoveryPath = "";
    private string? sceneId, resourceId;
    private string sceneName = "", resourceName = "", resourceKind = "url", resourceTarget = "";
    private readonly List<string> resourceArguments = new();
    private string? sceneSlot1, sceneSlot2;
    private readonly HashSet<string> sceneResources = new(StringComparer.Ordinal);
    private StackPanel? workspaceView, workspaceResults;
    private TextBlock? workspaceFeedback;
    private Button? workspaceRunButton, workspaceCancelButton;
    internal Task LatestWorkspaceLoad { get; private set; } = Task.CompletedTask;
    internal Task LatestWorkspaceSave { get; private set; } = Task.CompletedTask;
    internal Task LatestWorkspaceLaunch { get; private set; } = Task.CompletedTask;
    internal Task LatestWorkspaceChoose { get; private set; } = Task.CompletedTask;
    private bool WorkspaceEditable => workspaceLoaded && !workspaceLoading && !workspaceSaving && !closing && !workspaceLoadFailed && !workspaceNeedsReload;

    private void RenderWorkspaces()
    {
        workspaceView = new StackPanel { Spacing = 5 }; body.Children.Add(workspaceView);
        workspaceFeedback = Hint(""); workspaceFeedback.Name = "workspace-feedback"; workspaceView.Children.Add(workspaceFeedback);
        if (!workspaceLoaded)
        {
            workspaceFeedback.Text = T("正在读取工作场景…", "Loading workspaces…");
            if (!workspaceLoading) LatestWorkspaceLoad = LoadWorkspacesAsync(false);
            return;
        }
        var reload = AsyncButton(T("重新加载", "Reload"), () => LatestWorkspaceLoad = LoadWorkspacesAsync(false), () => !workspaceSaving && !workspaceLoading && !closing); reload.Name = "workspace-reload";
        if (workspaceLoadFailed)
        {
            workspaceView.Children.Add(Hint(workspaceNeedsReload ? T("请重新加载以核对当前磁盘内容。恢复副本保留，核对前不能再次重建。", "Reload to inspect the current disk contents. Recovery copies are retained; another rebuild is unavailable until reloaded.") : T("保存文件无法读取。原文件保留；可用时，备份并重建将创建空列表并保留原始副本。", "The saved file could not be read. It has been retained. When available, Back up & rebuild creates empty lists and keeps the original copy.")));
            var recover = AsyncButton(T("备份并重建", "Back up & rebuild"), () => LatestWorkspaceLoad = LoadWorkspacesAsync(true), () => workspaceCanRecover && !workspaceSaving && !workspaceLoading && !closing); recover.Name = "workspace-recover";
            workspaceView.Children.Add(WorkspaceActions(reload, recover)); UpdateWorkspaceFeedback(); return;
        }

        workspaceView.Children.Add(Hint(T("保存快捷入口，再勾选组合成场景。修改后请保存；切换条目会载入对应的已保存内容。", "Save shortcuts, then select them for a workspace. Save your edits before selecting another item.")));
        var editor = new StackPanel { Spacing = 5, IsEnabled = WorkspaceEditable }; workspaceView.Children.Add(editor);
        string[] sceneIds = workspaceData.Scenes.Select(item => item.Id).ToArray();
        var scenes = new ComboBox { Name = "workspace-scene-picker", ItemsSource = new[] { T("新场景", "New workspace") }.Concat(workspaceData.Scenes.Select(item => item.Name)).ToArray(), SelectedIndex = sceneId is null ? 0 : Array.IndexOf(sceneIds, sceneId) + 1 };
        scenes.SelectionChanged += (_, _) => { if (scenes.SelectedIndex < 0 || !WorkspaceEditable) return; SelectWorkspaceScene(scenes.SelectedIndex == 0 ? null : sceneIds[scenes.SelectedIndex - 1]); RebuildWorkspacePage(); };
        editor.Children.Add(scenes);
        editor.Children.Add(WorkspaceInput("workspace-name", sceneName, value => sceneName = value, T("场景名称", "Workspace name"), 60));
        var choices = new StackPanel { Spacing = 1 };
        foreach (var item in workspaceData.Resources)
        {
            var check = Check(item.Name, sceneResources.Contains(item.Id), enabled => { if (enabled) sceneResources.Add(item.Id); else sceneResources.Remove(item.Id); });
            check.Name = "workspace-include-" + item.Id; choices.Children.Add(check);
        }
        if (choices.Children.Count == 0) choices.Children.Add(Hint(T("先在下方添加快捷入口。", "Add shortcuts below first.")));
        editor.Children.Add(new ScrollViewer { Name = "workspace-resources", Content = choices, MaxHeight = 160, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        editor.Children.Add(Hint(T("按入口列表顺序启动。最多 20 个场景，每场景 30 个入口。", "Launches in shortcut-list order. Up to 20 workspaces, 30 shortcuts each.")));
        editor.Children.Add(WorkspaceWidgetPicker("workspace-slot-1", sceneSlot1, value => sceneSlot1 = value, true));
        editor.Children.Add(WorkspaceWidgetPicker("workspace-slot-2", sceneSlot2, value => sceneSlot2 = value, false));
        var saveScene = AsyncButton(T("保存场景", "Save workspace"), () => LatestWorkspaceSave = SaveWorkspaceSceneAsync(), () => WorkspaceEditable); saveScene.Name = "workspace-save-scene";
        var removeScene = AsyncButton(T("删除场景", "Delete workspace"), () => LatestWorkspaceSave = RemoveWorkspaceSceneAsync(), () => WorkspaceEditable && sceneId is not null); removeScene.Name = "workspace-delete-scene";
        editor.Children.Add(WorkspaceActions(saveScene, removeScene));

        workspaceRunButton = AsyncButton(T("启动已保存场景", "Launch saved workspace"), () => LatestWorkspaceLaunch = LaunchWorkspaceAsync(), CanLaunchWorkspace); workspaceRunButton.Name = "workspace-launch";
        workspaceCancelButton = Button(T("取消后续启动", "Cancel remaining"), () => { workspaceLaunch?.Cancel(); UpdateWorkspaceActions(); }); workspaceCancelButton.Name = "workspace-cancel";
        workspaceView.Children.Add(WorkspaceActions(workspaceRunButton, workspaceCancelButton));
        workspaceView.Children.Add(Hint(T("启动使用已保存内容；可切换两个收起组件。不会自动连接账号、开始计时或终止已经打开的软件。", "Launch uses saved content and optional collapsed widgets. It does not connect accounts, start timers or stop opened apps.")));
        workspaceResults = new StackPanel { Spacing = 4 }; workspaceResults.Name = "workspace-results"; workspaceView.Children.Add(workspaceResults);

        var resourceEditor = RenderWorkspaceResourceEditor();
        var expander = new Expander { Name = "workspace-shortcuts", Header = Text(T("快捷入口", "Shortcuts"), 13), IsExpanded = resourceExpanded, Content = resourceEditor, HorizontalContentAlignment = HorizontalAlignment.Stretch, IsEnabled = WorkspaceEditable };
        expander.PropertyChanged += (_, e) => { if (e.Property == Expander.IsExpandedProperty) resourceExpanded = expander.IsExpanded; }; workspaceView.Children.Add(expander);
        workspaceView.Children.Add(WorkspaceActions(reload));
        workspaceView.Children.Add(Hint(T("重新加载会丢弃未保存编辑。数据仅存于本机；检测到外部修改时保存会停止，避免覆盖其他编辑。", "Reload discards unsaved edits. Data stays local; saving stops when an external change is detected.")));
        UpdateWorkspaceFeedback(); UpdateWorkspaceActions(); RenderWorkspaceResults();
    }

    private Control RenderWorkspaceResourceEditor()
    {
        var editor = new StackPanel { Spacing = 5 };
        string[] ids = workspaceData.Resources.Select(item => item.Id).ToArray();
        var picker = new ComboBox { Name = "workspace-resource-picker", ItemsSource = new[] { T("新入口", "New shortcut") }.Concat(workspaceData.Resources.Select(item => item.Name)).ToArray(), SelectedIndex = resourceId is null ? 0 : Array.IndexOf(ids, resourceId) + 1 };
        picker.SelectionChanged += (_, _) => { if (picker.SelectedIndex < 0 || !WorkspaceEditable) return; SelectWorkspaceResource(picker.SelectedIndex == 0 ? null : ids[picker.SelectedIndex - 1]); RebuildWorkspacePage(); }; editor.Children.Add(picker);
        editor.Children.Add(WorkspaceInput("workspace-resource-name", resourceName, value => resourceName = value, T("入口名称", "Shortcut name"), 60));
        string[] kinds = ["url", "folder", "program"];
        var kind = new ComboBox { Name = "workspace-resource-kind", ItemsSource = new[] { T("网页", "Web page"), T("文件夹", "Folder"), T("程序", "Program") }, SelectedIndex = Array.IndexOf(kinds, resourceKind) };
        kind.SelectionChanged += (_, _) => { if (kind.SelectedIndex < 0) return; resourceKind = kinds[kind.SelectedIndex]; RebuildWorkspacePage(); }; editor.Children.Add(kind);
        var target = WorkspaceInput("workspace-resource-target", resourceTarget, value => resourceTarget = value, resourceKind == "url" ? "https://…" : T("绝对路径", "Absolute path"), 4096); editor.Children.Add(target);
        if (resourceKind != "url")
        {
            var choose = AsyncButton(T("选择路径", "Choose path"), () => LatestWorkspaceChoose = ChooseWorkspaceTargetAsync(target), () => WorkspaceEditable && !workspaceChoosing); choose.Name = "workspace-choose"; editor.Children.Add(choose);
        }
        if (resourceKind == "program")
        {
            editor.Children.Add(Hint(T("每一栏是一个参数，原样传递（包括空白），无需加外层引号。Mac 可填 .app 目录或可执行文件；Ubuntu 填可执行文件。", "Each field is one literal argument, including blank values; no wrapper quotes needed. Mac accepts an .app directory or executable; Ubuntu accepts an executable.")));
            for (int index = 0; index < resourceArguments.Count; index++)
            {
                int slot = index;
                var args = WorkspaceInput("workspace-argument-" + slot, resourceArguments[slot], value => resourceArguments[slot] = value, T("参数 ", "Argument ") + (slot + 1), 1024, true); args.MinHeight = 34; args.MaxHeight = 90;
                var removeArg = Button(T("移除", "Remove"), () => { resourceArguments.RemoveAt(slot); RebuildWorkspacePage(); }); removeArg.Name = "workspace-remove-argument-" + slot;
                var row = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 6 }; Grid.SetColumn(removeArg, 1); row.Children.Add(args); row.Children.Add(removeArg); editor.Children.Add(row);
            }
            var addArg = Button(T("添加参数", "Add argument"), () => { if (resourceArguments.Count < 32) { resourceArguments.Add(""); RebuildWorkspacePage(); } }); addArg.Name = "workspace-add-argument"; addArg.IsEnabled = resourceArguments.Count < 32; editor.Children.Add(addArg);
        }
        var save = AsyncButton(T("保存入口", "Save shortcut"), () => LatestWorkspaceSave = SaveWorkspaceResourceAsync(), () => WorkspaceEditable); save.Name = "workspace-save-resource";
        var remove = AsyncButton(T("删除入口", "Delete shortcut"), () => LatestWorkspaceSave = RemoveWorkspaceResourceAsync(), () => WorkspaceEditable && resourceId is not null); remove.Name = "workspace-delete-resource";
        editor.Children.Add(WorkspaceActions(save, remove));
        editor.Children.Add(Hint(T("最多 60 个入口。删除入口会同时移除场景中的引用，不删除磁盘文件。", "Up to 60 shortcuts. Removing one also removes its workspace references; disk files are unchanged."))); return editor;
    }

    private TextBox WorkspaceInput(string name, string value, Action<string> changed, string placeholder, int limit, bool multiline = false)
    {
        var box = new TextBox { Name = name, Text = value, PlaceholderText = placeholder, MaxLength = limit, MinHeight = multiline ? 90 : 34, MaxHeight = multiline ? 160 : double.PositiveInfinity, AcceptsReturn = multiline, TextWrapping = multiline ? Avalonia.Media.TextWrapping.Wrap : Avalonia.Media.TextWrapping.NoWrap };
        box.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) changed(box.Text ?? ""); }; return box;
    }
    private Control WorkspaceActions(params Control[] controls)
    {
        foreach (var control in controls) control.VerticalAlignment = VerticalAlignment.Stretch;
        return Actions(controls);
    }
    private ComboBox WorkspaceWidgetPicker(string name, string? value, Action<string?> changed, bool first)
    {
        string[] labels = [T("Codex 短期额度", "Codex short-term"), T("Codex 每周额度", "Codex weekly"), T("专注计时", "Focus"), T("时钟", "Clock"), T("音乐", "Music"), T("任务动态", "Tasks"), T("保持唤醒", "Keep awake"), T("未读通知", "Unread notifications")];
        string prefix = T(first ? "组件 1：" : "组件 2：", first ? "Widget 1: " : "Widget 2: ");
        var picker = new ComboBox { Name = name, ItemsSource = new[] { prefix + T("保持当前", "Keep current") }.Concat(labels.Select(label => prefix + label)).ToArray(), SelectedIndex = value is null ? 0 : Array.IndexOf(Preferences.Widgets, value) + 1 };
        picker.SelectionChanged += (_, _) => { if (picker.SelectedIndex >= 0) changed(picker.SelectedIndex == 0 ? null : Preferences.Widgets[picker.SelectedIndex - 1]); }; return picker;
    }
    private void SelectWorkspaceScene(string? id)
    {
        var scene = workspaceData.Scenes.FirstOrDefault(item => item.Id == id); sceneId = scene?.Id; sceneName = scene?.Name ?? ""; sceneSlot1 = scene?.Slot1; sceneSlot2 = scene?.Slot2;
        sceneResources.Clear(); if (scene is not null) foreach (string resource in scene.ResourceIds) sceneResources.Add(resource);
    }
    private void SelectWorkspaceResource(string? id)
    {
        var item = workspaceData.Resources.FirstOrDefault(item => item.Id == id); resourceId = item?.Id; resourceName = item?.Name ?? ""; resourceKind = item?.Kind ?? "url"; resourceTarget = item?.Target ?? ""; resourceArguments.Clear(); if (item is not null) resourceArguments.AddRange(item.Arguments);
    }
    private void RebuildWorkspacePage() { if (!closing && page == "tools" && selected == "workspaces") RenderPage(); }
    private async Task LoadWorkspacesAsync(bool recover)
    {
        if (closing || workspaceLoading || workspaceSaving) return;
        workspaceStore ??= new(Path.Combine(store.DirectoryPath, "workspace-scenes.json")); workspaceLoading = true; workspaceSaving = recover;
        if (workspaceLoaded) RebuildWorkspacePage();
        try
        {
            await Task.Run(() => { if (recover) workspaceStore.RecoverEmpty(); else workspaceStore.Load(); });
            if (closing) return;
            workspaceData = workspaceStore.Snapshot; workspaceLoaded = true; workspaceMessage = !recover && workspaceStore.Problem?.Code == WorkspaceProblemCode.Conflict ? "LoadConflict" : workspaceStore.Problem?.Code.ToString() ?? (recover ? "recovered" : ""); workspaceRecoveryPath = recover ? workspaceStore.LastRecoveryPath ?? "" : "";
            SelectWorkspaceScene(workspaceData.Scenes.FirstOrDefault()?.Id); SelectWorkspaceResource(null);
        }
        catch (WorkspaceProblem problem)
        {
            if (!closing)
            {
                workspaceLoaded = true;
                if (problem.DraftCommitted) { workspaceData = workspaceStore.Snapshot; SelectWorkspaceScene(null); SelectWorkspaceResource(null); }
                workspaceMessage = problem.DraftCommitted ? "CommittedConflictWritten" : problem.RequiresReload ? "UnconfirmedSave" : !recover && problem.Code == WorkspaceProblemCode.Conflict ? "LoadConflict" : problem.Code.ToString();
                workspaceRecoveryPath = problem.BackupPath ?? "";
            }
        }
        catch (Exception) { if (!closing) { workspaceLoaded = true; workspaceMessage = "Io"; } }
        finally { CaptureWorkspaceState(); workspaceLoading = false; workspaceSaving = false; ClearWorkspaceCloseMessage(); RebuildWorkspacePage(); }
    }
    private Task SaveWorkspaceResourceAsync()
    {
        string id = resourceId ?? Guid.NewGuid().ToString("N");
        string[] args = resourceKind == "program" ? resourceArguments.ToArray() : [];
        var item = new PortableWorkspaceResource(id, resourceName, resourceKind, resourceTarget, args);
        return PersistWorkspaceAsync(() => PortableWorkspaceStore.UpsertResource(workspaceData, item), () => { resourceId = id; });
    }
    private Task SaveWorkspaceSceneAsync()
    {
        string id = sceneId ?? Guid.NewGuid().ToString("N");
        var scene = new PortableWorkspaceScene(id, sceneName, workspaceData.Resources.Where(item => sceneResources.Contains(item.Id)).Select(item => item.Id).ToArray(), sceneSlot1, sceneSlot2);
        return PersistWorkspaceAsync(() => PortableWorkspaceStore.UpsertScene(workspaceData, scene), () => { sceneId = id; });
    }
    private Task RemoveWorkspaceResourceAsync() => resourceId is string id ? PersistWorkspaceAsync(() => PortableWorkspaceStore.RemoveResource(workspaceData, id), () => { sceneResources.Remove(id); SelectWorkspaceResource(null); }) : Task.CompletedTask;
    private Task RemoveWorkspaceSceneAsync() => sceneId is string id ? PersistWorkspaceAsync(() => PortableWorkspaceStore.RemoveScene(workspaceData, id), () => SelectWorkspaceScene(null)) : Task.CompletedTask;
    private async Task PersistWorkspaceAsync(Func<PortableWorkspaceSnapshot> create, Action apply)
    {
        if (!WorkspaceEditable) return;
        workspaceSaving = true;
        try
        {
            var next = create(); RebuildWorkspacePage();
            if (integrations.SaveWorkspace is not null) await integrations.SaveWorkspace(workspaceStore!, next); else await Task.Run(() => workspaceStore!.Save(next));
            if (closing) return;
            workspaceData = workspaceStore!.Snapshot; apply(); workspaceMessage = "saved"; workspaceRecoveryPath = "";
        }
        catch (WorkspaceProblem problem)
        {
            if (!closing) { if (problem.DraftCommitted) { workspaceData = workspaceStore!.Snapshot; apply(); } workspaceMessage = problem.DraftCommitted ? "CommittedConflictWritten" : problem.RequiresReload ? "UnconfirmedSave" : problem.Code == WorkspaceProblemCode.FileTooLarge ? "SaveTooLarge" : problem.Code.ToString(); workspaceRecoveryPath = problem.BackupPath ?? ""; }
        }
        catch (Exception) { if (!closing) workspaceMessage = "Io"; }
        finally { CaptureWorkspaceState(); workspaceSaving = false; ClearWorkspaceCloseMessage(); RebuildWorkspacePage(); }
    }
    private void CaptureWorkspaceState()
    {
        // Store properties share its filesystem lock. Read them only after the
        // awaited operation ends; rebuilding while IO runs uses these UI snapshots.
        if (workspaceStore is null) return;
        workspaceLoadFailed = workspaceStore.LoadFailed; workspaceNeedsReload = workspaceStore.NeedsReload; workspaceCanRecover = workspaceStore.CanRecover;
    }
    private async Task ChooseWorkspaceTargetAsync(TextBox target)
    {
        if (!WorkspaceEditable || workspaceChoosing || testMode && integrations.ChooseWorkspaceTarget is null) return;
        workspaceChoosing = true; string previous = target.Text ?? "", kind = resourceKind;
        try
        {
            string? path;
            if (integrations.ChooseWorkspaceTarget is not null) path = await integrations.ChooseWorkspaceTarget(kind);
            else if (kind == "folder" || OperatingSystem.IsMacOS())
            {
                var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = T("选择文件夹或应用目录", "Choose folder or application directory"), AllowMultiple = false });
                try { path = folders.FirstOrDefault()?.TryGetLocalPath(); } finally { foreach (var folder in folders) folder.Dispose(); }
            }
            else
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = T("选择可执行文件", "Choose executable"), AllowMultiple = false });
                try { path = files.FirstOrDefault()?.TryGetLocalPath(); } finally { foreach (var file in files) file.Dispose(); }
            }
            if (path is not null && !closing && target.IsAttachedToVisualTree() && resourceKind == kind && target.Text == previous) target.Text = path;
        }
        finally { workspaceChoosing = false; RebuildWorkspacePage(); }
    }
    private bool CanLaunchWorkspace() => WorkspaceEditable && workspaceLaunch is null && sceneId is not null && workspaceData.Scenes.Any(item => item.Id == sceneId);
    private async Task LaunchWorkspaceAsync()
    {
        if (!CanLaunchWorkspace()) return;
        var scene = workspaceData.Scenes.Single(item => item.Id == sceneId);
        var resources = scene.ResourceIds.Select(id => workspaceData.Resources.Single(item => item.Id == id)).ToArray();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); workspaceLaunch = cancellation; workspaceResult = null; workspaceLaunchName = scene.Name; workspaceMessage = ""; workspaceRecoveryPath = ""; UpdateWorkspaceActions(); UpdateWorkspaceFeedback(); RenderWorkspaceResults();
        try
        {
            if (testMode && integrations.LaunchWorkspace is null) { workspaceMessage = "preview"; return; }
            if (scene.Slot1 is not null) prefs.Slot1 = scene.Slot1; if (scene.Slot2 is not null) prefs.Slot2 = scene.Slot2;
            if (prefs.Slot1 == "unread" || prefs.Slot2 == "unread") EnsureNotifications(); RefreshCompact(); Save();
            var result = integrations.LaunchWorkspace is not null ? await integrations.LaunchWorkspace(resources, cancellation.Token) : await new WorkspaceLauncher().RunAsync(resources, cancellation.Token);
            if (!closing) workspaceResult = result;
        }
        catch (OperationCanceledException) { if (!closing) workspaceMessage = "cancelled"; }
        catch (Exception) { if (!closing) workspaceMessage = "start-failed"; }
        finally { workspaceLaunch = null; if (!closing) { UpdateWorkspaceActions(); UpdateWorkspaceFeedback(); RenderWorkspaceResults(); } }
    }
    private void UpdateWorkspaceActions()
    {
        if (workspaceRunButton is not null) workspaceRunButton.IsEnabled = CanLaunchWorkspace();
        if (workspaceCancelButton is not null) workspaceCancelButton.IsEnabled = workspaceLaunch is { IsCancellationRequested: false };
    }
    private void UpdateWorkspaceFeedback()
    {
        if (workspaceFeedback is null || closing) return;
        workspaceFeedback.Text = workspaceLoading ? T("正在读取…", "Loading…") : workspaceSaving ? T("正在保存…", "Saving…") : workspaceMessage switch
        {
            "saved" => T("已保存。", "Saved."), "recovered" => T("原文件已备份，列表已重建。", "Original backed up; lists rebuilt."), "preview" => T("验证模式不会打开资源。", "Test mode does not open resources."), "cancelled" => T("已取消后续启动。", "Remaining launches cancelled."), _ => WorkspaceErrorText(workspaceMessage)
        };
        if (workspaceRecoveryPath.Length > 0) workspaceFeedback.Text += "\n" + workspaceRecoveryPath;
        workspaceFeedback.IsVisible = !string.IsNullOrEmpty(workspaceFeedback.Text);
    }
    private void RenderWorkspaceResults()
    {
        if (workspaceResults is null || closing) return; workspaceResults.Children.Clear();
        if (workspaceLaunch is not null) workspaceResults.Children.Add(Hint(T("正在启动：", "Launching: ") + workspaceLaunchName));
        if (workspaceResult is null) return;
        workspaceResults.Children.Add(Hint(T("启动结果：", "Launch results: ") + workspaceLaunchName));
        foreach (var item in workspaceResult.Items)
        {
            string state = item.Status switch { WorkspaceLaunchStatus.HandedOff => T("已交给系统", "Handed to system"), WorkspaceLaunchStatus.Unconfirmed => T("已发起，未确认接收", "Requested; reception unconfirmed"), WorkspaceLaunchStatus.Skipped => T("已跳过", "Skipped"), _ => T("启动失败", "Failed") };
            workspaceResults.Children.Add(Card(new StackPanel { Spacing = 3, Children = { Text(item.Name, 12), Hint(state + (item.ErrorCode is not null ? " · " + WorkspaceErrorText(item.ErrorCode) : "")) } }));
        }
        workspaceResults.Children.Add(Hint(T("交给系统不代表软件已加载完成，也不代表其中的任务执行完成。", "Hand-off does not mean the app has finished loading or its work is complete.")));
    }
    private bool TryPrepareWorkspaceClose()
    {
        if (!workspaceSaving) return true;
        workspaceClosePending = true; Report(T("工作场景正在保存，请完成后再关闭。", "A workspace is being saved. Close after saving finishes.")); return false;
    }
    private void ClearWorkspaceCloseMessage() { if (workspaceClosePending) { workspaceClosePending = false; Report(""); } }
    private string WorkspaceErrorText(string code) => code switch
    {
        "" => "",
        "InvalidName" => T("名称需要 1 至 60 个字符，不能含控制字符。", "Use a name of 1–60 characters without control characters."),
        "InvalidTarget" => T("请输入完整的 http(s) 网址或文件的绝对路径。", "Enter a complete http(s) URL or absolute file path."),
        "InvalidArguments" => T("最多 32 个启动参数，每个最多 1,024 字符，不能包含空字符。", "Use up to 32 arguments, 1,024 characters each, without NUL characters."),
        "TooManyResources" => T("最多保存 60 个快捷入口。", "Up to 60 shortcuts can be saved."),
        "TooManyScenes" => T("最多保存 20 个场景。", "Up to 20 workspaces can be saved."),
        "TooManySceneResources" => T("每个场景最多选择 30 个入口。", "Select up to 30 shortcuts per workspace."),
        "Conflict" => T("文件已被外部修改，本次未保存。请重新加载后编辑。", "The file changed externally. This save was not applied. Reload before editing."),
        "LoadConflict" => T("读取时文件发生变化，请重新加载。", "The file changed while it was being read. Reload to try again."),
        "CommittedConflict" => T("保存时检测到并发修改；外部版本已备份，请重新加载核对当前内容。", "A concurrent change was detected during saving. The external version was backed up; reload to inspect the current content."),
        "CommittedConflictWritten" => T("本次修改已写入，但后续核验发现问题；请核对当前文件及下方可用的恢复副本。", "Your changes were written, but a later check found a problem. Review the current file and any recovery copies shown below."),
        "UnconfirmedSave" => T("无法确认保存后的当前文件，请重新加载核对；可用的恢复副本已保留。", "The current file after saving could not be confirmed. Reload to inspect it; available recovery copies were retained."),
        "LoadFailed" or "InvalidData" => T("无法读取保存的数据；可重新加载，或在可用时备份并重建。", "Saved data could not be read. Reload, or back up & rebuild when available."),
        "FileTooLarge" => T("场景文件超过 2 MiB；请先在本机恢复或移走，再重新加载。", "The workspace file exceeds 2 MiB. Restore or move it locally, then reload."),
        "SaveTooLarge" => T("保存内容超过 2 MiB，原文件未修改。请减少入口或参数。", "Saved content would exceed 2 MiB. The original is unchanged; reduce shortcuts or arguments."),
        "Busy" => T("另一保存操作或遗留锁文件正在占用，请检查后重试。", "Another save or a leftover lock file is blocking this operation. Check before retrying."),
        "MissingResource" or "DuplicateResource" => T("场景中的快捷入口已变化，请重新选择。", "Workspace shortcuts changed. Select them again."),
        "InvalidId" or "InvalidKind" or "InvalidLayout" or "DuplicateId" or "invalid-resource" => T("条目无效，请检查内容后重试。", "The item is invalid. Check it and try again."),
        "unsupported-platform" => T("当前平台不支持此启动方式。", "This launch method is unavailable on this platform."),
        "target-missing" => T("目标不存在或不可访问。", "The target is missing or inaccessible."),
        "invalid-program" or "not-executable" => T("请选择可执行程序或 Mac 应用目录。", "Choose an executable or Mac application directory."),
        "helper-missing" => T("系统打开工具不可用。", "The system open helper is unavailable."),
        "permission-denied" => T("系统拒绝访问。", "Access was denied."),
        "helper-pending" or "cancelled-after-start" => T("启动请求已发送，未能确认结果。", "The launch request was sent; its result is unconfirmed."),
        "cancelled" => T("未发出启动请求。", "No launch request was sent."),
        "start-failed" or "helper-failed" => T("系统未能打开目标，请检查路径和关联程序。", "The system could not open the target. Check its path and associated app."),
        _ => T("操作失败；原有内容和编辑已保留，请检查文件权限后重试。", "The operation failed. Existing content and edits were retained; check file access and retry.")
    };
}
