using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Foundation;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace CodexIsland
{
    internal sealed class OcrLanguageChoice
    {
        internal string Tag, Name;
        public override string ToString() { return String.IsNullOrEmpty(Tag) ? UiLocalization.Localize(Name) : Name; }
    }

    internal interface IOcrAdapter
    {
        OcrLanguageChoice[] Languages();
        Task<string> RecognizeAsync(BitmapSource image, string language, CancellationToken cancellation);
    }

    internal static class OcrImages
    {
        internal const int MaximumDimension = 4096;
        internal const int MaximumResultCharacters = 120000;

        internal static BitmapSource Prepare(BitmapSource source, int maximumDimension)
        {
            if (source == null || !source.IsFrozen) throw new ArgumentException("图片尚未加载完成。");
            if (source.PixelWidth < 2 || source.PixelHeight < 2 || (long)source.PixelWidth * source.PixelHeight > ScreenPinTools.MaximumCapturePixels)
                throw new ArgumentException("请选择有效图片，最多支持 3200 万像素。");
            int limit = Math.Max(2, Math.Min(MaximumDimension, maximumDimension));
            double scale = Math.Min(1, limit / (double)Math.Max(source.PixelWidth, source.PixelHeight));
            BitmapSource resized = scale < 1 ? new TransformedBitmap(source, new ScaleTransform(scale, scale)) : source;
            var converted = new FormatConvertedBitmap(resized, PixelFormats.Bgra32, null, 0);
            // Detach from the decoder so a frozen WPF bitmap safely crosses workers.
            int stride = checked(converted.PixelWidth * 4);
            byte[] pixels = new byte[checked(stride * converted.PixelHeight)]; converted.CopyPixels(pixels, stride, 0);
            var result = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            result.Freeze(); return result;
        }

        internal static string BoundResult(IEnumerable<string> lines)
        {
            var text = new StringBuilder(); bool first = true;
            foreach (string line in lines ?? Enumerable.Empty<string>())
            {
                int separator = first ? 0 : Environment.NewLine.Length;
                if ((long)text.Length + separator + (line ?? "").Length > MaximumResultCharacters) throw new ArgumentException("识别文字超过 120,000 字符，请缩小区域后重试。");
                if (separator != 0) text.Append(Environment.NewLine); text.Append(line ?? ""); first = false;
            }
            return text.ToString();
        }
    }

    internal static class OcrRecognition
    {
        internal static async Task<string> RunAsync(IOcrAdapter adapter, BitmapSource image, string language, CancellationToken cancellation)
        {
            if (adapter == null || image == null || !image.IsFrozen) throw new ArgumentException("图片尚未加载完成。");
            cancellation.ThrowIfCancellationRequested();
            string text = await Task.Run(() => adapter.RecognizeAsync(image, language ?? "", cancellation), cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            if ((text ?? "").Length > OcrImages.MaximumResultCharacters) throw new ArgumentException("识别文字超过 120,000 字符，请缩小区域后重试。");
            return text ?? "";
        }
    }

    // Windows' installed OCR language models run locally. No image is uploaded.
    internal sealed class WindowsOcrAdapter : IOcrAdapter
    {
        public OcrLanguageChoice[] Languages()
        {
            return OcrEngine.AvailableRecognizerLanguages.Select(item => new OcrLanguageChoice { Tag = item.LanguageTag, Name = item.DisplayName + " (" + item.LanguageTag + ")" }).ToArray();
        }

        public async Task<string> RecognizeAsync(BitmapSource source, string language, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            OcrEngine engine = String.IsNullOrEmpty(language) ? OcrEngine.TryCreateFromUserProfileLanguages() : OcrEngine.TryCreateFromLanguage(new Language(language));
            if (engine == null) throw new NotSupportedException("此识别语言不可用，请在 Windows 设置中安装对应的 OCR 语言包，或选择其他语言。");
            BitmapSource image = OcrImages.Prepare(source, (int)OcrEngine.MaxImageDimension);
            cancellation.ThrowIfCancellationRequested();
            int stride = checked(image.PixelWidth * 4); byte[] bytes = new byte[checked(stride * image.PixelHeight)]; image.CopyPixels(bytes, stride, 0);
            using (var writer = new DataWriter())
            {
                writer.WriteBytes(bytes);
                using (var bitmap = SoftwareBitmap.CreateCopyFromBuffer(writer.DetachBuffer(), BitmapPixelFormat.Bgra8, image.PixelWidth, image.PixelHeight, BitmapAlphaMode.Straight))
                {
                    OcrResult result = await Await(engine.RecognizeAsync(bitmap), cancellation).ConfigureAwait(false);
                    cancellation.ThrowIfCancellationRequested();
                    return OcrImages.BoundResult(result.Lines.Select(line => line.Text));
                }
            }
        }

        private static async Task<T> Await<T>(IAsyncOperation<T> operation, CancellationToken cancellation)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                timeout.CancelAfter(20000);
                try
                {
                    while (operation.Status == AsyncStatus.Started) await Task.Delay(45, timeout.Token).ConfigureAwait(false);
                    timeout.Token.ThrowIfCancellationRequested();
                    if (operation.Status == AsyncStatus.Canceled) throw new OperationCanceledException();
                    return operation.GetResults();
                }
                catch (OperationCanceledException)
                {
                    if (cancellation.IsCancellationRequested) throw;
                    throw new TimeoutException("本机文字识别超时，请缩小图片或区域后重试。");
                }
                finally
                {
                    try
                    {
                        if (operation.Status == AsyncStatus.Started) { operation.Completed = (finished, state) => { try { finished.Close(); } catch { } }; operation.Cancel(); }
                        else operation.Close();
                    }
                    catch { }
                }
            }
        }
    }
}
