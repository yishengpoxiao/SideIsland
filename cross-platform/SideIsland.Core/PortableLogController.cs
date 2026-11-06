#nullable enable
using CodexIsland;

namespace SideIsland.Core;

public enum PortableLogState { Stopped, Watching, Missing, Rotated, Truncated, Skipped, Retrying }
public sealed record PortableLogSnapshot(string Path, IReadOnlyList<string> Lines, long Offset, PortableLogState State, string? Error, bool Running);
public sealed record PortableLogUpdate(long Generation, PortableLogSnapshot Snapshot, string? Alert);

internal interface IPortableLogSource : IDisposable
{
    LogWatchSnapshot Read();
    void SetKeywords(string keywords);
    void Clear();
}

internal sealed class PortableLogSource : IPortableLogSource
{
    private readonly LogWatchReader reader;
    internal PortableLogSource(string path) => reader = new(path, () => DateTime.UtcNow, stream => PortableBatchFiles.ReadOpenFileIdentity(stream.SafeFileHandle));
    public LogWatchSnapshot Read() => reader.Read();
    public void SetKeywords(string keywords) => reader.SetKeywords(keywords);
    public void Clear() => reader.Clear();
    public void Dispose() => reader.Dispose();
}

/// <summary>
/// One persistent controller per window. OS reads may be uncancellable: a stopped
/// or replaced session retains its single worker slot until the read returns.
/// State changes and disposal never wait for that worker or acquire a reader lock.
/// </summary>
public sealed class PortableLogController : IDisposable
{
    private sealed record Session(long Id, string Path, string Keywords, long ClearRevision);
    private readonly object gate = new();
    private readonly Func<string, IPortableLogSource> create;
    private Session? requested;
    private IPortableLogSource? source;
    private long sourceSession, appliedClear, nextSession, generation;
    private string? appliedKeywords;
    private bool busy, disposed;
    private PortableLogSnapshot snapshot = new("", Array.Empty<string>(), 0, PortableLogState.Stopped, null, false);

    public PortableLogController() : this(path => new PortableLogSource(path)) { }
    internal PortableLogController(Func<string, IPortableLogSource> create) => this.create = create ?? throw new ArgumentNullException(nameof(create));
    public PortableLogSnapshot Snapshot { get { lock (gate) return snapshot; } }
    public long Generation { get { lock (gate) return generation; } }
    public int InFlight { get { lock (gate) return busy ? 1 : 0; } }

    public void Start(string path, string keywords)
    {
        // Validation is lexical only; neither it nor the constructor probes disk.
        string normalized = LogWatchReader.NormalizePath(path);
        string terms = string.Join(",", LogWatchReader.ParseKeywords(keywords));
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            requested = new(++nextSession, normalized, terms, 0); generation++;
            snapshot = new(normalized, Array.Empty<string>(), 0, PortableLogState.Watching, null, true);
        }
    }

    public void Stop()
    {
        lock (gate)
        {
            if (disposed) return;
            requested = null; generation++;
            snapshot = snapshot with { State = PortableLogState.Stopped, Error = null, Running = false };
        }
        // If idle, release the retained reader on the same bounded worker. If a
        // read is active, its finally block owns cleanup after the OS returns.
        _ = PollAsync();
    }

    public void Clear()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            generation++;
            if (requested is not null) requested = requested with { ClearRevision = requested.ClearRevision + 1 };
            snapshot = snapshot with { Lines = Array.Empty<string>() };
        }
    }

    public void SetKeywords(string keywords)
    {
        string terms = string.Join(",", LogWatchReader.ParseKeywords(keywords));
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (requested is null || requested.Keywords == terms) return;
            requested = requested with { Keywords = terms }; generation++;
        }
    }

    public bool IsCurrent(PortableLogUpdate update)
    {
        lock (gate) return !disposed && requested is not null && generation == update.Generation;
    }

    public Task<PortableLogUpdate?> PollAsync()
    {
        lock (gate)
        {
            if (busy || (requested is null && source is null)) return Task.FromResult<PortableLogUpdate?>(null);
            busy = true;
            var session = requested; long revision = generation;
            return Task.Run(() => ReadOnce(session, revision));
        }
    }

    private PortableLogUpdate? ReadOnce(Session? session, long revision)
    {
        IPortableLogSource? local;
        long localSession, localClear;
        string? localKeywords;
        lock (gate) { local = source; localSession = sourceSession; localClear = appliedClear; localKeywords = appliedKeywords; }
        try
        {
            if (session is null || localSession != session.Id)
            {
                local?.Dispose(); local = null; localClear = 0; localKeywords = null;
            }
            if (session is null) return null;
            bool created = local is null;
            local ??= create(session.Path);
            localSession = session.Id;
            if (localKeywords != session.Keywords) { local.SetKeywords(session.Keywords); localKeywords = session.Keywords; }
            if (localClear != session.ClearRevision)
            {
                // Clear may arrive before the first poll: establish the initial
                // tail boundary before clearing, so the next read cannot bring
                // that historical text back. Both reads use the same worker.
                if (created) local.Read();
                local.Clear(); localClear = session.ClearRevision;
            }
            var result = local.Read();
            var state = (PortableLogState)result.State;
            var next = new PortableLogSnapshot(result.Path, Array.AsReadOnly(result.Lines.ToArray()), result.Offset, state, result.Error, true);
            lock (gate)
            {
                if (disposed || requested?.Id != session.Id || generation != revision) return null;
                snapshot = next;
                return new(revision, next, result.Alert);
            }
        }
        catch (Exception error)
        {
            lock (gate)
            {
                if (disposed || session is null || requested?.Id != session.Id || generation != revision) return null;
                snapshot = snapshot with { State = PortableLogState.Retrying, Error = error.Message };
                return new(revision, snapshot, null);
            }
        }
        finally
        {
            bool retain;
            lock (gate)
            {
                retain = !disposed && session is not null && requested?.Id == session.Id;
                if (retain)
                {
                    source = local; sourceSession = localSession; appliedClear = localClear; appliedKeywords = localKeywords;
                    busy = false;
                }
                else source = null;
            }
            if (!retain)
            {
                // Keep the slot occupied through disposal as well; a new Start
                // cannot schedule another source while old cleanup is blocked.
                try { local?.Dispose(); }
                finally { lock (gate) busy = false; }
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true; requested = null; generation++;
            snapshot = snapshot with { State = PortableLogState.Stopped, Error = null, Running = false };
        }
        _ = PollAsync();
    }
}
