using System.Collections;
using System.Text;
using SideIsland.Core;

namespace SideIsland.Tests;

internal static class PortableWorkspaceTests
{
    private static int checks;
    public static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "side-island-workspace-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            Models(directory); Limits(directory); Persistence(directory); Recovery(directory); Conflicts(directory); ExchangeCommits(directory);
            Console.WriteLine("PASS: " + checks + " portable workspace checks; immutable ordered catalogs, model limits, strict bounded JSON, owned-file recovery, optimistic conflicts and exact displaced-version backups. No resources were launched.");
        }
        finally { Directory.Delete(directory, true); }
    }
    private static string Id() => Guid.NewGuid().ToString("N");
    private static PortableWorkspaceResource Resource(string directory, string name = "程序 App") => new(Id(), name, "program", Path.Combine(directory, "app with spaces "), ["--name", "two words", "\"quoted\"", "a\nb", "中文😀", ""]);
    private static PortableWorkspaceSnapshot Catalog(string directory, string name = "程序 App") => new([Resource(directory, name)], []);
    private static void Models(string directory)
    {
        var empty = PortableWorkspaceStore.Empty(); var first = Resource(directory, " first "); var second = Resource(directory, "second");
        var value = PortableWorkspaceStore.UpsertResource(PortableWorkspaceStore.UpsertResource(empty, first), second);
        var scene = new PortableWorkspaceScene(Id(), "Coding 写代码", [second.Id, first.Id], "codex-week", "focus");
        value = PortableWorkspaceStore.UpsertScene(value, scene);
        var renamed = PortableWorkspaceStore.UpsertResource(value, first with { Name = "renamed" });
        Check(renamed.Resources.Select(item => item.Id).SequenceEqual(new[] { first.Id, second.Id }), "upsert retains resource position");
        Check(value.Resources[0].Name == " first " && value.Resources[0].Target.EndsWith(' '), "input names and path whitespace are never silently trimmed");
        var removed = PortableWorkspaceStore.RemoveResource(value, second.Id);
        Check(removed.Scenes.Single().ResourceIds.SequenceEqual(new[] { first.Id }) && value.Resources.Count == 2, "delete removes scene references without mutating previous snapshot");
        Check(PortableWorkspaceStore.RemoveScene(value, scene.Id).Scenes.Count == 0, "delete scene retains catalog");
        string[] arguments = ["original"], ids = [first.Id];
        var frozen = PortableWorkspaceStore.Freeze(new([first with { Arguments = arguments }], [scene with { ResourceIds = ids }]));
        arguments[0] = "changed"; ids[0] = second.Id;
        Check(frozen.Resources.Single().Arguments.Single() == "original" && frozen.Scenes.Single().ResourceIds.Single() == first.Id, "snapshot deep copies caller collections");
        foreach (object collection in new object[] { frozen.Resources, frozen.Scenes, frozen.Resources[0].Arguments, frozen.Scenes[0].ResourceIds })
        {
            Check(collection is IList list && list.IsReadOnly, "snapshot collection is genuinely read-only");
            try { ((IList)collection).Clear(); throw new Exception("Mutable snapshot"); } catch (NotSupportedException) { checks++; }
        }
        PortableWorkspaceStore.ValidateResource(first); Check(true, "arguments preserve spaces, quotes, newline, Unicode and empty values");
        Reject(() => PortableWorkspaceStore.ValidateResource(first with { Id = "bad" }), WorkspaceProblemCode.InvalidId);
        Reject(() => PortableWorkspaceStore.ValidateResource(first with { Name = "bad\nname" }), WorkspaceProblemCode.InvalidName);
        Reject(() => PortableWorkspaceStore.ValidateResource(first with { Name = "\ud800" }), WorkspaceProblemCode.InvalidName);
        Reject(() => PortableWorkspaceStore.ValidateResource(first with { Name = " " }), WorkspaceProblemCode.InvalidName);
        Reject(() => PortableWorkspaceStore.ValidateResource(first with { Kind = "shell" }), WorkspaceProblemCode.InvalidKind);
        Reject(() => PortableWorkspaceStore.ValidateResource(first with { Target = "relative" }), WorkspaceProblemCode.InvalidTarget);
        Reject(() => PortableWorkspaceStore.ValidateResource(first with { Kind = "url", Target = "file:///tmp/file", Arguments = [] }), WorkspaceProblemCode.InvalidTarget);
        Reject(() => PortableWorkspaceStore.ValidateResource(first with { Kind = "url", Target = "https://example.invalid", Arguments = ["argument"] }), WorkspaceProblemCode.InvalidArguments);
        Reject(() => PortableWorkspaceStore.ValidateResource(first with { Arguments = ["nul\0arg"] }), WorkspaceProblemCode.InvalidArguments);
        Reject(() => PortableWorkspaceStore.ValidateResource(first with { Arguments = ["bad\udc00"] }), WorkspaceProblemCode.InvalidArguments);
        Reject(() => PortableWorkspaceStore.ValidateScene(scene with { Slot1 = "not-a-widget" }, value.Resources.Select(item => item.Id)), WorkspaceProblemCode.InvalidLayout);
        Reject(() => PortableWorkspaceStore.ValidateScene(scene with { ResourceIds = [first.Id, first.Id] }, [first.Id]), WorkspaceProblemCode.DuplicateResource);
        Reject(() => PortableWorkspaceStore.ValidateScene(scene with { ResourceIds = [second.Id] }, [first.Id]), WorkspaceProblemCode.MissingResource);
        Reject(() => PortableWorkspaceStore.Validate(new([first, first], [])), WorkspaceProblemCode.DuplicateId);
        PortableWorkspaceStore.ValidateResource(first with { Kind = "url", Target = "https://example.invalid/path?q=中文", Arguments = [] }); checks++;
        PortableWorkspaceStore.ValidateScene(scene with { Slot1 = null, Slot2 = null }, value.Resources.Select(item => item.Id)); checks++;
    }
    private static void Limits(string directory)
    {
        var item = Resource(directory);
        PortableWorkspaceStore.ValidateResource(item with { Name = new string('n', 60), Arguments = Enumerable.Repeat(new string('a', 1024), 32).ToArray() }); checks++;
        Reject(() => PortableWorkspaceStore.ValidateResource(item with { Name = new string('n', 61) }), WorkspaceProblemCode.InvalidName);
        Reject(() => PortableWorkspaceStore.ValidateResource(item with { Arguments = [new string('a', 1025)] }), WorkspaceProblemCode.InvalidArguments);
        Reject(() => PortableWorkspaceStore.ValidateResource(item with { Arguments = Enumerable.Repeat("", 33).ToArray() }), WorkspaceProblemCode.InvalidArguments);
        Reject(() => PortableWorkspaceStore.ValidateResource(item with { Target = Path.GetPathRoot(directory)! + new string('a', 4096) }), WorkspaceProblemCode.InvalidTarget);
        var resources = Enumerable.Range(0, 60).Select(index => Resource(directory, "r" + index)).ToArray();
        var scenes = Enumerable.Range(0, 20).Select(index => new PortableWorkspaceScene(Id(), "s" + index, resources.Take(30).Select(resource => resource.Id).ToArray())).ToArray();
        PortableWorkspaceStore.Validate(new(resources, scenes)); checks++;
        Reject(() => PortableWorkspaceStore.Validate(new(resources.Append(item).ToArray(), scenes)), WorkspaceProblemCode.TooManyResources);
        Reject(() => PortableWorkspaceStore.Validate(new(resources, scenes.Append(new(Id(), "extra", [])).ToArray())), WorkspaceProblemCode.TooManyScenes);
        Reject(() => PortableWorkspaceStore.ValidateScene(scenes[0] with { ResourceIds = resources.Take(31).Select(resource => resource.Id).ToArray() }, resources.Select(resource => resource.Id)), WorkspaceProblemCode.TooManySceneResources);
    }
    private static void Persistence(string directory)
    {
        string path = Path.Combine(directory, "not-created", "workspaces.json"); var store = new PortableWorkspaceStore(path);
        Check(store.Load().Resources.Count == 0 && !File.Exists(path) && !Directory.Exists(Path.GetDirectoryName(path)), "missing startup never saves empty defaults or creates directories");
        var value = Catalog(directory); var scene = new PortableWorkspaceScene(Id(), "工作", [value.Resources[0].Id], "music", "clock"); value = value with { Scenes = [scene] };
        store.Save(value); byte[] first = File.ReadAllBytes(path);
        Check(first.Length > 0 && !first.Take(3).SequenceEqual(new byte[] { 239, 187, 191 }), "explicit save is UTF8 without BOM");
        var reopened = new PortableWorkspaceStore(path); var restored = reopened.Load();
        Check(!reopened.LoadFailed && restored.Resources[0].Arguments.SequenceEqual(value.Resources[0].Arguments) && restored.Scenes[0].ResourceIds.Single() == value.Resources[0].Id, "strict JSON roundtrips ordered resources/arguments/scene references");
        var changed = PortableWorkspaceStore.UpsertResource(restored, restored.Resources[0] with { Name = "changed" }); reopened.Save(changed);
        Check(reopened.Snapshot.Resources[0].Name == "changed" && reopened.LastRecoveryPath is not null && File.ReadAllBytes(reopened.LastRecoveryPath).SequenceEqual(first), "atomic update retains exact previous bytes and publishes committed snapshot");
        var giant = new PortableWorkspaceSnapshot(Enumerable.Range(0, 60).Select(index => Resource(directory) with { Arguments = Enumerable.Repeat(new string('中', 1024), 32).ToArray() }).ToArray(), []);
        byte[] before = File.ReadAllBytes(path); Reject(() => reopened.Save(giant), WorkspaceProblemCode.FileTooLarge);
        Check(File.ReadAllBytes(path).SequenceEqual(before) && reopened.Snapshot.Resources[0].Name == "changed", "encoded size rejection leaves file and in-memory catalog intact");
        Check(!Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp").Any() && !File.Exists(path + ".lock"), "save leaves no staging or lock files");
    }
    private static void Recovery(string directory)
    {
        string path = Path.Combine(directory, "corrupt.json");
        foreach (byte[] bad in new[] { Encoding.UTF8.GetBytes("{"), new byte[] { 0xff, 0xfe, 0 }, Encoding.UTF8.GetBytes("{\"Version\":1,\"Version\":1,\"Resources\":[],\"Scenes\":[]}"), Encoding.UTF8.GetBytes("{\"Version\":1,\"Resources\":[],\"Scenes\":[],\"unknown\":true}") })
        {
            File.WriteAllBytes(path, bad); var store = new PortableWorkspaceStore(path); store.Load();
            Check(store.LoadFailed && store.Problem?.Code == WorkspaceProblemCode.InvalidData, "invalid encoding/JSON/schema is a visible load failure");
            Reject(() => store.Save(PortableWorkspaceStore.Empty()), WorkspaceProblemCode.LoadFailed);
            Check(File.ReadAllBytes(path).SequenceEqual(bad), "failed load is never silently overwritten");
            store.RecoverEmpty();
            Check(!store.LoadFailed && store.Snapshot.Resources.Count == 0 && File.ReadAllBytes(store.LastRecoveryPath!).SequenceEqual(bad), "explicit recovery backs up exact unreadable bytes before rebuilding");
            Check(!new PortableWorkspaceStore(path).Load().Resources.Any(), "recovered document can be loaded");
        }
        File.WriteAllBytes(path, new byte[PortableWorkspaceStore.MaximumFileBytes + 1]); var oversized = new PortableWorkspaceStore(path); oversized.Load();
        Check(oversized.LoadFailed && oversized.Problem?.Code == WorkspaceProblemCode.FileTooLarge, "load cap enforced before unbounded allocation");
        Reject(() => oversized.RecoverEmpty(), WorkspaceProblemCode.LoadFailed);
        Check(new FileInfo(path).Length == PortableWorkspaceStore.MaximumFileBytes + 1, "unverified oversized source is preserved for manual recovery");
        File.WriteAllText(path, "{"); bool fail = true;
        var retry = new PortableWorkspaceStore(path, stage => { if (stage == "prepared" && fail) throw new IOException("owned recovery fixture failure"); }); retry.Load();
        Reject(() => retry.RecoverEmpty(), WorkspaceProblemCode.Io); Check(retry.LoadFailed && File.ReadAllText(path) == "{", "precommit recovery failure retains failure state and original file");
        fail = false; retry.RecoverEmpty(); Check(File.ReadAllText(retry.LastRecoveryPath!) == "{", "recovery retry still makes the required original backup");
    }
    private static void Conflicts(string directory)
    {
        string path = Path.Combine(directory, "conflicts.json"); var writer = new PortableWorkspaceStore(path); writer.Load(); writer.Save(Catalog(directory, "original"));
        var stale = new PortableWorkspaceStore(path); var loaded = stale.Load(); writer.Save(Catalog(directory, "external")); byte[] external = File.ReadAllBytes(path);
        Reject(() => stale.Save(loaded), WorkspaceProblemCode.Conflict);
        Check(File.ReadAllBytes(path).SequenceEqual(external) && stale.Snapshot.Resources[0].Name == "original", "external preflight edits are rejected without changing file or catalog");
        stale.Load(); File.Delete(path); Reject(() => stale.Save(loaded), WorkspaceProblemCode.Conflict); Check(!File.Exists(path), "external deletion is not silently recreated");
        stale.Load(); File.WriteAllText(path, "external creation"); Reject(() => stale.Save(loaded), WorkspaceProblemCode.Conflict); Check(File.ReadAllText(path) == "external creation", "external creation after empty baseline is preserved");
        writer.Load(); writer.RecoverEmpty(); writer.Save(Catalog(directory, "baseline"));
        var racing = new PortableWorkspaceStore(path, stage => { if (stage == "prepared") File.WriteAllText(path, "prepared external"); }); racing.Load();
        Reject(() => racing.Save(Catalog(directory, "draft")), WorkspaceProblemCode.Conflict); Check(File.ReadAllText(path) == "prepared external", "final precommit check rejects edits during staging");
        writer.Load(); writer.RecoverEmpty(); writer.Save(Catalog(directory, "baseline"));
        var atCommit = new PortableWorkspaceStore(path, stage => { if (stage == "checked") File.WriteAllText(path, "late external version"); }); atCommit.Load();
        var committed = Reject(() => atCommit.Save(Catalog(directory, "committed draft")), WorkspaceProblemCode.CommittedConflict);
        Check(committed.DraftCommitted && !committed.RequiresReload && committed.BackupPath is not null && File.ReadAllText(committed.BackupPath) == "late external version", "atomic Replace captures the exact late external version and reports committed draft truthfully");
        Check(atCommit.Snapshot.Resources[0].Name == "committed draft" && new PortableWorkspaceStore(path).Load().Resources[0].Name == "committed draft", "committed-conflict catalog matches the confirmed on-disk draft");
        var later = new PortableWorkspaceStore(path, stage => { if (stage == "committed") File.WriteAllText(path, "even later external version"); }); later.Load();
        var ambiguous = Reject(() => later.Save(Catalog(directory, "not current")), WorkspaceProblemCode.Conflict);
        Check(ambiguous.RequiresReload && !ambiguous.DraftCommitted && later.NeedsReload && File.ReadAllText(path) == "even later external version", "postcommit external edit is never rolled back and requires reload");
        Reject(() => later.Save(Catalog(directory)), WorkspaceProblemCode.Conflict);
        writer.Load(); writer.RecoverEmpty();
        using (var locked = new FileStream(path + ".lock", FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var busy = new PortableWorkspaceStore(path); busy.Load(); Reject(() => busy.Save(Catalog(directory)), WorkspaceProblemCode.Busy);
            Check(File.Exists(path + ".lock"), "another writer's lock is never removed");
        }
        File.Delete(path + ".lock");
        Check(!Directory.EnumerateFiles(directory, "*.tmp", SearchOption.AllDirectories).Any(), "all conflict paths clean their own staging files");
    }
    private static void ExchangeCommits(string directory)
    {
        string root = Path.Combine(directory, "exchange"); Directory.CreateDirectory(root);
        string Seed(string name)
        {
            string path = Path.Combine(root, name + ".json"); var store = new PortableWorkspaceStore(path); store.Load(); store.Save(Catalog(directory, "original")); return path;
        }
        string path = Seed("normal"); byte[] original = File.ReadAllBytes(path); var adapter = new ExchangeFixture();
        var normal = new PortableWorkspaceStore(path, null, adapter); normal.Load(); normal.Save(Catalog(directory, "exchanged"));
        Check(adapter.Exchanges == 1 && adapter.Moves == 1 && File.ReadAllBytes(normal.LastRecoveryPath!).SequenceEqual(original), "exchange commit keeps the exact old entry and names its backup without copying");

        path = Seed("unsupported"); original = File.ReadAllBytes(path);
        var unsupported = new ExchangeFixture { BeforeExchange = (_, _) => throw new PlatformNotSupportedException("owned ENOTSUP fixture") };
        var denied = new PortableWorkspaceStore(path, null, unsupported); denied.Load(); Reject(() => denied.Save(Catalog(directory, "must not write")), WorkspaceProblemCode.Io);
        Check(unsupported.Exchanges == 1 && unsupported.Moves == 0 && File.ReadAllBytes(path).SequenceEqual(original) && !denied.NeedsReload, "unsupported exchange has no non-atomic fallback and confirmed unchanged state remains usable");
        Check(!Directory.EnumerateFiles(root, "*.tmp").Any(), "known unchanged staging file is cleaned after unsupported exchange");

        path = Seed("failed-with-external-edit");
        var failedAdapter = new ExchangeFixture { BeforeExchange = (_, target) => { File.WriteAllText(target, "external edit before rejected exchange"); throw new IOException("owned operation rejected"); } };
        var failed = new PortableWorkspaceStore(path, null, failedAdapter); failed.Load(); var failedError = Reject(() => failed.Save(Catalog(directory)), WorkspaceProblemCode.Conflict);
        Check(failedError.RequiresReload && failedError.BackupPath is null && File.ReadAllText(path) == "external edit before rejected exchange", "uncommitted exchange failure never advertises its disposable new draft as a recovery copy");

        path = Seed("backup-collision"); original = File.ReadAllBytes(path); string? occupiedBackup = null;
        var collisionAdapter = new ExchangeFixture { BeforeMove = (_, target) => { occupiedBackup = target; File.WriteAllText(target, "owned collision"); } };
        var collision = new PortableWorkspaceStore(path, null, collisionAdapter); collision.Load();
        var collisionError = Reject(() => collision.Save(Catalog(directory, "collision draft")), WorkspaceProblemCode.CommittedConflict);
        Check(collisionError.DraftCommitted && collisionError.BackupPath is not null && collisionError.BackupPath.EndsWith(".tmp", StringComparison.Ordinal) && File.ReadAllBytes(collisionError.BackupPath).SequenceEqual(original), "backup rename failure keeps the exchanged old file at its real staging path");
        Check(File.ReadAllText(occupiedBackup!) == "owned collision" && new PortableWorkspaceStore(path).Load().Resources[0].Name == "collision draft", "backup collision never overwrites an existing name and draft status is truthful");

        path = Seed("exchange-errno"); original = File.ReadAllBytes(path);
        var uncertainAdapter = new ExchangeFixture { AfterExchange = (_, _) => throw new IOException("owned exchange succeeded before errno fixture") };
        var uncertain = new PortableWorkspaceStore(path, null, uncertainAdapter); uncertain.Load();
        var uncertainError = Reject(() => uncertain.Save(Catalog(directory, "confirmed after errno")), WorkspaceProblemCode.CommittedConflict);
        Check(uncertainError.DraftCommitted && File.ReadAllBytes(uncertainError.BackupPath!).SequenceEqual(original) && uncertainAdapter.Moves == 0, "exchange succeeded then errored: original retained before syscall return and draft confirmed by identity");

        path = Seed("rename-errno"); original = File.ReadAllBytes(path);
        var moveAdapter = new ExchangeFixture { AfterMove = (_, _) => throw new IOException("owned backup rename succeeded before errno fixture") };
        var move = new PortableWorkspaceStore(path, null, moveAdapter); move.Load();
        var moveError = Reject(() => move.Save(Catalog(directory, "backup moved")), WorkspaceProblemCode.CommittedConflict);
        Check(moveError.DraftCommitted && moveError.BackupPath!.EndsWith(".bak", StringComparison.Ordinal) && File.ReadAllBytes(moveError.BackupPath).SequenceEqual(original), "rename succeeded then errored: recovery path is located by the displaced identity");

        path = Seed("later-edit"); original = File.ReadAllBytes(path);
        var laterAdapter = new ExchangeFixture { AfterExchange = (_, target) => { File.WriteAllText(target, "later external content"); throw new IOException("owned late modification"); } };
        var later = new PortableWorkspaceStore(path, null, laterAdapter); later.Load();
        var laterError = Reject(() => later.Save(Catalog(directory, "draft overwritten externally")), WorkspaceProblemCode.Conflict);
        Check(laterError.RequiresReload && !laterError.DraftCommitted && File.ReadAllBytes(laterError.BackupPath!).SequenceEqual(original) && File.ReadAllText(path) == "later external content", "uncertain exchange plus later edit preserves recovery and external content without rollback");

        path = Path.Combine(root, "first-create.json");
        var firstAdapter = new ExchangeFixture { BeforeMove = (_, target) => File.WriteAllText(target, "external appeared before first create") };
        var first = new PortableWorkspaceStore(path, null, firstAdapter); first.Load(); var firstError = Reject(() => first.Save(Catalog(directory)), WorkspaceProblemCode.Conflict);
        Check(!firstError.DraftCommitted && File.ReadAllText(path) == "external appeared before first create" && firstAdapter.Exchanges == 0, "native no-replace creation retains a destination that appeared after the final check");

        path = Seed("late-before-exchange");
        var lateAdapter = new ExchangeFixture { BeforeExchange = (_, target) => File.WriteAllText(target, "external immediately before exchange") };
        var last = new PortableWorkspaceStore(path, null, lateAdapter); last.Load(); var lastError = Reject(() => last.Save(Catalog(directory, "latest draft")), WorkspaceProblemCode.CommittedConflict);
        Check(lastError.DraftCommitted && File.ReadAllText(lastError.BackupPath!) == "external immediately before exchange", "the atomic exchange captures the exact version present at the swap boundary");
        Console.WriteLine("WORKSPACE EXCHANGE: owned filesystem contract fixtures cover unsupported operations, backup collision, success-before-error, concurrent creation and later edits; Windows emulation is not Mac/Linux native execution.");
    }
    private sealed class ExchangeFixture : IWorkspaceFileCommit
    {
        internal Action<string, string>? BeforeExchange, AfterExchange, BeforeMove, AfterMove;
        internal int Exchanges, Moves;
        public bool UsesExchange => true;
        public void Exchange(string source, string target)
        {
            Exchanges++; BeforeExchange?.Invoke(source, target);
            if (OperatingSystem.IsWindows())
            {
                // Owned-file contract fixture only: exercise the controller's
                // post-exchange state on Windows without claiming foreign native IO.
                string held = source + ".held"; File.Replace(source, target, held); File.Move(held, source, false);
            }
            else PortableBatchFiles.ExchangeFiles(source, target);
            AfterExchange?.Invoke(source, target);
        }
        public void MoveNew(string source, string target)
        {
            Moves++; BeforeMove?.Invoke(source, target); PortableBatchFiles.MoveFileNoReplace(source, target); AfterMove?.Invoke(source, target);
        }
    }
    private static WorkspaceProblem Reject(Action action, WorkspaceProblemCode code)
    {
        try { action(); }
        catch (WorkspaceProblem error) { Check(error.Code == code, "expected " + code + ", got " + error.Code + ": " + error.Message); return error; }
        throw new Exception("Portable workspace accepted operation expected to fail: " + code);
    }
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception("Portable workspace: " + message); }
}
