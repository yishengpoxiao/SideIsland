using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace CodexIsland
{
    public sealed class IslandNotification
    {
        public string Id { get; set; }
        public string AppId { get; set; }
        public string AppName { get; set; }
        public string Title { get; set; }
        public string Body { get; set; }
        public DateTime CreatedAt { get; set; }
        public bool IsRead { get; set; }
        public bool IsDemo { get; set; }
        public IslandNotification() { RepeatCount = 1; }
        public int RepeatCount { get; set; }
        public int PresentationPriority { get; set; }
    }

    public enum NotificationFilter { All, WeChat, QQ, Other }

    // This cache never writes messages to disk. The OS remains the source of truth
    // for active notifications; the short local history lasts only for this process.
    public sealed class NotificationFeed
    {
        public const int MaximumItems = 50;
        public const int MaximumTitleLength = 160;
        public const int MaximumBodyLength = 700;
        public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

        private readonly object gate = new object();
        private readonly Func<DateTime> utcNow;
        private readonly Dictionary<string, IslandNotification> items = new Dictionary<string, IslandNotification>(StringComparer.Ordinal);
        // Remember identities after clearing or trimming so a repeated OS snapshot
        // cannot resurrect an old message or trigger another arrival alert.
        private readonly Dictionary<string, DateTime> seen = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private readonly HashSet<string> blockedSources = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> restoredSources = new HashSet<string>(StringComparer.Ordinal);

        public NotificationFeed() : this(delegate { return DateTime.UtcNow; }) { }

        public NotificationFeed(Func<DateTime> clock)
        {
            if (clock == null) throw new ArgumentNullException("clock");
            utcNow = clock;
        }

        public List<IslandNotification> Items
        {
            get
            {
                lock (gate)
                {
                    Prune(AsUtc(utcNow()));
                    return Snapshot();
                }
            }
        }

        public int UnreadCount
        {
            get
            {
                lock (gate)
                {
                    Prune(AsUtc(utcNow()));
                    return items.Values.Count(item => !item.IsRead);
                }
            }
        }

        // baseline avoids alerting for notifications already present when listening
        // begins. A failed OS read must not be used as the initial baseline.
        public List<IslandNotification> Merge(IEnumerable<IslandNotification> incoming, bool baseline = false, bool isSystemSnapshot = true)
        {
            lock (gate)
            {
                DateTime now = AsUtc(utcNow());
                Prune(now);
                var arrivals = new HashSet<string>(StringComparer.Ordinal);
                if (incoming != null)
                {
                    foreach (IslandNotification raw in incoming)
                    {
                        if (raw == null || String.IsNullOrWhiteSpace(raw.Id) || raw.CreatedAt == DateTime.MinValue) continue;
                        IslandNotification candidate = Normalize(raw);
                        // Reject malformed dates rather than letting them evade expiry.
                        if (candidate.CreatedAt < now - Retention || candidate.CreatedAt > now.AddMinutes(5)) continue;
                        string key = Key(candidate);
                        string sourceKey = NotificationPreferences.SourceKey(candidate);
                        if (blockedSources.Contains(sourceKey))
                        {
                            // Consume identities even while blocked, so restoring an
                            // app never replays the notifications hidden in this session.
                            seen[key] = candidate.CreatedAt;
                            items.Remove(key);
                            continue;
                        }
                        IslandNotification existing;
                        if (items.TryGetValue(key, out existing))
                        {
                            // OS replacement notifications can change the text while
                            // preserving their identity. Reading is a local action.
                            candidate.IsRead = existing.IsRead;
                            items[key] = candidate;
                            continue;
                        }
                        if (seen.ContainsKey(key)) continue;
                        seen[key] = candidate.CreatedAt;
                        candidate.IsRead = baseline || restoredSources.Contains(sourceKey) || candidate.IsRead;
                        items[key] = candidate;
                        if (!candidate.IsRead) arrivals.Add(key);
                    }
                }
                // The first successful OS snapshot after restoring an app is its
                // baseline, including an empty snapshot. Later arrivals alert normally.
                if (isSystemSnapshot) restoredSources.Clear();
                Prune(now);
                return items.Where(pair => arrivals.Contains(pair.Key)).Select(pair => Copy(pair.Value))
                    .OrderByDescending(item => item.CreatedAt).ThenBy(item => item.Id, StringComparer.Ordinal).ToList();
            }
        }

        public List<IslandNotification> GetItems(NotificationFilter filter)
        {
            return Items.Where(item => filter == NotificationFilter.All || Classify(item) == filter).ToList();
        }

        public bool IsSourceBlocked(IslandNotification item)
        {
            lock (gate) return blockedSources.Contains(NotificationPreferences.SourceKey(item));
        }

        public void SetSourceBlocked(string sourceKey, bool blocked)
        {
            if (String.IsNullOrEmpty(sourceKey)) return;
            lock (gate)
            {
                if (blocked)
                {
                    blockedSources.Add(sourceKey);
                    restoredSources.Remove(sourceKey);
                    foreach (string key in items.Where(pair => NotificationPreferences.SourceKey(pair.Value) == sourceKey).Select(pair => pair.Key).ToList()) items.Remove(key);
                }
                else if (blockedSources.Remove(sourceKey)) restoredSources.Add(sourceKey);
            }
        }

        public void MarkAllRead()
        {
            lock (gate)
                foreach (IslandNotification item in items.Values) item.IsRead = true;
        }

        // This clears only the island's memory, not Windows Notification Center.
        public void Clear()
        {
            lock (gate)
            {
                Prune(AsUtc(utcNow()));
                items.Clear();
            }
        }

        public static NotificationFilter Classify(IslandNotification item)
        {
            if (item == null) return NotificationFilter.Other;
            string appName = SanitizeText(item.AppName, 160).ToLowerInvariant();
            string appId = SanitizeText(item.AppId, 1024).ToLowerInvariant();
            HashSet<string> tokens = IdentityTokens(appName + " " + appId);
            // Delimited app identity words, never message text or arbitrary substrings.
            if (tokens.Contains("wechat") || tokens.Contains("weixin") ||
                appName == "微信" || appName == "微信电脑版" || appName == "微信桌面版")
                return NotificationFilter.WeChat;
            bool unrelatedQQProduct = tokens.Contains("qqmusic") || tokens.Contains("qqbrowser") ||
                tokens.Contains("music") || tokens.Contains("browser") ||
                appName.Contains("音乐") || appName.Contains("浏览器");
            if (!unrelatedQQProduct && (tokens.Contains("qq") || tokens.Contains("qqnt") ||
                appName == "腾讯qq" || appName == "qq电脑版" || appName == "qq桌面版"))
                return NotificationFilter.QQ;
            return NotificationFilter.Other;
        }

        // Produces a single plain-text line without control or bidi override chars.
        // Truncation never leaves half of a UTF-16 surrogate pair on the screen.
        public static string SanitizeText(string value, int maximumLength)
        {
            if (String.IsNullOrEmpty(value) || maximumLength <= 0) return String.Empty;
            var result = new StringBuilder(Math.Min(value.Length, maximumLength + 1));
            bool pendingSpace = false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (Char.IsWhiteSpace(c)) { pendingSpace = result.Length > 0; continue; }
                if (Char.IsControl(c) || c == '\u200e' || c == '\u200f' ||
                    (c >= '\u202a' && c <= '\u202e') || (c >= '\u2066' && c <= '\u2069')) continue;
                if (Char.IsLowSurrogate(c)) continue;
                if (Char.IsHighSurrogate(c) && (i + 1 >= value.Length || !Char.IsLowSurrogate(value[i + 1]))) continue;
                if (pendingSpace) { result.Append(' '); pendingSpace = false; }
                result.Append(c);
                if (Char.IsHighSurrogate(c)) result.Append(value[++i]);
                if (result.Length > maximumLength) break;
            }
            if (result.Length <= maximumLength) return result.ToString();
            int length = maximumLength - 1;
            if (length > 0 && Char.IsHighSurrogate(result[length - 1])) length--;
            return result.ToString(0, length).TrimEnd() + "…";
        }

        private void Prune(DateTime now)
        {
            DateTime cutoff = now - Retention;
            foreach (string key in items.Where(pair => pair.Value.CreatedAt < cutoff).Select(pair => pair.Key).ToList()) items.Remove(key);
            foreach (string key in seen.Where(pair => pair.Value < cutoff).Select(pair => pair.Key).ToList()) seen.Remove(key);
            foreach (string key in items.OrderByDescending(pair => pair.Value.CreatedAt)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal).Skip(MaximumItems).Select(pair => pair.Key).ToList()) items.Remove(key);
        }

        private List<IslandNotification> Snapshot()
        {
            return items.Values.OrderByDescending(item => item.CreatedAt).ThenBy(item => item.Id, StringComparer.Ordinal)
                .Select(Copy).ToList();
        }

        private static IslandNotification Normalize(IslandNotification item)
        {
            IslandNotification result = Copy(item);
            result.Id = SanitizeText(result.Id, 256);
            // AppId is an opaque identity, not display text. Sanitizing or truncating
            // it could make one application's block switch affect another app.
            result.AppId = result.AppId ?? "";
            result.AppName = SanitizeText(result.AppName, 80);
            if (result.AppName.Length == 0) result.AppName = "未知应用";
            result.Title = SanitizeText(result.Title, MaximumTitleLength);
            result.Body = SanitizeText(result.Body, MaximumBodyLength);
            result.CreatedAt = AsUtc(result.CreatedAt);
            return result;
        }

        private static DateTime AsUtc(DateTime date)
        {
            return date.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(date, DateTimeKind.Utc) : date.ToUniversalTime();
        }

        private static string Key(IslandNotification item)
        {
            string source = String.IsNullOrWhiteSpace(item.AppId) ? item.AppName : item.AppId;
            return (item.IsDemo ? "demo:" : "os:") + source.Length + ":" + source + ":" + item.Id.Length + ":" +
                item.Id + ":" + item.CreatedAt.Ticks;
        }

        private static IslandNotification Copy(IslandNotification item)
        {
            return new IslandNotification
            {
                Id = item.Id, AppId = item.AppId, AppName = item.AppName, Title = item.Title,
                Body = item.Body, CreatedAt = item.CreatedAt, IsRead = item.IsRead, IsDemo = item.IsDemo
            };
        }

        private static HashSet<string> IdentityTokens(string value)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            var token = new StringBuilder();
            foreach (char c in value)
            {
                if (Char.IsLetterOrDigit(c)) token.Append(c);
                else if (token.Length > 0) { result.Add(token.ToString()); token.Clear(); }
            }
            if (token.Length > 0) result.Add(token.ToString());
            return result;
        }
    }
}
