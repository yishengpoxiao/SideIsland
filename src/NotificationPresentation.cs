using System;
using System.Collections.Generic;
using System.Linq;

namespace CodexIsland
{
    // Group only exact repeats from one source within 60 seconds. The feed keeps
    // every identity/read state, so grouping never consumes or replays arrivals.
    internal static class NotificationPresentation
    {
        internal static List<IslandNotification> Project(IEnumerable<IslandNotification> messages, NotificationPreferences preferences)
        {
            var output = new List<IslandNotification>();
            var latest = new Dictionary<string, IslandNotification>(StringComparer.Ordinal);
            foreach (var message in messages.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id, StringComparer.Ordinal))
            {
                string key = Part(NotificationPreferences.SourceKey(message)) + Part(message.AppId) + Part(message.AppName) + Part(message.Title) + Part(message.Body) + message.IsDemo;
                IslandNotification grouped;
                if (preferences.MergeDuplicates && latest.TryGetValue(key, out grouped) && (grouped.CreatedAt - message.CreatedAt).TotalSeconds <= 60)
                { grouped.RepeatCount++; grouped.IsRead = grouped.IsRead && message.IsRead; continue; }
                grouped = new IslandNotification { Id = message.Id, AppId = message.AppId, AppName = message.AppName, Title = message.Title, Body = message.Body,
                    IsRead = message.IsRead, IsDemo = message.IsDemo, CreatedAt = message.CreatedAt, PresentationPriority = preferences.PriorityFor(message) };
                output.Add(grouped); latest[key] = grouped;
            }
            return output.OrderByDescending(x => x.PresentationPriority).ThenByDescending(x => x.CreatedAt).ThenBy(x => x.Id, StringComparer.Ordinal).ToList();
        }
        private static string Part(string value) { value = value ?? ""; return value.Length + ":" + value; }
    }
}
