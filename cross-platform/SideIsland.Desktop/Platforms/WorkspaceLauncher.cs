using System.ComponentModel;
using System.Diagnostics;
using SideIsland.Core;

namespace SideIsland.Desktop.Platforms;

public enum WorkspaceLaunchStatus { HandedOff, Unconfirmed, Failed, Skipped }
public sealed record WorkspaceLaunchItemResult(string SourceId, string Name, WorkspaceLaunchStatus Status, string? ErrorCode = null, int? ExitCode = null);
public sealed record WorkspaceLaunchResult(IReadOnlyList<WorkspaceLaunchItemResult> Items);

internal enum WorkspaceLaunchPlatform { Unsupported, MacOS, Linux }
internal sealed record WorkspaceLaunchCommand(string Executable, IReadOnlyList<string> Arguments, bool ObserveHelper);
internal sealed record WorkspaceProcessOutcome(int? ExitCode, bool CancelledAfterStart = false);
internal interface IWorkspaceLaunchAdapter
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    bool IsExecutable(string path);
    string? FindHelper(WorkspaceLaunchPlatform platform);
    Task<WorkspaceProcessOutcome> StartAsync(WorkspaceLaunchCommand command, CancellationToken cancellationToken);
}

/// <summary>
/// Explicit scene launch only. The caller owns the single-launch UI guard.
/// A snapshot is frozen before the first await. Cancelling skips pending items;
/// programs already handed to the OS are never killed or owned by this window.
/// </summary>
public sealed class WorkspaceLauncher
{
    private readonly WorkspaceLaunchPlatform platform;
    private readonly IWorkspaceLaunchAdapter adapter;
    public WorkspaceLauncher() : this(OperatingSystem.IsMacOS() ? WorkspaceLaunchPlatform.MacOS : OperatingSystem.IsLinux() ? WorkspaceLaunchPlatform.Linux : WorkspaceLaunchPlatform.Unsupported, new SystemWorkspaceLaunchAdapter()) { }
    internal WorkspaceLauncher(WorkspaceLaunchPlatform platform, IWorkspaceLaunchAdapter adapter)
    { this.platform = platform; this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter)); }

    public async Task<WorkspaceLaunchResult> RunAsync(IReadOnlyList<PortableWorkspaceResource> resources, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resources);
        if (resources.Count > PortableWorkspaceStore.MaximumSceneResources) throw new WorkspaceProblem(WorkspaceProblemCode.TooManySceneResources, "A launch accepts up to 30 shortcuts.");
        // The public record can contain a caller-owned mutable list; never retain it.
        var frozen = resources.Select(resource => resource is null ? null : resource with
        { Arguments = resource.Arguments is null ? null! : Array.AsReadOnly(resource.Arguments.Take(PortableWorkspaceStore.MaximumArguments + 1).ToArray()) }).ToArray();
        var results = new List<WorkspaceLaunchItemResult>(frozen.Length); var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var resource in frozen)
        {
            string id = resource?.Id ?? "", name = resource?.Name ?? "";
            if (cancellationToken.IsCancellationRequested) { results.Add(new(id, name, WorkspaceLaunchStatus.Skipped, "cancelled")); continue; }
            if (resource is null || !identities.Add(id)) { results.Add(new(id, name, WorkspaceLaunchStatus.Failed, "invalid-resource")); continue; }
            WorkspaceLaunchCommand? command = null;
            try
            {
                // Filesystem/permission checks can block on a mounted volume; keep them off the dispatcher.
                command = await Task.Run(() => Prepare(resource), cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var outcome = await adapter.StartAsync(command, cancellationToken).ConfigureAwait(false);
                results.Add(!command.ObserveHelper || outcome.ExitCode == 0
                    ? new(id, name, WorkspaceLaunchStatus.HandedOff)
                    : outcome.ExitCode is int code
                        ? new(id, name, WorkspaceLaunchStatus.Failed, platform == WorkspaceLaunchPlatform.Linux && code == 3 ? "helper-missing" : "helper-failed", code)
                        : new(id, name, WorkspaceLaunchStatus.Unconfirmed, outcome.CancelledAfterStart ? "cancelled-after-start" : "helper-pending"));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { results.Add(new(id, name, WorkspaceLaunchStatus.Skipped, "cancelled")); }
            catch (WorkspaceLaunchException error) { results.Add(new(id, name, WorkspaceLaunchStatus.Failed, error.Code)); }
            catch (WorkspaceProblem) { results.Add(new(id, name, WorkspaceLaunchStatus.Failed, "invalid-resource")); }
            catch (UnauthorizedAccessException) { results.Add(new(id, name, WorkspaceLaunchStatus.Failed, "permission-denied")); }
            catch (Win32Exception error) { results.Add(new(id, name, WorkspaceLaunchStatus.Failed, error.NativeErrorCode == 2 ? command?.ObserveHelper == true ? "helper-missing" : "target-missing" : error.NativeErrorCode is 5 or 13 ? "permission-denied" : "start-failed")); }
            catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or NotSupportedException or System.Security.SecurityException)
            { results.Add(new(id, name, WorkspaceLaunchStatus.Failed, "start-failed")); }
        }
        return new(results.AsReadOnly());
    }

    private WorkspaceLaunchCommand Prepare(PortableWorkspaceResource resource)
    {
        PortableWorkspaceStore.ValidateResource(resource);
        if (platform == WorkspaceLaunchPlatform.Unsupported) throw new WorkspaceLaunchException("unsupported-platform");
        string target = resource.Target;
        if (resource.Kind == "folder" && !adapter.DirectoryExists(target)) throw new WorkspaceLaunchException("target-missing");
        if (resource.Kind == "program")
        {
            if (platform == WorkspaceLaunchPlatform.MacOS && target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).EndsWith(".app", StringComparison.OrdinalIgnoreCase) && adapter.DirectoryExists(target))
                return new(Helper(), Array.AsReadOnly(new[] { "-a", target, "--args" }.Concat(resource.Arguments).ToArray()), true);
            if (!adapter.FileExists(target)) throw new WorkspaceLaunchException(adapter.DirectoryExists(target) ? "invalid-program" : "target-missing");
            if (!adapter.IsExecutable(target)) throw new WorkspaceLaunchException("not-executable");
            return new(target, resource.Arguments, false);
        }
        // HTTP(S) URLs and absolute folder paths cannot become helper options.
        return new(Helper(), Array.AsReadOnly(new[] { target }), true);
    }
    private string Helper() => adapter.FindHelper(platform) ?? throw new WorkspaceLaunchException("helper-missing");
    private sealed class WorkspaceLaunchException(string code) : Exception(code) { internal string Code { get; } = code; }
}

