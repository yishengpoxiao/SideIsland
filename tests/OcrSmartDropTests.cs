using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexIsland
{
    internal static class OcrSmartDropTests
    {
        internal static void Run()
        {
            var data = new DataObject(DataFormats.FileDrop, new[] { @"C:\Fixture\error.PNG" });
            Check(SmartDropParser.Read(data).Kind == SmartDropKind.Image, "image file extensions are case insensitive");
            var many = SmartDropParser.Read(new DataObject(DataFormats.FileDrop, new[] { @"C:\Fixture\one.png", @"C:\Fixture\two.png" }));
            Check(many.Kind == SmartDropKind.Files && many.Paths.Length == 2, "multiple images offer file actions without partially processing the first");
            Check(SmartDropParser.Read(new DataObject(DataFormats.FileDrop, new[] { "relative.png" })).Kind == SmartDropKind.None, "relative file payloads are rejected");
            Check(SmartDropParser.Read(new DataObject(DataFormats.FileDrop, new[] { @"C:relative.png" })).Kind == SmartDropKind.None, "drive-relative file payloads are rejected");
            Check(SmartDropParser.Read(new DataObject(DataFormats.FileDrop, Enumerable.Repeat(@"C:\test.txt", 101).ToArray())).Kind == SmartDropKind.None, "large file batches are bounded before deduplication");
            var duplicate = SmartDropParser.Read(new DataObject(DataFormats.FileDrop, new[] { @"C:\Fixture\a.txt", @"c:\fixture\A.TXT" }));
            Check(duplicate.Kind == SmartDropKind.Files && duplicate.Paths.Length == 1, "same Windows path is deduplicated");
            string text = "配置😀\nport=8080";
            Check(SmartDropParser.Read(new DataObject(DataFormats.UnicodeText, text)).Text == text, "Unicode text payloads preserve exact whitespace and emoji");
            Check(SmartDropParser.Read(new DataObject(DataFormats.UnicodeText, new string('x', 120001))).Kind == SmartDropKind.None, "oversized text is not truncated silently");
            Check(SmartDropParser.Read(new DataObject(DataFormats.UnicodeText, "abc\0def")).Kind == SmartDropKind.None, "embedded NUL is rejected");
            Check(SmartDropParser.Read(null).Kind == SmartDropKind.None && !SmartDropParser.Supports(null), "missing data safely declines the drop");

            var pixels = new byte[32 * 16 * 4]; for (int i = 0; i < pixels.Length; i++) pixels[i] = 255;
            var bitmap = BitmapSource.Create(32, 16, 96, 96, PixelFormats.Bgra32, null, pixels, 32 * 4); bitmap.Freeze();
            var prepared = OcrImages.Prepare(bitmap, 16);
            Check(prepared.PixelWidth == 16 && prepared.PixelHeight == 8 && prepared.IsFrozen && prepared.Format == PixelFormats.Bgra32, "OCR preparation preserves aspect ratio and detaches a frozen BGRA image");
            Check(OcrImages.Prepare(bitmap, 4096).PixelWidth == 32, "small images are not enlarged");
            Check(OcrImages.BoundResult(new[] { "Error 8080", "配置😀" }) == "Error 8080" + Environment.NewLine + "配置😀", "OCR line boundaries survive extraction");
            Check(OcrImages.BoundResult(new[] { "", "next" }) == Environment.NewLine + "next", "empty OCR lines keep their boundary");
            bool bounded = false; try { OcrImages.BoundResult(new[] { new string('a', 120000), "b" }); } catch (ArgumentException) { bounded = true; }
            Check(bounded, "OCR output beyond the comparison limit is rejected");
            var fake = new FakeOcrAdapter(); int caller = Thread.CurrentThread.ManagedThreadId;
            Check(OcrRecognition.RunAsync(fake, bitmap, "en-US", CancellationToken.None).GetAwaiter().GetResult() == text, "fake OCR result returns unchanged");
            Check(fake.Thread != caller && fake.Language == "en-US" && ReferenceEquals(fake.Image, bitmap), "recognition runs off the caller with the chosen language and frozen fixture");
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel(); bool cancelled = false;
                try { OcrRecognition.RunAsync(fake, bitmap, "", cancellation.Token).GetAwaiter().GetResult(); } catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled && fake.Calls == 1, "cancelled work never invokes the adapter");
            }
            fake.Output = new string('a', 120001); bounded = false;
            try { OcrRecognition.RunAsync(fake, bitmap, "", CancellationToken.None).GetAwaiter().GetResult(); } catch (ArgumentException) { bounded = true; }
            Check(bounded, "oversized adapter output cannot leak into UI");
            using (var started = new ManualResetEventSlim())
            using (var cancellation = new CancellationTokenSource())
            {
                var slow = new DelayedOcrAdapter(started); Task<string> pending = OcrRecognition.RunAsync(slow, bitmap, "", cancellation.Token);
                Check(started.Wait(2000), "slow fake adapter starts on a worker"); cancellation.Cancel(); bool cancelled = false;
                try { pending.GetAwaiter().GetResult(); } catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled, "cancelling an in-flight OCR operation does not publish a stale result");
            }
            Console.WriteLine("PASS: smart drop payload routing, bounded Unicode data, synthetic OCR bitmap preparation, background fake recognition, language selection and cancellation.");
        }
        private sealed class DelayedOcrAdapter : IOcrAdapter
        {
            private readonly ManualResetEventSlim started;
            internal DelayedOcrAdapter(ManualResetEventSlim signal) { started = signal; }
            public OcrLanguageChoice[] Languages() { return new OcrLanguageChoice[0]; }
            public async Task<string> RecognizeAsync(BitmapSource image, string language, CancellationToken cancellation)
            { started.Set(); await Task.Delay(5000, cancellation).ConfigureAwait(false); return "late result"; }
        }

        private sealed class FakeOcrAdapter : IOcrAdapter
        {
            internal int Thread, Calls; internal string Language, Output = "配置😀\nport=8080"; internal BitmapSource Image;
            public OcrLanguageChoice[] Languages() { return new[] { new OcrLanguageChoice { Tag = "en-US", Name = "English" } }; }
            public Task<string> RecognizeAsync(BitmapSource image, string language, CancellationToken cancellation)
            { Thread = System.Threading.Thread.CurrentThread.ManagedThreadId; Language = language; Image = image; Calls++; cancellation.ThrowIfCancellationRequested(); return Task.FromResult(Output); }
        }
        private static void Check(bool condition, string message) { if (!condition) throw new Exception("OCR / smart drop: " + message); }
    }
}
