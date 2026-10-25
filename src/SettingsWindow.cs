using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace CodexIsland
{
    internal class SettingsWindow : Window
    {
        public Settings Result { get; private set; }
        private readonly Settings original;
        private ComboBox side, screen, interval, compactMode, compactTextDirection, language, theme, compactSlot1, compactSlot2;
        private CheckBox collapse, startup, notifications, previews, privacy, quiet, alwaysTop, autoSnap, autoMusic, onlineLyrics, hoverExpand;
        private Slider threshold, opacity;
        private readonly System.Collections.Generic.Dictionary<string, CheckBox> moduleChecks = new System.Collections.Generic.Dictionary<string, CheckBox>();
        private TextBox executable, home;
        private TextBox hoverDelay;
        private TextBlock hoverValidation;
        private readonly System.Collections.Generic.List<string> screenNames = new System.Collections.Generic.List<string>();
        private readonly System.Collections.Generic.List<SmoothSettingsSection> animatedSections = new System.Collections.Generic.List<SmoothSettingsSection>();
        private ScrollViewer settingsScroll;

        public SettingsWindow(Settings current)
        {
            original = current;
            Title = "Codex Island · 设置"; MinWidth = 320; MinHeight = 360;
            Width = Math.Max(MinWidth, Math.Min(590, SystemParameters.WorkArea.Width - 40));
            MaxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height - 40); Height = Math.Min(750, MaxHeight);
            ResizeMode = ResizeMode.CanResize; ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Display.Brush("#111518"); Foreground = Display.Brush("#E1ECE5");
            UiLocalization.Mark(this, Window.TitleProperty, Title); UiTheme.Install(Resources);
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"); FontSize = 12;
            UseLayoutRounding = true; SnapsToDevicePixels = true;
            SizeChanged += (s, e) => SettleSectionMotion();
            IsVisibleChanged += (s, e) => { if (!IsVisible) SettleSectionMotion(); };
            Loaded += (s, e) => { AppAppearance.Changed -= SettleSectionMotion; AppAppearance.Changed += SettleSectionMotion; };
            Unloaded += (s, e) => { SettleSectionMotion(); AppAppearance.Changed -= SettleSectionMotion; };
            Closed += (s, e) => { SettleSectionMotion(); AppAppearance.Changed -= SettleSectionMotion; };
            var root = new Grid { Margin = new Thickness(16, 14, 16, 14) }; Content = root;
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 10) }; root.Children.Add(header);
            header.Children.Add(IslandWindow.Text("设置", 21, "#EEF7F1", true));
            UiLocalization.ToolTip(header, "让灵动岛停在顺手的位置");
            var cancel = IslandWindow.Button("取消", () => DialogResult = false); cancel.IsCancel = true;
            var save = IslandWindow.Button("保存设置", Save); save.Background = Display.Brush("#3B6755"); save.Foreground = Display.Brush("#A1EACF");
            var footer = PanelActionLayout.Create(2, 90, cancel, save); footer.Margin = new Thickness(0, 10, 0, 0); Grid.SetRow(footer, 2); root.Children.Add(footer);
            var sections = new StackPanel { Margin = new Thickness(0, 0, 6, 0) };
            var scroll = settingsScroll = new ScrollViewer { Content = sections, VerticalScrollBarVisibility = ScrollBarVisibility.Visible, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; Grid.SetRow(scroll, 1); root.Children.Add(scroll);
            var body = Section(sections, "外观");
            var appearance = Pair(body);
            Label(appearance[0], "界面语言"); language = Combo("简体中文", "English"); language.SelectedIndex = current.Language == "en" ? 1 : 0; appearance[0].Children.Add(language);
            Label(appearance[1], "外观模式"); theme = Combo("暗色", "亮色"); theme.SelectedIndex = current.Theme == "light" ? 1 : 0; appearance[1].Children.Add(theme);
            alwaysTop = Check("窗口保持置顶", current.AlwaysOnTop); body.Children.Add(alwaysTop);
            Label(body, "窗口不透明度"); opacity = new Slider { Minimum = 60, Maximum = 100, TickFrequency = 5, IsSnapToTickEnabled = true, Value = current.OpacityPercent, Margin = new Thickness(0, 3, 0, 2) }; body.Children.Add(opacity);
            body = Section(sections, "消息与通知");
            notifications = Check("汇集 Windows 通知（需要系统授权）", current.NotificationsEnabled); body.Children.Add(notifications);
            previews = Check("新消息到来时短暂展开预览", current.NotificationPreview); body.Children.Add(previews);
            privacy = Check("隐私模式：隐藏发送者和消息正文", current.HideNotificationBody); body.Children.Add(privacy);
            quiet = Check("静默预览：继续收消息，暂不展开预览", current.QuietPreviews); body.Children.Add(quiet);
            var privacyNote = IslandWindow.Text("微信、QQ 等应用需发送系统通知；消息历史仅保留在本次运行的内存中。", 11, "#879D90"); privacyNote.TextWrapping = TextWrapping.Wrap; privacyNote.Margin = new Thickness(0, 6, 0, 0); body.Children.Add(privacyNote);
            body = Section(sections, "停靠位置"); side = Combo("屏幕左侧", "屏幕右侧", "屏幕上方", "屏幕下方", "自由位置（拖动调整）");
            side.SelectedIndex = current.FreePosition ? 4 : current.Side == "left" ? 0 : current.Side == "top" ? 2 : current.Side == "bottom" ? 3 : 1; body.Children.Add(side);
            autoSnap = Check("拖动收起的灵动岛后，吸附最近的屏幕边缘", current.AutoSnapToEdge); body.Children.Add(autoSnap);
            var snapHint = IslandWindow.Text("松手时自动选择上、下、左、右边缘；关闭后可自由摆放。", 11, "#879D90"); snapHint.TextWrapping = TextWrapping.Wrap; snapHint.Margin = new Thickness(0, 6, 0, 0); body.Children.Add(snapHint);
            Label(body, "显示器"); screen = Choice(); screen.Items.Add(ChoiceItem("主显示器（自动）")); screen.SelectedIndex = 0;
            screenNames.Add("");
            foreach (var display in Forms.Screen.AllScreens)
            {
                screenNames.Add(display.DeviceName);
                screen.Items.Add(ChoiceItem(display.DeviceName + "  ·  " + display.Bounds.Width + " × " + display.Bounds.Height, false));
                if (display.DeviceName == current.ScreenName) screen.SelectedIndex = screen.Items.Count - 1;
            }
            body.Children.Add(screen);
            body = Section(sections, "收起时的主内容"); compactMode = Combo("双组件自定义", "时钟与日期", "专注计时", "正在播放"); compactMode.SelectedIndex = current.CompactMode == "clock" ? 1 : current.CompactMode == "focus" ? 2 : current.CompactMode == "music" ? 3 : 0; body.Children.Add(compactMode);
            autoMusic = Check("播放时自动显示音乐卡片（专注计时优先）", current.AutoMusic); body.Children.Add(autoMusic);
            onlineLyrics = Check("在线匹配歌词（发送歌名和歌手到 LRCLIB）", current.OnlineLyricsEnabled); body.Children.Add(onlineLyrics);
            var widgetOptions = new StackPanel();
            var widgetPair = Pair(widgetOptions);
            Label(widgetPair[0], "第一个组件"); compactSlot1 = WidgetCombo(current.CompactSlot1, "codex-primary"); widgetPair[0].Children.Add(compactSlot1);
            Label(widgetPair[1], "第二个组件"); compactSlot2 = WidgetCombo(current.CompactSlot2, "codex-secondary"); widgetPair[1].Children.Add(compactSlot2);
            var widgetHint = IslandWindow.Text("两个组件可自由组合，也可以选择相同内容。", 11, "#879D90"); widgetHint.TextWrapping = TextWrapping.Wrap; widgetHint.Margin = new Thickness(0, 6, 0, 0); widgetOptions.Children.Add(widgetHint);
            widgetOptions.Visibility = compactMode.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
            compactMode.SelectionChanged += (s, e) => widgetOptions.Visibility = compactMode.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
            body.Children.Add(widgetOptions);
            Label(body, "收起时的文字方向"); compactTextDirection = Combo("自动正向", "旋转 180°", "左右镜像");
            compactTextDirection.SelectedIndex = current.CompactTextDirection == "rotate180" ? 1 : current.CompactTextDirection == "mirror" ? 2 : 0; body.Children.Add(compactTextDirection);
            var directionHint = IslandWindow.Text("只影响收起状态，展开面板始终保持正常阅读方向。", 11, "#879D90"); directionHint.TextWrapping = TextWrapping.Wrap; directionHint.Margin = new Thickness(0, 6, 0, 0); body.Children.Add(directionHint);
            body = Section(sections, "工具箱显示模块", true);
            var moduleGrid = new FieldGrid(150); moduleGrid.Margin = new Thickness(0, 6, 0, 0); body.Children.Add(moduleGrid);
            foreach (var module in ToolCatalog.All)
            {
                string key = module.Key; var check = Check(module.SettingsLabel, !Array.Exists((current.HiddenTools ?? "").Split(','), item => item == key), false);
                check.Margin = new Thickness(0); moduleChecks[key] = check; moduleGrid.Children.Add(check);
            }
            var moduleHint = IslandWindow.Text("隐藏专注或提醒模块不会停止计时和待触发的提醒。提醒需要程序运行。快捷键：Ctrl+Alt+Space 展开，Ctrl+Alt+P 隐私模式。", 10, "#879D90"); moduleHint.TextWrapping = TextWrapping.Wrap; moduleHint.Margin = new Thickness(0, 6, 0, 0); body.Children.Add(moduleHint);
            body = Section(sections, "展开 / 收起");
            hoverExpand = Check("悬停时自动展开", current.HoverExpandEnabled); body.Children.Add(hoverExpand);
            Label(body, "悬停等待时间（毫秒）");
            hoverDelay = new TextBox { Text = current.HoverDelayMilliseconds.ToString(CultureInfo.InvariantCulture), MinHeight = 32,
                MaxLength = 8, Padding = new Thickness(7, 5, 7, 5), VerticalContentAlignment = VerticalAlignment.Center,
                IsEnabled = current.HoverExpandEnabled };
            UiLocalization.Protect(hoverDelay); body.Children.Add(hoverDelay);
            var hoverHint = IslandWindow.Text("100—5000 毫秒（1000 毫秒 = 1 秒）；关闭后，点击星标或空白处展开。", 11, "#879D90");
            hoverHint.TextWrapping = TextWrapping.Wrap; hoverHint.Margin = new Thickness(0, 6, 0, 0); body.Children.Add(hoverHint);
            hoverValidation = IslandWindow.Text("", 11, "#F5B9B9"); hoverValidation.TextWrapping = TextWrapping.Wrap;
            hoverValidation.Margin = new Thickness(0, 7, 0, 0); hoverValidation.Visibility = Visibility.Collapsed; body.Children.Add(hoverValidation);
            hoverExpand.Checked += (s, e) => RefreshHoverAvailability(); hoverExpand.Unchecked += (s, e) => RefreshHoverAvailability();
            hoverDelay.TextChanged += (s, e) => ClearHoverValidation();
            RefreshHoverAvailability();
            collapse = Check("鼠标移开后自动收起", current.AutoCollapse); body.Children.Add(collapse);
            startup = Check("登录 Windows 时启动", current.LaunchAtLogin); body.Children.Add(startup);
            body = Section(sections, "Codex 额度");
            Label(body, "自动刷新频率"); interval = Combo("30 秒", "60 秒", "2 分钟", "5 分钟", "10 分钟");
            int[] intervals = { 30, 60, 120, 300, 600 }; interval.SelectedIndex = Array.IndexOf(intervals, current.RefreshSeconds); if (interval.SelectedIndex < 0) interval.SelectedIndex = 1; body.Children.Add(interval);
            Label(body, "低额度提醒阈值");
            var thresholdLabel = IslandWindow.Text("剩余 " + current.AlertThreshold + "% 时提醒", 11, "#B8CCC0");
            threshold = new Slider { Minimum = 5, Maximum = 50, TickFrequency = 5, IsSnapToTickEnabled = true, Value = current.AlertThreshold, Margin = new Thickness(0, 7, 0, 5) };
            threshold.ValueChanged += (s, e) => thresholdLabel.Text = "剩余 " + ((int)threshold.Value) + "% 时提醒";
            body.Children.Add(threshold); body.Children.Add(thresholdLabel);
            Label(body, "Codex 程序路径（留空自动查找）"); executable = PathInput(body, current.CodexPath, false);
            Label(body, "Codex 数据目录（留空自动查找）"); home = PathInput(body, current.CodexHome, true);
            var hint = IslandWindow.Text("使用该目录中已有的 Codex 登录状态。若额度无法读取，请先登录 Codex，或指定已登录账户的数据目录。", 11, "#879D90"); hint.TextWrapping = TextWrapping.Wrap; hint.Margin = new Thickness(0, 6, 0, 0); body.Children.Add(hint);
        }

        private void SettleSectionMotion()
        {
            foreach (var section in animatedSections) section.SettleMotion();
        }

        private StackPanel Section(StackPanel parent, string title, bool collapsible = false)
        {
            var panel = new StackPanel();
            UIElement content = panel;
            if (collapsible)
            {
                var expander = new SmoothSettingsSection { Header = title, Content = panel, IsExpanded = false, FontSize = 13, FontWeight = FontWeights.SemiBold, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                animatedSections.Add(expander);
                UiLocalization.Mark(expander, HeaderedContentControl.HeaderProperty, title); panel.SetValue(System.Windows.Documents.TextElement.FontSizeProperty, 12.0); panel.SetValue(System.Windows.Documents.TextElement.FontWeightProperty, FontWeights.Normal); content = expander;
            }
            else
            {
                var heading = IslandWindow.Text(title, 13, "#E3EFE7", true); heading.Margin = new Thickness(0, 0, 0, 2); heading.TextWrapping = TextWrapping.Wrap; panel.Children.Add(heading);
            }
            parent.Children.Add(new Border { Child = content, Background = Display.Brush("#1A2323"), BorderBrush = Display.Brush("#293A32"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 0, 8) });
            return panel;
        }
        private static StackPanel[] Pair(StackPanel parent)
        {
            var grid = new FieldGrid(150);
            var left = new StackPanel(); var right = new StackPanel();
            grid.Children.Add(left); grid.Children.Add(right); parent.Children.Add(grid); return new[] { left, right };
        }
        private void Label(StackPanel panel, string text) { var label = IslandWindow.Text(text, 11, "#A9BDB0"); label.Margin = new Thickness(0, 8, 0, 4); label.TextWrapping = TextWrapping.Wrap; panel.Children.Add(label); }
        private static ComboBox Choice() { return UiTheme.CompactChoice(new ComboBox { MinHeight = 32, Padding = new Thickness(8, 5, 8, 5), HorizontalContentAlignment = HorizontalAlignment.Stretch }); }
        private static ComboBoxItem ChoiceItem(string text, bool localize = true)
        {
            var label = new FrameworkElementFactory(typeof(TextBlock)); label.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding());
            label.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
            var item = new ComboBoxItem { ContentTemplate = new DataTemplate { VisualTree = label }, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            if (localize) UiLocalization.Content(item, text);
            else { item.Content = text; UiLocalization.Protect(item); }
            return item;
        }
        private ComboBox Combo(params string[] values) { var control = Choice(); foreach (string value in values) control.Items.Add(ChoiceItem(value)); return control; }
        private ComboBox WidgetCombo(string selected, string fallback)
        {
            var control = Choice(); string key = CompactWidgetCatalog.Normalize(selected, fallback);
            foreach (string item in CompactWidgetCatalog.Keys) control.Items.Add(ChoiceItem(CompactWidgetCatalog.Label(item, "zh")));
            control.SelectedIndex = Array.IndexOf(CompactWidgetCatalog.Keys, key); return control;
        }
        private CheckBox Check(string text, bool value, bool isSwitch = true)
        {
            var control = UiLocalization.Content(new CheckBox { IsChecked = value, Foreground = Display.Brush("#CDDCD1"), Margin = new Thickness(0, 6, 0, 0), MinHeight = 24, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Center }, text);
            if (isSwitch) control.SetResourceReference(FrameworkElement.StyleProperty, "Switch.CheckBox");
            var label = new FrameworkElementFactory(typeof(TextBlock)); label.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding()); label.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
            control.ContentTemplate = new DataTemplate { VisualTree = label }; return control;
        }
        private TextBox PathInput(StackPanel panel, string value, bool folder)
        {
            var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
            var box = new TextBox { Text = value, MinHeight = 32, Padding = new Thickness(7, 5, 7, 5), VerticalContentAlignment = VerticalAlignment.Center };
            UiLocalization.Protect(box);
            box.SetBinding(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding("Text") { Source = box });
            var browse = IslandWindow.Button("浏览", () => {
                if (folder) { using (var dialog = new Forms.FolderBrowserDialog()) { dialog.Description = UiLocalization.Localize("选择包含 Codex 登录状态的目录"); if (NativeDialogs.ShowFolder(dialog, this) == Forms.DialogResult.OK) box.Text = dialog.SelectedPath; } }
                else { var dialog = new Microsoft.Win32.OpenFileDialog { Title = UiLocalization.Localize("选择 Codex 程序"), Filter = UiLocalization.FileFilter("Codex 程序|*.exe;*.cmd;*.ps1|所有文件|*.*") }; if (dialog.ShowDialog(this) == true) box.Text = dialog.FileName; }
            });
            PanelActionLayout.Caption(browse); browse.Margin = new Thickness(0); browse.MinHeight = 32; browse.HorizontalAlignment = HorizontalAlignment.Stretch; browse.VerticalAlignment = VerticalAlignment.Stretch;
            Grid.SetColumn(browse, 2); row.Children.Add(browse); row.Children.Add(box); panel.Children.Add(row); return box;
        }
        // Reflow fields during measure so resizing a short dialog never needs a
        // second SizeChanged pass. The same grid keeps module rows aligned.
        private sealed class FieldGrid : Grid
        {
            private readonly double minimumWidth;
            private int columns, count = -1;
            internal FieldGrid(double minimum) { minimumWidth = minimum; }
            protected override Size MeasureOverride(Size constraint)
            {
                int next = Double.IsInfinity(constraint.Width) || constraint.Width >= minimumWidth * 2 + 6 ? 2 : 1;
                if (next != columns || count != Children.Count)
                {
                    columns = next; count = Children.Count; RowDefinitions.Clear(); ColumnDefinitions.Clear();
                    for (int column = 0; column < columns; column++)
                    {
                        if (column > 0) ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
                        ColumnDefinitions.Add(new ColumnDefinition());
                    }
                    for (int row = 0; row < (count + columns - 1) / columns; row++)
                    {
                        if (row > 0) RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
                        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    }
                    for (int index = 0; index < count; index++) { SetColumn(Children[index], index % columns * 2); SetRow(Children[index], index / columns * 2); }
                }
                return base.MeasureOverride(constraint);
            }
        }
        private void Save()
        {
            int dwell;
            bool validDwell = Int32.TryParse(hoverDelay.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out dwell)
                && dwell >= Settings.MinimumHoverDelayMilliseconds && dwell <= Settings.MaximumHoverDelayMilliseconds;
            if (hoverExpand.IsChecked == true && !validDwell)
            {
                UiLocalization.Text(hoverValidation, "请输入 100—5000 之间的整数毫秒值。");
                hoverValidation.Visibility = Visibility.Visible; hoverDelay.Focus(); return;
            }
            if (!validDwell) dwell = Math.Max(Settings.MinimumHoverDelayMilliseconds,
                Math.Min(Settings.MaximumHoverDelayMilliseconds, original.HoverDelayMilliseconds));
            ClearHoverValidation();
            int[] intervals = { 30, 60, 120, 300, 600 };
            string[] sides = { "left", "right", "top", "bottom" };
            var result = new Settings {
                Side = side.SelectedIndex >= 0 && side.SelectedIndex < sides.Length ? sides[side.SelectedIndex] : original.Side, ScreenName = screen.SelectedIndex <= 0 ? "" : screenNames[screen.SelectedIndex],
                FreePosition = side.SelectedIndex == 4, HorizontalPosition = original.HorizontalPosition,
                AutoSnapToEdge = autoSnap.IsChecked == true,
                CompactTextDirection = compactTextDirection.SelectedIndex == 1 ? "rotate180" : compactTextDirection.SelectedIndex == 2 ? "mirror" : "auto",
                CustomExpandedSize = original.CustomExpandedSize, ExpandedWidth = original.ExpandedWidth, ExpandedHeight = original.ExpandedHeight,
                VerticalPosition = original.VerticalPosition, RefreshSeconds = intervals[Math.Max(0, interval.SelectedIndex)],
                AutoCollapse = collapse.IsChecked == true, LaunchAtLogin = startup.IsChecked == true,
                HoverExpandEnabled = hoverExpand.IsChecked == true, HoverDelayMilliseconds = dwell,
                AlertThreshold = (int)threshold.Value, CodexPath = executable.Text.Trim(), CodexHome = home.Text.Trim(),
                NotificationsEnabled = notifications.IsChecked == true, NotificationPreview = previews.IsChecked == true, HideNotificationBody = privacy.IsChecked == true,
                QuietPreviews = quiet.IsChecked == true, AlwaysOnTop = alwaysTop.IsChecked == true, OpacityPercent = (int)opacity.Value,
                CompactMode = compactMode.SelectedIndex == 1 ? "clock" : compactMode.SelectedIndex == 2 ? "focus" : compactMode.SelectedIndex == 3 ? "music" : "quota",
                AutoMusic = autoMusic.IsChecked == true, OnlineLyricsEnabled = onlineLyrics.IsChecked == true,
                CompactSlot1 = SelectedWidget(compactSlot1, original.CompactSlot1, "codex-primary"), CompactSlot2 = SelectedWidget(compactSlot2, original.CompactSlot2, "codex-secondary"),
                Language = language.SelectedIndex < 0 ? original.Language : language.SelectedIndex == 1 ? "en" : "zh", Theme = theme.SelectedIndex < 0 ? original.Theme : theme.SelectedIndex == 1 ? "light" : "dark",
                FocusMinutes = original.FocusMinutes,
                FavoriteTools = original.FavoriteTools, LastTool = original.LastTool,
                HiddenTools = String.Join(",", System.Linq.Enumerable.Select(System.Linq.Enumerable.Where(moduleChecks, pair => pair.Value.IsChecked != true), pair => pair.Key))
            };
            if (result.CodexPath.Length > 0 && !System.IO.File.Exists(result.CodexPath)) { AppNotice.Show(this, UiLocalization.Localize("所选 Codex 程序不存在。"), Title, MessageBoxImage.Warning); return; }
            if (result.CodexHome.Length > 0 && !System.IO.Directory.Exists(result.CodexHome)) { AppNotice.Show(this, UiLocalization.Localize("所选 Codex 数据目录不存在。"), Title, MessageBoxImage.Warning); return; }
            try { if (result.LaunchAtLogin != original.LaunchAtLogin) SettingsStore.SetLaunchAtLogin(result.LaunchAtLogin); }
            catch { AppNotice.Show(this, UiLocalization.Localize("无法修改登录启动项，请检查系统权限。"), Title, MessageBoxImage.Warning); return; }
            Result = result; DialogResult = true;
        }
        private void RefreshHoverAvailability()
        {
            hoverDelay.IsEnabled = hoverExpand.IsChecked == true;
            hoverDelay.Opacity = hoverDelay.IsEnabled ? 1.0 : 0.45;
            if (!hoverDelay.IsEnabled) ClearHoverValidation();
        }
        private void ClearHoverValidation()
        {
            UiLocalization.Text(hoverValidation, ""); hoverValidation.Visibility = Visibility.Collapsed;
        }
        private static string SelectedWidget(ComboBox control, string previous, string fallback)
        {
            return control.SelectedIndex >= 0 && control.SelectedIndex < CompactWidgetCatalog.Keys.Length ? CompactWidgetCatalog.Keys[control.SelectedIndex] : CompactWidgetCatalog.Normalize(previous, fallback);
        }
    }
}
