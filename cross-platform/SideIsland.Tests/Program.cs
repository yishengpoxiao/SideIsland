using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using SideIsland.Desktop;
namespace SideIsland.Tests;
internal static class Program
{
    [STAThread] private static int Main(string[] args)
    {
        try
        {
            bool uiOnly = args.Contains("--ui-only");
            if (!uiOnly) { PlacementTests.Run(); SharedCoreTests.Run(); ProviderTests.Run(); BatchFileTests.Run(); NotificationTests.Run(); }
            else Console.WriteLine("DIAGNOSTIC SELECTION: UI-only; this is not a complete regression run.");
            AppBuilder.Configure<IslandApplication>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().WithInterFont().SetupWithoutStarting();
            MacNotchTests.Run(); ScreenPinTests.Run(); BatchFileTests.RunImages(); InteractionTests.Run(); LayoutTests.Run(args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal)));
            Console.WriteLine(uiOnly ? "PASS: UI-only diagnostic selection; full regression still required." : "PASS: portable geometry, shared models, notch contracts and actual Avalonia layout matrix. Target macOS/Linux native execution is not implied."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
