using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using CodexIsland;

namespace SideIsland.Desktop;
public sealed partial class IslandWindow
{
    private void RenderDiff()
    {
        var left = Input("diff-left", "", true); left.Name = "diff-left";
        var right = Input("diff-right", "", true); right.Name = "diff-right";
        left.MaxLength = right.MaxLength = TextDiff.MaximumCharacters + 1;
        var content = new StackPanel { Spacing = 6 }; var rows = new StackPanel { Spacing = 3 };
        string? patch = null; string capturedLeft = left.Text ?? "", capturedRight = right.Text ?? "";
        long revision = 0; bool detached = false; CancellationTokenSource? pending = null;
        var state = Hint(T("修改文本后重新比较，再复制或保存完整差异。", "Compare after editing, then copy or save the complete diff.")); state.Name = "diff-status";
        var copy = AsyncButton(T("复制差异", "Copy diff"), async () => { if (patch is not null && Clipboard is not null && !testMode) await Clipboard.SetTextAsync(patch); }, () => !detached && !string.IsNullOrEmpty(patch)); copy.Name = "diff-copy";
        var save = AsyncButton(T("导出补丁", "Export patch"), async () =>
        {
            if (string.IsNullOrEmpty(patch) || testMode) return; string snapshot = patch;
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = T("导出差异补丁", "Export diff patch"), SuggestedFileName = "comparison.patch", DefaultExtension = "patch", FileTypeChoices = [new("Patch") { Patterns = ["*.patch", "*.diff"] }] });
            if (file is null || detached || closing) return;
            var path = file.TryGetLocalPath();
            if (path is null) { state.Text = T("请选择本地文件位置。", "Choose a local file location."); return; }
            try { TextDiff.SaveUnified(path, snapshot); state.Text = T("差异补丁已保存。", "Diff patch saved."); }
            catch { state.Text = T("补丁保存失败，请检查权限或选择其他位置。", "Could not save the patch. Check permissions or choose another location."); }
        }, () => !detached && !string.IsNullOrEmpty(patch)); save.Name = "diff-save";
        void UpdateActions() => copy.IsEnabled = save.IsEnabled = !detached && !string.IsNullOrEmpty(patch);
        void Invalidate()
        {
            if (capturedLeft == (left.Text ?? "") && capturedRight == (right.Text ?? "")) return;
            capturedLeft = left.Text ?? ""; capturedRight = right.Text ?? ""; revision++; pending?.Cancel(); patch = null; rows.Children.Clear(); UpdateActions();
            state.Text = T("输入已更新，请重新比较。", "Inputs changed. Compare again.");
        }
        var compare = AsyncButton(T("比较差异", "Compare"), async () =>
        {
            pending?.Cancel(); using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); pending = cancellation;
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
        content.Children.Add(compare); content.Children.Add(Actions(copy, save)); content.Children.Add(state);
        content.Children.Add(Hint(T("UTF-8 补丁目标为 text.txt，换行统一为 LF；不修改原文。", "UTF-8 patch for text.txt with LF line endings. Original text stays unchanged.")));
        content.Children.Add(rows); UpdateActions();
        content.DetachedFromVisualTree += (_, _) => { detached = true; revision++; pending?.Cancel(); patch = null; UpdateActions(); };
        body.Children.Add(content);
    }
}
