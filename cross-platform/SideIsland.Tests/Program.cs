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
            if (PortToolTests.TryRunHelper(args, out int helperExit)) return helperExit;
            bool portsOnly = args.Contains("--ports-only");
            bool uiOnly = args.Contains("--ui-only");
            if (portsOnly) { Console.WriteLine("DIAGNOSTIC SELECTION: ports-only; this is not a complete regression run."); PortToolTests.Run(); }
            else if (!uiOnly) { PlacementTests.Run(); SharedCoreTests.Run(); ProviderTests.Run(); PortToolTests.Run(); BatchFileTests.Run(); NotificationTests.Run(); WatchTests.Run(); }
            else Console.WriteLine("DIAGNOSTIC SELECTION: UI-only; this is not a complete regression run.");
            AppBuilder.Configure<IslandApplication>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().WithInterFont().SetupWithoutStarting();
            if (portsOnly) { PortToolTests.RunUi(); Console.WriteLine("PASS: ports-only diagnostic selection; full regression still required."); return 0; }
            IntegrationLifecycleTests.Run(); MacNotchTests.Run(); ScreenPinTests.Run(); BatchFileTests.RunImages(); InteractionTests.Run(); HashToolTests.Run(); DiffToolTests.Run(); PortToolTests.RunUi(); WatchTests.RunUi(); LayoutTests.Run(args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal)));
            Console.WriteLine(uiOnly ? "PASS: UI-only diagnostic selection; full regression still required." : "PASS: portable geometry, shared models, notch contracts and actual Avalonia layout matrix. Target macOS/Linux native execution is not implied."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
