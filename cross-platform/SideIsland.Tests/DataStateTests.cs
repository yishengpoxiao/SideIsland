using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CodexIsland;
using SideIsland.Core;
using SideIsland.Desktop;
using SideIsland.Desktop.Platforms;

namespace SideIsland.Tests;

internal static class DataStateTests
{
    private static int checks;
    public static void Run(string? artifacts = null)
    {
        checks = 0;
        RecoveryAndConflicts(); CommittedWarning(); PreferenceRaces(); ReloadConnections(); NotificationRaces(); NotificationBackupOutcomes(); ReminderTail(); DraftLocation();
        int layouts = Layouts(artifacts);
        Console.WriteLine($"PASS: {checks} data status/recovery checks; {layouts} populated bilingual theme/size/DPI layouts. Actual controls, blocked filesystem commits, guarded reloads, captured preference drafts, late media/quota replies, bounded reminder summaries, and local privacy warnings. Owned fixtures only; no native notification/media or Mac/Linux claim.");
    }

    private static void RecoveryAndConflicts()
    {
        using var fixture = new Fixture(corruptPreferences: true); var window = fixture.Window;
        byte[] damaged = File.ReadAllBytes(fixture.Store.FilePath);
        Check(fixture.Store.PersistenceStatus.LoadFailed && Find<Button>(window, "data-preferences-recover").IsEnabled, "unreadable preferences expose explicit recovery");
        Check(!Find<Button>(window, "data-preferences-retry").IsEnabled, "ordinary retry cannot overwrite damaged state");
        window.SelectToolForTest("settings"); Click(window, "settings-data-status"); Flush(window);
        Check(Find<TextBlock>(window, "data-preferences-path").Text == fixture.Store.FilePath && fixture.NotificationLoads == 0, "settings links to state without automatically reading notification data");
        EditNotes(window, "unsaved recovery draft 中文 😀"); window.SelectToolForTest("data-status");
        Wait(window, Begin(window, "preferences", "recover"));
        Check(!fixture.Store.PersistenceStatus.LoadFailed && File.ReadAllBytes(fixture.Store.PersistenceStatus.RecoveryPath!).SequenceEqual(damaged), "explicit recovery saves draft and retains exact damaged original");
        Check(Field<string>(window, "status") == "", "successful explicit recovery removes the obsolete autosave failure footer");
        Check(new PreferenceStore(fixture.DirectoryPath).Load().Notes == "unsaved recovery draft 中文 😀", "recovered data contains the current unsaved preference edits");
        EditNotes(window, "dirty local edit"); window.SelectToolForTest("data-status");
        var external = window.Settings.Copy(); external.Notes = "external saved value"; WritePrefs(fixture.Store.FilePath, external);
        Wait(window, Begin(window, "preferences", "retry"));
        Check(fixture.Store.PersistenceStatus.NeedsReload && window.Settings.Notes == "dirty local edit", "optimistic save conflict retains memory edits");
        Check(!Find<Button>(window, "data-preferences-retry").IsEnabled && !Find<Button>(window, "data-preferences-recover").IsEnabled, "conflict cannot be bypassed with retry or corrupt-file recovery");
        Wait(window, Begin(window, "preferences", "reload"));
        string backup = Field<string>(window, "preferenceDraftBackup");
        Check(File.Exists(backup) && JsonSerializer.Deserialize<Preferences>(File.ReadAllBytes(backup))!.Notes == "dirty local edit", "reload first retains unsaved local preferences in a new draft file");
        Check(window.Settings.Notes == "external saved value", "successful explicit reload applies disk values");
        window.SelectToolForTest("notes"); Check(window.GetVisualDescendants().OfType<TextBox>().Single(box => box.AcceptsReturn).Text == "external saved value", "successful reload refreshes cached editor draft");
        EditNotes(window, "must survive invalid reload"); window.SelectToolForTest("data-status"); File.WriteAllText(fixture.Store.FilePath, "{invalid");
        Wait(window, Begin(window, "preferences", "reload"));
        Check(window.Settings.Notes == "must survive invalid reload" && fixture.Store.PersistenceStatus.Dirty, "failed reload never erases current preference edits");
        Check(JsonSerializer.Deserialize<Preferences>(File.ReadAllBytes(Field<string>(window, "preferenceDraftBackup")))!.Notes == "must survive invalid reload", "failed reload still exposes successfully saved draft");
    }

