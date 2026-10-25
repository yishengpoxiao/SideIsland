using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace CodexIsland
{
    internal sealed class LrcLine
    {
        public TimeSpan Time { get; private set; }
        public string Text { get; private set; }
        internal LrcLine(TimeSpan time, string text) { Time = time; Text = text; }
    }

    // Parsing and seeking are independent of WPF and never contact a lyrics service.
    internal sealed class LrcDocument
    {
        public const int MaximumFileBytes = 256 * 1024;
        public const int MaximumTimedLines = 5000;
        private readonly LrcLine[] lines;
        public ReadOnlyCollection<LrcLine> Lines { get; private set; }
        public int Count { get { return lines.Length; } }
        public int OffsetMilliseconds { get; private set; }
        public bool IsSynchronized { get; private set; }

        private sealed class Cue
        {
            public long Milliseconds;
            public string Text;
            public int Order;
        }

        private LrcDocument(LrcLine[] value, int offset, bool synchronized = true)
        { lines = value; Lines = Array.AsReadOnly(lines); OffsetMilliseconds = offset; IsSynchronized = synchronized; }

        internal static LrcDocument FromPlainText(string text)
        {
            if (String.IsNullOrWhiteSpace(text)) return null;
            if (text.Length > MaximumFileBytes || Encoding.UTF8.GetByteCount(text) > MaximumFileBytes)
                throw new InvalidDataException("歌词文件不能超过 256 KB");
            return new LrcDocument(new[] { new LrcLine(TimeSpan.Zero, text) }, 0, false);
        }

        public static LrcDocument Parse(string text)
        {
            if (text == null) throw new ArgumentNullException("text");
            if (text.Length > MaximumFileBytes || Encoding.UTF8.GetByteCount(text) > MaximumFileBytes)
                throw new InvalidDataException("歌词文件不能超过 256 KB");
            var cues = new List<Cue>();
            int offset = 0;
            using (var reader = new StringReader(text))
            {
                string raw;
                while ((raw = reader.ReadLine()) != null)
                {
                    string line = raw.TrimStart('\uFEFF', ' ', '\t');
                    int position = 0;
                    var stamps = new List<long>();
                    while (position < line.Length && line[position] == '[')
                    {
                        int end = line.IndexOf(']', position + 1);
                        if (end < 0) break;
                        string tag = line.Substring(position + 1, end - position - 1);
                        long milliseconds;
                        if (TryTimestamp(tag, out milliseconds))
                        {
                            if (cues.Count + stamps.Count >= MaximumTimedLines)
                                throw new InvalidDataException("歌词不能超过 5000 个时间标记");
                            stamps.Add(milliseconds);
                        }
                        else if (tag.StartsWith("offset:", StringComparison.OrdinalIgnoreCase))
                        {
                            int parsed;
                            if (Int32.TryParse(tag.Substring(7).Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out parsed)) offset = parsed;
                        }
                        else if (stamps.Count != 0) break;
                        position = end + 1;
                    }
                    if (stamps.Count == 0) continue;
                    if (cues.Count + stamps.Count > MaximumTimedLines)
                        throw new InvalidDataException("歌词不能超过 5000 个时间标记");
                    string words = line.Substring(position).Trim();
                    foreach (long stamp in stamps)
                        cues.Add(new Cue { Milliseconds = stamp, Text = words, Order = cues.Count });
                }
            }
            // Keep pre-roll cues distinct after an offset. Clamping them all to zero
            // would wrongly concatenate earlier lines when playback starts.
            foreach (var cue in cues) cue.Milliseconds -= offset;
            cues.Sort(delegate(Cue left, Cue right)
            { int comparison = left.Milliseconds.CompareTo(right.Milliseconds); return comparison != 0 ? comparison : left.Order.CompareTo(right.Order); });
            var result = new List<LrcLine>();
            for (int index = 0; index < cues.Count; )
            {
                var cue = cues[index++];
                var words = new StringBuilder(cue.Text);
                while (index < cues.Count && cues[index].Milliseconds == cue.Milliseconds)
                {
                    string next = cues[index++].Text;
                    if (next.Length == 0) continue;
                    if (words.Length > 0) words.Append('\n');
                    words.Append(next);
                }
                result.Add(new LrcLine(TimeSpan.FromMilliseconds(cue.Milliseconds), words.ToString()));
            }
            return new LrcDocument(result.ToArray(), offset);
        }

        public static LrcDocument ReadFile(string path) { return Parse(ReadText(path)); }

        internal static string ReadText(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("请选择歌词文件", "path");
            byte[] bytes;
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (input.Length > MaximumFileBytes) throw new InvalidDataException("歌词文件不能超过 256 KB");
                using (var output = new MemoryStream())
                {
                    var buffer = new byte[4096];
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (output.Length + read > MaximumFileBytes) throw new InvalidDataException("歌词文件不能超过 256 KB");
                        output.Write(buffer, 0, read);
                    }
                    bytes = output.ToArray();
                }
            }
            // BOM-aware Unicode first; older QQ/NetEase exports may use GB18030.
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return new UnicodeEncoding(false, true, true).GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return new UnicodeEncoding(true, true, true).GetString(bytes, 2, bytes.Length - 2);
            int skip = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            try { return new UTF8Encoding(false, true).GetString(bytes, skip, bytes.Length - skip); }
            catch (DecoderFallbackException)
            { return Encoding.GetEncoding(54936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(bytes); }
        }

        public string LineAt(TimeSpan position)
        {
            if (position < TimeSpan.Zero) return "";
            int low = 0, high = lines.Length - 1, found = -1;
            while (low <= high)
            {
                int middle = low + (high - low) / 2;
                if (lines[middle].Time <= position) { found = middle; low = middle + 1; }
                else high = middle - 1;
            }
            return found < 0 ? "" : lines[found].Text;
        }

        private static bool TryTimestamp(string value, out long milliseconds)
        {
            milliseconds = 0;
            int colon = value.IndexOf(':');
            if (colon < 1 || colon > 4) return false;
            int fractionStart = value.IndexOf('.', colon + 1);
            int secondsEnd = fractionStart < 0 ? value.Length : fractionStart;
            if (secondsEnd - colon != 3) return false;
            int minutes = 0, seconds = 0, fraction = 0;
            for (int index = 0; index < colon; index++)
            { if (value[index] < '0' || value[index] > '9') return false; minutes = minutes * 10 + value[index] - '0'; }
            for (int index = colon + 1; index < secondsEnd; index++)
            { if (value[index] < '0' || value[index] > '9') return false; seconds = seconds * 10 + value[index] - '0'; }
            if (seconds > 59) return false;
            if (fractionStart >= 0)
            {
                int length = value.Length - fractionStart - 1;
                if (length < 1 || length > 3) return false;
                for (int index = fractionStart + 1; index < value.Length; index++)
                { if (value[index] < '0' || value[index] > '9') return false; fraction = fraction * 10 + value[index] - '0'; }
                if (length == 1) fraction *= 100;
                else if (length == 2) fraction *= 10;
            }
            milliseconds = (minutes * 60L + seconds) * 1000 + fraction;
            return true;
        }
    }

    // The managed directory contains copies, so a moved/deleted original cannot break lyrics.
    // A complete player/title/artist identity prevents lyrics leaking onto another recording.
    internal sealed class MediaLyricsLibrary
    {
        public const int MaximumEntries = 64;
        private readonly string directory;
        private readonly bool memoryOnly;
        private readonly object sync = new object();
        private readonly Dictionary<string, CacheEntry> cache = new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
        private long sequence;
        private sealed class CacheEntry { public LrcDocument Document; public long Access; }

        public MediaLyricsLibrary() : this(Path.GetDirectoryName(new SettingsStore().FilePath), false) { }
        public MediaLyricsLibrary(string appDataDirectory, bool memoryOnly = false)
        {
            if (String.IsNullOrWhiteSpace(appDataDirectory)) throw new ArgumentException("appDataDirectory");
            directory = Path.Combine(Path.GetFullPath(appDataDirectory), "lyrics");
            this.memoryOnly = memoryOnly;
        }

        public LrcDocument Find(string source, string title, string artist)
        {
            string key = Key(source, title, artist);
            lock (sync)
            {
                CacheEntry existing;
                if (cache.TryGetValue(key, out existing)) { existing.Access = ++sequence; return existing.Document; }
                LrcDocument document = null;
                if (!memoryOnly)
                {
                    try
                    {
                        document = LrcDocument.ReadFile(Path.Combine(directory, key + ".lrc"));
                        if (document.Count == 0) document = null;
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    catch (ArgumentException) { }
                    catch (NotSupportedException) { }
                }
                Put(key, document);
                return document;
            }
        }

        public LrcDocument Import(string source, string title, string artist, string filePath)
        {
            string text = LrcDocument.ReadText(filePath);
            LrcDocument document = LrcDocument.Parse(text);
            if (document.Count == 0) throw new InvalidDataException("歌词文件没有可用的时间标记");
            string key = Key(source, title, artist);
            lock (sync)
            {
                if (!memoryOnly)
                {
                    Directory.CreateDirectory(directory);
                    string target = Path.Combine(directory, key + ".lrc");
                    string temporary = Path.Combine(directory, key + "." + Guid.NewGuid().ToString("N") + ".tmp");
                    try
                    {
                        File.WriteAllText(temporary, text, new UTF8Encoding(false, true));
                        if (File.Exists(target)) File.Replace(temporary, target, null);
                        else File.Move(temporary, target);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                    Prune(key);
                }
                Put(key, document);
                return document;
            }
        }

        public bool Remove(string source, string title, string artist)
        {
            string key = Key(source, title, artist);
            lock (sync)
            {
                CacheEntry existing;
                bool removed = cache.TryGetValue(key, out existing) && existing.Document != null;
                if (!memoryOnly)
                {
                    string path = Path.Combine(directory, key + ".lrc");
                    if (File.Exists(path)) { removed = true; File.Delete(path); }
                }
                Put(key, null);
                return removed;
            }
        }

        private void Put(string key, LrcDocument document)
        {
            cache[key] = new CacheEntry { Document = document, Access = ++sequence };
            while (cache.Count > MaximumEntries)
            {
                string oldest = cache.OrderBy(pair => pair.Value.Access).First().Key;
                cache.Remove(oldest);
            }
        }

        private void Prune(string importedKey)
        {
            var files = new DirectoryInfo(directory).EnumerateFiles("*.lrc")
                .Where(file => IsManagedName(file.Name)).OrderByDescending(file => file.Name == importedKey + ".lrc")
                .ThenByDescending(file => file.LastWriteTimeUtc).ToArray();
            for (int index = MaximumEntries; index < files.Length; index++)
            {
                try
                {
                    files[index].Delete();
                    cache.Remove(Path.GetFileNameWithoutExtension(files[index].Name));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static bool IsManagedName(string name)
        {
            if (name.Length != 68 || !name.EndsWith(".lrc", StringComparison.Ordinal)) return false;
            for (int index = 0; index < 64; index++)
                if (!(name[index] >= '0' && name[index] <= '9') && !(name[index] >= 'a' && name[index] <= 'f')) return false;
            return true;
        }

        private static string Key(string source, string title, string artist)
        {
            var value = new StringBuilder();
            foreach (string part in new[] { source ?? "", title ?? "", artist ?? "" })
                value.Append(part.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(part);
            using (var algorithm = SHA256.Create())
                return BitConverter.ToString(algorithm.ComputeHash(Encoding.UTF8.GetBytes(value.ToString()))).Replace("-", "").ToLowerInvariant();
        }
    }
}
