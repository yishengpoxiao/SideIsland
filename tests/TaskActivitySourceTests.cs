using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CodexIsland
{
    // Lifecycle/model tests only: no windows, native input, network, or user files.
    internal static class TaskActivitySourceTests
    {
        internal static void Run()
        {
            PublishTransitions(); TitleBoundaries(); WatchRemoval(); ReadyRemoval();
            Console.WriteLine("PASS: watch/readiness activity transitions, indeterminate progress, state deduplication, Unicode title boundaries, and atomic removal versus late completion (model tests; no UI or network).");
        }
        private static void Check(bool value, string label) { if (!value) throw new InvalidOperationException("Task activity source: " + label); }
        private static void PublishTransitions()
        {
            var publisher = new WatchActivityPublisher(); var updates = new List<TaskActivityUpdate>();
            Action<TaskActivityUpdate> sink = updates.Add;
            publisher.Publish("watch:one", "File", "running", "任务守望", "wait", null);
            publisher.Publish("watch:one", "File", "running", "任务守望", "wait", sink);
            publisher.Publish("watch:one", "File", "running", "任务守望", "checking again", sink);
            publisher.Publish("watch:one", "File", "completed", "任务守望", "done", sink);
            publisher.Publish("watch:one", "File", "completed", "任务守望", "done", sink);
            Check(updates.Count == 2 && updates[0].State == "running" && updates[1].State == "completed", "only lifecycle transitions publish, including after late subscription");
            Check(updates.All(x => x.Progress == null && x.Id == "watch:one" && x.Source == "任务守望"), "unknown progress never fabricates a percentage");
            publisher.Forget("watch:one"); publisher.Publish("watch:one", "File", "running", "任务守望", "wait", sink);
            Check(updates.Count == 3, "forgotten item does not suppress a subsequent lifecycle");
            bool failed = false;
            publisher.Publish("ready:two", "Server", "running", "服务就绪", "wait", update => { failed = true; throw new InvalidOperationException("synthetic sink failure"); });
            publisher.Publish("ready:two", "Server", "running", "服务就绪", "wait", sink);
            Check(failed && updates.Count == 4, "failed delivery can retry the same state");
            var hub = new TaskActivityHub(); DateTime time = new DateTime(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc);
            var stable = new WatchActivityPublisher(); Action<TaskActivityUpdate> publish = update => { hub.Publish(update, time); time = time.AddSeconds(1); };
            stable.Publish("watch:stable", "Output", "running", "任务守望", "wait", publish);
            DateTime firstUpdate = hub.Current.UpdatedUtc;
            for (int index = 0; index < 10; index++) stable.Publish("watch:stable", "Output", "running", "任务守望", "poll " + index, publish);
            Check(hub.Current.UpdatedUtc == firstUpdate && time == firstUpdate.AddSeconds(1), "one-second probes do not refresh task ordering or timestamps");
        }
        private static void TitleBoundaries()
        {
            string original = new string('a', 158) + "\ud83d\ude00.txt";
            string title = WatchActivityPublisher.Title(original);
            Check(title.Length <= 160 && title.EndsWith("…", StringComparison.Ordinal), "long title is capped with ellipsis");
            new UTF8Encoding(false, true).GetBytes(title);
            Check(original.EndsWith("\ud83d\ude00.txt", StringComparison.Ordinal), "sanitizing metadata does not change the source target");
            string exact = new string('b', 158) + "\ud83d\ude00";
            Check(WatchActivityPublisher.Title(exact) == exact, "exact 160-unit title preserves a valid final surrogate pair");
            string cleaned = WatchActivityPublisher.Title("\t\ud83dA\ude00\n\ud83d\ude00");
            Check(cleaned == "A\ud83d\ude00" && WatchActivityPublisher.Title("\r\n\ud83d") == "后台任务", "controls and unpaired surrogates are removed with a nonempty fallback");
            new UTF8Encoding(false, true).GetBytes(cleaned);
        }
        private static void WatchRemoval()
        {
            bool exists = false;
            var book = new TaskWatchBook(path => exists, pid => null);
            book.AddFile(@"C:\synthetic-task-activity\finished.txt"); string id = book.Items[0].Id;
            exists = true; Check(book.Poll().Count == 1, "file completion is observed");
            var finished = book.RemoveForActivity(id);
            Check(finished != null && finished.Completed && book.Items.Count == 0 && book.RemoveForActivity(id) == null, "completed deletion preserves completed lifecycle atomically");
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                bool polling = false;
                book = new TaskWatchBook(path => { if (!polling) return false; entered.Set(); if (!release.Wait(2000)) throw new TimeoutException("synthetic file probe release"); return true; }, pid => null);
                book.AddFile(@"C:\synthetic-task-activity\pending.txt"); id = book.Items[0].Id; polling = true;
                Task<List<TaskWatchNotice>> poll = Task.Run(() => book.Poll());
                try
                {
                    Check(entered.Wait(1500), "in-flight file probe entered");
                    var removed = book.RemoveForActivity(id); Check(removed != null && !removed.Completed, "running removal reports cancellation source state");
                }
                finally { release.Set(); }
                Check(poll.GetAwaiter().GetResult().Count == 0 && book.Items.Count == 0, "late file completion cannot resurrect or notify a removed watch");
            }
        }
        private sealed class Probe : IServiceReadyProbe
        {
            internal Func<Task<ServiceReadyObservation>> Read;
            public Task<ServiceReadyObservation> ProbeAsync(ServiceReadyRequest request, CancellationToken cancellation) { return Read(); }
            public void Dispose() { }
        }
        private static ServiceReadyRequest Request() { return new ServiceReadyRequest { Name = "Server", Kind = "tcp", Target = "localhost", Port = 3000 }; }
        private static void ReadyRemoval()
        {
            using (var monitor = new ServiceReadyMonitor(false, new Probe { Read = () => Task.FromResult(new ServiceReadyObservation { Ready = true }) }))
            {
                string id = monitor.Add(Request()); monitor.PollAsync().GetAwaiter().GetResult();
                var ready = monitor.RemoveForActivity(id);
                Check(ready != null && ready.State == "ready" && !ready.Running && monitor.Snapshot.Count == 0, "ready removal keeps completed state rather than cancellation");
            }
            var entered = new TaskCompletionSource<bool>(); var release = new TaskCompletionSource<ServiceReadyObservation>();
            using (var monitor = new ServiceReadyMonitor(false, new Probe { Read = () => { entered.TrySetResult(true); return release.Task; } }))
            {
                int notifications = 0; monitor.Ready += item => notifications++;
                string id = monitor.Add(Request()); Task poll = monitor.PollAsync();
                try
                {
                    Check(entered.Task.Wait(1500), "in-flight readiness probe entered");
                    var removed = monitor.RemoveForActivity(id);
                    Check(removed != null && removed.Running && removed.State == "checking", "atomic readiness removal retains pre-cancel state");
                }
                finally { release.TrySetResult(new ServiceReadyObservation { Ready = true }); }
                poll.GetAwaiter().GetResult();
                Check(notifications == 0 && monitor.Snapshot.Count == 0 && monitor.RemoveForActivity(id) == null, "late readiness completion cannot notify or reappear after removal");
            }
            using (var monitor = new ServiceReadyMonitor(true, new Probe { Read = () => { throw new InvalidOperationException("demo must not probe"); } }))
            {
                string id = monitor.Add(Request()); monitor.Cancel(id); var canceled = monitor.RemoveForActivity(id);
                Check(canceled != null && canceled.State == "canceled" && !canceled.Running, "previously canceled removal retains its terminal state");
            }
        }
    }
}
