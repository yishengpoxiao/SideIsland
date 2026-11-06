#nullable enable
using System.Security.Cryptography;
using System.Text.Json;

namespace SideIsland.Core;

public enum StateFileProblemCode { InvalidData, FileTooLarge, LoadFailed, Conflict, CommittedConflict, Busy, Io }
public sealed record StateFileStatus(bool LoadFailed, bool NeedsReload, bool CanRecover, bool Dirty,
    StateFileProblemCode? ProblemCode, string? Message, string? RecoveryPath, string TargetPath);
public sealed class StateFileException(StateFileProblemCode code, string targetPath, string message, Exception? inner = null) : IOException(message, inner)
{
    public StateFileProblemCode Code { get; } = code;
    public string TargetPath { get; } = targetPath;
    public bool DraftCommitted { get; init; }
    public bool RequiresReload { get; init; }
    public string? BackupPath { get; init; }
    public override string ToString() => base.ToString() + Environment.NewLine + "State file: " + TargetPath + Environment.NewLine +
        "Result: " + Code + "; DraftCommitted=" + DraftCommitted + "; RequiresReload=" + RequiresReload + Environment.NewLine +
        "Recovery copy: " + (BackupPath ?? "(none confirmed)");
}

/// <summary>
/// Bounded opaque state bytes with optimistic concurrency and explicit corrupt-file
/// recovery. UI state is an immutable lock-free snapshot; disk operations serialize.
/// </summary>
public sealed class PortableStateFile
{
    private sealed record Stamp(bool Exists, string Hash, string Identity, long Length, long ModifiedTicks);
    private sealed record Contents(byte[] Bytes, Stamp Stamp);
    private readonly object gate = new();
    private readonly int maximumBytes;
    private readonly Action<string>? checkpoint;
    private readonly IWorkspaceFileCommit files;
    private readonly Action<Stream, byte[]> writeBytes;
    private Stamp? baseline;
    private bool loaded;
    private long draftRevision;
    private StateFileStatus status;
    public string FilePath { get; }
    public bool IsLoaded => Volatile.Read(ref loaded);
    public StateFileStatus Status => Volatile.Read(ref status);
    public PortableStateFile(string path, int maximumBytes) : this(path, maximumBytes, null) { }
    internal PortableStateFile(string path, int maximumBytes, Action<string>? checkpoint, IWorkspaceFileCommit? files = null, Action<Stream, byte[]>? writeBytes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (maximumBytes is < 1 or > 64 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        FilePath = Path.GetFullPath(path); this.maximumBytes = maximumBytes; this.checkpoint = checkpoint; this.files = files ?? new WorkspaceFileCommit();
        this.writeBytes = writeBytes ?? ((stream, bytes) => stream.Write(bytes));
        status = new(false, false, false, false, null, null, null, FilePath);
    }
    public void MarkDirty()
    {
        Interlocked.Increment(ref draftRevision); Update(current => current with { Dirty = true });
    }
    public void ReportFailure(StateFileProblemCode code, string message)
    {
        Interlocked.Increment(ref draftRevision); Update(current => current with { Dirty = true, ProblemCode = code, Message = message });
    }
    public T Load<T>(Func<byte[], T> decode, Func<T> fallback, Func<T>? missing = null)
    {
        ArgumentNullException.ThrowIfNull(decode); ArgumentNullException.ThrowIfNull(fallback);
        lock (gate)
        {
            long revision = Volatile.Read(ref draftRevision); baseline = null; Volatile.Write(ref loaded, true);
            try
            {
                var input = ReadFile(FilePath); baseline = input.Stamp;
                T value = input.Stamp.Exists ? decode(input.Bytes) : (missing ?? fallback)();
                Update(current => current with { LoadFailed = false, NeedsReload = false, CanRecover = false,
                    Dirty = Volatile.Read(ref draftRevision) != revision, ProblemCode = null, Message = null });
                return value;
            }
            catch (Exception error) when (Expected(error))
            {
                var failure = AsProblem(error);
                Update(current => current with { LoadFailed = true, NeedsReload = false, CanRecover = baseline is { Exists: true },
                    ProblemCode = failure.Code, Message = failure.Message });
                return fallback(); // A reload caller may preserve its current in-memory draft.
            }
        }
    }
    public void Save(byte[] bytes) => SaveCore(bytes, false);
    public void RecoverAndSave(byte[] bytes) => SaveCore(bytes, true);
    private void SaveCore(byte[] bytes, bool recovering)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        long revision = Interlocked.Increment(ref draftRevision);
        Update(current => current with { Dirty = true });
        try
        {
            if (bytes.Length > maximumBytes) throw Issue(StateFileProblemCode.FileTooLarge, "Saved state exceeds its size limit.");
            byte[] frozen = (byte[])bytes.Clone();
            lock (gate)
            {
                if (!loaded) throw Issue(StateFileProblemCode.LoadFailed, "Load the state file before saving.");
                if (Status.NeedsReload) throw Issue(StateFileProblemCode.Conflict, "Reload the current disk version before saving again.");
                if (recovering)
                {
                    if (!Status.LoadFailed || baseline is not { Exists: true }) throw Issue(StateFileProblemCode.LoadFailed, "Recovery requires a readable original file and explicit user action.");
                }
                else if (Status.LoadFailed) throw Issue(StateFileProblemCode.LoadFailed, "The unreadable original is retained. Reload or explicitly recover before saving.");
                string? backup = Commit(frozen, recovering);
                Saved(revision, backup, null);
            }
        }
        catch (Exception error) when (Expected(error))
        {
            var failure = AsProblem(error);
            if (failure.DraftCommitted) Saved(revision, failure.BackupPath, failure);
            else Update(current => current with { Dirty = true,
                NeedsReload = current.NeedsReload || failure.RequiresReload || failure.Code == StateFileProblemCode.Conflict,
                CanRecover = current.CanRecover && !failure.RequiresReload && failure.Code != StateFileProblemCode.Conflict,
                ProblemCode = failure.Code, Message = failure.Message, RecoveryPath = failure.BackupPath ?? current.RecoveryPath });
            throw failure;
        }
    }
    private void Saved(long revision, string? backup, StateFileException? warning)
        => Update(current => current with { LoadFailed = false, NeedsReload = false, CanRecover = false,
            Dirty = Volatile.Read(ref draftRevision) != revision,
            ProblemCode = Volatile.Read(ref draftRevision) == revision ? warning?.Code : current.ProblemCode,
            Message = Volatile.Read(ref draftRevision) == revision ? warning?.Message : current.Message,
            RecoveryPath = backup ?? current.RecoveryPath });
    private void Update(Func<StateFileStatus, StateFileStatus> change)
    {
        while (true)
        {
            var current = Status; var next = change(current);
            if (ReferenceEquals(Interlocked.CompareExchange(ref status, next, current), current)) return;
        }
    }
    private string? Commit(byte[] bytes, bool recovering)
    {
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp", lockPath = FilePath + ".lock";
        FileStream? writerLock = null;
        bool attempted = false, written = false, temporaryMayHoldRecovery = false;
        string? backup = null, plannedBackup = null, displacedIdentity = null, createdIdentity = null;
        Stamp? staging = null;
        try
        {
            CheckLocation(FilePath);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            try { writerLock = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
            catch (IOException error) { throw new StateFileException(StateFileProblemCode.Busy, FilePath, "Another save or a leftover lock is blocking this state file.", error); }
            MatchBaseline();
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
            {
                createdIdentity = PortableBatchFiles.ReadOpenFileIdentity(stream.SafeFileHandle);
                writeBytes(stream, bytes); stream.Flush(true);
            }
            staging = ReadFile(temporary).Stamp;
            checkpoint?.Invoke("prepared"); MatchBaseline(); checkpoint?.Invoke("checked");
            var expected = baseline!;
            if (expected.Exists)
            {
                plannedBackup = FilePath + (recovering ? ".unreadable-" : ".previous-") + Guid.NewGuid().ToString("N") + ".bak";
                if (files.UsesExchange)
                {
                    backup = temporary; temporaryMayHoldRecovery = true; attempted = true;
                    files.Exchange(temporary, FilePath); written = true; displacedIdentity = EntryIdentity(temporary);
                    files.MoveNew(temporary, plannedBackup); backup = plannedBackup;
                }
                else { backup = plannedBackup; attempted = true; File.Replace(temporary, FilePath, backup); written = true; }
            }
            else { attempted = true; files.MoveNew(temporary, FilePath); written = true; }
            checkpoint?.Invoke("committed");
            if (backup is not null && ReadFile(backup).Stamp != expected) throw Issue(StateFileProblemCode.CommittedConflict, "A concurrent external version was retained in the recovery copy.");
            var current = ReadFile(FilePath);
            if (current.Stamp.Identity != staging.Identity || !current.Bytes.AsSpan().SequenceEqual(bytes))
                throw Issue(StateFileProblemCode.Conflict, "The state file changed again during commit.");
            baseline = current.Stamp;
            if (!recovering && backup is not null)
            {
                checkpoint?.Invoke("cleanup");
                // Only an ordinary, fully verified previous version is disposable.
                // Recovery, concurrent and ambiguous versions remain available.
                if (ReadFile(backup).Stamp != expected) throw Issue(StateFileProblemCode.CommittedConflict, "The previous-version copy changed before cleanup.");
                File.Delete(backup); backup = null;
            }
            return backup;
        }
        catch (Exception error) when (Expected(error))
        {
            if (!attempted)
            {
                string? retained = CleanDraft(temporary, createdIdentity);
                var original = AsProblem(error);
                throw new StateFileException(original.Code, FilePath, original.Message, error)
                    { RequiresReload = original.RequiresReload, BackupPath = retained ?? original.BackupPath };
            }
            if (displacedIdentity is not null && plannedBackup is not null)
            {
                try { if (EntryIdentity(plannedBackup) == displacedIdentity) backup = plannedBackup; } catch (Exception inspect) when (Expected(inspect)) { }
            }
            if (temporaryMayHoldRecovery && backup == temporary && staging is not null)
            {
                try { var remaining = ReadFile(temporary); if (!remaining.Stamp.Exists || remaining.Stamp == staging) backup = null; } catch (Exception inspect) when (Expected(inspect)) { }
            }
            try
            {
                var current = ReadFile(FilePath);
                bool isStaged = staging is not null && current.Stamp.Identity == staging.Identity && current.Bytes.AsSpan().SequenceEqual(bytes);
                if (isStaged)
                {
                    baseline = current.Stamp;
                    throw new StateFileException(StateFileProblemCode.CommittedConflict, FilePath, "The draft is saved, but follow-up checks found a problem. Inspect the current file and available recovery copy.", error)
                        { DraftCommitted = true, BackupPath = backup };
                }
                if (!written && current.Stamp == baseline && staging is not null && ReadFile(temporary).Stamp == staging)
                    throw new StateFileException(AsProblem(error).Code, FilePath, error.Message, error)
                        { BackupPath = CleanDraft(temporary, createdIdentity) };
            }
            catch (StateFileException confirmed) when (confirmed.DraftCommitted) { throw; }
            catch (StateFileException unchanged) when (!written && unchanged.InnerException == error) { throw; }
            catch (Exception inspect) when (Expected(inspect)) { }
            baseline = null;
            if (backup is null && createdIdentity is not null)
            {
                try { if (EntryIdentity(temporary) == createdIdentity) backup = temporary; } catch (Exception inspect) when (Expected(inspect)) { }
            }
            throw new StateFileException(StateFileProblemCode.Conflict, FilePath, "The commit result cannot be confirmed. Reload before editing again; available recovery copies are retained.", error)
                { RequiresReload = true, BackupPath = backup };
        }
        finally
        {
            if (writerLock is not null)
            {
                writerLock.Dispose();
                try { File.Delete(lockPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
    private string? CleanDraft(string path, string? identity)
    {
        if (identity is null) return null;
        // Check our created identity before cleanup; preserve detected replacements.
        // This does not isolate hostile writers with access to the same directory.
        try
        {
            if (EntryIdentity(path) != identity) return null;
            try { File.Delete(path); return null; }
            catch (Exception cleanup) when (Expected(cleanup)) { return EntryIdentity(path) == identity ? path : null; }
        }
        catch (Exception inspect) when (Expected(inspect)) { return null; }
    }
    private void MatchBaseline()
    {
        if (baseline is null || ReadFile(FilePath).Stamp != baseline) throw Issue(StateFileProblemCode.Conflict, "The state file changed externally. Reload before saving.");
    }
    private Contents ReadFile(string path)
    {
        try
        {
            CheckLocation(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > maximumBytes) throw Issue(StateFileProblemCode.FileTooLarge, "The state file exceeds its size limit.");
            string identity = PortableBatchFiles.ReadOpenFileIdentity(stream.SafeFileHandle); long modified = File.GetLastWriteTimeUtc(path).Ticks;
            using var memory = new MemoryStream(); byte[] buffer = new byte[16384]; int count;
            while ((count = stream.Read(buffer, 0, Math.Min(buffer.Length, maximumBytes + 1 - (int)memory.Length))) > 0)
            { memory.Write(buffer, 0, count); if (memory.Length > maximumBytes) throw Issue(StateFileProblemCode.FileTooLarge, "The state file grew beyond its size limit."); }
            byte[] bytes = memory.ToArray();
            if (stream.Length != bytes.Length || PortableBatchFiles.ReadOpenFileIdentity(stream.SafeFileHandle) != identity || File.GetLastWriteTimeUtc(path).Ticks != modified)
                throw Issue(StateFileProblemCode.Conflict, "The state file changed while being read.");
            return new(bytes, new(true, Convert.ToHexString(SHA256.HashData(bytes)), identity, bytes.Length, modified));
        }
        catch (FileNotFoundException) { return new([], new(false, "", "", 0, 0)); }
        catch (DirectoryNotFoundException) { return new([], new(false, "", "", 0, 0)); }
    }
    private string? EntryIdentity(string path)
    {
        try { CheckLocation(path); using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); return PortableBatchFiles.ReadOpenFileIdentity(stream.SafeFileHandle); }
        catch (FileNotFoundException) { return null; } catch (DirectoryNotFoundException) { return null; }
    }
    private void CheckLocation(string path)
    {
        foreach (string candidate in new[] { Path.GetDirectoryName(path)!, path })
        {
            try
            {
                var attributes = File.GetAttributes(candidate);
                if ((attributes & FileAttributes.ReparsePoint) != 0 || candidate == path && (attributes & FileAttributes.Directory) != 0)
                    throw Issue(StateFileProblemCode.Io, "State files and their immediate parent must not be symbolic links or reparse points.");
            }
            catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
        }
    }
    private StateFileException Issue(StateFileProblemCode code, string message) => new(code, FilePath, message);
    private StateFileException AsProblem(Exception error) => error as StateFileException ?? new(error is JsonException or ArgumentException or InvalidDataException ? StateFileProblemCode.InvalidData : StateFileProblemCode.Io, FilePath, error.Message, error);
    private static bool Expected(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException or System.ComponentModel.Win32Exception or EntryPointNotFoundException or DllNotFoundException;
}
