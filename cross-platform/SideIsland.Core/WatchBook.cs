#nullable enable
using System.ComponentModel;
using System.Diagnostics;

namespace SideIsland.Core;

public sealed record WatchProcess(int Pid, string Name, long StartedUtcTicks);
public sealed record WatchEntry(string Id, string Target, int Pid, long StartedUtcTicks, bool Completed = false, string Error = "")
{
    public bool IsProcess => Pid > 0;
}
public sealed record WatchProbeResult(WatchEntry Entry, bool CompletedNow);
public sealed class WatchProblem(string code) : Exception(code) { public string Code { get; } = code; }

// Probes never hold the model gate. A removed entry cannot be resurrected by
// a late probe, and competing polls can publish completion only once.
public sealed class WatchBook
{
    public const int MaximumActive = 24, MaximumFinished = 24;
    private readonly object gate = new();
    private readonly List<WatchEntry> entries = [];
    private readonly Func<string, bool> fileExists, directoryExists;
    private readonly Func<int, WatchProcess?> processIdentity;
    private readonly StringComparer paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    public WatchBook() : this(File.Exists, Directory.Exists, ReadProcess) { }
    public WatchBook(Func<string, bool> fileProbe, Func<string, bool> directoryProbe, Func<int, WatchProcess?> processProbe)
    { fileExists = fileProbe; directoryExists = directoryProbe; processIdentity = processProbe; }
    public WatchEntry[] Snapshot { get { lock (gate) return entries.ToArray(); } }
    public WatchEntry AddFile(string value, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        string path = value ?? "";
        if (path.Length >= 2 && path[0] == '"' && path[^1] == '"') path = path[1..^1];
        if (!Path.IsPathFullyQualified(path)) throw new WatchProblem("path");
        path = Path.GetFullPath(path);
        if (directoryExists(path)) throw new WatchProblem("directory");
        if (fileExists(path)) throw new WatchProblem("exists");
        return Add(new("watch:" + Guid.NewGuid().ToString("N"), path, 0, 0), cancellation);
    }
    public WatchEntry AddProcess(int pid, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (pid <= 0) throw new WatchProblem("pid");
        var process = processIdentity(pid);
        if (process is null) throw new WatchProblem("exited");
        return Add(new("watch:" + Guid.NewGuid().ToString("N"), process.Name, pid, process.StartedUtcTicks), cancellation);
    }
    private WatchEntry Add(WatchEntry entry, CancellationToken cancellation)
    {
        lock (gate)
        {
            cancellation.ThrowIfCancellationRequested();
            if (entries.Any(item => !item.Completed && item.IsProcess == entry.IsProcess &&
                (entry.IsProcess ? item.Pid == entry.Pid && item.StartedUtcTicks == entry.StartedUtcTicks : paths.Equals(item.Target, entry.Target)))) throw new WatchProblem("duplicate");
            if (entries.Count(item => !item.Completed) >= MaximumActive) throw new WatchProblem("capacity");
            entries.Add(entry); return entry;
        }
    }
    public WatchEntry? Remove(string id)
    {
        lock (gate) { int index = entries.FindIndex(item => item.Id == id); if (index < 0) return null; var item = entries[index]; entries.RemoveAt(index); return item; }
    }
    public WatchEntry[] RemoveFinished()
    {
        lock (gate) { var removed = entries.Where(item => item.Completed).ToArray(); entries.RemoveAll(item => item.Completed); return removed; }
    }
    public int ClearFinished() => RemoveFinished().Length;
    public List<WatchEntry> Poll(CancellationToken cancellation = default)
    {
        var completed = new List<WatchEntry>();
        foreach (var candidate in Snapshot.Where(item => !item.Completed))
        {
            var result = Probe(candidate.Id, cancellation); if (result?.CompletedNow == true) completed.Add(result.Entry);
        }
        return completed;
    }
    public WatchProbeResult? Probe(string id, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var candidate = Snapshot.FirstOrDefault(item => item.Id == id && !item.Completed);
        if (candidate is null) return null;
        bool done = false; string error = "";
        try
        {
            if (!candidate.IsProcess) done = fileExists(candidate.Target);
            else { var current = processIdentity(candidate.Pid); done = current is null || current.StartedUtcTicks != candidate.StartedUtcTicks; }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or ArgumentException)
        { error = failure.Message.Length > 512 ? failure.Message[..512] : failure.Message; }
        lock (gate)
        {
            cancellation.ThrowIfCancellationRequested(); int index = entries.FindIndex(item => item.Id == id);
            if (index < 0 || entries[index].Completed) return null;
            var updated = candidate with { Completed = done, Error = error };
            if (done) { entries.RemoveAt(index); entries.Add(updated); } else entries[index] = updated;
            // Completed history has a separate bound and never consumes active
            // capacity. Oldest retained completion rows are discarded first.
            while (entries.Count(item => item.Completed) > MaximumFinished)
                entries.RemoveAt(entries.FindIndex(item => item.Completed));
            return new(updated, done);
        }
    }
    public static WatchProcess? ReadProcess(int pid)
    {
        Process process;
        // Only a missing PID is proof of exit. Errors reading process metadata
        // remain unavailable, even if the original exception is ArgumentException.
        try { process = Process.GetProcessById(pid); } catch (ArgumentException) { return null; }
        using (process)
        {
            if (process.HasExited) return null;
            return new(pid, process.ProcessName, process.StartTime.ToUniversalTime().Ticks);
        }
    }
}
