using System.Text;
using CodexIsland;
using SideIsland.Core;

namespace SideIsland.Tests;

internal static class PortableFilePublicationTests
{
    private static int checks;
    public static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "side-island-publication-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            Basics(directory); Collisions(directory); Cancellation(directory); NativeErrors(directory); CleanupFailures(directory); UnknownOutcomes(directory); Manifest(directory);
            Console.WriteLine("PASS: " + checks + " exclusive publication checks; actual owned files, directory/symlink collisions, cancellation, success-before-error, unknown-result retention and whole-manifest publisher integration. Mac/Linux native execution is not implied.");
        }
        finally { Directory.Delete(directory, true); }
    }
    private static void Basics(string directory)
    {
        string target = Path.Combine(directory, "new 中文.bin"); byte[] bytes = Encoding.UTF8.GetBytes("owned complete snapshot 中文😀\n");
        string retained = Path.Combine(directory, ".side-island-publish-diagnostic.tmp");
        string diagnostic = new FilePublicationException(FilePublicationState.OutcomeUnknown, target, retained, "owned diagnostic fixture", new IOException("inner fixture")).ToString();
        Check(diagnostic.Contains(target, StringComparison.Ordinal) && diagnostic.Contains(retained, StringComparison.Ordinal) && diagnostic.Contains("OutcomeUnknown", StringComparison.Ordinal) && diagnostic.Contains("inner fixture", StringComparison.Ordinal), "local exception diagnostics retain both inspection paths, state and inner failure after UI closes");
        PortableFilePublication.WriteNew(target, bytes); Check(File.ReadAllBytes(target).SequenceEqual(bytes), "byte publication writes a complete literal snapshot");
        PortableFilePublication.WriteNew(Path.Combine(directory, "empty.bin"), Array.Empty<byte>()); Check(new FileInfo(Path.Combine(directory, "empty.bin")).Length == 0, "empty snapshots are valid");
        byte[] large = Enumerable.Range(0, 3 * 1024 * 1024).Select(index => (byte)(index % 251)).ToArray();
        string largePath = Path.Combine(directory, "large.bin"); PortableFilePublication.WriteNew(largePath, large); Check(File.ReadAllBytes(largePath).SequenceEqual(large), "streaming identity/hash verification handles a multi-megabyte payload");
        string failed = Path.Combine(directory, "encoder-failed.bin");
        var error = Expect(() => PortableFilePublication.WriteNew(failed, stream => { stream.Write(bytes); throw new IOException("owned encoding failure"); }), FilePublicationState.NotPublished);
        Check(!File.Exists(failed) && error.RetainedTemporaryPath is null && !Stages(directory).Any(), "encoder failure cannot expose a partial destination and cleans only its own staging file");
    }
    private static void Collisions(string directory)
    {
        byte[] original = [1, 2, 3], draft = [4, 5, 6]; string existing = Path.Combine(directory, "existing.bin"); File.WriteAllBytes(existing, original);
        bool encoded = false; Expect(() => PortableFilePublication.WriteNew(existing, stream => { encoded = true; stream.Write(draft); }), FilePublicationState.NotPublished);
        Check(!encoded && File.ReadAllBytes(existing).SequenceEqual(original), "existing file rejected before invoking encoder");
        string existingDirectory = Path.Combine(directory, "existing-directory"); Directory.CreateDirectory(existingDirectory);
        Expect(() => PortableFilePublication.WriteNew(existingDirectory, draft), FilePublicationState.NotPublished); Check(Directory.Exists(existingDirectory), "existing directory remains intact");
        foreach (bool dangling in new[] { false, true })
        {
            string link = Path.Combine(directory, dangling ? "dangling-link.bin" : "live-link.bin"); File.CreateSymbolicLink(link, dangling ? Path.Combine(directory, "not-there.bin") : existing);
            Expect(() => PortableFilePublication.WriteNew(link, draft), FilePublicationState.NotPublished);
            Check(new FileInfo(link).LinkTarget is not null && File.ReadAllBytes(existing).SequenceEqual(original), "existing symlink is never replaced, including dangling links");
        }
        foreach (string kind in new[] { "file", "directory", "symlink", "dangling" })
        {
            string target = Path.Combine(directory, "late-" + kind);
            Expect(() => PortableFilePublication.WriteNew(target, stream =>
            {
                stream.Write(draft);
                if (kind == "file") File.WriteAllBytes(target, original);
                else if (kind == "directory") Directory.CreateDirectory(target);
                else File.CreateSymbolicLink(target, kind == "symlink" ? existing : Path.Combine(directory, "not-there.bin"));
            }), FilePublicationState.NotPublished);
            Check(kind == "file" ? File.ReadAllBytes(target).SequenceEqual(original) : kind == "directory" ? Directory.Exists(target) : new FileInfo(target).LinkTarget is not null, "late " + kind + " is preserved by native exclusive publication");
            Check(!Stages(directory).Any(), "late collision cleans only disposable own staging");
        }
    }
    private static void Cancellation(string directory)
    {
        string target = Path.Combine(directory, "cancelled.bin");
        using var before = new CancellationTokenSource(); before.Cancel(); bool wrote = false;
        Cancelled(() => PortableFilePublication.WriteNew(target, stream => { wrote = true; stream.WriteByte(1); }, before.Token));
        Check(!wrote && !File.Exists(target), "pre-cancelled write creates no staging or destination");
        using var during = new CancellationTokenSource();
        Cancelled(() => PortableFilePublication.WriteNew(target, stream => { stream.WriteByte(2); during.Cancel(); }, during.Token));
        Check(!File.Exists(target) && !Stages(directory).Any(), "cancellation after encoding prevents native publication and removes own staging");
        using var after = new CancellationTokenSource();
        PortableFilePublication.WriteNew(target, stream => stream.WriteByte(3), after.Token, (source, destination) => { PortableBatchFiles.MoveFileNoReplace(source, destination); after.Cancel(); });
        Check(File.ReadAllBytes(target).SequenceEqual(new byte[] { 3 }), "cancellation after native publication does not misreport an already saved file");
    }
    private static void NativeErrors(string directory)
    {
        string target = Path.Combine(directory, "unsupported.bin"); int calls = 0;
        Expect(() => PortableFilePublication.WriteNew(target, stream => stream.WriteByte(1), default, (_, _) => { calls++; throw new PlatformNotSupportedException("owned ENOTSUP fixture"); }), FilePublicationState.NotPublished);
        Check(calls == 1 && !File.Exists(target) && !Stages(directory).Any(), "unsupported exclusive move fails without any unsafe fallback");
        target = Path.Combine(directory, "committed-before-error.bin");
        PortableFilePublication.WriteNew(target, stream => stream.WriteByte(7), default, (source, destination) => { PortableBatchFiles.MoveFileNoReplace(source, destination); throw new IOException("owned success before errno"); });
        Check(File.ReadAllBytes(target).SequenceEqual(new byte[] { 7 }) && !Stages(directory).Any(), "identity and complete bytes confirm success even when native move reports failure");
        target = Path.Combine(directory, "committed-before-cancel-error.bin");
        PortableFilePublication.WriteNew(target, stream => stream.WriteByte(6), default, (source, destination) => { PortableBatchFiles.MoveFileNoReplace(source, destination); throw new OperationCanceledException("owned cancellation after native success"); });
        Check(File.ReadAllBytes(target).SequenceEqual(new byte[] { 6 }), "cancellation exception after native success cannot hide a confirmed saved file");
        target = Path.Combine(directory, "copied-not-moved.bin");
        Expect(() => PortableFilePublication.WriteNew(target, stream => stream.WriteByte(8), default, (source, destination) => { File.Copy(source, destination, false); throw new IOException("owned foreign inode with matching bytes"); }), FilePublicationState.NotPublished);
        Check(File.ReadAllBytes(target).SequenceEqual(new byte[] { 8 }), "equal bytes in a different identity cannot masquerade as our publication or be deleted");
    }
    private static void CleanupFailures(string directory)
    {
        if (!OperatingSystem.IsWindows()) return; // Real Windows sharing semantics, not an emulated Unix claim.
        foreach (bool encoderFailure in new[] { false, true })
        {
            string target = Path.Combine(directory, encoderFailure ? "cleanup-encoder.bin" : "cleanup-native.bin"); string? staging = null; FileStream? held = null;
            try
            {
                var error = Expect(() => PortableFilePublication.WriteNew(target, stream =>
                {
                    stream.WriteByte(42); stream.Flush();
                    if (encoderFailure)
                    {
                        staging = ((FileStream)stream).Name; held = new FileStream(staging, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        throw new IOException("owned interrupted encoder");
                    }
                }, default, (source, destination) =>
                {
                    staging = source;
                    held = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    PortableBatchFiles.MoveFileNoReplace(source, destination);
                }), FilePublicationState.NotPublished);
                Check(!File.Exists(target) && staging is not null && File.ReadAllBytes(staging).SequenceEqual(new byte[] { 42 }), "actual sharing failure retains owned staging after " + (encoderFailure ? "interrupted encoding" : "failed publication"));
                Check(error.RetainedTemporaryPath == staging && error.ToString().Contains(staging!, StringComparison.Ordinal), "failed cleanup reports the confirmed retained path in the exception and local diagnostics");
            }
            finally { held?.Dispose(); if (staging is not null) File.Delete(staging); }
        }
    }
    private static void UnknownOutcomes(string directory)
    {
        string target = Path.Combine(directory, "replaced-after-commit.bin"), displaced = Path.Combine(directory, "published-then-moved.bin");
        var changed = Expect(() => PortableFilePublication.WriteNew(target, stream => stream.WriteByte(9), default, (source, destination) =>
        {
            PortableBatchFiles.MoveFileNoReplace(source, destination); File.Move(destination, displaced); File.WriteAllText(destination, "later external version");
        }), FilePublicationState.OutcomeUnknown);
        Check(changed.RetainedTemporaryPath is null && changed.TargetPath == target && File.ReadAllText(target) == "later external version" && File.ReadAllBytes(displaced).SequenceEqual(new byte[] { 9 }), "later target replacement is unknown, preserves both files and never claims an absent staging path");

        target = Path.Combine(directory, "unknown-different-stage.bin"); string? foreignStage = null; string held = Path.Combine(directory, "own-stage-retained-elsewhere.bin");
        var different = Expect(() => PortableFilePublication.WriteNew(target, stream => stream.WriteByte(10), default, (source, _) =>
        {
            File.Move(source, held); foreignStage = source; File.WriteAllText(source, "foreign replacement"); throw new IOException("owned replaced-staging fixture");
        }), FilePublicationState.OutcomeUnknown);
        Check(different.RetainedTemporaryPath is null && File.ReadAllText(foreignStage!) == "foreign replacement" && File.ReadAllBytes(held).SequenceEqual(new byte[] { 10 }), "unknown operation never deletes or advertises another file at the staging name");
        File.Delete(foreignStage!);

        target = Path.Combine(directory, "unknown-locked-target.bin"); FileStream? targetLock = null;
        try
        {
            var locked = Expect(() => PortableFilePublication.WriteNew(target, stream => stream.WriteByte(11), default, (_, destination) =>
            {
                targetLock = new FileStream(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None); targetLock.WriteByte(77); targetLock.Flush(true); throw new IOException("owned unreadable-target fixture");
            }), FilePublicationState.OutcomeUnknown);
            Check(locked.RetainedTemporaryPath is not null && File.ReadAllBytes(locked.RetainedTemporaryPath).SequenceEqual(new byte[] { 11 }), "unverifiable destination retains and reports a confirmed own staging snapshot");
        }
        finally { targetLock?.Dispose(); }
        Check(File.ReadAllBytes(target).SequenceEqual(new byte[] { 77 }), "unknown inspection cannot alter the external target");

        target = Path.Combine(directory, "unknown-locked-stage.bin"); FileStream? stageLock = null; string? unknownStage = null;
        try
        {
            var unknown = Expect(() => PortableFilePublication.WriteNew(target, stream => stream.WriteByte(12), default, (source, _) =>
            {
                File.Move(source, Path.Combine(directory, "own-stage-other-name.bin")); unknownStage = source;
                stageLock = new FileStream(source, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None); stageLock.WriteByte(88); stageLock.Flush(true); throw new IOException("owned unverifiable-stage fixture");
            }), FilePublicationState.OutcomeUnknown);
            Check(unknown.RetainedTemporaryPath is null, "unverifiable staging identity is preserved but not mislabelled as owned recovery");
        }
        finally { stageLock?.Dispose(); }
        Check(File.ReadAllBytes(unknownStage!).SequenceEqual(new byte[] { 88 }), "uncertain cleanup leaves the locked foreign staging replacement intact");
    }
    private static void Manifest(string directory)
    {
        string target = Path.Combine(directory, "manifest.sha256"); string text = new string('a', 64) + " *中文 file.zip\n";
        int calls = 0;
        FileHashTools.SaveManifestNew(target, text, (destination, bytes) => { calls++; Check(destination == Path.GetFullPath(target) && Encoding.UTF8.GetString(bytes) == text, "manifest delegates exact whole UTF8 snapshot and destination"); PortableFilePublication.WriteNew(destination, bytes); });
        Check(calls == 1 && File.ReadAllText(target, Encoding.UTF8) == text && !File.ReadAllBytes(target).Take(3).SequenceEqual(new byte[] { 239, 187, 191 }), "shared manifest delegates safe complete publication without BOM");
        string invalid = Path.Combine(directory, "invalid.sha256");
        try { FileHashTools.SaveManifestNew(invalid, "invalid\ud800", (_, _) => calls++); throw new Exception("Invalid Unicode accepted"); }
        catch (EncoderFallbackException) { Check(calls == 1 && !File.Exists(invalid), "malformed UTF16 is rejected before invoking publisher"); }
        if (!OperatingSystem.IsWindows())
        {
            try { FileHashTools.SaveManifestNew(invalid, text); throw new Exception("Unsafe Unix default accepted"); }
            catch (PlatformNotSupportedException) { Check(!File.Exists(invalid), "Unix checksum caller without safe publisher is explicitly rejected"); }
        }
        else
        {
            string legacy = Path.Combine(directory, "legacy-windows.sha256"); FileHashTools.SaveManifestNew(legacy, text);
            Check(File.ReadAllText(legacy) == text, "Windows default manifest behavior remains supported");
        }
    }
    private static string[] Stages(string directory) => Directory.GetFiles(directory, ".side-island-publish-*.tmp");
    private static FilePublicationException Expect(Action action, FilePublicationState state)
    {
        try { action(); } catch (FilePublicationException error) { Check(error.State == state, "expected " + state + ", got " + error.State); return error; }
        throw new Exception("Expected publication failure: " + state);
    }
    private static void Cancelled(Action action) { try { action(); } catch (OperationCanceledException) { checks++; return; } throw new Exception("Expected publication cancellation"); }
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception("Publication: " + message); }
}
