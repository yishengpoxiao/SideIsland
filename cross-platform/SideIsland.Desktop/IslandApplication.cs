using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace SideIsland.Desktop;
public sealed class IslandApplication : Application
{
    public override void Initialize() { Name = "Side Island"; Styles.Add(new FluentTheme()); Styles.Add(IslandStyle.Create()); Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) => DiagnosticLog.Write("ui-unhandled", e.Exception); }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new IslandWindow(); desktop.MainWindow = window;
            var menu = new NativeMenu(); var show = new NativeMenuItem("Side Island"); show.Click += (_, _) => { window.Show(); window.Activate(); window.Expand(); };
            var quit = new NativeMenuItem("Quit Side Island"); quit.Click += (_, _) => window.Close(); menu.Add(show); menu.Add(quit); NativeMenu.SetMenu(this, menu);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
