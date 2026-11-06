using System.Text;
using CodexIsland;
using SideIsland.Core;

namespace SideIsland.Tests;

internal static class PortablePatchTests
{
    private static int checks;
    public static void Run()
    {
        checks = 0; string directory = Path.Combine(Path.GetTempPath(), "side-island-patch-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try { Basic(directory); ObservedConflicts(directory); CommitRaces(directory); Rejections(directory); Cancelled(directory); }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine($"PASS: {checks} portable patch save checks; captured UTF8, confirmed overwrite, observed-target conflicts, exact displaced backups, cancellation and unknown outcomes. Owned real files; injected native replies do not claim Mac/Linux execution.");
    }
    private static string Patch(string content) => TextDiff.ExportUnified("before\n", content, CancellationToken.None);
    private static void Basic(string directory)
    {
        string path = Path.Combine(directory, "basic.patch"), first = Patch("after 中文 😀\n"), second = Patch("next\n");
        var target = PortablePatchFiles.Prepare(path, first);
        Check(!target.Exists && target.ExistingLength == 0 && target.TargetPath == path && !File.Exists(path), "preparing absent destination is read-only with accurate observed metadata");
        target.Save(); Check(File.ReadAllBytes(path).SequenceEqual(new UTF8Encoding(false, true).GetBytes(first)), "new patch publishes exact strict UTF8 without BOM");
        bool reused = false; try { target.Save(); } catch (InvalidOperationException) { reused = true; } Check(reused && File.ReadAllText(path) == first, "prepared destination is one-use and cannot silently repeat an old export");
        var replacement = PortablePatchFiles.Prepare(path, second);
        Check(replacement.Exists && replacement.ExistingLength == new UTF8Encoding(false, true).GetByteCount(first), "existing destination metadata is captured before overwrite confirmation");
        replacement.Save(); Check(File.ReadAllText(path) == second, "explicitly confirmed unchanged existing destination is replaced");
        Check(!Directory.EnumerateFiles(directory).Any(file => Path.GetFileName(file).StartsWith("basic.patch.", StringComparison.Ordinal)), "normal verified replacement leaves no staging, locks or old-version debris");
    }
    private static void ObservedConflicts(string directory)
    {
        foreach (string change in new[] { "edit", "delete", "create", "identity" })
        {
            string path = Path.Combine(directory, "observed-" + change + ".patch"); byte[] original = Encoding.UTF8.GetBytes("observed original"), external = Encoding.UTF8.GetBytes("external changed version");
            if (change != "create") File.WriteAllBytes(path, original);
            var target = PortablePatchFiles.Prepare(path, Patch("mine\n"));
            if (change == "delete") File.Delete(path);
            else if (change == "identity") { string other = path + ".external"; File.WriteAllBytes(other, original); File.Move(other, path, true); }
            else File.WriteAllBytes(path, external);
            var error = Reject(() => target.Save());
            Check(!error.DraftCommitted && error.Code == StateFileProblemCode.Conflict && !error.RequiresReload, "changed observation is rejected before commit and needs fresh confirmation: " + change);
            Check(change == "delete" ? !File.Exists(path) : File.ReadAllBytes(path).SequenceEqual(change == "identity" ? original : external), "unconfirmed external version is preserved: " + change);
        }
    }
    private static void CommitRaces(string directory)
    {
        string late = Path.Combine(directory, "late-existing.patch"); File.WriteAllText(late, "observed"); byte[] external = Encoding.UTF8.GetBytes("last-moment external bytes"); string mine = Patch("mine\n");
        var lateTarget = PortablePatchFiles.Prepare(late, mine, default, phase => { if (phase == "checked") File.WriteAllBytes(late, external); });
        var displaced = Reject(() => lateTarget.Save());
        Check(displaced.DraftCommitted && File.ReadAllText(late) == mine, "last-moment replacement reports actual committed patch rather than claiming no save");
        Check(displaced.BackupPath is not null && File.ReadAllBytes(displaced.BackupPath).SequenceEqual(external), "last-moment displaced external bytes are preserved exactly");
        string appeared = Path.Combine(directory, "late-created.patch");
        var created = PortablePatchFiles.Prepare(appeared, mine, default, phase => { if (phase == "checked") File.WriteAllBytes(appeared, external); });
        var noReplace = Reject(() => created.Save());
        Check(!noReplace.DraftCommitted && File.ReadAllBytes(appeared).SequenceEqual(external), "native exclusive creation never replaces a target appearing at the commit boundary");
        Check(noReplace.BackupPath is not null && File.ReadAllText(noReplace.BackupPath) == mine, "unconfirmed exclusive-create result retains actual staged patch for inspection");
        string changedAfter = Path.Combine(directory, "after-native.patch"); File.WriteAllText(changedAfter, "old observed bytes");
        var unknown = PortablePatchFiles.Prepare(changedAfter, mine, default, phase => { if (phase == "committed") File.WriteAllBytes(changedAfter, external); });
        var outcome = Reject(() => unknown.Save());
        Check(!outcome.DraftCommitted && outcome.RequiresReload && File.ReadAllBytes(changedAfter).SequenceEqual(external), "later external change after publication is unknown and is never rolled back");
        Check(outcome.BackupPath is not null && File.ReadAllText(outcome.BackupPath) == "old observed bytes" && outcome.ToString().Contains(outcome.BackupPath, StringComparison.Ordinal), "unknown result retains original recovery bytes and concrete diagnostic path");
        string unsupported = Path.Combine(directory, "unsupported.patch");
        var unavailable = PortablePatchFiles.Prepare(unsupported, mine, default, null, new UnsupportedCommit());
        var unavailableError = Reject(() => unavailable.Save());
        Check(!unavailableError.DraftCommitted && !File.Exists(unsupported), "unsupported atomic publication never falls back to unsafe overwrite");
    }
    private static void Rejections(string directory)
    {
        string path = Path.Combine(directory, "invalid.patch"); File.WriteAllText(path, "unchanged");
        bool invalid = false; try { PortablePatchFiles.Prepare(path, "bad\ud800"); } catch (EncoderFallbackException) { invalid = true; }
        Check(invalid && File.ReadAllText(path) == "unchanged", "invalid UTF8 input fails before any file operation");
        bool oversized = false; try { PortablePatchFiles.Prepare(path, new string('x', PortablePatchFiles.MaximumPatchBytes + 1)); } catch (ArgumentException) { oversized = true; }
        Check(oversized && File.ReadAllText(path) == "unchanged", "patch byte limit rejects before replacing a file");
        string huge = Path.Combine(directory, "too-large.patch"); using (var stream = File.Create(huge)) stream.SetLength((long)PortablePatchFiles.MaximumExistingBytes + 1);
        var large = Reject(() => PortablePatchFiles.Prepare(huge, Patch("mine\n")));
        Check(large.Code == StateFileProblemCode.FileTooLarge && new FileInfo(huge).Length == (long)PortablePatchFiles.MaximumExistingBytes + 1, "oversized existing target cannot bypass complete observation with partial reads");
        string link = Path.Combine(directory, "link.patch"); File.CreateSymbolicLink(link, path);
        Reject(() => PortablePatchFiles.Prepare(link, Patch("mine\n")));
        Check(new FileInfo(link).LinkTarget is not null && File.ReadAllText(path) == "unchanged", "preparation does not follow or replace symlink destinations");
        string parent = Path.Combine(directory, "parent-link"), actual = Path.Combine(directory, "actual-parent"); Directory.CreateDirectory(actual); Directory.CreateSymbolicLink(parent, actual);
        Reject(() => PortablePatchFiles.Prepare(Path.Combine(parent, "patch.diff"), Patch("mine\n")));
        Check(Directory.GetFiles(actual).Length == 0, "linked immediate parent is rejected before publication");
        if (OperatingSystem.IsWindows())
        {
            var locked = PortablePatchFiles.Prepare(path, Patch("mine\n")); using var handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var failure = Reject(() => locked.Save()); Check(!failure.DraftCommitted && File.ReadAllText(path) == "unchanged", "actual replacement sharing failure preserves existing bytes");
        }
    }
    private static void Cancelled(string directory)
    {
        using var source = new CancellationTokenSource(); string path = Path.Combine(directory, "cancelled.patch");
        var target = PortablePatchFiles.Prepare(path, Patch("mine\n"), default, stage => { if (stage == "checked") source.Cancel(); });
        bool cancelled = false; try { target.Save(source.Token); } catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && !File.Exists(path) && Directory.GetFiles(directory, "cancelled.patch.*").Length == 0, "wrapper forwards cancellation through staging cleanup before native publication");
        bool prepareCancelled = false; try { PortablePatchFiles.Prepare(path, Patch("mine\n"), source.Token); } catch (OperationCanceledException) { prepareCancelled = true; }
        Check(prepareCancelled && !File.Exists(path), "cancelled preparation never creates a target");
    }
    private sealed class UnsupportedCommit : IWorkspaceFileCommit
    {
        public bool UsesExchange => false;
        public void Exchange(string source, string target) => throw new PlatformNotSupportedException("Owned unsupported native fixture");
        public void MoveNew(string source, string target) => throw new PlatformNotSupportedException("Owned unsupported native fixture");
    }
    private static StateFileException Reject(Action action)
    { try { action(); } catch (StateFileException error) { checks++; return error; } throw new InvalidOperationException("Expected conditional patch publication to reject this target."); }
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException("Portable patch: " + message); }
}
