using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SideIsland.Core;

namespace SideIsland.Tests;

internal static class NotificationPersistenceTests
{
    private static int checks;
    public static void Run()
    {
        checks = 0; string directory = Path.Combine(Path.GetTempPath(), "side-island-notification-persistence-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try { Conflicts(directory); Recovery(directory); PrivacyIntent(directory); ReloadDraft(directory); BackupReload(directory); CommitWarnings(directory); StatusDoesNotBlock(directory); Bounds(directory); Links(directory); DraftPublicationFeedback(directory); DraftFailureBelongsToReload(directory); }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine("PASS: " + checks + " notification persistence checks; exact external/recovery bytes, explicit recovery, pending privacy changes, reload draft retention, committed-warning/unknown states, bounded UTF8 and nonblocking status. Owned files only.");
    }
    private static void Conflicts(string directory)
    {
        string path = Path.Combine(directory,"conflicts.json"); var first = Seed(path); var external = new NotificationCenter(path); external.SetApplicationEnabled("fixture", false); byte[] bytes = File.ReadAllBytes(path);
        first.SetApplicationPriority("fixture",2);
        Check(Same(path,bytes) && first.PersistenceStatus.ProblemCode == StateFileProblemCode.Conflict && first.PersistenceStatus.Dirty, "external disabled policy is preserved and conflict is observable");
        Check(first.Applications.Single().Enabled && first.Applications.Single().Priority == 2, "failed save retains requested in-memory rules");
        Check(!first.RetrySave() && Same(path,bytes), "retry cannot bypass an unresolved external conflict");
        Check(first.Reload() && !first.PersistenceStatus.Dirty && !first.Applications.Single().Enabled, "explicit reload adopts saved external rules and clears pending state");
        first.SetApplicationEnabled("fixture",true); Check(new NotificationCenter(path).Applications.Single().Enabled, "subsequent save after reload succeeds");
        File.Delete(path); first.SetApplicationPriority("fixture",1); Check(!File.Exists(path) && first.PersistenceStatus.ProblemCode == StateFileProblemCode.Conflict, "external deletion is not silently recreated");
        Check(first.Reload() && first.Applications.Count == 0, "reload adopts intentionally missing state");
        var other = Seed(path); bytes=File.ReadAllBytes(path); first.Publish(Input("new", "owned")); Check(Same(path,bytes) && first.PersistenceStatus.Dirty, "external creation after missing baseline is preserved");
    }
    private static void Recovery(string directory)
    {
        foreach(var pair in new[] { ("json",Encoding.UTF8.GetBytes("{owned bad")), ("utf8",new byte[]{0xc3,0x28}), ("null",Encoding.UTF8.GetBytes("null")) })
        {
            string path=Path.Combine(directory,"broken-"+pair.Item1+".json"); File.WriteAllBytes(path,pair.Item2); var center=new NotificationCenter(path);
            Check(center.PersistenceStatus.LoadFailed && center.PersistenceStatus.CanRecover, "malformed readable state has exact recoverable baseline: "+pair.Item1);
            center.Save(); Check(Same(path,pair.Item2) && Directory.GetFiles(directory,Path.GetFileName(path)+".unreadable-*.bak").Length==0, "ordinary close/save leaves unreadable original untouched: "+pair.Item1);
            center.Publish(Input("fixture","kept in memory")); Check(center.Items.Count==1 && Same(path,pair.Item2), "new notifications can remain in memory without implicitly recovering disk");
            Check(center.RecoverAndSave() && !center.PersistenceStatus.LoadFailed && !center.PersistenceStatus.Dirty, "explicit recovery commits current rules");
            Check(center.PersistenceStatus.RecoveryPath is string backup && Same(backup,pair.Item2), "recovery retains exact displaced malformed bytes");
            Check(new NotificationCenter(path).Applications.Count==1 && new NotificationCenter(path).Items.Count==0, "recovery preserves default body privacy");
        }
        string lockedPath=Path.Combine(directory,"recovery-lock.json"); byte[] original=Encoding.UTF8.GetBytes("broken A"); File.WriteAllBytes(lockedPath,original); var lockedCenter=new NotificationCenter(lockedPath);
        using(var locked=new FileStream(lockedPath,FileMode.Open,FileAccess.Read,FileShare.Read)) Check(!lockedCenter.RecoverAndSave() && Same(lockedPath,original), "failed replacement leaves original and reports failure");
        byte[] newer=Encoding.UTF8.GetBytes("external broken B"); File.WriteAllBytes(lockedPath,newer); Check(!lockedCenter.RecoverAndSave() && Same(lockedPath,newer), "failed recovery cannot later overwrite a new external original without reload");
        Check(!lockedCenter.Reload() && lockedCenter.PersistenceStatus.CanRecover, "reload records new malformed baseline for explicit recovery");
        Check(lockedCenter.RecoverAndSave() && lockedCenter.PersistenceStatus.RecoveryPath is string latest && Same(latest,newer), "subsequent explicit recovery backs up newest original");
        string readPath=Path.Combine(directory,"read-lock.json"); Seed(readPath); NotificationCenter unreadable;
        using(var locked=new FileStream(readPath,FileMode.Open,FileAccess.ReadWrite,FileShare.None)) unreadable=new NotificationCenter(readPath);
        Check(unreadable.PersistenceStatus.LoadFailed && !unreadable.PersistenceStatus.CanRecover, "failed read without exact baseline cannot recover by guessing");
        byte[] valid=File.ReadAllBytes(readPath); Check(!unreadable.RecoverAndSave() && Same(readPath,valid), "unlocked valid original cannot be replaced from a transient read fallback");
        Check(unreadable.Reload() && !unreadable.PersistenceStatus.LoadFailed && unreadable.Applications.Count==1, "successful reload clears transient failure state");
    }
    private static void PrivacyIntent(string directory)
    {
        string path=Path.Combine(directory,"privacy.json"); var center=Seed(path); const string body="OWNED_BODY_PENDING_PRIVACY"; center.Publish(Input("fixture",body));
        using(var locked=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) center.SetPersistHistory(true);
        Check(!center.PersistHistory && center.PendingPersistHistory==true && center.PersistenceStatus.Dirty, "failed enable retains retry intent without activating body persistence");
        Check(!File.ReadAllText(path).Contains(body,StringComparison.Ordinal), "failed enable does not put bodies on disk");
        if(center.PersistenceStatus.NeedsReload) throw new Exception("A confirmed precommit lock failure should allow save retry without losing privacy intent.");
        Check(center.RetrySave() && center.PersistHistory && center.PendingPersistHistory is null && File.ReadAllText(path).Contains(body,StringComparison.Ordinal), "explicit retry applies pending enable after access returns");
        using(var locked=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) center.SetPersistHistory(false);
        Check(!center.PersistHistory && center.PendingPersistHistory==false && center.PersistenceStatus.Dirty && File.ReadAllText(path).Contains(body,StringComparison.Ordinal), "failed disable stops recording but accurately retains disk-removal intent");
        center.Publish(Input("fixture","NEW_BODY_MUST_NOT_BE_PERSISTED")); Check(!File.ReadAllText(path).Contains("NEW_BODY_MUST_NOT_BE_PERSISTED",StringComparison.Ordinal), "new messages do not continue automatic body writes after failed disable");
        Check(center.RetrySave() && center.PendingPersistHistory is null && !new NotificationCenter(path).PersistHistory && !File.ReadAllText(path).Contains(body,StringComparison.Ordinal), "retry completes pending history removal");
    }
    private static void ReloadDraft(string directory)
    {
        string path=Path.Combine(directory,"reload.json"); var center=Seed(path); center.SetPersistHistory(true); var before=center.Items.Single();
        using(var locked=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) center.SetApplicationPriority("fixture",2);
        File.WriteAllText(path,"{bad while editing"); Check(!center.Reload(), "failed explicit reload reports false");
        Check(center.Items.Single().Id==before.Id && center.Items.Single().Priority==2 && center.Applications.Single().Priority==2 && center.PersistenceStatus.Dirty, "failed reload retains complete dirty model, not partially decoded collections");
        Check(center.RecoverAndSave() && new NotificationCenter(path).Items.Single().Priority==2, "explicit recovery writes preserved dirty draft");
        center.SetPersistHistory(false); var external=Seed(Path.Combine(directory,"external-reload.json")); external.SetApplicationPriority("fixture",1); byte[] disk=File.ReadAllBytes(Path.Combine(directory,"external-reload.json")); File.WriteAllBytes(path,disk);
        Check(center.Reload() && center.Items.Count==0 && center.Applications.Single().Priority==1 && center.PendingPersistHistory is null, "successful reload atomically adopts disk and clears stale history/native state");
    }
    private static void CommitWarnings(string directory)
    {
        foreach(string stage in new[]{"checked","committed"})
        {
            string path=Path.Combine(directory,"race-"+stage+".json"); Seed(path); string externalPath=Path.Combine(directory,"race-external-"+stage+".json"); var other=Seed(externalPath); other.SetApplicationEnabled("fixture",false); byte[] external=File.ReadAllBytes(externalPath);
            bool armed=false; var file=new PortableStateFile(path,1024*1024,point=>{if(armed && point==stage){armed=false; File.WriteAllBytes(path,external);}});
            var center=new NotificationCenter(path,null,file); armed=true; center.SetPersistHistory(true);
            if(stage=="checked")
            {
                Check(center.PersistHistory && center.PendingPersistHistory is null && !center.PersistenceStatus.Dirty && center.LastError is not null, "confirmed committed warning cannot roll privacy setting back to old value");
                Check(new NotificationCenter(path).PersistHistory && center.PersistenceStatus.RecoveryPath is string backup && Same(backup,external), "confirmed commit keeps current model and exact external backup");
            }
            else
            {
                Check(!center.PersistHistory && center.PendingPersistHistory==true && center.PersistenceStatus.NeedsReload && Same(path,external), "unconfirmed postcommit change retains pending intent and requires reload without clobbering newer bytes");
                Check(!center.RetrySave() && Same(path,external), "unconfirmed save cannot be retried over a newer version");
                Check(center.Reload() && !center.Applications.Single().Enabled && center.PendingPersistHistory is null, "explicit reload reconciles unconfirmed result");
            }
        }
    }
    private static void BackupReload(string directory)
    {
        string optInPath=Path.Combine(directory,"backup-pending-opt-in.json"); var optIn=Seed(optInPath); const string neverPersisted="NEVER_PERSISTED_ENABLE_FAILED_MARKER_aa8042"; optIn.Publish(Input("fixture",neverPersisted));
        using(var locked=new FileStream(optInPath,FileMode.Open,FileAccess.Read,FileShare.Read)) optIn.SetPersistHistory(true);
        Check(!optIn.PersistHistory && optIn.PendingPersistHistory==true && optIn.PersistenceStatus.Dirty, "backup fixture retains failed enable without activating history");
        Check(optIn.Reload(true) && optIn.LastDraftBackupPath is not null && !File.ReadAllText(optInPath).Contains(neverPersisted,StringComparison.Ordinal) && Directory.GetFiles(directory,"backup-pending-opt-in.json.draft-*.json").All(file=>!File.ReadAllText(file).Contains(neverPersisted,StringComparison.Ordinal)), "failed enable followed by backup reload never writes memory-only body/title marker to any draft");
        string path=Path.Combine(directory,"backup-reload.json"); var center=Seed(path); center.SetPersistHistory(true);
        var external=new NotificationCenter(path); external.SetApplicationPriority("fixture",1); byte[] original=File.ReadAllBytes(path);
        center.SetApplicationPriority("fixture",2); Check(center.PersistenceStatus.Dirty, "backup reload fixture has unsaved rule changes");
        Check(center.Reload(true) && center.Applications.Single().Priority==1 && !center.PersistenceStatus.Dirty && Same(path,original), "backup reload adopts external version without writing active state");
        string? backup=center.LastDraftBackupPath; Check(backup is not null && new NotificationCenter(backup).Applications.Single().Priority==2 && new NotificationCenter(backup).Items.Count==1, "atomic draft snapshot preserves pending rules and opted-in messages");
        center.SetPersistHistory(false); external=new NotificationCenter(path); external.SetApplicationPriority("fixture",0); center.SetApplicationPriority("fixture",2); center.Publish(Input("fixture","MEMORY_ONLY_BODY_MUST_NOT_BACK_UP"));
        Check(center.Reload(true) && center.LastDraftBackupPath!=backup && !File.ReadAllText(center.LastDraftBackupPath!).Contains("MEMORY_ONLY_BODY_MUST_NOT_BACK_UP",StringComparison.Ordinal), "backup respects disabled history and creates a distinct file");
        center.SetApplicationPriority("fixture",2); File.WriteAllText(path,"bad during explicit reload");
        Check(!center.RetrySave() && !center.Reload(true) && center.LastDraftBackupPath is string retained && File.Exists(retained) && center.Applications.Single().Priority==2 && center.PersistenceStatus.Dirty, "failed read after draft backup retains draft path, live model and dirty state");

        string blockedDirectory=Path.Combine(directory,"backup-unavailable"), moved=blockedDirectory+"-moved"; Directory.CreateDirectory(blockedDirectory); string blockedPath=Path.Combine(blockedDirectory,"state.json"); var blocked=Seed(blockedPath);
        var changed=new NotificationCenter(blockedPath); changed.SetApplicationPriority("fixture",1); blocked.SetApplicationPriority("fixture",2);
        Directory.Move(blockedDirectory,moved); File.WriteAllText(blockedDirectory,"owned blocking parent file");
        try { Check(!blocked.Reload(true) && blocked.Applications.Single().Priority==2 && blocked.PersistenceStatus.Dirty && blocked.LastDraftBackupPath is null, "backup failure does not load/replace current unsaved edit"); }
        finally { File.Delete(blockedDirectory); Directory.Move(moved,blockedDirectory); }
    }
    private static void StatusDoesNotBlock(string directory)
    {
        string path=Path.Combine(directory,"status.json"); Seed(path); bool armed=false; using var entered=new ManualResetEventSlim(); using var release=new ManualResetEventSlim();
        var file=new PortableStateFile(path,1024*1024,point=>{if(armed && point=="prepared"){entered.Set(); if(!release.Wait(5000)) throw new IOException("owned checkpoint timeout");}});
        var center=new NotificationCenter(path,null,file); armed=true; Task save=Task.Run(()=>center.SetApplicationPriority("fixture",1));
        try
        {
            Check(entered.Wait(3000), "actual notification save reaches owned pending I/O checkpoint"); var timer=Stopwatch.StartNew(); var state=center.PersistenceStatus; var pending=center.PendingPersistHistory; timer.Stop();
            Check(timer.ElapsedMilliseconds<100 && !release.IsSet, "status and pending intent snapshots never wait behind notification/file locks");
        }
        finally { release.Set(); save.GetAwaiter().GetResult(); }
        Check(!center.PersistenceStatus.Dirty, "pending save settles normally after status read");
    }
    private static void DraftPublicationFeedback(string directory)
    {
        string path = Path.Combine(directory, "draft-unknown.json"); Seed(path);
        int mode = 0; string? uncertainTarget = null, uncertainStaging = null;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var center = new NotificationCenter(path, null, null, (destination, bytes) =>
        {
            if (mode == 0)
            {
                uncertainTarget = destination; uncertainStaging = destination + ".owned-recovery";
                File.WriteAllBytes(destination, bytes); File.WriteAllBytes(uncertainStaging, bytes);
                throw new FilePublicationException(FilePublicationState.OutcomeUnknown, destination, uncertainStaging, "Owned unconfirmed publication fixture");
            }
            if (mode == 1)
            {
                entered.Set(); if (!release.Wait(5000)) throw new IOException("Owned draft publisher was not released");
                throw new FilePublicationException(FilePublicationState.NotPublished, destination, null, "Owned confirmed unpublished fixture");
            }
            PortableFilePublication.WriteNew(destination, bytes);
        });
        const string privateMarker = "RAM_ONLY_UNKNOWN_BACKUP_MARKER_f328a";
        center.Publish(Input("fixture", privateMarker));
        var external = new NotificationCenter(path); external.SetApplicationPriority("fixture", 1); byte[] disk = File.ReadAllBytes(path);
        center.SetApplicationPriority("fixture", 2);
        Check(!center.Reload(true) && center.PersistenceStatus.Dirty && center.Items.Single().Body == privateMarker && center.Applications.Single().Priority == 2 && Same(path, disk), "unknown draft publication aborts reload and retains exact live message/rules and active file");
        var diagnostic = center.LastDraftBackupFailure;
        Check(diagnostic is { State: FilePublicationState.OutcomeUnknown } && diagnostic.TargetPath == uncertainTarget && diagnostic.RetainedTemporaryPath == uncertainStaging, "unknown draft exposes structured target and staging paths without parsing error text");
        Check(File.Exists(diagnostic!.TargetPath) && File.Exists(diagnostic.RetainedTemporaryPath) && center.LastDraftBackupPath is null, "unconfirmed draft locations are inspectable but never advertised as a confirmed backup");
        Check(!File.ReadAllText(diagnostic.TargetPath).Contains(privateMarker, StringComparison.Ordinal) && !File.ReadAllText(diagnostic.RetainedTemporaryPath!).Contains(privateMarker, StringComparison.Ordinal), "unconfirmed backup does not silently persist recording-off message bodies");

        mode = 1; Task<bool> nextAttempt = Task.Run(() => center.Reload(true));
        try
        {
            Check(entered.Wait(3000), "next draft publication reaches genuinely blocked publisher");
            var timer = Stopwatch.StartNew(); var during = center.LastDraftBackupFailure; timer.Stop();
            Check(during is null && timer.ElapsedMilliseconds < 100 && !release.IsSet, "new backup attempt clears stale uncertainty through a lock-free diagnostic snapshot");
        }
        finally { release.Set(); }
        Check(!nextAttempt.GetAwaiter().GetResult() && center.Items.Single().Body == privateMarker && center.LastDraftBackupPath is null, "confirmed new backup failure leaves live data intact");
        Check(center.LastDraftBackupFailure is FilePublicationException { State: FilePublicationState.NotPublished }, "confirmed failure is structured separately from unknown publication");
        mode = 2;
        Check(center.Reload(true) && center.Applications.Single().Priority == 1 && center.Items.Count == 0 && Same(path, disk), "successful later draft publication permits explicit reload of disk state");
        Check(center.LastDraftBackupFailure is null && center.LastDraftBackupPath is string saved && saved != uncertainTarget && File.Exists(saved), "successful backup clears stale failure and reports its own confirmed path");
        Check(File.Exists(uncertainTarget) && File.Exists(uncertainStaging), "later attempts never remove earlier uncertain publication evidence");
    }
    private static void Bounds(string directory)
    {
        string path=Path.Combine(directory,"bounded.json"); var center=new NotificationCenter(path);
        for(int index=0;index<70;index++) center.Publish(new("fixture","Fixture","Owned "+index,new string('中',6000),DateTimeOffset.UtcNow));
        byte[] before=File.ReadAllBytes(path); center.SetPersistHistory(true);
        Check(center.PersistenceStatus.ProblemCode==StateFileProblemCode.FileTooLarge && center.PersistenceStatus.Dirty && !center.PersistHistory && center.PendingPersistHistory==true && Same(path,before), "oversized serializable draft marks dirty error without replacing readable disk");
        center.Clear(); Check(center.PersistHistory && center.PendingPersistHistory is null && !center.PersistenceStatus.Dirty, "reducing pending history to valid size permits its save");
        string malformed=Path.Combine(directory,"partial-state.json"); File.WriteAllText(malformed,"{\"Version\":1,\"PersistHistory\":false,\"Applications\":[{\"SourceId\":\"good\",\"Name\":\"Good\",\"Enabled\":true,\"Priority\":0},null],\"Items\":[]}");
        var bad=new NotificationCenter(malformed); Check(bad.PersistenceStatus.LoadFailed && bad.Applications.Count==0, "partially decoded invalid application list never reaches live state");
    }
    private static void DraftFailureBelongsToReload(string directory)
    {
        foreach (bool malformed in new[] { false, true })
        {
            string path = Path.Combine(directory, "draft-diagnostic-generation-" + malformed + ".json"); Seed(path); int attempts = 0;
            var center = new NotificationCenter(path, null, null, (destination, bytes) =>
            {
                attempts++; File.WriteAllBytes(destination, bytes);
                throw new FilePublicationException(FilePublicationState.OutcomeUnknown, destination, null, "Owned earlier unknown backup");
            });
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) center.SetApplicationPriority("fixture", 2);
            Check(!center.Reload(true) && center.LastDraftBackupFailure is { State: FilePublicationState.OutcomeUnknown }, "initial unknown diagnostic belongs to failed backup reload");
            Check(center.RetrySave() && !center.PersistenceStatus.Dirty && center.LastDraftBackupFailure is not null, "retry can save without erasing inspectable earlier backup uncertainty");
            if (malformed) File.WriteAllText(path, "{owned malformed after successful retry");
            bool loaded = center.Reload(true);
            Check(loaded == !malformed && attempts == 1, "clean later reload reads disk without invoking another draft publication");
            Check(center.LastDraftBackupFailure is null, "later reload does not reuse an earlier backup diagnostic even when the new read fails");
            Check(center.Applications.Single().Priority == 2 && center.PersistenceStatus.LoadFailed == malformed, "later reload accurately retains the valid rules and reports its own read result");
        }
    }
    private static void Links(string directory)
    {
        string target=Path.Combine(directory,"link-target.json"); Seed(target); byte[] bytes=File.ReadAllBytes(target);
        foreach(bool dangling in new[]{false,true})
        {
            string link=Path.Combine(directory,dangling?"dangling-state.json":"linked-state.json"); File.CreateSymbolicLink(link,dangling?Path.Combine(directory,"missing-target.json"):target);
            var center=new NotificationCenter(link); Check(center.PersistenceStatus.LoadFailed && !center.PersistenceStatus.CanRecover, "state symlink cannot be loaded/recovered, including dangling links");
            center.Publish(Input("fixture","new")); Check(!center.RecoverAndSave() && new FileInfo(link).LinkTarget is not null && Same(target,bytes), "notification save/recovery never follows or replaces state symlink");
        }
        string parent=Path.Combine(directory,"linked-parent"), actual=Path.Combine(directory,"actual-parent"); Directory.CreateDirectory(actual); Directory.CreateSymbolicLink(parent,actual); string through=Path.Combine(parent,"state.json");
        var blocked=new NotificationCenter(through); blocked.Publish(Input("fixture","parent link")); Check(blocked.PersistenceStatus.LoadFailed && !File.Exists(Path.Combine(actual,"state.json")), "direct parent symlink preserves prior notification rejection semantics");
        Check(!blocked.Reload(true) && Directory.GetFiles(actual).Length==0 && blocked.Items.Count==1, "draft reload cannot bypass rejected linked storage directory and retains RAM");
    }
    private static NotificationCenter Seed(string path) { var center=new NotificationCenter(path); center.Publish(Input("fixture","owned initial body")); return center; }
    private static NotificationInput Input(string source,string body)=>new(source,"Fixture","Owned "+body,body,DateTimeOffset.UtcNow);
    private static bool Same(string path,byte[] bytes)=>File.ReadAllBytes(path).SequenceEqual(bytes);
    private static void Check(bool condition,string message) { checks++; if(!condition) throw new Exception("Notification persistence: "+message); }
}
