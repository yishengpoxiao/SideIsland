using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexIsland
{
    internal sealed class SettingsBackupBundle
    {
        public string Format = "SideIsland.Settings";
        public int Version = 1;
        public Dictionary<string, string> Files = new Dictionary<string, string>(StringComparer.Ordinal);
    }
    internal sealed class SettingsBackupService
    {
        internal const int MaximumBytes = 1024 * 1024, MaximumFileBytes = 256 * 1024;
        internal static readonly string[] Names = { "settings.json", "desktop-experience.json", "layout-profiles.json", "notification-sources.json", "music-preferences.json", "auto-scene-rules.json", "widget-layouts.json", "task-activity-preferences.json" };
        private readonly string directory;
        private readonly Action<string, byte[]> writer;
        internal SettingsBackupService(string folder, Action<string, byte[]> write = null) { directory = Path.GetFullPath(folder); writer = write ?? AtomicWrite; }
        internal string PendingPath { get { return Path.Combine(directory, "pending-settings-import.json"); } }
        internal string RecoveryPath { get { return Path.Combine(directory, "settings-import-recovery.json"); } }
        private string ApplyingPath { get { return Path.Combine(directory, "settings-import-in-progress.json"); } }
        private bool rollbackFailed;
        private static JavaScriptSerializer Serializer(int maximum = MaximumBytes) { return new JavaScriptSerializer { MaxJsonLength = maximum, RecursionLimit = 32 }; }
        internal SettingsBackupBundle Export()
        {
            var bundle = new SettingsBackupBundle();
            foreach (string name in Names)
            {
                string path = Path.Combine(directory, name); if (!File.Exists(path)) continue;
                bundle.Files[name] = SettingsBackupSchema.Clean(name, ReadBounded(path, MaximumFileBytes), true);
            }
            return Canonical(bundle);
        }
        internal void ExportTo(string path)
        { ValidateExportPath(path); AtomicWrite(Path.GetFullPath(path), new UTF8Encoding(false, true).GetBytes(Encode(Export()))); }
        internal void ValidateExportPath(string path)
        {
            string target = Path.GetFullPath(path), prefix = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (String.Equals(target, directory, StringComparison.OrdinalIgnoreCase) || target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("请选择应用数据目录以外的位置，避免覆盖当前偏好设置。");
            RejectReparsePath(target);
        }
        internal static string Encode(SettingsBackupBundle bundle) { return Serializer().Serialize(Canonical(bundle)); }
        internal static SettingsBackupBundle Read(string path)
        { return ReadObject(Object(ReadBounded(path, MaximumBytes))); }
        private static SettingsBackupBundle ReadObject(Dictionary<string, object> root)
        {
            if (root.Count != 3 || !root.ContainsKey("Format") || !root.ContainsKey("Version") || !root.ContainsKey("Files") || !(root["Format"] is string) || !(root["Version"] is int)) throw new ArgumentException("备份格式或版本无效。");
            var files = root["Files"] as Dictionary<string, object>; if (files == null) throw new ArgumentException("备份格式或版本无效。");
            var bundle = new SettingsBackupBundle { Format = (string)root["Format"], Version = (int)root["Version"] };
            foreach (var pair in files) { if (!(pair.Value is string)) throw new ArgumentException("备份格式或版本无效。"); bundle.Files.Add(pair.Key, (string)pair.Value); }
            return Canonical(bundle);
        }
        internal void Stage(SettingsBackupBundle bundle)
        { if (File.Exists(ApplyingPath)) throw new IOException("上次导入尚未完成，请重新启动后重试。"); writer(PendingPath, new UTF8Encoding(false, true).GetBytes(Encode(bundle))); }
        internal bool HasPending { get { return File.Exists(PendingPath) || File.Exists(ApplyingPath); } }
        internal void Cancel()
        { if (File.Exists(ApplyingPath)) throw new IOException("上次导入尚未完成，请重新启动后重试。"); if (File.Exists(PendingPath)) File.Delete(PendingPath); }
        internal bool ApplyPending()
        {
            SettingsBackupBundle bundle; string transaction;
            if (File.Exists(ApplyingPath))
            {
                var marker = Object(ReadBounded(ApplyingPath, MaximumBytes + 256)); object id; Guid parsed;
                if (!marker.TryGetValue("TransactionId", out id) || !(id is string) || !Guid.TryParseExact((string)id, "N", out parsed)) throw new ArgumentException("备份格式或版本无效。");
                transaction = (string)id; marker.Remove("TransactionId"); bundle = ReadObject(marker); RestoreRecovery(transaction);
            }
            else
            {
                if (!File.Exists(PendingPath)) return false; bundle = Read(PendingPath); transaction = Guid.NewGuid().ToString("N");
                var marker = Object(Encode(bundle)); marker["TransactionId"] = transaction;
                AtomicWrite(ApplyingPath, new UTF8Encoding(false, true).GetBytes(Serializer(MaximumBytes + 256).Serialize(marker)));
                try { File.Delete(PendingPath); } catch { try { File.Delete(ApplyingPath); } catch { } throw; }
            }
            try { Apply(bundle, transaction); }
            catch
            {
                if (!rollbackFailed)
                {
                    AtomicWrite(PendingPath, new UTF8Encoding(false, true).GetBytes(Encode(bundle))); File.Delete(ApplyingPath);
                }
                throw;
            }
            try { File.Delete(ApplyingPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return true;
        }
        internal void Apply(SettingsBackupBundle bundle)
        { Apply(bundle, Guid.NewGuid().ToString("N")); }
        private void Apply(SettingsBackupBundle bundle, string transaction)
        {
            rollbackFailed = false; bundle = Canonical(bundle); Directory.CreateDirectory(directory);
            var before = new Dictionary<string, byte[]>(); var after = new Dictionary<string, byte[]>();
            foreach (var pair in Names.Where(bundle.Files.ContainsKey).Select(name => new KeyValuePair<string, string>(name, bundle.Files[name])))
            {
                string path = Path.Combine(directory, pair.Key); RejectReparsePath(path); before[pair.Key] = File.Exists(path) ? ReadBytesBounded(path, MaximumFileBytes) : null;
                var values = Object(pair.Value);
                if (pair.Key == "settings.json")
                {
                    var current = before[pair.Key] == null ? new Dictionary<string, object>() : Object(new UTF8Encoding(false, true).GetString(before[pair.Key]));
                    foreach (var setting in values) current[setting.Key] = setting.Value;
                    values = current;
                }
                if (pair.Key == "layout-profiles.json") { values["FocusRestoreArmed"] = false; values["Previous"] = null; }
                after[pair.Key] = new UTF8Encoding(false, true).GetBytes(Serializer().Serialize(values));
                if (after[pair.Key].Length > MaximumFileBytes) throw new ArgumentException("设置文件超过大小限制。");
            }
            // One recoverable copy of the affected preferences is kept locally.
            var recovery = before.ToDictionary(x => x.Key, x => x.Value == null ? null : Convert.ToBase64String(x.Value));
            AtomicWrite(RecoveryPath, Encoding.UTF8.GetBytes(Serializer(MaximumBytes * 2).Serialize(new { Version = 1, TransactionId = transaction, Files = recovery })));
            var committed = new List<string>();
            try { foreach (var pair in after) { committed.Add(pair.Key); writer(Path.Combine(directory, pair.Key), pair.Value); } }
            catch (Exception error)
            {
                foreach (string name in committed.AsEnumerable().Reverse())
                {
                    try { RestoreOriginal(name, before[name]); } catch { rollbackFailed = true; }
                }
                if (rollbackFailed) throw new IOException("导入未完成，部分文件无法恢复；原文件恢复副本已保留，请重新启动以重试恢复。", error);
                throw;
            }
        }
        private void RestoreOriginal(string name, byte[] bytes)
        {
            string path = Path.Combine(directory, name); RejectReparsePath(path);
            if (bytes == null) { if (File.Exists(path)) File.Delete(path); } else AtomicWrite(path, bytes);
        }
        private void RestoreRecovery(string transaction)
        {
            var record = Serializer(MaximumBytes * 2).DeserializeObject(ReadBounded(RecoveryPath, MaximumBytes * 2)) as Dictionary<string, object>;
            if (record == null || record.Count != 3 || !record.ContainsKey("Version") || !(record["Version"] is int) || (int)record["Version"] != 1 || !record.ContainsKey("TransactionId") || !System.Object.Equals(record["TransactionId"], transaction) || !record.ContainsKey("Files")) throw new ArgumentException("原文件恢复副本与待导入事务不匹配，请保留文件并检查数据目录。");
            var raw = record["Files"] as Dictionary<string, object>;
            if (raw == null || raw.Count == 0 || raw.Count > Names.Length) throw new ArgumentException("备份格式或版本无效。");
            var originals = new Dictionary<string, byte[]>();
            foreach (var pair in raw)
            {
                if (!Names.Contains(pair.Key, StringComparer.Ordinal) || (pair.Value != null && !(pair.Value is string))) throw new ArgumentException("备份格式或版本无效。");
                byte[] bytes = pair.Value == null ? null : Convert.FromBase64String((string)pair.Value);
                if (bytes != null && bytes.Length > MaximumFileBytes) throw new ArgumentException("设置文件超过大小限制。"); originals[pair.Key] = bytes;
            }
            foreach (var pair in originals) RestoreOriginal(pair.Key, pair.Value);
        }
        internal static void Validate(SettingsBackupBundle bundle)
        { Canonical(bundle); }
        private static SettingsBackupBundle Canonical(SettingsBackupBundle bundle)
        {
            if (bundle == null || bundle.Format != "SideIsland.Settings" || bundle.Version != 1 || bundle.Files == null || bundle.Files.Count == 0 || bundle.Files.Count > Names.Length) throw new ArgumentException("备份格式或版本无效。");
            var copy = new SettingsBackupBundle();
            foreach (var pair in bundle.Files)
            {
                if (!Names.Contains(pair.Key, StringComparer.Ordinal) || pair.Value == null || Encoding.UTF8.GetByteCount(pair.Value) > MaximumFileBytes) throw new ArgumentException("备份包含不支持的文件或大小超限。");
                copy.Files[pair.Key] = SettingsBackupSchema.Clean(pair.Key, pair.Value, false);
            }
            if (Encoding.UTF8.GetByteCount(Serializer().Serialize(copy)) > MaximumBytes) throw new ArgumentException("备份超过 1 MB 限制。"); return copy;
        }
        private static Dictionary<string, object> Object(string json)
        { var result = Serializer().DeserializeObject(json) as Dictionary<string, object>; if (result == null) throw new ArgumentException("设置内容必须是 JSON 对象。"); return result; }
        private static string ReadBounded(string path, int maximum) { return new UTF8Encoding(false, true).GetString(ReadBytesBounded(path, maximum)); }
        private static byte[] ReadBytesBounded(string path, int maximum)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > maximum) throw new ArgumentException("设置文件超过大小限制。");
                byte[] bytes = new byte[(int)stream.Length]; int offset = 0, count;
                while (offset < bytes.Length && (count = stream.Read(bytes, offset, bytes.Length - offset)) > 0) offset += count;
                if (offset != bytes.Length || stream.ReadByte() != -1) throw new IOException("读取设置期间文件发生变化。"); return bytes;
            }
        }
        internal static void AtomicWrite(string path, byte[] bytes)
        {
            RejectReparsePath(path); Directory.CreateDirectory(Path.GetDirectoryName(path)); string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
        private static void RejectReparsePath(string path)
        {
            string current = Path.GetFullPath(path);
            while (!String.IsNullOrEmpty(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("备份路径不能使用符号链接或目录联接，请选择普通文件夹。");
                string parent = Path.GetDirectoryName(current); if (parent == current) break; current = parent;
            }
        }
    }
}
