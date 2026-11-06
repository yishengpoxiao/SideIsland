using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using SideIsland.Core;
using SideIsland.Desktop.Platforms;
using CodexIsland;

namespace SideIsland.Desktop;

public sealed partial class IslandWindow : Window
{
    private readonly PreferenceStore store;
    private readonly Preferences prefs;
    private readonly FocusTimer focus = new();
    private readonly TaskActivityHub tasks = new();
    private readonly TaskActivityApi taskApi;
    private readonly CommandPlatformService platform = new();
    private readonly IntegrationSources integrations;
    private readonly bool injectedIntegrations;
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer pulse = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer hover = new(), collapse = new(), saveDebounce = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly Dictionary<string, string> drafts = new();
    private Border shell = null!, compactHost = null!;
    private Grid expandedHost = null!;
    private TextBlock slotOne = null!, slotTwo = null!, liveStatus = null!, focusTime = null!, mediaTitle = null!, mediaArtist = null!, quotaText = null!;
    private StackPanel body = null!, taskRows = null!;
    private ScrollViewer scroll = null!;
    private Button[] tabs = [];
    private ComboBox picker = null!;
    private string page = "tools", selected;
    private bool expanded, pinned, closing, dragging, readingMedia, readingQuota, placementBusy, rebuilding;
    private bool realNotch;
    private int tasksDirty;
    private MacNotchPlacement? notchPlacement;
    private DateTime nextMedia, nextQuota, awakeUntil;
    private MediaSnapshot? media;
    private UsageSnapshot? quota;
    private IQuotaSession? codex;
    private string status = "";
    private readonly bool testMode;
    public bool IsExpanded => expanded;
    public Preferences Settings => prefs;
    public bool UsesPhysicalNotch => realNotch;
    internal static string ApplicationVersion => typeof(IslandWindow).Assembly.GetName().Version?.ToString(3) ?? "unknown";
    public IslandWindow(bool testMode = false, string? dataDirectory = null) : this(testMode, dataDirectory, null) { }
    internal IslandWindow(bool testMode, string? dataDirectory, IntegrationSources? sources)
    {
        this.testMode = testMode; store = new(dataDirectory); prefs = store.Load(); selected = prefs.LastTool;
        injectedIntegrations = sources is not null;
        integrations = sources ?? new(platform.ReadMediaAsync, platform.ControlMediaAsync, (executable, home) => new CodexQuotaSession(executable, home));
        taskApi = new(tasks);
        InitializeDragHandlers();
        Title = $"Side Island {ApplicationVersion} · macOS / Ubuntu preview";
        WindowDecorations = WindowDecorations.None; CanResize = false; ShowInTaskbar = true;
        Background = Brushes.Transparent; TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        MinWidth = 0; MinHeight = 0; Width = 76; Height = 218; Topmost = prefs.AlwaysOnTop;
        RequestedThemeVariant = prefs.LightTheme ? ThemeVariant.Light : ThemeVariant.Dark;
        hover.Tick += (_, _) => { hover.Stop(); if (!closing && dragOrigin is null && !dragging && !expanded) Expand(); };
        collapse.Interval = TimeSpan.FromMilliseconds(220); collapse.Tick += (_, _) => { collapse.Stop(); if (!pinned && !dragging && !IsPointerOver && !PopupOpen()) Collapse(); };
        saveDebounce.Tick += (_, _) => { saveDebounce.Stop(); Save(); };
        PointerEntered += (_, _) => { collapse.Stop(); if (!closing && dragOrigin is null && !expanded && prefs.HoverExpand && !dragging) { hover.Interval = TimeSpan.FromMilliseconds(prefs.HoverDelayMs); hover.Start(); } };
        PointerExited += (_, _) => { hover.Stop(); if (expanded && !pinned) collapse.Start(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { pinned = false; Collapse(); e.Handled = true; } };
        Opened += (_, _) => { BuildShell(); Place(); if (!testMode) pulse.Start(); };
        Screens.Changed += OnScreensChanged;
        Activated += (_, _) => { if (!closing) Place(); };
        PositionChanged += (_, _) => { if (dragging) hover.Stop(); };
        SizeChanged += (_, _) => { if (!placementBusy && expanded && !realNotch && IsVisible) { prefs.ExpandedWidth = Bounds.Width; prefs.ExpandedHeight = Bounds.Height; prefs.Normalize(); QueueSave(); } };
        Closing += (_, e) => { if (!TryPrepareBatchClose()) { e.Cancel = true; return; } closing = true; Screens.Changed -= OnScreensChanged; pulse.Stop(); hover.Stop(); collapse.Stop(); saveDebounce.Stop(); Save(); StopMediaRequests(); StopQuotaRequests(); lifetime.Cancel(); DisposeNotifications(); batchEngine?.Dispose(); taskApi.Dispose(); platform.Dispose(); };
        pulse.Tick += async (_, _) => await TickAsync();
        focus.SetCountdown(TimeSpan.FromMinutes(prefs.FocusMinutes), "Focus", true);
        focus.Completed += () => Notice(T("专注已完成", "Focus completed"), T("休息一下，再开始下一轮。", "Take a break before the next session."));
        tasks.Changed += () => Interlocked.Exchange(ref tasksDirty, 1);
        if (prefs.Slot1 == "unread" || prefs.Slot2 == "unread") EnsureNotifications();
        BuildShell();
    }
    internal static readonly (string Id, string Zh, string En)[] Tools =
    [
        ("focus", "专注计时", "Focus timer"), ("media", "正在播放", "Now playing"), ("quota", "Codex 额度", "Codex usage"),
        ("ports", "端口占用", "Port monitor"), ("notes", "便笺", "Notes"), ("clipboard", "剪贴板", "Clipboard"),
        ("diff", "文字对照", "Text comparison"), ("text", "文字工具", "Text tools"), ("qr", "二维码传话", "QR transfer"),
        ("calculator", "计算器", "Calculator"), ("random", "密码与随机", "Password / random"),
        ("hash", "文件校验", "File checksums"), ("screen-pin", "截图钉屏", "Screenshot pin"), ("batch-files", "批量文件", "Batch files"), ("batch-images", "批量图片", "Batch images"),
        ("notifications", "通知接入与规则", "Notification sources & rules"), ("watch", "任务守望", "Task watch"), ("awake", "保持唤醒", "Keep awake"),
        ("volume", "系统音量", "System volume"), ("activities", "任务动态", "Task activities"), ("settings", "布局与平台设置", "Layout & platform")
    ];
    private string T(string zh, string en) => prefs.Language == "en" ? en : zh;
    private IBrush CardBrush => new SolidColorBrush(prefs.LightTheme ? Color.Parse("#F2F2F7") : Color.Parse("#232326"));
    private IBrush TextBrush => new SolidColorBrush(prefs.LightTheme ? Color.Parse("#1C1C1E") : Color.Parse("#F5F5F7"));
    private TextBlock Text(string value, double size = 13) => new() { Text = value, FontSize = size, Foreground = TextBrush, TextWrapping = TextWrapping.Wrap };
    private TextBlock Hint(string value) { var t = Text(value, 11); t.Opacity = .65; return t; }
    private Border Card(Control child) => new() { Child = child, CornerRadius = new(14), Padding = new(12), Margin = new(0, 0, 0, 8), Background = CardBrush };
    private Button Button(string caption, Action action) { var b = new Button { Content = Text(caption, 12), HorizontalAlignment = HorizontalAlignment.Stretch }; b.Click += (_, _) => { try { action(); } catch (Exception e) { Report(e.Message); } }; return b; }
    private Button AsyncButton(string caption, Func<Task> action, Func<bool>? available = null)
    {
        var b = new Button { Content = Text(caption, 12), HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = available?.Invoke() ?? true };
        b.Click += async (_, _) => { if (closing || !b.IsEnabled || available?.Invoke() == false) return; b.IsEnabled = false; try { await action(); } catch (OperationCanceledException) { } catch (Exception e) { if (!closing) Report(e.Message); } finally { if (!closing) b.IsEnabled = available?.Invoke() ?? true; } }; return b;
    }
    private Control Actions(params Control[] controls)
    {
        var grid = new Grid { ColumnSpacing = 6, RowSpacing = 6, Margin = new(0, 6, 0, 6) };
        for (int i = 0; i < controls.Length; i++) { grid.ColumnDefinitions.Add(new(GridLength.Star)); Grid.SetColumn(controls[i], i); grid.Children.Add(controls[i]); }
        return grid;
    }
    private TextBox Input(string key, string initial = "", bool multiline = false)
    {
        var input = new TextBox { Text = drafts.GetValueOrDefault(key, initial), AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, MinHeight = multiline ? 110 : 34, MaxLength = multiline ? 120000 : 4096, Margin = new(0, 0, 0, 6) };
        input.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) drafts[key] = input.Text ?? ""; }; return input;
    }
    private void BuildShell()
    {
        rebuilding = true; UiLocalization.Language = prefs.Language;
        RequestedThemeVariant = prefs.LightTheme ? ThemeVariant.Light : ThemeVariant.Dark;
        shell = new Border { Background = new SolidColorBrush(Color.Parse("#080809")), CornerRadius = new(26), ClipToBounds = true };
        var layer = new Grid(); compactHost = new Border(); expandedHost = new Grid { RowDefinitions = new("Auto,Auto,*,Auto"), Background = prefs.LightTheme ? Brushes.White : new SolidColorBrush(Color.Parse("#141416")) };
        BuildCompact();
        var header = new Grid { ColumnDefinitions = new("*,Auto,Auto,Auto"), ColumnSpacing = 6, Margin = new(16, 10, 16, 8), MinHeight = 32, Background = Brushes.Transparent };
        var name = Text("Side Island", 16); name.FontWeight = FontWeight.SemiBold; name.VerticalAlignment = VerticalAlignment.Center; header.Children.Add(name);
        var language = Button(prefs.Language == "en" ? "中" : "EN", () => { prefs.Language = prefs.Language == "en" ? "zh" : "en"; Save(); Rebuild(); }); Grid.SetColumn(language, 1); header.Children.Add(language);
        var theme = Button(prefs.LightTheme ? "☾" : "☼", () => { prefs.LightTheme = !prefs.LightTheme; Save(); Rebuild(); }); Grid.SetColumn(theme, 2); header.Children.Add(theme);
        var less = Button("−", () => { pinned = false; Collapse(); }); Grid.SetColumn(less, 3); header.Children.Add(less);
        AttachIslandDrag(header, null, source => ReferenceEquals(source, name) || ReferenceEquals(source, header));
        expandedHost.Children.Add(header);
        tabs = [Button(T("消息", "Messages"), () => SelectPage("messages")), Button(T("状态", "Status"), () => SelectPage("status")), Button(T("工具", "Tools"), () => SelectPage("tools"))];
        var nav = Actions(tabs); nav.Margin = new(14, 0, 14, 8); Grid.SetRow(nav, 1); expandedHost.Children.Add(nav);
        body = new StackPanel { Spacing = 2, Margin = new(0, 0, 6, 0) }; scroll = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new(14, 0, 8, 0) }; Grid.SetRow(scroll, 2); expandedHost.Children.Add(scroll);
        liveStatus = Hint(status.Length == 0 ? T("Mac 刘海优先 · Ubuntu 四边停靠", "Mac notch first · Ubuntu edge docking") : status); liveStatus.Margin = new(16, 8, 16, 10); Grid.SetRow(liveStatus, 3); expandedHost.Children.Add(liveStatus);
        layer.Children.Add(compactHost); layer.Children.Add(expandedHost); shell.Child = layer; Content = shell;
        var menu = new ContextMenu();
        foreach (var item in new[] { (T("展开 / 收起", "Expand / collapse"), new Action(() => { if (expanded) Collapse(); else Expand(); })), (T("固定展开", "Pin open"), new Action(() => { pinned = !pinned; Expand(); })), (T("恢复刘海 / 顶边位置", "Restore notch / top position"), new Action(() => { prefs.PreferNotch = true; prefs.Dock = OperatingSystem.IsMacOS() ? "Top" : "Auto"; Save(); Place(); })), (T("退出", "Quit"), new Action(Close)) }) { var m = new MenuItem { Header = item.Item1 }; m.Click += (_, _) => item.Item2(); menu.Items.Add(m); }
        shell.ContextMenu = menu; menu.Opened += (_, _) => { hover.Stop(); collapse.Stop(); }; menu.Closed += (_, _) => { if (expanded && !pinned && !IsPointerOver) collapse.Start(); };
        RenderPage(); SetVisibility(); rebuilding = false;
    }
    private void Rebuild() { BuildShell(); Place(); RefreshCompact(); }
    private void OnScreensChanged(object? sender, EventArgs e) { if (!closing) Place(); }
    private void BuildCompact()
    {
        var grid = new Grid { Margin = new(10, 8), RowSpacing = 8, ColumnSpacing = 8 };
        slotOne = new TextBlock { Foreground = Brushes.White, FontSize = 14, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxLines = 2 };
        slotTwo = new TextBlock { Foreground = Brushes.White, FontSize = 14, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxLines = 2 };
        var left = new Border { Child = slotOne, Background = Brushes.Transparent };
        var right = new Border { Child = slotTwo, Background = Brushes.Transparent };
        grid.Children.Add(left); grid.Children.Add(right); compactHost.Child = grid;
        foreach (var (target, index) in new[] { (left, 0), (right, 1) })
        {
            AttachIslandDrag(target, index);
        }
        RefreshCompact();
    }
    private void UpdateCompactLayout()
    {
        var grid = (Grid)compactHost.Child!; grid.ColumnDefinitions.Clear(); grid.RowDefinitions.Clear();
        var horizontal = realNotch || prefs.Dock is "Top" or "Bottom" || Width > Height;
        grid.Margin = realNotch ? new Thickness(10, 3) : new Thickness(10, 8);
        if (horizontal)
        {
            grid.ColumnDefinitions.Add(realNotch && notchPlacement is not null ? new(new GridLength(Math.Max(0, notchPlacement.NotchLeft - grid.Margin.Left - grid.ColumnSpacing))) : new(GridLength.Star));
            grid.ColumnDefinitions.Add(new(new GridLength(realNotch ? notchPlacement?.NotchWidth ?? 0 : 12)));
            grid.ColumnDefinitions.Add(new(GridLength.Star));
            Grid.SetColumn(grid.Children[0], 0); Grid.SetColumn(grid.Children[1], 2); Grid.SetRow(grid.Children[0], 0); Grid.SetRow(grid.Children[1], 0);
        }
        else { grid.RowDefinitions.Add(new(GridLength.Star)); grid.RowDefinitions.Add(new(GridLength.Star)); Grid.SetColumn(grid.Children[0], 0); Grid.SetColumn(grid.Children[1], 0); Grid.SetRow(grid.Children[0], 0); Grid.SetRow(grid.Children[1], 1); }
        slotOne.FontSize = slotTwo.FontSize = horizontal ? 12 : 15;
        slotOne.Text = Widget(prefs.Slot1); slotTwo.Text = Widget(prefs.Slot2);
    }
    public void Expand() { if (expanded) return; expanded = true; hover.Stop(); collapse.Stop(); SetVisibility(); Place(); body.Opacity = 0; Dispatcher.UIThread.Post(() => body.Opacity = 1, DispatcherPriority.Render); }
    public void Collapse() { if (!expanded) return; expanded = false; hover.Stop(); collapse.Stop(); SetVisibility(); Place(); }
    private void SetVisibility() { compactHost.IsVisible = !expanded; expandedHost.IsVisible = expanded; CanResize = expanded && !realNotch; }
    private void SelectPage(string value) { page = value; RenderPage(); }
    internal void SelectToolForTest(string value) { selected = value; page = "tools"; RenderPage(); }
    private void RenderPage()
    {
        if (body is null) return; body.Children.Clear();
        foreach (var b in tabs) b.Background = Brushes.Transparent;
        tabs[page == "messages" ? 0 : page == "status" ? 1 : 2].Background = CardBrush;
        if (page == "messages") { RenderMessages(); return; }
        if (page == "status") { RenderStatus(); return; }
        if (!Tools.Any(t => t.Id == selected)) selected = "focus";
        picker = new ComboBox { ItemsSource = Tools.Select(t => T(t.Zh, t.En)).ToArray(), SelectedIndex = Array.FindIndex(Tools, t => t.Id == selected), Margin = new(0, 0, 0, 10) };
        picker.SelectionChanged += (_, _) => { if (rebuilding || picker.SelectedIndex < 0) return; selected = Tools[picker.SelectedIndex].Id; prefs.LastTool = selected; QueueSave(); RenderPage(); }; body.Children.Add(picker);
        var selectedTitle = Tools.First(t => t.Id == selected); var heading = Text(T(selectedTitle.Zh, selectedTitle.En), 18); heading.FontWeight = FontWeight.SemiBold; heading.Margin = new(0, 0, 0, 8); body.Children.Add(heading);
        RenderTool(); scroll.Offset = default;
        body.Transitions = new Transitions { new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(140), Easing = new CubicEaseOut() } };
    }
    private void Place()
    {
        if (closing || placementBusy || dragging || !IsVisible) return; placementBusy = true;
        try
        {
            var edge = Enum.TryParse<DockEdge>(prefs.Dock, out var parsed) ? parsed : DockEdge.Auto;
            if (OperatingSystem.IsMacOS() && prefs.AlwaysOnTop && prefs.PreferNotch && edge is DockEdge.Auto or DockEdge.Top && MacNotchBridge.TryApply(this, expanded, true, prefs.AcrossSpaces, prefs.OverFullscreen, prefs.ExpandedWidth, prefs.ExpandedHeight, out var native, out var error))
            {
                realNotch = true; notchPlacement = native;
                shell.CornerRadius = new(0, 0, 24, 24);
                expandedHost.Margin = new(0, native.ContentTop, 0, 0); CanResize = false;
            }
            else
            {
                if (OperatingSystem.IsMacOS()) MacNotchBridge.TryRestoreWindowPolicy(this, prefs.AlwaysOnTop, out _);
                realNotch = false; notchPlacement = null; expandedHost.Margin = default; shell.CornerRadius = new(26);
                var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary ?? Screens.All.FirstOrDefault(); if (screen is null) return;
                double scale = screen.Scaling;
                var bounds = new RectD(screen.Bounds.X / scale, screen.Bounds.Y / scale, screen.Bounds.Width / scale, screen.Bounds.Height / scale);
                var work = new RectD(screen.WorkingArea.X / scale, screen.WorkingArea.Y / scale, screen.WorkingArea.Width / scale, screen.WorkingArea.Height / scale);
                var target = PlacementPolicy.Place(new("current", bounds, work, scale, screen.IsPrimary), OperatingSystem.IsMacOS() && edge == DockEdge.Auto ? DockEdge.Top : edge, false, expanded, prefs.ExpandedWidth, prefs.ExpandedHeight, prefs.AlongEdge);
                Width = target.Bounds.Width; Height = target.Bounds.Height; Position = new((int)Math.Round(target.Bounds.X * scale), (int)Math.Round(target.Bounds.Y * scale)); CanResize = expanded;
            }
            UpdateCompactLayout();
        }
        finally { placementBusy = false; }
    }
    private void Snap()
    {
        if (realNotch) return; var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary; if (screen is null) return;
        var area = screen.WorkingArea; var edge = PlacementPolicy.NearestEdge(new(area.X, area.Y, area.Width, area.Height), Position.X + Bounds.Width * screen.Scaling / 2, Position.Y + Bounds.Height * screen.Scaling / 2);
        prefs.Dock = edge.ToString(); prefs.AlongEdge = edge is DockEdge.Left or DockEdge.Right ? (Position.Y + Bounds.Height * screen.Scaling / 2 - area.Y) / area.Height : (Position.X + Bounds.Width * screen.Scaling / 2 - area.X) / area.Width; prefs.Normalize(); Save(); Place();
    }
    private bool PopupOpen() => shell.ContextMenu?.IsOpen == true || picker?.IsDropDownOpen == true;
    private void Save() { try { store.Save(prefs); } catch (Exception e) { status = T("偏好保存失败：", "Could not save: ") + e.Message; } }
    private void QueueSave() { saveDebounce.Stop(); saveDebounce.Start(); }
    private void Report(string value) { status = value; if (liveStatus is not null) liveStatus.Text = value; }
    private void Notice(string title, string message) { PublishInternalNotification(title, message); if (page == "messages") RefreshNotificationRows(); }
    private string Widget(string key) => key switch
    {
        "clock" => DateTime.Now.ToString("HH:mm"), "focus" => FocusTimer.Format(focus.Value, false) + "\n" + T(focus.IsRunning ? "专注中" : "点击开始", focus.IsRunning ? "Focusing" : "Tap to start"),
        "codex-short" => QuotaValue(false), "codex-week" => QuotaValue(true), "music" => media?.Available == true ? (media.Playing ? "♫ " : "Ⅱ ") + media.Title : T("音乐", "Music"),
        "tasks" => tasks.Current is { } task ? task.Progress.HasValue ? task.Progress.Value.ToString("0") + "%" : "◉ " + task.Title : T("无任务", "No tasks"),
        "unread" => (notificationCenter?.UnreadCount ?? 0) + "\n" + T("未读消息", "Unread"),
        "awake" => awakeUntil > DateTime.UtcNow ? "☕ " + FocusTimer.Format(awakeUntil - DateTime.UtcNow, false) : T("保持唤醒", "Keep awake"), _ => "Side Island"
    };
    private string QuotaValue(bool week)
    {
        var bucket = quota?.Buckets.FirstOrDefault(); var value = week ? bucket?.Secondary : bucket?.Primary;
        return value?.Remaining is double remaining ? remaining.ToString("0") + "%\n" + T(week ? "每周额度" : "短期额度", week ? "Weekly" : "Short term") : "—\n" + T(week ? "每周额度" : "短期额度", week ? "Weekly" : "Short term");
    }
    private void RefreshCompact() { if (slotOne is null) return; slotOne.Text = Widget(prefs.Slot1); slotTwo.Text = Widget(prefs.Slot2); }
    private void QuickAction(string key)
    {
        if (key == "focus") { if (focus.IsRunning) focus.Pause(); else { if (focus.IsCompleted) focus.Reset(); focus.Start(); } RefreshCompact(); }
        else if (key == "music" && prefs.MediaEnabled) _ = MediaAction("play-pause");
        else if (key == "unread") { SelectPage("messages"); Expand(); }
        else { selected = key is "codex-short" or "codex-week" ? "quota" : key == "clock" ? "settings" : key == "tasks" ? "activities" : key; page = "tools"; RenderPage(); Expand(); }
    }
    private async Task TickAsync()
    {
        focus.Poll(); RefreshCompact(); if (focusTime is not null) focusTime.Text = FocusTimer.Format(focus.Value, false);
        if (Interlocked.Exchange(ref tasksDirty, 0) != 0 && expanded && selected == "activities") RenderTaskRows();
        if (Interlocked.Exchange(ref notificationDirty, 0) != 0 && expanded && page == "messages") RefreshNotificationRows();
        PollWatches();
        PollAwakeState();
        if (!testMode && prefs.MediaEnabled && DateTime.UtcNow >= nextMedia && !readingMedia) { nextMedia = DateTime.UtcNow.AddSeconds(2); _ = ReadMedia(); }
        if (!testMode && prefs.CodexEnabled && DateTime.UtcNow >= nextQuota && !readingQuota) { nextQuota = DateTime.UtcNow.AddSeconds(60); _ = ReadQuota(); }
        await Task.CompletedTask;
    }
}
