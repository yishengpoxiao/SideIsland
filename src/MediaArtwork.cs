using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace CodexIsland
{
    internal static class MediaArtwork
    {
        internal const int MaxEncodedBytes = 4 * 1024 * 1024;
        internal const int MaxDimension = 256;

        internal static BitmapSource Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > MaxEncodedBytes) return null;
            try
            {
                using (var input = new MemoryStream(bytes, false))
                {
                    var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
                    if (decoder.Frames.Count == 0) return null;
                    int width = decoder.Frames[0].PixelWidth, height = decoder.Frames[0].PixelHeight;
                    if (width < 1 || height < 1 || width > 65535 || height > 65535 || (long)width * height > 100000000) return null;
                    input.Position = 0;
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    if (width > MaxDimension || height > MaxDimension)
                    {
                        if (width >= height) image.DecodePixelWidth = MaxDimension;
                        else image.DecodePixelHeight = MaxDimension;
                    }
                    image.StreamSource = input;
                    image.EndInit();
                    image.Freeze();
                    return image;
                }
            }
            catch (Exception) { return null; }
        }
    }

    // At most one current-track entry. No unbounded collection or retained encoded data.
    internal sealed class MediaArtworkCache
    {
        private readonly object gate = new object();
        private string key;
        private BitmapSource image;
        private DateTime attemptedAt;
        private Task<BitmapSource> pending;
        private int generation;

        internal Task<BitmapSource> ReadAsync(string trackKey, DateTime utcNow, Func<Task<BitmapSource>> loader)
        { return ReadAsync(trackKey, utcNow, true, loader); }

        internal Task<BitmapSource> ReadAsync(string trackKey, DateTime utcNow, bool providerReady, Func<Task<BitmapSource>> loader)
        {
            lock (gate)
            {
                if (!String.Equals(key, trackKey, StringComparison.Ordinal))
                {
                    key = trackKey; image = null; pending = null; attemptedAt = DateTime.MinValue; generation++;
                }
                // Some players publish the new title before its Thumbnail. Absence
                // is not a failed read and must not delay the first available cover.
                // Keep only same-track valid art; changing tracks invalidates it above.
                if (!providerReady) return Task.FromResult(image);
                if (attemptedAt != DateTime.MinValue)
                {
                    if (pending != null) return pending;
                    double age = (utcNow - attemptedAt).TotalSeconds;
                    if (age >= 0 && age < (image == null ? 10 : 60)) return Task.FromResult(image);
                }
                image = null;
                attemptedAt = utcNow;
                int request = ++generation;
                // A provider may complete synchronously. Starting on a worker keeps all
                // decoder and WinRT work outside the caller's Dispatcher and the gate.
                pending = Task.Run(() => LoadAsync(request, loader));
                return pending;
            }
        }

        private async Task<BitmapSource> LoadAsync(int request, Func<Task<BitmapSource>> loader)
        {
            BitmapSource value = null;
            try { value = await loader().ConfigureAwait(false); }
            catch (Exception) { }
            lock (gate)
            {
                if (generation == request)
                { image = value; pending = null; }
            }
            return value;
        }
    }
}
