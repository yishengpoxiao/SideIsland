#nullable enable
using System.Text;

namespace SideIsland.Core;

/// <summary>A captured patch and observed destination, to be confirmed before replacing an existing file.</summary>
public sealed class PortablePatchTarget
{
    private readonly PortableStateFile persistence;
    private readonly byte[] patch;
    private int used;
    public string TargetPath => persistence.FilePath;
    public bool Exists { get; }
    public long ExistingLength { get; }
    internal PortablePatchTarget(PortableStateFile persistence, byte[] patch, bool exists, long existingLength)
    { this.persistence = persistence; this.patch = patch; Exists = exists; ExistingLength = existingLength; }

    /// <summary>Publishes once against the observed version. Confirm Exists with the user before calling.</summary>
    public void Save(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref used, 1) != 0) throw new InvalidOperationException("This patch destination has already been used. Observe and confirm a fresh destination before saving again.");
        persistence.Save(patch, cancellationToken);
    }
}

public static class PortablePatchFiles
{
    public const int MaximumPatchBytes = 2 * 1024 * 1024;
    public const int MaximumExistingBytes = 64 * 1024 * 1024;
    public static PortablePatchTarget Prepare(string path, string patch, CancellationToken cancellationToken = default)
        => Prepare(path, patch, cancellationToken, null, null, null);

    internal static PortablePatchTarget Prepare(string path, string patch, CancellationToken cancellationToken,
        Action<string>? checkpoint, IWorkspaceFileCommit? files = null, Action<Stream, byte[]>? writeBytes = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(patch);
        cancellationToken.ThrowIfCancellationRequested();
        // Validate and freeze UTF-8 before examining or creating filesystem entries.
        var encoding = new UTF8Encoding(false, true);
        if (encoding.GetByteCount(patch) > MaximumPatchBytes) throw new ArgumentException("The exported patch exceeds 2 MiB.", nameof(patch));
        byte[] bytes = encoding.GetBytes(patch);
        var persistence = new PortableStateFile(path, MaximumExistingBytes, checkpoint, files, writeBytes);
        var observed = persistence.Load(data => (Exists: true, Length: (long)data.Length), () => (Exists: false, Length: 0L));
        cancellationToken.ThrowIfCancellationRequested();
        StateFileStatus state = persistence.Status;
        if (state.LoadFailed)
            throw new StateFileException(state.ProblemCode ?? StateFileProblemCode.LoadFailed, persistence.FilePath,
                "The patch destination could not be inspected. Nothing was saved. " + state.Message)
                { RequiresReload = true, BackupPath = state.RecoveryPath };
        return new PortablePatchTarget(persistence, bytes, observed.Exists, observed.Length);
    }
}
