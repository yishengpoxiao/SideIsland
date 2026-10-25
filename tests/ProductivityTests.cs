using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Controls;

namespace CodexIsland
{
    internal static class ProductivityTests
    {
        private sealed class TestClock : IFocusClock
        {
            public DateTime Time = new DateTime(2026, 10, 22, 3, 0, 0, DateTimeKind.Utc);
            public TimeSpan Monotonic = TimeSpan.Zero;
            public DateTime UtcNow { get { return Time; } }
            public TimeSpan Elapsed { get { return Monotonic; } }
            public void Advance(TimeSpan elapsed) { Time = Time.Add(elapsed); Monotonic += elapsed; }
        }

        public static void Run()
        {
            TimerTests();
            PersistenceTests();
            SaveFailureRecoveryTests();
            UnreadableRecoveryTests();
        }

        private static void TimerTests()
        {
            var clock = new TestClock(); var timer = new FocusTimer(clock); int notifications = 0;
            timer.Completed += () => notifications++;
            timer.Start(); clock.Advance(TimeSpan.FromMinutes(7)); timer.Pause();
            Check(timer.Value == TimeSpan.FromMinutes(18), "Pause retains the remaining countdown");
            clock.Advance(TimeSpan.FromHours(3));
            Check(timer.Value == TimeSpan.FromMinutes(18) && !timer.IsRunning, "Time while paused is excluded");
            timer.Start(); clock.Advance(TimeSpan.FromHours(2)); timer.Poll(); timer.Poll(); timer.Pause();
            Check(timer.IsCompleted && !timer.IsRunning && timer.Value == TimeSpan.Zero && notifications == 1, "Resume after suspend emits exactly one completion");
            Check(timer.CompletedFocusSessions == 1, "Completed focus session counted once");
            timer.Start(); Check(timer.Value == TimeSpan.FromMinutes(25), "Restart after completion uses original duration");
            timer.Reset(); clock.Advance(TimeSpan.FromHours(2)); timer.Poll();
            Check(notifications == 1 && timer.Value == TimeSpan.FromMinutes(25), "Reset cancels a pending completion");
            timer.SetCountdown(TimeSpan.FromMinutes(5), "休息", false); timer.Start(); clock.Advance(TimeSpan.FromMinutes(5)); timer.Poll();
            Check(notifications == 2 && timer.CompletedFocusSessions == 1, "Break completion does not count as focused work");
            timer.SetCountdown(TimeSpan.FromMinutes(1), "计时", false); timer.Start(); clock.Advance(TimeSpan.FromMinutes(1)); timer.Pause(); timer.Poll();
            Check(notifications == 3 && timer.IsCompleted, "Pausing exactly at deadline does not lose the alert");
            bool rejected = false;
            try { timer.SetCountdown(TimeSpan.Zero, "计时", false); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Check(rejected, "Empty countdown rejected");
            rejected = false;
            try { timer.SetCountdown(TimeSpan.FromHours(25), "计时", false); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Check(rejected, "Unbounded countdown rejected");
            timer.SetStopwatch(); timer.Start(); clock.Advance(TimeSpan.FromSeconds(12.3));
            clock.Time = clock.Time.AddHours(-4);
            Check(Math.Abs(timer.Value.TotalSeconds - 12.3) < .001, "Stopwatch is independent of wall-clock corrections");
            timer.Pause(); clock.Advance(TimeSpan.FromMinutes(10)); timer.Start(); clock.Advance(TimeSpan.FromSeconds(7.7)); timer.Pause();
            Check(Math.Abs(timer.Value.TotalSeconds - 20) < .001 && notifications == 3, "Stopwatch pauses, resumes, and never sends countdown alerts");
            Check(FocusTimer.Format(TimeSpan.FromMilliseconds(1), false) == "00:01", "Countdown never displays zero before reaching deadline");
            Check(FocusTimer.Format(TimeSpan.FromSeconds(3601), false) == "01:00:01", "Long timers include hours");
        }

        private static void PersistenceTests()
        {
            string directory = Path.Combine(Path.GetTempPath(), "SideIslandProductivityTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string file = Path.Combine(directory, "productivity.json");
            try
            {
                var store = new NotesStore(file); var data = store.Load();
                Check(data.Note == "" && data.Tasks.Count == 0 && !File.Exists(file), "Missing notes use empty defaults without writing");
                data.Note = "测试便签\n包含换行和中文";
                data.Tasks.Add(new NoteTask { Text = "已完成的测试任务", Done = true }); data.Tasks.Add(new NoteTask { Text = "未完成的测试任务" });
                store.Save(data); var restored = store.Load();
                Check(restored.Note == data.Note && restored.Tasks.Count == 2 && restored.Tasks[0].Done && !restored.Tasks[1].Done, "Notes and completion status survive restart");
                data.Note = "第二次保存"; store.Save(data);
                Check(store.Load().Note == data.Note && Directory.GetFiles(directory, "*.tmp").Length == 0, "Atomic replacement leaves no temporary files");
                string[] invalid = { "{broken", "null", "[]", "42" };
                foreach (string text in invalid)
                {
                    File.WriteAllText(file, text); restored = store.Load();
                    Check(restored.Note == "" && restored.Tasks.Count == 0 && store.LoadFailed && File.ReadAllText(file) == text, "Malformed notes are not destroyed while loading");
                }
                File.WriteAllBytes(file, new byte[] { 0xff, 0xfe, 0x00 });
                Check(store.Load().Tasks.Count == 0 && store.LoadFailed, "Invalid UTF-8 is handled safely");
                File.WriteAllText(file, "{\"Note\":123,\"Tasks\":[null,{},1,{\"Text\":\"\"},{\"Text\":\"valid\",\"Done\":\"true\"},{\"Text\":\"done\",\"Done\":true}]}");
                restored = store.Load();
                Check(restored.Note == "" && restored.Tasks.Count == 2 && !restored.Tasks[0].Done && restored.Tasks[1].Done, "Malformed fields cannot create invalid task state");
                data = new ProductivityData { Note = new String('记', NotesStore.MaxNoteLength + 100) };
                for (int i = 0; i < 45; i++) data.Tasks.Add(new NoteTask { Text = new String('事', NotesStore.MaxTaskLength + 10) });
                store.Save(data); restored = store.Load();
                Check(restored.Note.Length == NotesStore.MaxNoteLength && restored.Tasks.Count == NotesStore.MaxTasks && restored.Tasks[0].Text.Length == NotesStore.MaxTaskLength, "Storage bounds prevent unbounded text and task growth");
                data.Note = new String('x', NotesStore.MaxNoteLength - 1) + Char.ConvertFromUtf32(0x1F4DD); store.Save(data);
                Check(store.Load().Note.Length == NotesStore.MaxNoteLength - 1, "Truncation preserves valid Unicode surrogate pairs");
                File.WriteAllText(file, new String('x', 129 * 1024), new UTF8Encoding(false));
                Check(store.Load().Note == "" && store.LoadFailed, "Oversized files rejected before parsing");
            }
            finally { Directory.Delete(directory, true); }
        }

        private static void SaveFailureRecoveryTests()
        {
            string directory = Path.Combine(Path.GetTempPath(), "SideIslandNotesRecoveryTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string previousOverride = Environment.GetEnvironmentVariable("CODEX_ISLAND_DATA_DIR");
            NotesPanel panel = null;
            try
            {
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", directory);
                var store = new NotesStore();
                Check(store.FilePath == Path.Combine(directory, "productivity.json"), "Panel recovery test is isolated from user data");
                store.Save(new ProductivityData { Note = "previous test note", Tasks = new List<NoteTask> { new NoteTask { Text = "preserve this test task", Done = true } } });
                panel = new NotesPanel(false);
                var editor = (TextBox)typeof(NotesPanel).GetField("note", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(panel);
                const string edited = "Edited through the real textbox.\n恢复保存后仍保留这段测试内容。";
                // Permit reads but deny replacing the destination, as with a file held
                // open by another program. TextChanged must retain the pending save.
                using (var locked = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    editor.Text = edited;
                    Check(!panel.TryFlush(), "A failed panel save prevents close from treating edits as persisted");
                    Check(editor.Text == edited, "Failed save keeps the edited text available for backup");
                    Check(store.Load().Note == "previous test note", "Failed atomic replacement preserves the previous saved note");
                    Check(Directory.GetFiles(directory, "*.tmp").Length == 0, "Failed save cleans up temporary output");
                }
                Check(panel.TryFlush(), "Retry succeeds after the file lock is released without another edit");
                ProductivityData restored = new NotesStore(store.FilePath).Load();
                Check(restored.Note == edited && restored.Tasks.Count == 1 && restored.Tasks[0].Done, "Recovered save persists edits and retains existing task state");
                Check(panel.TryFlush(), "A successfully saved panel remains safe to close");
            }
            finally
            {
                if (panel != null) panel.Dispose();
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", previousOverride);
                Directory.Delete(directory, true);
            }
        }

        private static void UnreadableRecoveryTests()
        {
            string directory = Path.Combine(Path.GetTempPath(), "SideIslandNotesCorruptionTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string file = Path.Combine(directory, "productivity.json");
            try
            {
                foreach (string damaged in new[] { "{\"Note\":\"recoverable draft", "{\"Note\":\"valid note\",\"Tasks\":[{\"Text\":\"valid task\",\"Done\":false},{\"Text\":42}]}" })
                {
                    File.WriteAllText(file, damaged, new UTF8Encoding(false));
                    var store = new NotesStore(file); var data = store.Load();
                    Check(store.LoadFailed, "Partial as well as total corruption is surfaced before an edit");
                    int previous = Directory.GetFiles(directory, "*.bak").Length;
                    data.Note = "new edit after a damaged file"; store.Save(data);
                    string[] backups = Directory.GetFiles(directory, "*.bak");
                    Check(backups.Length == previous + 1 && Array.Exists(backups, backup => File.ReadAllText(backup) == damaged), "Editing damaged notes preserves the exact original bytes in a backup");
                    Check(!store.LoadFailed && new NotesStore(file).Load().Note == data.Note, "A repaired edit becomes the active saved note");
                    data.Note = "one more edit"; store.Save(data);
                    Check(Directory.GetFiles(directory, "*.bak").Length == backups.Length, "Normal follow-up saves do not accumulate redundant backups");
                }
            }
            finally { Directory.Delete(directory, true); }
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception("Productivity: " + message);
        }
    }
}
