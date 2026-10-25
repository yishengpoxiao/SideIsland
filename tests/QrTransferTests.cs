using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Net.Codecrete.QrCodeGenerator;

namespace CodexIsland
{
    internal static class QrTransferTests
    {
        internal static void Run()
        {
            Match(QrCode.EncodeText("98323", QrCode.Ecc.Medium), QrReferenceVectors.Modules0);
            Match(QrCode.EncodeBinary(Encoding.UTF8.GetBytes("😀 € éñ 💩"), QrCode.Ecc.Low), QrReferenceVectors.Modules3);
            foreach (string text in new[] { "https://example.com/?q=你好", "第一行\n第二行 😀", new string('中', 700), new string('a', QrTransferTools.MaximumUtf8Bytes) })
            {
                QrCode code = QrTransferTools.Encode(text);
                BitmapSource image = QrTransferTools.Render(code);
                Check(image.IsFrozen && image.PixelWidth == (code.Size + 8) * 6, "rendered pixels have an integer module scale and four-module quiet zone");
                byte[] pixels = new byte[image.PixelWidth * image.PixelHeight]; image.CopyPixels(pixels, image.PixelWidth, 0);
                Check(pixels.Take(image.PixelWidth * 24).All(x => x == 255), "top quiet zone is white");
                Check(pixels.Any(x => x == 0), "render contains black modules");
            }
            Throws(() => QrTransferTools.Encode("")); Throws(() => QrTransferTools.Encode(" \n"));
            Throws(() => QrTransferTools.Encode(new string('中', 985))); Throws(() => QrTransferTools.Encode("\uD800"));
            Throws(() => QrTransferTools.CountBytes("\uDC00"));
            CapacityTests();
            string folder = Path.Combine(Path.GetTempPath(), "SideIsland.Qr." + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            try
            {
                string path = Path.Combine(folder, "qr.png"); QrTransferTools.Save(QrTransferTools.Render(QrTransferTools.Encode("中文传话 😀")), path);
                using (var stream = File.OpenRead(path))
                {
                    var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    Check(decoder is PngBitmapDecoder && decoder.Frames[0].PixelWidth > 0, "saved PNG decodes without a runtime dependency");
                }
                using (var panel = new QrTransferPanel(true))
                {
                    var input = (TextBox)typeof(QrTransferPanel).GetField("input", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(panel);
                    input.Text = new string('中', 985);
                    var result = (System.Windows.Controls.Image)typeof(QrTransferPanel).GetField("image", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(panel);
                    var status = (TextBlock)typeof(QrTransferPanel).GetField("status", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(panel);
                    PumpUntil(() => status.Text.Contains("容量"));
                    Check(result.Source == null, "invalid replacement input cannot retain an earlier QR");
                    input.Text = "https://example.com/中文";
                    PumpUntil(() => result.Source != null);
                    Check(result.Source.IsFrozen, "asynchronous current input publishes a frozen QR image");
                    var correction = Field<ComboBox>(panel, "correction");
                    Check(correction.SelectedIndex == 0, "the default correction level gives the maximum standard single-code capacity");
                    correction.SelectedIndex = 1;
                    input.Text = new string('a', 2900);
                    PumpUntil(() => status.Text.Contains("容量") && result.Source == null);
                    correction.SelectedIndex = 0;
                    PumpUntil(() => result.Source != null);
                    Check(((BitmapSource)result.Source).PixelWidth == (QrTransferTools.Encode(input.Text, QrCode.Ecc.Low).Size + 8) * 6, "selecting L updates real encoding capacity");
                    correction.SelectedIndex = 3;
                    PumpUntil(() => status.Text.Contains("容量") && result.Source == null);
                    Check(!Field<Button>(panel, "copy").IsEnabled && !Field<Button>(panel, "save").IsEnabled, "changing to a smaller capacity disables stale outputs");
                    correction.SelectedIndex = 0;
                    var generate = typeof(QrTransferPanel).GetMethod("Generate", BindingFlags.NonPublic | BindingFlags.Instance);
                    input.Text = new string('a', QrTransferTools.MaximumUtf8Bytes); generate.Invoke(panel, null);
                    input.Text = "最新原文 😀"; generate.Invoke(panel, null);
                    PumpUntil(() => result.Source != null);
                    int latestWidth = (QrTransferTools.Encode(input.Text).Size + 8) * 6;
                    Check(((BitmapSource)result.Source).PixelWidth == latestWidth, "a superseded maximum-size worker cannot replace the current input");
                    input.Text = new string('a', QrTransferTools.MaximumUtf8Bytes); generate.Invoke(panel, null);
                    panel.Dispose(); PumpFor(200);
                    Check(result.Source == null && Field<BitmapSource>(panel, "current") == null && !Field<Button>(panel, "copy").IsEnabled, "disposing cancels pending output and releases the image");
                    panel.Dispose();
                }
            }
            finally { Directory.Delete(folder, true); }
            Console.WriteLine("PASS: QR reference matrices, all four real UTF-8 capacity boundaries, Unicode/emoji, quiet zone, PNG output and stale-work cancellation without external requests.");
        }
        private static void CapacityTests()
        {
            FinderPenaltyBoundaryTests();
            foreach (var level in new[] { QrCode.Ecc.Low, QrCode.Ecc.Medium, QrCode.Ecc.Quartile, QrCode.Ecc.High })
            {
                int maximum = QrTransferTools.MaximumBytes(level);
                QrCode atLimit = QrTransferTools.Encode(new string('a', maximum), level);
                Check(atLimit.Version == 40, "each correction level uses its largest standard QR capacity");
                Throws(() => QrTransferTools.Encode(new string('a', maximum + 1), level));
                // Check the bound against the vendored encoder itself, bypassing our validator.
                byte[] tooLong = Encoding.UTF8.GetBytes(new string('a', maximum + 1));
                bool libraryRejected = false;
                try { QrCode.EncodeSegments(new System.Collections.Generic.List<QrSegment> { QrSegment.MakeEci(26), QrSegment.MakeBytes(tooLong) }, level); }
                catch (DataTooLongException) { libraryRejected = true; }
                Check(libraryRejected, "the next byte really exceeds the QR standard capacity with UTF-8 ECI");
                var emoji = new StringBuilder();
                for (int i = 0; i < maximum / 4; i++) emoji.Append("😀");
                emoji.Append(new string('x', maximum % 4));
                Check(QrTransferTools.CountBytes(emoji.ToString()) == maximum, "emoji boundary accounts for four UTF-8 bytes per scalar");
                Check(QrTransferTools.Encode(emoji.ToString(), level).Version == 40, "emoji text reaches the exact byte capacity without truncation");
                string overflow = emoji.ToString() + "x"; Throws(() => QrTransferTools.Encode(overflow, level));
            }
            Check(QrTransferTools.MaximumUtf8Bytes > 1200, "the earlier artificial limit is removed");
            Throws(() => QrTransferTools.Encode(new string('x', 1024 * 1024)));
        }
        private static void FinderPenaltyBoundaryTests()
        {
            // The QR timing lines can alternate at every module. Their outside
            // white borders require two more history entries at version 40.
            for (int version = 1; version <= 40; version++)
            {
                int size = 17 + version * 4;
                foreach (bool firstBlack in new[] { false, true })
                {
                    var penalty = new QrCode.FinderPenalty(size); penalty.Reset();
                    bool runColor = false; int run = 0, pad = size, patterns = 0;
                    for (int i = 0; i < size; i++)
                    {
                        bool black = (i % 2 == 0) == firstBlack;
                        if (black == runColor) run++;
                        else
                        {
                            penalty.AddHistory(run + pad); pad = 0;
                            if (!runColor) patterns += penalty.CountPatterns();
                            runColor = black; run = 1;
                        }
                    }
                    patterns += penalty.TerminateAndCount(runColor, run + pad);
                    Check(patterns == 0, "fully alternating lines and their borders cannot resemble a 1:1:3:1:1 finder pattern at any QR version");
                }
            }
        }
        private static T Field<T>(object value, string name)
        { return (T)value.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(value); }
        private static void Match(QrCode code, string[] expected)
        {
            Check(code.Size == expected.Length, "upstream QR vector size");
            for (int y = 0; y < expected.Length; y++) for (int x = 0; x < expected.Length; x++)
                Check(code.GetModule(x, y) == (expected[y][x] == 'X'), "QR encoding matches independent upstream fixture");
        }
        private static void PumpUntil(Func<bool> complete)
        {
            var watch = Stopwatch.StartNew(); var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (s, e) => { if (complete() || watch.ElapsedMilliseconds > 6000) frame.Continue = false; };
            timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
            Check(complete(), "asynchronous QR work completes within its fixture deadline");
        }
        private static void PumpFor(int milliseconds)
        {
            var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
            timer.Tick += (s, e) => frame.Continue = false; timer.Start();
            try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        }
        private static void Throws(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("QR invalid input was accepted"); }
        private static void Check(bool success, string message) { if (!success) throw new Exception("QR transfer: " + message); }
    }
}
