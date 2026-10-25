using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace CodexIsland
{
    internal sealed class BatchFilesPanel : StackPanel, IDisposable
    {
        private sealed class PreviewLine { public string Caption { get; set; } public string Details { get; set; } }
        private readonly bool demo;
        private readonly ComboBox mode = new ComboBox { Margin = new Thickness(0, 4, 0, 6), Padding = new Thickness(8, 5, 8, 5) };
        private readonly TextBlock selection = ToolStyle.Hint("尚未选择文件"), status = ToolStyle.Hint("");
        private readonly TextBox template = ToolStyle.Input(), find = ToolStyle.Input(), replace = ToolStyle.Input(), start = ToolStyle.Input(), padding = ToolStyle.Input();
        private readonly TextBox output = ToolStyle.Input(), maxWidth = ToolStyle.Input(), maxHeight = ToolStyle.Input(), quality = ToolStyle.Input();
        private readonly ComboBox format = new ComboBox { Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 4, 0, 6) };
        private readonly CheckBox resize = new CheckBox { Content = "限制图片最大尺寸（保持比例、不放大）", IsChecked = true, Margin = new Thickness(0, 6, 0, 6) };
        private readonly StackPanel renameFields = new StackPanel(), imageFields = new StackPanel();
        private readonly ListBox preview = new ListBox { MaxHeight = 190, MinHeight = 60, Margin = new Thickness(0, 6, 0, 6), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        private readonly ProgressBar progress = new ProgressBar { Minimum = 0, Maximum = 100, Height = 5, Margin = new Thickness(0, 5, 0, 5), Visibility = Visibility.Collapsed };
        private readonly Button choose, clear, createPreview, execute, cancel, undo, chooseOutput;
        private readonly List<string> paths = new List<string>();
        private BatchRenamePlan renamePlan;
        private List<BatchImageEntry> imagePlan;
        private BatchImageOptions imageOptions;
        private BatchRenameTransaction transaction;
        private CancellationTokenSource pending;
        private bool busy, disposed;
        private int revision;
        private string activityId;
        private readonly Stopwatch activityClock = new Stopwatch();
        private long lastActivity;
        public event Action<TaskActivityUpdate> ActivityChanged;
        internal int SelectedCount { get { return paths.Count; } }
        internal bool HasPreview { get { return renamePlan != null || imagePlan != null; } }
        internal bool CanUndo { get { return transaction != null && !transaction.Undone; } }

        public BatchFilesPanel(bool demoMode)
        {
            demo = demoMode;
            Children.Add(ToolStyle.Heading("批量文件处理")); Children.Add(ToolStyle.Hint("选择或拖入文件，先预览再执行。重命名保留扩展名并可撤销；图片处理始终另存，不覆盖原图。"));
            choose = IslandWindow.Button("选择多个文件", Choose); clear = IslandWindow.Button("清空选择", () => { paths.Clear(); InvalidatePreview(); RefreshSelection(); }); Children.Add(PanelActionLayout.Create(2, choose, clear)); Children.Add(selection);
            mode.Items.Add("批量重命名"); mode.Items.Add("批量图片处理"); mode.SelectedIndex = 0; Children.Add(mode);
            template.Text = "{name}_{n}"; start.Text = "1"; padding.Text = "3";
            renameFields.Children.Add(ToolStyle.Hint("文件名模板（{name} 原名，{n} 编号，保留扩展名）")); renameFields.Children.Add(template);
            renameFields.Children.Add(FieldRow("查找原名中的文字", find, "替换为", replace)); renameFields.Children.Add(FieldRow("起始编号", start, "编号位数（1–9）", padding));
            renameFields.Children.Add(ToolStyle.Hint("预览时校验文件内容。执行及撤销会锁定文件，不覆盖冲突目标；文件改变后会停止。撤销记录仅保留到退出或下次重命名。")); Children.Add(renameFields);
            output.IsReadOnly = true; output.TextWrapping = TextWrapping.Wrap; output.MinHeight = 32; output.MaxHeight = 70;
            chooseOutput = IslandWindow.Button("选择输出文件夹", ChooseOutput); imageFields.Children.Add(chooseOutput); imageFields.Children.Add(output);
            format.Items.Add("JPG"); format.Items.Add("PNG"); format.SelectedIndex = 0; imageFields.Children.Add(ToolStyle.Hint("图片输出格式")); imageFields.Children.Add(format);
            maxWidth.Text = "1920"; maxHeight.Text = "1080"; quality.Text = "82"; imageFields.Children.Add(resize); imageFields.Children.Add(FieldRow("最大宽度", maxWidth, "最大高度", maxHeight)); imageFields.Children.Add(ToolStyle.Hint("JPG 质量（1–100）")); imageFields.Children.Add(quality);
            imageFields.Children.Add(ToolStyle.Hint("JPG / PNG 最多 64 MB、2400 万像素。取消后保留已完成的新文件，原文件始终不变。")); Children.Add(imageFields);
            createPreview = IslandWindow.Button("生成处理预览", async () => await PreviewAsync()); execute = IslandWindow.Button("执行预览操作", async () => await ExecuteAsync()); cancel = IslandWindow.Button("取消处理", () => { if (pending != null) pending.Cancel(); });
            Children.Add(PanelActionLayout.Create(3, createPreview, execute, cancel));
            var itemText = new FrameworkElementFactory(typeof(TextBlock)); itemText.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Caption")); itemText.SetBinding(TextBlock.ToolTipProperty, new System.Windows.Data.Binding("Details")); itemText.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); itemText.SetValue(TextBlock.MarginProperty, new Thickness(6, 4, 6, 4)); preview.ItemTemplate = new DataTemplate { VisualTree = itemText }; UiLocalization.Protect(preview); Children.Add(preview);
            undo = IslandWindow.Button("撤销上次重命名", async () => await UndoAsync()); Children.Add(undo); Children.Add(progress); Children.Add(status);
            foreach (var box in new[] { template, find, replace, start, padding, maxWidth, maxHeight, quality }) { box.MaxLength = 180; box.TextChanged += (s, e) => InvalidatePreview(); }
            start.MaxLength = 9; padding.MaxLength = 1; maxWidth.MaxLength = maxHeight.MaxLength = 5; quality.MaxLength = 3;
            mode.SelectionChanged += (s, e) => { InvalidatePreview(); UpdateControls(); }; format.SelectionChanged += (s, e) => InvalidatePreview(); resize.Checked += (s, e) => InvalidatePreview(); resize.Unchecked += (s, e) => InvalidatePreview();
            AllowDrop = true; PreviewDragOver += (s, e) => { e.Effects = !busy && !demo && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
            PreviewDrop += (s, e) => { e.Handled = true; if (busy || demo || disposed) return; try { AcceptFiles(e.Data.GetData(DataFormats.FileDrop) as string[]); } catch (Exception error) { status.Text = Error(error); } };
            UiLocalization.Combo(mode); UiLocalization.Combo(format); UiLocalization.Content(resize, "限制图片最大尺寸（保持比例、不放大）");
            if (demo) status.Text = "演示模式不读取或更改本机文件。"; UpdateControls();
        }
        private static Grid FieldRow(string firstLabel, TextBox first, string secondLabel, TextBox second)
        {
            var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
            var left = new StackPanel(); left.Children.Add(ToolStyle.Hint(firstLabel)); left.Children.Add(first); var right = new StackPanel(); right.Children.Add(ToolStyle.Hint(secondLabel)); right.Children.Add(second); Grid.SetColumn(right, 2); grid.Children.Add(left); grid.Children.Add(right); return grid;
        }
        private void Choose()
        {
            if (demo || disposed || busy) return;
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = UiLocalization.Localize("选择多个文件"), Multiselect = true, CheckFileExists = true, Filter = UiLocalization.FileFilter(mode.SelectedIndex == 1 ? "JPG / PNG 图片|*.jpg;*.jpeg;*.png" : "所有文件|*.*") };
            if (dialog.ShowDialog(Window.GetWindow(this)) == true) AcceptFiles(dialog.FileNames);
        }
        internal void AcceptFiles(IEnumerable<string> files)
        {
            if (demo || disposed || busy) return;
            var next = BatchFiles.NormalizePaths(files); paths.Clear(); paths.AddRange(next); InvalidatePreview(); RefreshSelection();
        }
        private void ChooseOutput()
        {
            if (demo || disposed || busy) return;
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = UiLocalization.Localize("选择输出文件夹"), ShowNewFolderButton = true })
                if (NativeDialogs.ShowFolder(dialog, Window.GetWindow(this)) == System.Windows.Forms.DialogResult.OK) { output.Text = dialog.SelectedPath; InvalidatePreview(); }
        }
        private void RefreshSelection() { selection.Text = paths.Count == 0 ? "尚未选择文件" : UiLocalization.Format("已选择 {0} 个文件", paths.Count); UpdateControls(); }
        private void InvalidatePreview() { if (busy) return; renamePlan = null; imagePlan = null; preview.Items.Clear(); UpdateControls(); }
        private int Number(TextBox input) { int value; if (!Int32.TryParse(input.Text, NumberStyles.None, CultureInfo.InvariantCulture, out value)) throw new ArgumentException("数字参数请输入有效整数。"); return value; }
        private BatchImageOptions ReadImageOptions()
        {
            bool limitSize = resize.IsChecked == true, jpeg = format.SelectedIndex == 0;
            // Disabled drafts remain in their text boxes for later reuse, but
            // do not invalidate an operation that does not consume them.
            return new BatchImageOptions { OutputDirectory = output.Text, Format = jpeg ? "JPG" : "PNG", MaximumWidth = limitSize ? Number(maxWidth) : 1, MaximumHeight = limitSize ? Number(maxHeight) : 1, Quality = jpeg ? Number(quality) : 82, Resize = limitSize };
        }
        internal async Task PreviewAsync()
        {
            if (busy || disposed || demo || paths.Count == 0) return;
            var selected = paths.ToArray(); bool images = mode.SelectedIndex == 1; BatchRenameOptions renameOptions = null; BatchImageOptions imagesOptions = null;
            try { if (images) imagesOptions = ReadImageOptions(); else renameOptions = new BatchRenameOptions { Template = template.Text, Find = find.Text, Replace = replace.Text, Start = Number(start), Padding = Number(padding) }; }
            catch (Exception error) { status.Text = Error(error); return; }
            InvalidatePreview(); var work = Begin("正在校验并生成预览…", false); int current = revision;
            try
            {
                var updates = Updates(current, false);
                if (images)
                {
                    var plan = await Task.Run(() => BatchImageFiles.Preview(selected, imagesOptions, work.Token, updates)); work.Token.ThrowIfCancellationRequested(); if (disposed || current != revision) return; imagePlan = plan; imageOptions = imagesOptions;
                    foreach (var entry in plan) preview.Items.Add(new PreviewLine { Caption = Path.GetFileName(entry.SourcePath) + " → " + Path.GetFileName(entry.OutputPath) + " · " + entry.Width + " × " + entry.Height, Details = entry.SourcePath + "\n→ " + entry.OutputPath });
                }
                else
                {
                    var plan = await Task.Run(() => BatchFiles.Preview(selected, renameOptions, work.Token, updates)); work.Token.ThrowIfCancellationRequested(); if (disposed || current != revision) return; renamePlan = plan;
                    foreach (var entry in plan.Entries) preview.Items.Add(new PreviewLine { Caption = Path.GetFileName(entry.OriginalPath) + " → " + Path.GetFileName(entry.DestinationPath), Details = entry.OriginalPath + "\n→ " + entry.DestinationPath });
                }
                status.Text = "预览已生成，请核对全部文件名后执行。";
            }
            catch (OperationCanceledException) { if (!disposed) status.Text = "已取消处理。"; }
            catch (Exception error) { if (!disposed) status.Text = Error(error); }
            finally { Finish(work); }
        }
        internal async Task ExecuteAsync()
        {
            if (busy || disposed || demo || !HasPreview) return;
            bool images = imagePlan != null; var selectedImages = imagePlan; var selectedRename = renamePlan; var selectedOptions = imageOptions;
            var work = Begin(images ? "正在批量处理图片…" : "正在批量重命名…", true); int current = revision;
            var updates = Updates(current, true);
            try
            {
                if (images)
                {
                    var result = await Task.Run(() => BatchImageFiles.Execute(selectedImages, selectedOptions, work.Token, updates));
                    if (disposed || current != revision) return;
                    status.Text = result.Canceled ? UiLocalization.Format("已取消，保留 {0} 个已完成文件。", result.Saved.Count) : UiLocalization.Format("已生成 {0} 张新图片，原图未修改。", result.Saved.Count);
                    Emit(result.Canceled ? "cancelled" : "completed", status.Text, result.Canceled ? (double?)null : 100, true);
                }
                else
                {
                    transaction = await Task.Run(() => BatchFiles.Execute(selectedRename, work.Token, updates));
                    if (disposed || current != revision) return;
                    paths.Clear(); paths.AddRange(selectedRename.Entries.Select(entry => entry.DestinationPath)); RefreshSelection(); status.Text = "重命名完成，可撤销上次操作。"; Emit("completed", status.Text, 100, true);
                }
                renamePlan = null; imagePlan = null;
            }
            catch (OperationCanceledException) { if (!disposed) { status.Text = "已取消处理。"; Emit("cancelled", status.Text, null, true); } }
            catch (Exception error) { if (!disposed) { status.Text = Error(error); Emit("failed", status.Text, null, true); } }
            finally { Finish(work); }
        }
        internal async Task UndoAsync()
        {
            if (busy || disposed || demo || !CanUndo) return;
            var selected = transaction; var work = Begin("正在撤销重命名…", true); int current = revision;
            var updates = Updates(current, true);
            try
            {
                await Task.Run(() => selected.Undo(work.Token, updates));
                if (disposed || current != revision) return; paths.Clear(); paths.AddRange(selected.Entries.Select(entry => entry.OriginalPath)); RefreshSelection(); renamePlan = null; imagePlan = null; preview.Items.Clear(); status.Text = "已恢复原文件名。"; Emit("completed", status.Text, 100, true);
            }
            catch (OperationCanceledException) { if (!disposed) { status.Text = "已取消处理。"; Emit("cancelled", status.Text, null, true); } }
            catch (Exception error) { if (!disposed) { status.Text = Error(error); Emit("failed", status.Text, null, true); } }
            finally { Finish(work); }
        }
        private CancellationTokenSource Begin(string message, bool activity)
        {
            busy = true; revision++; pending = new CancellationTokenSource(); progress.Value = 0; status.Text = message;
            if (activity) { activityId = "batch:" + Guid.NewGuid().ToString("N"); activityClock.Restart(); lastActivity = -100; Emit("running", message, 0, true); }
            UpdateControls(); return pending;
        }
        private IProgress<int> Updates(int current, bool activity)
        {
            return new Progress<int>(value => { if (disposed || !busy || revision != current) return; progress.Value = value; if (activity) Emit("running", "正在处理…", value, false); });
        }
        private void Emit(string state, string message, double? value, bool force)
        {
            if (String.IsNullOrEmpty(activityId) || (!force && activityClock.ElapsedMilliseconds - lastActivity < 100)) return; lastActivity = activityClock.ElapsedMilliseconds;
            var callback = ActivityChanged; if (callback != null) callback(new TaskActivityUpdate { Id = activityId, Title = UiLocalization.Localize("批量文件处理"), State = state, Message = message, Progress = value, Source = "批量文件" });
        }
        private void Finish(CancellationTokenSource work) { work.Dispose(); if (ReferenceEquals(pending, work)) pending = null; busy = false; if (!disposed) UpdateControls(); }
        private void UpdateControls()
        {
            if (choose == null) return;
            renameFields.Visibility = mode.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed; imageFields.Visibility = mode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            choose.IsEnabled = !busy && !demo; clear.IsEnabled = !busy && paths.Count > 0; mode.IsEnabled = renameFields.IsEnabled = imageFields.IsEnabled = !busy;
            maxWidth.IsEnabled = maxHeight.IsEnabled = !busy && resize.IsChecked == true; quality.IsEnabled = !busy && format.SelectedIndex == 0;
            if (createPreview != null) createPreview.IsEnabled = !busy && !demo && paths.Count > 0;
            if (execute != null) execute.IsEnabled = !busy && !demo && (imagePlan != null || (renamePlan != null && renamePlan.ChangedCount > 0));
            if (cancel != null) cancel.IsEnabled = busy; if (undo != null) undo.IsEnabled = !busy && !demo && CanUndo;
            progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed; preview.Visibility = preview.Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        private static string Error(Exception error)
        {
            if (UiLocalization.HasTranslation(error.Message)) return error.Message;
            return error is UnauthorizedAccessException || error is System.ComponentModel.Win32Exception ? "无法访问或锁定文件，请关闭正在编辑它的程序并检查权限。" : "批量处理失败，原文件未被覆盖，请检查预览与输出位置。";
        }
        internal bool TryPrepareClose()
        {
            if (!busy) return true;
            if (pending != null) pending.Cancel();
            status.Text = "批量任务正在安全结束，请稍后再次退出。";
            return false;
        }
        public void Dispose() { if (disposed) return; disposed = true; revision++; if (pending != null) pending.Cancel(); if (busy) Emit("cancelled", UiLocalization.Localize("已取消处理。"), null, true); ActivityChanged = null; }
    }
}
