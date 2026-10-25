using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SideIsland.Desktop.Platforms;

public sealed record MediaSnapshot(bool Available, string Player, string Title, string Artist,
    string? ArtworkUri, double? PositionSeconds, double? DurationSeconds, bool Playing, string? Error);
public sealed record VolumeSnapshot(bool Available, int Percent, bool Muted, string? Error);
public sealed record PortOwner(int Port, string Protocol, int? Pid, string Name, string Path);

/// <summary>
/// Opt-in desktop integrations. Reading Mac media contacts Apple Music only, and
/// may show macOS's Automation consent prompt. This class never reads messages,
/// installs tools, changes OS permissions, or executes a user-provided command.
/// </summary>
public sealed class CommandPlatformService : IDisposable
{
    private readonly object gate = new();
    private readonly HashSet<Process> children = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim awakeChanges = new(1, 1);
    private readonly SemaphoreSlim volumeChanges = new(1, 1);
    private Process? awakeProcess;
    private DateTime awakeUntilUtc;
    private string? awakeError;
    private string? preferredPlayer;
    private bool disposed;

    public string Description => OperatingSystem.IsMacOS()
        ? "macOS: Apple Music (explicit Automation connection), system volume, lsof, caffeinate. Other players and per-app volume are not provided by this adapter."
        : OperatingSystem.IsLinux()
            ? "Linux: MPRIS via playerctl, system volume via pactl, ports via ss, keep awake via systemd-inhibit. Missing tools are reported; nothing is installed automatically."
            : "These command integrations support macOS and Linux only.";

    public bool IsAwake
    {
        get { lock (gate) return awakeProcess is not null && !HasExited(awakeProcess) && DateTime.UtcNow < awakeUntilUtc; }
    }
    public string? AwakeError { get { lock (gate) return awakeError; } }

    public async Task<MediaSnapshot> ReadMediaAsync(CancellationToken cancellationToken)
    {
        CheckAlive(cancellationToken);
        if (OperatingSystem.IsMacOS())
        {
            var result = await RunAsync("osascript", new[] { "-e", MacReadMusic }, cancellationToken, 12000);
            if (!result.Success) return NoMedia("Apple Music", Explain(result, "Apple Music could not be read. Allow this app under Privacy & Security > Automation if macOS requests it."));
            var parts = result.Output.TrimEnd('\r', '\n').Split('\u001f');
            if (parts.Length == 1 && parts[0] == "unavailable") return NoMedia("Apple Music", "Apple Music is not running or has no current track.");
            if (parts.Length != 6 || parts[0] != "ok") return NoMedia("Apple Music", "Apple Music returned incomplete track information.");
            return new(true, "Apple Music", Clean(parts[2], 512), Clean(parts[3], 512), null,
                Timeline(Seconds(parts[4])), Timeline(PositiveSeconds(parts[5])), parts[1].Equals("playing", StringComparison.OrdinalIgnoreCase), null);
        }
        if (!OperatingSystem.IsLinux()) return NoMedia("", "Media integration is unavailable on this platform.");
        string? player = await SelectLinuxPlayerAsync(cancellationToken);
        if (player is null) return NoMedia("MPRIS", lastPlayerError ?? "No MPRIS player is available.");
        string format = "{{status}}\u001f{{position}}\u001f{{mpris:length}}\u001f{{xesam:title}}\u001f{{xesam:artist}}\u001f{{mpris:artUrl}}";
        var metadata = await RunAsync("playerctl", new[] { "--player", player, "--format", format, "metadata" }, cancellationToken);
        if (!metadata.Success) return NoMedia(player, Explain(metadata, "The selected MPRIS player is no longer available."));
        var values = metadata.Output.TrimEnd('\r', '\n').Split('\u001f');
        if (values.Length != 6) return NoMedia(player, "The MPRIS player returned incomplete metadata.");
        double? position = Seconds(values[1]), duration = PositiveSeconds(values[2]);
        // MPRIS Position and mpris:length use microseconds; playerctl's format
        // variables expose those raw values rather than the position command's seconds.
        position /= 1_000_000d; duration /= 1_000_000d;
        string title = Clean(values[3], 512);
        return new(true, player, title.Length == 0 ? player : title, Clean(values[4], 512), Artwork(values[5]),
            Timeline(position), Timeline(duration), values[0].Equals("Playing", StringComparison.OrdinalIgnoreCase), null);
    }

