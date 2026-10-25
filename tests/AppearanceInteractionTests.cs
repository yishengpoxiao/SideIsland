using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexIsland
{
    // Exercises only this fixture's demo windows and synthetic notifications.
    // No clipboard, system notifications, process launch or global input.
    internal static class AppearanceInteractionTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        public static void Run()
        {
            string previous = Environment.GetEnvironmentVariable("CODEX_ISLAND_DATA_DIR");
            string folder = Path.Combine(Path.GetTempPath(), "SideIsland.AppearanceInteraction." + Guid.NewGuid().ToString("N"));
            IslandWindow window = null;
            try
            {
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", folder);
                new SettingsStore().Save(new Settings { Language = "zh", Theme = "dark", AlwaysOnTop = false, AutoCollapse = false, NotificationsEnabled = false });
                window = new IslandWindow(true) { ShowActivated = false, IsHitTestVisible = false };
                window.Loaded += (s, e) => StopTimers(window); window.Show(); window.UpdateLayout(); StopTimers(window);
                window.SetExpanded(true, false); window.UpdateLayout();
                HeaderAndData(window);
                SettingsPersistence(window, folder);
                Console.WriteLine("PASS: header language/theme controls, preserved user text and notifications, translated privacy/English search, and settings widget/appearance save round trips.");
            }
            finally
            {
                if (window != null) { StopTimers(window); window.Close(); }
                AppAppearance.Configure("zh", "dark");
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", previous);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
            Check(UiLocalization.Language == "zh" && UiTheme.Name == "dark", "fixture restores the process-wide language and theme after window closure");
        }

        private static void HeaderAndData(IslandWindow window)
        {
            var language = Read<Button>(window, "languageButton"); var theme = Read<Button>(window, "themeButton");
            Check(language != null && theme != null && language.Parent != null && theme.Parent != null, "both appearance controls are installed in the header");
            Check((string)language.Content == "EN" && (string)theme.Content == "☼", "header controls describe the available switches");
            var settings = Read<Settings>(window, "settings");
            var panels = Read<Dictionary<string, UIElement>>(window, "toolPanels");
            var qr = Read<TextBox>(panels["qr"], "input");
            var diffLeft = Read<TextBox>(panels["diff"], "left"); var diffRight = Read<TextBox>(panels["diff"], "right");
            var textInput = Read<TextBox>(panels["text"], "input"); var textOutput = Read<TextBox>(panels["text"], "output");
            var operation = Read<ComboBox>(panels["text"], "operation");
            qr.Text = "刷新"; diffLeft.Text = "设置\nkeep=true"; diffRight.Text = "刷新\nkeep=false";
            textInput.Text = "设置"; textOutput.Text = "刷新"; operation.SelectedIndex = 7;
            StopTimers(window);
            var inputs = new[] { qr, diffLeft, diffRight, textInput, textOutput };
            string[] expected = inputs.Select(box => box.Text).ToArray();
            var feed = Read<NotificationFeed>(window, "notificationFeed"); feed.Clear();
            var message = new IslandNotification { Id = "appearance-user-text", AppId = "fixture.appearance", AppName = "设置", Title = "刷新", Body = "设置", CreatedAt = DateTime.UtcNow, IsDemo = true };
            feed.Merge(new[] { message }, false, false); Call(window, "UpdateNotificationList");
            AssertOriginalNotification(window);
            var reactiveBrush = (SolidColorBrush)Display.Brush("#E3EFE7"); Color darkText = reactiveBrush.Color;
            Click(language);
            Check(settings.Language == "en" && UiLocalization.Language == "en" && (string)language.Content == "中", "header language button changes actual language and its own label");
            AssertInputs(inputs, expected, operation); AssertOriginalNotification(window);
            var search = Read<TextBox>(window, "toolSearch"); search.Text = "QR transfer";
            var toolButtons = Read<Dictionary<string, Button>>(window, "toolButtons");
            Check(toolButtons["qr"].Visibility == Visibility.Visible && Read<TextBlock>(window, "noToolMatches").Visibility == Visibility.Collapsed, "English display-name search finds QR transfer");
            search.Clear();
            Click(theme);
            Check(settings.Theme == "light" && UiTheme.Name == "light" && (string)theme.Content == "☾", "header theme button changes actual theme and its own label");
            Check(!Object.ReferenceEquals(reactiveBrush, Display.Brush("#E3EFE7")) && ((SolidColorBrush)Display.Brush("#E3EFE7")).Color != darkText && reactiveBrush.Color == darkText && reactiveBrush.IsFrozen, "header theme switch replaces snapshots without mutating the previous native brush");
            AssertInputs(inputs, expected, operation); AssertOriginalNotification(window);
            Click(language); Click(theme);
            Check(settings.Language == "zh" && settings.Theme == "dark" && reactiveBrush.Color == darkText, "switching back restores language and original palette");
            AssertInputs(inputs, expected, operation); AssertOriginalNotification(window);

            Click(language); settings.HideNotificationBody = true; Call(window, "ApplyAppearance");
            AssertPrivateNotification(window, "A new notification arrived", "Message content hidden");
            window.SetExpanded(false, false); Call(window, "ShowPeek", message); StopTimers(window);
            Check(Read<TextBlock>(window, "peekTitle").Text == "A new notification arrived" && Read<TextBlock>(window, "peekBody").Text == "Message content hidden", "English privacy placeholders appear in notification previews");
            Click(language);
            AssertPrivateNotification(window, "收到一条新通知", "消息内容已隐藏");
            Check(Read<TextBlock>(window, "peekTitle").Text == "收到一条新通知" && Read<TextBlock>(window, "peekBody").Text == "消息内容已隐藏", "an already-visible preview follows privacy language changes");
            Click(theme); settings.HideNotificationBody = false; Call(window, "ApplyAppearance");
            AssertOriginalNotification(window);
            Check(Read<TextBlock>(window, "peekTitle").Text == "刷新" && Read<TextBlock>(window, "peekBody").Text == "设置", "leaving privacy restores exact user-authored preview text");
            Check(feed.Items.Single().Title == "刷新" && feed.Items.Single().Body == "设置", "appearance changes never rewrite notification source data");
            AssertInputs(inputs, expected, operation);
            Call(window, "EndPeek"); window.SetExpanded(true, false); StopTimers(window);
        }

        private static void SettingsPersistence(IslandWindow owner, string folder)
        {
            string dummy = Path.Combine(folder, "fixture.exe"); File.WriteAllText(dummy, "Fixture only; never executed.");
            var source = new Settings { Language = "en", Theme = "light", CompactMode = "quota", CompactSlot1 = "codex-secondary", CompactSlot2 = "cpu", AlwaysOnTop = false, AutoCollapse = false, NotificationsEnabled = false,
                HorizontalPosition = .23, VerticalPosition = .61, FavoriteTools = "qr,text", LastTool = "qr", CustomExpandedSize = true, ExpandedWidth = 512, ExpandedHeight = 730, CodexPath = dummy, CodexHome = folder };
            var first = SaveDialog(owner, source, dialog => {
                Check(Read<ComboBox>(dialog, "language").SelectedIndex == 1 && Read<ComboBox>(dialog, "theme").SelectedIndex == 1, "settings load the current language and theme");
                Check(Read<ComboBox>(dialog, "compactSlot1").SelectedIndex == Array.IndexOf(CompactWidgetCatalog.Keys, "codex-secondary") && Read<ComboBox>(dialog, "compactSlot2").SelectedIndex == Array.IndexOf(CompactWidgetCatalog.Keys, "cpu"), "settings load each independent widget selection");
                string[] expectedWidgets = { "codex-primary", "codex-secondary", "cpu", "memory", "battery", "clock", "focus", "awake", "notifications", "media", "ports", "activities" };
                Check(CompactWidgetCatalog.Keys.SequenceEqual(expectedWidgets), "settings widget mapping retains all twelve specific keys in order");
                foreach (string slot in new[] { "compactSlot1", "compactSlot2" })
                {
                    var choice = Read<ComboBox>(dialog, slot);
                    Check(choice.Items.Count == 12 && choice.Items.Cast<ComboBoxItem>().Select(item => (string)item.Content).SequenceEqual(expectedWidgets.Select(key => CompactWidgetCatalog.Label(key, UiLocalization.Language))), slot + " exposes all twelve expected widget labels in key order, including task activities");
                }
            });
            Check(first.Language == "en" && first.Theme == "light" && first.CompactSlot1 == "codex-secondary" && first.CompactSlot2 == "cpu", "saving unrelated settings retains all four appearance/widget fields");
            Check(first.FavoriteTools == source.FavoriteTools && first.LastTool == source.LastTool && first.HorizontalPosition == source.HorizontalPosition && first.VerticalPosition == source.VerticalPosition && first.ExpandedWidth == source.ExpandedWidth && first.ExpandedHeight == source.ExpandedHeight && first.CodexPath == dummy && first.CodexHome == folder, "appearance settings save retains navigation, placement, dimensions and user paths");
            var store = new SettingsStore(); store.Save(first); var loaded = store.Load();
            Check(loaded.Language == "en" && loaded.Theme == "light" && loaded.CompactSlot1 == "codex-secondary" && loaded.CompactSlot2 == "cpu", "new selections survive the real settings store");
            var second = SaveDialog(owner, first, dialog => {
                Read<ComboBox>(dialog, "language").SelectedIndex = 0; Read<ComboBox>(dialog, "theme").SelectedIndex = 0;
                Read<ComboBox>(dialog, "compactSlot1").SelectedIndex = Array.IndexOf(CompactWidgetCatalog.Keys, "notifications");
                Read<ComboBox>(dialog, "compactSlot2").SelectedIndex = Array.IndexOf(CompactWidgetCatalog.Keys, "awake");
                Read<ComboBox>(dialog, "compactMode").SelectedIndex = 1;
            });
            Check(second.Language == "zh" && second.Theme == "dark" && second.CompactSlot1 == "notifications" && second.CompactSlot2 == "awake" && second.CompactMode == "clock", "saving legacy clock mode preserves independently edited hidden slot preferences");
            store.Save(second); loaded = store.Load();
            Check(loaded.Language == "zh" && loaded.Theme == "dark" && loaded.CompactSlot1 == "notifications" && loaded.CompactSlot2 == "awake" && loaded.CompactMode == "clock", "edited appearance fields survive persistence together with the legacy mode");
        }

        private static Settings SaveDialog(IslandWindow owner, Settings source, Action<SettingsWindow> inspect)
        {
            var dialog = new SettingsWindow(source) { Owner = owner, ShowActivated = false, IsHitTestVisible = false };
            Exception failure = null;
            dialog.Loaded += (s, e) => dialog.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => {
                try { inspect(dialog); Call(dialog, "Save"); }
                catch (Exception error) { failure = error; dialog.Close(); }
            }));
            bool? result = dialog.ShowDialog();
            if (failure != null) throw new Exception("Appearance settings dialog: " + failure.Message, failure);
            Check(result == true && dialog.Result != null, "real settings Save completes its modal dialog");
            return dialog.Result;
        }
        private static void AssertInputs(TextBox[] inputs, string[] expected, ComboBox operation)
        {
            for (int i = 0; i < inputs.Length; i++) Check(inputs[i].Text == expected[i], "appearance toggles preserve user input/output " + i);
            Check(operation.SelectedIndex == 7, "translation preserves text operation selection");
        }
        private static void AssertOriginalNotification(IslandWindow window) { AssertPrivateNotification(window, "刷新", "设置"); }
        private static void AssertPrivateNotification(IslandWindow window, string title, string body)
        {
            var list = Read<ListBox>(window, "notificationList");
            Check(list.Items.Count == 1, "fixture contains its single synthetic notification");
            var stack = (StackPanel)((Border)list.Items[0]).Child;
            Check(((TextBlock)stack.Children[1]).Text == title && ((TextBlock)stack.Children[2]).Text == body, "notification title and body match the expected data/privacy language");
        }
        private static void StopTimers(IslandWindow window)
        {
            StopObjectTimers(window);
            foreach (var panel in Read<Dictionary<string, UIElement>>(window, "toolPanels").Values) StopObjectTimers(panel);
        }
        private static void StopObjectTimers(object target) { foreach (var field in target.GetType().GetFields(Private)) if (field.FieldType == typeof(DispatcherTimer)) ((DispatcherTimer)field.GetValue(target)).Stop(); }
        private static void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
        private static T Read<T>(object value, string name) { return (T)value.GetType().GetField(name, Private).GetValue(value); }
        private static object Call(object value, string name, params object[] args)
        {
            try { return value.GetType().GetMethod(name, Private).Invoke(value, args); }
            catch (TargetInvocationException error) { throw new Exception(name + ": " + error.InnerException.Message, error.InnerException); }
        }
        private static void Check(bool value, string message) { if (!value) throw new Exception("Appearance interaction: " + message); }
    }
}