    private static void PreferenceRaces()
    {
        using var fixture = new Fixture(); var window = fixture.Window;
        EditNotes(window, "captured before slow disk"); window.SelectToolForTest("data-status"); fixture.PreferenceGate.Arm();
        Task saving = Begin(window, "preferences", "retry"); WaitUntil(window, () => fixture.PreferenceGate.Entered.IsSet);
        try
        {
            var clock = Stopwatch.StartNew();
            AppearanceTestActions.Set(window, "zh", true); window.SelectToolForTest("notes"); EditNotes(window, "new edit while save is blocked");
            window.SelectToolForTest("data-status"); Flush(window); window.Collapse(); window.Expand(); Flush(window);
            Check(clock.ElapsedMilliseconds < 1200, "actual theme/language/page/expand actions never wait on blocked preference commit");
            Check(!Find<Button>(window, "data-preferences-retry").IsEnabled && !Find<Button>(window, "data-notifications-load").IsEnabled, "app-wide data guard survives rebuilding controls");
            int operations = fixture.BeforeCalls; Click(window, "data-preferences-retry"); Check(fixture.BeforeCalls == operations, "rebuilt button cannot start duplicate IO");
            window.Close(); Check(window.IsVisible && !Field<bool>(window, "closing"), "close is clearly postponed while a commit is in progress");
        }
        finally { fixture.PreferenceGate.Release(); }
        Wait(window, saving);
        Check(window.Settings.Notes == "new edit while save is blocked" && fixture.Store.PersistenceStatus.Dirty, "late save cannot overwrite newer in-memory preference changes or clear their dirty state");
        Check(new PreferenceStore(fixture.DirectoryPath).Load().Notes == "captured before slow disk", "blocked save wrote only its captured immutable snapshot");
        Check(Field<string>(window, "status") == "", "finished file operation clears temporary close warning");
        Check(Find<Button>(window, "data-preferences-retry").IsEnabled, "explicit retry is restored after a blocked save");

        var external = window.Settings.Copy(); external.Notes = "late loaded value"; WritePrefs(fixture.Store.FilePath, external);
        fixture.Delay("preferences:reload"); Task loading = Begin(window, "preferences", "reload"); WaitUntil(window, () => fixture.PendingEntered);
        EditNotes(window, "edited while reload waited"); window.SelectToolForTest("data-status"); fixture.ReleaseDelay(); Wait(window, loading);
        Check(window.Settings.Notes == "edited while reload waited" && fixture.Store.PersistenceStatus.Dirty, "late reload cannot replace preferences edited after the click");
        Check(Field<string>(window, "preferenceDataMessage") == "new-edits", "late reload reports retained newer edits instead of claiming all edits saved");

        fixture.Delay("preferences:reload"); loading = Begin(window, "preferences", "reload"); WaitUntil(window, () => fixture.PendingEntered);
        window.SelectToolForTest("quota");
        window.GetVisualDescendants().OfType<TextBox>().Single(box => box.PlaceholderText == "CODEX_HOME").Text = "late unconnected home 中文";
        window.GetVisualDescendants().OfType<TextBox>().Single(box => box.PlaceholderText != "CODEX_HOME").Text = "late unconnected executable";
        fixture.ReleaseDelay(); Wait(window, loading); window.SelectToolForTest("quota");
        Check(window.GetVisualDescendants().OfType<TextBox>().Single(box => box.PlaceholderText == "CODEX_HOME").Text == "late unconnected home 中文", "late reload preserves unconnected Codex home draft typed while waiting");
        Check(window.GetVisualDescendants().OfType<TextBox>().Single(box => box.PlaceholderText != "CODEX_HOME").Text == "late unconnected executable", "late reload preserves unconnected Codex executable draft typed while waiting");
        window.SelectToolForTest("data-status"); Wait(window, Begin(window, "preferences", "retry"));
        var retry = new PreferenceStore(fixture.DirectoryPath).Load();
        Check(retry.CodexHome != "late unconnected home 中文" && retry.CodexPath != "late unconnected executable" && fixture.Quotas.Count == 0, "retry does not persist or connect unapplied Codex input drafts");
        fixture.ReleaseDelay(); Wait(window, Begin(window, "preferences", "reload"));
        var connectionBackup = JsonSerializer.Deserialize<Preferences>(File.ReadAllBytes(Field<string>(window, "preferenceDraftBackup")))!;
        Check(connectionBackup.CodexHome == "late unconnected home 中文" && connectionBackup.CodexPath == "late unconnected executable", "a subsequent explicit reload first backs up unconnected connection drafts");
    }

