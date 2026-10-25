using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexIsland
{
    internal static class MediaArtworkTests
    {
        internal static void Run()
        {
            var wide = Task.Run(() => MediaArtwork.Decode(ImageBytes(512, 256))).GetAwaiter().GetResult();
            Check(wide != null && wide.IsFrozen && wide.PixelWidth == 256 && wide.PixelHeight == 128, "Landscape artwork decodes off Dispatcher and remains safe across threads");
            var tall = MediaArtwork.Decode(ImageBytes(100, 400));
            Check(tall != null && tall.PixelWidth == 64 && tall.PixelHeight == 256, "Portrait artwork retains aspect ratio within 256 px");
            var small = MediaArtwork.Decode(ImageBytes(16, 8));
            Check(small != null && small.PixelWidth == 16 && small.PixelHeight == 8, "Small artwork is not enlarged");
            Check(MediaArtwork.Decode(null) == null && MediaArtwork.Decode(new byte[0]) == null, "Missing artwork is harmless");
            Check(MediaArtwork.Decode(new byte[] { 1, 2, 3 }) == null, "Invalid artwork does not break valid media metadata");
            Check(MediaArtwork.Decode(new byte[MediaArtwork.MaxEncodedBytes + 1]) == null, "Oversized artwork is rejected before decoder work");
            CacheAndRaces(wide, tall);
            DelayedThumbnail(wide, tall);
            TimelineInterpolation();
            Console.WriteLine("PASS: bounded frozen media artwork, portrait/landscape sizing, cache refresh/failure/races and playback timeline interpolation.");
        }

        private static void DelayedThumbnail(BitmapSource oldCover, BitmapSource newCover)
        {
            var cache = new MediaArtworkCache(); var now = new DateTime(2026, 10, 24, 1, 0, 0, DateTimeKind.Utc); int loads = 0;
            Func<Task<BitmapSource>> ready = delegate { Interlocked.Increment(ref loads); return Task.FromResult(newCover); };
            cache.ReadAsync("愿与愁", now, true, () => Task.FromResult(oldCover)).GetAwaiter().GetResult();
            Check(cache.ReadAsync("玫瑰少年", now.AddMilliseconds(700), false, ready).GetAwaiter().GetResult() == null && loads == 0,
                "A new song without a Thumbnail clears the previous song's cover without invoking the loader");
            Check(cache.ReadAsync("玫瑰少年", now.AddMilliseconds(1200), false, ready).GetAwaiter().GetResult() == null && loads == 0,
                "Repeated not-ready metadata creates no negative cache or decode work");
            Check(Object.ReferenceEquals(cache.ReadAsync("玫瑰少年", now.AddMilliseconds(1800), true, ready).GetAwaiter().GetResult(), newCover) && loads == 1,
                "QQ's delayed Thumbnail is read immediately when it appears instead of waiting ten seconds");
            Check(Object.ReferenceEquals(cache.ReadAsync("玫瑰少年", now.AddMilliseconds(3000), true, ready).GetAwaiter().GetResult(), newCover) && loads == 1,
                "A ready cover for the same track still uses the normal positive cache");
            Check(Object.ReferenceEquals(cache.ReadAsync("玫瑰少年", now.AddSeconds(4), false, ready).GetAwaiter().GetResult(), newCover) && loads == 1,
                "A transient missing Thumbnail preserves an already valid cover only for that same track");

            int failures = 0; Func<Task<BitmapSource>> broken = delegate { Interlocked.Increment(ref failures); throw new IOException("synthetic ready-provider failure"); };
            Check(cache.ReadAsync("故障曲目", now, false, broken).GetAwaiter().GetResult() == null && failures == 0, "An absent provider is never treated as a failure");
            cache.ReadAsync("故障曲目", now.AddMilliseconds(700), true, broken).GetAwaiter().GetResult();
            cache.ReadAsync("故障曲目", now.AddMilliseconds(1800), true, broken).GetAwaiter().GetResult();
            Check(failures == 1, "Actual ready-provider failures retain their bounded retry backoff");
            cache.ReadAsync("故障曲目", now.AddMilliseconds(10700), true, broken).GetAwaiter().GetResult();
            Check(failures == 2, "Failure retry age starts at the actual read, not the earlier not-ready metadata");

            var pending = new TaskCompletionSource<BitmapSource>(); var older = cache.ReadAsync("A", now, true, () => pending.Task);
            Check(cache.ReadAsync("B", now.AddMilliseconds(700), false, ready).GetAwaiter().GetResult() == null, "A not-ready next song invalidates old in-flight art");
            Check(Object.ReferenceEquals(cache.ReadAsync("B", now.AddMilliseconds(1800), true, ready).GetAwaiter().GetResult(), newCover), "The next song can acquire its cover while the obsolete provider is still pending");
            pending.SetResult(oldCover); older.GetAwaiter().GetResult();
            Check(Object.ReferenceEquals(cache.ReadAsync("B", now.AddSeconds(3), false, ready).GetAwaiter().GetResult(), newCover), "An obsolete completion cannot replace a delayed next-track cover");
        }

        private static void CacheAndRaces(BitmapSource first, BitmapSource second)
        {
            var cache = new MediaArtworkCache();
            var now = new DateTime(2026, 10, 23, 1, 0, 0, DateTimeKind.Utc);
            int loads = 0;
            Func<Task<BitmapSource>> load = delegate { Interlocked.Increment(ref loads); return Task.FromResult(first); };
            Check(Object.ReferenceEquals(cache.ReadAsync("track", now, load).GetAwaiter().GetResult(), first), "Cache returns decoded artwork");
            Check(Object.ReferenceEquals(cache.ReadAsync("track", now.AddSeconds(2), load).GetAwaiter().GetResult(), first) && loads == 1, "Polling does not decode the same cover every second");
            cache.ReadAsync("track", now.AddSeconds(61), load).GetAwaiter().GetResult();
            Check(loads == 2, "Same-title artwork is periodically refreshed");
            int missing = 0;
            Func<Task<BitmapSource>> fail = delegate { missing++; throw new IOException("synthetic artwork provider failure"); };
            Check(cache.ReadAsync("missing", now, fail).GetAwaiter().GetResult() == null, "Artwork loader failure is isolated");
            cache.ReadAsync("missing", now.AddSeconds(9), fail).GetAwaiter().GetResult();
            Check(missing == 1, "Artwork failures have a bounded retry interval");
            cache.ReadAsync("missing", now.AddSeconds(10), fail).GetAwaiter().GetResult();
            Check(missing == 2, "Missing artwork can recover without changing track");

            var pending = new TaskCompletionSource<BitmapSource>();
            var older = cache.ReadAsync("A", now, () => pending.Task);
            Check(Object.ReferenceEquals(older, cache.ReadAsync("A", now, load)), "Concurrent reads share the same in-flight artwork load");
            cache.ReadAsync("B", now, () => Task.FromResult(second)).GetAwaiter().GetResult();
            cache.ReadAsync("A", now, () => Task.FromResult(first)).GetAwaiter().GetResult();
            pending.SetResult(second);
            older.GetAwaiter().GetResult();
            var current = cache.ReadAsync("A", now.AddSeconds(1), () => Task.FromResult(second)).GetAwaiter().GetResult();
            Check(Object.ReferenceEquals(current, first), "An old A/B/A completion cannot replace the current track cover");
        }

        private static void TimelineInterpolation()
        {
            var now = new DateTime(2026, 10, 23, 1, 0, 0, DateTimeKind.Utc);
            var track = new MediaSnapshot { Playing = true, Position = TimeSpan.FromSeconds(10), Duration = TimeSpan.FromSeconds(30), SampledAtUtc = now, PlaybackRate = 1.5 };
            Check(track.EstimatePosition(now.AddSeconds(2)) == TimeSpan.FromSeconds(13), "Timeline interpolation honors playback speed");
            Check(track.EstimatePosition(now.AddSeconds(60)) == track.Duration, "Interpolated position cannot pass track end");
            track.Playing = false;
            Check(track.EstimatePosition(now.AddSeconds(5)) == track.Position, "Paused position remains stationary");
            track.Playing = true;
            Check(track.EstimatePosition(now.AddSeconds(-2)) == track.Position, "Future source timestamps cannot move playback backwards");
            track.SampledAtUtc = DateTime.MinValue;
            Check(track.EstimatePosition(now.AddSeconds(2)) == track.Position, "Missing timestamp cannot invent elapsed time");
            track.SampledAtUtc = now;
            track.PlaybackRate = Double.NaN;
            Check(track.EstimatePosition(now.AddSeconds(2)) == TimeSpan.FromSeconds(12), "Nonfinite playback rate uses safe normal speed");
            track.PlaybackRate = -1;
            Check(track.EstimatePosition(now.AddSeconds(2)) == track.Position, "Invalid negative playback speed does not reverse lyric position");
            track.Position = TimeSpan.FromSeconds(-5); track.Playing = false;
            Check(track.EstimatePosition(now) == TimeSpan.Zero, "Invalid negative position is clamped");
        }

        private static byte[] ImageBytes(int width, int height)
        {
            var pixels = new byte[width * height * 4];
            for (int index = 0; index < pixels.Length; index += 4)
            { pixels[index] = 80; pixels[index + 1] = 140; pixels[index + 2] = 220; pixels[index + 3] = 255; }
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            bitmap.Freeze();
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = new MemoryStream()) { encoder.Save(stream); return stream.ToArray(); }
        }

        private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    }
}
