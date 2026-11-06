using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using SideIsland.Core;

namespace SideIsland.Desktop;

internal sealed record ScreenPinSaveSources
{
    public Func<CancellationToken, Task<string?>>? ChooseDestination { get; init; }
    public Func<Bitmap, string, CancellationToken, Task>? SavePng { get; init; }
}

/// <summary>Small managed sizing model shared by the pin UI and behavior tests.</summary>
public sealed class ScreenPinState
{
    public double Zoom { get; private set; } = 1;
    public double Alpha { get; private set; } = 1;
    public void SetZoom(double value) => Zoom = double.IsFinite(value) ? Math.Clamp(value, .05, 4) : 1;
    public void ZoomBy(double wheelDelta) { if (double.IsFinite(wheelDelta)) SetZoom(Zoom * Math.Pow(1.12, Math.Clamp(wheelDelta, -20, 20))); }
    public void SetAlpha(double value) => Alpha = double.IsFinite(value) ? Math.Clamp(value, .2, 1) : 1;
    public static double FitZoom(double imageWidth, double imageHeight, double availableWidth, double availableHeight)
    {
        if (!double.IsFinite(imageWidth) || !double.IsFinite(imageHeight) || !double.IsFinite(availableWidth) || !double.IsFinite(availableHeight) || imageWidth <= 0 || imageHeight <= 0) return 1;
        return Math.Clamp(Math.Min(1, Math.Min(Math.Max(1, availableWidth) / imageWidth, Math.Max(1, availableHeight) / imageHeight)), .05, 1);
    }
}

/// <summary>Owns its decoded bitmap. Saving always writes a separate PNG.</summary>
public sealed class ScreenPinWindow : Window
{
    private readonly Bitmap bitmap;
    private readonly Image image;
    private readonly TextBlock zoomLabel, message;
    private readonly ScreenPinState state = new();
    private readonly bool english, testMode;
    private readonly ScreenPinSaveSources? sources;
    private readonly string? sourcePath;
    private readonly CancellationTokenSource saveLifetime = new();
    private readonly Button saveButton;
    private bool released, saving;
    public ScreenPinState PinState => state;
    internal Task LatestSave { get; private set; } = Task.CompletedTask;
    private string T(string zh, string en) => english ? en : zh;