    private static void CommittedWarning()
    {
        using var fixture = new Fixture(corruptPreferences: true); var window = fixture.Window;
        Check(Field<string>(window, "status").Contains("not saved", StringComparison.Ordinal), "owned unreadable preference file produces a real prior autosave failure footer");
        byte[] damaged = File.ReadAllBytes(fixture.Store.FilePath); bool injected = false;
        fixture.PreferenceCheckpoint = stage =>
        {
            if (stage == "committed" && !injected) { injected = true; throw new IOException("Owned follow-up failure after actual preference commit"); }
        };
        Wait(window, Begin(window, "preferences", "recover"));
        bool originalRetained = File.ReadAllBytes(fixture.Store.PersistenceStatus.RecoveryPath!).SequenceEqual(damaged);
        bool actualSaved = new PreferenceStore(fixture.DirectoryPath).Load().Language == "en";
        bool retainedOldFailure = Field<string>(window, "status").Contains("not saved", StringComparison.Ordinal);
        Console.WriteLine($"OBSERVATION: actual preference recovery committed={actualSaved}; exact damaged original retained={originalRetained}; status={fixture.Store.PersistenceStatus.ProblemCode}; stale failure footer={retainedOldFailure}");
        Check(injected && actualSaved && originalRetained && fixture.Store.PersistenceStatus.ProblemCode == StateFileProblemCode.CommittedConflict, "post-commit fixture uses a real committed draft and retained recovery copy");
        Check(Field<string>(window, "preferenceDataMessage") == "saved", "confirmed post-commit warning correctly says edits were saved");
        Check(!retainedOldFailure && Field<string>(window, "status") == "", "confirmed commit warning clears obsolete not-saved footer");
        Check(Find<TextBlock>(window, "data-preferences-state").Text == "Saved; review the retained copy", "confirmed warning stays visible without falsely asserting an external conflict");
    }

    private static void ReloadConnections()
    {
        using var fixture = new Fixture(); var window = fixture.Window;
        Task oldQuota = InvokeTask(window, "ConnectQuota", "old executable", "old home");
        Task oldMedia = InvokeTask(window, "ConnectMedia");
        Check(fixture.Quotas.Count == 1 && fixture.Media.Count == 1, "controlled old provider requests are genuinely pending");
        var loaded = window.Settings.Copy(); loaded.CodexPath = "new executable 中文"; loaded.CodexHome = "new home 😀"; loaded.FocusMinutes = 73; loaded.Notes = "loaded notes";
        WritePrefs(fixture.Store.FilePath, loaded); window.SelectToolForTest("data-status"); Wait(window, Begin(window, "preferences", "reload"));
        Check(fixture.Quotas[0].Client.Disposed && fixture.Quotas[0].Client.Token.IsCancellationRequested && fixture.Media[0].Token.IsCancellationRequested, "reload cancels old requests and disposes previous Codex client");
        Check(Field<DateTime>(window, "nextQuota") == DateTime.MinValue && Field<DateTime>(window, "nextMedia") == DateTime.MinValue, "reload schedules fresh provider reads");
        Check(Field<FocusTimer>(window, "focus").Value == TimeSpan.FromMinutes(73), "idle focus timer adopts the reloaded duration");
        fixture.Quotas[0].Client.Response.TrySetResult(new() { PlanType = "obsolete" }); fixture.Media[0].Response.TrySetResult(Media("obsolete")); Wait(window, oldQuota); Wait(window, oldMedia);
        Check(Field<UsageSnapshot?>(window, "quota") is null && Field<MediaSnapshot?>(window, "media") is null, "late old provider replies cannot repopulate stale compact content");
        Task newQuota = InvokeTask(window, "ReadQuota"); Task newMedia = InvokeTask(window, "ReadMedia", false);
        Check(fixture.Quotas.Count == 2 && fixture.Quotas[1].Path == loaded.CodexPath && fixture.Quotas[1].Home == loaded.CodexHome, "next quota session uses the newly loaded executable and home");
        fixture.Quotas[1].Client.Response.TrySetResult(new() { PlanType = "fresh" }); fixture.Media[1].Response.TrySetResult(Media("fresh")); Wait(window, newQuota); Wait(window, newMedia);
        Check(Field<UsageSnapshot>(window, "quota").PlanType == "fresh" && Field<MediaSnapshot>(window, "media").Title == "fresh", "fresh provider results work after reload");
        FocusTimer focus = Field<FocusTimer>(window, "focus"); focus.Start(); var before = focus.Value;
        loaded = window.Settings.Copy(); loaded.FocusMinutes = 5; loaded.MediaEnabled = false; loaded.CodexEnabled = false; WritePrefs(fixture.Store.FilePath, loaded);
        Wait(window, Begin(window, "preferences", "reload"));
        Check(focus.IsRunning && Math.Abs((focus.Value - before).TotalSeconds) < 3 && window.Settings.FocusMinutes == 5, "reload retains running focus countdown while changing the next-session setting");
        Check(Field<UsageSnapshot?>(window, "quota") is null && Field<MediaSnapshot?>(window, "media") is null, "reload also clears previously completed media/quota snapshots");
    }

