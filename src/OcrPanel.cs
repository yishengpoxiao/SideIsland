using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexIsland
{
    internal sealed class OcrPanel : StackPanel, IDisposable
    {
        private readonly bool demo;
        private readonly IOcrAdapter adapter;
        private readonly Func<Int32Rect, CancellationToken, BitmapSource> capture;
        private readonly Func<CancellationToken, Task<Int32Rect?>> select;
        private readonly OcrClipboardReader clipboard = new OcrClipboardReader();
        private readonly TextBox result = ToolStyle.Input();
        private readonly ComboBox languages = new ComboBox { Margin = new Thickness(0, 5, 0, 8), Padding = new Thickness(7) };
        private readonly Image preview = new Image { MaxHeight = 160, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 0, 6), Visibility = Visibility.Collapsed };
        private readonly TextBlock status = ToolStyle.Hint("选择图片或框选屏幕开始识别。"), languageStatus = ToolStyle.Hint("");
        private readonly Button captureButton, fileButton, pasteButton, retryButton, cancelButton, copyButton, compareButton;
        private BitmapSource source;
        private CancellationTokenSource pending;
        private bool disposed, busy, loadedLanguages;
        private int revision;
        internal bool IsCapturing { get; private set; }
        internal event Action<bool> CaptureStateChanged;
        internal event Action<string> CompareRequested;

        internal OcrPanel(bool demoMode) : this(demoMode, new WindowsOcrAdapter(), ScreenPinTools.Capture, ScreenPinSelection.Select) { }
        internal OcrPanel(bool demoMode, IOcrAdapter engine, Func<Int32Rect, CancellationToken, BitmapSource> captureImage, Func<CancellationToken, Task<Int32Rect?>> selectRegion)
        {
            demo = demoMode; adapter = engine; capture = captureImage; select = selectRegion;
            Children.Add(ToolStyle.Heading("截图提字 OCR"));
            Children.Add(ToolStyle.Hint("框选屏幕、选择 JPG / PNG 或读取剪贴板图片，在本机识别文字。结果可编辑、复制或送入文字对照。"));
            captureButton = IslandWindow.Button("框选屏幕提字", async () => await BeginCaptureAsync());
            fileButton = IslandWindow.Button("选择图片", ChooseFile);
            pasteButton = IslandWindow.Button("读取剪贴板图片", async () => await ReadClipboardAsync()); Children.Add(PanelActionLayout.Create(3, captureButton, fileButton, pasteButton));
            languages.Items.Add(new OcrLanguageChoice { Tag = "", Name = "自动（Windows 首选语言）" }); languages.SelectedIndex = 0; UiLocalization.OcrLanguageCombo(languages); Children.Add(languages); Children.Add(languageStatus); Children.Add(preview);
            retryButton = IslandWindow.Button("重新识别", async () => await RecognizeCurrentAsync()); cancelButton = IslandWindow.Button("取消识别", Cancel);
            var second = PanelActionLayout.Create(2, retryButton, cancelButton); second.Margin = new Thickness(0, 0, 0, 6); Children.Add(second);
            result.AcceptsReturn = true; result.AcceptsTab = true; result.TextWrapping = TextWrapping.Wrap; result.Height = 200; result.MaxLength = OcrImages.MaximumResultCharacters;
            result.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; Children.Add(result); UiLocalization.Protect(result);
            copyButton = IslandWindow.Button("复制文字", Copy);
            compareButton = IslandWindow.Button("送入文字对照", () => { var handler = CompareRequested; if (handler != null && result.Text.Length > 0) handler(result.Text); });
            var outputs = PanelActionLayout.Create(2, copyButton, compareButton); outputs.Margin = new Thickness(0, 8, 0, 0); Children.Add(outputs); Children.Add(status);
            Children.Add(ToolStyle.Hint("OCR 使用 Windows 已安装的语言包，不联网。较大图片会缩小至最长边 4,096 像素；识别准确度受字号、清晰度及语言影响。"));
            result.TextChanged += (s, e) => UpdateControls(); Loaded += async (s, e) => await LoadLanguagesAsync();
            if (demo) { result.Text = "Side Island OCR\nError: port 8080 is already in use.\n配置文件已更新。"; status.Text = "演示模式不读取真实屏幕、图片或剪贴板。"; }
            UpdateControls();
        }

        private async Task LoadLanguagesAsync()
        {
            if (loadedLanguages || disposed || demo) return; loadedLanguages = true;
            try
            {
                OcrLanguageChoice[] choices = await Task.Run(() => adapter.Languages());
                if (disposed) return; foreach (var choice in choices) languages.Items.Add(choice);
                languageStatus.Text = choices.Length == 0 ? "未安装 OCR 语言包，请在 Windows 设置的语言选项中添加文字识别。" : "自动使用首选语言；也可指定已安装的 OCR 语言。";
            }
            catch { if (!disposed) languageStatus.Text = "Windows OCR 暂不可用，请使用 Windows 10 / 11 并检查识别语言包。"; }
        }

        private void ChooseFile()
        {
            if (demo || disposed || busy) return;
            var picker = new Microsoft.Win32.OpenFileDialog { Title = UiLocalization.Localize("选择提字图片"), Filter = UiLocalization.FileFilter("图片|*.png;*.jpg;*.jpeg"), CheckFileExists = true };
            if (picker.ShowDialog(Window.GetWindow(this)) == true) AcceptFile(picker.FileName);
        }

        internal async void AcceptFile(string path) { if (busy) { status.Text = "识别正在进行，请先取消后再选择新图片。"; return; } await LoadFileAsync(path); }
        internal async Task LoadFileAsync(string path)
        {
            if (demo || disposed || busy) return;
            var work = BeginWork("正在读取图片…");
            try
            {
                BitmapSource image = await Task.Run(() => ImageQuickTools.Load(path, work.Token));
                work.Token.ThrowIfCancellationRequested(); if (disposed) return; SetSource(image); await RecognizeAsync(work);
            }
            catch (OperationCanceledException) { if (!disposed) status.Text = "已取消识别。"; }
            catch (Exception error) { if (!disposed) status.Text = FriendlyError(error); }
            finally { EndWork(work); }
        }

        internal async Task BeginCaptureAsync()
        {
            if (demo || disposed || busy) return;
            var work = BeginWork("请拖动框选需要识别的区域，Esc 取消。");
            try
            {
                IsCapturing = true; RaiseCapture(true); await Task.Delay(100, work.Token);
                Int32Rect? area = await select(work.Token); if (!area.HasValue) { status.Text = "已取消识别。"; return; }
                ScreenPinTools.ValidateCapture(area.Value); await Task.Delay(100, work.Token);
                BitmapSource image = await Task.Run(() => capture(area.Value, work.Token)); work.Token.ThrowIfCancellationRequested();
                if (disposed) return; SetSource(image);
                IsCapturing = false; RaiseCapture(false); await RecognizeAsync(work);
            }
            catch (OperationCanceledException) { if (!disposed) status.Text = "已取消识别。"; }
            catch (Exception error) { if (!disposed) status.Text = FriendlyError(error); }
            finally { if (IsCapturing) { IsCapturing = false; RaiseCapture(false); } EndWork(work); }
        }

        private async Task ReadClipboardAsync()
        {
            if (demo || disposed || busy) return; var work = BeginWork("正在读取剪贴板图片…");
            try
            {
                BitmapSource image = await clipboard.ReadAsync(work.Token); if (disposed) return;
                if (image == null) { status.Text = "剪贴板中没有可读取的图片。"; return; }
                SetSource(image); await RecognizeAsync(work);
            }
            catch (OperationCanceledException) { if (!disposed) status.Text = "已取消识别。"; }
            catch (Exception error) { if (!disposed) status.Text = FriendlyError(error); }
            finally { EndWork(work); }
        }

        internal async Task RecognizeCurrentAsync()
        {
            if (demo || disposed || busy || source == null) return; var work = BeginWork("正在本机识别文字…");
            try { await RecognizeAsync(work); }
            catch (OperationCanceledException) { if (!disposed) status.Text = "已取消识别。"; }
            catch (Exception error) { if (!disposed) status.Text = FriendlyError(error); }
            finally { EndWork(work); }
        }
        private async Task RecognizeAsync(CancellationTokenSource work)
        {
            var choice = languages.SelectedItem as OcrLanguageChoice; string tag = choice == null ? "" : choice.Tag;
            BitmapSource selected = source; int version = revision; status.Text = "正在本机识别文字…";
            string text = await OcrRecognition.RunAsync(adapter, selected, tag, work.Token);
            work.Token.ThrowIfCancellationRequested(); if (disposed || version != revision) return;
            if ((text ?? "").Length > OcrImages.MaximumResultCharacters) throw new ArgumentException("识别文字超过 120,000 字符，请缩小区域后重试。");
            result.Text = text ?? ""; status.Text = result.Text.Length == 0 ? "未识别到文字，请尝试放大文字或切换识别语言。" : "识别完成，可编辑结果后复制或对照。";
        }
        private void SetSource(BitmapSource image)
        {
            if (image == null || !image.IsFrozen) throw new ArgumentException("图片尚未加载完成。");
            source = image; preview.Source = source; preview.Visibility = Visibility.Visible; result.Clear();
        }
        private CancellationTokenSource BeginWork(string message) { busy = true; revision++; pending = new CancellationTokenSource(); status.Text = message; UpdateControls(); return pending; }
        private void EndWork(CancellationTokenSource work) { if (ReferenceEquals(pending, work)) { pending = null; busy = false; } work.Dispose(); if (!disposed) UpdateControls(); }
        private void Cancel() { if (pending != null) { pending.Cancel(); status.Text = "正在取消…"; } }
        private void RaiseCapture(bool value) { var handler = CaptureStateChanged; if (handler != null) handler(value); }
        private void Copy() { if (demo || disposed || result.Text.Length == 0) return; try { Clipboard.SetText(result.Text); status.Text = "文字已复制。"; } catch { status.Text = "剪贴板正忙，请重试。"; } }
        private void UpdateControls()
        {
            captureButton.IsEnabled = fileButton.IsEnabled = pasteButton.IsEnabled = !busy && !disposed;
            retryButton.IsEnabled = !busy && !disposed && source != null; cancelButton.IsEnabled = busy && !disposed;
            languages.IsEnabled = !busy && !disposed; copyButton.IsEnabled = compareButton.IsEnabled = !busy && !disposed && result.Text.Length > 0;
        }
        private static string FriendlyError(Exception error)
        {
            if (error is ArgumentException || error is NotSupportedException || error is TimeoutException) return error.Message;
            return "识别未完成，请检查图片、Windows OCR 语言包和访问权限后重试。";
        }
        public void Dispose() { if (disposed) return; disposed = true; revision++; if (pending != null) pending.Cancel(); source = null; preview.Source = null; result.Clear(); }
    }
}
