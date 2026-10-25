using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CodexIsland
{
    internal static class ClipboardHistoryTests
    {
        private sealed class FakeClipboard : IClipboardHistoryAdapter
        {
            public int Reads, Writes;
            public ClipboardHistoryPayload Next;
            public ClipboardHistoryPayload Read() { Reads++; return Next; }
            public void Write(ClipboardHistoryItem item) { Writes++; }
        }
        private sealed class WorkerClipboard : IClipboardHistoryAdapter
        {
            internal Func<ClipboardHistoryPayload> ReadPayload;
            public ClipboardHistoryPayload Read() { return ReadPayload(); }
            public void Write(ClipboardHistoryItem item) { throw new Exception("Worker tests must not write the clipboard"); }
        }
        public static void Run()
        {
            var book = new ClipboardHistoryBook();
            Check(!book.Add("private", null, "editor", false), "Clipboard history starts paused");
            book.Paused = false; book.SetExcludedApps("KeePass.exe, 1password;APP.exe");
            Check(book.IsExcluded("keepass") && book.IsExcluded("APP.EXE"), "Excluded process names normalize case and executable suffix");
            Check(!book.Add("secret", null, "KeePass", false) && !book.Add("secret", null, "editor", true), "Excluded and protected content never enters history");
            Check(!book.Add(new string('x', ClipboardHistoryBook.MaximumTextLength + 1), null, "", false), "Oversized text is rejected");
            Check(book.Add("配置😀", null, "editor.exe", false) && book.Add("配置😀", null, "editor", false) && book.Items.Count == 1, "Duplicate Unicode text updates the entry without duplicating");
            string id = book.Items[0].Id; book.ToggleFavorite(id);
            for (int i = 0; i < 75; i++) book.Add("text " + i, null, "editor", false);
            Check(book.Items.Count == ClipboardHistoryBook.MaximumEntries && book.Items.Any(x => x.Id == id && x.Favorite), "Capacity evicts old transient entries while retaining favorites");
            Check(book.Search("配置", true).Count() == 1 && book.Search("EDITOR", false).Count() == 50, "Search matches text and source case-insensitively");
            book.Clear(false); Check(book.Items.Count == 1, "Clear preserves explicit favorites");
            var image = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8); image.Freeze();
            byte[] png = ClipboardHistoryImage.Encode(image); var restoredImage = ClipboardHistoryImage.Decode(png);
            Check(restoredImage.IsFrozen && restoredImage.PixelWidth > 0 && book.Add(null, png, "imageapp", false), "Image history uses bounded frozen PNG data");
            book.ToggleFavorite(book.Items[0].Id);
            string folder = Path.Combine(Path.GetTempPath(), "island-clipboard-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            try
            {
                string location = Path.Combine(folder, "favorites.json"); var store = new ClipboardHistoryStore(location); store.Save(book.Export());
                var data = store.Load(); Check(!store.LoadFailed && data.Favorites.Count == 2 && data.Favorites.Any(x => x.Png != null), "Text and image favorites round-trip locally");
                var loaded = new ClipboardHistoryBook(); loaded.Restore(data); Check(loaded.Paused && loaded.Items.Count == 2 && loaded.IsExcluded("keepass.exe"), "Restoration keeps recording paused and restores exclusions");
                File.WriteAllText(location, "broken original"); Check(store.Load().Favorites.Count == 0 && store.LoadFailed, "Corrupt favorites produce an empty safe view");
                store.Save(new ClipboardHistoryData { Favorites = new System.Collections.Generic.List<ClipboardHistoryItem>(), ExcludedApps = new System.Collections.Generic.List<string>() });
                Check(Directory.GetFiles(folder, "*.bak").Length == 1 && File.ReadAllText(Directory.GetFiles(folder, "*.bak")[0]) == "broken original", "Corrupt favorites are backed up before a replacement is saved");
                PanelTests(folder);
            }
            finally { Directory.Delete(folder, true); }
            WorkerTests();
            Console.WriteLine("PASS: clipboard history privacy gates, limits, exclusions, favorites, image persistence, isolated adapters and STA worker lifecycle/coalescing/late-failure tests.");
        }
        private static void WorkerTests()
        {
            // These fixtures use ordinary tasks and fake adapters only. They neither
            // pump UI nor read/write the real clipboard, even on the suite's STA.
            var previous = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(null);
            try { WorkerApartmentAndCoalescing(); WorkerDisposeDoesNotBlock(); WorkerLateFailureAndRecovery(); }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }
        private static void WorkerApartmentAndCoalescing()
        {
            int caller = Thread.CurrentThread.ManagedThreadId, thread = caller, reads = 0; ApartmentState apartment = ApartmentState.Unknown;
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            using (var worker = new ClipboardHistoryWorker(new WorkerClipboard { ReadPayload = () => {
                thread = Thread.CurrentThread.ManagedThreadId; apartment = Thread.CurrentThread.GetApartmentState(); int index = Interlocked.Increment(ref reads);
                entered.Set(); release.Wait(3000); return new ClipboardHistoryPayload { Text = "worker payload " + index, Source = "fixture" };
            } }))
            {
                try
                {
                    var watch = Stopwatch.StartNew(); var first = worker.ReadAsync(); watch.Stop();
                    Check(watch.ElapsedMilliseconds < 300, "Queuing a clipboard read returns before a slow adapter");
                    Check(entered.Wait(2000), "Fake clipboard read begins on the worker");
                    var joined = Enumerable.Range(0, 8).Select(index => worker.ReadAsync()).ToArray();
                    Check(reads == 1 && thread != caller && apartment == ApartmentState.STA, "Concurrent reads share one native call on a separate STA");
                    release.Set(); Check(first.Wait(2000) && Task.WaitAll(joined, 2000), "All callers receive the shared read result");
                    Check(joined.All(task => Object.ReferenceEquals(task.Result, first.Result)), "Coalesced callers see the same payload");
                    var fresh = worker.ReadAsync(); Check(fresh.Wait(2000) && reads == 2 && fresh.Result.Text == "worker payload 2", "A read requested after completion obtains a fresh payload");
                    worker.Dispose(); Check(worker.Completion.Wait(2000), "Idle worker exits and releases its signal");
                }
                finally { release.Set(); worker.Dispose(); worker.Completion.Wait(4000); }
            }
        }
        private static void WorkerDisposeDoesNotBlock()
        {
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            using (var continuationEntered = new ManualResetEventSlim())
            using (var releaseContinuation = new ManualResetEventSlim())
            using (var worker = new ClipboardHistoryWorker(new WorkerClipboard { ReadPayload = () => {
                entered.Set(); release.Wait(3000); throw new InvalidOperationException("late disposed fake read");
            } }))
            {
                Task continuation = null;
                try
                {
                    var pendingRead = worker.ReadAsync(); Check(entered.Wait(2000), "Slow fake read is in flight before disposal");
                    continuation = pendingRead.ContinueWith(task => { continuationEntered.Set(); releaseContinuation.Wait(3000); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    var watch = Stopwatch.StartNew(); worker.Dispose(); worker.Dispose(); watch.Stop();
                    Check(watch.ElapsedMilliseconds < 300, "Disposal does not wait for a native read or a synchronous consumer continuation");
                    Check(pendingRead.Wait(2000) && pendingRead.Result == null, "Disposal completes an outstanding caller with no clipboard data");
                    Check(continuationEntered.Wait(2000) && !worker.Completion.IsCompleted, "Caller completion is independent of a blocked native adapter");
                    var stoppedRead = worker.ReadAsync(); Check(stoppedRead.IsCompleted && stoppedRead.Result == null, "Disposed workers reject new work immediately");
                    releaseContinuation.Set(); release.Set();
                    Check(worker.Completion.Wait(2000) && continuation.Wait(2000), "A late adapter failure finishes cleanly after disposal");
                    Parallel.For(0, 12, index => worker.Dispose());
                }
                finally { releaseContinuation.Set(); release.Set(); worker.Dispose(); worker.Completion.Wait(4000); if (continuation != null) continuation.Wait(4000); }
            }
        }
        private static void WorkerLateFailureAndRecovery()
        {
            int reads = 0;
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            using (var worker = new ClipboardHistoryWorker(new WorkerClipboard { ReadPayload = () => {
                int index = Interlocked.Increment(ref reads);
                if (index == 1) { entered.Set(); release.Wait(3000); throw new InvalidOperationException("fake read failed after timeout"); }
                if (index == 3) throw new InvalidOperationException("fake active read failed");
                return new ClipboardHistoryPayload { Text = "recovered " + index };
            } }, 500))
            {
                try
                {
                    var first = worker.ReadAsync(); Check(entered.Wait(2000), "Timed fake read starts");
                    var promiseSource = typeof(ClipboardHistoryWorker).GetField("pending", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(worker);
                    var sharedPromise = (Task)promiseSource.GetType().GetProperty("Task").GetValue(promiseSource, null);
                    Check(first.Wait(2000) && first.Result == null, "Slow clipboard adapters time out without blocking callers");
                    release.Set(); Check(sharedPromise.Wait(2000) && sharedPromise.Status == TaskStatus.RanToCompletion, "A driver failure after timeout cannot fault an abandoned shared promise");
                    var recovered = worker.ReadAsync(); Check(recovered.Wait(2000) && recovered.Result.Text == "recovered 2", "The worker clears a failed read and accepts fresh work");
                    var activeFailure = worker.ReadAsync(); Check(SpinWait.SpinUntil(() => activeFailure.IsCompleted, 2000), "An active adapter failure finishes");
                    bool propagated = false;
                    try { activeFailure.GetAwaiter().GetResult(); } catch (InvalidOperationException error) { propagated = error.Message == "fake active read failed"; }
                    Check(propagated, "An active caller receives the original adapter exception");
                    var finalRead = worker.ReadAsync(); Check(finalRead.Wait(2000) && finalRead.Result.Text == "recovered 4", "Observed adapter errors do not terminate the STA worker");
                }
                finally { release.Set(); worker.Dispose(); worker.Completion.Wait(4000); }
            }
        }
        private static void PanelTests(string directory)
        {
            var previous = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try
            {
                var adapter = new FakeClipboard { Next = new ClipboardHistoryPayload { Text = "synthetic clipboard", Source = "fixture" } };
                using (var panel = new ClipboardHistoryPanel(false, directory, adapter))
                {
                    panel.Start(); Pump(panel.CaptureNowAsync()); Check(adapter.Reads == 0, "Starting the panel does not read the real or injected clipboard");
                    panel.Book.Paused = false; Pump(panel.CaptureNowAsync()); Check(adapter.Reads == 1 && panel.Book.Items.Any(x => x.Text == "synthetic clipboard"), "Explicitly enabled synthetic capture records content");
                    adapter.Next = new ClipboardHistoryPayload { Text = "excluded", Source = "blocked", Protected = true }; Pump(panel.CaptureNowAsync()); Check(!panel.Book.Items.Any(x => x.Text == "excluded"), "Protected adapter payload is discarded");
                    panel.Stop(); Pump(panel.CaptureNowAsync()); Check(adapter.Reads == 2 && panel.Book.Paused, "Stopping disables further reads and pauses recording");
                    Check(adapter.Writes == 0, "No clipboard writes occur without an explicit copy click");
                }
                using (var panel = new ClipboardHistoryPanel(true, directory, adapter)) { panel.Start(); panel.Book.Paused = false; Pump(panel.CaptureNowAsync()); Check(adapter.Reads == 2, "Demo panel never reads clipboard adapters"); }
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }
        private static void Pump(Task task)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(8);
            while (!task.IsCompleted && DateTime.UtcNow < deadline) { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); Thread.Sleep(1); }
            if (task.IsFaulted) throw task.Exception.GetBaseException(); Check(task.IsCompleted, "Clipboard worker completes");
        }
        private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    }
}
