using System;
using System.IO;
using System.Linq;
using System.Text;

namespace CodexIsland
{
    internal static class SettingsRecoveryTests
    {
        private static int checks;
        internal static void Run()
        {
            string root = Path.Combine(Path.GetTempPath(), "SideIslandSettingsRecovery-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                MissingAndHealthy(root);
                foreach (string malformed in new[] { "{broken", "[]", "null", "42", "\"text\"" })
                    Unreadable(root, Encoding.UTF8.GetBytes(malformed));
                Unreadable(root, new byte[] { 0xFF, 0xFE, 0x00 });
                Unreadable(root, new byte[] { 0xC3, 0x28 });
                TransientLock(root);
                Reload(root);
                Console.WriteLine("PASS: " + checks + " settings recovery checks: unchanged fallback preservation, atomic byte-exact backup, malformed/encoding recovery, failed save retry and normal first-run writes.");
            }
            finally
            {
                string full = Path.GetFullPath(root), parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
                if (Path.GetDirectoryName(full).Equals(parent, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("SideIslandSettingsRecovery-", StringComparison.Ordinal)) Directory.Delete(full, true);
            }
        }
        private static string Fixture(string root)
        { string directory = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); return Path.Combine(directory, "settings.json"); }
        private static string[] Backups(string path) { return Directory.GetFiles(Path.GetDirectoryName(path), "settings.json.unreadable-*.bak"); }
        private static void MissingAndHealthy(string root)
        {
            string path = Fixture(root); var store = new SettingsStore(path); var value = store.Load();
            Check(!store.LoadFailed && !File.Exists(path), "absent first-run file is not classified as unreadable");
            store.Save(value); Check(File.Exists(path) && !store.LoadFailed && Backups(path).Length == 0, "first-run defaults persist normally without recovery backup");
            value.Side = "left"; value.Language = "en"; store.Save(value);
            Check(store.Load().Side == "left" && store.Load().Language == "en" && Backups(path).Length == 0, "healthy loads and replacements retain normal semantics");
        }
        private static void Unreadable(string root, byte[] original)
        {
            string path = Fixture(root); File.WriteAllBytes(path, original); var store = new SettingsStore(path); var fallback = store.Load();
            Check(store.LoadFailed && fallback.Side == "right", "unreadable syntax/type/encoding selects recoverable defaults");
            store.Save(fallback); store.Save(new Settings());
            Check(store.LoadFailed && File.ReadAllBytes(path).SequenceEqual(original) && Backups(path).Length == 0, "ordinary unchanged fallback saves leave every original byte intact");
            fallback.FocusMinutes = 47; store.Save(fallback);
            Check(!store.LoadFailed && Backups(path).Length == 1 && File.ReadAllBytes(Backups(path)[0]).SequenceEqual(original), "first changed save atomically preserves the exact unreadable original");
            Check(store.Load().FocusMinutes == 47, "changed preferences persist after recovery");
            fallback.Theme = "light"; store.Save(fallback);
            Check(Backups(path).Length == 1 && store.Load().Theme == "light" && Directory.GetFiles(Path.GetDirectoryName(path), "*.tmp").Length == 0, "later healthy edits do not create redundant backups or temporary debris");
        }
        private static void TransientLock(string root)
        {
            string path = Fixture(root);
            byte[] original = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(" { \"Side\" : \"left\", \"Language\" : \"en\", \"Theme\" : \"light\", \"FocusMinutes\" : 47 }\r\n")).ToArray();
            File.WriteAllBytes(path, original); var store = new SettingsStore(path); Settings fallback;
            using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                fallback = store.Load(); Check(store.LoadFailed, "exclusive temporary lock is recorded as a failed load");
                store.Save(fallback);
                fallback.HoverDelayMilliseconds = 1250; bool failed = false;
                try { store.Save(fallback); } catch (IOException) { failed = true; } catch (UnauthorizedAccessException) { failed = true; }
                Check(failed && store.LoadFailed && Directory.GetFiles(Path.GetDirectoryName(path), "*.tmp").Length == 0, "changed save while still locked fails safely and retains recovery state");
            }
            Check(File.ReadAllBytes(path).SequenceEqual(original) && Backups(path).Length == 0, "failed locked save does not alter or partially back up original");
            fallback.HoverDelayMilliseconds = new Settings().HoverDelayMilliseconds; store.Save(fallback);
            Check(File.ReadAllBytes(path).SequenceEqual(original) && store.LoadFailed, "unchanged fallback after unlock does not erase previously unread settings");
            fallback.HoverDelayMilliseconds = 1250; store.Save(fallback);
            Check(Backups(path).Length == 1 && File.ReadAllBytes(Backups(path)[0]).SequenceEqual(original) && store.Load().HoverDelayMilliseconds == 1250, "retry after unlock preserves BOM, whitespace and original settings before changed save");
        }
        private static void Reload(string root)
        {
            string path = Fixture(root); File.WriteAllText(path, "{broken"); var store = new SettingsStore(path); store.Load();
            File.WriteAllText(path, "{\"Side\":\"left\",\"Language\":\"en\"}"); var value = store.Load();
            Check(!store.LoadFailed && value.Side == "left", "successful explicit reload clears stale failure state");
            value.FocusMinutes = 33; store.Save(value); Check(Backups(path).Length == 0, "saving a successfully reloaded file does not require recovery");
            byte[] second = Encoding.UTF8.GetBytes("{second invalid fixture"); File.WriteAllBytes(path, second); value = store.Load(); value.FocusMinutes = 34; store.Save(value);
            Check(Backups(path).Length == 1 && File.ReadAllBytes(Backups(path)[0]).SequenceEqual(second), "later independent corruption is preserved rather than reusing stale recovery state");
        }
        private static void Check(bool value, string label) { checks++; if (!value) throw new Exception("Settings recovery: " + label); }
    }
}
