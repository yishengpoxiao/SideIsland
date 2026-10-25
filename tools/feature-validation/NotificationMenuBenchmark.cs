using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace CodexIsland
{
    // Production notification-card construction only. Never Show(), pump input,
    // open a menu, start Application.Run(), or launch an external operation.
    internal static class NotificationMenuBenchmark
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
        [STAThread]
        private static int Main(string[] args)
        {
            string previous = Environment.GetEnvironmentVariable("CODEX_ISLAND_DATA_DIR"), language = UiLocalization.Language, theme = UiTheme.Name;
            IslandWindow window = null; string output = null;
            try
            {
                var manifest = VerifyBuild(); string project = (string)manifest["ProjectRoot"];
                int count = 80, rounds = 5;
                for (int index = 0; index < args.Length; index += 2) {
                    Check(index + 1 < args.Length, "Each argument requires a value.");
                    if (args[index] == "--output") output = Path.GetFullPath(Path.IsPathRooted(args[index + 1]) ? args[index + 1] : Path.Combine(project, args[index + 1]));
                    else if (args[index] == "--count") count = Int32.Parse(args[index + 1], CultureInfo.InvariantCulture);
                    else if (args[index] == "--rounds") rounds = Int32.Parse(args[index + 1], CultureInfo.InvariantCulture);
                    else throw new ArgumentException("Use --output artifacts/subdirectory [--count 40|80] [--rounds 1..10].");
                }
                Check(output != null && (count == 40 || count == 80) && rounds >= 1 && rounds <= 10, "Specify an artifact subdirectory, count 40 or 80, and 1..10 rounds.");
                string prefix = Path.GetFullPath(Path.Combine(project, "artifacts")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                Check(output.StartsWith(prefix, StringComparison.OrdinalIgnoreCase), "Output must remain under project artifacts.");
                for (string cursor = output; cursor != null && cursor.Length >= project.Length; cursor = Path.GetDirectoryName(cursor))
                    if (Directory.Exists(cursor)) Check((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) == 0, "No reparse output path.");
                Check(!Directory.Exists(output) && !File.Exists(output), "Use a fresh output directory."); Directory.CreateDirectory(output);
                string data = Path.Combine(output, "data"); Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", data);
                new SettingsStore().Save(new Settings { Language = "zh", Theme = "dark", AutoCollapse = false, HoverExpandEnabled = false, AlwaysOnTop = false, NotificationsEnabled = false, AutoMusic = false, OnlineLyricsEnabled = false });
                window = new IslandWindow(true) { ShowActivated = false, IsHitTestVisible = false, Topmost = false };
                Check(new WindowInteropHelper(window).Handle == IntPtr.Zero, "Benchmark must not create an HWND.");
                var list = (ListBox)typeof(IslandWindow).GetField("notificationList", Private).GetValue(window);
                var factory = typeof(IslandWindow).GetMethod("NotificationCard", Private);
                var results = new List<object>();
                using (var writer = new StreamWriter(Path.Combine(output, "benchmark.jsonl"), false, new UTF8Encoding(false))) {
                    writer.AutoFlush = true;
                    for (int round = 0; round < rounds; round++) {
                        list.Items.Clear(); AppAppearance.Configure("zh", "dark");
                        var messages = Enumerable.Range(0, count).Select(index => new IslandNotification { Id = "menu-benchmark-" + round + "-" + index,
                            AppId = "fixture.menu." + index % 3, AppName = "Synthetic menu source", Title = "Synthetic title " + index,
                            Body = "Synthetic text " + new string('x', 160), IsDemo = true, CreatedAt = new DateTime(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc).AddSeconds(index) }).ToArray();
                        long beforeBytes = GC.GetTotalMemory(false); int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
                        var cards = new List<Border>(); var create = Stopwatch.StartNew();
                        foreach (var message in messages) { var card = (Border)factory.Invoke(window, new object[] { message }); cards.Add(card); list.Items.Add(card); }
                        create.Stop(); object created = Counters(cards);
                        var appearance = Stopwatch.StartNew(); AppAppearance.Configure("en", "light"); appearance.Stop();
                        object refreshed = Counters(cards);
                        Check(new WindowInteropHelper(window).Handle == IntPtr.Zero, "Appearance refresh must not create an HWND.");
                        var record = new { Round = round + 1, ColdRound = round == 0, CardCount = count, CreateMilliseconds = create.Elapsed.TotalMilliseconds,
                            UnopenedAppearanceMilliseconds = appearance.Elapsed.TotalMilliseconds, Created = created, AfterAppearance = refreshed,
                            ManagedBytesDelta = GC.GetTotalMemory(false) - beforeBytes, Gen0Collections = GC.CollectionCount(0) - gen0,
                            Gen1Collections = GC.CollectionCount(1) - gen1, Gen2Collections = GC.CollectionCount(2) - gen2, NativeWindowCreated = false };
                        results.Add(record); writer.WriteLine(Json.Serialize(record));
                        Console.WriteLine("ROUND " + (round + 1) + ": cards=" + count + " create=" + create.Elapsed.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture) + "ms appearance=" + appearance.Elapsed.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture) + "ms " + Json.Serialize(refreshed));
                    }
                }
                File.WriteAllText(Path.Combine(output, "summary.json"), Json.Serialize(new { Status = "passed", Utc = DateTime.UtcNow.ToString("o"),
                    ExecutableSHA256 = manifest["ExecutableSHA256"], ProductionSources = manifest["ProductionSources"], CardCount = count, Rounds = rounds,
                    NativeWindowCreated = false, MenusOpened = false, ExternalActionsStarted = false, Results = results }), new UTF8Encoding(false));
                Console.WriteLine("PASS: production notification menu construction; no HWND/menu/global input/external action."); return 0;
            }
            catch (Exception error) {
                Console.Error.WriteLine(error);
                if (output != null && Directory.Exists(output)) File.WriteAllText(Path.Combine(output, "failure.txt"), error.ToString(), new UTF8Encoding(false));
                return 1;
            }
            finally {
                if (window != null) window.Close();
                AppAppearance.Configure(language, theme); Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", previous);
            }
        }
        private static object Counters(IEnumerable<Border> cards)
        {
            var menus = cards.Where(card => card.ContextMenu != null).Select(card => card.ContextMenu).ToArray();
            return new { AttachedMenus = menus.Length, MenuItems = menus.Sum(menu => menu.Items.Count), StyleDictionaries = menus.Sum(menu => menu.Resources.MergedDictionaries.Count), MenuResourceEntries = menus.Sum(menu => menu.Resources.Count) };
        }
        private static IDictionary<string, object> VerifyBuild()
        {
            string exe = Assembly.GetExecutingAssembly().Location;
            var manifest = Json.DeserializeObject(File.ReadAllText(Path.Combine(Path.GetDirectoryName(exe), "notification-menu-build.json"), new UTF8Encoding(false, true))) as IDictionary<string, object>;
            Check(manifest != null && (string)manifest["EntryPoint"] == "CodexIsland.NotificationMenuBenchmark", "Invalid build identity.");
            CheckHash(exe, (string)manifest["ExecutableSHA256"]); CheckHash(exe + ".config", (string)manifest["RuntimeConfigSHA256"]);
            var sources = ((object[])manifest["ProductionSources"]).Cast<IDictionary<string, object>>().ToArray();
            string[] current = Directory.GetFiles(Path.Combine((string)manifest["ProjectRoot"], "src"), "*.cs").Select(Path.GetFullPath).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            Check(sources.Length > 0 && current.SequenceEqual(sources.Select(x => (string)x["Path"]).OrderBy(x => x, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase), "Production inventory changed; rebuild.");
            foreach (var item in sources.Concat(((object[])manifest["HarnessInputs"]).Cast<IDictionary<string, object>>())) CheckHash((string)item["Path"], (string)item["SHA256"]);
            return manifest;
        }
        private static void CheckHash(string path, string expected)
        {
            using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create()) Check(String.Equals(BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", ""), expected, StringComparison.OrdinalIgnoreCase), "Build input changed; rebuild: " + path);
        }
        private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
