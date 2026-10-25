using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;

namespace CodexIsland
{
    // A normal, taskbar-visible demo window for manual/CUA visual validation.
    // Only startup navigation is reflected; subsequent input uses production UI.
    internal static class FeatureVisualGallery
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
        private static readonly object LogGate = new object();
        private static StreamWriter log;
        private static string output;

        [STAThread]
        private static int Main(string[] args)
        {
            string previousData = Environment.GetEnvironmentVariable("CODEX_ISLAND_DATA_DIR");
            try
            {
                if (args.Length != 2 || args[0] != "--output") throw new ArgumentException("Use --output <fresh project artifacts subdirectory>.");
                var manifest = VerifyBuild();
                string project = ReadString(manifest, "ProjectRoot");
                string artifactRoot = Path.GetFullPath(Path.Combine(project, "artifacts"));
                output = Path.GetFullPath(Path.IsPathRooted(args[1]) ? args[1] : Path.Combine(project, args[1]));
                RequireChild(output, artifactRoot);
                RejectReparseParents(output, project);
                Directory.CreateDirectory(output);
                string data = Path.Combine(output, "data");
                if (Directory.Exists(data) || File.Exists(data)) throw new IOException("A fresh isolated data directory is required.");
                log = new StreamWriter(new FileStream(Path.Combine(output, "visual-gallery.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", data);
                new SettingsStore().Save(new Settings {
                    AutoCollapse = false, HoverExpandEnabled = false, AlwaysOnTop = false,
                    Theme = "light", Language = "zh", LastTool = "autorules",
                    CustomExpandedSize = true, ExpandedWidth = 590, ExpandedHeight = 750,
                    NotificationsEnabled = false, AutoMusic = false, OnlineLyricsEnabled = false,
                    LaunchAtLogin = false
                });
                Write("starting", new { Demo = true, DataDirectory = data, ProductionSourceCount = ReadArray(manifest, "ProductionSources").Length, ExternalActionsStarted = false });
                StartupDiagnostics.Begin(true);
                var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                app.DispatcherUnhandledException += (s, e) => {
                    Write("dispatcher-failure", new { Error = e.Exception.ToString() });
                    e.Handled = true; Environment.ExitCode = 1; app.Shutdown(1);
                };
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Write("unhandled-failure", new { Error = Convert.ToString(e.ExceptionObject, CultureInfo.InvariantCulture) });
                var window = new IslandWindow(true) {
                    ShowInTaskbar = true, ShowActivated = true, Topmost = false,
                    Title = "Side Island 1.13 UI validation"
                };
                app.MainWindow = window;
                window.Loaded += (s, e) => {
                    InvokeInitialNavigation(window, "SelectToolsPage");
                    InvokeInitialNavigation(window, "SelectTool", "autorules");
                    window.SetExpanded(true, false);
                    window.Topmost = false;
                    Write("loaded", new { window.Title, window.ShowInTaskbar, window.ShowActivated, window.Topmost, window.Width, window.Height });
                };
                window.ContentRendered += (s, e) => Write("content-rendered", new { window.ActualWidth, window.ActualHeight });
                window.StateChanged += (s, e) => Write("window-state", new { State = window.WindowState.ToString() });
                window.Closed += (s, e) => Write("closed", new { ExitCode = Environment.ExitCode });
                int exit = app.Run(window);
                Write("application-exit", new { ExitCode = exit });
                return exit;
            }
            catch (Exception error)
            {
                Write("startup-failure", new { Error = error.ToString() });
                Console.Error.WriteLine(error);
                return 1;
            }
            finally
            {
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", previousData);
                lock (LogGate) { if (log != null) { log.Dispose(); log = null; } }
            }
        }

        private static void InvokeInitialNavigation(IslandWindow window, string name, params object[] arguments)
        {
            var method = typeof(IslandWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (method == null) throw new MissingMethodException(typeof(IslandWindow).FullName, name);
            method.Invoke(window, arguments);
        }

        private static IDictionary<string, object> VerifyBuild()
        {
            string executable = Assembly.GetExecutingAssembly().Location;
            string manifestPath = Path.Combine(Path.GetDirectoryName(executable), "visual-gallery-build.json");
            var manifest = Json.DeserializeObject(File.ReadAllText(manifestPath, new UTF8Encoding(false, true))) as IDictionary<string, object>;
            if (manifest == null || ReadString(manifest, "EntryPoint") != "CodexIsland.FeatureVisualGallery") throw new InvalidDataException("Missing visual validation build identity.");
            CheckHash(executable, ReadString(manifest, "ExecutableSHA256"));
            string project = Path.GetFullPath(ReadString(manifest, "ProjectRoot"));
            string sourceRoot = Path.Combine(project, "src");
            var compiled = ReadArray(manifest, "ProductionSources").Select(item => item as IDictionary<string, object>).ToArray();
            if (compiled.Length == 0 || compiled.Any(item => item == null)) throw new InvalidDataException("Missing production source list.");
            string[] current = Directory.GetFiles(sourceRoot, "*.cs", SearchOption.TopDirectoryOnly).Select(Path.GetFullPath).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
            string[] recorded = compiled.Select(item => Path.GetFullPath(ReadString(item, "Path"))).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
            if (!current.SequenceEqual(recorded, StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("Production source inventory changed; rebuild the gallery.");
            foreach (var item in compiled) CheckHash(ReadString(item, "Path"), ReadString(item, "SHA256"));
            foreach (object value in ReadArray(manifest, "HarnessInputs")) {
                var item = value as IDictionary<string, object>;
                if (item == null) throw new InvalidDataException("Invalid harness input identity.");
                CheckHash(ReadString(item, "Path"), ReadString(item, "SHA256"));
            }
            CheckHash(executable + ".config", ReadString(manifest, "RuntimeConfigSHA256"));
            return manifest;
        }

        private static string ReadString(IDictionary<string, object> data, string key)
        {
            object value;
            if (!data.TryGetValue(key, out value) || !(value is string) || String.IsNullOrWhiteSpace((string)value)) throw new InvalidDataException("Missing manifest field: " + key);
            return (string)value;
        }
        private static object[] ReadArray(IDictionary<string, object> data, string key)
        {
            object value;
            if (!data.TryGetValue(key, out value) || !(value is object[])) throw new InvalidDataException("Missing manifest array: " + key);
            return (object[])value;
        }
        private static void CheckHash(string path, string expected)
        {
            string actual;
            using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create()) actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
            if (!String.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Build input changed; rebuild the gallery: " + path);
        }
        private static void RequireChild(string path, string parent)
        {
            string prefix = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Output must be a subdirectory of project artifacts.");
        }
        private static void RejectReparseParents(string path, string project)
        {
            string current = path, stop = Path.GetFullPath(project).TrimEnd(Path.DirectorySeparatorChar);
            while (current != null) {
                if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Output must not pass through a reparse point.");
                if (String.Equals(current.TrimEnd(Path.DirectorySeparatorChar), stop, StringComparison.OrdinalIgnoreCase)) return;
                current = Path.GetDirectoryName(current);
            }
            throw new ArgumentException("Output must remain inside the project.");
        }
        private static void Write(string kind, object value)
        {
            lock (LogGate) {
                if (log == null) return;
                log.WriteLine(Json.Serialize(new { Utc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), Event = kind, Details = value }));
            }
        }
    }
}
