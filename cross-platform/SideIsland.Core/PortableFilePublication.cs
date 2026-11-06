#nullable enable
using System.ComponentModel;
using System.Security.Cryptography;

namespace SideIsland.Core;

public enum FilePublicationState { NotPublished, OutcomeUnknown }
public sealed class FilePublicationException(FilePublicationState state, string targetPath, string? retainedTemporaryPath, string message, Exception? inner = null) : IOException(message, inner)
{
    public FilePublicationState State { get; } = state;
    public string TargetPath { get; } = targetPath;
    public string? RetainedTemporaryPath { get; private set; } = retainedTemporaryPath;
    internal void RecordRetainedTemporary(string path) => RetainedTemporaryPath = path;
    public override string ToString() => base.ToString() + Environment.NewLine +
        "Publication state: " + State + Environment.NewLine + "Target path: " + TargetPath + Environment.NewLine +
        "Retained staging path: " + (RetainedTemporaryPath ?? "(none confirmed)");
}

/// <summary>A complete sibling staging file is published without replacing any existing directory entry.</summary>
public static class PortableFilePublication
{
    private sealed record Stamp(string Identity, long Length, string Hash);
    private enum Probe { Absent, Same, Different, Unverifiable }

    public static void WriteNew(string destination, byte[] bytes, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        token.ThrowIfCancellationRequested();
        // The caller may own a mutable buffer. Its later edits cannot alter the
        // staged snapshot while a background publication is still running.
        byte[] frozen = (byte[])bytes.Clone();
        WriteNew(destination, stream => stream.Write(frozen), token);
    }
    public static void WriteNew(string destination, Action<Stream> write, CancellationToken token = default)
        => WriteNew(destination, write, token, PortableBatchFiles.MoveFileNoReplace);

