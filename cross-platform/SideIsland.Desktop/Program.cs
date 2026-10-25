using Avalonia;
namespace SideIsland.Desktop;
internal static class Program
{
    [STAThread] public static int Main(string[] args)
    {
        DiagnosticLog.Write("start");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => DiagnosticLog.Write("unhandled", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => DiagnosticLog.Write("unobserved-task", e.Exception);
        try { var result = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); DiagnosticLog.Write("exit:" + result); return result; }
        catch (Exception error) { DiagnosticLog.Write("startup-failed", error); return 1; }
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<IslandApplication>().UsePlatformDetect().With(new MacOSPlatformOptions { ShowInDock = false }).WithInterFont().LogToTrace();
}
