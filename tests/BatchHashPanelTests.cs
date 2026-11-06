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
    internal static class BatchHashPanelTests
    {
        private static int checks;
        internal static void Run()
        {
            checks = 0; string root = Path.Combine(Path.GetTempPath(), "island-batch-ui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            string language = UiLocalization.Language, theme = UiTheme.Name;
            try
            {
                AppAppearance.Configure("zh", "dark"); RealFiles(root); SaveRaces(root); Cancellation(root); Modes(root); Layouts(root);
                Console.WriteLine("PASS: " + checks + " Windows batch checksum UI checks; actual owned files, partial failures, completed-result retention, cancellation/disposal and shared worker guard, stale/existing/deleted-source export guards, responsive asynchronous saving and 96 compact zh/en/theme/size/layout-scale matrices.");
            }
            finally { AppAppearance.Configure(language, theme); Directory.Delete(root, true); }
        }
        private static string FileAt(string root, string name, string text) { string path = Path.Combine(root, name); File.WriteAllText(path, text, new UTF8Encoding(false)); return path; }
        private static BatchHashPanel Panel(Func<string, string, string> destination = null, Action<string> copy = null, Action<string, string> save = null, IBatchHashProvider provider = null)
        { return new BatchHashPanel(false, destination ?? ((name, ext) => null), copy ?? (text => { }), save ?? FileHashTools.SaveManifestNew, provider); }
        private static void RealFiles(string root)
        {
            string first = FileAt(root, "first 中文.txt", "first"), second = FileAt(root, "second.txt", "second"), missing = Path.Combine(root, "missing");
            string copied = null, destination = Path.Combine(root, "batch.sha256");
            using (var panel = Panel((name, ext) => destination, text => copied = text))
            {
                panel.AcceptFiles(new[] { first, missing, second }); Complete(panel.Calculate());
                Check(panel.Result.Items.Count == 3 && panel.Result.Items[0].Status == BatchHashStatus.Completed && panel.Result.Items[1].Status == BatchHashStatus.Failed && panel.Result.Items[2].Status == BatchHashStatus.Completed, "missing file remains in ordered list without losing completed neighbors");
                Check(Field<ListBox>(panel, "results").Items.Count == 3 && !Field<Button>(panel, "copyManifest").IsEnabled, "partial batch stays visible and cannot export as complete");
                var missingRow = (ListBoxItem)Field<ListBox>(panel, "results").Items[1]; var missingLabel = (TextBlock)((StackPanel)missingRow.Content).Children[1];
                AppAppearance.Configure("en", "dark"); Check(missingLabel.Text == "File not found", "missing-file result follows English language selection");
                AppAppearance.Configure("zh", "dark"); Check(missingLabel.Text == "找不到文件", "missing-file result returns to Chinese without changing user paths");
                Field<ListBox>(panel, "results").SelectedIndex = 2; Check(Field<TextBox>(panel, "digest").Text == panel.Result.Items[2].Digest, "selected completed checksum is available even after another file failed");
                panel.AcceptFiles(new[] { first, second }); Complete(panel.Calculate());
                string manifest, error; Check(BatchHashTools.TryCreateManifest(panel.Result, out manifest, out error), "valid batch manifest available");
                Field<TextBox>(panel, "expected").Text = manifest; Complete(panel.Calculate());
                Check(panel.Result.Items.All(item => item.Verification == BatchHashVerification.Matched), "real UI verifies every filename in a GNU list");
                Click(Field<Button>(panel, "copyManifest")); Check(copied == manifest, "explicit copy gets exact combined manifest");
                Complete(panel.SaveManifest()); Check(File.ReadAllText(destination) == manifest, "explicit save publishes exact real new-file bytes");
                Complete(panel.SaveManifest()); Check(File.ReadAllText(destination) == manifest && Field<TextBlock>(panel, "status").Text.Contains("未保存"), "existing destination is preserved with honest status");
                Field<TextBox>(panel, "expected").Text = new string(' ', FileHashTools.MaximumManifestLength + 1); Complete(panel.Calculate());
                Check(panel.Result.Items.All(item => item.ExpectedError == "too-large") && Field<TextBox>(panel, "expected").Text.Length == FileHashTools.MaximumManifestLength + 1, "full oversized input reaches bounded backend without silent truncation");
            }
        }
        private static void SaveRaces(string root)
        {
            string file = FileAt(root, "source-to-remove.txt", "owned source"), destination = Path.Combine(root, "should-not-save.sha256"); int writes = 0;
            using (var panel = Panel((name, ext) => file, save: (path, text) => writes++))
            {
                panel.AcceptFiles(new[] { file }); Complete(panel.Calculate()); File.Delete(file); Complete(panel.SaveManifest());
                Check(writes == 0 && !File.Exists(file) && Field<TextBlock>(panel, "status").Text.Contains("源文件"), "deleted source path cannot be recreated as a checksum manifest");
            }
            file = FileAt(root, "stale-source.txt", "owned source"); BatchHashPanel stale = null;
            using (stale = Panel((name, ext) => { Field<ComboBox>(stale, "algorithm").SelectedIndex = 1; return destination; }, save: (path, text) => writes++))
            {
                stale.AcceptFiles(new[] { file }); Complete(stale.Calculate()); Complete(stale.SaveManifest());
                Check(writes == 0 && stale.Result == null && !File.Exists(destination), "picker input change invalidates export before worker starts");
            }
            BatchHashPanel closing = null;
            using (closing = Panel((name, ext) => { closing.Dispose(); return destination; }, save: (path, text) => writes++))
            {
                closing.AcceptFiles(new[] { file }); Complete(closing.Calculate()); Complete(closing.SaveManifest());
                Check(writes == 0 && !File.Exists(destination), "picker close/dispose prevents late publication");
            }
            using (var entered = new ManualResetEventSlim()) using (var release = new ManualResetEventSlim())
            using (var blocked = Panel((name, ext) => destination, save: (path, text) => { Interlocked.Increment(ref writes); entered.Set(); if (!release.Wait(10000)) throw new IOException("fixture timeout"); FileHashTools.SaveManifestNew(path, text); }))
            {
                blocked.AcceptFiles(new[] { file }); Complete(blocked.Calculate()); Task work = blocked.SaveManifest();
                try
                {
                    Check(entered.Wait(5000), "owned save enters blocked worker");
                    var clock = Stopwatch.StartNew(); Pump(); Check(clock.ElapsedMilliseconds < 500, "UI dispatcher stays responsive while save is blocked");
                    Complete(blocked.SaveManifest()); Complete(blocked.Calculate()); Check(writes == 1, "duplicate save and calculate are suppressed during publication");
                    blocked.Dispose();
                }
                finally { release.Set(); Complete(work); }
                Check(File.Exists(destination) && writes == 1, "already authorized worker save finishes after UI disposal");
            }
        }
        private sealed class BlockingProvider : IBatchHashProvider, IDisposable
        {
            internal readonly ManualResetEventSlim Entered = new ManualResetEventSlim(), Release = new ManualResetEventSlim();
            internal int Opened, Active;
            private readonly int blockOn;
            internal BlockingProvider(int block) { blockOn = block; }
            public BatchHashSource Open(string path) { Opened++; Active++; return new Source(this, Opened == blockOn); }
            private sealed class Source : BatchHashSource
            {
                private readonly BlockingProvider owner; private readonly WaitingStream stream;
                internal Source(BlockingProvider owner, bool block) { this.owner = owner; stream = new WaitingStream(owner, block); }
                public override Stream Stream { get { return stream; } }
                public override BatchHashStamp ReadStamp() { return new BatchHashStamp("owned-stream", 1, 1, 0); }
                public override BatchHashStamp ReadPathStamp() { return ReadStamp(); }
                public override void Dispose() { stream.Dispose(); owner.Active--; }
            }
            private sealed class WaitingStream : MemoryStream
            {
                private readonly BlockingProvider owner; private bool block;
                internal WaitingStream(BlockingProvider owner, bool block) : base(new byte[] { 8 }) { this.owner = owner; this.block = block; }
                public override int Read(byte[] buffer, int offset, int count) { if (block) { block = false; owner.Entered.Set(); if (!owner.Release.Wait(10000)) throw new IOException("fixture timeout"); } return base.Read(buffer, offset, count); }
            }
            public void Dispose() { Release.Set(); Entered.Dispose(); Release.Dispose(); }
        }
        private static void Cancellation(string root)
        {
            string[] paths = new[] { "first-probe", "second-probe", "third-probe" }.Select(name => Path.Combine(root, name)).ToArray();
            using (var provider = new BlockingProvider(2)) using (var panel = Panel(provider: provider))
            {
                panel.AcceptFiles(paths); Task work = panel.Calculate();
                try
                {
                    Check(provider.Entered.Wait(5000), "second owned read blocks after first completion"); Pump(); panel.Cancel();
                    using (var other = Panel(provider: provider))
                    {
                        other.AcceptFiles(paths); Complete(other.Calculate());
                        Check(provider.Opened == 2 && other.Result == null && Field<TextBlock>(other, "status").Text.Contains("尚未结束"), "shared read guard prevents replacement workers before cancellation unwinds");
                    }
                }
                finally { provider.Release.Set(); Complete(work); }
                Check(panel.Result.Cancelled && panel.Result.Items[0].Status == BatchHashStatus.Completed && panel.Result.Items[1].Status == BatchHashStatus.Cancelled && panel.Result.Items[2].Status == BatchHashStatus.NotStarted, "cancel preserves completed/current/unstarted rows separately");
                Check(provider.Opened == 2 && provider.Active == 0 && Field<ListBox>(panel, "results").Items.Count == 3, "cancel has no later IO or leaked active source");
                Complete(panel.Calculate()); Check(panel.Result.Items.All(item => item.Status == BatchHashStatus.Completed), "a fresh request works after the cancelled worker exits");
            }
            using (var provider = new BlockingProvider(1)) using (var panel = Panel(provider: provider))
            {
                panel.AcceptFiles(paths); Task work = panel.Calculate();
                try { Check(provider.Entered.Wait(5000), "close test reaches blocked read"); panel.Dispose(); }
                finally { provider.Release.Set(); Complete(work); }
                Check(panel.Result == null && provider.Active == 0 && Field<ListBox>(panel, "results").Items.Count == 0, "disposed panel ignores all late result callbacks");
            }
        }
        private static void Modes(string root)
        {
            using (var panel = new FileHashPanel(true))
            {
                var batch = Field<BatchHashPanel>(panel, "batchContent"); panel.ShowBatch(true);
                Check(batch.Visibility == Visibility.Visible && Field<StackPanel>(panel, "singleContent").Visibility == Visibility.Collapsed, "batch mode shares the existing hash tool");
                batch.AcceptFiles(new[] { Path.Combine(root, "never-read") }); Complete(batch.Calculate());
                Check(batch.Result == null && !Field<Button>(batch, "choose").IsEnabled, "demo batch mode never reads files or opens a picker");
                panel.ShowBatch(false); Complete(panel.Calculate());
                Check(Field<TextBox>(panel, "output").Text.Length == 64 && batch.Visibility == Visibility.Collapsed, "existing single-file demo still computes after mode switch");
            }
        }
        private static void Layouts(string root)
        {
            string first = FileAt(root, "保存", "long filename UI"), second = FileAt(root, "long-selected-文件-name-with-details.txt", "second");
            using (var panel = Panel())
            {
                panel.AcceptFiles(new[] { first, second }); Field<TextBox>(panel, "expected").Text = new string('a', 64); Complete(panel.Calculate());
                var scroll = new ScrollViewer { Content = panel, Padding = new Thickness(12), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
                var window = new Window { Content = scroll, ShowActivated = false, ShowInTaskbar = false, Width = 480, Height = 720 };
                try
                {
                    AppAppearance.Register(window); window.Show();
                    foreach (string language in new[] { "zh", "en" }) foreach (string theme in new[] { "dark", "light" })
                    foreach (double width in new[] { 320.0, 480.0, 760.0 }) foreach (double height in new[] { 360.0, 720.0 }) foreach (double scale in new[] { 1.0, 1.25, 1.5, 2.0 })
                    {
                        window.Width = width; window.Height = height; scroll.LayoutTransform = new ScaleTransform(scale, scale); AppAppearance.Configure(language, theme); window.UpdateLayout(); Pump(); window.UpdateLayout();
                        foreach (string[] pair in new[] { new[] { "choose", "clear" }, new[] { "calculate", "cancel" }, new[] { "copyManifest", "saveManifest" } })
                        {
                            var a = Field<Button>(panel, pair[0]); var b = Field<Button>(panel, pair[1]);
                            Check(Math.Abs(a.ActualWidth - b.ActualWidth) < 1 && Math.Abs(a.ActualHeight - b.ActualHeight) < 1, "same-group actions keep equal sizes at each width and scale");
                            CheckCaptions(a); CheckCaptions(b);
                        }
                        foreach (FrameworkElement control in panel.Children)
                        { Point position = control.TranslatePoint(new Point(), panel); Check(position.X >= -1 && position.X + control.ActualWidth <= panel.ActualWidth + 1, "batch controls remain inside narrow/scaled page"); }
                        var row = (ListBoxItem)Field<ListBox>(panel, "results").Items[0]; var content = (StackPanel)row.Content;
                        Check(((TextBlock)content.Children[0]).Text == "保存", "language switch never translates user filename");
                        Check(((TextBlock)content.Children[1]).Text == UiLocalization.Localize("多个文件需要校验清单，不能共用单个校验值。"), "per-file status follows live Chinese/English changes");
                        Check(Field<TextBox>(panel, "expected").Text == new string('a', 64), "language/theme/layout changes keep manifest unchanged");
                    }
                }
                finally { window.Close(); }
            }
        }
        private static void CheckCaptions(DependencyObject node)
        {
            var text = node as TextBlock;
            if (text != null && !String.IsNullOrEmpty(text.Text))
            {
                var measured = new FormattedText(text.Text, UiLocalization.Culture, FlowDirection.LeftToRight, new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(text).PixelsPerDip) { MaxTextWidth = Math.Max(1, text.ActualWidth) };
                Check(text.ActualHeight + 2 >= measured.Height, "wrapped action caption has sufficient height");
            }
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++) CheckCaptions(VisualTreeHelper.GetChild(node, index));
        }
        private static T Field<T>(object target, string name) { return (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target); }
        private static void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); }
        private static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
        private static void Complete(Task task)
        {
            var watch = Stopwatch.StartNew(); while (!task.IsCompleted && watch.ElapsedMilliseconds < 20000) { Pump(); Thread.Sleep(1); }
            Check(task.IsCompleted, "async UI operation completes"); task.GetAwaiter().GetResult(); Pump();
        }
        private static void Check(bool value, string message) { if (!value) throw new Exception("Batch hash UI: " + message); checks++; }
    }
}
