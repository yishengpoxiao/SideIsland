using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace CodexIsland
{
    // Only injected readers, temporary LRC fixtures and the own Dispatcher are used.
    // No fixture queries a player, invokes playback controls or contacts the network.
    internal static class NowPlayingMonitorTests
    {
        internal static void Run()
        {
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            string temporaryRoot = Path.GetFullPath(Path.GetTempPath());
            string fixture = Path.GetFullPath(Path.Combine(temporaryRoot, "CodexIslandMonitorTests-" + Guid.NewGuid().ToString("N")));
            Check(fixture.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase), "Fixture stays under the temporary directory");
            Directory.CreateDirectory(fixture);
            try
            {
                string first = Path.Combine(fixture, "first.lrc"), second = Path.Combine(fixture, "second.lrc");
                File.WriteAllText(first, "[00:00]Local A\n[00:02]Local A next");
                File.WriteAllText(second, "[00:00]Imported A\n[00:02]Imported A next");
                SlowReadAndRestart();
                LibraryWorkDoesNotBlockDispatcher();
                TrackChangeAndOffline(first);
                OnlineTransitionsAndDisposal();
                OnlineRestartAndDemoPrivacy();
                LookupSignatureUsesCompletedMetadata();
                FailedOnlineLookupBacksOffAndRecovers();
                MetadataCompletionReplacesPendingLookup();
                LyricsStatusAndExplicitRetry(first);
                LocalLyricsWin(first);
                ImportDuringPendingRead(first, second);
                ImportDuringTrackChange(first, second);
                ImportForUpcomingTrack(second);
                Console.WriteLine("PASS: compact media monitor responsiveness, generations, privacy, bounded lyric retries, metadata completion, online cancellation, import races and shutdown.");
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
                if (Directory.Exists(fixture)) Directory.Delete(fixture, true);
            }
        }

        private static MediaLyricsLibrary Library()
        { return new MediaLyricsLibrary(Path.Combine(Path.GetTempPath(), "CodexIslandMonitorMemory-" + Guid.NewGuid().ToString("N")), true); }

        private static MediaSnapshot Track(string title)
        {
            return new MediaSnapshot { Available = true, Playing = true, Title = title, Artist = "Fixture Artist", Source = "Fixture.Player",
                Position = TimeSpan.FromSeconds(1), Duration = TimeSpan.FromMinutes(3), SampledAtUtc = DateTime.UtcNow };
        }

        private static void SlowReadAndRestart()
        {
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                int reads = 0, readerThread = 0, dispatcherThread = Thread.CurrentThread.ManagedThreadId;
                var titles = new List<string>();
                using (var monitor = new NowPlayingMonitor(false, delegate
                {
                    readerThread = Thread.CurrentThread.ManagedThreadId;
                    int call = Interlocked.Increment(ref reads);
                    if (call == 1) { entered.Set(); release.Wait(4000); }
                    return Task.FromResult(Track(call == 1 ? "Stale" : "Fresh"));
                }, Library()))
                {
                    monitor.Changed += () => { if (monitor.Snapshot.Available) titles.Add(monitor.Snapshot.Title); };
                    try
                    {
                        var watch = Stopwatch.StartNew(); monitor.Start(); watch.Stop();
                        Check(watch.ElapsedMilliseconds < 500, "Starting a slow synchronous reader returns promptly");
                        Check(entered.Wait(1000), "Injected slow reader started");
                        Check(readerThread != dispatcherThread, "Reader synchronous work runs off Dispatcher");
                        bool input = false; Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => input = true));
                        PumpUntil(() => input, 500);
                        monitor.Stop(); monitor.Stop(); monitor.Start();
                        release.Set();
                        PumpUntil(() => monitor.Snapshot.Title == "Fresh", 1500);
                        Check(reads == 2 && titles.Count == 1 && titles[0] == "Fresh", "Restart ignores the old generation and immediately fetches a fresh sample");
                        Check(monitor.LastPlayingIdentity == NowPlayingMonitor.Identity(monitor.Snapshot) && monitor.LastPlayingUtc != DateTime.MinValue, "Playing identity and timestamp follow the fresh sample");
                    }
                    finally { release.Set(); monitor.Stop(); PumpUntil(() => !Field<bool>(monitor, "reading"), 1500); }
                }
            }
        }

        private static void LibraryWorkDoesNotBlockDispatcher()
        {
            var library = Library();
            object gate = Field<object>(library, "sync");
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                Task blocker = Task.Run(delegate { lock (gate) { entered.Set(); release.Wait(4000); } });
                Check(entered.Wait(1000), "Library lock fixture starts");
                using (var monitor = new NowPlayingMonitor(false, () => Task.FromResult(Track("Library")), library))
                {
                    try
                    {
                        monitor.Start();
                        PumpUntil(() => Field<bool>(monitor, "reading"), 500);
                        bool input = false; var watch = Stopwatch.StartNew();
                        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => input = true));
                        PumpUntil(() => input, 500); watch.Stop();
                        Check(watch.ElapsedMilliseconds < 500, "Blocked lyric storage cannot block Dispatcher input");
                        Check(!monitor.Snapshot.Available, "A blocked storage read has not committed a partial sample");
                        release.Set(); PumpUntil(() => monitor.Snapshot.Title == "Library", 1000);
                    }
                    finally { release.Set(); monitor.Stop(); blocker.Wait(1500); PumpUntil(() => !Field<bool>(monitor, "reading"), 1500); }
                }
            }
        }

        private static void TrackChangeAndOffline(string lyricsFile)
        {
            var library = Library(); var first = Track("A"); library.Import(first.Source, first.Title, first.Artist, lyricsFile);
            MediaSnapshot current = first; int external = 0; bool mismatch = false;
            using (var monitor = new NowPlayingMonitor(false, () => Task.FromResult(current), library, delegate(MediaSnapshot track, CancellationToken token)
            { Interlocked.Increment(ref external); return Task.FromResult(LrcDocument.Parse("[00:00]Online")); }))
            {
                monitor.Changed += () => { if (monitor.Snapshot.Title != "A" && monitor.Lyrics != null) mismatch = true; };
                monitor.Start(); PumpUntil(() => monitor.Lyrics != null, 1000);
                Check(monitor.Lyric == "Local A", "Local lyric follows current position");
                current = Track("B"); monitor.Refresh(); PumpUntil(() => monitor.Snapshot.Title == "B", 1000);
                Check(monitor.Lyrics == null && monitor.Lyric == "" && !mismatch, "Changed track immediately drops previous track lyrics");
                current = new MediaSnapshot(); monitor.Refresh(); PumpUntil(() => !monitor.Snapshot.Available, 1000);
                Check(monitor.Lyrics == null && external == 0, "Default offline mode never calls the external provider");
                monitor.EnableOnline(false); Check(external == 0, "Explicitly keeping online lookup off issues no request");
                monitor.Stop();
            }
        }

        private static void OnlineTransitionsAndDisposal()
        {
            var first = new TaskCompletionSource<LrcDocument>(); var second = new TaskCompletionSource<LrcDocument>();
            MediaSnapshot current = Track("Online A"); int requests = 0, events = 0; CancellationToken firstToken = default(CancellationToken);
            using (var monitor = new NowPlayingMonitor(false, () => Task.FromResult(current), Library(), delegate(MediaSnapshot track, CancellationToken token)
            {
                int call = Interlocked.Increment(ref requests);
                if (call == 1) { firstToken = token; return first.Task; }
                return second.Task;
            }))
            {
                monitor.Changed += () => events++;
                try
                {
                    monitor.EnableOnline(true); monitor.Start(); PumpUntil(() => requests == 1, 1000);
                    current = Track("Online B"); monitor.Refresh(); PumpUntil(() => monitor.Snapshot.Title == "Online B" && requests == 2, 1000);
                    Check(firstToken.IsCancellationRequested, "Changing track cancels old online lookup");
                    first.SetResult(LrcDocument.Parse("[00:00]Stale online"));
                    PumpUntil(() => !Field<bool>(monitor, "reading"), 500);
                    Check(monitor.Lyrics == null, "Late old-track online result cannot overwrite the current track");
                    second.SetResult(LrcDocument.Parse("[00:00]Online B lyric")); PumpUntil(() => monitor.Lyrics != null, 1000);
                    Check(monitor.Lyric == "Online B lyric", "Current online result is displayed");
                    monitor.EnableOnline(false); Check(monitor.Lyrics == null, "Disabling online lookup removes its displayed lyric");
                    current = Track("Online C"); monitor.Refresh(); PumpUntil(() => monitor.Snapshot.Title == "Online C", 1000);
                    Check(requests == 2, "Disabled online lookup stays silent after track changes");
                    monitor.Dispose(); int stoppedEvents = events; monitor.Start(); monitor.Refresh();
                    Check(!monitor.Running && events == stoppedEvents, "Disposed monitor cannot restart or raise notifications");
                }
                finally { first.TrySetResult(null); second.TrySetResult(null); monitor.Dispose(); }
            }

            var pending = new TaskCompletionSource<MediaSnapshot>();
            using (var monitor = new NowPlayingMonitor(false, () => pending.Task, Library()))
            {
                int changes = 0; monitor.Changed += () => changes++; monitor.Start();
                Check(changes == 0, "Starting a pending first read does not publish an unchanged initial status");
                monitor.Dispose();
                Check(changes == 0, "Dispose detaches subscribers before stopping and publishes no shutdown status");
                pending.SetResult(Track("Late disposed sample")); PumpUntil(() => !Field<bool>(monitor, "reading"), 1000);
                Check(changes == 0 && !monitor.Snapshot.Available, "Completing an in-flight sample cannot resurrect a disposed monitor");
            }

            var onlinePending = new TaskCompletionSource<LrcDocument>(); CancellationToken canceled = default(CancellationToken); int onlineCalls = 0;
            using (var monitor = new NowPlayingMonitor(false, () => Task.FromResult(Track("Pending dispose")), Library(), delegate(MediaSnapshot track, CancellationToken token)
            { canceled = token; Interlocked.Increment(ref onlineCalls); return onlinePending.Task; }))
            {
                monitor.EnableOnline(true); monitor.Start(); PumpUntil(() => onlineCalls == 1, 1000); monitor.Dispose();
                Check(canceled.IsCancellationRequested, "Dispose cancels pending online lookup");
                onlinePending.SetResult(LrcDocument.Parse("[00:00]Late"));
                Check(monitor.Lyrics == null, "Disposed monitor retains no late online lyric");
            }
        }

        private static void OnlineRestartAndDemoPrivacy()
        {
            int requests = 0; bool stalePublished = false;
            var old = new TaskCompletionSource<LrcDocument>(); var fresh = new TaskCompletionSource<LrcDocument>();
            CancellationToken oldToken = default(CancellationToken);
            using (var monitor = new NowPlayingMonitor(false, () => Task.FromResult(Track("Restart online")), Library(), delegate(MediaSnapshot track, CancellationToken token)
            {
                int call = Interlocked.Increment(ref requests);
                if (call == 1) { oldToken = token; return old.Task; }
                return fresh.Task;
            }))
            {
                monitor.Changed += () => { if (monitor.Lyric == "Old generation") stalePublished = true; };
                try
                {
                    monitor.EnableOnline(true); monitor.Start(); PumpUntil(() => requests == 1, 1000);
                    monitor.Stop(); monitor.Start(); PumpUntil(() => requests == 2, 1000);
                    Check(oldToken.IsCancellationRequested, "Stopping cancels the first generation's online lookup");
                    old.SetResult(LrcDocument.Parse("[00:00]Old generation"));
                    fresh.SetResult(LrcDocument.Parse("[00:00]Fresh generation"));
                    PumpUntil(() => monitor.Lyric == "Fresh generation", 1000);
                    Check(!stalePublished, "Restarted monitor never publishes the old online generation");
                    monitor.Stop();
                }
                finally { old.TrySetResult(null); fresh.TrySetResult(null); monitor.Dispose(); }
            }
            int demoRequests = 0;
            using (var monitor = new NowPlayingMonitor(true, () => Task.FromResult(Track("Demo online")), Library(), delegate(MediaSnapshot track, CancellationToken token)
            { Interlocked.Increment(ref demoRequests); return Task.FromResult(LrcDocument.Parse("[00:00]Unexpected")); }))
            {
                monitor.EnableOnline(true); monitor.Start(); PumpUntil(() => monitor.Snapshot.Available, 1000);
                Check(demoRequests == 0 && monitor.Lyrics == null, "Demo fixtures cannot start an external lyrics lookup");
                monitor.Stop();
            }
        }

        private static void LocalLyricsWin(string path)
        {
            var library = Library(); var current = Track("Priority"); library.Import(current.Source, current.Title, current.Artist, path); int requests = 0;
            using (var monitor = new NowPlayingMonitor(false, () => Task.FromResult(current), library, delegate(MediaSnapshot track, CancellationToken token)
            { requests++; return Task.FromResult(LrcDocument.Parse("[00:00]Unwanted online")); }))
            {
                monitor.EnableOnline(true); monitor.Start(); PumpUntil(() => monitor.Lyrics != null, 1000);
                Check(monitor.Lyric == "Local A" && requests == 0, "Bound local lyrics take precedence and avoid an external request");
                var removed = monitor.RemoveLyricsAsync(current); PumpUntil(() => removed.IsCompleted, 1000); removed.GetAwaiter().GetResult();
                Check(monitor.Lyrics == null && requests == 0, "Explicit removal does not immediately refill lyrics from online");
                monitor.Refresh(); PumpUntil(() => !Field<bool>(monitor, "reading"), 1000);
                Check(requests == 0, "Polling after removal keeps that track's online lookup suppressed");
                current = Track("Priority"); current.Album = "Late album metadata"; current.Duration = TimeSpan.FromSeconds(231.586);
                monitor.Refresh(); PumpUntil(() => !Field<bool>(monitor, "reading"), 1000);
                Check(requests == 0 && monitor.Lyrics == null, "Metadata enrichment cannot undo explicit lyric removal");
                monitor.Stop();
            }
        }

        private static void LookupSignatureUsesCompletedMetadata()
        {
            var first = Track("  Caf\u00e9  "); first.Artist = "  Fixture Artist  "; first.Album = " Album "; first.Duration = TimeSpan.Zero;
            var equivalent = Track("CAFE\u0301"); equivalent.Artist = "FIXTURE ARTIST"; equivalent.Album = "ALBUM"; equivalent.Duration = TimeSpan.Zero;
            string initial = NowPlayingMonitor.OnlineLookupSignature(first);
            Check(initial.Length > 0 && initial == NowPlayingMonitor.OnlineLookupSignature(equivalent), "Query signatures normalize Unicode, edge whitespace and case");
            equivalent.Duration = TimeSpan.FromSeconds(231.586);
            string duration = NowPlayingMonitor.OnlineLookupSignature(equivalent);
            Check(initial != duration, "A previously missing timeline duration changes the actual lookup signature");
            equivalent.Album = "Different album";
            Check(duration != NowPlayingMonitor.OnlineLookupSignature(equivalent), "Album completion or correction changes the lookup signature");
            equivalent.Album = "ALBUM"; equivalent.Source = "Another.Player";
            Check(duration != NowPlayingMonitor.OnlineLookupSignature(equivalent), "Separate Windows source application IDs have separate lookup state");
            equivalent.Title = "\ud800";
            Check(NowPlayingMonitor.OnlineLookupSignature(equivalent) == "", "Malformed Unicode metadata cannot escape a media refresh");
            Check(NowPlayingMonitor.OnlineLookupSignature(new MediaSnapshot()) == "", "Unavailable sessions have no query signature");
        }

        private static void FailedOnlineLookupBacksOffAndRecovers()
        {
            DateTime now = new DateTime(2026, 10, 24, 0, 0, 0, DateTimeKind.Utc);
            int requests = 0;
            using (var monitor = new NowPlayingMonitor(false, () => Task.FromResult(Track("Retry fixture")), Library(),
                delegate(MediaSnapshot track, CancellationToken token)
                {
                    int call = Interlocked.Increment(ref requests);
                    if (call == 2) throw new IOException("Synthetic transient provider failure");
                    if (call == 3) return Task.FromResult(LrcDocument.Parse(""));
                    return Task.FromResult(call == 6 ? LrcDocument.Parse("[00:00]Recovered lyric") : null);
                }, null, () => now))
            {
                monitor.EnableOnline(true); monitor.Start();
                PumpUntil(() => requests == 1 && Field<CancellationTokenSource>(monitor, "onlineCancellation") == null, 1000);
                int[] delays = { 30, 60, 120, 300, 300 };
                for (int index = 0; index < delays.Length; index++)
                {
                    int expected = index + 1;
                    Check(Field<DateTime>(monitor, "nextOnlineAttemptUtc") == now.AddSeconds(delays[index]), "Null, thrown and empty results share the bounded retry schedule");
                    now = now.AddSeconds(delays[index] - 1);
                    monitor.Refresh(); PumpUntil(() => !Field<bool>(monitor, "reading"), 1000);
                    Check(requests == expected, "Repeated samples do not query before the retry deadline");
                    now = now.AddSeconds(1);
                    monitor.Refresh();
                    PumpUntil(() => requests == expected + 1 && Field<CancellationTokenSource>(monitor, "onlineCancellation") == null, 1000);
                }
                Check(monitor.Lyric == "Recovered lyric" && requests == 6, "The same song recovers after transient and negative lookups without changing tracks");
                now = now.AddDays(2);
                monitor.Refresh(); PumpUntil(() => !Field<bool>(monitor, "reading"), 1000);
                Check(requests == 6, "A successful signature is not repeatedly queried after its retry deadline");
                monitor.Stop();
            }
        }

        private static void MetadataCompletionReplacesPendingLookup()
        {
            DateTime now = new DateTime(2026, 10, 24, 0, 0, 0, DateTimeKind.Utc);
            MediaSnapshot current = Track("Metadata fixture"); current.Duration = TimeSpan.Zero;
            var incomplete = new TaskCompletionSource<LrcDocument>();
            var timed = new TaskCompletionSource<LrcDocument>();
            var complete = new TaskCompletionSource<LrcDocument>();
            var corrected = new TaskCompletionSource<LrcDocument>();
            int requests = 0; bool stalePublished = false, mixedMetadata = false;
            CancellationToken incompleteToken = default(CancellationToken);
            double queriedDuration = 0; string queriedAlbum = null;
            using (var monitor = new NowPlayingMonitor(false, () => Task.FromResult(current), Library(),
                delegate(MediaSnapshot track, CancellationToken token)
                {
                    int call = Interlocked.Increment(ref requests);
                    if (call == 1) { incompleteToken = token; return incomplete.Task; }
                    if (call == 2) { queriedDuration = track.Duration.TotalSeconds; return timed.Task; }
                    if (call == 3) { queriedAlbum = track.Album; return complete.Task; }
                    return corrected.Task;
                }, null, () => now))
            {
                monitor.Changed += delegate
                {
                    if (monitor.Lyric == "Stale incomplete lyric") stalePublished = true;
                    if (monitor.Snapshot.Album == "Corrected album" && monitor.Lyric == "Completed metadata lyric") mixedMetadata = true;
                };
                try
                {
                    monitor.EnableOnline(true); monitor.Start(); PumpUntil(() => requests == 1 && incompleteToken.CanBeCanceled, 1000);
                    now = now.AddDays(1);
                    for (int index = 0; index < 4; index++)
                    {
                        monitor.Refresh(); PumpUntil(() => !Field<bool>(monitor, "reading"), 1000);
                    }
                    Check(requests == 1, "Even a distant retry clock cannot overlap an in-flight query of the same signature");
                    current = Track("Metadata fixture"); current.Duration = TimeSpan.FromSeconds(231.586);
                    monitor.Refresh(); PumpUntil(() => requests == 2 && queriedDuration > 0, 1000);
                    Check(incompleteToken.IsCancellationRequested && Math.Abs(queriedDuration - 231.586) < .001, "A real duration immediately cancels the incomplete query and reaches the provider");
                    incomplete.SetResult(LrcDocument.Parse("[00:00]Stale incomplete lyric")); timed.SetResult(null);
                    PumpUntil(() => Field<CancellationTokenSource>(monitor, "onlineCancellation") == null, 1000);
                    current = Track("Metadata fixture"); current.Duration = TimeSpan.FromSeconds(231.586); current.Album = "Completed album";
                    monitor.Refresh(); PumpUntil(() => requests == 3 && queriedAlbum == "Completed album", 1000);
                    Check(queriedAlbum == "Completed album", "Album completion bypasses the old signature's failure backoff");
                    complete.SetResult(LrcDocument.Parse("[00:00]Completed metadata lyric"));
                    PumpUntil(() => monitor.Lyric == "Completed metadata lyric", 1000);
                    current = Track("Metadata fixture"); current.Duration = TimeSpan.FromSeconds(231.586); current.Album = "Corrected album";
                    monitor.Refresh(); PumpUntil(() => requests == 4, 1000);
                    Check(!stalePublished && !mixedMetadata && monitor.Lyrics == null, "Late incomplete results and old-release lyrics are never published with new lookup metadata");
                    corrected.SetResult(LrcDocument.Parse("[00:00]Corrected lyric"));
                    PumpUntil(() => monitor.Lyric == "Corrected lyric", 1000);
                    monitor.Stop();
                }
                finally
                {
                    incomplete.TrySetResult(null); timed.TrySetResult(null); complete.TrySetResult(null); corrected.TrySetResult(null);
                    monitor.Dispose();
                }
            }
        }

        private static void LyricsStatusAndExplicitRetry(string path)
        {
            DateTime now = new DateTime(2026, 10, 24, 0, 0, 0, DateTimeKind.Utc);
            MediaSnapshot current = Track("Status fixture"); current.Duration = TimeSpan.Zero; current.SampledAtUtc = new DateTime(1601, 1, 1);
            var missingTimeline = new TaskCompletionSource<LrcDocument>(); var knownTimeline = new TaskCompletionSource<LrcDocument>();
            int requests = 0; var statuses = new List<string>();
            using (var monitor = new NowPlayingMonitor(false, () => Task.FromResult(current), Library(),
                delegate(MediaSnapshot track, CancellationToken token)
                {
                    int call = Interlocked.Increment(ref requests);
                    if (call == 1) return missingTimeline.Task;
                    if (call == 2) return knownTimeline.Task;
                    return Task.FromResult(call == 3 ? null : LrcDocument.Parse("[00:00]Explicitly retried lyric"));
                }, null, () => now))
            {
                monitor.Changed += () => statuses.Add(monitor.LyricsStatus);
                try
                {
                    Check(monitor.LyricsStatus == "未播放媒体，等待歌词", "No session has an explicit lyric status");
                    monitor.Start(); PumpUntil(() => monitor.Snapshot.Available, 1000);
                    Check(requests == 0 && monitor.LyricsStatus == "在线歌词已关闭，可导入本地 LRC", "Offline status does not start an online request");
                    monitor.EnableOnline(true); PumpUntil(() => requests == 1, 1000);
                    Check(monitor.LyricsStatus == "正在匹配在线歌词" && statuses.Contains("正在匹配在线歌词"), "Starting a lookup publishes matching status");
                    missingTimeline.SetResult(LrcDocument.Parse("[00:10]Future first line"));
                    PumpUntil(() => monitor.Lyrics != null, 1000);
                    Check(monitor.LyricsStatus == "已加载歌词，但播放器未提供可用播放进度，无法同步", "A 1601 zero-duration timeline is reported without claiming synchronization");
                    current = Track("Status fixture");
                    monitor.Refresh(); PumpUntil(() => requests == 2, 1000);
                    knownTimeline.SetResult(LrcDocument.Parse("[00:10]Future first line"));
                    PumpUntil(() => monitor.Lyrics != null, 1000);
                    Check(monitor.LyricsStatus == "歌词已加载，尚未到首句", "A loaded lyric before its first timestamp has a distinct explanation");
                    var removed = monitor.RemoveLyricsAsync(current); PumpUntil(() => removed.IsCompleted, 1000); removed.GetAwaiter().GetResult();
                    Check(monitor.LyricsStatus == "当前歌曲歌词已清除，可重试在线匹配", "Explicit removal is visible rather than appearing as a failed match");
                    monitor.Refresh(); PumpUntil(() => !Field<bool>(monitor, "reading"), 1000);
                    Check(requests == 2, "Removal continues to suppress automatic requests");
                    monitor.RetryOnlineLyrics();
                    PumpUntil(() => requests == 3 && Field<CancellationTokenSource>(monitor, "onlineCancellation") == null, 1000);
                    Check(monitor.LyricsStatus == "未匹配到歌词或服务暂不可用，将稍后重试" && statuses.Contains(monitor.LyricsStatus), "An unsuccessful lookup publishes a truthful failure status");
                    monitor.RetryOnlineLyrics();
                    PumpUntil(() => monitor.Lyric == "Explicitly retried lyric", 1000);
                    Check(requests == 4 && monitor.LyricsStatus == "在线歌词已加载，按播放器进度显示", "Explicit retry bypasses both removal suppression and a failed query's backoff");
                    monitor.EnableOnline(false); monitor.RetryOnlineLyrics();
                    Check(requests == 4 && monitor.Lyrics == null, "Retry preserves the user's disabled online setting");
                    var imported = monitor.ImportLyricsAsync(current, path); PumpUntil(() => imported.IsCompleted, 1000); imported.GetAwaiter().GetResult();
                    monitor.RetryOnlineLyrics();
                    Check(requests == 4 && monitor.Lyric == "Local A" && monitor.LyricsStatus == "本地歌词已加载，按播放器进度显示", "Local imported lyrics retain priority through explicit online retry");
                    monitor.Stop();
                    Check(monitor.LyricsStatus == "已加载歌词，媒体监控已暂停" && statuses.Contains(monitor.LyricsStatus), "Stopping publishes the paused-monitor state");
                }
                finally { missingTimeline.TrySetResult(null); knownTimeline.TrySetResult(null); monitor.Dispose(); }
            }
            using (var monitor = new NowPlayingMonitor(false, () => Task.FromResult(Track("Plain lyric fixture")), Library(),
                (track, token) => Task.FromResult(LrcDocument.FromPlainText("First plain line\nSecond plain line"))))
            {
                monitor.EnableOnline(true); monitor.Start(); PumpUntil(() => monitor.Lyrics != null, 1000);
                Check(!monitor.Lyrics.IsSynchronized && monitor.LyricsStatus == "已加载纯文本歌词，点击查看完整歌词，无法逐句同步", "Plain-text fallback is readable without claiming line synchronization");
                monitor.Stop();
            }
        }

        private static void ImportDuringPendingRead(string original, string replacement)
        {
            var library = Library(); var track = Track("Import race"); library.Import(track.Source, track.Title, track.Artist, original);
            var pending = new TaskCompletionSource<MediaSnapshot>(); int reads = 0;
            using (var monitor = new NowPlayingMonitor(false, delegate { int call = Interlocked.Increment(ref reads); return call == 2 ? pending.Task : Task.FromResult(track); }, library))
            {
                try
                {
                    monitor.Start(); PumpUntil(() => monitor.Lyrics != null, 1000); monitor.Refresh(); PumpUntil(() => reads == 2, 1000);
                    Task imported = monitor.ImportLyricsAsync(track, replacement); PumpUntil(() => imported.IsCompleted, 1000); imported.GetAwaiter().GetResult();
                    Check(monitor.Lyric == "Imported A", "Import updates the active track while a media read is pending");
                    pending.SetResult(track); PumpUntil(() => reads >= 3 && !Field<bool>(monitor, "reading"), 1000);
                    Check(monitor.Lyric == "Imported A", "Completing an older media read cannot undo imported lyrics");
                }
                finally { pending.TrySetResult(track); monitor.Stop(); }
            }
        }

        private static void ImportDuringTrackChange(string original, string replacement)
        {
            var library = Library(); var first = Track("A"); var next = Track("B"); library.Import(first.Source, first.Title, first.Artist, original);
            var pending = new TaskCompletionSource<MediaSnapshot>(); int reads = 0; bool wrongTrackLyrics = false;
            using (var monitor = new NowPlayingMonitor(false, delegate { int call = Interlocked.Increment(ref reads); return call == 1 ? Task.FromResult(first) : call == 2 ? pending.Task : Task.FromResult(next); }, library))
            {
                monitor.Changed += () => { if (monitor.Snapshot.Title == "B" && monitor.Lyrics != null) wrongTrackLyrics = true; };
                try
                {
                    monitor.Start(); PumpUntil(() => monitor.Lyrics != null, 1000); monitor.Refresh(); PumpUntil(() => reads == 2, 1000);
                    var imported = monitor.ImportLyricsAsync(first, replacement); PumpUntil(() => imported.IsCompleted, 1000); imported.GetAwaiter().GetResult();
                    pending.SetResult(next); PumpUntil(() => monitor.Snapshot.Title == "B" && !Field<bool>(monitor, "reading"), 1000);
                    Check(!wrongTrackLyrics && monitor.Lyrics == null && reads >= 3, "Import during a pending track change never publishes new metadata with old lyrics");
                }
                finally { pending.TrySetResult(next); monitor.Stop(); }
            }
        }

        private static void ImportForUpcomingTrack(string replacement)
        {
            var library = Library(); var first = Track("Current"); var next = Track("Upcoming"); var pending = new TaskCompletionSource<MediaSnapshot>(); int reads = 0;
            using (var monitor = new NowPlayingMonitor(false, delegate { int call = Interlocked.Increment(ref reads); return call == 1 ? Task.FromResult(first) : call == 2 ? pending.Task : Task.FromResult(next); }, library))
            {
                try
                {
                    monitor.Start(); PumpUntil(() => monitor.Snapshot.Title == "Current", 1000); monitor.Refresh(); PumpUntil(() => reads == 2, 1000);
                    Task imported = monitor.ImportLyricsAsync(next, replacement); PumpUntil(() => imported.IsCompleted, 1000); imported.GetAwaiter().GetResult();
                    Check(monitor.Snapshot.Title == "Current" && monitor.Lyrics == null, "Importing for another track never replaces the current track's lyric");
                    pending.SetResult(next); PumpUntil(() => monitor.Snapshot.Title == "Upcoming" && monitor.Lyrics != null, 1000);
                    Check(monitor.Lyric == "Imported A", "An upcoming track sees its newly imported document");
                }
                finally { pending.TrySetResult(next); monitor.Stop(); }
            }
        }

        private static T Field<T>(object value, string name)
        { return (T)value.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(value); }

        private static void PumpUntil(Func<bool> completed, int milliseconds)
        {
            if (completed()) return;
            var frame = new DispatcherFrame(); var watch = Stopwatch.StartNew();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(5) };
            timer.Tick += delegate { if (completed() || watch.ElapsedMilliseconds >= milliseconds) frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
            Check(completed(), "Dispatcher fixture timed out");
        }

        private static void Check(bool value, string message) { if (!value) throw new Exception("Now-playing monitor: " + message); }
    }
}
