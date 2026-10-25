using System;
using System.Collections.Generic;
using System.Linq;

namespace CodexIsland
{
    // Synthetic fixtures only. No Windows messages, credentials, or chat databases.
    internal static class NotificationTests
    {
        public static void Run()
        {
            MergeAndReadTests();
            HistoryTests();
            ClassificationTests();
            TextTests();
            SourceBlockingTests();
            NotificationPreferenceTests.Run();
        }

        private static void MergeAndReadTests()
        {
            DateTime now = new DateTime(2026, 10, 22, 10, 0, 0, DateTimeKind.Utc);
            var feed = new NotificationFeed(delegate { return now; });
            IslandNotification original = Message("1", "Tencent.WeChat", "微信", now.AddMinutes(-5));
            Check(feed.Merge(new[] { original }, true).Count == 0 && feed.UnreadCount == 0, "Initial OS snapshot is read without alerts");
            Check(!original.IsRead && feed.Items[0].IsRead, "Incoming objects remain untouched");
            original.Body = "edited synthetic text";
            Check(feed.Merge(new[] { original }).Count == 0 && feed.Items.Count == 1 &&
                feed.Items[0].Body == original.Body && feed.Items[0].IsRead, "Replacement updates text and preserves local read state");

            IslandNotification added = Message("2", "Tencent.QQ", "QQ", now);
            Check(feed.Merge(new[] { added, added }).Count == 1 && feed.UnreadCount == 1, "Duplicate snapshot items alert once");
            var snapshot = feed.Items;
            snapshot[0].Body = "caller change";
            snapshot[0].IsRead = true;
            snapshot.Clear();
            Check(feed.Items.Count == 2 && feed.UnreadCount == 1 && feed.Items[0].Body != "caller change", "Snapshot copies protect feed contents");
            feed.MarkAllRead();
            Check(feed.UnreadCount == 0 && feed.Merge(new[] { added }).Count == 0, "Repeated snapshots preserve mark read");
            Check(feed.Merge(new[] { Message("2", "Other.App", "Other", now) }).Count == 1, "Same OS id from another app is distinct");
            now = now.AddSeconds(1);
            Check(feed.Merge(new[] { Message("2", "Tencent.QQ", "QQ", now) }).Count == 1, "OS id reused at a new time is a new message");
            Check(feed.Merge(null).Count == 0 && feed.Items.Count == 4, "Missing notifications do not erase local history");
            Check(feed.Merge(new[] { (IslandNotification)null, new IslandNotification(), Message(" ", "app", "App", now) }).Count == 0,
                "Malformed identities and dates are ignored");
        }

        private static void HistoryTests()
        {
            DateTime now = new DateTime(2026, 10, 22, 10, 0, 0, DateTimeKind.Utc);
            var feed = new NotificationFeed(delegate { return now; });
            var incoming = Enumerable.Range(0, 60).Select(index => Message(index.ToString(), "app", "App", now.AddSeconds(index - 60))).ToList();
            Check(feed.Merge(incoming).Count == 50 && feed.Items.Count == 50 && feed.UnreadCount == 50, "Feed and returned arrivals are capped at fifty");
            Check(feed.Items[0].Id == "59" && feed.Items[49].Id == "10", "Newest fifty retained in descending order");
            Check(feed.Merge(incoming).Count == 0, "Trimmed OS messages do not reappear or alert");
            feed.Clear();
            Check(feed.Items.Count == 0 && feed.Merge(incoming).Count == 0 && feed.UnreadCount == 0, "Clear suppresses still active OS notifications");
            now = now.AddSeconds(1);
            Check(feed.Merge(new[] { Message("59", "app", "App", now) }).Count == 1, "A fresh reused id survives clearing");
            now = now.AddHours(25);
            Check(feed.Items.Count == 0 && feed.UnreadCount == 0, "History expires after twenty-four hours without another merge");
            Check(feed.Merge(incoming).Count == 0, "Expired OS snapshot messages stay expired");
            Check(feed.Merge(new[] { Message("future", "app", "App", now.AddDays(1)) }).Count == 0, "Malformed future dates cannot evade retention");
        }

        private static void ClassificationTests()
        {
            DateTime now = DateTime.UtcNow;
            Check(NotificationFeed.Classify(Message("1", "Tencent.WeChat_abcdef!App", "WeChat", now)) == NotificationFilter.WeChat, "WeChat package classification");
            Check(NotificationFeed.Classify(Message("1", "Tencent.Weixin", "微信", now)) == NotificationFilter.WeChat, "Weixin classification");
            Check(NotificationFeed.Classify(Message("1", "Tencent.QQNT", "QQ", now)) == NotificationFilter.QQ, "QQ NT classification");
            Check(NotificationFeed.Classify(Message("1", "Tencent.QQ", "QQ", now)) == NotificationFilter.QQ, "QQ classification");
            Check(NotificationFeed.Classify(Message("1", "Tencent.QQMusic", "QQ 音乐", now)) == NotificationFilter.Other, "QQ Music is not QQ chat");
            Check(NotificationFeed.Classify(Message("1", "Tencent.QQBrowser", "QQ浏览器", now)) == NotificationFilter.Other, "QQ Browser is not QQ chat");
            IslandNotification unrelated = Message("1", "Example.AQQount", "Task Manager", now);
            unrelated.Title = "微信 QQ";
            Check(NotificationFeed.Classify(unrelated) == NotificationFilter.Other, "Arbitrary substrings and message content do not classify an app");
            var feed = new NotificationFeed(delegate { return now; });
            feed.Merge(new[] { Message("1", "Tencent.WeChat", "微信", now), Message("2", "Tencent.QQ", "QQ", now), unrelated });
            Check(feed.GetItems(NotificationFilter.All).Count == 3 && feed.GetItems(NotificationFilter.WeChat).Count == 1 &&
                feed.GetItems(NotificationFilter.QQ).Count == 1 && feed.GetItems(NotificationFilter.Other).Count == 1, "App filters partition feed");
        }

