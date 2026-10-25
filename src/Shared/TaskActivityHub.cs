using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CodexIsland
{
    public sealed class TaskActivityUpdate
    {
        public string Id = "", Title = "", State = "running", Message = "", Source = "local";
        public double? Progress;
    }
    internal sealed class TaskActivity
    {
        public string Id, Title, State, Message, Source;
        public double? Progress;
        public DateTime StartedUtc, UpdatedUtc;
        public bool Active { get { return State == "running" || State == "queued"; } }
        public TaskActivity Copy() { return (TaskActivity)MemberwiseClone(); }
    }
    internal sealed class TaskActivityHub
    {
        internal const int MaximumItems = 80;
        private readonly object gate = new object();
        private readonly Dictionary<string, TaskActivity> items = new Dictionary<string, TaskActivity>(StringComparer.Ordinal);
        public event Action Changed;
        public IList<TaskActivity> Snapshot()
        { lock (gate) return items.Values.OrderByDescending(x => x.Active).ThenByDescending(x => x.UpdatedUtc).Select(x => x.Copy()).ToList(); }
        public TaskActivity Current { get { var snapshot = Snapshot(); return snapshot.FirstOrDefault(x => x.Active) ?? snapshot.FirstOrDefault(); } }
        internal static bool Builtin(TaskActivity item) { return item != null && new[] { "batch:", "lan:", "watch:", "ready:" }.Any(prefix => item.Id.StartsWith(prefix, StringComparison.Ordinal)); }
        internal static string DisplayTitle(TaskActivity item) { return item.Id.StartsWith("batch:", StringComparison.Ordinal) ? UiLocalization.Localize("批量文件处理") : item.Title; }
        internal static string DisplaySource(TaskActivity item) { return Builtin(item) || (item.Id.StartsWith("api:", StringComparison.Ordinal) && item.Source == "本地接口") ? UiLocalization.Localize(item.Source) : item.Source; }
        internal static string DisplayMessage(TaskActivity item) { return Builtin(item) && UiLocalization.HasTranslation(item.Message) ? UiLocalization.Localize(item.Message) : item.Message; }
        public void Publish(TaskActivityUpdate update) { Publish(update, DateTime.UtcNow); }
        internal void Publish(TaskActivityUpdate update, DateTime now)
        {
            if (update == null) throw new ArgumentNullException("update");
            string id = (update.Id ?? "").Trim();
            if (id.Length < 1 || id.Length > 80 || id.Any(c => !Char.IsLetterOrDigit(c) && c != '-' && c != '_' && c != '.' && c != ':')) throw new ArgumentException("任务标识应为 1—80 位字母、数字或 - _ . :。");
            string title = (update.Title ?? "").Trim();
            if (title.Length == 0 || title.Length > 160 || title.Any(c => Char.IsControl(c))) throw new ArgumentException("任务标题应为 1—160 字，不能包含控制字符。");
            string state = (update.State ?? "running").ToLowerInvariant();
            if (!new[] { "queued", "running", "completed", "failed", "cancelled" }.Contains(state)) throw new ArgumentException("未知任务状态。");
            if (update.Progress.HasValue && (Double.IsNaN(update.Progress.Value) || Double.IsInfinity(update.Progress.Value) || update.Progress < 0 || update.Progress > 100)) throw new ArgumentException("任务进度应为 0—100，未知进度请留空。");
            string message = update.Message ?? ""; if (message.Length > 4096) throw new ArgumentException("任务说明最多 4096 字。");
            string source = (update.Source ?? "local").Trim(); if (source.Length > 80 || source.Any(Char.IsControl)) throw new ArgumentException("任务来源最多 80 字。");
            lock (gate)
            {
                TaskActivity item;
                if (!items.TryGetValue(id, out item))
                {
                    if (items.Count >= MaximumItems)
                    {
                        var oldest = items.Values.Where(x => !x.Active).OrderBy(x => x.UpdatedUtc).FirstOrDefault();
                        if (oldest == null) throw new InvalidOperationException("活动任务已达上限，请先完成或移除任务。");
                        items.Remove(oldest.Id);
                    }
                    item = new TaskActivity { Id = id, StartedUtc = now }; items.Add(id, item);
                }
                else if (!item.Active && (state == "running" || state == "queued")) item.StartedUtc = now;
                item.Title = title; item.State = state; item.Message = message; item.Source = source;
                item.Progress = state == "completed" ? 100 : update.Progress; item.UpdatedUtc = now;
            }
            Raise();
        }
        public bool Remove(string id) { bool changed; lock (gate) changed = items.Remove(id); if (changed) Raise(); return changed; }
        public void ClearFinished() { lock (gate) foreach (var id in items.Values.Where(x => !x.Active).Select(x => x.Id).ToArray()) items.Remove(id); Raise(); }
        private void Raise() { var callback = Changed; if (callback != null) callback(); }
        internal static string StateLabel(string state)
        { switch (state) { case "queued": return "等待中"; case "completed": return "已完成"; case "failed": return "失败"; case "cancelled": return "已取消"; default: return "进行中"; } }
        internal static string Elapsed(TaskActivity item, DateTime utc)
        {
            var span = (item.Active ? utc : item.UpdatedUtc) - item.StartedUtc;
            if (span < TimeSpan.Zero) span = TimeSpan.Zero;
            return span.TotalHours >= 1 ? ((int)span.TotalHours).ToString(CultureInfo.InvariantCulture) + ":" + span.Minutes.ToString("00") + ":" + span.Seconds.ToString("00") : ((int)span.TotalMinutes).ToString(CultureInfo.InvariantCulture) + ":" + span.Seconds.ToString("00");
        }
    }
}
