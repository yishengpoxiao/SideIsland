using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace CodexIsland
{
    internal sealed class BatchRenameOptions
    {
        internal string Template = "{name}_{n}", Find = "", Replace = "";
        internal int Start = 1, Padding = 3;
    }
    internal sealed class BatchFileStamp
    {
        internal string FileId, Hash;
        internal long Length, LastWrite;
        internal bool Same(BatchFileStamp other) { return other != null && FileId == other.FileId && Hash == other.Hash && Length == other.Length && LastWrite == other.LastWrite; }
    }
    internal sealed class BatchRenameEntry
    {
        internal string OriginalPath, DestinationPath;
        internal BatchFileStamp Stamp;
        internal bool Changed { get { return !String.Equals(OriginalPath, DestinationPath, StringComparison.OrdinalIgnoreCase); } }
    }
    internal sealed class BatchRenamePlan
    {
        internal readonly List<BatchRenameEntry> Entries;
        internal BatchRenamePlan(List<BatchRenameEntry> entries) { Entries = entries; }
        internal int ChangedCount { get { return Entries.Count(entry => entry.Changed); } }
    }
    internal sealed class BatchRenameTransaction
    {
        internal readonly List<BatchRenameEntry> Entries;
        internal bool Undone { get; private set; }
        internal BatchRenameTransaction(List<BatchRenameEntry> entries) { Entries = entries; }
        internal void Undo(CancellationToken token, IProgress<int> progress)
        {
            if (Undone) throw new InvalidOperationException("这次重命名已撤销。");
            BatchFiles.Rename(Entries, true, token, progress); Undone = true;
        }
    }

    internal static class BatchFiles
    {
        internal const int MaximumFiles = 200;
        [StructLayout(LayoutKind.Sequential)] private struct NativeTime { internal uint Low, High; internal long Value { get { return ((long)High << 32) | Low; } } }
        [StructLayout(LayoutKind.Sequential)] private struct FileInformation
        { internal uint Attributes; internal NativeTime Created, Accessed, Written; internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
        [StructLayout(LayoutKind.Sequential)] private struct RenameHeader { internal byte Replace; internal IntPtr Root; internal uint Length; internal ushort FirstCharacter; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, IntPtr information, uint bytes);
        private sealed class Locked : IDisposable
        {
            internal readonly FileStream Stream;
            internal readonly BatchRenameEntry Entry;
            internal readonly string From, To, Temporary;
            internal int Stage;
            internal Locked(BatchRenameEntry entry, bool undo)
            {
                Entry = entry; From = undo ? entry.DestinationPath : entry.OriginalPath; To = undo ? entry.OriginalPath : entry.DestinationPath;
                Temporary = Path.Combine(Path.GetDirectoryName(From), ".side-island-rename-" + Guid.NewGuid().ToString("N") + ".tmp");
                // DELETE access permits a handle-based rename. Excluding sharing
                // of writes/deletes prevents an editor or replacement race while
                // validating and moving the exact opened file.
                var handle = CreateFile(From, 0x80010000, 1, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
                try { Stream = new FileStream(handle, FileAccess.Read); }
                catch { handle.Dispose(); throw; }
            }
            internal void Move(string path)
            {
                byte[] name = Encoding.Unicode.GetBytes(path); int offset = (int)Marshal.OffsetOf(typeof(RenameHeader), "FirstCharacter"); int size = checked(offset + name.Length + 2);
                IntPtr buffer = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.Copy(new byte[size], 0, buffer, size);
                    Marshal.WriteIntPtr(buffer, (int)Marshal.OffsetOf(typeof(RenameHeader), "Root"), IntPtr.Zero);
                    Marshal.WriteInt32(buffer, (int)Marshal.OffsetOf(typeof(RenameHeader), "Length"), name.Length);
                    Marshal.Copy(name, 0, IntPtr.Add(buffer, offset), name.Length);
                    // ReplaceIfExists remains FALSE. This never overwrites even
                    // if another program creates a destination after preview.
                    if (!SetFileInformationByHandle(Stream.SafeFileHandle, 3, buffer, (uint)size)) throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            public void Dispose() { Stream.Dispose(); }
        }
        internal static List<string> NormalizePaths(IEnumerable<string> input)
        {
            if (input == null) throw new ArgumentException("请先选择文件。");
            var paths = input.Where(path => !String.IsNullOrWhiteSpace(path)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (paths.Count == 0 || paths.Count > MaximumFiles) throw new ArgumentException("请选择 1–200 个文件。");
            foreach (string path in paths)
            {
                if (path.StartsWith(@"\\", StringComparison.Ordinal) || !File.Exists(path) || (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                    throw new ArgumentException("仅支持现有本机普通文件，不处理文件夹、链接或网络路径。");
            }
            return paths;
        }
        internal static BatchRenamePlan Preview(IEnumerable<string> input, BatchRenameOptions options, CancellationToken token, IProgress<int> progress)
        {
            if (options == null || String.IsNullOrWhiteSpace(options.Template) || options.Template.Length > 160 || options.Start < 0 || options.Padding < 1 || options.Padding > 9)
                throw new ArgumentException("请填写有效模板、起始编号和 1–9 位编号宽度。");
            if (options.Template.Replace("{name}", "").Replace("{n}", "").IndexOfAny(new[] { '{', '}' }) >= 0) throw new ArgumentException("模板仅支持 {name} 和 {n}。");
            var paths = NormalizePaths(input); var entries = new List<BatchRenameEntry>();
            for (int index = 0; index < paths.Count; index++)
            {
                token.ThrowIfCancellationRequested(); string path = paths[index], originalName = Path.GetFileNameWithoutExtension(path);
                if (!String.IsNullOrEmpty(options.Find)) originalName = originalName.Replace(options.Find, options.Replace ?? "");
                string next = options.Template.Replace("{name}", originalName).Replace("{n}", checked(options.Start + index).ToString("D" + options.Padding, CultureInfo.InvariantCulture)) + Path.GetExtension(path);
                ValidateName(next); string target = Path.Combine(Path.GetDirectoryName(path), next);
                if (target.Length >= 260) throw new ArgumentException("输出路径过长，请缩短文件名。");
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) entries.Add(new BatchRenameEntry { OriginalPath = path, DestinationPath = target, Stamp = Stamp(stream, token) });
                if (progress != null) progress.Report((index + 1) * 100 / paths.Count);
            }
            CheckDestinations(entries, false); return new BatchRenamePlan(entries);
        }
        internal static void ValidateName(string name)
        {
            if (String.IsNullOrWhiteSpace(name) || name.Length > 255 || name == "." || name == ".." || name.EndsWith(".") || name.EndsWith(" ") || name.Any(Char.IsControl) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException("输出文件名包含无效字符、结尾空格或点。");
            string stem = name.Split('.')[0].ToUpperInvariant();
            if (new[] { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$" }.Contains(stem) || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] >= '0' && stem[3] <= '9')) throw new ArgumentException("输出文件名属于 Windows 保留名称。");
        }
        private static void CheckDestinations(IEnumerable<BatchRenameEntry> values, bool undo)
        {
            var entries = values.Where(entry => entry.Changed).ToList();
            var sources = new HashSet<string>(entries.Select(entry => undo ? entry.DestinationPath : entry.OriginalPath), StringComparer.OrdinalIgnoreCase);
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                string target = undo ? entry.OriginalPath : entry.DestinationPath;
                if (!targets.Add(target)) throw new ArgumentException("预览存在重复目标文件名，请修改模板。");
                if (Directory.Exists(target) || (File.Exists(target) && !sources.Contains(target))) throw new IOException("目标文件已存在，不会覆盖，请修改模板。");
            }
        }
        internal static BatchRenameTransaction Execute(BatchRenamePlan plan, CancellationToken token, IProgress<int> progress)
        {
            if (plan == null || plan.ChangedCount == 0) throw new ArgumentException("请先生成包含实际变化的重命名预览。");
            Rename(plan.Entries, false, token, progress); return new BatchRenameTransaction(plan.Entries.ToList());
        }
        internal static void Rename(IEnumerable<BatchRenameEntry> values, bool undo, CancellationToken token, IProgress<int> progress)
        {
            var entries = values.Where(entry => entry.Changed).ToList(); CheckDestinations(entries, undo); var locked = new List<Locked>();
            try
            {
                foreach (var entry in entries)
                {
                    token.ThrowIfCancellationRequested(); var file = new Locked(entry, undo); locked.Add(file);
                    if (!entry.Stamp.Same(Stamp(file.Stream, token))) throw new IOException("文件在预览后已改变，操作已停止且没有修改文件。");
                }
                token.ThrowIfCancellationRequested();
                try
                {
                    foreach (var file in locked) { file.Move(file.Temporary); file.Stage = 1; }
                    for (int index = 0; index < locked.Count; index++) { var file = locked[index]; file.Move(file.To); file.Stage = 2; if (progress != null) progress.Report((index + 1) * 100 / locked.Count); }
                }
                catch (Exception original)
                {
                    var failures = new List<Exception>();
                    foreach (var file in locked.Where(file => file.Stage == 2)) try { file.Move(file.Temporary); file.Stage = 1; } catch (Exception error) { failures.Add(error); }
                    foreach (var file in locked.Where(file => file.Stage == 1)) try { file.Move(file.From); file.Stage = 0; } catch (Exception error) { failures.Add(error); }
                    if (failures.Count != 0) throw new AggregateException("部分文件无法恢复，已保留临时文件；请检查文件夹中的 .side-island-rename 文件。", new[] { original }.Concat(failures));
                    throw;
                }
            }
            finally { foreach (var file in locked) file.Dispose(); }
        }
        private static BatchFileStamp Stamp(FileStream stream, CancellationToken token)
        {
            FileInformation info; if (!GetFileInformationByHandle(stream.SafeFileHandle, out info)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0) throw new ArgumentException("不处理链接文件。");
            stream.Position = 0;
            using (var hash = SHA256.Create())
            {
                var buffer = new byte[1024 * 1024]; int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) != 0) { token.ThrowIfCancellationRequested(); hash.TransformBlock(buffer, 0, read, null, 0); }
                hash.TransformFinalBlock(new byte[0], 0, 0);
                return new BatchFileStamp { FileId = info.Volume.ToString("X8") + ":" + info.IndexHigh.ToString("X8") + info.IndexLow.ToString("X8"), Length = ((long)info.SizeHigh << 32) | info.SizeLow, LastWrite = info.Written.Value, Hash = BitConverter.ToString(hash.Hash).Replace("-", "") };
            }
        }
    }
}