    public async Task<string?> ControlMediaAsync(string action, CancellationToken cancellationToken)
    {
        CheckAlive(cancellationToken);
        if (action is not ("play-pause" or "previous" or "next")) return "Unknown media action.";
        if (OperatingSystem.IsMacOS())
        {
            string command = action == "play-pause" ? "playpause" : action == "next" ? "next track" : "previous track";
            string script = "if application \"Music\" is not running then error \"Apple Music is not running.\"\ntell application \"Music\" to " + command;
            var result = await RunAsync("osascript", new[] { "-e", script }, cancellationToken, 12000);
            return result.Success ? null : Explain(result, "Apple Music control failed. Check macOS Automation permission.");
        }
        if (!OperatingSystem.IsLinux()) return "Media control is unavailable on this platform.";
        string? player;
        lock (gate) player = preferredPlayer;
        player ??= await SelectLinuxPlayerAsync(cancellationToken);
        if (player is null) return lastPlayerError ?? "No MPRIS player is available.";
        var control = await RunAsync("playerctl", new[] { "--player", player, action }, cancellationToken);
        return control.Success ? null : Explain(control, "The player did not accept the media command.");
    }

    public async Task<VolumeSnapshot> ReadVolumeAsync(CancellationToken cancellationToken)
    {
        CheckAlive(cancellationToken);
        await volumeChanges.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await ReadVolumeCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { volumeChanges.Release(); }
    }

