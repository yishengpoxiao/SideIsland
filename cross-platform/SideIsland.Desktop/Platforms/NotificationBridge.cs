#nullable enable
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SideIsland.Core;

namespace SideIsland.Desktop.Platforms;

/// <summary>
/// Explicitly enabled notification inputs, not a replacement notification daemon.
/// macOS reads only on-screen NotificationCenter AX windows. Linux monitors only
/// Notify calls permitted by session-bus policy. No private notification database,
/// permissions changes, extension installation, or global input is used.
/// </summary>
public sealed class NotificationBridge : IDisposable
{
    private readonly NotificationCenter center;
    private readonly object gate = new();
    private readonly SemaphoreSlim changes = new(1, 1), httpSlots = new(4, 4);
    private readonly HashSet<TcpClient> clients = new();
    private CancellationTokenSource? macStop, linuxStop, httpStop;
    private Process? monitor;
    private TcpListener? listener;
    private string token = "", url = "", macStatus = "off", linuxStatus = "off", httpStatus = "off";
    private bool disposed;
    public NotificationBridge(NotificationCenter center) { this.center = center; }
    public event Action? Changed;
    public bool IsMacEnabled { get { lock (gate) return macStop is not null; } }
    public bool IsLinuxEnabled { get { lock (gate) return linuxStop is not null; } }
    public bool IsHttpEnabled { get { lock (gate) return listener is not null; } }
    public string Status { get { lock (gate) return "macOS AX: " + macStatus + "\nLinux D-Bus: " + linuxStatus + "\nLocal bridge: " + httpStatus; } }
    public string Url { get { lock (gate) return url; } }
    public string Token { get { lock (gate) return token; } }

