using Avalonia.Media.Imaging;
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
    {
        token.ThrowIfCancellationRequested();
        string target = Path.GetFullPath(destination);
        bool exists;
        try { File.GetAttributes(target); exists = true; }
        catch (FileNotFoundException) { exists = false; }
        catch (DirectoryNotFoundException) { exists = false; }
        if (exists) throw new IOException("The destination already exists. Choose a new filename.");
        string temporary = Path.Combine(Path.GetDirectoryName(target)!, ".side-island-pin-" + Guid.NewGuid().ToString("N") + ".tmp");
        bool owned = false;
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                owned = true;
                encode(output);
                output.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, target); // Never overwrite, including a target created while encoding.
            owned = false;
        }
        finally { if (owned) { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } } }
    }
}