internal sealed class SystemWorkspaceLaunchAdapter : IWorkspaceLaunchAdapter
{
    private readonly Func<ProcessStartInfo, Process?> start;
    private readonly TimeSpan observation;
    internal SystemWorkspaceLaunchAdapter(Func<ProcessStartInfo, Process?>? start = null, TimeSpan? observation = null)
    { this.start = start ?? Process.Start; this.observation = observation ?? TimeSpan.FromMilliseconds(500); }
    public bool FileExists(string path) => File.Exists(path);
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public bool IsExecutable(string path)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return File.Exists(path);
        return (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
    }
    public string? FindHelper(WorkspaceLaunchPlatform platform)
    {
        if (platform == WorkspaceLaunchPlatform.MacOS) return FileExists("/usr/bin/open") && IsExecutable("/usr/bin/open") ? "/usr/bin/open" : null;
        if (platform != WorkspaceLaunchPlatform.Linux) return null;
        foreach (string directory in new[] { "/usr/bin", "/bin" }.Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)).Where(Path.IsPathFullyQualified).Distinct(StringComparer.Ordinal))
        {
            string candidate = Path.Combine(directory, "xdg-open");
            try { if (FileExists(candidate) && IsExecutable(candidate)) return candidate; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
        return null;
    }
    public async Task<WorkspaceProcessOutcome> StartAsync(WorkspaceLaunchCommand command, CancellationToken cancellationToken)
    {
        using var process = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new ProcessStartInfo(command.Executable) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string argument in command.Arguments) info.ArgumentList.Add(argument);
            return start(info) ?? throw new InvalidOperationException("The OS did not return a launched process.");
        }, cancellationToken).ConfigureAwait(false);
        if (!command.ObserveHelper) return new(null);
        // Some xdg-open handlers remain alive with the target application. Never
        // wait for the user's app to exit and never kill it when observing ends.
        using var observationStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); observationStop.CancelAfter(observation);
        try { await process.WaitForExitAsync(observationStop.Token).ConfigureAwait(false); return new(process.ExitCode); }
        catch (OperationCanceledException) { return new(null, cancellationToken.IsCancellationRequested); }
    }
}
