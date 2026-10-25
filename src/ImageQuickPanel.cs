using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexIsland
{
    internal sealed class ImageQuickPanel : StackPanel, IDisposable
    {
        private readonly bool demo;
        private readonly TextBox width = ToolStyle.Input(), height = ToolStyle.Input();
        private readonly CheckBox keepRatio = new CheckBox { Content = "保持比例", IsChecked = true, Foreground = Display.Brush("#C7E9D7"), Margin = new Thickness(0, 8, 0, 9) };
        private readonly ComboBox format = new ComboBox { Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 4, 0, 6) };
        private readonly Slider quality = new Slider { Minimum = 1, Maximum = 100, Value = 82, TickFrequency = 1, IsSnapToTickEnabled = true, Margin = new Thickness(0, 2, 0, 9) };
        private readonly Image preview = new Image { MaxHeight = 190, Stretch = Stretch.Uniform, Margin = new Thickness(0, 8, 0, 8) };
        private readonly Border previewFrame;
        private readonly TextBlock info = ToolStyle.Hint("尚未选择图片"), status = ToolStyle.Hint(""), qualityLabel = ToolStyle.Hint("JPG 质量：82（越低文件通常越小）");
        private readonly Button choose, save, cancel;
        private BitmapSource source;
        private string sourcePath;
        private bool disposed, busy, updating;
        private int revision;
        private CancellationTokenSource pending;

        public ImageQuickPanel(bool demoMode)
        {
            demo = demoMode; Children.Add(ToolStyle.Heading("图片快处理"));
            Children.Add(ToolStyle.Hint("拖入一张 JPG / PNG，调整尺寸和格式后另存为新文件。图片仅在本机处理。"));
            choose = PanelActionLayout.Caption(IslandWindow.Button("选择图片", Choose)); choose.Margin = new Thickness(0, 4, 0, 0); Children.Add(choose); Children.Add(info);
            previewFrame = new Border { Child = preview, Background = Display.Brush("#17201D"), CornerRadius = new CornerRadius(8), Visibility = Visibility.Collapsed }; Children.Add(previewFrame);
            Children.Add(ToolStyle.Hint("输出宽度 × 高度（像素）"));
            var dimensions = new Grid(); dimensions.ColumnDefinitions.Add(new ColumnDefinition()); dimensions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) }); dimensions.ColumnDefinitions.Add(new ColumnDefinition());
            dimensions.Children.Add(width); var separator = ToolStyle.Hint("×"); separator.HorizontalAlignment = HorizontalAlignment.Center; Grid.SetColumn(separator, 1); dimensions.Children.Add(separator); Grid.SetColumn(height, 2); dimensions.Children.Add(height); Children.Add(dimensions); Children.Add(keepRatio);
            width.MaxLength = height.MaxLength = 5; width.ToolTip = "输出宽度"; height.ToolTip = "输出高度";
            width.TextChanged += (s, e) => MatchRatio(true); height.TextChanged += (s, e) => MatchRatio(false);
            keepRatio.Checked += (s, e) => MatchRatio(true);
            format.Items.Add("PNG · 无损 / 保留透明"); format.Items.Add("JPG · 可压缩 / 透明铺白"); format.SelectedIndex = 0;
            format.SelectionChanged += (s, e) => UpdateControls(); Children.Add(format); Children.Add(qualityLabel); Children.Add(quality);
            quality.ValueChanged += (s, e) => qualityLabel.Text = "JPG 质量：" + ((int)quality.Value).ToString(CultureInfo.InvariantCulture) + "（越低文件通常越小）";
            save = IslandWindow.Button("另存新图片", async () => await SaveDialog()); cancel = IslandWindow.Button("取消处理", Cancel);
            Children.Add(PanelActionLayout.Create(2, save, cancel)); Children.Add(status);
            Children.Add(ToolStyle.Hint("PNG 为无损编码，缩小分辨率可减少体积；JPG 可调质量。最多 64 MB / 2400 万像素，不覆盖原图。"));
            AllowDrop = true; PreviewDragOver += (s, e) => { e.Effects = !demo && !disposed && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
            PreviewDrop += (s, e) => { e.Handled = true; if (demo || disposed) return; try { var paths = e.Data.GetData(DataFormats.FileDrop) as string[]; if (paths == null || paths.Length != 1) status.Text = "请每次拖入一张图片。"; else LoadFile(paths[0]); } catch { status.Text = "无法读取拖入的图片。"; } };
            UpdateControls();
        }
        private void Choose()
        {
            if (demo) { status.Text = "演示模式不读取本机图片。"; return; }
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = UiLocalization.Localize("选择图片"), Filter = UiLocalization.FileFilter("JPG / PNG 图片|*.jpg;*.jpeg;*.png"), CheckFileExists = true };
            if (dialog.ShowDialog(Window.GetWindow(this)) == true) LoadFile(dialog.FileName);
        }
        public async void LoadFile(string path) { await LoadFileAsync(path); }
        internal async Task LoadFileAsync(string path)
        {
            if (disposed || demo) return;
            Cancel(); int current = ++revision; var work = new CancellationTokenSource(); pending = work; busy = true;
            source = null; sourcePath = null; preview.Source = null; info.Text = "正在读取图片…"; status.Text = ""; UpdateControls();
            try
            {
                BitmapSource loaded = await Task.Run(() => ImageQuickTools.Load(path, work.Token));
                if (disposed || current != revision) return;
                work.Token.ThrowIfCancellationRequested(); source = loaded; sourcePath = Path.GetFullPath(path); preview.Source = loaded;
                updating = true; width.Text = loaded.PixelWidth.ToString(CultureInfo.InvariantCulture); height.Text = loaded.PixelHeight.ToString(CultureInfo.InvariantCulture); updating = false;
                format.SelectedIndex = Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
                string fileSize = ""; try { fileSize = " · " + (new FileInfo(path).Length / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB"; } catch (IOException) { } catch (UnauthorizedAccessException) { }
                info.Text = Path.GetFileName(path) + " · " + loaded.PixelWidth + " × " + loaded.PixelHeight + fileSize;
                status.Text = "图片已就绪。调整参数后另存新文件。";
            }
            catch (OperationCanceledException) { if (!disposed && current == revision) { info.Text = "读取已取消"; status.Text = "已取消处理。"; } }
            catch (Exception error) { if (!disposed && current == revision) { info.Text = "未加载图片"; status.Text = FriendlyError(error); } }
            finally { work.Dispose(); if (current == revision) { pending = null; busy = false; if (!disposed) UpdateControls(); } }
        }
        private void MatchRatio(bool fromWidth)
        {
            if (updating || source == null || keepRatio.IsChecked != true) return; int value;
            if (!Int32.TryParse(fromWidth ? width.Text : height.Text, NumberStyles.None, CultureInfo.InvariantCulture, out value) || value < 1 || value > ImageQuickTools.MaximumDimension) return;
            double result = fromWidth ? value * (double)source.PixelHeight / source.PixelWidth : value * (double)source.PixelWidth / source.PixelHeight;
            updating = true; (fromWidth ? height : width).Text = Math.Max(1, (int)Math.Round(result)).ToString(CultureInfo.InvariantCulture); updating = false;
        }
        private async Task SaveDialog()
        {
            if (disposed || busy || source == null || demo) return;
            bool jpg = format.SelectedIndex == 1; string extension = jpg ? ".jpg" : ".png";
            var dialog = new Microsoft.Win32.SaveFileDialog { Title = UiLocalization.Localize("另存处理后的图片"), Filter = UiLocalization.FileFilter(jpg ? "JPG 图片|*.jpg" : "PNG 图片|*.png"), DefaultExt = extension, AddExtension = true, OverwritePrompt = true, FileName = Path.GetFileNameWithoutExtension(sourcePath) + "-处理后" + extension };
            if (dialog.ShowDialog(Window.GetWindow(this)) == true) await SaveToAsync(dialog.FileName);
        }
        internal async Task SaveToAsync(string path)
        {
            if (disposed || busy || source == null || demo) return;
            int w, h; if (!Int32.TryParse(width.Text, NumberStyles.None, CultureInfo.InvariantCulture, out w) || !Int32.TryParse(height.Text, NumberStyles.None, CultureInfo.InvariantCulture, out h)) { status.Text = "宽度和高度请输入正整数。"; return; }
            try { ImageQuickTools.ValidateSize(w, h); } catch (ArgumentException error) { status.Text = error.Message; return; }
            var selected = source; string original = sourcePath, selectedFormat = format.SelectedIndex == 1 ? "JPG" : "PNG"; int selectedQuality = (int)quality.Value;
            int current = ++revision; var work = new CancellationTokenSource(); pending = work; busy = true; status.Text = "正在处理并保存…"; UpdateControls();
            try
            {
                long bytes = await Task.Run(() => ImageQuickTools.Save(selected, original, path, w, h, selectedFormat, selectedQuality, work.Token));
                if (!disposed && current == revision) status.Text = "已另存 " + Path.GetFileName(path) + " · " + w + " × " + h + " · " + (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB";
            }
            catch (OperationCanceledException) { if (!disposed && current == revision) status.Text = "已取消处理。"; }
            catch (Exception error) { if (!disposed && current == revision) status.Text = FriendlyError(error); }
            finally { work.Dispose(); if (current == revision) { pending = null; busy = false; if (!disposed) UpdateControls(); } }
        }
        private static string FriendlyError(Exception error) { return error is ArgumentException ? error.Message : error is UnauthorizedAccessException ? "无法访问图片或保存位置，请选择其他路径。" : error is IOException ? "图片可能已移动、被占用或保存位置不可用。" : error is OutOfMemoryException ? "图片过大，暂时无法处理，请尝试更小的图片。" : "图片处理失败，请确认文件是有效的 JPG / PNG 图片。"; }
        private void Cancel() { if (pending != null) { pending.Cancel(); status.Text = "正在取消…"; } }
        private void UpdateControls()
        {
            if (save == null) return;
            previewFrame.Visibility = source == null ? Visibility.Collapsed : Visibility.Visible;
            choose.IsEnabled = !busy; save.IsEnabled = !busy && source != null && !demo; cancel.IsEnabled = busy;
            width.IsEnabled = height.IsEnabled = keepRatio.IsEnabled = format.IsEnabled = !busy && source != null;
            quality.IsEnabled = !busy && source != null && format.SelectedIndex == 1; qualityLabel.Opacity = format.SelectedIndex == 1 ? 1 : 0.5;
        }
        public void Dispose() { if (disposed) return; disposed = true; revision++; Cancel(); source = null; preview.Source = null; }
    }
}
