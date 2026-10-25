using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexIsland
{
    internal static class OnlineMediaLyricsTests
    {
        internal static void Run()
        {
            ExactMatchingAndQueries();
            PlainLyricsAndMissingTimeline();
            MissingInvalidAndBounded();
            CacheCapacityAndCoalescing();
            RetryExpiredMisses();
            CancellationAndBackoff();
            Console.WriteLine("PASS: offline online-lyrics endpoint/encoding, exact metadata/duration, bounded input, miss/LRU cache, concurrent lookup, cancellation and 429 backoff.");
        }

        private static void ExactMatchingAndQueries()
        {
            var track = Track(1); Uri requested = null;
            var provider = new OnlineMediaLyrics((uri, cancellation) => { requested = uri; return Task.FromResult(Response(track)); });
            var lyrics = provider.FindAsync(track, CancellationToken.None).GetAwaiter().GetResult();
            Check(lyrics != null && lyrics.Count == 2 && lyrics.LineAt(TimeSpan.FromSeconds(2)) == "first synthetic line", "Exact metadata produces synchronized lyrics");
            Check(requested.Scheme == "https" && requested.Host == "lrclib.net" && requested.AbsolutePath == "/api/get", "Online lookup has one fixed HTTPS endpoint");
            Check(requested.OriginalString.Contains("track_name=" + Uri.EscapeDataString(track.Title)) && requested.OriginalString.Contains("artist_name=" + Uri.EscapeDataString(track.Artist)), "Unicode and query punctuation are encoded as metadata values");
            Check(requested.Query.Contains("duration=181.2") && requested.OriginalString.Contains("album_name=" + Uri.EscapeDataString(track.Album)), "Known album and invariant seconds are included");
            var withoutAlbum = Track(2); withoutAlbum.Album = null;
            var noAlbumProvider = new OnlineMediaLyrics((uri, cancellation) => { requested = uri; return Task.FromResult(Response(withoutAlbum)); });
            Check(noAlbumProvider.FindAsync(withoutAlbum, CancellationToken.None).GetAwaiter().GetResult() != null && !requested.Query.Contains("album_name"), "Missing album is omitted without preventing an exact title/artist match");

            RejectRecord(track, record => record["trackName"] = "different song", "Unrelated title is rejected");
            RejectRecord(track, record => record["artistName"] = "different artist", "Unrelated artist is rejected");
            RejectRecord(track, record => record["albumName"] = "different release", "Wrong known album is rejected");
            RejectRecord(track, record => record["duration"] = track.Duration.TotalSeconds + 2.01, "A different-duration recording is rejected");
            RejectRecord(track, record => record["duration"] = "181.2", "String duration cannot bypass numeric validation");
            RejectRecord(track, record => record["instrumental"] = true, "Instrumental records do not invent lyrics");
            RejectRecord(track, record => { record["syncedLyrics"] = null; record["plainLyrics"] = null; }, "A record without lyric content is rejected");
            RejectRecord(track, record => { record["syncedLyrics"] = "plain text without timestamps"; record["plainLyrics"] = null; }, "Untimed content in the synchronized field is rejected");
            var boundary = Record(track); boundary["duration"] = track.Duration.TotalSeconds + 2;
            var boundaryProvider = new OnlineMediaLyrics((uri, cancellation) => Task.FromResult(new OnlineLyricsResponse { Body = Json(boundary) }));
            Check(boundaryProvider.FindAsync(track, CancellationToken.None).GetAwaiter().GetResult() != null, "Official ±2-second matching boundary is accepted");
        }

        private static void PlainLyricsAndMissingTimeline()
        {
            var track = Track(31); var record = Record(track);
            string original = "保存设置\nSecond synthetic line 😀";
            record["syncedLyrics"] = null; record["plainLyrics"] = original;
            var plainProvider = new OnlineMediaLyrics((uri, cancellation) => Task.FromResult(new OnlineLyricsResponse { Body = Json(record) }));
            var plain = plainProvider.FindAsync(track, CancellationToken.None).GetAwaiter().GetResult();
            Check(plain != null && !plain.IsSynchronized && plain.LineAt(TimeSpan.Zero) == original, "Plain lyrics preserve exact text and are never labeled synchronized");
            var unknown = Track(32); unknown.Duration = TimeSpan.Zero; unknown.Album = ""; Uri requested = null;
            var unknownRecord = Record(unknown); unknownRecord["duration"] = 182.0;
            var provider = new OnlineMediaLyrics((uri, cancellation) => { requested = uri; return Task.FromResult(new OnlineLyricsResponse { Body = Json(unknownRecord) }); });
            var document = provider.FindAsync(unknown, CancellationToken.None).GetAwaiter().GetResult();
            Check(document != null && document.IsSynchronized && !unknown.HasUsableTimeline && !requested.Query.Contains("duration=") && !requested.Query.Contains("album_name="), "Missing duration uses exact title/artist lookup without inventing a usable player timeline");
            int calls = 0; var retry = new OnlineMediaLyrics((uri, cancellation) => { calls++; return Task.FromResult(new OnlineLyricsResponse { StatusCode = 404 }); });
            retry.FindAsync(track, CancellationToken.None).GetAwaiter().GetResult(); retry.FindAsync(track, CancellationToken.None).GetAwaiter().GetResult();
            Check(calls == 1, "A miss is cached until explicit retry or expiry");
            retry.Invalidate(track); retry.FindAsync(track, CancellationToken.None).GetAwaiter().GetResult();
            Check(calls == 2, "Explicit retry invalidates the completed current-song miss");
            var pending = new TaskCompletionSource<OnlineLyricsResponse>(); int pendingCalls = 0;
            var coalesced = new OnlineMediaLyrics((uri, cancellation) => { Interlocked.Increment(ref pendingCalls); return pending.Task; });
            var first = coalesced.FindAsync(track, CancellationToken.None);
            Check(SpinWait.SpinUntil(() => pendingCalls == 1, 1000), "Pending lookup begins");
            coalesced.Invalidate(track); var second = coalesced.FindAsync(track, CancellationToken.None);
            pending.SetResult(Response(track)); Task.WaitAll(first, second);
            Check(pendingCalls == 1 && ReferenceEquals(first.Result, second.Result), "Invalidating a pending lookup does not duplicate network requests");
        }

        private static void MissingInvalidAndBounded()
        {
            int calls = 0;
            var provider = new OnlineMediaLyrics((uri, cancellation) => { calls++; return Task.FromResult(new OnlineLyricsResponse { StatusCode = 404 }); });
            var invalid = new List<MediaSnapshot> { null, new MediaSnapshot(), Track(1), Track(2), Track(3), Track(4), Track(5), Track(6) };
            invalid[2].Title = ""; invalid[3].Artist = "未提供作者"; invalid[4].Duration = TimeSpan.FromSeconds(-1);
            invalid[5].Duration = TimeSpan.FromHours(2); invalid[6].Title = new string('x', 513);
            invalid[7].Title = "broken surrogate " + new string('\uD800', 1);
            foreach (var track in invalid) Check(provider.FindAsync(track, CancellationToken.None).GetAwaiter().GetResult() == null, "Incomplete track does not request lyrics");
            Check(calls == 0, "Incomplete metadata never leaves the app");
            var valid = Track(10);
            var bad = new[] {
                new OnlineLyricsResponse { Body = "{" },
                new OnlineLyricsResponse { Body = "[]" },
                new OnlineLyricsResponse { Body = Json(Record(valid)), ContentType = "text/html" },
                new OnlineLyricsResponse { Body = Json(Record(valid)), ContentType = "application/json-fake" },
                new OnlineLyricsResponse { Body = Json(Record(valid)), StatusCode = 302 },
                new OnlineLyricsResponse { Body = new string('中', OnlineMediaLyrics.MaximumResponseBytes / 3 + 1) }
            };
            foreach (var response in bad)
            {
                var selected = response;
                var local = new OnlineMediaLyrics((uri, cancellation) => Task.FromResult(selected));
                Check(local.FindAsync(valid, CancellationToken.None).GetAwaiter().GetResult() == null, "Malformed, redirected, non-JSON or oversized response is isolated");
            }
            Check(provider.FindAsync(valid, CancellationToken.None).GetAwaiter().GetResult() == null, "Missing song returns no lyrics");
            provider.FindAsync(valid, CancellationToken.None).GetAwaiter().GetResult();
            Check(calls == 1, "A missing song is cached and not retried on every sample");
        }

        private static void CacheCapacityAndCoalescing()
        {
            int ticks = 0, loads = 0;
            var origin = new DateTime(2026, 10, 23, 2, 0, 0, DateTimeKind.Utc);
            var provider = new OnlineMediaLyrics((uri, cancellation) => { loads++; return Task.FromResult(new OnlineLyricsResponse { StatusCode = 404 }); }, () => origin.AddSeconds(Interlocked.Increment(ref ticks)));
            for (int index = 0; index < 70; index++) provider.FindAsync(Track(index), CancellationToken.None).GetAwaiter().GetResult();
            Check(loads == 70 && provider.CachedCount == OnlineMediaLyrics.MaximumCacheEntries, "Lyrics successes and misses have a bounded 64-entry memory cache");
            provider.FindAsync(Track(69), CancellationToken.None).GetAwaiter().GetResult();
            Check(loads == 70, "Recent entries remain cached");
            provider.FindAsync(Track(0), CancellationToken.None).GetAwaiter().GetResult();
            Check(loads == 71 && provider.CachedCount == 64, "Old entries are evicted without growing memory");

            var track = Track(500); var release = new TaskCompletionSource<OnlineLyricsResponse>(); int requests = 0;
            var concurrent = new OnlineMediaLyrics((uri, cancellation) => { Interlocked.Increment(ref requests); return release.Task; });
            var tasks = new Task<LrcDocument>[8];
            for (int index = 0; index < tasks.Length; index++) tasks[index] = concurrent.FindAsync(track, CancellationToken.None);
            Check(SpinWait.SpinUntil(() => requests == 1, 1000), "Concurrent lyrics request starts");
            using (var stopped = new CancellationTokenSource())
            {
                var canceledWaiter = concurrent.FindAsync(track, stopped.Token); stopped.Cancel();
                Check(canceledWaiter.GetAwaiter().GetResult() == null, "A secondary caller can cancel its own wait");
            }
            release.SetResult(Response(track));
            foreach (var task in tasks) Check(task.GetAwaiter().GetResult() != null, "Other callers retain their shared result");
            Check(requests == 1, "Concurrent callers issue one lookup per track");
        }

        private static void CancellationAndBackoff()
        {
            var track = Track(1); int requests = 0;
            var cancelable = new OnlineMediaLyrics(async (uri, token) => {
                if (Interlocked.Increment(ref requests) == 1) await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                return Response(track);
            });
            using (var cancellation = new CancellationTokenSource())
            {
                var task = cancelable.FindAsync(track, cancellation.Token);
                Check(SpinWait.SpinUntil(() => requests == 1, 1000), "Synthetic in-flight online request starts");
                cancellation.Cancel();
                Check(task.GetAwaiter().GetResult() == null && SpinWait.SpinUntil(() => cancelable.CachedCount == 0, 1000), "Cancellation aborts lookup and does not permanently cache a miss");
            }
            Check(cancelable.FindAsync(track, CancellationToken.None).GetAwaiter().GetResult() != null && requests == 2, "Re-enabling lookup can retry a canceled track");
            using (var canceled = new CancellationTokenSource())
            { canceled.Cancel(); Check(cancelable.FindAsync(Track(2), canceled.Token).GetAwaiter().GetResult() == null && requests == 2, "Pre-canceled lookup sends no metadata"); }

            var now = new DateTime(2026, 10, 23, 3, 0, 0, DateTimeKind.Utc); int limited = 0;
            var backoff = new OnlineMediaLyrics((uri, token) => { limited++; return Task.FromResult(new OnlineLyricsResponse { StatusCode = 429, RetryAfterSeconds = 1 }); }, () => now);
            backoff.FindAsync(Track(10), CancellationToken.None).GetAwaiter().GetResult();
            now = now.AddSeconds(59); backoff.FindAsync(Track(11), CancellationToken.None).GetAwaiter().GetResult();
            Check(limited == 1, "429 applies at least 60 seconds of backoff across tracks");
            now = now.AddSeconds(2); backoff.FindAsync(Track(11), CancellationToken.None).GetAwaiter().GetResult();
            Check(limited == 2, "Lookup resumes after the backoff interval");
            int longer = 0;
            var longerBackoff = new OnlineMediaLyrics((uri, token) => { longer++; return Task.FromResult(new OnlineLyricsResponse { StatusCode = 429, RetryAfterSeconds = 120 }); }, () => now);
            longerBackoff.FindAsync(Track(20), CancellationToken.None).GetAwaiter().GetResult();
            now = now.AddSeconds(61); longerBackoff.FindAsync(Track(21), CancellationToken.None).GetAwaiter().GetResult();
            Check(longer == 1, "A server retry interval longer than one minute is honored");
            now = now.AddSeconds(60); longerBackoff.FindAsync(Track(21), CancellationToken.None).GetAwaiter().GetResult();
            Check(longer == 2, "Long server backoff eventually expires");
            Check(OnlineMediaLyrics.RetryDelay("120", now) == 120 && OnlineMediaLyrics.RetryDelay("-5", now) == 0 && OnlineMediaLyrics.RetryDelay("invalid", now) == 0, "Retry-After seconds are bounded and validated");
            Check(OnlineMediaLyrics.RetryDelay(now.AddSeconds(75).ToString("r", CultureInfo.InvariantCulture), now) == 75, "Retry-After HTTP dates are honored");
        }

        private static void RetryExpiredMisses()
        {
            var now = new DateTime(2026, 10, 23, 4, 0, 0, DateTimeKind.Utc);
            var track = Track(400); int misses = 0;
            var missing = new OnlineMediaLyrics((uri, token) => { misses++; return Task.FromResult(new OnlineLyricsResponse { StatusCode = 404 }); }, () => now);
            missing.FindAsync(track, CancellationToken.None).GetAwaiter().GetResult();
            now = now.AddSeconds(299); missing.FindAsync(track, CancellationToken.None).GetAwaiter().GetResult();
            Check(misses == 1, "Missing lyrics are cached for five minutes");
            now = now.AddSeconds(2); missing.FindAsync(track, CancellationToken.None).GetAwaiter().GetResult();
            Check(misses == 2, "A previously missing song can be found on a later revisit");
            int failures = 0;
            var offline = new OnlineMediaLyrics((uri, token) => {
                if (++failures == 1) throw new InvalidOperationException("synthetic offline connection");
                return Task.FromResult(Response(track));
            }, () => now);
            Check(offline.FindAsync(track, CancellationToken.None).GetAwaiter().GetResult() == null, "Temporary network failure produces no lyric");
            now = now.AddSeconds(29); offline.FindAsync(track, CancellationToken.None).GetAwaiter().GetResult();
            Check(failures == 1, "Temporary failures do not cause a polling request loop");
            now = now.AddSeconds(2);
            Check(offline.FindAsync(track, CancellationToken.None).GetAwaiter().GetResult() != null && failures == 2, "An offline lookup can recover after thirty seconds");
        }

        private static void RejectRecord(MediaSnapshot track, Action<Dictionary<string, object>> mutate, string message)
        {
            var record = Record(track); mutate(record);
            var provider = new OnlineMediaLyrics((uri, token) => Task.FromResult(new OnlineLyricsResponse { Body = Json(record) }));
            Check(provider.FindAsync(track, CancellationToken.None).GetAwaiter().GetResult() == null, message);
        }
        private static MediaSnapshot Track(int index)
        { return new MediaSnapshot { Available = true, Title = "Test & 标题 #" + index, Artist = "Test / Artist", Album = "Demo ? Album", Duration = TimeSpan.FromSeconds(181.2), Source = "synthetic player" }; }
        private static Dictionary<string, object> Record(MediaSnapshot track)
        {
            return new Dictionary<string, object> {
                { "trackName", track.Title }, { "artistName", track.Artist }, { "albumName", track.Album ?? "" },
                { "duration", track.Duration.TotalSeconds }, { "instrumental", false },
                { "plainLyrics", "unsynchronized synthetic text" }, { "syncedLyrics", "[00:01.00]first synthetic line\n[00:02.50]second synthetic line" }
            };
        }
        private static OnlineLyricsResponse Response(MediaSnapshot track) { return new OnlineLyricsResponse { Body = Json(Record(track)) }; }
        private static string Json(object value) { return new JavaScriptSerializer().Serialize(value); }
        private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    }
}
