using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexIsland
{
    internal sealed class NotificationSourcePreference
    {
        public string AppId = "", AppName = "";
        public bool Blocked, IsDemo;
        public int Priority;
        public string Key { get { return NotificationPreferences.SourceKey(AppId, AppName, IsDemo); } }
    }

    // Only app identities and the user's switches are persisted, never message text.
    internal sealed class NotificationPreferences
    {
        public bool MergeDuplicates { get; private set; }
        private const int MaximumSources = 500, MaximumFileBytes = 1024 * 1024;
        private readonly bool demo;
        private bool dirty, loadFailed, unreadablePreserved;
        private readonly Dictionary<string, NotificationSourcePreference> sources = new Dictionary<string, NotificationSourcePreference>(StringComparer.Ordinal);
        public string FilePath { get; private set; }
        public string LastError { get; private set; }

        public NotificationPreferences(bool demoMode, string path = null)
        {
            demo = demoMode;
            FilePath = Path.GetFullPath(path ?? Path.Combine(Path.GetDirectoryName(new SettingsStore().FilePath), "notification-sources.json"));
            LastError = ""; MergeDuplicates = true;
            if (!demo) Load();
        }

        public List<NotificationSourcePreference> Sources
        {
            get { return sources.Values.OrderByDescending(source => source.Blocked).ThenBy(source => source.AppName, StringComparer.CurrentCultureIgnoreCase).ThenBy(source => source.AppId, StringComparer.Ordinal).Select(Copy).ToList(); }
        }

        public static string SourceKey(IslandNotification item)
        {
            return item == null ? "" : SourceKey(item.AppId, item.AppName, item.IsDemo);
        }

        public static string SourceKey(string appId, string appName, bool isDemo)
        {
            // Keep AppIds exact: display names, casing and truncation must not merge
            // separately registered apps. An absent AppId falls back to a name only.
            string identity = appId ?? "";
            if (identity.Length > 4096) return "";
            string prefix = isDemo ? "demo:" : "os:";
            if (!String.IsNullOrWhiteSpace(identity)) return prefix + "id:" + identity;
            string name = NotificationFeed.SanitizeText(appName, 80);
            return name.Length == 0 || name == "未知应用" ? "" : prefix + "name:" + name;
        }

        public void Observe(IEnumerable<IslandNotification> notifications)
        {
            bool persistentChanged = false;
            if (notifications != null)
                foreach (IslandNotification item in notifications)
                {
                    if (item == null) continue;
                    string key = SourceKey(item);
                    if (key.Length == 0) continue;
                    string name = NotificationFeed.SanitizeText(item.AppName, 80);
                    if (name.Length == 0) name = "未知应用";
                    NotificationSourcePreference known;
                    if (sources.TryGetValue(key, out known))
                    {
                        if (known.AppName != name) { known.AppName = name; if (!item.IsDemo) persistentChanged = true; }
                    }
                    else if (sources.Count < MaximumSources)
                    {
                        sources[key] = new NotificationSourcePreference { AppId = item.AppId ?? "", AppName = name, IsDemo = item.IsDemo };
                        if (!item.IsDemo) persistentChanged = true;
                    }
                }
            if (persistentChanged) dirty = true;
            // A normal snapshot also retries switches that could not be saved earlier.
            if (dirty) Save();
        }

        public bool SetBlocked(string sourceKey, bool blocked)
        {
            NotificationSourcePreference source;
            if (!sources.TryGetValue(sourceKey, out source)) return false;
            if (source.Blocked == blocked) { if (dirty) Save(); return true; }
            source.Blocked = blocked;
            if (!source.IsDemo) { dirty = true; Save(); }
            return true;
        }

        public int PriorityFor(IslandNotification item)
        { NotificationSourcePreference source; return sources.TryGetValue(SourceKey(item), out source) ? source.Priority : 0; }
        public bool SetPriority(string sourceKey, int priority)
        {
            NotificationSourcePreference source;
            if (!sources.TryGetValue(sourceKey, out source)) return false;
            source.Priority = priority == 1 ? 1 : 0;
            if (!source.IsDemo) { dirty = true; Save(); } return true;
        }
        public void SetMergeDuplicates(bool enabled)
        { MergeDuplicates = enabled; dirty = true; Save(); }

        private void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                if (new FileInfo(FilePath).Length > MaximumFileBytes) throw new InvalidOperationException();
                var serializer = new JavaScriptSerializer { MaxJsonLength = MaximumFileBytes, RecursionLimit = 8 };
                var root = serializer.DeserializeObject(File.ReadAllText(FilePath, new UTF8Encoding(false, true))) as IDictionary<string, object>;
                object list;
                if (root == null || !root.TryGetValue("Sources", out list) || !(list is IList)) throw new InvalidOperationException();
                object merge; if (root.TryGetValue("MergeDuplicates", out merge) && merge is bool) MergeDuplicates = (bool)merge;
                foreach (object raw in (IList)list)
                {
                    var entry = raw as IDictionary<string, object>; object appId, appName, blocked;
                    if (entry == null || !entry.TryGetValue("AppId", out appId) || !(appId is string) || !entry.TryGetValue("AppName", out appName) || !(appName is string)) { loadFailed = true; continue; }
                    bool hasBlocked = entry.TryGetValue("Blocked", out blocked);
                    if (hasBlocked && !(blocked is bool)) loadFailed = true;
                    var source = new NotificationSourcePreference { AppId = (string)appId, AppName = NotificationFeed.SanitizeText((string)appName, 80), Blocked = hasBlocked && blocked is bool && (bool)blocked };
                    object priority; if (entry.TryGetValue("Priority", out priority) && priority is int && (int)priority == 1) source.Priority = 1;
                    if (source.Key.Length == 0 || sources.ContainsKey(source.Key)) { loadFailed = true; continue; }
                    sources[source.Key] = source;
                    if (sources.Count == MaximumSources) { if (((IList)list).Count > MaximumSources) loadFailed = true; break; }
                }
                if (loadFailed) LastError = "部分应用通知设置无法读取；重新保存前会保留原文件备份。";
            }
            catch (Exception error)
            {
                if (!(error is IOException) && !(error is UnauthorizedAccessException) && !(error is ArgumentException) && !(error is InvalidOperationException)) throw;
                loadFailed = true;
                LastError = "应用通知设置暂时无法读取；重新保存前会保留原文件备份。";
            }
        }

        private void Save()
        {
            if (demo) return;
            string temporary = null;
            try
            {
                var entries = sources.Values.Where(source => !source.IsDemo).Select(source => new { source.AppId, source.AppName, source.Blocked, source.Priority }).ToArray();
                string json = new JavaScriptSerializer().Serialize(new { Version = 2, MergeDuplicates = MergeDuplicates, Sources = entries });
                string directory = Path.GetDirectoryName(FilePath);
                Directory.CreateDirectory(directory);
                temporary = Path.Combine(directory, ".notification-sources-" + Guid.NewGuid().ToString("N") + ".tmp");
                byte[] bytes = new UTF8Encoding(false).GetBytes(json + Environment.NewLine);
                if (bytes.Length > MaximumFileBytes) throw new IOException("Notification source metadata exceeded the file size limit.");
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                if (loadFailed && !unreadablePreserved && File.Exists(FilePath))
                {
                    File.Copy(FilePath, FilePath + ".unreadable-" + Guid.NewGuid().ToString("N") + ".bak", false);
                    unreadablePreserved = true;
                }
                if (File.Exists(FilePath)) File.Replace(temporary, FilePath, null); else File.Move(temporary, FilePath);
                dirty = false; loadFailed = false; LastError = "";
            }
            catch (Exception error)
            {
                if (!(error is IOException) && !(error is UnauthorizedAccessException) && !(error is ArgumentException) && !(error is InvalidOperationException)) throw;
                LastError = "更改已在本次运行生效，但暂时无法保存。请检查数据目录权限。";
            }
            finally
            {
                if (temporary != null && File.Exists(temporary))
                    try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        private static NotificationSourcePreference Copy(NotificationSourcePreference source)
        {
            return new NotificationSourcePreference { AppId = source.AppId, AppName = source.AppName, Blocked = source.Blocked, IsDemo = source.IsDemo, Priority = source.Priority };
        }
    }
}