    private async Task<VolumeSnapshot> ReadVolumeCoreAsync(CancellationToken cancellationToken)
    {
        CheckAlive(cancellationToken);
        if (OperatingSystem.IsMacOS())
        {
            const string script = "set v to get volume settings\nreturn ((output volume of v) as text) & \"|\" & ((output muted of v) as text)";
            var result = await RunAsync("osascript", new[] { "-e", script }, cancellationToken);
            var parts = result.Output.Trim().Split('|');
            if (!result.Success) return NoVolume(Explain(result, "macOS system volume could not be read."));
            if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int percent) ||
                percent is < 0 or > 100 || !bool.TryParse(parts[1], out bool muted)) return NoVolume("The selected audio device does not expose a readable system volume.");
            return new(true, percent, muted, null);
        }
        if (!OperatingSystem.IsLinux()) return NoVolume("Volume control is unavailable on this platform.");
        var volume = await RunAsync("pactl", new[] { "get-sink-volume", "@DEFAULT_SINK@" }, cancellationToken);
        if (!volume.Success) return NoVolume(Explain(volume, "The PulseAudio-compatible audio server could not be reached."));
        var mute = await RunAsync("pactl", new[] { "get-sink-mute", "@DEFAULT_SINK@" }, cancellationToken);
        if (!mute.Success) return NoVolume(Explain(mute, "The default output mute state could not be read."));
        var channels = Regex.Matches(volume.Output, @"(?<!\d)(\d{1,4})%")
            .Cast<Match>().Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).ToArray();
        var state = Regex.Match(mute.Output, @"\bMute:\s*(yes|no)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (channels.Length == 0 || !state.Success) return NoVolume("The audio server returned an unrecognized volume response.");
        // Over-amplified PulseAudio outputs are displayed at this UI's 100% cap.
        return new(true, Math.Clamp((int)Math.Round(channels.Average()), 0, 100), state.Groups[1].Value.Equals("yes", StringComparison.OrdinalIgnoreCase), null);
    }

    public async Task<string?> SetVolumeAsync(int percent, bool muted, CancellationToken cancellationToken)
    {
        CheckAlive(cancellationToken);
        if (percent is < 0 or > 100) return "Volume must be between 0 and 100.";
        await volumeChanges.WaitAsync(cancellationToken);
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                string script = "set volume output volume " + percent.ToString(CultureInfo.InvariantCulture) + " output muted " + (muted ? "true" : "false");
                var result = await RunAsync("osascript", new[] { "-e", script }, cancellationToken);
                return result.Success ? null : Explain(result, "The selected macOS output does not accept system volume changes.");
            }
            if (!OperatingSystem.IsLinux()) return "Volume control is unavailable on this platform.";
            var volume = await RunAsync("pactl", new[] { "set-sink-volume", "@DEFAULT_SINK@", percent.ToString(CultureInfo.InvariantCulture) + "%" }, cancellationToken);
            if (!volume.Success) return Explain(volume, "The output volume could not be changed.");
            var mute = await RunAsync("pactl", new[] { "set-sink-mute", "@DEFAULT_SINK@", muted ? "1" : "0" }, cancellationToken);
            return mute.Success ? null : Explain(mute, "Volume changed, but updating mute failed. Read the current state before retrying.");
        }
        finally { volumeChanges.Release(); }
    }

    public async Task<IReadOnlyList<PortOwner>> ReadPortsAsync(int[] ports, CancellationToken cancellationToken)
    {
        CheckAlive(cancellationToken);
        if (ports is null || ports.Length == 0 || ports.Length > 64 || ports.Any(port => port is < 1 or > 65535))
            throw new ArgumentException("Select between 1 and 64 valid ports (1–65535).", nameof(ports));
        int[] requested = ports.Distinct().Order().ToArray();
        CommandResult result; List<PortOwner> rows;
        if (OperatingSystem.IsMacOS())
        {
            string numbers = string.Join(",", requested.Select(port => port.ToString(CultureInfo.InvariantCulture)));
            result = await RunAsync("lsof", new[] { "-nP", "+c", "0", "-F0pcfnP", "-iTCP:" + numbers, "-iUDP:" + numbers }, cancellationToken, 8000, 512 * 1024);
            // lsof uses exit 1 for an empty selection, not just for failures.
            if (!result.Success && !(result.ExitCode == 1 && string.IsNullOrWhiteSpace(result.Output) && string.IsNullOrWhiteSpace(result.Error)))
                throw new InvalidOperationException(Explain(result, "Socket ownership could not be read."));
            rows = ParseLsof(result.Output, requested);
        }
        else if (OperatingSystem.IsLinux())
        {
            string filter = "( " + string.Join(" or ", requested.Select(port => "sport = :" + port.ToString(CultureInfo.InvariantCulture))) + " )";
            result = await RunAsync("ss", new[] { "-H", "-n", "-a", "-t", "-u", "-p", filter }, cancellationToken, 8000, 512 * 1024);
            if (!result.Success) throw new InvalidOperationException(Explain(result, "Socket ownership could not be read."));
            rows = ParseSs(result.Output, requested);
        }
        else throw new PlatformNotSupportedException("Port ownership is supported by this adapter on macOS and Linux only.");
        return rows.DistinctBy(row => (row.Port, row.Protocol, row.Pid, row.Name, row.Path))
            .OrderBy(row => row.Port).ThenBy(row => row.Protocol, StringComparer.Ordinal).ThenBy(row => row.Pid).ToArray();
    }

    public async Task<string?> SetAwakeAsync(bool enabled, TimeSpan duration, CancellationToken cancellationToken)
    {
        CheckAlive(cancellationToken);
        if (enabled && (duration <= TimeSpan.Zero || duration > TimeSpan.FromDays(1))) return "Keep-awake duration must be greater than zero and at most 24 hours.";
        await awakeChanges.WaitAsync(cancellationToken);
        try
        {
            if (!enabled) { StopAwake(); return null; }
            string executable; string[] arguments;
            int seconds = (int)Math.Ceiling(duration.TotalSeconds);
            if (OperatingSystem.IsMacOS()) { executable = "caffeinate"; arguments = new[] { "-i", "-t", seconds.ToString(CultureInfo.InvariantCulture) }; }
            else if (OperatingSystem.IsLinux())
            {
                string? sleep = FindCommand("sleep");
                if (sleep is null) return "Keep awake requires the system sleep command; no package was installed.";
                executable = "systemd-inhibit";
                arguments = new[] { "--what=idle:sleep", "--mode=block", "--who=Side Island", "--why=Temporary keep awake requested by the user", sleep, seconds.ToString(CultureInfo.InvariantCulture) };
            }
            else return "Keep awake is unavailable on this platform.";
            string? path = FindCommand(executable);
            if (path is null) return Missing(executable);
            var process = NewProcess(path, arguments);
            try { StartOwned(process); }
            catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException) { process.Dispose(); return Clean(error.Message, 600); }
            var output = new CapturedOutput(8192); var errors = new CapturedOutput(8192);
            Task drainOut = DrainAsync(process.StandardOutput, output, () => KillOwned(process));
            Task drainError = DrainAsync(process.StandardError, errors, () => KillOwned(process));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            bool handedOff = false;
            try
            {
                await Task.Delay(450, linked.Token);
                if (HasExited(process))
                {
                    await FinishDraining(drainOut, drainError);
                    return "Keep-awake helper did not remain active. " + Clean(errors.Text, 600);
                }
                Process? previous;
                lock (gate)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    if (disposed) throw new ObjectDisposedException(nameof(CommandPlatformService));
                    previous = awakeProcess; awakeProcess = process;
                    awakeUntilUtc = DateTime.UtcNow.AddSeconds(seconds); awakeError = null;
                }
                if (previous is not null) KillOwned(previous);
                handedOff = true;
                _ = ObserveAwakeAsync(process, drainOut, drainError, errors);
                return null;
            }
            finally
            {
                if (!handedOff) { KillOwned(process); await FinishDraining(drainOut, drainError); Forget(process); process.Dispose(); }
            }
        }
        finally { awakeChanges.Release(); }
    }

    private string? lastPlayerError;
    private async Task<string?> SelectLinuxPlayerAsync(CancellationToken cancellationToken)
    {
        var result = await RunAsync("playerctl", new[] { "--all-players", "--format", "{{playerInstance}}\u001f{{status}}", "status" }, cancellationToken);
        if (!result.Success) { lastPlayerError = Explain(result, "No MPRIS player is available."); return null; }
        var players = result.Output.Split('\n').Select(line => line.TrimEnd('\r').Split('\u001f'))
            .Where(parts => parts.Length == 2 && Regex.IsMatch(parts[0], @"\A[A-Za-z0-9_.-]{1,240}\z", RegexOptions.CultureInvariant)).Take(64).ToArray();
        string? preferred;
        lock (gate) preferred = preferredPlayer;
        string? selected = players.FirstOrDefault(parts => parts[1].Equals("Playing", StringComparison.OrdinalIgnoreCase))?[0]
            ?? players.FirstOrDefault(parts => parts[0] == preferred)?[0] ?? players.FirstOrDefault()?[0];
        lock (gate) preferredPlayer = selected;
        lastPlayerError = selected is null ? "No MPRIS player is available." : null;
        return selected;
    }

    private static List<PortOwner> ParseSs(string text, int[] requested)
    {
        var wanted = requested.ToHashSet(); var rows = new List<PortOwner>();
        foreach (string line in text.Split('\n'))
        {
            string[] columns = Regex.Split(line.Trim(), @"\s+", RegexOptions.CultureInvariant);
            if (columns.Length < 6 || !(columns[0].StartsWith("tcp", StringComparison.Ordinal) || columns[0].StartsWith("udp", StringComparison.Ordinal))) continue;
            int? port = EndpointPort(columns[4]); if (port is null || !wanted.Contains(port.Value)) continue;
            string protocol = columns[0].StartsWith("tcp", StringComparison.Ordinal) ? "TCP" : "UDP";
            var owners = Regex.Matches(string.Join(" ", columns.Skip(6)), """\("(?<name>(?:[^"\\]|\\.)*)",pid=(?<pid>[0-9]+)""", RegexOptions.CultureInvariant);
            if (owners.Count == 0) rows.Add(new(port.Value, protocol, null, "Process details unavailable", ""));
            foreach (Match owner in owners)
                if (int.TryParse(owner.Groups["pid"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
                    rows.Add(new(port.Value, protocol, pid, Clean(owner.Groups["name"].Value.Replace("\\\"", "\""), 256), ProcessPath(pid)));
        }
        return rows;
    }

    private static List<PortOwner> ParseLsof(string text, int[] requested)
    {
        var wanted = requested.ToHashSet(); var rows = new List<PortOwner>();
        int? pid = null; string name = "", endpoint = "", protocol = "";
        void Flush()
        {
            int? port = EndpointPort(endpoint);
            if (pid.HasValue && port.HasValue && wanted.Contains(port.Value) && protocol is "TCP" or "UDP")
                rows.Add(new(port.Value, protocol, pid, Clean(name, 256), ProcessPath(pid.Value)));
            endpoint = protocol = "";
        }
        foreach (string raw in text.Split('\0'))
        {
            string field = raw.TrimStart('\r', '\n'); if (field.Length < 2) continue;
            string value = field[1..].TrimEnd('\r', '\n');
            switch (field[0])
            {
                case 'p': Flush(); pid = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) ? parsed : null; name = ""; break;
                case 'c': name = value; break;
                case 'f': Flush(); break;
                case 'P': protocol = value.ToUpperInvariant(); break;
                case 'n': endpoint = value; break;
            }
        }
        Flush(); return rows;
    }

    private static int? EndpointPort(string endpoint)
    {
        int peer = endpoint.IndexOf("->", StringComparison.Ordinal); if (peer >= 0) endpoint = endpoint[..peer];
        int colon = endpoint.LastIndexOf(':');
        return colon >= 0 && int.TryParse(endpoint[(colon + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int port) && port is > 0 and <= 65535 ? port : null;
    }

    private static string ProcessPath(int pid)
    {
        try
        {
            if (OperatingSystem.IsLinux()) return new FileInfo("/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/exe").LinkTarget ?? "";
            if (OperatingSystem.IsMacOS())
            {
                var buffer = new byte[4096]; int length = proc_pidpath(pid, buffer, (uint)buffer.Length);
                if (length > 0) { int end = Array.IndexOf(buffer, (byte)0); return Encoding.UTF8.GetString(buffer, 0, end >= 0 ? end : Math.Min(length, buffer.Length)); }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException) { }
        return "";
    }
    [DllImport("/usr/lib/libproc.dylib", SetLastError = true)] private static extern int proc_pidpath(int pid, [Out] byte[] buffer, uint bufferSize);

    private async Task<CommandResult> RunAsync(string command, IReadOnlyList<string> arguments, CancellationToken cancellationToken, int timeoutMilliseconds = 6000, int maximumCharacters = 128 * 1024)
    {
        CheckAlive(cancellationToken);
        string? path = FindCommand(command); if (path is null) return new(-1, "", Missing(command));
        using var timeout = new CancellationTokenSource(timeoutMilliseconds);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token, timeout.Token);
        using var process = NewProcess(path, arguments);
        try { StartOwned(process); }
        catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException) { return new(-1, "", Clean(error.Message, 600)); }
        var output = new CapturedOutput(maximumCharacters); var errors = new CapturedOutput(16 * 1024);
        Task stdout = DrainAsync(process.StandardOutput, output, () => KillOwned(process));
        Task stderr = DrainAsync(process.StandardError, errors, () => KillOwned(process));
        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException) { KillOwned(process); timedOut = timeout.IsCancellationRequested; }
        finally { await FinishDraining(stdout, stderr); Forget(process); }
        cancellationToken.ThrowIfCancellationRequested();
        lifetime.Token.ThrowIfCancellationRequested();
        if (timedOut) return new(-1, "", command + " timed out; only its own helper process was stopped.");
        if (output.Exceeded || errors.Exceeded) return new(-1, "", command + " exceeded the output limit and was stopped.");
        return new(HasExited(process) ? process.ExitCode : -1, output.Text, errors.Text);
    }

    private static Process NewProcess(string path, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["LC_ALL"] = "C";
        return new Process { StartInfo = start, EnableRaisingEvents = true };
    }
    private void StartOwned(Process process)
    {
        lock (gate)
        {
            if (disposed) throw new ObjectDisposedException(nameof(CommandPlatformService));
            if (!process.Start()) throw new InvalidOperationException("The helper process could not be started.");
            children.Add(process);
        }
    }
    private void Forget(Process process) { lock (gate) children.Remove(process); }
    private static bool HasExited(Process process) { try { return process.HasExited; } catch (InvalidOperationException) { return true; } }
    private static void KillOwned(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or NotSupportedException) { }
    }
    private static async Task FinishDraining(Task first, Task second)
    { await Task.WhenAny(Task.WhenAll(first, second), Task.Delay(1500)); }
    private static async Task DrainAsync(StreamReader reader, CapturedOutput output, Action overflow)
    {
        try
        {
            var buffer = new char[4096]; int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory())) > 0)
                if (!output.Append(buffer, read)) overflow();
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException) { }
    }
    private sealed class CapturedOutput(int limit)
    {
        private readonly object sync = new(); private readonly StringBuilder value = new();
        private bool exceeded;
        public bool Exceeded { get { lock (sync) return exceeded; } }
        public string Text { get { lock (sync) return value.ToString(); } }
        public bool Append(char[] buffer, int count)
        {
            lock (sync)
            {
                int available = Math.Max(0, limit - value.Length); value.Append(buffer, 0, Math.Min(count, available));
                if (count > available) exceeded = true;
                return !exceeded;
            }
        }
    }
    private sealed record CommandResult(int ExitCode, string Output, string Error)
    { public bool Success => ExitCode == 0; }

    private async Task ObserveAwakeAsync(Process process, Task stdout, Task stderr, CapturedOutput errors)
    {
        try
        {
            await process.WaitForExitAsync(); await FinishDraining(stdout, stderr);
            lock (gate)
            {
                if (ReferenceEquals(awakeProcess, process))
                {
                    if (DateTime.UtcNow < awakeUntilUtc.AddSeconds(-1) || process.ExitCode != 0)
                        awakeError = "Keep-awake helper stopped early. " + Clean(errors.Text, 600);
                    awakeProcess = null;
                }
            }
        }
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException or Win32Exception) { }
        finally { Forget(process); process.Dispose(); }
    }
    private void StopAwake()
    {
        Process? previous;
        lock (gate) { previous = awakeProcess; awakeProcess = null; awakeError = null; }
        if (previous is not null) KillOwned(previous);
    }
    private void CheckAlive(CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); lock (gate) if (disposed) throw new ObjectDisposedException(nameof(CommandPlatformService)); }
    public void Dispose()
    {
        Process[] snapshot;
        lock (gate) { if (disposed) return; disposed = true; awakeProcess = null; snapshot = children.ToArray(); }
        lifetime.Cancel(); foreach (Process process in snapshot) KillOwned(process);
        // In-flight operations own disposal of their process/readers. Semaphores
        // remain valid until their finally blocks release, including cancellation.
    }

    private static string? FindCommand(string name)
    {
        if (name.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))) return null;
        string[] system = OperatingSystem.IsMacOS() ? new[] { "/usr/bin", "/usr/sbin", "/bin", "/sbin" } : Array.Empty<string>();
        IEnumerable<string> paths = system.Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)).Concat(new[] { "/usr/bin", "/bin", "/usr/sbin", "/sbin" });
        foreach (string directory in paths.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal))
            try { string full = Path.GetFullPath(Path.Combine(directory, name)); if (File.Exists(full)) return full; }
            catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { }
        return null;
    }
    private static string Missing(string command) => "Required system command '" + command + "' was not found. Install or enable it yourself if desired; this app does not install packages.";
    private static string Explain(CommandResult result, string fallback)
    { string detail = Clean(result.Error, 800); return detail.Length == 0 ? fallback + " (exit " + result.ExitCode.ToString(CultureInfo.InvariantCulture) + ")" : fallback + " " + detail; }
    private static string Clean(string value, int maximum)
    {
        string clean = Regex.Replace(value ?? "", @"[\x00-\x1f\x7f]+", " ").Trim();
        if (clean.Length <= maximum) return clean;
        if (maximum > 0 && char.IsHighSurrogate(clean[maximum - 1])) maximum--;
        return clean[..maximum];
    }
    private static double? Seconds(string value)
    {
        value = value.Trim();
        // AppleScript may format a decimal using the user's regional comma.
        if (Regex.IsMatch(value, @"\A[0-9]+,[0-9]+(?:[eE][+-]?[0-9]+)?\z", RegexOptions.CultureInvariant)) value = value.Replace(',', '.');
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result) && double.IsFinite(result) && result >= 0 ? result : null;
    }
    private static double? PositiveSeconds(string value) { double? result = Seconds(value); return result.HasValue && result.Value > 0 ? result : null; }
    private static double? Timeline(double? seconds) => seconds.HasValue && seconds.Value >= 0 && seconds.Value < TimeSpan.MaxValue.TotalSeconds ? seconds : null;
    private static string? Artwork(string value)
    { value = value.Trim(); return value.Length <= 4096 && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" or "file" ? value : null; }
    private static MediaSnapshot NoMedia(string player, string error) => new(false, player, "", "", null, null, null, false, error);
    private static VolumeSnapshot NoVolume(string error) => new(false, 0, false, error);

    private const string MacReadMusic = """
        on cleanField(v)
            set oldDelimiters to AppleScript's text item delimiters
            set AppleScript's text item delimiters to ASCII character 31
            set pieces to text items of (v as text)
            set AppleScript's text item delimiters to " "
            set resultText to pieces as text
            set AppleScript's text item delimiters to oldDelimiters
            return resultText
        end cleanField
        if application "Music" is not running then return "unavailable"
        tell application "Music"
            set currentState to player state as text
            if currentState is "stopped" then return "unavailable"
            set trackTitle to my cleanField(name of current track)
            set trackArtist to my cleanField(artist of current track)
            set trackPosition to player position as text
            set trackDuration to duration of current track as text
            return "ok" & (ASCII character 31) & currentState & (ASCII character 31) & trackTitle & (ASCII character 31) & trackArtist & (ASCII character 31) & trackPosition & (ASCII character 31) & trackDuration
        end tell
        """;
}
