using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using CodexIsland;
using SideIsland.Core;

namespace SideIsland.Desktop;

public sealed partial class IslandWindow
{
    internal Task LatestChecksumExport { get; private set; } = Task.CompletedTask;
    private void RenderHash()
    {
        var singleMode = Button(T("单个文件", "Single file"), () => { batchChecksumMode = false; RenderPage(); }); singleMode.Name = "hash-single-mode"; singleMode.IsEnabled = batchChecksumMode;
        var batchMode = Button(T("批量校验", "Batch checksums"), () => { batchChecksumMode = true; RenderPage(); }); batchMode.Name = "hash-batch-mode"; batchMode.IsEnabled = !batchChecksumMode;
        body.Children.Add(Actions(singleMode, batchMode));
        if (batchChecksumMode) { RenderBatchChecksums(); return; }
        var path = Input("hash-path"); path.Name = "hash-path";
        // Do not truncate a pasted manifest: a later conflicting entry must
        // reach the bounded parser and fail explicitly rather than be hidden.
        var expected = new TextBox { Name = "hash-expected", Text = drafts.GetValueOrDefault("hash-expected", ""), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 82, MaxHeight = 180, MaxLength = 0 };
        expected.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) drafts["hash-expected"] = expected.Text ?? ""; };
        var algorithm = new ComboBox { Name = "hash-algorithm", ItemsSource = new[] { "SHA-256", "SHA-512" }, SelectedIndex = drafts.GetValueOrDefault("hash-algorithm") == "SHA512" ? 1 : 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var result = new TextBox { Name = "hash-result", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 80 };
        var state = Text(T("选择文件后开始计算。", "Choose a file to begin."), 12); state.Name = "hash-status";
        var progress = new ProgressBar { Name = "hash-progress", Minimum = 0, Maximum = 100, Height = 4 };
        var exportNote = Hint(T("此文件名无法导出为 UTF-8 清单，仍可复制校验值。", "This filename cannot be exported as a UTF-8 list. The digest can still be copied.")); exportNote.Name = "hash-export-note"; exportNote.IsVisible = false;
        var content = new StackPanel { Spacing = 6 };
        CancellationTokenSource? pending = null, pendingSave = null; long revision = 0; bool detached = false, exporting = false;
        ChecksumExport? completed = null; Button? copy = null, copyManifest = null, saveManifest = null;
        bool CanExport() => !closing && !detached && !exporting && pending is null && completed is not null;
        bool CanExportManifest() => CanExport() && completed!.Manifest is not null;
        void UpdateExportActions()
        {
            bool available = CanExport();
            if (copy is not null) copy.IsEnabled = available;
            if (copyManifest is not null) copyManifest.IsEnabled = CanExportManifest();
            if (saveManifest is not null) saveManifest.IsEnabled = CanExportManifest();
            exportNote.IsVisible = completed is not null && completed.Manifest is null;
        }
        string lastTarget = path.Text ?? "", lastExpected = expected.Text ?? ""; int lastAlgorithm = algorithm.SelectedIndex;
        Button cancel = Button(T("取消", "Cancel"), () => pending?.Cancel()); cancel.Name = "hash-cancel"; cancel.IsEnabled = false;
        void Invalidate()
        {
            // Only changes from the captured inputs invalidate current work.
            if (lastTarget == (path.Text ?? "") && lastExpected == (expected.Text ?? "") && lastAlgorithm == algorithm.SelectedIndex) return;
            lastTarget = path.Text ?? ""; lastExpected = expected.Text ?? ""; lastAlgorithm = algorithm.SelectedIndex;
            revision++; pending?.Cancel(); pendingSave?.Cancel(); result.Text = ""; completed = null; progress.Value = 0; UpdateExportActions();
            state.Text = T("输入已更新，请重新计算。", "Inputs changed. Calculate again."); state.Foreground = TextBrush;
        }
        void SetFailure(string message) { state.Text = message; state.Foreground = new SolidColorBrush(Color.Parse(prefs.LightTheme ? "#9A5B00" : "#FF9F0A")); }
        async Task Calculate()
        {
            long request = ++revision; pendingSave?.Cancel(); result.Text = ""; completed = null; progress.Value = 0; state.Foreground = TextBrush; UpdateExportActions();
            string target = path.Text ?? "", kind = algorithm.SelectedIndex == 1 ? "SHA512" : "SHA256", comparison = expected.Text ?? "";
            lastTarget = target; lastExpected = comparison; lastAlgorithm = algorithm.SelectedIndex;
            if (string.IsNullOrWhiteSpace(target)) { SetFailure(T("请先选择文件。", "Choose a file first.")); return; }
            string? expectedDigest = null;
            if (!string.IsNullOrWhiteSpace(comparison) && !FileHashTools.TryResolveExpected(comparison, kind, target, out expectedDigest, out string parseError))
            { SetFailure(HashExpectedError(parseError)); return; }
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            pending = cancellation; cancel.IsEnabled = true; state.Text = T("正在计算…", "Calculating…");
            bool Current() => !closing && !detached && revision == request;
            var updates = new Progress<int>(percent => { if (Current() && ReferenceEquals(pending, cancellation) && !cancellation.IsCancellationRequested) progress.Value = percent; });
            try
            {
                var output = await Task.Run(() =>
                {
                    string fullPath = Path.GetFullPath(target);
                    string digest = FileHashTools.ComputeFile(fullPath, kind, cancellation.Token, updates);
                    string? manifest = null;
                    try { manifest = FileHashTools.CreateManifest(digest, kind, fullPath); }
                    catch (ArgumentException) { /* Some native filenames cannot be represented faithfully in UTF-8. Hashing remains valid. */ }
                    return new ChecksumExport(fullPath, kind, digest, manifest);
                }, cancellation.Token);
                if (!Current()) return;
                cancellation.Token.ThrowIfCancellationRequested();
                completed = output; result.Text = output.Digest; progress.Value = 100;
                if (expectedDigest is null) state.Text = T("计算完成。可复制校验值或导出校验清单。", "Calculated. Copy the digest or export a checksum list.");
                else if (output.Digest == expectedDigest) { state.Text = T("校验一致", "Checksum matches"); state.Foreground = new SolidColorBrush(Color.Parse(prefs.LightTheme ? "#16783D" : "#30B75C")); }
                else SetFailure(T("校验不一致，请核对下载文件和校验清单。", "Checksum mismatch. Check the downloaded file and checksum list."));
            }
            catch (OperationCanceledException) { if (Current()) { state.Text = T("已取消。", "Cancelled."); progress.Value = 0; } }
            catch (Exception error)
            {
                if (Current())
                {
                    if (!testMode) DiagnosticLog.Write("hash-computation-failed", error);
                    SetFailure(error switch
                    {
                        FileNotFoundException or DirectoryNotFoundException => T("找不到所选文件，请检查路径。", "The selected file was not found. Check its path."),
                        UnauthorizedAccessException => T("无法读取此文件，请检查访问权限。", "This file cannot be read. Check access permissions."),
                        IOException => T("无法完成读取，文件可能被占用或已发生变化。", "Reading could not finish. The file may be in use or may have changed."),
                        _ => T("无法计算校验值，请检查文件路径和访问权限。", "Could not calculate a checksum. Check the file path and access permissions.")
                    });
                    progress.Value = 0;
                }
            }
            finally { if (ReferenceEquals(pending, cancellation)) pending = null; if (!detached) { cancel.IsEnabled = false; UpdateExportActions(); } }
        }
        var compute = AsyncButton(T("计算 / 校验", "Calculate / verify"), Calculate); compute.Name = "hash-compute";
        var choose = AsyncButton(T("选择文件", "Choose file"), async () =>
        {
            if (testMode) return;
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false, Title = T("选择校验文件", "Choose a file") });
            if (!closing && !detached && files.Count > 0) path.Text = files[0].TryGetLocalPath() ?? "";
        });
        async Task CopyExport(bool manifest)
        {
            if (!(manifest ? CanExportManifest() : CanExport())) return; var snapshot = completed!; long request = revision; exporting = true; UpdateExportActions();
            bool Current() => !closing && !detached && revision == request && ReferenceEquals(completed, snapshot);
            try
            {
                string text = manifest ? snapshot.Manifest! : snapshot.Digest;
                if (integrations.WriteChecksumText is not null) await integrations.WriteChecksumText(text);
                else if (testMode) { if (Current()) state.Text = T("演示模式不修改剪贴板。", "Preview mode does not modify the clipboard."); return; }
                else if (Clipboard is not null) await Clipboard.SetTextAsync(text);
                else throw new InvalidOperationException("Clipboard unavailable");
                if (Current()) state.Text = manifest ? T("已复制校验清单。", "Checksum list copied.") : T("已复制校验值。", "Checksum copied.");
            }
            catch (Exception) { if (Current()) SetFailure(T("剪贴板正忙，请重试。", "Clipboard unavailable. Try again.")); }
            finally { exporting = false; if (!closing && !detached) UpdateExportActions(); }
        }
        copy = AsyncButton(T("复制校验值", "Copy digest"), () => LatestChecksumExport = CopyExport(false), CanExport); copy.Name = "hash-copy";
        copyManifest = AsyncButton(T("复制清单", "Copy list"), () => LatestChecksumExport = CopyExport(true), CanExportManifest); copyManifest.Name = "hash-copy-manifest";
        async Task SaveExport()
        {
            if (!CanExportManifest()) return; var snapshot = completed!; long request = revision; exporting = true; UpdateExportActions();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); pendingSave = cancellation;
            bool Current() => !closing && !detached && revision == request && ReferenceEquals(completed, snapshot);
            try
            {
                string suggested = Path.GetFileName(snapshot.SourcePath) + (snapshot.Algorithm == "SHA512" ? ".sha512" : ".sha256");
                string? destination;
                if (integrations.ChooseChecksumDestination is not null) destination = await integrations.ChooseChecksumDestination(suggested);
                else if (testMode) { if (Current()) state.Text = T("演示模式不保存文件。", "Preview mode does not save files."); return; }
                else
                {
                    var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                    {
                        Title = T("保存为新的校验清单", "Save a new checksum list"), SuggestedFileName = suggested, ShowOverwritePrompt = false,
                        DefaultExtension = snapshot.Algorithm == "SHA512" ? "sha512" : "sha256",
                        FileTypeChoices = [new("Checksum list") { Patterns = ["*.sha256", "*.sha512", "*.txt"] }]
                    });
                    destination = file?.TryGetLocalPath();
                }
                // A picker can remain open while the inputs or page change.
                if (destination is null || !Current()) return;
                string fullPath = Path.GetFullPath(destination);
                if (string.Equals(fullPath, snapshot.SourcePath, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
                { SetFailure(T("请选择新的清单文件名，不能使用原文件路径。", "Choose a new list filename, not the original file path.")); return; }
                if (integrations.SaveChecksumManifest is { } writer) await writer(fullPath, snapshot.Manifest!, cancellation.Token);
                else await Task.Run(() => FileHashTools.SaveManifestNew(fullPath, snapshot.Manifest!, (target, bytes) => PortableFilePublication.WriteNew(target, bytes, cancellation.Token)), cancellation.Token);
                if (Current()) state.Text = T("校验清单已另存为新文件。", "Checksum list saved as a new file.");
            }
            catch (OperationCanceledException) { }
            catch (FilePublicationException error) when (error.State == FilePublicationState.OutcomeUnknown)
            {
                if (!testMode) DiagnosticLog.Write("checksum-export-outcome-unknown", error);
                if (Current()) SetFailure(FilePublicationFeedback.Unknown(error, prefs.Language));
                else if (!closing) Notice(T("校验清单保存结果需核对", "Check the checksum save result"), FilePublicationFeedback.Unknown(error, prefs.Language));
            }
            catch (IOException) { if (Current()) SetFailure(T("未保存：目标已存在、不可写或不支持安全保存。请选择新文件名或其他位置。", "Not saved: the destination exists, is unwritable or does not support safe saving. Choose a new name or location.")); }
            catch (Exception error)
            {
                if (Current()) { if (!testMode) DiagnosticLog.Write("checksum-export-failed", error); SetFailure(T("无法保存清单，请检查位置和权限。", "Could not save the list. Check its location and permissions.")); }
            }
            finally { if (ReferenceEquals(pendingSave, cancellation)) pendingSave = null; exporting = false; if (!closing && !detached) UpdateExportActions(); }
        }
        saveManifest = AsyncButton(T("保存清单", "Save list"), () => LatestChecksumExport = SaveExport(), CanExportManifest); saveManifest.Name = "hash-save-manifest";
        path.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) Invalidate(); };
        expected.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) Invalidate(); };
        algorithm.SelectionChanged += (_, _) => { drafts["hash-algorithm"] = algorithm.SelectedIndex == 1 ? "SHA512" : "SHA256"; Invalidate(); };
        content.Children.Add(path); content.Children.Add(Actions(choose, algorithm));
        content.Children.Add(Hint(T("可选：粘贴预期值、SHA256SUMS / SHA512SUMS 或 BSD 校验清单。", "Optional: paste an expected digest, SHA256SUMS / SHA512SUMS, or a BSD checksum list.")));
        content.Children.Add(expected); content.Children.Add(Actions(compute, cancel)); content.Children.Add(progress); content.Children.Add(state); content.Children.Add(result); content.Children.Add(Actions(copy, copyManifest, saveManifest));
        content.Children.Add(exportNote);
        content.Children.Add(Hint(T("导出只包含文件名和校验值，保存不会覆盖已有文件。清单按文件名匹配，重名冲突时不会猜测。", "Exports contain only the filename and digest, and never overwrite an existing file. Ambiguous filename matches are rejected.")));
        content.DetachedFromVisualTree += (_, _) => { detached = true; revision++; pending?.Cancel(); pendingSave?.Cancel(); completed = null; UpdateExportActions(); };
        UpdateExportActions();
        body.Children.Add(content);
    }

    private sealed record ChecksumExport(string SourcePath, string Algorithm, string Digest, string? Manifest);

    private string HashExpectedError(string error) => error switch
    {
        "too-large" => T("校验清单过大（最多 128 Ki 字符、1024 行）。", "Checksum list is too large (128 Ki characters / 1024 lines maximum)."),
        "file-required" => T("请先选择要校验的文件。", "Select the file to verify first."),
        "file-missing" => T("清单中没有所选文件的校验值。", "The list has no checksum for the selected filename."),
        "ambiguous" => T("清单中存在同名冲突，请使用唯一条目。", "The list has conflicting filename entries. Use an unambiguous entry."),
        "manifest" => T("清单格式不正确，或算法与清单不一致。", "Invalid checksum list, or the list uses a different algorithm."),
        _ => T("预期校验值无效，请核对所选算法。", "Invalid expected checksum. Check the selected algorithm.")
    };
}
