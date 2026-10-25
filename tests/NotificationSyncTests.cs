using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Threading;

namespace CodexIsland
{
    internal static class NotificationSyncTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        public static void Run()
        {
            RunnerDeadlines(); RunnerStopAndRecovery();
            string previousFolder = Environment.GetEnvironmentVariable("CODEX_ISLAND_DATA_DIR");
            var previousContext = SynchronizationContext.Current;
            string folder = Path.Combine(Path.GetTempPath(), "island-notification-sync-" + Guid.NewGuid().ToString("N"));
            try
            {
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", folder);
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                new SettingsStore().Save(new Settings { NotificationsEnabled = true, AlwaysOnTop = false, AutoCollapse = false });
                FirstPaintAndInput(); WindowGenerationAndPermissions(); WindowTimeoutRecovery(); WindowPreviewReplacement();
                Console.WriteLine("PASS: background notification startup, bounded probes, responsive first paint, cached status, revoked permissions and stale-result guards.");
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", previousFolder);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }
        private static TaskCompletionSource<NotificationSnapshot> Source() { return new TaskCompletionSource<NotificationSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously); }
        private static NotificationSnapshot Allowed(string id)
        {
            var snapshot = new NotificationSnapshot { Access = NotificationAccessState.Allowed, ReadSucceeded = true };
            snapshot.Messages.Add(new IslandNotification { Id = id, AppId = "synthetic-sync", AppName = "Synthetic fixture", Title = id, CreatedAt = DateTime.UtcNow }); return snapshot;
        }
        private static void RunnerDeadlines()
        {
            var delayed = Source(); int calls = 0, caller = Thread.CurrentThread.ManagedThreadId, worker = caller;
            using (var runner = new NotificationSyncRunner(() => { worker = Thread.CurrentThread.ManagedThreadId; return Interlocked.Increment(ref calls) == 1 ? delayed.Task : Task.FromResult(Allowed("fresh")); }, 80))
            {
                var watch = Stopwatch.StartNew(); var expired = Wait(runner.SynchronizeAsync());
                Check(expired.Error.Contains("超时") && expired.Messages.Count == 0 && watch.ElapsedMilliseconds < 2000, "complete probe/read/recheck pipeline has a caller deadline");
                Check(worker != caller, "even synchronous probe startup executes outside caller/UI thread");
                for (int i = 0; i < 12; i++) Check(Wait(runner.SynchronizeAsync()).Error.Contains("暂缓"), "still-running probe reports postponed retry");
                Check(calls == 1, "timed-out probes never accumulate a second worker");
                delayed.SetResult(Allowed("late")); WaitWorker(runner);
                var fresh = Wait(runner.SynchronizeAsync());
                Check(fresh.ReadSucceeded && fresh.Messages[0].Id == "fresh" && calls == 2 && expired.Messages.Count == 0, "late result is discarded and next fresh read recovers");
            }
        }
        private static void RunnerStopAndRecovery()
        {
            var delayed = Source(); int calls = 0;
            var runner = new NotificationSyncRunner(() => { Interlocked.Increment(ref calls); return delayed.Task; }, 10000);
            Task<NotificationSnapshot> waiting = runner.SynchronizeAsync();
            SpinUntil(() => calls == 1, "worker began before disposal"); runner.Dispose();
            var stopped = Wait(waiting); Check(stopped.Error.Contains("停止") && stopped.Messages.Count == 0, "disposing promptly frees waiter without waiting for system call");
            delayed.SetResult(Allowed("too-late")); WaitWorker(runner);
            Check(Wait(runner.SynchronizeAsync()).Error.Contains("停止") && calls == 1, "disposed runner rejects all subsequent probes");
            int attempt = 0;
            using (var recovery = new NotificationSyncRunner(() => { if (Interlocked.Increment(ref attempt) == 1) throw new InvalidOperationException("synthetic private detail"); return Task.FromResult(new NotificationSnapshot { Access = NotificationAccessState.NotGranted }); }, 1000))
            {
                var failure = Wait(recovery.SynchronizeAsync()); Check(failure.Error != null && !failure.Error.Contains("private detail"), "synchronous startup failure is contained without exporting raw details");
                Check(Wait(recovery.SynchronizeAsync()).Access == NotificationAccessState.NotGranted, "startup failure releases probe slot for recovery");
            }
        }
        private static void FirstPaintAndInput()
        {
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                int ui = Thread.CurrentThread.ManagedThreadId, worker = ui;
                var runner = new NotificationSyncRunner(() => { worker = Thread.CurrentThread.ManagedThreadId; entered.Set(); release.Wait(); return Task.FromResult(new NotificationSnapshot { Access = NotificationAccessState.NotGranted }); }, 150);
                IslandWindow window = null;
                try
                {
                    window = new IslandWindow(true, runner); bool rendered = false, inputProcessed = false;
                    var clock = Stopwatch.StartNew(); long firstPaint = -1;
                    window.ContentRendered += (s, e) => { rendered = true; firstPaint = clock.ElapsedMilliseconds; };
                    window.Show(); PumpUntil(() => rendered && entered.IsSet, "first paint occurs while permission probe is still blocked"); StopTimers(window);
                    Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => inputProcessed = true));
                    PumpUntil(() => inputProcessed, "Dispatcher still processes input while permission probe is blocked");
                    Check(!release.IsSet && worker != ui, "blocked startup remains isolated from UI through first render and input");
                    PumpUntil(() => !Read<bool>(window, "notificationPolling"), "slow startup deadline clears polling flag");
                    Check(Read<TextBlock>(window, "notificationExplanation").Text.Contains("超时"), "slow startup presents actionable timeout instead of endless loading");
                    Console.WriteLine("Notification startup fixture: first ContentRendered " + firstPaint + " ms while permission probe is blocked.");
                    release.Set(); WaitWorker(runner);
                }
                finally { release.Set(); if (window != null) window.Close(); else runner.Dispose(); WaitWorker(runner); }
            }
        }
        private static void WindowGenerationAndPermissions()
        {
            var responses = new[] { Source(), Source(), Source(), Source(), Source() }; int calls = 0;
            var runner = new NotificationSyncRunner(() => responses[Interlocked.Increment(ref calls) - 1].Task, 2000);
            var window = new IslandWindow(true, runner);
            try
            {
                var settings = Read<Settings>(window, "settings"); var feed = Read<NotificationFeed>(window, "notificationFeed");
                Call(window, "UpdateNotificationConnection"); Check(calls == 0 && Read<TextBlock>(window, "notificationState").Text.Contains("检查"), "cached status rendering starts no permission calls");
                Call(window, "PollNotifications"); PumpUntil(() => calls == 1, "first synthetic request starts");
                settings.HideNotificationBody = true; Call(window, "ConfigureNotifications"); responses[0].SetResult(Allowed("old-generation"));
                PumpUntil(() => calls == 2, "changed generation automatically starts a fresh request");
                Check(feed.Items.Count == 0 && !Read<NotificationAccessState?>(window, "notificationCachedAccess").HasValue, "stale generation publishes neither data nor permission cache");
                responses[1].SetResult(Allowed("current-generation")); PumpUntil(() => !Read<bool>(window, "notificationPolling"), "current generation completes");
                Check(feed.Items.Count == 1 && feed.Items[0].Id == "current-generation" && Read<TextBlock>(window, "notificationState").Text.Contains("已连接"), "current snapshot is published and clears loading");
                Call(window, "PollNotifications"); PumpUntil(() => calls == 3, "revocation request starts");
                var revoked = Allowed("revoked-data"); revoked.Access = NotificationAccessState.Denied; responses[2].SetResult(revoked);
                PumpUntil(() => !Read<bool>(window, "notificationPolling"), "revocation completes");
                Check(feed.Items.Count == 1 && Read<TextBlock>(window, "notificationState").Text.Contains("未获允许"), "revocation updates connection and discards accompanying data");
                Call(window, "PollNotifications"); PumpUntil(() => calls == 4, "request starts before disable");
                settings.NotificationsEnabled = false; Call(window, "ConfigureNotifications"); responses[3].SetResult(Allowed("disabled-data"));
                PumpUntil(() => !Read<bool>(window, "notificationPolling"), "disabled generation finishes without publication");
                Check(feed.Items.Count == 0 && Read<TextBlock>(window, "notificationState").Text.Contains("已关闭"), "disable clears history and cannot be overwritten by an in-flight result");
                settings.NotificationsEnabled = true; Call(window, "ConfigureNotifications"); PumpUntil(() => calls == 5, "reenabling starts a fresh generation");
                string state = Read<TextBlock>(window, "notificationState").Text; window.Close();
                responses[4].SetResult(Allowed("after-close")); WaitWorker(runner); PumpUntil(() => !Read<bool>(window, "notificationPolling"), "closing releases pending UI waiter");
                Check(feed.Items.Count == 0 && Read<TextBlock>(window, "notificationState").Text == state, "closed window never publishes late data or status");
            }
            finally { foreach (var response in responses) response.TrySetResult(new NotificationSnapshot { Access = NotificationAccessState.NotGranted }); window.Close(); WaitWorker(runner); }
        }
        private static void WindowTimeoutRecovery()
        {
            var delayed = Source(); int calls = 0;
            var runner = new NotificationSyncRunner(() => Interlocked.Increment(ref calls) == 1 ? delayed.Task : Task.FromResult(Allowed("fresh-after-timeout")), 80);
            var window = new IslandWindow(true, runner);
            try
            {
                Call(window, "PollNotifications"); PumpUntil(() => !Read<bool>(window, "notificationPolling"), "timeout restores UI controls");
                var retry = Read<Button>(window, "notificationAccess");
                Check(retry.IsEnabled && Read<TextBlock>(window, "notificationState").Text.Contains("失败"), "timed-out startup offers enabled retry");
                Call(window, "PollNotifications"); PumpUntil(() => !Read<bool>(window, "notificationPolling"), "postponed retry completes quickly");
                Check(calls == 1 && Read<TextBlock>(window, "notificationState").Text.Contains("失败"), "postponed retry preserves failure and never starts a duplicate probe");
                delayed.SetResult(Allowed("late-timeout-result")); WaitWorker(runner); Call(window, "PollNotifications"); PumpUntil(() => !Read<bool>(window, "notificationPolling"), "fresh retry succeeds");
                var feed = Read<NotificationFeed>(window, "notificationFeed");
                Check(feed.Items.Count == 1 && feed.Items[0].Id == "fresh-after-timeout" && Read<TextBlock>(window, "notificationState").Text.Contains("已连接"), "fresh successful read replaces error without publishing old timed-out data");
            }
            finally { delayed.TrySetResult(new NotificationSnapshot { Access = NotificationAccessState.NotGranted }); window.Close(); WaitWorker(runner); }
        }
        private static void WindowPreviewReplacement()
        {
            var response = Source(); int calls = 0;
            var runner = new NotificationSyncRunner(() => { Interlocked.Increment(ref calls); return response.Task; }, 2000);
            var window = new IslandWindow(true, runner) { ShowActivated = false, IsHitTestVisible = false };
            var settings = Read<Settings>(window, "settings");
            settings.NotificationsEnabled = true; settings.NotificationPreview = true; settings.QuietPreviews = false;
            settings.HideNotificationBody = false; settings.HoverExpandEnabled = false; settings.AutoMusic = false; settings.OnlineLyricsEnabled = false;
            DateTime stamp = DateTime.UtcNow;
            Func<string, string, string, IslandNotification> message = (id, title, body) => new IslandNotification {
                Id = id, AppId = "replacement-sync", AppName = "Replacement fixture", Title = title, Body = body, CreatedAt = stamp };
            Func<IslandNotification[], NotificationSnapshot> snapshot = messages => {
                var value = new NotificationSnapshot { Access = NotificationAccessState.Allowed, ReadSucceeded = true };
                value.Messages.AddRange(messages); return value;
            };
            Action<IslandNotification[]> publish = messages => {
                response = Source(); int before = calls; Call(window, "PollNotifications");
                PumpUntil(() => calls == before + 1, "replacement snapshot worker starts");
                response.SetResult(snapshot(messages)); PumpUntil(() => !Read<bool>(window, "notificationPolling"), "replacement snapshot reaches UI");
            };
            try
            {
                var first = message("current", "Original title", "Original body");
                var preferences = Read<NotificationPreferences>(window, "notificationPreferences");
                preferences.Observe(new[] { first }); preferences.SetPriority(NotificationPreferences.SourceKey(first), 1);
                window.Loaded += (s, e) => StopTimers(window); window.Show();
                PumpUntil(() => calls == 1, "preview fixture baseline starts");
                response.SetResult(snapshot(new IslandNotification[0]));
                PumpUntil(() => !Read<bool>(window, "notificationPolling"), "preview fixture baseline completes");
                publish(new[] { first });
                Check(Read<bool>(window, "peeking") && Read<TextBlock>(window, "peekBody").Text == first.Body, "new important snapshot opens its real preview");
                Call(window, "StopVisualTransition", true);

                var updated = message("current", "Updated title", "Updated body"); publish(new[] { updated });
                Check(Read<TextBlock>(window, "peekTitle").Text == updated.Title && Read<TextBlock>(window, "peekBody").Text == updated.Body &&
                    Read<IslandNotification>(window, "peekMessage").Body == updated.Body, "same feed identity refreshes current preview title, body and model");
                Call(window, "StopNotificationPeekTransition"); StopTimers(window);
                int version = Read<int>(window, "peekTransitionVersion");
                publish(new[] { message("current", updated.Title, updated.Body) });
                Check(Read<int>(window, "peekTransitionVersion") == version && Read<Stopwatch>(window, "peekTransitionWatch") == null &&
                    !Read<DispatcherTimer>(window, "peekTimer").IsEnabled, "identical replacement neither replays motion nor renews preview lifetime");

                var newer = message("new-arrival", "New arrival wins", "New arrival body");
                publish(new[] { message("current", "Older replacement", "Must not supersede arrival"), newer });
                Check(Read<IslandNotification>(window, "peekMessage").Id == newer.Id && Read<TextBlock>(window, "peekTitle").Text == newer.Title,
                    "a new important arrival wins over a simultaneous current-identity replacement");

                settings.HideNotificationBody = true; Call(window, "ApplyAppearance"); StopTimers(window);
                version = Read<int>(window, "peekTransitionVersion");
                var privateUpdate = message(newer.Id, "Private updated title", "Private updated body"); publish(new[] { privateUpdate });
                Check(Read<bool>(window, "peeking") && Read<IslandNotification>(window, "peekMessage").Body == privateUpdate.Body &&
                    Read<TextBlock>(window, "peekBody").Text == UiLocalization.Localize("消息内容已隐藏") &&
                    !String.Equals(Read<TextBlock>(window, "peekBody").ToolTip as string, privateUpdate.Body, StringComparison.Ordinal) &&
                    Read<int>(window, "peekTransitionVersion") == version && !Read<DispatcherTimer>(window, "peekTimer").IsEnabled,
                    "masked replacements retain fresh original data without exposing it, replaying motion or renewing timeout");
                settings.HideNotificationBody = false; Call(window, "ApplyAppearance");
                Check(Read<TextBlock>(window, "peekBody").Text == privateUpdate.Body, "unmasking reveals the latest replacement rather than the old copied body");

                window.SetExpanded(false, false); publish(new[] { message(newer.Id, "Ended replacement", "Ended body") });
                Check(!Read<bool>(window, "peeking") && Read<IslandNotification>(window, "peekMessage") == null, "a replacement never reopens an ended preview");
                settings.NotificationPreview = false; publish(new[] { message("preview-disabled", "Disabled arrival", "Disabled body") });
                Check(!Read<bool>(window, "peeking") && Read<IslandNotification>(window, "peekMessage") == null, "disabled previews never open for a new snapshot arrival");

                settings.NotificationPreview = true; response = Source(); int beforeClose = calls;
                Call(window, "PollNotifications"); PumpUntil(() => calls == beforeClose + 1, "late preview snapshot starts before close");
                var feed = Read<NotificationFeed>(window, "notificationFeed"); int count = feed.Items.Count;
                window.Close(); response.TrySetResult(snapshot(new[] { message("after-close-preview", "Late title", "Late body") })); WaitWorker(runner);
                PumpUntil(() => !Read<bool>(window, "notificationPolling"), "closed preview fixture releases pending result");
                Check(!Read<bool>(window, "peeking") && Read<IslandNotification>(window, "peekMessage") == null && feed.Items.Count == count,
                    "a closed window never publishes or opens a late preview snapshot");
                Console.WriteLine("PASS: fake monitor replacement snapshots refresh live previews, preserve unchanged clocks, prefer new arrivals and respect privacy, ended previews, disabled previews and closure.");
            }
            finally { response.TrySetResult(new NotificationSnapshot { Access = NotificationAccessState.NotGranted }); window.Close(); WaitWorker(runner); }
        }
        private static NotificationSnapshot Wait(Task<NotificationSnapshot> task) { SpinUntil(() => task.IsCompleted, "runner task completes within test deadline"); return task.GetAwaiter().GetResult(); }
        private static void WaitWorker(NotificationSyncRunner runner)
        {
            SpinUntil(() => { var work = (Task)typeof(NotificationSyncRunner).GetField("pending", Private).GetValue(runner); return work == null || work.IsCompleted; }, "isolated underlying worker eventually finishes");
        }
        private static void SpinUntil(Func<bool> condition, string message) { var timer = Stopwatch.StartNew(); while (!condition() && timer.ElapsedMilliseconds < 3000) Thread.Sleep(5); Check(condition(), message); }
        private static void PumpUntil(Func<bool> condition, string message)
        {
            if (!condition())
            {
                var clock = Stopwatch.StartNew(); var frame = new DispatcherFrame(); var tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(5) };
                tick.Tick += (s, e) => { if (condition() || clock.ElapsedMilliseconds >= 3000) frame.Continue = false; };
                tick.Start(); try { Dispatcher.PushFrame(frame); } finally { tick.Stop(); }
            }
            Check(condition(), message);
        }
        private static void StopTimers(IslandWindow window) { foreach (var field in typeof(IslandWindow).GetFields(Private)) if (field.FieldType == typeof(DispatcherTimer)) ((DispatcherTimer)field.GetValue(window)).Stop(); }
        private static T Read<T>(IslandWindow window, string name) { return (T)typeof(IslandWindow).GetField(name, Private).GetValue(window); }
        private static void Call(IslandWindow window, string name, params object[] arguments) { typeof(IslandWindow).GetMethod(name, Private).Invoke(window, arguments); }
        private static void Check(bool value, string message) { if (!value) throw new Exception("Notification sync: " + message); }
    }
}
