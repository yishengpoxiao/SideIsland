using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using CodexIsland;

namespace SideIsland.Desktop;

public sealed partial class IslandWindow
{
    private void RenderHash()
    {
        var path = Input("hash-path"); path.Name = "hash-path";
        // Do not truncate a pasted manifest: a later conflicting entry must
        // reach the bounded parser and fail explicitly rather than be hidden.
        var expected = new TextBox { Name = "hash-expected", Text = drafts.GetValueOrDefault("hash-expected", ""), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 82, MaxHeight = 180, MaxLength = 0 };
        expected.TextChanged += (_, _) => drafts["hash-expected"] = expected.Text ?? "";
        var algorithm = new ComboBox { Name = "hash-algorithm", ItemsSource = new[] { "SHA-256", "SHA-512" }, SelectedIndex = drafts.GetValueOrDefault("hash-algorithm") == "SHA512" ? 1 : 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var result = new TextBox { Name = "hash-result", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 80 };
        var state = Text(T("选择文件后开始计算。", "Choose a file to begin."), 12); state.Name = "hash-status";
        var progress = new ProgressBar { Name = "hash-progress", Minimum = 0, Maximum = 100, Height = 4 };
        var content = new StackPanel { Spacing = 6 };
        CancellationTokenSource? pending = null; long revision = 0; bool detached = false;
        string lastTarget = path.Text ?? "", lastExpected = expected.Text ?? ""; int lastAlgorithm = algorithm.SelectedIndex;
        Button cancel = Button(T("取消", "Cancel"), () => pending?.Cancel()); cancel.Name = "hash-cancel"; cancel.IsEnabled = false;
        void Invalidate()
        {
            // Avalonia can deliver TextChanged after a click in the same UI
            // turn. Only a change from the captured inputs invalidates work.
            if (lastTarget == (path.Text ?? "") && lastExpected == (expected.Text ?? "") && lastAlgorithm == algorithm.SelectedIndex) return;
            lastTarget = path.Text ?? ""; lastExpected = expected.Text ?? ""; lastAlgorithm = algorithm.SelectedIndex;
            revision++; pending?.Cancel(); result.Text = ""; progress.Value = 0;
            state.Text = T("输入已更新，请重新计算。", "Inputs changed. Calculate again."); state.Foreground = TextBrush;
        }
        void SetFailure(string message) { state.Text = message; state.Foreground = new SolidColorBrush(Color.Parse(prefs.LightTheme ? "#9A5B00" : "#FF9F0A")); }
        async Task Calculate()
        {
            long request = ++revision; result.Text = ""; progress.Value = 0; state.Foreground = TextBrush;
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
                string digest = await Task.Run(() => FileHashTools.ComputeFile(target, kind, cancellation.Token, updates), cancellation.Token);
                if (!Current()) return;
                cancellation.Token.ThrowIfCancellationRequested();
                result.Text = digest; progress.Value = 100;
                if (expectedDigest is null) state.Text = T("计算完成。可复制结果或粘贴校验清单后重算。", "Calculated. Copy the result, or paste a checksum list and calculate again.");
                else if (digest == expectedDigest) { state.Text = T("校验一致", "Checksum matches"); state.Foreground = new SolidColorBrush(Color.Parse(prefs.LightTheme ? "#16783D" : "#30B75C")); }
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
            finally { if (ReferenceEquals(pending, cancellation)) pending = null; if (!detached) cancel.IsEnabled = false; }
        }
        var compute = AsyncButton(T("计算 / 校验", "Calculate / verify"), Calculate); compute.Name = "hash-compute";
        var choose = AsyncButton(T("选择文件", "Choose file"), async () =>
        {
            if (testMode) return;
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false, Title = T("选择校验文件", "Choose a file") });
            if (!closing && !detached && files.Count > 0) path.Text = files[0].TryGetLocalPath() ?? "";
        });
        var copy = AsyncButton(T("复制结果", "Copy result"), async () => { if (Clipboard is not null && !testMode && !string.IsNullOrEmpty(result.Text)) await Clipboard.SetTextAsync(result.Text); }); copy.Name = "hash-copy";
        path.TextChanged += (_, _) => Invalidate(); expected.TextChanged += (_, _) => Invalidate();
        algorithm.SelectionChanged += (_, _) => { drafts["hash-algorithm"] = algorithm.SelectedIndex == 1 ? "SHA512" : "SHA256"; Invalidate(); };
        content.Children.Add(path); content.Children.Add(Actions(choose, algorithm));
        content.Children.Add(Hint(T("可选：粘贴预期值、SHA256SUMS / SHA512SUMS 或 BSD 校验清单。", "Optional: paste an expected digest, SHA256SUMS / SHA512SUMS, or a BSD checksum list.")));
        content.Children.Add(expected); content.Children.Add(Actions(compute, cancel)); content.Children.Add(progress); content.Children.Add(state); content.Children.Add(result); content.Children.Add(copy);
        content.Children.Add(Hint(T("只读取所选文件；清单按文件名匹配，发现重名冲突时不会猜测。", "Only the selected file is read. Lists match its filename; ambiguous entries are rejected.")));
        content.DetachedFromVisualTree += (_, _) => { detached = true; revision++; pending?.Cancel(); };
        body.Children.Add(content);
    }

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
