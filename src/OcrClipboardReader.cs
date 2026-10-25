using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace CodexIsland
{
    // Clipboard providers can block. At most one background STA read can be in flight.
    internal sealed class OcrClipboardReader
    {
        private readonly object gate = new object();
        private Task<BitmapSource> current;
        internal async Task<BitmapSource> ReadAsync(CancellationToken cancellation)
        {
            Task<BitmapSource> work;
            lock (gate)
            {
                if (current == null || current.IsCompleted)
                {
                    var result = new TaskCompletionSource<BitmapSource>();
                    var thread = new Thread(() => {
                        try { var image = Clipboard.GetImage(); if (image != null) { image = image.Clone(); image.Freeze(); } result.TrySetResult(image); }
                        catch { result.TrySetResult(null); }
                    }) { IsBackground = true, Name = "Side Island OCR clipboard" };
                    thread.SetApartmentState(ApartmentState.STA); current = result.Task; thread.Start();
                }
                work = current;
            }
            if (await Task.WhenAny(work, Task.Delay(2000, cancellation)).ConfigureAwait(false) != work)
            { cancellation.ThrowIfCancellationRequested(); throw new TimeoutException("剪贴板读取超时，请重试或选择图片。"); }
            cancellation.ThrowIfCancellationRequested(); return await work.ConfigureAwait(false);
        }
    }
}
