using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexIsland
{
    internal sealed class ReminderItem
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public long DueUtcTicks { get; set; }
        public bool Daily { get; set; }
        public int Hour { get; set; }
        public int Minute { get; set; }
        public bool Enabled { get; set; }
        public bool Completed { get; set; }
        [ScriptIgnore]
        public DateTime DueUtc { get { return new DateTime(DueUtcTicks, DateTimeKind.Utc); } }
        public ReminderItem Copy() { return (ReminderItem)MemberwiseClone(); }
    }

    internal sealed class ReminderStore
    {
        internal const int MaxItems = 30, MaxTitleLength = 80, MaxFileBytes = 64 * 1024;
        public string FilePath { get; private set; }
        public bool LoadFailed { get; private set; }
        private bool unreadablePreserved;
        public ReminderStore(string path = null) { FilePath = Path.GetFullPath(path ?? Path.Combine(Path.GetDirectoryName(new SettingsStore().FilePath), "reminders.json")); }
        public List<ReminderItem> Load()
        {
            LoadFailed = false; unreadablePreserved = false;
            try
            {
                if (!File.Exists(FilePath)) return new List<ReminderItem>();
                if (new FileInfo(FilePath).Length > MaxFileBytes) throw new IOException();
                var items = new JavaScriptSerializer { MaxJsonLength = MaxFileBytes, RecursionLimit = 12 }.Deserialize<List<ReminderItem>>(File.ReadAllText(FilePath, new UTF8Encoding(false, true)));
                if (items == null) throw new ArgumentException();
                var result = Normalize(items);
                if (result.Count != items.Count) LoadFailed = true;
                return result;
            }
            catch (IOException) { LoadFailed = true; }
            catch (UnauthorizedAccessException) { LoadFailed = true; }
            catch (ArgumentException) { LoadFailed = true; }
            catch (InvalidOperationException) { LoadFailed = true; }
            return new List<ReminderItem>();
        }
        internal static List<ReminderItem> Normalize(IEnumerable<ReminderItem> source)
        {
            var items = new List<ReminderItem>(); var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in source)
            {
                Guid id;
                if (item == null || !Guid.TryParse(item.Id, out id) || !ids.Add(item.Id) || String.IsNullOrWhiteSpace(item.Title) || item.DueUtcTicks <= DateTime.MinValue.Ticks || item.DueUtcTicks >= DateTime.MaxValue.AddDays(-2).Ticks || item.Hour < 0 || item.Hour > 23 || item.Minute < 0 || item.Minute > 59) continue;
                var safe = item.Copy(); safe.Title = item.Title.Trim();
                if (safe.Title.Length > MaxTitleLength) safe.Title = safe.Title.Substring(0, Char.IsHighSurrogate(safe.Title[MaxTitleLength - 1]) ? MaxTitleLength - 1 : MaxTitleLength);
                if (safe.Completed) safe.Enabled = false;
                items.Add(safe); if (items.Count == MaxItems) break;
            }
            return items;
        }
        public void Save(List<ReminderItem> items)
        {
            if (items == null) throw new ArgumentNullException("items");
            var safe = Normalize(items);
            if (safe.Count != items.Count) throw new ArgumentException("提醒数据无效");
            string directory = Path.GetDirectoryName(FilePath); Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, ".reminders-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                // Preserve unreadable original data before either a user edit or a due-time update.
                if (LoadFailed && !unreadablePreserved && File.Exists(FilePath)) { File.Copy(FilePath, FilePath + ".unreadable-" + Guid.NewGuid().ToString("N") + ".bak", false); unreadablePreserved = true; }
                byte[] bytes = new UTF8Encoding(false).GetBytes(new JavaScriptSerializer().Serialize(safe));
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                if (File.Exists(FilePath)) File.Replace(temporary, FilePath, null); else File.Move(temporary, FilePath);
                LoadFailed = false;
            }
            finally
            {
                if (File.Exists(temporary))
                    try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    // All scheduling uses an injected clock. Only the panel owns a DispatcherTimer.
    internal sealed class ReminderBook
    {
        private List<ReminderItem> items;
        private readonly Func<DateTime> utcNow;
        private readonly Func<TimeZoneInfo> zone;
        private readonly Action<List<ReminderItem>> save;
        private DateTime nextRetryUtc = DateTime.MinValue;
        public bool Dirty { get; private set; }
        public string LastError { get; private set; }
        public List<ReminderItem> Items { get { return items.Select(x => x.Copy()).ToList(); } }
        public ReminderBook(IEnumerable<ReminderItem> initial, Action<List<ReminderItem>> persist, Func<DateTime> clock = null, Func<TimeZoneInfo> timeZone = null)
        {
            items = ReminderStore.Normalize(initial ?? new List<ReminderItem>()); save = persist ?? delegate { };
            utcNow = clock ?? (() => DateTime.UtcNow); zone = timeZone ?? (() => TimeZoneInfo.Local);
        }
        public bool AddMinutes(string title, int minutes)
        {
            if (minutes < 1 || minutes > 525600) throw new ArgumentException("提醒时间需在 1 分钟到 1 年之间");
            return Add(title, utcNow().AddMinutes(minutes), false, 0, 0);
        }
        public bool AddLocal(string title, DateTime local, bool daily)
        {
            return Add(title, LocalToUtc(local, zone()), daily, local.Hour, local.Minute);
        }
        private bool Add(string title, DateTime due, bool daily, int hour, int minute)
        {
            if (items.Count >= ReminderStore.MaxItems) throw new ArgumentException("最多保留 30 条提醒，请先删除已完成的提醒");
            title = (title ?? "").Trim();
            if (title.Length == 0 || title.Length > ReminderStore.MaxTitleLength) throw new ArgumentException("请输入 1—80 字的提醒标题");
            if (due <= utcNow()) throw new ArgumentException("请选择未来的日期和时间");
            if (due >= DateTime.MaxValue.AddDays(-2)) throw new ArgumentException("日期超出支持范围");
            items.Add(new ReminderItem { Id = Guid.NewGuid().ToString("N"), Title = title, DueUtcTicks = due.Ticks, Daily = daily, Hour = hour, Minute = minute, Enabled = true });
            Dirty = true; return TryFlush();
        }
        public bool Remove(string id)
        {
            if (items.RemoveAll(x => x.Id == id) == 0) return true;
            Dirty = true; return TryFlush();
        }
        public bool SetEnabled(string id, bool enabled, out string explanation)
        {
            explanation = enabled ? "提醒已恢复" : "提醒已暂停";
            var item = items.FirstOrDefault(x => x.Id == id); if (item == null) return true;
            var now = utcNow();
            if (enabled && (item.Completed || item.DueUtc <= now))
            {
                item.DueUtcTicks = (item.Daily ? NextDaily(now, item.Hour, item.Minute, zone()) : now.AddMinutes(10)).Ticks;
                explanation = item.Daily ? "已恢复，从下一个每日时刻开始" : "已恢复，改为 10 分钟后提醒";
            }
            item.Enabled = enabled; item.Completed = false; Dirty = true; return TryFlush();
        }
        public bool TryFlush()
        {
            if (!Dirty) return true;
            try { save(Items); Dirty = false; LastError = null; nextRetryUtc = DateTime.MinValue; return true; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            LastError = "尚未保存：请检查本地目录权限或磁盘空间。到期提醒将在保存成功后发出。";
            nextRetryUtc = utcNow().AddSeconds(30); return false;
        }
        public List<ReminderItem> Poll()
        {
            var now = utcNow(); var due = new List<ReminderItem>();
            if (now < nextRetryUtc) return due;
            var candidate = Items;
            foreach (var item in candidate)
            {
                if (!item.Enabled || item.Completed || item.DueUtc > now) continue;
                due.Add(item.Copy());
                if (item.Daily) item.DueUtcTicks = NextDaily(now, item.Hour, item.Minute, zone()).Ticks;
                else { item.Completed = true; item.Enabled = false; }
            }
            if (due.Count == 0) { if (Dirty) TryFlush(); return due; }
            // Persist delivery state first: a restart or a second timer tick cannot duplicate a delivery.
            var previous = items; items = candidate; Dirty = true;
            if (TryFlush()) return due;
            items = previous; return new List<ReminderItem>();
        }
        internal static DateTime LocalToUtc(DateTime local, TimeZoneInfo zone)
        {
            local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            // A time inside a spring-forward gap moves to the first valid local minute.
            int adjustment = 0;
            while (zone.IsInvalidTime(local)) { local = local.AddMinutes(1); if (++adjustment > 1440) throw new ArgumentException("当地时间不可用"); }
            if (zone.IsAmbiguousTime(local))
            {
                // Pick the later occurrence once when clocks move backwards.
                TimeSpan offset = zone.GetAmbiguousTimeOffsets(local).Min();
                return new DateTimeOffset(local, offset).UtcDateTime;
            }
            return TimeZoneInfo.ConvertTimeToUtc(local, zone);
        }
        internal static DateTime NextDaily(DateTime afterUtc, int hour, int minute, TimeZoneInfo zone)
        {
            DateTime local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(afterUtc, DateTimeKind.Utc), zone);
            DateTime next = local.Date.AddHours(hour).AddMinutes(minute);
            DateTime result = LocalToUtc(next, zone);
            if (result <= afterUtc) result = LocalToUtc(next.AddDays(1), zone);
            return result;
        }
    }
}
