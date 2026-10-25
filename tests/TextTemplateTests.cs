using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Threading;

namespace CodexIsland
{
    internal static class TextTemplateTests
    {
        internal static void Run()
        {
            Renderer(); Storage(); Panel();
            Console.WriteLine("PASS: text-template exact date tokens, unknown token preservation, bounded atomic local storage/corrupt backup, preview/search/edit/delete and explicit fake clipboard only.");
        }
        private static TextTemplateEntry Entry(string name, string body)
        { return new TextTemplateEntry { Id = Guid.NewGuid().ToString("N"), Name = name, Body = body, UpdatedUtc = new DateTime(2026, 10, 24, 1, 0, 0, DateTimeKind.Utc) }; }
        private static void Renderer()
        {
            var now = new DateTime(2026, 10, 24, 9, 7, 6, DateTimeKind.Local);
            string input = "{date}|{time}|{datetime}|{yyyy}/{MM}/{dd} {HH}:{mm}:{ss}|{user}|{{date}}|{Date}|中文 😀";
            string expected = "2026-10-24|09:07:06|2026-10-24 09:07:06|2026/10/24 09:07:06|{user}|{{date}}|{Date}|中文 😀";
            Check(TextTemplateRenderer.Render(input, now) == expected, "Known date/time tokens render invariantly while unknown and doubled braces remain unchanged");
            Check(TextTemplateRenderer.Render("{ MM } {unknown} {\"json\": true}", now) == "{ MM } {unknown} {\"json\": true}", "Ordinary configuration, JSON and unmatched placeholders stay intact");
        }
        private static void Storage()
        {
            string folder = Path.Combine(Path.GetTempPath(), "SideIsland.TextTemplates." + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            try
            {
                string path = Path.Combine(folder, "templates.json"); var store = new TextTemplateStore(false, path); var first = Entry("问候", "你好 😀 {date}");
                store.Save(new[] { first }); var loaded = new TextTemplateStore(false, path).Load();
                Check(loaded.Count == 1 && loaded[0].Body == first.Body && loaded[0].Id == first.Id, "Unicode, body tokens and stable template identity survive a restart");
                first.Body = "updated"; store.Save(new[] { first }); Check(store.Load()[0].Body == "updated" && Directory.GetFiles(folder, "*.tmp").Length == 0, "Atomic replacement leaves no temporary files");
                bool rejected = false;
                try { store.Save(new[] { first, Entry("问候", "duplicate") }); } catch (ArgumentException) { rejected = true; }
                Check(rejected && store.Load().Count == 1, "Duplicate names cannot overwrite an existing template");
                rejected = false; try { store.Save(new[] { Entry("large", new String('x', TextTemplateStore.MaximumBodyLength + 1)) }); } catch (ArgumentException) { rejected = true; }
                Check(rejected, "Individual body length is bounded");
                rejected = false; try { store.Save(Enumerable.Range(0, 51).Select(index => Entry("template " + index, "x"))); } catch (ArgumentException) { rejected = true; }
                Check(rejected, "Template count is bounded");
                rejected = false; try { store.Save(Enumerable.Range(0, 40).Select(index => Entry("multibyte " + index, new String('字', 12000)))); } catch (ArgumentException) { rejected = true; }
                Check(rejected && store.Load().Count == 1, "Aggregate UTF-8 byte capacity is bounded independently of character counts");
                File.WriteAllText(Path.Combine(folder, "templates.json.corrupt-keep.bak"), "unrelated file");
                for (int index = 0; index < 5; index++)
                {
                    string corrupt = "broken original " + index; File.WriteAllText(path, corrupt);
                    Check(store.Load().Count == 0 && store.LoadFailed && File.ReadAllText(path) == corrupt, "Corrupt templates preserve the original during loading");
                    store.Save(new[] { first });
                }
                var backups = Directory.GetFiles(folder, "templates.json.corrupt-*.bak").Where(file => !file.EndsWith("corrupt-keep.bak", StringComparison.Ordinal)).ToArray();
                Check(backups.Length == 3 && backups.Any(file => File.ReadAllText(file) == "broken original 4") && File.Exists(Path.Combine(folder, "templates.json.corrupt-keep.bak")), "Corrupt originals are backed up with a bounded three-file history without touching unrelated names");
                File.WriteAllBytes(path, new byte[TextTemplateStore.MaximumStoreBytes + 1]); Check(store.LoadFailed == false, "Previous successful save cleared the corrupt marker");
                Check(store.Load().Count == 0 && store.LoadFailed, "Oversized template files do not enter memory");
                bool preserved = false; try { store.Save(new[] { first }); } catch (IOException) { preserved = new FileInfo(path).Length == TextTemplateStore.MaximumStoreBytes + 1; }
                Check(preserved, "Oversized corrupt originals are preserved rather than overwritten or copied without a bound");
                string unused = Path.Combine(folder, "memory-only.json"); var memory = new TextTemplateStore(true, unused); memory.Save(new[] { first });
                Check(memory.Load().Count == 1 && !File.Exists(unused), "Demo storage stays in memory");
            }
            finally { Directory.Delete(folder, true); }
        }
        private static void Panel()
        {
            var previous = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try
            {
                var store = new TextTemplateStore(true); string copied = null; int copies = 0; DateTime now = new DateTime(2026, 10, 24, 9, 7, 6);
                using (var panel = new TextTemplatePanel(false, store, text => { copies++; copied = text; }, () => now))
                {
                    Pump(panel.PendingLoad); Check(copies == 0, "Opening templates never changes the clipboard");
                    Field<TextBox>(panel, "name").Text = "Greeting"; Field<TextBox>(panel, "body").Text = "Hello world {time} {unknown}";
                    Check(Field<TextBox>(panel, "preview").Text == "Hello world 09:07:06 {unknown}" && copies == 0 && panel.HasUnsavedChanges, "Editing updates preview with no automatic copy/paste");
                    int previewChanges = 0; Field<TextBox>(panel, "preview").TextChanged += (s, e) => previewChanges++;
                    typeof(TextTemplatePanel).GetMethod("UpdatePreview", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(panel, null);
                    Check(previewChanges == 0 && !Field<DispatcherTimer>(panel, "previewTimer").IsEnabled, "Unchanged preview does not rewrite text and an unloaded template panel does not run a timer");
                    var saved = panel.SaveCurrentAsync(); Pump(saved); Check(saved.Result && panel.TemplateNames.Single() == "Greeting" && !panel.HasUnsavedChanges, "Explicit save exposes the named template to commands");
                    now = now.AddMinutes(1); Check(panel.FindRendered("greeting") == "Hello world 09:08:06 {unknown}", "Command lookup renders fresh time and matches template names case-insensitively");
                    Check(panel.CopyTemplate("Greeting") && copied == "Hello world 09:08:06 {unknown}" && copies == 1, "Only an explicit command writes rendered text through the fake clipboard adapter");
                    Field<TextBox>(panel, "search").Text = "missing"; Check(Field<StackPanel>(panel, "list").Children[0] is TextBlock, "Search hides unmatched templates");
                    Field<TextBox>(panel, "search").Text = "world";
                    var matchedNames = Field<StackPanel>(panel, "list").Children.OfType<Panel>().SelectMany(row => row.Children.OfType<Button>()).Where(button => Convert.ToString(button.Content) == "Greeting").ToArray();
                    Check(matchedNames.Length == 1, "Search also matches saved body text and exposes the matching named action");
                    var deleted = panel.DeleteCurrentAsync(); Pump(deleted); Check(deleted.Result && !panel.TemplateNames.Any() && !panel.CopyTemplate("Greeting") && copies == 1, "Deletion persists and removed names cannot copy stale text");
                }
                store.Save(new[] { Entry("Demo", "demo") });
                using (var demo = new TextTemplatePanel(true, store, text => copies++, () => now)) { Pump(demo.PendingLoad); Check(!demo.CopyTemplate("Demo") && copies == 1, "Demo refuses clipboard changes even when a template is present"); }
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }
        private static T Field<T>(object value, string name) { return (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value); }
        private static void Pump(Task task)
        {
            var watch = Stopwatch.StartNew();
            while (!task.IsCompleted && watch.ElapsedMilliseconds < 4000)
            {
                var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); Thread.Sleep(1);
            }
            Check(task.IsCompleted, "Template fake operation completes"); task.GetAwaiter().GetResult();
        }
        private static void Check(bool value, string message) { if (!value) throw new Exception("Text templates: " + message); }
    }
}
