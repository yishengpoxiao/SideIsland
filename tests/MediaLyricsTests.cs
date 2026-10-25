using System;
using System.IO;
using System.Linq;
using System.Text;

namespace CodexIsland
{
    internal static class MediaLyricsTests
    {
        public static void Run()
        {
            ParseAndSeek();
            OffsetsAndDuplicates();
            Bounds();
            PersistentLibrary();
            Console.WriteLine("PASS: LRC parsing, Unicode, timeline seeking, offsets, duplicate translations, size limits and bounded exact-track lyrics library.");
        }

        private static void ParseAndSeek()
        {
            var lyrics = LrcDocument.Parse("\uFEFF[ti:Title]\n[ar:Artist]\n[00:03.500]第三行 😀\n[0:01.2][00:02.25]歌词\n[00:04]刷新\n[00:05]\nnot timed\n[00:60]invalid seconds\n[xx:12]broken\n[00:02.1234]too precise");
            Check(lyrics.Count == 5, "Timed cues are sorted, including multiple timestamps and clearing cues");
            Check(lyrics.LineAt(TimeSpan.Zero) == "", "No lyrics before the first line");
            Check(lyrics.LineAt(TimeSpan.FromSeconds(-1)) == "", "Negative position is before first cue");
            Check(lyrics.LineAt(TimeSpan.FromMilliseconds(1199)) == "", "First cue is not displayed early");
            Check(lyrics.LineAt(TimeSpan.FromMilliseconds(1200)) == "歌词", "One fractional digit means tenths");
            Check(lyrics.Lines[1].Time == TimeSpan.FromMilliseconds(2250), "Two fractional digits mean hundredths");
            Check(lyrics.LineAt(TimeSpan.FromMilliseconds(3500)) == "第三行 😀", "Three fractional digits and Unicode preserved");
            Check(lyrics.LineAt(TimeSpan.FromSeconds(4)) == "刷新", "Command-like lyric text stays unchanged");
            Check(lyrics.LineAt(TimeSpan.FromSeconds(5)) == "", "Empty timed cue clears the previous lyrics");
            Check(lyrics.LineAt(TimeSpan.MaxValue) == "", "Unknown far future position remains safe");
            Check(lyrics.LineAt(TimeSpan.FromSeconds(2)) == "歌词", "Backward seeking recomputes the active cue");
            Check(LrcDocument.Parse("[ar:Only metadata]").Count == 0, "Metadata-only files have no invented cues");
            Check(LrcDocument.Parse("[100:01.001]long task").LineAt(TimeSpan.FromMilliseconds(6001001)) == "long task", "Long tracks accept more than two minute digits");
        }

        private static void OffsetsAndDuplicates()
        {
            var lyrics = LrcDocument.Parse("[00:02]Original\n[00:02]译文\n[00:03]Next\n[offset:500]\n[offset:+1000]");
            Check(lyrics.OffsetMilliseconds == 1000, "Final offset tag applies to the whole file");
            Check(lyrics.LineAt(TimeSpan.FromMilliseconds(999)) == "", "Positive offset does not display before adjusted cue");
            Check(lyrics.LineAt(TimeSpan.FromSeconds(1)) == "Original\n译文", "Positive offset advances and merges translations in original order");
            Check(lyrics.LineAt(TimeSpan.FromSeconds(2)) == "Next", "Offset applies to all cues");
            lyrics = LrcDocument.Parse("[offset:-500]\n[00:00.1]Delayed\n[offset:garbage]");
            Check(lyrics.LineAt(TimeSpan.FromMilliseconds(599)) == "" && lyrics.LineAt(TimeSpan.FromMilliseconds(600)) == "Delayed", "Negative offset delays and malformed tags do not replace a valid offset");
            lyrics = LrcDocument.Parse("[00:00]A\n[00:00.5]B\n[offset:1000]");
            Check(lyrics.LineAt(TimeSpan.Zero) == "B", "Pre-roll cues remain distinct so only the latest lyric shows at zero");
            Check(LrcDocument.Parse("[offset:2147483647]\n[00:01]A").LineAt(TimeSpan.Zero) == "A", "Extreme valid positive offset cannot overflow");
            Check(LrcDocument.Parse("[offset:-2147483648]\n[00:01]A").LineAt(TimeSpan.Zero) == "", "Extreme valid negative offset cannot overflow");
        }