    private static void NotificationRaces()
    {
        using var fixture = new Fixture(); var window = fixture.Window;
        Wait(window, Begin(window, "notifications", "load")); NotificationCenter center = fixture.Center!;
        Check(fixture.NotificationLoads == 1 && !Find<Button>(window, "data-notifications-reload").IsEnabled, "notification data load is explicit and reload requires memory-only history acknowledgement");
        center.Publish(new("fixture", "Owned fixture", "memory marker", "never persist this body", DateTimeOffset.UtcNow));
        fixture.NotificationGate.Arm(); Task saving = Begin(window, "notifications", "retry"); WaitUntil(window, () => fixture.NotificationGate.Entered.IsSet);
        try
        {
            var clock = Stopwatch.StartNew(); window.Settings.Slot1 = "unread";
            AppearanceTestActions.Set(window, "zh", true); Invoke(window, "SelectPage", "messages"); Flush(window);
            Check(window.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text?.Contains("正在处理", StringComparison.Ordinal) == true), "message view exposes busy state without reading locked notification collection");
            window.SelectToolForTest("notifications"); Flush(window); window.SelectToolForTest("data-status"); Flush(window);
            for (int index = 0; index < 27; index++) Invoke(window, "Notice", "owned reminder " + index, new string('a', 149) + "😀");
            window.Collapse(); window.Expand(); Flush(window);
            Check(clock.ElapsedMilliseconds < 1500, "notification gate does not block theme changes, rules/messages, compact unread or bounded reminders");
            Check(Field<int>(window, "queuedNotificationOmissions") == 7 && Field<System.Collections.ICollection>(window, "queuedDataNotifications").Count == 20, "reminder buffering is bounded with explicit omission count");
            window.Close(); Check(window.IsVisible, "notification commit prevents closing before its real outcome");
        }
        finally { fixture.NotificationGate.Release(); }
        Wait(window, saving);
        Check(Field<int>(window, "dataReminderCount") == 27 && Field<int>(window, "dataReminderOmissions") == 7 && Field<bool>(window, "dataReminderShown"), "queued reminders become one counted summary after save");
        Check(center.Items.Count == 2 && center.Items.Any(item => item.Body.Contains("7", StringComparison.Ordinal)), "bounded summary retains an explicit omitted-preview count");
        Check(!File.ReadAllText(center.PersistenceStatus.TargetPath).Contains("never persist this body", StringComparison.Ordinal), "recording-off UI operations do not persist RAM-only message bodies");
        AppearanceTestActions.Set(window, "en", false);
        // An unsaved rule is rescued, while explicit acknowledgement makes the
        // unavoidable loss of memory-only messages reviewable before reloading.
        File.WriteAllText(center.PersistenceStatus.TargetPath, "{broken"); center.SetApplicationEnabled("fixture", false);
        int beforeReload = center.Items.Count;
        Check(center.PersistenceStatus.Dirty, "controlled external change makes a genuinely unsaved notification rule");
        window.SelectToolForTest("data-status"); Find<CheckBox>(window, "data-notifications-reload-ack").IsChecked = true; Flush(window);
        Wait(window, Begin(window, "notifications", "reload"));
        Check(center.PersistenceStatus.LoadFailed && center.Items.Count == beforeReload, "failed notification reload preserves current memory messages after applying app visibility rules");
        string backup = center.LastDraftBackupPath!; string bytes = File.ReadAllText(backup);
        Check(bytes.Contains("fixture", StringComparison.Ordinal) && !bytes.Contains("never persist this body", StringComparison.Ordinal), "reload draft preserves rules without silently persisting disabled-history content");
        Check(!Find<Button>(window, "data-notifications-reload").IsEnabled, "reload acknowledgement resets after each attempt");
        center.SetPersistHistory(true); window.SelectToolForTest("data-status"); Flush(window);
        Check(center.PendingPersistHistory == true && AllText(window).Contains("recording remains off", StringComparison.Ordinal), "failed opt-in is visible without claiming active recording");
        Wait(window, Begin(window, "notifications", "recover"));
        Check(!center.PersistenceStatus.LoadFailed && center.PersistHistory && File.Exists(center.PersistenceStatus.RecoveryPath), "notification recovery explicitly retains original and applies requested history policy");
        Check(AllText(window).Contains("existing copies are not deleted", StringComparison.Ordinal), "privacy notice distinguishes active history from retained recovery copies");
    }

    private static void DraftLocation()
    {
        using var fixture = new Fixture(); var window = fixture.Window;
        EditNotes(window, "dirty draft must stay in RAM"); window.SelectToolForTest("data-status");
        string moved = fixture.DirectoryPath + "-owned-target"; Directory.Move(fixture.DirectoryPath, moved);
        bool linked = false;
        try
        {
            try { Directory.CreateSymbolicLink(fixture.DirectoryPath, moved); linked = true; }
            catch (Exception error) when (error is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            { Console.WriteLine("SKIP: owned symbolic-link draft test unavailable: " + error.GetType().Name); }
            if (linked)
            {
                Wait(window, Begin(window, "preferences", "reload"));
                Check(!Directory.EnumerateFiles(moved, "preferences.draft-*").Any() && window.Settings.Notes == "dirty draft must stay in RAM", "draft backup cannot bypass rejected state parent symlink");
                Check(Field<string>(window, "preferenceDataMessage") == "failed", "link rejection is surfaced as an incomplete operation");
            }
        }
        finally { if (linked) Directory.Delete(fixture.DirectoryPath); Directory.Move(moved, fixture.DirectoryPath); }
    }

    private static void NotificationBackupOutcomes()
    {
        using var fixture = new Fixture(); var window = fixture.Window;
        Wait(window, Begin(window, "notifications", "load")); var center = fixture.Center!;
        center.Publish(new("owned", "Owned fixture", "retain in memory", "private RAM marker", DateTimeOffset.UtcNow));
        File.WriteAllText(center.PersistenceStatus.TargetPath, "{invalid external file"); center.SetApplicationPriority("owned", 2);
        string? target = null; string retained = Path.Combine(fixture.DirectoryPath, "owned-uncertain-draft.tmp");
        fixture.DraftPublisher = (path, bytes) => { target = path; File.WriteAllBytes(retained, bytes); throw new FilePublicationException(FilePublicationState.OutcomeUnknown, path, retained, "Owned uncertain publication"); };
        window.SelectToolForTest("data-status"); Find<CheckBox>(window, "data-notifications-reload-ack").IsChecked = true; Flush(window);
        Wait(window, Begin(window, "notifications", "reload"));
        Check(center.Items.Single().Body == "private RAM marker" && center.PersistenceStatus.Dirty, "uncertain draft publication never continues reload or loses current notification memory");
        Check(Field<string>(window, "notificationDataMessage") == "backup-unknown", "uncertain notification draft has distinct outcome feedback");
        string text = Find<TextBlock>(window, "data-notifications-backup-outcome").Text!;
        Check(text.Contains("could not be confirmed", StringComparison.Ordinal) && text.Contains(target!, StringComparison.Ordinal) && text.Contains(retained, StringComparison.Ordinal), "uncertain outcome exposes both exact target and retained-copy paths");
        Check(!File.ReadAllText(retained).Contains("private RAM marker", StringComparison.Ordinal), "uncertain notification draft still honors recording-off privacy");
        AppearanceTestActions.Set(window, "zh", true);
        Check(Find<TextBlock>(window, "data-notifications-backup-outcome").Text!.StartsWith("无法确认", StringComparison.Ordinal), "unknown backup state translates after an actual appearance rebuild");
        fixture.DraftPublisher = null; Find<CheckBox>(window, "data-notifications-reload-ack").IsChecked = true; Flush(window);
        Wait(window, Begin(window, "notifications", "reload"));
        Check(center.LastDraftBackupFailure is null && !window.GetVisualDescendants().OfType<TextBlock>().Any(control => control.Name == "data-notifications-backup-outcome"), "a later completed draft attempt clears obsolete uncertainty even when loading malformed state fails");
    }

    private static void ReminderTail()
    {
        using var fixture = new Fixture(); var window = fixture.Window;
        Wait(window, Begin(window, "notifications", "load"));
        fixture.Delay("notifications:retry"); Task saving = Begin(window, "notifications", "retry"); WaitUntil(window, () => fixture.PendingEntered);
        for (int index = 0; index < 27; index++) Invoke(window, "Notice", "first reminder " + index, "owned preview");
        // First prepared commit is RetrySave; second is the first aggregate.
        fixture.NotificationGate.Arm(2); fixture.ReleaseDelay(); WaitUntil(window, () => fixture.NotificationGate.Entered.IsSet);
        try
        {
            Check(Field<bool>(window, "notificationDataBusy"), "first summary has a genuinely blocked persistence commit");
            for (int index = 0; index < 5; index++) Invoke(window, "Notice", "late during summary " + index, "owned tail preview");
            Check(Field<System.Collections.ICollection>(window, "queuedDataNotifications").Count == 5, "new reminders actually arrive after the first batch was copied");
        }
        finally { fixture.NotificationGate.Release(); }
        Wait(window, saving);
        Check(Field<System.Collections.ICollection>(window, "queuedDataNotifications").Count == 0 && Field<int>(window, "queuedNotificationOmissions") == 0, "final guard is released only after tail reminders are drained without waiting for a timer tick");
        Check(Field<int>(window, "dataReminderCount") == 32 && Field<int>(window, "dataReminderOmissions") == 7, "multi-batch counts retain all arrivals and declared omissions");
        Check(fixture.Center!.Items.Count == 2 && fixture.Center.Items.Any(item => item.Body.Contains("late during summary 4", StringComparison.Ordinal)), "second aggregate retains the final reminder received during the first summary write");
        window.Close(); Check(!window.IsVisible, "immediate close after draining does not strand a trailing reminder queue");
    }

    private static int Layouts(string? artifacts)
    {
        using var fixture = new Fixture(corruptPreferences: true, corruptNotifications: true); var window = fixture.Window;
        Wait(window, Begin(window, "notifications", "load")); fixture.Center!.SetPersistHistory(true);
        EditNotes(window, "layout dirty 中文 😀"); window.SelectToolForTest("data-status"); int count = 0;
        foreach (string language in new[] { "en", "zh" }) foreach (bool light in new[] { false, true })
        foreach (int width in new[] { 320, 480, 760 }) foreach (int height in new[] { 360, 720 }) foreach (double scale in new[] { 1d, 1.25, 1.5, 2 })
        {
            AppearanceTestActions.Set(window, language, light); window.SetRenderScaling(scale); window.Width = width; window.Height = height; Flush(window);
            string context = $"{language}/{light}/{width}/{height}/{scale}";
            if (language == "zh")
            {
                Invoke(window, "Save"); string footer = Field<TextBlock>(window, "liveStatus").Text ?? "";
                Check(footer.Contains("偏好设置未保存", StringComparison.Ordinal) && !footer.Any(letter => letter is >= 'a' and <= 'z' or >= 'A' and <= 'Z'), "Chinese failed-save footer is localized and actually rendered " + context);
            }
            foreach (string section in new[] { "preferences", "notifications" })
            {
                var first = Find<Button>(window, "data-" + section + "-retry"); var second = Find<Button>(window, "data-" + section + "-reload");
                var a = first.TranslatePoint(default, window); var b = second.TranslatePoint(default, window);
                Check(Math.Abs(first.Bounds.Width - second.Bounds.Width) <= 1 && Math.Abs(first.Bounds.Height - second.Bounds.Height) <= 1, "equal grouped action dimensions " + context + section);
                Check(a is Point p && b is Point q && Math.Abs(p.Y - q.Y) <= 1 && Math.Abs(q.X - p.X - first.Bounds.Width - 6) <= 1, "aligned group and compact gap " + context + section);
                foreach (var button in new[] { first, second, Find<Button>(window, "data-" + section + "-recover") })
                {
                    var label = (TextBlock)button.Content!; var point = button.TranslatePoint(default, window);
                    Check(label.Bounds.Height > 0 && label.Bounds.Height + button.Padding.Top + button.Padding.Bottom <= button.Bounds.Height + 2 && point is Point origin && origin.X >= -1 && origin.X + button.Bounds.Width <= width + 1, "no cropped or overflowing action caption " + context + section);
                }
                foreach (string field in new[] { "path", "problem" })
                {
                    var text = Find<TextBlock>(window, "data-" + section + "-" + field); var origin = text.TranslatePoint(default, window);
                    Check(text.Bounds.Width > 0 && text.Bounds.Height >= 1 && origin is Point point && point.X >= 0 && point.X + text.Bounds.Width <= width + 1, "populated wrapped status stays inside viewport " + context + field);
                }
            }
            Check(AllText(window).Contains(language == "en" ? "Recovery copies stay on this device" : "恢复副本仍保留在本机", StringComparison.Ordinal), "privacy recovery guidance follows actual language change " + context);
            if (artifacts is not null && height == 720 && scale == 1 && (language == "en" && light && width == 320 || language == "zh" && !light && width == 480))
            {
                Directory.CreateDirectory(artifacts); AvaloniaHeadlessPlatform.ForceRenderTimerTick(3); Dispatcher.UIThread.RunJobs();
                using var image = new RenderTargetBitmap(new(width, height), new(96, 96)); image.Render(window); image.Save(Path.Combine(artifacts, $"data-status-{language}-{width}.png"), PngBitmapEncoderOptions.Default);
                Find<Button>(window, "data-notifications-recover").BringIntoView(); Flush(window);
                using var lower = new RenderTargetBitmap(new(width, height), new(96, 96)); lower.Render(window); lower.Save(Path.Combine(artifacts, $"data-status-notifications-{language}-{width}.png"), PngBitmapEncoderOptions.Default);
            }
            count++;
        }
        Check(count == 96, "complete data status layout matrix"); return count;
    }

    private sealed class CommitGate : IDisposable
    {
        private int armed;
        internal readonly ManualResetEventSlim Entered = new(false), Released = new(false);
        internal void Arm(int occurrence = 1) { Entered.Reset(); Released.Reset(); Interlocked.Exchange(ref armed, occurrence); }
        internal void Checkpoint(string stage)
        {
            if (stage != "prepared" || Volatile.Read(ref armed) <= 0 || Interlocked.Decrement(ref armed) != 0) return;
            Entered.Set(); if (!Released.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException("Owned blocked commit was not released; UI may have waited on disk.");
        }
        internal void Release() => Released.Set();
        public void Dispose() { Release(); Entered.Dispose(); Released.Dispose(); }
    }
    private sealed class QuotaFixture : IQuotaSession
    {
        internal bool Disposed; internal CancellationToken Token;
        internal readonly TaskCompletionSource<UsageSnapshot> Response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<UsageSnapshot> ReadAsync(CancellationToken token) { Token = token; return Response.Task; }
        public void Dispose() => Disposed = true;
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "side-island-data-state-" + Guid.NewGuid().ToString("N"));
        internal readonly CommitGate PreferenceGate = new(), NotificationGate = new();
        internal readonly PreferenceStore Store;
        internal readonly IslandWindow Window;
        internal NotificationCenter? Center;
        internal Action<string, byte[]>? DraftPublisher;
        internal Action<string>? PreferenceCheckpoint;
        internal int NotificationLoads, BeforeCalls;
        internal readonly List<(string Path, string Home, QuotaFixture Client)> Quotas = new();
        internal readonly List<(CancellationToken Token, TaskCompletionSource<MediaSnapshot> Response)> Media = new();
        private string? delayedAction; private TaskCompletionSource? pending;
        internal bool PendingEntered;
        internal Fixture(bool corruptPreferences = false, bool corruptNotifications = false)
        {
            Directory.CreateDirectory(DirectoryPath); string preferencePath = Path.Combine(DirectoryPath, "preferences.json");
            if (corruptPreferences) File.WriteAllText(preferencePath, "{owned unreadable preferences 中文");
            else WritePrefs(preferencePath, new() { Language = "en", LastTool = "data-status" });
            if (corruptNotifications) File.WriteAllText(Path.Combine(DirectoryPath, "notifications.json"), "{owned unreadable notifications 中文");
            Store = new(DirectoryPath, new PortableStateFile(preferencePath, 1024 * 1024, stage => { PreferenceGate.Checkpoint(stage); PreferenceCheckpoint?.Invoke(stage); }));
            var integrations = new IntegrationSources(token => { var response = new TaskCompletionSource<MediaSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously); Media.Add((token, response)); return response.Task; }, (_, _) => Task.FromResult<string?>(null),
                (path, home) => { var client = new QuotaFixture(); Quotas.Add((path, home, client)); return client; })
            {
                PreferenceData = Store,
                CreateNotificationData = path => { Interlocked.Increment(ref NotificationLoads); return Center = new NotificationCenter(path, null, new PortableStateFile(path, 1024 * 1024, NotificationGate.Checkpoint), (destination, bytes) => { if (DraftPublisher is { } publish) publish(destination, bytes); else PortableFilePublication.WriteNew(destination, bytes); }); },
                BeforeDataOperation = action => { BeforeCalls++; if (action != delayedAction || pending is null) return Task.CompletedTask; PendingEntered = true; return pending.Task; }
            };
            Window = new IslandWindow(true, DirectoryPath, integrations); Window.Show(); Window.Expand(); AppearanceTestActions.Set(Window, "en", false); Window.SelectToolForTest("data-status"); Flush(Window); StopDebounce(Window);
        }
        internal void Delay(string action) { delayedAction = action; pending = new(TaskCreationOptions.RunContinuationsAsynchronously); PendingEntered = false; }
        internal void ReleaseDelay() { pending?.TrySetResult(); delayedAction = null; }
        public void Dispose()
        {
            PreferenceGate.Release(); NotificationGate.Release(); ReleaseDelay();
            foreach (var item in Quotas) item.Client.Response.TrySetResult(new()); foreach (var item in Media) item.Response.TrySetResult(DataStateTests.Media("cleanup"));
            Wait(Window, Window.LatestPreferenceDataOperation); Wait(Window, Window.LatestNotificationDataOperation); Window.Close(); Flush(Window);
            PreferenceGate.Dispose(); NotificationGate.Dispose();
            string full = Path.GetFullPath(DirectoryPath), parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(Path.GetDirectoryName(full), parent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("side-island-data-state-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected data fixture cleanup path");
            Directory.Delete(full, true);
        }
    }
    private static MediaSnapshot Media(string title) => new(true, "owned", title, "fixture", null, 0, 120, true, null);
    private static void WritePrefs(string path, Preferences value) => File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(value));
    private static void StopDebounce(IslandWindow window) => Field<DispatcherTimer>(window, "saveDebounce").Stop();
    private static void EditNotes(IslandWindow window, string value) { window.SelectToolForTest("notes"); window.GetVisualDescendants().OfType<TextBox>().Single(box => box.AcceptsReturn).Text = value; StopDebounce(window); }
    private static Task Begin(IslandWindow window, string section, string action)
    {
        var button = Find<Button>(window, "data-" + section + "-" + action);
        Check(button.IsEnabled, "actual data action is available: " + section + ":" + action);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); return section == "preferences" ? window.LatestPreferenceDataOperation : window.LatestNotificationDataOperation;
    }
    private static string AllText(Window window) => string.Join("\n", window.GetVisualDescendants().OfType<TextBlock>().Select(control => control.Text));
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static object? Invoke(object instance, string name, params object[] values) => instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, values);
    private static Task InvokeTask(object instance, string name, params object[] values) => (Task)Invoke(instance, name, values)!;
    private static void Click(Window window, string name) => Find<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Flush(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
    private static void Wait(Window window, Task task) { WaitUntil(window, () => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void WaitUntil(Window window, Func<bool> done)
    {
        var clock = Stopwatch.StartNew(); while (!done() && clock.ElapsedMilliseconds < 15000) { Flush(window); Thread.Sleep(2); }
        if (!done()) throw new TimeoutException("Data state fixture did not finish"); Flush(window);
    }
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException("Data state: " + message); }
}
