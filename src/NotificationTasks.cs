using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace CodexIsland
{
    internal sealed class NotificationTask
    {
        public string Id = "", SourceKey = "", Source = "", Title = "", Body = "", Project = "";
        public long CreatedUtcTicks, DueUtcTicks;
        public bool Done, Pinned, ReminderDelivered;
        public NotificationTask Copy() { return (NotificationTask)MemberwiseClone(); }
    }
    internal sealed class NotificationTaskData { public List<NotificationTask> Items = new List<NotificationTask>(); }
    internal sealed class NotificationTaskBook
    {
        internal const int Maximum = 100;
        private readonly LocalDataFile<NotificationTaskData> file;
        private NotificationTaskData data;
        internal NotificationTaskBook(bool demo)
        {
            file = new LocalDataFile<NotificationTaskData>("notification-tasks.json", demo, 1024 * 1024);
            data = file.Load(new NotificationTaskData());
            var original = data.Items;
            data.Items = (original ?? new List<NotificationTask>()).Where(Valid).GroupBy(x => x.Id).Select(x => x.First()).Take(Maximum).ToList();
            if (original == null || original.Count != data.Items.Count) file.MarkInvalid();
        }
        private static bool Valid(NotificationTask item)
        { Guid id; return item != null && Guid.TryParseExact(item.Id, "N", out id) && item.Title != null && item.Title.Length > 0 && item.Title.Length <= 240 && item.Body != null && item.Body.Length <= 4000 && item.Source != null && item.Source.Length <= 160 && item.Project != null && item.Project.Length <= 60 && item.SourceKey != null && item.SourceKey.Length == 64 && item.CreatedUtcTicks > 0 && item.CreatedUtcTicks <= DateTime.MaxValue.Ticks && item.DueUtcTicks >= 0 && item.DueUtcTicks <= DateTime.MaxValue.Ticks; }
        internal IList<NotificationTask> Snapshot()
        { return data.Items.OrderBy(x => x.Done).ThenByDescending(x => x.Pinned).ThenByDescending(x => x.CreatedUtcTicks).Select(x => x.Copy()).ToList(); }
        internal NotificationTask Add(IslandNotification message, int remindMinutes, DateTime utc)
        {
            if (message == null) throw new ArgumentNullException("message");
            if (remindMinutes < 0 || remindMinutes > 10080) throw new ArgumentException("提醒时间应在一周内。");
            string key;
            using (var hash = SHA256.Create()) key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes((message.AppId ?? "") + "\n" + (message.Id ?? "") + "\n" + message.CreatedAt.Ticks + "\n" + (message.Title ?? "") + "\n" + (message.Body ?? "")))).Replace("-", "");
            var existing = data.Items.FirstOrDefault(x => x.SourceKey == key && !x.Done);
            var result = existing;
            Change(() =>
            {
                if (result == null)
                {
                    if (data.Items.Count >= Maximum) throw new InvalidOperationException("通知待办已达 100 项，请先移除已完成事项。");
                    result = new NotificationTask { Id = Guid.NewGuid().ToString("N"), SourceKey = key, Source = Bound(message.AppName, 160), Title = Bound(String.IsNullOrWhiteSpace(message.Title) ? message.Body : message.Title, 240), Body = Bound(message.Body, 4000), CreatedUtcTicks = utc.Ticks };
                    if (String.IsNullOrWhiteSpace(result.Title)) result.Title = "通知待办";
                    data.Items.Add(result);
                }
                if (remindMinutes > 0) { result.DueUtcTicks = utc.AddMinutes(remindMinutes).Ticks; result.ReminderDelivered = false; }
            });
            return result.Copy();
        }
        internal void SetDone(string id, bool done) { Change(() => { var item = Find(id); item.Done = done; }); }
        internal void SetPinned(string id, bool value) { Change(() => Find(id).Pinned = value); }
        internal void SetProject(string id, string project)
        {
            project = (project ?? "").Trim(); if (project.Length > 60 || project.Any(Char.IsControl)) throw new ArgumentException("项目名称最多 60 字，不能包含控制字符。");
            Change(() => Find(id).Project = project);
        }
        internal void Remove(string id) { Change(() => data.Items.RemoveAll(x => x.Id == id)); }
        internal IList<NotificationTask> TakeDue(DateTime utc)
        {
            var due = data.Items.Where(x => !x.Done && !x.ReminderDelivered && x.DueUtcTicks > 0 && x.DueUtcTicks <= utc.Ticks).Select(x => x.Copy()).ToList();
            if (due.Count > 0) Change(() => { foreach (var item in data.Items.Where(x => due.Any(d => d.Id == x.Id))) item.ReminderDelivered = true; });
            return due;
        }
        private NotificationTask Find(string id) { var item = data.Items.FirstOrDefault(x => x.Id == id); if (item == null) throw new ArgumentException("待办不存在。"); return item; }
        private void Change(Action mutation)
        {
            var before = data.Items.Select(x => x.Copy()).ToList();
            try { mutation(); file.Save(data); } catch { data.Items = before; throw; }
        }
        private static string Bound(string value, int maximum)
        { value = (value ?? "").Replace('\0', ' ').Trim(); if (value.Length <= maximum) return value; if (maximum > 0 && Char.IsHighSurrogate(value[maximum - 1])) maximum--; return value.Substring(0, maximum); }
    }
}
