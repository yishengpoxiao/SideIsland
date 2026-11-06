using System.Text;
using System.Web.Script.Serialization;
using CodexIsland;
using Net.Codecrete.QrCodeGenerator;
using SideIsland.Core;

namespace SideIsland.Tests;
internal static class SharedCoreTests
{
    public static void Run()
    {
        Check(Calculator.Evaluate("2^(3+1)+5*2") == 26, "calculator shared core");
        Check(TextTools.Transform("json-pretty", "{\"number\":12345678901234567890,\"text\":\"中文😀\"}").Contains("12345678901234567890"), "JSON numeric losslessness");
        Check(TextTools.Transform("base64-decode", TextTools.Transform("base64-encode", "中文😀")) == "中文😀", "UTF-8 text roundtrip");
        var diff = TextDiff.Compare("one\ntwo\n", "one\nchanged\nadded\n"); Check(!diff.Identical && diff.Rows.Count > 1, "shared text diff");
        var qr = QrCode.EncodeText("中文😀 https://example.com", QrCode.Ecc.Low); Check(qr.Size >= 21 && qr.GetModule(0, 0), "QR actual matrix"); Check(qr.ToSvgString(4).Contains("<svg"), "portable SVG exporter");
        Check(QrCode.EncodeText(new string('x', 2000), QrCode.Ecc.Low).Size > 100, "maximum-sized single QR remains available");
        var json = new JavaScriptSerializer(); var obj = (IDictionary<string, object>)json.DeserializeObject("{\"int\":1,\"arr\":[2,3],\"null\":null}"); Check(obj["int"] is int && obj["arr"] is object[], "legacy JSON adapter preserves consumer numeric and array types");
        var clock = new Clock(); var timer = new FocusTimer(clock); timer.SetCountdown(TimeSpan.FromMinutes(1), "Focus", true); timer.Start(); clock.Advance(10); Check(timer.Value.TotalSeconds == 50, "monotonic countdown"); timer.Pause(); clock.Advance(20); Check(timer.Value.TotalSeconds == 50, "pause"); timer.Start(); clock.Advance(50); timer.Poll(); Check(timer.IsCompleted && !timer.IsRunning, "completion after resume");
        var hub = new TaskActivityHub(); hub.Publish(new() { Id = "test", Title = "真实任务", Progress = null }); Check(hub.Current.Progress is null, "unknown progress is never fabricated"); hub.Publish(new() { Id = "test", Title = "真实任务", State = "completed" }); Check(hub.Current.Progress == 100, "completion semantics");
        var password = RandomTools.Password(new PasswordOptions { Length = 8 }); Check(password.Length == 8 && password.Any(char.IsUpper) && password.Any(char.IsLower) && password.Any(char.IsDigit) && password.Any(c => RandomTools.Symbols.Contains(c)), "portable OS-backed secure password minimum and groups");
        var folder = Path.Combine(Path.GetTempPath(), "side-island-core-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new PreferenceStore(folder);
            var prefs = new Preferences { Notes = "User text · Edit 中文", HoverDelayMs = -1, ExpandedWidth = double.NaN, Slot1 = "invalid" };
            store.Save(prefs); var restored = store.Load();
            Check(restored.Notes == prefs.Notes && restored.HoverDelayMs == 100 && restored.ExpandedWidth == 480 && restored.Slot1 == "codex-week", "portable preference validation and persistence");
            foreach (string notes in new[] { new string('中', 60000), string.Concat(Enumerable.Repeat("😀", 30000)), new string('x', 60000) })
            {
                prefs.Notes = notes; prefs.Language = "en"; prefs.FocusMinutes = 47; prefs.LightTheme = true;
                store.Save(prefs); restored = new PreferenceStore(folder).Load();
                Check(restored.Notes == notes && restored.Language == "en" && restored.FocusMinutes == 47 && restored.LightTheme, "maximum-length notes survive restart without resetting unrelated preferences");
            }
            foreach (string invalidDock in new[] { "99", "-1", "Top, Bottom", "unknown" })
            {
                prefs.Dock = invalidDock; store.Save(prefs); restored = store.Load();
                Check(restored.Dock == "Auto", "undefined dock values cannot reach native placement");
            }
            byte[] previous = File.ReadAllBytes(store.FilePath); prefs.CodexPath = new string('x', 1024 * 1024 + 1);
            bool rejected = false; try { store.Save(prefs); } catch (InvalidDataException) { rejected = true; }
            Check(rejected && File.ReadAllBytes(store.FilePath).SequenceEqual(previous), "oversized save preserves the last usable file");
            const string malformed = "{\"Notes\":\"recoverable original\", BROKEN";
            File.WriteAllText(store.FilePath, malformed); var recovery = new PreferenceStore(folder); var defaults = recovery.Load();
            recovery.Save(defaults); recovery.Save(defaults);
            var backups = Directory.GetFiles(folder, "preferences.json.unreadable-*.bak");
            Check(backups.Length == 1 && File.ReadAllText(backups[0]) == malformed && new PreferenceStore(folder).Load().FocusMinutes == 25, "unreadable preferences preserved exactly once before recovery save");
            Check(Directory.GetFiles(folder, "*.tmp").Length == 0, "preference writes leave no temporary files");
            string digest = new string('a', 64), resolved, error;
            Check(!FileHashTools.TryResolveExpected(digest + "  download.zip ", "SHA256", "download.zip", out resolved, out error) && error == "file-missing", "manifest trailing filename space is significant and cannot verify a different name");
            Check(FileHashTools.TryResolveExpected(digest + "  download.zip ", "SHA256", "download.zip ", out resolved, out error) && resolved == digest, "manifest preserves exact trailing filename space");
            if (!OperatingSystem.IsWindows())
            {
                string literal = Path.Combine(folder, "literal\\name.txt"); File.WriteAllText(literal, "Unix filename fixture");
                string actual = FileHashTools.ComputeFile(literal, "SHA256", default);
                Check(FileHashTools.TryResolveExpected(actual + "  literal\\name.txt", "SHA256", literal, out resolved, out error) && resolved == actual, "Unix backslash is a literal part of the real selected basename");
                Check(!FileHashTools.TryResolveExpected(actual + "  directory/name.txt", "SHA256", literal, out resolved, out error) && error == "file-missing", "Unix literal-backslash filename cannot match a slash-separated different name");
            }
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        Console.WriteLine("PASS: reused C# calculator/text/diff/QR/focus/task models, modern JSON boundary and isolated preference persistence.");
    }
    private sealed class Clock : IFocusClock { public DateTime UtcNow { get; private set; } = DateTime.UtcNow; public TimeSpan Elapsed { get; private set; } public void Advance(int seconds) { UtcNow = UtcNow.AddSeconds(seconds); Elapsed += TimeSpan.FromSeconds(seconds); } }
    private static void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); }
}
