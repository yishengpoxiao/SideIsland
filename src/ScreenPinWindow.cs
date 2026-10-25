using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexIsland
{
    internal sealed class ScreenPinWindow : Window
    {
        private readonly Image image = new Image { Stretch = Stretch.Uniform };
        private readonly Slider zoom = new Slider { Minimum = 10, Maximum = 300, Value = 100, MinHeight = 24, VerticalAlignment = VerticalAlignment.Center };
        private readonly Slider transparency = new Slider { Minimum = 20, Maximum = 100, Value = 100, MinHeight = 24, VerticalAlignment = VerticalAlignment.Center };
        private readonly double initialWidth, initialHeight;
        private readonly DockPanel body;
        private readonly Grid toolbar;
        private readonly ScrollViewer viewport;
        private readonly ScreenshotAnnotationDocument annotations;
        private readonly ScreenshotAnnotationSurface annotationSurface;
        private readonly ScrollViewer annotationScroll;
        internal BitmapSource Snapshot { get { return image.Source as BitmapSource; } }
        internal double ZoomPercent { get { return zoom.Value; } set { zoom.Value = value; } }
        internal double OpacityPercent { get { return transparency.Value; } set { transparency.Value = value; } }
        public ScreenPinWindow(BitmapSource snapshot)
        {
            if (snapshot == null || !snapshot.IsFrozen) throw new ArgumentException("截图未准备好。");
            ScreenPinTools.ValidateCapture(new Int32Rect(0, 0, snapshot.PixelWidth, snapshot.PixelHeight));
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; Topmost = true; ShowInTaskbar = false; ResizeMode = ResizeMode.NoResize; SizeToContent = SizeToContent.WidthAndHeight; Title = "Side Island 截图钉屏";
            FontFamily = new FontFamily("Segoe UI"); FontSize = 12; UseLayoutRounding = true; SnapsToDevicePixels = true;
            double initialScale = Math.Min(1, Math.Min(720.0 / snapshot.PixelWidth, 520.0 / snapshot.PixelHeight)); initialWidth = snapshot.PixelWidth * initialScale; initialHeight = snapshot.PixelHeight * initialScale;
            image.Source = snapshot; image.Width = initialWidth; image.Height = initialHeight;
            annotations = new ScreenshotAnnotationDocument(snapshot);
            annotationSurface = new ScreenshotAnnotationSurface(annotations) { Width = initialWidth, Height = initialHeight };
            annotationSurface.PanRequested += () => { try { DragMove(); } catch (InvalidOperationException) { } };
            body = new DockPanel { LastChildFill = true };
            toolbar = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            toolbar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); toolbar.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) }); toolbar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var header = new Grid(); header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) }); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = ToolStyle.Hint("拖动图片 · 滚轮缩放"); label.Margin = new Thickness(0); label.VerticalAlignment = VerticalAlignment.Center; label.Cursor = Cursors.SizeAll; header.Children.Add(label);
            var close = IslandWindow.Button("×", Close); close.Width = close.Height = 28; close.Padding = new Thickness(0); close.FontSize = 18; close.Margin = new Thickness(0); UiLocalization.ToolTip(close, "关闭"); UiLocalization.Mark(close, System.Windows.Automation.AutomationProperties.NameProperty, "关闭"); Grid.SetColumn(close, 2); header.Children.Add(close); toolbar.Children.Add(header);
            var controls = new Grid(); controls.ColumnDefinitions.Add(new ColumnDefinition()); controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) }); controls.ColumnDefinitions.Add(new ColumnDefinition());
            controls.Children.Add(SliderGroup("缩放", zoom)); var alphaGroup = SliderGroup("透明度", transparency); Grid.SetColumn(alphaGroup, 2); controls.Children.Add(alphaGroup); Grid.SetRow(controls, 2); toolbar.Children.Add(controls);
            UiLocalization.ToolTip(zoom, "缩放截图（滚轮也可缩放）"); UiLocalization.ToolTip(transparency, "截图不透明度"); DockPanel.SetDock(toolbar, Dock.Bottom); body.Children.Add(toolbar);
            toolbar.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) }); toolbar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            annotationScroll = new ScrollViewer { Content = new ScreenshotAnnotationControls(annotations, annotationSurface), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            var annotationExpander = new Expander { Header = "标注与打码", Content = annotationScroll, Margin = new Thickness(0), Padding = new Thickness(0) };
            UiLocalization.Mark(annotationExpander, HeaderedContentControl.HeaderProperty, "标注与打码"); Grid.SetRow(annotationExpander, 4); toolbar.Children.Add(annotationExpander);
            annotationExpander.Expanded += (s, e) => Dispatcher.BeginInvoke(new Action(UpdateViewportLimits)); annotationExpander.Collapsed += (s, e) => Dispatcher.BeginInvoke(new Action(UpdateViewportLimits));
            var picture = new Grid(); image.Visibility = Visibility.Hidden; picture.Children.Add(image); picture.Children.Add(annotationSurface);
            viewport = new ScrollViewer { Content = picture, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; body.Children.Add(viewport); UpdateViewportLimits();
            Content = new Border { Child = body, Background = UiTheme.Brush("#1A2323"), BorderBrush = UiTheme.Brush("#252F31"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16), Padding = new Thickness(8) };
            image.MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch (InvalidOperationException) { } e.Handled = true; } };
            label.MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch (InvalidOperationException) { } e.Handled = true; } };
            PreviewMouseWheel += (s, e) => { if (annotationScroll.IsMouseOver) return; zoom.Value = Math.Max(zoom.Minimum, Math.Min(zoom.Maximum, zoom.Value * (e.Delta > 0 ? 1.1 : 1 / 1.1))); e.Handled = true; };
            zoom.ValueChanged += (s, e) => { UpdateViewportLimits(); image.Width = annotationSurface.Width = initialWidth * zoom.Value / 100; image.Height = annotationSurface.Height = initialHeight * zoom.Value / 100; };
            transparency.ValueChanged += (s, e) => Opacity = transparency.Value / 100;
            PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) { e.Handled = true; if (annotationSurface.IsDrawing) annotationSurface.CancelDrawing(); else Close(); } else if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control && !(Keyboard.FocusedElement is TextBox)) { annotations.Undo(); e.Handled = true; } };
            SizeChanged += (s, e) => { if (!IsVisible) return; var area = CurrentWorkArea(); Left = Math.Max(area.Left, Math.Min(Left, area.Right - ActualWidth)); Top = Math.Max(area.Top, Math.Min(Top, area.Bottom - ActualHeight)); };
            Closed += (s, e) => { annotationSurface.CancelDrawing(); image.Source = null; annotations.Dispose(); };
            AppAppearance.Register(this);
        }
        private static Grid SliderGroup(string caption, Slider slider)
        {
            var group = new Grid(); group.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); group.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var heading = new Grid(); heading.ColumnDefinitions.Add(new ColumnDefinition()); heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = ToolStyle.Hint(caption); name.Margin = new Thickness(0, 0, 6, 0); name.VerticalAlignment = VerticalAlignment.Center; heading.Children.Add(name);
            var value = UiLocalization.Protect(new TextBlock { FontSize = 11, LineHeight = 16, Foreground = UiTheme.Brush("#E3EFE7"), TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center }); value.SetBinding(TextBlock.TextProperty, new Binding("Value") { Source = slider, StringFormat = "{0:0}%" }); Grid.SetColumn(value, 1); heading.Children.Add(value); group.Children.Add(heading);
            Grid.SetRow(slider, 1); group.Children.Add(slider); return group;
        }
        private Rect CurrentWorkArea()
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return SystemParameters.WorkArea;
            var screen = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
            var source = PresentationSource.FromVisual(this); Matrix conversion = source != null && source.CompositionTarget != null ? source.CompositionTarget.TransformFromDevice : Matrix.Identity;
            Point first = conversion.Transform(new Point(screen.Left, screen.Top)), last = conversion.Transform(new Point(screen.Right, screen.Bottom)); return new Rect(first, last);
        }
        private void UpdateViewportLimits()
        {
            Rect workArea = CurrentWorkArea();
            double width = Math.Min(900, Math.Max(120, workArea.Width - 32));
            if (annotationScroll != null) annotationScroll.MaxHeight = Math.Max(32, Math.Min(240, workArea.Height - 180));
            body.MinWidth = Math.Min(222, width); body.MaxWidth = width; toolbar.MaxWidth = width; toolbar.Measure(new Size(width, Double.PositiveInfinity));
            double height = Math.Max(32, Math.Min(650, workArea.Height - toolbar.DesiredSize.Height - 48));
            viewport.MaxWidth = width; viewport.MaxHeight = height; body.MaxHeight = height + toolbar.DesiredSize.Height;
        }
    }
}
