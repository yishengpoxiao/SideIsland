using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexIsland
{
    internal sealed class LocalDataFile<T> where T : class
    {
        private readonly string path;
        private readonly int limit;
        private readonly bool memoryOnly;
        private bool failed, preserved;
        internal LocalDataFile(string filename, bool demo, int maximumBytes = 256 * 1024)
        { path = Path.Combine(Path.GetDirectoryName(new SettingsStore().FilePath), filename); memoryOnly = demo; limit = maximumBytes; }
        internal T Load(T fallback)
        {
            if (memoryOnly) return fallback;
            try
            {
                if (!File.Exists(path)) return fallback;
                if (new FileInfo(path).Length > limit) throw new InvalidDataException();
                return new JavaScriptSerializer { MaxJsonLength = limit, RecursionLimit = 16 }.Deserialize<T>(File.ReadAllText(path, new UTF8Encoding(false, true))) ?? fallback;
            }
            catch (Exception error)
            {
                if (!(error is IOException || error is UnauthorizedAccessException || error is ArgumentException || error is InvalidOperationException)) throw;
                failed = true; return fallback;
            }
        }
        internal void MarkInvalid() { failed = true; }
        internal void Save(T value)
        {
            if (memoryOnly) return;
            byte[] bytes = new UTF8Encoding(false, true).GetBytes(new JavaScriptSerializer { MaxJsonLength = limit, RecursionLimit = 16 }.Serialize(value));
            if (bytes.Length > limit) throw new InvalidDataException("本地设置超过大小限制。");
            Directory.CreateDirectory(Path.GetDirectoryName(path)); string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                if (failed && !preserved && File.Exists(path)) { File.Copy(path, path + ".unreadable-" + Guid.NewGuid().ToString("N") + ".bak"); preserved = true; }
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path); failed = false;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
