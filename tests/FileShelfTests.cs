using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CodexIsland
{
    internal static class FileShelfTests
    {
        public static void Run()
        {
            PathTests(); StoreTests(); SaveFailureRecoveryTests(); UnreadableBackupTests();
        }

        private static void PathTests()
        {
            string path, error;
            Check(FileShelfPaths.TryNormalize("C:/Work/sub/../计划.txt", out path, out error) && path == "C:\\Work\\计划.txt", "Local references normalize separators and dot segments");
            Check(FileShelfPaths.TryNormalize("C:\\Work\\", out path, out error) && path == "C:\\Work", "Directory reference trailing separator is normalized");
            Check(FileShelfPaths.TryNormalize("C:\\", out path, out error) && path == "C:\\", "Drive roots retain their root separator");
            foreach (string invalid in new[] { "", "  ", "relative.txt", "C:relative.txt", "https://example.test/file", "file:///C:/file", "\\\\server\\share\\file.txt", "//server/share/file.txt", "\\\\?\\C:\\file", "C:\\bad|name", "C:\\a.txt:stream", "C:\\a*", "C:\\folder \\file", "C:\\name.", "C:\\NUL.txt", "C:\\COM1", "C:\\a\nb", "C:\\" + new String('x', 260) })
                Check(!FileShelfPaths.TryNormalize(invalid, out path, out error), "Unsupported or ambiguous path rejected");
            foreach (string extension in new[] { ".EXE", ".cmd", ".Ps1", ".lnk", ".url", ".msi", ".reg", ".js", ".py", ".pyw", ".ahk", ".unknown", ".zip", "" })
                Check(!FileShelfPaths.CanOpenFile("C:\\test" + extension), "Only document/media allowlisted extensions can be opened directly");
            foreach (string extension in new[] { ".pdf", ".PNG", ".docx", ".xlsx", ".pptx", ".txt", ".mp3", ".mp4" })
                Check(FileShelfPaths.CanOpenFile("C:\\资料" + extension), "Supported document and media extensions remain openable");
            Check(!FileShelfPaths.CanOpenFile("C:\\report.pdf.exe"), "A document-looking executable remains locate-only");
            Check(FileShelfPaths.FormatSize(0) == "0 B" && FileShelfPaths.FormatSize(1024) == "1 KB" && FileShelfPaths.FormatSize(1048576) == "1 MB", "File size labels use expected units");
        }

        private static void StoreTests()
        {
            string directory = CreateDirectory(); string file = Path.Combine(directory, "file-shelf.json");
            try
            {
                var store = new FileShelfStore(file); var shelf = new FileShelf(store, false); string error;
                Check(shelf.Entries.Count == 0 && !shelf.Dirty && shelf.TryFlush() && !File.Exists(file), "Empty startup never creates a storage file");
                string document = Path.Combine(directory, "document.txt"), folder = Path.Combine(directory, "collection");
                File.WriteAllText(document, "Original content"); Directory.CreateDirectory(folder);
                Check(shelf.Add(document, out error) && shelf.Add(folder, out error), "Files and folders can be referenced");
                Check(!shelf.Add(document.ToUpperInvariant(), out error) && !shelf.Add(folder + "\\", out error), "References deduplicate case and directory aliases");
                Check(shelf.TryFlush() && !shelf.Dirty, "Successful save clears pending changes");
                var restored = new FileShelf(store, false);
                Check(restored.Entries.SequenceEqual(shelf.Entries) && !restored.Dirty, "References survive restart in insertion order");
                FileShelfMetadata info = FileShelfPaths.Inspect(document);
                Check(info.Available && info.CanOpen && !info.IsDirectory && info.Size == new FileInfo(document).Length, "Metadata reads only file properties");
                Check(FileShelfPaths.Inspect(folder).IsDirectory, "Directory metadata does not require listing its contents");
                foreach (string extension in new[] { ".py", ".ahk", ".unknown", "" })
                {
                    string restricted = Path.Combine(directory, "reference" + extension); File.WriteAllText(restricted, "inert test fixture");
                    var restrictedInfo = FileShelfPaths.Inspect(restricted);
                    Check(restrictedInfo.Available && !restrictedInfo.CanOpen, "Present but unlisted files remain locate-only after metadata refresh");
                }
                restored.Remove(document); restored.Clear(); Check(restored.TryFlush(), "Clearing references persists");
                Check(File.ReadAllText(document) == "Original content" && Directory.Exists(folder), "Remove and clear never modify source files or directories");
                string missing = Path.Combine(directory, "missing.txt");
                Check(restored.Add(missing, out error) && restored.TryFlush() && store.Load().Contains(missing), "Unavailable paths remain references for later reconnection");
                Check(!FileShelfPaths.Inspect(missing).Available, "Missing references report unavailable");
                restored.Clear();
                for (int i = 0; i < FileShelfStore.MaxEntries; i++) Check(restored.Add(Path.Combine(directory, i + ".txt"), out error), "Reference accepted below the storage bound");
                Check(!restored.Add(Path.Combine(directory, "extra.txt"), out error) && restored.Entries.Count == 30, "Maximum count is bounded at 30");
                Check(restored.TryFlush() && Directory.GetFiles(directory, "*.tmp").Length == 0, "Atomic save cleans its temporary file");
                foreach (string invalid in new[] { "{broken", "null", "{}", "42" })
                {
                    File.WriteAllText(file, invalid); var broken = new FileShelf(store, false);
                    Check(broken.LoadFailed && broken.Entries.Count == 0 && broken.TryFlush() && File.ReadAllText(file) == invalid, "Invalid storage is not overwritten merely by loading or flushing");
                }
                File.WriteAllText(file, "[null,{},1,\"C:\\\\valid.txt\",\"c:\\\\VALID.txt\",\"relative.txt\"]");
                Check(store.Load().Count == 1, "Loaded entries enforce valid paths and case-insensitive deduplication");
                File.WriteAllText(file, new String('x', 65537), new UTF8Encoding(false));
                Check(store.Load().Count == 0 && store.LoadFailed, "Oversized storage is rejected before parsing");
                File.WriteAllBytes(file, new byte[] { 0xff, 0xfe, 0x00 });
                Check(store.Load().Count == 0 && store.LoadFailed, "Invalid UTF-8 is handled without overwriting it");
                string before = Convert.ToBase64String(File.ReadAllBytes(file));
                var demo = new FileShelf(store, true); demo.Add(document, out error); Check(demo.TryFlush(), "Demo collection can change in memory");
                Check(Convert.ToBase64String(File.ReadAllBytes(file)) == before, "Demo never reads or writes existing persisted references");
            }
            finally { Directory.Delete(directory, true); }
        }

        private static void SaveFailureRecoveryTests()
        {
            string directory = CreateDirectory(); string file = Path.Combine(directory, "file-shelf.json");
            try
            {
                var store = new FileShelfStore(file); var shelf = new FileShelf(store, false); string error;
                shelf.Add(Path.Combine(directory, "first.txt"), out error); Check(shelf.TryFlush(), "Initial storage save succeeds");
                byte[] original = File.ReadAllBytes(file); shelf.Add(Path.Combine(directory, "second.txt"), out error);
                using (var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                    Check(!shelf.TryFlush() && shelf.Dirty && shelf.Entries.Count == 2, "Replacement failure retains unsaved references for retry");
                Check(File.ReadAllBytes(file).SequenceEqual(original) && Directory.GetFiles(directory, "*.tmp").Length == 0, "Failed replacement preserves previous storage and removes temporary writes");
                Check(shelf.TryFlush() && !shelf.Dirty && store.Load().Count == 2, "A later retry saves the retained change");
                string blocker = Path.Combine(directory, "blocked"); File.WriteAllText(blocker, "not a folder");
                var blocked = new FileShelf(new FileShelfStore(Path.Combine(blocker, "file-shelf.json")), false); blocked.Add(Path.Combine(directory, "third.txt"), out error);
                Check(!blocked.TryFlush() && blocked.Dirty, "An unavailable storage directory leaves changes pending");
                File.Delete(blocker); Check(blocked.TryFlush() && !blocked.Dirty, "Storage recovers after the blocking condition is removed");
            }
            finally { Directory.Delete(directory, true); }
        }

        private static void UnreadableBackupTests()
        {
            string directory = CreateDirectory(); string file = Path.Combine(directory, "file-shelf.json");
            try
            {
                const string unreadable = "{broken original content";
                File.WriteAllText(file, unreadable); var store = new FileShelfStore(file); var shelf = new FileShelf(store, false); string error;
                shelf.Add(Path.Combine(directory, "first.txt"), out error);
                using (var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                    Check(!shelf.TryFlush() && shelf.Dirty, "An unreadable original cannot be replaced while locked");
                string[] backups = Directory.GetFiles(directory, "file-shelf.json.unreadable-*.bak");
                Check(backups.Length == 1 && File.ReadAllText(backups[0]) == unreadable && File.ReadAllText(file) == unreadable, "Original unreadable data is backed up before a replacement attempt");
                Check(shelf.TryFlush() && !shelf.Dirty && !shelf.LoadFailed, "A later retry can save after preserving the bad original");
                Check(Directory.GetFiles(directory, "file-shelf.json.unreadable-*.bak").Length == 1 && File.ReadAllText(backups[0]) == unreadable, "Retry does not duplicate or overwrite the preserved backup");
                shelf.Add(Path.Combine(directory, "second.txt"), out error); Check(shelf.TryFlush(), "Further edits save normally after recovery");
                Check(store.Load().Count == 2 && File.ReadAllText(backups[0]) == unreadable, "Recovery preserves both the new collection and the bad-data backup");
                const string partial = "[\"C:\\\\valid.txt\",null,\"relative\"]";
                File.WriteAllText(file, partial); var partialShelf = new FileShelf(new FileShelfStore(file), false);
                Check(partialShelf.LoadFailed && partialShelf.Entries.Count == 1, "A partially invalid collection is marked for backup while usable references remain");
                partialShelf.Add(Path.Combine(directory, "third.txt"), out error); Check(partialShelf.TryFlush(), "Editing a partial collection preserves its original first");
                Check(Directory.GetFiles(directory, "file-shelf.json.unreadable-*.bak").Any(p => File.ReadAllText(p) == partial), "A partially unreadable original also receives an intact backup");
            }
            finally { Directory.Delete(directory, true); }
        }

        private static string CreateDirectory() { string directory = Path.Combine(Path.GetTempPath(), "SideIslandFileShelfTests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); return directory; }
        private static void Check(bool condition, string message) { if (!condition) throw new Exception("File shelf: " + message); }
    }
}
