using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using SideIsland.Core;

namespace SideIsland.Desktop;

public sealed partial class IslandWindow
{
    private PortableBatchFiles? batchEngine;
    private PortableRenamePlan? batchPlan;
    private bool batchWorking;
    private CancellationTokenSource? batchCancellation;
    private string batchMessage = "";
    private string[] batchImageRows = [];
    private TextBlock? batchProgressLabel;

    private bool TryPrepareBatchClose()
    {
        if (!batchWorking) return true;
        batchCancellation?.Cancel();
        Report(T("正在取消批量操作并恢复文件名；完成后即可关闭。", "Cancelling the batch and restoring filenames. Close again when it finishes."));
        return false;
    }
    private PortableBatchFiles BatchEngine => batchEngine ??= new PortableBatchFiles(store.DirectoryPath);
    private static string[] BatchPaths(string text) => text.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    private static int BatchNumber(string? text, int minimum, int maximum)
    {
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < minimum || value > maximum) throw new ArgumentException($"Enter a whole number from {minimum} to {maximum}.");
        return value;
    }
    private TextBox BatchField(string key, string initial, string zh, string en, bool multiline = false)
    {
        var input = Input(key, initial, multiline); input.PlaceholderText = T(zh, en); input.IsEnabled = !batchWorking;
        if (multiline) { input.MinHeight = 84; input.MaxHeight = 150; input.MaxLength = 64000; }
        else input.MaxLength = 240;
        var lastText = input.Text;
        input.TextChanged += (_, _) => { var current = input.Text; if (current == lastText) return; lastText = current; if (key.StartsWith("batch-", StringComparison.Ordinal)) batchPlan = null; };
        return input;
    }
    private async Task ChooseBatchFiles(TextBox target, bool images)
    {
        if (testMode || batchWorking) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = true, Title = images ? T("选择 JPG / PNG 图片", "Choose JPG / PNG images") : T("选择需要重命名的文件", "Choose files to rename"),
            FileTypeFilter = images ? [new FilePickerFileType("JPG / PNG") { Patterns = ["*.jpg", "*.jpeg", "*.png"], MimeTypes = ["image/jpeg", "image/png"] }] : null
        });
        if (files.Count == 0) return;
        target.Text = string.Join(Environment.NewLine, files.Select(f => f.TryGetLocalPath()).Where(p => p is not null));
    }
    private void BatchFooter()
    {
        batchProgressLabel = Hint(batchMessage); body.Children.Add(batchProgressLabel);
        if (batchWorking) body.Children.Add(Button(T("取消操作", "Cancel batch"), () => batchCancellation?.Cancel()));
    }
    private void RenderBatchFiles()
    {
        body.Children.Add(Hint(T("先预览，再执行。保留扩展名，不覆盖已有文件；撤销仅限本次运行。开始执行新一批或尝试撤销后，上次撤销权限即失效。", "Preview before renaming; extensions are kept and files are never replaced. Session undo expires when a new batch starts or undo is attempted.")));
        var paths = BatchField("batch-paths", "", "每行一个绝对文件路径", "One absolute file path per line", true); body.Children.Add(paths);
        var choose = AsyncButton(T("选择文件", "Choose files"), () => ChooseBatchFiles(paths, false));
        var clear = Button(T("清空选择", "Clear selection"), () => { paths.Text = ""; batchPlan = null; RenderPage(); });
        choose.IsEnabled = clear.IsEnabled = !batchWorking; body.Children.Add(Actions(choose, clear));
        var template = BatchField("batch-template", "{name}_{n}", "模板：{name}_{n}", "Template: {name}_{n}");
        body.Children.Add(Hint(T("名称模板 · 支持 {name} 和 {n}", "Name template · supports {name} and {n}"))); body.Children.Add(template);
        var prefix = BatchField("batch-prefix", "", "前缀", "Prefix"); var suffix = BatchField("batch-suffix", "", "后缀", "Suffix");
        body.Children.Add(Actions(prefix, suffix));
        var find = BatchField("batch-find", "", "查找（名称部分）", "Find in name"); var replace = BatchField("batch-replace", "", "替换为", "Replace with");
        body.Children.Add(Actions(find, replace));
        var start = BatchField("batch-start", "1", "起始编号", "Start number"); var padding = BatchField("batch-padding", "3", "编号位数 1–9", "Digits 1–9");
        body.Children.Add(Hint(T("起始编号 / 编号位数", "Start number / number digits"))); body.Children.Add(Actions(start, padding));
        var preview = AsyncButton(T("生成预览", "Preview"), async () =>
        {
            var files = BatchPaths(paths.Text ?? ""); var options = new PortableRenameOptions { Template = template.Text ?? "", Prefix = prefix.Text ?? "", Suffix = suffix.Text ?? "", Find = find.Text ?? "", Replace = replace.Text ?? "", Start = BatchNumber(start.Text, 0, 1_000_000_000), Padding = BatchNumber(padding.Text, 1, 9) };
            var progress = BatchProgress(); await RunBatch(async token => { batchPlan = await Task.Run(() => BatchEngine.Preview(files, options, token, progress), token); batchMessage = T($"预览 {batchPlan.Items.Count} 项，将更改 {batchPlan.ChangedCount} 项。", $"Preview: {batchPlan.Items.Count} files, {batchPlan.ChangedCount} changes."); });
        });
        var execute = AsyncButton(T("执行重命名", "Rename"), async () =>
        {
            var plan = batchPlan ?? throw new InvalidOperationException(T("请先重新生成预览。", "Create a fresh preview first."));
            var progress = BatchProgress(); await RunBatch(async token => { await Task.Run(() => BatchEngine.Execute(plan, token, progress), token); drafts["batch-paths"] = string.Join(Environment.NewLine, plan.Items.Select(i => i.DestinationPath)); batchPlan = null; batchMessage = T("重命名完成。可撤销最近一批；详细清单已保存。", "Renamed. The latest batch can be undone; the manifest is saved."); });
        });
        var undoButton = AsyncButton(T("撤销最近一批", "Undo last batch"), async () =>
        {
            var progress = BatchProgress(); await RunBatch(async token => { await Task.Run(() => BatchEngine.Undo(token, progress), token); drafts["batch-paths"] = ""; batchPlan = null; batchMessage = T("已安全恢复原文件名。", "Original filenames restored safely."); });
        });
        preview.IsEnabled = !batchWorking; execute.IsEnabled = !batchWorking && batchPlan?.ChangedCount > 0; undoButton.IsEnabled = !batchWorking && BatchEngine.CanUndo;
        body.Children.Add(Actions(preview, execute)); body.Children.Add(Actions(undoButton));
        if (batchPlan is not null)
        {
            var rows = new StackPanel { Spacing = 5 };
            foreach (var item in batchPlan.Items.Take(200)) rows.Children.Add(Card(new StackPanel { Spacing = 2, Children = { Text(Path.GetFileName(item.OriginalPath), 12), Text("→ " + Path.GetFileName(item.DestinationPath), 12), Hint(Path.GetDirectoryName(item.OriginalPath) ?? "") } }));
            body.Children.Add(rows);
        }
        BatchFooter();
    }
    private IProgress<PortableBatchProgress> BatchProgress() => new Progress<PortableBatchProgress>(value =>
    {
        if (closing || !batchWorking) return;
        batchMessage = T("处理中", "Processing") + $" · {value.Completed}/{value.Total}";
        if (batchProgressLabel is not null) batchProgressLabel.Text = batchMessage;
    });
    private async Task RunBatch(Func<CancellationToken, Task> action)
    {
        if (batchWorking || testMode) return;
        batchWorking = true; batchCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); batchMessage = T("正在处理…", "Working…");
        RenderPage();
        try { await action(batchCancellation.Token); }
        catch (OperationCanceledException) { batchMessage = T("操作已取消；已完成的图片输出会保留。重命名恢复记录见日志。", "Cancelled. Completed image outputs are kept. Rename recovery details are in the journal."); batchPlan = null; }
        catch (Exception error) { batchMessage = T("操作停止：", "Batch stopped: ") + error.Message; batchPlan = null; }
        finally { batchWorking = false; batchCancellation.Dispose(); batchCancellation = null; if (!closing) { Report(batchMessage); if (page == "tools" && selected is "batch-files" or "batch-images") RenderPage(); } }
    }
    private void RenderBatchImages()
    {
        body.Children.Add(Hint(T("批量缩小、压缩或转换 JPG / PNG；所有结果另存为新文件。JPEG 的透明区域使用白底。", "Resize, compress or convert JPG / PNG images into new files. Transparent areas use white for JPEG.")));
        var paths = BatchField("images-paths", "", "每行一个图片绝对路径", "One absolute image path per line", true); body.Children.Add(paths);
        var choose = AsyncButton(T("选择图片", "Choose images"), () => ChooseBatchFiles(paths, true)); var clear = Button(T("清空选择", "Clear selection"), () => { paths.Text = ""; batchImageRows = []; RenderPage(); });
        choose.IsEnabled = clear.IsEnabled = !batchWorking; body.Children.Add(Actions(choose, clear));
        var output = BatchField("images-output", "", "输出文件夹（已存在）", "Existing output folder"); body.Children.Add(output);
        var folder = AsyncButton(T("选择输出文件夹", "Choose output folder"), async () => { if (testMode || batchWorking) return; var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false, Title = T("选择输出文件夹", "Choose output folder") }); if (folders.Count != 0) output.Text = folders[0].TryGetLocalPath() ?? ""; }); folder.IsEnabled = !batchWorking; body.Children.Add(folder);
        var format = new ComboBox { ItemsSource = new[] { "JPG", "PNG" }, SelectedIndex = drafts.GetValueOrDefault("images-format") == "PNG" ? 1 : 0, HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = !batchWorking };
        format.SelectionChanged += (_, _) => drafts["images-format"] = format.SelectedIndex == 1 ? "PNG" : "JPG";
        var quality = BatchField("images-quality", "85", "JPG 质量 1–100", "JPG quality 1–100");
        body.Children.Add(Hint(T("输出格式 / JPG 质量", "Output format / JPG quality"))); body.Children.Add(Actions(format, quality));
        var width = BatchField("images-width", "1600", "最大宽度", "Max width"); var height = BatchField("images-height", "1600", "最大高度", "Max height");
        body.Children.Add(Hint(T("最大像素尺寸 · 保持比例，只缩小不放大", "Maximum pixels · keep aspect ratio, no enlargement"))); body.Children.Add(Actions(width, height));
        var process = AsyncButton(T("处理并另存新文件", "Process into new files"), async () =>
        {
            var files = BatchPaths(paths.Text ?? "");
            var options = new PortableImageOptions(output.Text ?? "", format.SelectedIndex == 1 ? "PNG" : "JPG", BatchNumber(width.Text, 1, 8192), BatchNumber(height.Text, 1, 8192), BatchNumber(quality.Text, 1, 100));
            var progress = BatchProgress(); await RunBatch(async token =>
            {
                var results = await Task.Run(() => PortableBatchImages.Process(files, options, store.DirectoryPath, token, progress), token);
                batchImageRows = results.Select(r => Path.GetFileName(r.SourcePath) + "\n→ " + r.OutputPath).ToArray();
                batchMessage = T($"已保存 {results.Count} 张图片；清单含源文件 SHA-256。", $"Saved {results.Count} images; the manifest includes source SHA-256 hashes.");
            });
        }); process.IsEnabled = !batchWorking; body.Children.Add(process);
        body.Children.Add(Hint(T("最多 200 张，每张文件不超过 64 MiB、解码不超过 2400 万像素；按像素处理，不承诺保留 EXIF、方向、色彩配置或动画。", "Up to 200 images, 64 MiB and 24 million decoded pixels each. Pixel processing does not promise to retain EXIF, orientation, color profiles or animation.")));
        foreach (var row in batchImageRows) body.Children.Add(Card(Text(row, 12)));
        BatchFooter();
    }
}