        private static void TextTests()
        {
            Check(NotificationFeed.SanitizeText(null, 20) == "" && NotificationFeed.SanitizeText("abc", 0) == "", "Empty text and zero length handled");
            Check(NotificationFeed.SanitizeText("  hello\r\n\tworld\0\u202e  ", 50) == "hello world", "Control characters, direction overrides and extra whitespace removed");
            Check(NotificationFeed.SanitizeText("abcdef", 5) == "abcd…", "Long text shortened with ellipsis");
            string emoji = "A\ud83d\ude00BC";
            Check(NotificationFeed.SanitizeText(emoji, 3) == "A…" && NotificationFeed.SanitizeText(emoji, 4) == "A\ud83d\ude00…", "Truncation preserves surrogate pairs");
            DateTime now = DateTime.UtcNow;
            var feed = new NotificationFeed(delegate { return now; });
            IslandNotification item = Message("long", "app", "App", now);
            item.Title = new string('x', 2000);
            item.Body = new string('y', 4000);
            feed.Merge(new[] { item });
            Check(feed.Items[0].Title.Length <= NotificationFeed.MaximumTitleLength && feed.Items[0].Body.Length <= NotificationFeed.MaximumBodyLength,
                "Cached message text is bounded");
        }

        private static void SourceBlockingTests()
        {
            DateTime now = new DateTime(2026, 10, 22, 10, 0, 0, DateTimeKind.Utc);
            var feed = new NotificationFeed(delegate { return now; });
            IslandNotification first = Message("one", "Vendor.First!App", "同名软件", now);
            IslandNotification second = Message("two", "Vendor.Second!App", "同名软件", now);
            IslandNotification caseDistinct = Message("three", "vendor.first!App", "同名软件", now);
            feed.Merge(new[] { first, second, caseDistinct });
            string key = NotificationPreferences.SourceKey(first);
            feed.SetSourceBlocked(key, true);
            Check(feed.Items.Count == 2 && feed.UnreadCount == 2 && feed.IsSourceBlocked(first), "Blocking removes current notifications and unread count immediately");
            Check(feed.Items.Any(item => item.AppId == second.AppId) && feed.Items.Any(item => item.AppId == caseDistinct.AppId), "Same names and differently registered AppIds remain independent");
            var blockedArrival = Message("late", first.AppId, first.AppName, now.AddSeconds(1));
            Check(feed.Merge(new[] { first, blockedArrival }).Count == 0 && feed.Items.Count == 2, "A late asynchronous snapshot cannot reintroduce blocked notifications");
            feed.SetSourceBlocked(key, false);
            IslandNotification missedWhileBlocked = Message("missed", first.AppId, first.AppName, now.AddSeconds(2));
            var restored = feed.Merge(new[] { first, blockedArrival, missedWhileBlocked });
            Check(restored.Count == 0 && feed.Items.Single(item => item.Id == "missed").IsRead && feed.Items.All(item => item.Id != "late"), "Restoring consumes an app baseline without replaying old notifications");
            Check(feed.Merge(new[] { Message("new", first.AppId, first.AppName, now.AddSeconds(3)) }).Count == 1, "New notifications arrive normally after restore baseline");
            feed.SetSourceBlocked(key, true); feed.SetSourceBlocked(key, false);
            feed.Merge(new IslandNotification[0]);
            Check(feed.Merge(new[] { Message("after-empty", first.AppId, first.AppName, now.AddSeconds(4)) }).Count == 1, "An empty restore snapshot does not silence the next real arrival");
            feed.SetSourceBlocked(key, true); feed.SetSourceBlocked(key, false);
            IslandNotification demo = Message("demo", "demo.app", "演示", now); demo.IsDemo = true;
            feed.Merge(new[] { demo }, false, false);
            Check(feed.Merge(new[] { Message("restore-after-demo", first.AppId, first.AppName, now.AddSeconds(5)) }).Count == 0, "A demo insert does not consume a pending system restore baseline");
            IslandNotification named = Message("fallback", "", "无标识软件", now);
            feed.SetSourceBlocked(NotificationPreferences.SourceKey(named), true);
            Check(feed.Merge(new[] { named }).Count == 0, "Missing AppId uses a stable exact display-name fallback");
            Check(NotificationPreferences.SourceKey(Message("unknown", null, "未知应用", now)) == "", "An unknown source is not grouped into an unsafe global block");
            Check(NotificationPreferences.SourceKey(named) != NotificationPreferences.SourceKey(Message("named", "无标识软件", "Other", now)), "Display-name fallback cannot collide with an AppId");
        }

        private static IslandNotification Message(string id, string appId, string appName, DateTime time)
        {
            return new IslandNotification { Id = id, AppId = appId, AppName = appName, Title = "Synthetic title", Body = "Synthetic message", CreatedAt = time };
        }

        private static void Check(bool condition, string label)
        {
            if (!condition) throw new Exception("Notification test failed: " + label);
        }
    }
}