    public async Task<string?> SetMacAccessibilityAsync(bool enabled, CancellationToken cancellationToken)
    {
        await changes.WaitAsync(cancellationToken);
        try
        {
            CheckAlive(); StopMac();
            if (!enabled) { SetStatus("mac", "off"); return null; }
            if (!OperatingSystem.IsMacOS()) return "Visible Accessibility notifications are supported on macOS only.";
            cancellationToken.ThrowIfCancellationRequested();
            if (!MacAccessibility.Trusted)
            {
                const string error = "Accessibility permission is missing. Manually allow this installed app in System Settings > Privacy & Security > Accessibility, then enable again. No permission is requested automatically.";
                SetStatus("mac", error); return error;
            }
            var source = new CancellationTokenSource(); lock (gate) { if (disposed) { source.Dispose(); throw new ObjectDisposedException(nameof(NotificationBridge)); } macStop = source; }
            SetStatus("mac", "enabled; only visible accessible notification cards, no background history");
            _ = Task.Run(() => MacLoopAsync(source));
            return null;
        }
        finally { changes.Release(); }
    }
    public async Task<string?> SetLinuxMonitorAsync(bool enabled, CancellationToken cancellationToken)
    {
        await changes.WaitAsync(cancellationToken);
        try
        {
            CheckAlive(); StopLinux();
            if (!enabled) { SetStatus("linux", "off"); return null; }
            if (!OperatingSystem.IsLinux()) return "Session D-Bus notification monitoring is supported on Linux only.";
            string? command = FindCommand("dbus-monitor");
            if (command is null) { const string error = "dbus-monitor was not found. Nothing was installed; use the optional local feed or install the tool yourself."; SetStatus("linux", error); return error; }
            var start = new ProcessStartInfo(command) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "--session", "--monitor", "type='method_call',interface='org.freedesktop.Notifications',member='Notify'" }) start.ArgumentList.Add(argument);
            start.Environment["LC_ALL"] = "C";
            var process = new Process { StartInfo = start }; var source = new CancellationTokenSource();
            try { if (!process.Start()) throw new InvalidOperationException("The D-Bus monitor could not be started."); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
            { process.Dispose(); source.Dispose(); string text = "D-Bus monitor failed: " + Safe(error.Message); SetStatus("linux", text); return text; }
            lock (gate) { if (disposed) { Kill(process); process.Dispose(); source.Dispose(); throw new ObjectDisposedException(nameof(NotificationBridge)); } monitor = process; linuxStop = source; }
            SetStatus("linux", "connected to session monitor; desktop bus policy may restrict notifications");
            _ = Task.Run(() => LinuxLoopAsync(process, source));
            await Task.Delay(250, cancellationToken);
            lock (gate) return ReferenceEquals(monitor, process) ? null : linuxStatus;
        }
        finally { changes.Release(); }
    }
    public async Task<string?> SetHttpAsync(bool enabled, int port, CancellationToken cancellationToken)
    {
        await changes.WaitAsync(cancellationToken);
        try
        {
            CheckAlive(); StopHttp();
            if (!enabled) { SetStatus("http", "off; previous token invalidated"); return null; }
            if (port is < 0 or > 65535) return "Local bridge port must be 0–65535; 0 selects a free port.";
            try
            {
                var server = new TcpListener(IPAddress.Loopback, port); server.Start(8);
                var source = new CancellationTokenSource(); CancellationToken stopped = source.Token; string access = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
                lock (gate) { if (disposed) { server.Stop(); source.Dispose(); throw new ObjectDisposedException(nameof(NotificationBridge)); } listener = server; httpStop = source; token = access; url = "http://127.0.0.1:" + ((IPEndPoint)server.LocalEndpoint).Port + "/notifications"; }
                SetStatus("http", "enabled on 127.0.0.1 only; token is kept in memory");
                _ = Task.Run(() => HttpLoopAsync(server, stopped, access)); return null;
            }
            catch (Exception error) when (error is SocketException or IOException or InvalidOperationException)
            { string text = "Local bridge failed: " + Safe(error.Message); SetStatus("http", text); return text; }
        }
        finally { changes.Release(); }
    }

    private async Task MacLoopAsync(CancellationTokenSource source)
    {
        var visible = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            while (!source.IsCancellationRequested)
            {
                var snapshot = MacAccessibility.Read(source.Token);
                var current = new HashSet<string>(StringComparer.Ordinal);
                foreach (var value in snapshot.Items)
                {
                    source.Token.ThrowIfCancellationRequested();
                    string key = Hash(value.SourceId + "\n" + value.Title + "\n" + value.Body);
                    current.Add(key);
                    if (!visible.TryGetValue(key, out var native)) visible[key] = native = "visible:" + Guid.NewGuid().ToString("N");
                    lock (gate) if (!disposed && ReferenceEquals(macStop, source) && !source.IsCancellationRequested) center.Publish(value with { NativeId = native });
                }
                foreach (string stale in visible.Keys.Where(key => !current.Contains(key)).ToArray()) visible.Remove(stale);
                SetRunningStatus(source, "mac", snapshot.Status);
                await Task.Delay(1500, source.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        { SetRunningStatus(source, "mac", "Accessibility read failed: " + Safe(error.Message)); }
        finally { lock (gate) if (ReferenceEquals(macStop, source)) macStop = null; source.Dispose(); Signal(); }
    }
    private async Task LinuxLoopAsync(Process process, CancellationTokenSource source)
    {
        var errors = new StringBuilder(); var parser = new DbusParser();
        Task stderr = Lines(process.StandardError, line => { lock (errors) if (errors.Length < 4000) errors.AppendLine(line[..Math.Min(line.Length, 1000)]); }, source.Token);
        try
        {
            await Lines(process.StandardOutput, line =>
            {
                if (parser.Push(line, DateTimeOffset.UtcNow) is { } value)
                    lock (gate) if (!disposed && ReferenceEquals(linuxStop, source) && !source.IsCancellationRequested) center.Publish(value);
            }, source.Token);
            if (!source.IsCancellationRequested)
            {
                string detail; lock (errors) detail = Safe(errors.ToString());
                SetRunningStatus(source, "linux", "monitor stopped. Session policy may reject BecomeMonitor; no permissions were changed. " + detail);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { if (!source.IsCancellationRequested) SetRunningStatus(source, "linux", "monitor failed: " + Safe(error.Message)); }
        finally
        {
            Kill(process); try { await Task.WhenAny(stderr, Task.Delay(500)); } catch (OperationCanceledException) { }
            lock (gate) if (ReferenceEquals(monitor, process)) { monitor = null; linuxStop = null; }
            process.Dispose(); source.Dispose(); Signal();
        }
    }
    // dbus-monitor prints top-level arguments at three spaces. Nested image-data
    // integers must not terminate the Notify record before later hints arrive.
    internal sealed class DbusParser
    {
        private readonly StringBuilder message = new(); private bool collecting;
        internal NotificationInput? Push(string line, DateTimeOffset now)
        {
            if (line.Length > 0 && !line.StartsWith(' '))
            {
                message.Clear(); collecting = line.StartsWith("method call ", StringComparison.Ordinal) && line.Contains("interface=org.freedesktop.Notifications;", StringComparison.Ordinal) && line.EndsWith("member=Notify", StringComparison.Ordinal);
            }
            if (!collecting) return null;
            if (message.Length + line.Length > 64 * 1024) { collecting = false; message.Clear(); return null; }
            message.AppendLine(line);
            if (!line.StartsWith("   int32 ", StringComparison.Ordinal)) return null;
            collecting = false; var value = TryParseDbusNotify(message.ToString(), now); message.Clear(); return value;
        }
    }
    private static async Task Lines(StreamReader reader, Action<string> consume, CancellationToken cancellationToken)
    {
        var buffer = new char[4096]; var line = new StringBuilder(); bool excessive = false;
        while (true)
        {
            int count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken); if (count == 0) break;
            for (int index = 0; index < count; index++)
            {
                char character = buffer[index];
                if (character == '\n') { if (!excessive) consume(line.ToString().TrimEnd('\r')); line.Clear(); excessive = false; }
                else if (line.Length < 16000) line.Append(character); else excessive = true;
            }
        }
        if (!excessive && line.Length > 0) consume(line.ToString());
    }
    internal static NotificationInput? TryParseDbusNotify(string text, DateTimeOffset now)
    {
        if (text.Length > 64 * 1024) return null;
        string[] lines = text.Split('\n'); if (lines.Length == 0 || !lines[0].StartsWith("method call ", StringComparison.Ordinal) || !lines[0].Contains("interface=org.freedesktop.Notifications;", StringComparison.Ordinal) || !lines[0].TrimEnd('\r').EndsWith("member=Notify", StringComparison.Ordinal) || !lines.Any(line => line.StartsWith("   int32 ", StringComparison.Ordinal))) return null;
        var strings = new List<string>(); uint replacement = 0; string? desktop = null;
        for (int index = 1; index < lines.Length; index++)
        {
            string line = lines[index].TrimStart();
            if (lines[index].StartsWith("   uint32 ", StringComparison.Ordinal) && strings.Count == 1) uint.TryParse(line[7..].Trim(), out replacement);
            if (line.StartsWith("string ", StringComparison.Ordinal) && Quoted(line[7..]) is { } value)
            {
                if (strings.Count < 4 && lines[index].StartsWith("   string ", StringComparison.Ordinal)) strings.Add(value);
                else if (value == "desktop-entry")
                {
                    for (int next = index + 1; next < Math.Min(index + 4, lines.Length); next++)
                    { string candidate = lines[next].TrimStart(); int marker = candidate.IndexOf("string ", StringComparison.Ordinal); if (marker >= 0 && Quoted(candidate[(marker + 7)..]) is { } key) { desktop = key; break; } }
                }
            }
        }
        if (strings.Count != 4) return null;
        string app = strings[0].Trim(); if (app.Length == 0) app = "Unknown application";
        string identity = string.IsNullOrWhiteSpace(desktop) ? app : desktop;
        string source = "dbus:" + Hash(identity.ToLowerInvariant());
        string? native = replacement == 0 ? null : "replacement:" + replacement.ToString(CultureInfo.InvariantCulture);
        string body = WebUtility.HtmlDecode(Regex.Replace(strings[3], "<[^>]{0,1024}>", "", RegexOptions.CultureInvariant));
        return new(source, app, strings[2], body, now, native);
    }
    private static string? Quoted(string text)
    {
        if (text.Length < 2 || text[0] != '"') return null; var result = new StringBuilder();
        for (int index = 1; index < text.Length; index++)
        {
            char character = text[index]; if (character == '"') return result.ToString();
            if (character == '\\' && index + 1 < text.Length)
            {
                char escaped = text[++index];
                if (escaped is >= '0' and <= '7')
                {
                    // C-style octal escapes encode bytes, not Unicode code points.
                    // Decode consecutive escaped bytes as UTF-8 (e.g. Chinese).
                    var octets = new List<byte>();
                    while (true)
                    {
                        int code = escaped - '0', digits = 1;
                        while (digits < 3 && index + 1 < text.Length && text[index + 1] is >= '0' and <= '7') { code = code * 8 + text[++index] - '0'; digits++; }
                        if (code > 255) return null; octets.Add((byte)code);
                        if (index + 2 >= text.Length || text[index + 1] != '\\' || text[index + 2] is not (>= '0' and <= '7')) break;
                        index += 2; escaped = text[index];
                    }
                    try { result.Append(new UTF8Encoding(false, true).GetString(octets.ToArray())); }
                    catch (DecoderFallbackException) { return null; }
                }
                else result.Append(escaped switch { 'n' => '\n', 'r' => '\r', 't' => '\t', 'b' => '\b', 'f' => '\f', _ => escaped });
            }
            else result.Append(character);
            if (result.Length > 12000) return null;
        }
        return null;
    }

    private async Task HttpLoopAsync(TcpListener server, CancellationToken stopped, string access)
    {
        try
        {
            while (!stopped.IsCancellationRequested)
            {
                var client = await server.AcceptTcpClientAsync(stopped);
                if (!httpSlots.Wait(0)) { client.Dispose(); continue; }
                lock (gate) { if (disposed || stopped.IsCancellationRequested || !ReferenceEquals(listener, server)) { client.Dispose(); httpSlots.Release(); return; } clients.Add(client); }
                _ = HttpClientAsync(client, server, stopped, access);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or SocketException) { }
    }
    private sealed class FeedMessage
    {
        public string? SourceId { get; set; } public string? SourceName { get; set; }
        public string? Title { get; set; } public string? Body { get; set; } public string? NativeId { get; set; }
    }
    private async Task HttpClientAsync(TcpClient client, TcpListener server, CancellationToken stopped, string access)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopped); deadline.CancelAfter(4000);
        NetworkStream? stream = null;
        try
        {
            stream = client.GetStream(); var header = new List<byte>(); var single = new byte[1]; bool complete = false;
            while (header.Count < 8192)
            {
                if (await stream.ReadAsync(single, deadline.Token) == 0) return;
                header.Add(single[0]); int count = header.Count;
                if (count >= 4 && header[count - 4] == 13 && header[count - 3] == 10 && header[count - 2] == 13 && header[count - 1] == 10) { complete = true; break; }
            }
            if (!complete) { await Respond(stream, 400, "headers exceed 8 KiB", deadline.Token); return; }
            string[] lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in lines.Skip(1).Where(line => line.Length > 0)) { int colon = line.IndexOf(':'); if (colon <= 0 || !fields.TryAdd(line[..colon], line[(colon + 1)..].Trim())) { await Respond(stream, 400, "invalid headers", deadline.Token); return; } }
            if (lines[0] != "POST /notifications HTTP/1.1" || fields.ContainsKey("Origin") || fields.ContainsKey("Transfer-Encoding")) { await Respond(stream, 400, "only local JSON feed requests are accepted", deadline.Token); return; }
            string expectedHost = "127.0.0.1:" + ((IPEndPoint)server.LocalEndpoint).Port;
            if (!fields.TryGetValue("Host", out var host) || !host.Equals(expectedHost, StringComparison.Ordinal)) { await Respond(stream, 400, "invalid host", deadline.Token); return; }
            if (!fields.TryGetValue("Authorization", out var auth) || !FixedEquals(auth, "Bearer " + access)) { await Respond(stream, 401, "authentication required", deadline.Token); return; }
            if (!fields.TryGetValue("Content-Type", out var mime) || !mime.Split(';')[0].Trim().Equals("application/json", StringComparison.OrdinalIgnoreCase) || !fields.TryGetValue("Content-Length", out var rawLength) || !int.TryParse(rawLength, NumberStyles.None, CultureInfo.InvariantCulture, out int length) || length is <= 0 or > 16000) { await Respond(stream, 400, "invalid JSON length or content type", deadline.Token); return; }
            var bytes = new byte[length]; int read = 0; while (read < bytes.Length) { int count = await stream.ReadAsync(bytes.AsMemory(read), deadline.Token); if (count == 0) return; read += count; }
            var value = JsonSerializer.Deserialize<FeedMessage>(bytes, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, MaxDepth = 8 });
            if (value is null || string.IsNullOrWhiteSpace(value.SourceId) || value.SourceId.Length > 150 || value.SourceId.Any(char.IsControl)) { await Respond(stream, 400, "a sourceId of 1–150 characters is required", deadline.Token); return; }
            bool accepted;
            lock (gate)
            {
                if (disposed || stopped.IsCancellationRequested || !ReferenceEquals(listener, server) || token != access) return;
                accepted = center.Publish(new("http:" + value.SourceId.Trim(), value.SourceName ?? value.SourceId, value.Title ?? "", value.Body ?? "", DateTimeOffset.UtcNow, value.NativeId));
            }
            await Respond(stream, 200, accepted ? "accepted" : "ignored by source policy or duplicate rules", deadline.Token);
        }
        catch (JsonException) { try { if (stream is not null) await Respond(stream, 400, "invalid JSON", deadline.Token); } catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { } }
        catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException) { }
        finally { lock (gate) clients.Remove(client); stream?.Dispose(); client.Dispose(); httpSlots.Release(); }
    }
    private static async Task Respond(NetworkStream stream, int status, string message, CancellationToken cancellationToken)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(new { status, message });
        byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " " + (status == 200 ? "OK" : "Error") + "\r\nConnection: close\r\nContent-Type: application/json; charset=utf-8\r\nCache-Control: no-store\r\nContent-Length: " + body.Length + "\r\n\r\n");
        await stream.WriteAsync(head, cancellationToken); await stream.WriteAsync(body, cancellationToken);
    }
    private static bool FixedEquals(string left, string right)
    { byte[] a = Encoding.UTF8.GetBytes(left), b = Encoding.UTF8.GetBytes(right); return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b); }
    private void StopMac() { CancellationTokenSource? source; lock (gate) { source = macStop; macStop = null; } try { source?.Cancel(); } catch (ObjectDisposedException) { } }
    private void StopLinux()
    {
        CancellationTokenSource? source; Process? process;
        lock (gate) { source = linuxStop; linuxStop = null; process = monitor; monitor = null; }
        try { source?.Cancel(); } catch (ObjectDisposedException) { } if (process is not null) Kill(process);
    }
    private void StopHttp()
    {
        TcpListener? server; CancellationTokenSource? source; TcpClient[] active;
        lock (gate) { server = listener; listener = null; source = httpStop; httpStop = null; token = url = ""; active = clients.ToArray(); }
        source?.Cancel(); server?.Stop(); foreach (var client in active) client.Dispose(); source?.Dispose();
    }
    private void SetStatus(string input, string value)
    { bool changed; lock (gate) { string old = input == "mac" ? macStatus : input == "linux" ? linuxStatus : httpStatus; changed = old != value; if (input == "mac") macStatus = value; else if (input == "linux") linuxStatus = value; else httpStatus = value; } if (changed) Signal(); }
    private void SetRunningStatus(CancellationTokenSource source, string input, string value)
    { lock (gate) { if (disposed || source.IsCancellationRequested || !ReferenceEquals(input == "mac" ? macStop : linuxStop, source)) return; SetStatus(input, value); } }
    private void Signal() { if (!disposed) Changed?.Invoke(); }
    private void CheckAlive() { lock (gate) if (disposed) throw new ObjectDisposedException(nameof(NotificationBridge)); }
    public void Dispose() { lock (gate) { if (disposed) return; disposed = true; } StopMac(); StopLinux(); StopHttp(); }
    private static void Kill(Process process) { try { if (!process.HasExited) process.Kill(true); } catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { } }
    private static string? FindCommand(string command)
    {
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Concat(new[] { "/usr/bin", "/bin" }).Where(value => value.Length > 0))
            try { string candidate = Path.GetFullPath(Path.Combine(directory, command)); if (File.Exists(candidate)) return candidate; } catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException) { }
        return null;
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Safe(string value) { value = Regex.Replace(value, @"[\x00-\x1f\x7f]+", " ").Trim(); return value[..Math.Min(value.Length, 800)]; }

    // Pure classification is shared with synthetic AX-tree contract fixtures.
    // A NotificationCenter bundle prefix on a card is not itself a root marker.
    internal static bool IsMacNotificationCard(string role, string identifier, string description, int textCount)
    {
        if (role is not ("AXGroup" or "AXButton") || textCount < 2) return false;
        string id = identifier.Trim().ToLowerInvariant(), label = description.Trim().ToLowerInvariant(), marker = id + " " + label;
        bool explicitRoot = id.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(value => value is "notificationcenter" or "notificationcenterui" or "com.apple.notificationcenter" or "com.apple.notificationcenterui") || label is "notification center" or "通知中心";
        return !explicitRoot && !id.Contains("viewcontroller", StringComparison.Ordinal) && (marker.Contains("notification", StringComparison.Ordinal) || marker.Contains("通知", StringComparison.Ordinal));
    }

    private static class MacAccessibility
    {
        private const string Ax = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices", Cf = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", Cg = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
        internal static bool Trusted => AXIsProcessTrusted();
        internal sealed record Snapshot(IReadOnlyList<NotificationInput> Items, string Status);
        private sealed record Node(string Role, string Identifier, string Description, string Text, List<Node> Children);
        private readonly record struct Rect(double X, double Y, double W, double H)
        { internal bool Intersects(Rect other) => W > 0 && H > 0 && X < other.X + other.W && X + W > other.X && Y < other.Y + other.H && Y + H > other.Y; }
        [StructLayout(LayoutKind.Sequential)] private struct Pair { public double X, Y; }
        internal static Snapshot Read(CancellationToken cancellationToken)
        {
            if (!Trusted) return new(Array.Empty<NotificationInput>(), "permission missing; visible Accessibility reading is stopped");
            var processes = Process.GetProcessesByName("NotificationCenter"); var inputs = new List<NotificationInput>(); int visibleWindows = 0, recognizable = 0;
            var clock = Stopwatch.StartNew(); IntPtr system = AXUIElementCreateSystemWide();
            try { AXUIElementSetMessagingTimeout(system, .15f); } finally { Release(system); }
            foreach (var process in processes)
            {
                using (process)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var visible = VisibleWindows(process.Id); if (visible.Count == 0) continue;
                    IntPtr app = AXUIElementCreateApplication(process.Id), windows = Attribute(app, "AXWindows");
                    try
                    {
                        if (windows == IntPtr.Zero || CFGetTypeID(windows) != CFArrayGetTypeID()) continue;
                        for (int index = 0; index < Math.Min((long)CFArrayGetCount(windows), 8); index++)
                        {
                            cancellationToken.ThrowIfCancellationRequested(); if (clock.ElapsedMilliseconds > 2500) break;
                            IntPtr window = CFArrayGetValueAtIndex(windows, index); var bounds = Bounds(window);
                            if (bounds is null || !visible.Any(rect => rect.Intersects(bounds.Value))) continue;
                            visibleWindows++; int budget = 350; var node = Capture(window, bounds.Value, 0, ref budget, clock, cancellationToken);
                            if (node is null) continue;
                            foreach (var card in Cards(node))
                            {
                                var text = Texts(card).Where(value => !Boilerplate(value)).Distinct(StringComparer.Ordinal).Take(20).ToArray();
                                if (text.Length < 2) continue;
                                string sourceName = text[0], title = text[1], body = string.Join("\n", text.Skip(2));
                                if (sourceName.Length > 160 || sourceName.Contains('\n')) continue;
                                inputs.Add(new("mac.ax:" + Hash(sourceName.ToLowerInvariant()), sourceName, title, body, DateTimeOffset.UtcNow)); recognizable++;
                            }
                        }
                    }
                    finally { Release(windows); Release(app); }
                }
            }
            return new(inputs, visibleWindows == 0 ? "enabled; no on-screen Notification Center windows (background history is not read)" : recognizable == 0 ? "enabled; no recognizable visible notification cards; this macOS Accessibility layout may be unsupported" : "enabled; sampled " + recognizable + " visible accessible notification cards");
        }
        private static Node? Capture(IntPtr element, Rect viewport, int depth, ref int budget, Stopwatch clock, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); if (depth > 18 || budget-- <= 0 || clock.ElapsedMilliseconds > 2500) return null;
            if (Boolean(element, "AXHidden") == true) return null;
            var bounds = Bounds(element); if (bounds is not null && !bounds.Value.Intersects(viewport)) return null;
            string role = String(element, "AXRole"), identifier = String(element, "AXIdentifier"), subrole = String(element, "AXSubrole"), description = String(element, "AXRoleDescription");
            string text = role is "AXStaticText" or "AXTextArea" ? String(element, "AXValue") : "";
            if (text.Length == 0 && role == "AXStaticText") text = String(element, "AXTitle");
            var children = new List<Node>(); IntPtr array = Attribute(element, "AXChildren");
            try
            {
                if (array != IntPtr.Zero && CFGetTypeID(array) == CFArrayGetTypeID())
                    for (int index = 0; index < Math.Min((long)CFArrayGetCount(array), 100); index++)
                    { var child = Capture(CFArrayGetValueAtIndex(array, index), viewport, depth + 1, ref budget, clock, cancellationToken); if (child is not null) children.Add(child); if (budget <= 0 || clock.ElapsedMilliseconds > 2500) break; }
            }
            finally { Release(array); }
            return new(role, identifier + " " + subrole, description, text, children);
        }
        private static IEnumerable<Node> Cards(Node node)
        {
            var nested = node.Children.SelectMany(Cards).ToArray(); if (nested.Length > 0) return nested;
            return IsMacNotificationCard(node.Role, node.Identifier, node.Description, Texts(node).Take(2).Count()) ? new[] { node } : Array.Empty<Node>();
        }
        private static IEnumerable<string> Texts(Node node)
        { if (node.Text.Trim() is { Length: > 0 } text) yield return text; foreach (var child in node.Children) foreach (string value in Texts(child)) yield return value; }
        private static bool Boilerplate(string value) => Regex.IsMatch(value.Trim(), @"\A(?:now|just now|\d+\s+(?:seconds?|minutes?|hours?)\s+ago|刚刚|现在|\d+\s*(?:秒|分钟|小时)前|close|clear all|options|关闭|全部清除|选项)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static Rect? Bounds(IntPtr element)
        {
            IntPtr position = Attribute(element, "AXPosition"), size = Attribute(element, "AXSize");
            try { return position != IntPtr.Zero && size != IntPtr.Zero && AXValueGetValue(position, 1, out var p) && AXValueGetValue(size, 2, out var s) ? new(p.X, p.Y, s.X, s.Y) : null; }
            finally { Release(position); Release(size); }
        }
        private static List<Rect> VisibleWindows(int pid)
        {
            var result = new List<Rect>(); IntPtr list = CGWindowListCopyWindowInfo(17, 0);
            try
            {
                if (list == IntPtr.Zero) return result;
                for (nint index = 0; index < CFArrayGetCount(list); index++)
                {
                    IntPtr entry = CFArrayGetValueAtIndex(list, index);
                    if (Number(Dictionary(entry, "kCGWindowOwnerPID")) != pid) continue;
                    IntPtr bounds = Dictionary(entry, "kCGWindowBounds");
                    if (bounds == IntPtr.Zero) continue;
                    result.Add(new(Number(Dictionary(bounds, "X")), Number(Dictionary(bounds, "Y")), Number(Dictionary(bounds, "Width")), Number(Dictionary(bounds, "Height"))));
                }
            }
            finally { Release(list); }
            return result;
        }
        private static double Number(IntPtr value) => value != IntPtr.Zero && CFNumberGetValue(value, 13, out double number) ? number : double.NaN;
        private static IntPtr Dictionary(IntPtr dictionary, string name)
        { if (dictionary == IntPtr.Zero) return IntPtr.Zero; IntPtr key = CFStringCreateWithCString(IntPtr.Zero, name, 0x08000100); try { return CFDictionaryGetValue(dictionary, key); } finally { Release(key); } }
        private static IntPtr Attribute(IntPtr element, string name)
        { IntPtr key = CFStringCreateWithCString(IntPtr.Zero, name, 0x08000100); try { return AXUIElementCopyAttributeValue(element, key, out var value) == 0 ? value : IntPtr.Zero; } finally { Release(key); } }
        private static string String(IntPtr element, string name)
        {
            IntPtr value = Attribute(element, name);
            try { if (value == IntPtr.Zero || CFGetTypeID(value) != CFStringGetTypeID()) return ""; var bytes = new byte[8192]; return CFStringGetCString(value, bytes, bytes.Length, 0x08000100) ? Encoding.UTF8.GetString(bytes, 0, Array.IndexOf(bytes, (byte)0) is int end && end >= 0 ? end : bytes.Length) : ""; }
            finally { Release(value); }
        }
        private static bool? Boolean(IntPtr element, string name)
        { IntPtr value = Attribute(element, name); try { return value != IntPtr.Zero && CFGetTypeID(value) == CFBooleanGetTypeID() ? CFBooleanGetValue(value) : null; } finally { Release(value); } }
        private static void Release(IntPtr value) { if (value != IntPtr.Zero) CFRelease(value); }
        [DllImport(Ax)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool AXIsProcessTrusted();
        [DllImport(Ax)] private static extern IntPtr AXUIElementCreateApplication(int pid);
        [DllImport(Ax)] private static extern IntPtr AXUIElementCreateSystemWide();
        [DllImport(Ax)] private static extern int AXUIElementSetMessagingTimeout(IntPtr element, float seconds);
        [DllImport(Ax)] private static extern int AXUIElementCopyAttributeValue(IntPtr element, IntPtr attribute, out IntPtr value);
        [DllImport(Ax)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool AXValueGetValue(IntPtr value, int type, out Pair result);
        [DllImport(Cg)] private static extern IntPtr CGWindowListCopyWindowInfo(uint options, uint relativeToWindow);
        [DllImport(Cf)] private static extern void CFRelease(IntPtr value);
        [DllImport(Cf)] private static extern nuint CFGetTypeID(IntPtr value);
        [DllImport(Cf)] private static extern nuint CFArrayGetTypeID();
        [DllImport(Cf)] private static extern nint CFArrayGetCount(IntPtr array);
        [DllImport(Cf)] private static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, nint index);
        [DllImport(Cf)] private static extern IntPtr CFDictionaryGetValue(IntPtr dictionary, IntPtr key);
        [DllImport(Cf)] private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, uint encoding);
        [DllImport(Cf)] private static extern nuint CFStringGetTypeID();
        [DllImport(Cf)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFStringGetCString(IntPtr value, byte[] buffer, nint size, uint encoding);
        [DllImport(Cf)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFNumberGetValue(IntPtr number, int type, out double value);
        [DllImport(Cf)] private static extern nuint CFBooleanGetTypeID();
        [DllImport(Cf)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFBooleanGetValue(IntPtr value);
    }
}
