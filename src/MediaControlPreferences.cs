using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexIsland
{
    internal sealed class MediaUiPreferences
    {
        public int LyricFontSize = 10;
        public int LyricOffsetMilliseconds;
        public string PreferredSourceId = "";
        internal void Validate()
        {
            LyricFontSize = Math.Max(9, Math.Min(16, LyricFontSize));
            LyricOffsetMilliseconds = Math.Max(-10000, Math.Min(10000, LyricOffsetMilliseconds));
            PreferredSourceId = MediaSessionSelection.NormalizeSource(PreferredSourceId);
        }
        internal MediaUiPreferences Copy() { return new MediaUiPreferences { LyricFontSize = LyricFontSize, LyricOffsetMilliseconds = LyricOffsetMilliseconds, PreferredSourceId = PreferredSourceId }; }
    }

    internal sealed class MediaUiPreferencesStore
    {
        private readonly string path;
        private readonly bool memoryOnly;
        private MediaUiPreferences memory = new MediaUiPreferences();
        internal MediaUiPreferencesStore(bool memoryOnly = false, string filePath = null)
        {
            this.memoryOnly = memoryOnly;
            path = filePath ?? Path.Combine(Path.GetDirectoryName(new SettingsStore().FilePath), "music-preferences.json");
        }
        internal MediaUiPreferences Load()
        {
            if (memoryOnly) return memory.Copy();
            try
            {
                using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (input.Length > 16384) return new MediaUiPreferences();
                    using (var reader = new StreamReader(input, new UTF8Encoding(false, true)))
                    {
                        var value = new JavaScriptSerializer { MaxJsonLength = 16384 }.Deserialize<MediaUiPreferences>(reader.ReadToEnd()) ?? new MediaUiPreferences();
                        value.Validate(); return value;
                    }
                }
            }
            catch (Exception) { return new MediaUiPreferences(); }
        }
        internal void Save(MediaUiPreferences value)
        {
            value = value.Copy(); value.Validate();
            if (memoryOnly) { memory = value; return; }
            string directory = Path.GetDirectoryName(path), temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            Directory.CreateDirectory(directory);
            try
            {
                File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(value), new UTF8Encoding(false, true));
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    internal static class MediaLyricTiming
    {
        // Positive user offset advances the displayed lyric, in addition to LRC's own offset.
        internal static TimeSpan Adjust(TimeSpan position, int milliseconds)
        {
            long delta = Math.Max(-10000, Math.Min(10000, milliseconds)) * TimeSpan.TicksPerMillisecond;
            if (delta > 0 && position.Ticks > TimeSpan.MaxValue.Ticks - delta) return TimeSpan.MaxValue;
            if (delta < 0 && position.Ticks < TimeSpan.MinValue.Ticks - delta) return TimeSpan.MinValue;
            return TimeSpan.FromTicks(position.Ticks + delta);
        }
    }
}
