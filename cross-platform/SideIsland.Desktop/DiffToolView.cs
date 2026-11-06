using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using CodexIsland;
using SideIsland.Core;

namespace SideIsland.Desktop;
public sealed partial class IslandWindow
{
    internal Task LatestDiffExport { get; private set; } = Task.CompletedTask;
    internal Task LatestDiffCopy { get; private set; } = Task.CompletedTask;
    private bool exportingDiff, copyingDiff;
    private CancellationTokenSource? diffExportCancellation;
    private string diffExportState = "", diffExportTarget = "", diffExportBackup = "";
    private Action? refreshDiffTransfer;
    private Control? diffTransferView;
    private PortablePatchTarget? diffOverwriteTarget;
    private TaskCompletionSource<bool>? diffOverwriteDecision;
    private void RenderDiff()
    {
        var left = Input("diff-left", "", true); left.Name = "diff-left";
        var right = Input("diff-right", "", true); right.Name = "diff-right";
        left.MaxLength = right.MaxLength = TextDiff.MaximumCharacters + 1;
        var content = new StackPanel { Spacing = 6 }; var rows = new StackPanel { Spacing = 3 };
        string? patch = null; string capturedLeft = left.Text ?? "", capturedRight = right.Text ?? "";
        long revision = 0; bool detached = false; CancellationTokenSource? pending = null;
        var state = Hint(T("修改文本后重新比较，再复制或保存完整差异。", "Compare after editing, then copy or save the complete diff.")); state.Name = "diff-status";
        async Task CopyPatch()
        {
            if (string.IsNullOrEmpty(patch)) return; string snapshot = patch; long request = revision;
            await CopyDiffAsync(snapshot, () => !detached && revision == request && patch == snapshot);
        }
        var copy = AsyncButton(T("复制差异", "Copy diff"), () => LatestDiffCopy = CopyPatch(), () => !detached && !copyingDiff && !string.IsNullOrEmpty(patch)); copy.Name = "diff-copy";
        async Task SavePatch()
        {
            if (string.IsNullOrEmpty(patch)) return;
            string snapshot = patch; long request = revision;
            await SaveDiffPatchAsync(snapshot, () => !detached && revision == request && patch == snapshot);
        }
        var save = AsyncButton(T("导出补丁", "Export patch"), () => LatestDiffExport = SavePatch(), () => !detached && !exportingDiff && !string.IsNullOrEmpty(patch)); save.Name = "diff-save";
        bool CurrentView() => !closing && !detached && ReferenceEquals(diffTransferView, content);
        var cancel = Button(T("取消导出", "Cancel export"), () => { if (CurrentView()) CancelDiffExport(); }); cancel.Name = "diff-export-cancel";
        var exportFeedback = Hint(""); exportFeedback.Name = "diff-export-feedback";
        var overwritePath = Hint(""); overwritePath.Name = "diff-overwrite-path";
        var overwriteSize = Hint(""); overwriteSize.Name = "diff-overwrite-size";
        var replace = Button(T("替换该文件", "Replace this file"), () => { if (CurrentView() && diffOverwriteTarget is not null && diffExportCancellation?.IsCancellationRequested == false) diffOverwriteDecision?.TrySetResult(true); }); replace.Name = "diff-overwrite-confirm";
        var keep = Button(T("取消", "Cancel"), () => { if (CurrentView()) CancelDiffExport(); }); keep.Name = "diff-overwrite-cancel";
        var overwriteContent = new StackPanel { Spacing = 5 };
        overwriteContent.Children.Add(Text(T("此文件已存在", "This file already exists"), 13)); overwriteContent.Children.Add(overwritePath); overwriteContent.Children.Add(overwriteSize);
        overwriteContent.Children.Add(Hint(T("确认后会替换此版本；若文件随后变化，将拒绝保存或保留被替换的版本供核对。", "Confirm to replace this observed version. A later change is rejected or retained as a copy for review.")));
        overwriteContent.Children.Add(Actions(replace, keep)); var overwrite = Card(overwriteContent); overwrite.Name = "diff-overwrite-card";
        void UpdateActions()
        {
            copy.IsEnabled = !detached && !closing && !copyingDiff && !string.IsNullOrEmpty(patch);
            save.IsEnabled = !detached && !closing && !exportingDiff && !string.IsNullOrEmpty(patch);
            cancel.IsVisible = exportingDiff && diffOverwriteTarget is null; cancel.IsEnabled = exportingDiff && diffExportCancellation?.IsCancellationRequested == false;
            overwrite.IsVisible = !detached && diffOverwriteTarget is not null;
            replace.IsEnabled = keep.IsEnabled = !detached && diffOverwriteTarget is not null && diffExportCancellation?.IsCancellationRequested == false;
            overwritePath.Text = diffOverwriteTarget?.TargetPath ?? "";
            overwriteSize.Text = diffOverwriteTarget is { } target ? T($"当前文件 {target.ExistingLength:N0} 字节", $"Current file: {target.ExistingLength:N0} bytes") : "";
            exportFeedback.Text = DiffExportFeedback(); exportFeedback.IsVisible = exportFeedback.Text.Length > 0;
        }
        void Invalidate()
        {
            if (capturedLeft == (left.Text ?? "") && capturedRight == (right.Text ?? "")) return;
            capturedLeft = left.Text ?? ""; capturedRight = right.Text ?? ""; revision++; pending?.Cancel(); patch = null; rows.Children.Clear(); UpdateActions();
            InvalidateDiffExport();
            state.Text = T("输入已更新，请重新比较。", "Inputs changed. Compare again.");
        }
        var compare = AsyncButton(T("比较差异", "Compare"), async () =>
        {
            pending?.Cancel(); InvalidateDiffExport(); using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); pending = cancellation;
            long request = ++revision; string a = left.Text ?? "", b = right.Text ?? ""; capturedLeft = a; capturedRight = b;
            patch = null; rows.Children.Clear(); UpdateActions(); state.Text = T("正在对照…", "Comparing…");
            bool Current() => !closing && !detached && revision == request;
            try
            {
                var output = await Task.Run(() => (Diff: TextDiff.Compare(a, b, cancellation.Token), Patch: TextDiff.ExportUnified(a, b, cancellation.Token)), cancellation.Token);
                if (!Current()) return; cancellation.Token.ThrowIfCancellationRequested();
                patch = output.Patch; UpdateActions(); var result = output.Diff;
                state.Text = result.Identical ? T("两侧文本一致。", "Both texts are identical.") : T($"新增 {result.Added} · 删除 {result.Removed} · 修改 {result.Modified}", $"Added {result.Added} · Removed {result.Removed} · Changed {result.Modified}");
                foreach (var line in result.Rows.Take(1600))
                {
                    var label = Text((line.LeftNumber?.ToString() ?? "+") + " │ " + line.Left + (line.Kind == TextDiffKind.Modified ? "\n→ " + line.Right : line.Kind == TextDiffKind.Added ? line.Right : ""), 12);
                    var card = Card(label); card.Background = new SolidColorBrush(Color.Parse(line.Kind == TextDiffKind.Added ? prefs.LightTheme ? "#E5F4E9" : "#253D32" : line.Kind == TextDiffKind.Removed ? prefs.LightTheme ? "#FCE9EA" : "#472A2E" : line.Kind == TextDiffKind.Modified ? prefs.LightTheme ? "#EBE8FA" : "#3B354D" : prefs.LightTheme ? "#F2F2F7" : "#232326")); rows.Children.Add(card);
                }
                if (result.Rows.Count > 1600) rows.Children.Add(Hint(T("页面仅显示前 1,600 项，补丁包含完整差异。", "The preview shows 1,600 rows; the patch includes every change.")));
            }
            catch (OperationCanceledException) { }
            catch (Exception) { if (Current()) state.Text = T("无法比较：每侧最多 120,000 字符、1,600 行。", "Could not compare: each side supports 120,000 characters and 1,600 lines."); }
            finally { if (ReferenceEquals(pending, cancellation)) pending = null; }
        }); compare.Name = "diff-compare";
        left.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) Invalidate(); };
        right.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) Invalidate(); };
        content.Children.Add(Hint(T("原文", "Original"))); content.Children.Add(left); content.Children.Add(Hint(T("新文", "Revised"))); content.Children.Add(right);
        content.Children.Add(compare); content.Children.Add(Actions(copy, save)); content.Children.Add(cancel); content.Children.Add(overwrite); content.Children.Add(exportFeedback); content.Children.Add(state);
        content.Children.Add(Hint(T("UTF-8 补丁目标为 text.txt，换行统一为 LF；不修改原文。已有目标文件会在这里再次核对后替换。", "UTF-8 patch for text.txt with LF line endings. Original text stays unchanged. Existing destination files are replaced only after confirmation here.")));
        content.Children.Add(rows); UpdateActions();
        diffTransferView = content; refreshDiffTransfer = UpdateActions;
        content.DetachedFromVisualTree += (_, _) =>
        {
            detached = true; revision++; pending?.Cancel(); patch = null; InvalidateDiffExport(); UpdateActions();
            if (ReferenceEquals(diffTransferView, content)) { diffTransferView = null; refreshDiffTransfer = null; }
        };
        body.Children.Add(content);
    }

    private void InvalidateDiffExport()
    {
        if (exportingDiff) { diffExportState = "invalidated"; diffExportCancellation?.Cancel(); diffOverwriteDecision?.TrySetResult(false); }
        else diffExportState = "";
        diffOverwriteTarget = null; refreshDiffTransfer?.Invoke();
    }
    private void CancelDiffExport()
    {
        if (!exportingDiff) return;
        diffExportState = "cancelled"; diffExportCancellation?.Cancel(); diffOverwriteDecision?.TrySetResult(false); diffOverwriteTarget = null;
        refreshDiffTransfer?.Invoke();
    }
    private string DiffExportFeedback()
    {
        string text = diffExportState switch
        {
            "choosing" => T("选择补丁保存位置…", "Choose where to save the patch…"),
            "preparing" => T("正在核对目标文件…", "Checking the destination file…"),
            "confirming" => T("请核对目标，确认后才会替换。", "Review the destination before confirming replacement."),
            "saving" => T("正在保存比较时的补丁…", "Saving the compared patch…"),
            "saved" => T("差异补丁已保存。", "Diff patch saved."),
            "saved-earlier" => T("先前比较的补丁已保存，不包含后续修改。", "The earlier compared patch was saved, without later edits."),
            "saved-warning" => T("补丁已保存，请核对当前文件与保留副本。", "Patch saved; review the destination and retained copy."),
            "saved-earlier-warning" => T("先前比较的补丁已保存，请核对当前文件与保留副本；不包含后续修改。", "The earlier patch was saved without later edits; review the destination and retained copy."),
            "invalidated" => T("输入或页面已变化，尚未提交的导出已取消。", "Inputs or page changed; any uncommitted export was cancelled."),
            "cancelled" => T("已请求取消尚未提交的导出。", "Cancellation requested for the uncommitted export."),
            "local" => T("请选择本机文件位置。", "Choose a local file location."),
            "failed" => T("补丁未保存：目标已变化、无法写入或不支持安全保存，请重新选择。", "Patch not saved: the destination changed, is unwritable or does not support safe saving. Choose again."),
            "not-saved-copy" => T("补丁未保存，请核对保留副本。", "Patch not saved; review the retained copy."),
            "unknown" => T("无法确认补丁保存结果，请先核对目标文件和保留副本再重试。", "The patch save result could not be confirmed. Check the destination and retained copy before retrying."),
            _ => ""
        };
        if (diffExportState is "saved" or "saved-earlier" or "saved-warning" or "saved-earlier-warning" or "unknown" or "not-saved-copy") text += "\n" + diffExportTarget;
        if (diffExportBackup.Length > 0 && diffExportState is "saved-warning" or "saved-earlier-warning" or "unknown" or "failed" or "not-saved-copy") text += T("\n保留副本：", "\nRetained copy: ") + diffExportBackup;
        return text;
    }
    private async Task SaveDiffPatchAsync(string snapshot, Func<bool> sameSnapshot)
    {
        if (closing || exportingDiff || testMode && integrations.ChooseDiffDestination is null) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        exportingDiff = true; diffExportCancellation = cancellation; diffExportState = "choosing"; diffExportTarget = diffExportBackup = "";
        refreshDiffTransfer?.Invoke();
        bool publicationStarted = false;
        bool Current() => !closing && !cancellation.IsCancellationRequested && sameSnapshot();
        try
        {
            string? destination;
            if (integrations.ChooseDiffDestination is { } choose) destination = await choose("comparison.patch");
            else
            {
                // Existing destinations are confirmed once in our compact
                // panel, after observing the exact version that Save protects.
                using var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = T("导出差异补丁", "Export diff patch"), SuggestedFileName = "comparison.patch", DefaultExtension = "patch", ShowOverwritePrompt = false,
                    FileTypeChoices = [new("Patch") { Patterns = ["*.patch", "*.diff"] }]
                });
                destination = file?.TryGetLocalPath();
                if (file is not null && destination is null && Current()) diffExportState = "local";
            }
            if (!Current()) return;
            if (destination is null) { if (diffExportState != "local") diffExportState = ""; return; }
            string path = Path.GetFullPath(destination); diffExportTarget = path; diffExportState = "preparing"; refreshDiffTransfer?.Invoke();
            PortablePatchTarget target = await Task.Run(async () => integrations.PrepareDiffPatch is { } prepare
                ? await prepare(path, snapshot, cancellation.Token).ConfigureAwait(false)
                : PortablePatchFiles.Prepare(path, snapshot, cancellation.Token), cancellation.Token);
            if (!Current()) return;
            if (target.Exists)
            {
                diffOverwriteTarget = target; var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); diffOverwriteDecision = decision;
                diffExportState = "confirming"; refreshDiffTransfer?.Invoke();
                bool replace;
                try { replace = await decision.Task.WaitAsync(cancellation.Token); }
                finally { if (ReferenceEquals(diffOverwriteDecision, decision)) { diffOverwriteDecision = null; diffOverwriteTarget = null; } }
                if (!replace || !Current()) return;
            }
            diffExportState = "saving"; refreshDiffTransfer?.Invoke();
            publicationStarted = true;
            await Task.Run(async () =>
            {
                if (integrations.SaveDiffPatch is { } save) await save(target, cancellation.Token).ConfigureAwait(false);
                else target.Save(cancellation.Token);
            }, cancellation.Token);
            // A successful commit cannot be undone or called cancelled merely
            // because a later edit or close happened during its final checks.
            if (!closing) diffExportState = Current() ? "saved" : "saved-earlier";
        }
        catch (StateFileException error)
        {
            bool committed = publicationStarted && error.DraftCommitted, unknown = publicationStarted && error.RequiresReload;
            if (!testMode) await Task.Run(() => DiagnosticLog.Write(committed ? "diff-export-committed-warning" : unknown ? "diff-export-outcome-unknown" : "diff-export-failed", error));
            if (!closing)
            {
                diffExportTarget = error.TargetPath; diffExportBackup = error.BackupPath ?? "";
                diffExportState = committed ? Current() ? "saved-warning" : "saved-earlier-warning" : unknown ? "unknown" : Current() ? "failed" : diffExportBackup.Length > 0 ? "not-saved-copy" : diffExportState;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!testMode) await Task.Run(() => DiagnosticLog.Write("diff-export-failed", error));
            if (Current()) diffExportState = "failed";
        }
        finally
        {
            if (ReferenceEquals(diffExportCancellation, cancellation)) diffExportCancellation = null;
            diffOverwriteTarget = null; diffOverwriteDecision = null; exportingDiff = false;
            if (!closing) refreshDiffTransfer?.Invoke();
        }
    }
    private async Task CopyDiffAsync(string? patch, Func<bool> sameSnapshot)
    {
        if (closing || copyingDiff || string.IsNullOrEmpty(patch)) return;
        copyingDiff = true; refreshDiffTransfer?.Invoke();
        try
        {
            if (integrations.WriteDiffText is { } write) await write(patch);
            else if (!testMode && Clipboard is not null) await Clipboard.SetTextAsync(patch);
        }
        catch (Exception error)
        {
            if (!testMode) await Task.Run(() => DiagnosticLog.Write("diff-copy-failed", error));
            if (!closing && sameSnapshot()) Report(T("差异复制失败，请重试。", "Could not copy the diff. Try again."));
        }
        finally { copyingDiff = false; if (!closing) refreshDiffTransfer?.Invoke(); }
    }
}
