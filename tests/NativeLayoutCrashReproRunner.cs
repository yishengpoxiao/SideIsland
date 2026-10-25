using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace CodexIsland
{
    // Opt-in diagnostic entry point, excluded from the normal suite registry.
    // Full mode invokes the original suite unchanged. Short modes are explicit
    // counterfactuals for debugging and never claim full regression coverage.
    internal static class NativeLayoutCrashRepro
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length >= 2 && args[0] == "--decode")
            {
                var lines = NativeLayoutCrashTrace.Decode(args[1]);
                if (args.Length == 3)
                {
                    string output = NativeLayoutCrashTrace.WorkspacePath(args[2]);
                    Directory.CreateDirectory(Path.GetDirectoryName(output));
                    File.WriteAllLines(output, lines, new UTF8Encoding(false));
                }
                else foreach (string line in lines) Console.WriteLine(line);
                return 0;
            }
            string mode = "full", trace = null, log = null; int iterations = 1, maximumSeconds = 180;
            try
            {
                for (int index = 0; index < args.Length; index += 2)
                {
                    if (index + 1 == args.Length) throw new ArgumentException("Every diagnostic option needs a value.");
                    switch (args[index])
                    {
                        case "--mode": mode = args[index + 1]; break;
                        case "--iterations": iterations = Int32.Parse(args[index + 1]); break;
                        case "--max-seconds": maximumSeconds = Int32.Parse(args[index + 1]); break;
                        case "--trace": trace = NativeLayoutCrashTrace.WorkspacePath(args[index + 1]); break;
                        case "--log": log = NativeLayoutCrashTrace.WorkspacePath(args[index + 1]); break;
                        default: throw new ArgumentException("Unknown diagnostic option: " + args[index]);
                    }
                }
                if (mode != "full" && mode != "content-media" && mode != "media") throw new ArgumentException("Mode must be full, content-media or media.");
                if (iterations < 1 || iterations > 20 || maximumSeconds < 1 || maximumSeconds > 1200) throw new ArgumentException("Invalid diagnostic bounds.");
                if (trace == null) throw new ArgumentException("--trace requires a project-local file path.");
                Environment.SetEnvironmentVariable("CODEX_ISLAND_NATIVE_TRACE_PATH", trace);
                Environment.SetEnvironmentVariable("CODEX_ISLAND_NATIVE_TRACE_QUIET", "1");
                var previousContext = SynchronizationContext.Current;
                var clock = Stopwatch.StartNew();
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                try
                {
                    NativeLayoutCrashTrace.Mark("repro.start mode=" + mode + ";iterations=" + iterations + ";maxSeconds=" + maximumSeconds);
                    if (mode == "full")
                    {
                        for (int iteration = 0; iteration < iterations; iteration++)
                        {
                            Deadline(clock, maximumSeconds); NativeLayoutCrashTrace.Mark("repro.full.before iteration=" + iteration);
                            ToolPageLayoutTests.Run();
                            NativeLayoutCrashTrace.Mark("repro.full.after iteration=" + iteration); Deadline(clock, maximumSeconds);
                        }
                    }
                    else ShortSequence(mode, iterations, maximumSeconds, clock);
                    NativeLayoutCrashTrace.Mark("repro.completed elapsedMs=" + clock.ElapsedMilliseconds);
                    Console.WriteLine("DIAGNOSTIC COMPLETE: mode=" + mode + ";iterations=" + iterations + ";elapsedMs=" + clock.ElapsedMilliseconds);
                    if (log != null) File.WriteAllLines(log, NativeLayoutCrashTrace.Decode(trace), new UTF8Encoding(false));
                    return 0;
                }
                finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
            }
            catch (Exception error)
            {
                NativeLayoutCrashTrace.Mark("repro.managed-failure type=" + error.GetType().FullName + ";message=" + error.Message);
                Console.Error.WriteLine("DIAGNOSTIC FAILED: " + error);
                if (log != null && trace != null && File.Exists(trace)) File.WriteAllLines(log, NativeLayoutCrashTrace.Decode(trace), new UTF8Encoding(false));
                return 1;
            }
        }

        private static void ShortSequence(string mode, int iterations, int seconds, Stopwatch clock)
        {
            string previousDirectory = Environment.GetEnvironmentVariable("CODEX_ISLAND_DATA_DIR");
            string language = UiLocalization.Language, theme = UiTheme.Name;
            string directory = NativeLayoutCrashTrace.WorkspacePath(Path.Combine(Environment.CurrentDirectory, ".runtime", "native-layout-repro-data-" + Guid.NewGuid().ToString("N")));
            IslandWindow window = null;
            try
            {
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", directory);
                new SettingsStore().Save(new Settings { AlwaysOnTop = false, AutoCollapse = false, NotificationsEnabled = false,
                    CustomExpandedSize = true, ExpandedWidth = 440, ExpandedHeight = 720, FavoriteTools = "", HiddenTools = "" });
                window = new IslandWindow(true) { ShowActivated = false };
                window.Loaded += (sender, e) => StopTimers(window);
                window.Show(); Pump(); StopTimers(window);
                Call(window, "SelectToolsPage"); window.SetExpanded(true, false);
                var panels = (Dictionary<string, UIElement>)typeof(IslandWindow).GetField("toolPanels", Private).GetValue(window);
                for (int iteration = 0; iteration < iterations; iteration++)
                {
                    Deadline(clock, seconds); NativeLayoutCrashTrace.Mark("repro.short.before mode=" + mode + ";iteration=" + iteration);
                    if (mode == "content-media") CompactContentStateTests.Run(window, panels);
                    CompactMediaStateTests.Run(window, panels);
                    NativeLayoutCrashTrace.Mark("repro.short.after iteration=" + iteration); Deadline(clock, seconds);
                }
            }
            finally
            {
                if (window != null) { StopTimers(window); window.Close(); }
                AppAppearance.Configure(language, theme);
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", previousDirectory);
                // Preserve the project-local fixture directory for diagnosis.
            }
        }
        private static void Deadline(Stopwatch clock, int seconds)
        {
            if (clock.ElapsedMilliseconds > seconds * 1000L) throw new TimeoutException("Diagnostic iteration deadline elapsed.");
        }
        private static void StopTimers(IslandWindow window)
        {
            foreach (var field in typeof(IslandWindow).GetFields(Private)) if (field.FieldType == typeof(DispatcherTimer)) ((DispatcherTimer)field.GetValue(window)).Stop();
        }
        private static void Pump()
        {
            var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame);
        }
        private static object Call(object value, string method, params object[] args)
        {
            try { return value.GetType().GetMethod(method, Private).Invoke(value, args); }
            catch (TargetInvocationException error) { throw error.InnerException ?? error; }
        }
    }
}
