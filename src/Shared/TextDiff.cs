using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace CodexIsland
{
    internal enum TextDiffKind { Equal, Added, Removed, Modified }
    internal sealed class TextDiffRow
    {
        public string Left { get; set; }
        public string Right { get; set; }
        public int? LeftNumber { get; set; }
        public int? RightNumber { get; set; }
        public TextDiffKind Kind { get; set; }
    }
    internal sealed class TextDiffResult
    {
        public readonly List<TextDiffRow> Rows = new List<TextDiffRow>();
        public int Added { get; set; }
        public int Removed { get; set; }
        public int Modified { get; set; }
        public bool Identical { get { return Added == 0 && Removed == 0 && Modified == 0; } }
    }
    internal static class TextDiff
    {
        public const int MaximumCharacters = 120000;
        public const int MaximumLines = 1600;

        public static TextDiffResult Compare(string left, string right) { return Compare(left, right, CancellationToken.None); }
        public static TextDiffResult Compare(string left, string right, CancellationToken cancellation)
        {
            string[] a = Split(left), b = Split(right);
            return CompareLines(a, b, cancellation);
        }
        private static TextDiffResult CompareLines(string[] a, string[] b, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            // A hard input limit bounds both CPU and memory: at most 5.2 MB of LCS cells.
            var table = new ushort[a.Length + 1, b.Length + 1];
            for (int i = a.Length - 1; i >= 0; i--)
            {
                cancellation.ThrowIfCancellationRequested();
                for (int j = b.Length - 1; j >= 0; j--)
                    table[i, j] = String.Equals(a[i], b[j], StringComparison.Ordinal) ? (ushort)(table[i + 1, j + 1] + 1) : Math.Max(table[i + 1, j], table[i, j + 1]);
            }
            var result = new TextDiffResult(); int ai = 0, bi = 0;
            while (ai < a.Length || bi < b.Length)
            {
                cancellation.ThrowIfCancellationRequested();
                if (ai < a.Length && bi < b.Length && String.Equals(a[ai], b[bi], StringComparison.Ordinal))
                {
                    result.Rows.Add(new TextDiffRow { Left = a[ai], Right = b[bi], LeftNumber = ai + 1, RightNumber = bi + 1, Kind = TextDiffKind.Equal }); ai++; bi++; continue;
                }
                int aStart = ai, bStart = bi;
                while ((ai < a.Length || bi < b.Length) && !(ai < a.Length && bi < b.Length && String.Equals(a[ai], b[bi], StringComparison.Ordinal)))
                {
                    if (ai < a.Length && (bi >= b.Length || table[ai + 1, bi] >= table[ai, bi + 1])) ai++;
                    else bi++;
                }
                int length = Math.Max(ai - aStart, bi - bStart);
                for (int n = 0; n < length; n++)
                {
                    bool hasLeft = aStart + n < ai, hasRight = bStart + n < bi;
                    var kind = hasLeft && hasRight ? TextDiffKind.Modified : hasLeft ? TextDiffKind.Removed : TextDiffKind.Added;
                    result.Rows.Add(new TextDiffRow { Left = hasLeft ? a[aStart + n] : null, Right = hasRight ? b[bStart + n] : null, LeftNumber = hasLeft ? (int?)(aStart + n + 1) : null, RightNumber = hasRight ? (int?)(bStart + n + 1) : null, Kind = kind });
                    if (kind == TextDiffKind.Modified) result.Modified++; else if (kind == TextDiffKind.Removed) result.Removed++; else result.Added++;
                }
            }
            return result;
        }

        private sealed class PatchLine
        {
            internal char Prefix; internal string Text;
            internal PatchLine(char prefix, string text) { Prefix = prefix; Text = text; }
        }

        // Physical lines retain their LF terminator as part of the comparison
        // key. This distinguishes a missing final newline from an extra blank
        // line without changing the existing side-by-side display semantics.
        public static string ExportUnified(string left, string right, CancellationToken cancellation)
        {
            var result = CompareLines(PhysicalLines(left), PhysicalLines(right), cancellation);
            if (result.Identical) return "";
            var lines = new List<PatchLine>();
            for (int i = 0; i < result.Rows.Count;)
            {
                cancellation.ThrowIfCancellationRequested();
                if (result.Rows[i].Kind == TextDiffKind.Equal) { lines.Add(new PatchLine(' ', result.Rows[i++].Left)); continue; }
                int end = i;
                while (end < result.Rows.Count && result.Rows[end].Kind != TextDiffKind.Equal) end++;
                for (int n = i; n < end; n++) if (result.Rows[n].LeftNumber.HasValue) lines.Add(new PatchLine('-', result.Rows[n].Left));
                for (int n = i; n < end; n++) if (result.Rows[n].RightNumber.HasValue) lines.Add(new PatchLine('+', result.Rows[n].Right));
                i = end;
            }
            var ranges = new List<int[]>();
            for (int i = 0; i < lines.Count; i++) if (lines[i].Prefix != ' ')
            {
                int start = Math.Max(0, i - 3), end = Math.Min(lines.Count, i + 4);
                if (ranges.Count > 0 && start <= ranges[ranges.Count - 1][1]) ranges[ranges.Count - 1][1] = end;
                else ranges.Add(new[] { start, end });
            }
            int[] oldBefore = new int[lines.Count + 1], newBefore = new int[lines.Count + 1];
            for (int i = 0; i < lines.Count; i++)
            { oldBefore[i + 1] = oldBefore[i] + (lines[i].Prefix == '+' ? 0 : 1); newBefore[i + 1] = newBefore[i] + (lines[i].Prefix == '-' ? 0 : 1); }
            var output = new StringBuilder("--- a/text.txt\n+++ b/text.txt\n");
            foreach (var range in ranges)
            {
                cancellation.ThrowIfCancellationRequested();
                int start = range[0], end = range[1], oldCount = oldBefore[end] - oldBefore[start], newCount = newBefore[end] - newBefore[start];
                output.Append("@@ -").Append((oldBefore[start] + (oldCount == 0 ? 0 : 1)).ToString(CultureInfo.InvariantCulture)).Append(',').Append(oldCount.ToString(CultureInfo.InvariantCulture));
                output.Append(" +").Append((newBefore[start] + (newCount == 0 ? 0 : 1)).ToString(CultureInfo.InvariantCulture)).Append(',').Append(newCount.ToString(CultureInfo.InvariantCulture)).Append(" @@\n");
                for (int i = start; i < end; i++)
                {
                    output.Append(lines[i].Prefix).Append(lines[i].Text);
                    if (!lines[i].Text.EndsWith("\n", StringComparison.Ordinal)) output.Append("\n\\ No newline at end of file\n");
                }
            }
            return output.ToString();
        }
        private static string[] PhysicalLines(string value)
        {
            var split = Split(value); var lines = new List<string>();
            for (int i = 0; i < split.Length; i++)
                if (i + 1 < split.Length) lines.Add(split[i] + "\n"); else if (split[i].Length > 0) lines.Add(split[i]);
            return lines.ToArray();
        }
        public static void SaveUnified(string path, string patch)
        {
            if (String.IsNullOrEmpty(patch)) throw new ArgumentException("没有可导出的差异。");
            // Encoding fails before creating or replacing any file.
            byte[] bytes = new UTF8Encoding(false, true).GetBytes(patch);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        private static string[] Split(string value)
        {
            value = value ?? "";
            if (value.Length > MaximumCharacters) throw new ArgumentException("每侧最多支持 120,000 个字符，请缩短文本后再对照。");
            if (value.Length == 0) return new string[0];
            string normalized = value.Replace("\r\n", "\n").Replace('\r', '\n');
            string[] lines = normalized.Split(new[] { '\n' });
            if (lines.Length > MaximumLines) throw new ArgumentException("每侧最多支持 1,600 行，请分段对照。");
            return lines;
        }
    }
}
