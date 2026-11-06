using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CodexIsland
{
    internal sealed class LogWatchSnapshot
    {
        public string Path, Status, Alert;
        public List<string> Lines = new List<string>();
        public long Offset;
        public bool Missing, Reset;
    }
    internal sealed class LogWatchReader : IDisposable
    {
        public const int MaximumLines = 1000, MaximumLineLength = 4000, MaximumBufferCharacters = 512 * 1024, MaximumReadBytes = 64 * 1024;
        private readonly object gate = new object();
        private readonly List<string> lines = new List<string>();
        private readonly Func<DateTime> clock;
        private string path, fileIdentity, partial = "";
        private long offset;
        private int characters;
        private bool initialized, previousCarriageReturn, partialMatched, disposed;
        private byte[] boundary = new byte[0];
        private Decoder decoder;
        private string[] keywords = new string[0];
        private DateTime lastAlertUtc = DateTime.MinValue;
        internal string[] Keywords { get { lock (gate) return keywords.ToArray(); } }
        public LogWatchReader(string target) : this(target, () => DateTime.UtcNow) { }
        internal LogWatchReader(string target, Func<DateTime> now)
        {
            string input = (target ?? "").Trim().Trim('"');
            if (String.IsNullOrWhiteSpace(input) || !System.IO.Path.IsPathRooted(input)) throw new ArgumentException("请输入日志文件完整路径。");
            path = System.IO.Path.GetFullPath(input); clock = now;
        }
        public void SetKeywords(string value)
        {
            var entries = (value ?? "").Split(new[] { ',', ';', '\r', '\n', '，', '；' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (entries.Length > 20 || entries.Any(x => x.Length > 100)) throw new ArgumentException("最多 20 个关键词，每个最多 100 字。");
            lock (gate) { keywords = entries; partialMatched = Matches(partial); }
        }
        public void Clear() { lock (gate) { lines.Clear(); partial = ""; characters = 0; previousCarriageReturn = false; partialMatched = false; } }
        public LogWatchSnapshot Read()
        {
            lock (gate)
            {
                var snapshot = new LogWatchSnapshot { Path = path, Offset = offset };
                if (disposed) { snapshot.Status = "监测已停止。"; return snapshot; }
                try
                {
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan))
                    {
                        string identity = Identity(stream.SafeFileHandle);
                        bool replaced = initialized && fileIdentity != identity;
                        bool truncated = initialized && (stream.Length < offset || (!replaced && BoundaryChanged(stream)));
                        bool initial = !initialized || replaced || truncated;
                        // An empty file has already established the live boundary. Its
                        // first append still needs BOM detection, but is new log data.
                        bool firstAppend = !initial && offset == 0 && stream.Length > 0;
                        bool backlog = !initial && stream.Length - offset > MaximumReadBytes * 8L;
                        if (initial || firstAppend || backlog)
                        {
                            int bom; Encoding encoding = DetectEncoding(stream, out bom);
                            long start = Math.Max(bom, stream.Length - MaximumReadBytes);
                            int unit = encoding.CodePage == 1200 || encoding.CodePage == 1201 ? 2 : encoding.CodePage == 12000 || encoding.CodePage == 12001 ? 4 : 1;
                            start -= (start - bom) % unit;
                            offset = start; decoder = encoding.GetDecoder(); partial = ""; previousCarriageReturn = false; partialMatched = false;
                            if (initial) { lines.Clear(); characters = 0; }
                            snapshot.Reset = replaced || truncated || backlog;
                            stream.Position = offset;
                            ReadChunk(stream, start > bom, firstAppend, snapshot);
                            initialized = true; fileIdentity = identity;
                            snapshot.Status = backlog ? "文件增长较快，已跳到末尾。" : replaced ? "日志已轮换，正在读取新文件。" : truncated ? "日志已截断，已重新读取。" : "正在监测新增日志。";
                        }
                        else
                        {
                            stream.Position = offset; ReadChunk(stream, false, true, snapshot);
                            snapshot.Status = "正在监测新增日志。";
                        }
                        RememberBoundary(stream);
                    }
                }
                catch (FileNotFoundException) { snapshot.Missing = true; snapshot.Status = "文件尚不存在，等待出现…"; }
                catch (DirectoryNotFoundException) { snapshot.Missing = true; snapshot.Status = "文件尚不存在，等待出现…"; }
                catch (IOException error) { snapshot.Status = "暂时无法读取，将自动重试：" + error.Message; }
                catch (UnauthorizedAccessException error) { snapshot.Status = "暂时无法读取，将自动重试：" + error.Message; }
                catch (Win32Exception error) { snapshot.Status = "暂时无法读取，将自动重试：" + error.Message; }
                snapshot.Offset = offset; snapshot.Lines = lines.ToList();
                if (partial.Length > 0) snapshot.Lines.Add(partial);
                int total = snapshot.Lines.Sum(x => x.Length);
                while (snapshot.Lines.Count > MaximumLines || total > MaximumBufferCharacters) { total -= snapshot.Lines[0].Length; snapshot.Lines.RemoveAt(0); }
                return snapshot;
            }
        }
        private bool BoundaryChanged(FileStream stream)
        {
            if (boundary.Length == 0 || stream.Length < offset) return false;
            byte[] actual = new byte[boundary.Length]; stream.Position = offset - boundary.Length;
            int count = stream.Read(actual, 0, actual.Length);
            return count != boundary.Length || !boundary.SequenceEqual(actual);
        }
        private void RememberBoundary(FileStream stream)
        {
            int length = (int)Math.Min(64, offset); boundary = new byte[length];
            if (length == 0 || stream.Length < offset) return;
            stream.Position = offset - length; int count = stream.Read(boundary, 0, length);
            if (count != length) boundary = new byte[0];
        }
        private void ReadChunk(FileStream stream, bool skipFirstLine, bool allowAlert, LogWatchSnapshot snapshot)
        {
            byte[] bytes = new byte[MaximumReadBytes]; int read = stream.Read(bytes, 0, bytes.Length); offset += read;
            char[] chars = new char[MaximumReadBytes + 4]; int count = decoder.GetChars(bytes, 0, read, chars, 0, false);
            var hits = new List<string>();
            for (int i = 0; i < count; i++)
            {
                char value = chars[i];
                if (value == '\n' && previousCarriageReturn) { previousCarriageReturn = false; continue; }
                previousCarriageReturn = value == '\r';
                if (value == '\r' || value == '\n')
                {
                    if (!skipFirstLine) { AppendLine(partial); if (allowAlert && !partialMatched && Matches(partial)) hits.Add(partial); }
                    skipFirstLine = false; partial = ""; partialMatched = false;
                }
                else if (partial.Length < MaximumLineLength) partial += value;
            }
            if (skipFirstLine) partial = "";
            // A logical line is considered once, including a historical unfinished
            // line. Empty polls, later suffixes, and its eventual newline must not
            // replay the same error after the notification throttle expires.
            if (Matches(partial)) { if (allowAlert && !partialMatched) hits.Add(partial); partialMatched = true; }
            if (hits.Count > 0 && clock() - lastAlertUtc >= TimeSpan.FromSeconds(10)) { lastAlertUtc = clock(); snapshot.Alert = String.Join("\n", hits.Take(3).Select(x => x.Length > 200 ? x.Substring(0, 200) + "…" : x)); }
        }
        private bool Matches(string line) { return line.Length > 0 && keywords.Any(x => line.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0); }
        private void AppendLine(string line)
        {
            lines.Add(line); characters += line.Length;
            while (lines.Count > MaximumLines || characters > MaximumBufferCharacters) { characters -= lines[0].Length; lines.RemoveAt(0); }
        }
        private static Encoding DetectEncoding(FileStream stream, out int bom)
        {
            byte[] bytes = new byte[4]; stream.Position = 0; int count = stream.Read(bytes, 0, 4); bom = 0;
            if (count >= 4 && bytes[0] == 0xff && bytes[1] == 0xfe && bytes[2] == 0 && bytes[3] == 0) { bom = 4; return new UTF32Encoding(false, false); }
            if (count >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xfe && bytes[3] == 0xff) { bom = 4; return new UTF32Encoding(true, false); }
            if (count >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf) { bom = 3; return new UTF8Encoding(false); }
            if (count >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe) { bom = 2; return new UnicodeEncoding(false, false); }
            if (count >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff) { bom = 2; return new UnicodeEncoding(true, false); }
            return new UTF8Encoding(false);
        }
        [StructLayout(LayoutKind.Sequential)] private struct FileInformation
        {
            public uint Attributes, CreatedLow, CreatedHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation info);
        private static string Identity(SafeFileHandle handle) { FileInformation info; if (!GetFileInformationByHandle(handle, out info)) throw new Win32Exception(Marshal.GetLastWin32Error()); return info.Volume + ":" + info.IndexHigh + ":" + info.IndexLow + ":" + info.CreatedHigh + ":" + info.CreatedLow; }
        public void Dispose() { lock (gate) { disposed = true; lines.Clear(); partial = ""; decoder = null; } }
    }
}
