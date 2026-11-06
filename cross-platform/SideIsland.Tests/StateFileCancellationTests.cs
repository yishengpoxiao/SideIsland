using System.Text;
using SideIsland.Core;

namespace SideIsland.Tests;

internal static class StateFileCancellationTests
{
    private static int checks;
    public static void Run()
    {
        checks = 0; string directory = Path.Combine(Path.GetTempPath(), "side-island-state-cancel-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try { BeforeCommit(directory); PartialWrite(directory); AfterCommit(directory); }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine($"PASS: {checks} state-file cancellation checks; real staging cleanup, retained-file diagnostics and committed-result verification. Owned local files only.");
    }
    private static void BeforeCommit(string directory)
    {
        foreach (string stage in new[] { "before", "prepared", "checked" }) foreach (bool exists in new[] { false, true })
        {
            string path = Path.Combine(directory, stage + exists + ".txt"); if (exists) File.WriteAllText(path, "original");
            using var cancellation = new CancellationTokenSource();
            var state = new PortableStateFile(path, 1024, point => { if (point == stage) cancellation.Cancel(); }); Load(state);
            if (stage == "before") cancellation.Cancel();
            bool cancelled = false; try { state.Save(Bytes("new bytes"), cancellation.Token); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "precommit cancellation propagates: " + stage + exists);
            Check(exists ? File.ReadAllText(path) == "original" : !File.Exists(path), "cancelled operation never changes target: " + stage + exists);
            Check(!File.Exists(path + ".lock") && Directory.GetFiles(directory, Path.GetFileName(path) + ".*.tmp").Length == 0, "cancelled operation removes own staging and lock: " + stage + exists);
        }
    }
    private static void PartialWrite(string directory)
    {
        foreach (bool retain in new[] { false, true })
        {
            if (retain && !OperatingSystem.IsWindows()) continue;
            string path = Path.Combine(directory, "partial-" + retain + ".txt"); File.WriteAllText(path, "original");
            using var cancellation = new CancellationTokenSource(); FileStream? held = null;
            var state = new PortableStateFile(path, 1024, null, writeBytes: (stream, bytes) =>
            {
                stream.Write(bytes.AsSpan(0, 3)); ((FileStream)stream).Flush(true);
                if (retain) held = new FileStream(((FileStream)stream).Name, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested();
            }); Load(state);
            try
            {
                Exception? failure = null; try { state.Save(Bytes("partial snapshot"), cancellation.Token); } catch (Exception error) { failure = error; }
                Check(File.ReadAllText(path) == "original" && !File.Exists(path + ".lock"), "partial cancelled write preserves target and releases own lock");
                if (retain)
                {
                    Check(failure is StateFileException { DraftCommitted: false, BackupPath: not null }, "cancelled staging cleanup failure preserves diagnostic instead of a bare cancellation");
                    var error = (StateFileException)failure!;
                    Check(File.ReadAllText(error.BackupPath!) == "par" && error.ToString().Contains(error.BackupPath!, StringComparison.Ordinal) && state.Status.RecoveryPath == error.BackupPath, "retained partial bytes are real and present in exception and immutable status");
                }
                else Check(failure is OperationCanceledException && Directory.GetFiles(directory, Path.GetFileName(path) + ".*.tmp").Length == 0, "partial cancelled write removes complete ownership-checked temporary entry");
            }
            finally { held?.Dispose(); }
        }
    }
    private static void AfterCommit(string directory)
    {
        foreach (bool exists in new[] { false, true })
        {
            string path = Path.Combine(directory, "post-commit-" + exists + ".txt"); if (exists) File.WriteAllText(path, "original");
            using var cancellation = new CancellationTokenSource();
            var state = new PortableStateFile(path, 1024, point => { if (point == "committed") cancellation.Cancel(); }); Load(state);
            state.Save(Bytes("confirmed"), cancellation.Token);
            Check(cancellation.IsCancellationRequested && File.ReadAllText(path) == "confirmed" && !state.Status.Dirty, "cancellation after native publication cannot mislabel a confirmed save");
        }
        string observed = Path.Combine(directory, "native-reported-cancel.txt");
        var unexpected = new PortableStateFile(observed, 1024, null, new CommitAfterCancellation()); Load(unexpected);
        StateFileException? warning = null; try { unexpected.Save(Bytes("published"), CancellationToken.None); } catch (StateFileException error) { warning = error; }
        Check(warning is { DraftCommitted: true } && File.ReadAllText(observed) == "published" && !unexpected.Status.Dirty, "native move followed by cancellation-shaped error is verified as committed");
    }
    private sealed class CommitAfterCancellation : IWorkspaceFileCommit
    {
        public bool UsesExchange => false;
        public void Exchange(string source, string target) => throw new NotSupportedException();
        public void MoveNew(string source, string target) { File.Move(source, target); throw new OperationCanceledException("Owned native reply after actual move"); }
    }
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
    private static void Load(PortableStateFile state) => state.Load(Encoding.UTF8.GetString, () => "");
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException("State cancellation: " + message); }
}
