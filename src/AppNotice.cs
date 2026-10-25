using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CodexIsland
{
    internal static class AppNotice
    {
        [ThreadStatic] private static bool showing;

        internal static void Show(Window owner, string message, string title, MessageBoxImage image = MessageBoxImage.Information)
        {
            // An exception may occur before WPF resources are available, or in
            // the notice itself. Keep a native last-resort path for that case.
            if (showing) { Native(owner, message, title, image); return; }
            showing = true;
            try { new NoticeWindow(message, title, image, owner).ShowDialog(); }
            catch { Native(owner, message, title, image); }
            finally { showing = false; }
        }

        private static void Native(Window owner, string message, string title, MessageBoxImage image)
        {
            if (owner != null && owner.IsVisible) MessageBox.Show(owner, message, title, MessageBoxButton.OK, image);
            else MessageBox.Show(message, title, MessageBoxButton.OK, image);
        }
    }

    internal sealed class NoticeWindow : Window
    {
        internal readonly TextBlock Heading, Message;
        internal readonly ScrollViewer MessageScroll;
        internal readonly Button Accept;

        internal NoticeWindow(string message, string title, MessageBoxImage image = MessageBoxImage.Information, Window owner = null)
        {
            Title = title ?? UiLocalization.Localize("灵动岛");
            Width = 420; MinWidth = 280;
            MaxWidth = Math.Max(MinWidth, Math.Min(560, SystemParameters.WorkArea.Width - 32));
            MaxHeight = Math.Max(180, SystemParameters.WorkArea.Height - 32);
            SizeToContent = SizeToContent.Height; ShowInTaskbar = false; ShowActivated = true;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true; Background = Brushes.Transparent;
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"); FontSize = 12;
            UseLayoutRounding = true; SnapsToDevicePixels = true;
            if (owner != null && owner.IsVisible) { Owner = owner; Topmost = owner.Topmost; }
            WindowStartupLocation = Owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
            UiTheme.Install(Resources);
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition());
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var header = new Grid { Margin = new Thickness(0, 0, 0, 10), Background = Brushes.Transparent };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new ColumnDefinition());
            bool warning = image == MessageBoxImage.Warning || image == MessageBoxImage.Error;
            string accent = image == MessageBoxImage.Error ? "#F59A9A" : warning ? "#FFBE83" : "#A1EACF";
            var mark = IslandWindow.Text(warning ? "!" : "i", 14, accent, true, false); mark.TextAlignment = TextAlignment.Center; mark.VerticalAlignment = VerticalAlignment.Center;
            header.Children.Add(new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(8), Background = Display.Brush("#202D29"), Child = mark, Margin = new Thickness(0, 0, 8, 0) });
            Heading = IslandWindow.Text(Title, 18, "#E3EFE7", true, false); Heading.TextWrapping = TextWrapping.Wrap; Heading.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(Heading, 1); header.Children.Add(Heading);
            Heading.LineHeight = 24; Heading.LineStackingStrategy = LineStackingStrategy.BlockLineHeight; Heading.MaxHeight = 72; Heading.TextTrimming = TextTrimming.CharacterEllipsis; Heading.ToolTip = Title;
            header.MouseLeftButtonDown += (s, e) => { if (e.ChangedButton == MouseButton.Left) { try { DragMove(); } catch (InvalidOperationException) { } } };
            grid.Children.Add(header);
            // Messages may contain paths or exception details. Localize only
            // caller-owned UI text, never these complete original strings.
            Message = IslandWindow.Text(message ?? "", 12, "#E3EFE7", false, false); Message.TextWrapping = TextWrapping.Wrap; Message.LineHeight = 18;
            var card = new Border { Child = Message, Background = Display.Brush("#1A2323"), CornerRadius = new CornerRadius(12), Padding = new Thickness(12) };
            MessageScroll = new ScrollViewer { Content = card, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = Math.Min(300, Math.Max(64, MaxHeight - 112)) };
            Grid.SetRow(MessageScroll, 1); grid.Children.Add(MessageScroll);
            Accept = IslandWindow.Button("确定", Close); Accept.IsDefault = true; Accept.IsCancel = true; Accept.MinHeight = 32; Accept.Background = Display.Brush("#31594B"); Accept.Foreground = Display.Brush("#A1EACF");
            var footer = PanelActionLayout.Create(1, Accept); footer.Margin = new Thickness(0, 10, 0, 0); Grid.SetRow(footer, 2); grid.Children.Add(footer);
            Content = new Border { Child = grid, Padding = new Thickness(16), CornerRadius = new CornerRadius(16), Background = Display.Brush("#111518"), BorderBrush = Display.Brush("#3A5145"), BorderThickness = new Thickness(1) };
            AppAppearance.Register(this);
            ContentRendered += (s, e) => Accept.Focus();
        }
    }
}
