using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace SideIsland.Core;

public sealed record PortableRenameOptions
{
    public string Template { get; init; } = "{name}";
    public string Prefix { get; init; } = "";
    public string Suffix { get; init; } = "";
    public string Find { get; init; } = "";
    public string Replace { get; init; } = "";
    public int Start { get; init; } = 1;
    public int Padding { get; init; } = 3;
}

public sealed record PortableFileStamp(string Identity, long Length, long ModifiedSeconds, long ModifiedFraction, string Sha256);
public sealed record PortableRenameItem(string OriginalPath, string DestinationPath, PortableFileStamp Source)
{
    public bool Changed => !StringComparer.Ordinal.Equals(OriginalPath, DestinationPath);
}
public sealed class PortableRenamePlan
{
    internal PortableRenamePlan(Guid session, List<PortableRenameItem> items, Dictionary<string, string> directories)
    { Session = session; Items = items.AsReadOnly(); Directories = directories; }
    internal Guid Session { get; }
    internal Dictionary<string, string> Directories { get; }
    public IReadOnlyList<PortableRenameItem> Items { get; }
    public int ChangedCount => Items.Count(i => i.Changed);
}
public sealed record PortableBatchProgress(string Stage, int Completed, int Total);
public sealed record PortableFileSnapshot(string Path, byte[] Bytes, PortableFileStamp Source);

/// <summary>Local regular files only. Never replaces an existing directory entry.</summary>
public sealed class PortableBatchFiles : IDisposable
{
    public const int MaximumFiles = 200;
    public const long MaximumTotalBytes = 8L * 1024 * 1024 * 1024;
    private readonly Guid session = Guid.NewGuid();
    private readonly string journalDirectory;
    private readonly object gate = new();
    private PortableRenamePlan undo;
    private readonly Dictionary<string, IDisposable> undoAnchors = new(StringComparer.Ordinal);
    private bool disposed;
    public string LastJournalPath { get; private set; } = "";
    public bool CanUndo => Volatile.Read(ref undo) is not null;
    // Shared read-only identity boundary: reuse the audited native stat layout.
    internal static string ReadOpenFileIdentity(SafeFileHandle handle)
    {
        Native.RequireSupported();
        var stat = Native.StatHandle(handle);
        if (!stat.Regular) throw new IOException("The selected path is not a regular file.");
        return stat.Identity;
    }
    public PortableBatchFiles(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        journalDirectory = Path.Combine(Path.GetFullPath(dataDirectory), "batch-operations");
    }

