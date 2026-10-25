using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace CodexIsland
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            try { Run(args); }
            catch (Exception error)
            {
                StartupDiagnostics.Mark("启动失败", false);
                StartupDiagnostics.Failure(error);
                Environment.ExitCode = 1;
                bool diagnostic = args.Contains("--notification-status") || args.Contains("--notification-read-check") || args.Contains("--shutdown");
                if (!diagnostic) AppNotice.Show(null, UiLocalization.Localize("灵动岛未能完成启动：") + error.Message, UiLocalization.Localize("Codex Island"), MessageBoxImage.Error);
            }
        }

        private static void Run(string[] args)
        {
            if (args.Contains("--shutdown")) { SingleInstance.Signal("Close"); return; }
            if (args.Length == 2 && args[0] == "--notification-status")
            {
                // Diagnostics expose permission state only; never message contents.
                var service = new NotificationListenerService();
                File.WriteAllText(args[1], service.GetStatus().ToString());
                return;
            }
            if (args.Length == 2 && args[0] == "--notification-read-check")
            {
                // Packaged integration probe: exercise the same background read as
                // the UI, but never export application names or message contents.
                string result = System.Threading.Tasks.Task.Run(async () => {
                    var service = new NotificationListenerService();
                    var access = service.GetStatus();
                    if (access != NotificationAccessState.Allowed) return access + "; read=not-run";
                    await service.ReadAsync();
                    return access + "; read=" + (service.LastReadSucceeded ? "ok" : "failed");
                }).GetAwaiter().GetResult();
                File.WriteAllText(args[1], result); return;
            }
            bool demo = args.Contains("--demo") || args.Contains("--preview") || args.Contains("--smoke");
            // Previews and smoke tests must never claim the real application's
            // mutex or signal names, even when no normal instance is running yet.
            using (var instance = demo ? null : new SingleInstance())
            {
                if (instance != null && !instance.IsPrimary)
                {
                    if (instance.SignalShow(1500)) return;
                    // The previous instance may have exited while events were
                    // becoming available. Take over only after owning the mutex.
                    if (!instance.TryBecomePrimary())
                    {
                        Environment.ExitCode = 1;
                        AppNotice.Show(null, UiLocalization.Localize("已运行的灵动岛暂时未能接收显示请求，请稍后重试。"), UiLocalization.Localize("Codex Island"), MessageBoxImage.Warning);
                        return;
                    }
                }
                StartupDiagnostics.Begin(demo);
                var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                app.DispatcherUnhandledException += (s, e) => {
                    StartupDiagnostics.Failure(e.Exception);
                    AppNotice.Show(app.MainWindow, UiLocalization.Localize("灵动岛遇到错误：") + e.Exception.Message, UiLocalization.Localize("Codex Island"), MessageBoxImage.Error);
                    e.Handled = true;
                };
                var window = new IslandWindow(demo);
                StartupDiagnostics.Mark("窗口已创建", false);
                window.Loaded += (s, e) => StartupDiagnostics.Mark("窗口已加载", false);
                window.ContentRendered += (s, e) => StartupDiagnostics.Mark("界面已显示", true);
                int diagnosticIndex = Array.IndexOf(args, "--status-file");
                if (diagnosticIndex >= 0 && diagnosticIndex + 1 < args.Length) window.DiagnosticPath = args[diagnosticIndex + 1];
                app.MainWindow = window;
                window.Loaded += (s, e) => {
                    int index = Array.IndexOf(args, "--preview");
                    if (index >= 0 && index + 1 < args.Length) window.ExportPreviews(args[index + 1]);
                    if (args.Contains("--smoke")) window.RunSmoke();
                };
                var signals = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                if (instance != null)
                {
                    Action receive = () => { if (instance.TakeCloseRequest()) window.Close(); else if (instance.TakeShowRequest()) window.Reveal(); };
                    signals.Tick += (s, e) => receive();
                    // Consume any queued launch only after the window's own
                    // Loaded handler has established its initial compact state.
                    window.Loaded += (s, e) => { signals.Start(); receive(); };
                }
                try { app.Run(window); }
                finally { signals.Stop(); }
            }
        }
    }
}
