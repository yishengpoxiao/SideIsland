#nullable enable
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SideIsland.Core;

public sealed record PortableWorkspaceResource(string Id, string Name, string Kind, string Target, IReadOnlyList<string> Arguments);
public sealed record PortableWorkspaceScene(string Id, string Name, IReadOnlyList<string> ResourceIds, string? Slot1 = null, string? Slot2 = null);
public sealed record PortableWorkspaceSnapshot(IReadOnlyList<PortableWorkspaceResource> Resources, IReadOnlyList<PortableWorkspaceScene> Scenes);
public enum WorkspaceProblemCode
{
    InvalidId, InvalidName, InvalidKind, InvalidTarget, InvalidArguments, InvalidLayout,
    TooManyResources, TooManyScenes, TooManySceneResources, DuplicateId, MissingResource,
    DuplicateResource, InvalidData, FileTooLarge, LoadFailed, Conflict, CommittedConflict, Busy, Io
}
public sealed class WorkspaceProblem(WorkspaceProblemCode code, string message, Exception? inner = null) : Exception(message, inner)
{
    public WorkspaceProblemCode Code { get; } = code;
    public bool DraftCommitted { get; init; }
    public bool RequiresReload { get; init; }
    public string? BackupPath { get; init; }
}
internal interface IWorkspaceFileCommit
{
    bool UsesExchange { get; }
    void Exchange(string source, string target);
    void MoveNew(string source, string target);
}
internal sealed class WorkspaceFileCommit : IWorkspaceFileCommit
{
    public bool UsesExchange => !OperatingSystem.IsWindows();
    public void Exchange(string source, string target) => PortableBatchFiles.ExchangeFiles(source, target);
    public void MoveNew(string source, string target) => PortableBatchFiles.MoveFileNoReplace(source, target);
}

/// <summary>Explicit saves only. A failed load never permits an implicit empty overwrite.</summary>
public sealed class PortableWorkspaceStore
{
    public const int MaximumResources = 60, MaximumScenes = 20, MaximumSceneResources = 30;
    public const int MaximumArguments = 32, MaximumArgumentLength = 1024, MaximumTargetLength = 4096, MaximumNameLength = 60;
    public const int MaximumFileBytes = 2 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, MaxDepth = 12, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private sealed class Document
    {
        public int Version { get; set; }
        public PortableWorkspaceResource[]? Resources { get; set; }
        public PortableWorkspaceScene[]? Scenes { get; set; }
    }
    private sealed record Stamp(bool Exists, string Hash, string Identity, long Length, long ModifiedTicks);
    private sealed record FileContents(byte[] Bytes, Stamp Stamp);
    private readonly object gate = new();
    private readonly Action<string>? checkpoint;
    private readonly IWorkspaceFileCommit files;
    private Stamp? baseline;
    private PortableWorkspaceSnapshot snapshot = Empty();
    private bool loaded, failed, needsReload;
    private WorkspaceProblem? problem;
    private string? recovery;
    public string FilePath { get; }
    public string? LastRecoveryPath { get { lock (gate) return recovery; } }
    public PortableWorkspaceSnapshot Snapshot { get { lock (gate) return snapshot; } }
    public bool LoadFailed { get { lock (gate) return failed; } }
    public bool CanRecover { get { lock (gate) return failed && baseline is not null && !needsReload; } }
    public bool NeedsReload { get { lock (gate) return needsReload; } }
    public WorkspaceProblem? Problem { get { lock (gate) return problem; } }
    public string? Error => Problem?.Message;

    public PortableWorkspaceStore(string filePath) : this(filePath, null) { }
    internal PortableWorkspaceStore(string filePath, Action<string>? checkpoint, IWorkspaceFileCommit? files = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = Path.GetFullPath(filePath); this.checkpoint = checkpoint; this.files = files ?? new WorkspaceFileCommit();
    }
    public static PortableWorkspaceSnapshot Empty() => new(Array.Empty<PortableWorkspaceResource>(), Array.Empty<PortableWorkspaceScene>());

