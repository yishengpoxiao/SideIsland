using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CodexIsland
{
    // Only synthetic messages and this fixture's demo window are used. These
    // assertions check actual generated containers, not just virtualization flags.
    internal static class ModeWorkloadTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        public static void Run(string renderDirectory = null)
        {
            string previous = Environment.GetEnvironmentVariable("CODEX_ISLAND_DATA_DIR");
            string directory = Path.Combine(Path.GetTempPath(), "CodexIsland.ModeWorkload." + Guid.NewGuid().ToString("N"));
            IslandWindow window = null;
            try
            {
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", directory);
                new SettingsStore().Save(new Settings { AlwaysOnTop = false, AutoCollapse = false, NotificationsEnabled = false,
                    CustomExpandedSize = true, ExpandedWidth = 440, ExpandedHeight = 680 });
                window = new IslandWindow(true) { ShowActivated = false };
                window.Loaded += (s, e) => StopTimers(window);
                window.Show(); Settle(window); StopTimers(window);
                window.SetExpanded(true, false);
                var feed = Read<NotificationFeed>(window, "notificationFeed");
                var preferences = Read<NotificationPreferences>(window, "notificationPreferences");
                var list = Read<ListBox>(window, "notificationList");
                var settings = Read<Settings>(window, "settings");
                DateTime now = DateTime.UtcNow;
                var messages = Enumerable.Range(0, NotificationFeed.MaximumItems).Select(i => new IslandNotification {
                    Id = "fixture-" + i, AppId = i % 2 == 0 ? "fixture.wechat" : "fixture.qq", AppName = i % 2 == 0 ? "微信" : "QQ",
                    Title = "Synthetic title " + i + " " + new string('t', 140), Body = "Synthetic body " + i + " " + new string('b', 680),
                    CreatedAt = now.AddSeconds(-i), IsDemo = true }).ToList();
                feed.Clear(); preferences.Observe(messages); feed.Merge(messages, false, false); Call(window, "UpdateNotificationList"); Settle(window);
                Check(list.Items.Count == 50, "all fifty retained messages remain available");
                Check(Realized(list) > 0 && Realized(list) < 12, "only viewport containers are generated for fifty long messages");
                var firstCard = list.Items[0]; var secondCard = list.Items[1];
                Call(window, "UpdateNotificationList"); Settle(window);
                Check(ReferenceEquals(firstCard, list.Items[0]) && ReferenceEquals(secondCard, list.Items[1]), "unchanged refresh keeps card instances");
                messages[0].Title = "Synthetic edited title"; feed.Merge(messages, false, false); Call(window, "UpdateNotificationList"); Settle(window);
                Check(!ReferenceEquals(firstCard, list.Items[0]) && ReferenceEquals(secondCard, list.Items[1]), "single-message change replaces only its own card");
                Check(CardText((Border)list.Items[0]).Contains("Synthetic edited title"), "changed card displays latest text");
                if (renderDirectory != null) Render(window, renderDirectory, "notifications-top.png");

                var viewer = Descendants<ScrollViewer>(list).First();
                for (int attempt = 0; attempt < 3; attempt++) { list.ScrollIntoView(list.Items[49]); viewer.ScrollToEnd(); Settle(window); }
                var last = list.ItemContainerGenerator.ContainerFromIndex(49) as ListBoxItem;
                Check(last != null && last.IsVisible, "scrolling reaches the fiftieth message");
                double lastY = last.TransformToAncestor(list).Transform(new Point(0, 0)).Y;
                Check(lastY < list.ActualHeight && lastY + last.ActualHeight > 0, "last message intersects the visible viewport");
                Check(Realized(list) < 12, "scrolling to the end does not realize all history");
                double offset = viewer.VerticalOffset; Call(window, "UpdateNotificationList"); Settle(window);
                Check(Math.Abs(viewer.VerticalOffset - offset) < 1, "unchanged refresh preserves scroll position");
                if (renderDirectory != null) Render(window, renderDirectory, "notifications-bottom.png");

                var timings = new List<double>();
                for (int i = 0; i < 5; i++)
                {
                    Call(window, "SelectPage", false); Settle(window);
                    var watch = Stopwatch.StartNew(); Call(window, "SelectPage", true); window.UpdateLayout(); watch.Stop(); timings.Add(watch.Elapsed.TotalMilliseconds);
                    Settle(window); Check(Realized(list) < 12, "reopening the messages page keeps layout bounded");
                    window.SetExpanded(false, false); Settle(window); window.SetExpanded(true, false); Settle(window);
                    Check(Realized(list) < 12, "expansion keeps history virtualized");
                }
                foreach (int width in new[] { 332, 560, 440 })
                {
                    settings.ExpandedWidth = width; window.SetExpanded(true, false); Settle(window);
                    Check(list.ActualWidth > 0 && Realized(list) < 12, "resizing reflows only visible cards");
                }

                var filters = Read<Dictionary<NotificationFilter, Button>>(window, "filterButtons");
                Click(filters[NotificationFilter.WeChat]); Settle(window);
                Check(list.Items.Count == 25 && list.Items.Cast<Border>().All(card => CardText(card).Contains("微信")), "WeChat filter retains its full scrollable subset");
                Click(filters[NotificationFilter.QQ]); Settle(window);
                Check(list.Items.Count == 25 && list.Items.Cast<Border>().All(card => CardText(card).Contains("QQ")), "QQ filter retains its full scrollable subset");
                Click(filters[NotificationFilter.All]); Settle(window);
                settings.HideNotificationBody = true; Call(window, "UpdateNotificationList"); Settle(window);
                Check(list.Items.Cast<Border>().All(card => !CardText(card).Contains("Synthetic") && CardText(card).Contains("消息内容已隐藏")), "privacy hides title and body in every cached card");
                settings.HideNotificationBody = false; Call(window, "UpdateNotificationList"); Settle(window);
                Check(CardText((Border)list.Items[0]).Contains("Synthetic edited title"), "leaving privacy restores correct card text");

                viewer.ScrollToTop(); Settle(window);
                var block = Descendants<Button>((Border)list.Items[0]).Single(button => (string)button.Content == "屏蔽此应用");
                Click(block); Settle(window);
                Check(list.Items.Count == 25 && list.Items.Cast<Border>().All(card => CardText(card).Contains("QQ")), "real card block action removes only that application's messages");
                Check(feed.IsSourceBlocked(messages[0]) && !feed.IsSourceBlocked(messages[1]), "blocking preserves the other app's source state");
                feed.MarkAllRead(); Call(window, "UpdateNotificationList"); Settle(window);
                Check(feed.UnreadCount == 0 && list.Items.Cast<Border>().All(card => !CardText(card).Contains("●")), "mark read updates badges in recycled cards");
                feed.Clear(); Call(window, "UpdateNotificationList"); Settle(window);
                Check(list.Items.Count == 1 && CardText((Border)list.Items[0]).Contains("此刻安静"), "clear replaces history with the empty state");
                Churn(window, feed, list, now);
                Console.WriteLine("Mode workload fixture: 50 messages; page switch layout " + timings.Min().ToString("F1") + "–" + timings.Max().ToString("F1") + " ms.");
                Console.WriteLine("PASS: bounded notification containers, retained cards/scroll, last-message access, resize, filters, privacy, source blocking and read/clear actions.");
            }
            finally
            {
                if (window != null) { StopTimers(window); window.Close(); }
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", previous);
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }
        private static void Churn(IslandWindow window, NotificationFeed feed, ListBox list, DateTime now)
        {
            // Refill after a clear with genuinely new identities, in shuffled input
            // order. The feed's order, not input enumeration order, drives the list.
            var batch = Enumerable.Range(0, 50).Select(i => new IslandNotification {
                Id = "batch-" + i, AppId = "fixture.batch", AppName = "Batch source", Title = "Batch title " + i,
                Body = "Batch body " + i, CreatedAt = now.AddSeconds(i), IsDemo = true }).ToList();
            feed.Merge(batch.Where((item, i) => i % 2 == 0).Concat(batch.Where((item, i) => i % 2 == 1)), false, false);
            Call(window, "UpdateNotificationList"); Settle(window); Unique(list, 50);
            Check(CardText((Border)list.Items[0]).Contains("Batch title 49") && CardText((Border)list.Items[49]).Contains("Batch title 0"), "full refill respects descending notification order");
            object formerFirst = list.Items[0];
            var newest = new IslandNotification { Id = "batch-new", AppId = "fixture.batch", AppName = "Batch source", Title = "New head", Body = "New body", CreatedAt = now.AddSeconds(51), IsDemo = true };
            feed.Merge(new[] { newest }, false, false); Call(window, "UpdateNotificationList"); Settle(window); Unique(list, 50);
            Check(CardText((Border)list.Items[0]).Contains("New head") && ReferenceEquals(formerFirst, list.Items[1]), "prepending retains existing card and drops only oldest tail");
            foreach (var message in batch) message.Body = "Bulk replacement " + message.Id;
            feed.Merge(batch, false, false); Call(window, "UpdateNotificationList"); Settle(window); Unique(list, 50);
            Check(list.Items.Cast<Border>().Skip(1).All(card => CardText(card).Contains("Bulk replacement")), "bulk replacement leaves no stale or duplicated cards");
            feed.MarkAllRead(); Call(window, "UpdateNotificationList"); Settle(window); Unique(list, 50);
            Check(list.Items.Cast<Border>().All(card => !CardText(card).Contains("●")), "mark-all-read replaces every affected card without dropping history");
            feed.SetSourceBlocked(NotificationPreferences.SourceKey(newest), true); Call(window, "UpdateNotificationList"); Settle(window);
            Check(list.Items.Count == 1 && CardText((Border)list.Items[0]).Contains("此刻安静"), "deleting every source card leaves a single empty state");
            // IsDemo is part of source identity: a real and demo notification with
            // the same AppId/Id/time must never share the wrong source-label card.
            var real = new IslandNotification { Id = "identity", AppId = "fixture.identity", AppName = "Identity source", Title = "Identity title", Body = "Identity body", CreatedAt = now, IsDemo = false };
            var demo = new IslandNotification { Id = real.Id, AppId = real.AppId, AppName = real.AppName, Title = real.Title, Body = real.Body, CreatedAt = real.CreatedAt, IsDemo = true };
            feed.Merge(new[] { real, demo }, false, false); Call(window, "UpdateNotificationList"); Settle(window); Unique(list, 2);
            Check(list.Items.Cast<Border>().Count(card => CardText(card).Contains("示例")) == 1, "real and demo identities retain distinct source labels");
            feed.Clear();
            real.AppId = new string('a', 4097); demo.AppId = new string('b', 4097); demo.IsDemo = false;
            feed.Merge(new[] { real, demo }, false, false); Call(window, "UpdateNotificationList"); Settle(window); Unique(list, 2);
            Check(NotificationPreferences.SourceKey(real) == "" && NotificationPreferences.SourceKey(demo) == "", "unmanageable source switches do not collapse distinct display identities");
        }
        private static void Unique(ListBox list, int count)
        {
            Check(list.Items.Count == count && list.Items.Cast<Border>().Select(card => (string)card.Tag).Distinct().Count() == count, "list item identities remain unique and complete");
        }
        private static int Realized(ListBox list) { return Enumerable.Range(0, list.Items.Count).Count(i => list.ItemContainerGenerator.ContainerFromIndex(i) != null); }
        private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i); var match = child as T; if (match != null) yield return match;
                foreach (var descendant in Descendants<T>(child)) yield return descendant;
            }
        }
        private static string CardText(Border card)
        {
            // The card's logical tree exists even when its container is unrealized.
            var result = new List<string>(); CollectText(card, result); return String.Join("\n", result);
        }
        private static void CollectText(DependencyObject root, List<string> text)
        {
            var label = root as TextBlock; if (label != null) { text.Add(label.Text); return; }
            foreach (object child in LogicalTreeHelper.GetChildren(root)) { var dependency = child as DependencyObject; if (dependency != null) CollectText(dependency, text); }
        }
        private static void Render(IslandWindow window, string directory, string name)
        {
            Directory.CreateDirectory(directory);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(directory, name))) encoder.Save(stream);
        }
        private static void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
        private static T Read<T>(object target, string field) { return (T)target.GetType().GetField(field, Private).GetValue(target); }
        private static object Call(object target, string method, params object[] values)
        {
            try { return target.GetType().GetMethod(method, Private).Invoke(target, values); }
            catch (TargetInvocationException error) { throw error.InnerException ?? error; }
        }
        private static void StopTimers(IslandWindow window)
        {
            foreach (FieldInfo field in typeof(IslandWindow).GetFields(Private)) if (field.FieldType == typeof(DispatcherTimer)) ((DispatcherTimer)field.GetValue(window)).Stop();
        }
        private static void Settle(IslandWindow window)
        {
            window.UpdateLayout(); var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); window.UpdateLayout();
        }
        private static void Check(bool value, string message) { if (!value) throw new Exception("Mode workload: " + message); }
    }
}