    public ScreenPinWindow(Bitmap bitmap, string language = "zh", bool lightTheme = false, bool testMode = false) : this(bitmap, language, lightTheme, testMode, null, null) { }
    internal ScreenPinWindow(Bitmap bitmap, string language, bool lightTheme, bool testMode, ScreenPinSaveSources? sources, string? sourcePath)
    {
        this.bitmap = bitmap ?? throw new ArgumentNullException(nameof(bitmap));
        this.sources = sources; this.sourcePath = sourcePath is null ? null : Path.GetFullPath(sourcePath);
        english = language == "en"; this.testMode = testMode;
        Title = T("截图钉屏", "Pinned image");
        WindowDecorations = WindowDecorations.None; CanResize = true; Topmost = true;
        ShowInTaskbar = true; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.Transparent; TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        RequestedThemeVariant = lightTheme ? ThemeVariant.Light : ThemeVariant.Dark;
        MinWidth = 250; MinHeight = 190; Width = 520; Height = 430;
        IBrush foreground = new SolidColorBrush(Color.Parse(lightTheme ? "#1C1C1E" : "#F5F5F7"));
        TextBlock Label(string text, int size = 12) => new() { Text = text, FontSize = size, Foreground = foreground, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        Button Action(string text, Action action)
        {
            var button = new Button { Content = Label(text), MinHeight = 30, HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(9, 4), CornerRadius = new CornerRadius(9) };
            button.Click += (_, _) => action(); return button;
        }
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto"), RowSpacing = 6 };
        var title = Label(Title, 13); title.FontWeight = FontWeight.SemiBold;
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, Background = Brushes.Transparent };
        header.Children.Add(title); var close = Action("×", Close); Grid.SetColumn(close, 1); header.Children.Add(close); root.Children.Add(header);
        header.PointerPressed += (_, e) => { if ((ReferenceEquals(e.Source, header) || ReferenceEquals(e.Source, title)) && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { BeginMoveDrag(e); e.Handled = true; } };
        var tools = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,*"), ColumnSpacing = 6 };
        zoomLabel = Label("100%"); zoomLabel.TextAlignment = TextAlignment.Center;
        var minus = Action("−", () => ChangeZoom(-1)); var plus = Action("+", () => ChangeZoom(1));
        saveButton = Action(T("另存 PNG", "Save PNG"), RequestSave); saveButton.Name = "pin-save";
        tools.Children.Add(minus); Grid.SetColumn(zoomLabel, 1); tools.Children.Add(zoomLabel); Grid.SetColumn(plus, 2); tools.Children.Add(plus); Grid.SetColumn(saveButton, 3); tools.Children.Add(saveButton); Grid.SetRow(tools, 1); root.Children.Add(tools);
        image = new Image { Source = bitmap, Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var view = new ScrollViewer { Content = image, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        var imageFrame = new Border { Child = view, CornerRadius = new CornerRadius(10), ClipToBounds = true, Background = new SolidColorBrush(Color.Parse(lightTheme ? "#E5E5EA" : "#09090B")) };
        Grid.SetRow(imageFrame, 2); root.Children.Add(imageFrame);
        image.PointerPressed += (_, e) => { if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { BeginMoveDrag(e); e.Handled = true; } };
        imageFrame.PointerWheelChanged += (_, e) => { ChangeZoom(e.Delta.Y); e.Handled = true; };
        var opacity = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8 };
        opacity.Children.Add(Label(T("透明度", "Opacity"), 11));
        var alpha = new Slider { Minimum = .2, Maximum = 1, Value = 1, MinWidth = 48 };
        Grid.SetColumn(alpha, 1); opacity.Children.Add(alpha);
        var percentage = Label("100%", 11); percentage.MinWidth = 36; percentage.TextAlignment = TextAlignment.Right; Grid.SetColumn(percentage, 2); opacity.Children.Add(percentage);
        alpha.ValueChanged += (_, _) => { state.SetAlpha(alpha.Value); Opacity = state.Alpha; percentage.Text = (state.Alpha * 100).ToString("0") + "%"; };
        Grid.SetRow(opacity, 3); root.Children.Add(opacity);
        message = Label(T("拖动图片移动 · 滚轮缩放 · Esc 关闭", "Drag to move · Scroll to zoom · Esc to close"), 10); message.Opacity = .65;
        var messageView = new ScrollViewer { Name = "pin-message-scroll", Content = message, MaxHeight = 28, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        Grid.SetRow(messageView, 4); root.Children.Add(messageView);
        SizeChanged += (_, e) => messageView.MaxHeight = Math.Clamp(e.NewSize.Height - 162, 28, 96);
        Content = new Border { Child = root, Padding = new Thickness(10), CornerRadius = new CornerRadius(16), ClipToBounds = true, Background = new SolidColorBrush(Color.Parse(lightTheme ? "#FAFAFC" : "#1C1C1E")), BorderBrush = new SolidColorBrush(Color.Parse(lightTheme ? "#DCDCE2" : "#343438")), BorderThickness = new Thickness(1) };
        var context = new ContextMenu();
        foreach (var item in new[] { (T("适合窗口", "Fit image"), new Action(FitImage)), (T("原始大小", "Actual size"), new Action(() => { state.SetZoom(1); ApplyZoom(); })), (T("另存 PNG", "Save PNG"), new Action(RequestSave)), (T("关闭", "Close"), new Action(Close)) })
        { var entry = new MenuItem { Header = item.Item1 }; entry.Click += (_, _) => item.Item2(); context.Items.Add(entry); }
        imageFrame.ContextMenu = context;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            else if (e.Key == Key.S && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0) { RequestSave(); e.Handled = true; }
            else if (e.Key == Key.D0 || e.Key == Key.NumPad0) { FitImage(); e.Handled = true; }
            else if (e.Key is Key.Add or Key.OemPlus) { ChangeZoom(1); e.Handled = true; }
            else if (e.Key is Key.Subtract or Key.OemMinus) { ChangeZoom(-1); e.Handled = true; }
        };
        Opened += (_, _) =>
        {
            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            double availableWidth = screen is null ? 720 : Math.Max(250, screen.WorkingArea.Width / screen.Scaling - 48);
            double availableHeight = screen is null ? 600 : Math.Max(190, screen.WorkingArea.Height / screen.Scaling - 48);
            state.SetZoom(ScreenPinState.FitZoom(bitmap.Size.Width, bitmap.Size.Height, Math.Min(900, availableWidth) - 24, Math.Min(720, availableHeight) - 150));
            Width = Math.Min(availableWidth, Math.Max(250, bitmap.Size.Width * state.Zoom + 24));
            Height = Math.Min(availableHeight, Math.Max(190, bitmap.Size.Height * state.Zoom + 150));
            ApplyZoom();
        };
        Closed += (_, _) => ReleaseImage();
        ApplyZoom();
    }

    public void ChangeZoom(double delta) { state.ZoomBy(delta); ApplyZoom(); }
    public void FitImage() { state.SetZoom(ScreenPinState.FitZoom(bitmap.Size.Width, bitmap.Size.Height, Math.Max(1, Bounds.Width - 24), Math.Max(1, Bounds.Height - 150))); ApplyZoom(); }
    internal void ReleaseImage() { if (released) return; released = true; saveLifetime.Cancel(); image.Source = null; saveButton.IsEnabled = false; if (!saving) { bitmap.Dispose(); saveLifetime.Dispose(); } }
    private void ApplyZoom() { if (released) return; image.Width = bitmap.Size.Width * state.Zoom; image.Height = bitmap.Size.Height * state.Zoom; zoomLabel.Text = (state.Zoom * 100).ToString("0") + "%"; }
    private void RequestSave() { if (!saving && !released) LatestSave = SaveAsync(); }
    private async Task SaveAsync()
    {
        if ((testMode && sources?.ChooseDestination is null) || saving || released) return;
        saving = true; saveButton.IsEnabled = false;
        try
        {
            string? destination;
            if (sources?.ChooseDestination is { } choose) destination = await choose(saveLifetime.Token);
            else
            {
                using var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = T("另存新的 PNG", "Save a new PNG"), SuggestedFileName = "side-island-pin.png", DefaultExtension = "png", ShowOverwritePrompt = false, FileTypeChoices = [new FilePickerFileType("PNG") { Patterns = ["*.png"] }] });
                if (file is null) return;
                destination = file.TryGetLocalPath() ?? throw new IOException(T("请选择本地的新 PNG 文件。", "Choose a new local PNG file."));
            }
            if (destination is null || released) return;
            saveLifetime.Token.ThrowIfCancellationRequested();
            string fullPath = Path.GetFullPath(destination);
            if (sourcePath is not null && string.Equals(fullPath, sourcePath, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
            { message.Text = T("请选择新文件名，不能覆盖原图片。", "Choose a new filename to keep the original image unchanged."); return; }
            if (sources?.SavePng is { } save) await save(bitmap, fullPath, saveLifetime.Token);
            else await Task.Run(() => ScreenPinImageFiles.SaveNew(bitmap, fullPath, saveLifetime.Token), saveLifetime.Token);
            if (!released) message.Text = T("已另存新的 PNG", "Saved a new PNG");
        }
        catch (OperationCanceledException) { }
        catch (FilePublicationException exception) when (exception.State == FilePublicationState.OutcomeUnknown)
        {
            if (!testMode) DiagnosticLog.Write("pin-export-outcome-unknown", exception);
            if (!released) message.Text = FilePublicationFeedback.Unknown(exception, english ? "en" : "zh");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException) { if (!released) message.Text = T("保存失败：", "Save failed: ") + exception.Message; }
        finally { saving = false; if (released) { bitmap.Dispose(); saveLifetime.Dispose(); } else saveButton.IsEnabled = true; }
    }
}
