using System;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;

namespace CodexIsland
{
    internal static class ImageQuickTools
    {
        internal const int MaximumDimension = 16000;
        internal const long MaximumPixels = 24000000;
        internal const long MaximumFileBytes = 64L * 1024 * 1024;

        internal static void ValidateSize(int width, int height)
        {
            if (width < 1 || height < 1 || width > MaximumDimension || height > MaximumDimension || (long)width * height > MaximumPixels)
                throw new ArgumentException("图片尺寸需为 1–16000 像素，总像素不超过 2400 万。");
        }

        public static BitmapSource Load(string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension != ".jpg" && extension != ".jpeg" && extension != ".png") throw new ArgumentException("请选择 JPG 或 PNG 图片。");
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > MaximumFileBytes) throw new ArgumentException("图片文件不能超过 64 MB。");
                // Read dimensions before requesting a full, detached decode.
                var header = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                if (!(header is PngBitmapDecoder) && !(header is JpegBitmapDecoder)) throw new ArgumentException("文件内容不是 JPG 或 PNG 图片。");
                ValidateSize(header.Frames[0].PixelWidth, header.Frames[0].PixelHeight);
                token.ThrowIfCancellationRequested(); stream.Position = 0;
                var decoded = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                decoded.Freeze(); token.ThrowIfCancellationRequested(); return decoded;
            }
        }

        public static long Save(BitmapSource source, string originalPath, string outputPath, int width, int height, string format, int quality, CancellationToken token)
        {
            if (source == null || !source.IsFrozen) throw new ArgumentException("图片尚未加载完成。");
            ValidateSize(width, height); token.ThrowIfCancellationRequested();
            string target = Path.GetFullPath(outputPath), original = String.IsNullOrEmpty(originalPath) ? null : Path.GetFullPath(originalPath);
            if (original != null && String.Equals(target, original, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("请另存为新文件，不能覆盖原图。");
            bool jpeg = String.Equals(format, "JPG", StringComparison.OrdinalIgnoreCase);
            if (!jpeg && !String.Equals(format, "PNG", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("仅支持 JPG 和 PNG 格式。");
            string extension = Path.GetExtension(target).ToLowerInvariant();
            if (jpeg ? extension != ".jpg" && extension != ".jpeg" : extension != ".png") throw new ArgumentException("文件扩展名与所选输出格式不一致。");
            if (quality < 1 || quality > 100) throw new ArgumentException("JPG 质量需为 1–100。");
            string temporary = Path.Combine(Path.GetDirectoryName(target), ".side-island-image-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                // This conversion is local to the worker. Freezing it would walk
                // a BitmapFrame's decoder, which can belong to its load thread.
                var converted = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
                int stride = checked(source.PixelWidth * 4); var pixels = new byte[stride];
                token.ThrowIfCancellationRequested();
                using (var input = new System.Drawing.Bitmap(source.PixelWidth, source.PixelHeight, DrawingPixelFormat.Format32bppPArgb))
                using (var resized = new System.Drawing.Bitmap(width, height, jpeg ? DrawingPixelFormat.Format24bppRgb : DrawingPixelFormat.Format32bppArgb))
                {
                    var bits = input.LockBits(new System.Drawing.Rectangle(0, 0, input.Width, input.Height), ImageLockMode.WriteOnly, DrawingPixelFormat.Format32bppPArgb);
                    try { for (int row = 0; row < input.Height; row++) { if ((row & 63) == 0) token.ThrowIfCancellationRequested(); converted.CopyPixels(new System.Windows.Int32Rect(0, row, input.Width, 1), pixels, stride, 0); Marshal.Copy(pixels, 0, IntPtr.Add(bits.Scan0, row * bits.Stride), stride); } }
                    finally { input.UnlockBits(bits); }
                    pixels = null;
                    using (var graphics = System.Drawing.Graphics.FromImage(resized))
                    {
                        graphics.CompositingMode = CompositingMode.SourceCopy;
                        graphics.Clear(jpeg ? System.Drawing.Color.White : System.Drawing.Color.Transparent);
                        if (jpeg) graphics.CompositingMode = CompositingMode.SourceOver;
                        graphics.CompositingQuality = CompositingQuality.HighQuality; graphics.InterpolationMode = InterpolationMode.HighQualityBicubic; graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        using (var attributes = new ImageAttributes())
                        {
                            attributes.SetWrapMode(WrapMode.TileFlipXY);
                            graphics.DrawImage(input, new System.Drawing.Rectangle(0, 0, width, height), 0, 0, input.Width, input.Height, System.Drawing.GraphicsUnit.Pixel, attributes);
                        }
                    }
                    token.ThrowIfCancellationRequested();
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        if (jpeg)
                        {
                            var codec = ImageCodecInfo.GetImageEncoders().First(x => x.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
                            using (var parameters = new EncoderParameters(1)) { parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality); resized.Save(output, codec, parameters); }
                        }
                        else resized.Save(output, System.Drawing.Imaging.ImageFormat.Png);
                        output.Flush(true);
                    }
                }
                token.ThrowIfCancellationRequested();
                long size = new FileInfo(temporary).Length;
                if (File.Exists(target)) File.Replace(temporary, target, null); else File.Move(temporary, target);
                return size;
            }
            finally { if (File.Exists(temporary)) try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
    }
}
