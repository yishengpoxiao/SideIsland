using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexIsland
{
    internal static class SettingsBackupTests
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        internal static void Run()
        {
            string root = Path.Combine(Path.GetTempPath(), "SideIsland.BackupTests." + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try
            {
                string source = Path.Combine(root, "source"), target = Path.Combine(root, "target"); Directory.CreateDirectory(source); Directory.CreateDirectory(target);
                SeedSource(source); var exporter = new SettingsBackupService(source); SettingsBackupBundle bundle = exporter.Export(); string encoded = SettingsBackupService.Encode(bundle);
                Check(bundle.Files.Count == 8 && !encoded.Contains("CANARY") && !encoded.Contains("CodexPath") && !encoded.Contains("CodexHome") && !encoded.Contains("LaunchAtLogin") && !encoded.Contains("Unknown") && !encoded.Contains("\"Token\"") && !encoded.Contains("ApiToken"), "eight-file export strips account paths, login registration, clipboard/task contents, API/share tokens and unknown fields recursively");
                var layout = Obj(bundle.Files["layout-profiles.json"]);
                Check(layout["Previous"] == null && (bool)layout["FocusRestoreArmed"] == false && (bool)layout["RestoreAfterFocus"], "temporary focus recovery state does not migrate while the user's restore preference remains");
                string exportPath = Path.Combine(root, "settings-export.json"); exporter.ExportTo(exportPath);
                Check(SettingsBackupService.Read(exportPath).Files.Count == 8, "all eight preference files round trip through strict format validation");
                CheckNewPreferences(bundle, "clean exported");
                Throws(() => exporter.ExportTo(Path.Combine(source, "settings.json")), "export cannot overwrite its source settings");
                Throws(() => exporter.ValidateExportPath(Path.Combine(source, "nested", "..", "settings.json")), "normalized managed paths are rejected");
                Throws(() => exporter.ValidateExportPath(Path.Combine(source, "notes.json")), "all application data files are protected from export overwrite");

                SeedTarget(target); var before = Snapshot(target); var importer = new SettingsBackupService(target); importer.Stage(bundle);
                Check(importer.HasPending && SameSnapshot(target, before), "staging changes only the pending import and never live preferences");
                Check(importer.ApplyPending() && !importer.HasPending && !importer.ApplyPending(), "pending imports apply once on the next startup");
                var settings = Obj(File.ReadAllText(Path.Combine(target, "settings.json"), Utf8));
                Check((string)settings["CodexPath"] == @"C:\LocalAccount\Codex.exe" && (string)settings["CodexHome"] == @"C:\LocalAccount\.codex" && (bool)settings["LaunchAtLogin"], "import preserves this machine's account paths and launch-at-login setting");
                Check((string)settings["Theme"] == "dark" && (string)settings["LocalUnknown"] == "keep-local", "preferences import while existing local-only fields remain intact");
                CheckRecovery(importer.RecoveryPath, before);
                Check(Obj(File.ReadAllText(Path.Combine(target, "layout-profiles.json"), Utf8))["Previous"] == null, "temporary layout snapshots never reach imported files");
                var imported = new SettingsBackupBundle(); foreach (string name in new[] { "auto-scene-rules.json", "widget-layouts.json", "task-activity-preferences.json" }) imported.Files[name] = File.ReadAllText(Path.Combine(target, name), Utf8);
                CheckNewPreferences(imported, "restored");
                Check(File.ReadAllText(Path.Combine(target, "task-activities.json"), Utf8).Contains("LOCAL_CANARY_TASK") && File.ReadAllText(Path.Combine(target, "lan-transfer-state.json"), Utf8).Contains("LOCAL_CANARY_TOKEN"), "import does not touch this machine's live task content or transfer access state");
                importer.Stage(bundle); importer.Cancel(); Check(!importer.HasPending, "pending imports can be cancelled without changing live preferences");

                LegacyFiveFileImport(root, bundle); RulesAtModelLimit(root); ValidateBadBundles(root, bundle); FaultRollback(root, bundle); InterruptedReplay(root, bundle);
                Console.WriteLine("PASS: eight-file preferences backup/restore, legacy five-file compatibility, rule/widget/task preferences, token/content exclusion, strict schemas and bounds, staged startup import, final-file failure rollback and interrupted-import recovery.");
            }
            finally { Directory.Delete(root, true); }
        }
        private static void SeedSource(string folder)
        {
            Write(folder, "settings.json", new { Theme = "dark", FocusMinutes = 45, CodexPath = @"C:\CANARY\account.exe", CodexHome = @"C:\CANARY\.codex", LaunchAtLogin = false, Unknown = "CANARY" });
            Write(folder, "desktop-experience.json", new {
                FullscreenMode = "slim", ClickAction = "default", MiddleAction = "search", WheelAction = "default", RememberMonitors = true,
                ToggleHotkey = "Ctrl+Alt+Space", PrivacyHotkey = "Ctrl+Alt+P", SearchHotkey = "Ctrl+Alt+K", CaptureHotkey = "",
                Monitors = new[] { new { Device = "DISPLAY1", Side = "right", X = .5, Y = .4, Free = false, Unknown = "CANARY" } },
                FullscreenRules = new[] { new { ProcessName = "player.exe", Mode = "hide", Unknown = "CANARY" } }, Unknown = "CANARY"
            });
            Write(folder, "layout-profiles.json", new {
                Profiles = new[] { new { Id = "11111111111111111111111111111111", Name = "Coding", Mode = "quota", Slot1 = "codex-secondary", Slot2 = "ports", Tool = "ports", Favorites = "ports,focus", Quiet = false, AutoMusic = false, Unknown = "CANARY" } },
                ActiveId = "focus", RestoreAfterFocus = true, FocusRestoreArmed = true, Previous = new { Secret = "CANARY" }, Unknown = "CANARY"
            });
            Write(folder, "notification-sources.json", new { Version = 2, MergeDuplicates = true, Sources = new[] { new { AppId = "test.app", AppName = "Fixture", Blocked = false, Priority = 1, Body = "CANARY", Unknown = "CANARY" } }, Unknown = "CANARY" });
            Write(folder, "music-preferences.json", new { LyricFontSize = 14, LyricOffsetMilliseconds = 120, PreferredSourceId = "test.player", Unknown = "CANARY" });
            Write(folder, "auto-scene-rules.json", new { Enabled = true, Rules = new[] { new { Id = "22222222222222222222222222222222", Name = "Development", Executable = "Code.exe", Start = "09:00", End = "18:00", Monitor = "DISPLAY1", Power = "ac", ProfileId = "code", Enabled = true, Priority = 77, CooldownSeconds = 45, Unknown = "CANARY", Token = "CANARY" } }, Token = "CANARY", RuntimeContext = new { WindowTitle = "CANARY" } });
            Write(folder, "widget-layouts.json", new { Layouts = new[] { new { ProfileId = "code", Enabled = true, RotateSeconds = 10, NotificationOverride = true, FocusOverride = false, ActivityOverride = true, NotificationPriority = 93, FocusPriority = 83, ActivityPriority = 73, Cards = new[] { new { Id = "33333333333333333333333333333333", Key = "clock", Size = "large", Unknown = "CANARY" }, new { Id = "44444444444444444444444444444444", Key = "codex-secondary", Size = "small", Unknown = "CANARY" } }, Unknown = "CANARY" } }, Token = "CANARY" });
            Write(folder, "task-activity-preferences.json", new { CompactEnabled = false, Token = "CANARY", ApiToken = "CANARY", Activities = new[] { new { Id = "CANARY", Title = "CANARY", Message = "CANARY", Source = "CANARY" } } });
            Write(folder, "clipboard-history.json", new { Text = "CANARY" }); Write(folder, "text-templates.json", new { Text = "CANARY" }); Write(folder, "notes.json", new { Text = "CANARY" });
            Write(folder, "task-activities.json", new { Title = "CANARY", Message = "CANARY" }); Write(folder, "lan-transfer-state.json", new { Token = "CANARY", Files = new[] { "CANARY" } });
        }
        private static void SeedTarget(string folder)
        {
            Directory.CreateDirectory(folder);
            Write(folder, "settings.json", new { Theme = "light", CodexPath = @"C:\LocalAccount\Codex.exe", CodexHome = @"C:\LocalAccount\.codex", LaunchAtLogin = true, LocalUnknown = "keep-local" });
            File.WriteAllText(Path.Combine(folder, "desktop-experience.json"), "{ \"FullscreenMode\": \"off\" }\r\n", Utf8);
            File.WriteAllText(Path.Combine(folder, "auto-scene-rules.json"), "{ \"Enabled\":false, \"Rules\":[] }\r\n", Utf8);
            File.WriteAllText(Path.Combine(folder, "widget-layouts.json"), "{ \"Layouts\":[] }\r\n", Utf8);
            File.WriteAllText(Path.Combine(folder, "task-activity-preferences.json"), "{ \"CompactEnabled\":true }\r\n", Utf8);
            Write(folder, "task-activities.json", new { Title = "LOCAL_CANARY_TASK" }); Write(folder, "lan-transfer-state.json", new { Token = "LOCAL_CANARY_TOKEN" });
        }
        private static void CheckNewPreferences(SettingsBackupBundle bundle, string label)
        {
            var rules = Obj(bundle.Files["auto-scene-rules.json"]); var rule = (Dictionary<string, object>)((object[])rules["Rules"])[0];
            Check((bool)rules["Enabled"] && (string)rule["Executable"] == "Code.exe" && (string)rule["ProfileId"] == "code" && (int)rule["Priority"] == 77 && (int)rule["CooldownSeconds"] == 45 && (string)rule["Start"] == "09:00", label + " automatic rule conditions/actions and user preference remain intact");
            var widgets = Obj(bundle.Files["widget-layouts.json"]); var layout = (Dictionary<string, object>)((object[])widgets["Layouts"])[0]; var cards = (object[])layout["Cards"];
            Check((string)layout["ProfileId"] == "code" && (bool)layout["Enabled"] && (int)layout["RotateSeconds"] == 10 && !(bool)layout["FocusOverride"] && (int)layout["ActivityPriority"] == 73 && cards.Length == 2 && (string)((Dictionary<string, object>)cards[0])["Key"] == "clock" && (string)((Dictionary<string, object>)cards[0])["Size"] == "large" && (string)((Dictionary<string, object>)cards[1])["Key"] == "codex-secondary", label + " widget order, sizes, profile association and priorities remain intact");
            var activity = Obj(bundle.Files["task-activity-preferences.json"]); Check(activity.Count == 1 && activity.ContainsKey("CompactEnabled") && !(bool)activity["CompactEnabled"], label + " task preferences contain only the opted-out compact setting, never task content or credentials");
        }
        private static void LegacyFiveFileImport(string root, SettingsBackupBundle current)
        {
            var old = new SettingsBackupBundle();
            foreach (string name in new[] { "settings.json", "desktop-experience.json", "layout-profiles.json", "notification-sources.json", "music-preferences.json" }) old.Files[name] = current.Files[name];
            SettingsBackupService.Validate(old); string folder = Path.Combine(root, "legacy-five"); SeedTarget(folder);
            var untouched = new[] { "auto-scene-rules.json", "widget-layouts.json", "task-activity-preferences.json" }.ToDictionary(name => name, name => File.ReadAllBytes(Path.Combine(folder, name)));
            string legacyPath = Path.Combine(root, "legacy-v1-five-files.json"); File.WriteAllText(legacyPath, SettingsBackupService.Encode(old), Utf8); var parsed = SettingsBackupService.Read(legacyPath);
            Check(parsed.Version == 1 && parsed.Files.Count == 5, "legacy version-one five-file backups remain accepted without new required entries");
            var service = new SettingsBackupService(folder); service.Stage(parsed); Check(service.ApplyPending(), "legacy backup applies through the real staged startup path");
            Check((string)Obj(File.ReadAllText(Path.Combine(folder, "settings.json"), Utf8))["Theme"] == "dark", "legacy import still restores its old preferences");
            Check(untouched.All(pair => File.ReadAllBytes(Path.Combine(folder, pair.Key)).SequenceEqual(pair.Value)), "legacy five-file import leaves all three newer preferences byte-for-byte intact");
            var recovery = Obj(File.ReadAllText(service.RecoveryPath, Utf8)); Check(((Dictionary<string, object>)recovery["Files"]).Count == 5, "legacy recovery journal covers only its five affected files");
        }
        private static void RulesAtModelLimit(string root)
        {
            string folder = Path.Combine(root, "forty-rules"); Directory.CreateDirectory(folder);
            var rules = Enumerable.Range(1, 40).Select(index => new AutoSceneRule { Id = index.ToString("x32"), Name = "Rule " + index, Executable = "Code.exe", ProfileId = "code" }).ToArray();
            Write(folder, "auto-scene-rules.json", new { Enabled = true, Rules = rules });
            var exported = new SettingsBackupService(folder).Export(); var roundTrip = SettingsBackupService.Encode(exported);
            Check(((object[])Obj(exported.Files["auto-scene-rules.json"])["Rules"]).Length == 40 && roundTrip.Length > 0, "backup accepts the same forty-rule maximum as the automatic-rule store without silently dropping entries");
        }
        private static void ValidateBadBundles(string root, SettingsBackupBundle good)
        {
            foreach (string name in new[] { "../settings.json", "..\\settings.json", "SETTINGS.JSON", @"C:\settings.json", "clipboard-history.json" })
                Throws(() => SettingsBackupService.Validate(Single(name, "{}")), "unsupported or traversing file names are rejected before writes");
            Throws(() => SettingsBackupService.Validate(Single("settings.json", "{\"CodexHome\":\"C:\\\\other\"}")), "import cannot replace local-only account fields");
            foreach (string json in new[] { "{\"FocusMinutes\":\"45\"}", "{\"FocusMinutes\":0}", "{\"FocusMinutes\":45.5}", "{\"AutoMusic\":1}", "{\"Theme\":\"other\"}", "{\"VerticalPosition\":2}", "{\"FavoriteTools\":\"missing-tool\"}" })
                Throws(() => SettingsBackupService.Validate(Single("settings.json", json)), "main settings types and ranges are strict");
            foreach (string json in new[] { "{\"ClickAction\":\"unknown\"}", "{\"ToggleHotkey\":\"Ctrl+Alt+P\",\"PrivacyHotkey\":\"Ctrl+Alt+P\"}", "{\"Monitors\":[{\"Device\":\"DISPLAY1\",\"X\":2}]}", "{\"FullscreenRules\":[{\"ProcessName\":\"C:\\\\app.exe\",\"Mode\":\"hide\"}]}", "{\"Monitors\":{}}" })
                Throws(() => SettingsBackupService.Validate(Single("desktop-experience.json", json)), "desktop nested records, gestures and duplicate hotkeys are validated");
            foreach (string json in new[] { "{\"Version\":2,\"Sources\":\"text\"}", "{\"Version\":2,\"Sources\":[{\"AppId\":\"a\",\"AppName\":\"A\",\"Priority\":\"1\"}]}", "{\"Version\":2,\"Sources\":[{\"AppId\":\"a\",\"AppName\":\"A\",\"Body\":\"secret\"}]}" })
                Throws(() => SettingsBackupService.Validate(Single("notification-sources.json", json)), "notification schemas reject content and wrong collection types");
            foreach (string json in new[] { "{\"LyricFontSize\":17}", "{\"LyricOffsetMilliseconds\":-10001}", "{\"LyricFontSize\":\"14\"}", "{\"PreferredSourceId\":null}", "{\"Lyrics\":\"secret\"}" })
                Throws(() => SettingsBackupService.Validate(Single("music-preferences.json", json)), "music preferences reject wrong types, limits and song content");
            Throws(() => SettingsBackupService.Validate(Single("layout-profiles.json", "{\"Profiles\":[{\"Id\":\"code\",\"Name\":\"collision\"}]}")), "custom profile IDs cannot overwrite built-ins");
            Throws(() => SettingsBackupService.Validate(Single("layout-profiles.json", "{\"Profiles\":null}")), "null profile collections are rejected");
            foreach (string json in new[] { "{\"Enabled\":1,\"Rules\":[]}", "{\"Rules\":[],\"Token\":\"secret\"}", "{\"Enabled\":true,\"Rules\":[{\"Id\":\"22222222222222222222222222222222\",\"Name\":\"Rule\",\"Executable\":\"Code.exe\",\"ProfileId\":\"code\",\"Priority\":101}]}", "{\"Enabled\":true,\"Rules\":[{\"Id\":\"22222222222222222222222222222222\",\"Name\":\"Rule\",\"Executable\":\"Code.exe\",\"ProfileId\":\"code\",\"Token\":\"secret\"}]}" })
                Throws(() => SettingsBackupService.Validate(Single("auto-scene-rules.json", json)), "automatic-rule imports reject wrong types, out-of-range priorities and tokens at every nesting level");
            foreach (string json in new[] { "{\"Layouts\":null}", "{\"Layouts\":[],\"Token\":\"secret\"}", "{\"Layouts\":[{\"ProfileId\":\"code\",\"Cards\":[{\"Id\":\"33333333333333333333333333333333\",\"Key\":\"clock\",\"Size\":\"giant\"},{\"Id\":\"44444444444444444444444444444444\",\"Key\":\"clock\",\"Size\":\"small\"}]}]}" })
                Throws(() => SettingsBackupService.Validate(Single("widget-layouts.json", json)), "widget imports reject invalid collections, private tokens and invalid card sizes");
            foreach (string json in new[] { "{\"CompactEnabled\":\"false\"}", "{\"CompactEnabled\":false,\"Token\":\"secret\"}", "{\"CompactEnabled\":true,\"Activities\":[{\"Title\":\"private content\"}]}" })
                Throws(() => SettingsBackupService.Validate(Single("task-activity-preferences.json", json)), "task imports accept only preference booleans and reject credentials or task bodies");
            Throws(() => SettingsBackupService.Validate(Single("settings.json", new string(' ', SettingsBackupService.MaximumFileBytes + 1))), "individual preference size limit is enforced");
            var future = new SettingsBackupBundle { Version = 2, Files = good.Files }; Throws(() => SettingsBackupService.Validate(future), "unsupported versions are rejected");
            string path = Path.Combine(root, "invalid-bundle.json");
            foreach (string json in new[] { "{}", "{\"Files\":{}}", "{\"Format\":\"SideIsland.Settings\",\"Files\":{}}", "{\"Format\":\"SideIsland.Settings\",\"Version\":1,\"Files\":{},\"Extra\":true}", "{\"Format\":\"SideIsland.Settings\",\"Version\":true,\"Files\":{}}", "{\"Format\":\"SideIsland.Settings\",\"Version\":1,\"Files\":{\"settings.json\":{}}}" })
            { File.WriteAllText(path, json, Utf8); Throws(() => SettingsBackupService.Read(path), "bundle fields are explicitly required with no initializer defaults"); }
            File.WriteAllBytes(path, new byte[SettingsBackupService.MaximumBytes + 1]); Throws(() => SettingsBackupService.Read(path), "bundle byte size is bounded before decoding");
        }
        private static void FaultRollback(string root, SettingsBackupBundle bundle)
        {
            string folder = Path.Combine(root, "rollback"); SeedTarget(folder); var before = Snapshot(folder);
            var touched = new HashSet<string>(StringComparer.Ordinal);
            var service = new SettingsBackupService(folder, (path, bytes) => {
                SettingsBackupService.AtomicWrite(path, bytes);
                touched.Add(Path.GetFileName(path));
                if (Path.GetFileName(path) == "task-activity-preferences.json") throw new IOException("injected final preference commit failure after write");
            });
            Throws(() => service.Apply(bundle), "injected failures are reported");
            Check(touched.Count == 8, "fault is injected after all eight preference files including new rules/widgets/task preferences were written");
            Check(SameSnapshot(folder, before), "all original files restore byte-for-byte and newly created files disappear after a partial commit");
            CheckRecovery(service.RecoveryPath, before);
            new SettingsBackupService(folder).Stage(bundle); Throws(() => service.ApplyPending(), "failed startup import rolls back and retains the pending bundle");
            Check(service.HasPending && File.Exists(service.PendingPath) && SameSnapshot(folder, before), "failed pending import is recoverable and can be retried or cancelled");
            service.Cancel(); Check(!service.HasPending, "a rolled-back pending import can be cancelled");
            Check(!Directory.GetFiles(folder, "*.tmp").Any(), "transaction failures do not leave temporary write files");
        }
        private static void InterruptedReplay(string root, SettingsBackupBundle bundle)
        {
            string folder = Path.Combine(root, "interrupted"); SeedTarget(folder); var before = Snapshot(folder); var service = new SettingsBackupService(folder); service.Apply(bundle);
            var marker = Obj(SettingsBackupService.Encode(bundle)); marker["TransactionId"] = Obj(File.ReadAllText(service.RecoveryPath, Utf8))["TransactionId"];
            File.WriteAllText(Path.Combine(folder, "settings-import-in-progress.json"), new JavaScriptSerializer().Serialize(marker), Utf8);
            File.WriteAllText(Path.Combine(folder, "desktop-experience.json"), "partial interrupted write", Utf8);
            Check(new SettingsBackupService(folder).ApplyPending(), "an interrupted transaction restores original bytes before replaying the staged bundle");
            CheckRecovery(service.RecoveryPath, before);
            var settings = Obj(File.ReadAllText(Path.Combine(folder, "settings.json"), Utf8));
            Check((string)settings["CodexHome"] == @"C:\LocalAccount\.codex" && (string)settings["Theme"] == "dark", "interrupted replay preserves original machine paths and applies imported preferences");
            var complete = Snapshot(folder); marker["TransactionId"] = Guid.NewGuid().ToString("N");
            File.WriteAllText(Path.Combine(folder, "settings-import-in-progress.json"), new JavaScriptSerializer().Serialize(marker), Utf8);
            Throws(() => new SettingsBackupService(folder).ApplyPending(), "a stale recovery copy cannot restore files for a different transaction");
            Check(SameSnapshot(folder, complete), "mismatched transaction recovery performs no writes");
        }
        private static void CheckRecovery(string path, Dictionary<string, byte[]> original)
        {
            var values = (Dictionary<string, object>)Obj(File.ReadAllText(path, Utf8))["Files"];
            foreach (var pair in original) Check(pair.Value == null ? values[pair.Key] == null : Convert.FromBase64String((string)values[pair.Key]).SequenceEqual(pair.Value), "local recovery copy preserves every affected original file including missing-file markers");
        }
        private static SettingsBackupBundle Single(string name, string json) { var bundle = new SettingsBackupBundle(); bundle.Files[name] = json; return bundle; }
        private static Dictionary<string, object> Obj(string json) { return (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json); }
        private static void Write(string folder, string name, object value) { File.WriteAllText(Path.Combine(folder, name), new JavaScriptSerializer().Serialize(value), Utf8); }
        private static Dictionary<string, byte[]> Snapshot(string folder) { return SettingsBackupService.Names.ToDictionary(name => name, name => File.Exists(Path.Combine(folder, name)) ? File.ReadAllBytes(Path.Combine(folder, name)) : null); }
        private static bool SameSnapshot(string folder, Dictionary<string, byte[]> before)
        { return before.All(pair => pair.Value == null ? !File.Exists(Path.Combine(folder, pair.Key)) : File.Exists(Path.Combine(folder, pair.Key)) && File.ReadAllBytes(Path.Combine(folder, pair.Key)).SequenceEqual(pair.Value)); }
        private static void Throws(Action action, string message) { bool thrown = false; try { action(); } catch (ArgumentException) { thrown = true; } catch (IOException) { thrown = true; } catch (InvalidOperationException) { thrown = true; } Check(thrown, message); }
        private static void Check(bool value, string message) { if (!value) throw new Exception("Settings backup: " + message); }
    }
}