    public PortableRenamePlan Preview(IEnumerable<string> paths, PortableRenameOptions options, CancellationToken token = default, IProgress<PortableBatchProgress> progress = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed), this);
        ValidateOptions(options); Native.RequireSupported();
        var input = paths?.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray() ?? [];
        if (input.Length is < 1 or > MaximumFiles) throw new ArgumentException("Select 1–200 local regular files.");
        var items = new List<PortableRenameItem>();
        var parents = new Dictionary<string, string>(StringComparer.Ordinal);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        long bytes = 0;
        for (var index = 0; index < input.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            if (!Path.IsPathFullyQualified(input[index]) || input[index].StartsWith(@"\\", StringComparison.Ordinal)) throw new ArgumentException("Use absolute local paths.");
            var full = Path.GetFullPath(input[index]);
            using var directory = DirectoryLease.Open(Path.GetDirectoryName(full), true);
            var name = Path.GetFileName(full); ValidateName(name);
            using var source = SourceLease.Open(directory, name, false);
            var stamp = source.Stamp(token);
            bytes = checked(bytes + stamp.Length);
            if (bytes > MaximumTotalBytes) throw new ArgumentException("A batch may contain at most 8 GiB of source data.");
            if (!identities.Add(stamp.Identity)) throw new ArgumentException("A file was selected twice or has a shared hard-link identity.");
            parents[directory.Path] = directory.Identity;
            var extension = Extension(name);
            var stem = name[..(name.Length - extension.Length)];
            if (!string.IsNullOrEmpty(options.Find)) stem = stem.Replace(options.Find, options.Replace ?? "", StringComparison.Ordinal);
            var number = checked(options.Start + index).ToString("D" + options.Padding, CultureInfo.InvariantCulture);
            var next = (options.Prefix ?? "") + System.Text.RegularExpressions.Regex.Replace(options.Template, @"\{(name|n)\}", match => match.Groups[1].Value == "name" ? stem : number)
                + (options.Suffix ?? "") + extension;
            ValidateName(next);
            items.Add(new(Path.Combine(directory.Path, name), Path.Combine(directory.Path, next), stamp));
            progress?.Report(new("preview", index + 1, input.Length));
        }
        var plan = new PortableRenamePlan(session, items, parents);
        using var leases = OpenDirectories(plan);
        CheckDestinations(plan.Items, false, leases);
        return plan;
    }

    public void Execute(PortableRenamePlan plan, CancellationToken token = default, IProgress<PortableBatchProgress> progress = null)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (plan is null || plan.Session != session || plan.ChangedCount == 0) throw new ArgumentException("Create a changed preview in this session first.");
            // Release the previous session's descriptors before opening a new batch. In particular,
            // Apple's default soft fd limit must not require two full batches to coexist.
            Volatile.Write(ref undo, null); ReleaseAnchors();
            Run(plan, false, token, progress); Volatile.Write(ref undo, plan);
        }
    }
    public void Undo(CancellationToken token = default, IProgress<PortableBatchProgress> progress = null)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (undo is null) throw new InvalidOperationException("No successful rename from this session is available to undo.");
            var plan = undo; Volatile.Write(ref undo, null);
            try { Run(plan, true, token, progress); }
            finally { ReleaseAnchors(); }
        }
    }
    private void ReleaseAnchors() { foreach (var handle in undoAnchors.Values) handle.Dispose(); undoAnchors.Clear(); }
    public void Dispose() { lock (gate) { if (disposed) return; disposed = true; Volatile.Write(ref undo, null); ReleaseAnchors(); } }
    public static string Extension(string name)
    {
        var dot = name.LastIndexOf('.'); return dot > 0 ? name[dot..] : "";
    }
    /// <summary>Fits a generated output name while retaining its suffix and extension, without splitting a Unicode scalar.</summary>
    public static string FitOutputName(string stem, string suffix, string extension)
    {
        ArgumentException.ThrowIfNullOrEmpty(stem); ArgumentNullException.ThrowIfNull(suffix); ArgumentNullException.ThrowIfNull(extension);
        ValidateName(stem + suffix + extension, false);
        var ending = suffix + extension;
        var remainingChars = 240 - ending.Length; var remainingBytes = 255 - Encoding.UTF8.GetByteCount(ending);
        var length = 0;
        while (length < stem.Length)
        {
            if (!Rune.TryGetRuneAt(stem, length, out var rune)) throw new ArgumentException("The output filename contains invalid Unicode.");
            if (rune.Utf16SequenceLength > remainingChars || rune.Utf8SequenceLength > remainingBytes) break;
            length += rune.Utf16SequenceLength; remainingChars -= rune.Utf16SequenceLength; remainingBytes -= rune.Utf8SequenceLength;
        }
        if (length == 0) throw new ArgumentException("The output suffix and extension leave no room for a filename.");
        var name = stem[..length] + ending; ValidateName(name); return name;
    }
    public static PortableFileSnapshot ReadLocalSnapshot(string path, long maximumBytes, CancellationToken token = default)
    {
        if (!System.IO.Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || maximumBytes is < 1 or > 128 * 1024 * 1024) throw new ArgumentException("Use a local absolute source path and a bounded snapshot size.");
        using var directory = DirectoryLease.Open(System.IO.Path.GetDirectoryName(path), true);
        using var source = SourceLease.Open(directory, System.IO.Path.GetFileName(path), false);
        var stat = Native.StatHandle(source.Stream.SafeFileHandle);
        if (stat.Length > maximumBytes) throw new IOException("The source exceeds the snapshot size limit.");
        var bytes = new byte[checked((int)stat.Length)]; var position = 0;
        while (position < bytes.Length) { token.ThrowIfCancellationRequested(); var read = source.Stream.Read(bytes, position, Math.Min(1024 * 1024, bytes.Length - position)); if (read == 0) throw new EndOfStreamException("The source changed while it was being read."); position += read; }
        token.ThrowIfCancellationRequested();
        if (source.Stream.ReadByte() != -1 || Native.StatHandle(source.Stream.SafeFileHandle) != stat) throw new IOException("The source changed while it was being read.");
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        return new(System.IO.Path.Combine(directory.Path, System.IO.Path.GetFileName(path)), bytes, new(stat.Identity, stat.Length, stat.ModifiedSeconds, stat.ModifiedFraction, hash));
    }
    public static string WriteNewOutput(string outputDirectory, string preferredName, byte[] bytes, CancellationToken token = default)
    {
        ValidateName(preferredName); ArgumentNullException.ThrowIfNull(bytes);
        if (!System.IO.Path.IsPathFullyQualified(outputDirectory) || outputDirectory.StartsWith(@"\\", StringComparison.Ordinal)) throw new ArgumentException("Select an existing local output directory.");
        using var directory = DirectoryLease.Open(outputDirectory, true);
        var extension = Extension(preferredName); var stem = preferredName[..(preferredName.Length - extension.Length)];
        for (var suffix = 0; suffix < 10000; suffix++)
        {
            token.ThrowIfCancellationRequested();
            var name = suffix == 0 ? preferredName : FitOutputName(stem, "-" + suffix.ToString(CultureInfo.InvariantCulture), extension);
            SafeFileHandle handle;
            if (OperatingSystem.IsWindows())
            {
                handle = Native.CreateFile(System.IO.Path.Combine(directory.Path, name), 0x40000000, 0, IntPtr.Zero, 1, 0x00200000, IntPtr.Zero);
                if (handle.IsInvalid) { var error = Marshal.GetLastPInvokeError(); handle.Dispose(); if (error is 80 or 183) continue; throw new Win32Exception(error); }
            }
            else
            {
                var flags = OperatingSystem.IsLinux() ? 1 | 0x40 | 0x80 | 0x20000 | 0x80000 : 1 | 0x200 | 0x800 | 0x100 | 0x1000000;
                var fd = Native.openat_create(directory.Handle.DangerousGetHandle().ToInt32(), name, flags, 0x180); // 0600, affected only by the user's umask.
                if (fd < 0) { if (Marshal.GetLastPInvokeError() == 17) continue; Native.ThrowLastError(); }
                handle = new(new IntPtr(fd), true);
            }
            using var stream = new FileStream(handle, FileAccess.Write);
            // Finish one newly created file before observing cancellation again. A disk failure retains
            // its uniquely named partial output rather than deleting a possibly replaced external entry.
            stream.Write(bytes); stream.Flush(true);
            return System.IO.Path.Combine(directory.Path, name);
        }
        throw new IOException("Cannot allocate an unused output filename.");
    }
    public static void ValidateName(string name) => ValidateName(name, true);
    private static void ValidateName(string name, bool checkLength)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Any(char.IsControl)
            || name.IndexOfAny(['/', '\\', '\0']) >= 0 || (checkLength && (Encoding.UTF8.GetByteCount(name) > 255 || name.Length > 240)))
            throw new ArgumentException("The generated filename is empty, too long or contains a path separator/control character.");
        for (var index = 0; index < name.Length;)
        {
            if (!Rune.TryGetRuneAt(name, index, out var rune)) throw new ArgumentException("The filename contains invalid Unicode.");
            index += rune.Utf16SequenceLength;
        }
        if (!OperatingSystem.IsWindows()) return;
        var stem = name.Split('.')[0].ToUpperInvariant();
        if (name.EndsWith('.') || name.EndsWith(' ') || name.IndexOfAny(['<', '>', ':', '"', '|', '?', '*']) >= 0
            || new[] { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$" }.Contains(stem)
            || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '0' and <= '9'))
            throw new ArgumentException("The generated filename is reserved or invalid on Windows.");
    }
    private static void ValidateOptions(PortableRenameOptions options)
    {
        if (options is null || string.IsNullOrEmpty(options.Template) || options.Template.Length > 200 || options.Start < 0 || options.Padding is < 1 or > 9
            || (options.Prefix?.Length ?? 0) > 100 || (options.Suffix?.Length ?? 0) > 100 || (options.Find?.Length ?? 0) > 200 || (options.Replace?.Length ?? 0) > 200)
            throw new ArgumentException("Use a template, nonnegative start number and 1–9 number digits.");
        if (options.Template.Replace("{name}", "").Replace("{n}", "").IndexOfAny(['{', '}']) >= 0) throw new ArgumentException("Only {name} and {n} template fields are supported.");
    }

    private static DirectorySet OpenDirectories(PortableRenamePlan plan)
    {
        var result = new DirectorySet();
        try
        {
            foreach (var pair in plan.Directories)
            {
                var directory = DirectoryLease.Open(pair.Key, false); result.Add(directory);
                if (directory.Identity != pair.Value) throw new IOException("A source directory was replaced after preview.");
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }
    private static void CheckDestinations(IReadOnlyList<PortableRenameItem> items, bool reverse, DirectorySet directories)
    {
        // Compare actual target entries by native identity; do not guess a volume's case rules.
        var active = items.Where(i => i.Changed).ToArray();
        var sourceIds = active.Select(i => i.Source.Identity).ToHashSet(StringComparer.Ordinal);
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in active)
        {
            var destination = reverse ? item.OriginalPath : item.DestinationPath;
            var directory = directories.For(destination);
            var name = Path.GetFileName(destination);
            var key = directory.Identity + ":" + directory.CollisionKey(name);
            if (!targets.Add(key)) throw new IOException("Two selected files have the same destination name.");
            var occupied = directory.TryStat(name);
            if (occupied is not null && (!occupied.Regular || !sourceIds.Contains(occupied.Identity))) throw new IOException("A destination already exists; no files will be overwritten: " + destination);
        }
    }
    private sealed class MoveEntry : IDisposable
    {
        internal PortableRenameItem Item;
        internal DirectoryLease Directory;
        internal SourceLease Source;
        internal string From, To, Temporary;
        internal int Stage;
        internal string Current => Stage == 0 ? From : Stage == 1 ? Temporary : To;
        public void Dispose() => Source?.Dispose();
    }
    private void Run(PortableRenamePlan plan, bool reverse, CancellationToken token, IProgress<PortableBatchProgress> progress)
    {
        Native.RequireSupported(); token.ThrowIfCancellationRequested();
        using var directories = OpenDirectories(plan);
        CheckDestinations(plan.Items, reverse, directories);
        var entries = new List<MoveEntry>();
        try
        {
            foreach (var item in plan.Items.Where(i => i.Changed))
            {
                token.ThrowIfCancellationRequested();
                var from = reverse ? item.DestinationPath : item.OriginalPath;
                var to = reverse ? item.OriginalPath : item.DestinationPath;
                var directory = directories.For(from);
                var source = SourceLease.Open(directory, Path.GetFileName(from), true);
                var entry = new MoveEntry { Item = item, Directory = directory, Source = source, From = Path.GetFileName(from), To = Path.GetFileName(to), Temporary = ".side-island-rename-" + Guid.NewGuid().ToString("N") + ".tmp" };
                entries.Add(entry);
                if (source.Stamp(token) != item.Source) throw new IOException("A file changed or was replaced since preview. Nothing has been renamed.");
                // Open the replacement transaction descriptor before releasing the identity anchor,
                // preventing inode reuse across an undo. Keep the live descriptor count flat.
                if (reverse && undoAnchors.Remove(item.Source.Identity, out var anchor)) anchor.Dispose();
            }
            CheckDestinations(plan.Items, reverse, directories); token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(journalDirectory);
            var journalPath = Path.Combine(journalDirectory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".jsonl");
            using var journal = new Journal(journalPath);
            LastJournalPath = journalPath;
            journal.Append(new { kind = "manifest", version = 1, operation = reverse ? "undo" : "rename", session, utc = DateTime.UtcNow,
                entries = entries.Select(e => new { directory = e.Directory.Path, directoryId = e.Directory.Identity, e.From, e.To, e.Temporary, source = e.Item.Source }).ToArray() });
            try
            {
                for (var index = 0; index < entries.Count; index++)
                {
                    token.ThrowIfCancellationRequested(); var entry = entries[index];
                    MoveChecked(entry, entry.Temporary); entry.Stage = 1;
                    journal.Append(new { kind = "staged", index, utc = DateTime.UtcNow });
                    progress?.Report(new("staging", index + 1, entries.Count));
                }
                for (var index = 0; index < entries.Count; index++)
                {
                    token.ThrowIfCancellationRequested(); var entry = entries[index];
                    // Check contents once more while the selected inode is open. Unix locks are advisory.
                    if (entry.Source.Stamp(token) != entry.Item.Source) throw new IOException("A source was modified during the rename.");
                    MoveChecked(entry, entry.To); entry.Stage = 2;
                    journal.Append(new { kind = "renamed", index, utc = DateTime.UtcNow });
                    progress?.Report(new("renaming", index + 1, entries.Count));
                }
                token.ThrowIfCancellationRequested();
                foreach (var entry in entries)
                    if (entry.Source.Stamp(token) != entry.Item.Source || entry.Directory.TryStat(entry.To)?.Identity != entry.Item.Source.Identity) throw new IOException("A destination changed during the rename.");
                var prepared = new Dictionary<string, IDisposable>(StringComparer.Ordinal);
                try
                {
                    if (!reverse)
                    {
                        foreach (var entry in entries)
                        {
                            if (OperatingSystem.IsWindows())
                            {
                                var anchor = Native.CreateFile(Path.Combine(entry.Directory.Path, entry.To), 0x80, 7, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero); Native.CheckHandle(anchor);
                                try { if (Native.StatHandle(anchor).Identity != entry.Item.Source.Identity) throw new IOException("Identity anchor no longer matches the renamed file."); prepared.Add(entry.Item.Source.Identity, anchor); }
                                catch { anchor.Dispose(); throw; }
                            }
                            else
                            {
                                // A retained open inode prevents recycling, without blocking cooperating
                                // editors with our execution flock after the operation has completed.
                                if (Native.flock(entry.Source.Stream.SafeFileHandle.DangerousGetHandle().ToInt32(), 8) != 0) Native.ThrowLastError();
                            }
                        }
                    }
                    journal.Append(new { kind = "completed", utc = DateTime.UtcNow });
                    if (!reverse)
                    {
                        if (!OperatingSystem.IsWindows()) foreach (var entry in entries) { prepared.Add(entry.Item.Source.Identity, entry.Source); entry.Source = null; }
                        foreach (var pair in prepared) undoAnchors.Add(pair.Key, pair.Value);
                        prepared.Clear();
                    }
                }
                finally { foreach (var handle in prepared.Values) handle.Dispose(); }
            }
            catch (Exception original)
            {
                var failures = new List<Exception>();
                foreach (var entry in entries.Where(e => e.Stage == 2))
                    try { MoveChecked(entry, entry.Temporary); entry.Stage = 1; journal.TryAppend(new { kind = "rollback-staged", entry.To, utc = DateTime.UtcNow }); }
                    catch (Exception error) { failures.Add(error); }
                foreach (var entry in entries.Where(e => e.Stage == 1))
                    try { MoveChecked(entry, entry.From); entry.Stage = 0; journal.TryAppend(new { kind = "restored", entry.From, utc = DateTime.UtcNow }); }
                    catch (Exception error) { failures.Add(error); }
                journal.TryAppend(new { kind = failures.Count == 0 ? "rolled-back" : "recovery-required", error = original.Message,
                    remaining = entries.Where(e => e.Stage != 0).Select(e => Path.Combine(e.Directory.Path, e.Current)).ToArray(), utc = DateTime.UtcNow });
                if (failures.Count != 0) throw new AggregateException("Rollback could not restore every filename. No foreign file was overwritten. Keep the journal and .side-island-rename files for manual recovery: " + journalPath, new[] { original }.Concat(failures));
                throw;
            }
        }
        finally { foreach (var entry in entries) entry.Dispose(); }
    }
    private static void MoveChecked(MoveEntry entry, string destination)
    {
        if (entry.Directory.TryStat(entry.Current)?.Identity != entry.Item.Source.Identity) throw new IOException("A directory entry was replaced externally; refusing to move it.");
        entry.Source.Move(entry.Current, destination);
        // Record the native move before any verification can fail, so rollback knows its location.
        entry.Stage = destination == entry.From ? 0 : destination == entry.Temporary ? 1 : 2;
        if (entry.Directory.TryStat(destination)?.Identity != entry.Item.Source.Identity) throw new IOException("Unexpected identity after native rename; retain the operation journal.");
    }
    private sealed class Journal : IDisposable
    {
        private readonly FileStream stream;
        internal Journal(string path) => stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        internal void Append(object value) { var data = JsonSerializer.SerializeToUtf8Bytes(value); stream.Write(data); stream.WriteByte(10); stream.Flush(true); }
        internal void TryAppend(object value) { try { Append(value); } catch (IOException) { } }
        public void Dispose() => stream.Dispose();
    }
    private sealed class DirectorySet : IDisposable
    {
        private readonly Dictionary<string, DirectoryLease> values = new(StringComparer.Ordinal);
        internal void Add(DirectoryLease value) => values.Add(value.Path, value);
        internal DirectoryLease For(string path) => values[Path.GetDirectoryName(path)];
        public void Dispose() { foreach (var value in values.Values) value.Dispose(); }
    }
    private sealed class DirectoryLease : IDisposable
    {
        internal string Path, Identity;
        internal SafeFileHandle Handle;
        private bool? insensitive;
        internal static DirectoryLease Open(string path, bool canonicalize)
        {
            Native.RequireSupported();
            path = System.IO.Path.GetFullPath(path);
            if (!OperatingSystem.IsWindows() && canonicalize) path = Native.RealPath(path);
            SafeFileHandle handle;
            if (OperatingSystem.IsWindows())
            {
                // Junctions and reparse ancestors would make absolute handle-based targets ambiguous.
                var current = new DirectoryInfo(path);
                while (current is not null) { if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked source directories are not supported on Windows."); current = current.Parent; }
                handle = Native.CreateFile(path, 0x80, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                Native.CheckHandle(handle);
            }
            else
            {
                var fd = Native.open("/", Native.DirectoryFlags);
                if (fd < 0) Native.ThrowLastError();
                handle = new(new IntPtr(fd), true);
                try
                {
                    foreach (var component in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var next = Native.openat(handle.DangerousGetHandle().ToInt32(), component, Native.DirectoryFlags);
                        if (next < 0) Native.ThrowLastError();
                        handle.Dispose(); handle = new(new IntPtr(next), true);
                    }
                }
                catch { handle.Dispose(); throw; }
            }
            try
            {
                var stat = Native.StatHandle(handle);
                if (!stat.Directory) throw new IOException("Expected an ordinary local source directory.");
                return new() { Path = System.IO.Path.TrimEndingDirectorySeparator(path), Handle = handle, Identity = stat.Identity };
            }
            catch { handle.Dispose(); throw; }
        }
        internal NativeStat TryStat(string name) => Native.StatAt(this, name);
        internal string CollisionKey(string name)
        {
            // Linux may have case-fold directories; APFS supports both case modes.
            // A harmless read probes an existing alphabetic filename in this directory.
            if (insensitive is null)
            {
                insensitive = OperatingSystem.IsWindows();
                foreach (var file in System.IO.Directory.EnumerateFileSystemEntries(Path).Take(512))
                {
                    var existing = System.IO.Path.GetFileName(file);
                    var chars = existing.ToCharArray(); var index = Array.FindIndex(chars, c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z');
                    if (index < 0) continue;
                    chars[index] = char.IsUpper(chars[index]) ? char.ToLowerInvariant(chars[index]) : char.ToUpperInvariant(chars[index]);
                    var a = TryStat(existing); var b = TryStat(new string(chars));
                    insensitive = b is not null && a?.Identity == b.Identity; break;
                }
            }
            var value = OperatingSystem.IsMacOS() ? name.Normalize(NormalizationForm.FormC) : name;
            return insensitive == true ? value.ToUpperInvariant() : value;
        }
        public void Dispose() => Handle?.Dispose();
    }
    private sealed class SourceLease : IDisposable
    {
        internal FileStream Stream;
        private DirectoryLease directory;
        internal static SourceLease Open(DirectoryLease parent, string name, bool writableRename)
        {
            SafeFileHandle handle;
            if (OperatingSystem.IsWindows())
            {
                handle = Native.CreateFile(System.IO.Path.Combine(parent.Path, name), writableRename ? 0x80010000u : 0x80000000u, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
                Native.CheckHandle(handle);
            }
            else
            {
                var fd = Native.openat(parent.Handle.DangerousGetHandle().ToInt32(), name, Native.FileFlags);
                if (fd < 0) Native.ThrowLastError();
                handle = new(new IntPtr(fd), true);
                if (writableRename && Native.flock(fd, 2 | 4) != 0) { var error = Marshal.GetLastPInvokeError(); handle.Dispose(); throw new Win32Exception(error, "The source is locked by another cooperating process."); }
            }
            try
            {
                var info = Native.StatHandle(handle);
                if (!info.Regular || info.Links != 1) throw new IOException("Only ordinary files with one hard link are supported; folders, links and devices are excluded.");
                return new() { Stream = new FileStream(handle, FileAccess.Read), directory = parent };
            }
            catch { handle.Dispose(); throw; }
        }
        internal PortableFileStamp Stamp(CancellationToken token)
        {
            var before = Native.StatHandle(Stream.SafeFileHandle);
            if (!before.Regular || before.Links != 1 || before.Length > MaximumTotalBytes) throw new IOException("The source type, links or size is outside supported limits.");
            Stream.Position = 0; using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1024 * 1024]; int read; long total = 0;
            while ((read = Stream.Read(buffer, 0, buffer.Length)) != 0) { token.ThrowIfCancellationRequested(); total = checked(total + read); if (total > MaximumTotalBytes) throw new IOException("Source grew beyond the batch limit."); hash.AppendData(buffer, 0, read); }
            token.ThrowIfCancellationRequested(); var after = Native.StatHandle(Stream.SafeFileHandle);
            if (before != after || total != after.Length) throw new IOException("The source changed while its SHA-256 was being read.");
            return new(after.Identity, after.Length, after.ModifiedSeconds, after.ModifiedFraction, Convert.ToHexString(hash.GetHashAndReset()));
        }
        internal void Move(string from, string to)
        {
            if (OperatingSystem.IsWindows()) Native.RenameHandle(Stream.SafeFileHandle, System.IO.Path.Combine(directory.Path, to));
            else
            {
                var fd = directory.Handle.DangerousGetHandle().ToInt32();
                var result = OperatingSystem.IsLinux() ? Native.renameat2(fd, from, fd, to, 1) : Native.renameatx_np(fd, from, fd, to, 4);
                if (result != 0) Native.ThrowLastError();
            }
        }
        public void Dispose() => Stream?.Dispose();
    }
    private sealed record NativeStat(string Identity, long Length, long ModifiedSeconds, long ModifiedFraction, uint Links, bool Regular, bool Directory);
    private static class Native
    {
        internal static int DirectoryFlags => OperatingSystem.IsLinux() ? 0x10000 | 0x20000 | 0x80000 : 0x100000 | 0x100 | 0x1000000;
        internal static int FileFlags => OperatingSystem.IsLinux() ? 0x20000 | 0x80000 | 0x800 : 0x100 | 0x1000000 | 0x4;
        internal static void RequireSupported()
        {
            if (OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64) return;
            if (OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64) return;
            if (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64) return;
            throw new PlatformNotSupportedException("Safe batch rename requires Windows x64, Linux x64/arm64 (statx + renameat2), or Apple Silicon macOS (fstat + renameatx_np). No unsafe fallback is used.");
        }
        [DllImport("libc", SetLastError = true)] internal static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
        [DllImport("libc", SetLastError = true)] internal static extern int openat(int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
        [DllImport("libc", EntryPoint = "openat", SetLastError = true)] internal static extern int openat_create(int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mode);
        [DllImport("libc", SetLastError = true)] internal static extern int flock(int fd, int operation);
        [DllImport("libc", SetLastError = true)] private static extern IntPtr realpath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr result);
        [DllImport("libc")] private static extern void free(IntPtr ptr);
        [DllImport("libc", SetLastError = true)] private static extern int statx(int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, [Out] byte[] result);
        [DllImport("libc", SetLastError = true)] private static extern int fstat(int fd, [Out] byte[] result);
        [DllImport("libc", SetLastError = true)] private static extern int fstatat(int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] byte[] result, int flags);
        [DllImport("libc", SetLastError = true)] internal static extern int renameat2(int fromFd, [MarshalAs(UnmanagedType.LPUTF8Str)] string from, int toFd, [MarshalAs(UnmanagedType.LPUTF8Str)] string to, uint flags);
        [DllImport("libc", SetLastError = true)] internal static extern int renameatx_np(int fromFd, [MarshalAs(UnmanagedType.LPUTF8Str)] string from, int toFd, [MarshalAs(UnmanagedType.LPUTF8Str)] string to, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle file, out WinInfo info);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, IntPtr info, uint bytes);
        [StructLayout(LayoutKind.Sequential)] private struct WinTime { internal uint Low, High; internal long Value => unchecked((long)(((ulong)High << 32) | Low)); }
        [StructLayout(LayoutKind.Sequential)] private struct WinInfo { internal uint Attributes; internal WinTime Created, Accessed, Written; internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
        [StructLayout(LayoutKind.Sequential)] private struct RenameHeader { internal byte Replace; internal IntPtr Root; internal uint Length; internal ushort First; }
        internal static void ThrowLastError() => throw new Win32Exception(Marshal.GetLastPInvokeError());
        internal static void CheckHandle(SafeFileHandle handle) { if (!handle.IsInvalid) return; var error = Marshal.GetLastPInvokeError(); handle.Dispose(); throw new Win32Exception(error); }
        internal static string RealPath(string path)
        {
            var ptr = realpath(path, IntPtr.Zero); if (ptr == IntPtr.Zero) ThrowLastError();
            try { return Marshal.PtrToStringUTF8(ptr) ?? throw new IOException("Cannot resolve the source directory."); }
            finally { free(ptr); }
        }
        internal static NativeStat StatHandle(SafeFileHandle handle)
        {
            if (OperatingSystem.IsWindows())
            {
                if (!GetFileInformationByHandle(handle, out var info)) ThrowLastError();
                var link = (info.Attributes & 0x400) != 0;
                var directory = (info.Attributes & 0x10) != 0;
                return new(info.Volume.ToString("X8") + ":" + info.IndexHigh.ToString("X8") + info.IndexLow.ToString("X8"), ((long)info.SizeHigh << 32) | info.SizeLow, info.Written.Value, 0, info.Links, !link && !directory, !link && directory);
            }
            var bytes = new byte[256]; var fd = handle.DangerousGetHandle().ToInt32();
            var result = OperatingSystem.IsLinux() ? statx(fd, "", 0x1000, 0x7ff, bytes) : fstat(fd, bytes);
            if (result != 0) ThrowLastError(); return Decode(bytes, OperatingSystem.IsLinux());
        }
        internal static NativeStat StatAt(DirectoryLease directory, string name)
        {
            if (OperatingSystem.IsWindows())
            {
                using var handle = CreateFile(System.IO.Path.Combine(directory.Path, name), 0x80, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                if (handle.IsInvalid) { var error = Marshal.GetLastPInvokeError(); if (error is 2 or 3) return null; throw new Win32Exception(error); }
                return StatHandle(handle);
            }
            var bytes = new byte[256]; var fd = directory.Handle.DangerousGetHandle().ToInt32();
            var result = OperatingSystem.IsLinux() ? statx(fd, name, 0x100, 0x7ff, bytes) : fstatat(fd, name, bytes, 0x20);
            if (result != 0) { if (Marshal.GetLastPInvokeError() == 2) return null; ThrowLastError(); }
            return Decode(bytes, OperatingSystem.IsLinux());
        }
        internal static NativeStat Decode(byte[] bytes, bool linux)
        {
            if (!BitConverter.IsLittleEndian || bytes.Length < (linux ? 256 : 144)) throw new PlatformNotSupportedException("Unsupported native stat byte order/ABI.");
            ushort mode; uint links; ulong inode; long size, seconds, fraction; string device;
            if (linux)
            {
                if ((BitConverter.ToUInt32(bytes, 0) & 0x345) != 0x345) throw new PlatformNotSupportedException("The filesystem did not return required statx identity fields.");
                mode = BitConverter.ToUInt16(bytes, 28); links = BitConverter.ToUInt32(bytes, 16); inode = BitConverter.ToUInt64(bytes, 32);
                size = BitConverter.ToInt64(bytes, 40); seconds = BitConverter.ToInt64(bytes, 112); fraction = BitConverter.ToUInt32(bytes, 120);
                device = BitConverter.ToUInt32(bytes, 136).ToString("X") + ":" + BitConverter.ToUInt32(bytes, 140).ToString("X");
            }
            else
            {
                mode = BitConverter.ToUInt16(bytes, 4); links = BitConverter.ToUInt16(bytes, 6); inode = BitConverter.ToUInt64(bytes, 8);
                size = BitConverter.ToInt64(bytes, 96); seconds = BitConverter.ToInt64(bytes, 48); fraction = BitConverter.ToInt64(bytes, 56); device = BitConverter.ToUInt32(bytes, 0).ToString("X");
            }
            if (size < 0 || fraction is < 0 or >= 1_000_000_000 || inode == 0 || links == 0) throw new PlatformNotSupportedException("Invalid native identity/stat contract; refusing to rename.");
            return new(device + ":" + inode.ToString("X"), size, seconds, fraction, links, (mode & 0xf000) == 0x8000, (mode & 0xf000) == 0x4000);
        }
        internal static void RenameHandle(SafeFileHandle handle, string destination)
        {
            var name = Encoding.Unicode.GetBytes(destination); var offset = (int)Marshal.OffsetOf<RenameHeader>(nameof(RenameHeader.First));
            var size = checked(offset + name.Length + 2); var buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.Copy(new byte[size], 0, buffer, size);
                Marshal.WriteInt32(buffer, (int)Marshal.OffsetOf<RenameHeader>(nameof(RenameHeader.Length)), name.Length);
                Marshal.Copy(name, 0, IntPtr.Add(buffer, offset), name.Length);
                if (!SetFileInformationByHandle(handle, 3, buffer, (uint)size)) ThrowLastError();
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }
    // A deterministic ABI fixture validates offsets without pretending to execute a foreign OS.
    public static void VerifyNativeAbiContract()
    {
        foreach (var linux in new[] { true, false })
        {
            var bytes = new byte[256];
            void Put(int offset, byte[] data) => data.CopyTo(bytes, offset);
            if (linux)
            { Put(0, BitConverter.GetBytes(0x7ffu)); Put(16, BitConverter.GetBytes(1u)); Put(28, BitConverter.GetBytes((ushort)0x81a4)); Put(32, BitConverter.GetBytes(123UL)); Put(40, BitConverter.GetBytes(456L)); Put(112, BitConverter.GetBytes(789L)); Put(120, BitConverter.GetBytes(42u)); Put(136, BitConverter.GetBytes(8u)); Put(140, BitConverter.GetBytes(9u)); }
            else
            { Put(0, BitConverter.GetBytes(8u)); Put(4, BitConverter.GetBytes((ushort)0x81a4)); Put(6, BitConverter.GetBytes((ushort)1)); Put(8, BitConverter.GetBytes(123UL)); Put(48, BitConverter.GetBytes(789L)); Put(56, BitConverter.GetBytes(42L)); Put(96, BitConverter.GetBytes(456L)); }
            var result = Native.Decode(bytes, linux);
            if (!result.Regular || result.Directory || result.Length != 456 || result.ModifiedSeconds != 789 || result.ModifiedFraction != 42 || result.Links != 1 || result.Identity != (linux ? "8:9:7B" : "8:7B")) throw new InvalidOperationException("Native stat ABI fixture failed.");
            if (linux) bytes[0] = 0; else Put(56, BitConverter.GetBytes(1_000_000_000L));
            try { Native.Decode(bytes, linux); throw new InvalidOperationException("Invalid native stat was accepted."); }
            catch (PlatformNotSupportedException) { }
        }
    }
}
