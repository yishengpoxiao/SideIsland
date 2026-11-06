using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace CodexIsland
{
    internal sealed class FileHashPanel : StackPanel, IDisposable
    {
        private readonly bool demo;
        private readonly ComboBox algorithm = new ComboBox { Padding = new Thickness(8, 6, 8, 6), FontSize = 12, Margin = new Thickness(0, 6, 0, 6) };
        private readonly TextBox file = ToolStyle.Input(), output = ToolStyle.Input(), expected = ToolStyle.Input();
        private readonly TextBlock status = ToolStyle.Hint(""), comparison = ToolStyle.Hint("");
        private readonly ProgressBar progress = new ProgressBar { Height = 4, Minimum = 0, Maximum = 100, Foreground = Display.Brush("#A1EACF"), Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        private readonly Button choose, calculate, cancel, copy, copyManifest, saveManifest;
        private readonly Func<string, string, string> selectManifestPath;
        private readonly Action<string> writeClipboard;
        private readonly Action<string, string> writeManifest;
        private string path, result;
        private HashExportSnapshot completed;
        private bool disposed, busy, exporting;
        private int revision;
        private CancellationTokenSource pending;
        private sealed class HashExportSnapshot
        {
            internal readonly string SelectedPath, SourcePath, Algorithm, Digest, Manifest;
            internal HashExportSnapshot(string selectedPath, string sourcePath, string algorithm, string digest)
            {
                SelectedPath = selectedPath; SourcePath = sourcePath; Algorithm = algorithm; Digest = digest;
                try { Manifest = FileHashTools.CreateManifest(digest, algorithm, sourcePath ?? "Side Island demo"); }
                catch (ArgumentException) { Manifest = null; } // A readable NTFS name need not be representable as strict UTF-8.
            }
        }
        public FileHashPanel(bool demoMode) : this(demoMode, null, null) { }
        internal FileHashPanel(bool demoMode, Func<string, string, string> chooseManifestPath, Action<string> clipboardWriter, Action<string, string> manifestWriter = null)
        {
            demo = demoMode;
            selectManifestPath = chooseManifestPath ?? ChooseManifestPath;
            writeClipboard = clipboardWriter ?? Clipboard.SetText;
            writeManifest = manifestWriter ?? FileHashTools.SaveManifestNew;
            Children.Add(ToolStyle.Heading("文件是否一致，一眼核对"));
            Children.Add(ToolStyle.Hint("选择或拖入一个文件，再计算 SHA-256 / SHA-512。可与下载页面提供的校验值比较。"));
            file.IsReadOnly = true; file.TextWrapping = TextWrapping.Wrap; file.VerticalContentAlignment = VerticalAlignment.Top; file.MaxHeight = 82; file.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; file.Text = demo ? "演示文本样本 · Side Island demo" : "尚未选择文件"; Children.Add(file);
            choose = PanelActionLayout.Caption(IslandWindow.Button("选择文件", Choose)); choose.Margin = new Thickness(0, 6, 0, 0); Children.Add(choose);
            algorithm.Items.Add("SHA-256"); algorithm.Items.Add("SHA-512"); algorithm.SelectedIndex = 0; Children.Add(algorithm);
            calculate = IslandWindow.Button("开始计算", async () => await Calculate()); cancel = IslandWindow.Button("取消计算", Cancel); copy = IslandWindow.Button("复制校验值", Copy);
            Children.Add(PanelActionLayout.Create(3, calculate, cancel, copy)); Children.Add(progress);
            output.IsReadOnly = true; output.TextWrapping = TextWrapping.Wrap; output.VerticalContentAlignment = VerticalAlignment.Top; output.MinHeight = 42; output.MaxHeight = 102; output.Margin = new Thickness(0, 6, 0, 0); output.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; Children.Add(output); Children.Add(status);
            copyManifest = IslandWindow.Button("复制清单", CopyManifest); saveManifest = IslandWindow.Button("另存清单", async () => await SaveManifest());
            Children.Add(PanelActionLayout.Create(2, copyManifest, saveManifest));
            Children.Add(ToolStyle.Hint("清单仅包含文件名和校验值；另存为新文件，不覆盖现有文件。"));
            Children.Add(ToolStyle.Hint("粘贴校验值或 SHA256SUMS / SHA512SUMS 清单，自动匹配所选文件名。")); expected.MaxLength = 0; expected.AcceptsReturn = true; expected.TextWrapping = TextWrapping.Wrap; expected.VerticalContentAlignment = VerticalAlignment.Top; expected.MinHeight = 42; expected.MaxHeight = 102; expected.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; Children.Add(expected); Children.Add(comparison);
            Children.Add(ToolStyle.Hint(demo ? "演示模式 · 仅计算固定文字样本，不读取本机文件或修改剪贴板" : "计算在后台进行，可随时取消。只读取所选文件，不修改文件或上传内容；校验一致仅表示字节一致，不能替代来源判断。"));
            algorithm.SelectionChanged += (s, e) => { InvalidateResult(); UpdateControls(); };
            expected.TextChanged += (s, e) => Compare();
            AllowDrop = true;
            PreviewDragOver += (s, e) => { e.Effects = !busy && !demo && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None; e.Handled = true; };
            PreviewDrop += (s, e) => {
                e.Handled = true; if (busy || demo) return;
                try { var paths = e.Data.GetData(DataFormats.FileDrop) as string[]; if (paths == null || paths.Length != 1) { status.Text = "请每次拖入一个文件。"; return; } SetFile(paths[0]); }
                catch { status.Text = "无法读取此次拖放，请使用选择文件。"; }
            };
            UpdateControls();
        }
        private string Algorithm { get { return algorithm.SelectedIndex == 1 ? "SHA512" : "SHA256"; } }
        private void Choose()
        {
            if (demo) { status.Text = "演示模式使用固定文字样本。"; return; }
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = UiLocalization.Localize("选择要校验的文件"), CheckFileExists = true, Filter = UiLocalization.FileFilter("所有文件|*.*"), Multiselect = false };
            if (dialog.ShowDialog(Window.GetWindow(this)) == true) SetFile(dialog.FileName);
        }
        internal void AcceptExternalFile(string value) { if (!busy && !disposed && !demo) SetFile(value); }
        private void SetFile(string value) { path = value; file.Text = value; InvalidateResult(); status.Text = "文件已选择，点击开始计算。"; UpdateControls(); }
        private void InvalidateResult() { result = null; completed = null; output.Clear(); progress.Value = 0; status.Text = ""; Compare(); }
        internal async Task Calculate()
        {
            if (busy || disposed || (!demo && String.IsNullOrEmpty(path))) return;
            busy = true; InvalidateResult(); int current = ++revision; string selected = Algorithm, selectedPath = path;
            var source = new CancellationTokenSource(); pending = source;
            var updates = new Progress<int>(value => { if (!disposed && current == revision && busy) { progress.Value = value; status.Text = "正在计算… " + value + "%"; } });
            status.Text = "正在计算…"; UpdateControls();
            try
            {
                string sourcePath = demo ? null : Path.GetFullPath(selectedPath);
                string digest = await Task.Run(() => {
                    if (!demo) return FileHashTools.ComputeFile(sourcePath, selected, source.Token, updates);
                    using (var data = new MemoryStream(Encoding.UTF8.GetBytes("Side Island demo"))) return FileHashTools.Compute(data, selected, source.Token, updates);
                });
                if (disposed || current != revision) return;
                source.Token.ThrowIfCancellationRequested();
                if (Algorithm != selected || path != selectedPath) return;
                var snapshot = new HashExportSnapshot(selectedPath, sourcePath, selected, digest);
                result = digest; completed = snapshot; output.Text = digest; progress.Value = 100;
                status.Text = snapshot.Manifest == null ? "校验值已计算，但文件名无法导出为 UTF-8 清单。仍可复制校验值。" : demo ? "演示样本计算完成" : "计算完成"; Compare();
            }
            catch (OperationCanceledException) { if (!disposed && current == revision) status.Text = "已取消计算，可以重新开始。"; }
            catch (Exception error) { if (!disposed && current == revision) status.Text = error is UnauthorizedAccessException ? "无法读取文件：请检查权限或是否选择了文件夹。" : error is IOException ? "无法读取文件：可能已移动、被占用或设备暂不可用。" : "计算失败，请重新选择文件后再试。"; }
            finally { source.Dispose(); if (ReferenceEquals(pending, source)) pending = null; busy = false; if (!disposed) UpdateControls(); }
        }
        private void Cancel() { if (pending != null) { pending.Cancel(); status.Text = "正在取消…"; cancel.IsEnabled = false; } }
        private void Compare()
        {
            comparison.Foreground = Display.Brush("#8FA89A");
            if (String.IsNullOrWhiteSpace(expected.Text)) { comparison.Text = ""; return; }
            string normalized, error;
            if (!FileHashTools.TryResolveExpected(expected.Text, Algorithm, demo ? "Side Island demo" : path, out normalized, out error))
            {
                comparison.Text = error == "too-large" ? "校验清单最多支持 128 Ki 字符和 1,024 行。" :
                    error == "file-required" ? "请先选择文件，再匹配校验清单。" :
                    error == "file-missing" ? "清单中没有与所选文件名完全一致的记录。" :
                    error == "ambiguous" ? "清单中存在同名文件或冲突校验值，请仅保留目标记录。" :
                    error == "manifest" ? "清单格式无效，请使用所选算法的 GNU 或 BSD 校验清单。" :
                    Algorithm == "SHA256" ? "SHA-256 需要 64 位十六进制字符。" : "SHA-512 需要 128 位十六进制字符。";
                return;
            }
            if (result == null) { comparison.Text = "格式正确，计算完成后会进行比较。"; return; }
            bool equal = String.Equals(result, normalized, StringComparison.Ordinal); comparison.Text = equal ? "✓ 校验值一致" : "校验值不一致，请检查文件和校验算法。"; comparison.Foreground = Display.Brush(equal ? "#A1EACF" : "#F0B88F");
        }
        private void Copy() { if (disposed || busy || exporting || result == null) return; if (demo) { status.Text = "演示模式不修改剪贴板。"; return; } try { writeClipboard(result); status.Text = "已复制校验值"; } catch { status.Text = "剪贴板正忙，请稍后再试。"; } }
        private bool Current(HashExportSnapshot snapshot) { return !disposed && !busy && snapshot != null && ReferenceEquals(snapshot, completed) && path == snapshot.SelectedPath && Algorithm == snapshot.Algorithm && result == snapshot.Digest; }
        private void CopyManifest()
        {
            var snapshot = completed; if (exporting || !Current(snapshot) || snapshot.Manifest == null) return;
            if (demo) { status.Text = "演示模式不修改剪贴板。"; return; }
            try { writeClipboard(snapshot.Manifest); status.Text = "已复制校验清单。"; }
            catch { status.Text = "剪贴板正忙，请稍后再试。"; }
        }
        private string ChooseManifestPath(string name, string extension)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { Title = UiLocalization.Localize("保存校验清单"), FileName = name, DefaultExt = extension, AddExtension = true, CheckPathExists = true, OverwritePrompt = false,
                Filter = extension == ".sha512" ? "SHA-512|*.sha512" : "SHA-256|*.sha256" };
            return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FileName : null;
        }
        private async Task SaveManifest()
        {
            var snapshot = completed; if (exporting || !Current(snapshot) || snapshot.Manifest == null) return;
            if (demo) { status.Text = "演示模式不保存文件。"; return; }
            exporting = true; UpdateControls();
            try
            {
                string extension = snapshot.Algorithm == "SHA512" ? ".sha512" : ".sha256";
                string destination = selectManifestPath(Path.GetFileName(snapshot.SourcePath) + extension, extension);
                if (disposed || String.IsNullOrEmpty(destination)) return;
                // Native dialogs pump the UI: selection, algorithm, a new job,
                // or disposal may have invalidated this result while open.
                if (!Current(snapshot)) { status.Text = "计算结果已更新，请重新导出清单。"; return; }
                if (String.Equals(Path.GetFullPath(destination), snapshot.SourcePath, StringComparison.OrdinalIgnoreCase)) { status.Text = "不能覆盖源文件，请另选新文件名。"; return; }
                status.Text = "正在保存校验清单…";
                await Task.Run(() => writeManifest(destination, snapshot.Manifest));
                if (Current(snapshot)) status.Text = "校验清单已保存。";
            }
            catch (IOException) { if (Current(snapshot)) status.Text = "清单未保存：目标可能已存在或不可写，请选择新文件名。"; }
            catch (Exception) { if (Current(snapshot)) status.Text = "清单保存失败，请检查路径和权限。"; }
            finally { exporting = false; if (!disposed) UpdateControls(); }
        }
        private void UpdateControls() { choose.IsEnabled = algorithm.IsEnabled = !busy && !disposed; calculate.IsEnabled = !disposed && !busy && (demo || !String.IsNullOrEmpty(path)); cancel.IsEnabled = !disposed && busy; copy.IsEnabled = !disposed && !busy && !exporting && result != null; copyManifest.IsEnabled = saveManifest.IsEnabled = !exporting && Current(completed) && completed.Manifest != null; progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed; }
        public void Dispose() { disposed = true; revision++; if (pending != null) pending.Cancel(); UpdateControls(); }
    }
}
