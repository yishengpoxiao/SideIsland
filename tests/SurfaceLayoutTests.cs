using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CodexIsland
{
    // The windows and messages are owned fixtures. No real notification reads,
    // settings saves, permission requests, clipboard access or global input.
    internal static class SurfaceLayoutTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly List<string> Issues = new List<string>();
        private static int scenarios, captions;
        internal static void Run(string directory = null)
        {
            if (directory == null) directory = Environment.GetEnvironmentVariable("CODEX_ISLAND_SURFACE_ARTIFACTS");
            string previous = Environment.GetEnvironmentVariable("CODEX_ISLAND_DATA_DIR"), language = UiLocalization.Language, theme = UiTheme.Name;
            string folder = Path.Combine(Path.GetTempPath(), "CodexIsland.SurfaceLayout." + Guid.NewGuid().ToString("N"));
            IslandWindow owner = null; SettingsWindow settingsWindow = null; NotificationSourceWindow sourcesWindow = null;
            Issues.Clear(); scenarios = captions = 0;
            try
            {
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", folder);
                new SettingsStore().Save(new Settings { AlwaysOnTop = false, AutoCollapse = false, NotificationsEnabled = false, CustomExpandedSize = true, ExpandedWidth = 440, ExpandedHeight = 680 });
                owner = new IslandWindow(true) { ShowActivated = false }; owner.Loaded += (s, e) => Stop(owner);
                owner.Show(); Settle(owner); owner.SetExpanded(true, false); Call(owner, "SelectPage", true);
                var original = new Settings { AlwaysOnTop = false, NotificationsEnabled = false, CompactMode = "quota", HoverDelayMilliseconds = 1100,
                    CodexPath = "C:\\Fixture\\保存设置\\a long application folder\\Codex.exe", CodexHome = "C:\\Fixture\\中文数据目录\\long project name" };
                settingsWindow = new SettingsWindow(original) { Owner = owner, ShowActivated = false };
                AppAppearance.Register(settingsWindow); settingsWindow.Show(); Settle(settingsWindow);
                Read<ComboBox>(settingsWindow, "side").SelectedIndex = 4;
                Read<ComboBox>(settingsWindow, "compactSlot1").SelectedIndex = Array.IndexOf(CompactWidgetCatalog.Keys, "codex-secondary");
                var preferences = Read<NotificationPreferences>(owner, "notificationPreferences");
                var messages = Messages(); preferences.Observe(messages);
                var sourcePreferences = new NotificationPreferences(true, Path.Combine(folder, "fixture-sources.json")); sourcePreferences.Observe(messages);
                sourcePreferences.SetBlocked(NotificationPreferences.SourceKey(messages[1]), true);
                sourcesWindow = new NotificationSourceWindow(sourcePreferences, (key, blocked) => sourcePreferences.SetBlocked(key, blocked)) { Owner = owner, ShowActivated = false };
                AppAppearance.Register(sourcesWindow); sourcesWindow.Show(); Settle(sourcesWindow);
                var toggled = sourcePreferences.Sources.First();
                var firstToggle = Descendants<Button>(Read<StackPanel>(sourcesWindow, "rows")).First(); firstToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(sourcePreferences.Sources.First(source => source.Key == toggled.Key).Blocked != toggled.Blocked, "source action stopped restoring the intended application");
                Descendants<Button>(Read<StackPanel>(sourcesWindow, "rows")).First().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(sourcePreferences.Sources.First(source => source.AppId == messages[0].AppId).Blocked, "source action stopped disabling the intended application");
                var firstPriority = Descendants<ComboBox>(Read<StackPanel>(sourcesWindow, "rows")).First(); firstPriority.SelectedIndex = 1;
                Check(sourcePreferences.PriorityFor(messages[0]) == 1, "source priority no longer follows the selected option");
                if (directory != null) Directory.CreateDirectory(directory);
                foreach (string testLanguage in new[] { "zh", "en" })
                foreach (string testTheme in new[] { "light", "dark" })
                {
                    AppAppearance.Configure(testLanguage, testTheme);
                    foreach (int width in new[] { 320, 388, 590, 900 })
                    foreach (int height in new[] { 360, 560, 800 })
                    {
                        string context = testLanguage + "/" + testTheme + "/" + width + "x" + height;
                        settingsWindow.Width = width; settingsWindow.Height = height;
                        foreach (bool expanded in new[] { false, true })
                        {
                            foreach (var expander in Descendants<Expander>(settingsWindow)) expander.IsExpanded = expanded;
                            Settle(settingsWindow); CheckDialog(settingsWindow, context + "/settings/modules=" + expanded); scenarios++;
                            Check(Read<TextBox>(settingsWindow, "executable").Text == original.CodexPath && Read<TextBox>(settingsWindow, "home").Text == original.CodexHome, context + ": appearance rewrote a user path");
                            var moduleChecks = Read<Dictionary<string, CheckBox>>(settingsWindow, "moduleChecks");
                            Check(moduleChecks.Count == 38 && moduleChecks.Values.All(c => c.IsChecked == true), context + ": layout changed module preferences");
                        }
                        sourcesWindow.Width = width; sourcesWindow.Height = height;
                        var search = Read<TextBox>(sourcesWindow, "search"); var onlyBlocked = Read<CheckBox>(sourcesWindow, "onlyBlocked");
                        foreach (string state in new[] { "all", "filtered", "empty", "blocked" })
                        {
                            onlyBlocked.IsChecked = state == "blocked"; search.Text = state == "filtered" ? "Fixture 01" : state == "empty" ? "no matching app fixture" : "";
                            Settle(sourcesWindow); CheckDialog(sourcesWindow, context + "/sources/" + state); scenarios++;
                        }
                        onlyBlocked.IsChecked = false; search.Clear(); Settle(sourcesWindow);
                        typeof(NotificationPreferences).GetProperty("LastError").SetValue(sourcePreferences, String.Join(" ", Enumerable.Repeat("Fixture save error / 无法保存偏好。", 45)), null);
                        Call(sourcesWindow, "RefreshSaveStatus"); Settle(sourcesWindow); CheckDialog(sourcesWindow, context + "/sources/long-error"); scenarios++;
                        typeof(NotificationPreferences).GetProperty("LastError").SetValue(sourcePreferences, "", null); Call(sourcesWindow, "RefreshSaveStatus");
                        NotificationStates(owner, messages, width, height, context);
                        if (width == 320 && height == 560)
                        {
                            CheckDropdown(Read<ComboBox>(settingsWindow, "side"), context + "/settings/side");
                            CheckDropdown(Read<ComboBox>(settingsWindow, "compactSlot1"), context + "/settings/widget");
                            foreach (var combo in Descendants<ComboBox>(Read<StackPanel>(sourcesWindow, "rows")).Take(1)) CheckDropdown(combo, context + "/sources/priority");
                            var settingsCard = ((StackPanel)((ScrollViewer)((Grid)settingsWindow.Content).Children.OfType<ScrollViewer>().First()).Content).Children.OfType<Border>().First();
                            var canonical = (SolidColorBrush)Display.Brush("#1A2323"); var actual = (SolidColorBrush)settingsCard.Background;
                            Color expected = UiTheme.IsLight ? Color.FromRgb(255,255,255) : Color.FromRgb(28,28,30);
                            Check(canonical.Color == expected && actual.Color == expected && Object.ReferenceEquals(actual, canonical), context + ": actual settings card color failed to follow theme");
                            var probe = Snapshot(settingsWindow); Point sample = settingsCard.TranslatePoint(new Point(6,24), settingsWindow); var pixel = new byte[4];
                            probe.CopyPixels(new Int32Rect((int)sample.X,(int)sample.Y,1,1),pixel,4,0);
                            Check(pixel[0] == expected.B && pixel[1] == expected.G && pixel[2] == expected.R, context + ": rendered card pixels are stale after theme switch");
                            if (directory != null)
                            {
                                string suffix = testLanguage + "-" + testTheme;
                                File.WriteAllLines(Path.Combine(directory, "colors-" + suffix + ".txt"), new[] { "Theme=" + UiTheme.Name, "Canonical=" + canonical.Color + ";Frozen=" + canonical.IsFrozen + ";Bound=" + System.Windows.Data.BindingOperations.IsDataBound(canonical, SolidColorBrush.ColorProperty),
                                    "SettingsCard=" + actual.Color + ";Frozen=" + actual.IsFrozen + ";Bound=" + System.Windows.Data.BindingOperations.IsDataBound(actual, SolidColorBrush.ColorProperty) + ";Same=" + Object.ReferenceEquals(actual, canonical),
                                    "ComboText=" + Read<ComboBox>(settingsWindow,"side").Foreground, "ButtonFill=" + Descendants<Button>(settingsWindow).First().Background });
                                SaveWindow(settingsWindow, directory, "settings-top-" + suffix); Scroll(settingsWindow, true); SaveWindow(settingsWindow, directory, "settings-bottom-" + suffix);
                                SaveWindow(sourcesWindow, directory, "sources-top-" + suffix); Scroll(sourcesWindow, true); SaveWindow(sourcesWindow, directory, "sources-bottom-" + suffix);
                                SaveWindow(owner, directory, "notifications-" + suffix);
                            }
                        }
                    }
                }
                RawSelectors(owner);
                Check(settingsWindow.Result == null && original.CodexPath.Contains("保存设置") && original.HoverDelayMilliseconds == 1100, "layout inspection saved or mutated original settings");
                if (directory != null) File.WriteAllLines(Path.Combine(directory, "measurements.txt"), new[] { "Scenarios=" + scenarios + "; captions=" + captions + "; issues=" + Issues.Count,
                    "Widths320/388/590/900; heights360/560/800; zh/en; light/dark; settings modules open/closed; source filters; notification states and privacy." }.Concat(Issues));
                if (Issues.Count > 0) throw new Exception("Compact surfaces (" + Issues.Count + "): " + String.Join("\n", Issues.Take(35)));
                Console.WriteLine("PASS: " + scenarios + " compact settings/notification/source layouts and " + captions + " captions across 4 widths,3 heights, Chinese/English and light/dark; fixed equal footers, scroll-end access, dropdown wrapping, protected paths and notification privacy.");
            }
            finally
            {
                if (settingsWindow != null) settingsWindow.Close(); if (sourcesWindow != null) sourcesWindow.Close();
                if (owner != null) { Stop(owner); owner.Close(); }
                AppAppearance.Configure(language, theme); Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", previous);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }
        private sealed class RawLabel { public string Name { get; set; } }
        private static void RawSelectors(Window owner)
        {
            var window = new Window { Owner = owner, Width = 260, Height = 240, ShowActivated = false, ShowInTaskbar = false };
            var body = new StackPanel { Margin = new Thickness(12) }; window.Content = body; AppAppearance.Register(window);
            string original = "中文 raw process / device / project " + new string('W', 500);
            var text = UiLocalization.Protect(new ComboBox { ItemsSource = new[] { original }, SelectedIndex = 0 });
            var model = UiLocalization.Protect(new ComboBox { ItemsSource = new[] { new RawLabel { Name = original } }, DisplayMemberPath = "Name", SelectedIndex = 0 });
            var item = UiLocalization.Protect(new ComboBox()); item.Items.Add(UiLocalization.Protect(new ComboBoxItem { Content = original })); item.SelectedIndex = 0;
            foreach (var combo in new[] { text, model, item }) body.Children.Add(combo);
            try
            {
                window.Show(); Settle(window);
                foreach (var combo in new[] { text, model, item })
                {
                    combo.IsDropDownOpen = true; Settle(window);
                    try
                    {
                        var popup = (Popup)combo.Template.FindName("PART_Popup", combo); var frame = (Border)popup.Child; var scroll = (ScrollViewer)frame.Child;
                        Check(Double.IsNaN(frame.Width) && scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Auto, "compact popup constraint leaked to a raw model selector");
                        Check(frame.ActualWidth > combo.ActualWidth + 1 || scroll.ScrollableWidth > 0, "long raw selector cannot expose its complete value"); scenarios++;
                    }
                    finally { combo.IsDropDownOpen = false; Settle(window); }
                }
                Check(Convert.ToString(text.SelectedItem) == original && ((RawLabel)model.SelectedItem).Name == original && Convert.ToString(((ComboBoxItem)item.SelectedItem).Content) == original, "raw selector labels changed during layout");
            }
            finally { window.Close(); }
        }
        private static IslandNotification[] Messages()
        {
            return Enumerable.Range(0, 20).Select(i => new IslandNotification { Id = "surface-" + i, AppId = i == 19 ? "" : "fixture." + i,
                AppName = "Fixture " + i.ToString("00") + " 中文应用 / Long original application label", Title = "PRIVATE_TITLE_" + i + " " + new string('t', 150),
                Body = "PRIVATE_BODY_" + i + " 中文配置\n" + new string('b', 360), CreatedAt = DateTime.UtcNow.AddSeconds(-i), IsDemo = true }).ToArray();
        }
        private static void NotificationStates(IslandWindow window, IslandNotification[] messages, int width, int height, string context)
        {
            var settings = Read<Settings>(window, "settings"); settings.ExpandedWidth = width; settings.ExpandedHeight = height; window.SetExpanded(true, false);
            var page = Read<StackPanel>(window, "notificationPage"); var feed = Read<NotificationFeed>(window, "notificationFeed");
            foreach (string state in new[] { "empty", "filled", "private", "loading", "failure", "retry", "connected" })
            {
                settings.HideNotificationBody = state == "private"; feed.Clear();
                var fresh = messages.Select((message, index) => new IslandNotification { Id = message.Id + "-" + scenarios, AppId = message.AppId, AppName = message.AppName,
                    Title = message.Title, Body = message.Body, CreatedAt = DateTime.UtcNow.AddSeconds(-index), IsDemo = true }).ToArray();
                if (state != "empty") feed.Merge(fresh, false, false);
                Call(window, "UpdateNotificationList");
                if (state == "loading") Call(window, "ApplyNotificationReadState", false, true, null);
                if (state == "failure") Call(window, "ApplyNotificationReadState", true, false, "Fixture timeout / 请重试");
                if (state == "retry") Call(window, "ApplyNotificationReadState", true, true, "Fixture timeout / 请重试");
                if (state == "connected") Call(window, "ApplyNotificationReadState", true, false, null);
                Settle(window); CheckSurface(page, context + "/notification/" + state); CheckHeader(window, context); scenarios++;
                var filters = Read<Dictionary<NotificationFilter, Button>>(window, "filterButtons");
                Check(Convert.ToString(filters[NotificationFilter.WeChat].Content) == (UiLocalization.IsEnglish ? "WeChat" : "微信"), context + ": static app filter did not follow language");
                foreach (var tab in new[] { Read<Button>(window, "notificationsTab"), Read<Button>(window, "statusTab"), Read<Button>(window, "toolsTab") })
                    foreach (var label in Descendants<TextBlock>(tab).Where(t => t.IsVisible)) Check(label.ActualHeight <= 20, context + ": page-tab caption unexpectedly wraps " + label.Text + " (width=" + label.ActualWidth + ",font=" + label.FontSize + ")");
                if (state == "private")
                    foreach (var label in Descendants<TextBlock>(page).Where(t => t.IsVisible))
                        Check(!label.Text.Contains("PRIVATE_TITLE_") && !label.Text.Contains("PRIVATE_BODY_") && !Convert.ToString(label.ToolTip).Contains("PRIVATE_"), context + ": private data leaked through text or tooltip");
                Check(feed.Items.Count == (state == "empty" ? 0 : messages.Length), context + ": layout changed retained notification data");
            }
            settings.HideNotificationBody = false;
            var list = Read<ListBox>(window, "notificationList"); var viewer = Descendants<ScrollViewer>(list).FirstOrDefault();
            if (list.Items.Count > 0 && viewer != null) { list.ScrollIntoView(list.Items[list.Items.Count - 1]); viewer.ScrollToEnd(); Settle(window); Check(viewer.VerticalOffset > 0, context + ": message history cannot scroll"); }
            Call(window, "UpdateNotificationConnection");
        }
        private static void CheckDialog(Window window, string context)
        {
            Scroll(window, false); CheckSurface((FrameworkElement)window.Content, context); Scroll(window, true); CheckSurface((FrameworkElement)window.Content, context + "/bottom");
            var root = (Grid)window.Content; var footer = root.Children.OfType<FrameworkElement>().First(c => Grid.GetRow(c) == root.RowDefinitions.Count - 1);
            Rect contentBounds = new Rect(new Point(), ((FrameworkElement)window.Content).RenderSize);
            Point origin = footer.TranslatePoint(new Point(), (FrameworkElement)window.Content);
            Check(origin.Y >= -1 && origin.Y + footer.ActualHeight <= contentBounds.Height + 1, context + ": fixed footer falls outside window");
            var scroll = root.Children.OfType<ScrollViewer>().First(); Check(scroll.ViewportHeight > 90, context + ": scroll viewport collapsed");
            Check(Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) <= 1, context + ": last form fields cannot be reached"); Scroll(window, false);
        }
        private static void CheckHeader(IslandWindow window, string context)
        {
            var header = Read<Grid>(window, "expandedHeader"); CheckSurface(header, context + "/header");
            var actions = Descendants<Button>(header).ToArray(); Check(actions.Length == 4, context + ": header lost an action");
            foreach (var action in actions) Check(Math.Abs(action.ActualWidth - 32) < 1 && Math.Abs(action.ActualHeight - 32) < 1, context + ": header icon sizes differ");
        }
        private static void CheckSurface(FrameworkElement root, string context)
        {
            foreach (var control in Descendants<Control>(root).Where(c => c.IsVisible && (c is Button || c is TextBox || c is CheckBox || c is ComboBox)))
            {
                Point origin = control.TranslatePoint(new Point(), root);
                Check(origin.X >= -1 && origin.X + control.ActualWidth <= root.ActualWidth + 1, context + ": horizontal control overflow " + control.GetType().Name);
                var box = control as TextBox;
                if (box != null && box.Text.Length > 0 && box.VerticalOffset < .1) { Rect glyph = box.GetRectFromCharacterIndex(0); Check(glyph.IsEmpty || glyph.Top >= -1 && glyph.Bottom <= box.ActualHeight + 1 && glyph.Height >= box.FontSize, context + ": cropped input glyph"); }
            }
            foreach (var text in Descendants<TextBlock>(root).Where(t => t.IsVisible && !String.IsNullOrEmpty(t.Text) && t.TextTrimming == TextTrimming.None))
            {
                captions++; if (text.ActualWidth <= 0) { Check(false, context + ": zero-width caption " + text.Text); continue; }
                if (text.TextWrapping == TextWrapping.NoWrap)
                {
                    var formatted = new FormattedText(text.Text, UiLocalization.Culture, FlowDirection.LeftToRight, new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(text).PixelsPerDip);
                    Check(formatted.WidthIncludingTrailingWhitespace <= text.ActualWidth + 1.5, context + ": cropped single-line caption " + text.Text);
                }
                Check(text.DesiredSize.Width - text.Margin.Left - text.Margin.Right <= text.ActualWidth + 1 && text.DesiredSize.Height - text.Margin.Top - text.Margin.Bottom <= text.ActualHeight + 1, context + ": cropped wrapped caption " + text.Text);
            }
            foreach (var grid in Descendants<Grid>(root).Where(g => g.IsVisible && g.GetType().Name == "ActionGrid"))
            {
                var buttons = grid.Children.OfType<Button>().Where(b => b.IsVisible).ToArray();
                foreach (var button in buttons) Check(Math.Abs(button.ActualWidth - buttons[0].ActualWidth) <= 1 && Math.Abs(button.ActualHeight - buttons[0].ActualHeight) <= 1, context + ": unequal action group");
                foreach (var row in buttons.GroupBy(Grid.GetRow))
                {
                    var ordered = row.OrderBy(Grid.GetColumn).ToArray();
                    for (int i = 1; i < ordered.Length; i++) { Point a = ordered[i - 1].TranslatePoint(new Point(), grid), b = ordered[i].TranslatePoint(new Point(), grid); Check(Math.Abs(b.X - a.X - ordered[i - 1].ActualWidth - 6) <= 1 && Math.Abs(a.Y - b.Y) <= 1, context + ": action gap/alignment differs"); }
                }
            }
        }
        private static void CheckDropdown(ComboBox combo, string context)
        {
            DropdownRequire(combo != null, combo, context, "combo is null");
            var window = Window.GetWindow(combo);
            DropdownDiagnostic(combo, context, "enter");
            DropdownRequire(window != null, combo, context, "combo has no owning window");
            DropdownRequire(window.IsVisible && window.WindowState != WindowState.Minimized, combo, context, "owning window is hidden or minimized");
            var ancestors = DropdownAncestors(combo).ToArray();
            var expanders = ancestors.OfType<Expander>().Reverse().Select(value => new KeyValuePair<Expander, bool>(value, value.IsExpanded)).ToArray();
            var scrolls = ancestors.OfType<ScrollViewer>().Select(value => new KeyValuePair<ScrollViewer, Point>(value, new Point(value.HorizontalOffset, value.VerticalOffset))).ToArray();
            try
            {
                // Inspect the control in the same visible state a user must
                // reach. Hidden/off-viewport controls may not have a template
                // yet; ApplyTemplate alone does not establish that precondition.
                foreach (var expander in expanders) { expander.Key.ApplyTemplate(); expander.Key.IsExpanded = true; }
                combo.BringIntoView();
                WaitDropdownLayout(combo, window, context, true);
                DropdownRequire(combo.IsVisible && combo.IsLoaded, combo, context, "combo is not loaded and visible after scrolling");
                DropdownRequire(combo.ActualWidth > 0 && combo.ActualHeight > 0 && combo.IsMeasureValid && combo.IsArrangeValid, combo, context, "combo has no stable nonzero layout");
                combo.ApplyTemplate();
                DropdownRequire(combo.Template != null, combo, context, "combo.Template is null after visible stable layout and ApplyTemplate");
                object part = combo.Template.FindName("PART_Popup", combo);
                DropdownRequire(part != null, combo, context, "PART_Popup is null in the applied combo template");
                var popup = part as Popup;
                DropdownRequire(popup != null, combo, context, "PART_Popup has unexpected type " + part.GetType().FullName);
                combo.IsDropDownOpen = true;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                FrameworkElement child;
                do
                {
                    DropdownPump(window); child = popup.Child as FrameworkElement;
                    if (popup.IsOpen && child != null && child.IsVisible && child.ActualWidth > 0 && child.ActualHeight > 0 && child.IsMeasureValid && child.IsArrangeValid) break;
                } while (clock.ElapsedMilliseconds < 1600);
                DropdownDiagnostic(combo, context, "opened");
                DropdownRequire(combo.IsDropDownOpen && popup.IsOpen, combo, context, "combo requested open but PART_Popup did not open");
                DropdownRequire(popup.Child != null, combo, context, "PART_Popup.Child is null after opening");
                DropdownRequire(child != null, combo, context, "PART_Popup.Child is not a FrameworkElement");
                DropdownRequire(child.IsVisible && child.ActualWidth > 0 && child.ActualHeight > 0 && child.IsMeasureValid && child.IsArrangeValid, combo, context, "popup child is hidden, empty or not settled");
                Check(Math.Abs(child.ActualWidth - combo.ActualWidth) <= 1, context + ": dropdown does not follow field width"); CheckSurface(child, context + "/popup");
            }
            finally
            {
                combo.IsDropDownOpen = false;
                foreach (var expander in expanders) expander.Key.IsExpanded = expander.Value;
                WaitDropdownLayout(combo, window, context + "/restore", false);
                foreach (var scroll in scrolls) { scroll.Key.ScrollToHorizontalOffset(scroll.Value.X); scroll.Key.ScrollToVerticalOffset(scroll.Value.Y); }
                Settle(window);
            }
        }
        private static IEnumerable<DependencyObject> DropdownAncestors(DependencyObject value)
        {
            var visited = new HashSet<DependencyObject>();
            for (DependencyObject current = value; current != null && visited.Add(current); )
            {
                yield return current;
                DependencyObject parent = current is Visual ? VisualTreeHelper.GetParent(current) : null;
                if (parent == null) parent = LogicalTreeHelper.GetParent(current);
                var element = current as FrameworkElement;
                if (parent == null && element != null) parent = element.Parent ?? element.TemplatedParent;
                current = parent;
            }
        }
        private static void DropdownPump(Window window)
        {
            window.UpdateLayout();
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (s, e) => frame.Continue = false; timer.Start();
            try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
            var island = window as IslandWindow; if (island != null) Stop(island);
            window.UpdateLayout();
        }
        private static FrameworkElement DropdownViewport(ScrollViewer scroll)
        {
            scroll.ApplyTemplate();
            return Descendants<ScrollContentPresenter>(scroll).FirstOrDefault(value => Object.ReferenceEquals(value.TemplatedParent, scroll));
        }
        private static bool DropdownInView(ComboBox combo)
        {
            foreach (var scroll in DropdownAncestors(combo).OfType<ScrollViewer>())
            {
                var viewport = DropdownViewport(scroll); if (viewport == null) return false;
                Point origin = combo.TranslatePoint(new Point(), viewport);
                if (origin.X < -1 || origin.Y < -1 || origin.X + combo.ActualWidth > viewport.ActualWidth + 1 || origin.Y + combo.ActualHeight > viewport.ActualHeight + 1) return false;
            }
            return true;
        }
        private static void WaitDropdownLayout(ComboBox combo, Window window, string context, bool scrollIntoView)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew(); int stable = 0;
            Point previousOrigin = new Point(Double.NaN, Double.NaN); Size previousSize = new Size();
            while (clock.ElapsedMilliseconds < 1600)
            {
                DropdownRequire(window.IsVisible && window.WindowState != WindowState.Minimized, combo, context, "owning window became hidden or minimized during preparation");
                if (scrollIntoView) combo.BringIntoView();
                DropdownPump(window);
                bool moving = Descendants<SmoothToolPicker>(window).Any(value => value.IsMotionActive);
                Point origin = combo.TranslatePoint(new Point(), window); var size = new Size(combo.ActualWidth, combo.ActualHeight);
                bool ready = !moving && window.IsMeasureValid && window.IsArrangeValid && (!scrollIntoView || (combo.IsMeasureValid && combo.IsArrangeValid && combo.IsVisible && combo.IsLoaded && size.Width > 0 && size.Height > 0 && DropdownInView(combo)));
                if (ready && Math.Abs(origin.X - previousOrigin.X) < .1 && Math.Abs(origin.Y - previousOrigin.Y) < .1 && Math.Abs(size.Width - previousSize.Width) < .1 && Math.Abs(size.Height - previousSize.Height) < .1) stable++; else stable = 0;
                if (stable >= 2) return;
                previousOrigin = origin; previousSize = size;
            }
            DropdownRequire(false, combo, context, "visible viewport/layout/section motion did not stabilize within 1600 ms; scrollIntoView=" + scrollIntoView);
        }
        private static void DropdownRequire(bool condition, ComboBox combo, string context, string message)
        {
            if (condition) return;
            DropdownDiagnostic(combo, context, "failure: " + message);
            throw new InvalidOperationException(context + ": dropdown precondition: " + message);
        }
        private static void DropdownDiagnostic(ComboBox combo, string context, string phase)
        {
            var window = combo == null ? null : Window.GetWindow(combo);
            Console.WriteLine("DROPDOWN " + context + "; Phase=" + phase + "; Combo=" + (combo == null ? "null" : combo.GetType().Name) +
                "; IsVisible=" + (combo != null && combo.IsVisible) + "; Window=" + (window == null ? "null" : window.GetType().Name + "/" + window.WindowState + "/Visible=" + window.IsVisible) +
                "; Size=" + (combo == null ? "null" : combo.ActualWidth + "x" + combo.ActualHeight) + "; Template=" + (combo == null || combo.Template == null ? "null" : combo.Template.GetType().Name));
            if (combo != null)
            {
                int depth = 0;
                foreach (var ancestor in DropdownAncestors(combo).OfType<FrameworkElement>())
                {
                    var control = ancestor as Control; var expander = ancestor as Expander; var motion = ancestor as SmoothToolPicker; var scroll = ancestor as ScrollViewer;
                    var clip = LayoutInformation.GetLayoutClip(ancestor);
                    Console.WriteLine("DROPDOWN ANCESTOR " + context + "/" + depth++ + "; Type=" + ancestor.GetType().Name + "; Name=" + ancestor.Name +
                        "; IsVisible=" + ancestor.IsVisible + "; Visibility=" + ancestor.Visibility + "; Loaded=" + ancestor.IsLoaded + "; Size=" + ancestor.ActualWidth + "x" + ancestor.ActualHeight +
                        "; Desired=" + ancestor.DesiredSize + "; Slot=" + LayoutInformation.GetLayoutSlot(ancestor) + "; Clip=" + (clip == null ? "none" : clip.Bounds.ToString()) +
                        "; Template=" + (control == null ? "n/a" : control.Template == null ? "null" : control.Template.GetType().Name) +
                        "; Expanded=" + (expander == null ? "n/a" : expander.IsExpanded.ToString()) + "; Motion=" + (motion == null ? "n/a" : motion.IsMotionActive.ToString()) +
                        "; MeasureValid=" + ancestor.IsMeasureValid + "; ArrangeValid=" + ancestor.IsArrangeValid +
                        "; Scroll=" + (scroll == null ? "n/a" : scroll.VerticalOffset + "/" + scroll.ScrollableHeight + "; Viewport=" + scroll.ViewportWidth + "x" + scroll.ViewportHeight));
                }
                if (combo.Template != null)
                {
                    try
                    {
                        var popup = combo.Template.FindName("PART_Popup", combo) as Popup;
                        var child = popup == null ? null : popup.Child as FrameworkElement;
                        Console.WriteLine("DROPDOWN PART " + context + "; IsDropDownOpen=" + combo.IsDropDownOpen + "; Popup=" + (popup == null ? "null" : "IsOpen=" + popup.IsOpen) +
                            "; Child=" + (popup == null || popup.Child == null ? "null" : popup.Child.GetType().Name) + "; ChildVisible=" + (child != null && child.IsVisible) + "; ChildSize=" + (child == null ? "n/a" : child.ActualWidth + "x" + child.ActualHeight));
                    }
                    catch (Exception error) { Console.WriteLine("DROPDOWN PART " + context + "; TemplatePartLookup=" + error.GetType().Name + ": " + error.Message); }
                }
            }
            Console.Out.Flush();
        }
        private static void Scroll(Window window, bool bottom) { var root = (Grid)window.Content; var scroll = root.Children.OfType<ScrollViewer>().First(); if (bottom) scroll.ScrollToBottom(); else scroll.ScrollToTop(); Settle(window); }
        private static void SaveWindow(Window window, string directory, string name)
        {
            var bitmap = Snapshot(window);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using (var file = File.Create(Path.Combine(directory, name + ".png"))) png.Save(file);
        }
        private static RenderTargetBitmap Snapshot(Window window)
        {
            Settle(window); window.InvalidateVisual();
            var frame = new DispatcherFrame(); EventHandler rendered = null;
            var deadline = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(700) };
            rendered = (s,e) => { CompositionTarget.Rendering -= rendered; window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(() => frame.Continue = false)); };
            deadline.Tick += (s,e) => frame.Continue = false;
            CompositionTarget.Rendering += rendered; deadline.Start();
            try { Dispatcher.PushFrame(frame); } finally { deadline.Stop(); CompositionTarget.Rendering -= rendered; }
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.RenderSize.Width), (int)Math.Ceiling(window.RenderSize.Height),96,96,PixelFormats.Pbgra32);
            bitmap.Render(window); return bitmap;
        }
        private static IEnumerable<T> Descendants<T>(DependencyObject value) where T : DependencyObject { for (int i = 0; i < VisualTreeHelper.GetChildrenCount(value); i++) { var child = VisualTreeHelper.GetChild(value, i); var found = child as T; if (found != null) yield return found; foreach (var item in Descendants<T>(child)) yield return item; } }
        private static T Read<T>(object value, string field) { return (T)value.GetType().GetField(field, Private).GetValue(value); }
        private static object Call(object value, string method, params object[] args) { try { return value.GetType().GetMethod(method, Private).Invoke(value, args); } catch (TargetInvocationException e) { throw e.InnerException ?? e; } }
        private static void Stop(IslandWindow window) { foreach (var field in typeof(IslandWindow).GetFields(Private)) if (field.FieldType == typeof(DispatcherTimer)) ((DispatcherTimer)field.GetValue(window)).Stop(); }
        private static void Settle(Window window) { window.UpdateLayout(); var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); var island = window as IslandWindow; if (island != null) Stop(island); window.UpdateLayout(); }
        private static void Check(bool condition, string message) { if (!condition && Issues.Count < 500) Issues.Add(message); }
    }
}
