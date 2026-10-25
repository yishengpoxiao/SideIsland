using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace CodexIsland
{
    // Guards the actual repair invariant: a theme switch must never update
    // Color on a brush that retired native RenderData can still observe.
    internal static class ThemeSnapshotTests
    {
        internal static void Run()
        {
            string language = UiLocalization.Language, theme = UiTheme.Name;
            Window window = null;
            try
            {
                AppAppearance.Configure("zh", "dark");
                var retired = (SolidColorBrush)Display.Brush("#E3EFE7"); Color retiredColor = retired.Color;
                Check(retired.IsFrozen && !BindingOperations.IsDataBound(retired, SolidColorBrush.ColorProperty), "palette must enter rendering immutable");
                window = new Window { Width = 360, Height = 500, ShowActivated = false, ShowInTaskbar = false };
                var host = new StackPanel { Background = Display.Brush("#111518") }; window.Content = host;
                var literal = new Border { Height = 20, Background = Display.Brush("#1A2323"), BorderBrush = Display.Brush("#3A5145") }; host.Children.Add(literal);
                var shape = new Rectangle { Height = 20, Fill = Display.Brush("#A1EACF"), Stroke = Display.Brush("#3A5145") }; host.Children.Add(shape);
                var run = new Run("原文 Original") { Foreground = retired, Background = Display.Brush("#111518") };
                var document = new FlowDocument(new Paragraph(run));
                var rich = new RichTextBox { Height = 75, Document = document }; host.Children.Add(rich);
                var editor = new TextBox { Text = "保持原文 user=value", Foreground = retired }; host.Children.Add(editor);
                var dynamicLabel = new TextBlock { Text = "Dynamic resource" }; dynamicLabel.SetResourceReference(TextBlock.ForegroundProperty, "Island.Text"); host.Children.Add(dynamicLabel);
                var bindingSource = new Border { Background = retired };
                var bound = new TextBlock { Text = "Caller-owned binding" }; host.Children.Add(bound);
                var binding = BindingOperations.SetBinding(bound, TextBlock.ForegroundProperty, new Binding("Background") { Source = bindingSource });
                var protectedLabel = new TextBlock { Text = "#FFFFFF", Foreground = retired };
                host.Children.Add(UiTheme.ProtectColor(new Border { Child = protectedLabel }));
                var paint = new PalettePaint { Width = 24, Height = 24 }; host.Children.Add(paint);
                AppAppearance.Register(window); window.Show(); Settle(window); editor.Select(2, 4);
                string original = new TextRange(document.ContentStart, document.ContentEnd).Text;
                int styles = window.Resources.MergedDictionaries.Count;
                for (int iteration = 0; iteration < 24; iteration++)
                {
                    string next = iteration % 2 == 0 ? "light" : "dark";
                    int previousRenders = paint.Renders;
                    AppAppearance.Configure(iteration % 2 == 0 ? "en" : "zh", next); Settle(window);
                    Check(retired.Color == retiredColor && retired.IsFrozen, "a retired brush changed Color");
                    Same(literal.Background, "#1A2323", "literal border"); Same(literal.BorderBrush, "#3A5145", "literal outline");
                    Same(shape.Fill, "#A1EACF", "shape fill"); Same(shape.Stroke, "#3A5145", "shape stroke");
                    Same(run.Foreground, "#E3EFE7", "FlowDocument Run foreground"); Same(run.Background, "#111518", "FlowDocument Run background");
                    Same(editor.Foreground, "#E3EFE7", "literal editor foreground"); Same(dynamicLabel.Foreground, "#E3EFE7", "dynamic resource");
                    Check(Object.ReferenceEquals(window.Resources["Island.Text"], Display.Brush("#E3EFE7")), "resource dictionary did not select current snapshot");
                    Check(window.Resources.MergedDictionaries.Count == styles, "theme switching reinstalls styles");
                    Check(Object.ReferenceEquals(BindingOperations.GetBindingExpression(bound, TextBlock.ForegroundProperty), binding) && Object.ReferenceEquals(bound.Foreground, retired), "caller binding was overwritten");
                    Check(Object.ReferenceEquals(protectedLabel.Foreground, retired), "protected colors were replaced");
                    Check(editor.Text == "保持原文 user=value" && editor.SelectionStart == 2 && editor.SelectionLength == 4 && new TextRange(document.ContentStart, document.ContentEnd).Text == original, "theme replacement altered user content or selection");
                    Check(paint.Renders > previousRenders, "cached custom drawing was not invalidated");
                    // Render the child's local content at the capture origin;
                    // its offset inside the shown StackPanel is outside 24px.
                    var capture = new DrawingVisual();
                    using (var drawing = capture.RenderOpen()) drawing.DrawRectangle(new VisualBrush(paint) { Stretch = Stretch.Fill }, null, new Rect(0, 0, 24, 24));
                    var bitmap = new RenderTargetBitmap(24, 24, 96, 96, PixelFormats.Pbgra32); bitmap.Render(capture);
                    var pixel = new byte[4]; bitmap.CopyPixels(new Int32Rect(5, 5, 1, 1), pixel, 4, 0);
                    Color expected = ((SolidColorBrush)Display.Brush("#A1EACF")).Color;
                    Check(pixel[0] == expected.B && pixel[1] == expected.G && pixel[2] == expected.R && pixel[3] == 255, "custom drawing retained previous theme pixels: iteration=" + iteration + "; actual BGRA=" + String.Join(",", pixel) + "; expected=" + expected + "; renders=" + paint.Renders);
                }
                Console.WriteLine("PASS: 24 live frozen-theme replacements preserve retired brush colors, literal/dynamic/bound/protected brushes, document text, selection, style identity and custom-drawing pixels.");
            }
            finally { if (window != null) window.Close(); AppAppearance.Configure(language, theme); }
        }
        private sealed class PalettePaint : FrameworkElement
        {
            internal int Renders;
            protected override void OnRender(DrawingContext drawing) { Renders++; drawing.DrawRectangle(Display.Brush("#A1EACF"), null, new Rect(0, 0, ActualWidth, ActualHeight)); }
        }
        private static void Same(Brush actual, string role, string context) { Check(Object.ReferenceEquals(actual, Display.Brush(role)) && actual.IsFrozen, context + " did not adopt the current frozen brush"); }
        private static void Settle(Window window)
        {
            window.UpdateLayout(); var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); window.UpdateLayout();
        }
        private static void Check(bool value, string message) { if (!value) throw new Exception("Theme snapshot: " + message); }
    }
}
