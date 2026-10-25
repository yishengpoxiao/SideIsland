using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexIsland
{
    internal sealed class FileShelfMetadata
    {
        public string Path, Name, Extension, Status;
        public bool Available, IsDirectory, CanOpen;
        public long Size;
    }

    internal static class FileShelfPaths
    {
        // Only explicitly supported document/media types can invoke their default application.
        // Unknown extensions, extensionless files, shortcuts, programs and scripts stay locate-only.
        private static readonly HashSet<string> OpenableExtensions = new HashSet<string>(new[] { ".txt", ".md", ".csv", ".tsv", ".json", ".xml", ".yaml", ".yml", ".log", ".ini", ".cfg", ".pdf", ".docx", ".xlsx", ".pptx", ".odt", ".ods", ".odp", ".rtf", ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".ico", ".heic", ".avif", ".mp3", ".wav", ".flac", ".aac", ".ogg", ".wma", ".m4a", ".mp4", ".m4v", ".mov", ".webm", ".avi", ".mkv", ".wmv" }, StringComparer.OrdinalIgnoreCase);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern uint GetDriveType(string rootPathName);

        public static bool TryNormalize(string value, out string normalized, out string error)
        {
            normalized = null; error = "请选择本机文件或文件夹的完整路径。";
            if (String.IsNullOrWhiteSpace(value)) return false;
            if (value.Length > 259) { error = "暂不支持超过 259 个字符的路径。"; return false; }
            string path = value.Replace('/', '\\');
            if (path.StartsWith("\\\\", StringComparison.Ordinal)) { error = "暂不支持网络共享或设备路径，请选择本地文件。"; return false; }
            if (path.Length < 3 || !((path[0] >= 'a' && path[0] <= 'z') || (path[0] >= 'A' && path[0] <= 'Z')) || path[1] != ':' || path[2] != '\\') return false;
            for (int i = 0; i < path.Length; i++)
                if (Char.IsControl(path[i]) || "<>\"|*?".IndexOf(path[i]) >= 0 || (path[i] == ':' && i != 1)) return false;
            string[] parts = path.Substring(3).Split('\\');
            foreach (string part in parts)
            {
                if (part.Length == 0 || part == "." || part == "..") continue;
                if (part.EndsWith(" ", StringComparison.Ordinal) || part.EndsWith(".", StringComparison.Ordinal)) { error = "路径名称不能以空格或句点结尾。"; return false; }
                string name = part.Split('.')[0].ToUpperInvariant();
                if (name == "CON" || name == "PRN" || name == "AUX" || name == "NUL" || name == "CLOCK$" || name == "CONIN$" || name == "CONOUT$" || (name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal)) && "0123456789¹²³".IndexOf(name[3]) >= 0)) return false;
            }
            try
            {
                normalized = System.IO.Path.GetFullPath(path);
                if (normalized.Length > 3) normalized = normalized.TrimEnd('\\');
                error = null; return true;
            }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
            catch (PathTooLongException) { return false; }
        }

        public static bool CanOpenFile(string path) { return OpenableExtensions.Contains(System.IO.Path.GetExtension(path)); }

        // Call on a worker: file metadata may be slow for removable/offline drives.
        public static FileShelfMetadata Inspect(string path)
        {
            var result = new FileShelfMetadata { Path = path, Name = System.IO.Path.GetFileName(path), Extension = System.IO.Path.GetExtension(path), Status = "文件暂不可用" };
            if (String.IsNullOrEmpty(result.Name)) result.Name = path;
            try
            {
                uint drive = GetDriveType(System.IO.Path.GetPathRoot(path));
                if (drive == 4) { result.Status = "网络磁盘不支持 · 可移除引用"; return result; }
                if (drive == 0 || drive == 1) return result;
                FileAttributes attributes = File.GetAttributes(path);
                // Reparse points may target a remote location, so avoid following them.
                if ((attributes & FileAttributes.ReparsePoint) != 0) { result.Status = "链接路径不直接打开 · 请打开所在位置"; return result; }
                result.IsDirectory = (attributes & FileAttributes.Directory) != 0;
                if (!result.IsDirectory) result.Size = new FileInfo(path).Length;
                result.Available = true; result.CanOpen = result.IsDirectory || CanOpenFile(path);
                result.Status = result.IsDirectory ? "文件夹 · 不扫描内容" : result.Extension.ToUpperInvariant() + " · " + FormatSize(result.Size);
                if (!result.CanOpen) result.Status += " · 此类型仅定位";
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { result.Status = "没有权限读取此路径"; }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
            return result;
        }

        public static string FormatSize(long size)
        {
            if (size < 1024) return Math.Max(0, size).ToString(CultureInfo.InvariantCulture) + " B";
            if (size < 1024L * 1024) return (size / 1024d).ToString("0.#", CultureInfo.InvariantCulture) + " KB";
            if (size < 1024L * 1024 * 1024) return (size / (1024d * 1024)).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
            return (size / (1024d * 1024 * 1024)).ToString("0.#", CultureInfo.InvariantCulture) + " GB";
        }
    }

    internal sealed class FileShelfStore
    {
        public const int MaxEntries = 30;
        private const int MaxFileBytes = 64 * 1024;
        public string FilePath { get; private set; }
        public bool LoadFailed { get; private set; }
        private bool unreadablePreserved;
        public FileShelfStore(string path = null) { FilePath = Path.GetFullPath(path ?? Path.Combine(Path.GetDirectoryName(new SettingsStore().FilePath), "file-shelf.json")); }

        public List<string> Load()
        {
            LoadFailed = false; unreadablePreserved = false;
            try
            {
                if (!File.Exists(FilePath)) return new List<string>();
                if (new FileInfo(FilePath).Length > MaxFileBytes) { LoadFailed = true; return new List<string>(); }
                var serializer = new JavaScriptSerializer { MaxJsonLength = MaxFileBytes, RecursionLimit = 8 };
                var raw = serializer.DeserializeObject(File.ReadAllText(FilePath, new UTF8Encoding(false, true))) as IList;
                if (raw == null) { LoadFailed = true; return new List<string>(); }
                var result = Normalize(raw.Cast<object>().OfType<string>());
                if (result.Count != raw.Count) LoadFailed = true;
                return result;
            }
            catch (IOException) { LoadFailed = true; }
            catch (UnauthorizedAccessException) { LoadFailed = true; }
            catch (ArgumentException) { LoadFailed = true; }
            catch (InvalidOperationException) { LoadFailed = true; }
            return new List<string>();
        }

        public void Save(IEnumerable<string> paths)
        {
            if (paths == null) throw new ArgumentNullException("paths");
            string folder = Path.GetDirectoryName(FilePath); Directory.CreateDirectory(folder);
            string temporary = Path.Combine(folder, ".file-shelf-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(new JavaScriptSerializer().Serialize(Normalize(paths)));
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                if (LoadFailed && !unreadablePreserved && File.Exists(FilePath))
                {
                    File.Copy(FilePath, FilePath + ".unreadable-" + Guid.NewGuid().ToString("N") + ".bak", false);
                    unreadablePreserved = true;
                }
                if (File.Exists(FilePath)) File.Replace(temporary, FilePath, null); else File.Move(temporary, FilePath);
                LoadFailed = false;
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static List<string> Normalize(IEnumerable<string> paths)
        {
            var result = new List<string>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string entry in paths)
            {
                string path, error;
                if (FileShelfPaths.TryNormalize(entry, out path, out error) && seen.Add(path)) result.Add(path);
                if (result.Count == MaxEntries) break;
            }
            return result;
        }
    }

    internal sealed class FileShelf
    {
        private readonly FileShelfStore store;
        private readonly bool demo;
        private readonly List<string> entries;
        public IList<string> Entries { get { return entries.AsReadOnly(); } }
        public bool Dirty { get; private set; }
        public bool LoadFailed { get { return !demo && store.LoadFailed; } }
        public FileShelf(FileShelfStore storage, bool demoMode) { store = storage; demo = demoMode; entries = demo ? new List<string>() : store.Load(); }
        public bool Add(string candidate, out string error)
        {
            string path;
            if (!FileShelfPaths.TryNormalize(candidate, out path, out error)) return false;
            if (entries.Contains(path, StringComparer.OrdinalIgnoreCase)) { error = "此路径已在收纳架中。"; return false; }
            if (entries.Count == FileShelfStore.MaxEntries) { error = "最多收纳 30 项，请先移除一些引用。"; return false; }
            entries.Add(path); Dirty = true; error = null; return true;
        }
        public void Remove(string path) { if (entries.RemoveAll(x => String.Equals(x, path, StringComparison.OrdinalIgnoreCase)) > 0) Dirty = true; }
        public void Clear() { if (entries.Count > 0) { entries.Clear(); Dirty = true; } }
        public bool TryFlush()
        {
            if (!Dirty) return true;
            if (demo) { Dirty = false; return true; }
            try { store.Save(entries); Dirty = false; return true; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }
}
