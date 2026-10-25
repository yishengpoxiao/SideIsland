using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace CodexIsland
{
    internal sealed class NoteTask
    {
        public string Text = "";
        public bool Done;
    }

    internal sealed class ProductivityData
    {
        public string Note = "";
        public List<NoteTask> Tasks = new List<NoteTask>();
    }

    internal sealed class NotesStore
    {
        internal const int MaxTasks = 30, MaxTaskLength = 160, MaxNoteLength = 6000;
        private const int MaxFileBytes = 128 * 1024;
        internal string FilePath { get; private set; }
        internal bool LoadFailed { get; private set; }
        private bool unreadablePreserved;
        public NotesStore(string path = null)
        {
            FilePath = Path.GetFullPath(path ?? Path.Combine(Path.GetDirectoryName(new SettingsStore().FilePath), "productivity.json"));
        }

        public ProductivityData Load()
        {
            LoadFailed = false; unreadablePreserved = false;
            try
            {
                if (!File.Exists(FilePath)) return new ProductivityData();
                if (new FileInfo(FilePath).Length > MaxFileBytes) { LoadFailed = true; return new ProductivityData(); }
                var serializer = new JavaScriptSerializer { MaxJsonLength = MaxFileBytes, RecursionLimit = 8 };
                var root = serializer.DeserializeObject(File.ReadAllText(FilePath, new UTF8Encoding(false, true))) as IDictionary<string, object>;
                if (root == null) { LoadFailed = true; return new ProductivityData(); }
                var result = new ProductivityData(); object value;
                if (root.TryGetValue("Note", out value))
                {
                    if (value is string) { result.Note = Bound((string)value, MaxNoteLength); if (result.Note.Length != ((string)value).Length) LoadFailed = true; }
                    else LoadFailed = true;
                }
                if (root.TryGetValue("Tasks", out value))
                {
                    IEnumerable list = value as IEnumerable;
                    if (list != null && !(value is string) && !(value is IDictionary))
                        foreach (object entry in list)
                        {
                            var task = entry as IDictionary<string, object>; object title, done;
                            if (task == null || !task.TryGetValue("Text", out title) || !(title is string)) { LoadFailed = true; continue; }
                            string text = Bound(((string)title).Trim(), MaxTaskLength);
                            if (text.Length == 0 || result.Tasks.Count >= MaxTasks) { LoadFailed = true; continue; }
                            if (text.Length != ((string)title).Trim().Length) LoadFailed = true;
                            bool hasDone = task.TryGetValue("Done", out done);
                            if (hasDone && !(done is bool)) LoadFailed = true;
                            result.Tasks.Add(new NoteTask { Text = text, Done = hasDone && done is bool && (bool)done });
                        }
                    else LoadFailed = true;
                }
                return result;
            }
            catch (IOException) { LoadFailed = true; }
            catch (UnauthorizedAccessException) { LoadFailed = true; }
            catch (ArgumentException) { LoadFailed = true; }
            catch (InvalidOperationException) { LoadFailed = true; }
            return new ProductivityData();
        }

        public void Save(ProductivityData data)
        {
            if (data == null) throw new ArgumentNullException("data");
            var safe = new ProductivityData { Note = Bound(data.Note ?? "", MaxNoteLength) };
            if (data.Tasks != null)
                foreach (var task in data.Tasks)
                {
                    if (task == null || String.IsNullOrWhiteSpace(task.Text)) continue;
                    safe.Tasks.Add(new NoteTask { Text = Bound(task.Text.Trim(), MaxTaskLength), Done = task.Done });
                    if (safe.Tasks.Count == MaxTasks) break;
                }
            string json = new JavaScriptSerializer().Serialize(safe);
            string directory = Path.GetDirectoryName(FilePath);
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, ".productivity-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(json + Environment.NewLine);
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                // Recovery must retain the damaged original before the first edit replaces it.
                if (LoadFailed && !unreadablePreserved && File.Exists(FilePath))
                {
                    File.Copy(FilePath, FilePath + ".unreadable-" + Guid.NewGuid().ToString("N") + ".bak", false);
                    unreadablePreserved = true;
                }
                if (File.Exists(FilePath)) File.Replace(temporary, FilePath, null);
                else File.Move(temporary, FilePath);
                LoadFailed = false;
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    try { File.Delete(temporary); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        private static string Bound(string value, int length)
        {
            if (value.Length <= length) return value;
            // Avoid truncating between UTF-16 surrogate halves.
            if (Char.IsHighSurrogate(value[length - 1])) length--;
            return value.Substring(0, length);
        }
    }

    internal sealed class NotesPanel : StackPanel, IDisposable
    {
        internal NotificationTasksPanel NotificationTasks { get; private set; }
        private Expander notificationTasksExpander;
        private readonly NotesStore store;
        private readonly ProductivityData data;
        private readonly bool demo;
        private readonly DispatcherTimer saveTimer = new DispatcherTimer();
        private readonly TextBox newTask, note;
        private readonly TextBlock count, saved, noteCount, taskHint;
        private readonly StackPanel tasks;
        private bool dirty;

        public NotesPanel(bool demoMode)
        {
            demo = demoMode;
            store = new NotesStore();
            data = demo ? new ProductivityData { Note = "演示便签：在这里随手记下灵感。\n演示内容不会保存。", Tasks = new List<NoteTask> { new NoteTask { Text = "演示：完成今天最重要的一件事" }, new NoteTask { Text = "演示：休息时喝一杯水", Done = true } } } : store.Load();
            Children.Add(ToolStyle.Heading("待办与便签"));
            var intro = ToolStyle.Hint(demo ? "演示内容 · 修改不会保存" : "仅保存在这台电脑；输入后自动保存。"); Children.Add(intro);
            var heading = new Grid(); heading.ColumnDefinitions.Add(new ColumnDefinition()); heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            count = IslandWindow.Text("待办", 12, "#B9D5C9", true); count.VerticalAlignment = VerticalAlignment.Center; heading.Children.Add(count);
            var clear = PanelActionLayout.Caption(IslandWindow.Button("清除已完成", () => { if (data.Tasks.RemoveAll(x => x.Done) > 0) { RebuildTasks(); ScheduleSave(); } })); clear.Padding = new Thickness(8, 5, 8, 5); clear.MaxWidth = 130; clear.Margin = new Thickness(6, 0, 0, 0); Grid.SetColumn(clear, 1); heading.Children.Add(clear); Children.Add(heading);
            var addRow = new Grid { Margin = new Thickness(0, 6, 0, 4) }; addRow.ColumnDefinitions.Add(new ColumnDefinition()); addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) }); addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            newTask = Editor(false); newTask.MaxLength = NotesStore.MaxTaskLength; newTask.ToolTip = "输入待办，按 Enter 添加（最多 30 项）"; addRow.Children.Add(newTask);
            var add = PanelActionLayout.Caption(IslandWindow.Button("添加", AddTask)); add.MinWidth = 54; Grid.SetColumn(add, 2); addRow.Children.Add(add); Children.Add(addRow);
            newTask.KeyDown += (s, e) => { if (e.Key == Key.Enter) { AddTask(); e.Handled = true; } };
            taskHint = IslandWindow.Text("Enter 添加 · 勾选完成 · × 删除", 10, "#748A7E"); taskHint.TextWrapping = TextWrapping.Wrap; taskHint.Margin = new Thickness(0, 0, 0, 6); Children.Add(taskHint);
            tasks = new StackPanel(); Children.Add(tasks);
            Children.Add(new Border { Height = 1, Background = Display.Brush("#2B3933"), Margin = new Thickness(0, 8, 0, 8) });
            var noteHeader = new Grid(); noteHeader.ColumnDefinitions.Add(new ColumnDefinition()); noteHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            noteHeader.Children.Add(IslandWindow.Text("随手记", 12, "#B9D5C9", true));
            noteCount = IslandWindow.Text("", 10, "#748A7E"); noteCount.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(noteCount, 1); noteHeader.Children.Add(noteCount); Children.Add(noteHeader);
            note = Editor(true); note.Text = data.Note; note.Height = 128; note.MaxLength = NotesStore.MaxNoteLength; note.Margin = new Thickness(0, 6, 0, 6); note.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; note.ToolTip = "便签仅本机保存，最多 6000 字符"; Children.Add(note);
            note.TextChanged += (s, e) => { data.Note = note.Text; UpdateNoteCount(); ScheduleSave(); };
            saved = IslandWindow.Text(demo ? "演示模式 · 不写入文件" : store.LoadFailed ? "原文件部分或全部无法读取；保存修改前会先保留原文件备份。" : "自动保存已启用", 10, store.LoadFailed ? "#E9BA83" : "#748A7E"); saved.TextWrapping = TextWrapping.Wrap; Children.Add(saved);
            var actions = PanelActionLayout.Create(1, IslandWindow.Button("立即保存", Flush)); actions.Margin = new Thickness(0, 6, 0, 0); Children.Add(actions);
            saveTimer.Interval = TimeSpan.FromMilliseconds(700); saveTimer.Tick += (s, e) => { saveTimer.Stop(); Flush(); };
            NotificationTasks = new NotificationTasksPanel(demo);
            notificationTasksExpander = new Expander { Header = "通知待办", Content = NotificationTasks, Margin = new Thickness(0, 8, 0, 0), FontSize = 12 };
            Children.Add(notificationTasksExpander);
            RebuildTasks(); UpdateNoteCount();
        }

        private static TextBox Editor(bool multiline)
        {
            return new TextBox { FontSize = 12, MinHeight = 32, Padding = new Thickness(8, 6, 8, 6), Background = Display.Brush("#192220"), Foreground = Display.Brush("#E0ECE5"), BorderBrush = Display.Brush("#34473D"), BorderThickness = new Thickness(1), AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center, CaretBrush = Display.Brush("#A1EACF"), SelectionBrush = Display.Brush("#3C6B56") };
        }

        private void AddTask()
        {
            string text = newTask.Text.Trim();
            if (text.Length == 0) { taskHint.Text = "先写下要做的事。"; return; }
            if (data.Tasks.Count >= NotesStore.MaxTasks) { taskHint.Text = "已到 30 项上限，请先清理已完成的待办。"; return; }
            data.Tasks.Add(new NoteTask { Text = text }); newTask.Clear();
            taskHint.Text = "Enter 添加 · 勾选完成 · × 删除"; RebuildTasks(); ScheduleSave(); newTask.Focus();
        }

        private void RebuildTasks()
        {
            tasks.Children.Clear();
            int remaining = data.Tasks.Count(x => !x.Done);
            count.Text = "待办 " + remaining + " / " + data.Tasks.Count;
            if (data.Tasks.Count == 0)
            {
                var empty = IslandWindow.Text("写下一件小事，从这里开始。", 11, "#7E9588"); empty.Margin = new Thickness(0, 9, 0, 9); tasks.Children.Add(empty); return;
            }
            foreach (NoteTask task in data.Tasks)
            {
                NoteTask current = task;
                var row = new Grid { Margin = new Thickness(0, 0, 0, 6) }; row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var title = IslandWindow.Text(task.Text, 12, task.Done ? "#799184" : "#D6E5DB", false, false); title.TextWrapping = TextWrapping.Wrap; title.VerticalAlignment = VerticalAlignment.Center;
                if (task.Done) title.TextDecorations = TextDecorations.Strikethrough;
                var check = UiLocalization.Protect(new CheckBox { Content = title, IsChecked = task.Done, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(4, 5, 4, 5), Margin = new Thickness(0, 0, 5, 0), Foreground = Display.Brush("#B9D5C9"), HorizontalContentAlignment = HorizontalAlignment.Stretch });
                check.Checked += (s, e) => { current.Done = true; title.TextDecorations = TextDecorations.Strikethrough; title.Foreground = Display.Brush("#799184"); UpdateTaskCount(); ScheduleSave(); };
                check.Unchecked += (s, e) => { current.Done = false; title.TextDecorations = null; title.Foreground = Display.Brush("#D6E5DB"); UpdateTaskCount(); ScheduleSave(); };
                row.Children.Add(check);
                var remove = IslandWindow.Button("×", () => { data.Tasks.Remove(current); RebuildTasks(); ScheduleSave(); }); remove.ToolTip = "删除这项待办"; remove.Padding = new Thickness(8, 3, 8, 3); remove.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(remove, 1); row.Children.Add(remove);
                tasks.Children.Add(row);
            }
        }

        private void UpdateTaskCount() { count.Text = "待办 " + data.Tasks.Count(x => !x.Done) + " / " + data.Tasks.Count; }
        private void UpdateNoteCount() { noteCount.Text = data.Note.Length + " / " + NotesStore.MaxNoteLength; }
        private void ScheduleSave()
        {
            if (demo) { saved.Text = "演示模式 · 修改不会保存"; return; }
            dirty = true; saved.Text = "正在编辑…"; saved.Foreground = Display.Brush("#748A7E"); saveTimer.Stop(); saveTimer.Start();
        }

        public void Flush()
        {
            saveTimer.Stop();
            if (demo || !dirty) return;
            try { store.Save(data); dirty = false; saved.Text = "已保存 · " + DateTime.Now.ToString("HH:mm:ss"); saved.Foreground = Display.Brush("#A1CDB8"); }
            catch (IOException) { SaveFailed(); }
            catch (UnauthorizedAccessException) { SaveFailed(); }
        }

        public bool TryFlush() { Flush(); return demo || !dirty; }

        private void SaveFailed() { saved.Text = "保存失败，内容仍在内存中。请稍后点“立即保存”。"; saved.Foreground = Display.Brush("#E9BA83"); }
        internal void OpenNotificationTasks() { notificationTasksExpander.IsExpanded = true; NotificationTasks.BringIntoView(); }
        public void Dispose() { Flush(); saveTimer.Stop(); if (NotificationTasks != null) NotificationTasks.Dispose(); }
    }
}
