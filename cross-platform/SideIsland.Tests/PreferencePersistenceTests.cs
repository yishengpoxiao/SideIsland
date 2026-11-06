#nullable enable
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SideIsland.Core;

namespace SideIsland.Tests;

internal static class PreferencePersistenceTests
{
    private static int checks;
    public static void Run()
    {
        checks = 0;
        string root = Path.Combine(Path.GetTempPath(), "side-island-preferences-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            PreferencesRoundtrip(root); Recovery(root); Protection(root); CommitOutcomes(root); Cleanup(root); ResponsiveState(root);
            Console.WriteLine("PASS: " + checks + " preference/state persistence checks; explicit recovery, exact external-version protection, bounded reads, ordinary-backup cleanup, partial-write diagnostics and nonblocking immutable UI state. Owned temporary files only; exchange adapter fixtures do not establish native Mac/Linux validation.");
        }
        finally { Directory.Delete(root, true); }
    }
    private static string DirectoryFor(string root)
    { string value = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(value); return value; }
    private static string StatePath(string root) => Path.Combine(DirectoryFor(root), "state.json");
    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
    private static string Load(PortableStateFile file) => file.Load(Encoding.UTF8.GetString, () => "fallback");
    private static void Check(bool value, string label)
    { if (!value) throw new InvalidOperationException(label); checks++; }
    private static StateFileException Reject(Action action, StateFileProblemCode? code = null)
    {
        try { action(); } catch (StateFileException error) { Check(code is null || error.Code == code, "expected persistence problem: " + code + ", received " + error.Code); return error; }
        throw new InvalidOperationException("Expected a state file rejection.");
    }
    private static void PreferencesRoundtrip(string root)
    {
        string directory = DirectoryFor(root); var store = new PreferenceStore(directory); var current = store.Load();
        Check(!File.Exists(store.FilePath) && !store.PersistenceStatus.Dirty, "load missing preferences creates no file");
        current.Notes = "中文😀"; current.Language = "en"; current.HoverDelayMs = -10; current.ExpandedWidth = double.NaN;
        store.Save(current); var loaded = new PreferenceStore(directory).Load();
        Check(loaded.Notes == "中文😀" && loaded.Language == "en" && loaded.HoverDelayMs == 100 && loaded.ExpandedWidth == 480, "normalize a saved snapshot");
        Check(current.HoverDelayMs == -10 && double.IsNaN(current.ExpandedWidth), "save does not mutate the caller draft");
        Check(!store.PersistenceStatus.Dirty && store.PersistenceStatus.ProblemCode is null, "successful save clears dirty state");
        foreach (string notes in new[] { new string('中', 60000), string.Concat(Enumerable.Repeat("😀", 30000)), new string('a', 60000) })
        { current.Notes = notes; store.Save(current); Check(new PreferenceStore(directory).Load().Notes == notes, "maximum allowed notes roundtrip"); }
        Check(Directory.GetFiles(directory, "*.previous-*.bak").Length == 0, "ordinary automatic saves do not accumulate full notes copies");
        byte[] before = File.ReadAllBytes(store.FilePath); current.CodexPath = new string('x', 1024 * 1024 + 1);
        Reject(() => store.Save(current), StateFileProblemCode.FileTooLarge);
        Check(before.SequenceEqual(File.ReadAllBytes(store.FilePath)) && store.PersistenceStatus.Dirty, "oversized draft preserves prior bytes and dirty flag");
        var copy = loaded.Copy(); copy.Notes = "new copy"; Check(loaded.Notes == "中文😀", "draft copy independent");
        var destination = new Preferences(); destination.CopyFrom(copy);
        Check(JsonSerializer.Serialize(destination) == JsonSerializer.Serialize(copy), "CopyFrom copies every current preference field");
        var external = new PreferenceStore(directory); var disk = external.Load(); disk.Notes = "external"; external.Save(disk);
        current.CodexPath = ""; current.Notes = "unsaved mine";
        Reject(() => store.Save(current), StateFileProblemCode.Conflict);
        Check(new PreferenceStore(directory).Load().Notes == "external" && store.PersistenceStatus.NeedsReload && store.PersistenceStatus.Dirty, "external preference changes are not overwritten");
        var reloaded = store.Reload(current); Check(reloaded.Notes == "external" && !store.PersistenceStatus.NeedsReload && !store.PersistenceStatus.Dirty, "explicit reload adopts external state");
    }
    private static void Recovery(string root)
    {
        string directory = DirectoryFor(root), path = Path.Combine(directory, "preferences.json"); const string corrupt = "{\"Notes\":\"original\", BAD";
        File.WriteAllText(path, corrupt); var store = new PreferenceStore(directory); var draft = store.Load(); draft.Notes = "mine";
        Check(store.PersistenceStatus.LoadFailed && store.PersistenceStatus.CanRecover, "invalid JSON has an exact recoverable baseline");
        Reject(() => store.Save(draft), StateFileProblemCode.LoadFailed);
        Check(File.ReadAllText(path) == corrupt && Directory.GetFiles(directory, "*.bak").Length == 0, "automatic save does not replace corrupt original");
        if (OperatingSystem.IsWindows())
        {
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                Reject(() => store.RecoverAndSave(draft));
                Check(store.PersistenceStatus.LoadFailed && store.PersistenceStatus.CanRecover && store.PersistenceStatus.Dirty, "failed commit does not clear corrupt recovery status");
                Check(File.ReadAllText(path) == corrupt, "locked recovery keeps malformed original");
            }
        }
        File.WriteAllText(path, "{replacement bad B"); Reject(() => store.RecoverAndSave(draft), StateFileProblemCode.Conflict);
        Check(File.ReadAllText(path) == "{replacement bad B" && !store.PersistenceStatus.CanRecover, "failed recovery followed by external edit requires reload");
        Check(ReferenceEquals(store.Reload(draft), draft) && store.PersistenceStatus.Dirty && store.PersistenceStatus.CanRecover, "failed explicit reload preserves current unsaved draft");
        store.RecoverAndSave(draft);
        string recovery = store.PersistenceStatus.RecoveryPath!;
        Check(File.ReadAllText(recovery) == "{replacement bad B" && new PreferenceStore(directory).Load().Notes == "mine", "explicit recovery backs up exact current malformed version");
        Check(!store.PersistenceStatus.LoadFailed && !store.PersistenceStatus.Dirty, "successful recovery settles flags");
        store.Save(draft); Check(File.Exists(recovery) && File.ReadAllText(recovery) == "{replacement bad B", "later ordinary save retains malformed recovery copy");
        Reject(() => store.RecoverAndSave(draft), StateFileProblemCode.LoadFailed);
        File.WriteAllText(path, "null"); Check(ReferenceEquals(store.Reload(draft), draft) && store.PersistenceStatus.LoadFailed, "JSON null cannot crash load or erase in-memory draft");
        File.Delete(path); var empty = store.Reload(draft); Check(empty.Notes == "" && !store.PersistenceStatus.LoadFailed, "explicit reload of deleted file returns defaults without writing");
    }
    private static void Protection(string root)
    {
        string path = StatePath(root); File.WriteAllText(path, new string('x', 65)); var state = new PortableStateFile(path, 64);
        Check(Load(state) == "fallback" && state.Status.LoadFailed && !state.Status.CanRecover && state.Status.ProblemCode == StateFileProblemCode.FileTooLarge, "oversized source is bounded and cannot be recovered without a baseline");
        Reject(() => state.RecoverAndSave(Bytes("mine")), StateFileProblemCode.LoadFailed);
        Check(File.ReadAllText(path).Length == 65, "oversized source preserved");
        string invalid = StatePath(root); File.WriteAllText(invalid, "null"); var invalidState = new PortableStateFile(invalid, 64);
        Check(invalidState.Load<string>(_ => throw new InvalidDataException("schema null"), () => "fallback") == "fallback" && invalidState.Status.ProblemCode == StateFileProblemCode.InvalidData, "schema InvalidDataException is a recoverable load failure");
        string locked = StatePath(root); File.WriteAllText(locked, "original"); var lockState = new PortableStateFile(locked, 64);
        using (var held = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Check(Load(lockState) == "fallback" && !lockState.Status.CanRecover, "unreadable file lacks a recovery baseline");
        Reject(() => lockState.RecoverAndSave(Bytes("mine")), StateFileProblemCode.LoadFailed);
        string occupied = StatePath(root); File.WriteAllText(occupied, "original"); var occupiedState = new PortableStateFile(occupied, 64); Load(occupiedState);
        File.WriteAllText(occupied + ".lock", "external lock"); Reject(() => occupiedState.Save(Bytes("mine")), StateFileProblemCode.Busy);
        Check(File.ReadAllText(occupied + ".lock") == "external lock" && File.ReadAllText(occupied) == "original", "foreign cooperative lock is never removed");
        string target = StatePath(root); File.WriteAllText(target, "real"); string link = StatePath(root);
        File.CreateSymbolicLink(link, target); var linked = new PortableStateFile(link, 64); Load(linked);
        Check(linked.Status.LoadFailed && !linked.Status.CanRecover, "live symbolic state file rejected");
        Reject(() => linked.Save(Bytes("mine"))); Check(File.ReadAllText(target) == "real" && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0, "symbolic link and target remain intact");
        string dangling = StatePath(root); File.CreateSymbolicLink(dangling, Path.Combine(root, "not-created")); var danglingState = new PortableStateFile(dangling, 64); Load(danglingState);
        Check(danglingState.Status.LoadFailed && !danglingState.Status.CanRecover, "dangling symbolic state file rejected");
        string linkedDirectory = Path.Combine(root, "linked-dir"); Directory.CreateSymbolicLink(linkedDirectory, Path.GetDirectoryName(target)!);
        var parentState = new PortableStateFile(Path.Combine(linkedDirectory, "new-state"), 64); Load(parentState);
        Check(parentState.Status.LoadFailed && !parentState.Status.CanRecover, "direct symbolic parent rejected");
        string fresh = StatePath(root); var noLoad = new PortableStateFile(fresh, 64); Reject(() => noLoad.Save(Bytes("mine")), StateFileProblemCode.LoadFailed);
        Check(!File.Exists(fresh), "saving an uninitialized primitive cannot overwrite unknown data");
    }
    private sealed class ExchangeFixture(Action<string, string>? exchange = null, Action<string, string>? move = null) : IWorkspaceFileCommit
    {
        public bool UsesExchange => true;
        public void Exchange(string source, string target)
        {
            if (exchange is not null) { exchange(source, target); return; }
            Swap(source, target);
        }
        public void MoveNew(string source, string target) { if (move is not null) move(source, target); else File.Move(source, target, false); }
        internal static void Swap(string source, string target)
        { string held = source + ".swap"; File.Move(source, held); File.Move(target, source); File.Move(held, target); }
    }
    private static void CommitOutcomes(string root)
    {
        foreach (bool failAfterNative in new[] { false, true })
        {
            string path = StatePath(root); File.WriteAllText(path, "old");
            var adapter = new ExchangeFixture((source, target) => { ExchangeFixture.Swap(source, target); if (failAfterNative) throw new IOException("after exchange"); });
            var state = new PortableStateFile(path, 128, null, adapter); Load(state);
            if (!failAfterNative) { state.Save(Bytes("mine")); Check(!state.Status.Dirty && File.ReadAllText(path) == "mine", "exchange success"); }
            else
            {
                var error = Reject(() => state.Save(Bytes("mine")), StateFileProblemCode.CommittedConflict);
                Check(error.DraftCommitted && !error.RequiresReload && File.ReadAllText(error.BackupPath!) == "old", "native exchange success followed by error preserves actual displaced temporary");
                Check(!state.Status.Dirty && !state.Status.LoadFailed, "confirmed committed warning settles draft");
            }
        }
        string changed = StatePath(root); File.WriteAllText(changed, "old");
        var changedState = new PortableStateFile(changed, 128, phase => { if (phase == "committed") { File.Move(changed, changed + ".own-draft"); File.WriteAllText(changed, "mine"); } });
        Load(changedState); var foreign = Reject(() => changedState.Save(Bytes("mine")), StateFileProblemCode.Conflict);
        Check(!foreign.DraftCommitted && foreign.RequiresReload && changedState.Status.Dirty, "same bytes with a foreign identity are not confirmed as our committed draft");
        Check(File.ReadAllText(changed) == "mine" && File.ReadAllText(changed + ".own-draft") == "mine" && File.ReadAllText(foreign.BackupPath!) == "old", "postcommit foreign replacement and displaced original are preserved");
        string late = StatePath(root); File.WriteAllText(late, "old");
        var lateState = new PortableStateFile(late, 128, phase => { if (phase == "checked") File.WriteAllText(late, "external-late"); }); Load(lateState);
        var lateError = Reject(() => lateState.Save(Bytes("mine")), StateFileProblemCode.CommittedConflict);
        Check(lateError.DraftCommitted && File.ReadAllText(lateError.BackupPath!) == "external-late", "late external modification is preserved in exact displaced backup");
        string absent = StatePath(root); var absentState = new PortableStateFile(absent, 128, phase => { if (phase == "checked") File.WriteAllText(absent, "late-created"); }); Load(absentState);
        var absentError = Reject(() => absentState.Save(Bytes("mine")), StateFileProblemCode.Conflict);
        Check(!absentError.DraftCommitted && absentError.RequiresReload && File.ReadAllText(absent) == "late-created", "late newly created target cannot be overwritten");
        Check(absentError.BackupPath is not null && File.ReadAllText(absentError.BackupPath) == "mine", "uncertain uncommitted draft is retained for inspection");
        string unsupported = StatePath(root); File.WriteAllText(unsupported, "old");
        var unsupportedState = new PortableStateFile(unsupported, 128, null, new ExchangeFixture((_, _) => throw new PlatformNotSupportedException("no atomic exchange"))); Load(unsupportedState);
        var unsupportedError = Reject(() => unsupportedState.Save(Bytes("mine")));
        Check(!unsupportedError.DraftCommitted && !unsupportedError.RequiresReload && File.ReadAllText(unsupported) == "old", "unsupported atomic operation has no unsafe overwrite fallback");
        Check(Directory.GetFiles(Path.GetDirectoryName(unsupported)!, "*.tmp").Length == 0, "known uncommitted staging is cleaned");
        string moved = StatePath(root); File.WriteAllText(moved, "old");
        var moveState = new PortableStateFile(moved, 128, null, new ExchangeFixture(move: (source, target) => { File.Move(source, target); throw new IOException("after backup move"); })); Load(moveState);
        var moveError = Reject(() => moveState.Save(Bytes("mine")), StateFileProblemCode.CommittedConflict);
        Check(moveError.DraftCommitted && moveError.BackupPath!.EndsWith(".bak") && File.ReadAllText(moveError.BackupPath) == "old", "backup move succeeded then failed reports existing backup path");
    }
    private static void Cleanup(string root)
    {
        string path = StatePath(root); File.WriteAllText(path, "old");
        var partial = new PortableStateFile(path, 128, null, writeBytes: (stream, bytes) => { stream.Write(bytes.AsSpan(0, 2)); throw new IOException("owned partial write failure"); }); Load(partial);
        var failure = Reject(() => partial.Save(Bytes("mine")));
        Check(File.ReadAllText(path) == "old" && Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").Length == 0, "partial staging is cleaned using its captured creation identity");
        Check(partial.Status.Dirty && failure.BackupPath is null, "partial write failure remains dirty without a nonexistent recovery path");
        if (OperatingSystem.IsWindows())
        {
            string blocked = StatePath(root); File.WriteAllText(blocked, "old"); FileStream? holder = null;
            var blockedState = new PortableStateFile(blocked, 128, null, writeBytes: (stream, bytes) =>
            {
                stream.Write(bytes.AsSpan(0, 2)); stream.Flush();
                holder = new FileStream(((FileStream)stream).Name, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                throw new IOException("owned partial staging held open");
            }); Load(blockedState);
            try
            {
                var held = Reject(() => blockedState.Save(Bytes("mine")));
                Check(held.BackupPath is not null && File.ReadAllText(held.BackupPath) == "mi", "failed partial cleanup reports real retained own path");
                Check(held.ToString().Contains(held.BackupPath!) && blockedState.Status.RecoveryPath == held.BackupPath, "retained partial draft appears in diagnostic and immutable status");
            }
            finally { holder?.Dispose(); }
            string cleanup = StatePath(root); File.WriteAllText(cleanup, "old"); FileStream? backupHolder = null;
            var cleanupState = new PortableStateFile(cleanup, 128, phase => { if (phase == "cleanup") backupHolder = new FileStream(Directory.GetFiles(Path.GetDirectoryName(cleanup)!, "*.bak").Single(), FileMode.Open, FileAccess.Read, FileShare.ReadWrite); }); Load(cleanupState);
            try
            {
                var warning = Reject(() => cleanupState.Save(Bytes("mine")), StateFileProblemCode.CommittedConflict);
                Check(warning.DraftCommitted && !cleanupState.Status.Dirty && File.ReadAllText(cleanup) == "mine", "backup cleanup failure does not falsely report unsaved draft");
                Check(warning.BackupPath is not null && File.ReadAllText(warning.BackupPath) == "old" && cleanupState.Status.RecoveryPath == warning.BackupPath, "backup cleanup failure retains actual previous-version path");
            }
            finally { backupHolder?.Dispose(); }
        }
    }
    private static void ResponsiveState(string root)
    {
        string path = StatePath(root); File.WriteAllText(path, "old"); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var state = new PortableStateFile(path, 128, phase => { if (phase == "prepared") { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new IOException("fixture timed out"); } }); Load(state);
        var writer = Task.Run(() => state.Save(Bytes("first")));
        try
        {
            Check(entered.Wait(TimeSpan.FromSeconds(5)), "writer reaches owned blocking checkpoint");
            var stopwatch = Stopwatch.StartNew(); var snapshot = state.Status; state.MarkDirty(); var newer = state.Status;
            Check(stopwatch.Elapsed < TimeSpan.FromMilliseconds(250) && snapshot.Dirty && newer.Dirty, "state read and new edit do not wait for IO gate");
            Check(!ReferenceEquals(snapshot, newer), "status snapshots are independent immutable values");
        }
        finally { release.Set(); }
        writer.GetAwaiter().GetResult();
        Check(state.Status.Dirty && File.ReadAllText(path) == "first", "late edit revision survives older save completion");
        state.Save(Bytes("newest")); Check(!state.Status.Dirty && File.ReadAllText(path) == "newest", "subsequent newest save settles dirty state");
        state.MarkDirty(); File.WriteAllText(path, "bad");
        string draft = state.Load<string>(_ => throw new InvalidDataException("bad schema"), () => "newer draft");
        Check(draft == "newer draft" && state.Status.Dirty && state.Status.CanRecover, "failed reload preserves dirty revision and fallback draft");
    }
}
