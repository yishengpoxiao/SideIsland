using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CodexIsland
{
    internal sealed class FileShelfPanel : StackPanel, IDisposable
    {
        private readonly bool demo;
        private readonly FileShelf shelf;
        private readonly StackPanel list = new StackPanel();
        private readonly TextBlock status = ToolStyle.Hint("");
        private readonly TextBlock counter = ToolStyle.Hint("");
        private readonly Dictionary<string, FileShelfMetadata> metadata = new Dictionary<string, FileShelfMetadata>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Task<FileShelfMetadata>> inspections = new Dictionary<string, Task<FileShelfMetadata>>(StringComparer.OrdinalIgnoreCase);
        private int revision;
        private bool disposed;
        private bool initiallyRefreshed;

        public FileShelfPanel(bool demoMode)
        {
            demo = demoMode; shelf = new FileShelf(new FileShelfStore(), demo);
            Children.Add(ToolStyle.Heading("文件随手放"));
            Children.Add(ToolStyle.Hint("拖入文件或文件夹，随时打开与定位。仅保存引用，不复制、移动或删除原文件；支持本机路径。"));
            var dropText = IslandWindow.Text("＋  把文件拖到这里", 15, "#A1EACF", true);
            dropText.TextWrapping = TextWrapping.Wrap; dropText.TextAlignment = TextAlignment.Center; dropText.HorizontalAlignment = HorizontalAlignment.Stretch; dropText.Margin = new Thickness(0, 4, 0, 4);
            var dropZone = ToolStyle.Card(dropText); dropZone.BorderBrush = Display.Brush("#4B7A63"); dropZone.BorderThickness = new Thickness(1); Children.Add(dropZone);
            AllowDrop = true;
            PreviewDragOver += DragOverFiles;
            PreviewDrop += DropFiles;
            var actions = PanelActionLayout.Create(2,
                IslandWindow.Button("选择文件", ChooseFiles), IslandWindow.Button("选择文件夹", ChooseFolder), IslandWindow.Button("刷新状态", Refresh),
                IslandWindow.Button("清空引用", () => { shelf.Clear(); Persist(); Rebuild(); }));
            Children.Add(actions);
            var summary = new Grid { Margin = new Thickness(0, 6, 0, 0) }; summary.ColumnDefinitions.Add(new ColumnDefinition()); summary.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            counter.Margin = new Thickness(0, 0, 6, 0); counter.VerticalAlignment = VerticalAlignment.Center; summary.Children.Add(counter);
            var retry = PanelActionLayout.Caption(IslandWindow.Button("重试保存", () => { if (TryFlush()) status.Text = demo ? "演示模式不会保存" : "引用已保存"; })); retry.Padding = new Thickness(8, 5, 8, 5); retry.MinHeight = 32; retry.MaxWidth = 145; Grid.SetColumn(retry, 1); summary.Children.Add(retry);
            Children.Add(summary); Children.Add(status); Children.Add(list);
            status.Text = demo ? "演示模式 · 不访问文件或剪贴板，修改不会保存" : shelf.LoadFailed ? "原收纳文件部分或全部无法读取；修改保存前会先保留备份。" : "常见文档、图片和音视频可直接打开；其他文件类型仅定位。";
            if (demo)
            {
                string error;
                shelf.Add("C:\\SideIslandDemo\\项目计划.docx", out error);
                shelf.Add("C:\\SideIslandDemo\\灵感素材", out error);
                shelf.Add("C:\\SideIslandDemo\\界面草图.png", out error);
                foreach (string path in shelf.Entries)
                    metadata[path] = new FileShelfMetadata { Path = path, Name = Path.GetFileName(path), Extension = Path.GetExtension(path), Available = true, CanOpen = true, IsDirectory = Path.GetExtension(path) == "", Size = 243712, Status = Path.GetExtension(path) == "" ? "文件夹 · 不扫描内容" : Path.GetExtension(path).ToUpperInvariant() + " · 238 KB" };
            }
            Rebuild();
            Loaded += (s, e) => { if (!initiallyRefreshed) { initiallyRefreshed = true; Refresh(); } };
        }

        private void DragOverFiles(object sender, DragEventArgs e)
        {
            e.Effects = !demo && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None; e.Handled = true;
        }

        private void DropFiles(object sender, DragEventArgs e)
        {
            e.Handled = true;
            if (demo) return;
            try { var paths = e.Data.GetData(DataFormats.FileDrop) as string[]; if (paths != null) AddPaths(paths); }
            catch { status.Text = "无法读取本次拖放，请使用“选择文件”。"; }
        }

        private void ChooseFiles()
        {
            if (demo) { status.Text = "演示模式不会访问本机文件。"; return; }
            var picker = new Microsoft.Win32.OpenFileDialog { Title = UiLocalization.Localize("收纳文件引用"), Multiselect = true, CheckFileExists = true, Filter = UiLocalization.FileFilter("所有文件|*.*"), DereferenceLinks = false };
            if (picker.ShowDialog(Window.GetWindow(this)) == true) AddPaths(picker.FileNames);
        }

        private void ChooseFolder()
        {
            if (demo) { status.Text = "演示模式不会访问本机文件。"; return; }
            using (var picker = new System.Windows.Forms.FolderBrowserDialog { Description = UiLocalization.Localize("收纳本地文件夹引用（不会扫描内容）"), ShowNewFolderButton = false })
                if (NativeDialogs.ShowFolder(picker, Window.GetWindow(this)) == System.Windows.Forms.DialogResult.OK) AddPaths(new[] { picker.SelectedPath });
        }

        internal void AcceptExternalFiles(IEnumerable<string> paths) { if (!demo && !disposed) AddPaths(paths); }
        private void AddPaths(IEnumerable<string> paths)
        {
            int added = 0, skipped = 0; string lastError = null;
            foreach (string path in paths.Take(100)) { string error; if (shelf.Add(path, out error)) added++; else { skipped++; lastError = error; } }
            status.Text = "已收纳 " + added + " 项" + (skipped > 0 ? "，跳过 " + skipped + " 项。" + lastError : " · 原文件保持原位");
            Persist(); Rebuild(); Refresh();
        }

        private void Rebuild()
        {
            list.Children.Clear(); counter.Text = shelf.Entries.Count + " / " + FileShelfStore.MaxEntries + " 项";
            if (shelf.Entries.Count == 0) { list.Children.Add(ToolStyle.Hint("收纳架还是空的。可以拖入文件，或用上方按钮选择。")); return; }
            foreach (string path in shelf.Entries)
            {
                FileShelfMetadata info; metadata.TryGetValue(path, out info);
                var card = new StackPanel();
                string name = info == null ? Path.GetFileName(path) : info.Name;
                var title = IslandWindow.Text(String.IsNullOrEmpty(name) ? path : name, 13, "#D6EADF", true, false); title.TextWrapping = TextWrapping.Wrap; title.TextTrimming = TextTrimming.CharacterEllipsis; title.LineHeight = 18; title.MaxHeight = 36; title.ToolTip = path; card.Children.Add(title);
                var detail = ToolStyle.Hint(info == null ? "正在检查状态…" : info.Status); detail.Margin = new Thickness(0, 4, 0, 6); card.Children.Add(detail);
                var open = IslandWindow.Button("打开", () => OpenPath(path, false)); open.IsEnabled = info != null && info.CanOpen; open.ToolTip = open.IsEnabled ? "使用 Windows 默认应用打开" : "路径不可用，或此类型仅支持打开所在位置";
                var buttons = PanelActionLayout.Create(2, open,
                    IslandWindow.Button("所在位置", () => OpenPath(path, true)),
                    IslandWindow.Button("复制路径", () => { if (demo) return; try { Clipboard.SetText(path); status.Text = "已复制路径"; } catch { status.Text = "剪贴板被占用，请重试。"; } }),
                    IslandWindow.Button("移除", () => { shelf.Remove(path); metadata.Remove(path); Persist(); Rebuild(); }));
                card.Children.Add(buttons); list.Children.Add(ToolStyle.Card(card));
            }
        }

        private async void Refresh()
        {
            if (demo || disposed) return;
            int current = ++revision; string[] paths = shelf.Entries.ToArray();
            var results = await Task.WhenAll(paths.Select(InspectAsync).ToArray());
            if (disposed || current != revision) return;
            metadata.Clear(); foreach (var item in results) if (shelf.Entries.Contains(item.Path, StringComparer.OrdinalIgnoreCase)) metadata[item.Path] = item;
            Rebuild();
        }

        private async Task<FileShelfMetadata> InspectAsync(string path)
        {
            Task<FileShelfMetadata> inspection;
            if (!inspections.TryGetValue(path, out inspection))
            {
                // Reuse an outstanding read after a timeout instead of queuing more workers.
                inspection = Task.Run(() => FileShelfPaths.Inspect(path)); inspections[path] = inspection;
            }
            if (await Task.WhenAny(inspection, Task.Delay(4000)) != inspection)
                return new FileShelfMetadata { Path = path, Name = Path.GetFileName(path), Status = "检查超时 · 稍后点击刷新重试" };
            Task<FileShelfMetadata> current;
            if (inspections.TryGetValue(path, out current) && Object.ReferenceEquals(current, inspection)) inspections.Remove(path);
            return await inspection;
        }

        private async void OpenPath(string path, bool location)
        {
            if (demo || disposed) { status.Text = "演示模式不打开文件。"; return; }
            status.Text = "正在检查路径…";
            FileShelfMetadata info = await InspectAsync(path);
            if (disposed) return;
            try
            {
                if (location)
                {
                    string parent = info.IsDirectory && info.Available ? path : Path.GetDirectoryName(path);
                    if (String.IsNullOrEmpty(parent)) parent = path;
                    FileShelfMetadata folder = await InspectAsync(parent);
                    if (disposed) return;
                    if (!folder.Available || !folder.IsDirectory) { status.Text = "所在文件夹暂不可用。"; return; }
                    Process.Start(new ProcessStartInfo(parent) { UseShellExecute = true });
                    status.Text = "已打开所在文件夹";
                }
                else
                {
                    if (!info.Available || !info.CanOpen) { status.Text = info.Status; return; }
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); status.Text = "已交给 Windows 打开";
                }
            }
            catch { status.Text = "无法打开，请检查路径、权限和默认应用。"; }
        }

        private void Persist() { if (!TryFlush()) status.Text = "保存失败；修改仍保留在本次运行中，稍后可重试。"; }
        public bool TryFlush() { bool saved = shelf.TryFlush(); if (!saved) status.Text = "收纳引用保存失败，请检查本地目录权限。"; return saved; }
        public void Dispose() { disposed = true; revision++; TryFlush(); }
    }
}
