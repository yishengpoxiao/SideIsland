using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Threading;

namespace CodexIsland
{
    internal static class TextDiffTests
    {
        public static void Run()
        {
            var unchanged = TextDiff.Compare("甲😀\r\n\r\n重复\r\n重复\r\n", "甲😀\n\n重复\n重复\n");
            Check(unchanged.Identical && unchanged.Rows.Count == 5, "Unicode, blank lines, duplicate lines and trailing newline survive comparison");
            Check(TextDiff.Compare("", "").Identical, "Empty texts compare equal");
            Check(TextDiff.Compare("", "新增").Added == 1 && TextDiff.Compare("删除", "").Removed == 1, "Empty side produces addition or deletion");
            var change = TextDiff.Compare("keep\nold\nend", "keep\nnew\nend\nextra");
            Check(change.Modified == 1 && change.Added == 1 && change.Removed == 0, "Replacement and appended line are identified separately");
            Check(change.Rows[1].LeftNumber == 2 && change.Rows[1].RightNumber == 2 && change.Rows[3].LeftNumber == null && change.Rows[3].RightNumber == 4, "Source line numbers remain accurate across unmatched blocks");
            Check(TextDiff.Compare("a\rb", "a\nb").Identical, "Classic Mac newline is normalized");
            Check(!TextDiff.Compare(" a", "a").Identical, "Whitespace remains meaningful");
            Check(!TextDiff.Compare("A", "a").Identical, "Text comparison is case sensitive");
            Reject(() => TextDiff.Compare(new string('x', TextDiff.MaximumCharacters + 1), ""), "Character limit rejects large input before allocating an LCS matrix");
            Reject(() => TextDiff.Compare(new string('\n', TextDiff.MaximumLines), ""), "Line limit rejects excessive blank lines");
            var cancellation = new CancellationTokenSource(); cancellation.Cancel(); bool cancelled = false;
            try { TextDiff.Compare("a", "b", cancellation.Token); } catch (OperationCanceledException) { cancelled = true; } finally { cancellation.Dispose(); }
            Check(cancelled, "Cancelled comparison does not produce a result");
            var random = new Random(1729);
            for (int i = 0; i < 150; i++)
            {
                string a = String.Join("\n", Enumerable.Range(0, random.Next(1, 35)).Select(n => new[] { "", "a", "b", "重复😀", "配置=true" }[random.Next(5)]));
                string b = String.Join("\n", Enumerable.Range(0, random.Next(1, 35)).Select(n => new[] { "", "a", "b", "重复😀", "配置=true" }[random.Next(5)]));
                var result = TextDiff.Compare(a, b);
                Check(String.Join("\n", result.Rows.Where(r => r.LeftNumber.HasValue).Select(r => r.Left)) == a && String.Join("\n", result.Rows.Where(r => r.RightNumber.HasValue).Select(r => r.Right)) == b, "Random repeated-line edit keeps complete inputs and ordering");
            }
            var bounded = TextDiff.Compare(String.Join("\n", Enumerable.Range(0, 1600).Select(i => "old" + i)), String.Join("\n", Enumerable.Range(0, 1600).Select(i => "new" + i)));
            Check(bounded.Modified == 1600, "Maximum supported completely different input completes with bounded memory");
            ExportTests();
            PanelTests();
            Console.WriteLine("PASS: text comparison limits, Unicode, edits, cancellation and stale result tests.");
        }
        private static void ExportTests()
        {
            string added = TextDiff.ExportUnified("", "新增😀", CancellationToken.None);
            Check(added == "--- a/text.txt\n+++ b/text.txt\n@@ -0,0 +1,1 @@\n+新增😀\n\\ No newline at end of file\n", "empty input exports a valid addition with unterminated-line marker");
            string removed = TextDiff.ExportUnified("old\n", "", CancellationToken.None);
            Check(removed == "--- a/text.txt\n+++ b/text.txt\n@@ -1,1 +0,0 @@\n-old\n", "empty target exports correct zero-count hunk");
            Check(TextDiff.ExportUnified("a\r\nb\r", "a\nb\n", CancellationToken.None) == "", "line-ending normalization alone produces no patch");
            string newline = TextDiff.ExportUnified("a", "a\n", CancellationToken.None);
            Check(newline.Contains("-a\n\\ No newline at end of file\n+a\n") && !newline.Contains("+\n"), "final LF change is not exported as a phantom blank line");
            string longLine = new string('长', 2300) + "😀";
            string many = String.Join("\n", Enumerable.Range(0, 700).Select(i => "line" + i));
            string patch = TextDiff.ExportUnified(many, many + "\n" + longLine, CancellationToken.None);
            Check(patch.Contains(longLine) && patch.Contains("line699"), "export retains long lines and changes beyond the UI preview bound");
            string source = String.Join("\n", Enumerable.Range(0, 40).Select(i => "row " + i)) + "\n";
            string hunks = TextDiff.ExportUnified(source, source.Replace("row 3\n", "new 3\n").Replace("row 35\n", "new 35\n"), CancellationToken.None);
            Check(hunks.Split(new[] { "@@ -" }, StringSplitOptions.None).Length == 3 && !hunks.Contains(" row 20\n"), "distant changes use separate three-context-line hunks");
            string folder = Path.Combine(Path.GetTempPath(), "island-diff-export-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            try
            {
                string path = Path.Combine(folder, "comparison.patch"); TextDiff.SaveUnified(path, patch);
                Check(File.ReadAllBytes(path).SequenceEqual(new UTF8Encoding(false, true).GetBytes(patch)), "actual saved patch is complete UTF-8 without BOM");
                TextDiff.SaveUnified(path, added); Check(File.ReadAllText(path) == added, "explicit save atomically replaces an existing output");
                bool failed = false; try { TextDiff.SaveUnified(path, "bad\ud800"); } catch (EncoderFallbackException) { failed = true; }
                Check(failed && File.ReadAllText(path) == added, "invalid UTF-16 cannot truncate the previous output");
                Check(Directory.GetFiles(folder, "*.tmp").Length == 0, "atomic export removes temporary files");
            }
            finally { Directory.Delete(folder, true); }
            var cancellation = new CancellationTokenSource(); cancellation.Cancel(); bool cancelled = false;
            try { TextDiff.ExportUnified("old", "new", cancellation.Token); } catch (OperationCanceledException) { cancelled = true; } finally { cancellation.Dispose(); }
            Check(cancelled, "export cancellation is honored");
        }
        private static void PanelTests()
        {
            var previous = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try
            {
            using (var panel = new TextDiffPanel(false))
            {
                var left = Field<TextBox>(panel, "left"); var right = Field<TextBox>(panel, "right");
                left.Text = String.Join("\n", Enumerable.Range(0, 1600).Select(i => "old" + i)); right.Text = String.Join("\n", Enumerable.Range(0, 1600).Select(i => "new" + i));
                Task stale = panel.CompareNowAsync(); left.Text = "最新😀"; right.Text = "最新😀"; Task newest = panel.CompareNowAsync();
                Pump(Task.WhenAll(stale, newest));
                Check(panel.LastResult != null && panel.LastResult.Identical && panel.LastResult.Rows.Count == 1 && panel.LastResult.Rows[0].Left == "最新😀", "Older background result cannot overwrite the latest input");
                Check(panel.UnifiedPatch == "" && !Field<Button>(panel, "copyPatch").IsEnabled && !Field<Button>(panel, "savePatch").IsEnabled, "identical text disables patch actions");
                right.Text = "新文本😀\n"; Pump(panel.CompareNowAsync());
                Check(panel.UnifiedPatch.Contains("+新文本😀\n") && Field<Button>(panel, "copyPatch").IsEnabled && Field<Button>(panel, "savePatch").IsEnabled, "successful comparison publishes full export and enables actions");
                left.Text = "changed"; Check(panel.LastResult == null, "Input edit invalidates the previous comparison immediately");
                Check(panel.UnifiedPatch == null && !Field<Button>(panel, "copyPatch").IsEnabled && !Field<Button>(panel, "savePatch").IsEnabled, "input changes immediately disable stale export");
                panel.Dispose(); Pump(stale); Check(panel.LastResult == null && left.Text.Length == 0 && right.Text.Length == 0, "Disposal clears transient text and prevents later result publication");
            }
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }
        private static T Field<T>(object instance, string name) { return (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(instance); }
        private static void Pump(Task task)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(8);
            while (!task.IsCompleted && DateTime.UtcNow < deadline)
            {
                var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); Thread.Sleep(1);
            }
            if (task.IsFaulted) throw new Exception("Async comparison failed: " + task.Exception.GetBaseException().ToString());
            Check(task.IsCompleted, "Async comparison completes without failure");
        }
        private static void Reject(Action action, string message) { bool failed = false; try { action(); } catch (ArgumentException) { failed = true; } Check(failed, message); }
        private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    }
}