        private static void Bounds()
        {
            bool rejected = false;
            try { LrcDocument.Parse(new string('a', LrcDocument.MaximumFileBytes + 1)); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Oversized strings are rejected");
            rejected = false;
            try { LrcDocument.Parse(new string('字', 100000)); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, "UTF-8 byte capacity also bounds CJK content");
            var text = new StringBuilder();
            for (int index = 0; index < LrcDocument.MaximumTimedLines; index++) text.Append("[00:01]x\n");
            Check(LrcDocument.Parse(text.ToString()).Count == 1, "Maximum timed markers accepted and duplicate cue grouped");
            text.Append("[00:02]x"); rejected = false;
            try { LrcDocument.Parse(text.ToString()); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Too many timed markers rejected");
            Check(LrcDocument.Parse("[" + new string('0', 10000) + "]broken\n[00:01]valid").Count == 1, "Malformed long tags remain bounded and do not discard later valid cues");
        }

        private static void PersistentLibrary()
        {
            string parent = Path.GetFullPath(Path.GetTempPath());
            string directory = Path.GetFullPath(Path.Combine(parent, "CodexIslandLyricsTests-" + Guid.NewGuid().ToString("N")));
            Check(directory.StartsWith(parent, StringComparison.OrdinalIgnoreCase), "Fixture directory stays inside the temporary root");
            Directory.CreateDirectory(directory);
            try
            {
                string path = Path.Combine(directory, "input.lrc");
                File.WriteAllText(path, "[00:01]你好 😀\n[00:02]Next", new UTF8Encoding(true));
                var library = new MediaLyricsLibrary(directory);
                Check(library.Find("QQ", "Title", "Artist") == null, "Missing tracks are absent");
                Check(!library.Remove("QQ", "Title", "Artist"), "Removing a missing library before its directory exists succeeds");
                var loaded = library.Import("QQ", "Title", "Artist", path);
                Check(loaded.LineAt(TimeSpan.FromSeconds(1)) == "你好 😀", "UTF-8 BOM file loads without changing text");
                Check(Object.ReferenceEquals(loaded, library.Find("QQ", "Title", "Artist")), "Import invalidates cached miss without another read");
                Check(library.Find("QQ", "Cached miss", "Artist") == null, "Missing lyric lookup cached");
                new MediaLyricsLibrary(directory).Import("QQ", "Cached miss", "Artist", path);
                Check(library.Find("QQ", "Cached miss", "Artist") == null, "Cached misses avoid repeated reads while another library changes disk");
                library.Import("QQ", "Cached miss", "Artist", path);
                Check(library.Find("QQ", "Cached miss", "Artist") != null, "Explicit import refreshes cached misses");
                Check(library.Find("NetEase", "Title", "Artist") == null && library.Find("QQ", "title", "Artist") == null && library.Find("QQ", "Title", "Other") == null,
                    "Player, title, artist and case form an exact track identity");
                File.Delete(path);
                Check(new MediaLyricsLibrary(directory).Find("QQ", "Title", "Artist").Count == 2, "Lyrics survive a deleted source and library restart");
                File.WriteAllText(path, "[00:00]Replacement", Encoding.Unicode);
                library.Import("QQ", "Title", "Artist", path);
                Check(new MediaLyricsLibrary(directory).Find("QQ", "Title", "Artist").LineAt(TimeSpan.Zero) == "Replacement", "Atomic replacement and UTF-16 BOM imports work");
                Check(!Directory.GetFiles(Path.Combine(directory, "lyrics"), "*.tmp").Any(), "No temporary files remain after import");
                Check(library.Remove("QQ", "Title", "Artist") && library.Find("QQ", "Title", "Artist") == null && new MediaLyricsLibrary(directory).Find("QQ", "Title", "Artist") == null,
                    "Removal clears memory and persistent lyrics");
                File.WriteAllText(path, "[00:01]旧式歌词", Encoding.GetEncoding(54936));
                Check(LrcDocument.ReadFile(path).LineAt(TimeSpan.FromSeconds(1)) == "旧式歌词", "GB18030 exports supported");
                for (int index = 0; index <= MediaLyricsLibrary.MaximumEntries; index++) library.Import("player", "track" + index, "artist", path);
                Check(Directory.GetFiles(Path.Combine(directory, "lyrics"), "*.lrc").Length == MediaLyricsLibrary.MaximumEntries, "Persistent lyrics have a fixed 64-track bound");
                Check(new MediaLyricsLibrary(directory).Find("player", "track64", "artist") != null, "Newest import survives pruning");
                string memoryDirectory = Path.Combine(directory, "no-io");
                var memory = new MediaLyricsLibrary(memoryDirectory, true);
                Check(memory.Find("p", "t", "a") == null && !memory.Remove("p", "t", "a"), "Demo memory library does not read or create storage");
                memory.Import("p", "t", "a", path);
                Check(memory.Find("p", "t", "a") != null && !Directory.Exists(memoryDirectory), "Memory-only import retains lyrics without filesystem writes");
                File.WriteAllText(path, "plain unsynchronized lyrics");
                bool rejected = false;
                try { library.Import("player", "untimed", "artist", path); } catch (InvalidDataException) { rejected = true; }
                Check(rejected, "Import rejects a file without synchronized lines");
                File.WriteAllBytes(path, new byte[LrcDocument.MaximumFileBytes + 1]); rejected = false;
                try { LrcDocument.ReadFile(path); } catch (InvalidDataException) { rejected = true; }
                Check(rejected, "Physical file capacity enforced before reading content");
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }

        private static void Check(bool condition, string message)
        { if (!condition) throw new Exception("Media lyrics: " + message); }
    }
}
