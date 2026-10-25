using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexIsland
{
    // Uses isolated settings and this test's own demo HWND. Never generates
    // global keyboard/mouse input, launches account readers, or touches clipboard.
    internal static class ToolNavigationTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        [DllImport("user32.dll")] private static extern IntPtr SetActiveWindow(IntPtr window);
        public static void Run()
        {
            string previous = Environment.GetEnvironmentVariable("CODEX_ISLAND_DATA_DIR");
            string directory = Path.Combine(Path.GetTempPath(), "CodexIsland.ToolNavigationTests." + Guid.NewGuid().ToString("N"));
            IslandWindow window = null;
            try
            {
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", directory);
                CatalogAndPersistence();
                var store = new SettingsStore();
                store.Save(new Settings { LastTool = "hash", FavoriteTools = "focus,time,random", AlwaysOnTop = false, AutoCollapse = false,
                    NotificationsEnabled = false, CustomExpandedSize = true, ExpandedWidth = 440, ExpandedHeight = 680 });
                window = new IslandWindow(true) { ShowActivated = false };
                window.Loaded += (s, e) => StopTimers(window);
                Check(Read<string>(window, "selectedTool") == "hash", "new window reads the saved last module before display");
                window.Show(); Pump(1); StopTimers(window);
                Check(Read<string>(window, "selectedTool") == "hash", "loaded startup does not reset the saved last module");
                Call(window, "SelectToolsPage"); window.SetExpanded(true, false); window.UpdateLayout();
                MatchInstalledCatalog(window); Favorites(window); FavoritePopupClosure(window); HiddenModules(window); Search(window); NavigationGeometry(window);
                var settings = Read<Settings>(window, "settings");
                settings.HiddenTools = ""; Call(window, "ConfigureToolkit"); Click(Read<Dictionary<string, Button>>(window, "toolButtons")["calculator"]);
                Check(settings.LastTool == "calculator", "normal tool clicks update LastTool");
                // Demo intentionally suppresses SaveSettings. Test the real store
                // explicitly, then instantiate a second demo with that saved state.
                store.Save(settings); window.Close(); window = null;
                window = new IslandWindow(true);
                Check(Read<string>(window, "selectedTool") == "calculator", "last module survives persisted settings and window reconstruction");
                Check(Read<Settings>(window, "settings").FavoriteTools == settings.FavoriteTools, "favorite order survives persisted settings and window reconstruction");
                Console.WriteLine("PASS: 38-module catalog, favorite normalization/cap/persistence, last-tool restoration, hidden-tool recovery, keyboard search/focus and compact aligned two-action picker across widths, languages, themes and search states.");
            }
            finally
            {
                if (window != null) { StopTimers(window); window.Close(); }
                Keyboard.ClearFocus(); SetActiveWindow(IntPtr.Zero);
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", previous);
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }
        private static void CatalogAndPersistence()
        {
            string[] expected = { "media", "focus", "notes", "reminders", "time", "launch", "files", "hash", "clipboard", "text", "calculator", "converter", "random", "colors", "system", "ports", "screenpin", "image", "diff", "watch", "awake", "qr", "scenes", "clipboardhistory", "logwatch", "desktop", "profiles", "diagnostics", "ocr", "templates", "ready", "backup", "autorules", "widgets", "audio-mixer", "lan-transfer", "batch", "activities" };
            Check(ToolCatalog.All.Length == 38 && ToolCatalog.All.Select(item => item.Key).SequenceEqual(expected), "catalog defines every expected module in a stable order");
            Check(ToolCatalog.All.All(item => !String.IsNullOrWhiteSpace(item.Label) && !String.IsNullOrWhiteSpace(item.SettingsLabel)), "all catalog entries have picker and settings labels");
            Check(ToolCatalog.All.Select(item => item.Key).Distinct().Count() == 38, "all thirty-eight expected catalog keys are unique");
            Check(ToolCatalog.NormalizeFavorites(null) == "" && ToolCatalog.NormalizeFavorites(" ,invalid, ") == "", "empty and unknown favorites are discarded");
            Check(ToolCatalog.NormalizeFavorites(" time ,focus,time,missing, notes ,,focus ") == "time,focus,notes", "normalization trims, deduplicates and preserves first-occurrence order");
            Check(ToolCatalog.MaximumFavorites == 6 && ToolCatalog.NormalizeFavorites(String.Join(",", expected)) == "media,focus,notes,reminders,time,launch", "normalization enforces the six-favorite cap");
            Check(!ToolCatalog.Contains(null) && !ToolCatalog.Contains("missing") && ToolCatalog.Contains("time"), "catalog membership rejects missing keys");
            var store = new SettingsStore();
            store.Save(new Settings { LastTool = "time", FavoriteTools = "time, time ,bogus,focus,notes,hash,random,colors,system" });
            var loaded = store.Load();
            Check(loaded.LastTool == "time" && loaded.FavoriteTools == "time,focus,notes,hash,random,colors", "settings normalize and persist the selected tool and favorite order");
            store.Save(new Settings { LastTool = "unknown", FavoriteTools = "" }); loaded = store.Load();
            Check(loaded.LastTool == "focus" && loaded.FavoriteTools == "", "invalid last tool falls back while an explicitly empty favorites list stays empty");
            File.WriteAllText(store.FilePath, "{\"Side\":\"left\"}"); loaded = store.Load();
            Check(loaded.LastTool == "focus" && loaded.FavoriteTools == "focus,notes,reminders", "legacy settings gain compatible default navigation preferences");
        }
        private static void MatchInstalledCatalog(IslandWindow window)
        {
            var panels = Read<Dictionary<string, UIElement>>(window, "toolPanels"); var buttons = Read<Dictionary<string, Button>>(window, "toolButtons");
            var expected = ToolCatalog.All.Select(item => item.Key).OrderBy(key => key).ToArray();
            Check(panels.Keys.OrderBy(key => key).SequenceEqual(expected) && buttons.Keys.OrderBy(key => key).SequenceEqual(expected), "actual panels and picker buttons exactly match all 38 catalog definitions");
            foreach (var item in ToolCatalog.All) Check((string)buttons[item.Key].Content == item.Label, "picker uses catalog label for " + item.Key);
            Check(panels["hash"].Visibility == Visibility.Visible && panels.Count(item => item.Value.Visibility == Visibility.Visible) == 1, "restored module is the only selected panel");
        }
        private static void Favorites(IslandWindow window)
        {
            var settings = Read<Settings>(window, "settings"); var favorites = Read<WrapPanel>(window, "favoriteTools");
            var current = Read<Button>(window, "favoriteCurrent");
            Check(favorites.Children.Count == 3 && favorites.Visibility == Visibility.Visible, "saved favorites populate the quick access strip");
            Click(current); Check(settings.FavoriteTools == "focus,time,random,hash" && favorites.Children.Count == 4, "current-tool favorite button adds once at the end");
            Click(current); Check(settings.FavoriteTools == "focus,time,random" && favorites.Children.Count == 3, "current-tool favorite button toggles removal");
            Click(favorites.Children.OfType<Button>().Single(button => ((string)button.Content).Contains("日期与时区")));
            Check(Read<string>(window, "selectedTool") == "time" && settings.LastTool == "time", "favorite button selects the real module and remembers it");
            Check(Read<Dictionary<string, UIElement>>(window, "toolPanels")["time"].Visibility == Visibility.Visible && !Read<Expander>(window, "toolPicker").IsExpanded, "favorite selection reveals the panel and closes the picker");
            var timeButton = favorites.Children.OfType<Button>().Single(button => ((string)button.Content).Contains("日期与时区"));
            ((MenuItem)timeButton.ContextMenu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Check(settings.FavoriteTools == "focus,random", "favorite context action removes its own key");
            settings.FavoriteTools = "focus,notes,reminders,time,hash,random"; Call(window, "ConfigureToolkit");
            Call(window, "SelectTool", "colors"); Click(current);
            Check(settings.FavoriteTools == "focus,notes,reminders,time,hash,random" && favorites.Children.Count == 6, "seventh favorite leaves the current six unchanged");
            Check(Read<TextBlock>(window, "toolkitHint").Text.Contains("6"), "favorite limit is explained to the user");
            var first = favorites.Children.OfType<Button>().First(); ((MenuItem)first.ContextMenu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Click(current);
            Check(settings.FavoriteTools == "notes,reminders,time,hash,random,colors" && favorites.Children.Count == 6, "removing a favorite frees a slot for the next one");
        }
        private static void HiddenModules(IslandWindow window)
        {
            var settings = Read<Settings>(window, "settings"); var favorites = Read<WrapPanel>(window, "favoriteTools"); var panels = Read<Dictionary<string, UIElement>>(window, "toolPanels");
            settings.FavoriteTools = "focus,time,random"; settings.HiddenTools = "time"; Call(window, "ConfigureToolkit");
            Check(favorites.Children.Count == 2 && !favorites.Children.OfType<Button>().Any(button => ((string)button.Content).Contains("日期与时区")), "hidden favorites disappear from quick access");
            Check(settings.FavoriteTools == "focus,time,random", "hiding a favorite preserves its preference for later restoration");
            settings.HiddenTools = String.Join(",", ToolCatalog.All.Select(item => item.Key)); Call(window, "ConfigureToolkit");
            Check(Read<string>(window, "selectedTool") == "" && panels.All(item => item.Value.Visibility == Visibility.Collapsed), "hiding every module leaves no invisible selected panel");
            Check(Read<TextBlock>(window, "emptyTools").Visibility == Visibility.Visible && favorites.Visibility == Visibility.Collapsed, "all-hidden state explains how to restore modules");
            Check(Read<Expander>(window, "toolPicker").IsExpanded, "all-hidden state opens the picker containing the settings recovery action");
            Check(!Read<Button>(window, "favoriteCurrent").IsEnabled && Read<Dictionary<string, Button>>(window, "toolButtons").All(item => item.Value.Visibility == Visibility.Collapsed), "all-hidden state disables pinning and picker entries");
            settings.HiddenTools = String.Join(",", ToolCatalog.All.Where(item => item.Key != "focus").Select(item => item.Key)); Call(window, "ConfigureToolkit");
            Check(Read<string>(window, "selectedTool") == "focus" && panels["focus"].Visibility == Visibility.Visible && Read<TextBlock>(window, "emptyTools").Visibility == Visibility.Collapsed, "re-enabling one module restores selection and dismisses the empty state");
            Check(favorites.Children.Count == 1 && Read<Button>(window, "favoriteCurrent").IsEnabled, "re-enabled favorite becomes usable again");
            settings.HiddenTools = ""; Call(window, "ConfigureToolkit"); Check(favorites.Children.Count == 3, "unhiding restores all saved favorites");
        }
        private static void FavoritePopupClosure(IslandWindow window)
        {
            var settings = Read<Settings>(window, "settings"); settings.HiddenTools = ""; settings.FavoriteTools = "focus,time"; Call(window, "ConfigureToolkit");
            Call(window, "SelectToolsPage"); window.SetExpanded(true, false); window.UpdateLayout();
            var favorite = Read<WrapPanel>(window, "favoriteTools").Children.OfType<Button>().First(); var menu = favorite.ContextMenu;
            bool closed = false; menu.Closed += (s, e) => closed = true;
            // IsOpen=true alone bypasses ContextMenuService's routed opening and
            // closing events. Exercise the same service path as a real right click,
            // targeted exclusively at this test's own demo button, without global input.
            var popupType = typeof(ContextMenu).Assembly.GetType("System.Windows.Controls.PopupControlService");
            const BindingFlags popupFlags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            var opening = popupType.GetMethod("RaiseContextMenuOpeningEvent", popupFlags, null, new[] { typeof(IInputElement), typeof(double), typeof(double), typeof(bool) }, null);
            object service = popupType.GetMethod("get_Current", popupFlags).Invoke(null, null);
            try
            {
                opening.Invoke(service, new object[] { favorite, -1.0, -1.0, true }); Pump(20); StopTimers(window);
                Check(menu.IsOpen && (bool)Call(window, "HasPopupInteraction"), "real favorite menu opening protects its owning island");
                var item = (MenuItem)menu.Items[0]; typeof(MenuItem).GetMethod("OnClick", Private).Invoke(item, null); Pump(40); StopTimers(window);
                Check(!menu.IsOpen && favorite.Parent == null && settings.FavoriteTools == "time", "real remove action closes menu and rebuilds favorite strip");
                // IsOpen turns false before the closing fade completes. Wait for
                // Closed itself so the test respects Windows menu animation settings.
                var wait = Stopwatch.StartNew(); while (!closed && wait.ElapsedMilliseconds < 1000) Pump(10); StopTimers(window);
                Check(closed, "detached favorite menu finishes its native closing animation");
                Check(!(bool)Call(window, "HasPopupInteraction"), "removing menu placement target does not leave a permanent popup guard");
            }
            finally { menu.IsOpen = false; Pump(10); StopTimers(window); Keyboard.ClearFocus(); }
        }
        private static void Search(IslandWindow window)
        {
            var search = Read<TextBox>(window, "toolSearch"); var picker = Read<Expander>(window, "toolPicker"); var buttons = Read<Dictionary<string, Button>>(window, "toolButtons");
            var searchFrame = search.Parent as Grid;
            Check(searchFrame != null, "compact search has a shared frame for input and noninteractive hints");
            var placeholder = searchFrame.Children.OfType<TextBlock>().Single(item => item.Text == UiLocalization.Localize("搜索工具名称"));
            var shortcut = searchFrame.Children.OfType<TextBlock>().Single(item => item.Text == "Ctrl + K");
            Check(search.Text == "" && placeholder.Visibility == Visibility.Visible && !placeholder.IsHitTestVisible && !shortcut.IsHitTestVisible, "empty search displays placeholder and shortcut without storing them as input or intercepting clicks");
            search.Text = "User config 中文 = literal";
            Check(placeholder.Visibility == Visibility.Collapsed, "typing removes the search placeholder");
            string language = UiLocalization.Language, theme = UiTheme.Name;
            try
            {
                AppAppearance.Configure("en", "light");
                Check(search.Text == "User config 中文 = literal" && placeholder.Visibility == Visibility.Collapsed, "language and theme changes preserve the exact search input");
                search.Text = "";
                Check(placeholder.Visibility == Visibility.Visible && placeholder.Text == "Search tools", "empty search restores the translated placeholder");
            }
            finally { AppAppearance.Configure(language, theme); search.Text = ""; }
            search.Text = "RaNdOm"; picker.IsExpanded = true;
            Check(buttons.Where(item => item.Value.Visibility == Visibility.Visible).Select(item => item.Key).SequenceEqual(new[] { "random" }), "search matches English keys without case sensitivity");
            SendEnter(window, search); Check(Read<string>(window, "selectedTool") == "random" && !picker.IsExpanded, "Enter opens the filtered module and closes picker");
            search.Text = "日期"; picker.IsExpanded = true; SendEnter(window, search);
            Check(Read<string>(window, "selectedTool") == "time", "Chinese search and Enter open the matching module");
            search.Text = "no-such-tool-7402"; picker.IsExpanded = true;
            Check(Read<TextBlock>(window, "noToolMatches").Visibility == Visibility.Visible && buttons.All(item => item.Value.Visibility == Visibility.Collapsed), "no-match query explains why the results list is empty");
            SendEnter(window, search); Check(Read<string>(window, "selectedTool") == "time" && picker.IsExpanded, "Enter with no matches preserves current selection");
            search.Text = "日期"; window.SetExpanded(false, false); window.UpdateLayout();
            var source = PresentationSource.FromVisual(window) as HwndSource;
            Check(source != null && !source.IsDisposed, "test window has its own valid input source");
            SetActiveWindow(source.Handle);
            Call(window, "OpenToolSearch"); Pump(IslandWindow.TransitionMilliseconds + 20); window.UpdateLayout(); StopTimers(window);
            Check(Read<bool>(window, "expanded") && Read<bool>(window, "toolsPageSelected") && picker.IsExpanded, "OpenToolSearch expands the island, selects toolkit and opens picker");
            Check(ReferenceEquals(Keyboard.FocusedElement, search) && search.IsKeyboardFocused, "OpenToolSearch focuses the actual search input");
            Check(search.SelectedText == "日期", "OpenToolSearch selects the current query for replacement");
            const string qrPayload = "https://example.com/path?text=中文";
            search.Text = "生成二维码 " + qrPayload; picker.IsExpanded = true;
            Check(Read<TextBlock>(window, "toolCommandHint").Visibility == Visibility.Visible && Read<TextBlock>(window, "noToolMatches").Visibility == Visibility.Collapsed, "compact search still offers a valid command instead of an empty-results warning");
            SendEnter(window, search);
            var qr = Read<QrTransferPanel>(window, "qrTransferPanel");
            var qrInput = (TextBox)typeof(QrTransferPanel).GetField("input", Private).GetValue(qr);
            Check(Read<string>(window, "selectedTool") == "qr" && qrInput.Text == qrPayload && search.Text == "" && placeholder.Visibility == Visibility.Visible, "Enter executes the real QR command, preserves its payload and restores the empty placeholder");
            var settings = Read<Settings>(window, "settings"); settings.HiddenTools = "time"; Call(window, "ConfigureToolkit");
            search.Text = "日期与时区"; Check(buttons["time"].Visibility == Visibility.Collapsed && Read<TextBlock>(window, "noToolMatches").Visibility == Visibility.Visible, "search never resurrects a hidden module");
            search.Text = ""; settings.HiddenTools = ""; Call(window, "ConfigureToolkit");
            Check(buttons.All(item => item.Value.Visibility == Visibility.Visible) && Read<TextBlock>(window, "noToolMatches").Visibility == Visibility.Collapsed, "clearing search restores the full visible catalog");
        }
        private static void NavigationGeometry(IslandWindow window)
        {
            var settings = Read<Settings>(window, "settings");
            string language = UiLocalization.Language, theme = UiTheme.Name, favorites = settings.FavoriteTools, hidden = settings.HiddenTools;
            string selected = Read<string>(window, "selectedTool"); int width = settings.ExpandedWidth;
            var search = Read<TextBox>(window, "toolSearch"); var picker = Read<Expander>(window, "toolPicker");
            var buttons = Read<Dictionary<string, Button>>(window, "toolButtons"); var current = Read<Button>(window, "favoriteCurrent");
            var navigation = buttons.First().Value.Parent as UniformGrid; var actions = current.Parent as UniformGrid;
            Check(navigation != null && actions != null && !Object.ReferenceEquals(navigation, actions), "picker footer and tool tiles have independent layout regions");
            var footerButtons = actions.Children.OfType<Button>().ToArray();
            Check(actions.Columns == 2 && actions.Children.Count == 2 && footerButtons.Length == 2 && Object.ReferenceEquals(footerButtons[0], current), "picker footer has exactly two adjacent actions, with pinning first");
            var settingsButton = footerButtons[1];
            Check(Convert.ToString(settingsButton.Content) == UiLocalization.Localize("模块与外观设置"), "picker footer exposes the real tools and appearance action");
            var scroll = Ancestor<ScrollViewer>(navigation);
            Check(scroll != null && !Object.ReferenceEquals(scroll, Ancestor<ScrollViewer>(actions)), "pin action stays outside the scrolling tool list");
            try
            {
                settings.FavoriteTools = ""; settings.HiddenTools = ""; Call(window, "ConfigureToolkit"); Call(window, "SelectTool", "hash");
                Call(window, "SelectToolsPage"); window.SetExpanded(true, false);
                foreach (string testLanguage in new[] { "zh", "en" })
                foreach (string testTheme in new[] { "light", "dark" })
                foreach (int testWidth in new[] { 320, 388, 560, 900 })
                {
                    AppAppearance.Configure(testLanguage, testTheme); settings.ExpandedWidth = testWidth; window.SetExpanded(true, false); picker.IsExpanded = true;
                    search.Text = ""; Settle(window);
                    string label = testLanguage + "/" + testTheme + "/" + testWidth;
                    AssertSearchGeometry(window, search, label);
                    Check(scroll.ComputedVerticalScrollBarVisibility == Visibility.Visible, "full catalog has its real scrollbar at " + label);
                    AssertNavigationGeometry(window, navigation, actions, buttons, current, label + "/all");
                    double fullWidth = current.ActualWidth;
                    Click(current); Settle(window);
                    AssertNavigationGeometry(window, navigation, actions, buttons, current, label + "/unpin-caption");
                    AssertCaptionFits(current, label + "/unpin-caption"); Click(current); Settle(window);
                    settings.FavoriteTools = "focus,notes,reminders,time,random,colors"; Call(window, "ConfigureToolkit"); picker.IsExpanded = true; Settle(window);
                    Click(current); Settle(window);
                    Check(settings.FavoriteTools == "focus,notes,reminders,time,random,colors", "full favorite strip cannot silently add a seventh tool at " + label);
                    Check(Read<TextBlock>(window, "toolkitHint").Text.Contains("6"), "full favorite state explains its limit at " + label);
                    AssertNavigationGeometry(window, navigation, actions, buttons, current, label + "/full-favorites");
                    settings.FavoriteTools = ""; Call(window, "ConfigureToolkit"); picker.IsExpanded = true; Settle(window);
                    search.Text = "random"; Settle(window);
                    Check(scroll.ComputedVerticalScrollBarVisibility == Visibility.Visible && scroll.ScrollableHeight <= .75 && buttons.Count(item => item.Value.Visibility == Visibility.Visible) == 1, "single search result keeps the stable gutter without scroll extent at " + label);
                    AssertNavigationGeometry(window, navigation, actions, buttons, current, label + "/filtered");
                    Near(current.ActualWidth, fullWidth, "filtering retains the pin action width at " + label);
                    search.Text = "no-such-tool-7402"; Settle(window);
                    Check(buttons.All(item => item.Value.Visibility == Visibility.Collapsed), "empty query result has no stale tool tiles at " + label);
                    AssertNavigationGeometry(window, navigation, actions, buttons, current, label + "/empty");
                    search.Text = ""; settings.HiddenTools = String.Join(",", ToolCatalog.All.Where(item => item.Key != "hash").Select(item => item.Key));
                    Call(window, "ConfigureToolkit"); picker.IsExpanded = true; Settle(window);
                    Check(scroll.ComputedVerticalScrollBarVisibility == Visibility.Visible && scroll.ScrollableHeight <= .75, "hidden modules keep the stable gutter without scroll extent at " + label);
                    AssertNavigationGeometry(window, navigation, actions, buttons, current, label + "/hidden");
                    settings.HiddenTools = String.Join(",", ToolCatalog.All.Select(item => item.Key)); Call(window, "ConfigureToolkit"); picker.IsExpanded = true; Settle(window);
                    Check(!current.IsEnabled && settingsButton.IsEnabled, "all-hidden state disables pinning while retaining a working settings recovery action at " + label);
                    AssertNavigationGeometry(window, navigation, actions, buttons, current, label + "/all-hidden");
                    settings.HiddenTools = ""; Call(window, "ConfigureToolkit"); picker.IsExpanded = true; Settle(window);
                    AssertSearchLayoutSettles(window, navigation, actions, search);
                }
            }
            finally
            {
                search.Text = ""; settings.FavoriteTools = favorites; settings.HiddenTools = hidden; settings.ExpandedWidth = width;
                AppAppearance.Configure(language, theme); Call(window, "ConfigureToolkit"); Call(window, "SelectTool", selected); window.SetExpanded(true, false); Settle(window);
            }
        }
        private static void AssertNavigationGeometry(IslandWindow window, UniformGrid navigation, UniformGrid actions, Dictionary<string, Button> buttons, Button current, string label)
        {
            Check(navigation.Columns >= 2 && navigation.Columns <= 4 && actions.Columns == 2, "responsive tool catalog retains a filled two-action footer at " + label);
            Check(navigation.Columns == Math.Max(2, Math.Min(4, (int)(navigation.ActualWidth / 140))), "tool catalog responds to its measured viewport width at " + label);
            Near(navigation.ActualWidth, actions.ActualWidth, "footer follows actual navigation viewport width at " + label);
            Point navigationOrigin = navigation.TranslatePoint(new Point(), window), actionOrigin = actions.TranslatePoint(new Point(), window);
            Near(navigationOrigin.X, actionOrigin.X, "footer starts at the same left edge as navigation at " + label);
            var footerButtons = actions.Children.OfType<Button>().ToArray(); var settingsButton = footerButtons[1];
            double expectedWidth = actions.ActualWidth / 2 - current.Margin.Left - current.Margin.Right;
            Near(current.ActualWidth, expectedWidth, "pin action fills half of the footer at " + label);
            Near(current.ActualWidth, settingsButton.ActualWidth, "footer actions have equal width at " + label);
            Near(current.ActualHeight, settingsButton.ActualHeight, "footer actions have equal height at " + label);
            Point pinOrigin = current.TranslatePoint(new Point(), window), settingsOrigin = settingsButton.TranslatePoint(new Point(), window);
            Near(pinOrigin.X, navigationOrigin.X + current.Margin.Left, "pin button aligns to catalog left content edge at " + label);
            Near(settingsOrigin.X - pinOrigin.X - current.ActualWidth, 6, "footer actions have only the intended six-pixel gap at " + label);
            Near(settingsOrigin.X + settingsButton.ActualWidth, navigationOrigin.X + navigation.ActualWidth - settingsButton.Margin.Right, "settings fills the footer through the catalog right content edge at " + label);
            foreach (var footerButton in footerButtons)
            {
                Check(footerButton.Visibility == Visibility.Visible && footerButton.IsVisible, "footer action remains visible at " + label + "/" + footerButton.Content);
                Check(footerButton.ActualHeight >= footerButton.MinHeight && footerButton.MinHeight >= 40, "footer action preserves its touch target at " + label);
                AssertCaptionFits(footerButton, label + "/" + footerButton.Content);
            }
            foreach (var button in buttons.Values.Where(item => item.Visibility == Visibility.Visible))
            {
                double tileWidth = navigation.ActualWidth / navigation.Columns - button.Margin.Left - button.Margin.Right;
                Near(button.ActualWidth, tileWidth, "tool fills its responsive catalog column at " + label + "/" + button.Content);
                Check(button.Padding == current.Padding && button.MinHeight == current.MinHeight, "tool and pin share content padding and minimum height at " + label);
                Point origin = button.TranslatePoint(new Point(), window);
                double relative = origin.X - navigationOrigin.X - button.Margin.Left;
                double step = navigation.ActualWidth / navigation.Columns;
                Near(relative, Math.Round(relative / step) * step, "each tile lies on a matching navigation column at " + label);
                AssertCaptionFits(button, label + "/" + button.Content);
            }
        }
        private static void AssertCaptionFits(Button button, string label)
        {
            var caption = Descendants<TextBlock>(button).FirstOrDefault();
            Check(caption != null && caption.ActualWidth > 0 && caption.ActualHeight > 0, "button has a measured caption at " + label);
            double availableWidth = button.ActualWidth - button.Padding.Left - button.Padding.Right;
            double availableHeight = button.ActualHeight - button.Padding.Top - button.Padding.Bottom;
            Check(caption.ActualWidth <= availableWidth + .75 && caption.ActualHeight <= availableHeight + .75, "caption fits inside button padding at " + label);
            Check(caption.DesiredSize.Width <= caption.ActualWidth + .75 && caption.DesiredSize.Height <= caption.ActualHeight + .75, "wrapped caption is not vertically or horizontally clipped at " + label);
        }
        private static void AssertSearchGeometry(IslandWindow window, TextBox search, string label)
        {
            var frame = (Grid)search.Parent;
            var placeholder = frame.Children.OfType<TextBlock>().Single(item => item.Text == UiLocalization.Localize("搜索工具名称"));
            var shortcut = frame.Children.OfType<TextBlock>().Single(item => item.Text == "Ctrl + K");
            Near(search.ActualHeight, 32, "compact search keeps its intended height at " + label);
            Check(!search.AcceptsReturn && search.TextWrapping == TextWrapping.NoWrap, "search retains a single unwrapped input line at " + label);
            const string sample = "查询 QR Ab1";
            search.Text = sample; Settle(window);
            Rect first = search.GetRectFromCharacterIndex(0), last = search.GetRectFromCharacterIndex(sample.Length - 1, true);
            foreach (Rect rectangle in new[] { first, last })
                Check(!rectangle.IsEmpty && rectangle.Height >= search.FontSize && rectangle.Top >= -.75 && rectangle.Bottom <= search.ActualHeight + .75 && rectangle.Left >= -.75 && rectangle.Right <= search.ActualWidth + .75,
                    "first and last input characters remain fully visible inside the 32-pixel search box at " + label + "/" + rectangle);
            double inputLeft = search.TranslatePoint(new Point(first.Left, 0), window).X;
            search.Text = ""; Settle(window);
            Point inputOrigin = search.TranslatePoint(new Point(), window), placeholderOrigin = placeholder.TranslatePoint(new Point(), window), shortcutOrigin = shortcut.TranslatePoint(new Point(), window);
            Check(placeholder.Visibility == Visibility.Visible && placeholder.ActualHeight > 0 && shortcut.ActualHeight > 0, "search placeholder and shortcut have measured visible content at " + label);
            Near(placeholderOrigin.Y + placeholder.ActualHeight / 2, inputOrigin.Y + search.ActualHeight / 2, "placeholder is vertically centered on the input at " + label);
            Near(shortcutOrigin.Y + shortcut.ActualHeight / 2, inputOrigin.Y + search.ActualHeight / 2, "shortcut is vertically centered on the input at " + label);
            Check(Math.Abs(inputLeft - placeholderOrigin.X) <= 2, "placeholder aligns with the actual typed text at " + label + " (input " + inputLeft + ", placeholder " + placeholderOrigin.X + ")");
            Check(placeholderOrigin.X + placeholder.ActualWidth <= shortcutOrigin.X + .75, "placeholder and shortcut occupy separate horizontal areas at " + label);
        }
        private static void AssertSearchLayoutSettles(IslandWindow window, UniformGrid navigation, UniformGrid actions, TextBox search)
        {
            int sizeChanges = 0, layoutPasses = 0;
            SizeChangedEventHandler sizeChanged = (s, e) => sizeChanges++;
            EventHandler layoutUpdated = (s, e) => layoutPasses++;
            navigation.SizeChanged += sizeChanged; actions.SizeChanged += sizeChanged; navigation.LayoutUpdated += layoutUpdated;
            try
            {
                for (int index = 0; index < 12; index++) { search.Text = index % 2 == 0 ? "random" : ""; Settle(window); }
                Check(layoutPasses < 120 && sizeChanges < 120, "repeated search has bounded layout and resize passes");
                var picker = Read<Expander>(window, "toolPicker") as SmoothToolPicker;
                var wait = Stopwatch.StartNew();
                while (picker != null && picker.IsMotionActive && wait.ElapsedMilliseconds < 1200) { Pump(15); StopTimers(window); }
                Check(picker == null || !picker.IsMotionActive, "picker finishes its height transition before checking idle layout");
                Settle(window); sizeChanges = layoutPasses = 0;
                for (int index = 0; index < 5; index++) { Pump(20); StopTimers(window); }
                Check(sizeChanges == 0 && layoutPasses <= 4 && navigation.IsMeasureValid && navigation.IsArrangeValid && actions.IsMeasureValid && actions.IsArrangeValid,
                    "navigation and pin layout stop resizing after the final search settles");
            }
            finally { navigation.SizeChanged -= sizeChanged; actions.SizeChanged -= sizeChanged; navigation.LayoutUpdated -= layoutUpdated; }
        }
        private static T Ancestor<T>(DependencyObject value) where T : DependencyObject
        {
            while (value != null) { value = VisualTreeHelper.GetParent(value); var found = value as T; if (found != null) return found; }
            return null;
        }
        private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
        {
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, index); var match = child as T; if (match != null) yield return match;
                foreach (var item in Descendants<T>(child)) yield return item;
            }
        }
        private static void Near(double value, double expected, string description) { Check(Math.Abs(value - expected) <= .75, description + " (actual " + value + ", expected " + expected + ")"); }
        private static void Settle(IslandWindow window) { window.UpdateLayout(); Pump(20); StopTimers(window); window.UpdateLayout(); Pump(1); StopTimers(window); }
        private static void SendEnter(IslandWindow window, TextBox search)
        {
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent };
            search.RaiseEvent(args); Check(args.Handled, "search handles Enter without bubbling another action");
        }
        private static void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
        private static T Read<T>(IslandWindow window, string name) { return (T)typeof(IslandWindow).GetField(name, Private).GetValue(window); }
        private static object Call(IslandWindow window, string name, params object[] values)
        {
            try { return typeof(IslandWindow).GetMethod(name, Private).Invoke(window, values); }
            catch (TargetInvocationException error) { throw error.InnerException ?? error; }
        }
        private static void StopTimers(IslandWindow window)
        {
            foreach (FieldInfo field in typeof(IslandWindow).GetFields(Private))
                if (field.FieldType == typeof(DispatcherTimer)) ((DispatcherTimer)field.GetValue(window)).Stop();
        }
        private static void Pump(int milliseconds)
        {
            var frame = new DispatcherFrame(); var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
            timer.Tick += (s, e) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
        }
        private static void Check(bool condition, string description) { if (!condition) throw new Exception("Tool navigation: " + description); }
    }
}
