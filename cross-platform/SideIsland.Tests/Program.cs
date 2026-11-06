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
            if (args.Contains("--preferences-only"))
            {
                Console.WriteLine("DIAGNOSTIC SELECTION: preference persistence; this is not a complete regression run.");
                PreferencePersistenceTests.Run(); SharedCoreTests.Run(); return 0;
            }
            if (args.Contains("--notification-persistence-only"))
            {
                Console.WriteLine("DIAGNOSTIC SELECTION: notification persistence; this is not a complete regression run.");
                NotificationTests.Run(); return 0;
            }
            if (PortToolTests.TryRunHelper(args, out int helperExit)) return helperExit;
            if (PowerToolTests.TryRunHelper(args, out helperExit)) return helperExit;
            if (WorkspaceLauncherTests.TryRunHelper(args, out helperExit)) return helperExit;
            bool portsOnly = args.Contains("--ports-only");
            bool hashOnly = args.Contains("--hash-only");
            bool logsOnly = args.Contains("--logs-only");
            bool workspaceOnly = args.Contains("--workspace-only");
            bool publicationOnly = args.Contains("--publication-only");
            bool dataStatusOnly = args.Contains("--data-status-only");
            bool batchHashOnly = args.Contains("--batch-hash-only");
            bool publicationBaseline = args.Contains("--publication-pin-baseline");
            bool uiOnly = args.Contains("--ui-only");
            if (batchHashOnly) { Console.WriteLine("DIAGNOSTIC SELECTION: batch checksums; this is not a complete regression run."); BatchHashTests.Run(); }
            else if (dataStatusOnly) Console.WriteLine("DIAGNOSTIC SELECTION: data status; this is not a complete regression run.");
            else if (publicationOnly) { Console.WriteLine("DIAGNOSTIC SELECTION: publication; this is not a complete regression run."); PortableFilePublicationTests.Run(); }
            else if (publicationBaseline) Console.WriteLine("DIAGNOSTIC SELECTION: pre-fix pin feedback; this is not a complete regression run.");
            else if (workspaceOnly) { Console.WriteLine("DIAGNOSTIC SELECTION: workspace-only; this is not a complete regression run."); PortableWorkspaceTests.Run(); WorkspaceLauncherTests.Run(); }
            else if (logsOnly) { Console.WriteLine("DIAGNOSTIC SELECTION: logs-only; this is not a complete regression run."); PortableLogTests.Run(); }
            else if (hashOnly) Console.WriteLine("DIAGNOSTIC SELECTION: hash-only; this is not a complete regression run.");
            else if (portsOnly) { Console.WriteLine("DIAGNOSTIC SELECTION: ports-only; this is not a complete regression run."); PortToolTests.Run(); }
            else if (!uiOnly) { PlacementTests.Run(); SharedCoreTests.Run(); PreferencePersistenceTests.Run(); ProviderTests.Run(); PortToolTests.Run(); BatchFileTests.Run(); PortableFilePublicationTests.Run(); NotificationTests.Run(); WatchTests.Run(); PowerToolTests.Run(); PortableLogTests.Run(); PortableWorkspaceTests.Run(); WorkspaceLauncherTests.Run(); }
            else Console.WriteLine("DIAGNOSTIC SELECTION: UI-only; this is not a complete regression run.");
            AppBuilder.Configure<IslandApplication>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().WithInterFont().SetupWithoutStarting();
            if (batchHashOnly) { BatchHashUiTests.Run(args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal))); return 0; }
            if (dataStatusOnly) { DataStateTests.Run(args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal))); return 0; }
            if (publicationBaseline) { PublicationUiTests.RunPinBaseline(); return 0; }
            if (publicationOnly) { PublicationUiTests.Run(args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal))); ScreenPinLifecycleTests.Run(); ClipboardQrTests.Run(); HashExportTests.Run(args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal))); Console.WriteLine("PASS: publication UI diagnostic selection; full regression still required."); return 0; }
            if (workspaceOnly) { WorkspaceUiTests.Run(args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal))); Console.WriteLine("PASS: workspace-only diagnostic selection; full regression still required."); return 0; }
            if (logsOnly) { LogToolTests.Run(args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal))); LogExportTests.Run(args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal))); Console.WriteLine("PASS: logs-only diagnostic selection; full regression still required."); return 0; }
            if (hashOnly) { HashToolTests.Run(args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal))); Console.WriteLine("PASS: hash-only diagnostic selection; full regression still required."); return 0; }
            if (portsOnly) { PortToolTests.RunUi(); Console.WriteLine("PASS: ports-only diagnostic selection; full regression still required."); return 0; }
            string? artifacts = args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal));
            BatchHashTests.Run();
            IntegrationLifecycleTests.Run();
            MacNotchTests.Run();
            ScreenPinTests.Run();
            BatchFileTests.RunImages();
            InteractionTests.Run();
            ClipboardQrTests.Run();
            PublicationUiTests.Run(artifacts);
            HashToolTests.Run(artifacts);
            BatchHashUiTests.Run(artifacts);
            DiffToolTests.Run();
            PortToolTests.RunUi();
            WatchTests.RunUi();
            PowerToolTests.RunUi();
            LogToolTests.Run(artifacts);
            LogExportTests.Run(artifacts);
            WorkspaceUiTests.Run(artifacts);
            DataStateTests.Run(artifacts);
            LayoutTests.Run(artifacts);
            Console.WriteLine(uiOnly ? "PASS: UI-only diagnostic selection; full regression still required." : "PASS: portable geometry, shared models, notch contracts and actual Avalonia layout matrix. Target macOS/Linux native execution is not implied."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
