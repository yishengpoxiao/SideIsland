using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexIsland
{
    internal static class UiThemePolishTests
    {
        internal static void Run()
        {
            string previous = UiTheme.Name;
            var window = new Window { Width = 400, Height = 430, ShowInTaskbar = false, ShowActivated = false, Title = "Theme control regression" };
            try
            {
                UiTheme.Configure("light"); UiTheme.Install(window.Resources);
                var body = new StackPanel { Margin = new Thickness(16) }; window.Content = body;
                var horizontal = new Slider { Minimum = 0, Maximum = 100, Value = 50, TickFrequency = 10, IsSnapToTickEnabled = true, SmallChange = 1, LargeChange = 20, Width = 300 };
                var vertical = new Slider { Minimum = 0, Maximum = 100, Value = 50, Orientation = Orientation.Vertical, Width = 30, Height = 120 };
                var editor = new TextBox { Text = "原文 user=value", Padding = new Thickness(8), Margin = new Thickness(0, 4, 0, 8) };
                var choice = new ComboBox { ItemsSource = new[] { "One", "Two", "Three" }, SelectedIndex = 1 };
                var switchBox = new CheckBox { Content = "Keep this setting", IsChecked = true, Style = (Style)window.Resources["Switch.CheckBox"] };
                var scroll = new ScrollViewer { Height = 65, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new Border { Height = 220, Background = Brushes.Transparent } };
                body.Children.Add(horizontal); body.Children.Add(vertical); body.Children.Add(editor); body.Children.Add(choice); body.Children.Add(switchBox); body.Children.Add(scroll);
                window.Show(); window.UpdateLayout(); Pump();
                var horizontalTrack = (Track)horizontal.Template.FindName("PART_Track", horizontal);
                var verticalTrack = (Track)vertical.Template.FindName("PART_Track", vertical);
                Check(horizontalTrack != null && verticalTrack != null, "slider templates expose the native track contract");
                double width = horizontal.ActualWidth, height = horizontal.ActualHeight;
                horizontalTrack.Thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
                horizontalTrack.Thumb.RaiseEvent(new DragDeltaEventArgs(45, 0) { RoutedEvent = Thumb.DragDeltaEvent });
                horizontalTrack.Thumb.RaiseEvent(new DragCompletedEventArgs(45, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
                Check(horizontal.Value > 50 && horizontal.Value % 10 == 0, "horizontal thumb dragging changes the real slider value and keeps tick snapping");
                horizontal.Value = 50; Slider.IncreaseSmall.Execute(null, horizontal);
                Check(horizontal.Value == 60, "native small-step keyboard command advances to the next snap tick");
                Slider.DecreaseLarge.Execute(null, horizontal);
                Check(horizontal.Value == 40, "native large-step command preserves configured LargeChange");
                verticalTrack.Thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
                verticalTrack.Thumb.RaiseEvent(new DragDeltaEventArgs(0, -24) { RoutedEvent = Thumb.DragDeltaEvent });
                verticalTrack.Thumb.RaiseEvent(new DragCompletedEventArgs(0, -24, false) { RoutedEvent = Thumb.DragCompletedEvent });
                Check(vertical.Value > 50, "vertical thumb dragging upward increases the real value");
                vertical.IsDirectionReversed = true; window.UpdateLayout();
                Check(verticalTrack.IsDirectionReversed, "vertical reversed sliders retain their inverse direction");
                var bars = Find<ScrollBar>(scroll);
                ScrollBar visibleBar = bars.Find(value => value.Orientation == Orientation.Vertical && value.Visibility == Visibility.Visible);
                Check(visibleBar != null && visibleBar.ActualWidth <= 10.1, "scrollbar overrides native minimum width and stays a narrow touch-free rail");
                visibleBar.ApplyTemplate();
                var scrollTrack = (Track)visibleBar.Template.FindName("PART_Track", visibleBar);
                scrollTrack.Thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
                scrollTrack.Thumb.RaiseEvent(new DragDeltaEventArgs(0, 18) { RoutedEvent = Thumb.DragDeltaEvent });
                scrollTrack.Thumb.RaiseEvent(new DragCompletedEventArgs(0, 18, false) { RoutedEvent = Thumb.DragCompletedEvent });
                Pump(); Check(scroll.VerticalOffset > 0, "narrow scrollbar thumb still scrolls its real viewer");
                var brush = (SolidColorBrush)window.Resources["Island.Text"]; Color light = brush.Color;
                UiTheme.Configure("dark"); window.UpdateLayout(); Pump();
                Check(brush.Color == light && brush.IsFrozen && !Object.ReferenceEquals(brush, window.Resources["Island.Text"]) && ((SolidColorBrush)window.Resources["Island.Text"]).Color != light && ((SolidColorBrush)editor.Foreground).Color == ((SolidColorBrush)window.Resources["Island.Text"]).Color, "live control resources replace frozen snapshots and preserve the old native brush color");
                Check(horizontal.ActualWidth == width && horizontal.ActualHeight == height, "theme changes do not change slider geometry");
                Check(editor.Text == "原文 user=value" && choice.SelectedIndex == 1 && switchBox.IsChecked == true, "theme switches preserve editor, selection and toggle state");
                Check(((SolidColorBrush)UiTheme.Brush("#111518")).Color.R == 17, "dark grouped background remains near-black");
                UiTheme.Configure("light");
                Check(((SolidColorBrush)UiTheme.Brush("#111518")).Color == Color.FromRgb(242, 242, 247) && ((SolidColorBrush)UiTheme.Brush("#1A2323")).Color == Colors.White, "light grouped palette separates neutral page and white cards");
                Console.WriteLine("PASS: grouped iOS palette, live control state, horizontal/vertical slider drag and keyboard steps, stable geometry, and narrow working scrollbar.");
            }
            finally { window.Close(); UiTheme.Configure(previous); Pump(); }
        }
        private static List<T> Find<T>(DependencyObject root) where T : DependencyObject
        {
            var result = new List<T>(); Visit(root, result); return result;
        }
        private static void Visit<T>(DependencyObject item, List<T> result) where T : DependencyObject
        {
            var typed = item as T; if (typed != null) result.Add(typed);
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(item); i++) Visit(VisualTreeHelper.GetChild(item, i), result);
        }
        private static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
        private static void Check(bool value, string message) { if (!value) throw new Exception("Theme polish: " + message); }
    }
}
