using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexIsland
{
    internal partial class IslandWindow
    {
        private StackPanel toolsPage, compactAlternate;
        private TextBlock compactAlternateMain, compactAlternateCaption, toolkitHint, emptyTools;
        private Button toolsTab;
        private bool toolsPageSelected;
        private string selectedTool = "focus";
        private readonly Dictionary<string, UIElement> toolPanels = new Dictionary<string, UIElement>();
        private readonly Dictionary<string, Button> toolButtons = new Dictionary<string, Button>();
        private readonly Dictionary<string, string> toolNames = new Dictionary<string, string>();
        private Expander toolPicker;
        private TextBox toolSearch;
        private TextBlock noToolMatches;
        private WrapPanel favoriteTools;
        private Button favoriteCurrent;
        private MediaPanel mediaPanel;
        private FocusPanel focusPanel;
        private NotesPanel notesPanel;
        private ReminderPanel reminderPanel;
        private FileShelfPanel fileShelfPanel;
        private ColorPanel colorPanel;
        private TimeToolsPanel timeToolsPanel;
        private FileHashPanel fileHashPanel;
        private SystemDashboard systemDashboard;
        private HotkeyRegistration hotkeys;
        private readonly DispatcherTimer compactClock = new DispatcherTimer();

        private void BuildToolbox(StackPanel parent)
        {
            toolsPage = new StackPanel { Visibility = Visibility.Collapsed }; parent.Children.Add(toolsPage);
            favoriteTools = new WrapPanel { Margin = new Thickness(0, 0, 0, 2) }; toolsPage.Children.Add(favoriteTools);
            var pickerBody = new StackPanel();
            toolSearch = ToolStyle.Input(); toolSearch.Height = 32; toolSearch.Padding = new Thickness(10, 5, 52, 5); toolSearch.MaxLength = 4096;
            toolSearch.Template = ToolSearchTemplate(); toolSearch.VerticalContentAlignment = VerticalAlignment.Center;
            toolSearch.AcceptsReturn = false; toolSearch.TextWrapping = TextWrapping.NoWrap;
            var searchFrame = new Grid { Margin = new Thickness(0, 5, 0, 0) };
            var searchPlaceholder = Text("搜索工具名称", 12, "#8FA89A"); searchPlaceholder.Margin = new Thickness(14, 0, 56, 0); searchPlaceholder.VerticalAlignment = VerticalAlignment.Center; searchPlaceholder.IsHitTestVisible = false;
            var searchShortcut = Text("Ctrl + K", 9, "#8FA89A", false, false); searchShortcut.Margin = new Thickness(0, 0, 10, 0); searchShortcut.HorizontalAlignment = HorizontalAlignment.Right; searchShortcut.VerticalAlignment = VerticalAlignment.Center; searchShortcut.IsHitTestVisible = false;
            searchFrame.Children.Add(toolSearch); searchFrame.Children.Add(searchPlaceholder); searchFrame.Children.Add(searchShortcut); pickerBody.Children.Add(searchFrame);
            toolSearch.TextChanged += (s, e) => searchPlaceholder.Visibility = String.IsNullOrEmpty(toolSearch.Text) ? Visibility.Visible : Visibility.Collapsed;
            toolSearch.KeyDown += (s, e) => { if (e.Key == Key.Enter) { ExecuteToolSearch(); e.Handled = true; } };
            BuildToolCommandHint(pickerBody);
            var navigation = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
            navigation.SizeChanged += (s, e) => { int columns = Math.Max(2, Math.Min(4, (int)(e.NewSize.Width / 140))); if (navigation.Columns != columns) navigation.Columns = columns; };
            pickerBody.Children.Add(new ScrollViewer { Content = navigation, Margin = new Thickness(0, 6, 0, 0), MaxHeight = 184, VerticalScrollBarVisibility = ScrollBarVisibility.Visible, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
            noToolMatches = ToolStyle.Hint("没有匹配的工具，可清空关键词或检查模块设置。"); pickerBody.Children.Add(noToolMatches);
            favoriteCurrent = Button("☆ 固定当前工具", () => ToggleFavorite(selectedTool)); ConfigureToolTile(favoriteCurrent);
            UiLocalization.ToolTip(favoriteCurrent, "最多固定 6 个常用工具；也可右键工具按钮固定或移除。");
            var settingsControl = Button("模块与外观设置", OpenSettings); ConfigureToolTile(settingsControl);
            // These two global actions fill one balanced row. The catalog may
            // have more columns on wide windows; both regions share outer edges
            // and follow its actual viewport when the scrollbar appears.
            var pickerActions = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            pickerActions.SetBinding(FrameworkElement.WidthProperty, new System.Windows.Data.Binding("ActualWidth") { Source = navigation });
            pickerActions.Children.Add(favoriteCurrent); pickerActions.Children.Add(settingsControl); pickerBody.Children.Add(pickerActions);
            toolPicker = new SmoothToolPicker { Header = "切换工具", Content = pickerBody, Foreground = Display.Brush("#C7E9D7"), Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(3), FontSize = 12,
                MotionAllowed = () => expanded && toolsPageSelected && !peeking && !visualTransitionActive && !dragging && !resizing && !shuttingDown && Opacity > 0 };
            UiLocalization.Protect(toolPicker);
            toolsPage.Children.Add(toolPicker); toolSearch.TextChanged += (s, e) => FilterToolButtons();
            mediaPanel = new MediaPanel(demo); focusPanel = new FocusPanel(demo, settings.FocusMinutes); notesPanel = new NotesPanel(demo); systemDashboard = new SystemDashboard(demo);
            InitializeNowPlaying();
            focusPanel.FocusMinutesChanged += minutes => { settings.FocusMinutes = minutes; SaveSettings(); };
            reminderPanel = new ReminderPanel(demo); fileShelfPanel = new FileShelfPanel(demo); colorPanel = new ColorPanel(demo);
            timeToolsPanel = new TimeToolsPanel(demo); fileHashPanel = new FileHashPanel(demo);
            focusPanel.OnNotify += (title, body) => { if (shuttingDown || demo) return; tray.ShowBalloonTip(7000, title, body, System.Windows.Forms.ToolTipIcon.Info); if (!expanded && !dialogOpen && IsVisible && !settings.QuietPreviews) ShowPeek(new IslandNotification { AppName = "专注计时", Title = title, Body = body, CreatedAt = DateTime.UtcNow, Id = "timer", AppId = "sideisland.timer" }); };
            reminderPanel.OnNotify += (title, body) => {
                if (shuttingDown || demo) return;
                tray.ShowBalloonTip(7000, settings.HideNotificationBody ? "提醒到时间了" : title, settings.HideNotificationBody ? "打开灵动岛的提醒模块查看。" : body, System.Windows.Forms.ToolTipIcon.Info);
                if (!expanded && !dialogOpen && IsVisible && !settings.QuietPreviews) ShowPeek(new IslandNotification { AppName = "提醒与闹钟", Title = title, Body = body, CreatedAt = DateTime.UtcNow, Id = "reminder", AppId = "sideisland.reminders" });
            };
            focusPanel.StatusChanged += () => UpdateToolkitCompact();
            AddTool(navigation, "media", mediaPanel);
            AddTool(navigation, "focus", focusPanel);
            AddTool(navigation, "notes", notesPanel);
            AddTool(navigation, "reminders", reminderPanel);
            AddTool(navigation, "time", timeToolsPanel);
            AddTool(navigation, "launch", new LauncherPanel(demo));
            AddTool(navigation, "files", fileShelfPanel);
            AddTool(navigation, "hash", fileHashPanel);
            AddTool(navigation, "clipboard", new ClipboardPanel(demo));
            AddTool(navigation, "text", new TextToolsPanel(demo));
            AddTool(navigation, "calculator", new CalculatorPanel(demo));
            AddTool(navigation, "converter", new ConverterPanel(demo));
            AddTool(navigation, "random", new RandomToolsPanel(demo));
            AddTool(navigation, "colors", colorPanel);
            AddTool(navigation, "system", systemDashboard);
            BuildAdditionalTools(navigation);
            BuildExperienceTools(navigation);
            BuildNextFeatures(navigation);
            emptyTools = ToolStyle.Hint("所有模块已隐藏，可在模块与外观设置中重新开启。"); toolsPage.Children.Add(emptyTools);
            toolkitHint = ToolStyle.Hint("Ctrl + Alt + Space 展开 / 收起 · Ctrl + Alt + P 隐私模式"); toolkitHint.FontSize = 9; toolkitHint.Margin = new Thickness(0, 12, 0, 0); toolsPage.Children.Add(toolkitHint);
            selectedTool = settings.LastTool; ConfigureToolkit();
        }

        private void AddTool(Panel navigation, string key, UIElement panel)
        {
            string name = ToolCatalog.Find(key).Label;
            var button = Button(name, () => SelectTool(key)); ConfigureToolTile(button);
            AddFavoriteMenu(button, key);
            navigation.Children.Add(button); toolButtons[key] = button; toolPanels[key] = panel; toolNames[key] = name;
            panel.Visibility = Visibility.Collapsed; toolsPage.Children.Add(panel);
        }

        private static void ConfigureToolTile(Button button)
        {
            button.FontSize = 11; button.MinHeight = 40;
            button.Padding = new Thickness(6, 5, 6, 5); button.Margin = new Thickness(0, 0, 6, 6);
            var caption = new FrameworkElementFactory(typeof(TextBlock)); caption.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding());
            caption.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); caption.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Center); caption.SetValue(TextBlock.LineHeightProperty, 14.0);
            button.ContentTemplate = new DataTemplate { VisualTree = caption };
        }

        private static ControlTemplate ToolSearchTemplate()
        {
            // TextBoxView already applies the control's padding. Applying it to
            // the border as well would clip the text in a compact single line.
            var template = new ControlTemplate(typeof(TextBox));
            var frame = new FrameworkElementFactory(typeof(Border)); frame.Name = "Frame";
            frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
            frame.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            frame.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            frame.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            var content = new FrameworkElementFactory(typeof(ScrollViewer)); content.Name = "PART_ContentHost";
            content.SetValue(UIElement.FocusableProperty, false); frame.AppendChild(content); template.VisualTree = frame;
            var focused = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
            focused.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("Island.Focus"), "Frame")); template.Triggers.Add(focused);
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, .42)); template.Triggers.Add(disabled);
            return template;
        }

        private void SelectTool(string key)
        {
            SelectToolCore(key, true);
        }

        private void SelectToolCore(string key, bool explicitSelection)
        {
            key = key ?? "";
            UIElement target;
            bool available = toolPanels.TryGetValue(key, out target) && IsToolVisible(key);
            // Profiles and hidden-module recovery can update selectedTool before
            // this call. Compare the actual visible instance as well as its key.
            UIElement previous = toolPanels.Values.FirstOrDefault(panel => panel.Visibility == Visibility.Visible);
            bool changed = selectedTool != key || !ReferenceEquals(previous, available ? target : null);
            bool pickerOpen = toolPicker.IsExpanded;
            bool animate = changed && available && toolsPageSelected && expanded && !peeking &&
                !visualTransitionActive && !pageTransitionActive && IsLoaded && IsVisible &&
                !shuttingDown && !dragging && !resizing;
            if (changed || !available || (toolTransitionPanel != null && !ReferenceEquals(toolTransitionPanel, target))) StopToolTransition();
            if (colorPanel != null && (selectedTool == "colors" || ReferenceEquals(previous, colorPanel)) && key != "colors") colorPanel.CancelPick();
            selectedTool = key; if (toolPanels.ContainsKey(key)) settings.LastTool = key;
            foreach (var entry in toolPanels) entry.Value.Visibility = entry.Key == key && IsToolVisible(key) ? Visibility.Visible : Visibility.Collapsed;
            foreach (var entry in toolButtons) entry.Value.Background = Display.Brush(entry.Key == key ? "#365649" : "#202C27");
            RefreshToolPickerHeader();
            if (explicitSelection || changed) toolPicker.IsExpanded = false;
            favoriteCurrent.IsEnabled = toolPanels.ContainsKey(key); favoriteCurrent.Content = FavoriteKeys().Contains(key) ? "★ 取消固定当前工具" : "☆ 固定当前工具";
            if (detailsScroll != null && (changed || (explicitSelection && pickerOpen)))
            {
                var smooth = toolPicker as SmoothToolPicker;
                if (smooth != null && smooth.IsMotionActive && !smooth.IsExpanded) smooth.SmoothScrollToTop(detailsScroll);
                else detailsScroll.ScrollToTop();
            }
            SyncToolkitActivity();
            if (changed) BeginToolTransition(available ? target : null, animate);
        }

        private void FilterToolButtons()
        {
            string query = (toolSearch.Text ?? "").Trim(); int visible = 0;
            foreach (var entry in toolButtons)
            {
                bool match = IsToolVisible(entry.Key) && ToolCatalog.Matches(entry.Key, query);
                entry.Value.Visibility = match ? Visibility.Visible : Visibility.Collapsed; if (match) visible++;
            }
            bool command = UpdateToolCommandHint(); noToolMatches.Visibility = visible == 0 && !command ? Visibility.Visible : Visibility.Collapsed;
            var picker = toolPicker as SmoothToolPicker; if (picker != null) picker.RefreshTarget();
        }

        private void RefreshToolPickerHeader()
        {
            if (toolPicker != null) toolPicker.Header = (toolNames.ContainsKey(selectedTool) ? UiLocalization.Localize(toolNames[selectedTool]) + "  ·  " : "") + UiLocalization.Localize("切换工具（" + toolPanels.Keys.Count(IsToolVisible) + "）");
        }

        private bool IsToolVisible(string key) { return !(settings.HiddenTools ?? "").Split(',').Contains(key); }

        private void SelectToolsPage()
        {
            SelectMainPage(true, false);
        }

        private void BuildCompactAlternate()
        {
            compactAlternate = new StackPanel { Width = 59, Height = 133, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
            compactAlternateMain = Text("", 22, "#C5B7F8", true, false); compactAlternateMain.TextWrapping = TextWrapping.Wrap; compactAlternateMain.TextAlignment = TextAlignment.Center; compactAlternateMain.Margin = new Thickness(0, 10, 0, 6);
            compactAlternateCaption = Text("", 10, "#8EAA9B"); compactAlternateCaption.TextAlignment = TextAlignment.Center; compactAlternateCaption.TextWrapping = TextWrapping.Wrap;
            compactAlternate.Children.Add(compactAlternateMain); compactAlternate.Children.Add(compactAlternateCaption); compact.Children.Insert(1, compactAlternate);
            compactAlternate.Background = Brushes.Transparent; compactAlternate.Cursor = Cursors.Hand;
            BuildCompactMusic();
        }

        private void StartToolkit()
        {
            compactClock.Interval = TimeSpan.FromSeconds(1); compactClock.Tick += (s, e) => UpdateToolkitCompact(); compactClock.Start();
            focusPanel.Start();
            reminderPanel.Start();
            StartAdditionalTools();
            StartExperienceTools();
            StartNextFeatures();
            SyncToolkitActivity();
            UpdateToolkitCompact();
        }

        private void ConfigureToolkit()
        {
            ConfigureHoverExpansion();
            Topmost = settings.AlwaysOnTop; Opacity = settings.OpacityPercent / 100.0;
            if (toolPanels.Count == 0) return;
            FilterToolButtons();
            if (!toolPanels.ContainsKey(selectedTool ?? "") || !IsToolVisible(selectedTool)) selectedTool = toolPanels.Keys.FirstOrDefault(IsToolVisible) ?? "";
            emptyTools.Visibility = selectedTool.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            SelectToolCore(selectedTool, false);
            if (selectedTool.Length == 0) toolPicker.IsExpanded = true;
            RebuildFavorites();
            if (systemDashboard != null) systemDashboard.SetPrivacy(settings.HideNotificationBody);
            SyncToolkitActivity();
            if (colorPanel != null && !IsToolVisible("colors")) colorPanel.CancelPick();
            if (portMonitorPanel != null) portMonitorPanel.SetCompactMonitoring(WidgetPoolContains("ports") || (settings.CompactMode == "quota" && (settings.CompactSlot1 == "ports" || settings.CompactSlot2 == "ports")));
            UpdateToolkitCompact();
            ConfigureDesktopExperience();
        }

        private void UpdateToolkitCompact()
        {
            if (compactAlternate == null || primaryRing == null) return;
            UpdateCompactLayout();
            bool music = ShowCompactMusic;
            bool alternate = !music && (settings.CompactMode == "clock" || settings.CompactMode == "focus");
            compactAlternate.Visibility = alternate ? Visibility.Visible : Visibility.Collapsed;
            primaryRing.Visibility = alternate || music ? Visibility.Collapsed : Visibility.Visible; secondaryRing.Visibility = primaryRing.Visibility;
            if (settings.CompactMode == "clock") { compactAlternateMain.FontSize = HorizontalDock ? 26 : 22; compactAlternateMain.Text = DateTime.Now.ToString(HorizontalDock ? "HH:mm" : "HH\nmm", UiLocalization.Culture); compactAlternateCaption.Text = DateTime.Now.ToString(HorizontalDock ? "MM/dd  ddd" : "MM/dd\nddd", UiLocalization.Culture); }
            else if (settings.CompactMode == "focus")
            {
                var focus = CompactWidgetState.Create("focus", new CompactWidgetData { Language = settings.Language, FocusStatus = focusPanel == null ? null : focusPanel.CompactStatus, FocusLabel = focusPanel == null ? null : focusPanel.ActiveTimerLabel });
                compactAlternateMain.FontSize = focus.Value.Length > 5 ? 13 : 17; compactAlternateMain.Text = focus.Value; compactAlternateCaption.Text = focus.Caption;
            }
            if (focusPanel != null && focusPanel.IsRunning && notificationFeed.UnreadCount == 0 && miniStatus != null) miniStatus.Text = focusPanel.CompactStatus;
            if (keepAwakePanel != null && keepAwakePanel.IsRunning && miniStatus != null) miniStatus.Text = keepAwakePanel.CompactStatus;
            UpdateCompactWidgets();
        }

        private void StopToolkit()
        {
            SettleCompactMediaContentTransition();
            StopAdditionalTools();
            StopExperienceTools(); StopNextFeatures();
            musicUiTimer.Stop(); if (nowPlaying != null) nowPlaying.Dispose();
            compactClock.Stop(); if (hotkeys != null) hotkeys.Dispose(); if (mediaPanel != null) mediaPanel.Dispose();
            if (systemDashboard != null) systemDashboard.Dispose(); if (focusPanel != null) focusPanel.Dispose(); if (notesPanel != null) notesPanel.Dispose();
            if (reminderPanel != null) reminderPanel.Dispose(); if (fileShelfPanel != null) fileShelfPanel.Dispose(); if (colorPanel != null) colorPanel.Dispose();
            if (timeToolsPanel != null) timeToolsPanel.Dispose(); if (fileHashPanel != null) fileHashPanel.Dispose();
        }

        private void SyncToolkitActivity()
        {
            bool active = IsLoaded && IsVisible && expanded && toolsPageSelected && !shuttingDown && !runtimeSuspended;
            SyncNextFeatureActivity(active);
            if (focusPanel != null) focusPanel.SetPresentationActive(active && selectedTool == "focus" && IsToolVisible("focus"));
            SyncNowPlayingActivity();
            if (mediaPanel != null) { if (active && selectedTool == "media" && IsToolVisible("media")) mediaPanel.Start(); else mediaPanel.Stop(); }
            if (systemDashboard != null) { if (active && selectedTool == "system" && IsToolVisible("system")) systemDashboard.Start(); else systemDashboard.Stop(); }
            if (timeToolsPanel != null) { if (active && selectedTool == "time" && IsToolVisible("time")) timeToolsPanel.Start(); else timeToolsPanel.Stop(); }
            ConfigureRuntimeWorkload();
        }

        private string[] FavoriteKeys() { return ToolCatalog.NormalizeFavorites(settings.FavoriteTools).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries); }
        private void ToggleFavorite(string key)
        {
            if (!toolPanels.ContainsKey(key)) return;
            var keys = FavoriteKeys().ToList();
            if (!keys.Remove(key))
            {
                if (keys.Count >= ToolCatalog.MaximumFavorites) { toolkitHint.Text = "常用工具最多 6 个，请先右键移除一个。"; return; }
                keys.Add(key);
            }
            settings.FavoriteTools = String.Join(",", keys); SaveSettings(); RebuildFavorites();
            favoriteCurrent.Content = keys.Contains(selectedTool) ? "★ 取消固定当前工具" : "☆ 固定当前工具";
        }
        private void RebuildFavorites()
        {
            if (favoriteTools == null) return; favoriteTools.Children.Clear();
            foreach (string key in FavoriteKeys().Where(IsToolVisible))
            {
                string selected = key; string label = UiLocalization.Localize(ToolCatalog.Find(key).Label);
                var button = Button("★ " + (label.Length > 2 ? label.Substring(2) : label), () => SelectTool(selected), false);
                button.FontSize = 10; button.Padding = new Thickness(8, 6, 8, 6); button.Margin = new Thickness(0, 0, 5, 5); UiLocalization.ToolTip(button, "常用工具 · 右键可移除");
                AddFavoriteMenu(button, key); favoriteTools.Children.Add(button);
            }
            favoriteTools.Visibility = favoriteTools.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        private void AddFavoriteMenu(Button button, string key)
        {
            var menu = new ContextMenu(); var item = UiLocalization.Header(new MenuItem(), ""); menu.Items.Add(item);
            menu.Opened += (s, e) => item.Header = FavoriteKeys().Contains(key) ? "从常用工具移除" : "固定到常用工具";
            // Removing a favorite detaches the menu's placement target before the
            // routed closing event can reach this window. Observe the menu itself.
            menu.Closed += (s, e) => QueueInteractionRecheck();
            item.Click += (s, e) => { menu.IsOpen = false; ToggleFavorite(key); }; button.ContextMenu = menu;
        }
        private void OpenToolSearch()
        {
            SelectToolsPage(); RequestExpanded(true); toolPicker.IsExpanded = true; toolSearch.Focus(); Keyboard.Focus(toolSearch); toolSearch.SelectAll();
            if (detailsScroll != null) detailsScroll.ScrollToTop();
        }
    }
}
