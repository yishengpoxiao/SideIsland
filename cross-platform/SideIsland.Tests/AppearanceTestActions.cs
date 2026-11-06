using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using SideIsland.Desktop;

namespace SideIsland.Tests;

// Use the same header actions as a user. Mutating preferences or only rendering
// a page can leave the old shell palette and navigation captions in place.
internal static class AppearanceTestActions
{
    internal static void Set(IslandWindow window, string language, bool light)
    {
        if (language is not ("zh" or "en")) throw new ArgumentException("Expected zh or en.", nameof(language));
        Flush(window);
        Assert(window, window.Settings.Language, window.Settings.LightTheme);
        if (window.Settings.Language != language)
        {
            ClickHeader(window, window.Settings.Language == "en" ? "中" : "EN");
            if (window.Settings.Language != language) throw new InvalidOperationException("Appearance: language header action did not apply the requested language.");
        }
        if (window.Settings.LightTheme != light)
        {
            ClickHeader(window, window.Settings.LightTheme ? "☾" : "☼");
            if (window.Settings.LightTheme != light) throw new InvalidOperationException("Appearance: theme header action did not apply the requested theme.");
        }
        Assert(window, language, light);
    }

    internal static void Assert(IslandWindow window, string language, bool light)
    {
        Flush(window);
        if (window.Settings.Language != language || window.Settings.LightTheme != light) throw new InvalidOperationException("Appearance: requested preferences differ from the actual UI state.");
        var host = Field<Grid>(window, "expandedHost");
        var expectedBackground = light ? Colors.White : Color.Parse("#141416");
        if (host.Background is not ISolidColorBrush background || background.Color != expectedBackground)
            throw new InvalidOperationException($"Appearance: {language}/{(light ? "light" : "dark")} shell background does not match {expectedBackground}.");
        var variant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        if (window.RequestedThemeVariant != variant || window.ActualThemeVariant != variant)
            throw new InvalidOperationException("Appearance: actual framework theme does not match the explicit shell palette.");
        string[] captions = language == "en" ? ["Messages", "Status", "Tools"] : ["消息", "状态", "工具"];
        var tabs = Field<Button[]>(window, "tabs");
        if (tabs.Length != captions.Length) throw new InvalidOperationException("Appearance: unexpected navigation count.");
        var foreground = Color.Parse(light ? "#1C1C1E" : "#F5F5F7");
        for (int index = 0; index < tabs.Length; index++)
            if (tabs[index].Content is not TextBlock text || text.Text != captions[index] || text.Foreground is not ISolidColorBrush brush || brush.Color != foreground)
                throw new InvalidOperationException("Appearance: navigation caption or text color does not match the requested language/theme: " + captions[index]);
        _ = HeaderButton(window, language == "en" ? "中" : "EN");
        _ = HeaderButton(window, light ? "☾" : "☼");
    }

    private static void ClickHeader(IslandWindow window, string caption)
    {
        var button = HeaderButton(window, caption);
        if (!window.IsExpanded || !button.IsEffectivelyVisible || !button.IsEnabled) throw new InvalidOperationException("Appearance: header action is unavailable: " + caption);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Flush(window);
    }
    private static Button HeaderButton(IslandWindow window, string caption)
    {
        var header = Field<Grid>(window, "expandedHost").Children.OfType<Grid>().Single(control => Grid.GetRow(control) == 0);
        return header.Children.OfType<Button>().Single(button => button.Content is TextBlock text && text.Text == caption);
    }
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
    private static void Flush(IslandWindow window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
}
