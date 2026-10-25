using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CodexIsland
{
    // Populates the already isolated demo window with long, real model content.
    // This covers controls absent from empty-state page measurements without
    // invoking file pickers, launching resources, or touching the clipboard.
    internal static class CompactContentStateTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static readonly string[] Keys = { "notes", "reminders", "clipboardhistory", "logwatch", "templates", "scenes", "profiles", "files" };

        internal static void Run(IslandWindow window, Dictionary<string, UIElement> panels)
        {
            NativeLayoutCrashTrace.Mark("content.enter");
            NativeLayoutCrashTrace.MarkWindowState("content.enter", window);
            if (!Read<bool>(window, "demo")) throw new Exception("Compact content fixtures require an isolated demo window.");
            var settings = Read<Settings>(window, "settings");
            int previousWidth = settings.ExpandedWidth;
            string previousTool = Read<string>(window, "selectedTool"), language = UiLocalization.Language, theme = UiTheme.Name;
            var restore = new List<Action>(); var issues = new List<string>(); int scenarios = 0, captions = 0;
            var template = (TextTemplatePanel)panels["templates"];
            var waiting = Stopwatch.StartNew();
            while (!template.PendingLoad.IsCompleted && waiting.ElapsedMilliseconds < 3000) Pump();
            if (!template.PendingLoad.IsCompleted) throw new Exception("Text template fixture did not finish its isolated load.");

            var inputs = Keys.SelectMany(key => Descendants<TextBox>(panels[key])).Distinct().Select(input => new InputState(input)).ToArray();
            bool oldTemplateDirty = Read<bool>(template, "dirty"), oldNotesDirty = Read<bool>(panels["notes"], "dirty");
            string token = "layout-fixture-" + Guid.NewGuid().ToString("N");
            string longName = "中文配置 / Long project name with spaces and extra parameters";
            string longBody = String.Join("\n", Enumerable.Range(0, 36).Select(index => "配置 value_" + index + " = literal 中文; token={date}; " + new string('x', 75)));
            try
            {
                var history = (ClipboardHistoryPanel)panels["clipboardhistory"];
                bool paused = history.Book.Paused; var existingIds = history.Book.Items.Select(item => item.Id).ToArray();
                restore.Add(() => { foreach (var item in history.Book.Items.Where(item => !existingIds.Contains(item.Id)).ToArray()) history.Book.Remove(item.Id); history.Book.Paused = paused; Call(history, "Rebuild"); });
                history.Book.Paused = false;
                Check(history.Book.Add(token + "\n" + longBody, null, "fixture", false), "long clipboard text is accepted");
                history.Book.ToggleFavorite(history.Book.Items.First(item => !item.IsImage).Id);
                Check(history.Book.Add(null, OwnImage(), "fixture", false), "owned clipboard image is accepted");
                history.Book.Paused = paused;
                var favoritesOnly = Read<CheckBox>(history, "favoritesOnly"); bool? oldFavoritesOnly = favoritesOnly.IsChecked;
                restore.Add(() => favoritesOnly.IsChecked = oldFavoritesOnly); favoritesOnly.IsChecked = false; Read<TextBox>(history, "search").Clear(); Call(history, "Rebuild");

                var profiles = (LayoutProfilePanel)panels["profiles"];
                var profileStore = Read<LayoutProfileStore>(profiles, "store"); string oldActive = profileStore.Data.ActiveId;
                var profile = LayoutProfile.Capture(settings, longName); profileStore.Data.Profiles.Add(profile); profileStore.Data.ActiveId = profile.Id;
                restore.Add(() => { profileStore.Data.Profiles.Remove(profile); profileStore.Data.ActiveId = oldActive; Call(profiles, "Rebuild"); });
                Call(profiles, "Rebuild");

                var oldEntries = Read<List<TextTemplateEntry>>(template, "entries"); string oldSelected = Read<string>(template, "selectedId");
                var entry = new TextTemplateEntry { Id = token, Name = longName, Body = longBody, UpdatedUtc = DateTime.UtcNow };
                restore.Add(() => { Write(template, "entries", oldEntries); Write(template, "selectedId", oldSelected); Call(template, "Rebuild"); });
                Write(template, "entries", new List<TextTemplateEntry> { entry }); Write(template, "dirty", false); Call(template, "Select", entry); Call(template, "Rebuild");

                var notes = (NotesPanel)panels["notes"]; var notesData = Read<ProductivityData>(notes, "data"); var oldTasks = notesData.Tasks; string oldNote = notesData.Note;
                restore.Add(() => { notesData.Tasks = oldTasks; notesData.Note = oldNote; Call(notes, "RebuildTasks"); });
                notesData.Tasks = oldTasks.Concat(new[] { new NoteTask { Text = longName + " · " + token }, new NoteTask { Text = "Completed fixture / 已完成的配置校验", Done = true } }).ToList();
                Read<TextBox>(notes, "note").Text = longBody; Call(notes, "RebuildTasks");

                var reminders = (ReminderPanel)panels["reminders"]; var book = Read<ReminderBook>(reminders, "book"); var reminderItems = Read<List<ReminderItem>>(book, "items");
                restore.Add(() => { Write(book, "items", reminderItems); Call(reminders, "Rebuild"); });
                var states = reminderItems.Select(item => item.Copy()).ToList();
                states.Add(new ReminderItem { Id = Guid.NewGuid().ToString("N"), Title = longName, DueUtcTicks = DateTime.UtcNow.AddHours(1).Ticks, Enabled = false, Completed = true });
                states.Add(new ReminderItem { Id = Guid.NewGuid().ToString("N"), Title = "Paused reminder / 已暂停的提醒", DueUtcTicks = DateTime.UtcNow.AddHours(2).Ticks, Enabled = false });
                Write(book, "items", states); Call(reminders, "Rebuild");

                var scenes = (WorkspaceScenePanel)panels["scenes"]; var sceneItems = Read<List<WorkspaceScene>>(scenes, "scenes"); var oldScene = Read<WorkspaceScene>(scenes, "selected"); bool oldSceneDirty = Read<bool>(scenes, "dirty");
                var scene = new WorkspaceScene { Id = token, Name = longName, Resources = new List<WorkspaceSceneResource> {
                    new WorkspaceSceneResource { Name = longName, Kind = "url", Target = "https://example.invalid/" + new string('x', 170), Arguments = "" },
                    new WorkspaceSceneResource { Name = "Project folder / 项目目录", Kind = "folder", Target = "C:\\Fixture\\" + new string('x', 130), Arguments = "" } } };
                sceneItems.Add(scene);
                restore.Add(() => { sceneItems.Remove(scene); Call(scenes, "SelectScene", oldScene); Write(scenes, "dirty", oldSceneDirty); Call(scenes, "RebuildScenes"); });
                Call(scenes, "SelectScene", scene); Call(scenes, "RebuildScenes");
                var import = Descendants<Expander>(scenes).First(); bool importExpanded = import.IsExpanded; import.IsExpanded = true; restore.Add(() => import.IsExpanded = importExpanded);

                var files = (FileShelfPanel)panels["files"]; var metadata = Read<Dictionary<string, FileShelfMetadata>>(files, "metadata"); var file = metadata.Values.First(); string fileName = file.Name, fileStatus = file.Status;
                restore.Add(() => { file.Name = fileName; file.Status = fileStatus; Call(files, "Rebuild"); });
                file.Name = longName + " " + new string('x', 100) + ".json"; file.Status = "Configuration file / 238 KB / Ready for comparison"; Call(files, "Rebuild");

                var log = (LogWatchPanel)panels["logwatch"];
                Read<TextBox>(log, "path").Text = "C:\\Fixture\\" + new string('x', 180) + ".log";
                var logOutput = Read<TextBox>(log, "output"); logOutput.Text = longBody;

                foreach (string testLanguage in new[] { "zh", "en" })
                foreach (string testTheme in new[] { "light", "dark" })
                foreach (int width in new[] { 320, 900 })
                {
                    AppAppearance.Configure(testLanguage, testTheme); settings.ExpandedWidth = width; window.SetExpanded(true, false);
                    foreach (string key in Keys)
                    {
                        Call(window, "SelectTool", key); Settle(window);
                        string context = "populated/" + key + "/" + testLanguage + "/" + testTheme + "/" + width;
                        CheckPage((FrameworkElement)panels[key], context, issues, ref captions); scenarios++;
                        if (key == "clipboardhistory")
                        {
                            if (!Descendants<Image>(history).Any(image => image.Source != null && image.IsVisible)) issues.Add(context + ": image history preview missing");
                            favoritesOnly.IsChecked = true; Settle(window); CheckPage(history, context + "/favorites", issues, ref captions); scenarios++;
                            if (Descendants<Image>(history).Any(image => image.IsVisible)) issues.Add(context + ": favorites filter retained unstarred image");
                            favoritesOnly.IsChecked = false; Settle(window);
                        }
                        if (key == "templates")
                        {
                            if (Read<TextBox>(template, "name").Text != longName || Read<TextBox>(template, "body").Text != longBody) issues.Add(context + ": appearance changed template content");
                            var body = Read<TextBox>(template, "body"); body.ScrollToEnd(); Settle(window); if (body.VerticalOffset <= 0) issues.Add(context + ": populated template editor lost scrolling"); body.ScrollToHome();
                        }
                        if (key == "notes" && Read<TextBox>(notes, "note").Text != longBody) issues.Add(context + ": appearance changed note content");
                        if (key == "logwatch") { logOutput.ScrollToEnd(); Settle(window); if (logOutput.VerticalOffset <= 0) issues.Add(context + ": populated log output lost scrolling"); logOutput.ScrollToHome(); }
                    }
                }
                if (issues.Count > 0) throw new Exception("Populated compact layouts (" + issues.Count + "): " + String.Join("\n", issues.Take(25)));
                Console.WriteLine("PASS: " + scenarios + " populated tool states and " + captions + " captions across narrow/wide windows, Chinese/English and light/dark; long clipboard text/image/favorites, custom active layout, template body/preview, tasks, completed/paused reminders, scene resources and file names preserve content and scrolling.");
            }
            finally
            {
                NativeLayoutCrashTrace.Mark("content.finally.enter previousTool=" + previousTool);
                NativeLayoutCrashTrace.MarkWindowState("content.finally.enter", window);
                NativeLayoutCrashTrace.MarkQrState("content.finally.enter", panels);
                for (int index = restore.Count - 1; index >= 0; index--)
                {
                    NativeLayoutCrashTrace.Mark("content.finally.restore[" + index + "].before");
                    restore[index]();
                    NativeLayoutCrashTrace.Mark("content.finally.restore[" + index + "].after");
                }
                NativeLayoutCrashTrace.Mark("content.finally.template.updating-true.before");
                Write(template, "updating", true);
                NativeLayoutCrashTrace.Mark("content.finally.template.updating-true.after");
                try { for (int index = 0; index < inputs.Length; index++) inputs[index].Restore(index); }
                finally
                {
                    NativeLayoutCrashTrace.Mark("content.finally.template.updating-false.before"); Write(template, "updating", false);
                    NativeLayoutCrashTrace.Mark("content.finally.template.dirty.before"); Write(template, "dirty", oldTemplateDirty);
                    NativeLayoutCrashTrace.Mark("content.finally.notes.dirty.before"); Write(panels["notes"], "dirty", oldNotesDirty);
                    NativeLayoutCrashTrace.Mark("content.finally.dirty.after");
                }
                NativeLayoutCrashTrace.MarkQrState("content.finally.before-appearance", panels);
                NativeLayoutCrashTrace.Mark("content.finally.appearance.before language=" + language + ";theme=" + theme);
                AppAppearance.Configure(language, theme);
                NativeLayoutCrashTrace.Mark("content.finally.appearance.after");
                settings.ExpandedWidth = previousWidth;
                NativeLayoutCrashTrace.MarkQrState("content.finally.before-select-tool", panels);
                NativeLayoutCrashTrace.Mark("content.finally.select-tool.before tool=" + previousTool);
                Call(window, "SelectTool", previousTool);
                NativeLayoutCrashTrace.Mark("content.finally.select-tool.after");
                NativeLayoutCrashTrace.MarkQrState("content.finally.after-select-tool", panels);
                NativeLayoutCrashTrace.Mark("content.finally.expand.before"); window.SetExpanded(true, false);
                NativeLayoutCrashTrace.Mark("content.finally.expand.after");
                NativeLayoutCrashTrace.MarkQrState("content.finally.before-settle", panels);
                Settle(window, "content.finally.settle");
                NativeLayoutCrashTrace.MarkQrState("content.finally.after-settle", panels);
                NativeLayoutCrashTrace.Mark("content.finally.exit");
            }
        }

        private sealed class InputState
        {
            private readonly TextBox input; private readonly string text; private readonly int selectionStart, selectionLength;
            internal InputState(TextBox value) { input = value; text = value.Text; selectionStart = value.SelectionStart; selectionLength = value.SelectionLength; }
            internal void Restore(int index)
            {
                NativeLayoutCrashTrace.Mark("content.finally.input[" + index + "].text.before length=" + text.Length);
                input.Text = text;
                NativeLayoutCrashTrace.Mark("content.finally.input[" + index + "].text.after");
                NativeLayoutCrashTrace.Mark("content.finally.input[" + index + "].select.before start=" + selectionStart + ";length=" + selectionLength);
                input.Select(selectionStart, selectionLength);
                NativeLayoutCrashTrace.Mark("content.finally.input[" + index + "].select.after");
            }
        }
        private static byte[] OwnImage()
        {
            var pixels = new byte[96 * 64 * 4];
            for (int pixel = 0; pixel < pixels.Length; pixel += 4) { pixels[pixel] = 200; pixels[pixel + 1] = (byte)((pixel / 4) % 160); pixels[pixel + 2] = 80; pixels[pixel + 3] = 255; }
            var bitmap = BitmapSource.Create(96, 64, 96, 96, PixelFormats.Bgra32, null, pixels, 96 * 4); bitmap.Freeze(); return ClipboardHistoryImage.Encode(bitmap);
        }
        private static void CheckPage(FrameworkElement page, string context, List<string> issues, ref int captions)
        {
            if (page.ActualWidth < 150 || !page.IsMeasureValid || !page.IsArrangeValid) issues.Add(context + ": layout did not settle");
            foreach (var control in Descendants<Control>(page).Where(control => control.IsVisible && (control is Button || control is TextBox || control is CheckBox || control is ComboBox)))
            {
                Point origin = control.TranslatePoint(new Point(), page);
                if (!InHorizontalScroller(control, page) && (origin.X < -1 || origin.X + control.ActualWidth > page.ActualWidth + 1)) issues.Add(context + ": horizontal overflow " + control.GetType().Name + " " + Caption(control));
                if (control is Button)
                    foreach (var caption in Descendants<TextBlock>(control).Where(text => text.IsVisible))
                    {
                        captions++;
                        if (caption.DesiredSize.Width > caption.ActualWidth + 1 || caption.DesiredSize.Height > caption.ActualHeight + 1) issues.Add(context + ": cropped action " + Caption(control));
                    }
                var input = control as TextBox;
                if (input != null && input.Text.Length > 0 && input.VerticalOffset < .1)
                {
                    Rect glyph = input.GetRectFromCharacterIndex(0);
                    if (!glyph.IsEmpty && (glyph.Top < -1 || glyph.Bottom > input.ActualHeight + 1 || glyph.Height < input.FontSize)) issues.Add(context + ": cropped populated input glyph " + glyph);
                }
            }
            foreach (var grid in Descendants<Grid>(page).Where(grid => grid.IsVisible && grid.GetType().Name == "ActionGrid"))
            {
                var buttons = grid.Children.OfType<Button>().Where(button => button.Visibility != Visibility.Collapsed).ToArray();
                if (buttons.Length == 0) continue;
                foreach (var button in buttons)
                    if (Math.Abs(button.ActualWidth - buttons[0].ActualWidth) > 1 || Math.Abs(button.ActualHeight - buttons[0].ActualHeight) > 1) issues.Add(context + ": unequal populated action size " + Caption(button));
                foreach (var row in buttons.GroupBy(Grid.GetRow))
                {
                    var ordered = row.OrderBy(Grid.GetColumn).ToArray();
                    for (int index = 1; index < ordered.Length; index++)
                    {
                        Point left = ordered[index - 1].TranslatePoint(new Point(), grid), right = ordered[index].TranslatePoint(new Point(), grid);
                        if (Math.Abs(right.X - left.X - ordered[index - 1].ActualWidth - 6) > 1 || Math.Abs(right.Y - left.Y) > 1) issues.Add(context + ": populated action gap/alignment differs");
                    }
                }
            }
        }
        private static bool InHorizontalScroller(DependencyObject item, DependencyObject page)
        {
            for (var ancestor = VisualTreeHelper.GetParent(item); ancestor != null && ancestor != page; ancestor = VisualTreeHelper.GetParent(ancestor))
            { var scroll = ancestor as ScrollViewer; if (scroll != null && scroll.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled && scroll.ExtentWidth > scroll.ViewportWidth + 1) return true; }
            return false;
        }
        private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
        {
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            { var child = VisualTreeHelper.GetChild(root, index); var value = child as T; if (value != null) yield return value; foreach (var descendant in Descendants<T>(child)) yield return descendant; }
        }
        private static string Caption(Control control) { var content = control as ContentControl; return content == null ? "" : Convert.ToString(content.Content); }
        private static void Check(bool condition, string message) { if (!condition) throw new Exception("Compact content fixture: " + message); }
        private static T Read<T>(object value, string field) { return (T)value.GetType().GetField(field, Private).GetValue(value); }
        private static void Write(object value, string field, object item) { value.GetType().GetField(field, Private).SetValue(value, item); }
        private static object Call(object value, string method, params object[] args) { try { return value.GetType().GetMethod(method, Private).Invoke(value, args); } catch (TargetInvocationException error) { throw error.InnerException ?? error; } }
        private static void Settle(IslandWindow window, string trace = null)
        {
            if (trace != null) NativeLayoutCrashTrace.Mark(trace + ".layout1.before"); window.UpdateLayout();
            if (trace != null) NativeLayoutCrashTrace.Mark(trace + ".pump1.before"); Pump();
            if (trace != null) NativeLayoutCrashTrace.Mark(trace + ".layout2.before"); window.UpdateLayout();
            if (trace != null) NativeLayoutCrashTrace.Mark(trace + ".pump2.before"); Pump();
            if (trace != null) NativeLayoutCrashTrace.Mark(trace + ".layout3.before"); window.UpdateLayout();
            if (trace != null) NativeLayoutCrashTrace.Mark(trace + ".after");
        }
        private static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
    }
}
