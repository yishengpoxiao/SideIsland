using Avalonia.Controls;
using Avalonia.Layout;
using SideIsland.Core;
using SideIsland.Desktop.Platforms;
using System.Text.Json;

namespace SideIsland.Desktop;

public sealed partial class IslandWindow
{
    private bool preferenceDataBusy, notificationDataBusy, preferenceSaveQueued, dataClosePending, notificationReloadAcknowledged;
    private long preferenceEditRevision;
    private string preferenceDataMessage = "", notificationDataMessage = "", preferenceDraftBackup = "", preferenceSaveStatus = "";
    private int cachedNotificationUnread, queuedNotificationOmissions;
    private int dataReminderCount, dataReminderOmissions;
    private bool dataReminderShown;
    private readonly Queue<(string Title, string Body)> queuedDataNotifications = new();
    private const int MaximumQueuedDataNotifications = 20;
    private bool DataOperationBusy => preferenceDataBusy || notificationDataBusy;
    internal Task LatestPreferenceDataOperation { get; private set; } = Task.CompletedTask;
    internal Task LatestNotificationDataOperation { get; private set; } = Task.CompletedTask;
    private void OpenDataStatus() { selected = "data-status"; page = "tools"; RenderPage(); }

    private void MarkPreferenceEdit()
    {
        preferenceEditRevision++; store.MarkDirty();
    }
    private void RenderDataStatus()
    {
        body.Children.Add(Hint(T("设置与通知数据分别保存在本机。保存失败时，当前修改继续留在内存；恢复操作不会自动执行。", "Preferences and notifications are separate local files. Unsaved edits remain in memory; recovery is always explicit.")));
        body.Children.Add(DataStateCard("preferences", T("偏好设置", "Preferences"), store.PersistenceStatus, preferenceDataBusy,
            preferenceDataMessage, preferenceDraftBackup, false));
        var center = notificationCenter;
        body.Children.Add(DataStateCard("notifications", T("通知数据", "Notifications"), center?.PersistenceStatus, notificationDataBusy,
            notificationDataMessage, center?.LastDraftBackupPath ?? "", true));
        if (dataReminderCount > 0) body.Children.Add(Hint(T($"处理了 {dataReminderCount} 条岛内提醒，其中 {dataReminderOmissions} 条未保留预览。", $"Processed {dataReminderCount} island reminders; {dataReminderOmissions} previews were omitted.") + " " +
            (dataReminderShown ? T("已在消息中心汇总显示。", "A summary is shown in the message center.") : T("汇总未显示，请检查应用通知规则。", "The summary was not shown; check the app notification rules."))));
        body.Children.Add(Hint(T("恢复副本仍保留在本机，可能包含旧通知正文。关闭历史只影响当前状态文件和后续记录，不会自动删除已有副本。", "Recovery copies stay on this device and may contain old message content. Turning history off affects the active file and future records; existing copies are not deleted automatically.")));
    }
    private Control DataStateCard(string id, string title, StateFileStatus? state, bool busy, string message, string draft, bool notifications)
    {
        var content = new StackPanel { Spacing = 5 }; content.Children.Add(Text(title, 15));
        var statusText = Text(busy ? T("正在处理文件…", "Working on the file…") : DataStateTitle(state), 13); statusText.Name = "data-" + id + "-state"; content.Children.Add(statusText);
        string path = state?.TargetPath ?? Path.Combine(store.DirectoryPath, "notifications.json");
        var location = Hint(path); location.Name = "data-" + id + "-path"; content.Children.Add(location);
        if (state?.ProblemCode is { } code) { var detail = Hint(DataStateProblem(code)); detail.Name = "data-" + id + "-problem"; content.Children.Add(detail); }
        if (!string.IsNullOrEmpty(state?.RecoveryPath)) content.Children.Add(Hint(T("恢复副本：", "Recovery copy: ") + state.RecoveryPath));
        if (draft.Length > 0) { var backup = Hint(T("编辑草稿备份：", "Draft backup: ") + draft); backup.Name = "data-" + id + "-draft"; content.Children.Add(backup); }
        if (message.Length > 0) { var feedback = Hint(DataStateMessage(message)); feedback.Name = "data-" + id + "-feedback"; content.Children.Add(feedback); }
        if (notifications && notificationCenter?.LastDraftBackupFailure is { State: FilePublicationState.OutcomeUnknown } uncertain)
        {
            var locations = Hint(FilePublicationFeedback.Unknown(uncertain, prefs.Language)); locations.Name = "data-notifications-backup-outcome"; content.Children.Add(locations);
        }
        if (notifications && notificationCenter?.PendingPersistHistory is { } pending)
            content.Children.Add(Hint(pending
                ? T("开启历史尚未保存，正文记录仍未启用。可重试保存。", "Enabling history has not been saved; message recording remains off. Retry saving.")
                : T("正文记录已在内存中关闭，但磁盘旧正文是否清除尚未确认。可重试保存。", "Recording is off in memory, but removal of old content from the active file is unconfirmed. Retry saving.")));
        if (notifications && state is null)
        {
            var load = AsyncButton(T("读取通知数据状态", "Load notification data"), () => LatestNotificationDataOperation = RunNotificationDataAsync("load"), () => !closing && !DataOperationBusy);
            load.Name = "data-notifications-load"; content.Children.Add(load);
        }
        else
        {
            if (notifications)
            {
                var acknowledgement = Check(T("确认重载：仅内存中的消息不会写入草稿备份，将被重新读取的数据替换。", "Reload confirmation: memory-only messages are not written to a draft backup and will be replaced by loaded data."), notificationReloadAcknowledged,
                    value => { notificationReloadAcknowledged = value; RefreshDataStatus(); });
                acknowledgement.Name = "data-notifications-reload-ack"; acknowledgement.IsEnabled = !DataOperationBusy; content.Children.Add(acknowledgement);
            }
            var retry = AsyncButton(T("重试保存", "Retry save"), () => notifications ? LatestNotificationDataOperation = RunNotificationDataAsync("retry") : LatestPreferenceDataOperation = RunPreferenceDataAsync("retry"),
                () => !closing && !DataOperationBusy && state is { LoadFailed: false, NeedsReload: false });
            retry.Name = "data-" + id + "-retry";
            var reload = AsyncButton(T("备份修改并重载", "Back up edits & reload"), () => notifications ? LatestNotificationDataOperation = RunNotificationDataAsync("reload") : LatestPreferenceDataOperation = RunPreferenceDataAsync("reload"),
                () => !closing && !DataOperationBusy && (!notifications || notificationReloadAcknowledged));
            reload.Name = "data-" + id + "-reload";
            retry.VerticalAlignment = reload.VerticalAlignment = VerticalAlignment.Stretch; content.Children.Add(Actions(retry, reload));
            var recover = AsyncButton(T("备份坏文件并保存当前修改", "Back up damaged file & save edits"), () => notifications ? LatestNotificationDataOperation = RunNotificationDataAsync("recover") : LatestPreferenceDataOperation = RunPreferenceDataAsync("recover"),
                () => !closing && !DataOperationBusy && state is { CanRecover: true, NeedsReload: false });
            recover.Name = "data-" + id + "-recover"; content.Children.Add(recover);
            content.Children.Add(Hint(T("重载先备份未保存修改，再读取磁盘文件。恢复仅适用于无法读取的文件，原文件会保留为副本。", "Reload backs up unsaved edits before reading the disk file. Recovery is for unreadable files and retains the original as a copy.")));
        }
        return Card(content);
    }
    private string DataStateTitle(StateFileStatus? state) => state switch
    {
        null => T("尚未读取", "Not loaded"),
        { NeedsReload: true } => T("文件已变化，需要重载", "File changed; reload required"),
        { LoadFailed: true } => T("文件无法读取，原件保留", "Unreadable file; original retained"),
        { Dirty: true } => T("有未保存修改", "Unsaved edits"),
        { ProblemCode: StateFileProblemCode.CommittedConflict } => T("已保存，请核对保留副本", "Saved; review the retained copy"),
        _ => T("没有待保存修改", "No pending edits")
    };
    private string DataStateProblem(StateFileProblemCode code) => code switch
    {
        StateFileProblemCode.Conflict => T("检测到外部修改。当前编辑仍保留，请先备份并重载。", "An external change was detected. Current edits remain available; back up and reload first."),
        StateFileProblemCode.CommittedConflict => T("保存后的核对或清理未完成；请检查当前文件与保留副本。", "A check or cleanup after saving did not finish. Review the active file and retained copy."),
        StateFileProblemCode.InvalidData or StateFileProblemCode.LoadFailed => T("数据格式无法读取或未加载。可重载，或明确备份坏文件后保存。", "Data could not be decoded or has not loaded. Reload, or explicitly back up the damaged file and save."),
        StateFileProblemCode.FileTooLarge => T("数据超过大小限制。原文件保留，请减少内容或检查文件。", "Data exceeds the size limit. The original is retained; reduce content or inspect the file."),
        StateFileProblemCode.Busy => T("另一保存操作或锁文件占用此位置，请检查后重试。", "Another save or lock file is using this location. Check before retrying."),
        _ => T("文件操作失败。检查权限、可用空间和文件系统后重试。", "The file operation failed. Check access, free space and filesystem support before retrying.")
    };
    private string DataStateMessage(string value) => value switch
    {
        "saved" => T("当前修改已保存。", "Current edits saved."),
        "reloaded" => T("已重新载入磁盘数据。", "Reloaded the saved data."),
        "recovered" => T("原文件已备份，当前修改已保存。", "Original backed up; current edits saved."),
        "new-edits" => T("操作完成；期间新增的编辑仍保留，尚未保存。", "The operation completed. Newer edits remain in memory and are not yet saved."),
        "failed" => T("操作未完成，当前编辑仍保留。请检查文件状态后重试。", "The operation did not complete. Current edits remain available; check file status before retrying."),
        "backup-unknown" => T("草稿备份结果无法确认，未继续重载。当前编辑仍保留，请检查列出的副本位置。", "The draft backup outcome is unconfirmed; reload did not continue. Current edits remain in memory. Inspect the listed copy locations."),
        "loaded" => T("通知数据状态已读取，系统通知接入保持原设置。", "Notification data loaded; system notification connections are unchanged."),
        "queued" => T("文件操作期间的岛内提醒正在汇总，最多保留 20 条预览；超出数量会在汇总中说明。", "Island reminders received during file operations are summarized with up to 20 previews; omissions are reported in the summary."),
        _ => value
    };
    private void RefreshDataStatus()
    {
        if (!closing && page == "tools" && selected == "data-status") RenderPage();
    }
    private async Task BeforeDataOperation(string name) { if (integrations.BeforeDataOperation is not null) await integrations.BeforeDataOperation(name); }
    private async Task RunPreferenceDataAsync(string action)
    {
        if (closing || DataOperationBusy) return;
        var initial = store.PersistenceStatus;
        if (action == "recover" && (!initial.CanRecover || initial.NeedsReload) || action == "retry" && (initial.LoadFailed || initial.NeedsReload)) return;
        Preferences captured = prefs.Copy(); long revision = preferenceEditRevision;
        string capturedExecutable = drafts.GetValueOrDefault("codex-path", captured.CodexPath), capturedHome = drafts.GetValueOrDefault("codex-home", captured.CodexHome);
        bool connectionDrafts = capturedExecutable != captured.CodexPath || capturedHome != captured.CodexHome;
        preferenceDataBusy = true; preferenceDataMessage = ""; preferenceSaveQueued = false; saveDebounce.Stop(); RefreshDataStatus();
        try
        {
            if (action == "reload" && (initial.Dirty || connectionDrafts))
            {
                var backup = captured.Copy(); backup.CodexPath = capturedExecutable; backup.CodexHome = capturedHome;
                preferenceDraftBackup = await Task.Run(() =>
                {
                    CheckPreferenceDraftLocation();
                    Directory.CreateDirectory(store.DirectoryPath);
                    CheckPreferenceDraftLocation();
                    string draft = Path.Combine(store.DirectoryPath, "preferences.draft-" + Guid.NewGuid().ToString("N") + ".json");
                    PortableFilePublication.WriteNew(draft, JsonSerializer.SerializeToUtf8Bytes(backup, new JsonSerializerOptions { WriteIndented = true })); return draft;
                });
            }
            await BeforeDataOperation("preferences:" + action);
            Preferences? loaded = await Task.Run(() =>
            {
                if (action == "reload") return store.Reload(captured);
                if (action == "recover") store.RecoverAndSave(captured); else store.Save(captured); return null;
            });
            if (closing) return;
            bool changed = preferenceEditRevision != revision || action == "reload" &&
                (capturedExecutable != drafts.GetValueOrDefault("codex-path", captured.CodexPath) || capturedHome != drafts.GetValueOrDefault("codex-home", captured.CodexHome));
            if (changed) store.MarkDirty();
            if (action == "reload" && !store.PersistenceStatus.LoadFailed && !changed && loaded is not null)
            {
                // Reload is also a connection change. Cancel captured providers
                // before applying new settings so their late replies cannot
                // restore old metadata or reuse the previous Codex session.
                StopQuotaRequests(); StopMediaRequests(); quota = null; media = null;
                nextQuota = nextMedia = DateTime.MinValue;
                prefs.CopyFrom(loaded); drafts["notes"] = prefs.Notes; drafts["codex-path"] = prefs.CodexPath; drafts["codex-home"] = prefs.CodexHome;
                if (!focus.IsRunning) focus.SetCountdown(TimeSpan.FromMinutes(prefs.FocusMinutes), "Focus", true);
                Topmost = prefs.AlwaysOnTop; Rebuild();
            }
            preferenceDataMessage = store.PersistenceStatus.LoadFailed || store.PersistenceStatus.NeedsReload ? "failed" : changed ? "new-edits" : action == "reload" ? "reloaded" : action == "recover" ? "recovered" : "saved";
            if (preferenceDataMessage != "failed") ClearPreferenceSaveStatus();
        }
        catch (StateFileException error)
        {
            if (error.DraftCommitted) ClearPreferenceSaveStatus();
            if (!testMode) await Task.Run(() => DiagnosticLog.Write("preferences-data-" + action, error));
            if (!closing) preferenceDataMessage = error.DraftCommitted ? preferenceEditRevision == revision ? "saved" : "new-edits" : "failed";
        }
        catch (FilePublicationException error) when (error.State == FilePublicationState.OutcomeUnknown)
        {
            preferenceDraftBackup = error.TargetPath + (error.RetainedTemporaryPath is null ? "" : "\n" + error.RetainedTemporaryPath);
            if (!closing) preferenceDataMessage = "backup-unknown";
            if (!testMode) await Task.Run(() => DiagnosticLog.Write("preferences-draft-outcome-unknown", error));
        }
        catch (Exception error) { if (!closing) preferenceDataMessage = "failed"; if (!testMode) await Task.Run(() => DiagnosticLog.Write("preferences-data-" + action, error)); }
        finally
        {
            preferenceDataBusy = false;
            // Do not run a synchronous autosave immediately after an explicit
            // reload; newer edits remain visible as dirty for a deliberate retry.
            if (preferenceSaveQueued) { store.MarkDirty(); preferenceSaveQueued = false; }
            ClearDataCloseMessage(); RefreshDataStatus();
        }
    }
    private void CheckPreferenceDraftLocation()
    {
        // Match the state store: neither its file nor its immediate parent may
        // redirect recovery into an unapproved location through a link.
        foreach (string path in new[] { store.DirectoryPath, store.FilePath })
        {
            try
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0 || path == store.FilePath && (attributes & FileAttributes.Directory) != 0)
                    throw new IOException("Preference draft storage may not be a symbolic link or an invalid state file.");
            }
            catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
        }
    }
    private async Task RunNotificationDataAsync(string action)
    {
        if (closing || DataOperationBusy || action == "reload" && !notificationReloadAcknowledged) return;
        var center = notificationCenter; var initial = center?.PersistenceStatus;
        if (action != "load" && center is null || action == "recover" && initial is not { CanRecover: true, NeedsReload: false } || action == "retry" && initial is not { LoadFailed: false, NeedsReload: false }) return;
        notificationDataBusy = true; notificationDataMessage = ""; RefreshDataStatus();
        try
        {
            await BeforeDataOperation("notifications:" + action);
            bool succeeded;
            if (action == "load")
            {
                center = await Task.Run(() => CreateNotificationData());
                if (closing) return; AttachNotificationData(center); succeeded = !center.PersistenceStatus.LoadFailed;
            }
            else succeeded = await Task.Run(() => action == "reload" ? center!.Reload(true) : action == "recover" ? center!.RecoverAndSave() : center!.RetrySave());
            if (!succeeded && action == "reload" && center?.LastDraftBackupFailure is { State: FilePublicationState.OutcomeUnknown } uncertain && !testMode)
                await Task.Run(() => DiagnosticLog.Write("notifications-draft-outcome-unknown", uncertain));
            if (!closing) notificationDataMessage = succeeded ? action == "load" ? "loaded" : action == "reload" ? "reloaded" : action == "recover" ? "recovered" : "saved" :
                action == "reload" && center?.LastDraftBackupFailure?.State == FilePublicationState.OutcomeUnknown ? "backup-unknown" : "failed";
            if (center is not null) cachedNotificationUnread = await Task.Run(() => center.UnreadCount);
            // This is the final await before releasing the guard. A summary
            // commit can itself receive reminders; drain every bounded batch.
            if (center is not null) await DrainDataNotificationsAsync(center);
        }
        catch (Exception error) { if (!closing) notificationDataMessage = "failed"; if (!testMode) await Task.Run(() => DiagnosticLog.Write("notification-data-" + action, error)); }
        finally
        {
            notificationDataBusy = false; notificationReloadAcknowledged = false; ClearDataCloseMessage();
            if (!closing)
            {
                if (page == "messages" || page == "tools" && selected is "data-status" or "notifications") RenderPage();
                RefreshCompact();
            }
        }
    }
    private async Task DrainDataNotificationsAsync(NotificationCenter center)
    {
        if (queuedDataNotifications.Count == 0 && queuedNotificationOmissions == 0) return;
        dataReminderCount = dataReminderOmissions = 0; dataReminderShown = false;
        while (queuedDataNotifications.Count > 0 || queuedNotificationOmissions > 0)
        {
            var queued = queuedDataNotifications.ToArray(); queuedDataNotifications.Clear(); int omitted = queuedNotificationOmissions; queuedNotificationOmissions = 0;
            int total = queued.Length + omitted;
            string title = T($"保存期间收到 {total} 条岛内提醒", $"{total} island reminders arrived during saving");
            string message = string.Join("\n", queued.Select(item => item.Title + ": " + item.Body));
            if (omitted > 0) message += "\n" + T($"另有 {omitted} 条未保留预览。", $"Previews for {omitted} additional reminders were omitted.");
            var result = await Task.Run(() =>
            {
                bool shown = center.Publish(new("side-island", "Side Island", title, message, DateTimeOffset.UtcNow, Internal: true));
                return (shown, unread: center.UnreadCount);
            });
            dataReminderShown |= result.shown; cachedNotificationUnread = result.unread;
            dataReminderCount += total; dataReminderOmissions += omitted;
        }
        if (notificationDataMessage == "queued") notificationDataMessage = "";
    }
    private void QueueDataNotification(string title, string message)
    {
        if (queuedDataNotifications.Count < MaximumQueuedDataNotifications) queuedDataNotifications.Enqueue((ReminderPreview(title, 100), ReminderPreview(message, 150)));
        else queuedNotificationOmissions++;
        notificationDataMessage = "queued"; RefreshDataStatus();
    }
    private static string ReminderPreview(string value, int maximum)
    {
        int count = Math.Min(value.Length, maximum);
        if (count > 0 && char.IsHighSurrogate(value[count - 1])) count--;
        return value[..count];
    }
    private int NotificationUnread() => notificationDataBusy ? cachedNotificationUnread : cachedNotificationUnread = notificationCenter?.UnreadCount ?? 0;
    private void PollDataNotificationQueue()
    {
        if (closing || DataOperationBusy || notificationCenter is null || queuedDataNotifications.Count == 0 && queuedNotificationOmissions == 0) return;
        LatestNotificationDataOperation = FlushDataNotificationQueueAsync(notificationCenter);
    }
    private async Task FlushDataNotificationQueueAsync(NotificationCenter center)
    {
        notificationDataBusy = true;
        try { await DrainDataNotificationsAsync(center); }
        catch (Exception error) { if (!testMode) await Task.Run(() => DiagnosticLog.Write("notification-data-reminders", error)); }
        finally { notificationDataBusy = false; ClearDataCloseMessage(); RefreshDataStatus(); if (!closing && page == "messages") RenderPage(); }
    }
    private bool TryPrepareDataClose()
    {
        if (!DataOperationBusy) return true;
        dataClosePending = true; Report(T("数据文件正在处理，请完成后再关闭。", "A data file operation is still running. Close after it finishes.")); return false;
    }
    private void ClearPreferenceSaveStatus()
    {
        if (preferenceSaveStatus.Length > 0 && status == preferenceSaveStatus) Report("");
        preferenceSaveStatus = "";
    }
    private void ClearDataCloseMessage() { if (dataClosePending) { dataClosePending = false; Report(""); } }
}