    internal static void WriteNew(string destination, Action<Stream> write, CancellationToken token, Action<string, string> publish)
    {
        ArgumentNullException.ThrowIfNull(write); ArgumentNullException.ThrowIfNull(publish);
        token.ThrowIfCancellationRequested();
        string target = Path.GetFullPath(destination);
        if (EntryExists(target)) throw Failure(FilePublicationState.NotPublished, target, null, "The destination already exists. Choose a new filename.");
        string temporary = Path.Combine(Path.GetDirectoryName(target)!, ".side-island-publish-" + Guid.NewGuid().ToString("N") + ".tmp");
        string? ownedIdentity = null;
        Stamp? staged = null;
        bool attempted = false, preserve = false;
        FileStream? output = null;
        FilePublicationException? failure = null;
        try
        {
            output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete, 65536, FileOptions.SequentialScan);
            ownedIdentity = PortableBatchFiles.ReadOpenFileIdentity(output.SafeFileHandle);
            write(output); output.Flush(true);
            staged = Capture(output, token);
            token.ThrowIfCancellationRequested(); // Cancellation has no authority to undo a subsequent native publish.
            attempted = true;
            try { publish(temporary, target); }
            catch (Exception nativeError) when (Expected(nativeError))
            {
                // NFS can complete a rename and still report failure. Verify the
                // actual entry before labelling the user's save unsuccessful.
                Probe destinationState = Inspect(target, staged);
                if (destinationState == Probe.Same) return;
                Probe stagingState = Inspect(temporary, staged);
                if (stagingState == Probe.Same && destinationState is Probe.Absent or Probe.Different)
                    throw Failure(FilePublicationState.NotPublished, target, null, "The file was not published. The destination was not replaced.", nativeError);
                preserve = true;
                throw Failure(FilePublicationState.OutcomeUnknown, target, stagingState == Probe.Same ? temporary : null,
                    "The save outcome could not be confirmed. Inspect the destination and any retained staging file before trying again.", nativeError);
            }
            if (Inspect(target, staged) != Probe.Same)
            {
                preserve = true;
                throw Failure(FilePublicationState.OutcomeUnknown, target, Inspect(temporary, staged) == Probe.Same ? temporary : null,
                    "The native publication completed, but the current destination could not be verified.");
            }
        }
        catch (FilePublicationException error) { failure = error; throw; }
        catch (OperationCanceledException) when (!attempted) { throw; }
        catch (Exception error) when (Expected(error))
        {
            preserve = attempted;
            failure = Failure(attempted ? FilePublicationState.OutcomeUnknown : FilePublicationState.NotPublished, target,
                attempted && staged is not null && Inspect(temporary, staged) == Probe.Same ? temporary : null,
                attempted ? "The save outcome could not be confirmed." : "The staging file could not be prepared; no file was published.", error);
            throw failure;
        }
        finally
        {
            output?.Dispose();
            if (!preserve && ownedIdentity is not null)
            {
                // Check staged identity/content before cleanup; preserve detected
                // replacements and unknown outcomes. This is not isolation from
                // hostile writers who can rename entries in the same directory.
                try
                {
                    bool same = staged is not null ? Inspect(temporary, staged) == Probe.Same : OwnIdentity(temporary, ownedIdentity);
                    if (same) File.Delete(temporary);
                }
                catch (Exception cleanupError) when (Expected(cleanupError))
                {
                    // A sharing violation can prevent both the native rename
                    // and deletion. Report only a still-recognized owned file;
                    // never advertise an unchecked replacement as recovery data.
                    try
                    {
                        bool same = staged is not null ? Inspect(temporary, staged) == Probe.Same : OwnIdentity(temporary, ownedIdentity);
                        if (same) failure?.RecordRetainedTemporary(temporary);
                    }
                    catch (Exception inspectionError) when (Expected(inspectionError)) { }
                }
            }
        }
    }

    private static bool EntryExists(string path)
    {
        try { File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
    private static bool OwnIdentity(string path, string identity)
    {
        if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) return false;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return PortableBatchFiles.ReadOpenFileIdentity(stream.SafeFileHandle) == identity;
    }
    private static Probe Inspect(string path, Stamp expected)
    {
        try
        {
            if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) return Probe.Different;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan);
            // Only read bytes if this is our own inode/file identity; an external
            // target is neither hashed nor modified just because its name collided.
            if (PortableBatchFiles.ReadOpenFileIdentity(stream.SafeFileHandle) != expected.Identity || stream.Length != expected.Length) return Probe.Different;
            return Capture(stream, CancellationToken.None) == expected ? Probe.Same : Probe.Different;
        }
        catch (FileNotFoundException) { return Probe.Absent; }
        catch (DirectoryNotFoundException) { return Probe.Absent; }
        catch (Exception error) when (Expected(error)) { return Probe.Unverifiable; }
    }
    private static Stamp Capture(FileStream stream, CancellationToken token)
    {
        string identity = PortableBatchFiles.ReadOpenFileIdentity(stream.SafeFileHandle);
        long length = stream.Length, remaining = length; stream.Position = 0;
        byte[] buffer = new byte[65536];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (remaining > 0)
        {
            token.ThrowIfCancellationRequested(); int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0) throw new IOException("The staging file changed while its content was being checked.");
            hash.AppendData(buffer, 0, read); remaining -= read;
        }
        token.ThrowIfCancellationRequested();
        if (stream.Length != length || PortableBatchFiles.ReadOpenFileIdentity(stream.SafeFileHandle) != identity) throw new IOException("The staging file changed while its content was being checked.");
        return new(identity, length, Convert.ToHexString(hash.GetHashAndReset()));
    }
    private static FilePublicationException Failure(FilePublicationState state, string target, string? retained, string message, Exception? inner = null) => new(state, target, retained, message, inner);
    private static bool Expected(Exception error) => error is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException or NotSupportedException or InvalidOperationException or System.Security.SecurityException or EntryPointNotFoundException or DllNotFoundException or OperationCanceledException;
}
