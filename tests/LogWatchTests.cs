using System;
using System.IO;
using System.Linq;
using System.Text;

namespace CodexIsland
{
    internal static class LogWatchTests
    {
        public static void Run()
        {
            string directory = Path.Combine(Path.GetTempPath(), "island-log-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            try
            {
                string path = Path.Combine(directory, "app.log"); DateTime now = DateTime.UtcNow;
                using (var reader = new LogWatchReader(path, () => now))
                {
                    reader.SetKeywords("error, 错误"); Check(reader.Read().Missing, "Missing logs wait without throwing");
                    File.WriteAllText(path, "old error\n配置😀\n", new UTF8Encoding(false)); var initial = reader.Read();
                    Check(initial.Lines.SequenceEqual(new[] { "old error", "配置😀" }) && initial.Alert == null, "Initial UTF-8 tail preserves Unicode and does not notify historical errors");
                    Append(path, "new ERROR\n"); var changed = reader.Read(); Check(changed.Alert == "new ERROR" && changed.Lines.Last() == "new ERROR", "New keyword matches generate an alert");
                    Append(path, "错误 repeated\n"); Check(reader.Read().Alert == null, "Rapid keyword matches are rate limited"); now = now.AddSeconds(11);
                    Append(path, "错误 later\n"); Check(reader.Read().Alert == "错误 later", "Alerts resume after the throttle interval");
                    byte[] emoji = Encoding.UTF8.GetBytes("😀"); using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) stream.Write(emoji, 0, 2);
                    reader.Read(); using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { stream.Write(emoji, 2, 2); stream.WriteByte(10); }
                    Check(reader.Read().Lines.Last() == "😀", "Decoder retains incomplete UTF-8 sequences across reads");
                    File.WriteAllText(path, "short\n", new UTF8Encoding(false)); var truncated = reader.Read(); Check(truncated.Reset && truncated.Lines.SequenceEqual(new[] { "short" }), "Truncation resets offset and stale lines");
                    File.WriteAllText(path, "rewritten with larger content\n", new UTF8Encoding(false)); var rewritten = reader.Read(); Check(rewritten.Reset && rewritten.Lines.SequenceEqual(new[] { "rewritten with larger content" }), "In-place rewrite is detected even when the file regrows past the previous offset");
                    string rotated = Path.Combine(directory, "old.log"); File.Move(path, rotated); File.WriteAllText(path, "new identity\n", new UTF8Encoding(false)); var replacement = reader.Read();
                    Check(replacement.Reset && replacement.Lines.SequenceEqual(new[] { "new identity" }), "File identity detects rotation even when the replacement is larger");
                    reader.Clear(); Check(reader.Read().Lines.Count == 0, "Clear removes the view without rereading the original file"); Append(path, "after clear\n"); Check(reader.Read().Lines.Single() == "after clear", "New content continues from the existing offset after clear");
                }
                using (var reader = new LogWatchReader(path))
                {
                    File.WriteAllText(path, "", new UTF8Encoding(false)); reader.Read();
                    File.WriteAllText(path, "甲\r\n乙\r丙\n末尾", new UnicodeEncoding(false, true)); Check(reader.Read().Lines.SequenceEqual(new[] { "甲", "乙", "丙", "末尾" }), "UTF-16 BOM and CRLF, CR, LF are decoded without duplicate lines");
                }
                using (var reader = new LogWatchReader(path))
                {
                    File.WriteAllText(path, "首行😀\n末尾", new UTF32Encoding(true, true)); Check(reader.Read().Lines.SequenceEqual(new[] { "首行😀", "末尾" }), "UTF-32 big-endian BOM is supported");
                }
                using (var reader = new LogWatchReader(path))
                {
                    File.WriteAllText(path, new string('x', 200000) + "\nlast\n", new UTF8Encoding(false)); var tail = reader.Read(); Check(tail.Lines.Last() == "last" && tail.Lines.Count == 1 && tail.Offset > 100000, "Huge existing logs read a bounded tail and discard the partial first line");
                    for (int round = 0; round < 4; round++) { Append(path, String.Join("\n", Enumerable.Range(0, 600).Select(i => "line " + round + " " + i)) + "\n"); reader.Read(); }
                    Check(reader.Read().Lines.Count <= LogWatchReader.MaximumLines, "Long-running tail buffers retain a bounded number of lines");
                }
                Check(File.ReadAllText(path).Contains("line 3 599"), "Readers never modify source logs");
                LiveBoundaryAlerts(path);
            }
            finally { Directory.Delete(directory, true); }
            Console.WriteLine("PASS: live log Unicode decoding, append offsets, missing/replacement/truncation recovery, historical partial suppression, first append alerts, logical-line deduplication, keyword throttling and bounded tail tests.");
        }
        private static void LiveBoundaryAlerts(string path)
        {
            DateTime now = DateTime.UtcNow;
            File.WriteAllText(path, "historical error", new UTF8Encoding(false));
            using (var reader = new LogWatchReader(path, () => now))
            {
                reader.SetKeywords("error"); Check(reader.Read().Alert == null, "Initial unfinished historical errors do not alert");
                now = now.AddSeconds(11); Check(reader.Read().Alert == null, "An idle poll cannot alert an unchanged historical partial line");
                Append(path, " detail\n"); Check(reader.Read().Alert == null, "Finishing a historical matched line does not replay its error");
                Append(path, "new error"); Check(reader.Read().Alert == "new error", "A new unfinished matching line alerts before its newline");
                now = now.AddSeconds(11); Append(path, " more detail"); Check(reader.Read().Alert == null, "Extending an already matched line does not repeat its alert after the throttle");
                Append(path, "\n"); Check(reader.Read().Alert == null, "Completing an already matched line does not repeat its alert");
                Append(path, "next error\n"); Check(reader.Read().Alert == "next error", "Deduplication ends at the logical line boundary");
            }
            File.WriteAllText(path, "historical err", new UTF8Encoding(false));
            using (var reader = new LogWatchReader(path, () => now))
            {
                reader.SetKeywords("error"); reader.Read(); Append(path, "or\n");
                Check(reader.Read().Alert == "historical error", "A keyword first completed by new bytes still alerts across the live boundary");
            }
            foreach (Encoding encoding in new Encoding[] { new UTF8Encoding(false), new UnicodeEncoding(false, true), new UTF32Encoding(true, true) })
            {
                File.WriteAllBytes(path, new byte[0]);
                using (var reader = new LogWatchReader(path, () => now))
                {
                    reader.SetKeywords("error"); reader.Read(); File.WriteAllText(path, "first error\n", encoding);
                    var first = reader.Read(); Check(first.Alert == "first error" && first.Lines.SequenceEqual(new[] { "first error" }), "The first append to an observed empty file detects its encoding and alerts: " + encoding.WebName);
                }
            }
            File.WriteAllText(path, "historical failure", new UTF8Encoding(false));
            using (var reader = new LogWatchReader(path, () => now))
            {
                reader.SetKeywords("error"); reader.Read(); reader.SetKeywords("failure");
                Check(reader.Read().Alert == null, "Updating keywords does not retroactively alert an existing partial line");
                Append(path, "\nnew failure\n"); Check(reader.Read().Alert == "new failure", "Updated keywords apply to newly arriving lines only");
                File.WriteAllText(path, "rotated failure", new UTF8Encoding(false)); Check(reader.Read().Reset && reader.Read().Alert == null, "A truncated historical partial line is not replayed after resetting the decoder");
            }
        }
        private static void Append(string path, string value) { File.AppendAllText(path, value, new UTF8Encoding(false)); }
        private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    }
}
