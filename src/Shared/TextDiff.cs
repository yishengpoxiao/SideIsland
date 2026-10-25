using System;
using System.Collections.Generic;
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