    public static void ValidateResource(PortableWorkspaceResource resource)
    {
        if (resource is null) throw Issue(WorkspaceProblemCode.InvalidData, "A shortcut is null.");
        Id(resource.Id); Name(resource.Name);
        if (resource.Kind is not ("url" or "folder" or "program")) throw Issue(WorkspaceProblemCode.InvalidKind, "Choose URL, folder, or program.");
        if (string.IsNullOrWhiteSpace(resource.Target) || resource.Target.Length > MaximumTargetLength || !WellFormed(resource.Target) || resource.Target.Any(char.IsControl))
            throw Issue(WorkspaceProblemCode.InvalidTarget, "Enter a valid target of up to 4096 characters.");
        if (resource.Kind == "url")
        {
            if (!Uri.TryCreate(resource.Target, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0 || resource.Target != resource.Target.Trim())
                throw Issue(WorkspaceProblemCode.InvalidTarget, "Enter a complete HTTP or HTTPS URL.");
        }
        else if (!Path.IsPathFullyQualified(resource.Target)) throw Issue(WorkspaceProblemCode.InvalidTarget, "Use an absolute local path.");
        if (resource.Arguments is null || resource.Arguments.Count > MaximumArguments || resource.Kind != "program" && resource.Arguments.Count != 0)
            throw Issue(WorkspaceProblemCode.InvalidArguments, "Only programs accept up to 32 separate arguments.");
        foreach (string argument in resource.Arguments)
            if (argument is null || argument.Length > MaximumArgumentLength || argument.Contains('\0') || !WellFormed(argument))
                throw Issue(WorkspaceProblemCode.InvalidArguments, "Each argument must contain at most 1024 valid characters and no NUL.");
    }
    public static void ValidateScene(PortableWorkspaceScene scene, IEnumerable<string> resourceIds)
    {
        if (scene is null) throw Issue(WorkspaceProblemCode.InvalidData, "A scene is null.");
        Id(scene.Id); Name(scene.Name);
        if (scene.ResourceIds is null || scene.ResourceIds.Count > MaximumSceneResources) throw Issue(WorkspaceProblemCode.TooManySceneResources, "A scene accepts up to 30 shortcuts.");
        var known = resourceIds.ToHashSet(StringComparer.Ordinal); var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in scene.ResourceIds)
        {
            Id(id);
            if (!used.Add(id)) throw Issue(WorkspaceProblemCode.DuplicateResource, "A scene includes the same shortcut twice.");
            if (!known.Contains(id)) throw Issue(WorkspaceProblemCode.MissingResource, "A scene refers to a missing shortcut.");
        }
        foreach (var slot in new[] { scene.Slot1, scene.Slot2 })
            if (slot is not null && !Preferences.Widgets.Contains(slot, StringComparer.Ordinal)) throw Issue(WorkspaceProblemCode.InvalidLayout, "Choose a supported compact widget.");
    }
    public static void Validate(PortableWorkspaceSnapshot value)
    {
        if (value is null || value.Resources is null || value.Scenes is null) throw Issue(WorkspaceProblemCode.InvalidData, "The workspace document is incomplete.");
        if (value.Resources.Count > MaximumResources) throw Issue(WorkspaceProblemCode.TooManyResources, "Save up to 60 shortcuts.");
        if (value.Scenes.Count > MaximumScenes) throw Issue(WorkspaceProblemCode.TooManyScenes, "Save up to 20 scenes.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var resource in value.Resources) { ValidateResource(resource); if (!ids.Add(resource.Id)) throw Issue(WorkspaceProblemCode.DuplicateId, "Duplicate shortcut identity."); }
        var sceneIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var scene in value.Scenes) { ValidateScene(scene, ids); if (!sceneIds.Add(scene.Id)) throw Issue(WorkspaceProblemCode.DuplicateId, "Duplicate scene identity."); }
    }
    public static PortableWorkspaceSnapshot Freeze(PortableWorkspaceSnapshot value)
    {
        Validate(value);
        return new(Array.AsReadOnly(value.Resources.Select(item => item with { Arguments = Array.AsReadOnly(item.Arguments.ToArray()) }).ToArray()),
            Array.AsReadOnly(value.Scenes.Select(item => item with { ResourceIds = Array.AsReadOnly(item.ResourceIds.ToArray()) }).ToArray()));
    }
    public static PortableWorkspaceSnapshot UpsertResource(PortableWorkspaceSnapshot value, PortableWorkspaceResource item)
    {
        ValidateResource(item); var resources = value.Resources.ToList(); int index = resources.FindIndex(existing => existing.Id == item.Id);
        if (index < 0) resources.Add(item); else resources[index] = item;
        return Freeze(value with { Resources = resources });
    }
    public static PortableWorkspaceSnapshot RemoveResource(PortableWorkspaceSnapshot value, string id)
        => Freeze(new(value.Resources.Where(item => item.Id != id).ToArray(), value.Scenes.Select(scene => scene with { ResourceIds = scene.ResourceIds.Where(item => item != id).ToArray() }).ToArray()));
    public static PortableWorkspaceSnapshot UpsertScene(PortableWorkspaceSnapshot value, PortableWorkspaceScene item)
    {
        ValidateScene(item, value.Resources.Select(resource => resource.Id)); var scenes = value.Scenes.ToList(); int index = scenes.FindIndex(existing => existing.Id == item.Id);
        if (index < 0) scenes.Add(item); else scenes[index] = item;
        return Freeze(value with { Scenes = scenes });
    }
    public static PortableWorkspaceSnapshot RemoveScene(PortableWorkspaceSnapshot value, string id) => Freeze(value with { Scenes = value.Scenes.Where(item => item.Id != id).ToArray() });

