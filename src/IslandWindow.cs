using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace CodexIsland
{
    internal partial class IslandWindow : Window
    {
        internal string DiagnosticPath;
        private readonly SettingsStore store = new SettingsStore();
        private Settings settings;
        private CodexClient client;
        private readonly bool demo;
        private bool expanded, pinned, busy, shuttingDown, dragging, moved, dialogOpen;
        private int generation;
        private UsageSnapshot snapshot;
        private string failure;
        private int bucketIndex;
        private Border shell;
        private IslandBackdrop islandBackdrop;
        private StackPanel compact, details;
        private ScrollViewer detailsScroll;
        private Grid expandedHost, expandedHeader;
        private Border expandedTabs;
        private TextBlock expandedHint;
        private QuotaRing primaryRing, secondaryRing;
        private TextBlock miniStatus, stateText, lastUpdated, cpuText, memoryText, batteryText, primaryValue, secondaryValue, primaryTitle, secondaryTitle, primaryReset, secondaryReset;
        private Border primaryFill, secondaryFill;
        private ComboBox bucketPicker;
        private Button refreshButton, pinButton;
        private Forms.NotifyIcon tray;
        private readonly DispatcherTimer pollTimer = new DispatcherTimer();
        private readonly DispatcherTimer displayTimer = new DispatcherTimer();
        private readonly DispatcherTimer collapseTimer = new DispatcherTimer();
        private readonly DispatcherTimer hoverTimer = new DispatcherTimer();
        private readonly System.Collections.Generic.HashSet<string> alerts = new System.Collections.Generic.HashSet<string>();
        private SystemStats stats = new SystemStats();

        public IslandWindow(bool demoMode)
        {
            demo = demoMode;
            PrepareSettingsMigration();
            settings = store.Load();
            AppAppearance.Configure(settings.Language, settings.Theme);
            Title = "Codex Island · 灵动岛";
            Width = 88; Height = 238;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true; Background = Brushes.Transparent;
            Topmost = true; ShowInTaskbar = false;
            ShowActivated = false;
            UseLayoutRounding = true; SnapsToDevicePixels = true;
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            Foreground = Display.Brush("#F0F5F2");
            BuildInterface();
            AppAppearance.Register(this);
            CreateTray();
            RefreshAppearanceControls();
            pollTimer.Interval = TimeSpan.FromSeconds(settings.RefreshSeconds);
            pollTimer.Tick += (s, e) => Refresh();
            displayTimer.Interval = TimeSpan.FromSeconds(2);
            displayTimer.Tick += (s, e) => RefreshRuntimeTelemetry();
            collapseTimer.Interval = TimeSpan.FromMilliseconds(CollapseDelayMilliseconds);
            collapseTimer.Tick += (s, e) => OnCollapseElapsed();
            ConfigureHoverExpansion();
            hoverTimer.Tick += (s, e) => OnHoverElapsed();
            MouseEnter += (s, e) => OnIslandMouseEnter();
            MouseLeave += (s, e) => OnIslandMouseLeave();
            MouseMove += (s, e) => RecheckCompactActionHover();
            AddHandler(ContextMenuService.ContextMenuOpeningEvent, new ContextMenuEventHandler((s, e) => { hoverTimer.Stop(); collapseTimer.Stop(); QueueInteractionRecheck(); }), true);
            AddHandler(ContextMenuService.ContextMenuClosingEvent, new ContextMenuEventHandler((s, e) => QueueInteractionRecheck()), true);
            IsVisibleChanged += (s, e) => { if (!IsVisible) { StopPageTransition(); StopVisualTransition(true); if (peeking) { if (IsLoaded) DismissNotificationPeek(false); else EndPeek(); } } SyncToolkitActivity(); };
            SizeChanged += (s, e) => { if (!visualTransitionActive) { if (!dragging && !resizing && !applyingBounds) Dock(); SyncWindowSurface(); UpdateNotificationViewport(); } };
            KeyDown += (s, e) => { if (e.Key == Key.Escape) { if (TryCancelExperienceDrag()) { e.Handled = true; return; } if (!HasPopupInteraction()) { CollapseNow(); e.Handled = true; } } };
            KeyDown += (s, e) => { if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control && !HasPopupInteraction()) { OpenToolSearch(); e.Handled = true; } };
            Loaded += (s, e) => {
                SetExpanded(false, false);
                stats = SystemStatsReader.Read();
                if (demo) LoadDemo(); else { pollTimer.Start(); Refresh(); }
                displayTimer.Start(); UpdateDisplay();
                StartNotificationMonitor();
                StartToolkit();
            };
            Closing += (s, e) => {
                string unsaved = batchFilesPanel != null && !batchFilesPanel.TryPrepareClose() ? "batch" : notesPanel != null && !notesPanel.TryFlush() ? "notes" : reminderPanel != null && !reminderPanel.TryFlush() ? "reminders" : fileShelfPanel != null && !fileShelfPanel.TryFlush() ? "files" : workspaceScenePanel != null && !workspaceScenePanel.TryFlush() ? "scenes" : textTemplatePanel != null && textTemplatePanel.HasUnsavedChanges ? "templates" : null;
                if (unsaved == null) { RestoreInterruptedTemporaryLayout(); SaveSettings(); return; }
                e.Cancel = true; Show();
                settings.HiddenTools = String.Join(",", (settings.HiddenTools ?? "").Split(',').Where(key => key != unsaved)); ConfigureToolkit();
                SelectToolsPage(); SelectTool(unsaved); SetExpanded(true); Activate();
                AppNotice.Show(this, UiLocalization.Localize(unsaved == "batch" ? "批处理正在安全结束，请等待当前操作完成后再退出。" : unsaved == "templates" ? "模板草稿尚未保存，请先保存模板，或点击新建 / 清空草稿后退出。" : "当前模块暂时无法保存，已保留窗口和内容。请备份内容，或恢复数据目录权限后重试保存再退出。"), UiLocalization.Localize("修改尚未保存"), MessageBoxImage.Warning);
            };
            Closed += (s, e) => {
                StopPageTransition();
                StopVisualTransition(false);
                shuttingDown = true; pollTimer.Stop(); displayTimer.Stop(); collapseTimer.Stop(); hoverTimer.Stop();
                tray.Visible = false;
                if (tray.ContextMenuStrip != null) tray.ContextMenuStrip.Dispose();
                tray.Dispose();
                if (client != null) client.Abort();
                StopNotificationMonitor();
                StopToolkit();
            };
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            Closed += (s, e) => Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            if (shuttingDown || Dispatcher.HasShutdownStarted) return;
            try { Dispatcher.BeginInvoke(new Action(() => { if (shuttingDown) return; StopPageTransition(); StopVisualTransition(true); OnDesktopDisplaySettingsChanged(); Dock(); })); } catch (InvalidOperationException) { }
        }

        private void BuildInterface()
        {
            islandBackdrop = new IslandBackdrop { Effect = new DropShadowEffect { BlurRadius = 13, ShadowDepth = 2, Opacity = .35, Color = Colors.Black } };
            shell = new Border { Margin = new Thickness(8), CornerRadius = new CornerRadius(32), Background = Brushes.Transparent, BorderThickness = new Thickness(1), ClipToBounds = true };
            outgoingImage = new Image { Visibility = Visibility.Collapsed, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Stretch = Stretch.None };
            // A reversal can make the native host smaller than the old bitmap.
            // Canvas measures that image at its full size without Grid's implicit
            // layout clipping before its translation into the new host.
            var outgoingLayer = new Canvas { ClipToBounds = false, IsHitTestVisible = false };
            outgoingLayer.Children.Add(outgoingImage);
            contentLayer = new Grid(); contentLayer.Children.Add(outgoingLayer); contentLayer.Children.Add(shell);
            var windowRoot = new Grid(); windowRoot.Children.Add(islandBackdrop); windowRoot.Children.Add(contentLayer); Content = windowRoot;
            SyncWindowSurface();
            BuildResizeHandles(windowRoot);
            var container = new Grid(); shell.Child = container;
            // Compact visuals and their gesture maps use a fixed logical size,
            // including at 125/150% DPI. Round only the expanded control layout.
            compact = new StackPanel { Margin = new Thickness(0, 15, 0, 10), HorizontalAlignment = HorizontalAlignment.Center, UseLayoutRounding = false };
            var brand = Text("✦", 18, "#A1EACF"); brand.HorizontalAlignment = HorizontalAlignment.Center; brand.Margin = new Thickness(0, 0, 0, 6);
            compact.Children.Add(brand);
            InitializeCompactNotifications(brand);
            primaryRing = new QuotaRing(); secondaryRing = new QuotaRing { Accent = "#C5B7F8", Caption = "7d" };
            compact.Children.Add(primaryRing); compact.Children.Add(secondaryRing);
            BuildCompactAlternate();
            miniStatus = Text("连接中", 9, "#85928E"); miniStatus.HorizontalAlignment = HorizontalAlignment.Center; miniStatus.Margin = new Thickness(0, 8, 0, 0);
            miniStatus.SetBinding(ToolTipProperty, new System.Windows.Data.Binding("Text") { Source = miniStatus });
            compact.Children.Add(miniStatus); container.Children.Add(compact);
            UiLocalization.ToolTip(compact, "点击组件快捷操作 · 按住拖动\n悬停星标或空白处展开");
            WireDrag(shell, true);

            // Chrome is outside the scrolling document. Reserve the same 10 DIP
            // gutter for every page so overflow cannot move its right edge.
            expandedHost = new Grid { Margin = new Thickness(23, 20, 25, 18), Visibility = Visibility.Collapsed };
            expandedHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            expandedHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            expandedHost.RowDefinitions.Add(new RowDefinition());
            expandedHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); container.Children.Add(expandedHost);
            details = new StackPanel { Visibility = Visibility.Collapsed };
            detailsScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Visible, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = details, Visibility = Visibility.Collapsed };
            Grid.SetRow(detailsScroll, 2);
            detailsScroll.SizeChanged += (s, e) => UpdateNotificationViewport();
            var header = expandedHeader = new Grid { Margin = new Thickness(0, 0, 10, 10), Background = Brushes.Transparent };
            header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var titleStack = new StackPanel { Background = Brushes.Transparent, Cursor = Cursors.SizeAll, Margin = new Thickness(0, 0, 6, 0) };
            var brandTitle = Text("✦ SIDE ISLAND", 14, "#EFF8F3", true, false); titleStack.Children.Add(brandTitle);
            var sub = Text("消息、状态，抬眼即见", 10, "#8D9C96"); sub.TextWrapping = TextWrapping.Wrap; sub.Margin = new Thickness(0, 3, 0, 0); titleStack.Children.Add(sub);
            header.SizeChanged += (s, e) => { brandTitle.FontSize = e.NewSize.Width < 280 ? 11 : 14; UpdateNotificationViewport(); };
            header.Children.Add(titleStack); WireDrag(titleStack);
            pinButton = Button("◇", () => { pinned = !pinned; pinButton.Content = pinned ? "◆" : "◇"; pinButton.ToolTip = pinned ? "取消固定展开" : "固定展开"; });
            pinButton.Width = 32; pinButton.Height = 32; pinButton.Padding = new Thickness(0); pinButton.ToolTip = "固定展开";
            var headerActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
            var collapseButton = Button("−", CollapseNow); collapseButton.Width = 32; collapseButton.Height = 32; collapseButton.Padding = new Thickness(0); collapseButton.Margin = new Thickness(0, 0, 6, 0); collapseButton.ToolTip = "立即收起（Esc）";
            BuildAppearanceControls(headerActions);
            headerActions.Children.Add(collapseButton); headerActions.Children.Add(pinButton);
            foreach (FrameworkElement action in headerActions.Children) action.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(headerActions, 1); header.Children.Add(headerActions); expandedHost.Children.Add(header);
            BuildNotificationInterface(details, container);
            statusPage = new StackPanel(); details.Children.Add(statusPage);

            var statePanel = new Border { Background = Display.Brush("#1B2824"), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 0, 0, 14) };
            stateText = Text("●  正在连接 Codex…", 11, "#A1EACF"); stateText.TextWrapping = TextWrapping.Wrap; statePanel.Child = stateText; statusPage.Children.Add(statePanel);
            bucketPicker = new ComboBox { Margin = new Thickness(0, 0, 0, 12), MinHeight = 28, Visibility = Visibility.Collapsed };
            UiLocalization.Protect(bucketPicker);
            bucketPicker.SelectionChanged += (s, e) => { bucketIndex = Math.Max(0, bucketPicker.SelectedIndex); UpdateDisplay(); };
            statusPage.Children.Add(bucketPicker);
            statusPage.Children.Add(CreateQuotaCard("#A1EACF", out primaryTitle, out primaryValue, out primaryReset, out primaryFill));
            statusPage.Children.Add(CreateQuotaCard("#C5B7F8", out secondaryTitle, out secondaryValue, out secondaryReset, out secondaryFill));
            var systemHeading = Text("此刻 · 系统状态", 10, "#879690"); systemHeading.Margin = new Thickness(0, 7, 0, 10); statusPage.Children.Add(systemHeading);
            var system = new Grid(); for (int i = 0; i < 3; i++) system.ColumnDefinitions.Add(new ColumnDefinition());
            system.Children.Add(Stat("CPU", out cpuText, 0)); system.Children.Add(Stat("内存", out memoryText, 1)); system.Children.Add(Stat("电源", out batteryText, 2)); statusPage.Children.Add(system);
            var divider = new Border { Height = 1, Background = Display.Brush("#2B3336"), Margin = new Thickness(0, 17, 0, 12) }; statusPage.Children.Add(divider);
            var footer = new Grid(); footer.ColumnDefinitions.Add(new ColumnDefinition()); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            lastUpdated = Text("正在获取额度", 10, "#879690"); lastUpdated.VerticalAlignment = VerticalAlignment.Center; footer.Children.Add(lastUpdated);
            refreshButton = Button("刷新", Refresh); refreshButton.Margin = new Thickness(0, 0, 7, 0); Grid.SetColumn(refreshButton, 1); footer.Children.Add(refreshButton);
            var settingsButton = Button("设置", OpenSettings); Grid.SetColumn(settingsButton, 2); footer.Children.Add(settingsButton); statusPage.Children.Add(footer);
            BuildToolbox(details);
            expandedHost.Children.Add(detailsScroll);
            // This instruction belongs to the window, not a page's scrolling
            // document. Reserve its footer so switching content cannot move it.
            var hint = expandedHint = Text("拖动标题移动  /  拖动边缘或四角调整大小", 9, "#64746D"); hint.TextWrapping = TextWrapping.Wrap; hint.TextAlignment = TextAlignment.Center; hint.Margin = new Thickness(0, 8, 10, 0); Grid.SetRow(hint, 3); expandedHost.Children.Add(hint);
            hint.SizeChanged += (s, e) => UpdateNotificationViewport();
            var menu = new ContextMenu();
            AddMenu(menu, "展开 / 收起", () => RequestExpanded(!expanded));
            AddMenu(menu, "停靠左侧", () => ChangeSide("left")); AddMenu(menu, "停靠右侧", () => ChangeSide("right"));
            AddMenu(menu, "停靠上方", () => ChangeSide("top")); AddMenu(menu, "停靠下方", () => ChangeSide("bottom"));
            AddMenu(menu, "自由摆放（关闭吸附）", () => { settings.AutoSnapToEdge = false; settings.FreePosition = true; RememberPlacement(false); });
            AddMenu(menu, "开启最近边缘吸附", () => { settings.AutoSnapToEdge = true; RememberPlacement(true); });
            var textMenu = new MenuItem { Header = "收起文字方向" }; menu.Items.Add(textMenu);
            foreach (var entry in new[] { new[] { "自动正向", "auto" }, new[] { "旋转 180°", "rotate180" }, new[] { "左右镜像", "mirror" } }) { string mode = entry[1]; var item = new MenuItem { Header = entry[0] }; item.Click += (s, e) => SetCompactTextDirection(mode); textMenu.Items.Add(item); }
            AddMenu(menu, "恢复默认展开大小", ResetExpandedSize);
            AddMenu(menu, "中 / English", ToggleLanguage); AddMenu(menu, "亮色 / 暗色", ToggleTheme);
            AddMenu(menu, "立即刷新", Refresh); AddMenu(menu, "设置", OpenSettings); AddMenu(menu, "退出", Close);
            AddMenu(menu, "工具箱", () => { SelectToolsPage(); RequestExpanded(true); });
            AddMenu(menu, "切换静默预览", () => { settings.QuietPreviews = !settings.QuietPreviews; SaveSettings(); });
            shell.ContextMenu = menu;
            menu.Opened += (s, e) => { dialogOpen = true; collapseTimer.Stop(); };
            menu.Closed += (s, e) => { dialogOpen = false; QueueInteractionRecheck(); };
            SelectPage(true);
        }

        private Border Stat(string label, out TextBlock value, int column)
        {
            var stack = new StackPanel(); value = Text("—", 17, "#DDE9E2", true); stack.Children.Add(value);
            var name = Text(label, 10, "#819289"); name.Margin = new Thickness(0, 4, 0, 0); stack.Children.Add(name);
            var border = new Border { Child = stack, Padding = new Thickness(11, 10, 8, 10), CornerRadius = new CornerRadius(10), Background = Display.Brush("#1A2023"), Margin = new Thickness(0, 0, column == 2 ? 0 : 7, 0) };
            Grid.SetColumn(border, column); return border;
        }

        private Border CreateQuotaCard(string accent, out TextBlock title, out TextBlock value, out TextBlock reset, out Border fill)
        {
            var stack = new StackPanel(); var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            title = Text("额度", 12, "#C6D1CB"); title.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(title);
            var number = new StackPanel { Orientation = Orientation.Horizontal }; value = Text("—", 27, accent, true); number.Children.Add(value);
            var left = Text(" 剩余", 10, "#87968E"); left.VerticalAlignment = VerticalAlignment.Bottom; left.Margin = new Thickness(0, 0, 0, 5); number.Children.Add(left); Grid.SetColumn(number, 1); row.Children.Add(number); stack.Children.Add(row);
            var track = new Border { Height = 5, Background = Display.Brush("#30383B"), CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 10, 0, 9) };
            fill = new Border { Height = 5, Width = 0, Background = Display.Brush(accent), CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left }; track.Child = fill;
            track.SizeChanged += (s, e) => UpdateDisplay(); stack.Children.Add(track);
            reset = Text("重置时间未知", 10, "#8D9D94"); stack.Children.Add(reset);
            return new Border { Child = stack, Background = Display.Brush("#1A2023"), BorderBrush = Display.Brush("#252F31"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(15), Padding = new Thickness(16, 12, 16, 13), Margin = new Thickness(0, 0, 0, 10) };
        }

        internal static TextBlock Text(string value, double size, string color, bool bold = false, bool localize = true)
        {
            var text = new TextBlock { Text = value, FontSize = size, Foreground = Display.Brush(color), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
            return localize ? UiLocalization.Text(text, value) : UiLocalization.Protect(text);
        }
        internal static Button Button(string label, Action action, bool localize = true)
        {
            var button = new Button { Content = label, Cursor = Cursors.Hand, Padding = new Thickness(10, 5, 10, 5), Background = Display.Brush("#27312F"), Foreground = Display.Brush("#DCEAE2"), BorderBrush = Display.Brush("#3A5145"), BorderThickness = new Thickness(1), FontSize = 11, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
            button.Click += (s, e) => action(); return localize ? UiLocalization.Content(button, label) : UiLocalization.Protect(button);
        }
        private void AddMenu(ContextMenu menu, string name, Action action) { var item = UiLocalization.Header(new MenuItem { Header = name }, name); item.Click += (s, e) => action(); menu.Items.Add(item); }

        private void CreateTray()
        {
            tray = new Forms.NotifyIcon { Text = "Codex Island · 后台监控", Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location), Visible = !demo };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("显示灵动岛", null, (s, e) => Dispatcher.Invoke(() => Reveal()));
            menu.Items.Add("停靠左侧", null, (s, e) => Dispatcher.Invoke(() => ChangeSide("left")));
            menu.Items.Add("停靠右侧", null, (s, e) => Dispatcher.Invoke(() => ChangeSide("right")));
            menu.Items.Add("停靠上方", null, (s, e) => Dispatcher.Invoke(() => ChangeSide("top")));
            menu.Items.Add("停靠下方", null, (s, e) => Dispatcher.Invoke(() => ChangeSide("bottom")));
            menu.Items.Add("刷新额度", null, (s, e) => Dispatcher.Invoke(Refresh));
            menu.Items.Add("设置", null, (s, e) => Dispatcher.Invoke(OpenSettings));
            menu.Items.Add("中 / English", null, (s, e) => Dispatcher.Invoke(ToggleLanguage));
            menu.Items.Add("亮色 / 暗色", null, (s, e) => Dispatcher.Invoke(ToggleTheme));
            menu.Items.Add("暂时隐藏（继续监控）", null, (s, e) => Dispatcher.Invoke(Hide));
            menu.Items.Add("退出", null, (s, e) => Dispatcher.Invoke(Close));
            foreach (Forms.ToolStripItem item in menu.Items) item.Tag = item.Text;
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += (s, e) => Dispatcher.Invoke(() => Reveal());
        }

        internal void Reveal(bool expand = true)
        {
            RestoreDesktopExperienceForReveal();
            if (shuttingDown) return;
            Rect visibleArea = PlacementArea(); visibleArea.Inflate(1, 1);
            bool animate = IsVisible && WindowState == WindowState.Normal &&
                visibleArea.Contains(CurrentBounds()) && visibleArea.Contains(CurrentVisualBounds());
            // Show alone leaves an existing minimized HWND minimized. Restore it
            // before applying bounds, and clamp saved placement to a live display.
            WindowState = WindowState.Normal;
            collapseTimer.Stop(); Show();
            if (animate) RequestExpanded(expand);
            else
            {
                // Recovery must not animate across an off-screen origin or build
                // a huge temporary surface between two unrelated monitors.
                SetExpanded(expand, false);
                if (!expand) hoverSuppressed = true;
            }
            Dock();
            if (expand && !Activate() && !Topmost) { Topmost = true; Topmost = false; }
        }

        private Rect WorkArea()
        {
            var screen = Forms.Screen.AllScreens.FirstOrDefault(x => x.DeviceName == settings.ScreenName) ?? Forms.Screen.PrimaryScreen;
            var area = screen.WorkingArea;
            var source = PresentationSource.FromVisual(this); Matrix scale = source == null ? Matrix.Identity : source.CompositionTarget.TransformFromDevice;
            return new Rect(scale.Transform(new Point(area.Left, area.Top)), scale.Transform(new Point(area.Right, area.Bottom)));
        }
        private void Dock()
        {
            if (dragging || resizing || applyingBounds || visualTransitionActive) return;
            Rect area = PlacementArea();
            Rect placed = WindowGeometry.PlaceFromCompact(SavedCompactBounds(), new Size(Width, Height), settings.Side, area);
            // Rect subtraction can differ by a fraction of a pixel. Do not treat
            // that rounding as a screen-size clamp and freeze a running animation.
            if (placed.Width < Width - .5 || placed.Height < Height - .5) { FreezeSizeAnimation(); ApplyBounds(placed); }
            else { Left = placed.Left; Top = placed.Top; }
        }

        private void RequestExpanded(bool value)
        {
            // User input changes the mode at once, while its visual transition
            // continues. An explicit collapse stays closed under the pointer.
            if (!value) hoverSuppressed = true;
            SetExpanded(value, true);
        }

        internal void SetExpanded(bool value, bool animate = false)
        {
            if (dragging || resizing) return;
            SetExpandedCore(value, animate);
        }

        private void SetExpandedCore(bool value, bool animate)
        {
            bool changingMode = expanded != value || peeking;
            CaptureTransitionContent(animate && (expanded != value || peeking));
            // Preserve the current rendered frame for a mode change, then end
            // its independent page fade. Explicit nonanimated requests also
            // settle page content for startup, diagnostics and manual sizing.
            if (changingMode || !animate || !value) StopPageTransition();
            if (!value) expandedSessionDefaultHeight = Double.NaN;
            else if (Double.IsNaN(expandedSessionDefaultHeight)) expandedSessionDefaultHeight = DefaultExpandedHeight();
            hoverTimer.Stop();
            // Refreshing an already expanded page must not discard the pending
            // mouse-leave collapse. Only a mode change replaces that request.
            if (!value || !expanded || peeking) collapseTimer.Stop();
            if (!value && !animate && (expanded || peeking)) hoverSuppressed = true;
            if (value) hoverSuppressed = false;
            EndPeek();
            expanded = value;
            UpdateCompactLayout(); UpdateToolkitCompact();
            UpdateResizeChrome();
            compact.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            details.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            detailsScroll.Visibility = details.Visibility;
            expandedHost.Visibility = details.Visibility;
            shell.CornerRadius = new CornerRadius(value ? 26 : 32);
            shell.Cursor = value ? Cursors.Arrow : Cursors.SizeAll;
            double targetWidth = Math.Min(value ? settings.ExpandedWidth : CompactSize.Width, PlacementArea().Width);
            double defaultHeight = expandedSessionDefaultHeight;
            double targetHeight = Math.Min(value ? settings.CustomExpandedSize ? settings.ExpandedHeight : defaultHeight : CompactSize.Height, PlacementArea().Height);
            TransitionSize(targetWidth, targetHeight, animate);
            SyncToolkitActivity();
        }

        private void ChangeSide(string side) { settings.Side = side; settings.FreePosition = false; settings.AutoSnapToEdge = true; SaveSettings(); SetExpanded(expanded, false); }
        private void SaveSettings() { if (demo) return; try { store.Save(settings); } catch { stateText.Text = "设置未能保存，请检查目录权限"; } }

        private async void Refresh()
        {
            if (busy || shuttingDown || demo) return;
            busy = true; refreshButton.IsEnabled = false; refreshButton.Content = "同步中"; UpdateDisplay();
            int currentGeneration = generation;
            var activeClient = client ?? (client = new CodexClient(settings.CodexPath, settings.CodexHome));
            try
            {
                var result = await Task.Run(() => activeClient.ReadLimits(25000));
                if (shuttingDown || currentGeneration != generation) return;
                snapshot = result; failure = null; Title = "Codex Island · 已同步 " + DateTime.Now.ToString("HH:mm:ss"); RebuildBuckets(); CheckAlerts();
                WriteDiagnostic("ok; buckets=" + result.Buckets.Count + "; expanded=" + expanded + "; updated=" + DateTime.Now.ToString("O"));
            }
            catch (Exception ex)
            {
                if (!shuttingDown && currentGeneration == generation) { failure = ex is CodexClientException ? ex.Message : "暂时无法读取额度，请检查 Codex 设置。"; Title = "Codex Island · 连接待恢复"; }
                WriteDiagnostic("unavailable");
            }
            finally
            {
                busy = false;
                if (!shuttingDown) { refreshButton.IsEnabled = true; refreshButton.Content = "刷新"; UpdateDisplay(); if (currentGeneration != generation) Refresh(); }
            }
        }

        private void WriteDiagnostic(string value)
        {
            if (string.IsNullOrWhiteSpace(DiagnosticPath)) return;
            try { File.WriteAllText(DiagnosticPath, value); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private void RebuildBuckets()
        {
            int wantedIndex = bucketIndex;
            bucketPicker.Items.Clear();
            if (snapshot == null) { bucketPicker.Visibility = Visibility.Collapsed; return; }
            foreach (var bucket in snapshot.Buckets) bucketPicker.Items.Add(string.IsNullOrEmpty(bucket.Name) ? bucket.LimitId : bucket.Name);
            bucketIndex = Math.Min(wantedIndex, Math.Max(0, snapshot.Buckets.Count - 1));
            bucketPicker.SelectedIndex = bucketIndex;
            bucketPicker.Visibility = snapshot.Buckets.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            if (expanded)
            {
                if (visualTransitionActive) SetExpanded(true, true);
                else { UpdateNotificationViewport(); SyncToolkitActivity(); }
            }
        }

        private QuotaBucket CurrentBucket { get { return snapshot != null && snapshot.Buckets.Count > 0 ? snapshot.Buckets[Math.Min(bucketIndex, snapshot.Buckets.Count - 1)] : null; } }
        private void UpdateDisplay()
        {
            if (primaryRing == null) return;
            var bucket = CurrentBucket;
            var first = bucket == null ? null : bucket.Primary; var second = bucket == null ? null : bucket.Secondary;
            bool stale = failure != null || (snapshot != null && (DateTime.UtcNow - snapshot.FetchedAt.ToUniversalTime()).TotalSeconds > settings.RefreshSeconds * 2 + 30);
            primaryRing.AlertThreshold = settings.AlertThreshold; secondaryRing.AlertThreshold = settings.AlertThreshold;
            primaryRing.Remaining = first == null ? null : first.Remaining; primaryRing.Caption = Display.ShortWindow(first == null ? null : first.WindowMinutes, "短期"); primaryRing.Stale = stale; primaryRing.InvalidateVisual();
            secondaryRing.Remaining = second == null ? null : second.Remaining; secondaryRing.Caption = Display.ShortWindow(second == null ? null : second.WindowMinutes, "长期"); secondaryRing.Stale = stale; secondaryRing.InvalidateVisual();
            UpdateCard(first, primaryTitle, primaryValue, primaryReset, primaryFill, "短期额度", "#A1EACF", stale);
            UpdateCard(second, secondaryTitle, secondaryValue, secondaryReset, secondaryFill, "长期额度", "#C5B7F8", stale);
            miniStatus.Text = demo ? "演示" : stale ? "未同步" : busy && snapshot == null ? "连接中" : snapshot == null ? "未连接" : "剩余 %";
            miniStatus.Foreground = Display.Brush(stale ? "#E9B883" : "#85928E");
            if (demo) stateText.Text = "●  演示数据 · 仅用于预览界面";
            else if (failure != null) stateText.Text = "●  " + failure + (snapshot == null ? "" : "（显示上次数据）");
            else if (snapshot == null) stateText.Text = busy ? "●  正在连接本机 Codex…" : "●  等待连接 Codex";
            else if (stale) stateText.Text = "●  数据待同步 · 显示上次获取的额度";
            else if (snapshot.Buckets.Count == 0) stateText.Text = "●  当前账户未返回可用的额度信息";
            else stateText.Text = "●  后台监控中 · 每 " + settings.RefreshSeconds + " 秒更新";
            stateText.Foreground = Display.Brush(stale ? "#F1BD8E" : "#A1EACF");
            lastUpdated.Text = snapshot == null ? "额度暂不可用" : (stale ? "上次成功 " : "更新于 ") + snapshot.FetchedAt.ToLocalTime().ToString("HH:mm:ss");
            cpuText.Text = Display.Percent(stats.CpuPercent); memoryText.Text = Display.Percent(stats.MemoryPercent); batteryText.Text = stats.BatteryPercent.HasValue ? Display.Percent(stats.BatteryPercent) : stats.OnAc == true ? "已接电" : "—";
            if (!demo) tray.Text = ("Codex Island · " + (stale ? "数据待同步" : "剩余 " + Display.Percent(first == null ? null : first.Remaining) + " / " + Display.Percent(second == null ? null : second.Remaining)));
            UpdateNotificationBadge();
            UpdateToolkitCompact();
        }

        private void UpdateCard(QuotaWindow window, TextBlock title, TextBlock value, TextBlock reset, Border fill, string fallback, string accent, bool stale)
        {
            double? remaining = window == null ? null : window.Remaining;
            title.Text = Display.Window(window == null ? null : window.WindowMinutes, fallback);
            value.Text = Display.Percent(remaining); value.Foreground = Display.Brush(stale ? "#93A19A" : remaining <= settings.AlertThreshold ? "#FFBE83" : accent);
            reset.Text = window == null ? "当前账户未提供此周期" : Display.Reset(window.ResetsAt);
            var parent = fill.Parent as FrameworkElement; double width = parent == null ? 0 : parent.ActualWidth;
            fill.Width = width * (remaining ?? 0) / 100.0; fill.Background = value.Foreground;
        }

        private void CheckAlerts()
        {
            if (demo || snapshot == null) return;
            foreach (var bucket in snapshot.Buckets)
                foreach (var pair in new[] { Tuple.Create("primary", bucket.Primary), Tuple.Create("secondary", bucket.Secondary) })
                {
                    var window = pair.Item2; if (window == null || !window.Remaining.HasValue) continue;
                    string key = bucket.LimitId + ":" + pair.Item1 + ":" + window.ResetsAt;
                    if (window.Remaining <= settings.AlertThreshold && alerts.Add(key)) tray.ShowBalloonTip(5000, "Codex 额度提醒", Display.Window(window.WindowMinutes, "额度") + "剩余 " + Display.Percent(window.Remaining), Forms.ToolTipIcon.Warning);
                }
        }

        private void OpenSettings()
        {
            if (dialogOpen) return;
            dialogOpen = true; collapseTimer.Stop();
            var dialog = new SettingsWindow(settings) { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                settings = dialog.Result;
                ApplyAppearance();
                SaveSettings(); generation++;
                var previous = client; client = null;
                if (previous != null) Task.Run(() => previous.Dispose());
                snapshot = null; failure = null; bucketIndex = 0; RebuildBuckets();
                pollTimer.Interval = TimeSpan.FromSeconds(settings.RefreshSeconds); Dock(); Refresh();
                ConfigureNotifications();
                ConfigureToolkit();
                SetExpanded(expanded, false);
            }
            dialogOpen = false; collapseTimer.Start();
        }

        private void LoadDemo()
        {
            long now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
            snapshot = new UsageSnapshot { FetchedAt = DateTime.UtcNow, PlanType = "demo", Buckets = new System.Collections.Generic.List<QuotaBucket> { new QuotaBucket { LimitId = "codex", Name = "Codex", Primary = new QuotaWindow { UsedPercent = 28, WindowMinutes = 300, ResetsAt = now + 8340 }, Secondary = new QuotaWindow { UsedPercent = 46, WindowMinutes = 10080, ResetsAt = now + 355200 } } } };
            stats = new SystemStats { CpuPercent = 12, MemoryPercent = 48, OnAc = true }; RebuildBuckets();
            AddDemoNotifications(false);
        }

        internal void ExportPreviews(string directory)
        {
            displayTimer.Stop(); Directory.CreateDirectory(directory);
            SetExpanded(false, false); UpdateDisplay(); UpdateLayout(); Render(Path.Combine(directory, "compact.png"));
            SetExpanded(true, false); UpdateDisplay(); UpdateLayout(); Render(Path.Combine(directory, "expanded.png"));
            SelectPage(false); SetExpanded(true, false); UpdateLayout(); Render(Path.Combine(directory, "status.png"));
            SelectPage(true); settings.HideNotificationBody = true; UpdateNotificationList(); UpdateLayout(); Render(Path.Combine(directory, "privacy.png"));
            SelectToolsPage(); foreach (string key in toolPanels.Keys) { SelectTool(key); SetExpanded(true, false); UpdateLayout(); Render(Path.Combine(directory, "tool-" + key + ".png")); }
            toolPicker.IsExpanded = true; Render(Path.Combine(directory, "tool-picker.png")); toolPicker.IsExpanded = false;
            SelectPage(true); settings.HideNotificationBody = false; notificationFilter = NotificationFilter.All; UpdateNotificationList();
            settings.CustomExpandedSize = true; settings.ExpandedWidth = 560; settings.ExpandedHeight = 680; SetExpanded(true, false); Render(Path.Combine(directory, "resized.png"));
            settings.CompactMode = "clock"; UpdateToolkitCompact(); SetExpanded(false, false); Render(Path.Combine(directory, "compact-clock.png"));
            settings.CompactMode = "focus"; UpdateToolkitCompact(); Render(Path.Combine(directory, "compact-focus.png"));
            foreach (string edge in new[] { "left", "right", "top", "bottom" })
            {
                settings.CompactMode = "quota"; ChangeSide(edge); Render(Path.Combine(directory, "dock-" + edge + ".png"));
                SetExpanded(true, false); Render(Path.Combine(directory, "expanded-" + edge + ".png")); SetExpanded(false, false);
            }
            settings.CompactMode = "clock"; ChangeSide("top"); Render(Path.Combine(directory, "horizontal-clock.png"));
            settings.CompactMode = "focus"; UpdateToolkitCompact(); Render(Path.Combine(directory, "horizontal-focus.png"));
            settings.CompactMode = "quota"; UpdateToolkitCompact(); SetCompactTextDirection("rotate180"); Render(Path.Combine(directory, "text-rotate180.png"));
            SetCompactTextDirection("mirror"); Render(Path.Combine(directory, "text-mirror.png"));
            Close();
        }
        private void Render(string path)
        {
            UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)ActualWidth * 2, (int)ActualHeight * 2, 192, 192, PixelFormats.Pbgra32); bitmap.Render(this);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var output = File.Create(path)) encoder.Save(output);
        }
        internal void RunSmoke()
        {
            try
            {
                SetExpanded(false, false); UpdateLayout(); if (Math.Abs(Width - 88) > 1) throw new Exception("Compact width");
                SetExpanded(true, false); UpdateLayout(); if (details.Visibility != Visibility.Visible) throw new Exception("Expanded visibility");
                settings.Side = "left"; Dock(); if (Left < WorkArea().Left) throw new Exception("Left docking");
                settings.Side = "right"; Dock(); if (Left + Width > WorkArea().Right + 1) throw new Exception("Right docking");
                failure = "测试断网"; UpdateDisplay(); if (!primaryRing.Stale || !secondaryRing.Stale) throw new Exception("Stale state");
                snapshot = null; UpdateDisplay(); if (primaryValue.Text != "—") throw new Exception("Missing data");
                SelectPage(true); notificationFilter = NotificationFilter.WeChat; UpdateNotificationList();
                if (notificationPage.Visibility != Visibility.Visible || statusPage.Visibility != Visibility.Collapsed || notificationFeed.GetItems(NotificationFilter.WeChat).Count != 1) throw new Exception("Notification tab/filter");
                settings.HideNotificationBody = true; UpdateNotificationList(); ShowPeek(notificationFeed.Items.First());
                if (!peeking || peekPanel.Visibility != Visibility.Visible || peekTitle.Text != "收到一条新通知" || peekBody.Text != "消息内容已隐藏") throw new Exception("Privacy preview");
                SetExpanded(false, false); if (peeking || peekPanel.Visibility != Visibility.Collapsed || compact.Visibility != Visibility.Visible) throw new Exception("Preview collapse");
                notificationFeed.MarkAllRead(); UpdateNotificationList(); if (notificationFeed.UnreadCount != 0) throw new Exception("Local read state");
                notificationFeed.Clear(); UpdateNotificationList(); if (notificationFeed.Items.Count != 0) throw new Exception("Clear local history");
                SelectToolsPage(); SetExpanded(true, false);
                foreach (string key in toolPanels.Keys) { SelectTool(key); UpdateLayout(); if (toolPanels[key].Visibility != Visibility.Visible || toolPanels.Count(item => item.Value.Visibility == Visibility.Visible) != 1) throw new Exception("Tool selection " + key); }
                toolSearch.Text = "换算"; if (toolButtons["converter"].Visibility != Visibility.Visible || toolButtons.Values.Count(button => button.Visibility == Visibility.Visible) != 1) throw new Exception("Tool search");
                toolSearch.Text = "not-a-tool"; if (noToolMatches.Visibility != Visibility.Visible) throw new Exception("Empty tool search"); toolSearch.Clear();
                settings.HiddenTools = "system"; ConfigureToolkit(); if (toolButtons["system"].Visibility != Visibility.Collapsed || selectedTool == "system") throw new Exception("Hidden module");
                settings.HiddenTools = String.Join(",", toolPanels.Keys); ConfigureToolkit(); if (emptyTools.Visibility != Visibility.Visible) throw new Exception("All modules hidden");
                settings.HiddenTools = ""; ConfigureToolkit();
                settings.CompactMode = "focus"; UpdateToolkitCompact(); if (compactAlternate.Visibility != Visibility.Visible || primaryRing.Visibility != Visibility.Collapsed) throw new Exception("Focus compact");
                settings.CompactMode = "clock"; UpdateToolkitCompact(); if (compactAlternateMain.FontSize != 22 || !compactAlternateMain.Text.Contains("\n")) throw new Exception("Clock compact");
                settings.CompactMode = "quota"; UpdateToolkitCompact(); if (primaryRing.Visibility != Visibility.Visible || compactAlternate.Visibility != Visibility.Collapsed) throw new Exception("Quota compact");
                ShowPeek(new IslandNotification { AppId = "sideisland.timer", AppName = "专注计时", Title = "测试", Body = "演示提醒" }); OpenPeekTarget();
                if (!toolsPageSelected || selectedTool != "focus") throw new Exception("Timer preview target");
                ShowPeek(new IslandNotification { AppId = "sideisland.reminders", AppName = "提醒闹钟", Title = "测试", Body = "演示提醒" }); OpenPeekTarget();
                if (!toolsPageSelected || selectedTool != "reminders") throw new Exception("Reminder preview target");
                RunInteractionSmoke();
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "smoke-result.txt"), "PASS: nearest four-edge snap, horizontal/vertical compact layouts, inward expansion, retained anchors, text rotation/mirroring, free placement, drag guards, eight resize handles, saved size, source blocking and tool panels");
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "smoke-result.txt"), "FAIL: " + ex.Message); Environment.ExitCode = 1; }
            Close();
        }
    }
}
