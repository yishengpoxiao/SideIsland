using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Media.Imaging;

namespace CodexIsland
{
    internal static class BatchFilesTests
    {
        private sealed class InlineProgress : IProgress<int>
        { private readonly Action<int> action; internal InlineProgress(Action<int> value) { action = value; } public void Report(int value) { action(value); } }
        internal static void Run()
        {
            string root = Path.Combine(Environment.CurrentDirectory, ".runtime", "batch-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try
            {
                string a = Path.Combine(root, "alpha.txt"), b = Path.Combine(root, "beta.txt"); File.WriteAllText(a, "AAAA", Encoding.UTF8); File.WriteAllText(b, "BBBB", Encoding.UTF8);
                var plan = BatchFiles.Preview(new[] { a, b }, new BatchRenameOptions { Template = "item_{n}", Start = 7, Padding = 3 }, CancellationToken.None, null);
                Check(Path.GetFileName(plan.Entries[0].DestinationPath) == "item_007.txt", "numbered preview preserves extension");
                var transaction = BatchFiles.Execute(plan, CancellationToken.None, null); Check(!File.Exists(a) && File.ReadAllText(plan.Entries[0].DestinationPath) == "AAAA", "real handle-based rename preserves content");
                transaction.Undo(CancellationToken.None, null); Check(File.Exists(a) && File.Exists(b) && transaction.Undone, "undo restores original names");
                bool duplicate = false; try { BatchFiles.Preview(new[] { a, b }, new BatchRenameOptions { Template = "duplicate" }, CancellationToken.None, null); } catch (ArgumentException) { duplicate = true; } Check(duplicate, "duplicate preview targets rejected before changes");
                bool traversal = false; try { BatchFiles.Preview(new[] { a }, new BatchRenameOptions { Template = "../escape" }, CancellationToken.None, null); } catch (ArgumentException) { traversal = true; } Check(traversal, "template path traversal rejected");
                var rollbackPlan = BatchFiles.Preview(new[] { a, b }, new BatchRenameOptions { Template = "collision_{n}" }, CancellationToken.None, null);
                string collision = rollbackPlan.Entries[1].DestinationPath; bool rollbackFailed = false;
                try { BatchFiles.Execute(rollbackPlan, CancellationToken.None, new InlineProgress(value => { if (!File.Exists(collision)) File.WriteAllText(collision, "UNRELATED"); })); } catch (System.ComponentModel.Win32Exception) { rollbackFailed = true; }
                Check(rollbackFailed && File.ReadAllText(a) == "AAAA" && File.ReadAllText(b) == "BBBB" && File.ReadAllText(collision) == "UNRELATED", "mid-commit target collision rolls every rename back and preserves unrelated file");
                Check(!Directory.GetFiles(root, ".side-island-rename-*").Any(), "successful rollback leaves no staging files"); File.Delete(collision);
                var changedPlan = BatchFiles.Preview(new[] { a }, new BatchRenameOptions { Template = "changed" }, CancellationToken.None, null); var changedTransaction = BatchFiles.Execute(changedPlan, CancellationToken.None, null);
                string changed = changedPlan.Entries[0].DestinationPath; DateTime before = File.GetLastWriteTimeUtc(changed); File.WriteAllText(changed, "ZZZZ", Encoding.UTF8); File.SetLastWriteTimeUtc(changed, before);
                bool changedBlocked = false; try { changedTransaction.Undo(CancellationToken.None, null); } catch (IOException) { changedBlocked = true; } Check(changedBlocked && !File.Exists(a) && File.ReadAllText(changed) == "ZZZZ", "undo rejects same-size changed content even when modification time is restored");
                File.Move(changed, a);
                using (var cancelled = new CancellationTokenSource()) { cancelled.Cancel(); bool stopped = false; try { BatchFiles.Preview(new[] { a }, new BatchRenameOptions(), cancelled.Token, null); } catch (OperationCanceledException) { stopped = true; } Check(stopped && File.Exists(a), "cancel before preview does not move files"); }
                ImageBatch(root);
                Console.WriteLine("PASS: actual Windows handle-based batch rename/undo, atomic collision rollback, no overwrite/traversal, changed-file refusal, cancellation and image batch outputs; temporary fixtures only.");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
        private static void ImageBatch(string root)
        {
            string first = Path.Combine(root, "one.png"), second = Path.Combine(root, "two.png"), output = Path.Combine(root, "output"); Directory.CreateDirectory(output);
            Save(first, ScreenshotAnnotationTests.Sample(32, 24)); Save(second, ScreenshotAnnotationTests.Sample(64, 32)); byte[] original = File.ReadAllBytes(first);
            var options = new BatchImageOptions { OutputDirectory = output, Format = "JPG", MaximumWidth = 16, MaximumHeight = 16, Quality = 60 };
            var preview = BatchImageFiles.Preview(new[] { first, second }, options, CancellationToken.None, null);
            Check(preview[0].Width == 16 && preview[0].Height == 12 && preview[1].Height == 8, "batch dimensions preserve ratio and never enlarge");
            using (var cancellation = new CancellationTokenSource())
            {
                var result = BatchImageFiles.Execute(preview, options, cancellation.Token, new InlineProgress(value => cancellation.Cancel()));
                Check(result.Canceled && result.Saved.Count == 1 && File.Exists(result.Saved[0]) && !File.Exists(preview[1].OutputPath), "cancel keeps completed outputs and stops next image");
                var decoded = ImageQuickTools.Load(result.Saved[0], CancellationToken.None); Check(decoded.PixelWidth == 16 && decoded.PixelHeight == 12, "real output has requested format and dimensions");
            }
            Check(File.ReadAllBytes(first).SequenceEqual(original), "original image bytes unchanged");
            var unusedDrafts = new BatchImageOptions { OutputDirectory = output, Format = "PNG", Resize = false, MaximumWidth = 0, MaximumHeight = 0, Quality = 0 };
            var unscaled = BatchImageFiles.Preview(new[] { first }, unusedDrafts, CancellationToken.None, null);
            var unscaledResult = BatchImageFiles.Execute(unscaled, unusedDrafts, CancellationToken.None, null);
            Check(unscaled[0].Width == 32 && unscaled[0].Height == 24 && unscaledResult.Saved.Count == 1, "PNG without resizing ignores unused dimension and JPG quality drafts");
            var racePreview = BatchImageFiles.Preview(new[] { first }, options, CancellationToken.None, null); File.WriteAllText(racePreview[0].OutputPath, "KEEP"); bool prevented = false;
            try { BatchImageFiles.Execute(racePreview, options, CancellationToken.None, null); } catch (IOException) { prevented = true; }
            Check(prevented && File.ReadAllText(racePreview[0].OutputPath) == "KEEP", "new target created after preview is never overwritten");
            Check(!Directory.GetFiles(output, ".side-island-batch-*").Any(), "image cancellation and conflict clean only owned temporary files");
        }
        private static void Save(string path, BitmapSource source) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source)); using (var file = File.Create(path)) encoder.Save(file); }
        private static void Check(bool value, string message) { if (!value) throw new Exception("Batch files: " + message); }
    }
}
