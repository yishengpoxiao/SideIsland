using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace CodexIsland
{
    // Editor forms share the same compact spacing and keep their actions outside
    // the scroll region, including when validation produces a long explanation.
    internal sealed class CompactEditorWindow : Window
    {
        internal readonly Grid Layout = new Grid { Margin = new Thickness(16, 14, 16, 14) };
        internal readonly StackPanel Body = new StackPanel { Margin = new Thickness(0, 0, 6, 0) };
        internal readonly ScrollViewer Scroll;
        internal readonly TextBlock Error = ToolStyle.Hint("");
        internal Grid Footer { get; private set; }
        internal CompactEditorWindow(string title, double height, Window owner)
        {
            Title = title; MinWidth = 320; MinHeight = 360;
            Width = Math.Max(MinWidth, Math.Min(430, SystemParameters.WorkArea.Width - 40));
            MaxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height - 40); Height = Math.Min(height, MaxHeight);
            ResizeMode = ResizeMode.CanResize; ShowInTaskbar = false;
            if (owner != null) Owner = owner;
            WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
            Background = Display.Brush("#111518"); Foreground = Display.Brush("#E3EFE7");
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"); FontSize = 12;
            UseLayoutRounding = true; SnapsToDevicePixels = true;
            UiLocalization.Mark(this, Window.TitleProperty, title); UiTheme.Install(Resources);
            Layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Layout.RowDefinitions.Add(new RowDefinition());
            Layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var heading = IslandWindow.Text(title, 20, "#E5F2E9", true); heading.TextWrapping = TextWrapping.Wrap; heading.Margin = new Thickness(0, 0, 0, 10); Layout.Children.Add(heading);
            Scroll = new ScrollViewer { Content = Body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            Grid.SetRow(Scroll, 1); Layout.Children.Add(Scroll); Content = Layout;
            AppAppearance.Register(this);
        }
        internal void SetActions(Button save)
        {
            var cancel = IslandWindow.Button("取消", () => DialogResult = false); cancel.IsCancel = true;
            save.Background = Display.Brush("#3B6755"); save.Foreground = Display.Brush("#A1EACF");
            Footer = PanelActionLayout.Create(2, 90, cancel, save); Footer.Margin = new Thickness(0, 10, 0, 0);
            Grid.SetRow(Footer, 2); Layout.Children.Add(Footer); Body.Children.Add(Error);
        }
    }
    internal static class CompactDialogLayout
    {
        internal static StackPanel Card(CompactEditorWindow window)
        {
            var body = new StackPanel(); var frame = ToolStyle.Card(body); frame.Padding = new Thickness(12); frame.Margin = new Thickness(0, 0, 0, 8);
            window.Body.Children.Add(frame); return body;
        }
        internal static void Field(StackPanel body, string title, FrameworkElement control)
        {
            var label = IslandWindow.Text(title, 11, "#8FA89A"); label.TextWrapping = TextWrapping.Wrap; label.LineHeight = 16;
            label.Margin = new Thickness(0, body.Children.Count == 0 ? 0 : 8, 0, 4); body.Children.Add(label); body.Children.Add(control);
        }
        internal static TextBox Input(string value, int maximum)
        {
            var input = UiLocalization.Protect(ToolStyle.Input(value ?? "")); input.MaxLength = maximum;
            input.SetBinding(FrameworkElement.ToolTipProperty, new Binding("Text") { Source = input }); return input;
        }
        internal static Grid Browse(params Button[] buttons)
        {
            var row = PanelActionLayout.Create(2, 100, buttons); row.Margin = new Thickness(0, 6, 0, 0); return row;
        }
    }
}
