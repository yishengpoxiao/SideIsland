using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexIsland
{
    internal sealed class OnlineLyricsResponse
    {
        public int StatusCode = 200;
        public string ContentType = "application/json", Body;
        public int RetryAfterSeconds;
    }

    // Invoked only after the user enables online lookup. Requests contain track
    // metadata, never media files, account credentials or a player's private data.
    // API contract: https://lrclib.net/docs (/api/get, exact supplied metadata,
    // ±2 s when duration is known). Missing player timelines never imply sync.
    internal sealed class OnlineMediaLyrics
    {
        internal const int MaximumResponseBytes = 1024 * 1024;
        internal const int MaximumCacheEntries = 64;
        private readonly object gate = new object();
        private readonly Dictionary<string, Entry> cache = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly LinkedList<string> order = new LinkedList<string>();
        private readonly SemaphoreSlim requests = new SemaphoreSlim(1, 1);
        private readonly Func<Uri, CancellationToken, Task<OnlineLyricsResponse>> fetch;
        private readonly Func<DateTime> clock;
        private DateTime blockedUntilUtc, nextRequestUtc;

        private sealed class Entry
        {
            internal Task<LrcDocument> Task;
            internal LinkedListNode<string> Node;
            internal bool Completed;
            internal DateTime ExpiresUtc;
        }
        private sealed class Signature
        {
            internal string Title, Artist, Album, Key;
            internal double Duration;
            internal Uri Uri;
        }

        internal OnlineMediaLyrics() : this(FetchAsync, () => DateTime.UtcNow) { }
        internal OnlineMediaLyrics(Func<Uri, CancellationToken, Task<OnlineLyricsResponse>> reader, Func<DateTime> utcNow = null)
        { if (reader == null) throw new ArgumentNullException("reader"); fetch = reader; clock = utcNow ?? (() => DateTime.UtcNow); }

        internal int CachedCount { get { lock (gate) return cache.Count; } }

        internal void Invalidate(MediaSnapshot track)
        {
            Signature signature;
            try { if (!TrySignature(track, out signature)) return; }
            catch (ArgumentException) { return; }
            lock (gate)
            {
                Entry entry;
                if (cache.TryGetValue(signature.Key, out entry) && entry.Completed)
                { cache.Remove(signature.Key); order.Remove(entry.Node); }
            }
        }

        internal Task<LrcDocument> FindAsync(MediaSnapshot track, CancellationToken cancellation)
        {
            Signature signature;
            try { if (cancellation.IsCancellationRequested || !TrySignature(track, out signature)) return Task.FromResult<LrcDocument>(null); }
            catch (ArgumentException) { return Task.FromResult<LrcDocument>(null); }
            lock (gate)
            {
                Entry entry;
                if (cache.TryGetValue(signature.Key, out entry))
                {
                    if (!entry.Completed || clock() < entry.ExpiresUtc)
                    {
                        order.Remove(entry.Node); order.AddLast(entry.Node);
                        return WaitForCaller(entry.Task, cancellation);
                    }
                    cache.Remove(signature.Key); order.Remove(entry.Node);
                }
                if (clock() < blockedUntilUtc) return Task.FromResult<LrcDocument>(null);
                if (cache.Count >= MaximumCacheEntries)
                {
                    var victim = order.First;
                    while (victim != null && !cache[victim.Value].Completed) victim = victim.Next;
                    // Never evict in-flight work and issue a duplicate request.
                    if (victim == null) return Task.FromResult<LrcDocument>(null);
                    cache.Remove(victim.Value); order.Remove(victim);
                }
                entry = new Entry { Node = order.AddLast(signature.Key) };
                cache.Add(signature.Key, entry);
                entry.Task = Task.Run(() => LookupAsync(signature, entry, cancellation));
                return WaitForCaller(entry.Task, cancellation);
            }
        }

        private async Task<LrcDocument> LookupAsync(Signature signature, Entry entry, CancellationToken cancellation)
        {
            bool acquired = false, canceled = false;
            double cacheSeconds = 300;
            try
            {
                await requests.WaitAsync(cancellation).ConfigureAwait(false); acquired = true;
                DateTime now = clock(), next;
                lock (gate)
                {
                    if (now < blockedUntilUtc) return null;
                    next = nextRequestUtc;
                }
                double delay = (next - now).TotalMilliseconds;
                if (delay > 0) await Task.Delay((int)Math.Min(500, Math.Ceiling(delay)), cancellation).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                var response = await fetch(signature.Uri, cancellation).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                if (response == null) { cacheSeconds = 30; return null; }
                if (response.StatusCode == 429)
                {
                    cacheSeconds = Math.Max(60, response.RetryAfterSeconds);
                    lock (gate) blockedUntilUtc = SafeAddSeconds(clock(), cacheSeconds);
                    return null;
                }
                if (response.StatusCode >= 500) cacheSeconds = 30;
                var document = ParseMatch(signature, response);
                if (document != null) cacheSeconds = Double.PositiveInfinity;
                return document;
            }
            catch (OperationCanceledException) { canceled = true; return null; }
            catch (Exception) { cacheSeconds = 30; return null; }
            finally
            {
                if (acquired)
                {
                    lock (gate) nextRequestUtc = SafeAddSeconds(clock(), 0.25);
                    requests.Release();
                }
                lock (gate)
                {
                    entry.Completed = true;
                    entry.ExpiresUtc = SafeAddSeconds(clock(), cacheSeconds);
                    if (canceled)
                    {
                        Entry current;
                        if (cache.TryGetValue(signature.Key, out current) && ReferenceEquals(current, entry))
                        { cache.Remove(signature.Key); order.Remove(entry.Node); }
                    }
                }
            }
        }

        private static async Task<LrcDocument> WaitForCaller(Task<LrcDocument> shared, CancellationToken cancellation)
        {
            if (!cancellation.CanBeCanceled) return await shared.ConfigureAwait(false);
            var stopped = new TaskCompletionSource<bool>();
            using (cancellation.Register(() => stopped.TrySetResult(true)))
            {
                var completed = await Task.WhenAny(shared, stopped.Task).ConfigureAwait(false);
                return completed == shared && !cancellation.IsCancellationRequested ? await shared.ConfigureAwait(false) : null;
            }
        }

        private static bool TrySignature(MediaSnapshot track, out Signature value)
        {
            value = null;
            if (track == null || !track.Available) return false;
            string title = Normalize(track.Title), artist = Normalize(track.Artist), album = Normalize(track.Album);
            double duration = track.Duration.TotalSeconds;
            if (title.Length == 0 || artist.Length == 0 || title.Length > 512 || artist.Length > 512 || album.Length > 512 || duration < 0 || duration > 3600) return false;
            if (title == "未提供媒体标题" || title == "暂无正在播放的媒体" || artist == "未提供作者" || artist == "打开支持 Windows 媒体控制的音乐或视频应用") return false;
            string seconds = duration.ToString("0.###", CultureInfo.InvariantCulture);
            string query = "https://lrclib.net/api/get?track_name=" + Uri.EscapeDataString(title) + "&artist_name=" + Uri.EscapeDataString(artist);
            if (album.Length > 0) query += "&album_name=" + Uri.EscapeDataString(album);
            if (duration > 0) query += "&duration=" + seconds;
            value = new Signature { Title = title, Artist = artist, Album = album, Duration = duration, Uri = new Uri(query),
                Key = Part(title.ToUpperInvariant()) + Part(artist.ToUpperInvariant()) + Part(album.ToUpperInvariant()) + seconds };
            return true;
        }

        private static string Part(string text) { return text.Length.ToString(CultureInfo.InvariantCulture) + ":" + text; }
        private static string Normalize(string text) { return (text ?? "").Trim().Normalize(NormalizationForm.FormC); }
        private static bool Same(string left, string right) { return String.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase); }

        private static LrcDocument ParseMatch(Signature signature, OnlineLyricsResponse response)
        {
            if (response.StatusCode != 200 || !IsJson(response.ContentType) || String.IsNullOrWhiteSpace(response.Body) ||
                response.Body.Length > MaximumResponseBytes || Encoding.UTF8.GetByteCount(response.Body) > MaximumResponseBytes) return null;
            try
            {
                var serializer = new JavaScriptSerializer { MaxJsonLength = MaximumResponseBytes, RecursionLimit = 16 };
                var record = serializer.DeserializeObject(response.Body) as Dictionary<string, object>;
                object instrumental, rawDuration;
                if (record == null || !record.TryGetValue("instrumental", out instrumental) || !(instrumental is bool) || (bool)instrumental) return null;
                if (!Same(GetString(record, "trackName"), signature.Title) || !Same(GetString(record, "artistName"), signature.Artist)) return null;
                if (signature.Album.Length > 0 && !Same(GetString(record, "albumName"), signature.Album)) return null;
                if (!record.TryGetValue("duration", out rawDuration) || !(rawDuration is int || rawDuration is long || rawDuration is decimal || rawDuration is double)) return null;
                double duration = Convert.ToDouble(rawDuration, CultureInfo.InvariantCulture);
                if (Double.IsNaN(duration) || Double.IsInfinity(duration) || duration < 1 || duration > 3600 || (signature.Duration > 0 && Math.Abs(duration - signature.Duration) > 2)) return null;
                string lrc = GetString(record, "syncedLyrics");
                if (!String.IsNullOrWhiteSpace(lrc))
                {
                    try { var document = LrcDocument.Parse(lrc); if (document.Count > 0) return document; }
                    catch (InvalidDataException) { }
                }
                return LrcDocument.FromPlainText(GetString(record, "plainLyrics"));
            }
            catch (Exception) { return null; }
        }

        private static string GetString(Dictionary<string, object> record, string key)
        { object value; return record.TryGetValue(key, out value) ? value as string : null; }
        private static bool IsJson(string contentType)
        { return !String.IsNullOrWhiteSpace(contentType) && String.Equals(contentType.Split(';')[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase); }
        private static DateTime SafeAddSeconds(DateTime utc, double seconds)
        { return seconds >= (DateTime.MaxValue - utc).TotalSeconds ? DateTime.MaxValue : utc.AddSeconds(seconds); }

        private static async Task<OnlineLyricsResponse> FetchAsync(Uri uri, CancellationToken cancellation)
        {
            // Fixed endpoint and disabled redirects keep track metadata on this host.
            if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != "lrclib.net" || uri.AbsolutePath != "/api/get") return null;
            var request = (HttpWebRequest)WebRequest.Create(uri);
            request.Method = "GET"; request.AllowAutoRedirect = false;
            request.UserAgent = "SideIsland/1.12 (Windows desktop lyrics display)";
            request.Accept = "application/json"; request.Timeout = 5000; request.ReadWriteTimeout = 5000;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            request.UseDefaultCredentials = false;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                timeout.CancelAfter(5000);
                using (timeout.Token.Register(() => { try { request.Abort(); } catch (Exception) { } }))
                {
                    HttpWebResponse response;
                    try { response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false); }
                    catch (WebException error)
                    {
                        timeout.Token.ThrowIfCancellationRequested();
                        response = error.Response as HttpWebResponse;
                        if (response == null) throw;
                    }
                    using (response)
                    {
                        var result = new OnlineLyricsResponse { StatusCode = (int)response.StatusCode, ContentType = response.ContentType,
                            RetryAfterSeconds = RetryDelay(response.Headers["Retry-After"], DateTime.UtcNow) };
                        if (result.StatusCode != 200 || !IsJson(result.ContentType) || response.ContentLength > MaximumResponseBytes) return result;
                        using (var stream = response.GetResponseStream())
                        using (var body = new MemoryStream())
                        {
                            var buffer = new byte[8192];
                            while (true)
                            {
                                int read = await stream.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false);
                                if (read == 0) break;
                                if (body.Length + read > MaximumResponseBytes) return result;
                                body.Write(buffer, 0, read);
                            }
                            timeout.Token.ThrowIfCancellationRequested();
                            result.Body = new UTF8Encoding(false, true).GetString(body.ToArray());
                        }
                        return result;
                    }
                }
            }
        }

        internal static int RetryDelay(string header, DateTime utcNow)
        {
            int seconds;
            if (Int32.TryParse(header, NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds)) return Math.Max(0, seconds);
            DateTimeOffset deadline;
            if (!DateTimeOffset.TryParse(header, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out deadline)) return 0;
            return (int)Math.Max(0, Math.Min(Int32.MaxValue, Math.Ceiling((deadline.UtcDateTime - utcNow).TotalSeconds)));
        }
    }
}
