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
    private bool batchChecksumMode, batchChecksumBusy;
    private string[] batchChecksumPaths = [];
    private BatchHashResult? batchChecksumResult;
    private CancellationTokenSource? batchChecksumCancellation;
    private Action? refreshBatchChecksums;
    private long batchChecksumBytes;
    private int batchChecksumIndex;
    private Func<string>? batchChecksumFeedback;
    private bool batchChecksumExporting;
    internal Task LatestBatchChecksumOperation { get; private set; } = Task.CompletedTask;

    private void RenderBatchChecksums()
    {
        bool detached = false;
        var content = new StackPanel { Spacing = 6 };
        var paths = new TextBox { Name = "batch-hash-paths", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 48, MaxHeight = 120 };
        var expected = new TextBox { Name = "batch-hash-expected", Text = drafts.GetValueOrDefault("batch-hash-expected", ""), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 60, MaxHeight = 140, MaxLength = 0 };
        var algorithm = new ComboBox { Name = "batch-hash-algorithm", ItemsSource = new[] { "SHA-256", "SHA-512" }, SelectedIndex = drafts.GetValueOrDefault("batch-hash-algorithm") == "SHA512" ? 1 : 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var state = Text("", 12); state.Name = "batch-hash-status";
        var rows = new StackPanel { Name = "batch-hash-results", Spacing = 6 };
        var progress = new ProgressBar { Name = "batch-hash-progress", Height = 4, IsIndeterminate = true };
        Button? choose = null, clear = null, run = null, cancel = null, copy = null, save = null;
        BatchHashResult? rendered = null;
        bool Current() => !detached && !closing;
        bool Manifest(out string text) { text = ""; return batchChecksumResult is not null && BatchHashTools.TryCreateManifest(batchChecksumResult, out text, out _); }
        void Refresh()
        {
            if (!Current()) return;
            paths.Text = string.Join("\n", batchChecksumPaths);
            bool idle = !batchChecksumBusy;
            choose!.IsEnabled = clear!.IsEnabled = algorithm.IsEnabled = expected.IsEnabled = idle;
            run!.IsEnabled = idle && batchChecksumPaths.Length > 0;
            cancel!.IsEnabled = !idle && batchChecksumCancellation?.IsCancellationRequested == false;
            copy!.IsEnabled = save!.IsEnabled = idle && Manifest(out _);
            progress.IsVisible = !idle;
            if (batchChecksumBusy && batchChecksumExporting) state.Text = T("正在导出清单…", "Exporting the list…");
            else if (batchChecksumBusy && batchChecksumCancellation is not null)
                state.Text = batchChecksumCancellation.IsCancellationRequested ? T("正在取消，已完成结果会保留…", "Cancelling; completed results will be retained…") : T($"正在读取 {Math.Min(batchChecksumIndex + 1, batchChecksumPaths.Length)} / {batchChecksumPaths.Length} · {batchChecksumBytes:N0} 字节", $"Reading {Math.Min(batchChecksumIndex + 1, batchChecksumPaths.Length)} / {batchChecksumPaths.Length} · {batchChecksumBytes:N0} bytes");
            else if (batchChecksumFeedback is not null) state.Text = batchChecksumFeedback();
            else if (batchChecksumResult is { } result)
            {
                int completed = result.Items.Count(item => item.Status == BatchHashStatus.Completed);
                state.Text = result.Cancelled ? T($"已取消 · 完成 {completed} / {result.Items.Count}，结果已保留。", $"Cancelled · {completed} / {result.Items.Count} completed; results retained.") : T($"已完成读取 · {completed} / {result.Items.Count} 成功。", $"Reading finished · {completed} / {result.Items.Count} succeeded.");
                if (!Manifest(out _)) state.Text += T(" 完整成功且文件名唯一时可导出清单。", " Export requires every file to succeed and have a unique filename.");
            }
            else state.Text = T($"已选择 {batchChecksumPaths.Length} 个文件（最多 200 个）。", $"{batchChecksumPaths.Length} files selected (up to 200).");
            if (ReferenceEquals(rendered, batchChecksumResult)) return;
            rendered = batchChecksumResult; rows.Children.Clear();
            if (rendered is null) return;
            foreach (var item in rendered.Items)
            {
                var title = Text(item.FileName, 13); title.FontWeight = FontWeight.SemiBold;
                var row = new StackPanel { Spacing = 4, Children = { title, Hint(BatchChecksumItemStatus(item)) } };
                var location = Hint(item.Path); location.FontSize = 11; row.Children.Add(location);
                if (!string.IsNullOrEmpty(item.Digest)) row.Children.Add(new TextBox { IsReadOnly = true, Text = item.Digest, TextWrapping = TextWrapping.Wrap, MinHeight = 36 });
                rows.Children.Add(Card(row));
            }
        }
        void ResetResult() { batchChecksumResult = null; batchChecksumFeedback = null; Refresh(); }
        async Task Pick()
        {
            if (batchChecksumBusy) return;
            batchChecksumBusy = true; Refresh();
            try
            {
                IReadOnlyList<string> selection;
                if (integrations.ChooseBatchChecksumFiles is { } picker) selection = await picker();
                else if (testMode) return;
                else selection = (await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = true, Title = T("选择要校验的文件", "Choose files to verify") })).Select(file => file.TryGetLocalPath()).Where(path => path is not null).Cast<string>().ToArray();
                if (!Current() || selection.Count == 0) return;
                if (selection.Count > 200) { batchChecksumFeedback = () => T("最多选择 200 个文件，请减少选择。", "Choose up to 200 files. Reduce the selection."); return; }
                batchChecksumPaths = selection.ToArray(); batchChecksumResult = null; batchChecksumFeedback = null;
            }
            catch (Exception) { if (Current()) batchChecksumFeedback = () => T("无法选择文件，请重试。", "Could not select files. Try again."); }
            finally { batchChecksumBusy = false; refreshBatchChecksums?.Invoke(); }
        }
        async Task Compute()
        {
            if (batchChecksumBusy || batchChecksumPaths.Length == 0) return;
            string[] selection = (string[])batchChecksumPaths.Clone(); string kind = algorithm.SelectedIndex == 1 ? "SHA512" : "SHA256", comparison = expected.Text ?? "";
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            batchChecksumCancellation = cancellation; batchChecksumBusy = true; batchChecksumResult = null; batchChecksumFeedback = null; batchChecksumBytes = 0; batchChecksumIndex = 0; Refresh();
            var updates = new Progress<BatchHashProgress>(value =>
            {
                if (!ReferenceEquals(batchChecksumCancellation, cancellation) || cancellation.IsCancellationRequested) return;
                batchChecksumBytes = value.TotalBytesRead; batchChecksumIndex = value.Index; refreshBatchChecksums?.Invoke();
            });
            try
            {
                batchChecksumResult = integrations.ComputeBatchChecksums is { } worker ? await worker(selection, kind, comparison, cancellation.Token, updates) : await Task.Run(() => PortableBatchHash.Run(selection, kind, comparison, cancellation.Token, updates));
            }
            catch (OperationCanceledException) { batchChecksumFeedback = () => T("计算已取消。", "Calculation cancelled."); }
            catch (ArgumentException) { batchChecksumFeedback = () => T("输入无效，请检查文件数量、路径、算法和清单。", "Invalid input. Check the file count, paths, algorithm and list."); }
            catch (Exception error)
            {
                batchChecksumFeedback = () => T("无法完成批量校验，请检查文件访问权限。", "Batch checksums could not finish. Check file access.");
                if (!testMode) await Task.Run(() => DiagnosticLog.Write("batch-checksum-failed", error));
            }
            finally { batchChecksumCancellation = null; batchChecksumBusy = false; refreshBatchChecksums?.Invoke(); }
        }
        async Task Export(bool toFile)
        {
            if (batchChecksumBusy || !Manifest(out string manifest)) return;
            batchChecksumBusy = true; batchChecksumExporting = true; batchChecksumFeedback = null;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            batchChecksumCancellation = cancellation; Refresh();
            try
            {
                if (!toFile)
                {
                    if (integrations.WriteChecksumText is { } writer) await writer(manifest);
                    else if (testMode) { batchChecksumFeedback = () => T("演示模式不修改剪贴板。", "Preview mode does not modify the clipboard."); return; }
                    else if (Clipboard is not null) await Clipboard.SetTextAsync(manifest);
                    else throw new InvalidOperationException("Clipboard unavailable");
                    batchChecksumFeedback = () => T("已复制校验清单。", "Checksum list copied.");
                }
                else
                {
                    string name = batchChecksumResult!.Algorithm == "SHA512" ? "SHA512SUMS" : "SHA256SUMS";
                    string? target;
                    if (integrations.ChooseBatchChecksumDestination is { } picker) target = await picker(name);
                    else if (testMode) { batchChecksumFeedback = () => T("演示模式不保存文件。", "Preview mode does not save files."); return; }
                    else target = (await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = T("保存新的校验清单", "Save a new checksum list"), SuggestedFileName = name, ShowOverwritePrompt = false }))?.TryGetLocalPath();
                    if (target is null || !Current() || cancellation.IsCancellationRequested) return;
                    target = Path.GetFullPath(target);
                    if (batchChecksumResult.Items.Any(item => string.Equals(target, Path.GetFullPath(item.Path), OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)))
                    { batchChecksumFeedback = () => T("请选择新的清单路径，不能使用任何源文件路径。", "Choose a new list path, not any of the source file paths."); return; }
                    if (integrations.SaveChecksumManifest is { } saveList) await saveList(target, manifest, cancellation.Token);
                    else await Task.Run(() => FileHashTools.SaveManifestNew(target, manifest, (path, bytes) => PortableFilePublication.WriteNew(path, bytes, cancellation.Token)));
                    batchChecksumFeedback = () => T("校验清单已另存为新文件。", "Checksum list saved as a new file.");
                }
            }
            catch (OperationCanceledException) { batchChecksumFeedback = () => T("导出已取消。", "Export cancelled."); }
            catch (FilePublicationException error)
            {
                batchChecksumFeedback = () => error.State == FilePublicationState.OutcomeUnknown ? FilePublicationFeedback.Unknown(error, prefs.Language) : T("未保存，请选择新文件名并检查权限。", "Not saved. Choose a new filename and check permissions.") + (error.RetainedTemporaryPath is { } retained ? T("\n保留副本：", "\nRetained copy: ") + retained : "");
                if (!testMode) await Task.Run(() => DiagnosticLog.Write("batch-checksum-export", error));
                if (!Current() && !closing && error.State == FilePublicationState.OutcomeUnknown) Notice(T("请核对清单保存结果", "Check the list save result"), batchChecksumFeedback());
            }
            catch (Exception) { batchChecksumFeedback = () => toFile ? T("清单未保存，请选择新文件名并检查权限。", "List not saved. Choose a new filename and check permissions.") : T("剪贴板不可用，请重试。", "Clipboard unavailable. Try again."); }
            finally { batchChecksumCancellation = null; batchChecksumBusy = false; batchChecksumExporting = false; refreshBatchChecksums?.Invoke(); }
        }
        choose = AsyncButton(T("选择文件", "Choose files"), () => LatestBatchChecksumOperation = Pick(), () => Current() && !batchChecksumBusy); choose.Name = "batch-hash-choose";
        clear = Button(T("清除选择", "Clear selection"), () => { if (batchChecksumBusy) return; batchChecksumPaths = []; ResetResult(); }); clear.Name = "batch-hash-clear";
        run = AsyncButton(T("计算 / 校验", "Calculate / verify"), () => LatestBatchChecksumOperation = Compute(), () => Current() && !batchChecksumBusy && batchChecksumPaths.Length > 0); run.Name = "batch-hash-compute";
        cancel = Button(T("取消", "Cancel"), () => { batchChecksumCancellation?.Cancel(); Refresh(); }); cancel.Name = "batch-hash-cancel";
        copy = AsyncButton(T("复制清单", "Copy list"), () => LatestBatchChecksumOperation = Export(false), () => Current() && !batchChecksumBusy && Manifest(out _)); copy.Name = "batch-hash-copy";
        save = AsyncButton(T("保存清单", "Save list"), () => LatestBatchChecksumOperation = Export(true), () => Current() && !batchChecksumBusy && Manifest(out _)); save.Name = "batch-hash-save";
        expected.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty && !batchChecksumBusy) { drafts["batch-hash-expected"] = expected.Text ?? ""; ResetResult(); } };
        algorithm.SelectionChanged += (_, _) => { if (!batchChecksumBusy) { drafts["batch-hash-algorithm"] = algorithm.SelectedIndex == 1 ? "SHA512" : "SHA256"; ResetResult(); } };
        content.Children.Add(Hint(T("顺序读取所选文件；可取消并保留完成结果。", "Read selected files sequentially; cancellation retains completed results.")));
        content.Children.Add(Actions(choose, clear)); content.Children.Add(paths); content.Children.Add(algorithm);
        content.Children.Add(Hint(T("可选：粘贴 SHA256SUMS / SHA512SUMS 或 BSD 清单。按完整文件名匹配，重名时不猜测。", "Optional: paste SHA256SUMS / SHA512SUMS or a BSD list. Match exact filenames; ambiguous names are rejected.")));
        content.Children.Add(expected); content.Children.Add(Actions(run, cancel)); content.Children.Add(progress); content.Children.Add(state); content.Children.Add(Actions(copy, save)); content.Children.Add(rows);
        content.Children.Add(Hint(T("清单只含文件名和校验值；保存为新文件，不覆盖。校验不能替代对文件来源的判断。", "Lists contain filenames and digests only; saves never overwrite. Checksums do not establish a file's trustworthiness.")));
        refreshBatchChecksums = Refresh;
        content.DetachedFromVisualTree += (_, _) => { detached = true; if (refreshBatchChecksums == Refresh) refreshBatchChecksums = null; batchChecksumCancellation?.Cancel(); };
        Refresh(); body.Children.Add(content);
    }

    private string BatchChecksumItemStatus(BatchHashItem item)
    {
        if (item.Status == BatchHashStatus.Cancelled) return T("已取消", "Cancelled");
        if (item.Status == BatchHashStatus.NotStarted) return T("未开始", "Not started");
        if (item.Status == BatchHashStatus.Failed) return item.ErrorCode switch
        {
            "file-missing" => T("找不到文件", "File not found"), "access" => T("无法读取：权限不足", "Cannot read: access denied"),
            "changed" => T("读取期间文件变化，请重试", "File changed while reading. Try again"), "not-regular" => T("不是普通文件", "Not a regular file"),
            _ => T("无法读取文件，请检查路径和权限", "Cannot read file. Check its path and permissions")
        };
        return item.Verification switch
        {
            BatchHashVerification.Matched => T("校验一致", "Checksum matches"),
            BatchHashVerification.Mismatched => T("校验不一致", "Checksum mismatch"),
            BatchHashVerification.InvalidExpected => item.ExpectedError switch
            {
                "manifest-required" => T("多个文件需要带文件名的清单", "Multiple files require a list with filenames"),
                "ambiguous-selection" => T("所选文件重名，无法唯一匹配清单", "Selected filenames are duplicated; list match is ambiguous"),
                _ => HashExpectedError(item.ExpectedError)
            },
            _ => T("已计算", "Calculated")
        };
    }
}
