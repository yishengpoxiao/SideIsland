using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexIsland
{
    internal static class FileHashTests
    {
        public static void Run()
        {
            Check(Hash("", "SHA256") == "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", "empty SHA256 vector");
            Check(Hash("abc", "SHA256") == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", "abc SHA256 vector");
            Check(Hash("abc", "SHA512") == "ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a2192992a274fc1a836ba3c23a3feebbd454d4423643ce80e2a9ac94fa54ca49f", "abc SHA512 vector");
            string expected; Check(FileHashTools.TryNormalizeExpected("  " + Hash("abc", "SHA256").ToUpperInvariant() + "\r\n", "SHA256", out expected) && expected == Hash("abc", "SHA256"), "normalize expected hash");
            Check(!FileHashTools.TryNormalizeExpected(new string('x', 64), "SHA256", out expected), "reject nonhex");
            Check(!FileHashTools.TryNormalizeExpected(new string('a', 128), "SHA256", out expected), "reject wrong algorithm length");
            ManifestTests();
            string path = Path.Combine(Path.GetTempPath(), "island-hash-" + Guid.NewGuid().ToString("N") + ".bin");
            try
            {
                byte[] payload = new byte[150003]; for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 251); File.WriteAllBytes(path, payload);
                string file = FileHashTools.ComputeFile(path, "SHA256", CancellationToken.None);
                using (var memory = new MemoryStream(payload)) Check(file == FileHashTools.Compute(memory, "SHA256", CancellationToken.None), "file streamed across multiple buffers");
                Check(File.ReadAllBytes(path).SequenceEqual(payload), "all source bytes preserved");
                var cancel = new CancellationTokenSource(); cancel.Cancel(); bool canceled = false;
                try { FileHashTools.ComputeFile(path, "SHA256", cancel.Token); } catch (OperationCanceledException) { canceled = true; } finally { cancel.Dispose(); }
                Check(canceled, "canceled request does not start hashing");
                using (var lockFile = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    bool denied = false; try { FileHashTools.ComputeFile(path, "SHA256", CancellationToken.None); } catch (IOException) { denied = true; } Check(denied, "locked source fails without modification");
                }
            }
            finally { if (File.Exists(path)) File.Delete(path); }
            PanelTests();
            ManifestLayoutTests();
            Console.WriteLine("PASS: SHA256/SHA512 vectors, GNU/BSD manifest matching, ambiguity and size bounds, file streaming, real async controls, comparison, cancellation, disposal and IO recovery.");
        }

        private static void ManifestTests()
        {
            string digest = Hash("abc", "SHA256"), other = Hash("def", "SHA256"), expected, error;
            Check(FileHashTools.TryResolveExpected(digest.ToUpperInvariant(), "SHA256", null, out expected, out error) && expected == digest, "raw checksum does not need a filename");
            string manifest = "\uFEFF# release checksums\r\n" + other + "  other.zip\r\n" + digest.ToUpperInvariant() + " *发布 包.zip\r\n";
            Check(FileHashTools.TryResolveExpected(manifest, "SHA256", "C:\\downloads\\发布 包.zip", out expected, out error) && expected == digest, "GNU binary list selects exact Unicode basename across platforms");
            Check(FileHashTools.TryResolveExpected(digest + "  build/app.zip\n" + digest + "  build/app.zip", "SHA256", "/tmp/app.zip", out expected, out error), "identical duplicate entries agree");
            Check(!FileHashTools.TryResolveExpected(digest + "  one/app.zip\n" + digest + "  two/app.zip", "SHA256", "app.zip", out expected, out error) && expected == null && error == "ambiguous", "distinct paths with same basename remain ambiguous even with identical hashes");
            Check(!FileHashTools.TryResolveExpected(digest + "  app.zip\n" + other + "  app.zip", "SHA256", "app.zip", out expected, out error) && expected == null && error == "ambiguous", "conflicting values cannot produce false success");
            Check(!FileHashTools.TryResolveExpected(manifest, "SHA256", "missing.zip", out expected, out error) && error == "file-missing", "unmatched file cannot use first list entry");
            Check(!FileHashTools.TryResolveExpected(manifest, "SHA256", null, out expected, out error) && error == "file-required", "list requires explicit selected filename");
            Check(!FileHashTools.TryResolveExpected(digest + "  App.zip", "SHA256", "app.zip", out expected, out error) && error == "file-missing", "filename matching remains case sensitive");
            Check(FileHashTools.TryResolveExpected("SHA256 (folder/my (file).zip) = " + digest, "SHA256", "my (file).zip", out expected, out error) && expected == digest, "BSD tagged list supports spaces and parentheses");
            string sha512 = Hash("abc", "SHA512");
            Check(FileHashTools.TryResolveExpected(sha512 + "  app.zip", "SHA512", "app.zip", out expected, out error) && expected == sha512, "GNU SHA512 list");
            Check(FileHashTools.TryResolveExpected("SHA512 (app.zip) = " + sha512, "SHA512", "app.zip", out expected, out error) && expected == sha512, "BSD SHA512 list");
            Check(!FileHashTools.TryResolveExpected("SHA512 (app.zip) = " + sha512, "SHA256", "app.zip", out expected, out error) && error == "manifest", "wrong selected algorithm rejected");
            Check(!FileHashTools.TryResolveExpected(digest + "  app.zip\nmalformed", "SHA256", "app.zip", out expected, out error) && expected == null && error == "manifest", "malformed later rows cannot be ignored after a match");
            Check(!FileHashTools.TryResolveExpected("\\" + digest + "  odd\\nname", "SHA256", "odd\\nname", out expected, out error) && error == "manifest", "unsupported escaped GNU filenames are rejected");
            Check(!FileHashTools.TryResolveExpected(new string('x', FileHashTools.MaximumManifestLength + 1), "SHA256", "app.zip", out expected, out error) && error == "too-large", "manifest character bound");
            Check(!FileHashTools.TryResolveExpected(String.Join("\n", Enumerable.Repeat("# comment", 1025)), "SHA256", "app.zip", out expected, out error) && error == "too-large", "manifest line bound");
            Check(!FileHashTools.TryResolveExpected(digest + "  app.zip ", "SHA256", "app.zip", out expected, out error) && error == "file-missing", "significant trailing filename space cannot match a different file");
            Check(FileHashTools.TryResolveExpected(digest + "  app.zip ", "SHA256", "app.zip ", out expected, out error) && expected == digest, "trailing filename space is preserved verbatim");
            Check(!FileHashTools.TryResolveExpected(digest + "  app.zip" + new string(' ', FileHashTools.MaximumManifestLength), "SHA256", "app.zip", out expected, out error) && error == "too-large", "raw padded payload cannot evade character limit");
            Check(!FileHashTools.TryResolveExpected(new string('\n', 1025) + digest + "  app.zip", "SHA256", "app.zip", out expected, out error) && error == "too-large", "leading blank lines cannot evade line bound");
            Check(!FileHashTools.TryResolveExpected(digest + new string('\n', 1025), "SHA256", null, out expected, out error) && error == "too-large", "raw checksum cannot bypass physical line bound");
        }

        private static void PanelTests()
        {
            var previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            string source = Path.Combine(Path.GetTempPath(), "island-hash-panel-" + Guid.NewGuid().ToString("N") + ".bin");
            string largeSource = Path.Combine(Path.GetTempPath(), "island-hash-cancel-" + Guid.NewGuid().ToString("N") + ".bin");
            try
            {
                using (var panel = new FileHashPanel(true))
                {
                    var algorithm = Field<ComboBox>(panel, "algorithm"); var output = Field<TextBox>(panel, "output"); var expected = Field<TextBox>(panel, "expected"); var comparison = Field<TextBlock>(panel, "comparison"); var copy = Field<Button>(panel, "copy");
                    Check(output.Text == "" && !copy.IsEnabled && Field<Button>(panel, "calculate").IsEnabled, "demo startup has no implied hash result");
                    expected.Text = "  " + Hash("Side Island demo", "SHA256").ToUpperInvariant() + "\r\n";
                    Check(comparison.Text.Contains("计算完成后"), "valid expected value waits for a calculated digest");
                    Complete(panel.Calculate());
                    Check(output.Text == Hash("Side Island demo", "SHA256") && copy.IsEnabled && Field<ProgressBar>(panel, "progress").Value == 100, "async demo calculation publishes SHA256 result and final progress");
                    Check(comparison.Text.Contains("✓"), "comparison accepts uppercase digest and surrounding whitespace");
                    expected.Text = Hash("another download", "SHA256") + "  another.zip\n" + output.Text + "  Side Island demo";
                    Check(comparison.Text.Contains("✓"), "real panel matches selected sample in pasted manifest");
                    expected.Text = output.Text + "  wrong.zip"; Check(!comparison.Text.Contains("✓") && comparison.Text.Contains("没有"), "a valid digest for another filename is not accepted");
                    Invoke(panel, "Copy"); Check(Field<TextBlock>(panel, "status").Text.Contains("不修改剪贴板"), "demo copy never writes the real clipboard");
                    expected.Text = new string('0', 64); Check(comparison.Text.Contains("不一致"), "different valid digest reports mismatch");
                    expected.Text = new string('x', 64); Check(comparison.Text.Contains("十六进制"), "invalid digest is format failure rather than mismatch");
                    expected.Text = ""; Check(comparison.Text == "", "clearing expected value clears comparison without clearing digest");
                    Check(output.Text.Length == 64 && copy.IsEnabled, "expected-value edits retain independently valid computed digest");
                    algorithm.SelectedIndex = 1;
                    Check(output.Text == "" && Field<string>(panel, "result") == null && !copy.IsEnabled && Field<ProgressBar>(panel, "progress").Value == 0, "algorithm change immediately invalidates old hash, copy and progress");
                    expected.Text = new string('a', 64); Check(comparison.Text.Contains("128"), "SHA512 expectation requires new digest length");
                    expected.Text = Hash("Side Island demo", "SHA512"); Complete(panel.Calculate());
                    Check(output.Text == expected.Text && output.Text.Length == 128 && comparison.Text.Contains("✓"), "async algorithm change computes and compares SHA512");
                }

                using (var panel = new FileHashPanel(false))
                {
                    var output = Field<TextBox>(panel, "output"); var copy = Field<Button>(panel, "copy"); var status = Field<TextBlock>(panel, "status");
                    Check(!Field<Button>(panel, "calculate").IsEnabled, "live panel cannot calculate before explicit file selection");
                    SetFile(panel, source); Complete(panel.Calculate());
                    Check(output.Text == "" && !copy.IsEnabled && status.Text.Contains("无法读取文件") && Field<Button>(panel, "calculate").IsEnabled, "missing selected file returns error and restores controls without a stale result");
                    File.WriteAllBytes(source, Encoding.UTF8.GetBytes("temporary hash fixture\n你好"));
                    Complete(panel.Calculate()); Check(output.Text == Hash("temporary hash fixture\n你好", "SHA256") && copy.IsEnabled, "same selection recovers when missing file becomes available");
                    SetFile(panel, source); Check(output.Text == "" && !copy.IsEnabled, "file selection invalidates previous result even when the path is unchanged");
                    using (var locked = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                        Complete(panel.Calculate()); Check(output.Text == "" && !copy.IsEnabled && status.Text.Contains("无法读取文件"), "locked source error is contained by real async controls");
                    }
                    Complete(panel.Calculate()); Check(output.Text == Hash("temporary hash fixture\n你好", "SHA256") && copy.IsEnabled, "panel recovers after file lock releases");
                }

                // A bounded, exclusively test-owned file keeps the worker occupied long
                // enough to exercise the actual Cancel/Dispose path before UI completion.
                using (var stream = new FileStream(largeSource, FileMode.CreateNew, FileAccess.Write, FileShare.None)) stream.SetLength(128L * 1024 * 1024);
                using (var panel = new FileHashPanel(false))
                {
                    SetFile(panel, largeSource); Task work = panel.Calculate();
                    Check(Field<bool>(panel, "busy") && !Field<ComboBox>(panel, "algorithm").IsEnabled && !Field<Button>(panel, "choose").IsEnabled && !Field<Button>(panel, "copy").IsEnabled, "active worker disables file/algorithm/copy changes");
                    Complete(panel.Calculate()); Check(Field<bool>(panel, "busy"), "reentrant calculate does not start a second worker");
                    Invoke(panel, "Cancel"); Check(!Field<Button>(panel, "cancel").IsEnabled, "cancel request prevents repeated cancellation clicks"); Complete(work);
                    Check(Field<TextBox>(panel, "output").Text == "" && Field<string>(panel, "result") == null && !Field<Button>(panel, "copy").IsEnabled && Field<TextBlock>(panel, "status").Text.Contains("已取消"), "canceled job never publishes a digest or copyable stale result");
                    Check(!Field<bool>(panel, "busy") && Field<Button>(panel, "calculate").IsEnabled && Field<Button>(panel, "choose").IsEnabled && Field<ComboBox>(panel, "algorithm").IsEnabled, "cancel completion restores interactive controls");
                    using (var exclusive = new FileStream(largeSource, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Check(exclusive.Length == 128L * 1024 * 1024, "cancellation releases file handle without modifying source");
                    SetFile(panel, source); Complete(panel.Calculate()); Check(Field<TextBox>(panel, "output").Text == Hash("temporary hash fixture\n你好", "SHA256"), "small-file calculation recovers after canceled job");
                }
                var disposedPanel = new FileHashPanel(false);
                try
                {
                    SetFile(disposedPanel, largeSource); Task work = disposedPanel.Calculate(); disposedPanel.Dispose(); Complete(work);
                    Check(Field<TextBox>(disposedPanel, "output").Text == "" && Field<string>(disposedPanel, "result") == null, "disposing during a worker discards late result");
                    Complete(disposedPanel.Calculate()); Check(!Field<bool>(disposedPanel, "busy"), "disposed panel cannot start further work");
                    using (var exclusive = new FileStream(largeSource, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Check(exclusive.Length > 0, "disposal cancels worker and releases source handle");
                }
                finally { disposedPanel.Dispose(); }
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
                if (File.Exists(source)) File.Delete(source);
                if (File.Exists(largeSource)) File.Delete(largeSource);
            }
        }
        private static void ManifestLayoutTests()
        {
            string language = UiLocalization.Language, theme = UiTheme.Name;
            using (var panel = new FileHashPanel(true))
            {
                var scroll = new ScrollViewer { Content = panel, Padding = new Thickness(12), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
                var window = new Window { Content = scroll, Width = 320, Height = 520, ShowActivated = false, ShowInTaskbar = false };
                try
                {
                    AppAppearance.Register(window); window.Show();
                    var expected = Field<TextBox>(panel, "expected"); var comparison = Field<TextBlock>(panel, "comparison");
                    string digest = Hash("Side Island demo", "SHA256");
                    string content = String.Join("\n", Enumerable.Range(0, 20).Select(i => digest + "  文档 archive " + i + ".zip")) + "\n" + digest + "  first/Side Island demo\n" + digest + "  second/Side Island demo";
                    expected.Text = content;
                    foreach (string lang in new[] { "zh", "en" })
                    foreach (string appearance in new[] { "light", "dark" })
                    foreach (int width in new[] { 320, 480, 760 })
                    {
                        window.Width = width; AppAppearance.Configure(lang, appearance); window.UpdateLayout(); PumpLayout(); window.UpdateLayout();
                        Check(expected.Text == content && expected.AcceptsReturn && expected.MaxLength == 0, "appearance changes preserve full untruncated manifest");
                        Check(expected.ActualHeight <= 103 && expected.ExtentHeight > expected.ViewportHeight, "multiline manifest stays compact and scrolls");
                        Check(comparison.Text == UiLocalization.Localize("清单中存在同名文件或冲突校验值，请仅保留目标记录。"), "conflict status follows live language selection");
                        var formatted = new FormattedText(comparison.Text, UiLocalization.Culture, FlowDirection.LeftToRight, new Typeface(comparison.FontFamily, comparison.FontStyle, comparison.FontWeight, comparison.FontStretch), comparison.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(comparison).PixelsPerDip) { MaxTextWidth = Math.Max(1, comparison.ActualWidth) };
                        Check(comparison.ActualHeight + 2 >= formatted.Height, "long conflict status wraps without vertical cropping");
                        foreach (FrameworkElement control in panel.Children)
                        {
                            Point point = control.TranslatePoint(new Point(), panel);
                            Check(point.X >= -1 && point.X + control.ActualWidth <= panel.ActualWidth + 1, "populated checksum controls remain within narrow page");
                        }
                        expected.ScrollToHome(); window.UpdateLayout();
                        Rect first = expected.GetRectFromCharacterIndex(0);
                        Check(!first.IsEmpty && first.Top >= -1 && first.Bottom <= expected.ActualHeight + 1, "first manifest glyph remains visible");
                        expected.ScrollToEnd(); window.UpdateLayout();
                        Rect last = expected.GetRectFromCharacterIndex(expected.Text.Length - 1);
                        Check(!last.IsEmpty && last.Top >= -1 && last.Bottom <= expected.ActualHeight + 1, "last manifest glyph reachable without cropping");
                    }
                    Console.WriteLine("PASS: 12 populated checksum layouts, zh/en, light/dark, 320/480/760 widths; wrapped conflict text, unmodified manifest and reachable first/last glyphs.");
                }
                finally { window.Close(); AppAppearance.Configure(language, theme); }
            }
        }
        private static void PumpLayout() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
        private static T Field<T>(object target, string name) { return (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target); }
        private static void Invoke(object target, string name) { target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, null); }
        private static void SetFile(FileHashPanel panel, string path) { panel.GetType().GetMethod("SetFile", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(panel, new object[] { path }); }
        private static void Complete(Task work)
        {
            if (!work.IsCompleted)
            {
                var watch = Stopwatch.StartNew(); var frame = new DispatcherFrame(); var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(5) };
                timer.Tick += (s, e) => { if (work.IsCompleted || watch.ElapsedMilliseconds > 15000) frame.Continue = false; };
                timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
            }
            Check(work.IsCompleted, "async UI operation completed within timeout"); work.GetAwaiter().GetResult();
        }
        private static string Hash(string text, string algorithm) { using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(text))) return FileHashTools.Compute(stream, algorithm, CancellationToken.None); }
        private static void Check(bool value, string message) { if (!value) throw new Exception("File hash: " + message); }
    }
}
