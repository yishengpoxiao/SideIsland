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
            if (PowerToolTests.TryRunHelper(args, out helperExit)) return helperExit;
            if (WorkspaceLauncherTests.TryRunHelper(args, out helperExit)) return helperExit;
            bool portsOnly = args.Contains("--ports-only");
            bool hashOnly = args.Contains("--hash-only");
            bool logsOnly = args.Contains("--logs-only");
            bool workspaceOnly = args.Contains("--workspace-only");
            bool uiOnly = args.Contains("--ui-only");
            if (workspaceOnly) { Console.WriteLine("DIAGNOSTIC SELECTION: workspace-only; this is not a complete regression run."); PortableWorkspaceTests.Run(); WorkspaceLauncherTests.Run(); }
            else if (logsOnly) { Console.WriteLine("DIAGNOSTIC SELECTION: logs-only; this is not a complete regression run."); PortableLogTests.Run(); }
            else if (hashOnly) Console.WriteLine("DIAGNOSTIC SELECTION: hash-only; this is not a complete regression run.");
            else if (portsOnly) { Console.WriteLine("DIAGNOSTIC SELECTION: ports-only; this is not a complete regression run."); PortToolTests.Run(); }
            else if (!uiOnly) { PlacementTests.Run(); SharedCoreTests.Run(); ProviderTests.Run(); PortToolTests.Run(); BatchFileTests.Run(); NotificationTests.Run(); WatchTests.Run(); PowerToolTests.Run(); PortableLogTests.Run(); PortableWorkspaceTests.Run(); WorkspaceLauncherTests.Run(); }
            else Console.WriteLine("DIAGNOSTIC SELECTION: UI-only; this is not a complete regression run.");
            AppBuilder.Configure<IslandApplication>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().WithInterFont().SetupWithoutStarting();
            if (workspaceOnly) { WorkspaceUiTests.Run(args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal))); Console.WriteLine("PASS: workspace-only diagnostic selection; full regression still required."); return 0; }
            if (logsOnly) { LogToolTests.Run(args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal))); Console.WriteLine("PASS: logs-only diagnostic selection; full regression still required."); return 0; }
            if (hashOnly) { HashToolTests.Run(args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal))); Console.WriteLine("PASS: hash-only diagnostic selection; full regression still required."); return 0; }
            if (portsOnly) { PortToolTests.RunUi(); Console.WriteLine("PASS: ports-only diagnostic selection; full regression still required."); return 0; }
            IntegrationLifecycleTests.Run(); MacNotchTests.Run(); ScreenPinTests.Run(); BatchFileTests.RunImages(); InteractionTests.Run(); ClipboardQrTests.Run(); HashToolTests.Run(args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal))); DiffToolTests.Run(); PortToolTests.RunUi(); WatchTests.RunUi(); PowerToolTests.RunUi(); LogToolTests.Run(args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal))); WorkspaceUiTests.Run(args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal))); LayoutTests.Run(args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal)));
            Console.WriteLine(uiOnly ? "PASS: UI-only diagnostic selection; full regression still required." : "PASS: portable geometry, shared models, notch contracts and actual Avalonia layout matrix. Target macOS/Linux native execution is not implied."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
