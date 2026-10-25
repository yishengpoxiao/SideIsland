using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Media.Imaging;

namespace CodexIsland
{
    internal sealed class BatchImageOptions
    {
        internal string OutputDirectory, Format = "JPG";
        internal int MaximumWidth = 1920, MaximumHeight = 1080, Quality = 82;
        internal bool Resize = true;
    }
    internal sealed class BatchImageEntry
    { internal string SourcePath, OutputPath; internal int Width, Height; }
    internal sealed class BatchImageResult
    { internal readonly List<string> Saved = new List<string>(); internal bool Canceled; }
    internal static class BatchImageFiles
    {
        internal static List<BatchImageEntry> Preview(IEnumerable<string> paths, BatchImageOptions options, CancellationToken token, IProgress<int> progress)
        {
            if (options == null || String.IsNullOrWhiteSpace(options.OutputDirectory) || !Directory.Exists(options.OutputDirectory)) throw new ArgumentException("请选择现有的输出文件夹。");
            if (options.Format != "JPG" && options.Format != "PNG") throw new ArgumentException("仅支持 JPG 和 PNG 格式。");
            if (options.Resize) ImageQuickTools.ValidateSize(options.MaximumWidth, options.MaximumHeight);
            if (options.Format == "JPG" && (options.Quality < 1 || options.Quality > 100)) throw new ArgumentException("JPG 质量需为 1–100。");
            var sources = BatchFiles.NormalizePaths(paths); var entries = new List<BatchImageEntry>(); var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < sources.Count; index++)
            {
                token.ThrowIfCancellationRequested(); var source = ImageQuickTools.Load(sources[index], token); double scale = options.Resize ? Math.Min(1, Math.Min(options.MaximumWidth / (double)source.PixelWidth, options.MaximumHeight / (double)source.PixelHeight)) : 1;
                int width = Math.Max(1, (int)Math.Round(source.PixelWidth * scale)), height = Math.Max(1, (int)Math.Round(source.PixelHeight * scale));
                string stem = Path.GetFileNameWithoutExtension(sources[index]) + "-processed", extension = options.Format == "JPG" ? ".jpg" : ".png", target = null;
                for (int suffix = 0; suffix < 10000; suffix++)
                {
                    string candidate = Path.Combine(Path.GetFullPath(options.OutputDirectory), stem + (suffix == 0 ? "" : "-" + suffix) + extension);
                    if (!File.Exists(candidate) && !Directory.Exists(candidate) && used.Add(candidate)) { target = candidate; break; }
                }
                if (target == null) throw new IOException("无法生成不重复的输出文件名。");
                entries.Add(new BatchImageEntry { SourcePath = sources[index], OutputPath = target, Width = width, Height = height });
                if (progress != null) progress.Report((index + 1) * 100 / sources.Count);
            }
            return entries;
        }
        internal static BatchImageResult Execute(IList<BatchImageEntry> entries, BatchImageOptions options, CancellationToken token, IProgress<int> progress)
        {
            if (entries == null || entries.Count == 0) throw new ArgumentException("请先生成图片处理预览。");
            var result = new BatchImageResult();
            try
            {
                for (int index = 0; index < entries.Count; index++)
                {
                    token.ThrowIfCancellationRequested(); var entry = entries[index];
                    string temporary = Path.Combine(Path.GetDirectoryName(entry.OutputPath), ".side-island-batch-" + Guid.NewGuid().ToString("N") + Path.GetExtension(entry.OutputPath));
                    try
                    {
                        BitmapSource source = ImageQuickTools.Load(entry.SourcePath, token);
                        ImageQuickTools.Save(source, entry.SourcePath, temporary, entry.Width, entry.Height, options.Format, options.Format == "JPG" ? options.Quality : 82, token);
                        token.ThrowIfCancellationRequested(); File.Move(temporary, entry.OutputPath); result.Saved.Add(entry.OutputPath);
                    }
                    finally { if (File.Exists(temporary)) try { File.Delete(temporary); } catch { } }
                    if (progress != null) progress.Report((index + 1) * 100 / entries.Count);
                }
            }
            catch (OperationCanceledException) { result.Canceled = true; }
            return result;
        }
    }
}
