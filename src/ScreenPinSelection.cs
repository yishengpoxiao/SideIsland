using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace CodexIsland
{
    // All selection coordinates are physical desktop pixels. One system-DPI-aware
    // overlay spans the virtual desktop, including monitors left of the primary.
    internal sealed class ScreenPinSelection : Window
    {
        private readonly Int32Rect desktop;
        private readonly Canvas canvas = new Canvas();
        private readonly System.Windows.Shapes.Path shade = new System.Windows.Shapes.Path { Fill = new SolidColorBrush(Color.FromArgb(92, 0, 0, 0)), IsHitTestVisible = false };
        private readonly Rectangle outline = new Rectangle { Stroke = new SolidColorBrush(Color.FromRgb(120, 191, 255)), StrokeThickness = 2, Fill = Brushes.Transparent, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        private readonly TextBlock size = new TextBlock { Foreground = Brushes.White, FontSize = 12, LineHeight = 17, TextWrapping = TextWrapping.Wrap, IsHitTestVisible = false };
        private readonly Border sizeCard;
        private readonly Border instructionCard;
        private readonly TaskCompletionSource<Int32Rect?> completion = new TaskCompletionSource<Int32Rect?>();
        private CancellationTokenRegistration registration;
        private Point start;
        private bool selecting, finished;
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);

        private ScreenPinSelection(Int32Rect bounds)
        {
            desktop = bounds; WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; Topmost = true; ShowInTaskbar = false; ResizeMode = ResizeMode.NoResize; Cursor = Cursors.Cross; Focusable = true;
            FontFamily = new FontFamily("Segoe UI"); FontSize = 12; UseLayoutRounding = true; SnapsToDevicePixels = true;
            Left = SystemParameters.VirtualScreenLeft; Top = SystemParameters.VirtualScreenTop; Width = Math.Max(1, SystemParameters.VirtualScreenWidth); Height = Math.Max(1, SystemParameters.VirtualScreenHeight);
            Content = UiTheme.ProtectColor(canvas); canvas.Background = Brushes.Transparent; canvas.Children.Add(shade); canvas.Children.Add(outline);
            sizeCard = CaptionCard(size); canvas.Children.Add(sizeCard);
            var instructions = UiLocalization.Text(new TextBlock { Foreground = Brushes.White, FontSize = 12, LineHeight = 18, TextWrapping = TextWrapping.Wrap, IsHitTestVisible = false }, "拖动框选截图区域 · Esc 取消"); instructionCard = CaptionCard(instructions); instructionCard.Padding = new Thickness(10, 8, 10, 8); canvas.Children.Add(instructionCard);
            Canvas.SetTop(instructionCard, 16); Canvas.SetLeft(instructionCard, 16); sizeCard.Visibility = Visibility.Collapsed;
            SourceInitialized += (s, e) => { SetWindowPos(new WindowInteropHelper(this).Handle, new IntPtr(-1), desktop.X, desktop.Y, desktop.Width, desktop.Height, 0x0010); };
            Loaded += (s, e) => { UpdateSelection(Int32Rect.Empty); Activate(); Keyboard.Focus(this); };
            SizeChanged += (s, e) => UpdateSelection(Int32Rect.Empty);
            PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) { e.Handled = true; Finish(null); } };
            MouseLeftButtonDown += (s, e) => { try { start = ScreenPinTools.CursorPosition(); selecting = true; CaptureMouse(); UpdateSelection(Int32Rect.Empty); e.Handled = true; } catch { Finish(null); } };
            MouseMove += (s, e) => { if (!selecting) return; try { UpdateSelection(ScreenPinTools.Selection(start, ScreenPinTools.CursorPosition(), desktop)); } catch { Finish(null); } };
            MouseLeftButtonUp += (s, e) => {
                if (!selecting) return; selecting = false; ReleaseMouseCapture(); e.Handled = true;
                try { Int32Rect selected = ScreenPinTools.Selection(start, ScreenPinTools.CursorPosition(), desktop); if (selected.Width >= 2 && selected.Height >= 2) Finish(selected); else { UpdateSelection(Int32Rect.Empty); UiLocalization.Text(size, "区域太小，请重新拖选。"); sizeCard.Visibility = Visibility.Visible; PositionBadge(new Rect(16, 64, 0, 0), new Rect(0, 0, ActualWidth, ActualHeight)); } } catch { Finish(null); }
            };
            Closed += (s, e) => { registration.Dispose(); completion.TrySetResult(null); };
            AppAppearance.Register(this);
        }
        private static Border CaptionCard(TextBlock caption)
        {
            return new Border { Child = caption, Background = new SolidColorBrush(Color.FromArgb(242, 28, 28, 30)), BorderBrush = new SolidColorBrush(Color.FromArgb(170, 75, 75, 80)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(8, 5, 8, 5), IsHitTestVisible = false };
        }
        private void PositionBadge(Rect area, Rect whole)
        {
            sizeCard.MaxWidth = Math.Max(1, Math.Min(360, whole.Width - 16));
            // Caption changes can precede WPF's deferred invalidation of the
            // parent Border. Re-measure the current caption before edge clamping.
            size.InvalidateMeasure(); sizeCard.InvalidateMeasure(); sizeCard.Measure(new Size(sizeCard.MaxWidth, Double.PositiveInfinity));
            Size measured = sizeCard.DesiredSize; double top = area.Top - measured.Height - 6;
            if (top < 4) top = area.Bottom + 6;
            Canvas.SetLeft(sizeCard, Math.Max(4, Math.Min(Math.Max(4, whole.Width - measured.Width - 4), area.Left)));
            Canvas.SetTop(sizeCard, Math.Max(4, Math.Min(Math.Max(4, whole.Height - measured.Height - 4), top)));
        }
        private void UpdateSelection(Int32Rect selected)
        {
            double scaleX = ActualWidth > 0 ? ActualWidth / desktop.Width : Width / desktop.Width, scaleY = ActualHeight > 0 ? ActualHeight / desktop.Height : Height / desktop.Height;
            Rect whole = new Rect(0, 0, desktop.Width * scaleX, desktop.Height * scaleY);
            instructionCard.MaxWidth = Math.Max(1, Math.Min(520, whole.Width - 32));
            if (selected.IsEmpty) { shade.Data = new RectangleGeometry(whole); outline.Visibility = Visibility.Collapsed; sizeCard.Visibility = Visibility.Collapsed; return; }
            var area = ScreenPinTools.ToOverlay(selected, desktop, whole.Width, whole.Height);
            shade.Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(whole), new RectangleGeometry(area));
            Canvas.SetLeft(outline, area.X); Canvas.SetTop(outline, area.Y); outline.Width = area.Width; outline.Height = area.Height; outline.Visibility = Visibility.Visible;
            UiLocalization.Text(size, selected.Width + " × " + selected.Height); sizeCard.Visibility = Visibility.Visible; PositionBadge(area, whole);
        }
        private void Finish(Int32Rect? selection) { if (finished) return; finished = true; completion.TrySetResult(selection); Close(); }
        internal static Task<Int32Rect?> Select(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); var screen = System.Windows.Forms.SystemInformation.VirtualScreen; var window = new ScreenPinSelection(new Int32Rect(screen.X, screen.Y, screen.Width, screen.Height));
            window.registration = token.Register(() => window.Dispatcher.BeginInvoke(new Action(() => window.Finish(null)))); window.Show(); return window.completion.Task;
        }
    }
}
