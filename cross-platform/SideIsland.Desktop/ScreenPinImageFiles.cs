using Avalonia.Media.Imaging;
using SideIsland.Core;
using SideIsland.Desktop.Platforms;

namespace SideIsland.Desktop;

internal static class ScreenPinImageFiles
{
    internal static Task<Bitmap> DecodeAsync(string path, CancellationToken token) => Task.Run(async () =>
    {
        // Validate and decode the same bounded byte snapshot. A user file or
        // portal path may be replaced after an earlier metadata check.
        byte[] bytes = await ScreenCaptureService.ReadPngSnapshotAsync(path, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        using var input = new MemoryStream(bytes, false);
        var bitmap = new Bitmap(input);
        if (token.IsCancellationRequested) { bitmap.Dispose(); token.ThrowIfCancellationRequested(); }
        return bitmap;
    }, token);

    internal static void SaveNew(Bitmap bitmap, string destination, CancellationToken token)
        => PublishNew(destination, stream => bitmap.Save(stream, PngBitmapEncoderOptions.Default), token);

    internal static void SaveNew(byte[] png, string destination, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(png);
        PublishNew(destination, stream => stream.Write(png), token);
    }

    internal static void PublishNew(string destination, Action<Stream> encode, CancellationToken token)
        => PortableFilePublication.WriteNew(destination, encode, token);
}
