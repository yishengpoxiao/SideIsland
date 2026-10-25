using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Threading.Tasks;

namespace CodexIsland
{
    internal static class NotificationLoadingTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        public static void Run()
        {
            string previous = Environment.GetEnvironmentVariable("CODEX_ISLAND_DATA_DIR");
            string folder = Path.Combine(Path.GetTempPath(), "CodexIsland.NotificationLoading." + Guid.NewGuid().ToString("N"));
            IslandWindow window = null;
            try
            {
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", folder);
                window = new IslandWindow(true);
                var state = Read<TextBlock>(window, "notificationState");
                var explanation = Read<TextBlock>(window, "notificationExplanation");
                var retry = Read<Button>(window, "notificationAccess");
                var feed = Read<NotificationFeed>(window, "notificationFeed");
                var message = new IslandNotification { Id = "loading-fixture", AppId = "fixture", AppName = "Fixture", Title = "Synthetic only", CreatedAt = DateTime.UtcNow };
                feed.Merge(new[] { message }, false);

                Apply(window, false, true, null);
                Check(state.Text.Contains("正在同步") && explanation.Text.Contains("其他工具"), "first read explicitly reports background loading");
                Check(retry.Visibility == Visibility.Collapsed, "first read does not offer duplicate requests");
                Apply(window, true, false, "模拟通知读取超时");
                Check(state.Text.Contains("失败") && explanation.Text == "模拟通知读取超时" && retry.IsEnabled && retry.Visibility == Visibility.Visible, "read failure displays its reason and a usable retry action");
                Apply(window, true, true, "模拟通知读取超时");
                Check(state.Text.Contains("失败") && explanation.Text.Contains("超时") && !retry.IsEnabled, "retry retains the failure instead of claiming a successful connection");
                Apply(window, true, false, "模拟通知读取超时");
                Check(retry.IsEnabled && (string)retry.Content == "重新同步通知", "failed retry restores the action");
                Check(feed.Items.Count == 1 && feed.UnreadCount == 1, "loading and failure presentation preserve messages and unread state");
                Apply(window, true, false, null);
                Check(state.Text.Contains("已连接") && retry.Visibility == Visibility.Collapsed, "successful snapshot clears failure and hides retry");
                ConnectionViewport(window);
                Console.WriteLine("PASS: notification loading/failure/retry presentation, recovery, preserved unread history and stable connection-state viewports in detached small/large Chinese/English layouts.");
            }
            finally
            {
                if (window != null) window.Close();
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", previous);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }
        private static void ConnectionViewport(IslandWindow window)
        {
            // Measure only the existing unshown window's own controls. This does
            // not create a presentation HWND or contact the notification API.
            string language = UiLocalization.Language, theme = UiTheme.Name;
            var settings = Read<Settings>(window, "settings");
            var list = Read<FrameworkElement>(window, "notificationListScroll");
            var page = Read<StackPanel>(window, "notificationPage");
            var scroll = Read<ScrollViewer>(window, "detailsScroll");
            var hint = Read<TextBlock>(window, "expandedHint");
            Check(ReferenceEquals(hint.Parent, Read<Grid>(window, "expandedHost")) && Grid.GetRow(hint) == 3,
                "window instruction occupies its own fixed footer row outside the scroll document");
            var root = (FrameworkElement)window.Content;
            var access = new NotificationAccessState?[] { null, NotificationAccessState.Allowed, NotificationAccessState.NeedsPackage,
                NotificationAccessState.NotGranted, NotificationAccessState.Denied, NotificationAccessState.Unsupported, NotificationAccessState.Error };
            int cases = 0, probes = 0;
            var runner = new NotificationSyncRunner(() => { probes++; return Task.FromResult(new NotificationSnapshot()); });
            Set(window, "notificationSync", runner);
            Set(window, "expanded", true);
            Read<Grid>(window, "expandedHost").Visibility = Visibility.Visible;
            Read<StackPanel>(window, "details").Visibility = Visibility.Visible;
            scroll.Visibility = Visibility.Visible;
            Read<StackPanel>(window, "compact").Visibility = Visibility.Collapsed;
            try
            {
                foreach (string locale in new[] { "zh", "en" })
                foreach (int width in new[] { 320, 391, 560 })
                foreach (int height in new[] { 420, 638, 800 })
                foreach (int branch in new[] { -2, -1, 0, 1, 2, 3, 4, 5, 6 })
                {
                    AppAppearance.Configure(locale, theme); window.Width = width; window.Height = height;
                    settings.NotificationsEnabled = branch != -2;
                    Set(window, "notificationReadFailure", branch == -1 ? "模拟读取失败" : null);
                    Set(window, "notificationCachedAccess", branch >= 0 ? access[branch] : null);
                    list.MaxHeight = 999;
                    Call(window, "UpdateNotificationConnection");
                    Layout(root, width, height);
                    // The fixed footer has already reduced the scroll viewport.
                    double surrounding = 0;
                    foreach (FrameworkElement child in page.Children) if (!ReferenceEquals(child, list)) surrounding += child.DesiredSize.Height;
                    double expected = Math.Max(72, scroll.ViewportHeight - surrounding), first = list.MaxHeight;
                    string context = locale + "/" + width + "x" + height + "/branch=" + branch;
                    Check(Math.Abs(first - expected) <= 1.1, "connection viewport fits final text/button layout: " + context + " (" + first + " vs " + expected + "; viewport=" + scroll.ViewportHeight + ", surrounding=" + surrounding + ", header=" + Read<Grid>(window, "expandedHeader").DesiredSize + ", tabs=" + Read<Border>(window, "expandedTabs").DesiredSize + ", host=" + Read<Grid>(window, "expandedHost").ActualWidth + ")");
                    Call(window, "UpdateNotificationConnection"); Layout(root, width, height);
                    Check(Math.Abs(first - list.MaxHeight) <= .1, "repeated connection refresh changes list geometry: " + context);
                    cases++;
                }
                // The demo branch must measure after hiding its access button.
                Set(window, "notificationSync", null); settings.NotificationsEnabled = true;
                Call(window, "UpdateNotificationConnection"); Layout(root, window.Width, window.Height);
                double demoSurrounding = 0;
                foreach (FrameworkElement child in page.Children) if (!ReferenceEquals(child, list)) demoSurrounding += child.DesiredSize.Height;
                Check(Math.Abs(list.MaxHeight - Math.Max(72, scroll.ViewportHeight - demoSurrounding)) <= 1.1, "demo viewport reflects the final hidden access action");
                Check(probes == 0 && new WindowInteropHelper(window).Handle == IntPtr.Zero, "detached viewport checks neither probe notifications nor create a window HWND");
                Console.WriteLine("Notification connection viewports: " + cases + " detached layouts plus final demo layout; zero API probes and zero presentation HWNDs.");
            }
            finally { runner.Dispose(); Set(window, "notificationSync", null); AppAppearance.Configure(language, theme); Set(window, "expanded", false); }
        }
        private static void Layout(FrameworkElement root, double width, double height)
        {
            for (int pass = 0; pass < 3; pass++) { root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout(); }
        }
        private static void Call(IslandWindow window, string name) { typeof(IslandWindow).GetMethod(name, Private).Invoke(window, null); }
        private static void Set(IslandWindow window, string name, object value) { typeof(IslandWindow).GetField(name, Private).SetValue(window, value); }
        private static T Read<T>(IslandWindow window, string name) { return (T)typeof(IslandWindow).GetField(name, Private).GetValue(window); }
        private static void Apply(IslandWindow window, bool hasSnapshot, bool reading, string error)
        {
            typeof(IslandWindow).GetMethod("ApplyNotificationReadState", Private).Invoke(window, new object[] { hasSnapshot, reading, error });
        }
        private static void Check(bool value, string message) { if (!value) throw new Exception("Notification loading: " + message); }
    }
}
