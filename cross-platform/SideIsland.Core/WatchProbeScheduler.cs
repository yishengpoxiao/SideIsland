#nullable enable
namespace SideIsland.Core;

// A filesystem call may be synchronous and uncancellable. Its slot remains
// occupied until it returns, even if its watch was removed or lifetime ended.
// Completed targets never wait for unrelated slow probes before being reported.
public sealed class WatchProbeScheduler
{
    public const int DefaultConcurrency = 8;
    private readonly object gate = new();
    private readonly WatchBook book;
    private readonly int maximum;
    private readonly HashSet<string> running = [];
    private readonly Dictionary<string, long> lastStarted = [];
    private long sequence;
    public event Action<WatchProbeResult?>? Probed;
    public event Action<Exception>? Failed;
    public WatchProbeScheduler(WatchBook book, int maximumConcurrency = DefaultConcurrency)
    {
        if (maximumConcurrency < 1 || maximumConcurrency > WatchBook.MaximumActive) throw new ArgumentOutOfRangeException(nameof(maximumConcurrency));
        this.book = book; maximum = maximumConcurrency;
    }
    public int InFlight { get { lock (gate) return running.Count; } }
    public int WaitingForCapacity
    {
        get { var snapshot = book.Snapshot; lock (gate) return running.Count < maximum ? 0 : snapshot.Count(item => !item.Completed && !running.Contains(item.Id)); }
    }
    public Task PollAsync(CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested(); var snapshot = book.Snapshot.Where(item => !item.Completed).ToArray();
        var started = new List<Task>();
        lock (gate)
        {
            var active = snapshot.Select(item => item.Id).ToHashSet();
            foreach (string id in lastStarted.Keys.Where(id => !active.Contains(id) && !running.Contains(id)).ToArray()) lastStarted.Remove(id);
            // Least recently checked first: a small set of fast pending targets
            // cannot monopolize the available slots and starve later entries.
            foreach (var item in snapshot.Where(item => !running.Contains(item.Id)).OrderBy(item => lastStarted.GetValueOrDefault(item.Id)))
            {
                if (running.Count >= maximum) break;
                string id = item.Id; running.Add(id); lastStarted[id] = ++sequence;
                started.Add(Task.Run(() => Run(id, cancellation)));
            }
        }
        return Task.WhenAll(started);
    }
    private void Run(string id, CancellationToken cancellation)
    {
        WatchProbeResult? result = null; Exception? failure = null;
        try { result = book.Probe(id, cancellation); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) { failure = error; }
        finally { lock (gate) running.Remove(id); }
        if (cancellation.IsCancellationRequested) return;
        if (failure is not null) Failed?.Invoke(failure); else Probed?.Invoke(result);
    }
}
