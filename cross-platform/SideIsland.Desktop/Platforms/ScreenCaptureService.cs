using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using Tmds.DBus.Protocol;

namespace SideIsland.Desktop.Platforms;

public enum ScreenCaptureStatus { Completed, Cancelled, Unavailable, Failed }
public sealed record ScreenCaptureResult(ScreenCaptureStatus Status, ScreenCaptureFile? Capture, string Backend, string? Error);

/// <summary>Owns exactly one generated temporary file, never a portal/user source.</summary>
public sealed class ScreenCaptureFile : IDisposable
{
    private string? path;
    internal ScreenCaptureFile(string path) => this.path = path;
    public string Path => path ?? throw new ObjectDisposedException(nameof(ScreenCaptureFile));
    public void Dispose()
    {
        string? owned = Interlocked.Exchange(ref path, null);
        if (owned is null) return;
        try { File.Delete(owned); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// User-initiated, permission-respecting capture. No shell, privileged helper,
/// background capture, installation, X11 root grab or Wayland bypass is used.
/// Native capture/consent must still be checked on the destination desktop.
/// </summary>
public sealed class ScreenCaptureService
{
    internal enum CapturePlatform { MacOS, Linux, Unsupported }
    internal enum Backend { MacOS, Gnome, Spectacle }
    internal sealed record CommandResult(int ExitCode, string Error);
    internal sealed record PortalResult(ScreenCaptureStatus Status, string? SourcePath = null, string? Error = null);
    private const string PortalName = "org.freedesktop.portal.Desktop";
    private readonly string temporaryDirectory;
    private readonly bool testMode;
    private readonly CapturePlatform platform;
    private readonly Func<ProcessStartInfo, CancellationToken, Task<CommandResult>> run;
    private readonly Func<CancellationToken, Task<PortalResult>> portal;
    private readonly Func<string, string?> find;
    internal const long MaximumPngBytes = 64L * 1024 * 1024;

    public ScreenCaptureService(string dataDirectory, bool testMode = false)
        : this(dataDirectory, OperatingSystem.IsMacOS() ? CapturePlatform.MacOS : OperatingSystem.IsLinux() ? CapturePlatform.Linux : CapturePlatform.Unsupported,
            RunAsync, CapturePortalAsync, FindExecutable, testMode) { }

    internal ScreenCaptureService(string dataDirectory, CapturePlatform platform,
        Func<ProcessStartInfo, CancellationToken, Task<CommandResult>> run,
        Func<CancellationToken, Task<PortalResult>> portal, Func<string, string?> find, bool testMode = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        temporaryDirectory = System.IO.Path.Combine(System.IO.Path.GetFullPath(dataDirectory), "screen-pin-temp");
        this.platform = platform; this.run = run; this.portal = portal; this.find = find; this.testMode = testMode;
    }

    public async Task<ScreenCaptureResult> CaptureRegionAsync(CancellationToken cancellationToken = default)
    {
        // Layout tests must not create a directory, request consent or launch a tool.
        if (testMode || cancellationToken.IsCancellationRequested) return new(ScreenCaptureStatus.Cancelled, null, "", null);
        if (platform == CapturePlatform.Unsupported) return new(ScreenCaptureStatus.Unavailable, null, "", "Screen capture is available on macOS and Linux in this preview.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        string? owned = null;
        bool ownsOutput = false;
        string backend = platform == CapturePlatform.MacOS ? "macOS Screenshot" : "XDG Screenshot portal";
        try
        {
            string? source = null;
            if (platform == CapturePlatform.Linux)
            {
                var result = await portal(timeout.Token).ConfigureAwait(false);
                if (result.Status is ScreenCaptureStatus.Cancelled or ScreenCaptureStatus.Failed)
                    return new(result.Status, null, backend, result.Error);
                if (result.Status == ScreenCaptureStatus.Completed)
                {
                    if (string.IsNullOrWhiteSpace(result.SourcePath)) return new(ScreenCaptureStatus.Failed, null, backend, "The Screenshot portal returned no local image file.");
                    source = result.SourcePath;
                }
            }
            Directory.CreateDirectory(temporaryDirectory);
            owned = System.IO.Path.Combine(temporaryDirectory, "capture-" + Guid.NewGuid().ToString("N") + ".png");
            if (source is not null)
            {
                // A portal URI is borrowed. Copy it and leave the portal's file alone.
                byte[] snapshot = await ReadPngSnapshotAsync(source, timeout.Token).ConfigureAwait(false);
                await using var output = new FileStream(owned, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
                ownsOutput = true;
                await output.WriteAsync(snapshot, timeout.Token).ConfigureAwait(false);
            }
            else
            {
                Backend selected;
                string? executable;
                if (platform == CapturePlatform.MacOS) { selected = Backend.MacOS; executable = "/usr/sbin/screencapture"; }
                else if ((executable = find("gnome-screenshot")) is not null) { selected = Backend.Gnome; backend = "GNOME Screenshot"; }
                else if ((executable = find("spectacle")) is not null) { selected = Backend.Spectacle; backend = "Spectacle"; }
                else return new(ScreenCaptureStatus.Unavailable, null, backend, "No Screenshot portal or installed GNOME Screenshot / Spectacle is available. Open an existing PNG instead.");
                ownsOutput = true; // This unique path is reserved for this invocation's native output.
                var command = await run(BuildStartInfo(selected, executable, owned), timeout.Token).ConfigureAwait(false);
                if (!File.Exists(owned))
                {
                    if (command.ExitCode == 0 || string.IsNullOrWhiteSpace(command.Error) || IsCancellation(command.Error))
                        return new(ScreenCaptureStatus.Cancelled, null, backend, null);
                    return new(ScreenCaptureStatus.Failed, null, backend, command.Error);
                }
                if (command.ExitCode != 0) return new(ScreenCaptureStatus.Failed, null, backend, command.Error.Length == 0 ? "The screenshot tool failed." : command.Error);
            }
            await ValidatePngAsync(owned, timeout.Token).ConfigureAwait(false);
            var capture = new ScreenCaptureFile(owned); owned = null;
            return new(ScreenCaptureStatus.Completed, capture, backend, null);
        }
        catch (OperationCanceledException)
        { return new(cancellationToken.IsCancellationRequested ? ScreenCaptureStatus.Cancelled : ScreenCaptureStatus.Failed, null, backend, cancellationToken.IsCancellationRequested ? null : "Screenshot selection timed out."); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or InvalidDataException or DBusExceptionBase)
        { return new(ScreenCaptureStatus.Failed, null, backend, exception.Message); }
        finally { if (ownsOutput && owned is not null) new ScreenCaptureFile(owned).Dispose(); }
    }

    internal static ProcessStartInfo BuildStartInfo(Backend backend, string executable, string output)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        string[] arguments = backend switch
        {
            Backend.MacOS => ["-i", "-x", "-t", "png", output],
            Backend.Gnome => ["--area", "--file", output],
            Backend.Spectacle => ["--background", "--region", "--nonotify", "--output", output],
            _ => throw new ArgumentOutOfRangeException(nameof(backend))
        };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }

    internal static async Task ValidatePngAsync(string path, CancellationToken token)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        var header = new byte[24]; await input.ReadExactlyAsync(header, token).ConfigureAwait(false);
        ValidatePngHeader(header, input.Length);
    }
    internal static async Task<byte[]> ReadPngSnapshotAsync(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        long length = input.Length;
        if (length < 33 || length > MaximumPngBytes) throw new InvalidDataException("The screenshot is missing, empty or larger than 64 MB.");
        var bytes = new byte[(int)length]; await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        var extra = new byte[1];
        if (await input.ReadAsync(extra, token).ConfigureAwait(false) != 0 || input.Length != length) throw new IOException("The PNG changed while it was being read. Try again.");
        token.ThrowIfCancellationRequested(); ValidatePngHeader(bytes, length); return bytes;
    }
    private static void ValidatePngHeader(ReadOnlySpan<byte> header, long length)
    {
        if (length < 33 || length > MaximumPngBytes) throw new InvalidDataException("The screenshot is missing, empty or larger than 64 MB.");
        if (!header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) || !header.Slice(12, 4).SequenceEqual("IHDR"u8))
            throw new InvalidDataException("The selected file is not a PNG image.");
        uint width = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(16, 4)), height = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(20, 4));
        if (width == 0 || height == 0 || width > 32768 || height > 32768 || (ulong)width * height > 48_000_000)
            throw new InvalidDataException("The screenshot dimensions exceed the supported limit.");
    }

