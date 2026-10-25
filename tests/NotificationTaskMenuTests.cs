using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace CodexIsland
{
    // Own demo HWND, routed WPF menu service input only. The sole persistent
    // target is a synthetic notification-task book under this fixture's temp dir.
    internal static class NotificationTaskMenuTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly string[] Captions = { "转为待办", "15 分钟后提醒", "1 小时后提醒", "固定为待办", "归档到项目…" };

        internal static void Run()
        {
            string previous = Environment.GetEnvironmentVariable("CODEX_ISLAND_DATA_DIR"), language = UiLocalization.Language, theme = UiTheme.Name;
            string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string folder = Path.GetFullPath(Path.Combine(temp, "SideIsland.NotificationMenu." + Guid.NewGuid().ToString("N")));
            Check(folder.StartsWith(temp, StringComparison.OrdinalIgnoreCase), "fixture must remain in the temp directory");
            IslandWindow window = null; ContextMenu open = null;
            try
            {
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", folder);
                new SettingsStore().Save(new Settings { Language = "zh", Theme = "dark", AlwaysOnTop = false, AutoCollapse = false,
                    HoverExpandEnabled = false, NotificationsEnabled = false, AutoMusic = false, OnlineLyricsEnabled = false });
                window = new IslandWindow(true) { ShowActivated = false, IsHitTestVisible = false, Topmost = false };
                window.Loaded += (s, e) => StopTimers(window);
                window.Show(); window.SetExpanded(true, false); Call(window, "SelectPage", true); Settle(window);
                var feed = Read<NotificationFeed>(window, "notificationFeed");
                var messages = new[] { Message("first", 0), Message("second", 1) };
                feed.Clear(); feed.Merge(messages, false, false); Call(window, "UpdateNotificationList"); Settle(window);
                var list = Read<ListBox>(window, "notificationList");
                var first = FindCard(list, messages[0].Title); var second = FindCard(list, messages[1].Title);
                var cards = new[] { first, second };
                foreach (var card in cards) AssertUnopened(card, "initial card");
                foreach (string locale in new[] { "en", "zh" }) foreach (string appearance in new[] { "light", "dark" })
                {
                    AppAppearance.Configure(locale, appearance);
                    foreach (var card in cards) AssertUnopened(card, locale + "/" + appearance);
                }

                // Use the production action and a real disk-backed task panel,
                // replacing only this demo fixture's child, never the user's data.
                var notes = Read<NotesPanel>(window, "notesPanel");
                var oldTasks = notes.NotificationTasks;
                var persistent = new NotificationTasksPanel(false);
                typeof(NotesPanel).GetProperty("NotificationTasks", Private).GetSetMethod(true).Invoke(notes, new object[] { persistent });
                Read<Expander>(notes, "notificationTasksExpander").Content = persistent;
                oldTasks.Dispose();
                string taskPath = Path.Combine(folder, "notification-tasks.json");
                Check(!File.Exists(taskPath), "no task file before an explicit menu action");

                var global = Read<Border>(window, "shell").ContextMenu; int globalOpens = 0;
                global.Opened += (s, e) => globalOpens++;
                Read<DispatcherTimer>(window, "hoverTimer").Start(); Read<DispatcherTimer>(window, "collapseTimer").Start();
                RequestOpening(first, true);
                open = first.ContextMenu;
                Check(open != null && open.IsOpen && open.Items.Count == 5, "first genuine WPF opening creates and opens all five actions");
                Check(open.Placement == PlacementMode.Center && ReferenceEquals(open.PlacementTarget, first), "keyboard opening is anchored to its card");
                Check(!Read<DispatcherTimer>(window, "hoverTimer").IsEnabled && !Read<DispatcherTimer>(window, "collapseTimer").IsEnabled, "handled opening still stops the island hover/collapse clocks");
                Check(!global.IsOpen && globalOpens == 0, "notification request never opens the ancestor island menu");
                Check((bool)Call(window, "HasPopupInteraction"), "opened notification menu protects its owning island");
                Check(open.Resources.MergedDictionaries.Count == 1, "only the opened menu has one installed style dictionary");
                AssertHeaders(open); AssertUnopened(second, "other card after first opening");
                var dictionary = open.Resources.MergedDictionaries[0];
                CloseMenu(open, window); open = null;
                Check(!(bool)Call(window, "HasPopupInteraction"), "closed menu releases the popup guard");

                var menu = first.ContextMenu;
                object oldBrush = menu.Resources["Island.Card"];
                AppAppearance.Configure("en", "light");
                Check(ReferenceEquals(first.ContextMenu, menu) && ReferenceEquals(menu.Resources.MergedDictionaries[0], dictionary) && menu.Resources.MergedDictionaries.Count == 1, "theme/language refresh reuses the existing menu and style dictionary");
                Check(!ReferenceEquals(oldBrush, menu.Resources["Island.Card"]), "created menu adopts the current theme brush snapshot");
                AssertHeaders(menu); AssertUnopened(second, "unopened card after another appearance switch");
                RequestOpening(first, false); open = first.ContextMenu;
                Check(ReferenceEquals(open, menu) && open.IsOpen && open.Placement == PlacementMode.MousePoint && ReferenceEquals(open.PlacementTarget, first), "mouse opening reuses the same menu at the pointer");
                Check(!global.IsOpen && globalOpens == 0 && (bool)Call(window, "HasPopupInteraction"), "reused menu preserves global-menu isolation and the interaction guard");
                Click((MenuItem)open.Items[0]); CloseMenu(open, window); open = null;
                var saved = new NotificationTaskBook(false).Snapshot();
                var retained = feed.Items.Single(item => item.Id == messages[0].Id);
                Check(saved.Count == 1 && saved[0].Title == retained.Title && saved[0].Body == retained.Body, "actual menu action persists the exact retained notification text after the feed's whitespace normalization");
                Check(persistent.Book.Snapshot().Count == 1 && File.Exists(taskPath), "menu action updates the live panel and its isolated persistent book");
                Check(feed.Items.Any(item => item.Id == messages[0].Id), "task conversion leaves the source notification intact");

                // Privacy does not expose the source through menu captions or
                // automatically open the project editor after the archive action.
                var settings = Read<Settings>(window, "settings"); settings.HideNotificationBody = true;
                Call(window, "ApplyAppearance"); Call(window, "SelectPage", true); window.SetExpanded(true, false); Settle(window);
                first = FindPrivateCard(list, messages[1].AppName);
                AssertUnopened(first, "rebuilt private card");
                RequestOpening(first, true); open = first.ContextMenu;
                AssertHeaders(open);
                Check(open.Items.Cast<MenuItem>().All(item => !Convert.ToString(item.Header).Contains(messages[1].Title) && !Convert.ToString(item.Header).Contains(messages[1].Body)), "private notification never leaks source text into menu captions");
                Click((MenuItem)open.Items[4]); CloseMenu(open, window); open = null;
                Check(persistent.Book.Snapshot().Count == 2 && Read<Border>(persistent, "projectCard").Visibility == Visibility.Collapsed, "private archive saves a task while keeping its project editor hidden");

                Call(window, "SelectPage", true); window.SetExpanded(true, false);
                var blocked = Message("blocked", 2);
                feed.Merge(new[] { blocked }, false, false); Call(window, "UpdateNotificationList"); Settle(window);
                var blockedCard = FindPrivateCard(list, blocked.AppName);
                RequestOpening(blockedCard, true); open = blockedCard.ContextMenu;
                byte[] before = File.ReadAllBytes(taskPath); DateTime written = File.GetLastWriteTimeUtc(taskPath);
                feed.SetSourceBlocked(NotificationPreferences.SourceKey(blocked), true);
                Click((MenuItem)open.Items[0]); CloseMenu(open, window); open = null;
                Check(persistent.Book.Snapshot().Count == 2 && new NotificationTaskBook(false).Snapshot().Count == 2, "blocking a source after opening prevents a stale action from adding a task");
                Check(File.ReadAllBytes(taskPath).SequenceEqual(before) && File.GetLastWriteTimeUtc(taskPath) == written, "blocked action performs no write to the task file");
                Check(!global.IsOpen && globalOpens == 0 && !(bool)Call(window, "HasPopupInteraction"), "all task-menu routes leave no global menu or stale popup guard");
                Console.WriteLine("PASS: lazy notification menus, genuine keyboard/mouse WPF opening, five commands, theme/language reuse, privacy, isolated persistent task conversion and blocked-source no-write guard.");
            }
            finally
            {
                if (open != null) open.IsOpen = false;
                if (window != null) { StopTimers(window); window.Close(); Pump(30); }
                AppAppearance.Configure(language, theme); Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", previous);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }
        private static IslandNotification Message(string id, int offset)
        {
            return new IslandNotification { Id = "lazy-menu-" + id, AppId = "fixture.lazy-menu." + id, AppName = "MENU_SOURCE_" + id,
                Title = "MENU_TITLE_" + id + " 中文😀", Body = "MENU_BODY_" + id + "\noriginal source", CreatedAt = DateTime.UtcNow.AddSeconds(-offset), IsDemo = true };
        }
        private static void AssertUnopened(Border card, string context)
        {
            Check(card.ContextMenu == null && card.ReadLocalValue(FrameworkElement.ContextMenuProperty) == DependencyProperty.UnsetValue, context + ": unopened card acquired a menu");
            Check(card.Resources.MergedDictionaries.Count == 0, context + ": unopened card acquired menu style dictionaries");
        }
        private static void AssertHeaders(ContextMenu menu)
        {
            Check(menu.Items.Count == Captions.Length && menu.Items.Cast<MenuItem>().Select(item => Convert.ToString(item.Header)).SequenceEqual(Captions.Select(UiLocalization.Localize)), "all five menu actions use the current language");
        }
        private static Border FindCard(ListBox list, string title)
        { return list.Items.Cast<Border>().Single(card => Text(card).Contains(title)); }
        private static Border FindPrivateCard(ListBox list, string source)
        { return list.Items.Cast<Border>().Single(card => Text(card).Contains(source)); }
        private static string Text(DependencyObject root)
        {
            string result = root is TextBlock ? ((TextBlock)root).Text : "";
            foreach (object child in LogicalTreeHelper.GetChildren(root)) { var item = child as DependencyObject; if (item != null) result += "\n" + Text(item); }
            return result;
        }
        private static void RequestOpening(FrameworkElement target, bool keyboard)
        {
            target.BringIntoView();
            var type = typeof(ContextMenu).Assembly.GetType("System.Windows.Controls.PopupControlService");
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var method = type.GetMethod("RaiseContextMenuOpeningEvent", flags, null, new[] { typeof(IInputElement), typeof(double), typeof(double), typeof(bool) }, null);
            Check(method != null, "real WPF menu service opening entry exists");
            var service = type.GetMethod("get_Current", flags).Invoke(null, null);
            method.Invoke(service, new object[] { target, keyboard ? -1.0 : 10.0, keyboard ? -1.0 : 10.0, true });
        }
        private static void Click(MenuItem item) { typeof(MenuItem).GetMethod("OnClick", Private).Invoke(item, null); }
        private static void CloseMenu(ContextMenu menu, IslandWindow window)
        {
            bool wasOpen = menu.IsOpen, closed = false;
            RoutedEventHandler handler = (s, e) => closed = true; menu.Closed += handler;
            try {
                menu.IsOpen = false;
                var wait = Stopwatch.StartNew(); do { Pump(15); StopTimers(window); } while (((wasOpen && !closed) || (bool)Call(window, "HasPopupInteraction")) && wait.ElapsedMilliseconds < 1200);
                Pump(30); StopTimers(window); Check(!menu.IsOpen && (!wasOpen || closed) && !(bool)Call(window, "HasPopupInteraction"), "native menu closing completes and releases capture");
            }
            finally { menu.Closed -= handler; }
        }
        private static void Settle(IslandWindow window) { Pump(IslandWindow.TransitionMilliseconds + 50); window.UpdateLayout(); StopTimers(window); }
        private static void StopTimers(IslandWindow window)
        {
            StopObject(window);
            foreach (var panel in Read<Dictionary<string, UIElement>>(window, "toolPanels").Values) StopObject(panel);
            StopObject(Read<NotesPanel>(window, "notesPanel").NotificationTasks);
        }
        private static void StopObject(object target)
        { foreach (var field in target.GetType().GetFields(Private)) if (field.FieldType == typeof(DispatcherTimer)) { var timer = field.GetValue(target) as DispatcherTimer; if (timer != null) timer.Stop(); } }
        private static T Read<T>(object target, string name) { return (T)target.GetType().GetField(name, Private).GetValue(target); }
        private static object Call(object target, string name, params object[] values)
        {
            try { return target.GetType().GetMethod(name, Private).Invoke(target, values); }
            catch (TargetInvocationException error) { throw error.InnerException ?? error; }
        }
        private static void Pump(int milliseconds)
        {
            var frame = new DispatcherFrame(); var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
            timer.Tick += (s, e) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
        }
        private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("Notification task menu: " + message); }
    }
}
