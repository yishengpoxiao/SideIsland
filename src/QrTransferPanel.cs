using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Net.Codecrete.QrCodeGenerator;

namespace CodexIsland
{
    internal static class QrTransferTools
    {
        // QR version 40 byte mode, including a UTF-8 ECI 26 segment. Boundaries are verified against the encoder.
        internal const int MaximumUtf8Bytes = 2952;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        internal static int MaximumBytes(QrCode.Ecc correction)
        {
            if (correction == null) throw new ArgumentNullException("correction");
            return new[] { MaximumUtf8Bytes, 2330, 1662, 1272 }[correction.Ordinal];
        }
        internal static int CountBytes(string text)
        {
            try { return StrictUtf8.GetByteCount(text ?? ""); }
            catch (EncoderFallbackException) { throw new ArgumentException("文字中包含不完整的 Unicode 字符。"); }
        }
        internal static void Validate(string text, QrCode.Ecc correction)
        {
            if (String.IsNullOrWhiteSpace(text)) throw new ArgumentException("请输入网页链接或文字。");
            int maximum = MaximumBytes(correction);
            // All valid UTF-8 text uses at least as many bytes as UTF-16 code units; reject very large pastes cheaply.
            if (text.Length > maximum || CountBytes(text) > maximum)
                throw new ArgumentException("超过单张二维码容量：当前纠错级别最多支持 " + maximum.ToString("N0") + " 个 UTF-8 字节。" + (correction == QrCode.Ecc.Low ? "请缩短内容。" : "可切换 L 级容纳更多文字。"));
        }
        internal static QrCode Encode(string text) { return Encode(text, QrCode.Ecc.Low); }
        internal static QrCode Encode(string text, QrCode.Ecc correction)
        {
            Validate(text, correction);
            byte[] data = StrictUtf8.GetBytes(text);
            return QrCode.EncodeSegments(new List<QrSegment> { QrSegment.MakeEci(26), QrSegment.MakeBytes(data) }, correction);
        }
        internal static BitmapSource Render(QrCode code)
        {
            const int border = 4, scale = 6;
            int width = (code.Size + border * 2) * scale;
            var pixels = new byte[width * width];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = 255;
            for (int y = 0; y < code.Size; y++) for (int x = 0; x < code.Size; x++)
                if (code.GetModule(x, y)) for (int yy = 0; yy < scale; yy++) for (int xx = 0; xx < scale; xx++)
                    pixels[((y + border) * scale + yy) * width + (x + border) * scale + xx] = 0;
            var bitmap = BitmapSource.Create(width, width, 96, 96, PixelFormats.Gray8, null, pixels, width);
            bitmap.Freeze(); return bitmap;
        }
        internal static void Save(BitmapSource bitmap, string path)
        {
            if (bitmap == null) throw new ArgumentException("请先生成二维码。");
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None)) encoder.Save(stream);
        }
    }

    internal sealed class QrTransferPanel : StackPanel, IDisposable
    {
        internal void AcceptExternalText(string text) { if (!disposed) input.Text = text ?? ""; }
        private readonly bool demo;
        private readonly TextBox input = ToolStyle.Input();
        private readonly ComboBox correction = new ComboBox { Margin = new Thickness(0, 2, 0, 4), Padding = new Thickness(6) };
        private readonly Image image = new Image { MaxWidth = 260, MaxHeight = 260, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center };
        private readonly Border frame;
        private readonly TextBlock status = ToolStyle.Hint("输入后自动生成二维码。"), counter = ToolStyle.Hint("");
        private readonly DispatcherTimer debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        private readonly Button copy, save;
        private BitmapSource current;
        private CancellationTokenSource workCancellation;
        private int revision;
        private bool disposed;

        internal QrTransferPanel(bool demoMode)
        {
            demo = demoMode;
            Children.Add(ToolStyle.Heading("二维码传话"));
            Children.Add(ToolStyle.Hint("文字仅在本机生成单张二维码，不上传。容量受二维码标准限制；L 可容纳更多，较高纠错级别更耐遮挡。"));
            input.AcceptsReturn = true; input.TextWrapping = TextWrapping.Wrap; input.VerticalContentAlignment = VerticalAlignment.Top; input.Height = 70; input.MaxLength = 0;
            input.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; Children.Add(input);
            correction.Items.Add("L · 最大容量（2,952 字节）");
            correction.Items.Add("M · 均衡纠错（2,330 字节）");
            correction.Items.Add("Q · 较高纠错（1,662 字节）");
            correction.Items.Add("H · 最高纠错（1,272 字节）");
            correction.SelectedIndex = 0; Children.Add(correction); Children.Add(counter);
            var generate = IslandWindow.Button("生成二维码", Generate);
            copy = IslandWindow.Button("复制图片", Copy); save = IslandWindow.Button("保存 PNG", Save); Children.Add(PanelActionLayout.Create(3, generate, copy, save));
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
            // Keep the real QR pixels and quiet zone white, but do not show a
            // blank white square before an image has actually been generated.
            frame = new Border { Child = image, Background = Brushes.White, Padding = new Thickness(4), MaxWidth = 268, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 6, 0, 6), Visibility = Visibility.Collapsed };
            Children.Add(frame); Children.Add(status);
            Children.Add(ToolStyle.Hint("密集二维码建议保存原图后放大扫码。字节数包含中文与表情的 UTF-8 编码，不是字数。"));
            input.TextChanged += (s, e) => InputChanged(); correction.SelectionChanged += (s, e) => InputChanged();
            debounce.Tick += (s, e) => Generate();
            copy.IsEnabled = save.IsEnabled = false; UpdateCounter();
            if (demo) input.Text = "https://example.com/你好";
        }
        private QrCode.Ecc SelectedCorrection
        {
            get { return new[] { QrCode.Ecc.Low, QrCode.Ecc.Medium, QrCode.Ecc.Quartile, QrCode.Ecc.High }[Math.Max(0, Math.Min(3, correction.SelectedIndex))]; }
        }
        private void CancelWork()
        {
            var previous = workCancellation; workCancellation = null;
            if (previous != null) previous.Cancel();
        }
        private void InputChanged()
        {
            revision++; CancelWork(); current = null; image.Source = null; frame.Visibility = Visibility.Collapsed; copy.IsEnabled = save.IsEnabled = false;
            UpdateCounter(); debounce.Stop(); if (!disposed) debounce.Start();
        }
        private void UpdateCounter()
        {
            try { counter.Text = QrTransferTools.CountBytes(input.Text).ToString("N0") + " / " + QrTransferTools.MaximumBytes(SelectedCorrection).ToString("N0") + " UTF-8 字节"; }
            catch (ArgumentException error) { counter.Text = error.Message; }
        }
        private async void Generate()
        {
            debounce.Stop(); if (disposed) return;
            CancelWork(); var cancellation = new CancellationTokenSource(); workCancellation = cancellation;
            CancellationToken token = cancellation.Token; string text = input.Text; var level = SelectedCorrection; int version = ++revision;
            try
            {
                QrTransferTools.Validate(text, level); status.Text = "正在生成…";
                BitmapSource result = await Task.Run(() => {
                    token.ThrowIfCancellationRequested(); QrCode code = QrTransferTools.Encode(text, level);
                    token.ThrowIfCancellationRequested(); BitmapSource bitmap = QrTransferTools.Render(code);
                    token.ThrowIfCancellationRequested(); return bitmap;
                }, token);
                if (disposed || version != revision || token.IsCancellationRequested) return;
                current = result; image.Source = result; frame.Visibility = Visibility.Visible; copy.IsEnabled = save.IsEnabled = true;
                status.Text = "已生成 · 手机相机或微信扫一扫即可获取内容";
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (disposed || version != revision) return;
                current = null; image.Source = null; frame.Visibility = Visibility.Collapsed; copy.IsEnabled = save.IsEnabled = false;
                status.Text = error is ArgumentException ? error.Message : "二维码暂时无法生成，请重试。";
            }
            finally
            {
                if (Object.ReferenceEquals(workCancellation, cancellation)) workCancellation = null;
                cancellation.Dispose();
            }
        }
        private void Copy()
        {
            if (current == null) return;
            if (demo) { status.Text = "演示模式不修改剪贴板。"; return; }
            try { Clipboard.SetImage(current); status.Text = "二维码图片已复制"; }
            catch { status.Text = "剪贴板正忙，请重试。"; }
        }
        private void Save()
        {
            if (current == null) return;
            if (demo) { status.Text = "演示模式不写入文件。"; return; }
            var dialog = new Microsoft.Win32.SaveFileDialog { Title = UiLocalization.Localize("保存 PNG"), Filter = UiLocalization.FileFilter("PNG 图片|*.png"), FileName = "二维码.png", DefaultExt = ".png", AddExtension = true };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            try { QrTransferTools.Save(current, dialog.FileName); status.Text = "已保存：" + dialog.FileName; }
            catch (Exception error) { status.Text = "保存失败：" + error.Message; }
        }
        public void Dispose() { if (disposed) return; disposed = true; revision++; debounce.Stop(); CancelWork(); current = null; image.Source = null; frame.Visibility = Visibility.Collapsed; copy.IsEnabled = save.IsEnabled = false; }
    }
}
