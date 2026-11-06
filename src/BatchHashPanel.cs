using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace CodexIsland
{
    internal sealed class BatchHashPanel : StackPanel, IDisposable
    {
        private readonly bool demo;
        private readonly Func<string, string, string> selectDestination;
        private readonly Action<string> clipboard;
        private readonly Action<string, string> save;
        private readonly IBatchHashProvider provider;
        private readonly TextBox selected = ToolStyle.Input(), expected = ToolStyle.Input(), digest = ToolStyle.Input();
        private readonly ComboBox algorithm = new ComboBox { Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 6, 0, 6) };
        private readonly TextBlock status = ToolStyle.Hint(""), exportNote = ToolStyle.Hint("");
        private readonly ListBox results = new ListBox { MinHeight = 48, MaxHeight = 190, HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 6, 0, 6) };
        private readonly ProgressBar progress = new ProgressBar { Height = 4, Minimum = 0, Maximum = 100, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        private readonly Button choose, clear, calculate, cancel, copyManifest, saveManifest;
        private readonly List<string> paths = new List<string>();
        private CancellationTokenSource pending;
        private BatchHashResult completed;
        private string manifest;
        private bool disposed, busy, exporting;
        private int revision;
        internal bool IsBusy { get { return busy; } }
        internal BatchHashResult Result { get { return completed; } }
        internal Task LatestExport { get; private set; }

        internal BatchHashPanel(bool demoMode, Func<string, string, string> destination, Action<string> clipboardWriter, Action<string, string> manifestWriter, IBatchHashProvider testProvider = null)
        {
            demo = demoMode; selectDestination = destination; clipboard = clipboardWriter; save = manifestWriter; provider = testProvider; LatestExport = Task.FromResult(0);
            Children.Add(ToolStyle.Heading("批量文件校验"));
            Children.Add(ToolStyle.Hint("最多选择 200 个文件，依次计算 SHA-256 / SHA-512。取消后保留已完成结果。"));
            choose = IslandWindow.Button("选择多个文件", Choose); clear = IslandWindow.Button("清空选择", () => AcceptFiles(new string[0]));
            Children.Add(PanelActionLayout.Create(2, choose, clear));
            selected.IsReadOnly = true; selected.AcceptsReturn = true; selected.TextWrapping = TextWrapping.Wrap; selected.MaxHeight = 82; selected.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; selected.Margin = new Thickness(0, 6, 0, 0); Children.Add(selected);
            algorithm.Items.Add("SHA-256"); algorithm.Items.Add("SHA-512"); algorithm.SelectedIndex = 0; Children.Add(algorithm);
            Children.Add(ToolStyle.Hint("可选：粘贴 GNU / BSD 校验清单，逐个匹配准确文件名。"));
            expected.AcceptsReturn = true; expected.TextWrapping = TextWrapping.Wrap; expected.MinHeight = 48; expected.MaxHeight = 100; expected.MaxLength = 0; expected.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; Children.Add(expected);
            calculate = IslandWindow.Button("计算 / 校验", async () => await Calculate()); cancel = IslandWindow.Button("取消计算", Cancel); Children.Add(PanelActionLayout.Create(2, calculate, cancel)); Children.Add(progress); Children.Add(status);
            ScrollViewer.SetHorizontalScrollBarVisibility(results, ScrollBarVisibility.Disabled); Children.Add(results);
            digest.IsReadOnly = true; digest.TextWrapping = TextWrapping.Wrap; digest.MinHeight = 36; digest.MaxHeight = 76; digest.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; Children.Add(digest);
            copyManifest = IslandWindow.Button("复制清单", CopyManifest); saveManifest = IslandWindow.Button("另存清单", async () => await (LatestExport = SaveManifest()));
            Children.Add(PanelActionLayout.Create(2, copyManifest, saveManifest)); Children.Add(exportNote);
            Children.Add(ToolStyle.Hint("所有文件完成且没有同名歧义时可导出总清单。只读源文件，另存不覆盖现有文件。"));
            algorithm.SelectionChanged += (s, e) => Invalidate(); expected.TextChanged += (s, e) => Invalidate();
            results.SelectionChanged += (s, e) => { var row = results.SelectedItem as ListBoxItem; var item = row == null ? null : row.Tag as BatchHashItem; digest.Text = item == null ? "" : item.Digest ?? ""; };
            if (demo) status.Text = "演示模式不读取或更改本机文件。";
            UpdateControls();
        }
        private string Algorithm { get { return algorithm.SelectedIndex == 1 ? "SHA512" : "SHA256"; } }
        private void Choose()
        {
            if (disposed || demo || busy || exporting) return;
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = UiLocalization.Localize("选择多个文件"), Multiselect = true, CheckFileExists = true, Filter = UiLocalization.FileFilter("所有文件|*.*") };
            if (dialog.ShowDialog(Window.GetWindow(this)) == true && !disposed && !busy && !exporting) AcceptFiles(dialog.FileNames);
        }
        internal void AcceptFiles(IEnumerable<string> values)
        {
            if (disposed || demo || busy || exporting) return;
            string[] next = (values ?? new string[0]).Take(BatchHashTools.MaximumFiles + 1).ToArray();
            if (next.Length > BatchHashTools.MaximumFiles) { status.Text = "一次最多选择 200 个文件，原选择已保留。"; return; }
            paths.Clear(); paths.AddRange(next); selected.Text = String.Join(Environment.NewLine, next); Invalidate();
        }
        private void Invalidate()
        {
            revision++; if (pending != null) pending.Cancel(); completed = null; manifest = null; results.Items.Clear(); digest.Clear(); progress.Value = 0;
            status.Text = demo ? "演示模式不读取或更改本机文件。" : paths.Count == 0 ? "尚未选择文件" : UiLocalization.Format("已选择 {0} 个文件。", paths.Count); UpdateControls();
        }
        internal async Task Calculate()
        {
            if (disposed || busy || exporting || demo || paths.Count == 0) return;
            if (!FileHashPanel.TryBeginRead()) { status.Text = "上一次文件读取尚未结束，请稍后重试。"; return; }
            var source = new CancellationTokenSource(); pending = source; busy = true; int request = ++revision;
            string[] selection = paths.ToArray(); string kind = Algorithm, comparison = expected.Text;
            completed = null; manifest = null; results.Items.Clear(); digest.Clear(); progress.Value = 0; status.Text = "正在批量计算…"; UpdateControls();
            Func<bool> current = () => !disposed && revision == request && ReferenceEquals(pending, source);
            var updates = new Progress<BatchHashProgress>(value =>
            {
                if (!current() || !busy) return;
                progress.IsIndeterminate = value.FileLength < 0;
                progress.Value = value.FileLength <= 0 ? 0 : Math.Min(100, value.FileBytesRead * 100.0 / value.FileLength);
                status.Text = UiLocalization.Format("文件 {0}/{1} · 已读取 {2} 字节", value.Index + 1, value.FileCount, value.TotalBytesRead);
            });
            var rows = new Progress<BatchHashItem>(item => { if (current() && busy) AddRow(item); });
            try
            {
                BatchHashResult result = await Task.Run(() => BatchHashTools.Run(selection, kind, comparison, source.Token, updates, item => ((IProgress<BatchHashItem>)rows).Report(item), provider));
                if (!current()) return;
                completed = result; results.Items.Clear(); foreach (var item in result.Items) AddRow(item);
                string error; if (!BatchHashTools.TryCreateManifest(result, out manifest, out error)) exportNote.Text = ExportError(error);
                status.Text = UiLocalization.Format(result.Cancelled ? "已取消 · 完成 {0}/{1} 个文件" : "计算结束 · 完成 {0}/{1} 个文件", result.Items.Count(item => item.Status == BatchHashStatus.Completed), result.Items.Count);
                progress.IsIndeterminate = false; progress.Value = result.Items.All(item => item.Status == BatchHashStatus.Completed) ? 100 : 0;
                if (results.Items.Count > 0) results.SelectedIndex = 0;
            }
            catch (Exception) { if (current()) status.Text = "批量计算未完成，已显示的结果仍保留。"; }
            finally
            {
                if (ReferenceEquals(pending, source)) pending = null; source.Dispose(); busy = false; FileHashPanel.EndRead(); if (!disposed) UpdateControls();
            }
        }
        internal void Cancel() { if (pending != null) { pending.Cancel(); status.Text = "正在取消…"; cancel.IsEnabled = false; } }
        private void AddRow(BatchHashItem item)
        {
            var content = new StackPanel();
            var name = ToolStyle.Hint(item.FileName, false); name.FontSize = 12; name.Margin = new Thickness(0, 0, 0, 2); name.ToolTip = item.Path; content.Children.Add(name);
            var state = ToolStyle.Hint(ItemStatus(item)); state.Margin = new Thickness(0); content.Children.Add(state);
            var row = new ListBoxItem { Content = content, Tag = item, Padding = new Thickness(6, 4, 6, 4), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            results.Items.Add(row);
        }
        private static string ItemStatus(BatchHashItem item)
        {
            if (item.Status == BatchHashStatus.NotStarted) return "未开始";
            if (item.Status == BatchHashStatus.Cancelled) return "已取消";
            if (item.Status == BatchHashStatus.Failed) return item.ErrorCode == "changed" ? "文件在读取期间发生变化，请重试。" : item.ErrorCode == "file-missing" ? "找不到文件" : item.ErrorCode == "access" ? "无法读取文件：请检查访问权限。" : item.ErrorCode == "not-regular" ? "请选择普通文件，不支持文件夹或符号链接。" : "无法读取此文件，其他结果已保留。";
            if (item.Verification == BatchHashVerification.Matched) return "✓ 校验值一致";
            if (item.Verification == BatchHashVerification.Mismatched) return "校验值不一致，请检查文件和校验算法。";
            if (item.Verification == BatchHashVerification.InvalidExpected)
                return item.ExpectedError == "ambiguous-selection" ? "所选文件同名，无法唯一匹配清单。" : item.ExpectedError == "manifest-required" ? "多个文件需要校验清单，不能共用单个校验值。" : item.ExpectedError == "file-missing" ? "清单中没有与所选文件名完全一致的记录。" : item.ExpectedError == "too-large" ? "校验清单最多支持 128 Ki 字符和 1,024 行。" : "校验清单无效或存在歧义，校验值已保留。";
            return "计算完成";
        }
        private static string ExportError(string error) { return error == "ambiguous-selection" ? "同名文件无法导出为总清单，请分别处理。" : error == "too-large" ? "总清单超过容量限制，请减少选择的文件。" : error == "filename" ? "此文件名无法导出为 UTF-8 清单。" : "完整计算后可导出总清单；可选中已完成项复制校验值。"; }
        private bool Current(BatchHashResult value, int request) { return !disposed && !busy && value != null && ReferenceEquals(completed, value) && request == revision; }
        private void CopyManifest()
        {
            if (demo || busy || disposed || exporting || manifest == null) return;
            try { clipboard(manifest); status.Text = "已复制校验清单。"; } catch { status.Text = "剪贴板正忙，请稍后再试。"; }
        }
        internal async Task SaveManifest()
        {
            if (demo || busy || disposed || exporting || manifest == null) return;
            BatchHashResult snapshot = completed; string text = manifest; int request = revision; exporting = true; UpdateControls();
            try
            {
                string extension = snapshot.Algorithm == "SHA512" ? ".sha512" : ".sha256";
                string destination = selectDestination(snapshot.Algorithm == "SHA512" ? "SHA512SUMS.sha512" : "SHA256SUMS.sha256", extension);
                if (String.IsNullOrEmpty(destination) || !Current(snapshot, request)) return;
                string full = Path.GetFullPath(destination);
                if (snapshot.Items.Any(item => String.Equals(Path.GetFullPath(item.Path), full, StringComparison.OrdinalIgnoreCase))) { status.Text = "不能覆盖源文件，请另选新文件名。"; return; }
                await Task.Run(() => save(full, text));
                if (Current(snapshot, request)) status.Text = "校验清单已保存。";
            }
            catch (IOException) { if (Current(snapshot, request)) status.Text = "清单未保存：目标可能已存在或不可写，请选择新文件名。"; }
            catch (Exception) { if (Current(snapshot, request)) status.Text = "清单保存失败，请检查路径和权限。"; }
            finally { exporting = false; if (!disposed) UpdateControls(); }
        }
        private void UpdateControls()
        {
            bool editable = !disposed && !busy && !exporting && !demo;
            choose.IsEnabled = clear.IsEnabled = expected.IsEnabled = algorithm.IsEnabled = editable;
            calculate.IsEnabled = editable && paths.Count > 0; cancel.IsEnabled = !disposed && busy && pending != null && !pending.IsCancellationRequested;
            copyManifest.IsEnabled = saveManifest.IsEnabled = editable && manifest != null;
            progress.Visibility = busy || completed != null ? Visibility.Visible : Visibility.Collapsed;
            if (manifest != null) exportNote.Text = ""; else if (completed == null) exportNote.Text = "";
        }
        public void Dispose() { disposed = true; revision++; if (pending != null) pending.Cancel(); UpdateControls(); }
    }
}