    private static bool IsCancellation(string error) => error.Contains("cancel", StringComparison.OrdinalIgnoreCase) || error.Contains("取消", StringComparison.Ordinal);
    private static string? FindExecutable(string name)
    {
        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!System.IO.Path.IsPathFullyQualified(folder)) continue;
            string path = System.IO.Path.Combine(folder, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static async Task<CommandResult> RunAsync(ProcessStartInfo info, CancellationToken token)
    {
        using var process = new Process { StartInfo = info };
        process.Start();
        // Drain both pipes to prevent a desktop tool blocking on stderr/stdout.
        var output = DrainAsync(process.StandardOutput); var error = DrainAsync(process.StandardError);
        try
        {
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            await Task.WhenAll(output, error).WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (Win32Exception) { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch (TimeoutException) { }
            try { await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch (TimeoutException) { }
            throw;
        }
        await output.ConfigureAwait(false);
        return new(process.ExitCode, (await error.ConfigureAwait(false)).Trim());
    }
    private static async Task<string> DrainAsync(StreamReader reader)
    {
        var text = new System.Text.StringBuilder(); var buffer = new char[1024]; int count;
        try
        {
            while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                if (text.Length < 4096) text.Append(buffer, 0, Math.Min(count, 4096 - text.Length));
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        return text.ToString();
    }

    private sealed record PortalResponse(string Path, uint Code, string? Uri);
    private static async Task<PortalResult> CapturePortalAsync(CancellationToken token)
    {
        if (string.IsNullOrEmpty(DBusAddress.Session)) return new(ScreenCaptureStatus.Unavailable);
        using var connection = new DBusConnection(DBusAddress.Session);
        string? request = null;
        bool completed = false;
        try
        {
            await connection.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
            string sender = (connection.UniqueName ?? throw new IOException("The session bus did not provide a unique name.")).TrimStart(':').Replace('.', '_');
            string id = "pin_" + Guid.NewGuid().ToString("N");
            request = "/org/freedesktop/portal/desktop/request/" + sender + "/" + id;
            string? returnedPath = null;
            var responses = new Dictionary<string, PortalResponse>();
            var result = new TaskCompletionSource<PortalResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            var gate = new object();
            // Subscribe before requesting, on the same connection as the caller.
            using var match = await connection.AddMatchAsync(new MatchRule { Type = MessageType.Signal, Sender = PortalName, Interface = "org.freedesktop.portal.Request", Member = "Response", PathNamespace = "/org/freedesktop/portal/desktop/request/" + sender },
                (message, _) =>
                {
                    var reader = message.GetBodyReader(); uint code = reader.ReadUInt32();
                    var values = reader.ReadDictionaryOfStringToVariantValue();
                    string? uri = values.TryGetValue("uri", out var value) && value.Type == VariantValueType.String ? value.GetString() : null;
                    return new PortalResponse(message.PathAsString ?? "", code, uri);
                },
                (Notification<PortalResponse> notification) =>
                {
                    if (notification.IsCompletion) { result.TrySetException(notification.Exception); return; }
                    if (!notification.HasValue) return;
                    var response = notification.Value;
                    lock (gate)
                    {
                        if (returnedPath is not null) { if (response.Path == returnedPath) result.TrySetResult(response); }
                        else if (responses.Count < 16) responses[response.Path] = response;
                    }
                }, emitOnCapturedContext: false, flags: ObserverFlags.EmitOnConnectionClosed | ObserverFlags.EmitOnReaderFailed).AsTask().WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
            string actual = await connection.CallMethodAsync(CreateScreenshotMessage(connection, id), (message, _) => message.GetBodyReader().ReadObjectPathAsString()).WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
            request = actual;
            lock (gate) { returnedPath = actual; if (responses.TryGetValue(actual, out var early)) result.TrySetResult(early); }
            var response = await result.Task.WaitAsync(token).ConfigureAwait(false);
            completed = true;
            if (response.Code == 1) return new(ScreenCaptureStatus.Cancelled);
            if (response.Code != 0) return new(ScreenCaptureStatus.Failed, Error: "The desktop Screenshot portal rejected the request.");
            if (!TryLocalPortalPath(response.Uri, out string? source)) return new(ScreenCaptureStatus.Failed, Error: "The Screenshot portal returned no local image file.");
            return new(ScreenCaptureStatus.Completed, source);
        }
        catch (DBusErrorReplyException exception) when (exception.ErrorName is "org.freedesktop.DBus.Error.ServiceUnknown" or "org.freedesktop.DBus.Error.NameHasNoOwner" or "org.freedesktop.DBus.Error.UnknownMethod" or "org.freedesktop.DBus.Error.UnknownInterface")
        { return new(ScreenCaptureStatus.Unavailable); }
        catch (DBusConnectFailedException) { return new(ScreenCaptureStatus.Unavailable); }
        catch (TimeoutException) { return new(ScreenCaptureStatus.Failed, Error: "The Screenshot portal did not respond."); }
        finally
        {
            if (!completed && request is not null)
            {
                try { await connection.CallMethodAsync(CreateCloseMessage(connection, request)).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
                catch (Exception exception) when (exception is DBusExceptionBase or InvalidOperationException or TimeoutException) { }
            }
        }
    }
    internal static bool TryLocalPortalPath(string? value, out string? path)
    {
        path = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !uri.IsFile || !string.IsNullOrEmpty(uri.Host) && uri.Host != "localhost" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return false;
        path = uri.LocalPath;
        return System.IO.Path.IsPathFullyQualified(path);
    }
    private static MessageBuffer CreateScreenshotMessage(DBusConnection connection, string id)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: PortalName, path: "/org/freedesktop/portal/desktop", @interface: "org.freedesktop.portal.Screenshot", member: "Screenshot", signature: "sa{sv}");
        writer.WriteString(""); // Unparented system picker: the island is hidden.
        writer.WriteDictionary(new Dictionary<string, VariantValue> { ["handle_token"] = id, ["interactive"] = true, ["modal"] = false });
        return writer.CreateMessage();
    }
    private static MessageBuffer CreateCloseMessage(DBusConnection connection, string request)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: PortalName, path: request, @interface: "org.freedesktop.portal.Request", member: "Close");
        return writer.CreateMessage();
    }
}
