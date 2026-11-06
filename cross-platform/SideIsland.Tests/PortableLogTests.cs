using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using CodexIsland;
using SideIsland.Core;

namespace SideIsland.Tests;

internal static class PortableLogTests
{
    private static int checks;
    public static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "side-island-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Paths(); EncodingAndBounds(directory); IdentityAndRecovery(directory);
            Controller(directory).GetAwaiter().GetResult(); Races(directory).GetAwaiter().GetResult();
            Console.WriteLine("PASS: " + checks + " portable log checks; real owned files, BOM decoding, bounds, identity/rotation/truncation, alerts and one-worker lifecycle. Foreign native execution is not implied.");
        }
        finally { Directory.Delete(directory, true); }
    }
    private static void Paths()
    {
        Check(LogWatchReader.PathInput("/tmp/log  ", false) == "/tmp/log  ", "Unix trailing spaces remain literal");
        Check(LogWatchReader.PathInput("/tmp/log\t", false) == "/tmp/log\t", "Unix trailing tab remains literal");
        Check(LogWatchReader.PathInput("\"/tmp/log  \"", false) == "/tmp/log  ", "one quoted Unix input preserves inner spaces");
        Check(LogWatchReader.PathInput("/tmp/a\"b\"", false) == "/tmp/a\"b\"", "literal quotes inside Unix filenames survive");
        Check(LogWatchReader.PathInput("  \"C:\\log.txt\"  ", true) == "C:\\log.txt", "Windows legacy pasted-path normalization is retained");
        using var controller = new PortableLogController();
        Reject(() => controller.Start("relative.log", "error"), "relative path rejected without disk access");
        Reject(() => controller.Start(Path.GetTempPath(), new string('x', 101)), "keyword character cap");
        Reject(() => controller.Start(Path.GetTempPath(), string.Join(',', Enumerable.Range(0, 21))), "keyword count cap");
    }
    private static LogWatchReader Reader(string path, Func<DateTime>? clock = null) => new(path, clock ?? (() => DateTime.UtcNow), stream => PortableBatchFiles.ReadOpenFileIdentity(stream.SafeFileHandle));
    private static void EncodingAndBounds(string directory)
    {
        string path = Path.Combine(directory, "encoding.log"); DateTime now = DateTime.UtcNow;
        foreach (Encoding encoding in new Encoding[] { new UTF8Encoding(false), new UTF8Encoding(true), new UnicodeEncoding(false, true), new UnicodeEncoding(true, true), new UTF32Encoding(false, true), new UTF32Encoding(true, true) })
        {
            File.WriteAllText(path, "old error\r\n配置😀\r末尾", encoding);
            using var reader = Reader(path, () => now); reader.SetKeywords("error;错误");
            var initial = reader.Read();
            Check(initial.Lines.SequenceEqual(new[] { "old error", "配置😀", "末尾" }) && initial.Alert is null, "historical BOM tail no alert: " + encoding.WebName);
            Append(path, "\nnew 错误\n", encoding); var live = reader.Read();
            Check(live.Alert == "new 错误" && live.Lines.Last() == "new 错误", "live bytes retain encoding: " + encoding.WebName);
            now = now.AddSeconds(11);
            File.WriteAllBytes(path, []);
            using var empty = Reader(path, () => now); empty.SetKeywords("error"); empty.Read();
            File.WriteAllText(path, "first error\n", encoding);
            Check(empty.Read().Alert == "first error", "empty file first append detects BOM and alerts: " + encoding.WebName);
        }
        foreach (Encoding encoding in new Encoding[] { new UTF8Encoding(true), new UnicodeEncoding(false, true), new UnicodeEncoding(true, true), new UTF32Encoding(false, true), new UTF32Encoding(true, true) })
        {
            File.WriteAllBytes(path, []);
            using var split = Reader(path, () => now); split.SetKeywords("error"); split.Read();
            byte[] bom = encoding.GetPreamble();
            for (int index = 0; index < bom.Length; index++)
            {
                AppendBytes(path, [bom[index]]); var pending = split.Read();
                Check(pending.Lines.Count == 0 && pending.Alert is null, "fragmented BOM stays invisible: " + encoding.WebName + "/" + index);
            }
            Append(path, "new error\n", encoding); var appended = split.Read();
            Check(appended.Lines.SequenceEqual(new[] { "new error" }) && appended.Alert == "new error", "fragmented BOM resolves before decoding live text: " + encoding.WebName);
        }
        File.WriteAllBytes(path, []);
        using (var reader = Reader(path, () => now))
        {
            reader.Read(); byte[] emoji = Encoding.UTF8.GetBytes("😀");
            AppendBytes(path, emoji[..2]); Check(reader.Read().Lines.Count == 0, "partial UTF8 codepoint remains in decoder");
            AppendBytes(path, emoji[2..]); Append(path, "\n"); Check(reader.Read().Lines.Single() == "😀", "split UTF8 codepoint roundtrips");
        }
        File.WriteAllText(path, "historical error", new UTF8Encoding(false));
        using (var reader = Reader(path, () => now))
        {
            reader.SetKeywords("error"); reader.Read(); now = now.AddSeconds(11);
            Check(reader.Read().Alert is null, "historical partial does not alert on idle");
            Append(path, " details\n"); Check(reader.Read().Alert is null, "historical partial newline does not replay");
            Append(path, "new error"); Check(reader.Read().Alert == "new error", "new partial alerts once");
            now = now.AddSeconds(11); Append(path, " details\n"); Check(reader.Read().Alert is null, "suffix/newline cannot repeat a matched logical line");
            Append(path, "next error\n"); Check(reader.Read().Alert == "next error", "later logical line still alerts");
        }
        File.WriteAllText(path, new string('x', 220000) + "\nlast\n", new UTF8Encoding(false));
        using (var reader = Reader(path))
        {
            var tail = reader.Read(); Check(tail.Lines.SequenceEqual(new[] { "last" }) && tail.Offset == new FileInfo(path).Length, "bounded initial tail drops partial leading line");
            for (int index = 0; index < 40; index++) { Append(path, string.Join('\n', Enumerable.Repeat(new string('a', 800), 30)) + "\n"); reader.Read(); }
            var bounded = reader.Read(); Check(bounded.Lines.Count <= LogWatchReader.MaximumLines && bounded.Lines.Sum(line => line.Length) <= LogWatchReader.MaximumBufferCharacters && bounded.Lines.All(line => line.Length <= LogWatchReader.MaximumLineLength), "tail line/character bounds hold over sustained append");
            reader.SetKeywords("error"); Append(path, new string('z', LogWatchReader.MaximumReadBytes * 9) + "\nold error\n");
            var backlog = reader.Read(); Check(backlog.State == LogWatchState.Skipped && backlog.Reset && backlog.Alert is null, "large backlog jumps to bounded tail without replaying alerts");
        }
    }
    private static void IdentityAndRecovery(string directory)
    {
        string path = Path.Combine(directory, "identity.log");
        using var reader = Reader(path);
        Check(reader.Read().Missing, "missing file waits");
        File.WriteAllText(path, "original error\n"); reader.SetKeywords("error"); var initial = reader.Read();
        File.Move(path, path + ".old"); File.WriteAllText(path, "replacement error is larger\n");
        var rotated = reader.Read(); Check(rotated.State == LogWatchState.Rotated && rotated.Lines.Single() == "replacement error is larger" && rotated.Alert is null, "real open-handle identity detects replacement without historical replay");
        File.WriteAllText(path, "tiny\n"); Check(reader.Read().State == LogWatchState.Truncated, "real truncation detected");
        File.WriteAllText(path, "regrown different file contents\n"); Check(reader.Read().State == LogWatchState.Truncated, "boundary detects truncate-and-regrow");
        reader.Clear(); Check(reader.Read().Lines.Count == 0, "clear keeps offset and cannot reread history");
        Append(path, "after clear\n"); Check(reader.Read().Lines.Single() == "after clear", "append continues after clear");
        string fixtureIdentity = "inode-one";
        using var injected = new LogWatchReader(path, () => DateTime.UtcNow, _ => fixtureIdentity);
        injected.Read(); fixtureIdentity = "inode-two"; Check(injected.Read().State == LogWatchState.Rotated, "injected stable-handle identity controls rotation boundary");
        using var denied = new LogWatchReader(path, () => DateTime.UtcNow, _ => throw new Win32Exception(5));
        Check(denied.Read().State == LogWatchState.Retrying && denied.Read().Error is not null, "identity error is retryable, not rotation or completion");
        if (!OperatingSystem.IsWindows())
        {
            string spaced = Path.Combine(directory, "literal space "); File.WriteAllText(spaced, "spaced\n");
            using var exact = Reader(spaced); Check(exact.Read().Lines.Single() == "spaced", "actual Unix file spelling with trailing space");
        }
        PortableBatchFiles.VerifyNativeAbiContract(); Check(true, "Linux/macOS stat ABI fixtures remain shared and validated");
    }
    private static async Task Controller(string directory)
    {
        string path = Path.Combine(directory, "controller.log"); File.WriteAllText(path, "historical error\n");
        using var controller = new PortableLogController(); controller.Start(path, "error");
        var first = await controller.PollAsync(); Check(first is not null && first.Alert is null && controller.IsCurrent(first), "first controller snapshot is current and historical alerts suppressed");
        var frozen = first!.Snapshot; Append(path, "live error\n"); var live = await controller.PollAsync();
        Check(live?.Alert == "live error" && frozen.Lines.Count == 1, "snapshot arrays are immutable across later reads");
        controller.Clear(); Check(!controller.IsCurrent(live!) && controller.Snapshot.Lines.Count == 0, "clear invalidates queued results immediately");
        Check((await controller.PollAsync())!.Snapshot.Lines.Count == 0, "clear does not replay historical content");
        Append(path, "failure\n"); controller.SetKeywords("failure"); await controller.PollAsync();
        controller.Stop(); Check(!controller.Snapshot.Running && controller.Snapshot.Lines.Single() == "failure", "stop retains last visible tail");
        await Idle(controller); controller.Start(path, "error"); var restarted = await controller.PollAsync();
        Check(restarted is not null && restarted.Alert is null && restarted.Snapshot.Lines.Count == 3, "restart establishes fresh historical baseline");
        controller.Dispose(); Check(!controller.IsCurrent(restarted!) && !controller.Snapshot.Running, "close invalidates queued callbacks");
        await Idle(controller); Check(await controller.PollAsync() is null, "closed controller cannot schedule a new reader");
        using var immediate = new PortableLogController(); immediate.Start(path, "error"); immediate.Clear();
        var clearedBeforeRead = await immediate.PollAsync();
        Check(clearedBeforeRead is not null && clearedBeforeRead.Snapshot.Lines.Count == 0 && clearedBeforeRead.Alert is null, "clear before first poll establishes offset without redisplaying history");
        Append(path, "after immediate clear\n");
        Check((await immediate.PollAsync())!.Snapshot.Lines.Single() == "after immediate clear", "new append survives a clear before first poll");
    }
    private static async Task Races(string directory)
    {
        string firstPath = Path.Combine(directory, "race-one.log"), nextPath = Path.Combine(directory, "race-two.log");
        File.WriteAllText(firstPath, "old error\n"); File.WriteAllText(nextPath, "new historical\n");
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        int created = 0, disposed = 0, simultaneous = 0, maximum = 0;
        using var controller = new PortableLogController(path =>
        {
            Interlocked.Increment(ref created);
            return new BlockingSource(path, () =>
            {
                int count = Interlocked.Increment(ref simultaneous); maximum = Math.Max(maximum, count);
                if (path == firstPath) { entered.Set(); if (!release.Wait(10000)) throw new TimeoutException("fixture release"); }
            }, () => Interlocked.Decrement(ref simultaneous), () => Interlocked.Increment(ref disposed));
        });
        controller.Start(firstPath, "error"); var blocked = controller.PollAsync();
        Check(entered.Wait(3000), "owned read reached controlled blocking point");
        try
        {
            var stopTimer = Stopwatch.StartNew(); controller.Stop();
            for (int index = 0; index < 80; index++) { controller.Start(nextPath, "error"); Check(await controller.PollAsync() is null, "restarts cannot overlap blocked OS read"); }
            controller.Clear(); controller.SetKeywords("failure");
            Check(stopTimer.Elapsed < TimeSpan.FromSeconds(2) && controller.InFlight == 1 && created == 1 && disposed == 0, "stop/restart/clear/keywords stay nonblocking with one retained slot");
        }
        finally { release.Set(); }
        Check(await blocked is null && disposed == 1, "stale stopped session suppresses result and releases its reader after return");
        var fresh = await controller.PollAsync();
        Check(fresh is not null && fresh.Snapshot.Path == nextPath && maximum == 1 && created == 2, "latest queued session gets the next sole worker");
        await ClearAndCloseRaces(directory);
        int attempts = 0;
        using var failure = new PortableLogController(path => { if (Interlocked.Increment(ref attempts) == 1) throw new IOException("owned fixture factory failure"); return new PortableLogSource(path); });
        failure.Start(nextPath, "error"); Check((await failure.PollAsync())?.Snapshot.State == PortableLogState.Retrying && failure.InFlight == 0, "factory failures release capacity and remain retryable");
        Check((await failure.PollAsync())?.Snapshot.State == PortableLogState.Watching, "factory failure recovers on later poll");
    }
    private static async Task ClearAndCloseRaces(string directory)
    {
        string path = Path.Combine(directory, "clear-race.log"); File.WriteAllText(path, "history\n");
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        int read = 0, disposed = 0;
        using var controller = new PortableLogController(value => new BlockingSource(value, () =>
        {
            if (Interlocked.Increment(ref read) == 2) { entered.Set(); if (!release.Wait(10000)) throw new TimeoutException("fixture release"); }
        }, () => { }, () => Interlocked.Increment(ref disposed)));
        controller.Start(path, "error"); await controller.PollAsync(); Append(path, "old error\n"); var blocked = controller.PollAsync();
        Check(entered.Wait(3000), "clear fixture blocked in real reader wrapper"); controller.Clear(); release.Set();
        Check(await blocked is null && controller.Snapshot.Lines.Count == 0, "read completion cannot restore content cleared while reading");
        var cleared = await controller.PollAsync(); Check(cleared!.Snapshot.Lines.Count == 0 && cleared.Alert is null, "next read clears retained reader without historic alert replay");
        Append(path, "after clear\n"); Check((await controller.PollAsync())!.Snapshot.Lines.Single() == "after clear", "new content after a racing clear survives");
        controller.SetKeywords("failure"); Check(!controller.IsCurrent(cleared), "keyword edits invalidate queued presentation");
        controller.Dispose(); await Idle(controller); Check(disposed == 1, "idle disposal frees exactly one source");

        using var closingEntered = new ManualResetEventSlim(); using var closingRelease = new ManualResetEventSlim();
        int closingDisposed = 0;
        var closing = new PortableLogController(value => new BlockingSource(value, () => { closingEntered.Set(); if (!closingRelease.Wait(10000)) throw new TimeoutException("fixture release"); }, () => { }, () => closingDisposed++));
        closing.Start(path, "error"); var closingTask = closing.PollAsync(); Check(closingEntered.Wait(3000), "closing fixture reached owned blocking read");
        var timer = Stopwatch.StartNew(); closing.Dispose();
        try { Check(timer.Elapsed < TimeSpan.FromSeconds(1) && closing.InFlight == 1 && closingDisposed == 0, "close does not block UI or dispose a source in use"); Check(await closing.PollAsync() is null, "closed blocked source cannot spawn another worker"); }
        finally { closingRelease.Set(); }
        Check(await closingTask is null && closingDisposed == 1 && closing.InFlight == 0, "late close completion is ignored and source disposed exactly once");
    }
    private sealed class BlockingSource(string path, Action before, Action after, Action disposing) : IPortableLogSource
    {
        private readonly PortableLogSource source = new(path);
        public LogWatchSnapshot Read() { before(); try { return source.Read(); } finally { after(); } }
        public void SetKeywords(string keywords) => source.SetKeywords(keywords);
        public void Clear() => source.Clear();
        public void Dispose() { source.Dispose(); disposing(); }
    }
    private static async Task Idle(PortableLogController controller)
    {
        for (int index = 0; index < 300 && controller.InFlight != 0; index++) await Task.Delay(10);
        Check(controller.InFlight == 0, "controller cleanup reaches idle");
    }
    private static void Append(string path, string value, Encoding? encoding = null) => AppendBytes(path, (encoding ?? new UTF8Encoding(false)).GetBytes(value));
    private static void AppendBytes(string path, byte[] bytes) { using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); stream.Write(bytes); }
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception("Portable log: " + message); }
    private static void Reject(Action action, string message) { try { action(); } catch (ArgumentException) { Check(true, message); return; } throw new Exception("Portable log accepted invalid input: " + message); }
}
