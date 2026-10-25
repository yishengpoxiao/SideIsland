using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace CodexIsland
{
    internal sealed class TextTemplateEntry
    {
        public string Id = "", Name = "", Body = "";
        public DateTime UpdatedUtc;
        internal TextTemplateEntry Copy() { return new TextTemplateEntry { Id = Id, Name = Name, Body = Body, UpdatedUtc = UpdatedUtc }; }
    }
    internal static class TextTemplateRenderer
    {
        private static readonly Regex Tokens = new Regex(@"(?<!\{)\{(datetime|date|time|yyyy|MM|dd|HH|mm|ss)\}(?!\})", RegexOptions.CultureInvariant);
        internal static string Render(string text, DateTime now)
        {
            return Tokens.Replace(text ?? "", match => {
                string key = match.Groups[1].Value;
                string format = key == "date" ? "yyyy-MM-dd" : key == "time" ? "HH:mm:ss" : key == "datetime" ? "yyyy-MM-dd HH:mm:ss" : key;
                return now.ToString(format, CultureInfo.InvariantCulture);
            });
        }
    }
    internal sealed class TextTemplateStore
    {
        internal const int MaximumEntries = 50, MaximumNameLength = 60, MaximumBodyLength = 12000, MaximumStoreBytes = 512 * 1024;
        private readonly string path;
        private readonly bool memoryOnly;
        private readonly object gate = new object();
        private List<TextTemplateEntry> memory = new List<TextTemplateEntry>();
        internal bool LoadFailed { get; private set; }
        internal TextTemplateStore(bool memoryOnly = false, string location = null)
        { this.memoryOnly = memoryOnly; path = Path.GetFullPath(location ?? Path.Combine(Path.GetDirectoryName(new SettingsStore().FilePath), "text-templates.json")); }
        internal List<TextTemplateEntry> Load()
        {
            lock (gate)
            {
                if (memoryOnly) return memory.Select(item => item.Copy()).ToList();
                LoadFailed = false;
                try
                {
                    if (!File.Exists(path)) return new List<TextTemplateEntry>();
                    using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        if (input.Length > MaximumStoreBytes) throw new InvalidDataException();
                        using (var bytes = new MemoryStream())
                        {
                            var buffer = new byte[4096]; int count;
                            while ((count = input.Read(buffer, 0, buffer.Length)) != 0) { if (bytes.Length + count > MaximumStoreBytes) throw new InvalidDataException(); bytes.Write(buffer, 0, count); }
                            byte[] raw = bytes.ToArray(); int skip = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF ? 3 : 0;
                            var values = new JavaScriptSerializer { MaxJsonLength = MaximumStoreBytes, RecursionLimit = 8 }.Deserialize<List<TextTemplateEntry>>(new UTF8Encoding(false, true).GetString(raw, skip, raw.Length - skip));
                            Validate(values); return values;
                        }
                    }
                }
                catch (Exception) { LoadFailed = true; return new List<TextTemplateEntry>(); }
            }
        }
        internal void Save(IEnumerable<TextTemplateEntry> source)
        {
            var values = source.Select(item => item == null ? null : item.Copy()).ToList(); Validate(values);
            string json = new JavaScriptSerializer { MaxJsonLength = MaximumStoreBytes, RecursionLimit = 8 }.Serialize(values);
            var bytes = new UTF8Encoding(false, true).GetBytes(json);
            if (bytes.Length > MaximumStoreBytes) throw new ArgumentException("模板总容量不能超过 512 KB。");
            lock (gate)
            {
                if (memoryOnly) { memory = values; return; }
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                string createdBackup = null;
                try
                {
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) output.Write(bytes, 0, bytes.Length);
                    if (LoadFailed && File.Exists(path))
                    {
                        if (new FileInfo(path).Length > MaximumStoreBytes) throw new IOException("原模板文件过大，请先备份并移走原文件后重试。");
                        string backup = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".bak";
                        File.Copy(path, backup, false);
                        createdBackup = backup;
                    }
                    if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
                    LoadFailed = false;
                    PruneBackups(createdBackup);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }
        private void PruneBackups(string newest)
        {
            string prefix = Path.GetFileName(path) + ".corrupt-";
            try
            {
                var files = new DirectoryInfo(Path.GetDirectoryName(path)).EnumerateFiles(prefix + "*.bak").Where(file => {
                    string suffix = file.Name.Substring(prefix.Length); Guid parsed;
                    return suffix.Length == 54 && suffix[17] == '-' && suffix.EndsWith(".bak", StringComparison.Ordinal) && suffix.Substring(0, 17).All(Char.IsDigit) && Guid.TryParseExact(suffix.Substring(18, 32), "N", out parsed) && (file.Attributes & FileAttributes.ReparsePoint) == 0;
                }).OrderByDescending(file => String.Equals(file.FullName, newest, StringComparison.OrdinalIgnoreCase)).ThenByDescending(file => file.CreationTimeUtc).ThenByDescending(file => file.Name, StringComparer.Ordinal).Skip(3).ToArray();
                foreach (var file in files) try { file.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        internal static void Validate(IList<TextTemplateEntry> values)
        {
            if (values == null || values.Count > MaximumEntries) throw new ArgumentException("最多保存 50 个文本模板。");
            var ids = new HashSet<string>(StringComparer.Ordinal); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in values)
            {
                if (item == null || String.IsNullOrWhiteSpace(item.Id) || item.Id.Length > 64 || !ids.Add(item.Id)) throw new ArgumentException("模板标识无效。");
                if (String.IsNullOrWhiteSpace(item.Name) || item.Name.Length > MaximumNameLength || item.Name != item.Name.Trim() || !names.Add(item.Name)) throw new ArgumentException("模板名称不能为空、重复或超过 60 字。");
                if (item.Body == null || item.Body.Length > MaximumBodyLength) throw new ArgumentException("模板正文最多 12000 字。");
            }
        }
    }
}