    public PortableWorkspaceSnapshot Load()
    {
        lock (gate)
        {
            loaded = true; failed = false; needsReload = false; problem = null; baseline = null;
            try
            {
                var file = ReadFile(FilePath); baseline = file.Stamp;
                snapshot = file.Stamp.Exists ? Decode(file.Bytes) : Empty();
            }
            catch (Exception error) when (IsExpected(error))
            {
                failed = true; snapshot = Empty(); problem = AsProblem(error);
            }
            return snapshot;
        }
    }
    public PortableWorkspaceSnapshot Save(PortableWorkspaceSnapshot value)
    {
        var frozen = Freeze(value); byte[] bytes = Encode(frozen);
        lock (gate)
        {
            if (!loaded) Load();
            if (needsReload) throw Issue(WorkspaceProblemCode.Conflict, "Reload the workspace file before making further changes.");
            if (failed) throw Issue(WorkspaceProblemCode.LoadFailed, "Reload or explicitly back up and rebuild the unreadable workspace file.");
            try { Commit(bytes, false); }
            catch (WorkspaceProblem error) when (error.DraftCommitted) { snapshot = frozen; problem = error; throw; }
            snapshot = frozen; problem = null; return snapshot;
        }
    }
    public PortableWorkspaceSnapshot RecoverEmpty()
    {
        lock (gate)
        {
            if (!loaded) Load();
            if (needsReload) throw Issue(WorkspaceProblemCode.Conflict, "Reload the workspace file before recovering it.");
            if (!failed) throw Issue(WorkspaceProblemCode.InvalidData, "Recovery is available only after a failed load.");
            // Oversized/unreadable files have no validated baseline. Recovery
            // refuses them rather than making an unverified overwrite.
            if (baseline is null) throw Issue(WorkspaceProblemCode.LoadFailed, "The file cannot be backed up safely; restore or move it manually, then reload.");
            var empty = Empty();
            try { Commit(Encode(empty), true); }
            catch (WorkspaceProblem error) when (error.DraftCommitted) { snapshot = empty; failed = false; problem = error; throw; }
            snapshot = empty; failed = false; problem = null; return snapshot;
        }
    }
    private void Commit(byte[] bytes, bool recovering)
    {
        string directory = Path.GetDirectoryName(FilePath)!;
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        string lockPath = FilePath + ".lock";
        FileStream? writerLock = null;
        bool commitAttempted = false, draftWritten = false;
        string? backup = null;
        string? plannedBackup = null, displacedIdentity = null;
        Stamp? staging = null;
        bool temporaryMayHoldRecovery = false;
        try
        {
            Directory.CreateDirectory(directory);
            // Serializes cooperating app processes; stale locks are not removed
            // automatically because ownership cannot be safely inferred.
            try { writerLock = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
            catch (IOException error) { throw new WorkspaceProblem(WorkspaceProblemCode.Busy, "Another workspace save is in progress, or a stale save lock needs attention.", error); }
            MatchBaseline();
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { stream.Write(bytes); stream.Flush(true); }
            staging = ReadFile(temporary).Stamp;
            checkpoint?.Invoke("prepared"); MatchBaseline(); checkpoint?.Invoke("checked");
            var expected = baseline!;
            if (baseline!.Exists)
            {
                string namedBackup = FilePath + (recovering ? ".unreadable-" : ".previous-") + Guid.NewGuid().ToString("N") + ".bak";
                plannedBackup = namedBackup;
                if (files.UsesExchange)
                {
                    // .NET Unix File.Replace links/copies the backup before a
                    // separate rename. Exchange instead retains the file displaced
                    // by the same atomic operation. On NFS even an error can follow
                    // a successful rename: protect the staging name BEFORE the call.
                    backup = temporary; temporaryMayHoldRecovery = true; commitAttempted = true;
                    files.Exchange(temporary, FilePath); draftWritten = true; recovery = temporary;
                    displacedIdentity = EntryIdentity(temporary);
                    files.MoveNew(temporary, namedBackup); backup = namedBackup; recovery = backup;
                }
                else
                {
                    backup = namedBackup;
                    commitAttempted = true; File.Replace(temporary, FilePath, backup); draftWritten = true; recovery = backup;
                }
            }
            else { commitAttempted = true; files.MoveNew(temporary, FilePath); draftWritten = true; }
            checkpoint?.Invoke("committed");
            // Check the exact displaced file captured by Windows Replace or Unix
            // exchange. A racing version is retained and never blindly rolled back.
            if (backup is not null && ReadFile(backup).Stamp != expected)
                throw Issue(WorkspaceProblemCode.CommittedConflict, "The draft was written; a concurrent external version was preserved in the recovery copy.");
            // Refresh from the committed bytes only if they remain our bytes;
            // a subsequent external edit must cause the next save to conflict.
            var committed = ReadFile(FilePath);
            if (!committed.Bytes.AsSpan().SequenceEqual(bytes)) throw Issue(WorkspaceProblemCode.Conflict, "The workspace file changed during save; reload before editing again.");
            baseline = committed.Stamp;
        }
        catch (Exception error) when (IsExpected(error))
        {
            if (!commitAttempted) throw AsProblem(error);
            // A no-replace rename can also succeed remotely before reporting an
            // error. Resolve the recovery path by the displaced file's identity.
            if (displacedIdentity is not null && plannedBackup is not null)
            {
                try { if (EntryIdentity(plannedBackup) == displacedIdentity) backup = plannedBackup; }
                catch (Exception inspectError) when (IsExpected(inspectError)) { }
            }
            if (temporaryMayHoldRecovery && backup == temporary && staging is not null)
            {
                try { var remaining = ReadFile(temporary); if (!remaining.Stamp.Exists || remaining.Stamp == staging) backup = null; }
                catch (Exception inspectError) when (IsExpected(inspectError)) { }
            }
            // An error after commit must not be presented as an untouched file.
            // Confirm the current bytes before updating the caller's catalog.
            try
            {
                var current = ReadFile(FilePath);
                bool currentIsStaged = staging is not null && current.Stamp.Identity == staging.Identity && current.Bytes.AsSpan().SequenceEqual(bytes);
                if ((draftWritten || currentIsStaged) && current.Bytes.AsSpan().SequenceEqual(bytes))
                {
                    baseline = current.Stamp; needsReload = false; recovery = backup;
                    throw new WorkspaceProblem(WorkspaceProblemCode.CommittedConflict, "The draft is saved; inspect the recovery copy and save diagnostic.", error) { DraftCommitted = true, BackupPath = backup };
                }
                if (!draftWritten && current.Stamp == baseline && staging is not null && ReadFile(temporary).Stamp == staging)
                    throw new WorkspaceProblem(AsProblem(error).Code, error.Message, error);
            }
            catch (WorkspaceProblem confirmed) when (confirmed.DraftCommitted) { throw; }
            catch (WorkspaceProblem unchanged) when (!draftWritten && unchanged.InnerException == error) { throw; }
            catch (Exception readError) when (IsExpected(readError)) { }
            baseline = null; needsReload = true;
            throw new WorkspaceProblem(WorkspaceProblemCode.Conflict, "The result of the save cannot be confirmed. Reload before editing again; any recovery copy has been retained.", error) { RequiresReload = true, BackupPath = backup };
        }
        finally
        {
            try
            {
                // Never delete a possibly exchanged old version, including when
                // the native call returned an error after changing directory entries.
                bool safe = !temporaryMayHoldRecovery || staging is not null && ReadFile(temporary).Stamp == staging;
                if (safe && File.Exists(temporary)) File.Delete(temporary);
            }
            catch (Exception cleanupError) when (IsExpected(cleanupError)) { }
            if (writerLock is not null)
            {
                writerLock.Dispose();
                try { File.Delete(lockPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
    private void MatchBaseline()
    {
        if (baseline is null || ReadFile(FilePath).Stamp != baseline) throw Issue(WorkspaceProblemCode.Conflict, "The workspace file changed externally; reload before saving.");
    }
    private static FileContents ReadFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaximumFileBytes) throw Issue(WorkspaceProblemCode.FileTooLarge, "Workspace file exceeds 2 MiB.");
            string identity = PortableBatchFiles.ReadOpenFileIdentity(stream.SafeFileHandle);
            long modified = File.GetLastWriteTimeUtc(path).Ticks;
            using var memory = new MemoryStream(); byte[] buffer = new byte[16384]; int count;
            while ((count = stream.Read(buffer, 0, Math.Min(buffer.Length, MaximumFileBytes + 1 - (int)memory.Length))) > 0)
            { memory.Write(buffer, 0, count); if (memory.Length > MaximumFileBytes) throw Issue(WorkspaceProblemCode.FileTooLarge, "Workspace file grew beyond 2 MiB."); }
            byte[] bytes = memory.ToArray();
            if (stream.Length != bytes.Length || PortableBatchFiles.ReadOpenFileIdentity(stream.SafeFileHandle) != identity || File.GetLastWriteTimeUtc(path).Ticks != modified)
                throw Issue(WorkspaceProblemCode.Conflict, "Workspace file changed while being read.");
            return new(bytes, new(true, Convert.ToHexString(SHA256.HashData(bytes)), identity, bytes.Length, modified));
        }
        catch (FileNotFoundException) { return new([], new(false, "", "", 0, 0)); }
        catch (DirectoryNotFoundException) { return new([], new(false, "", "", 0, 0)); }
    }
    private static string? EntryIdentity(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return PortableBatchFiles.ReadOpenFileIdentity(stream.SafeFileHandle);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
    private static PortableWorkspaceSnapshot Decode(byte[] bytes)
    {
        int start = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
        string json = Utf8.GetString(bytes, start, bytes.Length - start);
        using var parsed = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 12 }); RejectDuplicateProperties(parsed.RootElement);
        var document = JsonSerializer.Deserialize<Document>(json, Json) ?? throw Issue(WorkspaceProblemCode.InvalidData, "Workspace document is null.");
        if (document.Version != 1 || document.Resources is null || document.Scenes is null) throw Issue(WorkspaceProblemCode.InvalidData, "Workspace document version or collections are invalid.");
        return Freeze(new(document.Resources, document.Scenes));
    }
    private static byte[] Encode(PortableWorkspaceSnapshot value)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new Document { Version = 1, Resources = value.Resources.ToArray(), Scenes = value.Scenes.ToArray() }, Json);
        if (bytes.Length > MaximumFileBytes) throw Issue(WorkspaceProblemCode.FileTooLarge, "Workspace file exceeds 2 MiB.");
        return bytes;
    }
    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject()) { if (!names.Add(property.Name)) throw Issue(WorkspaceProblemCode.InvalidData, "Duplicate JSON property."); RejectDuplicateProperties(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) RejectDuplicateProperties(item);
    }
    private static void Id(string value)
    {
        if (!Guid.TryParseExact(value, "N", out var guid) || guid.ToString("N") != value) throw Issue(WorkspaceProblemCode.InvalidId, "Use a unique lowercase GUID identity.");
    }
    private static void Name(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumNameLength || value.Any(char.IsControl) || !WellFormed(value)) throw Issue(WorkspaceProblemCode.InvalidName, "Names require 1–60 valid characters without control characters.");
    }
    private static bool WellFormed(string value)
    {
        for (int index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index])) { if (++index >= value.Length || !char.IsLowSurrogate(value[index])) return false; }
            else if (char.IsLowSurrogate(value[index])) return false;
        }
        return true;
    }
    private static WorkspaceProblem Issue(WorkspaceProblemCode code, string message) => new(code, message);
    private static bool IsExpected(Exception error) => error is WorkspaceProblem or IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException or System.ComponentModel.Win32Exception;
    private static WorkspaceProblem AsProblem(Exception error) => error as WorkspaceProblem ?? new(error is JsonException or ArgumentException ? WorkspaceProblemCode.InvalidData : WorkspaceProblemCode.Io, error.Message, error);
}