internal sealed record PortableImageOptions(string OutputDirectory, string Format, int MaximumWidth = 1600, int MaximumHeight = 1600, int Quality = 85);
internal sealed record PortableImageResult(string SourcePath, string OutputPath, int Width, int Height, string SourceSha256);
internal static class PortableBatchImages
{
    internal const long MaximumFileBytes = 64L * 1024 * 1024;
    internal const long MaximumPixels = 24_000_000;
    internal static IReadOnlyList<PortableImageResult> Process(IEnumerable<string> input, PortableImageOptions options, string dataDirectory, CancellationToken token = default, IProgress<PortableBatchProgress>? progress = null)
    {
        var files = input.ToArray();
        if (files.Length is < 1 or > 200 || options.Format is not ("JPG" or "PNG") || options.MaximumWidth is < 1 or > 8192 || options.MaximumHeight is < 1 or > 8192 || options.Quality is < 1 or > 100
            || !Path.IsPathFullyQualified(options.OutputDirectory) || !Directory.Exists(options.OutputDirectory)) throw new ArgumentException("Choose 1–200 images, an existing absolute output folder, JPG/PNG, valid dimensions and quality.");
        var directory = Path.Combine(dataDirectory, "batch-operations"); Directory.CreateDirectory(directory);
        var journalPath = Path.Combine(directory, "images-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".jsonl");
        using var journal = new FileStream(journalPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        void Log(object value) { journal.Write(JsonSerializer.SerializeToUtf8Bytes(value)); journal.WriteByte(10); journal.Flush(true); }
        var results = new List<PortableImageResult>();
        Log(new { kind = "image-manifest", version = 1, options, sources = files, utc = DateTime.UtcNow });
        try
        {
            for (var index = 0; index < files.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                var source = PortableBatchFiles.ReadLocalSnapshot(files[index], MaximumFileBytes, token);
                var dimensions = ReadDimensions(source.Bytes);
                Log(new { kind = "source", source.Path, source.Source, dimensions.Width, dimensions.Height, utc = DateTime.UtcNow });
                using var memory = new MemoryStream(source.Bytes, false); using var bitmap = new Bitmap(memory);
                if (bitmap.PixelSize.Width != dimensions.Width || bitmap.PixelSize.Height != dimensions.Height) throw new IOException("The decoder dimensions differ from the checked image header.");
                var scale = Math.Min(1, Math.Min(options.MaximumWidth / (double)dimensions.Width, options.MaximumHeight / (double)dimensions.Height));
                var size = new PixelSize(Math.Max(1, (int)Math.Round(dimensions.Width * scale)), Math.Max(1, (int)Math.Round(dimensions.Height * scale)));
                using var scaled = bitmap.CreateScaledBitmap(size, BitmapInterpolationMode.HighQuality);
                token.ThrowIfCancellationRequested();
                using var encoded = new MemoryStream();
                if (options.Format == "PNG") scaled.Save(encoded, PngBitmapEncoderOptions.Default);
                else
                {
                    // CPU alpha composition avoids creating a window/render target on the worker.
                    using var flattened = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
                    using (var framebuffer = flattened.Lock())
                    {
                        scaled.CopyPixels(framebuffer); var row = new byte[checked(size.Width * 4)];
                        for (var y = 0; y < size.Height; y++)
                        {
                            if ((y & 63) == 0) token.ThrowIfCancellationRequested();
                            var pointer = IntPtr.Add(framebuffer.Address, checked(y * framebuffer.RowBytes)); Marshal.Copy(pointer, row, 0, row.Length);
                            for (var x = 0; x < row.Length; x += 4) { var a = row[x + 3]; for (var c = 0; c < 3; c++) row[x + c] = (byte)((row[x + c] * a + 255 * (255 - a) + 127) / 255); row[x + 3] = 255; }
                            Marshal.Copy(row, 0, pointer, row.Length);
                        }
                    }
                    flattened.Save(encoded, new JpegBitmapEncoderOptions { Quality = options.Quality });
                }
                token.ThrowIfCancellationRequested();
                if (encoded.Length > 128 * 1024 * 1024) throw new IOException("Encoded image exceeds the output memory limit.");
                var name = Path.GetFileNameWithoutExtension(source.Path);
                if (name.Length > 100) name = name[..100];
                if (name.Length > 0 && char.IsHighSurrogate(name[^1])) name = name[..^1];
                var output = PortableBatchFiles.WriteNewOutput(options.OutputDirectory, name + "-processed." + options.Format.ToLowerInvariant(), encoded.ToArray(), token);
                var result = new PortableImageResult(source.Path, output, size.Width, size.Height, source.Source.Sha256); results.Add(result);
                Log(new { kind = "saved", result, utc = DateTime.UtcNow }); progress?.Report(new("images", index + 1, files.Length));
            }
            Log(new { kind = "completed", count = results.Count, utc = DateTime.UtcNow }); return results;
        }
        catch (Exception error) { Log(new { kind = error is OperationCanceledException ? "cancelled" : "stopped", error = error.Message, saved = results.ToArray(), utc = DateTime.UtcNow }); throw; }
    }
    internal static (int Width, int Height) ReadDimensions(byte[] bytes)
    {
        int width = 0, height = 0;
        if (bytes.Length >= 24 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            && BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(8, 4)) == 13 && bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8))
        { width = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)); height = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)); }
        else if (bytes.Length >= 4 && bytes[0] == 0xff && bytes[1] == 0xd8)
        {
            var position = 2; var limit = Math.Min(bytes.Length, 1024 * 1024);
            while (position + 4 <= limit)
            {
                if (bytes[position++] != 0xff) throw new ArgumentException("Invalid JPEG marker stream.");
                while (position < limit && bytes[position] == 0xff) position++;
                if (position >= limit) break; var marker = bytes[position++];
                if (marker is 0xd9 or 0xda) break;
                if (marker is 0x01 or >= 0xd0 and <= 0xd7) continue;
                if (position + 2 > limit) break;
                var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(position, 2));
                if (length < 2 || position + length > limit) throw new ArgumentException("Invalid or oversized JPEG metadata header.");
                if (marker is >= 0xc0 and <= 0xc3 or >= 0xc5 and <= 0xc7 or >= 0xc9 and <= 0xcb or >= 0xcd and <= 0xcf)
                {
                    if (length < 8) throw new ArgumentException("Invalid JPEG size header.");
                    height = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(position + 3, 2)); width = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(position + 5, 2)); break;
                }
                position += length;
            }
        }
        if (width < 1 || height < 1 || width > 32768 || height > 32768 || (long)width * height > MaximumPixels) throw new ArgumentException("Only JPG/PNG images up to 24 million decoded pixels and 32768 pixels per side are supported.");
        return (width, height);
    }
}
